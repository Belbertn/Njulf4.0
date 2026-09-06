using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Silk.NET.Vulkan;
using Njulf.Rendering.Core;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline
{
    public sealed class RenderGraph : IDisposable
    {
        private readonly List<RenderPassBase> _passes = new List<RenderPassBase>();
        private readonly Dictionary<RenderGraphResourceId, RenderGraphResourceDescriptor> _resources = new();
        private readonly Dictionary<string, List<RenderGraphResourceUsage>> _passResourceUsages = new(StringComparer.Ordinal);
        private readonly Dictionary<RenderGraphResourceId, List<RenderTarget>> _ownedRenderTargets = new();
        // Generation-swapped resources may have active plus pending/retired
        // physical allocations under one logical ID. Ownership and layout
        // tracking are deliberately separate: only the atomically published
        // bank may be named by new command buffers.
        private readonly Dictionary<RenderGraphResourceId, List<RenderTarget>>
            _publishedOwnedRenderTargets = new();
        // Imported renderer targets participate in the same primary-command-buffer layout
        // planning as graph-owned targets, but the graph must never dispose them.
        private readonly Dictionary<RenderGraphResourceId, List<RenderTarget>> _importedRenderTargets = new();
        // Some imported images, such as the multi-mip Hi-Z pyramid, are not RenderTargets but
        // still need graph-owned layout transitions before secondary command buffers consume them.
        private readonly Dictionary<RenderGraphResourceId, List<IRenderGraphLayoutTrackedImage>> _importedImageTargets = new();
        private readonly RenderGraphResourceBindings _concreteResourceBindings = new();
        // Same-queue image synchronization follows the layout-tracked physical wrapper rather
        // than the logical graph ID. One Vulkan image can legitimately be exposed through more
        // than one logical resource (for example, a shared sampled texture view); keying this
        // state by the logical ID would lose a write -> read dependency when the second pass uses
        // an alias. Reference identity is intentional: each history bank has its own wrapper and
        // exact aliases register that same wrapper under every logical view.
        private readonly Dictionary<IRenderGraphLayoutTrackedImage, RenderGraphResourceUsage> _lastImageUsages =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<RenderGraphPlannedBarrier> _framePlannedBarriers = new();
        private readonly StringBuilder _barrierSummaryBuilder = new();
        private ulong _resourceAllocationGeneration;
        private readonly InterFrameAccessTracker _interFrameAccesses = new();
        private readonly HashSet<InterFrameAccessTracker.Allocation> _interFrameLiveAllocations = new();
        private readonly HashSet<InterFrameAccessTracker.Allocation> _interFrameExternalAllocations = new();
        private RenderGraphResourcePlan? _interFrameResourcePlan;
        private bool _interFrameDependenciesEnabled;
        private bool _interFrameHistoryValid;
        internal bool NeedsInterFramePriming => _interFrameDependenciesEnabled && !_interFrameHistoryValid;
        internal int InterFrameConservativePassCount { get; private set; }
        internal int InterFrameBarrierCount { get; private set; }
        private bool _cleanedUp;
        private bool _disposed;

        public IReadOnlyList<string> PassNames => _passes.ConvertAll(pass => pass.Name);
        public IReadOnlyCollection<RenderGraphResourceDescriptor> ResourceInventory => _resources.Values;
        public int OwnedRenderTargetCount
        {
            get
            {
                int count = 0;
                foreach (List<RenderTarget> targets in _ownedRenderTargets.Values)
                    count += targets.Count;
                return count;
            }
        }
        public IReadOnlyDictionary<string, IReadOnlyList<RenderGraphResourceUsage>> PassResourceUsages =>
            ToReadOnlyPassResourceUsages();
        public IReadOnlyList<RenderGraphPlannedBarrier> LastPlannedBarriers => _framePlannedBarriers.ToArray();
        public RenderGraphResourceBindings ConcreteResourceBindings => _concreteResourceBindings;
        /// <summary>
        /// Advances only when graph-owned allocations or their swapchain-dependent configuration
        /// change. Frame rotation does not invalidate an immutable concrete resource plan.
        /// </summary>
        public ulong ResourceAllocationGeneration => _resourceAllocationGeneration;

        internal void BeginInterFrameRecording(bool enabled)
        {
            _interFrameDependenciesEnabled = enabled;
            _interFrameAccesses.BeginRecording();
            InterFrameConservativePassCount = 0;
            InterFrameBarrierCount = 0;
        }

        internal void CommitInterFrameSubmission()
        {
            if (_interFrameDependenciesEnabled)
            {
                _interFrameAccesses.CommitSubmission();
                _interFrameHistoryValid = true;
            }
            else
            {
                _interFrameAccesses.Clear();
                _interFrameHistoryValid = false;
            }
        }

        private static InterFrameAccessTracker.Allocation InterFrameAllocation(
            RenderGraphConcreteResourceBinding binding) => new(
                binding.Kind,
                binding.Kind == RenderGraphConcreteResourceKind.Image ? binding.Image.Handle : binding.Buffer.Handle,
                binding.AllocationGeneration);

        private unsafe void ExecuteInterFrameBarriers(
            CommandBuffer cmd, RenderPassBase pass, int frameIndex)
        {
            if (PlanInterFrameBarrier(pass.Name, frameIndex) is not { } barrier)
                return;
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier
            };
            pass.Context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        internal void CoverInterFrameAccesses(PipelineStageFlags2 stages) =>
            _interFrameAccesses.CoverSubmittedAccesses(stages);

        internal MemoryBarrier2? PlanInterFrameBarrier(string passName, int frameIndex)
        {
            if (!_interFrameDependenciesEnabled)
                return null;

            if (!ReferenceEquals(_interFrameResourcePlan, _concreteResourceBindings.CurrentPlan))
            {
                _interFrameResourcePlan = _concreteResourceBindings.CurrentPlan;
                _interFrameLiveAllocations.Clear();
                _interFrameExternalAllocations.Clear();
                foreach (RenderGraphConcreteResourceBinding binding in _interFrameResourcePlan.Bindings)
                {
                    _interFrameLiveAllocations.Add(InterFrameAllocation(binding));
                    if (binding.Lifetime == RenderGraphResourceLifetime.Imported)
                        _interFrameExternalAllocations.Add(InterFrameAllocation(binding));
                }
                _interFrameAccesses.RetainAllocations(_interFrameLiveAllocations);
            }

            var barrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2 };
            bool conservative = false;
            foreach (RenderGraphResourceUsage usage in _passResourceUsages[passName])
            {
                var bindings = _concreteResourceBindings.GetBindings(usage.Resource, frameIndex, usage.HistoryBinding);
                bool write = IsWriteAccess(usage.Access);
                var destination = new InterFrameAccessTracker.Scope(
                    usage.StageMask == PipelineStageFlags2.None ? PipelineStageFlags2.AllCommandsBit : usage.StageMask,
                    usage.AccessMask == AccessFlags2.None
                        ? (write ? AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit : AccessFlags2.MemoryReadBit)
                        : usage.AccessMask);
                if (bindings.Count == 0)
                {
                    AddConservativeDependency(destination.Stages);
                    continue;
                }
                foreach (RenderGraphConcreteResourceBinding binding in bindings)
                {
                    var allocation = InterFrameAllocation(binding);
                    if (_interFrameExternalAllocations.Contains(allocation))
                    {
                        // The graph does not observe the complete access history of imported
                        // resources. Keep their source conservative without inventing history.
                        AddConservativeDependency(destination.Stages);
                        continue;
                    }
                    MemoryBarrier2? dependency = _interFrameAccesses.Access(allocation, destination, write);
                    if (dependency is not { } next)
                        continue;
                    barrier.SrcStageMask |= next.SrcStageMask;
                    barrier.SrcAccessMask |= next.SrcAccessMask;
                    barrier.DstStageMask |= next.DstStageMask;
                    barrier.DstAccessMask |= next.DstAccessMask;
                }
            }
            if (conservative)
                InterFrameConservativePassCount++;
            if (barrier.SrcStageMask == PipelineStageFlags2.None)
                return null;
            InterFrameBarrierCount++;
            return barrier;

            void AddConservativeDependency(PipelineStageFlags2 stages)
            {
                if (_interFrameAccesses.RequireConservativeDependency(stages) is not { } dependency)
                    return;
                barrier.SrcStageMask |= dependency.SrcStageMask;
                barrier.SrcAccessMask |= dependency.SrcAccessMask;
                barrier.DstStageMask |= dependency.DstStageMask;
                barrier.DstAccessMask |= dependency.DstAccessMask;
                conservative = true;
            }
        }

        public RenderGraphDiagnostics CreateDiagnostics(
            RenderFeatureIsolationMode featureIsolation,
            bool asyncComputeEnabled = false,
            SceneRenderingData? sceneData = null)
        {
            var resources = new List<RenderGraphResourceDiagnostics>(_resources.Count);
            int transientResourceCount = 0;
            int persistentResourceCount = 0;
            int aliasableResourceCount = 0;
            int importedResourceCount = 0;
            int asyncComputeCandidatePassCount = 0;
            int asyncComputeEnabledPassCount = 0;
            int queueOwnershipTransitionCount = CountPotentialQueueOwnershipTransitions(featureIsolation);
            ulong totalEstimatedBytes = 0;

            foreach (RenderGraphResourceDescriptor resource in _resources.Values)
            {
                bool graphOwned = _ownedRenderTargets.TryGetValue(resource.Id, out List<RenderTarget>? ownedTargets);
                int ownedTargetCount = ownedTargets?.Count ?? 0;
                ulong estimatedBytes = 0;
                if (ownedTargets != null)
                {
                    foreach (RenderTarget target in ownedTargets)
                        estimatedBytes += target.EstimatedByteSize;
                }

                totalEstimatedBytes += estimatedBytes;
                if (resource.Lifetime == RenderGraphResourceLifetime.Transient)
                    transientResourceCount++;
                if (resource.Persistent)
                    persistentResourceCount++;
                if (!resource.Persistent)
                    aliasableResourceCount++;
                if (resource.Lifetime == RenderGraphResourceLifetime.Imported)
                    importedResourceCount++;

                resources.Add(new RenderGraphResourceDiagnostics(
                    resource.Id.ToString(),
                    resource.DebugName,
                    resource.Kind.ToString(),
                    resource.Format?.ToString() ?? string.Empty,
                    resource.SizePolicy.ToString(),
                    resource.Lifetime.ToString(),
                    resource.Persistent,
                    graphOwned,
                    ownedTargetCount,
                    estimatedBytes));
            }

            var passes = new List<RenderGraphPassDiagnostics>(_passes.Count);
            foreach (RenderPassBase pass in _passes)
            {
                IReadOnlyList<RenderGraphResourceUsage> usages = GetPassResourceUsages(pass.Name);
                bool enabledByFeatureIsolation = RenderFeatureIsolationPolicy.ShouldExecutePass(featureIsolation, pass.Name);
                bool willExecute = enabledByFeatureIsolation &&
                    (sceneData == null || pass.ShouldExecute(checked((int)sceneData.CurrentFrameIndex), sceneData));
                bool asyncCandidate = pass.SupportsAsyncCompute;
                bool asyncEnabled = asyncComputeEnabled && willExecute && asyncCandidate;
                if (willExecute && asyncCandidate)
                    asyncComputeCandidatePassCount++;
                if (asyncEnabled)
                    asyncComputeEnabledPassCount++;

                passes.Add(new RenderGraphPassDiagnostics(
                    pass.Name,
                    willExecute,
                    pass.QueueIntent.ToString(),
                    asyncCandidate,
                    asyncEnabled,
                    pass.AsyncComputeReason,
                    UsageNames(usages, RenderGraphResourceAccess.Read),
                    UsageNames(usages, RenderGraphResourceAccess.Write),
                    UsageNames(usages, RenderGraphResourceAccess.ReadWrite)));
            }

            var barriers = new List<RenderGraphBarrierDiagnostics>(_framePlannedBarriers.Count);
            foreach (RenderGraphPlannedBarrier barrier in _framePlannedBarriers)
            {
                barriers.Add(new RenderGraphBarrierDiagnostics(
                    barrier.PassName,
                    barrier.Resource.ToString(),
                    barrier.PreviousAccess.ToString(),
                    barrier.NextAccess.ToString(),
                    barrier.OldLayout.ToString(),
                    barrier.NewLayout.ToString(),
                    barrier.SourceStage.ToString(),
                    barrier.SourceAccess.ToString(),
                    barrier.DestinationStage.ToString(),
                    barrier.DestinationAccess.ToString(),
                    barrier.PreviousQueueIntent.ToString(),
                    barrier.QueueIntent.ToString(),
                    barrier.QueueOwnershipTransition,
                    barrier.Executed,
                    barrier.HistoryIndex));
            }

            return new RenderGraphDiagnostics(
                _resources.Count,
                _passes.Count,
                _framePlannedBarriers.Count,
                _framePlannedBarriers.Count,
                transientResourceCount,
                persistentResourceCount,
                aliasableResourceCount,
                importedResourceCount,
                OwnedRenderTargetCount,
                asyncComputeCandidatePassCount,
                asyncComputeEnabledPassCount,
                queueOwnershipTransitionCount,
                totalEstimatedBytes,
                resources,
                passes,
                barriers);
        }

        public void RegisterResource(RenderGraphResourceDescriptor descriptor)
        {
            descriptor = descriptor.Validate();
            if (!_resources.TryAdd(descriptor.Id, descriptor))
                throw new InvalidOperationException($"Render graph resource '{descriptor.Id}' is already registered.");
        }

        public void RegisterResources(IEnumerable<RenderGraphResourceDescriptor> descriptors)
        {
            if (descriptors == null)
                throw new ArgumentNullException(nameof(descriptors));

            foreach (RenderGraphResourceDescriptor descriptor in descriptors)
                RegisterResource(descriptor);
        }

        public void DeclarePassResources(string passName, params RenderGraphResourceUsage[] usages)
        {
            if (string.IsNullOrWhiteSpace(passName))
                throw new ArgumentException("Pass name is required.", nameof(passName));
            if (usages == null)
                throw new ArgumentNullException(nameof(usages));

            _passResourceUsages[passName] = new List<RenderGraphResourceUsage>(usages);
        }

        public IReadOnlyList<RenderGraphResourceUsage> GetPassResourceUsages(string passName)
        {
            if (string.IsNullOrWhiteSpace(passName))
                throw new ArgumentException("Pass name is required.", nameof(passName));

            return _passResourceUsages.TryGetValue(passName, out List<RenderGraphResourceUsage>? usages)
                ? usages
                : Array.Empty<RenderGraphResourceUsage>();
        }

        /// <summary>
        /// Removes optional pass instances at a renderer-controlled device-idle
        /// transition. This is used when immutable feature evidence becomes
        /// stale and the graph must expose zero pass work for the fallback.
        /// </summary>
        internal int RemovePassesAfterDeviceIdle(IEnumerable<string> passNames)
        {
            if (passNames == null)
                throw new ArgumentNullException(nameof(passNames));

            var requested = new HashSet<string>(passNames, StringComparer.Ordinal);
            int removed = 0;
            for (int index = _passes.Count - 1; index >= 0; index--)
            {
                RenderPassBase pass = _passes[index];
                if (!requested.Remove(pass.Name))
                    continue;

                _passes.RemoveAt(index);
                _passResourceUsages.Remove(pass.Name);
                pass.Dispose();
                removed++;
            }

            if (requested.Count != 0)
            {
                throw new InvalidOperationException(
                    "Cannot remove unregistered render pass(es): " +
                    string.Join(", ", requested));
            }

            if (removed != 0)
                _concreteResourceBindings.Invalidate();
            return removed;
        }

        /// <summary>
        /// Removes logical resources only after all physical targets and every
        /// pass usage have been retired. This keeps disabled optional features
        /// out of both allocation and graph-inventory diagnostics.
        /// </summary>
        internal int UnregisterResourcesAfterDeviceIdle(
            IEnumerable<RenderGraphResourceId> resourceIds)
        {
            if (resourceIds == null)
                throw new ArgumentNullException(nameof(resourceIds));

            int removed = 0;
            foreach (RenderGraphResourceId id in resourceIds)
            {
                if (!_resources.ContainsKey(id))
                    continue;
                if (_ownedRenderTargets.ContainsKey(id) ||
                    _importedRenderTargets.ContainsKey(id) ||
                    _importedImageTargets.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        $"Cannot unregister graph resource '{id}' while a physical target is live.");
                }

                foreach ((string passName, List<RenderGraphResourceUsage> usages) in
                         _passResourceUsages)
                {
                    if (usages.Exists(usage => usage.Resource == id))
                    {
                        throw new InvalidOperationException(
                            $"Cannot unregister graph resource '{id}' while pass '{passName}' references it.");
                    }
                }

                _resources.Remove(id);
                _publishedOwnedRenderTargets.Remove(id);
                removed++;
            }

            if (removed != 0)
            {
                _concreteResourceBindings.Invalidate();
                AdvanceResourceAllocationGeneration();
            }
            return removed;
        }

        /// <summary>
        /// Resolves the same feature-isolation and per-pass predicate used by execution without
        /// recording work. The async planner uses this to avoid moving an optional no-op pass to
        /// another queue and, more importantly, to validate only the concrete resources a frame
        /// will really touch.
        /// </summary>
        public bool WillExecutePass(string passName, int frameIndex, SceneRenderingData sceneData)
        {
            if (string.IsNullOrWhiteSpace(passName))
                throw new ArgumentException("Pass name is required.", nameof(passName));
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));

            RenderPassBase? pass = _passes.Find(candidate => string.Equals(candidate.Name, passName, StringComparison.Ordinal));
            if (pass == null)
                throw new InvalidOperationException($"Render pass '{passName}' is not registered.");

            return RenderFeatureIsolationPolicy.ShouldExecutePass(sceneData.ActiveFeatureIsolation, pass.Name) &&
                   pass.ShouldExecute(frameIndex, sceneData);
        }

        public RenderTarget CreateOwnedRenderTarget(
            RenderGraphResourceId id,
            VulkanContext context,
            string name,
            Format format,
            Extent2D extent,
            RenderTargetDescriptor descriptor)
        {
            ValidateOwnedResource(id);

            var target = new RenderTarget(context, name, format, extent, descriptor);
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets))
            {
                targets = new List<RenderTarget>();
                _ownedRenderTargets.Add(id, targets);
            }

            targets.Add(target);
            AdvanceResourceAllocationGeneration();
            return target;
        }

        /// <summary>
        /// Registers a renderer-owned image target for graph layout planning.  Unlike
        /// <see cref="CreateOwnedRenderTarget"/>, this does not transfer lifetime ownership to the graph.
        /// Keeping these transitions on the primary command buffer is required before a secondary
        /// command buffer may consume an imported image.
        /// </summary>
        internal void RegisterImportedRenderTarget(RenderGraphResourceId id, RenderTarget target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!_resources.TryGetValue(id, out RenderGraphResourceDescriptor? resource))
                throw new InvalidOperationException($"Cannot track render target for unregistered resource '{id}'.");
            if (resource.Lifetime != RenderGraphResourceLifetime.Imported)
            {
                throw new InvalidOperationException(
                    $"Resource '{id}' is not imported and cannot register a renderer-owned render target.");
            }
            if (!IsImageResource(resource.Kind))
            {
                throw new InvalidOperationException(
                    $"Resource '{id}' is not an image and cannot register a renderer-owned render target.");
            }

            if (!_importedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets))
            {
                targets = new List<RenderTarget>();
                _importedRenderTargets.Add(id, targets);
            }

            if (!targets.Contains(target))
                targets.Add(target);
        }

        /// <summary>
        /// Registers an imported image with graph-visible layout state without transferring its
        /// allocation lifetime to the graph. This supports imported mip chains that cannot be
        /// represented by a single-mip <see cref="RenderTarget"/>.
        /// </summary>
        internal void RegisterImportedImageTarget(
            RenderGraphResourceId id,
            IRenderGraphLayoutTrackedImage target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!_resources.TryGetValue(id, out RenderGraphResourceDescriptor? resource))
                throw new InvalidOperationException($"Cannot track image for unregistered resource '{id}'.");
            if (resource.Lifetime != RenderGraphResourceLifetime.Imported)
            {
                throw new InvalidOperationException(
                    $"Resource '{id}' is not imported and cannot register a renderer-owned image.");
            }
            if (!IsImageResource(resource.Kind))
            {
                throw new InvalidOperationException(
                    $"Resource '{id}' is not an image and cannot register a renderer-owned image.");
            }

            if (!_importedImageTargets.TryGetValue(id, out List<IRenderGraphLayoutTrackedImage>? targets))
            {
                targets = new List<IRenderGraphLayoutTrackedImage>();
                _importedImageTargets.Add(id, targets);
            }

            if (!targets.Contains(target))
                targets.Add(target);
        }

        public void ReleaseOwnedRenderTarget(RenderGraphResourceId id, RenderTarget target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets) || !targets.Remove(target))
                throw new InvalidOperationException($"Resource '{id}' does not own render target '{target.Name}'.");

            target.Dispose();
            if (_publishedOwnedRenderTargets.TryGetValue(
                    id,
                    out List<RenderTarget>? publishedTargets))
            {
                publishedTargets.Remove(target);
            }
            if (targets.Count == 0)
                _ownedRenderTargets.Remove(id);
            AdvanceResourceAllocationGeneration();
        }

        public bool OwnsResource(RenderGraphResourceId id)
        {
            return _ownedRenderTargets.ContainsKey(id);
        }

        public bool HasResource(RenderGraphResourceId id)
        {
            return _resources.ContainsKey(id);
        }

        internal RenderGraphResourceLifetime GetResourceLifetime(
            RenderGraphResourceId id)
        {
            if (!_resources.TryGetValue(
                    id,
                    out RenderGraphResourceDescriptor? descriptor))
            {
                throw new InvalidOperationException(
                    $"Render-graph resource '{id}' is not registered.");
            }

            return descriptor.Lifetime;
        }

        public IReadOnlyList<RenderTarget> GetOwnedRenderTargets(RenderGraphResourceId id)
        {
            return _ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets)
                ? targets
                : Array.Empty<RenderTarget>();
        }

        /// <summary>
        /// Returns the targets whose layouts the graph may transition for this resource. Imported
        /// targets remain owned by their renderer manager; graph-owned targets retain their existing
        /// allocation/lifetime behavior.
        /// </summary>
        internal IReadOnlyList<RenderTarget> GetLayoutTrackedRenderTargets(RenderGraphResourceId id)
        {
            if (_publishedOwnedRenderTargets.TryGetValue(
                    id,
                    out List<RenderTarget>? publishedTargets))
            {
                return publishedTargets;
            }
            if (_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? ownedTargets))
                return ownedTargets;

            return _importedRenderTargets.TryGetValue(id, out List<RenderTarget>? importedTargets)
                ? importedTargets
                : Array.Empty<RenderTarget>();
        }

        /// <summary>
        /// Atomically selects the graph-owned image bindings visible to new
        /// command buffers. Non-published allocations remain graph-owned but
        /// receive no barriers and therefore acquire no new GPU references.
        /// </summary>
        internal void PublishOwnedRenderTargets(
            RenderGraphResourceId id,
            IReadOnlyList<RenderTarget> targets)
        {
            if (targets == null)
                throw new ArgumentNullException(nameof(targets));
            if (!_resources.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"Cannot publish targets for undeclared resource '{id}'.");
            }
            if (!_ownedRenderTargets.TryGetValue(
                    id,
                    out List<RenderTarget>? ownedTargets))
            {
                if (targets.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Resource '{id}' owns no render targets to publish.");
                }
            }

            var published = new List<RenderTarget>(targets.Count);
            for (int index = 0; index < targets.Count; index++)
            {
                RenderTarget target = targets[index] ??
                    throw new ArgumentException(
                        "A published graph target cannot be null.",
                        nameof(targets));
                if (ownedTargets is null || !ownedTargets.Contains(target))
                {
                    throw new InvalidOperationException(
                        $"Resource '{id}' does not own published target '{target.Name}'.");
                }
                if (published.Contains(target))
                {
                    throw new ArgumentException(
                        "A target cannot be published twice for one graph resource.",
                        nameof(targets));
                }
                published.Add(target);
            }

            if (_publishedOwnedRenderTargets.TryGetValue(
                    id,
                    out List<RenderTarget>? current) &&
                current.Count == published.Count)
            {
                bool unchanged = true;
                for (int index = 0; index < current.Count; index++)
                    unchanged &= ReferenceEquals(current[index], published[index]);
                if (unchanged)
                    return;
            }

            _publishedOwnedRenderTargets[id] = published;
            _concreteResourceBindings.Invalidate();
            AdvanceResourceAllocationGeneration();
        }

        internal IReadOnlyList<IRenderGraphLayoutTrackedImage> GetImportedImageTargets(RenderGraphResourceId id)
        {
            return _importedImageTargets.TryGetValue(id, out List<IRenderGraphLayoutTrackedImage>? targets)
                ? targets
                : Array.Empty<IRenderGraphLayoutTrackedImage>();
        }

        public void RecreateOwnedRenderTarget(RenderGraphResourceId id, RenderTarget target, Extent2D extent)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets) || !targets.Contains(target))
                throw new InvalidOperationException($"Resource '{id}' does not own render target '{target.Name}'.");

            if (target.Extent.Width != extent.Width || target.Extent.Height != extent.Height)
            {
                target.Recreate(extent);
                AdvanceResourceAllocationGeneration();
            }
        }

        public void RecreateOwnedRenderTargets(RenderGraphResourceId id, Extent2D extent)
        {
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets))
                throw new InvalidOperationException($"Resource '{id}' has no graph-owned render targets.");

            foreach (RenderTarget target in targets)
            {
                if (target.Extent.Width != extent.Width || target.Extent.Height != extent.Height)
                {
                    target.Recreate(extent);
                    AdvanceResourceAllocationGeneration();
                }
            }
        }
        
        public void AddPass(RenderPassBase pass)
        {
            if (pass == null)
                throw new ArgumentNullException(nameof(pass));
            _passes.Add(pass);
            System.Diagnostics.Debug.WriteLine($"Render pass added: {pass.Name}");
        }
        
        public void Initialize(Action<string, Action>? runStartupStep = null)
        {
            ValidateResourceDeclarations();
            foreach (var pass in _passes)
            {
                if (runStartupStep == null)
                {
                    pass.Initialize();
                }
                else
                {
                    runStartupStep(
                        $"RenderPass.Initialize.{pass.Name}",
                        pass.Initialize);
                }
            }
        }

        public void ValidateResourceDeclarations()
        {
            foreach (RenderPassBase pass in _passes)
            {
                if (!_passResourceUsages.ContainsKey(pass.Name))
                    throw new InvalidOperationException($"Render pass '{pass.Name}' has no graph resource declaration.");
            }

            foreach ((string passName, List<RenderGraphResourceUsage> usages) in _passResourceUsages)
            {
                if (!_passes.Exists(pass => string.Equals(pass.Name, passName, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"Graph resource declaration targets unknown pass '{passName}'.");

                foreach (RenderGraphResourceUsage usage in usages)
                {
                    if (!_resources.ContainsKey(usage.Resource))
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{passName}' declares {usage.Access} access to undeclared graph resource '{usage.Resource}'.");
                    }

                    RenderGraphResourceDescriptor resource = _resources[usage.Resource];
                    if (!Enum.IsDefined(usage.HistoryBinding))
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{passName}' declares unknown history binding selection " +
                            $"'{usage.HistoryBinding}' for '{usage.Resource}'.");
                    }
                    if (usage.HistoryBinding != RenderGraphHistoryBindingSelection.All &&
                        resource.Kind is not RenderGraphResourceKind.ImageChain and
                            not RenderGraphResourceKind.BufferSet)
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{passName}' selects a history bank for '{usage.Resource}', " +
                            $"but its graph resource kind is '{resource.Kind}', not ImageChain or BufferSet.");
                    }
                    if (usage.Access == RenderGraphResourceAccess.Read &&
                        resource.Lifetime != RenderGraphResourceLifetime.Imported &&
                        !HasPriorWrite(passName, usage.Resource) &&
                        !(usage.HistoryBinding == RenderGraphHistoryBindingSelection.Previous &&
                          resource.Kind == RenderGraphResourceKind.ImageChain))
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{passName}' reads graph resource '{usage.Resource}' before any prior pass writes it.");
                    }

                    if (usage.ImageLayout != ImageLayout.Undefined)
                    {
                        if (!IsImageResource(resource.Kind))
                        {
                            throw new InvalidOperationException(
                                $"Render pass '{passName}' declares image layout intent for non-image graph resource '{usage.Resource}'.");
                        }

                        if (usage.StageMask == PipelineStageFlags2.None || usage.AccessMask == AccessFlags2.None)
                        {
                            throw new InvalidOperationException(
                                $"Render pass '{passName}' declares image layout intent for '{usage.Resource}' without stage/access intent.");
                        }
                    }
                }
            }
        }
        
        public void Execute(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps = null,
            CommandBufferManager? commandBuffers = null,
            bool useSecondaryCommandBuffers = false)
        {
            ResetBarrierPlanning(sceneData);
            ExecuteSelected(
                cmd,
                frameIndex,
                sceneData,
                static _ => true,
                timestamps,
                commandBuffers,
                useSecondaryCommandBuffers);
            CompleteBarrierSummary(sceneData);
        }

        /// <summary>
        /// Replaces all concrete Vulkan bindings in one generation.  Resize, scene reload, and
        /// history ping-pong changes use this atomic operation so a scheduler can never retain a
        /// stale allocation handle from an earlier frame.
        /// </summary>
        public void ReplaceConcreteResourceBindings(IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            RenderGraphResourcePlan plan = CreateConcreteResourcePlan(bindings);
            _concreteResourceBindings.Activate(plan, resetState: true);
        }

        /// <summary>
        /// Builds and exhaustively validates an immutable concrete resource plan without publishing
        /// it. Callers may cache the result by their typed allocation-generation key.
        /// </summary>
        public RenderGraphResourcePlan CreateConcreteResourcePlan(
            IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            if (bindings == null)
                throw new ArgumentNullException(nameof(bindings));

            RenderGraphConcreteResourceBinding[] materialized = bindings.ToArray();
            foreach (RenderGraphConcreteResourceBinding binding in materialized)
            {
                if (!_resources.TryGetValue(binding.Resource, out RenderGraphResourceDescriptor? descriptor))
                {
                    throw new InvalidOperationException(
                        $"Concrete binding '{binding.Name}' references undeclared graph resource '{binding.Resource}'.");
                }

                if (descriptor.Kind != RenderGraphResourceKind.External)
                {
                    bool expectsImage = IsImageResource(descriptor.Kind);
                    bool isImage = binding.Kind == RenderGraphConcreteResourceKind.Image;
                    if (expectsImage != isImage)
                    {
                        throw new InvalidOperationException(
                            $"Concrete binding '{binding.Name}' has kind '{binding.Kind}', but graph resource " +
                            $"'{binding.Resource}' is declared as '{descriptor.Kind}'.");
                    }
                }
            }

            return _concreteResourceBindings.CreatePlan(materialized);
        }

        /// <summary>Publishes a previously validated plan in constant time.</summary>
        public void ActivateConcreteResourcePlan(RenderGraphResourcePlan plan, bool resetState = false) =>
            _concreteResourceBindings.Activate(plan, resetState);

        public void InvalidateConcreteResourceBindings() => _concreteResourceBindings.Invalidate();

        /// <summary>
        /// Returns deterministic validation errors instead of throwing so the async scheduler can
        /// reject just this frame and leave the graphics-only path intact.
        /// </summary>
        public IReadOnlyList<string> ValidateConcreteResourcePlan(
            IEnumerable<string> passNames,
            int frameIndex)
        {
            if (passNames == null)
                throw new ArgumentNullException(nameof(passNames));

            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string passName in passNames)
            {
                if (!_passResourceUsages.TryGetValue(passName, out List<RenderGraphResourceUsage>? usages))
                {
                    errors.Add($"Pass '{passName}' has no concrete resource declaration.");
                    continue;
                }

                foreach (RenderGraphResourceUsage usage in usages)
                {
                    IReadOnlyList<RenderGraphConcreteResourceBinding> bindings =
                        _concreteResourceBindings.GetBindings(
                            usage.Resource,
                            frameIndex,
                            usage.HistoryBinding);
                    if (bindings.Count == 0)
                    {
                        string error = $"Pass '{passName}' has no concrete binding for '{usage.Resource}' " +
                            $"({usage.HistoryBinding}).";
                        if (seen.Add(error))
                            errors.Add(error);
                        continue;
                    }

                    if (usage.HistoryBinding != RenderGraphHistoryBindingSelection.All &&
                        bindings.Count != 1)
                    {
                        string error = $"Pass '{passName}' resolved {bindings.Count} concrete bindings for " +
                            $"history-selected resource '{usage.Resource}' ({usage.HistoryBinding}); exactly one is required.";
                        if (seen.Add(error))
                            errors.Add(error);
                    }

                    if (usage.StageMask == PipelineStageFlags2.None || usage.AccessMask == AccessFlags2.None)
                    {
                        string error = $"Pass '{passName}' has no stage/access plan for '{usage.Resource}'.";
                        if (seen.Add(error))
                            errors.Add(error);
                    }

                    foreach (RenderGraphConcreteResourceBinding binding in bindings)
                    {
                        if (!_concreteResourceBindings.IsCurrent(binding))
                        {
                            string error = $"Pass '{passName}' resolved stale binding '{binding.Name}' for '{usage.Resource}'.";
                            if (seen.Add(error))
                                errors.Add(error);
                        }
                    }
                }
            }

            return errors;
        }

        public void BeginSplitExecution(SceneRenderingData sceneData)
        {
            ResetBarrierPlanning(sceneData);
        }

        /// <summary>
        /// Publishes the summary accumulated across all command-buffer segments of a split
        /// execution. The async scheduler owns its own plan summary, so an existing diagnostic
        /// value intentionally takes precedence.
        /// </summary>
        internal void CompleteSplitExecution(SceneRenderingData sceneData)
        {
            CompleteBarrierSummary(sceneData);
        }

        public void ExecuteSelected(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData,
            Func<string, bool> includePass,
            GpuTimestampRecorder? timestamps = null,
            CommandBufferManager? commandBuffers = null,
            bool useSecondaryCommandBuffers = false,
            bool isComputeQueue = false,
            bool usesExplicitQueueTransfers = false)
        {
            if (includePass == null)
                throw new ArgumentNullException(nameof(includePass));

            foreach (var pass in _passes)
            {
                if (!includePass(pass.Name))
                    continue;

                if (!RenderFeatureIsolationPolicy.ShouldExecutePass(sceneData.ActiveFeatureIsolation, pass.Name))
                {
                    SetPassRecordMicroseconds(sceneData, pass.Name, 0);
                    sceneData.SkippedRenderPassCount++;
                    continue;
                }

                if (!pass.ShouldExecute(frameIndex, sceneData))
                {
                    SetPassRecordMicroseconds(sceneData, pass.Name, 0);
                    continue;
                }

                pass.IsRecordingOnComputeQueue = isComputeQueue;
                ExecuteInterFrameBarriers(cmd, pass, frameIndex);
                ExecuteGraphPlannedBarriers(
                    cmd,
                    pass.Name,
                    frameIndex,
                    sceneData,
                    isComputeQueue,
                    usesExplicitQueueTransfers);

                var barriers = pass.GetBarriers(frameIndex);
                foreach (var barrier in barriers)
                    BarrierBuilder.ExecuteBarrier(cmd, barrier);

                if (!isComputeQueue && useSecondaryCommandBuffers && commandBuffers != null && pass.SupportsSecondaryCommandBuffer)
                {
                    ExecuteSecondaryPass(commandBuffers, cmd, pass, frameIndex, sceneData, timestamps);
                    ExecuteGraphFinalBarriers(
                        cmd,
                        pass.Name,
                        frameIndex,
                        sceneData,
                        usesExplicitQueueTransfers);
                    continue;
                }

                long passStart = Stopwatch.GetTimestamp();
                pass.Context.BeginDebugLabel(cmd, pass.Name);
                if (isComputeQueue)
                    timestamps?.BeginComputePass(cmd, frameIndex, pass.Name);
                else
                    timestamps?.BeginPass(cmd, frameIndex, pass.Name);
                try
                {
                    pass.Execute(cmd, frameIndex, sceneData, timestamps);
                    ExecuteGraphFinalBarriers(
                        cmd,
                        pass.Name,
                        frameIndex,
                        sceneData,
                        usesExplicitQueueTransfers);
                }
                finally
                {
                    timestamps?.EndPass(cmd, frameIndex);
                    pass.Context.EndDebugLabel(cmd);
                    long elapsedMicroseconds = ElapsedMicroseconds(passStart);
                    sceneData.CpuPrimaryCommandRecordMicroseconds += elapsedMicroseconds;
                    SetPassRecordMicroseconds(sceneData, pass.Name, elapsedMicroseconds);
                }
            }
        }

        private static void ExecuteSecondaryPass(
            CommandBufferManager commandBuffers,
            CommandBuffer primary,
            RenderPassBase pass,
            int frameIndex,
            SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps)
        {
            long passStart = Stopwatch.GetTimestamp();
            CommandBuffer secondary = commandBuffers.BeginSecondaryGraphicsCommand(frameIndex, pass.Name);
            pass.Context.BeginDebugLabel(secondary, pass.Name);
            timestamps?.BeginPass(secondary, frameIndex, pass.Name);
            try
            {
                pass.Execute(secondary, frameIndex, sceneData, timestamps);
            }
            finally
            {
                timestamps?.EndPass(secondary, frameIndex);
                pass.Context.EndDebugLabel(secondary);
                commandBuffers.EndCommandBuffer(secondary);
            }

            commandBuffers.ExecuteSecondaryGraphicsCommand(primary, secondary);
            long elapsedMicroseconds = ElapsedMicroseconds(passStart);
            sceneData.SecondaryCommandBufferPassCount++;
            sceneData.CpuSecondaryCommandRecordMicroseconds += elapsedMicroseconds;
            SetPassRecordMicroseconds(sceneData, pass.Name, elapsedMicroseconds);
        }

        private void ExecuteGraphPlannedBarriers(
            CommandBuffer cmd,
            string passName,
            int frameIndex,
            SceneRenderingData sceneData,
            bool isComputeQueue,
            bool usesExplicitQueueTransfers)
        {
            if (!_passResourceUsages.TryGetValue(passName, out List<RenderGraphResourceUsage>? usages))
                return;

            foreach (RenderGraphResourceUsage usage in usages)
            {
                int historyIndex = ResolvePhysicalHistoryIndex(usage, frameIndex);
                // Queue intent in the static declaration describes the preferred placement, not
                // necessarily the queue that recorded this frame (for example, compute-capable
                // passes remain on graphics in Disabled mode).  Track the actual recording
                // queue so a split plan never emits a second ordinary barrier across queues.
                RenderGraphResourceUsage effectiveUsage = usage with
                {
                    QueueIntent = isComputeQueue
                        ? RenderGraphQueueIntent.Compute
                        : RenderGraphQueueIntent.Graphics
                };

                IReadOnlyList<RenderTarget> targets = GetLayoutTrackedRenderTargets(usage.Resource);
                IReadOnlyList<IRenderGraphLayoutTrackedImage> importedImageTargets = GetImportedImageTargets(usage.Resource);
                if (usage.ImageLayout == ImageLayout.Undefined ||
                    !_resources.TryGetValue(usage.Resource, out RenderGraphResourceDescriptor? resource) ||
                    !IsImageResource(resource.Kind) ||
                    (targets.Count == 0 && importedImageTargets.Count == 0))
                {
                    continue;
                }

                if (historyIndex >= 0)
                {
                    if (targets.Count > 0)
                    {
                        if (historyIndex >= targets.Count)
                        {
                            throw new InvalidOperationException(
                                $"Render pass '{passName}' selected history bank {historyIndex} for " +
                                $"'{usage.Resource}', but the graph owns only {targets.Count} layout-tracked target(s).");
                        }
                        PlanTrackAndExecuteImageBarrier(
                            cmd,
                            passName,
                            effectiveUsage,
                            targets[historyIndex],
                            sceneData,
                            historyIndex,
                            usesExplicitQueueTransfers);
                    }
                    if (importedImageTargets.Count > 0)
                    {
                        if (historyIndex >= importedImageTargets.Count)
                        {
                            throw new InvalidOperationException(
                                $"Render pass '{passName}' selected history bank {historyIndex} for " +
                                $"'{usage.Resource}', but the graph imports only {importedImageTargets.Count} layout-tracked image(s).");
                        }
                        PlanTrackAndExecuteImageBarrier(
                            cmd,
                            passName,
                            effectiveUsage,
                            importedImageTargets[historyIndex],
                            sceneData,
                            historyIndex,
                            usesExplicitQueueTransfers);
                    }
                }
                else
                {
                    foreach (RenderTarget target in targets)
                    {
                        PlanTrackAndExecuteImageBarrier(
                            cmd,
                            passName,
                            effectiveUsage,
                            target,
                            sceneData,
                            historyIndex,
                            usesExplicitQueueTransfers);
                    }
                    foreach (IRenderGraphLayoutTrackedImage target in importedImageTargets)
                    {
                        PlanTrackAndExecuteImageBarrier(
                            cmd,
                            passName,
                            effectiveUsage,
                            target,
                            sceneData,
                            historyIndex,
                            usesExplicitQueueTransfers);
                    }
                }
            }
        }

        private void PlanTrackAndExecuteImageBarrier(
            CommandBuffer cmd,
            string passName,
            RenderGraphResourceUsage usage,
            IRenderGraphLayoutTrackedImage target,
            SceneRenderingData sceneData,
            int historyIndex,
            bool usesExplicitQueueTransfers)
        {
            bool hasPrevious = _lastImageUsages.TryGetValue(target, out RenderGraphResourceUsage previous);
            bool crossesRecordedQueues = usesExplicitQueueTransfers &&
                hasPrevious &&
                previous.QueueIntent != usage.QueueIntent &&
                previous.QueueIntent != RenderGraphQueueIntent.External &&
                usage.QueueIntent != RenderGraphQueueIntent.External;

            // QueueOwnershipTransferRecorder already emitted the matching release/acquire pair
            // and semaphore edge for the concrete allocation. An ordinary barrier in the
            // destination command buffer would duplicate that dependency and can name source
            // stages unsupported by a dedicated compute queue.
            if (!crossesRecordedQueues)
            {
                PlanAndExecuteImageBarrier(
                    cmd,
                    passName,
                    usage,
                    previous,
                    hasPrevious,
                    target,
                    sceneData,
                    historyIndex);
            }

            _lastImageUsages[target] = usage;
        }

        private void PlanAndExecuteImageBarrier(
            CommandBuffer cmd,
            string passName,
            RenderGraphResourceUsage usage,
            RenderGraphResourceUsage previous,
            bool hasPrevious,
            IRenderGraphLayoutTrackedImage target,
            SceneRenderingData sceneData,
            int historyIndex)
        {
            ImageLayout oldLayout = target.Layout;
            bool layoutTransition = oldLayout != usage.ImageLayout;
            bool previousLayoutMatchesActual = !hasPrevious ||
                previous.ImageLayout == ImageLayout.Undefined ||
                previous.ImageLayout == oldLayout;
            bool queueOwnershipTransition = false;
            bool memoryDependency = previousLayoutMatchesActual &&
                hasPrevious &&
                RequiresMemoryDependency(previous.Access, usage.Access);
            if (!layoutTransition && !memoryDependency && !queueOwnershipTransition)
                return;

            PipelineStageFlags2 sourceStage = ResolveSourceStage(previous, hasPrevious, oldLayout);
            AccessFlags2 sourceAccess = ResolveSourceAccess(previous, hasPrevious, oldLayout);
            target.TransitionToLayout(
                cmd,
                usage.ImageLayout,
                usage.StageMask,
                usage.AccessMask,
                sourceStage,
                sourceAccess,
                force: memoryDependency);

            var barrier = new RenderGraphPlannedBarrier(
                passName,
                usage.Resource,
                previous.Access,
                usage.Access,
                oldLayout,
                usage.ImageLayout,
                sourceStage,
                sourceAccess,
                usage.StageMask,
                usage.AccessMask,
                hasPrevious ? previous.QueueIntent : usage.QueueIntent,
                usage.QueueIntent,
                queueOwnershipTransition,
                Executed: true,
                HistoryIndex: historyIndex);
            _framePlannedBarriers.Add(barrier);
            sceneData.GraphPlannedBarrierCount++;
            sceneData.GraphExecutedBarrierCount++;
            if (queueOwnershipTransition)
                sceneData.GraphQueueOwnershipTransitionCount++;
            AppendBarrierSummary(barrier);
        }

        private void ResetBarrierPlanning(SceneRenderingData sceneData)
        {
            _framePlannedBarriers.Clear();
            _lastImageUsages.Clear();
            _barrierSummaryBuilder.Clear();
            sceneData.GraphPlannedBarrierCount = 0;
            sceneData.GraphExecutedBarrierCount = 0;
            sceneData.GraphQueueOwnershipTransitionCount = 0;
            sceneData.GraphBarrierSummary = string.Empty;
        }

        private void AppendBarrierSummary(RenderGraphPlannedBarrier barrier)
        {
            if (_barrierSummaryBuilder.Length > 0)
                _barrierSummaryBuilder.Append("; ");

            _barrierSummaryBuilder
                .Append(barrier.PassName)
                .Append(':')
                .Append(barrier.Resource);
            if (barrier.HistoryIndex >= 0)
            {
                _barrierSummaryBuilder
                    .Append("[history-")
                    .Append(barrier.HistoryIndex)
                    .Append(']');
            }
            _barrierSummaryBuilder
                .Append(' ')
                .Append(barrier.OldLayout)
                .Append("->")
                .Append(barrier.NewLayout);
        }

        private void CompleteBarrierSummary(SceneRenderingData sceneData)
        {
            if (!string.IsNullOrEmpty(sceneData.GraphBarrierSummary) || _barrierSummaryBuilder.Length == 0)
                return;

            sceneData.GraphBarrierSummary = _barrierSummaryBuilder.ToString();
        }

        private int CountPotentialQueueOwnershipTransitions(RenderFeatureIsolationMode featureIsolation)
        {
            // The declaration has no runtime frame index, but Current and
            // Previous are still distinct physical streams. Treating them as
            // one here would overstate queue handoffs and conceal a bank
            // dependency in diagnostics.
            var lastQueueByResource = new Dictionary<
                (RenderGraphResourceId Resource, RenderGraphHistoryBindingSelection HistoryBinding),
                RenderGraphQueueIntent>();
            int count = 0;

            foreach (RenderPassBase pass in _passes)
            {
                if (!RenderFeatureIsolationPolicy.ShouldExecutePass(featureIsolation, pass.Name) ||
                    !_passResourceUsages.TryGetValue(pass.Name, out List<RenderGraphResourceUsage>? usages))
                {
                    continue;
                }

                foreach (RenderGraphResourceUsage usage in usages)
                {
                    var key = (usage.Resource, usage.HistoryBinding);
                    if (!_resources.TryGetValue(usage.Resource, out RenderGraphResourceDescriptor? resource) ||
                        !IsImageResource(resource.Kind))
                    {
                        lastQueueByResource[key] = usage.QueueIntent;
                        continue;
                    }

                    if (lastQueueByResource.TryGetValue(key, out RenderGraphQueueIntent previousQueue) &&
                        previousQueue != usage.QueueIntent &&
                        previousQueue != RenderGraphQueueIntent.External &&
                        usage.QueueIntent != RenderGraphQueueIntent.External)
                    {
                        count++;
                    }

                    lastQueueByResource[key] = usage.QueueIntent;
                }
            }

            return count;
        }

        private static IReadOnlyList<string> UsageNames(IReadOnlyList<RenderGraphResourceUsage> usages, RenderGraphResourceAccess access)
        {
            var names = new List<string>();
            foreach (RenderGraphResourceUsage usage in usages)
            {
                if (usage.Access == access)
                    names.Add(usage.Resource.ToString());
            }

            return names;
        }

        private bool HasPriorWrite(string passName, RenderGraphResourceId resource)
        {
            foreach (RenderPassBase pass in _passes)
            {
                if (string.Equals(pass.Name, passName, StringComparison.Ordinal))
                    return false;
                if (!_passResourceUsages.TryGetValue(pass.Name, out List<RenderGraphResourceUsage>? usages))
                    continue;

                foreach (RenderGraphResourceUsage usage in usages)
                {
                    if (usage.Resource == resource &&
                        (usage.Access == RenderGraphResourceAccess.Write ||
                         usage.Access == RenderGraphResourceAccess.ReadWrite))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsImageResource(RenderGraphResourceKind kind)
        {
            return kind == RenderGraphResourceKind.Image || kind == RenderGraphResourceKind.ImageChain;
        }

        private static int ResolvePhysicalHistoryIndex(
            in RenderGraphResourceUsage usage,
            int frameIndex)
        {
            return usage.HistoryBinding == RenderGraphHistoryBindingSelection.All
                ? -1
                : RenderGraphResourcePlan.ResolveHistoryIndex(
                    frameIndex,
                    usage.HistoryBinding);
        }

        private static bool RequiresMemoryDependency(RenderGraphResourceAccess previous, RenderGraphResourceAccess next)
        {
            return IsWriteAccess(previous) || IsWriteAccess(next);
        }

        private static bool IsWriteAccess(RenderGraphResourceAccess access)
        {
            return access == RenderGraphResourceAccess.Write || access == RenderGraphResourceAccess.ReadWrite;
        }

        private static PipelineStageFlags2 ResolveSourceStage(
            RenderGraphResourceUsage previous,
            bool hasPrevious,
            ImageLayout oldLayout)
        {
            return hasPrevious && previous.StageMask != PipelineStageFlags2.None
                ? previous.StageMask
                : GetSourceStage(oldLayout);
        }

        private static AccessFlags2 ResolveSourceAccess(
            RenderGraphResourceUsage previous,
            bool hasPrevious,
            ImageLayout oldLayout)
        {
            return hasPrevious && previous.AccessMask != AccessFlags2.None
                ? previous.AccessMask
                : GetSourceAccess(oldLayout);
        }

        private static PipelineStageFlags2 GetSourceStage(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags2.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal => PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                ImageLayout.DepthStencilReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit,
                ImageLayout.General => PipelineStageFlags2.ComputeShaderBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags2.TransferBit,
                _ => PipelineStageFlags2.AllCommandsBit
            };
        }

        private static AccessFlags2 GetSourceAccess(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => AccessFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit,
                ImageLayout.ColorAttachmentOptimal => AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
                ImageLayout.DepthStencilAttachmentOptimal => AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.General => AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags2.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags2.TransferWriteBit,
                _ => AccessFlags2.MemoryReadBit
            };
        }

        private static void SetPassRecordMicroseconds(SceneRenderingData sceneData, string passName, long elapsedMicroseconds)
        {
            switch (passName)
            {
                case "DepthPrePass":
                    sceneData.CpuDepthPrePassRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "DirectionalShadowPass":
                    sceneData.CpuDirectionalShadowRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "SpotShadowPass":
                    sceneData.CpuSpotShadowRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "PointShadowPass":
                    sceneData.CpuPointShadowRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "HiZBuildPass":
                    sceneData.CpuHiZBuildRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AmbientOcclusionPass":
                    sceneData.CpuAmbientOcclusionRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "GtaoPass":
                    sceneData.CpuAmbientOcclusionRecordMicroseconds =
                        elapsedMicroseconds;
                    break;
                case "AmbientOcclusionBlurPass":
                    sceneData.CpuAmbientOcclusionBlurRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "GtaoTemporalPass":
                case "GtaoSpatialPass":
                    sceneData.CpuAmbientOcclusionBlurRecordMicroseconds +=
                        elapsedMicroseconds;
                    break;
                case "TiledLightCullingPass":
                    sceneData.CpuLightCullRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "ForwardPlusPass":
                    sceneData.CpuForwardOpaqueRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "TransparentForwardPass":
                    sceneData.CpuTransparentRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "WeightedTransparentPass":
                case "WeightedOitCompositePass":
                    sceneData.CpuTransparentRecordMicroseconds += elapsedMicroseconds;
                    break;
                case "ParticlePass":
                    sceneData.CpuParticleRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "DebugDrawPass":
                    sceneData.CpuDebugDrawRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "FogPass":
                    sceneData.CpuFogRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AutoExposurePass":
                    sceneData.CpuAutoExposureRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "ToneMapCompositePass":
                    sceneData.CpuCompositeRecordMicroseconds = elapsedMicroseconds;
                    break;
            }
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private IReadOnlyDictionary<string, IReadOnlyList<RenderGraphResourceUsage>> ToReadOnlyPassResourceUsages()
        {
            var copy = new Dictionary<string, IReadOnlyList<RenderGraphResourceUsage>>(StringComparer.Ordinal);
            foreach ((string passName, List<RenderGraphResourceUsage> usages) in _passResourceUsages)
                copy[passName] = usages.ToArray();
            return copy;
        }

        private void ValidateOwnedResource(RenderGraphResourceId id)
        {
            if (!_resources.TryGetValue(id, out RenderGraphResourceDescriptor? resource))
                throw new InvalidOperationException($"Cannot create graph-owned target for unregistered resource '{id}'.");
            if (resource.Lifetime == RenderGraphResourceLifetime.Imported)
                throw new InvalidOperationException($"Resource '{id}' is imported and cannot be graph-owned.");
        }
        
        public void OnSwapchainRecreated()
        {
            // Imported and graph-owned images may have been recreated. A plan compiled before
            // this point must never retain the old handle or ownership state.
            _concreteResourceBindings.Invalidate();
            AdvanceResourceAllocationGeneration();
            foreach (var pass in _passes)
                pass.OnSwapchainRecreated();
        }

        private void ExecuteGraphFinalBarriers(
            CommandBuffer cmd,
            string passName,
            int frameIndex,
            SceneRenderingData sceneData,
            bool usesExplicitQueueTransfers)
        {
            // A compiled cross-queue transfer owns the pass-exit transition. Recording a second
            // local transition would make its release/acquire old-layout contract stale.
            if (usesExplicitQueueTransfers ||
                !_passResourceUsages.TryGetValue(passName, out List<RenderGraphResourceUsage>? usages))
                return;

            foreach (RenderGraphResourceUsage usage in usages)
            {
                int historyIndex = ResolvePhysicalHistoryIndex(usage, frameIndex);
                IReadOnlyList<RenderTarget> targets = GetLayoutTrackedRenderTargets(usage.Resource);
                IReadOnlyList<IRenderGraphLayoutTrackedImage> importedImageTargets = GetImportedImageTargets(usage.Resource);
                if (usage.FinalImageLayout == ImageLayout.Undefined ||
                    usage.FinalImageLayout == usage.ImageLayout ||
                    (targets.Count == 0 && importedImageTargets.Count == 0))
                {
                    continue;
                }

                if (historyIndex >= 0)
                {
                    if (targets.Count > 0)
                    {
                        if (historyIndex >= targets.Count)
                        {
                            throw new InvalidOperationException(
                                $"Render pass '{passName}' selected history bank {historyIndex} for " +
                                $"'{usage.Resource}', but the graph owns only {targets.Count} layout-tracked target(s).");
                        }
                        ExecuteGraphFinalBarrier(
                            cmd,
                            passName,
                            usage,
                            targets[historyIndex],
                            sceneData,
                            historyIndex);
                    }
                    if (importedImageTargets.Count > 0)
                    {
                        if (historyIndex >= importedImageTargets.Count)
                        {
                            throw new InvalidOperationException(
                                $"Render pass '{passName}' selected history bank {historyIndex} for " +
                                $"'{usage.Resource}', but the graph imports only {importedImageTargets.Count} layout-tracked image(s).");
                        }
                        ExecuteGraphFinalBarrier(
                            cmd,
                            passName,
                            usage,
                            importedImageTargets[historyIndex],
                            sceneData,
                            historyIndex);
                    }
                }
                else
                {
                    foreach (RenderTarget target in targets)
                        ExecuteGraphFinalBarrier(cmd, passName, usage, target, sceneData, historyIndex);
                    foreach (IRenderGraphLayoutTrackedImage target in importedImageTargets)
                        ExecuteGraphFinalBarrier(cmd, passName, usage, target, sceneData, historyIndex);
                }
            }
        }

        private void ExecuteGraphFinalBarrier(
            CommandBuffer cmd,
            string passName,
            RenderGraphResourceUsage usage,
            IRenderGraphLayoutTrackedImage target,
            SceneRenderingData sceneData,
            int historyIndex)
        {
            ImageLayout oldLayout = target.Layout;
            if (oldLayout == usage.FinalImageLayout)
                return;
            target.TransitionToLayout(
                cmd,
                usage.FinalImageLayout,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.ShaderSampledReadBit,
                usage.StageMask,
                usage.AccessMask);
            var barrier = new RenderGraphPlannedBarrier(
                passName,
                usage.Resource,
                usage.Access,
                RenderGraphResourceAccess.Read,
                oldLayout,
                usage.FinalImageLayout,
                usage.StageMask,
                usage.AccessMask,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.ShaderSampledReadBit,
                usage.QueueIntent,
                usage.QueueIntent,
                QueueOwnershipTransition: false,
                Executed: true,
                HistoryIndex: historyIndex);
            _framePlannedBarriers.Add(barrier);
            sceneData.GraphPlannedBarrierCount++;
            sceneData.GraphExecutedBarrierCount++;
            AppendBarrierSummary(barrier);
        }

        private void AdvanceResourceAllocationGeneration()
        {
            _resourceAllocationGeneration++;
            if (_resourceAllocationGeneration == 0)
                _resourceAllocationGeneration = 1;
        }
        
        public void Cleanup()
        {
            if (_cleanedUp)
                return;

            // VulkanRenderer performs graph cleanup while its dependencies are still alive;
            // the DI container later disposes this graph as well. Keep that second path a no-op.
            _cleanedUp = true;
            foreach (var pass in _passes)
                pass.Cleanup();

            foreach (List<RenderTarget> targets in _ownedRenderTargets.Values)
            {
                foreach (RenderTarget target in targets)
                    target.Dispose();
            }

            _importedRenderTargets.Clear();
            _importedImageTargets.Clear();
            _publishedOwnedRenderTargets.Clear();
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
            System.Diagnostics.Debug.WriteLine("Render graph disposed.");
        }
    }
}
