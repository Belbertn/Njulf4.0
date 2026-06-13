using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics;

public sealed record RendererResourceLeakFinding(
    string Category,
    long Before,
    long After,
    long Tolerance,
    bool Passed);

public sealed class RendererResourceLeakAuditor
{
    public IReadOnlyList<RendererResourceLeakFinding> Compare(
        RendererResourceLeakSnapshot before,
        RendererResourceLeakSnapshot after,
        double managedMemoryGrowthTolerance = 0.10,
        ulong gpuByteTolerance = 0)
    {
        var findings = new List<RendererResourceLeakFinding>
        {
            CompareInt("meshes", before.MeshCount, after.MeshCount, 0),
            CompareInt("materials", before.MaterialCount, after.MaterialCount, 0),
            CompareInt("textures", before.TextureCount, after.TextureCount, 0),
            CompareInt("pending deletions", before.PendingDeletionCount, after.PendingDeletionCount, 0),
            CompareLong("gpu bytes", (long)before.GpuBytes, (long)after.GpuBytes, (long)gpuByteTolerance),
            CompareLong(
                "managed bytes",
                before.ManagedBytes,
                after.ManagedBytes,
                (long)Math.Ceiling(before.ManagedBytes * managedMemoryGrowthTolerance))
        };

        return findings;
    }

    private static RendererResourceLeakFinding CompareInt(string category, int before, int after, int tolerance)
    {
        return CompareLong(category, before, after, tolerance);
    }

    private static RendererResourceLeakFinding CompareLong(string category, long before, long after, long tolerance)
    {
        long growth = after - before;
        return new RendererResourceLeakFinding(category, before, after, tolerance, growth <= tolerance);
    }
}
