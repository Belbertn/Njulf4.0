using System;
using System.Collections.Generic;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed class RenderTargetManager : IDisposable
    {
        public const Format SceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format FoggedSceneColorFormat = SceneColorFormat;
        public const Format AmbientOcclusionFormat = Format.R8Unorm;
        public const Format LdrSceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format SmaaEdgesFormat = Format.R8G8Unorm;
        public const Format SmaaBlendWeightsFormat = Format.R8G8B8A8Unorm;
        public const Format MotionVectorFormat = Format.R16G16Sfloat;

        private readonly VulkanContext _context;
        private bool _disposed;

        private static readonly RenderTargetDescriptor HdrSceneColorDescriptor = new(
            colorAttachment: true,
            sampled: true,
            allowDriverCompression: true);

        private static readonly RenderTargetDescriptor SceneDepthDescriptor = new(
            colorAttachment: false,
            sampled: true,
            depthAttachment: true);

        private static readonly RenderTargetDescriptor FoggedSceneColorDescriptor = new(
            colorAttachment: false,
            sampled: true,
            storage: true,
            allowDriverCompression: true);

        private static readonly RenderTargetDescriptor AmbientOcclusionRawDescriptor = new(
            colorAttachment: false,
            sampled: true,
            storage: true);

        private static readonly RenderTargetDescriptor AmbientOcclusionBlurredDescriptor = new(
            colorAttachment: false,
            sampled: true,
            storage: true);

        private static readonly RenderTargetDescriptor StorageSampledDescriptor = new(
            colorAttachment: false,
            sampled: true,
            storage: true);

        private static readonly RenderTargetDescriptor ColorSampledDescriptor = new(
            colorAttachment: true,
            sampled: true);

        private static readonly RenderTargetDescriptor LdrSceneColorDescriptor = new(
            colorAttachment: true,
            sampled: true,
            allowDriverCompression: true);

        private static readonly RenderTargetDescriptor BloomMipDescriptor = new(
            colorAttachment: false,
            sampled: true,
            storage: true);

        public RenderTargetManager(
            VulkanContext context,
            Extent2D extent,
            Format depthFormat,
            int bloomMipCount = 6,
            bool ambientOcclusionEnabled = true,
            AntiAliasingMode antiAliasingMode = AntiAliasingMode.SmaaMedium,
            bool fogEnabled = true)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            SceneColor = new RenderTarget(_context, "HDR Scene Color", SceneColorFormat, extent, HdrSceneColorDescriptor);
            SceneDepth = new RenderTarget(_context, "Scene Depth", depthFormat, extent, SceneDepthDescriptor);
            FoggedSceneColor = new RenderTarget(_context, "Fogged HDR Scene Color", FoggedSceneColorFormat, CalculateFoggedSceneColorExtent(extent, fogEnabled), FoggedSceneColorDescriptor);
            Extent2D ambientOcclusionExtent = ambientOcclusionEnabled
                ? CalculateAmbientOcclusionExtent(extent, 0.5f)
                : PlaceholderExtent;
            AmbientOcclusionRaw = new RenderTarget(_context, "Ambient Occlusion Raw", AmbientOcclusionFormat, ambientOcclusionExtent, AmbientOcclusionRawDescriptor);
            AmbientOcclusionBlurred = new RenderTarget(_context, "Ambient Occlusion Blurred", AmbientOcclusionFormat, ambientOcclusionExtent, AmbientOcclusionBlurredDescriptor);
            AmbientOcclusionScratch = new RenderTarget(_context, "Ambient Occlusion Scratch", AmbientOcclusionFormat, ambientOcclusionExtent, StorageSampledDescriptor);
            LdrSceneColor = new RenderTarget(_context, "LDR Scene Color", LdrSceneColorFormat, RequiresAntiAliasingTarget(antiAliasingMode) ? extent : PlaceholderExtent, LdrSceneColorDescriptor);
            SmaaEdges = new RenderTarget(_context, "SMAA Edges", SmaaEdgesFormat, AntiAliasingSettings.IsSmaaMode(antiAliasingMode) ? extent : PlaceholderExtent, ColorSampledDescriptor);
            SmaaBlendWeights = new RenderTarget(_context, "SMAA Blend Weights", SmaaBlendWeightsFormat, AntiAliasingSettings.IsSmaaMode(antiAliasingMode) ? extent : PlaceholderExtent, ColorSampledDescriptor);
            MotionVectors = new RenderTarget(_context, "Motion Vectors", MotionVectorFormat, antiAliasingMode == AntiAliasingMode.Taa ? extent : PlaceholderExtent, ColorSampledDescriptor);
            TaaHistoryA = new RenderTarget(_context, "TAA History A", LdrSceneColorFormat, antiAliasingMode == AntiAliasingMode.Taa ? extent : PlaceholderExtent, LdrSceneColorDescriptor);
            TaaHistoryB = new RenderTarget(_context, "TAA History B", LdrSceneColorFormat, antiAliasingMode == AntiAliasingMode.Taa ? extent : PlaceholderExtent, LdrSceneColorDescriptor);
            RecreateBloomTargets(extent, bloomMipCount);
        }

        public RenderTarget SceneColor { get; }
        public RenderTarget SceneDepth { get; }
        public RenderTarget FoggedSceneColor { get; }
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
        public int BloomMipCount => _bloomMipChain.Count;
        public Extent2D BloomBaseExtent => _bloomMipChain.Count == 0 ? default : _bloomMipChain[0].Extent;
        public int ResizeCount { get; private set; }
        public int RenderTargetCount => 12 + _bloomMipChain.Count;
        public ulong TotalEstimatedBytes =>
            SceneColor.EstimatedByteSize +
            SceneDepth.EstimatedByteSize +
            SumEnabledBytes(FoggedSceneColor) +
            AmbientOcclusionRenderTargetBytes +
            AntiAliasingRenderTargetBytes +
            BloomRenderTargetBytes;
        public ulong AmbientOcclusionRenderTargetBytes => SumEnabledBytes(AmbientOcclusionRaw, AmbientOcclusionBlurred, AmbientOcclusionScratch);
        public ulong AntiAliasingRenderTargetBytes => SumEnabledBytes(LdrSceneColor, SmaaEdges, SmaaBlendWeights, MotionVectors, TaaHistoryA, TaaHistoryB);
        public ulong BloomRenderTargetBytes => SumTargetBytes(_bloomMipChain);

        private readonly List<RenderTarget> _bloomMipChain = new();

        private static Extent2D PlaceholderExtent => new() { Width = 1, Height = 1 };

        public void Recreate(
            Extent2D extent,
            float ambientOcclusionResolutionScale = 0.5f,
            int bloomMipCount = 6,
            bool ambientOcclusionEnabled = true,
            AntiAliasingMode antiAliasingMode = AntiAliasingMode.SmaaMedium,
            bool fogEnabled = true)
        {
            ulong before = TotalEstimatedBytes;
            RecreateIfDifferent(SceneColor, extent);
            RecreateIfDifferent(SceneDepth, extent);
            RecreateIfDifferent(FoggedSceneColor, CalculateFoggedSceneColorExtent(extent, fogEnabled));
            RecreateAmbientOcclusionTargets(extent, ambientOcclusionResolutionScale, ambientOcclusionEnabled);
            RecreateAntiAliasingTargets(extent, antiAliasingMode);
            RecreateBloomTargets(extent, bloomMipCount);
            if (TotalEstimatedBytes != before)
                ResizeCount++;
        }

        public void RecreateAntiAliasingTargets(Extent2D extent, AntiAliasingMode mode)
        {
            RecreateIfDifferent(LdrSceneColor, RequiresAntiAliasingTarget(mode) ? extent : PlaceholderExtent);
            RecreateIfDifferent(SmaaEdges, AntiAliasingSettings.IsSmaaMode(mode) ? extent : PlaceholderExtent);
            RecreateIfDifferent(SmaaBlendWeights, AntiAliasingSettings.IsSmaaMode(mode) ? extent : PlaceholderExtent);
            RecreateIfDifferent(MotionVectors, mode == AntiAliasingMode.Taa ? extent : PlaceholderExtent);
            RecreateIfDifferent(TaaHistoryA, mode == AntiAliasingMode.Taa ? extent : PlaceholderExtent);
            RecreateIfDifferent(TaaHistoryB, mode == AntiAliasingMode.Taa ? extent : PlaceholderExtent);
        }

        public void RecreateAmbientOcclusionTargets(Extent2D swapchainExtent, float resolutionScale, bool enabled)
        {
            Extent2D extent = enabled ? CalculateAmbientOcclusionExtent(swapchainExtent, resolutionScale) : PlaceholderExtent;
            RecreateIfDifferent(AmbientOcclusionRaw, extent);
            RecreateIfDifferent(AmbientOcclusionBlurred, extent);
            RecreateIfDifferent(AmbientOcclusionScratch, extent);
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

        public static Extent2D CalculateFoggedSceneColorExtent(Extent2D swapchainExtent, bool enabled)
        {
            if (swapchainExtent.Width == 0 || swapchainExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainExtent), "Swapchain extent must be non-zero.");

            return enabled ? swapchainExtent : PlaceholderExtent;
        }

        public static ulong CalculateBloomRenderTargetBytes(Extent2D swapchainExtent, int requestedMipCount)
        {
            IReadOnlyList<Extent2D> extents = CalculateBloomMipExtents(swapchainExtent, requestedMipCount);
            ulong bytes = 0;
            for (int i = 0; i < extents.Count; i++)
                bytes += RenderTarget.CalculateByteSize(extents[i].Width, extents[i].Height, SceneColorFormat);
            return bytes;
        }

        private void RecreateBloomTargets(Extent2D extent, int requestedMipCount)
        {
            IReadOnlyList<Extent2D> mipExtents = CalculateBloomMipExtents(extent, requestedMipCount);
            ResizeTargetList(_bloomMipChain, mipExtents, "Bloom Mip");
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
                    RecreateIfDifferent(targets[i], extents[i]);
                else
                    targets.Add(new RenderTarget(_context, name, SceneColorFormat, extents[i], BloomMipDescriptor));
            }
        }

        private static void RecreateIfDifferent(RenderTarget target, Extent2D extent)
        {
            if (target.Extent.Width == extent.Width && target.Extent.Height == extent.Height)
                return;

            target.Recreate(extent);
        }

        private static ulong SumTargetBytes(IReadOnlyList<RenderTarget> targets)
        {
            ulong bytes = 0;
            for (int i = 0; i < targets.Count; i++)
                bytes += targets[i].EstimatedByteSize;
            return bytes;
        }

        private static ulong SumEnabledBytes(params RenderTarget[] targets)
        {
            ulong bytes = 0;
            foreach (RenderTarget target in targets)
            {
                if (target.Extent.Width == 1 && target.Extent.Height == 1)
                    continue;

                bytes += target.EstimatedByteSize;
            }

            return bytes;
        }

        private static bool RequiresAntiAliasingTarget(AntiAliasingMode mode)
        {
            return mode != AntiAliasingMode.None;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            SceneColor.Dispose();
            SceneDepth.Dispose();
            FoggedSceneColor.Dispose();
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
            GC.SuppressFinalize(this);
        }
    }
}
