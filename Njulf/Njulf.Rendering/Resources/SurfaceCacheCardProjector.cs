using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;

namespace Njulf.Rendering.Resources
{
    public static class SurfaceCacheCardProjector
    {
        public const int AxisCount = 6;

        public static GPUSurfaceCard CreateCard(
            uint objectIndex,
            int axis,
            MeshInfo meshInfo,
            SurfaceCacheAtlasAllocation allocation,
            uint frameIndex) =>
            CreateCard(objectIndex, axis, meshInfo, CoreMatrix4x4.Identity, allocation, frameIndex);

        public static GPUSurfaceCard CreateCard(
            uint objectIndex,
            int axis,
            MeshInfo meshInfo,
            CoreMatrix4x4 worldMatrix,
            SurfaceCacheAtlasAllocation allocation,
            uint frameIndex)
        {
            if ((uint)axis >= AxisCount)
                throw new ArgumentOutOfRangeException(nameof(axis));

            Vector3 min = ToCore(meshInfo.BoundingBoxMin);
            Vector3 max = ToCore(meshInfo.BoundingBoxMax);
            Vector3 center = (min + max) * 0.5f;
            Vector3 extent = Max(max - min, new Vector3(0.001f));
            ResolveBasis(axis, out Vector3 n, out Vector3 u, out Vector3 v);

            Vector3 localAxisU = u;
            Vector3 localAxisV = v;
            Vector3 localAxisN = n;
            Vector3 worldAxisU = TransformDirection(localAxisU, worldMatrix);
            Vector3 worldAxisV = TransformDirection(localAxisV, worldMatrix);
            Vector3 worldAxisN = TransformDirection(localAxisN, worldMatrix);
            float uScale = MathF.Max(worldAxisU.Length(), 0.0001f);
            float vScale = MathF.Max(worldAxisV.Length(), 0.0001f);
            float nScale = MathF.Max(worldAxisN.Length(), 0.0001f);
            u = worldAxisU / uScale;
            v = worldAxisV / vScale;
            n = worldAxisN / nScale;

            float halfU = MathF.Max(0.0005f, AbsDot(extent, localAxisU) * 0.5f * uScale);
            float halfV = MathF.Max(0.0005f, AbsDot(extent, localAxisV) * 0.5f * vScale);
            float depthRange = MathF.Max(0.001f, AbsDot(extent, localAxisN) * nScale);
            Vector3 worldCenter = TransformPoint(center, worldMatrix);
            Vector3 origin = worldCenter - u * halfU - v * halfV - n * (depthRange * 0.5f);

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
        private static Vector3 TransformPoint(Vector3 point, CoreMatrix4x4 matrix) => point * matrix;
        private static Vector3 TransformDirection(Vector3 direction, CoreMatrix4x4 matrix) => new(
            direction.X * matrix.M11 + direction.Y * matrix.M21 + direction.Z * matrix.M31,
            direction.X * matrix.M12 + direction.Y * matrix.M22 + direction.Z * matrix.M32,
            direction.X * matrix.M13 + direction.Y * matrix.M23 + direction.Z * matrix.M33);
        private static Vector3 Max(Vector3 value, Vector3 min) => new(MathF.Max(value.X, min.X), MathF.Max(value.Y, min.Y), MathF.Max(value.Z, min.Z));
        private static float AbsDot(Vector3 a, Vector3 b) => MathF.Abs(a.X * b.X) + MathF.Abs(a.Y * b.Y) + MathF.Abs(a.Z * b.Z);
    }

    public readonly record struct SurfaceCacheAtlasAllocation(uint X, uint Y, uint Size);

    internal static class SurfaceCacheVectorExtensions
    {
        public static Vector3 XYZ(this Vector4 value) => new(value.X, value.Y, value.Z);
    }
}
