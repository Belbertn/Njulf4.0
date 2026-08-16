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
}
