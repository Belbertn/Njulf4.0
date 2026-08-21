using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Data;

[Flags]
public enum MaterialGiV2Feature : uint
{
    None = 0,
    MaterialTransport = 1u << 0,
    EmissiveMeshSampling = 1u << 1,
    FarFieldMaterial = 1u << 2,
    All = MaterialTransport |
          EmissiveMeshSampling |
          FarFieldMaterial
}

public enum MaterialGiRolloutMode
{
    LegacyUnqualified = 0,
    Conformance = 1,
    QualifiedRelease = 2,
    QualificationCandidate = 3
}

/// <summary>
/// Source-controlled retirement contract for the temporary material-GI V1
/// compatibility path. Reaching the target date invalidates a release
/// qualification until the V1 path is removed or this contract is deliberately
/// revised as part of a reviewed release decision.
/// </summary>
public static class MaterialGiV1CompatibilityContract
{
    public const string Owner = "Njulf Rendering Maintainers";
    public const int RetainedReleaseWindowCount = 1;
    public static DateOnly RemovalTargetDate { get; } = new(2026, 10, 31);
}

/// <summary>
/// Canonical evidence roles and aggregate identity for a material-GI qualified
/// release. The bundle pins independently produced artifacts; it does not
/// replace their producers or reinterpret their measurements.
/// </summary>
public static class MaterialGiReleaseEvidenceContract
{
    public const int BundleSchemaVersion = 5;
    public const int PreviousBundleSchemaVersion = 4;
    public const int ArtifactSchemaVersion = 5;
    public const int PreviousArtifactSchemaVersion = 4;
    public const string AggregateSchema =
        "material-gi-release-evidence-aggregate/v5";
    public const string PassedStatus = "Passed";
    public const string UnsupportedStatus = "Unsupported";
    public const string ReferenceDeviceClass = "Reference";
    public const string LowerMemoryRayQueryDeviceClass =
        "LowerMemoryRayQuery";
    public const string ApprovedHdrRole = "approved-hdr-regression";
    public const string KhronosRenderedSemanticRole =
        "khronos-rendered-semantic-gate";
    public const string GraphicsAsyncEquivalenceRole =
        "graphics-async-equivalence";
    public const string CpuGpuOracleReleaseMatrixRole =
        "cpu-gpu-oracle-release-matrix";
    public const string TierPerformanceMatrixRole =
        "tier-performance-matrix";
    public const string ThirtyMinuteSoakRole = "thirty-minute-soak";
    public const string CleanValidationRole = "clean-validation";
    public const string LifecycleResilienceRole =
        "lifecycle-resize-minimize-restore-reload";
    public const string QualitySwitchRollbackRole =
        "quality-switch-rollback";
    public const string TextureHotReloadRollbackRole =
        "texture-hot-reload-rollback";
    public const string RecoveryCapabilityRole =
        "recovery-capability";
    public const int MinimumSoakDurationSeconds = 30 * 60;
    public const int MaximumBundleBytes = 256 * 1024;
    public const int MaximumArtifactBytes = 16 * 1024 * 1024;
    public const int MaximumProducerArtifactCount = 64;
    public const long MaximumReportedDeviceLocalMemoryBytes = 1L << 60;
    public const string ApprovedHdrProducerKind =
        "material-gi-approved-hdr-regression";
    public const string ApprovedHdrProducerSchema =
        "material-gi-approved-hdr-regression/v4";
    public const string KhronosRenderedProducerKind =
        "khronos-material-gi-rendered";
    public const string KhronosRenderedProducerSchema =
        "khronos-material-gi-rendered/v3";
    public const string GraphicsAsyncProducerKind =
        "material-gi-graphics-async-comparison";
    public const string GraphicsAsyncProducerSchema =
        "material-gi-graphics-async-comparison/v2";
    public const string TestMatrixProducerKind =
        "material-gi-test-matrix";
    public const string TestMatrixProducerSchema =
        "material-gi-test-matrix/v1";
    public const string BenchmarkProducerKind =
        "njulf-renderer-benchmark";
    public const string BenchmarkProducerSchema =
        "njulf-renderer-benchmark/v4";
    public const string BenchmarkDdgiTransientRawEvidenceSchema =
        "njulf-benchmark-ddgi-transient-raw-evidence/v1";
    public const string BenchmarkDdgiTransientEvidenceSchema =
        "njulf-benchmark-ddgi-transient-evidence/v2";
    public const string LongRunProducerKind =
        "material-gi-long-run-stability";
    public const string LongRunProducerSchema =
        "material-gi-long-run-stability/v3";
    public const string HealthProducerKind = "renderer-health";
    public const string HealthProducerSchema = "renderer-health/v3";

    public static IReadOnlyList<string> RequiredRoles { get; } =
        Array.AsReadOnly(
        [
            ApprovedHdrRole,
            KhronosRenderedSemanticRole,
            GraphicsAsyncEquivalenceRole,
            CpuGpuOracleReleaseMatrixRole,
            TierPerformanceMatrixRole,
            ThirtyMinuteSoakRole,
            CleanValidationRole,
            LifecycleResilienceRole,
            QualitySwitchRollbackRole,
            TextureHotReloadRollbackRole,
            RecoveryCapabilityRole
        ]);

    public static IReadOnlyList<string> RequiredQualityTiers { get; } =
        Array.AsReadOnly(["Low", "Medium", "High", "Ultra"]);

    public static IReadOnlyList<string> RequiredOracleReleaseChecks { get; } =
        Array.AsReadOnly(
        [
            "CpuOracle",
            "GpuOracle",
            "ReleaseBuild",
            "ReleaseTests"
        ]);

    public static IReadOnlyList<string> RequiredApprovedHdrChecks { get; } =
        Array.AsReadOnly(
        [
            "LinearHdrRegression",
            "RelativeRmse",
            "FlipP95"
        ]);

    public static IReadOnlyList<string> RequiredKhronosSemanticChecks { get; } =
        Array.AsReadOnly(
        [
            "OfficialKhronosAssets",
            "RenderedSemanticRois"
        ]);

    public static IReadOnlyList<string> RequiredGraphicsAsyncChecks { get; } =
        Array.AsReadOnly(
        [
            "GraphicsQueueCapture",
            "AsyncComputeCapture",
            "SemanticEquivalence"
        ]);

    public static IReadOnlyList<string> RequiredTierPerformanceChecks { get; } =
        Array.AsReadOnly(
        [
            "GpuBudget",
            "CpuBudget",
            "MemoryBudget",
            "ConvergenceBudget",
            "RayQueryCapability"
        ]);

    public static IReadOnlyList<string> RequiredSoakChecks { get; } =
        Array.AsReadOnly(
        [
            "DynamicMaterialMutation",
            "LongCameraPath",
            "MemoryTrend",
            "WorkloadRollback"
        ]);

    public static IReadOnlyList<string> RequiredValidationChecks { get; } =
        Array.AsReadOnly(["VulkanValidation"]);

    public static IReadOnlyList<string> RequiredLifecycleChecks { get; } =
        Array.AsReadOnly(
        [
            "resize",
            "minimize-zero-framebuffer",
            "restore-framebuffer",
            "scene-reload"
        ]);

    public static IReadOnlyList<string> RequiredQualitySwitchRollbackChecks
    { get; } =
        Array.AsReadOnly(
        [
            "quality-switch",
            "rollback-settings-restored",
            "post-rollback-frame"
        ]);

    public static IReadOnlyList<string> RequiredTextureHotReloadRollbackChecks
    { get; } =
        Array.AsReadOnly(
        [
            "texture-hot-reload",
            "rollback-source-hash-restored",
            "rollback-transport-restored",
            "descriptor-occupancy-stable"
        ]);

    public static IReadOnlyList<string> RequiredRecoveryCapabilityChecks
    { get; } =
        Array.AsReadOnly(
        [
            "capability-reported",
            "recovery-attempted-when-supported",
            "unsupported-capability-documented"
        ]);

    public static IReadOnlyList<string> GetRequiredCoveredChecks(string role) =>
        role switch
        {
            ApprovedHdrRole => RequiredApprovedHdrChecks,
            KhronosRenderedSemanticRole => RequiredKhronosSemanticChecks,
            GraphicsAsyncEquivalenceRole => RequiredGraphicsAsyncChecks,
            CpuGpuOracleReleaseMatrixRole => RequiredOracleReleaseChecks,
            TierPerformanceMatrixRole => RequiredTierPerformanceChecks,
            ThirtyMinuteSoakRole => RequiredSoakChecks,
            CleanValidationRole => RequiredValidationChecks,
            LifecycleResilienceRole => RequiredLifecycleChecks,
            QualitySwitchRollbackRole => RequiredQualitySwitchRollbackChecks,
            TextureHotReloadRollbackRole =>
                RequiredTextureHotReloadRollbackChecks,
            RecoveryCapabilityRole => RequiredRecoveryCapabilityChecks,
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown material-GI release evidence role.")
        };

    public static string ComputeAggregateSha256(
        MaterialGiReleaseEvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        MaterialGiReleaseEvidenceArtifact[] artifacts =
            bundle.Artifacts ?? Array.Empty<MaterialGiReleaseEvidenceArtifact>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAggregateField(hash, AggregateSchema);
        AppendAggregateField(
            hash,
            bundle.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        AppendAggregateField(
            hash,
            artifacts.Length.ToString(CultureInfo.InvariantCulture));
        AppendAggregateField(hash, bundle.BuildCommit ?? string.Empty);
        AppendAggregateField(hash, bundle.ShaderFingerprint ?? string.Empty);
        AppendAggregateField(
            hash,
            bundle.SettingsContractFingerprint ?? string.Empty);
        MaterialGiEvidenceDeviceIdentity[] devices =
            bundle.Devices ?? Array.Empty<MaterialGiEvidenceDeviceIdentity>();
        AppendAggregateField(
            hash,
            devices.Length.ToString(CultureInfo.InvariantCulture));
        foreach (MaterialGiEvidenceDeviceIdentity device in devices
                     .OrderBy(static device => device.DeviceId, StringComparer.Ordinal))
        {
            AppendAggregateField(hash, device.DeviceId ?? string.Empty);
            AppendAggregateField(hash, device.GpuName ?? string.Empty);
            AppendAggregateField(hash, device.DriverVersion ?? string.Empty);
        }
        foreach (MaterialGiReleaseEvidenceArtifact artifact in artifacts
                     .OrderBy(static artifact => artifact.Role, StringComparer.Ordinal)
                     .ThenBy(
                         static artifact => artifact.ManifestRelativePath,
                         StringComparer.Ordinal))
        {
            AppendAggregateField(hash, artifact.Role ?? string.Empty);
            AppendAggregateField(
                hash,
                artifact.ManifestRelativePath ?? string.Empty);
            AppendAggregateField(
                hash,
                artifact.ByteLength.ToString(CultureInfo.InvariantCulture));
            AppendAggregateField(hash, artifact.Sha256 ?? string.Empty);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendAggregateField(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record MaterialGiReleaseEvidenceBundle
{
    public int SchemaVersion { get; init; } =
        MaterialGiReleaseEvidenceContract.BundleSchemaVersion;

    public MaterialGiReleaseEvidenceArtifact[] Artifacts { get; init; } =
        Array.Empty<MaterialGiReleaseEvidenceArtifact>();

    public string BuildCommit { get; init; } = string.Empty;
    public string ShaderFingerprint { get; init; } = string.Empty;
    public string SettingsContractFingerprint { get; init; } = string.Empty;
    public MaterialGiEvidenceDeviceIdentity[] Devices { get; init; } =
        Array.Empty<MaterialGiEvidenceDeviceIdentity>();
}

public sealed record MaterialGiReleaseEvidenceArtifact
{
    public string Role { get; init; } = string.Empty;
    public string ManifestRelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record MaterialGiReleaseEvidenceReport
{
    public int SchemaVersion { get; init; } =
        MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion;

    public string Role { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string ShaderFingerprint { get; init; } = string.Empty;
    public string SettingsContractFingerprint { get; init; } = string.Empty;
    public string[] DeviceIds { get; init; } = Array.Empty<string>();
    public MaterialGiEvidenceDeviceIdentity[] Devices { get; init; } =
        Array.Empty<MaterialGiEvidenceDeviceIdentity>();
    public MaterialGiProducerEvidenceArtifact[] Producers { get; init; } =
        Array.Empty<MaterialGiProducerEvidenceArtifact>();
    public int? DurationSeconds { get; init; }
    public string[] QualityTiers { get; init; } = Array.Empty<string>();
    public string[] CoveredChecks { get; init; } = Array.Empty<string>();
    public MaterialGiTierDeviceEvidence[] TierDevices { get; init; } =
        Array.Empty<MaterialGiTierDeviceEvidence>();
    public MaterialGiRecoveryDeviceEvidence[] RecoveryDevices { get; init; } =
        Array.Empty<MaterialGiRecoveryDeviceEvidence>();
    public bool? ValidationEnabled { get; init; }
    public int? ValidationWarningCount { get; init; }
    public int? ValidationErrorCount { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed record MaterialGiEvidenceDeviceIdentity
{
    public string DeviceId { get; init; } = string.Empty;
    public string GpuName { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
}

public sealed record MaterialGiProducerEvidenceArtifact
{
    public string Kind { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string ManifestRelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string GpuName { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string ShaderFingerprint { get; init; } = string.Empty;
    public string SettingsFingerprint { get; init; } = string.Empty;
    public string QualityTier { get; init; } = string.Empty;
}

/// <summary>
/// Identity emitted by the producer itself. The qualification wrapper pins the
/// same values, but cannot substitute them: authenticity validation reads this
/// object from the producer payload and requires an exact match.
/// </summary>
public sealed record MaterialGiProducerIdentity
{
    public const string CurrentSchema = "material-gi-producer-identity/v1";

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("buildCommit")]
    public string BuildCommit { get; init; } = string.Empty;

    [JsonPropertyName("shaderFingerprint")]
    public string ShaderFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("settingsFingerprint")]
    public string SettingsFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("sourceSettingsFingerprints")]
    public string[] SourceSettingsFingerprints { get; init; } =
        Array.Empty<string>();

    [JsonPropertyName("gpuName")]
    public string GpuName { get; init; } = string.Empty;

    [JsonPropertyName("driverVersion")]
    public string DriverVersion { get; init; } = string.Empty;

    [JsonPropertyName("qualityTier")]
    public string QualityTier { get; init; } = string.Empty;
}

/// <summary>
/// Canonical settings identities used at producer boundaries. Runtime helpers
/// may report a "sha256:" prefix; release evidence always stores lowercase
/// hexadecimal. Graphics/async comparison binds both deliberately different
/// settings snapshots through a length-framed ordered aggregate.
/// </summary>
public static class MaterialGiProducerSettingsFingerprint
{
    public static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string prefix = "sha256:";
        string normalized = value.Trim();
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];
        if (normalized.Length != 64 ||
            normalized.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "A SHA-256 fingerprint must contain exactly 64 hexadecimal characters, optionally prefixed by 'sha256:'.",
                nameof(value));
        }
        return normalized.ToLowerInvariant();
    }

    public static string ComputeGraphicsAsyncPair(
        string graphicsSettingsFingerprint,
        string asyncSettingsFingerprint)
    {
        string graphics = NormalizeSha256(graphicsSettingsFingerprint);
        string async = NormalizeSha256(asyncSettingsFingerprint);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFramed(hash, "material-gi-graphics-async-settings/v1");
        AppendFramed(hash, "graphics");
        AppendFramed(hash, graphics);
        AppendFramed(hash, "async");
        AppendFramed(hash, async);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFramed(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

/// <summary>
/// Strict producer contract for Release build/test and CPU/GPU oracle jobs,
/// which do not otherwise emit a stable machine-readable report.
/// </summary>
public sealed record MaterialGiTestMatrixProducerReport
{
    public string Schema { get; init; } =
        MaterialGiReleaseEvidenceContract.TestMatrixProducerSchema;
    public string Kind { get; init; } =
        MaterialGiReleaseEvidenceContract.TestMatrixProducerKind;
    public string Status { get; init; } = string.Empty;
    public string BuildConfiguration { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string ShaderFingerprint { get; init; } = string.Empty;
    public string SettingsFingerprint { get; init; } = string.Empty;
    public MaterialGiEvidenceDeviceIdentity Device { get; init; } = new();
    public MaterialGiTestMatrixProducerResult[] Results { get; init; } =
        Array.Empty<MaterialGiTestMatrixProducerResult>();
}

public sealed record MaterialGiTestMatrixProducerResult
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int PassedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
}

public sealed record MaterialGiTierDeviceEvidence
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceClass { get; init; } = string.Empty;
    public long DeviceLocalMemoryBytes { get; init; }
    public bool RayQuerySupported { get; init; }
    public string[] QualityTiers { get; init; } = Array.Empty<string>();
}

public sealed record MaterialGiRecoveryDeviceEvidence
{
    public string DeviceId { get; init; } = string.Empty;
    public bool Supported { get; init; }
    public bool Attempted { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// External release evidence required before material-GI V2 may be selected as
/// a shipping default. This evidence is intentionally not part of ordinary
/// render-settings persistence: copying a user settings file must never promote
/// an unqualified renderer to the shipping V2 path.
/// </summary>
public sealed record MaterialGiRolloutQualificationManifest
{
    public const int CurrentSchemaVersion = 7;
    public const int PreviousSchemaVersion = 6;

    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumJsonDepth = 32;

    private static readonly JsonSerializerOptions ManifestJsonOptions =
        CreateManifestJsonOptions();

    private static readonly JsonSerializerOptions ReleaseEvidenceJsonOptions =
        CreateStrictJsonOptions();

    [JsonIgnore]
    private QualificationAuthenticationSeal? _authenticationSeal;

    [JsonIgnore]
    public int AuthenticatedReleaseEvidenceRoleCount =>
        _authenticationSeal?.ReleaseEvidenceRoleCount ?? 0;

    [JsonIgnore]
    public int AuthenticatedTierDeviceCount =>
        _authenticationSeal?.TierDeviceCount ?? 0;

    [JsonIgnore]
    public int AuthenticatedLowerMemoryRayQueryDeviceCount =>
        _authenticationSeal?.LowerMemoryRayQueryDeviceCount ?? 0;

    [JsonIgnore]
    public string AuthenticatedRecoveryCapabilitySummary =>
        _authenticationSeal?.RecoveryCapabilitySummary ?? string.Empty;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public MaterialGiV2Feature EnabledFeatures { get; init; } = MaterialGiV2Feature.All;
    public string[] QualifiedDeviceIds { get; init; } = Array.Empty<string>();
    public string ReleaseEvidenceBundleRelativePath { get; init; } = string.Empty;
    public string ReleaseEvidenceBundleSha256 { get; init; } = string.Empty;
    public string EvidenceSha256 { get; init; } = string.Empty;
    public string ApprovalId { get; init; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; init; }
    public string AlphaVisibilityReportRelativePath { get; init; } = string.Empty;
    public string AlphaVisibilityReportSha256 { get; init; } = string.Empty;
    public string AlphaVisibilityEvidenceRelativePath { get; init; } = string.Empty;
    public string AlphaVisibilityEvidenceSha256 { get; init; } = string.Empty;
    public string V1RemovalOwner { get; init; } = MaterialGiV1CompatibilityContract.Owner;
    public DateOnly V1RemovalTargetDate { get; init; } =
        MaterialGiV1CompatibilityContract.RemovalTargetDate;
    public int V1RetainedReleaseWindowCount { get; init; } =
        MaterialGiV1CompatibilityContract.RetainedReleaseWindowCount;

    public IReadOnlyList<string> Validate(DateOnly evaluationDate)
    {
        var failures = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            failures.Add(CreateQualificationSchemaFailure(SchemaVersion));
        }

        if (_authenticationSeal is null)
        {
            failures.Add(
                "Qualification must be loaded from a manifest and authenticated against " +
                "its release evidence bundle and alpha-visibility artifacts.");
        }
        else
        {
            if (!_authenticationSeal.Matches(this))
            {
                failures.Add(
                    "Qualification manifest fields no longer match their load-time authentication seal.");
            }
            if (!ContainsQualifiedDevice(
                    QualifiedDeviceIds,
                    _authenticationSeal.AlphaVisibilityDeviceName))
            {
                failures.Add(
                    $"The authenticated alpha-visibility device '{_authenticationSeal.AlphaVisibilityDeviceName}' " +
                    "is not represented in QualifiedDeviceIds.");
            }
            if (_authenticationSeal.ReleaseEvidenceDeviceIds.Any(deviceId =>
                    !ContainsQualifiedDevice(QualifiedDeviceIds, deviceId)))
            {
                failures.Add(
                    "One or more authenticated release evidence devices are not represented in QualifiedDeviceIds.");
            }
        }

        if (EnabledFeatures == MaterialGiV2Feature.None ||
            (EnabledFeatures & ~MaterialGiV2Feature.All) != 0)
        {
            failures.Add("Qualification must enable at least one known material-GI V2 feature.");
        }

        int distinctDeviceCount = (QualifiedDeviceIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctDeviceCount < 2)
            failures.Add("At least two distinct qualified device identifiers are required.");
        if (QualifiedDeviceIds is null ||
            QualifiedDeviceIds.Any(static id =>
                string.IsNullOrWhiteSpace(id) ||
                !string.Equals(id, id.Trim(), StringComparison.Ordinal)) ||
            distinctDeviceCount != QualifiedDeviceIds.Length)
        {
            failures.Add(
                "QualifiedDeviceIds must contain only canonical, non-duplicate device identifiers.");
        }
        if (!IsManifestRelativeArtifactPath(ReleaseEvidenceBundleRelativePath))
        {
            failures.Add(
                "ReleaseEvidenceBundleRelativePath must be a canonical manifest-relative file path without traversal.");
        }
        if (!IsSha256(ReleaseEvidenceBundleSha256))
        {
            failures.Add(
                "ReleaseEvidenceBundleSha256 must contain exactly 64 hexadecimal characters.");
        }
        if (!IsSha256(EvidenceSha256))
        {
            failures.Add(
                "EvidenceSha256 must contain the recomputed 64-character release evidence aggregate.");
        }
        if (!IsManifestRelativeArtifactPath(AlphaVisibilityReportRelativePath))
        {
            failures.Add(
                "AlphaVisibilityReportRelativePath must be a canonical manifest-relative file path without traversal.");
        }
        if (!IsSha256(AlphaVisibilityReportSha256))
        {
            failures.Add(
                "AlphaVisibilityReportSha256 must contain exactly 64 hexadecimal characters.");
        }
        if (!IsManifestRelativeArtifactPath(AlphaVisibilityEvidenceRelativePath))
        {
            failures.Add(
                "AlphaVisibilityEvidenceRelativePath must be a canonical manifest-relative file path without traversal.");
        }
        if (!IsSha256(AlphaVisibilityEvidenceSha256))
        {
            failures.Add(
                "AlphaVisibilityEvidenceSha256 must contain exactly 64 hexadecimal characters.");
        }
        if (string.IsNullOrWhiteSpace(ApprovalId))
            failures.Add("A non-empty release approval identifier is required.");
        if (ApprovedAtUtc == default)
            failures.Add("The UTC approval timestamp is required.");
        else
        {
            if (ApprovedAtUtc.Offset != TimeSpan.Zero)
                failures.Add("ApprovedAtUtc must use a zero UTC offset.");
            DateOnly approvalDate = DateOnly.FromDateTime(ApprovedAtUtc.UtcDateTime);
            if (approvalDate > evaluationDate)
                failures.Add("The release approval timestamp cannot be in the future.");
            if (approvalDate >= V1RemovalTargetDate)
                failures.Add("The V1 removal target must be later than the release approval.");
        }
        if (!string.Equals(
                V1RemovalOwner?.Trim(),
                MaterialGiV1CompatibilityContract.Owner,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"The V1 removal owner must be '{MaterialGiV1CompatibilityContract.Owner}'.");
        }
        if (V1RemovalTargetDate != MaterialGiV1CompatibilityContract.RemovalTargetDate)
        {
            failures.Add(
                $"The V1 removal target must be {MaterialGiV1CompatibilityContract.RemovalTargetDate:yyyy-MM-dd}.");
        }
        if (V1RetainedReleaseWindowCount !=
            MaterialGiV1CompatibilityContract.RetainedReleaseWindowCount)
        {
            failures.Add("The V1 compatibility path may be retained for exactly one release window.");
        }
        if (evaluationDate > V1RemovalTargetDate)
        {
            failures.Add(
                $"The V1 removal target {V1RemovalTargetDate:yyyy-MM-dd} has expired.");
        }

        return failures;
    }

    public static MaterialGiRolloutQualificationManifest Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Qualification manifest path cannot be empty.", nameof(path));
        string manifestPath = Path.GetFullPath(path);
        byte[] manifestBytes;
        try
        {
            manifestBytes = BoundedFileReader.ReadStable(
                manifestPath,
                MaximumManifestBytes,
                "Qualification manifest");
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException(
                "Material-GI qualification manifest was not found.",
                manifestPath,
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException(
                "Material-GI qualification manifest was not found.",
                manifestPath,
                exception);
        }

        MaterialGiRolloutQualificationManifest manifest;
        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                manifestBytes,
                MaximumJsonDepth,
                "Qualification manifest");
            manifest =
                JsonSerializer.Deserialize<MaterialGiRolloutQualificationManifest>(
                    manifestBytes,
                    ManifestJsonOptions)
                ?? throw new InvalidDataException(
                    $"Qualification manifest '{manifestPath}' did not contain a valid object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Qualification manifest '{manifestPath}' contains invalid or unknown JSON metadata.",
                exception);
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                CreateQualificationSchemaFailure(manifest.SchemaVersion));

        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException(
                "Qualification manifest path has no parent directory.");
        string bundlePath = ResolveContainedArtifactPath(
            manifestDirectory,
            manifest.ReleaseEvidenceBundleRelativePath,
            nameof(ReleaseEvidenceBundleRelativePath));
        string reportPath = ResolveContainedArtifactPath(
            manifestDirectory,
            manifest.AlphaVisibilityReportRelativePath,
            nameof(AlphaVisibilityReportRelativePath));
        string evidencePath = ResolveContainedArtifactPath(
            manifestDirectory,
            manifest.AlphaVisibilityEvidenceRelativePath,
            nameof(AlphaVisibilityEvidenceRelativePath));
        if (new[] { bundlePath, reportPath, evidencePath }
                .Distinct(PathComparer)
                .Count() != 3)
        {
            throw new InvalidDataException(
                "Release evidence bundle, alpha-visibility report, and alpha-visibility " +
                "evidence paths must identify distinct files.");
        }

        AuthenticatedReleaseEvidence releaseEvidence =
            AuthenticateReleaseEvidence(
                manifestDirectory,
                bundlePath,
                manifest.ReleaseEvidenceBundleSha256,
                manifest.EvidenceSha256,
                manifest.QualifiedDeviceIds,
                [manifestPath, bundlePath, reportPath, evidencePath]);
        using FileStream reportLease = OpenPinnedArtifact(
            reportPath,
            manifest.AlphaVisibilityReportSha256,
            AlphaVisibilityConformanceContract.MaximumReportBytes,
            "alpha-visibility report",
            expectedByteLength: null);
        using FileStream evidenceLease = OpenPinnedArtifact(
            evidencePath,
            manifest.AlphaVisibilityEvidenceSha256,
            AlphaVisibilityConformanceContract.MaximumEvidenceBytes,
            "alpha-visibility evidence",
            expectedByteLength: null);
        byte[] reportBytes = ReadPinnedArtifactBytes(
            reportLease,
            "alpha-visibility report",
            manifest.AlphaVisibilityReportSha256);
        byte[] evidenceBytes = ReadPinnedArtifactBytes(
            evidenceLease,
            "alpha-visibility evidence",
            manifest.AlphaVisibilityEvidenceSha256);
        AlphaVisibilityConformanceReport report =
            AlphaVisibilityConformanceReports.AuthenticatePassed(
                reportBytes,
                reportPath,
                evidenceBytes,
                evidencePath);
        if (!ContainsQualifiedDevice(manifest.QualifiedDeviceIds, report.DeviceName))
        {
            throw new InvalidDataException(
                $"Authenticated alpha-visibility device '{report.DeviceName}' is not represented " +
                "in QualifiedDeviceIds.");
        }

        manifest._authenticationSeal = QualificationAuthenticationSeal.Capture(
            manifest,
            manifestPath,
            bundlePath,
            reportPath,
            evidencePath,
            report.DeviceName,
            releaseEvidence);
        return manifest;
    }

    private static string CreateQualificationSchemaFailure(int schemaVersion)
    {
        if (schemaVersion == PreviousSchemaVersion)
        {
            return
                $"Qualification schema {schemaVersion} is a legacy contract and " +
                "cannot be migrated implicitly because it lacks authenticated " +
                "producer payloads and build, shader, settings, GPU, and driver identity; " +
                $"regenerate the qualification using schema {CurrentSchemaVersion}.";
        }
        return
            $"Qualification schema {schemaVersion} is unsupported; expected " +
            $"{CurrentSchemaVersion}.";
    }

    private static JsonSerializerOptions CreateManifestJsonOptions()
    {
        JsonSerializerOptions options = CreateStrictJsonOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateStrictJsonOptions() =>
        new()
        {
            AllowTrailingCommas = false,
            MaxDepth = MaximumJsonDepth,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static AuthenticatedReleaseEvidence AuthenticateReleaseEvidence(
        string manifestDirectory,
        string bundlePath,
        string? expectedBundleSha256,
        string? expectedAggregateSha256,
        IEnumerable<string>? qualifiedDeviceIds,
        IEnumerable<string> reservedPaths)
    {
        using FileStream bundleLease = OpenPinnedArtifact(
            bundlePath,
            expectedBundleSha256,
            MaterialGiReleaseEvidenceContract.MaximumBundleBytes,
            "release evidence bundle",
            expectedByteLength: null);
        byte[] bundleBytes = ReadPinnedArtifactBytes(
            bundleLease,
            "release evidence bundle",
            expectedBundleSha256);

        MaterialGiReleaseEvidenceBundle bundle;
        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                bundleBytes,
                MaximumJsonDepth,
                "Release evidence bundle");
            bundle =
                JsonSerializer.Deserialize<MaterialGiReleaseEvidenceBundle>(
                    bundleBytes,
                    ReleaseEvidenceJsonOptions)
                ?? throw new InvalidDataException(
                    "Release evidence bundle JSON did not contain an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Release evidence bundle contains invalid or unknown JSON metadata.",
                exception);
        }

        if (bundle.SchemaVersion !=
            MaterialGiReleaseEvidenceContract.BundleSchemaVersion)
        {
            string migration = bundle.SchemaVersion ==
                MaterialGiReleaseEvidenceContract.PreviousBundleSchemaVersion
                ? " The legacy bundle cannot be migrated implicitly; regenerate it " +
                  "with authenticated producer payloads and exact release identities."
                : string.Empty;
            throw new InvalidDataException(
                $"Release evidence bundle schema {bundle.SchemaVersion} is unsupported; " +
                $"expected {MaterialGiReleaseEvidenceContract.BundleSchemaVersion}." +
                migration);
        }
        MaterialGiReleaseEvidenceArtifact[] artifacts =
            bundle.Artifacts ??
            throw new InvalidDataException(
                "Release evidence bundle artifact collection is null.");
        if (artifacts.Length == 0 ||
            artifacts.Length > MaterialGiReleaseEvidenceContract.RequiredRoles.Count)
        {
            throw new InvalidDataException(
                "Release evidence bundle must contain exactly one artifact for every required role.");
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(reservedPaths, PathComparer);
        var qualifiedDevices = new HashSet<string>(
            (qualifiedDeviceIds ?? Array.Empty<string>())
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var evidenceDevices = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
            authenticatedDeviceIdentities =
                MaterialGiReleaseEvidenceAuthenticity.ValidateBundleIdentity(
                    bundle,
                    qualifiedDevices);
        var artifactPaths = new Dictionary<
            MaterialGiReleaseEvidenceArtifact,
            string>(ReferenceEqualityComparer.Instance);
        var allPinnedPaths = new HashSet<string>(reserved, PathComparer);
        foreach (MaterialGiReleaseEvidenceArtifact artifact in artifacts)
        {
            if (artifact is null)
            {
                throw new InvalidDataException(
                    "Release evidence bundle contains a null artifact entry.");
            }
            string artifactPath = ResolveContainedArtifactPath(
                manifestDirectory,
                artifact.ManifestRelativePath,
                $"release evidence role '{artifact.Role}' path");
            if (!allPinnedPaths.Add(artifactPath))
            {
                throw new InvalidDataException(
                    $"Release evidence artifact path '{artifact.ManifestRelativePath}' is duplicated or aliases a reserved qualification file.");
            }
            artifactPaths.Add(artifact, artifactPath);
        }
        int tierDeviceCount = 0;
        int lowerMemoryRayQueryDeviceCount = 0;
        int recoverySupportedDeviceCount = 0;
        int recoveryUnsupportedDeviceCount = 0;

        foreach (MaterialGiReleaseEvidenceArtifact artifact in artifacts)
        {
            if (artifact is null)
            {
                throw new InvalidDataException(
                    "Release evidence bundle contains a null artifact entry.");
            }
            if (!MaterialGiReleaseEvidenceContract.RequiredRoles.Contains(
                    artifact.Role,
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Release evidence role '{artifact.Role}' is unknown.");
            }
            if (!roles.Add(artifact.Role))
            {
                throw new InvalidDataException(
                    $"Release evidence role '{artifact.Role}' is duplicated.");
            }
            if (artifact.ByteLength <= 0 ||
                artifact.ByteLength >
                    MaterialGiReleaseEvidenceContract.MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"Release evidence role '{artifact.Role}' has an invalid bounded byte length.");
            }

            string artifactPath = artifactPaths[artifact];
            using FileStream artifactLease = OpenPinnedArtifact(
                artifactPath,
                artifact.Sha256,
                MaterialGiReleaseEvidenceContract.MaximumArtifactBytes,
                $"release evidence role '{artifact.Role}'",
                artifact.ByteLength);
            MaterialGiReleaseEvidenceReport report =
                ReadReleaseEvidenceReport(
                    artifactLease,
                    artifact.Role,
                    artifact.Sha256);
            ValidateEvidenceDevices(
                report,
                qualifiedDevices,
                evidenceDevices);
            EvidenceRoleAuthentication roleAuthentication =
                ValidateEvidenceRoleMetadata(report, qualifiedDevices);
            MaterialGiReleaseEvidenceAuthenticity.ValidateRole(
                manifestDirectory,
                bundle,
                report,
                authenticatedDeviceIdentities,
                allPinnedPaths);
            tierDeviceCount = checked(
                tierDeviceCount + roleAuthentication.TierDeviceCount);
            lowerMemoryRayQueryDeviceCount = checked(
                lowerMemoryRayQueryDeviceCount +
                roleAuthentication.LowerMemoryRayQueryDeviceCount);
            recoverySupportedDeviceCount = checked(
                recoverySupportedDeviceCount +
                roleAuthentication.RecoverySupportedDeviceCount);
            recoveryUnsupportedDeviceCount = checked(
                recoveryUnsupportedDeviceCount +
                roleAuthentication.RecoveryUnsupportedDeviceCount);
        }

        string[] missingRoles = MaterialGiReleaseEvidenceContract.RequiredRoles
            .Where(role => !roles.Contains(role))
            .ToArray();
        if (missingRoles.Length > 0)
        {
            throw new InvalidDataException(
                "Release evidence bundle is missing required role(s): " +
                string.Join(", ", missingRoles) +
                ".");
        }
        if (evidenceDevices.Count < 2)
        {
            throw new InvalidDataException(
                "Release evidence must be produced on at least two distinct qualified devices.");
        }

        string actualAggregateSha256 =
            MaterialGiReleaseEvidenceContract.ComputeAggregateSha256(bundle);
        RequireFixedSha256(
            actualAggregateSha256,
            expectedAggregateSha256,
            "release evidence aggregate");
        return new AuthenticatedReleaseEvidence(
            evidenceDevices
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            roles.Count,
            tierDeviceCount,
            lowerMemoryRayQueryDeviceCount,
            recoverySupportedDeviceCount,
            recoveryUnsupportedDeviceCount);
    }

    private static MaterialGiReleaseEvidenceReport ReadReleaseEvidenceReport(
        FileStream artifactLease,
        string expectedRole,
        string expectedSha256)
    {
        byte[] bytes = ReadPinnedArtifactBytes(
            artifactLease,
            $"release evidence role '{expectedRole}'",
            expectedSha256);
        MaterialGiReleaseEvidenceReport report;
        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                bytes,
                MaximumJsonDepth,
                $"Release evidence role '{expectedRole}' artifact");
            report =
                JsonSerializer.Deserialize<MaterialGiReleaseEvidenceReport>(
                    bytes,
                    ReleaseEvidenceJsonOptions)
                ?? throw new InvalidDataException(
                    $"Release evidence role '{expectedRole}' artifact JSON is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Release evidence role '{expectedRole}' artifact contains invalid " +
                "or unknown JSON metadata.",
                exception);
        }

        if (report.SchemaVersion !=
            MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion)
        {
            string migration = report.SchemaVersion ==
                MaterialGiReleaseEvidenceContract.PreviousArtifactSchemaVersion
                ? " The legacy artifact cannot be migrated implicitly; regenerate " +
                  "it with pinned producer reports and exact release identities."
                : string.Empty;
            throw new InvalidDataException(
                $"Release evidence role '{expectedRole}' artifact schema " +
                $"{report.SchemaVersion} is unsupported; expected " +
                $"{MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion}." +
                migration);
        }
        if (!string.Equals(
                report.Role,
                expectedRole,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release evidence artifact role '{report.Role}' does not match " +
                $"bundle role '{expectedRole}'.");
        }
        if (!string.Equals(
                report.Status,
                MaterialGiReleaseEvidenceContract.PassedStatus,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release evidence role '{expectedRole}' status is " +
                $"'{report.Status}', not Passed.");
        }
        if (string.IsNullOrWhiteSpace(report.Summary) ||
            !string.Equals(
                report.Summary,
                report.Summary.Trim(),
                StringComparison.Ordinal) ||
            report.Summary.Length > 4096)
        {
            throw new InvalidDataException(
                $"Release evidence role '{expectedRole}' has no bounded canonical summary.");
        }
        return report;
    }

    private static void ValidateEvidenceDevices(
        MaterialGiReleaseEvidenceReport report,
        IReadOnlySet<string> qualifiedDevices,
        ISet<string> evidenceDevices)
    {
        string[] deviceIds = report.DeviceIds ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' device collection is null.");
        if (deviceIds.Length == 0 ||
            deviceIds.Any(static id =>
                string.IsNullOrWhiteSpace(id) ||
                !string.Equals(id, id.Trim(), StringComparison.Ordinal)) ||
            deviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                deviceIds.Length)
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' must name canonical, " +
                "non-duplicate device identifiers.");
        }

        foreach (string deviceId in deviceIds)
        {
            if (!qualifiedDevices.Contains(deviceId))
            {
                throw new InvalidDataException(
                    $"Release evidence role '{report.Role}' device '{deviceId}' " +
                    "is not represented in QualifiedDeviceIds.");
            }
            evidenceDevices.Add(deviceId);
        }
    }

    internal static void ValidateReleaseEvidenceRoleForAssembly(
        MaterialGiReleaseEvidenceReport report,
        IReadOnlySet<string> qualifiedDevices)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(qualifiedDevices);
        var authenticatedDevices = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        ValidateEvidenceDevices(
            report,
            qualifiedDevices,
            authenticatedDevices);
        _ = ValidateEvidenceRoleMetadata(report, qualifiedDevices);
    }

    private static EvidenceRoleAuthentication ValidateEvidenceRoleMetadata(
        MaterialGiReleaseEvidenceReport report,
        IReadOnlySet<string> qualifiedDevices)
    {
        string[] qualityTiers = report.QualityTiers ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' quality tier collection is null.");
        string[] coveredChecks = report.CoveredChecks ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' covered-check collection is null.");
        MaterialGiTierDeviceEvidence[] tierDevices = report.TierDevices ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' tier-device collection is null.");
        MaterialGiRecoveryDeviceEvidence[] recoveryDevices =
            report.RecoveryDevices ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' recovery-device collection is null.");

        RequireExactEvidenceSet(
            coveredChecks,
            MaterialGiReleaseEvidenceContract.GetRequiredCoveredChecks(
                report.Role),
            $"release evidence role '{report.Role}'",
            "covered checks");

        if (string.Equals(
                report.Role,
                MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole,
                StringComparison.Ordinal))
        {
            if (report.DurationSeconds is null ||
                report.DurationSeconds <
                    MaterialGiReleaseEvidenceContract.MinimumSoakDurationSeconds)
            {
                throw new InvalidDataException(
                    "Thirty-minute soak evidence must record at least 1800 seconds.");
            }
        }
        else if (report.DurationSeconds is not null)
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' contains inapplicable duration metadata.");
        }

        if (string.Equals(
                report.Role,
                MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                StringComparison.Ordinal))
        {
            RequireExactEvidenceSet(
                qualityTiers,
                MaterialGiReleaseEvidenceContract.RequiredQualityTiers,
                "tier performance evidence",
                "quality tiers");
            ValidateTierDeviceEvidence(
                report,
                tierDevices,
                qualifiedDevices,
                out int lowerMemoryRayQueryDeviceCount);
            if (recoveryDevices.Length != 0)
            {
                throw new InvalidDataException(
                    "Tier performance evidence contains inapplicable recovery-device metadata.");
            }
            ValidateValidationMetadata(report);
            return new EvidenceRoleAuthentication(
                tierDevices.Length,
                lowerMemoryRayQueryDeviceCount,
                RecoverySupportedDeviceCount: 0,
                RecoveryUnsupportedDeviceCount: 0);
        }
        if (qualityTiers.Length != 0)
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' contains inapplicable quality tiers.");
        }
        if (tierDevices.Length != 0)
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' contains inapplicable tier-device metadata.");
        }

        if (string.Equals(
                report.Role,
                MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole,
                StringComparison.Ordinal))
        {
            ValidateRecoveryDeviceEvidence(
                report,
                recoveryDevices,
                qualifiedDevices,
                out int supportedDeviceCount,
                out int unsupportedDeviceCount);
            ValidateValidationMetadata(report);
            return new EvidenceRoleAuthentication(
                TierDeviceCount: 0,
                LowerMemoryRayQueryDeviceCount: 0,
                supportedDeviceCount,
                unsupportedDeviceCount);
        }
        if (recoveryDevices.Length != 0)
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' contains inapplicable recovery-device metadata.");
        }

        ValidateValidationMetadata(report);
        return default;
    }

    private static void ValidateValidationMetadata(
        MaterialGiReleaseEvidenceReport report)
    {
        if (string.Equals(
                report.Role,
                MaterialGiReleaseEvidenceContract.CleanValidationRole,
                StringComparison.Ordinal))
        {
            if (report.ValidationEnabled != true ||
                report.ValidationWarningCount != 0 ||
                report.ValidationErrorCount != 0)
            {
                throw new InvalidDataException(
                    "Clean validation evidence requires validation enabled with zero warnings and errors.");
            }
        }
        else if (report.ValidationEnabled is not null ||
                 report.ValidationWarningCount is not null ||
                 report.ValidationErrorCount is not null)
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' contains inapplicable validation metadata.");
        }
    }

    private static void ValidateTierDeviceEvidence(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiTierDeviceEvidence[] tierDevices,
        IReadOnlySet<string> qualifiedDevices,
        out int lowerMemoryRayQueryDeviceCount)
    {
        if (tierDevices.Length < 2 ||
            tierDevices.Any(static device => device is null))
        {
            throw new InvalidDataException(
                "Tier performance evidence requires structured results for at least " +
                "two qualified devices.");
        }

        string[] reportDeviceIds = report.DeviceIds ??
            throw new InvalidDataException(
                "Tier performance evidence device collection is null.");
        string[] tierDeviceIds =
            [.. tierDevices.Select(static device => device.DeviceId)];
        RequireExactEvidenceSet(
            reportDeviceIds,
            qualifiedDevices
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            "tier performance evidence",
            "qualified device identifiers");
        RequireExactEvidenceSet(
            tierDeviceIds,
            reportDeviceIds,
            "tier performance evidence",
            "structured device identifiers");

        int referenceDeviceCount = 0;
        lowerMemoryRayQueryDeviceCount = 0;
        long smallestReferenceMemoryBytes = long.MaxValue;
        long largestLowerMemoryBytes = 0;
        foreach (MaterialGiTierDeviceEvidence device in tierDevices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceId) ||
                !string.Equals(
                    device.DeviceId,
                    device.DeviceId.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Tier performance evidence contains a non-canonical device identifier.");
            }
            if (!device.RayQuerySupported)
            {
                throw new InvalidDataException(
                    $"Tier performance device '{device.DeviceId}' does not prove " +
                    "required Vulkan ray-query capability.");
            }
            if (device.DeviceLocalMemoryBytes <= 0 ||
                device.DeviceLocalMemoryBytes >
                    MaterialGiReleaseEvidenceContract
                        .MaximumReportedDeviceLocalMemoryBytes)
            {
                throw new InvalidDataException(
                    $"Tier performance device '{device.DeviceId}' has invalid " +
                    "bounded device-local memory metadata.");
            }
            RequireExactEvidenceSet(
                device.QualityTiers ??
                    throw new InvalidDataException(
                        $"Tier performance device '{device.DeviceId}' quality " +
                        "tier collection is null."),
                MaterialGiReleaseEvidenceContract.RequiredQualityTiers,
                $"tier performance device '{device.DeviceId}'",
                "quality tiers");

            if (string.Equals(
                    device.DeviceClass,
                    MaterialGiReleaseEvidenceContract.ReferenceDeviceClass,
                    StringComparison.Ordinal))
            {
                referenceDeviceCount++;
                smallestReferenceMemoryBytes = Math.Min(
                    smallestReferenceMemoryBytes,
                    device.DeviceLocalMemoryBytes);
            }
            else if (string.Equals(
                         device.DeviceClass,
                         MaterialGiReleaseEvidenceContract
                             .LowerMemoryRayQueryDeviceClass,
                         StringComparison.Ordinal))
            {
                lowerMemoryRayQueryDeviceCount++;
                largestLowerMemoryBytes = Math.Max(
                    largestLowerMemoryBytes,
                    device.DeviceLocalMemoryBytes);
            }
            else
            {
                throw new InvalidDataException(
                    $"Tier performance device '{device.DeviceId}' has unknown " +
                    $"device class '{device.DeviceClass}'.");
            }
        }

        if (referenceDeviceCount == 0 ||
            lowerMemoryRayQueryDeviceCount == 0)
        {
            throw new InvalidDataException(
                "Tier performance evidence must include both an established " +
                "reference device and a lower-memory ray-query device.");
        }
        if (largestLowerMemoryBytes >= smallestReferenceMemoryBytes)
        {
            throw new InvalidDataException(
                "Tier performance evidence does not prove that the designated " +
                "lower-memory ray-query device has less device-local memory than " +
                "the reference device.");
        }
    }

    private static void ValidateRecoveryDeviceEvidence(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiRecoveryDeviceEvidence[] recoveryDevices,
        IReadOnlySet<string> qualifiedDevices,
        out int supportedDeviceCount,
        out int unsupportedDeviceCount)
    {
        if (recoveryDevices.Length < 2 ||
            recoveryDevices.Any(static device => device is null))
        {
            throw new InvalidDataException(
                "Recovery capability evidence requires structured results for at " +
                "least two qualified devices.");
        }

        string[] reportDeviceIds = report.DeviceIds ??
            throw new InvalidDataException(
                "Recovery capability evidence device collection is null.");
        string[] recoveryDeviceIds =
            [.. recoveryDevices.Select(static device => device.DeviceId)];
        RequireExactEvidenceSet(
            reportDeviceIds,
            qualifiedDevices
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            "recovery capability evidence",
            "qualified device identifiers");
        RequireExactEvidenceSet(
            recoveryDeviceIds,
            reportDeviceIds,
            "recovery capability evidence",
            "structured device identifiers");

        supportedDeviceCount = 0;
        unsupportedDeviceCount = 0;
        foreach (MaterialGiRecoveryDeviceEvidence device in recoveryDevices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceId) ||
                !string.Equals(
                    device.DeviceId,
                    device.DeviceId.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Recovery capability evidence contains a non-canonical device identifier.");
            }

            if (device.Supported)
            {
                if (!device.Attempted ||
                    !string.Equals(
                        device.Status,
                        MaterialGiReleaseEvidenceContract.PassedStatus,
                        StringComparison.Ordinal) ||
                    device.Reason is not { Length: 0 })
                {
                    throw new InvalidDataException(
                        $"Recovery-capable device '{device.DeviceId}' must record " +
                        "an attempted, Passed recovery with no unsupported reason.");
                }
                supportedDeviceCount++;
                continue;
            }

            if (device.Attempted ||
                !string.Equals(
                    device.Status,
                    MaterialGiReleaseEvidenceContract.UnsupportedStatus,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(device.Reason) ||
                !string.Equals(
                    device.Reason,
                    device.Reason.Trim(),
                    StringComparison.Ordinal) ||
                device.Reason.Length > 1024)
            {
                throw new InvalidDataException(
                    $"Recovery-unsupported device '{device.DeviceId}' must record " +
                    "Unsupported without an attempt and with a bounded canonical reason.");
            }
            unsupportedDeviceCount++;
        }
    }

    private static void RequireExactEvidenceSet(
        IReadOnlyCollection<string> actual,
        IReadOnlyList<string> expected,
        string evidenceName,
        string fieldName)
    {
        if (actual.Count != expected.Count ||
            actual.Any(static value =>
                string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal)) ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Count ||
            !expected.All(value =>
                actual.Contains(value, StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                $"{evidenceName} must contain exactly the required {fieldName}: " +
                string.Join(", ", expected) +
                ".");
        }
    }

    private static string ResolveContainedArtifactPath(
        string manifestDirectory,
        string? relativePath,
        string propertyName)
    {
        if (!IsManifestRelativeArtifactPath(relativePath))
        {
            throw new InvalidDataException(
                $"{propertyName} must be a canonical manifest-relative file path " +
                "without a rooted prefix or traversal.");
        }

        string normalizedRelativePath = relativePath!
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string normalizedDirectory = Path.GetFullPath(manifestDirectory);
        string fullPath = Path.GetFullPath(
            Path.Combine(normalizedDirectory, normalizedRelativePath));
        string directoryBoundary = Path.EndsInDirectorySeparator(normalizedDirectory)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directoryBoundary, PathComparison))
        {
            throw new InvalidDataException(
                $"{propertyName} resolves outside the qualification manifest directory.");
        }
        return fullPath;
    }

    private static bool IsManifestRelativeArtifactPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            Path.IsPathRooted(value) ||
            Path.IsPathFullyQualified(value) ||
            value[0] is '/' or '\\' ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        string[] segments = value.Split(
            ['/', '\\'],
            StringSplitOptions.None);
        return segments.All(static segment =>
            segment.Length > 0 &&
            segment is not "." and not "..");
    }

    private static FileStream OpenPinnedArtifact(
        string path,
        string? expectedSha256,
        int maximumBytes,
        string artifactName,
        long? expectedByteLength)
    {
        if (!TryDecodeSha256(expectedSha256, out _))
        {
            throw new InvalidDataException(
                $"Pinned {artifactName} SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            long admittedLength = stream.Length;
            if (admittedLength <= 0 || admittedLength > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The {artifactName} has an invalid bounded length.");
            }
            if (expectedByteLength is not null &&
                admittedLength != expectedByteLength)
            {
                throw new InvalidDataException(
                    $"The {artifactName} does not match its pinned byte length.");
            }

            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static byte[] ReadPinnedArtifactBytes(
        FileStream stream,
        string artifactName,
        string? expectedSha256)
    {
        long admittedLength = stream.Length;
        if (admittedLength <= 0 || admittedLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"The {artifactName} has an invalid pinned byte length.");
        }
        stream.Position = 0;
        var bytes = new byte[checked((int)admittedLength)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != admittedLength)
        {
            throw new IOException(
                $"The {artifactName} changed length while it was being authenticated.");
        }
        if (!TryDecodeSha256(expectedSha256, out byte[] expectedHash) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                expectedHash))
        {
            throw new InvalidDataException(
                $"The {artifactName} does not match its pinned SHA-256 identity.");
        }
        return bytes;
    }

    private static bool ContainsQualifiedDevice(
        IEnumerable<string>? qualifiedDeviceIds,
        string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;
        string normalizedDeviceName = deviceName.Trim();
        return (qualifiedDeviceIds ?? Array.Empty<string>())
            .Any(id =>
                !string.IsNullOrWhiteSpace(id) &&
                string.Equals(
                    id.Trim(),
                    normalizedDeviceName,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSha256(string? value)
    {
        return TryDecodeSha256(value, out _);
    }

    private static void RequireFixedSha256(
        string? actual,
        string? expected,
        string name)
    {
        if (!TryDecodeSha256(actual, out byte[] actualBytes) ||
            !TryDecodeSha256(expected, out byte[] expectedBytes) ||
            !CryptographicOperations.FixedTimeEquals(
                actualBytes,
                expectedBytes))
        {
            throw new InvalidDataException(
                $"{name} does not match its recomputed SHA-256 identity.");
        }
    }

    private static bool TryDecodeSha256(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is not { Length: 64 })
            return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private readonly record struct EvidenceRoleAuthentication(
        int TierDeviceCount,
        int LowerMemoryRayQueryDeviceCount,
        int RecoverySupportedDeviceCount,
        int RecoveryUnsupportedDeviceCount);

    private sealed record AuthenticatedReleaseEvidence(
        string[] DeviceIds,
        int RoleCount,
        int TierDeviceCount,
        int LowerMemoryRayQueryDeviceCount,
        int RecoverySupportedDeviceCount,
        int RecoveryUnsupportedDeviceCount)
    {
        public string RecoveryCapabilitySummary =>
            $"supported={RecoverySupportedDeviceCount}," +
            $"unsupported={RecoveryUnsupportedDeviceCount}";
    }

    private sealed record QualificationAuthenticationSeal(
        int SchemaVersion,
        MaterialGiV2Feature EnabledFeatures,
        string[] QualifiedDeviceIds,
        string ReleaseEvidenceBundleRelativePath,
        string ReleaseEvidenceBundleSha256,
        string EvidenceSha256,
        string ApprovalId,
        DateTimeOffset ApprovedAtUtc,
        string AlphaVisibilityReportRelativePath,
        string AlphaVisibilityReportSha256,
        string AlphaVisibilityEvidenceRelativePath,
        string AlphaVisibilityEvidenceSha256,
        string V1RemovalOwner,
        DateOnly V1RemovalTargetDate,
        int V1RetainedReleaseWindowCount,
        string ManifestPath,
        string BundlePath,
        string ReportPath,
        string EvidencePath,
        string AlphaVisibilityDeviceName,
        string[] ReleaseEvidenceDeviceIds,
        int ReleaseEvidenceRoleCount,
        int TierDeviceCount,
        int LowerMemoryRayQueryDeviceCount,
        string RecoveryCapabilitySummary)
    {
        public static QualificationAuthenticationSeal Capture(
            MaterialGiRolloutQualificationManifest manifest,
            string manifestPath,
            string bundlePath,
            string reportPath,
            string evidencePath,
            string alphaVisibilityDeviceName,
            AuthenticatedReleaseEvidence releaseEvidence) =>
            new(
                manifest.SchemaVersion,
                manifest.EnabledFeatures,
                [.. manifest.QualifiedDeviceIds],
                manifest.ReleaseEvidenceBundleRelativePath,
                manifest.ReleaseEvidenceBundleSha256,
                manifest.EvidenceSha256,
                manifest.ApprovalId,
                manifest.ApprovedAtUtc,
                manifest.AlphaVisibilityReportRelativePath,
                manifest.AlphaVisibilityReportSha256,
                manifest.AlphaVisibilityEvidenceRelativePath,
                manifest.AlphaVisibilityEvidenceSha256,
                manifest.V1RemovalOwner,
                manifest.V1RemovalTargetDate,
                manifest.V1RetainedReleaseWindowCount,
                manifestPath,
                bundlePath,
                reportPath,
                evidencePath,
                alphaVisibilityDeviceName,
                releaseEvidence.DeviceIds
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                releaseEvidence.RoleCount,
                releaseEvidence.TierDeviceCount,
                releaseEvidence.LowerMemoryRayQueryDeviceCount,
                releaseEvidence.RecoveryCapabilitySummary);

        public bool Matches(MaterialGiRolloutQualificationManifest manifest)
        {
            return SchemaVersion == manifest.SchemaVersion &&
                   EnabledFeatures == manifest.EnabledFeatures &&
                   manifest.QualifiedDeviceIds is not null &&
                   QualifiedDeviceIds.SequenceEqual(
                       manifest.QualifiedDeviceIds,
                       StringComparer.Ordinal) &&
                   string.Equals(
                       ReleaseEvidenceBundleRelativePath,
                       manifest.ReleaseEvidenceBundleRelativePath,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       ReleaseEvidenceBundleSha256,
                       manifest.ReleaseEvidenceBundleSha256,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       EvidenceSha256,
                       manifest.EvidenceSha256,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       ApprovalId,
                       manifest.ApprovalId,
                       StringComparison.Ordinal) &&
                   ApprovedAtUtc == manifest.ApprovedAtUtc &&
                   string.Equals(
                       AlphaVisibilityReportRelativePath,
                       manifest.AlphaVisibilityReportRelativePath,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       AlphaVisibilityReportSha256,
                       manifest.AlphaVisibilityReportSha256,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       AlphaVisibilityEvidenceRelativePath,
                       manifest.AlphaVisibilityEvidenceRelativePath,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       AlphaVisibilityEvidenceSha256,
                       manifest.AlphaVisibilityEvidenceSha256,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       V1RemovalOwner,
                       manifest.V1RemovalOwner,
                       StringComparison.Ordinal) &&
                   V1RemovalTargetDate == manifest.V1RemovalTargetDate &&
                   V1RetainedReleaseWindowCount ==
                       manifest.V1RetainedReleaseWindowCount;
        }
    }
}

public readonly record struct MaterialGiRolloutEvaluation(
    MaterialGiRolloutMode Mode,
    MaterialGiV2Feature ActiveFeatures,
    bool ReleaseQualificationRequired,
    bool ReleaseQualified,
    int QualificationFailureCount,
    string QualificationSummary,
    string ApprovalId,
    string EvidenceSha256,
    int QualifiedDeviceCount,
    string V1RemovalOwner,
    DateOnly V1RemovalTargetDate);

/// <summary>
/// Fail-closed rollout state. Qualified release mode can only be entered by
/// validating an evidence manifest; conformance mode is an explicit
/// non-shipping opt-in for samples and tests.
/// </summary>
public sealed class MaterialGiRolloutPolicy
{
    private MaterialGiRolloutQualificationManifest? _qualification;

    public MaterialGiRolloutMode Mode { get; private set; } =
        MaterialGiRolloutMode.LegacyUnqualified;

    public MaterialGiV2Feature AllowedFeatures { get; private set; } =
        MaterialGiV2Feature.None;

    public void UseLegacy()
    {
        Mode = MaterialGiRolloutMode.LegacyUnqualified;
        AllowedFeatures = MaterialGiV2Feature.None;
        _qualification = null;
    }

    public void EnableConformance(MaterialGiV2Feature features = MaterialGiV2Feature.All)
    {
        ValidateFeatureMask(features);
        Mode = MaterialGiRolloutMode.Conformance;
        AllowedFeatures = features;
        _qualification = null;
    }

    /// <summary>
    /// Enables an explicit non-shipping candidate used to produce the
    /// performance evidence that a first qualification manifest consumes.
    /// This mode never claims release qualification and cannot carry approval
    /// or evidence identity from a prior manifest.
    /// </summary>
    public void EnableQualificationCandidate()
    {
        Mode = MaterialGiRolloutMode.QualificationCandidate;
        AllowedFeatures = MaterialGiV2Feature.All;
        _qualification = null;
    }

    public void ApplyQualification(
        MaterialGiRolloutQualificationManifest manifest,
        DateOnly? evaluationDate = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        DateOnly date = evaluationDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        IReadOnlyList<string> failures = manifest.Validate(date);
        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                "Material-GI V2 qualification was rejected: " +
                string.Join(" ", failures));
        }

        Mode = MaterialGiRolloutMode.QualifiedRelease;
        AllowedFeatures = manifest.EnabledFeatures;
        _qualification = manifest;
    }

    public MaterialGiRolloutEvaluation Evaluate(
        MaterialGiV2Feature activeFeatures,
        DateOnly? evaluationDate = null)
    {
        ValidateFeatureMask(activeFeatures, allowNone: true);
        DateOnly date = evaluationDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (Mode == MaterialGiRolloutMode.QualificationCandidate)
        {
            bool exactFeatures =
                AllowedFeatures == MaterialGiV2Feature.All &&
                activeFeatures == MaterialGiV2Feature.All;
            bool v1ContractExpired =
                date > MaterialGiV1CompatibilityContract.RemovalTargetDate;
            int candidateFailureCount =
                (exactFeatures ? 0 : 1) +
                (v1ContractExpired ? 1 : 0);
            string candidateSummary = candidateFailureCount == 0
                ? "Explicit non-shipping qualification candidate; human approval and an authenticated release manifest remain required."
                : !exactFeatures && v1ContractExpired
                    ? "Qualification-candidate evidence does not exercise all V2 features and the V1 compatibility contract has expired."
                    : !exactFeatures
                        ? "Qualification-candidate evidence must exercise all material-GI V2 features."
                        : "The V1 compatibility contract expired before qualification-candidate evidence was evaluated.";
            return new MaterialGiRolloutEvaluation(
                Mode,
                activeFeatures,
                ReleaseQualificationRequired: true,
                ReleaseQualified: false,
                QualificationFailureCount: candidateFailureCount,
                QualificationSummary: candidateSummary,
                ApprovalId: string.Empty,
                EvidenceSha256: string.Empty,
                QualifiedDeviceCount: 0,
                V1RemovalOwner: MaterialGiV1CompatibilityContract.Owner,
                V1RemovalTargetDate: MaterialGiV1CompatibilityContract.RemovalTargetDate);
        }

        if (activeFeatures == MaterialGiV2Feature.None)
        {
            return new MaterialGiRolloutEvaluation(
                Mode,
                activeFeatures,
                ReleaseQualificationRequired: false,
                ReleaseQualified: false,
                QualificationFailureCount: 0,
                QualificationSummary: "Material-GI V2 is inactive.",
                ApprovalId: string.Empty,
                EvidenceSha256: string.Empty,
                QualifiedDeviceCount: 0,
                V1RemovalOwner: MaterialGiV1CompatibilityContract.Owner,
                V1RemovalTargetDate: MaterialGiV1CompatibilityContract.RemovalTargetDate);
        }

        if (Mode == MaterialGiRolloutMode.Conformance)
        {
            int disallowed = (activeFeatures & ~AllowedFeatures) == 0 ? 0 : 1;
            return new MaterialGiRolloutEvaluation(
                Mode,
                activeFeatures,
                ReleaseQualificationRequired: false,
                ReleaseQualified: false,
                QualificationFailureCount: disallowed,
                QualificationSummary: disallowed == 0
                    ? "Explicit non-shipping conformance rollout."
                    : "One or more active features were not enabled by the conformance policy.",
                ApprovalId: string.Empty,
                EvidenceSha256: string.Empty,
                QualifiedDeviceCount: 0,
                V1RemovalOwner: MaterialGiV1CompatibilityContract.Owner,
                V1RemovalTargetDate: MaterialGiV1CompatibilityContract.RemovalTargetDate);
        }

        IReadOnlyList<string> manifestFailures = _qualification?.Validate(date) ??
            new[] { "No qualified release manifest is active." };
        bool featureMaskAccepted =
            Mode == MaterialGiRolloutMode.QualifiedRelease &&
            (activeFeatures & ~AllowedFeatures) == 0;
        int failureCount = manifestFailures.Count + (featureMaskAccepted ? 0 : 1);
        bool qualified = failureCount == 0;
        string summary = qualified
            ? $"Qualified by approval '{_qualification!.ApprovalId}'."
            : string.Join(
                " ",
                manifestFailures.Concat(
                    featureMaskAccepted
                        ? Array.Empty<string>()
                        : new[] { "One or more active features are outside the qualified feature mask." }));
        return new MaterialGiRolloutEvaluation(
            Mode,
            activeFeatures,
            ReleaseQualificationRequired: true,
            ReleaseQualified: qualified,
            QualificationFailureCount: failureCount,
            QualificationSummary: summary,
            ApprovalId: qualified ? _qualification!.ApprovalId : string.Empty,
            EvidenceSha256: qualified ? _qualification!.EvidenceSha256 : string.Empty,
            QualifiedDeviceCount: qualified
                ? _qualification!.QualifiedDeviceIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
                : 0,
            V1RemovalOwner:
                _qualification?.V1RemovalOwner ?? MaterialGiV1CompatibilityContract.Owner,
            V1RemovalTargetDate:
                _qualification?.V1RemovalTargetDate ??
                    MaterialGiV1CompatibilityContract.RemovalTargetDate);
    }

    /// <summary>
    /// Resolves the feature mask that runtime consumers may execute. Authored
    /// switches are configuration intent only; they cannot bypass the
    /// non-persisted rollout policy.
    /// </summary>
    public MaterialGiV2Feature ResolveEffectiveFeatures(
        MaterialGiV2Feature configuredFeatures,
        DateOnly? evaluationDate = null)
    {
        ValidateFeatureMask(configuredFeatures, allowNone: true);
        if (configuredFeatures == MaterialGiV2Feature.None)
            return MaterialGiV2Feature.None;

        DateOnly date = evaluationDate ??
            DateOnly.FromDateTime(DateTime.UtcNow);
        bool executionAuthorized = Mode switch
        {
            MaterialGiRolloutMode.Conformance => true,
            MaterialGiRolloutMode.QualificationCandidate =>
                date <= MaterialGiV1CompatibilityContract.RemovalTargetDate,
            MaterialGiRolloutMode.QualifiedRelease =>
                _qualification is not null &&
                date >= DateOnly.FromDateTime(
                    _qualification.ApprovedAtUtc.UtcDateTime) &&
                date <= _qualification.V1RemovalTargetDate,
            _ => false
        };
        return executionAuthorized
            ? configuredFeatures & AllowedFeatures
            : MaterialGiV2Feature.None;
    }

    private static void ValidateFeatureMask(
        MaterialGiV2Feature features,
        bool allowNone = false)
    {
        if ((!allowNone && features == MaterialGiV2Feature.None) ||
            (features & ~MaterialGiV2Feature.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(features),
                features,
                "A non-empty mask containing only known material-GI V2 features is required.");
        }
    }
}
