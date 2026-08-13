using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Resources;

[Flags]
public enum AdvancedGiQualificationDeviceCoverage : uint
{
    None = 0,
    PrimaryRtx30 = 1u << 0,
    AdaOrNewer = 1u << 1,
    NonNvidiaRayQuery = 1u << 2,
    FeatureDisabledFallback = 1u << 3,
    MinimumMemoryProfile = 1u << 4
}

public enum AdvancedGiQualificationEvidenceRole : byte
{
    Correctness = 0,
    Performance = 1,
    Memory = 2,
    LongRun = 3,
    Validation = 4,
    Fallback = 5,
    Lifecycle = 6
}

/// <summary>
/// Frozen identity and promotion floors for the independently qualified C features. This layer is
/// intentionally separate from <see cref="AdvancedGiPrerequisiteManifest"/>: prerequisite proof
/// says that a feature may be researched, while this contract says that one exact implementation
/// was measured and may be selected by <c>AutoQualified</c> on one exact device/driver class.
/// </summary>
public static class AdvancedGiQualificationContract
{
    public const uint ManifestSchemaRevision = 3u;
    public const uint EvidenceReportSchemaRevision = 3u;
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumEvidenceArtifactBytes = 16 * 1024 * 1024;
    public const int MaximumFeatures = 5;
    public const int MaximumDeviceRulesPerFeature = 8;
    public const int MaximumArtifactsPerFeature = 64;
    public const int MinimumIndependentRuns = 3;
    public const uint MinimumReferenceFrames = 120u;
    public const uint MinimumLongRunSeconds = 30u * 60u;
    public const uint NvidiaVendorId = 0x10DEu;
    public const uint MinimumDirectionalGuidingStatisticalCases = 7u;
    public const ulong MinimumDirectionalGuidingSamplesPerCase = 16_384UL;
    public const double MinimumDirectionalGuidingGoodnessOfFitPValue = 0.001;
    public const double MaximumDirectionalGuidingBiasStandardErrors =
        SimpleDdgiGuidingEstimatorConfidence.DefaultMaximumBiasStandardErrors;
    public const double MinimumDirectionalGuidingMaintenanceFraction = 0.10;

    private static readonly AdvancedGiQualificationEvidenceRole[] SupportedRoles =
    [
        AdvancedGiQualificationEvidenceRole.Correctness,
        AdvancedGiQualificationEvidenceRole.Performance,
        AdvancedGiQualificationEvidenceRole.Memory,
        AdvancedGiQualificationEvidenceRole.LongRun,
        AdvancedGiQualificationEvidenceRole.Validation,
        AdvancedGiQualificationEvidenceRole.Fallback,
        AdvancedGiQualificationEvidenceRole.Lifecycle
    ];

    private static readonly AdvancedGiQualificationEvidenceRole[] UnsupportedRoles =
    [
        AdvancedGiQualificationEvidenceRole.Validation,
        AdvancedGiQualificationEvidenceRole.Fallback,
        AdvancedGiQualificationEvidenceRole.Lifecycle
    ];

    private static readonly IReadOnlyDictionary<AdvancedGiQualificationEvidenceRole, string[]>
        RequiredChecks = new ReadOnlyDictionary<AdvancedGiQualificationEvidenceRole, string[]>(
            new Dictionary<AdvancedGiQualificationEvidenceRole, string[]>
            {
                [AdvancedGiQualificationEvidenceRole.Correctness] =
                    ["feature-isolation", "integrated-parity", "reference-quality"],
                [AdvancedGiQualificationEvidenceRole.Performance] =
                    ["confidence-interval", "promotion-floor", "total-gi-time"],
                [AdvancedGiQualificationEvidenceRole.Memory] =
                    ["budget-headroom", "exact-bytes", "retired-stable"],
                [AdvancedGiQualificationEvidenceRole.LongRun] =
                    ["minimum-duration", "no-growth", "no-p99-trend"],
                [AdvancedGiQualificationEvidenceRole.Validation] =
                    ["robust-buffer-access", "synchronization-validation", "vulkan-validation"],
                [AdvancedGiQualificationEvidenceRole.Fallback] =
                    ["failure-fallback", "feature-off-canonical-parity", "unsupported-zero-allocation"],
                [AdvancedGiQualificationEvidenceRole.Lifecycle] =
                    ["allocation-failure", "device-loss", "reload", "resize"]
            });

    private static readonly string[] DirectionalGuidingCorrectnessChecks =
    [
        "feature-isolation",
        "integrated-parity",
        "reference-quality",
        "analytic-distribution-suite",
        "gpu-sampling-goodness-of-fit",
        "independent-estimator-confidence",
        "uniform-maintenance-audit",
        "generation-time-pdf-identity"
    ];

    private static readonly string[] ReceiverFeedbackCorrectnessChecks =
    [
        "feature-isolation",
        "integrated-parity",
        "reference-quality",
        "all-required-producers",
        "exact-compaction-reference-parity",
        "generation-and-viewport-publication"
    ];

    public static string SettingsContractSha256 { get; } = ComputeSettingsContractSha256();

    public static bool IsQualifiableFeature(AdvancedGiPrerequisiteFeature feature) => feature is
        AdvancedGiPrerequisiteFeature.ReceiverFeedback or
        AdvancedGiPrerequisiteFeature.OpacityMicromaps or
        AdvancedGiPrerequisiteFeature.DirectionalGuiding or
        AdvancedGiPrerequisiteFeature.TaggedCaustics or
        AdvancedGiPrerequisiteFeature.NearFieldResidual;

    public static uint GetFeatureAbiRevision(AdvancedGiPrerequisiteFeature feature) => feature switch
    {
        AdvancedGiPrerequisiteFeature.ReceiverFeedback =>
            SimpleDdgiReceiverFeedbackGpuSortAbi.Version,
        AdvancedGiPrerequisiteFeature.OpacityMicromaps => OpacityMicromapRuntimeAbi.Version,
        AdvancedGiPrerequisiteFeature.DirectionalGuiding => SimpleDdgiGuidingGpuAbi.Version,
        AdvancedGiPrerequisiteFeature.TaggedCaustics => GiCausticGpuAbi.Version,
        AdvancedGiPrerequisiteFeature.NearFieldResidual => SimpleDdgiNearFieldResidualGpuAbi.Version,
        _ => 0u
    };

    public static string GetAlgorithmRevision(AdvancedGiPrerequisiteFeature feature) => feature switch
    {
        AdvancedGiPrerequisiteFeature.ReceiverFeedback =>
            "b1-exact-multi-producer-compaction/v1",
        AdvancedGiPrerequisiteFeature.OpacityMicromaps => "c1-ext-four-state-static-blas/v1",
        AdvancedGiPrerequisiteFeature.DirectionalGuiding => "c3-equal-area-mis-guiding/v2",
        AdvancedGiPrerequisiteFeature.TaggedCaustics => "c4-tagged-world-photon-cache/v1",
        AdvancedGiPrerequisiteFeature.NearFieldResidual => "c5-bounded-hiz-residual/v1",
        _ => string.Empty
    };

    public static IReadOnlyList<AdvancedGiQualificationEvidenceRole> GetRequiredRoles(
        bool featureSupported) => featureSupported ? SupportedRoles : UnsupportedRoles;

    public static IReadOnlyList<string> GetRequiredChecks(
        AdvancedGiQualificationEvidenceRole role) => RequiredChecks.TryGetValue(role, out string[]? checks)
        ? checks
        : Array.Empty<string>();

    public static IReadOnlyList<string> GetRequiredChecks(
        AdvancedGiPrerequisiteFeature feature,
        AdvancedGiQualificationEvidenceRole role) =>
        role != AdvancedGiQualificationEvidenceRole.Correctness
            ? GetRequiredChecks(role)
            : feature switch
            {
                AdvancedGiPrerequisiteFeature.ReceiverFeedback =>
                    ReceiverFeedbackCorrectnessChecks,
                AdvancedGiPrerequisiteFeature.DirectionalGuiding =>
                    DirectionalGuidingCorrectnessChecks,
                _ => GetRequiredChecks(role)
            };

    internal static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string normalized = value.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[7..];
        if (normalized.Length != 64)
            return string.Empty;
        foreach (char character in normalized)
        {
            if (!Uri.IsHexDigit(character))
                return string.Empty;
        }
        return normalized.ToLowerInvariant();
    }

    internal static bool IsCanonicalToken(string? value, int maximumLength = 256)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }

    private static string ComputeSettingsContractSha256()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "advanced-gi-settings-contract/v2");
        AppendEnum<SimpleDdgiReceiverFeedbackMode>(hash);
        AppendEnum<DdgiOpacityMicromapMode>(hash);
        AppendEnum<SimpleDdgiDirectionalGuidingMode>(hash);
        AppendEnum<GiCausticMode>(hash);
        AppendEnum<SimpleDdgiNearFieldResidualMode>(hash);
        AppendEnum<SimpleDdgiAdvancedMemoryCategory>(hash);
        Append(hash, $"runs={MinimumIndependentRuns.ToString(CultureInfo.InvariantCulture)}");
        Append(hash, $"frames={MinimumReferenceFrames.ToString(CultureInfo.InvariantCulture)}");
        Append(hash, $"soak={MinimumLongRunSeconds.ToString(CultureInfo.InvariantCulture)}");
        Append(hash, "c1-floor=max(0.05ms,3pct-total-gi)");
        Append(hash, "b1-floor=exact-producer-parity-and-no-total-time-regression");
        Append(hash, "c3-floor=20pct-error-or-10pct-time");
        Append(hash,
            $"c3-statistical-cases={MinimumDirectionalGuidingStatisticalCases.ToString(CultureInfo.InvariantCulture)}");
        Append(hash,
            $"c3-samples-per-case={MinimumDirectionalGuidingSamplesPerCase.ToString(CultureInfo.InvariantCulture)}");
        Append(hash,
            $"c3-gof-p={MinimumDirectionalGuidingGoodnessOfFitPValue.ToString("R", CultureInfo.InvariantCulture)}");
        Append(hash,
            $"c3-bias-standard-errors={MaximumDirectionalGuidingBiasStandardErrors.ToString("R", CultureInfo.InvariantCulture)}");
        Append(hash,
            $"c3-maintenance-floor={MinimumDirectionalGuidingMaintenanceFraction.ToString("R", CultureInfo.InvariantCulture)}");
        foreach (string check in DirectionalGuidingCorrectnessChecks)
            Append(hash, "c3-check=" + check);
        foreach (string check in ReceiverFeedbackCorrectnessChecks)
            Append(hash, "b1-check=" + check);
        Append(hash, "c4-floor=20pct-mask-error");
        Append(hash, "c5-floor=20pct-post-b3-and-beat-equal-cost-b3");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendEnum<TEnum>(IncrementalHash hash) where TEnum : struct, Enum
    {
        Append(hash, typeof(TEnum).FullName ?? typeof(TEnum).Name);
        foreach (TEnum value in Enum.GetValues<TEnum>())
            Append(hash, $"{value}={Convert.ToUInt64(value, CultureInfo.InvariantCulture)}");
    }

    internal static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record AdvancedGiQualificationDeviceRule
{
    public string RuleId { get; init; } = string.Empty;
    public AdvancedGiQualificationDeviceCoverage Coverage { get; init; }
    public uint VendorId { get; init; }
    public uint MinimumDeviceId { get; init; }
    public uint MaximumDeviceId { get; init; }
    public uint MinimumDriverVersion { get; init; }
    public uint MaximumDriverVersion { get; init; }
    public uint MinimumApiVersion { get; init; }
    public uint MaximumApiVersion { get; init; }
    public bool ExpectedFeatureSupported { get; init; }

    internal bool IsWellFormed =>
        AdvancedGiQualificationContract.IsCanonicalToken(RuleId, 128) &&
        Coverage != AdvancedGiQualificationDeviceCoverage.None &&
        (Coverage & ~(AdvancedGiQualificationDeviceCoverage.PrimaryRtx30 |
            AdvancedGiQualificationDeviceCoverage.AdaOrNewer |
            AdvancedGiQualificationDeviceCoverage.NonNvidiaRayQuery |
            AdvancedGiQualificationDeviceCoverage.FeatureDisabledFallback |
            AdvancedGiQualificationDeviceCoverage.MinimumMemoryProfile)) == 0 &&
        VendorId != 0u && MinimumDeviceId != 0u && MaximumDeviceId >= MinimumDeviceId &&
        MinimumDriverVersion != 0u && MaximumDriverVersion >= MinimumDriverVersion &&
        MinimumApiVersion != 0u && MaximumApiVersion >= MinimumApiVersion;

    internal bool Matches(in AdvancedGiRuntimeQualificationContext context) =>
        context.VendorId == VendorId &&
        context.DeviceId >= MinimumDeviceId && context.DeviceId <= MaximumDeviceId &&
        context.DriverVersion >= MinimumDriverVersion && context.DriverVersion <= MaximumDriverVersion &&
        context.ApiVersion >= MinimumApiVersion && context.ApiVersion <= MaximumApiVersion &&
        context.FeatureSupported == ExpectedFeatureSupported;
}

public sealed record AdvancedGiQualificationArtifactPin
{
    public AdvancedGiQualificationEvidenceRole Role { get; init; }
    public string DeviceRuleId { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record AdvancedGiQualificationMeasurements
{
    public uint FrameCount { get; init; }
    public uint IndependentRunCount { get; init; }
    public uint DurationSeconds { get; init; }
    public double BaselineTotalGiP95Milliseconds { get; init; }
    public double CandidateTotalGiP95Milliseconds { get; init; }
    public double BaselineReferenceError { get; init; }
    public double CandidateReferenceError { get; init; }
    public double EqualCostAlternativeError { get; init; }
    public ulong BudgetBytes { get; init; }
    public ulong PeakLiveBytes { get; init; }
    public ulong RetiredButLiveBytes { get; init; }
    public uint ValidationWarningCount { get; init; }
    public uint ValidationErrorCount { get; init; }
    public ulong NonFiniteCount { get; init; }
    public ulong OverflowCount { get; init; }
    public ulong GenerationMismatchCount { get; init; }
    public bool ConfidenceIntervalExcludesNoise { get; init; }
    public bool FeatureOffCanonicalParity { get; init; }
    public bool UnsupportedZeroAllocation { get; init; }
    public bool FailureFallbackVerified { get; init; }
    public bool LifecycleTransitionsVerified { get; init; }
    /// <summary>Number of distinct analytic/adversarial C3 distributions.</summary>
    public uint DirectionalGuidingStatisticalCaseCount { get; init; }
    /// <summary>Total fence-complete GPU samples across the C3 statistical cases.</summary>
    public ulong DirectionalGuidingStatisticalSampleCount { get; init; }
    /// <summary>Smallest Pearson upper-tail probability across all C3 cases.</summary>
    public double DirectionalGuidingWorstGoodnessOfFitPValue { get; init; }
    /// <summary>Largest absolute estimator bias measured in standard errors.</summary>
    public double DirectionalGuidingMaximumBiasStandardErrors { get; init; }
    public ulong DirectionalGuidingSampleCount { get; init; }
    public ulong DirectionalGuidingUniformMaintenanceSampleCount { get; init; }
    public ulong DirectionalGuidingDirectionPdfIdentityMismatchCount { get; init; }
    public ulong DirectionalGuidingIndependentAuditFailureCount { get; init; }
    public ulong DirectionalGuidingUniformMaintenanceFailureCount { get; init; }
    /// <summary>Required B1 producer mask observed in every qualified run.</summary>
    public uint ReceiverFeedbackRequiredProducerMask { get; init; }
    /// <summary>Union of producers with fence-complete authoritative output.</summary>
    public uint ReceiverFeedbackObservedProducerMask { get; init; }
    /// <summary>Exact compacted records that differed from the CPU/reference oracle.</summary>
    public ulong ReceiverFeedbackReferenceMismatchCount { get; init; }
    /// <summary>Published summaries rejected for generation, viewport, or producer identity.</summary>
    public ulong ReceiverFeedbackPublicationRejectionCount { get; init; }
}

public sealed record AdvancedGiQualificationEvidenceReport
{
    public uint SchemaRevision { get; init; } =
        AdvancedGiQualificationContract.EvidenceReportSchemaRevision;
    public AdvancedGiQualificationEvidenceRole Role { get; init; }
    public AdvancedGiPrerequisiteFeature Feature { get; init; }
    public uint FeatureAbiRevision { get; init; }
    public string BindingId { get; init; } = string.Empty;
    public string DeviceRuleId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string ShaderBundleSha256 { get; init; } = string.Empty;
    public string SettingsContractSha256 { get; init; } = string.Empty;
    public string SettingsFingerprintSha256 { get; init; } = string.Empty;
    public string CorpusSha256 { get; init; } = string.Empty;
    public string ContentProfileId { get; init; } = string.Empty;
    public string SceneAssetSha256 { get; init; } = string.Empty;
    public string PrerequisiteQualificationId { get; init; } = string.Empty;
    public string[] PassedChecks { get; init; } = Array.Empty<string>();
    public AdvancedGiQualificationMeasurements Measurements { get; init; } = new();
    public string Summary { get; init; } = string.Empty;
}

public sealed record AdvancedGiFeatureQualificationDocument
{
    public AdvancedGiPrerequisiteFeature Feature { get; init; }
    public uint FeatureAbiRevision { get; init; }
    public string AlgorithmRevision { get; init; } = string.Empty;
    public string PrerequisiteQualificationId { get; init; } = string.Empty;
    public string ShaderBundleSha256 { get; init; } = string.Empty;
    public string SettingsContractSha256 { get; init; } = string.Empty;
    public string SettingsFingerprintSha256 { get; init; } = string.Empty;
    public string CorpusSha256 { get; init; } = string.Empty;
    public string ContentProfileId { get; init; } = string.Empty;
    public string SceneAssetSha256 { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string ApprovalId { get; init; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; init; }
    public AdvancedGiQualificationDeviceRule[] DeviceRules { get; init; } = Array.Empty<AdvancedGiQualificationDeviceRule>();
    public AdvancedGiQualificationArtifactPin[] Artifacts { get; init; } = Array.Empty<AdvancedGiQualificationArtifactPin>();
    public string QualificationId { get; init; } = string.Empty;
}

public sealed record AdvancedGiQualificationManifestDocument
{
    public uint SchemaRevision { get; init; } =
        AdvancedGiQualificationContract.ManifestSchemaRevision;
    public AdvancedGiFeatureQualificationDocument[] Features { get; init; } =
        Array.Empty<AdvancedGiFeatureQualificationDocument>();
}

public readonly record struct AdvancedGiRuntimeQualificationContext(
    uint VendorId,
    uint DeviceId,
    uint DriverVersion,
    uint ApiVersion,
    bool FeatureSupported,
    string ShaderBundleSha256,
    string SettingsContractSha256,
    string BuildCommit,
    string SettingsFingerprintSha256,
    string CorpusSha256,
    string ContentProfileId,
    string SceneAssetSha256)
{
    public bool IsWellFormed => VendorId != 0u && DeviceId != 0u &&
        DriverVersion != 0u && ApiVersion != 0u &&
        AdvancedGiQualificationContract.NormalizeSha256(ShaderBundleSha256).Length == 64 &&
        AdvancedGiQualificationContract.NormalizeSha256(SettingsContractSha256).Length == 64 &&
        IsCommit(BuildCommit) &&
        AdvancedGiQualificationContract.NormalizeSha256(SettingsFingerprintSha256).Length == 64 &&
        AdvancedGiQualificationContract.NormalizeSha256(CorpusSha256).Length == 64 &&
        AdvancedGiQualificationContract.IsCanonicalToken(ContentProfileId, 256) &&
        AdvancedGiQualificationContract.NormalizeSha256(SceneAssetSha256).Length == 64;

    private static bool IsCommit(string? value) => value is { Length: >= 40 and <= 64 } &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) &&
        value.All(Uri.IsHexDigit);
}

public readonly record struct AdvancedGiQualificationGateResult(
    bool Passed,
    string QualificationId,
    string FailureDetail,
    string MatchedDeviceRuleId)
{
    public static AdvancedGiQualificationGateResult Reject(
        string detail,
        string qualificationId = "") => new(
            false,
            qualificationId,
            string.IsNullOrWhiteSpace(detail) ? "advanced-gi-qualification-rejected" : detail,
            string.Empty);
}

public readonly record struct AdvancedGiAuthenticatedQualificationBinding(
    string QualificationId,
    string PrerequisiteQualificationId,
    string BuildCommit,
    string ShaderBundleSha256,
    string SettingsFingerprintSha256,
    string CorpusSha256,
    string ContentProfileId,
    string SceneAssetSha256);

/// <summary>
/// Immutable, load-authenticated qualification set. There is intentionally no public mutation
/// API: callers can only obtain entries after the codec has pinned and validated every report.
/// </summary>
public sealed class AdvancedGiQualificationManifest
{
    private readonly IReadOnlyDictionary<AdvancedGiPrerequisiteFeature, AuthenticatedEntry> _entries;

    internal AdvancedGiQualificationManifest(
        IReadOnlyDictionary<AdvancedGiPrerequisiteFeature, AuthenticatedEntry> entries)
    {
        _entries = entries;
    }

    public static AdvancedGiQualificationManifest Empty { get; } = new(
        new ReadOnlyDictionary<AdvancedGiPrerequisiteFeature, AuthenticatedEntry>(
            new Dictionary<AdvancedGiPrerequisiteFeature, AuthenticatedEntry>()));

    public int Count => _entries.Count;

    /// <summary>
    /// Reports whether the authenticated manifest contains a pinned entry for
    /// a feature. This does not imply that the current device/runtime context
    /// passes that entry; <see cref="Evaluate"/> remains authoritative.
    /// </summary>
    public bool Contains(AdvancedGiPrerequisiteFeature feature) =>
        AdvancedGiQualificationContract.IsQualifiableFeature(feature) &&
        _entries.ContainsKey(feature);

    public bool TryGetQualificationId(
        AdvancedGiPrerequisiteFeature feature,
        out string qualificationId)
    {
        if (_entries.TryGetValue(feature, out AuthenticatedEntry? entry))
        {
            qualificationId = "sha256:" + entry.QualificationId;
            return true;
        }
        qualificationId = string.Empty;
        return false;
    }

    public bool TryGetBinding(
        AdvancedGiPrerequisiteFeature feature,
        out AdvancedGiAuthenticatedQualificationBinding binding)
    {
        if (!_entries.TryGetValue(feature, out AuthenticatedEntry? entry))
        {
            binding = default;
            return false;
        }
        binding = new AdvancedGiAuthenticatedQualificationBinding(
            "sha256:" + entry.QualificationId,
            "sha256:" + entry.PrerequisiteQualificationId,
            entry.BuildCommit,
            "sha256:" + entry.ShaderBundleSha256,
            "sha256:" + entry.SettingsFingerprintSha256,
            "sha256:" + entry.CorpusSha256,
            entry.ContentProfileId,
            "sha256:" + entry.SceneAssetSha256);
        return true;
    }

    public AdvancedGiQualificationGateResult Evaluate(
        AdvancedGiPrerequisiteFeature feature,
        in AdvancedGiRuntimeQualificationContext context,
        string prerequisiteQualificationId,
        string? configuredQualificationId)
    {
        if (!AdvancedGiQualificationContract.IsQualifiableFeature(feature))
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-feature-is-not-auto-qualifiable");
        if (!context.IsWellFormed)
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-runtime-qualification-context-invalid");
        if (!_entries.TryGetValue(feature, out AuthenticatedEntry? entry))
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-feature-qualification-evidence-missing");

        string configuredId = AdvancedGiQualificationContract.NormalizeSha256(configuredQualificationId);
        if (configuredId.Length == 0)
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-configured-qualification-id-missing");
        if (!FixedSha256Equals(configuredId, entry.QualificationId))
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-configured-qualification-id-mismatch",
                entry.QualificationId);
        if (!FixedSha256Equals(prerequisiteQualificationId, entry.PrerequisiteQualificationId))
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-prerequisite-qualification-id-mismatch",
                entry.QualificationId);
        if (entry.FeatureAbiRevision != AdvancedGiQualificationContract.GetFeatureAbiRevision(feature))
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-feature-ABI-revision-mismatch", entry.QualificationId);
        if (!string.Equals(entry.AlgorithmRevision,
                AdvancedGiQualificationContract.GetAlgorithmRevision(feature), StringComparison.Ordinal))
        {
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-algorithm-revision-mismatch", entry.QualificationId);
        }
        if (!FixedSha256Equals(context.ShaderBundleSha256, entry.ShaderBundleSha256))
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-shader-bundle-evidence-mismatch", entry.QualificationId);
        if (!FixedSha256Equals(context.SettingsContractSha256, entry.SettingsContractSha256))
            return AdvancedGiQualificationGateResult.Reject("advanced-gi-settings-contract-evidence-mismatch", entry.QualificationId);
        if (!string.Equals(context.BuildCommit, entry.BuildCommit,
                StringComparison.Ordinal))
        {
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-build-commit-evidence-mismatch",
                entry.QualificationId);
        }
        if (!FixedSha256Equals(
                context.SettingsFingerprintSha256,
                entry.SettingsFingerprintSha256))
        {
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-settings-fingerprint-evidence-mismatch",
                entry.QualificationId);
        }
        if (!FixedSha256Equals(context.CorpusSha256, entry.CorpusSha256))
        {
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-corpus-evidence-mismatch",
                entry.QualificationId);
        }
        if (!string.Equals(context.ContentProfileId, entry.ContentProfileId,
                StringComparison.Ordinal))
        {
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-content-profile-evidence-mismatch",
                entry.QualificationId);
        }
        if (!FixedSha256Equals(context.SceneAssetSha256,
                entry.SceneAssetSha256))
        {
            return AdvancedGiQualificationGateResult.Reject(
                "advanced-gi-scene-asset-evidence-mismatch",
                entry.QualificationId);
        }

        foreach (AdvancedGiQualificationDeviceRule rule in entry.DeviceRules)
        {
            if (!rule.Matches(context))
                continue;
            if (!context.FeatureSupported)
            {
                return AdvancedGiQualificationGateResult.Reject(
                    "advanced-gi-qualified-device-rule-confirms-canonical-fallback",
                    entry.QualificationId);
            }
            return new AdvancedGiQualificationGateResult(
                true,
                entry.QualificationId,
                "advanced-gi-qualified-evidence-matched",
                rule.RuleId);
        }

        return AdvancedGiQualificationGateResult.Reject(
            "advanced-gi-device-driver-class-not-qualified",
            entry.QualificationId);
    }

    private static bool FixedSha256Equals(string? left, string? right)
    {
        string normalizedLeft = AdvancedGiQualificationContract.NormalizeSha256(left);
        string normalizedRight = AdvancedGiQualificationContract.NormalizeSha256(right);
        if (normalizedLeft.Length != 64 || normalizedRight.Length != 64)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(normalizedLeft),
            Convert.FromHexString(normalizedRight));
    }

    internal sealed class AuthenticatedEntry
    {
        public AuthenticatedEntry(
            AdvancedGiFeatureQualificationDocument document,
            string bindingId,
            IReadOnlyList<AdvancedGiQualificationDeviceRule> rules)
        {
            Feature = document.Feature;
            FeatureAbiRevision = document.FeatureAbiRevision;
            AlgorithmRevision = document.AlgorithmRevision;
            PrerequisiteQualificationId = document.PrerequisiteQualificationId.ToLowerInvariant();
            ShaderBundleSha256 = document.ShaderBundleSha256.ToLowerInvariant();
            SettingsContractSha256 = document.SettingsContractSha256.ToLowerInvariant();
            SettingsFingerprintSha256 =
                document.SettingsFingerprintSha256.ToLowerInvariant();
            CorpusSha256 = document.CorpusSha256.ToLowerInvariant();
            ContentProfileId = document.ContentProfileId;
            SceneAssetSha256 = document.SceneAssetSha256.ToLowerInvariant();
            BuildCommit = document.BuildCommit;
            BindingId = bindingId;
            QualificationId = document.QualificationId.ToLowerInvariant();
            DeviceRules = rules;
        }

        public AdvancedGiPrerequisiteFeature Feature { get; }
        public uint FeatureAbiRevision { get; }
        public string AlgorithmRevision { get; }
        public string PrerequisiteQualificationId { get; }
        public string ShaderBundleSha256 { get; }
        public string SettingsContractSha256 { get; }
        public string SettingsFingerprintSha256 { get; }
        public string CorpusSha256 { get; }
        public string ContentProfileId { get; }
        public string SceneAssetSha256 { get; }
        public string BuildCommit { get; }
        public string BindingId { get; }
        public string QualificationId { get; }
        public IReadOnlyList<AdvancedGiQualificationDeviceRule> DeviceRules { get; }
    }
}

public static class AdvancedGiQualificationManifestCodec
{
    private const int MaximumJsonDepth = 48;
    private const string PassedStatus = "Passed";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string SerializeDocument(AdvancedGiQualificationManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static string SerializeReport(AdvancedGiQualificationEvidenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ComputeBindingId(AdvancedGiFeatureQualificationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AdvancedGiQualificationContract.Append(hash, "advanced-gi-feature-binding/v2");
        AdvancedGiQualificationContract.Append(hash, ((byte)document.Feature).ToString(CultureInfo.InvariantCulture));
        AdvancedGiQualificationContract.Append(hash, document.FeatureAbiRevision.ToString(CultureInfo.InvariantCulture));
        AdvancedGiQualificationContract.Append(hash, document.AlgorithmRevision ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.PrerequisiteQualificationId?.ToLowerInvariant() ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.ShaderBundleSha256?.ToLowerInvariant() ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.SettingsContractSha256?.ToLowerInvariant() ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.SettingsFingerprintSha256?.ToLowerInvariant() ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.CorpusSha256?.ToLowerInvariant() ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.ContentProfileId ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.SceneAssetSha256?.ToLowerInvariant() ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.BuildCommit?.ToLowerInvariant() ?? string.Empty);
        foreach (AdvancedGiQualificationDeviceRule rule in (document.DeviceRules ?? [])
                     .OrderBy(static rule => rule.RuleId, StringComparer.Ordinal))
        {
            AdvancedGiQualificationContract.Append(hash, rule.RuleId);
            AdvancedGiQualificationContract.Append(hash, ((uint)rule.Coverage).ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.VendorId.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.MinimumDeviceId.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.MaximumDeviceId.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.MinimumDriverVersion.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.MaximumDriverVersion.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.MinimumApiVersion.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.MaximumApiVersion.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, rule.ExpectedFeatureSupported ? "1" : "0");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeQualificationId(AdvancedGiFeatureQualificationDocument document)
    {
        string bindingId = ComputeBindingId(document);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AdvancedGiQualificationContract.Append(hash, "advanced-gi-feature-qualification/v2");
        AdvancedGiQualificationContract.Append(hash, bindingId);
        AdvancedGiQualificationContract.Append(hash, document.ApprovalId ?? string.Empty);
        AdvancedGiQualificationContract.Append(hash, document.ApprovedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        foreach (AdvancedGiQualificationArtifactPin artifact in (document.Artifacts ?? [])
                     .OrderBy(static item => item.DeviceRuleId, StringComparer.Ordinal)
                     .ThenBy(static item => item.Role)
                     .ThenBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            AdvancedGiQualificationContract.Append(hash, artifact.DeviceRuleId);
            AdvancedGiQualificationContract.Append(hash, ((byte)artifact.Role).ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, artifact.RelativePath);
            AdvancedGiQualificationContract.Append(hash, artifact.ByteLength.ToString(CultureInfo.InvariantCulture));
            AdvancedGiQualificationContract.Append(hash, artifact.Sha256?.ToLowerInvariant() ?? string.Empty);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool TryLoad(
        string path,
        out AdvancedGiQualificationManifest manifest,
        out string failureDetail,
        DateTimeOffset? evaluationTimeUtc = null)
    {
        manifest = AdvancedGiQualificationManifest.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail = "advanced-gi-qualification-manifest-path-empty";
            return false;
        }

        try
        {
            string manifestPath = Path.GetFullPath(path);
            byte[] bytes = BoundedFileReader.ReadStable(
                manifestPath,
                AdvancedGiQualificationContract.MaximumManifestBytes,
                "Advanced GI qualification manifest");
            StrictJsonContract.RejectDuplicateProperties(bytes, MaximumJsonDepth,
                "Advanced GI qualification manifest");
            AdvancedGiQualificationManifestDocument document =
                JsonSerializer.Deserialize<AdvancedGiQualificationManifestDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Advanced GI qualification manifest is null.");
            AdvancedGiQualificationManifest loaded = Authenticate(
                manifestPath,
                document,
                evaluationTimeUtc ?? DateTimeOffset.UtcNow);
            manifest = loaded;
            failureDetail = "valid";
            return true;
        }
        catch (FileNotFoundException)
        {
            failureDetail = "advanced-gi-qualification-manifest-not-found";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failureDetail = "advanced-gi-qualification-manifest-not-found";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "advanced-gi-qualification-manifest-access-denied";
            return false;
        }
        catch (InvalidDataException exception)
        {
            failureDetail = exception.Message;
            return false;
        }
        catch (IOException)
        {
            failureDetail = "advanced-gi-qualification-manifest-IO-failure";
            return false;
        }
        catch (JsonException)
        {
            failureDetail = "advanced-gi-qualification-manifest-JSON-invalid";
            return false;
        }
        catch (NotSupportedException)
        {
            failureDetail = "advanced-gi-qualification-manifest-JSON-shape-unsupported";
            return false;
        }
        catch (CryptographicException)
        {
            failureDetail = "advanced-gi-qualification-cryptographic-validation-failed";
            return false;
        }
    }

    private static AdvancedGiQualificationManifest Authenticate(
        string manifestPath,
        AdvancedGiQualificationManifestDocument manifest,
        DateTimeOffset evaluationTimeUtc)
    {
        if (manifest.SchemaRevision != AdvancedGiQualificationContract.ManifestSchemaRevision)
            throw Invalid("advanced-gi-qualification-manifest-schema-mismatch");
        if (manifest.Features is null || manifest.Features.Length == 0 ||
            manifest.Features.Length > AdvancedGiQualificationContract.MaximumFeatures)
        {
            throw Invalid("advanced-gi-qualification-feature-count-invalid");
        }

        string directory = Path.GetDirectoryName(manifestPath) ??
            throw Invalid("advanced-gi-qualification-manifest-directory-invalid");
        var entries = new Dictionary<AdvancedGiPrerequisiteFeature,
            AdvancedGiQualificationManifest.AuthenticatedEntry>();
        var allArtifactPaths = new HashSet<string>(PathComparer) { manifestPath };
        foreach (AdvancedGiFeatureQualificationDocument feature in manifest.Features)
        {
            ValidateFeatureDocument(feature, evaluationTimeUtc);
            if (!entries.TryAdd(feature.Feature, null!))
                throw Invalid("advanced-gi-qualification-feature-duplicated");

            string bindingId = ComputeBindingId(feature);
            string qualificationId = ComputeQualificationId(feature);
            RequireSha256Equal(feature.QualificationId, qualificationId,
                "advanced-gi-qualification-id-mismatch");

            AdvancedGiQualificationDeviceRule[] rules = feature.DeviceRules
                .Select(static rule => rule with { })
                .OrderBy(static rule => rule.RuleId, StringComparer.Ordinal)
                .ToArray();
            var rulesById = rules.ToDictionary(static rule => rule.RuleId, StringComparer.Ordinal);
            ValidateArtifactShape(feature, rulesById);

            foreach (AdvancedGiQualificationArtifactPin pin in feature.Artifacts)
            {
                string artifactPath = ResolveContainedArtifactPath(directory, pin.RelativePath);
                if (!allArtifactPaths.Add(artifactPath))
                    throw Invalid("advanced-gi-qualification-artifact-path-duplicated");
                byte[] artifactBytes = ReadPinnedArtifact(artifactPath, pin);
                RequireSha256Equal(
                    Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
                    pin.Sha256,
                    "advanced-gi-qualification-artifact-hash-mismatch");
                StrictJsonContract.RejectDuplicateProperties(
                    artifactBytes,
                    MaximumJsonDepth,
                    $"Advanced GI {pin.Role} evidence");
                AdvancedGiQualificationEvidenceReport report =
                    JsonSerializer.Deserialize<AdvancedGiQualificationEvidenceReport>(
                        artifactBytes,
                        JsonOptions) ?? throw Invalid("advanced-gi-qualification-evidence-report-null");
                ValidateReport(feature, pin, rulesById[pin.DeviceRuleId], report, bindingId);
            }

            entries[feature.Feature] = new AdvancedGiQualificationManifest.AuthenticatedEntry(
                feature,
                bindingId,
                Array.AsReadOnly(rules));
        }

        return new AdvancedGiQualificationManifest(
            new ReadOnlyDictionary<AdvancedGiPrerequisiteFeature,
                AdvancedGiQualificationManifest.AuthenticatedEntry>(entries));
    }

    private static byte[] ReadPinnedArtifact(
        string artifactPath,
        AdvancedGiQualificationArtifactPin pin)
    {
        try
        {
            return BoundedFileReader.ReadStable(
                artifactPath,
                AdvancedGiQualificationContract.MaximumEvidenceArtifactBytes,
                $"Advanced GI {pin.Role} evidence",
                pin.ByteLength);
        }
        catch (FileNotFoundException)
        {
            throw Invalid("advanced-gi-qualification-artifact-not-found");
        }
        catch (DirectoryNotFoundException)
        {
            throw Invalid("advanced-gi-qualification-artifact-not-found");
        }
        catch (UnauthorizedAccessException)
        {
            throw Invalid("advanced-gi-qualification-artifact-access-denied");
        }
        catch (InvalidDataException)
        {
            throw Invalid("advanced-gi-qualification-artifact-length-mismatch");
        }
        catch (IOException)
        {
            throw Invalid("advanced-gi-qualification-artifact-IO-failure");
        }
    }

    private static void ValidateFeatureDocument(
        AdvancedGiFeatureQualificationDocument document,
        DateTimeOffset evaluationTimeUtc)
    {
        if (document is null || !AdvancedGiQualificationContract.IsQualifiableFeature(document.Feature))
            throw Invalid("advanced-gi-qualification-feature-invalid");
        if (document.FeatureAbiRevision !=
            AdvancedGiQualificationContract.GetFeatureAbiRevision(document.Feature))
        {
            throw Invalid("advanced-gi-qualification-feature-ABI-invalid");
        }
        if (!string.Equals(document.AlgorithmRevision,
                AdvancedGiQualificationContract.GetAlgorithmRevision(document.Feature),
                StringComparison.Ordinal))
        {
            throw Invalid("advanced-gi-qualification-algorithm-revision-invalid");
        }
        if (AdvancedGiQualificationContract.NormalizeSha256(document.PrerequisiteQualificationId).Length != 64 ||
            AdvancedGiQualificationContract.NormalizeSha256(document.ShaderBundleSha256).Length != 64 ||
            AdvancedGiQualificationContract.NormalizeSha256(document.SettingsContractSha256).Length != 64 ||
            AdvancedGiQualificationContract.NormalizeSha256(document.SettingsFingerprintSha256).Length != 64 ||
            AdvancedGiQualificationContract.NormalizeSha256(document.CorpusSha256).Length != 64 ||
            AdvancedGiQualificationContract.NormalizeSha256(document.SceneAssetSha256).Length != 64 ||
            AdvancedGiQualificationContract.NormalizeSha256(document.QualificationId).Length != 64)
        {
            throw Invalid("advanced-gi-qualification-feature-hash-invalid");
        }
        if (!string.Equals(document.SettingsContractSha256,
                AdvancedGiQualificationContract.SettingsContractSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("advanced-gi-qualification-settings-contract-stale");
        }
        if (!IsCommit(document.BuildCommit) ||
            !AdvancedGiQualificationContract.IsCanonicalToken(
                document.ContentProfileId, 256) ||
            !AdvancedGiQualificationContract.IsCanonicalToken(document.ApprovalId, 256))
        {
            throw Invalid("advanced-gi-qualification-build-or-approval-identity-invalid");
        }
        if (document.ApprovedAtUtc == default || document.ApprovedAtUtc.Offset != TimeSpan.Zero ||
            document.ApprovedAtUtc > evaluationTimeUtc.ToUniversalTime())
        {
            throw Invalid("advanced-gi-qualification-approval-time-invalid");
        }
        if (document.DeviceRules is null || document.DeviceRules.Length == 0 ||
            document.DeviceRules.Length > AdvancedGiQualificationContract.MaximumDeviceRulesPerFeature ||
            document.DeviceRules.Any(static rule => rule is null || !rule.IsWellFormed))
        {
            throw Invalid("advanced-gi-qualification-device-rule-invalid");
        }
        if (document.DeviceRules.Select(static rule => rule.RuleId)
            .Distinct(StringComparer.Ordinal).Count() != document.DeviceRules.Length)
        {
            throw Invalid("advanced-gi-qualification-device-rule-duplicated");
        }
        ValidateDeviceMatrix(document.Feature, document.DeviceRules);
    }

    private static void ValidateDeviceMatrix(
        AdvancedGiPrerequisiteFeature feature,
        IReadOnlyList<AdvancedGiQualificationDeviceRule> rules)
    {
        AdvancedGiQualificationDeviceCoverage coverage = AdvancedGiQualificationDeviceCoverage.None;
        foreach (AdvancedGiQualificationDeviceRule rule in rules)
        {
            coverage |= rule.Coverage;
            bool nvidiaRole = (rule.Coverage &
                (AdvancedGiQualificationDeviceCoverage.PrimaryRtx30 |
                 AdvancedGiQualificationDeviceCoverage.AdaOrNewer)) != 0;
            if (nvidiaRole && (rule.VendorId != AdvancedGiQualificationContract.NvidiaVendorId ||
                !rule.ExpectedFeatureSupported))
            {
                throw Invalid("advanced-gi-qualification-NVIDIA-device-role-invalid");
            }
            if ((rule.Coverage & AdvancedGiQualificationDeviceCoverage.NonNvidiaRayQuery) != 0 &&
                (rule.VendorId == AdvancedGiQualificationContract.NvidiaVendorId || rule.VendorId == 0u))
            {
                throw Invalid("advanced-gi-qualification-non-NVIDIA-device-role-invalid");
            }
            if ((rule.Coverage & AdvancedGiQualificationDeviceCoverage.FeatureDisabledFallback) != 0 &&
                rule.ExpectedFeatureSupported)
            {
                throw Invalid("advanced-gi-qualification-disabled-fallback-role-invalid");
            }
        }

        AdvancedGiQualificationDeviceCoverage mandatory =
            AdvancedGiQualificationDeviceCoverage.PrimaryRtx30 |
            AdvancedGiQualificationDeviceCoverage.AdaOrNewer |
            AdvancedGiQualificationDeviceCoverage.NonNvidiaRayQuery |
            AdvancedGiQualificationDeviceCoverage.MinimumMemoryProfile;
        if ((coverage & mandatory) != mandatory)
            throw Invalid("advanced-gi-qualification-device-matrix-incomplete");
        if (rules.Where(static rule => (rule.Coverage &
                (AdvancedGiQualificationDeviceCoverage.PrimaryRtx30 |
                 AdvancedGiQualificationDeviceCoverage.AdaOrNewer)) != 0)
            .Select(static rule => (rule.VendorId, rule.MinimumDeviceId, rule.MaximumDeviceId))
            .Distinct().Count() < 2)
        {
            throw Invalid("advanced-gi-qualification-NVIDIA-generation-matrix-incomplete");
        }

        bool nonNvidiaExpectedSupport = feature != AdvancedGiPrerequisiteFeature.OpacityMicromaps;
        if (!rules.Any(rule =>
                (rule.Coverage & AdvancedGiQualificationDeviceCoverage.NonNvidiaRayQuery) != 0 &&
                rule.ExpectedFeatureSupported == nonNvidiaExpectedSupport))
        {
            throw Invalid("advanced-gi-qualification-portable-device-matrix-invalid");
        }
        if (feature == AdvancedGiPrerequisiteFeature.OpacityMicromaps &&
            ((coverage & AdvancedGiQualificationDeviceCoverage.FeatureDisabledFallback) == 0 ||
             !rules.Any(static rule => !rule.ExpectedFeatureSupported)))
        {
            throw Invalid("advanced-gi-qualification-C1-fallback-matrix-incomplete");
        }
    }

    private static void ValidateArtifactShape(
        AdvancedGiFeatureQualificationDocument feature,
        IReadOnlyDictionary<string, AdvancedGiQualificationDeviceRule> rules)
    {
        if (feature.Artifacts is null || feature.Artifacts.Length == 0 ||
            feature.Artifacts.Length > AdvancedGiQualificationContract.MaximumArtifactsPerFeature)
        {
            throw Invalid("advanced-gi-qualification-artifact-count-invalid");
        }

        var actual = new HashSet<(string Rule, AdvancedGiQualificationEvidenceRole Role)>();
        foreach (AdvancedGiQualificationArtifactPin artifact in feature.Artifacts)
        {
            if (artifact is null || !Enum.IsDefined(artifact.Role) ||
                !rules.ContainsKey(artifact.DeviceRuleId) ||
                !IsCanonicalRelativePath(artifact.RelativePath) ||
                artifact.ByteLength <= 0 ||
                artifact.ByteLength > AdvancedGiQualificationContract.MaximumEvidenceArtifactBytes ||
                AdvancedGiQualificationContract.NormalizeSha256(artifact.Sha256).Length != 64 ||
                !actual.Add((artifact.DeviceRuleId, artifact.Role)))
            {
                throw Invalid("advanced-gi-qualification-artifact-pin-invalid-or-duplicated");
            }
        }

        foreach (AdvancedGiQualificationDeviceRule rule in rules.Values)
        {
            IReadOnlyList<AdvancedGiQualificationEvidenceRole> required =
                AdvancedGiQualificationContract.GetRequiredRoles(rule.ExpectedFeatureSupported);
            foreach (AdvancedGiQualificationEvidenceRole role in required)
            {
                if (!actual.Contains((rule.RuleId, role)))
                    throw Invalid("advanced-gi-qualification-required-artifact-missing");
            }
            if (actual.Any(key => key.Rule == rule.RuleId && !required.Contains(key.Role)))
                throw Invalid("advanced-gi-qualification-inapplicable-artifact-present");
        }
    }

    private static void ValidateReport(
        AdvancedGiFeatureQualificationDocument feature,
        AdvancedGiQualificationArtifactPin pin,
        AdvancedGiQualificationDeviceRule rule,
        AdvancedGiQualificationEvidenceReport report,
        string bindingId)
    {
        if (report.SchemaRevision != AdvancedGiQualificationContract.EvidenceReportSchemaRevision ||
            report.Role != pin.Role || report.Feature != feature.Feature ||
            report.FeatureAbiRevision != feature.FeatureAbiRevision ||
            !string.Equals(report.DeviceRuleId, pin.DeviceRuleId, StringComparison.Ordinal) ||
            !string.Equals(report.Status, PassedStatus, StringComparison.Ordinal) ||
            !string.Equals(report.BindingId, bindingId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(report.BuildCommit, feature.BuildCommit, StringComparison.Ordinal) ||
            !HashTextEquals(report.ShaderBundleSha256, feature.ShaderBundleSha256) ||
            !HashTextEquals(report.SettingsContractSha256, feature.SettingsContractSha256) ||
            !HashTextEquals(report.SettingsFingerprintSha256,
                feature.SettingsFingerprintSha256) ||
            !HashTextEquals(report.CorpusSha256, feature.CorpusSha256) ||
            !string.Equals(report.ContentProfileId, feature.ContentProfileId,
                StringComparison.Ordinal) ||
            !HashTextEquals(report.SceneAssetSha256,
                feature.SceneAssetSha256) ||
            !HashTextEquals(report.PrerequisiteQualificationId, feature.PrerequisiteQualificationId))
        {
            throw Invalid("advanced-gi-qualification-evidence-binding-mismatch");
        }
        if (!AdvancedGiQualificationContract.IsCanonicalToken(report.Summary, 2048) ||
            report.PassedChecks is null || report.PassedChecks.Length == 0 ||
            report.PassedChecks.Length > 64 ||
            report.PassedChecks.Any(static check =>
                !AdvancedGiQualificationContract.IsCanonicalToken(check, 128)) ||
            report.PassedChecks.Distinct(StringComparer.Ordinal).Count() != report.PassedChecks.Length)
        {
            throw Invalid("advanced-gi-qualification-evidence-report-shape-invalid");
        }
        foreach (string requiredCheck in AdvancedGiQualificationContract.GetRequiredChecks(
                     feature.Feature,
                     pin.Role))
        {
            if (!report.PassedChecks.Contains(requiredCheck, StringComparer.Ordinal))
                throw Invalid("advanced-gi-qualification-evidence-required-check-missing");
        }

        ValidateMeasurements(feature.Feature, pin.Role, rule.ExpectedFeatureSupported, report.Measurements);
    }

    private static void ValidateMeasurements(
        AdvancedGiPrerequisiteFeature feature,
        AdvancedGiQualificationEvidenceRole role,
        bool featureSupported,
        AdvancedGiQualificationMeasurements measurements)
    {
        if (measurements is null)
            throw Invalid("advanced-gi-qualification-measurements-missing");
        bool countersClean = measurements.NonFiniteCount == 0UL &&
            measurements.OverflowCount == 0UL && measurements.GenerationMismatchCount == 0UL;
        switch (role)
        {
            case AdvancedGiQualificationEvidenceRole.Correctness:
                if (!featureSupported ||
                    measurements.FrameCount < AdvancedGiQualificationContract.MinimumReferenceFrames ||
                    measurements.IndependentRunCount < AdvancedGiQualificationContract.MinimumIndependentRuns ||
                    !IsFiniteNonNegative(measurements.BaselineReferenceError) ||
                    !IsFiniteNonNegative(measurements.CandidateReferenceError) ||
                    !countersClean)
                {
                    throw Invalid("advanced-gi-qualification-correctness-measurement-invalid");
                }
                double baseline = measurements.BaselineReferenceError;
                double candidate = measurements.CandidateReferenceError;
                if (feature == AdvancedGiPrerequisiteFeature.ReceiverFeedback)
                {
                    uint required =
                        measurements.ReceiverFeedbackRequiredProducerMask;
                    uint observed =
                        measurements.ReceiverFeedbackObservedProducerMask;
                    if (!SimpleDdgiReceiverFeedbackCaptureSourceAbi
                            .IsValidProducerMask(required) ||
                        !SimpleDdgiReceiverFeedbackCaptureSourceAbi
                            .IsValidProducerMask(observed) ||
                        (observed & required) != required ||
                        measurements.ReceiverFeedbackReferenceMismatchCount !=
                            0UL ||
                        measurements
                            .ReceiverFeedbackPublicationRejectionCount != 0UL ||
                        candidate > baseline + 1e-9)
                    {
                        throw Invalid(
                            "advanced-gi-qualification-B1-exact-producer-parity-failed");
                    }
                }
                else if (feature == AdvancedGiPrerequisiteFeature.OpacityMicromaps)
                {
                    if (candidate > baseline + 1e-9)
                        throw Invalid("advanced-gi-qualification-C1-quality-neutrality-failed");
                }
                else if (baseline <= 0.0 || 1.0 - candidate / baseline < 0.20)
                {
                    throw Invalid("advanced-gi-qualification-quality-promotion-floor-failed");
                }
                if (feature == AdvancedGiPrerequisiteFeature.NearFieldResidual &&
                    (!IsFiniteNonNegative(measurements.EqualCostAlternativeError) ||
                     candidate >= measurements.EqualCostAlternativeError))
                {
                    throw Invalid("advanced-gi-qualification-C5-equal-cost-B3-gate-failed");
                }
                if (feature == AdvancedGiPrerequisiteFeature.DirectionalGuiding &&
                    !TryValidateDirectionalGuidingCorrectness(
                        measurements,
                        out string guidingReason))
                {
                    throw Invalid(guidingReason);
                }
                break;

            case AdvancedGiQualificationEvidenceRole.Performance:
                if (!featureSupported ||
                    measurements.IndependentRunCount < AdvancedGiQualificationContract.MinimumIndependentRuns ||
                    !IsFinitePositive(measurements.BaselineTotalGiP95Milliseconds) ||
                    !IsFinitePositive(measurements.CandidateTotalGiP95Milliseconds) ||
                    !measurements.ConfidenceIntervalExcludesNoise)
                {
                    throw Invalid("advanced-gi-qualification-performance-measurement-invalid");
                }
                double saved = measurements.BaselineTotalGiP95Milliseconds -
                    measurements.CandidateTotalGiP95Milliseconds;
                double fraction = saved / measurements.BaselineTotalGiP95Milliseconds;
                if (feature == AdvancedGiPrerequisiteFeature.ReceiverFeedback &&
                    saved < 0.0)
                {
                    throw Invalid(
                        "advanced-gi-qualification-B1-total-time-regressed");
                }
                if (feature == AdvancedGiPrerequisiteFeature.OpacityMicromaps &&
                    (saved < 0.05 || fraction < 0.03))
                {
                    throw Invalid("advanced-gi-qualification-C1-performance-floor-failed");
                }
                if (feature == AdvancedGiPrerequisiteFeature.DirectionalGuiding &&
                    fraction < 0.10 &&
                    (!IsFinitePositive(measurements.BaselineReferenceError) ||
                     !IsFiniteNonNegative(measurements.CandidateReferenceError) ||
                     1.0 - measurements.CandidateReferenceError /
                         measurements.BaselineReferenceError < 0.20))
                {
                    throw Invalid("advanced-gi-qualification-C3-quality-time-floor-failed");
                }
                if (feature is AdvancedGiPrerequisiteFeature.TaggedCaustics or
                    AdvancedGiPrerequisiteFeature.NearFieldResidual && saved < 0.0)
                {
                    throw Invalid("advanced-gi-qualification-total-time-regressed");
                }
                break;

            case AdvancedGiQualificationEvidenceRole.Memory:
                if (!featureSupported || measurements.BudgetBytes == 0UL ||
                    measurements.PeakLiveBytes == 0UL ||
                    measurements.PeakLiveBytes > measurements.BudgetBytes ||
                    measurements.RetiredButLiveBytes > measurements.BudgetBytes)
                {
                    throw Invalid("advanced-gi-qualification-memory-measurement-invalid");
                }
                break;

            case AdvancedGiQualificationEvidenceRole.LongRun:
                if (!featureSupported ||
                    measurements.DurationSeconds < AdvancedGiQualificationContract.MinimumLongRunSeconds ||
                    measurements.FrameCount < AdvancedGiQualificationContract.MinimumReferenceFrames ||
                    !countersClean)
                {
                    throw Invalid("advanced-gi-qualification-long-run-measurement-invalid");
                }
                break;

            case AdvancedGiQualificationEvidenceRole.Validation:
                if (measurements.ValidationWarningCount != 0u ||
                    measurements.ValidationErrorCount != 0u || !countersClean)
                {
                    throw Invalid("advanced-gi-qualification-validation-not-clean");
                }
                break;

            case AdvancedGiQualificationEvidenceRole.Fallback:
                if (!measurements.FeatureOffCanonicalParity ||
                    !measurements.UnsupportedZeroAllocation ||
                    !measurements.FailureFallbackVerified)
                {
                    throw Invalid("advanced-gi-qualification-fallback-measurement-invalid");
                }
                break;

            case AdvancedGiQualificationEvidenceRole.Lifecycle:
                if (!measurements.LifecycleTransitionsVerified || !countersClean)
                    throw Invalid("advanced-gi-qualification-lifecycle-measurement-invalid");
                break;

            default:
                throw Invalid("advanced-gi-qualification-evidence-role-invalid");
        }
    }

    private static string ResolveContainedArtifactPath(string directory, string relativePath)
    {
        if (!IsCanonicalRelativePath(relativePath))
            throw Invalid("advanced-gi-qualification-artifact-path-invalid");
        string root = Path.GetFullPath(directory);
        string resolved = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, PathComparison))
            throw Invalid("advanced-gi-qualification-artifact-path-escapes-manifest-directory");
        RejectLinkedArtifactSegments(root, resolved);
        return resolved;
    }

    private static void RejectLinkedArtifactSegments(
        string root,
        string artifact)
    {
        string relative = Path.GetRelativePath(root, artifact);
        string current = root;
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                return;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(
                    "advanced-gi-qualification-artifact-linked-path-rejected");
            }
        }
    }

    private static bool IsCanonicalRelativePath(string? path)
    {
        if (path is null ||
            !AdvancedGiQualificationContract.IsCanonicalToken(path, 512) ||
            Path.IsPathRooted(path) || path.Contains('\\'))
        {
            return false;
        }
        string[] parts = path.Split('/');
        return parts.Length > 0 && parts.All(static part =>
            part.Length > 0 && part is not "." and not "..");
    }

    private static bool IsCommit(string? value)
    {
        if (value is null || value.Length is < 40 or > 64 ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }
        return value.All(Uri.IsHexDigit);
    }

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0.0;
    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0.0;

    public static bool TryValidateDirectionalGuidingCorrectness(
        AdvancedGiQualificationMeasurements? measurements,
        out string reason)
    {
        if (measurements is null)
        {
            reason = "advanced-gi-qualification-C3-statistical-measurements-missing";
            return false;
        }

        ulong requiredSamples;
        try
        {
            requiredSamples = checked(
                (ulong)AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingStatisticalCases *
                AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingSamplesPerCase);
        }
        catch (OverflowException)
        {
            reason = "advanced-gi-qualification-C3-statistical-policy-overflow";
            return false;
        }

        if (measurements.DirectionalGuidingStatisticalCaseCount <
                AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingStatisticalCases ||
            measurements.DirectionalGuidingStatisticalSampleCount <
                requiredSamples)
        {
            reason = "advanced-gi-qualification-C3-statistical-coverage-insufficient";
            return false;
        }
        if (!double.IsFinite(
                measurements.DirectionalGuidingWorstGoodnessOfFitPValue) ||
            measurements.DirectionalGuidingWorstGoodnessOfFitPValue <
                AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingGoodnessOfFitPValue ||
            measurements.DirectionalGuidingWorstGoodnessOfFitPValue > 1.0)
        {
            reason = "advanced-gi-qualification-C3-sampling-goodness-of-fit-failed";
            return false;
        }
        if (!double.IsFinite(
                measurements.DirectionalGuidingMaximumBiasStandardErrors) ||
            measurements.DirectionalGuidingMaximumBiasStandardErrors < 0.0 ||
            measurements.DirectionalGuidingMaximumBiasStandardErrors >
                AdvancedGiQualificationContract
                    .MaximumDirectionalGuidingBiasStandardErrors)
        {
            reason = "advanced-gi-qualification-C3-estimator-confidence-failed";
            return false;
        }
        if (measurements.DirectionalGuidingSampleCount == 0UL ||
            measurements.DirectionalGuidingUniformMaintenanceSampleCount >
                measurements.DirectionalGuidingSampleCount)
        {
            reason = "advanced-gi-qualification-C3-maintenance-count-invalid";
            return false;
        }
        double maintenanceFraction =
            measurements.DirectionalGuidingUniformMaintenanceSampleCount /
            (double)measurements.DirectionalGuidingSampleCount;
        if (!double.IsFinite(maintenanceFraction) ||
            maintenanceFraction < AdvancedGiQualificationContract
                .MinimumDirectionalGuidingMaintenanceFraction ||
            measurements.DirectionalGuidingUniformMaintenanceFailureCount != 0UL)
        {
            reason = "advanced-gi-qualification-C3-uniform-maintenance-audit-failed";
            return false;
        }
        if (measurements.DirectionalGuidingDirectionPdfIdentityMismatchCount != 0UL)
        {
            reason = "advanced-gi-qualification-C3-direction-PDF-identity-failed";
            return false;
        }
        if (measurements.DirectionalGuidingIndependentAuditFailureCount != 0UL)
        {
            reason = "advanced-gi-qualification-C3-independent-audit-failed";
            return false;
        }

        reason = "valid";
        return true;
    }

    private static bool HashTextEquals(string? left, string? right)
    {
        string normalizedLeft = AdvancedGiQualificationContract.NormalizeSha256(left);
        string normalizedRight = AdvancedGiQualificationContract.NormalizeSha256(right);
        return normalizedLeft.Length == 64 && normalizedRight.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(normalizedLeft),
                Convert.FromHexString(normalizedRight));
    }

    private static void RequireSha256Equal(string? actual, string? expected, string reason)
    {
        if (!HashTextEquals(actual, expected))
            throw Invalid(reason);
    }

    private static InvalidDataException Invalid(string reason) => new(reason);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            MaxDepth = MaximumJsonDepth,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
