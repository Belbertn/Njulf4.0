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
            BoxExtents = new Vector3(8.0f, 4.0f, 8.0f),
            BlendDistance = 1.5f,
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
