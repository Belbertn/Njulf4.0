using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class PointShadowPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly PointShadowCubemapArray _cubemapArray;
        private readonly ShadowSettings _settings;

        public PointShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            PointShadowCubemapArray cubemapArray,
            ShadowSettings settings)
            : base("PointShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _cubemapArray = cubemapArray ?? throw new ArgumentNullException(nameof(cubemapArray));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.PointShadowsEnabled || sceneData.PointShadowSelectedCount <= 0 || sceneData.LocalShadowMeshletCount <= 0)
                return;

            Transition(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            _context.Api.CmdSetDepthBias(cmd, _settings.PointConstantDepthBias, 0.0f, _settings.PointSlopeScaledDepthBias);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ShadowAlphaDepthPipeline);
            BindDescriptors(cmd);

            for (int pointIndex = 0; pointIndex < sceneData.PointShadowSelectedCount; pointIndex++)
            {
                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    _context.BeginDebugLabel(cmd, $"PointShadowPass Light {pointIndex} Face {FaceName(faceIndex)}");
                    try
                    {
                        RenderFace(cmd, sceneData, pointIndex, faceIndex);
                    }
                    finally
                    {
                        _context.EndDebugLabel(cmd);
                    }
                }
            }

            Transition(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void RenderFace(CommandBuffer cmd, SceneRenderingData sceneData, int pointIndex, int faceIndex)
        {
            var viewport = new Viewport { X = 0, Y = 0, Width = _cubemapArray.MapSize, Height = _cubemapArray.MapSize, MinDepth = 0.0f, MaxDepth = 1.0f };
            var scissor = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = new Extent2D { Width = _cubemapArray.MapSize, Height = _cubemapArray.MapSize } };
            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _cubemapArray.GetFaceView(pointIndex, faceIndex),
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = scissor,
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            GPUDepthPushConstants pushConstants = new()
            {
                ViewProjectionMatrix = GetFaceMatrix(sceneData.PointShadowData[pointIndex], faceIndex),
                ScreenDimensions = new Vector2(_cubemapArray.MapSize, _cubemapArray.MapSize),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)sceneData.LocalShadowMeshletCount,
                MeshletDrawBufferBaseIndex = BindlessIndex.LocalShadowMeshletDrawBufferBase
            };
            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(cmd, _meshPipeline.Layout, ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt, 0, size, &pushConstants);
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)sceneData.LocalShadowMeshletCount, 1, 1);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void BindDescriptors(CommandBuffer cmd)
        {
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 1, 1, &textureSet, 0, null);
        }

        private void Transition(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_cubemapArray.Layout == newLayout)
                return;
            ImageLayout oldLayout = _cubemapArray.Layout;
            _cubemapArray.Layout = newLayout;
            var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = (uint)Math.Max(1, _cubemapArray.LayerCount) };
            var barrier = BarrierBuilder.CreateImageBarrier(
                _cubemapArray.Image,
                oldLayout == ImageLayout.DepthStencilAttachmentOptimal ? PipelineStageFlags2.LateFragmentTestsBit : PipelineStageFlags2.None,
                oldLayout == ImageLayout.DepthStencilAttachmentOptimal ? AccessFlags2.DepthStencilAttachmentWriteBit : AccessFlags2.None,
                newLayout == ImageLayout.DepthStencilAttachmentOptimal ? PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit : PipelineStageFlags2.FragmentShaderBit,
                newLayout == ImageLayout.DepthStencilAttachmentOptimal ? AccessFlags2.DepthStencilAttachmentWriteBit : AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                newLayout,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);
            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private static Matrix4x4 GetFaceMatrix(GPUPointShadow shadow, int faceIndex)
        {
            return faceIndex switch
            {
                0 => shadow.FaceViewProjection0,
                1 => shadow.FaceViewProjection1,
                2 => shadow.FaceViewProjection2,
                3 => shadow.FaceViewProjection3,
                4 => shadow.FaceViewProjection4,
                _ => shadow.FaceViewProjection5
            };
        }

        private static string FaceName(int faceIndex)
        {
            return faceIndex switch
            {
                0 => "+X",
                1 => "-X",
                2 => "+Y",
                3 => "-Y",
                4 => "+Z",
                _ => "-Z"
            };
        }
    }
}
