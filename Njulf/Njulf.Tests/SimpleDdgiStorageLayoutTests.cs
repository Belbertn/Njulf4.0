using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using System.Runtime.InteropServices;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiStorageLayoutTests
{
    [Test]
    public void MixedRegions_AreAlignedAndChargeTheirExactWordStrides()
    {
        SimpleDdgiTransportCacheRegionRequest[] requests =
        [
            Request(0, "near", 0, 3, 7, 4, 0.125f, 0.10f),
            Request(1, "long-range", 3, 2, 7, 4_096, 32.0f, 0.10f)
        ];

        SimpleDdgiStorageLayout layout = SimpleDdgiStorageLayoutCompiler.Compile(requests);

        Assert.Multiple(() =>
        {
            Assert.That((uint)SimpleDdgiStorageAbiVersion.Packed, Is.EqualTo(7u));
            Assert.That(layout.AbiVersion, Is.EqualTo(SimpleDdgiStorageAbiVersion.Packed));
            Assert.That(layout.Regions[0].Format, Is.EqualTo(SimpleDdgiTransportCacheFormat.Compact24));
            Assert.That(layout.Regions[0].BaseWord, Is.Zero);
            Assert.That(layout.Regions[0].ByteCount, Is.EqualTo(3UL * 7UL * 24UL));
            Assert.That(layout.Regions[1].Format, Is.EqualTo(SimpleDdgiTransportCacheFormat.Compact28));
            Assert.That(layout.Regions[1].BaseWord, Is.EqualTo(128UL));
            Assert.That(layout.Regions[1].AlignmentPaddingBytes, Is.EqualTo(8UL));
            Assert.That(layout.Regions[1].ByteCount, Is.EqualTo(2UL * 7UL * 28UL));
            Assert.That(layout.SourceCacheBytes, Is.EqualTo(904UL));
            Assert.That(layout.Compact24Bytes + layout.Compact28Bytes + layout.AlignmentPaddingBytes,
                Is.EqualTo(layout.SourceCacheBytes));
        });
    }

    [Test]
    public void LayoutFingerprint_ChangesWithRepresentationOrPhysicalIdentity()
    {
        SimpleDdgiTransportCacheRegionRequest packed =
            Request(0, "hero", 0, 8, 32, 8, 0.125f, 0.10f);
        SimpleDdgiStorageLayout first = SimpleDdgiStorageLayoutCompiler.Compile([packed]);
        SimpleDdgiStorageLayout moved = SimpleDdgiStorageLayoutCompiler.Compile(
            [packed with { PhysicalFirstProbe = 16 }]);
        SimpleDdgiStorageLayout legacy = SimpleDdgiStorageLayoutCompiler.Compile(
            [packed with { PackingMode = SimpleDdgiStoragePackingMode.Legacy }]);

        Assert.Multiple(() =>
        {
            Assert.That(first.Fingerprint, Is.Not.EqualTo(moved.Fingerprint));
            Assert.That(first.Fingerprint, Is.Not.EqualTo(legacy.Fingerprint));
            Assert.That(legacy.Regions.Single().StrideWords,
                Is.EqualTo(SimpleDdgiStorageLayoutCompiler.LegacyStrideWords));
        });
    }

    [Test]
    public void RecursiveGlossySidecar_PreservesOrdinaryStrideAndChargesFourBytesPerRay()
    {
        SimpleDdgiTransportCacheRegionRequest request =
            Request(0, "recursive", 11, 3, 8, 3, 0.125f, 0.10f) with
            {
                UseRecursiveGlossySidecar = true
            };
        SimpleDdgiStorageLayout layout =
            SimpleDdgiStorageLayoutCompiler.Compile([request]);
        SimpleDdgiTransportCacheRegion region = layout.Regions.Single();
        bool secondValid =
            SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
                region,
                12U,
                out uint secondProbeBase);
        uint flags = SimpleDdgiStorageLayoutCompiler.PackVolumeFlags(
            region.Format,
            irradianceMirrorPresent: false,
            visibilityMirrorPresent: false,
            layout.AbiVersion,
            layout.DirectionCodebookVersion,
            recursiveGlossySidecar: true);

        Assert.Multiple(() =>
        {
            Assert.That(region.UsesRecursiveGlossySidecar, Is.True);
            Assert.That(region.StrideWords,
                Is.EqualTo(region.Format.WordCount()));
            Assert.That(region.OrdinaryRecordBytes,
                Is.EqualTo(3UL * 8UL * (ulong)region.StrideWords * 4UL));
            Assert.That(region.GlossyMaterialSidecarBytes,
                Is.EqualTo(3UL * 8UL * 4UL));
            Assert.That(layout.GlossyMaterialSidecarBytes,
                Is.EqualTo(region.GlossyMaterialSidecarBytes));
            Assert.That(region.ByteCount, Is.EqualTo(
                region.OrdinaryRecordBytes +
                region.GlossyMaterialSidecarBytes));
            Assert.That(secondValid, Is.True);
            Assert.That(secondProbeBase, Is.EqualTo(
                checked((uint)(region.BaseWord + region.WordsPerProbe + 1UL))));
            Assert.That(flags &
                SimpleDdgiStorageLayoutCompiler.RecursiveGlossySidecarFlag,
                Is.Not.Zero);
        });
    }

    [Test]
    public void RecursiveGlossyAdmission_RequiresCompleteSidecarAndAccountsItExactly()
    {
        SimpleDdgiTransportCacheRegionRequest ordinaryRequest =
            Request(0, "recursive-admission", 0, 4, 16, 4, 0.125f, 0.10f);
        SimpleDdgiStorageLayout ordinaryLayout =
            SimpleDdgiStorageLayoutCompiler.Compile([ordinaryRequest]);
        SimpleDdgiStorageLayout recursiveLayout =
            SimpleDdgiStorageLayoutCompiler.Compile(
                [ordinaryRequest with { UseRecursiveGlossySidecar = true }]);
        const ulong directionalBudget = 4UL * 64UL * 2UL;

        SimpleDdgiMemoryPlan fallback = SimpleDdgiMemoryPlan.Create(
            probeCount: 4,
            updateRequestCapacity: 4,
            rayCapacity: 16,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            storageLayout: ordinaryLayout,
            directionalRadianceMode: SimpleDdgiDirectionalRadianceMode.L2,
            glossyTransportMode: SimpleDdgiGlossyTransportMode.RecursiveCertified,
            directionalRadianceBudgetBytes: directionalBudget);
        SimpleDdgiMemoryPlan admitted = SimpleDdgiMemoryPlan.Create(
            probeCount: 4,
            updateRequestCapacity: 4,
            rayCapacity: 16,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            storageLayout: recursiveLayout,
            directionalRadianceMode: SimpleDdgiDirectionalRadianceMode.L2,
            glossyTransportMode: SimpleDdgiGlossyTransportMode.RecursiveCertified,
            directionalRadianceBudgetBytes: directionalBudget);

        Assert.Multiple(() =>
        {
            Assert.That(fallback.GlossyTransportMode,
                Is.EqualTo(SimpleDdgiGlossyTransportMode.OneBounce));
            Assert.That(fallback.DirectionalRadianceFallbackReason,
                Is.EqualTo("recursive-glossy-sidecar-unavailable"));
            Assert.That(admitted.GlossyTransportMode,
                Is.EqualTo(SimpleDdgiGlossyTransportMode.RecursiveCertified));
            Assert.That(admitted.DirectionalRadianceFallbackReason, Is.Empty);
            Assert.That(admitted.TransportSourceCacheGlossyMaterialSidecarBytes,
                Is.EqualTo(4UL * 16UL * sizeof(uint)));
            Assert.That(
                admitted.TransportSourceCacheLegacyBytes +
                admitted.TransportSourceCacheCompact28Bytes +
                admitted.TransportSourceCacheCompact24Bytes +
                admitted.TransportSourceCacheGlossyMaterialSidecarBytes +
                admitted.TransportSourceCacheAlignmentBytes,
                Is.EqualTo(admitted.TransportSourceCacheBytes));
        });
    }

    [Test]
    public void ExplicitMaximumTraceDistance_DrivesPackingAndFingerprint()
    {
        SimpleDdgiTransportCacheRegionRequest request =
            Request(0, "explicit-range", 0, 8, 32, 8, 0.125f, 0.10f);
        SimpleDdgiStorageLayout derived =
            SimpleDdgiStorageLayoutCompiler.Compile([request]);
        SimpleDdgiStorageLayout explicitLongRange =
            SimpleDdgiStorageLayoutCompiler.Compile(
                [request with { MaximumTraceDistance = 80_000.0f }]);

        Assert.Multiple(() =>
        {
            Assert.That(derived.Regions.Single().MaximumTraceDistance,
                Is.EqualTo(1.0f));
            Assert.That(derived.Regions.Single().Format,
                Is.EqualTo(SimpleDdgiTransportCacheFormat.Compact24));
            Assert.That(explicitLongRange.Regions.Single().MaximumTraceDistance,
                Is.EqualTo(80_000.0f));
            Assert.That(explicitLongRange.Regions.Single().Format,
                Is.EqualTo(SimpleDdgiTransportCacheFormat.Compact28));
            Assert.That(explicitLongRange.Fingerprint,
                Is.Not.EqualTo(derived.Fingerprint));
        });
    }

    [Test]
    public void VolumeFlags_AreNonOverlappingAndLeaveReservedBitsClear()
    {
        uint flags = SimpleDdgiStorageLayoutCompiler.PackVolumeFlags(
            SimpleDdgiTransportCacheFormat.Compact24,
            irradianceMirrorPresent: true,
            visibilityMirrorPresent: false,
            SimpleDdgiStorageAbiVersion.Packed);

        Assert.Multiple(() =>
        {
            Assert.That(flags & 0x3u, Is.EqualTo((uint)SimpleDdgiTransportCacheFormat.Compact24));
            Assert.That(flags & (1u << 2), Is.Not.Zero);
            Assert.That(flags & (1u << 3), Is.Zero);
            Assert.That((flags >> 4) & 0xfu, Is.EqualTo((uint)SimpleDdgiStorageAbiVersion.Packed));
            Assert.That((flags >> 8) & 0xffu, Is.EqualTo(SimpleDdgiDirectionCodebook.Version));
            Assert.That(flags & 0xffff_0000u, Is.Zero);
        });
    }

    [Test]
    public void PackedGpuRecords_MatchEveryDeclaredWordStride()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiVolume>(), Is.EqualTo(112));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiRayResult>(), Is.EqualTo(20));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiLegacyRayResult>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiTransportRayCache>(), Is.EqualTo(36));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiTransportRayCacheCompact28>(), Is.EqualTo(28));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiTransportRayCacheCompact24>(), Is.EqualTo(24));
        });
    }

    [Test]
    public void EmptyOneAndMaximumLayouts_UseCheckedExactByteArithmetic()
    {
        SimpleDdgiStorageLayout empty = SimpleDdgiStorageLayout.Empty();
        SimpleDdgiStorageLayout one = SimpleDdgiStorageLayoutCompiler.Compile(
            [Request(0, "one", 0, 1, 1, 1, 0.125f, 0.10f)]);
        SimpleDdgiStorageLayout maximum = SimpleDdgiStorageLayoutCompiler.Compile(
            [Request(
                0,
                "maximum",
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount,
                GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe,
                1,
                0.125f,
                0.10f)]);

        Assert.Multiple(() =>
        {
            Assert.That(empty.PackingMode,
                Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
            Assert.That(empty.SourceCacheBytes,
                Is.EqualTo(SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes));
            Assert.That(empty.LegacyBytes + empty.Compact28Bytes +
                empty.Compact24Bytes + empty.AlignmentPaddingBytes,
                Is.EqualTo(empty.SourceCacheBytes));
            Assert.That(empty.AbiVersion, Is.EqualTo(SimpleDdgiStorageAbiVersion.Packed));
            Assert.That(one.SourceCacheBytes, Is.EqualTo(24UL));
            Assert.That(maximum.SourceCacheBytes, Is.EqualTo(
                (ulong)GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount *
                GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe * 24UL));
        });
    }

    [Test]
    public void ProbeCacheBaseAddress_UsesOneBasedCheckedRegionAddressing()
    {
        SimpleDdgiStorageLayout layout = SimpleDdgiStorageLayoutCompiler.Compile(
            [Request(0, "addressed", 7, 3, 8, 3, 0.125f, 0.10f)]);
        SimpleDdgiTransportCacheRegion region = layout.Regions.Single();

        bool firstValid = SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
            region,
            7u,
            out uint first);
        bool lastValid = SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
            region,
            9u,
            out uint last);

        Assert.Multiple(() =>
        {
            Assert.That(firstValid, Is.True);
            Assert.That(first, Is.EqualTo(checked((uint)region.BaseWord + 1u)));
            Assert.That(lastValid, Is.True);
            Assert.That(last, Is.EqualTo(checked(
                (uint)region.BaseWord +
                2u * 8u * (uint)region.StrideWords +
                1u)));
            Assert.That(SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
                region, 6u, out uint before), Is.False);
            Assert.That(before, Is.Zero);
            Assert.That(SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
                region, 10u, out uint after), Is.False);
            Assert.That(after, Is.Zero);
        });
    }

    [Test]
    public void InvalidOrOverlappingRequests_AreRejectedBeforeAddressCompilation()
    {
        SimpleDdgiTransportCacheRegionRequest first =
            Request(0, "first", 0, 8, 32, 8, 0.125f, 0.10f);
        Assert.Multiple(() =>
        {
            Assert.That(() => SimpleDdgiStorageLayoutCompiler.Compile(
                [first, Request(1, "overlap", 7, 8, 32, 8, 0.125f, 0.10f)]),
                Throws.TypeOf<ArgumentException>());
            Assert.That(() => SimpleDdgiStorageLayoutCompiler.Compile(
                [first, first with { VolumeIndex = 1, Identity = "first" }]),
                Throws.TypeOf<ArgumentException>());
            Assert.That(() => SimpleDdgiStorageLayoutCompiler.Compile(
                [first with { Spacing = float.NaN }]),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => SimpleDdgiStorageLayoutCompiler.Compile(
                [first with { GridCountX = 0 }]),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => SimpleDdgiStorageLayoutCompiler.Compile(
                [first with
                {
                    PhysicalProbeCount = int.MaxValue,
                    RaysPerProbe = GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe
                }]),
                Throws.TypeOf<OverflowException>());
        });
    }

    [Test]
    public void DistancePacking_ReportsEachStaticRejectionBoundary()
    {
        SimpleDdgiTransportCacheRegionRequest packed =
            Request(0, "distance", 0, 1, 32, 4, 0.125f, 0.10f);
        SimpleDdgiTransportCacheRegionRequest thin = packed with
        {
            ArchitecturalThickness = 0.01f
        };

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                packed with { PackingMode = SimpleDdgiStoragePackingMode.Legacy },
                1.0f, 0.001f, 0.0005f),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.LegacyMode));
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                packed, float.NaN, float.PositiveInfinity, float.PositiveInfinity),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.NonFiniteRange));
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                packed, 65_505.0f, float.PositiveInfinity, float.PositiveInfinity),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.HalfRangeExceeded));
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                packed, 1.0f, 0.01f, 0.005f),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.HitPointOffsetError));
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                thin, 1.0f, 0.005f, 0.0025f),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.ArchitecturalThicknessError));
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                packed, 0.49f, 0.00048828125f, 0.0f),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.SyntheticBoundaryError));
            Assert.That(SimpleDdgiStorageLayoutCompiler.ResolveDistancePackingDecision(
                packed, 0.5f, 0.00048828125f, 0.000244140625f),
                Is.EqualTo(SimpleDdgiDistancePackingDecision.Eligible));
        });
    }

    private static SimpleDdgiTransportCacheRegionRequest Request(
        int volume,
        string identity,
        int firstProbe,
        int probeCount,
        int rays,
        int maximumGridCount,
        float spacing,
        float thickness) => new(
            volume,
            identity,
            volume + 1,
            firstProbe,
            probeCount,
            rays,
            maximumGridCount,
            1,
            1,
            spacing,
            thickness,
            SimpleDdgiStoragePackingMode.Packed);
}
