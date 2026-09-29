from __future__ import annotations

import hashlib
from pathlib import Path
import re
import shutil
import struct
import tempfile

TYPE_SIZES = {"char": 1, "uchar": 1, "int8": 1, "uint8": 1, "short": 2, "ushort": 2,
              "int16": 2, "uint16": 2, "int": 4, "uint": 4, "int32": 4, "uint32": 4,
              "float": 4, "float32": 4, "double": 8, "float64": 8}


def read_sharp_camera(source: Path) -> dict | None:
    """Read Apple's optional camera trailer without loading the vertex array."""
    with source.open("rb") as inp:
        lines = []
        while True:
            line = inp.readline(4096)
            if not line or sum(map(len, lines)) + len(line) > 65536:
                return None
            lines.append(line)
            if line == b"end_header\n":
                break
        header_size = sum(map(len, lines))
        try:
            text = b"".join(lines).decode("ascii")
            if "format binary_little_endian 1.0" not in text:
                return None
            sections = {}
            current = None
            for line in text.splitlines():
                parts = line.split()
                if len(parts) == 3 and parts[0] == "element":
                    current = parts[1]
                    sections[current] = [int(parts[2]), []]
                elif len(parts) == 3 and parts[0] == "property" and current:
                    sections[current][1].append(parts[1])
            if list(sections)[:3] != ["vertex", "extrinsic", "intrinsic"]:
                return None
            vertex_count, vertex_types = sections["vertex"]
            vertex_size = sum(TYPE_SIZES[kind] for kind in vertex_types)
            extrinsic_count, extrinsic_types = sections["extrinsic"]
            intrinsic_count, intrinsic_types = sections["intrinsic"]
            if (extrinsic_count, extrinsic_types, intrinsic_count, intrinsic_types) != (16, ["float"], 9, ["float"]):
                return None
            inp.seek(header_size + vertex_count * vertex_size)
            extrinsic = struct.unpack("<16f", inp.read(64))
            intrinsic = struct.unpack("<9f", inp.read(36))
            return {"extrinsic_row_major": list(extrinsic), "intrinsic_row_major": list(intrinsic)}
        except (KeyError, ValueError, struct.error, OverflowError):
            return None


def reduced_ply(source: Path, fraction: float, cache_dir: Path) -> Path:
    if not 0 < fraction <= 1:
        raise ValueError("budget must be in (0, 1]")
    if fraction == 1:
        return source
    stat = source.stat()
    identity = f"{source.resolve()}:{stat.st_size}:{stat.st_mtime_ns}:{fraction:.6f}".encode()
    cache_dir.mkdir(parents=True, exist_ok=True)
    target = cache_dir / (hashlib.sha256(identity).hexdigest() + ".ply")
    if target.is_file():
        return target
    with source.open("rb") as inp:
        header = bytearray()
        while not header.endswith(b"end_header\n"):
            line = inp.readline(4096)
            if not line or len(header) + len(line) > 65536:
                raise ValueError("Invalid or oversized PLY header")
            header.extend(line)
        text = header.decode("ascii")
        if not text.startswith("ply\nformat binary_little_endian 1.0\n"):
            raise ValueError("Budget reduction requires binary little-endian PLY")
        match = re.search(r"(?m)^element vertex (\d+)\r?$", text)
        if not match:
            raise ValueError("No PLY vertices")
        count = int(match.group(1))
        if not count:
            raise ValueError("Empty PLY")
        preceding = text[:match.start()]
        if re.search(r"(?m)^element ", preceding):
            raise ValueError("Vertex element must be first")
        vertex_lines = text[match.end():].split("element ", 1)[0]
        record_size = 0
        for line in vertex_lines.splitlines():
            if line.startswith("property list "):
                raise ValueError("List properties are unsupported")
            if line.startswith("property "):
                parts = line.split()
                if len(parts) != 3 or parts[1] not in TYPE_SIZES:
                    raise ValueError("Unsupported vertex property")
                record_size += TYPE_SIZES[parts[1]]
        if record_size == 0 or stat.st_size < len(header) + count * record_size:
            raise ValueError("Truncated PLY")
        new_count = max(1, round(count * fraction))
        updated = (text[:match.start(1)] + str(new_count) + text[match.end(1):]).encode("ascii")
        with tempfile.NamedTemporaryFile(dir=cache_dir, suffix=".tmp", delete=False) as tmp:
            temp_path = Path(tmp.name)
            try:
                tmp.write(updated)
                for i in range(new_count):
                    index = i * count // new_count
                    inp.seek(len(header) + index * record_size)
                    chunk = inp.read(record_size)
                    if len(chunk) != record_size:
                        raise ValueError("Truncated PLY vertex")
                    tmp.write(chunk)
                inp.seek(len(header) + count * record_size)
                shutil.copyfileobj(inp, tmp, length=1024 * 1024)
            except Exception:
                temp_path.unlink(missing_ok=True)
                raise
    temp_path.replace(target)
    return target
