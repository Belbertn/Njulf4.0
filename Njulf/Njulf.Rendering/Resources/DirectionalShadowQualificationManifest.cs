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

public enum DirectionalShadowQualificationEvidenceRole : byte
{
    NumericCorrectness = 0,
    VisualComparison = 1,
    Performance = 2,
    Memory = 3,
    Validation = 4,
    Lifecycle = 5,
    Fallback = 6
}

/// <summary>
/// Frozen promotion floors for directional shadows. The renderer may run an
/// explicitly selected ray mode without this evidence, but labels it
/// Experimental. Only a fully pinned, exact runtime match can claim Production
/// or activate the optional CSM temporal path in Auto mode.
/// </summary>
public static class DirectionalShadowQualificationContract
{
    public const uint ManifestSchemaRevision = 1u;
    public const string AlgorithmRevision =
        "directional-shadow-csm-ray-soft/v1";
    public const int MaximumManifestBytes = 256 * 1024;
    public const long MaximumArtifactBytes = 1024L * 1024L * 1024L;
    public const int MaximumEntries = 16;
    public const int MaximumDeviceRules = 16;
    public const int MaximumProfiles = 32;
    public const int MaximumArtifacts = 128;
    public const uint MinimumIndependentRuns = 3u;
    public const uint MinimumReferenceFrames = 120u;

    internal static readonly DirectionalShadowQualificationEvidenceRole[]
        RequiredEvidenceRoles =
        [
            DirectionalShadowQualificationEvidenceRole.NumericCorrectness,
            DirectionalShadowQualificationEvidenceRole.VisualComparison,
            DirectionalShadowQualificationEvidenceRole.Performance,
            DirectionalShadowQualificationEvidenceRole.Memory,
            DirectionalShadowQualificationEvidenceRole.Validation,
            DirectionalShadowQualificationEvidenceRole.Lifecycle,
            DirectionalShadowQualificationEvidenceRole.Fallback
        ];

    internal static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string normalized = value.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[7..];
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
            return string.Empty;
        return normalized.ToLowerInvariant();
    }

    internal static bool IsCommit(string? value) =>
        value is { Length: >= 40 and <= 64 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) &&
        value.All(Uri.IsHexDigit);

    internal static bool IsToken(string? value, int maximumLength = 256) =>
        value is { Length: > 0 } && value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    internal static bool FixedSha256Equals(string? left, string? right)
    {
        string normalizedLeft = NormalizeSha256(left);
        string normalizedRight = NormalizeSha256(right);
        return normalizedLeft.Length == 64 && normalizedRight.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(normalizedLeft),
                Convert.FromHexString(normalizedRight));
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

public sealed record DirectionalShadowQualificationArtifactPin
{
    public DirectionalShadowQualificationEvidenceRole Role { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record DirectionalShadowQualificationDeviceRule
{
    public string RuleId { get; init; } = string.Empty;
    public uint VendorId { get; init; }
    public uint MinimumDeviceId { get; init; }
    public uint MaximumDeviceId { get; init; } = uint.MaxValue;
    public uint MinimumDriverVersion { get; init; }
    public uint MaximumDriverVersion { get; init; } = uint.MaxValue;
    public uint MinimumApiVersion { get; init; }
    public uint MaximumApiVersion { get; init; } = uint.MaxValue;

    internal bool Matches(in DirectionalShadowQualificationRuntimeContext context) =>
        context.VendorId == VendorId &&
        context.DeviceId >= MinimumDeviceId &&
        context.DeviceId <= MaximumDeviceId &&
        context.DriverVersion >= MinimumDriverVersion &&
        context.DriverVersion <= MaximumDriverVersion &&
        context.ApiVersion >= MinimumApiVersion &&
        context.ApiVersion <= MaximumApiVersion;
}

/// <summary>
/// Exact release-build capture profile. Measurements and their frozen limits
/// live together so a manifest cannot pin an artifact while omitting the
/// numeric ship gate it was supposed to prove.
/// </summary>
public sealed record DirectionalShadowQualificationProfile
{
    public string TrackId { get; init; } = string.Empty;
    public uint Width { get; init; }
    public uint Height { get; init; }
    public AntiAliasingMode AntiAliasingMode { get; init; }
    public RenderQualityPreset QualityPreset { get; init; }
    public uint IndependentRuns { get; init; }
    public uint ReferenceFrames { get; init; }
    public double MedianTotalGpuMicroseconds { get; init; }
    public double P95TotalGpuMicroseconds { get; init; }
    public double P95DirectionalShadowGpuMicroseconds { get; init; }
    public ulong DirectionalShadowMemoryBytes { get; init; }
    public double TotalGpuBudgetMicroseconds { get; init; }
    public double P95TotalGpuBudgetMicroseconds { get; init; }
    public double DirectionalShadowGpuBudgetMicroseconds { get; init; }
    public ulong DirectionalShadowMemoryBudgetBytes { get; init; }
    public double MaximumImageDifference { get; init; }
    public double MeasuredImageDifference { get; init; }
    public int VulkanValidationErrorCount { get; init; }
    public bool VisualReviewApproved { get; init; }

    internal bool Matches(in DirectionalShadowQualificationRuntimeContext context) =>
        Width == context.Width && Height == context.Height &&
        AntiAliasingMode == context.AntiAliasingMode &&
        QualityPreset == context.QualityPreset;
}

public sealed record DirectionalShadowQualificationEntryDocument
{
    public DirectionalShadowMode Mode { get; init; }
    public bool CsmTemporalApproved { get; init; }
    public string AlgorithmRevision { get; init; } =
        DirectionalShadowQualificationContract.AlgorithmRevision;
    public string ShaderBundleSha256 { get; init; } = string.Empty;
    public string SettingsFingerprintSha256 { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public RaySceneGeometryCategory AllowedProxyCategories { get; init; }
    public DirectionalShadowQualificationDeviceRule[] DeviceRules { get; init; } = [];
    public DirectionalShadowQualificationProfile[] Profiles { get; init; } = [];
    public DirectionalShadowQualificationArtifactPin[] Artifacts { get; init; } = [];
    public string ApprovalId { get; init; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; init; }
    public string QualificationId { get; init; } = string.Empty;
}

public sealed record DirectionalShadowQualificationManifestDocument
{
    public uint SchemaRevision { get; init; } =
        DirectionalShadowQualificationContract.ManifestSchemaRevision;
    public DirectionalShadowQualificationEntryDocument[] Entries { get; init; } = [];
}

public readonly record struct DirectionalShadowQualificationRuntimeContext(
    DirectionalShadowMode Mode,
    bool CsmTemporalRequested,
    uint Width,
    uint Height,
    AntiAliasingMode AntiAliasingMode,
    RenderQualityPreset QualityPreset,
    uint VendorId,
    uint DeviceId,
    uint DriverVersion,
    uint ApiVersion,
    string ShaderBundleSha256,
    string SettingsFingerprintSha256,
    string BuildCommit,
    string DirtyWorktreeState,
    RaySceneGeometryCategory ExactCategories,
    RaySceneGeometryCategory ProxyCategories)
{
    public bool IsWellFormed =>
        Mode is (DirectionalShadowMode.Cascaded or
            DirectionalShadowMode.HybridContact or
            DirectionalShadowMode.RayQueryHard or
            DirectionalShadowMode.RayQuerySoft) &&
        Width != 0u && Height != 0u &&
        Enum.IsDefined(AntiAliasingMode) &&
        Enum.IsDefined(QualityPreset) &&
        VendorId != 0u && DeviceId != 0u &&
        DriverVersion != 0u && ApiVersion != 0u &&
        DirectionalShadowQualificationContract.NormalizeSha256(
            ShaderBundleSha256).Length == 64 &&
        DirectionalShadowQualificationContract.NormalizeSha256(
            SettingsFingerprintSha256).Length == 64 &&
        DirectionalShadowQualificationContract.IsCommit(BuildCommit) &&
        DirtyWorktreeState is "clean" or "dirty";
}

public readonly record struct DirectionalShadowQualificationGateResult(
    bool Passed,
    DirectionalShadowQualificationLevel Level,
    string QualificationId,
    string FailureDetail,
    string MatchedDeviceRuleId,
    string MatchedTrackId,
    bool CsmTemporalApproved,
    double DirectionalShadowGpuBudgetMicroseconds,
    ulong DirectionalShadowMemoryBudgetBytes)
{
    public static DirectionalShadowQualificationGateResult Reject(string detail) =>
        new(
            false,
            DirectionalShadowQualificationLevel.Experimental,
            string.Empty,
            string.IsNullOrWhiteSpace(detail)
                ? "directional-shadow-qualification-rejected"
                : detail,
            string.Empty,
            string.Empty,
            false,
            0.0,
            0UL);
}

/// <summary>
/// Immutable set produced only by the validating codec. There is no public API
/// that can insert a trusted entry directly.
/// </summary>
public sealed class DirectionalShadowQualificationManifest
{
    private readonly IReadOnlyList<AuthenticatedEntry> _entries;

    internal DirectionalShadowQualificationManifest(
        IReadOnlyList<AuthenticatedEntry> entries)
    {
        _entries = entries;
    }

    public static DirectionalShadowQualificationManifest Empty { get; } =
        new(Array.Empty<AuthenticatedEntry>());

    public int Count => _entries.Count;

    public DirectionalShadowQualificationGateResult Evaluate(
        in DirectionalShadowQualificationRuntimeContext context)
    {
        if (!context.IsWellFormed)
            return DirectionalShadowQualificationGateResult.Reject(
                "directional-shadow-runtime-qualification-context-invalid");
        if (!string.Equals(
                context.DirtyWorktreeState,
                "clean",
                StringComparison.Ordinal))
        {
            return DirectionalShadowQualificationGateResult.Reject(
                "directional-shadow-production-requires-clean-worktree");
        }
        if (_entries.Count == 0)
            return DirectionalShadowQualificationGateResult.Reject(
                "directional-shadow-qualification-manifest-missing");

        string mismatch = "directional-shadow-mode-qualification-missing";
        foreach (AuthenticatedEntry entry in _entries)
        {
            if (entry.Mode != context.Mode ||
                entry.CsmTemporalApproved != context.CsmTemporalRequested)
            {
                continue;
            }
            if (!string.Equals(
                    entry.AlgorithmRevision,
                    DirectionalShadowQualificationContract.AlgorithmRevision,
                    StringComparison.Ordinal))
            {
                mismatch = "directional-shadow-algorithm-revision-mismatch";
                continue;
            }
            if (!DirectionalShadowQualificationContract.FixedSha256Equals(
                    context.ShaderBundleSha256,
                    entry.ShaderBundleSha256))
            {
                mismatch = "directional-shadow-shader-bundle-mismatch";
                continue;
            }
            if (!DirectionalShadowQualificationContract.FixedSha256Equals(
                    context.SettingsFingerprintSha256,
                    entry.SettingsFingerprintSha256))
            {
                mismatch = "directional-shadow-settings-fingerprint-mismatch";
                continue;
            }
            if (!string.Equals(
                    context.BuildCommit,
                    entry.BuildCommit,
                    StringComparison.Ordinal))
            {
                mismatch = "directional-shadow-build-commit-mismatch";
                continue;
            }

            RaySceneGeometryCategory required = context.Mode ==
                    DirectionalShadowMode.Cascaded
                ? RaySceneGeometryCategory.None
                : RaySceneGeometryCategory.DirectionalShadowDefault;
            RaySceneGeometryCategory unapprovedProxy =
                context.ProxyCategories & ~entry.AllowedProxyCategories;
            RaySceneGeometryCategory qualified = context.ExactCategories |
                (context.ProxyCategories & entry.AllowedProxyCategories);
            if (unapprovedProxy != RaySceneGeometryCategory.None ||
                (qualified & required) != required)
            {
                mismatch = "directional-shadow-geometry-qualification-mismatch";
                continue;
            }

            DirectionalShadowQualificationRuntimeContext runtimeContext = context;
            DirectionalShadowQualificationProfile? profile =
                entry.Profiles.FirstOrDefault(profile =>
                    profile.Matches(runtimeContext));
            if (profile is null)
            {
                mismatch = "directional-shadow-resolution-aa-profile-mismatch";
                continue;
            }
            DirectionalShadowQualificationDeviceRule? deviceRule =
                entry.DeviceRules.FirstOrDefault(rule =>
                    rule.Matches(runtimeContext));
            if (deviceRule is null)
            {
                mismatch = "directional-shadow-device-driver-class-mismatch";
                continue;
            }

            return new DirectionalShadowQualificationGateResult(
                true,
                DirectionalShadowQualificationLevel.Production,
                "sha256:" + entry.QualificationId,
                "directional-shadow-production-evidence-matched",
                deviceRule.RuleId,
                profile.TrackId,
                entry.CsmTemporalApproved,
                profile.DirectionalShadowGpuBudgetMicroseconds,
                profile.DirectionalShadowMemoryBudgetBytes);
        }

        return DirectionalShadowQualificationGateResult.Reject(mismatch);
    }

    internal sealed class AuthenticatedEntry
    {
        public AuthenticatedEntry(
            DirectionalShadowQualificationEntryDocument document,
            string qualificationId)
        {
            Mode = document.Mode;
            CsmTemporalApproved = document.CsmTemporalApproved;
            AlgorithmRevision = document.AlgorithmRevision;
            ShaderBundleSha256 = DirectionalShadowQualificationContract
                .NormalizeSha256(document.ShaderBundleSha256);
            SettingsFingerprintSha256 = DirectionalShadowQualificationContract
                .NormalizeSha256(document.SettingsFingerprintSha256);
            BuildCommit = document.BuildCommit;
            AllowedProxyCategories = document.AllowedProxyCategories;
            DeviceRules = Array.AsReadOnly((DirectionalShadowQualificationDeviceRule[])
                document.DeviceRules.Clone());
            Profiles = Array.AsReadOnly((DirectionalShadowQualificationProfile[])
                document.Profiles.Clone());
            QualificationId = qualificationId;
        }

        public DirectionalShadowMode Mode { get; }
        public bool CsmTemporalApproved { get; }
        public string AlgorithmRevision { get; }
        public string ShaderBundleSha256 { get; }
        public string SettingsFingerprintSha256 { get; }
        public string BuildCommit { get; }
        public RaySceneGeometryCategory AllowedProxyCategories { get; }
        public IReadOnlyList<DirectionalShadowQualificationDeviceRule> DeviceRules { get; }
        public IReadOnlyList<DirectionalShadowQualificationProfile> Profiles { get; }
        public string QualificationId { get; }
    }
}

public static class DirectionalShadowSettingsFingerprint
{
    public static string Compute(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ShadowSettings shadows = settings.Shadows;
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        Append(hash, "directional-shadow-settings/v1");
        Append(hash, shadows.DirectionalShadowsEnabled);
        Append(hash, (uint)shadows.RequestedDirectionalShadowMode);
        Append(hash, (uint)shadows.DirectionalCsmTemporalMode);
        Append(hash, shadows.DirectionalShadowMapSize);
        Append(hash, shadows.DirectionalCascadeCount);
        Append(hash, shadows.MaxShadowDistance);
        Append(hash, shadows.DirectionalCascadeBlendFraction);
        Append(hash, shadows.DirectionalCascadeSplitLambda);
        Append(hash, shadows.DirectionalCasterExtrusionDistance);
        Append(hash, shadows.DirectionalContactShadowDistance);
        Append(hash, (uint)shadows.DirectionalFilterMode);
        Append(hash, (uint)shadows.DirectionalBiasMode);
        Append(hash, (uint)shadows.DirectionalPcfRadiusMode);
        Append(hash, shadows.NormalBias);
        Append(hash, shadows.SlopeScaledDepthBias);
        Append(hash, shadows.ConstantDepthBias);
        Append(hash, shadows.PcfRadius);
        Append(hash, shadows.DirectionalSoftRecoveryRayCount);
        Append(hash, shadows.DirectionalSoftHistoryLength);
        Append(hash, shadows.DirectionalSoftSpatialPassCount);
        Append(hash, shadows.DirectionalTransparentSoftRayCount);
        Append(hash, shadows.DirectionalSoftAngularDiameterScale);
        Append(hash, settings.Environment.SunAngularDiameterDegrees);
        Append(hash, (uint)settings.AntiAliasing.EffectiveMode);
        Append(hash, (uint)settings.Transparency.Mode);
        Append(hash, settings.Transparency.ReceiveShadows);
        Append(hash, settings.Decals.ReceiveShadows);
        return "sha256:" +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        DirectionalShadowQualificationContract.Append(hash, value);

    private static void Append(IncrementalHash hash, bool value) =>
        Append(hash, value ? "1" : "0");

    private static void Append(IncrementalHash hash, uint value) =>
        Append(hash, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, int value) =>
        Append(hash, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, float value) =>
        Append(hash, BitConverter.SingleToInt32Bits(value)
            .ToString("x8", CultureInfo.InvariantCulture));
}

public static class DirectionalShadowQualificationManifestCodec
{
    private const int MaximumJsonDepth = 40;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static string SerializeDocument(
        DirectionalShadowQualificationManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static string ComputeQualificationId(
        DirectionalShadowQualificationEntryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        void Add(string value) =>
            DirectionalShadowQualificationContract.Append(hash, value);

        Add("directional-shadow-qualification/v1");
        Add(((uint)document.Mode).ToString(CultureInfo.InvariantCulture));
        Add(document.CsmTemporalApproved ? "1" : "0");
        Add(document.AlgorithmRevision ?? string.Empty);
        Add(DirectionalShadowQualificationContract.NormalizeSha256(
            document.ShaderBundleSha256));
        Add(DirectionalShadowQualificationContract.NormalizeSha256(
            document.SettingsFingerprintSha256));
        Add(document.BuildCommit ?? string.Empty);
        Add(((uint)document.AllowedProxyCategories)
            .ToString(CultureInfo.InvariantCulture));
        Add(document.ApprovalId ?? string.Empty);
        Add(document.ApprovedAtUtc.ToUniversalTime().ToString(
            "O", CultureInfo.InvariantCulture));

        foreach (DirectionalShadowQualificationDeviceRule rule in
                 (document.DeviceRules ?? [])
                 .OrderBy(rule => rule.RuleId, StringComparer.Ordinal))
        {
            Add(rule.RuleId);
            Add(rule.VendorId.ToString(CultureInfo.InvariantCulture));
            Add(rule.MinimumDeviceId.ToString(CultureInfo.InvariantCulture));
            Add(rule.MaximumDeviceId.ToString(CultureInfo.InvariantCulture));
            Add(rule.MinimumDriverVersion.ToString(CultureInfo.InvariantCulture));
            Add(rule.MaximumDriverVersion.ToString(CultureInfo.InvariantCulture));
            Add(rule.MinimumApiVersion.ToString(CultureInfo.InvariantCulture));
            Add(rule.MaximumApiVersion.ToString(CultureInfo.InvariantCulture));
        }
        foreach (DirectionalShadowQualificationProfile profile in
                 (document.Profiles ?? [])
                 .OrderBy(profile => profile.TrackId, StringComparer.Ordinal))
        {
            Add(profile.TrackId);
            Add(profile.Width.ToString(CultureInfo.InvariantCulture));
            Add(profile.Height.ToString(CultureInfo.InvariantCulture));
            Add(((uint)profile.AntiAliasingMode).ToString(CultureInfo.InvariantCulture));
            Add(((uint)profile.QualityPreset).ToString(CultureInfo.InvariantCulture));
            Add(profile.IndependentRuns.ToString(CultureInfo.InvariantCulture));
            Add(profile.ReferenceFrames.ToString(CultureInfo.InvariantCulture));
            Add(profile.MedianTotalGpuMicroseconds.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.P95TotalGpuMicroseconds.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.P95DirectionalShadowGpuMicroseconds.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.DirectionalShadowMemoryBytes.ToString(CultureInfo.InvariantCulture));
            Add(profile.TotalGpuBudgetMicroseconds.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.P95TotalGpuBudgetMicroseconds.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.DirectionalShadowGpuBudgetMicroseconds.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.DirectionalShadowMemoryBudgetBytes.ToString(CultureInfo.InvariantCulture));
            Add(profile.MaximumImageDifference.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.MeasuredImageDifference.ToString("R", CultureInfo.InvariantCulture));
            Add(profile.VulkanValidationErrorCount.ToString(CultureInfo.InvariantCulture));
            Add(profile.VisualReviewApproved ? "1" : "0");
        }
        foreach (DirectionalShadowQualificationArtifactPin artifact in
                 (document.Artifacts ?? [])
                 .OrderBy(artifact => artifact.Role)
                 .ThenBy(artifact => artifact.RelativePath, StringComparer.Ordinal))
        {
            Add(((byte)artifact.Role).ToString(CultureInfo.InvariantCulture));
            Add(artifact.RelativePath);
            Add(artifact.ByteLength.ToString(CultureInfo.InvariantCulture));
            Add(DirectionalShadowQualificationContract.NormalizeSha256(
                artifact.Sha256));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool TryLoad(
        string path,
        out DirectionalShadowQualificationManifest manifest,
        out string failureDetail,
        DateTimeOffset? evaluationTimeUtc = null)
    {
        manifest = DirectionalShadowQualificationManifest.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail = "directional-shadow-qualification-manifest-path-empty";
            return false;
        }

        try
        {
            string manifestPath = Path.GetFullPath(path);
            byte[] bytes = BoundedFileReader.ReadStable(
                manifestPath,
                DirectionalShadowQualificationContract.MaximumManifestBytes,
                "Directional shadow qualification manifest");
            StrictJsonContract.RejectDuplicateProperties(
                bytes,
                MaximumJsonDepth,
                "Directional shadow qualification manifest");
            DirectionalShadowQualificationManifestDocument document =
                JsonSerializer.Deserialize<
                    DirectionalShadowQualificationManifestDocument>(
                    bytes,
                    JsonOptions) ?? throw new InvalidDataException(
                    "Directional shadow qualification manifest is null.");
            manifest = Authenticate(
                manifestPath,
                document,
                evaluationTimeUtc ?? DateTimeOffset.UtcNow);
            failureDetail = "valid";
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException or
            CryptographicException or
            ArgumentException or
            NotSupportedException or
            OverflowException)
        {
            failureDetail = "directional-shadow-qualification-rejected:" +
                exception.Message;
            manifest = DirectionalShadowQualificationManifest.Empty;
            return false;
        }
    }

    private static DirectionalShadowQualificationManifest Authenticate(
        string manifestPath,
        DirectionalShadowQualificationManifestDocument document,
        DateTimeOffset evaluationTimeUtc)
    {
        if (document.SchemaRevision !=
            DirectionalShadowQualificationContract.ManifestSchemaRevision)
        {
            throw new InvalidDataException(
                "Directional shadow qualification schema revision is unsupported.");
        }
        DirectionalShadowQualificationEntryDocument[] entries =
            document.Entries ?? [];
        if (entries.Length is < 1 or >
            DirectionalShadowQualificationContract.MaximumEntries)
        {
            throw new InvalidDataException(
                "Directional shadow qualification entry count is out of range.");
        }

        string directory = Path.GetDirectoryName(manifestPath) ??
            throw new InvalidDataException(
                "Directional shadow qualification manifest has no directory.");
        var authenticated = new List<
            DirectionalShadowQualificationManifest.AuthenticatedEntry>(
            entries.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (DirectionalShadowQualificationEntryDocument entry in entries)
        {
            ValidateEntry(directory, entry, evaluationTimeUtc);
            string qualificationId = ComputeQualificationId(entry);
            if (!DirectionalShadowQualificationContract.FixedSha256Equals(
                    entry.QualificationId,
                    qualificationId))
            {
                throw new InvalidDataException(
                    "Directional shadow qualification ID does not match its pinned content.");
            }
            if (!identities.Add(qualificationId))
                throw new InvalidDataException(
                    "Directional shadow qualification contains a duplicate entry.");
            authenticated.Add(new(
                entry,
                qualificationId));
        }
        return new DirectionalShadowQualificationManifest(
            new ReadOnlyCollection<
                DirectionalShadowQualificationManifest.AuthenticatedEntry>(
                authenticated));
    }

    private static void ValidateEntry(
        string manifestDirectory,
        DirectionalShadowQualificationEntryDocument entry,
        DateTimeOffset evaluationTimeUtc)
    {
        if (entry.Mode is not (DirectionalShadowMode.Cascaded or
                DirectionalShadowMode.HybridContact or
                DirectionalShadowMode.RayQueryHard or
                DirectionalShadowMode.RayQuerySoft))
            throw new InvalidDataException("Directional shadow mode is invalid.");
        if (entry.CsmTemporalApproved &&
            entry.Mode != DirectionalShadowMode.Cascaded)
            throw new InvalidDataException(
                "CSM temporal evidence must use Cascaded mode.");
        if (!entry.CsmTemporalApproved &&
            entry.Mode == DirectionalShadowMode.Cascaded)
            throw new InvalidDataException(
                "Baseline CSM requires no promotion manifest.");
        if (!string.Equals(
                entry.AlgorithmRevision,
                DirectionalShadowQualificationContract.AlgorithmRevision,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Directional shadow algorithm revision is stale.");
        if (DirectionalShadowQualificationContract.NormalizeSha256(
                entry.ShaderBundleSha256).Length != 64 ||
            DirectionalShadowQualificationContract.NormalizeSha256(
                entry.SettingsFingerprintSha256).Length != 64)
            throw new InvalidDataException(
                "Directional shadow qualification hashes are invalid.");
        if (!DirectionalShadowQualificationContract.IsCommit(entry.BuildCommit))
            throw new InvalidDataException(
                "Directional shadow qualification build commit is invalid.");
        if (!DirectionalShadowQualificationContract.IsToken(entry.ApprovalId))
            throw new InvalidDataException(
                "Directional shadow qualification approval ID is invalid.");
        if (entry.ApprovedAtUtc == default ||
            entry.ApprovedAtUtc > evaluationTimeUtc.AddMinutes(5))
            throw new InvalidDataException(
                "Directional shadow approval timestamp is invalid.");
        if ((entry.AllowedProxyCategories &
                ~RaySceneGeometryCategory.DirectionalShadowDefault) != 0)
            throw new InvalidDataException(
                "Directional shadow qualification admits an unknown proxy category.");

        DirectionalShadowQualificationDeviceRule[] rules =
            entry.DeviceRules ?? [];
        if (rules.Length is < 1 or >
            DirectionalShadowQualificationContract.MaximumDeviceRules)
            throw new InvalidDataException(
                "Directional shadow device-rule count is out of range.");
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (DirectionalShadowQualificationDeviceRule rule in rules)
        {
            if (!DirectionalShadowQualificationContract.IsToken(rule.RuleId) ||
                !ruleIds.Add(rule.RuleId) || rule.VendorId == 0u ||
                rule.MinimumDeviceId > rule.MaximumDeviceId ||
                rule.MinimumDriverVersion == 0u ||
                rule.MinimumDriverVersion > rule.MaximumDriverVersion ||
                rule.MinimumApiVersion == 0u ||
                rule.MinimumApiVersion > rule.MaximumApiVersion)
            {
                throw new InvalidDataException(
                    "Directional shadow device rule is invalid or duplicated.");
            }
        }

        DirectionalShadowQualificationProfile[] profiles = entry.Profiles ?? [];
        if (profiles.Length is < 1 or >
            DirectionalShadowQualificationContract.MaximumProfiles)
            throw new InvalidDataException(
                "Directional shadow capture-profile count is out of range.");
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (DirectionalShadowQualificationProfile profile in profiles)
        {
            ValidateProfile(profile);
            if (!profileIds.Add(profile.TrackId))
                throw new InvalidDataException(
                    "Directional shadow capture profile is duplicated.");
        }

        DirectionalShadowQualificationArtifactPin[] artifacts =
            entry.Artifacts ?? [];
        if (artifacts.Length is < 1 or >
            DirectionalShadowQualificationContract.MaximumArtifacts)
            throw new InvalidDataException(
                "Directional shadow evidence-artifact count is out of range.");
        foreach (DirectionalShadowQualificationEvidenceRole role in
                 DirectionalShadowQualificationContract.RequiredEvidenceRoles)
        {
            if (!artifacts.Any(artifact => artifact.Role == role))
                throw new InvalidDataException(
                    $"Directional shadow evidence role {role} is missing.");
        }
        var artifactPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (DirectionalShadowQualificationArtifactPin artifact in artifacts)
        {
            if (!Enum.IsDefined(artifact.Role) ||
                !artifactPaths.Add(artifact.RelativePath))
                throw new InvalidDataException(
                    "Directional shadow artifact is invalid or duplicated.");
            VerifyArtifact(manifestDirectory, artifact);
        }
    }

    private static void ValidateProfile(
        DirectionalShadowQualificationProfile profile)
    {
        static bool FiniteNonNegative(double value) =>
            double.IsFinite(value) && value >= 0.0;

        if (!DirectionalShadowQualificationContract.IsToken(profile.TrackId) ||
            profile.Width is 0u or > 16384u ||
            profile.Height is 0u or > 16384u ||
            !Enum.IsDefined(profile.AntiAliasingMode) ||
            !Enum.IsDefined(profile.QualityPreset) ||
            profile.IndependentRuns <
                DirectionalShadowQualificationContract.MinimumIndependentRuns ||
            profile.ReferenceFrames <
                DirectionalShadowQualificationContract.MinimumReferenceFrames ||
            !FiniteNonNegative(profile.MedianTotalGpuMicroseconds) ||
            !FiniteNonNegative(profile.P95TotalGpuMicroseconds) ||
            !FiniteNonNegative(profile.P95DirectionalShadowGpuMicroseconds) ||
            !FiniteNonNegative(profile.TotalGpuBudgetMicroseconds) ||
            !FiniteNonNegative(profile.P95TotalGpuBudgetMicroseconds) ||
            !FiniteNonNegative(profile.DirectionalShadowGpuBudgetMicroseconds) ||
            !FiniteNonNegative(profile.MaximumImageDifference) ||
            !FiniteNonNegative(profile.MeasuredImageDifference) ||
            profile.P95TotalGpuMicroseconds < profile.MedianTotalGpuMicroseconds ||
            profile.MedianTotalGpuMicroseconds > profile.TotalGpuBudgetMicroseconds ||
            profile.P95TotalGpuMicroseconds > profile.P95TotalGpuBudgetMicroseconds ||
            profile.P95DirectionalShadowGpuMicroseconds >
                profile.DirectionalShadowGpuBudgetMicroseconds ||
            profile.DirectionalShadowMemoryBytes >
                profile.DirectionalShadowMemoryBudgetBytes ||
            profile.MeasuredImageDifference > profile.MaximumImageDifference ||
            profile.VulkanValidationErrorCount != 0 ||
            !profile.VisualReviewApproved)
        {
            throw new InvalidDataException(
                "Directional shadow capture profile does not pass its frozen gates.");
        }
    }

    private static void VerifyArtifact(
        string manifestDirectory,
        DirectionalShadowQualificationArtifactPin artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.RelativePath) ||
            Path.IsPathRooted(artifact.RelativePath) ||
            artifact.ByteLength <= 0 ||
            artifact.ByteLength >
                DirectionalShadowQualificationContract.MaximumArtifactBytes ||
            DirectionalShadowQualificationContract.NormalizeSha256(
                artifact.Sha256).Length != 64)
            throw new InvalidDataException(
                "Directional shadow artifact pin is invalid.");

        string root = Path.GetFullPath(manifestDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(
            root,
            artifact.RelativePath));
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root, pathComparison))
            throw new InvalidDataException(
                "Directional shadow artifact escapes the manifest directory.");

        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length != artifact.ByteLength)
            throw new InvalidDataException(
                "Directional shadow artifact size does not match its pin.");
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        string actual = Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
        info.Refresh();
        if (info.Length != artifact.ByteLength ||
            !DirectionalShadowQualificationContract.FixedSha256Equals(
                actual,
                artifact.Sha256))
            throw new InvalidDataException(
                "Directional shadow artifact hash does not match its pin.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            MaxDepth = MaximumJsonDepth,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
