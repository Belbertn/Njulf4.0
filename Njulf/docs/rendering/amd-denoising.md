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

## Validation and limits

See [the implementation milestone](../performance/milestones/20260914-amd-denoising.md)
for measured costs and qualification limits. Compact layered filtering trades
detail for stability; this is intentional. These options are not a promise of
noise-free rendering, and the initial combined 3 ms mean / 4 ms p95 budget has
not been established.

References: [AMD denoiser documentation](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/denoiser/)
and the [pinned host contract](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/c6efa6bf7f2027b3ec94f28578bb5965eabb9e55/sdk/include/FidelityFX/host/ffx_denoiser.h).
