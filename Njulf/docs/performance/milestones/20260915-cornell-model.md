# Cornell model replacement — 2026-09-15

- Revision: 9cc0c406779c4e818b6e8c30b0f22da1916f2ead, dirty workspace (pre-existing renderer/shader work present).
- Decision: use the pinned ErfanMo77 glTF conversion of Benedikt Bitterli's CC0 Cornell Box for the default GI scene and GiCornellRoom scenario. Keep specialized procedural validation fixtures unchanged.
- Asset: eight meshes, 72 vertices, 108 indices, eight materials; source glTF and buffer preserved unchanged, uniformly scaled by 2 and translated to (0, 0, -5.5). Keep the existing direct point light alongside authored emissive geometry.
- Hardware: NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0. Development build, default Cornell GI settings, 1600x900.
- Validation: asset cook succeeded with zero warnings; Development build succeeded with zero warnings/errors; 120-frame startup passed. Both reload operations passed, but the reload run closed after 5/120 frames and its overall health status failed (zero Vulkan validation messages, one GI warning); sustained rendering after reload is unverified. Compact results: [validation JSON](20260915-cornell-model-validation.json).
- Baseline/candidate timings and tail latency: not measured; this is an asset replacement, not a performance comparison.
- Quality limitations: no reference-image comparison performed. Old procedural Cornell luminance/image baselines are invalid for this model. The point light means this is not a faithful reproduction of the upstream PBRT lighting reference.
- Reproduction: cook command and pinned provenance in `NjulfHelloGame/Assets/CornellBox/README.md`; then `dotnet build NjulfHelloGame -c Development` and `dotnet NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.dll --smoke-mode scene-reload --scene-reloads 2 --smoke-frames 120 --performance-scenario gi-cornell-room --health-report artifacts/cornell-model-reload-health.json`.
- Retention: no isolated builds or image/trace archives created. Source and cooked model are runtime assets. Storage pruning was inspected; existing campaign artifacts were not removed because active investigations share this workspace.
