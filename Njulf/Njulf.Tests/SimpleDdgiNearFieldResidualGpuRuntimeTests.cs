using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualGpuRuntimeTests
{
    [Test]
    public void ManagedAbiHasExactSizesAndPermitsOnlyDirectDiffuseAndEmissive()
    {
        Assert.DoesNotThrow(SimpleDdgiNearFieldResidualGpuAbi.VerifyManagedLayout);
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualHitMetadata>(),
                Is.EqualTo(40));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualTraceFrameConstants>(),
                Is.EqualTo(160));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualTelemetryHeader>(),
                Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualTileRecord>(),
                Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualResetPushConstants>(),
                Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualTracePushConstants>(),
                Is.EqualTo(84));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualTemporalPushConstants>(),
                Is.EqualTo(96));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualFilterPushConstants>(),
                Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualCompositePushConstants>(),
                Is.EqualTo(48));
            Assert.That(SimpleDdgiNearFieldResidualGpuAbi.HasOnlyAllowedTraceSources(
                SimpleDdgiNearFieldResidualGpuAbi.AllowedTraceSourceTerms), Is.True);
            Assert.That(SimpleDdgiNearFieldResidualGpuAbi.HasOnlyAllowedTraceSources(
                SimpleDdgiNearFieldResidualGpuAbi.AllowedTraceSourceTerms |
                (uint)SimpleDdgiNearFieldTraceSourceTerm.DdgiIndirect), Is.False);
            Assert.That(SimpleDdgiNearFieldResidualGpuAllocation.ExpectedDescriptorCount(
                CreateLayout()), Is.EqualTo(19u));
        });
    }

    [Test]
    public void PhysicalIntegrationAcceptsTheRendererHalfResolutionHiZContract()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiNearFieldResidualVulkanRuntime.IsCompatibleHiZExtent(
                    layout,
                    width: 320,
                    height: 180),
                Is.True);
            Assert.That(
                SimpleDdgiNearFieldResidualVulkanRuntime.IsCompatibleHiZExtent(
                    layout,
                    width: 640,
                    height: 360),
                Is.False);
            Assert.That(
                SimpleDdgiNearFieldResidualVulkanRuntime.IsCompatibleHiZExtent(
                    layout,
                    width: 320,
                    height: 179),
                Is.False);
        });
    }

    [Test]
    public void ValidityRenderTargetUsesTheFourByteR32UintAccountingContract()
    {
        Assert.That(
            RenderTarget.CalculateByteSize(320, 180, Format.R32Uint),
            Is.EqualTo(320UL * 180UL * 4UL));
    }

    [Test]
    public void ReconcileFailsClosedForProhibitedTraceSourceWithoutAllocating()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        SimpleDdgiNearFieldResidualGpuConfiguration reference =
            CreateReferenceConfiguration(layout);
        SimpleDdgiNearFieldResidualGpuConfiguration configuration = reference with
            {
                TraceSourceContract = reference.TraceSourceContract with
                    {
                        Terms = SimpleDdgiNearFieldTraceSourceTerm.DirectDiffuse |
                            SimpleDdgiNearFieldTraceSourceTerm.DdgiIndirect
                    }
            };
        var allocator = new FakeAllocator();
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();

        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot snapshot = manager.Reconcile(
            new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true, layout, configuration, CompleteIntegration()), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsEffectivelyEnabled, Is.False);
            Assert.That(snapshot.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.Disabled));
            Assert.That(snapshot.Reason,
                Is.EqualTo("near-field-trace-source-must-contain-only-direct-diffuse-and-emissive"));
            Assert.That(allocator.AllocateCount, Is.Zero);
            Assert.That(allocator.Retired, Is.Empty);
        });
    }

    [Test]
    public void ReconcileFailsClosedUntilEveryRendererIntegrationPrerequisiteIsDeclared()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        var allocator = new FakeAllocator();
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();
        SimpleDdgiNearFieldResidualGpuIntegrationCapabilities incomplete =
            CompleteIntegration() with { DoubleBufferedHistoryIdentityAvailable = false };

        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot snapshot = manager.Reconcile(
            new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true,
                layout,
                CreateReferenceConfiguration(layout),
                incomplete),
            allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsEffectivelyEnabled, Is.False);
            Assert.That(snapshot.IntegrationPrerequisitesDeclared, Is.False);
            Assert.That(snapshot.Reason,
                Is.EqualTo("near-field-double-buffered-history-identity-unavailable"));
            Assert.That(allocator.AllocateCount, Is.Zero);
        });
    }

    [Test]
    public void HistoryRevisionRejectsProjectionOriginSceneSourceLayoutAndB3OwnershipChanges()
    {
        var baseline = new SimpleDdgiNearFieldResidualGpuHistoryRevision(
            ViewportRevision: 1u,
            HiZRevision: 2u,
            TraceSourceAbiRevision: 3u,
            EffectiveModeRevision: 4u,
            ExposureDomainRevision: 5u,
            CameraCut: false,
            ProjectionJitterRevision: 6u,
            OriginRebaseRevision: 7u,
            SceneGeneration: 8u,
            TraceSourceContentRevision: 9u,
            NearFieldLayoutRevision: 10u,
            B3OwnershipRevision: 11u,
            TraceSourceLayoutRevision: 12u);

        Assert.Multiple(() =>
        {
            Assert.That(baseline.Matches(baseline), Is.True);
            Assert.That(baseline.Matches(baseline with { ProjectionJitterRevision = 12u }), Is.False);
            Assert.That(baseline.Matches(baseline with { OriginRebaseRevision = 12u }), Is.False);
            Assert.That(baseline.Matches(baseline with { SceneGeneration = 12u }), Is.False);
            Assert.That(baseline.Matches(baseline with { TraceSourceContentRevision = 12u }), Is.False);
            Assert.That(baseline.Matches(baseline with { TraceSourceLayoutRevision = 13u }), Is.False);
            Assert.That(baseline.Matches(baseline with { NearFieldLayoutRevision = 12u }), Is.False);
            Assert.That(baseline.Matches(baseline with { B3OwnershipRevision = 12u }), Is.False);
        });
    }

    [Test]
    public void CompleteFramePublishesHistoryOnlyAfterEveryStageWitnessesItsSafetyContract()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        var allocator = new FakeAllocator();
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();
        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot allocated = manager.Reconcile(
            new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true,
                layout,
                CreateReferenceConfiguration(layout),
                CompleteIntegration()),
            allocator);
        Assert.That(allocated.State,
            Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid));

        SimpleDdgiNearFieldResidualGpuBeginFrameResult begin = manager.BeginFrame(
            new SimpleDdgiNearFieldResidualGpuHistoryRevision(
                ViewportRevision: 4u,
                HiZRevision: 8u,
                TraceSourceAbiRevision: 1u,
                EffectiveModeRevision: 3u,
                ExposureDomainRevision: 9u,
                CameraCut: false,
                TraceSourceContentRevision: 1u,
                TraceSourceLayoutRevision: 1u));
        Assert.That(begin.Started, Is.True);
        Assert.That(begin.HistoryInvalidated, Is.True);

        Assert.That(manager.CompleteTrace(begin.Token,
            new SimpleDdgiNearFieldResidualGpuTraceCompletion(
                QueueOrderedCommandsRecorded: true,
                TraceSourceBindingVerified: true,
                StableSampleIdentityVerified: true,
                ReceiverBrdfAndPdfVerified: true,
                InvalidAndMissCandidatesZeroed: true,
                TileRecordsInitializedAndBounded: true)).Accepted, Is.True);
        Assert.That(manager.CompleteTemporal(begin.Token,
            new SimpleDdgiNearFieldResidualGpuTemporalCompletion(
                QueueOrderedCommandsRecorded: true,
                HistoryWritesContainOnlyValidCandidates: true,
                HistoryBankFullyInitialized: true)).Accepted, Is.True);
        Assert.That(manager.CompleteFilter(begin.Token,
            new SimpleDdgiNearFieldResidualGpuFilterCompletion(
                QueueOrderedCommandsRecorded: true,
                EdgeAwareValidityChecked: true,
                ExecutedIterationCount: 2u)).Accepted, Is.True);
        Assert.That(manager.CompleteComposite(begin.Token,
            new SimpleDdgiNearFieldResidualGpuCompositeCompletion(
                QueueOrderedCommandsRecorded: true,
                OnlyValidSignedResidualComposited: true,
                InvalidResidualPayloadWasZero: true)).Accepted, Is.True);

        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot published = manager.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(published.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.CompositeComplete));
            Assert.That(published.HistoryValid, Is.True);
            Assert.That(published.HistoryReadIndex, Is.EqualTo(1));
            Assert.That(published.HistoryWriteIndex, Is.EqualTo(0));
            Assert.That(published.IsContractReadyForRendererIntegration, Is.True);
        });
        Assert.Multiple(() =>
        {
            Assert.That(manager.ObserveFrameFenceCompletion(begin.Token), Is.True);
            Assert.That(manager.ObserveFrameFenceCompletion(begin.Token), Is.False);
            Assert.That(manager.Snapshot.LastFenceCompletedFrameEpoch,
                Is.EqualTo(begin.Token.FrameEpoch));
        });

        SimpleDdgiNearFieldResidualGpuBeginFrameResult reused = manager.BeginFrame(
            new SimpleDdgiNearFieldResidualGpuHistoryRevision(
                ViewportRevision: 4u,
                HiZRevision: 8u,
                TraceSourceAbiRevision: 1u,
                EffectiveModeRevision: 3u,
                ExposureDomainRevision: 9u,
                CameraCut: false,
                TraceSourceContentRevision: 1u,
                TraceSourceLayoutRevision: 1u));
        Assert.Multiple(() =>
        {
            Assert.That(reused.Started, Is.True);
            Assert.That(reused.HistoryInvalidated, Is.False);
            Assert.That(manager.InvalidateHistory(reused.Token), Is.True);
            Assert.That(manager.Snapshot.HistoryValid, Is.False);
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid));
        });
    }

    [Test]
    public void UnsafeTraceCompletionAbortsFrameAndInvalidatesHistory()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        var allocator = new FakeAllocator();
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();
        manager.Reconcile(new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
            true, layout, CreateReferenceConfiguration(layout),
            CompleteIntegration()), allocator);
        SimpleDdgiNearFieldResidualGpuBeginFrameResult begin = manager.BeginFrame(
            new SimpleDdgiNearFieldResidualGpuHistoryRevision(
                ViewportRevision: 1u,
                HiZRevision: 1u,
                TraceSourceAbiRevision: 1u,
                EffectiveModeRevision: 1u,
                ExposureDomainRevision: 1u,
                CameraCut: false,
                TraceSourceContentRevision: 1u,
                TraceSourceLayoutRevision: 1u));

        SimpleDdgiNearFieldResidualGpuStageResult result = manager.CompleteTrace(begin.Token,
            new SimpleDdgiNearFieldResidualGpuTraceCompletion(
                QueueOrderedCommandsRecorded: true,
                TraceSourceBindingVerified: true,
                StableSampleIdentityVerified: true,
                ReceiverBrdfAndPdfVerified: true,
                InvalidAndMissCandidatesZeroed: false,
                TileRecordsInitializedAndBounded: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason,
                Is.EqualTo("near-field-invalid-or-miss-residual-not-zeroed"));
            Assert.That(manager.Snapshot.HistoryValid, Is.False);
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid));
        });
    }

    [Test]
    public void FrameCannotStartWithARevisionThatDisagreesWithTheFrozenTraceSourceAbi()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        var allocator = new FakeAllocator();
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();
        manager.Reconcile(new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
            true, layout, CreateReferenceConfiguration(layout,
                traceSourceAbiRevision: 17u), CompleteIntegration()), allocator);

        SimpleDdgiNearFieldResidualGpuBeginFrameResult begin = manager.BeginFrame(
            new SimpleDdgiNearFieldResidualGpuHistoryRevision(
                ViewportRevision: 1u,
                HiZRevision: 1u,
                TraceSourceAbiRevision: 18u,
                EffectiveModeRevision: 1u,
                ExposureDomainRevision: 1u,
                CameraCut: false,
                TraceSourceContentRevision: 1u,
                TraceSourceLayoutRevision: 1u));

        Assert.Multiple(() =>
        {
            Assert.That(begin.Started, Is.False);
            Assert.That(begin.Reason,
                Is.EqualTo("near-field-frame-trace-source-abi-revision-mismatch"));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid));
        });
    }

    [Test]
    public void ReconcileFailsClosedForFrozenTraceSourceFormatScaleOrExtentMismatchWithoutAllocating()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        SimpleDdgiNearFieldResidualGpuConfiguration reference =
            CreateReferenceConfiguration(layout);
        SimpleDdgiNearFieldResidualGpuConfiguration formatMismatch = reference with
        {
            TraceSourceContract = reference.TraceSourceContract with
            {
                Format = SimpleDdgiNearFieldResidualFormat.B10G11R11UfloatPack32
            }
        };
        SimpleDdgiNearFieldResidualGpuConfiguration extentMismatch = reference with
        {
            TraceSourceContract = reference.TraceSourceContract with
            {
                Extent = new SimpleDdgiNearFieldTraceSourceScaledExtent(
                    FullWidth: 641,
                    FullHeight: 360,
                    ScaledWidth: 321,
                    ScaledHeight: 180,
                ResolutionScale: 0.5f)
            }
        };
        SimpleDdgiNearFieldResidualGpuConfiguration scaleMismatch = reference with
        {
            TraceSourceContract = reference.TraceSourceContract with
            {
                Extent = reference.TraceSourceContract.Extent with
                {
                    ResolutionScale = 0.499f
                }
            }
        };
        var formatAllocator = new FakeAllocator();
        var extentAllocator = new FakeAllocator();
        var scaleAllocator = new FakeAllocator();
        using var formatManager = new SimpleDdgiNearFieldResidualGpuManager();
        using var extentManager = new SimpleDdgiNearFieldResidualGpuManager();
        using var scaleManager = new SimpleDdgiNearFieldResidualGpuManager();

        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot formatSnapshot =
            formatManager.Reconcile(new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true, layout, formatMismatch, CompleteIntegration()), formatAllocator);
        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot extentSnapshot =
            extentManager.Reconcile(new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true, layout, extentMismatch, CompleteIntegration()), extentAllocator);
        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot scaleSnapshot =
            scaleManager.Reconcile(new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true, layout, scaleMismatch, CompleteIntegration()), scaleAllocator);

        Assert.Multiple(() =>
        {
            Assert.That(formatSnapshot.Reason,
                Is.EqualTo("near-field-trace-source-format-layout-mismatch"));
            Assert.That(extentSnapshot.Reason,
                Is.EqualTo("near-field-trace-source-scaled-extent-layout-mismatch"));
            Assert.That(scaleSnapshot.Reason,
                Is.EqualTo("near-field-trace-source-resolution-scale-layout-mismatch"));
            Assert.That(formatAllocator.AllocateCount, Is.Zero);
            Assert.That(extentAllocator.AllocateCount, Is.Zero);
            Assert.That(scaleAllocator.AllocateCount, Is.Zero);
        });
    }

    [Test]
    public void FrameCannotStartWithARevisionThatDisagreesWithFrozenTraceSourceLayoutOrContent()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        var allocator = new FakeAllocator();
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();
        manager.Reconcile(new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
            true,
            layout,
            CreateReferenceConfiguration(
                layout,
                traceSourceLayoutRevision: 7u,
                traceSourceRevision: 11u),
            CompleteIntegration()), allocator);

        SimpleDdgiNearFieldResidualGpuBeginFrameResult layoutMismatch = manager.BeginFrame(
            new SimpleDdgiNearFieldResidualGpuHistoryRevision(
                ViewportRevision: 1u,
                HiZRevision: 1u,
                TraceSourceAbiRevision: 1u,
                EffectiveModeRevision: 1u,
                ExposureDomainRevision: 1u,
                CameraCut: false,
                TraceSourceContentRevision: 11u,
                TraceSourceLayoutRevision: 8u));
        SimpleDdgiNearFieldResidualGpuBeginFrameResult contentMismatch = manager.BeginFrame(
            new SimpleDdgiNearFieldResidualGpuHistoryRevision(
                ViewportRevision: 1u,
                HiZRevision: 1u,
                TraceSourceAbiRevision: 1u,
                EffectiveModeRevision: 1u,
                ExposureDomainRevision: 1u,
                CameraCut: false,
                TraceSourceContentRevision: 12u,
                TraceSourceLayoutRevision: 7u));

        Assert.Multiple(() =>
        {
            Assert.That(layoutMismatch.Started, Is.False);
            Assert.That(layoutMismatch.Reason,
                Is.EqualTo("near-field-frame-trace-source-layout-revision-mismatch"));
            Assert.That(contentMismatch.Started, Is.False);
            Assert.That(contentMismatch.Reason,
                Is.EqualTo("near-field-frame-trace-source-content-revision-mismatch"));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualGpuResourceState.AllocatedHistoryInvalid));
        });
    }

    [Test]
    public void InvalidNativeAllocationIsRetiredAndNeverPublished()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        var allocator = new FakeAllocator { ReturnInvalidAllocation = true };
        using var manager = new SimpleDdgiNearFieldResidualGpuManager();

        SimpleDdgiNearFieldResidualGpuRuntimeSnapshot snapshot = manager.Reconcile(
            new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                true, layout, CreateReferenceConfiguration(layout),
                CompleteIntegration()), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsEffectivelyEnabled, Is.False);
            Assert.That(snapshot.Reason,
                Is.EqualTo("near-field-allocation-rejected:ArgumentException"));
            Assert.That(allocator.AllocateCount, Is.EqualTo(1));
            Assert.That(allocator.Retired, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ShaderSourcesKeepTraceSourceIsolatedAndCompositeValidityGuarded()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string trace = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_trace.comp"));
        string filter = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_filter.comp"));
        string composite = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_composite.comp"));
        string temporal = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_temporal.comp"));

        Assert.Multiple(() =>
        {
            Assert.That(trace, Does.Contain("directDiffuseEmissiveSource"));
            Assert.That(trace, Does.Contain("receiverPayload"));
            Assert.That(trace, Does.Contain("TraceFrameConstantsBuffer"));
            Assert.That(trace, Does.Contain("C5CreateStableCosineDirection"));
            Assert.That(trace, Does.Contain("SIMPLE_DDGI_NEAR_FIELD_MAX_TRACE_STEPS"));
            Assert.That(trace, Does.Contain("SIMPLE_DDGI_NEAR_FIELD_MAX_BINARY_REFINEMENTS"));
            Assert.That(trace, Does.Not.Contain("canonicalSceneColorInput"));
            Assert.That(trace, Does.Not.Contain("historyRadianceInput"));
            Assert.That(trace, Does.Not.Contain("filteredResidualInput"));
            Assert.That(temporal, Does.Not.Contain("canonicalSceneColorInput"));
            Assert.That(filter, Does.Not.Contain("canonicalSceneColorInput"));
            Assert.That(filter, Does.Contain("SIMPLE_DDGI_NEAR_FIELD_MAX_FILTER_RADIUS"));
            Assert.That(composite, Does.Contain("canonicalSceneColorInput"));
            Assert.That(composite, Does.Contain("SIMPLE_DDGI_NEAR_FIELD_FLAG_COMPOSITE_VALID_ONLY"));
            Assert.That(composite, Does.Contain("sole C5 stage allowed"));
            Assert.That(composite, Does.Not.Contain("residual.a * metadata.confidence"));
        });
    }

    [Test]
    public void ShaderAbiVersionAndTemporalHistoryBindingsMatchTheManagedV11Contract()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string shared = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual.glsl"));
        string temporal = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_temporal.comp"));
        string reset = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_reset.comp"));
        string trace = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_trace.comp"));
        string filter = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_filter.comp"));
        string composite = File.ReadAllText(Path.Combine(shaderDirectory,
            "ddgi_near_field_residual_composite.comp"));
        string passSource = File.ReadAllText(Path.Combine(
            FindRepoDirectory("Njulf.Rendering"),
            "Pipeline",
            "SimpleDdgiNearFieldResidualPasses.cs"));
        string runtimeSource = File.ReadAllText(Path.Combine(
            FindRepoDirectory("Njulf.Rendering"),
            "Resources",
            "SimpleDdgiNearFieldResidualVulkanRuntime.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain("0x4335000bu"));
            Assert.That(shared, Does.Contain("uvec2 receiverIdentity;"));
            Assert.That(shared, Does.Contain("uvec2 hitIdentity;"));
            Assert.That(shared, Does.Not.Contain("uvec4 identity;"));
            Assert.That(trace, Does.Contain("bool validMiss"));
            Assert.That(trace, Does.Contain(
                "persistent salt-and-pepper impulses"));
            Assert.That(trace, Does.Contain("c5TileHit"));
            Assert.That(trace, Does.Contain(
                "metadata.receiverIdentity = receiverIds"));
            Assert.That(trace, Does.Contain(
                "failureFlags = C5_TRACE_REASON_NORMAL | C5_TRACE_REASON_MISS"));
            Assert.That(trace, Does.Contain("pc.fullWeightTraceDistance"));
            Assert.That(trace, Does.Contain("1.0 - smoothstep("));
            Assert.That(temporal, Does.Contain("historyValidityInput"));
            Assert.That(temporal, Does.Contain("historyMetadata"));
            Assert.That(temporal, Does.Contain("temporalMetadataOutput"));
            Assert.That(temporal, Does.Contain("temporalHistoryNormalOutput"));
            Assert.That(temporal, Does.Contain("SimpleDdgiNearFieldUnpackHistoryValidity"));
            Assert.That(temporal, Does.Contain("TryGetCurrentNeighbourhoodBounds"));
            Assert.That(temporal, Does.Contain(
                "current.receiverIdentity"));
            Assert.That(temporal, Does.Not.Contain("previous.hitUv"));
            Assert.That(temporal, Does.Not.Contain(
                "current.hitIdentity"));
            Assert.That(temporal, Does.Contain(
                "SimpleDdgiNearFieldTemporalEvidenceConfidence"));
            Assert.That(filter, Does.Contain("effectiveSampleCount"));
            Assert.That(filter, Does.Contain("spatialEvidence"));
            Assert.That(composite, Does.Contain("correctionScale"));
            Assert.That(temporal, Does.Contain("outputHistoryLength"));
            Assert.That(temporal, Does.Contain("TemporalTileRecordsBuffer"));
            Assert.That(temporal, Does.Contain(
                "SIMPLE_DDGI_NEAR_FIELD_TELEMETRY_TEMPORAL_COMPLETE"));
            Assert.That(reset, Does.Contain("ResetHitMetadataBuffer"));
            Assert.That(reset, Does.Contain("ResetTileRecordsBuffer"));
            Assert.That(reset, Does.Contain("SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_ABI_VERSION"));
            Assert.That(passSource, Does.Contain(
                "images[3] = Sampled(validityRead, _bindlessHeap.HiZSampler)"));
            Assert.That(runtimeSource, Does.Contain(
                "return _frameAdmission && CanExecuteNoLock(sceneData);"));
        });
    }

    [Test]
    public void ReferenceConfigurationAccumulatesAStableSixtyFourSampleEstimate()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        SimpleDdgiNearFieldResidualGpuConfiguration configuration =
            CreateReferenceConfiguration(layout);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.TemporalBlend,
                Is.EqualTo(63.0f / 64.0f));
            Assert.That(configuration.MaximumHistoryLength, Is.EqualTo(64));
            Assert.That(configuration.FilterRadius, Is.EqualTo(3));
            Assert.That(configuration.MaximumTraceSteps, Is.EqualTo(64));
            Assert.That(configuration.FullWeightTraceDistance, Is.EqualTo(4.0f));
            Assert.That(configuration.MaximumTraceDistance, Is.EqualTo(8.0f));
        });
    }

    [Test]
    public void TraceDistanceFeatherMustHaveARealPositiveGuardBand()
    {
        SimpleDdgiNearFieldResidualLayout layout = CreateLayout();
        SimpleDdgiNearFieldResidualGpuConfiguration reference =
            CreateReferenceConfiguration(layout);

        Assert.Multiple(() =>
        {
            Assert.That(reference.Validate(layout).IsValid, Is.True);
            Assert.That((reference with
                {
                    FullWeightTraceDistance = reference.MaximumTraceDistance
                }).Validate(layout).Reason,
                Is.EqualTo("near-field-gpu-numeric-configuration-invalid"));
            Assert.That((reference with
                {
                    FullWeightTraceDistance = 0.0f
                }).Validate(layout).Reason,
                Is.EqualTo("near-field-gpu-numeric-configuration-invalid"));
        });
    }

    [Test]
    public void CpuAbiAndEveryC5PassDescriptorBindingMatchTheGlslContract()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string sharedPath = Path.Combine(shaderDirectory, "ddgi_near_field_residual.glsl");
        Assert.That(File.Exists(sharedPath), Is.True,
            "The C5 common GLSL contract must exist before a pass can be admitted.");
        string shared = File.ReadAllText(sharedPath);
        string abiLiteral = $"0x{SimpleDdgiNearFieldResidualGpuAbi.Version:x8}u";

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiNearFieldResidualGpuAbi.Version,
                Is.EqualTo(0x4335_000Bu));
            Assert.That(shared, Does.Match(
                $@"const\s+uint\s+SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_ABI_VERSION\s*=\s*{Regex.Escape(abiLiteral)}\s*;"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualHitMetadata"));
            Assert.That(shared, Does.Contain(
                "struct SimpleDdgiNearFieldResidualTraceFrameConstants"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualTileRecord"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualResetPushConstants"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualTracePushConstants"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualTemporalPushConstants"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualFilterPushConstants"));
            Assert.That(shared, Does.Contain("struct SimpleDdgiNearFieldResidualCompositePushConstants"));
            Assert.That(typeof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities)
                .GetProperty(nameof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities.ResetPassRegistered)),
                Is.Not.Null);
            Assert.That(typeof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities)
                .GetProperty(nameof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities.TracePassRegistered)),
                Is.Not.Null);
            Assert.That(typeof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities)
                .GetProperty(nameof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities.TemporalPassRegistered)),
                Is.Not.Null);
            Assert.That(typeof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities)
                .GetProperty(nameof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities.FilterPassRegistered)),
                Is.Not.Null);
            Assert.That(typeof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities)
                .GetProperty(nameof(SimpleDdgiNearFieldResidualGpuIntegrationCapabilities.CompositePassRegistered)),
                Is.Not.Null);
        });

        AssertShaderStageContract(
            shaderDirectory,
            "ddgi_near_field_residual_reset.comp",
            "NearFieldResetPushBlock",
            nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants),
            "SimpleDdgiNearFieldResidualResetPushConstants",
            (SimpleDdgiNearFieldResidualGpuBindings.ResetHitMetadata,
                "ResetHitMetadataBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.ResetTileRecords,
                "ResetTileRecordsBuffer"));
        AssertShaderStageContract(
            shaderDirectory,
            "ddgi_near_field_residual_trace.comp",
            "NearFieldTracePushBlock",
            nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants),
            "SimpleDdgiNearFieldResidualTracePushConstants",
            (SimpleDdgiNearFieldResidualGpuBindings.TraceDirectDiffuseEmissiveSource,
                "directDiffuseEmissiveSource"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceHiZ, "depthHierarchy"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceReceiverDepth, "receiverDepth"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceReceiverPayload,
                "receiverPayload"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceRawResidualOutput, "rawResidualOutput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceHitMetadataOutput,
                "TraceHitMetadataBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceTileRecords,
                "TraceTileRecordsBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.TraceFrameConstants,
                "TraceFrameConstantsBuffer"));
        AssertShaderStageContract(
            shaderDirectory,
            "ddgi_near_field_residual_temporal.comp",
            "NearFieldTemporalPushBlock",
            nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants),
            "SimpleDdgiNearFieldResidualTemporalPushConstants",
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalRawResidual, "rawCandidateInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalCurrentMetadata,
                "CurrentMetadataBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalHistoryRadiance,
                "historyRadianceInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalHistoryMoments,
                "historyMomentsInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalHistoryValidity,
                "historyValidityInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalHistoryMetadata,
                "HistoryMetadataBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalRadianceOutput,
                "temporalRadianceOutput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalMomentsOutput,
                "temporalMomentsOutput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalValidityOutput,
                "temporalValidityOutput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalMetadataOutput,
                "TemporalMetadataOutputBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalMotionVectors, "motionVectors"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalCurrentReceiverPayload,
                "currentReceiverPayload"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalHistoryReceiverNormal,
                "historyReceiverNormals"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalHistoryNormalOutput,
                "temporalHistoryNormalOutput"),
            (SimpleDdgiNearFieldResidualGpuBindings.TemporalTileRecords,
                "TemporalTileRecordsBuffer"));
        AssertShaderStageContract(
            shaderDirectory,
            "ddgi_near_field_residual_filter.comp",
            "NearFieldFilterPushBlock",
            nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants),
            "SimpleDdgiNearFieldResidualFilterPushConstants",
            (SimpleDdgiNearFieldResidualGpuBindings.FilterInput, "temporalResidualInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.FilterMetadata, "FilterMetadataBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.FilterOutput, "filteredResidualOutput"),
            (SimpleDdgiNearFieldResidualGpuBindings.FilterReceiverPayload,
                "receiverPayload"));
        AssertShaderStageContract(
            shaderDirectory,
            "ddgi_near_field_residual_composite.comp",
            "NearFieldCompositePushBlock",
            nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants),
            "SimpleDdgiNearFieldResidualCompositePushConstants",
            (SimpleDdgiNearFieldResidualGpuBindings.CompositeCanonicalSceneColor,
                "canonicalSceneColorInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.CompositeFilteredResidual,
                "filteredResidualInput"),
            (SimpleDdgiNearFieldResidualGpuBindings.CompositeMetadata,
                "CompositeMetadataBuffer"),
            (SimpleDdgiNearFieldResidualGpuBindings.CompositeSceneColorOutput,
                "canonicalSceneColorOutput"));
    }

    private static void AssertShaderStageContract(
        string shaderDirectory,
        string fileName,
        string pushBlockName,
        string managedPushConstantsType,
        string glslPushConstantsType,
        params (uint Binding, string Identifier)[] expectedBindings)
    {
        string path = Path.Combine(shaderDirectory, fileName);
        Assert.That(File.Exists(path), Is.True,
            $"The required C5 stage shader '{fileName}' is missing.");
        string source = File.ReadAllText(path);
        uint[] actualBindings = Regex.Matches(
                source,
                @"\bbinding\s*=\s*(?<binding>\d+)\b",
                RegexOptions.CultureInvariant)
            .Select(static match => uint.Parse(
                match.Groups["binding"].Value,
                CultureInfo.InvariantCulture))
            .ToArray();
        uint[] expectedBindingValues = expectedBindings
            .Select(static expected => expected.Binding)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("#include \"ddgi_near_field_residual.glsl\""));
            Assert.That(source, Does.Contain("layout(local_size_"));
            Assert.That(source, Does.Contain(
                $"layout(push_constant) uniform {pushBlockName}"));
            Assert.That(managedPushConstantsType,
                Is.EqualTo("GPU" + glslPushConstantsType),
                $"{fileName} must name the C# GPU push structure explicitly.");
            Assert.That(source, Does.Contain(glslPushConstantsType));
            Assert.That(source, Does.Contain(
                "SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_ABI_VERSION"));
            Assert.That(actualBindings, Is.EqualTo(expectedBindingValues),
                $"{fileName} has a descriptor binding set that differs from the C# contract.");
        });

        foreach ((uint binding, string identifier) in expectedBindings)
        {
            string declaration =
                $@"layout\s*\((?=[^)]*\bset\s*=\s*0\b)(?=[^)]*\bbinding\s*=\s*{binding}\b)[^)]*\)[^{{;\r\n]*\b{Regex.Escape(identifier)}\b";
            Assert.That(source, Does.Match(declaration),
                $"{fileName} binding {binding} must declare '{identifier}'.");
        }
    }

    private static SimpleDdgiNearFieldResidualLayout CreateLayout()
    {
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                sourceWidth: 640,
                sourceHeight: 360,
                SimpleDdgiNearFieldResidualProfile.HalfResolutionReference,
                budgetBytes: 256UL * 1024UL * 1024UL);
        Assert.That(layout.IsValid, Is.True, layout.FailureReason);
        return layout;
    }

    private static SimpleDdgiNearFieldResidualGpuConfiguration CreateReferenceConfiguration(
        in SimpleDdgiNearFieldResidualLayout layout,
        uint traceSourceAbiRevision = 1u,
        uint traceSourceLayoutRevision = 1u,
        uint traceSourceRevision = 1u) =>
        SimpleDdgiNearFieldResidualGpuConfiguration.CreateReference(
            layout,
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference,
            traceSourceAbiRevision,
            traceSourceLayoutRevision,
            traceSourceRevision);

    private static SimpleDdgiNearFieldResidualGpuIntegrationCapabilities CompleteIntegration() =>
        new(
            TracePassRegistered: true,
            TemporalPassRegistered: true,
            FilterPassRegistered: true,
            CompositePassRegistered: true,
            DirectDiffuseEmissiveAttachmentAvailable: true,
            HiZAvailable: true,
            ReceiverMetadataAvailable: true,
            StableSampleRayInputAvailable: true,
            ReceiverBrdfPdfInputAvailable: true,
            MotionVectorsAvailable: true,
            DoubleBufferedHistoryIdentityAvailable: true,
            HistoryIdentityMemoryBudgeted: true,
            TileRecordLayoutValidated: true,
            RequiredImageFormatsValidated: true,
            DescriptorAndBarrierContractValidated: true,
            ShaderArtifactsValidated: true,
            ResetPassRegistered: true,
            PingPongBankBindingAndSynchronizationValidated: true,
            DirectSourceVariantProvenanceValidated: true,
            GeometricAndShadingNormalHistoryAvailable: true,
            HitUvAndSourceRevisionValidationAvailable: true,
            TemporalVarianceClippingAndBoundedHistoryAvailable: true,
            B3FootprintFrequencySeparationValidated: true,
            MeasuredQualificationEvidenceVerified: true,
            DeviceLimitsAndActualAllocationRequirementsValidated: true);

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new AssertionException($"Could not find repository directory '{name}'.");
    }

    private sealed class FakeAllocator : ISimpleDdgiNearFieldResidualGpuResourceAllocator
    {
        private ulong _nextAllocationId = 1UL;

        public int AllocateCount { get; private set; }
        public bool ReturnInvalidAllocation { get; init; }
        public List<SimpleDdgiNearFieldResidualGpuAllocation> Retired { get; } = new();

        public SimpleDdgiNearFieldResidualGpuAllocation Allocate(
            in SimpleDdgiNearFieldResidualLayout layout,
            in SimpleDdgiNearFieldResidualGpuConfiguration configuration)
        {
            AllocateCount++;
            ulong allocationId = _nextAllocationId++;
            ulong handle = allocationId * 100UL;
            SimpleDdgiNearFieldResidualGpuResource Resource(
                ulong bytes,
                SimpleDdgiNearFieldResidualGpuResourceKind kind) =>
                bytes == 0UL
                    ? new SimpleDdgiNearFieldResidualGpuResource(0UL, 0UL, kind)
                    : new SimpleDdgiNearFieldResidualGpuResource(++handle, bytes, kind);

            var allocation = new SimpleDdgiNearFieldResidualGpuAllocation(
                allocationId,
                Resource(layout.TraceSourceBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.DirectDiffuseEmissiveSource),
                Resource(layout.ReceiverPayloadBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.ReceiverPayload),
                Resource(layout.TraceFrameConstantsBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TraceFrameConstants0),
                Resource(layout.TraceFrameConstantsBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TraceFrameConstants1),
                Resource(layout.RawCandidateBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.RawCandidate),
                Resource(layout.HitMetadataBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HitMetadata),
                Resource(layout.HistoryRadianceBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryRadiance0),
                Resource(layout.HistoryRadianceBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryRadiance1),
                Resource(layout.MomentBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMoments0),
                Resource(layout.MomentBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMoments1),
                Resource(layout.HistoryValidityBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryValidity0),
                Resource(layout.HistoryValidityBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryValidity1),
                Resource(layout.HistoryMetadataBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMetadata0),
                Resource(layout.HistoryMetadataBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMetadata1),
                Resource(layout.HistoryNormalBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryNormal0),
                Resource(layout.HistoryNormalBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryNormal1),
                Resource(layout.FilterScratchBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.FilterScratch0),
                Resource(layout.FilterScratchBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.FilterScratch1),
                Resource(layout.TileBuffersBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TileBuffers),
                Resource(layout.TelemetryReadbackBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TelemetryReadback0),
                Resource(layout.TelemetryReadbackBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TelemetryReadback1),
                SimpleDdgiNearFieldResidualGpuAllocation.ExpectedDescriptorCount(layout));
            return ReturnInvalidAllocation
                ? allocation with { DescriptorCount = 0u }
                : allocation;
        }

        public void Retire(SimpleDdgiNearFieldResidualGpuAllocation allocation) =>
            Retired.Add(allocation);
    }
}
