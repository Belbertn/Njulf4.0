using System;
using System.Collections.Generic;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

internal static class SampleVfxShowcaseScene
{
    private static readonly Guid GroundMistId =
        new("bc2eab6d-2cf0-4af2-8c8e-f3d05bbb7291");
    private static readonly Guid SpotlightHazeId =
        new("38962b95-5100-4cba-928b-513043134778");
    private static readonly Guid DenseSmokeBankId =
        new("5da3db94-4795-4790-a811-9b929d44f2ae");

    internal static void ConfigurePreInitializationSettings(
        RenderSettings settings,
        RenderQualityPreset? explicitQualityPreset)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (explicitQualityPreset.HasValue)
            settings.ApplyQualityPreset(explicitQualityPreset.Value);

        ConfigureRenderSettings(settings);
        ApplyPostQualityPreset(settings);
    }

    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.SimpleDdgiFogEnabled = true;
        gi.SimpleDdgiDirectionalFogEnabled = true;
        gi.SimpleDdgiParticlesEnabled = true;
        gi.SimpleDdgiDirectionalRadianceMode =
            SimpleDdgiDirectionalRadianceMode.L2;
        // Exact receiver feedback needs the analytic fog producer. The
        // showcase instead uses its authored receiver volume and camera rings
        // so the froxel path remains deterministic.
        gi.SimpleDdgiReceiverFeedbackMode = SimpleDdgiReceiverFeedbackMode.Off;
        gi.SimpleDdgiAuthoredVolumes.Clear();
        gi.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            new CoreVector3(-5f, -0.25f, -3.5f),
            new CoreVector3(5f, 5f, 3.5f),
            1f,
            new CoreVector3(0.25f, 0.5f, 0.25f),
            SimpleDdgiVolumePurpose.ReceiverHero,
            priority: 10));

        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.12f;
        settings.Environment.DiffuseIntensity = 0.22f;
        settings.Environment.SpecularIntensity = 0.15f;
        settings.Reflections.Enabled = false;
        settings.Fog.Mode = FogMode.Height;
        settings.Fog.ColorMode = FogColorMode.SkyAndConstantBlend;
        settings.Fog.Color = new CoreVector3(0.58f, 0.68f, 0.82f);
        settings.Fog.ColorBlend = 0.35f;
        settings.Fog.Height = 1.25f;
        settings.Fog.HeightFalloff = 0.7f;
        settings.Fog.HeightDensity = 0.03f;

        VolumetricFogSettings volumetric = settings.Fog.Volumetric;
        volumetric.MaxDistance = 36f;
        volumetric.BaseExtinctionPerMeter = 0.008f;
        volumetric.HeightExtinctionPerMeter = 0.045f;
        volumetric.Height = 1.25f;
        volumetric.HeightFalloff = 0.7f;
        volumetric.ScatteringAlbedo =
            new CoreVector3(0.82f, 0.88f, 0.96f);
        volumetric.Anisotropy = 0.38f;
        volumetric.GlobalWind = new CoreVector3(0.12f, 0f, 0.03f);
        volumetric.NoiseScale = 0.18f;
        volumetric.NoiseStrength = 0.25f;
        volumetric.NoiseContrast = 1.2f;
        volumetric.SelfShadowDistance = 18f;
        volumetric.TemporalHistoryWeight = 0.90f;
        volumetric.MultipleScatteringEnergyLimit = 0.35f;

        settings.Bloom.Enabled = true;
        settings.Bloom.Intensity = 0.18f;
        settings.Particles.Enabled = true;
        settings.AmbientOcclusion.Enabled = true;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 0.05f;

        ApplyPostQualityPreset(settings);
    }

    /// <summary>
    /// Reasserts scene intent after a quality change. Renderer-owned profile
    /// qualification is intentionally not modified here.
    /// </summary>
    public static void ApplyPostQualityPreset(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Fog.Enabled = true;
        settings.Fog.Mode = FogMode.Height;
        settings.Fog.Technique = FogTechnique.Froxel;
        settings.Reflections.Enabled = false;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 0.05f;
    }

    public static IReadOnlyList<ParticleEffectInstance> Configure(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (meshManager == null)
            throw new ArgumentNullException(nameof(meshManager));
        if (materialManager == null)
            throw new ArgumentNullException(nameof(materialManager));

        scene.Name = "Njulf Volumetric VFX Showcase";
        scene.AmbientLight = new Njulf.Core.Math.Color(0.035f, 0.04f, 0.05f, 1f);

        MeshHandle floorMesh = meshManager.RegisterMesh(CreateFloorVertices(), CreateFloorIndices());
        scene.Add(new RenderObject(floorMesh, CreateFloorMaterial(materialManager))
        {
            Name = "VfxShowcase.Floor",
            WorldMatrix = CoreMatrix4x4.Identity,
            Visible = true
        });

        MeshHandle pillarMesh = meshManager.RegisterMesh(
            CreateBoxVertices(),
            CreateBoxIndices());
        MaterialHandle wallMaterial = CreateWallMaterial(materialManager);
        AddBoxObject(scene, pillarMesh, wallMaterial,
            "VfxShowcase.BackWall",
            new CoreVector3(10f, 5f, 0.2f),
            new CoreVector3(0f, 2.5f, -3.1f));
        AddBoxObject(scene, pillarMesh, wallMaterial,
            "VfxShowcase.LeftWall",
            new CoreVector3(0.2f, 5f, 6.2f),
            new CoreVector3(-5.1f, 2.5f, 0f));
        AddBoxObject(scene, pillarMesh, wallMaterial,
            "VfxShowcase.RightWall",
            new CoreVector3(0.2f, 5f, 6.2f),
            new CoreVector3(5.1f, 2.5f, 0f));
        AddBoxObject(scene, pillarMesh, wallMaterial,
            "VfxShowcase.Ceiling",
            new CoreVector3(10f, 0.2f, 6.2f),
            new CoreVector3(0f, 5.1f, 0f));
        scene.Add(new RenderObject(
            pillarMesh,
            CreatePillarMaterial(materialManager))
        {
            Name = "VfxShowcase.ShadowPillar",
            WorldMatrix =
                CoreMatrix4x4.CreateScale(new CoreVector3(0.7f, 2.5f, 0.7f)) *
                CoreMatrix4x4.CreateTranslation(new CoreVector3(0f, 1.25f, 0.45f)),
            Visible = true
        });

        ConfigureDensityVolumes(scene);

        Console.WriteLine(
            "Volumetric showcase controls: X cycles fog debug views; Ctrl+X changes " +
            "projection; Ctrl+Shift+X restores the beauty view; Ctrl+Up/Down " +
            "changes the slice; Z toggles fog; " +
            "Backspace restarts particle sources with their fixed seeds.");

        return SampleVfxEffects.Configure(scene);
    }

    internal static void ConfigureDensityVolumes(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        scene.Add(new VolumetricDensityVolume
        {
            Id = GroundMistId,
            Name = "VfxShowcase.Volumetrics.GroundMist",
            Shape = VolumetricDensityVolumeShape.Box,
            Position = new CoreVector3(0f, 0.42f, 0f),
            BoxExtents = new CoreVector3(4.8f, 0.45f, 2.8f),
            EdgeFade = 0.35f,
            DensityMultiplier = 1f,
            ExtinctionPerMeter = 0.075f,
            ScatteringAlbedo = new CoreVector3(0.78f, 0.86f, 0.95f),
            Anisotropy = 0.25f,
            Priority = 10,
            NoiseScale = 0.35f,
            NoiseStrength = 0.50f,
            NoiseContrast = 1.4f,
            NoiseSeed = 1301u,
            FlowVelocity = new CoreVector3(0.18f, 0f, 0.03f)
        });
        scene.Add(new VolumetricDensityVolume
        {
            Id = SpotlightHazeId,
            Name = "VfxShowcase.Volumetrics.SpotlightHaze",
            Shape = VolumetricDensityVolumeShape.Box,
            Position = new CoreVector3(-0.35f, 1.65f, 0.35f),
            BoxExtents = new CoreVector3(1.45f, 1.65f, 3.0f),
            EdgeFade = 0.5f,
            DensityMultiplier = 1f,
            ExtinctionPerMeter = 0.13f,
            ScatteringAlbedo = new CoreVector3(0.95f, 0.86f, 0.72f),
            Anisotropy = 0.55f,
            Priority = 20,
            NoiseScale = 0.5f,
            NoiseStrength = 0.35f,
            NoiseContrast = 1.25f,
            NoiseSeed = 2402u,
            FlowVelocity = new CoreVector3(0.05f, 0.03f, -0.08f)
        });
        scene.Add(new VolumetricDensityVolume
        {
            Id = DenseSmokeBankId,
            Name = "VfxShowcase.Volumetrics.DenseSmokeBank",
            Shape = VolumetricDensityVolumeShape.Sphere,
            Position = new CoreVector3(1.8f, 1.1f, 0.2f),
            Radius = 1.35f,
            EdgeFade = 0.45f,
            DensityMultiplier = 1.55f,
            ExtinctionPerMeter = 0.32f,
            ScatteringAlbedo = new CoreVector3(0.62f, 0.68f, 0.76f),
            Anisotropy = 0.4f,
            Priority = 30,
            NoiseScale = 0.8f,
            NoiseStrength = 0.75f,
            NoiseContrast = 1.8f,
            NoiseSeed = 3503u,
            FlowVelocity = new CoreVector3(0f, 0.22f, -0.05f)
        });
    }

    private static MaterialHandle CreateFloorMaterial(MaterialManager materialManager)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = "VfxShowcase.Floor",
            BaseColorFactor = new CoreVector4(0.16f, 0.17f, 0.18f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 0.82f
        });
    }

    private static MaterialHandle CreatePillarMaterial(MaterialManager materialManager)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = "VfxShowcase.ShadowPillar",
            BaseColorFactor = new CoreVector4(0.08f, 0.085f, 0.095f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 0.72f
        });
    }

    private static MaterialHandle CreateWallMaterial(MaterialManager materialManager)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = "VfxShowcase.Walls",
            BaseColorFactor = new CoreVector4(0.11f, 0.12f, 0.14f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 0.9f
        });
    }

    private static void AddBoxObject(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreVector3 scale,
        CoreVector3 position)
    {
        scene.Add(new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = CoreMatrix4x4.CreateScale(scale) *
                CoreMatrix4x4.CreateTranslation(position),
            Visible = true
        });
    }

    private static GPUVertex[] CreateFloorVertices()
    {
        const float halfWidth = 5.0f;
        const float nearZ = -3.0f;
        const float farZ = 3.0f;

        return
        [
            CreateFloorVertex(-halfWidth, 0f, nearZ, 0f, 0f),
            CreateFloorVertex(halfWidth, 0f, nearZ, 1f, 0f),
            CreateFloorVertex(halfWidth, 0f, farZ, 1f, 1f),
            CreateFloorVertex(-halfWidth, 0f, farZ, 0f, 1f)
        ];
    }

    private static uint[] CreateFloorIndices()
    {
        return [0u, 2u, 1u, 0u, 3u, 2u];
    }

    private static GPUVertex[] CreateBoxVertices()
    {
        var vertices = new List<GPUVertex>(24);
        AddBoxFace(vertices, new CoreVector3(0f, 0f, 0.5f), CoreVector3.UnitZ, CoreVector3.UnitX, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(0f, 0f, -0.5f), -CoreVector3.UnitZ, -CoreVector3.UnitX, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(0.5f, 0f, 0f), CoreVector3.UnitX, -CoreVector3.UnitZ, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(-0.5f, 0f, 0f), -CoreVector3.UnitX, CoreVector3.UnitZ, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(0f, 0.5f, 0f), CoreVector3.UnitY, CoreVector3.UnitX, -CoreVector3.UnitZ);
        AddBoxFace(vertices, new CoreVector3(0f, -0.5f, 0f), -CoreVector3.UnitY, CoreVector3.UnitX, CoreVector3.UnitZ);
        return vertices.ToArray();
    }

    private static void AddBoxFace(
        ICollection<GPUVertex> vertices,
        CoreVector3 center,
        CoreVector3 normal,
        CoreVector3 right,
        CoreVector3 up)
    {
        vertices.Add(CreateBoxVertex(center - right * 0.5f - up * 0.5f, normal, right, 0f, 1f));
        vertices.Add(CreateBoxVertex(center + right * 0.5f - up * 0.5f, normal, right, 1f, 1f));
        vertices.Add(CreateBoxVertex(center + right * 0.5f + up * 0.5f, normal, right, 1f, 0f));
        vertices.Add(CreateBoxVertex(center - right * 0.5f + up * 0.5f, normal, right, 0f, 0f));
    }

    private static uint[] CreateBoxIndices()
    {
        var indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint vertex = face * 4;
            int index = checked((int)face * 6);
            indices[index] = vertex;
            indices[index + 1] = vertex + 2;
            indices[index + 2] = vertex + 1;
            indices[index + 3] = vertex;
            indices[index + 4] = vertex + 3;
            indices[index + 5] = vertex + 2;
        }
        return indices;
    }

    private static GPUVertex CreateFloorVertex(float x, float y, float z, float u, float v)
    {
        return new GPUVertex
        {
            Position = new CoreVector3(x, y, z),
            Padding0 = 0f,
            Normal = CoreVector3.UnitY,
            Padding1 = 0f,
            TexCoord = new CoreVector2(u, v),
            TexCoord2 = CoreVector2.Zero,
            Tangent = new CoreVector4(CoreVector3.UnitX, 1f),
            Color = GPUVertex.DefaultColor
        };
    }

    private static GPUVertex CreateBoxVertex(
        CoreVector3 position,
        CoreVector3 normal,
        CoreVector3 tangent,
        float u,
        float v)
    {
        return new GPUVertex
        {
            Position = position,
            Padding0 = 0f,
            Normal = normal,
            Padding1 = 0f,
            TexCoord = new CoreVector2(u, v),
            TexCoord2 = CoreVector2.Zero,
            Tangent = new CoreVector4(tangent, 1f),
            Color = GPUVertex.DefaultColor
        };
    }
}
