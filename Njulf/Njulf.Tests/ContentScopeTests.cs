using System.Diagnostics;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class ContentScopeTests
{
    private string _directory = null!;
    private string? _sourcePolicy;
    private TestDevice _device = null!;
    private TestUploader _uploader = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "content-scopes", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        File.WriteAllText(Path.Combine(_directory, "triangle.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        _sourcePolicy = Environment.GetEnvironmentVariable(CookedRuntimePolicy.AllowSourceFallbackVariable);
        Environment.SetEnvironmentVariable(CookedRuntimePolicy.AllowSourceFallbackVariable, "true");
        _device = new TestDevice();
        _uploader = new TestUploader();
    }

    [TearDown]
    public void TearDown()
    {
        _device.Dispose();
        Environment.SetEnvironmentVariable(CookedRuntimePolicy.AllowSourceFallbackVariable, _sourcePolicy);
        Directory.Delete(_directory, recursive: true);
    }

    private ContentManager Manager(IContentUploadDispatcher? dispatcher = null) => new(_directory, _uploader, dispatcher)
        { GraphicsDeviceProvider = () => _device };

    [Test]
    public void ScopesShareOneClaimPerAssetAndPreserveRootOwnership()
    {
        using var content = Manager();
        using var a = content.CreateScope();
        using var b = a.CreateScope();
        Texture shared = content.Load<Texture>("pixel.png");
        Assert.That(a.Load<Texture>("pixel.png"), Is.SameAs(shared));
        Assert.That(a.Load<Texture>("./pixel.png"), Is.SameAs(shared));
        Assert.That(b.Load<Texture>("pixel.png"), Is.SameAs(shared));
        a.Unload(shared);
        Assert.Throws<ArgumentException>(() => a.Unload(shared));
        a.Dispose();
        content.UnloadAll();
        Assert.That(shared.IsDisposed, Is.False);
        Assert.That(b.Load<Texture>("pixel.png"), Is.SameAs(shared));
        b.Dispose();
        Assert.That(shared.IsDisposed, Is.True);
        Assert.That(_device.Textures, Has.Count.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => a.Load<Texture>("pixel.png"));
    }

    [Test]
    public void RootCannotReleaseAnAssetOwnedOnlyByAScopeAndUnloadAllIsReusable()
    {
        using var content = Manager();
        using var scope = content.CreateScope();
        Texture first = scope.Load<Texture>("pixel.png");
        Assert.Throws<ArgumentException>(() => content.Unload(first));
        scope.UnloadAll();
        Assert.That(first.IsDisposed, Is.True);
        Texture next = scope.Load<Texture>("pixel.png");
        Assert.That(next, Is.Not.SameAs(first));
    }

    [Test]
    public void AsyncAndPreloadShareTexturesButColorSpacesDoNot()
    {
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var a = content.CreateScope();
        using var b = content.CreateScope();
        Texture[] textures = Pump(Task.WhenAll(a.LoadAsync<Texture>("pixel.png"), b.LoadAsync<Texture>("pixel.png")), dispatcher);
        Assert.That(textures[1], Is.SameAs(textures[0]));
        var preloaded = Pump(a.PreloadAsync<Texture>([new("pixel.png"), new("pixel.png")], new() { MaxConcurrency = 2 }), dispatcher);
        Assert.That(preloaded.ReadyCount, Is.EqualTo(2));
        Assert.That(preloaded.Items[0].Asset, Is.SameAs(textures[0]));
        Texture linear = a.Load<Texture>("pixel.png", new() { TextureColorSpace = TextureColorSpace.Linear });
        Assert.That(linear, Is.Not.SameAs(textures[0]));
        Assert.That(linear.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
        Assert.That(_device.Textures, Has.Count.EqualTo(2));
        Assert.That(_device.CreationThreads, Is.All.EqualTo(Environment.CurrentManagedThreadId));
    }

    [Test]
    public void CancellingOneScopeDoesNotCancelAnotherQueuedLoad()
    {
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var a = content.CreateScope();
        using var b = content.CreateScope();
        Task<Texture> cancelled = a.LoadAsync<Texture>("pixel.png");
        Task<Texture> surviving = b.LoadAsync<Texture>("pixel.png");
        Assert.That(SpinWait.SpinUntil(() => dispatcher.PendingCount >= 2, TimeSpan.FromSeconds(5)), Is.True);
        a.Dispose();
        Assert.Catch<OperationCanceledException>(() => Pump(cancelled, dispatcher));
        Texture texture = Pump(surviving, dispatcher);
        Assert.That(texture.IsDisposed, Is.False);
        Assert.That(_device.Textures, Has.Count.EqualTo(1));
        b.Dispose();
        Assert.That(texture.IsDisposed, Is.True);
    }

    [Test]
    public void ScopeUnloadedDuringCreationCannotPublishAndCanBeReused()
    {
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var scope = content.CreateScope();
        _device.AfterTextureCreated = scope.UnloadAll;
        Assert.Catch<OperationCanceledException>(() => Pump(scope.LoadAsync<Texture>("pixel.png"), dispatcher));
        Assert.That(_device.Textures.Single().IsDisposed, Is.True);
        _device.AfterTextureCreated = null;
        Assert.That(Pump(scope.LoadAsync<Texture>("pixel.png"), dispatcher).IsDisposed, Is.False);
    }

    [Test]
    public void FailedReleaseRemainsRetryableAfterScopeCloses()
    {
        using var content = Manager();
        using var scope = content.CreateScope();
        var texture = (TestTexture)scope.Load<Texture>("pixel.png");
        texture.FailRelease = true;
        Assert.Throws<AggregateException>(() => scope.Dispose());
        Assert.That(texture.IsDisposed, Is.False);
        texture.FailRelease = false;
        scope.Dispose();
        Assert.That(texture.IsDisposed, Is.True);
        Assert.That(texture.SuccessfulReleases, Is.EqualTo(1));
    }

    [Test]
    public void ShutdownDuringPartialMaterialLoadRetainsCleanupUntilManagerDisposal()
    {
        File.WriteAllText(Path.Combine(_directory, "material.njmaterial.json"), """{"baseColorTexturePath":"pixel.png"}""");
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var scope = content.CreateScope();
        Task<Material> loading = scope.LoadAsync<Material>("material.njmaterial.json");
        Assert.That(SpinWait.SpinUntil(() => dispatcher.PendingCount != 0, TimeSpan.FromSeconds(5)), Is.True);
        dispatcher.ProcessFrame(TimeSpan.FromMilliseconds(10), maximumCallbacks: 1);
        Assert.That(SpinWait.SpinUntil(() => dispatcher.PendingCount != 0, TimeSpan.FromSeconds(5)), Is.True);
        content.BeginShutdown();
        dispatcher.BeginShutdown();
        Assert.Catch<OperationCanceledException>(() => Pump(loading, dispatcher));
        Assert.That(content.ActiveOperationCount, Is.Zero);
        content.Dispose();
        Assert.That(_device.Textures.Single().IsDisposed, Is.True);
    }

    [Test]
    public void FailedUnpublishedTextureReleaseIsRetainedForManagerRetry()
    {
        using var content = Manager();
        using var scope = content.CreateScope();
        _device.AfterTextureCreated = () =>
        {
            _device.Textures.Last().FailRelease = true;
            scope.Dispose();
        };
        Assert.Throws<AggregateException>(() => scope.Load<Texture>("pixel.png"));
        TestTexture texture = _device.Textures.Single();
        Assert.That(texture.IsDisposed, Is.False);
        texture.FailRelease = false;
        content.UnloadAll();
        Assert.That(texture.IsDisposed, Is.True);
    }

    [Test]
    public void MaterialLoadsRelativeDependenciesAndRetainedObjectsSurviveUnload()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "materials"));
        File.WriteAllText(Path.Combine(_directory, "materials", "test.njmaterial.json"),
            """{"schemaVersion":1,"name":"Authored","roughness":0.3,"baseColorTexturePath":"../pixel.png","normalTexturePath":"../pixel.png"}""");
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var scope = content.CreateScope();
        Material material = Pump(scope.LoadAsync<Material>("materials/test.njmaterial.json"), dispatcher);
        Assert.That(scope.Load<Material>("materials/test.njmaterial.json"), Is.SameAs(material));
        Assert.That(material.Definition.Name, Is.EqualTo("Authored"));
        Assert.That(material.Definition.RoughnessFactor, Is.EqualTo(.3f));
        Assert.That(material.Definition.MetallicFactor, Is.Zero);
        Assert.That(material.Definition.BaseColorFactor, Is.EqualTo(Vector4.One));
        Assert.That(_device.Assignments.Select(a => a.Texture!.ColorSpace), Is.EqualTo(new[] { TextureColorSpace.Srgb, TextureColorSpace.Linear }));
        using var retainedObject = new RenderObject(null, material);
        scope.Dispose();
        Assert.That(material.IsDisposed, Is.True);
        Assert.That(retainedObject.Material!.Definition.Name, Is.EqualTo("Authored"));
        Assert.That(_device.Textures.Select(t => t.IsDisposed), Is.All.True);
    }

    [Test]
    public void FailedMaterialReleasesOnlyItsTemporaryDependencies()
    {
        File.WriteAllText(Path.Combine(_directory, "bad.njmaterial.json"),
            """{"baseColorTexturePath":"pixel.png","normalTexturePath":"missing.png"}""");
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var scope = content.CreateScope();
        Texture root = content.Load<Texture>("pixel.png");
        Assert.Throws<FileNotFoundException>(() => Pump(scope.LoadAsync<Material>("bad.njmaterial.json"), dispatcher));
        Assert.That(root.IsDisposed, Is.False);
        content.Unload(root);
        Assert.That(root.IsDisposed, Is.True);
        Assert.Throws<FileNotFoundException>(() => scope.Load<Material>("bad.njmaterial.json"));
        Assert.That(_device.Textures.Last().IsDisposed, Is.True);
    }

    [Test]
    public void DependencyReleaseFailureDoesNotReturnADisposedMaterialToAnotherScope()
    {
        File.WriteAllText(Path.Combine(_directory, "material.njmaterial.json"), """{"baseColorTexturePath":"pixel.png"}""");
        using var content = Manager();
        using var a = content.CreateScope();
        using var b = content.CreateScope();
        Material material = a.Load<Material>("material.njmaterial.json");
        TestTexture texture = _device.Textures.Single();
        texture.FailRelease = true;
        Assert.Throws<AggregateException>(() => a.Dispose());
        Assert.That(material.IsDisposed, Is.True);
        Assert.Throws<InvalidOperationException>(() => b.Load<Material>("material.njmaterial.json"));
        texture.FailRelease = false;
        a.Dispose();
        Assert.That(b.Load<Material>("material.njmaterial.json").IsDisposed, Is.False);
    }

    [Test]
    public void InvalidFilesFailBeforeGpuCreation()
    {
        using var content = Manager();
        File.WriteAllText(Path.Combine(_directory, "bad.png"), "not an image");
        File.WriteAllText(Path.Combine(_directory, "bad.njmaterial.json"), """{"schemaVersion":2}""");
        Assert.Throws<InvalidDataException>(() => content.Load<Texture>("bad.png"));
        Assert.Throws<InvalidDataException>(() => content.Load<Material>("bad.njmaterial.json"));
        Assert.Throws<NotSupportedException>(() => content.Load<Texture>("file.dds"));
        Assert.Throws<ArgumentOutOfRangeException>(() => content.Load<Texture>("pixel.png", new() { TextureColorSpace = (TextureColorSpace)999 }));
        Assert.That(_device.Textures, Is.Empty);
    }

    [Test]
    public void SceneLoadsAreIndependentAndShareModelsAcrossScopes()
    {
        WriteScene("level.njscene.json");
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var a = content.CreateScope();
        using var b = content.CreateScope();
        Scene first = a.LoadScene("level.njscene.json");
        Scene second = Pump(b.LoadSceneAsync("level.njscene.json"), dispatcher);
        Scene third = a.LoadScene("level.njscene.json");
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(third, Is.Not.SameAs(first));
        Assert.That(second.RenderObjects[0], Is.Not.SameAs(first.RenderObjects[0]));
        Assert.That(_uploader.Models, Has.Count.EqualTo(1));
        first.RenderObjects[0].Visible = false;
        a.Dispose();
        Assert.That(second.RenderObjects[0].Visible, Is.True);
        Assert.That(_uploader.Models[0].IsDisposed, Is.False);
        b.Dispose();
        Assert.That(_uploader.Models[0].IsDisposed, Is.True);
    }

    [Test]
    public void SceneFailureRollsBackAcquiredModelsWithoutReleasingOtherOwners()
    {
        WriteScene("bad.njscene.json", subObject: "missing-object");
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var content = Manager(dispatcher);
        using var scope = content.CreateScope();
        Model root = content.Load<Model>("triangle.obj");
        Assert.Throws<InvalidDataException>(() => Pump(scope.LoadSceneAsync("bad.njscene.json"), dispatcher));
        Assert.That(root.IsDisposed, Is.False);
        content.Unload(root);
        Assert.That(root.IsDisposed, Is.True);
        Assert.Throws<InvalidDataException>(() => scope.LoadScene("bad.njscene.json"));
        Assert.That(_uploader.Models.Last().IsDisposed, Is.True);
    }

    private void WriteScene(string path, string subObject = "*") => SceneDocumentJson.WriteAtomic(Path.Combine(_directory, path), new()
    {
        Objects = [new() { Name = "Triangle", Model = new("triangle.obj", subObject) }],
        // Deliberately empty: loading must enumerate actual references, not this manifest.
        Dependencies = []
    });

    private static T Pump<T>(Task<T> task, RenderThreadContentUploadDispatcher dispatcher)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("Content did not settle while pumping uploads.");
            dispatcher.ProcessFrame(TimeSpan.FromMilliseconds(10), maximumCallbacks: 32);
            Thread.Sleep(1);
        }
        return task.GetAwaiter().GetResult();
    }

    private sealed class TestUploader : IModelRenderUploadService
    {
        public List<Model> Models { get; } = [];
        public ModelRenderUploadDiagnostics LastUploadDiagnostics { get; } = new("", 0, 0, 0, 0, 0, 0, 0, 0);
        public Model UploadModel(ModelMesh mesh)
        {
            var model = new Model { Name = mesh.Name };
            model.Add(new RenderObject(TestGraphicsResources.Mesh("mesh"), TestGraphicsResources.Material("material")) { Name = "Triangle" });
            Models.Add(model);
            return model;
        }
        public Model UploadCookedModel(CookedModelAsset model) => throw new NotSupportedException();
    }

    private sealed class TestDevice : GraphicsDevice, IDisposable
    {
        private readonly MaterialManager _materials = new();
        public List<TestTexture> Textures { get; } = [];
        public List<int> CreationThreads { get; } = [];
        public MaterialTextureAssignment[] Assignments { get; private set; } = [];
        public Action? AfterTextureCreated { get; set; }
        public override GraphicsSettingsController Settings => throw new NotSupportedException();
        public override GraphicsCapabilities Capabilities => throw new NotSupportedException();
        public override Texture CreateTexture2D(int width, int height, ReadOnlySpan<byte> rgba8, TextureColorSpace colorSpace)
        {
            Assert.That(rgba8.Length, Is.EqualTo(width * height * 4));
            var texture = new TestTexture(width, height, colorSpace);
            Textures.Add(texture);
            CreationThreads.Add(Environment.CurrentManagedThreadId);
            AfterTextureCreated?.Invoke();
            return texture;
        }
        public override Material CreateMaterial(MaterialDefinition definition) => CreateMaterial(definition, []);
        public override Material CreateMaterial(MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> textures)
        {
            Assignments = textures.ToArray();
            CreationThreads.Add(Environment.CurrentManagedThreadId);
            // Observe dependency selection here; real GPU retention is exercised by the API example.
            return _materials.AdoptResource(_materials.RegisterMaterialDefinition(definition));
        }
        public override Mesh CreateMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) => throw new NotSupportedException();
        public override RenderTarget2D CreateRenderTarget2D(int width, int height) => throw new NotSupportedException();
        public override GraphicsBuffer CreateBuffer(ulong sizeInBytes) => throw new NotSupportedException();
        public override RenderObject CreateRenderObject(IMesh mesh, IMaterial material) => new(mesh, material);
        public void Dispose() => _materials.Dispose();
    }

    private sealed class TestTexture(int width, int height, TextureColorSpace colorSpace) : Texture
    {
        public bool FailRelease;
        public int SuccessfulReleases;
        public override int Width => width;
        public override int Height => height;
        public override TextureColorSpace ColorSpace => colorSpace;
        public override bool IsDisposed { get; protected set; }
        public override void Dispose()
        {
            if (IsDisposed) return;
            if (FailRelease) throw new IOException("Injected texture release failure.");
            SuccessfulReleases++;
            IsDisposed = true;
        }
    }
}
