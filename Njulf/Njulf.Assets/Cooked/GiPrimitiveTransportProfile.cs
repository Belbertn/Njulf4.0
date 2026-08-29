using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

[Flags]
public enum GiPrimitiveTransportProfileValidity : uint
{
    None = 0,
    Geometry = 1u << 0,
    Diffuse = 1u << 1,
    Emission = 1u << 2,
    AmbientOcclusion = 1u << 3,
    AlphaCoverage = 1u << 4,
    MetallicRoughness = 1u << 5,
    NormalVariance = 1u << 6,
    TextureSamplingComplete = 1u << 7,
    Finite = 1u << 8
}

public enum GiPrimitiveTransportProfileQuality
{
    Invalid,
    FactorAndVertexColor,
    SurfaceQuadrature7,
    PartialTextureData
}

[Flags]
public enum GiPrimitiveEmissiveTriangleFlags : uint
{
    None = 0,
    SamplingComplete = 1u << 0,
    Finite = 1u << 1,
    PrimitiveRecordCapTruncated = 1u << 2,
    PackageRecordCapTruncated = 1u << 3
}

public enum GiPrimitivePlanarEvidenceRejectionReason : uint
{
    None = 0,
    NotAnalyzed = 1,
    DeformingGeometry = 2,
    MissingOrMalformedGeometry = 3,
    NonFiniteGeometry = 4,
    NoPositiveArea = 5,
    TriangleNormalDivergence = 6,
    VertexPlaneDeviation = 7
}

/// <summary>
/// Deterministic local-space evidence that a rigid primitive is one planar
/// receiver. Bounds are expressed in the stored tangent/bitangent frame about
/// <see cref="LocalOrigin"/> and the plane follows dot(n, p) + w = 0.
/// </summary>
public sealed record GiPrimitivePlanarEvidence
{
    public const float MinimumTriangleNormalDot = 0.9995f;
    public const double MinimumPlaneToleranceMeters = 0.0005;
    public const double RelativePlaneTolerance = 1.0e-4;

    public bool IsValid { get; init; }
    public Vector4 LocalPlane { get; init; } =
        new(Vector3.UnitZ, 0.0f);
    public Vector3 LocalOrigin { get; init; }
    public Vector3 LocalTangent { get; init; } = Vector3.UnitX;
    public Vector3 LocalBitangent { get; init; } = Vector3.UnitY;
    public Vector2 ProjectedBoundsMin { get; init; }
    public Vector2 ProjectedBoundsMax { get; init; }
    public double SurfaceArea { get; init; }
    public double MaximumDeviation { get; init; }
    public double PlaneTolerance { get; init; } =
        MinimumPlaneToleranceMeters;
    public GiPrimitivePlanarEvidenceRejectionReason RejectionReason
    {
        get;
        init;
    } = GiPrimitivePlanarEvidenceRejectionReason.NotAnalyzed;
    public string Detail { get; init; } = "Planar evidence was not analyzed.";

    public static GiPrimitivePlanarEvidence NotAnalyzed { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        bool finite = float.IsFinite(LocalPlane.X) &&
            float.IsFinite(LocalPlane.Y) &&
            float.IsFinite(LocalPlane.Z) &&
            float.IsFinite(LocalPlane.W) &&
            IsFinite(LocalOrigin) && IsFinite(LocalTangent) &&
            IsFinite(LocalBitangent) &&
            float.IsFinite(ProjectedBoundsMin.X) &&
            float.IsFinite(ProjectedBoundsMin.Y) &&
            float.IsFinite(ProjectedBoundsMax.X) &&
            float.IsFinite(ProjectedBoundsMax.Y) &&
            double.IsFinite(SurfaceArea) &&
            double.IsFinite(MaximumDeviation) &&
            double.IsFinite(PlaneTolerance);
        if (!finite)
            errors.Add("Primitive planar evidence contains non-finite data.");
        if (!Enum.IsDefined(RejectionReason))
            errors.Add("Primitive planar evidence has an unknown rejection reason.");
        if (!IsValid)
        {
            if (RejectionReason == GiPrimitivePlanarEvidenceRejectionReason.None)
                errors.Add("Rejected primitive planar evidence requires a reason.");
            return errors;
        }

        if (RejectionReason != GiPrimitivePlanarEvidenceRejectionReason.None)
            errors.Add("Valid primitive planar evidence cannot have a rejection reason.");
        if (SurfaceArea <= 0.0 || PlaneTolerance <= 0.0 ||
            MaximumDeviation < 0.0 || MaximumDeviation > PlaneTolerance)
        {
            errors.Add("Valid primitive planar evidence has invalid area/deviation bounds.");
        }
        Vector3 normal = new(LocalPlane.X, LocalPlane.Y, LocalPlane.Z);
        if (Math.Abs(normal.LengthSquared() - 1.0f) > 1.0e-4f ||
            Math.Abs(LocalTangent.LengthSquared() - 1.0f) > 1.0e-4f ||
            Math.Abs(LocalBitangent.LengthSquared() - 1.0f) > 1.0e-4f ||
            Math.Abs(Vector3.Dot(normal, LocalTangent)) > 1.0e-4f ||
            Math.Abs(Vector3.Dot(normal, LocalBitangent)) > 1.0e-4f ||
            Math.Abs(Vector3.Dot(LocalTangent, LocalBitangent)) > 1.0e-4f)
        {
            errors.Add("Primitive planar evidence basis is not orthonormal.");
        }
        if (ProjectedBoundsMin.X > ProjectedBoundsMax.X ||
            ProjectedBoundsMin.Y > ProjectedBoundsMax.Y)
        {
            errors.Add("Primitive planar evidence projected bounds are inverted.");
        }
        return errors;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>Shared cooker/runtime deterministic planar analyzer.</summary>
public static class GiPrimitivePlanarEvidenceAnalyzer
{
    public static GiPrimitivePlanarEvidence Analyze(ModelSubMesh subMesh)
    {
        ArgumentNullException.ThrowIfNull(subMesh);
        bool deforming = subMesh.SkinIndex >= 0 ||
            subMesh.JointIndices0.Length != 0 ||
            subMesh.JointWeights0.Length != 0;
        return Analyze(subMesh.Vertices, subMesh.Indices, deforming);
    }

    public static GiPrimitivePlanarEvidence Analyze(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        bool deforming)
    {
        if (deforming)
        {
            return Reject(
                GiPrimitivePlanarEvidenceRejectionReason.DeformingGeometry,
                "Skinned or otherwise deforming geometry cannot own a stable planar capture.");
        }
        if (positions.Length == 0 || indices.Length == 0 ||
            indices.Length % 3 != 0)
        {
            return Reject(
                GiPrimitivePlanarEvidenceRejectionReason.MissingOrMalformedGeometry,
                "Planar evidence requires a non-empty triangle list.");
        }

        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions)
        {
            if (!IsFinite(position))
            {
                return Reject(
                    GiPrimitivePlanarEvidenceRejectionReason.NonFiniteGeometry,
                    "Planar evidence encountered a non-finite position.");
            }
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        double diagonal = (maximum - minimum).Length();
        double tolerance = Math.Max(
            GiPrimitivePlanarEvidence.MinimumPlaneToleranceMeters,
            diagonal * GiPrimitivePlanarEvidence.RelativePlaneTolerance);

        Vector3 referenceNormal = Vector3.Zero;
        Vector3 weightedNormal = Vector3.Zero;
        Vector3 weightedCentroid = Vector3.Zero;
        double totalArea = 0.0;
        for (int index = 0; index < indices.Length; index += 3)
        {
            if (!TryReadTriangle(
                    positions,
                    indices,
                    index,
                    out Vector3 p0,
                    out Vector3 p1,
                    out Vector3 p2))
            {
                return Reject(
                    GiPrimitivePlanarEvidenceRejectionReason.MissingOrMalformedGeometry,
                    "Planar evidence contains an out-of-range triangle index.",
                    tolerance);
            }
            Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
            double twiceArea = cross.Length();
            if (!double.IsFinite(twiceArea))
            {
                return Reject(
                    GiPrimitivePlanarEvidenceRejectionReason.NonFiniteGeometry,
                    "Planar evidence produced a non-finite triangle area.",
                    tolerance);
            }
            if (twiceArea <= 2.0e-20)
                continue;
            double area = 0.5 * twiceArea;
            Vector3 normal = cross / (float)twiceArea;
            if (referenceNormal.LengthSquared() <= 1.0e-12f)
                referenceNormal = normal;
            if (Vector3.Dot(normal, referenceNormal) < 0.0f)
                normal = -normal;
            weightedNormal += normal * (float)area;
            weightedCentroid += ((p0 + p1 + p2) / 3.0f) * (float)area;
            totalArea += area;
        }
        if (totalArea <= 0.0 || weightedNormal.LengthSquared() <= 1.0e-12f)
        {
            return Reject(
                GiPrimitivePlanarEvidenceRejectionReason.NoPositiveArea,
                "Planar evidence contains no positive-area triangle.",
                tolerance);
        }

        Vector3 fittedNormal = weightedNormal.Normalized();
        fittedNormal = OrientDeterministically(fittedNormal);
        Vector3 origin = weightedCentroid / (float)totalArea;
        float planeOffset = -Vector3.Dot(fittedNormal, origin);
        Vector3 tangent = CreateTangent(fittedNormal);
        Vector3 bitangent = Vector3.Cross(fittedNormal, tangent).Normalized();
        Vector2 projectedMinimum = new(float.PositiveInfinity);
        Vector2 projectedMaximum = new(float.NegativeInfinity);
        double maximumDeviation = 0.0;

        for (int index = 0; index < indices.Length; index += 3)
        {
            _ = TryReadTriangle(
                positions,
                indices,
                index,
                out Vector3 p0,
                out Vector3 p1,
                out Vector3 p2);
            Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
            float crossLength = cross.Length();
            if (crossLength <= 2.0e-20f)
                continue;
            Vector3 triangleNormal = cross / crossLength;
            float alignment = MathF.Abs(Vector3.Dot(
                triangleNormal,
                fittedNormal));
            if (alignment <
                GiPrimitivePlanarEvidence.MinimumTriangleNormalDot)
            {
                return Reject(
                    GiPrimitivePlanarEvidenceRejectionReason.TriangleNormalDivergence,
                    $"Triangle {index / 3} normal alignment {alignment:R} is below 0.9995.",
                    tolerance,
                    totalArea);
            }
            double deviation0 = Math.Abs(
                Vector3.Dot(fittedNormal, p0) + planeOffset);
            double deviation1 = Math.Abs(
                Vector3.Dot(fittedNormal, p1) + planeOffset);
            double deviation2 = Math.Abs(
                Vector3.Dot(fittedNormal, p2) + planeOffset);
            double triangleMaximumDeviation = Math.Max(
                deviation0,
                Math.Max(deviation1, deviation2));
            maximumDeviation = Math.Max(
                maximumDeviation,
                triangleMaximumDeviation);
            if (triangleMaximumDeviation > tolerance)
            {
                return Reject(
                    GiPrimitivePlanarEvidenceRejectionReason.VertexPlaneDeviation,
                    $"Triangle {index / 3} exceeds planar tolerance {tolerance:R} m.",
                    tolerance,
                    totalArea,
                    maximumDeviation);
            }
            IncludeProjected(p0, origin, tangent, bitangent,
                ref projectedMinimum, ref projectedMaximum);
            IncludeProjected(p1, origin, tangent, bitangent,
                ref projectedMinimum, ref projectedMaximum);
            IncludeProjected(p2, origin, tangent, bitangent,
                ref projectedMinimum, ref projectedMaximum);
        }

        return new GiPrimitivePlanarEvidence
        {
            IsValid = true,
            LocalPlane = new Vector4(fittedNormal, planeOffset),
            LocalOrigin = origin,
            LocalTangent = tangent,
            LocalBitangent = bitangent,
            ProjectedBoundsMin = projectedMinimum,
            ProjectedBoundsMax = projectedMaximum,
            SurfaceArea = totalArea,
            MaximumDeviation = maximumDeviation,
            PlaneTolerance = tolerance,
            RejectionReason = GiPrimitivePlanarEvidenceRejectionReason.None,
            Detail = "Rigid primitive satisfies deterministic planar evidence."
        };
    }

    private static GiPrimitivePlanarEvidence Reject(
        GiPrimitivePlanarEvidenceRejectionReason reason,
        string detail,
        double tolerance =
            GiPrimitivePlanarEvidence.MinimumPlaneToleranceMeters,
        double area = 0.0,
        double maximumDeviation = 0.0) => new()
    {
        IsValid = false,
        RejectionReason = reason,
        Detail = detail,
        PlaneTolerance = tolerance,
        SurfaceArea = area,
        MaximumDeviation = maximumDeviation
    };

    private static bool TryReadTriangle(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        int index,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2)
    {
        uint i0 = indices[index];
        uint i1 = indices[index + 1];
        uint i2 = indices[index + 2];
        if (i0 >= positions.Length || i1 >= positions.Length ||
            i2 >= positions.Length)
        {
            p0 = p1 = p2 = default;
            return false;
        }
        p0 = positions[(int)i0];
        p1 = positions[(int)i1];
        p2 = positions[(int)i2];
        return true;
    }

    private static Vector3 OrientDeterministically(Vector3 normal)
    {
        float x = MathF.Abs(normal.X);
        float y = MathF.Abs(normal.Y);
        float z = MathF.Abs(normal.Z);
        float largest = x >= y && x >= z
            ? normal.X
            : y >= z
                ? normal.Y
                : normal.Z;
        return largest < 0.0f ? -normal : normal;
    }

    private static void IncludeProjected(
        Vector3 position,
        Vector3 origin,
        Vector3 tangent,
        Vector3 bitangent,
        ref Vector2 minimum,
        ref Vector2 maximum)
    {
        Vector3 relative = position - origin;
        Vector2 projected = new(
            Vector3.Dot(relative, tangent),
            Vector3.Dot(relative, bitangent));
        minimum = new Vector2(
            Math.Min(minimum.X, projected.X),
            Math.Min(minimum.Y, projected.Y));
        maximum = new Vector2(
            Math.Max(maximum.X, projected.X),
            Math.Max(maximum.Y, projected.Y));
    }

    private static Vector3 CreateTangent(Vector3 normal)
    {
        float x = MathF.Abs(normal.X);
        float y = MathF.Abs(normal.Y);
        float z = MathF.Abs(normal.Z);
        Vector3 reference = x <= y && x <= z
            ? Vector3.UnitX
            : y <= z
                ? Vector3.UnitY
                : Vector3.UnitZ;
        return Vector3.Cross(reference, normal).Normalized();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>
/// Cooked, factor-neutral emissive transport for one source triangle.
/// <see cref="CoveredMeanEmissiveTexture"/> is conditional on the authored
/// alpha test accepting the surface. Multiplying it by <see cref="Coverage"/>
/// and the current emissive factor/strength therefore preserves spatial
/// emission/alpha correlation while still allowing safe factor-only edits.
/// </summary>
public sealed record GiPrimitiveEmissiveTriangleRecord
{
    public int TriangleIndex { get; init; } = -1;
    public double LocalSurfaceArea { get; init; }
    public double Coverage { get; init; }
    public TextureTransportVector4 CoveredMeanEmissiveTexture { get; init; }
    public double CookedImportance { get; init; }
}

/// <summary>
/// Texture-binding fields that affect deterministic surface sampling. Source
/// image content is authenticated separately by the ordered texture hashes.
/// </summary>
public sealed record GiPrimitiveTextureBindingSnapshot
{
    public bool IsBound { get; init; }
    public int TexCoordSet { get; init; }
    public Vector2 Offset { get; init; } = Vector2.Zero;
    public Vector2 Scale { get; init; } = Vector2.One;
    public float RotationRadians { get; init; }
    public TextureSamplerDescription Sampler { get; init; } = TextureSamplerDescription.Default;

    public static GiPrimitiveTextureBindingSnapshot Capture(ModelTextureSlot? binding) =>
        binding?.Source is null
            ? new GiPrimitiveTextureBindingSnapshot()
            : new GiPrimitiveTextureBindingSnapshot
            {
                IsBound = true,
                TexCoordSet = binding.TexCoordSet,
                Offset = binding.Offset,
                Scale = binding.Scale,
                RotationRadians = binding.RotationRadians,
                Sampler = binding.Sampler
            };
}

/// <summary>
/// Renderer-independent, cooked transport integration for one source submesh.
/// The profile is keyed by both submesh and material slot so instancing can
/// safely reuse it without guessing which authored binding produced it.
/// </summary>
public sealed record GiPrimitiveTransportProfile
{
    public const int CurrentSchemaVersion = 6;
    public const uint CurrentAlgorithmVersion = 7;
    // For Schlick Fresnel, the cosine-weighted hemispherical average of
    // 1 - F(NdotL) is (20 / 21) * (1 - F0).
    public const double SchlickCosineWeightedTransmission = 20.0 / 21.0;
    public const int SamplesPerTriangle = 7;
    public const int TextureSourceHashCount = 10;
    public const int MaximumEmissiveTriangleRecordsPerPrimitive = 4096;
    public const int MaximumEmissiveTriangleRecordsPerPackage = 65536;
    public const int EstimatedEmissiveTriangleRecordBytes = 64;
    public const long MaximumEmissiveTriangleBytesPerPrimitive =
        (long)MaximumEmissiveTriangleRecordsPerPrimitive * EstimatedEmissiveTriangleRecordBytes;
    public const long MaximumEmissiveTriangleBytesPerPackage =
        (long)MaximumEmissiveTriangleRecordsPerPackage * EstimatedEmissiveTriangleRecordBytes;
    public const string SampleRuleName = "Dunavant-7 degree-5; centroid error estimate";
    public const GiPrimitiveTransportProfileValidity CompleteValidity =
        GiPrimitiveTransportProfileValidity.Geometry |
        GiPrimitiveTransportProfileValidity.Diffuse |
        GiPrimitiveTransportProfileValidity.Emission |
        GiPrimitiveTransportProfileValidity.AmbientOcclusion |
        GiPrimitiveTransportProfileValidity.AlphaCoverage |
        GiPrimitiveTransportProfileValidity.MetallicRoughness |
        GiPrimitiveTransportProfileValidity.NormalVariance |
        GiPrimitiveTransportProfileValidity.TextureSamplingComplete |
        GiPrimitiveTransportProfileValidity.Finite;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public uint AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;
    public int SubMeshIndex { get; init; } = -1;
    public string SubMeshName { get; init; } = string.Empty;
    public int MaterialSlot { get; init; } = -1;
    public GiPrimitiveTransportProfileValidity Validity { get; init; }
    public GiPrimitiveTransportProfileQuality Quality { get; init; }
    public ulong InputHash { get; init; }
    public ulong[] TextureSourceHashes { get; init; } = Array.Empty<ulong>();
    public int TriangleCount { get; init; }
    public int DegenerateTriangleCount { get; init; }
    public int SampleCount { get; init; }
    public double SurfaceArea { get; init; }
    public TextureTransportVector4 MeanDiffuseReflectance { get; init; }
    public TextureTransportVector4 MeanTransmittedDiffuseReflectance { get; init; }
    public ModelGiTransmissionPolicy GiTransmissionPolicy { get; init; }
    public TextureTransportVector4 MeanEmission { get; init; }
    public double MeanAmbientOcclusion { get; init; }
    public double AlphaCoverage { get; init; }
    public double MeanMetallic { get; init; }
    public double MeanRoughness { get; init; }
    public double NormalVariance { get; init; }
    public double EstimatedIntegrationError { get; init; }
    public string SampleRule { get; init; } = SampleRuleName;
    public string? InvalidReason { get; init; }
    public GiPrimitiveEmissiveTriangleFlags EmissiveTriangleFlags { get; init; }
    public int EmissiveSourceTriangleCount { get; init; }
    public int EmissiveCandidateTriangleCount { get; init; }
    public GiPrimitiveEmissiveTriangleRecord[] EmissiveTriangles { get; init; } =
        Array.Empty<GiPrimitiveEmissiveTriangleRecord>();
    public double EmissiveTotalCookedImportance { get; init; }
    public double EmissiveRetainedCookedImportance { get; init; }
    public double EmissiveOmittedCookedImportance { get; init; }
    public TextureTransportVector4 CookedEmissiveFactor { get; init; }
    public double CookedEmissiveStrength { get; init; } = 1.0;
    public double CookedBaseAlphaFactor { get; init; } = 1.0;
    public ModelAlphaMode CookedAlphaMode { get; init; }
    public double CookedAlphaCutoff { get; init; } = 0.5;
    public bool CookedEmissionEligible { get; init; }
    public GiPrimitiveTextureBindingSnapshot BaseColorSamplingBinding { get; init; } = new();
    public GiPrimitiveTextureBindingSnapshot EmissiveSamplingBinding { get; init; } = new();
    public GiPrimitivePlanarEvidence PlanarEvidence { get; init; } =
        GiPrimitivePlanarEvidence.NotAnalyzed;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComplete => (Validity & CompleteValidity) == CompleteValidity;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Unsupported primitive-profile schema version {SchemaVersion}.");
        if (AlgorithmVersion != CurrentAlgorithmVersion)
            errors.Add($"Unsupported primitive-profile algorithm version {AlgorithmVersion}.");
        if (SubMeshIndex < 0)
            errors.Add($"Primitive-profile submesh index {SubMeshIndex} is invalid.");
        if (MaterialSlot < 0)
            errors.Add($"Primitive-profile material slot {MaterialSlot} is invalid.");
        if (TextureSourceHashes is null || TextureSourceHashes.Length != TextureSourceHashCount)
            errors.Add($"Primitive profile must contain exactly {TextureSourceHashCount} ordered texture hashes.");
        if (TriangleCount < 0 || DegenerateTriangleCount < 0 || SampleCount < 0)
            errors.Add("Primitive-profile triangle and sample counts cannot be negative.");
        else if (SampleCount != (long)TriangleCount * SamplesPerTriangle)
            errors.Add($"Primitive-profile sample count {SampleCount} does not match {TriangleCount} triangles.");
        if (!double.IsFinite(SurfaceArea) || SurfaceArea < 0.0)
            errors.Add($"Primitive-profile surface area {SurfaceArea} is invalid.");
        if (Validity.HasFlag(GiPrimitiveTransportProfileValidity.Geometry) &&
            (TriangleCount == 0 || SurfaceArea <= 0.0))
        {
            errors.Add("A geometry-valid primitive profile must contain positive sampled area.");
        }
        if (!AreFinite(MeanDiffuseReflectance))
            errors.Add("Primitive-profile diffuse reflectance contains a non-finite channel.");
        if (!AreFinite(MeanTransmittedDiffuseReflectance))
            errors.Add("Primitive-profile transmitted diffuse reflectance contains a non-finite channel.");
        if (!AreFinite(MeanEmission))
            errors.Add("Primitive-profile emission contains a non-finite channel.");
        ValidateUnit(MeanAmbientOcclusion, nameof(MeanAmbientOcclusion), errors);
        ValidateUnit(AlphaCoverage, nameof(AlphaCoverage), errors);
        ValidateUnit(MeanMetallic, nameof(MeanMetallic), errors);
        ValidateUnit(MeanRoughness, nameof(MeanRoughness), errors);
        ValidateUnit(NormalVariance, nameof(NormalVariance), errors);
        if (!double.IsFinite(EstimatedIntegrationError) || EstimatedIntegrationError < 0.0)
            errors.Add($"Primitive-profile integration error {EstimatedIntegrationError} is invalid.");
        if (Validity.HasFlag(GiPrimitiveTransportProfileValidity.Diffuse) &&
            !IsUnitRgb(MeanDiffuseReflectance))
        {
            errors.Add("A diffuse-valid primitive profile must have RGB reflectance in [0, 1].");
        }
        if (Validity.HasFlag(GiPrimitiveTransportProfileValidity.Diffuse) &&
            !IsUnitRgb(MeanTransmittedDiffuseReflectance))
        {
            errors.Add("A diffuse-valid primitive profile must have RGB transmittance in [0, 1].");
        }
        if (GiTransmissionPolicy == ModelGiTransmissionPolicy.ThinSurface &&
            (MeanDiffuseReflectance.X + MeanTransmittedDiffuseReflectance.X > 1.000001 ||
             MeanDiffuseReflectance.Y + MeanTransmittedDiffuseReflectance.Y > 1.000001 ||
             MeanDiffuseReflectance.Z + MeanTransmittedDiffuseReflectance.Z > 1.000001))
        {
            errors.Add("A thin-surface primitive profile exceeds the component-wise passive diffuse energy budget.");
        }
        if (Validity.HasFlag(GiPrimitiveTransportProfileValidity.Emission) &&
            (MeanEmission.X < 0.0 || MeanEmission.Y < 0.0 || MeanEmission.Z < 0.0))
        {
            errors.Add("An emission-valid primitive profile cannot contain negative radiance.");
        }
        if (!string.Equals(SampleRule, SampleRuleName, StringComparison.Ordinal))
            errors.Add($"Primitive-profile sample rule '{SampleRule}' is unsupported.");
        if (PlanarEvidence is null)
            errors.Add("Primitive-profile planar evidence cannot be null.");
        else
            foreach (string error in PlanarEvidence.Validate())
                errors.Add(error);
        if ((!IsComplete || Quality == GiPrimitiveTransportProfileQuality.Invalid) &&
            string.IsNullOrWhiteSpace(InvalidReason))
        {
            errors.Add("An incomplete primitive profile must provide an observable reason.");
        }
        ValidateEmissiveTriangles(errors);
        return errors;
    }

    public static GiPrimitiveTransportProfile LegacyInvalid(
        int subMeshIndex,
        string subMeshName,
        int materialSlot) => new()
        {
            SubMeshIndex = subMeshIndex,
            SubMeshName = subMeshName,
            MaterialSlot = materialSlot,
            TextureSourceHashes = new ulong[TextureSourceHashCount],
            Quality = GiPrimitiveTransportProfileQuality.Invalid,
            EmissiveTriangleFlags = GiPrimitiveEmissiveTriangleFlags.None,
            InvalidReason = "Legacy cooked material contains no primitive transport profile."
        };

    private void ValidateEmissiveTriangles(ICollection<string> errors)
    {
        GiPrimitiveEmissiveTriangleRecord[] records =
            EmissiveTriangles ?? Array.Empty<GiPrimitiveEmissiveTriangleRecord>();
        if (EmissiveTriangles is null)
            errors.Add("Primitive-profile emissive triangle records cannot be null.");
        if (EmissiveSourceTriangleCount < 0 ||
            EmissiveCandidateTriangleCount < 0 ||
            EmissiveCandidateTriangleCount > EmissiveSourceTriangleCount)
        {
            errors.Add("Primitive-profile emissive source/candidate triangle counts are invalid.");
        }
        if (records.Length > MaximumEmissiveTriangleRecordsPerPrimitive)
        {
            errors.Add(
                $"Primitive-profile emissive record count {records.Length} exceeds the hard " +
                $"per-primitive cap {MaximumEmissiveTriangleRecordsPerPrimitive}.");
        }
        if (records.Length > EmissiveCandidateTriangleCount)
            errors.Add("Primitive-profile retained emissive records exceed its candidate count.");

        const GiPrimitiveEmissiveTriangleFlags knownFlags =
            GiPrimitiveEmissiveTriangleFlags.SamplingComplete |
            GiPrimitiveEmissiveTriangleFlags.Finite |
            GiPrimitiveEmissiveTriangleFlags.PrimitiveRecordCapTruncated |
            GiPrimitiveEmissiveTriangleFlags.PackageRecordCapTruncated;
        if ((EmissiveTriangleFlags & ~knownFlags) != 0)
            errors.Add($"Primitive-profile emissive flags contain unknown bits: {EmissiveTriangleFlags}.");

        bool samplingComplete =
            EmissiveTriangleFlags.HasFlag(GiPrimitiveEmissiveTriangleFlags.SamplingComplete);
        bool finite = EmissiveTriangleFlags.HasFlag(GiPrimitiveEmissiveTriangleFlags.Finite);
        bool truncated =
            EmissiveTriangleFlags.HasFlag(GiPrimitiveEmissiveTriangleFlags.PrimitiveRecordCapTruncated) ||
            EmissiveTriangleFlags.HasFlag(GiPrimitiveEmissiveTriangleFlags.PackageRecordCapTruncated);
        if (samplingComplete !=
            Validity.HasFlag(GiPrimitiveTransportProfileValidity.TextureSamplingComplete))
        {
            errors.Add("Primitive-profile emissive sampling completeness disagrees with transport validity.");
        }
        if (finite != Validity.HasFlag(GiPrimitiveTransportProfileValidity.Finite))
            errors.Add("Primitive-profile emissive finite state disagrees with transport validity.");
        if (!truncated && records.Length != EmissiveCandidateTriangleCount)
            errors.Add("A non-truncated emissive profile must retain every candidate triangle.");
        if (truncated && records.Length >= EmissiveCandidateTriangleCount)
            errors.Add("A truncated emissive profile must omit at least one candidate triangle.");

        if (!AreFinite(CookedEmissiveFactor) ||
            CookedEmissiveFactor.X is < 0.0 or > 1.0 ||
            CookedEmissiveFactor.Y is < 0.0 or > 1.0 ||
            CookedEmissiveFactor.Z is < 0.0 or > 1.0)
        {
            errors.Add("Primitive-profile cooked emissive factor is invalid.");
        }
        if (!double.IsFinite(CookedEmissiveStrength) ||
            CookedEmissiveStrength is < 0.0 or > 65504.0)
        {
            errors.Add("Primitive-profile cooked emissive strength is invalid.");
        }
        if (!double.IsFinite(CookedBaseAlphaFactor) ||
            CookedBaseAlphaFactor is < 0.0 or > 1.0)
        {
            errors.Add("Primitive-profile cooked base alpha factor is invalid.");
        }
        if (!Enum.IsDefined(CookedAlphaMode))
            errors.Add($"Primitive-profile cooked alpha mode {CookedAlphaMode} is invalid.");
        if (!double.IsFinite(CookedAlphaCutoff) || CookedAlphaCutoff < 0.0)
            errors.Add("Primitive-profile cooked alpha cutoff must be finite and non-negative.");
        if (!CookedEmissionEligible &&
            (EmissiveCandidateTriangleCount != 0 ||
             records.Length != 0 ||
             EmissiveTotalCookedImportance != 0.0))
        {
            errors.Add("An emission-ineligible cooked primitive cannot retain emissive triangle transport.");
        }
        ValidateBinding(BaseColorSamplingBinding, nameof(BaseColorSamplingBinding), errors);
        ValidateBinding(EmissiveSamplingBinding, nameof(EmissiveSamplingBinding), errors);

        if (!double.IsFinite(EmissiveTotalCookedImportance) ||
            !double.IsFinite(EmissiveRetainedCookedImportance) ||
            !double.IsFinite(EmissiveOmittedCookedImportance) ||
            EmissiveTotalCookedImportance < 0.0 ||
            EmissiveRetainedCookedImportance < 0.0 ||
            EmissiveOmittedCookedImportance < 0.0)
        {
            errors.Add("Primitive-profile emissive importance totals are invalid.");
        }
        else if (!NearlyEqual(
                     EmissiveTotalCookedImportance,
                     EmissiveRetainedCookedImportance + EmissiveOmittedCookedImportance))
        {
            errors.Add("Primitive-profile emissive retained/omitted importance does not conserve total importance.");
        }

        int previousTriangle = -1;
        double retainedImportance = 0.0;
        foreach (GiPrimitiveEmissiveTriangleRecord? record in records)
        {
            if (record is null)
            {
                errors.Add("Primitive-profile contains a null emissive triangle record.");
                continue;
            }
            if (record.TriangleIndex <= previousTriangle ||
                record.TriangleIndex < 0 ||
                record.TriangleIndex >= EmissiveSourceTriangleCount)
            {
                errors.Add("Primitive-profile emissive triangle indices must be unique, in-range, and strictly increasing.");
            }
            previousTriangle = record.TriangleIndex;
            if (!double.IsFinite(record.LocalSurfaceArea) || record.LocalSurfaceArea <= 1e-20)
                errors.Add($"Emissive triangle {record.TriangleIndex} has invalid local area.");
            if (!double.IsFinite(record.Coverage) || record.Coverage is <= 0.0 or > 1.0)
                errors.Add($"Emissive triangle {record.TriangleIndex} has invalid coverage.");
            if (!IsUnitRgb(record.CoveredMeanEmissiveTexture) ||
                !AreFinite(record.CoveredMeanEmissiveTexture))
            {
                errors.Add($"Emissive triangle {record.TriangleIndex} has invalid conditional texture radiance.");
            }
            if (!double.IsFinite(record.CookedImportance) || record.CookedImportance <= 0.0)
                errors.Add($"Emissive triangle {record.TriangleIndex} has invalid cooked importance.");
            else
            {
                double expected = ComputeCookedImportance(record);
                if (!NearlyEqual(expected, record.CookedImportance))
                    errors.Add($"Emissive triangle {record.TriangleIndex} cooked importance is inconsistent.");
                retainedImportance += record.CookedImportance;
            }
        }
        if (!NearlyEqual(retainedImportance, EmissiveRetainedCookedImportance))
            errors.Add("Primitive-profile emissive record importance does not match retained importance.");
    }

    private double ComputeCookedImportance(GiPrimitiveEmissiveTriangleRecord record)
    {
        return record.LocalSurfaceArea * record.Coverage *
               (0.2126 * record.CoveredMeanEmissiveTexture.X +
                0.7152 * record.CoveredMeanEmissiveTexture.Y +
                0.0722 * record.CoveredMeanEmissiveTexture.Z);
    }

    private static void ValidateBinding(
        GiPrimitiveTextureBindingSnapshot? binding,
        string name,
        ICollection<string> errors)
    {
        if (binding is null)
        {
            errors.Add($"Primitive-profile {name} cannot be null.");
            return;
        }
        if (binding.TexCoordSet is < 0 or > 1 ||
            !float.IsFinite(binding.Offset.X) ||
            !float.IsFinite(binding.Offset.Y) ||
            !float.IsFinite(binding.Scale.X) ||
            !float.IsFinite(binding.Scale.Y) ||
            !float.IsFinite(binding.RotationRadians) ||
            !float.IsFinite(binding.Sampler.MaxAnisotropy) ||
            binding.Sampler.MaxAnisotropy <= 0.0f)
        {
            errors.Add($"Primitive-profile {name} contains invalid sampling fields.");
        }
        if (!binding.IsBound &&
            (binding.TexCoordSet != 0 ||
             !binding.Offset.Equals(Vector2.Zero) ||
             !binding.Scale.Equals(Vector2.One) ||
             binding.RotationRadians != 0.0f ||
             binding.Sampler != TextureSamplerDescription.Default))
        {
            errors.Add($"Primitive-profile unbound {name} must use canonical defaults.");
        }
    }

    private static bool NearlyEqual(double left, double right)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-9;
    }

    private static bool AreFinite(TextureTransportVector4 value) =>
        double.IsFinite(value.X) &&
        double.IsFinite(value.Y) &&
        double.IsFinite(value.Z) &&
        double.IsFinite(value.W);

    private static bool IsUnitRgb(TextureTransportVector4 value) =>
        value.X is >= 0.0 and <= 1.0 &&
        value.Y is >= 0.0 and <= 1.0 &&
        value.Z is >= 0.0 and <= 1.0;

    private static void ValidateUnit(double value, string name, ICollection<string> errors)
    {
        if (!double.IsFinite(value) || value is < 0.0 or > 1.0)
            errors.Add($"Primitive-profile {name} value {value} is outside [0, 1].");
    }
}

public sealed record GiPrimitiveTextureInputs(
    TextureTransportImage? BaseColor = null,
    TextureTransportImage? MetallicRoughness = null,
    TextureTransportImage? Occlusion = null,
    TextureTransportImage? Emissive = null,
    TextureTransportImage? Normal = null,
    TextureTransportImage? Clearcoat = null,
    TextureTransportImage? SheenColor = null,
    TextureTransportImage? Transmission = null,
    TextureTransportImage? Specular = null,
    TextureTransportImage? SpecularColor = null);

/// <summary>
/// Deterministic surface-area integration over source triangles. All material
/// channels use identical barycentric samples, preserving base/metallic and
/// emission/coverage correlation that independent texture averages lose.
/// </summary>
public static class GiPrimitiveTransportProfileGenerator
{
    private static readonly QuadraturePoint[] Quadrature =
    [
        new(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0, 0.225),
        new(0.059715871789770, 0.470142064105115, 0.470142064105115, 0.132394152788506),
        new(0.470142064105115, 0.059715871789770, 0.470142064105115, 0.132394152788506),
        new(0.470142064105115, 0.470142064105115, 0.059715871789770, 0.132394152788506),
        new(0.797426985353087, 0.101286507323456, 0.101286507323456, 0.125939180544827),
        new(0.101286507323456, 0.797426985353087, 0.101286507323456, 0.125939180544827),
        new(0.101286507323456, 0.101286507323456, 0.797426985353087, 0.125939180544827)
    ];

    public static GiPrimitiveTransportProfile Generate(
        int subMeshIndex,
        ModelSubMesh subMesh,
        ModelMaterial material,
        GiPrimitiveTextureInputs? textures = null,
        ulong? precomputedInputHash = null)
    {
        ArgumentNullException.ThrowIfNull(subMesh);
        ArgumentNullException.ThrowIfNull(material);
        if (!float.IsFinite(material.AlphaCutoff) || material.AlphaCutoff < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                "Primitive transport alpha cutoff must be finite and non-negative.");
        }
        textures ??= new GiPrimitiveTextureInputs();
        GiPrimitivePlanarEvidence planarEvidence =
            GiPrimitivePlanarEvidenceAnalyzer.Analyze(subMesh);

        GiPrimitiveTransportProfileValidity validity =
            GiPrimitiveTransportProfileValidity.Geometry |
            GiPrimitiveTransportProfileValidity.Diffuse |
            GiPrimitiveTransportProfileValidity.Emission |
            GiPrimitiveTransportProfileValidity.AmbientOcclusion |
            GiPrimitiveTransportProfileValidity.AlphaCoverage |
            GiPrimitiveTransportProfileValidity.MetallicRoughness |
            GiPrimitiveTransportProfileValidity.NormalVariance |
            GiPrimitiveTransportProfileValidity.TextureSamplingComplete |
            GiPrimitiveTransportProfileValidity.Finite;
        var integrated = new Accumulator();
        var centroid = new Accumulator();
        var emissiveCandidates = new List<GiPrimitiveEmissiveTriangleRecord>();
        int triangles = 0;
        int degenerateTriangles = 0;
        int sampleCount = 0;

        if (subMesh.Indices.Length == 0 || subMesh.Indices.Length % 3 != 0)
            validity &= ~GiPrimitiveTransportProfileValidity.Geometry;

        int completeIndexCount = subMesh.Indices.Length - subMesh.Indices.Length % 3;
        for (int index = 0; index < completeIndexCount; index += 3)
        {
            uint i0 = subMesh.Indices[index];
            uint i1 = subMesh.Indices[index + 1];
            uint i2 = subMesh.Indices[index + 2];
            if (i0 >= subMesh.Vertices.Length || i1 >= subMesh.Vertices.Length || i2 >= subMesh.Vertices.Length)
            {
                validity &= ~GiPrimitiveTransportProfileValidity.Geometry;
                continue;
            }

            Vector3 p0 = subMesh.Vertices[i0];
            Vector3 p1 = subMesh.Vertices[i1];
            Vector3 p2 = subMesh.Vertices[i2];
            double area = TriangleArea(p0, p1, p2);
            if (!double.IsFinite(area))
            {
                validity &= ~(GiPrimitiveTransportProfileValidity.Geometry | GiPrimitiveTransportProfileValidity.Finite);
                continue;
            }
            if (area <= 1e-20)
            {
                degenerateTriangles++;
                continue;
            }

            triangles++;
            var triangleEmission = new EmissiveTriangleAccumulator();
            foreach (QuadraturePoint point in Quadrature)
            {
                MaterialSample sample = EvaluateSample(
                    subMesh,
                    material,
                    textures,
                    i0,
                    i1,
                    i2,
                    point.B0,
                    point.B1,
                    point.B2,
                    ref validity);
                integrated.Add(sample, area * point.Weight);
                triangleEmission.Add(sample, point.Weight);
                sampleCount++;
            }

            MaterialSample centroidSample = EvaluateSample(
                subMesh,
                material,
                textures,
                i0,
                i1,
                i2,
                1.0 / 3.0,
                1.0 / 3.0,
                1.0 / 3.0,
                ref validity);
            centroid.Add(centroidSample, area);

            if (IsPotentialEmissiveMaterial(material) &&
                triangleEmission.TryFinish(
                    index / 3,
                    area,
                    out GiPrimitiveEmissiveTriangleRecord emissiveRecord))
            {
                emissiveCandidates.Add(emissiveRecord);
            }
        }

        ulong[] textureHashes = GetTextureHashes(textures);
        ulong inputHash = precomputedInputHash ??
                          ComputeInputHash(subMeshIndex, subMesh, material, textureHashes);
        EmissiveTriangleProfileData emissiveTriangleData = BuildEmissiveTriangleData(
            subMesh,
            material,
            validity,
            emissiveCandidates);
        if (integrated.Weight <= 0.0)
        {
            GiPrimitiveTransportProfile invalid = new()
            {
                SubMeshIndex = subMeshIndex,
                SubMeshName = subMesh.Name,
                MaterialSlot = subMesh.MaterialIndex,
                Validity = validity & ~(
                    GiPrimitiveTransportProfileValidity.Geometry |
                    GiPrimitiveTransportProfileValidity.Diffuse |
                    GiPrimitiveTransportProfileValidity.Emission |
                    GiPrimitiveTransportProfileValidity.AmbientOcclusion |
                    GiPrimitiveTransportProfileValidity.AlphaCoverage |
                    GiPrimitiveTransportProfileValidity.MetallicRoughness |
                    GiPrimitiveTransportProfileValidity.NormalVariance),
                Quality = GiPrimitiveTransportProfileQuality.Invalid,
                InputHash = inputHash,
                TextureSourceHashes = textureHashes,
                TriangleCount = triangles,
                DegenerateTriangleCount = degenerateTriangles,
                SampleCount = sampleCount,
                PlanarEvidence = planarEvidence,
                InvalidReason = "Primitive contains no finite, non-degenerate triangle area."
            };
            return ApplyEmissiveTriangleData(invalid, emissiveTriangleData);
        }

        IntegratedResult result = integrated.Finish();
        IntegratedResult centroidResult = centroid.Finish();
        double error = EstimateError(result, centroidResult);
        if (!result.IsFinite || !double.IsFinite(error))
            validity &= ~GiPrimitiveTransportProfileValidity.Finite;

        bool hasTextures =
            material.BaseColorTexture?.Source is not null ||
            material.MetallicRoughnessTexture?.Source is not null ||
            material.OcclusionTexture?.Source is not null ||
            material.EmissiveTexture?.Source is not null ||
            material.NormalTexture?.Source is not null ||
            HasActiveTexture(material, ModelMaterialFeatureBits.ClearcoatTexture, material.ClearcoatTexture) ||
            HasActiveTexture(material, ModelMaterialFeatureBits.SheenColorTexture, material.SheenColorTexture) ||
            HasActiveTexture(material, ModelMaterialFeatureBits.TransmissionTexture, material.TransmissionTexture) ||
            HasActiveTexture(material, ModelMaterialFeatureBits.SpecularTexture, material.SpecularTexture) ||
            HasActiveTexture(material, ModelMaterialFeatureBits.SpecularColorTexture, material.SpecularColorTexture);
        GiPrimitiveTransportProfileQuality quality = !hasTextures
            ? GiPrimitiveTransportProfileQuality.FactorAndVertexColor
            : validity.HasFlag(GiPrimitiveTransportProfileValidity.TextureSamplingComplete)
                ? GiPrimitiveTransportProfileQuality.SurfaceQuadrature7
                : GiPrimitiveTransportProfileQuality.PartialTextureData;

        GiPrimitiveTransportProfile profile = new()
        {
            SubMeshIndex = subMeshIndex,
            SubMeshName = subMesh.Name,
            MaterialSlot = subMesh.MaterialIndex,
            Validity = validity,
            Quality = quality,
            InputHash = inputHash,
            TextureSourceHashes = textureHashes,
            TriangleCount = triangles,
            DegenerateTriangleCount = degenerateTriangles,
            SampleCount = sampleCount,
            SurfaceArea = integrated.Weight,
            MeanDiffuseReflectance = result.Diffuse,
            MeanTransmittedDiffuseReflectance = result.TransmittedDiffuse,
            GiTransmissionPolicy = material.GiTransmissionPolicy,
            MeanEmission = result.Emission,
            MeanAmbientOcclusion = result.AmbientOcclusion,
            AlphaCoverage = result.Coverage,
            MeanMetallic = result.Metallic,
            MeanRoughness = result.Roughness,
            NormalVariance = result.NormalVariance,
            EstimatedIntegrationError = error,
            PlanarEvidence = planarEvidence,
            InvalidReason = GetInvalidReason(validity)
        };
        return ApplyEmissiveTriangleData(profile, emissiveTriangleData);
    }

    /// <summary>
    /// Computes the exact deterministic key consumed by <see cref="Generate"/>.
    /// Runtime caches may calculate it once, perform a lookup, and pass the
    /// returned value back as <c>precomputedInputHash</c> on a cache miss.
    /// </summary>
    public static ulong CalculateInputHash(
        int subMeshIndex,
        ModelSubMesh subMesh,
        ModelMaterial material,
        GiPrimitiveTextureInputs? textures = null)
    {
        ArgumentNullException.ThrowIfNull(subMesh);
        ArgumentNullException.ThrowIfNull(material);
        textures ??= new GiPrimitiveTextureInputs();
        return ComputeInputHash(
            subMeshIndex,
            subMesh,
            material,
            GetTextureHashes(textures));
    }

    public static IReadOnlyList<GiPrimitiveTransportProfile> ApplyPackageEmissiveRecordBudget(
        IReadOnlyList<GiPrimitiveTransportProfile> profiles,
        int maximumRecords = GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPackage)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (maximumRecords is < 0 or > GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPackage)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));

        int totalRecords = 0;
        var candidates = new List<PackageEmissiveCandidate>();
        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            GiPrimitiveTransportProfile profile = profiles[profileIndex] ??
                throw new ArgumentException("Primitive transport profile collection contains null.", nameof(profiles));
            GiPrimitiveEmissiveTriangleRecord[] records =
                profile.EmissiveTriangles ?? Array.Empty<GiPrimitiveEmissiveTriangleRecord>();
            totalRecords = checked(totalRecords + records.Length);
            foreach (GiPrimitiveEmissiveTriangleRecord record in records)
            {
                candidates.Add(new PackageEmissiveCandidate(
                    profileIndex,
                    profile.SubMeshIndex,
                    record));
            }
        }
        if (totalRecords <= maximumRecords)
            return profiles.ToArray();

        candidates.Sort(static (left, right) =>
        {
            int importance = right.Record.CookedImportance.CompareTo(left.Record.CookedImportance);
            if (importance != 0)
                return importance;
            int subMesh = left.SubMeshIndex.CompareTo(right.SubMeshIndex);
            return subMesh != 0
                ? subMesh
                : left.Record.TriangleIndex.CompareTo(right.Record.TriangleIndex);
        });

        var retainedByProfile = new List<GiPrimitiveEmissiveTriangleRecord>[profiles.Count];
        for (int i = 0; i < retainedByProfile.Length; i++)
            retainedByProfile[i] = new List<GiPrimitiveEmissiveTriangleRecord>();
        for (int i = 0; i < maximumRecords; i++)
            retainedByProfile[candidates[i].ProfileIndex].Add(candidates[i].Record);

        var bounded = new GiPrimitiveTransportProfile[profiles.Count];
        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            GiPrimitiveTransportProfile profile = profiles[profileIndex];
            List<GiPrimitiveEmissiveTriangleRecord> retained = retainedByProfile[profileIndex];
            retained.Sort(static (left, right) => left.TriangleIndex.CompareTo(right.TriangleIndex));
            double retainedImportance = retained.Sum(static record => record.CookedImportance);
            bool packageTruncated = retained.Count != profile.EmissiveTriangles.Length;
            bounded[profileIndex] = profile with
            {
                EmissiveTriangles = retained.ToArray(),
                EmissiveRetainedCookedImportance = retainedImportance,
                EmissiveOmittedCookedImportance =
                    Math.Max(profile.EmissiveTotalCookedImportance - retainedImportance, 0.0),
                EmissiveTriangleFlags = packageTruncated
                    ? profile.EmissiveTriangleFlags |
                      GiPrimitiveEmissiveTriangleFlags.PackageRecordCapTruncated
                    : profile.EmissiveTriangleFlags
            };
        }
        return bounded;
    }

    private static bool IsPotentialEmissiveMaterial(ModelMaterial material)
    {
        double luminance =
            0.2126 * Math.Max(material.Emissive.X, 0.0f) +
            0.7152 * Math.Max(material.Emissive.Y, 0.0f) +
            0.0722 * Math.Max(material.Emissive.Z, 0.0f);
        return !material.Unlit &&
               double.IsFinite(luminance) &&
               float.IsFinite(material.EmissiveStrength) &&
               (luminance > 0.0 || material.EmissiveTexture?.Source is not null);
    }

    private static EmissiveTriangleProfileData BuildEmissiveTriangleData(
        ModelSubMesh subMesh,
        ModelMaterial material,
        GiPrimitiveTransportProfileValidity validity,
        List<GiPrimitiveEmissiveTriangleRecord> candidates)
    {
        candidates.Sort(static (left, right) =>
        {
            int importance = right.CookedImportance.CompareTo(left.CookedImportance);
            return importance != 0
                ? importance
                : left.TriangleIndex.CompareTo(right.TriangleIndex);
        });
        double totalImportance = candidates.Sum(static record => record.CookedImportance);
        bool primitiveTruncated =
            candidates.Count > GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPrimitive;
        GiPrimitiveEmissiveTriangleRecord[] retained = candidates
            .Take(GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPrimitive)
            .OrderBy(static record => record.TriangleIndex)
            .ToArray();
        double retainedImportance = retained.Sum(static record => record.CookedImportance);
        GiPrimitiveEmissiveTriangleFlags flags = GiPrimitiveEmissiveTriangleFlags.None;
        if (validity.HasFlag(GiPrimitiveTransportProfileValidity.TextureSamplingComplete))
            flags |= GiPrimitiveEmissiveTriangleFlags.SamplingComplete;
        if (validity.HasFlag(GiPrimitiveTransportProfileValidity.Finite))
            flags |= GiPrimitiveEmissiveTriangleFlags.Finite;
        if (primitiveTruncated)
            flags |= GiPrimitiveEmissiveTriangleFlags.PrimitiveRecordCapTruncated;

        return new EmissiveTriangleProfileData(
            flags,
            subMesh.Indices.Length / 3,
            candidates.Count,
            retained,
            totalImportance,
            retainedImportance,
            Math.Max(totalImportance - retainedImportance, 0.0),
            new TextureTransportVector4(
                SanitizeUnit(material.Emissive.X),
                SanitizeUnit(material.Emissive.Y),
                SanitizeUnit(material.Emissive.Z),
                1.0),
            material.EmissiveStrength,
            material.Albedo.W,
            material.AlphaMode,
            material.AlphaCutoff,
            !material.Unlit,
            GiPrimitiveTextureBindingSnapshot.Capture(material.BaseColorTexture),
            GiPrimitiveTextureBindingSnapshot.Capture(material.EmissiveTexture));
    }

    private static GiPrimitiveTransportProfile ApplyEmissiveTriangleData(
        GiPrimitiveTransportProfile profile,
        EmissiveTriangleProfileData data) => profile with
        {
            EmissiveTriangleFlags = data.Flags,
            EmissiveSourceTriangleCount = data.SourceTriangleCount,
            EmissiveCandidateTriangleCount = data.CandidateTriangleCount,
            EmissiveTriangles = data.Records,
            EmissiveTotalCookedImportance = data.TotalImportance,
            EmissiveRetainedCookedImportance = data.RetainedImportance,
            EmissiveOmittedCookedImportance = data.OmittedImportance,
            CookedEmissiveFactor = data.CookedEmissiveFactor,
            CookedEmissiveStrength = data.CookedEmissiveStrength,
            CookedBaseAlphaFactor = data.CookedBaseAlphaFactor,
            CookedAlphaMode = data.CookedAlphaMode,
            CookedAlphaCutoff = data.CookedAlphaCutoff,
            CookedEmissionEligible = data.CookedEmissionEligible,
            BaseColorSamplingBinding = data.BaseColorSamplingBinding,
            EmissiveSamplingBinding = data.EmissiveSamplingBinding
        };

    private static string? GetInvalidReason(GiPrimitiveTransportProfileValidity validity)
    {
        GiPrimitiveTransportProfileValidity missing =
            GiPrimitiveTransportProfile.CompleteValidity & ~validity;
        if (missing == GiPrimitiveTransportProfileValidity.None)
            return null;
        if (missing.HasFlag(GiPrimitiveTransportProfileValidity.TextureSamplingComplete))
        {
            return "One or more bound textures or requested UV streams were unavailable; " +
                   $"affected validity flags are clear ({missing}).";
        }
        return $"Primitive transport inputs failed validation; incomplete fields: {missing}.";
    }

    private static MaterialSample EvaluateSample(
        ModelSubMesh subMesh,
        ModelMaterial material,
        GiPrimitiveTextureInputs textures,
        uint i0,
        uint i1,
        uint i2,
        double b0,
        double b1,
        double b2,
        ref GiPrimitiveTransportProfileValidity validity)
    {
        GiPrimitiveTransportProfileValidity vertexColorValidity =
            GiPrimitiveTransportProfileValidity.Diffuse;
        if (material.AlphaMode != ModelAlphaMode.Opaque)
            vertexColorValidity |= GiPrimitiveTransportProfileValidity.AlphaCoverage;
        TextureTransportVector4 vertexColor = InterpolateVertexColor(
            subMesh,
            i0,
            i1,
            i2,
            b0,
            b1,
            b2,
            vertexColorValidity,
            ref validity);
        GiPrimitiveTransportProfileValidity baseTextureValidity =
            GiPrimitiveTransportProfileValidity.Diffuse;
        if (material.AlphaMode != ModelAlphaMode.Opaque)
            baseTextureValidity |= GiPrimitiveTransportProfileValidity.AlphaCoverage;
        TextureTransportVector4 baseTexture = SampleTexture(
            subMesh, material.BaseColorTexture, textures.BaseColor, i0, i1, i2, b0, b1, b2,
            TextureTransportVector4.One,
            baseTextureValidity,
            ref validity);
        TextureTransportVector4 metallicRoughnessTexture = SampleTexture(
            subMesh, material.MetallicRoughnessTexture, textures.MetallicRoughness, i0, i1, i2, b0, b1, b2,
            TextureTransportVector4.One,
            GiPrimitiveTransportProfileValidity.MetallicRoughness | GiPrimitiveTransportProfileValidity.Diffuse,
            ref validity);
        TextureTransportVector4 occlusionTexture = SampleTexture(
            subMesh, material.OcclusionTexture, textures.Occlusion, i0, i1, i2, b0, b1, b2,
            TextureTransportVector4.One,
            GiPrimitiveTransportProfileValidity.AmbientOcclusion,
            ref validity);
        TextureTransportVector4 emissiveTexture = SampleTexture(
            subMesh, material.EmissiveTexture, textures.Emissive, i0, i1, i2, b0, b1, b2,
            TextureTransportVector4.One,
            GiPrimitiveTransportProfileValidity.Emission,
            ref validity);
        TextureTransportVector4 normalTexture = SampleTexture(
            subMesh, material.NormalTexture, textures.Normal, i0, i1, i2, b0, b1, b2,
            new TextureTransportVector4(0.5, 0.5, 1.0, 1.0),
            GiPrimitiveTransportProfileValidity.NormalVariance,
            ref validity);

        bool clearcoatEnabled = HasFeature(material, ModelMaterialFeatureBits.Clearcoat);
        bool sheenEnabled = HasFeature(material, ModelMaterialFeatureBits.Sheen);
        bool transmissionEnabled = HasFeature(material, ModelMaterialFeatureBits.Transmission);
        bool specularEnabled = HasFeature(material, ModelMaterialFeatureBits.Specular);
        bool iorEnabled =
            transmissionEnabled ||
            HasFeature(material, ModelMaterialFeatureBits.Ior);
        TextureTransportVector4 clearcoatTexture = SampleActiveTexture(
            clearcoatEnabled && HasFeature(material, ModelMaterialFeatureBits.ClearcoatTexture),
            subMesh, material.ClearcoatTexture, textures.Clearcoat, i0, i1, i2, b0, b1, b2,
            GiPrimitiveTransportProfileValidity.Diffuse,
            ref validity);
        TextureTransportVector4 sheenColorTexture = SampleActiveTexture(
            sheenEnabled && HasFeature(material, ModelMaterialFeatureBits.SheenColorTexture),
            subMesh, material.SheenColorTexture, textures.SheenColor, i0, i1, i2, b0, b1, b2,
            GiPrimitiveTransportProfileValidity.Diffuse,
            ref validity);
        TextureTransportVector4 transmissionTexture = SampleActiveTexture(
            transmissionEnabled && HasFeature(material, ModelMaterialFeatureBits.TransmissionTexture),
            subMesh, material.TransmissionTexture, textures.Transmission, i0, i1, i2, b0, b1, b2,
            GiPrimitiveTransportProfileValidity.Diffuse,
            ref validity);
        TextureTransportVector4 specularTexture = SampleActiveTexture(
            specularEnabled && HasFeature(material, ModelMaterialFeatureBits.SpecularTexture),
            subMesh, material.SpecularTexture, textures.Specular, i0, i1, i2, b0, b1, b2,
            GiPrimitiveTransportProfileValidity.Diffuse,
            ref validity);
        TextureTransportVector4 specularColorTexture = SampleActiveTexture(
            specularEnabled && HasFeature(material, ModelMaterialFeatureBits.SpecularColorTexture),
            subMesh, material.SpecularColorTexture, textures.SpecularColor, i0, i1, i2, b0, b1, b2,
            GiPrimitiveTransportProfileValidity.Diffuse,
            ref validity);

        double albedoR = material.Albedo.X * baseTexture.X * vertexColor.X;
        double albedoG = material.Albedo.Y * baseTexture.Y * vertexColor.Y;
        double albedoB = material.Albedo.Z * baseTexture.Z * vertexColor.Z;
        double alpha = material.Albedo.W * baseTexture.W * vertexColor.W;
        double metallic = material.Metallic * metallicRoughnessTexture.Z;
        double roughness = material.Roughness * metallicRoughnessTexture.Y;
        double clearcoat = clearcoatEnabled
            ? Math.Clamp(material.ClearcoatFactor * clearcoatTexture.X, 0.0, 1.0)
            : 0.0;
        double transmission = transmissionEnabled
            ? Math.Clamp(material.TransmissionFactor * transmissionTexture.X, 0.0, 1.0)
            : 0.0;
        double specularFactor = specularEnabled
            ? Math.Clamp(material.SpecularFactor * specularTexture.W, 0.0, 1.0)
            : 1.0;
        double ior = iorEnabled ? Math.Clamp(material.Ior, 1.0, 3.0) : 1.5;
        double iorRatio = (ior - 1.0) / (ior + 1.0);
        double dielectricF0 = iorRatio * iorRatio * specularFactor;
        double specularColorR = specularEnabled
            ? Math.Clamp(material.SpecularColor.X * specularColorTexture.X, 0.0, 1.0)
            : 1.0;
        double specularColorG = specularEnabled
            ? Math.Clamp(material.SpecularColor.Y * specularColorTexture.Y, 0.0, 1.0)
            : 1.0;
        double specularColorB = specularEnabled
            ? Math.Clamp(material.SpecularColor.Z * specularColorTexture.Z, 0.0, 1.0)
            : 1.0;
        double sheenR = sheenEnabled
            ? Math.Clamp(material.SheenColor.X * sheenColorTexture.X, 0.0, 1.0)
            : 0.0;
        double sheenG = sheenEnabled
            ? Math.Clamp(material.SheenColor.Y * sheenColorTexture.Y, 0.0, 1.0)
            : 0.0;
        double sheenB = sheenEnabled
            ? Math.Clamp(material.SheenColor.Z * sheenColorTexture.Z, 0.0, 1.0)
            : 0.0;
        double baseAvailableScale = (1.0 - metallic) * (1.0 - clearcoat * 0.04);
        double baseDiffuseScale = baseAvailableScale * (1.0 - transmission);
        double dielectricF0R = dielectricF0 * specularColorR;
        double dielectricF0G = dielectricF0 * specularColorG;
        double dielectricF0B = dielectricF0 * specularColorB;
        double hemisphericalEnergyR =
            GiPrimitiveTransportProfile.SchlickCosineWeightedTransmission *
            (1.0 - dielectricF0R) *
            (1.0 - dielectricF0R);
        double hemisphericalEnergyG =
            GiPrimitiveTransportProfile.SchlickCosineWeightedTransmission *
            (1.0 - dielectricF0G) *
            (1.0 - dielectricF0G);
        double hemisphericalEnergyB =
            GiPrimitiveTransportProfile.SchlickCosineWeightedTransmission *
            (1.0 - dielectricF0B) *
            (1.0 - dielectricF0B);
        double diffuseR = material.Unlit
            ? 0.0
            : albedoR * baseDiffuseScale * hemisphericalEnergyR * (1.0 - sheenR);
        double diffuseG = material.Unlit
            ? 0.0
            : albedoG * baseDiffuseScale * hemisphericalEnergyG * (1.0 - sheenG);
        double diffuseB = material.Unlit
            ? 0.0
            : albedoB * baseDiffuseScale * hemisphericalEnergyB * (1.0 - sheenB);
        bool thinSurface = material.GiTransmissionPolicy == ModelGiTransmissionPolicy.ThinSurface;
        double transmittedR = material.Unlit || !thinSurface
            ? 0.0
            : albedoR * baseAvailableScale * transmission * hemisphericalEnergyR *
              (1.0 - sheenR) * material.ThinTransmissionTint.X;
        double transmittedG = material.Unlit || !thinSurface
            ? 0.0
            : albedoG * baseAvailableScale * transmission * hemisphericalEnergyG *
              (1.0 - sheenG) * material.ThinTransmissionTint.Y;
        double transmittedB = material.Unlit || !thinSurface
            ? 0.0
            : albedoB * baseAvailableScale * transmission * hemisphericalEnergyB *
              (1.0 - sheenB) * material.ThinTransmissionTint.Z;
        double emissionScale = material.EmissiveStrength;
        double emissionR = material.Unlit ? 0.0 : material.Emissive.X * emissionScale * emissiveTexture.X;
        double emissionG = material.Unlit ? 0.0 : material.Emissive.Y * emissionScale * emissiveTexture.Y;
        double emissionB = material.Unlit ? 0.0 : material.Emissive.Z * emissionScale * emissiveTexture.Z;
        // glTF occlusion strength interpolates between neutral visibility and
        // the sampled red channel. Multiplication would incorrectly darken a
        // material that has no occlusion texture whenever strength is below 1.
        double occlusionStrength = Math.Clamp(material.AmbientOcclusion, 0.0, 1.0);
        double ambientOcclusion = 1.0 + occlusionStrength * (occlusionTexture.X - 1.0);
        double coverage;
        switch (material.AlphaMode)
        {
            case ModelAlphaMode.Mask:
                if (!double.IsFinite(material.AlphaCutoff))
                {
                    validity &= ~GiPrimitiveTransportProfileValidity.AlphaCoverage;
                    coverage = 0.0;
                }
                else
                {
                    coverage = alpha >= material.AlphaCutoff ? 1.0 : 0.0;
                }
                break;
            case ModelAlphaMode.Blend:
                coverage = Math.Clamp(alpha, 0.0, 1.0);
                break;
            case ModelAlphaMode.Opaque:
                coverage = 1.0;
                break;
            default:
                validity &= ~GiPrimitiveTransportProfileValidity.AlphaCoverage;
                coverage = 0.0;
                break;
        }
        if (!AreFinite(
                albedoR, albedoG, albedoB, alpha, metallic, roughness,
                clearcoat, transmission, specularFactor, dielectricF0) ||
            !AreFinite(
                specularColorR, specularColorG, specularColorB,
                sheenR, sheenG, sheenB,
                emissionR, emissionG, emissionB, ambientOcclusion))
        {
            validity &= ~GiPrimitiveTransportProfileValidity.Finite;
            if (!AreFinite(albedoR, albedoG, albedoB))
                validity &= ~GiPrimitiveTransportProfileValidity.Diffuse;
            if (!double.IsFinite(alpha) && material.AlphaMode != ModelAlphaMode.Opaque)
                validity &= ~GiPrimitiveTransportProfileValidity.AlphaCoverage;
            if (!AreFinite(metallic, roughness))
                validity &= ~(GiPrimitiveTransportProfileValidity.MetallicRoughness | GiPrimitiveTransportProfileValidity.Diffuse);
            if (!AreFinite(
                    clearcoat, transmission, specularFactor, dielectricF0,
                    specularColorR, specularColorG, specularColorB,
                    sheenR, sheenG, sheenB))
            {
                validity &= ~GiPrimitiveTransportProfileValidity.Diffuse;
            }
            if (!AreFinite(emissionR, emissionG, emissionB))
                validity &= ~GiPrimitiveTransportProfileValidity.Emission;
            if (!double.IsFinite(ambientOcclusion))
                validity &= ~GiPrimitiveTransportProfileValidity.AmbientOcclusion;
        }
        if (diffuseR is < 0.0 or > 1.0 ||
            diffuseG is < 0.0 or > 1.0 ||
            diffuseB is < 0.0 or > 1.0)
        {
            validity &= ~GiPrimitiveTransportProfileValidity.Diffuse;
        }
        if (metallic is < 0.0 or > 1.0 || roughness is < 0.0 or > 1.0)
            validity &= ~(GiPrimitiveTransportProfileValidity.MetallicRoughness | GiPrimitiveTransportProfileValidity.Diffuse);
        if (ambientOcclusion is < 0.0 or > 1.0)
            validity &= ~GiPrimitiveTransportProfileValidity.AmbientOcclusion;
        if (emissionR is < 0.0 or > 65504.0 ||
            emissionG is < 0.0 or > 65504.0 ||
            emissionB is < 0.0 or > 65504.0)
        {
            validity &= ~GiPrimitiveTransportProfileValidity.Emission;
        }

        double normalX = (normalTexture.X * 2.0 - 1.0) * material.NormalScale;
        double normalY = (normalTexture.Y * 2.0 - 1.0) * material.NormalScale;
        double normalZ = normalTexture.Z * 2.0 - 1.0;
        double normalLength = Math.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);
        if (!double.IsFinite(normalLength))
        {
            validity &= ~(GiPrimitiveTransportProfileValidity.NormalVariance | GiPrimitiveTransportProfileValidity.Finite);
            normalX = 0.0;
            normalY = 0.0;
            normalZ = 1.0;
        }
        else if (normalLength <= 1e-20)
        {
            normalX = 0.0;
            normalY = 0.0;
            normalZ = 1.0;
        }
        else
        {
            normalX /= normalLength;
            normalY /= normalLength;
            normalZ /= normalLength;
        }

        return new MaterialSample(
            SanitizeUnit(diffuseR),
            SanitizeUnit(diffuseG),
            SanitizeUnit(diffuseB),
            SanitizeUnit(transmittedR),
            SanitizeUnit(transmittedG),
            SanitizeUnit(transmittedB),
            SanitizeUnit(alpha),
            SanitizeHdr(emissionR),
            SanitizeHdr(emissionG),
            SanitizeHdr(emissionB),
            SanitizeUnit(emissiveTexture.X),
            SanitizeUnit(emissiveTexture.Y),
            SanitizeUnit(emissiveTexture.Z),
            SanitizeUnit(ambientOcclusion),
            SanitizeUnit(coverage),
            SanitizeUnit(metallic),
            SanitizeUnit(roughness),
            normalX,
            normalY,
            normalZ);
    }

    private static TextureTransportVector4 SampleTexture(
        ModelSubMesh subMesh,
        ModelTextureSlot? binding,
        TextureTransportImage? image,
        uint i0,
        uint i1,
        uint i2,
        double b0,
        double b1,
        double b2,
        TextureTransportVector4 neutral,
        GiPrimitiveTransportProfileValidity affected,
        ref GiPrimitiveTransportProfileValidity validity)
    {
        if (binding?.Source is null)
            return neutral;
        if (image is null || !image.Statistics.IsValid ||
            binding.TexCoordSet is < 0 or > 1 ||
            !AreFinite(binding.Offset.X, binding.Offset.Y, binding.Scale.X, binding.Scale.Y, binding.RotationRadians))
        {
            validity &= ~(affected | GiPrimitiveTransportProfileValidity.TextureSamplingComplete);
            return neutral;
        }

        Vector2[] coordinates = binding.TexCoordSet == 1 ? subMesh.TexCoords1 : subMesh.TexCoords;
        if (coordinates.Length != subMesh.Vertices.Length)
        {
            validity &= ~(affected | GiPrimitiveTransportProfileValidity.TextureSamplingComplete);
            return neutral;
        }
        Vector2 uv = Interpolate(coordinates[i0], coordinates[i1], coordinates[i2], b0, b1, b2);
        if (!AreFinite(uv.X, uv.Y))
        {
            validity &= ~(affected | GiPrimitiveTransportProfileValidity.TextureSamplingComplete);
            return neutral;
        }
        return image.Sample(binding, uv);
    }

    private static TextureTransportVector4 SampleActiveTexture(
        bool active,
        ModelSubMesh subMesh,
        ModelTextureSlot? binding,
        TextureTransportImage? image,
        uint i0,
        uint i1,
        uint i2,
        double b0,
        double b1,
        double b2,
        GiPrimitiveTransportProfileValidity affected,
        ref GiPrimitiveTransportProfileValidity validity) => active
        ? SampleTexture(
            subMesh, binding, image, i0, i1, i2, b0, b1, b2,
            TextureTransportVector4.One, affected, ref validity)
        : TextureTransportVector4.One;

    private static TextureTransportVector4 InterpolateVertexColor(
        ModelSubMesh subMesh,
        uint i0,
        uint i1,
        uint i2,
        double b0,
        double b1,
        double b2,
        GiPrimitiveTransportProfileValidity affected,
        ref GiPrimitiveTransportProfileValidity validity)
    {
        if (subMesh.VertexColors.Length == 0)
            return TextureTransportVector4.One;
        if (subMesh.VertexColors.Length != subMesh.Vertices.Length)
        {
            validity &= ~affected;
            return TextureTransportVector4.One;
        }
        Vector4 c0 = subMesh.VertexColors[i0];
        Vector4 c1 = subMesh.VertexColors[i1];
        Vector4 c2 = subMesh.VertexColors[i2];
        var result = new TextureTransportVector4(
            c0.X * b0 + c1.X * b1 + c2.X * b2,
            c0.Y * b0 + c1.Y * b1 + c2.Y * b2,
            c0.Z * b0 + c1.Z * b1 + c2.Z * b2,
            c0.W * b0 + c1.W * b1 + c2.W * b2);
        if (!AreFinite(result.X, result.Y, result.Z, result.W))
        {
            validity &= ~affected;
            return TextureTransportVector4.One;
        }
        return result;
    }

    private static Vector2 Interpolate(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        double b0,
        double b1,
        double b2) => new(
        (float)(p0.X * b0 + p1.X * b1 + p2.X * b2),
        (float)(p0.Y * b0 + p1.Y * b1 + p2.Y * b2));

    private static double TriangleArea(Vector3 p0, Vector3 p1, Vector3 p2)
    {
        double ax = p1.X - p0.X;
        double ay = p1.Y - p0.Y;
        double az = p1.Z - p0.Z;
        double bx = p2.X - p0.X;
        double by = p2.Y - p0.Y;
        double bz = p2.Z - p0.Z;
        double cx = ay * bz - az * by;
        double cy = az * bx - ax * bz;
        double cz = ax * by - ay * bx;
        return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    private static double EstimateError(IntegratedResult full, IntegratedResult centroid)
    {
        double error = 0.0;
        error = Math.Max(error, Difference(full.Diffuse, centroid.Diffuse));
        error = Math.Max(error, Difference(full.Emission, centroid.Emission));
        error = Math.Max(error, Math.Abs(full.AmbientOcclusion - centroid.AmbientOcclusion));
        error = Math.Max(error, Math.Abs(full.Coverage - centroid.Coverage));
        error = Math.Max(error, Math.Abs(full.Metallic - centroid.Metallic));
        error = Math.Max(error, Math.Abs(full.Roughness - centroid.Roughness));
        error = Math.Max(error, Math.Abs(full.NormalVariance - centroid.NormalVariance));
        return error;
    }

    private static double Difference(TextureTransportVector4 first, TextureTransportVector4 second) =>
        Math.Max(
            Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y)),
            Math.Max(Math.Abs(first.Z - second.Z), Math.Abs(first.W - second.W)));

    private static ulong[] GetTextureHashes(GiPrimitiveTextureInputs textures) =>
    [
        textures.BaseColor?.Statistics.SourceContentHash ?? 0,
        textures.MetallicRoughness?.Statistics.SourceContentHash ?? 0,
        textures.Occlusion?.Statistics.SourceContentHash ?? 0,
        textures.Emissive?.Statistics.SourceContentHash ?? 0,
        textures.Normal?.Statistics.SourceContentHash ?? 0,
        textures.Clearcoat?.Statistics.SourceContentHash ?? 0,
        textures.SheenColor?.Statistics.SourceContentHash ?? 0,
        textures.Transmission?.Statistics.SourceContentHash ?? 0,
        textures.Specular?.Statistics.SourceContentHash ?? 0,
        textures.SpecularColor?.Statistics.SourceContentHash ?? 0
    ];

    private static ulong ComputeInputHash(
        int subMeshIndex,
        ModelSubMesh subMesh,
        ModelMaterial material,
        ReadOnlySpan<ulong> textureHashes)
    {
        var hash = new StableHash();
        hash.Add(GiPrimitiveTransportProfile.CurrentAlgorithmVersion);
        hash.Add(subMeshIndex);
        hash.Add(subMesh.Name);
        hash.Add(subMesh.MaterialIndex);
        hash.Add(subMesh.SkinIndex);
        foreach (Vector3 value in subMesh.Vertices)
        {
            hash.Add(value.X);
            hash.Add(value.Y);
            hash.Add(value.Z);
        }
        foreach (uint value in subMesh.Indices)
            hash.Add(value);
        Add(hash, subMesh.TexCoords);
        Add(hash, subMesh.TexCoords1);
        foreach (Vector4 value in subMesh.VertexColors)
        {
            hash.Add(value.X);
            hash.Add(value.Y);
            hash.Add(value.Z);
            hash.Add(value.W);
        }
        hash.Add(material.Albedo.X);
        hash.Add(material.Albedo.Y);
        hash.Add(material.Albedo.Z);
        hash.Add(material.Albedo.W);
        hash.Add(material.Emissive.X);
        hash.Add(material.Emissive.Y);
        hash.Add(material.Emissive.Z);
        hash.Add(material.EmissiveStrength);
        hash.Add(material.Metallic);
        hash.Add(material.Roughness);
        hash.Add(material.AmbientOcclusion);
        hash.Add(material.NormalScale);
        hash.Add(material.FeatureFlags);
        hash.Add(material.Unlit ? 1u : 0u);
        hash.Add(material.ClearcoatFactor);
        hash.Add(material.SheenColor.X);
        hash.Add(material.SheenColor.Y);
        hash.Add(material.SheenColor.Z);
        hash.Add(material.SheenColor.W);
        hash.Add(material.TransmissionFactor);
        hash.Add((uint)material.GiTransmissionPolicy);
        hash.Add((uint)material.GiCausticCasterPolicy);
        hash.Add((uint)material.OpticalBoundaryKind);
        hash.Add(material.ThinTransmissionTint.X);
        hash.Add(material.ThinTransmissionTint.Y);
        hash.Add(material.ThinTransmissionTint.Z);
        hash.Add(material.Ior);
        hash.Add(material.ThicknessFactor);
        hash.Add(material.AttenuationDistance);
        hash.Add(material.AttenuationColor.X);
        hash.Add(material.AttenuationColor.Y);
        hash.Add(material.AttenuationColor.Z);
        hash.Add(material.WaterNormalVelocity0.X);
        hash.Add(material.WaterNormalVelocity0.Y);
        hash.Add(material.WaterNormalVelocity1.X);
        hash.Add(material.WaterNormalVelocity1.Y);
        hash.Add(material.WaterNormalUvScale0);
        hash.Add(material.WaterNormalUvScale1);
        hash.Add(material.Dispersion);
        hash.Add(material.SpecularFactor);
        hash.Add(material.SpecularColor.X);
        hash.Add(material.SpecularColor.Y);
        hash.Add(material.SpecularColor.Z);
        hash.Add(material.SpecularColor.W);
        hash.Add((uint)material.AlphaMode);
        hash.Add(material.AlphaCutoff);
        Add(hash, material.BaseColorTexture);
        Add(hash, material.MetallicRoughnessTexture);
        Add(hash, material.OcclusionTexture);
        Add(hash, material.EmissiveTexture);
        Add(hash, material.NormalTexture);
        Add(hash, material.ClearcoatTexture);
        Add(hash, material.SheenColorTexture);
        Add(hash, material.TransmissionTexture);
        Add(hash, material.SpecularTexture);
        Add(hash, material.SpecularColorTexture);
        foreach (ulong value in textureHashes)
            hash.Add(value);
        return hash.Value;
    }

    private static void Add(StableHash hash, IEnumerable<Vector2> values)
    {
        foreach (Vector2 value in values)
        {
            hash.Add(value.X);
            hash.Add(value.Y);
        }
    }

    private static void Add(StableHash hash, ModelTextureSlot? binding)
    {
        if (binding is null)
        {
            hash.Add(0u);
            return;
        }
        hash.Add(1u);
        hash.Add(binding.TexCoordSet);
        hash.Add(binding.Offset.X);
        hash.Add(binding.Offset.Y);
        hash.Add(binding.Scale.X);
        hash.Add(binding.Scale.Y);
        hash.Add(binding.RotationRadians);
        hash.Add((uint)binding.ColorSpace);
        hash.Add((uint)binding.Sampler.WrapU);
        hash.Add((uint)binding.Sampler.WrapV);
        hash.Add((uint)binding.Sampler.MinFilter);
        hash.Add((uint)binding.Sampler.MagFilter);
        hash.Add((uint)binding.Sampler.MipFilter);
        hash.Add(binding.Sampler.MaxAnisotropy);
    }

    private static bool AreFinite(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b);

    private static bool AreFinite(double a, double b, double c) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c);

    private static bool AreFinite(double a, double b, double c, double d) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d);

    private static bool AreFinite(double a, double b, double c, double d, double e) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) &&
        double.IsFinite(d) && double.IsFinite(e);

    private static bool HasFeature(ModelMaterial material, uint feature) =>
        (material.FeatureFlags & feature) != 0;

    private static bool HasActiveTexture(ModelMaterial material, uint feature, ModelTextureSlot? binding) =>
        HasFeature(material, feature) && binding?.Source is not null;

    private static bool AreFinite(
        double a,
        double b,
        double c,
        double d,
        double e,
        double f,
        double g,
        double h,
        double i,
        double j) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) &&
        double.IsFinite(d) && double.IsFinite(e) && double.IsFinite(f) &&
        double.IsFinite(g) && double.IsFinite(h) && double.IsFinite(i) &&
        double.IsFinite(j);

    private static bool AreFinite(
        double a,
        double b,
        double c,
        double d,
        double e,
        double f,
        double g,
        double h,
        double i,
        double j,
        double k,
        double l,
        double m) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) &&
        double.IsFinite(d) && double.IsFinite(e) && double.IsFinite(f) &&
        double.IsFinite(g) && double.IsFinite(h) && double.IsFinite(i) &&
        double.IsFinite(j) && double.IsFinite(k) && double.IsFinite(l) &&
        double.IsFinite(m);

    private static double SanitizeUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
    private static double SanitizeHdr(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 65504.0) : 0.0;

    private readonly record struct QuadraturePoint(double B0, double B1, double B2, double Weight);
    private readonly record struct PackageEmissiveCandidate(
        int ProfileIndex,
        int SubMeshIndex,
        GiPrimitiveEmissiveTriangleRecord Record);
    private readonly record struct EmissiveTriangleProfileData(
        GiPrimitiveEmissiveTriangleFlags Flags,
        int SourceTriangleCount,
        int CandidateTriangleCount,
        GiPrimitiveEmissiveTriangleRecord[] Records,
        double TotalImportance,
        double RetainedImportance,
        double OmittedImportance,
        TextureTransportVector4 CookedEmissiveFactor,
        double CookedEmissiveStrength,
        double CookedBaseAlphaFactor,
        ModelAlphaMode CookedAlphaMode,
        double CookedAlphaCutoff,
        bool CookedEmissionEligible,
        GiPrimitiveTextureBindingSnapshot BaseColorSamplingBinding,
        GiPrimitiveTextureBindingSnapshot EmissiveSamplingBinding);

    private readonly record struct MaterialSample(
        double DiffuseR,
        double DiffuseG,
        double DiffuseB,
        double TransmittedDiffuseR,
        double TransmittedDiffuseG,
        double TransmittedDiffuseB,
        double Alpha,
        double EmissionR,
        double EmissionG,
        double EmissionB,
        double EmissiveTextureR,
        double EmissiveTextureG,
        double EmissiveTextureB,
        double AmbientOcclusion,
        double Coverage,
        double Metallic,
        double Roughness,
        double NormalX,
        double NormalY,
        double NormalZ);

    private sealed class EmissiveTriangleAccumulator
    {
        private double _weight;
        private double _coveredWeight;
        private double _textureR;
        private double _textureG;
        private double _textureB;

        public void Add(MaterialSample sample, double weight)
        {
            _weight += weight;
            double coveredWeight = sample.Coverage * weight;
            _coveredWeight += coveredWeight;
            _textureR += sample.EmissiveTextureR * coveredWeight;
            _textureG += sample.EmissiveTextureG * coveredWeight;
            _textureB += sample.EmissiveTextureB * coveredWeight;
        }

        public bool TryFinish(
            int triangleIndex,
            double area,
            out GiPrimitiveEmissiveTriangleRecord record)
        {
            record = null!;
            if (_weight <= 1e-20 || _coveredWeight <= 1e-20)
                return false;

            double inverseCoveredWeight = 1.0 / _coveredWeight;
            double coverage = Math.Clamp(_coveredWeight / _weight, 0.0, 1.0);
            var texture = new TextureTransportVector4(
                Math.Clamp(_textureR * inverseCoveredWeight, 0.0, 1.0),
                Math.Clamp(_textureG * inverseCoveredWeight, 0.0, 1.0),
                Math.Clamp(_textureB * inverseCoveredWeight, 0.0, 1.0),
                1.0);
            double importance = area * coverage *
                (0.2126 * texture.X + 0.7152 * texture.Y + 0.0722 * texture.Z);
            if (!double.IsFinite(importance) || importance <= 1e-20)
                return false;

            record = new GiPrimitiveEmissiveTriangleRecord
            {
                TriangleIndex = triangleIndex,
                LocalSurfaceArea = area,
                Coverage = coverage,
                CoveredMeanEmissiveTexture = texture,
                CookedImportance = importance
            };
            return true;
        }
    }

    private sealed class Accumulator
    {
        private double _diffuseR;
        private double _diffuseG;
        private double _diffuseB;
        private double _transmittedDiffuseR;
        private double _transmittedDiffuseG;
        private double _transmittedDiffuseB;
        private double _alpha;
        private double _emissionR;
        private double _emissionG;
        private double _emissionB;
        private double _ambientOcclusion;
        private double _coverage;
        private double _metallic;
        private double _roughness;
        private double _normalX;
        private double _normalY;
        private double _normalZ;
        private double _coveredWeight;

        public double Weight { get; private set; }

        public void Add(MaterialSample sample, double weight)
        {
            Weight += weight;
            double coveredWeight = sample.Coverage * weight;
            _coveredWeight += coveredWeight;
            // Compact transport is evaluated only after the authored opacity
            // test accepts a ray/voxel candidate. Store the conditional mean
            // over covered surface so multiplying by AlphaCoverage reproduces
            // E[coverage * transport] even when color/emission and alpha are
            // correlated in the same texture.
            _diffuseR += sample.DiffuseR * coveredWeight;
            _diffuseG += sample.DiffuseG * coveredWeight;
            _diffuseB += sample.DiffuseB * coveredWeight;
            _transmittedDiffuseR += sample.TransmittedDiffuseR * coveredWeight;
            _transmittedDiffuseG += sample.TransmittedDiffuseG * coveredWeight;
            _transmittedDiffuseB += sample.TransmittedDiffuseB * coveredWeight;
            _alpha += sample.Alpha * weight;
            _emissionR += sample.EmissionR * coveredWeight;
            _emissionG += sample.EmissionG * coveredWeight;
            _emissionB += sample.EmissionB * coveredWeight;
            _ambientOcclusion += sample.AmbientOcclusion * coveredWeight;
            _coverage += coveredWeight;
            _metallic += sample.Metallic * coveredWeight;
            _roughness += sample.Roughness * coveredWeight;
            _normalX += sample.NormalX * coveredWeight;
            _normalY += sample.NormalY * coveredWeight;
            _normalZ += sample.NormalZ * coveredWeight;
        }

        public IntegratedResult Finish()
        {
            if (Weight <= 0.0)
                return default;
            double inverseWeight = 1.0 / Weight;
            double inverseCoveredWeight = _coveredWeight > 1e-20 ? 1.0 / _coveredWeight : 0.0;
            double meanNormalX = _normalX * inverseCoveredWeight;
            double meanNormalY = _normalY * inverseCoveredWeight;
            double meanNormalZ = _normalZ * inverseCoveredWeight;
            double normalVariance = Math.Clamp(
                1.0 - (meanNormalX * meanNormalX + meanNormalY * meanNormalY + meanNormalZ * meanNormalZ),
                0.0,
                1.0);
            if (_coveredWeight <= 1e-20)
                normalVariance = 0.0;
            return new IntegratedResult(
                new TextureTransportVector4(
                    _diffuseR * inverseCoveredWeight,
                    _diffuseG * inverseCoveredWeight,
                    _diffuseB * inverseCoveredWeight,
                    _alpha * inverseWeight),
                new TextureTransportVector4(
                    _transmittedDiffuseR * inverseCoveredWeight,
                    _transmittedDiffuseG * inverseCoveredWeight,
                    _transmittedDiffuseB * inverseCoveredWeight,
                    1.0),
                new TextureTransportVector4(
                    _emissionR * inverseCoveredWeight,
                    _emissionG * inverseCoveredWeight,
                    _emissionB * inverseCoveredWeight,
                    1.0),
                _coveredWeight > 1e-20 ? _ambientOcclusion * inverseCoveredWeight : 1.0,
                _coverage * inverseWeight,
                _metallic * inverseCoveredWeight,
                _coveredWeight > 1e-20 ? _roughness * inverseCoveredWeight : 1.0,
                normalVariance);
        }
    }

    private readonly record struct IntegratedResult(
        TextureTransportVector4 Diffuse,
        TextureTransportVector4 TransmittedDiffuse,
        TextureTransportVector4 Emission,
        double AmbientOcclusion,
        double Coverage,
        double Metallic,
        double Roughness,
        double NormalVariance)
    {
        public bool IsFinite =>
            AreFinite(Diffuse.X, Diffuse.Y, Diffuse.Z, Diffuse.W) &&
            AreFinite(
                TransmittedDiffuse.X,
                TransmittedDiffuse.Y,
                TransmittedDiffuse.Z,
                TransmittedDiffuse.W) &&
            AreFinite(Emission.X, Emission.Y, Emission.Z, Emission.W) &&
            AreFinite(AmbientOcclusion, Coverage, Metallic, Roughness, NormalVariance);
    }

    private sealed class StableHash
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public ulong Value { get; private set; } = Offset;

        public void Add(uint value)
        {
            AddByte((byte)value);
            AddByte((byte)(value >> 8));
            AddByte((byte)(value >> 16));
            AddByte((byte)(value >> 24));
        }

        public void Add(int value) => Add(unchecked((uint)value));
        public void Add(ulong value)
        {
            Add((uint)value);
            Add((uint)(value >> 32));
        }
        public void Add(float value) => Add(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        public void Add(string value)
        {
            foreach (char character in value)
                Add(character);
            Add(0u);
        }

        private void AddByte(byte value)
        {
            Value ^= value;
            Value *= Prime;
        }
    }
}
