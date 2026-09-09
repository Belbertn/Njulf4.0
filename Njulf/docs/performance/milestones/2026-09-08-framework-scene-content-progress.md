# Framework scene/content implementation checkpoint — 2026-09-08

Status: **partial implementation of the approved items 5–6 plan**, not completion.
Source: `7ce858e642b7937789b33a1e3ab9978792b8d15b`, dirty workspace containing the
earlier framework API work and separate user light/shadow changes, preserved.
Hardware: Ryzen 5 5600H, RTX 3060 Laptop, driver 32.0.16.1062; Windows, .NET 10.0.203.

Implemented unified `Njulf.Assets.IContentManager`, standard progress, cache-only unload,
host upload budgets/telemetry, `Game.LoadAsync`, synchronization-context pumping and
cooperative shutdown. Added scene membership/transfer checks, disposing Clear, grouped
ModelInstance ownership, neutral scene lights/environments and schema 12 persistence.
The model API example uses async content, scene lights, and grouped attachment.

Validation:

- Development solution build: 0 warnings/errors. `git diff --check`: clean.
- Focused combined run: 166 passed and one obsolete schema-number assertion failed.
  Updated the assertion; affected CPU recheck: 90 passed. Union of latest results:
  174 distinct passing cases, no outstanding failures in those selections.
- Early async-exit validation initially exposed a MeshPipeline constructor leak when
  startup compilation was cancelled. Added constructor rollback after native work drains;
  subsequent validation-enabled lifecycle tests passed with zero validation errors.
- Async model example: 123 full-quality frames, exit 0, zero Vulkan validation errors.
  Capture visually matches the retained pre-change model example at 960×640.
- New persistence fixture initially used an invalid area-light IES assignment. Existing
  validation correctly rejected it; corrected the fixture to a spot light.

Performance evidence is limited: example bootstrap 2.905 s, first production scene 18.483 s.
Compilation/test work overlapped this functional capture. No comparable baseline/candidate
frame-time distribution, tail latency or allocation comparison was collected; these timings
do not establish a performance improvement or regression.

Evidence (ignored active artifacts): `artifacts/framework-contracts/items56-final.trx`,
`items56-recheck.trx`, `items56-model.log`, `items56-model.png`, `items56-build.log`.
Prior visual reference: `artifacts/framework-api-typed/model.png`.

Reproduce build: `dotnet build Njulf.sln -c Development --no-restore`.
Reproduce example (new capture path required):
`dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example model --frames 120 --validation --capture artifacts/framework-contracts/items56-model-repeat.png`.
Focused test selections and exact outcomes are recorded in the TRX files; they cover scene,
model/content ownership, serialization, upload dispatcher, host lifecycle, graphics API,
imported lights, sample transitions, and material-override persistence.

Remaining: multiple independent view banks and offscreen material targets, viewport/blit
composition and capture, one-scene/one-simulation/one-present frame orchestration, feedback
checks/exclusions, full editor/large-sample ownership migration, combined example and view
GPU/performance validation. See [remaining work](../../FrameworkApiRemaining.md).

Storage inspection: `tools/prune-local-artifacts.ps1` reported 3.48 GiB eligible across 414
older payloads and 296.58 GiB free. No deletion applied: the candidates include unrelated
investigations. This checkpoint's active evidence remains within the workspace on D:.
