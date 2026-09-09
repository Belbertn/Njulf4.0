using System;
using Njulf.Graphics;

namespace Njulf.Graphics
{
}

namespace Njulf.Rendering.Data
{
    public static class MaterialFeatureFlagsExtensions
    {
        private const MaterialFeatureFlags ExtensionLightingMask =
            MaterialFeatureFlags.Clearcoat |
            MaterialFeatureFlags.Sheen |
            MaterialFeatureFlags.Anisotropy |
            MaterialFeatureFlags.Transmission |
            MaterialFeatureFlags.VolumeApproximation |
            MaterialFeatureFlags.Subsurface |
            MaterialFeatureFlags.EmissiveStrength |
            MaterialFeatureFlags.Specular |
            MaterialFeatureFlags.Iridescence |
            MaterialFeatureFlags.Dispersion |
            MaterialFeatureFlags.Ior;

        private const MaterialFeatureFlags ExtensionPayloadMask =
            MaterialFeatureFlags.Clearcoat |
            MaterialFeatureFlags.ClearcoatTexture |
            MaterialFeatureFlags.ClearcoatRoughnessTexture |
            MaterialFeatureFlags.ClearcoatNormalTexture |
            MaterialFeatureFlags.Sheen |
            MaterialFeatureFlags.SheenColorTexture |
            MaterialFeatureFlags.SheenRoughnessTexture |
            MaterialFeatureFlags.Anisotropy |
            MaterialFeatureFlags.AnisotropyTexture |
            MaterialFeatureFlags.Transmission |
            MaterialFeatureFlags.TransmissionTexture |
            MaterialFeatureFlags.VolumeApproximation |
            MaterialFeatureFlags.Subsurface |
            MaterialFeatureFlags.SubsurfaceTexture |
            MaterialFeatureFlags.EmissiveStrength |
            MaterialFeatureFlags.Specular |
            MaterialFeatureFlags.SpecularTexture |
            MaterialFeatureFlags.SpecularColorTexture |
            MaterialFeatureFlags.Iridescence |
            MaterialFeatureFlags.IridescenceTexture |
            MaterialFeatureFlags.IridescenceThicknessTexture |
            MaterialFeatureFlags.Dispersion |
            MaterialFeatureFlags.Ior;

        public static bool HasAnyExtensionLighting(this MaterialFeatureFlags flags)
        {
            return (flags & ExtensionLightingMask) != MaterialFeatureFlags.None;
        }

        public static bool RequiresExtensionData(this MaterialFeatureFlags flags)
        {
            return (flags & ExtensionPayloadMask) != MaterialFeatureFlags.None;
        }

        public static bool RequiresTransparentPass(this MaterialFeatureFlags flags)
        {
            return (flags & MaterialFeatureFlags.Transmission) != MaterialFeatureFlags.None;
        }

        public static bool RequiresOpaqueSceneColorInput(this MaterialFeatureFlags flags)
        {
            return (flags & MaterialFeatureFlags.Transmission) != MaterialFeatureFlags.None;
        }
    }
}
