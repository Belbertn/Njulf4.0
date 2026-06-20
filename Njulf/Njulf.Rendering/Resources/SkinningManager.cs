using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class SkinningManager : IDisposable
    {
        private const uint InitialMatrixCapacity = 4096;
        private const uint InitialDispatchCapacity = 1024;
        private const uint InitialSkinnedVertexCapacity = 65536;

        private static readonly ulong MatrixStride = (ulong)Marshal.SizeOf<Matrix4x4>();
        private static readonly ulong DispatchStride = (ulong)Marshal.SizeOf<GPUSkinningDispatch>();
        private static readonly ulong VertexStride = (ulong)Marshal.SizeOf<GPUVertex>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly StagingRing _stagingRing;
        private readonly MeshManager _meshManager;
        private readonly object _lock = new();

        private readonly SkinningBuffer[] _matrixBuffers = new SkinningBuffer[FramesInFlight];
        private readonly SkinningBuffer[] _dispatchBuffers = new SkinningBuffer[FramesInFlight];
        private readonly SkinningBuffer[] _skinnedVertexBuffers = new SkinningBuffer[FramesInFlight];
        private readonly List<Matrix4x4> _matrixScratch = new();
        private readonly List<GPUSkinningDispatch> _dispatchScratch = new();
        private BindlessHeap? _registeredBindlessHeap;
        private bool _disposed;

        public SkinningManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            MeshManager meshManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));

            for (int i = 0; i < FramesInFlight; i++)
            {
                _matrixBuffers[i] = CreateBuffer(InitialMatrixCapacity, MatrixStride, $"Skinning.MatrixBuffer.Frame{i}");
                _dispatchBuffers[i] = CreateBuffer(InitialDispatchCapacity, DispatchStride, $"Skinning.DispatchBuffer.Frame{i}");
                _skinnedVertexBuffers[i] = CreateBuffer(InitialSkinnedVertexCapacity, VertexStride, $"Skinning.OutputVertexBuffer.Frame{i}");
            }
        }

        public SkinningFrameStats PrepareFrame(Scene scene, CommandBuffer commandBuffer, bool enabled, int maxAnimatedInstances)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for skinning uploads.", nameof(commandBuffer));

            lock (_lock)
            {
                long start = Stopwatch.GetTimestamp();
                int frameIndex = _stagingRing.CurrentFrameIndex;
                _matrixScratch.Clear();
                _dispatchScratch.Clear();

                uint skinnedVertexOffset = 0;
                int skinnedObjects = 0;
                int jointMatrixCount = 0;

                foreach (RenderObject renderObject in scene.RenderObjects)
                {
                    if (renderObject is not SkinnedRenderObject skinned)
                        continue;

                    skinned.SkinningEnabled = false;
                    skinned.SkinnedVertexOffset = 0;

                    if (!enabled || !skinned.Enabled || !skinned.Visible)
                        continue;
                    if (maxAnimatedInstances >= 0 && skinnedObjects >= maxAnimatedInstances)
                        continue;
                    if (skinned.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                        continue;
                    if (skinned.Animator == null || skinned.SkinIndex < 0 || skinned.SkinIndex >= skinned.Animator.Skins.Count)
                        continue;

                    MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
                    if (!meshInfo.IsSkinned || meshInfo.SkinningDataCount != meshInfo.VertexCount)
                        continue;

                    ReadOnlySpan<Matrix4x4> matrices = skinned.Animator.GetSkinMatrices(skinned.SkinIndex);
                    if (matrices.IsEmpty)
                        continue;

                    uint matrixOffset = CheckedCount(_matrixScratch.Count);
                    for (int i = 0; i < matrices.Length; i++)
                        _matrixScratch.Add(ApplySkinningBindTransform(skinned.SkinningBindTransform, matrices[i]));

                    skinned.SkinningEnabled = true;
                    skinned.SkinnedVertexOffset = skinnedVertexOffset;
                    _dispatchScratch.Add(new GPUSkinningDispatch
                    {
                        SourceVertexOffset = meshInfo.VertexOffset,
                        SourceSkinningDataOffset = meshInfo.SkinningDataOffset,
                        DestinationVertexOffset = skinnedVertexOffset,
                        VertexCount = meshInfo.VertexCount,
                        SkinMatrixOffset = matrixOffset,
                        ObjectIndex = 0,
                        SourceMeshMetadataIndex = meshInfo.MeshMetadataOffset,
                        Flags = 0
                    });

                    skinnedVertexOffset = CheckedAdd(skinnedVertexOffset, meshInfo.VertexCount);
                    jointMatrixCount += matrices.Length;
                    skinnedObjects++;
                }

                long sampleMicroseconds = ElapsedMicroseconds(start);
                EnsureCapacity(ref _matrixBuffers[frameIndex], CheckedCount(_matrixScratch.Count), MatrixStride, $"Skinning.MatrixBuffer.Frame{frameIndex}");
                EnsureCapacity(ref _dispatchBuffers[frameIndex], CheckedCount(_dispatchScratch.Count), DispatchStride, $"Skinning.DispatchBuffer.Frame{frameIndex}");
                EnsureCapacity(ref _skinnedVertexBuffers[frameIndex], skinnedVertexOffset, VertexStride, $"Skinning.OutputVertexBuffer.Frame{frameIndex}");
                UpdateRegisteredBindlessBuffers();

                long uploadStart = Stopwatch.GetTimestamp();
                ulong uploadBytes = 0;
                uploadBytes += UploadSpan(CollectionsMarshal.AsSpan(_matrixScratch), _matrixBuffers[frameIndex].Handle, commandBuffer);
                uploadBytes += UploadSpan(CollectionsMarshal.AsSpan(_dispatchScratch), _dispatchBuffers[frameIndex].Handle, commandBuffer);
                RecordUploadToComputeBarriers(commandBuffer, frameIndex);
                long uploadMicroseconds = ElapsedMicroseconds(uploadStart);

                return new SkinningFrameStats(
                    skinnedObjects,
                    checked((int)skinnedVertexOffset),
                    _dispatchScratch.Count,
                    jointMatrixCount,
                    sampleMicroseconds,
                    uploadMicroseconds,
                    0,
                    uploadBytes,
                    _matrixBuffers[frameIndex].ByteSize,
                    _skinnedVertexBuffers[frameIndex].ByteSize,
                    _dispatchBuffers[frameIndex].Handle,
                    _skinnedVertexBuffers[frameIndex].Handle,
                    _dispatchScratch.ToArray());
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            lock (_lock)
            {
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffers();
            }
        }

        public BufferHandle GetSkinnedVertexBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _skinnedVertexBuffers[frameIndex].Handle;
        }

        internal static Matrix4x4 ApplySkinningBindTransform(Matrix4x4 bindTransform, Matrix4x4 skinMatrix)
        {
            return skinMatrix * bindTransform;
        }

        private SkinningBuffer CreateBuffer(uint elementCapacity, ulong stride, string debugName)
        {
            uint capacity = Math.Max(1u, elementCapacity);
            ulong byteSize = checked(capacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                debugName);
            _context.SetDebugName(_bufferManager.GetBuffer(handle).Handle, ObjectType.Buffer, debugName);
            return new SkinningBuffer(handle, capacity, byteSize);
        }

        private void EnsureCapacity(ref SkinningBuffer buffer, uint requiredElements, ulong stride, string debugName)
        {
            if (requiredElements <= buffer.ElementCapacity)
                return;

            uint newCapacity = buffer.ElementCapacity;
            do
            {
                newCapacity = checked(newCapacity * 2);
            }
            while (newCapacity < requiredElements);

            DestroyIfValid(buffer.Handle);
            buffer = CreateBuffer(newCapacity, stride, debugName);
        }

        private ulong UploadSpan<T>(ReadOnlySpan<T> data, BufferHandle destination, CommandBuffer commandBuffer)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return 0;

            return GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                destination,
                data).ByteCount;
        }

        private void RecordUploadToComputeBarriers(CommandBuffer commandBuffer, int frameIndex)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
            uint barrierCount = 0;

            if (_matrixScratch.Count > 0)
                barriers[barrierCount++] = CreateTransferToComputeReadBarrier(_matrixBuffers[frameIndex].Handle);
            if (_dispatchScratch.Count > 0)
                barriers[barrierCount++] = CreateTransferToComputeReadBarrier(_dispatchBuffers[frameIndex].Handle);
            if (barrierCount == 0)
                return;

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = barrierCount,
                PBufferMemoryBarriers = barriers
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private BufferMemoryBarrier2 CreateTransferToComputeReadBarrier(BufferHandle handle)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(handle),
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            if (_registeredBindlessHeap == null)
                return;

            RegisterStorageBuffer(BindlessIndex.SkinMatrixBufferBase, _matrixBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.SkinMatrixBufferFrame1, _matrixBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.SkinnedVertexBufferBase, _skinnedVertexBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.SkinnedVertexBufferFrame1, _skinnedVertexBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.SkinningDispatchBufferBase, _dispatchBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.SkinningDispatchBufferFrame1, _dispatchBuffers[1].Handle);
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _registeredBindlessHeap!.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private static uint CheckedCount(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (uint)count;
        }

        private static uint CheckedAdd(uint left, uint right)
        {
            ulong value = (ulong)left + right;
            if (value > uint.MaxValue)
                throw new InvalidOperationException("Skinning vertex offset exceeded uint range.");

            return (uint)value;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (_lock)
            {
                for (int i = 0; i < FramesInFlight; i++)
                {
                    DestroyIfValid(_matrixBuffers[i].Handle);
                    DestroyIfValid(_dispatchBuffers[i].Handle);
                    DestroyIfValid(_skinnedVertexBuffers[i].Handle);
                }

                _matrixScratch.Clear();
                _dispatchScratch.Clear();
            }
        }

        private readonly struct SkinningBuffer
        {
            public SkinningBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize)
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

    public readonly record struct SkinningFrameStats(
        int SkinnedObjectCount,
        int SkinnedVertexCount,
        int SkinningDispatchCount,
        int JointMatrixCount,
        long CpuAnimationSampleMicroseconds,
        long CpuSkinMatrixUploadMicroseconds,
        long CpuSkinningRecordMicroseconds,
        ulong SkinningUploadBytes,
        ulong SkinMatrixBufferSize,
        ulong SkinnedVertexBufferSize,
        BufferHandle SkinningDispatchBuffer,
        BufferHandle SkinnedVertexBuffer,
        GPUSkinningDispatch[] Dispatches);
}
