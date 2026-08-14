using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Interfaces;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SceneDocumentTests
{
    [Test]
    public void Serialize_IsDeterministicAndSortsUnorderedEntityCollections()
    {
        Guid first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var document = new SceneDocument
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000000"),
            Lights =
            [
                new SceneLightDocument { Id = second, Name = "second" },
                new SceneLightDocument { Id = first, Name = "first" }
            ],
            Dependencies = [new SceneAssetDependency("z.glb"), new SceneAssetDependency("a.glb")]
        };

        string once = SceneDocumentJson.Serialize(document);
        string twice = SceneDocumentJson.Serialize(document);

        Assert.Multiple(() =>
        {
            Assert.That(twice, Is.EqualTo(once));
            Assert.That(once.IndexOf(first.ToString(), StringComparison.Ordinal), Is.LessThan(once.IndexOf(second.ToString(), StringComparison.Ordinal)));
            Assert.That(once.IndexOf("a.glb", StringComparison.Ordinal), Is.LessThan(once.IndexOf("z.glb", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void Writer_UsesAtomicSaveAndCreatesOnlyOneSessionBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "NjulfTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "scene.njscene.json");
        try
        {
            var scene = new Scene { Name = "Saved scene", AmbientLight = new Color(0.1f, 0.2f, 0.3f, 1f) };
            scene.Add(new ReflectionProbe { Name = "Probe", Position = new Vector3(1f, 2f, 3f) });
            var writer = new SceneDocumentWriter();

            writer.Write(path, scene);
            string original = File.ReadAllText(path);
            scene.Name = "Changed scene";
            writer.Write(path, scene);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(path), Is.True);
                Assert.That(File.Exists(path + ".bak"), Is.True);
                Assert.That(File.ReadAllText(path + ".bak"), Is.EqualTo(original));
                Assert.That(File.ReadAllText(path), Does.Contain("Changed scene"));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Writer_OmitsExplicitRuntimeOnlyObjectsWithoutWeakeningAssetValidation()
    {
        var scene = new Scene();
        scene.Add(new RenderObject
        {
            Name = "Runtime diagnostic",
            PersistInSceneDocument = false
        });

        SceneDocument document = new SceneDocumentWriter().CreateDocument(scene);

        Assert.Multiple(() =>
        {
            Assert.That(document.Objects, Is.Empty);
            Assert.That(document.Dependencies, Is.Empty);
        });

        scene.Add(new RenderObject { Name = "Unbacked authored object" });
        Assert.That(
            () => new SceneDocumentWriter().CreateDocument(scene),
            Throws.InvalidOperationException.With.Message.Contains(
                "has no source asset reference"));
    }

    [Test]
    public void Read_RejectsUnknownSchemaAndIgnoresForwardFields()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.njscene.json");
        try
        {
            string? warning = null;
            void CaptureWarning(string message) => warning = message;
            SceneDocumentJson.Warning += CaptureWarning;
            File.WriteAllText(path, "{\"schemaVersion\":1,\"id\":\"10000000-0000-0000-0000-000000000000\",\"name\":\"Forward\",\"futureField\":42}");
            Assert.That(SceneDocumentJson.Read(path).Name, Is.EqualTo("Forward"));
            Assert.That(warning, Does.Contain("futureField"));
            File.WriteAllText(path, "{\"schemaVersion\":999}");
            Assert.That(() => SceneDocumentJson.Read(path), Throws.TypeOf<InvalidDataException>());
            SceneDocumentJson.Warning -= CaptureWarning;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Loader_ReportsRecordIdentityForMissingModelAndAppliesTrs()
    {
        Guid id = Guid.NewGuid();
        var document = new SceneDocument
        {
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = id,
                    Name = "Missing object",
                    Model = new SceneAssetReferenceDocument("missing.glb"),
                    Position = new SceneVector3(4f, 5f, 6f)
                }
            ]
        };
        var loader = new SceneDocumentLoader(new ThrowingContentManager());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => loader.Load(document))!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain(id.ToString()));
            Assert.That(error.Message, Does.Contain("Missing object"));
        });
    }

    [Test]
    public void Loader_RejectsNegativeMaterialAlphaCutoffBeforeLoadingContent()
    {
        Guid id = Guid.NewGuid();
        var document = new SceneDocument
        {
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = id,
                    Name = "Invalid alpha",
                    Model = new SceneAssetReferenceDocument("missing.glb"),
                    MaterialOverride = new SceneMaterialOverrideDocument
                    {
                        AlphaCutoff = -0.01f
                    }
                }
            ]
        };
        var loader = new SceneDocumentLoader(new ThrowingContentManager());

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(() => loader.Load(document))!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain(id.ToString()));
            Assert.That(error.Message, Does.Contain("alpha cutoff"));
            Assert.That(error.InnerException, Is.Null);
        });
    }

    [Test]
    public void EditSaveReload_RoundTripsObjectsAndLightsIntoFreshScene()
    {
        string root = Path.Combine(Path.GetTempPath(), "NjulfTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "edited.njscene.json");
        try
        {
            var model = new Model { Name = "Test model" };
            model.Add(new RenderObject { Name = "Mesh" });
            var content = new ModelContentManager(model);
            var lights = new MemoryLightStore();
            var source = new SceneDocument
            {
                Id = Guid.NewGuid(),
                Objects = [new SceneObjectDocument { Id = Guid.NewGuid(), Name = "Original", Model = new SceneAssetReferenceDocument("test.glb") }],
                Lights = [new SceneLightDocument { Id = Guid.NewGuid(), Name = "Key", Intensity = 2f }]
            };
            Scene scene = new SceneDocumentLoader(content).Load(source, lights);

            scene.RenderObjects[0].Position = new Vector3(3f, 4f, 5f);
            lights.ReplaceIntensity(7f);
            RenderObject added = model.CreateInstance().RenderObjects[0];
            added.Name = "Added";
            added.AssetReference = new SceneAssetReference { Path = "test.glb" };
            scene.Add(added);

            var writer = new SceneDocumentWriter();
            writer.Write(path, scene, lights);
            SceneDocument saved = SceneDocumentJson.Read(path);
            var freshLights = new MemoryLightStore();
            Scene fresh = new SceneDocumentLoader(content).Load(saved, freshLights);
            SceneDocument recaptured = writer.CreateDocument(fresh, freshLights);

            Assert.Multiple(() =>
            {
                Assert.That(fresh.RenderObjects, Has.Count.EqualTo(2));
                Assert.That(fresh.RenderObjects.Single(item => item.Name == "Original").Position, Is.EqualTo(new Vector3(3f, 4f, 5f)));
                Assert.That(freshLights.Enumerate().Single().Intensity, Is.EqualTo(7f));
                Assert.That(SceneDocumentJson.Serialize(recaptured), Is.EqualTo(SceneDocumentJson.Serialize(saved)));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AuthoredGiVolumes_LoadAndSaveWithoutSchemaLoss()
    {
        Guid volumeId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var source = new SceneDocument
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000000"),
            Name = "Authored GI scene",
            GiProbeVolumes =
            [
                new SceneGlobalIlluminationProbeVolumeDocument
                {
                    Id = volumeId,
                    Name = "Kitchen GI",
                    Enabled = false,
                    Origin = new SceneVector3(-3f, 1f, 7f),
                    Size = new SceneVector3(18f, 9f, 22f),
                    Interior = true,
                    QualityClass = "High",
                    Priority = 17,
                    BlendDistance = 2.5f,
                    StreamingCellId = 42,
                    ProbeCountX = 10,
                    ProbeCountY = 6,
                    ProbeCountZ = 12,
                    RaysPerProbe = 144,
                    MaxProbeUpdatesPerFrame = 37,
                    NormalBias = 0.17f,
                    ViewBias = 0.43f,
                    MaxRayDistance = 19f,
                    Intensity = 1.3f,
                    Hysteresis = 0.81f,
                    SteadyHysteresis = 0.93f,
                    DirtyHysteresis = 0.51f,
                    UpdatePriority = 23,
                    DirtyRaysPerProbe = 192
                }
            ]
        };

        Scene loaded = new SceneDocumentLoader(new ThrowingContentManager()).Load(source);
        try
        {
            SceneDocument recaptured = new SceneDocumentWriter().CreateDocument(loaded);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.GlobalIlluminationProbeVolumes, Has.Count.EqualTo(1));
                Assert.That(loaded.GlobalIlluminationProbeVolumes[0].Id, Is.EqualTo(volumeId));
                Assert.That(SceneDocumentJson.Serialize(recaptured),
                    Is.EqualTo(SceneDocumentJson.Serialize(source)));
            });
        }
        finally
        {
            loaded.Dispose();
        }
    }

    private sealed class ThrowingContentManager : IContentManager
    {
        public T Load<T>(string path) => throw new FileNotFoundException($"Missing {path}");
        public void Unload<T>(T asset) { }
        public void Clear() { }
    }

    private sealed class ModelContentManager(Model model) : IContentManager
    {
        public T Load<T>(string path) => (T)(object)model;
        public void Unload<T>(T asset) { }
        public void Clear() { }
    }

    private sealed class MemoryLightStore : ISceneLightStore
    {
        private readonly List<SceneLightDocument> _lights = [];
        private float? _intensityOverride;
        public void Clear() => _lights.Clear();
        public void Add(Guid id, SceneLightDocument light) => _lights.Add(light);
        public IEnumerable<SceneLightDocument> Enumerate() => _intensityOverride is not float intensity
            ? _lights
            : _lights.Select(light => new SceneLightDocument { Id = light.Id, Name = light.Name, Type = light.Type, Intensity = intensity });
        public void ReplaceIntensity(float intensity) => _intensityOverride = intensity;
    }
}
