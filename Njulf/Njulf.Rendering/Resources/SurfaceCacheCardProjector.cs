using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public static class SurfaceCacheCardProjector
    {
        public const int AxisCount = 6;

        public static GPUSurfaceCard CreateCard(uint objectIndex, int axis, MeshInfo meshInfo, SurfaceCacheAtlasAllocation allocation, uint frameIndex)
        {
            if ((uint)axis >= AxisCount)
                throw new ArgumentOutOfRangeException(nameof(axis));

            Vector3 min = ToCore(meshInfo.BoundingBoxMin);
            Vector3 max = ToCore(meshInfo.BoundingBoxMax);
            Vector3 center = (min + max) * 0.5f;
            Vector3 extent = Max(max - min, new Vector3(0.001f));
            ResolveBasis(axis, out Vector3 n, out Vector3 u, out Vector3 v);

            float halfU = MathF.Max(0.0005f, AbsDot(extent, u) * 0.5f);
            float halfV = MathF.Max(0.0005f, AbsDot(extent, v) * 0.5f);
            float depthRange = MathF.Max(0.001f, AbsDot(extent, n));
            Vector3 origin = center - u * halfU - v * halfV - n * (depthRange * 0.5f);

            return new GPUSurfaceCard
            {
                ObjectIndex = objectIndex,
                Axis = checked((uint)axis),
                LastCaptureFrame = frameIndex,
                Flags = 0,
                AtlasRect = new Vector4(allocation.X, allocation.Y, allocation.Size, allocation.Size),
                WorldOriginAndTileSize = new Vector4(origin.X, origin.Y, origin.Z, allocation.Size),
                WorldAxisUAndHalfExtent = new Vector4(u.X, u.Y, u.Z, halfU),
                WorldAxisVAndHalfExtent = new Vector4(v.X, v.Y, v.Z, halfV),
                WorldAxisNAndDepthRange = new Vector4(n.X, n.Y, n.Z, depthRange)
            };
        }

        public static Vector3 ProjectToWorld(in GPUSurfaceCard card, float u01, float v01, float depth01)
        {
            Vector3 origin = card.WorldOriginAndTileSize.XYZ();
            Vector3 axisU = card.WorldAxisUAndHalfExtent.XYZ();
            Vector3 axisV = card.WorldAxisVAndHalfExtent.XYZ();
            Vector3 axisN = card.WorldAxisNAndDepthRange.XYZ();
            float width = card.WorldAxisUAndHalfExtent.W * 2.0f;
            float height = card.WorldAxisVAndHalfExtent.W * 2.0f;
            float depth = card.WorldAxisNAndDepthRange.W;
            return origin + axisU * (u01 * width) + axisV * (v01 * height) + axisN * (depth01 * depth);
        }

        private static void ResolveBasis(int axis, out Vector3 n, out Vector3 u, out Vector3 v)
        {
            switch (axis)
            {
                case 0:
                    n = new Vector3(1, 0, 0); u = new Vector3(0, 0, -1); v = new Vector3(0, 1, 0); break;
                case 1:
                    n = new Vector3(-1, 0, 0); u = new Vector3(0, 0, 1); v = new Vector3(0, 1, 0); break;
                case 2:
                    n = new Vector3(0, 1, 0); u = new Vector3(1, 0, 0); v = new Vector3(0, 0, 1); break;
                case 3:
                    n = new Vector3(0, -1, 0); u = new Vector3(1, 0, 0); v = new Vector3(0, 0, -1); break;
                case 4:
                    n = new Vector3(0, 0, 1); u = new Vector3(1, 0, 0); v = new Vector3(0, 1, 0); break;
                default:
                    n = new Vector3(0, 0, -1); u = new Vector3(-1, 0, 0); v = new Vector3(0, 1, 0); break;
            }
        }

        private static Vector3 ToCore(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
        private static Vector3 Max(Vector3 value, Vector3 min) => new(MathF.Max(value.X, min.X), MathF.Max(value.Y, min.Y), MathF.Max(value.Z, min.Z));
        private static float AbsDot(Vector3 a, Vector3 b) => MathF.Abs(a.X * b.X) + MathF.Abs(a.Y * b.Y) + MathF.Abs(a.Z * b.Z);
    }

    public readonly record struct SurfaceCacheAtlasAllocation(uint X, uint Y, uint Size);

    internal static class SurfaceCacheVectorExtensions
    {
        public static Vector3 XYZ(this Vector4 value) => new(value.X, value.Y, value.Z);
    }
}
