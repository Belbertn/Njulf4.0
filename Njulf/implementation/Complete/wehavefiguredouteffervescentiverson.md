# GI Performance Pass — Issue List (revised against the Ctrl+F2 snapshot)

## Context

GI (Simple DDGI, 3 rings, 15,368 probes, `DdgiHigh`) is producing correct results, so the
work shifts to performance. `GiSponzaRightWallStationary` at 1600x900 on an **RTX 3060
Laptop** runs at ~90 ms/frame against a `Development 1080p60` budget of 6 ms CPU / 10 ms GPU.

The `performance20260802061609` snapshot has **GPU timing enabled and the GI CPU timers
populated**, which resolves the measurement blind spot in the first pass of this analysis and
materially reprioritises it. Two findings were confirmed and sharpened; the geometry-submission
section has been demoted from "major" to "marginal" because the depth and Hi-Z passes are now
measured at under 1 ms combined.

**This document is analysis only — no code has been changed.**

---

## The measured frame

Both sides are now fully attributed. Every timer below is from the snapshot.

### GPU — 85.909 ms total (budget 10 ms)

| Pass | µs | Share |
|---|---:|---:|
| **`GpuForwardOpaqueMicroseconds`** | **62,567** | **72.8%** |
| **`GpuTransparentMicroseconds`** | **11,956** | **13.9%** |
| `GpuDdgiUpdateMicroseconds` | 7,507 | 8.7% |
| ↳ `GpuSimpleDdgiTransportMicroseconds` | 6,241 | |
| ↳ `GpuSimpleDdgiBlendMicroseconds` | 930 | |
| ↳ publish / relocate / trace | 162 / 91 / 83 | |
| `GpuAmbientOcclusion` + blur | 2,128 | 2.5% |
| `GpuDepthPrePassMicroseconds` | 963 | 1.1% |
| AA / shadow / composite / skinning / Hi-Z / foliage | 789 | 0.9% |
| **Sum** | **85,910** | ✓ matches `GpuFrameMicroseconds` |

### CPU — 92.679 ms total (budget 6 ms)

| Item | µs | Share |
|---|---:|---:|
| **`CpuSimpleDdgiRecordMicroseconds`** | **84,587** | **91.3%** |
| `CpuAccelerationStructureBuildMicroseconds` | 1,063 | 1.1% |
| `CpuSecondaryCommandRecordMicroseconds` | 1,011 | 1.1% |
| `CpuPrimaryCommandRecordMicroseconds` (the *entire* render graph) | 814 | 0.9% |
| `CpuFarFieldRecordMicroseconds` | 656 | 0.7% |
| `CpuSceneBuildMicroseconds` | 228 | 0.2% |
| everything else | ~4,300 | 4.6% |

`CpuGlobalIlluminationRecordMicroseconds = 86,306` (P95 **92,563**) against a
`GlobalIlluminationCpuBudgetMilliseconds` of **0.25** — **345x over budget.**

`CpuWaitForFrameFenceMicroseconds = 7` and `RuntimeWorstStallMicroseconds = 7`: the CPU never
blocks on the GPU. With 2 frames in flight the frame is `max(92.7, 85.9)` ≈ **92.7 ms,
CPU-bound — but only just.** Fixing the CPU alone lands at ~86 ms; fixing the GPU alone lands
at ~93 ms. **Both must be fixed.**

### So there are exactly two problems

1. **`SimpleDdgiVolumeManager` CPU upload — 84.6 ms, 91% of the CPU frame.**
2. **`forward.frag` per-pixel cost — 74.5 ms across opaque + transparent, 87% of the GPU frame.**

Everything else in this document combined is under 12 ms of GPU and under 8 ms of CPU. Rank
accordingly.

---

## 1. CPU: `SimpleDdgiVolumeManager` per-frame upload — 84.6 ms *(confirmed, was a hypothesis)*

`Resources/SimpleDdgiVolumeManager.cs:1333-1536`, timed into `_lastUploadMicroseconds` at
`:1536`, surfaced as `CpuSimpleDdgiRecordMicroseconds` (`VulkanRenderer.cs:8745`).

**This is not the legacy CPU scheduler.** `DdgiGpuSchedulerFallbackReason` is empty and
`DdgiSchedulerMode = Gpu`, so the documented 67 ms fallback path
(`implementation/Complete/DDGIGpuSchedulingMigrationPlan-20260628.md:16-22`) is *not* what is
running. The cost is in the upload method itself.

**It is scene-independent.** From the two earlier console dumps: an empty scene (8 objects,
18 meshlets) still cost **55.0 ms** of CPU draw with the same 15,368 probes. Geometry adds
only a few ms on top. The cost tracks probe count.

Candidate O(probeCount) work inside that region, none of it individually timed:

- `:3181 RefreshProbeSchedulingImportance()` — full nested loop at `:3195-3210`, gated on
  `_schedulerVisibilityFullRefreshRequired`
- `:2135 RefreshPersistentSchedulerState()` — **called twice**, at `:1503` and `:1506`; can
  trigger `RebuildPersistentSchedulerState()` (`:2185`), clearing ~15 heaps/histograms and
  full arrays
- `:3538 BuildUpdateQueue()` — six multi-pass sweeps over work classes x volumes
- `:6090 _probeStateDirtySlots.Sort()` in `UploadProbeState`, with an O(probeCount) rebuild
  fallback at `:6111` when runs exceed `MaxSparseProbeStateUploadRuns`
- `:3472 MarkFreshForNewOrScrolledProbes`, `:3777 RefreshProbeLifecycleTelemetry`,
  `:6605 PreserveToroidalAtlasData` — three more full sweeps

There is a **feedback loop**: `RefreshPersistentSchedulerState` sets
`_schedulerVisibilityFullRefreshRequired = true` (`:2150`) on lighting-dirty, volume-generation,
or convergence-readback changes, forcing the full importance rebuild on the next frame.

Corroborating symptoms in the snapshot: `DdgiPartialRefreshFrameCount = 757`,
`DdgiSkippedProbeCount = 13320` (13,320 of 15,368 probes are skipped every frame yet still
walked), and `DDGI dirty first-update latency = 7 frames` against a 1-frame target.

**First action is to sub-time this method** — there are currently no sub-timers inside the
84.6 ms, so the specific culprit among the seven candidates above is still unidentified. Once
located, the fix is to make the refreshes incremental rather than full-field.

Two smaller CPU items nearby, both flagged OverBudget by the snapshot:
- `Material GI upload P95 = 3.511 ms` against a 0.25 ms budget (`MaterialUploadP95Microseconds`).
- `CpuAccelerationStructureBuildMicroseconds = 1,063` — 1 ms/frame of BLAS/TLAS work.

---

## 2. GPU: `forward.frag` — 74.5 ms across the opaque and transparent passes

NSight Summary for the opaque draw: SM 20.4%, L2 5.0%, PCIe 22.6% — **nothing saturated, pure
latency stall.** Pixel-warp occupancy 14.4% (6.9 avg warps), 71.9% of warp slots unallocated,
**166 live registers**, 23.7/32 active threads per warp. Input dependencies: **LSU Global
Memory Load 63%, LSU Local Memory Load 15.9%.** Top-Down attributes **73.94% of all samples to
`forward.frag:3663`**, which is `void main()` — i.e. the whole inlined fragment shader.

Derived per-pixel cost: 62,567 µs over a 1600x900 frame ≈ **43 ns/pixel**.

### 2a. `EstimateFarFieldSkyVisibility` runs on every pixel and its result is multiplied by 0.00023 *(highest-value single fix)*

`forward.frag:4531-4533`:
```glsl
if ((simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
    simpleEnvironmentFallback *= EstimateFarFieldSkyVisibility(fragWorldPosition, ddgiNormal);
finalDiffuseIndirect = finalDdgiDiffuse + simpleEnvironmentFallback * simpleFallback * indirectAo;
```

The snapshot measures the multiplier directly:
**`DdgiForwardEstimateEnvironmentFallbackWeight = 0.000233`.**
(`simpleFallback = (1 - ownership) * environmentFallbackIntensity`, `forward.frag:4460`;
`DdgiAverageOwnershipConsumedEstimate = 0.99976`.)

What that buys: `ddgi_simple_shared.glsl:1796-1851` fires **3 cone traces** (`coneCount = 3u`,
`:1817`) into `TraceFarFieldPaged` (`farfield_clipmap.glsl:483-564`), whose loop runs to
`p.maxTraceSteps = 256` (`RenderSettings.cs:1941`) across a ~876 m trace distance. Each step
costs a `log`+`pow` cascade select (`:511`), an **open-addressed linear-probe hash walk over up
to 128 slots** (`FindFarFieldPage`, `:308-348`, 1-4 dependent `ReadStorageWord` per probe), a
6-word voxel material read (`:535`), and a distance-field read (`:555`). The earlier console
dump measured **`skySamples = 1,729,372`** calls per frame — ~1.2 per pixel.

**The correct guard already exists in this codebase**, in `SampleSimpleDdgiUnifiedIrradiance`
(`ddgi_simple_shared.glsl:2184-2192`):
```glsl
float fallbackWeight = (1.0 - radiometricOwnership) * p.environmentFallbackIntensity;
if (fallbackWeight > 0.0001)
{
    vec3 fallback = SimpleDdgiEnvironmentIrradianceFallback(safeNormal, p);
    if ((p.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
        fallback *= EstimateFarFieldSkyVisibility(worldPos, safeNormal);
    irradiance += fallback * fallbackWeight;
}
```
`SampleSimpleDdgiSolverBounceIrradiance` (`:2237`) guards the same way. **Only the forward call
site is missing it.** Adding `simpleFallback > 0.0001` to the `:4531` condition is a
one-condition change with an in-repo precedent, and on this scene it is provably a visual no-op.

### 2b. The transparent/decal pass costs 12 ms for four objects — same root cause

`GpuTransparentMicroseconds = 11,956` for `GeometryDecalObjectCount = 4`,
`GeometryDecalMeshletCount = 481`, `TransparentMeshletCount = 481`, `transparentObjects = 0`.

`TransparentReceiveGlobalIllumination = 1` and `DecalReceiveGlobalIllumination = 1`, so these
fragments run the same GI gather and far-field raymarch. `DdgiDecalReceiverSampleCount = 1227`
against `DdgiGatherTileCount = 5700` puts decal coverage at ~21% of tiles, giving ~38 ns/pixel
— **statistically the same per-pixel cost as the opaque pass.** This is not a separate
pathology; it is the same shader, and 2a fixes both. `TransparentSortMicroseconds = 188` is
irrelevant by comparison.

### 2c. 166 live registers caps occupancy at 14.4%

`forward.frag` is a 5,199-line uber-shader. The "Simple Full-Input Opaque" permutation in the
capture strips only 4 `#if FORWARD_SIMPLE_OPAQUE` regions, so the full GI + far-field path and
82 diagnostic-accumulator call sites remain compiled in. **15.9% of stalls are local-memory
loads** — there are no declared local arrays in `forward.frag`, `ddgi_simple_shared.glsl`, or
`farfield_clipmap.glsl`, so that traffic is register spill. Splitting the far-field and
gather-recovery paths out of the main permutation is the structural fix behind 2a's tactical one.

### 2d. `ReadStorageWord` — scalar bindless loads with unconditional `nonuniformEXT`

`common.glsl:1959-1962`:
```glsl
uint ReadStorageWord(uint bufferIndex, uint wordOffset)
{
    return BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset];
}
```
**The top three entries in the NSight Hotspots table** (5.93% + 5.92% + 1.74% self samples).
Called from 433 sites in `common.glsl`, 43 in `forward.frag`, 44 in `ddgi_simple_shared.glsl`,
30 in `farfield_clipmap.glsl`. Two compounding costs:

- **`nonuniformEXT` on a dynamically-uniform index forces a descriptor waterfall loop.** Most
  callers pass a buffer index straight from a params struct or push constant, uniform across
  the draw. Consistent with `ALU Integer Compare` being the second-largest instruction family
  at **14.45%**, plus `Integer Multiply` at 9.16% — roughly a third of issued instructions are
  addressing and ballot overhead, not shading.
- **Unvectorized scalar loads.** `ReadSimpleDdgiVolume` = 24 scalar words,
  `ReadSimpleDdgiProbeState` = 8, `ReadFarFieldVoxelMaterial` = 6 (2.48% self samples alone).
  A `uvec4` accessor or `std430` typed buffers collapses these 4:1.

### 2e. `ReadSimpleDdgiParams` costs 52 scalar words and is re-read 3-5x per pixel

13 `ReadStorageVec4` (`ddgi_simple_shared.glsl:653-712`), with 10 fragment-reachable call
sites: `forward.frag:4409`, `SampleSimpleDdgiGather:1937`, `EstimateFarFieldSkyVisibility:1842`,
`ResolveSimpleDdgiMaterialTransportProvenance:3650`, and more. The compiler cannot CSE across
the intervening raymarch. Thread the struct through as a parameter, or move it to a UBO.

### 2f. The gather recovery loop can run 16 volume gathers per pixel

`SampleSimpleDdgiGather` (`ddgi_simple_shared.glsl:1934-2155`): primary gather at `:1989`,
early-out only at `ownership >= 0.95` (`:2016`), fallback gather at `:2068`, then a recovery
loop at `:2098-2143` bounded by `SIMPLE_DDGI_MAX_GATHER_VOLUME_SAMPLES - 2 = 14`, each
iteration doing a `FindSimpleDdgiFallbackVolume` walk plus a full 8-corner gather. The earlier
dump showed **`simpleGather primary/second/rate = 1918277/1041766/54.3%`** — over half of
shaded pixels run at least a second full gather, and `visAtlas = 942680` was the dominant
rejection reason, i.e. work done and discarded.

Note `DdgiAverageSpatialCoverageEstimate = 0.99996` and `DdgiAverageSupportCoverageEstimate =
0.99995` — coverage is essentially perfect on this scene, so the recovery path is doing almost
nothing useful here. Raising the `:2016` early-out threshold is worth measuring.

### 2g. DDGI transport does a full 8-corner gather per ray — 6.2 ms

`GpuSimpleDdgiTransportMicroseconds = 6,241`, 83% of the 7.5 ms DDGI update budget (which is
itself 3x the 2.5 ms `GlobalIlluminationGpuBudgetMilliseconds`).
`ddgi_simple_transport.comp:155` and `:180` call `SampleSimpleDdgiSolverBounceIrradiance` →
`ddgi_simple_shared.glsl:2222` → a full `SampleSimpleDdgiGather`.
`SimpleDdgiTransportSolveRayCount = 212,960` per frame. Note by contrast that
`GpuSimpleDdgiTraceMicroseconds` is only **83 µs** — the ray-query tracing is nearly free; the
solve is the cost.

### 2h. Ray dispatches are sized for the widest ring

`ddgi_simple_trace.comp:170-187` and `ddgi_simple_transport.comp:58-74` map
`gl_GlobalInvocationID.x` through `params.raysPerProbe` (the *max* ring count, 128 —
`SimpleDdgiVolumeManager.cs:5342`) and early-out when `rayIndex >= activeRayCount`. Mid-ring
probes use 64 and far-ring 32, so 50-75% of those launched threads exit immediately.

---

## 3. Measurement gaps that remain

**A1 and A2 from the first pass are resolved** — GPU timing is on (`GpuTimingValid = 1`) and the
GI CPU timers are populated. What still needs attention:

- **No sub-timers inside the 84.6 ms.** `CpuSimpleDdgiRecordMicroseconds` is a single opaque
  number covering seven candidate O(probeCount) sweeps. This is the blocking gap for issue 1.
- **The console reporter still prints none of these fields.** `SampleDiagnosticsReporter.cs`
  omits `CpuSimpleDdgiRecordMicroseconds`, `CpuGlobalIlluminationRecordMicroseconds`,
  `CpuFarFieldRecordMicroseconds`, and `CpuAccelerationStructureBuildMicroseconds`; the GI line
  prints only the legacy `cpuSsgiUs=0, cpuDdgiUs=0`. Anyone reading the console still sees
  "GI CPU cost = 0" while it is 91% of the frame.
- **Detailed DDGI diagnostics are still on in this capture** — `DdgiDetailedCountersEnabled = 1`.
  `ddgi_simple_shared.glsl:1844-1849` issues two *unsampled* `atomicAdd`s to fixed addresses on
  every `EstimateFarFieldSkyVisibility` call (~3.5 M fully-contended atomics/frame from the
  fragment shader). Most gather counters are 1-in-256 sampled (`forward.frag:18`); these two are
  not. Re-measure with counters off before treating 62.6 ms as the true opaque cost.
- **`SceneSubmissionGpuIndirectMeshletTaskCount = 242,574` is a double count.**
  `ForwardPlusPass.cs:599-601` adds CPU-side *capacity* while `VulkanRenderer.cs:11857`
  pre-seeds the same counter from the previous frame's readback. The real number is
  `SceneSubmissionGpuEmittedCount = 44,942`. Same for the console's `forwardTasks`.
- **27,101 meshlets are dropped with no counter.** `112,177` candidates −
  `40,134` frustum-rejected − `44,942` emitted leaves 27,101, which is the LOD decimation drop
  at `scene_opaque_compact.comp:537` (`lod0LocalIndex >= meshletCount → return false`). The
  drop itself is *correct* — `ApplyGpuLodSelection` runs at `:605`, before `IsMeshletVisible` at
  `:608`, so it decimates a dense list — but it is uncounted, which is why the numbers do not
  reconcile.
- `Forward GI incremental timing is unavailable` (snapshot warning): the GI gather is inside the
  inclusive forward draw, so `GpuForwardGiGatherMicroseconds = 62,567` is attribution-only, not
  an isolated measurement. A paired capture with GI disabled would bound it properly.

---

## 4. Demoted: geometry submission *(was "major" in the first pass — the timings say otherwise)*

The first pass ranked occlusion culling and mesh/task shader sizing highly. **The measured
numbers do not support that.** `GpuDepthPrePassMicroseconds = 963` and
`GpuHiZBuildMicroseconds = 40` — the entire geometry-culling stage the fixes would improve is
**1.0 ms of an 85.9 ms frame.** Perfect culling saves ~1 ms. Keep these on the list, but after
issues 1 and 2.

- **Occlusion culling is structurally weak.** `ForwardOcclusionTestedMeshletsGpu = 44,942`,
  `ForwardGpuOcclusionRejectedMeshlets = 3,922` — an **8.7%** cull rate.
  `VulkanRenderer.cs:1828` forces `PreviousHiZFrameValid = 0` whenever
  `ForwardVisibilityCompactionEnabled` is on (default, `RenderSettings.cs:4044`), so the Hi-Z
  gate in `scene_opaque_compact.comp:616-620` never fires and `SceneOpaqueCompactionPass` culls
  nothing by occlusion. `ProcessDepthCandidate` (`:657-698`) has no Hi-Z test at all. The one
  live test runs against a pyramid built from a prepass in which every candidate already wrote
  its own depth. The engine's own estimate (`HiZPolicyAdaptiveEstimatedSavedMicroseconds =
  5,982`) claims 6 ms saved, but that is a model, not a measurement, and it cannot exceed the
  1.0 ms the depth+Hi-Z passes actually take.
- **Task shaders are 1-thread workgroups that cull nothing.** `forward_compacted.task:7` is
  `local_size_x = 1` with a 17-line passthrough body calling `EmitMeshTasksEXT(1,1,1)` (`:38`).
  Same in `forward.task`, `depth.task`, `shadow_depth.task`, `motion_vector.task`. ~45k
  single-lane workgroup launches per frame at 1/32 warp utilization. Removable on the
  `GpuCompactedIndirect` path since compaction already sets `groupCountX`.
- **Mesh shader geometry is over-declared ~2x.** `forward.mesh:7-8` declares
  `local_size_x = 128, max_vertices = 64, max_primitives = 126`, but the builder in use is
  `RendererMeshletLodBuilder.cs:18-19` at **48 verts / 64 tris** (measured `avgVerts = 39.5`,
  `avgTris = 51.5`). ~60-69% of loop lanes idle, and the driver reserves output storage for 126
  primitives that can never be produced. `local_size_x = 64` / `max_primitives = 64` match.
  Note the inconsistency: `ContentManager.cs:330-331` builds at 64/126 while
  `ProcessedMeshAsset.cs:104-113` builds at 48/64.
- **No normal cone, so backface-cluster culling is impossible.**
  `meshopt_buildMeshlets`/`meshopt_computeMeshletBounds` are not bound
  (`MeshOptimizerCodec.cs:106-135` exposes only simplify + vertex/index codecs). Meshlets come
  from a greedy region-grower (`MeshletBuilder.cs:99-187`) with a centroid+max-radius bound
  (`:250-265`), which also weakens the frustum and Hi-Z tests.
- **Packing tail:** 16,834 submitted meshlets are <16 triangles and 22,938 are <32. The
  48-vertex cap binds before the 64-triangle cap (`MeshletBuilder.cs:145`) and there is no
  merge/repack pass.
- **Hi-Z build is 9 dispatches + 8 full-image barriers** (`HiZBuildPass.cs:88-127`) — but at
  40 µs total this is not worth touching.
- **Minor correctness:** `forward_visibility_compact.comp:206-212`,
  `scene_opaque_compact.comp:316`, and `forward.task:131` loop `i < 5` over a 6- or 7-element
  `projected[]`, so the near extremum never contributes to the screen bound.
- **The CPU culling counters are structurally dead, not broken.** `objectFrustumCulledCpu = 0`
  and `MeshletLod0SubmittedCpu = 112,658` are correct: `SceneDataBuilder.cs:980-983`
  short-circuits `IsVisible` and `:1091-1099` hard-codes LOD 0 when
  `useCameraDependentCpuPayload` is false, which `VulkanRenderer.cs:1607-1609` forces whenever
  GPU LOD selection is on. The CPU list is deliberately a camera-invariant superset. The
  reporter line (`SampleDiagnosticsReporter.cs:375-377`) should say so.

---

## 5. Memory and quality items flagged OverBudget by the snapshot

- **GI memory: 602 MB against a 96 MB budget (6.3x over).** Breakdown from `GiResidency`:
  **GI acceleration structures 406.7 MB**, DDGI cache 158.0 MB, far-field cache 53.8 MB.
  The BLAS/TLAS set dominates and is the obvious target.
- **Tracked GPU memory 1.91 GB / 2.0 GB — `Warning`.** Actual device usage is 2.29 GB of a
  5.48 GB budget, so this is a tracker-budget ceiling rather than a hardware limit.
- **DDGI emissive truncation: 8,098 candidate triangles against a 256 budget → 7,842 dropped,
  `DdgiEmissiveSkippedEnergyFraction = 0.2415`.** ~24% of emissive energy is being discarded.
  Flagged OverBudget; it is a *quality* defect that will show up as missing bounce from emissive
  surfaces. `DdgiEmissiveTableCacheHitCount = 753` vs 4 misses, so the rebuild is cached and not
  a per-frame CPU cost.
- **DDGI dirty first-update latency = 7 frames** against a 1-frame target (OverBudget).
- **Textures: ~427 MB for 80 textures at `maxTextureDim = 1024`** — 5.3 MB each, exactly
  1024²x4 with mips, i.e. uncompressed RGBA8. BC/ASTC appear only in
  `Diagnostics/ImageByteEstimator.cs:79-89` (a size estimator), not in the asset pipeline.
- **Async compute stays off** (`asyncRequested=1, asyncEnabled=0, asyncCandidates=2`). With GPU
  timing now enabled, `AsyncComputeTimingPolicy.cs:195` should start collecting the baseline
  samples it needs, so `Auto` may promote on its own — worth re-checking. Note it cannot help a
  CPU-bound frame, and the two candidate passes total 7.5 ms.
- **AO runs at full 1600x900** (`AmbientOcclusionResolutionScale = 1`, 32 samples + 2 blur
  passes) for 2.1 ms while Hi-Z and bloom base are half-res. Cheap relative to the forward pass.

---

## Recommended order of attack

Nothing here has been changed. This is the sequence I would take; each step is cheap and
de-risks the next.

1. **Guard `EstimateFarFieldSkyVisibility` at `forward.frag:4531`** behind
   `simpleFallback > 0.0001`, mirroring `ddgi_simple_shared.glsl:2184-2192`. Single condition,
   provably a visual no-op on this scene (multiplier is 0.000233), and it targets the largest
   GPU item — helping the opaque and transparent passes at once.
2. **Sub-time `SimpleDdgiVolumeManager` upload** (`:1333-1536`) around the seven sweeps listed
   in issue 1. Without this the 84.6 ms cannot be attributed further. Then make the identified
   sweep incremental.
3. **Re-capture with `DdgiForwardEstimateCountersEnabled` off** so the per-pixel atomics are out
   of the measurement, and re-baseline both numbers.
4. **Vectorize the bindless accessors** (`common.glsl:1959`) and add a uniform-index fast path;
   hoist `ReadSimpleDdgiParams` out of the per-pixel path.
5. **Reduce the transport solve** (6.2 ms) — the gather-per-ray at
   `ddgi_simple_transport.comp:155,180`, and the max-ring dispatch sizing at `:58-74`.
6. **Then** the geometry-submission items in section 4, worth ~1-2 ms combined.
7. **Separately, as a quality bug rather than performance:** the 24% emissive energy loss from
   the 256-triangle budget.

### How to verify

- Re-run `GiSponzaRightWallStationary` and take a Ctrl+F2 snapshot after each change; compare
  `GpuForwardOpaqueMicroseconds`, `GpuTransparentMicroseconds`, and
  `CpuSimpleDdgiRecordMicroseconds` against the baselines in this document
  (62,567 / 11,956 / 84,587 µs).
- For step 1, the invariant to hold is `DdgiForwardEstimateFinalDiffuseLuminance = 0.008659`
  vs `DdgiForwardEstimateRawDiffuseLuminance = 0.008656` — they already agree to 3 parts in
  10,000, which is the same fact that makes the raymarch discardable. Also diff against
  `SampleSponzaGiCaptureHarness.cs`.
- Watch `skySamples` in the console dump: it should fall from 1.73 M toward the count of
  genuinely low-ownership pixels (`DdgiForwardZeroDdgiButNonzeroIblCount = 1` here).
- `GpuFrameMicroseconds` and `CpuTotalDrawSceneMicroseconds` are the two headline numbers;
  both must come down for frame time to move.
