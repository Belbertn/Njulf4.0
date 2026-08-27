using System.Buffers.Binary;
using System.Security.Cryptography;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class OpacityMicromapModelPackageTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "opacity-micromap-model-package-tests",
            TestContext.CurrentContext.Test.ID,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Test]
    public void ModelPackage_OptionalPayloadRoundTrips_AndLegacyPackageRemainsUsable()
    {
        PackageFixture optional = WritePackage(
            Path.Combine(_directory, "optional"),
            includeOpacityMicromap: true);
        PackageFixture legacy = WritePackage(
            Path.Combine(_directory, "legacy"),
            includeOpacityMicromap: false);

        CookedModelAsset optionalLoaded = CookedPackage.LoadModel(optional.ModelPath);
        CookedModelAsset legacyLoaded = CookedPackage.LoadModel(legacy.ModelPath);

        Assert.Multiple(() =>
        {
            Assert.That(optionalLoaded.OpacityMicromapLoadStatus.SectionPresent, Is.True);
            Assert.That(optionalLoaded.OpacityMicromapLoadStatus.Accepted, Is.True);
            Assert.That(optionalLoaded.OpacityMicromapPayload, Is.Not.Null);
            Assert.That(optionalLoaded.OpacityMicromapPayload!.SourceContentHash,
                Is.EqualTo(optional.Payload!.SourceContentHash));
            Assert.That(optionalLoaded.OpacityMicromapPayload.OmmData.Span.ToArray(),
                Is.EqualTo(optional.Payload.OmmData.Span.ToArray()));
            Assert.That(legacyLoaded.OpacityMicromapLoadStatus,
                Is.EqualTo(CookedOpacityMicromapPayloadLoadStatus.Missing));
            Assert.That(legacyLoaded.OpacityMicromapPayload, Is.Null);
            Assert.That(legacyLoaded.Mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void ModelPackage_CorruptOptionalPayload_FallsBackWithoutRejectingBaseAsset()
    {
        PackageFixture fixture = WritePackage(
            Path.Combine(_directory, "corrupt"),
            includeOpacityMicromap: true);

        byte[] bytes = File.ReadAllBytes(fixture.ModelPath);
        using (var reader = new CookedAssetReader(
                   fixture.ModelPath,
                   CookedAssetKind.Model))
        {
            CookedSectionEntry optionalSection = reader.Sections.Single(
                section => section.SectionId == CookedSectionIds.OpacityMicromap);
            int offset = checked((int)optionalSection.Offset);
            bytes[offset] ^= 0x40;
        }
        File.WriteAllBytes(fixture.ModelPath, bytes);

        CookedModelAsset loaded = CookedPackage.LoadModel(fixture.ModelPath);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.OpacityMicromapPayload, Is.Null);
            Assert.That(loaded.OpacityMicromapLoadStatus.SectionPresent, Is.True);
            Assert.That(loaded.OpacityMicromapLoadStatus.Accepted, Is.False);
            Assert.That(loaded.OpacityMicromapLoadStatus.Failure,
                Is.EqualTo(OpacityMicromapPayloadValidationFailure.SpanChecksumMismatch));
            Assert.That(loaded.Mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
            Assert.That(loaded.Materials.Materials, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ModelPackage_OutOfRangePayloadAttachment_FallsBackWithoutRejectingBaseAsset()
    {
        PackageFixture fixture = WritePackage(
            Path.Combine(_directory, "attachment-mismatch"),
            includeOpacityMicromap: true,
            opacityMicromapMaterialSlot: 1);

        CookedModelAsset loaded = CookedPackage.LoadModel(fixture.ModelPath);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.OpacityMicromapPayload, Is.Null);
            Assert.That(loaded.OpacityMicromapLoadStatus.SectionPresent, Is.True);
            Assert.That(loaded.OpacityMicromapLoadStatus.Accepted, Is.False);
            Assert.That(loaded.OpacityMicromapLoadStatus.Failure,
                Is.EqualTo(OpacityMicromapPayloadValidationFailure.ModelAttachmentInvalid));
            Assert.That(loaded.Mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void Migrator_PreservesValidatedOptionalPayloadWhileRefreshingModelReferences()
    {
        string source = Path.Combine(_directory, "migration-source");
        string output = Path.Combine(_directory, "migration-output");
        PackageFixture sourcePackage = WritePackage(
            source,
            includeOpacityMicromap: true);

        // A current model carries a semantic import identity that can be
        // preserved while migration rewrites sidecars and refreshes their
        // content-hash references. Pre-1.4 models must be recooked instead.

        CookedMigrationReport report = CookedAssetMigrator.MigrateTree(source, output);
        string migratedPath = Path.Combine(output, "models", "fixture.njmodel");
        CookedModelAsset migrated = CookedPackage.LoadModel(migratedPath);
        using var migratedReader = new CookedAssetReader(
            migratedPath,
            CookedAssetKind.Model);

        Assert.Multiple(() =>
        {
            Assert.That(report.MigratedFiles, Is.EqualTo(3));
            Assert.That(migratedReader.Header.FormatMinor,
                Is.EqualTo(CookedFormatVersions.Model.Minor));
            Assert.That(migrated.OpacityMicromapLoadStatus.Accepted, Is.True);
            Assert.That(migrated.OpacityMicromapPayload, Is.Not.Null);
            Assert.That(migrated.OpacityMicromapPayload!.SourceContentHash,
                Is.EqualTo(sourcePackage.Payload!.SourceContentHash));
            Assert.That(migrated.Mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void ModelCooker_ProducerPublishesValidatedChunk_AndIdentityInvalidatesIncrementalOutput()
    {
        string sourcePath = Path.Combine(_directory, "producer.gltf");
        WriteTriangleGltf(sourcePath);
        var firstProducer = new StaticPayloadProducer(
            ProducerIdentity("tests.static", 7, 1, 200),
            CreatePayload(10));
        var secondProducer = new StaticPayloadProducer(
            ProducerIdentity("tests.static", 7, 1, 203),
            CreatePayload(11, provenanceKey: 203));
        var common = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            }
        };

        using var cooker = new ModelAssetCooker();
        AssetCookResult first = cooker.CookModel(
            sourcePath,
            _directory,
            common with { OpacityMicromapPayloadProducer = firstProducer });
        AssetCookResult unchanged = cooker.CookModel(
            sourcePath,
            _directory,
            common with { OpacityMicromapPayloadProducer = firstProducer });
        AssetCookResult changedIdentity = cooker.CookModel(
            sourcePath,
            _directory,
            common with { OpacityMicromapPayloadProducer = secondProducer });

        string modelPath = Path.Combine(_directory, "models", "producer.njmodel");
        CookedModelAsset loaded = CookedPackage.LoadModel(modelPath);

        Assert.Multiple(() =>
        {
            Assert.That(first.Skipped, Is.False);
            Assert.That(unchanged.Skipped, Is.True);
            Assert.That(changedIdentity.Skipped, Is.False);
            Assert.That(firstProducer.CallCount, Is.EqualTo(1));
            Assert.That(secondProducer.CallCount, Is.EqualTo(1));
            Assert.That(loaded.OpacityMicromapLoadStatus.Accepted, Is.True);
            Assert.That(loaded.OpacityMicromapPayload!.SourceContentHash,
                Is.EqualTo(secondProducer.Payload.SourceContentHash));
            Assert.That(changedIdentity.Report.Warnings,
                Has.None.Contains("optional payload rejected"));
        });
    }

    [Test]
    public void ModelCooker_ProducerFailure_IsBoundedAndPublishesTheOrdinaryFallback()
    {
        string sourcePath = Path.Combine(_directory, "producer-failure.gltf");
        WriteTriangleGltf(sourcePath);
        var options = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            Force = true,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            },
            OpacityMicromapPayloadProducer = new ThrowingPayloadProducer()
        };

        using var cooker = new ModelAssetCooker();
        AssetCookResult result = cooker.CookModel(sourcePath, _directory, options);
        CookedModelAsset loaded = CookedPackage.LoadModel(
            Path.Combine(_directory, "models", "producer-failure.njmodel"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skipped, Is.False);
            Assert.That(result.Report.Warnings,
                Has.Some.StartsWith("OpacityMicromap: optional producer failed"));
            Assert.That(loaded.OpacityMicromapPayload, Is.Null);
            Assert.That(loaded.OpacityMicromapLoadStatus,
                Is.EqualTo(CookedOpacityMicromapPayloadLoadStatus.Missing));
            Assert.That(loaded.Mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void ModelCooker_ProducerProvenanceMismatch_IsRejectedBeforeModelPublication()
    {
        string sourcePath = Path.Combine(_directory, "producer-provenance.gltf");
        WriteTriangleGltf(sourcePath);
        var producer = new StaticPayloadProducer(
            ProducerIdentity("tests.provenance", 7, 1, 203),
            CreatePayload(12, provenanceKey: 200));
        var options = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            Force = true,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            },
            OpacityMicromapPayloadProducer = producer
        };

        using var cooker = new ModelAssetCooker();
        AssetCookResult result = cooker.CookModel(sourcePath, _directory, options);
        CookedModelAsset loaded = CookedPackage.LoadModel(
            Path.Combine(_directory, "models", "producer-provenance.njmodel"));

        Assert.Multiple(() =>
        {
            Assert.That(producer.CallCount, Is.EqualTo(1));
            Assert.That(result.Report.Warnings,
                Has.Some.Contains("SDK provenance does not match"));
            Assert.That(loaded.OpacityMicromapPayload, Is.Null);
            Assert.That(loaded.OpacityMicromapLoadStatus,
                Is.EqualTo(CookedOpacityMicromapPayloadLoadStatus.Missing));
            Assert.That(loaded.Mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
        });
    }

    private static PackageFixture WritePackage(
        string root,
        bool includeOpacityMicromap,
        uint opacityMicromapMaterialSlot = 0)
    {
        string modelDirectory = Path.Combine(root, "models");
        string materialDirectory = Path.Combine(root, "materials");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(materialDirectory);

        string meshPath = Path.Combine(modelDirectory, "fixture.meshes.njmesh");
        string materialPath = Path.Combine(materialDirectory, "fixture.materials.njmat");
        string modelPath = Path.Combine(modelDirectory, "fixture.njmodel");
        CookedMeshPayload mesh = CreateTrianglePayload();
        CookedPackage.WriteMesh(
            meshPath,
            mesh,
            sourceHash: 11,
            settingsHash: 12,
            dependencyHash: 13,
            useMeshOptimizer: false);
        CookedPackage.WriteMaterials(
            materialPath,
            new CookedMaterialTable([ModelMaterial.Default]),
            sourceHash: 11,
            settingsHash: 12,
            dependencyHash: 13);
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var manifest = new CookedModelManifest(
            Guid.NewGuid(),
            "fixture",
            Path.Combine(root, "fixture.gltf").Replace('\\', '/'),
            11,
            12,
            13,
            new CookedAssetReference(Path.GetFileName(meshPath), CookedHash.File(meshPath)),
            new CookedAssetReference("../materials/fixture.materials.njmat", CookedHash.File(materialPath)),
            null,
            [new CookedModelSubObject(
                "Triangle",
                0,
                0,
                -1,
                -1,
                Matrix4x4.Identity)],
            bounds,
            BoundingSphere.FromBox(bounds));

        OpacityMicromapCookedPayload? payload = includeOpacityMicromap
            ? CreatePayload(1, opacityMicromapMaterialSlot)
            : null;
        CookedOpacityMicromapModelChunk? chunk = null;
        if (payload is not null)
        {
            Assert.That(
                CookedOpacityMicromapModelChunk.TryCreate(
                    payload,
                    out chunk,
                    out string detail),
                Is.True,
                detail);
        }

        CookedPackage.WriteModel(
            modelPath,
            manifest,
            toolVersion: 17,
            opacityMicromapChunk: chunk);
        return new PackageFixture(modelPath, payload);
    }

    private static CookedMeshPayload CreateTrianglePayload()
    {
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var record = new CookedSubMeshRecord(
            "Triangle",
            0,
            -1,
            -1,
            Matrix4x4.Identity,
            0,
            3,
            0,
            3,
            0,
            0,
            0,
            1,
            0,
            3,
            0,
            3,
            [new ProcessedMeshLodRange(0, 0, 1, 1)],
            [new ProcessedMeshDrawRange("Triangle", 0, 0, 3, 0)],
            bounds,
            BoundingSphere.FromBox(bounds),
            (uint)ProcessedVertexAttribute.Position);
        return new CookedMeshPayload(
            [record],
            [new(), new(), new()],
            [new(), new(), new()],
            [new(), new(), new()],
            [],
            [0u, 1u, 2u],
            [new Meshlet(Vector3.Zero, 1, 0, 3, 0, 3, 0, 3, 0, 1)],
            [],
            [],
            [0u, 1u, 2u],
            [0u, 1u, 2u]);
    }

    private static OpacityMicromapCookedPayload CreatePayload(
        byte sourceKey,
        uint materialSlot = 0,
        byte provenanceKey = 200) =>
        OpacityMicromapCookedPayload.Create(
            cookAbi: 7,
            sourceContentHash: Key(sourceKey),
            sdkProvenanceHash: Key(provenanceKey),
            maximumSubdivisionLevel: 1,
            primitiveCount: 1,
            descriptorCount: 1,
            materialContracts:
            [
                new OpacityMicromapMaterialContract(
                    MaterialSlot: materialSlot,
                    FirstPrimitive: 0,
                    PrimitiveCount: 1,
                    TexCoordSet: 0,
                    UvTransform: OpacityMicromapUvTransformBits.Identity,
                    TextureContentHash: Key(201),
                    TextureFormatAndMipHash: Key(202),
                    Sampler: OpacityMicromapEligibilityInput.ExactStaticMask.Sampler,
                    MaterialAlphaBits: Bits(1.0f),
                    UniformVertexAlphaBits: Bits(1.0f),
                    AlphaCutoffBits: Bits(0.5f),
                    FixedLodBits: Bits(0.0f),
                    AlphaContractRevision: 1,
                    ShaderAbiRevision: 1)
            ],
            usageHistogram:
            [
                new OpacityMicromapUsage(
                    OpacityMicromapFormat.FourState,
                    SubdivisionLevel: 1,
                    Count: 1)
            ],
            ommData: [1, 2, 3, 4],
            indexData: [5, 6, 7, 8],
            descriptorData: [9],
            classificationStatistics:
                new OpacityMicromapClassificationStatistics(1, 0, 0, 0));

    private static OpacityMicromapContentKey Key(byte value) =>
        OpacityMicromapContentKey.FromSha256(
            SHA256.HashData(new[] { value }));

    private static OpacityMicromapPayloadProducerIdentity ProducerIdentity(
        string name,
        uint cookAbi,
        uint policyRevision,
        byte provenanceKey) => new(name, cookAbi, policyRevision)
    {
        SdkProvenanceHash = Key(provenanceKey)
    };

    private static uint Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static void WriteTriangleGltf(string gltfPath)
    {
        string directory = Path.GetDirectoryName(gltfPath)!;
        Directory.CreateDirectory(directory);
        string binPath = Path.ChangeExtension(gltfPath, ".bin");
        byte[] positions =
        [
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f)
        ];
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        File.WriteAllBytes(binPath, [.. positions, .. indices]);
        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf optional OMM package test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0 }],
                "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1, "mode": 4 }] }],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{positions.Length + indices.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{positions.Length}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  {
                    "bufferView": 0,
                    "componentType": 5126,
                    "count": 3,
                    "type": "VEC3",
                    "min": [0, 0, 0],
                    "max": [1, 1, 0]
                  },
                  {
                    "bufferView": 1,
                    "componentType": 5123,
                    "count": 3,
                    "type": "SCALAR",
                    "min": [0],
                    "max": [2]
                  }
                ]
              }
              """);
    }

    private sealed record PackageFixture(
        string ModelPath,
        OpacityMicromapCookedPayload? Payload);

    private sealed class StaticPayloadProducer : IOpacityMicromapModelPayloadProducer
    {
        public StaticPayloadProducer(
            OpacityMicromapPayloadProducerIdentity identity,
            OpacityMicromapCookedPayload payload)
        {
            Identity = identity;
            Payload = payload;
        }

        public OpacityMicromapPayloadProducerIdentity Identity { get; }
        public OpacityMicromapCookedPayload Payload { get; }
        public int CallCount { get; private set; }

        public OpacityMicromapPayloadProductionResult Produce(
            in OpacityMicromapModelCookContext context)
        {
            CallCount++;
            return OpacityMicromapPayloadProductionResult.Produced(
                Payload,
                "test-produced");
        }
    }

    private sealed class ThrowingPayloadProducer : IOpacityMicromapModelPayloadProducer
    {
        public OpacityMicromapPayloadProducerIdentity Identity { get; } = new(
            "tests.throwing",
            7,
            1)
        {
            SdkProvenanceHash = Key(200)
        };

        public OpacityMicromapPayloadProductionResult Produce(
            in OpacityMicromapModelCookContext context) =>
            throw new InvalidOperationException("native bridge unavailable");
    }
}
