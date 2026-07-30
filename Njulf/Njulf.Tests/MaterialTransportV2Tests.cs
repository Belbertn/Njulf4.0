using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialTransportV2Tests
{
    [Test]
    public void CpuOracle_ImplementsMetalEmissionAoAlphaAndSidednessInvariants()
    {
        Vector3 metallicDiffuse = GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
            new Vector3(1f, 0.5f, 0.25f),
            metallic: 1f);

        Assert.Multiple(() =>
        {
            Assert.That(metallicDiffuse, Is.EqualTo(Vector3.Zero));
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateEmission(
                    new Vector3(0.5f, 0.25f, 0.125f),
                    new Vector3(0.25f, 0.5f, 1f),
                    0f),
                Is.EqualTo(Vector3.Zero));
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateEmission(
                    new Vector3(0.5f, 0.25f, 0.125f),
                    new Vector3(0.25f, 0.5f, 1f),
                    10f),
                Is.EqualTo(new Vector3(1.25f, 1.25f, 1.25f)));
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateMaterialOcclusion(0f, 0.1f),
                Is.EqualTo(1f));
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateMaterialOcclusion(0.5f, 0.2f),
                Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateOpacity(0.5f, MaterialAlphaMode.Mask, 0.5f),
                Is.True);
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateOpacity(1f, MaterialAlphaMode.Mask, 1.01f),
                Is.False);
            Assert.That(GiMaterialReferenceEvaluator.EvaluateSidedness(false, false), Is.False);
            Assert.That(GiMaterialReferenceEvaluator.EvaluateSidedness(true, false), Is.True);
        });
    }

    [Test]
    public void CpuOracle_DirectionalDiffuseUsesBothAnglesMaterialF0AndPassiveBaseEnergy()
    {
        Vector3 baseColor = new(0.9f, 0.7f, 0.4f);
        Vector3 normalIncidence = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            baseColor,
            metallic: 0f,
            nDotL: 1f,
            nDotV: 1f,
            ior: 1.5f);
        Vector3 grazingIncoming = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            baseColor,
            metallic: 0f,
            nDotL: 0.15f,
            nDotV: 1f,
            ior: 1.5f);
        Vector3 grazingOutgoing = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            baseColor,
            metallic: 0f,
            nDotL: 1f,
            nDotV: 0.15f,
            ior: 1.5f);
        Vector3 zeroF0 = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            baseColor,
            metallic: 0f,
            nDotL: 0.5f,
            nDotV: 0.5f,
            ior: 1f);
        Vector3 highF0 = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            baseColor,
            metallic: 0f,
            nDotL: 0.5f,
            nDotV: 0.5f,
            ior: 3f,
            specularFactor: 1f,
            specularColor: Vector3.One);
        Vector3 directionalBase =
            GiMaterialReferenceEvaluator.EvaluateDirectionalDiffuseBase(
                baseColor,
                metallic: 0f,
                transmission: 0.2f,
                clearcoat: 0.75f,
                sheenColor: new Vector3(0.1f, 0.2f, 0.3f));
        Vector3 passive = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            directionalBase,
            GiMaterialReferenceEvaluator.EvaluateMaterialDielectricF0(
                2.25f,
                0.8f,
                new Vector3(1f, 0.7f, 0.4f)),
            nDotL: 0.65f,
            nDotV: 0.35f);
        Vector3 metallic = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            Vector3.One,
            metallic: 1f,
            nDotL: 1f,
            nDotV: 1f,
            ior: 3f);

        Assert.Multiple(() =>
        {
            Assert.That(grazingIncoming.X, Is.LessThan(normalIncidence.X));
            Assert.That(grazingOutgoing.X, Is.LessThan(normalIncidence.X));
            Assert.That(highF0.X, Is.LessThan(zeroF0.X));
            Assert.That(highF0.Y, Is.LessThan(zeroF0.Y));
            Assert.That(highF0.Z, Is.LessThan(zeroF0.Z));
            Assert.That(passive.X * GiMaterialReferenceEvaluator.Pi, Is.LessThanOrEqualTo(directionalBase.X));
            Assert.That(passive.Y * GiMaterialReferenceEvaluator.Pi, Is.LessThanOrEqualTo(directionalBase.Y));
            Assert.That(passive.Z * GiMaterialReferenceEvaluator.Pi, Is.LessThanOrEqualTo(directionalBase.Z));
            Assert.That(metallic, Is.EqualTo(Vector3.Zero));
        });
    }

    [TestCase(1.0f, 1.0f)]
    [TestCase(1.5f, 0.65f)]
    [TestCase(2.25f, 0.37f)]
    [TestCase(3.0f, 0.2f)]
    public void CpuOracle_HemisphericalResponseMatchesIndependentDirectionalBrdfQuadrature(
        float ior,
        float nDotV)
    {
        const int sampleCount = 65_536;
        Vector3 baseColor = new(0.8f, 0.55f, 0.3f);
        const float metallic = 0.2f;
        const float specularFactor = 0.8f;
        Vector3 specularColor = new(0.9f, 0.6f, 0.25f);
        const float transmission = 0.15f;
        const float clearcoat = 0.4f;
        Vector3 sheenColor = new(0.1f, 0.2f, 0.3f);
        Vector3 directionalBase =
            GiMaterialReferenceEvaluator.EvaluateDirectionalDiffuseBase(
                baseColor,
                metallic,
                transmission,
                clearcoat,
                sheenColor);
        Vector3 dielectricF0 =
            GiMaterialReferenceEvaluator.EvaluateMaterialDielectricF0(
                ior,
                specularFactor,
                specularColor);
        Vector3 hemispherical =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                baseColor,
                metallic,
                ior,
                specularFactor,
                specularColor,
                transmission,
                clearcoat,
                sheenColor,
                nDotV);

        // Unit incident radiance has irradiance PI. Integrate the directional
        // BRDF independently over solid angle using midpoint quadrature.
        double integratedR = 0.0;
        double integratedG = 0.0;
        double integratedB = 0.0;
        double deltaMu = 1.0 / sampleCount;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            float nDotL = (float)((sample + 0.5) * deltaMu);
            Vector3 brdf = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
                directionalBase,
                dielectricF0,
                nDotL,
                nDotV);
            double solidAngleCosineWeight =
                2.0 * Math.PI * nDotL * deltaMu;
            integratedR += brdf.X * solidAngleCosineWeight;
            integratedG += brdf.Y * solidAngleCosineWeight;
            integratedB += brdf.Z * solidAngleCosineWeight;
        }

        Assert.Multiple(() =>
        {
            Assert.That(integratedR, Is.EqualTo(hemispherical.X).Within(2e-6));
            Assert.That(integratedG, Is.EqualTo(hemispherical.Y).Within(2e-6));
            Assert.That(integratedB, Is.EqualTo(hemispherical.Z).Within(2e-6));
        });
    }

    [Test]
    public void CpuOracle_HemisphericalResponseHasExactCosineWeightedSchlickEnergy()
    {
        float zeroF0 =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                Vector3.One,
                metallic: 0f,
                ior: 1f).X;
        float defaultF0 =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                Vector3.One,
                metallic: 0f,
                ior: 1.5f).X;
        float highF0 =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                Vector3.One,
                metallic: 0f,
                ior: 3f).X;

        Assert.Multiple(() =>
        {
            Assert.That(
                GiMaterialReferenceEvaluator.SchlickCosineWeightedTransmission,
                Is.EqualTo(20f / 21f));
            Assert.That(zeroF0, Is.EqualTo(20f / 21f).Within(1e-7f));
            Assert.That(
                defaultF0,
                Is.EqualTo((20f / 21f) * 0.96f * 0.96f).Within(1e-7f));
            Assert.That(
                highF0,
                Is.EqualTo((20f / 21f) * 0.75f * 0.75f).Within(1e-7f));
        });
    }

    [Test]
    public void IorFeature_ChangesOpaqueDielectricF0WithoutEnablingTransmission()
    {
        MaterialDefinition lowIor = new()
        {
            Name = "Opaque IOR 1",
            FeatureFlags = MaterialFeatureFlags.Ior,
            BaseColorFactor = Vector4.One,
            Extensions = MaterialExtensionDefinition.None with { Ior = 1f }
        };
        MaterialDefinition highIor = lowIor with
        {
            Name = "Opaque IOR 3",
            Extensions = lowIor.Extensions with { Ior = 3f }
        };

        GiSurfaceSample lowSurface =
            GiMaterialReferenceEvaluator.EvaluateSurface(lowIor, GiMaterialSampleInputs.Defaults);
        GiSurfaceSample highSurface =
            GiMaterialReferenceEvaluator.EvaluateSurface(highIor, GiMaterialSampleInputs.Defaults);
        CompiledMaterialTransport lowCompiled = MaterialTransportCompiler.Compile(lowIor);
        CompiledMaterialTransport highCompiled = MaterialTransportCompiler.Compile(highIor);

        Assert.Multiple(() =>
        {
            Assert.That(lowSurface.DielectricF0, Is.EqualTo(Vector3.Zero));
            Assert.That(highSurface.DielectricF0, Is.EqualTo(new Vector3(0.25f)));
            Assert.That(highSurface.DirectionalDiffuseBase, Is.EqualTo(Vector3.One));
            Assert.That(
                lowSurface.DiffuseReflectance.X,
                Is.EqualTo(20f / 21f).Within(1e-7f));
            Assert.That(
                highSurface.DiffuseReflectance.X,
                Is.EqualTo((20f / 21f) * 0.75f * 0.75f).Within(1e-7f));
            Assert.That(
                highSurface.DiffuseReflectance.X,
                Is.LessThan(lowSurface.DiffuseReflectance.X));
            Assert.That(highCompiled.ExtensionData, Is.Not.Null);
            Assert.That(highCompiled.ExtensionData!.Value.Transmission.Y, Is.EqualTo(3f));
            Assert.That(
                highCompiled.GpuMaterial.FeatureFlags,
                Is.EqualTo((uint)MaterialFeatureFlags.Ior));
            Assert.That(
                ((GiMaterialTransportFlags)highCompiled.GpuMaterial.TransportFlags)
                .HasFlag(GiMaterialTransportFlags.TransmissionRemovesOpaqueDiffuse),
                Is.False);
            Assert.That(
                highCompiled.TransportProfile.MeanDiffuseReflectance.X,
                Is.LessThan(lowCompiled.TransportProfile.MeanDiffuseReflectance.X));
            Assert.That(MaterialCompilationContext.CurrentAlgorithmVersion, Is.EqualTo(3u));
            Assert.That(highCompiled.TransportProfile.AlgorithmVersion, Is.EqualTo(3u));
        });
    }

    [Test]
    public void AlphaContract_HasExactMaskAndBlendSemanticsForRasterAndOpaqueTransport()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                MaterialAlphaCoverageContract.SurvivesRasterCoverage(
                    0.5f,
                    MaterialAlphaMode.Mask,
                    0.5f),
                Is.True);
            Assert.That(
                MaterialAlphaCoverageContract.OccupiesOpaqueTransport(
                    0.5f,
                    MaterialAlphaMode.Mask,
                    0.5f),
                Is.True);
            Assert.That(
                MaterialAlphaCoverageContract.SurvivesRasterCoverage(
                    1f,
                    MaterialAlphaMode.Mask,
                    1.01f),
                Is.False);
            Assert.That(
                MaterialAlphaCoverageContract.OccupiesOpaqueTransport(
                    1f,
                    MaterialAlphaMode.Mask,
                    1.01f),
                Is.False);
            Assert.That(
                MaterialAlphaCoverageContract.SurvivesRasterCoverage(
                    0f,
                    MaterialAlphaMode.Mask,
                    -0.01f),
                Is.True,
                "A negative authored cutoff must not be clamped before raster comparison.");
            Assert.That(
                MaterialAlphaCoverageContract.OccupiesOpaqueTransport(
                    0f,
                    MaterialAlphaMode.Mask,
                    -0.01f),
                Is.True,
                "DDGI/far-field opaque occupancy must use the same unclamped cutoff.");
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateOpacity(
                    0f,
                    MaterialAlphaMode.Mask,
                    -0.01f),
                Is.True);
            Assert.That(
                FarFieldMaterialPayloadV2.SurvivesAlpha(
                    0f,
                    MaterialAlphaMode.Mask,
                    -0.01f),
                Is.True);
            Assert.That(
                MaterialAlphaCoverageContract.SurvivesRasterCoverage(
                    0.25f,
                    MaterialAlphaMode.Blend,
                    0.5f),
                Is.True);
            Assert.That(
                MaterialAlphaCoverageContract.OccupiesOpaqueTransport(
                    0.25f,
                    MaterialAlphaMode.Blend,
                    0.5f),
                Is.False);
            Assert.That(
                GiMaterialReferenceEvaluator.EvaluateOpacity(
                    0.25f,
                    MaterialAlphaMode.Blend,
                    0.5f),
                Is.True);
            Assert.That(
                FarFieldMaterialPayloadV2.SurvivesAlpha(
                    0.25f,
                    MaterialAlphaMode.Blend,
                    0.5f),
                Is.False);
        });
    }

    [Test]
    public void CpuOracle_UnlitDefaultsToVisibilityOnlyButExplicitEmissionCanOptIn()
    {
        var unlit = new MaterialDefinition
        {
            ShadingModel = MaterialShadingModel.Unlit,
            BaseColorFactor = new Vector4(0.8f, 0.4f, 0.2f, 0.75f),
            EmissiveFactor = Vector3.One,
            EmissiveStrength = 4f
        };
        GiSurfaceSample defaultSurface =
            GiMaterialReferenceEvaluator.EvaluateSurface(unlit, GiMaterialSampleInputs.Defaults);
        GiSurfaceSample emittingSurface = GiMaterialReferenceEvaluator.EvaluateSurface(
            unlit with { EmissionGiParticipation = GiParticipationOverride.Enabled },
            GiMaterialSampleInputs.Defaults);

        Assert.Multiple(() =>
        {
            Assert.That(defaultSurface.DiffuseReflectance, Is.EqualTo(Vector3.Zero));
            Assert.That(defaultSurface.EmissiveRadiance, Is.EqualTo(Vector3.Zero));
            Assert.That(defaultSurface.Opacity, Is.EqualTo(0.75f));
            Assert.That(emittingSurface.DiffuseReflectance, Is.EqualTo(Vector3.Zero));
            Assert.That(emittingSurface.EmissiveRadiance, Is.EqualTo(new Vector3(4f, 4f, 4f)));
        });
    }

    [Test]
    public void Compiler_PreservesZeroAuthoredRoughnessAndFloorsOnlyEvaluatedTransport()
    {
        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(
            new MaterialDefinition { RoughnessFactor = 0f });
        float compactRoughness = (float)BitConverter.UInt16BitsToHalf(
            (ushort)(compiled.GpuMaterial.PackedMeanMetallicRoughness >> 16));

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Definition.RoughnessFactor, Is.Zero);
            Assert.That(compiled.GpuMaterial.MetallicRoughnessAO.Y, Is.Zero);
            Assert.That(
                compactRoughness,
                Is.EqualTo(GiMaterialReferenceEvaluator.MinimumRoughness).Within(0.0001f));
        });
    }

    [Test]
    public void Compiler_PacksAndMaterialManagerPreservesCompactDirectionalTransport()
    {
        var definition = new MaterialDefinition
        {
            BaseColorFactor = new Vector4(0.8f, 0.6f, 0.35f, 1f),
            MetallicFactor = 0.2f,
            FeatureFlags =
                MaterialFeatureFlags.Clearcoat |
                MaterialFeatureFlags.Sheen |
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.Specular,
            Extensions = MaterialExtensionDefinition.None with
            {
                ClearcoatFactor = 0.65f,
                SheenColorFactor = new Vector3(0.1f, 0.2f, 0.3f),
                TransmissionFactor = 0.15f,
                Ior = 2.1f,
                SpecularFactor = 0.75f,
                SpecularColorFactor = new Vector3(1f, 0.7f, 0.45f)
            }
        };
        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(definition);
        GiSurfaceSample expected = GiMaterialReferenceEvaluator.EvaluateSurface(
            definition,
            GiMaterialSampleInputs.Defaults);

        (float baseR, float baseG) =
            UnpackHalf2(compiled.GpuMaterial.PackedMeanGiDirectionalDiffuseBaseRg);
        (float baseB, float f0R) =
            UnpackHalf2(compiled.GpuMaterial.PackedMeanGiDirectionalDiffuseBaseBAndF0R);
        (float f0G, float f0B) =
            UnpackHalf2(compiled.GpuMaterial.PackedMeanGiDielectricF0Gb);

        using var manager = new MaterialManager();
        MaterialHandle handle = manager.RegisterMaterialDefinition(definition);
        GPUMaterialData published = manager.GetMaterialData(handle);

        Assert.Multiple(() =>
        {
            Assert.That(baseR, Is.EqualTo(expected.DirectionalDiffuseBase.X).Within(5e-4f));
            Assert.That(baseG, Is.EqualTo(expected.DirectionalDiffuseBase.Y).Within(5e-4f));
            Assert.That(baseB, Is.EqualTo(expected.DirectionalDiffuseBase.Z).Within(5e-4f));
            Assert.That(f0R, Is.EqualTo(expected.DielectricF0.X).Within(5e-4f));
            Assert.That(f0G, Is.EqualTo(expected.DielectricF0.Y).Within(5e-4f));
            Assert.That(f0B, Is.EqualTo(expected.DielectricF0.Z).Within(5e-4f));
            Assert.That(
                published.PackedMeanGiDirectionalDiffuseBaseRg,
                Is.EqualTo(compiled.GpuMaterial.PackedMeanGiDirectionalDiffuseBaseRg));
            Assert.That(
                published.PackedMeanGiDirectionalDiffuseBaseBAndF0R,
                Is.EqualTo(compiled.GpuMaterial.PackedMeanGiDirectionalDiffuseBaseBAndF0R));
            Assert.That(
                published.PackedMeanGiDielectricF0Gb,
                Is.EqualTo(compiled.GpuMaterial.PackedMeanGiDielectricF0Gb));
        });

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void Compiler_UsesIndependentBindingsAndExplicitZeroValidity()
    {
        var baseBinding = new MaterialTextureBinding
        {
            Texture = new TextureHandle(1, 1),
            TexCoordSet = 0,
            Offset = new Vector2(0.1f, 0.2f),
            Scale = new Vector2(0.5f, 0.75f)
        };
        var occlusionBinding = new MaterialTextureBinding
        {
            Texture = new TextureHandle(2, 1),
            TexCoordSet = 1,
            Offset = new Vector2(0.3f, 0.4f),
            Scale = new Vector2(0.2f, 0.6f),
            RotationRadians = 0.25f
        };
        var definition = new MaterialDefinition
        {
            BaseColorFactor = Vector4.One,
            MetallicFactor = 1f,
            EmissiveFactor = Vector3.Zero,
            EmissiveStrength = 10f,
            AlphaMode = MaterialAlphaMode.Mask,
            AlphaCutoff = 0.5f,
            BaseColor = baseBinding,
            Occlusion = occlusionBinding
        };
        var context = new MaterialCompilationContext
        {
            ProfileRevision = 17,
            ResolveTexture = (binding, semantic) => new MaterialTextureTransportInput(
                binding.Texture.Index + 8,
                MeanValid: true,
                LinearMean: semantic == MaterialTextureSemantic.Occlusion
                    ? new Vector4(0.25f, 0f, 0f, 1f)
                    : Vector4.One,
                AlphaCoverageValid: true,
                AlphaCoverage: 0.25f,
                NormalVarianceValid: true,
                NormalVariance: 0f,
                SourceContentHash: (ulong)binding.Texture.Index)
        };

        CompiledMaterialTransport compiled =
            MaterialTransportCompiler.Compile(definition, context);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.GpuMaterial.AlbedoTextureIndex, Is.EqualTo(9));
            Assert.That(compiled.GpuMaterial.OcclusionTextureIndex, Is.EqualTo(10));
            Assert.That(compiled.GpuMaterial.BaseColorOffsetScale, Is.EqualTo(new Vector4(0.1f, 0.2f, 0.5f, 0.75f)));
            Assert.That(compiled.GpuMaterial.OcclusionOffsetScale, Is.EqualTo(new Vector4(0.3f, 0.4f, 0.2f, 0.6f)));
            Assert.That(compiled.GpuMaterial.OcclusionBinding.X, Is.EqualTo(0.25f));
            Assert.That(compiled.GpuMaterial.OcclusionBinding.Y, Is.EqualTo(1f));
            Assert.That(compiled.GpuMaterial.DdgiAverageAlbedo.X, Is.EqualTo(0f));
            Assert.That(compiled.GpuMaterial.DdgiAverageAlbedo.Y, Is.EqualTo(0f));
            Assert.That(compiled.GpuMaterial.DdgiAverageAlbedo.Z, Is.EqualTo(0f));
            Assert.That(compiled.GpuMaterial.DdgiAverageAlbedo.W, Is.EqualTo(0.25f));
            Assert.That(compiled.GpuMaterial.DdgiAverageEmissive, Is.EqualTo(Vector4.Zero));
            Assert.That(compiled.GpuMaterial.DdgiMaterialPolicy.Z, Is.EqualTo(0.25f));
            Assert.That(compiled.GpuMaterial.TransportProfileRevision, Is.EqualTo(17u));
            Assert.That(
                compiled.TransportProfile.Has(GiMaterialTransportFlags.EmissionProfileValid),
                Is.True,
                "A physical zero emission remains explicitly valid.");
            Assert.That(
                compiled.TransportProfile.Has(GiMaterialTransportFlags.AlphaProfileValid),
                Is.True);
            Assert.That(
                compiled.GpuMaterial.FeatureFlags & (uint)MaterialFeatureFlags.EmissiveStrength,
                Is.Not.EqualTo(0u));
            Assert.That(compiled.ExtensionData.HasValue, Is.True);
            Assert.That(compiled.ExtensionData!.Value.Clearcoat.W, Is.EqualTo(10f));
        });
    }

    [Test]
    public void Compiler_InvalidCompactStatistics_SelectExplicitDetailedTextureFallback()
    {
        var binding = new MaterialTextureBinding
        {
            Texture = new TextureHandle(7, 1)
        };
        var definition = new MaterialDefinition
        {
            BaseColor = binding,
            AlphaMode = MaterialAlphaMode.Mask,
            Occlusion = binding
        };
        MaterialTextureTransportInput InvalidStatistics(
            MaterialTextureBinding _,
            MaterialTextureSemantic __) => new(
                BindlessIndex: 17,
                MeanValid: false,
                LinearMean: Vector4.Zero,
                AlphaCoverageValid: false,
                AlphaCoverage: 0f,
                NormalVarianceValid: false,
                NormalVariance: 0f,
                SourceContentHash: 91);

        CompiledMaterialTransport fallback = MaterialTransportCompiler.Compile(
            definition,
            new MaterialCompilationContext
            {
                ResolveTexture = InvalidStatistics,
                AllowInvalidCompactFallback = true
            });
        CompiledMaterialTransport explicitlyDisabled = MaterialTransportCompiler.Compile(
            definition,
            new MaterialCompilationContext
            {
                ResolveTexture = InvalidStatistics,
                AllowInvalidCompactFallback = false
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                fallback.TransportProfile.Has(GiMaterialTransportFlags.DiffuseProfileValid),
                Is.False);
            Assert.That(
                fallback.TransportProfile.Has(GiMaterialTransportFlags.BaseStatisticsValid),
                Is.False,
                "Invalid occlusion statistics must not be advertised as valid base statistics.");
            Assert.That(
                fallback.TransportProfile.Has(GiMaterialTransportFlags.CompactTextureFallback),
                Is.True);
            Assert.That(
                ((GiMaterialTransportFlags)fallback.GpuMaterial.TransportFlags)
                    .HasFlag(GiMaterialTransportFlags.CompactTextureFallback),
                Is.True);
            Assert.That(
                fallback.Diagnostics,
                Has.Some.Contains("coarse DDGI hits will sample"));
            Assert.That(
                explicitlyDisabled.TransportProfile.Has(GiMaterialTransportFlags.CompactTextureFallback),
                Is.False);
        });
    }

    [Test]
    public void Compiler_ExtensionEnergyTexturesParticipateInCompactDiffuseProfile()
    {
        MaterialTextureBinding Binding(int index) => new()
        {
            Texture = new TextureHandle(index, 1)
        };

        var definition = new MaterialDefinition
        {
            FeatureFlags = MaterialFeatureFlags.Clearcoat |
                           MaterialFeatureFlags.ClearcoatTexture |
                           MaterialFeatureFlags.Sheen |
                           MaterialFeatureFlags.SheenColorTexture |
                           MaterialFeatureFlags.Transmission |
                           MaterialFeatureFlags.TransmissionTexture |
                           MaterialFeatureFlags.Specular |
                           MaterialFeatureFlags.SpecularTexture |
                           MaterialFeatureFlags.SpecularColorTexture,
            Extensions = new MaterialExtensionDefinition
            {
                ClearcoatFactor = 0.8f,
                Clearcoat = Binding(1),
                SheenColorFactor = new Vector3(0.5f, 0.25f, 0.125f),
                SheenColor = Binding(2),
                TransmissionFactor = 0.6f,
                Transmission = Binding(3),
                SpecularFactor = 0.7f,
                Specular = Binding(4),
                SpecularColorFactor = new Vector3(0.8f, 0.6f, 0.4f),
                SpecularColor = Binding(5)
            }
        };
        Vector4 MeanFor(int index) => index switch
        {
            1 => new Vector4(0.5f, 1f, 1f, 1f),
            2 => new Vector4(0.2f, 0.4f, 0.6f, 1f),
            3 => new Vector4(0.25f, 1f, 1f, 1f),
            4 => new Vector4(1f, 1f, 1f, 0.5f),
            5 => new Vector4(0.5f, 0.75f, 1f, 1f),
            _ => Vector4.One
        };
        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(
            definition,
            new MaterialCompilationContext
            {
                ResolveTexture = (binding, _) => new MaterialTextureTransportInput(
                    binding.Texture.Index + 8,
                    MeanValid: true,
                    LinearMean: MeanFor(binding.Texture.Index),
                    AlphaCoverageValid: true,
                    AlphaCoverage: 1f,
                    NormalVarianceValid: true,
                    NormalVariance: 0f,
                    SourceContentHash: (ulong)(100 + binding.Texture.Index))
            });
        Vector3 expected = GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
            Vector3.One,
            metallic: 0f,
            ior: definition.Extensions.Ior,
            specularFactor: 0.7f * 0.5f,
            specularColor: new Vector3(0.8f, 0.6f, 0.4f) * new Vector3(0.5f, 0.75f, 1f),
            transmission: 0.6f * 0.25f,
            clearcoat: 0.8f * 0.5f,
            sheenColor: new Vector3(0.5f, 0.25f, 0.125f) * new Vector3(0.2f, 0.4f, 0.6f));

        Assert.Multiple(() =>
        {
            Assert.That(compiled.TransportProfile.Quality, Is.EqualTo(GiTransportProfileQuality.TextureStatistics));
            Assert.That(compiled.TransportProfile.SourceContentHash, Is.Not.Zero);
            Assert.That(compiled.TransportProfile.MeanDiffuseReflectance.X, Is.EqualTo(expected.X).Within(1e-6f));
            Assert.That(compiled.TransportProfile.MeanDiffuseReflectance.Y, Is.EqualTo(expected.Y).Within(1e-6f));
            Assert.That(compiled.TransportProfile.MeanDiffuseReflectance.Z, Is.EqualTo(expected.Z).Within(1e-6f));
        });
    }

    [Test]
    public void Compiler_DiagnosesDirectionalAndReceiverOwnedExtensionPolicies()
    {
        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(
            new MaterialDefinition
            {
                FeatureFlags = MaterialFeatureFlags.Anisotropy |
                               MaterialFeatureFlags.Iridescence |
                               MaterialFeatureFlags.Dispersion |
                               MaterialFeatureFlags.VolumeApproximation |
                               MaterialFeatureFlags.Subsurface,
                Extensions = new MaterialExtensionDefinition
                {
                    AnisotropyStrength = 0.5f,
                    IridescenceFactor = 0.5f,
                    Dispersion = 0.5f,
                    SubsurfaceStrength = 0.5f
                }
            });

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Diagnostics, Has.Some.Contains("Anisotropy is classified"));
            Assert.That(compiled.Diagnostics, Has.Some.Contains("Iridescence is classified"));
            Assert.That(compiled.Diagnostics, Has.Some.Contains("Dispersion is classified"));
            Assert.That(compiled.Diagnostics, Has.Some.Contains("Volume thickness"));
            Assert.That(compiled.Diagnostics, Has.Some.Contains("Subsurface is a receiver-side"));
        });
    }

    [Test]
    public void Compiler_PrimitiveProfileOverridesOnlyExplicitlyValidChannels()
    {
        var primitive = new GiMaterialTransportProfile
        {
            AlgorithmVersion = 7,
            PrimitiveContentHash = 42,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling,
            Flags = GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.AlphaProfileValid,
            MeanDiffuseReflectance = new Vector3(0.1f, 0.2f, 0.3f),
            MeanEmissiveRadiance = new Vector3(99f, 99f, 99f),
            AlphaCoverage = 0.4f,
            MeanMaterialOcclusion = 0.1f,
            MeanMetallic = 0.9f,
            MeanRoughness = 0.2f
        };
        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(
            new MaterialDefinition
            {
                BaseColorFactor = Vector4.One,
                EmissiveFactor = new Vector3(0.25f, 0.5f, 1f),
                EmissiveStrength = 2f,
                OcclusionStrength = 0f
            },
            new MaterialCompilationContext
            {
                ProfileRevision = 3,
                PrimitiveProfile = primitive
            });

        Assert.Multiple(() =>
        {
            Assert.That(compiled.TransportProfile.MeanDiffuseReflectance, Is.EqualTo(new Vector3(0.1f, 0.2f, 0.3f)));
            Assert.That(compiled.TransportProfile.MeanEmissiveRadiance, Is.EqualTo(new Vector3(0.5f, 1f, 2f)));
            Assert.That(compiled.TransportProfile.AlphaCoverage, Is.EqualTo(0.4f));
            Assert.That(compiled.TransportProfile.MeanMaterialOcclusion, Is.EqualTo(1f));
            Assert.That(compiled.TransportProfile.PrimitiveContentHash, Is.EqualTo(42uL));
        });
    }

    [Test]
    public void MaterialManager_AuthoredEditPublishesScopedRevisionsAndCopyOnWrite()
    {
        using var manager = new MaterialManager();
        var definition = new MaterialDefinition
        {
            Name = "Shared",
            EmissiveFactor = new Vector3(0.25f, 0f, 0f)
        };
        MaterialHandle first = manager.RegisterMaterialDefinition(definition);
        MaterialHandle second = manager.RegisterMaterialDefinition(definition);
        MaterialHandle editable = manager.CreateEditableMaterialCopy(second);
        MaterialAspectRevisions before = manager.GetMaterialAspectRevisions(editable);
        MaterialChangedEvent changed = manager.UpdateMaterialDefinition(
            editable,
            manager.GetMaterialDefinition(editable) with { EmissiveStrength = 4f });
        MaterialAspectRevisions after = manager.GetMaterialAspectRevisions(editable);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(editable, Is.Not.EqualTo(first));
            Assert.That(changed.ChangeMask.HasFlag(MaterialChangeMask.Emission), Is.True);
            Assert.That(changed.ChangeMask.HasFlag(MaterialChangeMask.FarField), Is.True);
            Assert.That(changed.ChangeMask.HasFlag(MaterialChangeMask.DiffuseTransport), Is.False);
            Assert.That(after.Material, Is.GreaterThan(before.Material));
            Assert.That(after.Emission, Is.EqualTo(after.Material));
            Assert.That(after.FarField, Is.EqualTo(after.Material));
            Assert.That(after.DiffuseTransport, Is.EqualTo(before.DiffuseTransport));
            Assert.That(manager.GetMaterialDefinition(first).EmissiveStrength, Is.EqualTo(1f));
            Assert.That(manager.GetMaterialDefinition(editable).EmissiveStrength, Is.EqualTo(4f));
        });

        manager.ReleaseMaterial(first);
        manager.ReleaseMaterial(editable);
    }

    [Test]
    public void Compiler_InfersBlendModeUnlessAuthoredOverrideIsPresent()
    {
        CompiledMaterialTransport opaque = MaterialTransportCompiler.Compile(
            new MaterialDefinition { AlphaMode = MaterialAlphaMode.Opaque });
        CompiledMaterialTransport masked = MaterialTransportCompiler.Compile(
            new MaterialDefinition { AlphaMode = MaterialAlphaMode.Mask });
        CompiledMaterialTransport blended = MaterialTransportCompiler.Compile(
            new MaterialDefinition { AlphaMode = MaterialAlphaMode.Blend });
        CompiledMaterialTransport overridden = MaterialTransportCompiler.Compile(
            new MaterialDefinition
            {
                AlphaMode = MaterialAlphaMode.Blend,
                RenderBlendModeOverride = MaterialBlendMode.PremultipliedAlpha
            });

        Assert.Multiple(() =>
        {
            Assert.That(opaque.Definition.RenderBlendModeOverride, Is.Null);
            Assert.That(opaque.Metadata.BlendMode, Is.EqualTo(MaterialBlendMode.Opaque));
            Assert.That(masked.Metadata.BlendMode, Is.EqualTo(MaterialBlendMode.Mask));
            Assert.That(blended.Metadata.BlendMode, Is.EqualTo(MaterialBlendMode.AlphaBlend));
            Assert.That(
                overridden.Metadata.BlendMode,
                Is.EqualTo(MaterialBlendMode.PremultipliedAlpha));
        });
    }

    [Test]
    public void Compiler_PreservesAuthoredShadowReceivingPolicy()
    {
        CompiledMaterialTransport defaultPolicy = MaterialTransportCompiler.Compile(
            new MaterialDefinition());
        CompiledMaterialTransport disabledPolicy = MaterialTransportCompiler.Compile(
            new MaterialDefinition { ReceivesShadows = false });

        Assert.Multiple(() =>
        {
            Assert.That(defaultPolicy.Definition.ReceivesShadows, Is.True);
            Assert.That(
                defaultPolicy.Metadata.SurfaceFlags.HasFlag(MaterialSurfaceFlags.ReceivesShadows),
                Is.True);
            Assert.That(disabledPolicy.Definition.ReceivesShadows, Is.False);
            Assert.That(
                disabledPolicy.Metadata.SurfaceFlags.HasFlag(MaterialSurfaceFlags.ReceivesShadows),
                Is.False);
        });
    }

    [Test]
    public void Validator_PreservesCutoffAboveOneAndRejectsNegativeOrNonFiniteAuthoredData()
    {
        MaterialDefinition aboveOne = MaterialDefinitionValidator.ValidateAndNormalize(
            new MaterialDefinition { AlphaCutoff = 2f });

        Assert.Multiple(() =>
        {
            Assert.That(aboveOne.AlphaCutoff, Is.EqualTo(2f));
            Assert.That(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    new MaterialDefinition { AlphaCutoff = -0.25f }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    new MaterialDefinition { AlphaCutoff = float.NaN }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    new MaterialDefinition { AlphaCutoff = float.PositiveInfinity }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    new MaterialDefinition { MetallicFactor = float.NaN }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void V1CompatibilityAndGpuMetadata_PreserveAboveOneAndRejectInvalidAlphaCutoff()
    {
        var gpuMaterial = new GPUMaterialData
        {
            Albedo = Vector4.One,
            MetallicRoughnessAO = new Vector4(0f, 1f, 1f, 0f),
            NormalScaleBias = new Vector4(1f, 1f, 1.25f, 0f)
        };
        MaterialRenderMetadata metadata = MaterialRenderMetadata.FromGpuMaterial(gpuMaterial);
        MaterialDefinition definition = MaterialDefinitionV1Adapter.FromGpuMaterial(
            gpuMaterial,
            extension: null,
            metadata);
        GPUMaterialData negative = gpuMaterial;
        negative.NormalScaleBias = new Vector4(1f, 1f, -0.25f, 0f);
        GPUMaterialData nonFinite = gpuMaterial;
        nonFinite.NormalScaleBias = new Vector4(1f, 1f, float.NaN, 0f);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.AlphaCutoff, Is.EqualTo(1.25f));
            Assert.That(definition.AlphaCutoff, Is.EqualTo(1.25f));
            Assert.That(
                () => MaterialRenderMetadata.FromGpuMaterial(negative),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MaterialDefinitionV1Adapter.FromGpuMaterial(
                    negative,
                    extension: null,
                    new MaterialRenderMetadata()),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MaterialRenderMetadata.FromGpuMaterial(nonFinite),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    private static (float Low, float High) UnpackHalf2(uint value)
    {
        return (
            (float)BitConverter.UInt16BitsToHalf((ushort)(value & 0xffffu)),
            (float)BitConverter.UInt16BitsToHalf((ushort)(value >> 16)));
    }
}
