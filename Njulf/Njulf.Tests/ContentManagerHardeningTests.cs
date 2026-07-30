using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ContentManagerHardeningTests
{
    private string _directory = null!;
    private string? _previousRequireSignature;
    private string? _previousStrict;

    [SetUp]
    public void SetUp()
    {
        _previousRequireSignature = Environment.GetEnvironmentVariable(
            CookedRuntimePolicy.RequireSignatureVariable);
        _previousStrict = Environment.GetEnvironmentVariable(
            CookedRuntimePolicy.StrictVariable);
        Environment.SetEnvironmentVariable(
            CookedRuntimePolicy.RequireSignatureVariable,
            "false");
        Environment.SetEnvironmentVariable(
            CookedRuntimePolicy.StrictVariable,
            "true");
        _directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfContentManagerHardeningTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(
            CookedRuntimePolicy.RequireSignatureVariable,
            _previousRequireSignature);
        Environment.SetEnvironmentVariable(
            CookedRuntimePolicy.StrictVariable,
            _previousStrict);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task ConcurrentCacheMisses_PublishOneUploadedOwner()
    {
        TestCookedModel fixture = WriteCookedModel(
            "Concurrent",
            "concurrent");
        var uploader = new RecordingUploadService(
            uploadDelay: TimeSpan.FromMilliseconds(75));
        using var content = new ContentManager(_directory, uploader);
        using var start = new ManualResetEventSlim(initialState: false);

        Task<Model>[] loads = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(
                () =>
                {
                    start.Wait();
                    return content.Load<Model>(fixture.ModelPath);
                }))
            .ToArray();
        start.Set();
        Model[] models = await Task.WhenAll(loads)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Multiple(() =>
        {
            Assert.That(
                models,
                Is.All.SameAs(models[0]));
            Assert.That(uploader.CookedUploadCount, Is.EqualTo(1));
            Assert.That(
                content.CookedDiagnostics.CookedAssetCount,
                Is.EqualTo(1));
        });
    }

    [Test]
    public void CookedReplacementAfterSnapshot_CannotPublishReplacementUnderOriginalIdentity()
    {
        TestCookedModel fixture = WriteCookedModel(
            "Original",
            "replacement");
        CookedModelManifest replacement =
            fixture.Manifest with { Name = "Replacement" };
        int captureCount = 0;
        CookedModelPackageSnapshot CaptureAndReplace(string path)
        {
            CookedModelPackageSnapshot snapshot =
                CookedPackage.CaptureModelSnapshot(path);
            if (Interlocked.Increment(ref captureCount) == 1)
                CookedPackage.WriteModel(path, replacement);
            return snapshot;
        }

        var uploader = new RecordingUploadService();
        using var content = new ContentManager(
            _directory,
            uploader,
            CaptureAndReplace);

        Model original = content.Load<Model>(fixture.ModelPath);
        Model replaced = content.Load<Model>(fixture.ModelPath);
        Model replacedAgain = content.Load<Model>(fixture.ModelPath);

        Assert.Multiple(() =>
        {
            Assert.That(original.Name, Is.EqualTo("Original"));
            Assert.That(replaced.Name, Is.EqualTo("Replacement"));
            Assert.That(replaced, Is.Not.SameAs(original));
            Assert.That(replacedAgain, Is.SameAs(replaced));
            Assert.That(uploader.CookedUploadCount, Is.EqualTo(2));
            Assert.That(
                content.CookedDiagnostics.CookedAssetCount,
                Is.EqualTo(2));
        });
    }

    [Test]
    public void UnloadFailure_RetainsCacheOwnershipForRetry()
    {
        TestCookedModel fixture = WriteCookedModel(
            "Retryable",
            "retryable");
        var release = new RetryableRelease();
        var uploader = new RecordingUploadService(
            configureModel: model =>
                model.AddDisposeAction(release.Release));
        using var content = new ContentManager(_directory, uploader);
        Model original = content.Load<Model>(fixture.ModelPath);

        Assert.That(
            () => content.Unload(original),
            Throws.TypeOf<AggregateException>()
                .With.Message.Contains("could not be disposed"));

        Model retained = content.Load<Model>(fixture.ModelPath);
        Assert.Multiple(() =>
        {
            Assert.That(retained, Is.SameAs(original));
            Assert.That(uploader.CookedUploadCount, Is.EqualTo(1));
            Assert.That(release.Attempts, Is.EqualTo(1));
        });

        Assert.DoesNotThrow(() => content.Unload(original));
        Model replacement = content.Load<Model>(fixture.ModelPath);

        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.SameAs(original));
            Assert.That(uploader.CookedUploadCount, Is.EqualTo(2));
            Assert.That(release.Attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public void PublicOperationsAfterDispose_FailWithObjectDisposedException()
    {
        TestCookedModel fixture = WriteCookedModel(
            "Disposed",
            "disposed");
        CookedModelPackageSnapshot snapshot =
            CookedPackage.CaptureModelSnapshot(fixture.ModelPath);
        var uploader = new RecordingUploadService();
        var content = new ContentManager(_directory, uploader);
        content.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => content.Load<Model>(fixture.ModelPath),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => content.Load<Model>(
                    fixture.ModelPath,
                    ContentLoadOptions.Default),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => content.LoadCookedModelSnapshot(snapshot),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => content.Unload(new Model()),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => content.Clear(),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => _ = content.CookedDiagnostics,
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => content.Dispose(),
                Throws.Nothing);
            Assert.That(uploader.CookedUploadCount, Is.Zero);
        });
    }

    private TestCookedModel WriteCookedModel(
        string modelName,
        string stem)
    {
        string sourcePath = Path.Combine(
            _directory,
            $"{stem}.gltf");
        File.WriteAllText(sourcePath, "{}");
        string modelDirectory = Path.Combine(
            _directory,
            "Cooked",
            "models");
        string materialDirectory = Path.Combine(
            _directory,
            "Cooked",
            "materials");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(materialDirectory);

        string meshPath = Path.Combine(
            modelDirectory,
            $"{stem}.meshes.njmesh");
        string materialPath = Path.Combine(
            materialDirectory,
            $"{stem}.materials.njmat");
        string modelPath = Path.Combine(
            modelDirectory,
            $"{stem}.njmodel");
        CookedMeshPayload mesh = CreateTrianglePayload();
        ulong sourceHash = CookedHash.File(sourcePath);
        CookedPackage.WriteMesh(
            meshPath,
            mesh,
            sourceHash,
            settingsHash: 1,
            dependencyHash: 2);
        CookedPackage.WriteMaterials(
            materialPath,
            new CookedMaterialTable([ModelMaterial.Default]),
            sourceHash,
            settingsHash: 1,
            dependencyHash: 2);
        var manifest = new CookedModelManifest(
            CookedPackage.StableAssetId(sourcePath),
            modelName,
            sourcePath,
            sourceHash,
            1,
            2,
            new CookedAssetReference(
                Path.GetFileName(meshPath),
                CookedHash.File(meshPath)),
            new CookedAssetReference(
                $"../materials/{Path.GetFileName(materialPath)}",
                CookedHash.File(materialPath)),
            Animation: null,
            [
                new CookedModelSubObject(
                    modelName,
                    SubMeshIndex: 0,
                    MaterialSlot: 0,
                    NodeIndex: -1,
                    SkinIndex: -1,
                    SkinningBindTransform: Matrix4x4.Identity)
            ],
            mesh.SubMeshes[0].BoundingBox,
            mesh.SubMeshes[0].BoundingSphere);
        CookedPackage.WriteModel(modelPath, manifest);
        return new TestCookedModel(modelPath, manifest);
    }

    private static CookedMeshPayload CreateTrianglePayload()
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
            MeshletVertexCount: 3,
            MeshletTriangleOffset: 0,
            MeshletTriangleCount: 3,
            [new ProcessedMeshLodRange(0, 0, 1, 1)],
            [new ProcessedMeshDrawRange("Triangle", 0, 0, 3, 0)],
            bounds,
            BoundingSphere.FromBox(bounds),
            (uint)ProcessedVertexAttribute.Position);
        return new CookedMeshPayload(
            [subMesh],
            [new(), new(), new()],
            [new(), new(), new()],
            [new(), new(), new()],
            [],
            [0u, 1u, 2u],
            [
                new Meshlet(
                    Vector3.Zero,
                    boundingSphereRadius: 1,
                    vertexOffset: 0,
                    vertexCount: 3,
                    indexOffset: 0,
                    indexCount: 3,
                    localVertexOffset: 0,
                    localVertexCount: 3,
                    localTriangleOffset: 0,
                    localTriangleCount: 1)
            ],
            [],
            [],
            [0u, 1u, 2u],
            [0u, 1u, 2u]);
    }

    private sealed record TestCookedModel(
        string ModelPath,
        CookedModelManifest Manifest);

    private sealed class RetryableRelease
    {
        public int Attempts { get; private set; }

        public void Release()
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic retryable release failure.");
            }
        }
    }

    private sealed class RecordingUploadService(
        TimeSpan? uploadDelay = null,
        Action<Model>? configureModel = null)
        : IModelRenderUploadService
    {
        private int _cookedUploadCount;

        public int CookedUploadCount =>
            Volatile.Read(ref _cookedUploadCount);

        public ModelRenderUploadDiagnostics LastUploadDiagnostics { get; } =
            new(string.Empty, 0, 0, 0, 0, 0, 0, 0, 0);

        public Model UploadModel(ModelMesh modelMesh) =>
            throw new AssertionException(
                "The cooked test must not use source upload.");

        public Model UploadCookedModel(CookedModelAsset model)
        {
            Interlocked.Increment(ref _cookedUploadCount);
            if (uploadDelay.HasValue)
                Thread.Sleep(uploadDelay.Value);
            var runtimeModel = new Model
            {
                Name = model.Manifest.Name
            };
            configureModel?.Invoke(runtimeModel);
            return runtimeModel;
        }
    }
}
