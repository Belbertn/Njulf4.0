using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class TransparentForwardPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly ForwardPlusPass _forwardPlusPass;
        private readonly RaySceneDescriptorBank? _raySceneDescriptors;

        private readonly ISimpleDdgiReceiverFeedbackCapture?
            _receiverFeedbackRuntime;

        public TransparentForwardPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            ForwardPlusPass forwardPlusPass,
            RaySceneDescriptorBank? raySceneDescriptors = null,
            ISimpleDdgiReceiverFeedbackCapture? receiverFeedbackRuntime = null)
            : base("TransparentForwardPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _forwardPlusPass = forwardPlusPass ??
                               throw new ArgumentNullException(nameof(forwardPlusPass));
            _raySceneDescriptors = raySceneDescriptors;
            _receiverFeedbackRuntime = receiverFeedbackRuntime;
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.TransparentPassEnabled &&
                   sceneData.TransparencyMode == TransparencyMode.SortedAlphaBlend &&
                   sceneData.TransparentMeshletCount > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData))
                return;

            if (sceneData.TransparentPipelinePartitioningEnabled &&
                TryExecutePartitioned(cmd, frameIndex, sceneData))
            {
                return;
            }

            bool allTransparentSurfacesAreThinGlass =
                AllTransparentSurfacesAreThinGlass(sceneData);
            bool existingRayVariantRequired =
                !allTransparentSurfacesAreThinGlass &&
                sceneData.TransparentObjectCount > 0 &&
                (sceneData.DirectionalShadowFramePlan.TransparentReceiverPolicy ==
                 DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery ||
                 sceneData.EffectiveThickTransmissionMode ==
                 ThickTransmissionMode.RayQuery);
            bool reflectionRayVariantRequired =
                RequiresSceneReflectionRayVariant(sceneData);
            bool rayVariant =
                (existingRayVariantRequired || reflectionRayVariantRequired) &&
                _raySceneDescriptors?.IsAvailable == true &&
                _meshPipeline.TryEnsureRayTransparentPipelines();
            if (sceneData.TransparentReceiveGlobalIllumination ||
                rayVariant ||
                sceneData.DecalReceiveGlobalIllumination)
            {
                PublishComputeStorageToFragment(cmd);
            }

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            bool exactFeedback = TrySelectExactFeedbackPipeline(
                frameIndex,
                sceneData,
                rayVariant,
                out Silk.NET.Vulkan.Pipeline pipeline,
                out PipelineLayout pipelineLayout);
            bool geometryDecalOverlay = ShouldUseGeometryDecalOverlay(
                sceneData,
                exactFeedback,
                rayVariant,
                _meshPipeline.GeometryDecalOverlayPipeline.Handle != 0);
            bool normalDirectionalOnlyThinGlass =
                ShouldUseDirectionalOnlyThinGlass(
                    sceneData,
                    exactFeedback,
                    rayVariant,
                    _meshPipeline.ThinGlassForwardPipeline.Handle != 0);
            bool exactDirectionalOnlyThinGlass =
                exactFeedback &&
                pipeline.Handle ==
                _meshPipeline.ThinGlassReceiverFeedbackPipeline.Handle;
            bool directionalOnlyThinGlass =
                normalDirectionalOnlyThinGlass ||
                exactDirectionalOnlyThinGlass;
            sceneData.ThinGlassDirectionalOnlyPipelineEnabled =
                directionalOnlyThinGlass ? 1 : 0;
            if (geometryDecalOverlay)
            {
                pipeline = _meshPipeline.GeometryDecalOverlayPipeline;
                pipelineLayout = _meshPipeline.Layout;
            }
            else if (normalDirectionalOnlyThinGlass)
            {
                pipeline = _meshPipeline.ThinGlassForwardPipeline;
                pipelineLayout = _meshPipeline.Layout;
            }

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            BindBindlessStorageAndTextures(cmd, pipelineLayout);
            if (rayVariant)
            {
                _raySceneDescriptors!.Bind(
                    cmd,
                    PipelineBindPoint.Graphics,
                    pipelineLayout,
                    frameIndex);
            }

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            var pushConstants = new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)sceneData.TransparentMeshletCount,
                MeshletDrawBufferBaseIndex = BindlessIndex.TransparentMeshletDrawBufferBase,
                PackedLightDispatch = GPUForwardPushConstants.PackLightDispatch(
                    sceneData.LightCount,
                    sceneData.LocalLightCount,
                    sceneData.DirectionalLightIndex0,
                    sceneData.DirectionalLightIndex1),
                LocalLightCount = (uint)sceneData.LocalLightCount,
                // Hi-Z is disabled for transparent task culling, so this word
                // carries the exact bounded optical traversal limits to the
                // fragment stage without changing the push-constant ABI.
                HiZMipCount = GPUForwardPushConstants.PackThickTransmissionLimits(
                    _meshPipeline.Settings.Transparency
                        .ThickTransmissionMaximumInterfaces,
                    _meshPipeline.Settings.Transparency
                        .ThickTransmissionMaximumMediaDepth,
                    _meshPipeline.Settings.Transparency
                        .ThickTransmissionMaximumCandidatesPerInterface),
                // Low bits remain Hi-Z off. Higher bits carry the exact
                // frame-local thick-transmission task budget.
                OcclusionCullingEnabled =
                    GPUForwardPushConstants.PackThickTransmissionTaskBudget(
                        _meshPipeline.Settings.Transparency
                            .ThickTransmissionRayTaskBudget),
                // Transparent task culling is disabled; this otherwise-unused
                // word carries the bounded optical path distance without
                // growing the frozen 256-byte forward push ABI.
                OcclusionBias = _meshPipeline.Settings.Transparency
                    .ThickTransmissionMaximumDistance,
                DebugAndAoFlags = GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    ambientOcclusionEnabled: false,
                    ambientOcclusionDebugView: (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: sceneData.TransparentReceiveShadows,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)AmbientOcclusionForwardSamplingMode.Disabled,
                    globalIlluminationEnabled:
                    sceneData.TransparentReceiveGlobalIllumination),
                DiagnosticFlags = GPUForwardPushConstants.PackDiagnosticFlags(
                    ddgiForwardEstimateCountersEnabled: false,
                    directionalShadowPreviewCascade: (uint)sceneData.DirectionalShadowPreviewCascade,
                    decalGlobalIlluminationEnabled:
                    sceneData.DecalReceiveGlobalIllumination,
                    ddgiLayeredReceiverCountersEnabled:
                    sceneData.TransparentDdgiReceiverCountersEnabled,
                    decalReceiveShadows: sceneData.DecalReceiveShadows,
                    thickTransmissionRayQueryEnabled:
                    sceneData.EffectiveThickTransmissionMode ==
                    ThickTransmissionMode.RayQuery,
                    thickTransmissionDispersionEnabled:
                    sceneData.ThickTransmissionDispersionEnabled,
                    effectiveReflectionMode:
                    sceneData.EffectiveReflectionMode,
                    transparentSampleReflections:
                    sceneData.TransparentSampleReflections,
                    opaqueSceneColorSnapshotAvailable:
                    sceneData.OpaqueSceneColorSnapshotAvailable)
            };

            uint size = (uint)Marshal.SizeOf<GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                pipelineLayout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            _context.ExtMeshShader.CmdDrawMeshTask(
                cmd,
                (uint)sceneData.TransparentMeshletCount,
                1,
                1);

            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            if (exactFeedback &&
                !_receiverFeedbackRuntime!.TryRecordOwnedProducerCompletion(
                    cmd,
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit,
                    out string completionReason))
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-transparent-completion-failed:" +
                    completionReason);
            }
        }

        private bool TryExecutePartitioned(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData)
        {
            sceneData.TransparentPipelinePartitioningEffective = false;
            sceneData.TransparentPipelineRunCount = 0;
            sceneData.TransparentPipelineAverageRunLength = 0;
            sceneData.TransparentPipelineMaximumRunLength = 0;
            sceneData.TransparentPipelineBindCount = 0;
            sceneData.TransparentPipelineUniversalFallbackCount = 0;
            sceneData.TransparentPipelineRayMeshletsAvoided = 0;
            sceneData.TransparentPipelineDecalCacheMeshlets = 0;
            sceneData.TransparentPipelineFallbackReason = string.Empty;

            if (sceneData.DebugViewMode != 0u ||
                sceneData.TransparencyDebugView !=
                    TransparencyDebugView.None ||
                sceneData.DecalDebugView != DecalDebugView.None ||
                sceneData.AmbientOcclusionDebugView !=
                    AmbientOcclusionDebugView.None)
            {
                SetPartitionFallback(
                    sceneData,
                    "transparent-run-debug-view-requires-universal");
                return false;
            }

            bool exactFeedbackRequested =
                _receiverFeedbackRuntime?.IsPendingOwnedProducerRequired(
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer
                        .TransparentWeightedOit) == true;
            bool exactFeedbackRequired = exactFeedbackRequested &&
                !RequiresCanonicalRayColorPipeline(sceneData);
            if (exactFeedbackRequested && !exactFeedbackRequired)
            {
                _receiverFeedbackRuntime!.AbortCapture(
                    "receiver-feedback-transparent-deferred-to-preserve-full-thick-transmission");
            }
            if (exactFeedbackRequired &&
                sceneData.CurrentFrameIndex != checked((uint)frameIndex))
            {
                SetPartitionFallback(
                    sceneData,
                    "transparent-run-feedback-frame-slot-mismatch");
                return false;
            }

            bool transparentLayeredRayRequired =
                sceneData.DirectionalShadowFramePlan
                    .TransparentReceiverPolicy ==
                DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery;
            bool decalLayeredRayRequired =
                sceneData.DirectionalShadowFramePlan.DecalReceiverPolicy ==
                DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery;
            var options = new TransparentRunPlanningOptions(
                sceneData.TransparencyMode,
                transparentLayeredRayRequired,
                decalLayeredRayRequired,
                sceneData.EffectiveThickTransmissionMode ==
                    ThickTransmissionMode.RayQuery,
                RequiresSceneReflectionRayVariant(sceneData),
                exactFeedbackRequired,
                _forwardPlusPass
                    .CanConsumeSimpleDdgiReceiverCacheForCurrentView &&
                sceneData.DecalReceiveGlobalIllumination);
            Span<TransparentDrawRun> runs = stackalloc TransparentDrawRun[
                TransparentDrawRunPlanner.DefaultMaximumRunCount];
            if (!TransparentDrawRunPlanner.TryBuildRuns(
                    sceneData.TransparentMaterialRuns,
                    sceneData.TransparentMeshletCount,
                    options,
                    runs,
                    out int runCount,
                    out string fallbackReason))
            {
                SetPartitionFallback(sceneData, fallbackReason);
                return false;
            }

            Span<PipelineObjects.TransparentPipelineSelection> selections =
                stackalloc PipelineObjects.TransparentPipelineSelection[
                    TransparentDrawRunPlanner.DefaultMaximumRunCount];
            Span<uint> packedDrawRanges = stackalloc uint[
                TransparentDrawRunPlanner.DefaultMaximumRunCount];
            bool anyRayRun = false;
            int maximumRunLength = 0;
            int rayMeshletsAvoided = 0;
            int decalCacheMeshlets = 0;
            for (int index = 0; index < runCount; index++)
            {
                TransparentDrawRun run = runs[index];
                if (!_meshPipeline.TryResolveTransparentPipeline(
                        run.PipelineKey,
                        out selections[index],
                        out string pipelineFailure))
                {
                    SetPartitionFallback(
                        sceneData,
                        "transparent-run-pipeline-unavailable:" +
                        pipelineFailure);
                    return false;
                }
                if (!GPUForwardPushConstants.TryPackTransparentDrawRange(
                        BindlessIndex.TransparentMeshletDrawBufferBase,
                        checked((uint)run.FirstDraw),
                        out packedDrawRanges[index]))
                {
                    SetPartitionFallback(
                        sceneData,
                        "transparent-run-range-not-representable");
                    return false;
                }

                anyRayRun |= run.PipelineKey.RaySceneRequired;
                if (!run.PipelineKey.RaySceneRequired &&
                    (transparentLayeredRayRequired ||
                     decalLayeredRayRequired ||
                     options.ThickTransmissionRayQueryEnabled ||
                     options.ReflectionRayQueryEnabled))
                {
                    rayMeshletsAvoided += run.DrawCount;
                }
                if (run.PipelineKey.DecalReceiverCacheRequired)
                    decalCacheMeshlets += run.DrawCount;
                maximumRunLength = Math.Max(
                    maximumRunLength,
                    run.DrawCount);
            }

            if (anyRayRun &&
                _raySceneDescriptors?.IsAvailable != true)
            {
                SetPartitionFallback(
                    sceneData,
                    "transparent-run-ray-descriptors-unavailable");
                return false;
            }

            if (sceneData.TransparentReceiveGlobalIllumination ||
                anyRayRun ||
                sceneData.DecalReceiveGlobalIllumination)
            {
                PublishComputeStorageToFragment(cmd);
            }

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);
            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = renderExtent
                },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };
            _context.KhrDynamicRendering.CmdBeginRendering(
                cmd,
                &renderingInfo);

            var pushConstants = new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(
                    sceneData.ScreenWidth,
                    sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                PackedLightDispatch =
                    GPUForwardPushConstants.PackLightDispatch(
                        sceneData.LightCount,
                        sceneData.LocalLightCount,
                        sceneData.DirectionalLightIndex0,
                        sceneData.DirectionalLightIndex1),
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZMipCount =
                    GPUForwardPushConstants.PackThickTransmissionLimits(
                        _meshPipeline.Settings.Transparency
                            .ThickTransmissionMaximumInterfaces,
                        _meshPipeline.Settings.Transparency
                            .ThickTransmissionMaximumMediaDepth,
                        _meshPipeline.Settings.Transparency
                            .ThickTransmissionMaximumCandidatesPerInterface),
                OcclusionCullingEnabled =
                    GPUForwardPushConstants
                        .PackThickTransmissionTaskBudget(
                            _meshPipeline.Settings.Transparency
                                .ThickTransmissionRayTaskBudget),
                OcclusionBias = _meshPipeline.Settings.Transparency
                    .ThickTransmissionMaximumDistance,
                DebugAndAoFlags =
                    GPUForwardPushConstants.PackDebugAndAoFlags(
                        sceneData.DebugViewMode,
                        ambientOcclusionEnabled: false,
                        ambientOcclusionDebugView:
                            (uint)sceneData.AmbientOcclusionDebugView,
                        transparentReceiveShadows:
                            sceneData.TransparentReceiveShadows,
                        transparencyDebugView:
                            (uint)sceneData.TransparencyDebugView,
                        ambientOcclusionForwardSamplingMode:
                            (uint)AmbientOcclusionForwardSamplingMode
                                .Disabled,
                        globalIlluminationEnabled:
                            sceneData
                                .TransparentReceiveGlobalIllumination),
                DiagnosticFlags =
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        ddgiForwardEstimateCountersEnabled: false,
                        directionalShadowPreviewCascade:
                            (uint)sceneData
                                .DirectionalShadowPreviewCascade,
                        decalGlobalIlluminationEnabled:
                            sceneData.DecalReceiveGlobalIllumination,
                        ddgiLayeredReceiverCountersEnabled:
                            sceneData
                                .TransparentDdgiReceiverCountersEnabled,
                        decalReceiveShadows:
                            sceneData.DecalReceiveShadows,
                        thickTransmissionRayQueryEnabled:
                            sceneData.EffectiveThickTransmissionMode ==
                            ThickTransmissionMode.RayQuery,
                        thickTransmissionDispersionEnabled:
                            sceneData
                                .ThickTransmissionDispersionEnabled,
                        effectiveReflectionMode:
                            sceneData.EffectiveReflectionMode,
                        transparentSampleReflections:
                            sceneData.TransparentSampleReflections,
                        opaqueSceneColorSnapshotAvailable:
                            sceneData
                                .OpaqueSceneColorSnapshotAvailable)
            };
            uint pushConstantSize =
                (uint)Marshal.SizeOf<GPUForwardPushConstants>();
            PipelineObjects.TransparentPipelineSelection previous = default;
            bool hasPreviousSelection = false;
            int pipelineBindCount = 0;
            for (int index = 0; index < runCount; index++)
            {
                TransparentDrawRun run = runs[index];
                PipelineObjects.TransparentPipelineSelection selection =
                    selections[index];
                if (!hasPreviousSelection ||
                    selection.Pipeline.Handle != previous.Pipeline.Handle ||
                    selection.Layout.Handle != previous.Layout.Handle ||
                    selection.BindRayScene != previous.BindRayScene ||
                    selection.BindReceiverCache !=
                        previous.BindReceiverCache)
                {
                    _context.Api.CmdBindPipeline(
                        cmd,
                        PipelineBindPoint.Graphics,
                        selection.Pipeline);
                    BindBindlessStorageAndTextures(
                        cmd,
                        selection.Layout);
                    if (selection.BindReceiverCache)
                    {
                        _forwardPlusPass
                            .BindSimpleDdgiReceiverCacheBuffer(
                                cmd,
                                frameIndex);
                    }
                    if (selection.BindRayScene)
                    {
                        _raySceneDescriptors!.Bind(
                            cmd,
                            PipelineBindPoint.Graphics,
                            selection.Layout,
                            frameIndex);
                    }

                    previous = selection;
                    hasPreviousSelection = true;
                    pipelineBindCount++;
                }

                pushConstants.MeshletDrawCount =
                    checked((uint)run.DrawCount);
                pushConstants.MeshletDrawBufferBaseIndex =
                    packedDrawRanges[index];
                _context.Api.CmdPushConstants(
                    cmd,
                    selection.Layout,
                    ShaderStageFlags.MeshBitExt |
                    ShaderStageFlags.FragmentBit |
                    ShaderStageFlags.TaskBitExt,
                    0,
                    pushConstantSize,
                    &pushConstants);
                _context.ExtMeshShader.CmdDrawMeshTask(
                    cmd,
                    checked((uint)run.DrawCount),
                    1,
                    1);
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            if (exactFeedbackRequired &&
                !_receiverFeedbackRuntime!
                    .TryRecordOwnedProducerCompletion(
                        cmd,
                        frameIndex,
                        SimpleDdgiReceiverFeedbackProducer
                            .TransparentWeightedOit,
                        out string completionReason))
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-transparent-completion-failed:" +
                    completionReason);
            }

            sceneData.ThinGlassDirectionalOnlyPipelineEnabled = 0;
            sceneData.TransparentPipelinePartitioningEffective = true;
            sceneData.TransparentPipelineRunCount = runCount;
            sceneData.TransparentPipelineAverageRunLength =
                sceneData.TransparentMeshletCount / runCount;
            sceneData.TransparentPipelineMaximumRunLength =
                maximumRunLength;
            sceneData.TransparentPipelineBindCount = pipelineBindCount;
            sceneData.TransparentPipelineRayMeshletsAvoided =
                rayMeshletsAvoided;
            sceneData.TransparentPipelineDecalCacheMeshlets =
                decalCacheMeshlets;
            return true;
        }

        private static void SetPartitionFallback(
            SceneRenderingData sceneData,
            string reason)
        {
            sceneData.TransparentPipelinePartitioningEffective = false;
            sceneData.TransparentPipelineUniversalFallbackCount = 1;
            sceneData.TransparentPipelineFallbackReason = reason;
        }

        internal static bool ShouldUseGeometryDecalOverlay(
            SceneRenderingData sceneData,
            bool exactFeedback,
            bool rayVariant,
            bool overlayPipelineAvailable)
        {
            ArgumentNullException.ThrowIfNull(sceneData);

            // A color decal inherits the opaque depth owner's already shaded
            // direct light, DDGI, shadows, and reflections through destination
            // modulation. Real transparent surfaces retain full forward
            // shading, and exact B1 captures retain their producer program.
            return sceneData.TransparentObjectCount == 0 &&
                   sceneData.TransparentMeshletCount > 0 &&
                   sceneData.GeometryDecalMeshletCount >=
                   sceneData.TransparentMeshletCount &&
                   sceneData.DebugViewMode == 0u &&
                   sceneData.DecalDebugView == DecalDebugView.None &&
                   sceneData.TransparencyDebugView == TransparencyDebugView.None &&
                   !exactFeedback &&
                   !rayVariant &&
                   overlayPipelineAvailable;
        }

        internal static bool ShouldUseDirectionalOnlyThinGlass(
            SceneRenderingData sceneData,
            bool exactFeedback,
            bool rayVariant,
            bool pipelineAvailable)
        {
            ArgumentNullException.ThrowIfNull(sceneData);

            // A single mesh-task dispatch can bind only one fragment program.
            // Admit the narrow program only when every real transparent draw
            // is explicitly classified ThinGlass. Diagnostics, ray-query
            // volume transport, decals, and B1 exact feedback retain the full
            // semantic shader. Exact B1 uses a matching directional-only
            // artifact selected separately by TrySelectExactFeedbackPipeline.
            return AllTransparentSurfacesAreThinGlass(sceneData) &&
                   sceneData.GeometryDecalMeshletCount == 0 &&
                   sceneData.DebugViewMode == 0u &&
                   sceneData.TransparencyDebugView ==
                   TransparencyDebugView.None &&
                   sceneData.DecalDebugView == DecalDebugView.None &&
                   sceneData.AmbientOcclusionDebugView ==
                   AmbientOcclusionDebugView.None &&
                   !exactFeedback &&
                   !rayVariant &&
                   pipelineAvailable;
        }

        internal static bool AllTransparentSurfacesAreThinGlass(
            SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            return sceneData.TransparentObjectCount > 0 &&
                   sceneData.ThinGlassObjectCount ==
                   sceneData.TransparentObjectCount &&
                   sceneData.TransparentMeshletCount > 0 &&
                   sceneData.ThinGlassMeshletCount ==
                   sceneData.TransparentMeshletCount;
        }

        internal static bool RequiresSceneReflectionRayVariant(
            SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            return sceneData.TransparentSampleReflections &&
                   sceneData.OpaqueSceneColorSnapshotAvailable &&
                   sceneData.HasTransparentReflectionReceivers &&
                   sceneData.EffectiveReflectionMode ==
                   ReflectionMode.HybridRayQuery &&
                   sceneData.TransparentSceneReflectionRayTaskBudget > 0;
        }

        internal static bool RequiresCanonicalRayColorPipeline(
            SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            if (sceneData.EffectiveThickTransmissionMode !=
                ThickTransmissionMode.RayQuery)
            {
                return false;
            }

            foreach (TransparentMaterialRun run in
                     sceneData.TransparentMaterialRuns)
            {
                if (run.Classification.MaterialClass ==
                    TransparentMaterialClass.ThickTransmission)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TrySelectExactFeedbackPipeline(
            int frameIndex,
            SceneRenderingData sceneData,
            bool rayVariant,
            out Silk.NET.Vulkan.Pipeline pipeline,
            out PipelineLayout pipelineLayout)
        {
            pipeline = rayVariant
                ? _meshPipeline.RayTransparentForwardPipeline
                : _meshPipeline.TransparentForwardPipeline;
            pipelineLayout = rayVariant
                ? _meshPipeline.RayTransparentLayout
                : _meshPipeline.Layout;
            if (_receiverFeedbackRuntime is null ||
                !_receiverFeedbackRuntime.IsPendingOwnedProducerRequired(
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit))
            {
                return false;
            }
            if (rayVariant && RequiresCanonicalRayColorPipeline(sceneData))
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-transparent-deferred-to-preserve-full-thick-transmission");
                return false;
            }

            bool thinGlassVariant = ShouldUseDirectionalOnlyThinGlass(
                sceneData,
                exactFeedback: false,
                rayVariant,
                pipelineAvailable: true);
            bool exactPipelineAvailable = rayVariant
                ? _meshPipeline.TryEnsureRayTransparentReceiverFeedbackPipeline()
                : _meshPipeline.TryEnsureTransparentReceiverFeedbackPipeline(
                    thinGlassVariant);
            Silk.NET.Vulkan.Pipeline exactPipeline = rayVariant
                ? _meshPipeline.RayTransparentReceiverFeedbackPipeline
                : thinGlassVariant
                    ? _meshPipeline.ThinGlassReceiverFeedbackPipeline
                    : _meshPipeline.TransparentReceiverFeedbackPipeline;
            if (sceneData.CurrentFrameIndex != checked((uint)frameIndex) ||
                !exactPipelineAvailable ||
                exactPipeline.Handle == 0)
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-transparent-pipeline-or-frame-slot-unavailable");
                return false;
            }

            pipeline = exactPipeline;
            return true;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
        }

        public override void Cleanup()
        {
        }
    }
}
