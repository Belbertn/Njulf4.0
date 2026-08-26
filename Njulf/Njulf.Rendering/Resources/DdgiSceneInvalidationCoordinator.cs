using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Core.Foliage;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns CPU-side DDGI mutation tracking, ordered dirty coverage, and the
/// scene histories used to decide invalidation. It has no Vulkan dependency.
/// Returned region storage remains valid until the next coordinator call.
/// </summary>
internal sealed class DdgiSceneInvalidationCoordinator : IDisposable
{
    private const ulong HashStart = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;

    public const uint SimpleDdgiDirtyReasonLight = 1u << 0;
    public const uint SimpleDdgiDirtyReasonEmissive = 1u << 1;
    public const uint SimpleDdgiDirtyReasonDynamicGeometry = 1u << 2;

    private readonly MeshManager _meshManager;
    private readonly MaterialManager _materialManager;
    private readonly DdgiMutationJournal _ddgiMutationJournal;
    private readonly DdgiInvalidationTelemetryAccumulator _telemetry = new();
    private readonly List<BoundingBox> _ddgiDirtyBoundsScratch = new();
    private readonly List<DdgiDirtyRegion> _ddgiDirtyRegionScratch = new();
    private readonly List<DdgiDirtyRegion> _ddgiMergedDirtyRegionScratch = new();
    private readonly Dictionary<RenderObject, DdgiTrackedRenderObject>
        _ddgiTrackedRenderObjects = new();
    private readonly List<RenderObject> _ddgiTrackedRenderObjectRemovalScratch = new();
    private readonly Dictionary<Guid, DdgiTrackedSkinnedPose>
        _ddgiTrackedSkinnedPoses = new();
    private readonly HashSet<Guid> _ddgiSeenSkinnedPoseIdentities = new();
    private readonly List<Guid> _ddgiTrackedSkinnedPoseRemovalScratch = new();
    private readonly Dictionary<ParticleEffectInstance, DdgiTrackedVfxProxy>
        _ddgiTrackedVfxProxies = new();
    private readonly List<ParticleEffectInstance>
        _ddgiTrackedVfxProxyRemovalScratch = new();

    private GlobalIlluminationSettings? _activeGi;
    private DdgiFoliageProxyFrame _activeFoliageFrame =
        DdgiFoliageProxyFrame.Empty(0);
    private int _ddgiTrackingFrame;
    private ulong _lastDdgiLightSignature;
    private Light[] _lastDdgiLights = Array.Empty<Light>();
    private bool _hasDdgiDynamicSignature;
    private bool _ddgiMutationOraclePrimed;
    private ulong _lastDdgiFoliageProxyDirtySignature;
    private bool _hasDdgiFoliageProxyDirtySignature;
    private BoundingBox? _lastDdgiFoliageProxyInfluenceBounds;
    private bool _hasSimpleDdgiDirtySignature;
    private ulong _lastSimpleDdgiLightSignature;
    private ulong _lastSimpleDdgiStableLightSignature;
    private ulong _lastSimpleDdgiSourcePolicySignature;
    private ulong _lastSimpleDdgiEnvironmentSignature;
    private ulong _lastSimpleDdgiAtmosphereStepSignature;
    private ulong _lastSimpleDdgiEmissiveSignature;
    private ulong _lastSimpleDdgiDynamicGeometrySignature;
    private bool _hasLastSimpleDdgiSoleDirectionalLight;
    private Light _lastSimpleDdgiSoleDirectionalLight;
    private bool _lastSimpleDdgiHadNoEmissiveSources;
    private WeakReference<Scene>? _simpleDdgiWarmStartIdentityScene;
    private ulong _simpleDdgiWarmStartIdentityMutationSerial;
    private ulong _simpleDdgiWarmStartIdentityContentRevision;
    private uint _simpleDdgiWarmStartIdentityMaterialRevision;
    private ulong _simpleDdgiWarmStartIdentityLightSignature;
    private ulong _simpleDdgiWarmStartIdentityEnvironmentSignature;
    private ulong _simpleDdgiWarmStartIdentityEmissiveSignature;
    private SimpleDdgiWarmStartSceneIdentity? _simpleDdgiWarmStartSceneIdentity;

    public DdgiSceneInvalidationCoordinator(
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager)
    {
        _meshManager = meshManager ??
            throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ??
            throw new ArgumentNullException(nameof(materialManager));
        ArgumentNullException.ThrowIfNull(lightManager);
        _ddgiMutationJournal = new DdgiMutationJournal(
            _materialManager,
            lightManager);
    }

    public DdgiInvalidationFrame CollectFrame(
        in DdgiInvalidationCollectionRequest request)
    {
        _activeGi = request.Settings ??
            throw new ArgumentNullException(nameof(request.Settings));
        _activeFoliageFrame = request.Foliage;
        _telemetry.Reset();
        try
        {
            IReadOnlyList<DdgiDirtyRegion> regions;
            if (request.MutationJournalEnabled)
            {
                regions = CollectDdgiDirtyRegionsFromJournal(
                    request.Scene,
                    request.Lights,
                    _telemetry);
            }
            else
            {
                _ddgiMutationJournal.DetachScene();
                _ddgiMutationOraclePrimed = false;
                regions = CollectDdgiDirtyRegions(
                    request.Scene,
                    request.Lights,
                    _telemetry);
            }

            regions = MergeDdgiFoliageProxyDirtyRegion(regions);
            regions = MergeDdgiSkinnedPoseDirtyRegions(
                request.Scene,
                regions);
            return new DdgiInvalidationFrame(
                regions,
                _telemetry.Capture());
        }
        finally
        {
            _activeGi = null;
            _activeFoliageFrame = DdgiFoliageProxyFrame.Empty(0);
        }
    }

    public void DetachScene()
    {
        _ddgiMutationJournal.DetachScene();
        _ddgiMutationOraclePrimed = false;
    }

    public void ResetDynamicTracking()
    {
        DetachScene();
        ResetDynamicTrackingCore();
    }

    public void Dispose() => _ddgiMutationJournal.Dispose();

    private readonly record struct DdgiTrackedRenderObject(
        ulong GeometrySignature,
        ulong MaterialSignature,
        ulong EmissiveSignature,
        ulong TransformSignature,
        BoundingBox Bounds,
        int LastSeenFrame);

    private readonly record struct DdgiTrackedVfxProxy(
        ulong Signature,
        BoundingBox Bounds,
        int LastSeenFrame);

    private readonly record struct DdgiTrackedSkinnedPose(
        ulong PoseRevision,
        BoundingBox Bounds);

    private IReadOnlyList<DdgiDirtyRegion> CollectDdgiDirtyRegionsFromJournal(
        Scene scene,
        LightFrameSnapshot lightSnapshot,
        DdgiInvalidationTelemetryAccumulator telemetry)
    {
        _ddgiMutationJournal.AttachScene(scene);
        IReadOnlyList<DdgiDirtyRegion> journalRegions =
            _ddgiMutationJournal.Drain(
                EstimateSceneProbeBounds(scene),
                ResolveDdgiMutation);

        int journalVfxDirtyEventCount = 0;
        for (int index = 0; index < journalRegions.Count; index++)
        {
            if (journalRegions[index].Reason == DdgiDirtyReason.EmissiveChanged)
                journalVfxDirtyEventCount++;
        }

        if (_activeGi!
            .SimpleDdgiMutationJournalValidationOracleEnabled)
        {
            IReadOnlyList<DdgiDirtyRegion> referenceRegions =
                CollectDdgiDirtyRegions(scene, lightSnapshot, telemetry);
            if (_ddgiMutationOraclePrimed)
            {
                _ddgiMutationJournal.RecordOracleComparison(
                    HaveEquivalentDdgiDirtyCoverage(
                        journalRegions,
                        referenceRegions));
            }
            else
            {
                _ddgiMutationOraclePrimed = true;
            }
        }
        else
        {
            _ddgiMutationOraclePrimed = false;
        }

        telemetry.VfxDdgiDirtyProbeEventCount =
            journalVfxDirtyEventCount;
        DdgiMutationJournalTelemetry journalTelemetry =
            _ddgiMutationJournal.Telemetry;
        telemetry.SimpleDdgiMutationJournalLastConsumedSerial =
            journalTelemetry.LastConsumedSerial;
        telemetry.SimpleDdgiMutationJournalEnqueuedEventCount =
            journalTelemetry.EnqueuedEventCount;
        telemetry.SimpleDdgiMutationJournalCoalescedEventCount =
            journalTelemetry.CoalescedEventCount;
        telemetry.SimpleDdgiMutationJournalOverflowCount =
            journalTelemetry.OverflowCount;
        telemetry.SimpleDdgiMutationJournalConservativeFallbackCount =
            journalTelemetry.ConservativeFallbackCount;
        telemetry.SimpleDdgiMutationJournalAttachScanCount =
            journalTelemetry.SceneAttachScanCount;
        telemetry.SimpleDdgiMutationJournalAttachObjectCount =
            journalTelemetry.SceneAttachObjectCount;
        telemetry.SimpleDdgiMutationJournalOracleComparisonCount =
            journalTelemetry.OracleComparisonCount;
        telemetry.SimpleDdgiMutationJournalOracleMismatchCount =
            journalTelemetry.OracleMismatchCount;
        telemetry.SimpleDdgiMutationJournalPendingEventCount =
            journalTelemetry.PendingEventCount;
        telemetry.SimpleDdgiMutationJournalOutputRegionCount =
            journalTelemetry.LastOutputRegionCount;
        telemetry.SimpleDdgiMutationJournalOverflowedThisFrame =
            journalTelemetry.OverflowedThisFrame ? 1 : 0;

        return journalRegions;
    }

    private DdgiMutationResolution ResolveDdgiMutation(
        SceneMutation mutation)
    {
        DdgiDirtyReason reason = ResolveDdgiMutationReason(mutation.Kind);
        switch (mutation.Producer)
        {
            case RenderObject renderObject:
                {
                    BoundingBox? currentBounds = mutation.NewWorldBounds;
                    if (!mutation.Kind.HasFlag(SceneMutationKind.Removed) &&
                        TryCreateDdgiTrackedRenderObject(
                            renderObject,
                            out DdgiTrackedRenderObject current))
                    {
                        currentBounds = current.Bounds;
                    }
                    else if ((!renderObject.Enabled || !renderObject.Visible) &&
                             mutation.Kind.HasFlag(SceneMutationKind.Visibility))
                    {
                        currentBounds = null;
                    }

                    return CreateDdgiMutationResolution(
                        mutation.OldWorldBounds,
                        currentBounds,
                        reason,
                        1.0f,
                        ignoreWhenUntracked: true);
                }
            case ParticleEffectInstance particle:
                {
                    if (!HasDdgiSustainedEmissiveVfxDefinition(particle))
                        return DdgiMutationResolution.Ignored;

                    BoundingBox? currentBounds = null;
                    if (!mutation.Kind.HasFlag(SceneMutationKind.Removed) &&
                        TryCreateDdgiTrackedVfxProxy(
                            particle,
                            out DdgiTrackedVfxProxy current))
                    {
                        currentBounds = current.Bounds;
                    }

                    return CreateDdgiMutationResolution(
                        mutation.OldWorldBounds,
                        currentBounds,
                        DdgiDirtyReason.EmissiveChanged,
                        1.0f,
                        ignoreWhenUntracked: true);
                }
            case StaticInstanceBatch batch:
                {
                    BoundingBox? currentBounds = null;
                    if (!mutation.Kind.HasFlag(SceneMutationKind.Removed) &&
                        TryGetStaticInstanceBatchBounds(batch, out BoundingBox bounds))
                    {
                        currentBounds = bounds;
                    }

                    return CreateDdgiMutationResolution(
                        mutation.OldWorldBounds,
                        currentBounds,
                        reason,
                        1.0f,
                        ignoreWhenUntracked: true);
                }
            case FoliagePatch patch:
                {
                    BoundingBox? currentBounds =
                        mutation.Kind.HasFlag(SceneMutationKind.Removed) ||
                        !patch.Visible
                            ? null
                            : patch.Bounds;
                    return CreateDdgiMutationResolution(
                        mutation.OldWorldBounds,
                        currentBounds,
                        reason,
                        1.0f,
                        ignoreWhenUntracked: true);
                }
            default:
                return default;
        }
    }

    private static DdgiMutationResolution CreateDdgiMutationResolution(
        BoundingBox? oldBounds,
        BoundingBox? newBounds,
        DdgiDirtyReason reason,
        float padding,
        bool ignoreWhenUntracked)
    {
        BoundingBox? influenceBounds = null;
        if (oldBounds.HasValue || newBounds.HasValue)
        {
            BoundingBox swept = oldBounds.HasValue && newBounds.HasValue
                ? Union(oldBounds.Value, newBounds.Value)
                : oldBounds ?? newBounds!.Value;
            Vector3 extent = new(MathF.Max(0.0f, padding));
            influenceBounds = new BoundingBox(
                swept.Min - extent,
                swept.Max + extent);
        }

        return new DdgiMutationResolution(
            oldBounds,
            newBounds,
            influenceBounds,
            reason,
            Priority: 1u,
            IgnoreWhenUntracked: ignoreWhenUntracked);
    }

    private static DdgiDirtyReason ResolveDdgiMutationReason(
        SceneMutationKind kind)
    {
        if (kind.HasFlag(SceneMutationKind.Removed))
            return DdgiDirtyReason.GeometryRemoved;
        if (kind.HasFlag(SceneMutationKind.Added))
            return DdgiDirtyReason.GeometryAdded;
        if (kind.HasFlag(SceneMutationKind.Emission))
            return DdgiDirtyReason.EmissiveChanged;
        if (kind.HasFlag(SceneMutationKind.Material))
            return DdgiDirtyReason.MaterialChanged;
        if (kind.HasFlag(SceneMutationKind.ParticleState))
            return DdgiDirtyReason.EmissiveChanged;
        return DdgiDirtyReason.TransformChanged;
    }

    private static bool HasDdgiSustainedEmissiveVfxDefinition(
        ParticleEffectInstance instance)
    {
        IReadOnlyList<ParticleEmitterDefinition> emitters =
            instance.Effect.Emitters;
        for (int index = 0; index < emitters.Count; index++)
        {
            if (IsDdgiSustainedEmissiveVfx(emitters[index]))
                return true;
        }
        IReadOnlyList<BeamDefinition> beams = instance.Effect.Beams;
        for (int index = 0; index < beams.Count; index++)
        {
            if (IsDdgiBeamMacroSource(beams[index]))
                return true;
        }
        return false;
    }

    private bool TryGetStaticInstanceBatchBounds(
        StaticInstanceBatch batch,
        out BoundingBox bounds)
    {
        bounds = default;
        if (!batch.Visible ||
            batch.Mesh is not MeshHandle meshHandle ||
            !meshHandle.IsValid ||
            batch.WorldMatrices.Count == 0)
        {
            return false;
        }

        try
        {
            MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
            var localBounds = new BoundingBox(
                ToCoreVector(meshInfo.BoundingBoxMin),
                ToCoreVector(meshInfo.BoundingBoxMax));
            bool hasBounds = false;
            foreach (Matrix4x4 worldMatrix in batch.WorldMatrices)
            {
                BoundingBox instanceBounds =
                    SceneDataBuilder.TransformBoundingBox(
                        localBounds,
                        worldMatrix);
                bounds = hasBounds
                    ? Union(bounds, instanceBounds)
                    : instanceBounds;
                hasBounds = true;
            }
            return hasBounds;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HaveEquivalentDdgiDirtyCoverage(
        IReadOnlyList<DdgiDirtyRegion> left,
        IReadOnlyList<DdgiDirtyRegion> right)
    {
        Span<BoundingBox> leftBounds = stackalloc BoundingBox[5];
        Span<BoundingBox> rightBounds = stackalloc BoundingBox[5];
        Span<bool> leftHasBounds = stackalloc bool[5];
        Span<bool> rightHasBounds = stackalloc bool[5];
        AccumulateDdgiDirtyCoverage(left, leftBounds, leftHasBounds);
        AccumulateDdgiDirtyCoverage(right, rightBounds, rightHasBounds);
        for (int index = 0; index < leftBounds.Length; index++)
        {
            if (leftHasBounds[index] != rightHasBounds[index])
                return false;
            if (leftHasBounds[index] &&
                (!DdgiVectorsApproximatelyEqual(
                     leftBounds[index].Min,
                     rightBounds[index].Min,
                     0.001f) ||
                 !DdgiVectorsApproximatelyEqual(
                     leftBounds[index].Max,
                     rightBounds[index].Max,
                     0.001f)))
            {
                return false;
            }
        }
        return true;
    }

    private static void AccumulateDdgiDirtyCoverage(
        IReadOnlyList<DdgiDirtyRegion> regions,
        Span<BoundingBox> bounds,
        Span<bool> hasBounds)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            DdgiDirtyRegion region = regions[index];
            int reasonClass = region.Reason switch
            {
                DdgiDirtyReason.LocalLightChanged or
                DdgiDirtyReason.DirectionalLightChanged => 1,
                DdgiDirtyReason.EmissiveChanged => 2,
                DdgiDirtyReason.MaterialChanged => 3,
                _ => 4
            };
            bounds[reasonClass] = hasBounds[reasonClass]
                ? Union(bounds[reasonClass], region.InfluenceBounds)
                : region.InfluenceBounds;
            hasBounds[reasonClass] = true;
        }
    }

    private static bool DdgiVectorsApproximatelyEqual(
        Vector3 left,
        Vector3 right,
        float epsilon) =>
        MathF.Abs(left.X - right.X) <= epsilon &&
        MathF.Abs(left.Y - right.Y) <= epsilon &&
        MathF.Abs(left.Z - right.Z) <= epsilon;

    private IReadOnlyList<DdgiDirtyRegion> CollectDdgiDirtyRegions(
        Scene scene,
        LightFrameSnapshot lightSnapshot,
        DdgiInvalidationTelemetryAccumulator telemetry)
    {
        _ddgiDirtyBoundsScratch.Clear();
        _ddgiDirtyRegionScratch.Clear();
        _ddgiMergedDirtyRegionScratch.Clear();
        _lastDdgiFoliageProxyDirtySignature = 0UL;
        _hasDdgiFoliageProxyDirtySignature = false;
        _lastDdgiFoliageProxyInfluenceBounds = null;
        telemetry.VfxDdgiDirtyProbeEventCount = 0;

        if (!_activeGi!.EffectiveUseDdgi)
        {
            ResetDynamicTrackingCore();
            return _ddgiDirtyRegionScratch;
        }

        _ddgiTrackingFrame++;
        ulong lightSignature = CreateDdgiLightSignature(lightSnapshot);
        bool hasPreviousSignature = _hasDdgiDynamicSignature;

        if (hasPreviousSignature)
        {
            if (lightSignature != _lastDdgiLightSignature)
                AddDdgiDirtyRegionsForLightChanges(scene, lightSnapshot);
        }

        foreach (RenderObject renderObject in scene.RenderObjects)
        {
            if (renderObject == null ||
                !TryCreateDdgiTrackedRenderObject(renderObject, out DdgiTrackedRenderObject current))
                continue;

            if (_ddgiTrackedRenderObjects.TryGetValue(renderObject, out DdgiTrackedRenderObject previous))
            {
                if (hasPreviousSignature)
                    AddDdgiDirtyRegionsForObjectChange(previous, current);
            }
            else if (hasPreviousSignature)
            {
                AddDdgiDirtyRegion(current.Bounds, 1.0f, DdgiDirtyReason.GeometryAdded);
            }

            _ddgiTrackedRenderObjects[renderObject] = current with { LastSeenFrame = _ddgiTrackingFrame };
        }

        foreach (KeyValuePair<RenderObject, DdgiTrackedRenderObject> entry in _ddgiTrackedRenderObjects)
        {
            if (entry.Value.LastSeenFrame == _ddgiTrackingFrame)
                continue;

            if (hasPreviousSignature)
                AddDdgiDirtyRegion(entry.Value.Bounds, 1.0f, DdgiDirtyReason.GeometryRemoved);
            _ddgiTrackedRenderObjectRemovalScratch.Add(entry.Key);
        }

        for (int i = 0; i < _ddgiTrackedRenderObjectRemovalScratch.Count; i++)
            _ddgiTrackedRenderObjects.Remove(_ddgiTrackedRenderObjectRemovalScratch[i]);
        _ddgiTrackedRenderObjectRemovalScratch.Clear();
        AddDdgiDirtyRegionsForSustainedVfx(
            scene,
            hasPreviousSignature,
            telemetry);

        _lastDdgiLightSignature = lightSignature;
        StoreLastDdgiLights(lightSnapshot);
        _hasDdgiDynamicSignature = true;
        return _ddgiDirtyRegionScratch;
    }

    private void AddDdgiDirtyRegionsForSustainedVfx(
        Scene scene,
        bool hasPreviousSignature,
        DdgiInvalidationTelemetryAccumulator telemetry)
    {
        foreach (ParticleEffectInstance instance in scene.ParticleEffects)
        {
            if (!TryCreateDdgiTrackedVfxProxy(instance, out DdgiTrackedVfxProxy current))
                continue;

            if (_ddgiTrackedVfxProxies.TryGetValue(instance, out DdgiTrackedVfxProxy previous))
            {
                if (hasPreviousSignature && previous.Signature != current.Signature)
                {
                    AddDdgiDirtyRegion(Union(previous.Bounds, current.Bounds), 1.0f, DdgiDirtyReason.EmissiveChanged);
                    telemetry.VfxDdgiDirtyProbeEventCount++;
                }
            }
            else if (hasPreviousSignature)
            {
                AddDdgiDirtyRegion(current.Bounds, 1.0f, DdgiDirtyReason.EmissiveChanged);
                telemetry.VfxDdgiDirtyProbeEventCount++;
            }

            _ddgiTrackedVfxProxies[instance] = current with { LastSeenFrame = _ddgiTrackingFrame };
        }

        foreach (KeyValuePair<ParticleEffectInstance, DdgiTrackedVfxProxy> entry in _ddgiTrackedVfxProxies)
        {
            if (entry.Value.LastSeenFrame == _ddgiTrackingFrame)
                continue;

            if (hasPreviousSignature)
            {
                AddDdgiDirtyRegion(entry.Value.Bounds, 1.0f, DdgiDirtyReason.EmissiveChanged);
                telemetry.VfxDdgiDirtyProbeEventCount++;
            }
            _ddgiTrackedVfxProxyRemovalScratch.Add(entry.Key);
        }

        for (int i = 0; i < _ddgiTrackedVfxProxyRemovalScratch.Count; i++)
            _ddgiTrackedVfxProxies.Remove(_ddgiTrackedVfxProxyRemovalScratch[i]);
        _ddgiTrackedVfxProxyRemovalScratch.Clear();
    }

    private bool TryCreateDdgiTrackedVfxProxy(
        ParticleEffectInstance instance,
        out DdgiTrackedVfxProxy tracked)
    {
        tracked = default;
        if (instance == null || !instance.Visible || !instance.Playing || instance.Stopped)
            return false;

        BoundingBox bounds = default;
        bool hasBounds = false;
        int sustainedEmitterCount = 0;
        ulong signature = HashAdd(HashStart, instance.WorldMatrix);

        IReadOnlyList<ParticleEmitterDefinition> emitters = instance.Effect.Emitters;
        for (int i = 0; i < emitters.Count; i++)
        {
            ParticleEmitterDefinition emitter = emitters[i];
            if (!IsDdgiSustainedEmissiveVfx(emitter))
                continue;

            BoundingBox emitterBounds = EstimateDdgiVfxEmitterBounds(instance, emitter);
            bounds = hasBounds ? Union(bounds, emitterBounds) : emitterBounds;
            hasBounds = true;
            sustainedEmitterCount++;
            signature = HashAdd(signature, i);
            signature = HashAdd(signature, emitter.SpawnShape.Radius);
            signature = HashAdd(signature, emitter.SpawnShape.Extents);
            signature = HashAdd(signature, emitter.SpawnShape.Length);
            signature = HashAdd(signature, emitter.SpawnRatePerSecond);
            signature = HashAdd(signature, emitter.DurationSeconds);
            signature = HashAdd(signature, emitter.Looping);
            signature = HashAdd(signature, SampleMaxEmissive(emitter));
            signature = HashAdd(signature, (uint)emitter.GlobalIlluminationEmission);
            signature = HashAdd(signature, (uint)emitter.GlobalIlluminationSourceShape);
            signature = HashAdd(signature, emitter.GlobalIlluminationPower);
            signature = HashAdd(signature, emitter.GlobalIlluminationEnergyHysteresis);
        }

        IReadOnlyList<BeamDefinition> beams = instance.Effect.Beams;
        for (int i = 0; i < beams.Count; i++)
        {
            BeamDefinition beam = beams[i];
            if (!IsDdgiBeamMacroSource(beam))
                continue;

            Vector3 start = beam.LocalStart * instance.WorldMatrix;
            Vector3 end = beam.LocalEnd * instance.WorldMatrix;
            float radius = MathF.Max(
                MathF.Max(beam.Width.Sample(0.0f), beam.Width.Sample(1.0f)) * 0.5f +
                    MathF.Max(beam.NoiseAmplitude, 0.0f),
                0.001f);
            Vector3 extent = new(radius);
            BoundingBox beamBounds = new(
                Vector3.Min(start, end) - extent,
                Vector3.Max(start, end) + extent);
            bounds = hasBounds ? Union(bounds, beamBounds) : beamBounds;
            hasBounds = true;
            sustainedEmitterCount++;
            signature = HashAdd(signature, i);
            signature = HashAdd(signature, beam.LocalStart);
            signature = HashAdd(signature, beam.LocalEnd);
            signature = HashAdd(signature, beam.GlobalIlluminationPower);
            signature = HashAdd(signature, (uint)beam.GlobalIlluminationEmission);
            signature = HashAdd(signature, beam.GlobalIlluminationEnergyHysteresis);
        }

        if (!hasBounds || sustainedEmitterCount == 0)
            return false;

        tracked = new DdgiTrackedVfxProxy(
            HashAdd(signature, sustainedEmitterCount),
            bounds,
            _ddgiTrackingFrame);
        return true;
    }

    private static bool IsDdgiSustainedEmissiveVfx(ParticleEmitterDefinition emitter)
    {
        if (emitter == null)
            return false;

        if (emitter.GlobalIlluminationEmission == ParticleGiEmissionMode.Disabled)
            return false;
        if (emitter.GlobalIlluminationEmission == ParticleGiEmissionMode.Force)
        {
            Vector3 power = emitter.GlobalIlluminationPower;
            return float.IsFinite(power.X) && float.IsFinite(power.Y) && float.IsFinite(power.Z) &&
                   (power.X > 0.0f || power.Y > 0.0f || power.Z > 0.0f);
        }

        float maxEmissive = SampleMaxEmissive(emitter);
        bool transientBurstOnly = !emitter.Looping &&
            emitter.DurationSeconds < 1.0f &&
            emitter.SpawnRatePerSecond <= 0.01f &&
            emitter.BurstCount > 0;
        bool sustained = emitter.Looping ||
            emitter.DurationSeconds >= 1.0f ||
            emitter.SpawnRatePerSecond >= 2.0f;
        return sustained && !transientBurstOnly && maxEmissive >= 1.25f;
    }

    private static float SampleMaxEmissive(ParticleEmitterDefinition emitter)
    {
        return MathF.Max(
            emitter.EmissiveOverLife.Sample(0.0f),
            MathF.Max(
                emitter.EmissiveOverLife.Sample(0.5f),
                emitter.EmissiveOverLife.Sample(1.0f)));
    }

    private static bool IsDdgiBeamMacroSource(BeamDefinition beam)
    {
        if (beam == null ||
            beam.GlobalIlluminationEmission == ParticleGiEmissionMode.Disabled)
        {
            return false;
        }

        Vector3 power = beam.GlobalIlluminationPower;
        return float.IsFinite(power.X) && float.IsFinite(power.Y) && float.IsFinite(power.Z) &&
               (power.X > 0.0f || power.Y > 0.0f || power.Z > 0.0f);
    }

    private static BoundingBox EstimateDdgiVfxEmitterBounds(
        ParticleEffectInstance instance,
        ParticleEmitterDefinition emitter)
    {
        Vector3 center = new(
            instance.WorldMatrix.M41,
            instance.WorldMatrix.M42,
            instance.WorldMatrix.M43);
        ParticleSpawnShape shape = emitter.SpawnShape;
        float spawnRadius = MathF.Max(
            shape.Radius,
            MathF.Max(shape.Length * 0.5f, MathF.Max(shape.Extents.X, MathF.Max(shape.Extents.Y, shape.Extents.Z))));
        float velocityRadius = MathF.Max(emitter.InitialVelocityMin.Length(), emitter.InitialVelocityMax.Length()) *
            MathF.Max(0.0f, emitter.LifetimeSeconds.Sample(1.0f));
        float sizeRadius = MathF.Max(emitter.Size.Sample(0.0f), emitter.Size.Sample(1.0f));
        float radius = MathF.Max(0.25f, spawnRadius + velocityRadius + sizeRadius);
        Vector3 r = new(radius);
        return new BoundingBox(center - r, center + r);
    }

    private bool TryCreateDdgiTrackedRenderObject(
        RenderObject renderObject,
        out DdgiTrackedRenderObject tracked)
    {
        tracked = default;

        if (!renderObject.Enabled ||
            !renderObject.Visible ||
            renderObject.Mesh is not MeshHandle meshHandle ||
            !meshHandle.IsValid)
        {
            return false;
        }

        try
        {
            MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
            if (meshInfo.VertexCount == 0 || meshInfo.IndexCount < 3)
                return false;

            MaterialHandle materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                renderObject.Material,
                _materialManager.DefaultMaterialHandle,
                renderObject.Name);
            MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
            if (metadata.RenderMode == MaterialRenderMode.Blend || metadata.IsGeometryDecal)
                return false;

            BoundingBox localBounds = new(ToCoreVector(meshInfo.BoundingBoxMin), ToCoreVector(meshInfo.BoundingBoxMax));
            BoundingBox bounds = SceneDataBuilder.TransformBoundingBox(localBounds, renderObject.WorldMatrix);

            ulong geometrySignature = HashStart;
            geometrySignature = HashAdd(geometrySignature, meshHandle.Index);
            geometrySignature = HashAdd(geometrySignature, meshHandle.Generation);
            geometrySignature = HashAdd(geometrySignature, meshInfo.VertexCount);
            geometrySignature = HashAdd(geometrySignature, meshInfo.IndexCount);
            geometrySignature = HashAdd(geometrySignature, ToCoreVector(meshInfo.BoundingBoxMin));
            geometrySignature = HashAdd(geometrySignature, ToCoreVector(meshInfo.BoundingBoxMax));

            GPUMaterialData materialData = _materialManager.GetMaterialData(materialHandle);
            MaterialAspectRevisions aspectRevisions = _materialManager.GetMaterialAspectRevisions(materialHandle);
            uint profileRevision = _materialManager.GetMaterialTransportProfileRevision(materialHandle.Index);
            ulong materialSignature = CreateDdgiMaterialSignature(
                materialHandle,
                materialData,
                metadata,
                aspectRevisions,
                profileRevision);
            ulong emissiveSignature = CreateDdgiEmissiveMaterialSignature(
                materialData,
                aspectRevisions.Emission,
                profileRevision);
            ulong transformSignature = HashAdd(HashStart, renderObject.WorldMatrix);
            tracked = new DdgiTrackedRenderObject(
                geometrySignature,
                materialSignature,
                emissiveSignature,
                transformSignature,
                bounds,
                _ddgiTrackingFrame);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void AddDdgiDirtyRegionsForObjectChange(
        DdgiTrackedRenderObject previous,
        DdgiTrackedRenderObject current)
    {
        if (previous.GeometrySignature != current.GeometrySignature)
        {
            AddDdgiDirtyRegion(previous.Bounds, 1.0f, DdgiDirtyReason.GeometryRemoved);
            AddDdgiDirtyRegion(current.Bounds, 1.0f, DdgiDirtyReason.GeometryAdded);
            return;
        }

        if (previous.TransformSignature != current.TransformSignature)
            AddDdgiDirtyRegion(Union(previous.Bounds, current.Bounds), 1.0f, DdgiDirtyReason.TransformChanged);

        if (previous.MaterialSignature != current.MaterialSignature)
        {
            DdgiDirtyReason reason = previous.EmissiveSignature != current.EmissiveSignature
                ? DdgiDirtyReason.EmissiveChanged
                : DdgiDirtyReason.MaterialChanged;
            AddDdgiDirtyRegion(current.Bounds, 1.0f, reason);
        }
    }

    private static ulong CreateDdgiMaterialSignature(
        MaterialHandle materialHandle,
        GPUMaterialData materialData,
        MaterialRenderMetadata metadata,
        MaterialAspectRevisions aspectRevisions,
        uint profileRevision)
    {
        ulong hash = HashStart;
        hash = HashAdd(hash, materialHandle.Index);
        hash = HashAdd(hash, materialHandle.Generation);
        // DDGI consumes every transport aspect below. Hash their monotonic
        // revisions instead of trying to maintain a second, inevitably partial
        // list of texture indices, transforms, extension words, and compiled
        // profile fields here. Keep the compact payload values in the signature
        // as a defensive ABI/backward-compatibility check.
        hash = HashAdd(hash, aspectRevisions.DiffuseTransport);
        hash = HashAdd(hash, aspectRevisions.Emission);
        hash = HashAdd(hash, aspectRevisions.AlphaCoverage);
        hash = HashAdd(hash, aspectRevisions.Sidedness);
        hash = HashAdd(hash, aspectRevisions.ShadingModel);
        hash = HashAdd(hash, profileRevision);
        hash = HashAdd(hash, materialData.PackedMeanGiDirectionalDiffuseBaseRg);
        hash = HashAdd(hash, materialData.PackedMeanGiDirectionalDiffuseBaseBAndF0R);
        hash = HashAdd(hash, materialData.PackedMeanGiDielectricF0Gb);
        hash = HashAdd(hash, materialData.DdgiAverageTransmission);
        hash = HashAdd(hash, materialData.DdgiAverageAlbedo);
        hash = HashAdd(hash, materialData.DdgiAverageEmissive);
        hash = HashAdd(hash, materialData.DdgiMaterialPolicy);
        hash = HashAdd(hash, materialData.Emissive);
        hash = HashAdd(hash, materialData.AlbedoTextureIndex);
        hash = HashAdd(hash, materialData.EmissiveTextureIndex);
        hash = HashAdd(hash, materialData.FeatureFlags);
        hash = HashAdd(hash, (int)metadata.RenderMode);
        hash = HashAdd(hash, metadata.IsGeometryDecal);
        return hash;
    }

    private static ulong CreateDdgiEmissiveMaterialSignature(
        GPUMaterialData materialData,
        uint emissionRevision,
        uint profileRevision)
    {
        ulong hash = HashStart;
        hash = HashAdd(hash, emissionRevision);
        hash = HashAdd(hash, profileRevision);
        hash = HashAdd(hash, materialData.Emissive);
        hash = HashAdd(hash, materialData.DdgiAverageEmissive);
        hash = HashAdd(hash, materialData.EmissiveTextureIndex);
        return hash;
    }

    private void AddDdgiDirtyRegionsForLightChanges(Scene scene, LightFrameSnapshot lightSnapshot)
    {
        bool dirtiedWholeScene = false;
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        int previousCount = _lastDdgiLights.Length;
        int compareCount = Math.Max(count, previousCount);
        for (int i = 0; i < compareCount; i++)
        {
            bool hasCurrent = i < count;
            bool hasPrevious = i < previousCount;
            Light current = hasCurrent ? lights[i] : default;
            Light previous = hasPrevious ? _lastDdgiLights[i] : default;
            if (hasCurrent && hasPrevious && HashAddDdgiLight(HashStart, current) == HashAddDdgiLight(HashStart, previous))
                continue;

            bool directional = (hasCurrent && current.Type == LightType.Directional) ||
                (hasPrevious && previous.Type == LightType.Directional);
            if (directional)
            {
                if (!dirtiedWholeScene)
                {
                    AddDdgiDirtyRegion(EstimateSceneProbeBounds(scene), 4.0f, DdgiDirtyReason.DirectionalLightChanged);
                    dirtiedWholeScene = true;
                }
                continue;
            }

            if (hasPrevious)
                AddDdgiDirtyRegion(CreateLocalLightBounds(previous), 1.0f, DdgiDirtyReason.LocalLightChanged);
            if (hasCurrent)
                AddDdgiDirtyRegion(CreateLocalLightBounds(current), 1.0f, DdgiDirtyReason.LocalLightChanged);
        }
    }

    private static BoundingBox CreateLocalLightBounds(Light light)
    {
        if (!AnalyticalLightGeometry.TryGetInfluenceBounds(
                light,
                out System.Numerics.Vector3 minimum,
                out System.Numerics.Vector3 maximum))
        {
            minimum = light.Position;
            maximum = light.Position;
        }
        return new BoundingBox(ToCoreVector(minimum), ToCoreVector(maximum));
    }

    private void AddDdgiDirtyRegion(BoundingBox bounds, float padding, DdgiDirtyReason reason)
    {
        if (bounds.Max.X < bounds.Min.X || bounds.Max.Y < bounds.Min.Y || bounds.Max.Z < bounds.Min.Z)
            return;

        Vector3 p = new(MathF.Max(0.0f, padding));
        BoundingBox expanded = new(bounds.Min - p, bounds.Max + p);
        _ddgiDirtyBoundsScratch.Add(expanded);
        _ddgiDirtyRegionScratch.Add(new DdgiDirtyRegion(expanded, reason)
        {
            OldWorldBounds = bounds,
            NewWorldBounds = bounds,
            InfluenceBounds = expanded,
            ReasonFlags = 1u << (int)reason,
            Priority = 0
        });
    }

    private void StoreLastDdgiLights(LightFrameSnapshot lightSnapshot)
    {
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        if (_lastDdgiLights.Length != count)
            _lastDdgiLights = new Light[count];
        for (int i = 0; i < count; i++)
            _lastDdgiLights[i] = lights[i];
    }

    private void ResetDynamicTrackingCore()
    {
        _ddgiDirtyBoundsScratch.Clear();
        _ddgiDirtyRegionScratch.Clear();
        _ddgiMergedDirtyRegionScratch.Clear();
        _ddgiTrackedRenderObjects.Clear();
        _ddgiTrackedRenderObjectRemovalScratch.Clear();
        _ddgiTrackedSkinnedPoses.Clear();
        _ddgiSeenSkinnedPoseIdentities.Clear();
        _ddgiTrackedSkinnedPoseRemovalScratch.Clear();
        _ddgiTrackedVfxProxies.Clear();
        _ddgiTrackedVfxProxyRemovalScratch.Clear();
        _lastDdgiLights = Array.Empty<Light>();
        _hasDdgiDynamicSignature = false;
        _ddgiMutationOraclePrimed = false;
        _lastDdgiFoliageProxyDirtySignature = 0UL;
        _hasDdgiFoliageProxyDirtySignature = false;
        _lastDdgiFoliageProxyInfluenceBounds = null;
    }

    private IReadOnlyList<DdgiDirtyRegion>
        MergeDdgiFoliageProxyDirtyRegion(
            IReadOnlyList<DdgiDirtyRegion> regions)
    {
        ulong signature = _activeFoliageFrame.ContentSignature;
        BoundingBox? currentBounds =
            _activeFoliageFrame.InfluenceBounds;
        if (!_hasDdgiFoliageProxyDirtySignature)
        {
            _lastDdgiFoliageProxyDirtySignature = signature;
            _lastDdgiFoliageProxyInfluenceBounds = currentBounds;
            _hasDdgiFoliageProxyDirtySignature = true;
            return regions;
        }
        if (signature == _lastDdgiFoliageProxyDirtySignature)
            return regions;

        BoundingBox? previousBounds =
            _lastDdgiFoliageProxyInfluenceBounds;
        _lastDdgiFoliageProxyDirtySignature = signature;
        _lastDdgiFoliageProxyInfluenceBounds = currentBounds;
        if (!previousBounds.HasValue && !currentBounds.HasValue)
            return regions;

        BoundingBox oldBounds = previousBounds ?? currentBounds!.Value;
        BoundingBox newBounds = currentBounds ?? previousBounds!.Value;
        BoundingBox influence =
            DdgiGeometryParticipation.CreateSweptInfluenceBounds(
                oldBounds,
                newBounds,
                1.0f);
        DdgiDirtyReason reason = !previousBounds.HasValue
            ? DdgiDirtyReason.GeometryAdded
            : !currentBounds.HasValue
                ? DdgiDirtyReason.GeometryRemoved
                : DdgiDirtyReason.TransformChanged;
        return AppendDdgiDirtyRegion(
            regions,
            new DdgiDirtyRegion(influence, reason)
            {
                OldWorldBounds = oldBounds,
                NewWorldBounds = newBounds,
                InfluenceBounds = influence,
                ReasonFlags = 1u << (int)reason,
                Priority = 1u,
                SourceRevision = signature,
                SourceIdentifier = 0x464F4C4941474547UL
            });
    }

    private IReadOnlyList<DdgiDirtyRegion>
        MergeDdgiSkinnedPoseDirtyRegions(
            Scene scene,
            IReadOnlyList<DdgiDirtyRegion> regions)
    {
        bool currentPoseEnabled = _activeGi!
            .EffectiveDdgiSkinnedGeometryMode ==
            DdgiSkinnedGeometryMode.CurrentPose;
        _ddgiSeenSkinnedPoseIdentities.Clear();
        if (currentPoseEnabled)
        {
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (renderObject is not SkinnedRenderObject skinned ||
                    !skinned.Enabled ||
                    !skinned.Visible ||
                    !skinned.SkinningEnabled ||
                    skinned.Animator == null ||
                    !TryResolveSkinnedDdgiBounds(
                        skinned,
                        out BoundingBox bounds))
                {
                    continue;
                }

                Guid identity = skinned.Id;
                _ddgiSeenSkinnedPoseIdentities.Add(identity);
                ulong poseRevision = skinned.Animator.PoseRevision;
                if (_ddgiTrackedSkinnedPoses.TryGetValue(
                        identity,
                        out DdgiTrackedSkinnedPose previous))
                {
                    if (previous.PoseRevision != poseRevision)
                    {
                        BoundingBox influence =
                            DdgiGeometryParticipation
                                .CreateSweptInfluenceBounds(
                                    previous.Bounds,
                                    bounds,
                                    1.0f);
                        regions = AppendDdgiDirtyRegion(
                            regions,
                            new DdgiDirtyRegion(
                                influence,
                                DdgiDirtyReason.TransformChanged)
                            {
                                OldWorldBounds = previous.Bounds,
                                NewWorldBounds = bounds,
                                InfluenceBounds = influence,
                                ReasonFlags = 1u <<
                                    (int)DdgiDirtyReason.TransformChanged,
                                Priority = 1u,
                                SourceRevision = poseRevision,
                                SourceIdentifier =
                                    AccelerationStructureManager
                                        .StableInstanceIdentity(identity)
                            });
                    }
                }
                else
                {
                    BoundingBox influence =
                        DdgiGeometryParticipation
                            .CreateSweptInfluenceBounds(
                                bounds,
                                bounds,
                                1.0f);
                    regions = AppendDdgiDirtyRegion(
                        regions,
                        new DdgiDirtyRegion(
                            influence,
                            DdgiDirtyReason.GeometryAdded)
                        {
                            OldWorldBounds = bounds,
                            NewWorldBounds = bounds,
                            InfluenceBounds = influence,
                            ReasonFlags = 1u <<
                                (int)DdgiDirtyReason.GeometryAdded,
                            Priority = 1u,
                            SourceRevision = poseRevision,
                            SourceIdentifier =
                                AccelerationStructureManager
                                    .StableInstanceIdentity(identity)
                        });
                }
                _ddgiTrackedSkinnedPoses[identity] =
                    new DdgiTrackedSkinnedPose(
                        poseRevision,
                        bounds);
            }
        }

        foreach (KeyValuePair<Guid, DdgiTrackedSkinnedPose> pair in
                 _ddgiTrackedSkinnedPoses)
        {
            if (_ddgiSeenSkinnedPoseIdentities.Contains(pair.Key))
                continue;
            // Scene mutation events own ordinary removals. This branch also
            // covers a current-pose admission loss or mode toggle, where no
            // scene event exists.
            BoundingBox influence =
                DdgiGeometryParticipation.CreateSweptInfluenceBounds(
                    pair.Value.Bounds,
                    pair.Value.Bounds,
                    1.0f);
            regions = AppendDdgiDirtyRegion(
                regions,
                new DdgiDirtyRegion(
                    influence,
                    DdgiDirtyReason.GeometryRemoved)
                {
                    OldWorldBounds = pair.Value.Bounds,
                    NewWorldBounds = pair.Value.Bounds,
                    InfluenceBounds = influence,
                    ReasonFlags = 1u <<
                        (int)DdgiDirtyReason.GeometryRemoved,
                    Priority = 1u,
                    SourceRevision = pair.Value.PoseRevision,
                    SourceIdentifier =
                        AccelerationStructureManager
                            .StableInstanceIdentity(pair.Key)
                });
            _ddgiTrackedSkinnedPoseRemovalScratch.Add(pair.Key);
        }
        for (int index = 0;
             index < _ddgiTrackedSkinnedPoseRemovalScratch.Count;
             index++)
        {
            _ddgiTrackedSkinnedPoses.Remove(
                _ddgiTrackedSkinnedPoseRemovalScratch[index]);
        }
        _ddgiTrackedSkinnedPoseRemovalScratch.Clear();
        return regions;
    }

    private bool TryResolveSkinnedDdgiBounds(
        SkinnedRenderObject skinned,
        out BoundingBox bounds)
    {
        BoundingBox? localBounds = skinned.AnimatedBoundingBox ??
            skinned.LocalMeshBounds;
        if (!localBounds.HasValue &&
            skinned.Mesh is MeshHandle mesh &&
            mesh.IsValid)
        {
            try
            {
                MeshInfo meshInfo = _meshManager.GetMeshInfo(mesh);
                localBounds = new BoundingBox(
                    ToCoreVector(meshInfo.BoundingBoxMin),
                    ToCoreVector(meshInfo.BoundingBoxMax));
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                bounds = default;
                return false;
            }
        }
        if (!localBounds.HasValue)
        {
            bounds = default;
            return false;
        }

        bounds = SceneDataBuilder.TransformBoundingBox(
            localBounds.Value,
            skinned.WorldMatrix);
        return true;
    }

    private IReadOnlyList<DdgiDirtyRegion> AppendDdgiDirtyRegion(
        IReadOnlyList<DdgiDirtyRegion> regions,
        DdgiDirtyRegion region)
    {
        if (ReferenceEquals(regions, _ddgiMergedDirtyRegionScratch))
        {
            _ddgiMergedDirtyRegionScratch.Add(region);
            return _ddgiMergedDirtyRegionScratch;
        }

        _ddgiMergedDirtyRegionScratch.Clear();
        for (int index = 0; index < regions.Count; index++)
            _ddgiMergedDirtyRegionScratch.Add(regions[index]);
        _ddgiMergedDirtyRegionScratch.Add(region);
        return _ddgiMergedDirtyRegionScratch;
    }



    public DdgiInvalidationIdentityFrame ResolveFrameIdentity(
        in DdgiInvalidationIdentityRequest request,
        ReadOnlySpan<bool> atmosphereOwnedLights)
    {
        ArgumentNullException.ThrowIfNull(request.Scene);
        ArgumentNullException.ThrowIfNull(request.Gi);
        ArgumentNullException.ThrowIfNull(request.Environment);
        SimpleDdgiDirtySignature dirty = CreateSimpleDdgiDirtySignature(
            request,
            atmosphereOwnedLights);
        SimpleDdgiWarmStartSceneIdentity? warmStart =
            ResolveSimpleDdgiWarmStartSceneIdentity(request);
        return new DdgiInvalidationIdentityFrame(dirty, warmStart);
    }

    internal static ulong CreateSimpleDdgiGlobalLightSignature(
        LightFrameSnapshot lightSnapshot)
    {
        ulong hash = HashStart;
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        int directionalCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (lights[i].Type != LightType.Directional)
                continue;
            hash = HashAddDdgiLight(hash, lights[i]);
            directionalCount++;
        }
        return HashAdd(hash, directionalCount);
    }

    internal static ulong CreateSimpleDdgiLightingSignature(LightFrameSnapshot lightSnapshot, uint emissiveSourceRevision)
    {
        ulong hash = CreateDdgiLightSignature(lightSnapshot);
        return HashAdd(hash, emissiveSourceRevision);
    }

    private static ulong CreateSimpleDdgiWarmStartLightSignature(
        LightFrameSnapshot lightSnapshot)
    {
        ulong hash = HashStart;
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        hash = HashAdd(hash, count);
        for (int index = 0; index < count; index++)
            hash = HashAdd(hash, lights[index]);
        return hash;
    }

    private static ulong CreateSimpleDdgiWarmStartEnvironmentSignature(
        in DdgiInvalidationIdentityRequest request)
    {
        GlobalIlluminationSettings gi = request.Gi;
        ulong hash = CreateSimpleDdgiEnvironmentSignature(
            request.Environment);
        hash = HashAdd(
            hash,
            request.EnvironmentGiLightingSignature);
        hash = HashAdd(hash, gi.EnvironmentFallbackIntensity);
        hash = HashAdd(hash, gi.DdgiAlphaMaskedTransportEnabled);
        hash = HashAdd(hash, gi.DdgiThinWallPolicyEnabled);
        hash = HashAdd(hash, gi.DdgiSelfShadowBiasScale);
        hash = HashAdd(hash, gi.DdgiThinWallLeakClampStrength);
        hash = HashAdd(hash, gi.SimpleDdgiNormalBias);
        hash = HashAdd(hash, gi.SimpleDdgiViewBias);
        hash = HashAdd(hash, gi.SimpleDdgiMaximumWorldBiasMeters);
        hash = HashAdd(hash, gi.SimpleDdgiArchitecturalThicknessMeters);
        hash = HashAdd(hash, gi.SimpleDdgiThinSurfaceTransmissionEnabled);
        hash = HashAdd(hash, gi.SimpleDdgiReducedBlendEnabled);
        hash = HashAdd(hash, gi.SimpleDdgiHysteresis);
        hash = HashAdd(hash, gi.SimpleDdgiHysteresisChangeThreshold);
        hash = HashAdd(hash, gi.SimpleDdgiHysteresisStepThreshold);
        hash = HashAdd(hash, gi.SimpleDdgiTransportSolverRelaxation);
        hash = HashAdd(hash, gi.SimpleDdgiTransportAlbedoClamp);
        hash = HashAdd(hash, gi.SimpleDdgiNearMaterialTextureMaxCascade);
        hash = HashAdd(hash, gi.SimpleDdgiMidMaterialTextureMaxCascade);
        hash = HashAdd(hash, gi.SimpleDdgiFarMaterialTextureMaxCascade);
        hash = HashAdd(hash, gi.SimpleDdgiNearMaxShadedLights);
        hash = HashAdd(hash, gi.SimpleDdgiMidMaxShadedLights);
        hash = HashAdd(hash, gi.SimpleDdgiFarMaxShadedLights);
        hash = HashAdd(hash, gi.EffectiveGiEmissiveMeshSampling);
        hash = HashAdd(hash, gi.DdgiEmissiveTriangleBudget);
        hash = HashAdd(hash, gi.FarFieldClipmapEnabled);
        hash = HashAdd(hash, gi.FarFieldPagedEnabled);
        hash = HashAdd(hash, gi.GiFarFieldMaterialV2);
        hash = HashAdd(hash, gi.FarFieldForceAll);
        hash = HashAdd(hash, gi.FarFieldSkyVisibilityEnabled);
        hash = HashAdd(hash, gi.FarFieldSunShadowEnabled);
        hash = HashAdd(hash, gi.FarFieldStartDistance);
        return HashAdd(hash, gi.FarFieldMaxTraceSteps);
    }

    private SimpleDdgiWarmStartSceneIdentity?
        ResolveSimpleDdgiWarmStartSceneIdentity(
            in DdgiInvalidationIdentityRequest request)
    {
        Scene scene = request.Scene;
        LightFrameSnapshot lightSnapshot = request.Lights;
        ulong lightSignature =
            CreateSimpleDdgiWarmStartLightSignature(lightSnapshot);
        ulong environmentSignature =
            CreateSimpleDdgiWarmStartEnvironmentSignature(request);
        ulong emissiveSignature = request.Emissive.SourceSignature;
        bool sameScene =
            _simpleDdgiWarmStartIdentityScene?.TryGetTarget(
                out Scene? identityScene) == true &&
            ReferenceEquals(identityScene, scene);
        bool unchanged = sameScene &&
            _simpleDdgiWarmStartIdentityMutationSerial ==
                scene.MutationSerial &&
            _simpleDdgiWarmStartIdentityContentRevision ==
                request.SceneContentRevision &&
            _simpleDdgiWarmStartIdentityMaterialRevision ==
                request.MaterialRevision &&
            _simpleDdgiWarmStartIdentityLightSignature ==
                lightSignature &&
            _simpleDdgiWarmStartIdentityEnvironmentSignature ==
                environmentSignature &&
            _simpleDdgiWarmStartIdentityEmissiveSignature ==
                emissiveSignature;
        if (unchanged)
            return _simpleDdgiWarmStartSceneIdentity;

        if (_simpleDdgiWarmStartIdentityScene is null)
            _simpleDdgiWarmStartIdentityScene = new WeakReference<Scene>(scene);
        else
            _simpleDdgiWarmStartIdentityScene.SetTarget(scene);
        _simpleDdgiWarmStartIdentityMutationSerial = scene.MutationSerial;
        _simpleDdgiWarmStartIdentityContentRevision =
            request.SceneContentRevision;
        _simpleDdgiWarmStartIdentityMaterialRevision =
            request.MaterialRevision;
        _simpleDdgiWarmStartIdentityLightSignature = lightSignature;
        _simpleDdgiWarmStartIdentityEnvironmentSignature =
            environmentSignature;
        _simpleDdgiWarmStartIdentityEmissiveSignature =
            emissiveSignature;

        // A persisted background prior must not capture a transient VFX
        // phase. Static mesh emission remains eligible and is represented
        // by the exact source/surface payload signature above.
        if (request.Emissive.VfxMacroSourceCount > 0)
        {
            _simpleDdgiWarmStartSceneIdentity = null;
            return null;
        }

        try
        {
            _simpleDdgiWarmStartSceneIdentity =
                SimpleDdgiWarmStartIdentityBuilder.Create(
                    scene,
                    _meshManager,
                    _materialManager,
                    lightSignature,
                    environmentSignature,
                    emissiveSignature,
                    request.ShaderBundleHash);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ArgumentException or
            OverflowException)
        {
            // Cache identity construction is an optional optimization. Any
            // malformed or unavailable producer data fails closed without
            // taking the live GI renderer down with it.
            System.Diagnostics.Debug.WriteLine(
                $"Persistent Simple-DDGI warm-start identity rejected: {exception.Message}");
            _simpleDdgiWarmStartSceneIdentity = null;
        }
        return _simpleDdgiWarmStartSceneIdentity;
    }

    internal static ulong CreateSimpleDdgiEnvironmentSignature(EnvironmentSettings environment)
    {
        if (environment == null)
            throw new ArgumentNullException(nameof(environment));

        ulong hash = HashStart;
        hash = HashAdd(hash, environment.Enabled);
        hash = HashAdd(hash, (uint)environment.SourceKind);
        hash = HashAdd(hash, environment.SkyIntensity);
        hash = HashAdd(hash, environment.RotationRadians);
        hash = HashAdd(hash, (uint)environment.SunDriver);
        hash = HashAdd(hash, environment.Turbidity);
        hash = HashAdd(hash, environment.GroundAlbedo.X);
        hash = HashAdd(hash, environment.GroundAlbedo.Y);
        hash = HashAdd(hash, environment.GroundAlbedo.Z);
        hash = HashAdd(hash, environment.SunAngularDiameterDegrees);
        hash = HashAdd(hash, environment.MoonAngularDiameterDegrees);
        hash = HashAdd(hash, environment.LatitudeDegrees);
        hash = HashAdd(hash, environment.DayOfYear);
        hash = HashAdd(hash, environment.NorthOffsetDegrees);
        hash = HashAdd(hash, environment.AtmosphereIntensity);
        hash = HashAdd(hash, environment.SolarIrradianceScale);
        hash = HashAdd(hash, environment.MoonIrradianceScale);
        hash = HashAdd(hash, environment.StarIntensity);
        hash = HashAdd(hash, environment.AirglowIntensity);
        hash = HashAdd(hash, environment.EnvironmentSize);
        hash = HashAdd(hash, environment.IrradianceSize);
        hash = HashAdd(hash, (uint)environment.TexturePrecision);
        string sourcePath = environment.SourcePath ?? string.Empty;
        hash = HashAdd(hash, sourcePath.Length);
        for (int i = 0; i < sourcePath.Length; i++)
            hash = HashAdd(hash, sourcePath[i]);
        return hash;
    }

    private SimpleDdgiDirtySignature CreateSimpleDdgiDirtySignature(
        in DdgiInvalidationIdentityRequest request,
        ReadOnlySpan<bool> atmosphereOwnedLights)
    {
        Scene scene = request.Scene;
        LightFrameSnapshot lightSnapshot = request.Lights;
        uint emissiveSourceRevision = request.Emissive.SourceRevision;
        GlobalIlluminationSettings gi = request.Gi;
        bool regionalDynamicSources =
            gi.SimpleDdgiRegionalInvalidationEnabled;
        bool steppedAtmosphere =
            request.Environment.Enabled &&
            request.Environment.SourceKind == EnvironmentSourceKind.ProceduralSky &&
            request.UsesAnalyticSky;
        ulong stableLightSignature = steppedAtmosphere
            ? CreateSimpleDdgiStableLightSignature(
                lightSnapshot,
                includeLocalLights: !regionalDynamicSources,
                atmosphereOwnedLights)
            : regionalDynamicSources
                ? CreateSimpleDdgiGlobalLightSignature(lightSnapshot)
                : CreateDdgiLightSignature(lightSnapshot);
        ulong environmentSignature =
            CreateSimpleDdgiEnvironmentSignature(request.Environment);
        ulong atmosphereStepSignature = steppedAtmosphere
            ? request.EnvironmentGiLightingSignature
            : 0UL;
        environmentSignature = HashAdd(
            environmentSignature,
            gi.EnvironmentFallbackIntensity);
        ulong sourcePolicySignature = HashStart;
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.DdgiSelfShadowBiasScale);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.DdgiThinWallPolicyEnabled);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.DdgiThinWallLeakClampStrength);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.SimpleDdgiNormalBias);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.SimpleDdgiViewBias);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.SimpleDdgiMaximumWorldBiasMeters);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.SimpleDdgiArchitecturalThicknessMeters);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.FarFieldClipmapEnabled);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.FarFieldPagedEnabled);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.FarFieldForceAll);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.FarFieldSkyVisibilityEnabled);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.FarFieldStartDistance);
        sourcePolicySignature = HashAdd(sourcePolicySignature, gi.FarFieldMaxTraceSteps);
        // Page residency is a transient producer detail, not a change to the
        // world-space lighting contract. Hashing CoverageReady here made a
        // camera move invalidate every cached source once while pages were
        // requested and again when they became resident. The live readiness
        // bit still reaches the trace flags through SimpleDdgiVolumeManager.
        ulong sourceContractSignature = HashAdd(
            stableLightSignature,
            sourcePolicySignature);
        bool hasSoleDirectionalLight = TryGetSoleDirectionalLight(
            lightSnapshot,
            out Light soleDirectionalLight);
        // The normal DDGI light signature is intentionally quantized to
        // suppress insignificant scene churn. A sole-sun relight is cheap
        // enough to retain exact color/intensity responsiveness, so fold an
        // exact witness into this path only.
        ulong exactSoleDirectionalSignature = HashStart;
        exactSoleDirectionalSignature = HashAdd(
            exactSoleDirectionalSignature,
            hasSoleDirectionalLight);
        if (hasSoleDirectionalLight)
            exactSoleDirectionalSignature = HashAdd(
                exactSoleDirectionalSignature,
                soleDirectionalLight);
        ulong lightSignature = HashStart;
        lightSignature = HashAdd(lightSignature, sourceContractSignature);
        lightSignature = HashAdd(lightSignature, environmentSignature);
        lightSignature = HashAdd(lightSignature, atmosphereStepSignature);
        lightSignature = HashAdd(
            lightSignature,
            exactSoleDirectionalSignature);
        // Local lights and emissive producers already publish swept dirty
        // regions through the bounded mutation journal. Keeping them in the
        // global source signature discarded that spatial information and
        // restarted every probe on every animation tick. With regional
        // invalidation enabled, only environment/directional policy remains
        // global; affected probes full-trace their region and retain their
        // previously published irradiance while the temporal blend catches up.
        ulong emissiveSignature = HashAdd(
            HashStart,
            regionalDynamicSources ? 0u : emissiveSourceRevision);
        // Region events are the normal Simple DDGI dynamic path.  Retain the
        // whole-scene signature only as the explicit legacy/validation mode;
        // otherwise hashing every render object merely recreates global dirty
        // boosts that the region scheduler is designed to avoid.
        ulong geometrySignature = !request.Gi.SimpleDdgiRegionalInvalidationEnabled &&
                                 request.Gi.SimpleDdgiDynamicGeometryDirtyBoostEnabled
            ? CreateSimpleDdgiDynamicGeometrySignature(scene)
            : HashStart;

        uint reasonFlags = 0u;
        bool cohortTransition = false;
        SimpleDdgiSourceRefreshMode sourceRefreshMode =
            SimpleDdgiSourceRefreshMode.None;
        Vector3 sourceRelightScale = Vector3.One;
        bool hasNoEmissiveSources =
            request.Emissive.SourceCount == 0 &&
            request.Emissive.TriangleCandidateCount == 0 &&
            request.Emissive.SkippedSkinnedObjectCount == 0 &&
            request.Emissive.ExcludedCandidateCount == 0;
        if (_hasSimpleDdgiDirtySignature)
        {
            if (lightSignature != _lastSimpleDdgiLightSignature)
                reasonFlags |= SimpleDdgiDirtyReasonLight;
            if (emissiveSignature != _lastSimpleDdgiEmissiveSignature)
                reasonFlags |= SimpleDdgiDirtyReasonEmissive;
            if (geometrySignature != _lastSimpleDdgiDynamicGeometrySignature)
                reasonFlags |= SimpleDdgiDirtyReasonDynamicGeometry;

            cohortTransition = steppedAtmosphere &&
                atmosphereStepSignature != _lastSimpleDdgiAtmosphereStepSignature &&
                sourceContractSignature == _lastSimpleDdgiStableLightSignature &&
                environmentSignature == _lastSimpleDdgiEnvironmentSignature &&
                emissiveSignature == _lastSimpleDdgiEmissiveSignature &&
                geometrySignature == _lastSimpleDdgiDynamicGeometrySignature;

            bool environmentOnlyChange =
                environmentSignature != _lastSimpleDdgiEnvironmentSignature &&
                sourceContractSignature == _lastSimpleDdgiStableLightSignature &&
                atmosphereStepSignature == _lastSimpleDdgiAtmosphereStepSignature &&
                emissiveSignature == _lastSimpleDdgiEmissiveSignature &&
                geometrySignature == _lastSimpleDdgiDynamicGeometrySignature;
            // A sole directional radiance edit is cache-relightable even
            // for the analytic sky. Cached surface hits scale exactly and
            // cached misses resample the current sky; direction or any
            // authored atmosphere setting change still fails closed.
            bool soleSunRadianceOnlyChange =
                lightSignature != _lastSimpleDdgiLightSignature &&
                hasSoleDirectionalLight &&
                _hasLastSimpleDdgiSoleDirectionalLight &&
                hasNoEmissiveSources &&
                _lastSimpleDdgiHadNoEmissiveSources &&
                sourcePolicySignature ==
                    _lastSimpleDdgiSourcePolicySignature &&
                environmentSignature ==
                    _lastSimpleDdgiEnvironmentSignature &&
                emissiveSignature ==
                    _lastSimpleDdgiEmissiveSignature &&
                geometrySignature ==
                    _lastSimpleDdgiDynamicGeometrySignature &&
                TryComputeSoleDirectionalRelightScale(
                    _lastSimpleDdgiSoleDirectionalLight,
                    soleDirectionalLight,
                    out sourceRelightScale);
            sourceRefreshMode = environmentOnlyChange
                ? SimpleDdgiSourceRefreshMode.EnvironmentMissRelight
                : soleSunRadianceOnlyChange
                    ? SimpleDdgiSourceRefreshMode.CachedHitRelight
                    : (lightSignature != _lastSimpleDdgiLightSignature ||
                       emissiveSignature != _lastSimpleDdgiEmissiveSignature ||
                       geometrySignature != _lastSimpleDdgiDynamicGeometrySignature
                        ? SimpleDdgiSourceRefreshMode.FullTrace
                        : SimpleDdgiSourceRefreshMode.None);
        }
        else
        {
            sourceRefreshMode = SimpleDdgiSourceRefreshMode.FullTrace;
        }

        _lastSimpleDdgiLightSignature = lightSignature;
        _lastSimpleDdgiStableLightSignature = sourceContractSignature;
        _lastSimpleDdgiSourcePolicySignature = sourcePolicySignature;
        _lastSimpleDdgiEnvironmentSignature = environmentSignature;
        _lastSimpleDdgiAtmosphereStepSignature = atmosphereStepSignature;
        _lastSimpleDdgiEmissiveSignature = emissiveSignature;
        _lastSimpleDdgiDynamicGeometrySignature = geometrySignature;
        _hasLastSimpleDdgiSoleDirectionalLight = hasSoleDirectionalLight;
        _lastSimpleDdgiSoleDirectionalLight = soleDirectionalLight;
        _lastSimpleDdgiHadNoEmissiveSources = hasNoEmissiveSources;
        _hasSimpleDdgiDirtySignature = true;

        ulong combined = HashStart;
        combined = HashAdd(combined, lightSignature);
        combined = HashAdd(combined, emissiveSignature);
        combined = HashAdd(combined, geometrySignature);
        return new SimpleDdgiDirtySignature(
            combined,
            reasonFlags,
            cohortTransition,
            sourceRefreshMode,
            sourceRelightScale);
    }

    private static bool TryGetSoleDirectionalLight(
        LightFrameSnapshot lightSnapshot,
        out Light light)
    {
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        bool sole = lightSnapshot.Count == 1 &&
            lights.Length >= 1 &&
            lightSnapshot.DirectionalLightCount == 1 &&
            lightSnapshot.LocalLightCount == 0 &&
            lights[0].Type == LightType.Directional;
        light = sole ? lights[0] : default;
        return sole;
    }

    internal static bool TryComputeSoleDirectionalRelightScale(
        in Light previous,
        in Light current,
        out Vector3 scale)
    {
        scale = Vector3.One;
        if (previous.Type != LightType.Directional ||
            current.Type != LightType.Directional ||
            previous.Position != current.Position ||
            previous.Range != current.Range ||
            previous.Direction != current.Direction ||
            previous.SpotAngle != current.SpotAngle ||
            previous.InnerSpotAngle != current.InnerSpotAngle ||
            previous.AttenuationMode != current.AttenuationMode ||
            previous.AttenuationConstant != current.AttenuationConstant ||
            previous.AttenuationLinear != current.AttenuationLinear ||
            previous.AttenuationQuadratic != current.AttenuationQuadratic ||
            previous.CastsShadows != current.CastsShadows ||
            previous.ShadowStrength != current.ShadowStrength ||
            previous.ShadowMapSizeOverride != current.ShadowMapSizeOverride ||
            previous.ShadowNearPlane != current.ShadowNearPlane ||
            previous.ShadowFarPlane != current.ShadowFarPlane ||
            previous.ShadowPriority != current.ShadowPriority ||
            !IsFiniteNonNegative(previous.Color) ||
            !IsFiniteNonNegative(current.Color) ||
            !float.IsFinite(previous.Intensity) ||
            !float.IsFinite(current.Intensity) ||
            previous.Intensity < 0.0f || current.Intensity < 0.0f)
        {
            return false;
        }

        System.Numerics.Vector3 oldRadiance =
            previous.Color * previous.Intensity;
        System.Numerics.Vector3 newRadiance =
            current.Color * current.Intensity;
        if (!IsFiniteNonNegative(oldRadiance) ||
            !IsFiniteNonNegative(newRadiance) ||
            !TryResolveRadianceScale(oldRadiance.X, newRadiance.X, out float x) ||
            !TryResolveRadianceScale(oldRadiance.Y, newRadiance.Y, out float y) ||
            !TryResolveRadianceScale(oldRadiance.Z, newRadiance.Z, out float z))
        {
            return false;
        }

        scale = new Vector3(x, y, z);
        return true;
    }

    private static bool TryResolveRadianceScale(
        float previous,
        float current,
        out float scale)
    {
        if (previous == 0.0f)
        {
            scale = 1.0f;
            return current == 0.0f;
        }

        scale = current / previous;
        return float.IsFinite(scale) && scale >= 0.0f;
    }

    private static bool IsFiniteNonNegative(
        System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        value.X >= 0.0f && value.Y >= 0.0f && value.Z >= 0.0f;

    private static ulong CreateSimpleDdgiStableLightSignature(
        LightFrameSnapshot lightSnapshot,
        bool includeLocalLights,
        ReadOnlySpan<bool> atmosphereOwnedLights)
    {
        ulong hash = HashStart;
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        int primaryDirectionalIndex = SelectPrimaryDdgiDirectionalLight(lightSnapshot);
        int includedCount = 0;
        for (int lightIndex = 0; lightIndex < count; lightIndex++)
        {
            Light light = lights[lightIndex];
            if (!includeLocalLights && light.Type != LightType.Directional)
                continue;
            bool atmosphereOwned = lightIndex == primaryDirectionalIndex ||
                (lightIndex < atmosphereOwnedLights.Length &&
                 atmosphereOwnedLights[lightIndex]);
            if (!atmosphereOwned)
            {
                hash = HashAddDdgiLight(hash, light);
                includedCount++;
                continue;
            }

            // Direction and radiance come from the stepped atmosphere
            // snapshot, but shadow ownership remains an authored transport
            // input and must still invalidate if it changes.
            hash = HashAdd(hash, (int)light.Type);
            hash = HashAdd(hash, light.CastsShadows);
            hash = HashAdd(
                hash,
                QuantizeForHash(light.ShadowStrength, 0.01f));
        }

        return HashAdd(hash, includedCount);
    }

    private ulong CreateSimpleDdgiDynamicGeometrySignature(Scene scene)
    {
        ulong hash = HashStart;
        int count = 0;
        foreach (RenderObject renderObject in scene.RenderObjects)
        {
            if (!TryCreateDdgiTrackedRenderObject(renderObject, out DdgiTrackedRenderObject tracked))
                continue;

            hash = HashAdd(hash, tracked.GeometrySignature);
            hash = HashAdd(hash, tracked.MaterialSignature);
            hash = HashAdd(hash, tracked.EmissiveSignature);
            hash = HashAdd(hash, tracked.TransformSignature);
            hash = HashAdd(hash, tracked.Bounds.Min);
            hash = HashAdd(hash, tracked.Bounds.Max);
            count++;
        }

        return HashAdd(hash, count);
    }


    private static int SelectPrimaryDdgiDirectionalLight(
        LightFrameSnapshot lightSnapshot)
    {
        int selectedIndex = -1;
        float selectedScore = -1.0f;
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        for (int index = 0; index < count; index++)
        {
            Light light = lights[index];
            if (light.Type != LightType.Directional)
                continue;
            float luminance = Math.Max(
                0.0f,
                0.2126f * light.Color.X +
                0.7152f * light.Color.Y +
                0.0722f * light.Color.Z);
            float score = luminance * Math.Max(light.Intensity, 0.0f);
            if (score <= selectedScore)
                continue;
            selectedIndex = index;
            selectedScore = score;
        }
        return selectedIndex;
    }


    private static ulong CreateDdgiLightSignature(
        LightFrameSnapshot lightSnapshot)
    {
        ulong hash = HashStart;
        ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
        int count = Math.Min(lightSnapshot.Count, lights.Length);
        hash = HashAdd(hash, count);
        for (int index = 0; index < count; index++)
            hash = HashAddDdgiLight(hash, lights[index]);
        return hash;
    }

    private static ulong HashAddDdgiLight(ulong hash, Light light)
    {
        hash = HashAdd(hash, (int)light.Type);
        hash = HashAdd(hash, QuantizeForHash(light.Intensity, 0.01f));
        hash = HashAdd(hash, QuantizeForHash(light.Color.X, 0.01f));
        hash = HashAdd(hash, QuantizeForHash(light.Color.Y, 0.01f));
        hash = HashAdd(hash, QuantizeForHash(light.Color.Z, 0.01f));
        hash = HashAdd(hash, light.CastsShadows);
        hash = HashAdd(hash, QuantizeForHash(light.ShadowStrength, 0.01f));
        if (light.Type == LightType.Directional)
        {
            hash = HashAdd(hash, QuantizeForHash(light.Direction.X, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Direction.Y, 0.0025f));
            return HashAdd(hash, QuantizeForHash(light.Direction.Z, 0.0025f));
        }
        hash = HashAdd(hash, QuantizeForHash(light.Position.X, 0.05f));
        hash = HashAdd(hash, QuantizeForHash(light.Position.Y, 0.05f));
        hash = HashAdd(hash, QuantizeForHash(light.Position.Z, 0.05f));
        hash = HashAdd(hash, QuantizeForHash(light.Range, 0.05f));
        hash = HashAdd(hash, (int)light.AttenuationMode);
        hash = HashAdd(hash, QuantizeForHash(light.AttenuationConstant, 0.0025f));
        hash = HashAdd(hash, QuantizeForHash(light.AttenuationLinear, 0.0025f));
        hash = HashAdd(hash, QuantizeForHash(light.AttenuationQuadratic, 0.0025f));
        if (light.Type == LightType.Spot)
        {
            hash = HashAdd(hash, QuantizeForHash(light.Direction.X, 0.005f));
            hash = HashAdd(hash, QuantizeForHash(light.Direction.Y, 0.005f));
            hash = HashAdd(hash, QuantizeForHash(light.Direction.Z, 0.005f));
            hash = HashAdd(hash, QuantizeForHash(light.SpotAngle, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.InnerSpotAngle, 0.0025f));
        }
        if (AnalyticalLightGeometry.IsArea(light.Type))
        {
            hash = HashAdd(hash, QuantizeForHash(light.Direction.X, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Direction.Y, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Direction.Z, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Up.X, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Up.Y, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Up.Z, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Size.X, 0.0025f));
            hash = HashAdd(hash, QuantizeForHash(light.Size.Y, 0.0025f));
            hash = HashAdd(hash, light.TwoSided);
        }
        if (AnalyticalLightGeometry.IsPunctual(light.Type))
        {
            hash = HashAdd(hash, light.PhotometricProfile.Value);
            hash = HashAdd(hash, light.PhotometricProfile.Revision);
            hash = HashAdd(hash, QuantizeForHash(light.IesRotationRadians, 0.0025f));
        }
        return hash;
    }

    private static int QuantizeForHash(float value, float step)
    {
        if (!float.IsFinite(value))
            return 0;
        if (!float.IsFinite(step) || step <= 0.0f)
            return (int)MathF.Round(value);
        float quantized = MathF.Round(value / step);
        if (quantized <= int.MinValue)
            return int.MinValue;
        if (quantized >= int.MaxValue)
            return int.MaxValue;
        return (int)quantized;
    }

    private static ulong HashAdd(ulong hash, Light light)
    {
        hash = HashAdd(hash, light.Position);
        hash = HashAdd(hash, light.Intensity);
        hash = HashAdd(hash, light.Color);
        hash = HashAdd(hash, light.Range);
        hash = HashAdd(hash, light.Direction);
        hash = HashAdd(hash, light.SpotAngle);
        hash = HashAdd(hash, light.InnerSpotAngle);
        hash = HashAdd(hash, (int)light.AttenuationMode);
        hash = HashAdd(hash, light.AttenuationConstant);
        hash = HashAdd(hash, light.AttenuationLinear);
        hash = HashAdd(hash, light.AttenuationQuadratic);
        hash = HashAdd(hash, (int)light.Type);
        hash = HashAdd(hash, light.CastsShadows);
        hash = HashAdd(hash, light.ShadowStrength);
        hash = HashAdd(hash, light.ShadowMapSizeOverride);
        hash = HashAdd(hash, light.ShadowNearPlane);
        hash = HashAdd(hash, light.ShadowFarPlane);
        return HashAdd(hash, light.ShadowPriority);
    }

    private static ulong HashAdd(
        ulong hash,
        System.Numerics.Vector3 value)
    {
        hash = HashAdd(hash, value.X);
        hash = HashAdd(hash, value.Y);
        return HashAdd(hash, value.Z);
    }

    private static ulong HashAdd(ulong hash, Matrix4x4 value)
    {
        hash = HashAdd(hash, value.M11); hash = HashAdd(hash, value.M12);
        hash = HashAdd(hash, value.M13); hash = HashAdd(hash, value.M14);
        hash = HashAdd(hash, value.M21); hash = HashAdd(hash, value.M22);
        hash = HashAdd(hash, value.M23); hash = HashAdd(hash, value.M24);
        hash = HashAdd(hash, value.M31); hash = HashAdd(hash, value.M32);
        hash = HashAdd(hash, value.M33); hash = HashAdd(hash, value.M34);
        hash = HashAdd(hash, value.M41); hash = HashAdd(hash, value.M42);
        hash = HashAdd(hash, value.M43); return HashAdd(hash, value.M44);
    }

    private static ulong HashAdd(ulong hash, Vector3 value)
    {
        hash = HashAdd(hash, value.X);
        hash = HashAdd(hash, value.Y);
        return HashAdd(hash, value.Z);
    }

    private static ulong HashAdd(ulong hash, Vector4 value)
    {
        hash = HashAdd(hash, value.X); hash = HashAdd(hash, value.Y);
        hash = HashAdd(hash, value.Z); return HashAdd(hash, value.W);
    }

    private static ulong HashAdd(ulong hash, bool value) =>
        HashAdd(hash, value ? 1u : 0u);
    private static ulong HashAdd(ulong hash, int value) =>
        HashAdd(hash, unchecked((uint)value));
    private static ulong HashAdd(ulong hash, float value) =>
        HashAdd(hash, BitConverter.SingleToUInt32Bits(value));
    private static ulong HashAdd(ulong hash, uint value)
    {
        unchecked
        {
            hash ^= value & 0xFFu; hash *= HashPrime;
            hash ^= (value >> 8) & 0xFFu; hash *= HashPrime;
            hash ^= (value >> 16) & 0xFFu; hash *= HashPrime;
            hash ^= (value >> 24) & 0xFFu;
            return hash * HashPrime;
        }
    }
    private static ulong HashAdd(ulong hash, ulong value)
    {
        hash = HashAdd(hash, unchecked((uint)value));
        return HashAdd(hash, unchecked((uint)(value >> 32)));
    }

    private static BoundingBox Union(BoundingBox left, BoundingBox right) =>
        new(Vector3.Min(left.Min, right.Min), Vector3.Max(left.Max, right.Max));
    private static Vector3 ToCoreVector(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);
    private static BoundingBox EstimateSceneProbeBounds(Scene scene) =>
        SimpleDdgiSceneBounds.Estimate(scene);
}

internal readonly record struct DdgiEmissiveInvalidationFacts(
    uint SourceRevision,
    ulong SourceSignature,
    int SourceCount,
    int TriangleCandidateCount,
    int SkippedSkinnedObjectCount,
    int ExcludedCandidateCount,
    int VfxMacroSourceCount);

internal readonly record struct DdgiInvalidationIdentityRequest(
    Scene Scene,
    LightFrameSnapshot Lights,
    GlobalIlluminationSettings Gi,
    EnvironmentSettings Environment,
    ulong EnvironmentGiLightingSignature,
    bool UsesAnalyticSky,
    ulong SceneContentRevision,
    uint MaterialRevision,
    DdgiEmissiveInvalidationFacts Emissive,
    string ShaderBundleHash);

internal readonly record struct DdgiInvalidationIdentityFrame(
    SimpleDdgiDirtySignature DirtySignature,
    SimpleDdgiWarmStartSceneIdentity? WarmStartIdentity);

internal readonly record struct SimpleDdgiDirtySignature(
    ulong Signature,
    uint ReasonFlags,
    bool CohortTransition,
    SimpleDdgiSourceRefreshMode SourceRefreshMode,
    Vector3 SourceRelightScale);

internal readonly record struct DdgiInvalidationCollectionRequest(
    Scene Scene,
    LightFrameSnapshot Lights,
    GlobalIlluminationSettings Settings,
    DdgiFoliageProxyFrame Foliage,
    bool MutationJournalEnabled);

internal readonly record struct DdgiInvalidationFrame(
    IReadOnlyList<DdgiDirtyRegion> DirtyRegions,
    DdgiInvalidationTelemetry Telemetry);

internal readonly record struct DdgiInvalidationTelemetry(
    int VfxDirtyProbeEventCount,
    ulong LastConsumedSerial,
    ulong EnqueuedEventCount,
    ulong CoalescedEventCount,
    ulong OverflowCount,
    ulong ConservativeFallbackCount,
    ulong SceneAttachScanCount,
    ulong SceneAttachObjectCount,
    ulong OracleComparisonCount,
    ulong OracleMismatchCount,
    int PendingEventCount,
    int OutputRegionCount,
    int OverflowedThisFrame);

internal sealed class DdgiInvalidationTelemetryAccumulator
{
    public int VfxDdgiDirtyProbeEventCount { get; set; }
    public ulong SimpleDdgiMutationJournalLastConsumedSerial { get; set; }
    public ulong SimpleDdgiMutationJournalEnqueuedEventCount { get; set; }
    public ulong SimpleDdgiMutationJournalCoalescedEventCount { get; set; }
    public ulong SimpleDdgiMutationJournalOverflowCount { get; set; }
    public ulong SimpleDdgiMutationJournalConservativeFallbackCount { get; set; }
    public ulong SimpleDdgiMutationJournalAttachScanCount { get; set; }
    public ulong SimpleDdgiMutationJournalAttachObjectCount { get; set; }
    public ulong SimpleDdgiMutationJournalOracleComparisonCount { get; set; }
    public ulong SimpleDdgiMutationJournalOracleMismatchCount { get; set; }
    public int SimpleDdgiMutationJournalPendingEventCount { get; set; }
    public int SimpleDdgiMutationJournalOutputRegionCount { get; set; }
    public int SimpleDdgiMutationJournalOverflowedThisFrame { get; set; }

    public void Reset()
    {
        VfxDdgiDirtyProbeEventCount = 0;
        SimpleDdgiMutationJournalLastConsumedSerial = 0UL;
        SimpleDdgiMutationJournalEnqueuedEventCount = 0UL;
        SimpleDdgiMutationJournalCoalescedEventCount = 0UL;
        SimpleDdgiMutationJournalOverflowCount = 0UL;
        SimpleDdgiMutationJournalConservativeFallbackCount = 0UL;
        SimpleDdgiMutationJournalAttachScanCount = 0UL;
        SimpleDdgiMutationJournalAttachObjectCount = 0UL;
        SimpleDdgiMutationJournalOracleComparisonCount = 0UL;
        SimpleDdgiMutationJournalOracleMismatchCount = 0UL;
        SimpleDdgiMutationJournalPendingEventCount = 0;
        SimpleDdgiMutationJournalOutputRegionCount = 0;
        SimpleDdgiMutationJournalOverflowedThisFrame = 0;
    }

    public DdgiInvalidationTelemetry Capture() => new(
        VfxDdgiDirtyProbeEventCount,
        SimpleDdgiMutationJournalLastConsumedSerial,
        SimpleDdgiMutationJournalEnqueuedEventCount,
        SimpleDdgiMutationJournalCoalescedEventCount,
        SimpleDdgiMutationJournalOverflowCount,
        SimpleDdgiMutationJournalConservativeFallbackCount,
        SimpleDdgiMutationJournalAttachScanCount,
        SimpleDdgiMutationJournalAttachObjectCount,
        SimpleDdgiMutationJournalOracleComparisonCount,
        SimpleDdgiMutationJournalOracleMismatchCount,
        SimpleDdgiMutationJournalPendingEventCount,
        SimpleDdgiMutationJournalOutputRegionCount,
        SimpleDdgiMutationJournalOverflowedThisFrame);
}
