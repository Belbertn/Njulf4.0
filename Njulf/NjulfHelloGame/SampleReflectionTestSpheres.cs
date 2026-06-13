using System;
using System.Collections.Generic;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

internal static class SampleReflectionTestSpheres
{
    private const int LatitudeSegments = 24;
    private const int LongitudeSegments = 48;
    private const float SphereRadius = 0.45f;
    private const float SphereCenterY = 0.62f;

    public static void Configure(Scene scene, MeshManager meshManager, MaterialManager materialManager)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (meshManager == null)
            throw new ArgumentNullException(nameof(meshManager));
        if (materialManager == null)
            throw new ArgumentNullException(nameof(materialManager));

        MeshHandle sphereMesh = meshManager.RegisterMesh(CreateSphereVertices(), CreateSphereIndices());

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
                extension =>
                {
                    extension.Clearcoat = new CoreVector4(1f, 0.04f, 1f, 1f);
                    return extension;
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
                extension =>
                {
                    extension.SheenColor = new CoreVector4(0.35f, 0.55f, 1.0f, 0.4f);
                    return extension;
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
                extension =>
                {
                    extension.Anisotropy = new CoreVector4(0.85f, 0f, 0f, 0f);
                    return extension;
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
                extension =>
                {
                    extension.Transmission = new CoreVector4(0.85f, 1.45f, 0.1f, 0f);
                    extension.AttenuationColor = new CoreVector4(0.78f, 0.95f, 1.0f, 0f);
                    return extension;
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
                extension =>
                {
                    extension.Subsurface = new CoreVector4(1.0f, 0.46f, 0.22f, 0.5f);
                    return extension;
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
                extension =>
                {
                    extension.Clearcoat = new CoreVector4(0f, 0f, 1f, 6f);
                    return extension;
                },
                emissive: new CoreVector3(0.1f, 0.75f, 1.0f)),
            "MaterialQuality.EmissiveHighIntensity",
            new CoreVector3(3.0f, SphereCenterY, 1.35f));
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

    private static MaterialHandle CreateMaterial(
        MaterialManager materialManager,
        CoreVector3 albedo,
        float metallic,
        float roughness)
    {
        return materialManager.RegisterMaterial(new GPUMaterialData
        {
            Albedo = new CoreVector4(albedo, 1f),
            Emissive = CoreVector4.Zero,
            NormalScaleBias = new CoreVector4(1f, MaterialRenderMode.Opaque.ToGpuAlphaModeCode(), 0.5f, 0f),
            MetallicRoughnessAO = new CoreVector4(
                Math.Clamp(metallic, 0f, 1f),
                Math.Clamp(roughness, 0.04f, 1f),
                1f,
                0f),
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
            FeatureFlags = 0u,
            ExtensionDataIndex = -1
        });
    }

    private static MaterialHandle CreateExtensionMaterial(
        MaterialManager materialManager,
        string name,
        CoreVector3 albedo,
        float metallic,
        float roughness,
        MaterialFeatureFlags featureFlags,
        Func<GPUMaterialExtensionData, GPUMaterialExtensionData> configureExtension,
        MaterialBlendMode blendMode = MaterialBlendMode.Opaque,
        CoreVector3 emissive = default)
    {
        var extension = configureExtension(CreateDefaultExtensionData());
        var material = new GPUMaterialData
        {
            Albedo = new CoreVector4(albedo, 1f),
            Emissive = new CoreVector4(emissive, 1f),
            NormalScaleBias = new CoreVector4(
                1f,
                blendMode == MaterialBlendMode.AlphaBlend ? MaterialRenderMode.Blend.ToGpuAlphaModeCode() : MaterialRenderMode.Opaque.ToGpuAlphaModeCode(),
                0.5f,
                0f),
            MetallicRoughnessAO = new CoreVector4(
                Math.Clamp(metallic, 0f, 1f),
                Math.Clamp(roughness, 0.04f, 1f),
                1f,
                0f),
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
            FeatureFlags = (uint)featureFlags,
            ExtensionDataIndex = -1
        };

        return materialManager.RegisterMaterial(
            material,
            extension,
            new MaterialRenderMetadata
            {
                BlendMode = blendMode,
                SurfaceFlags = MaterialSurfaceFlags.ReceivesShadows,
                AlphaCutoff = 0.5f
            });
    }

    private static GPUMaterialExtensionData CreateDefaultExtensionData()
    {
        return new GPUMaterialExtensionData
        {
            Clearcoat = new CoreVector4(0f, 0f, 1f, 1f),
            SheenColor = CoreVector4.Zero,
            Anisotropy = CoreVector4.Zero,
            Transmission = new CoreVector4(0f, 1.5f, 0f, 0f),
            AttenuationColor = new CoreVector4(1f, 1f, 1f, 0f),
            Subsurface = CoreVector4.Zero,
            ClearcoatTextureIndex = BindlessIndex.DefaultWhiteTexture,
            ClearcoatRoughnessTextureIndex = BindlessIndex.DefaultWhiteTexture,
            ClearcoatNormalTextureIndex = BindlessIndex.DefaultNormalTexture,
            SheenColorTextureIndex = BindlessIndex.DefaultWhiteTexture,
            SheenRoughnessTextureIndex = BindlessIndex.DefaultWhiteTexture,
            AnisotropyTextureIndex = BindlessIndex.DefaultWhiteTexture,
            TransmissionTextureIndex = BindlessIndex.DefaultWhiteTexture,
            ThicknessTextureIndex = BindlessIndex.DefaultWhiteTexture,
            SubsurfaceTextureIndex = BindlessIndex.DefaultWhiteTexture
        };
    }

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
}
