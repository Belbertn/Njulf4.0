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
    public sealed unsafe class PointShadowPass : RenderPassBase
    {
        // Labels are cached up to the engine-wide light capacity.
        private const int CachedPointLightLabelCapacity = LightManager.MaxLights;
        private const int PointLightFaceCount = 6;
        private static readonly string[] StaticFaceDebugLabels = CreateFaceDebugLabels("Static");
        private static readonly string[] DynamicFaceDebugLabels = CreateFaceDebugLabels("Dynamic");
        private static readonly string[] FoliageFaceDebugLabels = CreateFaceDebugLabels("Foliage");

        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly FoliagePipeline? _foliagePipeline;
        private readonly FoliageManager? _foliageManager;
        private readonly PointShadowPool _pool;
        private PointShadowCubemapArray _cubemapArray = null!;
        private int _pointIndex;
        private readonly ShadowSettings _settings;

        public PointShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            PointShadowPool cubemapArray,
            ShadowSettings settings,
            FoliagePipeline? foliagePipeline = null,
            FoliageManager? foliageManager = null)
            : base("PointShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _foliageManager = foliageManager;
            _pool = cubemapArray ?? throw new ArgumentNullException(nameof(cubemapArray));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
            sceneData.PointShadowsEnabled && sceneData.PointShadowSelectedCount > 0 && _pool.Maps.Length > 0;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData)) return;
            sceneData.PointShadowRecordSkipped = true;
            for (_pointIndex = 0; _pointIndex < sceneData.PointShadowSelectedCount; _pointIndex++)
            {
                _cubemapArray = _pool.Maps[_pointIndex].Images;
                LocalShadowCacheEntry cache = sceneData.PointShadowCacheEntries[_pointIndex];
                bool staticDirty = cache.StaticDirty || _cubemapArray.StaticLayout == ImageLayout.Undefined;
                bool foliage = HasFoliagePointShadowWork(sceneData) && _pointIndex < sceneData.FoliageMaxLocalShadowedPointLights;
                bool moving = sceneData.LocalDynamicShadowMeshletCount > 0 &&
                    (_pointIndex >= sceneData.PointShadowDynamicCasters.Length || sceneData.PointShadowDynamicCasters[_pointIndex]);
                bool dynamic = moving || foliage;
                if (!staticDirty && !dynamic && !cache.HadDynamic)
                { cache.LastResult = "Cached"; sceneData.LocalShadowCacheHitCount++; sceneData.PointShadowSkippedFaceCount += 6; continue; }
                for (int face = 0; face < 6; face++)
                    if (!IsFaceEnabled(sceneData, _pointIndex, face)) sceneData.PointShadowSkippedFaceCount++;
                sceneData.PointShadowRecordSkipped = false;
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
            ClearStaticImage(cmd);
            if (sceneData.LocalStaticShadowMeshletCount <= 0) return;
            TransitionStatic(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            RenderFaces(
                cmd,
                sceneData,
                staticViews: true,
                sceneData.LocalStaticShadowMeshletCount,
                BindlessIndex.LocalStaticShadowMeshletDrawBufferBase,
                StaticFaceDebugLabels,
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
                DynamicFaceDebugLabels,
                "Dynamic");
        }

        private void RenderFaces(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            bool staticViews,
            int meshletCount,
            int meshletDrawBufferBaseIndex,
            string[] debugLabels,
            string label)
        {
            if (meshletCount <= 0)
                return;

            {
                int pointIndex = _pointIndex;
                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    if (!IsFaceEnabled(sceneData, pointIndex, faceIndex))
                        continue;

                    _context.BeginDebugLabel(cmd, GetFaceDebugLabel(debugLabels, label, pointIndex, faceIndex));
                    try
                    {
                        ImageView view = staticViews
                            ? _cubemapArray.GetStaticFaceView(0, faceIndex)
                            : _cubemapArray.GetFaceView(0, faceIndex);
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
            sceneData.PointShadowRenderedFaceCount++;
        }

        private void RenderFoliage(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null || _foliageManager == null)
                return;

            TransitionWorking(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            int shadowCount = Math.Min(sceneData.PointShadowSelectedCount, sceneData.FoliageMaxLocalShadowedPointLights);
            for (int pointIndex = _pointIndex; pointIndex == _pointIndex && pointIndex < shadowCount; pointIndex++)
            {
                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    if (!IsFaceEnabled(sceneData, pointIndex, faceIndex))
                        continue;

                    _context.BeginDebugLabel(cmd, GetFaceDebugLabel(FoliageFaceDebugLabels, "Foliage", pointIndex, faceIndex));
                    try
                    {
                        RenderFoliageFace(
                            cmd,
                            sceneData,
                            pointIndex,
                            faceIndex,
                            _cubemapArray.GetFaceView(0, faceIndex));
                    }
                    finally
                    {
                        _context.EndDebugLabel(cmd);
                    }
                }
            }
        }

        private void RenderFoliageFace(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int pointIndex,
            int faceIndex,
            ImageView imageView)
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

            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _cubemapArray.MapSize,
                Height = _cubemapArray.MapSize,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = new Extent2D { Width = _cubemapArray.MapSize, Height = _cubemapArray.MapSize }
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

            Matrix4x4 viewProjection = GetFaceMatrix(sceneData.PointShadowData[pointIndex], faceIndex);
            var dimensions = new Vector2(_cubemapArray.MapSize, _cubemapArray.MapSize);
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            if (clusterDrawCount > 0u)
            {
                BindFoliageShadowPipeline(cmd, _foliagePipeline.ShadowPipeline);
                PushFoliageShadowConstants(
                    cmd,
                    sceneData,
                    viewProjection,
                    dimensions,
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
                    viewProjection,
                    dimensions,
                    meshletDrawCount,
                    1.0f);
                _context.ExtMeshShader.CmdDrawMeshTask(cmd, meshletDrawCount, 1, 1);
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            sceneData.PointShadowRenderedFaceCount++;
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

        private void BindFoliageShadowPipeline(CommandBuffer cmd, Silk.NET.Vulkan.Pipeline pipeline)
        {
            _context.Api.CmdSetDepthBias(cmd, _settings.PointConstantDepthBias, 0.0f, _settings.PointSlopeScaledDepthBias);
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

        private bool HasFoliagePointShadowWork(SceneRenderingData sceneData)
        {
            return sceneData.FoliageCastShadows &&
                   sceneData.FoliageLocalShadowsEnabled &&
                   sceneData.FoliageMaxLocalShadowedPointLights > 0 &&
                   sceneData.FoliageClusterCount > 0 &&
                   sceneData.FoliageDrawBufferBytes > 0 &&
                   _foliagePipeline != null &&
                   _foliageManager != null;
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

        internal static void GetTransitionMasks(
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
                    srcStage = PipelineStageFlags2.EarlyFragmentTestsBit |
                        PipelineStageFlags2.LateFragmentTestsBit;
                    srcAccess = AccessFlags2.DepthStencilAttachmentReadBit |
                        AccessFlags2.DepthStencilAttachmentWriteBit;
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
                    // Every face composes the cleared/copied cache with loadOp=Load
                    // before writing dynamic depth, so both accesses are required.
                    dstAccess = AccessFlags2.DepthStencilAttachmentReadBit |
                        AccessFlags2.DepthStencilAttachmentWriteBit;
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

        private static string[] CreateFaceDebugLabels(string passKind)
        {
            var labels = new string[CachedPointLightLabelCapacity * PointLightFaceCount];
            for (int pointIndex = 0; pointIndex < CachedPointLightLabelCapacity; pointIndex++)
            {
                for (int faceIndex = 0; faceIndex < PointLightFaceCount; faceIndex++)
                {
                    labels[GetFaceLabelIndex(pointIndex, faceIndex)] =
                        $"PointShadowPass {passKind} Light {pointIndex} Face {FaceName(faceIndex)}";
                }
            }

            return labels;
        }

        private static string GetFaceDebugLabel(string[] labels, string passKind, int pointIndex, int faceIndex)
        {
            if ((uint)pointIndex < CachedPointLightLabelCapacity && (uint)faceIndex < PointLightFaceCount)
                return labels[GetFaceLabelIndex(pointIndex, faceIndex)];

            return $"PointShadowPass {passKind} Light {pointIndex} Face {FaceName(faceIndex)}";
        }

        private static int GetFaceLabelIndex(int pointIndex, int faceIndex)
        {
            return pointIndex * PointLightFaceCount + faceIndex;
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
