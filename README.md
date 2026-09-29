# VRPhoto for Pico 4

View photographs as 3D Gaussian splats in a native Pico 4 app. Convert a photo with
[Apple SHARP](https://github.com/apple-aiml-research/ml-sharp), add its PLY to the
included Python library server, then open it from the headset.

The Unity app uses a native port of [Spark](https://github.com/sparkjsdev/spark)
for SHARP scenes. Photos stay fixed in space as you move your head. Full quality
(100% of splats) and 100% XR render resolution are the defaults. Local-file viewing
and the server's browser viewer are also available.

## Install on Pico 4

1. Download **VRPhoto-Pico4.apk** from the
   [Pico release](https://github.com/mrartanis/Splatviewer_VR_Android/releases/tag/pico-v0.1.0).
2. Install it using your APK sideloading tool, or ADB as below.
3. Open **VRPhoto** from the headset's app library.

For ADB installation from a Mac with [Homebrew](https://brew.sh):

```bash
brew install --cask android-platform-tools
adb devices
adb install -r "$HOME/Downloads/VRPhoto-Pico4.apk"
```

Enable developer mode / USB debugging on the Pico, connect it with a data-capable
USB cable, and accept the debugging prompt inside the headset. `adb devices` must
show `device`, not `unauthorized`. Windows/Linux can use Google's
[SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools).

The APK is ARM64, package `com.mrartanis.vrphoto.pico`, version `0.1`. This release
uses Unity's debug signing certificate. `adb install -r` preserves settings when
updating an installation signed with the same certificate.

## Convert photos on a Mac

### 1. Install official Apple SHARP

Use an Apple Silicon Mac for GPU inference through MPS. Keep SHARP in its own
Python environment; the library server does not need PyTorch or model weights.
These commands use Python 3.13, as recommended in Apple's
[installation instructions](https://github.com/apple-aiml-research/ml-sharp#getting-started).

With Homebrew already installed:

```bash
brew install python@3.13 git
mkdir -p "$HOME/Applications"
git clone https://github.com/apple-aiml-research/ml-sharp.git "$HOME/Applications/ml-sharp"
cd "$HOME/Applications/ml-sharp"
python3.13 -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
sharp --help
python -c 'import torch; print("MPS available:", torch.backends.mps.is_available())'
```

MPS should report `True`. CPU inference is also supported by SHARP, but is slower.
See Apple's repository for its software and model licenses and supported platforms.

### 2. Install VRPhoto's converter and library server

Open a new terminal. The server requires Python 3.11+ and works on macOS, Windows,
and Linux. These commands use the Python 3.13 installed above:

```bash
git clone https://github.com/mrartanis/Splatviewer_VR_Android.git
cd Splatviewer_VR_Android
python3.13 -m venv server/.venv
server/.venv/bin/python -m pip install --upgrade pip
server/.venv/bin/python -m pip install -e ./server
server/.venv/bin/vrphoto --help
```

On Windows, create the environment with `py -3 -m venv server\.venv` and use
`server\.venv\Scripts\python.exe` / `server\.venv\Scripts\vrphoto.exe` instead.
Serving an existing library does not require SHARP or a GPU.

### 3. Convert a photo tree, preserving its folders

From the repository root:

```bash
sh tools/convert-photos-macos.sh \
  "$HOME/Pictures/VRPhotos" "$HOME/Pictures/VRLibrary"
```

The script calls our `vrphoto process` batch importer. **Inference uses official
Apple SHARP**, installed separately in step 1. The importer stages photos with
unique temporary names, calls `sharp predict --device mps` once for the batch,
then maps the PLY files back into the original folder structure. Identical photo
basenames in different folders are safe. The original photo files are not renamed.

For example, `VRPhotos/Holidays/beach.jpg` becomes:

```text
VRLibrary/
  Holidays/
    beach/
      scene.ply
      preview.jpg
      metadata.json
```

Input can be a single file or a folder. JPG/JPEG, PNG, and WebP are supported;
export images from Apple Photos as files first. The library must be outside the
input tree. The PLY is copied unchanged, and each scene gets an aspect-correct
preview with EXIF orientation plus SHARP camera/coordinate metadata.

Unchanged completed photos are skipped using source size and modification time.
Repeat the command to resume, or add `--force` to regenerate scenes. Successful
scenes are retained if others fail. Same-stem files in the **same** folder, such
as `photo.jpg` and `photo.png`, are rejected before inference to avoid overwriting.

If SHARP is installed elsewhere:

```bash
SHARP_BIN="/path/to/ml-sharp/.venv/bin/sharp" \
  sh tools/convert-photos-macos.sh /path/to/Photos /path/to/VRLibrary
```

The default is `$HOME/Applications/ml-sharp/.venv/bin/sharp`. `VRPHOTO_BIN` can
override the server CLI location. Extra options go to `vrphoto process`; use
`--preview-max-size 768` for larger previews or `--force` to rerun inference.
The equivalent command without the shell launcher is:

```bash
server/.venv/bin/vrphoto process /path/to/Photos /path/to/VRLibrary \
  --sharp-path "$HOME/Applications/ml-sharp/.venv/bin/sharp"
```

Apple SHARP downloads its checkpoint on first use and caches it in
`~/.cache/torch/hub/checkpoints/`. This Mac batch workflow requires MPS and does
not invoke Apple's CUDA-only video-rendering option.

### Already have a PLY, or need CPU inference?

You can use Apple's CLI directly in its environment, then import the result:

```bash
"$HOME/Applications/ml-sharp/.venv/bin/sharp" predict \
  -i /path/to/photo.jpg -o /path/to/SharpOutput --device cpu
server/.venv/bin/vrphoto add-ply \
  /path/to/SharpOutput/photo.ply /path/to/photo.jpg /path/to/VRLibrary
```

`add-ply --name another-name` chooses a scene folder name. Reimporting that name
replaces the library scene while retaining source files. You can also copy scene
folders containing `scene.ply` and `preview.jpg` directly into the library.

## Connect the headset

Start the server and keep the terminal open:

```bash
server/.venv/bin/vrphoto serve "$HOME/Pictures/VRLibrary" --host 0.0.0.0 --port 876
```

Put the computer and Pico on the same network and allow incoming connections to
the server through the computer's firewall. In VRPhoto, enter the **HTTPS LAN
address printed by the server**, for example `https://192.168.1.20:876`.
`0.0.0.0` is a bind address, not the address to enter on the headset.

At first connection, compare the certificate SHA-256 fingerprint shown in the
headset with `Server certificate SHA-256` in the server log and approve it. The
app remembers that certificate for the address. A changed certificate requires
explicit approval again. The native app does not require installing a CA on Pico.

The server is intended for a trusted LAN and has no account authentication. Use
HTTPS, not HTTP. Stop it with Ctrl+C.

## Built-in JavaScript / WebXR viewer

**The server has its own browser viewer**, built with Spark and Three.js. Open
the same HTTPS server address in a desktop browser to browse the thumbnail library,
or in Pico Browser and press **Enter VR** on a photo to view it with WebXR. The
native APK is not required for this path. JavaScript dependencies are bundled
with the server, so no Node.js or external CDN is required at runtime.

Browser WebXR requires trusted HTTPS: install/trust only the server's
`~/.local/share/vrphoto/local-ca.crt` in the browser/device trust store. Keep private
`.key` files on the server. This browser CA setup is separate from the native
app's fingerprint approval. See [server/README.md](server/README.md) for browser
controls, configuration, and diagnostics.

## Headset controls

| Context | Control | Action |
| --- | --- | --- |
| Catalog | Stick | Move between thumbnail cards / load more entries |
| Catalog | Either trigger | Open the selected folder or photo |
| Catalog | B | Go back or cancel loading |
| Photo | B or left Y | Return to the catalog at the previous position |
| Photo | A | Load the next photo without returning to the catalog |
| Photo | Stick forward/back | Move the photo closer/farther |
| Photo | Hold grip and move the controller | Translate the scene, including up/down |
| Photo | Hold right grip + press left Y | Open/close hidden settings |
| Settings | Stick up/down, left/right | Select a setting, change its value |

The address screen includes an on-screen keyboard. **Local files** opens the
inherited browser, whose controls differ; see [upstream documentation](docs/upstream-viewer.md).

Hidden settings include **FPS counter** and **Frame generation (exp.)**. The counter
reports application FPS (excluding generated frames), frame interval, display Hz,
and AppSW status. Both toggles start off and persist when changed.

### Current limits

- The native Spark path targets static SHARP binary little-endian PLY with SH degree 0.
- The near clipping distance is 1 cm; the catalog uses 125% render scale and 4x MSAA.
- A scene with about 1.18 million splats reached up to roughly 20 application FPS
  without AppSW and roughly 15 with it. Results depend on the scene and viewpoint.
- AppSW's projection artifacts were fixed and confirmed on Pico 4. It remains
  experimental: the subsequent optimization did not improve performance in the
  user's test. Leave it off unless comparing modes; stable 90 FPS is not claimed.
- The APK was installed and visually tested on Pico 4. Mac instructions follow
  Apple's CLI; full model inference was not rerun on a Mac for this release.

## Build from source

Open `projects/Splatviewer_VR_Android` with Unity **6000.0.69f1**, Android Build
Support, SDK/NDK Tools, and OpenJDK. Activate your Unity license. The project pins
the PICO OpenXR package. Use **Tools → VRPhoto → Build Pico 4 APK**, or:

```powershell
& "C:\path\to\Unity.exe" -batchmode -quit `
  -projectPath "$PWD\projects\Splatviewer_VR_Android" -buildTarget Android `
  -executeMethod SparkNativeValidation.ValidateAndBuild -logFile "$PWD\build.log"
```

Output: `projects/Splatviewer_VR_Android/Builds/Pico/VRPhoto-Pico4.apk`.
GPU validation requires a graphics device: omit `-nographics`. Checks cover packing
against upstream Spark, eye projection, close clipping, and AppSW motion output.
Server tests: `server/.venv/bin/python -m pytest -q server/tests` (install pytest first).

## Repository and credits

- `projects/Splatviewer_VR_Android/`: native Pico app.
- `server/`: HTTPS library API, preview generation, browser viewer, and tests.
- `tools/convert-photos-macos.sh`: folder-preserving batch launcher using official Apple SHARP.
- `package/`: Unity Gaussian splat integration and native Spark implementation.
- [Native Spark port notes](third_party/spark/README.md), [release notes](RELEASE_NOTES.md).
- [Enndee's viewer](https://github.com/Enndee/Splatviewer_VR_Android) and
  [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) are the fork's foundations.

The Unity integration retains its MIT license; the Spark port includes the
[Spark MIT license](third_party/spark/LICENSE). Apple SHARP code and model weights
are installed separately under their upstream terms and are not bundled in the APK.
