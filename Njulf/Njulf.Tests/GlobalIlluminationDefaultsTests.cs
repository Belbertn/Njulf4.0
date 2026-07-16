using System;
using System.IO;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationDefaultsTests
{
    [Test]
    public void SimpleDdgiHeaderBitPacking_PreservesFullFrameAndFlagWords()
    {
        const uint frameIndex = 0xf1234567u;
        const uint flags = 0xc0debeefu;

        float packedFrameIndex = BitConverter.UInt32BitsToSingle(frameIndex);
        float packedFlags = BitConverter.UInt32BitsToSingle(flags);

        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.SingleToUInt32Bits(packedFrameIndex), Is.EqualTo(frameIndex));
            Assert.That(BitConverter.SingleToUInt32Bits(packedFlags), Is.EqualTo(flags));
        });
    }

    [Test]
    public void NewGlobalIlluminationSettings_SelectSimpleDdgi()
    {
        var settings = new GlobalIlluminationSettings
        {
            Enabled = true,
            Mode = GlobalIlluminationMode.Ddgi,
            UseDdgi = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.DdgiSimpleEnabled, Is.True);
            Assert.That(settings.EffectiveUseSimpleDdgi, Is.True);
            Assert.That(settings.EffectiveUseDdgi, Is.False);
        });
    }

    [TestCase(RenderQualityPreset.Low)]
    [TestCase(RenderQualityPreset.Medium)]
    [TestCase(RenderQualityPreset.High)]
    [TestCase(RenderQualityPreset.Ultra)]
    [TestCase(RenderQualityPreset.DdgiHigh)]
    public void QualityPreset_RestoresSimpleDdgiAsDefault(RenderQualityPreset preset)
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.DdgiSimpleEnabled = false;

        settings.ApplyQualityPreset(preset);

        Assert.That(settings.GlobalIllumination.DdgiSimpleEnabled, Is.True);
        if (settings.GlobalIllumination.Enabled && settings.GlobalIllumination.UseDdgi)
        {
            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.EffectiveUseSimpleDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.False);
            });
        }
    }

    [Test]
    public void SettingsFileWithoutBackendSelector_DefaultsToSimpleDdgi()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"render-settings-simple-ddgi-default-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "QualityPreset": "DdgiHigh",
                  "GlobalIllumination": {
                    "Enabled": true,
                    "Mode": "Ddgi",
                    "UseDdgi": true,
                    "UseRayQueryBackend": true
                  }
                }
                """);

            RenderSettings settings = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.DdgiSimpleEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseSimpleDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.False);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SettingsFileWithExplicitLegacySelector_PreservesOverride()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"render-settings-legacy-ddgi-override-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "QualityPreset": "DdgiHigh",
                  "GlobalIllumination": {
                    "Enabled": true,
                    "Mode": "Ddgi",
                    "UseDdgi": true,
                    "UseRayQueryBackend": true,
                    "DdgiSimpleEnabled": false
                  }
                }
                """);

            RenderSettings settings = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.DdgiSimpleEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseSimpleDdgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
