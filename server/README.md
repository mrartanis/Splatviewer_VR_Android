# VRPhoto library server

An HTTPS photo-splat library for the native Pico 4 app and an optional Spark WebXR
viewer. The native app, server, and macOS conversion instructions are in the
[main README](../README.md).

## Install

Python 3.11 or newer is required. From the repository root:

```bash
python3 -m venv server/.venv
server/.venv/bin/python -m pip install -e ./server
server/.venv/bin/vrphoto --help
```

On Windows use `py -3` and the executables in `server\.venv\Scripts\`.
No Unity, Node.js, GPU, or SHARP installation is needed to serve an existing library.
Browser dependencies (Spark 2.2.0 and Three.js 0.180.0) are bundled for local use.

## Batch conversion with official Apple SHARP

`vrphoto process` uses the separately installed official
[Apple SHARP](https://github.com/apple-aiml-research/ml-sharp) CLI for inference on
Mac MPS. See the main README for installation. Our importer preserves the input
folder tree, stages unique filenames to avoid SHARP's flat-output name collisions,
and runs one SHARP process per pending batch. Unchanged scenes are skipped; use
`--force` to regenerate. The output library must be outside the input directory.

```bash
server/.venv/bin/vrphoto process /path/to/Photos /path/to/VRLibrary \
  --sharp-path "$HOME/Applications/ml-sharp/.venv/bin/sharp"
# Equivalent convenience launcher:
sh tools/convert-photos-macos.sh /path/to/Photos /path/to/VRLibrary
```

JPG/JPEG, PNG and WebP inputs are supported. Identical basenames in different
folders are safe; collisions within one destination folder are rejected before
inference. Completed scenes are retained if another input fails. The summary
reports discovered, pending, processed, skipped, failed and elapsed time.

## Import an existing PLY

```bash
server/.venv/bin/vrphoto add-ply /path/to/photo.ply /path/to/photo.jpg /path/to/VRLibrary
server/.venv/bin/vrphoto serve /path/to/VRLibrary --host 0.0.0.0 --port 876
```

`add-ply` copies the complete PLY, builds a JPEG preview up to 600 pixels with EXIF
orientation applied, and saves metadata including SHARP's coordinate convention
and camera fields when present. Sources are preserved. `--name NAME` overrides
the scene folder name; an existing scene with that name is replaced. Use a nested
library path for albums. Use JPEG/PNG source photos for preview compatibility.

Each scene is a directory containing nonempty `scene.ply` and `preview.jpg` files.
`metadata.json` is optional for display but identifies the original SHARP camera.
Existing scene directories may be copied directly into the library.

## HTTPS and connection

The server prints its LAN address and `Server certificate SHA-256` at startup.
The native app asks you to compare and approve this fingerprint, and remembers
it for that server address. It blocks changed certificates until approved again.
No CA installation is required for the native app.

On first run, the server creates a local CA and a leaf certificate under
`~/.local/share/vrphoto/`. If the LAN IP changes, it may reissue the leaf certificate.
For the **browser** viewer, trust only `local-ca.crt` in the browser/device trust
store before using WebXR; private `.key` files stay on the server. Use the printed
`https://` address. Plain HTTP on the TLS port is not a redirect.

Keep the server terminal open. Ctrl+C shuts it down. It listens on the configured
interfaces without account authentication; use it on a trusted LAN.

## Configuration

Use `vrphoto --config config.yaml serve /path/to/VRLibrary` with optional YAML:

```yaml
sharp_path: /path/to/ml-sharp/.venv/bin/sharp
preview_max_size: 600
preview_quality: 85
https_host: 0.0.0.0
https_port: 876
http_redirect_port: null
state_dir: /path/to/vrphoto-state
certificate_file: null
private_key_file: null
ca_certificate_file: null
default_splat_budget: full
viewer_renderer_preference: auto
```

CLI overrides include `--host`, `--port`, `--state-dir`, `--http-redirect-port`,
`--certificate-file`, `--private-key-file`, `--ca-certificate-file`, `--budget`,
`--renderer`, and `--debug`. A custom certificate and private key must be supplied
together. The HTTP redirect, if enabled, uses a separate port. Run `serve --help`
for all options. The default port without overrides is 8443.

## Native API

`GET /api/v1/library?path=<relative-folder>&page=<number>` returns folders, scenes,
pagination data, preview/PLY URLs, and scene coordinate systems. Full is the
default budget; smaller explicit budgets produce a deterministic reduced PLY in
the server cache. Full requests preserve the original PLY. Media supports HTTP
Range. The native app downloads PLYs into its own cache.

## Built-in JavaScript viewer

The server also includes a standalone **Spark + Three.js JavaScript viewer**.
Open the HTTPS root in a browser to browse the library, select a photo, and use
**Enter VR** in Pico Browser. No native APK or Node.js is needed for this path.
The browser viewer uses WebGL2 and starts rendering inside a WebXR session; before
that it shows the preview. Head tracking is aligned with the original SHARP photo
camera. Grip translates the scene; grip plus stick forward/back changes distance.
The trigger opens a controller-selectable return-to-library panel.

With `--debug`, `/debug/capabilities`, `/debug/plain`, `/debug/catalog-lite`, and
`/debug/xr-raw` help isolate browser/XR problems. XR events are stored in
`xr-events.jsonl` under the state directory and available through `/debug/xr-events`.
The older PlayCanvas viewer is available through `?engine=playcanvas` in debug mode.
`--renderer` selects its renderer; the main Spark viewer uses WebGL2.

## Tests

```bash
server/.venv/bin/python -m pip install pytest
server/.venv/bin/python -m pytest -q server/tests
```

Tests cover HTTPS, catalog API, nested/Unicode paths, pagination, traversal,
certificate fingerprints, previews, large downloads, Range requests, and PLY
budgets, batch folder mapping and resume. The POSIX SIGINT test is skipped on Windows. On Windows the server uses
streamed file responses instead of `asyncio.sendfile` to handle interrupted downloads.
