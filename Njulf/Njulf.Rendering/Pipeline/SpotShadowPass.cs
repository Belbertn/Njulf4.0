using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SpotShadowPass : RenderPassBase
    {
        // ShadowSettings clamps this to 32 and SpotShadowAtlas has the same record capacity.
        private const int CachedLightLabelCapacity = 32;
        private static readonly string[] StaticLightDebugLabels = CreateLightDebugLabels("Static");
        private static readonly string[] DynamicLightDebugLabels = CreateLightDebugLabels("Dynamic");
        private static readonly string[] FoliageLightDebugLabels = CreateLightDebugLabels("Foliage");

        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly FoliagePipeline? _foliagePipeline;
        private readonly FoliageManager? _foliageManager;
        private readonly SpotShadowAtlas _atlas;
        private readonly ShadowSettings _settings;
        private int _spotIndex;
        private SpotShadowAtlasRect _region;

        public SpotShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            SpotShadowAtlas atlas,
            ShadowSettings settings,
            FoliagePipeline? foliagePipeline = null,
            FoliageManager? foliageManager = null)
            : base("SpotShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _foliageManager = foliageManager;
            _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
            sceneData.SpotShadowsEnabled && sceneData.SpotShadowSelectedCount > 0 && _atlas.WorkingImage.Handle != 0;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData)) return;
            sceneData.SpotShadowRecordSkipped = true;
            bool fresh = _atlas.StaticLayout == ImageLayout.Undefined || _atlas.Layout == ImageLayout.Undefined;
            for (_spotIndex = 0; _spotIndex < sceneData.SpotShadowSelectedCount; _spotIndex++)
            {
                _region = sceneData.SpotShadowAllocations[_spotIndex].Region;
                LocalShadowCacheEntry cache = sceneData.SpotShadowCacheEntries[_spotIndex];
                bool staticDirty = fresh || cache.StaticDirty;
                bool foliage = HasFoliageSpotShadowWork(sceneData) && _spotIndex < sceneData.FoliageMaxLocalShadowedSpotLights;
                bool moving = sceneData.LocalDynamicShadowMeshletCount > 0 &&
                    (_spotIndex >= sceneData.SpotShadowDynamicCasters.Length || sceneData.SpotShadowDynamicCasters[_spotIndex]);
                bool dynamic = moving || foliage;
                if (!staticDirty && !dynamic && !cache.HadDynamic)
                { cache.LastResult = "Cached"; sceneData.LocalShadowCacheHitCount++; continue; }
                sceneData.SpotShadowRecordSkipped = false;
                if (staticDirty)
                { RenderStaticCache(cmd, sceneData); sceneData.LocalShadowStaticRefreshCount++; }
                CopyStaticCacheToWorking(cmd);
                sceneData.LocalShadowCopyCount++;
                if (moving) RenderDynamic(cmd, sceneData);
                if (foliage) RenderFoliage(cmd, sceneData);
                if (dynamic) sceneData.LocalShadowDynamicUpdateCount++;
                TransitionWorking(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
                cache.Commit(dynamic);
            }
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void RenderStaticCache(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionStatic(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            ClearAtlas(cmd, _atlas.StaticView);
            if (sceneData.LocalStaticShadowMeshletCount <= 0) return;
            BindShadowPipeline(cmd);
            {
                int i = _spotIndex;
                _context.BeginDebugLabel(cmd, GetLightDebugLabel(StaticLightDebugLabels, "Static", i));
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
            {
                int i = _spotIndex;
                _context.BeginDebugLabel(cmd, GetLightDebugLabel(DynamicLightDebugLabels, "Dynamic", i));
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

        private void RenderFoliage(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null || _foliageManager == null)
                return;

            TransitionWorking(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            int shadowCount = Math.Min(sceneData.SpotShadowSelectedCount, sceneData.FoliageMaxLocalShadowedSpotLights);
            for (int i = _spotIndex; i == _spotIndex && i < shadowCount; i++)
            {
                _context.BeginDebugLabel(cmd, GetLightDebugLabel(FoliageLightDebugLabels, "Foliage", i));
                try
                {
                    RenderFoliageSpot(cmd, sceneData, i);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }
        }

        private static string[] CreateLightDebugLabels(string passKind)
        {
            var labels = new string[CachedLightLabelCapacity];
            for (int lightIndex = 0; lightIndex < labels.Length; lightIndex++)
                labels[lightIndex] = $"SpotShadowPass {passKind} Light {lightIndex}";

            return labels;
        }

        private static string GetLightDebugLabel(string[] labels, string passKind, int lightIndex)
        {
            return (uint)lightIndex < (uint)labels.Length
                ? labels[lightIndex]
                : $"SpotShadowPass {passKind} Light {lightIndex}";
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

            SpotShadowAtlasRect rect = sceneData.SpotShadowAllocations[shadowIndex].Region;
            var viewport = new Viewport
            {
                X = rect.X + 4,
                Y = rect.Y + 4,
                Width = rect.Width - 8,
                Height = rect.Height - 8,
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

        private void RenderFoliageSpot(CommandBuffer cmd, SceneRenderingData sceneData, int shadowIndex)
        {
            if (_foliagePipeline == null || _foliageManager == null)
                return;

            uint clusterDrawCount = checked((uint)Math.Min(
                sceneData.FoliageClusterCount,
                Math.Max(0, sceneData.FoliageLocalShadowClusterBudget)));
            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            uint meshletDrawCount = checked((uint)Math.Min(
                Math.Max(0, buffers.MeshletDrawCapacity),
                Math.Max(0, sceneData.FoliageLocalShadowMeshletDrawBudget)));
            if (clusterDrawCount == 0u && meshletDrawCount == 0u)
                return;

            SpotShadowAtlasRect rect = sceneData.SpotShadowAllocations[shadowIndex].Region;
            var viewport = new Viewport
            {
                X = rect.X + 4,
                Y = rect.Y + 4,
                Width = rect.Width - 8,
                Height = rect.Height - 8,
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
                ImageView = _atlas.WorkingView,
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
            if (clusterDrawCount > 0u)
            {
                BindFoliageShadowPipeline(cmd, _foliagePipeline.ShadowPipeline);
                PushFoliageShadowConstants(
                    cmd,
                    sceneData,
                    sceneData.SpotShadowData[shadowIndex].LightViewProjection,
                    new Vector2(rect.Width, rect.Height),
                    clusterDrawCount,
                    sceneData.FoliageGrassShadowDensityScale);
                _context.ExtMeshShader.CmdDrawMeshTask(cmd, clusterDrawCount, 1, 1);
            }

            if (meshletDrawCount > 0u)
            {
                BindFoliageShadowPipeline(cmd, _foliagePipeline.AuthoredShadowPipeline);
                PushFoliageShadowConstants(
                    cmd,
                    sceneData,
                    sceneData.SpotShadowData[shadowIndex].LightViewProjection,
                    new Vector2(rect.Width, rect.Height),
                    meshletDrawCount,
                    1.0f);
                _context.ExtMeshShader.CmdDrawMeshTask(cmd, meshletDrawCount, 1, 1);
            }

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
                    Offset = new Offset2D { X = (int)_region.X, Y = (int)_region.Y },
                    Extent = new Extent2D { Width = _region.Width, Height = _region.Height }
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

        private void BindFoliageShadowPipeline(CommandBuffer cmd, Silk.NET.Vulkan.Pipeline pipeline)
        {
            _context.Api.CmdSetDepthBias(cmd, _settings.SpotConstantDepthBias, 0.0f, _settings.SpotSlopeScaledDepthBias);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _foliagePipeline!.GraphicsLayout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _foliagePipeline.GraphicsLayout, 1, 1, &textureSet, 0, null);
        }

        private void PushFoliageShadowConstants(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Matrix4x4 viewProjection,
            Vector2 dimensions,
            uint drawCount,
            float shadowDensityScale)
        {
            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = viewProjection,
                CameraPositionTime = new Vector4(
                    sceneData.CameraPosition.X,
                    sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z,
                    sceneData.Time),
                ScreenDimensions = new Vector4(
                    dimensions.X,
                    dimensions.Y,
                    1.0f / Math.Max(1.0f, dimensions.X),
                    1.0f / Math.Max(1.0f, dimensions.Y)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = drawCount,
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = 3u,
                DebugView = sceneData.FoliageDebugView,
                ShadowDensityScale = shadowDensityScale
            };

            _context.Api.CmdPushConstants(
                cmd,
                _foliagePipeline!.GraphicsLayout,
                ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
                &pushConstants);
        }

        private bool HasFoliageSpotShadowWork(SceneRenderingData sceneData)
        {
            return sceneData.FoliageCastShadows &&
                   sceneData.FoliageLocalShadowsEnabled &&
                   sceneData.FoliageMaxLocalShadowedSpotLights > 0 &&
                   sceneData.FoliageClusterCount > 0 &&
                   sceneData.FoliageDrawBufferBytes > 0 &&
                   _foliagePipeline != null &&
                   _foliageManager != null;
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
                SrcOffset = new Offset3D((int)_region.X, (int)_region.Y, 0),
                DstOffset = new Offset3D((int)_region.X, (int)_region.Y, 0),
                Extent = new Extent3D { Width = _region.Width, Height = _region.Height, Depth = 1 }
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
                    srcStage = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
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
                    dstStage = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
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

    }
}
