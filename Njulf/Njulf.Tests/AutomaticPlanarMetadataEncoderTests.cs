using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AutomaticPlanarMetadataEncoderTests
{
    [Test]
    public void DenseBitset_NormalizesAndPreservesBoundaryIndicesExactly()
    {
        uint[] source = [33, 0, 32, 31, 1, 33, 1_000];
        AutomaticPlanarMetadataBankLayout layout = Build(
            [Input(0, source)],
            AutomaticPlanarExclusionEncodingMode.BitsetAuto,
            bankWords: 64);
        AutomaticPlanarMetadataCaptureLayout capture =
            layout.Captures.Single();

        Assert.Multiple(() =>
        {
            Assert.That(layout.Fits, Is.True, layout.Detail);
            Assert.That(capture.ExclusionEncoding, Is.EqualTo(
                AutomaticPlanarExclusionPayloadEncoding.DenseBitset));
            Assert.That(capture.ExcludedObjectCount, Is.EqualTo(6));
            Assert.That(capture.ExclusionPayload, Has.Length.EqualTo(32));
            Assert.That(capture.ExclusionDescriptor, Is.EqualTo(
                AutomaticPlanarMetadataEncoder.DenseBitsetFlag | 32u));
            foreach (uint present in new uint[] { 0, 1, 31, 32, 33, 1_000 })
            {
                Assert.That(
                    AutomaticPlanarMetadataEncoder.Contains(capture, present),
                    Is.True,
                    $"present index {present}");
            }
            foreach (uint absent in new uint[] { 2, 30, 34, 999, 1_001 })
            {
                Assert.That(
                    AutomaticPlanarMetadataEncoder.Contains(capture, absent),
                    Is.False,
                    $"absent index {absent}");
            }
        });
    }

    [Test]
    public void EmptySet_UsesZeroWordBitsetAndNeverContainsAnObject()
    {
        AutomaticPlanarMetadataBankLayout layout = Build(
            [Input(0, [])],
            AutomaticPlanarExclusionEncodingMode.BitsetAuto,
            bankWords: 1);
        AutomaticPlanarMetadataCaptureLayout capture = layout.Captures[0];

        Assert.Multiple(() =>
        {
            Assert.That(layout.Fits, Is.True);
            Assert.That(capture.ExclusionDescriptor, Is.EqualTo(
                AutomaticPlanarMetadataEncoder.DenseBitsetFlag));
            Assert.That(capture.ExclusionPayload, Is.Empty);
            Assert.That(
                AutomaticPlanarMetadataEncoder.Contains(capture, 0),
                Is.False);
            Assert.That(
                AutomaticPlanarMetadataEncoder.Contains(capture, uint.MaxValue),
                Is.False);
        });
    }

    [Test]
    public void CapacityPressure_ProducesDeterministicMixedBitsetAndExactList()
    {
        AutomaticPlanarMetadataBankLayout layout = Build(
            [
                Input(0, [0, 31, 32]),
                Input(1, [1_000])
            ],
            AutomaticPlanarExclusionEncodingMode.BitsetAuto,
            bankWords: 4);

        Assert.Multiple(() =>
        {
            Assert.That(layout.Fits, Is.True, layout.Detail);
            Assert.That(layout.WordsUsed, Is.EqualTo(3));
            Assert.That(layout.BitsetCaptureCount, Is.EqualTo(1));
            Assert.That(layout.SortedListCaptureCount, Is.EqualTo(1));
            Assert.That(layout.Captures[0].ExclusionEncoding, Is.EqualTo(
                AutomaticPlanarExclusionPayloadEncoding.DenseBitset));
            Assert.That(layout.Captures[1].ExclusionEncoding, Is.EqualTo(
                AutomaticPlanarExclusionPayloadEncoding.SortedList));
            Assert.That(layout.Captures[1].ExclusionPayload,
                Is.EqualTo(new uint[] { 1_000 }));
            Assert.That(
                AutomaticPlanarMetadataEncoder.Contains(
                    layout.Captures[1],
                    1_000),
                Is.True);
        });
    }

    [Test]
    public void ExactFitAndOneWordOver_UseBitsetThenExactFallback()
    {
        AutomaticPlanarMetadataBankLayout exactFit = Build(
            [Input(0, [0, 32])],
            AutomaticPlanarExclusionEncodingMode.BitsetAuto,
            bankWords: 2);
        AutomaticPlanarMetadataBankLayout fallback = Build(
            [Input(0, [100])],
            AutomaticPlanarExclusionEncodingMode.BitsetAuto,
            bankWords: 1);

        Assert.Multiple(() =>
        {
            Assert.That(exactFit.Fits, Is.True);
            Assert.That(exactFit.Captures[0].ExclusionEncoding, Is.EqualTo(
                AutomaticPlanarExclusionPayloadEncoding.DenseBitset));
            Assert.That(exactFit.WordsUsed, Is.EqualTo(2));
            Assert.That(fallback.Fits, Is.True);
            Assert.That(fallback.Captures[0].ExclusionEncoding, Is.EqualTo(
                AutomaticPlanarExclusionPayloadEncoding.SortedList));
            Assert.That(fallback.WordsUsed, Is.EqualTo(1));
        });
    }

    [Test]
    public void SparseMaximumIndex_FallsBackWithoutAllocatingAnUnboundedBitset()
    {
        AutomaticPlanarMetadataBankLayout layout = Build(
            [Input(0, [1, uint.MaxValue])],
            AutomaticPlanarExclusionEncodingMode.BitsetAuto,
            bankWords: 2);
        AutomaticPlanarMetadataCaptureLayout capture = layout.Captures[0];

        Assert.Multiple(() =>
        {
            Assert.That(layout.Fits, Is.True, layout.Detail);
            Assert.That(capture.ExclusionEncoding, Is.EqualTo(
                AutomaticPlanarExclusionPayloadEncoding.SortedList));
            Assert.That(capture.ExclusionPayload,
                Is.EqualTo(new uint[] { 1, uint.MaxValue }));
            Assert.That(
                AutomaticPlanarMetadataEncoder.Contains(
                    capture,
                    uint.MaxValue),
                Is.True);
            Assert.That(
                AutomaticPlanarMetadataEncoder.Contains(
                    capture,
                    uint.MaxValue - 1),
                Is.False);
        });
    }

    [Test]
    public void TwoSlots_ReceiveDeterministicNonOverlappingPayloadOffsets()
    {
        AutomaticPlanarMetadataBankLayout layout =
            AutomaticPlanarMetadataEncoder.Build(
                [
                    new AutomaticPlanarMetadataCaptureInput(
                        0, [9], [0], [101, 102]),
                    new AutomaticPlanarMetadataCaptureInput(
                        1, [8, 7], [32], [201])
                ],
                AutomaticPlanarExclusionEncodingMode.BitsetAuto,
                bankWordCount: 32,
                variableDataWordOffset: 5);

        Assert.Multiple(() =>
        {
            Assert.That(layout.Fits, Is.True, layout.Detail);
            Assert.That(layout.Captures[0].ReceiverOffset, Is.EqualTo(5u));
            Assert.That(layout.Captures[0].ExclusionOffset, Is.EqualTo(6u));
            Assert.That(layout.Captures[0].TextureOffset, Is.EqualTo(7u));
            Assert.That(layout.Captures[1].ReceiverOffset, Is.EqualTo(9u));
            Assert.That(layout.Captures[1].ExclusionOffset, Is.EqualTo(11u));
            Assert.That(layout.Captures[1].TextureOffset, Is.EqualTo(13u));
            Assert.That(layout.WordsUsed, Is.EqualTo(14));
        });
    }

    [Test]
    public void ImpossibleExactPayload_FailsWithoutReturningPartialCaptures()
    {
        AutomaticPlanarMetadataBankLayout layout = Build(
            [new AutomaticPlanarMetadataCaptureInput(
                0,
                [7, 8],
                [0, 32],
                [])],
            AutomaticPlanarExclusionEncodingMode.SortedList,
            bankWords: 3);

        Assert.Multiple(() =>
        {
            Assert.That(layout.Fits, Is.False);
            Assert.That(layout.Captures, Is.Empty);
            Assert.That(layout.Detail, Does.Contain("requires 4 words"));
        });
    }

    [Test]
    public void OverrideMode_IsStrictAndDefaultsToSortedList()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AutomaticPlanarMetadataEncoder.ResolveMode(null),
                Is.EqualTo(AutomaticPlanarExclusionEncodingMode.SortedList));
            Assert.That(
                AutomaticPlanarMetadataEncoder.ResolveMode("SortedList"),
                Is.EqualTo(AutomaticPlanarExclusionEncodingMode.SortedList));
            Assert.That(
                () => AutomaticPlanarMetadataEncoder.ResolveMode("approximate"),
                Throws.InvalidOperationException);
        });
    }

    private static AutomaticPlanarMetadataBankLayout Build(
        IReadOnlyList<AutomaticPlanarMetadataCaptureInput> captures,
        AutomaticPlanarExclusionEncodingMode mode,
        int bankWords) => AutomaticPlanarMetadataEncoder.Build(
        captures,
        mode,
        bankWords,
        variableDataWordOffset: 0);

    private static AutomaticPlanarMetadataCaptureInput Input(
        int slot,
        IReadOnlyList<uint> excluded) => new(
        slot,
        [],
        excluded,
        []);
}
