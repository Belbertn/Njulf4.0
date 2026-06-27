using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Dictionary<RenderGraphResourceId, RenderGraphResourceUsage> _lastResourceUsages = new();
        private readonly List<RenderGraphPlannedBarrier> _framePlannedBarriers = new();
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

        public RenderGraphDiagnostics CreateDiagnostics(RenderFeatureIsolationMode featureIsolation, bool asyncComputeEnabled = false)
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
                bool asyncCandidate = pass.SupportsAsyncCompute;
                bool asyncEnabled = asyncComputeEnabled && enabledByFeatureIsolation && asyncCandidate;
                if (enabledByFeatureIsolation && asyncCandidate)
                    asyncComputeCandidatePassCount++;
                if (asyncEnabled)
                    asyncComputeEnabledPassCount++;

                passes.Add(new RenderGraphPassDiagnostics(
                    pass.Name,
                    enabledByFeatureIsolation,
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
                    barrier.Executed));
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
            return target;
        }

        public void ReleaseOwnedRenderTarget(RenderGraphResourceId id, RenderTarget target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets) || !targets.Remove(target))
                throw new InvalidOperationException($"Resource '{id}' does not own render target '{target.Name}'.");

            target.Dispose();
            if (targets.Count == 0)
                _ownedRenderTargets.Remove(id);
        }

        public bool OwnsResource(RenderGraphResourceId id)
        {
            return _ownedRenderTargets.ContainsKey(id);
        }

        public bool HasResource(RenderGraphResourceId id)
        {
            return _resources.ContainsKey(id);
        }

        public IReadOnlyList<RenderTarget> GetOwnedRenderTargets(RenderGraphResourceId id)
        {
            return _ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets)
                ? targets
                : Array.Empty<RenderTarget>();
        }

        public void RecreateOwnedRenderTarget(RenderGraphResourceId id, RenderTarget target, Extent2D extent)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets) || !targets.Contains(target))
                throw new InvalidOperationException($"Resource '{id}' does not own render target '{target.Name}'.");

            if (target.Extent.Width != extent.Width || target.Extent.Height != extent.Height)
                target.Recreate(extent);
        }

        public void RecreateOwnedRenderTargets(RenderGraphResourceId id, Extent2D extent)
        {
            if (!_ownedRenderTargets.TryGetValue(id, out List<RenderTarget>? targets))
                throw new InvalidOperationException($"Resource '{id}' has no graph-owned render targets.");

            foreach (RenderTarget target in targets)
            {
                if (target.Extent.Width != extent.Width || target.Extent.Height != extent.Height)
                    target.Recreate(extent);
            }
        }
        
        public void AddPass(RenderPassBase pass)
        {
            if (pass == null)
                throw new ArgumentNullException(nameof(pass));
            _passes.Add(pass);
            System.Diagnostics.Debug.WriteLine($"Render pass added: {pass.Name}");
        }
        
        public void Initialize()
        {
            ValidateResourceDeclarations();
            foreach (var pass in _passes)
                pass.Initialize();
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
                    if (usage.Access == RenderGraphResourceAccess.Read &&
                        resource.Lifetime != RenderGraphResourceLifetime.Imported &&
                        !HasPriorWrite(passName, usage.Resource))
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

            foreach (var pass in _passes)
            {
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

                ExecuteGraphPlannedBarriers(cmd, pass.Name, sceneData);

                var barriers = pass.GetBarriers(frameIndex);
                foreach (var barrier in barriers)
                    BarrierBuilder.ExecuteBarrier(cmd, barrier);

                if (useSecondaryCommandBuffers && commandBuffers != null && pass.SupportsSecondaryCommandBuffer)
                {
                    ExecuteSecondaryPass(commandBuffers, cmd, pass, frameIndex, sceneData, timestamps);
                    continue;
                }

                long passStart = Stopwatch.GetTimestamp();
                pass.Context.BeginDebugLabel(cmd, pass.Name);
                timestamps?.BeginPass(cmd, frameIndex, pass.Name);
                try
                {
                    pass.Execute(cmd, frameIndex, sceneData);
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
                pass.Execute(secondary, frameIndex, sceneData);
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
            SceneRenderingData sceneData)
        {
            if (!_passResourceUsages.TryGetValue(passName, out List<RenderGraphResourceUsage>? usages))
                return;

            foreach (RenderGraphResourceUsage usage in usages)
            {
                if (usage.ImageLayout == ImageLayout.Undefined ||
                    !_resources.TryGetValue(usage.Resource, out RenderGraphResourceDescriptor? resource) ||
                    !IsImageResource(resource.Kind) ||
                    !_ownedRenderTargets.TryGetValue(usage.Resource, out List<RenderTarget>? targets))
                {
                    _lastResourceUsages[usage.Resource] = usage;
                    continue;
                }

                bool hasPrevious = _lastResourceUsages.TryGetValue(usage.Resource, out RenderGraphResourceUsage previous);
                foreach (RenderTarget target in targets)
                    PlanAndExecuteImageBarrier(cmd, passName, usage, previous, hasPrevious, target, sceneData);

                _lastResourceUsages[usage.Resource] = usage;
            }
        }

        private void PlanAndExecuteImageBarrier(
            CommandBuffer cmd,
            string passName,
            RenderGraphResourceUsage usage,
            RenderGraphResourceUsage previous,
            bool hasPrevious,
            RenderTarget target,
            SceneRenderingData sceneData)
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
                Executed: true);
            _framePlannedBarriers.Add(barrier);
            sceneData.GraphPlannedBarrierCount++;
            sceneData.GraphExecutedBarrierCount++;
            if (queueOwnershipTransition)
                sceneData.GraphQueueOwnershipTransitionCount++;
            sceneData.GraphBarrierSummary = BuildBarrierSummary();
        }

        private void ResetBarrierPlanning(SceneRenderingData sceneData)
        {
            _framePlannedBarriers.Clear();
            _lastResourceUsages.Clear();
            sceneData.GraphPlannedBarrierCount = 0;
            sceneData.GraphExecutedBarrierCount = 0;
            sceneData.GraphQueueOwnershipTransitionCount = 0;
            sceneData.GraphBarrierSummary = string.Empty;
        }

        private string BuildBarrierSummary()
        {
            var parts = new List<string>(_framePlannedBarriers.Count);
            for (int i = 0; i < _framePlannedBarriers.Count; i++)
            {
                RenderGraphPlannedBarrier barrier = _framePlannedBarriers[i];
                parts.Add($"{barrier.PassName}:{barrier.Resource} {barrier.OldLayout}->{barrier.NewLayout}");
            }

            return string.Join("; ", parts);
        }

        private int CountPotentialQueueOwnershipTransitions(RenderFeatureIsolationMode featureIsolation)
        {
            var lastQueueByResource = new Dictionary<RenderGraphResourceId, RenderGraphQueueIntent>();
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
                    if (!_resources.TryGetValue(usage.Resource, out RenderGraphResourceDescriptor? resource) ||
                        !IsImageResource(resource.Kind))
                    {
                        lastQueueByResource[usage.Resource] = usage.QueueIntent;
                        continue;
                    }

                    if (lastQueueByResource.TryGetValue(usage.Resource, out RenderGraphQueueIntent previousQueue) &&
                        previousQueue != usage.QueueIntent &&
                        previousQueue != RenderGraphQueueIntent.External &&
                        usage.QueueIntent != RenderGraphQueueIntent.External)
                    {
                        count++;
                    }

                    lastQueueByResource[usage.Resource] = usage.QueueIntent;
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
                _ => AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
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
                case "AmbientOcclusionBlurPass":
                    sceneData.CpuAmbientOcclusionBlurRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "SsgiTracePass":
                    sceneData.CpuSsgiRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "SsgiTemporalPass":
                    sceneData.CpuSsgiRecordMicroseconds += elapsedMicroseconds;
                    break;
                case "SsgiDenoisePass":
                    sceneData.CpuSsgiRecordMicroseconds += elapsedMicroseconds;
                    break;
                case "SsgiCompositePass":
                    sceneData.CpuSsgiRecordMicroseconds += elapsedMicroseconds;
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
            foreach (var pass in _passes)
                pass.OnSwapchainRecreated();
        }
        
        public void Cleanup()
        {
            foreach (var pass in _passes)
                pass.Cleanup();

            foreach (List<RenderTarget> targets in _ownedRenderTargets.Values)
            {
                foreach (RenderTarget target in targets)
                    target.Dispose();
            }
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
