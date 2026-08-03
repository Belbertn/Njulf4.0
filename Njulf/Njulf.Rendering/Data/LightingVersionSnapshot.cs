namespace Njulf.Rendering.Data;

/// <summary>Read-only cross-system version view; mutation remains with each owning subsystem.</summary>
public readonly record struct LightingVersionSnapshot(
    uint VisualEnvironmentGeneration,
    uint RequestedSpecularEnvironmentGeneration,
    uint PublishedSpecularEnvironmentGeneration,
    uint RequestedGiEnvironmentGeneration,
    uint AdmittedGiEnvironmentGeneration,
    uint CompletedGiSourceCohortGeneration,
    uint StaticGiConvergedGeneration,
    ulong SceneRadianceRevision);
