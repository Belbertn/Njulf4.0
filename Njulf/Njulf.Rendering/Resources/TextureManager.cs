using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using StbImageSharp;
using GpuAllocator = Vma;
using Vma;
using Buffer = System.Buffer;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class TextureManager : IDisposable, ITextureReferenceManager
    {
        private const int UnassignedBindlessIndex = -1;
        internal const int MaximumRuntimeEncodedTextureBytes =
            TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes;
        internal const long MaximumRuntimeDecodedTexturePixels =
            WebPTextureDecoder.DefaultMaximumDecodedPixels;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap? _bindlessHeap;
        private readonly FenceBasedDeleter? _deleter;
        private readonly Dictionary<string, TextureHandle> _textureCache = new Dictionary<string, TextureHandle>(StringComparer.OrdinalIgnoreCase);
        // A sampled image and a sampler are independent Vulkan objects. Keep physical images in a
        // second cache so material slots that need different sampler states receive distinct
        // bindless descriptors without uploading a duplicate image allocation.
        private readonly Dictionary<string, SharedTextureImage> _textureImageCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TextureSamplerDescription, Sampler> _samplerCache = new();
        private readonly List<TextureInfo> _textures = new List<TextureInfo>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly Dictionary<TextureHandle, PendingTextureRetirement>
            _pendingTextureRetirements = new();
        private readonly List<PendingTextureCreationRollback>
            _pendingTextureCreationRollbacks = [];
        private readonly DurableTextureContentNotificationDispatcher
            _contentNotificationDispatcher = new();
        private readonly object _lock = new object();
        private readonly object _disposeGate = new();
        private readonly TextureManagerLifecycleState _lifecycle = new();

        private TextureHandle _defaultWhiteTexture = TextureHandle.Invalid;
        private TextureHandle _defaultNormalTexture = TextureHandle.Invalid;
        private TextureHandle _defaultBlackTexture = TextureHandle.Invalid;
        private readonly ResumableDefaultTextureInitialization
            _defaultTextureInitialization = new();
        private uint _maxLoadedTextureDimension = 2048;
        private int _mipmapFallbackCount;
        private int _downscaledTextureCount;
        private int _runtimeDecodedTextureCount;
        private int _runtimeAlphaCoverageMipTextureCount;
        private int _cookedTextureLoadCount;
        private ulong _estimatedTextureBytes;
        private TextureBudgetProfile _activeTextureBudgetProfile =
            TextureBudgetProfile.Development;

        private sealed class SharedTextureImage
        {
            public Image Image;
            public Allocation* Allocation;
            public ImageView View;
            public Format Format;
            public Extent3D Extent;
            public uint MipLevels;
            public uint ArrayLayers;
            public ulong EstimatedByteSize;
            public bool WasDownscaled;
            public int ReferenceCount = 1;
            public string? CacheKey;
            public string? SourcePath;
            public string? SourceIdentity;
            public TextureSourceKind SourceKind;
            public int SourceEncodedByteLength;
            public uint OriginalWidth;
            public uint OriginalHeight;
            public bool IsCompressed;
            public CoreVector4? LinearAverageColor;
            public TextureTransportStatistics? TransportStatistics;
            public DurableTextureDisposalProgress DisposalProgress { get; } =
                new();
            public uint ContentRevision = 1;
            public ulong SourceContentHash;
            public TextureSemantic Semantic;
            public bool Srgb;
            public bool GenerateMipmaps;
            public RuntimeTextureMipPolicy MipPolicy;
        }

        private sealed class TextureInfo
        {
            public SharedTextureImage? SharedImage;
            public Image Image;
            public Allocation* Allocation;
            public ImageView View;
            public Format Format;
            public Extent3D Extent;
            public uint MipLevels;
            public uint ArrayLayers;
            public uint Generation;
            public int BindlessIndex = UnassignedBindlessIndex;
            public BindlessHeap? BindlessHeap;
            public string? SourcePath;
            public string? SourceIdentity;
            public TextureSourceKind SourceKind;
            public int SourceEncodedByteLength;
            public uint OriginalWidth;
            public uint OriginalHeight;
            public int ReferenceCount = 1;
            public ulong EstimatedByteSize;
            public bool WasDownscaled;
            public bool IsCompressed;
            public string? DescriptorCacheKey;
            public TextureSamplerDescription? SamplerDescription;
            public bool IsRetiring;
            public DurableTextureDescriptorDisposalProgress
                DescriptorDisposalProgress
            { get; } = new();
        }

        private ulong _resourceGeneration = 1;

        private sealed class PendingTextureRetirement
        {
            public PendingTextureRetirement(
                TextureHandle detachedHandle,
                TextureInfo textureInfo,
                int bindlessIndex,
                BindlessHeap? bindlessHeap,
                Fence retireFence)
            {
                DetachedHandle = detachedHandle;
                TextureInfo = textureInfo;
                BindlessIndex = bindlessIndex;
                BindlessHeap = bindlessHeap;
                RetireFence = retireFence;
            }

            public object Gate { get; } = new();
            public TextureHandle DetachedHandle { get; }
            public TextureInfo TextureInfo { get; }
            public int BindlessIndex { get; }
            public BindlessHeap? BindlessHeap { get; }
            public Fence RetireFence { get; }
            public DurableTextureRetirementProgress Progress { get; } = new();
            public bool FenceWorkQueued;
            public ImageView RetiredView;
            public Image RetiredImage;
            public Allocation* RetiredAllocation;
        }

        private sealed class PendingTextureCreationRollback
        {
            public BindlessHeap? Heap;
            public int BindlessIndex = UnassignedBindlessIndex;
            public ImageView View;
            public Image Image;
            public Allocation* Allocation;
            public DurableTextureRetirementProgress Progress { get; } = new();
        }

        private readonly record struct DecodedStandardTexture(
            byte[] Data,
            int Width,
            int Height,
            string Decoder);

        public TextureManager(
            VulkanContext context,
            BufferManager bufferManager,
            BindlessHeap? bindlessHeap = null,
            FenceBasedDeleter? deleter = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bindlessHeap = bindlessHeap;
            _deleter = deleter;
        }

        /// <summary>
        /// Raised after decoded transport metadata for an existing physical
        /// image is atomically replaced. One notification is emitted for every
        /// live descriptor alias so material dependency keys remain exact.
        /// </summary>
        public event Action<TextureContentChangedEvent>? TextureContentChanged;

        /// <summary>Changes when an existing texture identity publishes a new physical image.</summary>
        public ulong ResourceGeneration
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _resourceGeneration;
                }
            }
        }

        public TextureHandle DefaultWhiteTexture
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _defaultWhiteTexture;
                }
            }
        }

        public TextureHandle DefaultNormalTexture
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _defaultNormalTexture;
                }
            }
        }

        public TextureHandle DefaultBlackTexture
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _defaultBlackTexture;
                }
            }
        }

        public int TextureCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    int count = 0;
                    var seenImages = new HashSet<SharedTextureImage>();
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (IsLiveTexture(textureInfo) &&
                            textureInfo.SharedImage != null &&
                            seenImages.Add(textureInfo.SharedImage))
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public int LoadedFileTextureCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    int count = 0;
                    var seenImages = new HashSet<SharedTextureImage>();
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (IsLiveTexture(textureInfo) &&
                            textureInfo.SharedImage != null &&
                            !string.IsNullOrWhiteSpace(textureInfo.SharedImage.SourceIdentity) &&
                            seenImages.Add(textureInfo.SharedImage))
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public int MipmapFallbackCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _mipmapFallbackCount;
                }
            }
        }

        public uint MaxLoadedTextureDimension
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _maxLoadedTextureDimension;
                }
            }
            set
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    _maxLoadedTextureDimension = value;
                }
            }
        }

        public TextureBudgetProfile ActiveTextureBudgetProfile
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _activeTextureBudgetProfile;
                }
            }
            set
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    _activeTextureBudgetProfile = value;
                }
            }
        }

        public int DownscaledTextureCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _downscaledTextureCount;
                }
            }
        }

        public ulong EstimatedTextureBytes
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _estimatedTextureBytes;
                }
            }
        }

        public ulong DefaultTextureBytes
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    ulong bytes = 0;
                    var seenImages = new HashSet<SharedTextureImage>();
                    AddTextureBytes(_defaultWhiteTexture, ref bytes, seenImages);
                    AddTextureBytes(_defaultNormalTexture, ref bytes, seenImages);
                    AddTextureBytes(_defaultBlackTexture, ref bytes, seenImages);
                    return bytes;
                }
            }
        }

        public ulong FileTextureBytes
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    ulong bytes = 0;
                    var seenImages = new HashSet<SharedTextureImage>();
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (IsLiveTexture(textureInfo) &&
                            textureInfo.SharedImage != null &&
                            !string.IsNullOrWhiteSpace(textureInfo.SharedImage.SourceIdentity) &&
                            seenImages.Add(textureInfo.SharedImage))
                        {
                            bytes += textureInfo.SharedImage.EstimatedByteSize;
                        }
                    }

                    return bytes;
                }
            }
        }

        public int TextureCacheEntryCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _textureCache.Count;
                }
            }
        }

        public int TextureBindlessUsedCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    int count = 0;
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (textureInfo.Image.Handle != 0 &&
                            textureInfo.View.Handle != 0 &&
                            textureInfo.BindlessIndex != UnassignedBindlessIndex)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public int TextureBindlessFreeCount => Math.Max(0, BindlessIndex.MaxTextures - BindlessIndex.FirstDynamicTextureIndex - TextureBindlessUsedCount);

        public IReadOnlyList<TextureAssetMemoryEntry> GetLargestFileTextures(int count)
        {
            ThrowIfDisposed();
            if (count <= 0)
                return Array.Empty<TextureAssetMemoryEntry>();

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                var entries = new List<TextureAssetMemoryEntry>();
                var seenImages = new HashSet<SharedTextureImage>();
                foreach (TextureInfo textureInfo in _textures)
                {
                    SharedTextureImage? sharedImage = textureInfo.SharedImage;
                    if (!IsLiveTexture(textureInfo) ||
                        sharedImage == null ||
                        string.IsNullOrWhiteSpace(sharedImage.SourceIdentity) ||
                        !seenImages.Add(sharedImage))
                    {
                        continue;
                    }

                    entries.Add(new TextureAssetMemoryEntry(
                        sharedImage.SourceIdentity,
                        sharedImage.Extent.Width,
                        sharedImage.Extent.Height,
                        sharedImage.MipLevels,
                        sharedImage.EstimatedByteSize,
                        sharedImage.WasDownscaled)
                    {
                        SourceKind = sharedImage.SourceKind.ToString(),
                        OriginalWidth = sharedImage.OriginalWidth == 0 ? sharedImage.Extent.Width : sharedImage.OriginalWidth,
                        OriginalHeight = sharedImage.OriginalHeight == 0 ? sharedImage.Extent.Height : sharedImage.OriginalHeight,
                        EncodedByteLength = sharedImage.SourceEncodedByteLength,
                        Format = sharedImage.Format.ToString(),
                        IsCompressed = sharedImage.IsCompressed
                    });
                }

                entries.Sort((left, right) => right.EstimatedBytes.CompareTo(left.EstimatedBytes));
                if (entries.Count > count)
                    entries.RemoveRange(count, entries.Count - count);
                return entries;
            }
        }

        private void AddTextureBytes(TextureHandle handle, ref ulong bytes, HashSet<SharedTextureImage> seenImages)
        {
            if (!handle.IsValid || handle.Index >= _textures.Count)
                return;

            TextureInfo textureInfo = _textures[handle.Index];
            if (textureInfo.Generation == handle.Generation &&
                IsLiveTexture(textureInfo) &&
                textureInfo.SharedImage != null &&
                seenImages.Add(textureInfo.SharedImage))
            {
                bytes += textureInfo.SharedImage.EstimatedByteSize;
            }
        }

        public int RuntimeDecodedTextureCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _runtimeDecodedTextureCount;
                }
            }
        }

        public int CookedTextureLoadCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _cookedTextureLoadCount;
                }
            }
        }

        public int PendingTextureContentNotificationCount
        {
            get
            {
                ThrowIfDisposed();
                return _contentNotificationDispatcher.PendingCount;
            }
        }

        public long TextureContentNotificationFailureCount
        {
            get
            {
                ThrowIfDisposed();
                return _contentNotificationDispatcher.FailureCount;
            }
        }

        public Exception? LastTextureContentNotificationFailure
        {
            get
            {
                ThrowIfDisposed();
                return _contentNotificationDispatcher.LastFailure;
            }
        }

        internal Action<TexturePublicationCheckpoint>?
            PublicationCheckpointForTesting
        { get; set; }

        internal int PendingTextureRetirementCount
        {
            get
            {
                lock (_lock)
                    return _pendingTextureRetirements.Count;
            }
        }

        public int RuntimeAlphaCoverageMipTextureCount
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    return _runtimeAlphaCoverageMipTextureCount;
                }
            }
        }

        public void InitializeDefaultTextures(BindlessHeap? bindlessHeap = null)
        {
            ThrowIfDisposed();
            BindlessHeap heap = ResolveBindlessHeap(bindlessHeap);

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                _defaultTextureInitialization.Execute(
                    () => InitializeSolidTexture(
                        ref _defaultWhiteTexture,
                        "default:white",
                        [255, 255, 255, 255],
                        Format.R8G8B8A8Unorm,
                        BindlessIndex.DefaultWhiteTexture,
                        heap),
                    () => InitializeSolidTexture(
                        ref _defaultNormalTexture,
                        "default:normal",
                        [128, 128, 255, 255],
                        Format.R8G8B8A8Unorm,
                        BindlessIndex.DefaultNormalTexture,
                        heap),
                    () => InitializeSolidTexture(
                        ref _defaultBlackTexture,
                        "default:black",
                        [0, 0, 0, 255],
                        Format.R8G8B8A8Unorm,
                        BindlessIndex.DefaultBlackTexture,
                        heap),
                    PublicationCheckpointForTesting);
            }
        }

        public TextureHandle CreateTexture(
            uint width,
            uint height,
            Format format,
            uint mipLevels = 1,
            uint arrayLayers = 1,
            ImageUsageFlags additionalUsage = ImageUsageFlags.None,
            int bindlessIndex = UnassignedBindlessIndex,
            BindlessHeap? bindlessHeap = null,
            TextureSamplerDescription? samplerDescription = null,
            bool requireWithinMemoryBudget = false,
            string? debugName = null)
        {
            ThrowIfDisposed();
            FlushPendingTextureCreationRollbacks();
            if (width == 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height == 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (mipLevels == 0)
                throw new ArgumentOutOfRangeException(nameof(mipLevels));
            if (arrayLayers == 0)
                throw new ArgumentOutOfRangeException(nameof(arrayLayers));

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                bool reuseSlot = _freeIndices.Count > 0;
                int index = reuseSlot ? _freeIndices.Peek() : _textures.Count;
                uint generation = AllocateGeneration(index);
                ulong estimatedByteSize = CalculateTextureByteSize(
                    width,
                    height,
                    format,
                    mipLevels,
                    arrayLayers);
                ulong finalEstimatedTextureBytes = checked(
                    _estimatedTextureBytes + estimatedByteSize);
                if (!reuseSlot)
                    _textures.EnsureCapacity(checked(_textures.Count + 1));
                _pendingTextureCreationRollbacks.EnsureCapacity(
                    checked(_pendingTextureCreationRollbacks.Count + 1));

                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = format,
                    Extent = new Extent3D { Width = width, Height = height, Depth = 1 },
                    MipLevels = mipLevels,
                    ArrayLayers = arrayLayers,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.SampledBit |
                            ImageUsageFlags.TransferDstBit |
                            ImageUsageFlags.TransferSrcBit |
                            additionalUsage,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined
                };

                var allocInfo = new AllocationCreateInfo
                {
                    Usage = MemoryUsage.AutoPreferDevice,
                    Flags = requireWithinMemoryBudget && _context.MemoryBudgetExtensionEnabled
                        ? AllocationCreateFlags.WithinBudgetBit
                        : default
                };

                Image image = default;
                Allocation* allocation = null;
                AllocationInfo allocationInfo;
                ImageView view = default;
                int textureBindlessIndex = UnassignedBindlessIndex;
                bool dynamicBindlessAllocated = false;
                bool imageCreated = false;
                bool viewCreated = false;
                bool slotReserved = false;
                bool slotPublished = false;
                bool accountingPublished = false;
                TextureInfo? previousSlot =
                    reuseSlot ? _textures[index] : null;
                var pendingRollback = new PendingTextureCreationRollback();
                try
                {
                    Result result = GpuAllocator.Apis.CreateImage(
                        _context.Allocator,
                        &imageInfo,
                        &allocInfo,
                        &image,
                        &allocation,
                        &allocationInfo);
                    if (result != Result.Success)
                        throw new VulkanException("Failed to create texture image", result);
                    imageCreated = true;

                    string textureDebugName = string.IsNullOrWhiteSpace(debugName)
                        ? $"Texture Image[{index}] {width}x{height} {format}"
                        : $"{debugName} {width}x{height} {format}";
                    _context.SetDebugName(
                        image.Handle,
                        ObjectType.Image,
                        textureDebugName);
                    view = CreateImageView(image, format, ImageAspectFlags.ColorBit, mipLevels, arrayLayers);
                    viewCreated = true;
                    _context.SetDebugName(
                        view.Handle,
                        ObjectType.ImageView,
                        $"{textureDebugName} View");

                    var sharedImage = new SharedTextureImage
                    {
                        Image = image,
                        Allocation = allocation,
                        View = view,
                        Format = format,
                        Extent = imageInfo.Extent,
                        MipLevels = mipLevels,
                        ArrayLayers = arrayLayers,
                        EstimatedByteSize = estimatedByteSize
                    };
                    var textureInfo = new TextureInfo
                    {
                        SharedImage = sharedImage,
                        Image = image,
                        Allocation = allocation,
                        View = view,
                        Format = format,
                        Extent = imageInfo.Extent,
                        MipLevels = mipLevels,
                        ArrayLayers = arrayLayers,
                        Generation = generation,
                        BindlessIndex = UnassignedBindlessIndex,
                        BindlessHeap = bindlessHeap ?? _bindlessHeap,
                        EstimatedByteSize = estimatedByteSize,
                        SamplerDescription = samplerDescription
                    };

                    // Bindless publication is the last externally fallible
                    // operation. Every CPU container/accounting allocation was
                    // preflighted above.
                    textureBindlessIndex = AllocateOrRegisterBindlessIndex(
                        bindlessIndex,
                        view,
                        bindlessHeap,
                        samplerDescription);
                    dynamicBindlessAllocated =
                        textureBindlessIndex >=
                        BindlessIndex.FirstDynamicTextureIndex;
                    textureInfo.BindlessIndex = textureBindlessIndex;

                    if (reuseSlot)
                    {
                        int reservedIndex = _freeIndices.Pop();
                        if (reservedIndex != index)
                        {
                            throw new InvalidOperationException(
                                "Texture free-slot reservation changed while its lock was held.");
                        }
                        slotReserved = true;
                        _textures[index] = textureInfo;
                    }
                    else
                    {
                        _textures.Add(textureInfo);
                    }
                    slotPublished = true;
                    if (bindlessIndex < 0)
                    {
                        PublicationCheckpointForTesting?.Invoke(
                            TexturePublicationCheckpoint.TextureSlotPublished);
                    }

                    _estimatedTextureBytes = finalEstimatedTextureBytes;
                    accountingPublished = true;
                    if (bindlessIndex < 0)
                    {
                        PublicationCheckpointForTesting?.Invoke(
                            TexturePublicationCheckpoint.TextureAccountingPublished);
                    }

                    return new TextureHandle(index, textureInfo.Generation);
                }
                catch (Exception creationFailure)
                {
                    List<Exception>? rollbackFailures = null;
                    try
                    {
                        if (accountingPublished)
                            _estimatedTextureBytes -= estimatedByteSize;
                        if (slotPublished)
                        {
                            if (reuseSlot)
                                _textures[index] = previousSlot!;
                            else
                                _textures.RemoveAt(index);
                        }
                        if (slotReserved)
                            _freeIndices.Push(index);
                    }
                    catch (Exception rollbackFailure)
                    {
                        (rollbackFailures ??= []).Add(rollbackFailure);
                    }

                    if (dynamicBindlessAllocated)
                    {
                        pendingRollback.Heap =
                            bindlessHeap ?? _bindlessHeap;
                        pendingRollback.BindlessIndex =
                            textureBindlessIndex;
                        pendingRollback.View =
                            viewCreated ? view : default;
                        pendingRollback.Image =
                            imageCreated ? image : default;
                        pendingRollback.Allocation =
                            imageCreated ? allocation : null;
                        _pendingTextureCreationRollbacks.Add(
                            pendingRollback);
                        try
                        {
                            ExecuteTextureCreationRollback(
                                pendingRollback);
                        }
                        catch (Exception rollbackFailure)
                        {
                            (rollbackFailures ??= []).Add(rollbackFailure);
                        }
                    }
                    else if (viewCreated)
                    {
                        try
                        {
                            _context.Api.DestroyImageView(
                                _context.Device,
                                view,
                                null);
                        }
                        catch (Exception rollbackFailure)
                        {
                            (rollbackFailures ??= []).Add(rollbackFailure);
                        }
                    }
                    if (!dynamicBindlessAllocated && imageCreated)
                    {
                        try
                        {
                            GpuAllocator.Apis.DestroyImage(
                                _context.Allocator,
                                image,
                                allocation);
                        }
                        catch (Exception rollbackFailure)
                        {
                            (rollbackFailures ??= []).Add(rollbackFailure);
                        }
                    }

                    if (rollbackFailures is { Count: > 0 })
                    {
                        throw new AggregateException(
                            "Texture creation failed and rollback was incomplete.",
                            [creationFailure, .. rollbackFailures]);
                    }

                    throw;
                }
            }
        }

        public TextureHandle CreateCubemap(
            uint size,
            Format format,
            uint mipLevels = 1,
            ImageUsageFlags additionalUsage = ImageUsageFlags.None,
            int bindlessIndex = UnassignedBindlessIndex,
            BindlessHeap? bindlessHeap = null,
            string? debugName = null)
        {
            ThrowIfDisposed();
            FlushPendingTextureCreationRollbacks();
            if (size == 0)
                throw new ArgumentOutOfRangeException(nameof(size));
            if (mipLevels == 0)
                throw new ArgumentOutOfRangeException(nameof(mipLevels));

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                bool reuseSlot = _freeIndices.Count > 0;
                int index = reuseSlot ? _freeIndices.Peek() : _textures.Count;
                uint generation = AllocateGeneration(index);
                ulong estimatedByteSize = CalculateTextureByteSize(
                    size,
                    size,
                    format,
                    mipLevels,
                    6);
                ulong finalEstimatedTextureBytes = checked(
                    _estimatedTextureBytes + estimatedByteSize);
                if (!reuseSlot)
                    _textures.EnsureCapacity(checked(_textures.Count + 1));
                _pendingTextureCreationRollbacks.EnsureCapacity(
                    checked(_pendingTextureCreationRollbacks.Count + 1));

                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                    ImageType = ImageType.Type2D,
                    Format = format,
                    Extent = new Extent3D { Width = size, Height = size, Depth = 1 },
                    MipLevels = mipLevels,
                    ArrayLayers = 6,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.SampledBit |
                            ImageUsageFlags.TransferDstBit |
                            ImageUsageFlags.TransferSrcBit |
                            additionalUsage,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined
                };

                var allocInfo = new AllocationCreateInfo
                {
                    Usage = MemoryUsage.AutoPreferDevice
                };

                Image image = default;
                Allocation* allocation = null;
                AllocationInfo allocationInfo;
                ImageView view = default;
                int textureBindlessIndex = UnassignedBindlessIndex;
                bool dynamicBindlessAllocated = false;
                bool imageCreated = false;
                bool viewCreated = false;
                bool slotReserved = false;
                bool slotPublished = false;
                bool accountingPublished = false;
                TextureInfo? previousSlot =
                    reuseSlot ? _textures[index] : null;
                var pendingRollback = new PendingTextureCreationRollback();
                try
                {
                    Result result = GpuAllocator.Apis.CreateImage(
                        _context.Allocator,
                        &imageInfo,
                        &allocInfo,
                        &image,
                        &allocation,
                        &allocationInfo);
                    if (result != Result.Success)
                        throw new VulkanException("Failed to create cubemap image", result);
                    imageCreated = true;

                    _context.SetDebugName(
                        image.Handle,
                        ObjectType.Image,
                        debugName ?? $"Cubemap Image[{index}] {size} {format}");
                    view = CreateImageView(
                        image,
                        format,
                        ImageAspectFlags.ColorBit,
                        mipLevels,
                        6,
                        ImageViewType.TypeCube);
                    viewCreated = true;
                    _context.SetDebugName(
                        view.Handle,
                        ObjectType.ImageView,
                        debugName == null
                            ? $"Cubemap Image View[{index}]"
                            : $"{debugName} View");

                    var sharedImage = new SharedTextureImage
                    {
                        Image = image,
                        Allocation = allocation,
                        View = view,
                        Format = format,
                        Extent = imageInfo.Extent,
                        MipLevels = mipLevels,
                        ArrayLayers = 6,
                        EstimatedByteSize = estimatedByteSize
                    };
                    var textureInfo = new TextureInfo
                    {
                        SharedImage = sharedImage,
                        Image = image,
                        Allocation = allocation,
                        View = view,
                        Format = format,
                        Extent = imageInfo.Extent,
                        MipLevels = mipLevels,
                        ArrayLayers = 6,
                        Generation = generation,
                        BindlessIndex = UnassignedBindlessIndex,
                        BindlessHeap = bindlessHeap ?? _bindlessHeap,
                        EstimatedByteSize = estimatedByteSize
                    };

                    textureBindlessIndex = AllocateOrRegisterBindlessIndex(
                        bindlessIndex,
                        view,
                        bindlessHeap);
                    dynamicBindlessAllocated =
                        textureBindlessIndex >=
                        BindlessIndex.FirstDynamicTextureIndex;
                    textureInfo.BindlessIndex = textureBindlessIndex;

                    if (reuseSlot)
                    {
                        int reservedIndex = _freeIndices.Pop();
                        if (reservedIndex != index)
                        {
                            throw new InvalidOperationException(
                                "Texture free-slot reservation changed while its lock was held.");
                        }
                        slotReserved = true;
                        _textures[index] = textureInfo;
                    }
                    else
                    {
                        _textures.Add(textureInfo);
                    }
                    slotPublished = true;
                    if (bindlessIndex < 0)
                    {
                        PublicationCheckpointForTesting?.Invoke(
                            TexturePublicationCheckpoint.TextureSlotPublished);
                    }

                    _estimatedTextureBytes = finalEstimatedTextureBytes;
                    accountingPublished = true;
                    if (bindlessIndex < 0)
                    {
                        PublicationCheckpointForTesting?.Invoke(
                            TexturePublicationCheckpoint.TextureAccountingPublished);
                    }

                    return new TextureHandle(index, textureInfo.Generation);
                }
                catch (Exception creationFailure)
                {
                    List<Exception>? rollbackFailures = null;
                    try
                    {
                        if (accountingPublished)
                            _estimatedTextureBytes -= estimatedByteSize;
                        if (slotPublished)
                        {
                            if (reuseSlot)
                                _textures[index] = previousSlot!;
                            else
                                _textures.RemoveAt(index);
                        }
                        if (slotReserved)
                            _freeIndices.Push(index);
                    }
                    catch (Exception rollbackFailure)
                    {
                        (rollbackFailures ??= []).Add(rollbackFailure);
                    }

                    if (dynamicBindlessAllocated)
                    {
                        pendingRollback.Heap =
                            bindlessHeap ?? _bindlessHeap;
                        pendingRollback.BindlessIndex =
                            textureBindlessIndex;
                        pendingRollback.View =
                            viewCreated ? view : default;
                        pendingRollback.Image =
                            imageCreated ? image : default;
                        pendingRollback.Allocation =
                            imageCreated ? allocation : null;
                        _pendingTextureCreationRollbacks.Add(
                            pendingRollback);
                        try
                        {
                            ExecuteTextureCreationRollback(
                                pendingRollback);
                        }
                        catch (Exception rollbackFailure)
                        {
                            (rollbackFailures ??= []).Add(rollbackFailure);
                        }
                    }
                    else if (viewCreated)
                    {
                        try
                        {
                            _context.Api.DestroyImageView(
                                _context.Device,
                                view,
                                null);
                        }
                        catch (Exception rollbackFailure)
                        {
                            (rollbackFailures ??= []).Add(rollbackFailure);
                        }
                    }
                    if (!dynamicBindlessAllocated && imageCreated)
                    {
                        try
                        {
                            GpuAllocator.Apis.DestroyImage(
                                _context.Allocator,
                                image,
                                allocation);
                        }
                        catch (Exception rollbackFailure)
                        {
                            (rollbackFailures ??= []).Add(rollbackFailure);
                        }
                    }

                    if (rollbackFailures is { Count: > 0 })
                    {
                        throw new AggregateException(
                            "Cubemap creation failed and rollback was incomplete.",
                            [creationFailure, .. rollbackFailures]);
                    }

                    throw;
                }
            }
        }

        public TextureHandle LoadTextureFromFile(
            string path,
            bool generateMipmaps = true,
            bool srgb = true,
            bool requireWithinMemoryBudget = false,
            TextureSemantic semantic = TextureSemantic.Color,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            ThrowIfDisposed();
            return LoadTexture(
                new ModelTextureSource
                {
                    DebugName = Path.GetFileName(path),
                    FilePath = path,
                    CacheIdentity = Path.GetFullPath(path)
                },
                TextureSamplerDescription.Default,
                generateMipmaps,
                srgb,
                requireWithinMemoryBudget,
                semantic,
                mipPolicy);
        }

        public TextureHandle LoadTexture(
            ModelTextureSource source,
            TextureSamplerDescription samplerDescription,
            bool generateMipmaps = true,
            bool srgb = true,
            bool requireWithinMemoryBudget = false,
            TextureSemantic semantic = TextureSemantic.Color,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            ThrowIfDisposed();
            FlushPendingTextureCreationRollbacks();
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(source.FilePath) && source.Bytes is not { Length: > 0 })
                throw new ArgumentException("Texture source must provide a file path or memory bytes.", nameof(source));

            RuntimeTextureMipPolicy normalizedMipPolicy = mipPolicy.ValidateAndNormalize();
            string? fullPath = string.IsNullOrWhiteSpace(source.FilePath) ? null : Path.GetFullPath(source.FilePath);
            uint maxTextureDimension = MaxLoadedTextureDimension;
            string cacheIdentity = ResolveSourceIdentity(source, fullPath);
            if (fullPath != null)
            {
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Texture file was not found: {fullPath}", fullPath);
            }
            byte[] imageBytes = ReadTextureSourceBytes(source, out fullPath);

            if (IsGitLfsPointer(imageBytes))
            {
                throw new InvalidOperationException(
                    $"Texture source '{cacheIdentity}' is a Git LFS pointer file, not image data. " +
                    "Fetch the LFS object or replace the pointer with the real image file before loading this asset.");
            }

            // Cache lookup deliberately follows the byte read. File identity,
            // length, and timestamps are insufficient for editor hot reload:
            // source bytes can change while all three remain stable.
            ulong sourceContentHash = CalculateTextureSourceContentHash(imageBytes);
            bool isKtx2 = IsKtx2Source(source, fullPath);
            AuthenticatedCookedTexture? authenticatedCookedTexture =
                isKtx2 && IsCookedSource(source)
                    ? AuthenticateCookedTexture(
                        source,
                        fullPath,
                        imageBytes,
                        samplerDescription,
                        srgb,
                        semantic,
                        normalizedMipPolicy)
                    : null;
            ulong cacheContentHash =
                authenticatedCookedTexture?.PublicationContentHash ??
                sourceContentHash;
            TextureContainerKind effectiveContainerKind =
                WebPTextureDecoder.IsDeclaredWebP(source, imageBytes)
                    ? TextureContainerKind.WebP
                    : source.ContainerKind;
            string cacheKey = CreateTextureCacheKey(
                cacheIdentity,
                generateMipmaps,
                srgb,
                maxTextureDimension,
                samplerDescription,
                effectiveContainerKind,
                cacheContentHash,
                semantic,
                normalizedMipPolicy);
            string imageCacheKey = CreateTextureImageCacheKey(
                cacheIdentity,
                generateMipmaps,
                srgb,
                maxTextureDimension,
                effectiveContainerKind,
                cacheContentHash,
                semantic,
                normalizedMipPolicy);
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (_textureCache.TryGetValue(cacheKey, out TextureHandle cachedHandle))
                {
                    TextureInfo cachedTextureInfo = GetTextureInfoLocked(cachedHandle);
                    RetainLogicalTextureReference(
                        ref cachedTextureInfo.ReferenceCount);
                    return cachedHandle;
                }

                if (_textureImageCache.TryGetValue(imageCacheKey, out SharedTextureImage? sharedImage))
                    return CreateSharedTextureAliasLocked(sharedImage, samplerDescription, cacheKey);
            }

            if (isKtx2)
                return LoadKtx2Texture(
                    source,
                    imageBytes,
                    cacheIdentity,
                    cacheKey,
                    imageCacheKey,
                    samplerDescription,
                    requireWithinMemoryBudget,
                    srgb,
                    semantic,
                    sourceContentHash,
                    normalizedMipPolicy,
                    authenticatedCookedTexture);

            DecodedStandardTexture image =
                DecodeStandardTexture(source, imageBytes, cacheIdentity);
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                _runtimeDecodedTextureCount++;
            }

            // Capture source-resolution statistics before downscaling. Normal
            // variance is computed in the same pass for every normalized
            // texture; non-normal consumers simply ignore that valid channel.
            TextureTransportStatistics transportStatistics = TextureTransportImage.FromRgba8(
                image.Data,
                image.Width,
                image.Height,
                srgb ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
                TextureSemantic.Normal,
                sourceContentHash,
                image.Decoder).Statistics with
            {
                Semantic = semantic
            };
            CoreVector4 linearAverageColor = transportStatistics.LinearChannelMean.ToVector4();
            Format format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
            uint originalWidth = checked((uint)image.Width);
            uint originalHeight = checked((uint)image.Height);
            uint width = originalWidth;
            uint height = originalHeight;
            bool wasDownscaled = false;
            byte[] textureData = image.Data;
            double sourceAlphaCoverage = normalizedMipPolicy.PreserveAlphaCoverage
                ? AlphaCoverageMipGenerator.CalculateCoverage(
                    textureData,
                    normalizedMipPolicy.AlphaCutoff)
                : 0.0;
            if (TryDownscaleRgba(textureData, width, height, maxTextureDimension, out byte[]? downscaledData, out uint downscaledWidth, out uint downscaledHeight))
            {
                textureData = downscaledData ?? throw new InvalidOperationException("Texture downscale reported success without output data.");
                width = downscaledWidth;
                height = downscaledHeight;
                wasDownscaled = true;
                if (normalizedMipPolicy.PreserveAlphaCoverage)
                {
                    AlphaCoverageMipGenerator.PreserveCoverage(
                        textureData,
                        normalizedMipPolicy.AlphaCutoff,
                        sourceAlphaCoverage);
                }
            }

            RuntimeRgbaMipChain? runtimeMipChain =
                generateMipmaps && normalizedMipPolicy.PreserveAlphaCoverage
                    ? BuildRuntimeRgbaMipChain(
                        textureData,
                        width,
                        height,
                        srgb,
                        normalizedMipPolicy,
                        sourceAlphaCoverage)
                    : null;
            bool canGenerateMipmaps = generateMipmaps &&
                                      (runtimeMipChain != null || SupportsLinearBlit(format));
            uint mipLevels = canGenerateMipmaps
                ? CalculateMipLevels(width, height)
                : 1;

            if (generateMipmaps && !canGenerateMipmaps)
            {
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    _mipmapFallbackCount++;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"Texture '{fullPath}' uses one mip level because format {format} does not support linear blit mip generation.");
            }

            TextureHandle handle = CreateTexture(
                width,
                height,
                format,
                mipLevels,
                samplerDescription: samplerDescription,
                requireWithinMemoryBudget: requireWithinMemoryBudget,
                debugName: CreateSampledTextureDebugName(source, fullPath));
            try
            {
                if (runtimeMipChain != null)
                {
                    UploadTextureDataAllMipsAndLayers(
                        handle,
                        runtimeMipChain.ContiguousPixels,
                        width,
                        height,
                        format);
                }
                else
                {
                    UploadTextureData(
                        handle,
                        textureData,
                        width,
                        height,
                        format,
                        generateMipmaps: mipLevels > 1);
                }

                TextureHandle resolvedHandle = RegisterLoadedTexture(
                    handle,
                    cacheKey,
                    imageCacheKey,
                    samplerDescription,
                    fullPath,
                    cacheIdentity,
                    ResolveSourceKind(source, fullPath),
                    source.EncodedByteLength > 0 ? source.EncodedByteLength : imageBytes.Length,
                    originalWidth,
                    originalHeight,
                    wasDownscaled,
                    isCompressed: false,
                    linearAverageColor,
                    transportStatistics,
                    sourceContentHash,
                    semantic,
                    srgb,
                    generateMipmaps,
                    normalizedMipPolicy,
                    CreateLoadedTextureDebugName(fullPath, source.DebugName, format));
                if (resolvedHandle != handle)
                {
                    DestroyTexture(handle);
                    return resolvedHandle;
                }
                if (runtimeMipChain != null)
                {
                    lock (_lock)
                    {
                        _lifecycle.ThrowIfDisposedUnderGate(_lock);
                        _runtimeAlphaCoverageMipTextureCount++;
                    }
                }
            }
            catch
            {
                DestroyTexture(handle);
                throw;
            }

            return handle;
        }

        public static TextureAssetMemoryEntry InspectTextureSourceBudget(
            ModelTextureSource source,
            bool generateMipmaps = true,
            bool srgb = true,
            uint maxDimension = 0)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            byte[] imageBytes = ReadTextureSourceBytes(source, out string? fullPath);
            if (IsGitLfsPointer(imageBytes))
                throw new InvalidOperationException($"Texture source '{ResolveSourceIdentity(source, fullPath)}' is a Git LFS pointer file, not image data.");

            string identity = ResolveSourceIdentity(source, fullPath);
            TextureSourceKind sourceKind = ResolveSourceKind(source, fullPath);

            if (IsKtx2Source(source, fullPath))
            {
                Ktx2Texture texture = Ktx2Texture.Parse(imageBytes, identity);
                ulong estimatedBytes = ImageByteEstimator.EstimateBytes(
                    texture.Format,
                    new Extent3D { Width = texture.Width, Height = texture.Height, Depth = 1 },
                    texture.MipLevels);

                return new TextureAssetMemoryEntry(
                    identity,
                    texture.Width,
                    texture.Height,
                    texture.MipLevels,
                    estimatedBytes,
                    WasDownscaled: false)
                {
                    SourceKind = sourceKind.ToString(),
                    OriginalWidth = texture.Width,
                    OriginalHeight = texture.Height,
                    EncodedByteLength = source.EncodedByteLength > 0 ? source.EncodedByteLength : imageBytes.Length,
                    Format = texture.Format.ToString(),
                    IsCompressed = IsBlockCompressedFormat(texture.Format)
                };
            }

            DecodedStandardTexture image =
                DecodeStandardTexture(source, imageBytes, identity);

            uint originalWidth = checked((uint)image.Width);
            uint originalHeight = checked((uint)image.Height);
            CalculateImportedExtent(originalWidth, originalHeight, maxDimension, out uint importedWidth, out uint importedHeight, out bool wasDownscaled);
            uint mipLevels = generateMipmaps ? CalculateMipLevels(importedWidth, importedHeight) : 1u;
            Format format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
            ulong bytes = ImageByteEstimator.EstimateBytes(
                format,
                new Extent3D { Width = importedWidth, Height = importedHeight, Depth = 1 },
                mipLevels);

            return new TextureAssetMemoryEntry(
                identity,
                importedWidth,
                importedHeight,
                mipLevels,
                bytes,
                wasDownscaled)
            {
                SourceKind = sourceKind.ToString(),
                OriginalWidth = originalWidth,
                OriginalHeight = originalHeight,
                EncodedByteLength = source.EncodedByteLength > 0 ? source.EncodedByteLength : imageBytes.Length,
                Format = format.ToString(),
                IsCompressed = false
            };
        }

        private static DecodedStandardTexture DecodeStandardTexture(
            ModelTextureSource source,
            byte[] encoded,
            string sourceIdentity)
        {
            try
            {
                if (WebPTextureDecoder.IsDeclaredWebP(source, encoded))
                {
                    WebPDecodedImage webP = WebPTextureDecoder.DecodeRgba8(
                        encoded,
                        sourceIdentity);
                    return new DecodedStandardTexture(
                        webP.Rgba8,
                        webP.Width,
                        webP.Height,
                        TextureTransportStatistics.WebPDecoderVersion);
                }

                (int expectedWidth, int expectedHeight) =
                    InspectRuntimeStandardTextureHeader(encoded, sourceIdentity);
                ImageResult stb = ImageResult.FromMemory(
                    encoded,
                    ColorComponents.RedGreenBlueAlpha);
                if (stb.Width != expectedWidth ||
                    stb.Height != expectedHeight ||
                    stb.Data.LongLength !=
                    checked((long)expectedWidth * expectedHeight * 4L))
                {
                    throw new InvalidDataException(
                        $"Texture source '{sourceIdentity}' decoded to an inconsistent " +
                        "RGBA image.");
                }

                return new DecodedStandardTexture(
                    stb.Data,
                    stb.Width,
                    stb.Height,
                    TextureTransportStatistics.StbDecoderVersion);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException(
                    $"Texture source '{sourceIdentity}' could not be decoded as a supported image.",
                    ex);
            }
        }

        internal static void EnsureRuntimeTextureDecodeDimensions(
            int width,
            int height,
            string sourceIdentity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException(
                    $"Texture source '{sourceIdentity}' declares invalid dimensions " +
                    $"{width}x{height}.");
            }

            long pixelCount;
            try
            {
                pixelCount = checked((long)width * height);
            }
            catch (OverflowException ex)
            {
                throw new NotSupportedException(
                    $"Texture source '{sourceIdentity}' dimensions {width}x{height} " +
                    "overflow the runtime pixel budget.",
                    ex);
            }

            if (pixelCount > MaximumRuntimeDecodedTexturePixels)
            {
                throw new NotSupportedException(
                    $"Texture source '{sourceIdentity}' contains {pixelCount} decoded pixels, " +
                    $"exceeding the runtime decode limit {MaximumRuntimeDecodedTexturePixels}.");
            }
        }

        internal static byte[] ReadTextureSourceBytes(
            ModelTextureSource source,
            out string? fullPath)
        {
            ArgumentNullException.ThrowIfNull(source);
            fullPath = string.IsNullOrWhiteSpace(source.FilePath) ? null : Path.GetFullPath(source.FilePath);
            if (fullPath != null)
            {
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Texture file was not found: {fullPath}", fullPath);

                string sourceIdentity = ResolveSourceIdentity(source, fullPath);
                if (WebPTextureDecoder.IsDeclaredWebP(source) ||
                    WebPTextureDecoder.FileHasWebPSignature(fullPath))
                {
                    return WebPTextureDecoder.ReadBoundedFile(
                        fullPath,
                        sourceIdentity,
                        MaximumRuntimeEncodedTextureBytes);
                }

                return ReadBoundedTextureFile(fullPath, sourceIdentity);
            }

            if (source.Bytes is { Length: > 0 } sourceBytes)
            {
                if (sourceBytes.Length > MaximumRuntimeEncodedTextureBytes)
                {
                    throw new NotSupportedException(
                        $"Texture source '{ResolveSourceIdentity(source, fullPath)}' contains " +
                        $"{sourceBytes.Length} encoded bytes, exceeding the runtime decode limit " +
                        $"{MaximumRuntimeEncodedTextureBytes}.");
                }

                return sourceBytes.ToArray();
            }

            throw new ArgumentException("Texture source must provide a file path or memory bytes.", nameof(source));
        }

        private static (int Width, int Height) InspectRuntimeStandardTextureHeader(
            byte[] encoded,
            string sourceIdentity)
        {
            using var stream = new MemoryStream(encoded, writable: false);
            ImageInfo? info = ImageInfo.FromStream(stream);
            if (info is null)
            {
                throw new InvalidDataException(
                    $"Texture source '{sourceIdentity}' has no supported image header.");
            }

            EnsureRuntimeTextureDecodeDimensions(
                info.Value.Width,
                info.Value.Height,
                sourceIdentity);
            return (info.Value.Width, info.Value.Height);
        }

        private static byte[] ReadBoundedTextureFile(
            string fullPath,
            string sourceIdentity)
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            long declaredLength = stream.Length;
            if (declaredLength <= 0)
            {
                throw new InvalidDataException(
                    $"Texture source '{sourceIdentity}' is empty.");
            }
            if (declaredLength > MaximumRuntimeEncodedTextureBytes)
            {
                throw new NotSupportedException(
                    $"Texture source '{sourceIdentity}' contains {declaredLength} encoded bytes, " +
                    $"exceeding the runtime decode limit {MaximumRuntimeEncodedTextureBytes}.");
            }

            byte[] encoded = GC.AllocateUninitializedArray<byte>(
                checked((int)declaredLength));
            int totalRead = 0;
            while (totalRead < encoded.Length)
            {
                int read = stream.Read(encoded, totalRead, encoded.Length - totalRead);
                if (read == 0)
                {
                    throw new IOException(
                        $"Texture source '{sourceIdentity}' changed during its bounded read: " +
                        $"{declaredLength} bytes were admitted but only {totalRead} remained.");
                }

                totalRead += read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new IOException(
                    $"Texture source '{sourceIdentity}' grew beyond its admitted " +
                    $"{declaredLength}-byte length during the bounded read.");
            }

            return encoded;
        }

        private static string ResolveSourceIdentity(ModelTextureSource source, string? fullPath)
        {
            if (!string.IsNullOrWhiteSpace(source.CacheIdentity))
                return source.CacheIdentity;
            if (!string.IsNullOrWhiteSpace(fullPath))
                return Path.GetFullPath(fullPath);
            return string.IsNullOrWhiteSpace(source.DebugName) ? "UnnamedTexture" : source.DebugName;
        }

        private static void CalculateImportedExtent(
            uint sourceWidth,
            uint sourceHeight,
            uint maxDimension,
            out uint destinationWidth,
            out uint destinationHeight,
            out bool wasDownscaled)
        {
            destinationWidth = sourceWidth;
            destinationHeight = sourceHeight;
            wasDownscaled = false;
            if (maxDimension == 0 || Math.Max(sourceWidth, sourceHeight) <= maxDimension)
                return;

            double scale = maxDimension / (double)Math.Max(sourceWidth, sourceHeight);
            destinationWidth = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
            destinationHeight = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
            wasDownscaled = true;
        }

        private static bool IsGitLfsPointer(ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<byte> gitLfsHeader = "version https://git-lfs.github.com/spec/v1"u8;
            return data.StartsWith(gitLfsHeader);
        }

        private TextureHandle LoadKtx2Texture(
            ModelTextureSource source,
            byte[] imageBytes,
            string cacheIdentity,
            string cacheKey,
            string imageCacheKey,
            TextureSamplerDescription samplerDescription,
            bool requireWithinMemoryBudget,
            bool srgb,
            TextureSemantic semantic,
            ulong sourceContentHash,
            RuntimeTextureMipPolicy mipPolicy,
            AuthenticatedCookedTexture? authenticatedCookedTexture)
        {
            Ktx2Texture texture = Ktx2Texture.Parse(imageBytes, cacheIdentity);
            TextureTransportStatistics transportStatistics =
                authenticatedCookedTexture?.Metadata.TransportStatistics ??
                TextureCooker.AnalyzeTransportStatistics(
                    imageBytes,
                    TextureContainerKind.Ktx2,
                    cacheIdentity,
                    new TextureCookOptions(
                        ColorSpace: ResolveExpectedColorSpace(srgb, semantic),
                        TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                        Semantic: semantic == TextureSemantic.Hdr
                            ? TextureSemantic.Hdr
                            : TextureSemantic.Normal)) with
                {
                    Semantic = semantic
                };
            if (authenticatedCookedTexture != null)
            {
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    _cookedTextureLoadCount++;
                }
            }
            if (!SupportsSampledOptimalImage(texture.Format))
                throw new NotSupportedException($"KTX2 texture '{cacheIdentity}' uses format {texture.Format}, which is not supported as a sampled optimal-tiled image on this device.");

            TextureHandle handle = CreateTexture(
                texture.Width,
                texture.Height,
                texture.Format,
                texture.MipLevels,
                samplerDescription: samplerDescription,
                requireWithinMemoryBudget: requireWithinMemoryBudget,
                debugName: CreateSampledTextureDebugName(source, source.FilePath));

            try
            {
                UploadTextureDataMipLevels(handle, texture.Bytes.Span, texture.Levels);

                string? fullPath = string.IsNullOrWhiteSpace(source.FilePath) ? null : Path.GetFullPath(source.FilePath);
                TextureHandle resolvedHandle = RegisterLoadedTexture(
                    handle,
                    cacheKey,
                    imageCacheKey,
                    samplerDescription,
                    fullPath ?? source.DebugName,
                    cacheIdentity,
                    ResolveSourceKind(source, fullPath),
                    authenticatedCookedTexture?.Metadata.EncodedBytes is long encodedBytes
                        ? checked((int)encodedBytes)
                        : source.EncodedByteLength > 0
                            ? source.EncodedByteLength
                            : imageBytes.Length,
                    authenticatedCookedTexture?.Metadata.OriginalWidth is int originalWidth
                        ? checked((uint)originalWidth)
                        : texture.Width,
                    authenticatedCookedTexture?.Metadata.OriginalHeight is int originalHeight
                        ? checked((uint)originalHeight)
                        : texture.Height,
                    wasDownscaled: false,
                    isCompressed: IsBlockCompressedFormat(texture.Format),
                    linearAverageColor: transportStatistics.TryGetLinearMean(out CoreVector4 average)
                        ? average
                        : null,
                    transportStatistics,
                    sourceContentHash,
                    semantic,
                    srgb,
                    generateMipmaps: texture.MipLevels > 1,
                    mipPolicy,
                    CreateLoadedTextureDebugName(fullPath, source.DebugName, texture.Format));
                if (resolvedHandle != handle)
                {
                    DestroyTexture(handle);
                    return resolvedHandle;
                }
            }
            catch
            {
                DestroyTexture(handle);
                throw;
            }

            return handle;
        }

        private TextureHandle RegisterLoadedTexture(
            TextureHandle createdHandle,
            string descriptorCacheKey,
            string imageCacheKey,
            TextureSamplerDescription samplerDescription,
            string? sourcePath,
            string sourceIdentity,
            TextureSourceKind sourceKind,
            int sourceEncodedByteLength,
            uint originalWidth,
            uint originalHeight,
            bool wasDownscaled,
            bool isCompressed,
            CoreVector4? linearAverageColor,
            TextureTransportStatistics? transportStatistics,
            ulong sourceContentHash,
            TextureSemantic semantic,
            bool srgb,
            bool generateMipmaps,
            RuntimeTextureMipPolicy mipPolicy,
            string imageDebugName)
        {
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                // A concurrent request for the exact descriptor owns another logical reference to
                // the existing bindless descriptor, not a new physical image.
                if (_textureCache.TryGetValue(descriptorCacheKey, out TextureHandle cachedHandle))
                {
                    TextureInfo cachedTexture =
                        GetTextureInfoLocked(cachedHandle);
                    RetainLogicalTextureReference(
                        ref cachedTexture.ReferenceCount);
                    return cachedHandle;
                }

                TextureInfo createdTexture = GetTextureInfoLocked(createdHandle);
                SharedTextureImage createdImage = createdTexture.SharedImage
                    ?? throw new InvalidOperationException("A newly-created texture must own a shared image resource.");

                // Another sampler state may have finished loading the same source while this
                // request decoded and uploaded. Reuse its image and retain only this descriptor.
                if (_textureImageCache.TryGetValue(imageCacheKey, out SharedTextureImage? cachedImage))
                    return CreateSharedTextureAliasLocked(cachedImage, samplerDescription, descriptorCacheKey);

                _textureImageCache.EnsureCapacity(
                    checked(_textureImageCache.Count + 1));
                _textureCache.EnsureCapacity(
                    checked(_textureCache.Count + 1));
                if (wasDownscaled && _downscaledTextureCount == int.MaxValue)
                {
                    throw new OverflowException(
                        "Downscaled texture accounting reached its supported limit.");
                }

                // Debug naming can cross the Vulkan validation boundary. Do it
                // before either cache publishes the new physical image.
                _context.SetDebugName(
                    createdImage.Image.Handle,
                    ObjectType.Image,
                    imageDebugName);
                _context.SetDebugName(
                    createdImage.View.Handle,
                    ObjectType.ImageView,
                    $"{imageDebugName} View");

                createdImage.CacheKey = imageCacheKey;
                createdImage.SourcePath = sourcePath;
                createdImage.SourceIdentity = sourceIdentity;
                createdImage.SourceKind = sourceKind;
                createdImage.SourceEncodedByteLength = sourceEncodedByteLength;
                createdImage.OriginalWidth = originalWidth;
                createdImage.OriginalHeight = originalHeight;
                createdImage.WasDownscaled = wasDownscaled;
                createdImage.IsCompressed = isCompressed;
                createdImage.LinearAverageColor = linearAverageColor;
                createdImage.TransportStatistics = transportStatistics;
                createdImage.SourceContentHash = sourceContentHash;
                createdImage.Semantic = semantic;
                createdImage.Srgb = srgb;
                createdImage.GenerateMipmaps = generateMipmaps;
                createdImage.MipPolicy = mipPolicy;
                createdTexture.DescriptorCacheKey = descriptorCacheKey;
                createdTexture.SamplerDescription = samplerDescription;

                CopySourceMetadata(createdImage, createdTexture);
                bool imageCachePublished = false;
                bool descriptorCachePublished = false;
                bool downscaledPublished = false;
                try
                {
                    _textureImageCache.Add(imageCacheKey, createdImage);
                    imageCachePublished = true;
                    PublicationCheckpointForTesting?.Invoke(
                        TexturePublicationCheckpoint.ImageCachePublished);
                    _textureCache.Add(descriptorCacheKey, createdHandle);
                    descriptorCachePublished = true;
                    PublicationCheckpointForTesting?.Invoke(
                        TexturePublicationCheckpoint.DescriptorCachePublished);
                    if (wasDownscaled)
                    {
                        _downscaledTextureCount++;
                        downscaledPublished = true;
                    }
                }
                catch
                {
                    if (downscaledPublished)
                        _downscaledTextureCount--;
                    if (descriptorCachePublished &&
                        _textureCache.TryGetValue(
                            descriptorCacheKey,
                            out TextureHandle descriptorMapping) &&
                        descriptorMapping == createdHandle)
                    {
                        _textureCache.Remove(descriptorCacheKey);
                    }
                    if (imageCachePublished &&
                        _textureImageCache.TryGetValue(
                            imageCacheKey,
                            out SharedTextureImage? imageMapping) &&
                        ReferenceEquals(imageMapping, createdImage))
                    {
                        _textureImageCache.Remove(imageCacheKey);
                    }
                    createdImage.CacheKey = null;
                    createdTexture.DescriptorCacheKey = null;
                    throw;
                }

                return createdHandle;
            }
        }

        private TextureHandle CreateSharedTextureAliasLocked(
            SharedTextureImage sharedImage,
            TextureSamplerDescription samplerDescription,
            string descriptorCacheKey)
        {
            if (sharedImage.Image.Handle == 0 || sharedImage.View.Handle == 0)
                throw new InvalidOperationException("The cached shared texture image has already been released.");
            if (_textureCache.ContainsKey(descriptorCacheKey))
                throw new InvalidOperationException("A descriptor cache entry already exists for this texture alias.");
            if (sharedImage.ReferenceCount == int.MaxValue)
                throw new OverflowException("Shared texture image reference count overflow.");

            bool reuseSlot = _freeIndices.Count > 0;
            int index = reuseSlot ? _freeIndices.Peek() : _textures.Count;
            uint generation = AllocateGeneration(index);
            _textureCache.EnsureCapacity(checked(_textureCache.Count + 1));
            _pendingTextureCreationRollbacks.EnsureCapacity(
                checked(_pendingTextureCreationRollbacks.Count + 1));
            if (!reuseSlot)
                _textures.EnsureCapacity(checked(_textures.Count + 1));

            var textureInfo = new TextureInfo
            {
                SharedImage = sharedImage,
                Image = sharedImage.Image,
                Allocation = sharedImage.Allocation,
                View = sharedImage.View,
                Format = sharedImage.Format,
                Extent = sharedImage.Extent,
                MipLevels = sharedImage.MipLevels,
                ArrayLayers = sharedImage.ArrayLayers,
                Generation = generation,
                BindlessIndex = UnassignedBindlessIndex,
                BindlessHeap = _bindlessHeap,
                EstimatedByteSize = sharedImage.EstimatedByteSize,
                WasDownscaled = sharedImage.WasDownscaled,
                IsCompressed = sharedImage.IsCompressed,
                DescriptorCacheKey = descriptorCacheKey,
                SamplerDescription = samplerDescription
            };
            CopySourceMetadata(sharedImage, textureInfo);

            int textureBindlessIndex = UnassignedBindlessIndex;
            bool bindlessAllocated = false;
            bool slotReserved = false;
            bool slotPublished = false;
            bool referencePublished = false;
            bool cachePublished = false;
            TextureInfo? previousSlot = reuseSlot ? _textures[index] : null;
            var pendingRollback = new PendingTextureCreationRollback();
            try
            {
                textureBindlessIndex = AllocateOrRegisterBindlessIndex(
                    UnassignedBindlessIndex,
                    sharedImage.View,
                    bindlessHeap: null,
                    samplerDescription);
                bindlessAllocated =
                    textureBindlessIndex >=
                    BindlessIndex.FirstDynamicTextureIndex;
                textureInfo.BindlessIndex = textureBindlessIndex;

                if (reuseSlot)
                {
                    int reservedIndex = _freeIndices.Pop();
                    if (reservedIndex != index)
                    {
                        throw new InvalidOperationException(
                            "Texture free-slot reservation changed while the manager lock was held.");
                    }
                    slotReserved = true;
                    _textures[index] = textureInfo;
                }
                else
                {
                    _textures.Add(textureInfo);
                }
                slotPublished = true;
                PublicationCheckpointForTesting?.Invoke(
                    TexturePublicationCheckpoint.AliasSlotPublished);

                RetainLogicalTextureReference(
                    ref sharedImage.ReferenceCount);
                referencePublished = true;
                PublicationCheckpointForTesting?.Invoke(
                    TexturePublicationCheckpoint.AliasReferencePublished);
                var handle = new TextureHandle(index, textureInfo.Generation);
                _textureCache.Add(descriptorCacheKey, handle);
                cachePublished = true;
                PublicationCheckpointForTesting?.Invoke(
                    TexturePublicationCheckpoint.AliasCachePublished);
                return handle;
            }
            catch (Exception publicationFailure)
            {
                List<Exception>? rollbackFailures = null;
                try
                {
                    if (cachePublished &&
                        _textureCache.TryGetValue(
                            descriptorCacheKey,
                            out TextureHandle mapped) &&
                        mapped == new TextureHandle(index, generation))
                    {
                        _textureCache.Remove(descriptorCacheKey);
                    }
                    if (referencePublished)
                        sharedImage.ReferenceCount--;
                    if (slotPublished)
                    {
                        if (reuseSlot)
                            _textures[index] = previousSlot!;
                        else
                            _textures.RemoveAt(index);
                    }
                    if (slotReserved)
                        _freeIndices.Push(index);
                }
                catch (Exception rollbackFailure)
                {
                    (rollbackFailures ??= []).Add(rollbackFailure);
                }

                if (bindlessAllocated)
                {
                    pendingRollback.Heap = _bindlessHeap;
                    pendingRollback.BindlessIndex =
                        textureBindlessIndex;
                    _pendingTextureCreationRollbacks.Add(
                        pendingRollback);
                    try
                    {
                        ExecuteTextureCreationRollback(
                            pendingRollback);
                    }
                    catch (Exception rollbackFailure)
                    {
                        (rollbackFailures ??= []).Add(rollbackFailure);
                    }
                }

                if (rollbackFailures is { Count: > 0 })
                {
                    throw new AggregateException(
                        "Texture alias publication failed and rollback was incomplete.",
                        [publicationFailure, .. rollbackFailures]);
                }

                throw;
            }
        }

        private static void CopySourceMetadata(SharedTextureImage source, TextureInfo destination)
        {
            destination.SourcePath = source.SourcePath;
            destination.SourceIdentity = source.SourceIdentity;
            destination.SourceKind = source.SourceKind;
            destination.SourceEncodedByteLength = source.SourceEncodedByteLength;
            destination.OriginalWidth = source.OriginalWidth;
            destination.OriginalHeight = source.OriginalHeight;
            destination.WasDownscaled = source.WasDownscaled;
            destination.IsCompressed = source.IsCompressed;
        }

        private static string CreateLoadedTextureDebugName(string? fullPath, string? debugName, Format format)
        {
            string fileName = !string.IsNullOrWhiteSpace(fullPath)
                ? Path.GetFileName(fullPath)
                : string.IsNullOrWhiteSpace(debugName)
                    ? "Unnamed"
                    : debugName;
            return $"Texture Image '{fileName}' {format}";
        }

        private static bool IsKtx2Source(ModelTextureSource source, string? fullPath)
        {
            if (source.ContainerKind == TextureContainerKind.Ktx2)
                return true;
            if (string.Equals(source.MimeType, "image/ktx2", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrWhiteSpace(fullPath) &&
                   string.Equals(Path.GetExtension(fullPath), ".ktx2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCookedSource(ModelTextureSource source) =>
            source.CacheIdentity.StartsWith("cooked:", StringComparison.Ordinal);

        private static AuthenticatedCookedTexture AuthenticateCookedTexture(
            ModelTextureSource source,
            string? fullPath,
            ReadOnlySpan<byte> imageBytes,
            TextureSamplerDescription samplerDescription,
            bool srgb,
            TextureSemantic semantic,
            RuntimeTextureMipPolicy mipPolicy)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                throw new InvalidDataException(
                    "Cooked KTX2 textures must be loaded from a file so their sibling " +
                    ".njtex metadata can be authenticated.");
            }

            const string cookedPrefix = "cooked:";
            string sourceIdentity = source.CacheIdentity[cookedPrefix.Length..];
            if (string.IsNullOrWhiteSpace(sourceIdentity))
            {
                throw new InvalidDataException(
                    $"Cooked KTX2 texture '{fullPath}' has no authenticated source identity.");
            }

            return CookedTextureAuthentication.Authenticate(
                fullPath,
                imageBytes,
                new CookedTextureRuntimeContract(
                    sourceIdentity,
                    semantic,
                    ResolveExpectedColorSpace(srgb, semantic),
                    samplerDescription,
                    mipPolicy.PreserveAlphaCoverage,
                    mipPolicy.PreserveAlphaCoverage
                        ? mipPolicy.AlphaCutoff
                        : null),
                CookedRuntimePolicy.ReaderFlags);
        }

        private static TextureColorSpace ResolveExpectedColorSpace(
            bool srgb,
            TextureSemantic semantic) =>
            semantic == TextureSemantic.Hdr
                ? TextureColorSpace.HdrLinear
                : srgb
                    ? TextureColorSpace.Srgb
                    : TextureColorSpace.Linear;

        private static string CreateSampledTextureDebugName(ModelTextureSource source, string? fullPath)
        {
            string name = !string.IsNullOrWhiteSpace(source.DebugName)
                ? source.DebugName
                : !string.IsNullOrWhiteSpace(fullPath)
                    ? Path.GetFileName(fullPath)
                    : "Unnamed";

            return $"Sampled Texture '{name}'";
        }

        private static TextureSourceKind ResolveSourceKind(ModelTextureSource source, string? fullPath)
        {
            if (source.SourceKind != TextureSourceKind.Unknown)
                return source.SourceKind;
            if (!string.IsNullOrWhiteSpace(fullPath))
                return TextureSourceKind.ExternalFile;
            return source.IsMemorySource ? TextureSourceKind.EmbeddedMemory : TextureSourceKind.Unknown;
        }

        public TextureHandle LoadOptionalTextureFromFile(
            string? path,
            TextureHandle fallback,
            bool generateMipmaps = true,
            bool srgb = true,
            TextureSemantic semantic = TextureSemantic.Color,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.GetFullPath(path)))
                return fallback;

            try
            {
                return LoadTextureFromFile(
                    path,
                    generateMipmaps,
                    srgb,
                    requireWithinMemoryBudget: true,
                    semantic,
                    mipPolicy);
            }
            catch (VulkanException ex) when (_context.IsMemoryBudgetExceeded(ex.Result))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Optional texture '{path}' fell back because the GPU memory budget is exhausted.");
                return fallback;
            }
        }

        public (ImageView View, Format Format, Extent3D Extent) GetTextureInfo(TextureHandle handle)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                return (textureInfo.View, textureInfo.Format, textureInfo.Extent);
            }
        }

        public bool TryGetLinearAverageColor(TextureHandle handle, out CoreVector4 average)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (!TryGetTextureInfoLocked(handle, out TextureInfo textureInfo) ||
                    textureInfo.SharedImage?.LinearAverageColor is not CoreVector4 value)
                {
                    average = default;
                    return false;
                }

                average = value;
                return true;
            }
        }

        public bool TryGetTextureTransportStatistics(
            TextureHandle handle,
            out TextureTransportStatistics statistics)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (!TryGetTextureInfoLocked(handle, out TextureInfo textureInfo) ||
                    textureInfo.SharedImage?.TransportStatistics is not TextureTransportStatistics value)
                {
                    statistics = null!;
                    return false;
                }

                statistics = value;
                return true;
            }
        }

        public uint GetTextureContentRevision(TextureHandle handle)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                return TryGetTextureInfoLocked(handle, out TextureInfo textureInfo)
                    ? textureInfo.SharedImage?.ContentRevision ?? 0
                    : 0;
            }
        }

        /// <summary>
        /// Publishes replacement source-resolution statistics for a reloaded
        /// texture and invalidates every material referencing any sampler alias
        /// of the same physical image.
        /// </summary>
        public void PublishTextureTransportStatistics(
            TextureHandle handle,
            TextureTransportStatistics statistics)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(statistics);
            IReadOnlyList<string> validation = statistics.Validate();
            if (validation.Count != 0)
            {
                throw new InvalidDataException(
                    $"Texture transport statistics are invalid: {string.Join(" ", validation)}");
            }

            TextureContentChangedEvent[] notifications;
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                SharedTextureImage image = textureInfo.SharedImage
                    ?? throw new InvalidOperationException("Texture does not own a sampled image.");
                image.TransportStatistics = statistics;
                image.LinearAverageColor = statistics.TryGetLinearMean(out CoreVector4 mean)
                    ? mean
                    : null;
                image.ContentRevision = NextContentRevision(image.ContentRevision);

                var changed = new List<TextureContentChangedEvent>();
                for (int index = 0; index < _textures.Count; index++)
                {
                    TextureInfo candidate = _textures[index];
                    if (!IsLiveTexture(candidate) || !ReferenceEquals(candidate.SharedImage, image))
                        continue;
                    changed.Add(new TextureContentChangedEvent(
                        new TextureHandle(index, candidate.Generation),
                        image.ContentRevision,
                        statistics.SourceContentHash));
                }
                notifications = changed.ToArray();
            }

            DispatchTextureContentNotifications(notifications);
        }

        /// <summary>
        /// Retries only subscriber deliveries that failed after a texture
        /// publication. Successfully delivered alias/subscriber pairs are
        /// removed immediately and are never repeated by a later retry.
        /// </summary>
        public int RetryPendingTextureContentNotifications()
        {
            ThrowIfDisposed();
            return _contentNotificationDispatcher.RetryPending();
        }

        private void DispatchTextureContentNotifications(
            IReadOnlyList<TextureContentChangedEvent> notifications)
        {
            _contentNotificationDispatcher.Dispatch(
                notifications,
                TextureContentChanged);
        }

        /// <summary>
        /// Replaces decoded uncooked image pixels and their source-resolution
        /// transport statistics as one publication. All sampler aliases keep
        /// their handles and bindless indices. This must be called on the
        /// renderer thread; the intentional idle boundary makes descriptor
        /// replacement safe for in-flight frames.
        /// </summary>
        public TextureContentReloadResult ReloadTextureContent(
            TextureHandle handle,
            ModelTextureSource source,
            bool generateMipmaps = true,
            bool srgb = true,
            bool requireWithinMemoryBudget = false,
            TextureSemantic semantic = TextureSemantic.Color,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(source);
            if (!handle.IsValid)
                throw new ArgumentException("A valid texture handle is required.", nameof(handle));
            if (handle == _defaultWhiteTexture ||
                handle == _defaultNormalTexture ||
                handle == _defaultBlackTexture)
            {
                throw new InvalidOperationException("Built-in textures cannot be hot reloaded.");
            }

            int retriedNotificationCount =
                RetryPendingTextureContentNotifications();
            RuntimeTextureMipPolicy normalizedMipPolicy = mipPolicy.ValidateAndNormalize();
            byte[] imageBytes = ReadTextureSourceBytes(source, out string? fullPath);
            string sourceIdentity = ResolveSourceIdentity(source, fullPath);
            if (IsGitLfsPointer(imageBytes))
            {
                throw new InvalidOperationException(
                    $"Texture source '{sourceIdentity}' is a Git LFS pointer file, not image data.");
            }
            if (IsKtx2Source(source, fullPath))
                return ReloadKtx2TextureContent(
                    handle,
                    source,
                    imageBytes,
                    fullPath,
                    sourceIdentity,
                    requireWithinMemoryBudget,
                    srgb,
                    semantic,
                    normalizedMipPolicy,
                    retriedNotificationCount);

            ulong sourceContentHash = CalculateTextureSourceContentHash(imageBytes);
            uint maxTextureDimension = MaxLoadedTextureDimension;
            TextureContainerKind containerKind =
                WebPTextureDecoder.IsDeclaredWebP(source, imageBytes)
                    ? TextureContainerKind.WebP
                    : TextureContainerKind.StandardImage;
            string imageCacheKey = CreateTextureImageCacheKey(
                sourceIdentity,
                generateMipmaps,
                srgb,
                maxTextureDimension,
                containerKind,
                sourceContentHash,
                semantic,
                normalizedMipPolicy);
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                SharedTextureImage current = GetTextureInfoLocked(handle).SharedImage
                    ?? throw new InvalidOperationException("Texture does not own a sampled image.");
                if (current.SourceContentHash == sourceContentHash &&
                    current.Semantic == semantic &&
                    current.Srgb == srgb &&
                    current.GenerateMipmaps == generateMipmaps &&
                    current.MipPolicy == normalizedMipPolicy &&
                    string.Equals(
                        current.CacheKey,
                        imageCacheKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new TextureContentReloadResult(
                        Changed: false,
                        current.ContentRevision,
                        current.SourceContentHash,
                        NotifiedAliasCount: retriedNotificationCount);
                }
            }

            DecodedStandardTexture decoded =
                DecodeStandardTexture(source, imageBytes, sourceIdentity);

            TextureTransportStatistics transportStatistics = TextureTransportImage.FromRgba8(
                decoded.Data,
                decoded.Width,
                decoded.Height,
                srgb ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
                TextureSemantic.Normal,
                sourceContentHash,
                decoded.Decoder).Statistics with
            {
                Semantic = semantic
            };
            CoreVector4 linearAverageColor = transportStatistics.LinearChannelMean.ToVector4();
            uint originalWidth = checked((uint)decoded.Width);
            uint originalHeight = checked((uint)decoded.Height);
            uint width = originalWidth;
            uint height = originalHeight;
            byte[] textureData = decoded.Data;
            bool wasDownscaled = false;
            double sourceAlphaCoverage = normalizedMipPolicy.PreserveAlphaCoverage
                ? AlphaCoverageMipGenerator.CalculateCoverage(
                    textureData,
                    normalizedMipPolicy.AlphaCutoff)
                : 0.0;
            if (TryDownscaleRgba(
                    textureData,
                    width,
                    height,
                    maxTextureDimension,
                    out byte[]? downscaledData,
                    out uint downscaledWidth,
                    out uint downscaledHeight))
            {
                textureData = downscaledData
                    ?? throw new InvalidOperationException("Texture downscale reported success without output data.");
                width = downscaledWidth;
                height = downscaledHeight;
                wasDownscaled = true;
                if (normalizedMipPolicy.PreserveAlphaCoverage)
                {
                    AlphaCoverageMipGenerator.PreserveCoverage(
                        textureData,
                        normalizedMipPolicy.AlphaCutoff,
                        sourceAlphaCoverage);
                }
            }

            Format format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
            RuntimeRgbaMipChain? runtimeMipChain =
                generateMipmaps && normalizedMipPolicy.PreserveAlphaCoverage
                    ? BuildRuntimeRgbaMipChain(
                        textureData,
                        width,
                        height,
                        srgb,
                        normalizedMipPolicy,
                        sourceAlphaCoverage)
                    : null;
            bool canGenerateMipmaps = generateMipmaps &&
                                      (runtimeMipChain != null || SupportsLinearBlit(format));
            uint mipLevels = canGenerateMipmaps ? CalculateMipLevels(width, height) : 1u;
            if (generateMipmaps && !canGenerateMipmaps)
            {
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    _mipmapFallbackCount++;
                }
            }

            TextureHandle replacement = CreateTexture(
                width,
                height,
                format,
                mipLevels,
                samplerDescription: TextureSamplerDescription.Default,
                requireWithinMemoryBudget: requireWithinMemoryBudget,
                debugName: CreateSampledTextureDebugName(source, fullPath));
            bool replacementOwned = true;
            try
            {
                if (runtimeMipChain != null)
                {
                    UploadTextureDataAllMipsAndLayers(
                        replacement,
                        runtimeMipChain.ContiguousPixels,
                        width,
                        height,
                        format);
                }
                else
                {
                    UploadTextureData(
                        replacement,
                        textureData,
                        width,
                        height,
                        format,
                        generateMipmaps: mipLevels > 1);
                }

                // Updating a descriptor that may be used by a pending command
                // buffer is not legal without UPDATE_UNUSED_WHILE_PENDING.
                // Hot reload is infrequent, so prefer an explicit safe point.
                _context.WaitIdle();

                TextureContentChangedEvent[] notifications;
                uint contentRevision;
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    notifications = PublishReloadedTextureLocked(
                        handle,
                        replacement,
                        imageCacheKey,
                        fullPath,
                        sourceIdentity,
                        ResolveSourceKind(source, fullPath),
                        source.EncodedByteLength > 0
                            ? source.EncodedByteLength
                            : imageBytes.Length,
                        originalWidth,
                        originalHeight,
                        wasDownscaled,
                        linearAverageColor,
                        transportStatistics,
                        sourceContentHash,
                        isCompressed: false,
                        semantic,
                        srgb,
                        generateMipmaps,
                        normalizedMipPolicy,
                        CreateLoadedTextureDebugName(fullPath, source.DebugName, format));
                    contentRevision = notifications.Length > 0
                        ? notifications[0].ContentRevision
                        : GetTextureContentRevision(handle);
                    _runtimeDecodedTextureCount++;
                    if (runtimeMipChain != null)
                        _runtimeAlphaCoverageMipTextureCount++;
                }

                // The temporary logical handle now owns the retired physical
                // image after the resource swap.
                DestroyTexture(replacement);
                replacementOwned = false;

                DispatchTextureContentNotifications(notifications);

                return new TextureContentReloadResult(
                    Changed: true,
                    contentRevision,
                    sourceContentHash,
                    checked(notifications.Length + retriedNotificationCount));
            }
            catch
            {
                if (replacementOwned)
                    DestroyTexture(replacement);
                throw;
            }
        }

        private TextureContentReloadResult ReloadKtx2TextureContent(
            TextureHandle handle,
            ModelTextureSource source,
            byte[] imageBytes,
            string? fullPath,
            string sourceIdentity,
            bool requireWithinMemoryBudget,
            bool srgb,
            TextureSemantic semantic,
            RuntimeTextureMipPolicy mipPolicy,
            int retriedNotificationCount)
        {
            if (!IsCookedSource(source))
            {
                throw new NotSupportedException(
                    "Atomic KTX2 hot reload requires authenticated cooked content and a " +
                    "sibling .njtex metadata file.");
            }

            TextureSamplerDescription samplerDescription;
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                samplerDescription =
                    GetTextureInfoLocked(handle).SamplerDescription ??
                    TextureSamplerDescription.Default;
            }

            AuthenticatedCookedTexture authenticated = AuthenticateCookedTexture(
                source,
                fullPath,
                imageBytes,
                samplerDescription,
                srgb,
                semantic,
                mipPolicy);
            Ktx2Texture texture = Ktx2Texture.Parse(imageBytes, sourceIdentity);
            if (!SupportsSampledOptimalImage(texture.Format))
            {
                throw new NotSupportedException(
                    $"KTX2 texture '{sourceIdentity}' uses format {texture.Format}, which is " +
                    "not supported as a sampled optimal-tiled image on this device.");
            }

            ulong sourceContentHash = authenticated.Ktx2ContentHash;
            string imageCacheKey = CreateTextureImageCacheKey(
                sourceIdentity,
                texture.MipLevels > 1,
                srgb,
                MaxLoadedTextureDimension,
                TextureContainerKind.Ktx2,
                authenticated.PublicationContentHash,
                semantic,
                mipPolicy);
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                SharedTextureImage current = GetTextureInfoLocked(handle).SharedImage
                    ?? throw new InvalidOperationException("Texture does not own a sampled image.");
                if (current.SourceContentHash == sourceContentHash &&
                    current.Semantic == semantic &&
                    current.Srgb == srgb &&
                    current.GenerateMipmaps == (texture.MipLevels > 1) &&
                    current.MipPolicy == mipPolicy &&
                    string.Equals(
                        current.CacheKey,
                        imageCacheKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new TextureContentReloadResult(
                        Changed: false,
                        current.ContentRevision,
                        current.SourceContentHash,
                        NotifiedAliasCount: retriedNotificationCount);
                }
            }

            TextureHandle replacement = CreateTexture(
                texture.Width,
                texture.Height,
                texture.Format,
                texture.MipLevels,
                samplerDescription: TextureSamplerDescription.Default,
                requireWithinMemoryBudget: requireWithinMemoryBudget,
                debugName: CreateSampledTextureDebugName(source, fullPath));
            bool replacementOwned = true;
            try
            {
                UploadTextureDataMipLevels(
                    replacement,
                    texture.Bytes.Span,
                    texture.Levels);

                // Descriptor updates and physical ownership publication happen
                // only after every upload has completed at an explicit idle
                // boundary. The old image remains authoritative on any failure
                // before publication.
                _context.WaitIdle();

                TextureContentChangedEvent[] notifications;
                uint contentRevision;
                TextureTransportStatistics statistics =
                    authenticated.Metadata.TransportStatistics;
                lock (_lock)
                {
                    _lifecycle.ThrowIfDisposedUnderGate(_lock);
                    notifications = PublishReloadedTextureLocked(
                        handle,
                        replacement,
                        imageCacheKey,
                        fullPath,
                        sourceIdentity,
                        ResolveSourceKind(source, fullPath),
                        checked((int)authenticated.Metadata.EncodedBytes),
                        checked((uint)authenticated.Metadata.OriginalWidth),
                        checked((uint)authenticated.Metadata.OriginalHeight),
                        wasDownscaled: false,
                        statistics.TryGetLinearMean(out CoreVector4 average)
                            ? average
                            : null,
                        statistics,
                        sourceContentHash,
                        isCompressed: IsBlockCompressedFormat(texture.Format),
                        semantic,
                        srgb,
                        generateMipmaps: texture.MipLevels > 1,
                        mipPolicy,
                        CreateLoadedTextureDebugName(
                            fullPath,
                            source.DebugName,
                            texture.Format));
                    contentRevision = notifications.Length > 0
                        ? notifications[0].ContentRevision
                        : GetTextureContentRevision(handle);
                    _cookedTextureLoadCount++;
                }

                DestroyTexture(replacement);
                replacementOwned = false;

                DispatchTextureContentNotifications(notifications);

                return new TextureContentReloadResult(
                    Changed: true,
                    contentRevision,
                    sourceContentHash,
                    checked(notifications.Length + retriedNotificationCount));
            }
            catch
            {
                if (replacementOwned)
                    DestroyTexture(replacement);
                throw;
            }
        }

        private TextureContentChangedEvent[] PublishReloadedTextureLocked(
            TextureHandle handle,
            TextureHandle replacementHandle,
            string imageCacheKey,
            string? sourcePath,
            string sourceIdentity,
            TextureSourceKind sourceKind,
            int sourceEncodedByteLength,
            uint originalWidth,
            uint originalHeight,
            bool wasDownscaled,
            CoreVector4? linearAverageColor,
            TextureTransportStatistics transportStatistics,
            ulong sourceContentHash,
            bool isCompressed,
            TextureSemantic semantic,
            bool srgb,
            bool generateMipmaps,
            RuntimeTextureMipPolicy mipPolicy,
            string imageDebugName)
        {
            TextureInfo targetInfo = GetTextureInfoLocked(handle);
            TextureInfo replacementInfo = GetTextureInfoLocked(replacementHandle);
            SharedTextureImage target = targetInfo.SharedImage
                ?? throw new InvalidOperationException("Reload target has no sampled image.");
            SharedTextureImage replacement = replacementInfo.SharedImage
                ?? throw new InvalidOperationException("Reload replacement has no sampled image.");
            if (ReferenceEquals(target, replacement))
                throw new InvalidOperationException("Reload replacement must own a distinct physical image.");

            var aliasIndices = new List<int>();
            for (int index = 0; index < _textures.Count; index++)
            {
                TextureInfo candidate = _textures[index];
                if (IsLiveTexture(candidate) && ReferenceEquals(candidate.SharedImage, target))
                    aliasIndices.Add(index);
            }
            if (aliasIndices.Count == 0)
                throw new InvalidOperationException("Reload target has no live descriptor aliases.");

            // Allocate and validate every fallible publication input before a
            // descriptor or authoritative CPU record is changed.
            _textureCache.EnsureCapacity(checked(_textureCache.Count + aliasIndices.Count));
            _textureImageCache.EnsureCapacity(checked(_textureImageCache.Count + 1));
            var aliasSamplers = new Sampler[aliasIndices.Count];
            var descriptorKeys = new string[aliasIndices.Count];
            var descriptorCacheInsertions = new bool[aliasIndices.Count];
            var notifications = new TextureContentChangedEvent[aliasIndices.Count];
            var uniqueDescriptorKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            uint nextRevision = NextContentRevision(target.ContentRevision);
            for (int aliasPosition = 0; aliasPosition < aliasIndices.Count; aliasPosition++)
            {
                int aliasIndex = aliasIndices[aliasPosition];
                TextureInfo alias = _textures[aliasIndex];
                TextureSamplerDescription effectiveSampler =
                    alias.SamplerDescription ?? TextureSamplerDescription.Default;
                aliasSamplers[aliasPosition] =
                    alias.BindlessIndex != UnassignedBindlessIndex && _bindlessHeap != null
                        ? GetOrCreateSamplerLocked(effectiveSampler)
                        : default;
                string descriptorKey = CreateTextureDescriptorCacheKey(
                    imageCacheKey,
                    effectiveSampler);
                if (!uniqueDescriptorKeys.Add(descriptorKey))
                {
                    throw new InvalidOperationException(
                        "Texture reload target contains duplicate live aliases for the same sampler.");
                }
                descriptorKeys[aliasPosition] = descriptorKey;
                descriptorCacheInsertions[aliasPosition] =
                    !_textureCache.TryGetValue(descriptorKey, out TextureHandle mapped) ||
                    mapped == new TextureHandle(aliasIndex, alias.Generation);
                notifications[aliasPosition] = new TextureContentChangedEvent(
                    new TextureHandle(aliasIndex, alias.Generation),
                    nextRevision,
                    sourceContentHash);
            }

            _context.SetDebugName(
                replacement.Image.Handle,
                ObjectType.Image,
                imageDebugName);
            _context.SetDebugName(
                replacement.View.Handle,
                ObjectType.ImageView,
                $"{imageDebugName} View");

            // Updating a descriptor is the only externally-visible operation
            // in this locked publication that can partially succeed. Restore
            // every descriptor already updated if a later registration fails;
            // the old physical image and every CPU record are still untouched.
            int updatedDescriptorCount = 0;
            try
            {
                if (_bindlessHeap != null)
                {
                    for (int aliasPosition = 0; aliasPosition < aliasIndices.Count; aliasPosition++)
                    {
                        TextureInfo alias = _textures[aliasIndices[aliasPosition]];
                        if (alias.BindlessIndex == UnassignedBindlessIndex)
                            continue;
                        _bindlessHeap.RegisterTexture(
                            alias.BindlessIndex,
                            replacement.View,
                            aliasSamplers[aliasPosition]);
                        updatedDescriptorCount = aliasPosition + 1;
                    }
                }
            }
            catch (Exception publicationFailure)
            {
                Exception? rollbackFailure = null;
                if (_bindlessHeap != null)
                {
                    for (int aliasPosition = updatedDescriptorCount - 1;
                         aliasPosition >= 0;
                         aliasPosition--)
                    {
                        TextureInfo alias = _textures[aliasIndices[aliasPosition]];
                        if (alias.BindlessIndex == UnassignedBindlessIndex)
                            continue;
                        try
                        {
                            _bindlessHeap.RegisterTexture(
                                alias.BindlessIndex,
                                target.View,
                                aliasSamplers[aliasPosition]);
                        }
                        catch (Exception exception)
                        {
                            rollbackFailure = rollbackFailure == null
                                ? exception
                                : new AggregateException(rollbackFailure, exception);
                        }
                    }
                }

                if (rollbackFailure != null)
                {
                    throw new AggregateException(
                        "Texture reload descriptor publication failed and descriptor rollback was incomplete.",
                        publicationFailure,
                        rollbackFailure);
                }

                throw;
            }

            foreach (int aliasIndex in aliasIndices)
            {
                TextureInfo alias = _textures[aliasIndex];
                if (!string.IsNullOrWhiteSpace(alias.DescriptorCacheKey) &&
                    _textureCache.TryGetValue(alias.DescriptorCacheKey, out TextureHandle mapped) &&
                    mapped == new TextureHandle(aliasIndex, alias.Generation))
                {
                    _textureCache.Remove(alias.DescriptorCacheKey);
                }
                alias.DescriptorCacheKey = null;
            }
            if (!string.IsNullOrWhiteSpace(target.CacheKey) &&
                _textureImageCache.TryGetValue(target.CacheKey, out SharedTextureImage? cachedTarget) &&
                ReferenceEquals(cachedTarget, target))
            {
                _textureImageCache.Remove(target.CacheKey);
            }

            // Swap physical resources. The temporary handle takes ownership of
            // the retired image so normal destruction/accounting remains the
            // single resource-release path.
            (target.Image, replacement.Image) = (replacement.Image, target.Image);
            Allocation* retiredAllocation = target.Allocation;
            target.Allocation = replacement.Allocation;
            replacement.Allocation = retiredAllocation;
            (target.View, replacement.View) = (replacement.View, target.View);
            (target.Format, replacement.Format) = (replacement.Format, target.Format);
            (target.Extent, replacement.Extent) = (replacement.Extent, target.Extent);
            (target.MipLevels, replacement.MipLevels) = (replacement.MipLevels, target.MipLevels);
            (target.ArrayLayers, replacement.ArrayLayers) = (replacement.ArrayLayers, target.ArrayLayers);
            (target.EstimatedByteSize, replacement.EstimatedByteSize) =
                (replacement.EstimatedByteSize, target.EstimatedByteSize);
            (target.WasDownscaled, replacement.WasDownscaled) =
                (wasDownscaled, target.WasDownscaled);

            target.CacheKey = null;
            target.SourcePath = sourcePath;
            target.SourceIdentity = sourceIdentity;
            target.SourceKind = sourceKind;
            target.SourceEncodedByteLength = sourceEncodedByteLength;
            target.OriginalWidth = originalWidth;
            target.OriginalHeight = originalHeight;
            target.IsCompressed = isCompressed;
            target.LinearAverageColor = linearAverageColor;
            target.TransportStatistics = transportStatistics;
            target.SourceContentHash = sourceContentHash;
            target.Semantic = semantic;
            target.Srgb = srgb;
            target.GenerateMipmaps = generateMipmaps;
            target.MipPolicy = mipPolicy;
            target.ContentRevision = nextRevision;

            replacement.CacheKey = null;
            replacement.ReferenceCount = 1;
            bool imageKeyAvailable =
                !_textureImageCache.TryGetValue(imageCacheKey, out SharedTextureImage? existingImage) ||
                ReferenceEquals(existingImage, target);
            if (imageKeyAvailable)
            {
                target.CacheKey = imageCacheKey;
                _textureImageCache[imageCacheKey] = target;
            }

            for (int aliasPosition = 0; aliasPosition < aliasIndices.Count; aliasPosition++)
            {
                int aliasIndex = aliasIndices[aliasPosition];
                TextureInfo alias = _textures[aliasIndex];
                alias.Image = target.Image;
                alias.Allocation = target.Allocation;
                alias.View = target.View;
                alias.Format = target.Format;
                alias.Extent = target.Extent;
                alias.MipLevels = target.MipLevels;
                alias.ArrayLayers = target.ArrayLayers;
                alias.EstimatedByteSize = target.EstimatedByteSize;
                alias.WasDownscaled = target.WasDownscaled;
                alias.IsCompressed = target.IsCompressed;
                CopySourceMetadata(target, alias);

                var aliasHandle = new TextureHandle(aliasIndex, alias.Generation);
                if (descriptorCacheInsertions[aliasPosition])
                {
                    _textureCache[descriptorKeys[aliasPosition]] = aliasHandle;
                    alias.DescriptorCacheKey = descriptorKeys[aliasPosition];
                }
            }

            replacementInfo.Image = replacement.Image;
            replacementInfo.Allocation = replacement.Allocation;
            replacementInfo.View = replacement.View;
            replacementInfo.Format = replacement.Format;
            replacementInfo.Extent = replacement.Extent;
            replacementInfo.MipLevels = replacement.MipLevels;
            replacementInfo.ArrayLayers = replacement.ArrayLayers;
            replacementInfo.EstimatedByteSize = replacement.EstimatedByteSize;
            replacementInfo.WasDownscaled = replacement.WasDownscaled;
            replacementInfo.IsCompressed = replacement.IsCompressed;
            CopySourceMetadata(replacement, replacementInfo);

            if (target.WasDownscaled)
                _downscaledTextureCount++;

            _resourceGeneration++;
            if (_resourceGeneration == 0)
                _resourceGeneration = 1;

            return notifications;
        }

        public ImageView GetTextureView(TextureHandle handle)
        {
            ThrowIfDisposed();
            return GetTextureInfo(handle).View;
        }

        /// <summary>
        /// Creates a non-owning view over a validated texture subresource. The
        /// caller must destroy the view before destroying the texture and only
        /// after work referencing it has completed.
        /// </summary>
        public ImageView CreateTextureSubresourceView(
            TextureHandle handle,
            uint baseMipLevel,
            uint levelCount,
            uint baseArrayLayer,
            uint layerCount,
            ImageViewType viewType)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo info = GetTextureInfoLocked(handle);
                if (levelCount == 0 ||
                    baseMipLevel >= info.MipLevels ||
                    levelCount > info.MipLevels - baseMipLevel)
                {
                    throw new ArgumentOutOfRangeException(nameof(levelCount));
                }
                if (layerCount == 0 ||
                    baseArrayLayer >= info.ArrayLayers ||
                    layerCount > info.ArrayLayers - baseArrayLayer)
                {
                    throw new ArgumentOutOfRangeException(nameof(layerCount));
                }

                return CreateImageView(
                    info.Image,
                    info.Format,
                    ImageAspectFlags.ColorBit,
                    levelCount,
                    layerCount,
                    viewType,
                    baseMipLevel,
                    baseArrayLayer);
            }
        }

        public void DestroyTextureSubresourceView(ImageView view)
        {
            ThrowIfDisposed();
            if (view.Handle == 0)
                return;
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                _context.Api.DestroyImageView(_context.Device, view, null);
            }
        }

        public int GetBindlessTextureIndex(TextureHandle handle)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                return GetTextureInfoLocked(handle).BindlessIndex;
            }
        }

        public void RetainTexture(TextureHandle handle)
        {
            ThrowIfDisposed();
            if (!handle.IsValid ||
                handle == _defaultWhiteTexture ||
                handle == _defaultNormalTexture ||
                handle == _defaultBlackTexture)
            {
                return;
            }

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                RetainLogicalTextureReference(
                    ref textureInfo.ReferenceCount);
            }
        }

        public void UploadTextureData(
            TextureHandle handle,
            ReadOnlySpan<byte> data,
            uint width,
            uint height,
            Format format,
            bool generateMipmaps = false)
        {
            ThrowIfDisposed();
            if (data.IsEmpty)
                throw new ArgumentException("Texture upload data cannot be empty.", nameof(data));

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                if (textureInfo.Extent.Width != width || textureInfo.Extent.Height != height)
                    throw new InvalidOperationException("Texture upload dimensions do not match the destination image.");
                if (textureInfo.Format != format)
                    throw new InvalidOperationException("Texture upload format does not match the destination image.");

                ulong requiredSize = CalculateRequiredStagingSize(width, height, format);
                if ((ulong)data.Length < requiredSize)
                    throw new ArgumentException("Texture upload data is smaller than the required image size.", nameof(data));

                BufferHandle stagingHandle = _bufferManager.CreateStagingBuffer(requiredSize);
                try
                {
                    void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
                    fixed (byte* source = data)
                    {
                        Buffer.MemoryCopy(source, mappedData, requiredSize, requiredSize);
                    }

                    _bufferManager.FlushBuffer(stagingHandle, 0, requiredSize);

                    var upload = _context.BeginSingleTimeCommands();
                    RecordTextureUpload(
                        upload.CommandBuffer,
                        _bufferManager.GetBuffer(stagingHandle),
                        textureInfo,
                        width,
                        height,
                        generateMipmaps && textureInfo.MipLevels > 1);
                    _context.EndSingleTimeCommands(upload);
                }
                finally
                {
                    _bufferManager.DestroyBuffer(stagingHandle);
                }
            }
        }

        public void UploadTextureDataAllMipsAndLayers(
            TextureHandle handle,
            ReadOnlySpan<byte> data,
            uint width,
            uint height,
            Format format)
        {
            ThrowIfDisposed();
            if (data.IsEmpty)
                throw new ArgumentException("Texture upload data cannot be empty.", nameof(data));

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                if (textureInfo.Extent.Width != width || textureInfo.Extent.Height != height)
                    throw new InvalidOperationException("Texture upload dimensions do not match the destination image.");
                if (textureInfo.Format != format)
                    throw new InvalidOperationException("Texture upload format does not match the destination image.");

                ulong requiredSize = CalculateRequiredStagingSizeAllMipsAndLayers(width, height, format, textureInfo.MipLevels, textureInfo.ArrayLayers);
                if ((ulong)data.Length < requiredSize)
                    throw new ArgumentException("Texture upload data is smaller than the required image size.", nameof(data));

                BufferHandle stagingHandle = _bufferManager.CreateStagingBuffer(requiredSize);
                try
                {
                    void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
                    fixed (byte* source = data)
                    {
                        Buffer.MemoryCopy(source, mappedData, requiredSize, requiredSize);
                    }

                    _bufferManager.FlushBuffer(stagingHandle, 0, requiredSize);

                    var upload = _context.BeginSingleTimeCommands();
                    RecordTextureUploadAllMipsAndLayers(
                        upload.CommandBuffer,
                        _bufferManager.GetBuffer(stagingHandle),
                        textureInfo,
                        width,
                        height);
                    _context.EndSingleTimeCommands(upload);
                }
                finally
                {
                    _bufferManager.DestroyBuffer(stagingHandle);
                }
            }
        }

        internal void UploadTextureDataMipLevels(
            TextureHandle handle,
            ReadOnlySpan<byte> data,
            IReadOnlyList<Ktx2MipLevel> levels)
        {
            ThrowIfDisposed();
            if (data.IsEmpty)
                throw new ArgumentException("Texture upload data cannot be empty.", nameof(data));
            if (levels.Count == 0)
                throw new ArgumentException("Texture upload must include at least one mip level.", nameof(levels));

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                if (textureInfo.ArrayLayers != 1)
                    throw new NotSupportedException("KTX2 mip upload currently supports single-layer 2D textures only.");
                if (textureInfo.MipLevels != levels.Count)
                    throw new InvalidOperationException("KTX2 mip count does not match the destination image.");

                for (int i = 0; i < levels.Count; i++)
                {
                    Ktx2MipLevel level = levels[i];
                    ulong expected = CalculateTextureLevelByteSize(level.Width, level.Height, textureInfo.Format);
                    if ((ulong)level.ByteLength < expected)
                        throw new ArgumentException($"KTX2 mip level {i} is smaller than required for {textureInfo.Format}.", nameof(levels));
                    if (level.ByteOffset < 0 || level.ByteLength < 0 || level.ByteOffset + level.ByteLength > data.Length)
                        throw new ArgumentException($"KTX2 mip level {i} points outside the upload data.", nameof(levels));
                }

                ulong requiredSize = checked((ulong)data.Length);
                BufferHandle stagingHandle = _bufferManager.CreateStagingBuffer(requiredSize);
                try
                {
                    void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
                    fixed (byte* source = data)
                    {
                        Buffer.MemoryCopy(source, mappedData, requiredSize, requiredSize);
                    }

                    _bufferManager.FlushBuffer(stagingHandle, 0, requiredSize);

                    var upload = _context.BeginSingleTimeCommands();
                    RecordTextureUploadMipLevels(
                        upload.CommandBuffer,
                        _bufferManager.GetBuffer(stagingHandle),
                        textureInfo,
                        levels);
                    _context.EndSingleTimeCommands(upload);
                }
                finally
                {
                    _bufferManager.DestroyBuffer(stagingHandle);
                }
            }
        }

        public void UploadTextureData(
            TextureHandle handle,
            IntPtr data,
            ulong dataSize,
            uint width,
            uint height,
            Format format)
        {
            ThrowIfDisposed();
            if (data == IntPtr.Zero)
                throw new ArgumentNullException(nameof(data));
            if (dataSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dataSize), "Texture uploads larger than 2 GB require span-based chunking.");

            UploadTextureData(handle, new ReadOnlySpan<byte>((void*)data, checked((int)dataSize)), width, height, format);
        }

        internal void FlushPendingTextureRetirements()
        {
            lock (_disposeGate)
            {
                PendingTextureRetirement[] pending;
                lock (_lock)
                {
                    // The renderer calls this immediately before closing the
                    // fence-deletion queue. Begin the same monotonic lifecycle
                    // transition as Dispose while holding the publication
                    // lock, so no later release can publish retirement work
                    // outside this snapshot.
                    _lifecycle.BeginDisposeUnderGate(_lock);
                    pending = [.. _pendingTextureRetirements.Values];
                }

                List<Exception>? failures = null;
                foreach (PendingTextureRetirement retirement in pending)
                {
                    try
                    {
                        RetireDetachedTexture(retirement);
                    }
                    catch (Exception exception)
                    {
                        (failures ??= new List<Exception>()).Add(exception);
                    }
                }

                if (failures is { Count: 1 })
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(failures[0])
                        .Throw();
                }
                if (failures is { Count: > 1 })
                {
                    throw new AggregateException(
                        "One or more durable texture retirements remain incomplete.",
                        failures);
                }
            }
        }

        private void FlushPendingTextureCreationRollbacks()
        {
            PendingTextureCreationRollback[] pending;
            lock (_lock)
                pending = [.. _pendingTextureCreationRollbacks];

            List<Exception>? failures = null;
            foreach (PendingTextureCreationRollback rollback in pending)
            {
                try
                {
                    ExecuteTextureCreationRollback(rollback);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (failures is { Count: 1 })
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }
            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "One or more failed texture creations still own GPU rollback work.",
                    failures);
            }
        }

        private void ExecuteTextureCreationRollback(
            PendingTextureCreationRollback rollback)
        {
            lock (_lock)
            {
                ExecuteDependentTextureRetirement(
                    rollback.Progress,
                    () =>
                    {
                        if (rollback.BindlessIndex >=
                            BindlessIndex.FirstDynamicTextureIndex)
                        {
                            (rollback.Heap ??
                             throw new InvalidOperationException(
                                 "A dynamic texture rollback lost its bindless heap."))
                                .FreeTextureIndex(rollback.BindlessIndex);
                        }
                    },
                    static () => { },
                    () =>
                    {
                        DestroyTextureImageViewNow(rollback.View);
                        rollback.View = default;
                    },
                    () =>
                    {
                        DestroyTextureImageNow(
                            rollback.Image,
                            rollback.Allocation);
                        rollback.Image = default;
                        rollback.Allocation = null;
                    });

                if (rollback.Progress.IsComplete)
                    _pendingTextureCreationRollbacks.Remove(rollback);
            }
        }

        public void DestroyTexture(TextureHandle handle, Fence retireFence = default)
        {
            ThrowIfDisposed();
            PendingTextureRetirement? retirement;
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (!_pendingTextureRetirements.TryGetValue(
                        handle,
                        out retirement) &&
                    !TryDetachTextureForRetirementLocked(
                        handle,
                        retireFence,
                        releaseLastReference: false,
                        out retirement))
                {
                    return;
                }
            }

            RetireDetachedTexture(
                retirement ??
                throw new InvalidOperationException(
                    "Detached texture retirement work was not published."));
        }

        public void ReleaseTexture(TextureHandle handle, Fence retireFence = default)
        {
            ThrowIfDisposed();
            if (!handle.IsValid)
                return;

            if (handle == _defaultWhiteTexture ||
                handle == _defaultNormalTexture ||
                handle == _defaultBlackTexture)
            {
                return;
            }

            PendingTextureRetirement? retirement;
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (_pendingTextureRetirements.TryGetValue(
                        handle,
                        out retirement))
                {
                    // A previous call already consumed the logical reference.
                    // Resume its durable physical retirement without
                    // decrementing anything a second time.
                }
                else
                {
                    if (!TryGetTextureInfoLocked(handle, out TextureInfo textureInfo))
                        return;

                    if (textureInfo.ReferenceCount > 1)
                    {
                        _ = ReleaseLogicalTextureReference(
                            ref textureInfo.ReferenceCount);
                        return;
                    }
                    if (textureInfo.ReferenceCount <= 0)
                    {
                        throw new InvalidOperationException(
                            "A texture logical reference cannot be released more than once.");
                    }

                    if (!TryDetachTextureForRetirementLocked(
                            handle,
                            retireFence,
                            releaseLastReference: true,
                            out retirement))
                    {
                        throw new InvalidOperationException(
                            "A last-reference texture could not be detached for retirement.");
                    }
                }
            }

            RetireDetachedTexture(
                retirement ??
                throw new InvalidOperationException(
                    "Released texture retirement work was not published."));
        }

        internal static bool ReleaseLogicalTextureReference(ref int referenceCount)
        {
            if (referenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "A texture logical reference cannot be released more than once.");
            }

            referenceCount--;
            return referenceCount == 0;
        }

        internal static void RetainLogicalTextureReference(
            ref int referenceCount)
        {
            if (referenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "A retired texture reference cannot be retained.");
            }

            referenceCount = checked(referenceCount + 1);
        }

        private bool TryDetachTextureForRetirementLocked(
            TextureHandle handle,
            Fence retireFence,
            bool releaseLastReference,
            out PendingTextureRetirement retirement)
        {
            retirement = null!;
            if (!TryGetTextureInfoLocked(handle, out TextureInfo textureInfo))
            {
                return false;
            }

            if (releaseLastReference && textureInfo.ReferenceCount != 1)
            {
                throw new InvalidOperationException(
                    "Only a texture's last logical reference may enter retirement.");
            }
            uint detachedGeneration = AdvanceTextureGenerationForDetach(
                textureInfo.Generation,
                out bool slotCanBeReused);

            retirement = new PendingTextureRetirement(
                handle,
                textureInfo,
                textureInfo.BindlessIndex,
                textureInfo.BindlessHeap,
                retireFence);

            // Every potentially allocating publication operation is completed
            // before the cache, reference count, or generation is mutated.
            if (slotCanBeReused)
                _freeIndices.EnsureCapacity(checked(_freeIndices.Count + 1));
            _pendingTextureRetirements.EnsureCapacity(
                checked(_pendingTextureRetirements.Count + 1));
            if (_pendingTextureRetirements.ContainsKey(handle))
            {
                throw new InvalidOperationException(
                    $"Texture handle {handle} already has pending retirement work.");
            }

            if (releaseLastReference &&
                !ReleaseLogicalTextureReference(ref textureInfo.ReferenceCount))
            {
                throw new InvalidOperationException(
                    "A last-reference texture did not reach zero references.");
            }

            RemoveFromCacheLocked(handle);
            textureInfo.Generation = detachedGeneration;
            textureInfo.IsRetiring = true;
            if (slotCanBeReused)
                _freeIndices.Push(handle.Index);
            _pendingTextureRetirements.Add(handle, retirement);
            return true;
        }

        private void RetireDetachedTexture(PendingTextureRetirement retirement)
        {
            lock (retirement.Gate)
            {
                if (retirement.RetireFence.Handle != 0 &&
                    _deleter != null)
                {
                    if (!retirement.FenceWorkQueued)
                    {
                        _deleter.QueueDeletion(
                            retirement.RetireFence,
                            () => ExecuteDetachedTextureRetirement(retirement));
                        retirement.FenceWorkQueued = true;
                    }

                    return;
                }
            }

            ExecuteDetachedTextureRetirement(retirement);
        }

        private void ExecuteDetachedTextureRetirement(
            PendingTextureRetirement retirement)
        {
            lock (retirement.Gate)
            {
                ExecuteDependentTextureRetirement(
                    retirement.Progress,
                    () => FreeBindlessTextureIndexNow(
                        retirement.BindlessIndex,
                        retirement.BindlessHeap),
                    () => PrepareTextureResourceRetirement(retirement),
                    () => DestroyTextureImageViewNow(
                        retirement.RetiredView),
                    () => DestroyTextureImageNow(
                        retirement.RetiredImage,
                        retirement.RetiredAllocation));

                if (retirement.Progress.IsComplete)
                {
                    lock (_lock)
                    {
                        if (_pendingTextureRetirements.TryGetValue(
                                retirement.DetachedHandle,
                                out PendingTextureRetirement? current) &&
                            ReferenceEquals(current, retirement))
                        {
                            _pendingTextureRetirements.Remove(
                                retirement.DetachedHandle);
                        }
                    }
                }
            }
        }

        internal static void ExecuteDependentTextureRetirement(
            DurableTextureRetirementProgress progress,
            Action retireBindless,
            Action prepareResources,
            Action retireImageView,
            Action retireImage)
        {
            ArgumentNullException.ThrowIfNull(progress);
            progress.ExecuteBindless(retireBindless);
            progress.ExecuteResourcePreparation(prepareResources);
            progress.ExecuteImageView(retireImageView);
            progress.ExecuteImage(retireImage);
        }

        private void InitializeSolidTexture(
            ref TextureHandle handle,
            string cacheKey,
            ReadOnlySpan<byte> rgba,
            Format format,
            int bindlessIndex,
            BindlessHeap bindlessHeap)
        {
            TextureTransportStatistics statistics = TextureTransportImage.FromRgba8(
                rgba,
                1,
                1,
                TextureColorSpace.Linear,
                cacheKey == "default:normal" ? TextureSemantic.Normal : TextureSemantic.Data,
                CookedHash.Bytes(rgba),
                "Built-in texture").Statistics;

            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (!_textureCache.ContainsKey(cacheKey))
                    _textureCache.EnsureCapacity(checked(_textureCache.Count + 1));

                // A fixed default descriptor cannot safely be "unregistered" after
                // Vulkan accepted it. Publish the handle immediately and make every
                // subsequent step idempotent so a startup retry resumes the same
                // resource instead of allocating a second image or descriptor.
                if (!handle.IsValid)
                {
                    handle = CreateTexture(
                        1,
                        1,
                        format,
                        mipLevels: 1,
                        arrayLayers: 1,
                        additionalUsage: ImageUsageFlags.None,
                        bindlessIndex: bindlessIndex,
                        bindlessHeap: bindlessHeap,
                        debugName: $"Texture {cacheKey}");
                }

                UploadTextureData(handle, rgba, 1, 1, format);

                _textureCache[cacheKey] = handle;
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                textureInfo.DescriptorCacheKey = cacheKey;
                SharedTextureImage image = textureInfo.SharedImage
                    ?? throw new InvalidOperationException("Built-in texture does not own a sampled image.");
                image.TransportStatistics = statistics;
                image.LinearAverageColor = statistics.LinearChannelMean.ToVector4();
            }
        }

        private static uint NextContentRevision(uint current)
        {
            current++;
            return current == 0 ? 1 : current;
        }

        private void RecordTextureUpload(
            CommandBuffer commandBuffer,
            Silk.NET.Vulkan.Buffer stagingBuffer,
            TextureInfo textureInfo,
            uint width,
            uint height,
            bool generateMipmaps)
        {
            ImageSubresourceRange fullRange = ColorRange(0, textureInfo.MipLevels);
            PipelineImageBarrier(
                commandBuffer,
                textureInfo.Image,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                fullRange);

            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                ImageExtent = new Extent3D { Width = width, Height = height, Depth = 1 }
            };

            _context.Api.CmdCopyBufferToImage(
                commandBuffer,
                stagingBuffer,
                textureInfo.Image,
                ImageLayout.TransferDstOptimal,
                1,
                &region);

            if (generateMipmaps)
                RecordMipGeneration(commandBuffer, textureInfo, width, height);
            else
                PipelineImageBarrier(
                    commandBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderSampledReadBit,
                    fullRange);
        }

        private void RecordTextureUploadAllMipsAndLayers(
            CommandBuffer commandBuffer,
            Silk.NET.Vulkan.Buffer stagingBuffer,
            TextureInfo textureInfo,
            uint width,
            uint height)
        {
            ImageSubresourceRange fullRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = textureInfo.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = textureInfo.ArrayLayers
            };

            PipelineImageBarrier(
                commandBuffer,
                textureInfo.Image,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                fullRange);

            ulong offset = 0;
            var regions = new BufferImageCopy[checked(textureInfo.MipLevels * textureInfo.ArrayLayers)];
            int regionIndex = 0;
            uint mipWidth = width;
            uint mipHeight = height;

            for (uint mip = 0; mip < textureInfo.MipLevels; mip++)
            {
                ulong layerSize = CalculateTextureLevelByteSize(mipWidth, mipHeight, textureInfo.Format);
                for (uint layer = 0; layer < textureInfo.ArrayLayers; layer++)
                {
                    regions[regionIndex++] = new BufferImageCopy
                    {
                        BufferOffset = offset,
                        BufferRowLength = 0,
                        BufferImageHeight = 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            MipLevel = mip,
                            BaseArrayLayer = layer,
                            LayerCount = 1
                        },
                        ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                        ImageExtent = new Extent3D { Width = mipWidth, Height = mipHeight, Depth = 1 }
                    };
                    offset += layerSize;
                }

                mipWidth = Math.Max(1u, mipWidth / 2u);
                mipHeight = Math.Max(1u, mipHeight / 2u);
            }

            fixed (BufferImageCopy* regionPtr = regions)
            {
                _context.Api.CmdCopyBufferToImage(
                    commandBuffer,
                    stagingBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferDstOptimal,
                    (uint)regions.Length,
                    regionPtr);
            }

            PipelineImageBarrier(
                commandBuffer,
                textureInfo.Image,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderSampledReadBit,
                    fullRange);
        }

        private void RecordTextureUploadMipLevels(
            CommandBuffer commandBuffer,
            Silk.NET.Vulkan.Buffer stagingBuffer,
            TextureInfo textureInfo,
            IReadOnlyList<Ktx2MipLevel> levels)
        {
            ImageSubresourceRange fullRange = ColorRange(0, textureInfo.MipLevels);
            PipelineImageBarrier(
                commandBuffer,
                textureInfo.Image,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                fullRange);

            var regions = new BufferImageCopy[levels.Count];
            for (int i = 0; i < levels.Count; i++)
            {
                Ktx2MipLevel level = levels[i];
                regions[i] = new BufferImageCopy
                {
                    BufferOffset = checked((ulong)level.ByteOffset),
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = checked((uint)i),
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                    ImageExtent = new Extent3D { Width = level.Width, Height = level.Height, Depth = 1 }
                };
            }

            fixed (BufferImageCopy* regionPtr = regions)
            {
                _context.Api.CmdCopyBufferToImage(
                    commandBuffer,
                    stagingBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferDstOptimal,
                    checked((uint)regions.Length),
                    regionPtr);
            }

            PipelineImageBarrier(
                commandBuffer,
                textureInfo.Image,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                fullRange);
        }

        private void RecordMipGeneration(CommandBuffer commandBuffer, TextureInfo textureInfo, uint width, uint height)
        {
            int mipWidth = checked((int)width);
            int mipHeight = checked((int)height);

            for (uint i = 1; i < textureInfo.MipLevels; i++)
            {
                PipelineImageBarrier(
                    commandBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    ColorRange(i - 1, 1));

                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = i - 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = i,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };

                blit.SrcOffsets[0] = new Offset3D { X = 0, Y = 0, Z = 0 };
                blit.SrcOffsets[1] = new Offset3D { X = mipWidth, Y = mipHeight, Z = 1 };
                blit.DstOffsets[0] = new Offset3D { X = 0, Y = 0, Z = 0 };
                blit.DstOffsets[1] = new Offset3D
                {
                    X = mipWidth > 1 ? mipWidth / 2 : 1,
                    Y = mipHeight > 1 ? mipHeight / 2 : 1,
                    Z = 1
                };

                _context.Api.CmdBlitImage(
                    commandBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferSrcOptimal,
                    textureInfo.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &blit,
                    Filter.Linear);

                PipelineImageBarrier(
                    commandBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferSrcOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderSampledReadBit,
                    ColorRange(i - 1, 1));

                if (mipWidth > 1)
                    mipWidth /= 2;
                if (mipHeight > 1)
                    mipHeight /= 2;
            }

            PipelineImageBarrier(
                commandBuffer,
                textureInfo.Image,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                ColorRange(textureInfo.MipLevels - 1, 1));
        }

        private void PipelineImageBarrier(
            CommandBuffer commandBuffer,
            Image image,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            PipelineStageFlags2 srcStage,
            AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess,
            ImageSubresourceRange range)
        {
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStage,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStage,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private ImageView CreateImageView(
            Image image,
            Format format,
            ImageAspectFlags aspectMask,
            uint mipLevels = 1,
            uint arrayLayers = 1,
            ImageViewType viewType = ImageViewType.Type2D,
            uint baseMipLevel = 0,
            uint baseArrayLayer = 0)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = viewType,
                Format = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = baseMipLevel,
                    LevelCount = mipLevels,
                    BaseArrayLayer = baseArrayLayer,
                    LayerCount = arrayLayers
                }
            };

            Result result = _context.Api.CreateImageView(
                _context.Device,
                &viewInfo,
                null,
                out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create image view", result);

            return view;
        }

        private int AllocateOrRegisterBindlessIndex(
            int requestedIndex,
            ImageView view,
            BindlessHeap? bindlessHeap,
            TextureSamplerDescription? samplerDescription = null)
        {
            BindlessHeap? heap = bindlessHeap ?? _bindlessHeap;
            if (heap == null)
                return UnassignedBindlessIndex;

            Sampler sampler = samplerDescription.HasValue
                ? GetOrCreateSamplerLocked(samplerDescription.Value)
                : default;

            if (requestedIndex >= 0)
            {
                heap.RegisterTexture(requestedIndex, view, sampler);
                return requestedIndex;
            }

            return heap.AllocateTextureIndex(view, sampler);
        }

        private Sampler GetOrCreateSamplerLocked(TextureSamplerDescription description)
        {
            if (_samplerCache.TryGetValue(description, out Sampler sampler))
                return sampler;

            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = description.MagFilter == TextureFilterMode.Nearest ? Filter.Nearest : Filter.Linear,
                MinFilter = description.MinFilter == TextureFilterMode.Nearest ? Filter.Nearest : Filter.Linear,
                MipmapMode = description.MipFilter == TextureMipFilterMode.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
                AddressModeU = ToVulkanAddressMode(description.WrapU),
                AddressModeV = ToVulkanAddressMode(description.WrapV),
                AddressModeW = SamplerAddressMode.Repeat,
                MipLodBias = 0f,
                AnisotropyEnable = description.MaxAnisotropy > 1f,
                MaxAnisotropy = Math.Clamp(description.MaxAnisotropy, 1f, 16f),
                CompareEnable = false,
                CompareOp = CompareOp.Never,
                MinLod = 0f,
                MaxLod = 16f,
                BorderColor = BorderColor.FloatTransparentBlack,
                UnnormalizedCoordinates = false
            };

            Result result = _context.Api.CreateSampler(_context.Device, &samplerInfo, null, out sampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create imported texture sampler", result);

            _samplerCache.Add(description, sampler);
            return sampler;
        }

        private static SamplerAddressMode ToVulkanAddressMode(TextureWrapMode mode)
        {
            return mode switch
            {
                TextureWrapMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
                TextureWrapMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
                _ => SamplerAddressMode.Repeat
            };
        }

        private BindlessHeap ResolveBindlessHeap(BindlessHeap? bindlessHeap)
        {
            BindlessHeap? heap = bindlessHeap ?? _bindlessHeap;
            if (heap == null)
                throw new InvalidOperationException("A bindless heap is required to initialize default texture descriptors.");

            return heap;
        }

        private void ThrowIfDisposed() =>
            _lifecycle.ThrowIfDisposed();

        private bool SupportsLinearBlit(Format format)
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, &properties);
            const FormatFeatureFlags requiredFeatures =
                FormatFeatureFlags.BlitSrcBit |
                FormatFeatureFlags.BlitDstBit |
                FormatFeatureFlags.SampledImageFilterLinearBit;
            return (properties.OptimalTilingFeatures & requiredFeatures) == requiredFeatures;
        }

        private bool SupportsSampledOptimalImage(Format format)
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, &properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.SampledImageBit) == FormatFeatureFlags.SampledImageBit;
        }

        private static ImageSubresourceRange ColorRange(uint baseMipLevel, uint levelCount)
        {
            return new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                BaseArrayLayer = 0,
                LayerCount = 1
            };
        }

        private static uint CalculateMipLevels(uint width, uint height)
        {
            uint levels = 1;
            uint maxDimension = Math.Max(width, height);
            while (maxDimension > 1)
            {
                maxDimension /= 2;
                levels++;
            }

            return levels;
        }

        internal static bool TryDownscaleRgba(
            byte[] source,
            uint sourceWidth,
            uint sourceHeight,
            uint maxDimension,
            out byte[]? downscaled,
            out uint destinationWidth,
            out uint destinationHeight)
        {
            downscaled = null;
            destinationWidth = sourceWidth;
            destinationHeight = sourceHeight;

            if (maxDimension == 0 || Math.Max(sourceWidth, sourceHeight) <= maxDimension)
                return false;

            double scale = maxDimension / (double)Math.Max(sourceWidth, sourceHeight);
            destinationWidth = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
            destinationHeight = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
            downscaled = new byte[checked((int)(destinationWidth * destinationHeight * 4u))];

            for (uint y = 0; y < destinationHeight; y++)
            {
                uint sourceY = Math.Min(sourceHeight - 1u, (uint)((y + 0.5) / scale));
                for (uint x = 0; x < destinationWidth; x++)
                {
                    uint sourceX = Math.Min(sourceWidth - 1u, (uint)((x + 0.5) / scale));
                    int sourceOffset = checked((int)((sourceY * sourceWidth + sourceX) * 4u));
                    int destinationOffset = checked((int)((y * destinationWidth + x) * 4u));
                    downscaled[destinationOffset + 0] = source[sourceOffset + 0];
                    downscaled[destinationOffset + 1] = source[sourceOffset + 1];
                    downscaled[destinationOffset + 2] = source[sourceOffset + 2];
                    downscaled[destinationOffset + 3] = source[sourceOffset + 3];
                }
            }

            return true;
        }

        internal static RuntimeRgbaMipChain BuildRuntimeRgbaMipChain(
            ReadOnlySpan<byte> baseLevel,
            uint width,
            uint height,
            bool srgb,
            RuntimeTextureMipPolicy mipPolicy,
            double? targetAlphaCoverage = null)
        {
            RuntimeTextureMipPolicy normalizedPolicy = mipPolicy.ValidateAndNormalize();
            if (width == 0 || height == 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Mip dimensions must be positive.");
            int expectedLength = checked((int)(width * height * 4u));
            if (baseLevel.Length != expectedLength)
            {
                throw new ArgumentException(
                    $"RGBA base level contains {baseLevel.Length} bytes; expected {expectedLength}.",
                    nameof(baseLevel));
            }

            double coverage = targetAlphaCoverage ??
                (normalizedPolicy.PreserveAlphaCoverage
                    ? AlphaCoverageMipGenerator.CalculateCoverage(
                        baseLevel,
                        normalizedPolicy.AlphaCutoff)
                    : 0.0);
            if (!double.IsFinite(coverage) || coverage is < 0.0 or > 1.0)
                throw new ArgumentOutOfRangeException(nameof(targetAlphaCoverage));

            var levels = new List<RuntimeRgbaMipLevel>(checked((int)CalculateMipLevels(width, height)));
            byte[] current = baseLevel.ToArray();
            uint currentWidth = width;
            uint currentHeight = height;
            levels.Add(new RuntimeRgbaMipLevel(currentWidth, currentHeight, current));

            while (currentWidth > 1 || currentHeight > 1)
            {
                uint nextWidth = Math.Max(1u, currentWidth / 2u);
                uint nextHeight = Math.Max(1u, currentHeight / 2u);
                byte[] next = DownsampleRuntimeRgba(
                    current,
                    currentWidth,
                    currentHeight,
                    nextWidth,
                    nextHeight,
                    srgb);
                if (normalizedPolicy.PreserveAlphaCoverage)
                {
                    AlphaCoverageMipGenerator.PreserveCoverage(
                        next,
                        normalizedPolicy.AlphaCutoff,
                        coverage);
                }

                levels.Add(new RuntimeRgbaMipLevel(nextWidth, nextHeight, next));
                current = next;
                currentWidth = nextWidth;
                currentHeight = nextHeight;
            }

            int totalBytes = 0;
            foreach (RuntimeRgbaMipLevel level in levels)
                totalBytes = checked(totalBytes + level.Pixels.Length);
            var contiguous = new byte[totalBytes];
            int destinationOffset = 0;
            foreach (RuntimeRgbaMipLevel level in levels)
            {
                level.Pixels.CopyTo(contiguous, destinationOffset);
                destinationOffset += level.Pixels.Length;
            }

            return new RuntimeRgbaMipChain(levels, contiguous);
        }

        private static byte[] DownsampleRuntimeRgba(
            ReadOnlySpan<byte> source,
            uint sourceWidth,
            uint sourceHeight,
            uint targetWidth,
            uint targetHeight,
            bool srgb)
        {
            var target = new byte[checked((int)(targetWidth * targetHeight * 4u))];
            for (uint y = 0; y < targetHeight; y++)
            {
                uint y0 = y * sourceHeight / targetHeight;
                uint y1 = Math.Max(y0 + 1u, (y + 1u) * sourceHeight / targetHeight);
                for (uint x = 0; x < targetWidth; x++)
                {
                    uint x0 = x * sourceWidth / targetWidth;
                    uint x1 = Math.Max(x0 + 1u, (x + 1u) * sourceWidth / targetWidth);
                    double red = 0.0;
                    double green = 0.0;
                    double blue = 0.0;
                    double alpha = 0.0;
                    int sampleCount = 0;
                    for (uint sampleY = y0; sampleY < y1; sampleY++)
                        for (uint sampleX = x0; sampleX < x1; sampleX++)
                        {
                            int offset = checked((int)((sampleY * sourceWidth + sampleX) * 4u));
                            red += srgb
                                ? RuntimeSrgbToLinear(source[offset])
                                : source[offset] / 255.0;
                            green += srgb
                                ? RuntimeSrgbToLinear(source[offset + 1])
                                : source[offset + 1] / 255.0;
                            blue += srgb
                                ? RuntimeSrgbToLinear(source[offset + 2])
                                : source[offset + 2] / 255.0;
                            alpha += source[offset + 3] / 255.0;
                            sampleCount++;
                        }

                    int targetOffset = checked((int)((y * targetWidth + x) * 4u));
                    double inverseSampleCount = 1.0 / sampleCount;
                    target[targetOffset] = RuntimeToByte(
                        srgb
                            ? RuntimeLinearToSrgb(red * inverseSampleCount)
                            : red * inverseSampleCount);
                    target[targetOffset + 1] = RuntimeToByte(
                        srgb
                            ? RuntimeLinearToSrgb(green * inverseSampleCount)
                            : green * inverseSampleCount);
                    target[targetOffset + 2] = RuntimeToByte(
                        srgb
                            ? RuntimeLinearToSrgb(blue * inverseSampleCount)
                            : blue * inverseSampleCount);
                    target[targetOffset + 3] = RuntimeToByte(alpha * inverseSampleCount);
                }
            }

            return target;
        }

        private static double RuntimeSrgbToLinear(byte value)
        {
            double normalized = value / 255.0;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        private static double RuntimeLinearToSrgb(double value)
        {
            value = Math.Clamp(value, 0.0, 1.0);
            return value <= 0.0031308
                ? value * 12.92
                : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        }

        private static byte RuntimeToByte(double value) =>
            (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);

        internal static ulong CalculateTextureSourceContentHash(ReadOnlySpan<byte> sourceBytes)
        {
            if (sourceBytes.IsEmpty)
                throw new ArgumentException("Texture source bytes cannot be empty.", nameof(sourceBytes));

            return CookedHash.Bytes(sourceBytes);
        }

        internal static string CreateTextureCacheKey(
            string fullPath,
            bool generateMipmaps,
            bool srgb,
            uint maxDimension = 0,
            TextureSamplerDescription? samplerDescription = null,
            TextureContainerKind containerKind = TextureContainerKind.StandardImage,
            ulong? sourceContentHash = null,
            TextureSemantic semantic = TextureSemantic.Color,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentException("Texture cache path cannot be null or empty.", nameof(fullPath));

            TextureSamplerDescription sampler =
                samplerDescription ?? TextureSamplerDescription.Default;
            string imageKey = CreateTextureImageCacheKey(
                fullPath,
                generateMipmaps,
                srgb,
                maxDimension,
                containerKind,
                sourceContentHash,
                semantic,
                mipPolicy);
            return CreateTextureDescriptorCacheKey(imageKey, sampler);
        }

        /// <summary>
        /// Builds the cache identity for the immutable image allocation. Sampler state is
        /// deliberately excluded: Vulkan descriptors can combine one image view with multiple
        /// cached samplers without duplicating the image and its mip chain.
        /// </summary>
        internal static string CreateTextureImageCacheKey(
            string fullPath,
            bool generateMipmaps,
            bool srgb,
            uint maxDimension = 0,
            TextureContainerKind containerKind = TextureContainerKind.StandardImage,
            ulong? sourceContentHash = null,
            TextureSemantic semantic = TextureSemantic.Color,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentException("Texture cache path cannot be null or empty.", nameof(fullPath));

            string identity = Path.IsPathRooted(fullPath) ? Path.GetFullPath(fullPath) : fullPath;
            RuntimeTextureMipPolicy normalizedPolicy = mipPolicy.ValidateAndNormalize();
            string content = sourceContentHash.HasValue
                ? sourceContentHash.Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
                : "unresolved";
            return FormattableString.Invariant(
                $"{identity}|content={content}|container={containerKind}|semantic={semantic}|mips={generateMipmaps}|mipPolicy={normalizedPolicy.CacheKey}|srgb={srgb}|max={maxDimension}");
        }

        private static string CreateTextureDescriptorCacheKey(
            string imageCacheKey,
            TextureSamplerDescription samplerDescription) =>
            CreateTextureDescriptorCacheKey(
                imageCacheKey,
                CreateTextureSamplerCacheIdentity(samplerDescription));

        private static string CreateTextureDescriptorCacheKey(
            string imageCacheKey,
            string samplerIdentity) =>
            $"{imageCacheKey}|sampler={samplerIdentity}";

        private static string CreateTextureSamplerCacheIdentity(
            TextureSamplerDescription sampler) =>
            FormattableString.Invariant(
                $"{sampler.WrapU}:{sampler.WrapV}:{sampler.MinFilter}:{sampler.MagFilter}:{sampler.MipFilter}:{sampler.MaxAnisotropy:R}");

        private static ulong CalculateRequiredStagingSize(uint width, uint height, Format format)
        {
            return checked((ulong)width * height * GetBytesPerPixel(format));
        }

        private static ulong CalculateRequiredStagingSizeAllMipsAndLayers(uint width, uint height, Format format, uint mipLevels, uint arrayLayers)
        {
            ulong total = 0;
            uint mipWidth = width;
            uint mipHeight = height;
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                total = checked(total + CalculateTextureLevelByteSize(mipWidth, mipHeight, format) * arrayLayers);
                mipWidth = Math.Max(1u, mipWidth / 2u);
                mipHeight = Math.Max(1u, mipHeight / 2u);
            }

            return total;
        }

        private static uint GetBytesPerPixel(Format format)
        {
            return format switch
            {
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4,
                Format.R32G32B32A32Sfloat => 16,
                Format.R16G16B16A16Sfloat => 8,
                Format.R8Unorm or Format.R8Srgb => 1,
                Format.R32Sfloat => 4,
                _ => throw new NotSupportedException($"Texture format {format} does not have a known staging size.")
            };
        }

        /// <summary>
        /// Resolves a descriptor handle to the underlying immutable image allocation for render
        /// graph synchronization. Invalid or retired handles intentionally return false so an
        /// optional material slot cannot manufacture a stale ownership contract.
        /// </summary>
        public bool TryGetImageBinding(TextureHandle handle, out TextureImageBinding binding)
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                _lifecycle.ThrowIfDisposedUnderGate(_lock);
                if (!TryGetTextureInfoLocked(handle, out TextureInfo? textureInfo) || textureInfo.Image.Handle == 0)
                {
                    binding = default;
                    return false;
                }

                binding = new TextureImageBinding(
                    textureInfo.Image,
                    textureInfo.Format,
                    textureInfo.Extent,
                    textureInfo.MipLevels,
                    textureInfo.ArrayLayers,
                    textureInfo.SharedImage?.ContentRevision ?? textureInfo.Generation);
                return true;
            }
        }

        private static bool IsBlockCompressedFormat(Format format)
        {
            return format is
                Format.BC1RgbUnormBlock or
                Format.BC1RgbSrgbBlock or
                Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC2UnormBlock or
                Format.BC2SrgbBlock or
                Format.BC3UnormBlock or
                Format.BC3SrgbBlock or
                Format.BC4UnormBlock or
                Format.BC4SNormBlock or
                Format.BC5UnormBlock or
                Format.BC5SNormBlock or
                Format.BC6HUfloatBlock or
                Format.BC6HSfloatBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock;
        }

        private static ulong CalculateTextureByteSize(uint width, uint height, Format format, uint mipLevels, uint arrayLayers)
        {
            ulong total = 0;
            uint mipWidth = width;
            uint mipHeight = height;
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                total = checked(total + CalculateTextureLevelByteSize(mipWidth, mipHeight, format) * arrayLayers);
                mipWidth = Math.Max(1u, mipWidth / 2u);
                mipHeight = Math.Max(1u, mipHeight / 2u);
            }

            return total;
        }

        private static ulong CalculateTextureLevelByteSize(uint width, uint height, Format format)
        {
            return ImageByteEstimator.EstimateMipBytes(format, width, height);
        }

        private TextureInfo GetTextureInfoLocked(TextureHandle handle)
        {
            if (!TryGetTextureInfoLocked(handle, out TextureInfo? textureInfo))
                throw new InvalidOperationException("Invalid texture handle.");

            return textureInfo;
        }

        private bool TryGetTextureInfoLocked(TextureHandle handle, out TextureInfo textureInfo)
        {
            textureInfo = null!;
            if (!handle.IsValid || handle.Index >= _textures.Count)
                return false;

            textureInfo = _textures[handle.Index];
            return textureInfo.Generation == handle.Generation &&
                   IsLiveTexture(textureInfo);
        }

        private static bool IsLiveTexture(TextureInfo textureInfo)
        {
            return !textureInfo.IsRetiring &&
                   textureInfo.SharedImage != null &&
                   textureInfo.Image.Handle != 0 &&
                   textureInfo.View.Handle != 0;
        }

        private uint AllocateGeneration(int textureIndex)
        {
            if (textureIndex >= _textures.Count)
                return checked((uint)(_textures.Count + 1));

            return AdvanceTextureGeneration(
                _textures[textureIndex].Generation,
                textureIndex);
        }

        internal static uint AdvanceTextureGeneration(
            uint currentGeneration,
            int textureIndex)
        {
            if (currentGeneration == uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Texture slot {textureIndex} exhausted its generation space and " +
                    "cannot be published safely.");
            }

            return currentGeneration + 1;
        }

        internal static uint AdvanceTextureGenerationForDetach(
            uint currentGeneration,
            out bool slotCanBeReused)
        {
            uint detachedGeneration = currentGeneration == uint.MaxValue
                ? uint.MaxValue
                : currentGeneration + 1;
            slotCanBeReused = detachedGeneration < uint.MaxValue;
            return detachedGeneration;
        }

        private void RemoveFromCacheLocked(TextureHandle handle)
        {
            if (TryGetTextureInfoLocked(handle, out TextureInfo textureInfo) &&
                !string.IsNullOrWhiteSpace(textureInfo.DescriptorCacheKey) &&
                _textureCache.TryGetValue(textureInfo.DescriptorCacheKey, out TextureHandle mappedHandle) &&
                mappedHandle == handle)
            {
                _textureCache.Remove(textureInfo.DescriptorCacheKey);
                textureInfo.DescriptorCacheKey = null;
                return;
            }

            string? keyToRemove = null;
            foreach (KeyValuePair<string, TextureHandle> entry in _textureCache)
            {
                if (entry.Value == handle)
                {
                    keyToRemove = entry.Key;
                    break;
                }
            }

            if (keyToRemove != null)
                _textureCache.Remove(keyToRemove);
        }

        private void PrepareTextureResourceRetirement(
            PendingTextureRetirement retirement)
        {
            lock (_lock)
            {
                TextureInfo textureInfo = retirement.TextureInfo;
                SharedTextureImage? sharedImage = textureInfo.SharedImage;
                if (sharedImage != null)
                {
                    if (sharedImage.ReferenceCount <= 0)
                    {
                        throw new InvalidOperationException(
                            "A shared texture image was released more than once.");
                    }
                    if (sharedImage.ReferenceCount == 1 &&
                        _estimatedTextureBytes < sharedImage.EstimatedByteSize)
                    {
                        throw new InvalidOperationException(
                            "Texture memory accounting would underflow during retirement.");
                    }
                    if (sharedImage.ReferenceCount == 1 &&
                        sharedImage.WasDownscaled &&
                        _downscaledTextureCount <= 0)
                    {
                        throw new InvalidOperationException(
                            "Downscaled texture accounting would underflow during retirement.");
                    }
                }

                textureInfo.SharedImage = null;
                textureInfo.Image = default;
                textureInfo.Allocation = null;
                textureInfo.View = default;
                textureInfo.BindlessIndex = UnassignedBindlessIndex;
                textureInfo.BindlessHeap = null;
                textureInfo.SourcePath = null;
                textureInfo.SourceIdentity = null;
                textureInfo.SourceKind = TextureSourceKind.Unknown;
                textureInfo.SourceEncodedByteLength = 0;
                textureInfo.OriginalWidth = 0;
                textureInfo.OriginalHeight = 0;
                textureInfo.IsCompressed = false;
                textureInfo.WasDownscaled = false;
                textureInfo.EstimatedByteSize = 0;
                textureInfo.DescriptorCacheKey = null;
                textureInfo.SamplerDescription = null;

                if (sharedImage == null)
                    return;

                sharedImage.ReferenceCount--;
                if (sharedImage.ReferenceCount != 0)
                    return;

                if (!string.IsNullOrWhiteSpace(sharedImage.CacheKey) &&
                    _textureImageCache.TryGetValue(
                        sharedImage.CacheKey,
                        out SharedTextureImage? cachedImage) &&
                    ReferenceEquals(cachedImage, sharedImage))
                {
                    _textureImageCache.Remove(sharedImage.CacheKey);
                }

                retirement.RetiredImage = sharedImage.Image;
                retirement.RetiredAllocation = sharedImage.Allocation;
                retirement.RetiredView = sharedImage.View;
                sharedImage.Image = default;
                sharedImage.Allocation = null;
                sharedImage.View = default;
                _estimatedTextureBytes -= sharedImage.EstimatedByteSize;
                if (sharedImage.WasDownscaled)
                    _downscaledTextureCount--;
            }
        }

        private void DestroyTextureImageViewNow(ImageView view)
        {
            if (view.Handle == 0)
                return;

            _context.Api.DestroyImageView(_context.Device, view, null);
        }

        private void DestroyTextureImageNow(
            Image image,
            Allocation* allocation)
        {
            if (image.Handle == 0)
                return;

            GpuAllocator.Apis.DestroyImage(_context.Allocator, image, allocation);
        }

        private static void FreeBindlessTextureIndexNow(
            int bindlessIndex,
            BindlessHeap? bindlessHeap)
        {
            if (bindlessIndex < BindlessIndex.FirstDynamicTextureIndex ||
                bindlessHeap == null)
                return;

            bindlessHeap.FreeTextureIndex(bindlessIndex);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            lock (_disposeGate)
            {
                if (_lifecycle.IsDisposed)
                    return;

                _lifecycle.BeginDispose();

                FlushPendingTextureCreationRollbacks();

                // Detached ownership is not present in the live texture
                // caches. Drain its durable ledger first so captured
                // views/images cannot leak or be double-destroyed by normal
                // cache teardown.
                FlushPendingTextureRetirements();

                lock (_lock)
                {
                    EnsureTextureRetirementLedgerDrained(
                        _pendingTextureRetirements.Count);

                    var sharedImages = new HashSet<SharedTextureImage>();
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (textureInfo.SharedImage != null)
                            sharedImages.Add(textureInfo.SharedImage);
                    }

                    List<Exception>? failures = null;
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (textureInfo.SharedImage == null)
                            continue;

                        try
                        {
                            textureInfo.DescriptorDisposalProgress.Execute(
                                () =>
                                {
                                    FreeBindlessTextureIndexNow(
                                        textureInfo.BindlessIndex,
                                        textureInfo.BindlessHeap);
                                    textureInfo.BindlessIndex =
                                        UnassignedBindlessIndex;
                                    textureInfo.BindlessHeap = null;
                                });
                        }
                        catch (Exception exception)
                        {
                            (failures ??= []).Add(exception);
                        }
                    }

                    foreach (SharedTextureImage sharedImage in sharedImages)
                    {
                        if (HasPendingLiveDescriptorRetirement(sharedImage))
                            continue;

                        try
                        {
                            sharedImage.DisposalProgress.ExecuteView(() =>
                            {
                                if (sharedImage.View.Handle != 0)
                                {
                                    _context.Api.DestroyImageView(
                                        _context.Device,
                                        sharedImage.View,
                                        null);
                                }

                                sharedImage.View = default;
                            });
                        }
                        catch (Exception exception)
                        {
                            (failures ??= []).Add(exception);
                        }

                        // Vulkan requires every image view to be gone before
                        // the underlying image allocation can be destroyed.
                        // Other images and samplers remain independent and are
                        // still attempted when this view fails.
                        if (sharedImage.DisposalProgress.ViewCompleted)
                        {
                            try
                            {
                                sharedImage.DisposalProgress.ExecuteImage(() =>
                                {
                                    if (sharedImage.Image.Handle != 0)
                                    {
                                        GpuAllocator.Apis.DestroyImage(
                                            _context.Allocator,
                                            sharedImage.Image,
                                            sharedImage.Allocation);
                                    }

                                    sharedImage.Image = default;
                                    sharedImage.Allocation = null;
                                });
                            }
                            catch (Exception exception)
                            {
                                (failures ??= []).Add(exception);
                            }
                        }
                    }

                    List<TextureSamplerDescription> samplerKeys =
                        [.. _samplerCache.Keys];
                    foreach (TextureSamplerDescription samplerKey in samplerKeys)
                    {
                        if (HasPendingSamplerDescriptorRetirement(samplerKey))
                            continue;

                        Sampler sampler = _samplerCache[samplerKey];
                        if (sampler.Handle == 0)
                            continue;

                        try
                        {
                            _context.Api.DestroySampler(
                                _context.Device,
                                sampler,
                                null);
                            // The zero handle is the durable sampler stage bit:
                            // retries cannot destroy a successfully retired
                            // sampler a second time.
                            _samplerCache[samplerKey] = default;
                        }
                        catch (Exception exception)
                        {
                            (failures ??= []).Add(exception);
                        }
                    }

                    ThrowTextureDisposalFailures(failures);

                    _textures.Clear();
                    _textureCache.Clear();
                    _textureImageCache.Clear();
                    _samplerCache.Clear();
                    _freeIndices.Clear();
                    _pendingTextureRetirements.Clear();
                    _pendingTextureCreationRollbacks.Clear();
                    _lifecycle.CompleteDispose();
                }
            }

            System.Diagnostics.Debug.WriteLine("Texture manager disposed.");
        }

        private bool HasPendingLiveDescriptorRetirement(
            SharedTextureImage sharedImage)
        {
            foreach (TextureInfo textureInfo in _textures)
            {
                if (ReferenceEquals(textureInfo.SharedImage, sharedImage) &&
                    !textureInfo.DescriptorDisposalProgress.IsComplete)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasPendingSamplerDescriptorRetirement(
            TextureSamplerDescription samplerDescription)
        {
            foreach (TextureInfo textureInfo in _textures)
            {
                if (textureInfo.SamplerDescription == samplerDescription &&
                    !textureInfo.DescriptorDisposalProgress.IsComplete)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ThrowTextureDisposalFailures(
            List<Exception>? failures)
        {
            if (failures is not { Count: > 0 })
                return;
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }

            throw new AggregateException(
                "Texture manager disposal left one or more GPU resources pending.",
                failures);
        }

        internal static void EnsureTextureRetirementLedgerDrained(
            int pendingRetirementCount)
        {
            if (pendingRetirementCount < 0)
                throw new ArgumentOutOfRangeException(nameof(pendingRetirementCount));
            if (pendingRetirementCount != 0)
            {
                throw new InvalidOperationException(
                    "Texture manager disposal cannot continue while durable " +
                    "retirement work remains incomplete.");
            }
        }
    }

    public readonly record struct TextureContentChangedEvent(
        TextureHandle Handle,
        uint ContentRevision,
        ulong SourceContentHash);

    internal enum TexturePublicationCheckpoint : byte
    {
        ImageCachePublished = 0,
        DescriptorCachePublished = 1,
        AliasSlotPublished = 2,
        AliasReferencePublished = 3,
        AliasCachePublished = 4,
        TextureSlotPublished = 5,
        TextureAccountingPublished = 6,
        DefaultWhitePublished = 7,
        DefaultNormalPublished = 8,
        DefaultBlackPublished = 9
    }

    /// <summary>
    /// Commits built-in textures independently and in dependency order. A
    /// failed stage remains retryable while earlier stages are never repeated.
    /// Checkpoints run after the stage bit commits, which also makes injected
    /// post-publication failures safe to resume.
    /// </summary>
    internal sealed class ResumableDefaultTextureInitialization
    {
        private bool _whiteCompleted;
        private bool _normalCompleted;
        private bool _blackCompleted;

        internal bool IsComplete =>
            _whiteCompleted &&
            _normalCompleted &&
            _blackCompleted;

        internal void Execute(
            Action initializeWhite,
            Action initializeNormal,
            Action initializeBlack,
            Action<TexturePublicationCheckpoint>? checkpoint = null)
        {
            ExecuteStage(
                ref _whiteCompleted,
                initializeWhite,
                TexturePublicationCheckpoint.DefaultWhitePublished,
                checkpoint);
            ExecuteStage(
                ref _normalCompleted,
                initializeNormal,
                TexturePublicationCheckpoint.DefaultNormalPublished,
                checkpoint);
            ExecuteStage(
                ref _blackCompleted,
                initializeBlack,
                TexturePublicationCheckpoint.DefaultBlackPublished,
                checkpoint);
        }

        private static void ExecuteStage(
            ref bool completed,
            Action initialize,
            TexturePublicationCheckpoint publicationCheckpoint,
            Action<TexturePublicationCheckpoint>? checkpoint)
        {
            ArgumentNullException.ThrowIfNull(initialize);
            if (completed)
                return;

            initialize();
            completed = true;
            checkpoint?.Invoke(publicationCheckpoint);
        }
    }

    /// <summary>
    /// Monotonic lifecycle gate. Once disposal starts, operational entry
    /// points fail closed even when a cleanup failure leaves Dispose retryable.
    /// </summary>
    internal sealed class TextureManagerLifecycleState
    {
        private const int Active = 0;
        private const int Disposing = 1;
        private const int Disposed = 2;
        private int _state;

        internal bool IsDisposed =>
            Volatile.Read(ref _state) == Disposed;

        internal void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _state) != Active)
            {
                throw new ObjectDisposedException(
                    nameof(TextureManager),
                    "Texture manager disposal has started; no new operations are accepted.");
            }
        }

        internal void BeginDispose()
        {
            _ = Interlocked.CompareExchange(
                ref _state,
                Disposing,
                Active);
        }

        internal void BeginDisposeUnderGate(object publicationGate)
        {
            ArgumentNullException.ThrowIfNull(publicationGate);
            if (!Monitor.IsEntered(publicationGate))
            {
                throw new SynchronizationLockException(
                    "The texture publication gate must be held while disposal starts.");
            }

            BeginDispose();
        }

        internal void ThrowIfDisposedUnderGate(object publicationGate)
        {
            ArgumentNullException.ThrowIfNull(publicationGate);
            if (!Monitor.IsEntered(publicationGate))
            {
                throw new SynchronizationLockException(
                    "The texture publication gate must be held while publication is validated.");
            }

            ThrowIfDisposed();
        }

        internal void CompleteDispose()
        {
            int previous = Interlocked.Exchange(ref _state, Disposed);
            if (previous == Active)
            {
                throw new InvalidOperationException(
                    "Texture manager disposal cannot complete before it starts.");
            }
        }
    }

    /// <summary>
    /// Durable, dependency-ordered cleanup stages for a sampled image. A stage
    /// bit is set only after its Vulkan destruction call succeeds.
    /// </summary>
    internal sealed class DurableTextureDisposalProgress
    {
        private bool _viewCompleted;
        private bool _imageCompleted;

        internal bool ViewCompleted => _viewCompleted;
        internal bool ImageCompleted => _imageCompleted;
        internal bool IsComplete => ViewCompleted && ImageCompleted;

        internal void ExecuteView(Action action) =>
            ExecuteStage(ref _viewCompleted, action);

        internal void ExecuteImage(Action action)
        {
            if (!ViewCompleted)
            {
                throw new InvalidOperationException(
                    "A texture image cannot be disposed before its image view.");
            }

            ExecuteStage(ref _imageCompleted, action);
        }

        private static void ExecuteStage(ref bool completed, Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (completed)
                return;

            action();
            completed = true;
        }
    }

    internal sealed class DurableTextureDescriptorDisposalProgress
    {
        private bool _completed;

        internal bool IsComplete => _completed;

        internal void Execute(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (_completed)
                return;

            action();
            _completed = true;
        }
    }

    /// <summary>
    /// Durable delivery ledger for the cross-manager texture/material
    /// publication boundary. Each alias/subscriber pair is independent:
    /// failures do not prevent later aliases from being attempted, and retries
    /// invoke only deliveries that have not yet committed.
    /// </summary>
    internal sealed class DurableTextureContentNotificationDispatcher
    {
        private readonly Dictionary<DeliveryKey, PendingDelivery> _pending = [];
        private readonly object _gate = new();
        private readonly object _deliveryGate = new();
        private bool _deliveryInProgress;
        private long _failureCount;
        private Exception? _lastFailure;

        public int PendingCount
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        public long FailureCount
        {
            get
            {
                lock (_gate)
                    return _failureCount;
            }
        }

        public Exception? LastFailure
        {
            get
            {
                lock (_gate)
                    return _lastFailure;
            }
        }

        public void Dispatch(
            IReadOnlyList<TextureContentChangedEvent> notifications,
            Action<TextureContentChangedEvent>? subscribers)
        {
            ArgumentNullException.ThrowIfNull(notifications);
            if (notifications.Count == 0 || subscribers == null)
                return;

            lock (_deliveryGate)
            {
                if (_deliveryInProgress)
                {
                    throw new InvalidOperationException(
                        "Texture-content publication cannot be reentered by a subscriber.");
                }

                _deliveryInProgress = true;
                try
                {
                    DispatchCore(notifications, subscribers);
                }
                finally
                {
                    _deliveryInProgress = false;
                }
            }
        }

        private void DispatchCore(
            IReadOnlyList<TextureContentChangedEvent> notifications,
            Action<TextureContentChangedEvent> subscribers)
        {
            Delegate[] invocationList = subscribers.GetInvocationList();
            var handlers =
                new Action<TextureContentChangedEvent>[invocationList.Length];
            for (int index = 0; index < invocationList.Length; index++)
            {
                handlers[index] =
                    (Action<TextureContentChangedEvent>)invocationList[index];
            }

            lock (_gate)
            {
                _pending.EnsureCapacity(
                    checked(
                        _pending.Count +
                        notifications.Count * handlers.Length));
            }

            List<Exception>? failures = null;
            foreach (TextureContentChangedEvent notification in notifications)
                foreach (Action<TextureContentChangedEvent> handler in handlers)
                {
                    var key = new DeliveryKey(notification.Handle, handler);
                    try
                    {
                        handler(notification);
                        lock (_gate)
                        {
                            _pending.Remove(key);
                            if (_pending.Count == 0)
                                _lastFailure = null;
                        }
                    }
                    catch (Exception exception)
                    {
                        lock (_gate)
                        {
                            _pending[key] =
                                new PendingDelivery(key, notification, handler);
                            RecordFailureLocked(exception);
                        }
                        (failures ??= []).Add(exception);
                    }
                }

            ThrowFailures(failures);
        }

        public int RetryPending()
        {
            lock (_deliveryGate)
            {
                // A subscriber may ask for a retry while its own delivery is
                // still in flight. The outer delivery remains authoritative;
                // deferring the nested request prevents recursive duplicate
                // execution. Concurrent callers serialize on this same gate.
                if (_deliveryInProgress)
                    return 0;

                _deliveryInProgress = true;
                try
                {
                    return RetryPendingCore();
                }
                finally
                {
                    _deliveryInProgress = false;
                }
            }
        }

        private int RetryPendingCore()
        {
            PendingDelivery[] pending;
            lock (_gate)
                pending = [.. _pending.Values];

            int completed = 0;
            List<Exception>? failures = null;
            foreach (PendingDelivery delivery in pending)
            {
                try
                {
                    delivery.Handler(delivery.Notification);
                    lock (_gate)
                    {
                        if (_pending.TryGetValue(
                                delivery.Key,
                                out PendingDelivery? current) &&
                            ReferenceEquals(current, delivery))
                        {
                            _pending.Remove(delivery.Key);
                            completed++;
                        }
                        if (_pending.Count == 0)
                            _lastFailure = null;
                    }
                }
                catch (Exception exception)
                {
                    lock (_gate)
                        RecordFailureLocked(exception);
                    (failures ??= []).Add(exception);
                }
            }

            ThrowFailures(failures);
            return completed;
        }

        private void RecordFailureLocked(Exception exception)
        {
            _failureCount = checked(_failureCount + 1);
            _lastFailure = exception;
        }

        private static void ThrowFailures(List<Exception>? failures)
        {
            if (failures is not { Count: > 0 })
                return;
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }

            throw new AggregateException(
                "One or more texture-content subscribers rejected publication.",
                failures);
        }

        private sealed class PendingDelivery
        {
            public PendingDelivery(
                DeliveryKey key,
                TextureContentChangedEvent notification,
                Action<TextureContentChangedEvent> handler)
            {
                Key = key;
                Notification = notification;
                Handler = handler;
            }

            public DeliveryKey Key { get; }
            public TextureContentChangedEvent Notification { get; }
            public Action<TextureContentChangedEvent> Handler { get; }
        }

        private readonly record struct DeliveryKey(
            TextureHandle Handle,
            Action<TextureContentChangedEvent> Handler);
    }

    /// <summary>
    /// Exactly-once progress for independently fallible texture retirement
    /// stages. A stage is committed only after its ownership transfer or
    /// destruction action returns successfully, so a retry cannot repeat
    /// completed work.
    /// </summary>
    internal sealed class DurableTextureRetirementProgress
    {
        private bool _bindlessCompleted;
        private bool _resourcePreparationCompleted;
        private bool _imageViewCompleted;
        private bool _imageCompleted;

        public bool BindlessCompleted => _bindlessCompleted;
        public bool ResourcePreparationCompleted => _resourcePreparationCompleted;
        public bool ImageViewCompleted => _imageViewCompleted;
        public bool ImageCompleted => _imageCompleted;

        public bool IsComplete =>
            BindlessCompleted &&
            ResourcePreparationCompleted &&
            ImageViewCompleted &&
            ImageCompleted;

        public void ExecuteBindless(Action action) =>
            ExecuteStage(ref _bindlessCompleted, action);

        public void ExecuteResourcePreparation(Action action)
        {
            EnsureBindlessRetired();
            ExecuteStage(ref _resourcePreparationCompleted, action);
        }

        public void ExecuteImageView(Action action)
        {
            EnsureResourcesPrepared();
            ExecuteStage(ref _imageViewCompleted, action);
        }

        public void ExecuteImage(Action action)
        {
            EnsureImageViewRetired();
            ExecuteStage(ref _imageCompleted, action);
        }

        private void EnsureBindlessRetired()
        {
            if (!BindlessCompleted)
            {
                throw new InvalidOperationException(
                    "Texture ownership cannot be prepared before its bindless descriptor retires.");
            }
        }

        private void EnsureResourcesPrepared()
        {
            if (!ResourcePreparationCompleted)
            {
                throw new InvalidOperationException(
                    "Physical texture retirement cannot precede ownership preparation.");
            }
        }

        private void EnsureImageViewRetired()
        {
            EnsureResourcesPrepared();
            if (!ImageViewCompleted)
            {
                throw new InvalidOperationException(
                    "A texture image cannot retire before its image view.");
            }
        }

        private static void ExecuteStage(
            ref bool completed,
            Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (completed)
                return;

            action();
            completed = true;
        }
    }

    /// <summary>
    /// Runtime mip-generation policy for decoded, uncooked RGBA textures.
    /// Alpha cutoffs remain upper-unclamped so legal values above one retain
    /// fully-uncovered behavior.
    /// </summary>
    public readonly record struct RuntimeTextureMipPolicy(
        bool PreserveAlphaCoverage,
        float AlphaCutoff)
    {
        public static RuntimeTextureMipPolicy Default { get; } = new(false, 0.5f);

        public static RuntimeTextureMipPolicy AlphaMask(float cutoff) => new(true, cutoff);

        internal RuntimeTextureMipPolicy ValidateAndNormalize()
        {
            if (!PreserveAlphaCoverage)
                return Default;
            if (!float.IsFinite(AlphaCutoff) || AlphaCutoff < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AlphaCutoff),
                    "Alpha cutoff must be finite and non-negative.");
            }

            return this;
        }

        internal string CacheKey =>
            PreserveAlphaCoverage
                ? $"coverage:{AlphaCutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
                : "standard";
    }

    internal readonly record struct RuntimeRgbaMipLevel(
        uint Width,
        uint Height,
        byte[] Pixels);

    internal sealed record RuntimeRgbaMipChain(
        IReadOnlyList<RuntimeRgbaMipLevel> Levels,
        byte[] ContiguousPixels);

    public readonly record struct TextureContentReloadResult(
        bool Changed,
        uint ContentRevision,
        ulong SourceContentHash,
        int NotifiedAliasCount);

    /// <summary>Immutable physical image metadata used when importing sampled textures into a graph plan.</summary>
    public readonly record struct TextureImageBinding(
        Image Image,
        Format Format,
        Extent3D Extent,
        uint MipLevels,
        uint ArrayLayers,
        uint Generation);
}
