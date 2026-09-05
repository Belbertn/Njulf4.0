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
        Transparent = 4,
        ThickTransmission = 5,
        ThinGlass = 6
    }

    public static class MaterialForwardClassifier
    {
        private const float Epsilon = 0.0001f;

        public static MaterialForwardClass Classify(GPUMaterialData material, MaterialRenderMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            if (metadata.TransmissionPolicy == GiTransmissionPolicy.Volume)
                return MaterialForwardClass.ThickTransmission;
            if (metadata.ShadingModel == MaterialShadingModel.ThinGlass &&
                metadata.TransmissionPolicy == GiTransmissionPolicy.ThinSurface)
                return MaterialForwardClass.ThinGlass;
            if (metadata.IsGeometryDecal || metadata.RenderMode == MaterialRenderMode.Blend)
                return MaterialForwardClass.Transparent;

            if (RequiresExtensionOpaquePath(material, metadata))
                return MaterialForwardClass.FullOpaque;

            return RequiresFullInputOpaquePath(material, metadata)
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

        private static bool RequiresExtensionOpaquePath(
            GPUMaterialData material,
            MaterialRenderMetadata metadata)
        {
            MaterialFeatureFlags featureFlags =
                (MaterialFeatureFlags)material.FeatureFlags;
            // Opaque ThinSurface transmission affects GI transport, not the
            // visible raster lobe (forward.frag's rasterTransmissionEnabled).
            // Keep its payload for GI, but do not require the Full shader when
            // transmission is the only extension. IOR and other visible lobes
            // still require Full, even on a thin transmitting surface.
            bool giOnlyTransmission =
                metadata.TransmissionPolicy == GiTransmissionPolicy.ThinSurface &&
                metadata.ShadingModel != MaterialShadingModel.ThinGlass &&
                (material.TransportFlags & (uint)(
                    GiMaterialTransportFlags.ThinSurfaceTransmission |
                    GiMaterialTransportFlags.ThinGlass)) ==
                (uint)GiMaterialTransportFlags.ThinSurfaceTransmission &&
                (featureFlags & MaterialFeatureFlags.Transmission) != 0;
            if (giOnlyTransmission)
            {
                MaterialFeatureFlags rasterFeatures = featureFlags & ~(
                    MaterialFeatureFlags.Transmission |
                    MaterialFeatureFlags.TransmissionTexture);
                return rasterFeatures.RequiresExtensionData();
            }

            return featureFlags.RequiresExtensionData() ||
                   material.ExtensionDataIndex >= 0;
        }

        private static bool RequiresFullInputOpaquePath(
            GPUMaterialData material,
            MaterialRenderMetadata metadata)
        {
            if (metadata.RenderMode == MaterialRenderMode.Mask ||
                HasNormalMap(material))
            {
                return true;
            }

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
