# Lantern glass without reflections — 2026-09-08

Revision `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty workspace; preceding light, shadow and glass changes preserved. User requested removal of reflections from the lantern panes.

Set Sponza `lamp_glass_01` to `KHR_materials_specular.specularFactor = 0`. Preserve transmission 0.92, tint, IOR, double-sided thin glass and thin-surface GI transport. The dedicated forward thin-glass shader now reads the existing specular factor, skips DDGI/environment reflection sampling at zero, and scales reflected radiance and Fresnel opacity by that factor. The universal shader also suppresses the reflected sheet contribution and grazing opacity at zero. No material ABI or new setting was introduced. Bistro's headlight and other glass retain their existing material settings.

The prior [glass capture](20260907-lantern-headlight-glass.md) is the visual baseline. This is an intentional appearance change; no controlled frame-time or tail-latency comparison is claimed. Validation uses SponzaPlaza / GiSponzaRightWallStationary, production rendering at 1600×900 on an RTX 3060 Laptop GPU.

Validation passed: Development build with fresh affected shader compilation (zero warnings/errors), SPIR-V validation of all three dedicated thin-glass variants, two import tests (SharpGLTF/Assimp), and two explicit cooked-material/runtime-contract tests. Sponza main recooked in 342.523 seconds with zero warnings; refreshed the sample output afterward. The capture exercised one visible glass object/meshlet on the general transparent path (directional-only glass flag 0), with standard Vulkan validation and zero warnings/errors. The reflected bulb highlight beneath the bulb is gone; the bulb and stonework remain visible through the panes. Dedicated variants were compiled/validated but not separately image-tested. Decision: keep.

First-use driver pipeline preparation was slow after the shader bundle changed, especially the general transparent ray-query pipeline. After both capture artifacts were written and the window closed, the process remained busy in teardown; stopped that verified capture process. A clean process-exit check is therefore unavailable. This capture is appearance evidence, not a startup, shutdown or steady-state performance qualification. [Compact evidence and shader identities](20260908-lantern-no-reflections.json).

Raw evidence is under ignored `artifacts/material-glass/` on D:, retained only for this investigation and at most two days afterward. Storage pruner dry run found 6.21 GiB of older payloads; unrelated campaign references were retained because active ownership was not established. D: had 292.96 GiB free. No source assets or user captures were pruned.

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore
dotnet Njulf.AssetTool/bin/Development/net10.0/Njulf.AssetTool.dll cook model NjulfHelloGame/NewSponza_Main_glTF_003.gltf --out NjulfHelloGame/Cooked --platform win-x64 --backend SharpGltf --texture-format AutoBc --force
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'Name~SponzaLanternMaterial'
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-build --filter 'Name=MainCook_LanternGlassTransmitsWhileFrameAndBulbStayOpaque|Name=BothSponzaCooks_ResolveUnderExactRuntimeImportContracts'
./NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.exe --scene SponzaPlaza --performance-scenario GiSponzaRightWallStationary --smoke-frames 100000 --baseline-snapshot-dir artifacts/material-glass/sponza-no-reflections
```
