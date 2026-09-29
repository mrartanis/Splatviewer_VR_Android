from __future__ import annotations

from pathlib import Path


def within(root: Path, relative: str) -> Path:
    root = root.resolve()
    candidate = (root / relative).resolve()
    if not candidate.is_relative_to(root):
        raise ValueError("Path escapes library")
    return candidate


def scene_valid(path: Path) -> bool:
    if not path.is_dir() or path.is_symlink():
        return False
    try:
        for name in ("scene.ply", "preview.jpg"):
            file = path / name
            if not file.is_file() or file.is_symlink() or file.stat().st_size == 0:
                return False
        return True
    except OSError:
        return False


def contents(root: Path, folder: Path) -> tuple[list[Path], list[Path]]:
    directories, scenes = [], []
    for child in folder.iterdir():
        if child.is_symlink() or not child.is_dir():
            continue
        if scene_valid(child):
            scenes.append(child)
        else:
            directories.append(child)
    key = lambda p: p.name.casefold()
    return sorted(directories, key=key), sorted(scenes, key=key)
