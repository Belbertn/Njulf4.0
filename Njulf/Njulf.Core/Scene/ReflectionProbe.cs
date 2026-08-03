using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public enum ReflectionProbeShape { Box = 0, Sphere = 1 }

    public sealed class ReflectionProbe : IIdentifiedSceneEntity
    {
        private Guid _id = Guid.NewGuid();
        private string _name = string.Empty;
        private Vector3 _position;
        private Quaternion _rotation = Quaternion.Identity;
        private ReflectionProbeShape _shape;
        private Vector3 _boxExtents = new(5.0f, 5.0f, 5.0f);
        private float _radius = 5.0f;
        private float _blendDistance = 1.0f;
        private float _intensity = 1.0f;
        private int _priority;
        private string? _cubemapPath;
        private bool _boxProjection = true;

        /// <summary>Raised only when a semantically relevant authored value changes.</summary>
        public event Action<ReflectionProbe>? Changed;

        public Guid Id { get => _id; set => Set(ref _id, value); }
        public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }
        public Vector3 Position { get => _position; set => Set(ref _position, value); }
        public Quaternion Rotation { get => _rotation; set => Set(ref _rotation, value); }
        public ReflectionProbeShape Shape { get => _shape; set => Set(ref _shape, value); }

        public Vector3 BoxExtents
        {
            get => _boxExtents;
            set => Set(ref _boxExtents, new Vector3(
                MathF.Max(0.001f, value.X), MathF.Max(0.001f, value.Y), MathF.Max(0.001f, value.Z)));
        }

        public float Radius { get => _radius; set => Set(ref _radius, MathF.Max(0.001f, value)); }
        public float BlendDistance { get => _blendDistance; set => Set(ref _blendDistance, MathF.Max(0.0f, value)); }
        public float Intensity { get => _intensity; set => Set(ref _intensity, System.Math.Clamp(value, 0.0f, 16.0f)); }
        public int Priority { get => _priority; set => Set(ref _priority, value); }
        public string? CubemapPath { get => _cubemapPath; set => Set(ref _cubemapPath, value); }
        public bool BoxProjection { get => _boxProjection; set => Set(ref _boxProjection, value); }

        private void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
                return;
            field = value;
            Changed?.Invoke(this);
        }
    }
}
