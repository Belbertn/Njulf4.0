using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class EnvironmentPayloadCacheTests
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "NjulfEnvironmentPayloadCacheTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Test]
    public async Task RoundTrip_PreservesAllCubemapPayloads()
    {
        EnvironmentPayloadCacheIdentity identity = CreateIdentity();
        EnvironmentPayloadCacheData expected = CreatePayload();
        string path = Path.Combine(
            _temporaryDirectory,
            EnvironmentPayloadCache.GetCacheFileName(identity));

        await EnvironmentPayloadCache.WriteAsync(path, identity, expected);
        EnvironmentPayloadCacheReadResult result =
            await EnvironmentPayloadCache.TryReadAsync(path, identity);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.True, result.Reason);
            Assert.That(
                result.Payload.EnvironmentCubemap,
                Is.EqualTo(expected.EnvironmentCubemap));
            Assert.That(
                result.Payload.IrradianceCubemap,
                Is.EqualTo(expected.IrradianceCubemap));
            Assert.That(
                result.Payload.PrefilteredCubemap,
                Is.EqualTo(expected.PrefilteredCubemap));
        });
    }

    [Test]
    public async Task SourceStampMismatch_IsRejected()
    {
        EnvironmentPayloadCacheIdentity identity = CreateIdentity();
        string path = Path.Combine(_temporaryDirectory, "environment.njenv");
        await EnvironmentPayloadCache.WriteAsync(
            path,
            identity,
            CreatePayload());

        EnvironmentPayloadCacheReadResult result =
            await EnvironmentPayloadCache.TryReadAsync(
                path,
                identity with
                {
                    SourceLastWriteUtcTicks =
                        identity.SourceLastWriteUtcTicks + 1
                });

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.False);
            Assert.That(result.Reason, Does.Contain("identity"));
        });
    }

    [Test]
    public async Task CorruptPayload_IsRejectedByChecksum()
    {
        EnvironmentPayloadCacheIdentity identity = CreateIdentity();
        string path = Path.Combine(_temporaryDirectory, "environment.njenv");
        await EnvironmentPayloadCache.WriteAsync(
            path,
            identity,
            CreatePayload());
        byte[] encoded = await File.ReadAllBytesAsync(path);
        encoded[^1] ^= 0x80;
        await File.WriteAllBytesAsync(path, encoded);

        EnvironmentPayloadCacheReadResult result =
            await EnvironmentPayloadCache.TryReadAsync(path, identity);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.False);
            Assert.That(result.Reason, Does.Contain("checksum"));
        });
    }

    [Test]
    public async Task TruncatedFile_IsRejectedBeforePayloadAllocation()
    {
        EnvironmentPayloadCacheIdentity identity = CreateIdentity();
        string path = Path.Combine(_temporaryDirectory, "environment.njenv");
        await File.WriteAllBytesAsync(path, new byte[32]);

        EnvironmentPayloadCacheReadResult result =
            await EnvironmentPayloadCache.TryReadAsync(path, identity);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.False);
            Assert.That(result.Reason, Does.Contain("file length"));
        });
    }

    private static EnvironmentPayloadCacheIdentity CreateIdentity() => new(
        SourcePath: Path.Combine(Path.GetTempPath(), "environment.hdr"),
        SourceLength: 123_456,
        SourceLastWriteUtcTicks: 638_918_496_000_000_000,
        EnvironmentSize: 2,
        IrradianceSize: 1,
        PrefilteredSize: 2,
        PrefilteredMipCount: 2,
        BytesPerPixel: 8,
        ProcessingVersion:
            EnvironmentPayloadCache.CurrentProcessingVersion);

    private static EnvironmentPayloadCacheData CreatePayload() => new(
        CreateBytes(192, 3),
        CreateBytes(48, 7),
        CreateBytes(240, 11));

    private static byte[] CreateBytes(int length, int multiplier) =>
        Enumerable.Range(0, length)
            .Select(index => unchecked((byte)(index * multiplier)))
            .ToArray();
}
