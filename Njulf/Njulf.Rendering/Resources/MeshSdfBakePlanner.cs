using System;
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
        public const uint MaxResolution = 64;
        private const float TargetVoxelFractionOfMaxExtent = 0.025f;
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
            Vector3 paddedExtent = Vector3.Max(paddedMax - paddedMin, new Vector3(voxelSize));

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
                descriptor.Extent.Width <= 1 ? 0.0f : x / (float)(descriptor.Extent.Width - 1),
                descriptor.Extent.Height <= 1 ? 0.0f : y / (float)(descriptor.Extent.Height - 1),
                descriptor.Extent.Depth <= 1 ? 0.0f : z / (float)(descriptor.Extent.Depth - 1));

            return new MeshSdfVoxelAddress(
                descriptor.BoundsMin + descriptor.BoundsExtent * uv,
                uv);
        }

        private static uint ResolveAxisResolution(float axisExtent, float targetVoxelSize)
        {
            uint resolution = (uint)MathF.Ceiling(axisExtent / targetVoxelSize) + 1u;
            return Math.Clamp(resolution, MinResolution, MaxResolution);
        }
    }
}
