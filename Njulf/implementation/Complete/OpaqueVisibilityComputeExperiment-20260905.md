# Opaque visibility compute experiment — 2026-09-05

The prerequisite gate was not met. Exact compute remains opt-in; temporal cache
routing and foliage compute expansion are deferred. The current-build forward
control is slightly faster over the full GPU frame, and image equality is not
qualified by these captures.

This implements the exact-compute prerequisite of the temporal-adaptive DDGI
receiver-cache plan. The experiment must improve GPU frame p95 by both 3% and
0.25 ms, without a material mean-time or image-quality regression, before adding
temporal cache routing. It is an internal opt-in through
`NJULF_OPAQUE_VISIBILITY_COMPUTE=1`.

The depth prepass writes a primitive/draw-stream visibility ID and facing bit.
Compute classifies visible 2×2 quads into simple, full-input simple, and extended
material queues, then shades indirect work lists using the existing forward
shader template. Each primitive keeps four lanes through material evaluation so
texture gradients and specular antialiasing have valid helper samples. Only
covered lanes continue through lighting and publish color and hybrid-reflection
payloads. The opaque forward geometry draw and its visibility compaction are
skipped when this path is selected.

Queue capacity is bounded by the pixel count. Jobs use 16 bytes and their
partitioned indices use 4 bytes per pixel per frame slot; control storage is
128 bytes per slot. The RG32_UINT visibility attachment uses 8 bytes per pixel.
At 1920×1080, the two fenced queue slots plus visibility cost about 94.9 MiB.

Standard opaque, alpha-masked, thin and skinned meshes use exact shading.
Procedural foliage views retain forward shading; foliage compute reconstruction
is not implemented. Reflection captures, local-probe forward evaluation,
incompatible diagnostics, VRS, receiver-feedback captures, and C4/C5 auxiliary
outputs also retain their existing paths. A performance-mask change after
pipeline creation falls back until the pass is recreated or its original
configuration is restored. Transparency remains forward shaded.
No cache-safe/exact temporal partition, history identity validation, cache-age
policy, or new temporal filter is introduced by this prerequisite.

Evidence is under `.perf-loop-runs/opaque-compute-20260905/`:

- `baseline.json`, `baseline-health.json`, `baseline.pfm`: exact forward baseline.
- `smoke.json`, `smoke-health.json`: complete 240-frame Bistro motion loop with
  Standard Vulkan validation; passed with zero warnings and zero errors, and
  `SceneSubmissionForwardPath=VisibilityCompute`.
- `final-build.log`: Release application build and repository shader checks.
- `packaging-build.log`: final C# guards packaged successfully, zero warnings or
  errors; all 522 shader artifacts up to date.
- `candidate.json`, `candidate-health.json`, `candidate.pfm`: exact compute.
- `forward-control.json`, `forward-control-health.json`, `forward-control.pfm`:
  same-build forward control, requested after the initial HDR gate failed.

All 24 new SPIR-V modules passed `spirv-val --target-env vulkan1.3`; the final
classifier was revalidated after its material-feature mask was aligned with the
CPU classifier. The repository's production receiver, cache, ownership and
diagnostic-atomic shader audits passed. Both final benchmark health reports
passed. No new test suite was added.

The comparison uses Bistro presentation, DDGI High, Normal scenario, stress
budget, validation off, GPU timing, 120 warmup frames and 60 measured frames.
This is a short engineering comparison; the formal production capture contract
requires at least 120 measured frames. The existing image limits are retained:
relative HDR RMSE ≤0.005 and FLIP p95 ≤0.02.

| GPU timing | Current-build forward mean | Current-build forward p95 | Compute mean | Compute p95 |
| --- | ---: | ---: | ---: | ---: |
| Frame | 27.909 ms | 29.547 ms | 28.184 ms | 29.762 ms |
| ForwardPlusPass | 12.406 ms | 12.604 ms | 12.202 ms | 12.401 ms |
| DepthPrePass | 0.684 ms | 0.871 ms | 0.718 ms | 0.880 ms |

Compute frame p95 is 0.215 ms (0.73%) higher, and its mean is 0.275 ms (0.99%)
higher. The modest pass-local saving does not meet the required frame benefit.

The original pre-change forward capture measured 36.013 ms mean / 37.214 ms p95.
Its apparent 20% difference from compute cannot be attributed to the compute
backend: the current-build forward control removes that advantage. The cause
of the old-to-new forward timing difference was not isolated.

Against the original HDR reference, compute has relative RMSE 0.041712 and FLIP
p95 0.017452; the forward control has relative RMSE 0.047849 and FLIP p95
0.018710. Both pass FLIP and fail the unchanged RMSE limit. In the compute
comparison, the highest-error 0.1% of pixels account for 99.72% of squared error,
concentrated around bright reflective highlights. The forward control confirms
that comparison with the old reference is not sufficient to attribute this
error to compute. It does not establish image parity or waive the quality gate.

Stop at the approved prerequisite gate. Do not promote this backend or layer a
temporal receiver cache onto it based on these results.
