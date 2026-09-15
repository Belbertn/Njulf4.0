# Reduced reflection filtering and deferred compact glass

Date: 2026-09-14. Starting revision: `9cc0c406779c4e818b6e8c30b0f22da1916f2ead`, with existing dirty renderer changes preserved. Local qualification snapshots record the complete candidate independently of the user's checkout. Hardware: NVIDIA GeForce RTX 3060 Laptop GPU, Vulkan 1.4.341, driver 610.248.0.

## Implementation

- AMD reflections have a ceil(width/2) by ceil(height/2) filter grid, GPU-generated active tile list and three indirect filter dispatches. A separate native hit-distance plane preserves producer addressing. Native guides reconstruct the existing full-resolution history targets.
- Preparation selects a covered native representative and reconstructs missing filter input from compatible current observations or validated history. Upsampling rejects mismatched identity, depth, normal and roughness. Source changes reset history age without substituting black radiance. Missing observations remain distinct from valid black samples.
- Important materials retain their existing higher ray sampling rate and use a tighter upsampling footprint. All selected materials receive AMD filtering; an additional native temporal-only filter was rejected for motion noise.
- Compact glass exports inexpensive native shading and trace inputs, selects the nearest two layers, then runs expensive reflection and physical transmission in separate compute passes. Each invocation handles one layer; dispatch Z selects the layer and invocation order preserves derivative quads. Deeper/unmatched layers use the existing fallback. Native silhouettes, alpha, direct lighting and sorted/weighted composition are retained.
- Independent settings, snapshots and CLI controls select full/half AMD filtering and full/half optical shading. Bypass/debug/failure paths retain native shading. Diagnostics report allocation, filter dimensions, selected optical work and nested GPU stage times.
- The `optical-motion` trajectory provides a deterministic 240-frame camera route and 11 HDR checkpoints. Existing reflection-LOD and full-resolution controls remain available.

Implementation entry points: `HybridReflectionVulkanRuntime.Amd.cs`, `AmdReflectionGpuContract.cs`, `amd_reflection_prepare_reduced.glsl`, `amd_reflection_reconstruct.comp`, `OpticalDenoisingRuntime.cs`, `optical_compact_shade.glsl` and `forward_surface_shading.glsl`.

## Measurements and rejected candidates

Development measurements below use MaterialShowcase at 1920x1080, AMD reflections, area denoising on, compact glass, sorted transparency, all performance options, VSync/validation off, 120 warmup and 240 measured frames. These are development comparisons, not production qualification. Nested denoising times are already included in pass/frame totals.

| Candidate | GPU mean | p95 | p99 | Decision |
|---|---:|---:|---:|---|
| Full AMD + native glass shading | 24.182 ms | 26.446 ms | 28.076 ms | Control |
| Combined glass compute shader | 77.780 ms | 81.971 ms | 83.570 ms | Rejected; expensive combined tracing path |
| Split reflection/transmission, two layers per invocation | 54.619 ms | 57.778 ms | 58.178 ms | Rejected |
| One layer per invocation, both reductions | 16.711 ms | 17.712 ms | 19.564 ms | Glass improvement; reflection quality gate failed |

The last row predates subsequent reflection reconstruction fixes. It is evidence for the glass dispatch change, not a final timing claim for the complete candidate. Cold native pipeline compilation and background shutdown draining are excluded from these steady-state numbers and can take several minutes.

At 1080p, AMD private state falls from 191,031,424 to 85,149,192 bytes, including the reduced path's readbacks (55.4% less). Native optical export/composition still requires its existing large allocation: 535,953,504 bytes. The static showcase reduced selected physical-transmission tasks from 92,928 to 22,888, and admitted transparent reflection rays from 126,853 to 29,248. These are distinct counters; transmission tasks are not equivalent to individual ray-query operations.

Both modes captured 187,488 native optical fragments and reported 5,586 overflow pixels. Overflow retains fallback shading; this workload does not establish overflow-free coverage.

## Validation and rollout

See [compact machine-readable evidence](20260914-reduced-optics-validation.json). Focused behavioral/GPU tests cover layer selection, separate histories, valid black versus missing samples, identity/reset rejection, allocation bounds, settings and replay controls. The broader contract run passed 351 tests with 10 failures reproduced against the starting dirty source.

Full and reduced motion sequences each completed 11/11 capture-contract checkpoints. Those canonical captures validate replay/provenance, not perceptual equivalence. Visual comparison exposed coarser/noisier moving reflections in the half-resolution AMD path. Subsequent sparse-input and history fixes improved it, but did not establish equivalence to native AMD filtering.

**Keep global defaults unchanged and half-resolution AMD explicitly opt-in.** Reduced glass shading is independently usable with full-resolution AMD filtering and is enabled within the opt-in compact glass mode. Do not promote the combined path based only on its timing or memory savings.

The production atomic audit initially failed stale transparent-shader counts. All 25 affected counts were reproduced by compiling the starting source; the pinned counts were refreshed without exempting modules or adding diagnostic atomics. The build continues to enforce the production receiver contract.

## Reproduction

Build with `dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development -m:1 -nodeReuse:false -p:UseSharedCompilation=false` (use `ShippingPerformance` for production configuration). Keep TEMP and renderer caches under an ignored workspace directory on D:.

For the smoother comparison path:

```text
--scene MaterialShowcase --reflection-denoiser amd --amd-reflection-resolution full
--optical-denoising compact --optical-shading-resolution half --transparency-mode sorted
--area-denoising on --performance-optimizations on --performance-optimization-mask all
--vsync off --validation off --max-fps 0 --gi-caustic-mode Off
--benchmark=true --benchmark-warmup-frames 120 --benchmark-measure-frames 240
--benchmark-max-settle-frames 4096 --benchmark-report <run>/benchmark.json
--benchmark-hdr-candidate <run>/scene.pfm
```

Switch optical shading to `full` for the upstream control; switch AMD resolution to `half` for the smaller filter. Add `--benchmark-trajectory optical-motion` for motion timing. Quality sequences require a clean source snapshot and a separate run/cache directory; pace them at 60 FPS so bootstrap cannot skip the fixed GI warmup boundary.

Raw captures and local source/build copies are temporary investigation artifacts. Preserve this compact record and comparisons; retain bulky evidence only for the active investigation and at most two days afterward. Automatic approval review rejected removal of the baseline source copy with reason `blocked by policy`; it was left in place.

## Final user-directed stopping point

Implementation is complete. At the user’s request, further qualification stopped and defaults now enable AMD reflections, half-resolution AMD filtering, compact optical layers, and reduced-resolution optical shading. Explicit saved settings and CLI overrides still take precedence. This supersedes the earlier opt-in rollout decision; measured reflection quality/performance limitations remain. The final default-only edits were not rebuilt or retested. Prior production build passed both shader audits; the final focused suite passed 69 tests and weighted transparency/resize smoke reported zero Vulkan warnings/errors.
