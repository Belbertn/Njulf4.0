using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class AutoExposureManager : IDisposable
    {
        public const int HistogramBinCount = 256;
        public const int StateWordCount = 8;
        public const ulong HistogramBufferSize = HistogramBinCount * sizeof(uint);
        public const ulong StateBufferSize = StateWordCount * sizeof(uint);

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly BufferHandle[] _histogramBuffers = new BufferHandle[FramesInFlight];
        private readonly BufferHandle[] _stateBuffers = new BufferHandle[FramesInFlight];
        private bool _disposed;

        public AutoExposureManager(VulkanContext context, BufferManager bufferManager, RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            for (int i = 0; i < FramesInFlight; i++)
            {
                _histogramBuffers[i] = _bufferManager.CreateBuffer(
                    HistogramBufferSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferDevice,
                    debugName: $"Auto Exposure Histogram Buffer Frame {i}",
                    category: MemoryBudgetCategory.RenderTargets);

                _stateBuffers[i] = _bufferManager.CreateBuffer(
                    StateBufferSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    $"Auto Exposure State Buffer Frame {i}",
                    MemoryBudgetCategory.RenderTargets);

                InitializeStateBuffer(i, _settings.Exposure);
            }

            LastCompleted = CreateManualSnapshot();
        }

        public AutoExposureSnapshot LastCompleted { get; private set; }

        public float PreviousExposure => LastCompleted.Valid
            ? LastCompleted.Exposure
            : Math.Clamp(_settings.Exposure, _settings.AutoExposure.MinExposure, _settings.AutoExposure.MaxExposure);

        public int GetHistogramBufferIndex(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return BindlessIndex.AutoExposureHistogramBufferBase + frameIndex;
        }

        public int GetStateBufferIndex(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return BindlessIndex.AutoExposureStateBufferBase + frameIndex;
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            for (int i = 0; i < FramesInFlight; i++)
            {
                bindlessHeap.RegisterStorageBuffer(
                    GetHistogramBufferIndex(i),
                    _bufferManager.GetBuffer(_histogramBuffers[i]),
                    0,
                    HistogramBufferSize);

                bindlessHeap.RegisterStorageBuffer(
                    GetStateBufferIndex(i),
                    _bufferManager.GetBuffer(_stateBuffers[i]),
                    0,
                    StateBufferSize);
            }
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);

            if (!_settings.AutoExposure.Enabled)
            {
                LastCompleted = CreateManualSnapshot();
                return;
            }

            _bufferManager.InvalidateBuffer(_stateBuffers[frameIndex], 0, StateBufferSize);
            float* values = (float*)_bufferManager.GetMappedPointer(_stateBuffers[frameIndex]);
            uint* words = (uint*)values;

            bool valid = words[5] != 0;
            float exposure = values[0];
            float averageLuminance = values[1];
            float targetExposure = values[2];
            uint sampleCount = words[3];
            float adaptationAlpha = values[4];

            if (!valid ||
                !float.IsFinite(exposure) ||
                !float.IsFinite(averageLuminance) ||
                !float.IsFinite(targetExposure))
            {
                LastCompleted = CreateManualSnapshot();
                return;
            }

            LastCompleted = new AutoExposureSnapshot(
                true,
                Math.Clamp(exposure, _settings.AutoExposure.MinExposure, _settings.AutoExposure.MaxExposure),
                Math.Max(0.0f, averageLuminance),
                Math.Clamp(targetExposure, _settings.AutoExposure.MinExposure, _settings.AutoExposure.MaxExposure),
                sampleCount,
                Math.Clamp(adaptationAlpha, 0.0f, 1.0f));
        }

        public void ResetHistogram(CommandBuffer commandBuffer, int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            VkBuffer histogramBuffer = _bufferManager.GetBuffer(_histogramBuffers[frameIndex]);

            _context.Api.CmdFillBuffer(commandBuffer, histogramBuffer, 0, HistogramBufferSize, 0);
            InsertBufferBarrier(
                commandBuffer,
                histogramBuffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                HistogramBufferSize);
        }

        public void BarrierAfterHistogram(CommandBuffer commandBuffer, int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            InsertBufferBarrier(
                commandBuffer,
                _bufferManager.GetBuffer(_histogramBuffers[frameIndex]),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                HistogramBufferSize);
        }

        public void BarrierAfterExposureWrite(CommandBuffer commandBuffer, int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            InsertBufferBarrier(
                commandBuffer,
                _bufferManager.GetBuffer(_stateBuffers[frameIndex]),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                StateBufferSize);
        }

        private void InitializeStateBuffer(int frameIndex, float exposure)
        {
            float* values = (float*)_bufferManager.GetMappedPointer(_stateBuffers[frameIndex]);
            uint* words = (uint*)values;
            values[0] = Math.Max(0.0f, exposure);
            values[1] = 0.0f;
            values[2] = Math.Max(0.0f, exposure);
            words[3] = 0u;
            values[4] = 0.0f;
            words[5] = 0u;
            words[6] = 0u;
            words[7] = 0u;
            _bufferManager.FlushBuffer(_stateBuffers[frameIndex], 0, StateBufferSize);
        }

        private AutoExposureSnapshot CreateManualSnapshot()
        {
            float exposure = Math.Clamp(
                _settings.Exposure,
                _settings.AutoExposure.MinExposure,
                _settings.AutoExposure.MaxExposure);
            return new AutoExposureSnapshot(false, exposure, 0.0f, exposure, 0u, 0.0f);
        }

        private void InsertBufferBarrier(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            PipelineStageFlags2 sourceStage,
            AccessFlags2 sourceAccess,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess,
            ulong size)
        {
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = sourceStage,
                SrcAccessMask = sourceAccess,
                DstStageMask = destinationStage,
                DstAccessMask = destinationAccess,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = 0,
                Size = size
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            for (int i = 0; i < FramesInFlight; i++)
            {
                if (_histogramBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_histogramBuffers[i]);
                if (_stateBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_stateBuffers[i]);
            }
        }
    }

    public readonly record struct AutoExposureSnapshot(
        bool Valid,
        float Exposure,
        float AverageLuminance,
        float TargetExposure,
        uint SampleCount,
        float AdaptationAlpha);
}
