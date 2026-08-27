# RendererLifetimeCoordinator Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor.

## 1. Required outcome

Extract the renderer-level lifecycle state and transition rules currently spread across `Njulf.Rendering/VulkanRenderer.cs` into one internal `RendererLifetimeCoordinator` under `Njulf.Rendering/Core`.

The completed refactor must:

- Give one object ownership of successful-initialization state, active-frame state, pending swapchain-recreation intent, terminal frame-submission fault state, device-loss state, startup-step logging, and retryable disposal progress.
- Replace direct reads and writes of the corresponding renderer booleans, fault text, disposal lock, retained `StagedDisposalPlan`, and disposal device-idle result with narrow coordinator operations.
- Make the legal transition points explicit without pretending that all lifecycle facts fit into one mutually exclusive enum.
- Keep initialization work, Vulkan waits/acquisition/recording/submission/presentation, swapchain/resource rebuilding, fault cleanup effects, and the concrete resource-disposal dependency graph in their existing owners.
- Preserve lazy and idempotent initialization, retry after initialization failure, exact public guard behavior and messages, minimized-window recreation behavior, fail-closed submission faults, and durable staged-disposal retry.
- Preserve the renderer's current single-threaded frame assumptions and its narrower synchronization guarantee for disposal; this extraction must not advertise new general thread safety.
- Preserve all public renderer APIs and all diagnostics, settings, capture, startup-log, and serialized schemas.

The intended relationship is:

```text
              VulkanRenderer public API
                         |
                         v
             RendererLifetimeCoordinator
       admission + state transitions + guard text
                         |
          +--------------+---------------+
          |              |               |
          v              v               v
   renderer-owned   renderer-owned   retained staged
   Vulkan effects   fault cleanup    disposal progress
          |              |               |
          +--------------+---------------+
                         |
                         v
             existing subsystem owners
```

`RendererLifetimeCoordinator` is not a resource manager and must not become a second renderer. It must not retain `VulkanRenderer`, `IWindow`, `RenderSettings`, command buffers, queues, swapchain handles, render targets, passes, or managers.

## 2. Audited starting point

### 2.1 Size and distribution

`VulkanRenderer.cs` is 23,084 lines in the audited working tree. Renderer-lifetime behavior is distributed across otherwise unrelated regions:

| Region in the audited tree | Current responsibility |
| --- | --- |
| lines 98-99 | Dependency-ownership policy and optional `RendererStartupLog`. |
| lines 417-425 | Initialization, disposal, active-frame, and recreation fields. |
| lines 535-537 | Device-loss and terminal frame-fault fields. |
| lines 726-1093 | Eleven pre-initialization configuration guards. |
| lines 1339-1429 | Construction, settings event subscription, ownership flag, and startup-log capture. |
| lines 1437-1668 | Idempotent initialization and the successful initialization commit point. |
| lines 2653-2673 | Renderer startup-step logging wrapper. |
| lines 3568-3855 | Lazy initialization, frame admission, acquire/recreate behavior, and active-frame publication. |
| lines 3857-4075 | End-frame guard, terminal submission/presentation, frame completion, and post-present recreation. |
| lines 10489-11036 | Async recording/submission failures, terminal fault latching, cleanup, and future-frame rejection. |
| lines 21388-21400 | Resize-triggered recreation intent and immediate/deferred attempt policy. |
| lines 22134-22242 | Concrete swapchain recreation and frame-in-progress guards. |
| lines 22309-22344 | Initialization-sensitive dynamic-resolution/caustic decisions. |
| lines 22541-23044 | Public disposal, retryable disposal-plan construction/drain, device-idle capture resolution, and disposal guard. |

The state itself is small, but its transition boundaries protect expensive and unsafe Vulkan effects. The extraction is therefore primarily about making ownership and ordering auditable, not maximizing line removal.

### 2.2 Current lifecycle facts are orthogonal

The renderer currently uses independent facts rather than a single phase:

- `_isInitialized` is monotonic after a successful initialization and remains true during a frame, after a submission fault, and during/after disposal.
- `_frameInProgress` is true only after most of `BeginFrame` has completed and is cleared after successful presentation or terminal fault cleanup.
- `_swapchainNeedsRecreate` may be true before a frame, during a suboptimal acquired frame, after presentation, or while a minimized window prevents recreation.
- `_deviceLost` is monotonic and is distinct from the textual submission-fault latch.
- `_frameSubmissionFaulted` permanently blocks future `BeginFrame` calls but does not currently block every other public operation, such as `Resize`.
- `_disposeStarted` is monotonic once a complete disposal plan has been prepared. `_disposeCompleted` is set only after every retained stage completes.
- A failed initialization leaves `_isInitialized == false` even though some renderer-owned resources may already have been created.

Do not replace these facts with one enum such as `Created -> Initialized -> Rendering -> Faulted -> Disposed`. That model cannot represent the current overlaps without either losing information or changing behavior. A small immutable internal snapshot may expose the facts for tests, but the coordinator's source of truth should remain explicit orthogonal state.

### 2.3 Initialization behavior

`Initialize` currently:

1. Calls the disposal-start guard before any other work.
2. Returns immediately after a previous successful initialization.
3. Resolves startup capture identity before optional backend and immutable graph selection.
4. Constructs renderer-owned runtime objects and managers in the existing order.
5. Registers graph resources and creates pipelines through named startup steps.
6. Resolves post-pipeline executable/worktree identity.
7. Initializes the render graph, persists the pipeline cache, registers static buffers, and ensures per-image render-finished semaphores.
8. Sets `_isInitialized = true` only after all preceding work succeeds.

There is no transactional rollback. If any operation throws, the method leaves `_isInitialized` false and a later call may retry against the partial resource state. The extraction must preserve that behavior. Introducing an `InitializationFailed` terminal state or suppressing later attempts is out of scope.

The eleven configuration methods guard only successful initialization. Some perform argument validation before the guard, and none currently add a disposal-start check. In particular, a renderer disposed before it ever initialized may still pass these configuration guards. That surprising behavior must remain unless changed in a separately reviewed hardening change.

### 2.4 Startup-step logging

The renderer's `RunStartupStep` has specific behavior:

- It validates the action.
- If there is no log or `RendererStartupLog.Path` is null, it invokes the action directly.
- Otherwise it publishes Started, invokes the action, then publishes Succeeded.
- It publishes Failed with the original exception and rethrows that same exception.
- The supplied `RendererStartupLog` is externally owned and is not disposed by `VulkanRenderer`.

`VulkanContext` has a separate startup-step helper for its own construction. This extraction should centralize only the renderer-level wrapper; it must not absorb context startup or alter the JSONL schema.

### 2.5 Begin-frame behavior

The current frame admission order is significant:

1. Reject after disposal starts.
2. Lazily call the same public `Initialize` path if initialization has not succeeded.
3. Reject after any terminal frame-submission fault.
4. Reject a nested `BeginFrame` with the exact existing message.
5. If recreation is pending, attempt it before fence reuse; return `false` and retain the request if the window has zero extent.
6. Wait the current frame-slot fence and consume every matching fence-complete readback/retirement/timing result.
7. Acquire a swapchain image.
8. On `ErrorOutOfDateKhr`, request and attempt recreation, then return `false` even if the immediate recreation succeeds.
9. On `SuboptimalKhr`, continue the frame while retaining recreation intent for the post-present boundary.
10. Reset pools, begin the primary command buffer, and reset per-frame subsystem state.
11. Set `_frameInProgress = true` immediately before `_gpuTimestamps.BeginFrame`.

The last boundary is not ideal if timestamp initialization throws, but moving it would be a behavior change. The first extraction must publish the coordinator's active-frame state at that exact point. A later transactional frame-begin change can be proposed separately.

### 2.6 End-frame behavior

`EndFrame` currently:

1. Rejects after disposal starts.
2. Rejects without a successful `BeginFrame` using an EndFrame-specific message.
3. Ends the terminal command buffer and submits deferred async segments.
4. Resets the terminal fence immediately before the terminal graphics submission.
5. Performs fence-only recovery after a non-device-loss terminal-submit failure, but still permanently stops rendering.
6. Publishes submission ownership/readback/timing facts only after terminal submit succeeds.
7. Presents and accepts Success, OutOfDate, or Suboptimal as non-throwing present outcomes.
8. Advances frame indices and synchronization, then clears `_frameInProgress`.
9. Requests/attempts recreation after the frame is no longer active.
10. Refreshes validation diagnostics and throws any validation failure after frame completion.

The active-frame clear must remain before post-present recreation and validation. A recreation or validation exception after a successful present must not leave the coordinator believing a frame is active.

`Clear` and `DrawScene` use a different exact guard message: `"{operation} requires a successful BeginFrame call."` Preserve both forms rather than forcing all frame guards through one generic error string.

### 2.7 Swapchain recreation behavior

Recreation intent and concrete recreation are separate concerns:

- `Resize` sets recreation intent before checking dimensions or active-frame state.
- Non-positive dimensions retain the request and return without touching Vulkan resources.
- Resize during command recording retains the request for the next legal boundary.
- `RecreateSwapchain` throws if command recording is in progress.
- `SwapchainManager.RecreateSwapchain` returns `false` for a zero-sized surface; this is not a terminal fault.
- Successful recreation clears the request only at the caller after all dependent targets, descriptors, pipelines, graph state, and timing history are rebuilt.
- A failed/false recreation attempt never clears the request.
- Resize currently is not rejected merely because a prior submission fault was latched.

The coordinator should own request/clear/admission state. The renderer must continue to own the device-idle wait, capture completion/failure, target and pipeline rebuilds, descriptor registration, render-graph notification, and subsystem-specific timing resets.

### 2.8 Terminal frame faults and device loss

`MarkFrameSubmissionFault` is the single fail-closed adapter used by fence waits, acquire-time device loss, command-buffer end, async plan recording/projection, deferred submits, terminal fence reset/submit, presentation, and targeted descriptor-reader fence waits.

It currently performs this sequence:

1. Monotonically record device loss when the Vulkan result is `ErrorDeviceLost`.
2. Latch the renderer submission fault and normalize blank text to `"A Vulkan frame submission failed."`.
3. Fail all screenshot and linear-HDR captures with the normalized reason.
4. Abort DDGI receiver-feedback capture.
5. Permanently latch async-compute emergency fallback.
6. Clear the current timing record and deferred async submissions.
7. Clear `_frameInProgress`.

Future `BeginFrame` calls throw an `InvalidOperationException` with one of two exact prefixes, selected by the monotonic device-loss fact, followed by the retained reason.

The terminal graphics-submit path records device loss before it decides whether fence-only recovery is legal. The coordinator API must support that early monotonic update without requiring the final reason to be known yet.

The coordinator should own the fault/device-loss facts and their guard text. The renderer should retain the screenshot/HDR/DDGI/async/timing cleanup effects in `MarkFrameSubmissionFault`. Use a two-phase fault adapter or another explicit API that preserves the current state/effect/frame-abandonment order; do not give the coordinator references to those subsystems.

### 2.9 Disposal behavior

The current protected virtual `Dispose(bool)` provides stronger guarantees than a conventional one-shot list of `Dispose` calls:

1. Non-disposing calls are ignored.
2. A renderer whose disposal completed returns immediately.
3. The full `StagedDisposalPlan` is constructed before `_disposeStarted` is published. Allocation/validation failure while building the plan leaves the renderer operational and retryable.
4. After successful plan construction, disposal start is monotonic, the quality-preset event is unsubscribed, and the plan is retained.
5. `StagedDisposalPlan.TryDrain` records per-stage success durably, continues independent stages, gates dependents, aggregates failures, and retries only pending stages on a later `Dispose` call.
6. Any drain failure is rethrown while the renderer remains terminal to public operational methods.
7. Completion is published only if every stage completed. A no-failure/incomplete outcome throws an explicit consistency exception.
8. The existing outer disposal lock is held across plan preparation, start publication, event unsubscription, plan drain, and completion publication.

The first plan stage records raw `DeviceWaitIdle`. Screenshot and HDR capture-resolution stages then complete captures only for Success without a previously recorded device loss; otherwise they fail captures with the exact result text. Every renderer-owned resource stage depends on device idle. When `_ownsDependencies` is true, the plan additionally retires injected managers and enforces the model-upload -> material -> mesh -> pending texture retirements -> deleter -> texture -> resource-owner barrier -> bindless -> buffer manager -> context chain.

`StagedDisposalPlan` already owns topological execution, concurrent-drain serialization, reentrancy detection, exact-once completion, and retry. The new coordinator must compose it, not duplicate that algorithm.

### 2.10 Existing test constraints

`StagedDisposalPlanTests` already cover retry at every dependency, independent failure aggregation, concurrent drains, and invalid dependencies.

`RendererTeardownOrderTests` currently parses `VulkanRenderer.cs` source text to assert:

- selected resource ordering and dependency declarations;
- the location and name of `CreateDisposalPlan`;
- literal `ThrowIfDisposalStarted();` at the beginning of selected public methods;
- literal ordering of plan preparation, `_disposeStarted`, event unsubscription, and plan publication.

Those tests will fail on a correct extraction because they assert implementation spelling. Preserve the resource-order tests while the graph remains in the renderer, update them for the honest `CreateResourceDisposalPlan` name, and move lifecycle semantics to direct coordinator tests. Retain only a narrow renderer source integration audit where constructing a real Vulkan renderer is impractical.

## 3. Scope boundary

### In scope

- One internal sealed `RendererLifetimeCoordinator` in `Njulf.Rendering.Core`.
- One small internal immutable submission-fault value if needed by the renderer effect adapter.
- Successful-initialization admission/commit and exact retry semantics.
- Renderer-level startup-step logging.
- Pre-initialization configuration guards.
- Active-frame admission, requirement, completion, and abandonment state.
- Swapchain-recreation request, success acknowledgement, and active-frame rejection.
- Terminal submission-fault and device-loss state plus exact future-frame guard text.
- Disposal-start/completion synchronization, retained plan, plan-factory ordering, drain retry, and device-idle result state.
- Narrow integration changes in `VulkanRenderer` and lifecycle-focused tests.
- Semantic rename of `CreateDisposalPlan` to `CreateResourceDisposalPlan` so it is not confused with coordinator state ownership.

### Out of scope

- Moving the body of renderer resource initialization out of `VulkanRenderer`.
- Transactional initialization rollback or cleanup of partially initialized resources.
- Moving the concrete resource-disposal graph or its resource actions into the coordinator.
- Changing manager/pass ownership or adding a service locator, resource bag, or dozens of coordinator constructor dependencies.
- Vulkan fence waits, image acquisition, command recording, queue submission, presentation, terminal fence recovery, or validation handling.
- Swapchain and render-target reconstruction effects.
- Screenshot, HDR, DDGI, async-compute, or timing cleanup effects after a fault.
- General thread safety for initialization, frame execution, resize, settings, diagnostics, or resource access.
- Device-loss recovery, renderer restart, frame cancellation, or a new public lifecycle API.
- Changing configuration calls after dispose-before-initialize.
- Diagnostics or performance-capture schema changes.

## 4. Non-negotiable invariants

1. All public `VulkanRenderer` signatures and the protected virtual `Dispose(bool)` shape remain unchanged.
2. `Initialize` rejects disposal first, is idempotent only after success, and commits initialized state only after the full current body returns.
3. An initialization exception is rethrown unchanged and leaves initialization retryable; no new rollback or terminal failed state is introduced.
4. Startup shader identity remains before optional backend/graph selection. Post-pipeline executable/worktree identity remains after `CreatePipelines` and before later admission consumers.
5. Startup-step Started/Succeeded/Failed ordering, exception identity, log-path gating, and external log ownership remain unchanged.
6. Every pre-initialization configuration method retains its exact exception message and current argument-validation order.
7. Configuration guards test only successful initialization. They do not silently acquire a disposal guard.
8. `BeginFrame` retains the exact disposal -> lazy initialization -> terminal fault -> nested-frame -> pending-recreation admission order.
9. A pending recreation that cannot proceed because the surface is zero-sized causes `BeginFrame` to return `false` and remains pending.
10. Acquire OutOfDate requests/attempts recreation and returns `false` even when that attempt succeeds. Acquire Suboptimal proceeds with a frame and retains recreation intent.
11. Active-frame state is published at the current line immediately before `GpuTimestampRecorder.BeginFrame`; the extraction does not repair that transaction boundary.
12. `EndFrame`, `Clear`, and `DrawScene` retain their distinct exact failure messages.
13. Successful `EndFrame` clears active-frame state after frame/sync advancement and before recreation or validation refresh.
14. Resize records recreation intent before dimension/active-frame checks and does not clear it on a skipped or unsuccessful attempt.
15. Concrete recreation remains forbidden while a frame is active with the exact current exception text.
16. Device-loss state is monotonic. A later non-device-loss fault cannot clear it.
17. A terminal fault permanently blocks future frame acquisition and retains the exact normalized reason/prefix formatting.
18. The terminal-submit path observes device loss before considering fence-only recovery.
19. Fault cleanup preserves screenshot/HDR failure, DDGI abort, async fallback, timing/deferred-work clearing, and final active-frame abandonment order.
20. Disposal may begin from uninitialized, partially initialized, initialized, active-frame, or faulted state exactly as today; it does not require a successful `EndFrame`.
21. The complete disposal plan is prepared before disposal start becomes visible. Plan-construction failure leaves operational guards open and allows a later retry.
22. Once disposal starts, operational methods fail closed even if one or more disposal stages fail.
23. The quality-preset event unsubscription remains between disposal-start publication and retained-plan publication. After the plan is retained, later drain retries do not unsubscribe again.
24. A retained disposal plan is reused across retries. Completed stages are never repeated; failed/pending stages remain retryable.
25. The disposal lock remains held across plan preparation, start publication, event unsubscription, plan publication, drain, and completion publication.
26. Device idle remains the first disposal stage and every renderer resource stage remains dependent on it.
27. Capture resolution continues to distinguish DeviceWaitIdle Success plus no device loss from every failure/device-loss outcome and preserves exact messages.
28. `_ownsDependencies == false` continues to skip disposal of injected dependency owners while still disposing renderer-created resources.
29. The existing resource dependency edges and topological order remain unchanged.
30. The coordinator owns no Vulkan handle or resource and performs no steady-frame allocation, logging, reflection, file I/O, or manager lookup.
31. No new lock is taken in the steady frame path. Only the existing disposal guard/coordination synchronization is preserved.
32. No lifecycle diagnostics field or serialization contract is added as part of the extraction.

## 5. Target design

### 5.1 Coordinator state

Add an internal sealed class in `Njulf.Rendering.Core`. The exact member names may be refined during implementation, but ownership should follow this shape:

```csharp
internal sealed class RendererLifetimeCoordinator
{
    private readonly object _disposalGate = new();
    private readonly string _disposedObjectName;
    private readonly RendererStartupLog? _startupLog;

    private bool _initializationSucceeded;
    private bool _frameInProgress;
    private bool _swapchainRecreationRequested;
    private bool _deviceLost;
    private bool _submissionFaulted;
    private string _submissionFaultReason = string.Empty;

    private bool _disposalStarted;
    private bool _disposalCompleted;
    private StagedDisposalPlan? _disposalPlan;
    private Result _disposalDeviceIdleResult = Result.ErrorUnknown;
}
```

Using `Silk.NET.Vulkan.Result` for the scalar device-idle outcome is acceptable in this Vulkan-specific Core class. It must not accept or retain a device, queue, fence, command buffer, swapchain, or manager.

Keep `_ownsDependencies` in `VulkanRenderer`. It is composition-root policy used while constructing the concrete resource graph, not coordinator transition state.

Do not add an `Initializing` or `InitializationFailed` terminal phase in the first extraction. The coordinator can wrap the initialization action and commit its success, but it should preserve current retry and unsupported-concurrency behavior rather than invent a new public contract.

### 5.2 Initialization and startup API

Prefer an action wrapper that makes the successful commit impossible to forget:

```csharp
internal bool Initialize(Action initializeCore);
internal void RunStartupStep(string name, Action action);
internal bool InitializationSucceeded { get; }
internal void ThrowIfInitializationSucceeded(string message);
```

`Initialize` must:

1. Validate the supplied action.
2. call `ThrowIfDisposalStarted`;
3. return `false` without invoking the action if initialization already succeeded;
4. invoke the action without holding the disposal lock;
5. set success only after the action returns; and
6. return `true` when it performed the attempt successfully.

`VulkanRenderer.Initialize` then becomes a narrow wrapper around a private `InitializeCore` containing the existing resource effects. Keep the public method and lazy `BeginFrame` call intact.

The coordinator does not add synchronization around initialization. The existing renderer is not safe for concurrent `Initialize`/frame/resource calls, and holding the disposal lock across the several-hundred-line Vulkan startup would create a new blocking/deadlock contract. Document that the action is renderer-thread-affine.

`ThrowIfInitializationSucceeded` checks only the monotonic initialization fact. Call it at each existing configuration guard location after any argument validation that currently precedes the guard.

### 5.3 Frame-state API

Expose intent-specific operations rather than public setters:

```csharp
internal bool FrameInProgress { get; }

internal void ThrowIfSubmissionFaulted();
internal void EnsureCanBeginFrame();
internal void MarkFrameStarted();
internal void EnsureCanEndFrame();
internal void EnsureFrameInProgress(string operation);
internal void CompleteFrame();
internal void AbandonFrame();
```

- `EnsureCanBeginFrame` preserves `"BeginFrame was called while a frame is already in progress."`.
- `EnsureCanEndFrame` preserves `"EndFrame was called without a successful BeginFrame."`.
- `EnsureFrameInProgress` preserves the interpolated Clear/DrawScene message.
- `MarkFrameStarted`, `CompleteFrame`, and `AbandonFrame` are internal transition operations. They must not invoke renderer effects.

Do not introduce a disposable frame lease in this refactor. An automatic `finally`-based clear would change the current fail-closed and partially recorded-command behavior.

### 5.4 Recreation-intent API

Use explicit request/acknowledgement operations:

```csharp
internal bool SwapchainRecreationRequested { get; }
internal void RequestSwapchainRecreation();
internal void ObserveSwapchainRecreationAttempt(bool succeeded);
internal void EnsureSwapchainRecreationAllowed();
```

`ObserveSwapchainRecreationAttempt(false)` retains the request; `true` clears it. It must be called only after the renderer completes all existing dependent-resource rebuild work, not merely after a new swapchain handle is created.

`EnsureSwapchainRecreationAllowed` checks active-frame state and preserves `"Swapchain cannot be recreated while command recording is in progress."`. It does not add initialization, submission-fault, or disposal checks that are absent from the current private method.

Keep result interpretation in `VulkanRenderer`:

- OutOfDate requests recreation, attempts it, acknowledges success, and returns `false`.
- Suboptimal requests recreation and continues.
- Resize requests first, then decides whether an immediate attempt is legal/useful.
- EndFrame completes the frame first, then attempts a pending/post-present request.

### 5.5 Fault API and renderer effect adapter

Use a small immutable value for normalized fault state:

```csharp
internal readonly record struct RendererSubmissionFault(
    string Reason,
    bool DeviceLost);
```

The coordinator should provide:

```csharp
internal bool DeviceLost { get; }
internal void RecordDeviceLoss();
internal RendererSubmissionFault LatchSubmissionFault(
    string? reason,
    bool deviceLost);
internal void ThrowIfSubmissionFaulted();
```

`RecordDeviceLoss` is needed before terminal fence-recovery selection. `LatchSubmissionFault` monotonically combines device loss, normalizes the reason, overwrites the retained reason as the current method does, and returns the canonical value for cleanup messages.

Keep `VulkanRenderer.MarkFrameSubmissionFault` as the effect adapter:

```text
coordinator latches fault/device state and returns normalized reason
                         |
                         v
renderer fails capture requests and aborts DDGI feedback
                         |
                         v
renderer latches async fallback and clears timing/deferred work
                         |
                         v
coordinator abandons active-frame state
```

Do not move the subsystem calls into the coordinator. Add focused integration tests or a narrow source-order audit so a later edit cannot latch the fault but forget to abandon the frame.

`ThrowIfSubmissionFaulted` owns the exact existing future-frame exception construction. It must use the device-loss prefix whenever device loss was observed, even if the most recently retained fault result was not device loss.

### 5.6 Disposal API

The coordinator should own the outer gate, retained plan, and completion state through one operation such as:

```csharp
internal bool DrainDisposal(
    Func<StagedDisposalPlan> createPlan,
    Action onDisposalStarted);

internal void ThrowIfDisposalStarted();
internal bool DisposalStarted { get; }
internal bool DisposalCompleted { get; }
internal Result DisposalDeviceIdleResult { get; }
internal void RecordDisposalDeviceIdleResult(Result result);
```

`DrainDisposal` should return `true` only for the call that transitions to completed, so `VulkanRenderer` can retain its one successful debug message. Within the existing-equivalent monitor boundary it must:

1. return `false` if completion was already published;
2. if there is no retained plan, invoke `createPlan` into a local first;
3. only after the factory returns, publish disposal start;
4. invoke `onDisposalStarted` to unsubscribe the settings event;
5. retain the prepared plan;
6. drain it and rethrow any returned failure;
7. publish completion from `IsComplete`; and
8. throw the exact consistency exception if drain returned no failure while stages remain pending.

This deliberately preserves the current plan-start-unsubscribe-publication order, including the fact that plan-factory failure leaves disposal unstarted. Do not move plan construction outside the gate or drain outside it in the initial extraction.

`VulkanRenderer.Dispose(bool)` remains responsible for the `disposing` check, supplying `CreateResourceDisposalPlan`, supplying the settings-event unsubscription action, and writing the success diagnostic. Public `Dispose` remains responsible for `GC.SuppressFinalize`.

The first device-idle stage calls `RecordDisposalDeviceIdleResult`. The existing screenshot/HDR resolution actions read `DisposalDeviceIdleResult` and `DeviceLost` from the coordinator. The coordinator does not interpret those results or call a capture manager.

### 5.7 Renderer field replacement

Add one readonly renderer field:

```csharp
private readonly RendererLifetimeCoordinator _lifetime;
```

Construct it once from the optional externally owned startup log and the exact disposed object name `nameof(VulkanRenderer)`. Remove these renderer-owned fields after semantic usage migration:

- `_startupLog`;
- `_isInitialized`;
- `_disposeLock`;
- `_disposeStarted`;
- `_disposeCompleted`;
- `_disposalPlan`;
- `_disposalDeviceIdleResult`;
- `_frameInProgress`;
- `_swapchainNeedsRecreate`;
- `_deviceLost`;
- `_frameSubmissionFaulted`;
- `_frameSubmissionFaultReason`.

Retain `_ownsDependencies` in the renderer. Retain frame indices, fence values, image index, current command buffer, temporal serials, and every subsystem-specific lifecycle field in their current owner.

### 5.8 No broad state enum or public snapshot

A test-only/internal snapshot is optional, but no public lifecycle DTO or diagnostics schema should be introduced. If a snapshot helps avoid many testing properties, it must represent the orthogonal facts directly:

```csharp
internal readonly record struct RendererLifetimeSnapshot(
    bool InitializationSucceeded,
    bool FrameInProgress,
    bool SwapchainRecreationRequested,
    bool SubmissionFaulted,
    bool DeviceLost,
    string SubmissionFaultReason,
    bool DisposalStarted,
    bool DisposalCompleted,
    Result DisposalDeviceIdleResult);
```

Do not imply that taking this snapshot makes non-disposal renderer operations thread-safe. Prefer omitting it if the tests can use narrow properties without bloating the production API.

## 6. Integration with the other planned extractions

### PerformanceCaptureMetadataProvider

The lifetime coordinator wraps the attempt; the metadata provider owns identity effects inside `InitializeCore`. Every failed initialization attempt must still rerun the early shader identity and later post-pipeline identity at their existing boundaries. The coordinator must not cache or inspect capture identity.

### AsyncComputeCoordinator

The async coordinator owns async-specific retry/quarantine/fallback state. `RendererLifetimeCoordinator` owns the global renderer submission-fault/device-loss latch. `VulkanRenderer.MarkFrameSubmissionFault` remains the bridge: it publishes global fault state, tells async coordination to abort/latch, performs other subsystem cleanup, and abandons the frame. Neither coordinator should call the other directly.

The async plan's statement that global frame-fault ownership stays outside `AsyncComputeCoordinator` is satisfied by moving that ownership to this coordinator.

### DebugOverlayBuilder and ShadowFramePlanner

Both remain renderer-lifetime collaborators created by `VulkanRenderer`. They are not recreated with the swapchain and gain no direct dependency on this coordinator. Their operations continue to be admitted by the renderer's existing public/frame guards.

### RendererDiagnosticsAssembler

No lifecycle field is added to `RendererDiagnostics` in this extraction. If diagnostics later need a lifecycle summary, the diagnostics assembler may consume a deliberate immutable internal snapshot in a separate schema-reviewed change; it must not read coordinator internals piecemeal.

### Resource managers and StagedDisposalPlan

Managers retain resource ownership and their own disposal algorithms. `StagedDisposalPlan` retains stage execution/retry. The renderer remains the composition root that declares resource dependency edges. The lifetime coordinator owns only whether that prepared plan has been published, drained, retried, and completed.

Because `VulkanRenderer.cs` and the implementation directory contain unrelated working-tree changes, implementation must use narrow semantic edits and patches. Do not regenerate, replace, or revert the full renderer file.

## 7. Delivery order and change isolation

### Phase 0: Characterize existing behavior

1. Add direct characterization tests for initialization success, idempotency, and retry after a thrown initialization action.
2. Capture exact BeginFrame/EndFrame/Clear/DrawScene/recreation/fault/disposal exception messages.
3. Add state-order tests for recreation request retention and fault/device-loss monotonicity.
4. Extend disposal tests for plan-factory failure, first-start publication, failed drain retry, concurrent callers, and completed no-op behavior.
5. Record startup/normal-frame/minimize-resize/disposal smoke baselines before moving state.

Exit criterion: tests distinguish current transition order from plausible but incompatible alternatives.

### Phase 1: Add the coordinator shell

1. Add `RendererLifetimeCoordinator.cs` with initialization, frame, recreation, fault, startup, and disposal state but no renderer integration.
2. Add pure tests through `InternalsVisibleTo("Njulf.Tests")`.
3. Keep method names intent-specific and preserve exact exception text in one owner.
4. Do not add a dependency on renderer resources or public APIs.

Exit criterion: coordinator tests pass without constructing Vulkan objects.

### Phase 2: Migrate initialization and startup logging

1. Add `_lifetime` to the renderer and construct it with `startupLog` and `nameof(VulkanRenderer)`.
2. Split the existing public method into `Initialize` plus `InitializeCore` without reordering the body.
3. Route successful commit/idempotency/disposal admission through `_lifetime.Initialize(InitializeCore)`.
4. Route `RunStartupStep` calls through the coordinator and remove the renderer helper/field.
5. Replace all eleven direct `_isInitialized` configuration checks with the coordinator's initialization-only guard at the same statement location.
6. Replace the two dynamic-resolution/caustic reads with `InitializationSucceeded`.

Exit criterion: failed initialization remains retryable, startup log output is byte/record equivalent, and no `_isInitialized` or `_startupLog` reference remains in `VulkanRenderer`.

### Phase 3: Migrate frame and recreation state

1. Replace BeginFrame's nested-frame test, pending-recreation checks, and active-frame publication with coordinator calls in the exact current order.
2. Replace EndFrame/Clear/DrawScene guards and the successful frame-completion write.
3. Replace Resize, acquire, present, and private recreation flag writes with request/attempt acknowledgement operations.
4. Replace the private recreation active-frame guard.
5. Do not move resource effects or add `try/finally` around frame activation.

Exit criterion: the normal, nested, minimized, OutOfDate, Suboptimal, resize-during-frame, and post-present paths have identical results and state.

### Phase 4: Migrate terminal fault ownership

1. Move device-loss, submission-fault, and reason state plus future-frame guard text into the coordinator.
2. Preserve the early `RecordDeviceLoss` call before terminal fence-recovery selection.
3. Keep `MarkFrameSubmissionFault` in the renderer as the ordered subsystem cleanup adapter.
4. End the adapter with coordinator frame abandonment at the current final boundary.
5. Update async-coordinator integration if that extraction has already landed; keep the dependency mediated by the renderer.

Exit criterion: every current fault call site produces the same cleanup text, permanent frame rejection, recovery decision, and active-frame state.

### Phase 5: Migrate disposal coordination

1. Semantically rename `CreateDisposalPlan` to `CreateResourceDisposalPlan` and update resolved usages/tests.
2. Route protected `Dispose(bool)` through `DrainDisposal` while preserving the `disposing` check and successful debug message.
3. Move the outer lock, started/completed flags, retained plan, and device-idle result into the coordinator.
4. Leave every resource stage and dependency edge in `CreateResourceDisposalPlan` unchanged.
5. Route the first stage's result write and the two capture-resolution reads through the coordinator.
6. Replace every public operational disposal guard with `_lifetime.ThrowIfDisposalStarted()`.

Exit criterion: plan creation failure remains non-terminal, stage failure remains terminal but retryable, and all completed resource actions remain exact-once.

### Phase 6: Semantic cleanup and source-test repair

1. Use Rider semantic Find Usages for every old lifecycle field/helper before deleting its declaration.
2. Use semantic rename from `CreateDisposalPlan` to `CreateResourceDisposalPlan`; do not use a repository-wide textual replacement.
3. Update `RendererTeardownOrderTests` to locate the renamed resource graph and the `_lifetime` disposal guard.
4. Move plan/start/retry assertions out of renderer source parsing and into `RendererLifetimeCoordinatorTests`.
5. Audit every public operational method, including development pin/freeze and reflection-probe recapture methods currently omitted from the source test.
6. Confirm there is no compatibility wrapper or duplicate lifecycle source of truth.

Exit criterion: searches find no old fields/helpers, resource-order tests still protect the graph, and lifecycle behavior is tested directly.

### Phase 7: Automated and runtime verification

1. Run focused lifetime/startup/disposal tests.
2. Run the full test project and solution build.
3. Exercise startup, normal frames, minimized restore, resize during a frame, OutOfDate/Suboptimal presentation, injected submission failure/device loss, and disposal retry with Vulkan validation enabled.
4. Compare startup logs, exception text, capture cancellation results, frame progress, and resource teardown order with the baseline.

Exit criterion: source, unit, integration, and runtime checks pass without public/schema changes.

## 8. File-level implementation map

### New files

`Njulf.Rendering/Core/RendererLifetimeCoordinator.cs`

- Internal coordinator and optional small fault/snapshot contracts.
- Renderer-level startup wrapper.
- Initialization, frame, recreation, fault, and disposal transition rules.
- Exact lifecycle guard messages.
- Retained `StagedDisposalPlan` coordination and scalar device-idle outcome.

`Njulf.Tests/RendererLifetimeCoordinatorTests.cs`

- Pure lifecycle transition, exact-message, startup logging, device-loss, disposal ordering/retry, concurrency, and no-op completion tests.

### Modified files

`Njulf.Rendering/VulkanRenderer.cs`

- Add `_lifetime`; remove the twelve migrated lifecycle/startup fields.
- Split `InitializeCore` without changing effect order.
- Route guards and transition points through the coordinator.
- Retain frame/Vulkan/resource effects and fault cleanup.
- Rename and retain the concrete `CreateResourceDisposalPlan` graph.
- Route disposal device-idle/capture facts through the coordinator.

`Njulf.Tests/RendererTeardownOrderTests.cs`

- Update the resource-plan method name and guard spelling.
- Keep resource dependency/order coverage.
- Remove lifecycle assertions that are now directly covered by coordinator tests.
- Expand the narrow public-entry-point guard audit to all guarded operational methods.

`Njulf.Tests/RendererStartupLogTests.cs` (only if coverage is clearer there)

- Preserve exact renderer-level Started/Succeeded/Failed records and external ownership behavior. Prefer keeping coordinator-specific wrapper cases in `RendererLifetimeCoordinatorTests` to avoid splitting one behavior across suites.

### Expected to remain behavior/schema stable

- `Njulf.Rendering/Resources/StagedDisposalPlan.cs`
- `Njulf.Rendering/Core/SwapchainManager.cs`
- `Njulf.Rendering/Core/SynchronizationManager.cs`
- `Njulf.Rendering/Core/CommandBufferManager.cs`
- `Njulf.Rendering/Core/VulkanContext.cs`
- `Njulf.Rendering/Diagnostics/RendererStartupLog.cs`
- capture/readback managers and services;
- async-compute scheduler/timing/projection contracts;
- render settings and quality-preset event behavior;
- renderer diagnostics and performance-capture schemas;
- public `IRenderer` and `IRendererDebugTools` contracts.

If implementation requires changing one of these contracts, treat it as a separate reviewed behavior change rather than silently folding it into this extraction.

## 9. Test matrix

### Initialization and configuration

- Initial state reports not initialized.
- A successful initialization action runs once and commits success afterward.
- A second call is a no-op and does not repeat the action.
- An action exception is rethrown unchanged, leaves success false, and permits a later successful retry.
- Disposal-start rejection happens before initialization action invocation.
- Every configuration guard permits changes before successful initialization and rejects them afterward with its exact supplied message.
- Argument-null checks that currently precede initialization guards still win.
- Dispose-before-initialize does not newly change configuration-guard behavior.
- Dynamic-resolution/caustic initialization-sensitive decisions observe the same boolean boundary.

### Startup logging

- Null log and null normalized path invoke the action directly once.
- A configured path records Started then Succeeded with the same step name.
- A thrown action records Started then Failed and rethrows the same exception instance.
- A log whose file open failed but whose normalized `Path` is non-null retains current wrapper behavior.
- Disposing the coordinator does not dispose the externally supplied log; the coordinator itself should not implement resource ownership.

### Frame admission and completion

- Nested begin throws the exact current message.
- End without begin throws the exact EndFrame message.
- Clear/DrawScene requirement helpers retain their interpolated messages.
- A successful begin marker makes frame-required operations legal.
- Successful completion clears active state.
- Abandonment clears active state without marking a normal completion.
- Initialization and terminal-fault checks retain their relative BeginFrame order.
- Integration verifies active state is published immediately before GPU timestamp begin and cleared before post-present recreation/validation.

### Recreation intent

- A request remains set across skipped and failed attempts.
- Only a successful full recreation acknowledgement clears it.
- Recreation while a frame is active throws the exact current message.
- Non-positive resize and resize during an active frame retain intent.
- OutOfDate acquire returns `false` even after a successful immediate rebuild.
- Suboptimal acquire completes a frame and attempts recreation after presentation.
- Success after a previous completed rebuild does not recreate again.
- A faulted renderer is not given a new blanket Resize rejection.

### Fault and device-loss state

- Blank/whitespace reason normalizes to `A Vulkan frame submission failed.`.
- A non-device-loss fault uses the unsafe-resource-reuse prefix.
- Device loss uses the device-lost prefix.
- Device loss is monotonic across later fault latches.
- A later fault replaces the reason as current code does without clearing device loss.
- Device loss is visible before terminal fence recovery is selected.
- Fault cleanup uses the returned normalized reason in screenshot, HDR, DDGI, and async paths.
- Frame abandonment occurs after the current cleanup effects.
- Every current fault call site permanently blocks the next BeginFrame.

### Disposal coordination

- `disposing == false` remains a no-op at the renderer wrapper.
- Plan-factory failure does not publish disposal start, invoke event unsubscription, or retain a partial plan; a later attempt can retry.
- Successful plan preparation publishes start, invokes unsubscription once, and retains exactly that plan before drain.
- Public guards reject from the moment start is published, including after a stage failure.
- A failed drain rethrows the aggregate and a later call resumes the retained plan.
- Completed stages are never repeated; failed stages can run again.
- Independent stages continue while dependents remain gated, as covered by `StagedDisposalPlanTests`.
- Concurrent Dispose calls create one plan, unsubscribe once, and do not repeat completed stages.
- Same-thread reentrant drain retains the existing staged-plan failure behavior.
- A completed second Dispose call is a no-op and does not repeat the success diagnostic.
- No-failure/incomplete drain preserves the exact consistency exception.
- Device-idle result defaults to ErrorUnknown and records the raw first-stage result.
- Screenshot/HDR resolution succeeds only for Success plus no device loss and otherwise preserves exact cancellation text.
- Owned and borrowed dependency modes retain their exact graph membership.

### Resource dependency graph

- Every renderer resource stage still depends on device idle.
- Near-field residual and hybrid reflection runtimes precede render-graph cleanup.
- GI pipeline cache and directional history/caustic/render-target stages retain their graph dependencies.
- Ray-scene descriptor bank follows render graph and mesh pipeline; acceleration structures follow the bank; foliage proxies follow acceleration structures.
- Advanced-GI transient arena follows receiver-feedback, guiding, and near-field runtimes.
- Owned manager chain remains model upload -> material -> mesh -> pending texture retirements -> deleter -> texture.
- Resource-owner barrier still gates bindless -> buffer manager -> Vulkan context.
- Borrowed dependencies are never disposed by the renderer plan.

### Runtime matrix

| Case | Required evidence |
| --- | --- |
| First normal frame | Lazy initialization succeeds, frame activates once, presents, and completes. |
| Explicit Initialize twice | Startup effects/logging run once after success. |
| Injected initialization failure | Original failure escapes and the next call retries. |
| Nested BeginFrame / EndFrame without begin | Exact current exceptions and no unintended state change. |
| Minimize then restore | Recreation request survives zero extent and clears only after full rebuild. |
| Resize during frame | Request defers until the post-frame or next-begin legal boundary. |
| Acquire OutOfDate | No frame is published; method returns false even if rebuild succeeds. |
| Acquire/Present Suboptimal | Current frame completes and rebuild is attempted afterward. |
| Deferred/terminal submit failure | Capture/DDGI/async cleanup occurs and all later begins fail closed. |
| Device loss | No fence-only recovery is attempted and disposal capture resolution fails explicitly. |
| Dispose before Initialize | Prepared stages run against nullable resources and owned/borrowed policy is preserved. |
| Dispose after partial Initialize | Created resources drain without requiring initialized success. |
| Injected disposal-stage failure | Public work stays closed and a later Dispose resumes only pending stages. |

## 10. Verification commands

Run focused lifetime tests:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~RendererLifetimeCoordinatorTests|FullyQualifiedName~StagedDisposalPlanTests|FullyQualifiedName~RendererTeardownOrderTests|FullyQualifiedName~RendererStartupLogTests"
```

Run the full test project and solution build:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore
dotnet build Njulf.sln --no-restore
```

Audit old ownership and new integration:

```powershell
rg -n "_isInitialized|_disposeLock|_disposeStarted|_disposeCompleted|_disposalPlan|_disposalDeviceIdleResult|_frameInProgress|_swapchainNeedsRecreate|_deviceLost|_frameSubmissionFaulted|_frameSubmissionFaultReason|_startupLog" Njulf.Rendering/VulkanRenderer.cs
rg -n "ThrowIfDisposalStarted|ThrowIfFrameSubmissionFaulted|EnsureFrameInProgress|CreateDisposalPlan" Njulf.Rendering/VulkanRenderer.cs Njulf.Tests
rg -n "RendererLifetimeCoordinator|CreateResourceDisposalPlan|MarkFrameSubmissionFault" Njulf.Rendering Njulf.Tests
```

The first two searches should return no obsolete renderer-owned field/helper references after migration. Review the third search to confirm one coordinator instance, narrow renderer integration, the retained fault effect adapter, and the concrete resource graph's honest name.

Use Rider semantic Find Usages before deleting each old field/helper and semantic rename for `CreateDisposalPlan`. Source searches are a final ownership audit, not a substitute for resolved-reference refactoring.

For runtime validation, use the established startup and resize smoke scenarios with Vulkan validation enabled, plus controlled failure injection for initialization, queue submission/device loss where available, and one disposal stage. Record method result/exception, lifetime state, startup log sequence, capture cancellation result, async fallback state, and disposed stage attempts.

## 11. Risks and mitigations

### Risk: a single enum changes overlapping semantics

Keep initialization, frame, recreation, fault/device loss, and disposal as orthogonal facts. Test combinations such as initialized + faulted, frame active + recreation pending, and faulted + disposing.

### Risk: initialization becomes permanently failed

Commit only after `InitializeCore` returns. Do not cache an attempt exception or add an InitializationFailed terminal phase.

### Risk: initialization action runs under the disposal lock

Keep only the existing short disposal guard under the gate. Do not hold it across Vulkan startup. Document that frame/startup operations remain renderer-thread-affine.

### Risk: configuration methods gain stricter guards accidentally

Use an initialization-only coordinator guard at the exact current call position. Do not combine it with disposal admission in this extraction.

### Risk: frame-active publication is “cleaned up” while moving code

Move the write mechanically to `MarkFrameStarted` at the current pre-timestamp boundary. Do not add a frame lease or broad `finally` until separately designed against partially recorded commands.

### Risk: recreation request clears too early

Acknowledge success only after swapchain-dependent resources, descriptors, pipelines, graph notification, and timing resets finish. False/skipped/throwing attempts retain intent.

### Risk: terminal-submit recovery sees stale device-loss state

Call `RecordDeviceLoss` before `TryRecoverFrameFenceAfterTerminalSubmitFailure`. Keep the final fault reason latch after the recovery detail has been composed.

### Risk: coordinator becomes coupled to every faulted subsystem

Keep `MarkFrameSubmissionFault` as a renderer effect adapter. The coordinator returns normalized state and later receives `AbandonFrame`; it does not receive manager references or callbacks for ordinary rendering.

### Risk: fault adapter partially executes

Characterize the current ordering and retain it mechanically. Do not introduce `finally` behavior in the initial move; if cleanup itself can throw, address that with a separate fault-aggregation design.

### Risk: disposal begins before a valid graph exists

Invoke the plan factory into a local before setting the start flag. Add a test whose factory throws and verify public guards remain open.

### Risk: failed disposal reconstructs the graph and repeats work

Retain the exact prepared `StagedDisposalPlan` in the coordinator as soon as start publication/unsubscription succeeds. Retry that instance only.

### Risk: outer and staged disposal locks are simplified together

Preserve both locks and current lock scope during extraction. Lock consolidation has reentrancy/concurrency consequences and belongs in separate work.

### Risk: resource graph is hidden inside a new god object

Leave `CreateResourceDisposalPlan` in the renderer composition root. A later dedicated plan-factory extraction can introduce a typed action catalog if the remaining graph is still too large.

### Risk: source-contract tests encourage compatibility shims

Move transition semantics to direct coordinator tests and update only the narrow renderer integration checks. Do not keep old helper wrappers merely to satisfy string searches.

### Risk: other extraction plans duplicate lifecycle ownership

Keep the global fault and lifecycle rules here, async fallback in `AsyncComputeCoordinator`, capture identity in `PerformanceCaptureMetadataProvider`, and diagnostics composition in `RendererDiagnosticsAssembler`. Connect them only in `VulkanRenderer`.

### Risk: unrelated working-tree changes are overwritten

Use semantic usage tools and narrow `apply_patch` edits around audited fields/call sites. Never replace, regenerate, or revert the full dirty renderer file.

## 12. Definition of done

The extraction is complete only when all of the following are true:

- `RendererLifetimeCoordinator` exists under `Njulf.Rendering/Core` and has no renderer/resource-manager/handle ownership.
- One coordinator owns successful initialization, active-frame state, recreation intent, global submission fault/device loss, startup-step wrapper state, disposal gate/progress, retained plan, and device-idle outcome.
- All twelve old lifecycle/startup fields listed in section 5.7 are gone from `VulkanRenderer`; `_ownsDependencies` remains with the resource graph.
- Public `Initialize` is a narrow wrapper and the existing initialization effect order remains intact in `InitializeCore`.
- Failed initialization is retryable and successful initialization is idempotent.
- All eleven configuration guards retain exact messages and validation ordering.
- BeginFrame/EndFrame/Clear/DrawScene/Resize/recreation transition points and exact error text match the baseline.
- OutOfDate, Suboptimal, minimized, and resize-during-frame behavior is unchanged.
- `MarkFrameSubmissionFault` remains the sole renderer effect adapter, while global state/guard text is coordinator-owned.
- Device loss remains monotonic and is observed before terminal fence recovery selection.
- Disposal-plan preparation precedes start publication; failures after start reuse the retained plan and reject public work.
- The concrete resource graph, DeviceWaitIdle-first rule, capture resolution, ownership branch, and dependency edges are unchanged.
- `RendererTeardownOrderTests` no longer assert old lifecycle field spelling, while direct coordinator tests cover the semantics.
- No public API, settings, diagnostics, startup-log, capture, or serialized schema changes are introduced.
- Focused tests, the full test project, the solution build, and the runtime matrix pass.
- No new steady-frame allocation, lock, reflection, logging, file I/O, Vulkan query, or manager lookup is introduced by the coordinator boundary.

## 13. Follow-up work explicitly deferred

After the behavior-preserving extraction is stable, separate work may consider:

- extracting `CreateResourceDisposalPlan` into a dedicated factory backed by a typed disposal-action catalog;
- transactional initialization with rollback of partially created resources;
- an explicit renderer-thread-affinity guard or supported multi-threaded lifecycle contract;
- a scoped frame transaction that safely handles exceptions after command recording begins;
- device-loss recovery or full renderer re-creation;
- making resize and all configuration APIs consistently fail after disposal/fault;
- consolidating renderer and Vulkan-context startup-step wrappers;
- exposing a reviewed lifecycle diagnostics snapshot;
- simplifying the outer disposal lock after reentrancy/concurrency behavior is independently characterized.

None of these follow-ups should be mixed into the initial extraction.
