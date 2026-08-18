using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace NjulfHelloGame;

/// <summary>
/// Local specular anchors for Bistro. The broad courtyard probe prevents the
/// procedural sky from being the only off-screen source, while the higher
/// priority cafe probe gives the scooter, glazing, and shaded storefront a
/// nearby box-projected source. Captures are scheduled incrementally by the
/// renderer and include the converged DDGI field.
/// </summary>
internal static class SampleBistroReflectionProbes
{
    public static void Configure(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        scene.Add(new ReflectionProbe
        {
            Name = "BistroCourtyard",
            Position = new Vector3(-2.0f, 3.0f, 1.0f),
            Shape = ReflectionProbeShape.Box,
            BoxExtents = new Vector3(22.0f, 10.0f, 16.0f),
            BlendDistance = 3.0f,
            Intensity = 1.0f,
            Priority = 0,
            BoxProjection = true
        });

        scene.Add(new ReflectionProbe
        {
            Name = "BistroCafeCorner",
            Position = new Vector3(-8.0f, 2.2f, 1.5f),
            Shape = ReflectionProbeShape.Box,
            BoxExtents = new Vector3(12.0f, 5.5f, 9.0f),
            BlendDistance = 2.0f,
            Intensity = 1.0f,
            Priority = 1,
            BoxProjection = true
        });
    }
}
