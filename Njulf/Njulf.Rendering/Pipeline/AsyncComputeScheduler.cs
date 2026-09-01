using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public enum AsyncComputeQueue
    {
        Graphics,
        Compute
    }

    public sealed record AsyncComputeQueueCapabilities(
        bool HasIndependentComputeQueue,
        uint GraphicsQueueFamily,
        uint ComputeQueueFamily,
        QueueFlags GraphicsQueueFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit | QueueFlags.TransferBit,
        QueueFlags ComputeQueueFlags = QueueFlags.ComputeBit | QueueFlags.TransferBit)
    {
        /// <summary>
        /// True when compute work runs on a separately created queue submission stream.  The
        /// stream may come from the graphics family, in which case the planner emits ordinary
        /// memory/layout dependencies rather than queue-family ownership transfers.
        /// </summary>
        public bool UsesDistinctQueueFamilies => GraphicsQueueFamily != ComputeQueueFamily;

        /// <summary>Compatibility view for diagnostics that distinguish queue-family topology.</summary>
        public bool HasDedicatedComputeQueue => HasIndependentComputeQueue && UsesDistinctQueueFamilies;
    }

    public sealed record AsyncComputePathEligibility(
        AsyncComputePath Path,
        bool RequestedByFeature,
        bool AutoTimingEligible,
        AsyncComputePathStatus AutoTimingStatus,
        string Reason = "",
        bool IsAutoTimingProbe = false,
        bool CorrectnessCertified = true,
        bool ForceValidationAuthorized = true);

    public sealed record AsyncComputePassRequest(
        string Name,
        AsyncComputePath? Path,
        IReadOnlyList<RenderGraphResourceUsage> ResourceUsages,
        bool EnabledByFeatureIsolation = true,
        string AtomicGroup = "",
        bool WillExecute = true);

    public sealed record AsyncComputePathRuntimeStatus(
        AsyncComputePath Path,
        bool Requested,
        bool Supported,
        bool Eligible,
        bool Active,
        AsyncComputePathStatus Status,
        string Reason,
        IReadOnlyList<string> Passes);

    public sealed record AsyncComputeTimelineWait(
        ulong Value,
        PipelineStageFlags2 StageMask,
        bool IsSignalOrderingDependency = false);

    /// <summary>
    /// Explicit release/acquire halves of a queue handoff. Keeping these scopes separate prevents
    /// a destination usage from accidentally being reused as the source release scope when a
    /// resource has several graph consumers.
    /// </summary>
    public readonly record struct AsyncComputeReleaseScope(
        PipelineStageFlags2 StageMask,
        AccessFlags2 AccessMask,
        ImageLayout OldLayout,
        ImageLayout NewLayout,
        uint SourceQueueFamily,
        uint DestinationQueueFamily);

    public readonly record struct AsyncComputeAcquireScope(
        PipelineStageFlags2 StageMask,
        AccessFlags2 AccessMask,
        ImageLayout OldLayout,
        ImageLayout NewLayout,
        uint SourceQueueFamily,
        uint DestinationQueueFamily);

    /// <summary>
    /// One concrete cross-queue resource handoff. Distinct-family exclusive resources produce a
    /// matched release/acquire pair. Same-family and concurrent resources use the semaphore's
    /// memory dependency and emit only an acquire-side image layout transition when required.
    /// </summary>
    public sealed record QueueOwnershipTransfer(
        int Id,
        RenderGraphConcreteResourceBinding Binding,
        int SourceSegmentId,
        int DestinationSegmentId,
        AsyncComputeQueue SourceQueue,
        AsyncComputeQueue DestinationQueue,
        uint SourceQueueFamily,
        uint DestinationQueueFamily,
        PipelineStageFlags2 SourceStageMask,
        AccessFlags2 SourceAccessMask,
        PipelineStageFlags2 DestinationStageMask,
        AccessFlags2 DestinationAccessMask,
        ImageLayout OldLayout,
        ImageLayout NewLayout,
        bool RequiresQueueFamilyOwnershipTransfer,
        bool IsConcurrentResource)
    {
        /// <summary>
        /// Original binding identities represented by this transfer. Coalesced barriers use a
        /// synthetic range for Vulkan, while ownership and layout state must still be committed
        /// against every original concrete binding key.
        /// </summary>
        public IReadOnlyList<RenderGraphConcreteResourceBinding> ConstituentBindings { get; init; } =
            Array.Empty<RenderGraphConcreteResourceBinding>();

        public IReadOnlyList<RenderGraphConcreteResourceBinding> AllBindings =>
            ConstituentBindings.Count == 0 ? new[] { Binding } : ConstituentBindings;

        public bool IsImage => Binding.Kind == RenderGraphConcreteResourceKind.Image;
        public bool RequiresReleaseBarrier =>
            RequiresQueueFamilyOwnershipTransfer;
        public bool RequiresAcquireBarrier =>
            RequiresQueueFamilyOwnershipTransfer ||
            IsImage && AcquireOldLayout != AcquireNewLayout;
        public ulong TransferBytes => IsImage ? 0UL : Binding.ByteSize;
        public int TransferImageSubresources => IsImage ? CountImageSubresources(Binding.SubresourceRange) : 0;

        /// <summary>
        /// For a queue-family ownership transfer, both barriers carry the same image layout
        /// transition. Same-family and concurrent resources instead leave layout unchanged at
        /// release and perform the ordinary layout transition once at acquire.
        /// </summary>
        public ImageLayout ReleaseOldLayout => OldLayout;
        public ImageLayout ReleaseNewLayout => RequiresQueueFamilyOwnershipTransfer ? NewLayout : OldLayout;
        public ImageLayout AcquireOldLayout => OldLayout;
        public ImageLayout AcquireNewLayout => NewLayout;
        public AsyncComputeReleaseScope ReleaseScope => new(
            SourceStageMask,
            SourceAccessMask,
            ReleaseOldLayout,
            ReleaseNewLayout,
            SourceQueueFamily,
            DestinationQueueFamily);
        public AsyncComputeAcquireScope AcquireScope => new(
            DestinationStageMask,
            DestinationAccessMask,
            AcquireOldLayout,
            AcquireNewLayout,
            SourceQueueFamily,
            DestinationQueueFamily);

        private static int CountImageSubresources(ImageSubresourceRange range)
        {
            ulong count = (ulong)range.LevelCount * range.LayerCount;
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
    }

    public sealed record AsyncComputeSubmissionSegment(
        int Id,
        AsyncComputeQueue Queue,
        IReadOnlyList<string> Passes,
        IReadOnlyList<QueueOwnershipTransfer> AcquireTransfers,
        IReadOnlyList<QueueOwnershipTransfer> ReleaseTransfers,
        IReadOnlyList<AsyncComputeTimelineWait> TimelineWaits,
        ulong? TimelineSignalValue,
        bool AccessesSwapchain,
        bool IsTerminalGraphicsSegment);

    public sealed record AsyncComputeSubmissionPlan(
        bool Accepted,
        string FailureReason,
        ulong ResourcePlanGeneration,
        IReadOnlyList<AsyncComputeSubmissionSegment> Segments,
        IReadOnlyList<QueueOwnershipTransfer> Transfers,
        IReadOnlyList<AsyncComputePathRuntimeStatus> Paths)
    {
        public bool ContainsAsyncCompute => Segments.Any(segment =>
            segment.Queue == AsyncComputeQueue.Compute && segment.Passes.Count > 0);

        public int GraphicsSegmentCount => Segments.Count(segment => segment.Queue == AsyncComputeQueue.Graphics);
        public int ComputeSegmentCount => Segments.Count(segment => segment.Queue == AsyncComputeQueue.Compute);
        public int PlannedReleaseBarrierCount => Transfers.Count(transfer =>
            transfer.RequiresReleaseBarrier);
        public int PlannedAcquireBarrierCount => Transfers.Count(transfer =>
            transfer.RequiresAcquireBarrier);
        public int QueueFamilyOwnershipTransferCount => Transfers.Count(transfer => transfer.RequiresQueueFamilyOwnershipTransfer);
        public ulong TransferBytes => Transfers.Aggregate(0UL, (total, transfer) => checked(total + transfer.TransferBytes));
        public int TransferImageSubresources => Transfers.Sum(transfer => transfer.TransferImageSubresources);
        public IReadOnlyList<string> ActivePasses => Segments
            .Where(segment => segment.Queue == AsyncComputeQueue.Compute)
            .SelectMany(segment => segment.Passes)
            .ToArray();
    }

    public sealed record AsyncComputeSchedulerInput(
        AsyncComputeMode Mode,
        AsyncComputeQueueCapabilities QueueCapabilities,
        RenderGraphResourceBindings ResourceBindings,
        IReadOnlyList<AsyncComputePathEligibility> Paths,
        IReadOnlyList<AsyncComputePassRequest> Passes,
        int FrameIndex,
        ulong FirstTimelineValue)
    {
        public IReadOnlyList<AsyncComputePath> AllPaths { get; init; } = Enum.GetValues<AsyncComputePath>();
    }

    /// <summary>
    /// Compiles immutable graph declarations into queue submissions.  It does no Vulkan calls and
    /// therefore gives the renderer a complete all-or-nothing validation point before recording
    /// any async command buffer.
    /// </summary>
    public sealed class AsyncComputeScheduler
    {
        public AsyncComputeSubmissionPlan Compile(AsyncComputeSchedulerInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (input.ResourceBindings == null)
                throw new ArgumentNullException(nameof(input.ResourceBindings));
            if (input.FirstTimelineValue == 0)
                throw new ArgumentOutOfRangeException(nameof(input), "Timeline semaphore values start at one.");

            var eligibilityByPath = input.Paths.ToDictionary(item => item.Path);
            var passGroups = input.Passes
                .Where(pass => pass.Path.HasValue)
                .GroupBy(pass => pass.Path)
                .ToDictionary(group => group.Key!.Value, group => group.OrderBy(pass => pass.Name, StringComparer.Ordinal).ToArray());
            var statuses = new List<AsyncComputePathRuntimeStatus>(input.AllPaths.Count);
            var activePaths = new HashSet<AsyncComputePath>();

            foreach (AsyncComputePath path in input.AllPaths)
            {
                AsyncComputePathEligibility eligibility = eligibilityByPath.TryGetValue(path, out AsyncComputePathEligibility? supplied)
                    ? supplied
                    : new AsyncComputePathEligibility(path, RequestedByFeature: false, AutoTimingEligible: false, AsyncComputePathStatus.DisabledByFeature);
                IReadOnlyList<AsyncComputePassRequest> pathPasses = passGroups.TryGetValue(path, out AsyncComputePassRequest[]? requests)
                    ? requests
                    : Array.Empty<AsyncComputePassRequest>();
                AsyncComputePassRequest[] executablePathPasses = pathPasses
                    .Where(pass => pass.EnabledByFeatureIsolation && pass.WillExecute)
                    .ToArray();
                IReadOnlyList<string> passNames = executablePathPasses.Select(pass => pass.Name).ToArray();

                AsyncComputePathStatus status;
                string reason;
                bool eligible = false;
                if (input.Mode == AsyncComputeMode.Disabled)
                {
                    status = AsyncComputePathStatus.DisabledByPolicy;
                    reason = "Async compute mode is Disabled.";
                }
                else if (pathPasses.Count == 0)
                {
                    status = AsyncComputePathStatus.MissingResourcePlan;
                    reason = "The requested async path has no registered render-graph passes or concrete resource plan." +
                        (string.IsNullOrWhiteSpace(eligibility.Reason) ? string.Empty : $" {eligibility.Reason}");
                }
                else if (!eligibility.RequestedByFeature || executablePathPasses.Length == 0)
                {
                    status = AsyncComputePathStatus.DisabledByFeature;
                    reason = string.IsNullOrWhiteSpace(eligibility.Reason)
                        ? "The feature or its complete pass group is inactive this frame."
                        : eligibility.Reason;
                }
                else if (!input.QueueCapabilities.HasIndependentComputeQueue)
                {
                    status = AsyncComputePathStatus.UnsupportedQueue;
                    reason = "No separately created compute queue is available.";
                }
                else if (input.Mode == AsyncComputeMode.Auto && !eligibility.CorrectnessCertified)
                {
                    status = AsyncComputePathStatus.Uncertified;
                    reason = string.IsNullOrWhiteSpace(eligibility.Reason)
                        ? "Auto cannot submit this path until its correctness evidence is source-certified."
                        : eligibility.Reason;
                }
                else if (input.Mode == AsyncComputeMode.ForceEnabledForValidation && !eligibility.ForceValidationAuthorized)
                {
                    status = AsyncComputePathStatus.Uncertified;
                    reason = "Force mode requires an explicit atomic validation-path selector from the validation harness.";
                }
                else if (input.Mode == AsyncComputeMode.Auto && !eligibility.AutoTimingEligible && !eligibility.IsAutoTimingProbe)
                {
                    status = eligibility.AutoTimingStatus is AsyncComputePathStatus.PendingWarmup or AsyncComputePathStatus.NoMeasuredBenefit
                        ? eligibility.AutoTimingStatus
                        : AsyncComputePathStatus.NoMeasuredBenefit;
                    reason = string.IsNullOrWhiteSpace(eligibility.Reason)
                        ? "Auto mode has not measured a stable benefit for this path."
                        : eligibility.Reason;
                }
                else
                {
                    IReadOnlyList<string> errors = ValidateConcreteResources(input, executablePathPasses);
                    if (errors.Count > 0)
                    {
                        status = AsyncComputePathStatus.MissingResourcePlan;
                        reason = string.Join(" ", errors);
                    }
                    else
                    {
                        bool isTimingProbe = input.Mode == AsyncComputeMode.Auto && eligibility.IsAutoTimingProbe;
                        status = isTimingProbe
                            ? AsyncComputePathStatus.PendingWarmup
                            : AsyncComputePathStatus.Enabled;
                        reason = isTimingProbe
                            ? string.IsNullOrWhiteSpace(eligibility.Reason)
                                ? "Running one isolated Auto timing probe after resource-plan validation."
                                : eligibility.Reason
                            : input.Mode == AsyncComputeMode.ForceEnabledForValidation
                                ? "Forced for validation after capability and resource-plan validation."
                                : "Enabled by measured Auto policy.";
                        eligible = true;
                        activePaths.Add(path);
                    }
                }

                statuses.Add(new AsyncComputePathRuntimeStatus(
                    path,
                    input.Mode != AsyncComputeMode.Disabled && eligibility.RequestedByFeature,
                    input.QueueCapabilities.HasIndependentComputeQueue,
                    eligible,
                    eligible,
                    status,
                    reason,
                    passNames));
            }

            if (activePaths.Count == 0)
            {
                return new AsyncComputeSubmissionPlan(
                    Accepted: true,
                    FailureReason: string.Empty,
                    input.ResourceBindings.Generation,
                    Array.Empty<AsyncComputeSubmissionSegment>(),
                    Array.Empty<QueueOwnershipTransfer>(),
                    statuses);
            }

            // Feature-isolated passes are not recorded at all. Keeping their declarations in a
            // split plan would manufacture producers/consumers and queue waits for work the
            // renderer intentionally skipped, shrinking overlap and obscuring diagnostics.
            AsyncComputePassRequest[] executablePasses = input.Passes
                .Where(pass => pass.EnabledByFeatureIsolation && pass.WillExecute)
                .ToArray();

            try
            {
                ValidateAtomicComputeGroups(executablePasses, activePaths);
            }
            catch (InvalidOperationException exception)
            {
                return new AsyncComputeSubmissionPlan(
                    Accepted: false,
                    FailureReason: exception.Message,
                    input.ResourceBindings.Generation,
                    Array.Empty<AsyncComputeSubmissionSegment>(),
                    Array.Empty<QueueOwnershipTransfer>(),
                    MarkValidationFallback(statuses, activePaths, exception.Message));
            }

            List<MutableSegment> segments = CreateSegments(executablePasses, activePaths);
            if (segments.Count == 0 || !segments.Any(segment => segment.Queue == AsyncComputeQueue.Compute && segment.Passes.Count > 0))
            {
                return new AsyncComputeSubmissionPlan(
                    Accepted: false,
                    FailureReason: "The scheduler selected an async path but could not create a compute segment.",
                    input.ResourceBindings.Generation,
                    Array.Empty<AsyncComputeSubmissionSegment>(),
                    Array.Empty<QueueOwnershipTransfer>(),
                    MarkValidationFallback(statuses, activePaths, "No executable compute segment was produced."));
            }

            List<QueueOwnershipTransfer> transfers;
            try
            {
                transfers = BuildTransfers(input, segments);
                ValidateTransferQueueScopes(input.QueueCapabilities, transfers);
                QueueOwnershipTransferValidator.Validate(transfers, segments, requireSemaphoreEdges: false);
            }
            catch (InvalidOperationException exception)
            {
                return new AsyncComputeSubmissionPlan(
                    Accepted: false,
                    FailureReason: exception.Message,
                    input.ResourceBindings.Generation,
                    Array.Empty<AsyncComputeSubmissionSegment>(),
                    Array.Empty<QueueOwnershipTransfer>(),
                    MarkValidationFallback(statuses, activePaths, exception.Message));
            }

            try
            {
                AssignTimelineDependencies(input.FirstTimelineValue, segments, transfers);
                QueueOwnershipTransferValidator.Validate(transfers, segments, requireSemaphoreEdges: true);
            }
            catch (InvalidOperationException exception)
            {
                return new AsyncComputeSubmissionPlan(
                    Accepted: false,
                    FailureReason: exception.Message,
                    input.ResourceBindings.Generation,
                    Array.Empty<AsyncComputeSubmissionSegment>(),
                    Array.Empty<QueueOwnershipTransfer>(),
                    MarkValidationFallback(statuses, activePaths, exception.Message));
            }

            var compiledSegments = new List<AsyncComputeSubmissionSegment>(segments.Count);
            foreach (MutableSegment segment in segments)
            {
                compiledSegments.Add(new AsyncComputeSubmissionSegment(
                    segment.Id,
                    segment.Queue,
                    segment.Passes.Select(pass => pass.Name).ToArray(),
                    segment.Acquires.ToArray(),
                    segment.Releases.ToArray(),
                    CoalesceWaits(segment.Waits),
                    segment.SignalValue,
                    segment.Passes.Any(pass => pass.ResourceUsages.Any(usage => usage.Resource == RenderGraphResourceId.SwapchainColor)),
                    segment.IsTerminalGraphics));
            }

            // imageAvailable and the frame fence belong exclusively to the terminal graphics
            // submission.  A declaration that touches the acquired swapchain image before that
            // point is not an execution-time concern: reject its immutable plan here, before a
            // compute command buffer is begun or any ownership state can be committed.
            AsyncComputeSubmissionSegment? earlySwapchainSegment = compiledSegments.FirstOrDefault(segment =>
                segment.AccessesSwapchain && !segment.IsTerminalGraphicsSegment);
            if (earlySwapchainSegment != null)
            {
                string passNames = string.Join(", ", earlySwapchainSegment.Passes);
                string failureReason =
                    $"Async compute plan segment {earlySwapchainSegment.Id} ({earlySwapchainSegment.Queue}: {passNames}) " +
                    "would access the acquired swapchain image before the terminal graphics submission.";
                return new AsyncComputeSubmissionPlan(
                    Accepted: false,
                    FailureReason: failureReason,
                    input.ResourceBindings.Generation,
                    Array.Empty<AsyncComputeSubmissionSegment>(),
                    Array.Empty<QueueOwnershipTransfer>(),
                    MarkValidationFallback(statuses, activePaths, failureReason));
            }

            return new AsyncComputeSubmissionPlan(
                Accepted: true,
                FailureReason: string.Empty,
                input.ResourceBindings.Generation,
                compiledSegments,
                transfers,
                statuses);
        }

        private static IReadOnlyList<string> ValidateConcreteResources(
            AsyncComputeSchedulerInput input,
            IReadOnlyList<AsyncComputePassRequest> passes)
        {
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (AsyncComputePassRequest pass in passes)
            {
                if (pass.ResourceUsages.Count == 0)
                {
                    string error = $"Pass '{pass.Name}' has no concrete resource usages and cannot run asynchronously.";
                    if (seen.Add(error))
                        errors.Add(error);
                    continue;
                }

                foreach (RenderGraphResourceUsage usage in pass.ResourceUsages)
                {
                    if (usage.StageMask == PipelineStageFlags2.None || usage.AccessMask == AccessFlags2.None)
                    {
                        string error = $"Pass '{pass.Name}' has no concrete stage/access intent for '{usage.Resource}'.";
                        if (seen.Add(error))
                            errors.Add(error);
                    }

                    var capabilities = new QueueStageCapabilities(input.QueueCapabilities.ComputeQueueFlags);
                    if (!capabilities.SupportsScope(usage.StageMask, usage.AccessMask))
                    {
                        string error =
                            $"Pass '{pass.Name}' declares unsupported compute-queue scope for '{usage.Resource}': " +
                            $"family={input.QueueCapabilities.ComputeQueueFamily}, flags={input.QueueCapabilities.ComputeQueueFlags}, " +
                            $"stage={usage.StageMask}, access={usage.AccessMask} (VUID-vkCmdPipelineBarrier2-commandBuffer-09675/09676).";
                        if (seen.Add(error))
                            errors.Add(error);
                    }

                    IReadOnlyList<RenderGraphConcreteResourceBinding> bindings =
                        input.ResourceBindings.GetBindings(
                            usage.Resource,
                            input.FrameIndex,
                            usage.HistoryBinding);
                    if (bindings.Count == 0)
                    {
                        string error = $"Pass '{pass.Name}' has no concrete binding for '{usage.Resource}'.";
                        if (seen.Add(error))
                            errors.Add(error);
                        continue;
                    }

                    foreach (RenderGraphConcreteResourceBinding binding in bindings)
                    {
                        if (!input.ResourceBindings.IsCurrent(binding))
                        {
                            string error = $"Pass '{pass.Name}' resolved stale binding '{binding.Name}' for '{usage.Resource}'.";
                            if (seen.Add(error))
                                errors.Add(error);
                        }
                        if (!binding.PermittedQueueFamilies.Contains(input.QueueCapabilities.ComputeQueueFamily))
                        {
                            string error =
                                $"Pass '{pass.Name}' cannot use binding '{binding.Name}' for '{usage.Resource}' on compute queue family {input.QueueCapabilities.ComputeQueueFamily}.";
                            if (seen.Add(error))
                                errors.Add(error);
                        }
                    }
                }
            }

            return errors;
        }

        private static void ValidateTransferQueueScopes(
            AsyncComputeQueueCapabilities queues,
            IReadOnlyList<QueueOwnershipTransfer> transfers)
        {
            foreach (QueueOwnershipTransfer transfer in transfers)
            {
                QueueFlags sourceFlags = transfer.SourceQueue == AsyncComputeQueue.Graphics
                    ? queues.GraphicsQueueFlags
                    : queues.ComputeQueueFlags;
                QueueFlags destinationFlags = transfer.DestinationQueue == AsyncComputeQueue.Graphics
                    ? queues.GraphicsQueueFlags
                    : queues.ComputeQueueFlags;
                var source = new QueueStageCapabilities(sourceFlags);
                var destination = new QueueStageCapabilities(destinationFlags);
                if (!source.SupportsScope(transfer.SourceStageMask, transfer.SourceAccessMask))
                {
                    throw new InvalidOperationException(
                        $"Transfer {transfer.Id} release for '{transfer.Binding.Name}' uses unsupported scope " +
                        $"on {transfer.SourceQueue} family {transfer.SourceQueueFamily} ({sourceFlags}): " +
                        $"stage={transfer.SourceStageMask}, access={transfer.SourceAccessMask} " +
                        "(VUID-vkCmdPipelineBarrier2-commandBuffer-09675/09676).");
                }
                if (!destination.SupportsScope(transfer.DestinationStageMask, transfer.DestinationAccessMask))
                {
                    throw new InvalidOperationException(
                        $"Transfer {transfer.Id} acquire for '{transfer.Binding.Name}' uses unsupported scope " +
                        $"on {transfer.DestinationQueue} family {transfer.DestinationQueueFamily} ({destinationFlags}): " +
                        $"stage={transfer.DestinationStageMask}, access={transfer.DestinationAccessMask} " +
                        "(VUID-vkCmdPipelineBarrier2-commandBuffer-09675/09676).");
                }
            }
        }

        private static List<MutableSegment> CreateSegments(
            IReadOnlyList<AsyncComputePassRequest> passes,
            IReadOnlySet<AsyncComputePath> activePaths)
        {
            var segments = new List<MutableSegment>();
            var graphics = new MutableSegment(0, AsyncComputeQueue.Graphics);
            segments.Add(graphics);
            MutableSegment current = graphics;
            // A graphics submission can overlap a preceding compute segment until its first
            // actual consumer. Track abstract resources currently last used by compute and split
            // a graphics run at that first consumer, rather than attaching the timeline wait to
            // unrelated graphics work at the beginning of the run.
            var computeOwnedResources = new HashSet<RenderGraphResourceId>();
            bool currentGraphicsRunWaitsForCompute = false;

            foreach (AsyncComputePassRequest pass in passes)
            {
                bool onCompute = pass.Path.HasValue && activePaths.Contains(pass.Path.Value);
                AsyncComputeQueue desiredQueue = onCompute ? AsyncComputeQueue.Compute : AsyncComputeQueue.Graphics;
                bool consumesComputeOwnedResource =
                    desiredQueue == AsyncComputeQueue.Graphics &&
                    pass.ResourceUsages.Any(usage =>
                        computeOwnedResources.Contains(usage.Resource));
                if (current.Queue != desiredQueue)
                {
                    current = new MutableSegment(segments.Count, desiredQueue);
                    segments.Add(current);
                    currentGraphicsRunWaitsForCompute =
                        desiredQueue == AsyncComputeQueue.Graphics &&
                        consumesComputeOwnedResource;
                }
                else if (desiredQueue == AsyncComputeQueue.Graphics &&
                         current.Passes.Count > 0 &&
                         !currentGraphicsRunWaitsForCompute &&
                         consumesComputeOwnedResource)
                {
                    // Keep the already-recorded unrelated graphics work free of the compute
                    // wait. BuildTransfers will attach the acquire and timeline edge to this new
                    // segment because it contains the first concrete consumer. Every later pass
                    // on the same graphics queue is ordered after that wait, so it must remain in
                    // this segment even when it consumes another compute-owned allocation.
                    current = new MutableSegment(segments.Count, AsyncComputeQueue.Graphics);
                    segments.Add(current);
                    currentGraphicsRunWaitsForCompute = true;
                }

                current.Passes.Add(pass);
                if (desiredQueue == AsyncComputeQueue.Compute)
                {
                    currentGraphicsRunWaitsForCompute = false;
                    foreach (RenderGraphResourceUsage usage in pass.ResourceUsages)
                        computeOwnedResources.Add(usage.Resource);
                }
                else
                {
                    currentGraphicsRunWaitsForCompute |=
                        consumesComputeOwnedResource;
                    foreach (RenderGraphResourceUsage usage in pass.ResourceUsages)
                        computeOwnedResources.Remove(usage.Resource);
                }
            }

            // A terminal graphics submission owns the frame fence and consumes imageAvailable. It
            // also gives every compute-owned exclusive resource a concrete return path before the
            // per-frame command pools become reusable.
            bool currentAccessesSwapchain = current.Queue == AsyncComputeQueue.Graphics &&
                current.Passes.Any(pass => pass.ResourceUsages.Any(usage =>
                    usage.Resource == RenderGraphResourceId.SwapchainColor));
            if (current.Queue != AsyncComputeQueue.Graphics ||
                (computeOwnedResources.Count > 0 && !currentAccessesSwapchain))
            {
                current = new MutableSegment(segments.Count, AsyncComputeQueue.Graphics);
                segments.Add(current);
            }
            // If the final graphics run already owns swapchain presentation, it
            // must also be the fence-bearing terminal submission. BuildTransfers
            // can return unrelated compute-only resources to this segment just as
            // it does to an otherwise empty terminal segment.
            current.IsTerminalGraphics = true;
            return segments;
        }

        /// <summary>
        /// An atomic compute path is only valid as one submission while its intermediate
        /// resources remain queue-local. If a future pipeline edit inserts graphics work into
        /// an atomic group, reject the plan instead of silently splitting it across queues.
        /// </summary>
        private static void ValidateAtomicComputeGroups(
            IReadOnlyList<AsyncComputePassRequest> passes,
            IReadOnlySet<AsyncComputePath> activePaths)
        {
            var spans = new Dictionary<string, (int First, int Last)>(StringComparer.Ordinal);
            for (int index = 0; index < passes.Count; index++)
            {
                AsyncComputePassRequest pass = passes[index];
                if (!pass.Path.HasValue ||
                    !activePaths.Contains(pass.Path.Value) ||
                    string.IsNullOrWhiteSpace(pass.AtomicGroup))
                {
                    continue;
                }

                if (spans.TryGetValue(pass.AtomicGroup, out (int First, int Last) span))
                    spans[pass.AtomicGroup] = (span.First, index);
                else
                    spans.Add(pass.AtomicGroup, (index, index));
            }

            foreach (KeyValuePair<string, (int First, int Last)> entry in spans)
            {
                string group = entry.Key;
                int first = entry.Value.First;
                int last = entry.Value.Last;
                for (int index = first; index <= last; index++)
                {
                    AsyncComputePassRequest pass = passes[index];
                    if (pass.Path.HasValue &&
                        activePaths.Contains(pass.Path.Value) &&
                        string.Equals(pass.AtomicGroup, group, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Atomic async group '{group}' is not contiguous: '{passes[index].Name}' splits its compute passes.");
                }
            }
        }

        private static List<QueueOwnershipTransfer> BuildTransfers(
            AsyncComputeSchedulerInput input,
            IReadOnlyList<MutableSegment> segments)
        {
            var transfers = new List<QueueOwnershipTransfer>();
            var lastUses = new Dictionary<RenderGraphAllocationIdentity, ResourceUse>();
            int nextTransferId = 1;
            MutableSegment terminalGraphics = segments[^1];

            foreach (MutableSegment segment in segments)
            {
                foreach (AsyncComputePassRequest pass in segment.Passes)
                {
                    foreach (RenderGraphResourceUsage usage in pass.ResourceUsages)
                    {
                        IReadOnlyList<RenderGraphConcreteResourceBinding> bindings =
                            input.ResourceBindings.GetBindings(
                                usage.Resource,
                                input.FrameIndex,
                                usage.HistoryBinding);
                        foreach (RenderGraphConcreteResourceBinding binding in bindings)
                        {
                            if (lastUses.TryGetValue(binding.AllocationIdentity, out ResourceUse previous))
                            {
                                if (previous.Segment.Id != segment.Id && previous.Segment.Queue != segment.Queue)
                                {
                                    QueueOwnershipTransfer transfer = CreateTransfer(
                                        nextTransferId++,
                                        binding,
                                        previous.Segment,
                                        segment,
                                        previous.Usage,
                                        usage,
                                        input.QueueCapabilities,
                                        input.ResourceBindings.GetCurrentLayout(binding));
                                    transfers.Add(transfer);
                                    previous.Segment.Releases.Add(transfer);
                                    segment.Acquires.Add(transfer);
                                }
                            }
                            else
                            {
                                uint? owner = input.ResourceBindings.GetCurrentOwner(binding);
                                if (owner.HasValue && owner.Value != QueueFamily(segment.Queue, input.QueueCapabilities))
                                {
                                    if (owner.Value != input.QueueCapabilities.GraphicsQueueFamily)
                                    {
                                        throw new InvalidOperationException(
                                            $"Binding '{binding.Name}' is owned by queue family {owner.Value} before frame planning; no source submission can release it.");
                                    }

                                    MutableSegment source = FindPriorGraphicsSegment(segments, segment.Id);
                                    QueueOwnershipTransfer transfer = CreateTransfer(
                                        nextTransferId++,
                                        binding,
                                        source,
                                        segment,
                                        InitialUsage(
                                            binding,
                                            input.ResourceBindings.GetCurrentLayout(binding)),
                                        usage,
                                        input.QueueCapabilities,
                                        input.ResourceBindings.GetCurrentLayout(binding));
                                    transfers.Add(transfer);
                                    source.Releases.Add(transfer);
                                    segment.Acquires.Add(transfer);
                                }
                            }

                            lastUses[binding.AllocationIdentity] = new ResourceUse(segment, usage, binding);
                        }
                    }
                }
            }

            // Frame-local resources must be returned to graphics before the only in-flight fence is
            // signalled. Otherwise the next frame could reuse an upload or command-pool resource
            // while the compute family still owns it.
            foreach ((RenderGraphAllocationIdentity _, ResourceUse lastUse) in lastUses)
            {
                if (lastUse.Segment.Queue != AsyncComputeQueue.Compute)
                    continue;

                QueueOwnershipTransfer transfer = CreateTransfer(
                    nextTransferId++,
                    lastUse.Binding,
                    lastUse.Segment,
                    terminalGraphics,
                    lastUse.Usage,
                    FinalGraphicsUsage(
                        lastUse.Binding,
                        lastUse.Usage,
                        input.ResourceBindings.GetCurrentLayout(lastUse.Binding)),
                    input.QueueCapabilities,
                    input.ResourceBindings.GetCurrentLayout(lastUse.Binding));
                transfers.Add(transfer);
                lastUse.Segment.Releases.Add(transfer);
                terminalGraphics.Acquires.Add(transfer);
            }

            return QueueOwnershipTransferCoalescer.Coalesce(transfers, segments);
        }

        private static MutableSegment FindPriorGraphicsSegment(IReadOnlyList<MutableSegment> segments, int beforeSegmentId)
        {
            for (int i = beforeSegmentId - 1; i >= 0; i--)
            {
                if (segments[i].Queue == AsyncComputeQueue.Graphics)
                    return segments[i];
            }

            throw new InvalidOperationException("A compute segment has no graphics producer segment for its initial ownership acquire.");
        }

        private static QueueOwnershipTransfer CreateTransfer(
            int id,
            RenderGraphConcreteResourceBinding binding,
            MutableSegment source,
            MutableSegment destination,
            RenderGraphResourceUsage sourceUsage,
            RenderGraphResourceUsage destinationUsage,
            AsyncComputeQueueCapabilities queues,
            ImageLayout currentLayout)
        {
            uint sourceFamily = QueueFamily(source.Queue, queues);
            uint destinationFamily = QueueFamily(destination.Queue, queues);
            bool concurrent = binding.SharingMode == SharingMode.Concurrent;
            if (!binding.PermittedQueueFamilies.Contains(sourceFamily) ||
                !binding.PermittedQueueFamilies.Contains(destinationFamily))
            {
                throw new InvalidOperationException(
                    $"Binding '{binding.Name}' cannot transfer from queue family {sourceFamily} to {destinationFamily}; " +
                    "one or both families are not permitted by its concrete resource plan.");
            }
            bool needsOwnership = !concurrent && sourceFamily != destinationFamily;
            return new QueueOwnershipTransfer(
                id,
                binding,
                source.Id,
                destination.Id,
                source.Queue,
                destination.Queue,
                sourceFamily,
                destinationFamily,
                ResolveSourceStage(sourceUsage, binding),
                ResolveSourceAccess(sourceUsage, binding),
                ResolveDestinationStage(destinationUsage, binding),
                ResolveDestinationAccess(destinationUsage, binding),
                binding.Kind == RenderGraphConcreteResourceKind.Image ? ResolveReleaseLayout(sourceUsage, currentLayout) : ImageLayout.Undefined,
                binding.Kind == RenderGraphConcreteResourceKind.Image ? ResolveLayout(destinationUsage, currentLayout) : ImageLayout.Undefined,
                needsOwnership,
                concurrent)
            {
                ConstituentBindings = new[] { binding }
            };
        }

        private static void AssignTimelineDependencies(
            ulong firstTimelineValue,
            IReadOnlyList<MutableSegment> segments,
            IReadOnlyList<QueueOwnershipTransfer> transfers)
        {
            var byId = segments.ToDictionary(segment => segment.Id);
            foreach (QueueOwnershipTransfer transfer in transfers)
            {
                if (transfer.SourceQueue == transfer.DestinationQueue)
                    continue;
                byId[transfer.SourceSegmentId].SignalsTimeline = true;
            }

            ulong nextValue = firstTimelineValue;
            foreach (MutableSegment segment in segments)
            {
                if (!segment.SignalsTimeline)
                    continue;
                segment.SignalValue = nextValue++;
            }

            foreach (QueueOwnershipTransfer transfer in transfers)
            {
                if (transfer.SourceQueue == transfer.DestinationQueue)
                    continue;

                MutableSegment source = byId[transfer.SourceSegmentId];
                MutableSegment destination = byId[transfer.DestinationSegmentId];
                if (!source.SignalValue.HasValue)
                    throw new InvalidOperationException($"Transfer {transfer.Id} has no timeline signal on source segment {source.Id}.");
                destination.Waits.Add(new AsyncComputeTimelineWait(
                    source.SignalValue.Value,
                    transfer.DestinationStageMask));
            }

            // Timeline semaphore values are globally monotonic even when different queues can
            // execute concurrently. A concrete handoff wait already orders the destination's
            // later signal, so add an AllCommands ordering dependency only for an otherwise
            // independent cross-queue submission. This preserves the earliest real-consumer
            // stage for normal producer/consumer pairs.
            ulong previousSignal = 0;
            AsyncComputeQueue previousSignalQueue = AsyncComputeQueue.Graphics;
            foreach (MutableSegment segment in segments)
            {
                if (!segment.SignalValue.HasValue)
                    continue;

                if (previousSignal != 0 &&
                    previousSignalQueue != segment.Queue &&
                    !segment.Waits.Any(wait => wait.Value == previousSignal))
                {
                    segment.Waits.Add(new AsyncComputeTimelineWait(
                        previousSignal,
                        PipelineStageFlags2.AllCommandsBit,
                        IsSignalOrderingDependency: true));
                }

                previousSignal = segment.SignalValue.Value;
                previousSignalQueue = segment.Queue;
            }

            // A frame fence belongs to the terminal graphics submit. Make that submit wait for the
            // latest compute signal when no concrete terminal handoff already provides the wait.
            // Do not add an AllCommands duplicate to a real first-consumer wait: merging it would
            // unnecessarily stall unrelated work at the start of the terminal graphics segment.
            ulong lastComputeSignal = 0;
            foreach (MutableSegment segment in segments)
            {
                if (segment.Queue == AsyncComputeQueue.Compute && segment.SignalValue.GetValueOrDefault() > lastComputeSignal)
                    lastComputeSignal = segment.SignalValue!.Value;
            }
            if (lastComputeSignal != 0)
            {
                MutableSegment terminal = segments[^1];
                if (!terminal.Waits.Any(wait => wait.Value == lastComputeSignal))
                {
                    terminal.Waits.Add(new AsyncComputeTimelineWait(lastComputeSignal, PipelineStageFlags2.AllCommandsBit));
                }
            }
        }

        private static IReadOnlyList<AsyncComputeTimelineWait> CoalesceWaits(IReadOnlyList<AsyncComputeTimelineWait> waits)
        {
            if (waits.Count <= 1)
                return waits.ToArray();

            var stages = new Dictionary<ulong, (PipelineStageFlags2 Stage, bool SignalOrdering)>();
            foreach (AsyncComputeTimelineWait wait in waits)
            {
                if (stages.TryGetValue(wait.Value, out (PipelineStageFlags2 Stage, bool SignalOrdering) existing))
                {
                    stages[wait.Value] = (existing.Stage | wait.StageMask, existing.SignalOrdering || wait.IsSignalOrderingDependency);
                }
                else
                {
                    stages.Add(wait.Value, (wait.StageMask, wait.IsSignalOrderingDependency));
                }
            }

            return stages
                .OrderBy(pair => pair.Key)
                .Select(pair => new AsyncComputeTimelineWait(pair.Key, pair.Value.Stage, pair.Value.SignalOrdering))
                .ToArray();
        }

        private static IReadOnlyList<AsyncComputePathRuntimeStatus> MarkValidationFallback(
            IReadOnlyList<AsyncComputePathRuntimeStatus> statuses,
            IReadOnlySet<AsyncComputePath> activePaths,
            string reason)
        {
            return statuses.Select(status => activePaths.Contains(status.Path)
                ? status with
                {
                    Active = false,
                    Eligible = false,
                    Status = AsyncComputePathStatus.ValidationFallback,
                    Reason = reason
                }
                : status).ToArray();
        }

        private static uint QueueFamily(AsyncComputeQueue queue, AsyncComputeQueueCapabilities capabilities) =>
            queue == AsyncComputeQueue.Graphics
                ? capabilities.GraphicsQueueFamily
                : capabilities.ComputeQueueFamily;

        private static RenderGraphResourceUsage InitialUsage(
            RenderGraphConcreteResourceBinding binding,
            ImageLayout currentLayout) =>
            new(
                binding.Resource,
                RenderGraphResourceAccess.ReadWrite,
                binding.InitialStageMask != PipelineStageFlags2.None
                    ? binding.InitialStageMask
                    : PipelineStageFlags2.AllCommandsBit,
                binding.InitialAccessMask != AccessFlags2.None
                    ? binding.InitialAccessMask
                    : AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                currentLayout,
                RenderGraphQueueIntent.Graphics);

        private static RenderGraphResourceUsage FinalGraphicsUsage(
            RenderGraphConcreteResourceBinding binding,
            RenderGraphResourceUsage sourceUsage,
            ImageLayout currentLayout) =>
            new(
                binding.Resource,
                RenderGraphResourceAccess.Read,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.MemoryReadBit,
                ResolveReleaseLayout(sourceUsage, currentLayout),
                RenderGraphQueueIntent.Graphics);

        private static PipelineStageFlags2 ResolveSourceStage(RenderGraphResourceUsage usage, RenderGraphConcreteResourceBinding binding) =>
            usage.StageMask != PipelineStageFlags2.None
                ? usage.StageMask
                : binding.Kind == RenderGraphConcreteResourceKind.Image
                    ? PipelineStageFlags2.AllCommandsBit
                    : PipelineStageFlags2.AllCommandsBit;

        private static AccessFlags2 ResolveSourceAccess(RenderGraphResourceUsage usage, RenderGraphConcreteResourceBinding binding) =>
            usage.AccessMask != AccessFlags2.None
                ? usage.AccessMask
                : binding.Kind == RenderGraphConcreteResourceKind.Image
                    ? AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
                    : AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;

        private static PipelineStageFlags2 ResolveDestinationStage(RenderGraphResourceUsage usage, RenderGraphConcreteResourceBinding binding) =>
            ResolveSourceStage(usage, binding);

        private static AccessFlags2 ResolveDestinationAccess(RenderGraphResourceUsage usage, RenderGraphConcreteResourceBinding binding) =>
            ResolveSourceAccess(usage, binding);

        private static ImageLayout ResolveLayout(RenderGraphResourceUsage usage, ImageLayout fallback) =>
            usage.ImageLayout == ImageLayout.Undefined ? fallback : usage.ImageLayout;

        private static ImageLayout ResolveReleaseLayout(RenderGraphResourceUsage usage, ImageLayout fallback) =>
            usage.FinalImageLayout != ImageLayout.Undefined
                ? usage.FinalImageLayout
                : ResolveLayout(usage, fallback);

        private sealed class MutableSegment
        {
            public MutableSegment(int id, AsyncComputeQueue queue)
            {
                Id = id;
                Queue = queue;
            }

            public int Id { get; }
            public AsyncComputeQueue Queue { get; }
            public List<AsyncComputePassRequest> Passes { get; } = new();
            public List<QueueOwnershipTransfer> Acquires { get; } = new();
            public List<QueueOwnershipTransfer> Releases { get; } = new();
            public List<AsyncComputeTimelineWait> Waits { get; } = new();
            public bool SignalsTimeline { get; set; }
            public ulong? SignalValue { get; set; }
            public bool IsTerminalGraphics { get; set; }
        }

        private readonly record struct ResourceUse(
            MutableSegment Segment,
            RenderGraphResourceUsage Usage,
            RenderGraphConcreteResourceBinding Binding);

        private static class QueueOwnershipTransferValidator
        {
            public static void Validate(
                IReadOnlyList<QueueOwnershipTransfer> transfers,
                IReadOnlyList<MutableSegment> segments,
                bool requireSemaphoreEdges)
            {
                var byId = segments.ToDictionary(segment => segment.Id);
                var seenIds = new HashSet<int>();
                foreach (QueueOwnershipTransfer transfer in transfers)
                {
                    if (!seenIds.Add(transfer.Id))
                        throw new InvalidOperationException($"Queue transfer id {transfer.Id} is duplicated.");
                    if (!byId.TryGetValue(transfer.SourceSegmentId, out MutableSegment? source) ||
                        !byId.TryGetValue(transfer.DestinationSegmentId, out MutableSegment? destination))
                    {
                        throw new InvalidOperationException($"Queue transfer {transfer.Id} targets a missing segment.");
                    }
                    if (transfer.SourceSegmentId >= transfer.DestinationSegmentId)
                        throw new InvalidOperationException($"Queue transfer {transfer.Id} does not make forward scheduling progress.");
                    if (transfer.SourceQueue != source.Queue || transfer.DestinationQueue != destination.Queue)
                        throw new InvalidOperationException($"Queue transfer {transfer.Id} queue metadata disagrees with its segments.");
                    if (source.Releases.Count(item => item.Id == transfer.Id) != 1 ||
                        destination.Acquires.Count(item => item.Id == transfer.Id) != 1)
                    {
                        throw new InvalidOperationException($"Queue transfer {transfer.Id} does not have exactly one release and acquire.");
                    }
                    if (transfer.RequiresQueueFamilyOwnershipTransfer &&
                        (transfer.SourceQueueFamily == transfer.DestinationQueueFamily || transfer.IsConcurrentResource))
                    {
                        throw new InvalidOperationException($"Queue transfer {transfer.Id} has invalid ownership-transfer family metadata.");
                    }
                    if (!transfer.RequiresQueueFamilyOwnershipTransfer &&
                        !transfer.IsConcurrentResource &&
                        transfer.SourceQueueFamily != transfer.DestinationQueueFamily)
                    {
                        throw new InvalidOperationException($"Exclusive cross-family transfer {transfer.Id} omitted queue ownership transfer metadata.");
                    }
                    if (transfer.AllBindings.Any(binding =>
                            !binding.PermittedQueueFamilies.Contains(transfer.SourceQueueFamily) ||
                            !binding.PermittedQueueFamilies.Contains(transfer.DestinationQueueFamily)))
                    {
                        throw new InvalidOperationException(
                            $"Queue transfer {transfer.Id} references a queue family outside one or more concrete binding plans.");
                    }
                    if (transfer.IsImage && transfer.OldLayout == ImageLayout.Undefined && transfer.NewLayout == ImageLayout.Undefined)
                    {
                        throw new InvalidOperationException($"Image transfer {transfer.Id} has no compatible layout plan.");
                    }

                    if (requireSemaphoreEdges && transfer.SourceQueue != transfer.DestinationQueue)
                    {
                        if (!source.SignalValue.HasValue ||
                            !destination.Waits.Any(wait => wait.Value == source.SignalValue.Value))
                        {
                            throw new InvalidOperationException(
                                $"Queue transfer {transfer.Id} has no semaphore dependency from segment {source.Id} to {destination.Id}.");
                        }
                    }
                }
            }
        }

        private static class QueueOwnershipTransferCoalescer
        {
            public static List<QueueOwnershipTransfer> Coalesce(
                IReadOnlyList<QueueOwnershipTransfer> transfers,
                IReadOnlyList<MutableSegment> segments)
            {
                if (transfers.Count < 2)
                    return transfers.ToList();

                var coalesced = new List<QueueOwnershipTransfer>(transfers.Count);
                foreach (QueueOwnershipTransfer transfer in transfers.OrderBy(transfer => transfer.Id))
                {
                    if (coalesced.Count > 0 && TryCoalesce(coalesced[^1], transfer, out QueueOwnershipTransfer merged))
                    {
                        coalesced[^1] = merged;
                    }
                    else
                    {
                        coalesced.Add(transfer);
                    }
                }

                if (coalesced.Count == transfers.Count)
                    return coalesced;

                var byId = segments.ToDictionary(segment => segment.Id);
                foreach (MutableSegment segment in segments)
                {
                    segment.Acquires.Clear();
                    segment.Releases.Clear();
                }
                foreach (QueueOwnershipTransfer transfer in coalesced)
                {
                    byId[transfer.SourceSegmentId].Releases.Add(transfer);
                    byId[transfer.DestinationSegmentId].Acquires.Add(transfer);
                }

                return coalesced;
            }

            private static bool TryCoalesce(
                QueueOwnershipTransfer first,
                QueueOwnershipTransfer second,
                out QueueOwnershipTransfer merged)
            {
                merged = first;
                if (first.Binding.Kind != second.Binding.Kind ||
                    first.SourceSegmentId != second.SourceSegmentId ||
                    first.DestinationSegmentId != second.DestinationSegmentId ||
                    first.SourceQueue != second.SourceQueue ||
                    first.DestinationQueue != second.DestinationQueue ||
                    first.SourceQueueFamily != second.SourceQueueFamily ||
                    first.DestinationQueueFamily != second.DestinationQueueFamily ||
                    first.SourceStageMask != second.SourceStageMask ||
                    first.SourceAccessMask != second.SourceAccessMask ||
                    first.DestinationStageMask != second.DestinationStageMask ||
                    first.DestinationAccessMask != second.DestinationAccessMask ||
                    first.OldLayout != second.OldLayout ||
                    first.NewLayout != second.NewLayout ||
                    first.RequiresQueueFamilyOwnershipTransfer != second.RequiresQueueFamilyOwnershipTransfer ||
                    first.IsConcurrentResource != second.IsConcurrentResource)
                {
                    return false;
                }

                if (first.Binding.Kind == RenderGraphConcreteResourceKind.Buffer)
                {
                    if (first.Binding.Buffer.Handle != second.Binding.Buffer.Handle ||
                        first.Binding.ByteOffset + first.Binding.ByteSize != second.Binding.ByteOffset)
                    {
                        return false;
                    }

                    RenderGraphConcreteResourceBinding binding = first.Binding with
                    {
                        Name = first.Binding.Name + "+" + second.Binding.Name,
                        ByteSize = checked(first.Binding.ByteSize + second.Binding.ByteSize)
                    };
                    merged = first with
                    {
                        Binding = binding,
                        ConstituentBindings = CombineConstituentBindings(first, second)
                    };
                    return true;
                }

                if (first.Binding.Image.Handle != second.Binding.Image.Handle)
                    return false;
                ImageSubresourceRange a = first.Binding.SubresourceRange;
                ImageSubresourceRange b = second.Binding.SubresourceRange;
                if (a.AspectMask != b.AspectMask ||
                    a.BaseArrayLayer != b.BaseArrayLayer ||
                    a.LayerCount != b.LayerCount ||
                    (ulong)a.BaseMipLevel + a.LevelCount != b.BaseMipLevel)
                {
                    return false;
                }

                RenderGraphConcreteResourceBinding imageBinding = first.Binding with
                {
                    Name = first.Binding.Name + "+" + second.Binding.Name,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = a.AspectMask,
                        BaseMipLevel = a.BaseMipLevel,
                        LevelCount = checked(a.LevelCount + b.LevelCount),
                        BaseArrayLayer = a.BaseArrayLayer,
                        LayerCount = a.LayerCount
                    }
                };
                merged = first with
                {
                    Binding = imageBinding,
                    ConstituentBindings = CombineConstituentBindings(first, second)
                };
                return true;
            }

            private static IReadOnlyList<RenderGraphConcreteResourceBinding> CombineConstituentBindings(
                QueueOwnershipTransfer first,
                QueueOwnershipTransfer second)
            {
                IReadOnlyList<RenderGraphConcreteResourceBinding> firstBindings = first.AllBindings;
                IReadOnlyList<RenderGraphConcreteResourceBinding> secondBindings = second.AllBindings;
                var combined = new RenderGraphConcreteResourceBinding[firstBindings.Count + secondBindings.Count];
                for (int index = 0; index < firstBindings.Count; index++)
                    combined[index] = firstBindings[index];
                for (int index = 0; index < secondBindings.Count; index++)
                    combined[firstBindings.Count + index] = secondBindings[index];
                return combined;
            }
        }
    }
}
