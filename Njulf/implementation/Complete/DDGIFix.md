# DDGI: fix stale request queue pairing + performance cleanup

## Context

The latest capture (Diag.txt) shows the resolve-failure fix worked (`earlyOutResolve*=0`, sun bounce alive: `direct≈0.03–0.07`, `directHit` 1.2k–2.6k/frame, probe atlas `irrLum≈0.5–0.65`), but exposed the real root defect: **`ringMismatchCorrected ≈ 333–381 of ~350–384 requests, every frame, with the camera stationary and gridMin/ringOffset stable for 240+ frames**. Support is stuck at ~0.40, `supportReject inactive ≈ 374k/frame`, warmup never leaves Recovery (0.25–0.29 warmed), and ~9.5k px/frame run the exhaustive fallback and find nothing (the black patches in the debug views).

### What the analysis established (verified numerically against the samples)

- GLSL `DdgiDecodeLogicalCellFromPhysicalProbeIndex` and `DdgiCalculateLocalPhysicalProbeIndex` (common.glsl:1776–1826) are exact inverses. `decode(1882)` under the live state round-trips fine.
- The failing requests pair `ProbeIndex=A` with `LogicalCell=decode(B)` where B≠A — each field internally valid, but under **ring states from many frames in the past** (reconstructed rings are ~16 scrolled cells behind, inconsistently per axis across samples ⇒ entries originate from *different* past frames, not one snapshot).
- Every current writer produces self-consistent pairs by construction: score derives `LogicalCell=decode(probeIndex)` from the live volume buffer (ddgi_schedule_shared.glsl:118–183); compact copies whole entries into a disjoint output region (`candidateOutputOffset = scanProbeCount`, DdgiProbeVolumeManager.cs:1490); finalize copies whole candidates into the queue; the CPU queue upload (`UploadScheduledProbeUpdateQueue`) does **not** run in pure GPU mode (VulkanRenderer.cs:4860–4878).
- Conclusion: the update passes are consuming **stale queue/candidate entries written many frames ago** through a hole we haven't located statically (candidates: reserve counters live in the prefix buffer words that prefix later rewrites — checked OK; finalize's `priority != bucket → continue` skips silently — a tell-tale if segments are misaligned).
- Additional consequence: because the current self-heal **trusts the LogicalCell**, a stale entry redirects the trace to `forward(stale cell)` — the *wrong* probe. The probe the scheduler actually selected (by age/confidence) never gets updated, so age-refresh/warmed-fraction feedback loops never converge. This is why support/warmup are stuck.

## Changes

### 1. Make the trace robust: derive the cell from ProbeIndex (the fix)
`Njulf/Njulf.Shaders/ddgi_update_shared.glsl`, `ResolveProbeUpdateRequest` clipmap branch (~line 1280):
- Invert the self-heal: keep `request.ProbeIndex` authoritative and recompute `LogicalCell = DdgiDecodeLogicalCellFromPhysicalProbeIndex(request.ProbeIndex, gridMin, ringOffset, counts, firstProbe)` against the live volume buffer. Use that for `probePosition` and the in-grid check. Under ring addressing, a physical slot's current owner cell is *always* valid, so this never needs to drop work and always updates the probe the scheduler scored — restoring the age/confidence/warmup feedback loop regardless of where stale entries come from.
- Keep the existing round-trip comparison purely as telemetry: bump `ringMismatchCorrected` + one-shot sample when the request's cell disagrees (unchanged wiring in RendererDiagnosticsBuffer/reporter).
- Remove the now-dead `DDGI_RESOLVE_FAILURE_CLIPMAP_RING` path and its always-zero counter (flagged previously), or repurpose the diag slot for the stamp below.
- Mirror updates: `ShaderBuildTests` text assertions for the changed lines.

### 2. Find the stale-entry origin: stamp candidates and requests with FrameSerial
- Score pass: write `constants.FrameSerial` into the candidate's `RESERVED0` word (`WriteDdgiProbeCandidate`, ddgi_schedule_shared.glsl:287–308) instead of 0.
- Finalize: copy the stamp into the request's spare word 7 (`WriteDdgiProbeUpdateRequestFromCandidate`, currently writes 0 at ddgi_schedule_shared.glsl:337).
- Finalize: count `priority != bucket` skips into a new (or the freed clipmap-ring) diagnostic counter — this fires only if compact segments are misaligned, which is the prime suspect.
- Trace: extend `RecordDdgiTraceRingMismatchSample` to include `FrameSerial - requestStamp` (age of the consumed entry). One run then pinpoints whether stale entries come from the candidate output region (compact holes), the queue (finalize under-writing), or elsewhere — and with fix #1 already in place, this is diagnosis-only, not load-bearing.
- Mirror: `GPUStructLayoutTests`/`ShaderBuildTests` as needed; reporter prints the age in the existing `ringMismatchSample` string (SampleDiagnosticsReporter.cs:448+).

### 3. Performance: skip the shadow ray for zero-potential hits
`ddgi_update_shared.glsl`, `EvaluateSelectedDdgiLight` (~line 811): the shadow ray is now traced for every hit with `nDotL > 0`. Skip `TraceLightVisibility` when `DdgiTraceEnergyLuminance(noShadowDiffuse)` is below a small epsilon (~1e-4) — the visibility result can't matter. In this capture hits are ~30–40% of ~95k rays/frame; a meaningful slice of the 1.2–1.5 ms `gpuDdgiUs` is shadow rays on dark hits.

### 4. Performance: stop paying for empty exhaustive fallbacks
`Njulf/Njulf.Shaders/forward.frag`, `SampleDdgiIrradiance` (~line 1581): ~9.5k px/frame run `SampleDdgiIrradianceExhaustive` over up to 16 volumes and return empty (`ddgiShaderFallback 9595/127/9468`). Gate it: only run the exhaustive path when the fast-gather result had *some* spatial-but-no-support signal (`spatialCoverage > 0` and `supportCoverage == 0` on fewer than N consecutive frames is not trackable per-pixel, so instead bound it by volume count 4 instead of 16 and early-out per volume when `supportWeightSum` stays 0 after the first cascade). Expect most of this cost to disappear anyway once fix #1 raises support (~0.88 was reached in the one healthy warmup frame).

### 5. Performance: scheduler pass overhead
`ddgi_schedule_reset.comp` + recording in `DdgiSchedulePass`: reset (~190 µs) and finalize (~170 µs) dominate `scheduleUs≈480`, flagged over budget every frame.
- Replace the reset dispatch's counter/prefix/group clears with `vkCmdFillBuffer` (tiny regions, avoids a full compute dispatch + barrier), keeping only the two seeded words as a 1-thread dispatch or moving them into the score pass guarded by `id==0`.
- Finalize is a single-thread loop over ~500 candidates × ~10 storage reads; acceptable short-term, but note follow-up: parallelize selection per bucket (subgroup scan) if it stays >100 µs after the queue fix.

### 6. Cleanup / follow-ups (small)
- Score pass reads reason-flag hints from candidate slots it later overwrites (previously flagged): after fix #2's stamp exists, assert staleness by checking the stamp when reading hints, or have the CPU writer clear `RESERVED0`.
- Re-evaluate warmup exit and the `lowQuality` reject band (~32k/frame, q≈0.20–0.26) after a post-fix capture; don't tune thresholds before then.
- Out-of-DDGI-scope but visible in the capture (separate ticket, no change here): `shadowRecordUs≈31 ms` and `hizRecordUs≈13 ms` CPU record times dwarf all DDGI costs; `FrameFenceWait` stalls 15–24 ms.

## Files to modify
- `Njulf/Njulf.Shaders/ddgi_update_shared.glsl` — fixes #1, #2 (trace side), #3
- `Njulf/Njulf.Shaders/ddgi_schedule_shared.glsl` — #2 (stamp in candidate/request writers)
- `Njulf/Njulf.Shaders/ddgi_schedule_finalize.comp` — #2 (stamp copy + priority-skip counter)
- `Njulf/Njulf.Shaders/ddgi_schedule_reset.comp`, `Njulf/Njulf.Rendering/Pipeline/DdgiSchedulePass.cs` — #5
- `Njulf/Njulf.Shaders/forward.frag` — #4
- `Njulf/Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs`, `Data/DdgiRuntimeSnapshot.cs`, `Data/SceneRenderingData.cs`, `Data/RendererDiagnostics.cs`, `VulkanRenderer.cs`, `NjulfHelloGame/SampleDiagnosticsReporter.cs` — diagnostics plumbing for the stamp/age + skip counter
- `Njulf/Njulf.Tests/ShaderBuildTests.cs`, `DebugToolingContractsTests.cs` — mirror assertions

## Verification
1. `dotnet build Njulf/Njulf.sln` and `dotnet test Njulf/Njulf.sln` (shader text mirrors, layout contracts, addressing round-trip).
2. Run the Sponza sample, capture the same DdgiOnly diagnostics for ~400 frames including an initial camera move, and check:
   - `ringMismatchCorrected` reported age tells us the stale-entry origin (diagnosis goal); the counter itself may stay non-zero but is now harmless.
   - `support` climbs toward ~0.8+ and stays (vs 0.40 frozen), `supportReject inactive` trends down from ~374k.
   - `warmup` leaves Recovery and reaches SteadyState; warmed fractions rise past 0.8.
   - `ddgiShaderFallback empty` drops sharply; `gpuDdgiUs` drops from ~1.4 ms (shadow-ray skip) despite more probes converging.
   - `scheduleUs` p95 < ~300 µs with `scheduleOverBudget=0` on most frames.
   - Debug views: `DdgiSupportCoverage` black patches shrink to geometry interiors only; final render shows stable bounce in the courtyard.