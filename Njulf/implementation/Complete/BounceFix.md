# Bounce-lighting diagnostic: verification, snapshot analysis, and fix plan

## Context

Branch `claude/bounce-lighting-diagnostic-eq99jc` (== `origin/Simplified-SDF`, head `272f7d0`) carries three commits implementing `Njulf/implementation/SimplifiedSdfPlan-20260711.md` (minimal "simple DDGI" rebuild + far-field clipmap, legacy DDGI kept as A/B reference). The user's Cornell-box screenshot shows blotchy bounce lighting (per-probe blobs on walls/ceiling, dark splotches on the boxes) and a performance snapshot was captured. Task: verify the pushed work, analyse the snapshot, diagnose the artifact — then fix.

## Diagnosis (completed)

### Root cause of the bad screenshot: the new pipeline never runs

- `DdgiSimpleEnabled` defaults `false` (`RenderSettings.cs:1496`) and **nothing ever sets it true** — the Cornell sample config (`SampleGlobalIlluminationValidation.ConfigureRenderSettings`) enables `Mode=Ddgi, UseDdgi, UseRayQueryBackend` but not the simple switch; `SampleInputController` has no toggle for it. The plan's Phase-1e A/B enablement was never delivered.
- `EffectiveUseDdgi` / `EffectiveUseSimpleDdgi` (`RenderSettings.cs:2017/2022`) are mutually exclusive on that flag ⇒ **legacy 4-cascade GPU-scheduled clipmap DDGI executed**; `SimpleDdgiTracePass/BlendPass`, `FarFieldClipmapBakePass` skipped.
- Snapshot confirms: 4 clipmap volumes, GPU scheduler ran, legacy trace/blend/relocate/publish executed, forward gather chose the legacy clipmap path for 5700/5700 tiles, and zero simple-path counters exist.
- So the screenshot is the **legacy** DDGI — the system the plan itself calls misbehaving — running 4× over probe budget (32 256 vs 8 192; explicit snapshot warning), 390 probe updates/frame (83-frame refresh), every probe stale (age 121), cascade-0 warmup 73.7%, scheduler bucket overflows (837) + budget rejections (130), GI memory over budget (112.9 MB vs 100.7 MB).

### Wiring defects (found in review, to fix)

1. **forward.frag selects its gather branch from the contents of the `SimpleDdgiParams` storage buffer** (`forward.frag:3360-3361`), which is only written while simple mode is active (`VulkanRenderer.cs:4865-4873`, `SimpleDdgiVolumeManager.cs:96-101`) and never zero-initialized. Frame-0 reads uninitialized memory; toggling simple on→off leaves the buffer saying "enabled" (gather would keep sampling a frozen atlas).
2. **No first-activation convergence path**: irradiance/visibility atlases never cleared (`SimpleDdgiVolumeManager.cs:167-183`); blend always does `mix(new, prev, hysteresis≈0.97)` in place (`ddgi_simple_blend.comp:71-73,101-103`) ⇒ first activation blends toward garbage for ~150 frames.
3. **No simple-path diagnostics** — nothing in `RendererDiagnosticsBuffer`/snapshot records whether the path ran (why the snapshot couldn't show it).

### Simple-path shader bugs (would blob just like the screenshot once enabled)

4. **Nearest-neighbour octahedral sampling, no bilinear, no border handling** — `SimpleDdgiDirectionTexel` does `floor(uv*N)` (`ddgi_simple_shared.glsl:152-158`), used for irradiance and, critically, the 16×16 visibility map (`:229-231`). Per-probe visibility jumps discontinuously as the receiver moves ⇒ the classic cellular blob artifact.
5. **No normal/view surface bias**: `viewDir` param unused (`:200`), grid coords use raw `worldPos` (`:206`) ⇒ Chebyshev self-occlusion ⇒ dark splotches on occluders.
6. **Energy constant wrong**: blend stores `2 ×` cosine-weighted mean radiance (`ddgi_simple_blend.comp:68-69`) where irradiance is `π ×`; forward divides by π ⇒ GI globally ~0.64× too dark. The mirror test enshrines the wrong constant (`SimpleDdgiShaderMirrorTests.cs:76-95`).
7. **Backface weight linear + floored at 0.05** (`ddgi_simple_shared.glsl:227`) instead of the plan's `((dot+1)/2)²` ⇒ ≥5% leak through walls; visibility weight also floored at 0.03 (`:235`).
8. **Per-frame "rotation" only spins azimuth about +Z** (`:120-129`) ⇒ same latitude rings sampled every frame; structured estimator error locks in under hysteresis.
9. **Visibility texels with zero ray weight this frame are overwritten with a default** `vec2(spacing*4, spacing²*16)` (`ddgi_simple_blend.comp:98-100`) instead of keeping the previous value; with `dot¹⁶` over 256 texels and 128 rays most texels are empty each frame ⇒ visibility noise/zero variance.
10. Minor parity gap: simple branch never adds `diffuseIbl` fallback (`forward.frag:3393-3394`), unlike the legacy compose (irrelevant for Cornell — env off — but worth parity).

Verified correct (no action): octahedral encode/decode alignment, scratch layout/stride agreement, probe indexing, trilinear corner weights, single-π application, firefly/half-float guards, trace→blend→forward ordering + barriers, buffer size math, dispatch sizes, mutually-exclusive gather branches.

Pre-existing, out-of-scope observations to report to the user: CPU renderer 18.3 ms vs 6 ms budget dominated by Hi-Z record (7.7 ms) + Hi-Z final barrier (6.6 ms); 33 MB retained staging-overflow allocation (visibility-atlas-sized); legacy DDGI budget failures are inherent to the legacy DdgiHigh preset.

## Fix plan (implementation)

All on branch `claude/bounce-lighting-diagnostic-eq99jc`, commit + push when green.

### Step 1 — Make the simple path reachable and the selector authoritative
- `NjulfHelloGame` GI scenarios (`SampleGlobalIlluminationValidation.ConfigureRenderSettings`): set `DdgiSimpleEnabled = true` for the Cornell/GI scenes (legacy remains one keypress away).
- `SampleInputController`: add a toggle key (pattern of existing GI toggles at `SampleInputController.cs:68-75`) flipping `DdgiSimpleEnabled` for live A/B.
- `SimpleDdgiVolumeManager.Upload` / `VulkanRenderer.cs:4865`: upload the params buffer **every frame regardless of mode**, with the ENABLED flag mirroring `EffectiveUseSimpleDdgi` (fixes frame-0 garbage + stale-enable; makes forward.frag's branch authoritative).

### Step 2 — First-activation convergence
- Zero-fill irradiance/visibility atlases at creation and on recenter (`vkCmdFillBuffer`, in `SimpleDdgiVolumeManager`).
- Track "atlas fresh" in the manager; while fresh, params carry hysteresis 0 (blend takes the new estimate outright) and the trace uses full rays; clear the flag after the first executed blend. Satisfies the plan's "<1 s convergence".

### Step 3 — Shader math fixes (`ddgi_simple_shared.glsl`, `ddgi_simple_blend.comp`, `ddgi_simple_trace.comp`)
- **Bilinear octahedral sampling** for both atlases: `uv = OctEncode(dir)*N - 0.5`, fetch 4 texels with octahedral mirror-wrap addressing (no gutter memory needed since fetches are manual), lerp. Applies to irradiance (8×8) and visibility (16×16).
- **Surface bias**: new settings `SimpleDdgiNormalBias` (default 0.1) / `SimpleDdgiViewBias` (default 0.3); sample position = `worldPos + N*normalBias + V*viewBias`; wire the existing `viewDir` param; use the biased position for grid coords and Chebyshev distances.
- **Energy**: blend constant 2 → π (atlas stores true irradiance; forward's `albedo/π` stays); update the mirror test expectation.
- **Backface weight**: `((dot+1)/2)²`, drop the 0.05/0.03 floors to a small epsilon applied once on the final combined weight (RTXGI convention).
- **Per-frame direction rotation**: CPU-generated random 3D rotation (quaternion in `GPUSimpleDdgiParams`) applied to Fibonacci directions in trace; blend is unaffected (reads directions from scratch).
- **Visibility accumulation**: when a texel's ray weight sum is ~0 this frame, keep the previous value (skip write) instead of writing the default; keep the default only for the never-initialized case (handled by Step 2's clear + hysteresis-0 frame).
- Optional parity: add `diffuseIbl` fallback to the simple gather branch in `forward.frag` mirroring the legacy compose.

### Step 4 — Diagnostics for the simple path
- Add fields (RendererDiagnostics + snapshot): simple-DDGI active flag, probe count, probes updated/frame, rays/frame, atlas bytes, GPU trace/blend microseconds; surface in the perf snapshot JSON so the next capture proves which path ran.

### Step 5 — Tests
- Update `SimpleDdgiShaderMirrorTests.cs`: π constant; new mirrors for bilinear octahedral continuity across tile seams, squared backface weight, biased-position Chebyshev, zero-weight-keeps-previous blend; source-contract pins updated for edited lines.
- `dotnet test Njulf/Njulf.sln` green.

## Verification

1. `dotnet test Njulf/Njulf.sln` — mirror/oracle/shader-embed/pass-order suites (CPU-only).
2. This environment has no GPU, so the runtime gate is a user step: run `NjulfHelloGame` Cornell scene (simple path now on by default), capture screenshot + Keypad0 diagnostic snapshot; expect smooth red/green bleed (no per-probe blobs, no dark splotches), <1 s convergence, and the new Simple* diagnostics populated; A/B key flips back to legacy for comparison.
3. Confirm the snapshot no longer shows legacy passes executing while the simple path is on (probe count within budget: Cornell grid ≪ 8 192 probes).