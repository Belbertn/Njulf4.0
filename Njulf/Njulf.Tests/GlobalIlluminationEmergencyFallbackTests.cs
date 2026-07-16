using System;
using System.IO;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationEmergencyFallbackTests
{
    [Test]
    public void EmergencyFallback_GatesDynamicGiWithoutMutatingEnvironmentOrReflections()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.RayQueryHybrid;
        gi.UseSsgi = true;
        gi.UseDdgi = true;
        gi.DdgiSimpleEnabled = true;
        gi.UseRayQueryBackend = true;
        gi.IndirectIntensity = 1.25f;
        gi.EnvironmentFallbackIntensity = 0.75f;
        settings.Environment.Enabled = true;
        settings.Environment.DiffuseIntensity = 1.75f;
        settings.Reflections.Enabled = true;
        settings.Reflections.GlobalFallbackIntensity = 1.5f;

        Assert.Multiple(() =>
        {
            Assert.That(gi.EmergencyGiFallbackEnabled, Is.False);
            Assert.That(gi.EffectiveUseSsgi, Is.True);
            Assert.That(gi.EffectiveUseSimpleDdgi, Is.True);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.True);
        });

        gi.EmergencyGiFallbackEnabled = true;

        Assert.Multiple(() =>
        {
            Assert.That(gi.EffectiveUseSsgi, Is.False);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.EffectiveUseSimpleDdgi, Is.False);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.False);
            Assert.That(gi.Enabled, Is.True);
            Assert.That(gi.Mode, Is.EqualTo(GlobalIlluminationMode.RayQueryHybrid));
            Assert.That(gi.UseSsgi, Is.True);
            Assert.That(gi.UseDdgi, Is.True);
            Assert.That(gi.DdgiSimpleEnabled, Is.True);
            Assert.That(gi.UseRayQueryBackend, Is.True);
            Assert.That(gi.IndirectIntensity, Is.EqualTo(1.25f));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(0.75f));
            Assert.That(settings.Environment.Enabled, Is.True);
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(1.75f));
            Assert.That(settings.Reflections.Enabled, Is.True);
            Assert.That(settings.Reflections.GlobalFallbackIntensity, Is.EqualTo(1.5f));
        });

        gi.EmergencyGiFallbackEnabled = false;

        Assert.Multiple(() =>
        {
            Assert.That(gi.EffectiveUseSsgi, Is.True);
            Assert.That(gi.EffectiveUseSimpleDdgi, Is.True);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.True);
        });

        // Verify that the same switch also gates the legacy DDGI backend,
        // rather than relying solely on the Simple-DDGI selection path.
        gi.DdgiSimpleEnabled = false;
        Assert.That(gi.EffectiveUseDdgi, Is.True);

        gi.EmergencyGiFallbackEnabled = true;

        Assert.Multiple(() =>
        {
            Assert.That(gi.EffectiveUseSsgi, Is.False);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.EffectiveUseSimpleDdgi, Is.False);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.False);
        });

        // Graphics presets must not silently re-enable a path that an operator
        // explicitly put into emergency fallback.
        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        Assert.Multiple(() =>
        {
            Assert.That(gi.EmergencyGiFallbackEnabled, Is.True);
            Assert.That(gi.EffectiveUseSsgi, Is.False);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.EffectiveUseSimpleDdgi, Is.False);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.False);
        });
    }

    [Test]
    public void EmergencyFallback_PersistsAcrossSettingsSnapshotsAndKeepsLoadClampsIdempotent()
    {
        string inputPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"emergency-gi-fallback-input-{Guid.NewGuid():N}.json");
        string roundTripPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"emergency-gi-fallback-roundtrip-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(inputPath, """
                {
                  "Version": 4,
                  "QualityPreset": "DdgiHigh",
                  "GlobalIllumination": {
                    "Enabled": true,
                    "Mode": "RayQueryHybrid",
                    "EmergencyGiFallbackEnabled": true,
                    "IndirectIntensity": 99.0,
                    "EnvironmentFallbackIntensity": -5.0,
                    "UseSsgi": true,
                    "UseDdgi": true,
                    "DdgiSimpleEnabled": false,
                    "UseRayQueryBackend": true
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(inputPath);
            loaded.Save(roundTripPath);
            string snapshot = File.ReadAllText(roundTripPath);
            RenderSettings reloaded = RenderSettings.Load(roundTripPath);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot, Does.Contain("\"EmergencyGiFallbackEnabled\": true"));
                Assert.That(loaded.GlobalIllumination.EmergencyGiFallbackEnabled, Is.True);
                Assert.That(reloaded.GlobalIllumination.EmergencyGiFallbackEnabled, Is.True);
                Assert.That(loaded.GlobalIllumination.IndirectIntensity, Is.EqualTo(8.0f));
                Assert.That(reloaded.GlobalIllumination.IndirectIntensity, Is.EqualTo(8.0f));
                Assert.That(loaded.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(0.0f));
                Assert.That(reloaded.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(0.0f));
                Assert.That(reloaded.GlobalIllumination.EffectiveUseSsgi, Is.False);
                Assert.That(reloaded.GlobalIllumination.EffectiveUseDdgi, Is.False);
                Assert.That(reloaded.GlobalIllumination.EffectiveUseSimpleDdgi, Is.False);
                Assert.That(reloaded.GlobalIllumination.EffectiveUseRayQueryBackend, Is.False);
            });
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (File.Exists(roundTripPath))
                File.Delete(roundTripPath);
        }
    }
}
