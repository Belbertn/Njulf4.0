using System;
using System.Collections.Generic;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
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
        public const Format WeightedOitAccumulationFormat = Format.R16G16B16A16Sfloat;
        public const Format WeightedOitRevealageFormat = Format.R8Unorm;

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

        private static readonly RenderTargetDescriptor OitColorSampledDescriptor = new(
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
            SceneColor = RenderTarget.CreateGraphFacade(_context, "HDR Scene Color", SceneColorFormat, extent, HdrSceneColorDescriptor);
            SceneDepth = RenderTarget.CreateGraphFacade(_context, "Scene Depth", depthFormat, extent, SceneDepthDescriptor);
            FoggedSceneColor = RenderTarget.CreateGraphFacade(_context, "Fogged HDR Scene Color", FoggedSceneColorFormat, CalculateFoggedSceneColorExtent(extent, fogEnabled), FoggedSceneColorDescriptor);
            Extent2D ambientOcclusionExtent = ambientOcclusionEnabled
                ? CalculateAmbientOcclusionExtent(extent, 0.5f)
                : PlaceholderExtent;
            AmbientOcclusionRaw = RenderTarget.CreateGraphFacade(_context, "Ambient Occlusion Raw", AmbientOcclusionFormat, ambientOcclusionExtent, AmbientOcclusionRawDescriptor);
            AmbientOcclusionBlurred = RenderTarget.CreateGraphFacade(_context, "Ambient Occlusion Blurred", AmbientOcclusionFormat, ambientOcclusionExtent, AmbientOcclusionBlurredDescriptor);
            AmbientOcclusionScratch = RenderTarget.CreateGraphFacade(_context, "Ambient Occlusion Scratch", AmbientOcclusionFormat, ambientOcclusionExtent, StorageSampledDescriptor);
            LdrSceneColor = RenderTarget.CreateGraphFacade(_context, "LDR Scene Color", LdrSceneColorFormat, RequiresAntiAliasingTarget(antiAliasingMode) ? extent : PlaceholderExtent, LdrSceneColorDescriptor);
            SmaaEdges = RenderTarget.CreateGraphFacade(_context, "SMAA Edges", SmaaEdgesFormat, AntiAliasingSettings.IsSmaaMode(antiAliasingMode) ? extent : PlaceholderExtent, ColorSampledDescriptor);
            SmaaBlendWeights = RenderTarget.CreateGraphFacade(_context, "SMAA Blend Weights", SmaaBlendWeightsFormat, AntiAliasingSettings.IsSmaaMode(antiAliasingMode) ? extent : PlaceholderExtent, ColorSampledDescriptor);
            MotionVectors = RenderTarget.CreateGraphFacade(_context, "Motion Vectors", MotionVectorFormat, antiAliasingMode == AntiAliasingMode.Taa ? extent : PlaceholderExtent, ColorSampledDescriptor);
            WeightedOitAccumulation = RenderTarget.CreateGraphFacade(_context, "Weighted OIT Accumulation", WeightedOitAccumulationFormat, extent, OitColorSampledDescriptor);
            WeightedOitRevealage = RenderTarget.CreateGraphFacade(_context, "Weighted OIT Revealage", WeightedOitRevealageFormat, extent, OitColorSampledDescriptor);
            TaaHistoryA = RenderTarget.CreateGraphFacade(_context, "TAA History A", LdrSceneColorFormat, antiAliasingMode == AntiAliasingMode.Taa ? extent : PlaceholderExtent, LdrSceneColorDescriptor);
            TaaHistoryB = RenderTarget.CreateGraphFacade(_context, "TAA History B", LdrSceneColorFormat, antiAliasingMode == AntiAliasingMode.Taa ? extent : PlaceholderExtent, LdrSceneColorDescriptor);
            CreateBloomFacades(extent, bloomMipCount);
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
        public RenderTarget WeightedOitAccumulation { get; }
        public RenderTarget WeightedOitRevealage { get; }
        public RenderTarget TaaHistoryA { get; }
        public RenderTarget TaaHistoryB { get; }
        public IReadOnlyList<RenderTarget> BloomMipChain => _bloomMipChain;
        public int BloomMipCount => _bloomMipChain.Count;
        public Extent2D BloomBaseExtent => _bloomMipChain.Count == 0 ? default : _bloomMipChain[0].Extent;
        public int ResizeCount { get; private set; }
        public int RenderTargetCount => 14 + _bloomMipChain.Count;
        public ulong TotalEstimatedBytes =>
            SceneColor.EstimatedByteSize +
            SceneDepth.EstimatedByteSize +
            SumEnabledBytes(FoggedSceneColor) +
            WeightedOitRenderTargetBytes +
            AmbientOcclusionRenderTargetBytes +
            AntiAliasingRenderTargetBytes +
            BloomRenderTargetBytes;
        public ulong AmbientOcclusionRenderTargetBytes => SumEnabledBytes(AmbientOcclusionRaw, AmbientOcclusionBlurred, AmbientOcclusionScratch);
        public ulong AntiAliasingRenderTargetBytes => SumEnabledBytes(LdrSceneColor, SmaaEdges, SmaaBlendWeights, MotionVectors, TaaHistoryA, TaaHistoryB);
        public ulong WeightedOitRenderTargetBytes => SumEnabledBytes(WeightedOitAccumulation, WeightedOitRevealage);
        public ulong BloomRenderTargetBytes => SumTargetBytes(_bloomMipChain);

        private readonly List<RenderTarget> _bloomMipChain = new();

        private static Extent2D PlaceholderExtent => new() { Width = 1, Height = 1 };

        public void BindGraphImages(IReadOnlyDictionary<RenderGraphResourceHandle, RenderGraphAllocatedImage> graphImages)
        {
            if (graphImages == null)
                throw new ArgumentNullException(nameof(graphImages));

            ulong before = TotalEstimatedBytes;
            UnbindGraphFacades();

            var imagesByName = new Dictionary<string, RenderGraphAllocatedImage>(StringComparer.Ordinal);
            foreach (RenderGraphAllocatedImage image in graphImages.Values)
                imagesByName[image.Name] = image;

            BindIfPresent(imagesByName, ProductionRenderGraphResources.HdrSceneColorName, SceneColor);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.SceneDepthName, SceneDepth);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.FoggedSceneColorName, FoggedSceneColor);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.AmbientOcclusionRawName, AmbientOcclusionRaw);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.AmbientOcclusionBlurredName, AmbientOcclusionBlurred);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.AmbientOcclusionScratchName, AmbientOcclusionScratch);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.LdrSceneColorName, LdrSceneColor);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.SmaaEdgesName, SmaaEdges);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.SmaaBlendWeightsName, SmaaBlendWeights);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.MotionVectorsName, MotionVectors);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.WeightedOitAccumulationName, WeightedOitAccumulation);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.WeightedOitRevealageName, WeightedOitRevealage);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.TaaHistoryAName, TaaHistoryA);
            BindIfPresent(imagesByName, ProductionRenderGraphResources.TaaHistoryBName, TaaHistoryB);

            SynchronizeBloomFacades(imagesByName);

            if (TotalEstimatedBytes != before)
                ResizeCount++;
        }

        private void UnbindGraphFacades()
        {
            SceneColor.UnbindGraphImage(SceneColor.Extent);
            SceneDepth.UnbindGraphImage(SceneDepth.Extent);
            FoggedSceneColor.UnbindGraphImage(PlaceholderExtent);
            AmbientOcclusionRaw.UnbindGraphImage(PlaceholderExtent);
            AmbientOcclusionBlurred.UnbindGraphImage(PlaceholderExtent);
            AmbientOcclusionScratch.UnbindGraphImage(PlaceholderExtent);
            LdrSceneColor.UnbindGraphImage(PlaceholderExtent);
            SmaaEdges.UnbindGraphImage(PlaceholderExtent);
            SmaaBlendWeights.UnbindGraphImage(PlaceholderExtent);
            MotionVectors.UnbindGraphImage(PlaceholderExtent);
            WeightedOitAccumulation.UnbindGraphImage(PlaceholderExtent);
            WeightedOitRevealage.UnbindGraphImage(PlaceholderExtent);
            TaaHistoryA.UnbindGraphImage(PlaceholderExtent);
            TaaHistoryB.UnbindGraphImage(PlaceholderExtent);

            foreach (RenderTarget target in _bloomMipChain)
                target.UnbindGraphImage(target.Extent);
        }

        private void SynchronizeBloomFacades(IReadOnlyDictionary<string, RenderGraphAllocatedImage> imagesByName)
        {
            int graphBloomCount = 0;
            while (true)
            {
                string name = graphBloomCount == 0
                    ? ProductionRenderGraphResources.BloomExtractName
                    : $"Bloom Mip {graphBloomCount}";
                if (!imagesByName.ContainsKey(name))
                    break;
                graphBloomCount++;
            }

            while (_bloomMipChain.Count > graphBloomCount)
            {
                int last = _bloomMipChain.Count - 1;
                _bloomMipChain[last].Dispose();
                _bloomMipChain.RemoveAt(last);
            }

            for (int i = 0; i < graphBloomCount; i++)
            {
                string name = i == 0 ? ProductionRenderGraphResources.BloomExtractName : $"Bloom Mip {i}";
                if (!imagesByName.TryGetValue(name, out RenderGraphAllocatedImage image))
                    continue;

                while (_bloomMipChain.Count <= i)
                {
                    string facadeName = _bloomMipChain.Count == 0
                        ? ProductionRenderGraphResources.BloomExtractName
                        : $"Bloom Mip {_bloomMipChain.Count}";
                    _bloomMipChain.Add(RenderTarget.CreateGraphFacade(
                        _context,
                        facadeName,
                        SceneColorFormat,
                        image.Extent,
                        BloomMipDescriptor));
                }

                BindIfPresent(imagesByName, name, _bloomMipChain[i]);
            }
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

        private void CreateBloomFacades(Extent2D extent, int requestedMipCount)
        {
            IReadOnlyList<Extent2D> mipExtents = CalculateBloomMipExtents(extent, requestedMipCount);
            for (int i = 0; i < mipExtents.Count; i++)
            {
                string name = i == 0
                    ? ProductionRenderGraphResources.BloomExtractName
                    : $"Bloom Mip {i}";
                _bloomMipChain.Add(RenderTarget.CreateGraphFacade(
                    _context,
                    name,
                    SceneColorFormat,
                    mipExtents[i],
                    BloomMipDescriptor));
            }
        }

        private static void BindIfPresent(
            IReadOnlyDictionary<string, RenderGraphAllocatedImage> imagesByName,
            string name,
            RenderTarget target)
        {
            if (imagesByName.TryGetValue(name, out RenderGraphAllocatedImage image))
                target.BindGraphImage(image);
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
            WeightedOitAccumulation.Dispose();
            WeightedOitRevealage.Dispose();
            TaaHistoryA.Dispose();
            TaaHistoryB.Dispose();
            foreach (RenderTarget target in _bloomMipChain)
                target.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
