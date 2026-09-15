# AMD denoising

The renderer integrates the GLSL kernels from AMD FidelityFX Denoiser 1.3
(SDK 1.1.4, commit `c6efa6bf7f2027b3ec94f28578bb5965eabb9e55`).
The vendored files and license are in `Njulf.Shaders/ThirdParty/FidelityFX`.
There is no native FidelityFX DLL dependency.

## Enable

```text
--reflection-denoiser amd --area-denoising on --optical-denoising compact
```

The settings are also persisted as `Reflections.Denoiser`,
`Shadows.AreaDenoisingEnabled`, and `OpticalDenoising.CompactLayers`.
Existing defaults are retained pending the performance gate. For comparison,
use `--reflection-denoiser existing|off`, `--area-denoising off`, and
`--optical-denoising on|off|bypass`. Compact glass requires optical denoising
enabled; the `compact` command-line preset sets both properties.

## Signal handling

* **Tube, rectangle and disk lights:** trace one visibility ray selected from
  four geometry-weighted emitter candidates at half width and height. Each of
  the four selected lights has independent AMD visibility history. Pack 8×4
  visibility masks, classify, run the three AMD shadow filters, and upsample
  with receiver-depth rejection before direct-light shading. Stable light IDs
  prevent history from being reassigned when light selection changes.
* **Opaque reflections:** preserve actual SSR/ray-query hit distances. Prepare
  sparse observations using validated geometric history or nearby fresh rays;
  skipped-ray analytic fallback is not a fresh reflection observation. Run AMD
  reprojection, spatial prefilter and temporal resolve before composition.
  Histories retain receiver identity, source, depth, normals, roughness and
  sparse age. FP16 kernels are selected when shaderFloat16 is enabled; FP32
  reflection kernels remain available on other devices.
* **Glass:** AMD's opaque-surface denoiser does not supply a layered refraction
  contract. A custom compact path handles the nearest two exported layers at
  half width and height, with separate reflection/transmission radiance,
  temporal accumulation capped at 16 samples and two spatial passes. Up to
  eight native layers are exported so selection uses depth rather than atomic
  allocation order. Deeper exported layers receive a spatial approximation.
  Original material weights and sorted/weighted-OIT composition are preserved.
  Layers beyond the export/pool capacity retain their original signal.

## Integration contracts

Njulf velocity is current UV minus previous UV; AMD callbacks negate it.
Matrix adapters account for AMD's clip-space Y convention. Roughness is
converted from perceptual to linear, and normals are decoded to world space.
History sampling uses bilinear texel-centre addressing. The vendor entry
points retain their required workgroup/lane remapping.

AMD shadow filtering requires shaderFloat16: the upstream FP32 shadow filter
body is a stub. The path also checks compute subgroup basic/vote/quad support
and falls back to the existing shadow path when unsupported. Reflection
allocation failures fall back to the existing filter. Buffers are bounded by
device storage-buffer limits; compact optical allocation stays within the
configured optical memory budget.

Private histories use two frame banks, explicit Vulkan memory barriers and
serialized graphics-queue dispatches. The area pass is declared between ray
visibility and forward lighting. Resize, scene/camera resets, relevant light
changes and denoiser switches invalidate histories. Diagnostics expose active
paths, allocated bytes and GPU pass timings; the benchmark includes the new
area-shadow denoising pass.

## Optimized execution

Each signal selects one filter backend. AMD opaque reflections bypass the legacy
reflection temporal/spatial passes; compact glass bypasses the legacy optical
filters. Glass is still a separate custom layered filter, not a second AMD pass
over the same opaque signal. Pipelines are created lazily for the selected path.
An AMD setup failure selects the existing reflection backend and resets history
before any filter dispatch for that frame.

The AMD reflection stages reuse the existing 8×8 receiver tile list and indirect
arguments in Adaptive mode; Legacy mode retains screen dispatch. Preparation is
still a full-screen pass so inactive pixels and neighboring tile halos have defined
values. Classifier-validated history carries retain their hit distance without a
second preparation history test. Missing observations alone use the fill logic.

Reflection guide fields are contiguous planes. Seven persistent planes alternate
between history banks; nine scratch planes and tile averages are shared on the
serialized graphics queue. Only hit distance, validity/sample count, reprojection
scratch and tile averages are cleared. At 1920×1080, private reflection state is
191,031,424 bytes, down from 265,940,224 bytes. Sampled aliases of the existing
radiance/variance histories provide hardware bilinear filtering in GENERAL
layout; geometry and identity rejection remain explicit. No history copy is added.

Compact glass caches repeated neighborhood matches, geometry weights and native
composition inputs. A workgroup aggregates history-reuse counts before publishing
one global atomic. Every lane reaches the aggregation barriers, including empty
and out-of-bounds lanes. Native export and composition resolution are unchanged.

Shadow filtering runs each stage across all selected lights before the next
stage. One native-pixel invocation packs all light results, avoiding repeated
read/modify/write publication. There are `5 × selectedLights + 1` dispatches,
versus `6 × selectedLights` previously. Private AMD dependencies use compute and
transfer scopes; no asynchronous queue is introduced.

Diagnostics expose `DenoisingDispatchCounts` and nested
`DenoisingStageMicroseconds`. Benchmarks put those nested measurements in
`DenoisingStages`, separate from the pass totals: never add both sets together.
For three area lights, the expected AMD counts are 16 shadow dispatches, one
reflection preparation and three reflection filtering dispatches. Compact glass
adds five dispatches; corresponding existing-backend counts must be absent.

## Validation and limits

See [the implementation milestone](../performance/milestones/20260914-amd-denoising.md)
and [optimization milestone](../performance/milestones/20260914-amd-denoising-optimization.md)
for measured costs and qualification limits. Compact layered filtering trades
detail for stability; this is intentional. These options are not a promise of
noise-free rendering, and the initial combined 3 ms mean / 4 ms p95 budget has
not been established.

References: [AMD denoiser documentation](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/denoiser/)
and the [pinned host contract](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/c6efa6bf7f2027b3ec94f28578bb5965eabb9e55/sdk/include/FidelityFX/host/ffx_denoiser.h).

## Reduced-resolution execution

`Reflections.AmdHalfResolution` selects a ceil(width/2) by ceil(height/2)
AMD filter grid. Native hit-distance writes use an explicit header offset, so
reducing guide storage cannot overrun the allocation. Preparation chooses a
covered native representative, publishes depth/normal/roughness/identity guides,
and appends active low-resolution 8x8 tiles. The three AMD stages use that list
through indirect dispatch. Reconstruction writes the existing native histories.
The existing material-based full-resolution ray sampling tier protects mirrors;
they use AMD filtering with a tighter reconstruction footprint. Full-resolution geometry remains available for
edge rejection; it is never averaged across unrelated receivers.

Preparation fills unobserved filter cells from nearby compatible current-frame
observations or validated history. Source transitions reset sample age without
injecting black history. Native reconstruction also searches beyond the immediate
bilinear footprint when sparse tracing leaves holes.

`OpticalDenoising.ReducedResolutionShading` applies when compact filtering is
active. Native forward shading exports material, geometry and trace inputs along
with cheap fallback lighting. Compact selection retains the nearest two layers
per covered 2x2 representative; separate compute dispatches trace their expensive
reflection/refraction paths before temporal filtering. Both paths share
`forward_surface_shading.glsl`, including hit lighting and physical transmission.
Each invocation shades one selected layer; the dispatch's Z dimension selects
the layer. Local invocation order preserves the shared shader's 2x2 derivative
quad layout. Reflection and transmission use separate pipelines to avoid keeping
both tracing paths live in the same invocation.
Native correction preserves silhouettes, alpha and sorted/weighted composition.
Deeper layers and unmatched edges retain their native fallback. Missing samples
have separate confidence from valid black observations.

Controls for comparisons:

```text
--reflection-denoiser amd --amd-reflection-resolution half|full
--optical-denoising compact --optical-shading-resolution half|full
```

The `full` optical control retains compact filtering with native shading, making
it a direct upstream-cost comparison. Bypass and debug views use native shading.
Allocation/pipeline failures keep the established fallback paths. Resolution
changes invalidate history; resource replacement waits for in-flight work.

Diagnostics expose actual filter dimensions, padded dispatched pixel count,
private allocation bytes, deferred-shading state and selected optical tasks.
The half-resolution reflection path adds native reconstruction; deferred glass
adds `Denoising/GlassReflectionShading` and
`Denoising/GlassTransmissionShading` (the latter requires ray queries). Include these and preparation/correction when
comparing costs. Nested stage times must not be added again to frame/pass totals.

For motion qualification, `--benchmark-trajectory optical-motion` replays a
240-frame lateral/forward camera movement through MaterialShowcase. The matching
quality-sequence trajectory exports 11 HDR checkpoints, including adjacent-frame
pairs around direction changes. `reflection-lod` remains the dedicated mirror
and roughness-distance replay.

### Rollout status

The existing global defaults remain unchanged. `AmdHalfResolution` defaults to
false: moving showcase captures still show a visible quality difference from
native AMD filtering, so the smaller filter is explicitly opt-in. Reduced glass
shading defaults on only within the opt-in compact glass mode. For smoother
reflections while reducing glass work, use AMD `full` with optical shading `half`.
The native optical shading control remains available for exact comparisons.

Final default selection: AMD reflections, half-resolution AMD filtering, compact glass, and reduced-resolution glass shading are enabled at the user’s request. This supersedes the opt-in rollout guidance above. Use the independent full-resolution overrides when finer reflection detail is needed; existing saved settings take precedence.
