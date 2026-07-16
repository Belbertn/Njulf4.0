namespace Njulf.Rendering.Diagnostics;

public sealed record RendererResourceLeakSnapshot(
    int MeshCount,
    int MaterialCount,
    int TextureCount,
    int DescriptorWrites,
    ulong GpuBytes,
    long ManagedBytes,
    int PendingDeletionCount)
{
    public ulong TotalTrackedBytes => GpuBytes + (ulong)System.Math.Max(0, ManagedBytes);
}
