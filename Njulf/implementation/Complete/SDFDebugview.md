# Fix: DDGI SDF debug view looks identical to the full SDF debug view

## Context

Commit `ba89fa3` added two SDF debug views: `GlobalSdfSlice` (magenta border, meant to show only the SDF cascades the DDGI backend actually uses, `cascade >= SdfBackendFirstCascade`, default 2) and `GlobalSdfFullSlice` (yellow border, all cascades from 0). Both currently render identically.

## Root cause: frame-ordering bug — the forward pass reads the value before it is written

The shader gets `SdfBackendFirstCascade` via push constants: `ForwardPlusPass` packs `sceneData.GlobalSdfBackendFirstCascade` into `DiagnosticFlags` bits 8–15 (`ForwardPlusPass.cs:351,508`).

But that sceneData field is only populated inside **`GlobalSdfPass.Execute`** (`GlobalSdfPasses.cs:123`), and the production pass order (`ProductionRenderPipelineDeclaration.cs:29,35`) records **`ForwardPlusPass` (index 29) before `GlobalSdfPass` (index 35)**. `sceneData` is rebuilt fresh every frame (`VulkanRenderer.cs:1184`, default 0), so at the moment the forward pass packs its push constants the field is always **0**.

With `firstCascade = 0` in the shader:
- `DdgiCascadeUsesSdfBackend()` (`forward.frag:250`) is always true → the DDGI-view gate never blacks out near-camera pixels, and
- `GlobalSdfRaymarchDebugColor(..., 0)` marches from cascade 0 — exactly what the full view does.

The two views therefore produce **pixel-identical** output. (Side effect: `DdgiRayBackendHeatmap` regressed too — it used to hardcode `>= 1`, now reads `>= 0`, so every pixel shows "SDF eligible".)

Note: `TransparentForwardPass`/`WeightedTransparentPass` record *after* `GlobalSdfPass`, so transparents would get 2 while opaques get 0 — an inconsistency the fix below also removes.

## Fix

Populate the field during per-frame scene-data setup, before any pass records:

1. **`Njulf/Njulf.Rendering/VulkanRenderer.cs`** (~line 1204, next to `sceneData.FrameIndex = ...` right after `_sceneDataBuilder.Build(...)`):
   ```csharp
   sceneData.GlobalSdfBackendFirstCascade = Settings.GlobalIllumination.SdfBackendFirstCascade;
   ```
2. **`Njulf/Njulf.Rendering/Pipeline/GlobalSdfPasses.cs:123`** — delete the now-redundant late assignment so there is a single source of truth (no test depends on it; the diagnostics readback at `VulkanRenderer.cs:3748` keeps its `giUsesDdgi` gate and is unaffected).

No shader or push-constant layout changes needed — the plumbing in `GPUForwardPushConstants.PackDiagnosticFlags` and `forward.frag` is already correct.

## Expected result after fix

- `GlobalSdfSlice` (magenta): near-camera fragments whose DDGI gather cascade is 0–1 render black; the rest raymarches only cascades ≥ 2 (coarser cascade tints).
- `GlobalSdfFullSlice` (yellow): unchanged — full march from cascade 0 with fine near-camera detail.
- `DdgiRayBackendHeatmap`: blue (ray-query) near camera, green (SDF) beyond the cascade-2 boundary, restoring/improving the pre-commit behavior.

## Verification

1. `dotnet test` — run existing suites (`ShaderBuildTests`, `GPUStructLayoutTests`, `RendererDiagnosticsTests`) to confirm nothing regresses.
2. Optionally extend `ForwardPlusPassTests`/`RendererDiagnosticsTests` with an assertion that `sceneData.GlobalSdfBackendFirstCascade == 2` after frame setup (before pass execution), guarding against the ordering regression.
3. Runtime check (user-side, needs GPU): toggle between the two views — `GlobalSdfSlice` should now show black near the camera plus coarse-cascade tints in the distance, clearly distinct from `GlobalSdfFullSlice`; the ray-backend heatmap should show blue near / green far.