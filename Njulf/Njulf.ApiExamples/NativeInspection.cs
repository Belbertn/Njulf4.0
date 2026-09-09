using Njulf.Graphics;
using Njulf.Graphics.Vulkan;

namespace Njulf.ApiExamples;

internal static class NativeInspection
{
    public static void Print(GraphicsDevice graphics, Mesh mesh)
    {
        VulkanDeviceInfo device = graphics.GetVulkanDeviceInfo();
        VulkanMeshBindings bindings = graphics.GetVulkanMeshBindings(mesh);
        Console.WriteLine($"Existing Vulkan device: {device.Device.Handle}; mesh position buffer: {bindings.Positions.Buffer.Handle}");
        Console.WriteLine($"Position slice: offset={bindings.Positions.Offset}, bytes={bindings.Positions.Length}, stride={bindings.Positions.Stride}");
        // Inspection only. These borrowed snapshots are not cached or destroyed.
    }
}
