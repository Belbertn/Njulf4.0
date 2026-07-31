using System.Reflection;
using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialChangeClassificationTests
{
    private const MaterialChangeMask RasterOnly =
        MaterialChangeMask.RasterAppearance;

    private const MaterialChangeMask DiffuseChange =
        RasterOnly |
        MaterialChangeMask.DiffuseTransport |
        MaterialChangeMask.FarField;

    private const MaterialChangeMask EmissionChange =
        RasterOnly |
        MaterialChangeMask.Emission |
        MaterialChangeMask.FarField;

    private const MaterialChangeMask CoverageChange =
        RasterOnly |
        MaterialChangeMask.AlphaCoverage |
        MaterialChangeMask.AccelerationStructure |
        MaterialChangeMask.FarField;

    private const MaterialChangeMask BaseColorBindingChange =
        DiffuseChange |
        MaterialChangeMask.AlphaCoverage |
        MaterialChangeMask.AccelerationStructure;

    private const MaterialChangeMask SidednessChange =
        RasterOnly |
        MaterialChangeMask.Sidedness |
        MaterialChangeMask.AccelerationStructure |
        MaterialChangeMask.FarField;

    private const MaterialChangeMask ShadingModelChange =
        RasterOnly |
        MaterialChangeMask.DiffuseTransport |
        MaterialChangeMask.Emission |
        MaterialChangeMask.ShadingModel |
        MaterialChangeMask.AccelerationStructure |
        MaterialChangeMask.FarField;

    private const MaterialChangeMask TransmissionClassificationChange =
        DiffuseChange |
        MaterialChangeMask.AlphaCoverage |
        MaterialChangeMask.ShadingModel |
        MaterialChangeMask.AccelerationStructure;

    private const MaterialChangeMask DecalClassificationChange =
        CoverageChange |
        MaterialChangeMask.ShadingModel;

    [Test]
    public void AuthoredDefinitionProperties_HaveCompleteExactChangeAndRevisionCoverage()
    {
        MaterialChangeCase[] cases = CreateDefinitionCases().ToArray();
        AssertWritablePropertiesCovered<MaterialDefinition>(
            cases.Select(change => change.PropertyName),
            nameof(MaterialDefinition));

        foreach (MaterialChangeCase change in cases)
            AssertClassificationAndAspectRevisions(change);
    }

    [Test]
    public void AuthoredExtensionProperties_HaveCompleteExactChangeAndRevisionCoverage()
    {
        MaterialChangeCase[] cases = CreateExtensionCases().ToArray();
        AssertWritablePropertiesCovered<MaterialExtensionDefinition>(
            cases.Select(change => change.PropertyName),
            nameof(MaterialExtensionDefinition));

        foreach (MaterialChangeCase change in cases)
            AssertClassificationAndAspectRevisions(change);
    }

    [Test]
    public void EveryFeatureFlag_HasAnExplicitExactChangeAndRevisionClassification()
    {
        MaterialFeatureFlags[] flags = Enum.GetValues<MaterialFeatureFlags>()
            .Where(IsSingleBit)
            .ToArray();
        uint declaredBits = Enum.GetValues<MaterialFeatureFlags>()
            .Aggregate(0u, (bits, value) => bits | (uint)value);
        uint coveredBits = flags.Aggregate(0u, (bits, value) => bits | (uint)value);
        Assert.That(coveredBits, Is.EqualTo(declaredBits), "Every declared feature bit must be exercised.");

        foreach (MaterialFeatureFlags flag in flags)
        {
            var before = new MaterialDefinition();
            var after = before with { FeatureFlags = flag };
            AssertClassificationAndAspectRevisions(new MaterialChangeCase(
                $"FeatureFlags.{flag}",
                nameof(MaterialDefinition.FeatureFlags),
                before,
                after,
                ExpectedFeatureFlagChange(flag)));
        }
    }

    [Test]
    public void EveryTextureBindingComponent_HasExactRoleSpecificChangeAndRevisionClassification()
    {
        BindingComponentMutation[] components = CreateBindingComponentMutations().ToArray();
        AssertWritablePropertiesCovered<MaterialTextureBinding>(
            components.Select(component => component.PropertyName),
            nameof(MaterialTextureBinding));

        foreach (BindingSlot slot in CreateBindingSlots())
        {
            foreach (BindingComponentMutation component in components)
            {
                MaterialTextureBinding original = CreateBinding(31);
                MaterialDefinition before = slot.Assign(
                    new MaterialDefinition { FeatureFlags = slot.ActivationFlags },
                    original);
                MaterialDefinition after = slot.Assign(before, component.Apply(original));
                AssertClassificationAndAspectRevisions(new MaterialChangeCase(
                    $"{slot.Path}.{component.PropertyName}",
                    component.PropertyName,
                    before,
                    after,
                    slot.ExpectedChange | MaterialChangeMask.TextureDependencies));
            }
        }
    }

    [Test]
    public void BaseColorFactor_SeparatesRgbTransportFromAlphaVisibility()
    {
        var before = new MaterialDefinition { BaseColorFactor = Vector4.One };
        MaterialDefinition rgb = before with
        {
            BaseColorFactor = new Vector4(0.5f, 0.75f, 0.25f, 1f)
        };
        MaterialDefinition alpha = before with
        {
            BaseColorFactor = new Vector4(1f, 1f, 1f, 0.25f)
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(before, rgb),
                Is.EqualTo(DiffuseChange));
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(before, alpha),
                Is.EqualTo(CoverageChange));
        });
    }

    [Test]
    public void ExtensionFactors_DoNotMasqueradeAsTextureDependencyChanges()
    {
        var before = new MaterialDefinition
        {
            FeatureFlags = MaterialFeatureFlags.Clearcoat,
            Extensions = new MaterialExtensionDefinition { ClearcoatFactor = 0.25f }
        };
        MaterialDefinition factorOnly = before with
        {
            Extensions = before.Extensions with { ClearcoatFactor = 0.75f }
        };
        MaterialDefinition binding = before with
        {
            Extensions = before.Extensions with { Clearcoat = CreateBinding(9) }
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(before, factorOnly),
                Is.EqualTo(DiffuseChange));
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(before, binding),
                Is.EqualTo(DiffuseChange | MaterialChangeMask.TextureDependencies));
        });
    }

    [Test]
    public void GiParticipationOverrides_AdvanceOnlyTheirOwnedTransportAspect()
    {
        var before = new MaterialDefinition();
        MaterialDefinition diffuse = before with
        {
            DiffuseGiParticipation = GiParticipationOverride.Disabled
        };
        MaterialDefinition emission = before with
        {
            EmissionGiParticipation = GiParticipationOverride.Disabled
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(before, diffuse),
                Is.EqualTo(DiffuseChange));
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(before, emission),
                Is.EqualTo(EmissionChange));
        });
    }

    [Test]
    public void DirectionalOnlyExtensions_RemainRasterOnly()
    {
        var before = new MaterialDefinition
        {
            FeatureFlags = MaterialFeatureFlags.Anisotropy |
                           MaterialFeatureFlags.Iridescence |
                           MaterialFeatureFlags.Dispersion |
                           MaterialFeatureFlags.VolumeApproximation |
                           MaterialFeatureFlags.Subsurface
        };
        MaterialDefinition after = before with
        {
            Extensions = before.Extensions with
            {
                AnisotropyStrength = 0.5f,
                IridescenceFactor = 0.5f,
                Dispersion = 0.5f,
                ThicknessFactor = 0.5f,
                SubsurfaceStrength = 0.5f
            }
        };

        Assert.That(
            MaterialTransportCompiler.ClassifyChanges(before, after),
            Is.EqualTo(RasterOnly));
    }

    private static IEnumerable<MaterialChangeCase> CreateDefinitionCases()
    {
        yield return Change(
            nameof(MaterialDefinition.Name),
            definition => definition with { Name = "Renamed" },
            RasterOnly);
        yield return Change(
            nameof(MaterialDefinition.BaseColorFactor),
            definition => definition with
            {
                BaseColorFactor = new Vector4(0.5f, 0.75f, 0.25f, 1f)
            },
            DiffuseChange);
        yield return Change(
            nameof(MaterialDefinition.EmissiveFactor),
            definition => definition with { EmissiveFactor = new Vector3(0.25f, 0.5f, 1f) },
            EmissionChange);
        yield return Change(
            nameof(MaterialDefinition.EmissiveStrength),
            definition => definition with { EmissiveStrength = 2f },
            EmissionChange);
        yield return Change(
            nameof(MaterialDefinition.MetallicFactor),
            definition => definition with { MetallicFactor = 0.5f },
            DiffuseChange);
        yield return Change(
            nameof(MaterialDefinition.RoughnessFactor),
            definition => definition with { RoughnessFactor = 0.5f },
            DiffuseChange);
        yield return Change(
            nameof(MaterialDefinition.OcclusionStrength),
            definition => definition with { OcclusionStrength = 0.5f },
            DiffuseChange);
        yield return Change(
            nameof(MaterialDefinition.NormalScale),
            definition => definition with { NormalScale = 0.5f },
            DiffuseChange);
        yield return BindingDefinitionChange(
            nameof(MaterialDefinition.BaseColor),
            (definition, binding) => definition with { BaseColor = binding },
            BaseColorBindingChange);
        yield return BindingDefinitionChange(
            nameof(MaterialDefinition.Normal),
            (definition, binding) => definition with { Normal = binding },
            DiffuseChange);
        yield return BindingDefinitionChange(
            nameof(MaterialDefinition.MetallicRoughness),
            (definition, binding) => definition with { MetallicRoughness = binding },
            DiffuseChange);
        yield return BindingDefinitionChange(
            nameof(MaterialDefinition.Occlusion),
            (definition, binding) => definition with { Occlusion = binding },
            DiffuseChange);
        yield return BindingDefinitionChange(
            nameof(MaterialDefinition.Emissive),
            (definition, binding) => definition with { Emissive = binding },
            EmissionChange);
        yield return Change(
            nameof(MaterialDefinition.AlphaMode),
            definition => definition with { AlphaMode = MaterialAlphaMode.Mask },
            CoverageChange);
        yield return Change(
            nameof(MaterialDefinition.AlphaCutoff),
            definition => definition with { AlphaCutoff = 0.25f },
            CoverageChange);
        yield return Change(
            nameof(MaterialDefinition.DoubleSided),
            definition => definition with { DoubleSided = true },
            SidednessChange);
        yield return Change(
            nameof(MaterialDefinition.ReceivesShadows),
            definition => definition with { ReceivesShadows = false },
            RasterOnly);
        yield return Change(
            nameof(MaterialDefinition.RenderBlendModeOverride),
            definition => definition with
            {
                RenderBlendModeOverride = MaterialBlendMode.PremultipliedAlpha
            },
            CoverageChange);
        yield return Change(
            nameof(MaterialDefinition.ShadingModel),
            definition => definition with { ShadingModel = MaterialShadingModel.Unlit },
            ShadingModelChange);
        yield return Change(
            nameof(MaterialDefinition.FeatureFlags),
            definition => definition with { FeatureFlags = MaterialFeatureFlags.Clearcoat },
            DiffuseChange);

        var extensionBefore = new MaterialDefinition
        {
            FeatureFlags = MaterialFeatureFlags.Clearcoat
        };
        yield return new MaterialChangeCase(
            nameof(MaterialDefinition.Extensions),
            nameof(MaterialDefinition.Extensions),
            extensionBefore,
            extensionBefore with
            {
                Extensions = extensionBefore.Extensions with { ClearcoatFactor = 0.5f }
            },
            DiffuseChange);

        yield return Change(
            nameof(MaterialDefinition.DiffuseGiParticipation),
            definition => definition with
            {
                DiffuseGiParticipation = GiParticipationOverride.Disabled
            },
            DiffuseChange);
        yield return Change(
            nameof(MaterialDefinition.EmissionGiParticipation),
            definition => definition with
            {
                EmissionGiParticipation = GiParticipationOverride.Disabled
            },
            EmissionChange);
        yield return Change(
            nameof(MaterialDefinition.IsGeometryDecal),
            definition => definition with { IsGeometryDecal = true },
            DecalClassificationChange);
        yield return Change(
            nameof(MaterialDefinition.DecalLayer),
            definition => definition with { DecalLayer = 1 },
            RasterOnly);
        yield return Change(
            nameof(MaterialDefinition.DecalDepthBias),
            definition => definition with { DecalDepthBias = 0.001f },
            RasterOnly);
    }

    private static IEnumerable<MaterialChangeCase> CreateExtensionCases()
    {
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.ClearcoatFactor),
            MaterialFeatureFlags.Clearcoat,
            extension => extension with { ClearcoatFactor = 0.5f },
            DiffuseChange);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.ClearcoatRoughness),
            MaterialFeatureFlags.Clearcoat,
            extension => extension with { ClearcoatRoughness = 0.5f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.ClearcoatNormalScale),
            MaterialFeatureFlags.Clearcoat,
            extension => extension with { ClearcoatNormalScale = 0.5f },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Clearcoat),
            MaterialFeatureFlags.Clearcoat | MaterialFeatureFlags.ClearcoatTexture,
            (extension, binding) => extension with { Clearcoat = binding },
            DiffuseChange);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.ClearcoatRoughnessTexture),
            MaterialFeatureFlags.Clearcoat | MaterialFeatureFlags.ClearcoatRoughnessTexture,
            (extension, binding) => extension with { ClearcoatRoughnessTexture = binding },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.ClearcoatNormal),
            MaterialFeatureFlags.Clearcoat | MaterialFeatureFlags.ClearcoatNormalTexture,
            (extension, binding) => extension with { ClearcoatNormal = binding },
            RasterOnly);

        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.SheenColorFactor),
            MaterialFeatureFlags.Sheen,
            extension => extension with { SheenColorFactor = new Vector3(0.25f, 0.5f, 0.75f) },
            DiffuseChange);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.SheenRoughness),
            MaterialFeatureFlags.Sheen,
            extension => extension with { SheenRoughness = 0.5f },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.SheenColor),
            MaterialFeatureFlags.Sheen | MaterialFeatureFlags.SheenColorTexture,
            (extension, binding) => extension with { SheenColor = binding },
            DiffuseChange);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.SheenRoughnessTexture),
            MaterialFeatureFlags.Sheen | MaterialFeatureFlags.SheenRoughnessTexture,
            (extension, binding) => extension with { SheenRoughnessTexture = binding },
            RasterOnly);

        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.AnisotropyStrength),
            MaterialFeatureFlags.Anisotropy,
            extension => extension with { AnisotropyStrength = 0.5f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.AnisotropyRotation),
            MaterialFeatureFlags.Anisotropy,
            extension => extension with { AnisotropyRotation = 0.5f },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Anisotropy),
            MaterialFeatureFlags.Anisotropy | MaterialFeatureFlags.AnisotropyTexture,
            (extension, binding) => extension with { Anisotropy = binding },
            RasterOnly);

        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.TransmissionFactor),
            MaterialFeatureFlags.Transmission,
            extension => extension with { TransmissionFactor = 0.5f },
            DiffuseChange);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.Ior),
            MaterialFeatureFlags.Ior,
            extension => extension with { Ior = 1.7f },
            DiffuseChange);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.ThicknessFactor),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.VolumeApproximation,
            extension => extension with { ThicknessFactor = 0.5f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.AttenuationDistance),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.VolumeApproximation,
            extension => extension with { AttenuationDistance = 2f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.AttenuationColor),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.VolumeApproximation,
            extension => extension with { AttenuationColor = new Vector3(0.5f, 0.75f, 1f) },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Transmission),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.TransmissionTexture,
            (extension, binding) => extension with { Transmission = binding },
            DiffuseChange);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Thickness),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.VolumeApproximation,
            (extension, binding) => extension with { Thickness = binding },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.TransmissionPolicy),
            MaterialFeatureFlags.Transmission,
            extension => extension with { TransmissionPolicy = GiTransmissionPolicy.ThinSurface },
            DiffuseChange | MaterialChangeMask.AccelerationStructure);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.ThinTransmissionTint),
            MaterialFeatureFlags.Transmission,
            extension => extension with { ThinTransmissionTint = new Vector3(0.5f, 0.75f, 1f) },
            DiffuseChange);

        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.SpecularFactor),
            MaterialFeatureFlags.Specular,
            extension => extension with { SpecularFactor = 0.5f },
            DiffuseChange);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.SpecularColorFactor),
            MaterialFeatureFlags.Specular,
            extension => extension with
            {
                SpecularColorFactor = new Vector3(0.5f, 0.75f, 1f)
            },
            DiffuseChange);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Specular),
            MaterialFeatureFlags.Specular | MaterialFeatureFlags.SpecularTexture,
            (extension, binding) => extension with { Specular = binding },
            DiffuseChange);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.SpecularColor),
            MaterialFeatureFlags.Specular | MaterialFeatureFlags.SpecularColorTexture,
            (extension, binding) => extension with { SpecularColor = binding },
            DiffuseChange);

        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.IridescenceFactor),
            MaterialFeatureFlags.Iridescence,
            extension => extension with { IridescenceFactor = 0.5f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.IridescenceIor),
            MaterialFeatureFlags.Iridescence,
            extension => extension with { IridescenceIor = 1.5f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.IridescenceThicknessMinimum),
            MaterialFeatureFlags.Iridescence,
            extension => extension with { IridescenceThicknessMinimum = 200f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.IridescenceThicknessMaximum),
            MaterialFeatureFlags.Iridescence,
            extension => extension with { IridescenceThicknessMaximum = 500f },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Iridescence),
            MaterialFeatureFlags.Iridescence | MaterialFeatureFlags.IridescenceTexture,
            (extension, binding) => extension with { Iridescence = binding },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.IridescenceThickness),
            MaterialFeatureFlags.Iridescence |
            MaterialFeatureFlags.IridescenceThicknessTexture,
            (extension, binding) => extension with { IridescenceThickness = binding },
            RasterOnly);

        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.Dispersion),
            MaterialFeatureFlags.Dispersion,
            extension => extension with { Dispersion = 0.5f },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.SubsurfaceColor),
            MaterialFeatureFlags.Subsurface,
            extension => extension with { SubsurfaceColor = new Vector3(0.5f, 0.75f, 1f) },
            RasterOnly);
        yield return ExtensionChange(
            nameof(MaterialExtensionDefinition.SubsurfaceStrength),
            MaterialFeatureFlags.Subsurface,
            extension => extension with { SubsurfaceStrength = 0.5f },
            RasterOnly);
        yield return ExtensionBindingChange(
            nameof(MaterialExtensionDefinition.Subsurface),
            MaterialFeatureFlags.Subsurface | MaterialFeatureFlags.SubsurfaceTexture,
            (extension, binding) => extension with { Subsurface = binding },
            RasterOnly);
    }

    private static IEnumerable<BindingSlot> CreateBindingSlots()
    {
        yield return new BindingSlot(
            nameof(MaterialDefinition.BaseColor),
            MaterialFeatureFlags.None,
            (definition, binding) => definition with { BaseColor = binding },
            BaseColorBindingChange);
        yield return new BindingSlot(
            nameof(MaterialDefinition.Normal),
            MaterialFeatureFlags.None,
            (definition, binding) => definition with { Normal = binding },
            DiffuseChange);
        yield return new BindingSlot(
            nameof(MaterialDefinition.MetallicRoughness),
            MaterialFeatureFlags.None,
            (definition, binding) => definition with { MetallicRoughness = binding },
            DiffuseChange);
        yield return new BindingSlot(
            nameof(MaterialDefinition.Occlusion),
            MaterialFeatureFlags.None,
            (definition, binding) => definition with { Occlusion = binding },
            DiffuseChange);
        yield return new BindingSlot(
            nameof(MaterialDefinition.Emissive),
            MaterialFeatureFlags.None,
            (definition, binding) => definition with { Emissive = binding },
            EmissionChange);

        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Clearcoat),
            MaterialFeatureFlags.Clearcoat | MaterialFeatureFlags.ClearcoatTexture,
            (extension, binding) => extension with { Clearcoat = binding },
            DiffuseChange);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.ClearcoatRoughnessTexture),
            MaterialFeatureFlags.Clearcoat | MaterialFeatureFlags.ClearcoatRoughnessTexture,
            (extension, binding) => extension with { ClearcoatRoughnessTexture = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.ClearcoatNormal),
            MaterialFeatureFlags.Clearcoat | MaterialFeatureFlags.ClearcoatNormalTexture,
            (extension, binding) => extension with { ClearcoatNormal = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.SheenColor),
            MaterialFeatureFlags.Sheen | MaterialFeatureFlags.SheenColorTexture,
            (extension, binding) => extension with { SheenColor = binding },
            DiffuseChange);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.SheenRoughnessTexture),
            MaterialFeatureFlags.Sheen | MaterialFeatureFlags.SheenRoughnessTexture,
            (extension, binding) => extension with { SheenRoughnessTexture = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Anisotropy),
            MaterialFeatureFlags.Anisotropy | MaterialFeatureFlags.AnisotropyTexture,
            (extension, binding) => extension with { Anisotropy = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Transmission),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.TransmissionTexture,
            (extension, binding) => extension with { Transmission = binding },
            DiffuseChange);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Thickness),
            MaterialFeatureFlags.Transmission | MaterialFeatureFlags.VolumeApproximation,
            (extension, binding) => extension with { Thickness = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Specular),
            MaterialFeatureFlags.Specular | MaterialFeatureFlags.SpecularTexture,
            (extension, binding) => extension with { Specular = binding },
            DiffuseChange);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.SpecularColor),
            MaterialFeatureFlags.Specular | MaterialFeatureFlags.SpecularColorTexture,
            (extension, binding) => extension with { SpecularColor = binding },
            DiffuseChange);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Iridescence),
            MaterialFeatureFlags.Iridescence | MaterialFeatureFlags.IridescenceTexture,
            (extension, binding) => extension with { Iridescence = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.IridescenceThickness),
            MaterialFeatureFlags.Iridescence |
            MaterialFeatureFlags.IridescenceThicknessTexture,
            (extension, binding) => extension with { IridescenceThickness = binding },
            RasterOnly);
        yield return ExtensionBindingSlot(
            nameof(MaterialExtensionDefinition.Subsurface),
            MaterialFeatureFlags.Subsurface | MaterialFeatureFlags.SubsurfaceTexture,
            (extension, binding) => extension with { Subsurface = binding },
            RasterOnly);
    }

    private static IEnumerable<BindingComponentMutation> CreateBindingComponentMutations()
    {
        yield return new BindingComponentMutation(
            nameof(MaterialTextureBinding.Texture),
            binding => binding with { Texture = new TextureHandle(32, 1) });
        yield return new BindingComponentMutation(
            nameof(MaterialTextureBinding.Sampler),
            binding => binding with
            {
                Sampler = binding.Sampler with { WrapU = TextureWrapMode.ClampToEdge }
            });
        yield return new BindingComponentMutation(
            nameof(MaterialTextureBinding.TexCoordSet),
            binding => binding with { TexCoordSet = 1 });
        yield return new BindingComponentMutation(
            nameof(MaterialTextureBinding.Offset),
            binding => binding with { Offset = new Vector2(0.25f, 0.5f) });
        yield return new BindingComponentMutation(
            nameof(MaterialTextureBinding.Scale),
            binding => binding with { Scale = new Vector2(0.5f, 0.75f) });
        yield return new BindingComponentMutation(
            nameof(MaterialTextureBinding.RotationRadians),
            binding => binding with { RotationRadians = 0.25f });
    }

    private static MaterialChangeMask ExpectedFeatureFlagChange(MaterialFeatureFlags flag)
    {
        const MaterialFeatureFlags diffuseEnergy =
            MaterialFeatureFlags.Clearcoat |
            MaterialFeatureFlags.ClearcoatTexture |
            MaterialFeatureFlags.Sheen |
            MaterialFeatureFlags.SheenColorTexture |
            MaterialFeatureFlags.TransmissionTexture |
            MaterialFeatureFlags.Specular |
            MaterialFeatureFlags.SpecularTexture |
            MaterialFeatureFlags.SpecularColorTexture |
            MaterialFeatureFlags.CompressedNormalBc5 |
            MaterialFeatureFlags.Ior;

        if (flag is MaterialFeatureFlags.Transmission or MaterialFeatureFlags.Foliage)
            return TransmissionClassificationChange;
        if (flag == MaterialFeatureFlags.EmissiveStrength)
            return EmissionChange;
        if ((flag & diffuseEnergy) != MaterialFeatureFlags.None)
            return DiffuseChange;
        return RasterOnly;
    }

    private static void AssertClassificationAndAspectRevisions(MaterialChangeCase change)
    {
        MaterialChangeMask classified = MaterialTransportCompiler.ClassifyChanges(
            change.Before,
            change.After);
        Assert.That(classified, Is.EqualTo(change.ExpectedChange), change.Name);

        using var manager = new MaterialManager();
        MaterialCompilationContext context = CreateCompilationContext();
        MaterialDefinition managerBefore = change.Before with
        {
            Name = $"Classification test: {change.Before.Name}"
        };
        MaterialDefinition managerAfter = change.After with
        {
            Name = $"Classification test: {change.After.Name}"
        };
        MaterialHandle handle = manager.RegisterMaterialDefinition(managerBefore, context);
        MaterialAspectRevisions before = manager.GetMaterialAspectRevisions(handle);
        MaterialChangedEvent changed = manager.UpdateMaterialDefinition(handle, managerAfter, context);
        MaterialAspectRevisions after = manager.GetMaterialAspectRevisions(handle);

        Assert.Multiple(() =>
        {
            Assert.That(changed.ChangeMask, Is.EqualTo(change.ExpectedChange), change.Name);
            Assert.That(after.Material, Is.GreaterThan(before.Material), change.Name);
            AssertAspectRevision(
                change,
                nameof(MaterialAspectRevisions.DiffuseTransport),
                change.ExpectedChange.HasFlag(MaterialChangeMask.DiffuseTransport),
                before.DiffuseTransport,
                after.DiffuseTransport,
                after.Material);
            AssertAspectRevision(
                change,
                nameof(MaterialAspectRevisions.Emission),
                change.ExpectedChange.HasFlag(MaterialChangeMask.Emission),
                before.Emission,
                after.Emission,
                after.Material);
            AssertAspectRevision(
                change,
                nameof(MaterialAspectRevisions.AlphaCoverage),
                change.ExpectedChange.HasFlag(MaterialChangeMask.AlphaCoverage),
                before.AlphaCoverage,
                after.AlphaCoverage,
                after.Material);
            AssertAspectRevision(
                change,
                nameof(MaterialAspectRevisions.Sidedness),
                change.ExpectedChange.HasFlag(MaterialChangeMask.Sidedness),
                before.Sidedness,
                after.Sidedness,
                after.Material);
            AssertAspectRevision(
                change,
                nameof(MaterialAspectRevisions.ShadingModel),
                change.ExpectedChange.HasFlag(MaterialChangeMask.ShadingModel),
                before.ShadingModel,
                after.ShadingModel,
                after.Material);
            AssertAspectRevision(
                change,
                nameof(MaterialAspectRevisions.FarField),
                change.ExpectedChange.HasFlag(MaterialChangeMask.FarField),
                before.FarField,
                after.FarField,
                after.Material);
        });
    }

    private static void AssertAspectRevision(
        MaterialChangeCase change,
        string aspect,
        bool affected,
        uint before,
        uint after,
        uint materialRevision)
    {
        Assert.That(
            after,
            Is.EqualTo(affected ? materialRevision : before),
            $"{change.Name}: {aspect}");
    }

    private static void AssertWritablePropertiesCovered<T>(
        IEnumerable<string> coveredPropertyNames,
        string contractName)
    {
        string[] expected = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] actual = coveredPropertyNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(
            actual,
            Is.EqualTo(expected),
            $"{contractName} gained or lost an authored property without updating its change-classification matrix.");
    }

    private static MaterialCompilationContext CreateCompilationContext() => new()
    {
        ResolveTexture = (binding, _) => new MaterialTextureTransportInput(
            BindlessIndex: binding.Texture.Index + 8,
            MeanValid: true,
            LinearMean: Vector4.One,
            AlphaCoverageValid: true,
            AlphaCoverage: 1f,
            NormalVarianceValid: true,
            NormalVariance: 0f,
            SourceContentHash: (ulong)binding.Texture.Index)
    };

    private static MaterialTextureBinding CreateBinding(int textureIndex) => new()
    {
        Texture = new TextureHandle(textureIndex, 1)
    };

    private static bool IsSingleBit(MaterialFeatureFlags value)
    {
        uint bits = (uint)value;
        return bits != 0 && (bits & (bits - 1)) == 0;
    }

    private static MaterialChangeCase Change(
        string propertyName,
        Func<MaterialDefinition, MaterialDefinition> apply,
        MaterialChangeMask expectedChange)
    {
        var before = new MaterialDefinition();
        return new MaterialChangeCase(
            propertyName,
            propertyName,
            before,
            apply(before),
            expectedChange);
    }

    private static MaterialChangeCase BindingDefinitionChange(
        string propertyName,
        Func<MaterialDefinition, MaterialTextureBinding, MaterialDefinition> assign,
        MaterialChangeMask expectedChange)
    {
        var before = new MaterialDefinition();
        return new MaterialChangeCase(
            propertyName,
            propertyName,
            before,
            assign(before, CreateBinding(17)),
            expectedChange | MaterialChangeMask.TextureDependencies);
    }

    private static MaterialChangeCase ExtensionChange(
        string propertyName,
        MaterialFeatureFlags activationFlags,
        Func<MaterialExtensionDefinition, MaterialExtensionDefinition> apply,
        MaterialChangeMask expectedChange)
    {
        var before = new MaterialDefinition { FeatureFlags = activationFlags };
        return new MaterialChangeCase(
            $"Extensions.{propertyName}",
            propertyName,
            before,
            before with { Extensions = apply(before.Extensions) },
            expectedChange);
    }

    private static MaterialChangeCase ExtensionBindingChange(
        string propertyName,
        MaterialFeatureFlags activationFlags,
        Func<MaterialExtensionDefinition, MaterialTextureBinding, MaterialExtensionDefinition> assign,
        MaterialChangeMask expectedChange)
    {
        return ExtensionChange(
            propertyName,
            activationFlags,
            extension => assign(extension, CreateBinding(23)),
            expectedChange | MaterialChangeMask.TextureDependencies);
    }

    private static BindingSlot ExtensionBindingSlot(
        string propertyName,
        MaterialFeatureFlags activationFlags,
        Func<MaterialExtensionDefinition, MaterialTextureBinding, MaterialExtensionDefinition> assign,
        MaterialChangeMask expectedChange)
    {
        return new BindingSlot(
            $"Extensions.{propertyName}",
            activationFlags,
            (definition, binding) => definition with
            {
                Extensions = assign(definition.Extensions, binding)
            },
            expectedChange);
    }

    private sealed record MaterialChangeCase(
        string Name,
        string PropertyName,
        MaterialDefinition Before,
        MaterialDefinition After,
        MaterialChangeMask ExpectedChange);

    private sealed record BindingSlot(
        string Path,
        MaterialFeatureFlags ActivationFlags,
        Func<MaterialDefinition, MaterialTextureBinding, MaterialDefinition> Assign,
        MaterialChangeMask ExpectedChange);

    private sealed record BindingComponentMutation(
        string PropertyName,
        Func<MaterialTextureBinding, MaterialTextureBinding> Apply);
}
