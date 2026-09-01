# Startup performance

Njulf defaults to progressive `active-scene` startup in every build tier. It
publishes three active phases:

1. `Bootstrap` presents a subtle animated dark, pipeline-free clear.
2. `ProductionPreparing` keeps that dark clear visible while production
   graphics and compute pipelines are created through the shared cache service.
3. `FullQuality` is published after the production graph and every active-scene
   pipeline that can change the presented pixels is ready. The milestone
   completes only after that production frame has successfully presented.

`FallbackScene` remains an enum/API compatibility value but is not entered by
the default path. Production resource preparation starts immediately on the
Vulkan-owning thread, native pipeline creation starts as soon as those resources
exist, and initial content loading overlaps that compilation after the bootstrap
present. Fast startup admits one full-material opaque beauty pipeline in the
selected task/taskless submission form. Performance-equivalent simple,
full-input, and alternate-submission variants are deferred.

Exact receiver-feedback producers, the receiver-cache/adaptive compute family,
and output-equivalent transparent partition pipelines do not gate the first
production present. They are compiled on the bounded worker after that present;
rendering continues on the canonical exact DDGI and production color pipelines
until the complete immutable bank is published atomically. A failed or partial
bank is never visible and permanently retains the exact path for that renderer
generation.
Hybrid-reflection receiver specializations follow the same rule. The first
production frame uses the already-ready exact hybrid receiver, while the
cache-accepted, exact-fallback, and combined rollback programs are prepared as
one post-present family only when the requested receiver mode needs them.
Thick-transmission ray-query draws always retain the full canonical ray color
program: the compact feedback shader is never substituted for that path.

`--pipeline-startup blocking-active-scene` retains the synchronous active-scene
path. `--pipeline-startup exhaustive` builds every optional material family for
tooling and qualification. The equivalent environment setting is
`NJULF_PIPELINE_STARTUP_MODE`.

## Pipeline caches and deployment seeds

The writable Vulkan cache defaults to:

`%LOCALAPPDATA%/Njulf/PipelineCaches/gi-<vendor>-<device>.njvkcache`

Override it with `NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY`. No cache checkpoint is
scheduled while the first full-quality frame is being prepared. The first
successful full-quality present requests an immediate checkpoint; later changes
are debounced and rate-limited, and clean shutdown performs a final synchronous
attempt. Serialization runs off the render thread.

The envelope and driver payload are bounded to 512 MiB and decoded as a stream.
Writers use a per-cache lock and atomic replacement, so concurrent application
processes cannot publish a partially written cache.

A deployment can include a read-only seed at:

`<application base>/PipelineCacheSeeds/gi-<vendor>-<device>.njvkcache`

Override that directory with
`NJULF_VULKAN_PIPELINE_CACHE_SEED_DIRECTORY`. Seeds and writable caches use the
same checked envelope. Vendor, device, driver, Vulkan API version,
`pipelineCacheUUID`, renderer ABI, shader bundle, and build configuration are
validated. A missing, corrupt, or incompatible seed is ignored; seeds are never
written or deleted by the renderer.

After an exhaustive qualification on the intended GPU/driver and shipping build
tier, export a checked seed with:

```powershell
./tools/export-pipeline-seeds.ps1 -PipelineCachePath <path-to-njvkcache>
```

Pass `-PipelineBinaryGlobalKeyDirectory` as well to export a qualified
explicit-binary store. The default destination is the sample project; override
it with `-DestinationRoot`. The script validates the device-qualified cache
filename, copies into `PipelineCacheSeeds` / `PipelineBinarySeeds/v1`, and emits
a SHA-256 receipt. Either source may be exported independently; a binary-only
deployment uses `-PipelineBinaryGlobalKeyDirectory` without
`-PipelineCachePath`. The sample project copies either directory to build and
publish output when it exists.

Device, driver, API, `pipelineCacheUUID`, and renderer ABI remain hard cache
compatibility boundaries. Within those boundaries, a cache from an older shader
bundle or build is admitted as an opportunistic partial cache: Vulkan keys exact
pipeline inputs internally, so unchanged pipelines can hit while changed
pipelines compile normally. Revision-mismatched data is never classified as
warm or as a qualified seed. After the current build presents a full-quality
frame, the combined cache is atomically republished with current provenance.

## Explicit pipeline binaries

When the complete `VK_KHR_pipeline_binary` feature chain is available, the
renderer can maintain an application-owned content-addressed binary store.
Pipeline keys map to ordered binary-key lists; immutable blobs are SHA-256
checked and deduplicated. Device/driver identity, the global Vulkan key, and
renderer ABI are hard compatibility boundaries. Shader/build revisions may
reuse only entries whose driver-generated pipeline-create-info key still matches
exactly; other entries are ordinary misses.

The writable store defaults to:

`%LOCALAPPDATA%/Njulf/PipelineBinaries/v1/<global-key>`

Override it with `NJULF_PIPELINE_BINARY_CACHE_DIRECTORY`. A read-only deployment
seed can be placed at
`<application base>/PipelineBinarySeeds/v1/<global-key>` or overridden with
`NJULF_PIPELINE_BINARY_SEED_DIRECTORY`. Writable entries take precedence.
Successfully consumed seed entries are promoted into the writable store, so a
qualification run produces one self-contained export even when it began with a
partial read-only seed.

Use `--pipeline-binary-cache <mode>` or
`NJULF_PIPELINE_BINARY_CACHE=<mode>`:

| Value | Behavior |
| --- | --- |
| `auto` (default) | Consume compatible stored binaries. With a current Vulkan cache, a miss keeps using that cache. On an application-cold run, drivers without an internal binary cache use capture-on-miss so the writable binary store is populated for later launches. |
| `off` | Disable the application-owned binary store and use only the Vulkan pipeline cache. |
| `capture` | Explicit population mode. A miss uses the capture-data path with a null `VkPipelineCache`, extracts the resulting binaries, and persists them asynchronously. |
| `require` | Consume-only verification mode. Fail if binary support or any requested artifact is missing; never silently compile a missing artifact. |

The store is bounded to 512 MiB, collects least-recently-used mappings, and uses
cross-process locking plus atomic manifest/blob publication.
Set `NJULF_PIPELINE_BINARY_AUTO_CAPTURE=off` to disable the application-cold
capture-on-miss path without disabling binary-store consumption.

Startup prints one `Pipeline cache:` health line with the Vulkan cache source,
provenance, admitted payload size, binary-store mode, and writable/seed mapping
counts. A rejected cache or stale deployment seed also prints actionable advice
to `stderr`.

## Startup compilation and verification

Pipeline owners use one compiler gateway. Telemetry records wall and driver
feedback time, application-cache hits, compile-required results, artifact source
(writable binary, seed binary, Vulkan cache, or compilation), stage count, peak
concurrency, and any creation that escaped into a render-critical frame.

Independent startup families use a bounded worker pool.
`NJULF_PIPELINE_COMPILE_WORKERS` accepts `1` through `8`; the default is
`min(4, max(1, processor-count / 4))`. Active-scene preparation scans visible
material metadata and requests only the masked, transparent, glass,
transmission, decal, receiver-feedback, and ray variants used by that scene.
First-present publication waits for the complete pixel-affecting manifest.
Feedback and partition specializations publish only after their post-present
manifest has completed, so command recording never sees a partially built bank.
Pipeline creation and resource `Ensure` operations are forbidden in the
receiver-feedback command-recording path. Shutdown drains the bounded compiler
before render-graph resources are destroyed. These rules keep render-critical
pipeline creation at zero; bank readiness may change performance, never final
image quality.

Set `NJULF_PIPELINE_CACHE_VERIFY=1` or pass `--pipeline-cache-verify` to add
`VK_PIPELINE_CREATE_FAIL_ON_PIPELINE_COMPILE_REQUIRED_BIT` where supported. A
compile-required result counts as a cache miss and fails qualification at the
owning pipeline. Warm classification requires exact current-build writable data
and zero feedback-backed compile misses.

## Independent latency gates

Each milestone is measured from `Game.Run`:

| Milestone | Cache class | Target | Hard limit |
| --- | --- | ---: | ---: |
| Responsive bootstrap present | Any | 3 s | 5 s |
| Visually qualified final frame | Exact warm writable cache | 5 s | 10 s |
| Visually qualified final frame | Empty cache or exact deployment seed | 15 s | 30 s |

The cache class is evidence, not a command-line label. A compatible writable
cache that reports any feedback-backed compile miss is not an exact-warm run
and must use the empty/incomplete-cache row.

The production-graph present is a control-plane timing only: it does not prove
that the swapchain contains scene pixels. Startup qualification uses the
renderer-owned final-LDR readback, rejects black or uniform bootstrap images,
and reports the timestamp of the captured present as the authoritative final
frame. It also requires zero pipeline creations on the render thread after
first-present preparation. Ordinary launches report only. Set
`NJULF_STARTUP_LATENCY_GATE=enforce` for fatal qualification, `timing` for
report-only behavior, or `off` to disable output.

The earlier 3.170 s RTX 3060 result measured only the production-graph present
and is retracted as a final-frame qualification result. On 2026-08-30, the
Development build was requalified on the NVIDIA GeForce RTX 3060 Laptop GPU
(`vendor=0x10DE`, `device=0x2560`, driver `610.248.0`) from empty writable Vulkan
and explicit-binary cache directories using the packaged device seed. The
production graph presented at 5.444 s and the renderer-owned 1600x900 final-LDR
capture qualified at 6.597 s. Its raw readback contained visible content in
1,382,687 of 1,440,000 pixels (96.02%, luminance range 0..230), and no pipeline
was created on the render thread. The corresponding incomplete-seed baseline
was 39.109 s, so this measured final-frame path is 32.512 s (83.1%) faster. This
is pipeline-cache/ownership work only; no content or shader recook was required.
Machine-readable seed and capture evidence is recorded in
`NjulfHelloGame/pipeline-seed-qualification.json`.

On 2026-09-01, the current ShippingPerformance integration was exercised on the
same RTX 3060 Laptop GPU and driver with Bistro, DdgiHigh, Standard validation,
and a renderer-owned 1600x900 final-LDR capture. The responsive bootstrap
presented in 3.811 s, the production graph in 15.676 s, and the visually
qualified frame in 16.733 s. The run had 16 compile misses, so it is not an
exact-warm result: it misses the 15 s incomplete-cache target but passes the
30 s hard limit. It reported zero validation warnings/errors and zero
render-critical pipeline creations. Post-present receiver specializations and
the receiver compute bank completed after visible scene publication and did not
extend the final-frame milestone. This replaces the multi-minute black startup,
but the soft bootstrap and incomplete-cache targets remain open performance
work.

Startup JSONL can be requested with `--startup-log <path>`. Its throttled
snapshots include active pipeline count, oldest-active duration, and the active
pipeline basename. After two seconds the same progress is shown in the window
title and emitted as a heartbeat every ten seconds; the normal title returns at
`FullQuality`. Smoke automation can
choose which milestone keeps the process alive:

```text
--smoke-mode Startup --smoke-frames 1 --pipeline-startup active-scene \
--startup-wait bootstrap|full-quality \
--pipeline-binary-cache auto|off|capture|require
```

`scene` and `fallback-scene` remain compatibility aliases for `full-quality`.
For startup smoke, that target now waits for a renderer-owned final-LDR capture;
it never invokes the Windows Print Screen or Snipping Tool path. No model,
texture, material, or other asset recook is required.
