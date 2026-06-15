using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Pipeline;

public enum AsyncComputeMode
{
    Disabled,
    Conservative,
    Aggressive
}

public enum AsyncComputeAvailability
{
    Unavailable,
    SharedGraphicsQueue,
    DedicatedComputeQueue
}

public sealed record AsyncComputeDeviceProfile(
    AsyncComputeAvailability Availability,
    uint GraphicsQueueFamily,
    uint ComputeQueueFamily,
    uint TransferQueueFamily,
    bool TimelineSemaphoresSupported,
    bool TimestampComputeAndGraphicsSupported)
{
    public bool HasDedicatedCompute => Availability == AsyncComputeAvailability.DedicatedComputeQueue;
    public bool RequiresOwnershipTransfer => HasDedicatedCompute && GraphicsQueueFamily != ComputeQueueFamily;

    public static AsyncComputeDeviceProfile FromQueueFamilies(
        uint graphicsQueueFamily,
        uint computeQueueFamily,
        uint transferQueueFamily,
        bool computeQueueAvailable,
        bool timelineSemaphoresSupported,
        bool timestampComputeAndGraphicsSupported)
    {
        AsyncComputeAvailability availability = !computeQueueAvailable
            ? AsyncComputeAvailability.Unavailable
            : computeQueueFamily == graphicsQueueFamily
                ? AsyncComputeAvailability.SharedGraphicsQueue
                : AsyncComputeAvailability.DedicatedComputeQueue;
        return new AsyncComputeDeviceProfile(
            availability,
            graphicsQueueFamily,
            computeQueueFamily,
            transferQueueFamily,
            timelineSemaphoresSupported,
            timestampComputeAndGraphicsSupported);
    }

    public string Explain(AsyncComputeMode mode)
    {
        if (mode == AsyncComputeMode.Disabled)
            return "Async compute disabled by settings.";
        return Availability switch
        {
            AsyncComputeAvailability.Unavailable => "Async compute unavailable: no compute-capable queue was selected.",
            AsyncComputeAvailability.SharedGraphicsQueue => "Async compute disabled: compute shares the graphics queue family.",
            _ => TimelineSemaphoresSupported
                ? "Async compute enabled with dedicated compute queue and timeline synchronization."
                : "Async compute enabled with dedicated compute queue and binary semaphore fallback."
        };
    }
}

public sealed record AsyncPassSchedulingHint(
    string PassName,
    bool AsyncEligible,
    RenderGraphQueueClass PreferredQueue,
    int ExpectedWorkloadScore,
    bool BandwidthHeavy,
    bool ImmediateGraphicsConsumer);

public sealed record ScheduledPass(
    string PassName,
    RenderGraphQueueClass Queue,
    bool Async,
    string Reason)
{
    public int QueueSubmissionIndex { get; init; }
    public int ExpectedWorkloadScore { get; init; }
    public bool BandwidthHeavy { get; init; }
}

public sealed record QueueSyncEdge(
    string Producer,
    string Consumer,
    RenderGraphQueueClass FromQueue,
    RenderGraphQueueClass ToQueue,
    ulong SignalValue,
    bool RequiresOwnershipTransfer);

public sealed record AsyncQueueDiagnostics(
    int AsyncScheduledPassCount,
    int GraphicsWaitCount,
    int ComputeWaitCount,
    int OwnershipTransferCount,
    int BandwidthHeavyAsyncPassCount,
    int ImmediateGraphicsWaitAvoidedCount,
    int TinyDispatchAvoidedCount);

public sealed record AsyncSchedulePlan(
    IReadOnlyList<ScheduledPass> Passes,
    IReadOnlyList<QueueSyncEdge> SyncEdges,
    string Diagnostic)
{
    public AsyncQueueDiagnostics QueueDiagnostics { get; init; } = new(0, 0, 0, 0, 0, 0, 0);
}

public static class AsyncComputeScheduler
{
    private const int ConservativeWorkloadThreshold = 100;
    private const int AggressiveWorkloadThreshold = 25;

    public static AsyncSchedulePlan Build(
        RenderGraphDeclarationPlan graph,
        AsyncComputeDeviceProfile device,
        AsyncComputeMode mode,
        IReadOnlyList<AsyncPassSchedulingHint> hints,
        IReadOnlyDictionary<string, AsyncComputeMode>? passOverrides = null)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (device == null)
            throw new ArgumentNullException(nameof(device));
        if (hints == null)
            throw new ArgumentNullException(nameof(hints));

        var hintByPass = hints.ToDictionary(hint => hint.PassName, StringComparer.Ordinal);
        var scheduled = new List<ScheduledPass>();
        var passByName = graph.Passes.ToDictionary(pass => pass.Name, StringComparer.Ordinal);
        var scheduledByName = new Dictionary<string, ScheduledPass>(StringComparer.Ordinal);
        var resourceQueues = new Dictionary<RenderGraphResourceHandle, RenderGraphQueueClass>();
        foreach (string passName in graph.Diagnostics.CompiledPassOrder)
        {
            RenderGraphPassDesc pass = passByName[passName];
            hintByPass.TryGetValue(pass.Name, out AsyncPassSchedulingHint? hint);
            AsyncComputeMode effectiveMode = passOverrides != null && passOverrides.TryGetValue(pass.Name, out AsyncComputeMode overrideMode)
                ? overrideMode
                : mode;
            ScheduledPass chosen = ChooseQueue(pass, hint, device, effectiveMode);
            if (chosen.Queue == RenderGraphQueueClass.Compute &&
                HasPreGraphicsSubmitConflict(pass, graph, scheduledByName, resourceQueues))
            {
                chosen = chosen with
                {
                    Queue = RenderGraphQueueClass.Graphics,
                    Async = false,
                    Reason = "single compute submit cannot wait for graphics producer"
                };
            }

            chosen = chosen with { QueueSubmissionIndex = scheduled.Count };
            scheduled.Add(chosen);
            scheduledByName[pass.Name] = chosen;
            TrackResourceQueues(pass, chosen.Queue, resourceQueues);
        }

        var byName = scheduled.ToDictionary(pass => pass.PassName, StringComparer.Ordinal);
        var syncEdges = new List<QueueSyncEdge>();
        ulong nextSignalValue = 1;
        foreach (string passName in graph.Diagnostics.CompiledPassOrder)
        {
            RenderGraphPassDesc pass = passByName[passName];
            ScheduledPass consumer = byName[pass.Name];
            foreach (string dependency in pass.DependsOn)
            {
                if (!byName.TryGetValue(dependency, out ScheduledPass? producer))
                    continue;
                if (producer.Queue == consumer.Queue)
                    continue;

                syncEdges.Add(new QueueSyncEdge(
                    producer.PassName,
                    consumer.PassName,
                    producer.Queue,
                    consumer.Queue,
                    nextSignalValue++,
                    device.RequiresOwnershipTransfer));
            }
        }

        string diagnostic = device.Explain(mode);
        AsyncQueueDiagnostics queueDiagnostics = BuildDiagnostics(scheduled, syncEdges);
        return new AsyncSchedulePlan(scheduled, syncEdges, diagnostic)
        {
            QueueDiagnostics = queueDiagnostics
        };
    }

    private static bool HasPreGraphicsSubmitConflict(
        RenderGraphPassDesc pass,
        RenderGraphDeclarationPlan graph,
        IReadOnlyDictionary<string, ScheduledPass> scheduledByName,
        IReadOnlyDictionary<RenderGraphResourceHandle, RenderGraphQueueClass> resourceQueues)
    {
        foreach (string dependency in pass.DependsOn)
        {
            if (!scheduledByName.TryGetValue(dependency, out ScheduledPass? producer))
                return true;
            if (producer.Queue == RenderGraphQueueClass.Graphics)
                return true;
        }

        return HasGraphicsResourceProducer(pass.Reads, resourceQueues) ||
               HasGraphicsResourceProducer(pass.Writes, resourceQueues) ||
               HasGraphicsResourceProducer(pass.ReadWrites, resourceQueues) ||
               HasFirstUseExternalRead(pass.Reads, graph, resourceQueues) ||
               HasFirstUseExternalRead(pass.ReadWrites, graph, resourceQueues);
    }

    private static bool HasFirstUseExternalRead(
        IReadOnlyList<RenderGraphResourceUse> uses,
        RenderGraphDeclarationPlan graph,
        IReadOnlyDictionary<RenderGraphResourceHandle, RenderGraphQueueClass> resourceQueues)
    {
        foreach (RenderGraphResourceUse use in uses)
        {
            if (resourceQueues.ContainsKey(use.Handle))
                continue;
            if (IsExternal(use.Handle, graph))
                return true;
        }

        return false;
    }

    private static bool IsExternal(RenderGraphResourceHandle handle, RenderGraphDeclarationPlan graph)
    {
        return handle.Kind switch
        {
            RenderGraphResourceKind.Image => graph.Images[handle.Index].Persistence == RenderGraphResourcePersistence.External,
            RenderGraphResourceKind.Buffer => graph.Buffers[handle.Index].Persistence == RenderGraphResourcePersistence.External,
            _ => false
        };
    }

    private static bool HasGraphicsResourceProducer(
        IReadOnlyList<RenderGraphResourceUse> uses,
        IReadOnlyDictionary<RenderGraphResourceHandle, RenderGraphQueueClass> resourceQueues)
    {
        foreach (RenderGraphResourceUse use in uses)
        {
            if (resourceQueues.TryGetValue(use.Handle, out RenderGraphQueueClass queue) &&
                queue == RenderGraphQueueClass.Graphics)
            {
                return true;
            }
        }

        return false;
    }

    private static void TrackResourceQueues(
        RenderGraphPassDesc pass,
        RenderGraphQueueClass queue,
        IDictionary<RenderGraphResourceHandle, RenderGraphQueueClass> resourceQueues)
    {
        Track(pass.Reads, queue, resourceQueues);
        Track(pass.Writes, queue, resourceQueues);
        Track(pass.ReadWrites, queue, resourceQueues);
    }

    private static void Track(
        IReadOnlyList<RenderGraphResourceUse> uses,
        RenderGraphQueueClass queue,
        IDictionary<RenderGraphResourceHandle, RenderGraphQueueClass> resourceQueues)
    {
        foreach (RenderGraphResourceUse use in uses)
            resourceQueues[use.Handle] = queue;
    }

    private static ScheduledPass ChooseQueue(
        RenderGraphPassDesc pass,
        AsyncPassSchedulingHint? hint,
        AsyncComputeDeviceProfile device,
        AsyncComputeMode mode)
    {
        if (mode == AsyncComputeMode.Disabled)
            return new ScheduledPass(pass.Name, RenderGraphQueueClass.Graphics, false, "async disabled");
        if (!device.HasDedicatedCompute)
            return new ScheduledPass(pass.Name, RenderGraphQueueClass.Graphics, false, device.Explain(mode));
        bool asyncEligible = hint?.AsyncEligible ?? pass.AsyncEligible;
        RenderGraphQueueClass preferredQueue = hint?.PreferredQueue ?? pass.PreferredQueue;
        int workload = hint?.ExpectedWorkloadScore ?? pass.ExpectedWorkloadScore;
        bool bandwidthHeavy = hint?.BandwidthHeavy ?? pass.BandwidthHeavy;
        bool immediateGraphicsConsumer = hint?.ImmediateGraphicsConsumer ??
            pass.DependencyUrgency == RenderGraphDependencyUrgency.ImmediateGraphicsConsumer;

        if (!pass.SupportedQueues.Contains(RenderGraphQueueClass.Compute))
            return new ScheduledPass(pass.Name, pass.Queue == RenderGraphQueueClass.Compute ? RenderGraphQueueClass.Graphics : pass.Queue, false, "pass does not declare compute queue support");
        if (!asyncEligible || preferredQueue != RenderGraphQueueClass.Compute)
            return new ScheduledPass(pass.Name, pass.Queue == RenderGraphQueueClass.Compute ? RenderGraphQueueClass.Graphics : pass.Queue, false, "pass is not async eligible");
        if (immediateGraphicsConsumer && mode == AsyncComputeMode.Conservative)
            return new ScheduledPass(pass.Name, RenderGraphQueueClass.Graphics, false, "conservative mode avoids immediate graphics waits");
        if (bandwidthHeavy && mode == AsyncComputeMode.Conservative)
            return new ScheduledPass(pass.Name, RenderGraphQueueClass.Graphics, false, "conservative mode avoids bandwidth-heavy overlap");

        int threshold = mode == AsyncComputeMode.Aggressive ? AggressiveWorkloadThreshold : ConservativeWorkloadThreshold;
        if (workload < threshold)
            return new ScheduledPass(pass.Name, RenderGraphQueueClass.Graphics, false, "workload below async threshold");

        return new ScheduledPass(pass.Name, RenderGraphQueueClass.Compute, true, "scheduled on async compute")
        {
            ExpectedWorkloadScore = workload,
            BandwidthHeavy = bandwidthHeavy
        };
    }

    private static AsyncQueueDiagnostics BuildDiagnostics(
        IReadOnlyList<ScheduledPass> scheduled,
        IReadOnlyList<QueueSyncEdge> syncEdges)
    {
        return new AsyncQueueDiagnostics(
            scheduled.Count(pass => pass.Async),
            syncEdges.Count(edge => edge.ToQueue == RenderGraphQueueClass.Graphics),
            syncEdges.Count(edge => edge.ToQueue == RenderGraphQueueClass.Compute),
            syncEdges.Count(edge => edge.RequiresOwnershipTransfer),
            scheduled.Count(pass => pass.Async && pass.BandwidthHeavy),
            scheduled.Count(pass => string.Equals(pass.Reason, "conservative mode avoids immediate graphics waits", StringComparison.Ordinal)),
            scheduled.Count(pass => string.Equals(pass.Reason, "workload below async threshold", StringComparison.Ordinal)));
    }
}
