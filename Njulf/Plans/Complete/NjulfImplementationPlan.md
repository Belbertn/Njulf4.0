# Njulf Framework Implementation Plan

> **Based on:** NjulfFrameworkRenderingOverview.txt + VulkanToSilkTranslation.md  
> **Target:** Clean MonoGame-style API over Vulkan 1.3+ with meshlet-first rendering  
> **Backend:** Silk.NET.Vulkan + GPUMemoryAllocator.VMA  

---

## Legend

- **File**: `Project/Path/To/File.cs` - file to create
- **Silk.NET**: Vulkan calls translated per VulkanToSilkTranslation.md
- **Priority**: High (>), Medium (=), Low (<)
- **Test**: NjulfHelloGame should demonstrate final API

---

# PHASE 0: FOUNDATION (Already Set Up)

- [x] Solution structure with 7 projects
- [x] NuGet packages installed (Silk.NET.Vulkan, VMA, Silk.NET.Assimp, Silk.NET.Input, Silk.NET.Windowing)
- [x] Project references configured

---

# PHASE 1: CORE INFRASTRUCTURE >>

**Goal:** Bootstrap Vulkan with device selection, queues, allocator, swapchain.

## Step 1.1: Vulkan Context (Njulf.Rendering/Core/VulkanContext.cs)

**Responsibilities:**
- Instance creation with validation layers (dev only)
- Physical device selection (first with Vulkan 1.3 + mesh shader support)
- Logical device with required features/extensions
- Queue family discovery (graphics required, transfer optional)
- VMA allocator creation with `BufferDeviceAddressBit`
- Extension handlers (KHR_surface, KHR_swapchain, EXT_mesh_shader, KHR_dynamic_rendering, KHR_synchronization2)

**Silk.NET Translation:**
```csharp
// Instance creation
var appInfo = new ApplicationInfo
{
    SType = StructureType.ApplicationInfo,
    PApplicationName = SilkMarshal.StringToPtr("Njulf"),
    ApplicationVersion = Vk.MakeApiVersion(0, 1, 0, 0),
    PEngineName = SilkMarshal.StringToPtr("Njulf"),
    EngineVersion = Vk.MakeApiVersion(0, 1, 0, 0),
    ApiVersion = Vk.ApiVersion13
};

var createInfo = new InstanceCreateInfo
{
    SType = StructureType.InstanceCreateInfo,
    PApplicationInfo = &appInfo,
    // Enable validation layers in debug
};

var vk = Vk.GetApi();
Result result = vk.CreateInstance(&createInfo, null, out Instance instance);

// Device selection with mesh shader feature check
var features2 = new PhysicalDeviceFeatures2
{
    SType = StructureType.PhysicalDeviceFeatures2
};
var meshShaderFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
{
    SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt
};
features2.PNext = &meshShaderFeatures;

vk.GetPhysicalDeviceFeatures2(physicalDevice, &features2);
if (!meshShaderFeatures.MeshShader)
    throw new Exception("Mesh shader feature not supported");

// Device creation with chained features
var deviceFeatures2 = new PhysicalDeviceFeatures2
{
    SType = StructureType.PhysicalDeviceFeatures2,
    Features = new PhysicalDeviceFeatures
    {
        // Required features
    }
};
var meshShaderDeviceFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
{
    SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
    MeshShader = true,
    TaskShader = true
};
var dynRenderingFeatures = new PhysicalDeviceDynamicRenderingFeatures
{
    SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
    DynamicRendering = true
};
var sync2Features = new PhysicalDeviceSynchronization2Features
{
    SType = StructureType.PhysicalDeviceSynchronization2Features,
    Synchronization2 = true
};

// Chain them
meshShaderDeviceFeatures.PNext = &sync2Features;
sync2Features.PNext = &dynRenderingFeatures;
dynRenderingFeatures.PNext = deviceFeatures2.Features.PNext;
deviceFeatures2.PNext = &meshShaderDeviceFeatures;

var deviceCreateInfo = new DeviceCreateInfo
{
    SType = StructureType.DeviceCreateInfo,
    QueueCreateInfoCount = 1,
    PQueueCreateInfos = &queueCreateInfo,
    PEnabledFeatures = null, // Use chained features2
    PNext = &deviceFeatures2
};

vk.CreateDevice(physicalDevice, &deviceCreateInfo, null, out Device device);

// Get extension handlers
vk.TryGetInstanceExtension(instance, out KhrSurface khrSurface);
vk.TryGetDeviceExtension(instance, device, out KhrSwapchain khrSwapchain);
vk.TryGetDeviceExtension(instance, device, out ExtMeshShader extMeshShader);
vk.TryGetDeviceExtension(instance, device, out KhrDynamicRendering khrDynamicRendering);
vk.TryGetDeviceExtension(instance, device, out KhrSynchronization2 khrSync2);

// Create VMA allocator
var allocatorCreateInfo = new AllocatorCreateInfo
{
    VulkanApiVersion = Vk.ApiVersion13,
    PhysicalDevice = physicalDevice,
    Device = device,
    Instance = instance,
    Flags = AllocatorCreateFlags.BitBufferDeviceAddress
};
Apis.CreateAllocator(&allocatorCreateInfo, out Allocator allocator);
```

**Files:**
- `Njulf.Rendering/Core/VulkanContext.cs` - Main context class
- `Njulf.Rendering/Core/VulkanContextOptions.cs` - Configuration options

---

## Step 1.2: Swapchain Manager (Njulf.Rendering/Core/SwapchainManager.cs)

**Responsibilities:**
- Surface creation via Silk.NET.Windowing
- Swapchain creation (triple buffering preferred, double fallback)
- Depth buffer creation (D32_SFLOAT preferred)
- Image layout tracking
- Swapchain recreation on resize
- Device idle wait before swapchain operations

**Silk.NET Translation:**
```csharp
// Surface creation (window is IWindow from Silk.NET.Windowing)
SurfaceKHR surface = window.VkSurface!
    .Create<AllocationCallbacks>(instance.ToHandle(), null)
    .ToSurface();

// Swapchain creation
var surfaceFormat = ChooseSwapSurfaceFormat(physicalDevice, surface);
var presentMode = ChooseSwapPresentMode(physicalDevice, surface);
var extent = ChooseSwapExtent(window, surfaceCapabilities);

var swapchainCreateInfo = new SwapchainCreateInfoKHR
{
    SType = StructureType.SwapchainCreateInfoKhr,
    Surface = surface,
    MinImageCount = 3, // Triple buffering
    ImageFormat = surfaceFormat.Format,
    ImageColorSpace = surfaceFormat.ColorSpace,
    ImageExtent = extent,
    ImageArrayLayers = 1,
    ImageUsage = ImageUsageFlags.ColorAttachmentBit,
    ImageSharingMode = SharingMode.Exclusive,
    PreTransform = surfaceCapabilities.CurrentTransform,
    CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
    PresentMode = presentMode,
    Clipped = true
};

khrSwapchain.CreateSwapchain(device, &swapchainCreateInfo, null, out SwapchainKHR swapchain);

// Get swapchain images
uint imageCount;
khrSwapchain.GetSwapchainImagesKHR(device, swapchain, &imageCount, null);
var images = new Image[imageCount];
fixed (Image* imagesPtr = images)
    khrSwapchain.GetSwapchainImagesKHR(device, swapchain, &imageCount, imagesPtr);

// Create image views
for (int i = 0; i < imageCount; i++)
{
    var viewCreateInfo = new ImageViewCreateInfo
    {
        SType = StructureType.ImageViewCreateInfo,
        Image = images[i],
        ViewType = ImageViewType.Type2D,
        Format = surfaceFormat.Format,
        Components = new ComponentMapping(),
        SubresourceRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        }
    };
    vk.CreateImageView(device, &viewCreateInfo, null, out ImageView view);
}

// Depth buffer creation with VMA
var depthFormat = ImageFormat.D32Sfloat; // or choose from supported list
var depthImageInfo = new ImageCreateInfo
{
    SType = StructureType.ImageCreateInfo,
    ImageType = ImageType.Type2D,
    Format = depthFormat,
    Extent = new Extent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
    MipLevels = 1,
    ArrayLayers = 1,
    Samples = SampleCountFlags.Monokhr,
    Tiling = ImageTiling.Optimal,
    Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
    InitialLayout = ImageLayout.Undefined
};

AllocationCreateInfo depthAllocInfo = new AllocationCreateInfo
{
    Usage = MemoryUsage.AutoPreferDevice
};

Apis.CreateImage(allocator, &depthImageInfo, &depthAllocInfo, &depthImage, &depthAllocation, &depthAllocationInfo);

// Create depth image view
var depthViewInfo = new ImageViewCreateInfo
{
    SType = StructureType.ImageViewCreateInfo,
    Image = depthImage,
    ViewType = ImageViewType.Type2D,
    Format = depthFormat,
    SubresourceRange = new ImageSubresourceRange
    {
        AspectMask = ImageAspectFlags.DepthBit,
        BaseMipLevel = 0,
        LevelCount = 1,
        BaseArrayLayer = 0,
        LayerCount = 1
    }
};
vk.CreateImageView(device, &depthViewInfo, null, out ImageView depthView);
```

**Files:**
- `Njulf.Rendering/Core/SwapchainManager.cs`

---

## Step 1.3: Synchronization Manager (Njulf.Rendering/Core/SynchronizationManager.cs)

**Responsibilities:**
- Frame synchronization primitives (semaphores, fences)
- Image-available semaphore (per swapchain image)
- Render-finished semaphore (per swapchain image)
- Transfer-finished semaphore
- In-flight fence (per frame, framesInFlight=2)
- Transfer fence

**Silk.NET Translation:**
```csharp
const int FramesInFlight = 2;

// Per-frame semaphores and fences
var imageAvailableSemaphores = new Semaphore[FramesInFlight];
var renderFinishedSemaphores = new Semaphore[FramesInFlight];
var inFlightFences = new Fence[FramesInFlight];

for (int i = 0; i < FramesInFlight; i++)
{
    var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
    vk.CreateSemaphore(device, &semInfo, null, out imageAvailableSemaphores[i]);
    vk.CreateSemaphore(device, &semInfo, null, out renderFinishedSemaphores[i]);
    
    var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
    vk.CreateFence(device, &fenceInfo, null, out inFlightFences[i]);
}

// Transfer semaphore and fence
var transferSemaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
vk.CreateSemaphore(device, &transferSemaphoreInfo, null, out Semaphore transferFinishedSemaphore);

var transferFenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
vk.CreateFence(device, &transferFenceInfo, null, out Fence transferFence);
```

**Files:**
- `Njulf.Rendering/Core/SynchronizationManager.cs`

---

## Step 1.4: Command Buffer Manager (Njulf.Rendering/Core/CommandBufferManager.cs)

**Responsibilities:**
- Graphics command pool and primary command buffers (per frame)
- Transfer command pool and primary command buffer
- Single-time command buffer helper
- Command buffer reset/recording lifecycle

**Silk.NET Translation:**
```csharp
// Graphics command pool
var graphicsPoolInfo = new CommandPoolCreateInfo
{
    SType = StructureType.CommandPoolCreateInfo,
    QueueFamilyIndex = graphicsQueueFamilyIndex,
    Flags = CommandPoolCreateFlags.ResetCommandBufferBit
};
vk.CreateCommandPool(device, &graphicsPoolInfo, null, out CommandPool graphicsCommandPool);

// Per-frame graphics command buffers
var graphicsCmdBufAllocateInfo = new CommandBufferAllocateInfo
{
    SType = StructureType.CommandBufferAllocateInfo,
    CommandPool = graphicsCommandPool,
    Level = CommandBufferLevel.Primary,
    CommandBufferCount = FramesInFlight
};
var graphicsCommandBuffers = new CommandBuffer[FramesInFlight];
fixed (CommandBuffer* ptr = graphicsCommandBuffers)
    vk.AllocateCommandBuffers(device, &graphicsCmdBufAllocateInfo, ptr);

// Transfer command pool (if separate queue family)
if (transferQueueFamilyIndex != graphicsQueueFamilyIndex)
{
    var transferPoolInfo = new CommandPoolCreateInfo
    {
        SType = StructureType.CommandPoolCreateInfo,
        QueueFamilyIndex = transferQueueFamilyIndex,
        Flags = CommandPoolCreateFlags.ResetCommandBufferBit
    };
    vk.CreateCommandPool(device, &transferPoolInfo, null, out CommandPool transferCommandPool);
    
    var transferCmdBufAllocateInfo = new CommandBufferAllocateInfo
    {
        SType = StructureType.CommandBufferAllocateInfo,
        CommandPool = transferCommandPool,
        Level = CommandBufferLevel.Primary,
        CommandBufferCount = 1
    };
    vk.AllocateCommandBuffers(device, &transferCmdBufAllocateInfo, &transferCommandBuffer);
}

// Single-time command helper
public CommandBuffer BeginSingleTimeCommands()
{
    var allocateInfo = new CommandBufferAllocateInfo
    {
        SType = StructureType.CommandBufferAllocateInfo,
        CommandPool = graphicsCommandPool,
        Level = CommandBufferLevel.Primary,
        CommandBufferCount = 1
    };
    vk.AllocateCommandBuffers(device, &allocateInfo, &cmd);
    
    var beginInfo = new CommandBufferBeginInfo
    {
        SType = StructureType.CommandBufferBeginInfo,
        Flags = CommandBufferUsageFlags.OneTimeSubmitBit
    };
    vk.BeginCommandBuffer(cmd, &beginInfo);
    return cmd;
}

public void EndSingleTimeCommands(CommandBuffer cmd)
{
    vk.EndCommandBuffer(cmd);
    
    var submitInfo = new SubmitInfo
    {
        SType = StructureType.SubmitInfo,
        CommandBufferCount = 1,
        PCommandBuffers = &cmd
    };
    vk.QueueSubmit(graphicsQueue, 1, &submitInfo, default);
    vk.QueueWaitIdle(graphicsQueue);
    vk.FreeCommandBuffers(device, graphicsCommandPool, 1, &cmd);
}
```

**Files:**
- `Njulf.Rendering/Core/CommandBufferManager.cs`

---

# PHASE 2: RESOURCE MANAGEMENT >>

**Goal:** Buffer, texture, mesh, and meshlet management with bindless support.

## Step 2.1: Buffer Manager (Njulf.Rendering/Memory/BufferManager.cs)

**Responsibilities:**
- Buffer allocation via VMA
- Buffer handle tracking with generation numbers (BufferHandle = index + generation)
- Mapped persistent staging buffers
- Buffer resizing with copy + old buffer retirement
- Bindless heap registration

**Key Design:**
```csharp
public struct BufferHandle
{
    public int Index;
    public uint Generation;
}

public class BufferManager : IDisposable
{
    private readonly List<BufferInfo> _buffers = new();
    private readonly Stack<int> _freeIndices = new();
    
    public BufferHandle CreateBuffer(ulong size, BufferUsageFlags usage, MemoryUsage memoryUsage)
    {
        int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _buffers.Count;
        
        var bufferInfo = new BufferInfo
        {
            Size = size,
            Usage = usage,
            MemoryUsage = memoryUsage
        };
        
        CreateVmaBuffer(size, usage, memoryUsage, out bufferInfo.Buffer, out bufferInfo.Allocation);
        
        if (index == _buffers.Count)
            _buffers.Add(bufferInfo);
        else
            _buffers[index] = bufferInfo;
        
        bufferInfo.Generation++;
        return new BufferHandle { Index = index, Generation = bufferInfo.Generation };
    }
    
    public Buffer GetBuffer(BufferHandle handle)
    {
        if (handle.Index >= _buffers.Count || _buffers[handle.Index].Generation != handle.Generation)
            throw new Exception("Invalid buffer handle");
        return _buffers[handle.Index].Buffer;
    }
    
    private void CreateVmaBuffer(ulong size, BufferUsageFlags usage, MemoryUsage memoryUsage, out Buffer buffer, out Allocation allocation)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        
        var allocInfo = new AllocationCreateInfo
        {
            Usage = memoryUsage
        };
        
        if (memoryUsage == MemoryUsage.AutoPreferHost)
            allocInfo.Flags = AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessSequentialWriteBit;
        
        Apis.CreateBuffer(allocator, &bufferInfo, &allocInfo, &buffer, &allocation, out _);
    }
}
```

**Files:**
- `Njulf.Rendering/Memory/BufferManager.cs`
- `Njulf.Rendering/Memory/BufferHandle.cs`

---

## Step 2.2: Staging Ring Buffer (Njulf.Rendering/Memory/StagingRing.cs)

**Responsibilities:**
- Per-frame staging buffer cycling (FramesInFlight=2)
- Ring buffer with configurable size (default: 64MB per frame)
- Upload operations with automatic offset management
- Flush/invalidate operations for non-coherent memory

**Design:**
```csharp
public class StagingRing : IDisposable
{
    private readonly Buffer[] _stagingBuffers;
    private readonly Allocation[] _stagingAllocations;
    private readonly AllocationInfo[] _allocationInfos;
    private readonly ulong[] _offsets;
    private int _currentFrame = 0;
    private ulong[] _currentOffsets;
    
    public StagingRing(Allocator allocator, ulong sizePerBuffer, int bufferCount)
    {
        _stagingBuffers = new Buffer[bufferCount];
        _stagingAllocations = new Allocation[bufferCount];
        _allocationInfos = new AllocationInfo[bufferCount];
        _currentOffsets = new ulong[bufferCount];
        
        for (int i = 0; i < bufferCount; i++)
        {
            CreateStagingBuffer(allocator, sizePerBuffer, i);
        }
    }
    
    public (Buffer Buffer, ulong Offset) Allocate(ulong size)
    {
        int frameIndex = _currentFrame % _stagingBuffers.Length;
        ulong offset = _currentOffsets[frameIndex];
        
        // Align offset
        ulong alignment = 256; // or query from device
        offset = (offset + alignment - 1) & ~(alignment - 1);
        
        if (offset + size > _allocationInfos[frameIndex].Size)
            throw new Exception("Staging buffer overflow");
        
        _currentOffsets[frameIndex] = offset + size;
        return (_stagingBuffers[frameIndex], offset);
    }
    
    public void AdvanceFrame()
    {
        _currentFrame++;
        // Reset offsets for the frame we're cycling back to
        int resetIndex = _currentFrame % _stagingBuffers.Length;
        _currentOffsets[resetIndex] = 0;
    }
    
    public void Flush(ulong offset, ulong size)
    {
        int frameIndex = (_currentFrame - 1) % _stagingBuffers.Length;
        Apis.FlushAllocation(allocator, _stagingAllocations[frameIndex], offset, size);
    }
}
```

**Files:**
- `Njulf.Rendering/Memory/StagingRing.cs`

---

## Step 2.3: Fence-Based Deleter (Njulf.Rendering/Memory/FenceBasedDeleter.cs)

**Responsibilities:**
- Queue resources for deletion when fence signals
- Track per-fence deletion lists
- Automatic cleanup on fence wait

**Design:**
```csharp
public class FenceBasedDeleter : IDisposable
{
    private readonly Dictionary<Fence, List<Action>> _pendingDeletions = new();
    private readonly VulkanContext _context;
    
    public void QueueDeletion(Fence fence, Action deletionAction)
    {
        lock (_pendingDeletions)
        {
            if (!_pendingDeletions.TryGetValue(fence, out var list))
            {
                list = new List<Action>();
                _pendingDeletions[fence] = list;
            }
            list.Add(deletionAction);
        }
    }
    
    public void QueueBufferDeletion(Fence fence, Buffer buffer, Allocation allocation)
    {
        QueueDeletion(fence, () => 
        {
            Apis.DestroyBuffer(allocator, buffer, allocation);
        });
    }
    
    public void ProcessCompletedFrame(Fence fence)
    {
        lock (_pendingDeletions)
        {
            if (_pendingDeletions.TryGetValue(fence, out var deletions))
            {
                foreach (var action in deletions)
                    action();
                deletions.Clear();
            }
        }
    }
    
    public void Cleanup()
    {
        foreach (var kvp in _pendingDeletions)
        {
            foreach (var action in kvp.Value)
                action();
        }
        _pendingDeletions.Clear();
    }
}
```

**Files:**
- `Njulf.Rendering/Memory/FenceBasedDeleter.cs`

---

## Step 2.4: Texture Manager (Njulf.Rendering/Resources/TextureManager.cs)

**Responsibilities:**
- Texture allocation via VMA
- Texture upload with staging
- Image view creation
- Layout transitions
- Bindless heap index assignment
- Queue family ownership transfer

**Files:**
- `Njulf.Rendering/Resources/TextureManager.cs`
- `Njulf.Rendering/Resources/TextureHandle.cs`

---

## Step 2.5: Mesh Manager (Njulf.Rendering/Resources/MeshManager.cs)

**Responsibilities:**
- Consolidated vertex/index buffer management
- Meshlet generation (CPU, at load time)
- Meshlet descriptor buffer
- Local vertex/triangle index buffers
- Bindless heap registration for all mesh buffers

**Files:**
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Rendering/Resources/MeshHandle.cs`

---

## Step 2.6: Meshlet Builder (Njulf.Assets/MeshletBuilder.cs)

**Responsibilities:**
- Convert triangulated mesh to meshlets
- Configurable max vertices (64) and triangles (126) per meshlet
- Bounding sphere computation per meshlet
- Local index buffer generation

**Algorithm** (from APPENDIX C):
1. Validate triangulation
2. Split large meshes into chunks (1024 vertices max)
3. Greedy meshlet generation per chunk
4. Build local vertex and triangle index buffers
5. Compute bounding spheres

**Files:**
- `Njulf.Assets/MeshletBuilder.cs`
- `Njulf.Assets/Meshlet.cs` - Data structures

---

## Step 2.7: Light Manager (Njulf.Rendering/Resources/LightManager.cs)

**Responsibilities:**
- Fixed-capacity GPU light buffer (default: 1024)
- Dynamic light count tracking
- Light buffer updates via staging
- Bindless heap index management

**Files:**
- `Njulf.Rendering/Resources/LightManager.cs`

---

# PHASE 3: DESCRIPTOR SYSTEM >>

**Goal:** Bindless resource access with fixed compile-time indices.

## Step 3.1: Bindless Index Table (Njulf.Rendering/Descriptors/BindlessIndexTable.cs)

**Responsibilities:**
- Define compile-time indices matching shader bindings
- MUST match shader bindings exactly (critical invariant)
- Fixed slots for static buffers (0-14)
- Dynamic slots for textures via free-list

**Critical - MUST match shaders:**
```csharp
public static class BindlessIndex
{
    // Storage Buffer Heap Indices
    public const int ObjectDataBuffer = 0;
    public const int MaterialDataBuffer = 1;
    public const int SceneMeshMetadataBuffer = 2;
    public const int VertexBuffer = 3;
    public const int IndexBuffer = 4;
    public const int MeshletBuffer = 5;
    public const int MeshletVertexIndexBuffer = 6;
    public const int MeshletTriangleIndexBuffer = 7;
    public const int InstanceBufferBase = 8;
    public const int InstanceBufferFrame1 = 9;
    public const int MeshletDrawBufferBase = 10;
    public const int MeshletDrawBufferFrame1 = 11;
    public const int LightBuffer = 12;
    public const int TiledLightHeaderBuffer = 13;
    public const int TiledLightIndicesBuffer = 14;
    
    // Texture Heap Indices (dynamic allocation)
    public const int FirstTextureIndex = 0;
    public const int MaxTextures = 65536;
}
```

**Files:**
- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`

---

## Step 3.2: Bindless Heap (Njulf.Rendering/Descriptors/BindlessHeap.cs)

**Responsibilities:**
- Two large heaps: storage buffer + combined image sampler
- Single binding, update-after-bind, variable descriptor count
- Partially-bound support
- Very large fixed capacity (65536 each)

**Silk.NET Translation:**
```csharp
// Storage buffer heap (for SSBOs)
var storageHeapCreateInfo = new DescriptorPoolCreateInfo
{
    SType = StructureType.DescriptorPoolCreateInfo,
    PoolSizeCount = 1,
    PPoolSizes = &new DescriptorPoolSize
    {
        Type = DescriptorType.StorageBuffer,
        DescriptorCount = 65536
    },
    MaxSets = 1,
    Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
};
vk.CreateDescriptorPool(device, &storageHeapCreateInfo, null, out DescriptorPool storageBufferPool);

// Combined image sampler heap (for textures)
var textureHeapCreateInfo = new DescriptorPoolCreateInfo
{
    SType = StructureType.DescriptorPoolCreateInfo,
    PoolSizeCount = 1,
    PPoolSizes = &new DescriptorPoolSize
    {
        Type = DescriptorType.CombinedImageSampler,
        DescriptorCount = 65536
    },
    MaxSets = 1,
    Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
};
vk.CreateDescriptorPool(device, &textureHeapCreateInfo, null, out DescriptorPool textureSamplerPool);

// Create descriptor sets from pools
var storageSetLayout = ...; // From DescriptorSetLayouts
var textureSetLayout = ...;

var allocInfo = new DescriptorSetAllocateInfo
{
    SType = StructureType.DescriptorSetAllocateInfo,
    DescriptorPool = storageBufferPool,
    DescriptorSetCount = 1,
    PSetLayouts = &storageSetLayout
};
vk.AllocateDescriptorSets(device, &allocInfo, out DescriptorSet storageBufferSet);

// Update with all buffers at their fixed indices
var writes = new WriteDescriptorSet[15]; // For indices 0-14
for (int i = 0; i < 15; i++)
{
    writes[i] = new WriteDescriptorSet
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = storageBufferSet,
        DstBinding = 0, // Single binding
        DstArrayElement = (uint)i,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = &new DescriptorBufferInfo
        {
            Buffer = buffers[i],
            Offset = 0,
            Range = Vk.WholeSize
        }
    };
}
vk.UpdateDescriptorSets(device, (uint)writes.Length, writes, 0, null);
```

**Files:**
- `Njulf.Rendering/Descriptors/BindlessHeap.cs`
- `Njulf.Rendering/Descriptors/DescriptorSetLayouts.cs`

---

## Step 3.3: Default Sampler (Njulf.Rendering/Descriptors/SamplerManager.cs)

**Responsibilities:**
- Default sampler creation (linear filtering, repeat wrap)
- Optional additional samplers (nearest, clamp, etc.)

**Files:**
- `Njulf.Rendering/Descriptors/SamplerManager.cs`

---

# PHASE 4: PIPELINE & RENDERING >>

**Goal:** Mesh-shader pipeline with render graph: DepthPrePass -> TiledLightCulling -> ForwardPlus

## Step 4.1: GPU Structs (Njulf.Rendering/Data/GPUStructs.cs)

**Responsibilities:**
- GPU-layout structs MUST match shader definitions
- Packed for 4-byte alignment
- All fields public, no padding (use explicit layout if needed)

**Example:**
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct GPUMeshlet
{
    public Vector3 BoundingSphereCenter;
    public float BoundingSphereRadius;
    public uint VertexOffset;
    public uint VertexCount;
    public uint IndexOffset;
    public uint IndexCount;
    public uint LocalVertexOffset;
    public uint LocalVertexCount;
    public uint LocalTriangleOffset;
    public uint LocalTriangleCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUObjectData
{
    public Matrix4x4 WorldMatrix;
    public Matrix4x4 WorldMatrixInverseTranspose;
    public int MeshIndex;
    public int MaterialIndex;
    // 8-byte padding for 16-byte alignment
}
```

**Files:**
- `Njulf.Rendering/Data/GPUStructs.cs`

---

## Step 4.2: Barrier Builder (Njulf.Rendering/Utilities/BarrierBuilder.cs)

**Responsibilities:**
- Synchronization2 barrier helper
- Explicit layout transitions
- Queue family ownership transfer

**Files:**
- `Njulf.Rendering/Utilities/BarrierBuilder.cs`

---

## Step 4.3: Render Graph (Njulf.Rendering/Pipeline/RenderGraph.cs)

**Responsibilities:**
- Pass dependency management
- Ordered pass execution
- Automatic barrier insertion at pass boundaries

**Design:**
```csharp
public abstract class RenderPassBase : IDisposable
{
    public abstract string Name { get; }
    public abstract void Initialize(VulkanContext context, SwapchainManager swapchain, BindlessHeap bindlessHeap);
    public abstract void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData);
    public abstract void Cleanup();
    public virtual IEnumerable<Barrier> GetBarriers(CommandBuffer cmd, int frameIndex) => Enumerable.Empty<Barrier>();
}

public class RenderGraph : IDisposable
{
    private readonly List<RenderPassBase> _passes = new();
    
    public void AddPass(RenderPassBase pass)
    {
        _passes.Add(pass);
    }
    
    public void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        for (int i = 0; i < _passes.Count; i++)
        {
            var barriers = _passes[i].GetBarriers(cmd, frameIndex);
            InsertBarriers(cmd, barriers);
            _passes[i].Execute(cmd, frameIndex, sceneData);
        }
    }
}
```

**Files:**
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf.Rendering/Pipeline/RenderPassBase.cs`

---

## Step 4.4: Pipeline Objects (Njulf.Rendering/Pipeline/PipelineObjects/)

**Files:**
- `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs` - Task + Mesh + Fragment pipeline
- `Njulf.Rendering/Pipeline/PipelineObjects/ComputePipeline.cs` - For light culling

---

## Step 4.5: Depth Pre-Pass (Njulf.Rendering/Pipeline/DepthPrePass.cs)

**Responsibilities:**
- Mesh-shader dispatch for all visible meshlets
- Reverse-Z (depth cleared to 0.0, greater comparison)
- Output: hi-Z depth buffer

**Files:**
- `Njulf.Rendering/Pipeline/DepthPrePass.cs`

---

## Step 4.6: Tiled Light Culling Pass (Njulf.Rendering/Pipeline/TiledLightCullingPass.cs)

**Responsibilities:**
- Compute shader assigning lights to screen tiles
- Input: light buffer, depth buffer
- Output: per-tile light lists (headers + indices)
- Workgroup per tile

**Files:**
- `Njulf.Rendering/Pipeline/TiledLightCullingPass.cs`

---

## Step 4.7: Forward+ Pass (Njulf.Rendering/Pipeline/ForwardPlusPass.cs)

**Responsibilities:**
- Mesh-shader dispatch for visible meshlets
- Input: meshlet data, material data, textures, light index buffers
- PBR shading with image-based lighting approximation
- Per-meshlet: read per-tile light lists

**Files:**
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`

---

## Step 4.8: Scene Data Builder (Njulf.Rendering/Data/SceneDataBuilder.cs)

**Responsibilities:**
- CPU frustum culling of visible objects
- Generate per-meshlet draw commands
- Deduplicate materials and meshes
- Upload scene data via staging ring
- Validate offsets against buffer sizes

**Files:**
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`

---

# PHASE 5: CONTENT PIPELINE >>

**Goal:** Asset loading and meshlet integration.

## Step 5.1: Content Manager (Njulf.Assets/ContentManager.cs)

**Responsibilities:**
- Generic typed `Load<T>(path)` entry point
- Asset caching
- Model import via Assimp
- Texture loading

**Files:**
- `Njulf.Assets/ContentManager.cs`

---

## Step 5.2: Model Importer (Njulf.Assets/ModelImporter.cs)

**Responsibilities:**
- Assimp wrapper for glTF/OBJ/FBX
- Validate triangulation
- Compute bounding boxes
- Optional winding flip
- Produce framework mesh structure

**Silk.NET Translation (Assimp):**
```csharp
using Silk.NET.Assimp;

var assimp = Assimp.GetApi();
var scene = assimp.ImportFile(path, (uint)(PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.FlipUVs));

if (scene == null || scene->MFlags == SceneFlags.Incomplete || scene->MRootNode == null)
    throw new Exception("Failed to load model");

// Process meshes
for (uint i = 0; i < scene->MNumMeshes; i++)
{
    var mesh = scene->PMeshes[i];
    // Extract vertices, normals, tangents, UVs
    // Validate: mesh->MNumVertices > 0, mesh->MNumFaces > 0
    // Validate triangulation: each face has exactly 3 indices
}
```

**Files:**
- `Njulf.Assets/ModelImporter.cs`
- `Njulf.Assets/ImporterOptions.cs`

---

## Step 5.3: Mesh Integration (Njulf.Assets -> Njulf.Rendering)

**Responsibilities:**
- ModelImporter produces standard meshes
- MeshManager converts meshes to meshlets at registration
- Meshlet data uploaded to consolidated GPU buffers
- Bindless heap indices assigned
- Material deduplication

**Files:**
- Update `Njulf.Rendering/Resources/MeshManager.cs` to integrate with `Njulf.Assets`

---

# PHASE 6: HIGH-LEVEL API >>

**Goal:** Clean MonoGame-style API for end users.

## Step 6.1: Math Library (Njulf.Core/Math/)

**Files:**
- `Njulf.Core/Math/Vector2.cs`, `Vector3.cs`, `Vector4.cs`
- `Njulf.Core/Math/Matrix4x4.cs`
- `Njulf.Core/Math/Quaternion.cs`
- `Njulf.Core/Math/BoundingBox.cs`, `BoundingSphere.cs`
- `Njulf.Core/Math/Ray.cs`
- `Njulf.Core/Math/Color.cs`

---

## Step 6.2: Enums (Njulf.Core/Enums/)

**Files:**
- `Njulf.Core/Enums/BufferUsage.cs`
- `Njulf.Core/Enums/MemoryUsage.cs`
- `Njulf.Core/Enums/PrimitiveType.cs`
- `Njulf.Core/Enums/CullMode.cs`
- `Njulf.Core/Enums/BlendState.cs`
- `Njulf.Core/Enums/DepthStencilState.cs`

---

## Step 6.3: Interfaces (Njulf.Core/Interfaces/)

**Files:**
- `Njulf.Core/Interfaces/IRenderer.cs`
- `Njulf.Core/Interfaces/ICamera.cs`
- `Njulf.Core/Interfaces/IContentManager.cs`
- `Njulf.Core/Interfaces/IInputManager.cs`
- `Njulf.Core/Interfaces/IRenderable.cs`
- `Njulf.Core/Interfaces/IUpdateable.cs`

---

## Step 6.4: Base Game Class (Njulf.Core/Game.cs)

**Responsibilities:**
- Run() method starts main loop
- Lifecycle hooks: Initialize(), Load(), Update(), Draw(), Unload(), OnResize()
- Frame timing (fixed or variable timestep)
- FPS and frame time statistics
- Service container management

**Design:**
```csharp
public abstract class Game
{
    private IServiceProvider _services;
    private IRenderer _renderer;
    private IContentManager _content;
    private IInputManager _input;
    private ICamera _camera;
    
    public void Run()
    {
        Initialize();
        Load();
        
        var window = _services.GetRequiredService<IWindow>();
        window.Update += OnWindowUpdate;
        window.RenderFrame += OnRenderFrame;
        window.Resize += OnResize;
        
        window.Run();
        
        Unload();
    }
    
    protected virtual void Initialize()
    {
        // Build DI container
        var services = new ServiceCollection();
        services.AddRendering(); // From Njulf.Rendering
        services.AddAssets();    // From Njulf.Assets
        services.AddInput();     // From Njulf.Input
        _services = services.BuildServiceProvider();
        
        _renderer = _services.GetRequiredService<IRenderer>();
        _content = _services.GetRequiredService<IContentManager>();
        _input = _services.GetRequiredService<IInputManager>();
        _camera = _services.GetRequiredService<ICamera>();
    }
    
    protected virtual void Load() { }
    protected virtual void Update(float deltaTime) { }
    protected virtual void Draw() { }
    protected virtual void Unload() { }
    protected virtual void OnResize(int width, int height) { }
    
    private void OnWindowUpdate(double deltaTime)
    {
        _input.Update();
        Update((float)deltaTime);
    }
    
    private void OnRenderFrame(double deltaTime)
    {
        _renderer.BeginFrame();
        Draw();
        _renderer.EndFrame();
    }
}
```

**Files:**
- `Njulf.Core/Game.cs`

---

## Step 6.5: Camera (Njulf.Core/Camera/)

**Files:**
- `Njulf.Core/Camera/CameraBase.cs` - Abstract base
- `Njulf.Core/Camera/FirstPersonCamera.cs` - WASD + mouse look
- `Njulf.Core/Camera/OrbitCamera.cs` - Orbit around target

---

## Step 6.6: Scene Graph (Njulf.Core/Scene/)

**Files:**
- `Njulf.Core/Scene/Scene.cs` - Lightweight scene manager
- `Njulf.Core/Scene/RenderObject.cs` - Mesh + material + transform
- `Njulf.Core/Scene/Model.cs` - Collection of render objects

---

## Step 6.7: Service Registration (Njulf.Rendering/ServiceCollectionExtensions.cs)

**Responsibilities:**
- DI registration helpers for each module

**Files:**
- `Njulf.Rendering/ServiceCollectionExtensions.cs` - `AddRendering()`
- `Njulf.Assets/ServiceCollectionExtensions.cs` - `AddAssets()`
- `Njulf.Input/ServiceCollectionExtensions.cs` - `AddInput()`

---

# PHASE 7: INPUT SYSTEM >>

## Step 7.1: Input Manager (Njulf.Input/InputManager.cs)

**Responsibilities:**
- Action mapping system
- Keyboard/mouse/joystick device management
- Using Silk.NET.Input

**Files:**
- `Njulf.Input/InputManager.cs`
- `Njulf.Input/Action.cs`
- `Njulf.Input/InputBinding.cs`

---

# PHASE 8: VULKAN RENDERER (Njulf.Rendering/VulkanRenderer.cs) >>

**Goal:** Main renderer implementing IRenderer.

**Responsibilities:**
- Coordinate all subsystems
- Implement IRenderer interface
- Main render loop integration
- Frame lifecycle management

**Design:**
```csharp
public class VulkanRenderer : IRenderer, IDisposable
{
    private readonly VulkanContext _context;
    private readonly SwapchainManager _swapchain;
    private readonly SynchronizationManager _sync;
    private readonly CommandBufferManager _cmd;
    private readonly BufferManager _bufferManager;
    private readonly TextureManager _textureManager;
    private readonly MeshManager _meshManager;
    private readonly LightManager _lightManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly RenderGraph _renderGraph;
    private readonly SceneDataBuilder _sceneDataBuilder;
    private readonly StagingRing _stagingRing;
    private readonly FenceBasedDeleter _deleter;
    
    private int _currentFrame = 0;
    
    public void Initialize(IWindow window)
    {
        // APPENDIX A initialization sequence
        _context = new VulkanContext(window);
        _swapchain = new SwapchainManager(_context, window);
        _sync = new SynchronizationManager(_context);
        _cmd = new CommandBufferManager(_context);
        _bufferManager = new BufferManager(_context.Allocator);
        _textureManager = new TextureManager(_context);
        _meshManager = new MeshManager(_context, _bufferManager);
        _lightManager = new LightManager(_context, _bufferManager);
        _bindlessHeap = new BindlessHeap(_context);
        _stagingRing = new StagingRing(_context.Allocator, 64 * 1024 * 1024, FramesInFlight);
        _deleter = new FenceBasedDeleter(_context);
        
        // Register static buffers in bindless heap
        RegisterSceneBuffers();
        
        // Create pipelines
        CreateMeshShaderPipeline();
        CreateLightCullingPipeline();
        
        // Build render graph
        _renderGraph = new RenderGraph();
        _renderGraph.AddPass(new DepthPrePass(_context, _swapchain, _bindlessHeap));
        _renderGraph.AddPass(new TiledLightCullingPass(_context, _swapchain, _bindlessHeap));
        _renderGraph.AddPass(new ForwardPlusPass(_context, _swapchain, _bindlessHeap));
        
        _sceneDataBuilder = new SceneDataBuilder(_meshManager, _bufferManager, _stagingRing);
    }
    
    public void BeginFrame()
    {
        _sync.WaitForInFlightFence(_currentFrame);
        _deleter.ProcessCompletedFrame(_sync.GetInFlightFence(_currentFrame));
        
        uint imageIndex = _swapchain.AcquireNextImage(_sync.GetImageAvailableSemaphore(_currentFrame));
        _sync.ResetFence(_currentFrame);
        
        var cmd = _cmd.BeginPrimaryGraphicsCommand(_currentFrame);
        
        // Transition swapchain image
        // ...
    }
    
    public void EndFrame()
    {
        var cmd = _cmd.GetCurrentGraphicsCommand();
        
        // Transition swapchain image for present
        // ...
        
        vk.EndCommandBuffer(cmd);
        
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &_sync.GetImageAvailableSemaphore(_currentFrame),
            PWaitDstStageMask = stackalloc PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit },
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &_sync.GetRenderFinishedSemaphore(_currentFrame)
        };
        
        vk.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, _sync.GetInFlightFence(_currentFrame));
        
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &_sync.GetRenderFinishedSemaphore(_currentFrame),
            SwapchainCount = 1,
            PSwapchains = &_swapchain.Swapchain,
            PImageIndices = &imageIndex,
            PResults = null
        };
        
        _swapchain.Present(&presentInfo);
        
        _currentFrame = (_currentFrame + 1) % FramesInFlight;
        _stagingRing.AdvanceFrame();
    }
    
    public void DrawScene(Scene scene, ICamera camera)
    {
        // Build CPU scene arrays
        _sceneDataBuilder.Build(scene, camera);
        
        // Upload scene data
        _sceneDataBuilder.Upload(_currentFrame);
        
        // Execute render graph
        var cmd = _cmd.GetCurrentGraphicsCommand();
        _renderGraph.Execute(cmd, _currentFrame, _sceneDataBuilder.SceneData);
    }
}
```

**Files:**
- `Njulf.Rendering/VulkanRenderer.cs`

---

# PHASE 9: TESTING & EXAMPLES >>

## Step 9.1: NjulfHelloGame (Test Example)

**Goal:** Demonstrate end-user API interaction.

**Example Usage:**
```csharp
public class HelloGame : Game
{
    private Model _model;
    private Texture2D _texture;
    
    protected override void Load()
    {
        _model = Content.Load<Model>("Assets/ models/teapot.gltf");
        _texture = Content.Load<Texture2D>("Assets/textures/wood.png");
        
        var renderObject = new RenderObject(_model.Meshes[0], _texture, Matrix4x4.Identity);
        Scene.Add(renderObject);
        
        Camera.Position = new Vector3(0, 0, 5);
    }
    
    protected override void Update(float deltaTime)
    {
        // Handle input
        if (Input.IsActionPressed("MoveForward"))
            Camera.MoveForward(deltaTime * 5);
        
        // Rotate model
        foreach (var obj in Scene.RenderObjects)
        {
            obj.Transform *= Matrix4x4.CreateRotationY(deltaTime * 0.5f);
        }
    }
    
    protected override void Draw()
    {
        Renderer.Clear(Color.CornflowerBlue);
        Renderer.DrawScene(Scene, Camera);
    }
}

public static class Program
{
    public static void Main()
    {
        using var game = new HelloGame();
        game.Run();
    }
}
```

**Files:**
- `NjulfHelloGame/HelloGame.cs` (update from Program.cs)

---

## Step 9.2: Unit Tests (Njulf.Tests/)

**Tests to implement:**
- Handle/index allocator use-after-free detection
- Bindless index table contract (host vs shader consistency)
- Buffer size estimation and growth
- Scene builder deduplication
- Bounding box computation
- Meshlet generation validity and coverage

**Files:**
- `Njulf.Tests/BufferManagerTests.cs`
- `Njulf.Tests/BindlessIndexTests.cs`
- `Njulf.Tests/MeshletBuilderTests.cs`
- `Njulf.Tests/SceneDataBuilderTests.cs`

---

# PHASE 10: VALIDATION & POLISH >=

## Step 10.1: Validation Layers
- Enable in development builds
- Zero validation errors as CI gate
- Debug markers for key resources

## Step 10.2: Error Handling
- Check all Vulkan result codes
- Contextual error messages
- Best-effort cleanup on initialization failure

## Step 10.3: Memory Leak Detection
- VMA statistics tracking
- Final cleanup verification

## Step 10.4: Performance Profiling
- Timestamp queries (optional)
- Frame statistics
- Buffer growth amortization testing

---

# IMPLEMENTATION ORDER (Recommended)

```
Phase 1  -> VulkanContext, SwapchainManager, SynchronizationManager, CommandBufferManager
        (Core infrastructure - can test with empty frame)

Phase 2  -> BufferManager, StagingRing, FenceBasedDeleter
        (Resource management foundation)

Phase 2  -> TextureManager, MeshManager, LightManager
        (Resource-specific managers)

Phase 3  -> BindlessIndexTable, BindlessHeap, SamplerManager
        (Descriptor system)

Phase 4  -> GPUStructs, BarrierBuilder
        (Pipeline foundation)

Phase 4  -> RenderGraph, RenderPassBase
        (Pipeline infrastructure)

Phase 5  -> MeshPipeline, ComputePipeline
        (Pipeline objects)

Phase 4  -> DepthPrePass, TiledLightCullingPass, ForwardPlusPass
        (Render passes - can test each independently)

Phase 4  -> SceneDataBuilder, SceneRenderingData
        (Scene assembly)

Phase 6  -> Math library, Enums, Interfaces
        (High-level API foundation)

Phase 6  -> Game.cs, Camera classes, Scene graph
        (High-level API)

Phase 7  -> InputManager
        (Input system)

Phase 8  -> VulkanRenderer
        (Integrate everything)

Phase 5  -> ContentManager, ModelImporter, MeshletBuilder
        (Content pipeline)

Phase 9  -> NjulfHelloGame, Unit Tests
        (Testing and validation)

Phase 10 -> Polish and optimization
```

---

# CRITICAL INVARIANTS TO VERIFY

| Invariant | Verification Method |
|-----------|---------------------|
| Bindless indices match shaders | Unit test comparing BindlessIndexTable.cs with shader header |
| Swapchain destroyed before surface | SwapchainManager.Dispose() order |
| Device-idle before in-use disposal | Call vk.DeviceWaitIdle() before swapchain recreation |
| Per-frame resources not reused | FenceBasedDeleter tracks per fence |
| Meshlet offsets validated | SceneDataBuilder validates all offsets |
| FramesInFlight=2 consistency | Constants defined in one place |
| Mesh shader support checked | Device selection verifies feature |
| Bindless heap indices unique | Free-list allocator for dynamic indices |

---

# FILES TO CREATE (Summary)

## Njulf.Core/
- Math/Vector2.cs, Vector3.cs, Vector4.cs
- Math/Matrix4x4.cs, Quaternion.cs
- Math/BoundingBox.cs, BoundingSphere.cs, Ray.cs, Color.cs
- Enums/BufferUsage.cs, MemoryUsage.cs, PrimitiveType.cs, CullMode.cs, BlendState.cs, DepthStencilState.cs
- Interfaces/IRenderer.cs, ICamera.cs, IContentManager.cs, IInputManager.cs, IRenderable.cs, IUpdateable.cs
- Camera/CameraBase.cs, FirstPersonCamera.cs, OrbitCamera.cs
- Scene/Scene.cs, RenderObject.cs, Model.cs
- Game.cs
- ServiceCollectionExtensions.cs

## Njulf.Rendering/
- Core/VulkanContext.cs, VulkanContextOptions.cs
- Core/SwapchainManager.cs
- Core/SynchronizationManager.cs
- Core/CommandBufferManager.cs
- Memory/BufferManager.cs, BufferHandle.cs
- Memory/StagingRing.cs
- Memory/FenceBasedDeleter.cs
- Resources/TextureManager.cs, TextureHandle.cs
- Resources/MeshManager.cs, MeshHandle.cs
- Resources/LightManager.cs
- Descriptors/BindlessIndexTable.cs
- Descriptors/BindlessHeap.cs
- Descriptors/DescriptorSetLayouts.cs
- Descriptors/SamplerManager.cs
- Data/GPUStructs.cs
- Data/SceneRenderingData.cs
- Data/SceneDataBuilder.cs
- Pipeline/RenderGraph.cs
- Pipeline/RenderPassBase.cs
- Pipeline/PipelineObjects/MeshPipeline.cs
- Pipeline/PipelineObjects/ComputePipeline.cs
- Pipeline/DepthPrePass.cs
- Pipeline/TiledLightCullingPass.cs
- Pipeline/ForwardPlusPass.cs
- Utilities/BarrierBuilder.cs
- VulkanRenderer.cs
- ServiceCollectionExtensions.cs

## Njulf.Assets/
- ContentManager.cs
- ModelImporter.cs, ImporterOptions.cs
- MeshletBuilder.cs
- Meshlet.cs
- ServiceCollectionExtensions.cs

## Njulf.Input/
- InputManager.cs
- Action.cs
- InputBinding.cs
- ServiceCollectionExtensions.cs

## Njulf.Tests/
- BufferManagerTests.cs
- BindlessIndexTests.cs
- MeshletBuilderTests.cs
- SceneDataBuilderTests.cs

## NjulfHelloGame/
- HelloGame.cs (replace Program.cs)

---

# SHADER FILES (Njulf.Shaders/)

- mesh.mesh - Mesh shader
- task.task - Task shader
- frag.frag - Fragment shader
- lightcull.comp - Light culling compute shader
- common.glsl - Shared definitions (bindless indices, structs)

**Note:** Shader files must use the same bindless indices as defined in `BindlessIndexTable.cs`.

---

# SUCCESS CRITERIA

1. **NjulfHelloGame runs** and renders a loaded model
2. **Zero Vulkan validation errors** in development builds
3. **All unit tests pass**
4. **API is clean and intuitive** - end user can load model, add to scene, run with minimal code
5. **No per-object draw calls** - verified via RenderDoc or similar
6. **Meshlets are generated correctly** - all triangles covered, valid bounds
7. **Bindless access works** - resources accessed by integer indices in shaders
8. **FramesInFlight=2** - double buffering with proper synchronization
9. **Memory leaks detected** - VMA statistics show no unreleased resources
10. **Performance targets met** - 60+ FPS with 10K-100K meshlets on capable GPU
