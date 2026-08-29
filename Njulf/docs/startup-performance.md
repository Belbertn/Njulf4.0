# Startup performance

Njulf starts in `active-scene` pipeline mode in every build tier. The renderer
creates only pipeline families required by the configured renderer and initial
scene before first present. `--pipeline-startup exhaustive` (or
`NJULF_PIPELINE_STARTUP_MODE=exhaustive`) remains available for tooling that
must validate every optional family up front.

## Pipeline caches and deployment seeds

The writable Vulkan cache defaults to:

`%LOCALAPPDATA%/Njulf/PipelineCaches/gi-<vendor>-<device>.njvkcache`

Override it with `NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY`. Serialization is
scheduled after successful presents, coalesced off the render thread, and also
attempted during clean renderer shutdown. The envelope and driver payload are
bounded to 512 MiB and decoded as a stream. Writers use a per-cache lock and
atomic replacement, so concurrent application processes cannot publish a
partially written cache.

A deployment can include a read-only seed at:

`<application base>/PipelineCacheSeeds/gi-<vendor>-<device>.njvkcache`

Override that directory with
`NJULF_VULKAN_PIPELINE_CACHE_SEED_DIRECTORY`. To produce a seed, run a
representative exhaustive qualification from the intended shipping build tier
on the target GPU/driver, close the application cleanly, and copy the resulting
writable `.njvkcache` file into the seed directory. Do not rename it.

Seeds and writable caches use the same checked envelope. Vendor, device,
driver, Vulkan API version, `pipelineCacheUUID`, and renderer ABI must match;
the payload is length-bounded and SHA-256 checked. A missing, corrupt, or
incompatible seed is ignored and the renderer falls back to an empty Vulkan
cache. Seeds are never written or deleted by the renderer.

Writable caches also record shader-bundle and build-configuration provenance.
Compatible data from an older envelope, another shader bundle, or another
build tier is still passed to Vulkan as an accelerator, but the launch is
classified as application-cold until the cache is refreshed after a successful
present. Only an exact current-tier writable cache is classified as warm. A
version-1 envelope is migrated automatically; no manual cache deletion is
required.

## Explicit pipeline binaries

When the device exposes `VK_KHR_pipeline_binary` together with its required
extended-flags dependency, the renderer also maintains an application-owned,
content-addressed binary store. Pipeline keys map to an ordered list of binary
keys; immutable blobs are SHA-256 checked and deduplicated. The global Vulkan
pipeline key, device/driver identity, shader bundle, renderer ABI, and build
configuration all participate in compatibility. A rejected writable entry is
removed and pipeline creation falls back to the ordinary Vulkan cache/compile
path.

The writable store defaults to:

`%LOCALAPPDATA%/Njulf/PipelineBinaries/v1/<global-key>`

Override it with `NJULF_PIPELINE_BINARY_CACHE_DIRECTORY`. The store is bounded
to 512 MiB and collects least-recently-used pipeline mappings. Manifest and
blob publication is atomic and protected by a cross-process store lock.

A read-only deployment seed can be placed at:

`<application base>/PipelineBinarySeeds/v1/<global-key>`

Override that root with `NJULF_PIPELINE_BINARY_SEED_DIRECTORY`. Writable
entries take precedence over seed entries. Seeds are validated but never
modified.

On a binary miss, drivers that prefer their internal cache keep using the
shared Vulkan cache and expose any available binary data afterward. Other
drivers use the explicit capture-data path with a null `VkPipelineCache`, save
the resulting binary set asynchronously, and release captured driver resources
immediately after extraction.

`NJULF_PIPELINE_BINARY_CACHE` controls the feature:

| Value | Behavior |
| --- | --- |
| `auto` (default) | Use pipeline binaries when the complete optional feature chain is available; otherwise use the Vulkan pipeline cache. |
| `off` | Disable the application-owned binary store. |
| `require` | Require pipeline binaries and enable compile-miss verification; fail startup if support or a required artifact is missing. |

## Startup compilation and verification

All pipeline owners connected to the renderer cache service create graphics
and compute pipelines through one compiler gateway. It records wall time,
driver feedback duration, application-cache hits, compile-required results,
artifact source (writable binary, seed binary, Vulkan cache, or compilation),
stage count, peak concurrency, and whether creation escaped into a
render-critical frame. Aggregate hit/miss, binary-capture, concurrency, store
path, and graphics-pipeline-library eligibility fields are included in renderer
diagnostics and performance snapshots.

Independent mesh startup families are scheduled through a bounded worker pool.
`NJULF_PIPELINE_COMPILE_WORKERS` accepts `1` through `8`. The default is
`min(4, max(1, processor-count / 4))`. Logical startup manifests are awaited
before publication, so a scene cannot observe a partially built pipeline bank.

Active-scene mode scans visible material metadata and prepares the masked,
transparent, thin-glass, thick-transmission, decal, receiver-feedback, and ray
variants required by that scene before command recording. Optional specialized
families retain their universal fallback. Exhaustive mode requests every
material family.

Set `NJULF_PIPELINE_CACHE_VERIFY=1` or pass `--pipeline-cache-verify` to add
`VK_PIPELINE_CREATE_FAIL_ON_PIPELINE_COMPILE_REQUIRED_BIT` when the device
supports pipeline creation cache control. A returned compile-required result
counts as a cache miss and makes qualification fail at the owning pipeline.
Warm classification requires an exact current-build writable cache and zero
feedback-backed compile misses.

`VK_EXT_graphics_pipeline_library` support and fast-link properties are probed
and reported as eligibility telemetry. Pipeline-library splitting remains
disabled until a representative fast-link qualification demonstrates a win;
unsupported or partial extension chains always fall back to monolithic
pipelines.

## Latency gates

The first successful present is measured from `Game.Run`:

| Cache class | Target | Hard limit |
| --- | ---: | ---: |
| Warm writable application cache | 5 s | 10 s |
| Empty writable cache, optionally using a deployment seed | 15 s | 30 s |

Ordinary launches report timing in every build tier and do not terminate when
a limit is exceeded. Automated qualification must opt into fatal enforcement
with `NJULF_STARTUP_LATENCY_GATE=enforce`; the graphics smoke workflow and
material-GI release gate do this explicitly. Use `timing` to select the normal
report-only behavior or `off` to disable the measurement output.

Startup JSONL can be requested with `--startup-log <path>`. Use a one-frame
smoke launch for repeatable first-present checks:

```text
--smoke-mode Startup --smoke-frames 1 --pipeline-startup active-scene
```
