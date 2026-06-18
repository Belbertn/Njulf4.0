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
    public sealed unsafe class DirectionalShadowPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly DirectionalShadowResources _shadowResources;
        private readonly ShadowSettings _settings;
        private ulong _lastStaticCacheSignature;
        private bool _hasStaticCacheSignature;

        public DirectionalShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            DirectionalShadowResources shadowResources,
            ShadowSettings settings)
            : base("DirectionalShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _shadowResources = shadowResources ?? throw new ArgumentNullException(nameof(shadowResources));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.DirectionalShadowPassEnabled || !_shadowResources.HasImage)
                return false;

            return IsStaticCacheDirty(sceneData) ||
                   sceneData.DirectionalDynamicShadowMeshletCount > 0;
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

            if (sceneData.DirectionalStaticShadowMeshletCount > 0)
            {
                CopyStaticCacheToWorking(cmd, sceneData);
            }
            else
            {
                ClearWorkingMap(cmd, sceneData);
            }

            if (sceneData.DirectionalDynamicShadowMeshletCount > 0)
                RenderWorkingDynamic(cmd, sceneData);

            TransitionWorkingMap(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        private void RenderStaticCache(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (sceneData.DirectionalStaticShadowMeshletCount <= 0)
                return;

            TransitionStaticMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                _context.BeginDebugLabel(cmd, $"DirectionalShadowPass Static Cascade {cascade}");
                try
                {
                    RenderCascade(
                        cmd,
                        sceneData,
                        cascade,
                        _shadowResources.GetStaticCascadeView(cascade),
                        sceneData.DirectionalStaticShadowMeshletCount,
                        BindlessIndex.DirectionalStaticShadowMeshletDrawBufferBase,
                        AttachmentLoadOp.Clear);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }
        }

        private void RenderWorkingDynamic(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionWorkingMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                _context.BeginDebugLabel(cmd, $"DirectionalShadowPass Dynamic Cascade {cascade}");
                try
                {
                    RenderCascade(
                        cmd,
                        sceneData,
                        cascade,
                        _shadowResources.GetWorkingCascadeView(cascade),
                        sceneData.DirectionalDynamicShadowMeshletCount,
                        BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferBase,
                        AttachmentLoadOp.Load);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void RenderCascade(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int cascade,
            ImageView imageView,
            int meshletCount,
            int meshletDrawBufferBaseIndex,
            AttachmentLoadOp loadOp)
        {
            if (meshletCount <= 0 && loadOp != AttachmentLoadOp.Clear)
                return;

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = imageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = loadOp,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = new Extent2D { Width = _shadowResources.MapSize, Height = _shadowResources.MapSize }
                },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            var pushConstants = new GPUDepthPushConstants
            {
                ViewProjectionMatrix = GetCascadeMatrix(sceneData.ShadowData, cascade),
                ScreenDimensions = new Vector2(_shadowResources.MapSize, _shadowResources.MapSize),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (meshletCount > 0)
                _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void BindShadowPipeline(CommandBuffer cmd)
        {
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _shadowResources.MapSize,
                Height = _shadowResources.MapSize,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = new Extent2D { Width = _shadowResources.MapSize, Height = _shadowResources.MapSize }
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            _context.Api.CmdSetDepthBias(cmd, _settings.ConstantDepthBias, 0.0f, _settings.SlopeScaledDepthBias);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ShadowAlphaDepthPipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 1, 1, &textureSet, 0, null);
        }

        private void ClearWorkingMap(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionWorkingMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                RenderCascade(
                    cmd,
                    sceneData,
                    cascade,
                    _shadowResources.GetWorkingCascadeView(cascade),
                    0,
                    BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferBase,
                    AttachmentLoadOp.Clear);
            }
        }

        private void TransitionStaticMap(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_shadowResources.StaticLayout == newLayout)
                return;

            ImageLayout oldLayout = _shadowResources.StaticLayout;
            _shadowResources.StaticLayout = newLayout;
            ExecuteTransition(cmd, _shadowResources.StaticImage, oldLayout, newLayout);
        }

        private void TransitionWorkingMap(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_shadowResources.Layout == newLayout)
                return;

            ImageLayout oldLayout = _shadowResources.Layout;
            _shadowResources.Layout = newLayout;
            ExecuteTransition(cmd, _shadowResources.WorkingImage, oldLayout, newLayout);
        }

        private void ExecuteTransition(CommandBuffer cmd, Image image, ImageLayout oldLayout, ImageLayout newLayout)
        {
            PipelineStageFlags2 srcStage;
            AccessFlags2 srcAccess;
            PipelineStageFlags2 dstStage;
            AccessFlags2 dstAccess;

            GetTransitionMasks(oldLayout, newLayout, out srcStage, out srcAccess, out dstStage, out dstAccess);

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = (uint)_shadowResources.CascadeCount
            };

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

        private void CopyStaticCacheToWorking(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionStaticMap(cmd, ImageLayout.TransferSrcOptimal);
            TransitionWorkingMap(cmd, ImageLayout.TransferDstOptimal);
            uint layerCount = (uint)Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            if (layerCount == 0)
                return;

            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = layerCount
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = layerCount
                },
                Extent = new Extent3D { Width = _shadowResources.MapSize, Height = _shadowResources.MapSize, Depth = 1 }
            };

            _context.Api.CmdCopyImage(
                cmd,
                _shadowResources.StaticImage,
                ImageLayout.TransferSrcOptimal,
                _shadowResources.WorkingImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copy);
        }

        private bool IsStaticCacheDirty(SceneRenderingData sceneData)
        {
            if (_shadowResources.StaticLayout == ImageLayout.Undefined ||
                _shadowResources.Layout == ImageLayout.Undefined)
            {
                return true;
            }

            ulong signature = CreateStaticCacheSignature(sceneData);
            return !_hasStaticCacheSignature || _lastStaticCacheSignature != signature;
        }

        private static ulong CreateStaticCacheSignature(SceneRenderingData sceneData)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, sceneData.DirectionalStaticShadowMeshletCount);
            hash = HashAdd(hash, sceneData.DirectionalStaticShadowMeshletDrawSignature);
            hash = HashAdd(hash, sceneData.DirectionalShadowMapSize);
            hash = HashAdd(hash, sceneData.DirectionalShadowCascadeCount);
            GPUShadowData shadowData = sceneData.ShadowData;
            GPUShadowData* shadowDataPtr = &shadowData;
            byte* bytes = (byte*)shadowDataPtr;
            for (int i = 0; i < sizeof(GPUShadowData); i++)
            {
                hash = HashAdd(hash, bytes[i]);
            }

            return hash;
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

        private static Matrix4x4 GetCascadeMatrix(GPUShadowData data, int cascade)
        {
            return cascade switch
            {
                0 => data.LightViewProjection0,
                1 => data.LightViewProjection1,
                2 => data.LightViewProjection2,
                _ => data.LightViewProjection3
            };
        }
    }
}
