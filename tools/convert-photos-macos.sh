#!/bin/sh
# Preserve the photo tree through VRPhoto's batch importer; inference is Apple SHARP.
set -eu

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
    cat <<'HELP'
Usage: sh tools/convert-photos-macos.sh INPUT LIBRARY [VRPhoto process options...]

INPUT is a photo or directory; LIBRARY receives ready-to-view scene folders.
The importer preserves subfolders and avoids duplicate basenames across folders.
Install Apple SHARP and the server CLI first, following README.md.
SHARP_BIN defaults to $HOME/Applications/ml-sharp/.venv/bin/sharp.
VRPHOTO_BIN can override the repository's server/.venv/bin/vrphoto executable.
Additional options go to vrphoto process (for example --force).
Prediction uses official sharp predict on MPS. Completed inputs are skipped.
HELP
    exit 0
fi
if [ "$#" -lt 2 ]; then
    echo 'Usage: sh tools/convert-photos-macos.sh INPUT LIBRARY [VRPhoto process options...]' >&2
    exit 2
fi
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
vrphoto_bin=${VRPHOTO_BIN:-"$script_dir/../server/.venv/bin/vrphoto"}
if ! command -v "$vrphoto_bin" >/dev/null 2>&1; then
    echo 'VRPhoto CLI was not found. Install server/ or set VRPHOTO_BIN; see README.md.' >&2
    exit 127
fi
input_path=$1
library_path=$2
shift 2
exec "$vrphoto_bin" process "$input_path" "$library_path" \
    --sharp-path "${SHARP_BIN:-$HOME/Applications/ml-sharp/.venv/bin/sharp}" "$@"
