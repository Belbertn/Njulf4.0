using System;
using System.Diagnostics;
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
    public const string TailJacobi = "tail-jacobi";
    public const string TailAccelerated = "tail-accelerated";
    public const string ForwardGiEnabled = "forward-gi-enabled";
    public const string ForwardGiDisabled = "forward-gi-disabled";
    public const string ForwardGiExact = "forward-gi-exact";
    public const string DecalMaterialPrefix = "decal-material:";

    public static bool IsTailVariant(string? variant)
    {
        string normalized = string.IsNullOrWhiteSpace(variant)
            ? Baseline
            : variant.Trim().ToLowerInvariant();
        return normalized is TailJacobi or TailAccelerated;
    }

    public static string Apply(RenderSettings settings, string? variant)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string normalized = Normalize(variant);

        // Explicitly reset all capture-only switches. ApplySmokeRenderSettings
        // can run more than once during startup/reload validation.
        settings.Decals.IsolatedMaterialIndex = -1;
        settings.GlobalIllumination.SimpleDdgiForceLegacyFarFieldFallbackEvaluation = false;
        settings.Diagnostics.SuppressForwardGiGatherForBenchmark = false;
        settings.Diagnostics.ForceExactForwardGiGatherForBenchmark = false;

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
            case TailJacobi:
            case TailAccelerated:
                settings.GlobalIllumination.SimpleDdgiSchedulerMode =
                    SimpleDdgiSchedulerMode.GpuResident;
                settings.GlobalIllumination.SimpleDdgiTransportV2Enabled = true;
                settings.GlobalIllumination.SimpleDdgiTransportTailCertificationEnabled = true;
                settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled =
                    normalized == TailAccelerated;
                return normalized;
            case ForwardGiEnabled:
                return normalized;
            case ForwardGiExact:
                settings.Diagnostics.ForceExactForwardGiGatherForBenchmark = true;
                return normalized;
            case ForwardGiDisabled:
                settings.Diagnostics.SuppressForwardGiGatherForBenchmark = true;
                return normalized;
        }

        if (normalized.StartsWith(DecalMaterialPrefix, StringComparison.Ordinal))
        {
            settings.Decals.IsolatedMaterialIndex = int.Parse(
                normalized[DecalMaterialPrefix.Length..],
                CultureInfo.InvariantCulture);
            return normalized;
        }

        throw new UnreachableException(
            $"Normalized benchmark capture variant '{normalized}' was not applied.");
    }

    public static string Normalize(string? variant)
    {
        string normalized = string.IsNullOrWhiteSpace(variant)
            ? Baseline
            : variant.Trim().ToLowerInvariant();
        if (normalized is Baseline or DecalsDisabled or DecalDdgiDisabled or
            DecalShadowsDisabled or FarFieldGated or FarFieldForcedOld or
            TailJacobi or TailAccelerated or ForwardGiEnabled or
            ForwardGiDisabled or ForwardGiExact)
        {
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

            return $"{DecalMaterialPrefix}{materialIndex.ToString(CultureInfo.InvariantCulture)}";
        }

        throw new ArgumentException(
            $"Unknown benchmark capture variant '{variant}'. Supported variants: " +
            $"{Baseline}, {DecalsDisabled}, {DecalDdgiDisabled}, " +
            $"{DecalShadowsDisabled}, {FarFieldGated}, {FarFieldForcedOld}, " +
            $"{TailJacobi}, {TailAccelerated}, " +
            $"{ForwardGiEnabled}, {ForwardGiDisabled}, {ForwardGiExact}, " +
            $"or {DecalMaterialPrefix}<index>.",
            nameof(variant));
    }
}
