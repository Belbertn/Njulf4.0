using System;
using Njulf.Core.Math;
using Njulf.Rendering.Descriptors;

namespace Njulf.Rendering.Data
{
    public enum MaterialForwardClass : byte
    {
        SimpleOpaque = 0,
        SimpleOpaqueNormal = 1,
        FullOpaque = 2,
        Masked = 3,
        Transparent = 4
    }

    public static class MaterialForwardClassifier
    {
        private const float Epsilon = 0.0001f;

        public static MaterialForwardClass Classify(GPUMaterialData material, MaterialRenderMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            if (metadata.IsGeometryDecal || metadata.RenderMode == MaterialRenderMode.Blend)
                return MaterialForwardClass.Transparent;
            if (metadata.RenderMode == MaterialRenderMode.Mask)
                return MaterialForwardClass.Masked;

            if (RequiresFullOpaquePath(material))
                return MaterialForwardClass.FullOpaque;

            return HasNormalMap(material)
                ? MaterialForwardClass.SimpleOpaqueNormal
                : MaterialForwardClass.SimpleOpaque;
        }

        public static bool IsSimpleOpaque(MaterialForwardClass materialClass)
        {
            return materialClass == MaterialForwardClass.SimpleOpaque;
        }

        public static bool IsSimpleNormalOpaque(MaterialForwardClass materialClass)
        {
            return materialClass == MaterialForwardClass.SimpleOpaqueNormal;
        }

        private static bool RequiresFullOpaquePath(GPUMaterialData material)
        {
            MaterialFeatureFlags featureFlags = (MaterialFeatureFlags)material.FeatureFlags;
            // BC5 is a base normal-map decode mode already supported by the
            // compact opaque shader. Treating it as extension lighting routed
            // nearly every cooked Sponza material through the largest variant.
            MaterialFeatureFlags unsupportedSimpleFlags =
                featureFlags & ~MaterialFeatureFlags.CompressedNormalBc5;
            if (unsupportedSimpleFlags != MaterialFeatureFlags.None || material.ExtensionDataIndex >= 0)
                return true;

            if (!IsIdentityTransform(material.BaseColorOffsetScale, material.TextureRotations.X) ||
                !IsIdentityTransform(material.NormalOffsetScale, material.TextureRotations.Y) ||
                !IsIdentityTransform(material.MetallicRoughnessOffsetScale, material.TextureRotations.Z) ||
                !IsIdentityTransform(material.EmissiveOffsetScale, material.TextureRotations.W))
                return true;

            return !IsDefaultTexCoordSet(material.TextureTexCoordSets);
        }

        private static bool HasNormalMap(GPUMaterialData material)
        {
            return material.NormalTextureIndex != BindlessIndex.DefaultNormalTexture &&
                   material.NormalScaleBias.X > Epsilon;
        }

        private static bool IsDefaultTexCoordSet(Vector4 texCoordSets)
        {
            return Math.Abs(texCoordSets.X) <= Epsilon &&
                   Math.Abs(texCoordSets.Y) <= Epsilon &&
                   Math.Abs(texCoordSets.Z) <= Epsilon &&
                   Math.Abs(texCoordSets.W) <= Epsilon;
        }

        private static bool IsIdentityTransform(Vector4 offsetScale, float rotationRadians)
        {
            return Math.Abs(offsetScale.X) <= Epsilon &&
                   Math.Abs(offsetScale.Y) <= Epsilon &&
                   Math.Abs(offsetScale.Z - 1f) <= Epsilon &&
                   Math.Abs(offsetScale.W - 1f) <= Epsilon &&
                   Math.Abs(rotationRadians) <= Epsilon;
        }
    }
}
