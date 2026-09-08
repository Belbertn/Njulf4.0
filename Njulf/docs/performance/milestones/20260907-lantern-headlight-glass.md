# Lantern and headlight glass — 2026-09-07

Revision `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty workspace with earlier renderer/editor work preserved. User requested glass instead of solid white/grey lantern panes and the Bistro moped headlight.

Sponza's `lamp_glass_01` was authored as opaque grey, double-sided, roughness 0.39, without transmission. Corrected that material only: transmission 0.92, roughness 0.18, IOR 1.52, neutral slight tint, alpha blend with full geometric coverage. The glTF carries standard transmission/IOR extensions plus explicit `NJULF_thin_glass: true` and the existing thin-surface GI policy. Both SharpGLTF and Assimp preserve the explicit visible-glass flag. This opt-in is separate from GI-only transmission, so cloth is not inferred to be glass. The metal frame and emissive bulb remain opaque.

Bistro has a dedicated `Vespa_Headlight_BaseColor` texture identity, despite the main Vespa object using its separate opaque body material. Its base texture is solid white. Added that exact identity to the existing reviewed Bistro thin-glass profile: transmission 0.94, roughness 0.12, IOR 1.52. Body and odometer materials are unaffected. These are chosen plausible thin-glass settings, not recovered original artist parameters. Bumped the Bistro profile revision to v4 and recooked exterior and interior so runtime contract checks remain exact.

## Validation

- 26 focused tests passed: reviewed Bistro profiles and exclusion identities, plus importing the actual Sponza lantern material on a minimal triangle through both import backends.
- Two explicitly selected Sponza cooked tests passed: runtime import/source contracts and persisted lantern glass/frame/bulb classification.
- Two explicitly selected Bistro cooked tests passed: both runtime import contracts and the exterior's five thin-glass materials, including the headlight.
- Asset tool, renderer, editor, sample, and tests built successfully. No GLSL changes; existing compiled shader bundle reused.
- Recooks completed without warnings: Sponza main 142.9 s, Bistro exterior 80.7 s, Bistro interior 35.2 s. The built sample received the updated cooks.
- Production Sponza capture on RTX 3060 Laptop GPU at 1600×900 shows the bulb and background through the lantern while its metal frame stays solid. One visible thin-glass object/meshlet was recorded; no Vulkan validation errors. The baseline capture helper now waits for a full-quality presented frame instead of capturing the progressive startup screen.
- Production Bistro capture at 1600×900 includes the moped: the lens shows a dark interior/reflection instead of an opaque white disc, while body panels remain solid. No Vulkan validation errors were logged.
- No controlled performance baseline or tail-latency comparison was made: this is a material/appearance correction. The glass uses the existing transparent pipeline; its cost depends on visibility and reflection settings. Capture timing is not a performance qualification.

[Compact machine-readable evidence](20260907-lantern-headlight-glass.json). Full screenshots, logs and snapshots are under ignored `artifacts/material-glass/` on D:, retained only for the active investigation and at most two days afterward. User screenshots on C: were only read. D: had 293.33 GiB free after the cooks. Existing source assets and unrelated cooked generations were not pruned.

## Reproduction

```powershell
dotnet build Njulf.AssetTool/Njulf.AssetTool.csproj -c Development --no-restore
dotnet Njulf.AssetTool/bin/Development/net10.0/Njulf.AssetTool.dll cook model NjulfHelloGame/NewSponza_Main_glTF_003.gltf --out NjulfHelloGame/Cooked --platform win-x64 --backend SharpGltf --texture-format AutoBc --force
# Repeat for BistroExterior.fbx and BistroInterior.fbx with --backend Assimp --assimp-material-texture-convention AmazonBistro.
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -p:ShaderBuildMode=UseExisting --filter "FullyQualifiedName~AmazonBistroMaterialProfileTests|Name~SponzaLanternMaterial"
# Run explicit cooked tests in their own exact-name filters; NUnit excludes them in a mixed broad/non-explicit run.
./NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.exe --scene SponzaPlaza --performance-scenario GiSponzaRightWallStationary --smoke-frames 100000 --baseline-snapshot-dir artifacts/material-glass/sponza-render
```

Restart the editor to use the rebuilt runtime and recooked assets; no manual recook remains.

Storage pruner dry run completed; older unrelated captures were left intact because active-reference ownership was not established. New diagnostic payloads remain within the two-day retention window.
