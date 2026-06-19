using System;
using System.Collections.Generic;
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
    public sealed unsafe class SceneOpaqueCompactionPass : RenderPassBase
    {
        private const uint WorkgroupSize = 64;
        private const int MaxValidationSampleCommands = 4096;
        private static readonly ulong DrawCommandStride = (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>();
        private static readonly ulong CounterStride = (ulong)Marshal.SizeOf<GPUSceneSubmissionCounters>();
        private static readonly ulong IndirectDispatchStride = (ulong)Marshal.SizeOf<GPUFoliageDispatchArgs>();
        private static readonly ulong ValidationReadbackBytes = checked((ulong)MaxValidationSampleCommands * (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());

        private readonly MeshPipeline _meshPipeline;
        private readonly BufferManager _bufferManager;
        private readonly RuntimeBuffer[] _compactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _solidDepthCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _maskedDepthCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _counterBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _indirectDispatchBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly BufferHandle[] _counterReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly BufferHandle[] _validationReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly bool[] _counterReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private readonly bool[] _validationReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private readonly ValidationExpectedFrame[] _validationExpectedFrames =
        [
            ValidationExpectedFrame.Invalid,
            ValidationExpectedFrame.Invalid
        ];
        private readonly SceneSubmissionCounterSnapshot[] _completedCounters =
        [
            SceneSubmissionCounterSnapshot.Invalid,
            SceneSubmissionCounterSnapshot.Invalid
        ];
        private readonly SceneSubmissionValidationSnapshot[] _completedValidation =
        [
            SceneSubmissionValidationSnapshot.Invalid,
            SceneSubmissionValidationSnapshot.Invalid
        ];

        public SceneOpaqueCompactionPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            BufferManager bufferManager)
            : base("SceneOpaqueCompactionPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.SceneSubmissionGpuCompactionEnabled &&
                   sceneData.SceneSubmissionFallbackReason.Length == 0 &&
                   (sceneData.OpaqueMeshletCount > 0 ||
                    (sceneData.DepthPrePassEnabled &&
                     (sceneData.SolidMeshletCount > 0 ||
                      sceneData.MaskedMeshletCount > 0)));
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
                _completedValidation[frameIndex] = SceneSubmissionValidationSnapshot.Invalid;
                return;
            }

            _bufferManager.InvalidateBuffer(_counterReadbackBuffers[frameIndex], 0, CounterStride);
            GPUSceneSubmissionCounters* counters =
                (GPUSceneSubmissionCounters*)_bufferManager.GetMappedPointer(_counterReadbackBuffers[frameIndex]);
            _completedCounters[frameIndex] = SceneSubmissionCounterSnapshot.FromCounters(*counters);
            _completedValidation[frameIndex] = ReadCompletedValidation(frameIndex, _completedCounters[frameIndex]);
            _counterReadbackRecorded[frameIndex] = false;
            _validationReadbackRecorded[frameIndex] = false;
        }

        public SceneSubmissionCounterSnapshot GetLastCompletedCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _completedCounters[frameIndex];
        }

        public SceneSubmissionValidationSnapshot GetLastCompletedValidation(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _completedValidation[frameIndex];
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            int candidateCount = checked(sceneData.SimpleOpaqueMeshletCount + sceneData.FullOpaqueMeshletCount);
            int solidDepthCandidateCount = sceneData.DepthPrePassEnabled ? sceneData.SolidMeshletCount : 0;
            int maskedDepthCandidateCount = sceneData.DepthPrePassEnabled ? sceneData.MaskedMeshletCount : 0;
            int dispatchCandidateCount = Math.Max(candidateCount, Math.Max(solidDepthCandidateCount, maskedDepthCandidateCount));
            if (dispatchCandidateCount <= 0)
                return;

            EnsureRuntimeBuffers(frameIndex, candidateCount, solidDepthCandidateCount, maskedDepthCandidateCount);
            RuntimeBuffer drawBuffer = _compactedDrawBuffers[frameIndex];
            RuntimeBuffer solidDepthDrawBuffer = _solidDepthCompactedDrawBuffers[frameIndex];
            RuntimeBuffer maskedDepthDrawBuffer = _maskedDepthCompactedDrawBuffers[frameIndex];
            RuntimeBuffer counterBuffer = _counterBuffers[frameIndex];
            RuntimeBuffer indirectDispatchBuffer = _indirectDispatchBuffers[frameIndex];
            if (!drawBuffer.Handle.IsValid ||
                !solidDepthDrawBuffer.Handle.IsValid ||
                !maskedDepthDrawBuffer.Handle.IsValid ||
                !counterBuffer.Handle.IsValid ||
                !indirectDispatchBuffer.Handle.IsValid)
                return;

            sceneData.SceneSubmissionGpuCompactionActive = true;
            sceneData.SceneSubmissionGpuOpaqueCandidateCount = candidateCount;
            sceneData.SceneSubmissionGpuCompactedOpaqueCapacity = (int)Math.Min(drawBuffer.ElementCapacity, int.MaxValue);
            sceneData.SceneSubmissionGpuDepthSolidCandidateCount = solidDepthCandidateCount;
            sceneData.SceneSubmissionGpuDepthMaskedCandidateCount = maskedDepthCandidateCount;
            sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity = (int)Math.Min(solidDepthDrawBuffer.ElementCapacity, int.MaxValue);
            sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity = (int)Math.Min(maskedDepthDrawBuffer.ElementCapacity, int.MaxValue);
            sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBuffer = drawBuffer.Handle;
            sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBuffer = solidDepthDrawBuffer.Handle;
            sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBuffer = maskedDepthDrawBuffer.Handle;
            sceneData.SceneSubmissionCounterBuffer = counterBuffer.Handle;
            sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer = indirectDispatchBuffer.Handle;
            sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize = drawBuffer.ByteSize;
            sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize = solidDepthDrawBuffer.ByteSize;
            sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize = maskedDepthDrawBuffer.ByteSize;
            sceneData.SceneSubmissionCounterBufferSize = counterBuffer.ByteSize;
            sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize = indirectDispatchBuffer.ByteSize;

            ResetOutputs(cmd, drawBuffer, solidDepthDrawBuffer, maskedDepthDrawBuffer, counterBuffer, indirectDispatchBuffer);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _meshPipeline.SceneOpaqueCompactionPipeline);
            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _meshPipeline.SceneSubmissionComputeLayout,
                0,
                1,
                &storageSet,
                0,
                null);

            var pushConstants = new GPUSceneOpaqueCompactionPushConstants
            {
                CameraPosition = new Njulf.Core.Math.Vector4(
                    sceneData.CameraPosition.X,
                    sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z,
                    0.0f),
                CurrentFrameIndex = (uint)frameIndex,
                SimpleCandidateCount = checked((uint)Math.Max(0, sceneData.SimpleOpaqueMeshletCount)),
                FullCandidateCount = checked((uint)Math.Max(0, sceneData.FullOpaqueMeshletCount)),
                OutputCapacity = drawBuffer.ElementCapacity,
                SolidDepthCandidateCount = checked((uint)Math.Max(0, solidDepthCandidateCount)),
                MaskedDepthCandidateCount = checked((uint)Math.Max(0, maskedDepthCandidateCount)),
                SolidDepthOutputCapacity = solidDepthDrawBuffer.ElementCapacity,
                MaskedDepthOutputCapacity = maskedDepthDrawBuffer.ElementCapacity,
                OutputBufferBaseIndex = (uint)BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase,
                CounterBufferBaseIndex = (uint)BindlessIndex.SceneSubmissionCounterBufferBase,
                Flags = sceneData.SceneSubmissionGpuLodSelectionEnabled ? 3u : 1u,
                IndirectDispatchBufferBaseIndex = (uint)BindlessIndex.SceneOpaqueIndirectDispatchBufferBase,
                SolidDepthOutputBufferBaseIndex = (uint)BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase,
                MaskedDepthOutputBufferBaseIndex = (uint)BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase
            };
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.SceneSubmissionComputeLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
                &pushConstants);

            uint groupCountX = Math.Max(1u, (checked((uint)dispatchCandidateCount) + WorkgroupSize - 1u) / WorkgroupSize);
            _context.Api.CmdDispatch(cmd, groupCountX, 1, 1);
            RecordOutputBarrier(cmd, drawBuffer, solidDepthDrawBuffer, maskedDepthDrawBuffer, counterBuffer, indirectDispatchBuffer);
            RecordCounterReadback(cmd, frameIndex, counterBuffer);
            if (sceneData.SceneSubmissionValidationCompareCpuGpuLists)
            {
                CaptureExpectedValidationFrame(frameIndex, sceneData);
                RecordValidationReadback(cmd, frameIndex, drawBuffer);
            }
            else
            {
                _validationExpectedFrames[frameIndex] = ValidationExpectedFrame.Invalid;
                _validationReadbackRecorded[frameIndex] = false;
            }
        }

        private void EnsureRuntimeBuffers(
            int frameIndex,
            int candidateCount,
            int solidDepthCandidateCount,
            int maskedDepthCandidateCount)
        {
            ValidateFrameIndex(frameIndex);
            uint required = checked((uint)Math.Max(1, candidateCount));
            uint requiredSolidDepth = checked((uint)Math.Max(1, solidDepthCandidateCount));
            uint requiredMaskedDepth = checked((uint)Math.Max(1, maskedDepthCandidateCount));
            EnsureCapacity(
                ref _compactedDrawBuffers[frameIndex],
                required,
                DrawCommandStride,
                $"SceneSubmission.OpaqueCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _solidDepthCompactedDrawBuffers[frameIndex],
                requiredSolidDepth,
                DrawCommandStride,
                $"SceneSubmission.SolidDepthCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _maskedDepthCompactedDrawBuffers[frameIndex],
                requiredMaskedDepth,
                DrawCommandStride,
                $"SceneSubmission.MaskedDepthCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _counterBuffers[frameIndex],
                1u,
                CounterStride,
                $"SceneSubmission.Counter.Frame{frameIndex}");
            EnsureCapacity(
                ref _indirectDispatchBuffers[frameIndex],
                1u,
                IndirectDispatchStride,
                $"SceneSubmission.OpaqueIndirectDispatch.Frame{frameIndex}",
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
            RegisterStorageBuffer(BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase + frameIndex, _compactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase + frameIndex, _solidDepthCompactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase + frameIndex, _maskedDepthCompactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneSubmissionCounterBufferBase + frameIndex, _counterBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneOpaqueIndirectDispatchBufferBase + frameIndex, _indirectDispatchBuffers[frameIndex].Handle);
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
            RuntimeBuffer drawBuffer,
            RuntimeBuffer solidDepthDrawBuffer,
            RuntimeBuffer maskedDepthDrawBuffer,
            RuntimeBuffer counterBuffer,
            RuntimeBuffer indirectDispatchBuffer)
        {
            VkBuffer draw = _bufferManager.GetBuffer(drawBuffer.Handle);
            VkBuffer solidDepthDraw = _bufferManager.GetBuffer(solidDepthDrawBuffer.Handle);
            VkBuffer maskedDepthDraw = _bufferManager.GetBuffer(maskedDepthDrawBuffer.Handle);
            VkBuffer counters = _bufferManager.GetBuffer(counterBuffer.Handle);
            VkBuffer indirect = _bufferManager.GetBuffer(indirectDispatchBuffer.Handle);
            _context.Api.CmdFillBuffer(cmd, counters, 0, counterBuffer.ByteSize, 0u);
            _context.Api.CmdFillBuffer(cmd, draw, 0, drawBuffer.ByteSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(cmd, solidDepthDraw, 0, solidDepthDrawBuffer.ByteSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(cmd, maskedDepthDraw, 0, maskedDepthDrawBuffer.ByteSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(cmd, indirect, 0, indirectDispatchBuffer.ByteSize, 0u);
            _context.Api.CmdFillBuffer(cmd, indirect, 4, 4, 1u);
            _context.Api.CmdFillBuffer(cmd, indirect, 8, 4, 1u);

            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[5];
            barriers[0] = BarrierBuilder.BufferBarrier(
                counters,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                counterBuffer.ByteSize);
            barriers[1] = BarrierBuilder.BufferBarrier(
                draw,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                drawBuffer.ByteSize);
            barriers[2] = BarrierBuilder.BufferBarrier(
                solidDepthDraw,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                solidDepthDrawBuffer.ByteSize);
            barriers[3] = BarrierBuilder.BufferBarrier(
                maskedDepthDraw,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                maskedDepthDrawBuffer.ByteSize);
            barriers[4] = BarrierBuilder.BufferBarrier(
                indirect,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                indirectDispatchBuffer.ByteSize);
            ExecuteBarriers(cmd, barriers);
        }

        private void RecordOutputBarrier(
            CommandBuffer cmd,
            RuntimeBuffer drawBuffer,
            RuntimeBuffer solidDepthDrawBuffer,
            RuntimeBuffer maskedDepthDrawBuffer,
            RuntimeBuffer counterBuffer,
            RuntimeBuffer indirectDispatchBuffer)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[5];
            barriers[0] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(counterBuffer.Handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                0,
                counterBuffer.ByteSize);
            barriers[1] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(drawBuffer.Handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                0,
                drawBuffer.ByteSize);
            barriers[2] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(solidDepthDrawBuffer.Handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                0,
                solidDepthDrawBuffer.ByteSize);
            barriers[3] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(maskedDepthDrawBuffer.Handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                0,
                maskedDepthDrawBuffer.ByteSize);
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

        private void EnsureCounterReadbackBuffer(int frameIndex)
        {
            if (_counterReadbackBuffers[frameIndex].IsValid)
                return;

            _counterReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                CounterStride,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"SceneSubmission.CounterReadback.Frame{frameIndex}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }

        private void EnsureValidationReadbackBuffer(int frameIndex)
        {
            if (_validationReadbackBuffers[frameIndex].IsValid)
                return;

            _validationReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                ValidationReadbackBytes,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"SceneSubmission.ValidationReadback.Frame{frameIndex}",
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

        private void RecordValidationReadback(CommandBuffer cmd, int frameIndex, RuntimeBuffer drawBuffer)
        {
            EnsureValidationReadbackBuffer(frameIndex);
            VkBuffer source = _bufferManager.GetBuffer(drawBuffer.Handle);
            VkBuffer destination = _bufferManager.GetBuffer(_validationReadbackBuffers[frameIndex]);
            ulong copyBytes = Math.Min(drawBuffer.ByteSize, ValidationReadbackBytes);
            if (copyBytes == 0)
                return;

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = copyBytes
            };
            _context.Api.CmdCopyBuffer(cmd, source, destination, 1, &copy);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                copyBytes);
            ExecuteBarrier(cmd, afterCopy);
            _validationReadbackRecorded[frameIndex] = true;
        }

        private void CaptureExpectedValidationFrame(int frameIndex, SceneRenderingData sceneData)
        {
            int cpuCount = checked(sceneData.SimpleOpaqueMeshletCount + sceneData.FullOpaqueMeshletCount);
            int sampleCount = Math.Min(cpuCount, MaxValidationSampleCommands);
            var expected = new ValidationCommandKey[sampleCount];
            int writeIndex = 0;
            for (int i = 0; i < sceneData.SimpleOpaqueMeshletCount && writeIndex < sampleCount; i++)
            {
                expected[writeIndex++] = CreateValidationKey(
                    sceneData.MeshletDrawCommands[i],
                    sceneData.ObjectData,
                    ValidationPathBucket.SimpleOpaque);
            }

            for (int i = 0; i < sceneData.FullOpaqueMeshletDrawCommands.Count && writeIndex < sampleCount; i++)
            {
                expected[writeIndex++] = CreateValidationKey(
                    sceneData.FullOpaqueMeshletDrawCommands[i],
                    sceneData.ObjectData,
                    ValidationPathBucket.FullOpaque);
            }

            _validationExpectedFrames[frameIndex] = new ValidationExpectedFrame(
                true,
                cpuCount,
                sampleCount,
                sceneData.OcclusionCullingEnabled && sceneData.HiZMipCount > 0,
                sceneData.SceneSubmissionGpuLodSelectionEnabled,
                expected);
        }

        private SceneSubmissionValidationSnapshot ReadCompletedValidation(
            int frameIndex,
            SceneSubmissionCounterSnapshot counters)
        {
            ValidationExpectedFrame expectedFrame = _validationExpectedFrames[frameIndex];
            if (!expectedFrame.Valid)
                return SceneSubmissionValidationSnapshot.Invalid;

            if (!_validationReadbackRecorded[frameIndex] || !_validationReadbackBuffers[frameIndex].IsValid)
            {
                return new SceneSubmissionValidationSnapshot(
                    0,
                    "pending",
                    expectedFrame.CpuCount,
                    ClampUIntToInt(counters.EmittedCount),
                    0,
                    0,
                    MaxValidationSampleCommands,
                    "GPU validation readback is not available yet.");
            }

            int gpuCount = ClampUIntToInt(counters.EmittedCount);
            bool compareFullSample = expectedFrame.CpuCount <= MaxValidationSampleCommands &&
                                     gpuCount <= MaxValidationSampleCommands;
            int gpuSampleCount = compareFullSample
                ? Math.Min(gpuCount, expectedFrame.SampleCount)
                : 0;
            _bufferManager.InvalidateBuffer(_validationReadbackBuffers[frameIndex], 0, ValidationReadbackBytes);
            GPUMeshletDrawCommand* gpuCommands =
                (GPUMeshletDrawCommand*)_bufferManager.GetMappedPointer(_validationReadbackBuffers[frameIndex]);

            var gpuKeys = new ValidationCommandKey[gpuSampleCount];
            for (int i = 0; i < gpuSampleCount; i++)
                gpuKeys[i] = CreateValidationKey(gpuCommands[i], expectedFrame.ExpectedCommands, ValidationPathBucket.Unknown);

            return CompareValidationSamples(expectedFrame, counters, gpuKeys, gpuCount, compareFullSample);
        }

        private static SceneSubmissionValidationSnapshot CompareValidationSamples(
            ValidationExpectedFrame expectedFrame,
            SceneSubmissionCounterSnapshot counters,
            ValidationCommandKey[] gpuKeys,
            int gpuCount,
            bool compareFullSample)
        {
            int expectedSampleCount = compareFullSample ? expectedFrame.SampleCount : 0;
            var expectedKeys = new ValidationCommandKey[expectedSampleCount];
            if (expectedSampleCount > 0)
                Array.Copy(expectedFrame.ExpectedCommands, expectedKeys, expectedSampleCount);
            Array.Sort(expectedKeys);
            Array.Sort(gpuKeys);

            int compared = Math.Min(expectedKeys.Length, gpuKeys.Length);
            int mismatches = expectedFrame.CpuCount == gpuCount ? 0 : 1;
            string firstMismatch = expectedFrame.CpuCount == gpuCount
                ? string.Empty
                : $"count cpu={expectedFrame.CpuCount} gpu={gpuCount}";

            for (int i = 0; i < compared; i++)
            {
                if (expectedKeys[i].CommandEquals(gpuKeys[i]))
                    continue;

                mismatches++;
                if (firstMismatch.Length == 0)
                    firstMismatch = $"sample[{i}] cpu={expectedKeys[i]} gpu={gpuKeys[i]}";
            }

            int sampleDelta = Math.Abs(expectedKeys.Length - gpuKeys.Length);
            if (sampleDelta > 0)
            {
                mismatches += sampleDelta;
                if (firstMismatch.Length == 0)
                    firstMismatch = $"sample-count cpu={expectedKeys.Length} gpu={gpuKeys.Length}";
            }

            if (counters.OverflowCount > 0 && firstMismatch.Length == 0)
                firstMismatch = $"overflow={counters.OverflowCount}";

            string status;
            if (counters.OverflowCount > 0)
                status = "overflow";
            else if (!compareFullSample && mismatches == 0)
                status = expectedFrame.HiZEnabled
                    ? "count matched; sample over limit; Hi-Z not included"
                    : "count matched; sample over limit";
            else if (!compareFullSample)
                status = expectedFrame.HiZEnabled
                    ? "count mismatch; sample over limit; Hi-Z not included"
                    : "count mismatch; sample over limit";
            else if (mismatches == 0 && expectedFrame.CpuCount > MaxValidationSampleCommands)
                status = expectedFrame.HiZEnabled ? "sample matched; Hi-Z not included" : "sample matched";
            else if (mismatches == 0)
                status = expectedFrame.HiZEnabled ? "matched; Hi-Z not included" : "matched";
            else
                status = expectedFrame.HiZEnabled ? "mismatch; Hi-Z not included" : "mismatch";

            if (expectedFrame.GpuLodSelectionEnabled)
                status += "; GPU LOD active";

            return new SceneSubmissionValidationSnapshot(
                1,
                status,
                expectedFrame.CpuCount,
                gpuCount,
                compared,
                mismatches,
                MaxValidationSampleCommands,
                firstMismatch);
        }

        private static ValidationCommandKey CreateValidationKey(
            GPUMeshletDrawCommand command,
            IReadOnlyList<GPUObjectData> objectData,
            ValidationPathBucket bucket)
        {
            uint meshIndex = command.InstanceId < objectData.Count
                ? checked((uint)Math.Max(0, objectData[(int)command.InstanceId].MeshIndex))
                : uint.MaxValue;
            return new ValidationCommandKey(
                command.MeshletIndex,
                command.InstanceId,
                meshIndex,
                command.MaterialIndex,
                bucket);
        }

        private static ValidationCommandKey CreateValidationKey(
            GPUMeshletDrawCommand command,
            IReadOnlyList<ValidationCommandKey> expectedCommands,
            ValidationPathBucket fallbackBucket)
        {
            for (int i = 0; i < expectedCommands.Count; i++)
            {
                ValidationCommandKey expected = expectedCommands[i];
                if (expected.MeshletIndex == command.MeshletIndex &&
                    expected.InstanceId == command.InstanceId &&
                    expected.MaterialIndex == command.MaterialIndex)
                {
                    return expected;
                }
            }

            return new ValidationCommandKey(
                command.MeshletIndex,
                command.InstanceId,
                uint.MaxValue,
                command.MaterialIndex,
                fallbackBucket);
        }

        private static int ClampUIntToInt(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
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
            for (int i = 0; i < _compactedDrawBuffers.Length; i++)
            {
                DestroyIfValid(_compactedDrawBuffers[i].Handle);
                _compactedDrawBuffers[i] = default;
                DestroyIfValid(_solidDepthCompactedDrawBuffers[i].Handle);
                _solidDepthCompactedDrawBuffers[i] = default;
                DestroyIfValid(_maskedDepthCompactedDrawBuffers[i].Handle);
                _maskedDepthCompactedDrawBuffers[i] = default;
                DestroyIfValid(_counterBuffers[i].Handle);
                _counterBuffers[i] = default;
                DestroyIfValid(_indirectDispatchBuffers[i].Handle);
                _indirectDispatchBuffers[i] = default;
                DestroyIfValid(_counterReadbackBuffers[i]);
                _counterReadbackBuffers[i] = BufferHandle.Invalid;
                DestroyIfValid(_validationReadbackBuffers[i]);
                _validationReadbackBuffers[i] = BufferHandle.Invalid;
                _counterReadbackRecorded[i] = false;
                _validationReadbackRecorded[i] = false;
                _validationExpectedFrames[i] = ValidationExpectedFrame.Invalid;
                _completedCounters[i] = SceneSubmissionCounterSnapshot.Invalid;
                _completedValidation[i] = SceneSubmissionValidationSnapshot.Invalid;
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

        private enum ValidationPathBucket : uint
        {
            Unknown = 0,
            SimpleOpaque = 1,
            FullOpaque = 2
        }

        private readonly record struct ValidationExpectedFrame(
            bool Valid,
            int CpuCount,
            int SampleCount,
            bool HiZEnabled,
            bool GpuLodSelectionEnabled,
            ValidationCommandKey[] ExpectedCommands)
        {
            public static ValidationExpectedFrame Invalid { get; } = new(false, 0, 0, false, false, Array.Empty<ValidationCommandKey>());
        }

        private readonly record struct ValidationCommandKey(
            uint MeshletIndex,
            uint InstanceId,
            uint MeshIndex,
            uint MaterialIndex,
            ValidationPathBucket Bucket) : IComparable<ValidationCommandKey>
        {
            public bool CommandEquals(ValidationCommandKey other)
            {
                return MeshletIndex == other.MeshletIndex &&
                       InstanceId == other.InstanceId &&
                       MeshIndex == other.MeshIndex &&
                       MaterialIndex == other.MaterialIndex;
            }

            public int CompareTo(ValidationCommandKey other)
            {
                int meshlet = MeshletIndex.CompareTo(other.MeshletIndex);
                if (meshlet != 0)
                    return meshlet;
                int instance = InstanceId.CompareTo(other.InstanceId);
                if (instance != 0)
                    return instance;
                int mesh = MeshIndex.CompareTo(other.MeshIndex);
                if (mesh != 0)
                    return mesh;
                return MaterialIndex.CompareTo(other.MaterialIndex);
            }

            public override string ToString()
            {
                return $"obj={InstanceId}, mesh={MeshIndex}, meshlet={MeshletIndex}, mat={MaterialIndex}, bucket={Bucket}";
            }
        }
    }

    public readonly record struct SceneSubmissionCounterSnapshot(
        uint CandidateCount,
        uint EmittedCount,
        uint FrustumRejectedCount,
        uint OverflowCount,
        uint HiZTestedCount,
        uint HiZRejectedCount,
        uint Lod0EmittedCount,
        uint Lod1EmittedCount,
        uint Lod2EmittedCount,
        uint MissingLodFallbackCount,
        uint SolidDepthCandidateCount,
        uint SolidDepthEmittedCount,
        uint SolidDepthOverflowCount,
        uint MaskedDepthCandidateCount,
        uint MaskedDepthEmittedCount,
        uint MaskedDepthOverflowCount)
    {
        public bool IsValid =>
            CandidateCount != 0 ||
            EmittedCount != 0 ||
            FrustumRejectedCount != 0 ||
            OverflowCount != 0 ||
            SolidDepthCandidateCount != 0 ||
            SolidDepthEmittedCount != 0 ||
            SolidDepthOverflowCount != 0 ||
            MaskedDepthCandidateCount != 0 ||
            MaskedDepthEmittedCount != 0 ||
            MaskedDepthOverflowCount != 0;

        public static SceneSubmissionCounterSnapshot Invalid { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public static SceneSubmissionCounterSnapshot FromCounters(GPUSceneSubmissionCounters counters)
        {
            return new SceneSubmissionCounterSnapshot(
                counters.CandidateCount,
                counters.EmittedCount,
                counters.FrustumRejectedCount,
                counters.OverflowCount,
                counters.HiZTestedCount,
                counters.HiZRejectedCount,
                counters.Lod0EmittedCount,
                counters.Lod1EmittedCount,
                counters.Lod2EmittedCount,
                counters.MissingLodFallbackCount,
                counters.SolidDepthCandidateCount,
                counters.SolidDepthEmittedCount,
                counters.SolidDepthOverflowCount,
                counters.MaskedDepthCandidateCount,
                counters.MaskedDepthEmittedCount,
                counters.MaskedDepthOverflowCount);
        }
    }

    public readonly record struct SceneSubmissionValidationSnapshot(
        int Valid,
        string Status,
        int CpuOpaqueCount,
        int GpuOpaqueCount,
        int ComparedSampleCount,
        int MismatchCount,
        int SampleLimit,
        string FirstMismatch)
    {
        public static SceneSubmissionValidationSnapshot Invalid { get; } = new(0, string.Empty, 0, 0, 0, 0, 0, string.Empty);
    }
}
