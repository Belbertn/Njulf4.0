using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiVisibilityPackingTests
{
    [Test]
    public void Rg16Moments_UseOneWordAndRoundTripFiniteValues()
    {
        Vector2 source = new(7.125f, 53.8125f);
        uint packed = SimpleDdgiVisibilityPacking.PackMoments(source);
        Vector2 decoded = SimpleDdgiVisibilityPacking.UnpackMoments(packed);

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVisibilityPacking.BytesPerTexel, Is.EqualTo(4));
            Assert.That(decoded.X, Is.EqualTo(source.X));
            Assert.That(decoded.Y, Is.EqualTo(source.Y));
            Assert.That(SimpleDdgiMemoryPlan.VisibilityBytesPerProbe,
                Is.EqualTo(16UL * 16UL * 4UL));
        });
    }

    [Test]
    public void TypedAddressing_DoesNotReuseIrradianceStride()
    {
        const uint probe = 11;
        const uint texel = 73;
        uint visibilityWord = SimpleDdgiVisibilityPacking.ResolveWordAddress(
            probe, texel, 16);
        uint irradianceWord = checked((probe * 8u * 8u + texel % 64u) * 2u);

        Assert.Multiple(() =>
        {
            Assert.That(visibilityWord, Is.EqualTo(probe * 256u + texel));
            Assert.That(visibilityWord, Is.Not.EqualTo(irradianceWord));
            Assert.That(
                () => SimpleDdgiVisibilityPacking.ResolveWordAddress(probe, 256, 16),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void VisibilityValidity_IsOwnedByProbeState()
    {
        const uint unrelatedFlags = (1u << 0) | (1u << 4);
        uint valid = unrelatedFlags |
            SimpleDdgiVisibilityPacking.VisibilityValidProbeFlag;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVisibilityPacking.IsVisibilityValid(unrelatedFlags), Is.False);
            Assert.That(SimpleDdgiVisibilityPacking.IsVisibilityValid(valid), Is.True);
            Assert.That(valid & 0xffff_ff00u, Is.Zero,
                "the validity bit must not overlap the 24-bit physical generation");
        });
    }

    [Test]
    public void Rg16Moments_PreserveLegacyXyBitsAtHalfBoundaries()
    {
        ushort[] finiteHalfBits =
        [
            0x0000, // zero
            0x0001, // minimum subnormal
            0x03ff, // maximum subnormal
            0x0400, // minimum normal
            0x3c00, // one
            0x7bff  // maximum finite
        ];
        foreach (ushort meanBits in finiteHalfBits)
        foreach (ushort secondMomentBits in finiteHalfBits)
        {
            Vector2 moments = new(
                (float)BitConverter.UInt16BitsToHalf(meanBits),
                (float)BitConverter.UInt16BitsToHalf(secondMomentBits));
            uint packedRg = SimpleDdgiVisibilityPacking.PackMoments(moments);
            uint legacyRgbaXyWord = meanBits | ((uint)secondMomentBits << 16);

            Assert.That(packedRg, Is.EqualTo(legacyRgbaXyWord),
                $"mean=0x{meanBits:x4}, second=0x{secondMomentBits:x4}");
        }
    }

    [Test]
    public void Rg16Moments_RejectNonFiniteAndClampToPhysicalHalfRange()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => SimpleDdgiVisibilityPacking.PackMoments(
                new Vector2(float.NaN, 1.0f)),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => SimpleDdgiVisibilityPacking.PackMoments(
                new Vector2(1.0f, float.PositiveInfinity)),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });

        Vector2 decoded = SimpleDdgiVisibilityPacking.UnpackMoments(
            SimpleDdgiVisibilityPacking.PackMoments(
                new Vector2(-1.0f, 100_000.0f)));
        Assert.That(decoded, Is.EqualTo(new Vector2(0.0f, 65_504.0f)));
    }

    [Test]
    public void TypedAddressing_RejectsOverflowBeforeReturningAnAlias()
    {
        Assert.That(
            () => SimpleDdgiVisibilityPacking.ResolveWordAddress(
                uint.MaxValue,
                0u,
                16u),
            Throws.TypeOf<OverflowException>());
    }
}
