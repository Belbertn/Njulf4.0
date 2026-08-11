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
    string AdmissionStatus);
