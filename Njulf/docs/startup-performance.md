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
scheduled only after the first successful present and the cache is bounded to
512 MiB.

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
