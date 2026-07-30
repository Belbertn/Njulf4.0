using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiCookedEmissiveTransportTests
{
    [Test]
    public void EvaluateCoveredRadiance_UsesCurrentFactorAndStrengthWithCookedCorrelation()
    {
        var record = new GiPrimitiveEmissiveTriangleRecord
        {
            TriangleIndex = 0,
            LocalSurfaceArea = 0.5,
            Coverage = 0.25,
            CoveredMeanEmissiveTexture = new TextureTransportVector4(0.5, 1.0, 0.25, 1.0),
            CookedImportance = 0.5 * 0.25 *
                (0.2126 * 0.5 + 0.7152 + 0.0722 * 0.25)
        };
        var material = new MaterialDefinition
        {
            EmissiveFactor = new Vector3(0.5f, 0.25f, 1.0f),
            EmissiveStrength = 4.0f
        };

        Vector3 radiance =
            DdgiCookedEmissiveTransport.EvaluateCoveredRadiance(record, material);

        Assert.Multiple(() =>
        {
            Assert.That(radiance.X, Is.EqualTo(0.25f).Within(1e-7f));
            Assert.That(radiance.Y, Is.EqualTo(0.25f).Within(1e-7f));
            Assert.That(radiance.Z, Is.EqualTo(0.25f).Within(1e-7f));
        });
    }

    [Test]
    public void Compatibility_AllowsFactorOnlyEditsButRejectsAlphaAndTextureRevisionChanges()
    {
        GiPrimitiveTransportProfile profile = CreateProfile(
            new ModelMaterial { Emissive = Vector4.One });
        var runtime = new GiMaterialTransportProfile
        {
            PrimitiveContentHash = profile.InputHash,
            SourceContentHash =
                DdgiCookedEmissiveTransport.CombineTextureSourceHashes(
                    profile.TextureSourceHashes)
        };
        var factorEdit = new MaterialDefinition
        {
            BaseColorFactor = Vector4.One,
            EmissiveFactor = new Vector3(0.25f, 0.5f, 0.75f),
            EmissiveStrength = 16.0f
        };

        bool accepted = DdgiCookedEmissiveTransport.TryValidateCompatibility(
            profile,
            factorEdit,
            runtime,
            out DdgiCookedEmissiveProfileRejection acceptedReason);
        bool alphaAccepted = DdgiCookedEmissiveTransport.TryValidateCompatibility(
            profile,
            factorEdit with
            {
                BaseColorFactor = new Vector4(1.0f, 1.0f, 1.0f, 0.5f)
            },
            runtime,
            out DdgiCookedEmissiveProfileRejection alphaReason);
        bool textureAccepted = DdgiCookedEmissiveTransport.TryValidateCompatibility(
            profile,
            factorEdit,
            runtime with { SourceContentHash = runtime.SourceContentHash + 1 },
            out DdgiCookedEmissiveProfileRejection textureReason);
        bool revisionAccepted = DdgiCookedEmissiveTransport.TryValidateCompatibility(
            profile with
            {
                AlgorithmVersion = GiPrimitiveTransportProfile.CurrentAlgorithmVersion - 1
            },
            factorEdit,
            runtime,
            out DdgiCookedEmissiveProfileRejection revisionReason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(acceptedReason, Is.EqualTo(DdgiCookedEmissiveProfileRejection.None));
            Assert.That(alphaAccepted, Is.False);
            Assert.That(alphaReason, Is.EqualTo(DdgiCookedEmissiveProfileRejection.StaleAlphaContract));
            Assert.That(textureAccepted, Is.False);
            Assert.That(textureReason, Is.EqualTo(DdgiCookedEmissiveProfileRejection.StaleTextureContent));
            Assert.That(revisionAccepted, Is.False);
            Assert.That(revisionReason, Is.EqualTo(DdgiCookedEmissiveProfileRejection.UnsupportedVersion));
        });
    }

    [Test]
    public void Compatibility_RejectsCookedUnlitProfileWhenRuntimeOverrideEnablesEmission()
    {
        GiPrimitiveTransportProfile profile = CreateProfile(
            new ModelMaterial
            {
                Emissive = Vector4.One,
                Unlit = true
            });
        var runtime = new GiMaterialTransportProfile
        {
            PrimitiveContentHash = profile.InputHash,
            SourceContentHash =
                DdgiCookedEmissiveTransport.CombineTextureSourceHashes(
                    profile.TextureSourceHashes)
        };
        var enabledUnlit = new MaterialDefinition
        {
            BaseColorFactor = Vector4.One,
            EmissiveFactor = Vector3.One,
            ShadingModel = MaterialShadingModel.Unlit,
            EmissionGiParticipation = GiParticipationOverride.Enabled
        };

        bool accepted = DdgiCookedEmissiveTransport.TryValidateCompatibility(
            profile,
            enabledUnlit,
            runtime,
            out DdgiCookedEmissiveProfileRejection rejection);

        Assert.Multiple(() =>
        {
            Assert.That(profile.CookedEmissionEligible, Is.False);
            Assert.That(profile.EmissiveCandidateTriangleCount, Is.Zero);
            Assert.That(accepted, Is.False);
            Assert.That(
                rejection,
                Is.EqualTo(DdgiCookedEmissiveProfileRejection.StaleEmissionEligibility));
        });
    }

    [Test]
    public void OmittedImportanceBound_IsFiniteAndConservativeForNonUniformTransform()
    {
        var material = new MaterialDefinition
        {
            EmissiveFactor = new Vector3(0.25f, 1.0f, 0.5f),
            EmissiveStrength = 4.0f
        };
        Matrix4x4 nonUniform =
            Matrix4x4.CreateScale(new Vector3(2.0f, 3.0f, 0.5f));

        double bound = DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
            factorNeutralLocalImportance: 2.0,
            material,
            nonUniform,
            doubleSided: true);

        // Frobenius-squared = 4 + 9 + 0.25. It upper-bounds every
        // orientation-specific area scale, including shear/non-uniform scale.
        Assert.That(bound, Is.EqualTo(2.0 * 4.0 * 13.25 * 2.0).Within(1e-9));
    }

    [Test]
    public void ExcludedAggregation_ReportsAllExcludedSceneAsFullySkipped()
    {
        DdgiEmissiveTriangleTableStats aggregate =
            DdgiEmissiveTriangleTable.IncludeExcluded(
                retained: default,
                excludedCandidateCount: 12,
                excludedImportance: 42.0);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.CandidateCount, Is.EqualTo(12));
            Assert.That(aggregate.SelectedCount, Is.Zero);
            Assert.That(aggregate.TotalImportance, Is.EqualTo(42.0));
            Assert.That(aggregate.SelectedImportance, Is.Zero);
            Assert.That(aggregate.SkippedImportance, Is.EqualTo(42.0));
            Assert.That(aggregate.SkippedEnergyFraction, Is.EqualTo(1.0f));
        });
    }

    private static GiPrimitiveTransportProfile CreateProfile(ModelMaterial material)
    {
        var subMesh = new ModelSubMesh
        {
            Name = "emissive-triangle",
            MaterialIndex = 0,
            Vertices =
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0)
            ],
            Indices = [0, 1, 2]
        };
        return GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material);
    }
}
