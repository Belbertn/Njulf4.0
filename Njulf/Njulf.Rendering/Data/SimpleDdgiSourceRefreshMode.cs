namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Describes the cheapest source-cache refresh which is still exact for a
    /// lighting generation. Values 0..3 are also the two-bit shader ABI carried
    /// in a probe update; <see cref="None"/> is CPU-only.
    /// </summary>
    public enum SimpleDdgiSourceRefreshMode : uint
    {
        /// <summary>
        /// Geometry, direction, opacity, material provenance, or an otherwise
        /// inseparable lighting input changed. Rebuild the complete source path.
        /// </summary>
        FullTrace = 0,

        /// <summary>
        /// Only environment radiance changed. Re-evaluate cached misses while
        /// retaining every cached surface record and visibility result.
        /// </summary>
        EnvironmentMissRelight = 1,

        /// <summary>
        /// Cached first-hit geometry is valid, but hit lighting must be
        /// re-evaluated. Reserved for the visibility-transfer path.
        /// </summary>
        CachedHitRelight = 2,

        /// <summary>
        /// Copy source records whose cached segments do not intersect a typed
        /// mutation and retrace the conservative intersecting subset.
        /// </summary>
        SegmentSelective = 3,

        /// <summary>No new source generation is pending.</summary>
        None = uint.MaxValue
    }
}
