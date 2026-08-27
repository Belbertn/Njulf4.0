using System.Linq;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiPipelineCacheFileCodecTests
{
    [Test]
    public void RoundTrip_PreservesOpaquePayload()
    {
        GiPipelineCacheIdentity identity = CreateIdentity();
        byte[] payload = Enumerable.Range(0, 4096)
            .Select(index => unchecked((byte)(index * 31)))
            .ToArray();

        byte[] encoded = GiPipelineCacheFileCodec.Encode(identity, payload);
        bool decoded = GiPipelineCacheFileCodec.TryDecode(
            encoded,
            identity,
            out byte[] restored,
            out bool shaderBundleChanged,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True, reason);
            Assert.That(restored, Is.EqualTo(payload));
            Assert.That(shaderBundleChanged, Is.False);
            Assert.That(encoded.Length,
                Is.EqualTo(GiPipelineCacheFileCodec.HeaderSize + payload.Length));
        });
    }

    [Test]
    public void ShaderMismatch_IsAcceptedAndReportedAsProvenance()
    {
        GiPipelineCacheIdentity identity = CreateIdentity();
        byte[] encoded = GiPipelineCacheFileCodec.Encode(identity, [1, 2, 3]);
        GiPipelineCacheIdentity changedShader = identity with
        {
            ShaderBundleHash = Enumerable.Repeat((byte)0xCC, 32).ToArray()
        };

        bool shaderAccepted = GiPipelineCacheFileCodec.TryDecode(
            encoded,
            changedShader,
            out byte[] shaderPayload,
            out bool shaderBundleChanged,
            out string shaderReason);

        Assert.Multiple(() =>
        {
            Assert.That(shaderAccepted, Is.True, shaderReason);
            Assert.That(shaderPayload, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(shaderBundleChanged, Is.True);
            Assert.That(shaderReason,
                Does.Contain("different shader bundle").IgnoreCase);
        });
    }

    [Test]
    public void VulkanAndEngineIdentityMismatches_RemainHardRejections()
    {
        GiPipelineCacheIdentity identity = CreateIdentity();
        byte[] encoded = GiPipelineCacheFileCodec.Encode(identity, [1, 2, 3]);
        (string Name, GiPipelineCacheIdentity Identity)[] mismatches =
        [
            ("vendor", identity with { VendorId = identity.VendorId + 1 }),
            ("device", identity with { DeviceId = identity.DeviceId + 1 }),
            ("driver", identity with
            {
                DriverVersion = identity.DriverVersion + 1
            }),
            ("API", identity with { ApiVersion = identity.ApiVersion + 1 }),
            ("pipeline cache UUID", identity with
            {
                PipelineCacheUuid = ChangedCopy(identity.PipelineCacheUuid)
            }),
            ("engine ABI", identity with
            {
                EngineAbiHash = ChangedCopy(identity.EngineAbiHash)
            })
        ];

        Assert.Multiple(() =>
        {
            foreach ((string name, GiPipelineCacheIdentity changed) in mismatches)
            {
                bool accepted = GiPipelineCacheFileCodec.TryDecode(
                    encoded,
                    changed,
                    out byte[] payload,
                    out bool shaderBundleChanged,
                    out string reason);

                Assert.That(accepted, Is.False, name);
                Assert.That(payload, Is.Empty, name);
                Assert.That(shaderBundleChanged, Is.False, name);
                Assert.That(reason, Is.Not.Empty, name);
            }
        });
    }

    [Test]
    public void CorruptPayload_IsRejectedByChecksum()
    {
        GiPipelineCacheIdentity identity = CreateIdentity();
        byte[] encoded = GiPipelineCacheFileCodec.Encode(
            identity,
            [10, 20, 30, 40]);
        encoded[^1] ^= 0x80;

        bool accepted = GiPipelineCacheFileCodec.TryDecode(
            encoded,
            identity,
            out byte[] payload,
            out bool shaderBundleChanged,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(payload, Is.Empty);
            Assert.That(shaderBundleChanged, Is.False);
            Assert.That(reason, Does.Contain("checksum").IgnoreCase);
        });
    }

    private static GiPipelineCacheIdentity CreateIdentity() => new(
        VendorId: 0x10DE,
        DeviceId: 0x2684,
        DriverVersion: 1234,
        ApiVersion: 0x00403000,
        PipelineCacheUuid: Enumerable.Range(0, 16)
            .Select(value => (byte)value)
            .ToArray(),
        ShaderBundleHash: Enumerable.Repeat((byte)0x5A, 32).ToArray(),
        EngineAbiHash: Enumerable.Repeat((byte)0xA5, 32).ToArray());

    private static byte[] ChangedCopy(byte[] source)
    {
        byte[] changed = source.ToArray();
        changed[0] ^= 0x80;
        return changed;
    }
}
