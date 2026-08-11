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
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True, reason);
            Assert.That(restored, Is.EqualTo(payload));
            Assert.That(encoded.Length,
                Is.EqualTo(GiPipelineCacheFileCodec.HeaderSize + payload.Length));
        });
    }

    [Test]
    public void ShaderOrDriverMismatch_IsRejectedBeforeDriverAdmission()
    {
        GiPipelineCacheIdentity identity = CreateIdentity();
        byte[] encoded = GiPipelineCacheFileCodec.Encode(identity, [1, 2, 3]);
        GiPipelineCacheIdentity changedShader = identity with
        {
            ShaderBundleHash = Enumerable.Repeat((byte)0xCC, 32).ToArray()
        };
        GiPipelineCacheIdentity changedDriver = identity with
        {
            DriverVersion = identity.DriverVersion + 1
        };

        bool shaderAccepted = GiPipelineCacheFileCodec.TryDecode(
            encoded,
            changedShader,
            out byte[] shaderPayload,
            out string shaderReason);
        bool driverAccepted = GiPipelineCacheFileCodec.TryDecode(
            encoded,
            changedDriver,
            out byte[] driverPayload,
            out string driverReason);

        Assert.Multiple(() =>
        {
            Assert.That(shaderAccepted, Is.False);
            Assert.That(shaderPayload, Is.Empty);
            Assert.That(shaderReason, Does.Contain("shader").IgnoreCase);
            Assert.That(driverAccepted, Is.False);
            Assert.That(driverPayload, Is.Empty);
            Assert.That(driverReason, Does.Contain("driver").IgnoreCase);
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
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(payload, Is.Empty);
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
}
