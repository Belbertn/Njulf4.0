using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Tests;

[TestFixture]
public sealed class AsyncComputePhase3Tests
{
    [Test]
    public void Scheduler_DisabledModeProducesNoComputeSubmission()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "disabled", 100, 0, 1024));

        AsyncComputeSubmissionPlan plan = new AsyncComputeScheduler().Compile(new AsyncComputeSchedulerInput(
            AsyncComputeMode.Disabled,
            new AsyncComputeQueueCapabilities(true, 0, 1),
            bindings,
            new[] { Enabled(AsyncComputePath.AmbientOcclusionBlur) },
            StandardThreePasses(RenderGraphResourceId.SceneSubmissionBuffers),
            FrameIndex: 0,
            FirstTimelineValue: 1));

        AsyncComputePathRuntimeStatus status = plan.Paths.Single(path => path.Path == AsyncComputePath.AmbientOcclusionBlur);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(plan.Segments, Is.Empty);
            Assert.That(status.Status, Is.EqualTo(AsyncComputePathStatus.DisabledByPolicy));
            Assert.That(status.Active, Is.False);
        });
    }

    [Test]
    public void Scheduler_CompilesPairedGraphicsComputeGraphicsHandoffs()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "submission", 101, 0, 4096));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                GraphicsPass("producer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Write),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("consumer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Read)
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.ContainsAsyncCompute, Is.True);
            Assert.That(plan.Segments.Select(segment => segment.Queue), Is.EqualTo(new[]
            {
                AsyncComputeQueue.Graphics,
                AsyncComputeQueue.Compute,
                AsyncComputeQueue.Graphics
            }));
            Assert.That(plan.Transfers, Has.Count.EqualTo(2));
            Assert.That(plan.Transfers.All(transfer => transfer.RequiresQueueFamilyOwnershipTransfer), Is.True);
            Assert.That(plan.Transfers[0].SourceSegmentId, Is.EqualTo(0));
            Assert.That(plan.Transfers[0].DestinationSegmentId, Is.EqualTo(1));
            Assert.That(plan.Transfers[1].SourceSegmentId, Is.EqualTo(1));
            Assert.That(plan.Transfers[1].DestinationSegmentId, Is.EqualTo(2));
        });

        foreach (QueueOwnershipTransfer transfer in plan.Transfers)
        {
            AsyncComputeSubmissionSegment source = plan.Segments.Single(segment => segment.Id == transfer.SourceSegmentId);
            AsyncComputeSubmissionSegment destination = plan.Segments.Single(segment => segment.Id == transfer.DestinationSegmentId);
            Assert.That(source.ReleaseTransfers.Select(item => item.Id), Does.Contain(transfer.Id));
            Assert.That(destination.AcquireTransfers.Select(item => item.Id), Does.Contain(transfer.Id));
            Assert.That(source.TimelineSignalValue, Is.Not.Null);
            Assert.That(destination.TimelineWaits.Select(wait => wait.Value), Does.Contain(source.TimelineSignalValue!.Value));
        }

        AsyncComputeSubmissionSegment compute = plan.Segments.Single(segment => segment.Queue == AsyncComputeQueue.Compute);
        Assert.That(
            compute.TimelineWaits.Single(wait => wait.Value == plan.Segments[0].TimelineSignalValue!.Value).StageMask,
            Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
        Assert.That(plan.Segments.Select(segment => segment.TimelineSignalValue ?? 0UL).Where(value => value != 0), Is.Ordered);
    }

    [Test]
    public void Scheduler_RejectsEarlySwapchainAccessAndAcceptsARegeneratedTerminalPlan()
    {
        var bindings = new RenderGraphResourceBindings();
        bindings.Replace(new[]
        {
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "offscreen", 105, 0, 1024),
            CreateImageBinding(RenderGraphResourceId.SwapchainColor, "swapchain-0", 205)
        });
        ulong rejectedGeneration = bindings.Generation;

        AsyncComputeSubmissionPlan rejected = Compile(
            bindings,
            new[]
            {
                GraphicsPass("early-tone-map", RenderGraphResourceId.SwapchainColor, RenderGraphResourceAccess.Write),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("terminal-consumer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Read)
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        AsyncComputePathRuntimeStatus rejectedPath = rejected.Paths.Single(path => path.Path == AsyncComputePath.AmbientOcclusionBlur);
        Assert.Multiple(() =>
        {
            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.ContainsAsyncCompute, Is.False);
            Assert.That(rejected.FailureReason, Does.Contain("acquired swapchain image"));
            Assert.That(rejected.FailureReason, Does.Contain("early-tone-map"));
            Assert.That(rejectedPath.Status, Is.EqualTo(AsyncComputePathStatus.ValidationFallback));
        });

        // A resize/reload/settings binding refresh produces a new immutable plan generation.
        // Keep the swapchain exclusively in its terminal graphics segment and retry normally.
        bindings.Replace(new[]
        {
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "offscreen-rebuilt", 106, 0, 1024),
            CreateImageBinding(RenderGraphResourceId.SwapchainColor, "swapchain-1", 206)
        });
        AsyncComputeSubmissionPlan regenerated = Compile(
            bindings,
            new[]
            {
                GraphicsPass("producer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Write),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                new AsyncComputePassRequest("terminal-present", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.SceneSubmissionBuffers,
                        RenderGraphResourceAccess.Read,
                        PipelineStageFlags2.FragmentShaderBit,
                        AccessFlags2.ShaderReadBit,
                        ImageLayout.Undefined,
                        RenderGraphQueueIntent.Graphics),
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.SwapchainColor,
                        RenderGraphResourceAccess.Write,
                        PipelineStageFlags2.ColorAttachmentOutputBit,
                        AccessFlags2.ColorAttachmentWriteBit,
                        ImageLayout.ColorAttachmentOptimal,
                        RenderGraphQueueIntent.Graphics)
                })
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        Assert.Multiple(() =>
        {
            Assert.That(regenerated.ResourcePlanGeneration, Is.GreaterThan(rejectedGeneration));
            Assert.That(regenerated.Accepted, Is.True, regenerated.FailureReason);
            Assert.That(regenerated.ContainsAsyncCompute, Is.True);
            Assert.That(regenerated.Segments.Single(segment => segment.AccessesSwapchain).IsTerminalGraphicsSegment, Is.True);
            Assert.That(regenerated.Segments.Any(segment => segment.AccessesSwapchain && !segment.IsTerminalGraphicsSegment), Is.False);
        });
    }

    [Test]
    public void RecoverablePlanRetryGate_RetriesAfterResourceOrSettingsRegeneration()
    {
        var gate = new AsyncComputeRecoverablePlanRetryGate();
        var rejectedScope = new AsyncComputePlanRetryScope(ResourcePlanGeneration: 17, SettingsSignature: 101);

        Assert.That(gate.RecordRejected(rejectedScope, "early swapchain access"), Is.True);
        Assert.That(gate.CanAttempt(rejectedScope), Is.False);
        Assert.That(gate.RecordRejected(rejectedScope, "early swapchain access"), Is.False);

        var settingsRegeneratedScope = rejectedScope with { SettingsSignature = 102 };
        var resourceRegeneratedScope = rejectedScope with { ResourcePlanGeneration = 18 };
        Assert.Multiple(() =>
        {
            Assert.That(gate.CanAttempt(settingsRegeneratedScope), Is.True);
            Assert.That(gate.CanAttempt(resourceRegeneratedScope), Is.True);
            Assert.That(gate.ObserveScope(resourceRegeneratedScope), Is.True);
            Assert.That(gate.RejectedScope, Is.Null);
            Assert.That(gate.Reason, Is.Empty);
        });

        gate.RecordRejected(rejectedScope, "stale plan");
        gate.RecordValidatedPlan(rejectedScope);
        Assert.That(gate.CanAttempt(rejectedScope), Is.True);
    }

    [Test]
    public void Scheduler_UsesExactImportedAccelerationStructureProducerScope()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.TlasStorage,
                "TLAS",
                new Buffer { Handle = 108 },
                4096,
                new uint[] { 0, 1 },
                initialOwnerQueueFamily: 0,
                allocationGeneration: 108,
                initialStageMask: PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                initialAccessMask: AccessFlags2.AccelerationStructureWriteBitKhr));

        var passes = new[]
        {
            new AsyncComputePassRequest("trace", AsyncComputePath.SimpleDdgiUpdate, new[]
            {
                new RenderGraphResourceUsage(
                    RenderGraphResourceId.TlasStorage,
                    RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.AccelerationStructureReadBitKhr,
                    ImageLayout.Undefined,
                    RenderGraphQueueIntent.Compute)
            }, AtomicGroup: nameof(AsyncComputePath.SimpleDdgiUpdate))
        };

        AsyncComputeSubmissionPlan plan = Compile(bindings, passes, Enabled(AsyncComputePath.SimpleDdgiUpdate));
        QueueOwnershipTransfer initialTransfer = plan.Transfers.Single(transfer => transfer.SourceQueue == AsyncComputeQueue.Graphics && transfer.DestinationQueue == AsyncComputeQueue.Compute);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(initialTransfer.SourceStageMask, Is.EqualTo(PipelineStageFlags2.AccelerationStructureBuildBitKhr));
            Assert.That(initialTransfer.SourceAccessMask, Is.EqualTo(AccessFlags2.AccelerationStructureWriteBitKhr));
            Assert.That(initialTransfer.DestinationAccessMask, Is.EqualTo(AccessFlags2.AccelerationStructureReadBitKhr));
        });
    }

    [Test]
    public void Scheduler_DoesNotCreateQueueWorkForAnOptionalNoOpPass()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.GpuParticleState, "particle state", 109, 0, 1024));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                ComputePass("particle-simulate", AsyncComputePath.GpuParticles, RenderGraphResourceId.GpuParticleState, RenderGraphResourceAccess.ReadWrite) with
                {
                    WillExecute = false
                }
            },
            Enabled(AsyncComputePath.GpuParticles));

        AsyncComputePathRuntimeStatus path = plan.Paths.Single(status => status.Path == AsyncComputePath.GpuParticles);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(path.Status, Is.EqualTo(AsyncComputePathStatus.DisabledByFeature));
            Assert.That(path.Passes, Is.Empty);
        });
    }

    [Test]
    public void Scheduler_IgnoresSkippedDepthReaderWhenPlanningFirstComputeHandoff()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            RenderGraphConcreteResourceBinding.ForImage(
                RenderGraphResourceId.SceneDepth,
                "Scene Depth",
                new Image { Handle = 110 },
                new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageLayout.DepthStencilAttachmentOptimal,
                new uint[] { 0, 1 },
                initialOwnerQueueFamily: 0,
                allocationGeneration: 110));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                new AsyncComputePassRequest("DepthPrePass", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.SceneDepth,
                        RenderGraphResourceAccess.Write,
                        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                        AccessFlags2.DepthStencilAttachmentWriteBit,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        RenderGraphQueueIntent.Graphics)
                }),
                new AsyncComputePassRequest("MotionVectorPass", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.SceneDepth,
                        RenderGraphResourceAccess.Read,
                        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                        AccessFlags2.DepthStencilAttachmentReadBit,
                        ImageLayout.DepthStencilReadOnlyOptimal,
                        RenderGraphQueueIntent.Graphics)
                })
                {
                    WillExecute = false
                },
                new AsyncComputePassRequest("HiZBuildPass", AsyncComputePath.HiZBuild, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.SceneDepth,
                        RenderGraphResourceAccess.Read,
                        PipelineStageFlags2.ComputeShaderBit,
                        AccessFlags2.ShaderSampledReadBit,
                        ImageLayout.DepthStencilReadOnlyOptimal,
                        RenderGraphQueueIntent.Compute)
                }, AtomicGroup: nameof(AsyncComputePath.HiZBuild))
            },
            Enabled(AsyncComputePath.HiZBuild));

        QueueOwnershipTransfer depthHandoff = plan.Transfers.Single(transfer =>
            transfer.Binding.Resource == RenderGraphResourceId.SceneDepth &&
            transfer.SourceQueue == AsyncComputeQueue.Graphics &&
            transfer.DestinationQueue == AsyncComputeQueue.Compute);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.Segments.SelectMany(segment => segment.Passes), Does.Not.Contain("MotionVectorPass"));
            Assert.That(depthHandoff.OldLayout, Is.EqualTo(ImageLayout.DepthStencilAttachmentOptimal));
            Assert.That(depthHandoff.NewLayout, Is.EqualTo(ImageLayout.DepthStencilReadOnlyOptimal));
        });
    }

    [Test]
    public void Scheduler_DefersComputeWaitUntilTheFirstActualGraphicsConsumer()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "shared", 111, 0, 1024),
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "unrelated", 112, 0, 1024));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                GraphicsPass("producer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Write),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("unrelated", RenderGraphResourceId.LightTiles, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("consumer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Read)
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        AsyncComputeSubmissionSegment compute = plan.Segments.Single(segment => segment.Queue == AsyncComputeQueue.Compute);
        AsyncComputeSubmissionSegment overlapGraphics = plan.Segments.Single(segment => segment.Passes.SequenceEqual(new[] { "unrelated" }));
        AsyncComputeSubmissionSegment consumerGraphics = plan.Segments.Single(segment => segment.Passes.SequenceEqual(new[] { "consumer" }));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.Segments.Select(segment => segment.Queue), Is.EqualTo(new[]
            {
                AsyncComputeQueue.Graphics,
                AsyncComputeQueue.Compute,
                AsyncComputeQueue.Graphics,
                AsyncComputeQueue.Graphics
            }));
            Assert.That(overlapGraphics.TimelineWaits, Is.Empty);
            Assert.That(consumerGraphics.TimelineWaits.Select(wait => wait.Value), Does.Contain(compute.TimelineSignalValue!.Value));
            Assert.That(
                consumerGraphics.TimelineWaits.Single(wait => wait.Value == compute.TimelineSignalValue!.Value).StageMask,
                Is.EqualTo(PipelineStageFlags2.FragmentShaderBit));
            Assert.That(plan.Transfers.Single(transfer => transfer.SourceSegmentId == compute.Id).DestinationSegmentId,
                Is.EqualTo(consumerGraphics.Id));
        });
    }

    [Test]
    public void Scheduler_UsesAnEmptyTerminalSubmissionForComputeOnlyResources()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "compute-only", 113, 0, 1024),
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "unrelated", 114, 0, 1024));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                GraphicsPass("producer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Write),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("unrelated", RenderGraphResourceId.LightTiles, RenderGraphResourceAccess.ReadWrite)
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        AsyncComputeSubmissionSegment compute = plan.Segments.Single(segment => segment.Queue == AsyncComputeQueue.Compute);
        AsyncComputeSubmissionSegment unrelated = plan.Segments.Single(segment => segment.Passes.SequenceEqual(new[] { "unrelated" }));
        AsyncComputeSubmissionSegment terminal = plan.Segments[^1];

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(terminal.IsTerminalGraphicsSegment, Is.True);
            Assert.That(terminal.Passes, Is.Empty);
            Assert.That(unrelated.TimelineWaits, Is.Empty);
            Assert.That(terminal.TimelineWaits.Select(wait => wait.Value), Does.Contain(compute.TimelineSignalValue!.Value));
            Assert.That(plan.Transfers.Single(transfer => transfer.SourceSegmentId == compute.Id).DestinationSegmentId,
                Is.EqualTo(terminal.Id));
        });
    }

    [Test]
    public void Scheduler_UsesExistingSwapchainRunAsTerminalForComputeOnlyReturns()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "compute-only", 117, 0, 1024),
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "post", 118, 0, 1024),
            CreateImageBinding(RenderGraphResourceId.SwapchainColor, "swapchain", 218));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                GraphicsPass("producer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Write),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                new AsyncComputePassRequest("terminal-present", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.LightTiles,
                        RenderGraphResourceAccess.Read,
                        PipelineStageFlags2.FragmentShaderBit,
                        AccessFlags2.ShaderReadBit,
                        ImageLayout.Undefined,
                        RenderGraphQueueIntent.Graphics),
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.SwapchainColor,
                        RenderGraphResourceAccess.Write,
                        PipelineStageFlags2.ColorAttachmentOutputBit,
                        AccessFlags2.ColorAttachmentWriteBit,
                        ImageLayout.ColorAttachmentOptimal,
                        RenderGraphQueueIntent.Graphics)
                })
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        AsyncComputeSubmissionSegment compute = plan.Segments.Single(segment =>
            segment.Queue == AsyncComputeQueue.Compute);
        AsyncComputeSubmissionSegment terminal = plan.Segments[^1];

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(terminal.Passes, Is.EqualTo(new[] { "terminal-present" }));
            Assert.That(terminal.AccessesSwapchain, Is.True);
            Assert.That(terminal.IsTerminalGraphicsSegment, Is.True);
            Assert.That(plan.Segments, Has.Count.EqualTo(3));
            Assert.That(plan.Transfers.Any(transfer =>
                transfer.SourceSegmentId == compute.Id &&
                transfer.DestinationSegmentId == terminal.Id), Is.True);
        });
    }

    [Test]
    public void Scheduler_RejectsASeparatedAtomicAsyncGroup()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "atomic", 115, 0, 1024),
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "interleaved", 116, 0, 1024));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                ComputePass("ssgi-trace", AsyncComputePath.SsgiChain, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("interleaved-graphics", RenderGraphResourceId.LightTiles, RenderGraphResourceAccess.ReadWrite),
                ComputePass("ssgi-denoise", AsyncComputePath.SsgiChain, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite)
            },
            Enabled(AsyncComputePath.SsgiChain));

        AsyncComputePathRuntimeStatus status = plan.Paths.Single(path => path.Path == AsyncComputePath.SsgiChain);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.False);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(status.Status, Is.EqualTo(AsyncComputePathStatus.ValidationFallback));
            Assert.That(status.Reason, Does.Contain("not contiguous"));
        });
    }

    [Test]
    public void Scheduler_ExcludesFeatureIsolatedPassesBeforeCheckingAtomicGroups()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.SceneSubmissionBuffers, "atomic", 117, 0, 1024),
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "isolated", 118, 0, 1024));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                ComputePass("ssgi-trace", AsyncComputePath.SsgiChain, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("feature-isolated", RenderGraphResourceId.LightTiles, RenderGraphResourceAccess.ReadWrite) with
                {
                    EnabledByFeatureIsolation = false
                },
                ComputePass("ssgi-denoise", AsyncComputePath.SsgiChain, RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.ReadWrite),
                GraphicsPass("consumer", RenderGraphResourceId.SceneSubmissionBuffers, RenderGraphResourceAccess.Read)
            },
            Enabled(AsyncComputePath.SsgiChain));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.ContainsAsyncCompute, Is.True);
            Assert.That(plan.Segments.SelectMany(segment => segment.Passes), Does.Not.Contain("feature-isolated"));
            Assert.That(
                plan.Segments.Single(segment => segment.Queue == AsyncComputeQueue.Compute).Passes,
                Is.EqualTo(new[] { "ssgi-trace", "ssgi-denoise" }));
        });
    }

    [Test]
    public void Scheduler_AutoRejectsSsgiWhenCompositeIsItsImmediateGraphicsConsumer()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(RenderGraphResourceId.GiFinalDiffuse, "GI final", 119, 0, 1024));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                ComputePass("ssgi-denoise", AsyncComputePath.SsgiChain, RenderGraphResourceId.GiFinalDiffuse, RenderGraphResourceAccess.Write),
                GraphicsPass("ssgi-composite", RenderGraphResourceId.GiFinalDiffuse, RenderGraphResourceAccess.Read)
            },
            Enabled(AsyncComputePath.SsgiChain),
            mode: AsyncComputeMode.Auto);

        AsyncComputePathRuntimeStatus status = plan.Paths.Single(path => path.Path == AsyncComputePath.SsgiChain);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(status.Status, Is.EqualTo(AsyncComputePathStatus.NoMeasuredBenefit));
            Assert.That(status.Reason, Does.Contain("immediate graphics consumer"));
        });
    }

    [Test]
    public void Scheduler_SameFamilyAndConcurrentBindingsUseMemoryDependenciesWithoutOwnershipTransfer()
    {
        RenderGraphResourceBindings sameFamilyBindings = CreateBindings(
            CreateBufferBinding(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "same-family",
                102,
                3,
                1024,
                queueFamilies: new uint[] { 3 }));
        AsyncComputeSubmissionPlan sameFamilyPlan = Compile(
            sameFamilyBindings,
            StandardThreePasses(RenderGraphResourceId.SceneSubmissionBuffers),
            Enabled(AsyncComputePath.AmbientOcclusionBlur),
            graphicsFamily: 3,
            computeFamily: 3);

        RenderGraphResourceBindings concurrentBindings = CreateBindings(
            CreateBufferBinding(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "concurrent",
                103,
                initialOwner: null,
                byteSize: 1024,
                sharingMode: SharingMode.Concurrent,
                queueFamilies: new uint[] { 4, 5 }));
        AsyncComputeSubmissionPlan concurrentPlan = Compile(
            concurrentBindings,
            StandardThreePasses(RenderGraphResourceId.SceneSubmissionBuffers),
            Enabled(AsyncComputePath.AmbientOcclusionBlur),
            graphicsFamily: 4,
            computeFamily: 5);

        Assert.Multiple(() =>
        {
            Assert.That(sameFamilyPlan.Accepted, Is.True, sameFamilyPlan.FailureReason);
            Assert.That(sameFamilyPlan.Transfers, Is.Not.Empty);
            Assert.That(sameFamilyPlan.Transfers.All(transfer => !transfer.RequiresQueueFamilyOwnershipTransfer), Is.True);
            Assert.That(sameFamilyPlan.Transfers.All(transfer => !transfer.IsConcurrentResource), Is.True);

            Assert.That(concurrentPlan.Accepted, Is.True, concurrentPlan.FailureReason);
            Assert.That(concurrentPlan.Transfers, Is.Not.Empty);
            Assert.That(concurrentPlan.Transfers.All(transfer => !transfer.RequiresQueueFamilyOwnershipTransfer), Is.True);
            Assert.That(concurrentPlan.Transfers.All(transfer => transfer.IsConcurrentResource), Is.True);
        });
    }

    [Test]
    public void Scheduler_RejectsIncompletePathWithoutSubmittingAnyComputeSegment()
    {
        RenderGraphResourceBindings bindings = new();
        bindings.Replace(Array.Empty<RenderGraphConcreteResourceBinding>());

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[] { ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.AmbientOcclusionRaw, RenderGraphResourceAccess.ReadWrite) },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        AsyncComputePathRuntimeStatus path = plan.Paths.Single(status => status.Path == AsyncComputePath.AmbientOcclusionBlur);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(path.Status, Is.EqualTo(AsyncComputePathStatus.MissingResourcePlan));
            Assert.That(path.Reason, Does.Contain("no concrete binding"));
        });
    }

    [Test]
    public void Scheduler_ReportsRequestedPathWithoutRegisteredPassesAsMissingResourcePlan()
    {
        RenderGraphResourceBindings bindings = CreateBindings();

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            Array.Empty<AsyncComputePassRequest>(),
            Enabled(AsyncComputePath.GpuParticles));

        AsyncComputePathRuntimeStatus path = plan.Paths.Single(status => status.Path == AsyncComputePath.GpuParticles);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(path.Status, Is.EqualTo(AsyncComputePathStatus.MissingResourcePlan));
            Assert.That(path.Reason, Does.Contain("no registered render-graph passes"));
        });
    }

    [Test]
    public void Scheduler_CoalescesAdjacentBufferRangesAtEachSegmentBoundary()
    {
        var buffer = new Buffer { Handle = 104 };
        RenderGraphResourceBindings bindings = CreateBindings(
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "left",
                buffer,
                256,
                new uint[] { 0, 1 },
                0,
                byteOffset: 0,
                allocationGeneration: 104),
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "right",
                buffer,
                256,
                new uint[] { 0, 1 },
                0,
                byteOffset: 256,
                allocationGeneration: 104));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            StandardThreePasses(RenderGraphResourceId.SceneSubmissionBuffers),
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.Transfers, Has.Count.EqualTo(2));
            Assert.That(plan.Transfers.All(transfer => transfer.Binding.ByteSize == 512), Is.True);
            Assert.That(plan.Transfers.All(transfer => transfer.AllBindings.Count == 2), Is.True);
        });
    }

    [Test]
    public void Scheduler_PreservesImageLayoutPlanAcrossBothSidesOfHandoff()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            RenderGraphConcreteResourceBinding.ForImage(
                RenderGraphResourceId.AmbientOcclusionRaw,
                "AO raw",
                new Image { Handle = 201 },
                new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageLayout.ColorAttachmentOptimal,
                new uint[] { 0, 1 },
                0,
                allocationGeneration: 201));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                new AsyncComputePassRequest("producer", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.AmbientOcclusionRaw,
                        RenderGraphResourceAccess.Write,
                        PipelineStageFlags2.ColorAttachmentOutputBit,
                        AccessFlags2.ColorAttachmentWriteBit,
                        ImageLayout.ColorAttachmentOptimal,
                        RenderGraphQueueIntent.Graphics)
                }),
                ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, RenderGraphResourceId.AmbientOcclusionRaw, RenderGraphResourceAccess.ReadWrite),
                new AsyncComputePassRequest("consumer", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.AmbientOcclusionRaw,
                        RenderGraphResourceAccess.Read,
                        PipelineStageFlags2.FragmentShaderBit,
                        AccessFlags2.ShaderSampledReadBit,
                        ImageLayout.ShaderReadOnlyOptimal,
                        RenderGraphQueueIntent.Graphics)
                })
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.Transfers[0].OldLayout, Is.EqualTo(ImageLayout.ColorAttachmentOptimal));
            Assert.That(plan.Transfers[0].NewLayout, Is.EqualTo(ImageLayout.General));
            Assert.That(plan.Transfers[1].OldLayout, Is.EqualTo(ImageLayout.General));
            Assert.That(plan.Transfers[1].NewLayout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
            Assert.That(plan.Transfers[0].Binding.SubresourceRange, Is.EqualTo(plan.Transfers[1].Binding.SubresourceRange));
        });
    }

    [Test]
    public void TransferRecorder_AllowsUndefinedSourceLayoutForFirstImageUse()
    {
        RenderGraphConcreteResourceBinding binding = RenderGraphConcreteResourceBinding.ForImage(
            RenderGraphResourceId.AmbientOcclusionScratch,
            "AO scratch",
            new Image { Handle = 211 },
            new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageLayout.Undefined,
            new uint[] { 0, 1 },
            initialOwnerQueueFamily: 0,
            allocationGeneration: 211);
        var transfer = new QueueOwnershipTransfer(
            Id: 1,
            Binding: binding,
            SourceSegmentId: 0,
            DestinationSegmentId: 1,
            SourceQueue: AsyncComputeQueue.Graphics,
            DestinationQueue: AsyncComputeQueue.Compute,
            SourceQueueFamily: 0,
            DestinationQueueFamily: 1,
            SourceStageMask: PipelineStageFlags2.None,
            SourceAccessMask: AccessFlags2.None,
            DestinationStageMask: PipelineStageFlags2.ComputeShaderBit,
            DestinationAccessMask: AccessFlags2.ShaderStorageWriteBit,
            OldLayout: ImageLayout.Undefined,
            NewLayout: ImageLayout.General,
            RequiresQueueFamilyOwnershipTransfer: true,
            IsConcurrentResource: false);

        Assert.That(() => QueueOwnershipTransferRecorder.ValidatePair(transfer), Throws.Nothing);
    }

    [Test]
    public void Scheduler_UsesFinalImageLayoutAndTransitionsConcurrentImagesOnlyAtAcquire()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            RenderGraphConcreteResourceBinding.ForImage(
                RenderGraphResourceId.AmbientOcclusionRaw,
                "AO raw concurrent",
                new Image { Handle = 202 },
                new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageLayout.ColorAttachmentOptimal,
                new uint[] { 0, 1 },
                initialOwnerQueueFamily: null,
                sharingMode: SharingMode.Concurrent,
                allocationGeneration: 202));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            new[]
            {
                new AsyncComputePassRequest("producer", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.AmbientOcclusionRaw,
                        RenderGraphResourceAccess.Write,
                        PipelineStageFlags2.ColorAttachmentOutputBit,
                        AccessFlags2.ColorAttachmentWriteBit,
                        ImageLayout.ColorAttachmentOptimal,
                        RenderGraphQueueIntent.Graphics)
                }),
                new AsyncComputePassRequest("async", AsyncComputePath.AmbientOcclusionBlur, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.AmbientOcclusionRaw,
                        RenderGraphResourceAccess.ReadWrite,
                        PipelineStageFlags2.ComputeShaderBit,
                        AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                        ImageLayout.General,
                        RenderGraphQueueIntent.Compute,
                        ImageLayout.ShaderReadOnlyOptimal)
                }, AtomicGroup: "AO"),
                new AsyncComputePassRequest("consumer", null, new[]
                {
                    new RenderGraphResourceUsage(
                        RenderGraphResourceId.AmbientOcclusionRaw,
                        RenderGraphResourceAccess.Read,
                        PipelineStageFlags2.FragmentShaderBit,
                        AccessFlags2.ShaderSampledReadBit,
                        ImageLayout.ShaderReadOnlyOptimal,
                        RenderGraphQueueIntent.Graphics)
                })
            },
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        QueueOwnershipTransfer graphicsToCompute = plan.Transfers[0];
        QueueOwnershipTransfer computeToGraphics = plan.Transfers[1];
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(graphicsToCompute.IsConcurrentResource, Is.True);
            Assert.That(graphicsToCompute.ReleaseOldLayout, Is.EqualTo(ImageLayout.ColorAttachmentOptimal));
            Assert.That(graphicsToCompute.ReleaseNewLayout, Is.EqualTo(ImageLayout.ColorAttachmentOptimal));
            Assert.That(graphicsToCompute.AcquireNewLayout, Is.EqualTo(ImageLayout.General));
            Assert.That(computeToGraphics.OldLayout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
            Assert.That(computeToGraphics.NewLayout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
            Assert.That(computeToGraphics.ReleaseNewLayout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
        });
    }

    [Test]
    public void Scheduler_RejectsBindingThatDoesNotPermitTheComputeQueueFamily()
    {
        RenderGraphResourceBindings bindings = CreateBindings(
            CreateBufferBinding(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "graphics-only",
                250,
                0,
                128,
                queueFamilies: new uint[] { 0 }));

        AsyncComputeSubmissionPlan plan = Compile(
            bindings,
            StandardThreePasses(RenderGraphResourceId.SceneSubmissionBuffers),
            Enabled(AsyncComputePath.AmbientOcclusionBlur));

        AsyncComputePathRuntimeStatus status = plan.Paths.Single(path => path.Path == AsyncComputePath.AmbientOcclusionBlur);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True);
            Assert.That(plan.ContainsAsyncCompute, Is.False);
            Assert.That(status.Status, Is.EqualTo(AsyncComputePathStatus.MissingResourcePlan));
            Assert.That(status.Reason, Does.Contain("compute queue family 1"));
        });
    }

    [Test]
    public void ConcreteBindings_InvalidateGenerationsAndRejectInvalidResources()
    {
        var bindings = new RenderGraphResourceBindings();
        RenderGraphConcreteResourceBinding valid = CreateBufferBinding(RenderGraphResourceId.LightTiles, "light tiles", 301, 0, 64);
        bindings.Replace(new[] { valid });
        ulong generation = bindings.Generation;
        RenderGraphConcreteResourceBinding resolved = bindings.GetBindings(RenderGraphResourceId.LightTiles).Single();

        Assert.Multiple(() =>
        {
            Assert.That(bindings.IsCurrent(resolved), Is.True);
            Assert.That(bindings.GetCurrentOwner(resolved), Is.EqualTo(0u));
            Assert.That(() => bindings.Replace(new[]
            {
                RenderGraphConcreteResourceBinding.ForBuffer(
                    RenderGraphResourceId.LightTiles,
                    "invalid",
                    default,
                    64,
                    new uint[] { 0 },
                    0)
            }), Throws.TypeOf<InvalidOperationException>());
        });

        bindings.Invalidate();
        Assert.Multiple(() =>
        {
            Assert.That(bindings.Generation, Is.GreaterThan(generation));
            Assert.That(bindings.IsCurrent(resolved), Is.False);
            Assert.That(bindings.GetBindings(RenderGraphResourceId.LightTiles), Is.Empty);
        });
    }

    [Test]
    public void ConcreteBindings_SelectPerFrameResourcesAndInvalidateTheLookupCacheOnReplace()
    {
        var bindings = new RenderGraphResourceBindings();
        bindings.Replace(new[]
        {
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.LightTiles,
                "frame zero",
                new Buffer { Handle = 305 },
                64,
                new uint[] { 0, 1 },
                0,
                frameIndex: 0,
                allocationGeneration: 305),
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.LightTiles,
                "frame one",
                new Buffer { Handle = 306 },
                64,
                new uint[] { 0, 1 },
                0,
                frameIndex: 1,
                allocationGeneration: 306)
        });

        Assert.Multiple(() =>
        {
            Assert.That(bindings.GetBindings(RenderGraphResourceId.LightTiles, 0).Single().Buffer.Handle, Is.EqualTo(305UL));
            Assert.That(bindings.GetBindings(RenderGraphResourceId.LightTiles, 1).Single().Buffer.Handle, Is.EqualTo(306UL));
        });

        bindings.Replace(new[]
        {
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.LightTiles,
                "replacement",
                new Buffer { Handle = 307 },
                64,
                new uint[] { 0, 1 },
                0,
                frameIndex: 0,
                allocationGeneration: 307)
        });

        Assert.That(bindings.GetBindings(RenderGraphResourceId.LightTiles, 0).Single().Buffer.Handle, Is.EqualTo(307UL));
    }

    [Test]
    public void ConcreteBindings_RejectOverlappingAndOutOfRangeBufferRanges()
    {
        var buffer = new Buffer { Handle = 302 };
        var bindings = new RenderGraphResourceBindings();

        Assert.Multiple(() =>
        {
            Assert.That(() => bindings.Replace(new[]
            {
                RenderGraphConcreteResourceBinding.ForBuffer(
                    RenderGraphResourceId.LightTiles,
                    "out-of-range",
                    buffer,
                    64,
                    new uint[] { 0 },
                    0,
                    byteOffset: 32,
                    allocationGeneration: 302,
                    allocationSize: 64)
            }), Throws.TypeOf<InvalidOperationException>());

            Assert.That(() => bindings.Replace(new[]
            {
                RenderGraphConcreteResourceBinding.ForBuffer(
                    RenderGraphResourceId.LightTiles,
                    "left",
                    buffer,
                    64,
                    new uint[] { 0 },
                    0,
                    allocationGeneration: 302),
                RenderGraphConcreteResourceBinding.ForBuffer(
                    RenderGraphResourceId.LightTiles,
                    "right",
                    buffer,
                    64,
                    new uint[] { 0 },
                    0,
                    byteOffset: 32,
                    allocationGeneration: 302)
            }), Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void ConcreteBindings_TrackCompatibleExactImageAliasesAsOnePhysicalOwnershipRange()
    {
        var image = new Image { Handle = 303 };
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
        RenderGraphResourceBindings bindings = CreateBindings(
            RenderGraphConcreteResourceBinding.ForImage(
                RenderGraphResourceId.EnvironmentMaps,
                "environment texture",
                image,
                range,
                ImageLayout.ShaderReadOnlyOptimal,
                new uint[] { 0, 1 },
                0,
                allocationGeneration: 303),
            RenderGraphConcreteResourceBinding.ForImage(
                RenderGraphResourceId.MaterialTextures,
                "material texture alias",
                image,
                range,
                ImageLayout.ShaderReadOnlyOptimal,
                new uint[] { 0, 1 },
                0,
                allocationGeneration: 303));

        var passes = new[]
        {
            new AsyncComputePassRequest("environment producer", null, new[]
            {
                new RenderGraphResourceUsage(
                    RenderGraphResourceId.EnvironmentMaps,
                    RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderSampledReadBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    RenderGraphQueueIntent.Graphics)
            }),
            new AsyncComputePassRequest("material trace", AsyncComputePath.SimpleDdgiUpdate, new[]
            {
                new RenderGraphResourceUsage(
                    RenderGraphResourceId.MaterialTextures,
                    RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderSampledReadBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    RenderGraphQueueIntent.Compute)
            }, AtomicGroup: nameof(AsyncComputePath.SimpleDdgiUpdate))
        };

        AsyncComputeSubmissionPlan plan = Compile(bindings, passes, Enabled(AsyncComputePath.SimpleDdgiUpdate));
        QueueOwnershipTransfer graphicsToCompute = plan.Transfers.Single(transfer =>
            transfer.SourceQueue == AsyncComputeQueue.Graphics &&
            transfer.DestinationQueue == AsyncComputeQueue.Compute);
        RenderGraphConcreteResourceBinding environmentBinding =
            bindings.GetBindings(RenderGraphResourceId.EnvironmentMaps, 0).Single();

        bindings.CommitOwner(graphicsToCompute.Binding, graphicsToCompute.DestinationQueueFamily);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Accepted, Is.True, plan.FailureReason);
            Assert.That(plan.Transfers, Has.Count.EqualTo(2));
            Assert.That(graphicsToCompute.Binding.Image.Handle, Is.EqualTo(image.Handle));
            Assert.That(bindings.GetCurrentOwner(environmentBinding), Is.EqualTo(1));
        });
    }

    [Test]
    public void ConcreteBindings_RejectAliasingAcrossDifferentGraphResources()
    {
        var buffer = new Buffer { Handle = 304 };
        var bindings = new RenderGraphResourceBindings();

        Assert.That(() => bindings.Replace(new[]
        {
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "submission alias",
                buffer,
                128,
                new uint[] { 0, 1 },
                0,
                allocationGeneration: 304),
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.LightTiles,
                "light alias",
                buffer,
                128,
                new uint[] { 0, 1 },
                0,
                byteOffset: 64,
                allocationGeneration: 304)
        }), Throws.InvalidOperationException.With.Message.Contains("overlap"));
    }

    [Test]
    public void ConcreteResourcePlan_ReactivationReusesValidatedImmutableLookups()
    {
        using var graph = new RenderGraph();
        graph.RegisterResources(new[]
        {
            new RenderGraphResourceDescriptor(
                RenderGraphResourceId.LightTiles,
                "Light tiles",
                RenderGraphResourceKind.Buffer,
                null,
                RenderGraphResourceSizePolicy.Dynamic,
                RenderGraphResourceLifetime.Persistent,
                Persistent: true)
        });
        RenderGraphResourcePlan plan = graph.CreateConcreteResourcePlan(new[]
        {
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "cached", 401, 0, 64)
        });

        graph.ActivateConcreteResourcePlan(plan, resetState: true);
        IReadOnlyList<RenderGraphConcreteResourceBinding> firstLookup =
            graph.ConcreteResourceBindings.GetBindings(RenderGraphResourceId.LightTiles, 0);
        ulong generation = graph.ConcreteResourceBindings.Generation;

        graph.ActivateConcreteResourcePlan(plan);
        IReadOnlyList<RenderGraphConcreteResourceBinding> secondLookup =
            graph.ConcreteResourceBindings.GetBindings(RenderGraphResourceId.LightTiles, 0);

        Assert.Multiple(() =>
        {
            Assert.That(graph.ConcreteResourceBindings.Generation, Is.EqualTo(generation));
            Assert.That(plan.Generation, Is.EqualTo(generation));
            Assert.That(ReferenceEquals(firstLookup, secondLookup), Is.True);
            Assert.That(secondLookup.Single().ResourcePlanGeneration, Is.EqualTo(generation));
        });
    }

    [Test]
    public void ConcreteResourcePlan_UsesTypedIdentityInsteadOfDiagnosticName()
    {
        var bindings = new RenderGraphResourceBindings();
        RenderGraphConcreteResourceBinding first =
            CreateBufferBinding(RenderGraphResourceId.LightTiles, "first name", 402, 0, 64);
        RenderGraphConcreteResourceBinding renamed = first with { Name = "different diagnostic name" };

        Assert.That(
            () => bindings.Replace(new[] { first, renamed }),
            Throws.InvalidOperationException.With.Message.Contains("Duplicate concrete binding identity"));
    }

    [Test]
    public void ConcreteResourcePlan_ReadsLiveLayoutWithoutRebuildingGeneration()
    {
        ImageLayout liveLayout = ImageLayout.ColorAttachmentOptimal;
        var bindings = new RenderGraphResourceBindings();
        bindings.Replace(new[]
        {
            RenderGraphConcreteResourceBinding.ForImage(
                RenderGraphResourceId.AmbientOcclusionRaw,
                "live layout",
                new Image { Handle = 403 },
                new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                liveLayout,
                new uint[] { 0, 1 },
                initialOwnerQueueFamily: 0,
                allocationGeneration: 403,
                layoutProvider: () => liveLayout)
        });
        RenderGraphConcreteResourceBinding binding =
            bindings.GetBindings(RenderGraphResourceId.AmbientOcclusionRaw).Single();
        ulong generation = bindings.Generation;

        liveLayout = ImageLayout.ShaderReadOnlyOptimal;

        Assert.Multiple(() =>
        {
            Assert.That(bindings.GetCurrentLayout(binding), Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
            Assert.That(bindings.Generation, Is.EqualTo(generation));
        });
    }

    [Test]
    public void RenderGraph_RejectsConcreteBindingWithTheWrongResourceKind()
    {
        using var graph = new RenderGraph();
        graph.RegisterResources(new[]
        {
            new RenderGraphResourceDescriptor(
                RenderGraphResourceId.AmbientOcclusionRaw,
                "AO raw",
                RenderGraphResourceKind.Image,
                Format.R8Unorm,
                RenderGraphResourceSizePolicy.HalfResolution,
                RenderGraphResourceLifetime.Persistent,
                Persistent: true)
        });

        Assert.That(() => graph.ReplaceConcreteResourceBindings(new[]
        {
            CreateBufferBinding(RenderGraphResourceId.AmbientOcclusionRaw, "wrong kind", 303, 0, 64)
        }), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void SettingsMigrationAndRoundTrip_PreserveExplicitAsyncPolicy()
    {
        string legacyPath = Path.GetTempFileName();
        string roundTripPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(legacyPath, """
            {
              "Version": 1,
              "AsyncCompute": {
                "Enabled": true,
                "DdgiUpdateEnabled": false
              }
            }
            """);
            RenderSettings legacy = RenderSettings.Load(legacyPath);

            var settings = new RenderSettings();
            settings.AsyncCompute.Mode = AsyncComputeMode.Auto;
            settings.AsyncCompute.SimpleDdgiUpdateEnabled = false;
            settings.AsyncCompute.FullDdgiUpdateEnabled = true;
            settings.AsyncCompute.FarFieldClipmapBakeEnabled = false;
            settings.AsyncCompute.SsgiChainEnabled = false;
            settings.AsyncCompute.AutoMinimumSampleCount = 42;
            settings.AsyncCompute.AutoWarmupFrameCount = 12;
            settings.AsyncCompute.AutoMinimumAbsoluteBenefitMilliseconds = 0.4f;
            settings.AsyncCompute.AutoMinimumRelativeBenefit = 0.07f;
            settings.AsyncCompute.AutoDecisionCooldownFrames = 33;
            settings.Save(roundTripPath);
            RenderSettings roundTrip = RenderSettings.Load(roundTripPath);

            Assert.Multiple(() =>
            {
                Assert.That(legacy.AsyncCompute.Mode, Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
                Assert.That(legacy.AsyncCompute.SimpleDdgiUpdateEnabled, Is.False);
                Assert.That(legacy.AsyncCompute.FullDdgiUpdateEnabled, Is.False);
                Assert.That(roundTrip.AsyncCompute.Mode, Is.EqualTo(AsyncComputeMode.Auto));
                Assert.That(roundTrip.AsyncCompute.SimpleDdgiUpdateEnabled, Is.False);
                Assert.That(roundTrip.AsyncCompute.FullDdgiUpdateEnabled, Is.True);
                Assert.That(roundTrip.AsyncCompute.FarFieldClipmapBakeEnabled, Is.False);
                Assert.That(roundTrip.AsyncCompute.SsgiChainEnabled, Is.False);
                Assert.That(roundTrip.AsyncCompute.AutoMinimumSampleCount, Is.EqualTo(42));
                Assert.That(roundTrip.AsyncCompute.AutoWarmupFrameCount, Is.EqualTo(12));
                Assert.That(roundTrip.AsyncCompute.AutoMinimumAbsoluteBenefitMilliseconds, Is.EqualTo(0.4f));
                Assert.That(roundTrip.AsyncCompute.AutoMinimumRelativeBenefit, Is.EqualTo(0.07f));
                Assert.That(roundTrip.AsyncCompute.AutoDecisionCooldownFrames, Is.EqualTo(33));
            });
        }
        finally
        {
            File.Delete(legacyPath);
            File.Delete(roundTripPath);
        }
    }

    [Test]
    public void SettingsMigration_ModeAbsentKeepsExistingInstallOnGraphicsOnly()
    {
        string legacyPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(legacyPath, """
            {
              "Version": 1,
              "AsyncCompute": {
                "HiZBuildEnabled": true
              }
            }
            """);

            RenderSettings legacy = RenderSettings.Load(legacyPath);
            Assert.That(legacy.AsyncCompute.Mode, Is.EqualTo(AsyncComputeMode.Disabled));
        }
        finally
        {
            File.Delete(legacyPath);
        }
    }

    [Test]
    public void SettingsMigration_CurrentSchemaWithoutModeUsesFreshInstallAutoDefault()
    {
        string settingsPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(settingsPath, """
            {
              "Version": 3,
              "AsyncCompute": {
                "HiZBuildEnabled": true
              }
            }
            """);

            RenderSettings settings = RenderSettings.Load(settingsPath);
            Assert.That(settings.AsyncCompute.Mode, Is.EqualTo(AsyncComputeMode.Auto));
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Test]
    public void SettingsMigration_CurrentSchemaWithoutSsgiOptionsKeepsSsgiOptIn()
    {
        string settingsPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(settingsPath, """
            {
              "Version": 3,
              "GlobalIllumination": {
                "Mode": "Hybrid",
                "UseDdgi": true
              },
              "AsyncCompute": {
                "Mode": "Auto"
              }
            }
            """);

            RenderSettings settings = RenderSettings.Load(settingsPath);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiSimpleEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseSimpleDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.False);
                Assert.That(settings.AsyncCompute.SsgiChainEnabled, Is.False);
            });
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Test]
    public void TimingPolicy_UsesWarmupPromotionAndTailRegressionDemotion()
    {
        var settings = new AsyncComputeSettings
        {
            AutoMinimumSampleCount = 3,
            AutoWarmupFrameCount = 0,
            AutoMinimumAbsoluteBenefitMilliseconds = 0.25f,
            AutoMinimumRelativeBenefit = 0.03f,
            AutoDecisionCooldownFrames = 0
        };
        var policy = new AsyncComputeTimingPolicy(windowCapacity: 3);
        var key = new AsyncComputeTimingKey("device", "driver", "workload", AsyncComputePath.Bloom);

        for (int frame = 0; frame < 3; frame++)
        {
            policy.RecordGraphicsOnly(key, 10.0);
            policy.RecordAsync(key, 9.0, 1.0, 0.1, 0.1, 0.02);
        }
        AsyncComputeTimingDecision promoted = policy.Evaluate(key, settings, frameNumber: 3);

        for (int frame = 0; frame < 3; frame++)
            policy.RecordAsync(key, 11.0, 1.0, 0.1, 0.5, 0.02);
        AsyncComputeTimingDecision demoted = policy.Evaluate(key, settings, frameNumber: 7);

        Assert.Multiple(() =>
        {
            Assert.That(promoted.Status, Is.EqualTo(AsyncComputePathStatus.Enabled));
            Assert.That(promoted.Eligible, Is.True);
            Assert.That(demoted.Status, Is.EqualTo(AsyncComputePathStatus.NoMeasuredBenefit));
            Assert.That(demoted.Eligible, Is.False);
        });
    }

    [Test]
    public void TimingPolicy_OnlyProbesAfterGraphicsBaselineAndUntilAsyncWindowIsComplete()
    {
        var settings = new AsyncComputeSettings
        {
            AutoMinimumSampleCount = 2,
            AutoWarmupFrameCount = 3
        };
        var policy = new AsyncComputeTimingPolicy(windowCapacity: 4);
        var key = new AsyncComputeTimingKey("device", "driver", "workload", AsyncComputePath.HiZBuild);

        Assert.That(policy.CanCollectAsyncProbe(key, settings, frameNumber: 3), Is.False);
        policy.RecordGraphicsOnly(key, 10.0);
        policy.RecordGraphicsOnly(key, 10.0);
        Assert.That(policy.CanCollectAsyncProbe(key, settings, frameNumber: 2), Is.False);
        Assert.That(policy.CanCollectAsyncProbe(key, settings, frameNumber: 3), Is.True);

        policy.RecordAsync(key, 9.0, 1.0, 0.1, 0.1, 0.01);
        Assert.That(policy.CanCollectAsyncProbe(key, settings, frameNumber: 4), Is.True);
        policy.RecordAsync(key, 9.0, 1.0, 0.1, 0.1, 0.01);
        Assert.That(policy.CanCollectAsyncProbe(key, settings, frameNumber: 5), Is.False);
    }

    [Test]
    public void TimingPolicy_IncludesMeasuredCpuBarrierAndSubmissionCostInPromotion()
    {
        var settings = new AsyncComputeSettings
        {
            AutoMinimumSampleCount = 3,
            AutoWarmupFrameCount = 0,
            AutoMinimumAbsoluteBenefitMilliseconds = 0.25f,
            AutoMinimumRelativeBenefit = 0.03f,
            AutoDecisionCooldownFrames = 0
        };
        var policy = new AsyncComputeTimingPolicy(windowCapacity: 3);
        var key = new AsyncComputeTimingKey("device", "driver", "workload", AsyncComputePath.Fog);

        for (int frame = 0; frame < 3; frame++)
        {
            policy.RecordGraphicsOnly(key, 10.0, cpuSubmitMilliseconds: 0.05);
            policy.RecordAsync(
                key,
                frameMilliseconds: 9.8,
                computeDispatchMilliseconds: 1.0,
                transferBarrierMilliseconds: 0.25,
                graphicsWaitMilliseconds: 0.0,
                cpuSubmitMilliseconds: 0.15);
        }

        AsyncComputeTimingDecision decision = policy.Evaluate(key, settings, frameNumber: 3);
        Assert.That(decision.Status, Is.EqualTo(AsyncComputePathStatus.NoMeasuredBenefit));
        Assert.That(decision.Eligible, Is.False);
    }

    private static AsyncComputeSubmissionPlan Compile(
        RenderGraphResourceBindings bindings,
        IReadOnlyList<AsyncComputePassRequest> passes,
        AsyncComputePathEligibility eligibility,
        uint graphicsFamily = 0,
        uint computeFamily = 1,
        AsyncComputeMode mode = AsyncComputeMode.ForceEnabledForValidation)
    {
        return new AsyncComputeScheduler().Compile(new AsyncComputeSchedulerInput(
            mode,
            new AsyncComputeQueueCapabilities(true, graphicsFamily, computeFamily),
            bindings,
            new[] { eligibility },
            passes,
            FrameIndex: 0,
            FirstTimelineValue: 1));
    }

    private static AsyncComputePathEligibility Enabled(AsyncComputePath path) =>
        new(path, RequestedByFeature: true, AutoTimingEligible: true, AsyncComputePathStatus.Enabled, "test");

    private static RenderGraphResourceBindings CreateBindings(params RenderGraphConcreteResourceBinding[] bindings)
    {
        var result = new RenderGraphResourceBindings();
        result.Replace(bindings);
        return result;
    }

    private static RenderGraphConcreteResourceBinding CreateBufferBinding(
        RenderGraphResourceId resource,
        string name,
        ulong handle,
        uint? initialOwner,
        ulong byteSize,
        SharingMode sharingMode = SharingMode.Exclusive,
        IReadOnlyList<uint>? queueFamilies = null)
    {
        return RenderGraphConcreteResourceBinding.ForBuffer(
            resource,
            name,
            new Buffer { Handle = handle },
            byteSize,
            queueFamilies ?? new uint[] { 0, 1 },
            initialOwner,
            sharingMode,
            allocationGeneration: handle);
    }

    private static RenderGraphConcreteResourceBinding CreateImageBinding(
        RenderGraphResourceId resource,
        string name,
        ulong handle)
    {
        return RenderGraphConcreteResourceBinding.ForImage(
            resource,
            name,
            new Image { Handle = handle },
            new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageLayout.ColorAttachmentOptimal,
            new uint[] { 0, 1 },
            initialOwnerQueueFamily: 0,
            allocationGeneration: handle);
    }

    private static IReadOnlyList<AsyncComputePassRequest> StandardThreePasses(RenderGraphResourceId resource) =>
        new[]
        {
            GraphicsPass("producer", resource, RenderGraphResourceAccess.Write),
            ComputePass("async", AsyncComputePath.AmbientOcclusionBlur, resource, RenderGraphResourceAccess.ReadWrite),
            GraphicsPass("consumer", resource, RenderGraphResourceAccess.Read)
        };

    private static AsyncComputePassRequest GraphicsPass(
        string name,
        RenderGraphResourceId resource,
        RenderGraphResourceAccess access)
    {
        return new AsyncComputePassRequest(name, null, new[]
        {
            new RenderGraphResourceUsage(
                resource,
                access,
                PipelineStageFlags2.FragmentShaderBit,
                access == RenderGraphResourceAccess.Read ? AccessFlags2.ShaderReadBit : AccessFlags2.ShaderWriteBit,
                ImageLayout.Undefined,
                RenderGraphQueueIntent.Graphics)
        });
    }

    private static AsyncComputePassRequest ComputePass(
        string name,
        AsyncComputePath path,
        RenderGraphResourceId resource,
        RenderGraphResourceAccess access)
    {
        AccessFlags2 mask = access switch
        {
            RenderGraphResourceAccess.Read => AccessFlags2.ShaderStorageReadBit,
            RenderGraphResourceAccess.Write => AccessFlags2.ShaderStorageWriteBit,
            _ => AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        };
        return new AsyncComputePassRequest(name, path, new[]
        {
            new RenderGraphResourceUsage(
                resource,
                access,
                PipelineStageFlags2.ComputeShaderBit,
                mask,
                ImageLayout.General,
                RenderGraphQueueIntent.Compute)
        },
        AtomicGroup: path.ToString());
    }
}
