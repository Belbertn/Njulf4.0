using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public sealed class GlobalIlluminationProbeVolume
    {
        public const int MinProbeCountPerAxis = 2;
        public const int MaxProbeCountPerAxis = 128;
        public const int MinRaysPerProbe = 16;
        public const int MaxRaysPerProbe = 1024;

        private Vector3 _size = new(24.0f, 12.0f, 24.0f);
        private int _probeCountX = 12;
        private int _probeCountY = 6;
        private int _probeCountZ = 12;
        private int _raysPerProbe = 96;
        private int _maxProbeUpdatesPerFrame = 256;
        private float _normalBias = 0.2f;
        private float _viewBias = 0.5f;
        private float _maxRayDistance = 16.0f;
        private float _intensity = 1.0f;
        private float _hysteresis = 0.97f;

        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public Vector3 Origin { get; set; } = new(-12.0f, -2.0f, -12.0f);

        public Vector3 Size
        {
            get => _size;
            set => _size = new(
                ClampFinite(value.X, 0.1f, 10000.0f),
                ClampFinite(value.Y, 0.1f, 10000.0f),
                ClampFinite(value.Z, 0.1f, 10000.0f));
        }

        public int ProbeCountX
        {
            get => _probeCountX;
            set => _probeCountX = System.Math.Clamp(value, MinProbeCountPerAxis, MaxProbeCountPerAxis);
        }

        public int ProbeCountY
        {
            get => _probeCountY;
            set => _probeCountY = System.Math.Clamp(value, MinProbeCountPerAxis, MaxProbeCountPerAxis);
        }

        public int ProbeCountZ
        {
            get => _probeCountZ;
            set => _probeCountZ = System.Math.Clamp(value, MinProbeCountPerAxis, MaxProbeCountPerAxis);
        }

        public int RaysPerProbe
        {
            get => _raysPerProbe;
            set => _raysPerProbe = System.Math.Clamp(value, MinRaysPerProbe, MaxRaysPerProbe);
        }

        public int MaxProbeUpdatesPerFrame
        {
            get => _maxProbeUpdatesPerFrame;
            set => _maxProbeUpdatesPerFrame = System.Math.Clamp(value, 0, 1_000_000);
        }

        public float NormalBias
        {
            get => _normalBias;
            set => _normalBias = ClampFinite(value, 0.0f, 10.0f);
        }

        public float ViewBias
        {
            get => _viewBias;
            set => _viewBias = ClampFinite(value, 0.0f, 10.0f);
        }

        public float MaxRayDistance
        {
            get => _maxRayDistance;
            set => _maxRayDistance = ClampFinite(value, 0.1f, 1000.0f);
        }

        public float Intensity
        {
            get => _intensity;
            set => _intensity = ClampFinite(value, 0.0f, 16.0f);
        }

        public float Hysteresis
        {
            get => _hysteresis;
            set => _hysteresis = ClampFinite(value, 0.0f, 0.999f);
        }

        public int ProbeCount => checked(ProbeCountX * ProbeCountY * ProbeCountZ);
        public Vector3 Center => Origin + Size * 0.5f;
        public Vector3 ProbeSpacing => new(
            Size.X / (ProbeCountX - 1),
            Size.Y / (ProbeCountY - 1),
            Size.Z / (ProbeCountZ - 1));
        public BoundingBox Bounds => new(Origin, Origin + Size);

        public static GlobalIlluminationProbeVolume CreateDefaultForBounds(BoundingBox bounds)
        {
            Vector3 size = bounds.Size;
            if (size.X <= 0.0f || size.Y <= 0.0f || size.Z <= 0.0f)
                size = new Vector3(24.0f, 12.0f, 24.0f);

            Vector3 padding = new(
                MathF.Max(2.0f, size.X * 0.08f),
                MathF.Max(2.0f, size.Y * 0.12f),
                MathF.Max(2.0f, size.Z * 0.08f));
            Vector3 paddedSize = size + padding * 2.0f;

            return new GlobalIlluminationProbeVolume
            {
                Name = "Default DDGI Volume",
                Origin = bounds.Min - padding,
                Size = paddedSize,
                ProbeCountX = ResolveProbeCount(paddedSize.X),
                ProbeCountY = ResolveProbeCount(paddedSize.Y),
                ProbeCountZ = ResolveProbeCount(paddedSize.Z)
            };
        }

        private static int ResolveProbeCount(float axisSize)
        {
            const float TargetSpacing = 3.0f;
            return System.Math.Clamp((int)MathF.Ceiling(axisSize / TargetSpacing) + 1, 4, 32);
        }

        private static float ClampFinite(float value, float min, float max)
        {
            if (!float.IsFinite(value))
                return min;
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
