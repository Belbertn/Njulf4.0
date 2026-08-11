using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiDirectionSampleIdentityTests
{
    [Test]
    public void Identity_PreservesGenerationTimePdfAndCanonicalDirectionPayload()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.EightByEight;
        uint intraLeaf = SimpleDdgiDirectionSampleIdentity.PackIntraLeafSample(
            0.25d, 0.75d);
        int leaf = configuration.GetLeafIndex(3, 5);
        Vector3 direction = SimpleDdgiGuidingQuantizedHierarchy.CreateUniform(
            configuration).DirectionFromLeaf(leaf, 0.25d, 0.75d);
        const float pdf = 0.12345679f;
        SimpleDdgiDirectionSampleIdentity identity =
            SimpleDdgiDirectionSampleIdentity.Create(
                stableProbeId: 42UL,
                proposalEpoch: 7u,
                slotIndex: 19u,
                SimpleDdgiDirectionSamplingTechnique.Mixture,
                SimpleDdgiDirectionMixtureBranch.Guided,
                (uint)leaf,
                intraLeaf,
                direction,
                pdf,
                distributionGeneration: 11u);
        (double u, double v) =
            SimpleDdgiDirectionSampleIdentity.UnpackIntraLeafSample(intraLeaf);

        Assert.Multiple(() =>
        {
            Assert.That(identity.GenerationTimePdfBits,
                Is.EqualTo(BitConverter.SingleToUInt32Bits(pdf)));
            Assert.That(identity.GenerationTimePdf, Is.EqualTo(pdf));
            Assert.That(Vector3.Dot(direction, identity.DecodePackedDirection()),
                Is.GreaterThan(0.999999f));
            Assert.That(u, Is.GreaterThan(0.2499d).And.LessThan(0.2501d));
            Assert.That(v, Is.GreaterThan(0.7499d).And.LessThan(0.7501d));
            Assert.That(identity.Validate(configuration.LeafCount).IsValid, Is.True);
            Assert.That(SimpleDdgiGuidingReference.ValidateIdentity(
                identity, configuration).IsValid, Is.True);
        });
    }

    [Test]
    public void Identity_RejectsInvalidTechniqueOwnershipAndPdfPayloads()
    {
        SimpleDdgiDirectionSampleIdentity valid =
            SimpleDdgiDirectionSampleIdentity.Create(
                stableProbeId: 1UL,
                proposalEpoch: 1u,
                slotIndex: 0u,
                SimpleDdgiDirectionSamplingTechnique.Mixture,
                SimpleDdgiDirectionMixtureBranch.Uniform,
                leafIndex: 0u,
                intraLeafSampleBits:
                    SimpleDdgiDirectionSampleIdentity.PackIntraLeafSample(0.5d, 0.5d),
                direction: Vector3.UnitX,
                generationTimePdf: 0.25f,
                distributionGeneration: 1u);
        SimpleDdgiDirectionSampleIdentity invalidMaintenance = valid with
        {
            Technique = SimpleDdgiDirectionSamplingTechnique.UniformMaintenance,
            MixtureBranch = SimpleDdgiDirectionMixtureBranch.Guided
        };
        SimpleDdgiDirectionSampleIdentity invalidPdf = valid with
        {
            GenerationTimePdfBits = BitConverter.SingleToUInt32Bits(float.NaN)
        };

        Assert.Multiple(() =>
        {
            Assert.That(invalidMaintenance.Validate(64).Failure,
                Is.EqualTo(SimpleDdgiDirectionIdentityValidationFailure
                    .MaintenanceCannotUseGuidedBranch));
            Assert.That(invalidPdf.Validate(64).Failure,
                Is.EqualTo(SimpleDdgiDirectionIdentityValidationFailure
                    .InvalidGenerationTimePdf));
            Assert.That((valid with { LeafIndex = 64u }).Validate(64).Failure,
                Is.EqualTo(SimpleDdgiDirectionIdentityValidationFailure.LeafOutOfRange));
            Assert.That(SimpleDdgiGuidingReference.ValidateIdentity(
                valid with
                {
                    PackedDirectionOct32 =
                        SimpleDdgiTransportCachePacking.PackOctahedralSnorm16(
                            Vector3.UnitZ)
                },
                SimpleDdgiGuidingDistributionConfiguration.EightByEight).Failure,
                Is.EqualTo(SimpleDdgiDirectionIdentityValidationFailure
                    .DirectionDoesNotMatchLeafSample));
        });
    }

    [Test]
    public void IdentityHash_ChangesForEveryCacheRelevantGenerationField()
    {
        SimpleDdgiDirectionSampleIdentity identity =
            SimpleDdgiDirectionSampleIdentity.Create(
                91UL,
                5u,
                3u,
                SimpleDdgiDirectionSamplingTechnique.Mixture,
                SimpleDdgiDirectionMixtureBranch.Guided,
                12u,
                SimpleDdgiDirectionSampleIdentity.PackIntraLeafSample(0.2d, 0.8d),
                Vector3.Normalize(new Vector3(1.0f, 2.0f, 3.0f)),
                0.75f,
                9u);
        ulong original = identity.ComputeHash64();

        Assert.Multiple(() =>
        {
            Assert.That(identity.ComputeHash64(), Is.EqualTo(original));
            Assert.That((identity with { ProposalEpoch = 6u }).ComputeHash64(),
                Is.Not.EqualTo(original));
            Assert.That((identity with { DistributionGeneration = 10u }).ComputeHash64(),
                Is.Not.EqualTo(original));
            Assert.That((identity with { GenerationTimePdfBits =
                BitConverter.SingleToUInt32Bits(0.5f) }).ComputeHash64(),
                Is.Not.EqualTo(original));
            Assert.That((identity with { PackedDirectionOct32 = 0u }).ComputeHash64(),
                Is.Not.EqualTo(original));
        });
    }
}
