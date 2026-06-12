using System;
using System.Collections.Generic;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed class RenderTargetManager : IDisposable
    {
        public const Format SceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format AmbientOcclusionFormat = Format.R8Unorm;
        public const Format LdrSceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format SmaaEdgesFormat = Format.R8G8Unorm;
        public const Format SmaaBlendWeightsFormat = Format.R8G8B8A8Unorm;
        public const Format MotionVectorFormat = Format.R16G16Sfloat;

        private readonly VulkanContext _context;
        private bool _disposed;

        public RenderTargetManager(VulkanContext context, Extent2D extent)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            SceneColor = new RenderTarget(_context, "HDR Scene Color", SceneColorFormat, extent);
            Extent2D ambientOcclusionExtent = CalculateAmbientOcclusionExtent(extent, 0.5f);
            AmbientOcclusionRaw = new RenderTarget(_context, "Ambient Occlusion Raw", AmbientOcclusionFormat, ambientOcclusionExtent, ImageUsageFlags.StorageBit);
            AmbientOcclusionBlurred = new RenderTarget(_context, "Ambient Occlusion Blurred", AmbientOcclusionFormat, ambientOcclusionExtent, ImageUsageFlags.StorageBit);
            AmbientOcclusionScratch = new RenderTarget(_context, "Ambient Occlusion Scratch", AmbientOcclusionFormat, ambientOcclusionExtent, ImageUsageFlags.StorageBit);
            LdrSceneColor = new RenderTarget(_context, "LDR Scene Color", LdrSceneColorFormat, extent);
            SmaaEdges = new RenderTarget(_context, "SMAA Edges", SmaaEdgesFormat, extent);
            SmaaBlendWeights = new RenderTarget(_context, "SMAA Blend Weights", SmaaBlendWeightsFormat, extent);
            MotionVectors = new RenderTarget(_context, "Motion Vectors", MotionVectorFormat, extent);
            TaaHistoryA = new RenderTarget(_context, "TAA History A", LdrSceneColorFormat, extent);
            TaaHistoryB = new RenderTarget(_context, "TAA History B", LdrSceneColorFormat, extent);
            RecreateBloomTargets(extent, BindlessIndex.MaxBloomMipTextures);
        }

        public RenderTarget SceneColor { get; }
        public RenderTarget AmbientOcclusionRaw { get; }
        public RenderTarget AmbientOcclusionBlurred { get; }
        public RenderTarget AmbientOcclusionScratch { get; }
        public RenderTarget LdrSceneColor { get; }
        public RenderTarget SmaaEdges { get; }
        public RenderTarget SmaaBlendWeights { get; }
        public RenderTarget MotionVectors { get; }
        public RenderTarget TaaHistoryA { get; }
        public RenderTarget TaaHistoryB { get; }
        public IReadOnlyList<RenderTarget> BloomMipChain => _bloomMipChain;
        public IReadOnlyList<RenderTarget> BloomScratchChain => _bloomScratchChain;
        public int BloomMipCount => _bloomMipChain.Count;
        public Extent2D BloomBaseExtent => _bloomMipChain.Count == 0 ? default : _bloomMipChain[0].Extent;

        private readonly List<RenderTarget> _bloomMipChain = new();
        private readonly List<RenderTarget> _bloomScratchChain = new();

        public void Recreate(Extent2D extent, float ambientOcclusionResolutionScale = 0.5f)
        {
            SceneColor.Recreate(extent);
            RecreateAmbientOcclusionTargets(extent, ambientOcclusionResolutionScale);
            RecreateAntiAliasingTargets(extent);
            RecreateBloomTargets(extent, BindlessIndex.MaxBloomMipTextures);
        }

        public void RecreateAntiAliasingTargets(Extent2D extent)
        {
            LdrSceneColor.Recreate(extent);
            SmaaEdges.Recreate(extent);
            SmaaBlendWeights.Recreate(extent);
            MotionVectors.Recreate(extent);
            TaaHistoryA.Recreate(extent);
            TaaHistoryB.Recreate(extent);
        }

        public void RecreateAmbientOcclusionTargets(Extent2D swapchainExtent, float resolutionScale)
        {
            Extent2D extent = CalculateAmbientOcclusionExtent(swapchainExtent, resolutionScale);
            AmbientOcclusionRaw.Recreate(extent);
            AmbientOcclusionBlurred.Recreate(extent);
            AmbientOcclusionScratch.Recreate(extent);
        }

        public static Extent2D CalculateAmbientOcclusionExtent(Extent2D swapchainExtent, float resolutionScale)
        {
            if (swapchainExtent.Width == 0 || swapchainExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainExtent), "Swapchain extent must be non-zero.");

            float scale = resolutionScale <= 0.375f ? 0.25f : resolutionScale <= 0.75f ? 0.5f : 1.0f;
            return new Extent2D
            {
                Width = Math.Max(1u, (uint)MathF.Ceiling(swapchainExtent.Width * scale)),
                Height = Math.Max(1u, (uint)MathF.Ceiling(swapchainExtent.Height * scale))
            };
        }

        public static IReadOnlyList<Extent2D> CalculateBloomMipExtents(Extent2D swapchainExtent, int requestedMipCount)
        {
            if (swapchainExtent.Width == 0 || swapchainExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainExtent), "Swapchain extent must be non-zero.");

            int mipCount = requestedMipCount < 1
                ? 1
                : requestedMipCount > BindlessIndex.MaxBloomMipTextures
                    ? BindlessIndex.MaxBloomMipTextures
                    : requestedMipCount;

            var extents = new List<Extent2D>(mipCount);
            uint width = Math.Max(1u, swapchainExtent.Width / 2u);
            uint height = Math.Max(1u, swapchainExtent.Height / 2u);

            for (int i = 0; i < mipCount; i++)
            {
                extents.Add(new Extent2D { Width = width, Height = height });
                if (width == 1 && height == 1)
                    break;

                width = Math.Max(1u, width / 2u);
                height = Math.Max(1u, height / 2u);
            }

            return extents;
        }

        private void RecreateBloomTargets(Extent2D extent, int requestedMipCount)
        {
            IReadOnlyList<Extent2D> mipExtents = CalculateBloomMipExtents(extent, requestedMipCount);
            ResizeTargetList(_bloomMipChain, mipExtents, "Bloom Mip");
            ResizeTargetList(_bloomScratchChain, mipExtents, "Bloom Scratch Mip");
        }

        private void ResizeTargetList(List<RenderTarget> targets, IReadOnlyList<Extent2D> extents, string namePrefix)
        {
            while (targets.Count > extents.Count)
            {
                int last = targets.Count - 1;
                targets[last].Dispose();
                targets.RemoveAt(last);
            }

            for (int i = 0; i < extents.Count; i++)
            {
                string name = i == 0 && namePrefix == "Bloom Mip"
                    ? "Bloom Extract"
                    : $"{namePrefix} {i}";

                if (i < targets.Count)
                    targets[i].Recreate(extents[i]);
                else
                    targets.Add(new RenderTarget(_context, name, SceneColorFormat, extents[i], ImageUsageFlags.StorageBit));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            SceneColor.Dispose();
            AmbientOcclusionRaw.Dispose();
            AmbientOcclusionBlurred.Dispose();
            AmbientOcclusionScratch.Dispose();
            LdrSceneColor.Dispose();
            SmaaEdges.Dispose();
            SmaaBlendWeights.Dispose();
            MotionVectors.Dispose();
            TaaHistoryA.Dispose();
            TaaHistoryB.Dispose();
            foreach (RenderTarget target in _bloomMipChain)
                target.Dispose();
            foreach (RenderTarget target in _bloomScratchChain)
                target.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
