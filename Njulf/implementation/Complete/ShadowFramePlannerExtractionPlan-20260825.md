# ShadowFramePlanner Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor.

## 1. Required outcome

Extract directional-shadow frame policy from `Njulf.Rendering/VulkanRenderer.cs` into a focused, stateful `ShadowFramePlanner` under `Njulf.Rendering/Resources`.

The completed refactor must:

- Replace the policy-heavy portions of `ResolveDirectionalShadowFramePlan`, currently lines 6476-6795 in the audited working tree, with a narrow renderer-side resource orchestration shell plus calls to `ShadowFramePlanner`.
- Move effective-mode selection, renderer-specific fallback overrides, qualified-budget hysteresis, cascade masks, ray/history consumer selection, receiver policies, qualification labeling, and `DirectionalShadowFramePlan` construction into the planner.
- Move the two planner-owned renderer fields into the planner:
  - `_directionalShadowQualifiedBudgetOverrunStreak`;
  - `_directionalShadowBudgetDemotionUntilFrame`.
- Keep Vulkan/device queries, qualification-context capture, lazy resource allocation, history-resource invalidation, GPU parameter building, buffer uploads, and render-pass execution outside the planner.
- Preserve the existing requested/effective distinction, fail-closed CSM fallback, exact fallback reasons/details, qualification levels, cross-frame budget demotion, and all downstream pass inputs.
- Preserve the public `DirectionalShadowFramePlan`, settings, diagnostics, capture, and serialization contracts.

The intended flow is:

```text
settings + scene/light/readiness snapshots
                    |
                    v
 renderer prepares/ensures concrete resources
                    |
                    v
 ShadowFramePlanner.ResolveCandidate
                    |
       conditional qualification evaluation
                    |
                    v
       ShadowFramePlanner.CreatePlan
                    |
                    v
       DirectionalShadowFramePlan
          /            |            \
         v             v             v
  CSM/ray passes  history policy  diagnostics/capture
```

`ShadowFramePlanner` must not depend on, retain, or call back into `VulkanRenderer`.

## 2. Audited starting point

### 2.1 Mixed planning region

`VulkanRenderer.cs` is 23,084 lines in the audited working tree. The relevant contiguous region is:

- lines 6379-6410: `EvaluateDirectionalShadowQualification`;
- lines 6412-6474: `IsQualifiedDirectionalShadowBudgetDemoted`;
- lines 6476-6795: `ResolveDirectionalShadowFramePlan`.

Together they span approximately 417 lines, but they do not all have the same owner:

| Responsibility | Final owner |
| --- | --- |
| Vulkan physical-device property query | `VulkanRenderer` orchestration |
| Capture/build/settings qualification-context construction | `VulkanRenderer` or the planned capture provider integration |
| Qualification-manifest evaluation | Existing renderer-side qualification boundary for this extraction |
| History and ray-mask `Ensure` calls | Existing resource owners, invoked by `VulkanRenderer` |
| Mode resolution and fallback overrides | `ShadowFramePlanner` |
| Three-frame budget streak and 120-frame cooldown | `ShadowFramePlanner` |
| Cascade masks, consumers, receiver policies, and qualification publication | `ShadowFramePlanner` |
| `DirectionalShadowFramePlan` construction | `ShadowFramePlanner` |
| History invalidation, GPU parameter construction, and upload | `VulkanRenderer` orchestration |

The planner extraction should remove approximately 250-300 lines of policy from `VulkanRenderer`, while leaving a clearly effectful preparation/application method rather than hiding Vulkan work behind a planning name.

### 2.2 Existing call boundary and frame ordering

The current call occurs once in `DrawScene` at lines 4695-4698.

Before the call:

1. The renderer has selected the shadow-casting directional light and prepared stable CSM data.
2. `PrepareDirectionalShadows` has populated `SceneRenderingData` with cascade count, shadowed-light index, shadow data, fit diagnostics, biases, debug settings, light direction, and initial GPU parameters.
3. Scene submission, skinning, and acceleration-structure recording have completed far enough to publish the current `RaySceneReadinessSnapshot`.
4. Transparent and decal receiver enablement/counts are already known.

Inside the current method:

1. Readiness and screen extent are captured.
2. CSM-temporal auto qualification is evaluated and written back to the runtime-only settings admission flag.
3. History and ray-mask resources are ensured.
4. A candidate effective mode is resolved and resource-specific fallback details are applied.
5. Conditional ray qualification and completed-frame budget demotion are evaluated.
6. One `DirectionalShadowFramePlan` is published.
7. Unused history is invalidated, GPU directional-shadow parameters are rebuilt from the effective plan, and shadow data/parameters are uploaded.

After the call, DDGI preparation and render-graph execution consume the plan. This ordering must remain unchanged.

### 2.3 Current policy inputs

The current method reads policy from these groups:

**Settings**

- directional shadows enabled and requested mode;
- CSM temporal mode/effective admission;
- soft angular-diameter scale;
- shadow debug view;
- directional-shadow receiver-counter setting;
- anti-aliasing effective mode and quality preset;
- environment sun angular diameter;
- other settings consumed by `SurfaceHistoryPolicy` and the qualification fingerprint.

**Prepared frame state**

- frame serial;
- screen extent;
- cascade count and shadowed directional-light index;
- transparent/decal receiver flags and meshlet counts;
- current ray-scene readiness and generation/content epoch;
- near-field residual history participation;
- stable directional-light identities.

**Concrete capabilities/resources**

- ray-query and acceleration-structure support;
- universal CSM fallback availability;
- ray mask allocation result and failure detail;
- temporal/history resource allocation and generation;
- transparent ray descriptor/pipeline availability.

**Evidence and delayed feedback**

- CSM-temporal and ray qualification gate results;
- the previous published `DirectionalShadowRuntimeDiagnostics`;
- capture shader, commit, worktree, settings, device, driver, API, geometry-category, resolution, and AA identity used to evaluate the manifest.

The planner should receive typed values from these groups. It must not read managers, render targets, command buffers, renderer fields, or `LastDiagnostics` itself.

### 2.4 Existing stateful budget policy

The current budget-demotion helper is stateful and applies only to a passed qualification with positive GPU and memory budgets.

Its exact behavior is:

1. If qualification is not passed or either budget is absent, reset the overrun streak and do not demote.
2. If the current frame serial is before `_directionalShadowBudgetDemotionUntilFrame`, demote immediately with the existing cooldown detail.
3. Otherwise inspect the previous published `DirectionalShadowRuntimeDiagnostics`.
4. If that completed snapshot was not `Production` or its effective mode differs, reset the streak and do not demote.
5. Sum CSM, ray-trace, temporal, and spatial GPU microseconds with checked arithmetic.
6. Sum ray-mask and history bytes with checked arithmetic.
7. Increment the streak when either value is strictly greater than its qualified budget; otherwise decrement the streak toward zero.
8. On the third consecutive over-budget observation, reset the streak, set a checked 120-frame cooldown, and demote to CSM with the exact measured/budget detail.

The two tracks share one streak and cooldown. A demotion triggered by qualified ray shadows can therefore cool down qualified CSM temporal, and vice versa. Do not silently split this into per-mode trackers.

If no qualified path invokes the policy in a frame, the current streak is not proactively reset. Preserve that lifecycle.

### 2.5 Candidate and final-plan semantics

Candidate resolution currently applies policy in this order:

1. Call the existing `DirectionalShadowModeResolver.Resolve` using settings, light/capability/readiness state, receiver resources, and transparent variant availability.
2. If a soft request has effectively resolved to soft but its angular radius is effectively zero, convert it to deterministic hard rays with the exact existing detail.
3. If a requested ray mode lacks the ray mask and the resolver reported `DirectionalShadowFallbackReason.RequiredReceiverResourceUnavailable`, replace the generic resource failure with `DirectionalRayShadowPass.FailureDetail`; classify text containing `allocation failed` as `ResourceAllocationFailed`.
4. If universal CSM resources are unavailable while shadows and a shadow-casting directional light are active, force the explicit universal-fallback resource-allocation failure.

Final planning then applies CSM-temporal budget demotion before ray qualification/budget demotion, derives the active cascade mask, and publishes:

- requested/effective modes and fallback reason/detail;
- stable light identity;
- CSM fallback demand and receiver policies;
- requested ray-scene consumer, even when the effective mode falls back;
- surface-history consumers based on the final effective mode;
- ray-scene resource generation and content epoch;
- screen-history generation and sun angular radius;
- qualification level, identity, detail, device rule, track, and budgets.

### 2.6 Downstream consumers and later mutation

`DirectionalShadowFramePlan` is consumed by:

- `DirectionalShadowPass` for CSM admission;
- `DirectionalRayShadowPass` for ray admission, mode, maximum distance, recovery sampling, and initial history reset;
- directional temporal and spatial passes for CSM/soft-history admission and filtering;
- `DirectionalShadowHistoryResources` for revision identity;
- `SceneOpaqueCompactionPass` for CSM caster submission;
- `TransparentForwardPass` and `WeightedTransparentPass` for receiver policy;
- `MotionVectorPass` for surface-history consumers;
- renderer diagnostics, performance captures, qualification reporting, and editor/sample output.

The initial plan is intentionally not the final immutable diagnostic value. Ray and temporal passes later update `HistoryResetReason` with `with` expressions after comparing the live history revision. Diagnostics observe that post-pass plan. The new planner must create the initial plan only; it must not take ownership of pass-time history revision or commits.

### 2.7 Existing tests and gaps

Current tests cover:

- `DirectionalShadowModeResolver` fail-closed capability/readiness/resource behavior;
- `SurfaceHistoryPolicy` consumer composition;
- `RaySceneReadinessSnapshot` and directional ray-scene requirements;
- qualification-manifest loading, identity matching, authentication, and settings fingerprinting;
- shadow data building, cascade fitting, cache signatures, shader contracts, and render-graph ordering.

There is no focused test fixture for:

- the renderer-specific candidate overrides after `DirectionalShadowModeResolver`;
- final `DirectionalShadowFramePlan` construction;
- receiver-policy combinations;
- qualification-level publication;
- the shared three-frame/120-frame budget state machine;
- conditional qualification evaluation ordering;
- exact propagation of stable light and resource generations.

## 3. Scope

### In scope

- An internal renderer-lifetime `ShadowFramePlanner`.
- Typed candidate-resolution and final-plan input/output contracts.
- Moving the qualified budget streak and cooldown into the planner.
- Moving the mode override order, plan derivation, qualification labeling, receiver/history policy, and frame-plan construction.
- Replacing `ResolveDirectionalShadowFramePlan` with an honestly named renderer-side `PrepareDirectionalShadowFrame` orchestration method.
- Keeping conditional CSM/ray qualification calls at the same points.
- Focused planner characterization, state-machine, compatibility, and runtime tests.

### Out of scope

- Local spot/point shadow selection, atlas allocation, upload signatures, or frame planning.
- CSM fitting/stabilization, cache state, caster submission, or cascade-data generation.
- Changing `DirectionalShadowModeResolver`, `SurfaceHistoryPolicy`, `DirectionalShadowFramePlan`, `RaySceneReadinessSnapshot`, or public enum values.
- Changing qualification manifest files/codecs, authentication, settings fingerprints, or promotion rules.
- Moving Vulkan physical-device queries or performance-capture identity discovery into the planner.
- Changing when history/ray-mask resources allocate, retry, invalidate, or report failure.
- Changing shadow passes, render-graph declarations, descriptor bindings, shaders, GPU structs, barriers, timestamps, or queue ownership.
- Changing budget values, overrun threshold, cooldown length, comparison operators, measured components, or fallback wording.
- Changing runtime-only `DirectionalCsmTemporalQualificationApproved` persistence/serialization behavior.
- Adding settings, diagnostics, snapshot, or capture schema fields.
- Passing `VulkanRenderer`, a command buffer, a resource manager, a render pass, or a service locator into the planner.
- Combining this extraction with shadow-quality tuning or a new backend.

Although the requested class is named `ShadowFramePlanner`, its scope is the existing authoritative directional-shadow plan. Local-light shadow systems do not currently publish an equivalent frame-plan contract and remain outside this extraction.

## 4. Non-negotiable invariants

1. Planning occurs exactly once per successful `DrawScene` at the current boundary after current-pose acceleration-structure readiness is published and before any directional-shadow consumer pass executes.
2. Stable CSM data continues to be prepared before planning even when a ray mode is requested, so same-frame fallback remains initialized.
3. `DirectionalShadowQualificationManifest` evaluation remains conditional: CSM temporal is evaluated only for `Cascaded + Auto`, and ray qualification is evaluated only after candidate resolution leaves a non-CSM effective mode.
4. The planner receives qualification results; it performs no device query, file access, hash discovery, manifest loading, or capture-provider call.
5. CSM-temporal qualification approval is still written to `ShadowSettings.DirectionalCsmTemporalQualificationApproved` every frame, including clearing a previously approved value when auto temporal is not requested or no longer passes.
6. History and ray-mask `Ensure` calls occur before candidate resolution and retain current dimensions, detailed-diagnostic demand, history demand, retry behavior, exception handling, and logging.
7. Ray resource preparation remains gated by universal CSM availability. A ray path must not bypass the required same-frame fallback.
8. Candidate override order remains resolver, zero-radius soft-to-hard collapse, concrete ray-resource failure detail, then universal CSM resource failure.
9. Requested and effective modes remain distinct. `RaySceneRequirement` continues to describe requested intent even when effective rendering falls back to CSM.
10. Explicit ray modes remain usable as `Experimental` when their qualification manifest does not pass; manifest failure does not newly force CSM.
11. CSM temporal `Auto` remains fail-closed and becomes active only when qualification approves it and history resources allocate. `DeveloperForce` remains labeled `Developer`.
12. The budget tracker remains one shared renderer-lifetime state machine with a three-observation threshold and 120-frame cooldown.
13. Budget comparisons remain strict `>` comparisons. GPU and memory sums and cooldown arithmetic remain checked.
14. The planner reads only the previous published `DirectionalShadowRuntimeDiagnostics`, never current incomplete `SceneRenderingData` timings.
15. No planner reset is introduced for scene changes, mode changes, resize, swapchain recreation, or frames where no qualified path invokes budget evaluation.
16. The active cascade mask remains zero for zero cascades and `(1u << count) - 1u` after clamping to `ShadowSettings.MaxDirectionalCascades`.
17. Stable light identity uses the selected shadowed-light index only when it is in bounds; otherwise it remains zero.
18. Surface-history consumers are derived from the final post-demotion effective mode, not merely requested intent.
19. CSM debug views and active decal receivers continue to retain cascaded fallback for full hard/soft ray modes.
20. Opaque, transparent, and decal receiver policies preserve the exact current boolean precedence, including HybridContact transparent fallback when ray fragment variants are unavailable.
21. Qualification IDs/details/rules/tracks/budgets are published only from the same gate result selected by current behavior; production CSM fallback keeps its current synthetic rejection details.
22. The initial plan's `HistoryResetReason` remains available for later pass-time `with` updates. The planner does not commit or invalidate temporal history.
23. History bytes are reported only when the final plan uses screen history; unused history state is invalidated at the same renderer-side boundary.
24. `DirectionalShadowDataBuilder.BuildParameters` and `_directionalShadowResources.UploadShadowData` run after the final plan is assigned and retain existing inputs/order.
25. The planner retains only its two budget-policy scalars. It does not retain `RenderSettings`, `ShadowSettings`, scene/light/readiness snapshots, qualification results, diagnostics, strings, or per-frame inputs.
26. The extraction introduces no new steady-frame allocation, LINQ, reflection, delegates, logging, Vulkan work, or synchronization.
27. All public settings, frame-plan, diagnostics, and capture contracts remain source- and schema-compatible.

## 5. Chosen architecture

### 5.1 Two-stage stateful planner

Add `Njulf.Rendering/Resources/ShadowFramePlanner.cs`:

```csharp
internal sealed class ShadowFramePlanner
{
    private const int QualifiedBudgetOverrunThreshold = 3;
    private const ulong BudgetDemotionCooldownFrames = 120UL;

    private int _qualifiedBudgetOverrunStreak;
    private ulong _budgetDemotionUntilFrame;

    internal ShadowFrameCandidate ResolveCandidate(
        in ShadowFrameCandidateInput input);

    internal DirectionalShadowFramePlan CreatePlan(
        in ShadowFramePlanInput input);
}
```

The split is required because ray qualification must remain conditional on the resource-backed candidate:

```text
ResolveCandidate
      |
      +-- Cascaded --> use the existing not-effective rejection result
      |
      `-- non-CSM --> evaluate exact ray qualification context
                              |
                              v
                         CreatePlan
```

A one-shot method would either need a callback into the renderer or would evaluate manifests/device identity for modes already known to be unavailable. Both would blur ownership and change current per-frame work.

### 5.2 Candidate contract

Use a typed candidate rather than the current unnamed tuple:

```csharp
internal readonly record struct ShadowFrameCandidate(
    DirectionalShadowMode EffectiveMode,
    DirectionalShadowFallbackReason FallbackReason,
    string FallbackDetail);
```

`ShadowFrameCandidateInput` should contain only synchronous policy inputs:

```csharp
internal readonly record struct ShadowFrameCandidateInput(
    ShadowSettings Settings,
    bool HasShadowCastingDirectionalLight,
    bool RayQuerySupported,
    RaySceneReadinessSnapshot RaySceneReadiness,
    bool RayMaskAvailable,
    bool SoftHistoryAvailable,
    bool TransparentRayReceiverRequired,
    bool TransparentRayVariantAvailable,
    bool SoftCollapsesToHard,
    bool UniversalCsmFallbackAvailable,
    bool RayResourceProviderPresent,
    string RayResourceFailureDetail);
```

The planner does not retain `Settings`. Keeping the existing validated `ShadowSettings` object as a synchronous input lets it reuse `DirectionalShadowModeResolver` without duplicating that public policy or changing its signature during this extraction.

`ResolveCandidate` owns the exact four-step ordering documented in section 2.5.

### 5.3 Finalization contract

`ShadowFramePlanInput` supplies the candidate plus the values needed to construct the authoritative plan:

```csharp
internal readonly record struct ShadowFramePlanInput(
    RenderSettings Settings,
    ShadowFrameCandidate Candidate,
    bool CsmTemporalActive,
    DirectionalShadowQualificationGateResult CsmTemporalQualification,
    DirectionalShadowQualificationGateResult RayQualification,
    ulong FrameSerial,
    DirectionalShadowRuntimeDiagnostics CompletedRuntime,
    int CascadeCount,
    ulong StableLightIdentity,
    bool NearFieldResidualHistoryActive,
    bool GeometryDecalCsmFallbackRequired,
    bool CsmDebugFallbackRequired,
    bool TransparentRayVariantAvailable,
    uint ScreenResourceGeneration,
    float SunAngularRadiusRadians,
    RaySceneReadinessSnapshot RaySceneReadiness);
```

Names may be adjusted to project conventions, but do not replace this with a broad renderer context or `SceneRenderingData` parameter. Every field should make a policy dependency visible.

Passing `RenderSettings` synchronously preserves the existing canonical `SurfaceHistoryPolicy.Resolve` call. The planner must not mutate or retain it. The renderer must set the runtime-only CSM qualification approval before constructing this input.

### 5.4 Budget state machine ownership

Move `IsQualifiedDirectionalShadowBudgetDemoted` into `ShadowFramePlanner` as a private method whose inputs explicitly include:

- candidate/effective mode;
- qualification result;
- current frame serial;
- previous completed `DirectionalShadowRuntimeDiagnostics`.

Do not let the planner read `RendererDiagnostics` or `VulkanRenderer.LastDiagnostics`. At the call site, pass:

```csharp
_lastDiagnostics.DirectionalShadowRuntime
```

This makes the one-frame feedback edge explicit and keeps the planner compatible with the proposed `RendererDiagnosticsAssembler` extraction.

The private method must preserve exact reset, decrement, threshold, cooldown, checked arithmetic, and detail formatting behavior. The planner has renderer-instance lifetime and no public reset method.

### 5.5 Final-plan assembly stages

`CreatePlan` should remain easy to compare with the current statement order:

1. Copy the candidate into mutable local effective/reason/detail values.
2. If qualified CSM temporal auto is active, evaluate budget demotion; on demotion disable temporal and publish CSM `GpuBudgetDemotion`.
3. If the candidate remains non-CSM, evaluate its qualified budget; on demotion publish CSM `GpuBudgetDemotion`.
4. Clamp cascade count and compute the active mask.
5. Derive requested ray consumer from requested mode.
6. Call `SurfaceHistoryPolicy.Resolve` with final CSM-temporal and soft-ray booleans.
7. Derive cascaded receiver fallback demand.
8. Select the gate result and qualification level using the existing four branches:
   - effective ray mode;
   - auto-qualified CSM temporal;
   - developer-forced CSM temporal;
   - production baseline/fallback CSM.
9. Construct `DirectionalShadowFramePlan` with unchanged positional/init fields and receiver-policy expressions.

Keep exact synthetic qualification details:

- `directional-shadow-csm-temporal-developer-force`;
- `directional-shadow-baseline-csm-does-not-require-manifest`;
- `directional-shadow-ray-request-fell-back-to-production-csm`;
- the CSM-temporal qualification failure detail when auto was requested but inactive.

Do not “clean up” these values during extraction; they are observable capture/diagnostic data.

### 5.6 Renderer-side orchestration

Rename the remaining effectful method to `PrepareDirectionalShadowFrame` so its name describes what it still does.

Its responsibilities are:

1. Capture `ShadowSettings`, ray-scene readiness, screen extent, requested mode, sun radius, and diagnostic demand.
2. Publish `sceneData.RaySceneReadiness` and `DirectionalShadowRayCountersEnabled`.
3. Conditionally call `EvaluateDirectionalShadowQualification` for CSM temporal auto and update `DirectionalCsmTemporalQualificationApproved`.
4. Ensure CSM-temporal history resources with the current exception/reset/logging behavior.
5. Ensure ray-mask/history resources with current fallback gating and retry semantics.
6. Capture transparent variant availability and receiver demand.
7. Call `_shadowFramePlanner.ResolveCandidate(...)`.
8. Conditionally evaluate ray qualification only when the candidate remains non-CSM.
9. Resolve stable light identity and final per-frame boolean/scalar inputs.
10. Call `_shadowFramePlanner.CreatePlan(...)` and assign the result to `sceneData.DirectionalShadowFramePlan`.
11. Publish history bytes or invalidate unused history.
12. Build final GPU parameters and upload shadow data/parameters.

The resulting method is an effect coordinator. It should contain no fallback/receiver/qualification-level/budget policy beyond capturing inputs and applying the returned plan.

Conceptually:

```csharp
ShadowFrameCandidate candidate = _shadowFramePlanner.ResolveCandidate(
    candidateInput);

DirectionalShadowQualificationGateResult rayQualification =
    candidate.EffectiveMode != DirectionalShadowMode.Cascaded
        ? EvaluateDirectionalShadowQualification(/* candidate mode */)
        : DirectionalShadowQualificationGateResult.Reject(
            "directional-shadow-ray-mode-not-effective");

sceneData.DirectionalShadowFramePlan = _shadowFramePlanner.CreatePlan(
    finalInput with
    {
        Candidate = candidate,
        RayQualification = rayQualification,
        CompletedRuntime = _lastDiagnostics.DirectionalShadowRuntime
    });
```

Use normal constructor arguments rather than `with` if that is clearer in the implementation; the example illustrates data flow, not required syntax.

### 5.7 Qualification boundary remains separate

Keep `EvaluateDirectionalShadowQualification` outside the planner for this extraction because constructing its runtime context currently depends on:

- a Vulkan physical-device property query;
- performance-capture shader/commit/worktree identity;
- the full settings fingerprint;
- resolution, AA mode, quality preset, and current ray-scene category provenance;
- the configured immutable manifest.

The planner consumes only `DirectionalShadowQualificationGateResult` values. This prevents a dependency on the planned `PerformanceCaptureMetadataProvider` and keeps the planner deterministic in unit tests.

The existing public manifest configuration methods remain on `VulkanRenderer` and keep their pre-initialization guard and fail-closed empty-manifest behavior.

### 5.8 Resource and history ownership remains separate

The planner must not receive `DirectionalShadowHistoryResources`, `DirectionalRayShadowPass`, `DirectionalShadowResources`, `RenderTargetManager`, `AccelerationStructureManager`, `MeshPipeline`, or descriptor-bank objects.

Pass only the results they publish:

- allocation/capability booleans;
- resource failure detail;
- resource generation;
- readiness snapshot;
- transparent variant availability;
- estimated history bytes remain applied by the renderer after planning.

The renderer continues to invalidate history when `plan.UsesScreenHistory` is false. Ray/temporal passes continue to resolve and commit revision state when they execute.

### 5.9 Coordination with other renderer extraction plans

**PerformanceCaptureMetadataProvider**

The qualification-context helper should eventually read shader/commit/worktree identity from the provider. `ShadowFramePlanner` remains unaffected because it accepts the evaluated gate result.

**RendererDiagnosticsAssembler**

The planner consumes the previous published `DirectionalShadowRuntimeDiagnostics` as an explicit input. Diagnostics assembly still observes the post-pass plan, including later `HistoryResetReason` updates. Preserve the cycle:

```text
previous completed diagnostics -> current planner budget policy
current plan -> shadow passes/history updates -> current diagnostics
```

**DebugOverlayBuilder**

Directional cascade overlays read the final `SceneRenderingData` shadow plan/data but do not participate in planning. Neither class should depend on the other.

Because `VulkanRenderer.cs` contains unrelated working-tree changes, implementation must use narrow edits around the audited fields, one call site, and the 6379-6795 region. Do not replace or revert the full file.

## 6. Delivery order and change isolation

### Phase 0: Characterize current policy

Before production movement:

1. Add table-driven tests for every renderer-specific candidate override and exact fallback detail.
2. Add plan-construction tests for all requested/effective modes and receiver combinations.
3. Add qualification-level tests for baseline CSM, auto temporal pass/fail, developer force, explicit qualified ray, explicit unqualified ray, and ray-to-CSM fallback.
4. Add budget-state tests covering reset, decrement, third-strike demotion, cooldown, shared cross-track state, and checked arithmetic.
5. Record a runtime diagnostics baseline for Cascaded, HybridContact, RayQueryHard, RayQuerySoft, and CSM temporal modes.

Exit criterion: the tests fail on reordered fallback precedence, changed qualification detail, receiver policy, or budget timing.

### Phase 1: Add planner contracts and shell

1. Add `ShadowFrameCandidate`, `ShadowFrameCandidateInput`, and `ShadowFramePlanInput` as internal contracts next to the planner.
2. Add a renderer-lifetime `_shadowFramePlanner` field.
3. Move the two budget-state fields into the planner without changing the current method yet.
4. Temporarily route budget evaluation through the planner while plan construction remains in the renderer.

Exit criterion: budget characterization tests pass and no old state field remains in `VulkanRenderer`.

### Phase 2: Extract candidate resolution

1. Move the call to `DirectionalShadowModeResolver.Resolve` and the three renderer-specific overrides into `ResolveCandidate`.
2. Capture concrete resource/capability outcomes in `ShadowFrameCandidateInput`.
3. Keep resource ensures and their exact order in the renderer.
4. Move the ray qualification call between `ResolveCandidate` and `CreatePlan` so it remains conditional.

Exit criterion: every capability/readiness/allocation/zero-radius case returns the baseline candidate and performs the same qualification calls.

### Phase 3: Extract final plan construction

1. Move both budget-demotion branches.
2. Move cascade mask, stable identity input use, ray/history consumer selection, fallback demand, qualification selection, and receiver policy.
3. Return one complete `DirectionalShadowFramePlan` from `CreatePlan`.
4. Assign it once in the renderer before history/parameter application.

Exit criterion: planner tests compare the entire frame-plan value for representative inputs, including every init-only field.

### Phase 4: Reduce renderer method to effect orchestration

1. Rename `ResolveDirectionalShadowFramePlan` to `PrepareDirectionalShadowFrame` using semantic rename/update of its single call site.
2. Remove policy locals now owned by the planner.
3. Keep readiness publication, qualification-context evaluation, settings admission mutation, resource ensures, exception logging, history byte/invalidation handling, GPU parameter construction, and upload.
4. Confirm there is no planner-to-renderer callback or resource-manager reference.

Exit criterion: the renderer method reads as input capture, resource preparation, planner calls, and result application only.

### Phase 5: Semantic usage audit and automated verification

1. Use IDE semantic Find Usages for `ResolveDirectionalShadowFramePlan`, the two old budget fields, and `IsQualifiedDirectionalShadowBudgetDemoted` before deleting old declarations.
2. Confirm `DirectionalShadowFramePlan` downstream consumers are unchanged.
3. Run focused tests, full tests, and the solution build.
4. Verify no public API/schema diff was introduced.

Exit criterion: no compatibility wrapper or duplicate policy remains in `VulkanRenderer`.

### Phase 6: Runtime validation

1. Exercise the capability/resource/fallback matrix with Vulkan validation enabled.
2. Exercise qualification and budget transitions with deterministic captured inputs.
3. Verify post-pass history reset updates still reach diagnostics.
4. Compare requested/effective/fallback/qualification/receiver/history diagnostics with the baseline.

Exit criterion: frame behavior and evidence are unchanged across all modes and transitions.

## 7. File-level implementation map

### New files

`Njulf.Rendering/Resources/ShadowFramePlanner.cs`

- Internal candidate/final input contracts.
- `ShadowFramePlanner` and its two state fields.
- Candidate resolution, budget policy, plan construction, and pure support helpers.

`Njulf.Tests/ShadowFramePlannerTests.cs`

- Candidate, plan, qualification-label, receiver/history policy, and state-machine tests.

### Modified files

`Njulf.Rendering/VulkanRenderer.cs`

- Add `_shadowFramePlanner`.
- Remove the two budget fields and old budget helper.
- Rename the one call/method to `PrepareDirectionalShadowFrame`.
- Capture typed inputs, conditionally evaluate qualification, invoke the planner, and apply/upload the result.

### Files expected to remain schema/behavior stable

- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Rendering/Data/DirectionalShadowContracts.cs`
- `Njulf.Rendering/Data/DirectionalShadowRuntimeDiagnostics.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/DirectionalShadowDataBuilder.cs`
- `Njulf.Rendering/Resources/DirectionalShadowQualificationManifest.cs`
- `Njulf.Rendering/Resources/DirectionalShadowHistoryResources.cs`
- `Njulf.Rendering/Resources/DirectionalShadowResources.cs`
- `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs`
- `Njulf.Rendering/Pipeline/DirectionalRayShadowPass.cs`
- `Njulf.Rendering/Pipeline/DirectionalShadowScreenPasses.cs`
- `Njulf.Rendering/Pipeline/MotionVectorPass.cs`
- transparent/weighted-forward and scene-compaction passes;
- diagnostics, performance-snapshot, and qualification manifest schemas.

If implementing the extraction requires changing one of these contracts, treat that as a separate reviewed behavior change rather than silently folding it into the move.

## 8. Test matrix

### Candidate resolution

- Shadows disabled and no shadow-casting directional light preserve existing CSM fallback reasons/details.
- Unsupported ray queries, incomplete/generation-mismatched ray scenes, missing qualified bounds, missing mask, and missing transparent variants preserve resolver results.
- Zero angular radius converts only an otherwise-effective `RayQuerySoft` candidate to `RayQueryHard` with the exact deterministic-hard detail.
- A ray-mask failure uses the pass failure detail and maps only case-insensitive `allocation failed` text to `ResourceAllocationFailed`.
- Empty/whitespace ray failure detail retains the resolver detail.
- Missing universal CSM resources override earlier candidate outcomes only when shadows and a shadow-casting directional light are active.
- Override ordering is tested with inputs where multiple failures are simultaneously true.

### Plan construction

- Cascade counts below zero, zero, one through four, and above four produce the exact active mask.
- Stable light identity, ray-scene generation/epoch, screen generation, and sun radius are copied unchanged.
- Requested HybridContact maps to `DirectionalContact`; requested hard/soft maps to `DirectionalFull`; Cascaded maps to none, including fallback cases.
- TAA, near-field residual, CSM temporal, and soft-ray history consumers reflect final effective state.
- Full hard/soft ray modes retain CSM for active decals and named CSM debug views; HybridContact retains its inherent CSM path without mislabeling layered fallback.
- Opaque, transparent, and decal receiver policies cover Cascaded, HybridContact with/without transparent variants, hard, soft, and CSM-temporal cases.
- Static refresh/reuse masks and working composition mask retain current initial values.

### Qualification publication

- Qualified effective ray mode publishes `Production` and all manifest identifiers/budgets.
- Unqualified effective ray mode remains effective and publishes `Experimental`.
- Auto CSM temporal publishes its gate result and Production/Experimental level exactly as today.
- Developer-forced CSM temporal publishes `Developer` with the synthetic force detail.
- Baseline CSM and ray fallback publish `Production` with the exact baseline/fallback synthetic rejection detail.
- Auto requested but inactive preserves the current CSM qualification failure-detail selection, including an empty detail after resource-allocation omission.

### Budget state machine

- Failed qualification or absent GPU/memory budget resets the streak when the policy is invoked.
- Non-Production or different-mode completed diagnostics reset the streak after cooldown evaluation.
- Equal-to-budget measurements are not overruns.
- Under-budget observations decrement, but never below zero.
- Three consecutive over-budget completed observations demote on the third and produce exact GPU/memory detail.
- Cooldown lasts while `frameSerial < demotionUntilFrame` and expires exactly at the boundary.
- Cooldown is shared across qualified ray and CSM-temporal tracks.
- Frames that do not invoke qualified budget policy do not proactively reset state.
- Checked timing/memory sums and checked `frameSerial + 120` behavior are preserved.

### Renderer/resource integration

- CSM temporal qualification is not evaluated outside `Cascaded + Auto`.
- Ray qualification is not evaluated when candidate resolution falls back to CSM.
- History and ray-mask allocation inputs retain extent and detailed-diagnostic flags.
- CSM temporal allocation exceptions preserve `DirectionalShadowHistoryResetReason.ResourceRecreated` and diagnostic logging.
- Final plans without screen history invalidate history and report zero history bytes.
- Plans using history publish current generation/estimated bytes before passes execute.
- GPU parameters are built from final effective mode/qualification and uploaded after plan assignment.
- Pass-time history reset `with` updates and diagnostics publication remain unchanged.

### Compatibility and runtime matrix

Exercise at minimum:

| Case | Required evidence |
| --- | --- |
| Shadows disabled / no sun | Stable CSM fallback reason and no ray/history work. |
| Cascaded baseline | Production baseline plan and expected CSM consumers. |
| CSM temporal Disabled/Auto/DeveloperForce | Exact admission, qualification, allocation, and labeling behavior. |
| HybridContact | CSM plus contact-ray consumer and receiver policy. |
| RayQueryHard | Qualified/unqualified labels, transparent/decal policy, CSM fallback demand. |
| RayQuerySoft, finite sun | History allocation, soft consumers, temporal/spatial pass admission. |
| RayQuerySoft, zero sun radius | Deterministic hard conversion and no soft history. |
| Ray scene incomplete/stale/bounds invalid | Immediate diagnosed CSM fallback. |
| Ray/history allocation failure | Concrete failure reason and universal CSM behavior. |
| Three budget overruns | Third-frame demotion and 120-frame shared cooldown. |
| Resize/camera cut/light/mode/resource-generation change | Existing pass-time history reset reasons. |
| Transparent/decal/debug fallback demand | Correct map retention and receiver policies. |

## 9. Verification commands

Run focused policy and shadow tests:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~ShadowFramePlannerTests|FullyQualifiedName~DirectionalShadowContractsTests|FullyQualifiedName~DirectionalShadowQualificationManifestTests|FullyQualifiedName~DirectionalShadowDataBuilderTests|FullyQualifiedName~DirectionalShadowCacheStateTrackerTests|FullyQualifiedName~DirectionalRayShadowShaderContractTests|FullyQualifiedName~ProductionRenderPipelineDeclarationTests"
```

Run the full test project and solution build:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore
dotnet build Njulf.sln --no-restore
```

Audit ownership and semantic usages:

```powershell
rg -n "ResolveDirectionalShadowFramePlan|IsQualifiedDirectionalShadowBudgetDemoted|_directionalShadowQualifiedBudgetOverrunStreak|_directionalShadowBudgetDemotionUntilFrame" Njulf.Rendering/VulkanRenderer.cs Njulf.Tests
rg -n "ShadowFramePlanner|PrepareDirectionalShadowFrame" Njulf.Rendering Njulf.Tests
rg -n "DirectionalShadowFramePlan" Njulf.Rendering/Pipeline Njulf.Rendering/Resources Njulf.Rendering/VulkanRenderer.cs
```

The first search should return no old owner references. Review the other results to confirm the planner integration is narrow and all downstream plan consumers remain intact.

For runtime validation, use the established directional-shadow capture scenarios with Vulkan validation enabled. Record requested/effective mode, fallback reason/detail, qualification level/id/track, receiver policies, history consumers/reset/bytes, ray-scene generation/epoch, per-pass GPU timings, and memory for every matrix row.

## 10. Risks and mitigations

### Risk: qualification is evaluated for unavailable modes

Use the two-stage API. Resolve concrete capability/resource fallback first, then evaluate ray qualification only for a remaining non-CSM candidate.

### Risk: policy and resource effects are reordered

Keep resource ensures in the renderer and characterize the exact sequence. The planner consumes outcomes; it never initiates allocation or invalidation.

### Risk: explicit experimental ray modes become incorrectly gated

Test failed qualification with otherwise-ready hard and soft candidates. The effective ray mode must remain active and publish `Experimental`.

### Risk: budget feedback uses current incomplete data

Pass `_lastDiagnostics.DirectionalShadowRuntime` explicitly. Never build the input from current `sceneData` timings before render execution.

### Risk: shared cooldown is accidentally split or reset

Keep one streak/cooldown pair in the planner and no public reset API. Add cross-track and inactive-frame lifecycle tests.

### Risk: history allocation follows requested rather than final mode

Preserve current pre-resolution resource request, then derive `HistoryConsumers` and history-byte/invalidation behavior from the final post-demotion plan.

### Risk: fallback reason precedence changes

Use a typed candidate and one method with the current override order. Add multiple-simultaneous-failure tests.

### Risk: receiver policy boolean precedence changes

Move the current expressions mechanically first and cover every mode/transparent/decal/debug combination before simplifying parentheses or names.

### Risk: planner retains mutable per-frame objects

Use synchronous `in` contracts and retain only the two scalar budget fields. Review the class for stored settings, diagnostics, readiness, or strings.

### Risk: later pass mutations are mistaken for planner ownership

Keep history revision/reset resolution in ray/temporal passes and `DirectionalShadowHistoryResources`. Test that post-pass `HistoryResetReason` reaches diagnostics.

### Risk: other extraction plans create conflicting ownership

Keep qualification identities outside the planner and previous diagnostics as a typed input. This makes the planner independent of both the capture provider and diagnostics assembler implementation order.

### Risk: unrelated working-tree changes are overwritten

Use semantic usage searches and narrow patches. Never replace, regenerate, or revert the full dirty renderer file.

## 11. Definition of done

The extraction is complete only when all of the following are true:

- `ShadowFramePlanner` exists under `Njulf.Rendering/Resources` and has no dependency on `VulkanRenderer` or Vulkan/resource objects.
- The planner owns candidate resolution, renderer-specific fallback overrides, budget hysteresis, qualification labeling, receiver/history policy, and final plan construction.
- The two budget fields and old budget helper are gone from `VulkanRenderer`.
- `ResolveDirectionalShadowFramePlan` is replaced by a narrow, honestly named `PrepareDirectionalShadowFrame` effect coordinator.
- CSM and ray qualification calls remain conditional at their exact current decision boundaries.
- Resource ensure, exception/logging, history invalidation, GPU parameter, and upload behavior remain with current owners.
- Every requested/effective/fallback/qualification/receiver/history field in `DirectionalShadowFramePlan` matches the baseline for the test matrix.
- Budget demotion retains the shared three-frame threshold, strict comparisons, exact detail, and 120-frame cooldown.
- Later ray/temporal pass updates to `HistoryResetReason` and diagnostics publication are unchanged.
- No local-shadow, settings-schema, diagnostics-schema, capture-schema, shader, pass, or render-graph behavior changes are included.
- Focused tests, the full test project, the solution build, and runtime validation all pass.
- No new steady-frame allocation or device/resource query is introduced by the planner boundary.

## 12. Follow-up work explicitly deferred

After the behavior-preserving extraction is stable, separate work may consider:

- extracting qualification-context construction from `VulkanRenderer` after `PerformanceCaptureMetadataProvider` lands;
- caching immutable physical-device qualification identity instead of querying it during conditional evaluation;
- introducing an immutable shadow-settings snapshot if settings become concurrently mutable;
- separating the budget state machine into a reusable qualification-budget tracker if another feature adopts identical semantics;
- creating a resource-preparation coordinator if the remaining renderer method is still too large;
- unifying directional and local shadow planning only if local shadows gain an authoritative frame-plan contract;
- revisiting synthetic CSM qualification detail wording through an explicit schema/evidence migration.

None of these follow-ups should be mixed into the initial extraction.
