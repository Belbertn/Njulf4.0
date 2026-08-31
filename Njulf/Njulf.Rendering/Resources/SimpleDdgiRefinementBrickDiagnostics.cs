namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiRefinementBrickDiagnostics(
    bool Requested,
    int RequestedBrickCount,
    int AdmittedBrickCount,
    int ReceiverReadyBrickCount,
    int BaseFallbackBrickCount,
    int AllocatedProbeCount,
    ulong EvictionCount,
    bool TopologyChangedThisFrame,
    string AdmissionStatus)
{
    /// <summary>
    /// Receiver composition weight of a completely certified refinement field.
    /// Zero means exact base fallback; one means the fine field has completed
    /// its bounded publication handoff.
    /// </summary>
    public float ReceiverBlendWeight { get; init; }

    /// <summary>
    /// Bit <c>n</c> is set when refinement slot <c>n</c> changed physical
    /// ownership this frame and must independently rebuild receiver authority.
    /// </summary>
    public uint ChangedSlotMask { get; init; }

    /// <summary>Bit mask of refinement slots currently backed by a live brick.</summary>
    public uint ActiveSlotMask { get; init; }
}
