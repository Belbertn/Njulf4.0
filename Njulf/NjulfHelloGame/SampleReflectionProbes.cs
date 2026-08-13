using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace NjulfHelloGame;

internal static class SampleReflectionProbes
{
    public static void Configure(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        scene.Add(new ReflectionProbe
        {
            Name = "SampleRoomCenter",
            Position = new Vector3(0.0f, 2.0f, 0.0f),
            Shape = ReflectionProbeShape.Box,
            // Keep the capture point at human height, but place the influence
            // boundary beyond the complete Sponza scene. The old X = +/-8
            // boundary faded at the same world position as the near-DDGI ring
            // and amplified its diffuse transition with an IBL source swap.
            BoxExtents = new Vector3(24.0f, 21.0f, 18.0f),
            BlendDistance = 3.0f,
            Intensity = 1.0f,
            Priority = 0,
            BoxProjection = true
        });

        scene.Add(new ReflectionProbe
        {
            Name = "SampleEntranceOverlap",
            Position = new Vector3(0.0f, 1.75f, 7.0f),
            Shape = ReflectionProbeShape.Box,
            BoxExtents = new Vector3(5.0f, 3.0f, 4.0f),
            BlendDistance = 1.0f,
            Intensity = 0.9f,
            Priority = 1,
            BoxProjection = true
        });
    }
}
