using Njulf.Core.Math;

namespace Njulf.Rendering.Resources
{
    public enum DdgiDirtyReason : uint
    {
        Unknown = 0,
        GeometryAdded = 1,
        GeometryRemoved = 2,
        TransformChanged = 3,
        MaterialChanged = 4,
        EmissiveChanged = 5,
        LocalLightChanged = 6,
        DirectionalLightChanged = 7,
        StreamIn = 8,
        StreamOut = 9,
        Teleport = 10,
        AgeRefresh = 11
    }

    /// <summary>
    /// A bounded GI invalidation event.  The positional fields are retained for
    /// existing scheduler callers; the additional metadata makes the event usable
    /// by both the legacy scheduler and Simple DDGI's regional CPU scheduler.
    /// </summary>
    public readonly record struct DdgiDirtyRegion(
        BoundingBox Bounds,
        DdgiDirtyReason Reason)
    {
        /// <summary>Bounds before a move/removal, when known.</summary>
        public BoundingBox OldWorldBounds { get; init; } = Bounds;
        /// <summary>Bounds after a move/addition, when known.</summary>
        public BoundingBox NewWorldBounds { get; init; } = Bounds;
        /// <summary>Bounds expanded by the source's lighting/transport influence.</summary>
        public BoundingBox InfluenceBounds { get; init; } = Bounds;
        /// <summary>Bitwise reason representation for telemetry and GPU consumers.</summary>
        public uint ReasonFlags { get; init; } = 1u << (int)Reason;
        /// <summary>Higher values are serviced before lower values within a bounded budget.</summary>
        public uint Priority { get; init; }
        /// <summary>Stable revision of the source which emitted this event.</summary>
        public ulong SourceRevision { get; init; }
        /// <summary>Stable source identifier; zero is valid when the source is anonymous.</summary>
        public ulong SourceIdentifier { get; init; }
        /// <summary>
        /// True only for the one scene-attachment enumeration. Mixed bootstrap
        /// and runtime regions are runtime work so qualification percentiles
        /// cannot be satisfied by startup samples.
        /// </summary>
        public bool IsBootstrap { get; init; }
    }

    /// <summary>
    /// Holds only stable-identity transform invalidations while Transport V2 is
    /// auditing its immutable source-cache operator. Repeated animation events
    /// are coalesced into one swept region per source and released as soon as the
    /// audit leaves its frozen phase. Unsupported events and capacity overflow
    /// fail closed by releasing all retained work immediately.
    /// </summary>
    internal sealed class SimpleDdgiFrozenTailInvalidationBuffer
    {
        internal const int MaximumDeferredSourceCount = 1_024;
        internal const ulong MaximumDeferredFrameCount = 2UL;

        private readonly List<DdgiDirtyRegion> _deferred =
            new(MaximumDeferredSourceCount);
        private readonly List<DdgiDirtyRegion> _releaseScratch =
            new(MaximumDeferredSourceCount);
        private ulong _firstDeferredFrameSerial;
        private ulong _syntheticFrameSerial;

        public int DeferredCount => _deferred.Count;
        public bool DeferredCurrentFrame { get; private set; }
        public bool ReleasedDeferredThisFrame { get; private set; }
        public bool AuditInvalidatedThisFrame { get; private set; }

        public IReadOnlyList<DdgiDirtyRegion>? Resolve(
            bool auditFrozen,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            ulong frameSerial = 0UL)
        {
            DeferredCurrentFrame = false;
            ReleasedDeferredThisFrame = false;
            AuditInvalidatedThisFrame = false;
            _releaseScratch.Clear();

            ulong currentFrameSerial = frameSerial != 0UL
                ? frameSerial
                : ++_syntheticFrameSerial;

            if (!auditFrozen)
                return ReleaseWith(dirtyRegions);
            if (_deferred.Count > 0 &&
                currentFrameSerial >= _firstDeferredFrameSerial &&
                currentFrameSerial - _firstDeferredFrameSerial >=
                    MaximumDeferredFrameCount)
            {
                AuditInvalidatedThisFrame = true;
                return ReleaseWith(dirtyRegions);
            }
            if (dirtyRegions == null || dirtyRegions.Count == 0)
                return null;

            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                if (!CanDefer(dirtyRegions[i]))
                {
                    AuditInvalidatedThisFrame = true;
                    return ReleaseWith(dirtyRegions);
                }
            }

            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                DdgiDirtyRegion current = dirtyRegions[i];
                int existingIndex = FindDeferredSource(current.SourceIdentifier);
                if (existingIndex >= 0)
                {
                    _deferred[existingIndex] = Merge(
                        _deferred[existingIndex],
                        current);
                    continue;
                }

                if (_deferred.Count >= MaximumDeferredSourceCount)
                {
                    AuditInvalidatedThisFrame = true;
                    return ReleaseWith(dirtyRegions);
                }
                if (_deferred.Count == 0)
                    _firstDeferredFrameSerial = currentFrameSerial;
                _deferred.Add(current);
            }

            DeferredCurrentFrame = true;
            return null;
        }

        internal static bool CanDefer(in DdgiDirtyRegion region) =>
            region.Reason == DdgiDirtyReason.TransformChanged &&
            region.SourceIdentifier != 0UL &&
            IsFinite(region.Bounds) &&
            IsFinite(region.OldWorldBounds) &&
            IsFinite(region.NewWorldBounds) &&
            IsFinite(region.InfluenceBounds);

        internal static DdgiDirtyRegion Merge(
            in DdgiDirtyRegion previous,
            in DdgiDirtyRegion current)
        {
            BoundingBox influence = Union(
                previous.InfluenceBounds,
                current.InfluenceBounds);
            return new DdgiDirtyRegion(
                influence,
                DdgiDirtyReason.TransformChanged)
            {
                OldWorldBounds = previous.OldWorldBounds,
                NewWorldBounds = current.NewWorldBounds,
                InfluenceBounds = influence,
                ReasonFlags = previous.ReasonFlags |
                    current.ReasonFlags |
                    1u << (int)DdgiDirtyReason.TransformChanged,
                Priority = Math.Max(previous.Priority, current.Priority),
                SourceRevision = Math.Max(
                    previous.SourceRevision,
                    current.SourceRevision),
                SourceIdentifier = previous.SourceIdentifier,
                IsBootstrap = previous.IsBootstrap && current.IsBootstrap
            };
        }

        private IReadOnlyList<DdgiDirtyRegion>? ReleaseWith(
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions)
        {
            if (_deferred.Count == 0)
                return dirtyRegions;

            _releaseScratch.AddRange(_deferred);
            if (dirtyRegions != null)
            {
                for (int i = 0; i < dirtyRegions.Count; i++)
                    _releaseScratch.Add(dirtyRegions[i]);
            }
            _deferred.Clear();
            _firstDeferredFrameSerial = 0UL;
            ReleasedDeferredThisFrame = true;
            return _releaseScratch;
        }

        private int FindDeferredSource(ulong sourceIdentifier)
        {
            for (int i = 0; i < _deferred.Count; i++)
            {
                if (_deferred[i].SourceIdentifier == sourceIdentifier)
                    return i;
            }
            return -1;
        }

        private static bool IsFinite(in BoundingBox bounds) =>
            IsFinite(bounds.Min.X) &&
            IsFinite(bounds.Min.Y) &&
            IsFinite(bounds.Min.Z) &&
            IsFinite(bounds.Max.X) &&
            IsFinite(bounds.Max.Y) &&
            IsFinite(bounds.Max.Z);

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static BoundingBox Union(
            in BoundingBox left,
            in BoundingBox right) =>
            new(
                new(
                    MathF.Min(left.Min.X, right.Min.X),
                    MathF.Min(left.Min.Y, right.Min.Y),
                    MathF.Min(left.Min.Z, right.Min.Z)),
                new(
                    MathF.Max(left.Max.X, right.Max.X),
                    MathF.Max(left.Max.Y, right.Max.Y),
                    MathF.Max(left.Max.Z, right.Max.Z)));
    }

}
