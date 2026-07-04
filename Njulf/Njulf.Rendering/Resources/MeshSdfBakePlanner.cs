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
        public const float MinBakeBoundsVoxelsPerAxis = 2.0f;
        private const float TargetVoxelFractionOfMaxExtent = 0.015625f;
        private const float BoundsPaddingVoxels = 1.0f;

        public static MeshSdfBakeDescriptor CreateDescriptor(MeshInfo meshInfo)
        {
            Vector3 rawExtent = Vector3.Max(meshInfo.BoundingBoxMax - meshInfo.BoundingBoxMin, new Vector3(0.0001f));
            float maxExtent = MathF.Max(rawExtent.X, MathF.Max(rawExtent.Y, rawExtent.Z));
            float targetVoxelSize = MathF.Max(maxExtent * TargetVoxelFractionOfMaxExtent, 0.0001f);

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

                AddEdge(edgeUseCounts, i0, i1);
                AddEdge(edgeUseCounts, i1, i2);
                AddEdge(edgeUseCounts, i2, i0);
            }

            foreach (int count in edgeUseCounts.Values)
            {
                if (count != 2)
                {
                    hasInvalidTopology = true;
                    break;
                }
            }

            return hasInvalidTopology ? MeshSdfFlagUnsignedFallback : 0u;
        }

        private static uint ResolveAxisResolution(float axisExtent, float targetVoxelSize)
        {
            uint resolution = (uint)MathF.Ceiling(axisExtent / targetVoxelSize) + 1u;
            return Math.Clamp(resolution, MinResolution, MaxResolution);
        }

        private static void AddEdge(Dictionary<EdgeKey, int> edgeUseCounts, uint a, uint b)
        {
            var key = EdgeKey.Create(a, b);
            edgeUseCounts.TryGetValue(key, out int count);
            edgeUseCounts[key] = count + 1;
        }

        private readonly record struct EdgeKey(uint A, uint B)
        {
            public static EdgeKey Create(uint a, uint b) => new(Math.Min(a, b), Math.Max(a, b));
        }
    }
}
