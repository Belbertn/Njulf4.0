# Native Vulkan → Silk.NET Translation Guide

A concise reference for translating native Vulkan (C) API calls into Silk.NET
(C#) API calls.

## Core Concept: The `Vk` Dispatch Object

In native Vulkan, you call global functions like `vkCreateBuffer(...)`. In
Silk.NET, all commands are methods on a `Vk` instance (the API dispatch table).

```c
// Native C
vkCreateDevice(physicalDevice, &createInfo, NULL, &device);
```

```csharp
// Silk.NET — `vk` is a Vk instance
vk.CreateDevice(physicalDevice, &createInfo, null, out device);
```

## 1. Naming Conventions

| Concept         | Native Vulkan                  | Silk.NET                                |
|-----------------|--------------------------------|-----------------------------------------|
| Function prefix | `vkCreateBuffer`               | `vk.CreateBuffer` (drop `vk`, PascalCase) |
| Struct types    | `VkBufferCreateInfo`           | `BufferCreateInfo` (drop `Vk`)          |
| Enum types      | `VkResult`                     | `Result` (drop `Vk`)                    |
| Enum values     | `VK_RESULT_SUCCESS`            | `Result.Success`                        |
| Flag bits       | `VK_IMAGE_USAGE_SAMPLED_BIT`   | `ImageUsageFlags.SampledBit`            |
| Handles         | `VkDevice`                     | `Device`                                |
| Constants       | `VK_QUEUE_FAMILY_IGNORED`      | `Vk.QueueFamilyIgnored`                 |
| Null handle     | `VK_NULL_HANDLE`               | `default`                               |

## 2. Structs and `sType`

The `.sType` field must be set explicitly, just like in C, but using the
`StructureType` enum.

```c
// Native C
VkSemaphoreCreateInfo info = {0};
info.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
```

```csharp
// Silk.NET
var info = new SemaphoreCreateInfo
{
    SType = StructureType.SemaphoreCreateInfo
};
```

`pNext` chaining works identically using pointers:

```csharp
var properties2 = new PhysicalDeviceProperties2
{
    SType = StructureType.PhysicalDeviceProperties2,
    PNext = &vk12Properties   // pointer to next struct
};
```

## 3. Pointer Parameters

Silk.NET preserves the raw pointer signatures. You typically work inside
`unsafe` methods.

| Native C                       | Silk.NET                          |
|--------------------------------|-----------------------------------|
| `const VkXCreateInfo*`         | `&createInfoStruct` (address-of)  |
| `const VkAllocationCallbacks*` | `null` (typically)                |
| `VkX* pHandle` (out)           | `out handle` **or** `&handle`     |
| Array `const VkX* + count`     | `fixed (X* p = array)` + count    |

```c
// Native C
vkCreateBuffer(device, &createInfo, NULL, &buffer);
```

```csharp
// Silk.NET — two equivalent styles
vk.CreateBuffer(device, &createInfo, null, out buffer);
vk.CreateBuffer(device, &createInfo, null, &buffer);
```

## 4. Arrays: The `fixed` Pattern

When native Vulkan takes a count + pointer to an array, pin the managed array
with `fixed`.

```c
// Native C
VkDescriptorSetLayout layouts[N];
allocInfo.descriptorSetCount = N;
allocInfo.pSetLayouts = layouts;
vkAllocateDescriptorSets(device, &allocInfo, descriptorSets);
```

```csharp
// Silk.NET
fixed (DescriptorSetLayout* layoutsPtr = layouts)
fixed (DescriptorSet* setsPtr = descriptorSets)
{
    var allocInfo = new DescriptorSetAllocateInfo
    {
        SType = StructureType.DescriptorSetAllocateInfo,
        DescriptorSetCount = framesInFlight,
        PSetLayouts = layoutsPtr
    };
    vk.AllocateDescriptorSets(device, &allocInfo, setsPtr);
}
```

For small, temporary arrays, `stackalloc` is idiomatic:

```csharp
var waitSemaphores = stackalloc Semaphore[2] { a, b };
var waitStages = stackalloc PipelineStageFlags[2] { stageA, stageB };
```

## 5. Result Checking

```c
// Native C
if (vkCreateFence(device, &info, NULL, &fence) != VK_SUCCESS) { /* error */ }
```

```csharp
// Silk.NET
if (vk.CreateFence(device, &info, null, out fence) != Result.Success)
    throw new Exception("Failed to create fence");
```

## 6. Getting the API Instance & Extensions

Native Vulkan resolves extension function pointers manually
(`vkGetDeviceProcAddr`). Silk.NET wraps each extension in a class you fetch
via `TryGet...Extension`.

```csharp
// Get the core API
var vk = Vk.GetApi();

// Instance-level extension (e.g. VK_KHR_surface)
vk.TryGetInstanceExtension(instance, out KhrSurface khrSurface);

// Device-level extension (e.g. VK_KHR_swapchain, VK_EXT_mesh_shader)
vk.TryGetDeviceExtension(instance, device, out KhrSwapchain khrSwapchain);
vk.TryGetDeviceExtension(instance, device, out ExtMeshShader extMeshShader);
```

Extension commands are then methods on that object, with the `vk`/extension
prefix dropped:

```c
// Native C
vkAcquireNextImageKHR(device, swapchain, UINT64_MAX, sem, fence, &index);
vkQueuePresentKHR(queue, &presentInfo);
```

```csharp
// Silk.NET
khrSwapchain.AcquireNextImage(device, swapchain, ulong.MaxValue, sem, default, &index);
khrSwapchain.QueuePresent(queue, &presentInfo);
```

## 7. Command Buffer Recording

`vkCmd*` calls map directly to `vk.Cmd*`:

```c
// Native C
vkCmdCopyBuffer(cmd, src, dst, 1, &region);
vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, NULL, 0, NULL, 1, &barrier);
```

```csharp
// Silk.NET
vk.CmdCopyBuffer(cmd, src, dst, 1, &region);
vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
```

## 8. Strings

Native Vulkan uses `const char*`. Silk.NET provides `SilkMarshal` for
conversion.

```csharp
// Managed string -> native UTF-8 pointer
nint namePtr = SilkMarshal.StringToPtr(name);
try
{
    var info = new DebugUtilsObjectNameInfoEXT
    {
        SType = StructureType.DebugUtilsObjectNameInfoExt,
        PObjectName = (byte*)namePtr
    };
    // ... use info ...
}
finally
{
    SilkMarshal.Free(namePtr);   // always free
}
```

## 9. Handles Are Strongly-Typed Structs

Native `uint64_t`-style handles become small structs exposing a `.Handle`
(`ulong`) property. Use it for null checks:

```c
// Native C
if (buffer != VK_NULL_HANDLE) vkDestroyBuffer(device, buffer, NULL);
```

```csharp
// Silk.NET
if (buffer.Handle != 0) vk.DestroyBuffer(device, buffer, null);
```

## 10. Name Collisions (Important)

A few Silk.NET types clash with .NET BCL types. Use aliases at the top of the
file:

```csharp
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;   // vs System.Threading.Semaphore
using Buffer = Silk.NET.Vulkan.Buffer;         // vs System.Buffer
```

## Quick Reference Cheat Sheet

| Native Vulkan                  | Silk.NET                        |
|--------------------------------|---------------------------------|
| `vkCreateDevice(...)`          | `vk.CreateDevice(...)`          |
| `VkDeviceCreateInfo`           | `DeviceCreateInfo`              |
| `.sType = VK_STRUCTURE_TYPE_*` | `.SType = StructureType.*`      |
| `VK_SUCCESS`                   | `Result.Success`                |
| `VK_NULL_HANDLE`               | `default`                       |
| `NULL` (allocator)             | `null`                          |
| `&myStruct`                    | `&myStruct`                     |
| `VkX* pOut`                    | `out x` or `&x`                 |
| `array` (param)                | `fixed (X* p = array)` / `stackalloc` |
| `const char*`                  | `SilkMarshal.StringToPtr`       |
| `vkGetDeviceProcAddr` (ext)    | `vk.TryGetDeviceExtension(...)` |
| `vkCmdDraw(...)`               | `vk.CmdDraw(...)`               |
| `handle != VK_NULL_HANDLE`     | `handle.Handle != 0`            |

## Golden Rules

1. **Drop the `vk`/`Vk` prefix and PascalCase everything.**
2. Keep the same pointer-based call structure inside `unsafe` blocks.
3. Set `SType` on every struct.
4. Fetch extensions through `TryGet...Extension` instead of manual
   proc-address lookup.

## 11. Advanced & Non-Mechanical Translations

The rules above cover most calls, but the following areas do **not** follow a
simple "drop the prefix" mapping. They either have no native equivalent, use a
helper API, or require special marshalling.

### 11.1 API & Extension Bootstrapping

There is no native counterpart to `Vk.GetApi()` — in C you link against the
loader directly. In Silk.NET you must first obtain the dispatch object, then
fetch extensions through typed wrappers.

```csharp
// Acquire the core API (no native equivalent — this loads the dispatch table)
var vk = Vk.GetApi();

// Instance-level extensions
vk.TryGetInstanceExtension(instance, out KhrSurface khrSurface);
vk.TryGetInstanceExtension(instance, out ExtDebugUtils debugUtils);

// Device-level extensions
vk.TryGetDeviceExtension(instance, device, out KhrSwapchain khrSwapchain);
```

Each extension object exposes that extension's commands as methods (with the
`vk` prefix and `KHR`/`EXT` suffix dropped), e.g. `khrSwapchain.QueuePresent(...)`.

### 11.2 Memory Allocation via VMA

Raw `vkAllocateMemory` / `vkBindBufferMemory` are typically replaced by the
Vulkan Memory Allocator (VMA). This is a *different* API surface, not a 1:1
translation — VMA creates the buffer/image **and** binds memory in one call.

WE WILL BE USING GpuMemoryAllocator.vma from nuget

```c
// Native C (manual): create buffer, query requirements, allocate, bind
vkCreateBuffer(device, &bufferInfo, NULL, &buffer);
vkGetBufferMemoryRequirements(device, buffer, &memReqs);
vkAllocateMemory(device, &allocInfo, NULL, &memory);
vkBindBufferMemory(device, buffer, memory, 0);
```

```csharp
// Silk.NET + VMA: single call handles creation, allocation and binding
var bufferInfo = new BufferCreateInfo
{
    SType = StructureType.BufferCreateInfo,
    Size = size,
    Usage = usage,
    SharingMode = SharingMode.Exclusive
};

var allocInfo = new AllocationCreateInfo
{
    Usage = MemoryUsage.AutoPreferDevice
};

Buffer buffer;
Allocation* allocation;
AllocationInfo allocationInfo;

var result = Apis.CreateBuffer(
    allocator,           // VMA Allocator*
    &bufferInfo,
    &allocInfo,
    &buffer,
    &allocation,
    &allocationInfo);
```

| Native Vulkan                  | VMA via Silk.NET                 |
|--------------------------------|----------------------------------|
| `vkCreateBuffer` + bind        | `Apis.CreateBuffer(...)`         |
| `vkCreateImage` + bind         | `Apis.CreateImage(...)`          |
| `vkDestroyBuffer` + free       | `Apis.DestroyBuffer(...)`        |
| `vkDestroyImage` + free        | `Apis.DestroyImage(...)`         |
| `vkMapMemory`                  | `AllocationInfo.PMappedData`     |
| `vkFlushMappedMemoryRanges`    | `Apis.FlushAllocation(...)`      |
| `vkInvalidateMappedMemoryRanges`| `Apis.InvalidateAllocation(...)`|

> Note: `VkDeviceMemory` becomes an opaque VMA `Allocation*`; you never deal
> with raw memory handles directly.

### 11.3 Surface Creation via Windowing

Native surface creation is platform-specific (`vkCreateWin32SurfaceKHR`,
`vkCreateXcbSurfaceKHR`, ...). Silk.NET abstracts this through the windowing
layer, so there's no direct call to translate.

```csharp
// `window` is an IWindow; VkSurface bridges the windowing system to Vulkan
SurfaceKHR surface = window.VkSurface!
    .Create<AllocationCallbacks>(instance.ToHandle(), null)
    .ToSurface();

if (surface.Handle == 0)
    throw new Exception("Failed to create Vulkan surface");
```

Destruction still uses the `KHR_surface` extension:

```csharp
khrSurface.DestroySurface(instance, surface, null);
```

### 11.4 Debug Messenger Callbacks (Delegates)

Native function pointers (`PFN_vkDebugUtilsMessengerCallbackEXT`) become
Silk.NET delegate wrappers (`PfnDebugUtilsMessengerCallbackEXT`). The managed
delegate must be kept alive (store it in a field) to avoid GC collecting it
while Vulkan holds the pointer.

```csharp
// Managed callback matching the native signature
private static uint DebugCallback(
    DebugUtilsMessageSeverityFlagsEXT severity,
    DebugUtilsMessageTypeFlagsEXT type,
    DebugUtilsMessengerCallbackDataEXT* pCallbackData,
    void* pUserData)
{
    var msg = SilkMarshal.PtrToString((nint)pCallbackData->PMessage);
    Console.WriteLine($"[Vulkan] {msg}");
    return Vk.False; // must return VK_FALSE
}

var createInfo = new DebugUtilsMessengerCreateInfoEXT
{
    SType = StructureType.DebugUtilsMessengerCreateInfoExt,
    MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                    | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
    MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
    // Wrap the managed delegate as a native function pointer
    PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback)
};

debugUtils.CreateDebugUtilsMessenger(instance, &createInfo, null, out var messenger);
```

> Keep the `PfnDebugUtilsMessengerCallbackEXT` (or the delegate it wraps) in a
> field for the lifetime of the messenger, or it may be garbage-collected.

### 11.5 Synchronization2 & Dynamic Rendering

Newer APIs replace render passes / framebuffers and the old barrier struct with
richer, more explicit structures. The translation is still mechanical, but the
struct *shapes* differ from the legacy path.

**Dynamic rendering** (replaces `vkCmdBeginRenderPass`):

```csharp
var colorAttachment = new RenderingAttachmentInfo
{
    SType = StructureType.RenderingAttachmentInfo,
    ImageView = swapchainImageView,
    ImageLayout = ImageLayout.ColorAttachmentOptimal,
    LoadOp = AttachmentLoadOp.Clear,
    StoreOp = AttachmentStoreOp.Store,
    ClearValue = new ClearValue(new ClearColorValue(0.1f, 0.1f, 0.1f, 1f))
};

var renderingInfo = new RenderingInfo
{
    SType = StructureType.RenderingInfo,
    RenderArea = new Rect2D(new Offset2D(0, 0), extent),
    LayerCount = 1,
    ColorAttachmentCount = 1,
    PColorAttachments = &colorAttachment
};

vk.CmdBeginRendering(cmd, &renderingInfo);
// ... draw calls ...
vk.CmdEndRendering(cmd);
```

**Synchronization2 barriers** (replaces `vkCmdPipelineBarrier`):

```csharp
var imageBarrier = new ImageMemoryBarrier2
{
    SType = StructureType.ImageMemoryBarrier2,
    SrcStageMask = PipelineStageFlags2.TopOfPipeBit,
    SrcAccessMask = AccessFlags2.None,
    DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
    DstAccessMask = AccessFlags2.ColorAttachmentWriteBit,
    OldLayout = ImageLayout.Undefined,
    NewLayout = ImageLayout.ColorAttachmentOptimal,
    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
    Image = image,
    SubresourceRange = subresourceRange
};

var depInfo = new DependencyInfo
{
    SType = StructureType.DependencyInfo,
    ImageMemoryBarrierCount = 1,
    PImageMemoryBarriers = &imageBarrier
};

vk.CmdPipelineBarrier2(cmd, &depInfo);
```

| Legacy                     | Synchronization2 / Dynamic Rendering |
|----------------------------|--------------------------------------|
| `vkCmdPipelineBarrier`     | `vk.CmdPipelineBarrier2` + `DependencyInfo` |
| `VkImageMemoryBarrier`     | `ImageMemoryBarrier2` (stage masks live inside the barrier) |
| `PipelineStageFlags`       | `PipelineStageFlags2`                |
| `AccessFlags`              | `AccessFlags2`                       |
| `vkCmdBeginRenderPass`     | `vk.CmdBeginRendering`               |
| `VkRenderPass` / `VkFramebuffer` | (none — attachments passed inline) |

> Both require the relevant feature/extension enabled at device creation
> (`VK_KHR_synchronization2`, `VK_KHR_dynamic_rendering`, or Vulkan 1.3 core).

### 11.6 Bitwise Flags & Zero Values

Native `uint32_t` flag fields become typed `[Flags]` enums. A few gotchas:

```csharp
// Combine flags with | just like in C
var usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit;

// "No flags" — prefer the named member if it exists, otherwise default/0
var noFlags = ImageCreateFlags.None;     // explicit, preferred
SrcAccessMask = AccessFlags.None;        // instead of literal 0

// Casting between an enum and its underlying integer when an API needs it
uint raw = (uint)usage;
var typed = (BufferUsageFlags)raw;

// Null vs default for handles/pointers
vk.QueueSubmit(queue, 1, &submitInfo, default);   // VK_NULL_HANDLE fence
vk.CmdPipelineBarrier(cmd, src, dst, 0, 0, null, 0, null, 1, &barrier); // empty arrays => null
```

> `0`, `default`, and the enum's `None` member are interchangeable for flags,
> but using `None` (or `default`) reads more clearly than a bare `0`.