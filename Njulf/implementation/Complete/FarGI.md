# Findings: GI works up close, disappears at distance (branch `Simplified-SDF` @ `81e6a59`)

Diagnosis only — user will implement the fixes.

## Context

`6fb545a` + `81e6a59` enabled the simple DDGI path and fixed the earlier shader defects (π energy constant, bilinear octahedral sampling with mirror wrap, normal/view surface bias, squared backface weight, per-frame 3D ray rotation, atlas clear + hysteresis-0 first frame, per-frame authoritative params upload, diffuse-IBL parity, diagnostics). The four new snapshots confirm the simple path now runs (legacy skipped with `simple-ddgi-active`, 1944 probes, 248 832 rays/frame) and GI looks right up close — but vanishes as the camera moves away.

## Issue 1 (root cause): probe volume follows the camera off the scene

`SimpleDdgiVolumeManager.cs:128` sizes the lattice from **scene bounds** (Cornell ⇒ 18×6×18 = 1944 probes, lattice ≈ 12.75 × 3.75 × 12.75 m) but resolves its origin with `ResolveCameraFollowingOrigin` (`:345-387`):

- `ShouldRecenter` (`:378`) triggers whenever the **camera** leaves the middle half of the lattice — for Cornell that inner box is only ≈ 6.4 × 1.9 × 6.4 m. Merely flying up or backing away from the room triggers it.
- The new origin is `SnapOrigin(cameraPosition - latticeSize * 0.5)` (`:375`) — the volume is centered on the **camera**, in empty air, and the room leaves probe coverage.
- Out-of-volume gather returns black: corner probes outside the grid are skipped and zero total weight returns `vec3(0)` (`ddgi_simple_shared.glsl:282, 302-304`).

Snapshot evidence: `SimpleDdgiRecenterCount` 12 (first capture) / 3 (later run); trace hit rate 1.3 % (3 257 hits / 248 832 rays — almost all probes floating in empty space); debug views from the far viewpoint show gather blend weight = 0 (view 38, black), gather fallback sentinel (view 22, magenta), support coverage empty (view 31, red).

A GI probe volume must cover the **geometry**, not the camera — irradiance is view-independent. Walking back toward the room recenters the volume back over it, which is exactly the "works up close" behavior observed.

**Fix direction (revised after user input — target is camera-relative GI that holds up near AND far, mountains/tall buildings, far shadows on roadmap):**

1. **Nested camera-centered rings, not one volume**: 3–4 instances of the existing `SimpleDdgiVolumeManager` at doubling spacings (e.g. 0.75 / 3 / 12 / 48 m → radii ≈ 9 / 36 / 144 / 576 m), each with the existing snap-recenter (threshold scales with lattice, so coarse rings recenter rarely). Perspective foreshortening makes coarse spacing acceptable at distance. This is NOT the legacy toroidal clipmap: no toroidal addressing, no dirty bricks — whole volumes, snap recenter, full mirror-testable.
2. **Shift-copy on recenter**: snap deltas are exact spacing multiples, so add a small compute pass that remaps overlapping probes to their new indices on recenter and marks only the newly exposed slabs for refresh. Kills both the 1-frame displaced gather (Issue 2) and the full 250k-ray retrace.
3. **Per-probe relocation (user's question — yes, adopt)**: RTXGI-style, driven by the trace results already in scratch (backface-hit fraction + closest-backface direction → offset probe up to ~0.45×spacing out of geometry; store per-probe offset; use it in trace origin and gather probePos, trilinear weights stay on the logical grid). Cheap, procedural, already earmarked as Phase 4 in the plan doc. Essential for coarse rings whose probes land inside mountains/buildings. Complement with **classification** (probes fully in geometry or in empty sky → inactive, skip their rays).
4. **Per-axis scene clamp still applies**: a ring whose lattice covers the whole scene on an axis anchors to the scene on that axis (Cornell then never recenters); a following axis clamps to scene bounds. Same fix in `FarFieldClipmapManager.cs:233-278`.
5. **Beyond the coarsest ring**: sky/environment fallback (already wired via `diffuseIbl`); later upgraded by the far-field occupancy clipmap as a sky-visibility term — the same clipmap is the natural substrate for roadmap far shadows (DDA march toward the sun past shadow-map range).
6. Budgets: 4 rings at ~20×10×20 ≈ 16k probes total exceeds the profile's `DdgiProbeBudget` 8192 — bump the knob deliberately or shrink ring grids; update budget round-robins per ring (drop the current all-probes-every-frame default once rings exist — blend is already 1.1 ms for one small volume).

Note the source-contract test `FarFieldClipmapOracleTests.cs:151-186` pins the current camera-centered strings and must be updated with whatever policy lands.

## Issue 2: one-frame wrong gather on every recenter

Since `81e6a59` a recenter preserves the atlas and relies on a hysteresis-0 full retrace (`SimpleDdgiVolumeManager.cs:166-177`, sound idea). But pass order is ForwardPlus → SimpleDdgiTrace → SimpleDdgiBlend, and the params (new origin) are uploaded at frame start — so on the recenter frame the forward gather reads the **old atlas contents through the new origin mapping**: every probe's stored irradiance is displaced by the recenter delta for one frame (visible flicker per recenter). With Issue 1 fixed this becomes rare (large roaming scenes only), but worth handling — e.g. keep gathering with the previous origin until the post-recenter blend has landed, or accept the 1-frame pop.

## Issue 3: forward-side investigation counters are silently dead

All the new forward counters (`DdgiForwardSimplePathSampleCount`, `SimpleDdgiZero/NonzeroIrradianceSampleCount`, `SimpleDdgiAverageSampledIrradianceLuminance`, `…LowVisibility…`, and critically `DDGI_INVESTIGATION_FORWARD_OUT_OF_GRID_SAMPLE_COUNTER`) are gated by `DdgiForwardEstimateDiagnosticPixel()` → `pc.Push.DiagnosticFlags & 1` (`forward.frag:234-253`), which is only set when `RenderDiagnosticsSettings.DdgiForwardEstimateCountersEnabled` is true (`ForwardPlusPass.cs:541-551`). The Cornell sample never enables it, so every forward counter reads 0 in all four snapshots — the out-of-grid counter would have pointed straight at Issue 1. Either enable that setting in the GI validation scenarios (it's already 1/16×16 sparse-pixel sampled, so cost is negligible) or gate the investigation counters on their own flag that the sample turns on.

## Minor observations (no action required for the bug)

- **Blend cost**: `GpuSimpleDdgiBlendMicroseconds ≈ 1 130` — every texel loops all 128 rays, and with `SimpleDdgiProbeUpdatesPerFrame = 0` all 1 944 probes re-blend every frame (~80 M ray-texel ops). Fine for dev; will need the round-robin budget or shared-memory ray caching before bigger scenes.
- Trace + blend ≈ 1.6 ms of the 2.5 ms GI budget at 1 944 probes, dominated by the always-full refresh — same knob as above.
- `SimpleDdgiFramesSinceLastRecenter` reports 0 when no recenter ever happened (`int.MaxValue → 0` mapping) — reads as "recentered this frame"; cosmetic.
- The first capture (08:54) shows `RecenterCount = 12` with `AtlasClearCount = 1` — consistent with preserve-on-recenter; no bug, just confirming the diagnostics work as designed.

## Suggested verification once fixed

1. Cornell scene: `SimpleDdgiRecenterCount` must stay 0 regardless of camera path; GI on the room identical near and far; trace hit rate should jump well above 1.3 % (probes actually inside/around the room).
2. Enable `DdgiForwardEstimateCountersEnabled` in the GI scenarios and confirm `DdgiForwardSimplePathSampleCount > 0` and `FORWARD_OUT_OF_GRID_SAMPLE = 0` from any viewpoint.
3. `dotnet test Njulf/Njulf.sln` after updating the `FarFieldClipmapOracleTests` source-contract pins; ideally add behavioral tests for the origin resolver (scene-fits ⇒ origin independent of camera; scene-larger ⇒ follow clamped inside scene bounds) — `InternalsVisibleTo("Njulf.Tests")` already exists for this.