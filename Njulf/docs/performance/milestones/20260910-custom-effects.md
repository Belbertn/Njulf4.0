# Custom shader effects — 2026-09-10

Implemented the bounded shader-effect API and color-grading example. Source base:
`104403675fc74abf8c110c07a30a8a6411589689`, with a pre-existing dirty working tree;
existing scene/content/graphics/editor work was preserved. Feature changes are uncommitted.

Environment: Windows, .NET SDK 10.0.203, Development configuration, AMD Ryzen 5 5600H,
NVIDIA GeForce RTX 3060 Laptop GPU (6144 MiB), driver 610.62, Vulkan SDK 1.4.335.0.

## Result

- Public shader assets, discoverable typed parameters, named bindings and fullscreen/compute registrations
  reside above Vulkan. Content loading/scopes work for immutable CPU assets without a device.
- The existing native pass registry owns graph insertion, retention and retirement. Rebinding can also
  replace fullscreen outputs or compute thread extents. Disable/re-enable preserves registration order.
- Post effects run after tone mapping and before AA, using one scratch target and a copy-back pass each.
  AA-disabled rendering routes through the LDR intermediate and a presentation copy. UI/native overlays
  retain their ordering. Enabled-effect changes invalidate TAA history; edits while disabled do not.
- Explicit metadata and the existing shader compiler task are used. No reflection, editor panel,
  runtime compiler, new backend, pipeline cache or pooling system was introduced.
- Review caught an intermediate shader-evidence incompatibility: asset paths were not canonical module
  names and the validator did not recognize effect provenance. Logical-name hashing plus source kind
  `effect` now preserves canonical inventories and detection of changed code for the same logical asset.

## Validation

19 distinct focused cases passed across targeted runs, with no skips:

| Group | Cases | Observation |
| --- | ---: | --- |
| ShaderEffectAssetTests | 4 | CPU content/scopes/async, parameter ABI, invalid assets, shader evidence |
| ShaderEffectGpuTests | 3 | None/SMAA/TAA; image/buffer results, post chains, resizing, rebind, retirement |
| FrameworkArchitectureTests | 4 | Dependency boundaries and public contracts |
| CustomRenderingLifecycleTests | 3 | Existing native ordering, recreation and failure cleanup |
| LoadedShaderIdentityTests | 5 | Existing identity/measurement contracts |

The GPU fixture verifies all 17×13 pixels, then resizes targets/dispatches to 19×11, using RGBA16F
readback tolerance 0.001. It checks integer buffer writes, first-frame TAA grading changes, constant
composed screenshot colors within 2/255, disposal of caller-owned references before automatic resize,
disable/re-enable order and removal. A final TAA-only run also verified that editing a disabled effect
does not advance the history revision. Vulkan validation reported zero errors. Temporary test runtime
caches and screenshots are created under the test output directory on D: and deleted afterward.

All five new shaders passed SDK `spirv-val --target-env vulkan1.3`. The API example built with zero
warnings/errors. The grading example presented 180 full-quality frames; the unchanged native custom
example presented 360 frames, including its target replacement and resolution rebuild, both with zero
Vulkan validation errors. An earlier 160-frame AA-disabled grading smoke also passed.

## Timings and limits

These are paced application smoke intervals, **not a baseline/candidate optimization comparison**.
There was no equivalent previous effect API, and separate effect GPU timing was not measured.

| Smoke workload | Presented / measured frames | Median ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: |
| Final grading example, 960×640, default SMAA | 180 / 60 | 16.809 | 20.631 | 49.604 |
| Native custom compatibility example | 360 / 240 | 16.647 | 19.481 | 50.444 |
| Earlier grading example, AA disabled | 160 / 40 | 16.313 | 19.977 | 32.799 |

The earlier grading scene preceded the example's emission adjustment. Workloads differ and the small
sample counts are not tail certification; no performance improvement is claimed. Shader metadata must
match authored GLSL. Frequently animated active grading parameters reset TAA. No broad renderer,
device-matrix or performance campaign was run.

## Reproduction and retention

See [custom effects](../../CustomEffects.md) for assets, API usage and workspace runtime-cache settings.

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --filter 'FullyQualifiedName~ShaderEffectAssetTests|FullyQualifiedName~ShaderEffectGpuTests|FullyQualifiedName~FrameworkArchitectureTests|FullyQualifiedName~CustomRenderingLifecycleTests|FullyQualifiedName~LoadedShaderIdentityTests'
dotnet run --project Njulf.ApiExamples -c Development -- --example effects --frames 180 --validation --capture artifacts/effects/repro-graded.png
dotnet run --project Njulf.ApiExamples -c Development -- --example custom --frames 360 --validation --capture artifacts/effects/repro-native.png
```

Compact logs/TRX and final acceptance images are under `artifacts/effects/`. Earlier superseded grading
screenshots were removed after recording these results. Initial application smokes used the application's
pre-existing default runtime caches; those are not assumed to be disposable Codex-owned data. The new
GPU fixture redirects unset cache locations into its workspace output, and documented reproduction
settings direct application caches to D:. No uncertain C: files were deleted.

`tools/prune-local-artifacts.ps1` inspection found 7.84 GiB of older payload candidates and 283.1 GiB
free on D:. Pre-existing campaigns were left intact. This investigation's retained captures/logs are
approximately 1 MiB; no isolated builds or raw GPU traces were accumulated. Final screenshots are
temporary acceptance evidence subject to the repository's two-day retention rule.
