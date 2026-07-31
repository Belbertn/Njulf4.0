using System.Text.Json;
using Njulf.Assets.Scenes;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SceneMaterialOverridePersistenceTests
{
    [Test]
    public void SchemaV3_PartialOverrideSerializesOnlySpecifiedFieldsAndRetainsExactHdrCutoff()
    {
        float cutoff = MathF.BitIncrement(1f);
        var document = CreateDocument(
            new SceneMaterialOverrideDocument { AlphaCutoff = cutoff });

        string json = SceneDocumentJson.Serialize(document);
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement serializedOverride = parsed.RootElement
            .GetProperty("objects")[0]
            .GetProperty("materialOverride");

        Assert.Multiple(() =>
        {
            Assert.That(
                parsed.RootElement.GetProperty("schemaVersion").GetInt32(),
                Is.EqualTo(3));
            Assert.That(
                serializedOverride.EnumerateObject().Select(static item => item.Name),
                Is.EqualTo(new[] { "alphaCutoff" }));
            Assert.That(
                BitConverter.SingleToInt32Bits(
                    serializedOverride.GetProperty("alphaCutoff").GetSingle()),
                Is.EqualTo(BitConverter.SingleToInt32Bits(cutoff)));
        });

        SceneDocument reloaded = ReadTemporary(json);
        SceneMaterialOverrideDocument materialOverride =
            reloaded.Objects.Single().MaterialOverride!;
        Assert.Multiple(() =>
        {
            Assert.That(
                BitConverter.SingleToInt32Bits(materialOverride.AlphaCutoff!.Value),
                Is.EqualTo(BitConverter.SingleToInt32Bits(cutoff)));
            Assert.That(materialOverride.Name, Is.Null);
            Assert.That(materialOverride.Albedo, Is.Null);
            Assert.That(materialOverride.EmissiveColor, Is.Null);
            Assert.That(materialOverride.Metallic, Is.Null);
            Assert.That(materialOverride.AlphaMode, Is.Null);
            Assert.That(materialOverride.DoubleSided, Is.Null);
            Assert.That(materialOverride.RenderBlendModeOverride, Is.Null);
            Assert.That(materialOverride.DiffuseGiParticipation, Is.Null);
        });
    }

    [Test]
    public void Read_PreservesExplicitV1AndV2MaterialOverrideFields()
    {
        Guid sceneId = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();
        string v1 = $$"""
            {
              "schemaVersion": 1,
              "id": "{{sceneId}}",
              "objects": [{
                "id": "{{objectId}}",
                "model": { "path": "legacy-v1.glb" },
                "materialOverride": {
                  "albedo": { "r": 0.1, "g": 0.2, "b": 0.3, "a": 0.4 },
                  "emissive": { "r": 0.5, "g": 0.6, "b": 0.7, "a": 1.0 },
                  "metallic": 0.25,
                  "roughness": 0.75,
                  "normalScale": 2.0,
                  "alphaCutoff": 1.25
                }
              }]
            }
            """;
        string v2 = $$"""
            {
              "schemaVersion": 2,
              "id": "{{sceneId}}",
              "objects": [{
                "id": "{{objectId}}",
                "model": { "path": "legacy-v2.glb" },
                "materialOverride": {
                  "emissiveColor": { "r": 0.7, "g": 0.6, "b": 0.5, "a": 1.0 },
                  "emissiveStrength": 12.5,
                  "occlusionStrength": 0.35,
                  "shadingModel": "Unlit",
                  "emitsIntoGi": true,
                  "receivesDiffuseGi": false
                }
              }]
            }
            """;

        SceneDocument v1Document = ReadTemporary(v1);
        SceneDocument v2Document = ReadTemporary(v2);
        SceneMaterialOverrideDocument v1Override =
            v1Document.Objects.Single().MaterialOverride!;
        SceneMaterialOverrideDocument v2Override =
            v2Document.Objects.Single().MaterialOverride!;

        Assert.Multiple(() =>
        {
            Assert.That(v1Document.SchemaVersion, Is.EqualTo(1));
            Assert.That(v1Override.Albedo, Is.EqualTo(new SceneColor(0.1f, 0.2f, 0.3f, 0.4f)));
            Assert.That(v1Override.Emissive, Is.EqualTo(new SceneColor(0.5f, 0.6f, 0.7f, 1f)));
            Assert.That(v1Override.Metallic, Is.EqualTo(0.25f));
            Assert.That(v1Override.Roughness, Is.EqualTo(0.75f));
            Assert.That(v1Override.NormalScale, Is.EqualTo(2f));
            Assert.That(v1Override.AlphaCutoff, Is.EqualTo(1.25f));

            Assert.That(v2Document.SchemaVersion, Is.EqualTo(2));
            Assert.That(v2Override.EmissiveColor, Is.EqualTo(new SceneColor(0.7f, 0.6f, 0.5f, 1f)));
            Assert.That(v2Override.EmissiveStrength, Is.EqualTo(12.5f));
            Assert.That(v2Override.OcclusionStrength, Is.EqualTo(0.35f));
            Assert.That(v2Override.ShadingModel, Is.EqualTo("Unlit"));
            Assert.That(v2Override.EmitsIntoGi, Is.True);
            Assert.That(v2Override.ReceivesDiffuseGi, Is.False);
        });
    }

    [Test]
    public void Read_SparseV1OverrideMaterializesHistoricalDefaultsOnlyForV1()
    {
        Guid sceneId = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();
        string CreateJson(int schemaVersion) => $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "id": "{{sceneId}}",
              "objects": [{
                "id": "{{objectId}}",
                "model": { "path": "sparse-v{{schemaVersion}}.glb" },
                "materialOverride": {}
              }]
            }
            """;

        SceneMaterialOverrideDocument v1 =
            ReadTemporary(CreateJson(1)).Objects.Single().MaterialOverride!;
        SceneMaterialOverrideDocument v2 =
            ReadTemporary(CreateJson(2)).Objects.Single().MaterialOverride!;
        SceneMaterialOverrideDocument v3 =
            ReadTemporary(CreateJson(3)).Objects.Single().MaterialOverride!;

        Assert.Multiple(() =>
        {
            Assert.That(v1.Albedo, Is.EqualTo(new SceneColor(1f, 1f, 1f, 1f)));
            Assert.That(v1.Emissive, Is.EqualTo(new SceneColor(0f, 0f, 0f, 0f)));
            Assert.That(v1.Metallic, Is.EqualTo(0f));
            Assert.That(v1.Roughness, Is.EqualTo(1f));
            Assert.That(v1.NormalScale, Is.EqualTo(1f));
            Assert.That(v1.AlphaCutoff, Is.EqualTo(0.5f));

            Assert.That(v2.Albedo, Is.Null);
            Assert.That(v2.Emissive, Is.Null);
            Assert.That(v2.Metallic, Is.Null);
            Assert.That(v2.Roughness, Is.Null);
            Assert.That(v2.NormalScale, Is.Null);
            Assert.That(v2.AlphaCutoff, Is.Null);

            Assert.That(v3.Albedo, Is.Null);
            Assert.That(v3.Emissive, Is.Null);
            Assert.That(v3.Metallic, Is.Null);
            Assert.That(v3.Roughness, Is.Null);
            Assert.That(v3.NormalScale, Is.Null);
            Assert.That(v3.AlphaCutoff, Is.Null);
        });
    }

    [Test]
    public void Loader_SparseInMemoryV1OverrideAppliesDefaultsWithoutMutatingCaller()
    {
        var authored = new SceneMaterialOverrideDocument
        {
            Name = "Legacy authored name"
        };
        var document = new SceneDocument
        {
            SchemaVersion = 1,
            Id = Guid.NewGuid(),
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = Guid.NewGuid(),
                    Model = new SceneAssetReferenceDocument("legacy.glb"),
                    MaterialOverride = authored
                }
            ]
        };
        var model = new Model();
        model.Add(new RenderObject { Name = "Legacy mesh" });
        var store = new RecordingMaterialOverrideStore();

        new SceneDocumentLoader(new ModelContentManager(model))
            .Load(document, materials: store);

        SceneMaterialOverrideDocument applied = store.Applied.Single();
        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.Not.SameAs(authored));
            Assert.That(applied.Name, Is.EqualTo("Legacy authored name"));
            Assert.That(
                applied.Albedo,
                Is.EqualTo(new SceneColor(1f, 1f, 1f, 1f)));
            Assert.That(
                applied.Emissive,
                Is.EqualTo(new SceneColor(0f, 0f, 0f, 0f)));
            Assert.That(applied.Metallic, Is.EqualTo(0f));
            Assert.That(applied.Roughness, Is.EqualTo(1f));
            Assert.That(applied.NormalScale, Is.EqualTo(1f));
            Assert.That(applied.AlphaCutoff, Is.EqualTo(0.5f));

            Assert.That(document.Objects.Single().MaterialOverride, Is.SameAs(authored));
            Assert.That(authored.Albedo, Is.Null);
            Assert.That(authored.Emissive, Is.Null);
            Assert.That(authored.Metallic, Is.Null);
            Assert.That(authored.Roughness, Is.Null);
            Assert.That(authored.NormalScale, Is.Null);
            Assert.That(authored.AlphaCutoff, Is.Null);
        });
    }

    [TestCase(2)]
    [TestCase(3)]
    public void Loader_SparseModernOverrideRetainsInheritedSemantics(
        int schemaVersion)
    {
        var authored = new SceneMaterialOverrideDocument
        {
            Name = "Modern sparse name"
        };
        var document = new SceneDocument
        {
            SchemaVersion = schemaVersion,
            Id = Guid.NewGuid(),
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = Guid.NewGuid(),
                    Model = new SceneAssetReferenceDocument("modern.glb"),
                    MaterialOverride = authored
                }
            ]
        };
        var model = new Model();
        model.Add(new RenderObject { Name = "Modern mesh" });
        var store = new RecordingMaterialOverrideStore();

        new SceneDocumentLoader(new ModelContentManager(model))
            .Load(document, materials: store);

        SceneMaterialOverrideDocument applied = store.Applied.Single();
        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.SameAs(authored));
            Assert.That(applied.Name, Is.EqualTo("Modern sparse name"));
            Assert.That(applied.Albedo, Is.Null);
            Assert.That(applied.Emissive, Is.Null);
            Assert.That(applied.Metallic, Is.Null);
            Assert.That(applied.Roughness, Is.Null);
            Assert.That(applied.NormalScale, Is.Null);
            Assert.That(applied.AlphaCutoff, Is.Null);
        });
    }

    [Test]
    public void WriterReadLoader_RoundTripsEveryEditorExposedMaterialField()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "NjulfTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "material-roundtrip.njscene.json");
        float cutoff = MathF.BitIncrement(1f);

        try
        {
            using var materials = new MaterialManager();
            MaterialHandle sourceHandle = materials.RegisterMaterialDefinition(
                new MaterialDefinition
                {
                    Name = "Editor-authored material",
                    BaseColorFactor = new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
                    EmissiveFactor = new Vector3(0.6f, 0.5f, 0.4f),
                    EmissiveStrength = 12.75f,
                    MetallicFactor = 0.25f,
                    RoughnessFactor = 0.625f,
                    OcclusionStrength = 0.375f,
                    NormalScale = 2.5f,
                    AlphaMode = MaterialAlphaMode.Mask,
                    AlphaCutoff = cutoff,
                    DoubleSided = true,
                    ReceivesShadows = false,
                    RenderBlendModeOverride = MaterialBlendMode.PremultipliedAlpha,
                    ShadingModel = MaterialShadingModel.Foliage,
                    DiffuseGiParticipation = GiParticipationOverride.Default,
                    EmissionGiParticipation = GiParticipationOverride.Disabled,
                    FeatureFlags = MaterialFeatureFlags.Transmission,
                    Extensions = MaterialExtensionDefinition.None with
                    {
                        TransmissionPolicy = GiTransmissionPolicy.ThinSurface,
                        TransmissionFactor = 0.42f,
                        ThinTransmissionTint = new Vector3(0.8f, 0.45f, 0.2f)
                    }
                });
            var sourceObject = new RenderObject
            {
                Id = Guid.NewGuid(),
                Name = "Source object",
                Material = sourceHandle,
                AssetReference = new SceneAssetReference { Path = "roundtrip.glb" }
            };
            var sourceScene = new Scene { Id = Guid.NewGuid(), Name = "Material scene" };
            sourceScene.Add(sourceObject);
            var store = new MaterialManagerSceneMaterialOverrideStore(materials);

            new SceneDocumentWriter().Write(
                path,
                sourceScene,
                materials: store);
            SceneDocument saved = SceneDocumentJson.Read(path);

            MaterialHandle targetHandle = materials.RegisterMaterialDefinition(
                new MaterialDefinition
                {
                    Name = "Referenced asset material",
                    BaseColorFactor = Vector4.One,
                    EmissiveFactor = Vector3.Zero,
                    AlphaMode = MaterialAlphaMode.Opaque,
                    AlphaCutoff = 0.5f,
                    RenderBlendModeOverride = MaterialBlendMode.Additive,
                    ShadingModel = MaterialShadingModel.Pbr,
                    DiffuseGiParticipation = GiParticipationOverride.Enabled,
                    EmissionGiParticipation = GiParticipationOverride.Enabled,
                    IsGeometryDecal = true,
                    DecalLayer = 7,
                    DecalDepthBias = 0.002f,
                    FeatureFlags = MaterialFeatureFlags.Ior,
                    Extensions = new MaterialExtensionDefinition { Ior = 1.8f }
                });
            var model = new Model();
            model.Add(new RenderObject { Name = "Mesh", Material = targetHandle });
            Scene loaded = new SceneDocumentLoader(new ModelContentManager(model))
                .Load(saved, materials: store);

            MaterialHandle loadedHandle =
                (MaterialHandle)loaded.RenderObjects.Single().Material!;
            MaterialDefinition actual =
                materials.GetMaterialDefinition(loadedHandle);
            SceneMaterialOverrideDocument persisted =
                saved.Objects.Single().MaterialOverride!;

            Assert.Multiple(() =>
            {
                Assert.That(saved.SchemaVersion, Is.EqualTo(3));
                Assert.That(persisted.Emissive, Is.Null);
                Assert.That(
                    persisted.RenderBlendModeOverride,
                    Is.EqualTo(nameof(MaterialBlendMode.PremultipliedAlpha)));
                Assert.That(
                    persisted.DiffuseGiParticipation,
                    Is.EqualTo(nameof(GiParticipationOverride.Default)));
                Assert.That(
                    persisted.EmissionGiParticipation,
                    Is.EqualTo(nameof(GiParticipationOverride.Disabled)));
                Assert.That(persisted.EmitsIntoGi, Is.Null);
                Assert.That(persisted.ReceivesDiffuseGi, Is.Null);
                Assert.That(persisted.GiTransmissionPolicy, Is.EqualTo(nameof(GiTransmissionPolicy.ThinSurface)));
                Assert.That(persisted.ThinTransmissionFactor, Is.EqualTo(0.42f));

                Assert.That(actual.Name, Is.EqualTo("Editor-authored material"));
                Assert.That(actual.BaseColorFactor, Is.EqualTo(new Vector4(0.1f, 0.2f, 0.3f, 0.4f)));
                Assert.That(actual.EmissiveFactor, Is.EqualTo(new Vector3(0.6f, 0.5f, 0.4f)));
                Assert.That(actual.EmissiveStrength, Is.EqualTo(12.75f));
                Assert.That(actual.MetallicFactor, Is.EqualTo(0.25f));
                Assert.That(actual.RoughnessFactor, Is.EqualTo(0.625f));
                Assert.That(actual.OcclusionStrength, Is.EqualTo(0.375f));
                Assert.That(actual.NormalScale, Is.EqualTo(2.5f));
                Assert.That(actual.AlphaMode, Is.EqualTo(MaterialAlphaMode.Mask));
                Assert.That(
                    BitConverter.SingleToInt32Bits(actual.AlphaCutoff),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(cutoff)));
                Assert.That(actual.DoubleSided, Is.True);
                Assert.That(actual.ReceivesShadows, Is.False);
                Assert.That(
                    actual.RenderBlendModeOverride,
                    Is.EqualTo(MaterialBlendMode.PremultipliedAlpha));
                Assert.That(actual.ShadingModel, Is.EqualTo(MaterialShadingModel.Foliage));
                Assert.That(
                    actual.DiffuseGiParticipation,
                    Is.EqualTo(GiParticipationOverride.Default));
                Assert.That(
                    actual.EmissionGiParticipation,
                    Is.EqualTo(GiParticipationOverride.Disabled));
                Assert.That(actual.Extensions.TransmissionPolicy, Is.EqualTo(GiTransmissionPolicy.ThinSurface));
                Assert.That(actual.Extensions.TransmissionFactor, Is.EqualTo(0.42f));
                Assert.That(actual.Extensions.ThinTransmissionTint, Is.EqualTo(new Vector3(0.8f, 0.45f, 0.2f)));
                Assert.That(actual.FeatureFlags.HasFlag(MaterialFeatureFlags.Transmission), Is.True);

                Assert.That(actual.IsGeometryDecal, Is.True);
                Assert.That(actual.DecalLayer, Is.EqualTo(7));
                Assert.That(actual.DecalDepthBias, Is.EqualTo(0.002f));
                Assert.That(actual.Extensions.Ior, Is.EqualTo(1.8f));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestCase(GiParticipationOverride.Default)]
    [TestCase(GiParticipationOverride.Enabled)]
    [TestCase(GiParticipationOverride.Disabled)]
    public void CaptureApply_PreservesEveryGiParticipationState(
        GiParticipationOverride participation)
    {
        using var materials = new MaterialManager();
        MaterialHandle source = materials.RegisterMaterialDefinition(
            new MaterialDefinition
            {
                Name = $"GI {participation}",
                DiffuseGiParticipation = participation,
                EmissionGiParticipation = participation
            });
        var sourceObject = new RenderObject { Material = source };
        var store = new MaterialManagerSceneMaterialOverrideStore(materials);
        SceneMaterialOverrideDocument captured = store.Capture(sourceObject)!;

        MaterialHandle target = materials.RegisterMaterialDefinition(
            new MaterialDefinition
            {
                Name = "GI target",
                DiffuseGiParticipation = participation == GiParticipationOverride.Enabled
                    ? GiParticipationOverride.Disabled
                    : GiParticipationOverride.Enabled,
                EmissionGiParticipation = participation == GiParticipationOverride.Disabled
                    ? GiParticipationOverride.Enabled
                    : GiParticipationOverride.Disabled
            });
        var targetObject = new RenderObject { Material = target };
        store.Apply(targetObject, captured);
        MaterialDefinition actual = materials.GetMaterialDefinition(
            (MaterialHandle)targetObject.Material!);

        Assert.Multiple(() =>
        {
            Assert.That(captured.DiffuseGiParticipation, Is.EqualTo(participation.ToString()));
            Assert.That(captured.EmissionGiParticipation, Is.EqualTo(participation.ToString()));
            Assert.That(actual.DiffuseGiParticipation, Is.EqualTo(participation));
            Assert.That(actual.EmissionGiParticipation, Is.EqualTo(participation));
        });
    }

    [Test]
    public void CaptureApply_PreservesExplicitAutomaticBlendPolicy()
    {
        using var materials = new MaterialManager();
        MaterialHandle source = materials.RegisterMaterialDefinition(
            new MaterialDefinition
            {
                Name = "Automatic blend source",
                RenderBlendModeOverride = null
            });
        var store = new MaterialManagerSceneMaterialOverrideStore(materials);
        SceneMaterialOverrideDocument captured =
            store.Capture(new RenderObject { Material = source })!;

        MaterialHandle target = materials.RegisterMaterialDefinition(
            new MaterialDefinition
            {
                Name = "Explicit blend target",
                RenderBlendModeOverride = MaterialBlendMode.Additive
            });
        var targetObject = new RenderObject { Material = target };
        store.Apply(targetObject, captured);

        Assert.Multiple(() =>
        {
            Assert.That(
                captured.RenderBlendModeOverride,
                Is.EqualTo(SceneMaterialOverrideDocument.AutomaticBlendMode));
            Assert.That(
                materials.GetMaterialDefinition(
                    (MaterialHandle)targetObject.Material!).RenderBlendModeOverride,
                Is.Null);
        });
    }

    [Test]
    public void PartialApply_PreservesUnspecifiedAssetValuesAndSplitsOnlyForARealChange()
    {
        using var materials = new MaterialManager();
        var definition = new MaterialDefinition
        {
            Name = "Shared asset",
            BaseColorFactor = new Vector4(0.2f, 0.3f, 0.4f, 0.5f),
            EmissiveFactor = new Vector3(0.1f, 0.2f, 0.3f),
            MetallicFactor = 0.65f,
            RoughnessFactor = 0.35f,
            OcclusionStrength = 0.45f,
            NormalScale = 1.75f,
            AlphaMode = MaterialAlphaMode.Mask,
            AlphaCutoff = 0.4f,
            DoubleSided = true,
            ReceivesShadows = false,
            RenderBlendModeOverride = MaterialBlendMode.Mask,
            ShadingModel = MaterialShadingModel.Foliage,
            DiffuseGiParticipation = GiParticipationOverride.Disabled,
            EmissionGiParticipation = GiParticipationOverride.Enabled
        };
        MaterialHandle shared = materials.RegisterMaterialDefinition(definition);
        MaterialHandle alias = materials.RegisterMaterialDefinition(definition);
        var firstObject = new RenderObject { Material = alias };
        var store = new MaterialManagerSceneMaterialOverrideStore(materials);
        int materialCount = materials.RegisteredMaterialCount;

        store.Apply(firstObject, new SceneMaterialOverrideDocument());
        Assert.Multiple(() =>
        {
            Assert.That(firstObject.Material, Is.EqualTo(shared));
            Assert.That(materials.RegisteredMaterialCount, Is.EqualTo(materialCount));
        });

        float cutoff = MathF.BitIncrement(1f);
        store.Apply(
            firstObject,
            new SceneMaterialOverrideDocument { AlphaCutoff = cutoff });
        MaterialHandle edited = (MaterialHandle)firstObject.Material!;
        MaterialDefinition before = materials.GetMaterialDefinition(shared);
        MaterialDefinition actual = materials.GetMaterialDefinition(edited);

        Assert.Multiple(() =>
        {
            Assert.That(edited, Is.Not.EqualTo(shared));
            Assert.That(materials.GetMaterialDefinition(shared), Is.EqualTo(before));
            Assert.That(actual, Is.EqualTo(before with { AlphaCutoff = cutoff }));
            Assert.That(
                BitConverter.SingleToInt32Bits(actual.AlphaCutoff),
                Is.EqualTo(BitConverter.SingleToInt32Bits(cutoff)));
        });
    }

    [Test]
    public void Apply_OwnedObjectAdoptsCopyOnWriteTransferWithoutDoubleAccounting()
    {
        using var materials = new MaterialManager();
        var definition = new MaterialDefinition
        {
            Name = "Owned shared asset",
            RoughnessFactor = 0.4f
        };
        MaterialHandle shared =
            materials.RegisterMaterialDefinition(definition);
        MaterialHandle ownedAlias =
            materials.RegisterMaterialDefinition(definition);
        using var renderObject = new RenderObject
        {
            Material = ownedAlias
        };
        int retainCalls = 0;
        int releaseCalls = 0;
        renderObject.AttachResourceLifetime(
            static _ => { },
            static _ => { },
            material =>
            {
                retainCalls++;
                materials.RetainMaterial((MaterialHandle)material);
            },
            material =>
            {
                releaseCalls++;
                materials.ReleaseMaterial((MaterialHandle)material);
            },
            retainCurrentResources: false);
        var store =
            new MaterialManagerSceneMaterialOverrideStore(materials);

        store.Apply(
            renderObject,
            new SceneMaterialOverrideDocument
            {
                Roughness = 0.6f
            });
        MaterialHandle editable =
            (MaterialHandle)renderObject.Material!;

        Assert.Multiple(() =>
        {
            Assert.That(editable, Is.Not.EqualTo(shared));
            Assert.That(retainCalls, Is.Zero);
            Assert.That(releaseCalls, Is.Zero);
            Assert.That(
                materials.GetMaterialDefinition(shared),
                Is.EqualTo(definition));
            Assert.That(
                materials.GetMaterialDefinition(editable),
                Is.EqualTo(definition with
                {
                    RoughnessFactor = 0.6f
                }));
        });

        renderObject.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(releaseCalls, Is.EqualTo(1));
            Assert.That(
                () => materials.GetMaterialDefinition(editable),
                Throws.InvalidOperationException);
            Assert.That(
                materials.GetMaterialDefinition(shared),
                Is.EqualTo(definition));
        });
        materials.ReleaseMaterial(shared);
    }

    [Test]
    public void Apply_ValidatesEveryPolicyBeforeCopyOnWrite()
    {
        using var materials = new MaterialManager();
        var definition = new MaterialDefinition { Name = "Shared validation asset" };
        MaterialHandle shared = materials.RegisterMaterialDefinition(definition);
        MaterialHandle alias = materials.RegisterMaterialDefinition(definition);
        var renderObject = new RenderObject { Material = alias };
        var store = new MaterialManagerSceneMaterialOverrideStore(materials);
        int materialCount = materials.RegisteredMaterialCount;

        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument { AlphaCutoff = -0.01f }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument { Metallic = float.NaN }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument { AlphaMode = "future-alpha" }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument
                {
                    RenderBlendModeOverride = "future-blend"
                }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument { ShadingModel = "future-model" }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument
                {
                    DiffuseGiParticipation = "sometimes"
                }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument
                {
                    EmissionGiParticipation = "1"
                }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.Material, Is.EqualTo(shared));
            Assert.That(materials.RegisteredMaterialCount, Is.EqualTo(materialCount));
            Assert.That(materials.GetMaterialDefinition(shared), Is.EqualTo(definition));
        });
    }

    [Test]
    public void Apply_LegacyV1AndV2FieldsRemainSupportedWithoutOverwritingNewPolicies()
    {
        using var materials = new MaterialManager();
        MaterialHandle handle = materials.RegisterMaterialDefinition(
            new MaterialDefinition
            {
                Name = "Legacy target",
                AlphaMode = MaterialAlphaMode.Blend,
                DoubleSided = true,
                ReceivesShadows = false,
                RenderBlendModeOverride = MaterialBlendMode.Multiply,
                ShadingModel = MaterialShadingModel.Pbr,
                DiffuseGiParticipation = GiParticipationOverride.Default,
                EmissionGiParticipation = GiParticipationOverride.Default
            });
        var target = new RenderObject { Material = handle };
        var store = new MaterialManagerSceneMaterialOverrideStore(materials);

        store.Apply(
            target,
            new SceneMaterialOverrideDocument
            {
                Emissive = new SceneColor(0.3f, 0.4f, 0.5f, 1f),
                Metallic = 0.2f,
                Roughness = 0.8f,
                NormalScale = 2f,
                EmitsIntoGi = true,
                ReceivesDiffuseGi = false
            });
        MaterialDefinition actual = materials.GetMaterialDefinition(
            (MaterialHandle)target.Material!);

        Assert.Multiple(() =>
        {
            Assert.That(actual.EmissiveFactor, Is.EqualTo(new Vector3(0.3f, 0.4f, 0.5f)));
            Assert.That(actual.MetallicFactor, Is.EqualTo(0.2f));
            Assert.That(actual.RoughnessFactor, Is.EqualTo(0.8f));
            Assert.That(actual.NormalScale, Is.EqualTo(2f));
            Assert.That(actual.EmissionGiParticipation, Is.EqualTo(GiParticipationOverride.Enabled));
            Assert.That(actual.DiffuseGiParticipation, Is.EqualTo(GiParticipationOverride.Disabled));
            Assert.That(actual.Name, Is.EqualTo("Legacy target"));
            Assert.That(actual.AlphaMode, Is.EqualTo(MaterialAlphaMode.Blend));
            Assert.That(actual.DoubleSided, Is.True);
            Assert.That(actual.ReceivesShadows, Is.False);
            Assert.That(actual.RenderBlendModeOverride, Is.EqualTo(MaterialBlendMode.Multiply));
            Assert.That(actual.ShadingModel, Is.EqualTo(MaterialShadingModel.Pbr));
        });
    }

    private static SceneDocument CreateDocument(
        SceneMaterialOverrideDocument materialOverride) =>
        new()
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000000"),
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = Guid.Parse("20000000-0000-0000-0000-000000000000"),
                    Model = new SceneAssetReferenceDocument("material.glb"),
                    MaterialOverride = materialOverride
                }
            ]
        };

    private static SceneDocument ReadTemporary(string json)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "NjulfTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "scene.njscene.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, json);
            return SceneDocumentJson.Read(path);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ModelContentManager(Model model) : IContentManager
    {
        public T Load<T>(string path) => (T)(object)model;
        public void Unload<T>(T asset) { }
        public void Clear() { }
    }

    private sealed class RecordingMaterialOverrideStore :
        ISceneMaterialOverrideStore
    {
        public List<SceneMaterialOverrideDocument> Applied { get; } = [];

        public void Apply(
            RenderObject renderObject,
            SceneMaterialOverrideDocument materialOverride) =>
            Applied.Add(materialOverride);

        public SceneMaterialOverrideDocument? Capture(
            RenderObject renderObject) =>
            null;
    }
}
