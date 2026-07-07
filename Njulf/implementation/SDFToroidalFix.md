# Global SDF — Root cause found: toroidal wrap-seam in hardware-filtered SDF sampling

## Context / how we got here

The scene's global SDF corrupts (phantom geometry in air, holes) as soon as the camera translates, and stays corrupted — including back at spawn. Three earlier speculative fixes (thin-feature preservation etc.) were reverted as wrong-track. Two isolation experiments (toggles the user built) settled it:

- `DisableToroidalScroll = true` (ringOffset pinned 0 + full rebake): **settles correct.**
- `ForceFullSdfRebakeOnScroll = true` (ringOffset≠0 + full rebake): **settles broken.**

The only difference is the ring offset, and full rebake writes correct content into every brick — so the defect is on the **read side, specific to ringOffset≠0**.

## Root cause (confirmed)

`SampleGlobalSdfCascade` (`Njulf/Njulf.Shaders/global_sdf.glsl:70`) reads the clipmap with the **hardware Linear + Repeat sampler**: `uvw = (clampedLogicalVoxel + ringOffset*8) / res`. The global SDF texture is a **toroidal ring buffer** — physical texel `t` holds logical voxel `(t − ringOffset*8) mod res`. Hardware trilinear filtering interpolates between *physically adjacent* texels, but across the ring-buffer wrap those are **not logically adjacent**: logical `0` (near face of the cascade box) and logical `res−1` (far face) map to physically adjacent texels around `ringOffset*8`. So a linear tap there blends two world-far-apart distances → garbage on a thin world-plane, one per axis.

- ringOffset = 0 ⇒ that seam sits at the box edge (uvw ≈ 0/1), never sampled in the interior (and `clamp(0.5,res−0.5)` guards it) ⇒ clean. Explains Test 1.
- ringOffset ≠ 0 ⇒ seam moves into the interior of the traced volume ⇒ DDGI rays and the coarse SDF march cross it, producing false hits/tunneling over a large visible region; it sweeps while moving and persists when stopped; hysteresis leaves residual ringOffset at "spawn" so spawn corrupts too. Explains Test 3 + every prior symptom.

The **fine** trace path already avoids this: `FetchGlobalSdfCellCorners` / `GlobalSdfFetchLogicalVoxelDistanceMeters` (`global_sdf.glsl:84-106`) fetch each corner with per-voxel wrapped `texelFetch` (`GlobalSdfLogicalVoxelToPhysicalTexel`). Only `SampleGlobalSdfCascade` (used by the coarse march at `global_sdf.glsl:342`, by `EstimateGlobalSdfNormal`, by the DDGI gather at `ddgi_update_shared.glsl:2142`, and by the debug views) uses the seam-unsafe hardware sampler.

## The fix

Rewrite `SampleGlobalSdfCascade` to do **manual trilinear filtering** with per-corner wrapped fetches instead of the hardware sampler — reusing the helpers that already exist:

```glsl
GlobalSdfSample SampleGlobalSdfCascade(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex)
{
    vec3 logicalVoxelFloat = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
    if (any(lessThan(logicalVoxelFloat, vec3(0.0))) ||
        any(greaterThanEqual(logicalVoxelFloat, vec3(float(cascade.Resolution)))))
        return GlobalSdfSample(1.0e20, cascadeIndex, false);

    vec3 p = logicalVoxelFloat - vec3(0.5);           // sample at voxel centers
    ivec3 cell = ivec3(floor(p));
    vec3 f = p - vec3(cell);
    GlobalSdfCellCorners corners = FetchGlobalSdfCellCorners(cell, cascade);  // per-corner wrapped texelFetch, decoded to meters
    float d = EvaluateGlobalSdfTrilinear(corners, f);
    return GlobalSdfSample(d, cascadeIndex, true);
}
```

`FetchGlobalSdfCellCorners` already clamps the cell to `[0, res-2]` (matching the old edge behavior) and returns distances in meters, and `EvaluateGlobalSdfTrilinear` interpolates them — so this is a drop-in that never interpolates across the physical seam. No change needed to the writer, the metadata, the sampler, or the ring math (all verified correct). `EstimateGlobalSdfNormal` automatically becomes seam-safe since it calls `SampleGlobalSdfCascade`.

Optional follow-ups (separate, only if a residual remains after the core fix):
- The green/cyan GI blobs in the lit view are downstream (DDGI probe/surface-cache) light-leak, not the SDF; revisit after the seam fix since much of it is fed by the corrupted SDF trace.
- Once fixed, the `ForceFullSdfRebakeOnScroll` / `DisableToroidalScroll` debug toggles can stay as diagnostics or be removed; keep the physical-brick debug view and the scroll-desync assert.

## Verification

1. Build + `dotnet test` (`ShaderBuildTests` compiles the shader; `GlobalSdfManagerTests`, `RendererDiagnosticsTests`).
2. Both toggles **off** (real toroidal path). Repro: walk out ~10 m on an axis and back; capture `GlobalSdfFullSlice` at spawn and mid-corridor. Expect: no phantom geometry/holes while moving or stopped, spawn clean after the round trip, `DdgiSurfaceCacheFallbackPercent` < ~1%.
3. Confirm the interior seam-plane is gone: strafe slowly and watch for a thin moving garbage plane — it should not appear.
4. Push to `Simplified`.