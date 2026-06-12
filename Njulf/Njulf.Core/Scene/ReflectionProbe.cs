using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public enum ReflectionProbeShape
    {
        Box = 0,
        Sphere = 1
    }

    public sealed class ReflectionProbe
    {
        private Vector3 _boxExtents = new(5.0f, 5.0f, 5.0f);
        private float _radius = 5.0f;
        private float _blendDistance = 1.0f;
        private float _intensity = 1.0f;

        public string Name { get; set; } = string.Empty;
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public ReflectionProbeShape Shape { get; set; } = ReflectionProbeShape.Box;

        public Vector3 BoxExtents
        {
            get => _boxExtents;
            set => _boxExtents = new(
                MathF.Max(0.001f, value.X),
                MathF.Max(0.001f, value.Y),
                MathF.Max(0.001f, value.Z));
        }

        public float Radius
        {
            get => _radius;
            set => _radius = MathF.Max(0.001f, value);
        }

        public float BlendDistance
        {
            get => _blendDistance;
            set => _blendDistance = MathF.Max(0.0f, value);
        }

        public float Intensity
        {
            get => _intensity;
            set => _intensity = System.Math.Clamp(value, 0.0f, 16.0f);
        }

        public int Priority { get; set; }
        public string? CubemapPath { get; set; }
        public bool BoxProjection { get; set; } = true;
    }
}
