using System;
using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Graphics
{
}

namespace Njulf.Rendering.Data
{
    public enum GiTransportProfileQuality : byte
    {
        Invalid = 0,
        MaterialFactors = 1,
        TextureStatistics = 2,
        PrimitiveSurfaceSampling = 3
    }

    [Flags]
    public enum GiMaterialTransportFlags : uint
    {
        None = 0,
        BaseStatisticsValid = 1u << 0,
        DiffuseProfileValid = 1u << 1,
        EmissionProfileValid = 1u << 2,
        AlphaProfileValid = 1u << 3,
        NormalProfileValid = 1u << 4,
        Unlit = 1u << 5,
        DoubleSided = 1u << 6,
        TransmissionRemovesOpaqueDiffuse = 1u << 7,
        EmitsIntoGi = 1u << 8,
        ReceivesIndirectDiffuse = 1u << 9,
        ReflectsIndirectDiffuse = 1u << 10,
        HasBaseColorTexture = 1u << 11,
        HasMetallicRoughnessTexture = 1u << 12,
        HasOcclusionTexture = 1u << 13,
        HasEmissiveTexture = 1u << 14,
        LegacyV1Fallback = 1u << 15,
        UnsupportedTransmission = 1u << 16,
        /// <summary>
        /// A compact consumer must sample the detailed material bindings when one
        /// of the profiles required by this material is invalid. This is an
        /// explicit correctness policy, not a claim that guessed averages are
        /// valid.
        /// </summary>
        CompactTextureFallback = 1u << 17,
        /// <summary>
        /// The raster surface is an explicitly authored geometry decal. This flag
        /// is consumed by forward shading so decals can use an independent
        /// indirect-lighting policy while sharing the transparent draw path.
        /// </summary>
        GeometryDecal = 1u << 18,
        ThinSurfaceTransmission = 1u << 19,
        TransmissionProfileValid = 1u << 20,
        HasTransmissionTexture = 1u << 21,
        VolumeTransmission = 1u << 22,
        WaterSurfaceBoundary = 1u << 23,
        QualityShift = 24,
        QualityMask = 0x0f00_0000u,
        /// <summary>
        /// An extension payload is present solely or additionally for optical
        /// boundary/caustic policy, even when no glTF lighting extension flag is set.
        /// </summary>
        OpticalPolicyPayload = 1u << 28,
        /// <summary>
        /// Distinguishes visible zero-thickness glass from opaque thin-surface
        /// transport such as curtains. This bit is consumed by raster shading and
        /// does not alter the canonical transmitted-diffuse GI lobe.
        /// </summary>
        ThinGlass = 1u << 29
    }

    [Flags]
    public enum MaterialChangeMask : uint
    {
        None = 0,
        RasterAppearance = 1u << 0,
        DiffuseTransport = 1u << 1,
        Emission = 1u << 2,
        AlphaCoverage = 1u << 3,
        Sidedness = 1u << 4,
        ShadingModel = 1u << 5,
        FarField = 1u << 6,
        TextureDependencies = 1u << 7,
        AccelerationStructure = 1u << 8,
        /// <summary>
        /// CPU-only automatic-planar receiver policy. This never changes the GPU
        /// material layout or forward shader classification.
        /// </summary>
        AutomaticPlanarReflection = 1u << 9,
        All = RasterAppearance |
              DiffuseTransport |
              Emission |
              AlphaCoverage |
              Sidedness |
              ShadingModel |
              FarField |
              TextureDependencies |
              AccelerationStructure |
              AutomaticPlanarReflection
    }

    /// <summary>
    /// Canonical compact transport profile. Validity is represented only by flags;
    /// a zero value is always a valid physical value when its flag is set.
    /// </summary>
    public sealed record GiMaterialTransportProfile
    {
        public static GiMaterialTransportProfile Invalid { get; } = new();

        public uint AlgorithmVersion { get; init; }
        public ulong SourceContentHash { get; init; }
        public ulong PrimitiveContentHash { get; init; }
        public GiMaterialTransportFlags Flags { get; init; }
        public GiTransportProfileQuality Quality { get; init; }
        public Vector3 MeanDiffuseReflectance { get; init; } = Vector3.Zero;
        public Vector3 MeanTransmittedDiffuseReflectance { get; init; } = Vector3.Zero;
        public Vector3 MeanEmissiveRadiance { get; init; } = Vector3.Zero;
        public float EmissiveImportance { get; init; }
        public EmissivePhotometricUnit EmissiveUnit { get; init; }
        public float EffectiveEmissiveScale { get; init; } = 1f;
        public float EmissiveArtisticMultiplier { get; init; } = 1f;
        public float AverageEmissiveLuminanceNits { get; init; }
        /// <summary>
        /// Source-resolution peak estimate derived from cooked texture luminance
        /// statistics. <see cref="PeakEmissiveLuminanceValid"/> distinguishes a
        /// physical zero from unavailable texture statistics.
        /// </summary>
        public float PeakEmissiveLuminanceNits { get; init; }
        public bool PeakEmissiveLuminanceValid { get; init; }
        public float MeanMaterialOcclusion { get; init; } = 1f;
        public float AlphaCoverage { get; init; } = 1f;
        public float MeanMetallic { get; init; }
        public float MeanRoughness { get; init; } = 1f;
        public float NormalVariance { get; init; }

        public bool Has(GiMaterialTransportFlags flag) => (Flags & flag) == flag;
    }

    public readonly record struct MaterialAspectRevisions(
        uint Material,
        uint DiffuseTransport,
        uint Emission,
        uint AlphaCoverage,
        uint Sidedness,
        uint ShadingModel,
        uint FarField)
    {
        public static MaterialAspectRevisions Initial { get; } = new(1, 1, 1, 1, 1, 1, 1);
    }

    public sealed record MaterialChangedEvent(
        MaterialHandle Handle,
        MaterialChangeMask ChangeMask,
        MaterialAspectRevisions Revisions);

    /// <summary>
    /// CPU representation of the shared shader surface contract.
    /// </summary>
    public readonly record struct GiSurfaceSample(
        Vector3 CanonicalGeometricNormal,
        Vector3 GeometricNormal,
        Vector3 ShadingNormal,
        Vector3 DirectionalDiffuseBase,
        Vector3 DielectricF0,
        Vector3 DiffuseReflectance,
        Vector3 TransmittedDiffuseReflectance,
        Vector3 EmissiveRadiance,
        float MaterialOcclusion,
        float Opacity,
        float Metallic,
        float Roughness,
        GiMaterialTransportFlags Flags);
}
