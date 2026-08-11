# Asset Cooker Terminal Progress Implementation Plan

- Date: 2026-08-08
- Status: proposed; this document does not implement the changes
- Scope: `Njulf.AssetTool cook model|folder|changed`, the progress seam in
  `ModelAssetCooker`, terminal formatting, tests, and cooked-asset documentation
- Baseline: the current working tree, including incremental cooking,
  generation-qualified transactional publication, texture reuse, package
  signing, and the mesh LOD algorithm revision in the cook identity

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

Progress must consist of complete newline-terminated records. Do not implement
an in-place progress bar, spinner, carriage-return updates, ANSI cursor control,
or TTY-only output: those presentations disappear or become unreadable in IDE,
CI, redirected, and agent-captured logs.

The change is observability only. It must not change package bytes, cook hashes,
incremental decisions, output publication order, rollback behaviour, exit codes,
or the existing final result summaries.

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

## 3. Terminal and automation contract

### 3.1 Modes

Add the following options to all three `cook` modes:

```text
--progress <plain|jsonl|off>
```

- `plain` is the default, whether or not `stderr` is attached to a TTY.
- `jsonl` writes the same events as one JSON object per line for automation.
- `off` suppresses progress and heartbeats, but not final summaries, warnings,
  or errors.

Do not add an `auto` mode that silently disables or changes the presentation
when output is redirected. Consistent newline records are the feature that
makes the output useful to agents and CI.

Update `PrintUsage` and reject unknown values through the existing argument
error path. The progress selection is presentation state and must never enter
`ModelCookOptions`, the settings hash, reports, or asset database.

### 3.2 Plain records

Use a stable `[assetcook]` prefix, an event name, and invariant-culture
`key=value` fields. Quote and escape user-controlled values such as paths and
texture names. Example:

```text
[assetcook] run.start mode=folder source="Content" output="Cooked/win-x64" platform=win-x64
[assetcook] discovery.done assets=12 elapsed_ms=4
[assetcook] asset.start asset=3/12 source="Models/Sponza.gltf"
[assetcook] stage.start asset=3/12 stage=import elapsed_ms=18
[assetcook] stage.done asset=3/12 stage=import elapsed_ms=1842 backend=SharpGltf
[assetcook] stage.start asset=3/12 stage=materials-textures elapsed_ms=1891 materials=24 texture_slots=61
[assetcook] texture.start asset=3/12 slot=17/61 name="roughness.png"
[assetcook] heartbeat asset=3/12 stage=materials-textures item="roughness.png" stage_elapsed_ms=10000 total_elapsed_ms=11910
[assetcook] texture.done asset=3/12 slot=17/61 outcome=reused elapsed_ms=11244
[assetcook] asset.done asset=3/12 outcome=succeeded elapsed_ms=28457 meshes=31 textures=38 warnings=2
[assetcook] run.done outcome=succeeded assets=12 cooked=8 skipped=4 failed=0 elapsed_ms=91022
```

Requirements:

- exactly one event per physical line;
- flush `stderr` after every event;
- no `\r`, escape sequences, terminal-width dependence, or Unicode glyphs
  whose meaning is lost in plain logs;
- integer milliseconds and invariant formatting;
- paths displayed relative to the requested source/output roots when possible,
  while the underlying event retains the full source path;
- monotonically increasing `sequence` in JSONL (plain output may omit it); and
- a schema/version field in JSONL so consumers can reject an incompatible
  future event format.

### 3.3 JSON Lines records

JSONL uses fixed property names and enum strings, not a serialized arbitrary
message. A representative event is:

```json
{"schema":1,"sequence":14,"event":"stage.start","assetIndex":3,"assetCount":12,"sourcePath":"D:/Content/Models/Sponza.gltf","stage":"materials-textures","elapsedMs":1891,"materialCount":24,"itemCount":61}
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
guarantee that no late heartbeat is written after `run.done` or `run.failed`.

Bound normal output to meaningful work units:

- one start and completion event per asset stage;
- one start and completion event per material;
- one start and completion event per occupied material texture slot;
- identify a slot outcome as `cooked`, `reused`, or `deduplicated`;
- one material completion event after its primitive transport profile work;
- no per-mip, per-meshlet, per-vertex, or per-file-byte events.

This provides enough detail to locate slow texture work without turning a large
model cook into unbounded low-level trace noise.

## 4. Progress API and ownership

### 4.1 Typed library seam

Add `Njulf.Assets/Cooked/AssetCookProgress.cs` containing:

- `AssetCookProgressEventKind` for discovery, asset, stage, material, texture,
  skip, success, and failure transitions;
- `AssetCookStage` with stable names for `prepare`, `incremental-check`,
  `import`, `mesh`, `materials-textures`, `serialize`, `sign`, `publish`, and
  `report-database`;
- `AssetCookProgressOutcome` for `succeeded`, `skipped`, `failed`, `cooked`,
  `reused`, and `deduplicated` where applicable;
- an immutable `AssetCookProgressEvent` carrying typed optional fields such as
  source path, asset index/count, item index/count/name, elapsed milliseconds,
  counts, backend, and failure message; and
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
Document that custom sinks must return quickly and be thread-safe if the cooker
is parallelized in the future. The AssetTool sink must contain its own output
errors so a closed diagnostic stream cannot interrupt package publication.

### 4.2 Folder/run ownership

Materialize the sorted supported-file list once at the start of `CookFolder`.
This supplies a trustworthy total and preserves the current deterministic
order. Emit discovery completion for zero as well as non-zero assets.

`Program.RunCook` owns the command-level `run.start`/`run.done` presentation
because it knows whether the user selected `model`, `folder`, or `changed`, the
requested roots, and the final aggregate counts. `ModelAssetCooker` owns asset,
stage, material, texture, skip, and failure events.

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
- relative display paths and safe value escaping;
- sequence assignment;
- monotonic run/stage/item timing;
- synchronized writes and explicit flushes;
- the ten-second heartbeat timer; and
- final disposal.

Construct it in `RunCook` after arguments are valid and before invoking the
cooker. Wrap the cook invocation so exactly one terminal run outcome is emitted
for success or failure. Keep `PrintCookResult` and the existing folder summary
unchanged on `stdout`; progress is additional context, not a replacement for
the concise completion contract.

Use `TimeProvider` (or an equally injectable monotonic clock/timer seam) so
heartbeat behaviour is unit-testable without real ten-second sleeps. Avoid a
third-party terminal UI package: it adds no value to a line-oriented contract
and risks reintroducing redirected-output differences.

## 6. Implementation phases

### Phase 0: lock the output contract in tests

1. Add formatter tests for the exact event names and required fields.
2. Prove plain records contain one trailing newline, no carriage return, no
   ANSI escape, invariant numbers, and safely escaped paths/names.
3. Parse every JSONL record with `System.Text.Json` and assert schema version,
   sequence order, enum strings, and omission of absent optional fields.
4. Add fake-clock heartbeat tests for silence, reset-on-progress, periodic
   emission, and no emission after disposal/final outcome.

Exit criteria: the human/AI-visible protocol is executable test data before
cooker instrumentation begins.

### Phase 1: add ordered cooker events

1. Add the typed progress contracts in `Njulf.Assets.Cooked`.
2. Refactor `CookModel` into a public wrapper plus private context-aware core,
   leaving the transaction body and cleanup rules intact.
3. Materialize and index folder inputs in their existing ordinal-insensitive
   sorted order.
4. Instrument every stage boundary and the incremental skip branch.
5. Pass the private progress context into `CookMaterials` and instrument
   material/texture work and reuse outcomes.
6. Emit the failure event after rollback, then preserve the original throw.

Exit criteria: a recording sink observes a valid ordered lifecycle for a
successful, skipped, empty-folder, and failed cook, while a null sink produces
the same artifacts and reports as before.

### Phase 2: wire terminal output and CLI options

1. Add `AssetCookTerminalProgress` and its plain/JSONL/off selection.
2. Parse `--progress` in `RunCook` and update all cook usage lines.
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
   options, examples, and a note that progress is deliberately newline-based
   for CI and AI-agent visibility.

Exit criteria: documentation and subprocess evidence match the unit-tested
protocol.

## 7. Test matrix

| Scenario | Required evidence |
| --- | --- |
| `cook model`, no textures | `1/1`; all applicable stages ordered; success progress and final stdout summary are both present |
| Textured model | occupied-slot denominator; cooked/reused/deduplicated outcomes; unique final texture count unchanged |
| Incremental second cook | prepare/check followed by skip; no import/mesh/texture stages |
| Folder with multiple models | fixed total, deterministic indexes, one aggregate completion |
| Empty folder | discovery with `assets=0`, successful run completion, existing zero-count stdout summary |
| Invalid source/import failure | active stage, asset failure, run failure, original exception and exit code |
| Signing enabled | explicit sign stage; publication remains transaction-safe |
| Reporter disabled/null | no progress output/events and byte-identical cooked artifacts |
| Redirected `stderr` | newline records still emitted and flushed; no TTY check suppresses them |
| Long silent operation | heartbeat every ten seconds with current asset/stage/item |
| JSONL | every line independently parses; schema and sequence are stable |
| Closed/failing progress writer | diagnostic failure does not abort or corrupt the cook |

Retain and run the existing cooked-asset, artifact I/O, incremental, and
`ModelAssetCookerTransactionTests`. In particular, progress events must not
move `asset.done` ahead of the stable model publication or database commit.

## 8. Acceptance criteria

The work is complete when all of the following hold:

1. A default `cook model`, `cook folder`, or `cook changed` invocation prints a
   newline progress record before every expensive stage.
2. While one operation remains active, redirected logs receive a flushed
   heartbeat at least every ten seconds.
3. At any point, the latest line identifies the active asset, stage, and
   material/texture when applicable.
4. Every started asset reaches exactly one visible terminal outcome: succeeded,
   skipped, or failed. Every started run reaches one terminal outcome.
5. Plain logs use no in-place terminal control and remain readable as captured
   text. JSONL logs conform to schema version 1 and require no prose parsing.
6. Existing final summaries remain on `stdout`; progress remains on `stderr`;
   `--progress off` is available.
7. Cooked outputs, report contents, database identities, signing order,
   rollback guarantees, and exit codes are unchanged.
8. The implementation adds no expensive duplicate analysis and no third-party
   UI dependency.
9. Unit, subprocess, cooked-asset, and transaction tests pass, and
   `docs/CookedAssets.md` documents the observed contract.

## 9. Explicit non-goals

- changing synchronous or serial cook execution;
- cancellation, pause/resume, or continue-on-error folder semantics;
- persisting live progress into `.cook-report.json` or `assetdb.njassetdb`;
- calculating a synthetic overall percentage from incomparable stages;
- logging per-mip, per-meshlet, per-vertex, or byte-level progress;
- replacing existing summaries, warnings, exception messages, or exit codes;
- adding a full terminal UI framework.
