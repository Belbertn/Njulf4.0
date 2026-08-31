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
        private static readonly string[] StaticCascadeDebugLabels = CreateCascadeDebugLabels("Static");
        private static readonly string[] DynamicCascadeDebugLabels = CreateCascadeDebugLabels("Dynamic");
        private static readonly string[] FoliageCascadeDebugLabels = CreateCascadeDebugLabels("Foliage");

        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly DirectionalShadowResources _shadowResources;
        private readonly ShadowSettings _settings;
        private readonly DirectionalShadowCacheStateTracker _staticCacheState = new();
        // This is renderer-thread-owned and copied into the immutable runtime
        // snapshot. Reusing it avoids an otherwise steady per-frame allocation
        // while retaining one provenance item for every supported cascade.
        private readonly DirectionalShadowCacheLayerProvenance[] _cacheLayerProvenance =
            new DirectionalShadowCacheLayerProvenance[ShadowSettings.MaxDirectionalCascades];
        private readonly ulong[] _requiredCacheSignatures =
            new ulong[ShadowSettings.MaxDirectionalCascades];
        private ulong _shadowSubmissionSerial;
        private bool _workingMapHadTransientCasters;

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
            return GetStaticCacheRefreshMask(sceneData) != 0u;
        }

        public uint GetStaticCacheRefreshMask(SceneRenderingData sceneData)
        {
            if (!sceneData.DirectionalShadowPassEnabled ||
                !_shadowResources.HasImage ||
                !sceneData.DirectionalShadowFramePlan.UsesCascadedShadowMap)
                return 0u;

            uint activeMask = GetStaticCacheActiveMask(sceneData);
            CreateStaticCacheSignatures(sceneData, _settings, _requiredCacheSignatures);
            return _staticCacheState.GetDirtyMask(
                activeMask,
                _requiredCacheSignatures,
                _shadowResources.ResourceGeneration,
                _shadowResources.StaticLayout != ImageLayout.Undefined &&
                _shadowResources.Layout != ImageLayout.Undefined,
                _settings.ForceStaticCascadeCacheRefresh);
        }

        /// <summary>
        /// Makes a static-cache refresh eligible for reuse by a later graphics
        /// submission. The renderer calls this only after the command buffer
        /// containing the refresh has been accepted by the graphics queue.
        /// Queue ordering then establishes the dependency for the next frame.
        /// </summary>
        public void ConfirmCurrentFrameSubmission()
        {
            _staticCacheState.ConfirmRecordedRefreshSubmission();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.DirectionalShadowPassEnabled ||
                !_shadowResources.HasImage ||
                !sceneData.DirectionalShadowFramePlan.UsesCascadedShadowMap)
            {
                // A skipped full-ray frame does not rewrite the working map.
                // Preserve this bit so the next CSM frame can remove stale
                // dynamic/foliage depth by recomposing from the static cache.
                UpdateStaticCacheDiagnostics(sceneData, 0u, 0u, 0u);
                return false;
            }

            uint activeMask = GetStaticCacheActiveMask(sceneData);
            uint dirtyMask = GetStaticCacheRefreshMask(sceneData);
            uint reuseMask = _staticCacheState.GetReusableMask(activeMask) & ~dirtyMask;
            UpdateStaticCacheDiagnostics(sceneData, activeMask, 0u, reuseMask);

            bool hasTransientCasters =
                sceneData.DirectionalDynamicShadowMeshletCount > 0 ||
                HasFoliageShadowWork(sceneData);
            return dirtyMask != 0u || hasTransientCasters || _workingMapHadTransientCasters;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData))
                return;

            uint activeMask = GetStaticCacheActiveMask(sceneData);
            CreateStaticCacheSignatures(sceneData, _settings, _requiredCacheSignatures);
            uint dirtyMask = _staticCacheState.GetDirtyMask(
                activeMask,
                _requiredCacheSignatures,
                _shadowResources.ResourceGeneration,
                _shadowResources.StaticLayout != ImageLayout.Undefined &&
                _shadowResources.Layout != ImageLayout.Undefined,
                _settings.ForceStaticCascadeCacheRefresh);
            uint refreshMask = 0u;
            if (dirtyMask != 0u)
            {
                // Do not treat the previous contents as usable while a refresh is in
                // progress. Dirty layers are cleared and rendered independently;
                // signatures for unaffected layers remain reusable.
                _staticCacheState.BeginRefresh(dirtyMask);
                refreshMask = RenderStaticCache(cmd, sceneData, dirtyMask);
                _staticCacheState.RecordRefresh(
                    refreshMask,
                    _requiredCacheSignatures,
                    _shadowResources.ResourceGeneration);
            }
            uint reuseMask = _staticCacheState.GetReusableMask(activeMask) & ~refreshMask;

            // A layer refreshed earlier in this command buffer can safely be
            // copied into its working counterpart, but is deliberately not
            // reusable by a later frame until the graphics submission has
            // been accepted.
            uint currentSubmissionCopyMask = _staticCacheState.GetCurrentSubmissionCopyMask(activeMask);
            if (currentSubmissionCopyMask != 0u)
                CopyStaticCacheToWorking(cmd, sceneData, currentSubmissionCopyMask);

            uint explicitClearMask = activeMask & ~currentSubmissionCopyMask;
            if (explicitClearMask != 0u)
                ClearWorkingLayers(cmd, sceneData, explicitClearMask);

            if (sceneData.DirectionalDynamicShadowMeshletCount > 0)
                RenderWorkingDynamic(cmd, sceneData);

            if (HasFoliageShadowWork(sceneData))
                RenderWorkingFoliage(cmd, sceneData);

            TransitionWorkingMap(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
            UpdateStaticCacheDiagnostics(
                sceneData,
                activeMask,
                refreshMask,
                reuseMask,
                workingMapExplicitClearMask: explicitClearMask,
                dynamicWorkAppended: sceneData.DirectionalDynamicShadowMeshletCount > 0,
                foliageWorkAppended: HasFoliageShadowWork(sceneData),
                commandsRecorded: true);
            _workingMapHadTransientCasters =
                sceneData.DirectionalDynamicShadowMeshletCount > 0 ||
                HasFoliageShadowWork(sceneData);
        }

        private uint RenderStaticCache(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            uint refreshMask)
        {
            TransitionStaticMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            uint renderedMask = 0u;
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                uint bit = 1u << cascade;
                if ((refreshMask & bit) == 0u)
                    continue;

                _context.BeginDebugLabel(cmd, StaticCascadeDebugLabels[cascade]);
                try
                {
                    RenderCascade(
                        cmd,
                        sceneData,
                        cascade,
                        _shadowResources.GetStaticCascadeView(cascade),
                        GetStaticShadowMeshletCount(sceneData, cascade),
                        GetStaticShadowDoubleSidedBase(sceneData, cascade),
                        GetStaticShadowDoubleSidedCapacity(sceneData, cascade),
                        GetStaticShadowMeshletDrawBufferBaseIndex(sceneData, cascade),
                        AttachmentLoadOp.Clear,
                        GetStaticShadowIndirectDispatchOffset(sceneData, cascade),
                        GetStaticShadowDoubleSidedIndirectDispatchOffset(
                            sceneData,
                            cascade));
                    renderedMask |= bit;
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }

            return renderedMask;
        }

        private void RenderWorkingDynamic(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            TransitionWorkingMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                _context.BeginDebugLabel(cmd, DynamicCascadeDebugLabels[cascade]);
                try
                {
                    RenderCascade(
                        cmd,
                        sceneData,
                        cascade,
                        _shadowResources.GetWorkingCascadeView(cascade),
                        GetDynamicShadowMeshletCount(sceneData, cascade),
                        GetDynamicShadowDoubleSidedBase(sceneData, cascade),
                        GetDynamicShadowDoubleSidedCapacity(sceneData, cascade),
                        GetDynamicShadowMeshletDrawBufferBaseIndex(sceneData, cascade),
                        AttachmentLoadOp.Load,
                        GetDynamicShadowIndirectDispatchOffset(sceneData, cascade),
                        GetDynamicShadowDoubleSidedIndirectDispatchOffset(
                            sceneData,
                            cascade));
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
                _context.BeginDebugLabel(cmd, FoliageCascadeDebugLabels[cascade]);
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

        private static string[] CreateCascadeDebugLabels(string passKind)
        {
            var labels = new string[ShadowSettings.MaxDirectionalCascades];
            for (int cascade = 0; cascade < labels.Length; cascade++)
                labels[cascade] = $"DirectionalShadowPass {passKind} Cascade {cascade}";

            return labels;
        }

        private void RenderCascade(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int cascade,
            ImageView imageView,
            int meshletCount,
            int doubleSidedFirstDraw,
            int doubleSidedMeshletCount,
            int meshletDrawBufferBaseIndex,
            AttachmentLoadOp loadOp,
            ulong? indirectDispatchOffset = null,
            ulong? doubleSidedIndirectDispatchOffset = null)
        {
            if (meshletCount <= 0 &&
                doubleSidedMeshletCount <= 0 &&
                loadOp != AttachmentLoadOp.Clear)
                return;

            bool useCompactedIndirect =
                (meshletCount > 0 || doubleSidedMeshletCount > 0) &&
                indirectDispatchOffset.HasValue &&
                CanUseSceneIndirectDispatch(
                    sceneData,
                    indirectDispatchOffset.GetValueOrDefault());
            bool useSidedStreams = useCompactedIndirect &&
                sceneData.SceneSubmissionSidedRasterSpecializationActive &&
                doubleSidedIndirectDispatchOffset.HasValue &&
                CanUseSceneIndirectDispatch(
                    sceneData,
                    doubleSidedIndirectDispatchOffset.GetValueOrDefault());
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                useCompactedIndirect
                    ? _meshPipeline.CompactedShadowAlphaDepthPipeline
                    : _meshPipeline.ShadowAlphaDepthPipeline);
            if (useCompactedIndirect)
            {
                _context.Api.CmdSetCullMode(
                    cmd,
                    useSidedStreams
                        ? CullModeFlags.BackBit
                        : CullModeFlags.None);
                _context.Api.CmdSetDepthCompareOp(
                    cmd,
                    CompareOp.GreaterOrEqual);
            }

            SetReverseDepthBias(cmd, sceneData, cascade);

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
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex,
                FirstDraw = 0u
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (meshletCount > 0 || doubleSidedMeshletCount > 0)
            {
                if (useCompactedIndirect)
                {
                    VkBuffer indirect = _bufferManager!.GetBuffer(sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
                    if (meshletCount > 0)
                    {
                        sceneData.DirectionalShadowMeshOnlyIndirectDrawCount++;
                        _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                            cmd,
                            indirect,
                            indirectDispatchOffset.GetValueOrDefault(),
                            1,
                            (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
                    }
                    if (useSidedStreams)
                    {
                        if (doubleSidedMeshletCount > 0)
                        {
                            _context.Api.CmdSetCullMode(
                                cmd,
                                CullModeFlags.None);
                            pushConstants.MeshletDrawCount = checked(
                                (uint)doubleSidedMeshletCount);
                            pushConstants.FirstDraw = checked(
                                (uint)doubleSidedFirstDraw);
                            _context.Api.CmdPushConstants(
                                cmd,
                                _meshPipeline.Layout,
                                ShaderStageFlags.MeshBitExt |
                                ShaderStageFlags.FragmentBit |
                                ShaderStageFlags.TaskBitExt,
                                0,
                                size,
                                &pushConstants);
                            sceneData.DirectionalShadowMeshOnlyIndirectDrawCount++;
                            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                                cmd,
                                indirect,
                                doubleSidedIndirectDispatchOffset
                                    .GetValueOrDefault(),
                                1,
                                (uint)Marshal.SizeOf<
                                    DrawMeshTasksIndirectCommandEXT>());
                        }
                    }
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
            if (!_meshPipeline.TasklessSubmissionEnabled ||
                _bufferManager == null ||
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
                ? SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                    sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade],
                    sceneData.SceneSubmissionGpuDirectionalStaticShadowCapacities[cascade],
                    sceneData.SceneSubmissionSidedRasterSpecializationActive)
                : sceneData.DirectionalStaticShadowMeshletCount;
        }

        private static int GetDynamicShadowMeshletCount(SceneRenderingData sceneData, int cascade)
        {
            return CanUseSceneCompactedDirectionalShadows(sceneData, staticShadow: false, cascade)
                ? SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                    sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade],
                    sceneData.SceneSubmissionGpuDirectionalDynamicShadowCapacities[cascade],
                    sceneData.SceneSubmissionSidedRasterSpecializationActive)
                : sceneData.DirectionalDynamicShadowMeshletCount;
        }

        private static int GetStaticShadowDoubleSidedBase(
            SceneRenderingData sceneData,
            int cascade) =>
            CanUseSceneCompactedDirectionalShadows(
                sceneData,
                staticShadow: true,
                cascade)
                ? sceneData
                    .SceneSubmissionGpuDirectionalStaticShadowDoubleSidedBases[
                        cascade]
                : 0;

        private static int GetStaticShadowDoubleSidedCapacity(
            SceneRenderingData sceneData,
            int cascade) =>
            CanUseSceneCompactedDirectionalShadows(
                sceneData,
                staticShadow: true,
                cascade)
                ? sceneData
                    .SceneSubmissionGpuDirectionalStaticShadowDoubleSidedCapacities[
                        cascade]
                : 0;

        private static int GetDynamicShadowDoubleSidedBase(
            SceneRenderingData sceneData,
            int cascade) =>
            CanUseSceneCompactedDirectionalShadows(
                sceneData,
                staticShadow: false,
                cascade)
                ? sceneData
                    .SceneSubmissionGpuDirectionalDynamicShadowDoubleSidedBases[
                        cascade]
                : 0;

        private static int GetDynamicShadowDoubleSidedCapacity(
            SceneRenderingData sceneData,
            int cascade) =>
            CanUseSceneCompactedDirectionalShadows(
                sceneData,
                staticShadow: false,
                cascade)
                ? sceneData
                    .SceneSubmissionGpuDirectionalDynamicShadowDoubleSidedCapacities[
                        cascade]
                : 0;

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

        private static ulong? GetStaticShadowDoubleSidedIndirectDispatchOffset(
            SceneRenderingData sceneData,
            int cascade) =>
            CanUseSceneCompactedDirectionalShadows(
                sceneData,
                staticShadow: true,
                cascade) &&
            sceneData.SceneSubmissionSidedRasterSpecializationActive
                ? SceneOpaqueCompactionPass
                    .GetDirectionalStaticShadowDoubleSidedIndirectDispatchOffset(
                        cascade)
                : null;

        private static ulong? GetDynamicShadowDoubleSidedIndirectDispatchOffset(
            SceneRenderingData sceneData,
            int cascade) =>
            CanUseSceneCompactedDirectionalShadows(
                sceneData,
                staticShadow: false,
                cascade) &&
            sceneData.SceneSubmissionSidedRasterSpecializationActive
                ? SceneOpaqueCompactionPass
                    .GetDirectionalDynamicShadowDoubleSidedIndirectDispatchOffset(
                        cascade)
                : null;

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

            bool hasCapacity = staticShadow
                ? sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade] > 0 &&
                  (sceneData.SceneSubmissionGpuDirectionalStaticShadowCapacities[cascade] > 0 ||
                   sceneData.SceneSubmissionGpuDirectionalStaticShadowDoubleSidedCapacities[cascade] > 0)
                : sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade] > 0 &&
                  (sceneData.SceneSubmissionGpuDirectionalDynamicShadowCapacities[cascade] > 0 ||
                   sceneData.SceneSubmissionGpuDirectionalDynamicShadowDoubleSidedCapacities[cascade] > 0);
            if (!hasCapacity ||
                !sceneData.SceneSubmissionSidedRasterSpecializationActive)
            {
                return hasCapacity;
            }

            ulong doubleSidedOffset = staticShadow
                ? SceneOpaqueCompactionPass
                    .GetDirectionalStaticShadowDoubleSidedIndirectDispatchOffset(
                        cascade)
                : SceneOpaqueCompactionPass
                    .GetDirectionalDynamicShadowDoubleSidedIndirectDispatchOffset(
                        cascade);
            ulong requiredBytes = checked(
                doubleSidedOffset +
                (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            return sceneData.SceneSubmissionIndirectMeshletDispatchEnabled &&
                   sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer
                       .IsValid &&
                   sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize >=
                   requiredBytes;
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
            SetReverseDepthBias(cmd, sceneData, cascade);
            PushFoliageShadowConstants(
                cmd,
                sceneData,
                cascade,
                checked((uint)sceneData.FoliageClusterCount),
                shadowDensityScale: sceneData.FoliageGrassShadowDensityScale);
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, checked((uint)sceneData.FoliageClusterCount), 1, 1);

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
                    FoliageManager.AuthoredIndirectDispatchOffset,
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
                ShadowDensityScale = shadowDensityScale,
                // Padding1 carries the directional cascade only for the
                // diagnostic foliage shader variant. It remains ignored by
                // the production foliage shaders and preserves the ABI.
                Padding1 = checked((uint)cascade)
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
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _foliagePipeline!.GraphicsLayout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _foliagePipeline.GraphicsLayout, 1, 1, &textureSet, 0, null);
        }

        private void SetReverseDepthBias(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int cascade)
        {
            // Directional shadow maps use reverse-Z (clear 0, GreaterOrEqual). Vulkan adds
            // depth bias to the generated depth value, so a positive bias moves casters
            // toward the light and makes detailed surfaces shadow themselves. Move the
            // stored caster depth away from the light instead.
            float constantBias = _settings.ConstantDepthBias;
            float slopeBias = _settings.SlopeScaledDepthBias;
            if (_settings.DirectionalBiasMode == DirectionalShadowBiasMode.WorldTexelScaled &&
                (uint)cascade < (uint)sceneData.DirectionalShadowCascadeFitDiagnostics.Length)
            {
                float referenceTexel = sceneData.DirectionalShadowCascadeFitDiagnostics[0].WorldTexelSize;
                float cascadeTexel = sceneData.DirectionalShadowCascadeFitDiagnostics[cascade].WorldTexelSize;
                if (float.IsFinite(referenceTexel) && float.IsFinite(cascadeTexel) &&
                    referenceTexel > 0.000001f && cascadeTexel > 0f)
                {
                    // Keep existing authored bias values as the near-cascade
                    // reference, then scale them in proportion to world texel
                    // footprint. The bound prevents far cascades from detaching
                    // shadows when split ratios are unusually aggressive.
                    float worldTexelScale = Math.Clamp(cascadeTexel / referenceTexel, 0.25f, 4f);
                    constantBias = MathF.Min(_settings.ConstantDepthBias * worldTexelScale, 0.1f);
                    slopeBias = MathF.Min(_settings.SlopeScaledDepthBias * worldTexelScale, 16f);
                }
            }

            _context.Api.CmdSetDepthBias(
                cmd,
                -constantBias,
                0.0f,
                -slopeBias);
        }

        private bool HasFoliageShadowWork(SceneRenderingData sceneData)
        {
            return sceneData.FoliageCastShadows &&
                   sceneData.FoliageClusterCount > 0 &&
                   sceneData.FoliageDrawBufferBytes > 0 &&
                   _foliagePipeline != null;
        }

        private void ClearWorkingLayers(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            uint clearMask)
        {
            TransitionWorkingMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            BindShadowPipeline(cmd);
            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                if ((clearMask & (1u << cascade)) == 0u)
                    continue;
                RenderCascade(
                    cmd,
                    sceneData,
                    cascade,
                    _shadowResources.GetWorkingCascadeView(cascade),
                    0,
                    0,
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

        private void CopyStaticCacheToWorking(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            uint copyMask)
        {
            TransitionStaticMap(cmd, ImageLayout.TransferSrcOptimal);
            TransitionWorkingMap(cmd, ImageLayout.TransferDstOptimal);
            int layerCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            if (layerCount <= 0 || copyMask == 0u)
                return;

            for (int cascade = 0; cascade < layerCount; cascade++)
            {
                if ((copyMask & (1u << cascade)) == 0u)
                    continue;

                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        MipLevel = 0,
                        BaseArrayLayer = checked((uint)cascade),
                        LayerCount = 1
                    },
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        MipLevel = 0,
                        BaseArrayLayer = checked((uint)cascade),
                        LayerCount = 1
                    },
                    Extent = new Extent3D
                    {
                        Width = _shadowResources.MapSize,
                        Height = _shadowResources.MapSize,
                        Depth = 1
                    }
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
        }

        private uint GetStaticCacheActiveMask(SceneRenderingData sceneData)
        {
            // Every allocated cascade has a cache layer even when the current
            // static stream is empty.  A reverse-Z clear recorded under the
            // current signature is valid cache content and must be reusable.
            int cascadeCount = Math.Min(
                Math.Max(sceneData.DirectionalShadowCascadeCount, 0),
                _shadowResources.CascadeCount);
            return GetCascadeMask(cascadeCount);
        }

        private static uint GetCascadeMask(int cascadeCount)
        {
            cascadeCount = Math.Min(Math.Max(cascadeCount, 0), ShadowSettings.MaxDirectionalCascades);
            return cascadeCount == 0 ? 0u : (1u << cascadeCount) - 1u;
        }

        private void UpdateStaticCacheDiagnostics(
            SceneRenderingData sceneData,
            uint activeMask,
            uint refreshMask,
            uint reuseMask,
            uint workingMapExplicitClearMask = 0u,
            bool dynamicWorkAppended = false,
            bool foliageWorkAppended = false,
            bool commandsRecorded = false)
        {
            uint validMask = _staticCacheState.ValidMask & activeMask;
            sceneData.DirectionalShadowStaticCacheActiveMask = unchecked((int)activeMask);
            sceneData.DirectionalShadowStaticCacheValidMask = unchecked((int)validMask);
            sceneData.DirectionalShadowStaticCacheRefreshMask = unchecked((int)refreshMask);
            sceneData.DirectionalShadowStaticCacheReuseMask = unchecked((int)reuseMask);
            ulong submissionSerial = commandsRecorded && activeMask != 0u
                ? unchecked(++_shadowSubmissionSerial)
                : _shadowSubmissionSerial;
            DirectionalShadowCacheLayerProvenance[] provenance = _cacheLayerProvenance;
            for (int cascade = 0; cascade < provenance.Length; cascade++)
            {
                uint bit = 1u << cascade;
                bool active = (activeMask & bit) != 0u;
                bool refreshed = (refreshMask & bit) != 0u;
                bool explicitlyCleared = (workingMapExplicitClearMask & bit) != 0u;
                bool copied = (refreshMask & bit) != 0u || (reuseMask & bit) != 0u;
                provenance[cascade] = active
                    ? new DirectionalShadowCacheLayerProvenance(
                        CascadeIndex: cascade,
                        Active: 1,
                        CacheSignature: _staticCacheState.GetSignature(cascade),
                        ResourceGeneration: _shadowResources.ResourceGeneration,
                        CacheState: _staticCacheState.GetLayerState(cascade, activeMask, refreshMask),
                        CopiedFromCache: copied ? 1 : 0,
                        RefreshedThisFrame: refreshed ? 1 : 0,
                        ExplicitlyCleared: refreshed || explicitlyCleared ? 1 : 0,
                        DynamicWorkAppended: dynamicWorkAppended ? 1 : 0,
                        FoliageWorkAppended: foliageWorkAppended ? 1 : 0,
                        FinalWorkingLayerValid: commandsRecorded
                            ? 1
                            : ((validMask & bit) != 0u ? 1 : 0),
                        SubmissionSerial: submissionSerial)
                    : DirectionalShadowCacheLayerProvenance.Invalid(cascade);
            }

            sceneData.DirectionalShadowCacheLayerProvenance = provenance;
        }

        private static void CreateStaticCacheSignatures(
            SceneRenderingData sceneData,
            ShadowSettings settings,
            Span<ulong> signatures)
        {
            if (signatures.Length < ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentException("One output signature per cascade is required.", nameof(signatures));

            for (int cascade = 0; cascade < ShadowSettings.MaxDirectionalCascades; cascade++)
                signatures[cascade] = CreateStaticCacheSignature(sceneData, settings, cascade);
        }

        internal static ulong CreateStaticCacheSignature(SceneRenderingData sceneData, ShadowSettings settings)
            => CreateStaticCacheSignature(sceneData, settings, cascade: 0);

        internal static ulong CreateStaticCacheSignature(
            SceneRenderingData sceneData,
            ShadowSettings settings,
            int cascade)
        {
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (cascade < 0 || cascade >= ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentOutOfRangeException(nameof(cascade));

            ulong hash = 14695981039346656037UL;
            // The draw-command IDs do not include instance transforms or material
            // payload revisions. SceneContentRevision closes that gap so a static
            // caster edit cannot leave old depth cached until the camera moves.
            hash = HashAdd(hash, sceneData.SceneContentRevision);
            hash = HashAdd(hash, sceneData.DirectionalStaticShadowMeshletCount);
            hash = HashAdd(hash, sceneData.DirectionalStaticShadowMeshletDrawSignature);
            hash = HashAdd(hash, sceneData.DirectionalShadowMapSize);
            hash = HashAdd(hash, sceneData.DirectionalShadowCascadeCount);
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(settings.ConstantDepthBias));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(settings.SlopeScaledDepthBias));
            hash = HashAdd(hash, (uint)settings.DirectionalBiasMode);
            hash = HashAdd(hash, sceneData.SceneSubmissionGpuCompactionEnabled ? 1u : 0u);
            hash = HashAdd(hash, sceneData.SceneSubmissionGpuLodSelectionEnabled ? 1u : 0u);
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(sceneData.SceneSubmissionGpuLod1DistanceRatio));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(sceneData.SceneSubmissionGpuLod2DistanceRatio));
            hash = HashAdd(hash, sceneData.SceneSubmissionGpuShadowCompactionEnabled ? 1u : 0u);
            hash = HashAdd(hash, sceneData.SceneSubmissionGpuShadowLodBias);

            // Only this cascade's matrix participates. A near-cascade snap no
            // longer invalidates stable far-cache layers.
            Matrix4x4 matrix = GetCascadeMatrix(sceneData.ShadowData, cascade);
            Matrix4x4* matrixPtr = &matrix;
            byte* bytes = (byte*)matrixPtr;
            for (int i = 0; i < sizeof(Matrix4x4); i++)
            {
                hash = HashAdd(hash, bytes[i]);
            }

            if (settings.DirectionalBiasMode == DirectionalShadowBiasMode.WorldTexelScaled)
            {
                DirectionalShadowCascadeFitDiagnostics[] fit =
                    sceneData.DirectionalShadowCascadeFitDiagnostics;
                float referenceTexel = fit.Length > 0 ? fit[0].WorldTexelSize : 0f;
                float cascadeTexel = cascade < fit.Length ? fit[cascade].WorldTexelSize : 0f;
                hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(referenceTexel));
                hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(cascadeTexel));
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
                    // Depth tests and writes may occur in either the early or late
                    // fragment-test stage. Include reads as well because loadOp=Load
                    // and depth testing consume the attachment before a transition.
                    srcStage = PipelineStageFlags2.EarlyFragmentTestsBit |
                        PipelineStageFlags2.LateFragmentTestsBit;
                    srcAccess = AccessFlags2.DepthStencilAttachmentReadBit |
                        AccessFlags2.DepthStencilAttachmentWriteBit;
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
                    // Static cache copies are composed with loadOp=Load before dynamic
                    // casters are drawn, so the copied depth must be visible to both
                    // attachment reads and writes.
                    dstAccess = AccessFlags2.DepthStencilAttachmentReadBit |
                        AccessFlags2.DepthStencilAttachmentWriteBit;
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
