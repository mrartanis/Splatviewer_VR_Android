# Native Spark path for SHARP

Ported from World Labs Spark, MIT licensed, revision
`967263804e637776e94395e61ab2f6cb6a04663c` of https://github.com/sparkjsdev/spark.
The upstream copyright and license are in LICENSE in this directory.

Upstream references:

- `src/shaders/splatDefines.glsl`: PackedSplats 16-byte format, logarithmic scale
  quantization, octahedral quaternion encoding/decoding.
- `rust/spark-rs/src/sort.rs`: stable descending half-float bucket sort.
- `src/shaders/splatVertex.glsl`: covariance projection, eigenvectors, blur
  correction, clipping and Gaussian footprint.
- `src/shaders/splatFragment.glsl`: Gaussian falloff, opacity clipping and color.
- `src/SplatGeometry.ts`: indexed instanced quad.

Unity implementation:

- `package/Runtime/SparkSplatData.cs`: SHARP PLY decode directly to PackedSplats,
  stable worker sort. Static centers allow the sort metric to be computed on the
  worker without Spark's WebGL GPU readback. No splats are budgeted out here.
- `package/Runtime/SparkSplatRenderer.cs`: buffer ownership, asynchronous sorting,
  fixed capture pose and lifecycle.
- `package/Runtime/GaussianSplatURPFeature.cs`: direct XR eye raster pass using
  URP's own view/projection/viewport; no scaled intermediate render target.
- `Assets/Resources/SparkNative.shader` in the Unity project: native HLSL port.

Settings match VRPhoto's web renderer: maxStdDev=sqrt(5), blurAmount=0.3,
maxPixelRadius=512, minAlpha=1/255, radial sorting, standard back-to-front alpha
blending. Full uses all input splats. Packed storage quantizes the data as Spark
does; it does not mean a reduced splat count.

Scope: static SHARP binary little-endian PLY, float32 properties, SH degree 0.
Higher SH bands, non-SHARP local formats, animated generators, LoD, paging,
editing and WebGL-specific infrastructure are not part of this path. Existing
local-file rendering remains available. The complete required SHARP path is
independent of the old renderer's asset conversion, compute preparation, sort,
offscreen drawing and composite shader.

OpenCV data stays unmirrored in memory. A single object-space Y reflection maps
it to Unity; the capture pose is fixed on the first eye render. Head motion only
updates the standard URP eye matrices. Sorting uses the central eye while each
eye projects all splats separately.

## Validation

`tools/generate-spark-reference.mjs` runs the upstream `setPackedSplat` encoder
from the pinned checkout at `../spark_reference` with the vendored Three.js and
writes `encoding-vectors.json`. No approximation of the reference encoder is
used to generate these 64 golden cases.

Run Unity 6000.0.69f1 with `-batchmode -quit` (without `-nographics`), this project,
and `-executeMethod SparkNativeValidation.ValidateAndBuild -buildTarget Android`.
Optionally provide `-sparkPly <absolute scene.ply path>` to render a real scene.
The validation checks exact packing, original PLY coordinates, truncated input,
stable radial sort, and GPU projection against Camera.WorldToViewportPoint at
three camera rotations and two eye offsets with asymmetric projections.
It throws before building if any check fails. Captures are written to
`Builds/Pico/spark-validation/`.

Validated on 2026-09-29: all 64 packing cases passed; all six GPU projection
errors below 0.11 pixels; scene 240123_073 decoded and rendered with all 1,179,648
splats. ARM64 APK compiled without shader errors and ADB installation succeeded.
These desktop projection checks do not establish the headset frame rate or the
perceived stability under Pico tracking; those need the on-device session.

Pico session after installation (2026-09-29): both eyes logged 1204x1204,
1,179,648 splats; GPU scene/order buffers 22.5 MiB. Decode/pack/initial sort took
5.84 s asynchronously. Foreground metrics showed 22–30 FPS, typically 35–40 ms
GPU time. The user confirmed the photo is now fixed in space and the frame rate
feels higher, but reported discontinuities resembling tearing. Smooth 90 Hz
rendering is not achieved by this baseline port.

A subsequent on-device A/B/A trial precomputed a separate 24-byte covariance
buffer per splat. Despite matching the rendered image (desktop mean absolute
difference 0.00041/255), it was slower: median 18 FPS with the cache versus 22 FPS
with the packed path; GPU time was about 49.74 ms versus 37.94 ms. The extra
buffer and experimental shader branch were removed; the shipped APK uses the
validated packed baseline. Raw measurements remain in the local build output
`Builds/Pico/spark-covariance-benchmark.json`.

## Experimental Application SpaceWarp

The optional `VRPhotoSpaceWarpFeature` requests `XR_FB_space_warp`. The controller
switches to single-pass rendering while AppSW is enabled and a Spark scene is
visible; normal rendering uses the original multipass mode. A URP render-graph
pass writes the runtime's depth/motion attachments. The native UnityOpenXR calls
return an XrResult integer, with zero indicating success. A watchdog disables the
mode if usable XR motion targets do not appear.

`SparkNativeCommon.hlsl` shares the Gaussian projection between color and motion
passes. Motion uses previous eye view-projection and object transforms. Depth is
an approximation: the closest Gaussian billboard with alpha >= 0.2 writes depth.
This does not represent all transparent layers, and can create reprojection
artifacts. AppSW is disabled by default and explicitly labeled experimental.

Additional GPU validation passed on 2026-09-29: splats at 20 cm and 2 cm remain
visible with the 1 cm near plane; splats at 5 mm and behind the camera clip;
the motion pass matches stationary and both horizontal camera-translation cases.

Pico 4 reported runtime extension support and `AppSW=on` during the user's trial.
Application FPS was initially about 15 and dropped further, with GPU time around
66.8 ms; after disabling it the photo ran around 18-20 FPS. These are observations
from an interactive session, not a controlled benchmark. The user reported strong
artifacts when looking at the photo, fewer when looking down, and no clear gain
in smoothness. AppSW is therefore not recommended for normal viewing yet.

The follow-up found a projection-convention bug in our motion pass: the previous
frame used the XR unflipped GPU projection, while the current frame inherited
color-target matrices (potentially Y-flipped). Reproducing the actual XR convention
in the desktop test exposed nonzero vertical motion even for a stationary camera.
The pass now uses explicit per-eye view/projection/inverse matrices throughout,
following URP's XRDepthMotionPass, independently of color-target globals. GPU
checks cover zero motion over the entire Gaussian footprint, translations along
all three axes in both directions, and yaw/pitch turns. These pass; improvement
in the Pico artifacts still requires the user's follow-up test. The original
color projection and 100% splat budget are unchanged.
