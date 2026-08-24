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

    // The FBX key points straight along the street and produces a nearly
    // front-lit courtyard from the locked beauty camera. Rotate it toward the
    // supplied reference's diagonal street shadow, but retain its authored
    // radiance. Boosting the key by half clipped plaster and forced the meter
    // to crush shaded storefronts.
    internal static Vector3 DirectionalKeyDirection { get; } =
        Vector3.Normalize(new Vector3(
            0.340573f,
            -0.87636626f,
            -0.340573f));

    internal static Vector3 DirectionalKeyRadiance { get; } =
        SourceRadiance;

    internal const float DirectionalKeyIntensity = 165.150009f;

    internal static Vector3 DirectionalKeyColor { get; } =
        DirectionalKeyRadiance / DirectionalKeyIntensity;

    internal static Light CreateDirectionalKey() => new()
    {
        Type = LightType.Directional,
        Direction = DirectionalKeyDirection,
        Color = DirectionalKeyColor,
        Intensity = DirectionalKeyIntensity,
        Range = 100.0f,
        CastsShadows = true,
        ShadowStrength = 1.0f,
        ShadowPriority = 10
    };
}
