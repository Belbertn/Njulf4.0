# Snapshot Analysis: Residual Turn Artifact (SDFFixes77 follow-up) + Diagnostics Gaps

## Context

SDFFixes77 removed the empty-skip (confirmed root cause of the misalignment bug) and asked for a capture round to characterize the **residual turn artifact**: 2–3 snapshots (mid-turn / at-issue / settled) to decide between three candidate mechanisms — #1 cascade handoff pop at a fixed world distance, #2 scroll-hysteresis lag after reversal, #3 grazing-angle dither. The user captured exactly that. This plan records the diagnosis and proposes the follow-up work.

## Snapshot analysis (the deliverable)

**How to read the `GlobalSdfFullSlice` view** (decoded from `forward.frag:2901` `GlobalSdfRaymarchDebugColor` + `MeshletDebugColor` hash):
- The view raymarches the global SDF from the camera toward every visible pixel's fragment.
- **Hit** → cascade tint, brightness falls off with hit distance (near-black past ~14 m because `maxDistance = max(visD+slack, 16)`). Tints: **cascade 0 = brick-red** (129,33,19), **cascade 1 = white** (221,233,241), **cascade 2 = pink** (209,94,126), **cascade 3 = green** (62,213,69).
- **Miss** (ray reached the fragment without a hit in ANY cascade) → fall-through color from the nearest signed sample: green = near surface, blue = positive, red = inside. Blends produce the **pale teal**.

**Snapshot 1, mid-turn** (yaw ≈ −91°, Z −22.1): normal. Near geometry brick-red (cascade-0 hits), distant geometry dark navy (far hits). No anomaly.

**Snapshot 2, at-issue** (yaw ≈ 180°, Z −14.9): the **teal band across the whole mid-distance is an SDF hole, not a cascade tint** — rays pass through the fence/far floor beyond the fine-cascade box with no hit in cascades 1–3. Dashed dark streaks inside the band = scattered rows that still hit. Bright-green silhouette fringes = expected near-surface edges. This exact frame also executed a coarse-cascade scroll: 1 cell, a full 576-brick slab (24²) invalidated **and rebuilt the same frame** (budget auto-raised 512→1024 — the priority-reservation mechanism working as designed).

**Snapshot 3, settled** (+6.4 m, stationary): teal gone. The cascade-0→1 handoff is visible in its benign form: a bright **white** band on the floor at a fixed ~12 m (= cascade-0 half-extent, voxel 0.125 × 192) where cascade 1's inflated floor is hit early, plus a white ceiling wedge at grazing angle (cascade-0 step exhaustion → cascade 1 catches). Classic clipmap LOD pop.

**Verdict vs the SDFFixes77 candidates:**
- **#1 confirmed** — boundary-anchored artifacts at the fixed ~12 m cascade-0 edge (snapshot 3).
- **The turn-moment artifact is worse than a handoff pop**: all coarse cascades *missed*. Two mechanisms remain indistinguishable from these captures:
  - (a) thin-feature dropout in coarse cascades (0.5/1 m voxels vs fence slats) combined with hysteresis-lagged fine coverage right after reversal (#1+#2 compounding), or
  - (b) stale/erased coarse-cascade content in the previously-behind direction (a state bug, same family as the empty-skip).
- **#3** present only as cosmetic fringes — deprioritize.

The captures can't split (a) from (b) because of the diagnostics gaps below.

## Diagnostics issues found (these blocked a conclusive diagnosis)

1. **GPU counters/timers are frame-latent**: the scroll frame reports `GpuGlobalSdfBrickMicroseconds: 0` and `BricksWrittenEmpty/WithCandidates: 0` despite dispatching 576 bricks — the snapshot reads the *previous idle frame's* readback. SDFFixes77's "verify sub-ms scroll cost" is unverifiable via snapshots today.
2. **No per-cascade scroll attribution** — only cascade 0 has dedicated counters (`GlobalSdfManager.cs:99-104`); can't tell which cascade scrolled the 576-brick slab.
3. **`MeshSdf*` snapshot stats are stale zeros**: when `PendingBakeCount == 0`, `MeshSdfBakePass.ShouldExecute` skips and `Execute` (which copies `MeshSdfTextureBytes`, `MeshSdfTotalBakedMeshCount`, … at `MeshSdfBakePass.cs:70-81`) never runs — the snapshot claims 0 baked SDFs / 0 bytes while 40 mesh SDFs are demonstrably active.
4. **`GlobalSdfDirtyBrickBacklog` is sampled pre-consumption** (`GlobalSdfManager.cs:97`): "backlog 576 + updated 576" on one frame reads as falling behind when it isn't.
5. **Debug-view dynamic range hides the far field**: everything hit past ~14 m renders near-black, hiding cascade identity exactly where handoff issues live.

## Implementation plan

### Phase A — diagnostics fixes (make the next capture round conclusive)

Files: `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs`, `Pipeline/GlobalSdfPasses.cs`, `Pipeline/MeshSdfBakePass.cs`, `Data/SceneRenderingData.cs`, `Data/RendererDiagnostics.cs`, `NjulfHelloGame/SampleDiagnosticsReporter.cs` (follow the existing field-threading pattern manager → sceneData → diagnostics → snapshot JSON).

1. Per-cascade scroll diagnostics: replace the cascade-0-only specials with small per-cascade arrays (scroll delta, invalidated bricks, dirty backlog) in the snapshot payload.
2. Rolling-max (last ~120 frames) variants of `GpuGlobalSdfBrickMicroseconds`, `GpuGlobalSdfMicroseconds`, and last-nonzero `BricksWritten*` with the frame index they came from — so a snapshot on any frame shows the most recent real update cost/result.
3. Fix `MeshSdf*` stat staleness: move the steady-state copies (`MeshSdfTextureBytes`, `MeshSdfBufferBytes`, `MeshSdfTotalBakedMeshCount`, `MeshSdfPendingBakeCount`, …) into `MarkSkipped` or per-frame scene-data setup so they're populated on skip frames too.
4. Rename/resample backlog: report `GlobalSdfDirtyBrickBacklogBefore` and `...After` (after job consumption), or just sample post-consumption.

### Phase B — write `Njulf/implementation/SDFFixes88.md` (round doc)

Contents: the snapshot diagnosis above, plus the disambiguation procedure for mechanism (a) vs (b):
- Reproduce the turn artifact; at the moment it's visible, flip through the existing single-cascade views (`GlobalSdfCascade1/2/3`, `forward.frag:2966`) — if the coarse cascades show a near-zero band at the fence/far wall, content exists → trace/handoff problem (a); if the field there is uniformly positive/empty → content missing → state bug (b).
- Record temporal behavior (stable-until-moved vs flickers-with-movement vs fades-standing-still) — distinguishes coverage gap / scroll transient / deferred rebuild.
- Decision tree for the fix: (a) → conservative thin-feature handling in the brick writer + cascade-boundary fade band, and/or tune `ScrollHysteresisBrickFraction` 0.25→0.1; (b) → audit the scroll-slab rebuild path (ring-offset addressing of rebuilt bricks).

### Phase C — verification

1. `dotnet build` + `dotnet test` (`GlobalSdfManagerTests`, `RendererDiagnosticsTests`, `ShaderBuildTests` — no shader changes expected in Phase A).
2. Commit + push to `claude/analyze-snapshots-issues-h4i1e8`.
3. User-side (needs GPU): re-run the turn repro, capture one snapshot at the artifact moment — the new fields should pin which cascade scrolled, whether the slab bricks actually wrote content, and the real GPU cost; the single-cascade views settle (a) vs (b).