using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public enum GlobalIlluminationProbeVolumeQualityClass
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

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
        private int _priority;
        private float _blendDistance;
        private int _streamingCellId;
        private float _steadyHysteresis = 0.97f;
        private float _dirtyHysteresis = 0.72f;
        private int _updatePriority;
        private int _dirtyRaysPerProbe = 64;

        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public Vector3 Origin { get; set; } = new(-12.0f, -2.0f, -12.0f);
        public bool Interior { get; set; }
        public GlobalIlluminationProbeVolumeQualityClass QualityClass { get; set; } = GlobalIlluminationProbeVolumeQualityClass.Medium;

        public int Priority
        {
            get => _priority;
            set => _priority = System.Math.Clamp(value, -1024, 1024);
        }

        public float BlendDistance
        {
            get => _blendDistance;
            set => _blendDistance = ClampFinite(value, 0.0f, 1000.0f);
        }

        public int StreamingCellId
        {
            get => _streamingCellId;
            set => _streamingCellId = System.Math.Max(0, value);
        }

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

        public float SteadyHysteresis
        {
            get => _steadyHysteresis;
            set => _steadyHysteresis = ClampFinite(value, 0.0f, 0.999f);
        }

        public float DirtyHysteresis
        {
            get => _dirtyHysteresis;
            set => _dirtyHysteresis = ClampFinite(value, 0.0f, 0.999f);
        }

        public int UpdatePriority
        {
            get => _updatePriority;
            set => _updatePriority = System.Math.Clamp(value, 0, 1_000_000);
        }

        public int DirtyRaysPerProbe
        {
            get => _dirtyRaysPerProbe;
            set => _dirtyRaysPerProbe = System.Math.Clamp(value, MinRaysPerProbe, MaxRaysPerProbe);
        }

        public int ProbeCount => checked(ProbeCountX * ProbeCountY * ProbeCountZ);
        public Vector3 Center => Origin + Size * 0.5f;
        public Vector3 ProbeSpacing => new(
            Size.X / (ProbeCountX - 1),
            Size.Y / (ProbeCountY - 1),
            Size.Z / (ProbeCountZ - 1));
        public BoundingBox Bounds => new(Origin, Origin + Size);

        public static GlobalIlluminationProbeVolume CreateSmallRoomPreset(BoundingBox roomBounds, float targetSpacing = 0.6f)
        {
            return CreateCourtyardInteriorPreset(roomBounds, targetSpacing);
        }

        public static GlobalIlluminationProbeVolume CreateCourtyardInteriorPreset(BoundingBox roomBounds, float targetSpacing = 0.65f)
        {
            return CreateInteriorPreset(roomBounds, targetSpacing, 0.5f, 0.75f, "Courtyard/Interior DDGI Volume");
        }

        private static GlobalIlluminationProbeVolume CreateInteriorPreset(
            BoundingBox roomBounds,
            float targetSpacing,
            float minimumSpacing,
            float maximumSpacing,
            string name)
        {
            Vector3 size = ResolveValidBoundsSize(roomBounds, new Vector3(6.0f, 3.0f, 6.0f));
            float spacing = ClampFinite(targetSpacing, minimumSpacing, maximumSpacing);
            int maxAxisProbeCount = System.Math.Max(
                ResolveProbeCountForSpacing(size.X, spacing),
                System.Math.Max(
                    ResolveProbeCountForSpacing(size.Y, spacing),
                    ResolveProbeCountForSpacing(size.Z, spacing)));
            float maxAxis = MathF.Max(size.X, MathF.Max(size.Y, size.Z));

            return new GlobalIlluminationProbeVolume
            {
                Name = name,
                Origin = roomBounds.Min,
                Size = size,
                ProbeCountX = ResolveProbeCountForSpacing(size.X, spacing),
                ProbeCountY = ResolveProbeCountForSpacing(size.Y, spacing),
                ProbeCountZ = ResolveProbeCountForSpacing(size.Z, spacing),
                Interior = true,
                QualityClass = GlobalIlluminationProbeVolumeQualityClass.High,
                Priority = 128,
                UpdatePriority = 128,
                BlendDistance = MathF.Max(spacing * 1.5f, 0.75f),
                RaysPerProbe = 32,
                DirtyRaysPerProbe = 48,
                MaxProbeUpdatesPerFrame = System.Math.Clamp(maxAxisProbeCount * maxAxisProbeCount, 48, 64),
                MaxRayDistance = ClampFinite(maxAxis * 1.25f, 8.0f, 15.0f),
                NormalBias = MathF.Max(0.03f, spacing * 0.08f),
                ViewBias = MathF.Max(0.08f, spacing * 0.2f),
                Hysteresis = 0.82f,
                SteadyHysteresis = 0.94f,
                DirtyHysteresis = 0.55f
            };
        }

        public static GlobalIlluminationProbeVolume CreateThinWallRoomPreset(BoundingBox roomBounds, float targetSpacing = 0.45f)
        {
            GlobalIlluminationProbeVolume volume = CreateInteriorPreset(
                roomBounds,
                targetSpacing,
                0.35f,
                0.5f,
                "Thin-Wall Room DDGI Volume");

            float spacing = MinAxis(volume.ProbeSpacing);
            volume.Name = "Thin-Wall Room DDGI Volume";
            volume.QualityClass = GlobalIlluminationProbeVolumeQualityClass.Ultra;
            volume.Priority = 192;
            volume.UpdatePriority = 192;
            volume.BlendDistance = MathF.Max(spacing * 2.0f, 0.9f);
            volume.RaysPerProbe = 32;
            volume.DirtyRaysPerProbe = 64;
            volume.MaxProbeUpdatesPerFrame = 64;
            volume.MaxRayDistance = ClampFinite(MaxAxis(volume.Size), 8.0f, 12.0f);
            volume.NormalBias = MathF.Max(0.04f, spacing * 0.1f);
            volume.ViewBias = MathF.Max(0.1f, spacing * 0.25f);
            volume.Hysteresis = 0.78f;
            volume.SteadyHysteresis = 0.92f;
            volume.DirtyHysteresis = 0.45f;
            return volume;
        }

        private static int ResolveProbeCountForSpacing(float axisSize, float spacing)
        {
            return System.Math.Clamp(
                (int)MathF.Ceiling(MathF.Max(axisSize, 0.1f) / MathF.Max(spacing, 0.01f)) + 1,
                MinProbeCountPerAxis,
                MaxProbeCountPerAxis);
        }

        private static Vector3 ResolveValidBoundsSize(BoundingBox bounds, Vector3 fallback)
        {
            Vector3 size = bounds.Size;
            if (size.X <= 0.0f || size.Y <= 0.0f || size.Z <= 0.0f)
                return fallback;

            return size;
        }

        private static float MinAxis(Vector3 value)
        {
            return MathF.Min(value.X, MathF.Min(value.Y, value.Z));
        }

        private static float MaxAxis(Vector3 value)
        {
            return MathF.Max(value.X, MathF.Max(value.Y, value.Z));
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
