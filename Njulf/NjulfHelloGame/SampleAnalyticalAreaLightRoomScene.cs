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

/// <summary>
/// A compact, neutral room arranged to make the shape, specular response, and
/// penumbra of each analytical area-light type easy to compare.
/// </summary>
internal static class SampleAnalyticalAreaLightRoomScene
{
    private static readonly CoreVector3 DdgiMinimum =
        new(-5.25f, -0.25f, -4.3f);
    private static readonly CoreVector3 DdgiMaximum =
        new(5.25f, 5.25f, 4.3f);

    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);
        gi.SimpleDdgiRingCount = 1;
        gi.IndirectIntensity = 1.0f;
        gi.EnvironmentFallbackIntensity = 0.12f;
        gi.MaxBounceDistance = 12f;
        gi.SimpleDdgiAuthoredVolumes.Clear();
        gi.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            DdgiMinimum,
            DdgiMaximum,
            spacing: 0.85f,
            latticePhase: new CoreVector3(0.3f, 0.2f, 0.35f),
            purpose: SimpleDdgiVolumePurpose.ReceiverHero,
            priority: 30));

        // A low neutral baseline keeps unlit faces and reflection misses
        // readable without washing out the authored photometric pattern.
        settings.Environment.Enabled = true;
        settings.Environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
        settings.Environment.SkyIntensity = 0.06f;
        settings.Environment.DiffuseIntensity = 0.24f;
        settings.Environment.SpecularIntensity = 0.30f;
        settings.Reflections.Enabled = true;
        settings.Reflections.Mode = ReflectionMode.HybridRayQuery;
        settings.Reflections.Intensity = 0.65f;
        settings.Fog.Enabled = false;
        settings.Bloom.Enabled = true;
        settings.Bloom.Intensity = 0.035f;
        settings.AmbientOcclusion.Enabled = true;
        settings.AmbientOcclusion.Intensity = 0.55f;
        settings.Particles.Enabled = false;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 0.82f;

        settings.Shadows.DirectionalShadowsEnabled = false;
        settings.Shadows.SpotShadowsEnabled = false;
        settings.Shadows.PointShadowsEnabled = false;
        settings.Shadows.MaxShadowedSpotLights = 0;
        settings.Shadows.MaxShadowedPointLights = 0;
        settings.Shadows.AreaShadowsEnabled = true;
        settings.Shadows.MaxShadowedAreaLights = 3;
        settings.Shadows.AreaShadowSampleCount = 2;
    }

    public static void Configure(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(meshManager);
        ArgumentNullException.ThrowIfNull(materialManager);

        scene.Name = "Njulf Analytical Area Light Room";
        scene.AmbientLight = new Njulf.Core.Math.Color(
            0.018f,
            0.019f,
            0.022f,
            1f);

        MeshHandle boxMesh = meshManager.RegisterMesh(
            CreateBoxVertices(),
            CreateBoxIndices());
        MeshHandle sphereMesh = meshManager.RegisterMesh(
            SampleUvSphereMesh.CreateVertices(),
            SampleUvSphereMesh.CreateIndices());
        MeshHandle diskFixtureMesh = meshManager.RegisterMesh(
            CreateDiskFixtureVertices(),
            CreateDiskFixtureIndices());
        MeshHandle tubeFixtureMesh = meshManager.RegisterMesh(
            CreateTubeFixtureVertices(),
            CreateTubeFixtureIndices());

        MaterialHandle wallMaterial = CreateMaterial(
            materialManager,
            "AreaLightRoom.Walls",
            new CoreVector3(0.62f, 0.64f, 0.68f),
            metallic: 0f,
            roughness: 0.88f);
        MaterialHandle floorMaterial = CreateMaterial(
            materialManager,
            "AreaLightRoom.Floor",
            new CoreVector3(0.34f, 0.36f, 0.40f),
            metallic: 0f,
            roughness: 0.66f);
        MaterialHandle plinthMaterial = CreateMaterial(
            materialManager,
            "AreaLightRoom.Plinths",
            new CoreVector3(0.56f, 0.58f, 0.62f),
            metallic: 0f,
            roughness: 0.72f);
        MaterialHandle iesBoardMaterial = CreateMaterial(
            materialManager,
            "AreaLightRoom.IES.TargetBoard",
            new CoreVector3(0.78f, 0.79f, 0.82f),
            metallic: 0f,
            roughness: 0.92f);
        MaterialHandle iesBoardTrimMaterial = CreateMaterial(
            materialManager,
            "AreaLightRoom.IES.TargetBoardTrim",
            new CoreVector3(0.12f, 0.13f, 0.16f),
            metallic: 0.15f,
            roughness: 0.62f);

        AddBox(scene, boxMesh, floorMaterial, "AreaLightRoom.Floor",
            new CoreVector3(0f, -0.06f, 0f),
            new CoreVector3(10f, 0.12f, 8f));
        AddBox(scene, boxMesh, wallMaterial, "AreaLightRoom.BackWall",
            new CoreVector3(0f, 2.5f, -4.06f),
            new CoreVector3(10f, 5f, 0.12f));
        AddBox(scene, boxMesh, wallMaterial, "AreaLightRoom.LeftWall",
            new CoreVector3(-5.06f, 2.5f, 0f),
            new CoreVector3(0.12f, 5f, 8f));
        AddBox(scene, boxMesh, wallMaterial, "AreaLightRoom.RightWall",
            new CoreVector3(5.06f, 2.5f, 0f),
            new CoreVector3(0.12f, 5f, 8f));
        AddBox(scene, boxMesh, wallMaterial, "AreaLightRoom.Ceiling",
            new CoreVector3(0f, 5.06f, 0f),
            new CoreVector3(10f, 0.12f, 8f));

        // The light board gives the included IES fixture a clean, matte field
        // whose profile remains visible beside the three area-light stations.
        AddBox(scene, boxMesh, iesBoardTrimMaterial,
            "AreaLightRoom.IES.TargetBoardTrim",
            new CoreVector3(0f, 2.78f, -3.92f),
            new CoreVector3(2.72f, 2.92f, 0.055f));
        AddBox(scene, boxMesh, iesBoardMaterial,
            "AreaLightRoom.IES.TargetBoard",
            new CoreVector3(0f, 2.78f, -3.875f),
            new CoreVector3(2.44f, 2.64f, 0.045f));

        AddPlinth(scene, boxMesh, plinthMaterial, -2.75f, -0.45f);
        AddPlinth(scene, boxMesh, plinthMaterial, 0f, -0.45f);
        AddPlinth(scene, boxMesh, plinthMaterial, 2.75f, -0.45f);

        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(
                materialManager,
                "AreaLightRoom.RectangleTarget.Matte",
                new CoreVector3(0.82f, 0.22f, 0.10f),
                metallic: 0f,
                roughness: 0.78f),
            "AreaLightRoom.RectangleTarget.MatteSphere",
            new CoreVector3(-2.75f, 1.20f, -0.45f),
            new CoreVector3(0.50f));
        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(
                materialManager,
                "AreaLightRoom.TubeTarget.BrushedMetal",
                new CoreVector3(0.72f, 0.76f, 0.82f),
                metallic: 0.88f,
                roughness: 0.24f),
            "AreaLightRoom.TubeTarget.BrushedMetalSphere",
            new CoreVector3(0f, 1.20f, -0.45f),
            new CoreVector3(0.50f));
        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(
                materialManager,
                "AreaLightRoom.DiskTarget.Glossy",
                new CoreVector3(0.86f, 0.90f, 1.0f),
                metallic: 0f,
                roughness: 0.16f),
            "AreaLightRoom.DiskTarget.GlossySphere",
            new CoreVector3(2.75f, 1.20f, -0.45f),
            new CoreVector3(0.50f));

        AddEmitterProxies(
            scene,
            boxMesh,
            diskFixtureMesh,
            tubeFixtureMesh,
            materialManager);

        Console.WriteLine(
            "Analytical area-light room: warm rectangle at left, green tube at " +
            "centre, blue disk at right, and an IES cross-profile on the rear " +
            "board. Area fixtures use LTC direct lighting, DDGI hit sampling, " +
            "and scheduled ray-query penumbras.");
    }

    private static void AddPlinth(
        Scene scene,
        MeshHandle boxMesh,
        MaterialHandle material,
        float x,
        float z)
    {
        AddBox(
            scene,
            boxMesh,
            material,
            $"AreaLightRoom.Plinth.{x:0.00}.{z:0.00}",
            new CoreVector3(x, 0.36f, z),
            new CoreVector3(1.46f, 0.72f, 1.36f));
    }

    private static void AddEmitterProxies(
        Scene scene,
        MeshHandle boxMesh,
        MeshHandle diskFixtureMesh,
        MeshHandle tubeFixtureMesh,
        MaterialManager materialManager)
    {
        MaterialHandle rectangleProxy = CreateEmitterProxyMaterial(
            materialManager,
            "AreaLightRoom.RectangleEmitterProxy",
            new CoreVector3(1.0f, 0.67f, 0.34f));
        AddBox(
            scene,
            boxMesh,
            rectangleProxy,
            "AreaLightRoom.RectangleEmitterProxy",
            new CoreVector3(-2.75f, 4.94f, -0.55f),
            new CoreVector3(2.15f, 0.035f, 1.25f));

        MaterialHandle diskProxy = CreateEmitterProxyMaterial(
            materialManager,
            "AreaLightRoom.DiskEmitterProxy",
            new CoreVector3(0.25f, 0.52f, 1.0f));
        AddSphere(
            scene,
            diskFixtureMesh,
            diskProxy,
            "AreaLightRoom.DiskEmitterProxy",
            new CoreVector3(2.75f, 3.35f, -3.96f),
            new CoreVector3(1.25f, 1.25f, 0.05f));

        MaterialHandle tubeProxy = CreateEmitterProxyMaterial(
            materialManager,
            "AreaLightRoom.TubeEmitterProxy",
            new CoreVector3(0.18f, 1.0f, 0.66f));
        AddSphere(
            scene,
            tubeFixtureMesh,
            tubeProxy,
            "AreaLightRoom.TubeEmitterProxy",
            new CoreVector3(0f, 4.22f, 0.65f),
            new CoreVector3(2.5f, 0.16f, 0.16f));
    }

    private static MaterialHandle CreateMaterial(
        MaterialManager materialManager,
        string name,
        CoreVector3 albedo,
        float metallic,
        float roughness)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = name,
            BaseColorFactor = new CoreVector4(albedo, 1f),
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
            ReceivesShadows = true
        });
    }

    private static MaterialHandle CreateEmitterProxyMaterial(
        MaterialManager materialManager,
        string name,
        CoreVector3 color)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = name,
            BaseColorFactor = new CoreVector4(color, 1f),
            EmissiveFactor = color,
            EmissiveStrength = 1.15f,
            MetallicFactor = 0f,
            RoughnessFactor = 0.35f,
            DoubleSided = true,
            ShadingModel = MaterialShadingModel.Unlit,
            DiffuseGiParticipation = GiParticipationOverride.Disabled,
            EmissionGiParticipation = GiParticipationOverride.Disabled,
            ReceivesShadows = false
        });
    }

    private const int DiskFixtureSegmentCount = 48;

    private static GPUVertex[] CreateDiskFixtureVertices()
    {
        var vertices = new List<GPUVertex>(
            checked((DiskFixtureSegmentCount + 1) * 2));
        vertices.Add(CreateVertex(
            CoreVector3.Zero,
            CoreVector3.UnitZ,
            CoreVector3.UnitX,
            0.5f,
            0.5f));
        for (int segment = 0; segment < DiskFixtureSegmentCount; segment++)
        {
            float angle = 2f * MathF.PI * segment / DiskFixtureSegmentCount;
            float x = 0.5f * MathF.Cos(angle);
            float y = 0.5f * MathF.Sin(angle);
            vertices.Add(CreateVertex(
                new CoreVector3(x, y, 0f),
                CoreVector3.UnitZ,
                CoreVector3.UnitX,
                x + 0.5f,
                0.5f - y));
        }

        vertices.Add(CreateVertex(
            CoreVector3.Zero,
            -CoreVector3.UnitZ,
            -CoreVector3.UnitX,
            0.5f,
            0.5f));
        for (int segment = 0; segment < DiskFixtureSegmentCount; segment++)
        {
            float angle = 2f * MathF.PI * segment / DiskFixtureSegmentCount;
            float x = 0.5f * MathF.Cos(angle);
            float y = 0.5f * MathF.Sin(angle);
            vertices.Add(CreateVertex(
                new CoreVector3(x, y, 0f),
                -CoreVector3.UnitZ,
                -CoreVector3.UnitX,
                x + 0.5f,
                0.5f - y));
        }
        return vertices.ToArray();
    }

    private static uint[] CreateDiskFixtureIndices()
    {
        var indices = new uint[checked(DiskFixtureSegmentCount * 6)];
        uint backCenter = checked((uint)DiskFixtureSegmentCount + 1u);
        for (uint segment = 0; segment < DiskFixtureSegmentCount; segment++)
        {
            uint next = (segment + 1u) % DiskFixtureSegmentCount;
            int index = checked((int)segment * 6);
            indices[index] = 0u;
            indices[index + 1] = segment + 1u;
            indices[index + 2] = next + 1u;
            indices[index + 3] = backCenter;
            indices[index + 4] = backCenter + next + 1u;
            indices[index + 5] = backCenter + segment + 1u;
        }
        return indices;
    }

    private const int TubeFixtureSegmentCount = 32;

    private static GPUVertex[] CreateTubeFixtureVertices()
    {
        var vertices = new List<GPUVertex>(
            checked(TubeFixtureSegmentCount * 6 + 2));

        // Side rings. The unit fixture is one metre long on X with a unit
        // diameter; the authored transform supplies the analytical dimensions.
        for (int segment = 0; segment < TubeFixtureSegmentCount; segment++)
        {
            float angle = 2f * MathF.PI * segment / TubeFixtureSegmentCount;
            float y = 0.5f * MathF.Cos(angle);
            float z = 0.5f * MathF.Sin(angle);
            var normal = new CoreVector3(0f, y * 2f, z * 2f);
            float v = segment / (float)TubeFixtureSegmentCount;
            vertices.Add(CreateVertex(
                new CoreVector3(-0.5f, y, z),
                normal,
                CoreVector3.UnitX,
                0f,
                v));
            vertices.Add(CreateVertex(
                new CoreVector3(0.5f, y, z),
                normal,
                CoreVector3.UnitX,
                1f,
                v));
        }

        // Separate cap vertices preserve the flat end normals.
        uint negativeCenter = checked((uint)vertices.Count);
        vertices.Add(CreateVertex(
            new CoreVector3(-0.5f, 0f, 0f),
            -CoreVector3.UnitX,
            CoreVector3.UnitY,
            0.5f,
            0.5f));
        for (int segment = 0; segment < TubeFixtureSegmentCount; segment++)
        {
            float angle = 2f * MathF.PI * segment / TubeFixtureSegmentCount;
            float y = 0.5f * MathF.Cos(angle);
            float z = 0.5f * MathF.Sin(angle);
            vertices.Add(CreateVertex(
                new CoreVector3(-0.5f, y, z),
                -CoreVector3.UnitX,
                CoreVector3.UnitY,
                y + 0.5f,
                0.5f - z));
        }

        uint positiveCenter = checked((uint)vertices.Count);
        vertices.Add(CreateVertex(
            new CoreVector3(0.5f, 0f, 0f),
            CoreVector3.UnitX,
            CoreVector3.UnitY,
            0.5f,
            0.5f));
        for (int segment = 0; segment < TubeFixtureSegmentCount; segment++)
        {
            float angle = 2f * MathF.PI * segment / TubeFixtureSegmentCount;
            float y = 0.5f * MathF.Cos(angle);
            float z = 0.5f * MathF.Sin(angle);
            vertices.Add(CreateVertex(
                new CoreVector3(0.5f, y, z),
                CoreVector3.UnitX,
                CoreVector3.UnitY,
                y + 0.5f,
                0.5f - z));
        }

        _ = negativeCenter;
        _ = positiveCenter;
        return vertices.ToArray();
    }

    private static uint[] CreateTubeFixtureIndices()
    {
        var indices = new uint[checked(TubeFixtureSegmentCount * 12)];
        uint negativeCenter = checked((uint)TubeFixtureSegmentCount * 2u);
        uint positiveCenter = checked(
            negativeCenter + (uint)TubeFixtureSegmentCount + 1u);
        for (uint segment = 0; segment < TubeFixtureSegmentCount; segment++)
        {
            uint next = (segment + 1u) % TubeFixtureSegmentCount;
            uint left = segment * 2u;
            uint right = left + 1u;
            uint nextLeft = next * 2u;
            uint nextRight = nextLeft + 1u;
            int index = checked((int)segment * 12);

            indices[index] = left;
            indices[index + 1] = nextRight;
            indices[index + 2] = right;
            indices[index + 3] = left;
            indices[index + 4] = nextLeft;
            indices[index + 5] = nextRight;

            indices[index + 6] = negativeCenter;
            indices[index + 7] = negativeCenter + next + 1u;
            indices[index + 8] = negativeCenter + segment + 1u;
            indices[index + 9] = positiveCenter;
            indices[index + 10] = positiveCenter + segment + 1u;
            indices[index + 11] = positiveCenter + next + 1u;
        }
        return indices;
    }

    private static void AddBox(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreVector3 position,
        CoreVector3 scale)
    {
        scene.Add(new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = CoreMatrix4x4.CreateScale(scale) *
                CoreMatrix4x4.CreateTranslation(position),
            Visible = true,
            IsStatic = true
        });
    }

    private static void AddSphere(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreVector3 position,
        CoreVector3 scale)
    {
        scene.Add(new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = CoreMatrix4x4.CreateScale(scale) *
                CoreMatrix4x4.CreateTranslation(position),
            Visible = true,
            IsStatic = true
        });
    }

    internal static GPUVertex[] CreateBoxVertices()
    {
        var vertices = new List<GPUVertex>(24);
        AddBoxFace(vertices, new CoreVector3(0f, 0f, 0.5f),
            CoreVector3.UnitZ, CoreVector3.UnitX, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(0f, 0f, -0.5f),
            -CoreVector3.UnitZ, -CoreVector3.UnitX, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(0.5f, 0f, 0f),
            CoreVector3.UnitX, -CoreVector3.UnitZ, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(-0.5f, 0f, 0f),
            -CoreVector3.UnitX, CoreVector3.UnitZ, CoreVector3.UnitY);
        AddBoxFace(vertices, new CoreVector3(0f, 0.5f, 0f),
            CoreVector3.UnitY, CoreVector3.UnitX, -CoreVector3.UnitZ);
        AddBoxFace(vertices, new CoreVector3(0f, -0.5f, 0f),
            -CoreVector3.UnitY, CoreVector3.UnitX, CoreVector3.UnitZ);
        return vertices.ToArray();
    }

    private static void AddBoxFace(
        ICollection<GPUVertex> vertices,
        CoreVector3 center,
        CoreVector3 normal,
        CoreVector3 right,
        CoreVector3 up)
    {
        vertices.Add(CreateVertex(
            center - right * 0.5f - up * 0.5f, normal, right, 0f, 1f));
        vertices.Add(CreateVertex(
            center + right * 0.5f - up * 0.5f, normal, right, 1f, 1f));
        vertices.Add(CreateVertex(
            center + right * 0.5f + up * 0.5f, normal, right, 1f, 0f));
        vertices.Add(CreateVertex(
            center - right * 0.5f + up * 0.5f, normal, right, 0f, 0f));
    }

    internal static uint[] CreateBoxIndices()
    {
        var indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint vertex = face * 4;
            int index = checked((int)face * 6);
            indices[index] = vertex;
            indices[index + 1] = vertex + 1;
            indices[index + 2] = vertex + 2;
            indices[index + 3] = vertex;
            indices[index + 4] = vertex + 2;
            indices[index + 5] = vertex + 3;
        }

        return indices;
    }

    private static GPUVertex CreateVertex(
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
