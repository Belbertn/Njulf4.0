# Asset Cooker Progress and Loading Performance Implementation Plan

- Date: 2026-08-08
- Revised: 2026-08-12
- Status: proposed; this document does not implement the changes
- Scope: `Njulf.AssetTool cook model|folder|changed`, the progress seam in
  `ModelAssetCooker`, measured cooker-throughput improvements, cooked runtime
  loading in `ContentManager`/`CookedPackage`, terminal formatting, tests, and
  cooked-asset documentation
- Baseline: the current working tree, including incremental cooking,
  generation-qualified transactional publication, texture reuse, package
  signing, meshopt/Zstd cooked sections, strict runtime validation, preferred
  memory-mapped reads, and the mesh LOD algorithm revision in the cook identity
- Delivery rule: progress, cooker throughput, and runtime loading are separate
  milestones and should land as separate reviewable changes. No optimization is
  accepted without a before/after benchmark on the same corpus and machine.

## 1. Required outcome

An asset cook must continuously say what it is doing. A person watching a
terminal and an AI agent reading redirected process output must be able to tell:

- that the cooker started and discovered work;
- which asset is active and its position in a folder cook;
- which major stage is active;
- which material or texture is active during the longest stage;
- whether a texture was cooked, reused, or deduplicated;
- that a long-running stage is still alive;
- whether each asset succeeded, was incrementally skipped, or failed; and
- the final cooked/skipped/failed counts and elapsed time.

The revised plan also covers two distinct meanings of "load speed":

1. **Cook throughput**: how quickly `cook folder|changed` discovers, checks,
   imports, converts, and publishes assets.
2. **Runtime load latency**: how quickly `ContentManager` resolves, validates,
   decodes, uploads, and makes cooked assets ready to use.

These must be measured and reported separately. A faster cooker is not evidence
of a faster game startup, and a warm `ContentManager` cache hit is not evidence
of a faster cold package load.

The performance milestones must:

- remove redundant reads, hashes, database work, and texture analysis before
  adding concurrency;
- allow bounded parallel work without unbounded memory growth or new
  scheduling-dependent package bytes;
- add async preload/cancellation APIs without sending renderer work to an
  arbitrary thread; and
- preserve strict validation, signing policy, immutable-snapshot ownership, and
  transaction safety.

Progress must consist of complete newline-terminated records. Do not implement
an in-place progress bar, spinner, carriage-return updates, ANSI cursor control,
or TTY-only output: those presentations disappear or become unreadable in IDE,
CI, redirected, and agent-captured logs.

The terminal-progress milestone is observability only. It must not change
package bytes, cook hashes, incremental decisions, output publication order,
rollback behaviour, report schema/non-timing content, exit codes, or the existing
final result summaries. Later performance milestones in Phases 4-7 may change
internal scheduling, I/O, and measured timings, but not package bytes, cook
identity, integrity policy, per-asset transaction semantics, or the meaning of
existing synchronous APIs.

## 2. Current implementation audit

### 2.1 CLI behaviour

`Njulf.AssetTool/Program.cs` calls the synchronous cooker and remains silent
until `CookModel` returns. `PrintCookResult` then writes one result line and up
to eight warnings. Folder mode repeats that after every completed asset and
prints a final count.

Consequences:

1. Importing, mesh processing, texture analysis/encoding, serialization, and
   signing can run for a long time with no terminal output.
2. The user cannot distinguish a slow stage from a hung process.
3. An AI agent sees no incremental evidence and cannot identify the asset,
   texture, or stage that is consuming time.
4. A thrown exception produces only the top-level exception type/message; the
   last active cook stage is not visible.

### 2.2 Cooker stages already present

`Njulf.Assets/Cooked/ModelAssetCooker.cs` already has natural progress
boundaries:

1. normalize paths, create output directories, and check output collisions;
2. hash the source/settings/dependencies and evaluate the asset database;
3. import with `ModelImporter.ImportDetailed`;
4. build processed meshes and meshlet LOD payloads;
5. clone/classify materials, read and analyze texture sources, reuse or cook
   textures, and generate primitive transport profiles;
6. serialize mesh/material/animation packages;
7. sign generated artifacts when requested;
8. publish the stable `.njmodel` transaction point;
9. write the cook report and asset database atomically.

The existing `Stopwatch` measurements cover import, mesh, the combined
material/texture work, and serialization. Progress should reuse those results
where applicable and use a separate monotonic run/stage clock for the other
boundaries. It must not add another expensive hash, decode, or directory scan
just to calculate progress.

Emit `stage.start` before starting the existing operation stopwatch, and capture
the operation duration before emitting `stage.done`, so terminal formatting and
flush latency do not become import/mesh/texture/serialization report time. The
separate total wall clock intentionally includes reporter overhead because that
is what the user experiences.

`CookFolder` currently enumerates supported files, sorts them, then cooks them
serially through `CookModel`. Material cooking already walks every material and
texture slot, and its `cookedTextures` dictionary identifies cooked versus
deduplicated work. These are the authoritative places to report counts and
outcomes.

### 2.3 Existing output channels

Successful cook summaries currently go to `stdout`; top-level failures go to
`stderr`. Keep those contracts. Progress belongs on `stderr`, so scripts can
continue to redirect or consume the established result stream without progress
records mixed into it. Redirection must not disable progress.

### 2.4 Cook-throughput audit

The following are code-backed optimization candidates, not assumed bottlenecks.
Phase 4 must measure their contribution before any implementation is selected:

1. `CookFolder` enumerates and sorts once, but then calls `CookModel` serially.
2. Every `CookModel` call recreates output-directory state, serializes the same
   effective settings to compute the settings hash, loads the entire asset
   database, and atomically saves that database after success. In a folder cook,
   the database and run-invariant settings should have run scope.
3. Dependency discovery, texture cooking, manifest construction, signing, and
   final output recording can hash the same immutable file more than once.
   Hash reuse is safe only while tied to one stable snapshot or one finalized
   generation; timestamps alone are not a correctness boundary.
4. `CookMaterials` reads and hashes a texture before it can form the
   `cookedTextures` key. Repeated slots can therefore reread the same source.
   Its transport-image cache is material-scoped, so the same texture can also be
   decoded/analyzed again for a later material. Any broader cache must have a
   byte limit and release decoded pixels after the last consumer.
5. The reflection query that discovers `ModelTextureSlot` properties is repeated
   in material loops and output collection even though that property set is
   process-invariant.
6. Database serialization and output hash verification are intentionally strong
   safety checks. Speed work must reuse proven facts, not replace content hashes
   with length/mtime heuristics.

### 2.5 Runtime-loading audit

The cooked path already avoids source import, runtime texture transcoding, and
meshlet generation, and it already supports compressed sections and memory
mapping. Remaining candidates are in orchestration and duplicate work:

1. `ContentManager.Load` holds `_stateLock` across resolution, source hashing,
   stable-model snapshot capture, package decode, and renderer upload. This
   correctly coalesces duplicate cache misses, but it also serializes unrelated
   model loads and blocks cache/diagnostic operations for the whole load.
2. Strict source-path loading currently hashes the source in
   `CookedContentResolver.ResolveModel`, then hashes it again when
   `LoadResolvedCookedModel` calculates `expectedSourceHash`.
3. Resolution opens the stable `.njmodel` to inspect its header; snapshot capture
   then reopens and reads it, computes SHA-256, and only then can the cooked cache
   be checked. Preserve the replacement/snapshot hardening guarantee while
   reducing this to one immutable capture and one source hash.
4. Mesh, material, and optional animation sidecars are generation-qualified and
   independent after the manifest is authenticated, but `CookedPackage.LoadModel`
   loads them serially.
5. `CookedRuntimePolicy` requests memory mapping, but strict referenced sidecars
   carry an expected whole-file hash, so `OpenAuthenticatedReader` first captures
   each entire sidecar into a managed `byte[]` and uses a memory-backed reader.
   Decoded arrays coexist with that snapshot, and compressed sections currently
   allocate another stored-byte buffer before decode. Direct path readers instead
   create a mapping view per large section and still copy into upload-ready
   arrays. A one-open authenticated-handle reader could preserve TOCTOU safety
   while avoiding the full managed sidecar snapshot; the best direct/mapped/
   buffered strategy needs size- and compression-based evidence.
6. Large material and animation payloads are Zstd-compressed JSON. Packed binary
   metadata might reduce decode allocations, but it requires a format-versioned
   migration and is justified only if profiling identifies JSON as material.
7. `UploadCookedModelCore` slices each combined cooked mesh stream with
   `ToArray`, separately copies LOD0/1/2 meshlets, then concatenates those LOD
   arrays into another allocation before mesh registration. This can duplicate
   much of the decoded mesh working set immediately before GPU upload.
8. `TextureManager.LoadTexture` deliberately reads and hashes source bytes before
   its cache lookup to preserve editor hot-reload correctness. For cooked
   materials this also authenticates the sibling `.njtex` and KTX2 content before
   every slot-level lookup, so repeated cooked slots can incur repeated file
   reads/hashes/metadata decode even when they resolve to one GPU texture.
9. `ModelRenderUploadService.UploadCookedModel` holds its lifecycle lock across
   material texture I/O/authentication, per-submesh CPU copies, and GPU resource
   registration. Async CPU loading must move safe preparation outside that lock,
   while actual renderer mutation remains owner-serialized.
10. Loading is synchronous. There is no first-class preload group, priority,
    cancellation, byte-budget backpressure, or typed load-progress contract.
    `IModelRenderUploadService` is also synchronous, so an async API must split
    CPU read/decode from renderer-thread upload rather than wrapping the whole
    call in `Task.Run`.

## 3. Terminal and automation contract

### 3.1 Modes

Add the following options to all three `cook` modes:

```text
--progress <plain|jsonl|off>
--progress-detail <stages|items>
```

- `plain` is the default, whether or not `stderr` is attached to a TTY.
- `jsonl` writes the same events as one JSON object per line for automation.
- `off` suppresses progress and heartbeats, but not final summaries, warnings,
  or errors.
- `items` is the default detail and includes material/texture records.
- `stages` emits run/asset/stage records only, while still tracking the current
  item internally so a heartbeat can name a slow texture. It is intended for
  large CI cooks where per-item history would be excessive.

Do not add an `auto` mode that silently disables or changes the presentation
when output is redirected. Consistent newline records are the feature that
makes the output useful to agents and CI.

Update `PrintUsage` and reject unknown values through the existing argument
error path. Progress mode and detail are presentation state and must never enter
`ModelCookOptions`, the settings hash, reports, or asset database.

### 3.2 Plain records

Use a stable `[assetcook]` prefix, an event name, and invariant-culture
`key=value` fields. Quote and escape user-controlled values such as paths and
texture names. Example:

```text
[assetcook] run.start run_id=4f2c mode=folder source="Content" output="Cooked/win-x64" platform=win-x64
[assetcook] discovery.start run_id=4f2c source="Content"
[assetcook] discovery.done run_id=4f2c assets=12 total_elapsed_ms=4
[assetcook] asset.start run_id=4f2c asset=3/12 source="Models/Sponza.gltf"
[assetcook] stage.start run_id=4f2c asset=3/12 stage=incremental-check total_elapsed_ms=18
[assetcook] incremental.done run_id=4f2c asset=3/12 decision=cook reason=dependency-changed stage_elapsed_ms=7
[assetcook] stage.done run_id=4f2c asset=3/12 stage=incremental-check stage_elapsed_ms=7 total_elapsed_ms=25
[assetcook] stage.start run_id=4f2c asset=3/12 stage=import total_elapsed_ms=25
[assetcook] stage.done run_id=4f2c asset=3/12 stage=import stage_elapsed_ms=1842 total_elapsed_ms=1867 backend=SharpGltf
[assetcook] stage.start run_id=4f2c asset=3/12 stage=materials-textures total_elapsed_ms=1891 materials=24 texture_slots=61
[assetcook] texture.start run_id=4f2c asset=3/12 slot=17/61 name="roughness.png"
[assetcook] heartbeat run_id=4f2c asset=3/12 stage=materials-textures item="roughness.png" stage_elapsed_ms=10000 total_elapsed_ms=11910
[assetcook] texture.done run_id=4f2c asset=3/12 slot=17/61 outcome=reused item_elapsed_ms=11244
[assetcook] asset.done run_id=4f2c asset=3/12 outcome=succeeded asset_elapsed_ms=28457 total_elapsed_ms=30001 meshes=31 textures=38 warnings=2
[assetcook] run.done run_id=4f2c outcome=succeeded assets=12 cooked=8 skipped=4 failed=0 total_elapsed_ms=91022
```

Requirements:

- exactly one event per physical line;
- flush `stderr` after every event;
- no `\r`, escape sequences, terminal-width dependence, or Unicode glyphs
  whose meaning is lost in plain logs;
- integer milliseconds and invariant formatting;
- paths displayed relative to the requested source/output roots when possible,
  while the underlying event retains the full source path;
- one opaque, process-local `run_id` on every rendered record so interleaved CI
  logs can be correlated; it is diagnostic state and never cook identity;
- unambiguous duration fields: `stage_elapsed_ms`, `item_elapsed_ms`,
  `asset_elapsed_ms`, and `total_elapsed_ms`; do not overload `elapsed_ms` with
  different meanings;
- monotonically increasing `sequence` in JSONL (plain output may omit it); and
- a schema/version field in JSONL so consumers can reject an incompatible
  future event format.

### 3.3 JSON Lines records

JSONL uses fixed property names and enum strings, not a serialized arbitrary
message. A representative event is:

```json
{"schema":1,"runId":"4f2c","sequence":14,"event":"stage.start","assetIndex":3,"assetCount":12,"sourcePath":"D:/Content/Models/Sponza.gltf","stage":"materials-textures","totalElapsedMs":1891,"materialCount":24,"itemCount":61}
```

Every line must parse independently. Optional properties are omitted rather
than written with invented sentinel values. Human-readable `message` may be
included for failures, but consumers must be able to act on `event`, `stage`,
`outcome`, and numeric fields without parsing prose.

JSONL remains on `stderr`. A direct invocation of the built executable gives a
pure event stream; `dotnet run` may still prepend its own build diagnostics.

### 3.4 Heartbeat and verbosity policy

Emit an initial event before each potentially expensive operation and a
heartbeat after ten seconds without another progress record. Continue one
heartbeat every ten seconds until the stage or item changes. A heartbeat names
the current asset, stage, current material/texture when known, stage elapsed
time, and total elapsed time.

The heartbeat belongs to the terminal reporter, not the cook algorithm. It
tracks the most recent typed event with a monotonic clock and does not poll or
inspect cooker internals. Synchronize normal event and timer writes so records
cannot interleave. Stopping/disposing the reporter must stop the timer and
guarantee that no late heartbeat is written after `run.done`, `run.failed`, or
`run.cancelled`.

Bound normal output to meaningful work units:

- one start and completion event per asset stage;
- one start and completion event per material;
- one start and completion event per occupied material texture slot;
- identify a slot outcome as `cooked`, `reused`, or `deduplicated`;
- one material completion event after its primitive transport profile work;
- no per-mip, per-meshlet, per-vertex, or per-file-byte events.

This provides enough detail to locate slow texture work without turning a large
model cook into unbounded low-level trace noise.

### 3.5 Incremental, queue, and estimate records

Progress should explain *why* work happens, not merely that it happens. Complete
the incremental-check stage with `incremental.done decision=skip|cook` and one
stable reason enum such as `unchanged`, `forced`, `database-miss`,
`previous-status`, `source-changed`, `settings-changed`, `dependency-changed`,
`tool-changed`, `output-missing`, or `output-hash-mismatch`. Report the first
authoritative reason encountered in the existing decision order; do not add
extra hashes solely to enumerate every possible reason.

When bounded parallel folder cooking is added later, JSONL may also include
`queuedAssets`, `activeAssets`, and `completedAssets` on heartbeat/run records.
Normal asset events remain indexed by the original sorted discovery order even
when completion order differs. Plain output should not print a separate queue
line on every scheduler transition. `run.start` should include effective jobs
and in-flight-byte budget so a performance log is reproducible.

Do not promise an ETA or overall percentage in the initial milestone. Stage
durations and item denominators are factual; an estimate is not. A later
`estimatedRemainingMs` field may be added without changing schema 1 only when it
is optional, explicitly marked as an estimate, derived from compatible prior
cook reports, and omitted when there is no trustworthy history. Never delay a
cook or scan inputs again to calculate it.

## 4. Progress API and ownership

### 4.1 Typed library seam

Add `Njulf.Assets/Cooked/AssetCookProgress.cs` containing:

- `AssetCookProgressEventKind` for discovery, asset, stage, incremental decision,
  material, texture, skip, success, failure, and reserved cancellation
  transitions;
- `AssetCookStage` with stable names for `prepare`, `incremental-check`,
  `import`, `mesh`, `materials-textures`, `serialize`, `sign`, `publish`, and
  `report-database`;
- `AssetCookProgressOutcome` for `succeeded`, `skipped`, `failed`, `cooked`,
  `reused`, and `deduplicated` where applicable, plus a reserved `cancelled`
  outcome that Phase 6 begins emitting;
- an immutable `AssetCookProgressEvent` carrying typed optional fields such as
  source path, asset index/count, item index/count/name, decision reason,
  stage/item/asset/total elapsed milliseconds, counts, backend, and failure
  message; and
- `IAssetCookProgressSink.Report(AssetCookProgressEvent progress)`.

Append an optional progress sink to the public `CookModel` and `CookFolder`
methods, preserving all existing source-compatible call sites. Route both
methods through a private cook context so a single-model cook reports `1/1` and
a folder cook reports the sorted asset index and fixed total without producing
nested run events.

The library owns facts about cook work and emits typed events. It must not
reference `Console`, terminal capabilities, JSONL formatting, heartbeat timers,
or CLI options. With a null sink, behaviour and outputs remain unchanged and
the hot loops do not allocate progress strings.

The sink contract is synchronous so event order matches actual cook order.
Document that custom sinks must return quickly. Phase 1 may invoke it from only
one thread; the bounded-parallel milestone changes the contract to permit
concurrent calls for different assets while preserving per-asset order. The
AssetTool sink must serialize those calls and contain its own output errors so a
closed diagnostic stream cannot interrupt package publication.

### 4.2 Folder/run ownership

Materialize the sorted supported-file list once at the start of `CookFolder`.
This supplies a trustworthy total and preserves the current deterministic
order. Emit discovery completion for zero as well as non-zero assets.

`Program.RunCook` owns the command-level `run.start` and terminal
`run.done|failed|cancelled` presentation because it knows whether the user
selected `model`, `folder`, or `changed`, the requested roots, and the final
aggregate counts. `ModelAssetCooker` owns asset, stage, material, texture, skip,
failure, and later cancellation events.

Keep current fail-fast folder semantics. If an asset throws, emit
`asset.failed` and then rethrow the original exception after the existing
rollback logic. `Program` emits `run.failed`; `Main` continues to print the
existing exception and return the existing non-zero exit code. This plan does
not introduce continue-on-error or fabricate a persisted failed cook report.

### 4.3 Instrumentation points

Instrument `CookModel` around the existing operations rather than estimating a
single misleading percentage:

1. `prepare`: path validation, output directory setup, collision check;
2. `incremental-check`: source/settings/dependency hashing, database lookup,
   and output hash verification;
3. emit `asset.skipped` immediately on the current early-return path;
4. `import`: `ImportDetailed` through `EnsureImported`, including backend and
   diagnostic count on completion;
5. `mesh`: processed mesh and cooked meshlet LOD construction;
6. `materials-textures`: `CookMaterials`, with material/texture sub-events;
7. `serialize`: mesh, material, optional animation, and staged model writes;
8. `sign`: only when a signing key is configured;
9. `publish`: signature swap and stable model move;
10. `report-database`: output hashing, atomic report write, and atomic database
    save;
11. `asset.done` only after the database commit succeeds.

Track the current stage in the private context. The outer exception path emits
one failure event containing that stage after rollback attempts finish. If
rollback also fails, report the aggregate failure text while preserving the
existing `AggregateException` behaviour.

For `CookMaterials`, calculate the occupied texture-slot count from the cloned
materials before starting the loop. Increment the slot index in the existing
reflection traversal; do not pre-read or pre-hash texture bytes merely to find
a deduplicated total. Report:

- `texture.start` before stable source reading/reuse analysis/cooking;
- `texture.done outcome=cooked` after KTX2 and `.njtex` publication;
- `texture.done outcome=reused` when `TryReuseCookedTexture` succeeds;
- `texture.done outcome=deduplicated` when the in-memory key already exists;
- `material.start` before traversing that material's texture slots; and
- `material.done` after primitive transport profile generation for that
  material.

The slot count is a determinate user-visible denominator even when several
slots resolve to one cooked texture. The final report's unique `TextureCount`
remains authoritative and unchanged.

## 5. Terminal reporter

Add `Njulf.AssetTool/AssetCookTerminalProgress.cs` with one owner for:

- plain and JSONL serialization;
- process-local run-id creation and progress-detail filtering;
- relative display paths and safe value escaping;
- sequence assignment;
- monotonic run/stage/item timing;
- synchronized writes and explicit flushes;
- the ten-second heartbeat timer; and
- final disposal.

Construct it in `RunCook` after arguments are valid and before invoking the
cooker. Wrap the cook invocation so exactly one terminal run outcome is emitted
for success or failure, with cancellation added in Phase 6. Keep
`PrintCookResult` and the existing folder summary unchanged on `stdout`;
progress is additional context, not a replacement for the concise completion
contract.

Use `TimeProvider` (or an equally injectable monotonic clock/timer seam) so
heartbeat behaviour is unit-testable without real ten-second sleeps. Avoid a
third-party terminal UI package: it adds no value to a line-oriented contract
and risks reintroducing redirected-output differences.

The reporter must update its current item even when `--progress-detail stages`
filters the corresponding item record, otherwise the next heartbeat would lose
the most useful diagnostic. `--progress off` should avoid constructing the
reporter and pass a null sink so disabled progress has no timer, formatting, or
hot-loop allocation cost.

## 6. Performance and loading extensions

### 6.1 Measurement contract

Performance work starts with a repeatable harness, not a concurrency switch.
Record at least three runs after one warm-up and compare medians on the same
machine, power mode, build configuration, corpus, and output device. Keep raw
samples so variance is visible.

Measure these cooker cases independently:

1. **No-op changed cook**: all assets and outputs current.
2. **One dependency changed**: one shared texture changed, with affected and
   unaffected models present.
3. **Cold serial folder cook**: empty output root, `--jobs 1`.
4. **Cold bounded-parallel folder cook**: same input and fresh output root.

Measure these runtime cases independently:

1. **Cold process load**: new process and new `ContentManager`; report resolve,
   source-hash, stable-manifest capture, sidecar read, validation/decompression,
   unique cooked-texture capture/authentication, CPU object/view construction,
   renderer-lock wait, renderer upload, and ready-to-use latency.
2. **Warm OS-cache load**: new `ContentManager`, same package bytes likely in the
   operating-system cache.
3. **Manager cache hit**: same manager and unchanged package identity.
4. **Concurrent distinct models** and **concurrent same model**: prove overlap
   for independent work and single-flight coalescing for duplicate work.

Capture wall time, CPU time, bytes read/written, file-open count, hash count and
bytes hashed, texture decode/analysis count, managed allocations, peak managed
and process memory, GC counts, cooked texture authentication count/bytes,
per-submesh CPU copy bytes, and renderer upload time. Counters used solely for
benchmarking must be cheap and disabled or compiled out in normal builds.

An optimization ships only if its target median improves beyond run-to-run noise
and no non-target representative case regresses by more than 5 percent without a
documented tradeoff. Peak memory, integrity checks, and package determinism are
co-equal gates; wall-clock speed alone is insufficient.

### 6.2 Safe serial cooker improvements

Implement and benchmark these before parallel cooking, in this order:

1. Introduce a run-scoped cook session for `CookFolder`: normalize/create output
   directories once, compute the effective texture options and settings hash
   once, load the database once, and reuse cached `ModelTextureSlot` property
   metadata. Keep `CookModel` as a one-asset session for API compatibility.
2. Keep the current recovery boundary by merging an entry and atomically
   checkpointing the shared database after each successfully published asset.
   A later batched-checkpoint experiment is allowed only if benchmarks show the
   save is material and the documented crash behaviour is intentionally changed;
   a lagging database may cause safe recooking but must never claim unpublished
   outputs.
3. Add an asset-bounded stable-source snapshot cache keyed by canonical source
   identity plus captured content hash, and a separate derived-analysis cache
   keyed by the complete texture-work key. One stable byte snapshot should
   supply the source hash, texture cooker, and compatible transport analyses for
   repeated slots in that asset. It must detect a file changing during capture,
   never trust length/mtime as content identity, never survive into a later
   command invocation, and obey a conservative byte cap with deterministic
   eviction. Cross-model reuse is deferred to Phase 6's single-flight operation,
   where all consumers are tied to the same captured version.
4. Count remaining consumers of each decoded transport image. Reuse the image
   across materials in the same asset when the full semantic/color-space/options
   key matches, then release it after its last primitive-profile consumer. A
   cache hit must not retain source-resolution pixels in reports or create
   hundreds of MiB of hidden long-lived state.
5. Reuse final-artifact hash facts. A completed writer should return a receipt
   containing finalized path, byte length, and whole-file cook hash; manifest
   references and database output records should consume that receipt instead
   of reopening the same generation. Invalidate the receipt on overwrite/move
   unless the move is the transaction's identity-preserving publication step.
   Signing still uses its required cryptographic digest and must not substitute
   the non-cryptographic cook hash; when safe, compute both during the same final
   immutable-file pass.
6. Avoid rereading a skipped cook report when the database contains every field
   needed for a concise skip result only if the public `AssetCookResult` and
   final summary can remain identical. Otherwise retain the report read; do not
   trade contract fidelity for a micro-optimization.
7. Benchmark progress overhead separately. `--progress off` plus a null sink
   should be statistically indistinguishable from the pre-progress serial
   baseline. Report the cost of default item-level flushed output; if it is
   material, retain correctness and recommend `--progress-detail stages` for
   throughput-sensitive automation instead of weakening record delivery.

Each cache must expose hit/miss/eviction and bytes-retained counters to the
benchmark harness. Do not introduce a persistent cache in this milestone.

### 6.3 Bounded parallel folder cooking

Only after the serial work is measured, add to `folder` and `changed`:

```text
--jobs <1..N|auto>
--max-inflight-bytes <bytes>
```

Start with a default of `1`. `auto` is explicit and chooses a conservative cap
from processor count and the resource governor; changing the default to `auto`
is a later product decision backed by CI and representative-memory evidence.
The in-flight byte default is a documented constant chosen from Phase 4 evidence
and is printed on `run.start`; both options are run scheduling state and never
enter the cook/settings identity.

Represent these library-side settings in a separate immutable
`AssetCookBatchOptions` (degree of parallelism and byte budget), not in
`ModelCookOptions`. Append it and a conventional `CancellationToken` to a new
source-compatible `CookFolder` overload; keep the existing overload delegating
to jobs `1`. Add only a cancellation token to the one-model overload. Progress
sink, batch options, and cancellation are operational state and must never be
serialized into reports, the asset database, or cook hashes.

Parallel-cook requirements:

1. Materialize and sort inputs, then preflight model-output stem collisions
   before starting workers. Asset indexes always refer to this stable order.
2. Use worker-local `ModelAssetCooker` dependencies/importers; none of the
   current importer/texture-cooker objects should be presumed thread-safe.
3. Put database mutation and atomic checkpoints behind one run coordinator.
   Return results in discovery order even though progress and completion may be
   interleaved. Emit `asset.done` only after that asset's database entry is
   durably checkpointed.
4. Use single-flight publication for identical texture-work keys. Concurrent
   consumers await one producer and then validate/reuse its immutable artifact;
   they must not race writing the same `.ktx2`/`.njtex` paths.
5. Bound both worker count and large in-flight source/decoded texture bytes.
   When a byte reservation cannot be estimated safely, treat the work as large
   and serialize it. A single item larger than the soft byte budget may run alone
   with an explicit oversize diagnostic, so the scheduler cannot deadlock.
   Never rely on the GC or an out-of-memory exception as backpressure.
6. Preserve fail-fast semantics. On the first failure, stop admitting queued
   assets, request cancellation of cooperative work, allow active publication
   transactions to finish or roll back, checkpoint already successful assets,
   then rethrow the original failure. Do not abandon worker tasks or dispose
   shared state beneath them.
7. Do not introduce concurrency-dependent bytes. The current production cook
   intentionally uses random generation-qualified sidecar names and real
   timestamps, so cross-run tests must either inject fixed generation/time seams
   or compare payload bytes and normalize only those pre-existing variable
   fields. With fixed seams, `--jobs 1`, `--jobs 2`, and `--jobs auto` must be
   byte-identical; without them, content identity and referenced output sets must
   be semantically equivalent.
8. Do not add within-model mesh/texture parallelism until profiles show that
   folder-level concurrency leaves material CPU idle. Native encoders may
   already use their own threads.

Add cooperative Ctrl+C handling with this milestone because a parallel run
needs an orderly stop path. Append optional `CancellationToken` parameters
without breaking existing calls, check between stages/materials/textures, and
let opaque native work return before rollback. Completed asset checkpoints make
the next `cook changed` the resume mechanism; do not add mid-asset checkpoints.
Render `asset.cancelled`/`run.cancelled` and use exit code 130 for an actual
Ctrl+C cancellation. All pre-existing non-cancellation exit codes remain
unchanged.

### 6.4 Runtime fast path and asynchronous preload

Refactor runtime loading as an explicit pipeline:

```text
resolve -> capture/validate manifest -> read/decode sidecars -> construct CPU asset -> renderer upload -> ready
```

Required changes:

1. Make resolution return the already computed expected source hash and parse
   the header from the same bounded stable-model snapshot later used by
   `CookedPackage.LoadModel`. Preserve the test that replacement after snapshot
   capture cannot publish under the original identity. Do not introduce an
   mtime-only cache shortcut.
2. Replace the global load-duration lock with two-level single-flight ownership:
   a normalized-request gate coalesces concurrent resolution/source-hash/snapshot
   capture, then the snapshot SHA/reader-policy/source identity forms the existing
   content key for decode/upload ownership. Release the request gate after the
   immutable identity is known so a later replacement can still be discovered.
   Keep only short locks for cache publication, diagnostics, unload, and
   disposal. Same-content misses produce one decoded/uploaded owner; different
   content keys may overlap CPU work. Disposal must stop admission, await/cancel
   in-flight work, and retain the current retryable ownership guarantees. One
   follower's cancellation cancels only its wait; shared work is cancelled only
   when no interested caller remains or manager disposal requires it.
3. After authenticating the manifest, read/decompress generation-qualified mesh,
   material, and animation sidecars concurrently with a small I/O/decode limit.
   Maintain deterministic validation and exception context; if several branches
   fail, surface the earliest manifest-order failure and retain the others as
   inner diagnostics. On a single slow disk, the benchmark may select sequential
   reads as the faster policy.
4. Benchmark reader strategies by asset size and compression: the existing
   bounded whole-file snapshot, a one-open authenticated `SafeFileHandle` with
   direct `RandomAccess`, one reused memory-mapped view, and batched reads of
   adjacent stored sections. The handle path must parse/retain the original
   header/table, compute the expected whole-file hash and required signature from
   that same read-only handle opened without write/delete sharing, then verify
   every decoded section against the retained table; path replacement or later
   bytes must never enter the load.
   For an in-memory snapshot, decompress directly from its stored section slice
   rather than first copying encoded bytes. Select thresholds from measurements,
   not from the presence of `PreferMemoryMapped` alone.
5. Remove the decoded-mesh double copy. Add a cooked range/view registration
   path that consumes validated `ReadOnlyMemory<T>` slices (or one whole payload
   plus ranges) through staging upload without allocating per-submesh stream and
   LOD arrays. Where cooked and GPU structs differ, either prove layout
   compatibility with size/offset tests or convert directly into mapped staging
   memory; never reinterpret merely because fields look similar. The uploader
   may retain those views only until the staging copy is durably owned; rollback
   and `CookedModelAsset` lifetime must remain explicit.
6. Build a unique cooked-texture dependency plan from the authenticated material
   table. Capture/authenticate each `.njtex` plus KTX2 pair once per complete
   runtime contract, single-flight identical work across concurrent models, and
   pass an immutable authenticated receipt/byte snapshot to renderer upload.
   `TextureManager` must gain a cooked-receipt path rather than weakening its
   existing source/hot-reload path. Metadata, path, format, slot contract,
   signature, and full KTX2 content hash remain mandatory, and retained encoded
   bytes count against the preload byte budget until upload releases them.
7. Move manifest-derived mesh views and texture authentication/preparation out of
   `ModelRenderUploadService`'s lifecycle critical section. Keep GPU/material/
   texture publication and rollback under the renderer's existing ownership
   serialization; measure lock wait and lock hold time separately.
8. Add `LoadAsync<T>` and `PreloadAsync` without calling renderer APIs on a pool
   thread. Expose them through new companion async/preload interfaces implemented
   by `ContentManager`; do not add abstract members to the existing
   `Njulf.Core.Interfaces.IContentManager` and break third-party implementations.
   DI should resolve the same manager instance for all three contracts. CPU
   capture/read/decode may run on bounded workers; renderer upload goes through
   an explicit upload dispatcher or renderer-owned queue and is included in the
   returned task's completion.
9. `PreloadAsync` accepts a stable asset list, bounded priority, maximum
   concurrency, byte budget, cancellation token, and typed progress sink. Keep
   FIFO order within a priority and include starvation prevention. It reports
   resolve, read, decode, upload, ready, cache-hit, and failure transitions plus
   completed/total assets and bytes when known. A single asset above the soft
   byte budget runs alone with an oversize diagnostic. Cancellation before
   publication disposes partial CPU state; cancellation during/after upload
   follows renderer rollback rules and never leaves an unowned GPU resource.
   The group is fail-fast but not atomic: assets already published remain normal
   manager-owned cache entries and are listed in `ContentPreloadResult`; pending
   and unpublished work is cancelled/cleaned. Do not tear down an asset that a
   concurrent ordinary load has already acquired merely to roll back the group.
10. Preserve the synchronous `Load<T>` contract. It should share validation and
    cache logic with the async pipeline, not become a blind `.GetResult()` wrapper
    around a dispatcher that may require the calling/render thread to make
    progress. A synchronous load encountering same-key async preparation must
    either take/execute publication on the valid owner context or use an already
    safe completion path; it must not wait on work queued back to itself.
11. Extend cooked diagnostics with cold/warm/cache-hit classification, resolve,
    source-hash, manifest, sidecar and texture read/authentication/decode,
    mesh-copy bytes avoided, upload, lifecycle-lock wait/hold,
    wait-for-single-flight, total bytes read, and peak in-flight bytes. Keep
    diagnostic collection bounded.

Define the runtime progress contract separately from asset-cook JSONL. It should
be an immutable `ContentLoadProgressEvent` in `Njulf.Assets` with request id,
asset index/count, normalized path, stage, cache/single-flight outcome, known
bytes, elapsed times, and bounded failure detail. The library must not reference
`Console` or a UI framework; the game/editor may translate it to a loading screen
or log. Same-key followers report that they are waiting and then share the
owner's terminal outcome rather than fabricating duplicate read/upload work.
Never invoke user progress callbacks while holding content-cache or renderer
lifecycle locks. Preserve per-request order, permit documented interleaving
between assets, and contain callback failures so diagnostics cannot corrupt load
ownership.

### 6.5 Additional loading features and boundaries

| Feature | Decision in this plan | Reason |
| --- | --- | --- |
| Same-key single-flight | Include | Prevents duplicate decode/upload while allowing unrelated loads to overlap. |
| Async load and preload groups | Include | Enables startup orchestration without blocking the caller during CPU I/O/decode. |
| Priority and byte-budget backpressure | Include | Makes preload useful without turning startup into an unbounded memory spike. |
| Cancellation and typed load progress | Include | Required for responsive tools and explainable long startup loads. |
| Cooked asset catalog/index | Conditional follow-on | Could avoid path probing and provide dependency/byte totals, but it creates a new atomic publication/identity boundary. Add only after resolver measurements and a separate format design. |
| Packed binary material/animation metadata | Conditional follow-on | Potential CPU/allocation win that requires format migration; profile JSON first. |
| Move large optional OMM data to a sidecar | Conditional follow-on | Keeps stable manifest snapshots small, but changes model format and signing/reference rules. Measure real OMM package sizes first. |
| Mip/mesh-LOD streaming and partial residency | Defer to a separate plan | Requires new package chunking, renderer residency, fallback LOD, and eviction semantics; it is not an incremental loader refactor. |
| Scene bundles, archives, remote/CDN loading | Defer | Requires new addressing, packaging, trust, and patching contracts. |
| Automatic hot reload/file watching | Defer | Needs debouncing, dependency invalidation, safe GPU replacement, and editor ownership rules. |
| Placeholder models on failure | Defer | Changes gameplay/product error semantics; explicit failure remains safer here. |

## 7. Implementation phases

### Phase 0: lock the output contract in tests

1. Add formatter tests for the exact event names and required fields.
2. Prove plain records contain one trailing newline, no carriage return, no
   ANSI escape, invariant numbers, and safely escaped paths/names.
3. Parse every JSONL record with `System.Text.Json` and assert schema version,
   run id, sequence order, enum strings, unambiguous duration fields, and
   omission of absent optional fields.
4. Add fake-clock heartbeat tests for silence, reset-on-progress, periodic
   emission, and no emission after disposal/final outcome.
5. Prove `stages` detail suppresses item records but retains the current item in
   heartbeats, while `off` creates no timer or sink.

Exit criteria: the human/AI-visible protocol is executable test data before
cooker instrumentation begins.

### Phase 1: add ordered cooker events

1. Add the typed progress contracts in `Njulf.Assets.Cooked`.
2. Refactor `CookModel` into a public wrapper plus private context-aware core,
   leaving the transaction body and cleanup rules intact.
3. Materialize and index folder inputs in their existing ordinal-insensitive
   sorted order.
4. Instrument every stage boundary and emit the authoritative incremental
   `skip|cook` decision/reason without duplicating decision work.
5. Pass the private progress context into `CookMaterials` and instrument
   material/texture work and reuse outcomes.
6. Emit the failure event after rollback, then preserve the original throw.

Exit criteria: a recording sink observes a valid ordered lifecycle for a
successful, skipped, empty-folder, and failed cook, while a null sink produces
the same artifacts and semantically equivalent reports as before; fixed
generation/time test seams provide exact byte comparisons.

### Phase 2: wire terminal output and CLI options

1. Add `AssetCookTerminalProgress` and its plain/JSONL/off and stages/items
   selections.
2. Parse `--progress` and `--progress-detail` in `RunCook` and update all cook
   usage lines.
3. Emit command-level run events and feed the sink to `ModelAssetCooker`.
4. Preserve final `stdout` summaries and top-level failure/exit behaviour.
5. Explicitly flush every progress line and serialize timer/main-thread writes
   through one lock.

Exit criteria: direct and redirected invocations show the same ordered progress
records; `--progress off` restores the previous progress-free stream.

### Phase 3: integration tests and documentation

1. Add a CLI subprocess test using the existing concurrent stdout/stderr drain
   pattern in `AssetValidationTests`; never read one redirected pipe to EOF
   before draining the other.
2. Cook a minimal fixture and assert progress is on `stderr`, final results are
   on `stdout`, and the process exits successfully.
3. Re-run the fixture to assert `asset.skipped` and correct aggregate counts.
4. Exercise `plain`, `jsonl`, and `off`, including a source/output path with
   spaces.
5. Add a malformed/unsupported input test proving the last stage and
   `asset.failed`/`run.failed` are visible before the normal non-zero exit.
6. Update `docs/CookedAssets.md` with the default behaviour, channel contract,
   options, duration-field meanings, examples, and a note that progress is
   deliberately newline-based for CI and AI-agent visibility.

Exit criteria: documentation and subprocess evidence match the unit-tested
protocol.

### Phase 4: establish performance baselines

1. Add the cooker and runtime scenarios from section 6.1 to the existing
   benchmark/integration infrastructure; do not optimize in this phase.
2. Add opt-in counters for file opens, bytes read/written/hashed, hash calls,
   texture source captures, transport analyses, section decompressions,
   cooked texture authentications, per-submesh copy bytes, allocations, renderer
   lock wait/hold, and pipeline stage time.
3. Record serial/no-progress, serial/plain-progress, cold runtime, warm runtime,
   manager-cache-hit, same-key concurrency, and distinct-key concurrency
   baselines on a small deterministic fixture and a representative production
   corpus.
4. Store machine/build/corpus metadata and raw samples with the benchmark
   result so later comparisons are auditable.

Exit criteria: the plan's suspected duplicate work is confirmed or rejected by
numbers, and each proposed optimization has a named metric it intends to move.

### Phase 5: remove redundant serial work

1. Add the run-scoped cook session while retaining the public one-model API and
   per-asset atomic database checkpoint.
2. Cache invariant texture-slot metadata and stable input/hash facts with a hard
   byte budget.
3. Reuse decoded transport images until their last known consumer, then release
   them deterministically.
4. Return/reuse finalized artifact receipts instead of rehashing immutable
   generation files where the benchmark proved duplication.
5. Add mutation-during-read, cache-eviction, and bounded-memory tests before
   enabling each cache.
6. Re-run every Phase 4 case and retain only changes that pass section 6.1's
   speed, memory, integrity, and non-regression gates.

Exit criteria: `--jobs 1` is measurably faster in at least one targeted case,
does not regress the others beyond the stated gate, and, under fixed test
generation/time seams, produces byte-identical artifacts plus equivalent
reports/database entries.

### Phase 6: bounded folder parallelism and cancellation

1. Add `--jobs`, `--max-inflight-bytes`, worker-local cook services, preflight
   collision detection, the run coordinator, and deterministic result indexing.
2. Add texture-key single-flight publication and a test hook proving one
   producer for many concurrent consumers.
3. Add worker and in-flight-byte limits; exercise one oversized texture and many
   small textures under a deliberately small test budget.
4. Add cooperative cancellation/ Ctrl+C handling, terminal cancelled outcomes,
   safe worker draining, rollback, checkpoint, and exit-code tests.
5. Stress repeated `--jobs 1|2|auto` cooks using fixed generation/time seams for
   byte comparisons; also compare production-mode payloads, manifest references,
   output hashes, and sorted database content after normalizing only the existing
   generation names and timestamps.
6. Compare HDD-like serialized-I/O and SSD-like parallel-I/O test policies where
   the environment supports them; keep `1` as default until production evidence
   supports a change.

Exit criteria: bounded parallelism improves the representative cold folder cook,
obeys configured reservation bounds apart from one explicitly reported exclusive
oversize item and documented fixed overhead, and every injected
failure/cancellation leaves only complete published assets or recoverable
unreferenced generations.

### Phase 7: runtime async load and preload

1. Unify resolver/header parsing, source identity, and stable manifest capture so
   they use one source hash and one immutable model snapshot.
2. Introduce per-key in-flight ownership and narrow state locks while retaining
   same-key coalescing, replacement hardening, retryable unload, and disposal
   guarantees.
3. Add bounded independent-sidecar read/decode and benchmark-selected reader
   strategies.
4. Add range-aware cooked mesh registration and unique authenticated cooked
   texture receipts; prove per-submesh copy bytes and repeated slot reads/hashes
   are removed without changing renderer ownership or hot-reload semantics.
5. Move safe CPU preparation outside the renderer lifecycle lock and add the
   upload dispatcher contract, `LoadAsync<T>`, `PreloadAsync`, priority, byte
   budget, cancellation, and typed progress.
6. Keep synchronous `Load<T>` and run the existing renderer/content integration
   tests against both shared pipeline paths.
7. Add fault injection at every pipeline transition and verify ownership cleanup
   for read, validation, decode, queue, upload, publication, cancellation, clear,
   and dispose races.

Exit criteria: unrelated CPU loads overlap, duplicate loads coalesce, renderer
uploads run only on their owner-approved context, strict validation is unchanged,
and at least one cold/warm startup target improves under section 6.1's gate.

### Phase 8: conditional format/index follow-ons

Use Phase 4/7 evidence to decide separately whether to design a cooked catalog,
packed metadata, or an OMM sidecar. Each accepted item requires its own format
version, compatibility/migration plan, transactional publication point, signing
rules, corruption tests, and benchmark. Do not silently fold any of them into
the progress or async-loader changes.

Exit criteria: each item is either rejected with benchmark evidence or moved to
a dedicated approved implementation plan. Phase 8 is not required to ship
Phases 0-7.

## 8. Test matrix

| Scenario | Required evidence |
| --- | --- |
| `cook model`, no textures | `1/1`; all applicable stages ordered; success progress and final stdout summary are both present |
| Textured model | occupied-slot denominator; cooked/reused/deduplicated outcomes; unique final texture count unchanged |
| Incremental second cook | prepare/check followed by `decision=skip reason=unchanged`; no import/mesh/texture stages |
| Incremental miss variants | first authoritative reason is stable for force, source/settings/dependency/tool/status/output changes without duplicate checks |
| `--progress-detail stages` | item lines suppressed; heartbeat still identifies current material/texture |
| Folder with multiple models | fixed total, deterministic indexes, one aggregate completion |
| Empty folder | discovery start/done with `assets=0`, successful run completion, existing zero-count stdout summary |
| Invalid source/import failure | active stage, asset failure, run failure, original exception and exit code |
| Signing enabled | explicit sign stage; publication remains transaction-safe |
| Reporter disabled/null | no progress object/timer/events; with fixed generation/time seams, progress on/off yields byte-identical cooked artifacts |
| Redirected `stderr` | newline records still emitted and flushed; no TTY check suppresses them |
| Long silent operation | heartbeat every ten seconds with run, asset, stage, and item context |
| JSONL | every line independently parses; schema, run id, sequence, enums, and duration meanings are stable |
| Closed/failing progress writer | diagnostic failure does not abort or corrupt the cook |
| Repeated texture slots/materials | one stable source capture and one compatible transport analysis while cached; identical material/profile output |
| Texture cache over byte budget | deterministic eviction/reanalysis, bounded retained bytes, and no out-of-memory scheduling |
| Source mutation during cached read | stale/mixed bytes are never accepted; cook fails or retries according to the stable-snapshot contract |
| Serial folder session | settings/database loaded once per run; each successful asset still receives an atomic database checkpoint |
| Parallel shared texture | one texture producer, many validated consumers, no temporary/final-path races |
| Parallel determinism | fixed generation/time seams yield byte-identical packages for `--jobs 1|2|auto`; production-mode comparisons normalize only existing generation/timestamp fields |
| Parallel failure/Ctrl+C | queued work stops, active work drains or rolls back, completed entries remain resumable, one terminal cancelled/failed run outcome |
| Runtime strict source load | source hashed once and stable model captured once; replacement-after-snapshot hardening remains intact |
| Concurrent same-key runtime loads | one decode/upload owner; active callers receive the same published instance and one cancelled follower does not abort remaining callers |
| Concurrent distinct runtime loads | CPU read/decode overlaps within limits; renderer upload obeys its dispatcher/thread contract |
| Synchronous load during same-key preload | common work coalesces without blocking the renderer context needed to publish; exactly one runtime owner results |
| Cooked mesh with many submeshes/LODs | validated combined arrays feed range-aware staging; per-submesh stream/LOD copy counter remains zero |
| Repeated cooked texture slots/models | one authenticated `.njtex`/KTX2 capture per complete contract while in flight; existing source texture hot reload remains byte-sensitive |
| Renderer lifecycle contention | CPU texture authentication and mesh-view preparation occur outside the renderer lock; publication/rollback remain serialized |
| Sidecar read/decode failure | exact path/stage retained, siblings cancelled/drained, no CPU/GPU ownership leak |
| Reader strategy thresholds | selected direct/mapped/batched path is observable in diagnostics and all required hashes remain verified |
| Preload byte budget/cancellation | known peak in-flight bytes remains bounded; unpublished partial work is disposed, already-ready entries remain manager-owned and appear in the result, and no orphan is published |
| Clear/dispose with in-flight loads | admission stops safely, waiters complete consistently, and retryable ownership guarantees remain |

Retain and run the existing cooked-asset, artifact I/O, incremental,
`ModelAssetCookerTransactionTests`, `ContentManagerHardeningTests`, cooked runtime
policy, authentication/signature, and renderer upload ownership tests. In
particular, progress events must not move `asset.done` ahead of stable model
publication or database commit, and async work must not bypass existing
publication/disposal ledgers.

## 9. Acceptance criteria

### 9.1 Terminal-progress milestone

1. A default `cook model`, `cook folder`, or `cook changed` invocation prints a
   newline progress record before every expensive stage.
2. While one operation remains active, redirected logs receive a flushed
   heartbeat at least every ten seconds.
3. At any point, the latest line identifies the run, active asset, stage, and
   material/texture when applicable.
4. Every started asset reaches exactly one visible terminal outcome: succeeded,
   skipped, or failed. Every started run reaches one terminal outcome.
5. Incremental checks report `skip|cook` plus the first authoritative reason.
6. Plain logs use no in-place terminal control and remain readable as captured
   text. JSONL logs conform to schema version 1 and require no prose parsing.
7. Existing final summaries remain on `stdout`; progress remains on `stderr`;
   `--progress off` and bounded `stages` detail are available.
8. Cooked outputs, report schema/non-timing content, database identities,
   signing order, rollback guarantees, and existing exit codes are unchanged.
9. Unit, subprocess, cooked-asset, and transaction tests pass, and
   `docs/CookedAssets.md` documents the observed contract.

### 9.2 Cooker-throughput milestones

1. Raw before/after benchmark evidence exists for no-op changed, one-dependency
   changed, cold serial, cold parallel, and progress-overhead cases.
2. Serial optimizations retain only content-proven cache facts, keep memory
   bounded, and pass mutation/integrity tests.
3. `--jobs 1` remains the default until a separate evidence-backed decision;
   every other jobs setting is bounded by workers and in-flight bytes, with only
   the documented one-item oversize exception.
4. With fixed generation/time test seams, all job counts produce byte-identical
   cooked artifacts. Production mode introduces no nondeterminism beyond existing
   generation names/timestamps and preserves per-asset atomic
   publication/database semantics.
5. The target median improves beyond benchmark noise and representative
   non-target cases stay within the 5-percent regression gate or carry an
   explicit accepted tradeoff.
6. Ctrl+C produces one cancelled outcome, safely drains/rolls back active work,
   retains completed checkpoints for `cook changed`, and returns 130; other exit
   behaviour is unchanged.

### 9.3 Runtime-loading milestone

1. Resolution and load share one source hash and one immutable stable-model
   snapshot without weakening replacement, signature, whole-file, or section
   integrity guarantees.
2. Same-key concurrent misses single-flight; different-key CPU work can overlap;
   cache publication, unload, clear, and dispose remain race-safe.
3. Range-aware mesh upload eliminates per-submesh stream/LOD copies, and unique
   cooked texture receipts eliminate repeated slot-level file reads and
   authentication while retaining strict content and hot-reload correctness.
4. `LoadAsync<T>` and `PreloadAsync` provide bounded CPU work, renderer-owned
   upload dispatch, priority, cancellation, progress, and byte backpressure.
5. Existing synchronous `Load<T>` behaviour and cache ownership remain source
   compatible and semantically unchanged.
6. Runtime stage diagnostics distinguish cold load, warm OS-cache load, manager
   cache hit, single-flight wait, texture authentication, renderer-lock wait,
   and renderer upload.
7. At least one representative cold/warm startup target improves under the
   section 6.1 gate with no unexplained memory or p95 regression.

Conditional Phase 8 format/index ideas are not part of these acceptance criteria
until approved as their own implementation plans.

## 10. Explicit non-goals

- calculating a synthetic overall percentage or presenting an unqualified ETA;
- logging per-mip, per-meshlet, per-vertex, or byte-level progress;
- persisting live progress into `.cook-report.json` or `assetdb.njassetdb`;
- replacing existing summaries, warnings, exception messages, or the
  synchronous `Load<T>` API;
- adding a full terminal UI framework;
- trusting timestamps/lengths as content identity or disabling source,
  whole-file, section-hash, or signature validation to claim a speedup;
- unbounded parallelism, assuming current cooker dependencies are thread-safe,
  or making `auto` concurrency the initial default;
- running renderer upload on an arbitrary pool thread through `Task.Run`;
- all-or-nothing rollback of an entire preload group; completed entries remain
  manager-owned and only unpublished work is cancelled;
- pause/resume or mid-asset cook checkpoints; completed-asset resume is provided
  by the existing database plus `cook changed`;
- continue-on-error folder semantics; parallel cooking remains fail-fast;
- mip/LOD streaming, partial GPU residency, scene bundles, archive/remote
  loading, automatic hot reload, or placeholder-on-failure behaviour;
- changing cooked formats in Phases 0-7. Catalog, packed metadata, and OMM
  sidecar proposals require separate format plans and evidence gates.
