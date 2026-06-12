using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Rendering.Data
{
    public static class ReflectionProbeData
    {
        public const int BoxProjectionFlag = 1 << 0;
        public const int ReflectionEnabledFlag = 1 << 0;
        public const int ReflectionBoxProjectionEnabledFlag = 1 << 1;
        public const int ReflectionProbeBlendingEnabledFlag = 1 << 2;

        public static readonly ulong HeaderSize = (ulong)Marshal.SizeOf<GPUReflectionProbeHeader>();
        public static readonly ulong ProbeStride = (ulong)Marshal.SizeOf<GPUReflectionProbe>();

        public static uint CalculateMipCount(uint resolution)
        {
            if (resolution == 0)
                throw new ArgumentOutOfRangeException(nameof(resolution));

            uint levels = 1;
            uint size = resolution;
            while (size > 1)
            {
                size /= 2;
                levels++;
            }

            return levels;
        }

        public static ulong EstimateCubemapArrayBytes(int probeCapacity, uint resolution, uint mipCount)
        {
            if (probeCapacity <= 0 || resolution == 0 || mipCount == 0)
                return 0;

            ulong total = 0;
            uint size = resolution;
            for (uint mip = 0; mip < mipCount; mip++)
            {
                total = checked(total + (ulong)probeCapacity * 6UL * size * size * 8UL);
                size = Math.Max(1u, size / 2u);
            }

            return total;
        }

        public static int BuildProbes(
            IReadOnlyList<ReflectionProbe> authoredProbes,
            ReflectionSettings settings,
            Span<GPUReflectionProbe> destination)
        {
            if (authoredProbes == null)
                throw new ArgumentNullException(nameof(authoredProbes));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            int count = Math.Min(Math.Min(authoredProbes.Count, settings.MaxProbes), destination.Length);
            if (!settings.Enabled || settings.Mode is ReflectionMode.Disabled or ReflectionMode.GlobalEnvironmentOnly || count == 0)
                return 0;

            var sortable = new List<(ReflectionProbe Probe, int OriginalIndex)>(authoredProbes.Count);
            for (int i = 0; i < authoredProbes.Count; i++)
            {
                ReflectionProbe? probe = authoredProbes[i];
                if (probe != null)
                    sortable.Add((probe, i));
            }

            sortable.Sort((a, b) =>
            {
                int priority = b.Probe.Priority.CompareTo(a.Probe.Priority);
                if (priority != 0)
                    return priority;

                int name = string.CompareOrdinal(a.Probe.Name, b.Probe.Name);
                return name != 0 ? name : a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            int written = 0;
            for (int i = 0; i < sortable.Count && written < count; i++)
            {
                destination[written] = BuildGpuProbe(sortable[i].Probe, written);
                written++;
            }

            return written;
        }

        public static GPUReflectionProbeHeader BuildHeader(
            int activeProbeCount,
            ReflectionSettings settings,
            int probeCubemapArrayTextureIndex,
            int debugTextureIndex,
            uint mipCount)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            settings.ClampDebugResources(activeProbeCount, mipCount);

            uint flags = 0;
            if (settings.Enabled && settings.Mode != ReflectionMode.Disabled)
                flags |= ReflectionEnabledFlag;
            if (settings.BoxProjectionEnabled)
                flags |= ReflectionBoxProjectionEnabledFlag;
            if (settings.ProbeBlendingEnabled)
                flags |= ReflectionProbeBlendingEnabledFlag;

            return new GPUReflectionProbeHeader
            {
                ProbeCount = activeProbeCount,
                MaxProbesPerPixel = settings.MaxProbesPerPixel,
                ProbeCubemapArrayTextureIndex = probeCubemapArrayTextureIndex,
                DebugTextureIndex = debugTextureIndex,
                Intensity = settings.Intensity,
                GlobalFallbackIntensity = settings.GlobalFallbackIntensity,
                ProbeMipCount = mipCount,
                Flags = flags,
                DebugView = (uint)settings.DebugView,
                DebugProbeIndex = settings.DebugProbeIndex,
                DebugCubemapFace = settings.DebugCubemapFace,
                DebugMipLevel = settings.DebugMipLevel
            };
        }

        public static float CalculateInfluenceWeight(ReflectionProbe probe, Vector3 worldPosition)
        {
            if (probe == null)
                throw new ArgumentNullException(nameof(probe));

            return probe.Shape == ReflectionProbeShape.Sphere
                ? CalculateSphereInfluence(probe, worldPosition)
                : CalculateBoxInfluence(probe, worldPosition);
        }

        public static Vector3 BoxProjectDirection(ReflectionProbe probe, Vector3 worldPosition, Vector3 reflectionDirection)
        {
            if (probe == null)
                throw new ArgumentNullException(nameof(probe));

            Vector3 direction = reflectionDirection.Normalized();
            if (direction.LengthSquared() <= 0.000001f)
                return Vector3.Zero;

            Matrix4x4 worldToProbe = BuildWorldToProbe(probe);
            Matrix4x4 probeToWorld = worldToProbe.Invert();
            Vector3 localPosition = TransformPoint(worldPosition, worldToProbe);
            Vector3 localDirection = TransformVector(direction, worldToProbe).Normalized();
            Vector3 extents = probe.BoxExtents;

            if (!IsInsideBox(localPosition, extents))
                return direction;

            float tx = AxisIntersection(localPosition.X, localDirection.X, extents.X);
            float ty = AxisIntersection(localPosition.Y, localDirection.Y, extents.Y);
            float tz = AxisIntersection(localPosition.Z, localDirection.Z, extents.Z);
            float t = MathF.Min(tx, MathF.Min(ty, tz));
            if (!float.IsFinite(t) || t <= 0.0f)
                return direction;

            Vector3 hit = localPosition + localDirection * t;
            Vector3 projectedLocalDirection = hit.Normalized();
            return TransformVector(projectedLocalDirection, probeToWorld).Normalized();
        }

        private static GPUReflectionProbe BuildGpuProbe(ReflectionProbe probe, int cubemapArrayIndex)
        {
            Vector3 extents = probe.Shape == ReflectionProbeShape.Box
                ? probe.BoxExtents
                : new Vector3(probe.Radius);

            int flags = probe.BoxProjection ? BoxProjectionFlag : 0;
            return new GPUReflectionProbe
            {
                WorldToProbe = BuildWorldToProbe(probe),
                PositionAndRadius = new Vector4(probe.Position, probe.Radius),
                BoxMin = new Vector4(-extents, 0.0f),
                BoxMax = new Vector4(extents, 0.0f),
                BlendParams = new Vector4(probe.BlendDistance, probe.Intensity, 0.0f, 0.0f),
                CubemapArrayIndex = cubemapArrayIndex,
                Shape = (int)probe.Shape,
                Flags = flags,
                Priority = probe.Priority
            };
        }

        private static Matrix4x4 BuildWorldToProbe(ReflectionProbe probe)
        {
            Matrix4x4 inverseTranslation = Matrix4x4.CreateTranslation(-probe.Position);
            Matrix4x4 inverseRotation = probe.Rotation.Inverse().ToMatrix4x4();
            return inverseTranslation * inverseRotation;
        }

        private static float CalculateSphereInfluence(ReflectionProbe probe, Vector3 worldPosition)
        {
            float distance = Vector3.Distance(probe.Position, worldPosition);
            if (distance >= probe.Radius)
                return 0.0f;
            if (probe.BlendDistance <= 0.0f)
                return 1.0f;

            float innerRadius = MathF.Max(0.0f, probe.Radius - probe.BlendDistance);
            return 1.0f - SmoothStep(innerRadius, probe.Radius, distance);
        }

        private static float CalculateBoxInfluence(ReflectionProbe probe, Vector3 worldPosition)
        {
            Vector3 local = TransformPoint(worldPosition, BuildWorldToProbe(probe));
            Vector3 extents = probe.BoxExtents;
            if (!IsInsideBox(local, extents))
                return 0.0f;
            if (probe.BlendDistance <= 0.0f)
                return 1.0f;

            float boundaryDistance = MathF.Min(
                extents.X - MathF.Abs(local.X),
                MathF.Min(extents.Y - MathF.Abs(local.Y), extents.Z - MathF.Abs(local.Z)));
            return SmoothStep(0.0f, probe.BlendDistance, boundaryDistance);
        }

        private static bool IsInsideBox(Vector3 localPosition, Vector3 extents)
        {
            return MathF.Abs(localPosition.X) <= extents.X &&
                   MathF.Abs(localPosition.Y) <= extents.Y &&
                   MathF.Abs(localPosition.Z) <= extents.Z;
        }

        private static float AxisIntersection(float position, float direction, float extent)
        {
            if (MathF.Abs(direction) <= 0.00001f)
                return float.PositiveInfinity;

            float plane = direction > 0.0f ? extent : -extent;
            return (plane - position) / direction;
        }

        private static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix)
        {
            return new Vector3(
                point.X * matrix.M11 + point.Y * matrix.M21 + point.Z * matrix.M31 + matrix.M41,
                point.X * matrix.M12 + point.Y * matrix.M22 + point.Z * matrix.M32 + matrix.M42,
                point.X * matrix.M13 + point.Y * matrix.M23 + point.Z * matrix.M33 + matrix.M43);
        }

        private static Vector3 TransformVector(Vector3 vector, Matrix4x4 matrix)
        {
            return new Vector3(
                vector.X * matrix.M11 + vector.Y * matrix.M21 + vector.Z * matrix.M31,
                vector.X * matrix.M12 + vector.Y * matrix.M22 + vector.Z * matrix.M32,
                vector.X * matrix.M13 + vector.Y * matrix.M23 + vector.Z * matrix.M33);
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            if (edge1 <= edge0)
                return value >= edge1 ? 1.0f : 0.0f;

            float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }
    }
}
