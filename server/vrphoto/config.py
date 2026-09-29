from __future__ import annotations

from pathlib import Path
import yaml

DEFAULTS = {
    "sharp_path": str(Path.home() / "Applications/ml-sharp"),
    "preview_max_size": 600,
    "preview_quality": 85,
    "https_host": "0.0.0.0",
    "https_port": 8443,
    "http_redirect_port": None,
    "state_dir": str(Path.home() / ".local/share/vrphoto"),
    "certificate_file": None,
    "private_key_file": None,
    "ca_certificate_file": None,
    "default_splat_budget": "full",
    "viewer_renderer_preference": "auto",
}


def read_config(path: str | None) -> dict:
    settings = DEFAULTS.copy()
    if path:
        data = yaml.safe_load(Path(path).read_text(encoding="utf-8")) or {}
        if not isinstance(data, dict):
            raise ValueError("Config must be a YAML mapping")
        unknown = set(data) - set(DEFAULTS)
        if unknown:
            raise ValueError(f"Unknown config keys: {', '.join(sorted(unknown))}")
        settings.update(data)
    return settings
