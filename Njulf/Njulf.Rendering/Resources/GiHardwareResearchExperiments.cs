using System;

namespace Njulf.Rendering.Resources;

public enum GiOpacityMicromapState
{
    Unknown,
    Transparent,
    Opaque
}

public readonly record struct GiOpacityMicromapAssetFacts(
    bool StaticAlphaMask,
    bool StableUvs,
    bool CompleteResidentMipChain,
    bool ExactSamplerPolicy,
    bool ExactCutoffPolicy,
    bool ThinTransmissionAbsent,
    bool ProceduralMaskAbsent);

public readonly record struct GiOpacityMicromapClassification(
    GiOpacityMicromapState State,
    string Reason)
{
    public bool RequiresShaderConfirmation =>
        State == GiOpacityMicromapState.Unknown;
}

public readonly record struct GiOpacityMicromapHardwareCapabilities(
    bool ExtensionAvailable,
    bool FeatureAvailable,
    bool HostCommandsAvailable,
    uint MaximumTwoStateSubdivisionLevel,
    uint MaximumFourStateSubdivisionLevel,
    bool RuntimeBackendEnabled);

public readonly record struct GiOpacityMicromapQualification(
    bool VisibilityParityPassed,
    bool ThinTransmissionParityPassed,
    bool BuildCostAmortized,
    bool TotalGiTimeImproved,
    ulong ResidentBytes,
    ulong MemoryBudgetBytes);

public readonly record struct GiOpacityMicromapContentKey(
    ulong MeshTopologyRevision,
    ulong UvRevision,
    ulong AlphaTextureRevision,
    uint CutoffBits,
    ulong ResidencyRevision,
    uint SubdivisionLevel);

public static class GiOpacityMicromapExperiment
{
    public static GiOpacityMicromapClassification ClassifyMicrotriangle(
        float minimumAlpha,
        float maximumAlpha,
        float cutoff,
        in GiOpacityMicromapAssetFacts facts)
    {
        if (!float.IsFinite(minimumAlpha) ||
            !float.IsFinite(maximumAlpha) ||
            !float.IsFinite(cutoff) ||
            minimumAlpha < 0.0f || maximumAlpha > 1.0f ||
            minimumAlpha > maximumAlpha || cutoff < 0.0f || cutoff > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAlpha));
        }

        if (!facts.StaticAlphaMask)
            return Unknown("animated-or-non-mask-alpha");
        if (!facts.StableUvs)
            return Unknown("deforming-or-procedural-uv");
        if (!facts.CompleteResidentMipChain)
            return Unknown("incomplete-alpha-mips-or-residency");
        if (!facts.ExactSamplerPolicy)
            return Unknown("sampler-policy-mismatch");
        if (!facts.ExactCutoffPolicy)
            return Unknown("cutoff-policy-mismatch");
        if (!facts.ThinTransmissionAbsent)
            return Unknown("thin-transmission-requires-shader-confirmation");
        if (!facts.ProceduralMaskAbsent)
            return Unknown("procedural-mask-requires-shader-confirmation");

        // Match the shipping alpha test exactly: alpha < cutoff is discarded,
        // alpha == cutoff is opaque. Boundary-straddling cells remain unknown.
        if (maximumAlpha < cutoff)
            return new GiOpacityMicromapClassification(
                GiOpacityMicromapState.Transparent,
                "provably-below-cutoff");
        if (minimumAlpha >= cutoff)
            return new GiOpacityMicromapClassification(
                GiOpacityMicromapState.Opaque,
                "provably-at-or-above-cutoff");
        return Unknown("cutoff-boundary-unknown");

        static GiOpacityMicromapClassification Unknown(string reason) => new(
            GiOpacityMicromapState.Unknown,
            reason);
    }

    public static GiExperimentAdmission EvaluateAdmission(
        bool requested,
        in GiOpacityMicromapHardwareCapabilities hardware,
        in GiOpacityMicromapQualification qualification)
    {
        if (!requested)
            return GiExperimentAdmission.Disabled("C1");
        if (!hardware.ExtensionAvailable)
        {
            return GiExperimentAdmission.Missing(
                "C1",
                "VK_EXT_opacity_micromap-unavailable");
        }
        if (!hardware.FeatureAvailable ||
            hardware.MaximumFourStateSubdivisionLevel == 0u)
        {
            return GiExperimentAdmission.Missing(
                "C1",
                "opacity-micromap-feature-or-format-unavailable");
        }
        if (!hardware.RuntimeBackendEnabled)
        {
            return new GiExperimentAdmission(
                "C1",
                true,
                true,
                false,
                GiExperimentStage.CapabilityAvailable,
                0UL,
                "capability-only-runtime-backend-not-enabled");
        }

        bool memoryFits = qualification.MemoryBudgetBytes == 0UL ||
            qualification.ResidentBytes <= qualification.MemoryBudgetBytes;
        bool qualified = qualification.VisibilityParityPassed &&
            qualification.ThinTransmissionParityPassed &&
            qualification.BuildCostAmortized &&
            qualification.TotalGiTimeImproved &&
            memoryFits;
        if (!qualified)
        {
            return new GiExperimentAdmission(
                "C1",
                true,
                true,
                false,
                GiExperimentStage.QualificationFailed,
                0UL,
                ResolveQualificationFailure(qualification, memoryFits));
        }

        return new GiExperimentAdmission(
            "C1",
            true,
            true,
            true,
            GiExperimentStage.Active,
            qualification.ResidentBytes,
            "active-qualified-experiment");
    }

    private static string ResolveQualificationFailure(
        in GiOpacityMicromapQualification qualification,
        bool memoryFits)
    {
        if (!qualification.VisibilityParityPassed)
            return "alpha-visibility-parity-failed";
        if (!qualification.ThinTransmissionParityPassed)
            return "thin-transmission-parity-failed";
        if (!memoryFits)
            return "micromap-memory-budget-rejected";
        if (!qualification.BuildCostAmortized)
            return "construction-cost-not-amortized";
        return "total-gi-time-win-not-demonstrated";
    }
}

public enum GiRayTracingExperimentBackend
{
    InlineRayQuery,
    RayTracingPipeline,
    RayTracingPipelineWithInvocationReorder
}

public readonly record struct GiRayTracingPipelineHardwareCapabilities(
    bool PipelineExtensionAvailable,
    bool PipelineFeatureAvailable,
    bool InvocationReorderExtensionAvailable,
    bool InvocationReorderFeatureAvailable,
    bool EffectiveReorderingHint,
    uint MaximumShaderBindingTableRecordIndex,
    bool RuntimeBackendEnabled);

public readonly record struct GiRayTracingBackendMeasurement(
    GiRayTracingExperimentBackend Backend,
    bool SameRayParityPassed,
    bool AlphaAndTransmissionParityPassed,
    bool FarFieldParityPassed,
    bool PrewarmedWithoutFirstUseCreation,
    double P95TotalGiMicroseconds,
    double MeanTotalGiMicroseconds,
    ulong ResidentBytes);

public readonly record struct GiRayTracingBackendSelection(
    GiRayTracingExperimentBackend SelectedBackend,
    GiExperimentAdmission Admission);

public static class GiRayTracingInvocationReorderExperiment
{
    public static GiRayTracingBackendSelection Select(
        bool requested,
        in GiRayTracingPipelineHardwareCapabilities hardware,
        in GiRayTracingBackendMeasurement inlineBaseline,
        in GiRayTracingBackendMeasurement pipeline,
        in GiRayTracingBackendMeasurement reordered)
    {
        if (!requested)
        {
            return new GiRayTracingBackendSelection(
                GiRayTracingExperimentBackend.InlineRayQuery,
                GiExperimentAdmission.Disabled("C2"));
        }
        if (!hardware.PipelineExtensionAvailable ||
            !hardware.PipelineFeatureAvailable)
        {
            return Missing("VK_KHR_ray_tracing_pipeline-unavailable");
        }
        if (!hardware.RuntimeBackendEnabled)
        {
            return new GiRayTracingBackendSelection(
                GiRayTracingExperimentBackend.InlineRayQuery,
                new GiExperimentAdmission(
                    "C2",
                    true,
                    true,
                    false,
                    GiExperimentStage.CapabilityAvailable,
                    0UL,
                    "capability-only-runtime-pipeline-backend-not-enabled"));
        }
        if (!IsValidBaseline(inlineBaseline))
            return Failed("inline-baseline-measurement-invalid");

        GiRayTracingBackendMeasurement selected = inlineBaseline;
        if (Qualifies(pipeline, inlineBaseline))
            selected = pipeline;

        bool reorderCapable = hardware.InvocationReorderExtensionAvailable &&
            hardware.InvocationReorderFeatureAvailable &&
            hardware.EffectiveReorderingHint;
        if (reorderCapable && Qualifies(reordered, inlineBaseline) &&
            IsFaster(reordered, selected))
        {
            selected = reordered;
        }

        if (selected.Backend == GiRayTracingExperimentBackend.InlineRayQuery)
        {
            string status = reorderCapable
                ? "pipeline-total-gi-time-win-not-demonstrated"
                : "pipeline-not-qualified; EXT-reordering-unavailable-or-ineffective";
            return Failed(status);
        }

        return new GiRayTracingBackendSelection(
            selected.Backend,
            new GiExperimentAdmission(
                "C2",
                true,
                true,
                true,
                GiExperimentStage.Active,
                selected.ResidentBytes,
                selected.Backend ==
                    GiRayTracingExperimentBackend.RayTracingPipelineWithInvocationReorder
                        ? "active-qualified-EXT-invocation-reorder"
                        : "active-qualified-ray-tracing-pipeline"));

        GiRayTracingBackendSelection Missing(string status) => new(
            GiRayTracingExperimentBackend.InlineRayQuery,
            GiExperimentAdmission.Missing("C2", status));
        GiRayTracingBackendSelection Failed(string status) => new(
            GiRayTracingExperimentBackend.InlineRayQuery,
            new GiExperimentAdmission(
                "C2",
                true,
                true,
                false,
                GiExperimentStage.QualificationFailed,
                0UL,
                status));
    }

    private static bool IsValidBaseline(
        in GiRayTracingBackendMeasurement measurement) =>
        measurement.Backend == GiRayTracingExperimentBackend.InlineRayQuery &&
        measurement.SameRayParityPassed &&
        measurement.AlphaAndTransmissionParityPassed &&
        measurement.FarFieldParityPassed &&
        measurement.PrewarmedWithoutFirstUseCreation &&
        double.IsFinite(measurement.P95TotalGiMicroseconds) &&
        measurement.P95TotalGiMicroseconds > 0.0 &&
        double.IsFinite(measurement.MeanTotalGiMicroseconds) &&
        measurement.MeanTotalGiMicroseconds > 0.0;

    private static bool Qualifies(
        in GiRayTracingBackendMeasurement candidate,
        in GiRayTracingBackendMeasurement baseline) =>
        candidate.Backend != GiRayTracingExperimentBackend.InlineRayQuery &&
        candidate.SameRayParityPassed &&
        candidate.AlphaAndTransmissionParityPassed &&
        candidate.FarFieldParityPassed &&
        candidate.PrewarmedWithoutFirstUseCreation &&
        IsFaster(candidate, baseline);

    private static bool IsFaster(
        in GiRayTracingBackendMeasurement candidate,
        in GiRayTracingBackendMeasurement baseline) =>
        double.IsFinite(candidate.P95TotalGiMicroseconds) &&
        double.IsFinite(candidate.MeanTotalGiMicroseconds) &&
        candidate.P95TotalGiMicroseconds <
            baseline.P95TotalGiMicroseconds * 0.98 &&
        candidate.MeanTotalGiMicroseconds <
            baseline.MeanTotalGiMicroseconds * 0.98;
}
