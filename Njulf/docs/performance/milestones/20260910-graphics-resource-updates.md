# Graphics resource updates — 2026-09-10

- Source: `104403675fc74abf8c110c07a30a8a6411589689`, dirty workspace throughout. Existing content, material, scene, editor and pipeline changes were preserved.
- Hardware: Windows, NVIDIA GeForce RTX 3060 Laptop GPU; AMD Radeon integrated GPU also enumerated. Development/net10.0 builds.
- Decision: implement standard authored vertices, fixed-topology dynamic geometry, eight portable texture formats, authored/generated mips, explicit updates, buffer uploads and fence-completed asynchronous readback on the existing Vulkan device.
- Implementation limits: dynamic meshes retain LOD0 and use conservative whole-mesh meshlet bounds without normal-cone rejection. Dynamic BLAS work follows existing budgets; deferred dynamic geometry is excluded instead of using stale static acceleration structures. No arbitrary vertex layouts, topology updates, compressed uploads, arrays, cubemaps or depth transfers.

## Validation

- Rendering and API example projects built successfully. Final resource suite: **17 passed, zero skipped**, including native-byte round trips for every new format, partial updates, mip preservation/regeneration, HDR values, authored normal/UV/tangent GPU streams, shared bounds and sampler aliases, argument validation, cancellation before/after submission, and queued/submitted readbacks during real renderer disposal.
- The GPU ray test creates a BLAS, updates vertex storage, then observes a real Vulkan refit and updated GPU positions, with no static BLAS admitted. This is not an independent ray-intersection numerical oracle.
- Procedural example: 180 full-quality frames with asynchronous bloom explicitly active; zero Vulkan validation errors. The earlier capture was inspected and shows both textured, deformed instances.
- Custom example: 303 full-quality frames, asynchronous bloom active, zero Vulkan validation errors; verifies compute-written buffer bytes, a separately uploaded byte range, and native RGBA16F target readback.
- Initial five resource tests failed because transfers were only attached to production frames. Adding bootstrap-frame recording/submission/completion resolved those failures without waiting for production startup.
- A broader 42-test selection reported passing test bodies but failed `GraphicsApiIntegrationTests` teardown. Isolated to `PublicMaterialTextureEditsPreserveSiblingsAndRetainedSnapshots`, which does not invoke the new transfer operations: TextureManager reports incomplete durable retirement work during service-provider disposal. The runner incorrectly returns exit code zero for that fixture failure; it is **not** counted as clean validation. The isolated content-scope, typed texture lifetime and custom-resource declaration tests passed. No pre-change baseline was run for this teardown issue, so its provenance is unproven.

Compact machine-readable results: [resource tests](../../../artifacts/framework-api/tests/graphics-resources.trx), [isolated material teardown](../../../artifacts/framework-api/tests/legacy-material-edit-lifetime.trx).

## Timing and storage

This is API correctness work, not a performance optimization claim. No before/after benchmark baseline was collected. Example frame-interval observations include startup/JIT effects and different workloads:

| Workload | Baseline | Candidate median | p95 | p99 | Samples |
| --- | --- | --- | --- | --- | --- |
| Procedural, async bloom | Not measured | 16.652 ms | 22.910 ms | 49.975 ms | 60 |
| Custom passes, async bloom | Not measured | 16.710 ms | 20.876 ms | 52.345 ms | 183 |

`tools/prune-local-artifacts.ps1` inspection found 7.84 GiB eligible older payloads and 283.1 GiB free on D:. No bulk deletion was applied because other investigations' references were not classified as obsolete. This task produced two small PNGs (about 0.54 MiB total) and compact TRX results under `artifacts/framework-api`; no isolated build copies or traces. Captures are disposable after two days; retain this record and compact test results.

## Reproduction

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter "FullyQualifiedName~GraphicsResourceIntegrationTests" --logger "trx;LogFileName=graphics-resources.trx" --results-directory artifacts/framework-api/tests
dotnet run --project Njulf.ApiExamples -c Development -- --example procedural --frames 180 --validation --async-validation
dotnet run --project Njulf.ApiExamples -c Development -- --example custom --frames 120 --validation --async-validation
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-build --filter "FullyQualifiedName~GraphicsApiIntegrationTests.PublicMaterialTextureEditsPreserveSiblingsAndRetainedSnapshots"
```

Usage and precise scheduling semantics: [GraphicsResourceUpdates](../../GraphicsResourceUpdates.md).
