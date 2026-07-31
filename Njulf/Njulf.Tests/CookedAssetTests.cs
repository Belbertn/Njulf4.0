using System.Buffers.Binary;
using System.Diagnostics;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class CookedAssetTests
{
    private string _directory = null!;
    private string? _previousRequireSignature;

    [SetUp]
    public void SetUp()
    {
        _previousRequireSignature = Environment.GetEnvironmentVariable(CookedRuntimePolicy.RequireSignatureVariable);
        Environment.SetEnvironmentVariable(CookedRuntimePolicy.RequireSignatureVariable, "false");
        _directory = Path.Combine(Path.GetTempPath(), "NjulfCookedTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(CookedRuntimePolicy.RequireSignatureVariable, _previousRequireSignature);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void BinaryWriterReader_RoundTripsPrimitiveSectionsAndStrings()
    {
        string path = Path.Combine(_directory, "roundtrip.njmesh");
        var vectors = new[] { new Vector3(1, 2, 3), new Vector3(-4, 5, 6) };
        var strings = new CookedStringTableBuilder();
        Assert.That(strings.Add("mesh"), Is.EqualTo(0));
        Assert.That(strings.Add("material"), Is.EqualTo(1));
        Assert.That(strings.Add("mesh"), Is.EqualTo(0));
        using (var writer = new CookedAssetWriter(path, CookedAssetKind.Mesh, sourceHash: 11, importSettingsHash: 12, dependencyListHash: 13))
        {
            writer.WriteSection(CookedSectionIds.VertexPositions, CookedSectionFlags.Required, vectors);
            writer.WriteSection(CookedSectionIds.StringTable, CookedSectionFlags.None, strings.Build());
            writer.Complete();
        }

        using var reader = new CookedAssetReader(path, CookedAssetKind.Mesh);
        Assert.Multiple(() =>
        {
            Assert.That(reader.Header.SourceHash, Is.EqualTo(11));
            Assert.That(reader.Header.ImportSettingsHash, Is.EqualTo(12));
            Assert.That(reader.Header.DependencyListHash, Is.EqualTo(13));
            Assert.That(reader.ReadSection<Vector3>(CookedSectionIds.VertexPositions), Is.EqualTo(vectors));
        });
        CookedStringTable table = CookedStringTable.Parse(reader.GetRequiredSection(CookedSectionIds.StringTable).Span, path);
        Assert.That(new[] { table[0], table[1] }, Is.EqualTo(new[] { "mesh", "material" }));
    }

    [Test]
    public void BinaryReader_RejectsWrongMagicFutureMajorAndEndianness()
    {
        string path = CreateSimpleFile();
        byte[] bytes = File.ReadAllBytes(path);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0xdeadbeef);
        File.WriteAllBytes(path, bytes);
        Assert.That(() => new CookedAssetReader(path), Throws.TypeOf<CookedAssetFormatException>().With.Message.Contains("magic"));

        path = CreateSimpleFile();
        bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 99);
        File.WriteAllBytes(path, bytes);
        Assert.That(() => new CookedAssetReader(path), Throws.TypeOf<CookedAssetFormatException>().With.Message.Contains("major"));

        path = CreateSimpleFile();
        bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 0x04030201);
        File.WriteAllBytes(path, bytes);
        Assert.That(() => new CookedAssetReader(path), Throws.TypeOf<CookedAssetFormatException>().With.Message.Contains("endianness"));
    }

    [Test]
    public void BinaryReader_AcceptsOlderMinorAndSkipsUnknownOptionalSection()
    {
        string path = Path.Combine(_directory, "optional.njmesh");
        using (var writer = new CookedAssetWriter(path, CookedAssetKind.Mesh))
        {
            writer.WriteSection(CookedSectionIds.FourCc("ZZZZ"), CookedSectionFlags.None, new byte[] { 1, 2, 3 });
            writer.Complete();
        }
        using var reader = new CookedAssetReader(path, CookedAssetKind.Mesh);
        Assert.That(reader.TryGetSection(CookedSectionIds.FourCc("ZZZZ"), out ReadOnlyMemory<byte> data), Is.True);
        Assert.That(data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void BinaryReader_RejectsUnknownRequiredAndCorruptPayload()
    {
        string requiredPath = Path.Combine(_directory, "required.njmesh");
        using (var writer = new CookedAssetWriter(requiredPath, CookedAssetKind.Mesh))
        {
            writer.WriteSection(CookedSectionIds.FourCc("ZZZZ"), CookedSectionFlags.Required, new byte[] { 1 });
            writer.Complete();
        }
        Assert.That(() => new CookedAssetReader(requiredPath), Throws.TypeOf<CookedAssetFormatException>().With.Message.Contains("unknown required"));

        string corruptPath = CreateSimpleFile();
        byte[] bytes = File.ReadAllBytes(corruptPath);
        bytes[64] ^= 0x01;
        File.WriteAllBytes(corruptPath, bytes);
        using var reader = new CookedAssetReader(corruptPath);
        Assert.That(() => reader.GetRequiredSection(CookedSectionIds.Metadata), Throws.TypeOf<CookedAssetHashException>());
    }

    [Test]
    public void TextureMetadataReader_RejectsCompressedBombBeforeAllocation()
    {
        string path = Path.Combine(_directory, "compressed-bomb.njtex");
        using (var writer = new CookedAssetWriter(
                   path,
                   CookedAssetKind.Texture))
        {
            writer.WriteSection(
                CookedSectionIds.Metadata,
                CookedSectionFlags.Required | CookedSectionFlags.Zstd,
                new byte[4096]);
            writer.Complete();
        }

        byte[] bytes = File.ReadAllBytes(path);
        ulong tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(56, sizeof(ulong)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(
                checked((int)tableOffset) + 24,
                sizeof(ulong)),
            CookedAssetReader.MaximumTextureMetadataSectionBytes + 1);
        File.WriteAllBytes(path, bytes);

        Assert.That(
            () => new CookedAssetReader(
                path,
                CookedAssetKind.Texture),
            Throws.TypeOf<CookedAssetFormatException>()
                .With.Message.Contains("uncompressed bytes")
                .And.Message.Contains("runtime limit"));
    }

    [Test]
    public void TextureMetadataReader_RejectsExcessiveSectionCountBeforeTableAllocation()
    {
        string path = Path.Combine(_directory, "too-many-sections.njtex");
        using (var writer = new CookedAssetWriter(
                   path,
                   CookedAssetKind.Texture))
        {
            writer.WriteSection(
                CookedSectionIds.Metadata,
                CookedSectionFlags.Required,
                new byte[] { 1 });
            writer.Complete();
        }

        byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(48, sizeof(uint)),
            CookedAssetReader.MaximumTextureMetadataSectionCount + 1);
        File.WriteAllBytes(path, bytes);

        Assert.That(
            () => new CookedAssetReader(
                path,
                CookedAssetKind.Texture),
            Throws.TypeOf<CookedAssetFormatException>()
                .With.Message.Contains("section count")
                .And.Message.Contains("runtime limit"));
    }

    [Test]
    public void TextureMetadataReader_RejectsCumulativeUncompressedBudget()
    {
        string path = Path.Combine(_directory, "cumulative-bomb.njtex");
        using (var writer = new CookedAssetWriter(
                   path,
                   CookedAssetKind.Texture))
        {
            writer.WriteSection(
                CookedSectionIds.Metadata,
                CookedSectionFlags.Required | CookedSectionFlags.Zstd,
                new byte[4096]);
            writer.WriteSection(
                CookedSectionIds.StringTable,
                CookedSectionFlags.Zstd,
                new byte[4096]);
            writer.WriteSection(
                CookedSectionIds.Bounds,
                CookedSectionFlags.Zstd,
                new byte[4096]);
            writer.Complete();
        }

        byte[] bytes = File.ReadAllBytes(path);
        ulong tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(56, sizeof(ulong)));
        for (int entry = 0; entry < 3; entry++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(
                    checked((int)tableOffset) +
                    entry * CookedSectionEntry.Size +
                    24,
                    sizeof(ulong)),
                CookedAssetReader.MaximumTextureMetadataSectionBytes);
        }
        File.WriteAllBytes(path, bytes);

        Assert.That(
            () => new CookedAssetReader(
                path,
                CookedAssetKind.Texture),
            Throws.TypeOf<CookedAssetFormatException>()
                .With.Message.Contains("cumulative uncompressed")
                .And.Message.Contains("runtime limit"));
    }

    [Test]
    public void BinaryWriter_RejectsPackagesTheRuntimeReaderCannotAdmit()
    {
        string oversizedPath = Path.Combine(
            _directory,
            "writer-oversized.njtex");
        using (var writer = new CookedAssetWriter(
                   oversizedPath,
                   CookedAssetKind.Texture))
        {
            Assert.That(
                () => writer.WriteSection(
                    CookedSectionIds.Metadata,
                    CookedSectionFlags.Zstd,
                    new byte[
                        checked((int)CookedAssetReader
                            .MaximumTextureMetadataSectionBytes + 1)]),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("runtime limit"));
        }

        string sectionCountPath = Path.Combine(
            _directory,
            "writer-section-count.njtex");
        using (var writer = new CookedAssetWriter(
                   sectionCountPath,
                   CookedAssetKind.Texture))
        {
            for (uint index = 0;
                 index <
                 CookedAssetReader.MaximumTextureMetadataSectionCount;
                 index++)
            {
                writer.WriteSection(
                    CookedSectionIds.FourCc(
                        $"A{index / 10}{index % 10}Z"),
                    CookedSectionFlags.None,
                    new byte[] { checked((byte)index) });
            }

            Assert.That(
                () => writer.WriteSection(
                    CookedSectionIds.FourCc("OVR9"),
                    CookedSectionFlags.None,
                    new byte[] { 9 }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("sections")
                    .And.Message.Contains("runtime reader limit"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(oversizedPath), Is.False);
            Assert.That(File.Exists(sectionCountPath), Is.False);
        });
    }

    [Test]
    public void OrderedHash_UsesUnambiguousLengthPrefixedTuples()
    {
        const ulong asciiHash = 0x3837363534333231UL;
        const ulong tailHash = 0x1020304050607080UL;

        ulong twoTuples = CookedHash.Ordered(
        [
            ("a", asciiHash),
            ("c", tailHash)
        ]);
        ulong formerlyAmbiguousSingleTuple = CookedHash.Ordered(
        [
            ("a12345678c", tailHash)
        ]);

        Assert.That(
            twoTuples,
            Is.Not.EqualTo(formerlyAmbiguousSingleTuple));
    }

    [Test]
    public void BinaryWriter_IsDeterministic()
    {
        string first = Path.Combine(_directory, "first.njmesh");
        string second = Path.Combine(_directory, "second.njmesh");
        WriteDeterministic(first);
        WriteDeterministic(second);
        Assert.That(File.ReadAllBytes(second), Is.EqualTo(File.ReadAllBytes(first)));
    }

    [Test]
    public void TextureCooker_WritesFullUncompressedKtx2MipChain()
    {
        // 1x1 opaque white PNG.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
        string path = Path.Combine(_directory, "white.ktx2");
        var source = new ModelTextureSource { Bytes = png, CacheIdentity = "white", DebugName = "white.png" };
        var report = new TextureCooker().Cook(source, path, new TextureCookOptions(MaxDimension: 16, ColorSpace: TextureColorSpace.Srgb, TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8));
        var info = TextureCooker.Inspect(File.ReadAllBytes(path), path);
        Assert.Multiple(() =>
        {
            Assert.That(report.PassedThrough, Is.False);
            Assert.That(info.Width, Is.EqualTo(1));
            Assert.That(info.Height, Is.EqualTo(1));
            Assert.That(info.MipCount, Is.EqualTo(1));
            Assert.That(info.Format, Is.EqualTo(43u));
            Assert.That(report.LinearAverageColor, Is.EqualTo(Vector4.One));
        });
    }

    [Test]
    public void TextureColorAverage_DecodesSrgbChannelsBeforeAveraging()
    {
        Vector4 average = TextureColorAverages.CalculateRgba8Linear(
            [128, 64, 255, 127],
            srgb: true);

        Assert.Multiple(() =>
        {
            Assert.That(average.X, Is.EqualTo(0.2158605f).Within(0.000001f));
            Assert.That(average.Y, Is.EqualTo(0.05126946f).Within(0.000001f));
            Assert.That(average.Z, Is.EqualTo(1f).Within(0.000001f));
            Assert.That(average.W, Is.EqualTo(127f / 255f).Within(0.000001f));
        });
    }

    [Test]
    public void MaterialPackage_RoundTripsDdgiLinearBaseColorTextureAverage()
    {
        string path = Path.Combine(_directory, "average.materials.njmat");
        var expected = new Vector4(0.125f, 0.25f, 0.5f, 0.75f);
        CookedPackage.WriteMaterials(
            path,
            new CookedMaterialTable(
                [
                    new ModelMaterial
                    {
                        DdgiBaseColorTextureAverageLinear = expected,
                        FeatureFlags = 1u << 24,
                        Ior = 2.25f
                    }
                ]),
            sourceHash: 1,
            settingsHash: 2,
            dependencyHash: 3);

        CookedMaterialTable loaded = CookedPackage.LoadMaterials(
            path,
            CookedAssetReaderFlags.None,
            out _);

        ModelMaterial material = loaded.Materials.Single();
        Assert.Multiple(() =>
        {
            Assert.That(material.DdgiBaseColorTextureAverageLinear, Is.EqualTo(expected));
            Assert.That(material.FeatureFlags, Is.EqualTo(1u << 24));
            Assert.That(material.Ior, Is.EqualTo(2.25f));
            Assert.That(material.TransmissionFactor, Is.Zero);
        });
    }

    [Test]
    public void MeshPackage_RoundTripsRangesAndBulkStreams()
    {
        string path = Path.Combine(_directory, "triangle.njmesh");
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var record = new CookedSubMeshRecord(
            "Triangle", 0, -1, -1, Matrix4x4.Identity,
            0, 3, 0, 3, 0, 0, 0, 1, 0, 3, 0, 3,
            [new ProcessedMeshLodRange(0, 0, 1, 1), new ProcessedMeshLodRange(1, 1, 1, 0.35f), new ProcessedMeshLodRange(2, 2, 1, 0.12f)],
            [new ProcessedMeshDrawRange("Triangle", 0, 0, 3, 0)],
            bounds, BoundingSphere.FromBox(bounds), (uint)ProcessedVertexAttribute.Position)
        {
            MeshletLod1Offset = 0,
            MeshletLod1Count = 1,
            MeshletLod2Offset = 0,
            MeshletLod2Count = 1
        };
        var payload = new CookedMeshPayload(
            [record],
            [new(), new(), new()],
            [new(), new(), new()],
            [new(), new(), new()],
            [],
            [0u, 1u, 2u],
            [new Meshlet(Vector3.Zero, 1, 0, 3, 0, 3, 0, 3, 0, 1)],
            [new Meshlet(Vector3.Zero, 1, 0, 3, 0, 3, 0, 3, 0, 1)],
            [new Meshlet(Vector3.Zero, 1, 0, 3, 0, 3, 0, 3, 0, 1)],
            [0u, 1u, 2u], [0u, 1u, 2u]);
        CookedPackage.WriteMesh(path, payload, 1, 2, 3);
        CookedMeshPayload loaded = CookedPackage.LoadMesh(path, CookedAssetReaderFlags.None, out long bytesRead);
        Assert.Multiple(() =>
        {
            Assert.That(loaded.SubMeshes.Single().Name, Is.EqualTo("Triangle"));
            Assert.That(loaded.VertexPositions, Has.Length.EqualTo(3));
            Assert.That(loaded.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
            Assert.That(loaded.MeshletsLod0, Has.Length.EqualTo(1));
            Assert.That(bytesRead, Is.GreaterThan(0));
        });
    }

    [Test]
    public void MeshPackage_UsesPortableCompressionWhenMeshOptimizerIsUnavailable()
    {
        string path = Path.Combine(_directory, "portable.njmesh");
        CookedPackage.WriteMesh(path, CreateTrianglePayload(), 1, 2, 3, useMeshOptimizer: false);

        using var reader = new CookedAssetReader(path, CookedAssetKind.Mesh);
        Assert.Multiple(() =>
        {
            Assert.That(reader.Sections.Any(section => section.Compression is CookedCompression.MeshoptVertex or CookedCompression.MeshoptIndexSequence), Is.False);
            Assert.That(reader.Sections.Any(section => section.Compression == CookedCompression.Zstd), Is.True);
        });
    }

    [Test]
    [Category("AssetIntegration")]
    public void ModelCooker_CooksAndIncrementallyReloadsAnimatedGlbPackage()
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        string source = Path.Combine(repositoryRoot, "NjulfHelloGame", "Strut.glb");
        Assert.That(File.Exists(source), Is.True, $"Expected integration fixture at {source}");
        var options = new ModelCookOptions
        {
            ImporterOptions = new ImporterOptions { Backend = ModelImportBackend.SharpGltf },
            TextureOptions = new TextureCookOptions(MaxDimension: 64)
        };
        using var cooker = new ModelAssetCooker();
        AssetCookResult first = cooker.CookModel(source, _directory, options);
        string packagePath = Path.Combine(CookedPlatform.ResolveOutputRoot(_directory, options.Platform), "models", "Strut.njmodel");
        var stopwatch = Stopwatch.StartNew();
        using (var importer = new ModelImporter())
            _ = importer.Import(source, options.ImporterOptions);
        double sourceLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        CookedModelAsset loaded = CookedPackage.LoadModel(packagePath);
        double cookedLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        AssetCookResult second = cooker.CookModel(source, _directory, options);

        Assert.Multiple(() =>
        {
            Assert.That(first.Skipped, Is.False);
            Assert.That(second.Skipped, Is.True);
            Assert.That(loaded.Mesh.SubMeshes, Has.Count.EqualTo(first.Report.SubMeshCount));
            Assert.That(loaded.Mesh.VertexPositions, Has.Length.EqualTo(first.Report.VertexCount));
            Assert.That(loaded.Mesh.MeshletsLod0, Has.Length.EqualTo(first.Report.MeshletCount));
            Assert.That(loaded.Mesh.MeshletsLod1, Has.Length.EqualTo(first.Report.MeshletLod1Count));
            Assert.That(loaded.Mesh.MeshletsLod2, Has.Length.EqualTo(first.Report.MeshletLod2Count));
            Assert.That(first.Report.MeshletLod1Count, Is.GreaterThan(0));
            Assert.That(first.Report.MeshletLod2Count, Is.GreaterThan(0));
            Assert.That(loaded.Animation.Skeletons, Has.Count.GreaterThan(0));
            Assert.That(loaded.Animation.Skins, Has.Count.GreaterThan(0));
            Assert.That(loaded.Animation.AnimationClips, Has.Count.GreaterThan(0));
            Assert.That(loaded.BytesRead, Is.GreaterThan(0));
            Assert.That(
                cookedLoadMilliseconds,
                Is.LessThan(sourceLoadMilliseconds * 0.5),
                $"Cooked CPU load must be at least 50% faster than source import (source={sourceLoadMilliseconds:F2}ms, cooked={cookedLoadMilliseconds:F2}ms).");
        });
    }

    [Test]
    [Category("AssetIntegration")]
    public void SampleSponzaCookedPackage_LoadsCanonicalMaterialsStrictly()
    {
        string repositoryRoot = Path.GetFullPath(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                ".."));
        string packagePath = Path.Combine(
            repositoryRoot,
            "NjulfHelloGame",
            "Cooked",
            CookedPlatform.Current,
            "models",
            "NewSponza_Main_glTF_003.njmodel");
        Assert.That(
            File.Exists(packagePath),
            Is.True,
            $"Expected cooked Sponza fixture at {packagePath}");

        CookedModelAsset loaded = CookedPackage.LoadModel(
            packagePath,
            CookedAssetReaderFlags.StrictSourceHash);
        string copiedPackagePath = Path.Combine(
            AppContext.BaseDirectory,
            "Cooked",
            CookedPlatform.Current,
            "models",
            "NewSponza_Main_glTF_003.njmodel");
        Assert.That(
            File.Exists(copiedPackagePath),
            Is.True,
            $"Expected copied cooked Sponza fixture at {copiedPackagePath}");
        CookedModelAsset copied = CookedPackage.LoadModel(
            copiedPackagePath,
            CookedAssetReaderFlags.StrictSourceHash);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Materials.Materials, Is.Not.Empty);
            Assert.That(
                loaded.Materials.Materials,
                Has.All.Matches<ModelMaterial>(
                    material =>
                        float.IsFinite(material.AttenuationDistance) ||
                        float.IsPositiveInfinity(
                            material.AttenuationDistance)));
            Assert.That(
                loaded.Materials.Materials.Any(
                    material =>
                        float.IsPositiveInfinity(
                            material.AttenuationDistance)),
                Is.True);
            Assert.That(
                loaded.Materials.Materials,
                Has.Some.Matches<ModelMaterial>(
                    material =>
                        string.Equals(
                            material.Name,
                            "dirt_decal",
                            StringComparison.OrdinalIgnoreCase) &&
                        material.AlphaMode == ModelAlphaMode.Blend &&
                        material.IsGeometryDecal));
            Assert.That(
                loaded.Materials.PrimitiveTransportProfiles,
                Has.Count.EqualTo(loaded.Mesh.SubMeshes.Count));
            Assert.That(
                copied.Manifest.Material,
                Is.EqualTo(loaded.Manifest.Material));
            Assert.That(
                copied.Materials.Materials.Count,
                Is.EqualTo(loaded.Materials.Materials.Count));
        });
    }

    [Test]
    public void ContentManager_RoutesModelDirectlyToCookedUploadService()
    {
        string sourcePath = Path.Combine(_directory, "triangle.gltf");
        File.WriteAllText(sourcePath, "{}");
        string modelDirectory = Path.Combine(_directory, "Cooked", "models");
        string materialDirectory = Path.Combine(_directory, "Cooked", "materials");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(materialDirectory);
        string meshPath = Path.Combine(modelDirectory, "triangle.meshes.njmesh");
        string materialPath = Path.Combine(materialDirectory, "triangle.materials.njmat");
        string modelPath = Path.Combine(modelDirectory, "triangle.njmodel");
        CookedMeshPayload mesh = CreateTrianglePayload();
        ulong sourceHash = CookedHash.File(sourcePath);
        CookedPackage.WriteMesh(meshPath, mesh, sourceHash, 1, 2);
        CookedPackage.WriteMaterials(materialPath, new CookedMaterialTable([ModelMaterial.Default]), sourceHash, 1, 2);
        var manifest = new CookedModelManifest(
            CookedPackage.StableAssetId(sourcePath), "Triangle", sourcePath, sourceHash, 1, 2,
            new CookedAssetReference("triangle.meshes.njmesh", CookedHash.File(meshPath)),
            new CookedAssetReference("../materials/triangle.materials.njmat", CookedHash.File(materialPath)),
            null,
            [new CookedModelSubObject("Triangle", 0, 0, -1, -1, Matrix4x4.Identity)],
            mesh.SubMeshes[0].BoundingBox,
            mesh.SubMeshes[0].BoundingSphere);
        CookedPackage.WriteModel(modelPath, manifest);

        var upload = new FakeCookedUploadService();
        using var content = new ContentManager(_directory, upload);
        Model result = content.Load<Model>("triangle.gltf");
        Assert.Multiple(() =>
        {
            Assert.That(result.Name, Is.EqualTo("Triangle"));
            Assert.That(upload.CookedUploadCount, Is.EqualTo(1));
            Assert.That(upload.SourceUploadCount, Is.Zero);
            Assert.That(content.CookedDiagnostics.CookedAssetCount, Is.EqualTo(1));
            Assert.That(content.CookedDiagnostics.SourceFallbackCount, Is.Zero);
        });
    }

    [Test]
    public void ContentManager_LoadsExplicitCookedModelWithoutTreatingPackageAsSource()
    {
        string sourcePath = Path.Combine(_directory, "explicit-source.gltf");
        File.WriteAllText(sourcePath, "{}");
        string modelDirectory = Path.Combine(_directory, "Cooked", "models");
        string materialDirectory = Path.Combine(_directory, "Cooked", "materials");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(materialDirectory);
        string meshPath = Path.Combine(modelDirectory, "explicit.meshes.njmesh");
        string materialPath = Path.Combine(materialDirectory, "explicit.materials.njmat");
        string modelPath = Path.Combine(modelDirectory, "explicit.njmodel");
        CookedMeshPayload mesh = CreateTrianglePayload();
        ulong sourceHash = CookedHash.File(sourcePath);
        CookedPackage.WriteMesh(meshPath, mesh, sourceHash, 1, 2);
        CookedPackage.WriteMaterials(
            materialPath,
            new CookedMaterialTable([ModelMaterial.Default]),
            sourceHash,
            1,
            2);
        var manifest = new CookedModelManifest(
            CookedPackage.StableAssetId(sourcePath),
            "Explicit",
            sourcePath,
            sourceHash,
            1,
            2,
            new CookedAssetReference("explicit.meshes.njmesh", CookedHash.File(meshPath)),
            new CookedAssetReference("../materials/explicit.materials.njmat", CookedHash.File(materialPath)),
            null,
            [new CookedModelSubObject("Explicit", 0, 0, -1, -1, Matrix4x4.Identity)],
            mesh.SubMeshes[0].BoundingBox,
            mesh.SubMeshes[0].BoundingSphere);
        CookedPackage.WriteModel(modelPath, manifest);

        var upload = new FakeCookedUploadService();
        using var content = new ContentManager(_directory, upload);
        Model result = content.Load<Model>(modelPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Name, Is.EqualTo("Explicit"));
            Assert.That(upload.CookedUploadCount, Is.EqualTo(1));
            Assert.That(upload.SourceUploadCount, Is.Zero);
            Assert.That(content.CookedDiagnostics.CookedAssetCount, Is.EqualTo(1));
            Assert.That(
                content.CookedDiagnostics.Entries.Single().Reason,
                Is.EqualTo("cooked package was explicitly requested"));
        });
    }

    [Test]
    public void ContentManager_ModelSnapshotBindsValidationIdentityAndUploadAcrossPackageReplacement()
    {
        string sourcePath = Path.Combine(_directory, "snapshot-source.gltf");
        File.WriteAllText(sourcePath, "{}");
        string modelDirectory = Path.Combine(_directory, "Cooked", "models");
        string materialDirectory = Path.Combine(_directory, "Cooked", "materials");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(materialDirectory);
        string meshPath = Path.Combine(modelDirectory, "snapshot.meshes.njmesh");
        string materialPath = Path.Combine(materialDirectory, "snapshot.materials.njmat");
        string modelPath = Path.Combine(modelDirectory, "snapshot.njmodel");
        CookedMeshPayload mesh = CreateTrianglePayload();
        ulong sourceHash = CookedHash.File(sourcePath);
        CookedPackage.WriteMesh(meshPath, mesh, sourceHash, 1, 2);
        CookedPackage.WriteMaterials(
            materialPath,
            new CookedMaterialTable([ModelMaterial.Default]),
            sourceHash,
            1,
            2);
        var originalManifest = new CookedModelManifest(
            CookedPackage.StableAssetId(sourcePath),
            "OriginalSnapshot",
            sourcePath,
            sourceHash,
            1,
            2,
            new CookedAssetReference(
                "snapshot.meshes.njmesh",
                CookedHash.File(meshPath)),
            new CookedAssetReference(
                "../materials/snapshot.materials.njmat",
                CookedHash.File(materialPath)),
            null,
            [new CookedModelSubObject(
                "OriginalSnapshot",
                0,
                0,
                -1,
                -1,
                Matrix4x4.Identity)],
            mesh.SubMeshes[0].BoundingBox,
            mesh.SubMeshes[0].BoundingSphere);
        CookedPackage.WriteModel(modelPath, originalManifest);
        CookedModelPackageSnapshot originalSnapshot =
            CookedPackage.CaptureModelSnapshot(modelPath);

        var upload = new FakeCookedUploadService();
        using var content = new ContentManager(_directory, upload);
        CookedModelSnapshotLoadResult loaded =
            content.LoadCookedModelSnapshot(
                originalSnapshot,
                decoded =>
                {
                    Assert.That(
                        decoded.Manifest.Name,
                        Is.EqualTo("OriginalSnapshot"));
                    CookedPackage.WriteModel(
                        modelPath,
                        originalManifest with { Name = "ReplacementOnDisk" });
                });
        CookedModelPackageSnapshot replacementSnapshot =
            CookedPackage.CaptureModelSnapshot(modelPath);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Snapshot, Is.SameAs(originalSnapshot));
            Assert.That(
                loaded.CookedAsset,
                Is.SameAs(upload.LastCookedModel));
            Assert.That(loaded.CookedAsset.Manifest.Name, Is.EqualTo("OriginalSnapshot"));
            Assert.That(loaded.RuntimeModel.Name, Is.EqualTo("OriginalSnapshot"));
            Assert.That(originalSnapshot.ByteLength, Is.GreaterThan(0));
            Assert.That(originalSnapshot.Sha256, Has.Length.EqualTo(64));
            Assert.That(
                replacementSnapshot.Sha256,
                Is.Not.EqualTo(originalSnapshot.Sha256));
            Assert.That(upload.CookedUploadCount, Is.EqualTo(1));
            Assert.That(content.CookedDiagnostics.CookedAssetCount, Is.EqualTo(1));
            Assert.That(
                content.CookedDiagnostics.Entries.Single().Reason,
                Does.Contain("one immutable snapshot"));
        });
    }

    [Test]
    public void RendererMeshletLodBuilder_GeneratesThreeProgressivelySimplifiedLevels()
    {
        const int size = 12;
        var vertices = new Vector3[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                vertices[y * size + x] = new Vector3(x, y, MathF.Sin(x * 0.3f) * MathF.Cos(y * 0.2f));
        var indices = new List<uint>();
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                uint a = (uint)(y * size + x);
                uint b = a + 1;
                uint c = a + size;
                uint d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }

        RendererMeshletLodBuild result = new RendererMeshletLodBuilder().Build(vertices, indices.ToArray(), "Grid");
        Assert.Multiple(() =>
        {
            Assert.That(result.Ranges, Has.Count.EqualTo(3));
            Assert.That(result.Ranges.All(range => range.MeshletCount > 0), Is.True);
            Assert.That(result.IndexCounts[1], Is.LessThan(result.IndexCounts[0]));
            Assert.That(result.IndexCounts[2], Is.LessThan(result.IndexCounts[1]));
            Assert.That(result.Meshlets, Has.Length.EqualTo(result.Ranges.Sum(range => range.MeshletCount)));
        });
    }

    [Test]
    public void RendererMeshletLodBuilder_PreservesSmallClosedMeshTopologyAtEveryLod()
    {
        Vector3[] vertices =
        [
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
            new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)
        ];
        uint[] indices =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7
        ];

        RendererMeshletLodBuild result = new RendererMeshletLodBuilder().Build(vertices, indices, "Cube");

        Assert.Multiple(() =>
        {
            Assert.That(result.IndexCounts, Is.EqualTo(new[] { indices.Length, indices.Length, indices.Length }));
            Assert.That(result.SimplificationErrors, Is.EqualTo(new[] { 0f, 0f, 0f }));
        });
    }

    [Test]
    public void BinaryReader_RoundTripsZstdLz4AndMeshoptSections()
    {
        string path = Path.Combine(_directory, "compressed.njmesh");
        byte[] metadata = Enumerable.Repeat((byte)0x5a, 16 * 1024).ToArray();
        byte[] strings = Enumerable.Range(0, 8192).Select(i => (byte)(i % 7)).ToArray();
        var vertices = Enumerable.Range(0, 1024).Select(i => new Vector4(i * 0.01f, i % 5, 0, 1)).ToArray();
        uint[] indices = Enumerable.Range(0, 3072).Select(i => (uint)(i % 1024)).ToArray();
        using (var writer = new CookedAssetWriter(path, CookedAssetKind.Mesh))
        {
            writer.WriteSection(CookedSectionIds.Metadata, CookedSectionFlags.Zstd, metadata);
            writer.WriteSection(CookedSectionIds.StringTable, CookedSectionFlags.Lz4, strings);
            writer.WriteMeshoptVertexSection(CookedSectionIds.VertexPositions, CookedSectionFlags.Required, vertices);
            writer.WriteMeshoptIndexSequenceSection(CookedSectionIds.Indices, CookedSectionFlags.Required, indices, 1024);
            writer.Complete();
        }
        using var reader = new CookedAssetReader(path, CookedAssetKind.Mesh, CookedAssetReaderFlags.PreferMemoryMapped);
        Assert.Multiple(() =>
        {
            Assert.That(reader.GetRequiredSection(CookedSectionIds.Metadata).ToArray(), Is.EqualTo(metadata));
            Assert.That(reader.GetRequiredSection(CookedSectionIds.StringTable).ToArray(), Is.EqualTo(strings));
            Assert.That(reader.ReadSection<Vector4>(CookedSectionIds.VertexPositions), Is.EqualTo(vertices));
            Assert.That(reader.ReadSection<uint>(CookedSectionIds.Indices), Is.EqualTo(indices));
            Assert.That(reader.Sections.Any(section => section.Compression == CookedCompression.Zstd), Is.True);
            Assert.That(reader.Sections.Any(section => section.Compression == CookedCompression.Lz4), Is.True);
            Assert.That(reader.Sections.Any(section => section.Compression == CookedCompression.MeshoptVertex), Is.True);
        });
    }

    [Test]
    public void TextureCooker_WritesBc4Bc5Bc6AndBc7Ktx2()
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
        var source = new ModelTextureSource { Bytes = png, CacheIdentity = "formats", DebugName = "formats.png" };
        var cases = new[]
        {
            (TextureTargetFormatPolicy.Bc4, TextureColorSpace.Linear, 139u),
            (TextureTargetFormatPolicy.Bc5, TextureColorSpace.Linear, 141u),
            (TextureTargetFormatPolicy.Bc6H, TextureColorSpace.HdrLinear, 143u),
            (TextureTargetFormatPolicy.Bc7, TextureColorSpace.Srgb, 146u)
        };
        foreach ((TextureTargetFormatPolicy policy, TextureColorSpace colorSpace, uint format) in cases)
        {
            string path = Path.Combine(_directory, policy + ".ktx2");
            _ = new TextureCooker().Cook(source, path, new TextureCookOptions(16, colorSpace, TargetFormatPolicy: policy));
            Assert.That(TextureCooker.Inspect(File.ReadAllBytes(path), path).Format, Is.EqualTo(format), policy.ToString());
        }
    }

    [Test]
    public void DetachedSignature_VerifiesAndRejectsTampering()
    {
        string privateKey = Path.Combine(_directory, "private.pem");
        string publicKey = Path.Combine(_directory, "public.pem");
        string asset = CreateSimpleFile();
        CookedPackageSigner.GenerateKeyPair(privateKey, publicKey);
        _ = CookedPackageSigner.SignFile(asset, privateKey);
        Assert.That(() => CookedPackageSigner.VerifyRequired(asset, publicKey), Throws.Nothing);
        using (var stream = new FileStream(asset, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 64;
            stream.WriteByte((byte)(stream.ReadByte() ^ 0x01));
        }
        Assert.That(() => CookedPackageSigner.VerifyRequired(asset, publicKey), Throws.TypeOf<CookedAssetHashException>());
    }

    [Test]
    public void DetachedSignature_RejectsOversizedEnvelopeBeforeAllocation()
    {
        string publicKey = Path.Combine(_directory, "public.pem");
        string privateKey = Path.Combine(_directory, "private.pem");
        string asset = CreateSimpleFile();
        CookedPackageSigner.GenerateKeyPair(privateKey, publicKey);
        string signaturePath = CookedPackageSigner.SignFile(asset, privateKey);
        using (var stream = new FileStream(
                   signaturePath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(
                CookedPackageSigner.MaximumDetachedSignatureBytes + 1L);
        }

        Assert.That(
            () => CookedPackageSigner.VerifyRequired(asset, publicKey),
            Throws.TypeOf<CookedAssetHashException>()
                .With.Message.Contains("runtime limit"));
    }

    [Test]
    public void Migrator_RewritesOlderMinorToCurrentFormat()
    {
        string source = Path.Combine(_directory, "old");
        string output = Path.Combine(_directory, "new");
        Directory.CreateDirectory(source);
        string oldFile = Path.Combine(source, "texture.njtex");
        using (var writer = new CookedAssetWriter(oldFile, CookedAssetKind.Texture))
        {
            writer.WriteSection(CookedSectionIds.Metadata, CookedSectionFlags.Required, Enumerable.Repeat((byte)7, 4096).ToArray());
            writer.Complete();
        }
        byte[] bytes = File.ReadAllBytes(oldFile);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 0);
        File.WriteAllBytes(oldFile, bytes);
        CookedMigrationReport report = CookedAssetMigrator.MigrateTree(source, output);
        using var migrated = new CookedAssetReader(Path.Combine(output, "texture.njtex"), CookedAssetKind.Texture);
        Assert.Multiple(() =>
        {
            Assert.That(report.MigratedFiles, Is.EqualTo(1));
            Assert.That(migrated.Header.FormatMinor, Is.EqualTo(CookedFormatVersions.Texture.Minor));
            Assert.That(migrated.Sections.Single().Compression, Is.EqualTo(CookedCompression.Zstd));
        });
    }

    private string CreateSimpleFile()
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".njtex");
        using (var writer = new CookedAssetWriter(path, CookedAssetKind.Texture))
        {
            writer.WriteSection(CookedSectionIds.Metadata, CookedSectionFlags.Required, new byte[] { 1, 2, 3, 4 });
            writer.Complete();
        }
        return path;
    }

    private static void WriteDeterministic(string path)
    {
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Mesh, 1, 2, 3, 4);
        writer.WriteSection(CookedSectionIds.Metadata, CookedSectionFlags.Required, new byte[] { 4, 3, 2, 1 });
        writer.WriteSection(CookedSectionIds.Indices, CookedSectionFlags.Required, new uint[] { 0, 1, 2 });
        writer.Complete();
    }

    private static CookedMeshPayload CreateTrianglePayload()
    {
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var record = new CookedSubMeshRecord(
            "Triangle", 0, -1, -1, Matrix4x4.Identity,
            0, 3, 0, 3, 0, 0, 0, 1, 0, 3, 0, 3,
            [new ProcessedMeshLodRange(0, 0, 1, 1)],
            [new ProcessedMeshDrawRange("Triangle", 0, 0, 3, 0)],
            bounds, BoundingSphere.FromBox(bounds), (uint)ProcessedVertexAttribute.Position);
        return new CookedMeshPayload(
            [record], [new(), new(), new()], [new(), new(), new()], [new(), new(), new()], [],
            [0u, 1u, 2u], [new Meshlet(Vector3.Zero, 1, 0, 3, 0, 3, 0, 3, 0, 1)],
            [], [], [0u, 1u, 2u], [0u, 1u, 2u]);
    }

    private sealed class FakeCookedUploadService : IModelRenderUploadService
    {
        public int SourceUploadCount { get; private set; }
        public int CookedUploadCount { get; private set; }
        public CookedModelAsset? LastCookedModel { get; private set; }
        public ModelRenderUploadDiagnostics LastUploadDiagnostics { get; } = new("", 0, 0, 0, 0, 0, 0, 0, 0);
        public Model UploadModel(ModelMesh modelMesh)
        {
            SourceUploadCount++;
            return new Model { Name = modelMesh.Name };
        }
        public Model UploadCookedModel(CookedModelAsset model)
        {
            CookedUploadCount++;
            LastCookedModel = model;
            return new Model { Name = model.Manifest.Name };
        }
    }
}
