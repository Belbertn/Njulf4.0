using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class MaterialManager : IDisposable
    {
        private const uint InitialMaterialCapacity = 1024;
        private static readonly ulong MaterialStride = (ulong)Marshal.SizeOf<GPUMaterialData>();

        private readonly VulkanContext? _context;
        private readonly BufferManager? _bufferManager;
        private readonly StagingRing? _stagingRing;
        private readonly SynchronizationManager? _sync;
        private readonly TextureManager? _textureManager;
        private readonly object _lock = new object();
        private readonly List<MaterialSlot> _materials = new List<MaterialSlot>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly Dictionary<GPUMaterialData, MaterialHandle> _deduplicatedMaterials =
            new Dictionary<GPUMaterialData, MaterialHandle>(new MaterialDataComparer());

        private BufferHandle _materialBuffer = BufferHandle.Invalid;
        private uint _materialBufferCapacity;
        private bool _gpuUploadDirty = true;
        private BindlessHeap? _registeredBindlessHeap;
        private bool _disposed;

        public MaterialManager()
            : this(context: null, bufferManager: null, stagingRing: null, sync: null, textureManager: null, cpuOnly: true)
        {
        }

        public MaterialManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync)
            : this(
                context ?? throw new ArgumentNullException(nameof(context)),
                bufferManager ?? throw new ArgumentNullException(nameof(bufferManager)),
                stagingRing ?? throw new ArgumentNullException(nameof(stagingRing)),
                sync ?? throw new ArgumentNullException(nameof(sync)),
                textureManager: null,
                cpuOnly: false)
        {
        }

        public MaterialManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            TextureManager textureManager)
            : this(
                context ?? throw new ArgumentNullException(nameof(context)),
                bufferManager ?? throw new ArgumentNullException(nameof(bufferManager)),
                stagingRing ?? throw new ArgumentNullException(nameof(stagingRing)),
                sync ?? throw new ArgumentNullException(nameof(sync)),
                textureManager ?? throw new ArgumentNullException(nameof(textureManager)),
                cpuOnly: false)
        {
        }

        private MaterialManager(
            VulkanContext? context,
            BufferManager? bufferManager,
            StagingRing? stagingRing,
            SynchronizationManager? sync,
            TextureManager? textureManager,
            bool cpuOnly)
        {
            _context = context;
            _bufferManager = bufferManager;
            _stagingRing = stagingRing;
            _sync = sync;
            _textureManager = textureManager;

            DefaultMaterialHandle = RegisterMaterialInternal(
                CreateDefaultMaterial(),
                textureHandles: Array.Empty<TextureHandle>(),
                permanent: true);

            if (HasGpuServices)
                _materialBuffer = CreateMaterialBuffer(InitialMaterialCapacity);
        }

        public MaterialHandle DefaultMaterialHandle { get; }

        public BufferHandle MaterialBuffer => _materialBuffer;

        public ulong MaterialBufferSize
        {
            get
            {
                lock (_lock)
                    return _materialBufferCapacity * MaterialStride;
            }
        }

        public int RegisteredMaterialCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (MaterialSlot slot in _materials)
                    {
                        if (slot.Active)
                            count++;
                    }

                    return count;
                }
            }
        }

        public int UploadedMaterialCount
        {
            get
            {
                lock (_lock)
                    return _materials.Count;
            }
        }

        public MaterialManagerDiagnostics Diagnostics
        {
            get
            {
                lock (_lock)
                    return new MaterialManagerDiagnostics(RegisteredMaterialCount, UploadedMaterialCount);
            }
        }

        private bool HasGpuServices =>
            _context != null &&
            _bufferManager != null &&
            _stagingRing != null &&
            _sync != null;

        public MaterialHandle RegisterMaterial(
            GPUMaterialData material,
            IReadOnlyList<TextureHandle>? textureHandles = null)
        {
            lock (_lock)
            {
                ValidateMaterialTextureIndices(material);

                if (_deduplicatedMaterials.TryGetValue(material, out MaterialHandle existingHandle))
                {
                    MaterialSlot existing = GetValidatedSlotLocked(existingHandle);
                    existing.ReferenceCount++;
                    _materials[existingHandle.Index] = existing;
                    return existingHandle;
                }

                return RegisterMaterialInternal(material, textureHandles, permanent: false);
            }
        }

        public int ResolveMaterialIndex(MaterialHandle handle)
        {
            lock (_lock)
            {
                GetValidatedSlotLocked(handle);
                return handle.Index;
            }
        }

        public GPUMaterialData GetMaterialData(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).Data;
        }

        public IReadOnlyList<TextureHandle> GetMaterialTextures(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).TextureHandles;
        }

        public GPUMaterialData[] GetMaterialDataSnapshot()
        {
            lock (_lock)
            {
                var snapshot = new GPUMaterialData[_materials.Count];
                for (int i = 0; i < _materials.Count; i++)
                    snapshot[i] = _materials[i].Active ? _materials[i].Data : CreateDefaultMaterial();

                return snapshot;
            }
        }

        public void ReleaseMaterial(MaterialHandle handle, Fence retireFence = default)
        {
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (slot.Permanent)
                    return;

                slot.ReferenceCount--;
                if (slot.ReferenceCount > 0)
                {
                    _materials[handle.Index] = slot;
                    ReleaseMaterialTextures(slot, retireFence);
                    return;
                }

                DestroyMaterialSlotLocked(handle, slot, retireFence);
            }
        }

        public void DestroyMaterial(MaterialHandle handle, Fence retireFence = default)
        {
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (slot.Permanent)
                    throw new InvalidOperationException("The canonical default material cannot be destroyed.");

                DestroyMaterialSlotLocked(handle, slot, retireFence);
            }
        }

        public void UploadMaterials(CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for material upload.", nameof(commandBuffer));
            if (!HasGpuServices)
                throw new InvalidOperationException("Material GPU upload requires renderer GPU services.");

            lock (_lock)
            {
                EnsureMaterialBufferCapacityLocked((uint)Math.Max(1, _materials.Count));

                if (_gpuUploadDirty)
                {
                    GPUMaterialData[] snapshot = GetMaterialDataSnapshotLocked();
                    UploadSpan(snapshot, commandBuffer);
                    _gpuUploadDirty = false;
                }

                RecordMaterialReadBarrier(commandBuffer);
                UpdateRegisteredBindlessBuffer();
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));
            if (!_materialBuffer.IsValid)
                throw new InvalidOperationException("Material GPU buffer has not been created.");

            lock (_lock)
            {
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffer();
            }
        }

        private MaterialHandle RegisterMaterialInternal(
            GPUMaterialData material,
            IReadOnlyList<TextureHandle>? textureHandles,
            bool permanent)
        {
            int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _materials.Count;
            uint generation = AllocateGeneration(index);
            TextureHandle[] textureHandleArray = CopyTextureHandles(textureHandles);

            var slot = new MaterialSlot
            {
                Data = material,
                Generation = generation,
                Active = true,
                Permanent = permanent,
                ReferenceCount = 1,
                TextureHandles = textureHandleArray
            };

            if (index == _materials.Count)
                _materials.Add(slot);
            else
                _materials[index] = slot;

            var handle = new MaterialHandle(index, generation);
            _deduplicatedMaterials[material] = handle;
            _gpuUploadDirty = true;
            return handle;
        }

        private void DestroyMaterialSlotLocked(MaterialHandle handle, MaterialSlot slot, Fence retireFence)
        {
            _deduplicatedMaterials.Remove(slot.Data);
            ReleaseMaterialTextures(slot, retireFence);

            slot.Active = false;
            slot.ReferenceCount = 0;
            slot.Generation = NextGeneration(slot.Generation);
            slot.TextureHandles = Array.Empty<TextureHandle>();
            slot.Data = CreateDefaultMaterial();
            _materials[handle.Index] = slot;
            _freeIndices.Push(handle.Index);
            _gpuUploadDirty = true;
        }

        private void ReleaseMaterialTextures(MaterialSlot slot, Fence retireFence)
        {
            if (_textureManager == null)
                return;

            foreach (TextureHandle textureHandle in slot.TextureHandles)
                _textureManager.ReleaseTexture(textureHandle, retireFence);
        }

        private MaterialSlot GetValidatedSlotLocked(MaterialHandle handle)
        {
            if (!handle.IsValid)
                throw new InvalidOperationException("Invalid material handle.");
            if (handle.Index >= _materials.Count)
                throw new InvalidOperationException(
                    $"Material handle index {handle.Index} is outside the registered material table.");

            MaterialSlot slot = _materials[handle.Index];
            if (!slot.Active)
                throw new InvalidOperationException($"Material handle {handle} references a destroyed material.");
            if (slot.Generation != handle.Generation)
            {
                throw new InvalidOperationException(
                    $"Material handle generation mismatch for index {handle.Index}: " +
                    $"handle generation {handle.Generation}, current generation {slot.Generation}.");
            }

            return slot;
        }

        private void EnsureMaterialBufferCapacityLocked(uint requiredMaterialCount)
        {
            if (_bufferManager == null || requiredMaterialCount <= _materialBufferCapacity)
                return;

            WaitForOtherInFlightFrames();

            uint newCapacity = _materialBufferCapacity == 0 ? InitialMaterialCapacity : _materialBufferCapacity;
            while (newCapacity < requiredMaterialCount)
                newCapacity = checked(newCapacity * 2);

            BufferHandle oldBuffer = _materialBuffer;
            _materialBuffer = CreateMaterialBuffer(newCapacity);
            if (oldBuffer.IsValid)
                _bufferManager.DestroyBuffer(oldBuffer);

            UpdateRegisteredBindlessBuffer();
            _gpuUploadDirty = true;
        }

        private BufferHandle CreateMaterialBuffer(uint materialCapacity)
        {
            if (_bufferManager == null)
                throw new InvalidOperationException("Material GPU buffer creation requires a BufferManager.");

            _materialBufferCapacity = materialCapacity;
            return _bufferManager.CreateDeviceBuffer(
                checked(materialCapacity * MaterialStride),
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                true);
        }

        private void WaitForOtherInFlightFrames()
        {
            if (_sync == null || _stagingRing == null)
                return;

            int currentFrame = _stagingRing.CurrentFrameIndex;
            for (int i = 0; i < RenderingConstants.FramesInFlight; i++)
            {
                if (i != currentFrame)
                    _sync.WaitForFence(i);
            }
        }

        private void UploadSpan(ReadOnlySpan<GPUMaterialData> data, CommandBuffer commandBuffer)
        {
            if (data.IsEmpty || _bufferManager == null || _stagingRing == null || _context == null)
                return;

            ulong dataSize = checked((ulong)data.Length * (ulong)sizeof(GPUMaterialData));
            var (stagingBuffer, stagingOffset) = _stagingRing.Allocate(dataSize);
            void* mappedData = _bufferManager.GetMappedPointer(stagingBuffer);

            fixed (GPUMaterialData* source = data)
            {
                System.Buffer.MemoryCopy(
                    source,
                    (byte*)mappedData + stagingOffset,
                    dataSize,
                    dataSize);
            }

            _bufferManager.FlushBuffer(stagingBuffer, stagingOffset, dataSize);

            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = 0,
                Size = dataSize
            };

            _context.Api.CmdCopyBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(stagingBuffer),
                _bufferManager.GetBuffer(_materialBuffer),
                1,
                &copy);
        }

        private void RecordMaterialReadBarrier(CommandBuffer commandBuffer)
        {
            if (_context == null || _bufferManager == null || !_materialBuffer.IsValid)
                return;

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.MeshShaderBitExt |
                               PipelineStageFlags2.FragmentShaderBit |
                               PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_materialBuffer),
                Offset = 0,
                Size = Vk.WholeSize
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void UpdateRegisteredBindlessBuffer()
        {
            if (_registeredBindlessHeap == null || _bufferManager == null || !_materialBuffer.IsValid)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(_materialBuffer);
            _registeredBindlessHeap.RegisterStorageBuffer(BindlessIndex.MaterialDataBuffer, buffer, 0, Vk.WholeSize);
        }

        private GPUMaterialData[] GetMaterialDataSnapshotLocked()
        {
            var snapshot = new GPUMaterialData[_materials.Count];
            for (int i = 0; i < _materials.Count; i++)
                snapshot[i] = _materials[i].Active ? _materials[i].Data : CreateDefaultMaterial();

            return snapshot;
        }

        private uint AllocateGeneration(int index)
        {
            if (index == _materials.Count)
                return 1;

            return NextGeneration(_materials[index].Generation);
        }

        private static uint NextGeneration(uint generation)
        {
            generation++;
            return generation == 0 ? 1 : generation;
        }

        private static TextureHandle[] CopyTextureHandles(IReadOnlyList<TextureHandle>? textureHandles)
        {
            if (textureHandles == null || textureHandles.Count == 0)
                return Array.Empty<TextureHandle>();

            var copy = new TextureHandle[textureHandles.Count];
            for (int i = 0; i < textureHandles.Count; i++)
                copy[i] = textureHandles[i];

            return copy;
        }

        public static GPUMaterialData CreateDefaultMaterial()
        {
            return new GPUMaterialData
            {
                Albedo = new Vector4(1f, 1f, 1f, 1f),
                Emissive = Vector4.Zero,
                NormalScaleBias = new Vector4(1f, 0f, 0f, 0f),
                MetallicRoughnessAO = new Vector4(0f, 1f, 1f, 0f),
                TexCoordOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                AlbedoTextureIndex = BindlessIndex.DefaultWhiteTexture,
                NormalTextureIndex = BindlessIndex.DefaultNormalTexture,
                MetallicRoughnessTextureIndex = BindlessIndex.DefaultBlackTexture,
                EmissiveTextureIndex = BindlessIndex.DefaultBlackTexture
            };
        }

        public static void ValidateMaterialTextureIndices(GPUMaterialData material)
        {
            ValidateTextureIndex(material.AlbedoTextureIndex, nameof(GPUMaterialData.AlbedoTextureIndex));
            ValidateTextureIndex(material.NormalTextureIndex, nameof(GPUMaterialData.NormalTextureIndex));
            ValidateTextureIndex(material.MetallicRoughnessTextureIndex, nameof(GPUMaterialData.MetallicRoughnessTextureIndex));
            ValidateTextureIndex(material.EmissiveTextureIndex, nameof(GPUMaterialData.EmissiveTextureIndex));
        }

        private static void ValidateTextureIndex(int textureIndex, string fieldName)
        {
            if (!BindlessIndex.IsTextureIndex(textureIndex))
            {
                throw new InvalidOperationException(
                    $"{fieldName} contains invalid bindless texture index {textureIndex}. " +
                    $"Expected a value in [{BindlessIndex.FirstTextureIndex}, {BindlessIndex.MaxTextures - 1}].");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (_lock)
            {
                if (_materialBuffer.IsValid && _bufferManager != null)
                    _bufferManager.DestroyBuffer(_materialBuffer);

                _materialBuffer = BufferHandle.Invalid;
                _materials.Clear();
                _freeIndices.Clear();
                _deduplicatedMaterials.Clear();
            }
        }

        ~MaterialManager()
        {
            Dispose(false);
        }

        private struct MaterialSlot
        {
            public GPUMaterialData Data;
            public uint Generation;
            public bool Active;
            public bool Permanent;
            public int ReferenceCount;
            public TextureHandle[] TextureHandles;
        }

        public sealed class MaterialDataComparer : IEqualityComparer<GPUMaterialData>
        {
            public bool Equals(GPUMaterialData x, GPUMaterialData y)
            {
                return x.Albedo.Equals(y.Albedo) &&
                       x.Emissive.Equals(y.Emissive) &&
                       x.NormalScaleBias.Equals(y.NormalScaleBias) &&
                       x.MetallicRoughnessAO.Equals(y.MetallicRoughnessAO) &&
                       x.TexCoordOffsetScale.Equals(y.TexCoordOffsetScale) &&
                       x.AlbedoTextureIndex == y.AlbedoTextureIndex &&
                       x.NormalTextureIndex == y.NormalTextureIndex &&
                       x.MetallicRoughnessTextureIndex == y.MetallicRoughnessTextureIndex &&
                       x.EmissiveTextureIndex == y.EmissiveTextureIndex;
            }

            public int GetHashCode(GPUMaterialData obj)
            {
                return HashCode.Combine(
                    HashCode.Combine(obj.Albedo, obj.Emissive, obj.NormalScaleBias, obj.MetallicRoughnessAO),
                    obj.TexCoordOffsetScale,
                    obj.AlbedoTextureIndex,
                    obj.NormalTextureIndex,
                    obj.MetallicRoughnessTextureIndex,
                    obj.EmissiveTextureIndex);
            }
        }
    }

    public sealed record MaterialManagerDiagnostics(
        int RegisteredMaterialCount,
        int UploadedMaterialCount);
}
