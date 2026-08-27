# PerformanceCaptureMetadataProvider Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor.

## 1. Required outcome

Extract performance-capture provenance, scene/camera tracking, canonical hashing, and metadata composition from `Njulf.Rendering/VulkanRenderer.cs` into a focused `PerformanceCaptureMetadataProvider` under `Njulf.Rendering/Diagnostics`.

The completed refactor must:

- Remove the capture-only helper implementations currently spread across `VulkanRenderer`, including approximately 523 lines at 9834-9923 and 10025-10457 in the audited working tree.
- Move the six renderer-owned capture state fields into the provider:
  - shader-bundle hash;
  - executable-bundle hash;
  - dirty-worktree state;
  - observed scene revision;
  - scene-load frame serial;
  - camera-cut serial.
- Preserve `VulkanRenderer.CaptureSceneKind` and `VulkanRenderer.CaptureScenario` as source-compatible public properties, implemented as delegations to the provider.
- Supply one coherent capture identity to renderer diagnostics, performance snapshots, advanced-GI admission, directional-shadow qualification, GI pipeline-cache identity, and Simple-DDGI warm-start identity.
- Preserve every existing hash algorithm, canonical field order, normalization rule, placeholder/failure string, initialization boundary, scene/camera serial rule, and persisted metadata shape.
- Keep filesystem, assembly reflection, executable hashing, shader hashing, environment inspection, and `git status` work out of the per-frame path.
- Introduce no Vulkan dependency and perform no rendering or resource-lifetime work.

The desired ownership and data flow is:

```text
application/shader assemblies + host filesystem/process state
                         |
                         v
      PerformanceCaptureHostIdentityResolver
                         |
                         v
          cached build/provenance identity
                         |
scene + camera + frame --> PerformanceCaptureMetadataProvider
                         |
                         v
          PerformanceCaptureIdentitySnapshot
                  /                 \
                 v                   v
 RendererDiagnosticsAssembler   qualification/cache consumers
```

`PerformanceCaptureMetadataProvider` must not depend on or call back into `VulkanRenderer`. The renderer remains the composition root and decides when provider lifecycle methods are called.

## 2. Audited starting point

### 2.1 Capture-only code inside `VulkanRenderer`

The current renderer contains these capture responsibilities:

- Camera pitch extraction and view/projection matrix hashing.
- Stable scene-state and authored scene-asset hashing.
- Build-configuration and target-framework reporting.
- Application-version and source-revision discovery from assembly attributes.
- Executable identity as a manifest hash of the process apphost plus application-local `Njulf*.dll` files.
- Dirty-worktree discovery from an explicit value, `NJULF_DIRTY_WORKTREE_STATE`, or a bounded `git status` process.
- Effective shader-bundle hashing using on-disk shader candidates before embedded resources.
- Scene/scenario normalization and unavailable-reason generation.
- Per-scene frame-age and per-camera-cut serial tracking.
- Construction of `PerformanceCaptureRunMetadata`, `PerformanceCaptureCameraMetadata`, and `PerformanceCaptureFrameMetadata` inside `BuildDiagnostics`.

The capture-only methods occupy two principal regions:

- Lines 9834-9923: camera metadata, matrix hash, scene-state hash, and scene-asset hash.
- Lines 10025-10457: build/application/commit, executable/worktree/shader identity, normalization, hashing support, and compile-configuration selection.

The intervening `UpdateAdvancedGiRuntimeContentState`, `HashTextEquals`, and `AttributeSimpleDdgiTransportRingTimings` methods are not provider-owned simply because they are adjacent in the file. Advanced-GI content admission remains with its policy owner; DDGI timing attribution remains outside this extraction.

### 2.2 Initialization-time identity

Capture identity is part of renderer admission, not merely export decoration:

1. At the start of `Initialize`, the renderer resolves `_captureShaderBundleHash` before selecting optional backends and immutable graph variants.
2. The shader hash is supplied to `GiPipelineCacheService` before GI pipelines are created.
3. After `CreatePipelines`, the renderer resolves `_captureExecutableHash` and `_captureDirtyWorktreeState`.
4. The same shader/commit/worktree identity is later used by advanced-GI candidate authorization, advanced-GI qualification, C5 runtime-evidence selection, directional-shadow qualification, and persistent Simple-DDGI warm-start identity.

Moving shader resolution later would change fail-closed admission. Performing executable/worktree discovery every frame would add avoidable I/O and process work. Preserve the two existing initialization boundaries.

### 2.3 Per-frame scene and camera tracking

At the beginning of `DrawScene` frame preparation, the renderer currently:

1. Writes a fallback-normalized scene name and the raw configured scenario into `SceneRenderingData`.
2. Updates advanced-GI runtime content matching using the normalized scenario and scene-asset hash.
3. Detects a changed `SceneContentRevision`.
4. On change, records the scene-load frame serial, resets camera-cut serial to zero, and resets the stateful GI warning evaluator.
5. Writes camera yaw, pitch, field of view, near/far planes, and frames-since-scene-load.
6. Later, after Hi-Z camera-cut detection, increments the camera-cut serial when required and writes it to `SceneRenderingData`.

The final view/projection hashes and capture records are built much later, when renderer diagnostics are assembled. The provider must not retain `SceneRenderingData` or the camera between those call sites.

### 2.4 Current identity consumers

The extracted provider must replace all current reads of the capture fields/helpers at these boundaries:

- `LightingVersions`: publishes the observed scene revision.
- `TryAuthorizeAdvancedGiCandidate`: commit and shader-bundle identity.
- `Initialize`: startup shader identity, GI pipeline-cache identity, executable identity, and worktree state.
- `TrySelectNearFieldRuntimeEvidence`: shader-set identity.
- `EvaluateAdvancedGiQualification`: shader and commit identity.
- Directional-shadow qualification: shader, commit, and dirty-worktree identity.
- `UpdateAdvancedGiRuntimeContentState`: normalized scenario and scene-asset hash.
- Renderer diagnostics/assembler input: run, camera, frame, scene-asset, and scene-state identity.
- `SimpleDdgiWarmStartIdentityBuilder`: shader-bundle identity.

This shared use is why the provider must expose a stable typed identity rather than returning unrelated strings from ad hoc static methods.

### 2.5 Existing persisted contracts

The public data records live in `Njulf.Rendering/Diagnostics/GiDiagnosticsContracts.cs`:

- `PerformanceCaptureRunMetadata`
- `PerformanceCaptureCameraMetadata`
- `PerformanceCaptureFrameMetadata`

`PerformanceSnapshotWriter` wraps these in the existing public `PerformanceCaptureMetadata` record and normalizes missing values during export. The new provider does not replace or rename that public writer record.

Benchmark, evidence, qualification, and comparison code relies on exact values for scene asset/state hashes, camera hashes and cut serial, executable hash, commit, worktree state, shader-bundle hash, build configuration, settings schema, and DDGI frame state.

### 2.6 Existing tests and gaps

`FirstPersonCameraTests` currently verifies only:

- capture pitch round-tripping;
- matrix hash format and determinism.

`PerformanceSnapshotWriterTests` verifies persisted diagnostics/snapshot behavior, and benchmark tests compare capture identities after they have been populated. There is no focused coverage for:

- scene-asset versus scene-state inclusion rules;
- source-revision parsing;
- scene/scenario normalization;
- executable manifest hashing;
- shader-bundle framing and ordering;
- worktree override, repository lookup, process failure, and timeout behavior;
- scene-load/camera-cut lifecycle;
- initialization-time caching and consumer consistency.

The extraction must add that characterization coverage before deleting the renderer implementations.

## 3. Scope

### In scope

- A renderer-lifetime `PerformanceCaptureMetadataProvider`.
- A startup-only host/build identity resolver separated from per-frame metadata composition.
- Pure canonical hashing helpers owned by the capture metadata subsystem.
- Typed build identity, frame preparation, and final identity snapshot contracts.
- Moving capture configuration and serial state out of `VulkanRenderer`.
- Delegating existing `CaptureSceneKind` and `CaptureScenario` properties.
- Replacing every capture identity consumer with the provider's typed properties/snapshots.
- Feeding `PerformanceCaptureIdentitySnapshot` into `RendererDiagnosticsAssembler` or, until that extraction lands, into the existing diagnostics builder.
- Focused unit tests for algorithms, lifecycle, failure semantics, and wiring.
- Full compatibility checks for performance-snapshot and benchmark identity contracts.

### Out of scope

- Changing any public performance-capture record or JSON schema.
- Changing the paired-capture identity built by `PerformanceSnapshotWriter`.
- Adding new provenance fields, cryptographic signing, timestamps, machine identity, branch names, or remote repository information.
- Changing advanced-GI, directional-shadow, pipeline-cache, or warm-start admission policies.
- Changing which scene fields participate in the asset/state hashes.
- Replacing the current string-based float canonicalization with binary matrix hashing.
- Correcting or broadening the existing build-configuration symbol mapping. In particular, do not silently relabel `Development` as part of this move.
- Changing shader discovery precedence, shader candidate paths, embedded resource naming, or executable manifest membership.
- Running `git`, enumerating binaries, reflecting assemblies, or hashing files on every frame.
- Making `PerformanceSnapshotWriter` depend directly on the provider. It remains a serializer of completed diagnostics.
- Moving `UpdateAdvancedGiRuntimeContentState` into the provider. The provider supplies normalized identity; GI policy owns the admission result.
- Moving unrelated adjacent DDGI timing helpers.
- Using `partial VulkanRenderer` as the final extraction.

## 4. Non-negotiable invariants

1. Shader-bundle identity is resolved at the current early `Initialize` boundary, before optional-backend/graph admission and GI pipeline-cache construction.
2. Executable-bundle and dirty-worktree identity are resolved at the current post-`CreatePipelines` boundary.
3. A failed renderer initialization may be retried. Identity resolution must run for each new initialization attempt until initialization succeeds; provider caching must not permanently freeze a failed/partial attempt.
4. No host I/O, process launch, assembly scan, or shader/file hashing occurs in steady-state frame metadata creation.
5. Build/application/commit/shader/executable/worktree values used by diagnostics and qualification in one initialized renderer instance are identical.
6. Before their initialization boundary, provider properties expose the exact current `unavailable:*not-initialized` placeholders.
7. `CaptureSceneKind` falls back to `Scene.Name`; `CaptureScenario` never infers a value from scene content or camera pose.
8. Property setters retain the current raw-value behavior. Normalization happens when labels/metadata are produced, not opportunistically in the setter.
9. A nonblank `Scene.Name` is copied verbatim into `SceneRenderingData`; a null/empty/whitespace name becomes exactly `unknown-scene`. Do not trim a nonblank name before scene hashing.
10. A scene revision change records the current frame as load frame zero and resets camera-cut serial before that frame's Hi-Z camera-cut result is applied.
11. If the same frame is both a scene change and a detected camera cut, its published camera-cut serial follows the current sequence: reset to zero, then increment to one.
12. Frames-since-scene-load uses the current guarded subtraction and never underflows if frame serial moves backward.
13. Camera yaw/pitch sign conventions, FOV/near/far values, view/projection matrices, and cut serial remain exact.
14. Scene asset hash continues to describe authored/base inputs; scene state hash continues to include emitted draw/shadow state. Do not merge them or reuse one for both.
15. All SHA-256 identities retain the lowercase `sha256:` prefix and exact canonical byte framing.
16. All existing unavailable/failure strings remain byte-for-byte unchanged because qualification and benchmark diagnostics surface them.
17. Worktree probing remains bounded to 64 parent directories and a 2,000 ms `git status` timeout, with process-tree termination on timeout.
18. The provider does not retain per-frame camera, scene, `SceneRenderingData`, mutable lists, streams, or byte buffers.
19. The provider performs no Vulkan calls and has no dependency on Silk.NET, command buffers, render passes, or resource managers.
20. The provider never calls `RendererDiagnosticsAssembler`; the renderer coordinates the provider's `SceneChanged` result with the assembler's warning-history reset.
21. Existing public capture records, `RendererDiagnostics` fields, and snapshot serialization remain unchanged.

## 5. Chosen architecture

### 5.1 Stateful provider

Add an internal sealed provider in `Njulf.Rendering.Diagnostics` with renderer-instance lifetime. The exact names may be refined during implementation, but the ownership should follow this shape:

```csharp
internal sealed class PerformanceCaptureMetadataProvider
{
    private readonly PerformanceCaptureHostIdentityResolver _hostIdentityResolver;

    public string SceneKind { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;

    public PerformanceCaptureBuildIdentity BuildIdentity { get; private set; }
    public ulong ObservedSceneRevision { get; private set; } = ulong.MaxValue;

    public void ResolveStartupIdentity();
    public void ResolvePostPipelineIdentity();

    public void ApplySceneLabels(
        SceneRenderingData sceneData,
        string? sceneName);

    public PerformanceCaptureFramePreparation ObserveSceneAndCamera(
        SceneRenderingData sceneData,
        ICamera camera,
        ulong frameSerial);

    public void ApplyCameraCut(
        SceneRenderingData sceneData,
        bool cameraCut);

    public PerformanceCaptureIdentitySnapshot CreateFrameIdentity(
        SceneRenderingData sceneData,
        RendererValidationMode validationMode,
        int settingsSchemaVersion);
}
```

`ApplySceneLabels` remains before advanced-GI runtime content matching. `ObserveSceneAndCamera` remains after that matching at the current scene-revision/camera field boundary. `ApplyCameraCut` remains immediately after Hi-Z camera-cut detection. This split preserves ordering while removing state and calculations from the renderer.

The provider may write only the existing capture-specific properties on `SceneRenderingData`. It must not mutate rendering counts, revisions, matrices, scheduling data, or feature state.

### 5.2 Build identity contract

Add an immutable internal record:

```csharp
internal sealed record PerformanceCaptureBuildIdentity(
    string ApplicationVersion,
    string Commit,
    string ShaderBundleHash,
    string ExecutableHash,
    string DirtyWorktreeState)
{
    public static PerformanceCaptureBuildIdentity Uninitialized { get; } = ...;
}
```

Build configuration is partly dynamic because it includes the current validation mode. Store/cache only the compile configuration and target-framework portion, then compose the existing string with the validation argument when creating `PerformanceCaptureRunMetadata`.

`ResolveStartupIdentity` resolves and caches:

- application assembly version;
- source revision;
- target framework/build configuration base;
- shader-bundle hash.

`ResolvePostPipelineIdentity` adds/replaces:

- executable-bundle hash;
- dirty-worktree state.

Each renderer `Initialize` attempt may refresh these values. Once initialization succeeds, normal frames only read them.

### 5.3 Final per-frame identity contract

Add an immutable internal record containing exactly the values that diagnostics assembly needs:

```csharp
internal sealed record PerformanceCaptureIdentitySnapshot(
    string SceneAssetHash,
    string SceneStateHash,
    PerformanceCaptureRunMetadata Run,
    PerformanceCaptureCameraMetadata Camera,
    PerformanceCaptureFrameMetadata Frame);
```

`CreateFrameIdentity` constructs this record synchronously from final `SceneRenderingData`, the configured scene kind, current validation mode, settings schema version, and the cached build identity. Preserve the current label timing precisely: scene kind is read at final metadata creation and falls back to `sceneData.CaptureSceneName`, while scenario is normalized from the raw value copied into `sceneData.CaptureScenario` earlier by `ApplySceneLabels`. Do not replace that captured scenario with a second live read of the provider property.

It must preserve the existing frame metadata additions:

- DDGI cache generation;
- Simple-DDGI transport generation;
- transport convergence pending;
- converged probe count;
- pending solver probe count.

Do not make the provider construct `ResolvedGiSettingsMetadata`, `GiMeasurementMetadata`, feature states, or paired-capture identity. Those require completed renderer diagnostics and remain with the diagnostics assembler/writer.

### 5.4 Frame preparation result

Return scene transition explicitly:

```csharp
internal readonly record struct PerformanceCaptureFramePreparation(
    bool SceneChanged,
    ulong ObservedSceneRevision,
    ulong FramesSinceSceneLoad);
```

The renderer uses `SceneChanged` to trigger `_diagnosticsAssembler.ResetSceneHistory()` after the assembler extraction. Before that extraction, it preserves the existing GI warning reset at the same line. The provider does not know about warning evaluators.

### 5.5 Host identity resolver

Keep environment-dependent work separate from the stateful frame provider:

```csharp
internal sealed class PerformanceCaptureHostIdentityResolver
{
    // Assembly/build/source revision.
    // Executable manifest and file hashing.
    // Dirty-worktree resolution and bounded git probe.
    // Effective shader bundle discovery and hashing.
}
```

Construct it with explicit assembly anchors:

- application assembly: `typeof(VulkanRenderer).Assembly`, supplied by `VulkanRenderer` at composition time so the resolver itself has no renderer dependency;
- shader assembly: `typeof(ShaderLibrary).Assembly`.

The explicit application assembly preserves the current assembly metadata source even if the provider is moved to another project later.

Add a small injectable `IPerformanceCaptureGitStatusProbe` (or equivalent process-runner seam) for deterministic exit/timeout/failure tests. Do not introduce a general filesystem/service-locator abstraction. File hashing can be tested through explicit path/search-root overloads and temporary directories.

The production git probe must retain:

- `git -C <root> status --porcelain=v1 --untracked-files=normal`;
- redirected stdout/stderr;
- no shell and no window;
- the 2,000 ms timeout;
- process-tree kill on timeout;
- the exact current failure strings.

### 5.6 Pure canonical hashing

Put pure canonicalization/hash helpers either as internal static methods on the resolver/provider or in a focused `PerformanceCaptureHashing` class. Do not leave them on `VulkanRenderer` solely for existing tests.

Preserve these algorithms exactly:

#### Matrix hash

- Serialize all 16 matrix elements in the current row/field order.
- Use the round-trip `"R"` format and `CultureInfo.InvariantCulture`.
- Join with `|`.
- Hash UTF-8 bytes with SHA-256.

Do not replace this with binary float bytes during extraction; the resulting identity would change.

#### Scene-state hash

Preserve the exact ordered fields:

1. scene content revision;
2. GI transport material revision;
3. DDGI emissive source revision;
4. draw-packet revision;
5. directional-shadow meshlet-draw signature;
6. local-shadow meshlet-draw signature;
7. object count;
8. meshlet count;
9. material count;
10. texture count;
11. light count;
12. directional-light count;
13. local-light count;
14. geometry-decal object count;
15. capture scene name.

#### Scene-asset hash

Preserve the exact ordered authored/base fields:

1. scene content revision;
2. GI transport material revision;
3. DDGI emissive source revision;
4. object count;
5. material count;
6. texture count;
7. light count;
8. directional-light count;
9. local-light count;
10. geometry-decal object count;
11. capture scene name.

Draw-packet, shadow signatures, and meshlet count remain excluded from the asset hash.

#### Executable bundle hash

- When an explicit path is supplied, hash that one file exactly as today.
- Otherwise start with `Environment.ProcessPath`.
- Add top-level `Njulf*.dll` files from the process directory.
- Sort by filename using `OrdinalIgnoreCase`.
- Deduplicate full paths using `OrdinalIgnoreCase`.
- Append `filename:sha256:<lowercase hash>\n` for each entry.
- SHA-256 hash the UTF-8 manifest.
- Keep sequential 64 KiB file reads and `FileShare.Read | FileShare.Delete`.

#### Shader bundle hash

- Enumerate and ordinal-sort manifest resource names.
- Include resources under the exact `Njulf.Shaders.` prefix.
- Resolve on-disk candidates before the embedded stream in the current order.
- Frame each shader filename with its little-endian byte length and UTF-8 bytes.
- Hash each shader stream independently using the pooled 64 KiB buffer.
- Append each fixed-size shader digest to the bundle hash.
- Seed the bundle with `njulf-effective-shader-bundle-v1`.

Do not change the currently searched Debug/Release `net10.0` paths in this refactor, even if a more general target-framework search would be desirable.

### 5.7 Normalization and failure semantics

Keep the existing normalization behavior:

- Trim non-empty values.
- Treat values beginning with `unknown` (case-insensitive) as unavailable.
- Scenario fallback: `unavailable:active-scenario-not-supplied-by-renderer-client`.
- Scene-kind fallback: use configured scene kind, otherwise captured scene name, otherwise `unavailable:scene-kind-not-reported`.
- Shader fallback: `unavailable:shader-bundle-hash-not-reported`.
- Source revision: accept 7-128 hexadecimal characters, strip a recognized `sha` prefix separated by `:`, `-`, or `=`, and lowercase.
- Informational version fallback: use metadata after `+` only when explicit assembly metadata does not yield a valid revision.
- Dirty state: accept only `clean` or `dirty` after trim/lowercase.

Preserve every current `unavailable:*` return string and caught exception category. Do not consolidate distinct reasons into a generic failure during extraction.

### 5.8 Renderer integration

Replace the six capture fields with one provider field:

```csharp
private readonly PerformanceCaptureMetadataProvider
    _performanceCaptureMetadataProvider;
```

Delegate the public configuration properties:

```csharp
public string CaptureSceneKind
{
    get => _performanceCaptureMetadataProvider.SceneKind;
    set => _performanceCaptureMetadataProvider.SceneKind = value;
}

public string CaptureScenario
{
    get => _performanceCaptureMetadataProvider.Scenario;
    set => _performanceCaptureMetadataProvider.Scenario = value;
}
```

Do not add setter-time trimming or null replacement; retain current raw assignment semantics.

At initialization:

```csharp
_performanceCaptureMetadataProvider.ResolveStartupIdentity();
// Existing qualification and GI pipeline-cache construction.
RunStartupStep("VulkanRenderer.CreatePipelines", CreatePipelines);
_performanceCaptureMetadataProvider.ResolvePostPipelineIdentity();
```

At frame preparation, retain the current ordering:

```csharp
_performanceCaptureMetadataProvider.ApplySceneLabels(sceneData, scene.Name);
UpdateAdvancedGiRuntimeContentState(sceneData);

PerformanceCaptureFramePreparation captureFrame =
    _performanceCaptureMetadataProvider.ObserveSceneAndCamera(
        sceneData,
        camera,
        frameSerial);
if (captureFrame.SceneChanged)
    _diagnosticsAssembler.ResetSceneHistory();

// Hi-Z planning remains here.
_performanceCaptureMetadataProvider.ApplyCameraCut(
    sceneData,
    hiZDecision.CameraCut);
```

Until `RendererDiagnosticsAssembler` exists, use the existing GI evaluator reset rather than introducing a temporary assembler dependency.

Every qualification/cache consumer reads the same provider identity:

```csharp
PerformanceCaptureBuildIdentity identity =
    _performanceCaptureMetadataProvider.BuildIdentity;
```

Do not duplicate provider values into new renderer string fields for convenience.

### 5.9 RendererDiagnosticsAssembler coordination

The preferred landing order is:

1. Extract `PerformanceCaptureMetadataProvider` and make the existing `BuildDiagnostics` consume `PerformanceCaptureIdentitySnapshot`.
2. Extract `RendererDiagnosticsAssembler` and place that snapshot inside its `RendererDiagnosticsCaptureInput`.

If the assembler extraction lands first, introduce the provider contract before moving the capture mapping again. The final dependency is always:

```text
VulkanRenderer -> PerformanceCaptureMetadataProvider
VulkanRenderer -> RendererDiagnosticsAssembler
RendererDiagnosticsAssembler -> PerformanceCaptureIdentitySnapshot
```

Neither extracted component calls the other. This avoids lifecycle coupling and lets the renderer coordinate scene-change resets explicitly.

## 6. Delivery order and change isolation

### Phase 0: Freeze current identities

1. Re-audit all capture helper/field call sites immediately before implementation because `VulkanRenderer.cs` has active unrelated changes.
2. Capture known outputs for:
   - identity matrix and two nontrivial matrices;
   - a representative scene-data asset hash;
   - the matching state hash;
   - valid and invalid source revisions;
   - scene/scenario normalization;
   - current build configuration/application version/commit placeholders;
   - one executable test file and one ordered executable manifest;
   - a small named shader bundle;
   - explicit clean/dirty/invalid worktree states.
3. Export one current performance snapshot and record its `Run`, `Camera`, `Frame`, scene asset/state hashes, and paired-capture identity.
4. Record the exact startup order of shader, pipeline-cache, pipeline creation, executable, worktree, and graph initialization.

Exit gate: deterministic expected values and the current lifecycle order are recorded before code moves.

### Phase 1: Extract pure hashing and normalization

1. Add the provider/hash support files under `Njulf.Rendering/Diagnostics`.
2. Move matrix, scene asset/state, file, executable manifest, shader bundle, label, source-revision, and general metadata normalization with no algorithm changes.
3. Keep method bodies and failure strings textually close to the source during this phase.
4. Update `FirstPersonCameraTests` to target the new owner.
5. Add focused hash-vector and inclusion/exclusion tests.
6. Leave temporary renderer calls pointing to the new static owner; do not yet change lifecycle state.

Exit gate: all old and new hash vectors match, and `VulkanRenderer` no longer implements canonical hashing.

### Phase 2: Extract host identity resolution

1. Add `PerformanceCaptureHostIdentityResolver` with explicit application/shader assemblies.
2. Move application version, commit, build configuration, executable bundle, dirty-worktree, repository-root, shader-stream discovery, and shader-bundle resolution.
3. Add the small fakeable git process seam.
4. Test explicit values and all deterministic failure classifications without requiring a developer checkout or ambient environment variables.
5. Add optional integration coverage for a temporary git repository when `git` is available, but keep correctness tests independent of that executable.
6. Confirm all streams/processes are disposed and pooled buffers are returned on success and exceptions.

Exit gate: the resolver reproduces current outputs and no renderer helper performs host/process/file identity work.

### Phase 3: Introduce provider state and initialization lifecycle

1. Add `PerformanceCaptureBuildIdentity` and provider defaults with the exact current not-initialized strings.
2. Construct the provider in `VulkanRenderer` with explicit assembly anchors.
3. Replace the early shader-hash assignment with `ResolveStartupIdentity`.
4. Replace the post-pipeline executable/worktree assignments with `ResolvePostPipelineIdentity`.
5. Change candidate, near-field, advanced-GI, directional-shadow, pipeline-cache, and warm-start consumers to read provider identity.
6. Preserve initialization retry semantics: each renderer initialization attempt refreshes the appropriate identity phase.
7. Remove the three renderer-owned build/provenance strings.

Exit gate: every admission/cache consumer observes the exact same provider identity, and fail-closed unavailable states match the baseline.

### Phase 4: Move scene/camera tracking

1. Delegate `CaptureSceneKind` and `CaptureScenario` to the provider.
2. Move scene-label assignment, revision/load-frame tracking, camera angle/projection parameter capture, guarded age calculation, and camera-cut serial tracking.
3. Return `SceneChanged` and retain the existing GI warning reset at the same boundary.
4. Change `LightingVersions` to read the provider's observed scene revision, preserving zero before the first observation.
5. Remove the three renderer-owned scene/camera serial fields.
6. Add sequence tests for first frame, steady frames, scene change, same-frame camera cut, later cuts, and backward frame serial.

Exit gate: scene age and camera-cut serial sequences exactly match the pre-extraction behavior.

### Phase 5: Compose and wire final frame identity

1. Add `PerformanceCaptureIdentitySnapshot`.
2. Move construction of run, camera, and frame metadata from `BuildDiagnostics` into `CreateFrameIdentity`.
3. Include scene asset/state hashes and all current DDGI frame metadata fields.
4. Call `CreateFrameIdentity` once at the current diagnostics-assembly boundary.
5. Map the snapshot into the existing `RendererDiagnostics` fields without schema changes.
6. Update `UpdateAdvancedGiRuntimeContentState` to use provider normalization/hash methods while leaving admission decisions in place.
7. Do not cache the early advanced-GI scene-asset hash and reuse it for final diagnostics until tests prove every participating source field is immutable between those two current call sites. Initially compute at both existing semantic boundaries.

Exit gate: diagnostics and performance snapshots contain identical capture identity values to the baseline.

### Phase 6: Remove renderer remnants and verify

1. Delete all moved methods and unused capture-specific `using` directives from `VulkanRenderer`.
2. Verify the unrelated adjacent advanced-GI content and DDGI timing methods remain intact.
3. Run focused and full tests.
4. Compare performance-snapshot schemas and stable identity fields.
5. Exercise initialization, scene reload, camera cut, resize, qualification, pipeline-cache, and warm-start runtime paths.
6. Confirm steady frames perform no host identity I/O and do not launch `git`.

Exit gate: all definition-of-done conditions are satisfied with no capture or qualification identity drift.

## 7. File-level implementation map

### New files

`Njulf.Rendering/Diagnostics/PerformanceCaptureMetadataProvider.cs`

- Renderer-lifetime scene/camera/configuration state.
- Two-phase initialization identity caching.
- Capture-specific `SceneRenderingData` preparation.
- Final `PerformanceCaptureIdentitySnapshot` composition.

`Njulf.Rendering/Diagnostics/PerformanceCaptureHostIdentityResolver.cs`

- Application assembly version/commit/build configuration.
- Executable bundle hashing.
- Dirty-worktree/repository/process handling.
- Shader stream discovery and bundle hashing.
- Production git-status probe.

`Njulf.Rendering/Diagnostics/PerformanceCaptureIdentityContracts.cs`

- `PerformanceCaptureBuildIdentity`.
- `PerformanceCaptureFramePreparation`.
- `PerformanceCaptureIdentitySnapshot`.
- Small internal host-process result contracts if needed.

`Njulf.Rendering/Diagnostics/PerformanceCaptureHashing.cs` (optional separate file)

- Pure canonical hash functions if keeping them on the resolver/provider would make either class unfocused.
- No host state or renderer dependencies.

`Njulf.Tests/PerformanceCaptureMetadataProviderTests.cs`

- Provider state/lifecycle and metadata composition.

`Njulf.Tests/PerformanceCaptureHostIdentityResolverTests.cs`

- Version/commit normalization, executable/shader identity, worktree behavior, failure reasons, and deterministic fake process results.

`Njulf.Tests/PerformanceCaptureHashingTests.cs` (if hashing is separated)

- Known vectors and scene/matrix inclusion rules.

### Modified files

`Njulf.Rendering/VulkanRenderer.cs`

- Replace six fields with one provider.
- Delegate two public configuration properties.
- Wire two-phase initialization, frame preparation, camera-cut observation, final identity creation, and downstream identity consumers.
- Remove moved capture helper implementations.

`Njulf.Tests/FirstPersonCameraTests.cs`

- Point pitch/hash tests at the provider/hash owner rather than `VulkanRenderer`.

`Njulf.Rendering/Diagnostics/RendererDiagnosticsAssembler.cs` or the current `VulkanRenderer.BuildDiagnostics`

- Consume `PerformanceCaptureIdentitySnapshot` rather than constructing capture metadata itself.

### Schema-stable files

`Njulf.Rendering/Diagnostics/GiDiagnosticsContracts.cs`

- No changes to the three public performance-capture metadata records.

`Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`

- No capture schema, normalization, paired-identity, or serialization changes.

`Njulf.Rendering/Data/RendererDiagnostics.cs`

- No field/default/serialization changes.

## 8. Test matrix

### Canonical hashing tests

- Matrix identity is deterministic, lowercase SHA-256, invariant-culture, and sensitive to each matrix component.
- Matrix hashing produces the same result under a non-English current culture.
- Scene asset hash changes for every included authored field.
- Scene asset hash does not change for draw-packet revision, shadow signatures, or meshlet-count-only changes.
- Scene state hash changes for draw-packet revision, directional/local shadow signatures, meshlet count, and every other included field.
- Asset and state hashes use the fallback scene name exactly as before.
- Executable single-file hash matches a known SHA-256 vector.
- Executable manifest hash is deterministic under input enumeration order, filename-sorted, case-insensitive for path deduplication, and sensitive to filenames/content.
- Shader bundle hash is deterministic under resource enumeration order and sensitive to shader name/content.
- Shader filename framing prevents ambiguous concatenations.
- On-disk shader candidate precedence over embedded content remains unchanged.

### Normalization and build identity tests

- Empty, whitespace, and `unknown*` metadata produce the exact current unavailable reasons.
- Configured scene kind wins over scene name; scene name is the fallback; both missing yield the exact reason.
- Missing scenario yields the exact no-inference reason.
- Source revisions accept valid 7-128 digit hex and the supported `sha` prefix forms.
- Invalid length/non-hex revisions fail to the exact source-revision reason.
- Explicit assembly metadata wins over informational-version `+` metadata.
- Build configuration includes validation and target-framework text in the current format.
- Every supported compile configuration retains its current label, including the current fallback behavior for `Development`.
- Provider properties expose exact not-initialized placeholders before each phase.

### Executable/worktree/shader failure tests

- Missing process path returns `unavailable:process-path-not-reported`.
- Empty bundle returns `unavailable:executable-bundle-empty`.
- File/access/argument/crypto failures return `unavailable:executable-hash-failed`.
- Explicit clean/dirty values and environment override normalize correctly.
- Invalid explicit dirty state returns `unavailable:invalid-dirty-worktree-state` without launching git.
- Missing repository returns `unavailable:git-worktree-not-found`.
- Null process, nonzero exit, launch exception, and timeout return their exact existing reasons.
- Repository search recognizes both `.git` directories and worktree `.git` files and stops after 64 levels.
- Missing shader stream/resources and each caught failure category return the exact current reason.
- Pooled shader buffers and streams are released on failure.

### Provider lifecycle tests

- First observed scene is frame age zero and reports `SceneChanged`.
- Same revision advances frame age from the stored load serial.
- New revision resets age and cut serial.
- A camera cut on the scene-change frame publishes serial one.
- Repeated cuts increment once per reported cut; non-cut frames retain the value.
- Backward frame serial produces zero age rather than underflow.
- Camera yaw/pitch/FOV/near/far and matrix hashes match the old helpers.
- Raw scene/scenario setters are not normalized until metadata creation.
- Scenario uses the raw value captured at `ApplySceneLabels`, while scene kind retains the current end-of-frame property read/fallback behavior.
- Provider does not retain or mutate non-capture fields on `SceneRenderingData`.
- A new initialization attempt refreshes identity; steady frame creation performs no resolver calls.

### Consumer integration tests

- Candidate authorization receives provider commit/shader values.
- Advanced-GI qualification receives the same values.
- Directional-shadow qualification receives provider shader/commit/worktree values.
- GI pipeline cache and Simple-DDGI warm-start receive the same shader identity.
- Advanced-GI runtime content uses the provider's normalized scenario and scene-asset hash but retains its existing match/mismatch reasons.
- `LightingVersions.SceneRevision` remains zero before first observation and matches the provider afterward.
- Renderer diagnostics maps one `PerformanceCaptureIdentitySnapshot` without recomputing or normalizing fields differently.

### Compatibility tests

- Public capture record property inventories and JSON shapes are unchanged.
- `PerformanceSnapshotWriter.NormalizeCaptureRunMetadata` behavior is unchanged.
- Paired-capture identity for a fixed diagnostics fixture is unchanged.
- Benchmark quality-sequence identity comparisons continue to pass.
- Existing capture evidence and qualification fixtures compile without public API changes.

## 9. Verification commands

Run from the repository root:

```powershell
dotnet build Njulf.Rendering/Njulf.Rendering.csproj --no-restore

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~PerformanceCaptureMetadataProviderTests|FullyQualifiedName~PerformanceCaptureHostIdentityResolverTests|FullyQualifiedName~PerformanceCaptureHashingTests|FullyQualifiedName~FirstPersonCameraTests|FullyQualifiedName~PerformanceSnapshotWriterTests|FullyQualifiedName~SampleBenchmarkQualitySequenceTests|FullyQualifiedName~AdvancedGiQualificationManifestTests"

dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore

dotnet build Njulf.sln --no-restore
```

Structural checks:

```powershell
rg -n "_captureShaderBundleHash|_captureExecutableHash|_captureDirtyWorktreeState|_captureSceneRevision|_captureSceneLoadFrameSerial|_captureCameraCutSerial" Njulf.Rendering/VulkanRenderer.cs

rg -n "ResolvePerformanceCapture|ComputePerformanceCapture|HashPerformanceCaptureFile|FindCaptureGitRepositoryRoot|OpenEffectiveCaptureShaderStream|AppendCaptureHash" Njulf.Rendering/VulkanRenderer.cs

rg -n "VulkanRenderer|Silk.NET|CommandBuffer|CreatePipelines|RenderGraph" Njulf.Rendering/Diagnostics/PerformanceCaptureMetadataProvider.cs Njulf.Rendering/Diagnostics/PerformanceCaptureHostIdentityResolver.cs

rg -n "ProcessStartInfo|Directory.GetFiles|GetManifestResourceNames|SHA256.HashData\(stream\)" Njulf.Rendering/VulkanRenderer.cs
```

Expected final structural result:

- None of the six old capture fields remains on `VulkanRenderer`.
- No moved capture hash/host-resolution implementation remains on `VulkanRenderer`.
- The renderer contains only provider construction, property delegation, lifecycle calls, and typed identity consumption.
- The provider/resolver contains no Vulkan/render-graph dependency and no backreference to the renderer.
- Host identity resolution is reachable only from initialization lifecycle methods, not `DrawScene` or diagnostics assembly.

## 10. Risks and mitigations

### Risk: qualification identity changes during a mechanical move

Mitigation: freeze known hash vectors first; preserve canonical field/resource order and unavailable strings; feed all admission/cache consumers from one cached `PerformanceCaptureBuildIdentity`; compare accepted/rejected qualification fixtures before and after.

### Risk: application assembly metadata is read from the wrong assembly

Mitigation: inject `typeof(VulkanRenderer).Assembly` from the composition root rather than resolving metadata from the provider's declaring type implicitly.

### Risk: shader hash no longer describes the same effective bytes

Mitigation: preserve resource prefix, ordinal ordering, candidate search order, disk-before-embedded precedence, framing, per-stream digest, and seed. Do not improve path discovery in the extraction commit.

### Risk: executable identity changes because manifest ordering or membership drifts

Mitigation: lock test vectors for apphost plus `Njulf*.dll`, filename sorting, full-path deduplication, newline framing, and explicit-path behavior.

### Risk: host I/O leaks into frame assembly

Mitigation: split host resolution from frame composition, cache successful initialization-attempt results, instrument the fake resolver call count, and assert `CreateFrameIdentity` invokes no host resolver method.

### Risk: scene age or camera-cut serial becomes off by one

Mitigation: preserve the three current call boundaries (labels, scene/camera observation, cut application) and test full multi-frame sequences including scene-change-plus-cut.

### Risk: early and final scene-asset hashes are incorrectly collapsed

Mitigation: continue computing at both existing semantic boundaries until it is proven that every included field is immutable between them. Do not introduce a per-frame hash cache as part of extraction.

### Risk: provider configuration changes setter behavior

Mitigation: delegate raw values without trimming/defaulting in the setter; keep normalization in metadata creation exactly where it occurs today.

### Risk: git probing hangs or leaves a process alive

Mitigation: retain asynchronous stdout/stderr draining, bounded wait, process-tree kill, disposal, and deterministic timeout tests through the process seam.

### Risk: initialization retry freezes partial identity

Mitigation: refresh identity on each renderer initialization attempt; only steady frames treat the most recent initialized identity as immutable.

### Risk: provider and diagnostics-assembler branches move the same code twice

Mitigation: land provider contracts/hash ownership first, then make the assembler consume `PerformanceCaptureIdentitySnapshot`. Do not independently copy the old metadata helpers into both new classes.

### Risk: unrelated current renderer changes are overwritten

Mitigation: implement against the current working tree, move only audited capture members/call sites, avoid whole-file formatting, and inspect the final diff for edits outside the planned regions.

## 11. Definition of done

The extraction is complete when all of the following are true:

1. `PerformanceCaptureMetadataProvider` owns capture configuration, build identity, scene/load tracking, camera-cut tracking, and final run/camera/frame identity composition.
2. `PerformanceCaptureHostIdentityResolver` owns all assembly/filesystem/process/shader/executable/worktree resolution.
3. `VulkanRenderer` no longer contains the six capture state fields or the moved capture helper implementations.
4. Public `CaptureSceneKind` and `CaptureScenario` behavior is source-compatible through provider delegation.
5. Shader identity is available at the original early admission boundary; executable/worktree identity is resolved at the original post-pipeline boundary.
6. Advanced-GI candidate/qualification/content, near-field evidence, directional shadows, GI pipeline cache, warm start, diagnostics, and performance snapshots all consume the same provider identity.
7. Scene revision, frames-since-load, camera values, and camera-cut serial match the original multi-frame behavior exactly.
8. Hash algorithms, canonical ordering, prefixes, normalization, failure reasons, resource/file precedence, and timeout behavior match locked test vectors.
9. No steady-state frame path performs host identity I/O, launches git, enumerates binaries/resources, or reflects assembly metadata.
10. Public capture/diagnostics records and persisted performance-snapshot schema are unchanged.
11. Focused tests, the full `Njulf.Tests` suite, and the solution build pass.
12. Representative capture, qualification, pipeline-cache, warm-start, scene reload, and camera-cut runtime paths show no identity drift or validation regression.
13. `VulkanRenderer.cs` loses at least the approximately 523 capture-only helper lines, in addition to the removed state and metadata-construction code, without replacing them with a partial-class split.

## 12. Follow-up work explicitly deferred

After this extraction is stable, use separate changes if needed to:

- Generalize shader candidate discovery beyond the current Debug/Release `net10.0` paths.
- Decide whether `Development` should receive an explicit build-configuration label.
- Cache scene hashes after proving all contributing fields are immutable across their current call boundaries.
- Move advanced-GI runtime content matching to a dedicated admission coordinator.
- Add signed build provenance or CI-produced identity manifests.
- Consolidate provider output with a future nested capture section in `RendererDiagnostics` while preserving serialization compatibility.

None of those changes is required for this ownership extraction.
