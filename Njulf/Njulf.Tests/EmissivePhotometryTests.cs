using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class EmissivePhotometryTests
{
    [Test]
    public void LegacySceneLinearConvention_RemainsExactlyCompatible()
    {
        var material = new MaterialDefinition
        {
            EmissiveFactor = new Vector3(0.25f, 0.5f, 1f),
            EmissiveStrength = 8f
        };
        var texture = new Vector3(0.8f, 0.4f, 0.2f);

        Vector3 legacy = GiMaterialReferenceEvaluator.EvaluateEmission(
            material.EmissiveFactor,
            texture,
            material.EmissiveStrength);
        Vector3 converted = EmissivePhotometry.EvaluateSceneLinearRadiance(material, texture);

        Assert.That(converted, Is.EqualTo(legacy));
    }

    [Test]
    public void NitsConvention_NormalizesAuthoredColorToRequestedLuminance()
    {
        var material = new MaterialDefinition
        {
            EmissiveFactor = new Vector3(0.1f, 0.45f, 1f),
            EmissiveStrength = 1_250f,
            EmissiveUnit = EmissivePhotometricUnit.LuminanceNits
        };

        Vector3 radiance = EmissivePhotometry.EvaluateSceneLinearRadiance(
            material,
            Vector3.One);

        Assert.That(
            EmissivePhotometry.SceneLinearLuminanceToNits(
                EmissivePhotometry.Luminance(radiance)),
            Is.EqualTo(1_250f).Within(0.001f));
    }

    [Test]
    public void ArtisticMultiplier_IsAppliedAfterPhotometricConversionAndReported()
    {
        var material = new MaterialDefinition
        {
            EmissiveFactor = Vector3.One,
            EmissiveStrength = 300f,
            EmissiveUnit = EmissivePhotometricUnit.LuminanceNits,
            EmissiveArtisticMultiplier = 2.5f
        };

        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(material);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.TransportProfile.AverageEmissiveLuminanceNits,
                Is.EqualTo(750f).Within(0.001f));
            Assert.That(compiled.TransportProfile.EffectiveEmissiveScale,
                Is.EqualTo(7.5f).Within(0.00001f));
            Assert.That(compiled.ExtensionData, Is.Not.Null);
            Assert.That(compiled.ExtensionData!.Value.Clearcoat.W,
                Is.EqualTo(7.5f).Within(0.00001f));
            Assert.That(compiled.Diagnostics,
                Has.Some.Contains("2.5x"));
        });
    }

    [Test]
    public void Compiler_ExposesCookedPeakLuminanceBound()
    {
        var material = new MaterialDefinition
        {
            EmissiveFactor = Vector3.One,
            EmissiveStrength = 200f,
            EmissiveUnit = EmissivePhotometricUnit.LuminanceNits,
            Emissive = new MaterialTextureBinding
            {
                Texture = new TextureHandle(7, 1)
            }
        };
        var context = new MaterialCompilationContext
        {
            ResolveTexture = static (_, _) => new MaterialTextureTransportInput(
                BindlessIndex: 12,
                MeanValid: true,
                LinearMean: new Vector4(0.25f, 0.25f, 0.25f, 1f),
                AlphaCoverageValid: true,
                AlphaCoverage: 1f,
                NormalVarianceValid: true,
                NormalVariance: 0f,
                SourceContentHash: 42,
                EmissiveLuminanceMaximumValid: true,
                EmissiveLuminanceMaximum: 4f)
        };

        GiMaterialTransportProfile profile =
            MaterialTransportCompiler.Compile(material, context).TransportProfile;

        Assert.Multiple(() =>
        {
            Assert.That(profile.AverageEmissiveLuminanceNits,
                Is.EqualTo(50f).Within(0.001f));
            Assert.That(profile.PeakEmissiveLuminanceValid, Is.True);
            Assert.That(profile.PeakEmissiveLuminanceNits,
                Is.EqualTo(800f).Within(0.001f));
        });
    }

    [Test]
    public void PhotometricMetadataChanges_InvalidateEmissionAndFarField()
    {
        MaterialDefinition before = MaterialDefinition.Default;
        MaterialChangeMask unitMask = MaterialTransportCompiler.ClassifyChanges(
            before,
            before with { EmissiveUnit = EmissivePhotometricUnit.LuminanceNits });
        MaterialChangeMask artisticMask = MaterialTransportCompiler.ClassifyChanges(
            before,
            before with { EmissiveArtisticMultiplier = 1.25f });

        Assert.Multiple(() =>
        {
            Assert.That(unitMask.HasFlag(MaterialChangeMask.Emission), Is.True);
            Assert.That(unitMask.HasFlag(MaterialChangeMask.FarField), Is.True);
            Assert.That(artisticMask.HasFlag(MaterialChangeMask.Emission), Is.True);
            Assert.That(artisticMask.HasFlag(MaterialChangeMask.FarField), Is.True);
        });
    }

    [Test]
    public void EmissiveEnergyDiagnostics_SeparatesSurfaceRadianceFromMacroPower()
    {
        GPUDdgiEmissiveSource[] sources =
        [
            Source(
                area: 2f,
                payload: new Vector3(1f, 2f, 3f),
                DdgiEmissiveSourceFlags.Triangle |
                DdgiEmissiveSourceFlags.DoubleSided),
            Source(
                area: 0f,
                payload: new Vector3(10f, 20f, 30f),
                DdgiEmissiveSourceFlags.MacroEmitter)
        ];
        var table = new DdgiEmissiveTriangleTableStats(
            CandidateCount: 2,
            SelectedCount: 1,
            TotalImportance: 10.0,
            SelectedImportance: 8.0,
            SkippedImportance: 2.0);

        DdgiEmissiveEnergyDiagnostics result =
            DdgiEmissiveEnergyDiagnostics.Calculate(sources, table);
        double macroImportance = (0.2126 * 10.0 + 0.7152 * 20.0 + 0.0722 * 30.0) /
                                 (2.0 * Math.PI);

        Assert.Multiple(() =>
        {
            Assert.That(result.SelectedMeshSourceCount, Is.EqualTo(1));
            Assert.That(result.SelectedMacroSourceCount, Is.EqualTo(1));
            Assert.That(result.AreaWeightedAverageRadiance,
                Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(result.SelectedCoveredAreaSquareMeters, Is.EqualTo(2.0));
            Assert.That(result.IntegratedPowerRed,
                Is.EqualTo(4.0 * Math.PI + 10.0).Within(1e-9));
            Assert.That(result.IntegratedPowerGreen,
                Is.EqualTo(8.0 * Math.PI + 20.0).Within(1e-9));
            Assert.That(result.IntegratedPowerBlue,
                Is.EqualTo(12.0 * Math.PI + 30.0).Within(1e-9));
            Assert.That(result.SelectedProbability,
                Is.EqualTo((8.0 + macroImportance) / (10.0 + macroImportance)).Within(1e-6));
        });
    }

    [Test]
    public void EnergyChangeEvaluator_DistinguishesMeshScaleFromRadianceChange()
    {
        var baseline = new DdgiEmissiveEnergyDiagnostics(
            4, 0, Vector3.One, 100f, 10.0,
            10.0, 10.0, 10.0, 10.0, 1f);
        DdgiEmissiveEnergyChangeWarning scale =
            DdgiEmissiveEnergyChangeEvaluator.Evaluate(
                baseline,
                baseline with
                {
                    SelectedCoveredAreaSquareMeters = 20.0,
                    IntegratedPowerRed = 20.0,
                    IntegratedPowerGreen = 20.0,
                    IntegratedPowerBlue = 20.0,
                    IntegratedPowerLuminance = 20.0
                });
        DdgiEmissiveEnergyChangeWarning radiance =
            DdgiEmissiveEnergyChangeEvaluator.Evaluate(
                baseline,
                baseline with
                {
                    AreaWeightedAverageRadiance = Vector3.One * 2f,
                    IntegratedPowerRed = 20.0,
                    IntegratedPowerGreen = 20.0,
                    IntegratedPowerBlue = 20.0,
                    IntegratedPowerLuminance = 20.0
                });

        Assert.Multiple(() =>
        {
            Assert.That(scale.Kind, Is.EqualTo(DdgiEmissiveEnergyChangeKind.MeshScale));
            Assert.That(scale.Message, Does.Contain("mesh scale"));
            Assert.That(radiance.Kind,
                Is.EqualTo(DdgiEmissiveEnergyChangeKind.RadianceOrTexture));
            Assert.That(radiance.Message, Does.Contain("photometric units"));
        });
    }

    private static GPUDdgiEmissiveSource Source(
        float area,
        Vector3 payload,
        DdgiEmissiveSourceFlags flags)
    {
        uint packedFlags = (uint)flags << DdgiEmissiveTriangleTable.FlagsShift;
        return new GPUDdgiEmissiveSource
        {
            Vertex0Area = new Vector4(0f, 0f, 0f, area),
            Edge2AliasFlags = new Vector4(
                0f,
                0f,
                0f,
                BitConverter.UInt32BitsToSingle(packedFlags)),
            RadianceSelectionProbability = new Vector4(payload, 1f)
        };
    }
}
