using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public readonly record struct MeshSdfVoxelAddress(Vector3 LocalPosition, Vector3 NormalizedUv);

    public readonly record struct MeshSdfBakeDescriptor(
        Extent3D Extent,
        Vector3 BoundsMin,
        Vector3 BoundsMax,
        Vector3 BoundsExtent,
        float VoxelSize,
        float InvVoxelSize,
        ulong EstimatedByteSize);

    public static class MeshSdfBakePlanner
    {
        public const uint MinResolution = 8;
        public const uint MaxResolution = 128;
        public const uint MeshSdfFlagUnsignedFallback = 1u << 0;
        public const uint MeshSdfFlagAnalyticBox = 1u << 1;
        public const float MinBakeBoundsVoxelsPerAxis = 2.0f;
        public const float MaxTargetVoxelSize = 0.25f;
        private const float TargetVoxelFractionOfMaxExtent = 0.015625f;
        private const float BoundsPaddingVoxels = 1.0f;

        public static MeshSdfBakeDescriptor CreateDescriptor(MeshInfo meshInfo)
        {
            Vector3 rawExtent = Vector3.Max(meshInfo.BoundingBoxMax - meshInfo.BoundingBoxMin, new Vector3(0.0001f));
            float maxExtent = MathF.Max(rawExtent.X, MathF.Max(rawExtent.Y, rawExtent.Z));
            float targetVoxelSize = MathF.Max(MathF.Min(maxExtent * TargetVoxelFractionOfMaxExtent, MaxTargetVoxelSize), 0.0001f);

            uint resolutionX = ResolveAxisResolution(rawExtent.X, targetVoxelSize);
            uint resolutionY = ResolveAxisResolution(rawExtent.Y, targetVoxelSize);
            uint resolutionZ = ResolveAxisResolution(rawExtent.Z, targetVoxelSize);
            float voxelSize = maxExtent / MathF.Max(1.0f, MathF.Max(resolutionX, MathF.Max(resolutionY, resolutionZ)) - 1.0f);

            Vector3 paddedMin = meshInfo.BoundingBoxMin - new Vector3(voxelSize * BoundsPaddingVoxels);
            Vector3 paddedMax = meshInfo.BoundingBoxMax + new Vector3(voxelSize * BoundsPaddingVoxels);
            Vector3 paddedCenter = (paddedMin + paddedMax) * 0.5f;
            Vector3 paddedExtent = Vector3.Max(
                paddedMax - paddedMin,
                new Vector3(voxelSize * MinBakeBoundsVoxelsPerAxis));
            paddedMin = paddedCenter - paddedExtent * 0.5f;
            paddedMax = paddedCenter + paddedExtent * 0.5f;

            var extent = new Extent3D
            {
                Width = resolutionX,
                Height = resolutionY,
                Depth = resolutionZ
            };

            return new MeshSdfBakeDescriptor(
                extent,
                paddedMin,
                paddedMax,
                paddedExtent,
                voxelSize,
                1.0f / voxelSize,
                VolumeTexture.CalculateByteSize(extent, Format.R16Sfloat));
        }

        public static MeshSdfVoxelAddress GetVoxelAddress(MeshSdfBakeDescriptor descriptor, uint x, uint y, uint z)
        {
            if (x >= descriptor.Extent.Width || y >= descriptor.Extent.Height || z >= descriptor.Extent.Depth)
                throw new ArgumentOutOfRangeException("Voxel coordinates exceed the mesh SDF extent.");

            Vector3 uv = new(
                (x + 0.5f) / Math.Max(descriptor.Extent.Width, 1u),
                (y + 0.5f) / Math.Max(descriptor.Extent.Height, 1u),
                (z + 0.5f) / Math.Max(descriptor.Extent.Depth, 1u));

            return new MeshSdfVoxelAddress(
                descriptor.BoundsMin + descriptor.BoundsExtent * uv,
                uv);
        }

        public static uint CreateBakeFlags(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
        {
            if (positions.IsEmpty || indices.Length < 3 || indices.Length % 3 != 0)
                return MeshSdfFlagUnsignedFallback;

            Vector3 boundsMin = positions[0];
            Vector3 boundsMax = positions[0];
            for (int i = 1; i < positions.Length; i++)
            {
                boundsMin = Vector3.Min(boundsMin, positions[i]);
                boundsMax = Vector3.Max(boundsMax, positions[i]);
            }

            Vector3 boundsExtent = boundsMax - boundsMin;
            float maxExtent = MathF.Max(boundsExtent.X, MathF.Max(boundsExtent.Y, boundsExtent.Z));
            float weldTolerance = MathF.Max(maxExtent * 1.0e-4f, 1.0e-6f);

            var weldedVertices = new QuantizedVertexKey[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                weldedVertices[i] = QuantizedVertexKey.Create(positions[i], weldTolerance);

            var edgeUseCounts = new Dictionary<EdgeKey, int>(indices.Length);
            bool hasInvalidTopology = false;
            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];
                if (i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length || i0 == i1 || i1 == i2 || i2 == i0)
                {
                    hasInvalidTopology = true;
                    continue;
                }

                Vector3 a = positions[(int)i0];
                Vector3 b = positions[(int)i1];
                Vector3 c = positions[(int)i2];
                if (Vector3.Cross(b - a, c - a).LengthSquared() <= 1.0e-16f)
                {
                    hasInvalidTopology = true;
                    continue;
                }

                QuantizedVertexKey w0 = weldedVertices[(int)i0];
                QuantizedVertexKey w1 = weldedVertices[(int)i1];
                QuantizedVertexKey w2 = weldedVertices[(int)i2];
                if (w0 == w1 || w1 == w2 || w2 == w0)
                {
                    hasInvalidTopology = true;
                    continue;
                }

                AddEdge(edgeUseCounts, w0, w1);
                AddEdge(edgeUseCounts, w1, w2);
                AddEdge(edgeUseCounts, w2, w0);
            }

            foreach (int count in edgeUseCounts.Values)
            {
                if (count != 2)
                {
                    hasInvalidTopology = true;
                    break;
                }
            }

            if (hasInvalidTopology)
                return MeshSdfFlagUnsignedFallback;

            uint flags = 0u;
            if (IsAxisAlignedBoxMesh(positions, indices, boundsMin, boundsMax, weldTolerance))
                flags |= MeshSdfFlagAnalyticBox;

            return flags;
        }

        private static bool IsAxisAlignedBoxMesh(
            ReadOnlySpan<Vector3> positions,
            ReadOnlySpan<uint> indices,
            Vector3 boundsMin,
            Vector3 boundsMax,
            float tolerance)
        {
            Vector3 extent = boundsMax - boundsMin;
            if (extent.X <= tolerance || extent.Y <= tolerance || extent.Z <= tolerance)
                return false;

            var seenCorners = new bool[8];
            var faceTriangleCounts = new int[6];
            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];
                if (i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length)
                    return false;

                Vector3 a = positions[(int)i0];
                Vector3 b = positions[(int)i1];
                Vector3 c = positions[(int)i2];
                if (!TryClassifyBoxCorner(a, boundsMin, boundsMax, tolerance, out int ca) ||
                    !TryClassifyBoxCorner(b, boundsMin, boundsMax, tolerance, out int cb) ||
                    !TryClassifyBoxCorner(c, boundsMin, boundsMax, tolerance, out int cc))
                {
                    return false;
                }

                seenCorners[ca] = true;
                seenCorners[cb] = true;
                seenCorners[cc] = true;

                int face = ClassifyBoxFace(a, b, c, boundsMin, boundsMax, tolerance);
                if (face < 0)
                    return false;

                faceTriangleCounts[face]++;
            }

            for (int i = 0; i < seenCorners.Length; i++)
            {
                if (!seenCorners[i])
                    return false;
            }

            for (int i = 0; i < faceTriangleCounts.Length; i++)
            {
                if (faceTriangleCounts[i] != 2)
                    return false;
            }

            return true;
        }

        private static bool TryClassifyBoxCorner(
            Vector3 position,
            Vector3 boundsMin,
            Vector3 boundsMax,
            float tolerance,
            out int corner)
        {
            corner = 0;
            if (!TryClassifyAxisEndpoint(position.X, boundsMin.X, boundsMax.X, tolerance, out int x) ||
                !TryClassifyAxisEndpoint(position.Y, boundsMin.Y, boundsMax.Y, tolerance, out int y) ||
                !TryClassifyAxisEndpoint(position.Z, boundsMin.Z, boundsMax.Z, tolerance, out int z))
            {
                return false;
            }

            corner = x | (y << 1) | (z << 2);
            return true;
        }

        private static int ClassifyBoxFace(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 boundsMin,
            Vector3 boundsMax,
            float tolerance)
        {
            int face = -1;
            TrySetFace(AxisMatches(a.X, b.X, c.X, boundsMin.X, tolerance), 0, ref face);
            TrySetFace(AxisMatches(a.X, b.X, c.X, boundsMax.X, tolerance), 1, ref face);
            TrySetFace(AxisMatches(a.Y, b.Y, c.Y, boundsMin.Y, tolerance), 2, ref face);
            TrySetFace(AxisMatches(a.Y, b.Y, c.Y, boundsMax.Y, tolerance), 3, ref face);
            TrySetFace(AxisMatches(a.Z, b.Z, c.Z, boundsMin.Z, tolerance), 4, ref face);
            TrySetFace(AxisMatches(a.Z, b.Z, c.Z, boundsMax.Z, tolerance), 5, ref face);
            return face;

            static void TrySetFace(bool matches, int candidate, ref int face)
            {
                if (!matches)
                    return;

                face = face == -1 ? candidate : -2;
            }
        }

        private static bool TryClassifyAxisEndpoint(float value, float min, float max, float tolerance, out int endpoint)
        {
            if (MathF.Abs(value - min) <= tolerance)
            {
                endpoint = 0;
                return true;
            }

            if (MathF.Abs(value - max) <= tolerance)
            {
                endpoint = 1;
                return true;
            }

            endpoint = 0;
            return false;
        }

        private static bool AxisMatches(float a, float b, float c, float value, float tolerance) =>
            MathF.Abs(a - value) <= tolerance &&
            MathF.Abs(b - value) <= tolerance &&
            MathF.Abs(c - value) <= tolerance;

        private static uint ResolveAxisResolution(float axisExtent, float targetVoxelSize)
        {
            uint resolution = (uint)MathF.Ceiling(axisExtent / targetVoxelSize) + 1u;
            return Math.Clamp(resolution, MinResolution, MaxResolution);
        }

        private static void AddEdge(Dictionary<EdgeKey, int> edgeUseCounts, QuantizedVertexKey a, QuantizedVertexKey b)
        {
            var key = EdgeKey.Create(a, b);
            edgeUseCounts.TryGetValue(key, out int count);
            edgeUseCounts[key] = count + 1;
        }

        private readonly record struct QuantizedVertexKey(long X, long Y, long Z) : IComparable<QuantizedVertexKey>
        {
            public static QuantizedVertexKey Create(Vector3 position, float tolerance)
            {
                float invTolerance = 1.0f / MathF.Max(tolerance, 1.0e-12f);
                return new QuantizedVertexKey(
                    Quantize(position.X, invTolerance),
                    Quantize(position.Y, invTolerance),
                    Quantize(position.Z, invTolerance));
            }

            public int CompareTo(QuantizedVertexKey other)
            {
                int x = X.CompareTo(other.X);
                if (x != 0)
                    return x;

                int y = Y.CompareTo(other.Y);
                return y != 0 ? y : Z.CompareTo(other.Z);
            }

            private static long Quantize(float value, float invTolerance)
            {
                double quantized = Math.Round(value * invTolerance, MidpointRounding.AwayFromZero);
                if (quantized <= long.MinValue)
                    return long.MinValue;
                if (quantized >= long.MaxValue)
                    return long.MaxValue;

                return (long)quantized;
            }
        }

        private readonly record struct EdgeKey(QuantizedVertexKey A, QuantizedVertexKey B)
        {
            public static EdgeKey Create(QuantizedVertexKey a, QuantizedVertexKey b) =>
                a.CompareTo(b) <= 0 ? new EdgeKey(a, b) : new EdgeKey(b, a);
        }
    }
}
