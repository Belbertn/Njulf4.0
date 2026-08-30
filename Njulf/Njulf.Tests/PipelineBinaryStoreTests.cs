using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PipelineBinaryStoreTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "Njulf.Tests",
            nameof(PipelineBinaryStoreTests),
            Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void SaveAndLoad_RoundTripsOrderedBinaryBlobs()
    {
        PipelineBinaryStore store = CreateStore();
        byte[] pipelineKey = [1, 2, 3, 4];
        PipelineBinaryBlob[] expected =
        [
            new([0x10, 0x11], [1, 3, 5, 7]),
            new([0x20, 0x21], [2, 4, 6, 8])
        ];

        store.Save(
            new PipelineArtifactId("mesh.forward"),
            pipelineKey,
            expected);
        bool loaded = store.TryLoad(pipelineKey, out var lookup);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.True);
            Assert.That(
                lookup.Source,
                Is.EqualTo(PipelineArtifactSource.WritableBinary));
            Assert.That(lookup.Binaries, Has.Count.EqualTo(2));
            Assert.That(lookup.Binaries[0].Key, Is.EqualTo(expected[0].Key));
            Assert.That(lookup.Binaries[0].Data, Is.EqualTo(expected[0].Data));
            Assert.That(lookup.Binaries[1].Key, Is.EqualTo(expected[1].Key));
            Assert.That(lookup.Binaries[1].Data, Is.EqualTo(expected[1].Data));
        });
    }

    [Test]
    public void SeparateWriters_MergeManifestEntriesUnderStoreLock()
    {
        PipelineBinaryStore first = CreateStore();
        PipelineBinaryStore second = CreateStore();
        byte[] firstPipelineKey = [1];
        byte[] secondPipelineKey = [2];

        first.Save(
            new PipelineArtifactId("first"),
            firstPipelineKey,
            [new PipelineBinaryBlob([0x31], [1, 1, 1])]);
        second.Save(
            new PipelineArtifactId("second"),
            secondPipelineKey,
            [new PipelineBinaryBlob([0x32], [2, 2, 2])]);

        PipelineBinaryStore verifier = CreateStore();
        Assert.Multiple(() =>
        {
            Assert.That(verifier.TryLoad(firstPipelineKey, out _), Is.True);
            Assert.That(verifier.TryLoad(secondPipelineKey, out _), Is.True);
        });
    }

    [Test]
    public void CorruptBlob_IsRejectedWithoutTouchingSeedOrThrowing()
    {
        PipelineBinaryStore store = CreateStore();
        byte[] pipelineKey = [9, 8, 7];
        byte[] binaryKey = [0xAA, 0xBB];
        store.Save(
            new PipelineArtifactId("corrupt"),
            pipelineKey,
            [new PipelineBinaryBlob(binaryKey, [1, 2, 3, 4])]);
        string blobPath = Path.Combine(
            store.WritableRoot,
            "blobs",
            Convert.ToHexString(binaryKey) + ".bin");
        File.WriteAllBytes(blobPath, [4, 3, 2, 1]);

        Assert.That(store.TryLoad(pipelineKey, out _), Is.False);
    }

    [Test]
    public void ReadOnlySeed_IsUsedWhenWritableEntryIsAbsent()
    {
        string writable = Path.Combine(_root, "writable");
        string seed = Path.Combine(_root, "seed");
        PipelineBinaryStoreIdentity identity = CreateIdentity();
        byte[] pipelineKey = [4, 5, 6];
        var seedWriter = new PipelineBinaryStore(
            identity,
            writableRoot: seed,
            seedRoot: Path.Combine(_root, "unused"));
        seedWriter.Save(
            new PipelineArtifactId("seeded"),
            pipelineKey,
            [new PipelineBinaryBlob([0x44], [8, 6, 4, 2])]);
        var reader = new PipelineBinaryStore(identity, writable, seed);

        bool loaded = reader.TryLoad(pipelineKey, out var lookup);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.True);
            Assert.That(
                lookup.Source,
                Is.EqualTo(PipelineArtifactSource.SeedBinary));
            Assert.That(lookup.Binaries[0].Data,
                Is.EqualTo(new byte[] { 8, 6, 4, 2 }));
        });
    }

    [Test]
    public void IncompatibleIdentity_DoesNotReuseManifest()
    {
        byte[] pipelineKey = [0x50];
        CreateStore().Save(
            new PipelineArtifactId("identity"),
            pipelineKey,
            [new PipelineBinaryBlob([0x51], [5, 1])]);
        PipelineBinaryStoreIdentity changed = CreateIdentity() with
        {
            DriverVersion = 999
        };
        var reader = new PipelineBinaryStore(
            changed,
            Path.Combine(_root, "writable"),
            Path.Combine(_root, "seed"));

        Assert.That(reader.TryLoad(pipelineKey, out _), Is.False);
    }

    [Test]
    public void ShaderAndBuildRevision_ReusesMatchingPipelineKey()
    {
        byte[] pipelineKey = [0x52];
        CreateStore().Save(
            new PipelineArtifactId("revision-compatible"),
            pipelineKey,
            [new PipelineBinaryBlob([0x53], [5, 3, 1])]);
        PipelineBinaryStoreIdentity changed = CreateIdentity() with
        {
            ShaderBundleHash = new string('D', 64),
            BuildConfigurationHash = new string('E', 64)
        };
        var reader = new PipelineBinaryStore(
            changed,
            Path.Combine(_root, "writable"),
            Path.Combine(_root, "seed"));

        Assert.Multiple(() =>
        {
            Assert.That(reader.TryLoad(pipelineKey, out var lookup), Is.True);
            Assert.That(lookup.Binaries[0].Data,
                Is.EqualTo(new byte[] { 5, 3, 1 }));
            Assert.That(reader.TryLoad([0x54], out _), Is.False);
        });
    }

    [Test]
    public void Save_RejectsBinaryKeysBeyondVulkanLimit()
    {
        PipelineBinaryStore store = CreateStore();
        var oversizedKey = new byte[33];

        Assert.That(
            () => store.Save(
                new PipelineArtifactId("oversized-key"),
                [0x61],
                [new PipelineBinaryBlob(oversizedKey, [1])]),
            Throws.ArgumentException);
        Assert.That(Directory.Exists(store.WritableRoot), Is.False);
    }

    private PipelineBinaryStore CreateStore() => new(
        CreateIdentity(),
        Path.Combine(_root, "writable"),
        Path.Combine(_root, "seed"));

    private static PipelineBinaryStoreIdentity CreateIdentity() => new(
        VendorId: 0x10DE,
        DeviceId: 0x2684,
        DriverVersion: 123,
        ApiVersion: 0x00403000,
        GlobalKey: "00112233445566778899AABBCCDDEEFF",
        ShaderBundleHash: new string('A', 64),
        EngineAbiHash: new string('B', 64),
        BuildConfigurationHash: new string('C', 64));
}
