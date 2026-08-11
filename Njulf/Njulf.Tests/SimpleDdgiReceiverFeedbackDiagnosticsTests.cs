using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverFeedbackDiagnosticsTests
{
    [Test]
    public void Normalize_PreservesFenceValidatedPublicationAndExactMemory()
    {
        SimpleDdgiReceiverFeedbackDiagnostics diagnostics =
            CreateReadableDiagnostics();

        SimpleDdgiReceiverFeedbackDiagnostics normalized =
            diagnostics.NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.State,
                Is.EqualTo(SimpleDdgiReceiverFeedbackTelemetryState.Readable));
            Assert.That(normalized.HasAuthoritativePublication, Is.True);
            Assert.That(normalized.Publication.AppendCount, Is.EqualTo(768u));
            Assert.That(normalized.Publication.SummaryCount, Is.EqualTo(48u));
            Assert.That(normalized.Publication.AppendUtilization,
                Is.EqualTo(0.75d).Within(1e-12));
            Assert.That(normalized.Memory.AllocatedBytes, Is.EqualTo(7_168UL));
            Assert.That(normalized.Timings.TotalMicroseconds, Is.EqualTo(150L));
        });
    }

    [Test]
    public void Normalize_RejectsPublicationThatDoesNotMatchRuntimeGeneration()
    {
        SimpleDdgiReceiverFeedbackDiagnostics diagnostics =
            CreateReadableDiagnostics() with
            {
                Runtime = CreateReadableDiagnostics().Runtime with
                {
                    Resource = CreateReadableDiagnostics().Runtime.Resource with
                    {
                        PublishedGeneration = 8u
                    }
                }
            };

        SimpleDdgiReceiverFeedbackDiagnostics normalized =
            diagnostics.NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.State,
                Is.EqualTo(SimpleDdgiReceiverFeedbackTelemetryState.Faulted));
            Assert.That(normalized.Publication,
                Is.EqualTo(SimpleDdgiReceiverFeedbackPublicationTelemetry.Empty));
            Assert.That(normalized.Runtime.Publication,
                Is.EqualTo(SimpleDdgiReceiverFeedbackPublicationTelemetry.Empty));
            Assert.That(normalized.HasAuthoritativePublication, Is.False);
        });
    }

    [Test]
    public void Normalize_FailsClosedForOverflowedGpuHeader()
    {
        SimpleDdgiReceiverFeedbackDiagnostics diagnostics =
            CreateReadableDiagnostics() with
            {
                Publication = CreateReadableDiagnostics().Publication with
                {
                    DroppedCount = 1u,
                    Flags = SimpleDdgiReceiverFeedbackGpuBankFlags.Validated |
                        SimpleDdgiReceiverFeedbackGpuBankFlags.AppendOverflow
                }
            };

        SimpleDdgiReceiverFeedbackDiagnostics normalized =
            diagnostics.NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.State,
                Is.EqualTo(SimpleDdgiReceiverFeedbackTelemetryState.Disabled));
            Assert.That(normalized.HasAuthoritativePublication, Is.False);
            Assert.That(normalized.Reason,
                Is.EqualTo("receiver-feedback-telemetry-invalid"));
        });
    }

    [Test]
    public void Timings_NormalizeUnavailableAndNegativeValuesToZero()
    {
        var timings = new SimpleDdgiReceiverFeedbackStageTimings(
            ResetMicroseconds: -1L,
            CaptureMicroseconds: 20L,
            RawRadixMicroseconds: 30L,
            PartialBuildAndRadixMicroseconds: 40L,
            ReduceAndFinalizeMicroseconds: 50L,
            AvailableStages:
                SimpleDdgiReceiverFeedbackTimedStage.Reset |
                SimpleDdgiReceiverFeedbackTimedStage.RawRadix |
                SimpleDdgiReceiverFeedbackTimedStage.ReduceAndFinalize);

        SimpleDdgiReceiverFeedbackStageTimings normalized =
            timings.NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.ResetMicroseconds, Is.Zero);
            Assert.That(normalized.CaptureMicroseconds, Is.Zero);
            Assert.That(normalized.RawRadixMicroseconds, Is.EqualTo(30L));
            Assert.That(normalized.PartialBuildAndRadixMicroseconds, Is.Zero);
            Assert.That(normalized.ReduceAndFinalizeMicroseconds, Is.EqualTo(50L));
            Assert.That(normalized.TotalMicroseconds, Is.EqualTo(80L));
        });
    }

    [Test]
    public void RoadmapAllocatedBytes_IncludesB1PhysicalOwnership()
    {
        GiRoadmapExperimentDiagnostics roadmap =
            GiRoadmapExperimentDiagnostics.Disabled with
            {
                ReceiverFeedbackRuntime = CreateReadableDiagnostics()
            };

        Assert.That(roadmap.AllocatedBytes, Is.EqualTo(7_168UL));
    }

    internal static SimpleDdgiReceiverFeedbackDiagnostics
        CreateReadableDiagnostics()
    {
        const ulong RecordBytes = 2_048UL;
        const ulong ScratchBytes = 4_096UL;
        const ulong SummaryBytes = 1_024UL;
        const ulong TotalBytes = RecordBytes + ScratchBytes + SummaryBytes;
        var memory = new SimpleDdgiReceiverFeedbackMemoryTelemetry(
            SimpleDdgiAdvancedMemoryUsage.Admitted(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks,
                RecordBytes,
                RecordBytes,
                RecordBytes),
            SimpleDdgiAdvancedMemoryUsage.Admitted(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                ScratchBytes,
                ScratchBytes,
                ScratchBytes),
            SimpleDdgiAdvancedMemoryUsage.Admitted(
                SimpleDdgiAdvancedMemoryCategory
                    .ReceiverFeedbackProbeSummaries,
                SummaryBytes,
                SummaryBytes,
                SummaryBytes));
        var publication = new SimpleDdgiReceiverFeedbackPublicationTelemetry(
            Available: true,
            LayoutRevision: SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
            FeedbackGeneration: 7u,
            ViewportGeneration: 3u,
            FrameSerial: 123UL,
            AppendCount: 768u,
            DroppedCount: 0u,
            ProducerOverflowMask: 0u,
            RecordCapacity: 1_024u,
            ProbePartialCount: 64u,
            FallbackPartialCount: 16u,
            SummaryCount: 48u,
            FallbackSummaryCount: 8u,
            InvalidRecordCount: 0u,
            Flags: SimpleDdgiReceiverFeedbackGpuBankFlags.Validated);
        var resource = new SimpleDdgiReceiverFeedbackGpuResourceSnapshot(
            SimpleDdgiReceiverFeedbackGpuResourceState.Published,
            IsEffectivelyEnabled: true,
            AllocationEpoch: 11UL,
            AllocatedBytes: TotalBytes,
            DescriptorCount: 4u,
            PublishedBankIndex: 0,
            PublishedGeneration: publication.FeedbackGeneration,
            Reason: "published-for-next-frame-scheduling");
        var runtime = new SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics(
            SimpleDdgiReceiverFeedbackGpuCapabilityReason.None,
            ExactCaptureProducerAvailable: true,
            DescriptorContextRegistered: true,
            HeaderReadbackPending: false,
            resource,
            Detail: "receiver-feedback-previous-bank-published")
        {
            Publication = publication
        };
        var timings = new SimpleDdgiReceiverFeedbackStageTimings(
            10L,
            20L,
            30L,
            40L,
            50L,
            SimpleDdgiReceiverFeedbackTimedStage.All);

        return new SimpleDdgiReceiverFeedbackDiagnostics
        {
            State = SimpleDdgiReceiverFeedbackTelemetryState.Readable,
            Runtime = runtime,
            Publication = publication,
            Timings = timings,
            Memory = memory,
            Reason =
                "receiver-feedback-fence-validated-publication-available"
        };
    }
}
