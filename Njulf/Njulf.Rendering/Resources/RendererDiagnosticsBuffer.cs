using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class RendererDiagnosticsBuffer : IDisposable
    {
        public const int MeshletCounterCount = 9;
        public const int DdgiForwardEstimateCounterBase = MeshletCounterCount;
        public const int DdgiForwardEstimateCounterCount = 15;
        public const int CounterCount = MeshletCounterCount + DdgiForwardEstimateCounterCount;
        public const float DdgiForwardEstimateWeightScale = 1024.0f;
        public const float DdgiForwardEstimateLuminanceScale = 16.0f;
        private const ulong CounterBufferSize = CounterCount * sizeof(uint);

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BufferHandle[] _buffers = new BufferHandle[FramesInFlight];
        private readonly GpuMeshletCounters[] _lastCompletedCounters = new GpuMeshletCounters[FramesInFlight];
        private readonly DdgiForwardEstimateCounters[] _lastCompletedDdgiForwardEstimateCounters = new DdgiForwardEstimateCounters[FramesInFlight];
        private bool _disposed;

        public RendererDiagnosticsBuffer(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));

            for (int i = 0; i < FramesInFlight; i++)
            {
                _buffers[i] = _bufferManager.CreateBuffer(
                    CounterBufferSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    $"Renderer Diagnostics Buffer Frame {i}",
                    MemoryBudgetCategory.DiagnosticsAndDebug);
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            for (int i = 0; i < FramesInFlight; i++)
            {
                VkBuffer buffer = _bufferManager.GetBuffer(_buffers[i]);
                bindlessHeap.RegisterStorageBuffer(BindlessIndex.RendererDiagnosticsBufferBase + i, buffer, 0, CounterBufferSize);
            }
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            _bufferManager.InvalidateBuffer(_buffers[frameIndex], 0, CounterBufferSize);
            uint* counters = (uint*)_bufferManager.GetMappedPointer(_buffers[frameIndex]);

            _lastCompletedCounters[frameIndex] = new GpuMeshletCounters(
                checked((int)counters[0]),
                checked((int)counters[1]),
                checked((int)counters[2]),
                checked((int)counters[3]),
                checked((int)counters[4]),
                checked((int)counters[5]),
                checked((int)counters[6]),
                checked((int)counters[7]),
                checked((int)counters[8]));

            uint sampleCount = counters[DdgiForwardEstimateCounterBase + 5];
            uint visibilityMomentSampleCount = counters[DdgiForwardEstimateCounterBase + 11];
            if (sampleCount > 0 || visibilityMomentSampleCount > 0)
            {
                float invSampleCount = sampleCount > 0 ? 1.0f / sampleCount : 0.0f;
                float invVisibilityMomentSampleCount = visibilityMomentSampleCount > 0 ? 1.0f / visibilityMomentSampleCount : 0.0f;
                _lastCompletedDdgiForwardEstimateCounters[frameIndex] = new DdgiForwardEstimateCounters(
                    ReadbackValid: sampleCount > 0 ? 1 : 0,
                    CoverageAverage: counters[DdgiForwardEstimateCounterBase + 0] / DdgiForwardEstimateWeightScale * invSampleCount,
                    VisibleSupportAverage: counters[DdgiForwardEstimateCounterBase + 1] / DdgiForwardEstimateWeightScale * invSampleCount,
                    EffectiveWeightAverage: counters[DdgiForwardEstimateCounterBase + 2] / DdgiForwardEstimateWeightScale * invSampleCount,
                    RawDiffuseLuminanceAverage: counters[DdgiForwardEstimateCounterBase + 3] / DdgiForwardEstimateLuminanceScale * invSampleCount,
                    FinalDiffuseLuminanceAverage: counters[DdgiForwardEstimateCounterBase + 4] / DdgiForwardEstimateLuminanceScale * invSampleCount,
                    SampleCount: sampleCount,
                    ZeroVisibleButCoveredCount: counters[DdgiForwardEstimateCounterBase + 6],
                    ZeroEffectiveButCoveredCount: counters[DdgiForwardEstimateCounterBase + 7],
                    VisibilityMomentMeanAverage: counters[DdgiForwardEstimateCounterBase + 8] / DdgiForwardEstimateWeightScale * invVisibilityMomentSampleCount,
                    VisibilityMomentVarianceAverage: counters[DdgiForwardEstimateCounterBase + 9] / DdgiForwardEstimateWeightScale * invVisibilityMomentSampleCount,
                    VisibilityProbeDistanceAverage: counters[DdgiForwardEstimateCounterBase + 10] / DdgiForwardEstimateWeightScale * invVisibilityMomentSampleCount,
                    VisibilityMomentSampleCount: visibilityMomentSampleCount,
                    VisibilityLargeDistanceMarginCount: counters[DdgiForwardEstimateCounterBase + 12],
                    VisibilityZeroTransportCount: counters[DdgiForwardEstimateCounterBase + 13],
                    VisibilityZeroTransportWithIrradianceCount: counters[DdgiForwardEstimateCounterBase + 14]);
            }
            else
            {
                _lastCompletedDdgiForwardEstimateCounters[frameIndex] = DdgiForwardEstimateCounters.Empty;
            }
        }

        public GpuMeshletCounters GetLastCompletedCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedCounters[frameIndex];
        }

        public DdgiForwardEstimateCounters GetLastCompletedDdgiForwardEstimateCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDdgiForwardEstimateCounters[frameIndex];
        }

        public void ResetCounters(CommandBuffer commandBuffer, int frameIndex)
        {
            ValidateFrameIndex(frameIndex);

            _context.Api.CmdFillBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(_buffers[frameIndex]),
                0,
                CounterBufferSize,
                0);

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_buffers[frameIndex]),
                Offset = 0,
                Size = CounterBufferSize
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
                if (_buffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_buffers[i]);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct GpuMeshletCounters(
        int DepthCandidates,
        int DepthFrustumCulled,
        int DepthEmitted,
        int ForwardCandidates,
        int ForwardFrustumCulled,
        int ForwardOcclusionCulled,
        int ForwardEmitted,
        int ForwardOcclusionTested,
        int SsgiRejectedHistoryPixels);
}
