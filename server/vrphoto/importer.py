from __future__ import annotations

from datetime import datetime, timezone
import json
import logging
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

from PIL import Image, ImageOps

from .library import scene_valid
from .ply import read_sharp_camera

LOG = logging.getLogger(__name__)
EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp"}


def find_sharp(value: str) -> Path:
    path = Path(value).expanduser()
    if path.is_file():
        return path
    candidates = [path / "bin/sharp", path / "venv/bin/sharp", path / ".venv/bin/sharp",
                  path / "env/bin/sharp", path / "Scripts/sharp.exe", path / "venv/Scripts/sharp.exe"]
    found = [candidate for candidate in candidates if candidate.is_file()]
    if len(found) != 1:
        raise FileNotFoundError(f"Expected one SHARP CLI in {path}; found {len(found)}. Pass --sharp-path with the exact executable.")
    return found[0]


def check_sharp(command: Path) -> None:
    for args in (["--help"], ["predict", "--help"]):
        result = subprocess.run([str(command), *args], text=True, capture_output=True, timeout=30)
        if result.returncode or (args[0] == "predict" and ("--input-path" not in result.stdout or "--output-path" not in result.stdout)):
            raise RuntimeError(f"SHARP CLI validation failed: {command} {' '.join(args)}\n{result.stderr or result.stdout}")


def _fingerprint(path: Path) -> dict:
    stat = path.stat()
    return {"size": stat.st_size, "mtime_ns": stat.st_mtime_ns}


def _destination(source: Path, input_path: Path, output: Path) -> Path:
    relative = source.relative_to(input_path) if input_path.is_dir() else Path(source.name)
    return output / relative.parent / relative.stem


def _finalize(source: Path, relative: Path, ply: Path, dest: Path, max_size: int, quality: int) -> None:
    if ply.stat().st_size <= 0:
        raise ValueError("SHARP produced an empty PLY")
    dest.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=f".{dest.name}-", dir=dest.parent))
    backup = None
    try:
        shutil.copyfile(ply, staging / "scene.ply")
        with Image.open(source) as opened:
            image = ImageOps.exif_transpose(opened).convert("RGB")
            width, height = image.size
            image.thumbnail((max_size, max_size), Image.Resampling.LANCZOS)
            image.save(staging / "preview.jpg", "JPEG", quality=quality, optimize=True)
        metadata = {
            "schema_version": 1,
            "source_filename": source.name,
            "source_relative_path": relative.as_posix(),
            "source_width": width,
            "source_height": height,
            "source_fingerprint": _fingerprint(source),
            "created_at": datetime.now(timezone.utc).isoformat(),
            "generator": "apple-sharp",
            "ply": "scene.ply",
            "preview": "preview.jpg",
            "coordinate_system": "opencv-x-right-y-down-z-forward",
        }
        camera = read_sharp_camera(staging / "scene.ply")
        if camera is not None:
            metadata["sharp_camera"] = camera
        (staging / "metadata.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
        if not scene_valid(staging):
            raise ValueError("Staged scene is invalid")
        if dest.exists():
            backup = dest.with_name(f".{dest.name}-old-{os.getpid()}")
            if backup.exists():
                raise FileExistsError(backup)
            dest.rename(backup)
        try:
            staging.rename(dest)
        except Exception:
            if backup is not None:
                backup.rename(dest)
            raise
        if backup is not None:
            shutil.rmtree(backup)
        LOG.info("scene finalized: %s", dest)
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def process(input_name: str, output_name: str, sharp_path: str, max_size: int = 600,
            quality: int = 85, force: bool = False) -> dict:
    started = time.monotonic()
    source_root, output = Path(input_name).expanduser().resolve(), Path(output_name).expanduser().resolve()
    if not source_root.exists():
        raise FileNotFoundError(source_root)
    if source_root.is_dir() and (output == source_root or output.is_relative_to(source_root)):
        raise ValueError("Output library must be outside the input tree")
    if not (32 <= max_size <= 8192 and 1 <= quality <= 100):
        raise ValueError("Invalid preview settings")
    sources = ([source_root] if source_root.is_file() else
               sorted(p for p in source_root.rglob("*") if p.is_file() and not p.is_symlink() and p.suffix.lower() in EXTENSIONS))
    if source_root.is_file() and source_root.suffix.lower() not in EXTENSIONS:
        raise ValueError("Unsupported image extension")
    locations = {}
    for source in sources:
        dest = _destination(source, source_root, output)
        key = str(dest).casefold()
        if key in locations:
            raise ValueError(f"Scene name collision: {locations[key]} and {source}")
        locations[key] = source
    pending = []
    skipped = 0
    for source in sources:
        dest = _destination(source, source_root, output)
        relative = source.relative_to(source_root) if source_root.is_dir() else Path(source.name)
        if not force and scene_valid(dest):
            try:
                metadata = json.loads((dest / "metadata.json").read_text(encoding="utf-8"))
                if (metadata.get("source_relative_path") == relative.as_posix() and
                        metadata.get("source_fingerprint") == _fingerprint(source)):
                    skipped += 1
                    LOG.info("skipped: %s", source)
                    continue
            except (OSError, ValueError):
                pass
        pending.append((source, relative, dest))
    LOG.info("discovered=%d pending=%d skipped=%d", len(sources), len(pending), skipped)
    processed = failed = 0
    if pending:
        sharp = find_sharp(sharp_path)
        check_sharp(sharp)
        output.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="vrphoto-") as workspace:
            tmp = Path(workspace)
            sharp_input, sharp_output = tmp / "input", tmp / "output"
            sharp_input.mkdir()
            sharp_output.mkdir()
            mapping = {}
            for index, (source, relative, dest) in enumerate(pending):
                name = f"image_{index:08d}{source.suffix.lower()}"
                shutil.copyfile(source, sharp_input / name)
                mapping[Path(name).stem] = (source, relative, dest)
            LOG.info("submitted %d photos to SHARP in one process", len(mapping))
            result = subprocess.run([str(sharp), "predict", "-i", str(sharp_input), "-o", str(sharp_output), "--device", "mps"],
                                    text=True, capture_output=True)
            if result.returncode:
                LOG.error("SHARP exited %d: %s", result.returncode, result.stderr[-3000:])
            else:
                LOG.info("SHARP completed")
            produced = list(sharp_output.rglob("*.ply"))
            by_stem = {}
            for ply in produced:
                by_stem.setdefault(ply.stem, []).append(ply)
            for stem, (source, relative, dest) in mapping.items():
                files = by_stem.get(stem, [])
                if len(files) != 1:
                    failed += 1
                    LOG.error("No unique PLY for %s (found %d)", source, len(files))
                    continue
                try:
                    _finalize(source, relative, files[0], dest, max_size, quality)
                    processed += 1
                except Exception:
                    failed += 1
                    LOG.exception("Failed to finalize %s", source)
            unexpected = set(by_stem) - set(mapping)
            if unexpected:
                LOG.warning("Unexpected PLY names: %s", sorted(unexpected))
    summary = {"discovered": len(sources), "pending": len(pending), "processed": processed,
               "skipped": skipped, "failed": failed, "total_seconds": round(time.monotonic() - started, 2)}
    LOG.info("summary %s", summary)
    return summary


def add_ply(ply_name: str, photo_name: str, library_name: str, name: str | None = None) -> Path:
    ply, photo, library = Path(ply_name).resolve(), Path(photo_name).resolve(), Path(library_name).resolve()
    if not ply.is_file() or not photo.is_file():
        raise FileNotFoundError("PLY and source photo are required")
    dest = library / (name or photo.stem)
    _finalize(photo, Path(photo.name), ply, dest, 600, 85)
    return dest
