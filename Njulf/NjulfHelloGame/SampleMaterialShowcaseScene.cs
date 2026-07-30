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

internal static class SampleMaterialShowcaseScene
{
    private const int LatitudeSegments = 24;
    private const int LongitudeSegments = 48;
    private const float SphereRadius = 0.45f;
    private const float SphereCenterY = 0.62f;

    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        settings.GlobalIllumination.Enabled = false;
        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.75f;
        settings.Environment.DiffuseIntensity = 0.8f;
        settings.Environment.SpecularIntensity = 1.0f;
        settings.Reflections.Enabled = true;
        settings.Reflections.Intensity = 1.0f;
        settings.Fog.Enabled = false;
        settings.Bloom.Enabled = true;
        settings.Bloom.Intensity = 0.12f;
        settings.AmbientOcclusion.Enabled = true;
    }

    public static void Configure(Scene scene, MeshManager meshManager, MaterialManager materialManager)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (meshManager == null)
            throw new ArgumentNullException(nameof(meshManager));
        if (materialManager == null)
            throw new ArgumentNullException(nameof(materialManager));

        scene.Name = "Njulf Material Showcase";
        scene.AmbientLight = new Njulf.Core.Math.Color(0.055f, 0.06f, 0.07f, 1f);

        MeshHandle floorMesh = meshManager.RegisterMesh(CreateFloorVertices(), CreateFloorIndices());
        MeshHandle sphereMesh = meshManager.RegisterMesh(CreateSphereVertices(), CreateSphereIndices());

        scene.Add(new ReflectionProbe
        {
            Name = "MaterialShowcase.CenterProbe",
            Position = new CoreVector3(0f, 1.35f, 1.35f),
            Shape = ReflectionProbeShape.Box,
            BoxExtents = new CoreVector3(8.5f, 3.5f, 6.5f),
            BlendDistance = 1.0f,
            Intensity = 1.0f,
            Priority = 0,
            BoxProjection = true
        });

        AddObject(
            scene,
            floorMesh,
            CreateMaterial(materialManager, new CoreVector3(0.32f, 0.34f, 0.32f), metallic: 0.0f, roughness: 0.68f),
            "MaterialShowcase.Floor",
            CoreMatrix4x4.Identity);

        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(materialManager, new CoreVector3(0.95f, 0.96f, 1.0f), metallic: 1.0f, roughness: 0.04f),
            "ReflectionTest.Chrome",
            new CoreVector3(-1.8f, SphereCenterY, 0.0f));

        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(materialManager, new CoreVector3(1.0f, 0.76f, 0.46f), metallic: 1.0f, roughness: 0.16f),
            "ReflectionTest.SmoothGold",
            new CoreVector3(-0.6f, SphereCenterY, 0.0f));

        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(materialManager, new CoreVector3(0.72f, 0.72f, 0.70f), metallic: 1.0f, roughness: 0.42f),
            "ReflectionTest.BrushedMetal",
            new CoreVector3(0.6f, SphereCenterY, 0.0f));

        AddSphere(
            scene,
            sphereMesh,
            CreateMaterial(materialManager, new CoreVector3(0.88f, 0.96f, 1.0f), metallic: 0.0f, roughness: 0.08f),
            "ReflectionTest.GlossyDielectric",
            new CoreVector3(1.8f, SphereCenterY, 0.0f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.ClearcoatPaint",
                new CoreVector3(0.85f, 0.05f, 0.035f),
                metallic: 0f,
                roughness: 0.45f,
                MaterialFeatureFlags.Clearcoat,
                extension => extension with
                {
                    ClearcoatFactor = 1f,
                    ClearcoatRoughness = 0.04f,
                    ClearcoatNormalScale = 1f
                }),
            "MaterialQuality.ClearcoatPaint",
            new CoreVector3(-3.0f, SphereCenterY, 1.35f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.SheenVelvet",
                new CoreVector3(0.08f, 0.04f, 0.22f),
                metallic: 0f,
                roughness: 0.8f,
                MaterialFeatureFlags.Sheen,
                extension => extension with
                {
                    SheenColorFactor = new CoreVector3(0.35f, 0.55f, 1.0f),
                    SheenRoughness = 0.4f
                }),
            "MaterialQuality.SheenVelvet",
            new CoreVector3(-1.8f, SphereCenterY, 1.35f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.AnisotropicMetal",
                new CoreVector3(0.74f, 0.74f, 0.70f),
                metallic: 1f,
                roughness: 0.28f,
                MaterialFeatureFlags.Anisotropy,
                extension => extension with
                {
                    AnisotropyStrength = 0.85f,
                    AnisotropyRotation = 0f
                }),
            "MaterialQuality.AnisotropicMetal",
            new CoreVector3(-0.6f, SphereCenterY, 1.35f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.SimpleGlass",
                new CoreVector3(0.78f, 0.95f, 1.0f),
                metallic: 0f,
                roughness: 0.02f,
                MaterialFeatureFlags.Transmission,
                extension => extension with
                {
                    TransmissionFactor = 0.85f,
                    Ior = 1.45f,
                    ThicknessFactor = 0.1f,
                    AttenuationColor = new CoreVector3(0.78f, 0.95f, 1.0f)
                },
                MaterialBlendMode.AlphaBlend),
            "MaterialQuality.SimpleGlass",
            new CoreVector3(0.6f, SphereCenterY, 1.35f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.SubsurfaceWax",
                new CoreVector3(0.9f, 0.72f, 0.55f),
                metallic: 0f,
                roughness: 0.55f,
                MaterialFeatureFlags.Subsurface,
                extension => extension with
                {
                    SubsurfaceColor = new CoreVector3(1.0f, 0.46f, 0.22f),
                    SubsurfaceStrength = 0.5f
                }),
            "MaterialQuality.SubsurfaceWax",
            new CoreVector3(1.8f, SphereCenterY, 1.35f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.EmissiveHighIntensity",
                new CoreVector3(0.05f, 0.05f, 0.06f),
                metallic: 0f,
                roughness: 0.3f,
                MaterialFeatureFlags.EmissiveStrength,
                extension => extension,
                emissive: new CoreVector3(0.1f, 0.75f, 1.0f),
                emissiveStrength: 6f),
            "MaterialQuality.EmissiveHighIntensity",
            new CoreVector3(3.0f, SphereCenterY, 1.35f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.SpecularTint",
                new CoreVector3(0.62f, 0.68f, 0.78f),
                metallic: 0f,
                roughness: 0.18f,
                MaterialFeatureFlags.Specular,
                extension => extension with
                {
                    SpecularColorFactor = new CoreVector3(0.35f, 0.65f, 1.0f),
                    SpecularFactor = 0.85f
                }),
            "MaterialQuality.SpecularTint",
            new CoreVector3(-3.0f, SphereCenterY, 2.7f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.VolumeGlass",
                new CoreVector3(0.78f, 1.0f, 0.82f),
                metallic: 0f,
                roughness: 0.04f,
                MaterialFeatureFlags.Transmission | MaterialFeatureFlags.VolumeApproximation,
                extension => extension with
                {
                    TransmissionFactor = 0.92f,
                    Ior = 1.48f,
                    ThicknessFactor = 0.65f,
                    AttenuationDistance = 1.4f,
                    AttenuationColor = new CoreVector3(0.55f, 1.0f, 0.62f)
                },
                MaterialBlendMode.AlphaBlend),
            "MaterialQuality.VolumeGlass",
            new CoreVector3(-1.8f, SphereCenterY, 2.7f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.IridescenceFilm",
                new CoreVector3(0.06f, 0.06f, 0.08f),
                metallic: 0f,
                roughness: 0.12f,
                MaterialFeatureFlags.Iridescence | MaterialFeatureFlags.Specular,
                extension => extension with
                {
                    SpecularColorFactor = CoreVector3.One,
                    SpecularFactor = 1f,
                    IridescenceFactor = 1f,
                    IridescenceIor = 1.3f,
                    IridescenceThicknessMinimum = 120f,
                    IridescenceThicknessMaximum = 650f
                }),
            "MaterialQuality.IridescenceFilm",
            new CoreVector3(-0.6f, SphereCenterY, 2.7f));

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "MaterialQuality.DispersionGlass",
                new CoreVector3(0.94f, 0.98f, 1.0f),
                metallic: 0f,
                roughness: 0.01f,
                MaterialFeatureFlags.Transmission | MaterialFeatureFlags.Dispersion,
                extension => extension with
                {
                    TransmissionFactor = 0.9f,
                    Ior = 1.55f,
                    ThicknessFactor = 0.1f,
                    Dispersion = 0.8f
                },
                MaterialBlendMode.AlphaBlend),
            "MaterialQuality.DispersionGlass",
            new CoreVector3(0.6f, SphereCenterY, 2.7f));

        AddSphere(
            scene,
            sphereMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.18f, 0.46f, 0.22f),
                metallic: 0f,
                roughness: 0.62f,
                MaterialRenderMode.Mask,
                MaterialBlendMode.Mask,
                alpha: 1f,
                featureFlags: MaterialFeatureFlags.Foliage,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows),
            "MaterialQuality.MaskedFoliage",
            new CoreVector3(1.8f, SphereCenterY, 2.7f));

        AddSphere(
            scene,
            sphereMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.95f, 0.62f, 0.28f),
                metallic: 0f,
                roughness: 0.22f,
                MaterialRenderMode.Blend,
                MaterialBlendMode.PremultipliedAlpha,
                alpha: 0.48f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows),
            "MaterialQuality.PremultipliedAlpha",
            new CoreVector3(3.0f, SphereCenterY, 2.7f));

        AddSphere(
            scene,
            sphereMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.58f, 0.60f, 0.66f),
                metallic: 0f,
                roughness: 0.94f,
                MaterialRenderMode.Opaque,
                MaterialBlendMode.Opaque,
                alpha: 1f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows),
            "MaterialQuality.DoubleSidedMatte",
            new CoreVector3(-3.0f, SphereCenterY, 4.05f));
    }

    private static void AddObject(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreMatrix4x4 world)
    {
        scene.Add(new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = world,
            Visible = true
        });
    }

    private static void AddSphere(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreVector3 position)
    {
        scene.Add(new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = CoreMatrix4x4.CreateScale(new CoreVector3(SphereRadius)) *
                          CoreMatrix4x4.CreateTranslation(position),
            Visible = true
        });
    }

    private static MaterialHandle CreateRenderModeMaterial(
        MaterialManager materialManager,
        CoreVector3 albedo,
        float metallic,
        float roughness,
        MaterialRenderMode renderMode,
        MaterialBlendMode blendMode,
        float alpha,
        MaterialFeatureFlags featureFlags,
        MaterialSurfaceFlags surfaceFlags)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = $"MaterialShowcase.{blendMode}",
            BaseColorFactor = new CoreVector4(albedo, Math.Clamp(alpha, 0f, 1f)),
            MetallicFactor = Math.Clamp(metallic, 0f, 1f),
            RoughnessFactor = Math.Clamp(roughness, 0.04f, 1f),
            AlphaMode = ToAlphaMode(renderMode),
            AlphaCutoff = 0.5f,
            DoubleSided = surfaceFlags.HasFlag(MaterialSurfaceFlags.DoubleSided),
            ReceivesShadows = surfaceFlags.HasFlag(MaterialSurfaceFlags.ReceivesShadows),
            RenderBlendModeOverride = blendMode,
            FeatureFlags = featureFlags
        });
    }

    private static MaterialHandle CreateMaterial(
        MaterialManager materialManager,
        CoreVector3 albedo,
        float metallic,
        float roughness)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = "MaterialShowcase.Pbr",
            BaseColorFactor = new CoreVector4(albedo, 1f),
            MetallicFactor = Math.Clamp(metallic, 0f, 1f),
            RoughnessFactor = Math.Clamp(roughness, 0.04f, 1f)
        });
    }

    private static MaterialHandle CreateExtensionMaterial(
        MaterialManager materialManager,
        string name,
        CoreVector3 albedo,
        float metallic,
        float roughness,
        MaterialFeatureFlags featureFlags,
        Func<MaterialExtensionDefinition, MaterialExtensionDefinition> configureExtension,
        MaterialBlendMode blendMode = MaterialBlendMode.Opaque,
        CoreVector3 emissive = default,
        float emissiveStrength = 1f)
    {
        MaterialExtensionDefinition extension = configureExtension(MaterialExtensionDefinition.None);
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = name,
            BaseColorFactor = new CoreVector4(albedo, 1f),
            EmissiveFactor = emissive,
            EmissiveStrength = emissiveStrength,
            MetallicFactor = Math.Clamp(metallic, 0f, 1f),
            RoughnessFactor = Math.Clamp(roughness, 0.04f, 1f),
            AlphaMode = IsTransparent(blendMode) ? MaterialAlphaMode.Blend : MaterialAlphaMode.Opaque,
            RenderBlendModeOverride = blendMode,
            FeatureFlags = featureFlags,
            Extensions = extension
        });
    }

    private static MaterialAlphaMode ToAlphaMode(MaterialRenderMode renderMode)
    {
        return renderMode switch
        {
            MaterialRenderMode.Mask => MaterialAlphaMode.Mask,
            MaterialRenderMode.Blend => MaterialAlphaMode.Blend,
            _ => MaterialAlphaMode.Opaque
        };
    }

    private static bool IsTransparent(MaterialBlendMode blendMode) =>
        blendMode is MaterialBlendMode.AlphaBlend or
            MaterialBlendMode.PremultipliedAlpha or
            MaterialBlendMode.Additive or
            MaterialBlendMode.Multiply;

    private static GPUVertex[] CreateSphereVertices()
    {
        var vertices = new List<GPUVertex>(2 + (LatitudeSegments - 1) * LongitudeSegments);
        vertices.Add(CreateSphereVertex(CoreVector3.UnitY, 0f, 0f));

        for (int latitude = 1; latitude < LatitudeSegments; latitude++)
        {
            float v = (float)latitude / LatitudeSegments;
            float theta = v * MathF.PI;
            float y = MathF.Cos(theta);
            float ringRadius = MathF.Sin(theta);

            for (int longitude = 0; longitude < LongitudeSegments; longitude++)
            {
                float u = (float)longitude / LongitudeSegments;
                float phi = u * MathF.Tau;
                var normal = new CoreVector3(
                    ringRadius * MathF.Cos(phi),
                    y,
                    ringRadius * MathF.Sin(phi));

                vertices.Add(CreateSphereVertex(normal, u, v));
            }
        }

        vertices.Add(CreateSphereVertex(CoreVector3.Down, 0f, 1f));
        return vertices.ToArray();
    }

    private static GPUVertex[] CreateFloorVertices()
    {
        const float halfWidth = 4.4f;
        const float nearZ = -1.0f;
        const float farZ = 5.2f;

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

    private static uint[] CreateSphereIndices()
    {
        var indices = new List<uint>(LatitudeSegments * LongitudeSegments * 6);
        uint topIndex = 0;
        uint bottomIndex = (uint)(1 + (LatitudeSegments - 1) * LongitudeSegments);

        for (int longitude = 0; longitude < LongitudeSegments; longitude++)
        {
            uint firstRingCurrent = RingVertexIndex(0, longitude);
            uint firstRingNext = RingVertexIndex(0, longitude + 1);
            indices.Add(topIndex);
            indices.Add(firstRingNext);
            indices.Add(firstRingCurrent);
        }

        for (int latitude = 0; latitude < LatitudeSegments - 2; latitude++)
        {
            for (int longitude = 0; longitude < LongitudeSegments; longitude++)
            {
                uint upperCurrent = RingVertexIndex(latitude, longitude);
                uint upperNext = RingVertexIndex(latitude, longitude + 1);
                uint lowerCurrent = RingVertexIndex(latitude + 1, longitude);
                uint lowerNext = RingVertexIndex(latitude + 1, longitude + 1);

                indices.Add(upperCurrent);
                indices.Add(upperNext);
                indices.Add(lowerCurrent);

                indices.Add(upperNext);
                indices.Add(lowerNext);
                indices.Add(lowerCurrent);
            }
        }

        int lastRing = LatitudeSegments - 2;
        for (int longitude = 0; longitude < LongitudeSegments; longitude++)
        {
            uint lastRingCurrent = RingVertexIndex(lastRing, longitude);
            uint lastRingNext = RingVertexIndex(lastRing, longitude + 1);
            indices.Add(bottomIndex);
            indices.Add(lastRingCurrent);
            indices.Add(lastRingNext);
        }

        return indices.ToArray();
    }

    private static uint RingVertexIndex(int ring, int longitude)
    {
        int wrappedLongitude = longitude % LongitudeSegments;
        if (wrappedLongitude < 0)
            wrappedLongitude += LongitudeSegments;

        return (uint)(1 + ring * LongitudeSegments + wrappedLongitude);
    }

    private static GPUVertex CreateSphereVertex(CoreVector3 normal, float u, float v)
    {
        float phi = u * MathF.Tau;
        var tangent = new CoreVector3(-MathF.Sin(phi), 0f, MathF.Cos(phi));

        return new GPUVertex
        {
            Position = normal,
            Padding0 = 0f,
            Normal = normal,
            Padding1 = 0f,
            TexCoord = new CoreVector2(u, v),
            TexCoord2 = CoreVector2.Zero,
            Tangent = new CoreVector4(tangent, 1f),
            Color = GPUVertex.DefaultColor
        };
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
}
