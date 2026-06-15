using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Njulf.Rendering.Core;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace Njulf.Rendering.Pipeline
{
    public enum RenderGraphBarrierExecutionPhase
    {
        BeforePass,
        AfterPass
    }

    public sealed class RenderGraph : IDisposable
    {
        private readonly List<RenderPassBase> _passes = new List<RenderPassBase>();
        private RenderGraphDeclarationPlan _declarationPlan = new(
            Images: [],
            Buffers: [],
            Passes: [],
            Usage: new RenderGraphUsagePlan(
                new Dictionary<RenderGraphResourceHandle, ImageUsageFlags>(),
                new Dictionary<RenderGraphResourceHandle, BufferUsageFlags>()),
            Diagnostics: new RenderGraphCompilationDiagnostics([], [], [], 0, 0));
        private RenderGraphResolutionContext? _lastResolutionContext;
        private bool _disposed;

        public IReadOnlyList<string> PassNames => _passes.ConvertAll(pass => pass.Name);
        public RenderGraphDeclarationPlan DeclarationPlan => _declarationPlan;
        public IReadOnlyList<RenderGraphResolvedImageDesc> ResolvedImages { get; private set; } = Array.Empty<RenderGraphResolvedImageDesc>();
        public RenderGraphImageAllocationPlan ImageAllocationPlan { get; private set; } = RenderGraphImageAllocationPlan.Empty;
        public RenderGraphBarrierPlan BarrierPlan { get; private set; } = RenderGraphBarrierPlan.Empty;
        public RenderGraphBarrierExecutionPlan BarrierExecutionPlan { get; private set; } = RenderGraphBarrierExecutionPlan.Empty;
        public RenderGraphAliasPlan AliasPlan { get; private set; } = RenderGraphAliasPlan.Empty;
        public RenderGraphDescriptorPlan DescriptorPlan { get; private set; } = RenderGraphDescriptorPlan.Empty;
        public AsyncSchedulePlan? AsyncSchedulePlan { get; private set; }
        public AsyncComputeDeviceProfile? AsyncDeviceProfile { get; private set; }
        public AsyncComputeMode AsyncMode { get; private set; } = AsyncComputeMode.Disabled;
        public IReadOnlyDictionary<string, AsyncComputeMode> AsyncPassOverrides { get; private set; } =
            new Dictionary<string, AsyncComputeMode>(StringComparer.Ordinal);
        public long LastCompileMicroseconds { get; private set; }
        public RenderGraphDiagnosticSnapshot DiagnosticSnapshot => RenderGraphDiagnosticExporter.Export(_declarationPlan, BarrierPlan, AliasPlan);
        
        public void AddPass(RenderPassBase pass)
        {
            if (pass == null)
                throw new ArgumentNullException(nameof(pass));
            _passes.Add(pass);
            System.Diagnostics.Debug.WriteLine($"Render pass added: {pass.Name}");
        }
        
        public void Initialize()
        {
            InitializeDeclarations();
            InitializePasses();
        }

        public void InitializeDeclarations()
        {
            CompileResourceDeclarations();
        }

        public void InitializePasses()
        {
            foreach (var pass in _passes)
                pass.Initialize();
        }

        private void CompileResourceDeclarations()
        {
            long start = Stopwatch.GetTimestamp();
            var registry = new RenderGraphResourceRegistry();
            foreach (var pass in _passes)
                pass.DeclareResources(registry);

            _declarationPlan = registry.Compile();
            RebuildDeclarationDerivedPlans();
            LastCompileMicroseconds = ElapsedMicroseconds(start);
        }

        public void RecompileResourceDeclarations()
        {
            CompileResourceDeclarations();
            if (_lastResolutionContext.HasValue)
                RecompileForResolution(_lastResolutionContext.Value);
        }

        public void RecompileForResolution(RenderGraphResolutionContext context)
        {
            long start = Stopwatch.GetTimestamp();
            RenderGraphMaterializedPlan materialized = RenderGraphResolutionMaterializer.Materialize(
                _declarationPlan,
                context,
                _lastResolutionContext);

            _declarationPlan = materialized.DeclarationPlan;
            ResolvedImages = materialized.ResolvedImages;
            _lastResolutionContext = context;
            RebuildMaterializedDerivedPlans();
            LastCompileMicroseconds = ElapsedMicroseconds(start);
        }

        private void RebuildDeclarationDerivedPlans()
        {
            ImageAllocationPlan = RenderGraphImageAllocationPlan.Empty;
            RebuildAsyncSchedule();
            BarrierPlan = RenderGraphBarrierPlanner.Build(_declarationPlan, AsyncSchedulePlan, AsyncDeviceProfile);
            BarrierExecutionPlan = RenderGraphBarrierExecutionPlan.Build(BarrierPlan);
            AliasPlan = RenderGraphAliasPlan.Empty;
            DescriptorPlan = RenderGraphDescriptorPlanner.Build(_declarationPlan);
        }

        private void RebuildMaterializedDerivedPlans()
        {
            ImageAllocationPlan = RenderGraphImageAllocationPlanner.Build(_declarationPlan);
            RebuildAsyncSchedule();
            BarrierPlan = RenderGraphBarrierPlanner.Build(_declarationPlan, AsyncSchedulePlan, AsyncDeviceProfile);
            BarrierExecutionPlan = RenderGraphBarrierExecutionPlan.Build(BarrierPlan);
            AliasPlan = RenderGraphAliasPlanner.Build(_declarationPlan, ImageAllocationPlan);
            DescriptorPlan = RenderGraphDescriptorPlanner.Build(_declarationPlan);
        }

        public void ConfigureAsyncScheduling(
            AsyncComputeDeviceProfile deviceProfile,
            AsyncComputeMode mode,
            IReadOnlyDictionary<string, AsyncComputeMode>? passOverrides = null)
        {
            AsyncDeviceProfile = deviceProfile ?? throw new ArgumentNullException(nameof(deviceProfile));
            AsyncMode = mode;
            AsyncPassOverrides = passOverrides == null
                ? new Dictionary<string, AsyncComputeMode>(StringComparer.Ordinal)
                : new Dictionary<string, AsyncComputeMode>(passOverrides, StringComparer.Ordinal);
            RebuildAsyncSchedule();
            BarrierPlan = RenderGraphBarrierPlanner.Build(_declarationPlan, AsyncSchedulePlan, AsyncDeviceProfile);
            BarrierExecutionPlan = RenderGraphBarrierExecutionPlan.Build(BarrierPlan);
        }

        private void RebuildAsyncSchedule()
        {
            if (AsyncDeviceProfile == null)
            {
                AsyncSchedulePlan = null;
                return;
            }

            var hints = new List<AsyncPassSchedulingHint>();
            foreach (RenderGraphPassDesc pass in _declarationPlan.Passes)
            {
                if (!pass.AsyncEligible)
                    continue;

                hints.Add(new AsyncPassSchedulingHint(
                    pass.Name,
                    pass.AsyncEligible,
                    pass.PreferredQueue,
                    pass.ExpectedWorkloadScore,
                    pass.BandwidthHeavy,
                    pass.DependencyUrgency == RenderGraphDependencyUrgency.ImmediateGraphicsConsumer));
            }

            AsyncSchedulePlan = AsyncComputeScheduler.Build(_declarationPlan, AsyncDeviceProfile, AsyncMode, hints, AsyncPassOverrides);
        }
        
        public void Execute(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps = null,
            CommandBufferManager? commandBuffers = null,
            bool useSecondaryCommandBuffers = false,
            Action<CommandBuffer, string, RenderGraphQueueClass, RenderGraphBarrierExecutionPhase>? executeCompiledBarriers = null)
        {
            var compiledPasses = new HashSet<string>(_declarationPlan.Diagnostics.CompiledPassOrder, StringComparer.Ordinal);
            foreach (var pass in _passes)
            {
                if (!compiledPasses.Contains(pass.Name))
                {
                    SetPassRecordMicroseconds(sceneData, pass.Name, 0);
                    continue;
                }

                executeCompiledBarriers?.Invoke(cmd, pass.Name, RenderGraphQueueClass.Graphics, RenderGraphBarrierExecutionPhase.BeforePass);
                if (!pass.ShouldExecute(frameIndex, sceneData))
                {
                    executeCompiledBarriers?.Invoke(cmd, pass.Name, RenderGraphQueueClass.Graphics, RenderGraphBarrierExecutionPhase.AfterPass);
                    SetPassRecordMicroseconds(sceneData, pass.Name, 0);
                    continue;
                }

                var barriers = pass.GetBarriers(frameIndex);
                foreach (var barrier in barriers)
                    BarrierBuilder.ExecuteBarrier(cmd, barrier);

                if (useSecondaryCommandBuffers && commandBuffers != null && pass.SupportsSecondaryCommandBuffer)
                {
                    ExecuteSecondaryPass(commandBuffers, cmd, pass, frameIndex, sceneData, timestamps);
                    executeCompiledBarriers?.Invoke(cmd, pass.Name, RenderGraphQueueClass.Graphics, RenderGraphBarrierExecutionPhase.AfterPass);
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

                executeCompiledBarriers?.Invoke(cmd, pass.Name, RenderGraphQueueClass.Graphics, RenderGraphBarrierExecutionPhase.AfterPass);
            }
        }

        public void ExecuteQueue(
            CommandBuffer cmd,
            RenderGraphQueueClass queue,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps = null,
            CommandBufferManager? commandBuffers = null,
            bool useSecondaryCommandBuffers = false,
            Action<CommandBuffer, string, RenderGraphQueueClass, RenderGraphBarrierExecutionPhase>? executeCompiledBarriers = null)
        {
            var compiledPasses = new HashSet<string>(_declarationPlan.Diagnostics.CompiledPassOrder, StringComparer.Ordinal);
            Dictionary<string, RenderGraphPassDesc> declarationByName = _declarationPlan.Passes.ToDictionary(pass => pass.Name, StringComparer.Ordinal);
            Dictionary<string, ScheduledPass> scheduledByName = AsyncSchedulePlan?.Passes.ToDictionary(pass => pass.PassName, StringComparer.Ordinal) ??
                new Dictionary<string, ScheduledPass>(StringComparer.Ordinal);

            foreach (var pass in _passes)
            {
                if (!compiledPasses.Contains(pass.Name))
                {
                    SetPassRecordMicroseconds(sceneData, pass.Name, 0);
                    continue;
                }

                RenderGraphQueueClass passQueue = scheduledByName.TryGetValue(pass.Name, out ScheduledPass? scheduled)
                    ? scheduled.Queue
                    : declarationByName[pass.Name].Queue;
                if (passQueue != queue)
                    continue;

                executeCompiledBarriers?.Invoke(cmd, pass.Name, queue, RenderGraphBarrierExecutionPhase.BeforePass);
                if (!pass.ShouldExecute(frameIndex, sceneData))
                {
                    executeCompiledBarriers?.Invoke(cmd, pass.Name, queue, RenderGraphBarrierExecutionPhase.AfterPass);
                    SetPassRecordMicroseconds(sceneData, pass.Name, 0);
                    continue;
                }

                var barriers = pass.GetBarriers(frameIndex);
                foreach (var barrier in barriers)
                    BarrierBuilder.ExecuteBarrier(cmd, barrier);

                if (queue == RenderGraphQueueClass.Graphics &&
                    useSecondaryCommandBuffers &&
                    commandBuffers != null &&
                    pass.SupportsSecondaryCommandBuffer)
                {
                    ExecuteSecondaryPass(commandBuffers, cmd, pass, frameIndex, sceneData, timestamps);
                    executeCompiledBarriers?.Invoke(cmd, pass.Name, queue, RenderGraphBarrierExecutionPhase.AfterPass);
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

                executeCompiledBarriers?.Invoke(cmd, pass.Name, queue, RenderGraphBarrierExecutionPhase.AfterPass);
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

        private static void SetPassRecordMicroseconds(SceneRenderingData sceneData, string passName, long elapsedMicroseconds)
        {
            switch (passName)
            {
                case "GpuVisibilityPass":
                    sceneData.CpuGpuVisibilityRecordMicroseconds = elapsedMicroseconds;
                    break;
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
                case "MotionVectorPass":
                    sceneData.CpuMotionVectorRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AmbientOcclusionPass":
                    sceneData.CpuAmbientOcclusionRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AmbientOcclusionBlurPass":
                    sceneData.CpuAmbientOcclusionBlurRecordMicroseconds = elapsedMicroseconds;
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
                case "WeightedOitCompositePass":
                    sceneData.CpuWeightedOitCompositeRecordMicroseconds = elapsedMicroseconds;
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
        
        public void OnSwapchainRecreated()
        {
            foreach (var pass in _passes)
                pass.OnSwapchainRecreated();
        }
        
        public void Cleanup()
        {
            foreach (var pass in _passes)
                pass.Cleanup();
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
