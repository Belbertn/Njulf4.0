using Njulf.Assets;
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
    [Explicit("Requires the local Sponza cooked manifest and bundled scene document; no GPU upload.")]
    public void BundledSponzaSceneRetainsCookedOrigins()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Njulf.sln"))) directory = directory.Parent;
        string contentRoot = Path.Combine(directory!.FullName, "NjulfHelloGame");
        string path = Path.Combine(contentRoot, "Cooked", "win-x64", "models", "NewSponza_Main_glTF_003.njmodel");
        using var reader = new Njulf.Assets.Cooked.CookedAssetReader(path, Njulf.Assets.Cooked.CookedAssetKind.Model);
        var manifest = Njulf.Assets.Cooked.CookedJson.Deserialize<Njulf.Assets.Cooked.CookedModelManifest>(
            reader.GetRequiredSection(Njulf.Assets.Cooked.CookedSectionIds.Manifest).Span, path, "manifest");
        Assert.That(manifest.Nodes, Is.Not.Empty);
        // Use the package's real node/primitive metadata without retaining GPU resources.
        using var model = new Model();
        var nodes = manifest.Nodes.ToDictionary(node => node.Index,
            node => new SceneNode { Name = node.Name, LocalMatrix = node.LocalMatrix });
        foreach (var definition in manifest.Nodes)
        {
            if (definition.ParentIndex >= 0) nodes[definition.Index].SetParent(nodes[definition.ParentIndex], false);
            model.Add(nodes[definition.Index]);
        }
        var definitions = manifest.Nodes.ToDictionary(node => node.Index);
        foreach (var primitive in manifest.SubObjects)
        {
            var renderObject = new RenderObject { Name = primitive.Name };
            renderObject.AttachNode(nodes[primitive.NodeIndex], definitions[primitive.NodeIndex].WorldMatrix.Invert());
            model.Add(renderObject);
        }
        SceneDocument document = SceneDocumentJson.Read(Path.Combine(contentRoot, "Scenes", "SampleScene.njscene.json"));
        document.Objects.RemoveAll(item => item.Model?.Path != "NewSponza_Main_glTF_003.gltf");
        Assert.That(document.Objects, Is.Not.Empty);
        using Scene scene = new SceneDocumentLoader(new ModelContentManager(model)).Load(document);
        foreach (SceneObjectDocument record in document.Objects)
        {
            var item = (RenderObject)scene.FindById(record.Id)!;
            int index = int.Parse(record.Model!.SubObject);
            Matrix4x4 placement = Matrix4x4.CreateScale(new Vector3(record.Scale.X, record.Scale.Y, record.Scale.Z)) *
                new Quaternion(record.Rotation.X, record.Rotation.Y, record.Rotation.Z, record.Rotation.W).ToMatrix4x4() *
                Matrix4x4.CreateTranslation(new Vector3(record.Position.X, record.Position.Y, record.Position.Z));
            Matrix4x4 expectedOrigin = definitions[manifest.SubObjects[index].NodeIndex].WorldMatrix * placement;
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                Assert.That(item.Node.WorldMatrix[row, column], Is.EqualTo(expectedOrigin[row, column]).Within(0.001), record.Name);
                Assert.That(item.WorldMatrix[row, column], Is.EqualTo(placement[row, column]).Within(0.001), record.Name);
            }
        }
        Assert.That(scene.RenderObjects.Where(item => !item.IsTransformGroup)
            .Select(item => item.Node.WorldMatrix.Translation).Distinct().Count(), Is.GreaterThan(1));
    }

    [TestCase(1)]
    [TestCase(13)]
    public void LegacyBakedPlacementPreservesImportedOriginsAndRoundTrips(int version)
    {
        using var model = new Model();
        var parent = new SceneNode { Name = "Blender Empty" };
        model.Add(parent);
        foreach (float x in new[] { 10f, 20f })
        {
            var node = new SceneNode { LocalMatrix = Matrix4x4.CreateTranslation(new Vector3(x, 0, 0)) };
            node.SetParent(parent, false);
            model.Add(node);
            var mesh = new RenderObject();
            mesh.AttachNode(node, Matrix4x4.CreateTranslation(new Vector3(-x, 0, 0)));
            model.Add(mesh);
        }
        var secondPrimitive = new RenderObject();
        secondPrimitive.AttachNode(model.RenderObjects[0].Node, model.RenderObjects[0].MeshToNode);
        model.Add(secondPrimitive);
        var document = new SceneDocument { SchemaVersion = version };
        for (int i = 0; i < 3; i++)
            document.Objects.Add(new SceneObjectDocument { Name = $"Mesh {i}",
                Model = new SceneAssetReferenceDocument("fixture.glb", i.ToString()),
                Position = new SceneVector3(7, 0, 0) });
        var content = new ModelContentManager(model);
        using Scene scene = new SceneDocumentLoader(content).Load(document);
        RenderObject first = scene.RenderObjects.Single(item => item.Name == "Mesh 0");
        RenderObject second = scene.RenderObjects.Single(item => item.Name == "Mesh 1");
        Assert.That(first.Node.WorldMatrix.Translation, Is.EqualTo(new Vector3(17, 0, 0)));
        Assert.That(second.Node.WorldMatrix.Translation, Is.EqualTo(new Vector3(27, 0, 0)));
        Assert.That(first.WorldMatrix.Translation, Is.EqualTo(new Vector3(7, 0, 0)));
        Assert.That(first.Node.Parent, Is.SameAs(second.Node.Parent));
        Assert.That(scene.RenderObjects.Single(item => item.Name == "Mesh 2").Node, Is.SameAs(first.Node));
        first.Rotation = new Quaternion(new Vector3(0, 0, MathF.PI / 2));
        Assert.That(first.Node.WorldMatrix.Translation, Is.EqualTo(new Vector3(17, 0, 0)));
        Assert.That(10 * first.WorldMatrix.M11 + first.WorldMatrix.M41, Is.EqualTo(17).Within(0.0001));
        Assert.That(10 * first.WorldMatrix.M13 + first.WorldMatrix.M43, Is.Zero.Within(0.0001));
        SceneDocument saved = new SceneDocumentWriter().CreateDocument(scene);
        using Scene reloaded = new SceneDocumentLoader(content).Load(saved);
        RenderObject restored = reloaded.RenderObjects.Single(item => item.Name == "Mesh 0");
        Assert.That(restored.Node.WorldMatrix.Translation, Is.EqualTo(first.Node.WorldMatrix.Translation));
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
            Assert.That(restored.WorldMatrix[row, column], Is.EqualTo(first.WorldMatrix[row, column]).Within(0.0001));
        Assert.That(restored.PlacementRoot!.Id, Is.EqualTo(first.PlacementRoot!.Id));
        Assert.That(reloaded.RenderObjects.Single(item => item.Name == "Mesh 2").Node, Is.SameAs(restored.Node));
    }

    [Test]
    public void EmptyGroupsAndParentingRoundTrip()
    {
        using var scene = new Scene();
        var parent = new RenderObject { Name = "Parent", IsTransformGroup = true };
        var child = new RenderObject { Name = "Child", IsTransformGroup = true };
        child.Node.SetParent(parent.Node, keepWorld: false);
        child.Position = new Vector3(2f, 0f, 0f);
        scene.Add(parent);
        scene.Add(child);

        SceneDocument document = new SceneDocumentWriter().CreateDocument(scene);
        using Scene loaded = new SceneDocumentLoader(new ThrowingContentManager()).Load(document);
        RenderObject loadedParent = loaded.RenderObjects.Single(item => item.Name == "Parent");
        RenderObject loadedChild = loaded.RenderObjects.Single(item => item.Name == "Child");

        Assert.Multiple(() =>
        {
            Assert.That(document.SchemaVersion, Is.EqualTo(14));
            Assert.That(loadedChild.Node.Parent, Is.SameAs(loadedParent.Node));
            Assert.That(loadedChild.Position, Is.EqualTo(new Vector3(2f, 0f, 0f)));
        });
    }

    [Test]
    public void SceneOwnedLightingRoundTripsWithoutRendererStore()
    {
        using var scene = new Scene
        {
            Environment = new SceneEnvironment { SourceKind = SceneEnvironmentSource.HdrEquirectangular,
                SourcePath = "studio.hdr", GroundAlbedo = new Vector3(0.1f, 0.2f, 0.3f), TimeOfDayHours = 9f }
        };
        var light = new SceneLight { Type = SceneLightType.Spot, Intensity = 17f,
            Size = new Vector2(3, 2), CastsShadows = true,
            IesProfile = new SceneAssetReference { Path = "fixture.ies" } };
        scene.Add(light);
        SceneDocument document = new SceneDocumentWriter().CreateDocument(scene);
        string json = SceneDocumentJson.Serialize(document);
        SceneDocument restored = System.Text.Json.JsonSerializer.Deserialize<SceneDocument>(json, SceneDocumentJson.Options)!;
        using Scene loaded = new SceneDocumentLoader(new ThrowingContentManager()).Load(restored);
        Assert.That(loaded.Environment, Is.EqualTo(scene.Environment));
        Assert.That(loaded.Lights.Single().Id, Is.EqualTo(light.Id));
        Assert.That(loaded.Lights.Single().Intensity, Is.EqualTo(17f));
        Assert.That(loaded.Lights.Single().IesProfile, Is.EqualTo(light.IesProfile));
        ulong revision = loaded.LightRevision;
        loaded.Lights.Single().Intensity = 19f;
        Assert.That(loaded.LightRevision, Is.GreaterThan(revision));
    }

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
            VolumetricDensityVolumes =
            [
                new SceneVolumetricDensityVolumeDocument
                {
                    Id = second,
                    Name = "second volume"
                },
                new SceneVolumetricDensityVolumeDocument
                {
                    Id = first,
                    Name = "first volume"
                }
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
            Assert.That(once.IndexOf("first volume", StringComparison.Ordinal),
                Is.LessThan(once.IndexOf("second volume", StringComparison.Ordinal)));
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
    public void Loader_UsesSuppliedModelLoaderForDocumentAssets()
    {
        var model = new Model { Name = "Delegated model" };
        model.Add(new RenderObject { Name = "Mesh" });
        string? loadedPath = null;
        var document = new SceneDocument
        {
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "Delegated object",
                    Model = new SceneAssetReferenceDocument("delegated.glb")
                }
            ]
        };
        var loader = new SceneDocumentLoader(
            new ThrowingContentManager(),
            path =>
            {
                loadedPath = path;
                return model;
            });

        using Scene scene = loader.Load(document);

        Assert.Multiple(() =>
        {
            Assert.That(loadedPath, Is.EqualTo("delegated.glb"));
            Assert.That(scene.RenderObjects, Has.Count.EqualTo(1));
            Assert.That(scene.RenderObjects[0].Name,
                Is.EqualTo("Delegated object"));
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
                Assert.That(fresh.RenderObjects.Count(item => !item.IsTransformGroup), Is.EqualTo(2));
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
    public void AnalyticalLightsAndIesReferences_RoundTripWithoutSchemaLoss()
    {
        var source = new SceneDocument
        {
            Lights =
            [
                new SceneLightDocument
                {
                    Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                    Name = "Window softbox",
                    Type = "Rectangle",
                    Position = new SceneVector3(1f, 2f, 3f),
                    Direction = new SceneVector3(0f, 0f, -1f),
                    Up = new SceneVector3(0f, 1f, 0f),
                    Size = new SceneVector2(3f, 1.5f),
                    TwoSided = true,
                    Intensity = 20f,
                    Range = 14f
                },
                new SceneLightDocument
                {
                    Id = Guid.Parse("40000000-0000-0000-0000-000000000002"),
                    Name = "Photometric spot",
                    Type = "Spot",
                    Direction = new SceneVector3(0f, -1f, 0f),
                    IesProfile = new SceneAssetReferenceDocument(
                        "profiles/downlight.ies",
                        ContentHash: "fixture-hash"),
                    IesRotationRadians = 0.25f
                }
            ],
            Dependencies =
            [
                new SceneAssetDependency("profiles/downlight.ies", "fixture-hash")
            ]
        };
        var lights = new MemoryLightStore();
        Scene scene = new SceneDocumentLoader(new ThrowingContentManager()).Load(source, lights);
        try
        {
            SceneDocument captured = new SceneDocumentWriter().CreateDocument(scene, lights);
            Assert.Multiple(() =>
            {
                Assert.That(SceneDocument.CurrentSchemaVersion, Is.EqualTo(14));
                Assert.That(SceneDocumentJson.Serialize(captured),
                    Is.EqualTo(SceneDocumentJson.Serialize(source)));
                Assert.That(captured.Dependencies.Single().Path,
                    Is.EqualTo("profiles/downlight.ies"));
            });
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Test]
    public void Loader_RejectsInvalidAreaDimensionsAndIesUsage()
    {
        var loader = new SceneDocumentLoader(new ThrowingContentManager());
        var store = new MemoryLightStore();
        var nonCircularDisk = new SceneDocument
        {
            Lights =
            [
                new SceneLightDocument
                {
                    Type = "Disk",
                    Direction = new SceneVector3(0f, 0f, -1f),
                    Up = new SceneVector3(0f, 1f, 0f),
                    Size = new SceneVector2(2f, 1f)
                }
            ]
        };
        var areaWithIes = new SceneDocument
        {
            Lights =
            [
                new SceneLightDocument
                {
                    Type = "Rectangle",
                    Direction = new SceneVector3(0f, 0f, -1f),
                    Up = new SceneVector3(0f, 1f, 0f),
                    IesProfile = new SceneAssetReferenceDocument("profile.ies")
                }
            ]
        };
        var degenerateRectangle = new SceneDocument
        {
            Lights =
            [
                new SceneLightDocument
                {
                    Type = "Rectangle",
                    Direction = new SceneVector3(0f, 0f, -1f),
                    Up = new SceneVector3(0f, 1f, 0f),
                    Size = new SceneVector2(1e-6f, 1f)
                }
            ]
        };
        var unorientedPhotometricPoint = new SceneDocument
        {
            Lights =
            [
                new SceneLightDocument
                {
                    Type = "Point",
                    Direction = new SceneVector3(0f, 0f, 0f),
                    IesProfile = new SceneAssetReferenceDocument("profile.ies")
                }
            ]
        };

        Assert.Multiple(() =>
        {
            Assert.That(() => loader.Load(nonCircularDisk, store),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("dimensions"));
            Assert.That(() => loader.Load(areaWithIes, store),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("non-punctual"));
            Assert.That(() => loader.Load(degenerateRectangle, store),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("dimensions"));
            Assert.That(() => loader.Load(unorientedPhotometricPoint, store),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("zero direction"));
        });
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

    [Test]
    public void AuthoredVolumetricDensityVolumes_LoadAndSaveWithoutSchemaLoss()
    {
        Guid volumeId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var source = new SceneDocument
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000000"),
            Name = "Authored volumetric scene",
            VolumetricDensityVolumes =
            [
                new SceneVolumetricDensityVolumeDocument
                {
                    Id = volumeId,
                    Name = "Moving smoke",
                    Enabled = true,
                    Position = new SceneVector3(2f, 3f, -4f),
                    Rotation = SceneQuaternion.Identity,
                    Shape = "Sphere",
                    BoxExtents = new SceneVector3(7f, 6f, 5f),
                    Radius = 4.5f,
                    EdgeFade = 0.75f,
                    DensityMultiplier = 1.7f,
                    ExtinctionPerMeter = 0.32f,
                    ScatteringAlbedo = new SceneVector3(0.7f, 0.8f, 0.9f),
                    Anisotropy = 0.55f,
                    Priority = 9,
                    NoiseScale = 0.18f,
                    NoiseStrength = 0.8f,
                    NoiseContrast = 1.4f,
                    NoiseSeed = 73u,
                    FlowVelocity = new SceneVector3(1f, 0.25f, -2f)
                }
            ]
        };

        Scene loaded = new SceneDocumentLoader(
            new ThrowingContentManager()).Load(source);
        try
        {
            SceneDocument recaptured =
                new SceneDocumentWriter().CreateDocument(loaded);
            VolumetricDensityVolume volume =
                loaded.VolumetricDensityVolumes.Single();
            Assert.Multiple(() =>
            {
                Assert.That(volume.Id, Is.EqualTo(volumeId));
                Assert.That(volume.Shape,
                    Is.EqualTo(VolumetricDensityVolumeShape.Sphere));
                Assert.That(volume.FlowVelocity,
                    Is.EqualTo(new Vector3(1f, 0.25f, -2f)));
                Assert.That(volume.NoiseSeed, Is.EqualTo(73u));
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
        public void UnloadAll() { }
        public T Load<T>(string path, ContentLoadOptions options) => Load<T>(path);
        public Task<T> LoadAsync<T>(string path, ContentLoadOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(Load<T>(path));
        public Task<ContentPreloadResult<T>> PreloadAsync<T>(IEnumerable<ContentPreloadRequest> requests, ContentPreloadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ModelContentManager(Model model) : IContentManager
    {
        public T Load<T>(string path) => (T)(object)model;
        public void Unload<T>(T asset) { }
        public void UnloadAll() { }
        public T Load<T>(string path, ContentLoadOptions options) => Load<T>(path);
        public Task<T> LoadAsync<T>(string path, ContentLoadOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(Load<T>(path));
        public Task<ContentPreloadResult<T>> PreloadAsync<T>(IEnumerable<ContentPreloadRequest> requests, ContentPreloadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
