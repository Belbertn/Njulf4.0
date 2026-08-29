namespace Njulf.Rendering.Diagnostics;

public enum MeshletQualificationLevel
{
    Rejected,
    CorrectnessOnly,
    ProductionPerformance
}

public readonly record struct MeshletQualificationDevice(
    uint VendorId,
    string DeviceName,
    bool IntegratedGpu,
    uint VulkanApiVersion);

public sealed record MeshletQualificationEvidence
{
    public string BuildCommit { get; init; } = string.Empty;
    public string ArtifactBundleSha256 { get; init; } = string.Empty;
    public bool ReleaseBuild { get; init; }
    public bool CleanWorktree { get; init; }
    public int IndependentRuns { get; init; }
    public int MeasuredFramesPerRun { get; init; }
    public int VulkanValidationErrorCount { get; init; }
    public int DeviceLossCount { get; init; }
    public int CapacityOverflowCount { get; init; }
    public int BoundsFalseNegativeCount { get; init; }
    public int ConeFalseCullCount { get; init; }
    public int HierarchyCoverageFailureCount { get; init; }
    public int HierarchyTraversalFallbackCount { get; init; }
    public int StreamingAuthenticationFailureCount { get; init; }
    public int MissingCoarseFallbackCount { get; init; }
    public bool EightFrameDitherVerified { get; init; }
    public bool DirectionalShadowParityVerified { get; init; }
    public bool TransparentOrderingVerified { get; init; }
    public bool SkinnedPinnedResidencyVerified { get; init; }
    public bool ConservativeRayProxyVerified { get; init; }
    public bool FullResidentFallbackVerified { get; init; }
    public double MaximumReferenceImageDifference { get; init; }
    public double WarmPageCacheHitRate { get; init; }
    public double BaselineP95CpuMilliseconds { get; init; }
    public double CandidateP95CpuMilliseconds { get; init; }
    public double BaselineP95GpuMilliseconds { get; init; }
    public double CandidateP95GpuMilliseconds { get; init; }
    public long PeakPhysicalPageBytes { get; init; }
    public int PeakUploadBytesPerFrame { get; init; }
}

public readonly record struct MeshletQualificationResult(
    bool Passed,
    MeshletQualificationLevel Level,
    string Detail);

/// <summary>
/// Frozen release gate for the meshlet v2 implementation. NVIDIA's RTX 3060
/// Laptop GPU is the production performance target. AMD integrated GPUs run
/// the same correctness suite but cannot produce a performance-qualified
/// result from this contract.
/// </summary>
public static class MeshletSystemQualificationContract
{
    public const uint NvidiaVendorId = 0x10de;
    public const uint AmdVendorId = 0x1002;
    public const string ProductionDeviceToken = "RTX 3060 Laptop GPU";
    public const int MinimumIndependentRuns = 3;
    public const int MinimumMeasuredFramesPerRun = 1000;
    public const double MaximumReferenceImageDifference = 0.01;
    public const double MinimumWarmPageCacheHitRate = 0.90;
    public const double MaximumP95RegressionFraction = 0.02;
    public const int MaximumUploadBytesPerFrame = 8 * 1024 * 1024;
    public const long MaximumPhysicalPageBytes =
        4096L * 64L * 1024L;

    public static MeshletQualificationResult Evaluate(
        in MeshletQualificationDevice device,
        MeshletQualificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (device.VulkanApiVersion == 0 ||
            string.IsNullOrWhiteSpace(device.DeviceName))
        {
            return Reject(
                "meshlet-qualification-device-context-is-invalid");
        }
        MeshletQualificationLevel requestedLevel = Classify(device);
        if (requestedLevel == MeshletQualificationLevel.Rejected)
        {
            return Reject(
                "meshlet-qualification-device-is-outside-the-supported-matrix");
        }
        if (!IsLowerHex(evidence.BuildCommit, 40, 64) ||
            !IsLowerHex(evidence.ArtifactBundleSha256, 64, 64) ||
            !evidence.ReleaseBuild || !evidence.CleanWorktree)
        {
            return Reject(
                "meshlet-qualification-build-identity-is-not-release-reproducible");
        }
        if (evidence.IndependentRuns < MinimumIndependentRuns ||
            evidence.MeasuredFramesPerRun < MinimumMeasuredFramesPerRun)
        {
            return Reject(
                "meshlet-qualification-sample-floor-was-not-met");
        }
        if (evidence.VulkanValidationErrorCount != 0 ||
            evidence.DeviceLossCount != 0 ||
            evidence.CapacityOverflowCount != 0 ||
            evidence.BoundsFalseNegativeCount != 0 ||
            evidence.ConeFalseCullCount != 0 ||
            evidence.HierarchyCoverageFailureCount != 0 ||
            evidence.HierarchyTraversalFallbackCount != 0 ||
            evidence.StreamingAuthenticationFailureCount != 0 ||
            evidence.MissingCoarseFallbackCount != 0)
        {
            return Reject(
                "meshlet-qualification-correctness-counter-was-nonzero");
        }
        if (!evidence.EightFrameDitherVerified ||
            !evidence.DirectionalShadowParityVerified ||
            !evidence.TransparentOrderingVerified ||
            !evidence.SkinnedPinnedResidencyVerified ||
            !evidence.ConservativeRayProxyVerified ||
            !evidence.FullResidentFallbackVerified ||
            !double.IsFinite(evidence.MaximumReferenceImageDifference) ||
            evidence.MaximumReferenceImageDifference < 0.0 ||
            evidence.MaximumReferenceImageDifference >
                MaximumReferenceImageDifference)
        {
            return Reject(
                "meshlet-qualification-visual-or-fallback-proof-failed");
        }

        if (requestedLevel == MeshletQualificationLevel.CorrectnessOnly)
        {
            return new MeshletQualificationResult(
                true,
                MeshletQualificationLevel.CorrectnessOnly,
                "meshlet-correctness-qualified-performance-not-qualified-on-amd-igpu");
        }
        if (!IsPositiveFinite(evidence.BaselineP95CpuMilliseconds) ||
            !IsPositiveFinite(evidence.CandidateP95CpuMilliseconds) ||
            !IsPositiveFinite(evidence.BaselineP95GpuMilliseconds) ||
            !IsPositiveFinite(evidence.CandidateP95GpuMilliseconds) ||
            evidence.CandidateP95CpuMilliseconds >
                evidence.BaselineP95CpuMilliseconds *
                (1.0 + MaximumP95RegressionFraction) ||
            evidence.CandidateP95GpuMilliseconds >
                evidence.BaselineP95GpuMilliseconds *
                (1.0 + MaximumP95RegressionFraction) ||
            !double.IsFinite(evidence.WarmPageCacheHitRate) ||
            evidence.WarmPageCacheHitRate < MinimumWarmPageCacheHitRate ||
            evidence.WarmPageCacheHitRate > 1.0 ||
            evidence.PeakPhysicalPageBytes < 0 ||
            evidence.PeakPhysicalPageBytes > MaximumPhysicalPageBytes ||
            evidence.PeakUploadBytesPerFrame < 0 ||
            evidence.PeakUploadBytesPerFrame > MaximumUploadBytesPerFrame)
        {
            return Reject(
                "meshlet-qualification-production-performance-budget-failed");
        }

        return new MeshletQualificationResult(
            true,
            MeshletQualificationLevel.ProductionPerformance,
            "meshlet-production-qualified-rtx3060-laptop");
    }

    public static MeshletQualificationLevel Classify(
        in MeshletQualificationDevice device)
    {
        if (device.VendorId == NvidiaVendorId &&
            !device.IntegratedGpu &&
            device.DeviceName?.Contains(
                ProductionDeviceToken,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return MeshletQualificationLevel.ProductionPerformance;
        }
        if (device.VendorId == AmdVendorId && device.IntegratedGpu)
            return MeshletQualificationLevel.CorrectnessOnly;
        return MeshletQualificationLevel.Rejected;
    }

    private static MeshletQualificationResult Reject(string detail) =>
        new(false, MeshletQualificationLevel.Rejected, detail);

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0.0;

    private static bool IsLowerHex(
        string? value,
        int minimumLength,
        int maximumLength) =>
        value is not null &&
        value.Length >= minimumLength &&
        value.Length <= maximumLength &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
