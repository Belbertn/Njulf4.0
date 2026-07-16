using System.Runtime.InteropServices;

namespace Njulf.Assets.Cooked;

public static class CookedPlatform
{
    public static string Current { get; } = Normalize(RuntimeInformation.RuntimeIdentifier);

    public static string Normalize(string? platform)
    {
        platform = string.IsNullOrWhiteSpace(platform) ? RuntimeInformation.RuntimeIdentifier : platform.Trim().ToLowerInvariant();
        if (platform.StartsWith("win-", StringComparison.Ordinal) ||
            platform.StartsWith("linux-", StringComparison.Ordinal) ||
            platform.StartsWith("osx-", StringComparison.Ordinal))
            return platform;
        throw new ArgumentException($"Unsupported cooked platform '{platform}'. Expected a .NET RID such as win-x64 or linux-x64.", nameof(platform));
    }

    public static string ResolveOutputRoot(string root, string? platform)
    {
        root = Path.GetFullPath(root);
        string normalized = Normalize(platform);
        return string.Equals(Path.GetFileName(root), normalized, StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.Combine(root, normalized);
    }

    public static TextureTargetFormatPolicy ResolveTexturePolicy(string platform, TextureTargetFormatPolicy requested)
    {
        platform = Normalize(platform);
        if (requested != TextureTargetFormatPolicy.AutoBc)
            return requested;
        // BCn is mandatory on the supported desktop Vulkan targets; MoltenVK targets
        // retain RGBA8 until an ASTC encoder/runtime capability profile is added.
        return platform.StartsWith("osx-", StringComparison.Ordinal)
            ? TextureTargetFormatPolicy.Rgba8
            : TextureTargetFormatPolicy.AutoBc;
    }

    public static bool SupportsMeshOptimizer(string platform)
    {
        platform = Normalize(platform);
        return platform is "win-x64" or "linux-x64";
    }
}
