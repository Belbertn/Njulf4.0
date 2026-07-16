using System.Collections.Generic;
using Njulf.Core.Scene;

namespace Njulf.Rendering.Resources;

public sealed class FoliageDebugFallbackResult
{
    internal FoliageDebugFallbackResult(
        IReadOnlyList<StaticInstanceBatch> batches,
        int patchCount,
        int generatedInstanceCount,
        int droppedInstanceCount,
        long buildMicroseconds)
    {
        Batches = batches;
        PatchCount = patchCount;
        GeneratedInstanceCount = generatedInstanceCount;
        DroppedInstanceCount = droppedInstanceCount;
        BuildMicroseconds = buildMicroseconds;
    }

    public IReadOnlyList<StaticInstanceBatch> Batches { get; }
    public int PatchCount { get; }
    public int GeneratedInstanceCount { get; }
    public int DroppedInstanceCount { get; }
    public long BuildMicroseconds { get; }
    public bool WasCapped => DroppedInstanceCount > 0;
}
