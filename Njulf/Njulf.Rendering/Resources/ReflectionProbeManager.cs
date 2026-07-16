using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Owns the local-reflection cubemap array and the stable mapping from authored probe IDs to
    /// array layers. A layer is never exposed to shaders until its capture/prefilter work has
    /// been explicitly published.
    /// </summary>
    public sealed unsafe class ReflectionProbeManager : IDisposable
    {
        public const int AbsoluteMaxProbeCapacity = 256;
        private static readonly ulong MetadataBufferSize =
            ReflectionProbeData.HeaderSize + ReflectionProbeData.ProbeStride * AbsoluteMaxProbeCapacity;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly GPUReflectionProbe[] _probeScratch = new GPUReflectionProbe[AbsoluteMaxProbeCapacity];
        private readonly Dictionary<Guid, int> _layersByProbeId = new();
        private readonly SortedSet<int> _freeLayers = new();
        private readonly HashSet<Guid> _capturedProbeIds = new();
        private readonly Queue<Guid> _pendingCaptureProbeIds = new();
        private readonly HashSet<Guid> _queuedCaptureProbeIds = new();
        private readonly HashSet<Guid> _capturesInFlight = new();

        private BufferHandle _metadataBuffer;
        private GpuAllocator.Allocation* _cubemapArrayAllocation;
        private Image _cubemapArrayImage;
        private ImageView _cubemapArrayView;
        private ImageView _debugCubemapView;
        private ImageView[] _captureFaceViews = [];
        private Sampler _cubemapSampler;
        private BindlessHeap? _registeredBindlessHeap;
        private int _activeProbeCount;
        private int _cubemapArrayCapacity;
        private uint _cubemapArrayResolution;
        private uint _probeMipCount;
        private ulong _estimatedBytes;
        private long _lastUploadMicroseconds;
        private int _capturesCompletedThisFrame;
        private bool _captureOnLoadQueued;
        private bool _descriptorDirty = true;
        private bool _disposed;

        public ReflectionProbeManager(VulkanContext context, BufferManager bufferManager, RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _metadataBuffer = _bufferManager.CreateDeviceBuffer(
                MetadataBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ReflectionProbes,
                "Reflection Probe Metadata Buffer");
            CreateSampler();
            UpdateResourceMetrics();
        }

        public int ActiveProbeCount => _activeProbeCount;
        public int ProbeCapacity => _cubemapArrayCapacity;
        public uint ProbeResolution => _settings.Reflections.ProbeResolution;
        public uint ProbeMipCount => _probeMipCount;
        public ulong EstimatedBytes => _estimatedBytes;
        public ulong MetadataBufferBytes => MetadataBufferSize;
        public ulong CubemapArrayBytes => _cubemapArrayImage.Handle == 0
            ? 0
            : ReflectionProbeData.EstimateCubemapArrayBytes(_cubemapArrayCapacity, ProbeResolution, _probeMipCount);
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        public int CapturesQueued => _pendingCaptureProbeIds.Count + _capturesInFlight.Count;
        public int CapturesCompleted => _capturesCompletedThisFrame;
        public Image CaptureImage => _cubemapArrayImage;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            if (!ReferenceEquals(_registeredBindlessHeap, bindlessHeap))
            {
                _registeredBindlessHeap = bindlessHeap;
                bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.ReflectionProbeBuffer,
                    _bufferManager.GetBuffer(_metadataBuffer),
                    0,
                    MetadataBufferSize);
                _descriptorDirty = true;
            }

            // When no local image exists the environment manager's cube fallback remains bound.
            if (_cubemapArrayView.Handle != 0 && _descriptorDirty)
            {
                bindlessHeap.RegisterTexture(
                    BindlessIndex.ReflectionProbeCubemapArrayTexture,
                    _cubemapArrayView,
                    _cubemapSampler);
                if (_debugCubemapView.Handle != 0)
                    bindlessHeap.RegisterTexture(BindlessIndex.ReflectionProbeDebugTexture, _debugCubemapView, _cubemapSampler);
                _descriptorDirty = false;
            }
        }

        public void Upload(IReadOnlyList<ReflectionProbe> authoredProbes, StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (authoredProbes == null)
                throw new ArgumentNullException(nameof(authoredProbes));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for reflection probe upload.", nameof(commandBuffer));

            long uploadStart = Stopwatch.GetTimestamp();
            _capturesCompletedThisFrame = 0;
            IReadOnlyList<ReflectionProbe> activeProbes = SelectActiveProbes(authoredProbes);
            SynchronizeProbeLayers(activeProbes);
            EnsureCubemapArrayStorage(RequiredLayerCapacity());
            RegisterIfNeeded();

            _activeProbeCount = ReflectionProbeData.BuildProbes(
                activeProbes,
                _settings.Reflections,
                _probeScratch.AsSpan(0, AbsoluteMaxProbeCapacity),
                probe => _layersByProbeId[probe.Id],
                probe => _cubemapArrayImage.Handle != 0 && _capturedProbeIds.Contains(probe.Id));
            UpdateResourceMetrics();

            if (_settings.Reflections.CaptureOnLoad && !_captureOnLoadQueued && _activeProbeCount > 0)
            {
                RequestRecaptureAll("load");
                _captureOnLoadQueued = true;
            }

            GPUReflectionProbeHeader header = ReflectionProbeData.BuildHeader(
                _activeProbeCount,
                _settings.Reflections,
                BindlessIndex.ReflectionProbeCubemapArrayTexture,
                BindlessIndex.ReflectionProbeDebugTexture,
                _probeMipCount);
            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _metadataBuffer,
                header,
                _probeScratch.AsSpan(0, _activeProbeCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
        }

        public void RequestRecaptureAll(string reason)
        {
            _ = reason ?? throw new ArgumentNullException(nameof(reason));
            foreach (Guid probeId in _layersByProbeId.Keys)
            {
                _capturedProbeIds.Remove(probeId);
                QueueCapture(probeId);
            }
        }

        /// <summary>
        /// Acquires work within the configured per-frame capture budget. The caller renders all
        /// six faces into <see cref="GetCaptureFaceView"/>, prefilters every mip, and finally
        /// calls <see cref="PublishCapture"/> after recording the shader-read barrier.
        /// </summary>
        public bool TryBeginCapture(out ReflectionProbeCapture capture)
        {
            int budget = _settings.Reflections.MaxProbeCapturesPerFrame;
            if (budget <= 0 || _capturesInFlight.Count >= budget || _pendingCaptureProbeIds.Count == 0)
            {
                capture = default;
                return false;
            }

            Guid probeId = _pendingCaptureProbeIds.Dequeue();
            _queuedCaptureProbeIds.Remove(probeId);
            if (!_layersByProbeId.TryGetValue(probeId, out int layer) || _cubemapArrayImage.Handle == 0)
            {
                capture = default;
                return false;
            }

            _capturesInFlight.Add(probeId);
            capture = new ReflectionProbeCapture(probeId, layer, ProbeResolution, _probeMipCount);
            return true;
        }

        public ImageView GetCaptureFaceView(in ReflectionProbeCapture capture, int faceIndex)
        {
            ValidateCapture(capture);
            if ((uint)faceIndex >= 6u)
                throw new ArgumentOutOfRangeException(nameof(faceIndex));
            return _captureFaceViews[capture.CubemapArrayIndex * 6 + faceIndex];
        }

        /// <summary>
        /// Publishes a fully rendered and prefiltered capture. Calling this before all six faces
        /// and mips are transitioned to shader-read is a caller error; publication is the sole
        /// point at which the layer becomes visible to forward shading.
        /// </summary>
        public void PublishCapture(in ReflectionProbeCapture capture)
        {
            ValidateCapture(capture);
            _capturesInFlight.Remove(capture.ProbeId);
            _capturedProbeIds.Add(capture.ProbeId);
            _capturesCompletedThisFrame++;
        }

        public void CancelCapture(in ReflectionProbeCapture capture)
        {
            ValidateCapture(capture);
            _capturesInFlight.Remove(capture.ProbeId);
            QueueCapture(capture.ProbeId);
        }

        private IReadOnlyList<ReflectionProbe> SelectActiveProbes(IReadOnlyList<ReflectionProbe> authoredProbes)
        {
            if (!_settings.Reflections.Enabled ||
                _settings.Reflections.Mode is ReflectionMode.Disabled or ReflectionMode.GlobalEnvironmentOnly ||
                _settings.Reflections.MaxProbes == 0)
                return Array.Empty<ReflectionProbe>();

            var probes = new List<(ReflectionProbe Probe, int OriginalIndex)>(authoredProbes.Count);
            var ids = new HashSet<Guid>();
            for (int i = 0; i < authoredProbes.Count; i++)
            {
                ReflectionProbe? probe = authoredProbes[i];
                if (probe == null)
                    continue;
                if (probe.Id == Guid.Empty || !ids.Add(probe.Id))
                    throw new InvalidOperationException("Each live reflection probe must have a unique, non-empty Id.");
                probes.Add((probe, i));
            }

            probes.Sort((a, b) =>
            {
                int priority = b.Probe.Priority.CompareTo(a.Probe.Priority);
                if (priority != 0)
                    return priority;
                int name = string.CompareOrdinal(a.Probe.Name, b.Probe.Name);
                return name != 0 ? name : a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            int count = Math.Min(Math.Min(probes.Count, _settings.Reflections.MaxProbes), AbsoluteMaxProbeCapacity);
            var selected = new ReflectionProbe[count];
            for (int i = 0; i < count; i++)
                selected[i] = probes[i].Probe;
            return selected;
        }

        private void SynchronizeProbeLayers(IReadOnlyList<ReflectionProbe> activeProbes)
        {
            var liveIds = new HashSet<Guid>();
            for (int i = 0; i < activeProbes.Count; i++)
                liveIds.Add(activeProbes[i].Id);

            if (_layersByProbeId.Count > 0)
            {
                var removed = new List<Guid>();
                foreach ((Guid probeId, int layer) in _layersByProbeId)
                {
                    if (!liveIds.Contains(probeId))
                    {
                        removed.Add(probeId);
                        _freeLayers.Add(layer);
                    }
                }
                foreach (Guid probeId in removed)
                {
                    _layersByProbeId.Remove(probeId);
                    _capturedProbeIds.Remove(probeId);
                    _queuedCaptureProbeIds.Remove(probeId);
                    _capturesInFlight.Remove(probeId);
                }
            }

            // Drop stale queue entries in one bounded pass; removed IDs have already released
            // their layers and must never publish a recycled target.
            if (_pendingCaptureProbeIds.Count > 0)
            {
                int pendingCount = _pendingCaptureProbeIds.Count;
                for (int i = 0; i < pendingCount; i++)
                {
                    Guid probeId = _pendingCaptureProbeIds.Dequeue();
                    if (_layersByProbeId.ContainsKey(probeId))
                        _pendingCaptureProbeIds.Enqueue(probeId);
                }
            }

            for (int i = 0; i < activeProbes.Count; i++)
            {
                Guid probeId = activeProbes[i].Id;
                if (_layersByProbeId.ContainsKey(probeId))
                    continue;
                int layer = AllocateLayer();
                _layersByProbeId.Add(probeId, layer);
                QueueCapture(probeId);
            }
        }

        private int AllocateLayer()
        {
            if (_freeLayers.Count > 0)
            {
                int layer = _freeLayers.Min;
                _freeLayers.Remove(layer);
                return layer;
            }
            return _layersByProbeId.Count;
        }

        private int RequiredLayerCapacity()
        {
            int capacity = 0;
            foreach (int layer in _layersByProbeId.Values)
                capacity = Math.Max(capacity, checked(layer + 1));
            return capacity;
        }

        private void QueueCapture(Guid probeId)
        {
            if (_queuedCaptureProbeIds.Add(probeId))
                _pendingCaptureProbeIds.Enqueue(probeId);
        }

        private void EnsureCubemapArrayStorage(int requiredProbeCount)
        {
            if (requiredProbeCount <= 0 || (_cubemapArrayCapacity >= requiredProbeCount &&
                _cubemapArrayResolution == ProbeResolution && _cubemapArrayImage.Handle != 0))
                return;

            // Allocation is a rare settings/capacity event. Synchronize before destroying the
            // descriptor-visible image, then preserve all mappings but invalidate its contents.
            _context.WaitIdle();
            DestroyCubemapArrayResources();
            int capacity = Math.Min(Math.Max(requiredProbeCount, 1), _settings.Reflections.MaxProbes);
            uint layerCount = checked((uint)capacity * 6u);
            _probeMipCount = ReflectionProbeData.CalculateMipCount(ProbeResolution);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                ImageType = ImageType.Type2D,
                Format = Format.R16G16B16A16Sfloat,
                Extent = new Extent3D { Width = ProbeResolution, Height = ProbeResolution, Depth = 1 },
                MipLevels = _probeMipCount,
                ArrayLayers = layerCount,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                        ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            var allocationInfo = new GpuAllocator.AllocationCreateInfo
            {
                Usage = GpuAllocator.MemoryUsage.AutoPreferDevice,
                Flags = _context.MemoryBudgetExtensionEnabled
                    ? GpuAllocator.AllocationCreateFlags.WithinBudgetBit
                    : default
            };
            GpuAllocator.AllocationInfo allocationResult;
            Image createdImage;
            GpuAllocator.Allocation* createdAllocation;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator, &imageInfo, &allocationInfo,
                &createdImage, &createdAllocation, &allocationResult);
            if (result != Result.Success)
            {
                _cubemapArrayImage = default;
                _cubemapArrayAllocation = null;
                _cubemapArrayCapacity = 0;
                if (_context.IsMemoryBudgetExceeded(result))
                    return;
                throw new VulkanException("Failed to create reflection probe cubemap array", result);
            }

            _cubemapArrayImage = createdImage;
            _cubemapArrayAllocation = createdAllocation;
            _cubemapArrayCapacity = capacity;
            _cubemapArrayResolution = ProbeResolution;
            _context.SetDebugName(_cubemapArrayImage.Handle, ObjectType.Image, "Reflection Probe Cubemap Array");
            _cubemapArrayView = CreateView(ImageViewType.TypeCubeArray, 0, layerCount, 0, _probeMipCount);
            _debugCubemapView = CreateView(ImageViewType.TypeCube, 0, 6, 0, _probeMipCount);
            _captureFaceViews = new ImageView[layerCount];
            for (uint layer = 0; layer < layerCount; layer++)
                _captureFaceViews[layer] = CreateView(ImageViewType.Type2D, layer, 1, 0, 1);

            _capturedProbeIds.Clear();
            foreach (Guid probeId in _layersByProbeId.Keys)
                QueueCapture(probeId);
            _descriptorDirty = true;
        }

        private void RegisterIfNeeded()
        {
            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
        }

        private ImageView CreateView(ImageViewType viewType, uint baseLayer, uint layerCount, uint baseMip, uint mipCount)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _cubemapArrayImage,
                ViewType = viewType,
                Format = Format.R16G16B16A16Sfloat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = baseMip,
                    LevelCount = mipCount,
                    BaseArrayLayer = baseLayer,
                    LayerCount = layerCount
                }
            };
            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create reflection probe cubemap image view", result);
            return view;
        }

        private void CreateSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MinLod = 0f,
                MaxLod = 32f,
                MaxAnisotropy = 1f
            };
            Result result = _context.Api.CreateSampler(_context.Device, &samplerInfo, null, out _cubemapSampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create reflection probe cubemap sampler", result);
            _context.SetDebugName(_cubemapSampler.Handle, ObjectType.Sampler, "Reflection Probe Cubemap Sampler");
        }

        private void ValidateCapture(in ReflectionProbeCapture capture)
        {
            if (capture.ProbeId == Guid.Empty || !_capturesInFlight.Contains(capture.ProbeId) ||
                !_layersByProbeId.TryGetValue(capture.ProbeId, out int layer) ||
                layer != capture.CubemapArrayIndex || _cubemapArrayImage.Handle == 0)
            {
                throw new InvalidOperationException("The reflection probe capture is no longer active or its layer was recycled.");
            }
        }

        private void UpdateResourceMetrics()
        {
            _probeMipCount = ReflectionProbeData.CalculateMipCount(ProbeResolution);
            // The renderer reports metadata and cubemap residency as separate diagnostics and
            // memory-budget entries; keeping this to metadata avoids double accounting.
            _estimatedBytes = MetadataBufferSize;
        }

        private static long ElapsedMicroseconds(long startTimestamp) =>
            (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);

        private void DestroyCubemapArrayResources()
        {
            foreach (ImageView faceView in _captureFaceViews)
                if (faceView.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, faceView, null);
            _captureFaceViews = [];
            if (_cubemapArrayView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _cubemapArrayView, null);
            if (_debugCubemapView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _debugCubemapView, null);
            _cubemapArrayView = default;
            _debugCubemapView = default;
            if (_cubemapArrayAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _cubemapArrayImage, _cubemapArrayAllocation);
                _cubemapArrayAllocation = null;
                _cubemapArrayImage = default;
            }
            _cubemapArrayCapacity = 0;
            _cubemapArrayResolution = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _context.WaitIdle();
            DestroyCubemapArrayResources();
            if (_cubemapSampler.Handle != 0)
                _context.Api.DestroySampler(_context.Device, _cubemapSampler, null);
            if (_metadataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_metadataBuffer);
        }
    }

    public readonly record struct ReflectionProbeCapture(Guid ProbeId, int CubemapArrayIndex, uint Resolution, uint MipCount);
}
