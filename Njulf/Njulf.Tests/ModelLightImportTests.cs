using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Assets.Scenes;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ModelLightImportTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void SharpGltf_ImportsRequiredPunctualLightsFromSelectedScene(
        bool binary)
    {
        string path = WritePunctualLightAsset(binary);
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf,
                GlobalScale = 2f,
                DefaultImportedLightRange = 25f
            });

        Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
        ModelMesh mesh = result.EnsureImported();
        Assert.That(mesh.Lights, Has.Count.EqualTo(3));
        ModelLightDefinition point = mesh.Lights.Single(light =>
            light.Type == ModelLightType.Point);
        ModelLightDefinition spot = mesh.Lights.Single(light =>
            light.Type == ModelLightType.Spot);
        ModelLightDefinition directional = mesh.Lights.Single(light =>
            light.Type == ModelLightType.Directional);
        Assert.Multiple(() =>
        {
            Assert.That(point.Name, Is.EqualTo("PointKey"));
            Assert.That(point.Position, Is.EqualTo(new Vector3(2f, 4f, 6f)));
            Assert.That(point.Color, Is.EqualTo(new Vector3(0.5f, 0.25f, 1f)));
            Assert.That(point.Intensity, Is.EqualTo(12f));
            Assert.That(point.Range, Is.EqualTo(8f));
            Assert.That(point.HasAuthoredRange, Is.True);
            Assert.That(point.AttenuationMode,
                Is.EqualTo(ModelLightAttenuationMode.InverseSquare));

            Assert.That(spot.Position, Is.EqualTo(new Vector3(8f, 10f, 12f)));
            Assert.That(spot.Range, Is.EqualTo(25f));
            Assert.That(spot.HasAuthoredRange, Is.False);
            Assert.That(spot.InnerConeAngle, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(spot.OuterConeAngle, Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(spot.Direction, Is.EqualTo(Vector3.Forward));

            Assert.That(directional.Intensity, Is.EqualTo(2f));
            Assert.That(directional.Direction, Is.EqualTo(Vector3.Forward));
            Assert.That(mesh.ImportDiagnostics.ImportedLightCount, Is.EqualTo(3));
            Assert.That(mesh.ImportDiagnostics.ImportedPointLightCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.ImportedSpotLightCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.ImportedDirectionalLightCount, Is.EqualTo(1));
            Assert.That(result.Diagnostics.UnsupportedRequiredExtensionCount, Is.Zero);
        });
    }

    [Test]
    public void SharpGltf_ImportLightsCanBeDisabledWithoutRejectingRequiredExtension()
    {
        string path = WritePunctualLightAsset(binary: false);
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf,
                ImportLights = false
            });

        Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
        Assert.That(result.EnsureImported().Lights, Is.Empty);
    }

    [Test]
    public void Assimp_ImportsFbxPointDirectionalAndSpotLights()
    {
        string path = WriteAsciiFbxLightAsset();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.Assimp,
                GlobalScale = 2f,
                DefaultImportedLightRange = 30f
            });

        Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
        ModelMesh mesh = result.EnsureImported();
        Assert.That(mesh.Vertices, Has.Length.EqualTo(3));
        Assert.That(mesh.Lights, Has.Count.EqualTo(3));
        ModelLightDefinition point = mesh.Lights.Single(light =>
            light.Type == ModelLightType.Point);
        ModelLightDefinition directional = mesh.Lights.Single(light =>
            light.Type == ModelLightType.Directional);
        ModelLightDefinition spot = mesh.Lights.Single(light =>
            light.Type == ModelLightType.Spot);
        Assert.Multiple(() =>
        {
            Assert.That(point.Position.X, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(point.Position.Y, Is.EqualTo(4f).Within(1e-4f));
            Assert.That(point.Position.Z, Is.EqualTo(-6f).Within(1e-4f));
            Assert.That(point.Intensity, Is.EqualTo(1f));
            Assert.That(point.Color.X, Is.EqualTo(6f).Within(1e-3f));
            Assert.That(point.Color.Y, Is.EqualTo(3f).Within(1e-3f));
            Assert.That(point.Color.Z, Is.EqualTo(12f).Within(1e-3f));
            Assert.That(point.AttenuationMode,
                Is.EqualTo(ModelLightAttenuationMode.Polynomial));
            Assert.That(point.AttenuationQuadratic, Is.GreaterThan(0f));
            Assert.That(point.Range, Is.GreaterThan(0f));

            Assert.That(directional.Direction.Length(), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(spot.InnerConeAngle,
                Is.EqualTo(MathF.PI / 9f).Within(1e-3f));
            Assert.That(spot.OuterConeAngle,
                Is.EqualTo(MathF.PI * 2f / 9f).Within(1e-3f));
            Assert.That(mesh.ImportDiagnostics.ImportedLightCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void ModelLightPlacement_IsExplicitStableTransformableAndDisposable()
    {
        Guid instanceId = Guid.Parse("ca170000-0000-0000-0000-000000000001");
        var model = new Model();
        model.AddLights(
        [
            new ModelLightDefinition
            {
                SourceIndex = 4,
                SourceNodeIndex = 9,
                Name = "Placed",
                Type = ModelLightType.Spot,
                Position = new Vector3(1f, 0f, 0f),
                Direction = Vector3.Forward,
                Color = new Vector3(0.25f, 0.5f, 1f),
                Intensity = 7f,
                Range = 4f,
                InnerConeAngle = 0.2f,
                OuterConeAngle = 0.5f,
                AttenuationMode = ModelLightAttenuationMode.Polynomial,
                AttenuationConstant = 1f,
                AttenuationLinear = 2f,
                AttenuationQuadratic = 3f
            }
        ]);
        var store = new MutableMemoryLightStore();
        Matrix4x4 firstTransform = Matrix4x4.CreateScale(new Vector3(2f)) *
            Matrix4x4.CreateTranslation(new Vector3(10f, 0f, 0f));

        Guid stableId;
        using (ModelLightInstanceSet placed = ModelLightInstantiator.Instantiate(
                   model,
                   store,
                   firstTransform,
                   instanceId))
        {
            stableId = placed.LightIds.Single();
            SceneLightDocument first = store.Items.Single();
            Assert.Multiple(() =>
            {
                Assert.That(first.Position, Is.EqualTo(new SceneVector3(12f, 0f, 0f)));
                Assert.That(first.Range, Is.EqualTo(8f));
                Assert.That(first.AttenuationLinear, Is.EqualTo(1f));
                Assert.That(first.AttenuationQuadratic, Is.EqualTo(0.75f));
                Assert.That(first.CastsShadows, Is.False);
            });

            placed.UpdateTransform(Matrix4x4.CreateTranslation(
                new Vector3(20f, 0f, 0f)));
            Assert.That(
                store.Items.Single().Position,
                Is.EqualTo(new SceneVector3(21f, 0f, 0f)));
        }

        Assert.That(store.Items, Is.Empty);
        using ModelLightInstanceSet second = ModelLightInstantiator.Instantiate(
            model,
            store,
            Matrix4x4.Identity,
            instanceId);
        Assert.That(second.LightIds.Single(), Is.EqualTo(stableId));
        Assert.That(model.CreateInstance().Lights, Has.Count.EqualTo(1));
    }

    [Test]
    public void RuntimeController_TogglesAllImportedLightsWithoutTouchingAuthoredLights()
    {
        using Model model = CreateRuntimeModel(lightCount: 2);
        var content = new ModelContentManager(model);
        var store = new MutableMemoryLightStore();
        Guid authoredId = Guid.Parse("a0170000-0000-0000-0000-000000000001");
        store.Add(authoredId, CreateAuthoredLight(authoredId));
        using var scene = new Scene();
        AddPlacement(
            scene,
            Guid.Parse("a0170000-0000-0000-0000-000000000010"),
            "0",
            Vector3.Zero);
        AddPlacement(
            scene,
            Guid.Parse("a0170000-0000-0000-0000-000000000011"),
            "1",
            Vector3.Zero);
        RenderObject secondPlacement = AddPlacement(
            scene,
            Guid.Parse("a0170000-0000-0000-0000-000000000020"),
            "0",
            new Vector3(10f, 0f, 0f));

        ModelLightRuntimeController controller =
            ModelLightRuntimeController.Attach(scene, content, store);
        Assert.Multiple(() =>
        {
            Assert.That(controller.ImportedModelLightsEnabled, Is.False);
            Assert.That(controller.ModelPlacementCount, Is.EqualTo(2));
            Assert.That(controller.ModelPlacementsWithLightsCount, Is.EqualTo(2));
            Assert.That(controller.ImportedLightDefinitionCount, Is.EqualTo(4));
            Assert.That(controller.ActiveLightCount, Is.Zero);
            Assert.That(store.Items.Select(light => light.Id), Is.EqualTo(new[] { authoredId }));
        });

        controller.SetImportedModelLightsEnabled(true);
        Guid[] stableIds = store.Items
            .Where(light => light.Id != authoredId)
            .Select(light => light.Id)
            .Order()
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(controller.ActiveLightCount, Is.EqualTo(4));
            Assert.That(store.Items, Has.Count.EqualTo(5));
            Assert.That(stableIds, Has.Length.EqualTo(4));
        });

        secondPlacement.Position = new Vector3(20f, 0f, 0f);
        Assert.Multiple(() =>
        {
            Assert.That(
                store.Items
                    .Where(light => light.Id != authoredId)
                    .Select(light => light.Id)
                    .Order(),
                Is.EqualTo(stableIds));
            Assert.That(
                store.Items.Any(light =>
                    light.Id != authoredId && light.Position.X == 21f),
                Is.True);
        });

        controller.SetImportedModelLightsEnabled(false);
        Assert.Multiple(() =>
        {
            Assert.That(controller.ImportedModelLightsEnabled, Is.False);
            Assert.That(controller.ActiveLightCount, Is.Zero);
            Assert.That(store.Items.Select(light => light.Id), Is.EqualTo(new[] { authoredId }));
        });
    }

    [Test]
    public void RuntimeController_SponzaStyleFlatteningCreatesOneLightSet()
    {
        const int sponzaSubObjectCount = 405;
        const int sponzaImportedLightCount = 24;
        using Model model = CreateRuntimeModel(sponzaImportedLightCount);
        var content = new ModelContentManager(model);
        var store = new MutableMemoryLightStore();
        using var scene = new Scene();
        for (int index = 0; index < sponzaSubObjectCount; index++)
        {
            AddPlacement(
                scene,
                Guid.NewGuid(),
                index.ToString(),
                Vector3.Zero);
        }

        ModelLightRuntimeController controller =
            ModelLightRuntimeController.Attach(scene, content, store);
        controller.SetImportedModelLightsEnabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(controller.ModelPlacementCount, Is.EqualTo(1));
            Assert.That(controller.ModelPlacementsWithLightsCount, Is.EqualTo(1));
            Assert.That(
                controller.ImportedLightDefinitionCount,
                Is.EqualTo(sponzaImportedLightCount));
            Assert.That(controller.ActiveLightCount, Is.EqualTo(sponzaImportedLightCount));
            Assert.That(store.Items, Has.Count.EqualTo(sponzaImportedLightCount));
        });
    }

    [Test]
    public void RuntimeController_CoalescesStructuralModelAdditions()
    {
        using Model model = CreateRuntimeModel(lightCount: 1);
        var content = new ModelContentManager(model);
        var store = new MutableMemoryLightStore();
        using var scene = new Scene();
        int loadCount = 0;
        ModelLightRuntimeController controller =
            ModelLightRuntimeController.Attach(
                scene,
                content,
                store,
                _ =>
                {
                    loadCount++;
                    return model;
                });

        for (int index = 0; index < 256; index++)
        {
            AddPlacement(
                scene,
                Guid.NewGuid(),
                index.ToString(),
                Vector3.Zero);
        }

        Assert.Multiple(() =>
        {
            Assert.That(loadCount, Is.Zero);
            Assert.That(controller.ModelPlacementCount, Is.Zero);
        });

        ((IUpdateable)controller).Update(0f);

        Assert.Multiple(() =>
        {
            Assert.That(loadCount, Is.EqualTo(1));
            Assert.That(controller.ModelPlacementCount, Is.EqualTo(1));
            Assert.That(
                controller.ImportedLightDefinitionCount,
                Is.EqualTo(1));
        });
    }

    [Test]
    public void RuntimeController_UsesTheConfiguredModelLoader()
    {
        using Model lightModel = CreateRuntimeModel(lightCount: 1);
        using Model fallbackModel = CreateRuntimeModel(lightCount: 0);
        var content = new ModelContentManager(fallbackModel);
        var store = new MutableMemoryLightStore();
        using var scene = new Scene();
        AddPlacement(
            scene,
            Guid.Parse("b0170000-0000-0000-0000-000000000010"),
            "0",
            Vector3.Zero);
        int loadCount = 0;

        ModelLightRuntimeController controller =
            ModelLightRuntimeController.Attach(
                scene,
                content,
                store,
                path =>
                {
                    Assert.That(path, Is.EqualTo("fixture.glb"));
                    loadCount++;
                    return lightModel;
                });
        controller.SetImportedModelLightsEnabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(loadCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(controller.ImportedLightDefinitionCount, Is.EqualTo(1));
            Assert.That(controller.ActiveLightCount, Is.EqualTo(1));
            Assert.That(store.Items, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void RuntimeController_ActivationFailureRollsBackEveryPlacement()
    {
        using Model model = CreateRuntimeModel(lightCount: 1);
        var content = new ModelContentManager(model);
        var store = new MutableMemoryLightStore();
        Guid authoredId = Guid.Parse("fa170000-0000-0000-0000-000000000001");
        store.Add(authoredId, CreateAuthoredLight(authoredId));
        store.FailAfterSuccessfulAdds = 1;
        using var scene = new Scene();
        AddPlacement(
            scene,
            Guid.Parse("fa170000-0000-0000-0000-000000000010"),
            "0",
            Vector3.Zero);
        AddPlacement(
            scene,
            Guid.Parse("fa170000-0000-0000-0000-000000000020"),
            "0",
            new Vector3(10f, 0f, 0f));
        ModelLightRuntimeController controller =
            ModelLightRuntimeController.Attach(scene, content, store);

        Assert.Throws<InvalidOperationException>(() =>
            controller.SetImportedModelLightsEnabled(true));
        Assert.Multiple(() =>
        {
            Assert.That(controller.ImportedModelLightsEnabled, Is.False);
            Assert.That(controller.ActiveLightCount, Is.Zero);
            Assert.That(controller.LastError, Is.Not.Null.And.Not.Empty);
            Assert.That(store.Items.Select(light => light.Id), Is.EqualTo(new[] { authoredId }));
        });
    }

    [Test]
    public void SceneWriterAndLoader_PersistMasterToggleWithoutSerializingGeneratedLights()
    {
        using Model model = CreateRuntimeModel(lightCount: 1, renderObjectCount: 2);
        var content = new ModelContentManager(model);
        var sourceStore = new MutableMemoryLightStore();
        Guid authoredId = Guid.Parse("5a170000-0000-0000-0000-000000000001");
        sourceStore.Add(authoredId, CreateAuthoredLight(authoredId));
        SceneDocument saved;
        using (var sourceScene = new Scene())
        {
            AddPlacement(
                sourceScene,
                Guid.Parse("5a170000-0000-0000-0000-000000000010"),
                "0",
                Vector3.Zero);
            AddPlacement(
                sourceScene,
                Guid.Parse("5a170000-0000-0000-0000-000000000011"),
                "1",
                Vector3.Zero);
            ModelLightRuntimeController sourceController =
                ModelLightRuntimeController.Attach(
                    sourceScene,
                    content,
                    sourceStore);
            sourceController.SetImportedModelLightsEnabled(true);

            saved = new SceneDocumentWriter().CreateDocument(
                sourceScene,
                sourceStore);
            Assert.Multiple(() =>
            {
                Assert.That(saved.SchemaVersion,
                    Is.EqualTo(SceneDocument.CurrentSchemaVersion));
                Assert.That(saved.ImportedModelLightsEnabled, Is.True);
                Assert.That(saved.Lights.Select(light => light.Id),
                    Is.EqualTo(new[] { authoredId }));
            });
        }

        string serialized = SceneDocumentJson.Serialize(saved);
        SceneDocument decoded = JsonSerializer.Deserialize<SceneDocument>(
            serialized,
            SceneDocumentJson.Options)!;
        var loadedStore = new MutableMemoryLightStore();
        using Scene loaded = new SceneDocumentLoader(content).Load(
            decoded,
            loadedStore);
        ModelLightRuntimeController? loadedController =
            loaded.GetComponent<ModelLightRuntimeController>();

        Assert.Multiple(() =>
        {
            Assert.That(decoded.ImportedModelLightsEnabled, Is.True);
            Assert.That(loadedController, Is.Not.Null);
            Assert.That(loadedController!.ImportedModelLightsEnabled, Is.True);
            Assert.That(loadedController.ModelPlacementCount, Is.EqualTo(1));
            Assert.That(loadedController.ActiveLightCount, Is.EqualTo(1));
            Assert.That(loadedStore.Items, Has.Count.EqualTo(2));
            Assert.That(
                new SceneDocumentWriter()
                    .CreateDocument(loaded, loadedStore)
                    .Lights
                    .Select(light => light.Id),
                Is.EqualTo(new[] { authoredId }));
        });
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void LegacySceneSchemas_DefaultImportedModelLightsOff(int schemaVersion)
    {
        SceneDocument document = JsonSerializer.Deserialize<SceneDocument>(
            $$"""
              {
                "schemaVersion": {{schemaVersion}},
                "id": "10000000-0000-0000-0000-000000000000"
              }
              """,
            SceneDocumentJson.Options)!;

        Assert.That(document.ImportedModelLightsEnabled, Is.False);
    }

    [Test]
    public void SceneLoader_RequiresMutableLightStoreOnlyWhenMasterToggleIsEnabled()
    {
        using Model model = CreateRuntimeModel(lightCount: 0);
        var loader = new SceneDocumentLoader(new ModelContentManager(model));
        var readOnlyStore = new ReadOnlyMemoryLightStore();
        using Scene disabled = loader.Load(
            new SceneDocument
            {
                ImportedModelLightsEnabled = false
            },
            readOnlyStore);

        Assert.That(
            disabled.GetComponent<ModelLightRuntimeController>(),
            Is.Null);
        Assert.Throws<InvalidOperationException>(() => loader.Load(
            new SceneDocument
            {
                ImportedModelLightsEnabled = true
            },
            readOnlyStore));
    }

    [Test]
    public void CookedManifest_RoundTripsLightDefinitionsAndLegacyDefaultsEmpty()
    {
        CookedModelManifest manifest = CreateManifest() with
        {
            Lights =
            [
                new ModelLightDefinition
                {
                    SourceIndex = 1,
                    Name = "CookedPoint",
                    Type = ModelLightType.Point,
                    Range = 9f,
                    Color = Vector3.One,
                    Direction = Vector3.Forward,
                    AttenuationMode = ModelLightAttenuationMode.InverseSquare
                }
            ]
        };

        byte[] encoded = CookedJson.Serialize(manifest);
        CookedModelManifest decoded = CookedJson.Deserialize<CookedModelManifest>(
            encoded,
            "memory.njmodel",
            "manifest");
        Assert.That(decoded.Lights.Single().Name, Is.EqualTo("CookedPoint"));

        string json = Encoding.UTF8.GetString(encoded);
        int property = json.IndexOf(",\"lights\":", StringComparison.Ordinal);
        Assert.That(property, Is.GreaterThan(0));
        int arrayStart = json.IndexOf('[', property);
        int depth = 0;
        int arrayEnd = -1;
        for (int index = arrayStart; index < json.Length; index++)
        {
            if (json[index] == '[') depth++;
            if (json[index] == ']' && --depth == 0)
            {
                arrayEnd = index;
                break;
            }
        }
        string legacyJson = json.Remove(property, arrayEnd - property + 1);
        CookedModelManifest legacy = CookedJson.Deserialize<CookedModelManifest>(
            Encoding.UTF8.GetBytes(legacyJson),
            "legacy.njmodel",
            "manifest");
        Assert.That(legacy.Lights, Is.Empty);
    }

    [Test]
    public void ModelCooker_PreservesGltfLightDefinitions()
    {
        string source = WritePunctualLightAsset(binary: false);
        string output = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-light-cooked-tests",
            Guid.NewGuid().ToString("N"));
        var options = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf,
                DefaultImportedLightRange = 25f
            }
        };

        using var cooker = new ModelAssetCooker();
        AssetCookResult result = cooker.CookModel(source, output, options);
        string package = Path.Combine(
            output,
            "models",
            Path.GetFileNameWithoutExtension(source) + ".njmodel");
        CookedModelAsset cooked = CookedPackage.LoadModel(package);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skipped, Is.False);
            Assert.That(cooked.Manifest.Lights, Has.Count.EqualTo(3));
            Assert.That(
                cooked.Manifest.Lights.Select(light => light.Type),
                Is.EquivalentTo(new[]
                {
                    ModelLightType.Point,
                    ModelLightType.Spot,
                    ModelLightType.Directional
                }));
        });
    }

    [Test]
    public void GpuLight_PacksAttenuationModeAndCoefficients()
    {
        var source = new Light
        {
            Type = LightType.Spot,
            CastsShadows = true,
            SpotAngle = 0.6f,
            InnerSpotAngle = 0.2f,
            AttenuationMode = LightAttenuationMode.Polynomial,
            AttenuationConstant = 1f,
            AttenuationLinear = 2f,
            AttenuationQuadratic = 3f
        };

        GPULight packed = LightManager.ToGpuLight(source);
        Assert.Multiple(() =>
        {
            Assert.That(
                packed.ShadowFlags & GPULight.CastsShadowsFlag,
                Is.Not.Zero);
            Assert.That(
                packed.ShadowFlags & GPULight.AttenuationModeMask,
                Is.EqualTo(GPULight.EncodeAttenuationMode(
                    LightAttenuationMode.Polynomial)));
            Assert.That(packed.InnerSpotAngle, Is.EqualTo(0.2f));
            Assert.That(packed.AttenuationConstant, Is.EqualTo(1f));
            Assert.That(packed.AttenuationLinear, Is.EqualTo(2f));
            Assert.That(packed.AttenuationQuadratic, Is.EqualTo(3f));
        });
    }

    [Test]
    public void SceneSchemaV6_RoundTripsMasterToggleAndV4LightDefaults()
    {
        Guid id = Guid.Parse("5ce17000-0000-0000-0000-000000000001");
        var current = new SceneDocument
        {
            ImportedModelLightsEnabled = true,
            Lights =
            [
                new SceneLightDocument
                {
                    Id = id,
                    Name = "PhysicalSpot",
                    Type = "Spot",
                    Direction = new SceneVector3(0f, 0f, -1f),
                    Range = 20f,
                    SpotAngle = 0.7f,
                    InnerSpotAngle = 0.2f,
                    AttenuationMode = "Polynomial",
                    AttenuationConstant = 1f,
                    AttenuationLinear = 2f,
                    AttenuationQuadratic = 3f
                }
            ]
        };

        string encoded = SceneDocumentJson.Serialize(current);
        SceneDocument decoded = JsonSerializer.Deserialize<SceneDocument>(
            encoded,
            SceneDocumentJson.Options)!;
        SceneLightDocument light = decoded.Lights.Single();
        Assert.Multiple(() =>
        {
            Assert.That(decoded.SchemaVersion,
                Is.EqualTo(SceneDocument.CurrentSchemaVersion));
            Assert.That(decoded.ImportedModelLightsEnabled, Is.True);
            Assert.That(light.InnerSpotAngle, Is.EqualTo(0.2f));
            Assert.That(light.AttenuationMode, Is.EqualTo("Polynomial"));
            Assert.That(light.AttenuationConstant, Is.EqualTo(1f));
            Assert.That(light.AttenuationLinear, Is.EqualTo(2f));
            Assert.That(light.AttenuationQuadratic, Is.EqualTo(3f));
        });

        SceneDocument legacy = JsonSerializer.Deserialize<SceneDocument>(
            $$"""
              {
                "schemaVersion": 4,
                "id": "10000000-0000-0000-0000-000000000000",
                "lights": [
                  {
                    "id": "{{id}}",
                    "name": "Legacy",
                    "type": "Point",
                    "range": 10
                  }
                ]
              }
              """,
            SceneDocumentJson.Options)!;
        Assert.That(
            legacy.Lights.Single().AttenuationMode,
            Is.EqualTo("LegacyWindowed"));
    }

    private static string WritePunctualLightAsset(bool binary)
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-light-import-tests");
        Directory.CreateDirectory(directory);
        string stem = $"{TestContext.CurrentContext.Test.ID}-{binary}";
        byte[] geometry = CreateTriangleBinary();
        string? uri = binary
            ? null
            : $"data:application/octet-stream;base64,{Convert.ToBase64String(geometry)}";
        string uriProperty = uri is null ? string.Empty : $", \"uri\": \"{uri}\"";
        string json = $$"""
          {
            "asset": { "version": "2.0" },
            "extensionsUsed": ["KHR_lights_punctual"],
            "extensionsRequired": ["KHR_lights_punctual"],
            "extensions": {
              "KHR_lights_punctual": {
                "lights": [
                  { "name": "PointKey", "type": "point", "color": [0.5, 0.25, 1.0], "intensity": 12, "range": 4 },
                  { "name": "SpotFill", "type": "spot", "intensity": 3, "spot": { "innerConeAngle": 0.1, "outerConeAngle": 0.6 } },
                  { "name": "Sun", "type": "directional", "intensity": 2 },
                  { "name": "Inactive", "type": "point", "intensity": 99, "range": 1 }
                ]
              }
            },
            "scene": 0,
            "scenes": [
              { "nodes": [0, 1, 2, 3] },
              { "nodes": [4] }
            ],
            "nodes": [
              { "name": "Triangle", "mesh": 0 },
              { "name": "PointNode", "translation": [1, 2, 3], "extensions": { "KHR_lights_punctual": { "light": 0 } } },
              { "name": "SpotNode", "translation": [4, 5, 6], "extensions": { "KHR_lights_punctual": { "light": 1 } } },
              { "name": "SunNode", "extensions": { "KHR_lights_punctual": { "light": 2 } } },
              { "name": "InactiveNode", "extensions": { "KHR_lights_punctual": { "light": 3 } } }
            ],
            "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0, "NORMAL": 1 }, "indices": 2, "mode": 4 }] }],
            "buffers": [{ "byteLength": {{geometry.Length}}{{uriProperty}} }],
            "bufferViews": [
              { "buffer": 0, "byteOffset": 0, "byteLength": 36, "target": 34962 },
              { "buffer": 0, "byteOffset": 36, "byteLength": 36, "target": 34962 },
              { "buffer": 0, "byteOffset": 72, "byteLength": 6, "target": 34963 }
            ],
            "accessors": [
              { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
              { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3" },
              { "bufferView": 2, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
            ]
          }
          """;
        string path = Path.Combine(directory, stem + (binary ? ".glb" : ".gltf"));
        if (binary)
            WriteGlb(path, json, geometry);
        else
            File.WriteAllText(path, json);
        return path;
    }

    private static string WriteAsciiFbxLightAsset()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-light-import-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"{TestContext.CurrentContext.Test.ID}.fbx");
        File.WriteAllText(
            path,
            """
            ; FBX 7.4.0 project file
            FBXHeaderExtension:  {
                FBXHeaderVersion: 1003
                FBXVersion: 7400
                Creator: "Njulf light import test"
            }
            GlobalSettings:  {
                Version: 1000
                Properties70:  {
                    P: "UpAxis", "int", "Integer", "",1
                    P: "UpAxisSign", "int", "Integer", "",1
                    P: "FrontAxis", "int", "Integer", "",2
                    P: "FrontAxisSign", "int", "Integer", "",-1
                    P: "CoordAxis", "int", "Integer", "",0
                    P: "CoordAxisSign", "int", "Integer", "",1
                    P: "UnitScaleFactor", "double", "Number", "",100
                }
            }
            Documents:  {
                Count: 1
                Document: 100, "Scene", "Scene" {
                    Properties70:  {
                    }
                    RootNode: 0
                }
            }
            References:  {
            }
            Definitions:  {
                Version: 100
                Count: 9
                ObjectType: "GlobalSettings" { Count: 1 }
                ObjectType: "Model" { Count: 4 }
                ObjectType: "Geometry" { Count: 1 }
                ObjectType: "NodeAttribute" { Count: 3 }
            }
            Objects:  {
                Geometry: 1000, "Geometry::Triangle", "Mesh" {
                    GeometryVersion: 124
                    Vertices: *9 {
                        a: 0,0,0,1,0,0,0,1,0
                    }
                    PolygonVertexIndex: *3 {
                        a: 0,1,-3
                    }
                }
                Model: 1001, "Model::Triangle", "Mesh" {
                    Version: 232
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
                    }
                    Shading: T
                    Culling: "CullingOff"
                }
                NodeAttribute: 2000, "NodeAttribute::PointKey", "Light" {
                    TypeFlags: "Light"
                    GeometryVersion: 124
                    Properties70:  {
                        P: "LightType", "enum", "", "",0
                        P: "Color", "Color", "", "A",0.5,0.25,1
                        P: "Intensity", "double", "Number", "A",1200
                        P: "DecayType", "enum", "", "",2
                        P: "DecayStart", "double", "Number", "A",4
                    }
                }
                Model: 2001, "Model::PointKey", "Light" {
                    Version: 232
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",1,2,3
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
                    }
                    Shading: T
                    Culling: "CullingOff"
                }
                NodeAttribute: 3000, "NodeAttribute::Sun", "Light" {
                    TypeFlags: "Light"
                    GeometryVersion: 124
                    Properties70:  {
                        P: "LightType", "enum", "", "",1
                        P: "Color", "Color", "", "A",1,0.8,0.6
                        P: "Intensity", "double", "Number", "A",200
                    }
                }
                Model: 3001, "Model::Sun", "Light" {
                    Version: 232
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
                    }
                    Shading: T
                    Culling: "CullingOff"
                }
                NodeAttribute: 4000, "NodeAttribute::SpotFill", "Light" {
                    TypeFlags: "Light"
                    GeometryVersion: 124
                    Properties70:  {
                        P: "LightType", "enum", "", "",2
                        P: "Color", "Color", "", "A",0.25,0.5,1
                        P: "Intensity", "double", "Number", "A",300
                        P: "InnerAngle", "double", "Number", "A",20
                        P: "OuterAngle", "double", "Number", "A",40
                        P: "DecayType", "enum", "", "",1
                        P: "DecayStart", "double", "Number", "A",10
                    }
                }
                Model: 4001, "Model::SpotFill", "Light" {
                    Version: 232
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",4,5,6
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
                    }
                    Shading: T
                    Culling: "CullingOff"
                }
            }
            Connections:  {
                C: "OO",1000,1001
                C: "OO",1001,0
                C: "OO",2000,2001
                C: "OO",2001,0
                C: "OO",3000,3001
                C: "OO",3001,0
                C: "OO",4000,4001
                C: "OO",4001,0
            }
            Takes:  {
                Current: ""
            }
            """);
        return path;
    }

    private static byte[] CreateTriangleBinary()
    {
        var bytes = new List<byte>();
        foreach (float value in new[]
                 {
                     0f, 0f, 0f,
                     1f, 0f, 0f,
                     0f, 1f, 0f,
                     0f, 0f, 1f,
                     0f, 0f, 1f,
                     0f, 0f, 1f
                 })
        {
            bytes.AddRange(BitConverter.GetBytes(value));
        }
        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes((ushort)2));
        return bytes.ToArray();
    }

    private static void WriteGlb(string path, string json, byte[] binary)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int paddedJson = (jsonBytes.Length + 3) & ~3;
        int paddedBinary = (binary.Length + 3) & ~3;
        byte[] output = new byte[12 + 8 + paddedJson + 8 + paddedBinary];
        BinaryPrimitives.WriteUInt32LittleEndian(output, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)output.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), checked((uint)paddedJson));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
        jsonBytes.CopyTo(output.AsSpan(20));
        output.AsSpan(20 + jsonBytes.Length, paddedJson - jsonBytes.Length).Fill(0x20);
        int binaryHeader = 20 + paddedJson;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader), checked((uint)paddedBinary));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader + 4), 0x004E4942);
        binary.CopyTo(output.AsSpan(binaryHeader + 8));
        File.WriteAllBytes(path, output);
    }

    private static CookedModelManifest CreateManifest() => new(
        Guid.Parse("1a170000-0000-0000-0000-000000000001"),
        "Fixture",
        "fixture.gltf",
        1,
        2,
        3,
        new CookedAssetReference("fixture.njmesh", 4),
        new CookedAssetReference("fixture.njmat", 5),
        null,
        Array.Empty<CookedModelSubObject>(),
        default,
        default);

    private static Model CreateRuntimeModel(
        int lightCount,
        int renderObjectCount = 0)
    {
        var model = new Model { Name = "RuntimeLightFixture" };
        for (int index = 0; index < renderObjectCount; index++)
            model.Add(new RenderObject { Name = $"Mesh {index}" });
        model.AddLights(Enumerable.Range(0, lightCount).Select(index =>
            new ModelLightDefinition
            {
                SourceIndex = index,
                SourceNodeIndex = index,
                Name = $"Imported {index}",
                Type = ModelLightType.Point,
                Position = new Vector3(index + 1f, 0f, 0f),
                Direction = Vector3.Forward,
                Color = Vector3.One,
                Intensity = 1f,
                Range = 10f,
                AttenuationMode = ModelLightAttenuationMode.InverseSquare
            }));
        return model;
    }

    private static RenderObject AddPlacement(
        Scene scene,
        Guid id,
        string subObject,
        Vector3 position)
    {
        var renderObject = new RenderObject
        {
            Id = id,
            Name = $"Placement {subObject}",
            AssetReference = new SceneAssetReference
            {
                Path = "fixture.glb",
                SubObject = subObject
            },
            Position = position
        };
        scene.Add(renderObject);
        return renderObject;
    }

    private static SceneLightDocument CreateAuthoredLight(Guid id) => new()
    {
        Id = id,
        Name = "Authored",
        Type = "Point",
        Direction = new SceneVector3(0f, -1f, 0f),
        Color = new SceneVector3(1f, 1f, 1f),
        Intensity = 2f,
        Range = 10f
    };

    private sealed class ModelContentManager(Model model) : IContentManager
    {
        public T Load<T>(string path) => (T)(object)model;

        public void Unload<T>(T asset) { }
        public void Clear() { }
    }

    private sealed class MutableMemoryLightStore : IMutableSceneLightStore
    {
        private readonly Dictionary<Guid, SceneLightDocument> _items = [];

        public IReadOnlyCollection<SceneLightDocument> Items => _items.Values;
        public int? FailAfterSuccessfulAdds { get; set; }
        public void Clear() => _items.Clear();
        public void Add(Guid id, SceneLightDocument light)
        {
            if (FailAfterSuccessfulAdds == 0)
                throw new InvalidOperationException("Injected light-store failure.");
            if (FailAfterSuccessfulAdds is int remaining)
                FailAfterSuccessfulAdds = remaining - 1;
            _items.Add(id, light);
        }
        public IEnumerable<SceneLightDocument> Enumerate() => _items.Values;
        public bool TryUpdate(Guid id, SceneLightDocument light)
        {
            if (!_items.ContainsKey(id))
                return false;
            _items[id] = light;
            return true;
        }
        public bool TryRemove(Guid id) => _items.Remove(id);
    }

    private sealed class ReadOnlyMemoryLightStore : ISceneLightStore
    {
        private readonly Dictionary<Guid, SceneLightDocument> _items = [];

        public void Clear() => _items.Clear();
        public void Add(Guid id, SceneLightDocument light) =>
            _items.Add(id, light);
        public IEnumerable<SceneLightDocument> Enumerate() => _items.Values;
    }
}
