using Njulf.Assets.Scenes;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SceneDocumentFileIoTests
{
    [Test]
    public void Read_RejectsOversizedDocumentBeforeParsing()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "oversized.njscene");
        try
        {
            using (FileStream stream = File.Create(path))
                stream.SetLength(SceneDocumentJson.MaximumDocumentBytes + 1L);

            Assert.That(
                () => SceneDocumentJson.Read(path),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("exceeding"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void WriteAtomic_OverwritesWithDurableBackupAndLeavesNoTemporaryFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "scene.njscene");
        try
        {
            SceneDocumentJson.WriteAtomic(
                path,
                CreateDocument("before"));
            SceneDocumentJson.WriteAtomic(
                path,
                CreateDocument("after"),
                createBackup: true);

            Assert.Multiple(() =>
            {
                Assert.That(SceneDocumentJson.Read(path).Name, Is.EqualTo("after"));
                Assert.That(
                    SceneDocumentJson.Read(path + ".bak").Name,
                    Is.EqualTo("before"));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp"),
                    Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SerializationPreflightFailure_PreservesPublishedScene()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "scene.njscene");
        try
        {
            SceneDocumentJson.WriteAtomic(
                path,
                CreateDocument("published"));
            SceneDocument invalid = CreateDocument(
                "invalid",
                SceneDocument.CurrentSchemaVersion + 1);

            Assert.That(
                () => SceneDocumentJson.WriteAtomic(path, invalid),
                Throws.TypeOf<InvalidOperationException>());
            Assert.Multiple(() =>
            {
                Assert.That(
                    SceneDocumentJson.Read(path).Name,
                    Is.EqualTo("published"));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp"),
                    Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SceneDocument CreateDocument(
        string name,
        int schemaVersion = SceneDocument.CurrentSchemaVersion) => new()
        {
            SchemaVersion = schemaVersion,
            Id = Guid.NewGuid(),
            Name = name
        };

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-scene-io-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
