# RendererDiagnosticsAssembler Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor.

## 1. Required outcome

Extract renderer-diagnostics construction from `Njulf.Rendering/VulkanRenderer.cs` into a focused `RendererDiagnosticsAssembler` under `Njulf.Rendering/Diagnostics`.

The completed refactor must:

- Remove the current `VulkanRenderer.BuildDiagnostics` implementation, which occupies lines 7349-9816 in the audited working tree.
- Keep `VulkanRenderer` responsible for choosing the frame boundary at which diagnostics are captured and for publishing `LastDiagnostics` and `LastBudgetSnapshot`.
- Make the assembler responsible for mapping captured frame/subsystem state into `RendererDiagnostics`, evaluating the matching `RenderBudgetSnapshot`, deriving GI feature states and warnings, and applying the existing post-submission diagnostics patches.
- Preserve every existing diagnostic value, default, fallback reason, warning persistence rule, capture identity, budget classification, and serialization contract.
- Preserve the current frame lifecycle and Vulkan behavior. This is not a render-path, synchronization, settings, diagnostics-schema, or performance-feature redesign.
- Remove at least the approximately 2,468-line assembly method from `VulkanRenderer`; diagnostics-only helpers should move with it when doing so does not create a new cross-domain dependency.

The desired dependency direction is:

```text
renderer/subsystem owners
        |
        v
grouped read-only inputs and immutable subsystem snapshots
        |
        v
RendererDiagnosticsAssembler
        |
        v
RendererDiagnosticsAssemblyResult
        |
        v
VulkanRenderer publishes LastDiagnostics + LastBudgetSnapshot
```

`RendererDiagnosticsAssembler` must never depend on, retain, or call back into `VulkanRenderer`.

## 2. Audited starting point

### 2.1 Size and call site

The current `VulkanRenderer.cs` is 23,084 lines. `BuildDiagnostics(SceneRenderingData)` starts at line 7349 and ends at line 9816, making it approximately 2,468 lines by itself.

It is called once near the end of `DrawScene`, after completed GPU counters and timings have been applied to `SceneRenderingData`:

1. Completed meshlet, DDGI, shadow, reflection, particle, foliage, Hi-Z, and GPU-timing data is copied into `sceneData`.
2. `CpuTotalDrawSceneMicroseconds`, GI CPU timing, async timing capture, and submitted-frame DDGI evidence are finalized.
3. `_lastSceneData` is assigned.
4. `BuildDiagnostics(sceneData)` produces `_lastDiagnostics`.
5. `_debugDraw.ClearFrame()` clears frame-local debug data.

That ordering is observable. In particular, debug-draw counts must be captured before `ClearFrame`.

### 2.2 Existing post-assembly patches

The snapshot produced in `DrawScene` is not the final mutation of `_lastDiagnostics` for the frame:

- `UpdateAsyncComputeSubmissionDiagnostics` patches actual submitted graphics/compute segment counts during `EndFrame`.
- `RefreshValidationDiagnostics` patches the latest validation-message snapshot after present and any swapchain recreation.

The extraction must preserve both late patches and their timing. It must not move the main assembly after present, because that would change which frame-local state is observed and when captures can read `LastDiagnostics`.

`LastBudgetSnapshot` is evaluated during the main `DrawScene` assembly. Existing behavior does not recompute it after the two `EndFrame` patches; retain that behavior.

### 2.3 Responsibilities currently mixed in `BuildDiagnostics`

The method currently performs all of the following:

- Resolves requested versus effective reflection, GI, ray-query, debug-view, render-pipeline, and async-compute states.
- Reads approximately 47 renderer fields or owned subsystem references.
- Maps the large `SceneRenderingData` telemetry surface into `RendererDiagnostics`.
- Creates reflection, material, DDGI, near-field, shadow, foliage, particle, post-processing, debug, capture, validation, resource, and render-graph diagnostics.
- Builds DDGI runtime/content snapshots and advances the stateful `DdgiDiagnosticWarningTracker`.
- Builds GPU-frame timing, upload-budget, memory-budget, and runtime-stall snapshots.
- Evaluates `RenderBudgetSnapshot` and writes `_lastBudgetSnapshot` as a side effect.
- Derives performance-capture run, camera, frame, GI-settings, and GI-measurement metadata.
- Derives `GiFeatureStates`, evaluates the stateful `GiWarningEvaluator`, creates GI warnings, and mirrors blackout results into legacy fields.

The code first constructs a very large base `RendererDiagnostics`, applies layout/scheduling fields, evaluates budgets, applies capture/graph/async/resource fields, and finally applies GI-derived fields. Statement order matters because later stages consume the earlier snapshot.

### 2.4 Stateful ownership outside the method

Three collaborators currently live on `VulkanRenderer` solely or primarily for diagnostics assembly:

- `_budgetEvaluator`
- `_giWarningEvaluator`
- `_ddgiDiagnosticWarningTracker`

`GiWarningEvaluator` is reset when `sceneData.SceneContentRevision` changes. `DdgiDiagnosticWarningTracker` is not reset at that boundary in the current implementation; it updates continuously from the runtime snapshot. Preserve that distinction unless a separate behavior change is explicitly approved.

### 2.5 Existing consumers and test coverage

`LastDiagnostics` and `LastBudgetSnapshot` are consumed by the editor, debug overlays, benchmark runners, capture/evidence writers, qualification gates, and the sample diagnostics reporter. `PerformanceSnapshotWriter` serializes the resulting contracts.

Existing tests cover the `RendererDiagnostics` schema/defaults, budget evaluator, GI feature-state factory, GI warning evaluator, performance snapshot writer, GPU counter decoding, reflection-probe telemetry, and many individual DDGI helpers. There is no focused test fixture for the assembly boundary itself.

`Njulf.Rendering/Data/RendererDiagnostics.cs` is also large (2,391 lines), but decomposing that public data contract is deliberately outside this extraction.

## 3. Scope

### In scope

- A new `RendererDiagnosticsAssembler` with renderer-instance lifetime.
- Immutable, domain-grouped input contracts and an assembly result containing both the diagnostics and budget snapshots.
- Moving the main diagnostics mapping and its stateful evaluator ownership out of `VulkanRenderer`.
- Moving diagnostics-only pure helpers out of `VulkanRenderer`.
- Preserving shared helpers in their current owner or moving them to a neutral domain helper when they are also used by rendering, capture admission, or scheduling code.
- A narrow input-capture adapter in `VulkanRenderer` that reads the current owners and immediately invokes the assembler.
- Assembler-owned methods for the existing async-submission and validation-message patches.
- Focused characterization, lifecycle, budget, warning-persistence, patch, and serialization tests.
- Full solution and runtime validation proportional to this high-churn refactor.

### Out of scope

- Adding, removing, renaming, regrouping, or changing the types of `RendererDiagnostics` properties.
- Replacing the large positional `RendererDiagnostics` constructor or decomposing the record into feature records.
- Changing performance-snapshot JSON or benchmark/evidence schemas.
- Changing when GPU counters are recorded, copied, or read back.
- Changing render-graph execution, async-compute scheduling, queue submission, barriers, Vulkan resource ownership, or feature admission.
- Changing budget thresholds, warning thresholds, fallback wording, or qualification semantics.
- Optimizing DDGI, shadows, reflections, captures, or any other renderer feature while moving the code.
- Using a `partial VulkanRenderer` as the final architecture. A partial-class split changes navigation but not ownership or coupling.
- Introducing a general service locator or passing `VulkanRenderer` itself as an assembler dependency.

## 4. Non-negotiable invariants

1. Main diagnostics assembly remains synchronous and occurs exactly once per successful `DrawScene` at the existing boundary.
2. All completed counter and timestamp application remains before assembly.
3. Debug-draw diagnostics are sampled before `_debugDraw.ClearFrame()`.
4. Actual async submission counts and final validation messages remain post-assembly patches in `EndFrame`.
5. `RendererDiagnostics` and `RenderBudgetSnapshot` returned by one assembly describe the same input frame and budget profile.
6. `_lastDiagnostics` and `_lastBudgetSnapshot` are assigned together from one result; the assembler must not mutate renderer fields as a hidden side effect.
7. The assembler may keep warning/evaluator history, but it must not retain `SceneRenderingData`, `RenderSettings`, manager instances, mutable lists, or per-frame input snapshots after a call returns.
8. Scene revision changes reset `GiWarningEvaluator` at the same point as today. They do not newly reset `DdgiDiagnosticWarningTracker`.
9. Disabled or unavailable features preserve their exact current empty snapshots, zero values, and reason strings.
10. Authored/requested settings remain distinct from effective/runtime settings in captures.
11. Existing checked byte arithmetic, saturating calculations, array copies, list ordering, top-N limits, and fallback selection remain unchanged.
12. The assembler performs no Vulkan commands, waits, resource creation/destruction, descriptor writes, file writes, or render-state mutation.
13. Driver/resource queries needed for diagnostics occur no more often than they do now. Memory-budget queries must still be captured once for a single assembly result.
14. No new per-frame delegates, reflection, dictionaries, LINQ materializations, or unbounded collections are introduced by the abstraction.
15. Existing public interfaces and properties remain unchanged: `IRendererDebugTools.LastDiagnostics`, `VulkanRenderer.LastDiagnostics`, and `VulkanRenderer.LastBudgetSnapshot` keep their current types and semantics.

## 5. Chosen architecture

### 5.1 Assembler ownership

Add an internal sealed class in `Njulf.Rendering.Diagnostics`:

```csharp
internal sealed class RendererDiagnosticsAssembler
{
    private readonly RenderBudgetEvaluator _budgetEvaluator = new();
    private readonly GiWarningEvaluator _giWarningEvaluator = new();
    private readonly DdgiDiagnosticWarningTracker _ddgiWarningTracker = new();

    public RendererDiagnosticsAssemblyResult Assemble(
        in RendererDiagnosticsAssemblyInput input);

    public RendererDiagnostics ApplyAsyncSubmission(
        RendererDiagnostics current,
        in RendererDiagnosticsSubmissionPatch patch);

    public RendererDiagnostics ApplyValidationMessages(
        RendererDiagnostics current,
        in RendererValidationMessageSnapshot validation);

    public void ResetSceneHistory();
}
```

The exact visibility may be `internal`; `Njulf.Rendering` already grants `Njulf.Tests` access through `InternalsVisibleTo`.

`ResetSceneHistory` resets only state that the renderer currently resets on a scene revision (`GiWarningEvaluator`). The method name should describe the lifecycle event rather than expose the concrete evaluator.

`RenderBudgetEvaluator` is currently stateless, but keeping it as an assembler collaborator preserves the existing construction model and leaves room for focused injection in tests if needed.

### 5.2 Result contract

Return both published products explicitly:

```csharp
internal readonly record struct RendererDiagnosticsAssemblyResult(
    RendererDiagnostics Diagnostics,
    RenderBudgetSnapshot Budget);
```

Do not let `Assemble` update `VulkanRenderer._lastBudgetSnapshot` through a callback, shared mutable holder, or reference to the renderer. Explicit output makes same-frame pairing testable.

### 5.3 Input contracts

Do not replace the 47 current field dependencies with a 47-parameter method or a generic property bag. Use strongly typed domain groups: immutable records for captured snapshots, plus explicitly borrowed read-only `SceneRenderingData` and `RenderSettings` references for the synchronous call. The final top-level input should contain approximately these groups:

- `SceneRenderingData SceneData`: passed for the duration of the synchronous call and treated as read-only.
- `RenderSettings Settings`: passed for the synchronous call and never retained. If settings can be mutated from another thread in the future, replace this with a settings snapshot before enabling that threading model.
- `RendererDiagnosticsFeatureInput`: capability flags, active optional-runtime states, effective pipeline information, and precomputed feature snapshots that are not already in `SceneData`.
- `RendererDiagnosticsResourceInput`: memory heap, upload, texture, mesh, material, render-target, environment, shadow, staging, swapchain, and retirement snapshots.
- `RendererDiagnosticsExecutionInput`: render-graph snapshot, active production pass list, a diagnostics-only projection of the resolved async plan/submission plan, queue-family facts, resolved timeline values, frame counters, and CPU submission timings.
- `RendererDiagnosticsGiInput`: DDGI runtime/content/near-field snapshots, volume/layout/scheduling/residency telemetry sources, pipeline-cache telemetry, and completed material/geometry counters not already copied into `SceneData`.
- `RendererDiagnosticsToolingInput`: debug-draw counters, screenshot/RenderDoc state, GPU timestamp state, selected-object summary, and validation snapshot.
- `RendererDiagnosticsCaptureInput`: device/driver identity, build/application/commit/shader/executable/worktree identity, normalized scenario/scene identity, camera/frame metadata, and scene hashes.
- `RendererDiagnosticsFrameInput`: remaining renderer-local frame timings, target-recreation reason/scale, and async emitted/planned/transfer counters.

These are diagnostic data contracts, not new owners of Vulkan resources. Prefer existing immutable snapshots such as `MaterialManagerDiagnostics`, `MemoryHeapBudgetSnapshot`, `FrameTimingSnapshot`, `RenderGraphDiagnostics`, and `GpuCompletionRetirementSnapshot` over copying their fields into another duplicate model.

`VulkanRenderer.AsyncComputePlan` is currently a private nested record. Do not widen or relocate that scheduler type solely for diagnostics. At the capture boundary, project it into `RendererDiagnosticsExecutionInput`: requested/effective mode, support/status, graph diagnostics, candidate/enabled pass arrays, path diagnostics, segment diagnostics, queue-family facts, resource-plan generation, and the already-resolved timeline values/estimates needed by the output. `AsyncComputeSubmissionPlan` may be read while producing that projection, but the assembler input must not depend on the renderer-private wrapper.

When an owner exposes several values that must be coherent, add one immutable `CreateDiagnosticsSnapshot`/`Diagnostics` result to that owner rather than reading it repeatedly from the assembler. Do not make the assembler depend on concrete Vulkan handles merely to recover counts or byte totals.

The input capture code must materialize mutable collections that the result retains. Preserve existing behavior such as `DdgiVolumeDiagnostics.ToArray()`, the top-ten texture list, and the top-ten meshlet-quality list.

### 5.4 Input capture boundary

Keep one narrow method in `VulkanRenderer`, for example:

```csharp
private RendererDiagnosticsAssemblyInput CaptureDiagnosticsInput(
    SceneRenderingData sceneData,
    AsyncComputePlan asyncPlan)
```

This method may read renderer-owned state and ask subsystem owners for snapshots. It must not:

- Construct or patch `RendererDiagnostics`.
- Evaluate budgets or GI warnings.
- Duplicate fallback/status policy already assigned to the assembler.
- Retain the returned input after the synchronous assembly call.
- Execute Vulkan work or alter frame state.

The adapter is expected to be somewhat verbose because the current renderer has many diagnostic sources, but it should remain shallow data capture. If it begins reproducing feature policy, move that policy into the assembler or an existing domain factory.

### 5.5 Assembly pipeline

Preserve the current semantic order inside the assembler:

1. Validate required input and resolve requested/effective feature state.
2. Derive forward-occlusion, local-shadow, active-pipeline, reflection, and GI status values.
3. Create DDGI runtime/content snapshots and update persistent DDGI warnings from the captured inputs.
4. Construct the base `RendererDiagnostics` using the same constructor argument and initializer ordering as the current method.
5. Apply GPU-frame timing and Simple-DDGI layout/scheduling/residency telemetry.
6. Evaluate the budget using that intermediate diagnostics snapshot plus the captured memory, upload, and stall snapshots.
7. Apply capture, graph, async, budget, resource, validation, and high-water fields.
8. Derive `GiFeatureStates` from the completed snapshot.
9. Derive `ResolvedGiSettings` from the snapshot containing those feature states.
10. Evaluate stateful GI warnings and apply `GiWarnings`, `GiBlackFrameMetrics`, and the four legacy blackout mirror fields.
11. Return `RendererDiagnosticsAssemblyResult` without changing any source object.

Keep the current number of `RendererDiagnostics` record clone operations during the mechanical extraction. Splitting mapping into many `with` expressions would add large per-frame allocations because `RendererDiagnostics` is a reference-type record. Pure local calculator methods are welcome; repeated whole-record cloning is not.

### 5.6 Helper placement

Classify helpers before moving them:

Move into `RendererDiagnosticsAssembler` when they are pure and diagnostics-only:

- Forward occlusion reconciliation and sanity text.
- Local-shadow meshlet-test counts, compaction justification/status, overflow summary, and directional-shadow summary/runtime diagnostics.
- Budget metric lookup and memory-category lookup.
- Scene-buffer high-water calculations.
- Diagnostics-only GPU-frame aggregation if no non-diagnostics caller remains.
- Upload/memory snapshot composition once their raw resource inputs are available without manager backreferences.

Pass a precomputed value in the input or move the helper to a neutral domain owner when it is shared with live rendering behavior:

- Async-compute overlap, queue-busy, and first-consumer-wait estimates.
- Timeline-value resolution.
- DDGI transport-ring timing attribution.
- Performance-capture hashing and build/commit/shader identity resolution.
- Effective GI-mode selection if runtime code and tests also use it.

Do not solve a shared-helper dependency by calling `VulkanRenderer.SomeStaticHelper` from the assembler. The dependency arrow must not point back to the renderer.

### 5.7 Renderer wiring

`VulkanRenderer` retains:

- `_lastDiagnostics` and `_lastBudgetSnapshot` as the published snapshots.
- The public `LastDiagnostics` and `LastBudgetSnapshot` properties.
- The decision to capture at the end of `DrawScene`.
- Collection of current renderer/subsystem snapshots.
- The scene-revision event that calls `_diagnosticsAssembler.ResetSceneHistory()`.
- The `EndFrame` event points that request async-submission and validation patches.

The main call site should reduce to the following shape:

```csharp
RendererDiagnosticsAssemblyInput input = CaptureDiagnosticsInput(
    sceneData,
    frameAsyncComputePlan);
RendererDiagnosticsAssemblyResult result =
    _diagnosticsAssembler.Assemble(input);
_lastDiagnostics = result.Diagnostics;
_lastBudgetSnapshot = result.Budget;
```

For late patches:

```csharp
_lastDiagnostics = _diagnosticsAssembler.ApplyAsyncSubmission(
    _lastDiagnostics,
    submissionPatch);

_lastDiagnostics = _diagnosticsAssembler.ApplyValidationMessages(
    _lastDiagnostics,
    _context.ValidationMessageSnapshot);
```

The assembler requires no disposal stage because it owns only managed policy/evaluator state.

## 6. Delivery order and change isolation

Implement this as reviewable, behavior-preserving slices. Do not combine the extraction with feature work already present in the working tree.

### Phase 0: Capture the baseline

1. Record the exact current `BuildDiagnostics` boundaries, call sites, direct field dependencies, and helper call sites again immediately before implementation because `VulkanRenderer.cs` is actively changing.
2. Run the focused diagnostics/budget/GI/capture tests and save their results.
3. Export representative performance snapshots for:
   - canonical Simple-DDGI active;
   - GI emergency fallback/disabled;
   - async compute disabled;
   - async compute active or fallback;
   - debug tooling enabled with a DDGI overlay;
   - validation enabled if available.
4. Save schema/property-name inventories from `RendererDiagnostics` and the performance snapshot JSON.
5. Measure assembly allocation/time in a warmed representative run if a profiling harness is available. The extraction must not materially increase either.

Exit gate: the baseline is reproducible, and any pre-existing test failures are documented before code moves.

### Phase 1: Add contracts and focused characterization tests

1. Add the grouped input and result contracts under `Njulf.Rendering/Diagnostics`.
2. Add a test-only `RendererDiagnosticsAssemblyInputBuilder` in `Njulf.Tests`; production must not gain an `EmptyForTests` factory that could be called accidentally at runtime.
3. Add synthetic scenarios covering:
   - all major features disabled/unavailable;
   - canonical Simple-DDGI active with completed counters;
   - async compute active and fallback;
   - non-empty resource/memory/upload budgets;
   - screenshot, RenderDoc, debug overlay, and validation state;
   - capture identity and scene/camera metadata.
4. Add schema characterization that records all `RendererDiagnostics` property names/types and validates performance-snapshot JSON keys remain unchanged.
5. Add a deep comparison helper based on deterministic JSON nodes or explicit collection comparison; do not rely on reference equality for arrays and `IReadOnlyList` properties.

Exit gate: input contracts can represent every source currently read by `BuildDiagnostics` without a `VulkanRenderer` reference, untyped dictionary, or callback.

### Phase 2: Create the input-capture adapter

1. Add `CaptureDiagnosticsInput` to `VulkanRenderer` while leaving the existing `BuildDiagnostics` body in place.
2. Capture raw inputs at the existing call boundary.
3. Prefer existing owner snapshots. Add small owner-level snapshot APIs only when several related values otherwise risk becoming inconsistent.
4. Resolve the current async plan exactly once using `_frameAsyncComputePlan ?? BuildAsyncComputePlan(sceneData)`, then project that same plan into the execution input used by assembly.
5. Capture memory heap, upload, stall, validation, debug, screenshot, RenderDoc, resource, and top-N list state only once per assembly.
6. Ensure input capture occurs before debug-draw clearing and does not change exception behavior.

Exit gate: the existing in-renderer method can be expressed entirely in terms of `RendererDiagnosticsAssemblyInput` plus its stateful evaluators. No hidden read of a renderer field remains in the method body.

### Phase 3: Mechanically move the assembly body

1. Create `RendererDiagnosticsAssembler` and move `BuildDiagnostics` into `Assemble` with minimal textual change.
2. Replace renderer field reads with the corresponding grouped input values.
3. Preserve constructor argument order, object-initializer expressions, reason strings, `with` ordering, checked arithmetic, and collection materialization exactly.
4. Return the budget snapshot in `RendererDiagnosticsAssemblyResult` instead of assigning `_lastBudgetSnapshot` internally.
5. Move `_budgetEvaluator`, `_giWarningEvaluator`, and `_ddgiDiagnosticWarningTracker` ownership into the assembler.
6. Wire the result to both renderer fields at the current call site.
7. Do not rename locals, reformat the entire moved block, or simplify conditions in this phase; those changes obscure behavior review.

Exit gate: `VulkanRenderer` no longer contains `BuildDiagnostics`, the project compiles, focused tests pass, and a side-by-side review shows a move plus explicit input/output substitution rather than policy changes.

### Phase 4: Preserve lifecycle and late patches

1. Replace `_giWarningEvaluator.Reset()` at the scene-revision boundary with `_diagnosticsAssembler.ResetSceneHistory()`.
2. Move the field mapping from `UpdateAsyncComputeSubmissionDiagnostics` into `ApplyAsyncSubmission`; keep its call at the same `EndFrame` position.
3. Move the field mapping from `RefreshValidationDiagnostics` into `ApplyValidationMessages`; keep its call after present/swapchain handling.
4. Add tests proving each patch changes only its owned fields and leaves all other diagnostics values untouched.
5. Add a sequence test for GI warnings: accumulate consecutive-frame state, verify the threshold, reset on scene revision, and verify DDGI persistent-warning tracking retains its current independent behavior.

Exit gate: lifecycle sequence and warning thresholds match the baseline, including captures that read diagnostics between `DrawScene` and `EndFrame`.

### Phase 5: Relocate helpers and close dependency leaks

1. Move diagnostics-only helpers listed in section 5.6 into the assembler as private/internal pure methods.
2. For shared helpers, pass precomputed values or move them to an existing neutral diagnostics/timing/capture factory.
3. Update internal unit-test targets when a helper gains a more appropriate owner. Do not preserve a forwarding wrapper on `VulkanRenderer` merely for tests unless external binary compatibility requires it; these members are internal today.
4. Remove renderer `using` directives and fields that became unused.
5. Verify `RendererDiagnosticsAssembler.cs` has no reference to `VulkanRenderer`, command buffers, Vulkan handles, or resource-lifetime methods.
6. Verify `VulkanRenderer` no longer owns diagnostics evaluator/tracker instances and no longer maps individual `RendererDiagnostics` properties.

Exit gate: the dependency direction is clean and no diagnostics behavior is duplicated between the renderer and assembler.

### Phase 6: Verification and cleanup

1. Run the focused test matrix, then the full `Njulf.Tests` suite and solution build.
2. Compare before/after performance-snapshot schemas and stable identity/status fields.
3. Run representative runtime frames across feature, resize, scene-reload, camera-cut, async, validation, and debug-tooling transitions.
4. Confirm validation remains clean and no frame lifecycle changed.
5. Compare warmed assembly allocation/time against baseline. Investigate any material regression before merging.
6. Review the final diff specifically for changed reason strings, condition polarity, zero/default branches, list-copy behavior, constructor ordering, and budget/warning evaluation order.

Exit gate: all definition-of-done conditions are satisfied and the refactor has no known diagnostic-value drift.

## 7. File-level implementation map

### New files

`Njulf.Rendering/Diagnostics/RendererDiagnosticsAssembler.cs`

- Stateful assembler.
- Main assembly pipeline.
- Budget and GI warning evaluation.
- Diagnostics-only pure helpers.
- Async-submission and validation patch methods.

`Njulf.Rendering/Diagnostics/RendererDiagnosticsAssemblyInput.cs`

- Top-level grouped input contract.
- Domain input/snapshot records.
- `RendererDiagnosticsAssemblyResult`.
- `RendererDiagnosticsSubmissionPatch`.

`Njulf.Tests/RendererDiagnosticsAssemblerTests.cs`

- Synthetic assembly characterization.
- Disabled/active/fallback cases.
- Budget pairing.
- warning persistence/reset.
- late-patch isolation.
- collection snapshot behavior.

`Njulf.Tests/RendererDiagnosticsAssemblyInputBuilder.cs`

- Test-only defaults and named scenario setup.
- No production dependency on the builder.

### Modified files

`Njulf.Rendering/VulkanRenderer.cs`

- Add `_diagnosticsAssembler` and construct it with renderer lifetime.
- Add the shallow grouped input-capture adapter.
- Replace the main method call with explicit input/result wiring.
- Delegate scene-history reset and late diagnostics patches.
- Remove `BuildDiagnostics`, moved helpers, and renderer-owned evaluators/trackers.

`Njulf.Tests/FirstPersonCameraTests.cs`, `Njulf.Tests/ReflectionProbeFrameTelemetryTests.cs`, and relevant Simple-DDGI tests

- Update internal helper ownership only where helpers move out of `VulkanRenderer`.
- Preserve the tested behavior and test names unless the old owner is encoded in the name.

### Files that should remain schema-stable

`Njulf.Rendering/Data/RendererDiagnostics.cs`

- No field, constructor, default, or serialization changes for this extraction.

`Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`

- No output schema changes.

`Njulf.Rendering/Data/SceneRenderingData.cs`

- No new telemetry fields unless a missing coherent snapshot cannot otherwise be represented. Prefer the grouped assembly input over expanding this already-large frame data model.

## 8. Test matrix

### Pure assembly tests

- Empty/minimal input produces all current disabled/unavailable states and no unexpected warnings.
- Requested GI with emergency fallback preserves authored intent while reporting the effective disabled path.
- Active Simple-DDGI maps counters, storage/layout, scheduling, residency, content, liveness, and warning data correctly.
- Missing ray-query support and inactive acceleration structures produce the existing fallback state and reason.
- Reflection, shadows, foliage, particles, post-processing, debug overlays, and scene-submission fields map from their input groups.
- Render-graph and production-pass inventories retain ordering and counts.
- Async candidate/enabled paths and segment diagnostics retain ordering, timeline values, and fallback details.
- Capture run/camera/frame/GI measurement metadata remains stable for known inputs.
- Largest texture and meshlet-quality collections retain the current cap and ordering and are not aliases of mutable owner lists.

### Stateful tests

- GI blackout/support warnings require the same number of consecutive frames as before.
- Scene-history reset clears only the evaluator state currently reset by `VulkanRenderer`.
- DDGI persistent warning counters retain their existing lifetime behavior.
- Budget evaluation uses the intermediate diagnostics for the same frame and returns the matching snapshot.
- Repeated assembly does not retain or mutate prior `SceneRenderingData` or input snapshots.

### Patch tests

- Async-submission patch changes only submitted graphics and compute segment counts.
- Validation patch changes only the validation message counts and first/last message fields currently refreshed at `EndFrame`; `ValidationMode` remains part of the main assembly snapshot as it is today.
- A patch applied to `RendererDiagnostics.Empty` is safe and deterministic.
- Budget output is not silently recomputed by a late diagnostics patch.

### Compatibility tests

- `RendererDiagnostics.Empty` remains unchanged.
- Reflection-based property name/type inventory is identical before and after extraction.
- Performance snapshot JSON keys and nested shapes are identical.
- Existing benchmark, qualification, capture-evidence, and editor consumers compile without changes to public contracts.

### Runtime cases

- First frame, warmed frame, and frame with delayed GPU readback.
- Scene load/reload and camera cut.
- Resize and swapchain recreation.
- GI active, disabled, emergency fallback, and detailed-investigation modes.
- Async compute disabled, accepted, and graphics-fallback paths.
- Debug overlays on/off, including DDGI marker counters.
- Screenshot and RenderDoc requested/completed/error states.
- Validation off and validation-enabled runs.

## 9. Verification commands

Run from the repository root:

```powershell
dotnet build Njulf.Rendering/Njulf.Rendering.csproj --no-restore

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~RendererDiagnosticsAssemblerTests|FullyQualifiedName~RendererDiagnosticsTests|FullyQualifiedName~GiWarningEvaluatorTests|FullyQualifiedName~GiFeatureStateFactoryTests|FullyQualifiedName~RenderBudgetEvaluatorTests|FullyQualifiedName~PerformanceSnapshotWriterTests|FullyQualifiedName~ReflectionProbeFrameTelemetryTests"

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore

dotnet build Njulf.sln --no-restore
```

Structural checks:

```powershell
rg -n "BuildDiagnostics|_budgetEvaluator|_giWarningEvaluator|_ddgiDiagnosticWarningTracker" Njulf.Rendering/VulkanRenderer.cs

rg -n "VulkanRenderer|CommandBuffer|Silk.NET.Vulkan" Njulf.Rendering/Diagnostics/RendererDiagnosticsAssembler.cs

rg -n "_lastDiagnostics\s*=" Njulf.Rendering/VulkanRenderer.cs
```

Expected final structural result:

- No `BuildDiagnostics` implementation remains in `VulkanRenderer`.
- No evaluator/tracker fields remain in `VulkanRenderer`.
- The only `_lastDiagnostics` assignments are publication of the assembly result and the two intentional late patches.
- The assembler contains no backreference to `VulkanRenderer` and no Vulkan command/resource-lifetime dependency.

Do not use exact line-count reduction as the only gate, but verify that `VulkanRenderer.cs` loses at least the current approximately 2,468-line assembly body.

## 10. Risks and mitigations

### Risk: silent field drift during a very large move

Mitigation: make the first move mechanical; preserve statement and initializer order; freeze schema and serialized keys; use representative deep snapshot comparisons; review reason strings and disabled branches separately.

### Risk: replacing one god object with a giant untyped context

Mitigation: use named domain input groups, immutable owner snapshots, and explicitly borrowed read-only frame/settings references. Reject dictionaries, tuples with dozens of items, `dynamic`, callbacks, and a `VulkanRenderer` reference.

### Risk: torn snapshots from repeated manager reads

Mitigation: capture each owner once at the existing end-of-`DrawScene` boundary. Add an owner-level immutable snapshot where several values must represent one generation.

### Risk: warning behavior changes because evaluator lifetime changes

Mitigation: give the assembler renderer-instance lifetime; explicitly route the existing scene-revision reset; separately test `GiWarningEvaluator` and `DdgiDiagnosticWarningTracker` histories.

### Risk: budget and diagnostics describe different frames

Mitigation: return both from one assembly result and assign them together. Never let the assembler write a renderer field indirectly.

### Risk: late `EndFrame` information is lost or moved too early

Mitigation: retain explicit async-submission and validation patch APIs at their current call sites and test field-level patch isolation.

### Risk: additional full-record allocations

Mitigation: preserve the existing number of `RendererDiagnostics` construction/clone stages during extraction. Split calculations into value snapshots, not a chain of feature-specific `with` clones.

### Risk: shared helper creates a reverse dependency

Mitigation: pass a precomputed value or move the helper to a neutral timing/capture/diagnostics owner. Never call a static helper on `VulkanRenderer` from the assembler.

### Risk: concurrent work in `VulkanRenderer.cs` is overwritten

Mitigation: implement against the current working tree, move only the audited diagnostics regions, preserve unrelated uncommitted changes, and inspect the final diff for edits outside the planned call sites/helpers.

### Risk: the new assembler remains large

Mitigation: accept size that comes from cohesive mapping of the existing 2,391-line `RendererDiagnostics` schema. This extraction fixes ownership and change isolation first. Schema decomposition is a separate follow-up and must not be mixed into a behavior-preserving move.

## 11. Definition of done

The extraction is complete when all of the following are true:

1. `VulkanRenderer.BuildDiagnostics` no longer exists.
2. `RendererDiagnosticsAssembler` owns the full main mapping, budget evaluation, GI feature/warning derivation, and late field-patch mapping.
3. `VulkanRenderer` captures grouped inputs, invokes the assembler, publishes the result pair, and triggers lifecycle patches/resets only.
4. The assembler has no dependency on `VulkanRenderer`, Vulkan commands/handles, or renderer resource lifetime.
5. `RendererDiagnostics`, `RenderBudgetSnapshot`, performance snapshot JSON, public interfaces, and consumer behavior remain schema-compatible.
6. Assembly still occurs before debug-frame clearing, and late async/validation patches remain at their existing `EndFrame` points.
7. Same-frame diagnostics/budget pairing and stateful warning behavior are covered by focused tests.
8. Focused tests, the full `Njulf.Tests` suite, and the solution build pass.
9. Representative runtime validation is clean across scene reload, resize, GI modes, async modes, debug tooling, capture, and validation transitions.
10. Warmed diagnostics assembly has no material CPU or allocation regression.
11. `VulkanRenderer.cs` loses at least the approximately 2,468-line assembly body without replacing it with a partial-class split or a generic service-locator context.

## 12. Follow-up work explicitly deferred

After this extraction is stable, create separate plans if needed for:

- Decomposing `RendererDiagnostics` into nested feature snapshots while preserving external serialization compatibility.
- Extracting performance-capture identity and hashing into a dedicated provider.
- Extracting DDGI runtime/content diagnostics factories from remaining renderer-local capture adapters.
- Replacing direct concrete owner snapshots with narrow diagnostics-source interfaces where test or threading requirements justify them.
- Measuring and reducing the intrinsic allocation cost of the very large reference-record snapshot.

None of those follow-ups is required to complete this ownership extraction.
