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
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class GlobalSdfPass : RenderPassBase
    {
        private const string ShaderName = "global_sdf_update.comp.spv";
        private const string EntryPoint = "main";

        private readonly RenderSettings _settings;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly GlobalSdfManager _globalSdfManager;
        private readonly MeshSdfManager _meshSdfManager;
        private readonly StagingRing _stagingRing;
        private readonly Func<DdgiFrameLayout> _ddgiFrameLayoutProvider;
        private readonly nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public GlobalSdfPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            AccelerationStructureManager accelerationStructureManager,
            GlobalSdfManager globalSdfManager,
            MeshSdfManager meshSdfManager,
            StagingRing stagingRing,
            Func<DdgiFrameLayout>? ddgiFrameLayoutProvider = null)
            : base("GlobalSdfPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
            _globalSdfManager = globalSdfManager ?? throw new ArgumentNullException(nameof(globalSdfManager));
            _meshSdfManager = meshSdfManager ?? throw new ArgumentNullException(nameof(meshSdfManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _ddgiFrameLayoutProvider = ddgiFrameLayoutProvider ?? (() => DdgiFrameLayout.Empty);
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "Global SDF clipmap brick updates are compute-only and feed later DDGI/SDF consumers.";

        public override void Initialize()
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            string reason = ResolveSkipReason(sceneData);
            if (reason.Length != 0)
            {
                sceneData.GlobalSdfExecuted = 0;
                sceneData.GlobalSdfSkipReason = reason;
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            int activeMeshSdfCount = _meshSdfManager.PrepareInstanceRecords(
                _accelerationStructureManager.LastStaticOpaqueInstances,
                _stagingRing,
                cmd);
            IReadOnlyList<GlobalSdfUpdateJob> jobs = _globalSdfManager.PrepareUpdateJobs(
                sceneData.CameraPosition,
                _settings.GlobalIllumination.SdfClipmapResolution,
                _settings.GlobalIllumination.SdfBrickUpdateBudget,
                _ddgiFrameLayoutProvider());
            _globalSdfManager.UploadCascadeMetadata(_stagingRing, cmd);

            sceneData.GlobalSdfCascadeCount = _globalSdfManager.LastFrameCascadeCount;
            sceneData.GlobalSdfResolution = _globalSdfManager.LastFrameResolution;
            sceneData.GlobalSdfBricksUpdated = _globalSdfManager.LastFrameBricksUpdated;
            sceneData.GlobalSdfTextureBytes = _globalSdfManager.TextureBytes;
            sceneData.GlobalSdfMeshSdfCount = activeMeshSdfCount;
            sceneData.GlobalSdfBackendFirstCascade = _settings.GlobalIllumination.SdfBackendFirstCascade;

            if (jobs.Count == 0)
            {
                sceneData.GlobalSdfExecuted = 0;
                sceneData.GlobalSdfSkipReason = "no-global-sdf-brick-budget";
                return;
            }

            sceneData.GlobalSdfExecuted = 1;
            sceneData.GlobalSdfSkipReason = string.Empty;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            var touchedVolumes = new List<VolumeTexture>(jobs.Count);
            for (int i = 0; i < jobs.Count; i++)
            {
                GlobalSdfUpdateJob job = jobs[i];
                job.Volume.TransitionToStorageReadWrite(cmd);
                if (!touchedVolumes.Contains(job.Volume))
                    touchedVolumes.Add(job.Volume);

                GPUGlobalSdfConstants pushConstants = CreatePushConstants(sceneData, job, activeMeshSdfCount);
                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GPUGlobalSdfConstants>(),
                    &pushConstants);

                _context.Api.CmdDispatch(cmd, checked((uint)job.BrickCount), 1, 1);
            }

            for (int i = 0; i < touchedVolumes.Count; i++)
            {
                touchedVolumes[i].GenerateMipChain(cmd);
            }
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private string ResolveSkipReason(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (_pipeline.Handle == 0)
                return "pipeline-unavailable";
            if (!gi.Enabled)
                return "global-illumination-disabled";
            if (!gi.EffectiveUseDdgi)
                return "ddgi-disabled";
            if (!_accelerationStructureManager.Active)
                return "acceleration-structure-inactive";
            if (sceneData.DdgiProbeVolumeCount <= 0)
                return "no-ddgi-volumes";
            return string.Empty;
        }

        private GPUGlobalSdfConstants CreatePushConstants(SceneRenderingData sceneData, GlobalSdfUpdateJob job, int activeMeshSdfCount)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return new GPUGlobalSdfConstants
            {
                WorldMinAndVoxelSize = new Vector4(job.WorldMin.X, job.WorldMin.Y, job.WorldMin.Z, job.VoxelSize),
                WorldExtentAndInvVoxelSize = new Vector4(job.WorldExtent.X, job.WorldExtent.Y, job.WorldExtent.Z, 1.0f / Math.Max(job.VoxelSize, 0.0001f)),
                CascadeCount = checked((uint)gi.SdfClipmapCascadeCount),
                SdfBackendFirstCascade = checked((uint)gi.SdfBackendFirstCascade),
                FrameIndex = sceneData.CurrentFrameIndex,
                DebugFlags = 0,
                CascadeBufferIndex = uint.MaxValue,
                BrickUpdateBudget = checked((uint)gi.SdfBrickUpdateBudget),
                BricksUpdated = checked((uint)job.BrickCount),
                MeshSdfBufferIndex = BindlessIndex.MeshSdfBuffer,
                MeshSdfCount = checked((uint)activeMeshSdfCount),
                OutputTextureIndex = checked((uint)job.TextureIndex),
                CascadeIndex = checked((uint)job.CascadeIndex),
                Resolution = checked((uint)job.Resolution),
                BricksPerAxis = checked((uint)job.BricksPerAxis),
                BrickStartIndex = checked((uint)job.BrickStartIndex),
                BrickCount = checked((uint)job.BrickCount),
                Padding0 = 0,
                LogicalGridMinX = job.LogicalGridMinCell.X,
                LogicalGridMinY = job.LogicalGridMinCell.Y,
                LogicalGridMinZ = job.LogicalGridMinCell.Z,
                RingOffsetX = job.RingOffset.X,
                RingOffsetY = job.RingOffset.Y,
                RingOffsetZ = job.RingOffset.Z,
                Padding1 = 0,
                Padding2 = 0
            };
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create GlobalSdfPass pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "GlobalSdfPass Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];
            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUGlobalSdfConstants>()
                };

                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)_setLayouts.Length,
                    PSetLayouts = setLayouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create GlobalSdfPass pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "GlobalSdfPass Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, ShaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, ShaderName);

                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };

                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayout,
                    BasePipelineIndex = -1
                };

                Result result = _context.Api.CreateComputePipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create GlobalSdfPass compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "GlobalSdfPass Compute Pipeline");
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }
    }
}
