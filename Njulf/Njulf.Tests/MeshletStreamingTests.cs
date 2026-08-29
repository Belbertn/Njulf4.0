using System.IO.Hashing;
using System.Security.Cryptography;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class MeshletStreamingTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfMeshletStreamingTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task MeshPackage_WritesAuthenticatedContentAddressedPages()
    {
        string path = Path.Combine(_directory, "streamed.njmesh");
        CookedPackage.WriteMesh(
            path,
            CreateThreeLodPayload(),
            sourceHash: 11,
            settingsHash: 12,
            dependencyHash: 13);

        CookedMeshPayload loaded = CookedPackage.LoadMesh(
            path,
            CookedAssetReaderFlags.None,
            out _);
        MeshletStreamingManifest manifest = loaded.StreamingManifest!;
        string sidecarPath = Path.Combine(
            _directory,
            manifest.SidecarFileName);
        string exactContentId = Convert.ToHexString(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(sidecarPath))
                    .AsSpan(0, 16))
            .ToLowerInvariant();
        Assert.Multiple(() =>
        {
            Assert.That(manifest, Is.Not.Null);
            Assert.That(
                manifest.PageSizeBytes,
                Is.EqualTo(
                    MeshletStreamingManifest.ProductionPageSizeBytes));
            Assert.That(manifest.Pages, Has.Count.EqualTo(3));
            Assert.That(manifest.PinnedPageCount, Is.EqualTo(1));
            Assert.That(
                manifest.SidecarFileName,
                Does.Match(
                    "^streamed\\.njmesh\\.meshlets-[0-9a-f]{32}\\.pages$"));
            Assert.That(
                manifest.SidecarFileName,
                Is.EqualTo(
                    $"streamed.njmesh.meshlets-{exactContentId}.pages"));
            Assert.That(File.Exists(sidecarPath), Is.True);
        });

        using var pageFile = MeshletStreamingPageFile.Open(path, manifest);
        for (int pageId = 0; pageId < manifest.Pages.Count; pageId++)
        {
            byte[] decoded = await pageFile.ReadPageAsync(pageId);
            MeshletStreamingPagePayload payload =
                MeshletStreamingPageCodec.Decode(decoded);
            Assert.Multiple(() =>
            {
                Assert.That(
                    decoded,
                    Has.Length.EqualTo(
                        manifest.Pages[pageId].UncompressedBytes));
                Assert.That(payload.Meshlets, Has.Length.EqualTo(1));
                Assert.That(
                    payload.LocalVertexIndices,
                    Is.EqualTo(new uint[] { 0, 1, 2 }));
                Assert.That(
                    payload.LocalTriangleIndices,
                    Is.EqualTo(new uint[] { 0, 1, 2 }));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                manifest.Pages[0].Flags &
                MeshletStreamingPageFlags.Streamable,
                Is.EqualTo(MeshletStreamingPageFlags.Streamable));
            Assert.That(
                manifest.Pages[1].Flags &
                MeshletStreamingPageFlags.Streamable,
                Is.EqualTo(MeshletStreamingPageFlags.Streamable));
            Assert.That(
                manifest.Pages[2].Flags &
                MeshletStreamingPageFlags.Pinned,
                Is.EqualTo(MeshletStreamingPageFlags.Pinned));
            Assert.That(manifest.Pages[0].FallbackPageId, Is.EqualTo(2));
            Assert.That(manifest.Pages[1].FallbackPageId, Is.EqualTo(2));
            Assert.That(manifest.Pages[2].FallbackPageId, Is.EqualTo(2));
        });
    }

    [Test]
    public void MeshletPageFile_RejectsCorruptPageData()
    {
        string path = Path.Combine(_directory, "corrupt.njmesh");
        CookedPackage.WriteMesh(
            path,
            CreateThreeLodPayload(),
            1,
            2,
            3);
        CookedMeshPayload loaded = CookedPackage.LoadMesh(
            path,
            CookedAssetReaderFlags.None,
            out _);
        MeshletStreamingManifest manifest = loaded.StreamingManifest!;
        string sidecarPath = Path.Combine(
            _directory,
            manifest.SidecarFileName);
        MeshletStreamingPageRecord record = manifest.Pages[0];
        using (FileStream stream = new(
                   sidecarPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            stream.Position = record.DataOffset;
            int value = stream.ReadByte();
            Assert.That(value, Is.GreaterThanOrEqualTo(0));
            stream.Position = record.DataOffset;
            stream.WriteByte((byte)(value ^ 0x5a));
            stream.Flush(flushToDisk: true);
        }

        using var pageFile = MeshletStreamingPageFile.Open(path, manifest);
        Assert.That(
            async () => await pageFile.ReadPageAsync(record.PageId),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task SingleFileMigration_CopiesAndRevalidatesStreamingSidecar()
    {
        string sourceDirectory = Path.Combine(_directory, "source");
        string targetDirectory = Path.Combine(_directory, "target");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "asset.njmesh");
        string targetPath = Path.Combine(targetDirectory, "asset.njmesh");
        CookedPackage.WriteMesh(
            sourcePath,
            CreateThreeLodPayload(),
            1,
            2,
            3);

        CookedAssetMigrator.MigrateFile(sourcePath, targetPath);

        CookedMeshPayload migrated = CookedPackage.LoadMesh(
            targetPath,
            CookedAssetReaderFlags.None,
            out _);
        MeshletStreamingManifest manifest = migrated.StreamingManifest!;
        using var pageFile = MeshletStreamingPageFile.Open(
            targetPath,
            manifest);
        byte[] page = await pageFile.ReadPageAsync(0);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(
                targetDirectory,
                manifest.SidecarFileName)), Is.True);
            Assert.That(
                MeshletStreamingPageCodec.Decode(page).Meshlets,
                Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void PageBundle_UsesTheCompleteMultipageCoarseFallbackCut()
    {
        CookedMeshPayload payload = CreateMultipageCoarsePayload();
        MeshletStreamingManifest manifest =
            MeshletStreamingPageBundle.Build(
                    payload,
                    "multipage.pages")
                .Manifest;
        MeshletStreamingPageRecord[] coarsePages = manifest.Pages
            .Where(page =>
                (page.Flags & MeshletStreamingPageFlags.Lod2) != 0)
            .ToArray();
        MeshletStreamingPageRecord finePage = manifest.Pages.First(page =>
            (page.Flags & MeshletStreamingPageFlags.Lod0) != 0);

        Assert.Multiple(() =>
        {
            Assert.That(coarsePages, Has.Length.GreaterThan(1));
            Assert.That(
                finePage.FallbackPageId,
                Is.EqualTo(coarsePages[0].PageId));
            Assert.That(
                finePage.FallbackPageCount,
                Is.EqualTo(coarsePages.Length));
            Assert.That(
                coarsePages.Select(static page => page.PageId),
                Is.EqualTo(Enumerable.Range(
                    coarsePages[0].PageId,
                    coarsePages.Length)));
            Assert.That(
                coarsePages.All(page =>
                    (page.Flags & MeshletStreamingPageFlags.Pinned) != 0),
                Is.True);
        });
    }

    [Test]
    public void ContentAddress_AuthenticatesResidencyAndFallbackMetadata()
    {
        CookedMeshPayload staticPayload = CreateThreeLodPayload();
        CookedSubMeshRecord skinnedSubMesh =
            staticPayload.SubMeshes[0] with
            {
                SkinIndex = 0
            };
        CookedMeshPayload skinnedPayload = staticPayload with
        {
            SubMeshes = [skinnedSubMesh]
        };

        MeshletStreamingManifest staticManifest =
            MeshletStreamingPageBundle.Build(
                    staticPayload,
                    "temporary-static.pages")
                .WithContentAddressedSidecarName("same.njmesh")
                .Manifest;
        MeshletStreamingManifest skinnedManifest =
            MeshletStreamingPageBundle.Build(
                    skinnedPayload,
                    "temporary-skinned.pages")
                .WithContentAddressedSidecarName("same.njmesh")
                .Manifest;

        Assert.Multiple(() =>
        {
            Assert.That(
                staticManifest.Pages.Select(page => page.UncompressedBytes),
                Is.EqualTo(skinnedManifest.Pages.Select(page =>
                    page.UncompressedBytes)));
            Assert.That(
                staticManifest.Pages[0].Flags,
                Is.Not.EqualTo(skinnedManifest.Pages[0].Flags));
            Assert.That(
                staticManifest.SidecarFileName,
                Is.Not.EqualTo(skinnedManifest.SidecarFileName));
        });
    }

    [Test]
    public async Task Residency_PublishesPinnedFirstAndRetiresBeforeReuse()
    {
        InMemoryPageSource source = CreateInMemorySource();
        var uploader = new RecordingUploader();
        var manager = new MeshletStreamingResidencyManager(
            source,
            uploader,
            CreateSmallOptions());

        await manager.TickAsync(submissionSerial: 0, completedSerial: 0);
        Assert.That(
            manager.GetState(2),
            Is.EqualTo(MeshletPageResidencyState.Uploading));
        await manager.TickAsync(submissionSerial: 1, completedSerial: 1);
        Assert.That(
            manager.GetState(2),
            Is.EqualTo(MeshletPageResidencyState.Resident));

        MeshletPageResolution fallback = manager.RequestPage(
            0,
            MeshletStreamingResidencyManager.VisiblePriority,
            serial: 1);
        Assert.Multiple(() =>
        {
            Assert.That(fallback.IsResident, Is.True);
            Assert.That(fallback.UsesFallback, Is.True);
            Assert.That(fallback.ResolvedPageId, Is.EqualTo(2));
        });
        await manager.TickAsync(submissionSerial: 1, completedSerial: 1);
        await manager.TickAsync(submissionSerial: 2, completedSerial: 2);
        Assert.That(
            manager.ResolveResident(0).ResolvedPageId,
            Is.EqualTo(0));

        manager.RequestPage(
            1,
            MeshletStreamingResidencyManager.VisiblePriority + 1,
            serial: 3);
        await manager.TickAsync(submissionSerial: 3, completedSerial: 3);
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetState(0),
                Is.EqualTo(MeshletPageResidencyState.Evicting));
            Assert.That(
                manager.GetState(1),
                Is.EqualTo(MeshletPageResidencyState.Queued));
            Assert.That(
                uploader.Unpublished.Single().RetireAfterSerial,
                Is.EqualTo(5));
        });

        await manager.TickAsync(submissionSerial: 4, completedSerial: 4);
        Assert.That(
            manager.GetState(1),
            Is.EqualTo(MeshletPageResidencyState.Queued));
        await manager.TickAsync(submissionSerial: 5, completedSerial: 5);
        Assert.That(
            manager.GetState(1),
            Is.EqualTo(MeshletPageResidencyState.Uploading));
        await manager.TickAsync(submissionSerial: 6, completedSerial: 6);

        MeshletStreamingResidencySnapshot snapshot =
            manager.CreateSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.ResolveResident(1).ResolvedPageId,
                Is.EqualTo(1));
            Assert.That(snapshot.ResidentPageCount, Is.EqualTo(2));
            Assert.That(snapshot.PinnedResidentPageCount, Is.EqualTo(1));
            Assert.That(snapshot.EvictionCount, Is.EqualTo(1));
            Assert.That(snapshot.FailureCount, Is.Zero);
            Assert.That(uploader.Published.Keys, Is.EquivalentTo(new[] { 1, 2 }));
        });
    }

    [Test]
    public async Task Residency_AuthenticationFailureKeepsPinnedFallbackAndRetries()
    {
        InMemoryPageSource source = CreateInMemorySource();
        source.FailPageId = 0;
        var uploader = new RecordingUploader();
        var manager = new MeshletStreamingResidencyManager(
            source,
            uploader,
            CreateSmallOptions());

        await manager.TickAsync(0, 0);
        await manager.TickAsync(1, 1);
        manager.RequestPage(
            0,
            MeshletStreamingResidencyManager.VisiblePriority,
            1);
        await manager.TickAsync(1, 1);

        MeshletStreamingResidencySnapshot failed =
            manager.CreateSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetState(0),
                Is.EqualTo(MeshletPageResidencyState.Failed));
            Assert.That(failed.FailureCount, Is.EqualTo(1));
            Assert.That(failed.FreePhysicalPageCount, Is.EqualTo(1));
            Assert.That(
                manager.ResolveResident(0).ResolvedPageId,
                Is.EqualTo(2));
        });

        source.FailPageId = -1;
        manager.RequestPage(
            0,
            MeshletStreamingResidencyManager.VisiblePriority,
            2);
        await manager.TickAsync(2, 2);
        await manager.TickAsync(3, 3);
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetState(0),
                Is.EqualTo(MeshletPageResidencyState.Resident));
            Assert.That(
                manager.CreateSnapshot().FailureCount,
                Is.EqualTo(1));
        });
    }

    [Test]
    public void Residency_BoundsUniqueDemandWithoutDroppingPinnedFallback()
    {
        InMemoryPageSource source = CreateInMemorySource();
        var manager = new MeshletStreamingResidencyManager(
            source,
            new RecordingUploader(),
            CreateSmallOptions() with
            {
                MaximumRequestsPerSerial = 1
            });

        manager.RequestPage(
            0,
            MeshletStreamingResidencyManager.VisiblePriority,
            serial: 7);
        manager.RequestPage(
            1,
            MeshletStreamingResidencyManager.VisiblePriority,
            serial: 7);
        MeshletStreamingResidencySnapshot snapshot =
            manager.CreateSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetState(0),
                Is.EqualTo(MeshletPageResidencyState.Queued));
            Assert.That(
                manager.GetState(1),
                Is.EqualTo(MeshletPageResidencyState.Unloaded));
            Assert.That(
                manager.GetState(2),
                Is.EqualTo(MeshletPageResidencyState.Queued));
            Assert.That(snapshot.DroppedRequestCount, Is.EqualTo(1));
        });
    }

    private static MeshletStreamingResidencyOptions CreateSmallOptions() =>
        new(
            PhysicalPageCapacity: 2,
            MaximumUploadBytesPerTick:
                MeshletStreamingManifest.ProductionPageSizeBytes,
            MaximumAdmissionsPerTick: 1,
            MaximumConcurrentReads: 1,
            EvictionGraceSerials: 1,
            DemandLifetimeSerials: 100,
            RetryBaseSerials: 1,
            RetryMaximumSerials: 2,
            FramesInFlight: 2);

    private static InMemoryPageSource CreateInMemorySource()
    {
        byte[] pageBytes = MeshletStreamingPageCodec.Encode(
            new MeshletStreamingPagePayload(
                [CreateMeshlet(0)],
                [0u, 1u, 2u],
                [0u, 1u, 2u]));
        MeshletStreamingPageFlags[] flags =
        [
            MeshletStreamingPageFlags.Streamable |
            MeshletStreamingPageFlags.Lod0,
            MeshletStreamingPageFlags.Streamable |
            MeshletStreamingPageFlags.Lod1,
            MeshletStreamingPageFlags.Pinned |
            MeshletStreamingPageFlags.Lod2
        ];
        var records = new MeshletStreamingPageRecord[3];
        for (int pageId = 0; pageId < records.Length; pageId++)
        {
            records[pageId] = new MeshletStreamingPageRecord(
                pageId,
                SubMeshIndex: 0,
                LogicalFirstMeshlet: pageId,
                MeshletCount: 1,
                flags[pageId],
                FallbackPageId: pageId == 2 ? 2 : 2,
                DataOffset: 4096L * (pageId + 1),
                StoredBytes: pageBytes.Length,
                UncompressedBytes: pageBytes.Length,
                CookedCompression.None,
                Crc32.HashToUInt32(pageBytes),
                XxHash64.HashToUInt64(pageBytes));
        }
        var manifest = new MeshletStreamingManifest(
            MeshletStreamingManifest.CurrentSchemaVersion,
            MeshletStreamingManifest.ProductionPageSizeBytes,
            "memory.pages",
            records,
            records.Sum(static record => (long)record.StoredBytes),
            records.Sum(static record => (long)record.UncompressedBytes),
            PinnedPageCount: 1);
        manifest.Validate("memory");
        return new InMemoryPageSource(
            manifest,
            Enumerable.Repeat(pageBytes, 3)
                .Select(static bytes => (byte[])bytes.Clone())
                .ToArray());
    }

    private static CookedMeshPayload CreateThreeLodPayload()
    {
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var subMesh = new CookedSubMeshRecord(
            "Triangle",
            MaterialSlot: 0,
            NodeIndex: -1,
            SkinIndex: -1,
            SkinningBindTransform: Matrix4x4.Identity,
            VertexOffset: 0,
            VertexCount: 3,
            IndexOffset: 0,
            IndexCount: 3,
            SkinningOffset: 0,
            SkinningCount: 0,
            MeshletOffset: 0,
            MeshletCount: 1,
            MeshletVertexOffset: 0,
            MeshletVertexCount: 9,
            MeshletTriangleOffset: 0,
            MeshletTriangleCount: 9,
            [
                new ProcessedMeshLodRange(0, 0, 1, 1f),
                new ProcessedMeshLodRange(1, 1, 1, 0.5f),
                new ProcessedMeshLodRange(2, 2, 1, 0.25f)
            ],
            [new ProcessedMeshDrawRange("Triangle", 0, 0, 3, 0)],
            bounds,
            BoundingSphere.FromBox(bounds),
            (uint)ProcessedVertexAttribute.Position)
        {
            MeshletLod1Offset = 0,
            MeshletLod1Count = 1,
            MeshletLod2Offset = 0,
            MeshletLod2Count = 1,
            CoarseRayProxyIndexOffset = 0,
            CoarseRayProxyIndexCount = 3
        };
        return new CookedMeshPayload(
            [subMesh],
            [new(), new(), new()],
            [new(), new(), new()],
            [new(), new(), new()],
            [],
            [0u, 1u, 2u],
            [CreateMeshlet(0)],
            [CreateMeshlet(3)],
            [CreateMeshlet(6)],
            [0u, 1u, 2u, 0u, 1u, 2u, 0u, 1u, 2u],
            [0u, 1u, 2u, 0u, 1u, 2u, 0u, 1u, 2u])
        {
            CoarseRayProxyIndices = [0u, 1u, 2u]
        };
    }

    private static CookedMeshPayload CreateMultipageCoarsePayload()
    {
        const int localVertexCount = 48;
        const int localTriangleCount = 64;
        const int lod0Count = 1;
        const int lod1Count = 1;
        const int lod2Count = 80;
        const int totalMeshletCount = lod0Count + lod1Count + lod2Count;
        var allMeshlets = new Meshlet[totalMeshletCount];
        var meshletVertices = new uint[
            totalMeshletCount * localVertexCount];
        var meshletTriangles = new uint[
            totalMeshletCount * localTriangleCount * 3];
        for (int meshletIndex = 0;
             meshletIndex < totalMeshletCount;
             meshletIndex++)
        {
            int vertexOffset = meshletIndex * localVertexCount;
            int triangleOffset =
                meshletIndex * localTriangleCount * 3;
            allMeshlets[meshletIndex] = new Meshlet(
                Vector3.Zero,
                1f,
                0,
                localVertexCount,
                0,
                localTriangleCount * 3,
                (uint)vertexOffset,
                localVertexCount,
                (uint)triangleOffset,
                localTriangleCount);
            for (int vertex = 0; vertex < localVertexCount; vertex++)
                meshletVertices[vertexOffset + vertex] = (uint)vertex;
            for (int index = 0;
                 index < localTriangleCount * 3;
                 index++)
            {
                meshletTriangles[triangleOffset + index] =
                    (uint)(index % 3);
            }
        }

        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var subMesh = new CookedSubMeshRecord(
            "Multipage",
            0,
            -1,
            -1,
            Matrix4x4.Identity,
            0,
            localVertexCount,
            0,
            3,
            0,
            0,
            0,
            lod0Count,
            0,
            meshletVertices.Length,
            0,
            meshletTriangles.Length,
            [
                new ProcessedMeshLodRange(0, 0, lod0Count, 1f),
                new ProcessedMeshLodRange(1, lod0Count, lod1Count, 0.5f),
                new ProcessedMeshLodRange(
                    2,
                    lod0Count + lod1Count,
                    lod2Count,
                    0.25f)
            ],
            [new ProcessedMeshDrawRange("Multipage", 0, 0, 3, 0)],
            bounds,
            BoundingSphere.FromBox(bounds),
            (uint)ProcessedVertexAttribute.Position)
        {
            MeshletLod1Offset = 0,
            MeshletLod1Count = lod1Count,
            MeshletLod2Offset = 0,
            MeshletLod2Count = lod2Count
        };
        return new CookedMeshPayload(
            [subMesh],
            new CookedVertexPositionStream[localVertexCount],
            new CookedVertexNormalTangentStream[localVertexCount],
            new CookedVertexUvColorStream[localVertexCount],
            [],
            [0u, 1u, 2u],
            allMeshlets[..lod0Count],
            allMeshlets[lod0Count..(lod0Count + lod1Count)],
            allMeshlets[(lod0Count + lod1Count)..],
            meshletVertices,
            meshletTriangles);
    }

    private static Meshlet CreateMeshlet(uint localOffset) =>
        new(
            Vector3.Zero,
            boundingSphereRadius: 1f,
            vertexOffset: 0,
            vertexCount: 3,
            indexOffset: 0,
            indexCount: 3,
            localVertexOffset: localOffset,
            localVertexCount: 3,
            localTriangleOffset: localOffset,
            localTriangleCount: 1);

    private sealed class InMemoryPageSource : IMeshletStreamingPageSource
    {
        private readonly byte[][] _pages;

        public InMemoryPageSource(
            MeshletStreamingManifest manifest,
            byte[][] pages)
        {
            Manifest = manifest;
            _pages = pages;
        }

        public MeshletStreamingManifest Manifest { get; }

        public int FailPageId { get; set; } = -1;

        public ValueTask<byte[]> ReadPageAsync(
            int pageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageId == FailPageId)
            {
                throw new InvalidDataException(
                    "Synthetic authenticated page failure.");
            }
            return ValueTask.FromResult((byte[])_pages[pageId].Clone());
        }
    }

    private sealed class RecordingUploader : IMeshletStreamingPageUploader
    {
        private long _nextTicket;

        public Dictionary<int, int> Published { get; } = [];

        public List<(
            int PageId,
            int PhysicalSlot,
            ulong RetireAfterSerial)> Unpublished { get; } = [];

        public ValueTask<MeshletPageUploadTicket> BeginUploadAsync(
            int pageId,
            int physicalSlot,
            ReadOnlyMemory<byte> decodedPage,
            ulong submissionSerial,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = MeshletStreamingPageCodec.Decode(decodedPage.Span);
            return ValueTask.FromResult(new MeshletPageUploadTicket(
                Interlocked.Increment(ref _nextTicket),
                pageId,
                physicalSlot,
                submissionSerial + 1));
        }

        public void PublishResident(int pageId, int physicalSlot)
        {
            if (!Published.TryAdd(pageId, physicalSlot))
                throw new InvalidOperationException("Page was already published.");
        }

        public void UnpublishResident(
            int pageId,
            int physicalSlot,
            ulong retireAfterSerial)
        {
            if (!Published.Remove(pageId, out int publishedSlot) ||
                publishedSlot != physicalSlot)
            {
                throw new InvalidOperationException(
                    "Page publication did not match its eviction.");
            }
            Unpublished.Add((pageId, physicalSlot, retireAfterSerial));
        }
    }
}
