using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticDiagnosticsTests
{
    [Test]
    public void ValidatedHeaderTelemetry_ExposesExactPublicationCounts()
    {
        GPUCausticCacheHeaderV1 header = CreateHeader();

        GiCausticPublicationTelemetry telemetry =
            GiCausticPublicationTelemetry.FromValidatedHeader(header);

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.IsValid, Is.True);
            Assert.That(telemetry.CacheGeneration, Is.EqualTo(11u));
            Assert.That(telemetry.CandidateInputCount, Is.EqualTo(48u));
            Assert.That(telemetry.CandidateCount, Is.EqualTo(40u));
            Assert.That(telemetry.RetainedPhotonCount, Is.EqualTo(20u));
            Assert.That(telemetry.RetentionRatio, Is.EqualTo(0.5d));
            Assert.That(telemetry.PhotonBankIndex, Is.EqualTo(1u));
            Assert.That(telemetry.CacheBankIndex, Is.EqualTo(1u));
        });
    }

    [Test]
    public void PublicationTelemetry_RejectsOverflowStaleAndImpossibleCounts()
    {
        GiCausticPublicationTelemetry valid =
            GiCausticPublicationTelemetry.FromValidatedHeader(CreateHeader());

        Assert.Multiple(() =>
        {
            Assert.That((valid with { OverflowCount = 1u }).IsValid, Is.False);
            Assert.That((valid with
            {
                RetainedPhotonCount = valid.CandidateCount + 1u
            }).IsValid, Is.False);
            Assert.That((valid with
            {
                PublicationFlags = valid.PublicationFlags |
                    GiCausticGpuCachePublicationFlags.Invalidated
            }).IsValid, Is.False);
            Assert.That((valid with { CacheGeneration = 0u }).IsValid, Is.False);
        });
    }

    [Test]
    public void PersistedDiagnostics_RequireMatchingReadableGeneration()
    {
        GiCausticDiagnostics valid = CreateDiagnostics();
        GiCausticDiagnostics normalized = valid.NormalizeForPersistence();
        GiCausticDiagnostics mismatched = (valid with
        {
            Publication = valid.Publication with { CacheGeneration = 12u }
        }).NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.State,
                Is.EqualTo(GiCausticTelemetryState.Readable));
            Assert.That(normalized.HasAuthoritativePublication, Is.True);
            Assert.That(mismatched.State,
                Is.EqualTo(GiCausticTelemetryState.Faulted));
            Assert.That(mismatched.HasAuthoritativePublication, Is.False);
        });
    }

    [Test]
    public void StageTimingNormalization_ZeroesUnavailableAndNegativeValues()
    {
        GiCausticStageTimings normalized = new GiCausticStageTimings(
            TaskMicroseconds: -4L,
            TraceMicroseconds: 13L,
            CacheBuildMicroseconds: 17L,
            ResolveMicroseconds: 19L,
            CompositeMicroseconds: 23L,
            AvailableStages: GiCausticTimedStage.Task |
                GiCausticTimedStage.CacheBuild |
                GiCausticTimedStage.Composite)
            .NormalizeForPersistence();

        Assert.That(normalized, Is.EqualTo(new GiCausticStageTimings(
            TaskMicroseconds: 0L,
            TraceMicroseconds: 0L,
            CacheBuildMicroseconds: 17L,
            ResolveMicroseconds: 0L,
            CompositeMicroseconds: 23L,
            AvailableStages: GiCausticTimedStage.Task |
                GiCausticTimedStage.CacheBuild |
                GiCausticTimedStage.Composite)));
    }

    internal static GiCausticDiagnostics CreateDiagnostics()
    {
        GiCausticGpuMemoryRequirements memory = CreateMemory();
        GiCausticPublicationTelemetry publication =
            GiCausticPublicationTelemetry.FromValidatedHeader(CreateHeader());
        var runtime = new GiCausticVulkanRuntimeDiagnostics(
            GiCausticVulkanRuntimeCapabilityReason.None,
            TaggedTransportProducerAvailable: true,
            DeterministicCacheBuildQualified: true,
            DescriptorContextRegistered: true,
            HeaderReadbackPending: false,
            new GiCausticGpuRuntimeSnapshot(
                GiCausticGpuResourceState.Readable,
                IsEffectivelyEnabled: true,
                AllocationEpoch: 7UL,
                AllocatedBytes: memory.AllocatedBytes,
                DescriptorCount: 4u,
                PhotonReadBankIndex: 1,
                PhotonWriteBankIndex: 0,
                CacheReadBankIndex: 1,
                CacheWriteBankIndex: 0,
                ReadableGeneration: 11u,
                PendingGeneration: 0u,
                PublicationFailureCount: 0UL,
                InvalidationCount: 2UL,
                AllocationFailureCount: 0UL,
                MemoryRequirements: memory,
                Reason: "caustic-readable"),
            Detail: "caustic-readable")
        {
            Publication = publication
        };

        return new GiCausticDiagnostics
        {
            State = GiCausticTelemetryState.Readable,
            Runtime = runtime,
            Publication = publication,
            Timings = new GiCausticStageTimings(
                7L, 23L, 31L, 11L, 5L, GiCausticTimedStage.All),
            Memory = memory,
            Reason = "caustic-fence-validated-cache-publication-available"
        };
    }

    private static GiCausticGpuMemoryRequirements CreateMemory() => new(
        SimpleDdgiAdvancedMemoryUsage.Admitted(
            SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords,
            requiredBytes: 4_096UL,
            allocatedBytes: 4_096UL,
            peakLiveBytes: 4_096UL),
        SimpleDdgiAdvancedMemoryUsage.Admitted(
            SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
            requiredBytes: 2_048UL,
            allocatedBytes: 2_048UL,
            peakLiveBytes: 2_048UL),
        SimpleDdgiAdvancedMemoryUsage.Admitted(
            SimpleDdgiAdvancedMemoryCategory.CausticHistory,
            requiredBytes: 8_192UL,
            allocatedBytes: 8_192UL,
            peakLiveBytes: 8_192UL),
        TaskQueueBytes: 1_024UL,
        CandidateStagingBytes: 1_024UL,
        PublishedPhotonBytes: 2_048UL,
        PublicationHeaderBytes: 256UL);

    private static GPUCausticCacheHeaderV1 CreateHeader() => new()
    {
        AbiVersion = GiCausticGpuAbi.Version,
        CacheGeneration = 11u,
        RevisionFingerprintLow = 0x5566_7788u,
        RevisionFingerprintHigh = 0x1122_3344u,
        TaskCapacity = 64u,
        PhotonCapacity = 128u,
        PhotonRecordStrideBytes = GiCausticGpuAbi.PhotonRecordBytes,
        CellTableCapacity = 256u,
        MaximumPhotonsPerCell = 8u,
        CandidateCount = 40u,
        RetainedPhotonCount = 20u,
        OccupiedCellCount = 7u,
        OverflowCount = 0u,
        PublicationFlags = GiCausticGpuCachePublicationFlags.Initialized |
            GiCausticGpuCachePublicationFlags.BuildComplete,
        BuildSerial = 19u,
        CacheBankIndex = 1u,
        PhotonBankIndex = 1u,
        CandidateInputCount = 48u,
        TransportAbiVersion = GiCausticGpuAbi.Version
    };
}
