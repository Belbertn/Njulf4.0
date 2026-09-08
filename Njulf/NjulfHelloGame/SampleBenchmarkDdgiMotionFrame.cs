using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>Retains scroll and clear events even when the final frame is stable.</summary>
public sealed record SampleBenchmarkDdgiMotionFrame(
    int MeasurementFrame,
    float CameraY,
    float NearOriginY,
    int NearScrollDeltaY,
    bool Recentered,
    bool AtlasCleared,
    int DeferredCascades,
    int ExpectedScrollProbes,
    int AcceptedScrollProbes,
    int TracedScrollProbes,
    int CommittedScrollProbes,
    string CohortFailure,
    string CommitFailureBreakdown,
    int PendingSolverProbes,
    SimpleDdgiVolumeRemapKind RemapKind,
    ulong StorageFingerprint,
    SimpleDdgiCapacityTiming? CapacityChange,
    IReadOnlyList<SimpleDdgiTransportCacheRegion> ChangedCacheRegions)
{
    public static SampleBenchmarkDdgiMotionFrame Capture(RendererDiagnostics d, int frame)
    {
        DdgiVolumeDiagnosticsEntry? near = d.DdgiVolumes.FirstOrDefault(
            v => v.Kind == SimpleDdgiVolumeKind.CameraRing && v.CascadeIndex == 0);
        bool changed = !d.SimpleDdgiUploadTiming.CapacityDetails.StableKeyHit;
        return new(
            frame, d.CaptureCamera.PositionY, near?.OriginY ?? 0f,
            near?.ScrollCellDeltaY ?? 0, d.SimpleDdgiRecentered != 0,
            d.SimpleDdgiAtlasCleared != 0, d.SimpleDdgiScrollDeferredCascadeCount,
            d.SimpleDdgiScrollRepairExpectedProbeCount,
            (int)d.SimpleDdgiScrollGpuAcceptedCount,
            (int)d.SimpleDdgiScrollGpuTracedCount,
            (int)d.SimpleDdgiScrollGpuCommittedCount,
            d.SimpleDdgiScrollCohortFailure.ToString(),
            d.SimpleDdgiSchedulerCommitFailureBreakdown,
            d.SimpleDdgiTransportPendingSolverProbeCount,
            d.SimpleDdgiVolumeRemapKind, d.SimpleDdgiStorage.StorageLayoutFingerprint,
            changed ? d.SimpleDdgiUploadTiming.CapacityDetails : null,
            changed ? d.SimpleDdgiStorage.CacheRegions : Array.Empty<SimpleDdgiTransportCacheRegion>());
    }
}
