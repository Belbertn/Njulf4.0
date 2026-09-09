using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Graphics.Vulkan;

/// <summary>
/// Borrowed Vulkan device information. Do not dispose the API, instance, or device.
/// This information becomes invalid at renderer shutdown. Use only on the device thread.
/// </summary>
public readonly record struct VulkanDeviceInfo(Vk Api, Instance Instance,
    PhysicalDevice PhysicalDevice, Device Device);

/// <summary>A borrowed allocation slice; offset, length, and stride are measured in bytes.</summary>
public readonly record struct VulkanBufferSlice(VkBuffer Buffer, ulong Offset, ulong Length, uint Stride);

/// <summary>
/// The actual buffers used for a mesh's production streams. These snapshots become
/// invalid on resource mutation, buffer growth/compaction, or shutdown. Re-query before
/// inspection and never destroy or mutate these buffers or retain them across frames.
/// </summary>
public readonly record struct VulkanMeshBindings(VulkanBufferSlice Positions,
    VulkanBufferSlice NormalsAndTangents, VulkanBufferSlice UvsAndColors, VulkanBufferSlice Indices);

/// <summary>
/// Vulkan inspection and scoped custom-pass registration on the existing device.
/// Native device/resource handles are borrowed.
/// </summary>
public static class VulkanGraphicsExtensions
{
    /// <summary>Registers an owned pass on the graphics queue with a unique name.</summary>
    public static VulkanPassRegistration AddVulkanPass(this GraphicsDevice device,
        VulkanPassDescription description, IVulkanRenderPass pass)
    {
        ArgumentNullException.ThrowIfNull(device);
        var graphics = device as VulkanGraphicsDevice ?? throw new NotSupportedException("This graphics device does not provide Vulkan access.");
        return graphics.CustomPasses.Add(description, pass);
    }
    /// <summary>Gets the renderer's existing Vulkan device without creating another device.</summary>
    public static VulkanDeviceInfo GetVulkanDeviceInfo(this GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var graphics = device as VulkanGraphicsDevice ?? throw new NotSupportedException("This graphics device does not provide Vulkan access.");
        graphics.EnsureUsable();
        var context = graphics.Context;
        return new(context.Api, context.Instance, context.PhysicalDevice, context.Device);
    }

    /// <summary>Gets current production buffer slices for a live mesh owned by this device.</summary>
    public static VulkanMeshBindings GetVulkanMeshBindings(this GraphicsDevice device, IMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(device);
        var graphics = device as VulkanGraphicsDevice ?? throw new NotSupportedException("This graphics device does not provide Vulkan access.");
        MeshHandle handle = graphics.ValidateMesh(mesh);
        MeshInfo info = graphics.Meshes.GetMeshInfo(handle);
        return new(
            Slice(graphics, graphics.Meshes.VertexPositionBuffer, info.VertexOffset, info.VertexCount,
                (uint)Marshal.SizeOf<GPUVertexPositionStream>()),
            Slice(graphics, graphics.Meshes.VertexNormalTangentBuffer, info.VertexOffset, info.VertexCount,
                (uint)Marshal.SizeOf<GPUVertexNormalTangentStream>()),
            Slice(graphics, graphics.Meshes.VertexUvColorBuffer, info.VertexOffset, info.VertexCount,
                (uint)Marshal.SizeOf<GPUVertexUvColorStream>()),
            Slice(graphics, graphics.Meshes.IndexBuffer, info.IndexOffset, info.EffectiveGpuIndexCount,
                sizeof(uint)));
    }

    private static VulkanBufferSlice Slice(VulkanGraphicsDevice graphics, BufferHandle buffer,
        uint first, uint count, uint stride)
    {
        ulong offset = checked((ulong)first * stride);
        ulong length = checked((ulong)count * stride);
        if (checked(offset + length) > graphics.Buffers.GetBufferSize(buffer))
            throw new InvalidOperationException("The mesh slice exceeds its backing allocation.");
        return new(graphics.Buffers.GetBuffer(buffer), offset, length, stride);
    }
}
