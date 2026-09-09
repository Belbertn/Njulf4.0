namespace Njulf.Assets.Cooked;

internal static class TextureCookPlatformPolicy
{

    public static TextureTargetFormatPolicy ResolveTexturePolicy(string platform, TextureTargetFormatPolicy requested)
    {
        platform = CookedPlatform.Normalize(platform);
        if (requested != TextureTargetFormatPolicy.AutoBc)
            return requested;
        // BCn is mandatory on the supported desktop Vulkan targets; MoltenVK targets
        // retain RGBA8 until an ASTC encoder/runtime capability profile is added.
        return platform.StartsWith("osx-", StringComparison.Ordinal)
            ? TextureTargetFormatPolicy.Rgba8
            : TextureTargetFormatPolicy.AutoBc;
    }
}
