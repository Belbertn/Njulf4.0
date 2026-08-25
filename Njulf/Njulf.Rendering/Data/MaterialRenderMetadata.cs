using System;

namespace Njulf.Rendering.Data
{
    public enum MaterialBlendMode : uint
    {
        Opaque = 0,
        Mask = 1,
        AlphaBlend = 2,
        PremultipliedAlpha = 3,
        Additive = 4,
        Multiply = 5
    }

    [Flags]
    public enum MaterialSurfaceFlags : uint
    {
        None = 0,
        DoubleSided = 1 << 0,
        GeometryDecal = 1 << 1,
        ReceivesShadows = 1 << 2,
        WritesMotionVectors = 1 << 3
    }

    public sealed record MaterialRenderMetadata
    {
        public MaterialBlendMode BlendMode { get; init; } = MaterialBlendMode.Opaque;
        public MaterialSurfaceFlags SurfaceFlags { get; init; } = MaterialSurfaceFlags.ReceivesShadows;
        public float AlphaCutoff { get; init; } = 0.5f;
        public MaterialShadingModel ShadingModel { get; init; } = MaterialShadingModel.Pbr;
        public GiParticipationOverride DiffuseGiParticipation { get; init; } = GiParticipationOverride.Default;
        public GiParticipationOverride EmissionGiParticipation { get; init; } = GiParticipationOverride.Default;
        public GiTransmissionPolicy TransmissionPolicy { get; init; } = GiTransmissionPolicy.None;
        public OpticalBoundaryKind OpticalBoundary { get; init; } =
            OpticalBoundaryKind.ClosedVolume;
        public GiCausticCasterPolicy CausticCasterPolicy { get; init; } =
            GiCausticCasterPolicy.Default;
        public int DecalLayer { get; init; }
        public float DecalDepthBias { get; init; }

        public static MaterialRenderMetadata FromGpuMaterial(GPUMaterialData material)
        {
            if (!float.IsFinite(material.NormalScaleBias.Z) ||
                material.NormalScaleBias.Z < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(material),
                    "GPU material alpha cutoff must be finite and non-negative.");
            }

            MaterialRenderMode renderMode = MaterialRenderModeExtensions.FromGpuMaterial(material);
            MaterialSurfaceFlags flags = MaterialSurfaceFlags.ReceivesShadows;
            if (material.NormalScaleBias.W >= 0.5f)
                flags |= MaterialSurfaceFlags.DoubleSided;

            return new MaterialRenderMetadata
            {
                BlendMode = renderMode switch
                {
                    MaterialRenderMode.Mask => MaterialBlendMode.Mask,
                    MaterialRenderMode.Blend => MaterialBlendMode.AlphaBlend,
                    _ => MaterialBlendMode.Opaque
                },
                SurfaceFlags = flags,
                AlphaCutoff = material.NormalScaleBias.Z,
                TransmissionPolicy = (material.TransportFlags &
                                      (uint)GiMaterialTransportFlags.VolumeTransmission) != 0u
                    ? GiTransmissionPolicy.Volume
                    : (material.TransportFlags &
                       (uint)GiMaterialTransportFlags.ThinSurfaceTransmission) != 0u
                        ? GiTransmissionPolicy.ThinSurface
                    : (material.TransportFlags &
                       (uint)GiMaterialTransportFlags.TransmissionRemovesOpaqueDiffuse) != 0u
                        ? GiTransmissionPolicy.Unsupported
                        : GiTransmissionPolicy.None,
                OpticalBoundary = (material.TransportFlags &
                                   (uint)GiMaterialTransportFlags.WaterSurfaceBoundary) != 0u
                    ? OpticalBoundaryKind.WaterSurface
                    : OpticalBoundaryKind.ClosedVolume
            };
        }

        public MaterialRenderMode RenderMode => BlendMode switch
        {
            MaterialBlendMode.Mask => MaterialRenderMode.Mask,
            MaterialBlendMode.AlphaBlend or
                MaterialBlendMode.PremultipliedAlpha or
                MaterialBlendMode.Additive or
                MaterialBlendMode.Multiply => MaterialRenderMode.Blend,
            _ => MaterialRenderMode.Opaque
        };

        public bool IsGeometryDecal => SurfaceFlags.HasFlag(MaterialSurfaceFlags.GeometryDecal);
        public bool ReceivesShadows => SurfaceFlags.HasFlag(MaterialSurfaceFlags.ReceivesShadows);
        public bool DoubleSided => SurfaceFlags.HasFlag(MaterialSurfaceFlags.DoubleSided);
        public bool IsUnlit => ShadingModel == MaterialShadingModel.Unlit;
        public bool IsVolumeTransmission =>
            TransmissionPolicy == GiTransmissionPolicy.Volume;
        public bool ReceivesIndirectDiffuse =>
            DiffuseGiParticipation != GiParticipationOverride.Disabled &&
            ShadingModel is not MaterialShadingModel.Unlit and not MaterialShadingModel.Decal;
        public bool EmitsIntoGi =>
            EmissionGiParticipation == GiParticipationOverride.Enabled ||
            EmissionGiParticipation == GiParticipationOverride.Default &&
            ShadingModel != MaterialShadingModel.Unlit;
    }
}
