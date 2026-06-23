using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
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
    public sealed unsafe class SsgiTemporalPass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint TraceContractVersion = 1u;

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _outputSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _writeHistoryASet;
        private DescriptorSet _writeHistoryBSet;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _historyValid;
        private bool _writeHistoryA = true;
        private Extent2D _lastExtent;
        private GlobalIlluminationMode _lastMode = GlobalIlluminationMode.Disabled;
        private float _lastResolutionScale = -1.0f;
        private float _lastSsgiMaxDistance = -1.0f;
        private float _lastSsgiThickness = -1.0f;
        private float _lastSsgiHitNormalThreshold = -1.0f;
        private uint? _lastProducerExecutedSampleIndex;
        private uint _lastTraceContractVersion = TraceContractVersion;
        private bool _hasLastCameraState;
        private Matrix4x4 _lastProjectionMatrix;
        private Vector3 _lastCameraPosition;

        public SsgiTemporalPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("SsgiTemporalPass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            CreateOutputSetLayout();
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
            RecreateDescriptorSets();
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (!GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(gi, sceneData.DebugViewMode) ||
                !sceneData.DepthPrePassEnabled ||
                sceneData.FoliageDebugView != 0 ||
                sceneData.AnimationDebugView != AnimationDebugView.None)
            {
                InvalidateHistory(sceneData);
                RegisterResolvedSsgiTexture(_renderTargets.SsgiRaw.View, _renderTargets.SsgiRaw.View);
                return false;
            }

            if (!gi.TemporalEnabled)
            {
                InvalidateHistory(sceneData);
                RegisterResolvedSsgiTexture(_renderTargets.SsgiRaw.View, _renderTargets.SsgiRaw.View);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            ResetHistoryIfInputsChanged(sceneData);

            RenderTarget historyRead = _writeHistoryA ? _renderTargets.SsgiHistoryB : _renderTargets.SsgiHistoryA;
            RenderTarget historyWrite = _writeHistoryA ? _renderTargets.SsgiHistoryA : _renderTargets.SsgiHistoryB;
            RenderTarget depthHistoryRead = _writeHistoryA ? _renderTargets.SsgiDepthHistoryB : _renderTargets.SsgiDepthHistoryA;
            RenderTarget depthHistoryWrite = _writeHistoryA ? _renderTargets.SsgiDepthHistoryA : _renderTargets.SsgiDepthHistoryB;
            RenderTarget normalHistoryRead = _writeHistoryA ? _renderTargets.SsgiNormalHistoryB : _renderTargets.SsgiNormalHistoryA;
            RenderTarget normalHistoryWrite = _writeHistoryA ? _renderTargets.SsgiNormalHistoryA : _renderTargets.SsgiNormalHistoryB;
            RenderTarget momentsRead = _writeHistoryA ? _renderTargets.SsgiMomentsB : _renderTargets.SsgiMomentsA;
            RenderTarget momentsWrite = _writeHistoryA ? _renderTargets.SsgiMomentsA : _renderTargets.SsgiMomentsB;
            RenderTarget historyLengthRead = _writeHistoryA ? _renderTargets.SsgiHistoryLengthB : _renderTargets.SsgiHistoryLengthA;
            RenderTarget historyLengthWrite = _writeHistoryA ? _renderTargets.SsgiHistoryLengthA : _renderTargets.SsgiHistoryLengthB;
            DescriptorSet outputSet = _writeHistoryA ? _writeHistoryASet : _writeHistoryBSet;

            _renderTargets.SsgiRaw.TransitionToShaderRead(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.SceneNormal.TransitionToShaderRead(cmd);
            if (sceneData.MotionVectorsEnabled != 0)
                _renderTargets.MotionVectors.TransitionToShaderRead(cmd);
            historyRead.TransitionToShaderRead(cmd);
            depthHistoryRead.TransitionToShaderRead(cmd);
            normalHistoryRead.TransitionToShaderRead(cmd);
            momentsRead.TransitionToShaderRead(cmd);
            historyLengthRead.TransitionToShaderRead(cmd);
            _renderTargets.SsgiFiltered.TransitionToStorageWrite(cmd);
            historyWrite.TransitionToStorageWrite(cmd);
            depthHistoryWrite.TransitionToStorageWrite(cmd);
            normalHistoryWrite.TransitionToStorageWrite(cmd);
            momentsWrite.TransitionToStorageWrite(cmd);
            historyLengthWrite.TransitionToStorageWrite(cmd);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryTexture,
                historyRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousDepthTexture,
                depthHistoryRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousNormalTexture,
                normalHistoryRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiMomentsTexture,
                momentsRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryLengthTexture,
                historyLengthRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 1, 1, &textureSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &outputSet, 0, null);

            uint historyWasValid = _historyValid ? 1u : 0u;
            GPUSsgiTemporalPushConstants pushConstants = CreatePushConstants(sceneData, historyWasValid, (uint)frameIndex);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSsgiTemporalPushConstants>(),
                &pushConstants);

            Extent2D extent = _renderTargets.SsgiFiltered.Extent;
            _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u, (extent.Height + 7u) / 8u, 1);

            _renderTargets.SsgiFiltered.TransitionToShaderRead(cmd);
            historyWrite.TransitionToShaderRead(cmd);
            depthHistoryWrite.TransitionToShaderRead(cmd);
            normalHistoryWrite.TransitionToShaderRead(cmd);
            momentsWrite.TransitionToShaderRead(cmd);
            historyLengthWrite.TransitionToShaderRead(cmd);
            RegisterResolvedSsgiTexture(_renderTargets.SsgiFiltered.View, historyWrite.View);
            RegisterPreviousSurfaceHistoryTextures(depthHistoryWrite.View, normalHistoryWrite.View);
            RegisterTemporalStatisticsTextures(momentsWrite.View, historyLengthWrite.View);

            sceneData.SsgiHistoryValid = (int)historyWasValid;

            _historyValid = true;
            _writeHistoryA = !_writeHistoryA;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            _historyValid = false;
            _writeHistoryA = true;
            _lastExtent = default;
            _lastSsgiMaxDistance = -1.0f;
            _lastSsgiThickness = -1.0f;
            _lastSsgiHitNormalThreshold = -1.0f;
            _lastProducerExecutedSampleIndex = null;
            _lastTraceContractVersion = TraceContractVersion;
            _hasLastCameraState = false;
            RecreateDescriptorSets();
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }

            DestroyDescriptorPool();

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_outputSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _outputSetLayout, null);
                _outputSetLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private void InvalidateHistory(SceneRenderingData sceneData)
        {
            _historyValid = false;
            _writeHistoryA = true;
            _lastProducerExecutedSampleIndex = null;
            _hasLastCameraState = false;
            sceneData.SsgiHistoryValid = 0;
            sceneData.SsgiRejectedHistoryPixelCount = 0;
        }

        private void ResetHistoryIfInputsChanged(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            Extent2D extent = _renderTargets.SsgiRaw.Extent;
            bool changed = _lastExtent.Width != extent.Width ||
                _lastExtent.Height != extent.Height ||
                _lastMode != gi.Mode ||
                MathF.Abs(_lastResolutionScale - gi.ResolutionScale) > 0.0001f ||
                MathF.Abs(_lastSsgiMaxDistance - gi.SsgiMaxDistance) > 0.0001f ||
                MathF.Abs(_lastSsgiThickness - gi.SsgiThickness) > 0.0001f ||
                MathF.Abs(_lastSsgiHitNormalThreshold - gi.SsgiHitNormalThreshold) > 0.0001f;
            changed |= _lastTraceContractVersion != TraceContractVersion ||
                (_lastProducerExecutedSampleIndex.HasValue &&
                    sceneData.TemporalSampleIndex != unchecked(_lastProducerExecutedSampleIndex.Value + 1u));
            changed |= sceneData.HiZPolicyCameraCut != 0 ||
                HasProjectionChanged(sceneData.ProjectionMatrix) ||
                HasCameraTeleported(sceneData.CameraPosition, gi.SsgiMaxDistance);

            if (changed)
            {
                _historyValid = false;
                _writeHistoryA = true;
            }

            _lastExtent = extent;
            _lastMode = gi.Mode;
            _lastResolutionScale = gi.ResolutionScale;
            _lastSsgiMaxDistance = gi.SsgiMaxDistance;
            _lastSsgiThickness = gi.SsgiThickness;
            _lastSsgiHitNormalThreshold = gi.SsgiHitNormalThreshold;
            _lastProducerExecutedSampleIndex = sceneData.TemporalSampleIndex;
            _lastTraceContractVersion = TraceContractVersion;
            _lastProjectionMatrix = sceneData.ProjectionMatrix;
            _lastCameraPosition = sceneData.CameraPosition;
            _hasLastCameraState = true;
        }

        private bool HasProjectionChanged(Matrix4x4 projectionMatrix)
        {
            if (!_hasLastCameraState)
                return false;

            return !ApproximatelyEqualProjection(_lastProjectionMatrix, projectionMatrix, 0.0005f);
        }

        private bool HasCameraTeleported(Vector3 cameraPosition, float ssgiMaxDistance)
        {
            if (!_hasLastCameraState)
                return false;

            float maxStableDistance = MathF.Max(1.0f, ssgiMaxDistance * 0.5f);
            return Vector3.DistanceSquared(_lastCameraPosition, cameraPosition) > maxStableDistance * maxStableDistance;
        }

        private static bool ApproximatelyEqualProjection(Matrix4x4 a, Matrix4x4 b, float epsilon)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (IsJitterTerm(row, column))
                        continue;

                    float av = a[row, column];
                    float bv = b[row, column];
                    if (!float.IsFinite(av) || !float.IsFinite(bv))
                        return false;
                    if (MathF.Abs(av - bv) > epsilon)
                        return false;
                }
            }

            return true;
        }

        private static bool IsJitterTerm(int row, int column)
        {
            return (row == 2 && (column == 0 || column == 1)) ||
                (column == 2 && (row == 0 || row == 1));
        }

        private GPUSsgiTemporalPushConstants CreatePushConstants(SceneRenderingData sceneData, uint historyValid, uint frameIndex)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            Extent2D extent = _renderTargets.SsgiFiltered.Extent;
            return new GPUSsgiTemporalPushConstants
            {
                SourceDimensions = new Vector4(
                    extent.Width,
                    extent.Height,
                    1.0f / Math.Max(1u, extent.Width),
                    1.0f / Math.Max(1u, extent.Height)),
                ReprojectionParams = new Vector4(
                    gi.HistoryResponsiveness,
                    gi.NormalRejectionThreshold,
                    gi.DepthRejectionThreshold,
                    gi.LeakClampStrength),
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                HistoryValid = historyValid,
                MotionVectorsEnabled = sceneData.MotionVectorsEnabled != 0 ? 1u : 0u,
                FrameIndex = frameIndex,
                DebugView = (uint)gi.DebugView
            };
        }

        private void RegisterResolvedSsgiTexture(ImageView filteredView, ImageView historyView)
        {
            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiFilteredTexture,
                filteredView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryTexture,
                historyView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterPreviousSurfaceHistoryTextures(ImageView previousDepthView, ImageView previousNormalView)
        {
            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousDepthTexture,
                previousDepthView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousNormalTexture,
                previousNormalView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterTemporalStatisticsTextures(ImageView momentsView, ImageView historyLengthView)
        {
            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiMomentsTexture,
                momentsView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryLengthTexture,
                historyLengthView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        private void CreateOutputSetLayout()
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[5];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 2,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[2] = new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[3] = new DescriptorSetLayoutBinding
            {
                Binding = 3,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[4] = new DescriptorSetLayoutBinding
            {
                Binding = 4,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 5,
                PBindings = bindings
            };

            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _outputSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI temporal output descriptor set layout", result);
            _context.SetDebugName(_outputSetLayout.Handle, ObjectType.DescriptorSetLayout, "SSGI Temporal Output Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI temporal pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "SSGI Temporal Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts =
            [
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout,
                _outputSetLayout
            ];

            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUSsgiTemporalPushConstants>()
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
                    throw new VulkanException("Failed to create SSGI temporal pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "SSGI Temporal Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "ssgi_temporal.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "ssgi_temporal.comp.spv");

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
                    throw new VulkanException("Failed to create SSGI temporal compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "SSGI Temporal Compute Pipeline");
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void RecreateDescriptorSets()
        {
            DestroyDescriptorPool();

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = 12
            };

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 2
            };

            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI temporal descriptor pool", result);

            var sets = stackalloc DescriptorSet[2];
            var layouts = stackalloc DescriptorSetLayout[2];
            layouts[0] = _outputSetLayout;
            layouts[1] = _outputSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 2,
                PSetLayouts = layouts
            };

            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, sets);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate SSGI temporal descriptor sets", result);

            _writeHistoryASet = sets[0];
            _writeHistoryBSet = sets[1];
            WriteOutputSet(
                _writeHistoryASet,
                _renderTargets.SsgiHistoryA.View,
                _renderTargets.SsgiDepthHistoryA.View,
                _renderTargets.SsgiNormalHistoryA.View,
                _renderTargets.SsgiMomentsA.View,
                _renderTargets.SsgiHistoryLengthA.View);
            WriteOutputSet(
                _writeHistoryBSet,
                _renderTargets.SsgiHistoryB.View,
                _renderTargets.SsgiDepthHistoryB.View,
                _renderTargets.SsgiNormalHistoryB.View,
                _renderTargets.SsgiMomentsB.View,
                _renderTargets.SsgiHistoryLengthB.View);
        }

        private void WriteOutputSet(
            DescriptorSet set,
            ImageView historyWriteView,
            ImageView depthHistoryWriteView,
            ImageView normalHistoryWriteView,
            ImageView momentsWriteView,
            ImageView historyLengthWriteView)
        {
            var outputInfos = stackalloc DescriptorImageInfo[2];
            outputInfos[0] = new DescriptorImageInfo
            {
                ImageView = _renderTargets.SsgiFiltered.View,
                ImageLayout = ImageLayout.General
            };
            outputInfos[1] = new DescriptorImageInfo
            {
                ImageView = historyWriteView,
                ImageLayout = ImageLayout.General
            };

            var depthHistoryInfo = new DescriptorImageInfo
            {
                ImageView = depthHistoryWriteView,
                ImageLayout = ImageLayout.General
            };
            var normalHistoryInfo = new DescriptorImageInfo
            {
                ImageView = normalHistoryWriteView,
                ImageLayout = ImageLayout.General
            };
            var momentsInfo = new DescriptorImageInfo
            {
                ImageView = momentsWriteView,
                ImageLayout = ImageLayout.General
            };
            var historyLengthInfo = new DescriptorImageInfo
            {
                ImageView = historyLengthWriteView,
                ImageLayout = ImageLayout.General
            };

            var writes = stackalloc WriteDescriptorSet[5];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 2,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = outputInfos
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &depthHistoryInfo
            };
            writes[2] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &normalHistoryInfo
            };
            writes[3] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 3,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &momentsInfo
            };
            writes[4] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 4,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &historyLengthInfo
            };

            _context.Api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
        }

        private void DestroyDescriptorPool()
        {
            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
                _writeHistoryASet = default;
                _writeHistoryBSet = default;
            }
        }
    }
}
