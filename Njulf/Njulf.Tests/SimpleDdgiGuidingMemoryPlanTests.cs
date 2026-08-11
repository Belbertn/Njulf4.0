using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGuidingMemoryPlanTests
{
    [Test]
    public void EightByEightLayout_UsesDocumentedDoubleBufferedByteEquation()
    {
        var request = new SimpleDdgiGuidingLayoutRequest(
            SimpleDdgiGuidingDistributionConfiguration.EightByEight,
            PhysicalProbeCapacity: 100,
            ScheduledGuidedProbeCapacity: 20,
            StorageAlignmentBytes: 16UL,
            AllocateValidationReferenceBank: true);
        SimpleDdgiGuidingLayout layout =
            SimpleDdgiGuidingLayoutCompiler.Compile(request);

        Assert.Multiple(() =>
        {
            Assert.That(layout.HierarchyWeightCount, Is.EqualTo(85));
            Assert.That(layout.HeaderBytes, Is.EqualTo(32UL));
            Assert.That(layout.PersistentWeightBytesPerBank, Is.EqualTo(170UL));
            Assert.That(layout.PersistentBankUnalignedBytes, Is.EqualTo(202UL));
            Assert.That(layout.PersistentBankStrideBytes, Is.EqualTo(208UL));
            Assert.That(layout.PersistentDoubleBufferedBytes, Is.EqualTo(41_600UL));
            Assert.That(layout.ValidationReferenceBankStrideBytes, Is.EqualTo(384UL));
            Assert.That(layout.ValidationReferenceBankBytes, Is.EqualTo(38_400UL));
            Assert.That(layout.TrainingScratchBytes, Is.EqualTo(5_120UL));
            Assert.That(layout.TotalBytes, Is.EqualTo(85_120UL));
        });
    }

    [Test]
    public void ZeroCapacityLayout_AllocatesNothingEvenInValidationMode()
    {
        var request = new SimpleDdgiGuidingLayoutRequest(
            SimpleDdgiGuidingDistributionConfiguration.FourByFour,
            PhysicalProbeCapacity: 0,
            ScheduledGuidedProbeCapacity: 0,
            StorageAlignmentBytes: 16UL,
            AllocateValidationReferenceBank: true);

        SimpleDdgiGuidingLayout layout =
            SimpleDdgiGuidingLayoutCompiler.Compile(request);

        Assert.Multiple(() =>
        {
            Assert.That(layout.HasAllocation, Is.False);
            Assert.That(layout.PersistentDoubleBufferedBytes, Is.Zero);
            Assert.That(layout.ValidationReferenceBankBytes, Is.Zero);
            Assert.That(layout.TrainingScratchBytes, Is.Zero);
            Assert.That(layout.TotalBytes, Is.Zero);
        });
    }

    [Test]
    public void LayoutCompiler_RejectsInvalidCapacityAndDetectsOverflow()
    {
        var invalidSchedule = new SimpleDdgiGuidingLayoutRequest(
            SimpleDdgiGuidingDistributionConfiguration.FourByFour,
            PhysicalProbeCapacity: 3,
            ScheduledGuidedProbeCapacity: 4,
            StorageAlignmentBytes: 16UL,
            AllocateValidationReferenceBank: false);
        var overflowing = new SimpleDdgiGuidingLayoutRequest(
            SimpleDdgiGuidingDistributionConfiguration.FourByFour,
            PhysicalProbeCapacity: 2,
            ScheduledGuidedProbeCapacity: 0,
            StorageAlignmentBytes: 1UL << 63,
            AllocateValidationReferenceBank: false);

        Assert.Multiple(() =>
        {
            Assert.That(() => SimpleDdgiGuidingLayoutCompiler.Compile(invalidSchedule),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => SimpleDdgiGuidingLayoutCompiler.Compile(overflowing),
                Throws.TypeOf<OverflowException>());
        });
    }

    [Test]
    public void DoubleBufferedPublication_RequiresSeparateNewerValidatedBank()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.EightByEight;
        var valid = new SimpleDdgiGuidingDoubleBuffer(
            SamplingBankIndex: 0,
            SamplingBank: CreateBank(configuration, generation: 7u, proposalEpoch: 4u,
                virtualProbeId: 55u, pageGeneration: 9u),
            BuildBankIndex: 1,
            BuildBank: CreateBank(configuration, generation: 8u, proposalEpoch: 4u,
                virtualProbeId: 55u, pageGeneration: 9u));
        var stale = valid with
        {
            BuildBank = CreateBank(configuration, generation: 7u, proposalEpoch: 4u,
                virtualProbeId: 55u, pageGeneration: 9u)
        };
        var wrongPage = valid with
        {
            BuildBank = CreateBank(configuration, generation: 8u, proposalEpoch: 4u,
                virtualProbeId: 55u, pageGeneration: 10u)
        };
        var wrapped = new SimpleDdgiGuidingDoubleBuffer(
            0,
            CreateBank(configuration, uint.MaxValue, 11u, 55u, 9u),
            1,
            CreateBank(configuration, 1u, 11u, 55u, 9u));

        Assert.Multiple(() =>
        {
            Assert.That(valid.ValidatePublication(configuration, 55u, 9u).IsValid,
                Is.True);
            Assert.That(stale.ValidatePublication(configuration, 55u, 9u).Failure,
                Is.EqualTo(SimpleDdgiGuidingDoubleBufferValidationFailure
                    .CandidateGenerationNotNewer));
            Assert.That(wrongPage.ValidatePublication(configuration, 55u, 9u).Failure,
                Is.EqualTo(SimpleDdgiGuidingDoubleBufferValidationFailure
                    .PageGenerationMismatch));
            Assert.That(wrapped.ValidatePublication(configuration, 55u, 9u).IsValid,
                Is.True);
        });
    }

    private static SimpleDdgiGuidingDistributionBank CreateBank(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        uint generation,
        uint proposalEpoch,
        uint virtualProbeId,
        uint pageGeneration) => new(
        new SimpleDdgiGuidingDistributionHeader(
            virtualProbeId,
            pageGeneration,
            generation,
            proposalEpoch,
            SampleCountAndAge: 12u,
            TotalIncidentEnergy: 1.0f,
            SimpleDdgiGuidingDistributionFlags.None),
        SimpleDdgiGuidingQuantizedHierarchy.CreateUniform(configuration));
}
