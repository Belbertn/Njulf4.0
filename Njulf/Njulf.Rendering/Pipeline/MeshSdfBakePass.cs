using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class MeshSdfBakePass : RenderPassBase
    {
        private const string ShaderName = "mesh_sdf_bake.comp.spv";
        private const string EntryPoint = "main";

        private readonly RenderSettings _settings;
        private readonly MeshSdfManager _meshSdfManager;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public MeshSdfBakePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            MeshSdfManager meshSdfManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("MeshSdfBakePass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _meshSdfManager = meshSdfManager ?? throw new ArgumentNullException(nameof(meshSdfManager));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "Mesh SDF baking is compute-only and produces SDF textures consumed by later GI passes.";

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
                MarkSkipped(sceneData, reason);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            IReadOnlyList<MeshSdfBakeJob> jobs = _meshSdfManager.PrepareBakeJobs(_settings.GlobalIllumination.MeshSdfBakeBudget);
            sceneData.MeshSdfQueuedBakeCount = _meshSdfManager.LastFrameQueuedMeshCount;
            sceneData.MeshSdfBakedMeshCount = _meshSdfManager.LastFrameBakedMeshCount;
            sceneData.MeshSdfBakeVoxelCount = _meshSdfManager.LastFrameBakeVoxelCount;
            sceneData.MeshSdfTextureBytes = _meshSdfManager.MeshSdfTextureBytes;
            sceneData.MeshSdfBufferBytes = _meshSdfManager.MeshSdfBufferBytes;
            sceneData.MeshSdfAllocatedBytesThisFrame = _meshSdfManager.LastFrameAllocatedBytes;
            sceneData.MeshSdfTotalBakedMeshCount = _meshSdfManager.BakedMeshCount;
            sceneData.MeshSdfPendingBakeCount = _meshSdfManager.PendingBakeCount;

            if (jobs.Count == 0)
            {
                MarkSkipped(sceneData, "no-pending-mesh-sdf-bakes");
                return;
            }

            sceneData.MeshSdfBakeExecuted = 1;
            sceneData.MeshSdfBakeSkipReason = string.Empty;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            for (int i = 0; i < jobs.Count; i++)
            {
                MeshSdfBakeJob job = jobs[i];
                job.Volume.TransitionToStorageReadWrite(cmd);

                GPUMeshSdfBakeConstants pushConstants = job.PushConstants;
                pushConstants.FrameIndex = sceneData.CurrentFrameIndex;
                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GPUMeshSdfBakeConstants>(),
                    &pushConstants);

                uint groupX = (job.Request.Descriptor.Extent.Width + 3u) / 4u;
                uint groupY = (job.Request.Descriptor.Extent.Height + 3u) / 4u;
                uint groupZ = (job.Request.Descriptor.Extent.Depth + 3u) / 4u;
                _context.Api.CmdDispatch(cmd, groupX, groupY, groupZ);
                job.Volume.TransitionToShaderRead(cmd);
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
            if (_pipeline.Handle == 0)
                return "pipeline-unavailable";
            if (!_settings.GlobalIllumination.Enabled)
                return "global-illumination-disabled";
            if (!_settings.GlobalIllumination.EffectiveUseDdgi)
                return "ddgi-disabled";
            if (!_accelerationStructureManager.Active)
                return "acceleration-structure-inactive";
            if (_settings.GlobalIllumination.MeshSdfBakeBudget <= 0)
                return "mesh-sdf-bake-budget-zero";
            if (_meshSdfManager.PendingBakeCount <= 0)
                return "no-pending-mesh-sdf-bakes";
            return string.Empty;
        }

        private static void MarkSkipped(SceneRenderingData sceneData, string reason)
        {
            sceneData.MeshSdfBakeExecuted = 0;
            sceneData.MeshSdfBakeSkipReason = reason;
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create MeshSdfBakePass pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "MeshSdfBakePass Pipeline Cache");
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
                    Size = (uint)Marshal.SizeOf<GPUMeshSdfBakeConstants>()
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
                    throw new VulkanException("Failed to create MeshSdfBakePass pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "MeshSdfBakePass Pipeline Layout");
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
                    throw new VulkanException("Failed to create MeshSdfBakePass compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "MeshSdfBakePass Compute Pipeline");
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
