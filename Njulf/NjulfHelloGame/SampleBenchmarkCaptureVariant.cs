using System;
using System.Globalization;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Applies one named, deterministic capture delta. All unspecified renderer
/// state remains owned by the selected quality/scenario profile.
/// </summary>
public static class SampleBenchmarkCaptureVariant
{
    public const string Baseline = "baseline";
    public const string DecalsDisabled = "decals-disabled";
    public const string DecalDdgiDisabled = "decal-ddgi-disabled";
    public const string DecalShadowsDisabled = "decal-shadows-disabled";
    public const string FarFieldGated = "far-field-gated";
    public const string FarFieldForcedOld = "far-field-forced-old";
    public const string DecalMaterialPrefix = "decal-material:";

    public static string Apply(RenderSettings settings, string? variant)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string normalized = string.IsNullOrWhiteSpace(variant)
            ? Baseline
            : variant.Trim().ToLowerInvariant();

        // Explicitly reset all capture-only switches. ApplySmokeRenderSettings
        // can run more than once during startup/reload validation.
        settings.Decals.IsolatedMaterialIndex = -1;
        settings.GlobalIllumination.SimpleDdgiForceLegacyFarFieldFallbackEvaluation = false;

        switch (normalized)
        {
            case Baseline:
            case FarFieldGated:
                return normalized;
            case DecalsDisabled:
                settings.Decals.GeometryDecalsEnabled = false;
                settings.Decals.ProjectedDecalsEnabled = false;
                return normalized;
            case DecalDdgiDisabled:
                settings.Decals.ReceiveGlobalIllumination = false;
                return normalized;
            case DecalShadowsDisabled:
                settings.Decals.ReceiveShadows = false;
                return normalized;
            case FarFieldForcedOld:
                settings.GlobalIllumination.SimpleDdgiForceLegacyFarFieldFallbackEvaluation = true;
                return normalized;
        }

        if (normalized.StartsWith(DecalMaterialPrefix, StringComparison.Ordinal))
        {
            string indexText = normalized[DecalMaterialPrefix.Length..];
            if (!int.TryParse(
                    indexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int materialIndex) || materialIndex < 0)
            {
                throw new ArgumentException(
                    $"Benchmark variant '{variant}' requires a non-negative material index.",
                    nameof(variant));
            }

            settings.Decals.IsolatedMaterialIndex = materialIndex;
            return $"{DecalMaterialPrefix}{materialIndex.ToString(CultureInfo.InvariantCulture)}";
        }

        throw new ArgumentException(
            $"Unknown benchmark capture variant '{variant}'. Supported variants: " +
            $"{Baseline}, {DecalsDisabled}, {DecalDdgiDisabled}, " +
            $"{DecalShadowsDisabled}, {FarFieldGated}, {FarFieldForcedOld}, " +
            $"or {DecalMaterialPrefix}<index>.",
            nameof(variant));
    }
}
