# DDGI Coordinators Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor delivered as independently landable extractions.

## 1. Required outcome

Extract the renderer-level DDGI and advanced-DDGI orchestration currently spread through `Njulf.Rendering/VulkanRenderer.cs` into a family of bounded coordinators under `Njulf.Rendering/Resources`.

This must not be implemented as one `DdgiCoordinator`, `GlobalIlluminationManager`, partial `VulkanRenderer`, or generic context object. The audited code contains several different state machines with different frame, fence, resize, and resource-lifetime boundaries. Combining them would only move the renderer's current DDGI god region into another file.

The target family is:

1. `AdvancedGiAdmissionCoordinator`
2. `DdgiSceneInvalidationCoordinator`
3. `DdgiEmissiveTransportCoordinator`
4. `SimpleDdgiReceiverFeedbackCoordinator`
5. `SimpleDdgiFrameEvidenceCoordinator`
6. `GiCausticFrameCoordinator`
7. `SimpleDdgiNearFieldResidualCoordinator`
8. `SimpleDdgiFrameCoordinator`

The existing `SimpleDdgiGuidingFrameCoordinator` remains the C3 runtime coordinator and is composed by the new core frame coordinator. It must not be replaced or duplicated.

The completed refactor must:

- Give every persistent DDGI field exactly one state owner.
- Keep `VulkanRenderer` as the public facade and global frame/render-graph boundary, while reducing its DDGI work to input capture, ordered coordinator calls, effect application, and result publication.
- Preserve the current Simple-DDGI, B1 receiver-feedback, C3 directional-guiding, C4 hero-caustic, and C5 near-field-residual behavior.
- Preserve the distinction between CPU planning, Vulkan resource ownership, render-graph recording, fence-complete observation, and final diagnostics assembly.
- Reuse the existing volume manager, Vulkan runtimes, mutation journal, foliage manager, light-tree resources, guiding coordinator, transient arena, generation transaction, planners, and reference models as leaf components.
- Introduce typed immutable requests, results, and snapshots at coordinator boundaries; do not pass `VulkanRenderer`, a service locator, an untyped property bag, or a single all-feature mega-context.
- Keep all public renderer APIs, settings, serialized data, diagnostic schemas, shader contracts, render-graph pass names, graph resource names, and externally visible reason strings stable.
- Make each extraction independently reviewable, buildable, testable, and revertible.

The intended high-level relationship is:

```text
                         VulkanRenderer
       public facade + global frame/graph/submission/lifetime effects
                                |
             +------------------+------------------+
             |                                     |
             v                                     v
  AdvancedGiAdmissionCoordinator       renderer-owned cross-feature facts
  manifests/evidence/admission          camera, lights, AS, targets, shadows
             |                                     |
             +------------------+------------------+
                                v
                    SimpleDdgiFrameCoordinator
              thin ordered composition for core DDGI
                                |
       +-------------+----------+----------+----------------+
       |             |                     |                |
       v             v                     v                v
 invalidation    emissive transport   receiver feedback  frame evidence
 coordinator      coordinator          coordinator        coordinator
       |             |                     |                |
       +-------------+----------+----------+----------------+
                                |
          +---------------------+--------------------+
          |                     |                    |
          v                     v                    v
  SimpleDdgiVolumeManager   existing C3       existing leaf managers
                            coordinator       and Vulkan runtimes
                                |
               +----------------+----------------+
               |                                 |
               v                                 v
     GiCausticFrameCoordinator       NearFieldResidualCoordinator
                C4                                C5
               |                                 |
               +----------------+----------------+
                                v
                    immutable frame snapshots
                                |
                    DdgiFrameDataProjector
                                |
                       SceneRenderingData
                                |
               render graph / diagnostics assembler
```

`SimpleDdgiFrameCoordinator` is deliberately last in the delivery order. It is an integration seam over already-extracted owners, not the first destination for thousands of lines of mixed DDGI code.

## 2. Audited starting point

### 2.1 Scale

`VulkanRenderer.cs` is 23,084 lines in the audited working tree. A case-insensitive source scan finds:

- 4,318 lines containing `ddgi`;
- 5,798 individual `ddgi` occurrences;
- 999 lines containing `AdvancedGi`, `GiCaustic`, or `NearFieldResidual`, with some overlap with the DDGI count;
- 93 test files whose names contain `Ddgi`, `GiCaustic`, or `AdvancedGi`.

These counts are not a proposed deletion target, but they demonstrate why one extraction boundary is inappropriate. The DDGI-related code spans public configuration, startup admission, scene mutation tracking, buffer upload, scheduling, per-frame planning, render-graph inputs, delayed readback, liveness, resize transactions, diagnostics, and disposal.

### 2.2 Existing components are leaf owners, not code to re-extract

The repository already contains substantial specialized owners:

| Existing component | Audited size | Existing responsibility |
| --- | ---: | --- |
| `SimpleDdgiVolumeManager` | 20,951 lines | Probe volumes, scheduling, paging, storage, transport, publication, warm starts, and many associated resource generations. |
| `SimpleDdgiReceiverFeedbackVulkanRuntime` | 2,430 lines | B1 GPU allocation, capture, reduction, readback, scheduling binding, and capture abort. |
| `SimpleDdgiNearFieldResidualVulkanRuntime` | 1,699 lines | C5 GPU buffers, stages, targets, histories, recording, and readback. |
| `DdgiFoliageProxyManager` | 1,663 lines | Foliage-proxy planning, generation, buffer ownership, and frame data. |
| `SimpleDdgiGuidingFrameCoordinator` | 1,277 lines | C3 frame preparation, recording, publication, completion, and source-cache/runtime consistency. |
| `SimpleDdgiLightTreeGpuResources` | 924 lines | Light-tree upload, recording, readback, and diagnostics. |
| `DdgiMutationJournal` | 731 lines | Scene attachment, bounded mutation collection, drain, oracle telemetry, and disposal. |
| `SimpleDdgiNearFieldResidualGenerationTransaction<T>` | 635 lines | Fence-driven active/prepared/retired C5 generations. |
| `AdvancedGiTransientBufferArena` | 407 lines | Transactional shared B1/C3 scratch buffer allocation, slices, generations, and retirement. |

The new coordinators compose these objects. They do not reimplement their algorithms, conceal them behind a repository-wide interface hierarchy, or merge them into a second volume manager.

### 2.3 Renderer state clusters

The renderer currently owns several unrelated DDGI state clusters:

| Audited region | Current state or behavior |
| --- | --- |
| lines 104-124 | Advanced-GI prerequisite and qualification manifests, runtime content binding/state, settings fingerprint, candidate profile/status, and aggregate graph modes. |
| lines 151-184 | Simple-DDGI volume, refinement focus, light-tree, B1 receiver-feedback runtime/plan/key, shared B1/C3 arena, C3 runtime/source cache/coordinator, and desired/applied C3 configurations. |
| lines 188-242 | C4 evidence/admission/plan/runtime/publication state and C5 evidence/profile/plan/runtime/generation state. |
| lines 243-251 | Far-field, acceleration-structure, ray-scene, and foliage-proxy DDGI collaborators. |
| lines 262-350 | Dirty-region scratch, mutation journal, tracked objects/poses/VFX/lights, emissive source construction and GPU publication, signatures, energy diagnostics, and warm-start identity state. |
| lines 460-474 | DDGI liveness watchdog, scheduler cost model, submitted-frame ring, pending evidence, and completed evidence. |
| lines 726-1145 | Public advanced-GI manifest, runtime-binding, evidence, profile, and evidence-bundle configuration. |
| lines 2684-3050 | Startup graph-mode resolution and cross-feature preflight. |
| lines 13753-14909 | B1 capture finalization, core DDGI preparation, C3 compilation/reconciliation, submitted evidence, and reflection-probe recapture scheduling. |
| lines 14911-16837 | Core/advanced frame-data projection, liveness evaluation, and final runtime/content diagnostic snapshot creation. |
| lines 16940-18010 | Emissive source enumeration, caching, hierarchy construction, buffer upload, diagnostics, refinement demands, and energy tracking. |
| lines 18031-19083 | Journal/oracle invalidation, scene-object/VFX/light/pose tracking, dirty coverage, and dynamic reset. |
| lines 19084-19368 | C4 frame preparation, producer creation, publication, and rejection. |
| lines 19488-19670 | Foliage-proxy frame preparation and dirty influence. |
| lines 19671-20268 | DDGI signatures, source relight, warm-start identity, dynamic geometry hashing, and bounds helpers. |
| lines 21402-21890 | C4 extent invalidation and C5 generation compile, replacement, frame-boundary commit, submission references, disable, and graph publication effects. |
| lines 22541-23074 | Tracked-record declarations, staged DDGI disposal, and emissive-buffer destruction. |

Line numbers identify the audited working tree and will drift. Implementation must use symbol ownership and semantic usages, not line-number slicing.

### 2.4 Current core frame order

Within `DrawScene`, the significant ordering is:

1. Upload the environment.
2. Prepare DDGI foliage proxies before acceleration-structure preparation.
3. Prepare and record acceleration structures.
4. Prepare reflections and shadows.
5. Call `PrepareDdgiProbeVolumes`.
6. Call `PrepareGiCausticFrame`.
7. Populate current-frame advanced-GI data used by passes and diagnostics.
8. Prepare receiver-facing GI consumers and execute the render graph.
9. Finalize B1 receiver-feedback capture only after every registered receiver producer has completed.
10. Apply completed GPU counters and timings.
11. Capture immutable pending Simple-DDGI submitted-frame evidence.
12. Assemble final renderer diagnostics.

The extraction must preserve this order. In particular, C4 is not allowed to observe an emissive-source revision before the core DDGI/emissive preparation has published that revision, and B1 cannot finalize before late receiver producers.

### 2.5 Current fence-complete order

At the reused frame-slot boundary in `BeginFrame`, the renderer currently:

1. Waits the exact frame-slot graphics fence.
2. Consumes the submitted-frame ring entry before any model can train from it twice.
3. Notifies the volume manager of fence progress and consumes warm-start readback.
4. Completes B1 and C3 readback for that slot.
5. Reads diagnostics and other subsystem buffers.
6. Reads C4 and C5 results for that slot.
7. Advances the C5 generation transaction only after the old generation's readback has been consumed.
8. Consumes scheduler, residency, and transport-audit feedback.
9. Completes the submitted DDGI evidence using the matching timestamp and feedback snapshots.
10. Reuses the staging slot and continues frame admission.

After a successful terminal graphics submit, the renderer records the submitted graphics fence value, records the active C5 generation reference, commits pending DDGI evidence into the submitted-frame ring, and clears the pending evidence. A failed terminal submit clears pending evidence and must not create either reference.

These are correctness boundaries, not cleanup details.

### 2.6 Existing test coupling

The test suite has strong algorithm and contract coverage, but some tests are coupled to the current owner rather than to behavior:

- `SimpleDdgiSourceRelightTests`, `SimpleDdgiVolumeManagerTests`, `SimpleDdgiWarmupStateTests`, and `SimpleDdgiTransportTailTests` call renderer-local static DDGI helpers.
- `DdgiEmissiveTransportCacheTests`, `SimpleDdgiGpuSchedulerValidationTests`, `SimpleDdgiProbePagingShaderContractTests`, `SimpleDdgiReceiverFeedbackVulkanRuntimeTests`, `SimpleDdgiTransientFrameEvidenceTests`, `SimpleDdgiShaderMirrorTests`, and parts of `SimpleDdgiVolumeManagerTests` read `VulkanRenderer.cs` and assert source substrings.
- `FarFieldClipmapOracleTests`, `HybridReflectionContractsTests`, and debug-tooling tests also inspect renderer source around DDGI integration.

Direct helper tests should move to the helper's real owner. Source-substring tests that are intended to prove execution order or integration must become executable coordinator/adapter tests; they must not simply be redirected to read a different source file.

## 3. Why these are separate coordinators

The split follows state-transition boundaries, not method length or naming similarity.

| Coordinator | Owns | Consumes | Publishes | Explicitly does not own |
| --- | --- | --- | --- | --- |
| `AdvancedGiAdmissionCoordinator` | Manifests, runtime-content authorization, candidate profile/evidence, stable admission reasons, aggregate startup mode selection. | Settings fingerprint, device/build identity, feature evidence, content binding. | Startup graph-mode decision and immutable per-feature admission snapshot. | Vulkan resources, frame commands, render graph, C4/C5 generations, directional-shadow qualification. |
| `DdgiSceneInvalidationCoordinator` | Mutation journal, oracle comparison state, tracked scene objects/VFX/poses/lights, dirty signatures, warm-start identity cache. | Scene/light snapshot, content revisions, emissive revision/signature, foliage invalidation facts, camera-cut facts. | Immutable dirty regions, reason flags, source-refresh/relight decision, warm-start identity. | GPU upload, volume scheduling, foliage-proxy generation, reflection recapture effects. |
| `DdgiEmissiveTransportCoordinator` | Emissive CPU scratch/cache/builder/hierarchy/VFX reducer, source/surface buffers, revisions, upload validity, energy/refinement diagnostics. | Scene, ray-update availability, staging/command context, settings/content facts. | Immutable buffer/source/revision/hierarchy/refinement/diagnostic snapshot. | Scene invalidation history, C4 publication state, final renderer diagnostics. |
| `SimpleDdgiReceiverFeedbackCoordinator` | B1 desired/applied plan and key, runtime capture lifecycle, scheduling binding, rejection reason, viewport/generation handshake. | Admission snapshot, live volume/viewport capacities, producer workload, shared-arena outcome, command buffer. | Scheduling binding, graph capture contract, B1 frame snapshot and fence-complete result. | C3 work, volume scheduler algorithm, shared arena allocation policy, liveness/cost training. |
| `SimpleDdgiFrameEvidenceCoordinator` | Cost model, submitted-frame ring, pending/completed evidence, liveness watchdog, generation-rejection baselines. | Immutable frame workload, successful-submit notification, exact fence-complete counters/timestamps/feedback, volume evidence snapshot. | Cost estimate, completed evidence, liveness result, diagnostics snapshot. | GPU readback itself, queue submission, volume scheduling, public diagnostics assembly. |
| `GiCausticFrameCoordinator` | C4 mode/plan, runtime and producer state, current cache revision/fingerprint, configuration/publication/rejection state. | Admission, core DDGI/emissive/ray-scene/light facts, render extent, frame identity. | C4 graph binding, frame availability, publication and diagnostics snapshot. | General emissive table construction, AS building, forward-pass recording, C5. |
| `SimpleDdgiNearFieldResidualCoordinator` | C5 mode/plan/profile/scale/GPU configuration, runtime, generation transaction, resize replacement, active publication state. | Admission, target/Hi-Z/foliage/resource factory facts, completed timestamps, submitted fence value. | Active C5 graph binding, resize effect request, frame/fence diagnostics snapshot. | Swapchain creation, render-target mutation, mesh/forward pipeline mutation, render-graph mutation. |
| `SimpleDdgiFrameCoordinator` | Core DDGI orchestration order and small transition state not owned by a leaf. | The preceding coordinators, existing volume/far-field/light-tree/guiding managers, frame request, shared arena. | Coherent core frame result for render-graph input and projection. | C4/C5 internals, pass recording, async queue selection, final diagnostics, global renderer lifetime. |

The existing `SimpleDdgiGuidingFrameCoordinator` remains the C3 owner. A small stateless `SimpleDdgiGuidingConfigurationPlanner` may be introduced to move the renderer-local compilation helpers without making the existing coordinator responsible for global admission or memory budgets.

## 4. Scope

### In scope

- Moving the state clusters and methods identified in this plan to the eight target coordinators.
- Adding immutable, domain-specific request/result/snapshot contracts.
- Adding a stateless `DdgiFrameDataProjector` for pre-render `SceneRenderingData` mapping.
- Adding a stateless `SimpleDdgiGuidingConfigurationPlanner` if required to keep C3 policy separate from C3 Vulkan execution.
- Preserving renderer public configuration methods as facade methods that delegate to `AdvancedGiAdmissionCoordinator`.
- Replacing direct runtime/field access with narrow graph-resource and diagnostics snapshots where an extracted owner must remain encapsulated.
- Updating staged disposal nodes so resource-owning coordinators are disposed at the same dependency points as the resources they replace.
- Replacing source-coupled DDGI tests with behavior or contract tests.
- Updating renderer call sites, tests, and internal declarations through semantic refactoring.

### Out of scope

- Splitting or redesigning the 20,951-line `SimpleDdgiVolumeManager`.
- Changing DDGI quality, ray counts, scheduling policy, convergence behavior, cache formats, shader code, GPU ABI, or pass algorithms.
- Implementing any remaining items from the GI feature or performance roadmap.
- Optimizing the receiver cache or C5 while moving it.
- Replacing the render graph, pass classes, queue scheduling, descriptor model, buffer manager, or staging ring.
- Changing the number of frames in flight or generalizing renderer thread safety.
- Combining C4 and C5 into one advanced-GI runtime.
- Moving debug overlay construction; that belongs to the separate `DebugOverlayBuilder` plan.
- Building final `RendererDiagnostics`, `DdgiRuntimeSnapshot`, `DdgiContentRuntimeSnapshot`, or persistent warning history; that belongs to the separate `RendererDiagnosticsAssembler` plan.
- Owning renderer lifecycle, initialization guards, fault latching, swapchain recreation intent, or the overall disposal transaction; that belongs to the separate `RendererLifetimeCoordinator` plan.
- Choosing async-compute queues or submission topology; that belongs to the separate `AsyncComputeCoordinator` plan.
- Planning shadows or changing the pre-DDGI shadow order; that belongs to the separate `ShadowFramePlanner` plan.
- Introducing compatibility wrappers on `VulkanRenderer` solely to keep old internal tests compiling after ownership has moved.

## 5. Non-negotiable invariants

1. `VulkanRenderer` remains the public API facade. Public signatures, argument validation order, initialization guards, return values, and exception/reason text remain unchanged.
2. Simple DDGI is considered active only under the same settings, resource, ray-query, and acceleration-structure conditions as today.
3. The disabled path still detaches/reset invalidation state, disables the volume manager, publishes disabled B1/C3 state, resets evidence/liveness, and uploads a coherent empty/fallback emissive table.
4. Foliage proxies are prepared before acceleration structures. Core DDGI observes the resulting proxy frame; it does not prepare proxies late.
5. Far-field upload occurs before the volume-manager upload, and its same-frame coverage state is the one passed to the core scheduler.
6. Frame N scheduling may consume only the immutable B1 summary published by an earlier fence-complete frame. Current-frame receiver gather cannot affect current-frame scheduling.
7. If the current command buffer has recorded a read barrier against an existing B1 summary bank, post-upload reconciliation cannot replace that bank. The requested change remains pending until a safe transition.
8. B1 and C3 continue to share one `AdvancedGiTransientBufferArena`. Their desired layouts are compiled before one atomic arena reconciliation; neither feature may independently replace the backing buffer after the other has borrowed a slice.
9. Arena replacement still waits for every prior descriptor reader through the existing exact wait callback. No new normal-frame `DeviceWaitIdle` is introduced.
10. C3 retains exact source-cache/runtime/arena generation agreement and uses the existing `SimpleDdgiGuidingFrameCoordinator` for configure, prepare, record, fence-complete publication, and disposal.
11. Mutation-journal mode and full-scan oracle mode produce equivalent dirty coverage under the current comparison rules. Journal attach/detach and oracle priming occur at the same scene/disable boundaries.
12. Dirty-region order, merge rules, reason flags, padding, skinned-pose handling, sustained VFX bounds, and foliage influence behavior remain unchanged.
13. Light, environment, atmosphere, emissive, source-policy, stable-light, dynamic-geometry, and warm-start identity hashes remain bit-for-bit identical for the same inputs.
14. Sole-directional-light relight eligibility and radiance-scale validation remain identical. No new partial relight case is admitted.
15. Emissive source limits, deterministic importance ordering, cache keys, cache hit behavior, hierarchy layout, VFX reduction, cooked/runtime exclusions, upload suppression, revision increments, and buffer bytes remain identical.
16. When ray updates are disabled, emissive diagnostics still describe the current content while GPU bindings remain coherent with the existing disabled/fallback contract.
17. The visible receiver focus keeps the current camera fallback, spacing, camera-cut/content-revision resets, and generation-validated B1 witness behavior.
18. `SimpleDdgiVolumeManager.Upload` receives the same values in the same logical frame: dirty signature, flags, coverage, authored volumes, revision, source refresh/relight, warm-start identity, refinement demands, visible focus, and camera direction.
19. Page-management preparation occurs after the volume upload and before frame data is projected for graph consumption.
20. Reflection-probe recapture remains a renderer-owned cross-feature effect applied from an immutable DDGI result at the same boundary.
21. B1 capture begins and finalizes once per admitted frame, records all required producers, and aborts with the same fail-closed semantics. Late reduction remains after all receiver producers.
22. Submitted DDGI evidence is captured before terminal submission, committed to the ring only after successful terminal graphics submit, cleared after either success or failure, and consumed at most once after the matching slot fence.
23. Scheduler cost/source-cache training uses the same delayed completed counters and never observes current-frame or mismatched-generation data.
24. Liveness evidence keeps CPU/GPU scheduler authority rules, sparse-residency alignment, generation barriers, rejection deltas, watchdog history, and reset boundaries unchanged.
25. C4 uses the same admission, hero-source revision identity, producer fingerprint, runtime configuration, invalidation, readable-revision check, graph-frame publication, and rejection behavior.
26. C4 extent incompatibility is handled only at the existing device-idle/recreation-safe boundary.
27. C5 consumes fence-complete readback before a prepared generation becomes active or an old generation is reclaimed.
28. C5 active-generation references are recorded only after a successful terminal graphics submit with the exact submitted fence value.
29. C5 replacement allocates/validates before publication. A failed replacement preserves the previous active generation and reason semantics.
30. Render-target, mesh-pipeline, forward-pipeline, and render-graph mutations remain explicit renderer effects. C5 returns a publication/effect description and never back-calls `VulkanRenderer`.
31. `SceneRenderingData` fields used by render passes are populated at the same pre-render boundary. Completed counter fields remain associated with their matching fence-complete frame.
32. Final runtime/content snapshots and persistent warnings are assembled by `RendererDiagnosticsAssembler`; coordinators publish facts and do not construct the public diagnostics object.
33. Async-compute pass inclusion and queue selection do not move into a DDGI coordinator.
34. Resource creation, graph registration, descriptor registration, target recreation, and staged disposal retain their current ordering unless an individual phase explicitly proves an equivalent narrower order.
35. No coordinator retains `Scene`, `ICamera`, `SceneRenderingData`, `RenderSettings`, or `CommandBuffer` beyond the synchronous operation that receives it.
36. No coordinator accepts `VulkanRenderer`, delegates that expose arbitrary renderer state, `IServiceProvider`, `dynamic`, a dictionary of values, or an all-feature `DdgiContext`.
37. No old and new owner may be authoritative for the same mutable field during a landed phase.

## 6. Target design

### 6.1 `AdvancedGiAdmissionCoordinator`

This coordinator owns policy state that is configured before initialization and observed by multiple DDGI extensions.

Move ownership of:

- `_advancedGiPrerequisiteManifest`;
- `_advancedGiQualificationManifest`;
- `_advancedGiRuntimeContentBinding` and `_advancedGiRuntimeContentState`;
- `_advancedGiSettingsFingerprint`;
- `_advancedGiCandidateProfile` and `_advancedGiCandidateProfileStatus`;
- raw C4 and C5 qualification evidence and admission contexts;
- candidate authorization and runtime evidence state used by B1/C3/C4/C5;
- aggregate `AdvancedGiRenderGraphModes` selection.

Keep `_directionalShadowQualificationManifest` outside this coordinator. It is consumed by the shadow subsystem even though it participates in a combined startup check.

Suggested surface:

```csharp
internal sealed class AdvancedGiAdmissionCoordinator
{
    public AdvancedGiRuntimeContentState RuntimeContentState { get; }
    public string CandidateProfileStatus { get; }

    public void ConfigurePrerequisiteManifest(
        AdvancedGiPrerequisiteManifest manifest);

    public void ConfigureRuntimeContentBinding(
        in AdvancedGiRuntimeContentBinding binding);

    public void ConfigureQualificationManifest(
        AdvancedGiQualificationManifest manifest);

    public void ConfigureGiCausticEvidence(
        in GiCausticQualificationEvidence evidence,
        in GiCausticAdmissionContext context);

    public void ConfigureNearFieldResidualEvidence(
        in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
        in SimpleDdgiNearFieldResidualAdmissionContext context);

    public AdvancedGiStartupDecision ResolveStartup(
        in AdvancedGiStartupRequest request);

    public AdvancedGiRuntimeContentTransition ObserveSceneContent(
        in AdvancedGiSceneContentFacts facts);

    public AdvancedGiAdmissionSnapshot CaptureSnapshot();
}
```

`VulkanRenderer` retains the public methods and performs the existing renderer-lifetime guard at the same point, then delegates. File parsing/codec helpers may remain in their existing codec types or be called by the facade; the coordinator should receive validated documents rather than gain filesystem responsibility.

The startup decision should include per-feature mode state, qualification/prerequisite details, candidate authorization, and the aggregate graph modes. It must be immutable and coherent: graph construction must not re-read individual mutable fields after the decision is captured.

Move pure helpers such as `ResolveAdvancedGiMode`, candidate authorization, runtime-content update policy, and advanced-GI cross-feature preflight here. If combined directional-shadow evidence is required, pass an immutable `DirectionalShadowAdmissionFacts` value into `ResolveStartup`; do not give the coordinator ownership of the shadow manifest.

### 6.2 `DdgiSceneInvalidationCoordinator`

This coordinator owns CPU-side change detection and identity. It has no Vulkan dependency.

Move ownership of:

- dirty-bound and dirty-region scratch collections;
- `_ddgiMutationJournal`;
- tracked render-object, skinned-pose, VFX, and light histories plus removal scratch;
- journal-oracle priming and comparison state;
- dynamic tracking frame and dynamic signature state;
- Simple-DDGI dirty signature history;
- sole-directional-light history;
- warm-start scene identity cache;
- foliage dirty signature/influence history after proxy preparation.

Suggested request/result:

```csharp
internal readonly record struct DdgiInvalidationRequest(
    Scene Scene,
    LightFrameSnapshot Lights,
    DdgiFrameIdentity Identity,
    DdgiEmissiveTransportSnapshot Emissive,
    DdgiFoliageInvalidationSnapshot Foliage,
    bool MutationJournalEnabled,
    EnvironmentSettings Environment,
    DdgiInvalidationPolicy Policy);

internal readonly record struct DdgiInvalidationFrame(
    IReadOnlyList<DdgiDirtyRegion> DirtyRegions,
    SimpleDdgiDirtySignature DirtySignature,
    SimpleDdgiWarmStartSceneIdentity? WarmStartIdentity,
    DdgiMutationJournalTelemetry JournalTelemetry,
    DdgiInvalidationDiagnostics Diagnostics);
```

The returned dirty-region collection must be stable until the synchronous volume upload completes. Prefer a coordinator-owned reusable array plus count or an immutable copied segment; do not return a mutable list that may be changed by a later helper before upload.

Move these method families here:

- journal drain and mutation resolution;
- full-scan dirty collection;
- dirty coverage comparison/accumulation;
- render-object, sustained-VFX, skinned-pose, light, and foliage dirty-region handling;
- dynamic tracking reset;
- dirty, stable-light, environment, atmosphere, dynamic-geometry, and warm-start signatures;
- sole-directional relight validation and scale calculation;
- scene probe-bounds estimation used only by invalidation/identity.

`DdgiFoliageProxyManager.PrepareFrame` remains the leaf manager operation called before AS preparation. The renderer passes its immutable frame/influence result into invalidation; invalidation does not record the proxy generation pass.

### 6.3 `DdgiEmissiveTransportCoordinator`

This coordinator owns the complete renderer-level emissive source-table transaction, including its two buffers.

Move ownership of:

- emissive source/surface constants, strides, and CPU scratch arrays;
- source importance and combined-importance scratch;
- `DdgiEmissiveTableCache`;
- `DdgiEmissiveSourceSetBuilder`;
- `DdgiEmissiveSpatialHierarchy`;
- `DdgiVfxMacroEmitterReducer` and VFX scratch;
- source and surface `BufferHandle`s and allocated sizes;
- content-valid state, upload count, source count, hierarchy count, and revision;
- source/base-payload/VFX signatures;
- triangle-table, skipped/excluded, scan-count, energy, warning, and refinement-demand state.

Suggested surface:

```csharp
internal sealed class DdgiEmissiveTransportCoordinator : IDisposable
{
    public DdgiEmissiveTransportSnapshot Snapshot { get; }

    public DdgiEmissiveTransportSnapshot PrepareFrame(
        in DdgiEmissiveFrameRequest request,
        StagingRing stagingRing,
        CommandBuffer commandBuffer);

    public void ResetSceneTracking();
}
```

The snapshot should include, at minimum:

- source/surface buffer handles and byte ranges;
- buffer-content validity;
- source count and hierarchy node count;
- source revision, source signature, and base-payload signature;
- upload count and cache diagnostics;
- triangle-table/VFX/exclusion/energy diagnostics;
- a stable refinement-demand view plus its diagnostics/signature.

The coordinator may synchronously borrow `Scene`, staging, and the command buffer. It must not retain any of them. It may retain bounded scratch arrays and owns disposal of its two buffers at the same staged-disposal point as today.

`SceneRenderingData` writes move to `DdgiFrameDataProjector`; C4 consumes the typed emissive snapshot rather than reaching into coordinator fields.

### 6.4 `SimpleDdgiReceiverFeedbackCoordinator`

This coordinator is the renderer-level B1 owner above `SimpleDdgiReceiverFeedbackVulkanRuntime`.

Move ownership of:

- `_simpleDdgiReceiverFeedbackPlan`;
- `_simpleDdgiReceiverFeedbackConfigurationKey`;
- `_simpleDdgiReceiverFeedbackGraphicsPipelinesRequested`;
- the renderer-level compile/reject/configure sequence;
- the published scheduling binding and generation handshake;
- capture begin/finalization/abort coordination;
- B1-specific reason and snapshot state.

The low-level runtime continues to own its Vulkan allocation, capture buffers, exact producer contract, reduction, readback, and descriptor binding.

Suggested surface:

```csharp
internal sealed class SimpleDdgiReceiverFeedbackCoordinator : IDisposable
{
    public SimpleDdgiReceiverFeedbackDesiredState CompileDesiredState(
        in SimpleDdgiReceiverFeedbackRequest request);

    public bool TryApplyConfiguration(
        in SimpleDdgiReceiverFeedbackDesiredState desired,
        in AdvancedGiArenaReconciliationResult arena,
        out string reason);

    public SimpleDdgiReceiverFeedbackGpuSchedulingBinding
        AcquireSchedulingBinding(in SimpleDdgiSchedulingRequest request);

    public SimpleDdgiReceiverCaptureBinding BeginCapture(
        in SimpleDdgiReceiverCaptureRequest request);

    public void FinalizeAfterAllReceiverProducers(CommandBuffer commandBuffer);

    public void CompleteFrameAfterFence(
        int frameIndex,
        ulong expectedFrameSerial);

    public void AbortCapture(string reason);

    public SimpleDdgiReceiverFeedbackSnapshot CaptureSnapshot();
}
```

Shared B1/C3 arena policy is not owned by B1. `SimpleDdgiFrameCoordinator` compiles both desired states, produces one `GiExperimentScratchArenaPlan`, reconciles `AdvancedGiTransientBufferArena` once, then lets B1 and C3 apply configurations against that generation.

The coordinator must expose a narrow graph/capture contract instead of its concrete runtime. Passes should receive the exact buffers/ranges/generations and producer obligations they need. The renderer retains the explicit call after all receiver producers to `FinalizeAfterAllReceiverProducers`.

### 6.5 `SimpleDdgiFrameEvidenceCoordinator`

This coordinator owns delayed workload evidence and liveness because their lifetime is the submitted frame/fence boundary, not B1 capture or volume-resource lifetime.

Move ownership of:

- `SimpleDdgiSchedulerCostModel`;
- `SimpleDdgiSubmittedFrameRing`;
- pending submitted evidence;
- completed frame evidence;
- `SimpleDdgiLivenessWatchdog`;
- scheduler/residency generation-rejection baselines;
- completed-workload training logic;
- liveness telemetry construction and watchdog evaluation.

Suggested surface:

```csharp
internal sealed class SimpleDdgiFrameEvidenceCoordinator
{
    public SimpleDdgiSchedulerCostEstimate CostEstimate { get; }

    public void CapturePending(
        int frameIndex,
        in SimpleDdgiSubmittedWorkload workload);

    public void CommitSuccessfulSubmission(int frameIndex);

    public void AbortPendingSubmission();

    public SimpleDdgiCompletedFrameEvidence CompleteAfterFence(
        int frameIndex,
        in SimpleDdgiFenceCompletedEvidence completed);

    public SimpleDdgiLivenessSnapshot EvaluateLiveness(
        in SimpleDdgiLivenessRequest request);

    public void ResetDisabled();

    public SimpleDdgiFrameEvidenceSnapshot CaptureSnapshot();
}
```

`CompleteAfterFence` must consume the ring slot before invoking any training logic. If training throws, retrying `BeginFrame` must not train the same frame twice. The completed input groups timestamps, scheduler feedback, residency feedback, investigation/material counters, and generation facts captured at the exact fence-complete boundary.

The renderer or `RendererLifetimeCoordinator` retains queue-submit success/failure detection and calls either commit or abort. The evidence coordinator never submits work or reads a Vulkan fence.

### 6.6 `GiCausticFrameCoordinator`

This coordinator owns the C4 state machine above `GiCausticVulkanRuntime` and `GiCausticTaggedTransportGpuProducer`.

Move ownership of:

- C4 plan and mode;
- forward receiver-pipeline configuration;
- C4 runtime and tagged producer;
- current cache revision and producer revision fingerprint;
- runtime-configured and frame-available flags;
- candidate-authorization use state;
- C4 frame preparation, producer creation, rejection, extent invalidation, and fence-complete readback calls.

Suggested surface:

```csharp
internal sealed class GiCausticFrameCoordinator : IDisposable
{
    public void Initialize(in GiCausticInitializationRequest request);

    public GiCausticFrameResult PrepareFrame(
        in GiCausticFrameRequest request);

    public void CompleteFrameAfterFence(
        int frameIndex,
        Fence frameFence);

    public void DisableForIncompatibleExtentAfterDeviceIdle(string reason);

    public GiCausticGraphResourceSnapshot CaptureGraphResources();

    public GiCausticCoordinatorSnapshot CaptureSnapshot();
}
```

The frame request contains immutable admission, emissive, light, ray-scene, extent, and frame-identity facts. It does not contain `SceneRenderingData` or a renderer reference.

The result contains the graph-ready publication, frame availability, forward consumer configuration, and reason. Pass/graph owners consume the result; they do not reach into the C4 runtime.

### 6.7 `SimpleDdgiNearFieldResidualCoordinator`

This coordinator owns the full C5 generation and safe-replacement state machine.

Move ownership of:

- C5 plan, mode, authored/effective profile, execution scale, GPU configuration, and forward direct-source configuration;
- `SimpleDdgiNearFieldResidualVulkanRuntime` active reference;
- `SimpleDdgiNearFieldResidualGenerationTransaction<...>`;
- candidate-authorization use state;
- startup runtime factory/initial generation publication;
- generation compilation and replacement envelope;
- pre-target-recreation preparation and post-target-recreation completion;
- frame-boundary generation commit and retired-generation polling;
- successful-submit active reference;
- disable/release state.

Suggested surface:

```csharp
internal sealed class SimpleDdgiNearFieldResidualCoordinator : IDisposable
{
    public void Initialize(in NearFieldResidualInitializationRequest request);

    public NearFieldResidualRecreationPreparation
        PrepareTargetRecreationAfterDeviceIdle(
            in NearFieldResidualExtentRequest request);

    public NearFieldResidualPublication
        CompleteTargetRecreation(
            in NearFieldResidualTargetSet targets);

    public void CompleteFrameAfterFence(
        int frameIndex,
        in FrameTimingSnapshot timestamps);

    public NearFieldResidualGenerationTransition
        AdvanceGenerationAtFrameBoundary();

    public void ObserveSuccessfulSubmission(ulong graphicsFenceValue);

    public void DisableAfterDeviceIdle(string reason);

    public NearFieldResidualGraphResourceSnapshot CaptureGraphResources();

    public NearFieldResidualCoordinatorSnapshot CaptureSnapshot();
}
```

The coordinator may own the runtime factory dependencies required to allocate and validate a generation. It does not directly mutate `RenderTargetManager`, `MeshPipeline`, `ForwardPlusPass`, or `RenderGraph`. Instead it returns a `NearFieldResidualPublication` with explicit old/new bindings and graph-mode facts. A small renderer effect adapter applies that publication at the current device-idle/target-recreation boundaries.

This separation is mandatory: allowing C5 to call arbitrary renderer effects would create a hidden reverse dependency and make failed replacement rollback unauditable.

### 6.8 `SimpleDdgiFrameCoordinator`

This is the thin core integration owner created only after the preceding responsibilities have moved.

It composes:

- `DdgiSceneInvalidationCoordinator`;
- `DdgiEmissiveTransportCoordinator`;
- `SimpleDdgiReceiverFeedbackCoordinator`;
- `SimpleDdgiFrameEvidenceCoordinator`;
- existing `SimpleDdgiGuidingFrameCoordinator`;
- existing `SimpleDdgiVolumeManager`;
- existing `FarFieldClipmapManager`;
- existing `SimpleDdgiLightTreeGpuResources` where required;
- existing `AdvancedGiTransientBufferArena`.

It does not own or call C4/C5 internals. Those coordinators consume the coherent core result after core preparation.

Suggested surface:

```csharp
internal sealed class SimpleDdgiFrameCoordinator
{
    public SimpleDdgiCoreFrameResult PrepareFrame(
        in SimpleDdgiCoreFrameRequest request);

    public SimpleDdgiFenceCompletionResult CompleteAfterFence(
        in SimpleDdgiCoreFenceCompletion completion);

    public void AbortFrame(string reason);

    public SimpleDdgiCoreCoordinatorSnapshot CaptureSnapshot();
}
```

The final `PrepareFrame` sequence is:

1. Resolve core active and ray-update-active facts from the captured request.
2. Execute the complete disabled transition when inactive.
3. Obtain dirty regions/identity from invalidation.
4. Prepare emissive transport.
5. Upload far-field data and capture its frame snapshot.
6. Compile B1 and C3 desired states.
7. Reconcile their shared transient arena once.
8. Acquire and record the prior B1 scheduling-summary read dependency.
9. Resolve visible receiver focus from the validated prior witness or camera fallback.
10. Apply scheduler cost estimate and call `SimpleDdgiVolumeManager.Upload` once.
11. Recompile live-domain-dependent B1/C3 desired state after initial physical-domain publication.
12. Reconcile only if the command buffer has not already borrowed an incompatible B1 bank.
13. Configure/prepare the existing C3 frame coordinator.
14. Prepare page management.
15. Capture one coherent core result and reflection-recapture intent.

The coordinator may retain long-lived references to these explicitly named collaborators, but it does not own their disposal unless a later phase deliberately transfers a staged-disposal node. It never retains per-frame requests.

### 6.9 `DdgiFrameDataProjector`

This stateless support type is needed so coordinators do not mutate the renderer's giant transport object and the renderer does not retain thousands of mapping assignments.

Move the pre-render portions of:

- `PopulateSimpleDdgiFrameData`;
- `PopulateAdvancedGiFrameDiagnostics`;
- core warmup/frame-work mapping helpers;
- frame-local far-field, B1, C3, C4, and C5 projection;
- completed-DDGI counter application that is purely a schema mapping.

Suggested surface:

```csharp
internal static class DdgiFrameDataProjector
{
    public static void Apply(
        SceneRenderingData target,
        in DdgiFrameProjection projection);
}
```

`DdgiFrameProjection` groups immutable snapshots by feature; it is not another mutable owner. It may contain nested core, B1, C3, C4, C5, emissive, and completed-counter records. It must not contain live managers or runtimes.

Final `DdgiRuntimeSnapshot`, `DdgiContentRuntimeSnapshot`, `GiCausticDiagnostics`, public `RendererDiagnostics`, budget evaluation, and persistent warning updates remain with `RendererDiagnosticsAssembler`. If that extraction lands first, the projector consumes its agreed GI input contracts instead of recreating them.

### 6.10 Renderer-retained responsibilities

After all phases, `VulkanRenderer` still owns:

- the public renderer and advanced-GI configuration facade;
- overall initialization and successful construction order;
- global frame admission, command-buffer creation, render-graph execution, terminal submission, presentation, recreation, and device-loss handling;
- camera/light/shadow/AS/render-target fact capture;
- foliage-proxy preparation before AS and application of its graph work;
- global ordering between core DDGI, C4, C5, reflections, shadows, receivers, debug overlays, and the render graph;
- renderer effects returned by C5 target-generation transitions;
- reflection-probe recapture effects returned by core DDGI;
- descriptor/render-graph registration using coordinator resource snapshots;
- calls at exact fence-complete and successful-submit boundaries;
- final diagnostics invocation and publication;
- construction of the staged disposal graph.

The desired renderer shape is explicit orchestration, for example:

```csharp
DdgiFoliageProxyFrame foliage = PrepareDdgiFoliageProxiesBeforeAs(...);
PrepareAccelerationStructures(...);

SimpleDdgiCoreFrameResult core = _simpleDdgiFrames.PrepareFrame(
    CaptureSimpleDdgiFrameRequest(foliage, ...));

GiCausticFrameResult caustic = _giCaustics.PrepareFrame(
    CaptureGiCausticRequest(core, ...));

NearFieldResidualGraphResourceSnapshot nearField =
    _nearFieldResidual.CaptureGraphResources();

DdgiFrameDataProjector.Apply(
    sceneData,
    CaptureDdgiProjection(core, caustic, nearField, ...));
```

The exact method names may change during implementation, but the ownership and direction of dependencies must not.

## 7. Integration with the other extraction plans

### 7.1 `RendererLifetimeCoordinator`

The lifetime coordinator owns initialization/disposal/frame/fault guards. Public advanced-GI configuration remains:

```text
VulkanRenderer public method
  -> RendererLifetimeCoordinator initialization guard
  -> argument/file validation in the existing order
  -> AdvancedGiAdmissionCoordinator mutation
```

DDGI coordinators receive explicit `CompleteAfterFence`, `ObserveSuccessfulSubmission`, `AbortFrame`, target-recreation, and disposal calls. They do not infer renderer lifetime from nullable handles.

### 7.2 `RendererDiagnosticsAssembler`

The assembler owns final diagnostics construction, runtime/content snapshot factories, budget evaluation, GI feature/warning derivation, and persistent `DdgiDiagnosticWarningTracker` state. DDGI coordinators expose immutable source snapshots only.

Do not move `CreateDdgiRuntimeSnapshot` or `CreateDdgiContentRuntimeSnapshot` into a DDGI coordinator if the assembler plan has landed. If this plan lands first, leave those methods in the renderer as temporary adapters and move them directly to the assembler later; do not make them permanent coordinator responsibilities.

### 7.3 `DebugOverlayBuilder`

The debug builder owns DDGI probe-marker preparation, scratch instances, completed overlay counters, and overlay-only math. It may synchronously consume a `SimpleDdgiDebugSnapshot` or the existing volume manager during its own migration, but the DDGI coordinator family does not absorb overlay state.

### 7.4 `AsyncComputeCoordinator`

The async coordinator owns pass-to-queue admission, plans, timeline values, fallback, and submission bookkeeping. DDGI coordinators expose graph resource generations and whether feature work is executable. They do not choose queues or inspect the async pass-name set.

### 7.5 `ShadowFramePlanner`

Shadow planning remains before DDGI preparation. Core DDGI receives the finalized light/shadow/AS capability facts it already uses; it does not recompute a shadow plan.

### 7.6 `PerformanceCaptureMetadataProvider`

Capture metadata may include advanced-GI profile, qualification, mode, or runtime-content identifiers. The metadata provider consumes one immutable admission/coordinator snapshot; it does not read coordinator internals or become an admission owner.

## 8. Exact relocation map

The following table is the initial semantic move map. Use Rider Find Usages before moving each symbol because the dirty working tree may contain additional references.

| Current renderer symbol or region | Final owner |
| --- | --- |
| `ConfigureAdvancedGiPrerequisiteManifest`, qualification/runtime-content/candidate/evidence configuration internals | `AdvancedGiAdmissionCoordinator`; public facade remains in renderer. |
| `TryAuthorizeAdvancedGiCandidate`, `UpdateAdvancedGiRuntimeContentState` | `AdvancedGiAdmissionCoordinator`. |
| `ResolveInitialAdvancedGiGraphModes`, combined advanced-GI preflight, generic `ResolveAdvancedGiMode` | `AdvancedGiAdmissionCoordinator`. |
| C4/C5 raw evidence fields | `AdvancedGiAdmissionCoordinator`; derived executable plan/runtime state belongs to the feature coordinator. |
| `_ddgiMutationJournal`, dirty scratch, tracked object/pose/VFX/light fields | `DdgiSceneInvalidationCoordinator`. |
| `CollectDdgiDirtyRegionsFromJournal`, `ResolveDdgiMutation`, dirty coverage helpers | `DdgiSceneInvalidationCoordinator`. |
| `CollectDdgiDirtyRegions`, object/VFX/light/skinned/foliage merge helpers | `DdgiSceneInvalidationCoordinator`. |
| DDGI light/environment/dynamic/emissive dirty signatures, warm-start identity, source-relight helpers | `DdgiSceneInvalidationCoordinator`. |
| Emissive scratch/cache/builder/hierarchy/reducer/buffers and associated fields | `DdgiEmissiveTransportCoordinator`. |
| `UploadDdgiEmissiveSources`, source enumeration/material/geometry resolution, insertion/sort | `DdgiEmissiveTransportCoordinator`. |
| Emissive/refinement/energy diagnostics and demand construction | `DdgiEmissiveTransportCoordinator` snapshot; final schema mapping stays outside. |
| `_simpleDdgiReceiverFeedbackPlan`, key, pipeline-request flag | `SimpleDdgiReceiverFeedbackCoordinator`. |
| `ReconcileSimpleDdgiReceiverFeedback`, B1 plan/workload compilation and rejection | Split: B1 compile/apply in receiver coordinator; shared B1/C3 arena reconcile in core frame coordinator. |
| `FinalizeSimpleDdgiReceiverFeedbackCapture` and frame-complete B1 calls | `SimpleDdgiReceiverFeedbackCoordinator`, invoked at the same renderer boundaries. |
| Guiding desired/applied configurations and compile helpers | Existing guiding coordinator plus optional stateless `SimpleDdgiGuidingConfigurationPlanner`; shared arena ordering in core frame coordinator. |
| `_simpleDdgiSchedulerCostModel`, submitted ring, pending/completed evidence | `SimpleDdgiFrameEvidenceCoordinator`. |
| `ObserveCompletedSimpleDdgiWorkload`, `CapturePendingSimpleDdgiSubmittedFrame`, `CompleteSimpleDdgiSubmittedFrame` | `SimpleDdgiFrameEvidenceCoordinator`. |
| `UpdateSimpleDdgiLivenessTelemetry`, rejection baseline state/helpers | `SimpleDdgiFrameEvidenceCoordinator`; field projection via projector. |
| `PrepareGiCausticFrame`, producer creation/rejection, C4 runtime/config/revision fields | `GiCausticFrameCoordinator`. |
| C4 extent incompatibility and fence-complete readback | `GiCausticFrameCoordinator`, called only at existing safe boundaries. |
| C5 mode/plan/profile/runtime/generation fields | `SimpleDdgiNearFieldResidualCoordinator`. |
| C5 initial factory, generation compilation, target-recreation prepare/complete, frame-boundary commit, reference, disable | `SimpleDdgiNearFieldResidualCoordinator`; renderer applies returned graph/target effects. |
| `PrepareDdgiProbeVolumes` | Replaced by `SimpleDdgiFrameCoordinator.PrepareFrame` after leaf extractions. |
| Shared arena reconciliation and B1/C3 safe ordering | `SimpleDdgiFrameCoordinator`; low-level allocation remains `AdvancedGiTransientBufferArena`. |
| `PopulateSimpleDdgiFrameData` and pre-render advanced-GI mapping | `DdgiFrameDataProjector`. |
| `ResolveConfiguredSimpleDdgiPrimaryRayBudget`, `ResolveSimpleDdgiFrameWork`, light selection/ray upper-bound helpers | The narrow policy/projector owner that uses each helper; no renderer compatibility copy. |
| Reflection recapture scheduling | Renderer effect adapter consuming a request returned by core DDGI. |
| `CreateDdgiRuntimeSnapshot`, `CreateDdgiContentRuntimeSnapshot`, final C4/C5 diagnostics assembly | `RendererDiagnosticsAssembler`, not a DDGI coordinator. |
| DDGI debug marker helpers and counters | `DebugOverlayBuilder`, not a DDGI coordinator. |

## 9. Delivery order and change isolation

Each phase is a separate implementation change. A phase is complete only when old fields/methods for its responsibility have been deleted from `VulkanRenderer`, focused tests pass, and no downstream phase is required merely to restore the build.

### Phase 0: Freeze behavior and build an ownership ledger

1. Record the current focused and full-suite baseline, including any pre-existing failures.
2. Capture representative `SceneRenderingData`, diagnostics, admission reasons, graph modes, and submitted/fence-complete evidence for:
   - DDGI disabled;
   - CPU-reference scheduler;
   - GPU-mirror scheduler;
   - GPU-resident sparse scheduler;
   - B1 off/on;
   - C3 off/on;
   - C4 off/on/rejected;
   - C5 off/on/replaced/rejected.
3. Add an ownership ledger test or review artifact mapping every field listed in Section 8 to one final owner.
4. Add executable characterization tests for ordering that is currently protected only by source substrings.
5. Record allocation and CPU timing baselines for warmed DDGI preparation and disabled frames.

Exit criterion: behavior and field ownership are observable without relying on the continued presence of renderer-local method bodies.

### Phase 1: Extract `AdvancedGiAdmissionCoordinator`

1. Add immutable startup request/decision and per-frame admission snapshot contracts.
2. Move pure generic mode resolution, prerequisite/qualification/candidate checks, runtime-content transitions, and combined preflight.
3. Move manifest/binding/profile/evidence state.
4. Keep public renderer methods as thin guarded facades with identical validation/order/reason behavior.
5. Change startup graph creation and downstream feature initialization to consume one captured decision.
6. Retarget Advanced-GI tests to the coordinator and add facade compatibility tests.
7. Delete duplicate renderer fields and pure helpers.

Exit criterion: the renderer has no mutable advanced-GI admission source of truth, and all B1/C3/C4/C5 startup decisions derive from one immutable decision.

### Phase 2: Extract `DdgiSceneInvalidationCoordinator`

1. Add dirty-frame and foliage-invalidation contracts.
2. Move the mutation journal and tracked histories without changing collection types or iteration order.
3. Move journal and oracle paths mechanically, preserving comparison cadence and telemetry.
4. Move dirty-region merge, signature, warm-start, and source-relight logic.
5. Keep foliage proxy generation in its existing owner and pass its result into invalidation.
6. Retarget static helper tests and add multi-frame scene mutation sequences.
7. Delete renderer dirty/scratch/tracking/signature fields and methods.

Exit criterion: identical inputs produce bit-identical signatures/identities and equivalent ordered dirty coverage in journal and oracle modes.

### Phase 3: Extract `DdgiEmissiveTransportCoordinator`

1. Add the coordinator with existing fixed-size scratch/cache components and an immutable snapshot.
2. Move source enumeration, cooked/runtime selection, material/geometry resolution, importance ordering, VFX reduction, hierarchy build, cache, and signatures mechanically.
3. Transfer the two GPU buffers and their staged destruction to the coordinator.
4. Move upload suppression, revision, energy warning, exclusions, and refinement-demand state.
5. Replace `SceneRenderingData` writes with snapshot projection.
6. Change core DDGI and C4 to consume the snapshot through temporary renderer adapters until their own phases land.
7. Replace source-substring tests with buffer/snapshot behavior tests.

Exit criterion: source/surface bytes, revisions, upload counts, cache outcomes, refinement demands, and diagnostics match the baseline for static, animated, VFX, empty, excluded, and ray-update-disabled content.

### Phase 4: Extract `SimpleDdgiFrameEvidenceCoordinator`

1. Add pending/captured/completed input contracts that do not reference `SceneRenderingData`.
2. Move cost-model, ring, pending/completed, watchdog, and rejection-baseline state.
3. Route pre-submit capture, successful-submit commit, submit-failure abort, and exact fence-complete consumption through the coordinator.
4. Move completed-workload training and liveness construction.
5. Project liveness results through the temporary renderer adapter/projector.
6. Replace source-order assertions with fake-sequence tests that prove consume-before-train and success-only commit.
7. Delete renderer evidence/liveness fields and methods.

Exit criterion: delayed evidence, training samples, liveness barriers, and watchdog state are identical, and a thrown observer cannot double-consume a frame.

### Phase 5: Extract `SimpleDdgiReceiverFeedbackCoordinator`

1. Add B1 desired state, configuration key, scheduling request, capture binding, graph snapshot, and diagnostic snapshot contracts.
2. Transfer renderer-level B1 plan/configuration/capture state while retaining the existing Vulkan runtime as the leaf owner.
3. Move B1 plan/workload compilation and rejection.
4. Route scheduling acquisition/barrier, capture begin, all producer obligations, late finalization, abort, and fence-complete readback through the coordinator.
5. Temporarily leave shared B1/C3 arena reconciliation in a narrowly named renderer adapter; it moves only when the core coordinator has both desired states.
6. Update passes/graph setup to consume the graph/capture snapshot rather than the concrete runtime where practical.
7. Delete renderer B1 state and source-coupled assertions.

Exit criterion: B1 plan keys, arena byte requests, capture producer counts, scheduling bindings, rejection reasons, and delayed readback match the baseline.

### Phase 6: Extract `GiCausticFrameCoordinator`

1. Add C4 frame request/result and graph-resource snapshot contracts.
2. Move mode/plan/runtime/producer/revision/configuration/publication state.
3. Move frame preparation, producer creation, rejection, extent disable, and fence-complete readback.
4. Change C4 to consume admission and emissive snapshots.
5. Change graph/pass setup to consume a C4 resource snapshot instead of renderer fields.
6. Transfer the current C4 disposal stage to the coordinator without changing dependencies.
7. Delete renderer C4 state/methods and add revision/publication sequence tests.

Exit criterion: admitted, rejected, cache-hit, revision-change, extent-change, stale-readback, and disabled C4 frames match the baseline.

### Phase 7: Extract `SimpleDdgiNearFieldResidualCoordinator`

1. Add C5 initialization, extent request, target set, publication, graph-resource, and snapshot contracts.
2. Move plan/mode/profile/scale/configuration/runtime/generation state.
3. Move initial generation creation, executable checks, startup rejection, replacement compile, transaction prepare/commit/poll, and disable.
4. Add a renderer effect adapter for target/pipeline/render-graph publication.
5. Route fence-complete readback before generation advance and successful-submit fence reference after submit.
6. Transfer the C5 disposal stage without weakening generation-transaction cleanup.
7. Delete renderer C5 state/methods and add failure-atomicity tests.

Exit criterion: active/prepared/retired generations, graph bindings, extent envelopes, references, and reason strings match the baseline across success and every failure point.

### Phase 8: Extract `SimpleDdgiFrameCoordinator`

1. Add the core frame request/result and shared B1/C3 arena decision contracts.
2. Move the remaining `PrepareDdgiProbeVolumes` ordering into the coordinator without moving leaf algorithms.
3. Move shared B1/C3 desired-plan combination and one atomic arena reconciliation from the temporary adapter.
4. Move visible receiver focus resolution and core disabled transition.
5. Compose invalidation, emissive, far-field, feedback, existing guiding, evidence cost estimate, volume upload, and page management in the exact current order.
6. Return reflection recapture intent to the renderer effect adapter.
7. Delete `PrepareDdgiProbeVolumes` and remaining core DDGI orchestration fields.

Exit criterion: the renderer has one core prepare call and explicit C4/C5 calls; it contains no B1/C3 arena choreography or volume-upload argument construction.

### Phase 9: Extract projection and close dependency leaks

1. Add `DdgiFrameDataProjector` and group immutable projection inputs.
2. Move pre-render Simple-DDGI and advanced-GI field mapping mechanically.
3. Coordinate with `RendererDiagnosticsAssembler` so final snapshot factories/warnings have one owner.
4. Replace direct manager/runtime reads in render-graph setup with narrow graph-resource snapshots.
5. Retarget remaining renderer-local static helper tests to real owners.
6. Remove source-substring tests that merely enforce old file placement.
7. Run semantic Find Usages for every deleted renderer field/method and audit for compatibility shims.

Exit criterion: renderer DDGI code is facade/orchestration/effect application only, and no extracted owner leaks its concrete runtime to unrelated consumers.

### Phase 10: Full verification and cleanup

1. Run focused, full-suite, and solution builds.
2. Run representative runtime and resize/fault scenarios.
3. Compare pre/post snapshots, GPU resource generations, render-graph plans, reason strings, validation output, captures, CPU timing, and allocations.
4. Inspect staged disposal order and force each retryable/failure path.
5. Inspect the final diff against the dirty working tree and preserve unrelated changes.
6. Update this plan with the actual landed file/symbol deviations.

Exit criterion: every definition-of-done item is demonstrated and no phase relies on an unmerged follow-up for correctness.

## 10. File-level implementation map

### 10.1 New production files

Place coordinators beside the existing DDGI resource owners unless a repository-wide folder reorganization is separately approved:

- `Njulf.Rendering/Resources/AdvancedGiAdmissionCoordinator.cs`
- `Njulf.Rendering/Resources/DdgiSceneInvalidationCoordinator.cs`
- `Njulf.Rendering/Resources/DdgiEmissiveTransportCoordinator.cs`
- `Njulf.Rendering/Resources/SimpleDdgiReceiverFeedbackCoordinator.cs`
- `Njulf.Rendering/Resources/SimpleDdgiFrameEvidenceCoordinator.cs`
- `Njulf.Rendering/Resources/GiCausticFrameCoordinator.cs`
- `Njulf.Rendering/Resources/SimpleDdgiNearFieldResidualCoordinator.cs`
- `Njulf.Rendering/Resources/SimpleDdgiFrameCoordinator.cs`
- `Njulf.Rendering/Resources/SimpleDdgiGuidingConfigurationPlanner.cs` if the pure C3 compiler does not fit cleanly in an existing planner.
- `Njulf.Rendering/Core/DdgiFrameDataProjector.cs`

Keep coordinator-specific internal records in the coordinator file while they have one consumer. Create a separate contract file only when a contract is consumed by at least two production owners and has a stable domain name. Do not create `DdgiCoordinatorContracts.cs` as a miscellaneous dumping ground.

### 10.2 New focused test files

- `Njulf.Tests/AdvancedGiAdmissionCoordinatorTests.cs`
- `Njulf.Tests/DdgiSceneInvalidationCoordinatorTests.cs`
- `Njulf.Tests/DdgiEmissiveTransportCoordinatorTests.cs`
- `Njulf.Tests/SimpleDdgiReceiverFeedbackCoordinatorTests.cs`
- `Njulf.Tests/SimpleDdgiFrameEvidenceCoordinatorTests.cs`
- `Njulf.Tests/GiCausticFrameCoordinatorTests.cs`
- `Njulf.Tests/SimpleDdgiNearFieldResidualCoordinatorTests.cs`
- `Njulf.Tests/SimpleDdgiFrameCoordinatorTests.cs`
- `Njulf.Tests/DdgiFrameDataProjectorTests.cs`
- `Njulf.Tests/DdgiCoordinatorIntegrationTests.cs`

Use small fake leaf backends at existing seams. Do not create a fake `VulkanRenderer`.

### 10.3 Modified production files

- `Njulf.Rendering/VulkanRenderer.cs`
  - keep public facade and global boundaries;
  - construct coordinators in the existing dependency order;
  - capture typed requests and apply results/effects;
  - remove migrated fields/method bodies;
  - update descriptor/graph registration and staged disposal.
- `Njulf.Rendering/Resources/SimpleDdgiGuidingFrameCoordinator.cs`
  - only narrow configuration/snapshot changes needed for composition; retain C3 ownership.
- `Njulf.Rendering/Memory/AdvancedGiTransientBufferArena.cs`
  - only if a narrow immutable reconciliation result or slice-source interface is required; retain allocation ownership and behavior.
- `Njulf.Rendering/Resources/SimpleDdgiReceiverFeedbackVulkanRuntime.cs`
  - only narrow graph-resource/capture snapshot additions needed to hide the concrete runtime.
- `Njulf.Rendering/Resources/GiCausticVulkanRuntime.cs`
  - only narrow resource/publication snapshot additions needed by its coordinator.
- `Njulf.Rendering/Resources/SimpleDdgiNearFieldResidualVulkanRuntime.cs`
  - only narrow graph-resource snapshot additions needed by its coordinator.
- Render-graph/pass setup files that currently receive concrete renderer fields
  - consume immutable resource bindings; do not gain coordinator backreferences unless they already call the existing guiding coordinator by design.
- Staged disposal registration
  - replace individual renderer callbacks with coordinator stages at identical dependency positions.

### 10.4 Modified tests

Retarget direct renderer helper calls and remove source-file placement assertions in at least:

- `DdgiEmissiveTransportCacheTests.cs`
- `SimpleDdgiGpuSchedulerValidationTests.cs`
- `SimpleDdgiProbePagingShaderContractTests.cs`
- `SimpleDdgiReceiverFeedbackVulkanRuntimeTests.cs`
- `SimpleDdgiSourceRelightTests.cs`
- `SimpleDdgiTransientFrameEvidenceTests.cs`
- `SimpleDdgiWarmupStateTests.cs`
- `SimpleDdgiShaderMirrorTests.cs`
- `SimpleDdgiVolumeManagerTests.cs`
- `SimpleDdgiTransportTailTests.cs`
- relevant `FarFieldClipmapOracleTests.cs`, `HybridReflectionContractsTests.cs`, and debug-tooling tests.

Shader ABI tests that genuinely need to prove a CPU/GPU constant or resource declaration should inspect the stable contract type or graph/pass declaration, not an orchestration file.

### 10.5 Files expected to remain algorithmically stable

- all DDGI, B1, C3, C4, and C5 shader files;
- `SimpleDdgiVolumeManager.cs` except minimal accessors/snapshots justified by a coordinator contract;
- the scheduler, paging, storage, transport, refinement, warm-start, and reference-model implementations;
- `DdgiMutationJournal.cs` internals;
- `DdgiFoliageProxyManager.cs` internals;
- all low-level Vulkan runtime algorithms;
- render-graph pass algorithms and pass names;
- `SceneRenderingData`, `RendererDiagnostics`, and serialized capture schemas.

Any non-mechanical change to these files requires separate justification and tests; it must not be hidden inside an extraction diff.

## 11. Test matrix

### 11.1 Admission

- Default/unconfigured manifest and candidate profile behavior.
- Valid and invalid prerequisite manifests.
- Qualification evidence present, absent, stale, device-mismatched, build-mismatched, and feature-mismatched.
- Runtime content binding absent, first-scene bound, matching, and mismatched.
- Candidate authorization allowed, denied, and cleared.
- Explicit off/on and auto-qualified B1/C3/C4/C5 modes.
- Combined forward preflight and hybrid reflection/C4 preflight.
- Public facade guard/validation order and exact reason strings.

### 11.2 Invalidation and identity

- First scene, same scene, scene replacement, content revision, camera cut, and disabled/re-enabled transitions.
- Journal attach/detach/drain and overflow/full-invalidation behavior.
- Journal/oracle dirty-coverage equivalence.
- Static transform, material, emissive, topology, skinned-pose, sustained VFX, light, environment, and atmosphere changes.
- Foliage proxy bounds/signature changes and no-change frames.
- Dirty region ordering, padding, reason aggregation, and bounds union.
- Warm-start identity hit/miss and all component hashes.
- Sole directional relight admitted/rejected/scale behavior.

### 11.3 Emissive transport

- Empty scene and ray-update-disabled fallback.
- Static mesh, instanced mesh, cooked payload, runtime triangle scan, skinned exclusion, transparent/excluded content, analytic uniform emission, and VFX macro emitters.
- Source cap, deterministic importance ties, insertion/sort stability, saturation, and exclusion accounting.
- Cache hit/miss, material/base-payload/VFX signature changes, buffer-content invalidation, and upload suppression.
- Source/surface buffer bytes, offsets, counts, hierarchy nodes, revisions, and generation stability.
- Energy diagnostics and warning thresholds.
- Refinement emissive demand content, cap, signature, and reuse.
- Disposal retires both buffers exactly once at the existing safe stage.

### 11.4 Receiver feedback and shared B1/C3 arena

- B1 off/on/auto, prerequisites and qualification pass/fail, missing runtime, zero extent, capacity and storage-range limits, and memory-headroom rejection.
- Configuration-key reuse and changed viewport/volume/page generation.
- B1-only, C3-only, B1+C3, and neither arena plans.
- Arena replacement success/failure, generation change, slice alignment, and reader-wait failure.
- Pre-upload reconciliation, prior-summary scheduling acquisition, read barrier, post-upload initial-domain reconciliation, and deferred replacement after a recorded read.
- All producer combinations: screen, fog, particles, reflections, transparency, zero work, partial work, and abort.
- Finalization after the last producer, fence-complete readback, stale serial/generation rejection, and scheduling binding fallback.
- Existing C3 configuration/source-cache/runtime generation behavior remains green.

### 11.5 Frame evidence and liveness

- Capture pending, successful commit, submit failure abort, slot reuse, and invalid evidence.
- Ring consume occurs before observer/training work and at most once.
- CPU-reference, GPU-mirror, and GPU-resident scheduler authority.
- Sparse and non-sparse residency authority.
- Matching and mismatched scheduler/residency frame serials and generations.
- Resource-generation mismatch, source/transport transition, feedback rejection deltas, and reset baselines.
- Scheduler cost sample construction from material, alpha, far-field, primary, and visibility work.
- Liveness barriers, class/ring totals, publication progress, watchdog thresholds, disable reset, and re-enable behavior.

### 11.6 Core frame

- Disabled transition with and without previously active B1/C3/resources.
- Active frame with and without ray-query/AS availability.
- Far-field coverage ready/not ready.
- Mutation journal on/off.
- Receiver witness valid/stale/missing and camera fallback focus.
- First physical-domain upload and same-frame advanced-GI reconciliation.
- Page-management required/not required.
- Reflection recapture intent on readiness and dirty-reason transitions.
- No retained per-frame `Scene`, camera, settings, scene-data, or command-buffer reference.

### 11.7 C4

- Explicit off, admitted, auto-qualified, unauthorized, prerequisite failure, evidence failure, missing runtime, and missing producer.
- Hero source absent/present/changed and deterministic revision fingerprint.
- Runtime configure reuse, invalidation, prepare, readable revision, graph publication, and rejection.
- Extent-compatible reuse and device-idle incompatible disable.
- Fence-complete readback with matching and stale frame state.
- Emissive snapshot generation/revision consistency.

### 11.8 C5

- Explicit off, admitted, auto-qualified, unauthorized, preflight failure, unsupported targets, and allocation failure.
- Initial generation success/failure.
- Same-envelope reuse and extent/profile replacement.
- Prepared generation validation, failed publication, successful frame-boundary commit, and old-generation retirement.
- Fence-complete readback occurs before commit.
- Successful submit records the exact active-generation fence; failed submit records none.
- Target/pipeline/graph effect application is atomic and rollback-safe.
- Disable after device idle and disposal with active/prepared/retired generations.

### 11.9 Projection, diagnostics, and compatibility

- Field-for-field `SceneRenderingData` equality for representative core/B1/C3/C4/C5 states.
- Completed counters remain associated with their matching frame/generation.
- `RendererDiagnostics`, runtime/content snapshots, budget output, warning histories, and serialized capture schemas remain unchanged.
- Debug overlays receive equivalent DDGI volume/generation facts.
- Render-graph pass/resource names and queue admission inputs remain unchanged.
- Public renderer interfaces and settings serialize identically.

### 11.10 Runtime matrix

- First frame, warmed frame, scene reload, camera cut, and DDGI disable/re-enable.
- Static and rapidly changing lights/emissives/geometry/VFX.
- CPU-reference, GPU-mirror, and GPU-resident/sparse modes.
- B1/C3 combinations and shared-arena resize/reconfigure.
- C4 and C5 independently and together.
- Resize, minimized window, swapchain recreation, dynamic-resolution change, and target allocation failure.
- Submit failure/device loss simulation where available.
- Debug overlays, performance capture, screenshots, and validation on/off.
- Multi-minute traversal/soak sufficient to reuse every frame slot and retire multiple generations.

## 12. Verification commands

Run from the repository root after each relevant phase:

```powershell
dotnet build Njulf.Rendering/Njulf.Rendering.csproj --no-restore

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~AdvancedGi|FullyQualifiedName~DdgiMutation|FullyQualifiedName~DdgiEmissive|FullyQualifiedName~SimpleDdgiSourceRelight|FullyQualifiedName~SimpleDdgiWarmStart"

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~SimpleDdgiReceiverFeedback|FullyQualifiedName~SimpleDdgiGuiding|FullyQualifiedName~AdvancedGiScratchArena|FullyQualifiedName~SimpleDdgiTransientFrameEvidence|FullyQualifiedName~SimpleDdgiLiveness"

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~GiCaustic|FullyQualifiedName~SimpleDdgiNearFieldResidual|FullyQualifiedName~AdvancedGiRenderGraphModes"

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~SimpleDdgiFrameCoordinator|FullyQualifiedName~DdgiFrameDataProjector|FullyQualifiedName~SimpleDdgiVolumeManager|FullyQualifiedName~SimpleDdgiProbePagingShaderContract|FullyQualifiedName~SimpleDdgiShaderMirror"

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore

dotnet build Njulf.sln --no-restore
```

Structural checks during and after migration:

```powershell
rg -n "PrepareDdgiProbeVolumes|UploadDdgiEmissiveSources|CollectDdgiDirtyRegions|ReconcileSimpleDdgiReceiverFeedback|PrepareGiCausticFrame|TryCompileNearFieldResidualGeneration|UpdateSimpleDdgiLivenessTelemetry" Njulf.Rendering/VulkanRenderer.cs

rg -n "_ddgiTracked|_ddgiEmissive|_simpleDdgiReceiverFeedbackPlan|_simpleDdgiSchedulerCostModel|_simpleDdgiSubmittedFrameRing|_giCausticRuntimeConfigured|_simpleDdgiNearFieldResidualGenerations" Njulf.Rendering/VulkanRenderer.cs

rg -n -g "*Coordinator.cs" "VulkanRenderer" Njulf.Rendering/Resources Njulf.Rendering/Core/DdgiFrameDataProjector.cs

rg -n "VulkanRenderer\.(TryComputeSoleDirectionalRelightScale|CreateSimpleDdgi|ResolveSimpleDdgi|EstimateSimpleDdgi)" Njulf.Tests

rg -n 'ReadRepoText\("Njulf.Rendering", "VulkanRenderer.cs"\)' Njulf.Tests
```

Expected final structural result:

- None of the migrated method bodies or state fields remains in `VulkanRenderer`.
- Public advanced-GI methods are thin facades only.
- The renderer has one core DDGI prepare call, one C4 prepare call, one C5 resource snapshot/application path, one B1 finalization call, and explicit fence/submit boundary calls.
- No coordinator references `VulkanRenderer`.
- No coordinator other than the feature-specific resource owner directly exposes a mutable Vulkan runtime.
- No test depends on a helper remaining nested in `VulkanRenderer`.
- Source-reading tests that remain are genuine shader/ABI/resource-declaration contracts, not orchestration placement checks.

Do not use renderer line-count reduction as the only gate. A meaningful reduction is expected, but ownership, ordering, generation safety, test quality, and absence of a replacement god object are the actual acceptance criteria.

## 13. Risks and mitigations

### Risk: `SimpleDdgiFrameCoordinator` becomes the new god object

Mitigation: extract it last; keep algorithms and state in the seven preceding owners and existing leaf components; enforce the ownership table; reject any method moved into core that does not coordinate at least two named collaborators or a core enable/disable transition.

### Risk: admission and feature runtime state become duplicate authorities

Mitigation: admission owns evidence and authorization; C4/C5 own derived runtime execution state. Pass immutable admission snapshots with explicit revision/fingerprint identity and never let feature coordinators mutate admission.

### Risk: a giant request record hides the same coupling

Mitigation: use named nested request groups such as identity, capabilities, invalidation policy, and target facts. Do not flatten hundreds of fields or pass a generic context. Capture each group once at its current boundary.

### Risk: B1 accidentally owns shared C3 memory policy

Mitigation: B1 compiles only its desired memory request. Core DDGI combines B1/C3 requests and reconciles the existing arena once. The arena remains the sole physical owner.

### Risk: arena replacement invalidates a buffer already named by the command buffer

Mitigation: retain the summary-bank-read witness and post-upload reconciliation guard. Add an explicit test that a recorded scheduling read defers incompatible replacement until the next safe frame.

### Risk: current-frame feedback leaks into current-frame scheduling

Mitigation: use separate request types for prior published scheduling evidence and current capture. Make it impossible for `BeginCapture` output to satisfy `AcquireSchedulingBinding` for the same serial.

### Risk: fence-complete calls are reordered during cleanup

Mitigation: encode the audited BeginFrame order in one integration test with recording fakes. Keep readback, generation commit, evidence completion, and staging reuse as distinct named steps.

### Risk: failed terminal submission publishes evidence or a C5 reference

Mitigation: expose explicit success and abort methods; call success only after `QueueSubmit` returns success; test both terminal failure and fence-recovery branches.

### Risk: invalidation iteration order changes signatures or dirty coverage

Mitigation: preserve collection types, traversal order, quantization, hashing, and insertion order in the first move. Compare bit-identical hashes and ordered regions over multi-frame fixtures before any optimization.

### Risk: returned dirty-region or refinement-demand memory is mutated before use

Mitigation: define synchronous lifetime explicitly and use stable arrays plus count or immutable copies. Do not return a live `List<T>` that later coordinator work reuses in the same frame.

### Risk: emissive extraction changes GPU bytes despite equal semantic sources

Mitigation: compare complete source/surface buffer payloads and hierarchy bytes, not only counts. Preserve deterministic tie-breaking, floating-point operations, and cache copy order.

### Risk: empty/disabled emissive publication leaves stale bindings

Mitigation: characterize the current zero/fallback upload and content-valid behavior and keep it as a first-class coordinator transition.

### Risk: C4 observes torn emissive state

Mitigation: publish one immutable emissive snapshot only after source/surface/hierarchy upload and revision commit. C4 receives that snapshot by value.

### Risk: C5 coordinator gains arbitrary renderer mutation powers

Mitigation: use a closed `NearFieldResidualPublication` result and a renderer effect adapter. No callbacks that expose render targets, pipelines, or graph mutation beyond the enumerated transaction.

### Risk: C5 replacement loses the old working generation on failure

Mitigation: preserve allocate/validate-then-publish semantics of the existing generation transaction and test failure at allocation, validation, target binding, and publication.

### Risk: diagnostics move into multiple owners

Mitigation: coordinators expose raw coherent facts; `DdgiFrameDataProjector` maps pre-render fields; `RendererDiagnosticsAssembler` alone builds final diagnostics and warnings. Document every field as frame input, completed evidence, or final diagnostic.

### Risk: source-contract tests are mechanically redirected

Mitigation: decide what each test is proving. Use executable ordering tests, reflection/layout tests, graph-resource declaration tests, or shader constant tests against the real contract. Delete placement-only assertions.

### Risk: public configuration validation order changes

Mitigation: keep renderer facade methods and characterize null/file/parse/initialized cases. Move state mutation only after existing validation and lifetime guards have succeeded in the same order.

### Risk: disposal graph is flattened into coordinator `Dispose` calls

Mitigation: preserve one staged node per independently ordered resource group. A coordinator may implement `Dispose`, but the renderer's disposal plan must retain required dependencies and retry behavior; do not hide multiple failure-sensitive stages behind one irreversible call.

### Risk: transient allocations and CPU cost increase

Mitigation: retain reusable scratch arrays in invalidation/emissive owners; use value snapshots and bounded arrays; benchmark warmed active and disabled paths. Do not allocate a full diagnostics-shaped object during frame preparation.

### Risk: semantic usages are missed in the million-byte renderer file

Mitigation: implement declaration moves, renames, and signature changes with Rider semantic Find Usages/refactoring tools. Do not use project-wide textual replacement for C# symbols. Re-run semantic usage search before deleting each old declaration.

### Risk: unrelated dirty working-tree changes are overwritten

Mitigation: implement against the current tree, move only audited symbols, inspect every phase diff, and never reset or rewrite unrelated renderer/implementation changes.

## 14. Definition of done

The coordinator-family extraction is complete when all of the following are true:

1. All eight named coordinators exist with the ownership boundaries in Section 3.
2. The existing `SimpleDdgiGuidingFrameCoordinator` remains the single C3 execution owner.
3. `VulkanRenderer` has no mutable source of truth for advanced-GI admission, DDGI invalidation, emissive transport, B1 plan/capture, DDGI submitted evidence/liveness, C4 runtime state, or C5 generation state.
4. `PrepareDdgiProbeVolumes`, `UploadDdgiEmissiveSources`, renderer-local dirty collection, `ReconcileSimpleDdgiReceiverFeedback`, `PrepareGiCausticFrame`, renderer-local C5 generation methods, and renderer-local DDGI liveness methods no longer exist.
5. Core DDGI preparation is one thin coordinator call with explicit inputs/results; C4 and C5 remain separate calls/owners.
6. B1/C3 shared-arena reconciliation remains atomic and generation-safe, including the recorded-summary-read deferral rule.
7. Submitted evidence and C5 generation references publish only after successful terminal submission.
8. B1/C3/C4/C5 readback and C5 generation commit retain the exact fence-complete order.
9. Invalidation regions, signatures, warm-start identities, source relight, emissive bytes/revisions, volume upload inputs, and page-management output match the baseline.
10. C4 publication and C5 replacement remain failure-atomic and generation-safe.
11. `DdgiFrameDataProjector` produces field-equivalent pre-render data without owning state.
12. `RendererDiagnosticsAssembler` remains the single final diagnostics/warning owner.
13. No coordinator references `VulkanRenderer`, retains per-frame mutable inputs, or accepts an untyped mega-context/service locator.
14. Public APIs, settings, graph/pass names, GPU ABI, diagnostic/capture schemas, and reason strings remain compatible.
15. Old renderer-helper tests target real owners, and placement-only source tests have executable replacements.
16. Focused tests, the full `Njulf.Tests` suite, rendering-project build, and solution build pass.
17. Runtime validation is clean across scheduler modes, B1/C3/C4/C5 combinations, scene changes, resize/recreation, submission failure, debug/capture, and validation transitions.
18. Warming-frame CPU time and allocations show no material regression, and no normal-frame device-idle wait was introduced.
19. Staged disposal retains the current safe dependency order and retry behavior.
20. Every phase can be reviewed or reverted without requiring an unfinished later phase to restore correctness.

## 15. Follow-up work explicitly deferred

After this family is stable, create separate plans if needed for:

- decomposing `SimpleDdgiVolumeManager` into storage, scheduler, transport, paging, and publication owners;
- reorganizing the large flat `Resources` directory into domain subfolders/namespaces;
- replacing temporary concrete leaf references in debug or graph code with narrow read-only interfaces where there is a demonstrated testing or lifetime benefit;
- optimizing B1 receiver-cache construction;
- optimizing C5 inputs, histories, tile admission, reconstruction, and memory;
- changing DDGI frame-data or public diagnostics schemas;
- changing advanced-GI qualification policy or graduating C1/C3/C4/C5 modes;
- generalizing caustics, transmission, or participating-media transport;
- changing cross-queue execution or render-graph aliasing.

None of those follow-ups is required to complete this behavior-preserving renderer extraction.
