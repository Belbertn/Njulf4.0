using System;
using System.Collections.Generic;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Complete graph-owned image bank for one C5 layout generation. Shared
    /// scene colour/depth/motion and Hi-Z remain renderer-generation inputs;
    /// every C5-owned attachment is contained here and can overlap one prior
    /// bank during a fence-backed hot swap.
    /// </summary>
    public sealed record SimpleDdgiNearFieldResidualRenderTargetGeneration(
        SimpleDdgiNearFieldResidualLayout Layout,
        RenderTarget DirectSource,
        RenderTarget ReceiverPayload,
        RenderTarget? TraceRasterDepth,
        RenderTarget RawResidual,
        RenderTarget PreparedDepthFootprint,
        RenderTarget PreparedReceiverPayload,
        RenderTarget PreparedMotion,
        RenderTarget SourceLuminance,
        RenderTarget History0,
        RenderTarget History1,
        RenderTarget Moments0,
        RenderTarget Moments1,
        RenderTarget Validity0,
        RenderTarget Validity1,
        RenderTarget HistoryNormals0,
        RenderTarget HistoryNormals1,
        RenderTarget? FilterScratch0,
        RenderTarget? FilterScratch1)
    {
        public ulong EstimatedByteSize => checked(
            DirectSource.EstimatedByteSize +
            ReceiverPayload.EstimatedByteSize +
            (TraceRasterDepth?.EstimatedByteSize ?? 0UL) +
            RawResidual.EstimatedByteSize +
            PreparedDepthFootprint.EstimatedByteSize +
            PreparedReceiverPayload.EstimatedByteSize +
            PreparedMotion.EstimatedByteSize +
            SourceLuminance.EstimatedByteSize +
            History0.EstimatedByteSize + History1.EstimatedByteSize +
            Moments0.EstimatedByteSize + Moments1.EstimatedByteSize +
            Validity0.EstimatedByteSize + Validity1.EstimatedByteSize +
            HistoryNormals0.EstimatedByteSize +
            HistoryNormals1.EstimatedByteSize +
            (FilterScratch0?.EstimatedByteSize ?? 0UL) +
            (FilterScratch1?.EstimatedByteSize ?? 0UL));
    }

    public sealed class RenderTargetManager : IDisposable
    {
        public const Format SceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format FoggedSceneColorFormat = SceneColorFormat;
        public const Format AmbientOcclusionFormat = Format.R8Unorm;
        public const Format GtaoRadianceFormat = Format.R16G16B16A16Sfloat;
        public const Format GtaoGeometryHistoryFormat = Format.R32G32Uint;
        public const Format MaterialTransportProvenanceFormat = Format.R8Unorm;
        public const Format LdrSceneColorFormat = Format.R16G16B16A16Sfloat;
        public const Format SmaaEdgesFormat = Format.R8G8Unorm;
        public const Format SmaaBlendWeightsFormat = Format.R8G8B8A8Unorm;
        public const Format MotionVectorFormat = Format.R16G16Sfloat;
        public const Format VariableRateShadingFormat = Format.R8Uint;
        public const Format WeightedOitAccumulationFormat = Format.R16G16B16A16Sfloat;
        public const Format WeightedOitRevealageFormat = Format.R8Unorm;
        public const Format NearFieldResidualRadianceFormat = Format.R16G16B16A16Sfloat;
        public const Format NearFieldResidualMomentsFormat = Format.R16G16Sfloat;
        public const Format NearFieldResidualValidityFormat = Format.R32Uint;
        public const Format NearFieldResidualNormalsFormat = Format.R16G16B16A16Sfloat;
        public const Format NearFieldPreparedDepthFootprintFormat = Format.R32G32Sfloat;
        public const Format NearFieldPreparedPayloadFormat = Format.R32G32B32A32Uint;
        public const Format NearFieldPreparedMotionFormat = Format.R16G16Sfloat;
        public const Format NearFieldSourceLuminanceFormat = Format.R16Sfloat;
        public const Format GiCausticReceiverPayloadFormat =
            GiCausticScreenGpuAbi.ReceiverPayloadFormat;
        public const Format GiCausticRadianceFormat =
            GiCausticScreenGpuAbi.RadianceFormat;
        public const Format GiCausticMomentsFormat =
            GiCausticScreenGpuAbi.MomentsFormat;
        public const Format HybridReflectionReceiverPayloadFormat =
            Format.R32G32B32A32Uint;
        public const Format HybridReflectionRadianceFormat =
            Format.R16G16B16A16Sfloat;
        public const Format HybridReflectionMomentsFormat = Format.R16G16Sfloat;
        public const Format HybridReflectionRawMetadataFormat =
            Format.R32G32Uint;
        public const Format HybridReflectionHistoryMetadataFormat =
            Format.R32G32B32A32Uint;

        private readonly VulkanContext _context;
        private readonly RenderGraph? _renderGraph;
        private float _nearFieldResidualResolutionScale =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference
                .ResolutionScale;
        private SimpleDdgiNearFieldResidualRenderTargetGeneration?
            _nearFieldResidualGeneration;
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

        // C5 resets transient and current-history images with transfer clears
        // before compacted indirect work begins. Keep that capability local to
        // the C5 bank rather than widening every storage target in the renderer.
        private static readonly RenderTargetDescriptor
            NearFieldStorageSampledDescriptor = new(
                colorAttachment: false,
                sampled: true,
                storage: true,
                transferDestination: true);

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
            GiCausticScreenResolveLayout giCausticScreenLayout = default,
            bool hybridReflectionsEnabled = false,
            AmbientOcclusionMode ambientOcclusionMode =
                AmbientOcclusionMode.Ssao)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _renderGraph = renderGraph;
            SceneColor = new RenderTarget(
                _context,
                "HDR Scene Color",
                SceneColorFormat,
                extent,
                HdrSceneColorStorageDescriptor);
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
                ambientOcclusionEnabled ? extent : PlaceholderExtent,
                AmbientOcclusionBlurredDescriptor);
            AmbientOcclusionScratch = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.AmbientOcclusionScratch,
                "Ambient Occlusion Scratch",
                AmbientOcclusionFormat,
                ambientOcclusionExtent,
                StorageSampledDescriptor);
            bool gtaoEnabled = ambientOcclusionEnabled &&
                ambientOcclusionMode == AmbientOcclusionMode.Gtao;
            Extent2D gtaoWorkingExtent = gtaoEnabled
                ? ambientOcclusionExtent
                : PlaceholderExtent;
            Extent2D gtaoResolvedExtent = gtaoEnabled
                ? extent
                : PlaceholderExtent;
            GtaoRaw = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoRaw,
                "GTAO Raw Bent Normal and Visibility",
                GtaoRadianceFormat,
                gtaoWorkingExtent,
                StorageSampledDescriptor);
            GtaoSpatialScratch = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoSpatialScratch,
                "GTAO Spatial Debug Scratch",
                GtaoRadianceFormat,
                gtaoResolvedExtent,
                StorageSampledDescriptor);
            GtaoHistory0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoHistory,
                "GTAO History A",
                GtaoRadianceFormat,
                gtaoWorkingExtent,
                StorageSampledDescriptor);
            GtaoHistory1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoHistory,
                "GTAO History B",
                GtaoRadianceFormat,
                gtaoWorkingExtent,
                StorageSampledDescriptor);
            GtaoGeometryHistory0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoGeometryHistory,
                "GTAO Geometry History A",
                GtaoGeometryHistoryFormat,
                gtaoWorkingExtent,
                StorageSampledDescriptor);
            GtaoGeometryHistory1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoGeometryHistory,
                "GTAO Geometry History B",
                GtaoGeometryHistoryFormat,
                gtaoWorkingExtent,
                StorageSampledDescriptor);
            GtaoFiltered = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.GtaoFiltered,
                "GTAO Filtered Bent Normal and Visibility",
                GtaoRadianceFormat,
                gtaoResolvedExtent,
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
                bool traceResolutionSource =
                    nearFieldResidualLayout.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
                Extent2D sourceAttachmentExtent = traceResolutionSource
                    ? traceExtent
                    : extent;
                NearFieldDirectSource = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldDirectSource,
                    "Near-Field Direct Diffuse and Emissive",
                    ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    sourceAttachmentExtent,
                    ColorSampledDescriptor);
                NearFieldReceiverPayload = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldReceiverPayload,
                    "Near-Field Compact Receiver Payload",
                    ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat,
                    sourceAttachmentExtent,
                    ColorSampledDescriptor);
                NearFieldTraceRasterDepth = traceResolutionSource
                    ? CreateGraphOwnedRenderTarget(
                        RenderGraphResourceId.NearFieldTraceRasterDepth,
                        "Near-Field Trace-Resolution Source Depth",
                        depthFormat,
                        traceExtent,
                        SceneDepthDescriptor)
                    : null;

                NearFieldResidualRaw = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldResidualRaw,
                    "Near-Field Raw Signed Residual",
                    NearFieldResidualRadianceFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                NearFieldPreparedDepthFootprint = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                    "Near-Field Prepared Linear Depth and B3 Footprint",
                    NearFieldPreparedDepthFootprintFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                NearFieldPreparedReceiverPayload = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                    "Near-Field Prepared Receiver Payload",
                    NearFieldPreparedPayloadFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                NearFieldPreparedMotion = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldPreparedMotion,
                    "Near-Field Prepared Motion",
                    NearFieldPreparedMotionFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                NearFieldSourceLuminance = CreateGraphOwnedRenderTarget(
                    RenderGraphResourceId.NearFieldSourceLuminance,
                    "Near-Field Source Luminance",
                    NearFieldSourceLuminanceFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                CreateNearFieldHistoryTargets(traceExtent);
                if (nearFieldResidualLayout.FilterScratchBytes != 0UL)
                {
                    NearFieldResidualFilterScratch0 = CreateGraphOwnedRenderTarget(
                        RenderGraphResourceId.NearFieldResidualFilterScratch,
                        "Near-Field Filter Scratch 0",
                        NearFieldResidualRadianceFormat,
                        traceExtent,
                        NearFieldStorageSampledDescriptor);
                    NearFieldResidualFilterScratch1 = CreateGraphOwnedRenderTarget(
                        RenderGraphResourceId.NearFieldResidualFilterScratch,
                        "Near-Field Filter Scratch 1",
                        NearFieldResidualRadianceFormat,
                        traceExtent,
                        NearFieldStorageSampledDescriptor);
                }
                _nearFieldResidualGeneration =
                    CapturePublishedNearFieldResidualGeneration(
                        nearFieldResidualLayout);
                PublishNearFieldResidualGraphBindings(
                    _nearFieldResidualGeneration);
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
            CreateHybridReflectionTargets(
                hybridReflectionsEnabled ? extent : PlaceholderExtent);
            LdrSceneColor = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.LdrSceneColor,
                "LDR Scene Color",
                LdrSceneColorFormat,
                RequiresAntiAliasingTarget(antiAliasingMode)
                    ? CalculateAntiAliasingExtent(extent, antiAliasingMode)
                    : PlaceholderExtent,
                LdrSceneColorDescriptor);
            SmaaEdges = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.SmaaEdges,
                "SMAA Edges",
                SmaaEdgesFormat,
                AntiAliasingSettings.IsSmaaMode(antiAliasingMode)
                    ? CalculateAntiAliasingExtent(extent, antiAliasingMode)
                    : PlaceholderExtent,
                ColorSampledDescriptor);
            SmaaBlendWeights = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.SmaaBlendWeights,
                "SMAA Blend Weights",
                SmaaBlendWeightsFormat,
                AntiAliasingSettings.IsSmaaMode(antiAliasingMode)
                    ? CalculateAntiAliasingExtent(extent, antiAliasingMode)
                    : PlaceholderExtent,
                ColorSampledDescriptor);
            MotionVectors = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.MotionVectors,
                "Motion Vectors",
                MotionVectorFormat,
                motionVectorsEnabled ? extent : PlaceholderExtent,
                ColorSampledDescriptor);
            VariableRateShading = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.VariableRateShading,
                "Conservative Fragment Shading Rate",
                VariableRateShadingFormat,
                _context.FragmentShadingRateSupported
                    ? CalculateVariableRateShadingExtent(
                        extent,
                        _context.FragmentShadingRateAttachmentTexelSize)
                    : PlaceholderExtent,
                new RenderTargetDescriptor(
                    colorAttachment: false,
                    sampled: false,
                    storage: true,
                    fragmentShadingRateAttachment:
                        _context.FragmentShadingRateSupported));
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
        public RenderTarget GtaoRaw { get; }
        public RenderTarget GtaoSpatialScratch { get; }
        public RenderTarget GtaoHistory0 { get; }
        public RenderTarget GtaoHistory1 { get; }
        public RenderTarget GtaoGeometryHistory0 { get; }
        public RenderTarget GtaoGeometryHistory1 { get; }
        public RenderTarget GtaoFiltered { get; }
        public RenderTarget MaterialTransportProvenance { get; }
        public RenderTarget? NearFieldDirectSource { get; private set; }
        public RenderTarget? NearFieldReceiverPayload { get; private set; }
        public RenderTarget? NearFieldTraceRasterDepth { get; private set; }
        public RenderTarget? NearFieldResidualRaw { get; private set; }
        public RenderTarget? NearFieldPreparedDepthFootprint { get; private set; }
        public RenderTarget? NearFieldPreparedReceiverPayload { get; private set; }
        public RenderTarget? NearFieldPreparedMotion { get; private set; }
        public RenderTarget? NearFieldSourceLuminance { get; private set; }
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
        internal SimpleDdgiNearFieldResidualRenderTargetGeneration?
            CurrentNearFieldResidualGeneration =>
                _nearFieldResidualGeneration;
        public RenderTarget? GiCausticReceiverPayload { get; private set; }
        public RenderTarget? GiCausticRadiance { get; private set; }
        public RenderTarget? GiCausticMoments { get; private set; }
        public RenderTarget? HybridReflectionReceiverPayload { get; private set; }
        public RenderTarget? HybridReflectionRawRadiance { get; private set; }
        public RenderTarget? HybridReflectionRawMetadata { get; private set; }
        public RenderTarget? HybridReflectionHistory0 { get; private set; }
        public RenderTarget? HybridReflectionHistory1 { get; private set; }
        public RenderTarget? HybridReflectionMoments0 { get; private set; }
        public RenderTarget? HybridReflectionMoments1 { get; private set; }
        public RenderTarget? HybridReflectionHistoryMetadata0 { get; private set; }
        public RenderTarget? HybridReflectionHistoryMetadata1 { get; private set; }
        public RenderTarget? HybridReflectionFilterScratch { get; private set; }
        public RenderTarget? HybridReflectionDdgiCohorts { get; private set; }
        public RenderTarget LdrSceneColor { get; }
        public RenderTarget SmaaEdges { get; }
        public RenderTarget SmaaBlendWeights { get; }
        public RenderTarget MotionVectors { get; }
        public RenderTarget VariableRateShading { get; }
        public RenderTarget TaaHistoryA { get; }
        public RenderTarget TaaHistoryB { get; }
        public RenderTarget WeightedOitAccumulation { get; }
        public RenderTarget WeightedOitRevealage { get; }
        public IReadOnlyList<RenderTarget> BloomMipChain => _bloomMipChain;
        public int BloomMipCount => _bloomMipChain.Count;
        public Extent2D BloomBaseExtent => _bloomMipChain.Count == 0 ? default : _bloomMipChain[0].Extent;
        public int ResizeCount { get; private set; }
        public int RenderTargetCount => 23 + _bloomMipChain.Count +
            (NearFieldDirectSource is null ? 0 :
                14 + (NearFieldSourceLuminance is null ? 0 : 1) +
                (NearFieldTraceRasterDepth is null ? 0 : 1) +
                (NearFieldResidualFilterScratch0 is null ? 0 : 2)) +
            (GiCausticReceiverPayload is null ? 0 : 3) +
            (HybridReflectionReceiverPayload is null ? 0 : 11);
        public ulong TotalEstimatedBytes =>
            SceneColor.EstimatedByteSize +
            SceneDepth.EstimatedByteSize +
            SumEnabledBytes(FoggedSceneColor) +
            AmbientOcclusionRenderTargetBytes +
            MaterialTransportProvenanceRenderTargetBytes +
            GiCausticRenderTargetBytes +
            HybridReflectionRenderTargetBytes +
            NearFieldResidualRenderTargetBytes +
            VariableRateShadingRenderTargetBytes +
            AntiAliasingRenderTargetBytes +
            WeightedOitRenderTargetBytes +
            BloomRenderTargetBytes;
        public ulong AmbientOcclusionRenderTargetBytes => SumEnabledBytes(
            AmbientOcclusionRaw,
            AmbientOcclusionBlurred,
            AmbientOcclusionScratch,
            GtaoRaw,
            GtaoSpatialScratch,
            GtaoHistory0,
            GtaoHistory1,
            GtaoGeometryHistory0,
            GtaoGeometryHistory1,
            GtaoFiltered);
        public ulong MaterialTransportProvenanceRenderTargetBytes =>
            SumEnabledBytes(MaterialTransportProvenance);
        public ulong NearFieldResidualSourceRenderTargetBytes => SumEnabledBytes(
            NearFieldDirectSource,
            NearFieldReceiverPayload,
            NearFieldTraceRasterDepth);
        public ulong NearFieldResidualRenderTargetBytes =>
            NearFieldResidualSourceRenderTargetBytes + SumEnabledBytes(
                NearFieldResidualRaw,
                NearFieldPreparedDepthFootprint,
                NearFieldPreparedReceiverPayload,
                NearFieldPreparedMotion,
                NearFieldSourceLuminance,
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

        internal SimpleDdgiNearFieldResidualRenderTargetGeneration
            AllocateNearFieldResidualGeneration(
                ulong generation,
                in SimpleDdgiNearFieldResidualLayout layout)
        {
            if (generation == 0UL || !layout.IsValid ||
                layout.SourceFormat !=
                    SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat)
            {
                throw new ArgumentException(
                    "C5 generation allocation requires a valid V13 layout.",
                    nameof(layout));
            }

            var fullExtent = new Extent2D
            {
                Width = checked((uint)layout.SourceWidth),
                Height = checked((uint)layout.SourceHeight)
            };
            var traceExtent = new Extent2D
            {
                Width = checked((uint)layout.TraceWidth),
                Height = checked((uint)layout.TraceHeight)
            };
            bool traceResolutionSource = layout.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
            Extent2D sourceExtent = traceResolutionSource
                ? traceExtent
                : fullExtent;
            var created = new List<(RenderGraphResourceId Id,
                RenderTarget Target)>(18);
            RenderTarget Create(
                RenderGraphResourceId id,
                string name,
                Format format,
                Extent2D extent,
                RenderTargetDescriptor descriptor)
            {
                RenderTarget target = CreateGraphOwnedRenderTarget(
                    id,
                    $"{name} G{generation}",
                    format,
                    extent,
                    descriptor);
                created.Add((id, target));
                return target;
            }

            try
            {
                RenderTarget source = Create(
                    RenderGraphResourceId.NearFieldDirectSource,
                    "Near-Field Direct Diffuse and Emissive",
                    ForwardNearFieldDirectSourceContract
                        .RequiredAttachmentFormat,
                    sourceExtent,
                    ColorSampledDescriptor);
                RenderTarget receiver = Create(
                    RenderGraphResourceId.NearFieldReceiverPayload,
                    "Near-Field Compact Receiver Payload",
                    ForwardNearFieldDirectSourceContract
                        .ReceiverPayloadFormat,
                    sourceExtent,
                    ColorSampledDescriptor);
                RenderTarget? sourceDepth = traceResolutionSource
                    ? Create(
                        RenderGraphResourceId.NearFieldTraceRasterDepth,
                        "Near-Field Trace-Resolution Source Depth",
                        SceneDepth.Format,
                        traceExtent,
                        SceneDepthDescriptor)
                    : null;
                RenderTarget raw = Create(
                    RenderGraphResourceId.NearFieldResidualRaw,
                    "Near-Field Raw Signed Residual",
                    NearFieldResidualRadianceFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget preparedDepth = Create(
                    RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                    "Near-Field Prepared Linear Depth and B3 Footprint",
                    NearFieldPreparedDepthFootprintFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget preparedPayload = Create(
                    RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                    "Near-Field Prepared Receiver Payload",
                    NearFieldPreparedPayloadFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget preparedMotion = Create(
                    RenderGraphResourceId.NearFieldPreparedMotion,
                    "Near-Field Prepared Motion",
                    NearFieldPreparedMotionFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget luminance = Create(
                    RenderGraphResourceId.NearFieldSourceLuminance,
                    "Near-Field Source Luminance",
                    NearFieldSourceLuminanceFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget history0 = Create(
                    RenderGraphResourceId.NearFieldResidualHistory,
                    "Near-Field Residual History 0",
                    NearFieldResidualRadianceFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget history1 = Create(
                    RenderGraphResourceId.NearFieldResidualHistory,
                    "Near-Field Residual History 1",
                    NearFieldResidualRadianceFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget moments0 = Create(
                    RenderGraphResourceId.NearFieldResidualMoments,
                    "Near-Field Residual Moments 0",
                    NearFieldResidualMomentsFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget moments1 = Create(
                    RenderGraphResourceId.NearFieldResidualMoments,
                    "Near-Field Residual Moments 1",
                    NearFieldResidualMomentsFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget validity0 = Create(
                    RenderGraphResourceId.NearFieldResidualValidity,
                    "Near-Field Residual Validity 0",
                    NearFieldResidualValidityFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget validity1 = Create(
                    RenderGraphResourceId.NearFieldResidualValidity,
                    "Near-Field Residual Validity 1",
                    NearFieldResidualValidityFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget normals0 = Create(
                    RenderGraphResourceId.NearFieldResidualHistoryNormals,
                    "Near-Field Residual Normal History 0",
                    NearFieldResidualNormalsFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget normals1 = Create(
                    RenderGraphResourceId.NearFieldResidualHistoryNormals,
                    "Near-Field Residual Normal History 1",
                    NearFieldResidualNormalsFormat,
                    traceExtent,
                    NearFieldStorageSampledDescriptor);
                RenderTarget? scratch0 = null;
                RenderTarget? scratch1 = null;
                if (layout.FilterScratchBytes != 0UL)
                {
                    scratch0 = Create(
                        RenderGraphResourceId.NearFieldResidualFilterScratch,
                        "Near-Field Filter Scratch 0",
                        NearFieldResidualRadianceFormat,
                        traceExtent,
                        NearFieldStorageSampledDescriptor);
                    scratch1 = Create(
                        RenderGraphResourceId.NearFieldResidualFilterScratch,
                        "Near-Field Filter Scratch 1",
                        NearFieldResidualRadianceFormat,
                        traceExtent,
                        NearFieldStorageSampledDescriptor);
                }
                return new SimpleDdgiNearFieldResidualRenderTargetGeneration(
                    layout, source, receiver, sourceDepth, raw, preparedDepth,
                    preparedPayload, preparedMotion, luminance, history0,
                    history1, moments0, moments1, validity0, validity1,
                    normals0, normals1, scratch0, scratch1);
            }
            catch
            {
                for (int index = created.Count - 1; index >= 0; index--)
                {
                    ReleaseOrDisposeOwnedTarget(
                        created[index].Id,
                        created[index].Target);
                }
                throw;
            }
        }

        internal void PublishNearFieldResidualGeneration(
            SimpleDdgiNearFieldResidualRenderTargetGeneration generation)
        {
            ArgumentNullException.ThrowIfNull(generation);
            _nearFieldResidualGeneration = generation;
            _nearFieldResidualResolutionScale =
                generation.Layout.TraceResolutionScale;
            NearFieldDirectSource = generation.DirectSource;
            NearFieldReceiverPayload = generation.ReceiverPayload;
            NearFieldTraceRasterDepth = generation.TraceRasterDepth;
            NearFieldResidualRaw = generation.RawResidual;
            NearFieldPreparedDepthFootprint =
                generation.PreparedDepthFootprint;
            NearFieldPreparedReceiverPayload =
                generation.PreparedReceiverPayload;
            NearFieldPreparedMotion = generation.PreparedMotion;
            NearFieldSourceLuminance = generation.SourceLuminance;
            NearFieldResidualHistory0 = generation.History0;
            NearFieldResidualHistory1 = generation.History1;
            NearFieldResidualMoments0 = generation.Moments0;
            NearFieldResidualMoments1 = generation.Moments1;
            NearFieldResidualValidity0 = generation.Validity0;
            NearFieldResidualValidity1 = generation.Validity1;
            NearFieldResidualHistoryNormals0 = generation.HistoryNormals0;
            NearFieldResidualHistoryNormals1 = generation.HistoryNormals1;
            NearFieldResidualFilterScratch0 = generation.FilterScratch0;
            NearFieldResidualFilterScratch1 = generation.FilterScratch1;
            PublishNearFieldResidualGraphBindings(generation);
        }

        internal void ReleaseNearFieldResidualGeneration(
            SimpleDdgiNearFieldResidualRenderTargetGeneration generation)
        {
            ArgumentNullException.ThrowIfNull(generation);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldDirectSource,
                generation.DirectSource);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldReceiverPayload,
                generation.ReceiverPayload);
            if (generation.TraceRasterDepth is not null)
            {
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldTraceRasterDepth,
                    generation.TraceRasterDepth);
            }
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualRaw,
                generation.RawResidual);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                generation.PreparedDepthFootprint);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                generation.PreparedReceiverPayload);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldPreparedMotion,
                generation.PreparedMotion);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldSourceLuminance,
                generation.SourceLuminance);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualHistory,
                generation.History0);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualHistory,
                generation.History1);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualMoments,
                generation.Moments0);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualMoments,
                generation.Moments1);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualValidity,
                generation.Validity0);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualValidity,
                generation.Validity1);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                generation.HistoryNormals0);
            ReleaseOrDisposeOwnedTarget(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                generation.HistoryNormals1);
            if (generation.FilterScratch0 is not null)
            {
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    generation.FilterScratch0);
            }
            if (generation.FilterScratch1 is not null)
            {
                ReleaseOrDisposeOwnedTarget(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    generation.FilterScratch1);
            }

            if (ReferenceEquals(_nearFieldResidualGeneration, generation))
            {
                ClearPublishedNearFieldResidualGeneration();
            }
        }

        private SimpleDdgiNearFieldResidualRenderTargetGeneration
            CapturePublishedNearFieldResidualGeneration(
                in SimpleDdgiNearFieldResidualLayout layout) => new(
                    layout,
                    NearFieldDirectSource!,
                    NearFieldReceiverPayload!,
                    NearFieldTraceRasterDepth,
                    NearFieldResidualRaw!,
                    NearFieldPreparedDepthFootprint!,
                    NearFieldPreparedReceiverPayload!,
                    NearFieldPreparedMotion!,
                    NearFieldSourceLuminance!,
                    NearFieldResidualHistory0!,
                    NearFieldResidualHistory1!,
                    NearFieldResidualMoments0!,
                    NearFieldResidualMoments1!,
                    NearFieldResidualValidity0!,
                    NearFieldResidualValidity1!,
                    NearFieldResidualHistoryNormals0!,
                    NearFieldResidualHistoryNormals1!,
                    NearFieldResidualFilterScratch0,
                    NearFieldResidualFilterScratch1);

        private void ClearPublishedNearFieldResidualGeneration()
        {
            PublishEmptyNearFieldResidualGraphBindings();
            _nearFieldResidualGeneration = null;
            NearFieldDirectSource = null;
            NearFieldReceiverPayload = null;
            NearFieldTraceRasterDepth = null;
            NearFieldResidualRaw = null;
            NearFieldPreparedDepthFootprint = null;
            NearFieldPreparedReceiverPayload = null;
            NearFieldPreparedMotion = null;
            NearFieldSourceLuminance = null;
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

        private void PublishNearFieldResidualGraphBindings(
            SimpleDdgiNearFieldResidualRenderTargetGeneration generation)
        {
            if (_renderGraph is null)
                return;

            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldDirectSource,
                [generation.DirectSource]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldReceiverPayload,
                [generation.ReceiverPayload]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldTraceRasterDepth,
                generation.TraceRasterDepth is null
                    ? []
                    : [generation.TraceRasterDepth]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldResidualRaw,
                [generation.RawResidual]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                [generation.PreparedDepthFootprint]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                [generation.PreparedReceiverPayload]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldPreparedMotion,
                [generation.PreparedMotion]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldSourceLuminance,
                [generation.SourceLuminance]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldResidualHistory,
                [generation.History0, generation.History1]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldResidualMoments,
                [generation.Moments0, generation.Moments1]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldResidualValidity,
                [generation.Validity0, generation.Validity1]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                [generation.HistoryNormals0, generation.HistoryNormals1]);
            _renderGraph.PublishOwnedRenderTargets(
                RenderGraphResourceId.NearFieldResidualFilterScratch,
                generation.FilterScratch0 is not null &&
                generation.FilterScratch1 is not null
                    ? [generation.FilterScratch0, generation.FilterScratch1]
                    : []);
        }

        private void PublishEmptyNearFieldResidualGraphBindings()
        {
            if (_renderGraph is null)
                return;

            ReadOnlySpan<RenderGraphResourceId> resources =
            [
                RenderGraphResourceId.NearFieldDirectSource,
                RenderGraphResourceId.NearFieldReceiverPayload,
                RenderGraphResourceId.NearFieldTraceRasterDepth,
                RenderGraphResourceId.NearFieldResidualRaw,
                RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                RenderGraphResourceId.NearFieldPreparedMotion,
                RenderGraphResourceId.NearFieldSourceLuminance,
                RenderGraphResourceId.NearFieldResidualHistory,
                RenderGraphResourceId.NearFieldResidualMoments,
                RenderGraphResourceId.NearFieldResidualValidity,
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                RenderGraphResourceId.NearFieldResidualFilterScratch
            ];
            foreach (RenderGraphResourceId resource in resources)
                _renderGraph.PublishOwnedRenderTargets(resource, []);
        }

        /// <summary>
        /// Selects the validated trace shape before the renderer recreates the
        /// graph-owned images for a replacement C5 generation.
        /// </summary>
        internal void PrepareNearFieldResidualGeneration(
            in SimpleDdgiNearFieldResidualLayout layout)
        {
            if (!layout.IsValid ||
                layout.SourceFormat !=
                    SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat ||
                layout.TraceResolutionScale is not (0.5f or 0.25f or 0.125f))
            {
                throw new ArgumentException(
                    "C5 replacement render targets require a valid V13 layout.",
                    nameof(layout));
            }
            if (NearFieldDirectSource is null)
            {
                throw new InvalidOperationException(
                    "C5 replacement targets were not admitted at startup.");
            }
            _nearFieldResidualResolutionScale =
                layout.TraceResolutionScale;
        }
        public ulong GiCausticRenderTargetBytes => SumEnabledBytes(
            GiCausticReceiverPayload,
            GiCausticRadiance,
            GiCausticMoments);
        public ulong HybridReflectionRenderTargetBytes => SumEnabledBytes(
            HybridReflectionReceiverPayload,
            HybridReflectionRawRadiance,
            HybridReflectionRawMetadata,
            HybridReflectionHistory0,
            HybridReflectionHistory1,
            HybridReflectionMoments0,
            HybridReflectionMoments1,
            HybridReflectionHistoryMetadata0,
            HybridReflectionHistoryMetadata1,
            HybridReflectionFilterScratch,
            HybridReflectionDdgiCohorts);
        public ulong AntiAliasingRenderTargetBytes => SumEnabledBytes(LdrSceneColor, SmaaEdges, SmaaBlendWeights, MotionVectors, TaaHistoryA, TaaHistoryB);
        public ulong VariableRateShadingRenderTargetBytes =>
            SumEnabledBytes(VariableRateShading);
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
            bool materialTransportProvenanceEnabled = false,
            bool hybridReflectionsEnabled = false,
            AmbientOcclusionMode ambientOcclusionMode =
                AmbientOcclusionMode.Ssao)
        {
            ulong before = TotalEstimatedBytes;
            RecreateIfDifferent(SceneColor, extent);
            RecreateIfDifferent(SceneDepth, extent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.FogOutput, FoggedSceneColor, CalculateFoggedSceneColorExtent(extent, fogEnabled));
            RecreateAmbientOcclusionTargets(
                extent,
                ambientOcclusionResolutionScale,
                ambientOcclusionEnabled,
                ambientOcclusionMode);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.MaterialTransportProvenance,
                MaterialTransportProvenance,
                materialTransportProvenanceEnabled ? extent : PlaceholderExtent);
            RecreateGiCausticTargets(extent);
            RecreateHybridReflectionTargets(extent, hybridReflectionsEnabled);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.VariableRateShading,
                VariableRateShading,
                _context.FragmentShadingRateSupported
                    ? CalculateVariableRateShadingExtent(
                        extent,
                        _context.FragmentShadingRateAttachmentTexelSize)
                    : PlaceholderExtent);
            RecreateAntiAliasingTargets(extent, outputExtent, antiAliasingMode, motionVectorsEnabled);
            RecreateWeightedOitTargets(extent, weightedOitEnabled);
            RecreateBloomTargets(extent, bloomMipCount);
            if (TotalEstimatedBytes != before)
                ResizeCount++;
        }

        public void RecreateAntiAliasingTargets(Extent2D extent, Extent2D outputExtent, AntiAliasingMode mode, bool motionVectorsEnabled)
        {
            Extent2D antiAliasingExtent = CalculateAntiAliasingExtent(extent, mode);
            RecreateGraphOwnedTarget(RenderGraphResourceId.LdrSceneColor, LdrSceneColor, RequiresAntiAliasingTarget(mode) ? antiAliasingExtent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.SmaaEdges, SmaaEdges, AntiAliasingSettings.IsSmaaMode(mode) ? antiAliasingExtent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.SmaaBlendWeights, SmaaBlendWeights, AntiAliasingSettings.IsSmaaMode(mode) ? antiAliasingExtent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.MotionVectors, MotionVectors, motionVectorsEnabled ? extent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.TaaHistory, TaaHistoryA, mode == AntiAliasingMode.Taa ? outputExtent : PlaceholderExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.TaaHistory, TaaHistoryB, mode == AntiAliasingMode.Taa ? outputExtent : PlaceholderExtent);
        }

        public static Extent2D CalculateAntiAliasingExtent(
            Extent2D sourceExtent,
            AntiAliasingMode mode) =>
            CalculateAntiAliasingExtent(
                sourceExtent,
                AntiAliasingSettings.GetSmaaResolutionScale(mode));

        public static Extent2D CalculateAntiAliasingExtent(
            Extent2D sourceExtent,
            float resolutionScale)
        {
            float scale = float.IsFinite(resolutionScale)
                ? Math.Clamp(resolutionScale, 0.5f, 1.0f)
                : 1.0f;
            return new Extent2D
            {
                Width = Math.Max(1u, (uint)MathF.Ceiling(sourceExtent.Width * scale)),
                Height = Math.Max(1u, (uint)MathF.Ceiling(sourceExtent.Height * scale))
            };
        }

        public static Extent2D CalculateVariableRateShadingExtent(
            Extent2D sourceExtent,
            Extent2D attachmentTexelSize)
        {
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(sourceExtent));
            if (attachmentTexelSize.Width == 0 ||
                attachmentTexelSize.Height == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attachmentTexelSize));
            }

            return new Extent2D
            {
                Width = checked((sourceExtent.Width +
                    attachmentTexelSize.Width - 1) /
                    attachmentTexelSize.Width),
                Height = checked((sourceExtent.Height +
                    attachmentTexelSize.Height - 1) /
                    attachmentTexelSize.Height)
            };
        }

        public void RecreateWeightedOitTargets(Extent2D extent, bool enabled)
        {
            Extent2D targetExtent = enabled ? extent : PlaceholderExtent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.WeightedOitAccumulation, WeightedOitAccumulation, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.WeightedOitRevealage, WeightedOitRevealage, targetExtent);
        }

        public void RecreateAmbientOcclusionTargets(
            Extent2D sceneExtent,
            float resolutionScale,
            bool enabled,
            AmbientOcclusionMode mode = AmbientOcclusionMode.Ssao)
        {
            Extent2D workingExtent = enabled
                ? CalculateAmbientOcclusionExtent(sceneExtent, resolutionScale)
                : PlaceholderExtent;
            Extent2D resolvedExtent = enabled ? sceneExtent : PlaceholderExtent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.AmbientOcclusionRaw, AmbientOcclusionRaw, workingExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.AmbientOcclusionBlurred, AmbientOcclusionBlurred, resolvedExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.AmbientOcclusionScratch, AmbientOcclusionScratch, workingExtent);
            bool gtaoEnabled = enabled && mode == AmbientOcclusionMode.Gtao;
            Extent2D gtaoWorkingExtent = gtaoEnabled
                ? workingExtent
                : PlaceholderExtent;
            Extent2D gtaoResolvedExtent = gtaoEnabled
                ? sceneExtent
                : PlaceholderExtent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoRaw,
                GtaoRaw, gtaoWorkingExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoSpatialScratch,
                GtaoSpatialScratch, gtaoResolvedExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoHistory,
                GtaoHistory0, gtaoWorkingExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoHistory,
                GtaoHistory1, gtaoWorkingExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoGeometryHistory,
                GtaoGeometryHistory0, gtaoWorkingExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoGeometryHistory,
                GtaoGeometryHistory1, gtaoWorkingExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.GtaoFiltered,
                GtaoFiltered, gtaoResolvedExtent);
        }

        private void CreateHybridReflectionTargets(Extent2D extent)
        {
            HybridReflectionReceiverPayload = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionReceiverPayload,
                "Hybrid Reflection Receiver Payload",
                HybridReflectionReceiverPayloadFormat,
                extent,
                ColorSampledDescriptor);
            HybridReflectionRawRadiance = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionRawRadiance,
                "Hybrid Reflection Raw Radiance and Confidence",
                HybridReflectionRadianceFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionRawMetadata = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionRawMetadata,
                "Hybrid Reflection Raw Source Metadata",
                HybridReflectionRawMetadataFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionHistory0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionHistory,
                "Hybrid Reflection Radiance History 0",
                HybridReflectionRadianceFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionHistory1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionHistory,
                "Hybrid Reflection Radiance History 1",
                HybridReflectionRadianceFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionMoments0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionMoments,
                "Hybrid Reflection Moments 0",
                HybridReflectionMomentsFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionMoments1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionMoments,
                "Hybrid Reflection Moments 1",
                HybridReflectionMomentsFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionHistoryMetadata0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionHistoryMetadata,
                "Hybrid Reflection History Metadata 0",
                HybridReflectionHistoryMetadataFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionHistoryMetadata1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionHistoryMetadata,
                "Hybrid Reflection History Metadata 1",
                HybridReflectionHistoryMetadataFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionFilterScratch = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionFilterScratch,
                "Hybrid Reflection Filter Scratch",
                HybridReflectionRadianceFormat,
                extent,
                NearFieldStorageSampledDescriptor);
            HybridReflectionDdgiCohorts = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.HybridReflectionDdgiCohorts,
                "Hybrid Reflection DDGI Cohort Records",
                HybridReflectionRadianceFormat,
                extent,
                NearFieldStorageSampledDescriptor);
        }

        private void RecreateHybridReflectionTargets(
            Extent2D extent,
            bool enabled)
        {
            if (HybridReflectionReceiverPayload is null)
                return;

            Extent2D targetExtent = enabled ? extent : PlaceholderExtent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionReceiverPayload,
                HybridReflectionReceiverPayload, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionRawRadiance,
                HybridReflectionRawRadiance!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionRawMetadata,
                HybridReflectionRawMetadata!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionHistory,
                HybridReflectionHistory0!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionHistory,
                HybridReflectionHistory1!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionMoments,
                HybridReflectionMoments0!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionMoments,
                HybridReflectionMoments1!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionHistoryMetadata,
                HybridReflectionHistoryMetadata0!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionHistoryMetadata,
                HybridReflectionHistoryMetadata1!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionFilterScratch,
                HybridReflectionFilterScratch!, targetExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.HybridReflectionDdgiCohorts,
                HybridReflectionDdgiCohorts!, targetExtent);
        }

        private void RecreateNearFieldResidualSourceTargets(Extent2D extent)
        {
            if (NearFieldDirectSource is null)
                return;

            Extent2D traceExtent = CalculateNearFieldTraceExtent(extent);
            bool traceResolutionSource = _nearFieldResidualGeneration?.Layout
                .SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
            Extent2D sourceExtent = traceResolutionSource
                ? traceExtent
                : extent;
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldDirectSource,
                NearFieldDirectSource, sourceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldReceiverPayload,
                NearFieldReceiverPayload!, sourceExtent);
            if (NearFieldTraceRasterDepth is not null)
            {
                RecreateGraphOwnedTarget(
                    RenderGraphResourceId.NearFieldTraceRasterDepth,
                    NearFieldTraceRasterDepth,
                    traceExtent);
            }
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldResidualRaw,
                NearFieldResidualRaw!, traceExtent);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                NearFieldPreparedDepthFootprint!, traceExtent);
            RecreateGraphOwnedTarget(
                RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                NearFieldPreparedReceiverPayload!, traceExtent);
            RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldPreparedMotion,
                NearFieldPreparedMotion!, traceExtent);
            if (NearFieldSourceLuminance is not null)
            {
                RecreateGraphOwnedTarget(RenderGraphResourceId.NearFieldSourceLuminance,
                    NearFieldSourceLuminance, traceExtent);
            }
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
            if (_nearFieldResidualGeneration is { } generation)
                ReleaseNearFieldResidualGeneration(generation);
        }

        private void CreateNearFieldHistoryTargets(Extent2D traceExtent)
        {
            NearFieldResidualHistory0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistory,
                "Near-Field Residual History 0",
                NearFieldResidualRadianceFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualHistory1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistory,
                "Near-Field Residual History 1",
                NearFieldResidualRadianceFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualMoments0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualMoments,
                "Near-Field Residual Moments 0",
                NearFieldResidualMomentsFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualMoments1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualMoments,
                "Near-Field Residual Moments 1",
                NearFieldResidualMomentsFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualValidity0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualValidity,
                "Near-Field Residual Validity 0",
                NearFieldResidualValidityFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualValidity1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualValidity,
                "Near-Field Residual Validity 1",
                NearFieldResidualValidityFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualHistoryNormals0 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                "Near-Field Residual Normal History 0",
                NearFieldResidualNormalsFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
            NearFieldResidualHistoryNormals1 = CreateGraphOwnedRenderTarget(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                "Near-Field Residual Normal History 1",
                NearFieldResidualNormalsFormat,
                traceExtent,
                NearFieldStorageSampledDescriptor);
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
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoRaw, GtaoRaw);
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoSpatialScratch,
                GtaoSpatialScratch);
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoHistory,
                GtaoHistory0);
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoHistory,
                GtaoHistory1);
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoGeometryHistory,
                GtaoGeometryHistory0);
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoGeometryHistory,
                GtaoGeometryHistory1);
            DisposeIfManagerOwned(RenderGraphResourceId.GtaoFiltered,
                GtaoFiltered);
            DisposeIfManagerOwned(
                RenderGraphResourceId.MaterialTransportProvenance,
                MaterialTransportProvenance);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldDirectSource,
                NearFieldDirectSource);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldReceiverPayload,
                NearFieldReceiverPayload);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldTraceRasterDepth,
                NearFieldTraceRasterDepth);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldResidualRaw,
                NearFieldResidualRaw);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                NearFieldPreparedDepthFootprint);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                NearFieldPreparedReceiverPayload);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldPreparedMotion,
                NearFieldPreparedMotion);
            DisposeIfManagerOwned(RenderGraphResourceId.NearFieldSourceLuminance,
                NearFieldSourceLuminance);
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
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionReceiverPayload,
                HybridReflectionReceiverPayload);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionRawRadiance,
                HybridReflectionRawRadiance);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionRawMetadata,
                HybridReflectionRawMetadata);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionHistory,
                HybridReflectionHistory0);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionHistory,
                HybridReflectionHistory1);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionMoments,
                HybridReflectionMoments0);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionMoments,
                HybridReflectionMoments1);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionHistoryMetadata,
                HybridReflectionHistoryMetadata0);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionHistoryMetadata,
                HybridReflectionHistoryMetadata1);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionFilterScratch,
                HybridReflectionFilterScratch);
            DisposeIfManagerOwned(RenderGraphResourceId.HybridReflectionDdgiCohorts,
                HybridReflectionDdgiCohorts);
            DisposeIfManagerOwned(RenderGraphResourceId.LdrSceneColor, LdrSceneColor);
            DisposeIfManagerOwned(RenderGraphResourceId.SmaaEdges, SmaaEdges);
            DisposeIfManagerOwned(RenderGraphResourceId.SmaaBlendWeights, SmaaBlendWeights);
            DisposeIfManagerOwned(RenderGraphResourceId.MotionVectors, MotionVectors);
            DisposeIfManagerOwned(
                RenderGraphResourceId.VariableRateShading,
                VariableRateShading);
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
