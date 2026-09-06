# Reflection darkening when the camera stops

## Cause and correction

Sponza uses `HybridWithPlanar` even when no automatic planar reflection is
active. On stationary tiles, `hybrid_reflection_classify.comp` copies an
already shaded reflection into the raw input and marks it as a resolution
skip. SSR and DDGI correctly skip those receivers. The resolve pass excluded
all planar-capable modes from its early return for this carried value, then
treated outgoing radiance as incident radiance and applied the BRDF again.
Repeating that operation attenuated the reflection every stationary frame.
Camera movement forced fresh shading and restored the brighter curtain detail.

The resolve now preserves the already shaded value in every hybrid mode.
Classification rejects all carried sources on a planar-generation change, so
new, updated, or removed planar content still gets evaluated. The planar-only
mode still explicitly clears incoming metadata and evaluates its current
planar/probe/environment chain. No shadow, AO, exposure, reflection-intensity,
or temporal blend settings changed.

## Rendered regression

A temporary sample hook drove the real renderer for 920 frames at 1600x900:

- Camera `(6, 2, 0)`, yaw `0`, pitch `-0.35`.
- Warm up for 480 frames, translate 0.6 m along X over 120 frames, hold for
  120 frames, return over 120 frames, then hold.
- Frame 599 is the last moving frame and has the same camera pose as frames
  600–719. Exposure was fixed at 2; AO, bloom, and fog were disabled.
- Production directional shadows and hybrid reflections remained enabled.
- Readback screenshots and diagnostics were recorded at the movement/hold
  boundaries. Diagnostics confirmed mode 5 (`HybridWithPlanar`), adaptive
  reflection execution, and no active planar capture in the reproduction.

The curtain-pattern ROI is `[560, 205, 850, 270)`. Values below are mean luma
of the display RGB bytes, using `(0.2126, 0.7152, 0.0722)`, not linear HDR
luminance.

| Sample | Before | After |
| --- | ---: | ---: |
| Last moving frame, 599 | 19.2906 | 19.8616 |
| First stationary frame, 600 | 6.4605 | 19.8616 |
| Stationary frame, 615 | 5.2501 | 19.8616 |
| Long hold, 719 | 8.1572 | 19.6817 |

The stop-induced loss was 72.8% before and zero after. At the end of the
long hold the corrected ROI remained within 1% of the moving value. The
rendered check passed its 2% stop and 5% long-hold tolerances.

Isolation controls showed that disabling CSM temporal history retained the
defect; shadow-factor images were byte-identical from frame 599 through the
hold; disabling reflections removed the motion-dependent change. This
distinguishes the reflection defect from a shadow change hidden by lighting.

Local captures, diagnostics, the temporary reproducer, and `result.json` are
under `%TEMP%/njulf-shadow-motion-20260906`. `temporal` contains the original
run and `fixed` the corrected run. The exploratory `candidate` run did not
fix the issue and its velocity-weight change was discarded. The temporary
sample hook was removed after verification.

## Checks

- Development shader and sample builds passed.
- Both changed SPIR-V artifacts passed `spirv-val --target-env vulkan1.3`.
- The focused `HybridReflectionContractsTests` shader-contract and history-
  revision tests passed: 2 passed, 0 failed, 0 skipped.

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore `
  -p:BuildProjectReferences=false `
  --filter 'FullyQualifiedName~HybridReflectionContractsTests.ShaderSources_ContainStrictFallbackShadingAndDebugContracts|FullyQualifiedName~HybridReflectionContractsTests.History'
```
