using System;
using System.Collections.Generic;
using System.Numerics;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using CoreBoundingBox = Njulf.Core.Math.BoundingBox;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Rendering.Resources;

public interface IDdgiMaterialMutationSource
{
    event Action<MaterialChangedEvent>? MaterialChanged;
}

public interface IDdgiLightMutationSource
{
    event Action<LightMutation>? Changed;
}

/// <summary>Optional renderer-owned spatial resolution for a scene mutation.</summary>
public readonly record struct DdgiMutationResolution(
    CoreBoundingBox? OldWorldBounds,
    CoreBoundingBox? NewWorldBounds,
    CoreBoundingBox? InfluenceBounds,
    DdgiDirtyReason Reason,
    uint Priority = 0,
    bool Ignore = false,
    bool IgnoreWhenUntracked = false)
{
    public static DdgiMutationResolution Ignored { get; } =
        new(null, null, null, DdgiDirtyReason.Unknown, Ignore: true);
}

public readonly record struct DdgiMutationJournalTelemetry(
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
    int LastOutputRegionCount,
    bool OverflowedThisFrame)
{
    public static DdgiMutationJournalTelemetry Empty { get; } = default;
}

/// <summary>
/// Bounded producer-written GI mutation journal. Ordinary frames do no scene
/// enumeration; additions, transforms, material edits, lights, and VFX state
/// changes enqueue fixed-size records at the point where the edit is known.
/// </summary>
public sealed class DdgiMutationJournal : IDisposable
{
    public const int DefaultEventCapacity = 4096;
    public const int DefaultOutputCapacity =
        SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity;
    public const float DefaultCoalescingBrickSize = 8.0f;

    private readonly object _gate = new();
    private readonly SceneMutation[] _events;
    private readonly List<DdgiDirtyRegion> _output;
    private readonly Dictionary<CoalescingKey, int> _coalesced;
    private readonly Dictionary<MaterialHandle, HashSet<RenderObject>>
        _materialUsers = new();
    private readonly Dictionary<Guid, CoreBoundingBox> _lastKnownBounds = new();
    private readonly IDdgiMaterialMutationSource _materialManager;
    private readonly IDdgiLightMutationSource _lightManager;
    private readonly int _outputCapacity;
    private readonly float _brickSize;
    private Scene? _scene;
    private int _eventCount;
    private bool _overflowed;
    private bool _disposed;
    private ulong _syntheticSerial;
    private ulong _lastConsumedSerial;
    private ulong _enqueuedEventCount;
    private ulong _coalescedEventCount;
    private ulong _overflowCount;
    private ulong _conservativeFallbackCount;
    private ulong _sceneAttachScanCount;
    private ulong _sceneAttachObjectCount;
    private ulong _oracleComparisonCount;
    private ulong _oracleMismatchCount;
    private int _lastOutputRegionCount;
    private bool _overflowedLastDrain;

    public DdgiMutationJournal(
        IDdgiMaterialMutationSource materialManager,
        IDdgiLightMutationSource lightManager,
        int eventCapacity = DefaultEventCapacity,
        int outputCapacity = DefaultOutputCapacity,
        float coalescingBrickSize = DefaultCoalescingBrickSize)
    {
        _materialManager = materialManager ??
            throw new ArgumentNullException(nameof(materialManager));
        _lightManager = lightManager ??
            throw new ArgumentNullException(nameof(lightManager));
        if (eventCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(eventCapacity));
        if (outputCapacity <= 0 ||
            outputCapacity > SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(outputCapacity));
        }
        if (!float.IsFinite(coalescingBrickSize) || coalescingBrickSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coalescingBrickSize));

        _events = new SceneMutation[eventCapacity];
        _outputCapacity = outputCapacity;
        _brickSize = coalescingBrickSize;
        _output = new List<DdgiDirtyRegion>(outputCapacity);
        _coalesced = new Dictionary<CoalescingKey, int>(outputCapacity);
        _materialManager.MaterialChanged += OnMaterialChanged;
        _lightManager.Changed += OnLightChanged;
    }

    public DdgiMutationJournalTelemetry Telemetry
    {
        get
        {
            lock (_gate)
            {
                return new DdgiMutationJournalTelemetry(
                    _lastConsumedSerial,
                    _enqueuedEventCount,
                    _coalescedEventCount,
                    _overflowCount,
                    _conservativeFallbackCount,
                    _sceneAttachScanCount,
                    _sceneAttachObjectCount,
                    _oracleComparisonCount,
                    _oracleMismatchCount,
                    _eventCount,
                    _lastOutputRegionCount,
                    _overflowedLastDrain);
            }
        }
    }

    /// <summary>
    /// Attaches to a scene and performs the one allowed bootstrap enumeration.
    /// Subsequent unchanged frames consume no render-object or VFX comparisons.
    /// </summary>
    public void AttachScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_scene, scene))
                return;

            DetachSceneLocked();
            _scene = scene;
            scene.Mutated += OnSceneMutated;
            scene.RenderObjectMutated += OnRenderObjectMutated;
            _sceneAttachScanCount = SaturatingIncrement(_sceneAttachScanCount);

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                TrackMaterialUserLocked(renderObject, renderObject.Material);
                CoreBoundingBox? bounds = TryGetBounds(renderObject);
                EnqueueLocked(new SceneMutation(
                    NextSyntheticSerialLocked(),
                    renderObject.Id,
                    renderObject,
                    SceneMutationKind.Added | SceneMutationKind.Geometry,
                    null,
                    bounds,
                    renderObject.Revision));
                _sceneAttachObjectCount = SaturatingIncrement(
                    _sceneAttachObjectCount);
            }

            foreach (ParticleEffectInstance particle in scene.ParticleEffects)
            {
                EnqueueLocked(new SceneMutation(
                    NextSyntheticSerialLocked(),
                    particle.Id,
                    particle,
                    SceneMutationKind.Added | SceneMutationKind.ParticleState,
                    null,
                    null,
                    particle.Version));
            }

            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                EnqueueLocked(new SceneMutation(
                    NextSyntheticSerialLocked(),
                    batch.Id,
                    batch,
                    SceneMutationKind.Added | SceneMutationKind.StaticInstances,
                    null,
                    null,
                    batch.Revision));
            }

            foreach (Njulf.Core.Foliage.FoliagePatch patch in scene.FoliagePatches)
            {
                EnqueueLocked(new SceneMutation(
                    NextSyntheticSerialLocked(),
                    patch.Id,
                    patch,
                    SceneMutationKind.Added | SceneMutationKind.Foliage,
                    null,
                    patch.Bounds,
                    patch.ContentRevision));
            }
        }
    }

    public void DetachScene()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DetachSceneLocked();
        }
    }

    public IReadOnlyList<DdgiDirtyRegion> Drain(
        CoreBoundingBox conservativeSceneBounds,
        Func<SceneMutation, DdgiMutationResolution>? resolver = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _output.Clear();
            _coalesced.Clear();
            _overflowedLastDrain = _overflowed;

            if (_overflowed)
            {
                AddConservativeFallbackLocked(conservativeSceneBounds);
                ResetPendingLocked();
                return _output;
            }

            for (int eventIndex = 0; eventIndex < _eventCount; eventIndex++)
            {
                SceneMutation mutation = _events[eventIndex];
                _lastConsumedSerial = Math.Max(
                    _lastConsumedSerial,
                    mutation.Serial);
                DdgiMutationResolution resolution =
                    ResolveDefault(mutation);
                if (resolver != null)
                {
                    DdgiMutationResolution resolved = resolver(mutation);
                    resolution = MergeResolution(resolution, resolved);
                }

                if (resolution.Ignore)
                    continue;

                if (resolution.OldWorldBounds is null &&
                    _lastKnownBounds.TryGetValue(
                        mutation.ProducerId,
                        out CoreBoundingBox previousBounds))
                {
                    resolution = resolution with
                    {
                        OldWorldBounds = previousBounds
                    };
                }

                if (resolution.IgnoreWhenUntracked &&
                    resolution.OldWorldBounds is null &&
                    resolution.NewWorldBounds is null &&
                    resolution.InfluenceBounds is null)
                {
                    continue;
                }

                if (mutation.Kind.HasFlag(SceneMutationKind.Global) ||
                    resolution.OldWorldBounds is null &&
                    resolution.NewWorldBounds is null &&
                    resolution.InfluenceBounds is null)
                {
                    if (mutation.Kind.HasFlag(SceneMutationKind.Global))
                        _lastKnownBounds.Clear();
                    AddConservativeFallbackLocked(conservativeSceneBounds);
                    ResetPendingLocked();
                    return _output;
                }

                CoreBoundingBox oldBounds = resolution.OldWorldBounds ??
                    resolution.NewWorldBounds ??
                    resolution.InfluenceBounds!.Value;
                CoreBoundingBox newBounds = resolution.NewWorldBounds ??
                    resolution.OldWorldBounds ??
                    resolution.InfluenceBounds!.Value;
                CoreBoundingBox swept = Union(oldBounds, newBounds);
                CoreBoundingBox influence = resolution.InfluenceBounds ??
                    Expand(swept, ResolveDefaultPadding(resolution.Reason));
                influence = Union(influence, swept);

                if (mutation.Kind.HasFlag(SceneMutationKind.Removed))
                {
                    _lastKnownBounds.Remove(mutation.ProducerId);
                }
                else if (resolution.NewWorldBounds is { } currentBounds)
                {
                    _lastKnownBounds[mutation.ProducerId] = currentBounds;
                }

                var region = new DdgiDirtyRegion(
                    swept,
                    resolution.Reason)
                {
                    OldWorldBounds = oldBounds,
                    NewWorldBounds = newBounds,
                    InfluenceBounds = influence,
                    ReasonFlags = 1u << (int)resolution.Reason,
                    Priority = resolution.Priority,
                    SourceRevision = mutation.ContentRevision,
                    SourceIdentifier = StableIdentifier(mutation.ProducerId)
                };

                if (!TryCoalesceLocked(region))
                {
                    _overflowCount = SaturatingIncrement(_overflowCount);
                    _overflowedLastDrain = true;
                    AddConservativeFallbackLocked(conservativeSceneBounds);
                    ResetPendingLocked();
                    return _output;
                }
            }

            ResetPendingLocked();
            _lastOutputRegionCount = _output.Count;
            return _output;
        }
    }

    public void RecordOracleComparison(bool equal)
    {
        lock (_gate)
        {
            _oracleComparisonCount = SaturatingIncrement(_oracleComparisonCount);
            if (!equal)
                _oracleMismatchCount = SaturatingIncrement(_oracleMismatchCount);
        }
    }

    private void OnSceneMutated(SceneMutation mutation)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (mutation.Producer is RenderObject renderObject)
            {
                if (mutation.Kind.HasFlag(SceneMutationKind.Added))
                    TrackMaterialUserLocked(renderObject, renderObject.Material);
                else if (mutation.Kind.HasFlag(SceneMutationKind.Removed))
                    UntrackMaterialUserLocked(renderObject, renderObject.Material);
            }
            EnqueueLocked(mutation);
        }
    }

    private void OnRenderObjectMutated(RenderObjectMutation mutation)
    {
        if (!mutation.Kind.HasFlag(SceneMutationKind.Material))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            UntrackMaterialUserLocked(mutation.Source, mutation.OldResource);
            TrackMaterialUserLocked(mutation.Source, mutation.NewResource);
        }
    }

    private void OnMaterialChanged(MaterialChangedEvent changed)
    {
        lock (_gate)
        {
            if (_disposed || _scene is null ||
                !_materialUsers.TryGetValue(changed.Handle, out HashSet<RenderObject>? users))
            {
                return;
            }

            DdgiDirtyReason reason = changed.ChangeMask.HasFlag(
                    MaterialChangeMask.Emission)
                ? DdgiDirtyReason.EmissiveChanged
                : DdgiDirtyReason.MaterialChanged;
            foreach (RenderObject renderObject in users)
            {
                CoreBoundingBox? bounds = TryGetBounds(renderObject);
                EnqueueLocked(new SceneMutation(
                    NextSyntheticSerialLocked(),
                    renderObject.Id,
                    renderObject,
                    SceneMutationKind.Material |
                    (reason == DdgiDirtyReason.EmissiveChanged
                        ? SceneMutationKind.Emission
                        : SceneMutationKind.None),
                    bounds,
                    bounds,
                    MaximumRevision(changed.Revisions)));
            }
        }
    }

    private void OnLightChanged(LightMutation changed)
    {
        lock (_gate)
        {
            if (_disposed || _scene is null)
                return;

            Light? previous = changed.Previous;
            Light? current = changed.Current;
            bool directional = previous?.Type == LightType.Directional ||
                current?.Type == LightType.Directional ||
                changed.Kind == LightMutationKind.Cleared;
            SceneMutationKind kind = SceneMutationKind.Content;
            CoreBoundingBox? oldBounds = previous is { } oldLight &&
                oldLight.Type != LightType.Directional
                    ? CreateLightBounds(oldLight)
                    : null;
            CoreBoundingBox? newBounds = current is { } newLight &&
                newLight.Type != LightType.Directional
                    ? CreateLightBounds(newLight)
                    : null;
            if (directional)
                kind |= SceneMutationKind.Global;

            EnqueueLocked(new SceneMutation(
                NextSyntheticSerialLocked(),
                changed.Id,
                new LightMutationProducer(changed.Id),
                kind,
                oldBounds,
                newBounds,
                changed.Revision));
        }
    }

    private void EnqueueLocked(SceneMutation mutation)
    {
        if (_overflowed)
            return;
        if (_eventCount >= _events.Length)
        {
            _overflowed = true;
            _overflowCount = SaturatingIncrement(_overflowCount);
            return;
        }
        _events[_eventCount++] = mutation;
        _enqueuedEventCount = SaturatingIncrement(_enqueuedEventCount);
    }

    private bool TryCoalesceLocked(in DdgiDirtyRegion region)
    {
        CoalescingKey key = CreateCoalescingKey(region);
        if (_coalesced.TryGetValue(key, out int outputIndex))
        {
            DdgiDirtyRegion existing = _output[outputIndex];
            _output[outputIndex] = existing with
            {
                Bounds = Union(existing.Bounds, region.Bounds),
                OldWorldBounds = Union(existing.OldWorldBounds, region.OldWorldBounds),
                NewWorldBounds = Union(existing.NewWorldBounds, region.NewWorldBounds),
                InfluenceBounds = Union(existing.InfluenceBounds, region.InfluenceBounds),
                ReasonFlags = existing.ReasonFlags | region.ReasonFlags,
                Priority = Math.Max(existing.Priority, region.Priority),
                SourceRevision = Math.Max(existing.SourceRevision, region.SourceRevision),
                SourceIdentifier = existing.SourceIdentifier == region.SourceIdentifier
                    ? existing.SourceIdentifier
                    : 0UL
            };
            _coalescedEventCount = SaturatingIncrement(_coalescedEventCount);
            return true;
        }

        if (_output.Count >= _outputCapacity)
            return false;
        _coalesced.Add(key, _output.Count);
        _output.Add(region);
        return true;
    }

    private CoalescingKey CreateCoalescingKey(in DdgiDirtyRegion region)
    {
        CoreVector3 center = region.InfluenceBounds.Center;
        return new CoalescingKey(
            ToBrick(center.X),
            ToBrick(center.Y),
            ToBrick(center.Z),
            ToReasonClass(region.Reason));
    }

    private int ToBrick(float value)
    {
        if (!float.IsFinite(value))
            return 0;
        double brick = Math.Floor(value / _brickSize);
        return brick <= int.MinValue
            ? int.MinValue
            : brick >= int.MaxValue
                ? int.MaxValue
                : (int)brick;
    }

    private void AddConservativeFallbackLocked(CoreBoundingBox sceneBounds)
    {
        _output.Clear();
        _coalesced.Clear();
        _output.Add(new DdgiDirtyRegion(sceneBounds, DdgiDirtyReason.Teleport)
        {
            OldWorldBounds = sceneBounds,
            NewWorldBounds = sceneBounds,
            InfluenceBounds = sceneBounds,
            ReasonFlags = uint.MaxValue,
            Priority = uint.MaxValue,
            SourceRevision = _lastConsumedSerial
        });
        _conservativeFallbackCount = SaturatingIncrement(
            _conservativeFallbackCount);
        _lastOutputRegionCount = 1;
    }

    private void ResetPendingLocked()
    {
        Array.Clear(_events, 0, _eventCount);
        _eventCount = 0;
        _overflowed = false;
    }

    private static DdgiMutationResolution ResolveDefault(SceneMutation mutation)
    {
        DdgiDirtyReason reason;
        uint priority = 1u;
        SceneMutationKind kind = mutation.Kind;
        if (kind.HasFlag(SceneMutationKind.Removed))
            reason = DdgiDirtyReason.GeometryRemoved;
        else if (kind.HasFlag(SceneMutationKind.Added))
            reason = DdgiDirtyReason.GeometryAdded;
        else if (kind.HasFlag(SceneMutationKind.Transform))
            reason = DdgiDirtyReason.TransformChanged;
        else if (kind.HasFlag(SceneMutationKind.ParticleState))
            reason = DdgiDirtyReason.EmissiveChanged;
        else if (kind.HasFlag(SceneMutationKind.Emission))
            reason = DdgiDirtyReason.EmissiveChanged;
        else if (kind.HasFlag(SceneMutationKind.Material))
            reason = DdgiDirtyReason.MaterialChanged;
        else if (kind.HasFlag(SceneMutationKind.Visibility) ||
                 kind.HasFlag(SceneMutationKind.StaticInstances) ||
                 kind.HasFlag(SceneMutationKind.Foliage) ||
                 kind.HasFlag(SceneMutationKind.Geometry))
            reason = DdgiDirtyReason.TransformChanged;
        else
            reason = DdgiDirtyReason.LocalLightChanged;

        if (kind.HasFlag(SceneMutationKind.Global))
            priority = uint.MaxValue;
        return new DdgiMutationResolution(
            mutation.OldWorldBounds,
            mutation.NewWorldBounds,
            null,
            reason,
            priority);
    }

    private static DdgiMutationResolution MergeResolution(
        DdgiMutationResolution fallback,
        DdgiMutationResolution resolved) =>
        new(
            resolved.OldWorldBounds ?? fallback.OldWorldBounds,
            resolved.NewWorldBounds ?? fallback.NewWorldBounds,
            resolved.InfluenceBounds ?? fallback.InfluenceBounds,
            resolved.Reason == DdgiDirtyReason.Unknown
                ? fallback.Reason
                : resolved.Reason,
            Math.Max(fallback.Priority, resolved.Priority),
            resolved.Ignore,
            resolved.IgnoreWhenUntracked);

    private static CoreBoundingBox? TryGetBounds(RenderObject renderObject) =>
        renderObject.LocalMeshBounds is { } local
            ? CoreBoundingBox.Transform(local, renderObject.WorldMatrix)
            : null;

    private static CoreBoundingBox CreateLightBounds(in Light light)
    {
        float range = float.IsFinite(light.Range)
            ? Math.Max(0.0f, light.Range)
            : 0.0f;
        var center = new CoreVector3(
            light.Position.X,
            light.Position.Y,
            light.Position.Z);
        var extent = new CoreVector3(range);
        return new CoreBoundingBox(center - extent, center + extent);
    }

    private void TrackMaterialUserLocked(RenderObject renderObject, object? material)
    {
        if (material is not MaterialHandle handle || !handle.IsValid)
            return;
        if (!_materialUsers.TryGetValue(handle, out HashSet<RenderObject>? users))
        {
            users = new HashSet<RenderObject>();
            _materialUsers.Add(handle, users);
        }
        users.Add(renderObject);
    }

    private void UntrackMaterialUserLocked(RenderObject renderObject, object? material)
    {
        if (material is not MaterialHandle handle ||
            !_materialUsers.TryGetValue(handle, out HashSet<RenderObject>? users))
        {
            return;
        }
        users.Remove(renderObject);
        if (users.Count == 0)
            _materialUsers.Remove(handle);
    }

    private void DetachSceneLocked()
    {
        if (_scene != null)
        {
            _scene.Mutated -= OnSceneMutated;
            _scene.RenderObjectMutated -= OnRenderObjectMutated;
            _scene = null;
        }
        _materialUsers.Clear();
        _lastKnownBounds.Clear();
        ResetPendingLocked();
    }

    private ulong NextSyntheticSerialLocked()
    {
        _syntheticSerial = _syntheticSerial == ulong.MaxValue
            ? 1UL
            : _syntheticSerial + 1UL;
        return _syntheticSerial;
    }

    private static ulong MaximumRevision(MaterialAspectRevisions revisions) =>
        Math.Max(
            Math.Max(
                Math.Max(revisions.Material, revisions.DiffuseTransport),
                Math.Max(revisions.Emission, revisions.AlphaCoverage)),
            Math.Max(
                Math.Max(revisions.Sidedness, revisions.ShadingModel),
                revisions.FarField));

    private static CoreBoundingBox Union(
        CoreBoundingBox left,
        CoreBoundingBox right) =>
        new(
            new CoreVector3(
                MathF.Min(left.Min.X, right.Min.X),
                MathF.Min(left.Min.Y, right.Min.Y),
                MathF.Min(left.Min.Z, right.Min.Z)),
            new CoreVector3(
                MathF.Max(left.Max.X, right.Max.X),
                MathF.Max(left.Max.Y, right.Max.Y),
                MathF.Max(left.Max.Z, right.Max.Z)));

    private static CoreBoundingBox Expand(CoreBoundingBox bounds, float padding)
    {
        var extent = new CoreVector3(MathF.Max(0.0f, padding));
        return new CoreBoundingBox(bounds.Min - extent, bounds.Max + extent);
    }

    private static float ResolveDefaultPadding(DdgiDirtyReason reason) =>
        reason == DdgiDirtyReason.DirectionalLightChanged ? 4.0f : 1.0f;

    private static uint ToReasonClass(DdgiDirtyReason reason) => reason switch
    {
        DdgiDirtyReason.LocalLightChanged or
        DdgiDirtyReason.DirectionalLightChanged => 1u,
        DdgiDirtyReason.EmissiveChanged => 2u,
        DdgiDirtyReason.MaterialChanged => 3u,
        _ => 4u
    };

    private static ulong StableIdentifier(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        ulong low = BitConverter.ToUInt64(bytes);
        ulong high = BitConverter.ToUInt64(bytes[8..]);
        ulong value = low ^ BitOperations.RotateLeft(high, 29);
        return value == 0UL ? 1UL : value;
    }

    private static ulong SaturatingIncrement(ulong value) =>
        value == ulong.MaxValue ? value : value + 1UL;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            DetachSceneLocked();
            _materialManager.MaterialChanged -= OnMaterialChanged;
            _lightManager.Changed -= OnLightChanged;
            _disposed = true;
        }
    }

    private readonly record struct CoalescingKey(
        int X,
        int Y,
        int Z,
        uint ReasonClass);

    private sealed class LightMutationProducer(Guid id) : IIdentifiedSceneEntity
    {
        public Guid Id { get; } = id == Guid.Empty ? Guid.Empty : id;
    }
}
