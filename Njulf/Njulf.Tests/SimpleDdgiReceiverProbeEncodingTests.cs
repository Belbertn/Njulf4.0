using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverProbeEncodingTests
{
    [Test]
    public void ReceiverProbeAbi_IsOneAlignedUvec4()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverProbe>(), Is.EqualTo(16));
            Assert.That(OffsetOf(nameof(GPUSimpleDdgiReceiverProbe.PackedRelocationXY)), Is.Zero);
            Assert.That(OffsetOf(nameof(GPUSimpleDdgiReceiverProbe.PackedRelocationZWeight)), Is.EqualTo(4));
            Assert.That(OffsetOf(nameof(GPUSimpleDdgiReceiverProbe.Flags)), Is.EqualTo(8));
            Assert.That(OffsetOf(nameof(GPUSimpleDdgiReceiverProbe.AtlasProbeAddress)), Is.EqualTo(12));
        });
    }

    [Test]
    public void FillFriendlyInvalidRecord_IsFailClosed()
    {
        GPUSimpleDdgiReceiverProbe invalid = SimpleDdgiReceiverProbeEncoding.Invalid;

        Assert.Multiple(() =>
        {
            Assert.That(invalid.PackedRelocationXY, Is.EqualTo(uint.MaxValue));
            Assert.That(invalid.PackedRelocationZWeight, Is.EqualTo(uint.MaxValue));
            Assert.That(invalid.Flags, Is.EqualTo(uint.MaxValue));
            Assert.That(invalid.AtlasProbeAddress, Is.EqualTo(uint.MaxValue));
            Assert.That(
                SimpleDdgiReceiverProbeEncoding.TryUnpack(
                    invalid,
                    1.0f,
                    out _,
                    out _),
                Is.False);
        });
    }

    [Test]
    public void SnormEndpointsAndWeight_RoundTripCanonicalValues()
    {
        const float spacing = 4.0f;
        Vector3 relocation = new(-2.0f, 2.0f, 0.0f);

        bool packed = SimpleDdgiReceiverProbeEncoding.TryPack(
            relocation,
            spacing,
            1.0f,
            SimpleDdgiReceiverProbeEncoding.FreshFlag,
            37u,
            out GPUSimpleDdgiReceiverProbe record);
        bool unpacked = SimpleDdgiReceiverProbeEncoding.TryUnpack(
            record,
            spacing,
            out Vector3 decodedRelocation,
            out float decodedWeight,
            out uint decodedGeneration);

        Assert.Multiple(() =>
        {
            Assert.That(packed, Is.True);
            Assert.That(unpacked, Is.True);
            Assert.That(record.PackedRelocationXY & 0xffffu, Is.EqualTo(0x8001u));
            Assert.That(record.PackedRelocationXY >> 16, Is.EqualTo(0x7fffu));
            Assert.That(record.PackedRelocationZWeight >> 16, Is.EqualTo(0xffffu));
            Assert.That(decodedRelocation.X, Is.EqualTo(-2.0f).Within(1.0e-6f));
            Assert.That(decodedRelocation.Y, Is.EqualTo(2.0f).Within(1.0e-6f));
            Assert.That(decodedRelocation.Z, Is.Zero.Within(1.0e-6f));
            Assert.That(decodedWeight, Is.EqualTo(1.0f).Within(1.0e-6f));
            Assert.That(
                record.Flags & ~SimpleDdgiReceiverProbeEncoding.SlotGenerationMask,
                Is.EqualTo(
                    SimpleDdgiReceiverProbeEncoding.PublishedCoherentFlag |
                    SimpleDdgiReceiverProbeEncoding.FreshFlag));
            Assert.That(decodedGeneration, Is.EqualTo(1u));
            Assert.That(record.AtlasProbeAddress, Is.EqualTo(37u));
        });
    }

    [Test]
    public void SlotGeneration_RoundTripsAndCannotCollideWithLifecycleFlags()
    {
        const uint generation = 0x00fe_dcabu;
        Assert.That(SimpleDdgiReceiverProbeEncoding.TryPack(
            Vector3.Zero,
            1.0f,
            0.5f,
            SimpleDdgiReceiverProbeEncoding.FreshFlag,
            generation,
            17u,
            out GPUSimpleDdgiReceiverProbe record), Is.True);
        Assert.That(SimpleDdgiReceiverProbeEncoding.TryUnpack(
            record,
            1.0f,
            out _,
            out _,
            out uint decodedGeneration), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(decodedGeneration, Is.EqualTo(generation));
            Assert.That(
                record.Flags & ~SimpleDdgiReceiverProbeEncoding.SlotGenerationMask,
                Is.EqualTo(
                    SimpleDdgiReceiverProbeEncoding.PublishedCoherentFlag |
                    SimpleDdgiReceiverProbeEncoding.FreshFlag));
            Assert.That(
                record.Flags >> SimpleDdgiReceiverProbeEncoding.SlotGenerationShift,
                Is.EqualTo(generation));
        });
    }

    [Test]
    public void MaximumUpdateRelocation_HasSubMillimeterQuantizationAtMeterSpacing()
    {
        Assert.That(
            SimpleDdgiReceiverProbeEncoding.MaximumUpdateRelocationInProbeSpacings,
            Is.LessThan(
                SimpleDdgiReceiverProbeEncoding.RelocationEncodingRangeInProbeSpacings));

        const float spacing = 1.0f;
        float limit = spacing *
            SimpleDdgiReceiverProbeEncoding.MaximumUpdateRelocationInProbeSpacings;
        Vector3 relocation = new(limit, -limit, limit * 0.5f);

        Assert.That(SimpleDdgiReceiverProbeEncoding.TryPack(
            relocation,
            spacing,
            0.75f,
            0u,
            9u,
            out GPUSimpleDdgiReceiverProbe record), Is.True);
        Assert.That(SimpleDdgiReceiverProbeEncoding.TryUnpack(
            record,
            spacing,
            out Vector3 decoded,
            out float decodedWeight), Is.True);

        float relocationErrorBound =
            spacing * SimpleDdgiReceiverProbeEncoding.RelocationEncodingRangeInProbeSpacings /
            short.MaxValue;
        Assert.Multiple(() =>
        {
            Assert.That(MathF.Abs(decoded.X - relocation.X), Is.LessThanOrEqualTo(relocationErrorBound));
            Assert.That(MathF.Abs(decoded.Y - relocation.Y), Is.LessThanOrEqualTo(relocationErrorBound));
            Assert.That(MathF.Abs(decoded.Z - relocation.Z), Is.LessThanOrEqualTo(relocationErrorBound));
            Assert.That(decodedWeight, Is.EqualTo(0.75f).Within(0.5f / ushort.MaxValue));
        });
    }

    [Test]
    public void ActiveWeightThreshold_CannotQuantizeAcceptedWeightBackToRejected()
    {
        Assert.That(SimpleDdgiReceiverProbeEncoding.TryPack(
            Vector3.Zero,
            1.0f,
            SimpleDdgiReceiverProbeEncoding.ActiveWeightRejectionThreshold,
            0u,
            1u,
            out GPUSimpleDdgiReceiverProbe rejected), Is.True);
        Assert.That(SimpleDdgiReceiverProbeEncoding.TryPack(
            Vector3.Zero,
            1.0f,
            MathF.BitIncrement(SimpleDdgiReceiverProbeEncoding.ActiveWeightRejectionThreshold),
            0u,
            1u,
            out GPUSimpleDdgiReceiverProbe accepted), Is.True);

        uint rejectedCode = rejected.PackedRelocationZWeight >> 16;
        uint acceptedCode = accepted.PackedRelocationZWeight >> 16;
        Assert.Multiple(() =>
        {
            Assert.That(rejectedCode, Is.Zero);
            Assert.That(acceptedCode, Is.EqualTo(66u));
            Assert.That(
                acceptedCode / 65535.0f,
                Is.GreaterThan(SimpleDdgiReceiverProbeEncoding.ActiveWeightRejectionThreshold));
        });
    }

    [Test]
    public void RandomizedRoundTrip_StaysWithinDeclaredQuantizationBounds()
    {
        var random = new Random(0x51dd61);
        for (int sample = 0; sample < 10_000; sample++)
        {
            float spacing = 0.125f + random.NextSingle() * 31.875f;
            float relocationLimit = spacing *
                SimpleDdgiReceiverProbeEncoding.MaximumUpdateRelocationInProbeSpacings;
            Vector3 relocation = new(
                (random.NextSingle() * 2.0f - 1.0f) * relocationLimit,
                (random.NextSingle() * 2.0f - 1.0f) * relocationLimit,
                (random.NextSingle() * 2.0f - 1.0f) * relocationLimit);
            float weight = 0.01f + random.NextSingle() * 0.99f;

            Assert.That(SimpleDdgiReceiverProbeEncoding.TryPack(
                relocation,
                spacing,
                weight,
                0u,
                checked((uint)sample),
                out GPUSimpleDdgiReceiverProbe record), Is.True, $"pack sample {sample}");
            Assert.That(SimpleDdgiReceiverProbeEncoding.TryUnpack(
                record,
                spacing,
                out Vector3 decoded,
                out float decodedWeight), Is.True, $"unpack sample {sample}");

            float relocationErrorBound = spacing *
                SimpleDdgiReceiverProbeEncoding.RelocationEncodingRangeInProbeSpacings /
                short.MaxValue + 1.0e-6f;
            Assert.Multiple(() =>
            {
                Assert.That(MathF.Abs(decoded.X - relocation.X), Is.LessThanOrEqualTo(relocationErrorBound), $"x sample {sample}");
                Assert.That(MathF.Abs(decoded.Y - relocation.Y), Is.LessThanOrEqualTo(relocationErrorBound), $"y sample {sample}");
                Assert.That(MathF.Abs(decoded.Z - relocation.Z), Is.LessThanOrEqualTo(relocationErrorBound), $"z sample {sample}");
                Assert.That(MathF.Abs(decodedWeight - weight), Is.LessThanOrEqualTo(0.5f / ushort.MaxValue + 1.0e-6f), $"weight sample {sample}");
            });
        }
    }

    [Test]
    public void InvalidInputs_AreRejectedWithoutPublishingPartialPayload()
    {
        static bool Pack(Vector3 relocation, float spacing, float weight, uint address) =>
            SimpleDdgiReceiverProbeEncoding.TryPack(
                relocation,
                spacing,
                weight,
                0u,
                address,
                out _);

        Assert.Multiple(() =>
        {
            Assert.That(Pack(Vector3.Zero, 0.0f, 1.0f, 0u), Is.False);
            Assert.That(Pack(Vector3.Zero, float.NaN, 1.0f, 0u), Is.False);
            Assert.That(Pack(new Vector3(float.PositiveInfinity, 0.0f, 0.0f), 1.0f, 1.0f, 0u), Is.False);
            Assert.That(Pack(new Vector3(0.5001f, 0.0f, 0.0f), 1.0f, 1.0f, 0u), Is.False);
            Assert.That(Pack(Vector3.Zero, 1.0f, -0.01f, 0u), Is.False);
            Assert.That(Pack(Vector3.Zero, 1.0f, 1.01f, 0u), Is.False);
            Assert.That(Pack(Vector3.Zero, 1.0f, 1.0f, uint.MaxValue), Is.False);
            Assert.That(
                SimpleDdgiReceiverProbeEncoding.TryPack(
                    Vector3.Zero,
                    1.0f,
                    1.0f,
                    SimpleDdgiReceiverProbeEncoding.PublishedCoherentFlag,
                    0u,
                    out _),
                Is.False);
        });
    }

    private static int OffsetOf(string field) =>
        Marshal.OffsetOf<GPUSimpleDdgiReceiverProbe>(field).ToInt32();
}
