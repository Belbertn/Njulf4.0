using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Complete revision key for the renderer's single-entry DDGI emissive table
/// cache. Scene content covers object membership, geometry handles, visibility,
/// transforms, and instance-batch revisions. Material data covers compiled
/// emission, alpha coverage, sidedness, texture/profile hot reloads, and
/// participation policy.
/// </summary>
public readonly record struct DdgiEmissiveTableCacheKey(
    Guid SceneId,
    ulong SceneContentRevision,
    uint MaterialDataRevision,
    bool TriangleSampling,
    int TriangleBudget);

public readonly record struct DdgiEmissiveTableBuildResult(
    int Count,
    ulong PayloadSignature,
    DdgiEmissiveTriangleTableStats TriangleStats,
    int SkippedSkinnedObjectCount,
    double SkippedSkinnedImportance);

public readonly record struct DdgiEmissiveTableCacheDiagnostics(
    ulong HitCount,
    ulong MissCount,
    ulong RebuildCount,
    ulong InvalidationCount,
    bool LastLookupWasHit,
    bool HasValue);

/// <summary>
/// Bounded, single-entry CPU cache for the GPU emissive source payload.
/// Renderer use is single-threaded. A cache hit copies at most the declared
/// source budget and avoids scene-triangle enumeration, priority selection,
/// and alias-table construction.
/// </summary>
public sealed class DdgiEmissiveTableCache
{
    private readonly GPUDdgiEmissiveSource[] _sources;
    private DdgiEmissiveTableCacheKey _key;
    private DdgiEmissiveTableBuildResult _result;
    private bool _hasValue;
    private ulong _hitCount;
    private ulong _missCount;
    private ulong _rebuildCount;
    private ulong _invalidationCount;
    private bool _lastLookupWasHit;

    public DdgiEmissiveTableCache(int capacity)
    {
        if (capacity <= 0 || capacity > DdgiEmissiveTriangleTable.MaximumAliasEntryCount)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _sources = new GPUDdgiEmissiveSource[capacity];
    }

    public int Capacity => _sources.Length;

    public DdgiEmissiveTableCacheDiagnostics Diagnostics => new(
        _hitCount,
        _missCount,
        _rebuildCount,
        _invalidationCount,
        _lastLookupWasHit,
        _hasValue);

    public bool TryGet(
        DdgiEmissiveTableCacheKey key,
        Span<GPUDdgiEmissiveSource> destination,
        out DdgiEmissiveTableBuildResult result)
    {
        if (!TryGet(key, out result))
            return false;

        CopyPayloadTo(destination);
        return true;
    }

    /// <summary>
    /// Looks up cached build metadata without copying the source payload. The
    /// renderer uses this hot path while the matching payload is already
    /// resident in the persistent GPU buffer.
    /// </summary>
    public bool TryGet(
        DdgiEmissiveTableCacheKey key,
        out DdgiEmissiveTableBuildResult result)
    {
        if (!_hasValue || _key != key)
        {
            _missCount++;
            _lastLookupWasHit = false;
            result = default;
            return false;
        }

        _hitCount++;
        _lastLookupWasHit = true;
        result = _result;
        return true;
    }

    public void CopyPayloadTo(Span<GPUDdgiEmissiveSource> destination)
    {
        if (!_hasValue)
            throw new InvalidOperationException("The DDGI emissive table cache is empty.");
        if (destination.Length < _result.Count)
        {
            throw new ArgumentException(
                $"Destination capacity {destination.Length} is smaller than cached source count {_result.Count}.",
                nameof(destination));
        }

        _sources.AsSpan(0, _result.Count).CopyTo(destination);
    }

    public void Store(
        DdgiEmissiveTableCacheKey key,
        ReadOnlySpan<GPUDdgiEmissiveSource> sources,
        DdgiEmissiveTableBuildResult result)
    {
        if (result.Count < 0 || result.Count > _sources.Length)
            throw new ArgumentOutOfRangeException(nameof(result), $"Source count must be in [0, {_sources.Length}].");
        if (sources.Length < result.Count)
            throw new ArgumentException("Source payload is shorter than the declared build result.", nameof(sources));
        if (!double.IsFinite(result.SkippedSkinnedImportance) || result.SkippedSkinnedImportance < 0.0)
            throw new ArgumentOutOfRangeException(nameof(result), "Skipped skinned importance must be finite and non-negative.");

        if (_hasValue && _key != key)
            _invalidationCount++;

        sources[..result.Count].CopyTo(_sources);
        if (result.Count < _result.Count)
            Array.Clear(_sources, result.Count, _result.Count - result.Count);
        _key = key;
        _result = result;
        _hasValue = true;
        _rebuildCount++;
        _lastLookupWasHit = false;
    }

    public void Clear()
    {
        if (_hasValue)
            _invalidationCount++;
        Array.Clear(_sources);
        _key = default;
        _result = default;
        _hasValue = false;
        _lastLookupWasHit = false;
    }
}

[Flags]
public enum DdgiEmissiveEstimatorOwnership : uint
{
    None = 0,
    DirectSurfaceHit = 1u << 0,
    TriangleNextEvent = 1u << 1,
    ProxyRollbackNextEvent = 1u << 2,
    CachedMultiBounce = 1u << 3
}

/// <summary>
/// Checked-in radiometry and estimator-ownership contract for DDGI emission.
///
/// Njulf interprets glTF emissive factor × linear emissive texture/profile ×
/// emissive strength as scene-linear RGB radiance. Exposure is applied later;
/// direct emitter radiance is not multiplied by albedo, metallic, AO, or 1/PI.
/// Values remain HDR and are bounded only by the finite FP16 storage ceiling.
///
/// DirectSurfaceHit owns a probe ray that terminates on an emitter.
/// TriangleNextEvent (or its mutually-exclusive ProxyRollback fallback) owns
/// emitter-to-receiver direct illumination and includes visibility, geometry
/// terms, and the receiver diffuse BRDF. CachedMultiBounce owns only recursive
/// irradiance sampled at the receiver and is the sole term gated by transport
/// atlas ownership/material AO. These path classes are summed because their
/// segment topology differs; two estimators of one class are never enabled.
/// </summary>
public static class DdgiEmissiveTransportContract
{
    public const float MaximumSceneLinearRadiance = 65504.0f;

    public static Vector3 ResolveDirectSurfaceHitRadiance(Vector3 compiledSceneLinearRadiance)
    {
        if (!float.IsFinite(compiledSceneLinearRadiance.X) ||
            !float.IsFinite(compiledSceneLinearRadiance.Y) ||
            !float.IsFinite(compiledSceneLinearRadiance.Z))
        {
            throw new ArgumentOutOfRangeException(
                nameof(compiledSceneLinearRadiance),
                "Compiled emissive radiance must be finite.");
        }

        return new Vector3(
            Math.Clamp(compiledSceneLinearRadiance.X, 0.0f, MaximumSceneLinearRadiance),
            Math.Clamp(compiledSceneLinearRadiance.Y, 0.0f, MaximumSceneLinearRadiance),
            Math.Clamp(compiledSceneLinearRadiance.Z, 0.0f, MaximumSceneLinearRadiance));
    }

    public static DdgiEmissiveEstimatorOwnership ResolveOwnership(
        bool triangleSampling,
        bool cachedMultiBounce) =>
        DdgiEmissiveEstimatorOwnership.DirectSurfaceHit |
        (triangleSampling
            ? DdgiEmissiveEstimatorOwnership.TriangleNextEvent
            : DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent) |
        (cachedMultiBounce
            ? DdgiEmissiveEstimatorOwnership.CachedMultiBounce
            : DdgiEmissiveEstimatorOwnership.None);

    public static bool IsValid(DdgiEmissiveEstimatorOwnership ownership)
    {
        const DdgiEmissiveEstimatorOwnership nextEventMask =
            DdgiEmissiveEstimatorOwnership.TriangleNextEvent |
            DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent;
        DdgiEmissiveEstimatorOwnership nextEvent = ownership & nextEventMask;
        bool hasExactlyOneNextEvent =
            nextEvent is DdgiEmissiveEstimatorOwnership.TriangleNextEvent or
                DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent;
        return (ownership & DdgiEmissiveEstimatorOwnership.DirectSurfaceHit) != 0 &&
               hasExactlyOneNextEvent &&
               (ownership & ~(
                   DdgiEmissiveEstimatorOwnership.DirectSurfaceHit |
                   nextEventMask |
                   DdgiEmissiveEstimatorOwnership.CachedMultiBounce)) == 0;
    }
}
