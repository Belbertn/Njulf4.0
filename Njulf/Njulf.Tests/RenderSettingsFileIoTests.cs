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

    [Test]
    public void SaveLoad_PreservesIndependentLayeredReceiverGiPolicies()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "layered-gi.json");
        try
        {
            var settings = new RenderSettings();
            settings.Transparency.ReceiveGlobalIllumination = false;
            settings.Decals.ReceiveGlobalIllumination = true;

            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.Transparency.ReceiveGlobalIllumination,
                    Is.False);
                Assert.That(
                    loaded.Decals.ReceiveGlobalIllumination,
                    Is.True);
                Assert.That(
                    File.ReadAllText(path),
                    Does.Contain($"\"Version\": {RenderSettings.SerializationVersion}"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void QualityPresets_EnableLayeredDdgiOnlyWhenDdgiIsAvailable()
    {
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(RenderQualityPreset.Low);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.False);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.False);

        settings.ApplyQualityPreset(RenderQualityPreset.Medium);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.False);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.False);

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.True);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.True);

        settings.ApplyQualityPreset(RenderQualityPreset.Ultra);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.True);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.True);
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
