# Framework API and assembly boundaries — 2026-09-09

- Source: `7ce858e6`, dirty workspace containing earlier framework changes and independent user work. This milestone covers roadmap items 9–10; it does not claim validation of unrelated changes.
- Machine: Windows, AMD Ryzen 5 5600H, RTX 3060 Laptop GPU, NVIDIA 610.62, .NET SDK 10.0.203.
- Decision: retain the Core/Graphics/Assets/Rendering/Framework boundaries and the separate Assets.Tooling project. Core is BCL-only; Graphics depends only on Core. Runtime closure excludes Editor and offline tools. Native resource implementations remain internal and preserve existing managers and retirement behavior.
- Ordinary API: cached typed button actions, Njulf math, shared texture color space, RenderingOptions in Njulf.Rendering, public abstract graphics contracts, adjacent and packaged XML docs. Input enforces missing-documentation warnings as errors; critical XML entries and dependency closure have regression checks.

## Validation

- Development solution build: passed, zero warnings/errors. Release locked restore: passed.
- 255 focused tests passed: input, architecture/XML, resource retirement, settings, Vulkan pass contracts, GPU layouts, material contracts, scenes, cooked authentication, cooking/migration transactions, KTX2/WebP and transport decoding.
- 17 GPU lifecycle tests passed: game startup/shutdown/cancellation, resource ownership, custom pass lifecycle and cleanup.
- Final input/architecture/XML recheck: 12 passed after documentation and thread-guard cleanup (overlaps the 255 above).
- Nine local NuGet packages contain DLL/XML pairs with nonempty documentation. Graphics-only and Framework-only package consumers compile without source project references, zero warnings/errors. Evaluated dependency closures satisfy the boundary rules. Packages were not published.
- Custom example: 960×640, 360 full-quality frames, Vulkan validation enabled, forced supported async Bloom path active (one enabled pass). Target rebinding and resolution rebuilding completed; zero validation errors. Capture inspected: scene geometry, custom target panel and overlay marker visible.
- Observed custom-example frame intervals over 240 samples: median 16.573 ms, p95 21.450 ms, p99 50.361 ms; 556,054,432 allocated bytes. This is a validation run including resource changes, not a performance comparison. No matched baseline was collected; no performance improvement or non-regression is claimed.
- `git diff --check`: passed. No shader logic, GPU layouts or serialized enum values intentionally changed.

## Reproduction

```powershell
dotnet build Njulf.sln -c Development
dotnet restore Njulf.sln -p:Configuration=Release --locked-mode
pwsh -NoProfile -File tools/verify-framework-packages.ps1
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-build --filter 'FullyQualifiedName~FrameworkArchitectureTests|FullyQualifiedName~InputActionTests|FullyQualifiedName~InputManagerTests|FullyQualifiedName~FrameworkContractTests|FullyQualifiedName~GraphicsSettingsControllerTests|FullyQualifiedName~VulkanPassContractTests|FullyQualifiedName~GPUStructLayoutTests|FullyQualifiedName~MaterialTransportV2Tests|FullyQualifiedName~RenderSettingsFileIoTests|FullyQualifiedName~SceneTests|FullyQualifiedName~SceneDocumentTests|FullyQualifiedName~ModelAssetCookerTransactionTests|FullyQualifiedName~CookedAssetMigratorTransactionTests|FullyQualifiedName~CookedTextureAuthenticationTests|FullyQualifiedName~TextureTransportStatisticsTests|FullyQualifiedName~WebPTextureDecoderTests|FullyQualifiedName~Ktx2TextureTests|FullyQualifiedName~CookedAssetTests|FullyQualifiedName~GameLifecycleIntegrationTests|FullyQualifiedName~GraphicsApiIntegrationTests|FullyQualifiedName~CustomRenderingLifecycleTests'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example custom --frames 360 --validation --async-validation --capture artifacts/framework-contracts/items910-custom.png
```

Compact logs/TRX and the final local package result are under ignored `artifacts/framework-contracts/items910-*`, `tests/`, and `packages-20260909-091549/result.txt`. Automatic approval review rejected removal of the superseded package payloads (blocked by policy); both local package runs remain in ignored artifacts for normal retention cleanup. Artifact-pruner inspection reported 296.1 GiB free and 3.69 GiB eligible across other investigations; unrelated campaign data was left intact. Current bulky package/capture artifacts are eligible for normal retention cleanup two days after completion.

Limitations: legacy specialist XML documentation remains incomplete. Multi-view support and the outstanding editor/large-sample scene ownership migration remain separate work, as tracked in [FrameworkApiRemaining](../../FrameworkApiRemaining.md). Validation is Windows/NVIDIA and the current single-view renderer; no other backend or broad performance campaign was added.
