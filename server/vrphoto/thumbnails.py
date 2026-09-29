from __future__ import annotations

import hashlib
from pathlib import Path
import tempfile

from PIL import Image


def thumbnail(source: Path, cache_dir: Path, size: int = 320) -> Path:
    stat = source.stat()
    key = f"{source.resolve()}:{stat.st_size}:{stat.st_mtime_ns}:{size}".encode()
    cache_dir.mkdir(parents=True, exist_ok=True)
    target = cache_dir / (hashlib.sha256(key).hexdigest() + ".jpg")
    if target.is_file():
        return target
    with tempfile.NamedTemporaryFile(dir=cache_dir, suffix=".jpg", delete=False) as tmp:
        temporary = Path(tmp.name)
    try:
        with Image.open(source) as opened:
            image = opened.convert("RGB")
            image.thumbnail((size, size), Image.Resampling.LANCZOS)
            image.save(temporary, "JPEG", quality=82, optimize=True)
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)
    return target
