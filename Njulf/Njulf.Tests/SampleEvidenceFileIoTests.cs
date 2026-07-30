using System.Security.Cryptography;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleEvidenceFileIoTests
{
    [Test]
    public void Read_HashesTheExactBoundedBytesReturnedToTheParser()
    {
        string path = TemporaryPath();
        byte[] expected = [1, 3, 3, 7, 42];
        try
        {
            File.WriteAllBytes(path, expected);

            SampleEvidenceFileContent content =
                SampleEvidenceFileIo.Read(path, expected.Length, "test evidence");

            Assert.Multiple(() =>
            {
                Assert.That(content.Bytes, Is.EqualTo(expected));
                Assert.That(
                    content.Sha256,
                    Is.EqualTo(
                        Convert.ToHexString(SHA256.HashData(expected))
                            .ToLowerInvariant()));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void Read_RejectsEmptyAndOversizedEvidenceBeforePayloadAllocation()
    {
        string emptyPath = TemporaryPath();
        string oversizedPath = TemporaryPath();
        try
        {
            File.WriteAllBytes(emptyPath, []);
            using (var output = new FileStream(
                       oversizedPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                output.SetLength(1025);
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    () => SampleEvidenceFileIo.Read(
                        emptyPath,
                        1024,
                        "empty evidence"),
                    Throws.TypeOf<InvalidDataException>()
                        .With.Message.Contains("is empty"));
                Assert.That(
                    () => SampleEvidenceFileIo.Read(
                        oversizedPath,
                        1024,
                        "oversized evidence"),
                    Throws.TypeOf<InvalidDataException>()
                        .With.Message.Contains("bounded limit is 1024 bytes"));
            });
        }
        finally
        {
            if (File.Exists(emptyPath))
                File.Delete(emptyPath);
            if (File.Exists(oversizedPath))
                File.Delete(oversizedPath);
        }
    }

    [Test]
    public void WriteAtomic_CommitsAndVerifiesExactBytesWithoutTempLeak()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sample-evidence-write-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "report.json");
        byte[] first = """{"status":"first"}"""u8.ToArray();
        byte[] second = """{"status":"passed"}"""u8.ToArray();
        try
        {
            SampleEvidenceFileIo.WriteAtomic(
                path,
                first,
                1024,
                "test report");
            SampleEvidenceFileContent published =
                SampleEvidenceFileIo.WriteAtomic(
                    path,
                    second,
                    1024,
                    "test report");

            Assert.Multiple(() =>
            {
                Assert.That(published.Path, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(published.Bytes, Is.EqualTo(second));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(second));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp").ToArray(),
                    Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void WriteAtomic_RejectsOversizedPayloadWithoutReplacingPublishedFile()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sample-evidence-bound-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "report.json");
        Directory.CreateDirectory(directory);
        byte[] sentinel = """{"status":"previous"}"""u8.ToArray();
        File.WriteAllBytes(path, sentinel);
        try
        {
            Assert.That(
                () => SampleEvidenceFileIo.WriteAtomic(
                    path,
                    new byte[17],
                    16,
                    "bounded report"),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(sentinel));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ValidateStrictJson_RejectsRecursiveDuplicateProperties()
    {
        byte[] ambiguous =
            """
            {
              "producer": {
                "status": "passed",
                "status": "failed"
              }
            }
            """u8.ToArray();

        Assert.That(
            () => SampleEvidenceFileIo.ValidateStrictJson(
                ambiguous,
                maximumDepth: 32,
                role: "test evidence"),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("duplicate JSON property"));
    }

    private static string TemporaryPath() =>
        Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sample-evidence-{Guid.NewGuid():N}.bin");
}
