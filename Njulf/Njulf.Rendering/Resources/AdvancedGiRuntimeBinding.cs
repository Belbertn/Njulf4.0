using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Exact non-device identity selected by a startup profile. Qualification is
/// evaluated against this identity before immutable graph inventory is built,
/// then latched only after the scene producer reports the same profile and
/// authored-asset hash.
/// </summary>
public readonly record struct AdvancedGiRuntimeContentBinding(
    string CorpusSha256,
    string ContentProfileId,
    string SceneAssetSha256)
{
    public bool IsWellFormed =>
        AdvancedGiQualificationContract.NormalizeSha256(CorpusSha256).Length == 64 &&
        AdvancedGiQualificationContract.IsCanonicalToken(ContentProfileId, 256) &&
        AdvancedGiQualificationContract.NormalizeSha256(SceneAssetSha256).Length == 64;

    public AdvancedGiRuntimeContentBinding Normalize() => new(
        NormalizeHash(CorpusSha256),
        ContentProfileId?.Trim() ?? string.Empty,
        NormalizeHash(SceneAssetSha256));

    private static string NormalizeHash(string? value)
    {
        string normalized = AdvancedGiQualificationContract.NormalizeSha256(value);
        return normalized.Length == 64 ? "sha256:" + normalized : string.Empty;
    }
}

public readonly record struct AdvancedGiRuntimeContentState(
    AdvancedGiRuntimeContentBinding Expected,
    string ObservedContentProfileId,
    string ObservedSceneAssetSha256,
    bool Matched,
    string Reason)
{
    public static AdvancedGiRuntimeContentState Unconfigured { get; } = new(
        default,
        string.Empty,
        string.Empty,
        false,
        "advanced-gi-runtime-content-binding-not-configured");
}

/// <summary>
/// Central frame-admission policy for feature modes whose authorization is
/// bound to an exact corpus/profile/scene tuple. Explicit C4/C5 modes only
/// require this match when they were admitted by a candidate authorization;
/// AutoQualified always requires it.
/// </summary>
internal static class AdvancedGiRuntimeContentPolicy
{
    public static bool RequiresExactMatch(
        GiCausticMode mode,
        bool usesCandidateAuthorization) =>
        mode == GiCausticMode.AutoQualified || usesCandidateAuthorization;

    public static bool RequiresExactMatch(
        SimpleDdgiNearFieldResidualMode mode,
        bool usesCandidateAuthorization) =>
        mode == SimpleDdgiNearFieldResidualMode.AutoQualified ||
        usesCandidateAuthorization;
}

/// <summary>
/// Canonical fingerprint of GI controls that can affect any B1/C1/C3/C4/C5
/// measurement. Qualification identifiers and mode-selection policy are
/// intentionally excluded: they authorize an already measured implementation
/// and therefore cannot participate in their own identity.
/// </summary>
public static class AdvancedGiSettingsFingerprint
{
    private const string Domain = "advanced-gi-effective-settings/v1";

    private static readonly HashSet<string> ExcludedProperties = new(
        StringComparer.Ordinal)
    {
        nameof(GlobalIlluminationSettings.DebugView),
        nameof(GlobalIlluminationSettings.SimpleDdgiReceiverFeedbackMode),
        nameof(GlobalIlluminationSettings.DdgiOpacityMicromapMode),
        nameof(GlobalIlluminationSettings.SimpleDdgiDirectionalGuidingMode),
        nameof(GlobalIlluminationSettings.GiCausticMode),
        nameof(GlobalIlluminationSettings.SimpleDdgiNearFieldResidualMode),
        nameof(GlobalIlluminationSettings.SimpleDdgiReceiverFeedbackQualificationId),
        nameof(GlobalIlluminationSettings.DdgiOpacityMicromapQualificationId),
        nameof(GlobalIlluminationSettings.SimpleDdgiDirectionalGuidingQualificationId),
        nameof(GlobalIlluminationSettings.GiCausticQualificationId),
        nameof(GlobalIlluminationSettings.SimpleDdgiNearFieldResidualQualificationId),
        // Compatibility aliases duplicate the versioned properties above.
        nameof(GlobalIlluminationSettings.SimpleDdgiReceiverContributionFeedbackEnabled),
        nameof(GlobalIlluminationSettings.DdgiOpacityMicromapExperimentEnabled),
        nameof(GlobalIlluminationSettings.SimpleDdgiDirectionalRayGuidingExperimentEnabled),
        nameof(GlobalIlluminationSettings.DdgiTaggedCausticCacheExperimentEnabled),
        nameof(GlobalIlluminationSettings.SimpleDdgiNearFieldResidualExperimentEnabled)
    };

    private static readonly PropertyInfo[] Properties =
        typeof(GlobalIlluminationSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetIndexParameters().Length == 0 &&
                property.GetMethod?.IsPublic == true &&
                IsCanonicalScalar(property.PropertyType) &&
                !ExcludedProperties.Contains(property.Name))
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();

    public static string Compute(GlobalIlluminationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AdvancedGiQualificationContract.Append(hash, Domain);
        foreach (PropertyInfo property in Properties)
        {
            AdvancedGiQualificationContract.Append(hash, property.Name);
            AdvancedGiQualificationContract.Append(
                hash,
                Format(property.GetValue(settings), property.PropertyType));
        }

        // These tokens identify the implementation under test independently
        // from ExplicitExperiment versus AutoQualified selection policy.
        AdvancedGiQualificationContract.Append(
            hash,
            "B1=" + Normalize(settings.SimpleDdgiReceiverFeedbackMode));
        AdvancedGiQualificationContract.Append(
            hash,
            "C1=" + Normalize(settings.DdgiOpacityMicromapMode));
        AdvancedGiQualificationContract.Append(
            hash,
            "C3=" + Normalize(settings.SimpleDdgiDirectionalGuidingMode));
        AdvancedGiQualificationContract.Append(
            hash,
            "C4=" + Normalize(settings.GiCausticMode));
        AdvancedGiQualificationContract.Append(
            hash,
            "C5=" + Normalize(settings.SimpleDdgiNearFieldResidualMode));
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    internal static IReadOnlyList<PropertyInfo> CanonicalProperties => Properties;

    private static bool IsCanonicalScalar(Type type) =>
        type == typeof(bool) || type == typeof(string) ||
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type.IsEnum;

    private static string Format(object? value, Type type)
    {
        if (value is null)
            return "<null>";
        if (type == typeof(float))
            return BitConverter.SingleToUInt32Bits((float)value)
                .ToString("x8", CultureInfo.InvariantCulture);
        if (type == typeof(double))
            return BitConverter.DoubleToUInt64Bits((double)value)
                .ToString("x16", CultureInfo.InvariantCulture);
        if (type.IsEnum)
        {
            return (type.FullName ?? type.Name) + ":" +
                Convert.ToUInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
        }
        return value switch
        {
            bool boolean => boolean ? "1" : "0",
            IFormattable formattable =>
                formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string Normalize(SimpleDdgiReceiverFeedbackMode mode) =>
        mode is SimpleDdgiReceiverFeedbackMode.ExactCompacted or
            SimpleDdgiReceiverFeedbackMode.AutoQualified
            ? "exact-compacted"
            : mode.ToString();

    private static string Normalize(DdgiOpacityMicromapMode mode) =>
        mode is DdgiOpacityMicromapMode.ExtFourStateExperiment or
            DdgiOpacityMicromapMode.AutoQualified
            ? "ext-four-state"
            : mode.ToString();

    private static string Normalize(SimpleDdgiDirectionalGuidingMode mode) =>
        mode is SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment or
            SimpleDdgiDirectionalGuidingMode.AutoQualified
            ? "per-probe-histogram"
            : mode.ToString();

    private static string Normalize(GiCausticMode mode) =>
        mode is GiCausticMode.WorldCacheExperiment or
            GiCausticMode.AutoQualified
            ? "world-cache"
            : mode.ToString();

    private static string Normalize(SimpleDdgiNearFieldResidualMode mode) =>
        mode is SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment or
            SimpleDdgiNearFieldResidualMode.AutoQualified
            ? "hiz-residual"
            : mode.ToString();
}
