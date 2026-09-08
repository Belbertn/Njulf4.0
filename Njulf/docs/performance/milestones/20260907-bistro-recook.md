# Bistro loading restored — 2026-09-07

- Source: `7eafc485087af01fabfa4a931fb4d03eae93dae7`, with pre-existing uncommitted scene, editor, renderer, and test work. No application source changes were needed for this repair.
- Failure: pressing 3 from Sponza entered the lightweight loading scene, then rejected BistroExterior's cooked import contract `0x64b53367df9eb0e6`; runtime required `0x2c0c49b952e38f97`. The game recovered to GI instead of loading Bistro.
- Repair: ran the existing Bistro cook workflow for both exterior and interior using Development, Assimp/AmazonBistro, AutoBc, full mip chains, and portable-48v-64t meshlets. Rebuilt the game. Both runtime model files now match the source cook byte hashes and carry the expected import contract.
- Validation: all five explicit Bistro contract/material integration tests passed (none skipped). Game build succeeded with zero warnings/errors. Automated Sponza-to-Bistro transition completed through the loading-scene handoff and reached full residency, including the interior; source fallbacks=0, GI recovery=false, Vulkan errors=0, Vulkan warnings=0.
- Hardware: AMD Ryzen 5 5600H; NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0. Development smoke with Standard Vulkan validation and default sample settings, warm application pipeline cache, cold Bistro scene residency. Exact options and producer fingerprints are in [compact smoke evidence](20260907-bistro-recook.json).
- Timing: original load failed, so no successful baseline timing exists. Repaired run first Bistro present=6,923.957 ms; full residency=12,181.854 ms; maximum host step=2,247.854 ms. Single transition only; mean/p95 distributions were not measured. Exterior cooking took 77.440 s overall; interior texture processing took 25.504 s.
- Decision: retain regenerated packages. The loading failure is repaired. The smoke's separate latency gate still failed its first-present target (5,000 ms) and maximum-host-step target (33 ms); full residency met its 15,000 ms target. This is functional validation, not a performance improvement claim or image-quality qualification.
- Storage: all task logs/temp files stayed under workspace `artifacts/` on D:. Pruner inspection found 5.42 GiB in the earlier C-drive-migration folder; retained because its active references were not established in this task. Approximately 295 GiB remained free after repair. No bulky captures or isolated builds were created.

Reproduce from the repository root:

```powershell
./tools/cook-bistro.ps1 -Configuration Development
Push-Location NjulfHelloGame/bin/Development/net10.0
./NjulfHelloGame.exe --scene sponza --smoke-mode scene-transition --validation standard
Pop-Location
```

Detailed local logs: `artifacts/bistro-recook-20260907.log`, `artifacts/bistro-transition-20260907.log`, and `artifacts/bistro-transition-20260907.stderr.log`. Generated cooked assets are ignored local output; rebuilding alone cannot regenerate a stale cook.
