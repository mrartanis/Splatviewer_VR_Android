# Splatviewer_VR Release Notes

## VRPhoto for Pico 4 v0.1.0 (`pico-v0.1.0`)

First Pico-focused release of this fork. Install `VRPhoto-Pico4.apk` (ARM64,
package `com.mrartanis.vrphoto.pico`, Android version name `0.1`).

### Included

- Native Spark rendering of SHARP PLYs with correct stereo projection and stable world placement.
- Full/100% splats and 100% photo render resolution by default; 1 cm near clipping.
- Thumbnail catalog with preserved position, progressive loading, download progress, and next-photo navigation.
- Sharper catalog rendering, aspect-correct previews, scene translation/zoom, B to return.
- Integrated Python HTTPS server and versioned library API with certificate fingerprint confirmation.
- Hidden settings (right grip + left Y), FPS overlay, and optional experimental AppSW.
- English macOS installation/conversion guide and `tools/convert-photos-macos.sh`,
  which uses the folder-preserving batch importer with official Apple SHARP inference.
- The server's bundled Spark + Three.js browser/WebXR viewer is documented as an
  alternative to installing the native APK.

### Conversion workflow

Use `tools/convert-photos-macos.sh` or `vrphoto process` to convert a photo tree.
The importer stages unique names for official `sharp predict`, preserves folders
in the library and skips unchanged completed scenes. `vrphoto add-ply` imports
existing PLYs. Apple code and model weights are installed separately.

### Verification and limitations

The APK was installed and visually tested on Pico 4. AppSW projection artifacts
were fixed; the later motion-pass optimization did not improve performance in
the user's test. One roughly 1.18M-splat scene reached up to about 20 application
FPS without AppSW and about 15 with it. AppSW is off by default and remains
experimental; the counter excludes generated frames. This is not a 90 FPS release.

The APK retains the tested Unity debug signature. Native Spark GPU validations
passed. Server/launcher checks are described in the repository; actual Apple
Silicon model inference was not rerun for this release.

Older sections below describe inherited upstream Windows releases.

## Version 1.4

Adds runtime `.spx` support, preload caching, movie playback, lossless import naming cleanup, and runtime loading performance improvements.

### Added

- Runtime loading for `.spx` Gaussian splat files.
- Browser-controlled preload caching with a RAM-budget limit.
- Folder-based movie mode with progressive loading and adjustable playback FPS.
- Windows file association helpers included directly in the release package.

### Changed

- The editor import preset previously called `VeryHigh` is now `Lossless`.
- Release file association helpers now register `.spx` in addition to `.ply`, `.spz`, and `.sog`.
- The release package now ships as `releases/Splatviewer_VR_v1.4_Windows_x64.zip`.

### Fixed

- Movie mode now loads files from the folder currently shown in the VR browser.
- Runtime movie loading no longer competes with preload caching for I/O.
- Runtime packing and PLY parsing allocate substantially less temporary memory during loading.

## Version 1.2

Adds runtime PlayCanvas `.sog` loading support, desktop browser access from `Esc`, and follow-up fixes for camera reset and SOG decoding.

### Added

- Runtime loading for bundled PlayCanvas `.sog` files using an embedded WebP decoder.
- Desktop access to the in-scene file browser with `Esc`, plus keyboard browsing with arrow keys, `Enter`, and `Backspace`.

### Changed

- Desktop camera reset now correctly restores the initial look direction when loading a new splat.
- Desktop file browser input now cleanly blocks movement and file cycling shortcuts while open.

### Fixed

- Optional higher-order spherical harmonics handling for `.sog` files that only contain `sh0` data.
- `.sog` scale decoding now converts stored log-scale values back to linear scale before rendering.
- SOG parser metadata section inheritance compile errors.

## Version 1.0

Initial public release of the VR-focused Gaussian splat viewer.

### Included

- Windows standalone build.
- OpenXR VR support.
- Runtime loading for `.ply`, `.spz`, and bundled PlayCanvas `.sog` splat files.
- Command-line opening of `.ply`, `.spz`, and `.sog` files.
- VR locomotion with smooth movement and snap turn.
- Controller-based splat switching.
- Desktop mode with mouse and keyboard controls when no headset is active.
- Fullscreen windowed startup.

### Controls

#### VR

- Left stick: move.
- Right stick X: snap turn.
- Right stick Y: move up and down.
- Right controller `B`: next splat.
- Right controller `A`: previous splat.

#### Desktop fallback

- `W A S D`: move.
- Mouse: look.
- `SPACE / C`: move down / up.
- `R / F`: next / previous splat.
- `Q / E`: rotate the current splat.
- `Home`: reset splat rotation.
- `End`: flip upside down.

### Notes

- Bring your own Gaussian splat files.
- A D3D12-capable Windows system and an OpenXR runtime are recommended.
- The repository contains source and project files; the downloadable release package contains the built player.
- Windows file associations can launch the viewer directly with `.ply`, `.spz`, or `.sog` files when the executable is registered as the open command.
