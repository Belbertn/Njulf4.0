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
        public const Format MaterialTransportProvenanceFormat = Format.R8Unorm;
        public const Format LdrSceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format SmaaEdgesFormat = Format.R8G8Unorm;
        public const Format SmaaBlendWeightsFormat = Format.R8G8B8A8Unorm;
        public const Format MotionVectorFormat = Format.R16G16Sfloat;
        public const Format WeightedOitAccumulationFormat = Format.R16G16B16A16Sfloat;
        public const Format WeightedOitRevealageFormat = Format.R8Unorm;
        public const Format NearFieldResidualRadianceFormat = Format.R16G16B16A16Sfloat;
        public const Format NearFieldResidualMomentsFormat = Format.R16G16Sfloat;
        public const Format NearFieldResidualValidityFormat = Format.R32Uint;
        public const Format NearFieldResidualNormalsFormat = Format.R16G16B16A16Sfloat;
        public const Format GiCausticReceiverPayloadFormat =
            GiCausticScreenGpuAbi.ReceiverPayloadFormat;
        public const Format GiCausticRadianceFormat =
            GiCausticScreenGpuAbi.RadianceFormat;
        public const Format GiCausticMomentsFormat =
            GiCausticScreenGpuAbi.MomentsFormat;

        private readonly VulkanContext _context;
        private readonly RenderGraph? _renderGraph;
        private float _nearFieldResidualResolutionScale =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference
                .ResolutionScale;
        private bool _disposed;

        private static readonly RenderTargetDescriptor HdrSceneColorDescriptor = new(
            colorAttachment: true,
            sampled: true,
            // SceneColor is the sole linear evidence source. No other
            // production render target pays the transfer-source usage cost.
            transferSource: true,
            allowDriverCompression: true);

        private static readonly RenderTargetDescriptor HdrSceneColorStorageDescriptor = new(
            colorAttachment: true,
            sampled: true,
            storage: true,
            transferSource: true,
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
            float ambientOcclusionResolutionScale = 0.5f,
            AntiAliasingMode antiAliasingMode = AntiAliasingMode.SmaaMedium,
            bool motionVectorsEnabled = false,
            bool fogEnabled = true,
            bool weightedOitEnabled = false,
            RenderGraph? renderGraph = null,
            bool materialTransportProvenanceEnabled = false,
            bool nearFieldResidualEnabled = false,
            SimpleDdgiNearFieldResidualLayout nearFieldResidualLayout = default,
            bool giCausticEnabled = false,
            GiCausticScreenResolveLayout giCausticScreenLayout = default)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _renderGraph = renderGraph;
            SceneColor = new RenderTarget(
                _context,
                "HDR Scene Color",
                SceneColorFormat,
                extent,
                nearFieldResidualEnabled || giCausticEnabled
                    ? HdrSceneColorStorageDescriptor
                    : HdrSceneColorDescriptor);
            SceneDepth = new RenderTarget(_context, "Scene Depth", depthFormat, extent, SceneDepthDescriptor);
            _renderGraph?.RegisterImportedRenderTarget(RenderGraphResourceId.SceneColor, SceneColor);
            _renderGraph?.RegisterImportedRenderTarget(RenderGraphResourceId.SceneDepth, SceneDepth);
            FoggedSceneColor = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.FogOutput,
                "Fogged HDR Scene Color",
                FoggedSceneColorFormat,
                CalculateFoggedSceneColorExtent(extent, fogEnabled),
                FoggedSceneColorDescriptor);
            Extent2D ambientOcclusionExtent = ambientOcclusionEnabled
                ? CalculateAmbientOcclusionExtent(extent, ambientOcclusionResolutionScale)
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
            MaterialTransportProvenance = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.MaterialTransportProvenance,
                "Material Transport Provenance",
                MaterialTransportProvenanceFormat,
                materialTransportProvenanceEnabled ? extent : PlaceholderExtent,
                ColorSampledDescriptor);
            if (nearFieldResidualEnabled)
            {
                if (!nearFieldResidualLayout.IsValid)
                {
                    nearFieldResidualLayout =
                        SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                            checked((int)extent.Width),
                            checked((int)extent.Height),
                            SimpleDdgiNearFieldResidualProfile
                                .HalfResolutionReference,
                            ulong.MaxValue);
                }
                if (!nearFieldResidualLayout.IsValid ||
                    nearFieldResidualLayout.SourceWidth != checked((int)extent.Width) ||
                    nearFieldResidualLayout.SourceHeight != checked((int)extent.Height))
                {
                    throw new ArgumentException(
                        "The C5 render-target layout must be valid and match the scene extent.",
                        nameof(nearFieldResidualLayout));
                }

                _nearFieldResidualResolutionScale =
                    nearFieldResidualLayout.TraceResolutionScale;

                var traceExtent = new Extent2D
                {
                    Width = checked((uint)nearFieldResidualLayout.TraceWidth),
                    Height = checked((uint)nearFieldResidualLayout.TraceHeight)
                };
                NearFieldDirectSource = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldDirectSource,
                    "Near-Field Direct Diffuse and Emissive",
                    ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    extent,
                    ColorSampledDescriptor);
                NearFieldReceiverPayload = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldReceiverPayload,
                    "Near-Field Compact Receiver Payload",
                    ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat,
                    extent,
                    ColorSampledDescriptor);

                NearFieldResidualRaw = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldResidualRaw,
                    "Near-Field Raw Signed Residual",
                    NearFieldResidualRadianceFormat,
                    traceExtent,
                    StorageSampledDescriptor);
                CreateNearFieldHistoryTargets(traceExtent);
                if (nearFieldResidualLayout.FilterScratchBytes != 0UL)
                {
                    NearFieldResidualFilterScratch0 = CreateGraphOwnedRenderTarget(
                        RenderGraphResourceId.NearFieldResidualFilterScratch,
                        "Near-Field Filter Scratch 0",
                        NearFieldResidualRadianceFormat,
                        traceExtent,
                        StorageSampledDescriptor);
                    NearFieldResidualFilterScratch1 = CreateGraphOwnedRenderTarget(
                        RenderGraphResourceId.NearFieldResidualFilterScratch,
                        "Near-Field Filter Scratch 1",
                        NearFieldResidualRadianceFormat,
                        traceExtent,
                        StorageSampledDescriptor);
                }
            }
            if (giCausticEnabled)
            {
                if (!giCausticScreenLayout.IsValid ||
                    giCausticScreenLayout.Width != checked((int)extent.Width) ||
                    giCausticScreenLayout.Height != checked((int)extent.Height))
                {
                    throw new ArgumentException(
                        "The C4 screen layout must be valid and match the scene extent.",
                        nameof(giCausticScreenLayout));
                }

                GiCausticReceiverPayload = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.GiCausticReceiverPayload,
                    "C4 Visible Receiver Payload",
                    GiCausticReceiverPayloadFormat,
                    extent,
                    ColorSampledDescriptor);
                GiCausticRadiance = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.GiCausticRadiance,
                    "C4 Tagged Caustic Radiance",
                    GiCausticRadianceFormat,
                    extent,
                    StorageSampledDescriptor);
                GiCausticMoments = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.GiCausticMoments,
                    "C4 Resolve Confidence and Moments",
                    GiCausticMomentsFormat,
                    extent,
                    StorageSampledDescriptor);
            }
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
        public RenderTarget MaterialTransportProvenance { get; }
        public RenderTarget? NearFieldDirectSource { get; private set; }
        public RenderTarget? NearFieldReceiverPayload { get; private set; }
        public RenderTarget? NearFieldResidualRaw { get; private set; }
        public RenderTarget? NearFieldResidualHistory0 { get; private set; }
        public RenderTarget? NearFieldResidualHistory1 { get; private set; }
        public RenderTarget? NearFieldResidualMoments0 { get; private set; }
        public RenderTarget? NearFieldResidualMoments1 { get; private set; }
        public RenderTarget? NearFieldResidualValidity0 { get; private set; }
        public RenderTarget? NearFieldResidualValidity1 { get; private set; }
        public RenderTarget? NearFieldResidualHistoryNormals0 { get; private set; }
        public RenderTarget? NearFieldResidualHistoryNormals1 { get; private set; }
        public RenderTarget? NearFieldResidualFilterScratch0 { get; private set; }
        public RenderTarget? NearFieldResidualFilterScratch1 { get; private set; }
        public RenderTarget? GiCausticReceiverPayload { get; private set; }
        public RenderTarget? GiCausticRadiance { get; private set; }
        public RenderTarget? GiCausticMoments { get; private set; }
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
        public int RenderTargetCount => 15 + _bloomMipChain.Count +
            (NearFieldDirectSource is null ? 0 :
                11 + (NearFieldResidualFilterScratch0 is null ? 0 : 2)) +
            (GiCausticReceiverPayload is null ? 0 : 3);
        public ulong TotalEstimatedBytes =>
            SceneColor.EstimatedByteSize +
            SceneDepth.EstimatedByteSize +
            SumEnabledBytes(FoggedSceneColor) +
            AmbientOcclusionRenderTargetBytes +
            MaterialTransportProvenanceRenderTargetBytes +
            GiCausticRenderTargetBytes +
            NearFieldResidualRenderTargetBytes +
            AntiAliasingRenderTargetBytes +
            WeightedOitRenderTargetBytes +
            BloomRenderTargetBytes;
        public ulong AmbientOcclusionRenderTargetBytes => SumEnabledBytes(AmbientOcclusionRaw, AmbientOcclusionBlurred, AmbientOcclusionScratch);
        public ulong MaterialTransportProvenanceRenderTargetBytes =>
            SumEnabledBytes(MaterialTransportProvenance);
        public ulong NearFieldResidualSourceRenderTargetBytes => SumEnabledBytes(
            NearFieldDirectSource,
            NearFieldReceiverPayload);
        public ulong NearFieldResidualRenderTargetBytes =>
            NearFieldResidualSourceRenderTargetBytes + SumEnabledBytes(
                NearFieldResidualRaw,
                NearFieldResidualHistory0,
                NearFieldResidualHistory1,
                NearFieldResidualMoments0,
                NearFieldResidualMoments1,
                NearFieldResidualValidity0,
                NearFieldResidualValidity1,
                NearFieldResidualHistoryNormals0,
                NearFieldResidualHistoryNormals1,
                NearFieldResidualFilterScratch0,
                NearFieldResidualFilterScratch1);
        public ulong GiCausticRenderTargetBytes => SumEnabledBytes(
            GiCausticReceiverPayload,
            GiCausticRadiance,
            GiCausticMoments);
        public ulong AntiAliasingRenderTargetBytes => SumEnabledBytes(LdrSceneColor, SmaaEdges, SmaaBlendWeights, MotionVectors, TaaHistoryA, TaaHistoryB);
        public ulong WeightedOitRenderTargetBytes => SumEnabledBytes(WeightedOitAccumulation, WeightedOitRevealage);
        public ulong BloomRenderTargetBytes => SumTargetBytes(_bloomMipChain);

        private readonly List<RenderTarget> _bloomMipChain = new();

        private static Extent2D PlaceholderExtent => new() { Width = 1, Height = 1 };

        public void Recreate(
            Extent2D extent,
            Extent2D outputExtent,
            float ambientOcclusionResolutionScale = 0.5f,
            int bloomMipCount = 6,
            bool ambientOcclusionEnabled = true,
            AntiAliasingMode antiAliasingMode = AntiAliasingMode.SmaaMedium,
            bool motionVectorsEnabled = false,
            bool fogEnabled = true,
            bool weightedOitEnabled = false,
            bool materialTransportProvenanceEnabled = false)
        {
            ulong before = TotalEstimatedBytes;
            RecreateIfDifferent(SceneColor, extent);
            RecreateIfDifferent(SceneDepth, extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.FogOutput, FoggedSceneColor, CalculateFoggedSceneColorExtent(extent, fogEnabled));
            RecreateAmbientOcclusionTargets(extent, ambientOcclusionResolutionScale, ambientOcclusionEnabled);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.MaterialTransportProvenance,
                MaterialTransportProvenance,
                materialTransportProvenanceEnabled ? extent : PlaceholderExtent);
            RecreateGiCausticTargets(extent);
            RecreateNearFieldResidualSourceTargets(extent);
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

        private void RecreateNearFieldResidualSourceTargets(Extent2D extent)
        {
            if (NearFieldDirectSource is null)
                return;

            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldDirectSource,
                NearFieldDirectSource, extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldReceiverPayload,
                NearFieldReceiverPayload!, extent);

            Extent2D traceExtent = CalculateNearFieldTraceExtent(extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualRaw,
                NearFieldResidualRaw!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualHistory,
                NearFieldResidualHistory0!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualHistory,
                NearFieldResidualHistory1!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualMoments,
                NearFieldResidualMoments0!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualMoments,
                NearFieldResidualMoments1!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualValidity,
                NearFieldResidualValidity0!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualValidity,
                NearFieldResidualValidity1!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualHistoryNormals,
                NearFieldResidualHistoryNormals0!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualHistoryNormals,
                NearFieldResidualHistoryNormals1!, traceExtent);
            if (NearFieldResidualFilterScratch0 is not null)
            {
                RecreateGraphOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    NearFieldResidualFilterScratch0,
                    traceExtent);
                RecreateGraphOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    NearFieldResidualFilterScratch1!,
                    traceExtent);
            }
        }

        private void RecreateGiCausticTargets(Extent2D extent)
        {
            if (GiCausticReceiverPayload is null)
                return;

            RecreateGraphOwnedTarget(
                RenderGraphResourceId.GiCausticReceiverPayload,
                GiCausticReceiverPayload,
                extent);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.GiCausticRadiance,
                GiCausticRadiance!,
                extent);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.GiCausticMoments,
                GiCausticMoments!,
                extent);
        }

        internal void ReleaseGiCausticTargetsAfterDeviceIdle()
        {
            if (GiCausticReceiverPayload is not null)
            {
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.GiCausticReceiverPayload,
                    GiCausticReceiverPayload);
            }
            if (GiCausticRadiance is not null)
            {
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.GiCausticRadiance,
                    GiCausticRadiance);
            }
            if (GiCausticMoments is not null)
            {
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.GiCausticMoments,
                    GiCausticMoments);
            }

            GiCausticReceiverPayload = null;
            GiCausticRadiance = null;
            GiCausticMoments = null;
        }

        /// <summary>
        /// Releases every C5-owned image after the caller has established a
        /// device-idle transition. The static graph declaration may remain,
        /// but skipped C5 passes then resolve no physical image and consume
        /// exactly zero C5 image allocation.
        /// </summary>
        internal void ReleaseNearFieldResidualTargetsAfterDeviceIdle()
        {
            if (NearFieldDirectSource is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldDirectSource,
                    NearFieldDirectSource);
            if (NearFieldReceiverPayload is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldReceiverPayload,
                    NearFieldReceiverPayload);
            if (NearFieldResidualRaw is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualRaw,
                    NearFieldResidualRaw);
            if (NearFieldResidualHistory0 is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualHistory,
                    NearFieldResidualHistory0);
            if (NearFieldResidualHistory1 is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualHistory,
                    NearFieldResidualHistory1);
            if (NearFieldResidualMoments0 is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualMoments,
                    NearFieldResidualMoments0);
            if (NearFieldResidualMoments1 is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualMoments,
                    NearFieldResidualMoments1);
            if (NearFieldResidualValidity0 is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualValidity,
                    NearFieldResidualValidity0);
            if (NearFieldResidualValidity1 is not null)
                ReleaseOrDisposeOwnedTarget(RenderGraphResourceId.NearFieldResidualValidity,
                    NearFieldResidualValidity1);
            if (NearFieldResidualHistoryNormals0 is not null)
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualHistoryNormals,
                    NearFieldResidualHistoryNormals0);
            if (NearFieldResidualHistoryNormals1 is not null)
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualHistoryNormals,
                    NearFieldResidualHistoryNormals1);
            if (NearFieldResidualFilterScratch0 is not null)
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    NearFieldResidualFilterScratch0);
            if (NearFieldResidualFilterScratch1 is not null)
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    NearFieldResidualFilterScratch1);

            NearFieldDirectSource = null;
            NearFieldReceiverPayload = null;
            NearFieldResidualRaw = null;
            NearFieldResidualHistory0 = null;
            NearFieldResidualHistory1 = null;
            NearFieldResidualMoments0 = null;
            NearFieldResidualMoments1 = null;
            NearFieldResidualValidity0 = null;
            NearFieldResidualValidity1 = null;
            NearFieldResidualHistoryNormals0 = null;
            NearFieldResidualHistoryNormals1 = null;
            NearFieldResidualFilterScratch0 = null;
            NearFieldResidualFilterScratch1 = null;
        }

        private void CreateNearFieldHistoryTargets(Extent2D traceExtent)
        {
            NearFieldResidualHistory0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistory,
                "Near-Field Residual History 0",
                NearFieldResidualRadianceFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualHistory1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistory,
                "Near-Field Residual History 1",
                NearFieldResidualRadianceFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualMoments0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualMoments,
                "Near-Field Residual Moments 0",
                NearFieldResidualMomentsFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualMoments1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualMoments,
                "Near-Field Residual Moments 1",
                NearFieldResidualMomentsFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualValidity0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualValidity,
                "Near-Field Residual Validity 0",
                NearFieldResidualValidityFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualValidity1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualValidity,
                "Near-Field Residual Validity 1",
                NearFieldResidualValidityFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualHistoryNormals0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                "Near-Field Residual Normal History 0",
                NearFieldResidualNormalsFormat,
                traceExtent,
                StorageSampledDescriptor);
            NearFieldResidualHistoryNormals1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                "Near-Field Residual Normal History 1",
                NearFieldResidualNormalsFormat,
                traceExtent,
                StorageSampledDescriptor);
        }

        private Extent2D CalculateNearFieldTraceExtent(Extent2D extent) => new()
        {
            Width = Math.Max(1u, (uint)MathF.Ceiling(extent.Width *
                _nearFieldResidualResolutionScale)),
            Height = Math.Max(1u, (uint)MathF.Ceiling(extent.Height *
                _nearFieldResidualResolutionScale))
        };

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
            DisposeIfManagerOwned(
                RenderGraphResourceId.MaterialTransportProvenance,
                MaterialTransportProvenance);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldDirectSource,
                NearFieldDirectSource);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldReceiverPayload,
                NearFieldReceiverPayload);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualRaw,
                NearFieldResidualRaw);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualHistory,
                NearFieldResidualHistory0);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualHistory,
                NearFieldResidualHistory1);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualMoments,
                NearFieldResidualMoments0);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualMoments,
                NearFieldResidualMoments1);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualValidity,
                NearFieldResidualValidity0);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualValidity,
                NearFieldResidualValidity1);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualHistoryNormals,
                NearFieldResidualHistoryNormals0);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualHistoryNormals,
                NearFieldResidualHistoryNormals1);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualFilterScratch,
                NearFieldResidualFilterScratch0);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualFilterScratch,
                NearFieldResidualFilterScratch1);
            DisposeIfManagerOwned(RenderGraphResourceId.GiCausticReceiverPayload,
                GiCausticReceiverPayload);
            DisposeIfManagerOwned(RenderGraphResourceId.GiCausticRadiance,
                GiCausticRadiance);
            DisposeIfManagerOwned(RenderGraphResourceId.GiCausticMoments,
                GiCausticMoments);
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
