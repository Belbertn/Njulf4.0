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
    public sealed unsafe class SpotShadowPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly SpotShadowAtlas _atlas;
        private readonly ShadowSettings _settings;
        private ulong _lastStaticCacheSignature;
        private bool _hasStaticCacheSignature;

        public SpotShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            SpotShadowAtlas atlas,
            ShadowSettings settings)
            : base("SpotShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.SpotShadowsEnabled ||
                sceneData.SpotShadowSelectedCount <= 0 ||
                _atlas.WorkingImage.Handle == 0)
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
                ClearWorkingAtlas(cmd);

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

            TransitionStatic(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            ClearAtlas(cmd, _atlas.StaticView);
            BindShadowPipeline(cmd);
            for (int i = 0; i < sceneData.SpotShadowSelectedCount; i++)
            {
                _context.BeginDebugLabel(cmd, $"SpotShadowPass Static Light {i}");
                try
                {
                    RenderSpot(
                        cmd,
                        sceneData,
                        i,
                        _atlas.StaticView,
                        sceneData.LocalStaticShadowMeshletCount,
                        BindlessIndex.LocalStaticShadowMeshletDrawBufferBase);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }
        }

        private void RenderDynamic(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionWorking(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            for (int i = 0; i < sceneData.SpotShadowSelectedCount; i++)
            {
                _context.BeginDebugLabel(cmd, $"SpotShadowPass Dynamic Light {i}");
                try
                {
                    RenderSpot(
                        cmd,
                        sceneData,
                        i,
                        _atlas.WorkingView,
                        sceneData.LocalDynamicShadowMeshletCount,
                        BindlessIndex.LocalDynamicShadowMeshletDrawBufferBase);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }
        }

        private void RenderSpot(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int shadowIndex,
            ImageView imageView,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            SpotShadowAtlasRect rect = LocalShadowAllocator.GetSpotTileRect(_atlas.AtlasSize, _atlas.TileSize, shadowIndex);
            var viewport = new Viewport
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = (int)rect.X, Y = (int)rect.Y },
                Extent = new Extent2D { Width = rect.Width, Height = rect.Height }
            };
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
                ViewProjectionMatrix = sceneData.SpotShadowData[shadowIndex].LightViewProjection,
                ScreenDimensions = new Vector2(rect.Width, rect.Height),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex
            };
            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(cmd, _meshPipeline.Layout, ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt, 0, size, &pushConstants);
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void ClearWorkingAtlas(CommandBuffer cmd)
        {
            TransitionWorking(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            ClearAtlas(cmd, _atlas.WorkingView);
        }

        private void ClearAtlas(CommandBuffer cmd, ImageView imageView)
        {
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = imageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = new Extent2D { Width = _atlas.AtlasSize, Height = _atlas.AtlasSize }
                },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void BindShadowPipeline(CommandBuffer cmd)
        {
            _context.Api.CmdSetDepthBias(cmd, _settings.SpotConstantDepthBias, 0.0f, _settings.SpotSlopeScaledDepthBias);
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

        private void TransitionStatic(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_atlas.StaticLayout == newLayout)
                return;

            ImageLayout oldLayout = _atlas.StaticLayout;
            _atlas.StaticLayout = newLayout;
            ExecuteTransition(cmd, _atlas.StaticImage, oldLayout, newLayout);
        }

        private void TransitionWorking(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_atlas.Layout == newLayout)
                return;

            ImageLayout oldLayout = _atlas.Layout;
            _atlas.Layout = newLayout;
            ExecuteTransition(cmd, _atlas.WorkingImage, oldLayout, newLayout);
        }

        private void ExecuteTransition(CommandBuffer cmd, Image image, ImageLayout oldLayout, ImageLayout newLayout)
        {
            var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
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
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                Extent = new Extent3D { Width = _atlas.AtlasSize, Height = _atlas.AtlasSize, Depth = 1 }
            };

            _context.Api.CmdCopyImage(
                cmd,
                _atlas.StaticImage,
                ImageLayout.TransferSrcOptimal,
                _atlas.WorkingImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copy);
        }

        private bool IsStaticCacheDirty(SceneRenderingData sceneData)
        {
            if (_atlas.StaticLayout == ImageLayout.Undefined ||
                _atlas.Layout == ImageLayout.Undefined)
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
            hash = HashAdd(hash, sceneData.SpotShadowSelectedCount);
            hash = HashAdd(hash, sceneData.SpotShadowAtlasSize);
            hash = HashAdd(hash, sceneData.SpotShadowTileSize);
            for (int i = 0; i < sceneData.SpotShadowSelectedCount; i++)
            {
                GPUSpotShadow shadow = sceneData.SpotShadowData[i];
                GPUSpotShadow* shadowPtr = &shadow;
                byte* bytes = (byte*)shadowPtr;
                for (int byteIndex = 0; byteIndex < sizeof(GPUSpotShadow); byteIndex++)
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
    }
}
