using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Assets.Validation;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

internal static class SampleMaterialShowcaseScene
{
    private const float SphereRadius = 0.45f;
    private const float SphereCenterY = 0.62f;

    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        settings.GlobalIllumination.Enabled = true;
        settings.GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
        settings.GlobalIllumination.UseDdgi = true;
        settings.GlobalIllumination.UseRayQueryBackend = true;
        settings.GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);
        // The bounded showcase uses its authored lattice for hero receivers and
        // needs only the near ring as a coarser transition fallback. Keeping the
        // tier's mid/far rings would make the required layout exceed DdgiHigh's
        // authoritative probe and memory budgets.
        settings.GlobalIllumination.SimpleDdgiRingCount = 1;
        settings.GlobalIllumination.IndirectIntensity = 1.05f;
        settings.GlobalIllumination.EnvironmentFallbackIntensity = 0.8f;
        settings.GlobalIllumination.MaxBounceDistance = 12f;
        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Clear();
        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Add(
            new SimpleDdgiAuthoredVolume(
                new CoreVector3(-4.7f, -0.15f, -1.0f),
                new CoreVector3(4.7f, 3.0f, 5.35f),
                spacing: 0.65f,
                latticePhase: new CoreVector3(0.25f, 0.4f, 0.15f),
                purpose: SimpleDdgiVolumePurpose.ReceiverHero,
                priority: 20));
        settings.GlobalIllumination.SimpleDdgiThinSurfaceTransmissionEnabled = true;
        settings.GlobalIllumination.DdgiTransparentGeometryMode =
            DdgiTransparentGeometryMode.StochasticBlend;
        settings.GlobalIllumination.SimpleDdgiRoughSpecularEnabled = true;
        settings.GlobalIllumination.GiCausticMode = GiCausticMode.WorldCacheExperiment;
        settings.Transparency.Enabled = true;
        settings.Transparency.ReceiveGlobalIllumination = true;
        settings.Transparency.SampleReflections = true;
        settings.Transparency.ThickTransmissionMode = ThickTransmissionMode.RayQuery;
        settings.Transparency.DispersionMode = DispersionMode.RgbTriplet;
        settings.Transparency.ThickTransmissionMaximumDistance = 30f;
        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.75f;
        settings.Environment.DiffuseIntensity = 0.8f;
        settings.Environment.SpecularIntensity = 1.0f;
        settings.Reflections.Enabled = true;
        settings.Reflections.Mode = ReflectionMode.HybridRayQuery;
        settings.Reflections.Intensity = 1.0f;
        settings.Fog.Enabled = false;
        settings.Bloom.Enabled = true;
        settings.Bloom.Intensity = 0.12f;
        settings.AmbientOcclusion.Enabled = true;
    }

    public static void Configure(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager,
        TextureManager textureManager)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (meshManager == null)
            throw new ArgumentNullException(nameof(meshManager));
        if (materialManager == null)
            throw new ArgumentNullException(nameof(materialManager));
        if (textureManager == null)
            throw new ArgumentNullException(nameof(textureManager));

        scene.Name = "Njulf Material Showcase";
        scene.AmbientLight = new Njulf.Core.Math.Color(0.055f, 0.06f, 0.07f, 1f);

        MeshHandle floorMesh = meshManager.RegisterMesh(CreateFloorVertices(), CreateFloorIndices());
        GPUVertex[] sphereVertices = SampleUvSphereMesh.CreateVertices();
        uint[] sphereIndices = SampleUvSphereMesh.CreateIndices();
        MeshHandle sphereMesh = RegisterMeshWithCausticTopology(
            meshManager,
            sphereVertices,
            sphereIndices,
            "material-showcase sphere");
        MeshHandle boxMesh = meshManager.RegisterMesh(CreateBoxVertices(), CreateBoxIndices());
        GPUVertex[] waterVertices = CreateWaterVertices();
        uint[] waterIndices = CreateFloorIndices();
        MeshHandle waterMesh = RegisterMeshWithCausticTopology(
            meshManager,
            waterVertices,
            waterIndices,
            "material-showcase water surface");
        TextureHandle waterNormalTexture = CreateWaterNormalTexture(textureManager);

        AddObject(
            scene,
            floorMesh,
            CreateMaterial(materialManager, new CoreVector3(0.32f, 0.34f, 0.32f), metallic: 0.0f, roughness: 0.68f),
            "MaterialShowcase.Floor",
            CoreMatrix4x4.Identity);

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "ReflectionTest.Chrome",
                new CoreVector3(0.95f, 0.96f, 1.0f),
                metallic: 1.0f,
                roughness: 0.04f,
                MaterialFeatureFlags.Specular,
                extension => extension with
                {
                    SpecularFactor = 1f,
                    SpecularColorFactor = CoreVector3.One,
                    CausticCasterPolicy = GiCausticCasterPolicy.Mirror
                }),
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
            CreateExtensionMaterial(
                materialManager,
                "ReflectionTest.BrushedMetal",
                new CoreVector3(0.72f, 0.72f, 0.70f),
                metallic: 1.0f,
                roughness: 0.42f,
                MaterialFeatureFlags.Specular,
                extension => extension with
                {
                    SpecularFactor = 1f,
                    SpecularColorFactor = CoreVector3.One,
                    CausticCasterPolicy = GiCausticCasterPolicy.RoughSpecular
                }),
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
                MaterialFeatureFlags.Transmission | MaterialFeatureFlags.Ior,
                extension => extension with
                {
                    TransmissionFactor = 0.85f,
                    TransmissionPolicy = GiTransmissionPolicy.ThinSurface,
                    ThinTransmissionTint = new CoreVector3(0.78f, 0.95f, 1.0f),
                    Ior = 1.45f,
                    ThicknessFactor = 0f,
                    CausticCasterPolicy = GiCausticCasterPolicy.Disabled
                },
                blendMode: null),
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
                },
                shadingModel: MaterialShadingModel.SubsurfaceApproximation),
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
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.VolumeApproximation |
                MaterialFeatureFlags.Ior,
                extension => extension with
                {
                    TransmissionFactor = 0.92f,
                    TransmissionPolicy = GiTransmissionPolicy.Volume,
                    OpticalBoundary = OpticalBoundaryKind.ClosedVolume,
                    CausticCasterPolicy = GiCausticCasterPolicy.DielectricPriority,
                    Ior = 1.48f,
                    ThicknessFactor = 0.65f,
                    AttenuationDistance = 1.4f,
                    AttenuationColor = new CoreVector3(0.55f, 1.0f, 0.62f)
                },
                blendMode: null),
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
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.VolumeApproximation |
                MaterialFeatureFlags.Ior |
                MaterialFeatureFlags.Dispersion,
                extension => extension with
                {
                    TransmissionFactor = 0.9f,
                    TransmissionPolicy = GiTransmissionPolicy.Volume,
                    OpticalBoundary = OpticalBoundaryKind.ClosedVolume,
                    CausticCasterPolicy = GiCausticCasterPolicy.DielectricPriority,
                    Ior = 1.55f,
                    ThicknessFactor = 0.75f,
                    AttenuationDistance = 8f,
                    AttenuationColor = new CoreVector3(0.98f, 0.99f, 1.0f),
                    Dispersion = 0.8f
                },
                blendMode: null),
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
                surfaceFlags: MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows,
                shadingModel: MaterialShadingModel.Foliage),
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

        AddSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "Transport.RoughColoredGlass",
                new CoreVector3(0.38f, 0.74f, 1.0f),
                metallic: 0f,
                roughness: 0.27f,
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.VolumeApproximation |
                MaterialFeatureFlags.Ior,
                extension => extension with
                {
                    TransmissionFactor = 0.94f,
                    TransmissionPolicy = GiTransmissionPolicy.Volume,
                    OpticalBoundary = OpticalBoundaryKind.ClosedVolume,
                    CausticCasterPolicy = GiCausticCasterPolicy.DielectricPriority,
                    Ior = 1.50f,
                    ThicknessFactor = 0.8f,
                    AttenuationDistance = 0.7f,
                    AttenuationColor = new CoreVector3(0.30f, 0.68f, 1.0f)
                },
                blendMode: null),
            "Transport.RoughColoredGlass",
            new CoreVector3(-1.8f, SphereCenterY, 4.05f));

        CoreVector3 nestedCenter = new(-0.45f, 0.70f, 4.05f);
        AddScaledSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "Transport.NestedAmberLiquid",
                new CoreVector3(1.0f, 0.62f, 0.16f),
                metallic: 0f,
                roughness: 0.06f,
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.VolumeApproximation |
                MaterialFeatureFlags.Ior,
                extension => extension with
                {
                    TransmissionFactor = 0.93f,
                    TransmissionPolicy = GiTransmissionPolicy.Volume,
                    OpticalBoundary = OpticalBoundaryKind.ClosedVolume,
                    CausticCasterPolicy = GiCausticCasterPolicy.DielectricPriority,
                    Ior = 1.33f,
                    ThicknessFactor = 0.54f,
                    AttenuationDistance = 0.38f,
                    AttenuationColor = new CoreVector3(1.0f, 0.52f, 0.08f)
                },
                blendMode: null),
            "Transport.NestedAmberLiquid",
            nestedCenter,
            radius: 0.27f);
        AddScaledSphere(
            scene,
            sphereMesh,
            CreateExtensionMaterial(
                materialManager,
                "Transport.NestedGlassShell",
                new CoreVector3(0.96f, 0.99f, 1.0f),
                metallic: 0f,
                roughness: 0.015f,
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.VolumeApproximation |
                MaterialFeatureFlags.Ior,
                extension => extension with
                {
                    TransmissionFactor = 0.97f,
                    TransmissionPolicy = GiTransmissionPolicy.Volume,
                    OpticalBoundary = OpticalBoundaryKind.ClosedVolume,
                    CausticCasterPolicy = GiCausticCasterPolicy.DielectricPriority,
                    Ior = 1.52f,
                    ThicknessFactor = 1.16f,
                    AttenuationDistance = 12f,
                    AttenuationColor = new CoreVector3(0.96f, 0.99f, 1.0f)
                },
                blendMode: null),
            "Transport.NestedGlassShell",
            nestedCenter,
            radius: 0.58f);

        const float poolCenterX = 2.15f;
        const float poolCenterZ = 4.05f;
        const float poolWidth = 2.25f;
        const float poolDepth = 1.55f;
        MaterialHandle poolMaterial = CreateMaterial(
            materialManager,
            new CoreVector3(0.52f, 0.58f, 0.60f),
            metallic: 0f,
            roughness: 0.72f);
        AddBox(
            scene,
            boxMesh,
            poolMaterial,
            "WaterShowcase.CausticReceiver.Bottom",
            new CoreVector3(poolCenterX, 0.06f, poolCenterZ),
            new CoreVector3(poolWidth, 0.12f, poolDepth));
        AddBox(
            scene,
            boxMesh,
            poolMaterial,
            "WaterShowcase.Rim.Left",
            new CoreVector3(poolCenterX - poolWidth * 0.5f - 0.08f, 0.22f, poolCenterZ),
            new CoreVector3(0.16f, 0.44f, poolDepth + 0.32f));
        AddBox(
            scene,
            boxMesh,
            poolMaterial,
            "WaterShowcase.Rim.Right",
            new CoreVector3(poolCenterX + poolWidth * 0.5f + 0.08f, 0.22f, poolCenterZ),
            new CoreVector3(0.16f, 0.44f, poolDepth + 0.32f));
        AddBox(
            scene,
            boxMesh,
            poolMaterial,
            "WaterShowcase.Rim.Near",
            new CoreVector3(poolCenterX, 0.22f, poolCenterZ + poolDepth * 0.5f + 0.08f),
            new CoreVector3(poolWidth + 0.32f, 0.44f, 0.16f));
        AddBox(
            scene,
            boxMesh,
            poolMaterial,
            "WaterShowcase.Rim.Far",
            new CoreVector3(poolCenterX, 0.22f, poolCenterZ - poolDepth * 0.5f - 0.08f),
            new CoreVector3(poolWidth + 0.32f, 0.44f, 0.16f));

        MaterialHandle waterMaterial = CreateExtensionMaterial(
            materialManager,
            "WaterShowcase.MovingWater",
            new CoreVector3(0.12f, 0.52f, 0.68f),
            metallic: 0f,
            roughness: 0.075f,
            MaterialFeatureFlags.Transmission |
            MaterialFeatureFlags.VolumeApproximation |
            MaterialFeatureFlags.Ior,
            extension => extension with
            {
                TransmissionFactor = 0.97f,
                TransmissionPolicy = GiTransmissionPolicy.Volume,
                OpticalBoundary = OpticalBoundaryKind.WaterSurface,
                CausticCasterPolicy = GiCausticCasterPolicy.DielectricPriority,
                Ior = 1.333f,
                ThicknessFactor = 0.26f,
                AttenuationDistance = 2.8f,
                AttenuationColor = new CoreVector3(0.34f, 0.84f, 0.91f),
                WaterNormalVelocity0 = new CoreVector2(0.045f, 0.018f),
                WaterNormalVelocity1 = new CoreVector2(-0.024f, 0.036f),
                WaterNormalUvScale0 = 5.0f,
                WaterNormalUvScale1 = 8.5f
            },
            blendMode: null,
            normal: CreateBinding(waterNormalTexture),
            normalScale: 0.72f);
        AddObject(
            scene,
            waterMesh,
            waterMaterial,
            "WaterShowcase.MovingWaterSurface",
            CoreMatrix4x4.CreateScale(new CoreVector3(poolWidth, 1f, poolDepth)) *
            CoreMatrix4x4.CreateTranslation(
                new CoreVector3(poolCenterX, 0.32f, poolCenterZ)));

        MaterialHandle backdropMaterial = CreateMaterial(
            materialManager,
            new CoreVector3(0.30f, 0.32f, 0.36f),
            metallic: 0f,
            roughness: 0.82f);
        AddBox(
            scene,
            boxMesh,
            backdropMaterial,
            "MaterialTypes.GiBounceBackdrop",
            new CoreVector3(0f, 1.3f, -0.75f),
            new CoreVector3(8.8f, 2.6f, 0.12f));

        AddMaterialTypePanel(
            scene,
            boxMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.10f, 0.92f, 0.88f),
                metallic: 0f,
                roughness: 0.5f,
                MaterialRenderMode.Opaque,
                MaterialBlendMode.Opaque,
                alpha: 1f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided,
                shadingModel: MaterialShadingModel.Unlit,
                emissive: new CoreVector3(0.10f, 0.92f, 0.88f)),
            "MaterialTypes.Unlit",
            -2.4f);
        AddMaterialTypePanel(
            scene,
            boxMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.86f, 0.24f, 0.72f),
                metallic: 0f,
                roughness: 0.32f,
                MaterialRenderMode.Blend,
                MaterialBlendMode.AlphaBlend,
                alpha: 0.52f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided | MaterialSurfaceFlags.ReceivesShadows),
            "MaterialTypes.AlphaBlend",
            -1.2f);
        AddMaterialTypePanel(
            scene,
            boxMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(1.0f, 0.30f, 0.05f),
                metallic: 0f,
                roughness: 0.25f,
                MaterialRenderMode.Blend,
                MaterialBlendMode.Additive,
                alpha: 0.66f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided),
            "MaterialTypes.Additive",
            0f);
        AddMaterialTypePanel(
            scene,
            boxMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.16f, 0.28f, 0.72f),
                metallic: 0f,
                roughness: 0.6f,
                MaterialRenderMode.Blend,
                MaterialBlendMode.Multiply,
                alpha: 0.62f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.DoubleSided),
            "MaterialTypes.Multiply",
            1.2f);
        AddMaterialTypePanel(
            scene,
            boxMesh,
            CreateRenderModeMaterial(
                materialManager,
                new CoreVector3(0.92f, 0.78f, 0.12f),
                metallic: 0f,
                roughness: 0.38f,
                MaterialRenderMode.Opaque,
                MaterialBlendMode.Opaque,
                alpha: 1f,
                featureFlags: MaterialFeatureFlags.None,
                surfaceFlags: MaterialSurfaceFlags.GeometryDecal |
                              MaterialSurfaceFlags.ReceivesShadows,
                shadingModel: MaterialShadingModel.Decal),
            "MaterialTypes.GeometryDecal",
            2.4f);
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
        AddScaledSphere(scene, mesh, material, name, position, SphereRadius);
    }

    private static void AddScaledSphere(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreVector3 position,
        float radius)
    {
        scene.Add(new RenderObject(mesh, material)
        {
            Name = name,
            WorldMatrix = CoreMatrix4x4.CreateScale(new CoreVector3(radius)) *
                          CoreMatrix4x4.CreateTranslation(position),
            Visible = true
        });
    }

    private static void AddBox(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        CoreVector3 position,
        CoreVector3 scale)
    {
        AddObject(
            scene,
            mesh,
            material,
            name,
            CoreMatrix4x4.CreateScale(scale) *
            CoreMatrix4x4.CreateTranslation(position));
    }

    private static void AddMaterialTypePanel(
        Scene scene,
        MeshHandle mesh,
        MaterialHandle material,
        string name,
        float x)
    {
        AddBox(
            scene,
            mesh,
            material,
            name,
            new CoreVector3(x, 1.72f, -0.66f),
            new CoreVector3(0.82f, 0.58f, 0.035f));
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
        MaterialSurfaceFlags surfaceFlags,
        MaterialShadingModel shadingModel = MaterialShadingModel.Pbr,
        CoreVector3 emissive = default,
        float emissiveStrength = 1f)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = $"MaterialShowcase.{blendMode}",
            BaseColorFactor = new CoreVector4(albedo, Math.Clamp(alpha, 0f, 1f)),
            EmissiveFactor = emissive,
            EmissiveStrength = emissiveStrength,
            MetallicFactor = Math.Clamp(metallic, 0f, 1f),
            RoughnessFactor = Math.Clamp(roughness, 0.04f, 1f),
            AlphaMode = ToAlphaMode(renderMode),
            AlphaCutoff = 0.5f,
            DoubleSided = surfaceFlags.HasFlag(MaterialSurfaceFlags.DoubleSided),
            ReceivesShadows = surfaceFlags.HasFlag(MaterialSurfaceFlags.ReceivesShadows),
            RenderBlendModeOverride = blendMode,
            ShadingModel = shadingModel,
            IsGeometryDecal = surfaceFlags.HasFlag(MaterialSurfaceFlags.GeometryDecal),
            DecalLayer = surfaceFlags.HasFlag(MaterialSurfaceFlags.GeometryDecal) ? 1 : 0,
            DecalDepthBias = surfaceFlags.HasFlag(MaterialSurfaceFlags.GeometryDecal) ? 0.001f : 0f,
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
        MaterialBlendMode? blendMode = MaterialBlendMode.Opaque,
        CoreVector3 emissive = default,
        float emissiveStrength = 1f,
        MaterialTextureBinding? normal = null,
        float normalScale = 1f,
        MaterialShadingModel shadingModel = MaterialShadingModel.Pbr)
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
            Normal = normal ?? MaterialTextureBinding.Missing,
            NormalScale = Math.Clamp(normalScale, 0f, 2f),
            ShadingModel = shadingModel,
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

    private static bool IsTransparent(MaterialBlendMode? blendMode) =>
        blendMode is MaterialBlendMode.AlphaBlend or
            MaterialBlendMode.PremultipliedAlpha or
            MaterialBlendMode.Additive or
            MaterialBlendMode.Multiply;

    private static MaterialTextureBinding CreateBinding(TextureHandle texture) =>
        new()
        {
            Texture = texture,
            Sampler = TextureSamplerDescription.Default,
            TexCoordSet = 0,
            Offset = CoreVector2.Zero,
            Scale = CoreVector2.One,
            RotationRadians = 0f
        };

    private static MeshHandle RegisterMeshWithCausticTopology(
        MeshManager meshManager,
        GPUVertex[] vertices,
        uint[] indices,
        string debugName)
    {
        var positions = new CoreVector3[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
            positions[index] = vertices[index].Position;

        if (!ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
                positions,
                indices,
                isSkinned: false,
                out ModelGiCausticHeroTopologyEvidence evidence,
                out string reason))
        {
            throw new InvalidOperationException(
                $"Could not analyze {debugName} topology: {reason}.");
        }

        return meshManager.RegisterMeshes(
        [
            new MeshManager.MeshRegistrationData(
                vertices,
                indices,
                generateMeshlets: true,
                causticTopologyEvidence: evidence)
        ])[0];
    }

    private static TextureHandle CreateWaterNormalTexture(TextureManager textureManager)
    {
        const uint textureSize = 64;
        const uint mipLevels = 7;
        byte[] pixels = CreateWaterNormalPixels(textureSize);
        TextureHandle texture = textureManager.CreateTexture(
            textureSize,
            textureSize,
            Format.R8G8B8A8Unorm,
            mipLevels,
            debugName: "MaterialShowcase.MovingWaterNormal");
        textureManager.UploadTextureData(
            texture,
            pixels,
            textureSize,
            textureSize,
            Format.R8G8B8A8Unorm,
            generateMipmaps: true);
        return texture;
    }

    private static byte[] CreateWaterNormalPixels(uint textureSize)
    {
        int size = checked((int)textureSize);
        var pixels = new byte[checked(size * size * 4)];
        for (int y = 0; y < size; y++)
        {
            float v = (float)y / size;
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / size;
                float phase0 = MathF.Tau * (2f * u + v);
                float phase1 = MathF.Tau * (-u + 3f * v) + 0.73f;
                float phase2 = MathF.Tau * (4f * u - 2f * v) + 1.91f;
                float nx = -(
                    0.20f * MathF.Cos(phase0) -
                    0.11f * MathF.Cos(phase1) +
                    0.06f * MathF.Cos(phase2));
                float ny = -(
                    0.10f * MathF.Cos(phase0) +
                    0.24f * MathF.Cos(phase1) -
                    0.05f * MathF.Cos(phase2));
                const float nz = 1f;
                float inverseLength = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                nx *= inverseLength;
                ny *= inverseLength;
                int pixel = checked((y * size + x) * 4);
                pixels[pixel] = EncodeSignedNormal(nx);
                pixels[pixel + 1] = EncodeSignedNormal(ny);
                pixels[pixel + 2] = EncodeSignedNormal(nz * inverseLength);
                pixels[pixel + 3] = byte.MaxValue;
            }
        }
        return pixels;
    }

    private static byte EncodeSignedNormal(float value) =>
        (byte)Math.Clamp(
            (int)MathF.Round((value * 0.5f + 0.5f) * byte.MaxValue),
            byte.MinValue,
            byte.MaxValue);

    private static GPUVertex[] CreateFloorVertices()
    {
        const float halfWidth = 4.8f;
        const float nearZ = -1.0f;
        const float farZ = 5.3f;

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

    private static GPUVertex[] CreateWaterVertices()
    {
        return
        [
            CreateFloorVertex(-0.5f, 0f, -0.5f, 0f, 0f),
            CreateFloorVertex(0.5f, 0f, -0.5f, 1f, 0f),
            CreateFloorVertex(0.5f, 0f, 0.5f, 1f, 1f),
            CreateFloorVertex(-0.5f, 0f, 0.5f, 0f, 1f)
        ];
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
