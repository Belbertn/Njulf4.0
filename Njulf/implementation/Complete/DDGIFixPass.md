# DDGI Fix Plan — production-ready, performance-first

## Context

Audit of the new simplified DDGI found 12 substantive issues plus a cosmetic batch (see
conversation summary). This plan fixes each to production standard, grouped into three phases so each lands buildable and
testable. GPU-side conventions: all CPU/GLSL constant pairs must stay mirrored in
`SimpleDdgiShaderMirrorTests` / `GPUStructLayoutTests` — every packing change updates both sides
and the mirror test in the same commit.

---

## Phase A — shader-only correctness & perf (no ABI change)

### A1. Take `indirectIntensity` out of the bounce feedback loop (Issue 1)
- `ddgi_simple_shared.glsl`: remove the `* p.indirectIntensity` from
  `SampleSimpleDdgiUnifiedIrradiance` (line ~1068); keep the 0..64 clamp.
- Apply intensity at final-composition call sites only: `SampleSimpleDdgiIrradiance`
  (wrapper used by `fog.comp:113`, `particle.vert:113`) multiplies by `p.indirectIntensity`;
  `forward.frag:3587` already applies it — no change there.
- Trace bounce (`ddgi_simple_trace.comp:154`) keeps calling the unscaled function → energy in
  the atlas is now physical; intensity is a one-time composition scale.

### A2. Skip environment fallback + sky-visibility cones when weight is zero (Issue 3)
- In `SampleSimpleDdgiUnifiedIrradiance`: compute
  `fallbackWeight = (1.0 - ownership) * p.environmentFallbackIntensity` first; only when
  `> 0.0001` compute `biasedWorldPos`, sample the irradiance cubemap, and run
  `EstimateFarFieldSkyVisibility` (3 cone marches). Move the `biasedWorldPos` computation
  inside the branch (it is only used there).
- Wins: removes 3 far-field cone marches + a cubemap sample per fully-owned probe-ray hit,
  fog froxel, and particle vertex.

### A3. Fresh probes must never keep stale texels; fix bootstrap moments (Issues 2 + 7)
- `ddgi_simple_blend.comp`: derive `bool freshUpdate = (update.flags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0`
  in `main` and pass it into the four texel functions.
- Visibility (`BlendVisibilityTexel:244`, `BlendReducedVisibilityTexel:393`): change the
  keep-previous branch to `else if (previous.z > 0.5 && !freshUpdate) return;` — fresh updates
  fall through and overwrite with bootstrap moments (hysteresis is already 0 for fresh).
- Irradiance (`BlendIrradianceTexel:192`): same `!freshUpdate` guard; for fresh + zero weight
  write `vec4(0.0)` (invalid, w=0) so the gather's `irradiance.w > 0.5` support test excludes
  the direction instead of trusting stale data.
- Bootstrap moments (Issue 7): replace `(4·spacing, 16·spacing²)` with
  `mean = spacing`, `mean2 = mean² + (0.5·spacing)²` — soft partial visibility instead of
  "fully unoccluded to 4 cells". Named constants at the top of the file; tune in verification.

### A4. Smooth the ownership switch (Issue 6)
- `SimpleDdgiRadiometricOwnership` (`ddgi_simple_shared.glsl:719`): replace the 1e-6 step with
  `clamp(spatialCoverage,0,1) * smoothstep(0.0, SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP, validSupport)`
  with `SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP = 0.15`. Removes one-frame popping and stops a
  single barely-valid probe from claiming a whole cell; environment fallback ramps in instead.
- Forward, fog, particles, and trace all route through this one function — single change.

### A5. Shader cosmetic batch (Issue 13, shader half)
- `SimpleDdgiVolumeQualityCascade`: literal `2u` → `SIMPLE_DDGI_VOLUME_KIND_RING`.
- `BlendIrradianceTexel`: count `DDGI_INVESTIGATION_BLEND_ZERO_RAY_WEIGHT_PROBE_COUNTER` once,
  before branching (delete the second bump at :205).
- `ReadSimpleDdgiParams`: clamp `irradianceTexels` to `SIMPLE_DDGI_IRRADIANCE_TEXELS` and
  `visibilityTexels` to `SIMPLE_DDGI_VISIBILITY_TEXELS` (upper bound) so a bad upload can
  never index shared arrays out of bounds.
- Trace bounce offset: `surfaceNormal * max(0.03, volume.spacing * 0.02)` (spacing-relative).

---

## Phase B — paired CPU+GPU protocol changes (one commit each, mirrors updated)

### B1. Bias-by-one packing for max-shaded-lights (Issue 10)
- CPU `PackUpdateFlags` (`SimpleDdgiVolumeManager.cs:1547`): pack
  `Clamp(MaxShadedLights, 0, 62) + 1` (6 bits, 1..63).
- GLSL `SimpleDdgiUpdateMaxShadedLights`: `packed == 0 ? fallback : min(packed - 1u, fallback)`.
- Now packed-zero means "unset → default" exactly like the ray-count field; a zeroed queue
  entry can no longer silently disable direct lighting.

### B2. Bit-exact frame index and flags in the params header (Issue 12)
- CPU: write `HysteresisFrameAndFlags.Y/.Z` via `BitConverter.UInt32BitsToSingle(frameIndex/flags)`
  (also in `CreateDisabledParams`); GLSL `ReadSimpleDdgiParams`: `floatBitsToUint(hysteresis.y/.z)`.
- Removes the 2^24 quantization cliff for long-soak sessions and frees flag bits above 23.
- Update `SimpleDdgiShaderMirrorTests` / `GlobalIlluminationDefaultsTests` packing mirrors.

### B3. Fade the emissive proxy by probe-field ownership (Issue 4)
- `ddgi_simple_shared.glsl`: add overload
  `SampleSimpleDdgiUnifiedIrradiance(..., out float ownershipOut)` (existing signature wraps it).
- `ddgi_simple_trace.comp`: capture ownership from the bounce gather and scale:
  `radiance = surfaceEmissive + emissiveProxyDiffuse * (1.0 - ownership) + directDiffuse + bounce;`
- The proxy now fills only the transport the probe field doesn't represent — fast response in
  fresh regions, no double counting once converged. Zero extra gathers (reuses the bounce one).

### B4. Relocation target in the correct frame of reference (Issue 8)
- `ddgi_simple_relocate_classify.comp:155`: back-face evidence is measured from
  `logical + previous.relocation`, so target from there too:
  `targetRelocation = previous.relocation + direction * targetDistance;`
  (no-backface decay branch still targets `vec3(0.0)`; final 0.45·spacing length clamp stands).

---

## Phase C — CPU manager work

### C1. Sparse probe-state uploads (Issue 5)
- `SimpleDdgiVolumeManager`: collect dirty slots in `MarkProbeFresh` (when
  `shouldAdvanceGeneration`) into a reused `List<int> _probeStateDirtySlots`.
- `UploadProbeState`: full upload only on buffer (re)create / first frame / capacity change.
  Otherwise sort dirty slots, coalesce consecutive indices into runs, stage the packed structs
  contiguously, and issue one `BufferCopy` per run (scroll invalidations are plane-shaped, so
  runs are long). If run count exceeds a cap (256), fall back to full upload.
- Non-invalidated slots are never overwritten → GPU relocation/activeWeight/EMA survive
  scrolls, and per-scroll upload bandwidth drops from `probeCount·32B` to `dirty·32B`.
- Add a `GpuBufferUploader.UploadRunsToBuffer` helper (staging ring + multi-region copy + one
  barrier) rather than open-coding in the manager.

### C2. Delete the dead legacy scroll copier (Issue 9)
- Remove the non-toroidal branch of `PreserveScrolledAtlasData`, `CopyScrolledAtlasRuns`,
  `BuildScrollCopyRunsForTest`, `_copyTempBuffer/_copyTempBytes` + `EnsureTransferBuffer`
  usage, and their tests in `SimpleDdgiVolumeManagerTests`. Fix the stale comment in
  `MarkFreshForNewOrScrolledProbes` (non-toroidal recenter = clear + rebootstrap, full stop).

### C3. Importance-based direct-light selection at hits (Issue 11)
- Shader-side top-N (no CPU sort, bounded rays): in `EvaluateDirectDiffuseRadianceAtHit`
  (`ddgi_hit_shading.glsl`), scan up to `min(pc.LightCount, 64)` lights computing the cheap
  unshadowed estimate (`color·intensity·attenuation·nDotL` — ALU only, no rays), keep the
  top `DDGI_HIT_MAX_SHADED_LIGHTS` (≤ 8) in a small insertion array, then shade + trace shadow
  rays for the winners only.
- Ray budget is unchanged (still N rays/hit); selection cost is ALU on an already
  memory-bound pass. Directional lights bypass the ranking (always considered first).

### C4. CPU cosmetic batch (Issue 13, remainder)
- Rename `SIMPLE_DDGI_PROBE_FLAG_RAY_COUNT_*` → `SIMPLE_DDGI_UPDATE_RAY_COUNT_*` (GLSL) and
  align CPU comments; no bit changes.
- Drop the never-consumed `updateStartProbe` parse from the GLSL param/volume structs (CPU
  keeps writing the slot as reserved — no ABI shift).
