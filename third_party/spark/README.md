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
