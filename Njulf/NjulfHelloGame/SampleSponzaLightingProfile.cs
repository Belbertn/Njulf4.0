using System.Numerics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Defines the canonical Sponza directional key shared by recreated sample
/// scenarios and the authored scene document.
/// </summary>
public static class SampleSponzaLightingProfile
{
    /// <summary>
    /// Direction in which the disabled source glTF sun travels, reconstructed
    /// from its <c>SUN</c> and <c>SUN.Target</c> nodes.
    /// </summary>
    public static Vector3 SourceSunDirection { get; } =
        Vector3.Normalize(new Vector3(0.5505701f, -0.8281202f, 0.1053067f));

    /// <summary>
    /// Runtime key for the right-wall/courtyard validation view. The source
    /// KHR light has zero intensity and its azimuth enters from behind the
    /// occluding gallery; rotating that azimuth by 180 degrees preserves the
    /// authored elevation while exposing useful lit and shadowed receivers.
    /// </summary>
    public static Vector3 DirectionalKeyDirection { get; } = Vector3.Normalize(new Vector3(
        -SourceSunDirection.X,
        SourceSunDirection.Y,
        -SourceSunDirection.Z));

    public static Vector3 DirectionalKeyColor { get; } = new(1.0f, 0.92f, 0.82f);
    public const float DirectionalKeyIntensity = 14.0f;
    public const float DirectionalKeyShadowStrength = 1.0f;

    /// <summary>Creates the canonical directional key without retaining mutable state.</summary>
    public static Light CreateDirectionalKey() => new()
    {
        Type = LightType.Directional,
        Direction = DirectionalKeyDirection,
        Color = DirectionalKeyColor,
        Intensity = DirectionalKeyIntensity,
        Range = 10f,
        CastsShadows = true,
        // Preserve true occlusion. Softness belongs to a finite-area
        // sun/shadow filter, never a constant light leak.
        ShadowStrength = DirectionalKeyShadowStrength,
        ShadowPriority = 10
    };
}
