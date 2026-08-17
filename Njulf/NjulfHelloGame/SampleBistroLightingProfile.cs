using System.Numerics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Canonicalizes the Bistro FBX directional light into the renderer's
/// color-times-intensity representation and makes that single sun shadowed.
/// </summary>
internal static class SampleBistroLightingProfile
{
    // Values authored by BistroExterior.fbx. Assimp exposes the radiometric
    // magnitude in RGB while leaving scalar intensity at one.
    internal static Vector3 SourceDirection { get; } =
        Vector3.Normalize(new Vector3(0.0f, -0.87636626f, -0.48164532f));

    internal static Vector3 SourceRadiance { get; } =
        new(110.100006f, 87.414825f, 56.481304f);

    internal const float DirectionalKeyIntensity = 110.100006f;

    internal static Vector3 DirectionalKeyColor { get; } =
        SourceRadiance / DirectionalKeyIntensity;

    internal static Light CreateDirectionalKey() => new()
    {
        Type = LightType.Directional,
        Direction = SourceDirection,
        Color = DirectionalKeyColor,
        Intensity = DirectionalKeyIntensity,
        Range = 100.0f,
        CastsShadows = true,
        ShadowStrength = 1.0f,
        ShadowPriority = 10
    };
}
