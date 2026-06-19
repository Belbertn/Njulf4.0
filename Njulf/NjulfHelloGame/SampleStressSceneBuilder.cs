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
    private readonly List<RenderObject> _hiddenRenderObjects = new();
    private readonly FoliageManager _foliageManager = new();
    private MeshHandle _quadMesh = MeshHandle.Invalid;
    private MeshHandle _groundPlaneMesh = MeshHandle.Invalid;
    private MeshHandle _treeTrunkMesh = MeshHandle.Invalid;
    private MeshHandle _treeCanopyMesh = MeshHandle.Invalid;
    private MeshHandle _authoredGrassClumpMesh = MeshHandle.Invalid;

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
            SamplePerformanceScenario.ForestFoliage => BuildForestFoliage(),
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

    private SamplePerformanceScenarioSummary BuildForestFoliage()
    {
        HideBaseRenderObjects();
        SampleLighting.Configure(_lightManager, SampleLightingMode.DirectionalKey);

        MaterialHandle groundMaterial = _materialManager.RegisterMaterial(CreateGroundMaterial());
        AddObject(
            GetGroundPlaneMesh(),
            groundMaterial,
            "Forest.Ground",
            CoreMatrix4x4.Identity);

        CoreVector3[] treePositions =
        [
            new(-3.5f, 0f, 0.0f),
            new(-1.0f, 0f, -4.0f),
            new(2.5f, 0f, -2.0f),
            new(4.0f, 0f, -7.0f),
            new(0.5f, 0f, -10.0f)
        ];
        float[] treeScales = [1.15f, 0.95f, 1.25f, 0.9f, 1.05f];

        MaterialHandle trunkMaterial = _materialManager.RegisterMaterial(CreateTrunkMaterial());
        MaterialHandle canopyMaterial = _materialManager.RegisterMaterial(CreateCanopyMaterial());
        FoliagePrototype treeCanopyPrototype = CreateAuthoredFoliagePrototype(
            "Forest.TreeCanopy",
            GetTreeCanopyMesh(),
            canopyMaterial,
            lod0: 18f,
            lod1: 45f,
            lod2: 120f,
            windStrength: 0.06f);
        for (int treeIndex = 0; treeIndex < treePositions.Length; treeIndex++)
        {
            AddGeneratedTree(treePositions[treeIndex], treeScales[treeIndex], treeIndex, trunkMaterial, treeCanopyPrototype);
        }

        CoreVector3[] grassPositions =
        [
            new(-5.0f, 0f, 2.0f),
            new(-2.5f, 0f, 1.0f),
            new(0.0f, 0f, 0.5f),
            new(2.5f, 0f, 1.5f),
            new(5.0f, 0f, 0.0f),
            new(-4.0f, 0f, -5.0f),
            new(-1.5f, 0f, -6.5f),
            new(1.5f, 0f, -5.5f),
            new(3.5f, 0f, -8.0f),
            new(0.0f, 0f, -11.0f)
        ];
        MaterialHandle authoredGrassMaterial = RegisterMaskedFoliageMaterial(317);
        FoliagePrototype authoredGrassPrototype = CreateAuthoredFoliagePrototype(
            "Forest.AuthoredGrassClump",
            GetAuthoredGrassClumpMesh(),
            authoredGrassMaterial,
            lod0: 10f,
            lod1: 22f,
            lod2: 70f,
            windStrength: 0.16f);
        AddGeneratedAuthoredGrassClumps(authoredGrassPrototype, grassPositions);
        AddProceduralGrassPatch();

        return new SamplePerformanceScenarioSummary(
            SamplePerformanceScenario.ForestFoliage,
            _objects.Count,
            _lightManager.LightCount,
            _foliagePrototypes.Count,
            0,
            0,
            $"Forest clearing: trees={treePositions.Length}, authoredGrassClumps={grassPositions.Length}, foliagePatches={_foliagePatches.Count}");
    }

    private void AddGeneratedTree(
        CoreVector3 position,
        float scale,
        int treeIndex,
        MaterialHandle trunkMaterial,
        FoliagePrototype canopyPrototype)
    {
        CoreVector3 trunkPosition = new(position.X, position.Y + 0.95f * scale, position.Z);
        CoreMatrix4x4 trunkWorld =
            CoreMatrix4x4.CreateScale(new CoreVector3(0.32f * scale, 1.9f * scale, 0.32f * scale)) *
            CoreMatrix4x4.CreateTranslation(trunkPosition);
        AddObject(GetTreeTrunkMesh(), trunkMaterial, $"Forest.TreeTrunk.{treeIndex}", trunkWorld);

        CoreVector3 canopyPosition = new(position.X, position.Y + 2.25f * scale, position.Z);
        CoreMatrix4x4 canopyWorld =
            CoreMatrix4x4.CreateScale(new CoreVector3(1.25f * scale)) *
            CoreMatrix4x4.CreateTranslation(canopyPosition);
        AddAuthoredFoliagePatch(
            canopyPrototype,
            GetTreeCanopyMesh(),
            canopyWorld,
            $"Forest.TreeCanopy.{treeIndex}",
            density: 1f,
            seed: 0xF0A0_1000u + (uint)treeIndex);
    }

    private void AddGeneratedAuthoredGrassClumps(FoliagePrototype prototype, IReadOnlyList<CoreVector3> positions)
    {
        MeshHandle mesh = GetAuthoredGrassClumpMesh();
        for (int i = 0; i < positions.Count; i++)
        {
            float scale = 0.75f + (i % 4) * 0.08f;
            CoreMatrix4x4 world =
                CoreMatrix4x4.CreateScale(new CoreVector3(scale)) *
                CoreMatrix4x4.CreateTranslation(positions[i]);

            AddAuthoredFoliagePatch(
                prototype,
                mesh,
                world,
                $"Forest.AuthoredGrass.{i}",
                density: 1f,
                seed: 0xF0A0_2000u + (uint)i);
        }
    }

    private void AddProceduralGrassPatch()
    {
        MeshHandle mesh = GetQuadMesh();
        MaterialHandle material = RegisterMaskedFoliageMaterial(503);
        var prototype = new FoliagePrototype
        {
            Name = "Forest.ProceduralGrass",
            Mesh = mesh,
            Material = material,
            GeometryMode = FoliageGeometryMode.ProceduralGrass
        };
        prototype.CardHeight = 0.42f;
        prototype.CardWidth = 0.045f;
        prototype.Lod.Lod0Distance = 12f;
        prototype.Lod.Lod1Distance = 28f;
        prototype.Lod.Lod2Distance = 80f;
        prototype.Wind.Strength = 0.13f;
        prototype.Wind.Frequency = 0.9f;
        prototype.Wind.Flutter = 0.18f;
        prototype.Lighting.WrapDiffuse = 0.35f;
        prototype.Lighting.Backlight = 0.18f;
        _scene.Add(prototype);
        _foliagePrototypes.Add(prototype);

        var patch = new FoliagePatch(
            prototype,
            new BoundingBox(new CoreVector3(-8f, 0.02f, -14f), new CoreVector3(8f, 0.02f, 3f)))
        {
            Name = "Forest.ProceduralGrassPatch",
            Density = 4.0f,
            Seed = 0xF0A0_3000u,
            Visible = true
        };
        _scene.Add(patch);
        _foliagePatches.Add(patch);
    }

    private FoliagePrototype CreateAuthoredFoliagePrototype(
        string name,
        MeshHandle mesh,
        MaterialHandle material,
        float lod0,
        float lod1,
        float lod2,
        float windStrength)
    {
        var prototype = new FoliagePrototype
        {
            Name = name,
            Mesh = mesh,
            Material = material,
            GeometryMode = FoliageGeometryMode.AuthoredMeshlets
        };
        prototype.AuthoredMeshletStride = 1u;
        prototype.Lod.Lod0Distance = lod0;
        prototype.Lod.Lod1Distance = lod1;
        prototype.Lod.Lod2Distance = lod2;
        prototype.Wind.Strength = windStrength;
        prototype.Wind.Frequency = 0.55f;
        prototype.Wind.Flutter = 0.1f;
        prototype.Lighting.WrapDiffuse = 0.42f;
        prototype.Lighting.Backlight = 0.25f;
        _scene.Add(prototype);
        _foliagePrototypes.Add(prototype);
        return prototype;
    }

    private void AddAuthoredFoliagePatch(
        FoliagePrototype prototype,
        MeshHandle mesh,
        CoreMatrix4x4 world,
        string name,
        float density,
        uint seed)
    {
        var patch = new FoliagePatch(prototype, GetTransformedMeshBounds(mesh, world))
        {
            Name = name,
            Density = density,
            Seed = seed,
            InstancePosition = ExtractTranslation(world),
            InstanceScale = ExtractUniformScale(world),
            Visible = true
        };
        _scene.Add(patch);
        _foliagePatches.Add(patch);
    }

    private BoundingBox GetTransformedMeshBounds(MeshHandle mesh, CoreMatrix4x4 world)
    {
        MeshInfo meshInfo = _meshManager.GetMeshInfo(mesh);
        return TransformBounds(
            new BoundingBox(
                ToCoreVector(meshInfo.BoundingBoxMin),
                ToCoreVector(meshInfo.BoundingBoxMax)),
            world);
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

        foreach (RenderObject renderObject in _hiddenRenderObjects)
            renderObject.Visible = true;
        _hiddenRenderObjects.Clear();
    }

    private void HideBaseRenderObjects()
    {
        foreach (RenderObject renderObject in _scene.RenderObjects)
        {
            if (_objects.Contains(renderObject) || !renderObject.Visible)
                continue;

            renderObject.Visible = false;
            _hiddenRenderObjects.Add(renderObject);
        }
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

    private MeshHandle GetGroundPlaneMesh()
    {
        if (_groundPlaneMesh.IsValid)
            return _groundPlaneMesh;

        const float halfSize = 15f;
        _groundPlaneMesh = _meshManager.RegisterMesh(
            [
                CreateGroundVertex(-halfSize, -halfSize, 0f, 0f),
                CreateGroundVertex(halfSize, -halfSize, 1f, 0f),
                CreateGroundVertex(halfSize, halfSize, 1f, 1f),
                CreateGroundVertex(-halfSize, halfSize, 0f, 1f)
            ],
            [0u, 2u, 1u, 0u, 3u, 2u]);
        return _groundPlaneMesh;
    }

    private MeshHandle GetTreeTrunkMesh()
    {
        if (_treeTrunkMesh.IsValid)
            return _treeTrunkMesh;

        _treeTrunkMesh = _meshManager.RegisterMesh(
            [
                CreateTreeVertex(-0.5f, -0.5f, -0.5f, CoreVector3.UnitZ),
                CreateTreeVertex(0.5f, -0.5f, -0.5f, CoreVector3.UnitZ),
                CreateTreeVertex(0.5f, 0.5f, -0.5f, CoreVector3.UnitZ),
                CreateTreeVertex(-0.5f, 0.5f, -0.5f, CoreVector3.UnitZ),
                CreateTreeVertex(-0.5f, -0.5f, 0.5f, -CoreVector3.UnitZ),
                CreateTreeVertex(0.5f, -0.5f, 0.5f, -CoreVector3.UnitZ),
                CreateTreeVertex(0.5f, 0.5f, 0.5f, -CoreVector3.UnitZ),
                CreateTreeVertex(-0.5f, 0.5f, 0.5f, -CoreVector3.UnitZ)
            ],
            [
                0u, 2u, 1u, 0u, 3u, 2u,
                4u, 5u, 6u, 4u, 6u, 7u,
                0u, 1u, 5u, 0u, 5u, 4u,
                1u, 2u, 6u, 1u, 6u, 5u,
                2u, 3u, 7u, 2u, 7u, 6u,
                3u, 0u, 4u, 3u, 4u, 7u
            ]);
        return _treeTrunkMesh;
    }

    private MeshHandle GetTreeCanopyMesh()
    {
        if (_treeCanopyMesh.IsValid)
            return _treeCanopyMesh;

        _treeCanopyMesh = _meshManager.RegisterMesh(
            [
                CreateTreeVertex(0f, 1.0f, 0f, CoreVector3.UnitY),
                CreateTreeVertex(-1.0f, 0.15f, -1.0f, new CoreVector3(-0.5f, 0.7f, -0.5f).Normalized()),
                CreateTreeVertex(1.0f, 0.15f, -1.0f, new CoreVector3(0.5f, 0.7f, -0.5f).Normalized()),
                CreateTreeVertex(1.0f, 0.15f, 1.0f, new CoreVector3(0.5f, 0.7f, 0.5f).Normalized()),
                CreateTreeVertex(-1.0f, 0.15f, 1.0f, new CoreVector3(-0.5f, 0.7f, 0.5f).Normalized()),
                CreateTreeVertex(0f, -0.65f, 0f, -CoreVector3.UnitY)
            ],
            [
                0u, 1u, 2u,
                0u, 2u, 3u,
                0u, 3u, 4u,
                0u, 4u, 1u,
                5u, 2u, 1u,
                5u, 3u, 2u,
                5u, 4u, 3u,
                5u, 1u, 4u
            ]);
        return _treeCanopyMesh;
    }

    private MeshHandle GetAuthoredGrassClumpMesh()
    {
        if (_authoredGrassClumpMesh.IsValid)
            return _authoredGrassClumpMesh;

        var vertices = new List<GPUVertex>(24);
        var indices = new List<uint>(24);
        for (int blade = 0; blade < 8; blade++)
        {
            float angle = blade * (MathF.PI * 2f / 8f);
            float c = MathF.Cos(angle);
            float s = MathF.Sin(angle);
            float height = 0.45f + 0.08f * (blade % 3);
            float width = 0.045f + 0.012f * (blade % 2);
            CoreVector3 side = new(c, 0f, s);
            CoreVector3 normal = new(-s, 0.45f, c);
            normal = normal.Normalized();
            CoreVector3 root = side * (0.07f * (blade % 4));
            CoreVector3 bend = new(s * 0.08f, 0f, -c * 0.08f);
            uint baseVertex = (uint)vertices.Count;
            vertices.Add(CreateTreeVertex(root.X - side.X * width, 0f, root.Z - side.Z * width, normal));
            vertices.Add(CreateTreeVertex(root.X + side.X * width, 0f, root.Z + side.Z * width, normal));
            vertices.Add(CreateTreeVertex(root.X + bend.X, height, root.Z + bend.Z, normal));
            indices.Add(baseVertex + 0u);
            indices.Add(baseVertex + 1u);
            indices.Add(baseVertex + 2u);
        }

        _authoredGrassClumpMesh = _meshManager.RegisterMesh(vertices.ToArray(), indices.ToArray());
        return _authoredGrassClumpMesh;
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

    private static GPUMaterialData CreateGroundMaterial()
    {
        GPUMaterialData material = CreateMaterial(641, alpha: 1.0f);
        material.Albedo = new CoreVector4(0.18f, 0.28f, 0.12f, 1f);
        material.MetallicRoughnessAO = new CoreVector4(0f, 0.82f, 1f, 0f);
        return material;
    }

    private static GPUMaterialData CreateTrunkMaterial()
    {
        GPUMaterialData material = CreateMaterial(811, alpha: 1.0f);
        material.Albedo = new CoreVector4(0.26f, 0.13f, 0.07f, 1f);
        material.MetallicRoughnessAO = new CoreVector4(0f, 0.9f, 1f, 0f);
        return material;
    }

    private static GPUMaterialData CreateCanopyMaterial()
    {
        GPUMaterialData material = CreateMaterial(947, alpha: 1.0f);
        material.Albedo = new CoreVector4(0.12f, 0.42f, 0.16f, 1f);
        material.MetallicRoughnessAO = new CoreVector4(0f, 0.74f, 1f, 0f);
        material.NormalScaleBias = new CoreVector4(
            material.NormalScaleBias.X,
            MaterialRenderMode.Mask.ToGpuAlphaModeCode(),
            0.35f,
            1f);
        return material;
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

    private static GPUVertex CreateGroundVertex(float x, float z, float u, float v)
    {
        return new GPUVertex
        {
            Position = new CoreVector3(x, 0f, z),
            Normal = CoreVector3.UnitY,
            TexCoord = new Njulf.Core.Math.Vector2(u, v),
            Tangent = new CoreVector4(CoreVector3.UnitX, 1f),
            Color = GPUVertex.DefaultColor
        };
    }

    private static GPUVertex CreateTreeVertex(float x, float y, float z, CoreVector3 normal)
    {
        return new GPUVertex
        {
            Position = new CoreVector3(x, y, z),
            Normal = normal,
            TexCoord = new Njulf.Core.Math.Vector2(0.5f + x * 0.5f, 0.5f + z * 0.5f),
            Tangent = new CoreVector4(CoreVector3.UnitX, 1f),
            Color = GPUVertex.DefaultColor
        };
    }

    private static BoundingBox TransformBounds(BoundingBox bounds, CoreMatrix4x4 world)
    {
        CoreVector3 min = bounds.Min;
        CoreVector3 max = bounds.Max;
        Span<CoreVector3> corners = stackalloc CoreVector3[8]
        {
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z),
            new(max.X, max.Y, max.Z)
        };

        CoreVector3 transformedMin = TransformPoint(corners[0], world);
        CoreVector3 transformedMax = transformedMin;
        for (int i = 1; i < corners.Length; i++)
        {
            CoreVector3 point = TransformPoint(corners[i], world);
            transformedMin = CoreVector3.Min(transformedMin, point);
            transformedMax = CoreVector3.Max(transformedMax, point);
        }

        return new BoundingBox(transformedMin, transformedMax);
    }

    private static CoreVector3 TransformPoint(CoreVector3 point, CoreMatrix4x4 world)
    {
        return new CoreVector3(
            point.X * world.M11 + point.Y * world.M21 + point.Z * world.M31 + world.M41,
            point.X * world.M12 + point.Y * world.M22 + point.Z * world.M32 + world.M42,
            point.X * world.M13 + point.Y * world.M23 + point.Z * world.M33 + world.M43);
    }

    private static CoreVector3 ExtractTranslation(CoreMatrix4x4 world)
    {
        return new CoreVector3(world.M41, world.M42, world.M43);
    }

    private static float ExtractUniformScale(CoreMatrix4x4 world)
    {
        float x = MathF.Sqrt(world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13);
        float y = MathF.Sqrt(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23);
        float z = MathF.Sqrt(world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33);
        float scale = (x + y + z) / 3f;
        return float.IsFinite(scale) && scale > 0f ? scale : 1f;
    }

    private static CoreVector3 ToCoreVector(System.Numerics.Vector3 value)
    {
        return new CoreVector3(value.X, value.Y, value.Z);
    }

    private static System.Numerics.Vector3 Hue(int seed)
    {
        return new System.Numerics.Vector3(
            0.45f + ((seed * 17) % 100) / 180.0f,
            0.45f + ((seed * 29) % 100) / 180.0f,
            0.45f + ((seed * 43) % 100) / 180.0f);
    }
}
