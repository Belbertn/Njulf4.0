using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class MotionVectorPass : RenderPassBase
    {
        internal const ShaderStageFlags MeshPipelinePushConstantStages =
            ShaderStageFlags.TaskBitExt |
            ShaderStageFlags.MeshBitExt |
            ShaderStageFlags.FragmentBit;

        private readonly MeshPipeline _meshPipeline;
        private readonly FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly TemporalSurfaceValidityResources?
            _temporalSurfaceValidityResources;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly Func<SurfaceHistoryConsumer>? _historyConsumers;
        private Matrix4x4 _previousViewProjectionMatrix = Matrix4x4.Identity;
        private Vector3 _previousCameraPosition = Vector3.Zero;
        private float _previousTime;
        private bool _hasPreviousViewProjectionMatrix;
        private ulong _previousSceneContentRevision = ulong.MaxValue;
        private ulong _previousCameraCutSerial = ulong.MaxValue;
        private ulong _previousMotionFrameSerial = ulong.MaxValue;
        private bool _recordingFusedDepth;
        internal DirectionalShadowHistoryResources? DirectionalHistoryResources { get; set; }

        internal bool TryExecuteFusedDepth(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!SurfaceInputPolicy.DepthMotionFusionRequested ||
                !RenderFeatureIsolationPolicy.ShouldExecutePass(sceneData.ActiveFeatureIsolation, Name))
                return false;
            SurfaceHistoryConsumer consumers = ResolveHistoryConsumers(sceneData);
            bool identity = sceneData.DirectionalShadowFramePlan.UsesScreenHistory;
            bool targetsReady = _renderTargets.SceneDepth.Extent.Width == _renderTargets.MotionVectors.Extent.Width &&
                _renderTargets.SceneDepth.Extent.Height == _renderTargets.MotionVectors.Extent.Height &&
                _meshPipeline.GetDepthMotionPipeline(false, identity).Handle != 0 &&
                _meshPipeline.GetDepthMotionPipeline(true, identity).Handle != 0 &&
                (!identity || (_renderTargets.SurfaceReceiverIdentity?.Extent.Equals(_renderTargets.SceneDepth.Extent) == true &&
                    DirectionalHistoryResources?.IsAllocated == true &&
                    DirectionalHistoryResources.Width == _renderTargets.SceneDepth.Extent.Width &&
                    DirectionalHistoryResources.Height == _renderTargets.SceneDepth.Extent.Height));
            if (!SurfaceInputPolicy.CanFuse(sceneData, SurfaceInputPolicy.DepthMotionFusionRequested,
                    CanUseSceneCompactedMotionVectors(sceneData), targetsReady,
                    consumers.RequiresMotionVectors(), ShouldUseCameraOnlyReprojection(consumers, sceneData),
                    _renderTargets.OpaqueVisibility != null))
                return false;
            Record(cmd, frameIndex, sceneData, fused: true);
            return sceneData.DepthMotionFusionCompleted;
        }


        public MotionVectorPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            FoliagePipeline? foliagePipeline = null,
            BufferManager? bufferManager = null,
            FoliageManager? foliageManager = null,
            Func<SurfaceHistoryConsumer>? historyConsumers = null,
            TemporalSurfaceValidityResources? temporalSurfaceValidityResources = null)
            : base("MotionVectorPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _historyConsumers = historyConsumers;
            _temporalSurfaceValidityResources = temporalSurfaceValidityResources;
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (sceneData.HasCurrentDepthMotion)
                return false;
            SurfaceHistoryConsumer consumers = ResolveHistoryConsumers(sceneData);
            sceneData.SurfaceHistoryConsumers = consumers;
            bool cameraOnly = ShouldUseCameraOnlyReprojection(
                consumers,
                sceneData);
            sceneData.CameraOnlyMotionReprojectionEnabled = cameraOnly ? 1 : 0;
            if (cameraOnly)
            {
                sceneData.MotionVectorsEnabled = 0;
                return false;
            }
            return consumers.RequiresMotionVectors();
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.HasCurrentDepthMotion)
                Record(cmd, frameIndex, sceneData, fused: false);
        }

        private void Record(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData, bool fused)
        {
            _recordingFusedDepth = fused;
            SurfaceHistoryConsumer consumers = ResolveHistoryConsumers(sceneData);
            sceneData.SurfaceHistoryConsumers = consumers;
            bool cameraOnly = ShouldUseCameraOnlyReprojection(
                consumers,
                sceneData);
            sceneData.CameraOnlyMotionReprojectionEnabled = cameraOnly ? 1 : 0;
            if (cameraOnly)
            {
                sceneData.MotionVectorsEnabled = 0;
                return;
            }
            if (!consumers.RequiresMotionVectors())
            {
                sceneData.MotionVectorsEnabled = 0;
                return;
            }

            long start = Stopwatch.GetTimestamp();
            bool previousFrameValid =
                _hasPreviousViewProjectionMatrix &&
                sceneData.DdgiFrameSerial > _previousMotionFrameSerial &&
                sceneData.DdgiFrameSerial - _previousMotionFrameSerial == 1 &&
                sceneData.HiZPolicyCameraCut == 0 &&
                sceneData.HiZPolicySceneChanged == 0 &&
                _previousSceneContentRevision == sceneData.SceneContentRevision &&
                _previousCameraCutSerial == sceneData.CaptureCameraCutSerial;
            if (previousFrameValid && IsCameraCut(sceneData.ViewProjectionMatrix, _previousViewProjectionMatrix))
                previousFrameValid = false;
            Matrix4x4 previousViewProjection = previousFrameValid
                ? _previousViewProjectionMatrix
                : sceneData.ViewProjectionMatrix;
            float previousTime = previousFrameValid ? _previousTime : sceneData.Time;

            if (fused)
                _renderTargets.SceneDepth.TransitionToDepthAttachment(cmd);
            else
                _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.MotionVectors.TransitionToColorAttachment(cmd);
            bool identityAttachment = fused && sceneData.DirectionalShadowFramePlan.UsesScreenHistory;
            Extent2D renderExtent = _renderTargets.MotionVectors.Extent;
            if (IsSharedValidityActive(sceneData, renderExtent))
            {
                _temporalSurfaceValidityResources!.PrepareForMotionSeed(
                    cmd,
                    frameIndex);
            }

            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = renderExtent.Width,
                Height = renderExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = renderExtent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                0,
                1,
                &storageSet,
                0,
                null);

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                1,
                1,
                &textureSet,
                0,
                null);

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.MotionVectors.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f))
            };

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = fused ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = fused ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };

            RenderingAttachmentInfo* colorAttachments = stackalloc RenderingAttachmentInfo[2];
            colorAttachments[0] = colorAttachment;
            if (identityAttachment)
            {
                _renderTargets.SurfaceReceiverIdentity!.TransitionToColorAttachment(cmd);
                colorAttachments[1] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = _renderTargets.SurfaceReceiverIdentity.View,
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(new ClearColorValue(0u, 0u, 0u, 0u))
                };
            }
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = identityAttachment ? 2u : 1u,
                PColorAttachments = colorAttachments,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            if (CanUseSceneCompactedMotionVectors(sceneData))
            {
                DrawCompactedMotionVectorBucket(
                    cmd,
                    sceneData,
                    previousViewProjection,
                    previousTime,
                    previousFrameValid,
                    fused ? _meshPipeline.GetDepthMotionPipeline(false, identityAttachment) : _meshPipeline.CompactedMotionVectorPipeline,
                    SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                        sceneData.SceneSubmissionGpuDepthSolidCandidateCount,
                        sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity,
                        sceneData.SceneSubmissionSidedRasterSpecializationActive),
                    sceneData
                        .SceneSubmissionGpuCompactedSolidDepthDoubleSidedBase,
                    sceneData
                        .SceneSubmissionGpuCompactedSolidDepthDoubleSidedCapacity,
                    BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase,
                    SceneOpaqueCompactionPass.GetSolidDepthIndirectDispatchOffset(),
                    SceneOpaqueCompactionPass.GetSolidDepthDoubleSidedIndirectDispatchOffset());
                DrawCompactedMotionVectorBucket(
                    cmd,
                    sceneData,
                    previousViewProjection,
                    previousTime,
                    previousFrameValid,
                    fused ? _meshPipeline.GetDepthMotionPipeline(true, identityAttachment) : _meshPipeline.CompactedMaskedMotionVectorPipeline,
                    SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                        sceneData.SceneSubmissionGpuDepthMaskedCandidateCount,
                        sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity,
                        sceneData.SceneSubmissionSidedRasterSpecializationActive),
                    sceneData
                        .SceneSubmissionGpuCompactedMaskedDepthDoubleSidedBase,
                    sceneData
                        .SceneSubmissionGpuCompactedMaskedDepthDoubleSidedCapacity,
                    BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase,
                    SceneOpaqueCompactionPass.GetMaskedDepthIndirectDispatchOffset(),
                    SceneOpaqueCompactionPass.GetMaskedDepthDoubleSidedIndirectDispatchOffset());
            }
            else
            {
                DrawMotionVectorBucket(
                    cmd,
                    sceneData,
                    previousViewProjection,
                    previousTime,
                    previousFrameValid,
                    _meshPipeline.MotionVectorPipeline,
                    sceneData.SolidMeshletCount,
                    BindlessIndex.SolidDepthMeshletDrawBufferBase);
                DrawMotionVectorBucket(
                    cmd,
                    sceneData,
                    previousViewProjection,
                    previousTime,
                    previousFrameValid,
                    _meshPipeline.MaskedMotionVectorPipeline,
                    sceneData.MaskedMeshletCount,
                    BindlessIndex.MaskedDepthMeshletDrawBufferBase);
            }
            DrawFoliageMotionVectors(cmd, sceneData, previousViewProjection, previousTime, previousFrameValid);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            if (identityAttachment)
                CopyReceiverIdentity(cmd, frameIndex);
            _renderTargets.MotionVectors.TransitionToShaderRead(cmd);
            _previousViewProjectionMatrix = sceneData.ViewProjectionMatrix;
            _previousCameraPosition = sceneData.CameraPosition;
            _previousTime = sceneData.Time;
            _previousSceneContentRevision = sceneData.SceneContentRevision;
            _previousCameraCutSerial = sceneData.CaptureCameraCutSerial;
            _hasPreviousViewProjectionMatrix = true;
            _previousMotionFrameSerial = sceneData.DdgiFrameSerial;
            sceneData.DepthMotionFusionCompleted = fused;
            sceneData.MotionVectorsEnabled = previousFrameValid ? 1 : 0;
            // RenderGraph accounts the fused recording under DepthPrePass.
            sceneData.CpuMotionVectorRecordMicroseconds = fused ? 0 : ElapsedMicroseconds(start);
        }

        private void CopyReceiverIdentity(CommandBuffer cmd, int frameIndex)
        {
            RenderTarget identity = _renderTargets.SurfaceReceiverIdentity!;
            DirectionalShadowHistoryResources history = DirectionalHistoryResources!;
            VkBuffer destination = _bufferManager!.GetBuffer(history.GetScratch(frameIndex));
            identity.TransitionToTransferSource(cmd);
            BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                destination, PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit, 0, history.ScratchBufferBytes);
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
            var copy = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1
                },
                ImageExtent = new Extent3D(identity.Extent.Width, identity.Extent.Height, 1)
            };
            _context.Api.CmdCopyImageToBuffer(cmd, identity.Image, ImageLayout.TransferSrcOptimal,
                destination, 1, &copy);
            barrier = BarrierBuilder.BufferBarrier(destination,
                PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit,
                0, history.ScratchBufferBytes);
            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
        }

        private void DrawMotionVectorBucket(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Matrix4x4 previousViewProjection,
            float previousTime,
            bool previousFrameValid,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new GPUMotionVectorPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                PreviousViewProjectionMatrix = previousViewProjection,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex,
                PreviousFrameValid = PackHistoryFlags(previousFrameValid, sceneData),
                Time = sceneData.Time,
                PreviousTime = previousTime,
                CameraPosition = new Vector4(sceneData.CameraPosition, 1f),
                PreviousCameraPosition = new Vector4(
                    previousFrameValid
                        ? _previousCameraPosition
                        : sceneData.CameraPosition,
                    1f)
            };

            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                MeshPipelinePushConstantStages,
                0,
                (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                &pushConstants);

            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
        }

        private bool CanUseSceneCompactedMotionVectors(
            SceneRenderingData sceneData)
        {
            if (!_meshPipeline.TasklessSubmissionEnabled ||
                _bufferManager == null ||
                !sceneData.SceneSubmissionGpuCompactionActive ||
                !sceneData.SceneSubmissionIndirectMeshletDispatchEnabled ||
                sceneData.SceneSubmissionFallbackReason.Length != 0 ||
                !sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer.IsValid)
            {
                return false;
            }

            bool hasSolid =
                sceneData.SceneSubmissionGpuDepthSolidCandidateCount > 0;
            bool hasMasked =
                sceneData.SceneSubmissionGpuDepthMaskedCandidateCount > 0;
            if (!hasSolid && !hasMasked)
                return false;

            bool solidReady = !hasSolid ||
                (sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBuffer.IsValid &&
                 (sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity > 0 ||
                  sceneData.SceneSubmissionGpuCompactedSolidDepthDoubleSidedCapacity > 0));
            bool maskedReady = !hasMasked ||
                (sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBuffer.IsValid &&
                 (sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity > 0 ||
                  sceneData.SceneSubmissionGpuCompactedMaskedDepthDoubleSidedCapacity > 0));
            if (!solidReady || !maskedReady)
                return false;

            ulong requiredOffset = 0;
            if (hasSolid)
            {
                requiredOffset = sceneData.SceneSubmissionSidedRasterSpecializationActive
                    ? SceneOpaqueCompactionPass.GetSolidDepthDoubleSidedIndirectDispatchOffset()
                    : SceneOpaqueCompactionPass.GetSolidDepthIndirectDispatchOffset();
            }
            if (hasMasked)
            {
                ulong maskedOffset = sceneData.SceneSubmissionSidedRasterSpecializationActive
                    ? SceneOpaqueCompactionPass.GetMaskedDepthDoubleSidedIndirectDispatchOffset()
                    : SceneOpaqueCompactionPass.GetMaskedDepthIndirectDispatchOffset();
                requiredOffset = Math.Max(requiredOffset, maskedOffset);
            }

            ulong requiredBytes = checked(
                requiredOffset +
                (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            return sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize >=
                   requiredBytes;
        }

        private void DrawCompactedMotionVectorBucket(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Matrix4x4 previousViewProjection,
            float previousTime,
            bool previousFrameValid,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int doubleSidedFirstDraw,
            int doubleSidedMeshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectDispatchOffset,
            ulong doubleSidedIndirectDispatchOffset)
        {
            if ((meshletCapacity <= 0 && doubleSidedMeshletCapacity <= 0) ||
                _bufferManager == null)
                return;

            if (meshletCapacity > 0)
            {
                DrawCompactedMotionVectorPartition(
                    cmd,
                    sceneData,
                    previousViewProjection,
                    previousTime,
                    previousFrameValid,
                    pipeline,
                    meshletCapacity,
                    meshletDrawBufferBaseIndex,
                    indirectDispatchOffset,
                    firstDraw: 0u,
                    oneSided: sceneData
                        .SceneSubmissionSidedRasterSpecializationActive);
            }
            if (sceneData.SceneSubmissionSidedRasterSpecializationActive)
            {
                if (doubleSidedMeshletCapacity > 0)
                {
                    DrawCompactedMotionVectorPartition(
                        cmd,
                        sceneData,
                        previousViewProjection,
                        previousTime,
                        previousFrameValid,
                        pipeline,
                        doubleSidedMeshletCapacity,
                        meshletDrawBufferBaseIndex,
                        doubleSidedIndirectDispatchOffset,
                        firstDraw: checked((uint)doubleSidedFirstDraw),
                        oneSided: false);
                }
            }
        }

        private void DrawCompactedMotionVectorPartition(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Matrix4x4 previousViewProjection,
            float previousTime,
            bool previousFrameValid,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectDispatchOffset,
            uint firstDraw,
            bool oneSided)
        {
            BufferManager bufferManager = _bufferManager ??
                throw new InvalidOperationException(
                    "Compacted motion-vector drawing requires a buffer manager.");
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            _context.Api.CmdSetCullMode(
                cmd,
                oneSided ? CullModeFlags.BackBit : CullModeFlags.None);
            _context.Api.CmdSetDepthCompareOp(
                cmd,
                CompareOp.GreaterOrEqual);
            var pushConstants = new GPUMotionVectorPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                PreviousViewProjectionMatrix = previousViewProjection,
                ScreenDimensions = new Vector2(
                    sceneData.ScreenWidth,
                    sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = checked((uint)meshletCapacity),
                MeshletDrawBufferBaseIndex =
                    checked((uint)meshletDrawBufferBaseIndex),
                PreviousFrameValid = PackHistoryFlags(
                    previousFrameValid,
                    sceneData),
                Time = sceneData.Time,
                PreviousTime = previousTime,
                FirstDraw = firstDraw,
                CameraPosition = new Vector4(sceneData.CameraPosition, 1f),
                PreviousCameraPosition = new Vector4(
                    previousFrameValid
                        ? _previousCameraPosition
                        : sceneData.CameraPosition,
                    1f)
            };
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                MeshPipelinePushConstantStages,
                0,
                (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                &pushConstants);

            if (_recordingFusedDepth)
                sceneData.DepthMeshOnlyIndirectDrawCount++;
            VkBuffer indirect = bufferManager.GetBuffer(
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                indirectDispatchOffset,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private void DrawFoliageMotionVectors(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Matrix4x4 previousViewProjection,
            float previousTime,
            bool previousFrameValid)
        {
            if (!sceneData.FoliageMotionVectorsEnabled ||
                _foliagePipeline == null ||
                _bufferManager == null ||
                _foliageManager == null ||
                sceneData.FoliageClusterCount <= 0 ||
                sceneData.FoliageDrawBufferBytes == 0)
            {
                return;
            }

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid ||
                (buffers.VisibleClusterCapacity <= 0 &&
                 buffers.MeshletDrawCapacity <= 0))
                return;

            BindFoliageDescriptorSets(cmd);
            Vector3 previousCameraPosition = previousFrameValid
                ? _previousCameraPosition
                : sceneData.CameraPosition;
            var commonPushConstants = new GPUMotionVectorPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                PreviousViewProjectionMatrix = previousViewProjection,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = checked((uint)buffers.MeshletDrawCapacity),
                MeshletDrawBufferBaseIndex = (uint)BindlessIndex.FoliageMeshletDrawBufferBase,
                PreviousFrameValid = PackHistoryFlags(previousFrameValid, sceneData),
                Time = sceneData.Time,
                PreviousTime = previousTime,
                CameraPosition = new Vector4(sceneData.CameraPosition, 1f),
                PreviousCameraPosition = new Vector4(previousCameraPosition, 1f)
            };

            VkBuffer indirect = _bufferManager.GetBuffer(
                buffers.IndirectDispatchBuffer);

            if (buffers.VisibleClusterCapacity > 0)
            {
                _context.Api.CmdBindPipeline(
                    cmd,
                    PipelineBindPoint.Graphics,
                    _foliagePipeline.ProceduralCompactedMotionVectorPipeline);
                GPUMotionVectorPushConstants proceduralPushConstants =
                    commonPushConstants;
                proceduralPushConstants.MeshletDrawCount = checked(
                    (uint)buffers.VisibleClusterCapacity);
                proceduralPushConstants.MeshletDrawBufferBaseIndex =
                    (uint)BindlessIndex.FoliageVisibleClusterBufferBase;
                _context.Api.CmdPushConstants(
                    cmd,
                    _foliagePipeline.GraphicsLayout,
                    MeshPipelinePushConstantStages,
                    0,
                    (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                    &proceduralPushConstants);

                if (sceneData.FoliageIndirectMeshletDispatchEnabled)
                {
                    _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                        cmd,
                        indirect,
                        FoliageManager.ProceduralIndirectDispatchOffset,
                        1,
                        (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
                }
                else
                {
                    _context.ExtMeshShader.CmdDrawMeshTask(
                        cmd,
                        checked((uint)buffers.VisibleClusterCapacity),
                        1,
                        1);
                }
            }

            if (buffers.MeshletDrawCapacity <= 0)
                return;

            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                _foliagePipeline.AuthoredCompactedMotionVectorPipeline);
            GPUMotionVectorPushConstants authoredPushConstants =
                commonPushConstants;
            authoredPushConstants.MeshletDrawCount = checked(
                (uint)buffers.MeshletDrawCapacity);
            authoredPushConstants.MeshletDrawBufferBaseIndex =
                (uint)BindlessIndex.FoliageMeshletDrawBufferBase;

            _context.Api.CmdPushConstants(
                cmd,
                _foliagePipeline.GraphicsLayout,
                MeshPipelinePushConstantStages,
                0,
                (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                &authoredPushConstants);

            if (sceneData.FoliageIndirectMeshletDispatchEnabled)
            {
                _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                    cmd,
                    indirect,
                    FoliageManager.AuthoredIndirectDispatchOffset,
                    1,
                    (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
                return;
            }

            _context.ExtMeshShader.CmdDrawMeshTask(cmd, checked((uint)buffers.MeshletDrawCapacity), 1, 1);
        }

        private void BindFoliageDescriptorSets(CommandBuffer cmd)
        {
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _foliagePipeline!.GraphicsLayout,
                0,
                1,
                &storageSet,
                0,
                null);

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _foliagePipeline.GraphicsLayout,
                1,
                1,
                &textureSet,
                0,
                null);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            _hasPreviousViewProjectionMatrix = false;
            _previousCameraPosition = Vector3.Zero;
            _previousSceneContentRevision = ulong.MaxValue;
            _previousCameraCutSerial = ulong.MaxValue;
        }

        public override void Cleanup()
        {
        }

        private void TransitionDepthForRead(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilReadOnlyOptimal)
                return;

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageLayout oldLayout = _swapchain.DepthImageLayout;
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilReadOnlyOptimal);

            var barrier = BarrierBuilder.CreateImageBarrier(
                _swapchain.DepthImage,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                ImageLayout.DepthStencilReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private SurfaceHistoryConsumer ResolveHistoryConsumers(
            SceneRenderingData sceneData) =>
            (_historyConsumers?.Invoke() ??
             SurfaceHistoryPolicy.Resolve(_settings, nearFieldResidualActive: false)) |
            sceneData.DirectionalShadowFramePlan.HistoryConsumers;

        internal static bool ShouldUseCameraOnlyReprojection(
            SurfaceHistoryConsumer consumers,
            SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            // Reflection and C5 both reconstruct static receiver history from
            // depth plus the current/previous camera matrices. Other temporal
            // consumers still require the authored full-resolution velocity
            // target. Any moving surface also retains the authored pass.
            const SurfaceHistoryConsumer cameraOnlyCompatible =
                SurfaceHistoryConsumer.Reflection |
                SurfaceHistoryConsumer.NearFieldResidual;
            return consumers != SurfaceHistoryConsumer.None &&
                   (consumers & ~cameraOnlyCompatible) == 0 &&
                   !sceneData.AnimationEnabled &&
                   sceneData.SkinnedObjectCount == 0 &&
                   !(sceneData.FoliageMotionVectorsEnabled &&
                     sceneData.FoliageClusterCount > 0) &&
                   sceneData.AccelerationStructureDynamicBottomLevelCount == 0 &&
                   sceneData.DirectionalDynamicShadowMeshletCount == 0;
        }

        private uint PackHistoryFlags(
            bool previousFrameValid,
            SceneRenderingData sceneData) =>
            (previousFrameValid ? 1u : 0u) |
            (sceneData.DirectionalShadowFramePlan.UsesScreenHistory ? 2u : 0u) |
            (IsSharedValidityActive(
                sceneData,
                _renderTargets.MotionVectors.Extent) ? 4u : 0u);

        private bool IsSharedValidityActive(
            SceneRenderingData sceneData,
            Extent2D extent) =>
            SurfaceInputPolicy.SharedValidityEnabled &&
            TemporalSurfaceValidityCodec.RequiresProducer(
                sceneData.SurfaceHistoryConsumers) &&
            _temporalSurfaceValidityResources?.IsCompatible(
                extent.Width,
                extent.Height) == true;

        private static bool IsCameraCut(Matrix4x4 current, Matrix4x4 previous)
        {
            float maxDelta = 0.0f;
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M11 - previous.M11));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M12 - previous.M12));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M13 - previous.M13));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M14 - previous.M14));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M21 - previous.M21));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M22 - previous.M22));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M23 - previous.M23));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M24 - previous.M24));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M31 - previous.M31));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M32 - previous.M32));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M33 - previous.M33));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M34 - previous.M34));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M41 - previous.M41));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M42 - previous.M42));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M43 - previous.M43));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(current.M44 - previous.M44));
            return !float.IsFinite(maxDelta) || maxDelta > 16.0f;
        }
    }
}
