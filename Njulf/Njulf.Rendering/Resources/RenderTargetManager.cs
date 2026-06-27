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
        public const Format SceneNormalFormat = Format.R16G16B16A16Sfloat;
        public const Format SceneMaterialFormat = Format.R16G16B16A16Sfloat;
        public const Format SsgiTraceSourceFormat = SceneColorFormat;
        public const Format SsgiFormat = Format.R16G16B16A16Sfloat;
        public const Format SsgiHitDistanceFormat = Format.R16Sfloat;
        public const Format SsgiDepthHistoryFormat = Format.R32Sfloat;
        public const Format SsgiNormalHistoryFormat = SceneNormalFormat;
        public const Format SsgiMomentsFormat = Format.R16G16Sfloat;
        public const Format SsgiHistoryLengthFormat = Format.R16Sfloat;
        public const Format GiFinalDiffuseFormat = Format.R16G16B16A16Sfloat;
        public const Format LdrSceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format SmaaEdgesFormat = Format.R8G8Unorm;
        public const Format SmaaBlendWeightsFormat = Format.R8G8B8A8Unorm;
        public const Format MotionVectorFormat = Format.R16G16Sfloat;
        public const Format WeightedOitAccumulationFormat = Format.R16G16B16A16Sfloat;
        public const Format WeightedOitRevealageFormat = Format.R8Unorm;

        private readonly VulkanContext _context;
        private readonly RenderGraph? _renderGraph;
        private bool _disposed;
        private RenderTarget? _sceneNormal;
        private RenderTarget? _sceneMaterial;
        private RenderTarget? _ssgiTraceSource;
        private RenderTarget? _ssgiRaw;
        private RenderTarget? _ssgiHitDistance;
        private RenderTarget? _ssgiFiltered;
        private RenderTarget? _ssgiHistoryA;
        private RenderTarget? _ssgiHistoryB;
        private RenderTarget? _ssgiDepthHistoryA;
        private RenderTarget? _ssgiDepthHistoryB;
        private RenderTarget? _ssgiNormalHistoryA;
        private RenderTarget? _ssgiNormalHistoryB;
        private RenderTarget? _ssgiMomentsA;
        private RenderTarget? _ssgiMomentsB;
        private RenderTarget? _ssgiHistoryLengthA;
        private RenderTarget? _ssgiHistoryLengthB;
        private RenderTarget? _giFinalDiffuse;

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

        private static readonly RenderTargetDescriptor WeightedOitAccumulationDescriptor = new(
            colorAttachment: true,
            sampled: true);

        private static readonly RenderTargetDescriptor WeightedOitRevealageDescriptor = new(
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
            Extent2D outputExtent,
            Format depthFormat,
            int bloomMipCount = 6,
            bool ambientOcclusionEnabled = true,
            bool globalIlluminationSsgiEnabled = true,
            float globalIlluminationResolutionScale = 0.5f,
            AntiAliasingMode antiAliasingMode = AntiAliasingMode.SmaaMedium,
            bool motionVectorsEnabled = false,
            bool fogEnabled = true,
            bool weightedOitEnabled = false,
            RenderGraph? renderGraph = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _renderGraph = renderGraph;
            SceneColor = new RenderTarget(_context, "HDR Scene Color", SceneColorFormat, extent, HdrSceneColorDescriptor);
            SceneDepth = new RenderTarget(_context, "Scene Depth", depthFormat, extent, SceneDepthDescriptor);
            FoggedSceneColor = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.FogOutput,
                "Fogged HDR Scene Color",
                FoggedSceneColorFormat,
                CalculateFoggedSceneColorExtent(extent, fogEnabled),
                FoggedSceneColorDescriptor);
            Extent2D ambientOcclusionExtent = ambientOcclusionEnabled
                ? CalculateAmbientOcclusionExtent(extent, 0.5f)
                : PlaceholderExtent;
            AmbientOcclusionRaw = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.AmbientOcclusionRaw,
                "Ambient Occlusion Raw",
                AmbientOcclusionFormat,
                ambientOcclusionExtent,
                AmbientOcclusionRawDescriptor);
            AmbientOcclusionBlurred = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.AmbientOcclusionBlurred,
                "Ambient Occlusion Blurred",
                AmbientOcclusionFormat,
                ambientOcclusionExtent,
                AmbientOcclusionBlurredDescriptor);
            AmbientOcclusionScratch = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.AmbientOcclusionScratch,
                "Ambient Occlusion Scratch",
                AmbientOcclusionFormat,
                ambientOcclusionExtent,
                StorageSampledDescriptor);
            RecreateSceneSurfaceTargets(extent, globalIlluminationSsgiEnabled);
            RecreateGlobalIlluminationTargets(extent, globalIlluminationResolutionScale, globalIlluminationSsgiEnabled);
            LdrSceneColor = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.LdrSceneColor,
                "LDR Scene Color",
                LdrSceneColorFormat,
                RequiresAntiAliasingTarget(antiAliasingMode) ? extent : PlaceholderExtent,
                LdrSceneColorDescriptor);
            SmaaEdges = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.SmaaEdges,
                "SMAA Edges",
                SmaaEdgesFormat,
                AntiAliasingSettings.IsSmaaMode(antiAliasingMode) ? extent : PlaceholderExtent,
                ColorSampledDescriptor);
            SmaaBlendWeights = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.SmaaBlendWeights,
                "SMAA Blend Weights",
                SmaaBlendWeightsFormat,
                AntiAliasingSettings.IsSmaaMode(antiAliasingMode) ? extent : PlaceholderExtent,
                ColorSampledDescriptor);
            MotionVectors = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.MotionVectors,
                "Motion Vectors",
                MotionVectorFormat,
                motionVectorsEnabled ? extent : PlaceholderExtent,
                ColorSampledDescriptor);
            TaaHistoryA = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.TaaHistory,
                "TAA History A",
                LdrSceneColorFormat,
                antiAliasingMode == AntiAliasingMode.Taa ? outputExtent : PlaceholderExtent,
                LdrSceneColorDescriptor);
            TaaHistoryB = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.TaaHistory,
                "TAA History B",
                LdrSceneColorFormat,
                antiAliasingMode == AntiAliasingMode.Taa ? outputExtent : PlaceholderExtent,
                LdrSceneColorDescriptor);
            WeightedOitAccumulation = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.WeightedOitAccumulation,
                "Weighted OIT Accumulation",
                WeightedOitAccumulationFormat,
                weightedOitEnabled ? extent : PlaceholderExtent,
                WeightedOitAccumulationDescriptor);
            WeightedOitRevealage = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.WeightedOitRevealage,
                "Weighted OIT Revealage",
                WeightedOitRevealageFormat,
                weightedOitEnabled ? extent : PlaceholderExtent,
                WeightedOitRevealageDescriptor);
            RecreateBloomTargets(extent, bloomMipCount);
        }

        public RenderTarget SceneColor { get; }
        public RenderTarget SceneDepth { get; }
        public RenderTarget FoggedSceneColor { get; }
        public RenderTarget AmbientOcclusionRaw { get; }
        public RenderTarget AmbientOcclusionBlurred { get; }
        public RenderTarget AmbientOcclusionScratch { get; }
        public RenderTarget SceneNormal => RequireTarget(_sceneNormal, nameof(SceneNormal));
        public RenderTarget SceneMaterial => RequireTarget(_sceneMaterial, nameof(SceneMaterial));
        public RenderTarget SsgiTraceSource => RequireTarget(_ssgiTraceSource, nameof(SsgiTraceSource));
        public RenderTarget SsgiRaw => RequireTarget(_ssgiRaw, nameof(SsgiRaw));
        public RenderTarget SsgiHitDistance => RequireTarget(_ssgiHitDistance, nameof(SsgiHitDistance));
        public RenderTarget SsgiFiltered => RequireTarget(_ssgiFiltered, nameof(SsgiFiltered));
        public RenderTarget SsgiHistoryA => RequireTarget(_ssgiHistoryA, nameof(SsgiHistoryA));
        public RenderTarget SsgiHistoryB => RequireTarget(_ssgiHistoryB, nameof(SsgiHistoryB));
        public RenderTarget SsgiDepthHistoryA => RequireTarget(_ssgiDepthHistoryA, nameof(SsgiDepthHistoryA));
        public RenderTarget SsgiDepthHistoryB => RequireTarget(_ssgiDepthHistoryB, nameof(SsgiDepthHistoryB));
        public RenderTarget SsgiNormalHistoryA => RequireTarget(_ssgiNormalHistoryA, nameof(SsgiNormalHistoryA));
        public RenderTarget SsgiNormalHistoryB => RequireTarget(_ssgiNormalHistoryB, nameof(SsgiNormalHistoryB));
        public RenderTarget SsgiMomentsA => RequireTarget(_ssgiMomentsA, nameof(SsgiMomentsA));
        public RenderTarget SsgiMomentsB => RequireTarget(_ssgiMomentsB, nameof(SsgiMomentsB));
        public RenderTarget SsgiHistoryLengthA => RequireTarget(_ssgiHistoryLengthA, nameof(SsgiHistoryLengthA));
        public RenderTarget SsgiHistoryLengthB => RequireTarget(_ssgiHistoryLengthB, nameof(SsgiHistoryLengthB));
        public RenderTarget GiFinalDiffuse => RequireTarget(_giFinalDiffuse, nameof(GiFinalDiffuse));
        public RenderTarget LdrSceneColor { get; }
        public RenderTarget SmaaEdges { get; }
        public RenderTarget SmaaBlendWeights { get; }
        public RenderTarget MotionVectors { get; }
        public RenderTarget TaaHistoryA { get; }
        public RenderTarget TaaHistoryB { get; }
        public RenderTarget WeightedOitAccumulation { get; }
        public RenderTarget WeightedOitRevealage { get; }
        public IReadOnlyList<RenderTarget> BloomMipChain => _bloomMipChain;
        public int BloomMipCount => _bloomMipChain.Count;
        public Extent2D BloomBaseExtent => _bloomMipChain.Count == 0 ? default : _bloomMipChain[0].Extent;
        public int ResizeCount { get; private set; }
        public int RenderTargetCount => 14 + OptionalRenderTargetCount + _bloomMipChain.Count;
        public ulong TotalEstimatedBytes =>
            SceneColor.EstimatedByteSize +
            SceneDepth.EstimatedByteSize +
            SumEnabledBytes(FoggedSceneColor) +
            AmbientOcclusionRenderTargetBytes +
            SceneSurfaceRenderTargetBytes +
            GlobalIlluminationRenderTargetBytes +
            AntiAliasingRenderTargetBytes +
            WeightedOitRenderTargetBytes +
            BloomRenderTargetBytes;
        public ulong AmbientOcclusionRenderTargetBytes => SumEnabledBytes(AmbientOcclusionRaw, AmbientOcclusionBlurred, AmbientOcclusionScratch);
        public ulong SceneSurfaceRenderTargetBytes => SumEnabledBytes(_sceneNormal, _sceneMaterial, _ssgiTraceSource);
        public ulong GlobalIlluminationRenderTargetBytes => SumEnabledBytes(
            _ssgiRaw,
            _ssgiHitDistance,
            _ssgiFiltered,
            _ssgiHistoryA,
            _ssgiHistoryB,
            _ssgiDepthHistoryA,
            _ssgiDepthHistoryB,
            _ssgiNormalHistoryA,
            _ssgiNormalHistoryB,
            _ssgiMomentsA,
            _ssgiMomentsB,
            _ssgiHistoryLengthA,
            _ssgiHistoryLengthB,
            _giFinalDiffuse);
        public ulong AntiAliasingRenderTargetBytes => SumEnabledBytes(LdrSceneColor, SmaaEdges, SmaaBlendWeights, MotionVectors, TaaHistoryA, TaaHistoryB);
        public ulong WeightedOitRenderTargetBytes => SumEnabledBytes(WeightedOitAccumulation, WeightedOitRevealage);
        public ulong BloomRenderTargetBytes => SumTargetBytes(_bloomMipChain);

        private readonly List<RenderTarget> _bloomMipChain = new();

        private int OptionalRenderTargetCount =>
            Count(_sceneNormal) +
            Count(_sceneMaterial) +
            Count(_ssgiTraceSource) +
            Count(_ssgiRaw) +
            Count(_ssgiHitDistance) +
            Count(_ssgiFiltered) +
            Count(_ssgiHistoryA) +
            Count(_ssgiHistoryB) +
            Count(_ssgiDepthHistoryA) +
            Count(_ssgiDepthHistoryB) +
            Count(_ssgiNormalHistoryA) +
            Count(_ssgiNormalHistoryB) +
            Count(_ssgiMomentsA) +
            Count(_ssgiMomentsB) +
            Count(_ssgiHistoryLengthA) +
            Count(_ssgiHistoryLengthB) +
            Count(_giFinalDiffuse);

        private static Extent2D PlaceholderExtent => new() { Width = 1, Height = 1 };

        public void Recreate(
            Extent2D extent,
            Extent2D outputExtent,
            float ambientOcclusionResolutionScale = 0.5f,
            float globalIlluminationResolutionScale = 0.5f,
            int bloomMipCount = 6,
            bool ambientOcclusionEnabled = true,
            bool globalIlluminationSsgiEnabled = true,
            AntiAliasingMode antiAliasingMode = AntiAliasingMode.SmaaMedium,
            bool motionVectorsEnabled = false,
            bool fogEnabled = true,
            bool weightedOitEnabled = false)
        {
            ulong before = TotalEstimatedBytes;
            RecreateIfDifferent(SceneColor, extent);
            RecreateIfDifferent(SceneDepth, extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.FogOutput, FoggedSceneColor, CalculateFoggedSceneColorExtent(extent, fogEnabled));
            RecreateAmbientOcclusionTargets(extent, ambientOcclusionResolutionScale, ambientOcclusionEnabled);
            RecreateSceneSurfaceTargets(extent, globalIlluminationSsgiEnabled);
            RecreateGlobalIlluminationTargets(extent, globalIlluminationResolutionScale, globalIlluminationSsgiEnabled);
            RecreateAntiAliasingTargets(extent, outputExtent, antiAliasingMode, motionVectorsEnabled);
            RecreateWeightedOitTargets(extent, weightedOitEnabled);
            RecreateBloomTargets(extent, bloomMipCount);
            if (TotalEstimatedBytes != before)
                ResizeCount++;
        }

        public void RecreateAntiAliasingTargets(Extent2D extent, Extent2D outputExtent, AntiAliasingMode mode, bool motionVectorsEnabled)
        {
            RecreateGraphOwnedTarget(RenderGraphResourceId.LdrSceneColor, LdrSceneColor, RequiresAntiAliasingTarget(mode) ? extent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.SmaaEdges, SmaaEdges, AntiAliasingSettings.IsSmaaMode(mode) ? extent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.SmaaBlendWeights, SmaaBlendWeights, AntiAliasingSettings.IsSmaaMode(mode) ? extent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.MotionVectors, MotionVectors, motionVectorsEnabled ? extent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.TaaHistory, TaaHistoryA, mode == AntiAliasingMode.Taa ? outputExtent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.TaaHistory, TaaHistoryB, mode == AntiAliasingMode.Taa ? outputExtent : PlaceholderExtent);
        }

        public void RecreateWeightedOitTargets(Extent2D extent, bool enabled)
        {
            Extent2D targetExtent = enabled ? extent : PlaceholderExtent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.WeightedOitAccumulation, WeightedOitAccumulation, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.WeightedOitRevealage, WeightedOitRevealage, targetExtent);
        }

        public void RecreateAmbientOcclusionTargets(Extent2D swapchainExtent, float resolutionScale, bool enabled)
        {
            Extent2D extent = enabled ? CalculateAmbientOcclusionExtent(swapchainExtent, resolutionScale) : PlaceholderExtent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.AmbientOcclusionRaw, AmbientOcclusionRaw, extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.AmbientOcclusionBlurred, AmbientOcclusionBlurred, extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.AmbientOcclusionScratch, AmbientOcclusionScratch, extent);
        }

        public void RecreateSceneSurfaceTargets(Extent2D extent, bool enabled)
        {
            if (!enabled)
            {
                ReleaseOptionalTarget(RenderGraphResourceId.SceneNormal, ref _sceneNormal);
                ReleaseOptionalTarget(RenderGraphResourceId.SceneMaterial, ref _sceneMaterial);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiTraceSource, ref _ssgiTraceSource);
                return;
            }

            CreateOrRecreateOptionalTarget(
                ref _sceneNormal,
                RenderGraphResourceId.SceneNormal,
                "Scene Normal",
                SceneNormalFormat,
                extent,
                ColorSampledDescriptor);
            CreateOrRecreateOptionalTarget(
                ref _sceneMaterial,
                RenderGraphResourceId.SceneMaterial,
                "Scene Material",
                SceneMaterialFormat,
                extent,
                ColorSampledDescriptor);
            CreateOrRecreateOptionalTarget(
                ref _ssgiTraceSource,
                RenderGraphResourceId.SsgiTraceSource,
                "SSGI Trace Source",
                SsgiTraceSourceFormat,
                extent,
                ColorSampledDescriptor);
        }

        public void RecreateGlobalIlluminationTargets(Extent2D swapchainExtent, float resolutionScale, bool enabled)
        {
            if (!enabled)
            {
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiRaw, ref _ssgiRaw);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiHitDistance, ref _ssgiHitDistance);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiFiltered, ref _ssgiFiltered);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiHistory, ref _ssgiHistoryA);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiHistory, ref _ssgiHistoryB);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiDepthHistory, ref _ssgiDepthHistoryA);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiDepthHistory, ref _ssgiDepthHistoryB);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiNormalHistory, ref _ssgiNormalHistoryA);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiNormalHistory, ref _ssgiNormalHistoryB);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiMoments, ref _ssgiMomentsA);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiMoments, ref _ssgiMomentsB);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiHistoryLength, ref _ssgiHistoryLengthA);
                ReleaseOptionalTarget(RenderGraphResourceId.SsgiHistoryLength, ref _ssgiHistoryLengthB);
                ReleaseOptionalTarget(RenderGraphResourceId.GiFinalDiffuse, ref _giFinalDiffuse);
                return;
            }

            Extent2D ssgiExtent = CalculateGlobalIlluminationExtent(swapchainExtent, resolutionScale);
            CreateOrRecreateOptionalTarget(ref _ssgiRaw, RenderGraphResourceId.SsgiRaw, "SSGI Raw", SsgiFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiHitDistance, RenderGraphResourceId.SsgiHitDistance, "SSGI Hit Distance", SsgiHitDistanceFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiFiltered, RenderGraphResourceId.SsgiFiltered, "SSGI Filtered", SsgiFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiHistoryA, RenderGraphResourceId.SsgiHistory, "SSGI History A", SsgiFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiHistoryB, RenderGraphResourceId.SsgiHistory, "SSGI History B", SsgiFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiDepthHistoryA, RenderGraphResourceId.SsgiDepthHistory, "SSGI Depth History A", SsgiDepthHistoryFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiDepthHistoryB, RenderGraphResourceId.SsgiDepthHistory, "SSGI Depth History B", SsgiDepthHistoryFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiNormalHistoryA, RenderGraphResourceId.SsgiNormalHistory, "SSGI Normal History A", SsgiNormalHistoryFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiNormalHistoryB, RenderGraphResourceId.SsgiNormalHistory, "SSGI Normal History B", SsgiNormalHistoryFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiMomentsA, RenderGraphResourceId.SsgiMoments, "SSGI Moments A", SsgiMomentsFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiMomentsB, RenderGraphResourceId.SsgiMoments, "SSGI Moments B", SsgiMomentsFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiHistoryLengthA, RenderGraphResourceId.SsgiHistoryLength, "SSGI History Length A", SsgiHistoryLengthFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _ssgiHistoryLengthB, RenderGraphResourceId.SsgiHistoryLength, "SSGI History Length B", SsgiHistoryLengthFormat, ssgiExtent, StorageSampledDescriptor);
            CreateOrRecreateOptionalTarget(ref _giFinalDiffuse, RenderGraphResourceId.GiFinalDiffuse, "GI Final Diffuse", GiFinalDiffuseFormat, swapchainExtent, StorageSampledDescriptor);
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

        public static Extent2D CalculateGlobalIlluminationExtent(Extent2D swapchainExtent, float resolutionScale)
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

        public static ulong CalculateSceneSurfaceRenderTargetBytes(Extent2D swapchainExtent, bool ssgiEnabled)
        {
            if (!ssgiEnabled)
                return 0;
            if (swapchainExtent.Width == 0 || swapchainExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainExtent), "Swapchain extent must be non-zero.");

            return checked(
                RenderTarget.CalculateByteSize(swapchainExtent.Width, swapchainExtent.Height, SceneNormalFormat) +
                RenderTarget.CalculateByteSize(swapchainExtent.Width, swapchainExtent.Height, SceneMaterialFormat) +
                RenderTarget.CalculateByteSize(swapchainExtent.Width, swapchainExtent.Height, SsgiTraceSourceFormat));
        }

        public static ulong CalculateGlobalIlluminationRenderTargetBytes(
            Extent2D swapchainExtent,
            float resolutionScale,
            bool ssgiEnabled)
        {
            if (!ssgiEnabled)
                return 0;
            if (swapchainExtent.Width == 0 || swapchainExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainExtent), "Swapchain extent must be non-zero.");

            Extent2D ssgiExtent = CalculateGlobalIlluminationExtent(swapchainExtent, resolutionScale);
            return checked(
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiHitDistanceFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiDepthHistoryFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiDepthHistoryFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiNormalHistoryFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiNormalHistoryFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiMomentsFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiMomentsFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiHistoryLengthFormat) +
                RenderTarget.CalculateByteSize(ssgiExtent.Width, ssgiExtent.Height, SsgiHistoryLengthFormat) +
                RenderTarget.CalculateByteSize(swapchainExtent.Width, swapchainExtent.Height, GiFinalDiffuseFormat));
        }

        private void RecreateBloomTargets(Extent2D extent, int requestedMipCount)
        {
            IReadOnlyList<Extent2D> mipExtents = CalculateBloomMipExtents(extent, requestedMipCount);
            ResizeTargetList(_bloomMipChain, mipExtents, RenderGraphResourceId.BloomChain, "Bloom Mip", SceneColorFormat, BloomMipDescriptor);
        }

        private void ResizeTargetList(
            List<RenderTarget> targets,
            IReadOnlyList<Extent2D> extents,
            RenderGraphResourceId id,
            string namePrefix,
            Format format,
            RenderTargetDescriptor descriptor)
        {
            while (targets.Count > extents.Count)
            {
                int last = targets.Count - 1;
                ReleaseOrDisposeOwnedTarget(id, targets[last]);
                targets.RemoveAt(last);
            }

            for (int i = 0; i < extents.Count; i++)
            {
                string name = i == 0 && namePrefix == "Bloom Mip"
                    ? "Bloom Extract"
                    : $"{namePrefix} {i}";

                if (i < targets.Count)
                    RecreateGraphOwnedTarget(id, targets[i], extents[i]);
                else
                    targets.Add(CreateGraphOwnedRenderTarget(id, name, format, extents[i], descriptor));
            }
        }

        private static void RecreateIfDifferent(RenderTarget target, Extent2D extent)
        {
            if (target.Extent.Width == extent.Width && target.Extent.Height == extent.Height)
                return;

            target.Recreate(extent);
        }

        private RenderTarget CreateGraphOwnedRenderTarget(
            RenderGraphResourceId id,
            string name,
            Format format,
            Extent2D extent,
            RenderTargetDescriptor descriptor)
        {
            return _renderGraph?.HasResource(id) == true
                ? _renderGraph.CreateOwnedRenderTarget(id, _context, name, format, extent, descriptor)
                : new RenderTarget(_context, name, format, extent, descriptor);
        }

        private void CreateOrRecreateOptionalTarget(
            ref RenderTarget? target,
            RenderGraphResourceId id,
            string name,
            Format format,
            Extent2D extent,
            RenderTargetDescriptor descriptor)
        {
            if (target == null)
            {
                target = CreateGraphOwnedRenderTarget(id, name, format, extent, descriptor);
                return;
            }

            RecreateGraphOwnedTarget(id, target, extent);
        }

        private void ReleaseOptionalTarget(RenderGraphResourceId id, ref RenderTarget? target)
        {
            if (target == null)
                return;

            if (_renderGraph?.OwnsResource(id) == true)
                _renderGraph.ReleaseOwnedRenderTarget(id, target);
            else
                target.Dispose();

            target = null;
        }

        private void RecreateGraphOwnedTarget(RenderGraphResourceId id, RenderTarget fallbackTarget, Extent2D extent)
        {
            if (_renderGraph?.OwnsResource(id) == true)
            {
                _renderGraph.RecreateOwnedRenderTarget(id, fallbackTarget, extent);
                return;
            }

            RecreateIfDifferent(fallbackTarget, extent);
        }

        private void ReleaseOrDisposeOwnedTarget(RenderGraphResourceId id, RenderTarget target)
        {
            if (_renderGraph?.OwnsResource(id) == true)
            {
                _renderGraph.ReleaseOwnedRenderTarget(id, target);
                return;
            }

            target.Dispose();
        }

        private void DisposeIfManagerOwned(RenderGraphResourceId id, RenderTarget? target)
        {
            if (target == null)
                return;
            if (_renderGraph?.OwnsResource(id) == true)
                return;

            target.Dispose();
        }

        private static ulong SumTargetBytes(IReadOnlyList<RenderTarget> targets)
        {
            ulong bytes = 0;
            for (int i = 0; i < targets.Count; i++)
                bytes += targets[i].EstimatedByteSize;
            return bytes;
        }

        private static ulong SumEnabledBytes(params RenderTarget?[] targets)
        {
            ulong bytes = 0;
            foreach (RenderTarget? target in targets)
            {
                if (target == null)
                    continue;
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

        private static int Count(RenderTarget? target)
        {
            return target == null ? 0 : 1;
        }

        private static RenderTarget RequireTarget(RenderTarget? target, string name)
        {
            return target ?? throw new InvalidOperationException($"{name} is not allocated for the active render target profile.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            SceneColor.Dispose();
            SceneDepth.Dispose();
            DisposeIfManagerOwned(RenderGraphResourceId.FogOutput, FoggedSceneColor);
            DisposeIfManagerOwned(RenderGraphResourceId.AmbientOcclusionRaw, AmbientOcclusionRaw);
            DisposeIfManagerOwned(RenderGraphResourceId.AmbientOcclusionBlurred, AmbientOcclusionBlurred);
            DisposeIfManagerOwned(RenderGraphResourceId.AmbientOcclusionScratch, AmbientOcclusionScratch);
            DisposeIfManagerOwned(RenderGraphResourceId.SceneNormal, _sceneNormal);
            DisposeIfManagerOwned(RenderGraphResourceId.SceneMaterial, _sceneMaterial);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiTraceSource, _ssgiTraceSource);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiRaw, _ssgiRaw);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiHitDistance, _ssgiHitDistance);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiFiltered, _ssgiFiltered);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiHistory, _ssgiHistoryA);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiHistory, _ssgiHistoryB);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiDepthHistory, _ssgiDepthHistoryA);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiDepthHistory, _ssgiDepthHistoryB);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiNormalHistory, _ssgiNormalHistoryA);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiNormalHistory, _ssgiNormalHistoryB);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiMoments, _ssgiMomentsA);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiMoments, _ssgiMomentsB);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiHistoryLength, _ssgiHistoryLengthA);
            DisposeIfManagerOwned(RenderGraphResourceId.SsgiHistoryLength, _ssgiHistoryLengthB);
            DisposeIfManagerOwned(RenderGraphResourceId.GiFinalDiffuse, _giFinalDiffuse);
            DisposeIfManagerOwned(RenderGraphResourceId.LdrSceneColor, LdrSceneColor);
            DisposeIfManagerOwned(RenderGraphResourceId.SmaaEdges, SmaaEdges);
            DisposeIfManagerOwned(RenderGraphResourceId.SmaaBlendWeights, SmaaBlendWeights);
            DisposeIfManagerOwned(RenderGraphResourceId.MotionVectors, MotionVectors);
            DisposeIfManagerOwned(RenderGraphResourceId.TaaHistory, TaaHistoryA);
            DisposeIfManagerOwned(RenderGraphResourceId.TaaHistory, TaaHistoryB);
            DisposeIfManagerOwned(RenderGraphResourceId.WeightedOitAccumulation, WeightedOitAccumulation);
            DisposeIfManagerOwned(RenderGraphResourceId.WeightedOitRevealage, WeightedOitRevealage);
            foreach (RenderTarget target in _bloomMipChain)
                DisposeIfManagerOwned(RenderGraphResourceId.BloomChain, target);
            GC.SuppressFinalize(this);
        }
    }
}
