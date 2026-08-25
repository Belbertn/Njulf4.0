namespace Njulf.Rendering.Data;

public readonly record struct DdgiAreaLightGpuCounters(
    uint SampleAttemptCount,
    uint SampleAcceptCount,
    uint InvalidPdfCount,
    uint VisibilityRayCount);
