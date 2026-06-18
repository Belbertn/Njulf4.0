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
        private ulong _lastStaticCacheSignature;
        private bool _hasStaticCacheSignature;

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

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.PointShadowsEnabled ||
                sceneData.PointShadowSelectedCount <= 0 ||
                _cubemapArray.WorkingImage.Handle == 0)
            {
                return false;
            }

            return IsStaticCacheDirty(sceneData) ||
                   sceneData.LocalDynamicShadowMeshletCount > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData))
                return;

            bool staticDirty = IsStaticCacheDirty(sceneData);
            if (staticDirty)
            {
                RenderStaticCache(cmd, sceneData);
                _lastStaticCacheSignature = CreateStaticCacheSignature(sceneData);
                _hasStaticCacheSignature = true;
            }

            if (sceneData.LocalStaticShadowMeshletCount > 0)
                CopyStaticCacheToWorking(cmd);
            else
                ClearWorkingImage(cmd);

            if (sceneData.LocalDynamicShadowMeshletCount > 0)
                RenderDynamic(cmd, sceneData);

            TransitionWorking(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void RenderStaticCache(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (sceneData.LocalStaticShadowMeshletCount <= 0)
                return;

            ClearStaticImage(cmd);
            TransitionStatic(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            RenderFaces(
                cmd,
                sceneData,
                staticViews: true,
                sceneData.LocalStaticShadowMeshletCount,
                BindlessIndex.LocalStaticShadowMeshletDrawBufferBase,
                "Static");
        }

        private void RenderDynamic(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionWorking(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            RenderFaces(
                cmd,
                sceneData,
                staticViews: false,
                sceneData.LocalDynamicShadowMeshletCount,
                BindlessIndex.LocalDynamicShadowMeshletDrawBufferBase,
                "Dynamic");
        }

        private void RenderFaces(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            bool staticViews,
            int meshletCount,
            int meshletDrawBufferBaseIndex,
            string label)
        {
            if (meshletCount <= 0)
                return;

            for (int pointIndex = 0; pointIndex < sceneData.PointShadowSelectedCount; pointIndex++)
            {
                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    if (!IsFaceEnabled(sceneData, pointIndex, faceIndex))
                        continue;

                    _context.BeginDebugLabel(cmd, $"PointShadowPass {label} Light {pointIndex} Face {FaceName(faceIndex)}");
                    try
                    {
                        ImageView view = staticViews
                            ? _cubemapArray.GetStaticFaceView(pointIndex, faceIndex)
                            : _cubemapArray.GetFaceView(pointIndex, faceIndex);
                        RenderFace(
                            cmd,
                            sceneData,
                            pointIndex,
                            faceIndex,
                            view,
                            meshletCount,
                            meshletDrawBufferBaseIndex);
                    }
                    finally
                    {
                        _context.EndDebugLabel(cmd);
                    }
                }
            }
        }

        private void RenderFace(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int pointIndex,
            int faceIndex,
            ImageView imageView,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            var viewport = new Viewport { X = 0, Y = 0, Width = _cubemapArray.MapSize, Height = _cubemapArray.MapSize, MinDepth = 0.0f, MaxDepth = 1.0f };
            var scissor = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = new Extent2D { Width = _cubemapArray.MapSize, Height = _cubemapArray.MapSize } };
            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = imageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
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
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex
            };
            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(cmd, _meshPipeline.Layout, ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt, 0, size, &pushConstants);
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void BindShadowPipeline(CommandBuffer cmd)
        {
            _context.Api.CmdSetDepthBias(cmd, _settings.PointConstantDepthBias, 0.0f, _settings.PointSlopeScaledDepthBias);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ShadowAlphaDepthPipeline);
            BindDescriptors(cmd);
        }

        private void BindDescriptors(CommandBuffer cmd)
        {
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 1, 1, &textureSet, 0, null);
        }

        private static bool IsFaceEnabled(SceneRenderingData sceneData, int pointIndex, int faceIndex)
        {
            if (pointIndex < 0 || pointIndex >= sceneData.PointShadowFaceMasks.Length)
                return true;

            return (sceneData.PointShadowFaceMasks[pointIndex] & (1 << faceIndex)) != 0;
        }

        private void ClearStaticImage(CommandBuffer cmd)
        {
            ClearImage(cmd, _cubemapArray.StaticImage, staticImage: true);
        }

        private void ClearWorkingImage(CommandBuffer cmd)
        {
            ClearImage(cmd, _cubemapArray.WorkingImage, staticImage: false);
        }

        private void ClearImage(CommandBuffer cmd, Image image, bool staticImage)
        {
            if (_cubemapArray.LayerCount <= 0)
                return;

            if (staticImage)
                TransitionStatic(cmd, ImageLayout.TransferDstOptimal);
            else
                TransitionWorking(cmd, ImageLayout.TransferDstOptimal);

            var clearValue = new ClearDepthStencilValue(0.0f, 0);
            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = (uint)_cubemapArray.LayerCount
            };
            _context.Api.CmdClearDepthStencilImage(cmd, image, ImageLayout.TransferDstOptimal, &clearValue, 1, &range);
        }

        private void TransitionStatic(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_cubemapArray.StaticLayout == newLayout)
                return;

            ImageLayout oldLayout = _cubemapArray.StaticLayout;
            _cubemapArray.StaticLayout = newLayout;
            ExecuteTransition(cmd, _cubemapArray.StaticImage, oldLayout, newLayout);
        }

        private void TransitionWorking(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_cubemapArray.Layout == newLayout)
                return;

            ImageLayout oldLayout = _cubemapArray.Layout;
            _cubemapArray.Layout = newLayout;
            ExecuteTransition(cmd, _cubemapArray.WorkingImage, oldLayout, newLayout);
        }

        private void ExecuteTransition(CommandBuffer cmd, Image image, ImageLayout oldLayout, ImageLayout newLayout)
        {
            var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = (uint)Math.Max(1, _cubemapArray.LayerCount) };
            GetTransitionMasks(oldLayout, newLayout, out var srcStage, out var srcAccess, out var dstStage, out var dstAccess);
            var barrier = BarrierBuilder.CreateImageBarrier(
                image,
                srcStage,
                srcAccess,
                dstStage,
                dstAccess,
                oldLayout,
                newLayout,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);
            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private void CopyStaticCacheToWorking(CommandBuffer cmd)
        {
            TransitionStatic(cmd, ImageLayout.TransferSrcOptimal);
            TransitionWorking(cmd, ImageLayout.TransferDstOptimal);

            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)_cubemapArray.LayerCount
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)_cubemapArray.LayerCount
                },
                Extent = new Extent3D { Width = _cubemapArray.MapSize, Height = _cubemapArray.MapSize, Depth = 1 }
            };

            _context.Api.CmdCopyImage(
                cmd,
                _cubemapArray.StaticImage,
                ImageLayout.TransferSrcOptimal,
                _cubemapArray.WorkingImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copy);
        }

        private bool IsStaticCacheDirty(SceneRenderingData sceneData)
        {
            if (_cubemapArray.StaticLayout == ImageLayout.Undefined ||
                _cubemapArray.Layout == ImageLayout.Undefined)
            {
                return true;
            }

            ulong signature = CreateStaticCacheSignature(sceneData);
            return !_hasStaticCacheSignature || _lastStaticCacheSignature != signature;
        }

        private static ulong CreateStaticCacheSignature(SceneRenderingData sceneData)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, sceneData.LocalStaticShadowMeshletCount);
            hash = HashAdd(hash, sceneData.LocalStaticShadowMeshletDrawSignature);
            hash = HashAdd(hash, sceneData.PointShadowSelectedCount);
            hash = HashAdd(hash, sceneData.PointShadowMapSize);
            for (int i = 0; i < sceneData.PointShadowSelectedCount; i++)
            {
                if (i < sceneData.PointShadowFaceMasks.Length)
                    hash = HashAdd(hash, sceneData.PointShadowFaceMasks[i]);

                GPUPointShadow shadow = sceneData.PointShadowData[i];
                GPUPointShadow* shadowPtr = &shadow;
                byte* bytes = (byte*)shadowPtr;
                for (int byteIndex = 0; byteIndex < sizeof(GPUPointShadow); byteIndex++)
                {
                    hash = HashAdd(hash, bytes[byteIndex]);
                }
            }

            return hash;
        }

        private static void GetTransitionMasks(
            ImageLayout oldLayout,
            ImageLayout newLayout,
            out PipelineStageFlags2 srcStage,
            out AccessFlags2 srcAccess,
            out PipelineStageFlags2 dstStage,
            out AccessFlags2 dstAccess)
        {
            switch (oldLayout)
            {
                case ImageLayout.DepthStencilAttachmentOptimal:
                    srcStage = PipelineStageFlags2.LateFragmentTestsBit;
                    srcAccess = AccessFlags2.DepthStencilAttachmentWriteBit;
                    break;
                case ImageLayout.DepthStencilReadOnlyOptimal:
                    srcStage = PipelineStageFlags2.FragmentShaderBit;
                    srcAccess = AccessFlags2.ShaderSampledReadBit;
                    break;
                case ImageLayout.TransferSrcOptimal:
                    srcStage = PipelineStageFlags2.TransferBit;
                    srcAccess = AccessFlags2.TransferReadBit;
                    break;
                case ImageLayout.TransferDstOptimal:
                    srcStage = PipelineStageFlags2.TransferBit;
                    srcAccess = AccessFlags2.TransferWriteBit;
                    break;
                default:
                    srcStage = PipelineStageFlags2.None;
                    srcAccess = AccessFlags2.None;
                    break;
            }

            switch (newLayout)
            {
                case ImageLayout.DepthStencilAttachmentOptimal:
                    dstStage = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
                    dstAccess = AccessFlags2.DepthStencilAttachmentWriteBit;
                    break;
                case ImageLayout.DepthStencilReadOnlyOptimal:
                    dstStage = PipelineStageFlags2.FragmentShaderBit;
                    dstAccess = AccessFlags2.ShaderSampledReadBit;
                    break;
                case ImageLayout.TransferSrcOptimal:
                    dstStage = PipelineStageFlags2.TransferBit;
                    dstAccess = AccessFlags2.TransferReadBit;
                    break;
                case ImageLayout.TransferDstOptimal:
                    dstStage = PipelineStageFlags2.TransferBit;
                    dstAccess = AccessFlags2.TransferWriteBit;
                    break;
                default:
                    dstStage = PipelineStageFlags2.AllCommandsBit;
                    dstAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
                    break;
            }
        }

        private static ulong HashAdd(ulong hash, int value) => HashAdd(hash, unchecked((uint)value));
        private static ulong HashAdd(ulong hash, uint value)
        {
            const ulong prime = 1099511628211UL;
            unchecked
            {
                hash ^= value & 0xFFu;
                hash *= prime;
                hash ^= (value >> 8) & 0xFFu;
                hash *= prime;
                hash ^= (value >> 16) & 0xFFu;
                hash *= prime;
                hash ^= (value >> 24) & 0xFFu;
                return hash * prime;
            }
        }

        private static ulong HashAdd(ulong hash, ulong value)
        {
            hash = HashAdd(hash, (uint)value);
            return HashAdd(hash, (uint)(value >> 32));
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
