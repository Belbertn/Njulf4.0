using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Access history of submitted work, separate from the frame currently being recorded.
/// Whole allocations deliberately merge overlapping views; history banks have distinct handles.
/// This plans same-queue dependencies only. Queue handoffs remain the scheduler's responsibility.
/// </summary>
internal sealed class InterFrameAccessTracker
{
    internal readonly record struct Allocation(RenderGraphConcreteResourceKind Kind, ulong Handle, ulong Generation);

    internal readonly record struct Scope(PipelineStageFlags2 Stages, AccessFlags2 Access)
    {
        public Scope Union(Scope other) => new(Stages | other.Stages, Access | other.Access);
    }

    private readonly record struct Accesses(Scope Readers, Scope Writers)
    {
        public Accesses Union(Accesses other) => new(Readers.Union(other.Readers), Writers.Union(other.Writers));
    }

    private readonly Dictionary<Allocation, Accesses> _submitted = new();
    private readonly Dictionary<Allocation, Accesses> _recording = new();
    private readonly HashSet<(Allocation Allocation, bool Write, Scope Destination)> _dependencies = new();
    private HashSet<Allocation>? _pendingLiveAllocations;
    private PipelineStageFlags2 _coveredStages;

    public void BeginRecording()
    {
        // Also abandons an unsubmitted recording. It must never become GPU history.
        _recording.Clear();
        _dependencies.Clear();
        _pendingLiveAllocations = null;
        _coveredStages = PipelineStageFlags2.None;
    }

    // Only an ALL_COMMANDS source with all memory accesses can establish this
    // coverage. It orders prior submissions, not writes later in this recording.
    public void CoverSubmittedAccesses(PipelineStageFlags2 stages) => _coveredStages |= stages;

    public MemoryBarrier2? RequireConservativeDependency(PipelineStageFlags2 stages)
    {
        if ((_coveredStages & PipelineStageFlags2.AllCommandsBit) != 0)
            return null;
        stages &= ~_coveredStages;
        if (stages == PipelineStageFlags2.None)
            return null;
        CoverSubmittedAccesses(stages);
        return new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstStageMask = stages,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
        };
    }

    public MemoryBarrier2? Access(Allocation allocation, Scope destination, bool write)
    {
        _recording.TryGetValue(allocation, out Accesses current);
        _recording[allocation] = current.Union(write
            ? new Accesses(default, destination)
            : new Accesses(destination, default));

        if ((_coveredStages & PipelineStageFlags2.AllCommandsBit) != 0 ||
            (destination.Stages & ~_coveredStages) == 0)
            return null;

        if (!_submitted.TryGetValue(allocation, out Accesses previous))
            return null;

        Scope source = write ? previous.Readers.Union(previous.Writers) : previous.Writers;
        if (source.Stages == PipelineStageFlags2.None)
            return null;

        // Preserve the stage/access pairing. Unioning independent destinations can falsely
        // claim that a third stage/access combination has already acquired visibility.
        if (!_dependencies.Add((allocation, write, destination)))
            return null;

        // Reads must finish before an overwrite, but have no writes to make available.
        bool executionOnly = previous.Writers.Stages == PipelineStageFlags2.None;
        return new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = source.Stages,
            SrcAccessMask = executionOnly ? AccessFlags2.None : previous.Writers.Access,
            DstStageMask = destination.Stages,
            DstAccessMask = executionOnly ? AccessFlags2.None : destination.Access
        };
    }

    public void CommitSubmission()
    {
        if (_pendingLiveAllocations is { } live)
        {
            foreach (Allocation allocation in new List<Allocation>(_submitted.Keys))
                if (!live.Contains(allocation))
                    _submitted.Remove(allocation);
        }
        foreach (var (allocation, accesses) in _recording)
        {
            // A read does not retire the previous writer: a future reader at another stage
            // still needs visibility. Likewise retain all reader stages until an overwrite.
            if (accesses.Writers.Stages == PipelineStageFlags2.None &&
                _submitted.TryGetValue(allocation, out Accesses previous))
                _submitted[allocation] = previous.Union(accesses);
            else
                _submitted[allocation] = accesses;
        }
        BeginRecording();
    }

    public void RetainAllocations(HashSet<Allocation> live)
    {
        _pendingLiveAllocations = new HashSet<Allocation>(live);
    }

    public void Clear()
    {
        _submitted.Clear();
        BeginRecording();
    }
}
