using System;
using System.Collections.Generic;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

internal sealed class SampleStressSceneBuilder
{
    private readonly Scene _scene;
    private readonly MeshManager _meshManager;
    private readonly MaterialManager _materialManager;
    private readonly LightManager _lightManager;
    private readonly SampleLightingMode _normalLightingMode;
    private readonly List<RenderObject> _objects = new();
    private readonly List<ReflectionProbe> _probes = new();
    private readonly List<StaticInstanceBatch> _staticBatches = new();
    private readonly List<FoliagePatch> _foliagePatches = new();
    private readonly List<FoliagePrototype> _foliagePrototypes = new();
    private readonly FoliageManager _foliageManager = new();
    private MeshHandle _quadMesh = MeshHandle.Invalid;

    public SampleStressSceneBuilder(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        SampleLightingMode normalLightingMode)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
        _normalLightingMode = normalLightingMode;
    }

    public SamplePerformanceScenarioSummary Apply(SamplePerformanceScenario scenario)
    {
        Clear();
        SampleLighting.Configure(_lightManager, _normalLightingMode);

        return scenario switch
        {
            SamplePerformanceScenario.ManyLights => BuildManyLights(),
            SamplePerformanceScenario.ManyMaterials => BuildManyMaterials(128),
            SamplePerformanceScenario.ManyTransparentObjects => BuildTransparentObjects(256),
            SamplePerformanceScenario.LargeMeshletCount => BuildLargeMeshletCount(512),
            SamplePerformanceScenario.FoliageLikeStaticInstances => BuildFoliageLikeStaticInstances(4096),
            SamplePerformanceScenario.FoliageDebugFallback => BuildFoliageDebugFallback(),
            SamplePerformanceScenario.ReflectionHeavy => BuildReflectionHeavy(),
            SamplePerformanceScenario.UploadBurst => BuildUploadBurst(),
            SamplePerformanceScenario.CombinedWorstCase => BuildCombinedWorstCase(),
            _ => new SamplePerformanceScenarioSummary(SamplePerformanceScenario.Normal, 0, _lightManager.LightCount, 0, 0, 0, "Normal sample scene")
        };
    }

    private SamplePerformanceScenarioSummary BuildManyLights()
    {
        _lightManager.ClearLights();
        const int count = 256;
        for (int i = 0; i < count; i++)
        {
            int x = i % 16;
            int z = i / 16;
            _lightManager.AddLight(new Light
            {
                Type = LightType.Point,
                Position = new System.Numerics.Vector3((x - 7.5f) * 3.0f, 2.0f + (i % 3) * 0.7f, z * 3.0f - 18.0f),
                Color = Hue(i),
                Intensity = 1.6f,
                Range = 8.0f,
                CastsShadows = i % 29 == 0,
                ShadowStrength = 0.5f,
                ShadowPriority = i % 7
            });
        }

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ManyLights, 0, count, 0, 0, 0, "256 deterministic point lights");
    }

    private SamplePerformanceScenarioSummary BuildManyMaterials(int count)
    {
        RenderObject? source = FindSourceObject();
        if (source?.Mesh is not MeshHandle mesh)
            return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ManyMaterials, 0, _lightManager.LightCount, 0, 0, 0, "No source mesh available");

        int side = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
        {
            MaterialHandle material = _materialManager.RegisterMaterial(CreateMaterial(i, alpha: 1.0f));
            AddObject(mesh, material, $"Perf.Material.{i}", GridTransform(i, side, 1.5f, -10.0f));
        }

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ManyMaterials, count, _lightManager.LightCount, count, 0, 0, $"{count} unique materials");
    }

    private SamplePerformanceScenarioSummary BuildTransparentObjects(int count)
    {
        MeshHandle mesh = GetQuadMesh();
        MaterialHandle material = _materialManager.RegisterMaterial(
            CreateMaterial(17, alpha: 0.35f),
            new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend,
                SurfaceFlags = MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows
            });

        int side = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
            AddObject(mesh, material, $"Perf.Transparent.{i}", GridTransform(i, side, 0.65f, -8.0f, 0.05f * (i % 16)));

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ManyTransparentObjects, count, _lightManager.LightCount, 1, count, 0, $"{count} layered transparent quads");
    }

    private SamplePerformanceScenarioSummary BuildLargeMeshletCount(int count)
    {
        RenderObject? source = FindSourceObject();
        if (source?.Mesh is not MeshHandle mesh || source.Material is not MaterialHandle material)
            return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.LargeMeshletCount, 0, _lightManager.LightCount, 0, 0, 0, "No source mesh/material available");

        int side = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
            AddObject(mesh, material, $"Perf.Meshlet.{i}", GridTransform(i, side, 2.0f, 8.0f));

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.LargeMeshletCount, count, _lightManager.LightCount, 0, 0, 0, $"{count} repeated mesh instances");
    }

    private SamplePerformanceScenarioSummary BuildFoliageLikeStaticInstances(int count)
    {
        MeshHandle mesh = GetQuadMesh();
        GPUMaterialData materialData = CreateMaterial(97, alpha: 1.0f);
        materialData.NormalScaleBias = new CoreVector4(
            materialData.NormalScaleBias.X,
            MaterialRenderMode.Mask.ToGpuAlphaModeCode(),
            0.5f,
            1.0f);
        MaterialHandle material = _materialManager.RegisterMaterial(
            materialData,
            new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.Mask,
                SurfaceFlags = MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows,
                AlphaCutoff = 0.5f
            });

        int side = (int)Math.Ceiling(Math.Sqrt(count));
        var matrices = new List<CoreMatrix4x4>(count);
        for (int i = 0; i < count; i++)
            matrices.Add(GridTransform(i, side, 0.45f, -12.0f, 0.25f + 0.025f * (i % 7)));

        var batch = new StaticInstanceBatch(matrices)
        {
            Name = "Perf.FoliageLikeStaticInstances",
            Mesh = mesh,
            Material = material,
            Visible = true
        };
        _scene.Add(batch);
        _staticBatches.Add(batch);

        return new SamplePerformanceScenarioSummary(
            SamplePerformanceScenario.FoliageLikeStaticInstances,
            count,
            _lightManager.LightCount,
            1,
            0,
            0,
            $"{count} masked static-instance foliage cards");
    }

    private SamplePerformanceScenarioSummary BuildFoliageDebugFallback()
    {
        MeshHandle mesh = GetQuadMesh();
        MaterialHandle grassMaterial = RegisterMaskedFoliageMaterial(211);
        MaterialHandle bushMaterial = RegisterMaskedFoliageMaterial(317);

        var grassPrototype = new FoliagePrototype
        {
            Name = "Sample.GrassPrototype",
            Mesh = mesh,
            Material = grassMaterial,
            GeometryMode = FoliageGeometryMode.ProceduralGrass
        };
        var bushPrototype = new FoliagePrototype
        {
            Name = "Sample.BushPrototype",
            Mesh = mesh,
            Material = bushMaterial,
            GeometryMode = FoliageGeometryMode.AuthoredMeshlets
        };
        _scene.Add(grassPrototype);
        _scene.Add(bushPrototype);
        _foliagePrototypes.Add(grassPrototype);
        _foliagePrototypes.Add(bushPrototype);

        var grassPatch = new FoliagePatch(
            grassPrototype,
            new BoundingBox(new CoreVector3(-12f, 0.35f, -26f), new CoreVector3(12f, 0.35f, -2f)))
        {
            Name = "Sample.GrassPatch",
            Density = 2.0f,
            Seed = 0xB10F_0001u,
            Visible = true
        };
        var bushPatch = new FoliagePatch(
            bushPrototype,
            new BoundingBox(new CoreVector3(-10f, 0.75f, -23f), new CoreVector3(10f, 0.75f, -5f)))
        {
            Name = "Sample.BushPatch",
            Density = 0.08f,
            Seed = 0xB10F_0002u,
            Visible = true
        };
        _scene.Add(grassPatch);
        _scene.Add(bushPatch);
        _foliagePatches.Add(grassPatch);
        _foliagePatches.Add(bushPatch);

        FoliageDebugFallbackResult fallback = _foliageManager.ApplyDebugFallback(
            _scene,
            new FoliageDebugFallbackOptions
            {
                MaxInstancesPerPatch = 128,
                InstanceScale = 0.85f
            });

        return new SamplePerformanceScenarioSummary(
            SamplePerformanceScenario.FoliageDebugFallback,
            fallback.GeneratedInstanceCount,
            _lightManager.LightCount,
            2,
            0,
            0,
            $"2 foliage patches, 2 prototypes, debug fallback batches={fallback.Batches.Count}, generated={fallback.GeneratedInstanceCount}, droppedByCap={fallback.DroppedInstanceCount}");
    }

    private SamplePerformanceScenarioSummary BuildReflectionHeavy()
    {
        const int probeCount = 24;
        for (int i = 0; i < probeCount; i++)
        {
            int x = i % 6;
            int z = i / 6;
            var probe = new ReflectionProbe
            {
                Name = $"Perf.ReflectionProbe.{i}",
                Position = new CoreVector3((x - 2.5f) * 4.0f, 2.0f, z * 5.0f - 8.0f),
                BoxExtents = new CoreVector3(3.5f, 2.5f, 3.5f),
                BlendDistance = 1.0f,
                Intensity = 1.0f,
                Priority = i
            };
            _scene.Add(probe);
            _probes.Add(probe);
        }

        SamplePerformanceScenarioSummary materials = BuildManyMaterials(32);
        return materials with
        {
            Scenario = SamplePerformanceScenario.ReflectionHeavy,
            ReflectionProbeCount = probeCount,
            Notes = "24 probes plus reflective material fixture pressure"
        };
    }

    private SamplePerformanceScenarioSummary BuildUploadBurst()
    {
        SamplePerformanceScenarioSummary materials = BuildManyMaterials(256);
        BuildTransparentObjects(128);
        return materials with
        {
            Scenario = SamplePerformanceScenario.UploadBurst,
            ObjectCount = _objects.Count,
            MaterialCount = 257,
            TransparentObjectCount = 128,
            Notes = "One-frame material/object upload burst"
        };
    }

    private SamplePerformanceScenarioSummary BuildCombinedWorstCase()
    {
        BuildManyLights();
        BuildManyMaterials(128);
        BuildTransparentObjects(256);
        BuildLargeMeshletCount(256);
        BuildReflectionHeavy();
        return new SamplePerformanceScenarioSummary(
            SamplePerformanceScenario.CombinedWorstCase,
            _objects.Count,
            _lightManager.LightCount,
            129,
            256,
            _probes.Count,
            "Combined deterministic stress set");
    }

    private void Clear()
    {
        _foliageManager.ClearDebugFallback(_scene);
        foreach (RenderObject renderObject in _objects)
            _scene.Remove(renderObject);
        foreach (ReflectionProbe probe in _probes)
            _scene.Remove(probe);
        foreach (StaticInstanceBatch batch in _staticBatches)
            _scene.Remove(batch);
        foreach (FoliagePatch patch in _foliagePatches)
            _scene.Remove(patch);
        foreach (FoliagePrototype prototype in _foliagePrototypes)
            _scene.Remove(prototype);

        _objects.Clear();
        _probes.Clear();
        _staticBatches.Clear();
        _foliagePatches.Clear();
        _foliagePrototypes.Clear();
    }

    private RenderObject? FindSourceObject()
    {
        foreach (RenderObject renderObject in _scene.RenderObjects)
        {
            if (!_objects.Contains(renderObject) && renderObject.Mesh is MeshHandle && renderObject.Material is MaterialHandle)
                return renderObject;
        }

        return null;
    }

    private void AddObject(MeshHandle mesh, MaterialHandle material, string name, CoreMatrix4x4 world)
    {
        var renderObject = new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = world,
            Visible = true
        };
        _scene.Add(renderObject);
        _objects.Add(renderObject);
    }

    private MeshHandle GetQuadMesh()
    {
        if (_quadMesh.IsValid)
            return _quadMesh;

        _quadMesh = _meshManager.RegisterMesh(
            [
                CreateVertex(-0.5f, -0.5f, 0f, 0f),
                CreateVertex(0.5f, -0.5f, 1f, 0f),
                CreateVertex(0.5f, 0.5f, 1f, 1f),
                CreateVertex(-0.5f, 0.5f, 0f, 1f)
            ],
            [0u, 1u, 2u, 0u, 2u, 3u]);
        return _quadMesh;
    }

    private static GPUMaterialData CreateMaterial(int seed, float alpha)
    {
        CoreVector3 color = new(
            0.25f + ((seed * 37) % 100) / 140.0f,
            0.2f + ((seed * 53) % 100) / 150.0f,
            0.18f + ((seed * 71) % 100) / 160.0f);

        return new GPUMaterialData
        {
            Albedo = new CoreVector4(color, alpha),
            Emissive = CoreVector4.Zero,
            NormalScaleBias = new CoreVector4(1f, alpha < 1f ? MaterialRenderMode.Blend.ToGpuAlphaModeCode() : MaterialRenderMode.Opaque.ToGpuAlphaModeCode(), 0.5f, alpha < 1f ? 1f : 0f),
            MetallicRoughnessAO = new CoreVector4((seed % 5) / 4.0f, 0.08f + (seed % 17) / 18.0f, 1f, 0f),
            BaseColorOffsetScale = new CoreVector4(0f, 0f, 1f, 1f),
            NormalOffsetScale = new CoreVector4(0f, 0f, 1f, 1f),
            MetallicRoughnessOffsetScale = new CoreVector4(0f, 0f, 1f, 1f),
            EmissiveOffsetScale = new CoreVector4(0f, 0f, 1f, 1f),
            TextureRotations = CoreVector4.Zero,
            TextureTexCoordSets = CoreVector4.Zero,
            AlbedoTextureIndex = BindlessIndex.DefaultWhiteTexture,
            NormalTextureIndex = BindlessIndex.DefaultNormalTexture,
            MetallicRoughnessTextureIndex = BindlessIndex.DefaultBlackTexture,
            EmissiveTextureIndex = BindlessIndex.DefaultBlackTexture,
            ExtensionDataIndex = -1
        };
    }

    private MaterialHandle RegisterMaskedFoliageMaterial(int seed)
    {
        GPUMaterialData materialData = CreateMaterial(seed, alpha: 1.0f);
        materialData.NormalScaleBias = new CoreVector4(
            materialData.NormalScaleBias.X,
            MaterialRenderMode.Mask.ToGpuAlphaModeCode(),
            0.45f,
            1.0f);

        return _materialManager.RegisterMaterial(
            materialData,
            new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.Mask,
                SurfaceFlags = MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows,
                AlphaCutoff = 0.45f
            });
    }

    private static CoreMatrix4x4 GridTransform(int index, int side, float spacing, float zOffset, float yOffset = 0f)
    {
        int x = index % side;
        int z = index / side;
        float half = (side - 1) * spacing * 0.5f;
        return CoreMatrix4x4.CreateTranslation(new CoreVector3(x * spacing - half, 0.35f + yOffset, z * spacing - half + zOffset));
    }

    private static GPUVertex CreateVertex(float x, float y, float u, float v)
    {
        return new GPUVertex
        {
            Position = new CoreVector3(x, y, 0f),
            Normal = CoreVector3.UnitZ,
            TexCoord = new Njulf.Core.Math.Vector2(u, v),
            Tangent = new CoreVector4(CoreVector3.UnitX, 1f),
            Color = GPUVertex.DefaultColor
        };
    }

    private static System.Numerics.Vector3 Hue(int seed)
    {
        return new System.Numerics.Vector3(
            0.45f + ((seed * 17) % 100) / 180.0f,
            0.45f + ((seed * 29) % 100) / 180.0f,
            0.45f + ((seed * 43) % 100) / 180.0f);
    }
}
