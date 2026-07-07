# SDF Turn Artifact — Round-2 Diagnosis: Thin-Feature Dropout in Coarse Cascades

## Context

SDFFixes77 asked to characterize the residual turn artifact. Round-1 snapshots showed a teal band (total SDF miss) across mid-distance geometry right after a 180° turn. Round-2 snapshots (same camera position, four views: FullSlice + single-cascade 0/1/2, with the new per-cascade/rolling diagnostics) pin the mechanism. The user identified the gap in the right wall (FullSlice shot) as where the issue begins.

## Diagnosis (evidence-based)

**The artifact is representational, not stale state: thin, non-uniformly scaled geometry drops out of the coarse cascades, and marginal thin geometry in the fine cascade produces holes/melted surfaces. Nothing is wrong with scrolling, invalidation, or rebuild.**

Evidence chain:

1. **The whole scene is 40 instances of ONE mesh** (`MeshSdfTotalBakedMeshCount: 1`, `MeshSdfSkippedInstanceSdfCount: 0`, texture = 549,250 B = 65³ × R16), non-uniformly scaled into walls/floor/frames. Thin walls/frames = the mesh crushed to ~0.1–0.3 m on one axis.
2. **Thin-wall dropout is inevitable in coarse cascades**: a ~0.2–0.3 m wall sampled at cascade-2/3 voxels (0.5/1.0 m) has voxel centers on both sides reading positive — no zero crossing — so `IntersectTrilinearCell` (`global_sdf.glsl:371`) can never hit it. The wall is *invisible to the trace* even though the single-cascade debug view shows friendly green ("near zero") — the view colors |d| and cannot distinguish "near zero, never crosses" from "crosses". That is exactly the round-1 teal band: beyond cascade 0/1 coverage, only cascades 2/3 remain, and thin geometry doesn't exist in them.
3. **The wall-gap/doorway is the seed** because it's built from the thinnest, most-downscaled instances: cascade 0 (0.125 m voxels) renders them melted (aliasing of a downscaled instance + trilinear); cascade 1 is marginal; cascade ≥ 2 has nothing. The white takeover past ~12 m in FullSlice is the normal c0→c1 handoff (c0 half-extent), not a bug.
4. **The turn makes it dramatic, movement fixes it**: after a 180° turn you face geometry that only coarse cascades cover; walking forward brings it into c0/c1 range and the teal clears. Matches SDFFixes77 candidate #1 amplified by thin-feature dropout — NOT #2 (hysteresis lag is ≤ ~1.25 bricks, too small) and NOT a scroll-state bug (per-cascade scroll counters clean; last slab rebuild wrote 432 empty + 144 candidate bricks = plausible air/geometry split).
5. Checked and cleared: instance scale math (`MeshSdfManager.ComputeLocalToWorldAxisScales` is correct; the GPU field `WorldToLocalAxisScale` is just misnamed — it holds local-to-world), ring addressing (sampler UVW shift ≡ per-brick modulo), empty-brick `safeBound` writes (conservative-correct).

Secondary consequence worth stating in the doc: DDGI uses `SdfBackendFirstCascade: 2` — GI rays only ever see the cascades where thin walls don't exist, so GI leaks through exactly these walls.

## Implementation plan

### Phase 1 — write `Njulf/implementation/SDFFixes88.md` with this diagnosis + the fix

**Fix: thin-feature preservation when composing the global SDF** (`global_sdf_update.comp`, main change):
- For each voxel, when the sampled candidate distance indicates the surface passes within the voxel's cell but no sign change would survive quantization (|d| < voxel size on both sides), clamp the stored distance so a zero crossing is preserved — the standard "surface bias / minimum representable thickness = 1 voxel" approach (Lumen-style): `if (abs(d) < voxelSize * K) d = min(d, contact bias)` pushing near-surface positive samples down so thin geometry keeps a negative core at every cascade's resolution.
- Optional trace-side backstop (`global_sdf.glsl`): in the near-band DDA, if consecutive samples bracket a local minimum below ~0.5 voxel without a sign flip, synthesize a hit (catches residual +→+ tunneling at grazing angles — the teal panel on the near-left wall).

**Diagnostic improvements** (small, same round):
- Single-cascade debug views: split sign near zero (positive-near vs negative-near different colors) so dropout (no negative core) is directly visible (`forward.frag` `GlobalSdfSingleCascadeDebugColor`).
- Lengthen the GPU rolling-max window (~120 frames missed the last brick update at snapshot time by 4 frames; use ~600) and remove the dead `GpuGlobalSdfMipMicroseconds` metric (no mip pass exists; trace uses lod 0 only).
- Rename `WorldToLocalAxisScale` → `LocalToWorldAxisScale` (GPUStructs.cs + shader) to prevent a future real inversion bug.

### Phase 2 — verification

1. Build + existing test suites (`GlobalSdfManagerTests`, `ShaderBuildTests`).
2. User capture: repeat the 180°-turn repro. Expect: teal band gone (coarse cascades now hit thin walls); the wall-gap area keeps its melted look in c0 (separate, cosmetic under-resolution issue — note in doc as future work: per-instance minimum bake resolution for heavily downscaled instances).
3. Check GI side: with thin walls present in cascade ≥ 2, DDGI leak-through at those walls should visibly reduce.

Commit and push to `claude/analyze-snapshots-issues-h4i1e8`.