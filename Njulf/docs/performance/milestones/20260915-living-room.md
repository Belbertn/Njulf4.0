# Living Room test scene — 2026-09-15

- Revision: 9cc0c406779c4e818b6e8c30b0f22da1916f2ead, dirty workspace with pre-existing renderer/shader and Cornell work.
- Decision: add the extended glTF Living Room after Bistro in the key-3 scene cycle, before Material Showcase. Use native scale and the authored camera; support `--scene living-room`.
- Source: Wig42's CC BY 3.0 Grey & White Room through Benedikt Bitterli and ErfanMo77. Pinned revision, buffer checksum, license and cooking command are in `NjulfHelloGame/Assets/LivingRoom/README.md`.
- Import: initial cooking failed because non-finite generated direction vectors reached LOD simplification. The SharpGLTF normalization helper now treats non-finite lengths like zero-length directions. Cooking then succeeded: 65 meshes, 120468 vertices, 429489 indices, 19 materials, four textures, zero warnings. Source glTF and binary are unchanged.
- Validation: Development build passed with zero warnings/errors. All 22 selected SharpGltfModelMeshConverterTests and SampleAnalyticalAreaLightRoomSceneTests passed (test project emitted existing nullable warnings). Startup rendered 120 frames and health passed with zero validation or GI warnings/errors; 65 model objects loaded. See [compact validation](20260915-living-room-validation.json).
- Hardware/workload: NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0; Development, 1600x900, sample daylight and DDGI profile, procedural environment, hybrid reflections, enclosed-room sky visibility enabled.
- Baseline/candidate timings and tail latency: not measured; this is scene integration, not a performance comparison.
- Limitations: no reference-image comparison or interactive keypress validation. The PBRT PFM sky is only metadata in glTF; sample daylight does not reproduce upstream lighting. Raw startup health/log evidence is under ignored `artifacts/`.
- Reproduction: cook/build using the asset README, then `dotnet NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.dll --scene living-room --smoke-frames 120 --health-report artifacts/living-room-health.json`.
- Retention: pruning inspection run; no isolated builds, captures, or traces created. Runtime source/cooked assets retained. Shared active-campaign artifacts not removed.
