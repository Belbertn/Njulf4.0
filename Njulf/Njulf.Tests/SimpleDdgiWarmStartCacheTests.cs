using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiWarmStartCacheTests
{
    [Test]
    public void CodecRoundTripPreservesExactIdentityAndPayload()
    {
        SimpleDdgiWarmStartArchive expected = CreateArchive(seed: 17);

        byte[] encoded = SimpleDdgiWarmStartFileCodec.Encode(expected);
        bool accepted = SimpleDdgiWarmStartFileCodec.TryDecode(
            encoded,
            expected.Identity,
            out SimpleDdgiWarmStartArchive? actual,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, reason);
            Assert.That(actual, Is.Not.Null);
            Assert.That(
                actual!.Identity.IsCompatibleWith(expected.Identity),
                Is.True);
            Assert.That(actual.Volumes.Count, Is.EqualTo(1));
            Assert.That(actual.ProbeCount, Is.EqualTo(2));
        });
        AssertVolumeEqual(expected.Volumes[0], actual!.Volumes[0]);
    }

    [Test]
    public void CodecRejectsEveryProducerIdentityMismatch()
    {
        SimpleDdgiWarmStartArchive archive = CreateArchive(seed: 9);
        byte[] encoded = SimpleDdgiWarmStartFileCodec.Encode(archive);
        SimpleDdgiWarmStartIdentity incompatible = CreateIdentity(seed: 10);

        bool accepted = SimpleDdgiWarmStartFileCodec.TryDecode(
            encoded,
            incompatible,
            out SimpleDdgiWarmStartArchive? decoded,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(decoded, Is.Null);
            Assert.That(reason, Does.Contain("identity"));
        });
    }

    [Test]
    public void CodecRejectsTruncationCorruptionAndHostileCardinality()
    {
        SimpleDdgiWarmStartArchive archive = CreateArchive(seed: 31);
        byte[] encoded = SimpleDdgiWarmStartFileCodec.Encode(archive);
        byte[] truncated = encoded[..^1];
        byte[] corrupted = (byte[])encoded.Clone();
        corrupted[^1] ^= 0x5a;
        byte[] hostile = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            hostile.AsSpan(24, sizeof(uint)),
            SimpleDdgiWarmStartFileCodec.MaximumProbeCount + 1u);

        Assert.Multiple(() =>
        {
            Assert.That(TryDecode(truncated, archive.Identity), Is.False);
            Assert.That(TryDecode(corrupted, archive.Identity), Is.False);
            Assert.That(TryDecode(hostile, archive.Identity), Is.False);
        });
    }

    [Test]
    public void StoreUsesIdentityKeyedAtomicReplacementAndFailsClosed()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"simple-ddgi-warm-cache-{Guid.NewGuid():N}");
        try
        {
            var store = new SimpleDdgiWarmStartCacheStore(directory);
            SimpleDdgiWarmStartArchive archive = CreateArchive(seed: 77);

            SimpleDdgiWarmStartSaveResult save = store.Save(archive);
            SimpleDdgiWarmStartLoadResult load = store.Load(archive.Identity);

            Assert.Multiple(() =>
            {
                Assert.That(save.Saved, Is.True, save.Status);
                Assert.That(load.Found, Is.True);
                Assert.That(load.Accepted, Is.True, load.Status);
                Assert.That(load.Archive, Is.Not.Null);
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
                Assert.That(store.GetPath(archive.Identity), Is.EqualTo(save.Path));
            });

            File.WriteAllBytes(save.Path, new byte[32]);
            SimpleDdgiWarmStartLoadResult corrupt = store.Load(archive.Identity);
            Assert.Multiple(() =>
            {
                Assert.That(corrupt.Found, Is.True);
                Assert.That(corrupt.Accepted, Is.False);
                Assert.That(corrupt.Archive, Is.Null);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ToroidalMappingIsBijectiveAndReceiverAddressIsRemapped()
    {
        int[] mapped = new int[12];
        int cursor = 0;
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 3; x++)
        {
            mapped[cursor++] =
                SimpleDdgiVolumeManager
                    .CalculatePersistentWarmStartPhysicalIndex(
                        x, y, z,
                        3, 2, 2,
                        1, 1, 0);
        }

        byte[] receiver = Enumerable.Range(0, 16)
            .Select(static value => checked((byte)value))
            .ToArray();
        SimpleDdgiVolumeManager
            .RewritePersistentWarmStartReceiverAtlasAddress(
                receiver,
                0xfedcba98u);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Distinct().Count(), Is.EqualTo(mapped.Length));
            Assert.That(mapped, Has.All.InRange(0, mapped.Length - 1));
            Assert.That(mapped[0], Is.EqualTo(4));
            Assert.That(
                BinaryPrimitives.ReadUInt32LittleEndian(receiver.AsSpan(12)),
                Is.EqualTo(0xfedcba98u));
            Assert.That(receiver[..12], Is.EqualTo(
                Enumerable.Range(0, 12)
                    .Select(static value => checked((byte)value))
                    .ToArray()));
            Assert.That(
                SimpleDdgiVolumeManager.TryResolvePersistentWarmStartCellDelta(
                    -23.531062f,
                    -26.668537f,
                    3.137475f,
                    out int cellDelta),
                Is.True);
            Assert.That(cellDelta, Is.EqualTo(1));
            Assert.That(
                SimpleDdgiVolumeManager.TryResolvePersistentWarmStartCellDelta(
                    -23.0f,
                    -26.668537f,
                    3.137475f,
                    out _),
                Is.False);
        });
    }

    [Test]
    public void WarmPrior_IsSingleShotPerPhysicalOwnershipAndNeverRearmsOnScroll()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ShouldApplyPersistentWarmStartPrior(
                    archiveReady: true,
                    initialLiveWorkStarted: false,
                    appliedPhysicalOwnershipGeneration: 0u,
                    currentPhysicalOwnershipGeneration: 7u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldApplyPersistentWarmStartPrior(
                    archiveReady: true,
                    initialLiveWorkStarted: false,
                    appliedPhysicalOwnershipGeneration: 7u,
                    currentPhysicalOwnershipGeneration: 7u),
                Is.False,
                "A logical toroidal scroll retains physical ownership.");
            Assert.That(
                SimpleDdgiVolumeManager.ShouldApplyPersistentWarmStartPrior(
                    archiveReady: true,
                    initialLiveWorkStarted: true,
                    appliedPhysicalOwnershipGeneration: 0u,
                    currentPhysicalOwnershipGeneration: 8u),
                Is.False,
                "A late archive must never overwrite live probe transactions.");
        });
    }

    private static bool TryDecode(
        byte[] encoded,
        SimpleDdgiWarmStartIdentity identity) =>
        SimpleDdgiWarmStartFileCodec.TryDecode(
            encoded,
            identity,
            out _,
            out _);

    private static SimpleDdgiWarmStartArchive CreateArchive(byte seed)
    {
        const int probes = 2;
        return new SimpleDdgiWarmStartArchive(
            CreateIdentity(seed),
            new[]
            {
                new SimpleDdgiWarmStartVolumeData(
                    10_001,
                    1,
                    BitConverter.SingleToUInt32Bits(2.5f),
                    BitConverter.SingleToUInt32Bits(-4.25f),
                    BitConverter.SingleToUInt32Bits(7.125f),
                    BitConverter.SingleToUInt32Bits(12.75f),
                    2,
                    1,
                    1,
                    1,
                    0,
                    0,
                    CreateBytes(probes * 512, seed),
                    CreateBytes(probes * 1_024, unchecked((byte)(seed + 1))),
                    CreateBytes(probes * 16, unchecked((byte)(seed + 2))))
            });
    }

    private static SimpleDdgiWarmStartIdentity CreateIdentity(byte seed)
    {
        byte[][] components = Enumerable.Range(0, 8)
            .Select(index => CreateBytes(
                SimpleDdgiWarmStartIdentity.ComponentHashBytes,
                unchecked((byte)(seed + index * 13))))
            .ToArray();
        return new SimpleDdgiWarmStartIdentity(
            components[0], components[1], components[2], components[3],
            components[4], components[5], components[6], components[7]);
    }

    private static byte[] CreateBytes(int count, byte seed)
    {
        var bytes = new byte[count];
        for (int index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(seed + index * 37));
        return bytes;
    }

    private static void AssertVolumeEqual(
        SimpleDdgiWarmStartVolumeData expected,
        SimpleDdgiWarmStartVolumeData actual)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.SourceOrdinal, Is.EqualTo(expected.SourceOrdinal));
            Assert.That(actual.Kind, Is.EqualTo(expected.Kind));
            Assert.That(actual.SpacingBits, Is.EqualTo(expected.SpacingBits));
            Assert.That(actual.OriginXBits, Is.EqualTo(expected.OriginXBits));
            Assert.That(actual.OriginYBits, Is.EqualTo(expected.OriginYBits));
            Assert.That(actual.OriginZBits, Is.EqualTo(expected.OriginZBits));
            Assert.That(actual.CountX, Is.EqualTo(expected.CountX));
            Assert.That(actual.CountY, Is.EqualTo(expected.CountY));
            Assert.That(actual.CountZ, Is.EqualTo(expected.CountZ));
            Assert.That(actual.PhysicalOffsetX, Is.EqualTo(expected.PhysicalOffsetX));
            Assert.That(actual.PhysicalOffsetY, Is.EqualTo(expected.PhysicalOffsetY));
            Assert.That(actual.PhysicalOffsetZ, Is.EqualTo(expected.PhysicalOffsetZ));
            Assert.That(actual.Irradiance, Is.EqualTo(expected.Irradiance));
            Assert.That(actual.Visibility, Is.EqualTo(expected.Visibility));
            Assert.That(actual.ReceiverProbes, Is.EqualTo(expected.ReceiverProbes));
        });
    }
}
