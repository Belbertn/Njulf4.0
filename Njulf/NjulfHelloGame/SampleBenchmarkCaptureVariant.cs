using System;
using System.Diagnostics;
using System.Globalization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

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
    public const string TransparentGiDisabled = "transparent-gi-disabled";
    public const string TransparentShadowsDisabled =
        "transparent-shadows-disabled";
    public const string FarFieldGated = "far-field-gated";
    public const string FarFieldForcedOld = "far-field-forced-old";
    public const string TailJacobi = "tail-jacobi";
    public const string TailAccelerated = "tail-accelerated";
    public const string ForwardGiEnabled = "forward-gi-enabled";
    public const string ForwardGiDisabled = "forward-gi-disabled";
    public const string ForwardGiExact = "forward-gi-exact";
    public const string ForwardGiSurface = "forward-gi-surface";
    public const string ForwardGiTemporal = "forward-gi-temporal";
    public const string ForwardGiExactTailJacobi =
        "forward-gi-exact-tail-jacobi";
    public const string ForwardGiExactTailAccelerated =
        "forward-gi-exact-tail-accelerated";
    public const string ForwardGiSurfaceTailJacobi =
        "forward-gi-surface-tail-jacobi";
    public const string ForwardGiSurfaceTailAccelerated =
        "forward-gi-surface-tail-accelerated";
    public const string ForwardGiSurfaceDiagnostics =
        "forward-gi-surface-diagnostics";
    public const string AmbientOcclusionDisabled =
        "ambient-occlusion-disabled";
    public const string AmbientOcclusionRaw = "ambient-occlusion-raw";
    public const string AmbientOcclusionBlurred =
        "ambient-occlusion-blurred";
    public const string AmbientOcclusionFinal = "ambient-occlusion-final";
    public const string AmbientOcclusionUnblurred =
        "ambient-occlusion-unblurred";
    public const string MaterialOcclusion = "material-occlusion";
    public const string MaterialBaseColor = "material-base-color";
    public const string MaterialWorldNormal = "material-world-normal";
    public const string GiFinalIndirect = "gi-final-indirect";
    public const string ReflectionsDisabled = "reflections-disabled";
    public const string DdgiDiffuseOnly = "ddgi-diffuse-only";
    public const string DdgiDirectionalReceiverOff =
        "ddgi-directional-receiver-off";
    public const string ReflectionSourceSelection =
        "reflection-source-selection";
    public const string ReflectionDetailBudget =
        "reflection-detail-budget";
    public const string ReflectionDdgiLobe =
        "reflection-ddgi-lobe";
    public const string ReflectionReceiverMaterial =
        "reflection-receiver-material";
    public const string ReflectionRoughnessInputs =
        "reflection-roughness-inputs";
    public const string DirectionalShadowForcedRefresh =
        "directional-shadow-forced-refresh";
    public const string DirectionalShadowsDisabled =
        "directional-shadows-disabled";
    public const string DecalMaterialPrefix = "decal-material:";

    public static bool IsTailVariant(string? variant)
    {
        string normalized = string.IsNullOrWhiteSpace(variant)
            ? Baseline
            : variant.Trim().ToLowerInvariant();
        return normalized is TailJacobi or TailAccelerated or
            ForwardGiExactTailJacobi or ForwardGiExactTailAccelerated or
            ForwardGiSurfaceTailJacobi or
            ForwardGiSurfaceTailAccelerated;
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
        settings.Diagnostics.ForceForwardGiReceiverCacheForBenchmark = false;
        settings.Diagnostics.ForceExactForwardGiGatherForBenchmark = false;
        settings.Diagnostics.DdgiForwardEstimateCountersEnabled = false;
        settings.Shadows.ForceStaticCascadeCacheRefresh = false;
        settings.AmbientOcclusion.DebugView = AmbientOcclusionDebugView.None;
        settings.Reflections.DebugView = ReflectionDebugView.None;
        settings.Materials.DebugView = MaterialDebugView.None;
        settings.GlobalIllumination.DebugView =
            GlobalIlluminationDebugView.None;

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
            case TransparentGiDisabled:
                settings.Transparency.ReceiveGlobalIllumination = false;
                return normalized;
            case TransparentShadowsDisabled:
                settings.Transparency.ReceiveShadows = false;
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
                settings.Diagnostics.ForceForwardGiReceiverCacheForBenchmark = true;
                return normalized;
            case ForwardGiExact:
                settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                    SimpleDdgiReceiverCacheMode.Exact;
                settings.Diagnostics.ForceExactForwardGiGatherForBenchmark = true;
                return normalized;
            case ForwardGiSurface:
                settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial;
                return normalized;
            case ForwardGiTemporal:
                settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                    SimpleDdgiReceiverCacheMode.TemporalAdaptive;
                return normalized;
            case ForwardGiExactTailJacobi:
            case ForwardGiExactTailAccelerated:
                ConfigureTailSolver(
                    settings,
                    normalized == ForwardGiExactTailAccelerated);
                settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                    SimpleDdgiReceiverCacheMode.Exact;
                settings.Diagnostics.ForceExactForwardGiGatherForBenchmark = true;
                return normalized;
            case ForwardGiSurfaceTailJacobi:
            case ForwardGiSurfaceTailAccelerated:
                ConfigureTailSolver(
                    settings,
                    normalized == ForwardGiSurfaceTailAccelerated);
                settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial;
                return normalized;
            case ForwardGiSurfaceDiagnostics:
                settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial;
                settings.GlobalIllumination.GiCausticMode =
                    GiCausticMode.Off;
                settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
                    SimpleDdgiNearFieldResidualMode.Off;
                settings.GlobalIllumination.SimpleDdgiDirectionalRadianceMode =
                    SimpleDdgiDirectionalRadianceMode.Off;
                settings.GlobalIllumination.SimpleDdgiGlossyTransportMode =
                    SimpleDdgiGlossyTransportMode.Off;
                settings.Diagnostics.DdgiForwardEstimateCountersEnabled = true;
                // Qualification counter artifacts intentionally own the
                // ordinary SceneColor contract. Keep this diagnostic variant
                // isolated from C4/C5 and directional/hybrid-reflection MRT
                // production.
                settings.Reflections.Enabled = false;
                return normalized;
            case ForwardGiDisabled:
                settings.Diagnostics.SuppressForwardGiGatherForBenchmark = true;
                return normalized;
            case AmbientOcclusionDisabled:
                settings.AmbientOcclusion.Enabled = false;
                return normalized;
            case AmbientOcclusionRaw:
                settings.AmbientOcclusion.DebugView =
                    AmbientOcclusionDebugView.RawAo;
                return normalized;
            case AmbientOcclusionBlurred:
                settings.AmbientOcclusion.DebugView =
                    AmbientOcclusionDebugView.BlurredAo;
                return normalized;
            case AmbientOcclusionFinal:
                settings.AmbientOcclusion.DebugView =
                    AmbientOcclusionDebugView.FinalAo;
                return normalized;
            case AmbientOcclusionUnblurred:
                settings.AmbientOcclusion.BlurRadius = 0;
                return normalized;
            case MaterialOcclusion:
                settings.Materials.DebugView =
                    MaterialDebugView.MaterialOcclusion;
                return normalized;
            case MaterialBaseColor:
                settings.Materials.DebugView = MaterialDebugView.BaseColor;
                return normalized;
            case MaterialWorldNormal:
                settings.Materials.DebugView = MaterialDebugView.WorldNormal;
                return normalized;
            case GiFinalIndirect:
                settings.GlobalIllumination.DebugView =
                    GlobalIlluminationDebugView.FinalIndirect;
                return normalized;
            case ReflectionsDisabled:
                settings.Reflections.Enabled = false;
                return normalized;
            case DdgiDiffuseOnly:
                settings.Reflections.Enabled = false;
                settings.GlobalIllumination.SimpleDdgiGlossyTransportMode =
                    SimpleDdgiGlossyTransportMode.Off;
                return normalized;
            case DdgiDirectionalReceiverOff:
                settings.Reflections.Enabled = false;
                settings.GlobalIllumination
                    .SimpleDdgiRoughSpecularMinimumRoughness = 1.0f;
                settings.GlobalIllumination
                    .SimpleDdgiRoughSpecularFullWeightRoughness = 1.0f;
                return normalized;
            case ReflectionSourceSelection:
                settings.Reflections.DebugView =
                    ReflectionDebugView.SourceSelection;
                return normalized;
            case ReflectionDetailBudget:
                settings.Reflections.DebugView =
                    ReflectionDebugView.DetailBudget;
                return normalized;
            case ReflectionDdgiLobe:
                settings.Reflections.DebugView =
                    ReflectionDebugView.DdgiDirectionalRadianceLobe;
                return normalized;
            case ReflectionReceiverMaterial:
                settings.Reflections.DebugView =
                    ReflectionDebugView.ReceiverMaterial;
                return normalized;
            case ReflectionRoughnessInputs:
                settings.Reflections.DebugView =
                    ReflectionDebugView.RoughnessInputs;
                return normalized;
            case DirectionalShadowForcedRefresh:
                settings.Shadows.ForceStaticCascadeCacheRefresh = true;
                return normalized;
            case DirectionalShadowsDisabled:
                settings.Shadows.DirectionalShadowsEnabled = false;
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
            DecalShadowsDisabled or TransparentGiDisabled or
            TransparentShadowsDisabled or FarFieldGated or FarFieldForcedOld or
            TailJacobi or TailAccelerated or ForwardGiEnabled or
            ForwardGiDisabled or ForwardGiExact or ForwardGiSurface or
            ForwardGiTemporal or ForwardGiExactTailJacobi or
            ForwardGiExactTailAccelerated or ForwardGiSurfaceTailJacobi or
            ForwardGiSurfaceTailAccelerated or
            ForwardGiSurfaceDiagnostics or ReflectionsDisabled or
            AmbientOcclusionDisabled or AmbientOcclusionRaw or
            AmbientOcclusionBlurred or AmbientOcclusionFinal or
            AmbientOcclusionUnblurred or MaterialOcclusion or
            MaterialBaseColor or MaterialWorldNormal or GiFinalIndirect or
            DdgiDiffuseOnly or DdgiDirectionalReceiverOff or
            ReflectionSourceSelection or ReflectionDetailBudget or
            ReflectionDdgiLobe or ReflectionReceiverMaterial or
            ReflectionRoughnessInputs or
            DirectionalShadowForcedRefresh or DirectionalShadowsDisabled)
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
            $"{DecalShadowsDisabled}, {TransparentGiDisabled}, " +
            $"{TransparentShadowsDisabled}, {FarFieldGated}, {FarFieldForcedOld}, " +
            $"{TailJacobi}, {TailAccelerated}, " +
            $"{ForwardGiEnabled}, {ForwardGiDisabled}, {ForwardGiExact}, " +
            $"{ForwardGiSurface}, {ForwardGiTemporal}, " +
            $"{ForwardGiExactTailJacobi}, {ForwardGiExactTailAccelerated}, " +
            $"{ForwardGiSurfaceTailJacobi}, " +
            $"{ForwardGiSurfaceTailAccelerated}, " +
            $"{ForwardGiSurfaceDiagnostics}, " +
            $"{AmbientOcclusionDisabled}, {AmbientOcclusionRaw}, " +
            $"{AmbientOcclusionBlurred}, {AmbientOcclusionFinal}, " +
            $"{AmbientOcclusionUnblurred}, {MaterialOcclusion}, " +
            $"{MaterialBaseColor}, {MaterialWorldNormal}, {GiFinalIndirect}, " +
            $"{ReflectionsDisabled}, {DdgiDiffuseOnly}, " +
            $"{DdgiDirectionalReceiverOff}, " +
            $"{ReflectionSourceSelection}, {ReflectionDetailBudget}, " +
            $"{ReflectionDdgiLobe}, {ReflectionReceiverMaterial}, " +
            $"{ReflectionRoughnessInputs}, " +
            $"{DirectionalShadowForcedRefresh}, {DirectionalShadowsDisabled}, " +
            $"or {DecalMaterialPrefix}<index>.",
            nameof(variant));
    }

    private static void ConfigureTailSolver(
        RenderSettings settings,
        bool accelerated)
    {
        settings.GlobalIllumination.SimpleDdgiSchedulerMode =
            SimpleDdgiSchedulerMode.GpuResident;
        settings.GlobalIllumination.SimpleDdgiTransportV2Enabled = true;
        settings.GlobalIllumination.SimpleDdgiTransportTailCertificationEnabled =
            true;
        settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled =
            accelerated;
    }
}
