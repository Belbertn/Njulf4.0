using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class ForwardVisibilityCompactionPass : RenderPassBase
    {
        private const uint WorkgroupSize = 64;
        private const int SimpleOpaqueIndirectDispatchSlot = 1;
        private const int SimpleNormalOpaqueIndirectDispatchSlot = 2;
        private const int FullOpaqueIndirectDispatchSlot = 3;
        private const int IndirectDispatchSlotCount = 4;
        private static readonly ulong DrawCommandStride = (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>();
        private static readonly ulong CounterStride = (ulong)Marshal.SizeOf<GPUSceneSubmissionCounters>();
        private static readonly ulong IndirectDispatchStride = (ulong)Marshal.SizeOf<GPUFoliageDispatchArgs>();

        private readonly MeshPipeline _meshPipeline;
        private readonly BufferManager _bufferManager;
        private readonly RuntimeBuffer[] _simpleVisibleDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _simpleNormalVisibleDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _fullVisibleDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _counterBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _indirectDispatchBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly BufferHandle[] _counterReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly bool[] _counterReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private readonly SceneSubmissionCounterSnapshot[] _completedCounters =
        [
            SceneSubmissionCounterSnapshot.Invalid,
            SceneSubmissionCounterSnapshot.Invalid
        ];

        public ForwardVisibilityCompactionPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            BufferManager bufferManager)
            : base("ForwardVisibilityCompactionPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.ForwardVisibilityCompactionEnabled &&
                sceneData.SceneSubmissionGpuCompactionActive &&
                sceneData.SceneSubmissionIndirectMeshletDispatchEnabled &&
                sceneData.OcclusionCullingEnabled &&
                sceneData.HiZMipCount > 0 &&
                sceneData.SceneSubmissionFallbackReason.Length == 0 &&
                sceneData.SceneSubmissionCompactionSkipReason.Length == 0 &&
                sceneData.SceneSubmissionGpuOpaqueCandidateCount > 0;
        }

        public override void Initialize()
        {
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            if (!_counterReadbackRecorded[frameIndex] || !_counterReadbackBuffers[frameIndex].IsValid)
            {
                _completedCounters[frameIndex] = SceneSubmissionCounterSnapshot.Invalid;
                return;
            }

            _bufferManager.InvalidateBuffer(_counterReadbackBuffers[frameIndex], 0, CounterStride);
            GPUSceneSubmissionCounters* counters =
                (GPUSceneSubmissionCounters*)_bufferManager.GetMappedPointer(_counterReadbackBuffers[frameIndex]);
            _completedCounters[frameIndex] = SceneSubmissionCounterSnapshot.FromCounters(*counters);
            _counterReadbackRecorded[frameIndex] = false;
        }

        public SceneSubmissionCounterSnapshot GetLastCompletedCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _completedCounters[frameIndex];
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            int simpleCapacity = Math.Max(0, sceneData.SimpleOpaqueMeshletCount);
            int simpleNormalCapacity = Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount);
            int fullCapacity = Math.Max(0, sceneData.FullOpaqueMeshletCount);
            int dispatchCandidateCount = Math.Max(simpleCapacity, Math.Max(simpleNormalCapacity, fullCapacity));
            if (dispatchCandidateCount <= 0)
            {
                sceneData.ForwardVisibilityCompactionActive = false;
                sceneData.ForwardVisibilityCompactionSkipReason = "no forward visibility candidates";
                return;
            }

            EnsureRuntimeBuffers(frameIndex, simpleCapacity, simpleNormalCapacity, fullCapacity);
            RuntimeBuffer simpleVisible = _simpleVisibleDrawBuffers[frameIndex];
            RuntimeBuffer simpleNormalVisible = _simpleNormalVisibleDrawBuffers[frameIndex];
            RuntimeBuffer fullVisible = _fullVisibleDrawBuffers[frameIndex];
            RuntimeBuffer counterBuffer = _counterBuffers[frameIndex];
            RuntimeBuffer indirectDispatchBuffer = _indirectDispatchBuffers[frameIndex];
            if (!simpleVisible.Handle.IsValid ||
                !simpleNormalVisible.Handle.IsValid ||
                !fullVisible.Handle.IsValid ||
                !counterBuffer.Handle.IsValid ||
                !indirectDispatchBuffer.Handle.IsValid)
            {
                sceneData.ForwardVisibilityCompactionActive = false;
                sceneData.ForwardVisibilityCompactionSkipReason = "forward visibility buffers unavailable";
                return;
            }

            sceneData.ForwardVisibilityCompactionActive = true;
            sceneData.ForwardVisibilityCompactionSkipReason = string.Empty;
            sceneData.ForwardVisibilitySimpleCapacity = (int)Math.Min(simpleVisible.ElementCapacity, int.MaxValue);
            sceneData.ForwardVisibilitySimpleNormalCapacity = (int)Math.Min(simpleNormalVisible.ElementCapacity, int.MaxValue);
            sceneData.ForwardVisibilityFullCapacity = (int)Math.Min(fullVisible.ElementCapacity, int.MaxValue);
            sceneData.ForwardVisibilityCounterBuffer = counterBuffer.Handle;
            sceneData.ForwardVisibilityIndirectDispatchBuffer = indirectDispatchBuffer.Handle;
            sceneData.ForwardVisibilityBufferBytes = checked(
                simpleVisible.ByteSize +
                simpleNormalVisible.ByteSize +
                fullVisible.ByteSize +
                counterBuffer.ByteSize +
                indirectDispatchBuffer.ByteSize);

            ResetOutputs(cmd, simpleVisible, simpleNormalVisible, fullVisible, counterBuffer, indirectDispatchBuffer);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _meshPipeline.ForwardVisibilityCompactionPipeline);
            var descriptorSets = stackalloc DescriptorSet[2];
            descriptorSets[0] = _bindlessHeap.StorageBufferSet;
            descriptorSets[1] = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _meshPipeline.SceneSubmissionComputeLayout,
                0,
                2,
                descriptorSets,
                0,
                null);

            var pushConstants = new GPUForwardVisibilityCompactionPushConstants
            {
                CurrentFrameIndex = (uint)frameIndex,
                SimpleInputCapacity = checked((uint)Math.Max(0, simpleCapacity)),
                SimpleNormalInputCapacity = checked((uint)Math.Max(0, simpleNormalCapacity)),
                FullInputCapacity = checked((uint)Math.Max(0, fullCapacity)),
                SimpleOutputCapacity = simpleVisible.ElementCapacity,
                SimpleNormalOutputCapacity = simpleNormalVisible.ElementCapacity,
                FullOutputCapacity = fullVisible.ElementCapacity,
                InputCounterBufferBaseIndex = (uint)BindlessIndex.SceneSubmissionCounterBufferBase,
                OutputCounterBufferBaseIndex = (uint)BindlessIndex.ForwardVisibilityCounterBufferBase,
                InputSimpleBufferBaseIndex = (uint)BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                InputSimpleNormalBufferBaseIndex = (uint)BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                InputFullBufferBaseIndex = (uint)BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                OutputSimpleBufferBaseIndex = (uint)BindlessIndex.ForwardVisibleSimpleOpaqueMeshletDrawBufferBase,
                OutputSimpleNormalBufferBaseIndex = (uint)BindlessIndex.ForwardVisibleSimpleNormalOpaqueMeshletDrawBufferBase,
                OutputFullBufferBaseIndex = (uint)BindlessIndex.ForwardVisibleFullOpaqueMeshletDrawBufferBase,
                IndirectDispatchBufferBaseIndex = (uint)BindlessIndex.ForwardVisibilityIndirectDispatchBufferBase,
                ScreenDimensions = new Njulf.Core.Math.Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                HiZTextureIndex = (uint)BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias
            };
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.SceneSubmissionComputeLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUForwardVisibilityCompactionPushConstants>(),
                &pushConstants);

            uint groupCountX = Math.Max(1u, (checked((uint)dispatchCandidateCount) + WorkgroupSize - 1u) / WorkgroupSize);
            _context.Api.CmdDispatch(cmd, groupCountX, 1, 1);
            RecordOutputBarrier(cmd, simpleVisible, simpleNormalVisible, fullVisible, counterBuffer, indirectDispatchBuffer);
            RecordCounterReadback(cmd, frameIndex, counterBuffer);
        }

        public static ulong GetSimpleOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(SimpleOpaqueIndirectDispatchSlot);
        }

        public static ulong GetSimpleNormalOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(SimpleNormalOpaqueIndirectDispatchSlot);
        }

        public static ulong GetFullOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(FullOpaqueIndirectDispatchSlot);
        }

        private static ulong GetIndirectDispatchOffset(int slot)
        {
            return checked((ulong)slot * IndirectDispatchStride);
        }

        private void EnsureRuntimeBuffers(int frameIndex, int simpleCapacity, int simpleNormalCapacity, int fullCapacity)
        {
            ValidateFrameIndex(frameIndex);
            EnsureCapacity(
                ref _simpleVisibleDrawBuffers[frameIndex],
                checked((uint)Math.Max(1, simpleCapacity)),
                DrawCommandStride,
                $"ForwardVisibility.SimpleOpaqueVisibleMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _simpleNormalVisibleDrawBuffers[frameIndex],
                checked((uint)Math.Max(1, simpleNormalCapacity)),
                DrawCommandStride,
                $"ForwardVisibility.SimpleNormalOpaqueVisibleMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _fullVisibleDrawBuffers[frameIndex],
                checked((uint)Math.Max(1, fullCapacity)),
                DrawCommandStride,
                $"ForwardVisibility.FullOpaqueVisibleMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _counterBuffers[frameIndex],
                1u,
                CounterStride,
                $"ForwardVisibility.Counter.Frame{frameIndex}");
            EnsureCapacity(
                ref _indirectDispatchBuffers[frameIndex],
                IndirectDispatchSlotCount,
                IndirectDispatchStride,
                $"ForwardVisibility.IndirectDispatch.Frame{frameIndex}",
                BufferUsageFlags.IndirectBufferBit);
            UpdateRegisteredBindlessBuffers(frameIndex);
        }

        private void EnsureCapacity(
            ref RuntimeBuffer buffer,
            uint requiredElements,
            ulong stride,
            string debugName,
            BufferUsageFlags extraUsage = 0)
        {
            uint required = Math.Max(1u, requiredElements);
            if (buffer.Handle.IsValid && required <= buffer.ElementCapacity)
                return;

            uint newCapacity = buffer.Handle.IsValid ? buffer.ElementCapacity : 1u;
            while (newCapacity < required)
                newCapacity = checked(newCapacity * 2u);

            DestroyIfValid(buffer.Handle);
            ulong byteSize = checked(newCapacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | extraUsage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                $"{debugName} ({newCapacity} elements)");
            _context.SetDebugName(_bufferManager.GetBuffer(handle).Handle, ObjectType.Buffer, debugName);
            buffer = new RuntimeBuffer(handle, newCapacity, byteSize);
        }

        private void UpdateRegisteredBindlessBuffers(int frameIndex)
        {
            RegisterStorageBuffer(BindlessIndex.ForwardVisibleSimpleOpaqueMeshletDrawBufferBase + frameIndex, _simpleVisibleDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.ForwardVisibleSimpleNormalOpaqueMeshletDrawBufferBase + frameIndex, _simpleNormalVisibleDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.ForwardVisibleFullOpaqueMeshletDrawBufferBase + frameIndex, _fullVisibleDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.ForwardVisibilityCounterBufferBase + frameIndex, _counterBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.ForwardVisibilityIndirectDispatchBufferBase + frameIndex, _indirectDispatchBuffers[frameIndex].Handle);
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            if (!handle.IsValid)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _bindlessHeap.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private void ResetOutputs(
            CommandBuffer cmd,
            RuntimeBuffer simpleVisible,
            RuntimeBuffer simpleNormalVisible,
            RuntimeBuffer fullVisible,
            RuntimeBuffer counterBuffer,
            RuntimeBuffer indirectDispatchBuffer)
        {
            VkBuffer simple = _bufferManager.GetBuffer(simpleVisible.Handle);
            VkBuffer simpleNormal = _bufferManager.GetBuffer(simpleNormalVisible.Handle);
            VkBuffer full = _bufferManager.GetBuffer(fullVisible.Handle);
            VkBuffer counters = _bufferManager.GetBuffer(counterBuffer.Handle);
            VkBuffer indirect = _bufferManager.GetBuffer(indirectDispatchBuffer.Handle);
            _context.Api.CmdFillBuffer(cmd, counters, 0, counterBuffer.ByteSize, 0u);
            _context.Api.CmdFillBuffer(cmd, simple, 0, simpleVisible.ByteSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(cmd, simpleNormal, 0, simpleNormalVisible.ByteSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(cmd, full, 0, fullVisible.ByteSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(cmd, indirect, 0, indirectDispatchBuffer.ByteSize, 0u);
            for (uint slot = 0; slot < IndirectDispatchSlotCount; slot++)
            {
                ulong slotOffset = slot * IndirectDispatchStride;
                _context.Api.CmdFillBuffer(cmd, indirect, slotOffset + 4, 4, 1u);
                _context.Api.CmdFillBuffer(cmd, indirect, slotOffset + 8, 4, 1u);
            }

            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[5];
            barriers[0] = TransferToComputeBarrier(counters, counterBuffer.ByteSize);
            barriers[1] = TransferToComputeBarrier(simple, simpleVisible.ByteSize);
            barriers[2] = TransferToComputeBarrier(simpleNormal, simpleNormalVisible.ByteSize);
            barriers[3] = TransferToComputeBarrier(full, fullVisible.ByteSize);
            barriers[4] = TransferToComputeBarrier(indirect, indirectDispatchBuffer.ByteSize);
            ExecuteBarriers(cmd, barriers);
        }

        private void RecordOutputBarrier(
            CommandBuffer cmd,
            RuntimeBuffer simpleVisible,
            RuntimeBuffer simpleNormalVisible,
            RuntimeBuffer fullVisible,
            RuntimeBuffer counterBuffer,
            RuntimeBuffer indirectDispatchBuffer)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[5];
            barriers[0] = ComputeToTaskAndTransferBarrier(_bufferManager.GetBuffer(counterBuffer.Handle), counterBuffer.ByteSize);
            barriers[1] = ComputeToTaskAndTransferBarrier(_bufferManager.GetBuffer(simpleVisible.Handle), simpleVisible.ByteSize);
            barriers[2] = ComputeToTaskAndTransferBarrier(_bufferManager.GetBuffer(simpleNormalVisible.Handle), simpleNormalVisible.ByteSize);
            barriers[3] = ComputeToTaskAndTransferBarrier(_bufferManager.GetBuffer(fullVisible.Handle), fullVisible.ByteSize);
            barriers[4] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(indirectDispatchBuffer.Handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.DrawIndirectBit,
                AccessFlags2.IndirectCommandReadBit,
                0,
                indirectDispatchBuffer.ByteSize);
            ExecuteBarriers(cmd, barriers);
        }

        private static BufferMemoryBarrier2 TransferToComputeBarrier(VkBuffer buffer, ulong byteSize)
        {
            return BarrierBuilder.BufferBarrier(
                buffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                byteSize);
        }

        private static BufferMemoryBarrier2 ComputeToTaskAndTransferBarrier(VkBuffer buffer, ulong byteSize)
        {
            return BarrierBuilder.BufferBarrier(
                buffer,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                0,
                byteSize);
        }

        private void EnsureCounterReadbackBuffer(int frameIndex)
        {
            if (_counterReadbackBuffers[frameIndex].IsValid)
                return;

            _counterReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                CounterStride,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"ForwardVisibility.CounterReadback.Frame{frameIndex}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }

        private void RecordCounterReadback(CommandBuffer cmd, int frameIndex, RuntimeBuffer counterBuffer)
        {
            EnsureCounterReadbackBuffer(frameIndex);
            VkBuffer source = _bufferManager.GetBuffer(counterBuffer.Handle);
            VkBuffer destination = _bufferManager.GetBuffer(_counterReadbackBuffers[frameIndex]);

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = CounterStride
            };
            _context.Api.CmdCopyBuffer(cmd, source, destination, 1, &copy);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                CounterStride);
            ExecuteBarrier(cmd, afterCopy);
            _counterReadbackRecorded[frameIndex] = true;
        }

        private void ExecuteBarrier(CommandBuffer cmd, BufferMemoryBarrier2 barrier)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private void ExecuteBarriers(CommandBuffer cmd, ReadOnlySpan<BufferMemoryBarrier2> barriers)
        {
            fixed (BufferMemoryBarrier2* pBarriers = barriers)
            {
                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = (uint)barriers.Length,
                    PBufferMemoryBarriers = pBarriers
                };
                _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
            }
        }

        public override void Cleanup()
        {
            for (int i = 0; i < _simpleVisibleDrawBuffers.Length; i++)
            {
                DestroyIfValid(_simpleVisibleDrawBuffers[i].Handle);
                _simpleVisibleDrawBuffers[i] = default;
                DestroyIfValid(_simpleNormalVisibleDrawBuffers[i].Handle);
                _simpleNormalVisibleDrawBuffers[i] = default;
                DestroyIfValid(_fullVisibleDrawBuffers[i].Handle);
                _fullVisibleDrawBuffers[i] = default;
                DestroyIfValid(_counterBuffers[i].Handle);
                _counterBuffers[i] = default;
                DestroyIfValid(_indirectDispatchBuffers[i].Handle);
                _indirectDispatchBuffers[i] = default;
                DestroyIfValid(_counterReadbackBuffers[i]);
                _counterReadbackBuffers[i] = BufferHandle.Invalid;
                _counterReadbackRecorded[i] = false;
                _completedCounters[i] = SceneSubmissionCounterSnapshot.Invalid;
            }
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private static void ValidateFrameIndex(int frameIndex)
        {
            if ((uint)frameIndex >= RenderingConstants.FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, "Frame index is outside the frames-in-flight range.");
        }

        private readonly struct RuntimeBuffer
        {
            public RuntimeBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize)
            {
                Handle = handle;
                ElementCapacity = elementCapacity;
                ByteSize = byteSize;
            }

            public BufferHandle Handle { get; }
            public uint ElementCapacity { get; }
            public ulong ByteSize { get; }
        }
    }
}
