using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderSettingsFileIoTests
{
    [Test]
    public void Load_RejectsOversizedFileBeforeParsing()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "oversized.json");
        try
        {
            using (FileStream stream = File.Create(path))
                stream.SetLength(RenderSettings.MaximumSettingsFileBytes + 1L);

            Assert.That(
                () => RenderSettings.Load(path),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("valid range"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Save_AtomicallyReplacesSettingsAndLeavesNoTemporaryArtifact()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new RenderSettings
            {
                Exposure = 1.25f
            };
            settings.Save(path);
            settings.Exposure = 2.5f;
            settings.Save(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    RenderSettings.Load(path).Exposure,
                    Is.EqualTo(2.5f));
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

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-render-settings-io-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
