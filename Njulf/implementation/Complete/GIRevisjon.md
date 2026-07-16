# Simple DDGI v2: authored stationary volumes + camera-relative rings

## Context

v1 (`Simplified-SDF` @ `1b40821`) is a single scene-clamped probe volume: correct for Cornell-scale scenes, but a single fixed-spacing grid cannot cover a large world — grid dims derive from scene size and silently inflate to the 64×32×64 clamp (131k probes, ~336 MB atlases, ~47 m coverage). Requirements: developer-placed **stationary volumes** for important areas (interiors, plazas), a **camera-relative ring set** everywhere else, good quality up close, verticality (mountains/towers), far shadows later. Non-goals stay non-goals: no toroidal addressing, no free-form probe scatter, no confidence chains.

**Architecture**: one pooled probe space holding N volumes = authored volumes (world-anchored AABBs, dev-chosen spacing) + 3 camera-relative rings at doubling spacings (fixed grid dims, per-axis scene-clamped origins — the `ResolveSceneClampedOrigin` policy already shipped). Gather picks the smallest-spacing volume containing the sample with an edge fade to the next one, so authored volumes win where placed and rings cover everything else. Every new piece of GPU math lands with a CPU mirror + source-contract test (existing pattern in `SimpleDdgiShaderMirrorTests.cs` / `FarFieldClipmapOracleTests.cs`).

Defaults that make "good up close" hold everywhere: ring0 spacing 1.0 m / radius ~12 m around the camera (denser than the current 1.25 default), plus relocation (Phase 3). Authored volumes go denser (0.5–0.75 m) only where a dev decides it matters.

## Phase 1 — Multi-volume data model + gather

### 1a. Settings & authoring API (`Data/RenderSettings.cs`)
- `SimpleDdgiAuthoredVolume { Vector3 Min, Max; float Spacing; }` + `GlobalIlluminationSettings.SimpleDdgiAuthoredVolumes` (IList, capped at e.g. 16). Scenes add them in code (no editor yet).
- Ring config: `SimpleDdgiRingCount` (default 3, 0 = rings off), `SimpleDdgiRingBaseSpacing` (1.0), `SimpleDdgiRingSpacingMultiplier` (4.0), `SimpleDdgiRingGridSizeX/Y/Z` (24/12/24). Rings therefore cover radii ≈ 12 / 48 / 190 m and heights ≈ ±6 / ±24 / ±96 m; Y follows the camera only where scene Y outgrows the ring (existing per-axis logic — verticality needs no special case).
- Total probe cap `MaxSimpleDdgiTotalProbeCount` (32768) with a diagnostic warning on overflow (drop coarsest ring first, then reject newest authored volume). Bump the profile `DdgiProbeBudget` knob to match, deliberately.
- Back-compat: when no authored volumes and `SimpleDdgiRingCount == 0`, keep the v1 auto-fit volume path so existing scenes/tests behave identically. Cornell sample: either mode works — ring0 (24×12×24 @ 1.0 m ⇒ 23×11×23 m lattice) anchors to the room per axis.

### 1b. Volume table + pooled atlases (`Resources/SimpleDdgiVolumeManager.cs`)
- Build the volume table each frame, sorted by spacing ascending (ties: authored before ring): authored volumes → dims `ceil(size/spacing)+1` (clamped), origin = AABB min snapped; rings → fixed dims, origin via `ResolveSceneClampedOrigin` per ring (reuse verbatim, per-ring lattice).
- Pool probes contiguously: each volume gets `FirstProbeIndex`; keep the existing single irradiance/visibility/scratch buffers and bindless slots — only sizes change (total probes × existing strides). Resize/clear rules unchanged (`EnsureCapacity`, `ClearAtlasBuffersIfRequired`).
- Params buffer becomes header + array: `GPUSimpleDdgiHeader { volumeCount, totalProbeCount, frameIndex, flags, rayRotation, normalBias, viewBias, hysteresis, indirectIntensity, … }` + `GPUSimpleDdgiVolume { originAndSpacing, gridCountsAndFirstProbe, worldMinAndEdgeFade, worldMaxAndKind, updateStartAndCount, raysAndReserved }`. Mirror structs in `Data/GPUStructs.cs`, layout-pinned like `GPUSimpleDdgiParams` is today; `ReadSimpleDdgiHeader/Volume(i)` in `ddgi_simple_shared.glsl`.

### 1c. Gather (`ddgi_simple_shared.glsl`, `forward.frag`)
- `SampleSimpleDdgiIrradiance`: loop volumes in table order (≤ ~8, early-out); for each containing volume compute `edgeWeight = smoothstep` over distance-to-AABB-face in units of that volume's spacing (band ≈ 1.5 spacings). Take the first (finest) containing volume; if `edgeWeight < 1`, blend `(1 - edgeWeight)` from the next containing volume (typically the ring around an authored volume). Inner 8-probe trilinear × backface × Chebyshev is unchanged; probe indices offset by `FirstProbeIndex`.
- Debug view: selected-volume index (color per volume) so seams are inspectable.
- Tests (`SimpleDdgiShaderMirrorTests.cs`): selection mirror — containment, finest-wins, fade monotonic & continuous across a volume boundary (sample a line crossing an authored-volume edge into a ring; assert no weight discontinuity), ring-only fallback, struct layout lockstep.

## Phase 2 — Ring runtime: scroll + budgets

### 2a. Shift-copy on recenter (kills the recenter hitch and the 1-frame displaced gather)
- Recenter deltas are exact spacing multiples ⇒ surviving probes keep their world position at a new index. CPU computes the index remap; GPU executes it as `CmdCopyBuffer` region lists per atlas through a transient staging-sized temp buffer (old→temp full copy, temp→atlas remapped runs; runs are contiguous along X: `(X-|dx|)` probes × Y×Z rows). Newly exposed probes are marked fresh.
- Per-probe state buffer (new, u32/probe: `fresh` bit, age bits; also relocation offset in Phase 3): blend uses hysteresis 0 for fresh probes and clears the bit; replaces today's whole-volume `_atlasFresh` on recenter.
- Tests: remap mirror — bijective on the overlap, world-position invariance (`WorldPos(oldIndex, oldOrigin) == WorldPos(newIndex, newOrigin)` for every copied probe), exposed-slab enumeration; source contract pinning the copy-region builder.

### 2b. Update scheduling (replaces all-probes-every-frame — blend is already ~1.1 ms for one 2k-probe volume)
- Explicit update list: CPU fills a u32 probe-index buffer each frame (≤ `SimpleDdgiProbeUpdatesPerFrame`, new default 2048); trace/blend index through the list instead of `start+count` modulo. Priority: fresh probes → authored volumes & ring0 round-robin → coarser rings round-robin (budget split ~ 4/2/1/1).
- `SimpleDdgiPasses.cs`: dispatch sizes from list length; push constants gain the list buffer index. Diagnostics: per-volume `ProbesUpdated`, `RecenterCount`, `ScrollCount` in the snapshot's `DdgiVolumes` list (shape already exists from legacy).

## Phase 3 — Relocation + classification (coarse-ring quality; makes 4–16 m spacings usable)

- Relocation (RTXGI-style, the plan doc's Phase-4 "backface nudge"): per updated probe after trace, from scratch data compute backface-hit fraction and closest backface direction; move the probe's offset up to `0.45 × spacing` out of geometry (accumulate/decay for stability). Store packed offset in the probe-state buffer; trace origins and gather `probePos` add it; trilinear weights stay on the logical grid. Small compute pass between trace and blend (model: legacy `DdgiRelocateClassifyPass`).
- Classification: probe with all rays missing (no geometry within its ray range) → inactive: skipped by the scheduler, near-zero gather weight; re-probed every N frames via age. Recovers most of the ray budget over open terrain/sky.
- Tests: relocation mirror (synthetic hit sets → expected offset, clamp, decay), classification rules, gather-with-offset consistency.

## Phase 4 — Samples, diagnostics, verification scenes

- Cornell: add an authored volume around the room (spacing 0.75) — must look identical-or-better up close vs v1; fly-away test unchanged (room GI stable at any distance).
- New verticality scene in `SampleStressSceneBuilder`: large ground plane + tall tower + distant large boxes ("mountains"); rings only, no authored volumes.
- Enable `RenderDiagnosticsSettings.DdgiForwardEstimateCountersEnabled` in the GI validation scenarios (sparse-pixel counters are ~free) so the forward histogram (`selected volume per sample`, out-of-grid count) is populated in snapshots — this was the counter set that silently read 0 during the recenter investigation.
- Keybind: cycle the selected-volume debug view.

## Phase 5 — Later / roadmap hooks (not in this change)

- Far-field occupancy clipmap gets the same ring/clamp policy; DDA toward the sun for far shadows past shadow-map range; sky-visibility term for beyond-last-ring fallback (current fallback: `diffuseIbl`, already wired).
- Streaming: authored volumes register/unregister with a probe-pool free list; atlas compaction on unload.
- Editor gizmo/serialization for authored volumes once there is an editor.

## Budgets (defaults)

3 rings × 6 912 + Cornell-scale authored ≈ 23 k probes → atlases ≈ 59 MB (GI budget 100 MB), scratch sized by update list (2 048 × 128 rays × 32 B ≈ 8 MB), rays 262 k/frame ≈ today's Cornell load. Trace+blend stays within the 2.5 ms GI GPU budget because per-frame work is capped by the update list, not total probes.

## Verification

1. `dotnet test Njulf/Njulf.sln` — new mirrors (selection/fade, remap invariance, relocation, layouts) + updated source contracts (`FarFieldClipmapOracleTests` pins the origin/scroll builders).
2. Cornell A/B: authored-volume on/off screenshots near+far; snapshot shows authored volume selected indoors, ring0 outdoors, `FORWARD_OUT_OF_GRID = 0` from any viewpoint.
3. Tower scene: walk 200+ m — ring recenters produce no frame hitch (GPU timings flat, `ScrollCount` incrementing, `AtlasClearCount` static) and no visible GI pop; distant structures keep plausible bounce.
4. Budget metrics green with `DdgiProbeBudget` raised to the new cap.

## Risks

- Pooled `FirstProbeIndex` offsets are the main new bug surface → layout lockstep tests + the world-position-invariance scroll test cover the two ways they can silently corrupt.
- Seams at authored-volume↔ring boundaries (different spacing/convergence) → fade band sized by the finer volume's spacing; selected-volume debug view + doorway visual check.
- Gather loop cost (≤ 8 volumes, early-out) → measure `GpuForwardOpaqueMicroseconds` before/after; if needed, cull the volume list per-frame to those intersecting the view frustum's near region.
- Scroll copy through a temp buffer doubles transient bandwidth on recenter frames → bounded (one volume per frame recenters in practice; stagger rings if two trigger simultaneously).
