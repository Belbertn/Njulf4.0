using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class MaterialManager : IDisposable
    {
        private const uint InitialMaterialCapacity = 1024;
        private static readonly ulong MaterialStride = (ulong)Marshal.SizeOf<GPUMaterialData>();
        private static readonly ulong MaterialExtensionStride = (ulong)Marshal.SizeOf<GPUMaterialExtensionData>();

        private readonly VulkanContext? _context;
        private readonly BufferManager? _bufferManager;
        private readonly StagingRing? _stagingRing;
        private readonly SynchronizationManager? _sync;
        private readonly TextureManager? _textureManager;
        private readonly object _lock = new object();
        private readonly List<MaterialSlot> _materials = new List<MaterialSlot>();
        private readonly List<GPUMaterialExtensionData> _materialExtensions = new List<GPUMaterialExtensionData>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly Dictionary<MaterialRegistrationKey, MaterialHandle> _deduplicatedMaterials =
            new Dictionary<MaterialRegistrationKey, MaterialHandle>(new MaterialRegistrationKeyComparer());

        private BufferHandle _materialBuffer = BufferHandle.Invalid;
        private BufferHandle _materialExtensionBuffer = BufferHandle.Invalid;
        private uint _materialBufferCapacity;
        private uint _materialExtensionBufferCapacity;
        private uint _materialDataRevision;
        private bool _gpuUploadDirty = true;
        private ulong _lastUploadBytes;
        private ulong _lastExtensionUploadBytes;
        private long _lastUploadMicroseconds;
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
                extensionData: null,
                MaterialRenderMetadata.FromGpuMaterial(CreateDefaultMaterial()),
                textureHandles: Array.Empty<TextureHandle>(),
                permanent: true);

            if (HasGpuServices)
            {
                _materialBuffer = CreateMaterialBuffer(InitialMaterialCapacity);
                _materialExtensionBuffer = CreateMaterialExtensionBuffer(1);
            }
        }

        public MaterialHandle DefaultMaterialHandle { get; }

        public BufferHandle MaterialBuffer => _materialBuffer;

        public BufferHandle MaterialExtensionBuffer => _materialExtensionBuffer;

        public ulong MaterialBufferSize
        {
            get
            {
                lock (_lock)
                    return _materialBufferCapacity * MaterialStride;
            }
        }

        public ulong MaterialExtensionBufferSize
        {
            get
            {
                lock (_lock)
                    return _materialExtensionBufferCapacity * MaterialExtensionStride;
            }
        }

        public float MaterialBufferUtilization
        {
            get
            {
                lock (_lock)
                {
                    if (_materialBufferCapacity == 0)
                        return 0f;

                    return (float)_materials.Count / _materialBufferCapacity;
                }
            }
        }

        public int MaterialExtensionDataCount
        {
            get
            {
                lock (_lock)
                    return _materialExtensions.Count;
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

        public uint MaterialDataRevision
        {
            get
            {
                lock (_lock)
                    return _materialDataRevision;
            }
        }

        public MaterialManagerDiagnostics Diagnostics
        {
            get
            {
                lock (_lock)
                    return new MaterialManagerDiagnostics(RegisteredMaterialCount, UploadedMaterialCount, _materialExtensions.Count);
            }
        }

        public ulong LastUploadBytes
        {
            get
            {
                lock (_lock)
                    return _lastUploadBytes;
            }
        }

        public ulong LastExtensionUploadBytes
        {
            get
            {
                lock (_lock)
                    return _lastExtensionUploadBytes;
            }
        }

        public long LastUploadMicroseconds
        {
            get
            {
                lock (_lock)
                    return _lastUploadMicroseconds;
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
            return RegisterMaterial(material, extensionData: null, MaterialRenderMetadata.FromGpuMaterial(material), textureHandles);
        }

        public MaterialHandle RegisterMaterial(
            GPUMaterialData material,
            MaterialRenderMetadata metadata,
            IReadOnlyList<TextureHandle>? textureHandles = null)
        {
            return RegisterMaterial(material, extensionData: null, metadata, textureHandles);
        }

        public MaterialHandle RegisterMaterial(
            GPUMaterialData material,
            GPUMaterialExtensionData? extensionData,
            MaterialRenderMetadata metadata,
            IReadOnlyList<TextureHandle>? textureHandles = null)
        {
            lock (_lock)
            {
                if (metadata == null)
                    throw new ArgumentNullException(nameof(metadata));
                ValidateMaterialTextureIndices(material);
                MaterialFeatureFlags featureFlags = (MaterialFeatureFlags)material.FeatureFlags;
                if (featureFlags.RequiresExtensionData() && extensionData == null)
                    throw new InvalidOperationException("Materials with feature flags must provide material extension data.");
                if (!featureFlags.RequiresExtensionData() && extensionData.HasValue)
                    throw new InvalidOperationException("Extension data cannot be registered when FeatureFlags does not require an extension payload.");
                if (extensionData.HasValue)
                    ValidateMaterialExtensionTextureIndices(extensionData.Value);

                GPUMaterialData keyMaterial = material;
                keyMaterial.ExtensionDataIndex = -1;
                var key = new MaterialRegistrationKey(keyMaterial, extensionData, metadata);
                if (_deduplicatedMaterials.TryGetValue(key, out MaterialHandle existingHandle))
                {
                    MaterialSlot existing = GetValidatedSlotLocked(existingHandle);
                    existing.ReferenceCount++;
                    _materials[existingHandle.Index] = existing;
                    return existingHandle;
                }

                return RegisterMaterialInternal(material, extensionData, metadata, textureHandles, permanent: false);
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

        public GPUMaterialExtensionData? GetMaterialExtensionData(MaterialHandle handle)
        {
            lock (_lock)
            {
                GPUMaterialData data = GetValidatedSlotLocked(handle).Data;
                return data.ExtensionDataIndex >= 0 && data.ExtensionDataIndex < _materialExtensions.Count
                    ? _materialExtensions[data.ExtensionDataIndex]
                    : null;
            }
        }

        public IReadOnlyList<TextureHandle> GetMaterialTextures(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).TextureHandles;
        }

        public MaterialRenderMetadata GetMaterialMetadata(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).Metadata;
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

        public GPUMaterialExtensionData[] GetMaterialExtensionDataSnapshot()
        {
            lock (_lock)
                return _materialExtensions.ToArray();
        }

        public MaterialRenderMetadata[] GetMaterialMetadataSnapshot()
        {
            lock (_lock)
            {
                var snapshot = new MaterialRenderMetadata[_materials.Count];
                MaterialRenderMetadata defaultMetadata = MaterialRenderMetadata.FromGpuMaterial(CreateDefaultMaterial());
                for (int i = 0; i < _materials.Count; i++)
                    snapshot[i] = _materials[i].Active ? _materials[i].Metadata : defaultMetadata;

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
                long uploadStart = Stopwatch.GetTimestamp();
                _lastUploadBytes = 0;
                _lastExtensionUploadBytes = 0;
                EnsureMaterialBufferCapacityLocked((uint)Math.Max(1, _materials.Count));
                EnsureMaterialExtensionBufferCapacityLocked((uint)Math.Max(1, _materialExtensions.Count));

                if (_gpuUploadDirty)
                {
                    GPUMaterialData[] snapshot = GetMaterialDataSnapshotLocked();
                    _lastUploadBytes = UploadMaterialSpan(snapshot, commandBuffer);
                    GPUMaterialExtensionData[] extensionSnapshot = GetMaterialExtensionDataSnapshotLocked();
                    _lastExtensionUploadBytes = UploadMaterialExtensionSpan(extensionSnapshot, commandBuffer);
                    _gpuUploadDirty = false;
                }

                RecordMaterialReadBarrier(commandBuffer);
                RecordMaterialExtensionReadBarrier(commandBuffer);
                UpdateRegisteredBindlessBuffer();
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));
            if (!_materialBuffer.IsValid)
                throw new InvalidOperationException("Material GPU buffer has not been created.");
            if (!_materialExtensionBuffer.IsValid)
                throw new InvalidOperationException("Material extension GPU buffer has not been created.");

            lock (_lock)
            {
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffer();
            }
        }

        private MaterialHandle RegisterMaterialInternal(
            GPUMaterialData material,
            GPUMaterialExtensionData? extensionData,
            MaterialRenderMetadata metadata,
            IReadOnlyList<TextureHandle>? textureHandles,
            bool permanent)
        {
            int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _materials.Count;
            uint generation = AllocateGeneration(index);
            TextureHandle[] textureHandleArray = CopyTextureHandles(textureHandles);
            GPUMaterialData storedMaterial = material;
            storedMaterial.ExtensionDataIndex = -1;
            if (extensionData.HasValue)
            {
                storedMaterial.ExtensionDataIndex = _materialExtensions.Count;
                _materialExtensions.Add(extensionData.Value);
            }

            GPUMaterialData keyMaterial = material;
            keyMaterial.ExtensionDataIndex = -1;
            var registrationKey = new MaterialRegistrationKey(keyMaterial, extensionData, metadata);

            var slot = new MaterialSlot
            {
                Data = storedMaterial,
                Generation = generation,
                Active = true,
                Permanent = permanent,
                ReferenceCount = 1,
                TextureHandles = textureHandleArray,
                Metadata = metadata,
                RegistrationKey = registrationKey
            };

            if (index == _materials.Count)
                _materials.Add(slot);
            else
                _materials[index] = slot;

            var handle = new MaterialHandle(index, generation);
            _deduplicatedMaterials[registrationKey] = handle;
            MarkMaterialDataDirtyLocked();
            return handle;
        }

        private void DestroyMaterialSlotLocked(MaterialHandle handle, MaterialSlot slot, Fence retireFence)
        {
            _deduplicatedMaterials.Remove(slot.RegistrationKey);
            ReleaseMaterialTextures(slot, retireFence);

            slot.Active = false;
            slot.ReferenceCount = 0;
            slot.Generation = NextGeneration(slot.Generation);
            slot.TextureHandles = Array.Empty<TextureHandle>();
            slot.Data = CreateDefaultMaterial();
            slot.Metadata = MaterialRenderMetadata.FromGpuMaterial(slot.Data);
            slot.RegistrationKey = default;
            _materials[handle.Index] = slot;
            _freeIndices.Push(handle.Index);
            MarkMaterialDataDirtyLocked();
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
            MarkMaterialDataDirtyLocked();
        }

        private void EnsureMaterialExtensionBufferCapacityLocked(uint requiredExtensionCount)
        {
            if (_bufferManager == null || requiredExtensionCount <= _materialExtensionBufferCapacity)
                return;

            WaitForOtherInFlightFrames();

            uint newCapacity = _materialExtensionBufferCapacity == 0 ? 1u : _materialExtensionBufferCapacity;
            while (newCapacity < requiredExtensionCount)
                newCapacity = checked(newCapacity * 2);

            BufferHandle oldBuffer = _materialExtensionBuffer;
            _materialExtensionBuffer = CreateMaterialExtensionBuffer(newCapacity);
            if (oldBuffer.IsValid)
                _bufferManager.DestroyBuffer(oldBuffer);

            UpdateRegisteredBindlessBuffer();
            MarkMaterialDataDirtyLocked();
        }

        private void MarkMaterialDataDirtyLocked()
        {
            _gpuUploadDirty = true;
            _materialDataRevision++;
            if (_materialDataRevision == 0)
                _materialDataRevision = 1;
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
                true,
                MemoryBudgetCategory.MaterialBuffers,
                "Material Data Buffer");
        }

        private BufferHandle CreateMaterialExtensionBuffer(uint extensionCapacity)
        {
            if (_bufferManager == null)
                throw new InvalidOperationException("Material extension GPU buffer creation requires a BufferManager.");

            _materialExtensionBufferCapacity = extensionCapacity;
            return _bufferManager.CreateDeviceBuffer(
                checked(extensionCapacity * MaterialExtensionStride),
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                true,
                MemoryBudgetCategory.MaterialBuffers,
                "Material Extension Data Buffer");
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

        private ulong UploadMaterialSpan(ReadOnlySpan<GPUMaterialData> data, CommandBuffer commandBuffer)
        {
            if (data.IsEmpty || _bufferManager == null || _stagingRing == null || _context == null)
                return 0;

            return GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _materialBuffer,
                data).ByteCount;
        }

        private ulong UploadMaterialExtensionSpan(ReadOnlySpan<GPUMaterialExtensionData> data, CommandBuffer commandBuffer)
        {
            if (data.IsEmpty || _bufferManager == null || _stagingRing == null || _context == null)
                return 0;

            return GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _materialExtensionBuffer,
                data).ByteCount;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
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

        private void RecordMaterialExtensionReadBarrier(CommandBuffer commandBuffer)
        {
            if (_context == null || _bufferManager == null || !_materialExtensionBuffer.IsValid)
                return;

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_materialExtensionBuffer),
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
            VkBuffer extensionBuffer = _bufferManager.GetBuffer(_materialExtensionBuffer);
            _registeredBindlessHeap.RegisterStorageBuffer(BindlessIndex.MaterialExtensionDataBuffer, extensionBuffer, 0, Vk.WholeSize);
        }

        private GPUMaterialData[] GetMaterialDataSnapshotLocked()
        {
            var snapshot = new GPUMaterialData[_materials.Count];
            for (int i = 0; i < _materials.Count; i++)
                snapshot[i] = _materials[i].Active ? _materials[i].Data : CreateDefaultMaterial();

            return snapshot;
        }

        private GPUMaterialExtensionData[] GetMaterialExtensionDataSnapshotLocked()
        {
            return _materialExtensions.Count == 0
                ? Array.Empty<GPUMaterialExtensionData>()
                : _materialExtensions.ToArray();
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
                NormalScaleBias = new Vector4(1f, 0f, 0.5f, 0f),
                MetallicRoughnessAO = new Vector4(0f, 1f, 1f, 0f),
                BaseColorOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                NormalOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                MetallicRoughnessOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                EmissiveOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                TextureRotations = Vector4.Zero,
                TextureTexCoordSets = Vector4.Zero,
                AlbedoTextureIndex = BindlessIndex.DefaultWhiteTexture,
                NormalTextureIndex = BindlessIndex.DefaultNormalTexture,
                MetallicRoughnessTextureIndex = BindlessIndex.DefaultBlackTexture,
                EmissiveTextureIndex = BindlessIndex.DefaultBlackTexture,
                FeatureFlags = 0u,
                ExtensionDataIndex = -1,
                Reserved0 = 0u,
                Reserved1 = 0u
            };
        }

        public static void ValidateMaterialTextureIndices(GPUMaterialData material)
        {
            ValidateTextureIndex(material.AlbedoTextureIndex, nameof(GPUMaterialData.AlbedoTextureIndex));
            ValidateTextureIndex(material.NormalTextureIndex, nameof(GPUMaterialData.NormalTextureIndex));
            ValidateTextureIndex(material.MetallicRoughnessTextureIndex, nameof(GPUMaterialData.MetallicRoughnessTextureIndex));
            ValidateTextureIndex(material.EmissiveTextureIndex, nameof(GPUMaterialData.EmissiveTextureIndex));
            if (material.ExtensionDataIndex < -1)
                throw new InvalidOperationException($"{nameof(GPUMaterialData.ExtensionDataIndex)} must be -1 or a non-negative extension payload index.");
            if (material.FeatureFlags == 0u && material.ExtensionDataIndex != -1)
                throw new InvalidOperationException("ExtensionDataIndex must be -1 when FeatureFlags is zero.");
        }

        public static void ValidateMaterialExtensionTextureIndices(GPUMaterialExtensionData extensionData)
        {
            ValidateTextureIndex(extensionData.ClearcoatTextureIndex, nameof(GPUMaterialExtensionData.ClearcoatTextureIndex));
            ValidateTextureIndex(extensionData.ClearcoatRoughnessTextureIndex, nameof(GPUMaterialExtensionData.ClearcoatRoughnessTextureIndex));
            ValidateTextureIndex(extensionData.ClearcoatNormalTextureIndex, nameof(GPUMaterialExtensionData.ClearcoatNormalTextureIndex));
            ValidateTextureIndex(extensionData.SheenColorTextureIndex, nameof(GPUMaterialExtensionData.SheenColorTextureIndex));
            ValidateTextureIndex(extensionData.SheenRoughnessTextureIndex, nameof(GPUMaterialExtensionData.SheenRoughnessTextureIndex));
            ValidateTextureIndex(extensionData.AnisotropyTextureIndex, nameof(GPUMaterialExtensionData.AnisotropyTextureIndex));
            ValidateTextureIndex(extensionData.TransmissionTextureIndex, nameof(GPUMaterialExtensionData.TransmissionTextureIndex));
            ValidateTextureIndex(extensionData.ThicknessTextureIndex, nameof(GPUMaterialExtensionData.ThicknessTextureIndex));
            ValidateTextureIndex(extensionData.SubsurfaceTextureIndex, nameof(GPUMaterialExtensionData.SubsurfaceTextureIndex));
            ValidateTextureIndex(extensionData.SpecularTextureIndex, nameof(GPUMaterialExtensionData.SpecularTextureIndex));
            ValidateTextureIndex(extensionData.SpecularColorTextureIndex, nameof(GPUMaterialExtensionData.SpecularColorTextureIndex));
            ValidateTextureIndex(extensionData.IridescenceTextureIndex, nameof(GPUMaterialExtensionData.IridescenceTextureIndex));
            ValidateTextureIndex(extensionData.IridescenceThicknessTextureIndex, nameof(GPUMaterialExtensionData.IridescenceThicknessTextureIndex));
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
                if (_materialExtensionBuffer.IsValid && _bufferManager != null)
                    _bufferManager.DestroyBuffer(_materialExtensionBuffer);

                _materialBuffer = BufferHandle.Invalid;
                _materialExtensionBuffer = BufferHandle.Invalid;
                _materials.Clear();
                _materialExtensions.Clear();
                _freeIndices.Clear();
                _deduplicatedMaterials.Clear();
            }
        }

        private struct MaterialSlot
        {
            public GPUMaterialData Data;
            public uint Generation;
            public bool Active;
            public bool Permanent;
            public int ReferenceCount;
            public TextureHandle[] TextureHandles;
            public MaterialRenderMetadata Metadata;
            public MaterialRegistrationKey RegistrationKey;
        }

        private readonly record struct MaterialRegistrationKey(
            GPUMaterialData Material,
            GPUMaterialExtensionData? ExtensionData,
            MaterialRenderMetadata Metadata);

        public sealed class MaterialDataComparer : IEqualityComparer<GPUMaterialData>
        {
            public bool Equals(GPUMaterialData x, GPUMaterialData y)
            {
                return x.Albedo.Equals(y.Albedo) &&
                       x.Emissive.Equals(y.Emissive) &&
                       x.NormalScaleBias.Equals(y.NormalScaleBias) &&
                       x.MetallicRoughnessAO.Equals(y.MetallicRoughnessAO) &&
                       x.BaseColorOffsetScale.Equals(y.BaseColorOffsetScale) &&
                       x.NormalOffsetScale.Equals(y.NormalOffsetScale) &&
                       x.MetallicRoughnessOffsetScale.Equals(y.MetallicRoughnessOffsetScale) &&
                       x.EmissiveOffsetScale.Equals(y.EmissiveOffsetScale) &&
                       x.TextureRotations.Equals(y.TextureRotations) &&
                       x.TextureTexCoordSets.Equals(y.TextureTexCoordSets) &&
                       x.AlbedoTextureIndex == y.AlbedoTextureIndex &&
                       x.NormalTextureIndex == y.NormalTextureIndex &&
                       x.MetallicRoughnessTextureIndex == y.MetallicRoughnessTextureIndex &&
                       x.EmissiveTextureIndex == y.EmissiveTextureIndex &&
                       x.FeatureFlags == y.FeatureFlags &&
                       x.ExtensionDataIndex == y.ExtensionDataIndex &&
                       x.Reserved0 == y.Reserved0 &&
                       x.Reserved1 == y.Reserved1;
            }

            public int GetHashCode(GPUMaterialData obj)
            {
                var hash = new HashCode();
                hash.Add(obj.Albedo);
                hash.Add(obj.Emissive);
                hash.Add(obj.NormalScaleBias);
                hash.Add(obj.MetallicRoughnessAO);
                hash.Add(obj.BaseColorOffsetScale);
                hash.Add(obj.NormalOffsetScale);
                hash.Add(obj.MetallicRoughnessOffsetScale);
                hash.Add(obj.EmissiveOffsetScale);
                hash.Add(obj.TextureRotations);
                hash.Add(obj.TextureTexCoordSets);
                hash.Add(obj.AlbedoTextureIndex);
                hash.Add(obj.NormalTextureIndex);
                hash.Add(obj.MetallicRoughnessTextureIndex);
                hash.Add(obj.EmissiveTextureIndex);
                hash.Add(obj.FeatureFlags);
                hash.Add(obj.ExtensionDataIndex);
                hash.Add(obj.Reserved0);
                hash.Add(obj.Reserved1);
                return hash.ToHashCode();
            }
        }

        private sealed class MaterialRegistrationKeyComparer : IEqualityComparer<MaterialRegistrationKey>
        {
            private static readonly MaterialDataComparer MaterialComparer = new MaterialDataComparer();

            public bool Equals(MaterialRegistrationKey x, MaterialRegistrationKey y)
            {
                return MaterialComparer.Equals(x.Material, y.Material) &&
                       Nullable.Equals(x.ExtensionData, y.ExtensionData) &&
                       x.Metadata.Equals(y.Metadata);
            }

            public int GetHashCode(MaterialRegistrationKey obj)
            {
                return HashCode.Combine(MaterialComparer.GetHashCode(obj.Material), obj.ExtensionData, obj.Metadata);
            }
        }
    }

    public sealed record MaterialManagerDiagnostics(
        int RegisteredMaterialCount,
        int UploadedMaterialCount,
        int MaterialExtensionDataCount = 0);
}
