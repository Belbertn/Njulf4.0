using System;
using System.Collections.Generic;
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
    public sealed unsafe class DirectionalShadowPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
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
            ShadowSettings settings,
            FoliagePipeline? foliagePipeline = null,
            BufferManager? bufferManager = null,
            FoliageManager? foliageManager = null)
            : base("DirectionalShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _shadowResources = shadowResources ?? throw new ArgumentNullException(nameof(shadowResources));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public bool NeedsStaticCacheRefresh(SceneRenderingData sceneData)
        {
            return sceneData.DirectionalShadowPassEnabled &&
                   _shadowResources.HasImage &&
                   sceneData.DirectionalStaticShadowMeshletCount > 0 &&
                   IsStaticCacheDirty(sceneData);
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.DirectionalShadowPassEnabled || !_shadowResources.HasImage)
                return false;

            return IsStaticCacheDirty(sceneData) ||
                   sceneData.DirectionalDynamicShadowMeshletCount > 0 ||
                   HasFoliageShadowWork(sceneData);
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

            if (HasFoliageShadowWork(sceneData))
                RenderWorkingFoliage(cmd, sceneData);

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
                        GetStaticShadowMeshletCount(sceneData, cascade),
                        GetStaticShadowMeshletDrawBufferBaseIndex(sceneData, cascade),
                        AttachmentLoadOp.Clear,
                        GetStaticShadowIndirectDispatchOffset(sceneData, cascade));
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
                        GetDynamicShadowMeshletCount(sceneData, cascade),
                        GetDynamicShadowMeshletDrawBufferBaseIndex(sceneData, cascade),
                        AttachmentLoadOp.Load,
                        GetDynamicShadowIndirectDispatchOffset(sceneData, cascade));
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }
        }

        private void RenderWorkingFoliage(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null)
                return;

            TransitionWorkingMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                _context.BeginDebugLabel(cmd, $"DirectionalShadowPass Foliage Cascade {cascade}");
                try
                {
                    RenderFoliageCascade(
                        cmd,
                        sceneData,
                        cascade,
                        _shadowResources.GetWorkingCascadeView(cascade));
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
            AttachmentLoadOp loadOp,
            ulong? indirectDispatchOffset = null)
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
            {
                if (indirectDispatchOffset.HasValue &&
                    CanUseSceneIndirectDispatch(sceneData, indirectDispatchOffset.Value))
                {
                    VkBuffer indirect = _bufferManager!.GetBuffer(sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
                    _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                        cmd,
                        indirect,
                        indirectDispatchOffset.Value,
                        1,
                        (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
                }
                else
                {
                    _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
                }
            }
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private bool CanUseSceneIndirectDispatch(SceneRenderingData sceneData, ulong indirectDispatchOffset)
        {
            if (_bufferManager == null ||
                !sceneData.SceneSubmissionIndirectMeshletDispatchEnabled ||
                !sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer.IsValid)
            {
                return false;
            }

            ulong requiredBytes = checked(indirectDispatchOffset + (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            return sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize >= requiredBytes;
        }

        private static int GetStaticShadowMeshletCount(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: true, cascade)
                ? Math.Min(
                    sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade],
                    sceneData.SceneSubmissionGpuDirectionalStaticShadowCapacities[cascade])
                : sceneData.DirectionalStaticShadowMeshletCount;
        }

        private static int GetDynamicShadowMeshletCount(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: false, cascade)
                ? Math.Min(
                    sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade],
                    sceneData.SceneSubmissionGpuDirectionalDynamicShadowCapacities[cascade])
                : sceneData.DirectionalDynamicShadowMeshletCount;
        }

        private static int GetStaticShadowMeshletDrawBufferBaseIndex(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: true, cascade)
                ? SceneOpaqueCompactionPass.GetDirectionalStaticShadowCompactedBufferBaseIndex(cascade)
                : BindlessIndex.DirectionalStaticShadowMeshletDrawBufferBase;
        }

        private static int GetDynamicShadowMeshletDrawBufferBaseIndex(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: false, cascade)
                ? SceneOpaqueCompactionPass.GetDirectionalDynamicShadowCompactedBufferBaseIndex(cascade)
                : BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferBase;
        }

        private static ulong? GetStaticShadowIndirectDispatchOffset(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: true, cascade)
                ? SceneOpaqueCompactionPass.GetDirectionalStaticShadowIndirectDispatchOffset(cascade)
                : null;
        }

        private static ulong? GetDynamicShadowIndirectDispatchOffset(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: false, cascade)
                ? SceneOpaqueCompactionPass.GetDirectionalDynamicShadowIndirectDispatchOffset(cascade)
                : null;
        }

        private static bool CanUseSceneCompactedDirectionalShadows(
            SceneRenderingData sceneData,
            bool staticShadow,
            int cascade)
        {
            if (!sceneData.SceneSubmissionGpuCompactionActive ||
                !sceneData.SceneSubmissionGpuShadowCompactionEnabled ||
                sceneData.SceneSubmissionFallbackReason.Length != 0 ||
                (uint)cascade >= ShadowSettings.MaxDirectionalCascades)
            {
                return false;
            }

            return staticShadow
                ? sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade] > 0 &&
                  sceneData.SceneSubmissionGpuDirectionalStaticShadowCapacities[cascade] > 0
                : sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade] > 0 &&
                  sceneData.SceneSubmissionGpuDirectionalDynamicShadowCapacities[cascade] > 0;
        }

        private void RenderFoliageCascade(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int cascade,
            ImageView imageView)
        {
            if (_foliagePipeline == null)
                return;

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
            BindFoliageShadowPipeline(cmd, _foliagePipeline.ShadowPipeline);
            PushFoliageShadowConstants(
                cmd,
                sceneData,
                cascade,
                checked((uint)sceneData.FoliageClusterCount),
                shadowDensityScale: sceneData.FoliageGrassShadowDensityScale);
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, checked((uint)sceneData.FoliageClusterCount), 1, 1);
            sceneData.DepthTaskInvocations += sceneData.FoliageClusterCount;

            DrawAuthoredFoliageShadow(cmd, sceneData, cascade);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void DrawAuthoredFoliageShadow(CommandBuffer cmd, SceneRenderingData sceneData, int cascade)
        {
            if (_foliagePipeline == null || _bufferManager == null || _foliageManager == null)
                return;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid || buffers.MeshletDrawCapacity <= 0)
                return;

            BindFoliageShadowPipeline(cmd, _foliagePipeline.AuthoredShadowPipeline);
            PushFoliageShadowConstants(
                cmd,
                sceneData,
                cascade,
                checked((uint)buffers.MeshletDrawCapacity),
                shadowDensityScale: 1.0f);

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

        private void PushFoliageShadowConstants(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int cascade,
            uint drawCount,
            float shadowDensityScale)
        {
            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = GetCascadeMatrix(sceneData.ShadowData, cascade),
                CameraPositionTime = new Vector4(
                    sceneData.CameraPosition.X,
                    sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z,
                    sceneData.Time),
                ScreenDimensions = new Vector4(
                    _shadowResources.MapSize,
                    _shadowResources.MapSize,
                    1.0f / Math.Max(1u, _shadowResources.MapSize),
                    1.0f / Math.Max(1u, _shadowResources.MapSize)),
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

        private void BindFoliageShadowPipeline(CommandBuffer cmd, Silk.NET.Vulkan.Pipeline pipeline)
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
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _foliagePipeline!.GraphicsLayout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _foliagePipeline.GraphicsLayout, 1, 1, &textureSet, 0, null);
        }

        private bool HasFoliageShadowWork(SceneRenderingData sceneData)
        {
            return sceneData.FoliageCastShadows &&
                   sceneData.FoliageClusterCount > 0 &&
                   sceneData.FoliageDrawBufferBytes > 0 &&
                   _foliagePipeline != null;
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
