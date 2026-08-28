using System.Numerics;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationEnergyOwnershipTests
{
    [Test]
    public void ProductionGrid_NormalizesEveryExclusiveDomain()
    {
        foreach (float roughness in new[] { 0.0f, 0.2f, 0.55f, 0.7f, 1.0f })
        foreach (float geometricConfidence in new[] { 0.0f, 0.35f, 1.0f })
        foreach (float probeConfidence in new[] { 0.0f, 0.6f, 1.0f })
        foreach (float ddgiConfidence in new[] { 0.0f, 0.5f, 1.0f })
        {
            DdgiIndirectSpecularOwnership glossy =
                DdgiIndirectSpecularSelector.Select(
                    geometricConfidence,
                    probeConfidence,
                    ddgiConfidence,
                    roughness,
                    ddgiMinimumRoughness: 0.55f,
                    ddgiFullWeightRoughness: 0.70f);
            float diffuseDdgi = Math.Clamp(ddgiConfidence, 0.0f, 1.0f);
            var sample = ValidSample() with
            {
                DiffuseDdgiWeight = diffuseDdgi,
                DiffuseEnvironmentWeight = 1.0f - diffuseDdgi,
                GlossyOwnership = glossy
            };

            GlobalIlluminationEnergyOwnershipValidation result =
                GlobalIlluminationEnergyOwnershipContract.Validate(sample);

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, result.FailureReason);
                Assert.That(result.DiffuseOwnershipSum,
                    Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(result.GlossyOwnershipSum,
                    Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(result.MaximumSurfaceDirectionalAlbedo,
                    Is.LessThanOrEqualTo(1.0f));
            });
        }
    }

    [Test]
    public void DuplicateEmissiveNextEventEstimator_IsRejected()
    {
        GlobalIlluminationEnergyOwnershipSample sample = ValidSample() with
        {
            EmissiveTransportOwnership =
                DdgiEmissiveEstimatorOwnership.DirectSurfaceHit |
                DdgiEmissiveEstimatorOwnership.TriangleNextEvent |
                DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent
        };

        GlobalIlluminationEnergyOwnershipValidation result =
            GlobalIlluminationEnergyOwnershipContract.Validate(sample);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.FailureReason,
                Is.EqualTo("emissive-estimator-ownership-invalid"));
        });
    }

    [Test]
    public void OverBudgetSurfaceOrMergedVolumetricOwners_FailClosed()
    {
        GlobalIlluminationEnergyOwnershipValidation surface =
            GlobalIlluminationEnergyOwnershipContract.Validate(
                ValidSample() with
                {
                    DiffuseDirectionalAlbedo = new Vector3(0.95f),
                    GlossyDirectionalAlbedo = new Vector3(0.10f)
                });
        GlobalIlluminationEnergyOwnershipValidation volume =
            GlobalIlluminationEnergyOwnershipContract.Validate(
                ValidSample() with { VolumetricOwnersSeparated = false });

        Assert.Multiple(() =>
        {
            Assert.That(surface.Passed, Is.False);
            Assert.That(surface.FailureReason,
                Is.EqualTo("surface-brdf-energy-exceeds-one"));
            Assert.That(volume.Passed, Is.False);
            Assert.That(volume.FailureReason,
                Is.EqualTo(
                    "volumetric-direct-indirect-ownership-not-separated"));
        });
    }

    private static GlobalIlluminationEnergyOwnershipSample ValidSample() => new(
        DiffuseDirectionalAlbedo: new Vector3(0.70f, 0.55f, 0.40f),
        GlossyDirectionalAlbedo: new Vector3(0.08f, 0.10f, 0.12f),
        DirectSurfaceOwner: 1.0f,
        DiffuseDdgiWeight: 0.65f,
        DiffuseEnvironmentWeight: 0.35f,
        GlossyOwnership: DdgiIndirectSpecularSelector.Select(
            screenOrGeometricConfidence: 0.75f,
            localReflectionProbeConfidence: 0.5f,
            ddgiConfidence: 0.8f,
            perceptualRoughness: 0.75f,
            ddgiMinimumRoughness: 0.55f,
            ddgiFullWeightRoughness: 0.70f),
        EmissiveSurfaceOwner: 1.0f,
        EmissiveTransportOwnership:
            DdgiEmissiveTransportContract.ResolveOwnership(
                triangleSampling: true,
                cachedMultiBounce: true),
        VolumetricDirectOwner: 1.0f,
        VolumetricIndirectOwner: 1.0f,
        VolumetricOwnersSeparated: true);
}
