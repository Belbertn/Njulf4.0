using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
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
        private const string MipReduceShaderName = "global_sdf_mip_reduce.comp.spv";
        private const string EntryPoint = "main";
        private const uint MaxGeneratedMipLevel = 3;

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
        private VkPipeline _mipReducePipeline;

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
            _pipeline = CreatePipeline(ShaderName, "GlobalSdfPass Compute Pipeline");
            _mipReducePipeline = CreatePipeline(MipReduceShaderName, "GlobalSdfPass Min Mip Pipeline");
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
            ExecuteInternal(cmd, frameIndex, sceneData, null);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData, GpuTimestampRecorder? timestamps)
        {
            ExecuteInternal(cmd, frameIndex, sceneData, timestamps);
        }

        private void ExecuteInternal(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData, GpuTimestampRecorder? timestamps)
        {
            int activeMeshSdfCount;
            IReadOnlyList<GlobalSdfUpdateJob> jobs;
            timestamps?.BeginPass(cmd, frameIndex, "GlobalSdfUpload");
            try
            {
                activeMeshSdfCount = _meshSdfManager.PrepareInstanceRecords(
                    _accelerationStructureManager.LastStaticOpaqueInstances,
                    _stagingRing,
                    cmd);
                jobs = _globalSdfManager.PrepareUpdateJobs(
                    sceneData.CameraPosition,
                    _settings.GlobalIllumination.SdfClipmapResolution,
                    _settings.GlobalIllumination.SdfBrickUpdateBudget,
                    _ddgiFrameLayoutProvider());
                _globalSdfManager.UploadCascadeMetadata(_stagingRing, cmd);
            }
            finally
            {
                timestamps?.EndPass(cmd, frameIndex);
            }

            sceneData.GlobalSdfCascadeCount = _globalSdfManager.LastFrameCascadeCount;
            sceneData.GlobalSdfResolution = _globalSdfManager.LastFrameResolution;
            sceneData.GlobalSdfBricksUpdated = _globalSdfManager.LastFrameBricksUpdated;
            sceneData.GlobalSdfDirtyBrickBacklog = _globalSdfManager.LastFrameDirtyBrickBacklog;
            sceneData.GlobalSdfTextureBytes = _globalSdfManager.TextureBytes;
            sceneData.GlobalSdfMeshSdfCount = activeMeshSdfCount;
            sceneData.GlobalSdfBackendFirstCascade = _settings.GlobalIllumination.SdfBackendFirstCascade;
            sceneData.GlobalSdfBrickUpdateBudget = _settings.GlobalIllumination.SdfBrickUpdateBudget;
            sceneData.MeshSdfInstanceUploadBytes = _meshSdfManager.LastFrameInstanceUploadBytes;
            sceneData.MeshSdfInstanceUploadSkipped = _meshSdfManager.LastFrameInstanceUploadSkipped;

            if (jobs.Count == 0)
            {
                sceneData.GlobalSdfExecuted = 0;
                sceneData.GlobalSdfSkipReason = "no-global-sdf-brick-budget";
                return;
            }

            sceneData.GlobalSdfExecuted = 1;
            sceneData.GlobalSdfSkipReason = string.Empty;

            timestamps?.BeginPass(cmd, frameIndex, "GlobalSdfBricks");
            var touchedVolumes = new List<TouchedVolume>(jobs.Count);
            try
            {
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
                BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

                for (int i = 0; i < jobs.Count; i++)
                {
                    GlobalSdfUpdateJob job = jobs[i];
                    job.Volume.TransitionToStorageReadWrite(cmd);
                    if (!ContainsVolume(touchedVolumes, job.Volume))
                        touchedVolumes.Add(new TouchedVolume(job.Volume, job.TextureIndex, job.MipStorageImageIndices));

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

            }
            finally
            {
                timestamps?.EndPass(cmd, frameIndex);
            }

            timestamps?.BeginPass(cmd, frameIndex, "GlobalSdfMips");
            try
            {
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _mipReducePipeline);
                BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

                for (int i = 0; i < touchedVolumes.Count; i++)
                    GenerateMinMipChain(cmd, touchedVolumes[i]);
            }
            finally
            {
                timestamps?.EndPass(cmd, frameIndex);
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

            if (_mipReducePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _mipReducePipeline, null);
                _mipReducePipeline = default;
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

        private void GenerateMinMipChain(CommandBuffer cmd, TouchedVolume touchedVolume)
        {
            VolumeTexture volume = touchedVolume.Volume;
            if (volume.MipLevels <= 1)
            {
                volume.TransitionToShaderRead(cmd);
                return;
            }

            volume.TransitionToStorageReadWrite(cmd);

            uint mipWidth = volume.Extent.Width;
            uint mipHeight = volume.Extent.Height;
            uint mipDepth = volume.Extent.Depth;
            uint lastGeneratedMip = Math.Min(volume.MipLevels - 1u, MaxGeneratedMipLevel);
            for (uint mip = 1; mip <= lastGeneratedMip; mip++)
            {
                uint nextWidth = Math.Max(1u, mipWidth >> 1);
                uint nextHeight = Math.Max(1u, mipHeight >> 1);
                uint nextDepth = Math.Max(1u, mipDepth >> 1);

                BarrierGlobalSdfMipCompute(cmd, volume, mip - 1u);

                var pushConstants = new GlobalSdfMipReduceConstants
                {
                    SourceStorageImageIndex = checked((uint)touchedVolume.MipStorageImageIndices[mip - 1u]),
                    DestinationStorageImageIndex = checked((uint)touchedVolume.MipStorageImageIndices[mip]),
                    DestinationWidth = nextWidth,
                    DestinationHeight = nextHeight,
                    DestinationDepth = nextDepth
                };
                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GlobalSdfMipReduceConstants>(),
                    &pushConstants);

                _context.Api.CmdDispatch(cmd, DivideRoundUp(nextWidth, 4u), DivideRoundUp(nextHeight, 4u), DivideRoundUp(nextDepth, 4u));

                mipWidth = nextWidth;
                mipHeight = nextHeight;
                mipDepth = nextDepth;
            }

            volume.TransitionToShaderRead(cmd);
        }

        private void BarrierGlobalSdfMipCompute(CommandBuffer cmd, VolumeTexture volume, uint sourceMip)
        {
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = volume.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = sourceMip,
                    LevelCount = Math.Min(2u, volume.MipLevels - sourceMip),
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private static bool ContainsVolume(List<TouchedVolume> touchedVolumes, VolumeTexture volume)
        {
            for (int i = 0; i < touchedVolumes.Count; i++)
            {
                if (ReferenceEquals(touchedVolumes[i].Volume, volume))
                    return true;
            }

            return false;
        }

        private static uint DivideRoundUp(uint value, uint divisor) => (value + divisor - 1u) / divisor;

        private VkPipeline CreatePipeline(string shaderName, string debugName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, shaderName);

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
                    throw new VulkanException($"Failed to create {debugName}", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private readonly record struct TouchedVolume(VolumeTexture Volume, int TextureIndex, int[] MipStorageImageIndices);

        private struct GlobalSdfMipReduceConstants
        {
            public uint SourceStorageImageIndex;
            public uint DestinationStorageImageIndex;
            public uint DestinationWidth;
            public uint DestinationHeight;
            public uint DestinationDepth;
            public uint Padding0;
            public uint Padding1;
            public uint Padding2;
        }
    }
}
