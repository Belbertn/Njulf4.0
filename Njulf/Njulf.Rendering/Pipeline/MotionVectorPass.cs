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
        private readonly MeshPipeline _meshPipeline;
        private readonly FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private Matrix4x4 _previousViewProjectionMatrix = Matrix4x4.Identity;
        private float _previousTime;
        private bool _hasPreviousViewProjectionMatrix;

        public MotionVectorPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            FoliagePipeline? foliagePipeline = null,
            BufferManager? bufferManager = null,
            FoliageManager? foliageManager = null)
            : base("MotionVectorPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (_settings.AntiAliasing.EffectiveMode != AntiAliasingMode.Taa)
            {
                _hasPreviousViewProjectionMatrix = false;
                sceneData.MotionVectorsEnabled = 0;
                return;
            }

            long start = Stopwatch.GetTimestamp();
            bool previousFrameValid = _hasPreviousViewProjectionMatrix;
            Matrix4x4 previousViewProjection = previousFrameValid
                ? _previousViewProjectionMatrix
                : sceneData.ViewProjectionMatrix;
            float previousTime = previousFrameValid ? _previousTime : sceneData.Time;

            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            Extent2D renderExtent = _renderTargets.MotionVectors.Extent;

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
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.MotionVectorPipeline);

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
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.DontCare,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };

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
            DrawMotionVectorBucket(
                cmd,
                sceneData,
                previousViewProjection,
                previousTime,
                previousFrameValid,
                sceneData.SimpleOpaqueMeshletCount,
                BindlessIndex.MeshletDrawBufferBase);
            DrawMotionVectorBucket(
                cmd,
                sceneData,
                previousViewProjection,
                previousTime,
                previousFrameValid,
                sceneData.SimpleNormalOpaqueMeshletCount,
                BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase);
            DrawMotionVectorBucket(
                cmd,
                sceneData,
                previousViewProjection,
                previousTime,
                previousFrameValid,
                sceneData.FullOpaqueMeshletCount,
                BindlessIndex.FullOpaqueMeshletDrawBufferBase);
            DrawFoliageMotionVectors(cmd, sceneData, previousViewProjection, previousTime, previousFrameValid);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            _renderTargets.MotionVectors.TransitionToShaderRead(cmd);
            _previousViewProjectionMatrix = sceneData.ViewProjectionMatrix;
            _previousTime = sceneData.Time;
            _hasPreviousViewProjectionMatrix = true;
            sceneData.MotionVectorsEnabled = previousFrameValid ? 1 : 0;
            sceneData.CpuMotionVectorRecordMicroseconds = ElapsedMicroseconds(start);
        }

        private void DrawMotionVectorBucket(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Matrix4x4 previousViewProjection,
            float previousTime,
            bool previousFrameValid,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            var pushConstants = new GPUMotionVectorPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                PreviousViewProjectionMatrix = previousViewProjection,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex,
                PreviousFrameValid = previousFrameValid ? 1u : 0u,
                Time = sceneData.Time,
                PreviousTime = previousTime
            };

            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                &pushConstants);

            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
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
                sceneData.FoliageDrawBufferBytes == 0)
            {
                return;
            }

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid || buffers.MeshletDrawCapacity <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _foliagePipeline.AuthoredMotionVectorPipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUMotionVectorPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                PreviousViewProjectionMatrix = previousViewProjection,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = checked((uint)buffers.MeshletDrawCapacity),
                MeshletDrawBufferBaseIndex = (uint)BindlessIndex.FoliageMeshletDrawBufferBase,
                PreviousFrameValid = previousFrameValid ? 1u : 0u,
                Time = sceneData.Time,
                PreviousTime = previousTime
            };

            _context.Api.CmdPushConstants(
                cmd,
                _foliagePipeline.GraphicsLayout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                &pushConstants);

            if (sceneData.FoliageIndirectMeshletDispatchEnabled)
            {
                VkBuffer indirect = _bufferManager.GetBuffer(buffers.IndirectDispatchBuffer);
                _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                    cmd,
                    indirect,
                    0,
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
    }
}
