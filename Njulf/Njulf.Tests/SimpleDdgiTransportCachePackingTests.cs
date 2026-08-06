using System.Numerics;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiTransportCachePackingTests
{
    [TestCase(SimpleDdgiTransportCacheFormat.Legacy36, 9)]
    [TestCase(SimpleDdgiTransportCacheFormat.Compact28, 7)]
    [TestCase(SimpleDdgiTransportCacheFormat.Compact24, 6)]
    public void CacheFormats_RoundTripTheirDeclaredPayload(
        SimpleDdgiTransportCacheFormat format,
        int expectedWords)
    {
        const uint probe = 17;
        const uint ray = 23;
        const uint maximumRays = 128;
        Vector3 reconstructed = SimpleDdgiDirectionCodebook.ReconstructDirection(
            probe, ray, maximumRays, 37);
        var source = new SimpleDdgiTransportCachePacking.Sample(
            new Vector3(1.25f, 2.5f, 4.0f),
            17.3125f,
            reconstructed,
            Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.2f)),
            new Vector3(0.2f, 0.5f, 0.8f),
            new Vector3(0.1f, 0.3f, 0.6f),
            0.75f,
            2,
            0x00ab_cdefu,
            91,
            37,
            64);
        uint[] words = new uint[9];

        int written = SimpleDdgiTransportCachePacking.Pack(
            format, source, words, out SimpleDdgiTransportCachePacking.PackingError error);
        bool decoded = SimpleDdgiTransportCachePacking.TryUnpack(
            format,
            words,
            probe,
            ray,
            maximumRays,
            source.ProbeGeneration,
            source.SourceLightingGeneration,
            source.SourceEpoch,
            source.SourceRayCount,
            out SimpleDdgiTransportCachePacking.Sample result);

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(expectedWords));
            Assert.That(decoded, Is.True);
            Assert.That(result.ProbeGeneration, Is.EqualTo(source.ProbeGeneration));
            Assert.That(result.SourceEpoch, Is.EqualTo(source.SourceEpoch));
            Assert.That(result.SourceRayCount, Is.EqualTo(source.SourceRayCount));
            Assert.That(result.Distance, Is.EqualTo(source.Distance).Within(
                format is SimpleDdgiTransportCacheFormat.Legacy36 or
                    SimpleDdgiTransportCacheFormat.Compact28
                        ? 0.0f
                        : 0.01f));
            Assert.That(Vector3.Dot(result.Direction, reconstructed), Is.GreaterThan(0.999999f));
            Assert.That(error.MaximumRadianceAbsoluteError, Is.LessThan(0.002f));
        });
    }

    [Test]
    public void Legacy36_PreservesFp32RadianceAndDistanceBits()
    {
        var source = CreateSample() with
        {
            SourceRadiance = new Vector3(1.234567f, 2.345678f, 3.456789f),
            Distance = 12_345.678f
        };
        Span<uint> words = stackalloc uint[9];

        Assert.That(SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Legacy36,
            source,
            words,
            out var error), Is.EqualTo(9));
        uint radianceXBits = words[0];
        uint radianceYBits = words[1];
        uint radianceZBits = words[2];
        uint distanceBits = words[3];

        Assert.Multiple(() =>
        {
            Assert.That(radianceXBits, Is.EqualTo(BitConverter.SingleToUInt32Bits(source.SourceRadiance.X)));
            Assert.That(radianceYBits, Is.EqualTo(BitConverter.SingleToUInt32Bits(source.SourceRadiance.Y)));
            Assert.That(radianceZBits, Is.EqualTo(BitConverter.SingleToUInt32Bits(source.SourceRadiance.Z)));
            Assert.That(distanceBits, Is.EqualTo(BitConverter.SingleToUInt32Bits(source.Distance)));
            Assert.That(error.MaximumRadianceAbsoluteError, Is.Zero);
            Assert.That(error.DistanceAbsoluteError, Is.Zero);
        });
    }

    [Test]
    public void Compact28_PreservesLargeFiniteDistanceWhileCompact24Clamps()
    {
        var source = CreateSample() with { Distance = 80_000.0f };
        Span<uint> compact28 = stackalloc uint[7];
        Span<uint> compact24 = stackalloc uint[6];

        Assert.That(SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Compact28, source, compact28, out var error28), Is.EqualTo(7));
        Assert.That(SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Compact24, source, compact24, out var error24), Is.EqualTo(6));
        float compact28Distance = BitConverter.UInt32BitsToSingle(compact28[2]);

        Assert.Multiple(() =>
        {
            Assert.That(compact28Distance, Is.EqualTo(80_000.0f));
            Assert.That(error28.DistanceAbsoluteError, Is.Zero);
            Assert.That(error24.DistanceAbsoluteError, Is.EqualTo(14_496.0f));
        });
    }

    [Test]
    public void InvalidAndSaturatedPayloads_AreReportedExplicitly()
    {
        Span<uint> words = stackalloc uint[7];
        words.Fill(0xdead_beefu);
        var invalid = CreateSample() with
        {
            SourceRadiance = new Vector3(float.NaN, 0.0f, 0.0f)
        };
        var saturated = CreateSample() with
        {
            SourceRadiance = new Vector3(1_000.0f, 500.0f, 250.0f)
        };

        Assert.That(SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Compact28, invalid, words, out _), Is.Zero);
        Assert.That(words.ToArray(), Is.All.Zero,
            "A rejected write must invalidate stale destination bytes.");
        Assert.That(SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Compact28, saturated, words, out var error), Is.EqualTo(7));
        Assert.That(error.RadianceClamped, Is.True);
        Assert.That(error.MaximumRadianceAbsoluteError, Is.GreaterThan(100.0f));
    }

    [Test]
    public void FiniteExtremeHdrRadiance_IsBoundedWithoutCollapsingToBlack()
    {
        Vector3 bounded = SimpleDdgiTransportCachePacking.ClampTransportRadiance(
            new Vector3(float.MaxValue, 0.0f, 0.0f));
        float luminance = Vector3.Dot(
            bounded,
            new Vector3(0.2126f, 0.7152f, 0.0722f));

        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(bounded.X), Is.True);
            Assert.That(bounded.X, Is.GreaterThan(0.0f));
            Assert.That(bounded.Y, Is.Zero);
            Assert.That(bounded.Z, Is.Zero);
            Assert.That(luminance,
                Is.EqualTo(SimpleDdgiTransportCachePacking.MaximumTransportLuminance)
                    .Within(0.001f));
        });
    }

    [Test]
    public void ScratchMetadata_PreservesExactHitKindAndFiveBitEpoch()
    {
        uint metadata = SimpleDdgiTransportCachePacking.PackRayMetadata(12.375f, 4, 63);
        bool valid = SimpleDdgiTransportCachePacking.TryUnpackRayMetadata(
            metadata, out float distance, out int hitKind, out uint epoch);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(distance, Is.EqualTo(12.375f));
            Assert.That(hitKind, Is.EqualTo(4));
            Assert.That(epoch, Is.EqualTo(31u));
            Assert.That(SimpleDdgiTransportCachePacking.TryUnpackRayMetadata(
                metadata | (1u << 31), out _, out _, out _), Is.False);
        });
    }

    [Test]
    public void ScratchMetadata_InvalidPayloadsFailClosed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiTransportCachePacking.PackRayMetadata(
                float.NaN, 1, 1), Is.Zero);
            Assert.That(SimpleDdgiTransportCachePacking.PackRayMetadata(
                -1.0f, 1, 1), Is.Zero);
            Assert.That(SimpleDdgiTransportCachePacking.PackRayMetadata(
                1.0f, 5, 1), Is.Zero);
            Assert.That(SimpleDdgiTransportCachePacking.PackRayMetadata(
                1.0f, 1, 0), Is.Zero);
            uint negativeVisibility =
                BitConverter.HalfToUInt16Bits((Half)(-1.0f)) |
                SimpleDdgiTransportCachePacking.RayMetadataValidFlag;
            Assert.That(SimpleDdgiTransportCachePacking.TryUnpackRayMetadata(
                negativeVisibility,
                out _, out _, out _), Is.False,
                "A corrupted negative FP16 visibility distance is invalid.");
        });
    }

    [Test]
    public void CachePack_RejectsMalformedSurfaceAndIdentityPayloads()
    {
        Span<uint> words = stackalloc uint[9];
        var source = CreateSample();
        var malformed = new[]
        {
            source with { Direction = Vector3.Zero },
            source with { Normal = new Vector3(float.PositiveInfinity, 0.0f, 0.0f) },
            source with { DiffuseReflectance = new Vector3(-0.01f) },
            source with { TransmittedDiffuseReflectance = new Vector3(float.NaN) },
            source with { MaterialOcclusion = float.NaN },
            source with { ProbeGeneration = 0u },
            source with { SourceLightingGeneration = 0u },
            source with { SourceEpoch = 0u }
        };

        foreach (var invalid in malformed)
        {
            words.Fill(0xdead_beefu);
            Assert.That(SimpleDdgiTransportCachePacking.Pack(
                SimpleDdgiTransportCacheFormat.Legacy36,
                invalid,
                words,
                out _), Is.Zero);
            Assert.That(words.ToArray(), Is.All.Zero);
        }
    }

    [Test]
    public void CacheIdentityMismatch_FailsClosedForEveryFormat()
    {
        var source = CreateSample();
        uint[] words = new uint[9];
        foreach (SimpleDdgiTransportCacheFormat format in new[]
                 {
                     SimpleDdgiTransportCacheFormat.Legacy36,
                     SimpleDdgiTransportCacheFormat.Compact28,
                     SimpleDdgiTransportCacheFormat.Compact24
                 })
        {
            Assert.That(SimpleDdgiTransportCachePacking.Pack(
                format, source, words, out _), Is.EqualTo(format.WordCount()));

            Assert.Multiple(() =>
            {
                Assert.That(TryDecode(format, words, source with
                {
                    ProbeGeneration = source.ProbeGeneration + 1u
                }), Is.False, $"{format}: generation");
                Assert.That(TryDecode(format, words, source with
                {
                    SourceEpoch = source.SourceEpoch + 1u
                }), Is.False, $"{format}: epoch");
                if (format == SimpleDdgiTransportCacheFormat.Legacy36)
                {
                    Assert.That(TryDecode(format, words, source with
                    {
                        SourceRayCount = source.SourceRayCount / 2u
                    }), Is.False, $"{format}: stored ray count");
                }
            });
        }
    }

    [Test]
    public void Compact24_DecodesFiniteHalfExponentBoundariesWithinHalfUlp()
    {
        Span<uint> words = stackalloc uint[6];
        for (int exponent = -14; exponent <= 15; exponent++)
        {
            float boundary = MathF.Pow(2.0f, exponent);
            float ulp = MathF.Pow(2.0f, exponent - 10);
            foreach (float offset in new[] { -0.5f, -0.25f, 0.0f, 0.25f, 0.5f })
            {
                float distance = Math.Clamp(
                    boundary + offset * ulp,
                    0.0f,
                    SimpleDdgiTransportCachePacking.MaximumFiniteHalf);
                var source = CreateSample() with { Distance = distance };

                Assert.That(SimpleDdgiTransportCachePacking.Pack(
                    SimpleDdgiTransportCacheFormat.Compact24,
                    source,
                    words,
                    out var error), Is.EqualTo(6));
                Assert.That(error.DistanceAbsoluteError,
                    Is.LessThanOrEqualTo(ulp * 0.5f + float.Epsilon),
                    $"exponent={exponent}, offset={offset}");
                Assert.That(TryDecode(
                    SimpleDdgiTransportCacheFormat.Compact24,
                    words,
                    source), Is.True);
            }
        }
    }

    [Test]
    public void ReservedOrImpossibleFlagPayloads_FailClosed()
    {
        var source = CreateSample();
        Span<uint> compact28 = stackalloc uint[7];
        SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Compact28,
            source,
            compact28,
            out _);

        compact28[1] |= 1u << 16;
        Assert.That(TryDecode(
            SimpleDdgiTransportCacheFormat.Compact28,
            compact28,
            source), Is.False);

        SimpleDdgiTransportCachePacking.Pack(
            SimpleDdgiTransportCacheFormat.Compact28,
            source,
            compact28,
            out _);
        compact28[6] &= ~SimpleDdgiTransportCachePacking.HitFlag;
        compact28[6] |= SimpleDdgiTransportCachePacking.BackfaceFlag;
        Assert.That(TryDecode(
            SimpleDdgiTransportCacheFormat.Compact28,
            compact28,
            source), Is.False);
    }

    private static bool TryDecode(
        SimpleDdgiTransportCacheFormat format,
        ReadOnlySpan<uint> words,
        in SimpleDdgiTransportCachePacking.Sample expected) =>
        SimpleDdgiTransportCachePacking.TryUnpack(
            format,
            words,
            0u,
            0u,
            256u,
            expected.ProbeGeneration,
            expected.SourceLightingGeneration,
            expected.SourceEpoch,
            expected.SourceRayCount,
            out _);

    private static SimpleDdgiTransportCachePacking.Sample CreateSample() => new(
        new Vector3(1.0f, 2.0f, 3.0f),
        4.0f,
        Vector3.UnitZ,
        Vector3.UnitY,
        new Vector3(0.5f),
        new Vector3(0.25f),
        1.0f,
        1,
        1,
        2,
        3,
        32);
}
