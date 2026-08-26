# Build configurations

Use `Development` for ordinary engine, game, and editor work:

```powershell
dotnet build Njulf.sln -c Development
dotnet run --project NjulfHelloGame -c Development
```

`Development` keeps the editor and source-asset import path available, emits
portable managed symbols, and enables standard Vulkan validation and debug
labels. Managed code and shaders are optimized, while the expensive detailed
GPU counter variants and shader debug information are compiled out. This keeps
startup and frame iteration representative enough for productive work.
Read-only GI receiver visualizations remain compiled so the editor's GI debug
view menu is useful during ordinary Development builds; selecting one performs
its diagnostic sampling only for that view and does not enable detailed
counters or diagnostic atomics.

Stale or missing cooked packages automatically fall back to source import in
`Development`. Set `NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=false` when a
particular development run must enforce the cooked-only contract.

Renderer startup in `Development` also favors the active production path:

- mesh pipelines use the renderer's persistent, device-compatible Vulkan cache;
- the dense GPU-compacted path is created first;
- advanced GI material variants and inactive transparency families such as
  Weighted OIT are created on first use and then persisted.

`Debug` and `DetailedInvestigation` retain eager compatibility/diagnostic
pipeline creation so they can be used for fallback-path and deep GPU diagnosis.

| Configuration | Intended use | Editor | Managed/shader optimization | Default validation | Detailed GPU counters |
| --- | --- | --- | --- | --- | --- |
| `Development` | Daily implementation and editor work | Yes | Yes | Standard | No |
| `Debug` | Unoptimized stepping and deepest debug-only behavior | Yes | No | Standard | Yes |
| `DetailedInvestigation` | Optimized reproduction with expensive GPU diagnostics | Yes | Yes | Off; opt in as needed | Yes |
| `ProfileSymbols` | Profiler source attribution | Yes | Yes, with shader symbols | Off | No |
| `ShippingPerformance` | Controlled production performance evidence | No | Yes | Off | No |
| `Release` | Cooked release output | No | Yes | Off | No |

Override validation for a particular run with `--validation` or
`NJULF_RENDERER_VALIDATION`; the supported modes are `off`, `standard`, `gpu`,
`sync`, and `all`.

For a cold-cache startup investigation, point
`NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY` at an empty temporary directory. Normal
development should leave it unset so repeat launches reuse the validated cache.
