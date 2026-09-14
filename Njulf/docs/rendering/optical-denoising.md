# Transparent optical denoising

`RenderSettings.OpticalDenoising` controls a graphics-queue pass sequence between
transparent shading and weighted-OIT composition. The sample accepts
`--optical-denoising on|off|bypass` and `--transparency-mode sorted|weighted`.
Bypass exports layers but leaves the image and original sampling unchanged.

The forward shader exports reflection and transmission radiance separately from
their material weights. Transmission retains the path's Monte Carlo throughput;
enabled filtering advances its sample seed each frame. A measured black sample
has confidence; an untraced or failed observation does not. Temporal accumulation
uses at most 16 observations, followed by two 3×3 spatial passes at steps 1 and 2.
Spatial output never feeds temporal history. Smooth optical surfaces restrict
spatial filtering to protect detail.
Symmetric spatial exchanges retain energy at rejected neighbors. Zero-weight
lobes skip filtering, and bypass skips both reconstruction dispatches.

Receiver object/material identity, facing, geometric and shading normals,
roughness, previous world position, and source compatibility reject unrelated
samples. Total transmission path length is stochastic and is not a hard history
key. Skinned receivers reject temporal reuse because this export does not carry
previous deformed vertices. Camera cuts, source invalidation, scene/material
revisions, resize, mode changes, and bypass reset history.

Each pixel reserves at most four fragment references. Two frame banks share the
configured memory budget (512 MiB by default), further limited by Vulkan's maximum
storage-buffer range. Each bank contains a 64-word header, five words per pixel,
and a pool of 64-word fragment records. History, moments, and spatial scratch are
inside those records. Allocation failure disables the pass. Layer/pool overflow
invalidates the whole pixel, which retains the original transparent rendering.
Nonoptical layers still participate in foreground attenuation; unsupported
diagnostic/decal output invalidates its pixel.
Allocation is deferred until the first scene that draws transparency.

The CPU constants live in `OpticalDenoisingGpuContract`; GLSL accessors live in
`optical_layers.glsl`. Record offsets below are 32-bit words:

| Words | Contents |
| --- | --- |
| 0–3 | Object/material identity and packed geometric/filter normals |
| 4–11 | Current position/roughness and previous position/receiver distance |
| 12–19 | Original reflection/transmission RGB and observation validity |
| 20–27 | Reflection weight/alpha and transmission weight/fragment depth |
| 28–35 | Path distances, sources, draw/primitive order, facing, previous UV |
| 36–43 | Temporal reflection/transmission RGB and observation counts |
| 44–51 | First spatial iteration RGB and confidence |
| 52–59 | Second spatial iteration RGB and confidence |
| 60–63 | First/second luminance moments for each lobe |

Correction adds only `(filtered − original) × material weight × blend weight`.
Sorted alpha uses actual draw/primitive order and every foreground layer's
transmittance. Weighted OIT uses the existing alpha/depth weight and modifies only
accumulated RGB; revealage and alpha remain untouched. Zero corrections perform
no image write. The pass owns barriers and waits for in-flight work before
replacing descriptors during resize or budget changes.

`RendererDiagnostics` reports allocation, captured fragments, overflow pixels,
history reuses, and four GPU timings. Counters come from the most recently
completed use of the recycled frame bank. `OpticalDenoisingGpuTests` compiles and
executes the production temporal/spatial kernels and blend-weight helper on
synthetic layered inputs; it does not substitute a CPU implementation.
