using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using StbImageSharp;
using GpuAllocator = Vma;
using Vma;
using Buffer = System.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class TextureManager : IDisposable
    {
        private const int UnassignedBindlessIndex = -1;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap? _bindlessHeap;
        private readonly FenceBasedDeleter? _deleter;
        private readonly Dictionary<string, TextureHandle> _textureCache = new Dictionary<string, TextureHandle>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TextureInfo> _textures = new List<TextureInfo>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly object _lock = new object();

        private TextureHandle _defaultWhiteTexture = TextureHandle.Invalid;
        private TextureHandle _defaultNormalTexture = TextureHandle.Invalid;
        private TextureHandle _defaultBlackTexture = TextureHandle.Invalid;
        private int _mipmapFallbackCount;
        private bool _disposed;

        private sealed class TextureInfo
        {
            public Image Image;
            public Allocation* Allocation;
            public ImageView View;
            public Format Format;
            public Extent3D Extent;
            public uint MipLevels;
            public uint ArrayLayers;
            public uint Generation;
            public int BindlessIndex = UnassignedBindlessIndex;
            public string? SourcePath;
            public int ReferenceCount = 1;
        }

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

        public TextureHandle DefaultWhiteTexture => _defaultWhiteTexture;
        public TextureHandle DefaultNormalTexture => _defaultNormalTexture;
        public TextureHandle DefaultBlackTexture => _defaultBlackTexture;
        public int TextureCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (textureInfo.Image.Handle != 0 && textureInfo.View.Handle != 0)
                            count++;
                    }

                    return count;
                }
            }
        }

        public int LoadedFileTextureCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (TextureInfo textureInfo in _textures)
                    {
                        if (textureInfo.Image.Handle != 0 &&
                            textureInfo.View.Handle != 0 &&
                            !string.IsNullOrWhiteSpace(textureInfo.SourcePath))
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
                lock (_lock)
                    return _mipmapFallbackCount;
            }
        }

        public void InitializeDefaultTextures(BindlessHeap? bindlessHeap = null)
        {
            BindlessHeap heap = ResolveBindlessHeap(bindlessHeap);

            lock (_lock)
            {
                if (_defaultWhiteTexture.IsValid)
                    return;

                _defaultWhiteTexture = CreateSolidTexture(
                    "default:white",
                    stackalloc byte[] { 255, 255, 255, 255 },
                    Format.R8G8B8A8Unorm,
                    BindlessIndex.DefaultWhiteTexture,
                    heap);

                _defaultNormalTexture = CreateSolidTexture(
                    "default:normal",
                    stackalloc byte[] { 128, 128, 255, 255 },
                    Format.R8G8B8A8Unorm,
                    BindlessIndex.DefaultNormalTexture,
                    heap);

                _defaultBlackTexture = CreateSolidTexture(
                    "default:black",
                    stackalloc byte[] { 0, 0, 0, 255 },
                    Format.R8G8B8A8Unorm,
                    BindlessIndex.DefaultBlackTexture,
                    heap);
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
            BindlessHeap? bindlessHeap = null)
        {
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
                int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _textures.Count;

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
                    Usage = MemoryUsage.AutoPreferDevice
                };

                Image image;
                Allocation* allocation;
                AllocationInfo allocationInfo;
                Result result = GpuAllocator.Apis.CreateImage(
                    _context.Allocator,
                    &imageInfo,
                    &allocInfo,
                    &image,
                    &allocation,
                    &allocationInfo);

                if (result != Result.Success)
                    throw new VulkanException("Failed to create texture image", result);

                ImageView view;
                try
                {
                    view = CreateImageView(image, format, ImageAspectFlags.ColorBit, mipLevels, arrayLayers);
                }
                catch
                {
                    GpuAllocator.Apis.DestroyImage(_context.Allocator, image, allocation);
                    throw;
                }

                int textureBindlessIndex = AllocateOrRegisterBindlessIndex(bindlessIndex, view, bindlessHeap);

                var textureInfo = new TextureInfo
                {
                    Image = image,
                    Allocation = allocation,
                    View = view,
                    Format = format,
                    Extent = imageInfo.Extent,
                    MipLevels = mipLevels,
                    ArrayLayers = arrayLayers,
                    Generation = AllocateGeneration(index),
                    BindlessIndex = textureBindlessIndex
                };

                if (index == _textures.Count)
                    _textures.Add(textureInfo);
                else
                    _textures[index] = textureInfo;

                return new TextureHandle(index, textureInfo.Generation);
            }
        }

        public TextureHandle LoadTextureFromFile(string path, bool generateMipmaps = true, bool srgb = true)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Texture path cannot be null or empty.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            string cacheKey = CreateTextureCacheKey(fullPath, generateMipmaps, srgb);
            lock (_lock)
            {
                if (_textureCache.TryGetValue(cacheKey, out TextureHandle cachedHandle))
                {
                    TextureInfo cachedTextureInfo = GetTextureInfoLocked(cachedHandle);
                    cachedTextureInfo.ReferenceCount++;
                    return cachedHandle;
                }
            }

            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Texture file was not found: {fullPath}", fullPath);

            ImageResult image = ImageResult.FromMemory(
                File.ReadAllBytes(fullPath),
                ColorComponents.RedGreenBlueAlpha);

            Format format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
            uint width = checked((uint)image.Width);
            uint height = checked((uint)image.Height);
            bool canGenerateMipmaps = generateMipmaps && SupportsLinearBlit(format);
            uint mipLevels = canGenerateMipmaps
                ? CalculateMipLevels(width, height)
                : 1;

            if (generateMipmaps && !canGenerateMipmaps)
            {
                lock (_lock)
                    _mipmapFallbackCount++;

                Console.WriteLine(
                    $"Texture '{fullPath}' uses one mip level because format {format} does not support linear blit mip generation.");
            }

            TextureHandle handle = CreateTexture(width, height, format, mipLevels);
            try
            {
                UploadTextureData(handle, image.Data, width, height, format, generateMipmaps: mipLevels > 1);

                TextureHandle racedHandle = TextureHandle.Invalid;
                lock (_lock)
                {
                    if (!_textureCache.TryGetValue(cacheKey, out racedHandle))
                    {
                        racedHandle = TextureHandle.Invalid;
                        TextureInfo textureInfo = GetTextureInfoLocked(handle);
                        textureInfo.SourcePath = fullPath;
                        _textureCache[cacheKey] = handle;
                    }
                }

                if (racedHandle.IsValid)
                {
                    DestroyTexture(handle);
                    return racedHandle;
                }
            }
            catch
            {
                DestroyTexture(handle);
                throw;
            }

            return handle;
        }

        public TextureHandle LoadOptionalTextureFromFile(
            string? path,
            TextureHandle fallback,
            bool generateMipmaps = true,
            bool srgb = true)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.GetFullPath(path)))
                return fallback;

            return LoadTextureFromFile(path, generateMipmaps, srgb);
        }

        public (ImageView View, Format Format, Extent3D Extent) GetTextureInfo(TextureHandle handle)
        {
            lock (_lock)
            {
                TextureInfo textureInfo = GetTextureInfoLocked(handle);
                return (textureInfo.View, textureInfo.Format, textureInfo.Extent);
            }
        }

        public ImageView GetTextureView(TextureHandle handle)
        {
            return GetTextureInfo(handle).View;
        }

        public int GetBindlessTextureIndex(TextureHandle handle)
        {
            lock (_lock)
                return GetTextureInfoLocked(handle).BindlessIndex;
        }

        public void UploadTextureData(
            TextureHandle handle,
            ReadOnlySpan<byte> data,
            uint width,
            uint height,
            Format format,
            bool generateMipmaps = false)
        {
            if (data.IsEmpty)
                throw new ArgumentException("Texture upload data cannot be empty.", nameof(data));

            lock (_lock)
            {
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

        public void UploadTextureData(
            TextureHandle handle,
            IntPtr data,
            ulong dataSize,
            uint width,
            uint height,
            Format format)
        {
            if (data == IntPtr.Zero)
                throw new ArgumentNullException(nameof(data));
            if (dataSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dataSize), "Texture uploads larger than 2 GB require span-based chunking.");

            UploadTextureData(handle, new ReadOnlySpan<byte>((void*)data, checked((int)dataSize)), width, height, format);
        }

        public void DestroyTexture(TextureHandle handle, Fence retireFence = default)
        {
            TextureInfo? textureInfo;
            lock (_lock)
            {
                if (!TryGetTextureInfoLocked(handle, out textureInfo))
                    return;

                RetireBindlessTextureIndex(textureInfo.BindlessIndex, retireFence);

                RemoveFromCacheLocked(handle);
                textureInfo.Generation++;
                _freeIndices.Push(handle.Index);
            }

            DestroyTextureResources(textureInfo, retireFence);
        }

        public void ReleaseTexture(TextureHandle handle, Fence retireFence = default)
        {
            if (!handle.IsValid)
                return;

            if (handle == _defaultWhiteTexture ||
                handle == _defaultNormalTexture ||
                handle == _defaultBlackTexture)
            {
                return;
            }

            bool shouldDestroy;
            lock (_lock)
            {
                if (!TryGetTextureInfoLocked(handle, out TextureInfo textureInfo))
                    return;

                textureInfo.ReferenceCount--;
                shouldDestroy = textureInfo.ReferenceCount <= 0;
            }

            if (shouldDestroy)
                DestroyTexture(handle, retireFence);
        }

        private TextureHandle CreateSolidTexture(
            string cacheKey,
            ReadOnlySpan<byte> rgba,
            Format format,
            int bindlessIndex,
            BindlessHeap bindlessHeap)
        {
            TextureHandle handle = CreateTexture(
                1,
                1,
                format,
                mipLevels: 1,
                arrayLayers: 1,
                additionalUsage: ImageUsageFlags.None,
                bindlessIndex: bindlessIndex,
                bindlessHeap: bindlessHeap);

            UploadTextureData(handle, rgba, 1, 1, format);

            lock (_lock)
                _textureCache[cacheKey] = handle;

            return handle;
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
            uint arrayLayers = 1)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = 0,
                    LevelCount = mipLevels,
                    BaseArrayLayer = 0,
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

        private int AllocateOrRegisterBindlessIndex(int requestedIndex, ImageView view, BindlessHeap? bindlessHeap)
        {
            BindlessHeap? heap = bindlessHeap ?? _bindlessHeap;
            if (heap == null)
                return UnassignedBindlessIndex;

            if (requestedIndex >= 0)
            {
                heap.RegisterTexture(requestedIndex, view);
                return requestedIndex;
            }

            return heap.AllocateTextureIndex(view);
        }

        private BindlessHeap ResolveBindlessHeap(BindlessHeap? bindlessHeap)
        {
            BindlessHeap? heap = bindlessHeap ?? _bindlessHeap;
            if (heap == null)
                throw new InvalidOperationException("A bindless heap is required to initialize default texture descriptors.");

            return heap;
        }

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

        internal static string CreateTextureCacheKey(string fullPath, bool generateMipmaps, bool srgb)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentException("Texture cache path cannot be null or empty.", nameof(fullPath));

            return $"{Path.GetFullPath(fullPath)}|mips={generateMipmaps}|srgb={srgb}";
        }

        private static ulong CalculateRequiredStagingSize(uint width, uint height, Format format)
        {
            uint bytesPerPixel = format switch
            {
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4,
                Format.R32G32B32A32Sfloat => 16,
                Format.R16G16B16A16Sfloat => 8,
                Format.R8Unorm or Format.R8Srgb => 1,
                _ => throw new NotSupportedException($"Texture format {format} does not have a known staging size.")
            };

            return checked((ulong)width * height * bytesPerPixel);
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
                   textureInfo.Image.Handle != 0 &&
                   textureInfo.View.Handle != 0;
        }

        private uint AllocateGeneration(int textureIndex)
        {
            if (textureIndex >= _textures.Count)
                return checked((uint)(_textures.Count + 1));

            uint generation = _textures[textureIndex].Generation + 1;
            return generation == 0 ? 1 : generation;
        }

        private void RemoveFromCacheLocked(TextureHandle handle)
        {
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

        private void DestroyTextureResources(TextureInfo textureInfo, Fence retireFence)
        {
            Image image = textureInfo.Image;
            Allocation* allocation = textureInfo.Allocation;
            ImageView view = textureInfo.View;

            textureInfo.Image = default;
            textureInfo.Allocation = null;
            textureInfo.View = default;
            textureInfo.BindlessIndex = UnassignedBindlessIndex;

            if (_deleter != null && retireFence.Handle != 0)
            {
                if (view.Handle != 0)
                    _deleter.QueueImageViewDeletion(retireFence, view);
                if (image.Handle != 0)
                    _deleter.QueueImageDeletion(retireFence, image, allocation);
                return;
            }

            if (view.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, view, null);
            if (image.Handle != 0)
                GpuAllocator.Apis.DestroyImage(_context.Allocator, image, allocation);
        }

        private void RetireBindlessTextureIndex(int bindlessIndex, Fence retireFence)
        {
            if (bindlessIndex < BindlessIndex.FirstDynamicTextureIndex || _bindlessHeap == null)
                return;

            if (_deleter != null && retireFence.Handle != 0)
                _deleter.QueueDeletion(retireFence, () => _bindlessHeap.FreeTextureIndex(bindlessIndex));
            else
                _bindlessHeap.FreeTextureIndex(bindlessIndex);
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
                foreach (TextureInfo textureInfo in _textures)
                {
                    if (textureInfo.View.Handle != 0)
                        _context.Api.DestroyImageView(_context.Device, textureInfo.View, null);

                    if (textureInfo.Image.Handle != 0)
                        GpuAllocator.Apis.DestroyImage(
                            _context.Allocator,
                            textureInfo.Image,
                            textureInfo.Allocation);
                }

                _textures.Clear();
                _textureCache.Clear();
                _freeIndices.Clear();
            }

            Console.WriteLine("Texture manager disposed.");
        }

        ~TextureManager()
        {
            Dispose(false);
        }
    }
}
