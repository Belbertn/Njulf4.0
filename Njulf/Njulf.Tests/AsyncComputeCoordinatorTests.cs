using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Tests;

[TestFixture]
public sealed class AsyncComputeCoordinatorTests
{
    [Test]
    public void DisabledModePreservesGraphicsOnlyHotPath()
    {
        using var graph = new RenderGraph();
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        var settings = new RenderSettings();
        settings.AsyncCompute.Mode = AsyncComputeMode.Disabled;
        var sceneData = new SceneRenderingData();

        Assert.That(
            coordinator.RequiresConcreteResourceBindings(
                settings.AsyncCompute.Mode,
                independentQueueAvailable: true,
                timelineSemaphoreAvailable: true),
            Is.False);

        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));
        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(settings, sceneData));

        Assert.Multiple(() =>
        {
            Assert.That(plan.RequestedMode, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(plan.EffectiveMode, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(plan.SubmissionPlan.Accepted, Is.True);
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.False);
            Assert.That(
                plan.Status,
                Is.EqualTo(
                    "disabled by policy; graphics-only execution is active."));
            Assert.That(coordinator.TerminalWaits, Is.Empty);
        });
    }

    [Test]
    public void ForcedPlanCommitsRelativeTimelineAndLateSubmissionCounts()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 101);
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));

        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(settings, sceneData));
        AsyncComputeRecordingDecision decision =
            coordinator.ValidateForRecording(
                plan,
                graph.ConcreteResourceBindings.Generation);
        AsyncComputeRecordingPublication publication =
            coordinator.CommitRecording(
                decision.Plan,
                new AsyncComputeRecordingSummary(1, 1, 1, 17));

        foreach (AsyncComputeSubmissionSegment segment in
                 plan.SubmissionPlan.Segments)
        {
            if (!segment.IsTerminalGraphicsSegment)
            {
                coordinator.RecordSubmittedNonTerminalSegment(
                    segment.Queue,
                    cpuSubmitMicroseconds: 3);
            }
        }

        AsyncComputeDiagnosticsSnapshot beforeTerminal =
            coordinator.CreateDiagnosticsSnapshot(
                FrameTimingSnapshot.Empty,
                new AsyncComputeDiagnosticsContext(true, true, 0, 1));
        AsyncComputeSubmissionPatch patch =
            coordinator.CompleteTerminalSubmission(0, 29);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SubmissionPlan.Accepted, Is.True);
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.True);
            Assert.That(decision.RecordAsync, Is.True);
            Assert.That(publication.Succeeded, Is.True);
            Assert.That(coordinator.ResolveTimelineValue(0), Is.Zero);
            Assert.That(
                coordinator.TerminalWaits.Select(wait => wait.Value),
                Is.All.GreaterThan(0UL));
            Assert.That(
                beforeTerminal.SubmittedGraphicsSegments,
                Is.EqualTo(plan.SubmissionPlan.GraphicsSegmentCount - 1));
            Assert.That(
                patch.SubmittedGraphicsSegmentCount,
                Is.EqualTo(plan.SubmissionPlan.GraphicsSegmentCount));
            Assert.That(
                patch.SubmittedComputeSegmentCount,
                Is.EqualTo(plan.SubmissionPlan.ComputeSegmentCount));
        });
    }

    [Test]
    public void ExactReceiverConstraintFallsBackWithoutPoisoningRetryState()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 201);
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));

        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(
                settings,
                sceneData,
                "exact receiver-feedback capture requires one graphics queue completion domain"));
        AsyncComputeDiagnosticsSnapshot snapshot =
            coordinator.CreateDiagnosticsSnapshot(
                FrameTimingSnapshot.Empty,
                new AsyncComputeDiagnosticsContext(true, true, 0, 1));

        Assert.Multiple(() =>
        {
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.False);
            Assert.That(plan.EffectiveMode, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(plan.Status, Does.Contain("exact receiver-feedback"));
            Assert.That(snapshot.ValidationFallbackCount, Is.Zero);
            Assert.That(snapshot.LastFallbackReason, Is.Empty);
        });
    }

    [Test]
    public void ExactReceiverDomainAllowsOrderedGraphicsSegmentsAroundCompute()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 211);
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));

        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(
                settings,
                sceneData,
                "exact receiver-feedback capture requires one graphics queue completion domain",
                graphicsCompletionDomainPasses:
                [
                    "DepthPrePass",
                    "ForwardVisibilityCompactionPass"
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.True);
            Assert.That(
                plan.EffectiveMode,
                Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
            Assert.That(
                plan.SubmissionPlan.Segments
                    .Where(segment => segment.Queue == AsyncComputeQueue.Graphics)
                    .SelectMany(segment => segment.Passes),
                Does.Contain("DepthPrePass"));
            Assert.That(
                plan.SubmissionPlan.Segments
                    .Where(segment => segment.Queue == AsyncComputeQueue.Graphics)
                    .SelectMany(segment => segment.Passes),
                Does.Contain("ForwardVisibilityCompactionPass"));
        });
    }

    [Test]
    public void ExactReceiverDomainRejectsProducerScheduledOnCompute()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 212);
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));

        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(
                settings,
                sceneData,
                "exact receiver-feedback capture requires one graphics queue completion domain",
                graphicsCompletionDomainPasses: ["HiZBuildPass"]));

        Assert.Multiple(() =>
        {
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.False);
            Assert.That(plan.EffectiveMode, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(plan.Status, Does.Contain("HiZBuildPass"));
            Assert.That(plan.Status, Does.Contain("compute queue"));
        });
    }

    [Test]
    public void StaleConcreteGenerationIsRecoverableAndCountedOnce()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 301);
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));
        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(settings, sceneData));

        graph.ReplaceConcreteResourceBindings(
            [CreateBufferBinding(302)]);
        AsyncComputeRecordingDecision first =
            coordinator.ValidateForRecording(
                plan,
                graph.ConcreteResourceBindings.Generation);
        AsyncComputeRecordingDecision second =
            coordinator.ValidateForRecording(
                plan,
                graph.ConcreteResourceBindings.Generation);
        AsyncComputeDiagnosticsSnapshot snapshot =
            coordinator.CreateDiagnosticsSnapshot(
                FrameTimingSnapshot.Empty,
                new AsyncComputeDiagnosticsContext(true, true, 0, 1));

        Assert.Multiple(() =>
        {
            Assert.That(first.RecordAsync, Is.False);
            Assert.That(second.RecordAsync, Is.False);
            Assert.That(first.FallbackReason, Does.Contain("generation changed"));
            Assert.That(snapshot.ValidationFallbackCount, Is.EqualTo(1));
            Assert.That(snapshot.StalePlanRejectionCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void ValidationErrorAfterSubmittedComputeLatchesPermanentFallback()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 401);
        var coordinator = new AsyncComputeCoordinator(graph, framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));
        coordinator.RecordSubmittedNonTerminalSegment(
            AsyncComputeQueue.Compute,
            cpuSubmitMicroseconds: 1);

        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(2, 1));
        AsyncComputeFramePlan firstFallback = coordinator.PlanFrame(
            CreateInput(settings, sceneData));
        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(3, 1));
        AsyncComputeFramePlan retainedFallback = coordinator.PlanFrame(
            CreateInput(settings, sceneData));

        Assert.Multiple(() =>
        {
            Assert.That(
                firstFallback.EffectiveMode,
                Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(firstFallback.Status, Does.StartWith("emergency fallback latched:"));
            Assert.That(
                retainedFallback.EffectiveMode,
                Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(
                coordinator.RequiresConcreteResourceBindings(
                    settings.AsyncCompute.Mode,
                    independentQueueAvailable: true,
                    timelineSemaphoreAvailable: true),
                Is.False);
        });
    }

    [Test]
    public void CatalogMapsEveryProductionCandidateToOneAtomicPath()
    {
        foreach (string passName in
                 AsyncComputePassCatalog.ProductionCandidatePasses)
        {
            Assert.That(
                AsyncComputePassCatalog.TryGetPath(
                    passName,
                    out AsyncComputePath path),
                Is.True,
                passName);
            Assert.That(
                AsyncComputePassCatalog.GetRepresentativePass(path),
                Is.Not.Empty,
                passName);
        }
    }

    [TestCase(AsyncComputePath.SimpleDdgiUpdate,
        "SimpleDdgiSchedulePass")]
    [TestCase(AsyncComputePath.FarFieldClipmapBake,
        "FarFieldClipmapBakePass")]
    public void CampaignFeatureOff_ForcesGiPathsToGraphicsWithExactReason(
        AsyncComputePath path,
        string passName)
    {
        using RenderGraph graph = CreateMappedComputeGraph(
            passName,
            bindingHandle: 501UL + (ulong)path);
        var coordinator = new AsyncComputeCoordinator(
            graph,
            framesInFlight: 2);
        var settings = new RenderSettings();
        settings.AsyncCompute.Mode = AsyncComputeMode.Auto;
        settings.GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
        settings.GlobalIllumination.DdgiAsyncComputeEnabled = true;
        settings.GlobalIllumination.FarFieldClipmapEnabled = true;
        settings.PerformanceOptimizations.EnabledFeatures &=
            ~PerformanceOptimizationFeature.AsyncGiFarFieldExecution;
        var sceneData = new SceneRenderingData
        {
            ActiveFeatureIsolation = RenderFeatureIsolationMode.FullFrame,
            SimpleDdgiActive = 1,
            SimpleDdgiProbesUpdated = 1
        };

        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));
        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(
                settings,
                sceneData,
                farFieldBakePending: true));
        AsyncComputePathRuntimeStatus status =
            plan.SubmissionPlan.Paths.Single(candidate =>
                candidate.Path == path);

        Assert.Multiple(() =>
        {
            Assert.That(status.Requested, Is.False);
            Assert.That(
                status.Status,
                Is.EqualTo(AsyncComputePathStatus.DisabledByFeature));
            Assert.That(
                status.Reason,
                Does.Contain("async-gi-far-field performance feature switch"));
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.False);
        });
    }

    [Test]
    public void AutoRejectsCertificationFromDifferentQueueFamilyTopology()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 511);
        var coordinator = new AsyncComputeCoordinator(
            graph,
            framesInFlight: 2);
        var settings = new RenderSettings();
        settings.AsyncCompute.Mode = AsyncComputeMode.Auto;
        var sceneData = new SceneRenderingData
        {
            ActiveFeatureIsolation = RenderFeatureIsolationMode.FullFrame,
            HiZBuildEnabled = true
        };

        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));
        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(
                settings,
                sceneData,
                dedicatedQueueFamilyAvailable: false,
                computeQueueFamily: 0));
        AsyncComputePathRuntimeStatus status =
            plan.SubmissionPlan.Paths.Single(candidate =>
                candidate.Path == AsyncComputePath.HiZBuild);

        Assert.Multiple(() =>
        {
            Assert.That(status.Requested, Is.True);
            Assert.That(status.Status,
                Is.EqualTo(AsyncComputePathStatus.Uncertified));
            Assert.That(status.Reason,
                Does.Contain("distinct dedicated compute queue family"));
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.False);
        });
    }

    [Test]
    public void ForcedValidationAllowsSameFamilyCertificationRun()
    {
        using RenderGraph graph = CreateHiZGraph(bindingHandle: 512);
        var coordinator = new AsyncComputeCoordinator(
            graph,
            framesInFlight: 2);
        (RenderSettings settings, SceneRenderingData sceneData) =
            CreateForcedHiZFrame();

        coordinator.BeginFrame(new AsyncComputeFrameBoundaryInput(1, 0));
        AsyncComputeFramePlan plan = coordinator.PlanFrame(
            CreateInput(
                settings,
                sceneData,
                dedicatedQueueFamilyAvailable: false,
                computeQueueFamily: 0));

        Assert.Multiple(() =>
        {
            Assert.That(plan.EffectiveMode,
                Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
            Assert.That(plan.SubmissionPlan.ContainsAsyncCompute, Is.True,
                plan.SubmissionPlan.FailureReason);
            Assert.That(plan.SubmissionPlan.QueueFamilyOwnershipTransferCount,
                Is.Zero);
        });
    }

    [Test]
    public void TimingEstimatorCountsOnlyGraphicsBeforeFirstConsumerWait()
    {
        var plan = new AsyncComputeSubmissionPlan(
            Accepted: true,
            FailureReason: string.Empty,
            ResourcePlanGeneration: 1,
            Segments:
            [
                CreateTimingSegment(
                    id: 0,
                    AsyncComputeQueue.Compute,
                    ["ComputeWork"],
                    signalValue: 1),
                CreateTimingSegment(
                    id: 1,
                    AsyncComputeQueue.Graphics,
                    ["IndependentGraphics"]),
                CreateTimingSegment(
                    id: 2,
                    AsyncComputeQueue.Graphics,
                    ["ComputeConsumer"],
                    waitValue: 1)
            ],
            Transfers: Array.Empty<QueueOwnershipTransfer>(),
            Paths: Array.Empty<AsyncComputePathRuntimeStatus>());
        var timings = new FrameTimingSnapshot(
        [
            new PassTiming("ComputeWork", 0, 100, true),
            new PassTiming("IndependentGraphics", 0, 40, true),
            new PassTiming("ComputeConsumer", 0, 80, true)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(
                AsyncComputeCoordinator.EstimateOverlapMicroseconds(
                    plan,
                    timings),
                Is.EqualTo(40));
            Assert.That(
                AsyncComputeCoordinator.EstimateFirstConsumerWaitMicroseconds(
                    plan,
                    timings),
                Is.EqualTo(60));
        });
    }

    [Test]
    public void TimingEstimatorDoesNotCountWaitingSegmentAsOverlap()
    {
        var plan = new AsyncComputeSubmissionPlan(
            Accepted: true,
            FailureReason: string.Empty,
            ResourcePlanGeneration: 1,
            Segments:
            [
                CreateTimingSegment(
                    id: 0,
                    AsyncComputeQueue.Compute,
                    ["ComputeWork"],
                    signalValue: 1),
                CreateTimingSegment(
                    id: 1,
                    AsyncComputeQueue.Graphics,
                    ["ComputeConsumer"],
                    waitValue: 1)
            ],
            Transfers: Array.Empty<QueueOwnershipTransfer>(),
            Paths: Array.Empty<AsyncComputePathRuntimeStatus>());
        var timings = new FrameTimingSnapshot(
        [
            new PassTiming("ComputeWork", 0, 100, true),
            new PassTiming("ComputeConsumer", 0, 80, true)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(
                AsyncComputeCoordinator.EstimateOverlapMicroseconds(
                    plan,
                    timings),
                Is.Zero);
            Assert.That(
                AsyncComputeCoordinator.EstimateFirstConsumerWaitMicroseconds(
                    plan,
                    timings),
                Is.EqualTo(100));
        });
    }

    private static AsyncComputePlanningInput CreateInput(
        RenderSettings settings,
        SceneRenderingData sceneData,
        string constraint = "",
        bool farFieldBakePending = false,
        IReadOnlyList<string>? graphicsCompletionDomainPasses = null,
        bool dedicatedQueueFamilyAvailable = true,
        uint graphicsQueueFamily = 0,
        uint computeQueueFamily = 1) =>
        new(
            settings,
            sceneData,
            FrameIndex: 0,
            TimingFrameNumber: 1,
            IndependentQueueAvailable: true,
            TimelineSemaphoreAvailable: true,
            DedicatedQueueFamilyAvailable: dedicatedQueueFamilyAvailable,
            GraphicsQueueFamily: graphicsQueueFamily,
            ComputeQueueFamily: computeQueueFamily,
            QueueFlags.GraphicsBit | QueueFlags.ComputeBit |
                QueueFlags.TransferBit,
            QueueFlags.ComputeBit | QueueFlags.TransferBit,
            "test-device",
            "test-driver",
            FarFieldBakePending: farFieldBakePending,
            BloomMipCount: 0,
            constraint,
            graphicsCompletionDomainPasses);

    private static AsyncComputeSubmissionSegment CreateTimingSegment(
        int id,
        AsyncComputeQueue queue,
        IReadOnlyList<string> passes,
        ulong? waitValue = null,
        ulong? signalValue = null) =>
        new(
            id,
            queue,
            passes,
            Array.Empty<QueueOwnershipTransfer>(),
            Array.Empty<QueueOwnershipTransfer>(),
            waitValue.HasValue
                ?
                [
                    new AsyncComputeTimelineWait(
                        waitValue.Value,
                        PipelineStageFlags2.AllCommandsBit)
                ]
                : Array.Empty<AsyncComputeTimelineWait>(),
            signalValue,
            AccessesSwapchain: false,
            IsTerminalGraphicsSegment: false);

    private static (RenderSettings Settings, SceneRenderingData SceneData)
        CreateForcedHiZFrame()
    {
        var settings = new RenderSettings();
        settings.AsyncCompute.Mode =
            AsyncComputeMode.ForceEnabledForValidation;
        settings.AsyncCompute.ForceValidationPath =
            AsyncComputePath.HiZBuild;
        settings.AsyncCompute.HiZBuildEnabled = true;
        var sceneData = new SceneRenderingData
        {
            ActiveFeatureIsolation = RenderFeatureIsolationMode.FullFrame,
            HiZBuildEnabled = true
        };
        return (settings, sceneData);
    }

    private static RenderGraph CreateHiZGraph(ulong bindingHandle)
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.LightTiles,
            "Async test buffer",
            RenderGraphResourceKind.Buffer,
            null,
            RenderGraphResourceSizePolicy.Dynamic,
            RenderGraphResourceLifetime.Persistent,
            Persistent: true));

        AddPass(
            graph,
            "DepthPrePass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.LightTiles,
                RenderGraphResourceAccess.Write,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderWriteBit,
                ImageLayout.Undefined,
                RenderGraphQueueIntent.Graphics));
        AddPass(
            graph,
            "HiZBuildPass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.LightTiles,
                RenderGraphResourceAccess.ReadWrite,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.Undefined,
                RenderGraphQueueIntent.Compute));
        AddPass(
            graph,
            "ForwardVisibilityCompactionPass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.LightTiles,
                RenderGraphResourceAccess.Read,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderReadBit,
                ImageLayout.Undefined,
                RenderGraphQueueIntent.Graphics));
        graph.ReplaceConcreteResourceBindings(
            [CreateBufferBinding(bindingHandle)]);
        return graph;
    }

    private static RenderGraph CreateMappedComputeGraph(
        string passName,
        ulong bindingHandle)
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.LightTiles,
            "Mapped async test buffer",
            RenderGraphResourceKind.Buffer,
            null,
            RenderGraphResourceSizePolicy.Dynamic,
            RenderGraphResourceLifetime.Persistent,
            Persistent: true));
        AddPass(
            graph,
            passName,
            new RenderGraphResourceUsage(
                RenderGraphResourceId.LightTiles,
                RenderGraphResourceAccess.ReadWrite,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.Undefined,
                RenderGraphQueueIntent.Compute));
        graph.ReplaceConcreteResourceBindings(
            [CreateBufferBinding(bindingHandle)]);
        return graph;
    }

    private static void AddPass(
        RenderGraph graph,
        string name,
        RenderGraphResourceUsage usage)
    {
        graph.AddPass(CreateUninitializedPass(name));
        graph.DeclarePassResources(name, usage);
    }

    private static TestPass CreateUninitializedPass(string name)
    {
        var pass = (TestPass)RuntimeHelpers.GetUninitializedObject(
            typeof(TestPass));
        FieldInfo nameField = typeof(RenderPassBase).GetField(
            "<Name>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RenderPassBase.Name backing field was not found.");
        nameField.SetValue(pass, name);
        return pass;
    }

    private static RenderGraphConcreteResourceBinding CreateBufferBinding(
        ulong handle) =>
        RenderGraphConcreteResourceBinding.ForBuffer(
            RenderGraphResourceId.LightTiles,
            $"async-test-{handle}",
            new Buffer { Handle = handle },
            byteSize: 4096,
            permittedQueueFamilies: new uint[] { 0, 1 },
            initialOwnerQueueFamily: 0,
            allocationGeneration: handle);

    private sealed class TestPass : RenderPassBase
    {
        private TestPass()
            : base("unused", null!, null!, null!)
        {
        }

        public override bool SupportsAsyncCompute =>
            Name == "HiZBuildPass";

        public override RenderGraphQueueIntent QueueIntent =>
            SupportsAsyncCompute
                ? RenderGraphQueueIntent.Compute
                : RenderGraphQueueIntent.Graphics;

        public override void Initialize()
        {
        }

        public override void Execute(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData)
        {
        }
    }
}
