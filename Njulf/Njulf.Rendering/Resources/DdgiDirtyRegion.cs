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
    }

}
