using System;

namespace Njulf.Rendering.Data
{
    [Flags]
    public enum MaterialFeatureFlags : uint
    {
        None = 0,
        Clearcoat = 1u << 0,
        ClearcoatTexture = 1u << 1,
        ClearcoatRoughnessTexture = 1u << 2,
        ClearcoatNormalTexture = 1u << 3,
        Sheen = 1u << 4,
        SheenColorTexture = 1u << 5,
        SheenRoughnessTexture = 1u << 6,
        Anisotropy = 1u << 7,
        AnisotropyTexture = 1u << 8,
        Transmission = 1u << 9,
        TransmissionTexture = 1u << 10,
        VolumeApproximation = 1u << 11,
        Subsurface = 1u << 12,
        SubsurfaceTexture = 1u << 13,
        EmissiveStrength = 1u << 14
    }

    public static class MaterialFeatureFlagsExtensions
    {
        private const MaterialFeatureFlags ExtensionLightingMask =
            MaterialFeatureFlags.Clearcoat |
            MaterialFeatureFlags.Sheen |
            MaterialFeatureFlags.Anisotropy |
            MaterialFeatureFlags.Transmission |
            MaterialFeatureFlags.VolumeApproximation |
            MaterialFeatureFlags.Subsurface |
            MaterialFeatureFlags.EmissiveStrength;

        public static bool HasAnyExtensionLighting(this MaterialFeatureFlags flags)
        {
            return (flags & ExtensionLightingMask) != MaterialFeatureFlags.None;
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
