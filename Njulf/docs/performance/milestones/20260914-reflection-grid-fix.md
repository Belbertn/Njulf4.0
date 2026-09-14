# Reflection grid fix — 2026-09-14

Source: `cc35bcc6fc69854dd96e316cd6e14ec69b139e8e` plus dirty working tree.
Existing DDGI, optical denoising, renderer and showcase edits were preserved.
Follow-up to [the diagnosis](20260914-reflection-grid-diagnosis.md).
Compact measurements and final shader hashes: [comparison JSON](20260914-reflection-grid-fix.json).

## Change and decision

The opaque sparse reflection schedule now visits every lane in each sliding
4/16-frame window, with a stable block/lobe permutation. Selection coverage
does not guarantee ray admission; the existing ray budget is unchanged.
Spatial reconstruction now runs for missing observations even at zero temporal
variance and uses only compatible observed/reprojected values or validated
classifier reuse. It preserves receiver identity, depth, normal and roughness
boundaries, measured black, sharp observed reflections and immutable temporal
history. A wider search is attempted only for unsupported rough sparse pixels.

Actual ray misses retain observation provenance through analytic background
resolve. Sparse temporal carry no longer mixes unobserved fallback RGB into
measured radiance during motion; uncertainty attenuates confidence. Broad-lobe
history clipping requires at least four neighborhood observations. Existing
history lifetime, reprojection validation and sharp anti-trail policy remain.
Spatial raw-metadata access is declared in the production render graph.

Accept for the reported regular checker pattern. This is a quality correction,
not full renderer performance or radiometric qualification. Residual stochastic
noise and sparse-vs-dense energy differences remain, especially during motion.

## Workload and image evidence

NVIDIA GeForce RTX 3060 Laptop GPU, Vulkan 1.4.341, reported driver 610.248.0;
Development build, MaterialShowcase, 1600×900, Adaptive HybridRayQuery, optical
denoising on, caustics off, sorted transparency, standard validation, vsync off,
60 FPS cap. Paired diagnostic runs disable transparency to isolate opaque
reflections; full-scene captures additionally check the original material set.
Simulation is frozen, sampling phase synchronized, camera fixed for frames
0–159, moved horizontally by `0.6*sin((frame-160)*pi/32)` for 160–223, then
returned to origin. HDR snapshots at 159/208/319 cover stationary/moving/settled.

Metric: RMS of the phase-conditioned four-neighbor luminance high-pass grouped
by pixel coordinates modulo four. Fixed sphere-interior ROIs exclude silhouettes;
moving frame uses camera-adjusted ROIs. This measures the regular grid rather
than total image error. Final HDR snapshots contain finite, nonnegative values.

| State | Brushed baseline → final | Rough metal baseline → final |
|---|---:|---:|
| Stationary | 0.096119 → 0.001317 (98.6% lower) | 0.203800 → 0.000384 (99.8% lower) |
| Moving | 0.131251 → 0.000984 (99.3% lower) | 0.205367 → 0.002211 (98.9% lower) |
| Settled | 0.055288 → 0.001310 (97.6% lower) | 0.123431 → 0.000240 (99.8% lower) |

Matte-control mean changes are at most 0.011%. A diagnostic dense-ray reference
(phase/admission bypass and full-screen capacity) has stationary mean luminance
1.069/1.086 versus final 0.962/0.872 for brushed/rough metal; moving reference
0.827/1.401 versus final 0.889/1.230. This exposes remaining roughly 8–20%
energy differences rather than treating reduced brightness as proof of quality.
The reference predates the final sparse-only carry/wider-search changes and is
an approximate diagnostic comparator, not a ground-truth rendered gate.

## Timing and validation

Settled CPU frame intervals (40 samples, cap enabled): baseline mean 16.630 ms,
p95 17.697 ms, p99/max 18.692 ms; final mean 16.639 ms, p95 17.843 ms,
p99/max 18.259 ms. Moving p99/max 1573/1865 ms includes synchronous image/export
stalls and is not gameplay tail latency. GPU telemetry repeats two bank
snapshots: reported spatial cost rises from about 330 to 1204 µs, indicating
additional reconstruction cost, but these are not independent frame samples.
Do not infer a GPU speedup or a qualified GPU percentile from this run.

- 22 focused tests pass, zero skipped: production GLSL on Vulkan checks sliding
  coverage and wraparound, block decorrelation, zero-variance reconstruction,
  measured black, classified reuse, surface rejection, unsupported fallback,
  wider search, sharp detail, miss provenance and motion energy/confidence.
- Test project build: zero errors, 47 existing warnings. Sample build: zero
  errors/warnings. All six affected production SPIR-V modules validate for
  Vulkan 1.3 and exactly match the shader overrides used for final captures.
- Final full-scene stationary/moving/settled screenshots were inspected with
  transparency enabled; all three HDR images are finite and nonnegative and
  retain the opaque improvement. Standard Vulkan validation reported no errors.
  Transparent sampling noise and opaque silhouette/motion noise remain visible.
  The owned full-scene host was stopped after fenced captures, performance JSON
  and timings completed because shutdown still waited; no final health report
  was produced, so clean process teardown is not claimed.
  Review image: `artifacts/reflection-grid-20260914/motion-final-full/frame-319.png`.
- Earlier broader contract/showcase selection: 46 passed, three pre-existing
  failures in `ExactReceiverPublication_DefersCacheSpecializationsWithoutRenderTimeCreation`,
  `HybridReceiverCache_UsesCompactProducerOnlyForEligibleSurfacePath`, and
  `VulkanRuntime_ResetHeadersUseSingleOrderedTransferWrites`. These are not
  presented as a passing full suite.

## Rejected iterations and limitations

Initial phase/filter-only changes removed the grid but darkened rough metal
excessively (mean 0.355). Lost ray-miss provenance and single-observation
history clipping were corrected. Clearing the classifier skip marker caused
long-lived analytic reuse and large backboard blocks; that change was reverted
in favor of recognizing the existing validated reuse marker in spatial filtering.
Motion still brightened reflections through fallback RGB blending (rough mean
3.265); confidence-only uncertainty reduced it to 1.230. Wider search alone
did not fix that energy bug, but passes the isolated unsupported-block test.

## Reproduction and retention

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~HybridReflectionSparseGpuTests|FullyQualifiedName~ShaderSources_ContainStrictFallbackShadingAndDebugContracts'
# Active investigation artifacts contain the deterministic diagnostic host:
& artifacts/reflection-grid-20260914/run-motion.ps1 -Variant final
& artifacts/reflection-grid-20260914/run-motion.ps1 -Variant final -FullScene
py artifacts/reflection-grid-20260914/summarize.py
```

All generated capture/build/cache output is under the workspace on D:.
User attachments on C: are untouched. Pruner inspection identified 93 payloads
(0.16 GiB) with inherited older timestamps inside the active diagnostic host;
these remain active inputs and were not deleted. Free space is about 251.7 GiB.
Bulky captures/isolated host are retained for review for at most two days after
completion; this record, compact JSON and production GPU tests are durable.
