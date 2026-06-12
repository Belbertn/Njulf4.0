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
        public int DecalLayer { get; init; }
        public float DecalDepthBias { get; init; }

        public static MaterialRenderMetadata FromGpuMaterial(GPUMaterialData material)
        {
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
                AlphaCutoff = Math.Clamp(material.NormalScaleBias.Z, 0f, 1f)
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
    }
}
