using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class GpuParticleRuntimeManager : IDisposable
    {
        private const uint InitialParticleCapacity = 65536;
        private const uint InitialEmitterCapacity = 1024;
        private const uint BlendBucketCount = 5;
        private const uint InitialDrawCapacity = BlendBucketCount;
        private const uint CurveSamplesPerEmitter = 16;
        public const uint MaxSpawnPerEmitterPerFrame = 8;

        private static readonly ulong StateStride = (ulong)Marshal.SizeOf<GPUParticleState>();
        private static readonly ulong AliveIndexStride = sizeof(uint);
        private static readonly ulong DeadIndexStride = sizeof(uint);
        private static readonly ulong EmitterStride = (ulong)Marshal.SizeOf<GPUParticleEmitter>();
        private static readonly ulong CurveSampleStride = (ulong)Marshal.SizeOf<GPUParticleCurveSample>();
        private static readonly ulong CounterStride = (ulong)Marshal.SizeOf<GPUParticleCounters>();
        private static readonly ulong RenderInstanceStride = (ulong)Marshal.SizeOf<GPUParticleInstance>();
        private static readonly ulong IndirectDrawStride = (ulong)Marshal.SizeOf<GPUParticleDrawCommand>();
        private static readonly ulong SortKeyStride = (ulong)Marshal.SizeOf<GPUParticleSortKey>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly StagingRing _stagingRing;
        private readonly object _lock = new();
        private readonly List<GPUParticleEmitter> _emitterScratch = new();
        private readonly List<GPUParticleCurveSample> _curveSampleScratch = new();
        private readonly Dictionary<string, int> _textureIndexCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly RuntimeBuffer[] _stateBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _aliveIndexBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _emitterBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _curveSampleBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _counterBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _unsortedRenderInstanceBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _renderInstanceBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _indirectDrawBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly RuntimeBuffer[] _sortKeyBuffers = new RuntimeBuffer[FramesInFlight];
        private readonly BufferHandle[] _counterReadbackBuffers = new BufferHandle[FramesInFlight];
        private readonly bool[] _counterReadbackRecorded = new bool[FramesInFlight];
        private readonly GpuParticleCounterSnapshot[] _lastCompletedCounterSnapshots = new GpuParticleCounterSnapshot[FramesInFlight];
        private RuntimeBuffer _deadIndexBuffer;
        private BindlessHeap? _registeredBindlessHeap;
        private bool _wasGpuEnabled;
        private bool _resetRequired;
        private uint _particleCapacity;
        private uint _emitterCapacity;
        private uint _drawCapacity;
        private bool _disposed;

        public float SoftParticleDistanceForFrame { get; private set; }

        public GpuParticleRuntimeManager(VulkanContext context, BufferManager bufferManager, StagingRing stagingRing)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
        }

        public ulong AllocatedBytes
        {
            get
            {
                lock (_lock)
                {
                    ulong bytes = _deadIndexBuffer.ByteSize;
                    for (int i = 0; i < FramesInFlight; i++)
                    {
                        bytes += _stateBuffers[i].ByteSize;
                        bytes += _aliveIndexBuffers[i].ByteSize;
                        bytes += _emitterBuffers[i].ByteSize;
                        bytes += _curveSampleBuffers[i].ByteSize;
                        bytes += _counterBuffers[i].ByteSize;
                        bytes += _unsortedRenderInstanceBuffers[i].ByteSize;
                        bytes += _renderInstanceBuffers[i].ByteSize;
                        bytes += _indirectDrawBuffers[i].ByteSize;
                        bytes += _sortKeyBuffers[i].ByteSize;
                        if (_counterReadbackBuffers[i].IsValid)
                            bytes += _bufferManager.GetBufferSize(_counterReadbackBuffers[i]);
                    }

                    return bytes;
                }
            }
        }

        public void PrepareFrame(
            Scene scene,
            ParticleSettings settings,
            TextureManager textureManager,
            CommandBuffer commandBuffer,
            SceneRenderingData sceneData)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (textureManager == null)
                throw new ArgumentNullException(nameof(textureManager));
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for GPU particle emitter uploads.", nameof(commandBuffer));

            lock (_lock)
            {
                bool gpuParticlesEnabled =
                    settings.Enabled &&
                    settings.SimulationMode == ParticleSimulationMode.Gpu &&
                    settings.MaxParticles > 0 &&
                    settings.MaxEmitters > 0;

                if (gpuParticlesEnabled)
                {
                    uint particleCapacity = NextPowerOfTwo(CheckedCapacity(settings.MaxParticles, InitialParticleCapacity, nameof(settings.MaxParticles)));
                    uint emitterCapacity = CheckedCapacity(settings.MaxEmitters, InitialEmitterCapacity, nameof(settings.MaxEmitters));
                    uint drawCapacity = InitialDrawCapacity;
                    bool capacityChanged =
                        particleCapacity > _particleCapacity ||
                        emitterCapacity > _emitterCapacity ||
                        drawCapacity > _drawCapacity;

                    EnsureCapacity(ref _deadIndexBuffer, particleCapacity, DeadIndexStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, "GpuParticles.DeadIndexBuffer");
                    for (int i = 0; i < FramesInFlight; i++)
                    {
                        EnsureCapacity(ref _stateBuffers[i], particleCapacity, StateStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.StateBuffer.Frame{i}");
                        EnsureCapacity(ref _aliveIndexBuffers[i], particleCapacity, AliveIndexStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.AliveIndexBuffer.Frame{i}");
                        EnsureCapacity(ref _emitterBuffers[i], emitterCapacity, EmitterStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.EmitterBuffer.Frame{i}");
                        EnsureCapacity(ref _curveSampleBuffers[i], checked(emitterCapacity * CurveSamplesPerEmitter), CurveSampleStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.CurveSampleBuffer.Frame{i}");
                        EnsureCapacity(ref _counterBuffers[i], 1, CounterStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.CounterBuffer.Frame{i}");
                        EnsureCapacity(ref _unsortedRenderInstanceBuffers[i], particleCapacity, RenderInstanceStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.UnsortedRenderInstanceBuffer.Frame{i}");
                        EnsureCapacity(ref _renderInstanceBuffers[i], particleCapacity, RenderInstanceStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.RenderInstanceBuffer.Frame{i}");
                        EnsureCapacity(ref _indirectDrawBuffers[i], drawCapacity, IndirectDrawStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.IndirectBufferBit, $"GpuParticles.IndirectDrawBuffer.Frame{i}");
                        EnsureCapacity(ref _sortKeyBuffers[i], checked(particleCapacity * BlendBucketCount), SortKeyStride, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, $"GpuParticles.SortKeyBuffer.Frame{i}");
                        EnsureCounterReadbackBuffer(i);
                    }

                    UpdateRegisteredBindlessBuffers();
                    _particleCapacity = _stateBuffers[0].ElementCapacity;
                    _emitterCapacity = _emitterBuffers[0].ElementCapacity;
                    _drawCapacity = _indirectDrawBuffers[0].ElementCapacity;
                    if (!_wasGpuEnabled || capacityChanged)
                        _resetRequired = true;

                    long uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    BuildEmitterScratch(scene, settings, textureManager);
                    int frameIndex = _stagingRing.CurrentFrameIndex;
                    ulong uploadBytes = UploadSpan(CollectionsMarshal.AsSpan(_emitterScratch), _emitterBuffers[frameIndex].Handle, commandBuffer);
                    uploadBytes += UploadSpan(CollectionsMarshal.AsSpan(_curveSampleScratch), _curveSampleBuffers[frameIndex].Handle, commandBuffer);
                    if (uploadBytes > 0)
                        RecordEmitterUploadBarrier(commandBuffer, frameIndex);
                    sceneData.GpuParticleEmitterCount = _emitterScratch.Count;
                    sceneData.GpuParticleEmitterUploadBytes = uploadBytes;
                    sceneData.UploadedBytes += uploadBytes;
                    sceneData.CpuGpuParticleEmitterUploadMicroseconds =
                        System.Diagnostics.Stopwatch.GetElapsedTime(uploadStart).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                    SoftParticleDistanceForFrame = settings.SoftParticlesEnabled ? settings.SoftParticleDistance : 0.0f;
                }
                else
                {
                    _wasGpuEnabled = false;
                    _resetRequired = false;
                    _particleCapacity = 0;
                    _emitterCapacity = 0;
                    _drawCapacity = 0;
                    SoftParticleDistanceForFrame = 0.0f;
                }

                _wasGpuEnabled = gpuParticlesEnabled;
                sceneData.GpuParticlesEnabled = gpuParticlesEnabled ? 1 : 0;
                sceneData.GpuParticleCapacity = gpuParticlesEnabled ? checked((int)_particleCapacity) : 0;
                sceneData.GpuParticleEmitterCapacity = gpuParticlesEnabled ? checked((int)_emitterCapacity) : 0;
                sceneData.GpuParticleDrawCapacity = gpuParticlesEnabled ? checked((int)_drawCapacity) : 0;
                sceneData.GpuParticleMaxSpawnPerEmitter = gpuParticlesEnabled ? checked((int)MaxSpawnPerEmitterPerFrame) : 0;
                sceneData.GpuParticleResetRequired = gpuParticlesEnabled && _resetRequired ? 1 : 0;
                PopulateSceneData(sceneData);
            }
        }

        public void MarkResetRecorded()
        {
            lock (_lock)
            {
                _resetRequired = false;
            }
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);

            lock (_lock)
            {
                if (!_counterReadbackRecorded[frameIndex] || !_counterReadbackBuffers[frameIndex].IsValid)
                {
                    _lastCompletedCounterSnapshots[frameIndex] = GpuParticleCounterSnapshot.Invalid;
                    return;
                }

                _bufferManager.InvalidateBuffer(_counterReadbackBuffers[frameIndex], 0, CounterStride);
                GPUParticleCounters* counters = (GPUParticleCounters*)_bufferManager.GetMappedPointer(_counterReadbackBuffers[frameIndex]);
                _lastCompletedCounterSnapshots[frameIndex] = GpuParticleCounterSnapshot.FromCounters(*counters);
                _counterReadbackRecorded[frameIndex] = false;
            }
        }

        public GpuParticleCounterSnapshot GetLastCompletedCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);

            lock (_lock)
            {
                return _lastCompletedCounterSnapshots[frameIndex];
            }
        }

        public void RecordCounterReadback(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
        {
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));
            if (sceneData.GpuParticlesEnabled == 0 || sceneData.GpuParticleCapacity <= 0)
                return;
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for GPU particle counter readback.", nameof(commandBuffer));

            ValidateFrameIndex(frameIndex);

            lock (_lock)
            {
                GpuParticleRuntimeBuffers buffers = GetBuffers(frameIndex);
                if (!buffers.CounterBuffer.IsValid)
                    return;

                EnsureCounterReadbackBuffer(frameIndex);
                VkBuffer source = _bufferManager.GetBuffer(buffers.CounterBuffer);
                VkBuffer destination = _bufferManager.GetBuffer(_counterReadbackBuffers[frameIndex]);

                BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                    source,
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
                    AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    0,
                    CounterStride);
                ExecuteBufferBarrier(commandBuffer, beforeCopy);

                BufferCopy copy = new()
                {
                    SrcOffset = 0,
                    DstOffset = 0,
                    Size = CounterStride
                };
                _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

                BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                    destination,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.HostBit,
                    AccessFlags2.HostReadBit,
                    0,
                    CounterStride);
                ExecuteBufferBarrier(commandBuffer, afterCopy);

                _counterReadbackRecorded[frameIndex] = true;
            }
        }

        public GpuParticleRuntimeBuffers GetBuffers(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));

            lock (_lock)
            {
                return new GpuParticleRuntimeBuffers(
                    _stateBuffers[frameIndex].Handle,
                    _aliveIndexBuffers[frameIndex].Handle,
                    _deadIndexBuffer.Handle,
                    _counterBuffers[frameIndex].Handle,
                    _unsortedRenderInstanceBuffers[frameIndex].Handle,
                    _renderInstanceBuffers[frameIndex].Handle,
                    _indirectDrawBuffers[frameIndex].Handle,
                    _sortKeyBuffers[frameIndex].Handle);
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

        private void PopulateSceneData(SceneRenderingData sceneData)
        {
            int frameIndex = _stagingRing.CurrentFrameIndex;
            sceneData.GpuParticleRenderInstanceBuffer = _renderInstanceBuffers[frameIndex].Handle;
            sceneData.GpuParticleIndirectDrawBuffer = _indirectDrawBuffers[frameIndex].Handle;
            sceneData.GpuParticleStateBufferSize = MaxByteSize(_stateBuffers);
            sceneData.GpuParticleAliveIndexBufferSize = MaxByteSize(_aliveIndexBuffers);
            sceneData.GpuParticleDeadIndexBufferSize = _deadIndexBuffer.ByteSize;
            sceneData.GpuParticleEmitterBufferSize = MaxByteSize(_emitterBuffers);
            sceneData.GpuParticleCurveSampleBufferSize = MaxByteSize(_curveSampleBuffers);
            sceneData.GpuParticleCounterBufferSize = MaxByteSize(_counterBuffers);
            sceneData.GpuParticleUnsortedRenderInstanceBufferSize = MaxByteSize(_unsortedRenderInstanceBuffers);
            sceneData.GpuParticleRenderInstanceBufferSize = MaxByteSize(_renderInstanceBuffers);
            sceneData.GpuParticleIndirectDrawBufferSize = MaxByteSize(_indirectDrawBuffers);
            sceneData.GpuParticleSortKeyBufferSize = MaxByteSize(_sortKeyBuffers);
        }

        private void BuildEmitterScratch(Scene scene, ParticleSettings settings, TextureManager textureManager)
        {
            _emitterScratch.Clear();
            _curveSampleScratch.Clear();
            if (!settings.Enabled || settings.MaxEmitters <= 0)
                return;

            for (int effectIndex = 0; effectIndex < scene.ParticleEffects.Count; effectIndex++)
            {
                if (_emitterScratch.Count >= settings.MaxEmitters)
                    break;

                ParticleEffectInstance instance = scene.ParticleEffects[effectIndex];
                if (!instance.Visible)
                    continue;

                IReadOnlyList<ParticleEmitterDefinition> emitters = instance.Effect.Emitters;
                for (int emitterIndex = 0; emitterIndex < emitters.Count && _emitterScratch.Count < settings.MaxEmitters; emitterIndex++)
                {
                    ParticleEmitterDefinition emitter = emitters[emitterIndex];
                    _emitterScratch.Add(CreateEmitter(instance, emitter, settings, textureManager, emitterIndex));
                    AddCurveSamples(emitter, settings);
                }
            }
        }

        internal GPUParticleEmitter CreateEmitter(
            ParticleEffectInstance instance,
            ParticleEmitterDefinition definition,
            ParticleSettings settings,
            TextureManager textureManager,
            int emitterIndex)
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (textureManager == null)
                throw new ArgumentNullException(nameof(textureManager));

            ParticleSpawnShape shape = definition.SpawnShape;
            Vector4 initialColor = definition.ColorOverLife.Sample(0.0f).ToVector4();
            Vector4 colorEnd = definition.ColorOverLife.Sample(1.0f).ToVector4();
            ParticleFlipbook? flipbook = definition.Material.Flipbook;
            int textureIndex = ResolveTextureIndex(definition.Material.TexturePath, textureManager);
            uint flags = BuildEmitterFlags(definition);

            return new GPUParticleEmitter
            {
                WorldMatrix = instance.WorldMatrix,
                SpawnShape0 = new Vector4(shape.Extents, (float)shape.Kind),
                SpawnShape1 = new Vector4(shape.Radius, shape.InnerRadius, shape.AngleRadians, shape.Length),
                InitialVelocityMin = new Vector4(definition.InitialVelocityMin * settings.GlobalVelocityScale, definition.SpawnRatePerSecond * settings.GlobalSpawnRateScale),
                InitialVelocityMax = new Vector4(
                    definition.InitialVelocityMax * settings.GlobalVelocityScale,
                    Math.Clamp(definition.Material.AlphaClipThreshold, 0.0f, 1.0f)),
                AccelerationDrag = new Vector4(definition.Acceleration, definition.Drag),
                LifetimeSize = new Vector4(
                    Math.Max(0.001f, definition.LifetimeSeconds.Sample(0.0f)),
                    Math.Max(0.001f, definition.LifetimeSeconds.Sample(1.0f)),
                    Math.Max(0.0f, definition.Size.Sample(0.0f)),
                    Math.Max(0.0f, definition.Size.Sample(1.0f))),
                Color = initialColor,
                MaterialIndex = checked((uint)Math.Max(0, textureIndex)),
                MaxParticles = checked((uint)Math.Clamp(definition.MaxParticles, 0, settings.MaxParticles)),
                RandomSeed = MixSeed(instance.RandomSeed, (uint)emitterIndex),
                Flags = flags,
                ColorEnd = colorEnd,
                EmissiveAngularVelocity = new Vector4(
                    definition.EmissiveOverLife.Sample(0.0f) * settings.GlobalEmissiveScale,
                    definition.EmissiveOverLife.Sample(1.0f) * settings.GlobalEmissiveScale,
                    definition.AngularVelocityRadiansPerSecond.Sample(0.0f),
                    definition.AngularVelocityRadiansPerSecond.Sample(1.0f)),
                RotationParams = new Vector4(
                    definition.RotationRadians.Sample(0.0f),
                    flipbook?.FrameCount ?? 1,
                    flipbook?.FramesPerSecond ?? 0.0f,
                    flipbook?.InterpolateFrames == true ? 1.0f : 0.0f),
                TimingParams = new Vector4(
                    Math.Max(0, definition.BurstCount),
                    Math.Max(0.0f, definition.BurstTimeSeconds),
                    Math.Max(0.0f, definition.StartDelaySeconds),
                    Math.Max(0.0f, definition.DurationSeconds))
            };
        }

        internal static uint BuildEmitterFlags(ParticleEmitterDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            uint flags = 0;
            if (definition.Looping)
                flags |= 1u << 0;
            if (definition.LocalSpace)
                flags |= 1u << 1;
            if (definition.Material.SoftParticles)
                flags |= 1u << 2;
            ParticleFlipbook? flipbook = definition.Material.Flipbook;
            if (flipbook?.RandomStartFrame == true)
                flags |= 1u << 3;
            if (flipbook?.Loop == true)
                flags |= 1u << 4;
            flags |= ((uint)definition.Material.BlendMode & 0xFu) << 8;
            flags |= ((uint)definition.Material.BillboardMode & 0xFu) << 12;
            flags |= ((uint)Math.Clamp(flipbook?.Columns ?? 1, 1, 255) & 0xFFu) << 16;
            flags |= ((uint)Math.Clamp(flipbook?.Rows ?? 1, 1, 255) & 0xFFu) << 24;
            return flags;
        }

        private void AddCurveSamples(ParticleEmitterDefinition definition, ParticleSettings settings)
        {
            for (uint i = 0; i < CurveSamplesPerEmitter; i++)
            {
                float t = CurveSamplesPerEmitter <= 1
                    ? 0.0f
                    : i / (CurveSamplesPerEmitter - 1.0f);
                _curveSampleScratch.Add(new GPUParticleCurveSample
                {
                    Color = definition.ColorOverLife.Sample(t).ToVector4(),
                    Properties = new Vector4(
                        Math.Max(0.0f, definition.Size.Sample(t)),
                        definition.EmissiveOverLife.Sample(t) * settings.GlobalEmissiveScale,
                        definition.RotationRadians.Sample(t),
                        definition.AngularVelocityRadiansPerSecond.Sample(t))
                });
            }
        }

        private int ResolveTextureIndex(string? texturePath, TextureManager textureManager)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return BindlessIndex.DefaultWhiteTexture;

            string fullPath = System.IO.Path.GetFullPath(texturePath);
            if (_textureIndexCache.TryGetValue(fullPath, out int cachedIndex))
                return cachedIndex;

            TextureHandle texture = textureManager.LoadOptionalTextureFromFile(
                fullPath,
                textureManager.DefaultWhiteTexture,
                generateMipmaps: true,
                srgb: true);
            int index = textureManager.GetBindlessTextureIndex(texture);
            if (index < 0)
                index = BindlessIndex.DefaultWhiteTexture;

            _textureIndexCache[fullPath] = index;
            return index;
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

        private unsafe void RecordEmitterUploadBarrier(CommandBuffer commandBuffer, int frameIndex)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
            int count = 0;
            if (_emitterBuffers[frameIndex].Handle.IsValid)
                barriers[count++] = CreateTransferToComputeReadBarrier(_emitterBuffers[frameIndex].Handle);
            if (_curveSampleBuffers[frameIndex].Handle.IsValid)
                barriers[count++] = CreateTransferToComputeReadBarrier(_curveSampleBuffers[frameIndex].Handle);
            if (count == 0)
                return;

            fixed (BufferMemoryBarrier2* pBarriers = barriers)
            {
                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = (uint)count,
                    PBufferMemoryBarriers = pBarriers
                };

                _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
            }
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

        private static uint MixSeed(uint seed, uint emitterIndex)
        {
            uint value = seed ^ (emitterIndex + 0x9E3779B9u);
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value == 0 ? 1u : value;
        }

        private static ulong MaxByteSize(RuntimeBuffer[] buffers)
        {
            ulong max = 0;
            for (int i = 0; i < buffers.Length; i++)
                max = Math.Max(max, buffers[i].ByteSize);
            return max;
        }

        private void EnsureCapacity(
            ref RuntimeBuffer buffer,
            uint requiredElements,
            ulong stride,
            BufferUsageFlags usage,
            string debugName)
        {
            uint required = Math.Max(1u, requiredElements);
            if (!buffer.Handle.IsValid)
            {
                buffer = CreateBuffer(required, stride, usage, debugName);
                return;
            }

            if (required <= buffer.ElementCapacity)
                return;

            uint newCapacity = buffer.ElementCapacity;
            do
            {
                newCapacity = checked(newCapacity * 2);
            }
            while (newCapacity < required);

            DestroyIfValid(buffer.Handle);
            buffer = CreateBuffer(newCapacity, stride, usage, debugName);
        }

        private RuntimeBuffer CreateBuffer(uint elementCapacity, ulong stride, BufferUsageFlags usage, string debugName)
        {
            ulong byteSize = checked(elementCapacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                debugName);
            _context.SetDebugName(_bufferManager.GetBuffer(handle).Handle, ObjectType.Buffer, debugName);
            return new RuntimeBuffer(handle, elementCapacity, byteSize);
        }

        private void EnsureCounterReadbackBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            if (_counterReadbackBuffers[frameIndex].IsValid)
                return;

            _counterReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                CounterStride,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"GpuParticles.CounterReadback.Frame{frameIndex}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }

        private void ExecuteBufferBarrier(CommandBuffer commandBuffer, BufferMemoryBarrier2 barrier)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            if (_registeredBindlessHeap == null)
                return;

            RegisterStorageBuffer(BindlessIndex.GpuParticleStateBufferBase, _stateBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleStateBufferFrame1, _stateBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleAliveIndexBufferBase, _aliveIndexBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleAliveIndexBufferFrame1, _aliveIndexBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleDeadIndexBuffer, _deadIndexBuffer.Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleEmitterBufferBase, _emitterBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleEmitterBufferFrame1, _emitterBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleCurveSampleBufferBase, _curveSampleBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleCurveSampleBufferFrame1, _curveSampleBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleCounterBufferBase, _counterBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleCounterBufferFrame1, _counterBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleUnsortedRenderInstanceBufferBase, _unsortedRenderInstanceBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleUnsortedRenderInstanceBufferFrame1, _unsortedRenderInstanceBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleRenderInstanceBufferBase, _renderInstanceBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleRenderInstanceBufferFrame1, _renderInstanceBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleIndirectDrawBufferBase, _indirectDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleIndirectDrawBufferFrame1, _indirectDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleSortKeyBufferBase, _sortKeyBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.GpuParticleSortKeyBufferFrame1, _sortKeyBuffers[1].Handle);
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            if (!handle.IsValid)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _registeredBindlessHeap!.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private static uint CheckedCapacity(int requested, uint initialCapacity, string name)
        {
            if (requested < 0)
                throw new ArgumentOutOfRangeException(name);

            return Math.Max(initialCapacity, (uint)requested);
        }

        private static uint NextPowerOfTwo(uint value)
        {
            if (value <= 1u)
                return 1u;
            if (value > 0x80000000u)
                throw new OverflowException("GPU particle capacity exceeds the maximum power-of-two sort capacity.");

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1u;
        }

        private static void ValidateFrameIndex(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (_lock)
            {
                DestroyIfValid(_deadIndexBuffer.Handle);
                for (int i = 0; i < FramesInFlight; i++)
                {
                    DestroyIfValid(_stateBuffers[i].Handle);
                    DestroyIfValid(_aliveIndexBuffers[i].Handle);
                    DestroyIfValid(_emitterBuffers[i].Handle);
                    DestroyIfValid(_curveSampleBuffers[i].Handle);
                    DestroyIfValid(_counterBuffers[i].Handle);
                    DestroyIfValid(_unsortedRenderInstanceBuffers[i].Handle);
                    DestroyIfValid(_renderInstanceBuffers[i].Handle);
                    DestroyIfValid(_indirectDrawBuffers[i].Handle);
                    DestroyIfValid(_sortKeyBuffers[i].Handle);
                    DestroyIfValid(_counterReadbackBuffers[i]);
                }
            }
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

    public readonly record struct GpuParticleRuntimeBuffers(
        BufferHandle StateBuffer,
        BufferHandle AliveIndexBuffer,
        BufferHandle DeadIndexBuffer,
        BufferHandle CounterBuffer,
        BufferHandle UnsortedRenderInstanceBuffer,
        BufferHandle RenderInstanceBuffer,
        BufferHandle IndirectDrawBuffer,
        BufferHandle SortKeyBuffer);

    public readonly record struct GpuParticleCounterSnapshot(
        int Valid,
        uint AliveCount,
        uint DeadCount,
        uint SpawnedCount,
        uint KilledCount,
        uint CulledCount,
        uint RenderedCount,
        uint DroppedSpawnCount,
        uint BlendBucket0Count,
        uint BlendBucket1Count,
        uint BlendBucket2Count,
        uint BlendBucket3Count,
        uint BlendBucket4Count)
    {
        public static GpuParticleCounterSnapshot Invalid { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public static GpuParticleCounterSnapshot FromCounters(GPUParticleCounters counters)
        {
            return new GpuParticleCounterSnapshot(
                1,
                counters.AliveCount,
                counters.DeadCount,
                counters.SpawnedCount,
                counters.KilledCount,
                counters.CulledCount,
                counters.RenderedCount,
                counters.DroppedSpawnCount,
                counters.BlendBucket0Count,
                counters.BlendBucket1Count,
                counters.BlendBucket2Count,
                counters.BlendBucket3Count,
                counters.BlendBucket4Count);
        }
    }
}
