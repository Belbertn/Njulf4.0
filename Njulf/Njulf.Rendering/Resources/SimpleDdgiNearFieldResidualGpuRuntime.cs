using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// C5 GPU-stage configuration.  The trace-source contract is deliberately
/// carried here rather than inferred from a render target: a descriptor that
/// contains final scene colour, DDGI, C4, IBL, or history is rejected before
/// resource allocation or command recording.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuConfiguration(
    uint AbiVersion,
    SimpleDdgiNearFieldTraceSourceContract TraceSourceContract,
    int MaximumTraceSteps,
    int MaximumMipVisits,
    int BinaryRefinementSteps,
    int FilterIterationCount,
    int FilterRadius,
    float Thickness,
    float StartBias,
    float TemporalBlend,
    float DepthTolerance,
    float MinimumNormalDot,
    float MaximumTraceDistance,
    float FullWeightTraceDistance,
    int MinimumB3FootprintRadius,
    int MaximumB3FootprintRadius,
    int MaximumHistoryLength,
    float HitUvTolerance)
{
    public SimpleDdgiNearFieldResidualQualityPreset Preset { get; init; } =
        SimpleDdgiNearFieldResidualQualityPreset.Balanced;
    public int RaysPerPixel { get; init; } = 2;
    public float ResidualIntensity { get; init; } = 1.0f;

    public static SimpleDdgiNearFieldResidualGpuConfiguration CreateReference(
        SimpleDdgiNearFieldResidualLayout layout,
        SimpleDdgiNearFieldResidualProfile profile,
        uint traceSourceAbiRevision = 1u,
        uint traceSourceLayoutRevision = 1u,
        uint traceSourceRevision = 1u) => new(
        SimpleDdgiNearFieldResidualGpuAbi.Version,
        SimpleDdgiNearFieldTraceSourceContract
            .CreatePreDdgiDirectDiffuseAndEmissive(
                layout,
                profile,
                traceSourceAbiRevision,
                traceSourceLayoutRevision,
                traceSourceRevision),
        MaximumTraceSteps: profile.MaximumTraceSteps,
        MaximumMipVisits: profile.MaximumMipVisits,
        BinaryRefinementSteps: profile.BinaryRefinementSteps,
        FilterIterationCount: profile.FilterIterationCount,
        // A 7x7 receiver-bounded footprint overlaps the sparse screen-hit
        // population without crossing object/material/depth/normal edges.
        FilterRadius: 3,
        // Multipliers for the reconstructed receiver pixel footprint. Shader
        // minima remain 2 cm thickness and 1 mm start bias.
        Thickness: 2.0f,
        StartBias: 1.0f,
        // Match the 64-sample running average at saturation. Receiver depth,
        // identity, normal, and revision gates provide motion responsiveness;
        // shortening a stationary estimate to an effective ten-frame EMA
        // reintroduces visible impulse noise at the measured hit rate.
        TemporalBlend: 63.0f / 64.0f,
        DepthTolerance: 0.02f,
        MinimumNormalDot: 0.85f,
        // Preserve the original four-metre high-frequency response, then
        // feather it smoothly across a four-metre guard band.
        MaximumTraceDistance: profile.MaximumTraceDistanceMeters,
        FullWeightTraceDistance: profile.FullWeightTraceDistanceMeters,
        MinimumB3FootprintRadius: 1,
        MaximumB3FootprintRadius: 4,
        MaximumHistoryLength: 64,
        HitUvTolerance: 0.0025f)
    {
        Preset = profile.Preset,
        RaysPerPixel = profile.MaximumRaysPerPixel,
        ResidualIntensity = 1.0f
    };

    public SimpleDdgiNearFieldResidualGpuConfigurationValidation Validate(
        in SimpleDdgiNearFieldResidualLayout layout)
    {
        if (AbiVersion != SimpleDdgiNearFieldResidualGpuAbi.Version)
            return Invalid("near-field-gpu-abi-mismatch");
        if (!TraceSourceContract.TryValidateForLayout(layout, out string sourceFailure))
        {
            // The runtime already publishes stable near-field-prefixed reasons.
            // Preserve that boundary when the reusable source contract rejects
            // a descriptor, while evidence/plan admission can retain the
            // source-contract reason verbatim for artifact diagnosis.
            return Invalid("near-field-" + sourceFailure);
        }
        if (!SimpleDdgiNearFieldResidualGpuAbi.HasOnlyAllowedTraceSources(
                (uint)TraceSourceContract.Terms))
            return Invalid("near-field-trace-source-must-contain-only-direct-diffuse-and-emissive");
        if (MaximumTraceSteps is < 1 or > (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumTraceSteps ||
            MaximumMipVisits is < 1 or > (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumMipVisits ||
            BinaryRefinementSteps is < 0 or > (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumBinaryRefinementSteps ||
            FilterIterationCount is < 0 or > (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumFilterIterations ||
            FilterRadius is < 1 or > (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumFilterRadius)
        {
            return Invalid("near-field-gpu-loop-bound-invalid");
        }
        if (!Enum.IsDefined(Preset) || RaysPerPixel is < 1 or > 4 ||
            !float.IsFinite(ResidualIntensity) || ResidualIntensity is < 0.0f or > 2.0f)
        {
            return Invalid("near-field-gpu-quality-profile-invalid");
        }
        if (layout.FilterIterationCount != FilterIterationCount)
            return Invalid("near-field-filter-iteration-layout-mismatch");
        if (!float.IsFinite(Thickness) || Thickness < 0.0f ||
            !float.IsFinite(StartBias) || StartBias < 0.0f ||
            !float.IsFinite(TemporalBlend) || TemporalBlend < 0.0f || TemporalBlend > 1.0f ||
            !float.IsFinite(DepthTolerance) || DepthTolerance < 0.0f ||
            !float.IsFinite(MinimumNormalDot) || MinimumNormalDot < -1.0f || MinimumNormalDot > 1.0f ||
            !float.IsFinite(MaximumTraceDistance) || MaximumTraceDistance <= 0.0f ||
            MaximumTraceDistance >
                SimpleDdgiNearFieldResidualGpuAbi.MaximumEncodableTraceDistance ||
            !float.IsFinite(FullWeightTraceDistance) ||
            FullWeightTraceDistance <= 0.0f ||
            FullWeightTraceDistance >= MaximumTraceDistance ||
            MinimumB3FootprintRadius is < 1 or >
                (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumB3FootprintRadius ||
            MaximumB3FootprintRadius < MinimumB3FootprintRadius ||
            MaximumB3FootprintRadius >
                (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumB3FootprintRadius ||
            MaximumHistoryLength is < 1 or >
                (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumTemporalHistoryLength ||
            !float.IsFinite(HitUvTolerance) || HitUvTolerance < 0.0f ||
            HitUvTolerance > 0.25f)
        {
            return Invalid("near-field-gpu-numeric-configuration-invalid");
        }

        return SimpleDdgiNearFieldResidualGpuConfigurationValidation.Valid;
    }

    private static SimpleDdgiNearFieldResidualGpuConfigurationValidation Invalid(string reason) =>
        new(false, reason);
}

public readonly record struct SimpleDdgiNearFieldResidualGpuConfigurationValidation(
    bool IsValid,
    string Reason)
{
    public static SimpleDdgiNearFieldResidualGpuConfigurationValidation Valid { get; } =
        new(true, "valid");
}

/// <summary>
/// Declared integration prerequisites for C5.  This is a capability boundary,
/// not a claim of hardware validation: callers must set every bit only after
/// the renderer has actually created the attachments, descriptor sets,
/// barriers, and stage dispatches described by this ABI.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuIntegrationCapabilities(
    bool TracePassRegistered,
    bool TemporalPassRegistered,
    bool FilterPassRegistered,
    bool CompositePassRegistered,
    bool DirectDiffuseEmissiveAttachmentAvailable,
    bool HiZAvailable,
    bool ReceiverMetadataAvailable,
    bool StableSampleRayInputAvailable,
    bool ReceiverBrdfPdfInputAvailable,
    bool MotionVectorsAvailable,
    bool DoubleBufferedHistoryIdentityAvailable,
    bool HistoryIdentityMemoryBudgeted,
    bool TileRecordLayoutValidated,
    bool RequiredImageFormatsValidated,
    bool DescriptorAndBarrierContractValidated,
    bool ShaderArtifactsValidated,
    bool ResetPassRegistered = false,
    bool PingPongBankBindingAndSynchronizationValidated = false,
    bool DirectSourceVariantProvenanceValidated = false,
    bool GeometricAndShadingNormalHistoryAvailable = false,
    bool HitUvAndSourceRevisionValidationAvailable = false,
    bool TemporalVarianceClippingAndBoundedHistoryAvailable = false,
    bool B3FootprintFrequencySeparationValidated = false,
    bool MeasuredQualificationEvidenceVerified = false,
    bool DeviceLimitsAndActualAllocationRequirementsValidated = false)
{
    public bool PreparePassRegistered { get; init; }
    public bool FrequencySeparationPassRegistered { get; init; }
    public bool IndirectDispatchContractValidated { get; init; }
    public bool SurfaceTableAvailable { get; init; }

    public bool IsReady => TracePassRegistered && TemporalPassRegistered &&
        FilterPassRegistered && CompositePassRegistered && ResetPassRegistered &&
        PreparePassRegistered && FrequencySeparationPassRegistered &&
        IndirectDispatchContractValidated && SurfaceTableAvailable &&
        DirectDiffuseEmissiveAttachmentAvailable && HiZAvailable &&
        ReceiverMetadataAvailable && StableSampleRayInputAvailable &&
        ReceiverBrdfPdfInputAvailable && MotionVectorsAvailable &&
        DoubleBufferedHistoryIdentityAvailable &&
        HistoryIdentityMemoryBudgeted && TileRecordLayoutValidated &&
        RequiredImageFormatsValidated &&
        DescriptorAndBarrierContractValidated && ShaderArtifactsValidated &&
        PingPongBankBindingAndSynchronizationValidated &&
        DirectSourceVariantProvenanceValidated &&
        GeometricAndShadingNormalHistoryAvailable &&
        HitUvAndSourceRevisionValidationAvailable &&
        TemporalVarianceClippingAndBoundedHistoryAvailable &&
        B3FootprintFrequencySeparationValidated &&
        DeviceLimitsAndActualAllocationRequirementsValidated;

    public string FailureReason
    {
        get
        {
            if (!TracePassRegistered || !TemporalPassRegistered ||
                !FilterPassRegistered || !CompositePassRegistered || !ResetPassRegistered)
            {
                return "near-field-renderer-passes-not-integrated";
            }
            if (!PreparePassRegistered)
                return "near-field-prepare-pass-not-integrated";
            if (!FrequencySeparationPassRegistered)
                return "near-field-frequency-separation-pass-not-integrated";
            if (!IndirectDispatchContractValidated)
                return "near-field-indirect-dispatch-contract-unvalidated";
            if (!SurfaceTableAvailable)
                return "near-field-surface-table-unavailable";
            if (!DirectDiffuseEmissiveAttachmentAvailable)
                return "near-field-direct-diffuse-emissive-source-attachment-unavailable";
            if (!HiZAvailable)
                return "near-field-hiz-unavailable";
            if (!ReceiverMetadataAvailable)
                return "near-field-receiver-metadata-unavailable";
            if (!StableSampleRayInputAvailable)
                return "near-field-stable-sample-ray-input-unavailable";
            if (!ReceiverBrdfPdfInputAvailable)
                return "near-field-receiver-brdf-pdf-input-unavailable";
            if (!MotionVectorsAvailable)
                return "near-field-motion-vectors-unavailable";
            if (!DoubleBufferedHistoryIdentityAvailable)
                return "near-field-double-buffered-history-identity-unavailable";
            if (!HistoryIdentityMemoryBudgeted)
                return "near-field-history-identity-memory-not-budgeted";
            if (!TileRecordLayoutValidated)
                return "near-field-tile-record-layout-unvalidated";
            if (!RequiredImageFormatsValidated)
                return "near-field-required-image-format-not-validated";
            if (!DescriptorAndBarrierContractValidated)
                return "near-field-descriptor-or-barrier-contract-unvalidated";
            if (!ShaderArtifactsValidated)
                return "near-field-shader-artifacts-unvalidated";
            if (!PingPongBankBindingAndSynchronizationValidated)
                return "near-field-ping-pong-bank-binding-or-synchronization-unvalidated";
            if (!DirectSourceVariantProvenanceValidated)
                return "near-field-direct-source-variant-provenance-unvalidated";
            if (!GeometricAndShadingNormalHistoryAvailable)
                return "near-field-geometric-shading-normal-history-unavailable";
            if (!HitUvAndSourceRevisionValidationAvailable)
                return "near-field-hit-uv-or-source-revision-validation-unavailable";
            if (!TemporalVarianceClippingAndBoundedHistoryAvailable)
                return "near-field-temporal-variance-or-history-length-unvalidated";
            if (!B3FootprintFrequencySeparationValidated)
                return "near-field-b3-footprint-frequency-separation-unvalidated";
            if (!DeviceLimitsAndActualAllocationRequirementsValidated)
                return "near-field-device-limits-or-allocation-requirements-unvalidated";
            return "valid";
        }
    }
}

/// <summary>
/// A pre-admitted C5 runtime request.  Requested developer settings are
/// intentionally absent; a rejected setting must not allocate or bind C5
/// resources merely because a configuration object exists.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuRuntimeRequest(
    bool IsEffectivelyEnabled,
    SimpleDdgiNearFieldResidualLayout Layout,
    SimpleDdgiNearFieldResidualGpuConfiguration Configuration,
    SimpleDdgiNearFieldResidualGpuIntegrationCapabilities Integration);

public enum SimpleDdgiNearFieldResidualGpuResourceKind : byte
{
    DirectDiffuseEmissiveSource = 0,
    RawCandidate = 1,
    HitMetadata = 2,
    HistoryRadiance0 = 3,
    HistoryRadiance1 = 4,
    HistoryMoments0 = 5,
    HistoryMoments1 = 6,
    HistoryValidity0 = 7,
    HistoryValidity1 = 8,
    HistoryMetadata0 = 9,
    HistoryMetadata1 = 10,
    FilterScratch0 = 11,
    FilterScratch1 = 12,
    TileBuffers = 13,
    HistoryNormal0 = 14,
    HistoryNormal1 = 15,
    ReceiverPayload = 16,
    TraceFrameConstants0 = 17,
    TraceFrameConstants1 = 18,
    TelemetryReadback0 = 19,
    TelemetryReadback1 = 20,
    PreparedDepthFootprint = 21,
    PreparedReceiverPayload = 22,
    PreparedMotion = 23,
    SourceLuminance = 24,
    SurfaceTable = 25,
    ActiveTileAndIndirect = 26
}

/// <summary>
/// Backend-owned image or buffer identity.  Handle zero is never a valid
/// allocated C5 resource.  The native allocator decides whether a particular
/// payload is an image or storage buffer, while this contract pins its size and
/// lifetime.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuResource(
    ulong Handle,
    ulong Bytes,
    SimpleDdgiNearFieldResidualGpuResourceKind Kind)
{
    public bool IsAllocated => Handle != 0UL && Bytes != 0UL;
}

/// <summary>
/// Complete C5 allocation.  Every member corresponds to a byte category in
/// <see cref="SimpleDdgiNearFieldResidualLayout"/>; partial feature allocation
/// is invalid by construction.
/// </summary>
public sealed record SimpleDdgiNearFieldResidualGpuAllocation(
    ulong AllocationId,
    SimpleDdgiNearFieldResidualGpuResource DirectDiffuseEmissiveSource,
    SimpleDdgiNearFieldResidualGpuResource ReceiverPayload,
    SimpleDdgiNearFieldResidualGpuResource TraceFrameConstants0,
    SimpleDdgiNearFieldResidualGpuResource TraceFrameConstants1,
    SimpleDdgiNearFieldResidualGpuResource RawCandidate,
    SimpleDdgiNearFieldResidualGpuResource HitMetadata,
    SimpleDdgiNearFieldResidualGpuResource HistoryRadiance0,
    SimpleDdgiNearFieldResidualGpuResource HistoryRadiance1,
    SimpleDdgiNearFieldResidualGpuResource HistoryMoments0,
    SimpleDdgiNearFieldResidualGpuResource HistoryMoments1,
    SimpleDdgiNearFieldResidualGpuResource HistoryValidity0,
    SimpleDdgiNearFieldResidualGpuResource HistoryValidity1,
    SimpleDdgiNearFieldResidualGpuResource HistoryMetadata0,
    SimpleDdgiNearFieldResidualGpuResource HistoryMetadata1,
    SimpleDdgiNearFieldResidualGpuResource HistoryNormal0,
    SimpleDdgiNearFieldResidualGpuResource HistoryNormal1,
    SimpleDdgiNearFieldResidualGpuResource FilterScratch0,
    SimpleDdgiNearFieldResidualGpuResource FilterScratch1,
    SimpleDdgiNearFieldResidualGpuResource TileBuffers,
    SimpleDdgiNearFieldResidualGpuResource TelemetryReadback0,
    SimpleDdgiNearFieldResidualGpuResource TelemetryReadback1,
    uint DescriptorCount)
{
    public SimpleDdgiNearFieldResidualGpuResource PreparedDepthFootprint
        { get; init; } = new(0UL, 0UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.PreparedDepthFootprint);

    public SimpleDdgiNearFieldResidualGpuResource PreparedReceiverPayload
        { get; init; } = new(0UL, 0UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.PreparedReceiverPayload);

    public SimpleDdgiNearFieldResidualGpuResource PreparedMotion
        { get; init; } = new(0UL, 0UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.PreparedMotion);

    public SimpleDdgiNearFieldResidualGpuResource SourceLuminance
        { get; init; } = new(0UL, 0UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.SourceLuminance);

    public SimpleDdgiNearFieldResidualGpuResource SurfaceTable
        { get; init; } = new(0UL, 0UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.SurfaceTable);

    public SimpleDdgiNearFieldResidualGpuResource ActiveTileAndIndirect
        { get; init; } = new(0UL, 0UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.ActiveTileAndIndirect);

    public void Validate(in SimpleDdgiNearFieldResidualLayout layout)
    {
        if (AllocationId == 0UL)
            throw new ArgumentException("C5 allocation ID must be nonzero.", nameof(AllocationId));
        if (!layout.IsValid || layout.TotalBytes == 0UL ||
            layout.ReceiverPayloadBytes == 0UL ||
            layout.TraceFrameConstantsBytes == 0UL ||
            layout.PreparedDepthFootprintBytes == 0UL ||
            layout.PreparedReceiverPayloadBytes == 0UL ||
            layout.PreparedMotionBytes == 0UL ||
            layout.SourceLuminanceBytes == 0UL ||
            layout.HistoryRadianceBytes == 0UL || layout.MomentBytes == 0UL ||
            layout.HistoryValidityBytes == 0UL || layout.HistoryMetadataBytes == 0UL ||
            layout.HistoryNormalBytes == 0UL ||
            layout.SurfaceTableBytes == 0UL ||
            layout.ActiveTileAndIndirectBytes == 0UL ||
            layout.TelemetryReadbackBytes == 0UL ||
            (layout.TraceFrameConstantsBytes & 1UL) != 0UL ||
            (layout.HistoryRadianceBytes & 1UL) != 0UL ||
            (layout.MomentBytes & 1UL) != 0UL ||
            (layout.HistoryValidityBytes & 1UL) != 0UL ||
            (layout.HistoryMetadataBytes & 1UL) != 0UL ||
            (layout.HistoryNormalBytes & 1UL) != 0UL ||
            (layout.TelemetryReadbackBytes & 1UL) != 0UL ||
            (layout.FilterScratchBytes & 1UL) != 0UL)
        {
            throw new ArgumentException("C5 allocation requires a complete layout.", nameof(layout));
        }

        ValidateResource(DirectDiffuseEmissiveSource, layout.TraceSourceBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.DirectDiffuseEmissiveSource,
            nameof(DirectDiffuseEmissiveSource));
        ValidateResource(ReceiverPayload, layout.ReceiverPayloadBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.ReceiverPayload,
            nameof(ReceiverPayload));
        ValidateResource(TraceFrameConstants0, layout.TraceFrameConstantsBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.TraceFrameConstants0,
            nameof(TraceFrameConstants0));
        ValidateResource(TraceFrameConstants1, layout.TraceFrameConstantsBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.TraceFrameConstants1,
            nameof(TraceFrameConstants1));
        ValidateResource(RawCandidate, layout.RawCandidateBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.RawCandidate,
            nameof(RawCandidate));
        ValidateResource(PreparedDepthFootprint, layout.PreparedDepthFootprintBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.PreparedDepthFootprint,
            nameof(PreparedDepthFootprint));
        ValidateResource(PreparedReceiverPayload, layout.PreparedReceiverPayloadBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.PreparedReceiverPayload,
            nameof(PreparedReceiverPayload));
        ValidateResource(PreparedMotion, layout.PreparedMotionBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.PreparedMotion,
            nameof(PreparedMotion));
        ValidateResource(SourceLuminance, layout.SourceLuminanceBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.SourceLuminance,
            nameof(SourceLuminance));
        ValidateResource(HitMetadata, layout.HitMetadataBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.HitMetadata,
            nameof(HitMetadata));
        ValidateResource(HistoryRadiance0, layout.HistoryRadianceBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryRadiance0,
            nameof(HistoryRadiance0));
        ValidateResource(HistoryRadiance1, layout.HistoryRadianceBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryRadiance1,
            nameof(HistoryRadiance1));
        ValidateResource(HistoryMoments0, layout.MomentBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMoments0,
            nameof(HistoryMoments0));
        ValidateResource(HistoryMoments1, layout.MomentBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMoments1,
            nameof(HistoryMoments1));
        ValidateResource(HistoryValidity0, layout.HistoryValidityBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryValidity0,
            nameof(HistoryValidity0));
        ValidateResource(HistoryValidity1, layout.HistoryValidityBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryValidity1,
            nameof(HistoryValidity1));
        ValidateResource(HistoryMetadata0, layout.HistoryMetadataBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMetadata0,
            nameof(HistoryMetadata0));
        ValidateResource(HistoryMetadata1, layout.HistoryMetadataBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMetadata1,
            nameof(HistoryMetadata1));
        ValidateResource(HistoryNormal0, layout.HistoryNormalBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryNormal0,
            nameof(HistoryNormal0));
        ValidateResource(HistoryNormal1, layout.HistoryNormalBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.HistoryNormal1,
            nameof(HistoryNormal1));
        ValidateResource(FilterScratch0, layout.FilterScratchBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.FilterScratch0,
            nameof(FilterScratch0));
        ValidateResource(FilterScratch1, layout.FilterScratchBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.FilterScratch1,
            nameof(FilterScratch1));
        ValidateResource(SurfaceTable, layout.SurfaceTableBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.SurfaceTable,
            nameof(SurfaceTable));
        ValidateResource(ActiveTileAndIndirect, layout.ActiveTileAndIndirectBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.ActiveTileAndIndirect,
            nameof(ActiveTileAndIndirect));
        ValidateResource(TileBuffers, layout.TileBuffersBytes,
            SimpleDdgiNearFieldResidualGpuResourceKind.TileBuffers,
            nameof(TileBuffers));
        ValidateResource(TelemetryReadback0, layout.TelemetryReadbackBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.TelemetryReadback0,
            nameof(TelemetryReadback0));
        ValidateResource(TelemetryReadback1, layout.TelemetryReadbackBytes / 2UL,
            SimpleDdgiNearFieldResidualGpuResourceKind.TelemetryReadback1,
            nameof(TelemetryReadback1));

        uint expectedDescriptorCount = ExpectedDescriptorCount(layout);
        if (DescriptorCount != expectedDescriptorCount)
        {
            throw new ArgumentException(
                $"C5 allocation exposes {DescriptorCount} owned descriptors; expected " +
                $"{expectedDescriptorCount}.", nameof(DescriptorCount));
        }

        var allocatedHandles = new HashSet<ulong>();
        foreach (SimpleDdgiNearFieldResidualGpuResource resource in EnumerateResources())
        {
            if (resource.Handle != 0UL && !allocatedHandles.Add(resource.Handle))
            {
                throw new ArgumentException(
                    "C5 resources must not alias across persistent or transient lifetimes.",
                    nameof(AllocationId));
            }
        }

        ulong actualBytes = checked(
            DirectDiffuseEmissiveSource.Bytes + ReceiverPayload.Bytes +
            TraceFrameConstants0.Bytes + TraceFrameConstants1.Bytes +
            PreparedDepthFootprint.Bytes + PreparedReceiverPayload.Bytes +
            PreparedMotion.Bytes + SourceLuminance.Bytes + RawCandidate.Bytes +
            HitMetadata.Bytes +
            HistoryRadiance0.Bytes + HistoryRadiance1.Bytes +
            HistoryMoments0.Bytes + HistoryMoments1.Bytes +
            HistoryValidity0.Bytes + HistoryValidity1.Bytes +
            HistoryMetadata0.Bytes + HistoryMetadata1.Bytes +
            HistoryNormal0.Bytes + HistoryNormal1.Bytes +
            FilterScratch0.Bytes + FilterScratch1.Bytes + SurfaceTable.Bytes +
            ActiveTileAndIndirect.Bytes + TileBuffers.Bytes +
            TelemetryReadback0.Bytes + TelemetryReadback1.Bytes);
        if (actualBytes != layout.TotalBytes)
        {
            throw new ArgumentException(
                $"C5 allocation owns {actualBytes} bytes; expected {layout.TotalBytes}.",
                nameof(layout));
        }
    }

    public static uint ExpectedDescriptorCount(in SimpleDdgiNearFieldResidualLayout layout) =>
        SimpleDdgiNearFieldResidualGpuAbi.BaseOwnedDescriptorCount +
        (layout.FilterScratchBytes == 0UL
            ? 0u
            : SimpleDdgiNearFieldResidualGpuAbi.FilterScratchDescriptorCount);

    private IEnumerable<SimpleDdgiNearFieldResidualGpuResource> EnumerateResources()
    {
        yield return DirectDiffuseEmissiveSource;
        yield return ReceiverPayload;
        yield return TraceFrameConstants0;
        yield return TraceFrameConstants1;
        yield return PreparedDepthFootprint;
        yield return PreparedReceiverPayload;
        yield return PreparedMotion;
        yield return SourceLuminance;
        yield return RawCandidate;
        yield return HitMetadata;
        yield return HistoryRadiance0;
        yield return HistoryRadiance1;
        yield return HistoryMoments0;
        yield return HistoryMoments1;
        yield return HistoryValidity0;
        yield return HistoryValidity1;
        yield return HistoryMetadata0;
        yield return HistoryMetadata1;
        yield return HistoryNormal0;
        yield return HistoryNormal1;
        yield return FilterScratch0;
        yield return FilterScratch1;
        yield return SurfaceTable;
        yield return ActiveTileAndIndirect;
        yield return TileBuffers;
        yield return TelemetryReadback0;
        yield return TelemetryReadback1;
    }

    private static void ValidateResource(
        in SimpleDdgiNearFieldResidualGpuResource resource,
        ulong expectedBytes,
        SimpleDdgiNearFieldResidualGpuResourceKind expectedKind,
        string parameterName)
    {
        if (resource.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"C5 resource kind is {resource.Kind}; expected {expectedKind}.", parameterName);
        }
        if (expectedBytes == 0UL)
        {
            if (resource.Handle != 0UL || resource.Bytes != 0UL)
                throw new ArgumentException("Unexpected zero-byte C5 resource.", parameterName);
            return;
        }
        if (!resource.IsAllocated || resource.Bytes != expectedBytes)
        {
            throw new ArgumentException(
                $"C5 resource must be allocated with exactly {expectedBytes} bytes.", parameterName);
        }
    }
}

/// <summary>
/// Native resource ownership boundary.  Implementations must retire descriptor
/// references only after all command buffers that can reference an allocation
/// have completed.  If <see cref="Allocate"/> throws, it must clean up any
/// partial native allocations before rethrowing.
/// </summary>
public interface ISimpleDdgiNearFieldResidualGpuResourceAllocator
{
    SimpleDdgiNearFieldResidualGpuAllocation Allocate(
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualGpuConfiguration configuration);

    void Retire(SimpleDdgiNearFieldResidualGpuAllocation allocation);
}

public enum SimpleDdgiNearFieldResidualGpuResourceState : byte
{
    Disabled = 0,
    AllocatedHistoryInvalid = 1,
    Tracing = 2,
    TraceReadyForTemporal = 3,
    TemporalReadyForFilter = 4,
    ReadyForComposite = 5,
    CompositeComplete = 6,
    ReadyForFrequencySeparation = 7
}

/// <summary>
/// Global revision identity for C5 history.  Per-pixel depth/normal/object/
/// material comparisons remain in the temporal shader; this record prevents
/// an entire history image from surviving a semantic source or mode change.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuHistoryRevision(
    uint ViewportRevision,
    uint HiZRevision,
    uint TraceSourceAbiRevision,
    uint EffectiveModeRevision,
    uint ExposureDomainRevision,
    bool CameraCut,
    uint StructuralProjectionRevision = 0u,
    uint OriginRebaseRevision = 0u,
    uint SceneGeneration = 0u,
    uint TraceSourceContentRevision = 0u,
    uint NearFieldLayoutRevision = 0u,
    uint B3OwnershipRevision = 0u,
    uint TraceSourceLayoutRevision = 0u)
{
    public bool Matches(in SimpleDdgiNearFieldResidualGpuHistoryRevision other) =>
        !CameraCut && !other.CameraCut &&
        ViewportRevision == other.ViewportRevision &&
        HiZRevision == other.HiZRevision &&
        TraceSourceAbiRevision == other.TraceSourceAbiRevision &&
        EffectiveModeRevision == other.EffectiveModeRevision &&
        ExposureDomainRevision == other.ExposureDomainRevision &&
        StructuralProjectionRevision == other.StructuralProjectionRevision &&
        OriginRebaseRevision == other.OriginRebaseRevision &&
        SceneGeneration == other.SceneGeneration &&
        TraceSourceContentRevision == other.TraceSourceContentRevision &&
        TraceSourceLayoutRevision == other.TraceSourceLayoutRevision &&
        NearFieldLayoutRevision == other.NearFieldLayoutRevision &&
        B3OwnershipRevision == other.B3OwnershipRevision;

    public SimpleDdgiNearFieldResidualGpuHistoryRevision WithoutCameraCut() =>
        new(ViewportRevision, HiZRevision, TraceSourceAbiRevision,
            EffectiveModeRevision, ExposureDomainRevision, false,
            StructuralProjectionRevision, OriginRebaseRevision, SceneGeneration,
            TraceSourceContentRevision, NearFieldLayoutRevision,
            B3OwnershipRevision, TraceSourceLayoutRevision);
}

public readonly record struct SimpleDdgiNearFieldResidualGpuFrameToken(
    ulong AllocationEpoch,
    ulong FrameEpoch,
    uint HistoryEpoch,
    int HistoryReadIndex,
    int HistoryWriteIndex,
    uint AbiVersion)
{
    public bool IsDefault => AllocationEpoch == 0UL || FrameEpoch == 0UL;
}

public readonly record struct SimpleDdgiNearFieldResidualGpuBeginFrameResult(
    bool Started,
    SimpleDdgiNearFieldResidualGpuFrameToken Token,
    bool HistoryInvalidated,
    string Reason);

/// <summary>
/// Compact C5 trace completion witness.  It is supplied by the eventual
/// renderer pass/readback boundary; the lifecycle manager refuses to advance
/// if source ownership or zero-on-miss semantics have not been witnessed.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuTraceCompletion(
    bool QueueOrderedCommandsRecorded,
    bool TraceSourceBindingVerified,
    bool StableSampleIdentityVerified,
    bool ReceiverBrdfAndPdfVerified,
    bool InvalidAndMissCandidatesZeroed,
    bool TileRecordsInitializedAndBounded);

public readonly record struct SimpleDdgiNearFieldResidualGpuTemporalCompletion(
    bool QueueOrderedCommandsRecorded,
    bool HistoryWritesContainOnlyValidCandidates,
    bool HistoryBankFullyInitialized);

public readonly record struct SimpleDdgiNearFieldResidualGpuFilterCompletion(
    bool QueueOrderedCommandsRecorded,
    bool EdgeAwareValidityChecked,
    uint ExecutedIterationCount);

public readonly record struct
    SimpleDdgiNearFieldResidualGpuFrequencySeparationCompletion(
        bool QueueOrderedCommandsRecorded,
        bool B3FootprintSupportValidated,
        bool PerIdentityConfidenceWeightedMeanRemoved,
        bool InvalidResidualPayloadWasZero);

public readonly record struct SimpleDdgiNearFieldResidualGpuCompositeCompletion(
    bool QueueOrderedCommandsRecorded,
    bool OnlyValidSignedResidualComposited,
    bool InvalidResidualPayloadWasZero);

public readonly record struct SimpleDdgiNearFieldResidualGpuStageResult(
    bool Accepted,
    string Reason)
{
    public static SimpleDdgiNearFieldResidualGpuStageResult Success(string reason) =>
        new(true, reason);
}

/// <summary>Graph-planning and diagnostics snapshot for the C5 lifecycle.</summary>
public readonly record struct SimpleDdgiNearFieldResidualGpuRuntimeSnapshot(
    SimpleDdgiNearFieldResidualGpuResourceState State,
    bool IsEffectivelyEnabled,
    bool IntegrationPrerequisitesDeclared,
    bool HistoryValid,
    ulong AllocationEpoch,
    ulong FrameEpoch,
    ulong LastFenceCompletedFrameEpoch,
    uint HistoryEpoch,
    int HistoryReadIndex,
    int HistoryWriteIndex,
    ulong AllocatedBytes,
    uint OwnedDescriptorCount,
    string Reason)
{
    /// <summary>
    /// This only describes a declared integration contract.  It is not GPU
    /// conformance evidence and must not be presented as an active C5 feature
    /// until the renderer owns the actual pass/descriptor/command wiring.
    /// </summary>
    public bool IsContractReadyForRendererIntegration => IsEffectivelyEnabled &&
        IntegrationPrerequisitesDeclared && AllocatedBytes != 0UL;
}

/// <summary>
/// Transactional resource and history lifecycle for the optional C5 GPU path.
/// It deliberately contains no Vulkan dispatches: the renderer must pass its
/// explicit pass integration through the capabilities gate before this manager
/// allocates anything.  That keeps the unintegrated shader contract fail-closed
/// instead of treating an allocated image as a working GI feature.
/// </summary>
public sealed class SimpleDdgiNearFieldResidualGpuManager : IDisposable
{
    private readonly object _sync = new();
    private ISimpleDdgiNearFieldResidualGpuResourceAllocator? _allocator;
    private SimpleDdgiNearFieldResidualGpuAllocation? _allocation;
    private SimpleDdgiNearFieldResidualLayout _layout;
    private SimpleDdgiNearFieldResidualGpuConfiguration _configuration;
    private SimpleDdgiNearFieldResidualGpuIntegrationCapabilities _integration;
    private SimpleDdgiNearFieldResidualGpuResourceState _state;
    private ulong _allocationEpoch;
    private ulong _frameEpoch;
    private ulong _lastFenceCompletedFrameEpoch;
    private uint _historyEpoch;
    private int _historyReadIndex;
    private int _historyWriteIndex = 1;
    private bool _historyValid;
    private bool _currentFrameHasPublishableHistoryBank;
    private bool _hasHistoryRevision;
    private SimpleDdgiNearFieldResidualGpuHistoryRevision _historyRevision;
    private SimpleDdgiNearFieldResidualGpuFrameToken? _pendingFrame;
    private string _reason = "disabled";
    private bool _disposed;

    public SimpleDdgiNearFieldResidualGpuManager()
    {
        SimpleDdgiNearFieldResidualGpuAbi.VerifyManagedLayout();
    }

    public SimpleDdgiNearFieldResidualGpuRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Allocates C5 only for an already-effective mode with a complete layout,
    /// an allowed trace source, and every declared renderer integration
    /// prerequisite.  Any rejection retires existing C5 resources so stale
    /// allocations cannot masquerade as an enabled experiment.
    /// </summary>
    public SimpleDdgiNearFieldResidualGpuRuntimeSnapshot Reconcile(
        in SimpleDdgiNearFieldResidualGpuRuntimeRequest request,
        ISimpleDdgiNearFieldResidualGpuResourceAllocator? allocator)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!request.IsEffectivelyEnabled)
            {
                DisableNoLock("effective-mode-disabled");
                return CreateSnapshotNoLock();
            }
            if (!TryValidateActiveRequest(request, out string validationFailure))
            {
                DisableNoLock(validationFailure);
                return CreateSnapshotNoLock();
            }
            if (allocator is null)
            {
                DisableNoLock("near-field-resource-allocator-unavailable");
                return CreateSnapshotNoLock();
            }
            if (_allocation is not null && _layout.Equals(request.Layout) &&
                _configuration.Equals(request.Configuration) &&
                _integration.Equals(request.Integration))
            {
                return CreateSnapshotNoLock();
            }

            SimpleDdgiNearFieldResidualGpuAllocation? replacement = null;
            try
            {
                replacement = allocator.Allocate(request.Layout, request.Configuration) ??
                    throw new InvalidOperationException("C5 allocator returned a null allocation.");
                replacement.Validate(request.Layout);
            }
            catch (Exception exception)
            {
                if (replacement is not null)
                    allocator.Retire(replacement);
                DisableNoLock("near-field-allocation-rejected:" + exception.GetType().Name);
                return CreateSnapshotNoLock();
            }

            try
            {
                RetireActiveNoLock();
            }
            catch
            {
                allocator.Retire(replacement);
                ClearNoLock("near-field-prior-allocation-retirement-failed");
                throw;
            }

            _allocator = allocator;
            _allocation = replacement;
            _layout = request.Layout;
            _configuration = request.Configuration;
            _integration = request.Integration;
            _allocationEpoch = NextNonZero(_allocationEpoch);
            _frameEpoch = 0UL;
            _lastFenceCompletedFrameEpoch = 0UL;
            _historyEpoch = 1u;
            _historyReadIndex = 0;
            _historyWriteIndex = 1;
            _historyValid = false;
            _currentFrameHasPublishableHistoryBank = false;
            _hasHistoryRevision = false;
            _pendingFrame = null;
            _state = SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid;
            _reason = "allocated-history-invalid";
            return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Starts one ordered C5 frame.  A global revision change or camera cut
    /// explicitly invalidates history before trace work can read it.
    /// </summary>
    public SimpleDdgiNearFieldResidualGpuBeginFrameResult BeginFrame(
        in SimpleDdgiNearFieldResidualGpuHistoryRevision revision) =>
        BeginFrame(revision, requiredHistoryWriteIndex: -1);

    /// <summary>
    /// Starts a frame while pinning the physical write bank to the render
    /// graph's current-history selection (frame-index parity). A skipped frame
    /// or mismatched initial parity invalidates history and realigns both
    /// banks; it never samples a bank different from the graph barrier plan.
    /// </summary>
    public SimpleDdgiNearFieldResidualGpuBeginFrameResult BeginFrame(
        in SimpleDdgiNearFieldResidualGpuHistoryRevision revision,
        int requiredHistoryWriteIndex)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (requiredHistoryWriteIndex is < -1 or > 1)
            {
                return new(false, default, false,
                    "near-field-history-write-index-out-of-range");
            }
            if (_allocation is null || _state == SimpleDdgiNearFieldResidualGpuResourceState.Disabled)
                return new(false, default, false, "near-field-not-effectively-enabled");
            if (_pendingFrame.HasValue || _state is not (
                    SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid or
                    SimpleDdgiNearFieldResidualGpuResourceState.CompositeComplete))
            {
                return new(false, default, false, "near-field-frame-already-in-flight");
            }
            if (revision.TraceSourceAbiRevision !=
                _configuration.TraceSourceContract.AbiRevision)
            {
                return new(false, default, false,
                    "near-field-frame-trace-source-abi-revision-mismatch");
            }
            if (revision.TraceSourceLayoutRevision !=
                _configuration.TraceSourceContract.LayoutRevision)
            {
                return new(false, default, false,
                    "near-field-frame-trace-source-layout-revision-mismatch");
            }
            if (revision.TraceSourceContentRevision !=
                _configuration.TraceSourceContract.SourceRevision)
            {
                return new(false, default, false,
                    "near-field-frame-trace-source-content-revision-mismatch");
            }

            bool bankParityMismatch = requiredHistoryWriteIndex >= 0 &&
                _historyWriteIndex != requiredHistoryWriteIndex;
            bool historyInvalidated = !_historyValid || !_hasHistoryRevision ||
                revision.CameraCut || !revision.Matches(_historyRevision) ||
                bankParityMismatch;
            if (historyInvalidated)
            {
                InvalidateHistoryNoLock("near-field-history-revision-changed");
                if (requiredHistoryWriteIndex >= 0)
                {
                    _historyWriteIndex = requiredHistoryWriteIndex;
                    _historyReadIndex = 1 - requiredHistoryWriteIndex;
                }
            }

            _frameEpoch = NextNonZero(_frameEpoch);
            var token = new SimpleDdgiNearFieldResidualGpuFrameToken(
                _allocationEpoch,
                _frameEpoch,
                _historyEpoch,
                _historyReadIndex,
                _historyWriteIndex,
                SimpleDdgiNearFieldResidualGpuAbi.Version);
            _pendingFrame = token;
            _historyRevision = revision.WithoutCameraCut();
            _hasHistoryRevision = true;
            _currentFrameHasPublishableHistoryBank = false;
            _state = SimpleDdgiNearFieldResidualGpuResourceState.Tracing;
            _reason = historyInvalidated ? "tracing-history-invalidated" : "tracing-history-reusable";
            return new(true, token, historyInvalidated, "started");
        }
    }

    public SimpleDdgiNearFieldResidualGpuStageResult CompleteTrace(
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualGpuTraceCompletion completion)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!MatchesCurrentTokenNoLock(token) ||
                _state != SimpleDdgiNearFieldResidualGpuResourceState.Tracing)
            {
                return new(false, "near-field-trace-token-or-state-mismatch");
            }
            if (!completion.QueueOrderedCommandsRecorded)
                return AbortCurrentFrameNoLock("near-field-trace-command-recording-incomplete");
            if (!completion.TraceSourceBindingVerified)
                return AbortCurrentFrameNoLock("near-field-trace-source-ownership-unverified");
            if (!completion.StableSampleIdentityVerified)
                return AbortCurrentFrameNoLock("near-field-stable-sample-identity-unverified");
            if (!completion.ReceiverBrdfAndPdfVerified)
                return AbortCurrentFrameNoLock("near-field-receiver-brdf-pdf-unverified");
            if (!completion.InvalidAndMissCandidatesZeroed)
                return AbortCurrentFrameNoLock("near-field-invalid-or-miss-residual-not-zeroed");
            if (!completion.TileRecordsInitializedAndBounded)
                return AbortCurrentFrameNoLock("near-field-tile-records-uninitialized-or-unbounded");

            _state = SimpleDdgiNearFieldResidualGpuResourceState.TraceReadyForTemporal;
            _reason = "trace-recorded";
            return SimpleDdgiNearFieldResidualGpuStageResult.Success("trace-recorded");
        }
    }

    public SimpleDdgiNearFieldResidualGpuStageResult CompleteTemporal(
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualGpuTemporalCompletion completion)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!MatchesCurrentTokenNoLock(token) ||
                _state != SimpleDdgiNearFieldResidualGpuResourceState.TraceReadyForTemporal)
            {
                return new(false, "near-field-temporal-token-or-state-mismatch");
            }
            if (!completion.QueueOrderedCommandsRecorded)
                return AbortCurrentFrameNoLock("near-field-temporal-command-recording-incomplete");
            if (!completion.HistoryWritesContainOnlyValidCandidates)
                return AbortCurrentFrameNoLock("near-field-temporal-invalid-candidate-history-write");
            if (!completion.HistoryBankFullyInitialized)
                return AbortCurrentFrameNoLock(
                    "near-field-temporal-history-bank-not-fully-initialized");

            // A frame containing zero valid pixels is still a coherent bank:
            // reset/temporal wrote every validity texel and later sampling is
            // governed exclusively by that per-pixel value. CPU-side counts
            // are fence-delayed telemetry and never publication authority.
            _currentFrameHasPublishableHistoryBank = true;
            _state = _configuration.FilterIterationCount == 0
                ? SimpleDdgiNearFieldResidualGpuResourceState
                    .ReadyForFrequencySeparation
                : SimpleDdgiNearFieldResidualGpuResourceState.TemporalReadyForFilter;
            _reason = "temporal-recorded-coherent-history-bank";
            return SimpleDdgiNearFieldResidualGpuStageResult.Success(_reason);
        }
    }

    public SimpleDdgiNearFieldResidualGpuStageResult CompleteFilter(
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualGpuFilterCompletion completion)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!MatchesCurrentTokenNoLock(token) ||
                _state != SimpleDdgiNearFieldResidualGpuResourceState.TemporalReadyForFilter)
            {
                return new(false, "near-field-filter-token-or-state-mismatch");
            }
            if (!completion.QueueOrderedCommandsRecorded)
                return AbortCurrentFrameNoLock("near-field-filter-command-recording-incomplete");
            if (!completion.EdgeAwareValidityChecked)
                return AbortCurrentFrameNoLock("near-field-filter-validity-edge-check-missing");
            if (completion.ExecutedIterationCount != (uint)_configuration.FilterIterationCount)
                return AbortCurrentFrameNoLock("near-field-filter-iteration-count-mismatch");

            _state = SimpleDdgiNearFieldResidualGpuResourceState
                .ReadyForFrequencySeparation;
            _reason = "filter-recorded";
            return SimpleDdgiNearFieldResidualGpuStageResult.Success("filter-recorded");
        }
    }

    public SimpleDdgiNearFieldResidualGpuStageResult
        CompleteFrequencySeparation(
            in SimpleDdgiNearFieldResidualGpuFrameToken token,
            in SimpleDdgiNearFieldResidualGpuFrequencySeparationCompletion
                completion)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!MatchesCurrentTokenNoLock(token) ||
                _state != SimpleDdgiNearFieldResidualGpuResourceState
                    .ReadyForFrequencySeparation)
            {
                return new(false,
                    "near-field-frequency-token-or-state-mismatch");
            }
            if (!completion.QueueOrderedCommandsRecorded)
            {
                return AbortCurrentFrameNoLock(
                    "near-field-frequency-command-recording-incomplete");
            }
            if (!completion.B3FootprintSupportValidated)
            {
                return AbortCurrentFrameNoLock(
                    "near-field-frequency-B3-support-unvalidated");
            }
            if (!completion.PerIdentityConfidenceWeightedMeanRemoved)
            {
                return AbortCurrentFrameNoLock(
                    "near-field-frequency-identity-mean-not-removed");
            }
            if (!completion.InvalidResidualPayloadWasZero)
            {
                return AbortCurrentFrameNoLock(
                    "near-field-frequency-invalid-residual-not-zero");
            }

            _state = SimpleDdgiNearFieldResidualGpuResourceState
                .ReadyForComposite;
            _reason = "frequency-separation-recorded";
            return SimpleDdgiNearFieldResidualGpuStageResult.Success(_reason);
        }
    }

    public SimpleDdgiNearFieldResidualGpuStageResult CompleteComposite(
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualGpuCompositeCompletion completion)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!MatchesCurrentTokenNoLock(token) ||
                _state != SimpleDdgiNearFieldResidualGpuResourceState.ReadyForComposite)
            {
                return new(false, "near-field-composite-token-or-state-mismatch");
            }
            if (!completion.QueueOrderedCommandsRecorded)
                return AbortCurrentFrameNoLock("near-field-composite-command-recording-incomplete");
            if (!completion.OnlyValidSignedResidualComposited)
                return AbortCurrentFrameNoLock("near-field-composite-invalid-signed-residual-contract");
            if (!completion.InvalidResidualPayloadWasZero)
                return AbortCurrentFrameNoLock("near-field-composite-invalid-residual-not-zero");

            if (_currentFrameHasPublishableHistoryBank)
            {
                _historyReadIndex = token.HistoryWriteIndex;
                _historyWriteIndex = token.HistoryReadIndex;
                _historyValid = true;
            }
            else
            {
                _historyValid = false;
            }
            _pendingFrame = null;
            _state = SimpleDdgiNearFieldResidualGpuResourceState.CompositeComplete;
            _reason = _historyValid
                ? "composite-recorded-history-queue-ordered"
                : "composite-recorded-no-history-publication";
            return SimpleDdgiNearFieldResidualGpuStageResult.Success(_reason);
        }
    }

    /// <summary>
    /// Observes completion only after the renderer has waited the matching
    /// frame-slot fence. Command recording and queue ordering deliberately do
    /// not call this method. Older ring slots may complete after a newer frame
    /// was recorded, so tokens are accepted monotonically within the current
    /// allocation rather than requiring the current recording token.
    /// </summary>
    public bool ObserveFrameFenceCompletion(
        in SimpleDdgiNearFieldResidualGpuFrameToken token)
    {
        lock (_sync)
        {
            if (_disposed || _allocation is null ||
                token.AllocationEpoch != _allocationEpoch ||
                token.AbiVersion != SimpleDdgiNearFieldResidualGpuAbi.Version ||
                token.FrameEpoch == 0UL || token.FrameEpoch > _frameEpoch ||
                token.FrameEpoch <= _lastFenceCompletedFrameEpoch)
            {
                return false;
            }

            _lastFenceCompletedFrameEpoch = token.FrameEpoch;
            _reason = "frame-fence-complete";
            return true;
        }
    }

    /// <summary>Explicitly invalidates history and rejects a matching in-flight frame.</summary>
    public bool InvalidateHistory(
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        string reason = "near-field-explicit-history-invalidation")
    {
        lock (_sync)
        {
            if (_disposed || !MatchesCurrentTokenNoLock(token))
                return false;
            _pendingFrame = null;
            InvalidateHistoryNoLock(reason);
            _state = SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid;
            return true;
        }
    }

    public void Disable(string reason = "disabled")
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DisableNoLock(reason);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DisableNoLock("disposed");
            _disposed = true;
        }
    }

    private static bool TryValidateActiveRequest(
        in SimpleDdgiNearFieldResidualGpuRuntimeRequest request,
        out string failure)
    {
        if (!request.Integration.IsReady)
        {
            failure = request.Integration.FailureReason;
            return false;
        }
        if (!IsCompleteLayout(request.Layout))
        {
            failure = "near-field-complete-layout-required";
            return false;
        }
        SimpleDdgiNearFieldResidualGpuConfigurationValidation configuration =
            request.Configuration.Validate(request.Layout);
        if (!configuration.IsValid)
        {
            failure = configuration.Reason;
            return false;
        }
        failure = "valid";
        return true;
    }

    private static bool IsCompleteLayout(in SimpleDdgiNearFieldResidualLayout layout)
    {
        if (!layout.IsValid || layout.SourceWidth <= 0 || layout.SourceHeight <= 0 ||
            layout.TraceWidth <= 0 || layout.TraceHeight <= 0 || layout.TotalBytes == 0UL ||
            layout.TraceSourceBytes == 0UL || layout.RawCandidateBytes == 0UL ||
            layout.PreparedDepthFootprintBytes == 0UL ||
            layout.PreparedReceiverPayloadBytes == 0UL ||
            layout.PreparedMotionBytes == 0UL ||
            layout.HitMetadataBytes != 0UL || layout.HistoryRadianceBytes == 0UL ||
            layout.MomentBytes == 0UL || layout.HistoryValidityBytes == 0UL ||
            layout.HistoryMetadataBytes == 0UL || layout.HistoryNormalBytes == 0UL ||
            layout.TileBuffersBytes == 0UL || layout.TelemetryReadbackBytes == 0UL)
        {
            return false;
        }
        if (layout.FilterIterationCount == 0)
            return layout.FilterScratchBytes == 0UL;
        return layout.FilterScratchBytes != 0UL;
    }

    private bool MatchesCurrentTokenNoLock(
        in SimpleDdgiNearFieldResidualGpuFrameToken token) =>
        _pendingFrame.HasValue && _pendingFrame.Value.Equals(token) &&
        token.AllocationEpoch == _allocationEpoch &&
        token.AbiVersion == SimpleDdgiNearFieldResidualGpuAbi.Version;

    private SimpleDdgiNearFieldResidualGpuStageResult AbortCurrentFrameNoLock(string reason)
    {
        _pendingFrame = null;
        InvalidateHistoryNoLock(reason);
        _state = SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid;
        return new(false, reason);
    }

    private void InvalidateHistoryNoLock(string reason)
    {
        _historyValid = false;
        _currentFrameHasPublishableHistoryBank = false;
        _historyEpoch = NextNonZero(_historyEpoch);
        _historyReadIndex = 0;
        _historyWriteIndex = 1;
        _reason = reason;
    }

    private void DisableNoLock(string reason)
    {
        try
        {
            RetireActiveNoLock();
        }
        finally
        {
            ClearNoLock(reason);
        }
    }

    private void RetireActiveNoLock()
    {
        if (_allocation is null)
            return;

        ISimpleDdgiNearFieldResidualGpuResourceAllocator? allocator = _allocator;
        SimpleDdgiNearFieldResidualGpuAllocation allocation = _allocation;
        _allocation = null;
        _allocator = null;
        if (allocator is not null)
            allocator.Retire(allocation);
    }

    private void ClearNoLock(string reason)
    {
        _layout = default;
        _configuration = default;
        _integration = default;
        _state = SimpleDdgiNearFieldResidualGpuResourceState.Disabled;
        _frameEpoch = 0UL;
        _lastFenceCompletedFrameEpoch = 0UL;
        _historyEpoch = 0u;
        _historyReadIndex = 0;
        _historyWriteIndex = 1;
        _historyValid = false;
        _currentFrameHasPublishableHistoryBank = false;
        _hasHistoryRevision = false;
        _historyRevision = default;
        _pendingFrame = null;
        _reason = reason;
    }

    private SimpleDdgiNearFieldResidualGpuRuntimeSnapshot CreateSnapshotNoLock()
    {
        bool enabled = _allocation is not null &&
            _state != SimpleDdgiNearFieldResidualGpuResourceState.Disabled;
        return new SimpleDdgiNearFieldResidualGpuRuntimeSnapshot(
            _state,
            enabled,
            enabled && _integration.IsReady,
            enabled && _historyValid,
            _allocationEpoch,
            _frameEpoch,
            _lastFenceCompletedFrameEpoch,
            _historyEpoch,
            _historyReadIndex,
            _historyWriteIndex,
            enabled ? _layout.TotalBytes : 0UL,
            enabled ? _allocation!.DescriptorCount : 0u,
            _reason);
    }

    private static uint NextNonZero(uint value)
    {
        uint next = unchecked(value + 1u);
        return next == 0u ? 1u : next;
    }

    private static ulong NextNonZero(ulong value)
    {
        ulong next = unchecked(value + 1UL);
        return next == 0UL ? 1UL : next;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiNearFieldResidualGpuManager));
    }
}
