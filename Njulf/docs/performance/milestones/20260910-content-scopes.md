# Content asset types and scoped lifetime — 2026-09-10

- Source: `10440367`, dirty working tree. Existing uncommitted scene, material, editor and renderer work was preserved. This milestone describes the additional Content changes, not all working-tree changes.
- Machine: Windows, AMD Ryzen 5 5600H; NVIDIA GeForce RTX 3060 Laptop GPU and AMD Radeon integrated graphics. The example uses the existing host's Vulkan device selection.
- Workload: Development build, `Njulf.ApiExamples --example content`, 960×640, 180 full-quality frames, Vulkan validation enabled, two tetrahedra, two scene files, PNG textures and PBR material files. Level B replaces level A after 40 full-quality frames. No shader changes; cached shader build remained up to date.

## Result and decision

Implemented independent scopes sharing the Content cache, temporary acquisition claims, cancellation-safe publication, retryable release and composite dependency ownership. Added standalone one-mip PNG/JPEG textures, version-1 material JSON, and fresh scene loading through the existing scene document pipeline. Root unloading releases only root acquisitions; manager shutdown releases all owners.

- 98 focused tests passed, zero failed or skipped. Coverage includes Content/model hardening, upload dispatch, scene documents and material persistence, material lifetime, the new scope tests and one real Vulkan Content dependency-lifetime test.
- Final example completed 180 frames with zero Vulkan validation errors. Its checks confirmed level-exclusive assets released, shared material identity survived the transition, root texture survived both level scopes, and final root release succeeded.
- The Vulkan test confirmed the physical texture remained accessible after all Content claims were released while a render object still retained its material, then became inaccessible after the final object release.
- Inspected the final screenshot: both level-B tetrahedra render with the shared blue material.
- During integration, fixed two existing test-fixture issues: reused OBJ filenames could encounter stale cooked packages; retained resource comparisons now inspect handles instead of reflecting over borrowed material wrappers.

Accept this limited implementation. No performance improvement is claimed.

## Timing and limitations

No pre-change performance baseline was collected; this was a functional change. These are observational smoke-run frame intervals, not a controlled benchmark:

| Run | Samples after frame 120 | Median ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: |
| Initial implementation smoke | 60 | 16.706 | 20.549 | 31.135 |
| Final implementation smoke | 60 | 16.550 | 20.133 | 31.103 |

The sample includes startup and scene-transition hitches outside this tail sample. Large-asset throughput and async callback budgets were not qualified. Texture creation and scene population are indivisible callbacks. Extra texture formats/mips, material/scene cooking, hot reload and editor integration remain outside this change. Optional scene stores retain their existing synchronous contracts.

## Reproduction and retention

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~ContentScopeTests|FullyQualifiedName~ContentManagerHardeningTests|FullyQualifiedName~ContentRendererIntegrationTests|FullyQualifiedName~RenderThreadContentUploadDispatcherTests|FullyQualifiedName~SceneDocumentTests|FullyQualifiedName~SceneMaterialOverridePersistenceTests|FullyQualifiedName~PublicMaterialTests|FullyQualifiedName~MaterialReleaseDurabilityTests|FullyQualifiedName~ContentScopesPreserveGpuTexture'
dotnet run --project Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development -- --example content --frames 180 --validation
```

For a capture, append `--capture artifacts/content-validation/<new-name>.png`; the example requires a new output filename. Current logs and capture are in ignored `artifacts/content-validation/` on D:. The superseded initial capture was removed after preserving its findings here. The final capture may be pruned after two days; compact evidence remains in this milestone.

`tools/prune-local-artifacts.ps1` was inspected and run in preview mode: 1,926 old payload files / 7.84 GiB were identified, 283.11 GiB remained free, and there were zero inspection failures. Other investigations' payloads were left in place because their active-reference status was not established. This task's final retained raw capture and text logs total less than 0.3 MiB.
