using System;
using System.Collections.Generic;
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
            SamplePerformanceScenario.ManyStaticObjects => BuildManyStaticObjects(),
            SamplePerformanceScenario.ManySkinnedObjects => BuildManySkinnedObjects(),
            SamplePerformanceScenario.DenseFoliage => BuildDenseFoliage(),
            SamplePerformanceScenario.ImpostorTransitionField => BuildImpostorTransitionField(),
            SamplePerformanceScenario.ManyLights => BuildManyLights(),
            SamplePerformanceScenario.ShadowHeavy => BuildShadowHeavy(),
            SamplePerformanceScenario.ManyMaterials => BuildManyMaterials(128),
            SamplePerformanceScenario.ManyTransparentObjects => BuildTransparentObjects(256),
            SamplePerformanceScenario.ParticleHeavy => BuildParticleHeavy(),
            SamplePerformanceScenario.PostProcessingDynamicResolution => BuildPostProcessingDynamicResolution(),
            SamplePerformanceScenario.LargeMeshletCount => BuildLargeMeshletCount(512),
            SamplePerformanceScenario.ReflectionHeavy => BuildReflectionHeavy(),
            SamplePerformanceScenario.UploadBurst => BuildUploadBurst(),
            SamplePerformanceScenario.CombinedWorstCase => BuildCombinedWorstCase(),
            _ => new SamplePerformanceScenarioSummary(SamplePerformanceScenario.Normal, 0, _lightManager.LightCount, 0, 0, 0, "Normal sample scene")
        };
    }

    private SamplePerformanceScenarioSummary BuildManyStaticObjects()
    {
        SamplePerformanceScenarioSummary summary = BuildLargeMeshletCount(1024);
        return summary with
        {
            Scenario = SamplePerformanceScenario.ManyStaticObjects,
            Notes = "1024 repeated static mesh instances"
        };
    }

    private SamplePerformanceScenarioSummary BuildManySkinnedObjects()
    {
        RenderObject? source = FindSourceObject();
        if (source?.Mesh is not MeshHandle mesh || source.Material is not MaterialHandle material)
            return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ManySkinnedObjects, 0, _lightManager.LightCount, 0, 0, 0, "No source mesh/material available");

        const int count = 128;
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
        {
            var renderObject = new SkinnedRenderObject(mesh, material)
            {
                Name = $"Perf.Skinned.{i}",
                WorldMatrix = GridTransform(i, side, 1.4f, -4.0f),
                Visible = true,
                SkinningEnabled = true,
                SkinIndex = 0
            };
            _scene.Add(renderObject);
            _objects.Add(renderObject);
        }

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ManySkinnedObjects, count, _lightManager.LightCount, 1, 0, 0, "128 deterministic skinned-object submissions");
    }

    private SamplePerformanceScenarioSummary BuildDenseFoliage()
    {
        MeshHandle mesh = GetQuadMesh();
        MaterialHandle material = _materialManager.RegisterMaterial(CreateMaterial(91, alpha: 1.0f));
        const int count = 768;
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
            AddObject(mesh, material, $"Perf.Foliage.{i}", GridTransform(i, side, 0.45f, -12.0f, 0.2f + 0.02f * (i % 5)));

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.DenseFoliage, count, _lightManager.LightCount, 1, 0, 0, "768 dense foliage-card instances");
    }

    private SamplePerformanceScenarioSummary BuildImpostorTransitionField()
    {
        RenderObject? source = FindSourceObject();
        if (source?.Mesh is not MeshHandle mesh || source.Material is not MaterialHandle material)
            return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ImpostorTransitionField, 0, _lightManager.LightCount, 0, 0, 0, "No source mesh/material available");

        const int count = 384;
        for (int i = 0; i < count; i++)
        {
            int ring = i / 48;
            int segment = i % 48;
            float angle = segment / 48.0f * MathF.Tau;
            float radius = 6.0f + ring * 2.0f;
            CoreMatrix4x4 transform = CoreMatrix4x4.CreateTranslation(new CoreVector3(
                MathF.Cos(angle) * radius,
                0.35f,
                MathF.Sin(angle) * radius));
            AddObject(mesh, material, $"Perf.ImpostorTransition.{i}", transform);
        }

        return new SamplePerformanceScenarioSummary(SamplePerformanceScenario.ImpostorTransitionField, count, _lightManager.LightCount, 1, 0, 0, "384 radial LOD/impostor transition candidates");
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

    private SamplePerformanceScenarioSummary BuildShadowHeavy()
    {
        _lightManager.ClearLights();
        const int spotCount = 32;
        const int pointCount = 16;
        for (int i = 0; i < spotCount; i++)
        {
            int x = i % 8;
            int z = i / 8;
            _lightManager.AddLight(new Light
            {
                Type = LightType.Spot,
                Position = new System.Numerics.Vector3((x - 3.5f) * 3.0f, 6.0f, z * 4.0f - 8.0f),
                Direction = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.15f * (x - 3.5f), -1.0f, 0.1f)),
                Color = Hue(i + 211),
                Intensity = 2.5f,
                Range = 14.0f,
                SpotAngle = 0.75f,
                CastsShadows = true,
                ShadowStrength = 0.8f,
                ShadowPriority = i
            });
        }

        for (int i = 0; i < pointCount; i++)
        {
            _lightManager.AddLight(new Light
            {
                Type = LightType.Point,
                Position = new System.Numerics.Vector3((i % 4 - 1.5f) * 5.0f, 2.5f, i / 4 * 4.0f - 6.0f),
                Color = Hue(i + 307),
                Intensity = 1.8f,
                Range = 9.0f,
                CastsShadows = true,
                ShadowStrength = 0.7f,
                ShadowPriority = i
            });
        }

        SamplePerformanceScenarioSummary staticObjects = BuildManyStaticObjects();
        return staticObjects with
        {
            Scenario = SamplePerformanceScenario.ShadowHeavy,
            LightCount = spotCount + pointCount,
            Notes = "48 shadow-casting local lights plus static caster field"
        };
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

    private SamplePerformanceScenarioSummary BuildParticleHeavy()
    {
        SamplePerformanceScenarioSummary transparent = BuildTransparentObjects(1024);
        return transparent with
        {
            Scenario = SamplePerformanceScenario.ParticleHeavy,
            Notes = "1024 billboard particles through the transparent particle-like path"
        };
    }

    private SamplePerformanceScenarioSummary BuildPostProcessingDynamicResolution()
    {
        BuildManyLights();
        BuildTransparentObjects(128);
        BuildReflectionHeavy();
        return new SamplePerformanceScenarioSummary(
            SamplePerformanceScenario.PostProcessingDynamicResolution,
            _objects.Count,
            _lightManager.LightCount,
            33,
            128,
            _probes.Count,
            "Post-processing pressure scene with lights, transparency, and reflection probes");
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
        foreach (RenderObject renderObject in _objects)
            _scene.Remove(renderObject);
        foreach (ReflectionProbe probe in _probes)
            _scene.Remove(probe);

        _objects.Clear();
        _probes.Clear();
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

        _quadMesh = SampleProceduralMeshAssets.Register(
            _meshManager,
            "sample/stress-quad",
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
