using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using Vma;
using Buffer = System.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class TextureManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly Dictionary<string, TextureHandle> _textureCache = new Dictionary<string, TextureHandle>();
        private readonly List<TextureInfo> _textures = new List<TextureInfo>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly object _lock = new object();
        private bool _disposed;
        
        private class TextureInfo
        {
            public Image Image;
            public Allocation* Allocation;
            public ImageView View;
            public Format Format;
            public Extent3D Extent;
            public uint MipLevels;
            public uint ArrayLayers;
            public uint Generation;
        }
        
        public TextureManager(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        }
        
        public TextureHandle CreateTexture(
            uint width,
            uint height,
            Format format,
            uint mipLevels = 1,
            uint arrayLayers = 1,
            ImageUsageFlags additionalUsage = ImageUsageFlags.None)
        {
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
                    Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | additionalUsage,
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
                
                ImageView view = CreateImageView(image, format, ImageAspectFlags.ColorBit, mipLevels, arrayLayers);
                
                var textureInfo = new TextureInfo
                {
                    Image = image,
                    Allocation = allocation,
                    View = view,
                    Format = format,
                    Extent = imageInfo.Extent,
                    MipLevels = mipLevels,
                    ArrayLayers = arrayLayers,
                    Generation = (uint)(_textures.Count + 1)
                };
                
                if (index == _textures.Count)
                    _textures.Add(textureInfo);
                else
                    _textures[index] = textureInfo;
                
                return new TextureHandle(index, textureInfo.Generation);
            }
        }
        
        public TextureHandle LoadTextureFromFile(string path, bool generateMipmaps = true)
        {
            // Check cache
            lock (_lock)
            {
                if (_textureCache.TryGetValue(path, out var aHandle))
                    return aHandle;
            }
            
            // TODO: Implement actual texture loading from file
            // For now, create a dummy 1x1 white texture
            var handle = CreateTexture(1, 1, Format.R8G8B8A8Unorm, 1, 1);
            
            lock (_lock)
            {
                _textureCache[path] = handle;
            }
            
            return handle;
        }
        
        public (ImageView View, Format Format, Extent3D Extent) GetTextureInfo(TextureHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _textures.Count)
                    throw new InvalidOperationException("Invalid texture handle");
                
                var textureInfo = _textures[handle.Index];
                if (textureInfo.Generation != handle.Generation)
                    throw new InvalidOperationException("Texture handle generation mismatch");
                
                return (textureInfo.View, textureInfo.Format, textureInfo.Extent);
            }
        }
        
        public ImageView GetTextureView(TextureHandle handle)
        {
            return GetTextureInfo(handle).View;
        }
        
        public void UploadTextureData(
            TextureHandle handle,
            IntPtr data,
            ulong dataSize,
            uint width,
            uint height,
            Format format)
        {
            lock (_lock)
            {
                var textureInfo = _textures[handle.Index];
                
                // Create staging buffer
                ulong requiredSize = CalculateRequiredStagingSize(width, height, format);
                var stagingHandle = _bufferManager.CreateStagingBuffer(requiredSize);
                var stagingBuffer = _bufferManager.GetBuffer(stagingHandle);
                
                // Map and copy data
                void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
                Buffer.MemoryCopy((byte*)data, mappedData, dataSize, dataSize);
                
                // Flush staging buffer
                _bufferManager.FlushBuffer(stagingHandle, 0, requiredSize);
                
                // Use single-time commands for the copy
                var ctx = _context.BeginSingleTimeCommands();
                var cmd = ctx.CommandBuffer;
                
                // Transition image to transfer dst
                var barrier1 = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.None,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = textureInfo.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };
                
                _context.Api.CmdPipelineBarrier(
                    cmd,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    null,
                    null,
                    1,
                    &barrier1);
                
                // Copy buffer to image
                var region = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = width,
                    BufferImageHeight = height,
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
                    cmd,
                    stagingBuffer,
                    textureInfo.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &region);
                
                // Transition image to shader read
                var barrier2 = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = textureInfo.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };
                
                _context.Api.CmdPipelineBarrier(
                    cmd,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    null,
                    null,
                    1,
                    &barrier2);
                
                _context.EndSingleTimeCommands(ctx);
                
                // Clean up staging buffer
                _bufferManager.DestroyBuffer(stagingHandle);
            }
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
                _context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create image view", result);
            
            return view;
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
                _ => 4
            };
            
            return (ulong)width * height * bytesPerPixel;
        }
        
        public void DestroyTexture(TextureHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _textures.Count)
                    return;
                
                var textureInfo = _textures[handle.Index];
                if (textureInfo.Generation != handle.Generation)
                    return;
                
                GpuAllocator.Apis.DestroyImage(
                    _context.Allocator,
                    textureInfo.Image,
                    textureInfo.Allocation);
                
                if (textureInfo.View.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, textureInfo.View, null);
                
                textureInfo.Generation++;
                _freeIndices.Push(handle.Index);
            }
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                foreach (var textureInfo in _textures)
                {
                    if (textureInfo.Image.Handle != 0)
                        GpuAllocator.Apis.DestroyImage(
                            _context.Allocator,
                            textureInfo.Image,
                            textureInfo.Allocation);
                    
                    if (textureInfo.View.Handle != 0)
                        _context.Api.DestroyImageView(_context.Device, textureInfo.View, null);
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
