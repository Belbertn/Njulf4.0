using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceSnapshotWriterTests
{
    [Test]
    public void SnapshotWriter_RemainsAvailableForSimpleDdgiCaptures()
    {
        Assert.That(typeof(PerformanceSnapshotWriter).Assembly.GetName().Name, Is.EqualTo("Njulf.Rendering"));
    }

    [Test]
    public void GlobalIlluminationSnapshot_PreservesCompactReceiverPublicationEvidence()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiReceiverProbeBytes = 16UL * 123UL,
            SimpleDdgiReceiverProbeCapacity = 123,
            SimpleDdgiReceiverInvalidationBytes = 48UL,
            SimpleDdgiReceiverInvalidationRangeCount = 2,
            SimpleDdgiReceiverFullClear = 1,
            SimpleDdgiReceiverResourceGeneration = 17u,
            SimpleDdgiReceiverRecordsPublished = 29
        };

        PerformanceGlobalIlluminationSnapshot snapshot =
            PerformanceSnapshotWriter.CreateGlobalIlluminationSnapshot(diagnostics);

        Assert.That(snapshot.SimpleDdgiReceiverProbeBytes, Is.EqualTo(16UL * 123UL));
        Assert.That(snapshot.SimpleDdgiReceiverProbeCapacity, Is.EqualTo(123));
        Assert.That(snapshot.SimpleDdgiReceiverInvalidationBytes, Is.EqualTo(48UL));
        Assert.That(snapshot.SimpleDdgiReceiverInvalidationRangeCount, Is.EqualTo(2));
        Assert.That(snapshot.SimpleDdgiReceiverFullClear, Is.True);
        Assert.That(snapshot.SimpleDdgiReceiverResourceGeneration, Is.EqualTo(17u));
        Assert.That(snapshot.SimpleDdgiReceiverRecordsPublished, Is.EqualTo(29));
    }

    [Test]
    public void SnapshotAndMemoryAudit_PreserveAuthoritativePackedStorageEvidence()
    {
        SimpleDdgiStorageDiagnostics storage =
            SimpleDdgiStorageDiagnostics.Unavailable with
            {
                IsAvailable = true,
                PackingMode = SimpleDdgiStoragePackingMode.Packed,
                AbiVersion = SimpleDdgiStorageAbiVersion.Packed,
                DirectionCodebookVersion = SimpleDdgiDirectionCodebook.Version,
                CanonicalIrradianceFormat = "RGBA16F",
                CanonicalVisibilityFormat = "RG16F",
                CanonicalIrradianceBytes = 100UL,
                CanonicalVisibilityBytes = 200UL,
                SourceCacheBytes = 400UL,
                SourceCacheCompact28Bytes = 280UL,
                SourceCacheCompact24Bytes = 120UL,
                RayScratchStrideBytes = 20UL,
                RayScratchBytes = 600UL,
                MirrorCoverageMode =
                    SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                MirrorAdmittedProbeCount = 12,
                MirrorProvisionedProbeCount = 256,
                MirrorTotalBytes = 500UL,
                MirrorAllocatedBytes = 550UL,
                StorageLayoutFingerprint = 17UL,
                MirrorLayoutFingerprint = 19UL,
                MirrorAllocationGeneration = 23UL,
                ValidationCounters =
                    SimpleDdgiStorageValidationCounters.Empty with
                    {
                        ReadbackValid = 1,
                        MirrorImageHitCount = 31u
                    }
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiStorage = storage,
            SimpleDdgiAtlasBytes = 99_999UL,
            SimpleDdgiSampledAtlasImageBytes = 88_888UL,
            SimpleDdgiTransportIrradianceAtlasBytes = 300UL,
            SimpleDdgiTransportSourceCacheBytes = 77_777UL
        };

        PerformanceGlobalIlluminationSnapshot snapshot =
            PerformanceSnapshotWriter.CreateGlobalIlluminationSnapshot(diagnostics);
        PerformanceMemoryOwnershipAudit audit =
            PerformanceSnapshotWriter.CreateMemoryOwnershipAudit(
                diagnostics,
                MemoryBudgetSnapshot.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SimpleDdgiStorage, Is.EqualTo(storage));
            Assert.That(snapshot.SimpleDdgiStorage.ValidationCounters.ReadbackValid,
                Is.EqualTo(1));
            Assert.That(snapshot.SimpleDdgiStorage.ValidationCounters.MirrorImageHitCount,
                Is.EqualTo(31u));
            Assert.That(audit.CanonicalDdgiAtlasBytes, Is.EqualTo(300UL));
            Assert.That(audit.SampledAtlasMirrorBytes, Is.EqualTo(550UL));
            Assert.That(audit.TransportBytes, Is.EqualTo(700UL));
        });
    }
}
