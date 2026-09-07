# Surface input reuse — implementation and local qualification

This closes the producer-cost investigation in [the first performance list, section 3](NsightGpuTraceAnalysis-20260905.md#3-shared-temporal-validity-strongest-recent-regression-signal). Share geometric inputs only when the complete producer/consumer chain pays; shadow, glossy reflection, and AO rejection rules remain independent.

## Shipping behavior

- Disable the four-word shared-validity producer, its GPU allocation, motion seed writes/varyings, clear, dispatch, and graph traffic. Directional shadows use the existing local depth/normal/identity rejection path. Existing codecs and resource definitions remain dormant for compatibility; changing the CPU policy alone cannot restore the removed shader interface.
- Add **opt-in** compacted opaque/masked depth/motion fusion (`NJULF_DEPTH_MOTION_FUSION=1`, read at process startup). Default is off because broader image qualification is inconclusive. Disabled fusion creates no extra pipelines or identity image.
- The fused prepass reuses the motion mesh shaders and current compacted depth lists, writes depth and velocity together, and suppresses the separate motion draw only after current-frame depth recording completes. Alpha coverage, mirrored winding, sided raster buckets, LOD selection, and camera/scene reset checks stay on their existing paths. A skipped motion frame also invalidates previous-frame motion.
- When directional history needs receiver identity, a depth-tested `R32_UINT` attachment records the winning receiver. Its image-to-buffer copy replaces fragment storage writes to the existing directional scratch bank. Graph usages cover attachment writes, the transfer, and subsequent consumption. The target follows existing resize/disposal ownership. No new history bank or shared validity decision is introduced.
- Fall back for foliage, skinning, camera-only reprojection, visibility rendering, absent motion consumers, missing targets/pipelines, or unavailable compacted submission. Feature isolation still controls the motion producer.
- Benchmark diagnostics expose requested/effective producer and actual identity/shared-validity allocation bytes.

AO retains its lower-resolution geometry/history and rejection policy. Glossy reflections retain roughness/source-aware rejection and their own normals. Neither consumes a lossy shared validity bit.

## Measurements and decisions

Release, RTX 3060 Laptop GPU, driver 610.248.0, 1920×1080, `ddgi-high`, normal scenario, validation off, GPU timestamps, stress diagnostic budget; 120 warmup and 120 measured frames after the existing transport-settling gate. These are short local decisions, not statistical or release qualification. The worktree's pre-existing DDGI changes were preserved throughout.

| Isolated change | Affected GPU chain, before → after | Frame mean / p95, before → after | Decision |
| --- | --- | --- | --- |
| Disable shared producer, Bistro | Motion + directional temporal: 2.934 → 1.687 ms (−42.5%) | 33.493 / 34.185 → 31.940 / 32.524 ms | Keep disabled by default |
| Fusion, Sponza, original local baseline | Depth + motion: 1.006 → 0.780 ms (−22.4%) | 22.638 / 23.062 → 22.374 / 22.670 ms | Useful timing signal; opt-in |
| Fusion, Bistro, contemporary control | Depth + motion + directional temporal: 2.388 → 1.895 ms (−20.6%) | 25.785 / 26.253 → 24.403 / 25.431 ms | Image gate unresolved; opt-in |
| Reuse CSM normal through current history scratch, Bistro | Directional temporal: 0.787 → 0.770 ms (−2.1%) | 25.785 / 26.253 → 25.679 / 26.131 ms | Remove candidate; below 3% target threshold |

The normal experiment wrote full-float geometric normals into the existing current 12-byte shadow-history bank and read them before temporal overwrote each pixel. It preserved invalid-normal rejection and the separate CSM sampling fallback. Its extra stores, reads, and barrier did not demonstrate enough benefit, even without a new allocation. Its shader variants and production plumbing were removed.

Fusion's identity image allocates 8,847,360 bytes on this device (8.44 MiB). The disabled shared producer avoids two logical 16-byte-per-pixel banks (63.28 MiB at 1080p); diagnostics confirm zero shared allocation. Timings include the fused producer's extra outputs and identity copy, and the Bistro aggregate includes the downstream shadow cost increase.

### Image evidence and limits

The original Sponza comparison failed HDR-FLIP despite relative RMSE 0.000691. A separate-path control also failed that reference almost identically (FLIP 0.247569 versus fusion 0.247590). Comparing the already-captured fusion image against that control with the existing comparer **passed**: relative RMSE 0.000305, FLIP p95 0.007377, limits 0.005 / 0.02. No thresholds were relaxed.

Bistro's fusion image still failed relative RMSE (0.068968), although FLIP passed (0.005713). Bistro also showed unrelated forward/transparent timing drift and reference instability in the control. This does not establish a fusion regression or qualify it for default use. No additional performance campaign was started. The original shared-producer capture did not export an HDR reference, so the shared removal has timing and focused contract evidence, not a claimed before/after image proof.

Raw local reports/images/logs are `.tmp/surface-reuse-{shared-bistro,local-bistro,local-sponza,control-sponza,fused-sponza,control-bistro,fused-bistro,normals-bistro}.*`. The direct image recheck is `.tmp/surface-reuse-fusion-control-image.json`. Reports retain original gate failures; the recheck does not rewrite them. `.tmp/surface-reuse-capture.ps1` records the exact launch arguments, sets independent candidate flags, and bounds only its own application's post-report teardown.

## Focused verification

Use the existing motion camera/alpha contract tests plus `SurfaceInputReuseTests` and `TemporalSurfaceValidityTests`. These cover whole-frame fallback eligibility, current-frame completion, graph transfer/history declarations, camera reprojection, alpha-correct motion, and dormant shared traffic. Compile the affected Release projects and validate the changed SPIR-V modules for Vulkan 1.3.

The shader project normally reruns the entire DDGI diagnostic contract matrix whenever its bundle changes. After unchanged DDGI verification consumed over ten minutes, that unrelated matrix was stopped and omitted with an ignored task-local `CustomAfterMicrosoftCommonTargets` override. No tracked build gate was weakened. Changed shaders were compiled and separately checked with `spirv-val`; the full DDGI matrix is not claimed as passed.

The moving-camera check uses the existing 300-frame `sponza-horizontal` trajectory. Vulkan synchronization validation is a correctness check, not a production timing comparison. Detailed final results are recorded below.

- Final Release application build: **passed**, zero warnings/errors (`.tmp/surface-reuse-final-build.log`).
- Focused test filter: `FullyQualifiedName~SurfaceInputReuseTests|FullyQualifiedName~MotionVectorCameraReprojectionTests|FullyQualifiedName~TemporalSurfaceValidityTests|FullyQualifiedName~AlphaCorrectMotionVectorTests`: **42 passed**, no failures/skips (`.tmp/surface-reuse-final-tests.log`). Test compilation reported the existing unrelated CS0162 warning in `SampleDebugViewCycleTests.cs:31`.
- Final affected motion/foliage/fused artifacts: **23 passed** `spirv-val --target-env vulkan1.3`. No new push-constant or storage-buffer ABI was introduced.
- Both 300-frame synchronization runs completed and reported the intended producer (`separate` / `fused`) and zero shared-validity allocation. Each reported **30 validation errors, zero warnings**: swapchain acquire/layout synchronization, meshlet demand fill/copy ordering, and hybrid reflection indirect-argument ordering. No reported hazard names the moved depth/motion/identity resources. Existing validation duplicate limits apply; this is not a clean validation sign-off. Logs: `.tmp/surface-reuse-moving-{local,fused}-sync-sponza.stderr.log`.
- Moving HDR comparison remains unqualified. Even with matching synchronization instrumentation, relative RMSE was **0.013607** and FLIP p95 **0.592142**, exceeding 0.005 / 0.02 (`.tmp/surface-reuse-moving-sync-image.json`). This check observes the route's final image, not every temporal frame. Fusion therefore remains **off by default**, with no claim of completed temporal/image qualification.

No full solution suite, DDGI matrix, hardware sweep, or further image/timing campaign was run. To promote fusion later, resolve the image-comparison failures, distinguish capture reproducibility from fusion defects, and demonstrate the same measured saving under the moving/Bistro quality gates; do not add AO/reflection consumers or normal storage in the meantime.
