using System;
using System.Numerics;

namespace Njulf.Rendering.Resources;

[Flags]
public enum SimpleDdgiNearFieldTraceSourceTerm : uint
{
    None = 0,
    DirectDiffuse = 1u << 0,
    Emissive = 1u << 1,
    DdgiIndirect = 1u << 2,
    DirectionalRadianceReflection = 1u << 3,
    ScreenSpaceReflection = 1u << 4,
    ReflectionProbe = 1u << 5,
    EnvironmentIbl = 1u << 6,
    Caustic = 1u << 7,
    NearFieldHistory = 1u << 8,
    FogOrTransparency = 1u << 9,
    DisplayTransform = 1u << 10
}

/// <summary>
/// Explicit colour-space semantics of the sole C5 trace source.  C5 may only
/// inspect scene-linear radiance before DDGI, C4, IBL, fog, transparency, or a
/// display transform have contributed to it.
/// </summary>
public enum SimpleDdgiNearFieldTraceSourceColorSpace : byte
{
    Unspecified = 0,
    SceneLinearRadiance = 1,
    DisplayEncoded = 2,
    PostProcessOrUnknown = 3
}

/// <summary>
/// Provenance of the C5 trace-source attachment.  The accepted producer is a
/// distinct pre-DDGI direct-diffuse-plus-emissive render variant, rather than
/// an attachment inferred by name or an arbitrary scene-colour image.
/// </summary>
public enum SimpleDdgiNearFieldTraceSourceProducer : byte
{
    Unspecified = 0,
    PreDdgiDirectDiffuseAndEmissive = 1,
    FinalSceneColor = 2,
    DdgiOrIndirectComposite = 3,
    PostProcessOrUnknown = 4
}

/// <summary>
/// Coverage policy of a trace-source pixel.  Alpha-tested geometry may be
/// represented only after its deterministic opaque/masked coverage has been
/// resolved by the producer.  Blended, fog, transmission, or unresolved
/// per-sample alpha are never a valid C5 source.
/// </summary>
public enum SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics : byte
{
    Unspecified = 0,
    OpaqueAndMaskedCoverageResolved = 1,
    UnresolvedAlphaOrCoverage = 2,
    IncludesBlendedFogOrTransmission = 3
}

/// <summary>
/// Immutable full-resolution and scaled trace extent.  The scale and both
/// extents are carried explicitly so a descriptor cannot silently retain a
/// compatible byte size while its sampling footprint has changed.
/// </summary>
public readonly record struct SimpleDdgiNearFieldTraceSourceScaledExtent(
    int FullWidth,
    int FullHeight,
    int ScaledWidth,
    int ScaledHeight,
    float ResolutionScale)
{
    public bool TryValidate(out string failure)
    {
        if (FullWidth is < 1 or > 16_384 ||
            FullHeight is < 1 or > 16_384 ||
            ScaledWidth is < 1 or > 16_384 ||
            ScaledHeight is < 1 or > 16_384)
        {
            failure = "trace-source-scaled-extent-out-of-range";
            return false;
        }
        if (!float.IsFinite(ResolutionScale) ||
            ResolutionScale < 0.125f || ResolutionScale > 1.0f)
        {
            failure = "trace-source-resolution-scale-invalid";
            return false;
        }

        // Match the layout compiler's float multiplication and ceil rule
        // exactly; a mathematically equivalent double calculation can differ
        // by one pixel near a representable boundary.
        int expectedScaledWidth = Math.Max(1, checked((int)Math.Ceiling(
            FullWidth * ResolutionScale)));
        int expectedScaledHeight = Math.Max(1, checked((int)Math.Ceiling(
            FullHeight * ResolutionScale)));
        if (ScaledWidth != expectedScaledWidth ||
            ScaledHeight != expectedScaledHeight)
        {
            failure = "trace-source-scaled-extent-does-not-match-resolution-scale";
            return false;
        }

        failure = "valid";
        return true;
    }

    public bool Matches(in SimpleDdgiNearFieldResidualLayout layout) =>
        FullWidth == layout.SourceWidth &&
        FullHeight == layout.SourceHeight &&
        ScaledWidth == layout.TraceWidth &&
        ScaledHeight == layout.TraceHeight;
}

/// <summary>
/// Frozen C5 source-layout and producer contract.  Every property names an
/// observable attachment fact; an incomplete legacy terms/ABI declaration is
/// intentionally invalid and therefore cannot allocate C5 resources.
/// </summary>
public readonly record struct SimpleDdgiNearFieldTraceSourceContract
{
    public const SimpleDdgiNearFieldTraceSourceTerm RequiredTerms =
        SimpleDdgiNearFieldTraceSourceTerm.DirectDiffuse |
        SimpleDdgiNearFieldTraceSourceTerm.Emissive;

    public SimpleDdgiNearFieldTraceSourceTerm Terms { get; init; }
    public uint AbiRevision { get; init; }
    public SimpleDdgiNearFieldResidualFormat Format { get; init; }
    public SimpleDdgiNearFieldTraceSourceScaledExtent Extent { get; init; }
    public SimpleDdgiNearFieldTraceSourceColorSpace ColorSpace { get; init; }
    public SimpleDdgiNearFieldTraceSourceProducer Producer { get; init; }
    public SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics AlphaCoverage { get; init; }
    public uint LayoutRevision { get; init; }
    public uint SourceRevision { get; init; }

    /// <summary>
    /// Compatibility constructor for legacy callers.  It deliberately leaves
    /// layout/provenance facts unspecified, which makes the resulting contract
    /// fail closed until a real source attachment contract is supplied.
    /// </summary>
    public SimpleDdgiNearFieldTraceSourceContract(
        SimpleDdgiNearFieldTraceSourceTerm Terms,
        uint AbiRevision)
        : this(
            Terms,
            AbiRevision,
            default,
            default,
            SimpleDdgiNearFieldTraceSourceColorSpace.Unspecified,
            SimpleDdgiNearFieldTraceSourceProducer.Unspecified,
            SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics.Unspecified,
            0u,
            0u)
    {
    }

    public SimpleDdgiNearFieldTraceSourceContract(
        SimpleDdgiNearFieldTraceSourceTerm Terms,
        uint AbiRevision,
        SimpleDdgiNearFieldResidualFormat Format,
        SimpleDdgiNearFieldTraceSourceScaledExtent Extent,
        SimpleDdgiNearFieldTraceSourceColorSpace ColorSpace,
        SimpleDdgiNearFieldTraceSourceProducer Producer,
        SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics AlphaCoverage,
        uint LayoutRevision,
        uint SourceRevision)
    {
        this.Terms = Terms;
        this.AbiRevision = AbiRevision;
        this.Format = Format;
        this.Extent = Extent;
        this.ColorSpace = ColorSpace;
        this.Producer = Producer;
        this.AlphaCoverage = AlphaCoverage;
        this.LayoutRevision = LayoutRevision;
        this.SourceRevision = SourceRevision;
    }

    public static SimpleDdgiNearFieldTraceSourceContract
        CreatePreDdgiDirectDiffuseAndEmissive(
            in SimpleDdgiNearFieldResidualLayout layout,
            in SimpleDdgiNearFieldResidualProfile profile,
            uint abiRevision = 1u,
            uint layoutRevision = 1u,
            uint sourceRevision = 1u) => new(
                RequiredTerms,
                abiRevision,
                profile.SourceFormat,
                new SimpleDdgiNearFieldTraceSourceScaledExtent(
                    layout.SourceWidth,
                    layout.SourceHeight,
                    layout.TraceWidth,
                    layout.TraceHeight,
                    profile.ResolutionScale),
                SimpleDdgiNearFieldTraceSourceColorSpace.SceneLinearRadiance,
                SimpleDdgiNearFieldTraceSourceProducer
                    .PreDdgiDirectDiffuseAndEmissive,
                SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics
                    .OpaqueAndMaskedCoverageResolved,
                layoutRevision,
                sourceRevision);

    public bool IsValid => TryValidate(out _);

    public string FailureReason => TryValidate(out string failure)
        ? "valid"
        : failure;

    public bool TryValidate(out string failure)
    {
        if (Terms != RequiredTerms)
        {
            failure = "trace-source-must-contain-only-direct-diffuse-and-emissive";
            return false;
        }
        if (AbiRevision == 0u)
        {
            failure = "trace-source-abi-revision-required";
            return false;
        }
        if (Format != SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat &&
            Format != SimpleDdgiNearFieldResidualFormat.B10G11R11UfloatPack32)
        {
            failure = "trace-source-format-unrecognized";
            return false;
        }
        if (!Extent.TryValidate(out failure))
            return false;
        if (ColorSpace != SimpleDdgiNearFieldTraceSourceColorSpace.SceneLinearRadiance)
        {
            failure = "trace-source-scene-linear-radiance-required";
            return false;
        }
        if (Producer != SimpleDdgiNearFieldTraceSourceProducer
                .PreDdgiDirectDiffuseAndEmissive)
        {
            failure = "trace-source-pre-ddgi-direct-diffuse-emissive-producer-required";
            return false;
        }
        if (AlphaCoverage != SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics
                .OpaqueAndMaskedCoverageResolved)
        {
            failure = "trace-source-opaque-masked-coverage-resolution-required";
            return false;
        }
        if (LayoutRevision == 0u)
        {
            failure = "trace-source-layout-revision-required";
            return false;
        }
        if (SourceRevision == 0u)
        {
            failure = "trace-source-content-revision-required";
            return false;
        }

        failure = "valid";
        return true;
    }

    public bool TryValidateForLayout(
        in SimpleDdgiNearFieldResidualLayout layout,
        out string failure)
    {
        if (!TryValidate(out failure))
            return false;
        if (!layout.IsValid)
        {
            failure = "trace-source-requires-valid-near-field-layout";
            return false;
        }
        if (Format != layout.SourceFormat)
        {
            failure = "trace-source-format-layout-mismatch";
            return false;
        }
        if (Extent.ResolutionScale != layout.TraceResolutionScale)
        {
            failure = "trace-source-resolution-scale-layout-mismatch";
            return false;
        }
        if (!Extent.Matches(layout))
        {
            failure = "trace-source-scaled-extent-layout-mismatch";
            return false;
        }

        failure = "valid";
        return true;
    }
}

public enum SimpleDdgiNearFieldHistoryRejectionReason : byte
{
    None = 0,
    InvalidCurrentCandidate,
    InvalidPreviousCandidate,
    CameraCut,
    ViewportOrHiZRevisionChanged,
    TraceSourceAbiChanged,
    TraceSourceLayoutChanged,
    EffectiveModeChanged,
    ExposureDomainChanged,
    ProjectionOrOriginRevisionChanged,
    SceneOrTraceSourceContentRevisionChanged,
    NearFieldLayoutOrB3OwnershipChanged,
    ReceiverDepthMismatch,
    ReceiverNormalMismatch,
    ReceiverObjectMismatch,
    ReceiverMaterialRevisionMismatch,
    HitDepthMismatch,
    HitObjectMismatch,
    HitMaterialRevisionMismatch,
    ProbeOwnershipMismatch
}

/// <summary>Everything that makes residual history semantically reusable.</summary>
public readonly record struct SimpleDdgiNearFieldHistoryIdentity(
    bool CurrentCandidateValid,
    bool CameraCut,
    uint ViewportRevision,
    uint HiZRevision,
    uint TraceSourceAbiRevision,
    uint EffectiveModeRevision,
    uint ExposureDomainRevision,
    uint ReceiverObjectId,
    uint ReceiverMaterialRevision,
    uint HitObjectId,
    uint HitMaterialRevision,
    uint ProbeOwnershipRevision,
    float ReceiverDepth,
    float HitDepth,
    Vector3 ReceiverGeometricNormal,
    Vector3 ReceiverShadingNormal,
    uint ProjectionJitterRevision = 0u,
    uint OriginRebaseRevision = 0u,
    uint SceneGeneration = 0u,
    uint TraceSourceContentRevision = 0u,
    uint NearFieldLayoutRevision = 0u,
    uint B3OwnershipRevision = 0u,
    uint TraceSourceLayoutRevision = 0u);

public readonly record struct SimpleDdgiNearFieldHistoryValidation(
    bool Accepted,
    SimpleDdgiNearFieldHistoryRejectionReason Reason,
    float Confidence)
{
    public static SimpleDdgiNearFieldHistoryValidation Reject(
        SimpleDdgiNearFieldHistoryRejectionReason reason) => new(false, reason, 0.0f);
}

public static class SimpleDdgiNearFieldResidualReference
{
    public static bool IsTraceSourceValid(
        in SimpleDdgiNearFieldTraceSourceContract contract) => contract.IsValid;

    /// <summary>
    /// Produces the signed high-frequency band. This intentionally does not
    /// clamp negative values: a valid screen-space observation can correct a
    /// local over-bright DDGI estimate. Invalid data returns exactly zero.
    /// </summary>
    public static Vector3 EvaluateBandResidual(
        Vector3 nearEstimate,
        Vector3 lowEstimate,
        float confidence,
        bool nearEstimateValid,
        bool lowEstimateValid)
    {
        if (!IsFinite(nearEstimate) || !IsFinite(lowEstimate) ||
            !float.IsFinite(confidence) || !nearEstimateValid || !lowEstimateValid)
        {
            return Vector3.Zero;
        }

        // The final composite owns the one authoritative confidence weight.
        // Keeping this signed band unweighted mirrors the trace shader and
        // prevents a valid trace confidence from being squared at composite.
        return nearEstimate - lowEstimate;
    }

    /// <summary>Preserves the canonical DDGI/B3 result on invalid C5 data.</summary>
    public static Vector3 Composite(
        Vector3 canonicalDdgiPlusB3,
        Vector3 bandResidual,
        float confidence,
        bool residualValid)
    {
        if (!IsFinite(canonicalDdgiPlusB3))
            throw new ArgumentOutOfRangeException(nameof(canonicalDdgiPlusB3));
        if (!residualValid || !IsFinite(bandResidual) || !float.IsFinite(confidence))
            return Vector3.Max(canonicalDdgiPlusB3, Vector3.Zero);

        return Vector3.Max(
            canonicalDdgiPlusB3 + bandResidual * Math.Clamp(confidence, 0.0f, 1.0f),
            Vector3.Zero);
    }

    public static SimpleDdgiNearFieldHistoryValidation ValidateHistory(
        in SimpleDdgiNearFieldHistoryIdentity current,
        in SimpleDdgiNearFieldHistoryIdentity previous,
        float depthTolerance,
        float minimumNormalDot)
    {
        if (!current.CurrentCandidateValid)
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.InvalidCurrentCandidate);
        if (!previous.CurrentCandidateValid)
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.InvalidPreviousCandidate);
        if (current.CameraCut)
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.CameraCut);
        if (!float.IsFinite(depthTolerance) || depthTolerance < 0.0f ||
            !float.IsFinite(minimumNormalDot) || minimumNormalDot < -1.0f || minimumNormalDot > 1.0f ||
            !FiniteIdentity(current) || !FiniteIdentity(previous))
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.InvalidCurrentCandidate);
        }
        if (current.ViewportRevision != previous.ViewportRevision ||
            current.HiZRevision != previous.HiZRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ViewportOrHiZRevisionChanged);
        }
        if (current.TraceSourceAbiRevision != previous.TraceSourceAbiRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.TraceSourceAbiChanged);
        }
        if (current.TraceSourceLayoutRevision != previous.TraceSourceLayoutRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.TraceSourceLayoutChanged);
        }
        if (current.EffectiveModeRevision != previous.EffectiveModeRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.EffectiveModeChanged);
        }
        if (current.ExposureDomainRevision != previous.ExposureDomainRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ExposureDomainChanged);
        }
        if (current.ProjectionJitterRevision != previous.ProjectionJitterRevision ||
            current.OriginRebaseRevision != previous.OriginRebaseRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ProjectionOrOriginRevisionChanged);
        }
        if (current.SceneGeneration != previous.SceneGeneration ||
            current.TraceSourceContentRevision != previous.TraceSourceContentRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.SceneOrTraceSourceContentRevisionChanged);
        }
        if (current.NearFieldLayoutRevision != previous.NearFieldLayoutRevision ||
            current.B3OwnershipRevision != previous.B3OwnershipRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.NearFieldLayoutOrB3OwnershipChanged);
        }
        if (MathF.Abs(current.ReceiverDepth - previous.ReceiverDepth) > depthTolerance)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ReceiverDepthMismatch);
        }
        if (MathF.Abs(current.HitDepth - previous.HitDepth) > depthTolerance)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.HitDepthMismatch);
        }
        if (Vector3.Dot(Vector3.Normalize(current.ReceiverGeometricNormal),
                Vector3.Normalize(previous.ReceiverGeometricNormal)) < minimumNormalDot ||
            Vector3.Dot(Vector3.Normalize(current.ReceiverShadingNormal),
                Vector3.Normalize(previous.ReceiverShadingNormal)) < minimumNormalDot)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ReceiverNormalMismatch);
        }
        if (current.ReceiverObjectId != previous.ReceiverObjectId)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ReceiverObjectMismatch);
        }
        if (current.ReceiverMaterialRevision != previous.ReceiverMaterialRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ReceiverMaterialRevisionMismatch);
        }
        if (current.HitObjectId != previous.HitObjectId)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.HitObjectMismatch);
        }
        if (current.HitMaterialRevision != previous.HitMaterialRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.HitMaterialRevisionMismatch);
        }
        if (current.ProbeOwnershipRevision != previous.ProbeOwnershipRevision)
        {
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.ProbeOwnershipMismatch);
        }

        float depthConfidence = depthTolerance <= 0.0f
            ? 1.0f
            : Math.Clamp(1.0f - MathF.Abs(current.ReceiverDepth - previous.ReceiverDepth) / depthTolerance,
                0.0f, 1.0f);
        return new SimpleDdgiNearFieldHistoryValidation(
            true,
            SimpleDdgiNearFieldHistoryRejectionReason.None,
            depthConfidence);
    }

    private static bool FiniteIdentity(in SimpleDdgiNearFieldHistoryIdentity identity) =>
        float.IsFinite(identity.ReceiverDepth) &&
        float.IsFinite(identity.HitDepth) &&
        IsFinite(identity.ReceiverGeometricNormal) &&
        IsFinite(identity.ReceiverShadingNormal) &&
        identity.ReceiverGeometricNormal.LengthSquared() > 1.0e-12f &&
        identity.ReceiverShadingNormal.LengthSquared() > 1.0e-12f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public enum SimpleDdgiNearFieldDepthConvention : byte
{
    ForwardZ,
    ReversedZ
}

public interface ISimpleDdgiNearFieldDepthHierarchy
{
    int MaximumMipLevel { get; }
    bool TrySample(Vector2 uv, int mipLevel, out float depth);
}

public readonly record struct SimpleDdgiNearFieldTraceConfiguration(
    int MaximumSteps,
    int MaximumMipVisits,
    int BinaryRefinementSteps,
    float Thickness,
    float StartBias,
    SimpleDdgiNearFieldDepthConvention DepthConvention)
{
    public void Validate()
    {
        if (MaximumSteps is < 1 or > 256 || MaximumMipVisits is < 1 or > 32 ||
            BinaryRefinementSteps is < 0 or > 16 || !float.IsFinite(Thickness) || Thickness < 0.0f ||
            !float.IsFinite(StartBias) || StartBias < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSteps));
        }
    }
}

public readonly record struct SimpleDdgiNearFieldTraceResult(
    bool Hit,
    Vector2 HitUv,
    float RayDepth,
    float SceneDepth,
    int StepCount,
    int MipVisitCount,
    int RefinementCount,
    string RejectionReason)
{
    public static SimpleDdgiNearFieldTraceResult Miss(int steps, int mipVisits, string reason) =>
        new(false, default, 0.0f, 0.0f, steps, mipVisits, 0, reason);
}

/// <summary>
/// Bounded screen-space interval trace reference. It models the guardrails
/// shared by the shader: all loops have declared maxima, off-screen exits are
/// misses, and a result includes exact screen coordinates for source lookup.
/// </summary>
public static class SimpleDdgiNearFieldTraceReference
{
    public static SimpleDdgiNearFieldTraceResult Trace(
        ISimpleDdgiNearFieldDepthHierarchy hierarchy,
        Vector2 startUv,
        Vector2 endUv,
        float startDepth,
        float endDepth,
        in SimpleDdgiNearFieldTraceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        configuration.Validate();
        if (!IsFinite(startUv) || !IsFinite(endUv) || !float.IsFinite(startDepth) ||
            !float.IsFinite(endDepth))
        {
            return SimpleDdgiNearFieldTraceResult.Miss(0, 0, "non-finite-ray");
        }

        float previousT = 0.0f;
        float previousRayDepth = startDepth + configuration.StartBias;
        int mipVisits = 0;
        for (int step = 1; step <= configuration.MaximumSteps; step++)
        {
            float t = step / (float)configuration.MaximumSteps;
            Vector2 uv = Vector2.Lerp(startUv, endUv, t);
            if (uv.X < 0.0f || uv.X > 1.0f || uv.Y < 0.0f || uv.Y > 1.0f)
                return SimpleDdgiNearFieldTraceResult.Miss(step, mipVisits, "screen-exit");

            if (mipVisits >= configuration.MaximumMipVisits)
            {
                return SimpleDdgiNearFieldTraceResult.Miss(
                    step - 1, mipVisits, "mip-visit-budget");
            }
            int mip = Math.Min(hierarchy.MaximumMipLevel,
                EstimateMip(startUv, endUv, step));
            if (!hierarchy.TrySample(uv, mip, out float sampledDepth) || !float.IsFinite(sampledDepth))
                return SimpleDdgiNearFieldTraceResult.Miss(step, mipVisits, "depth-unavailable");
            mipVisits++;
            float rayDepth = Lerp(startDepth + configuration.StartBias, endDepth, t);
            if (!CrossesSurface(rayDepth, sampledDepth, configuration.Thickness, configuration.DepthConvention))
            {
                previousT = t;
                previousRayDepth = rayDepth;
                continue;
            }

            float lo = previousT;
            float hi = t;
            float sceneDepth = sampledDepth;
            int refinements = 0;
            for (; refinements < configuration.BinaryRefinementSteps; refinements++)
            {
                if (mipVisits >= configuration.MaximumMipVisits)
                {
                    return SimpleDdgiNearFieldTraceResult.Miss(
                        step, mipVisits, "mip-visit-budget");
                }
                float mid = 0.5f * (lo + hi);
                Vector2 midUv = Vector2.Lerp(startUv, endUv, mid);
                if (!hierarchy.TrySample(midUv, 0, out float midDepth) || !float.IsFinite(midDepth))
                    break;
                mipVisits++;
                float midRayDepth = Lerp(startDepth + configuration.StartBias, endDepth, mid);
                if (CrossesSurface(midRayDepth, midDepth, configuration.Thickness, configuration.DepthConvention))
                {
                    hi = mid;
                    sceneDepth = midDepth;
                }
                else
                {
                    lo = mid;
                    previousRayDepth = midRayDepth;
                }
            }

            Vector2 hitUv = Vector2.Lerp(startUv, endUv, hi);
            float hitRayDepth = Lerp(startDepth + configuration.StartBias, endDepth, hi);
            return new SimpleDdgiNearFieldTraceResult(
                true, hitUv, hitRayDepth, sceneDepth, step, mipVisits,
                refinements, "hit");
        }

        return SimpleDdgiNearFieldTraceResult.Miss(
            configuration.MaximumSteps, mipVisits, "step-limit");
    }

    private static int EstimateMip(Vector2 start, Vector2 end, int step)
    {
        float span = Vector2.Distance(start, end);
        if (span <= 0.0f)
            return 0;
        return Math.Max(0, (int)MathF.Floor(MathF.Log2(1.0f + span * 1_024.0f / step)));
    }

    private static bool CrossesSurface(
        float rayDepth,
        float sceneDepth,
        float thickness,
        SimpleDdgiNearFieldDepthConvention convention) =>
        convention == SimpleDdgiNearFieldDepthConvention.ForwardZ
            ? rayDepth >= sceneDepth - thickness
            : rayDepth <= sceneDepth + thickness;

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static float Lerp(float start, float end, float amount) =>
        start + (end - start) * amount;
}
