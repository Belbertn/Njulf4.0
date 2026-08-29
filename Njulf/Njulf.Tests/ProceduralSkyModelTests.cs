using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class ProceduralSkyModelTests
{
    private const float Pi = MathF.PI;
    private const float TwoPi = 2.0f * MathF.PI;

    [Test]
    public void ReferenceRgbDataset_HasExpectedDimensionsAndSentinels()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HosekWilkieRgbData.ParamsR, Has.Length.EqualTo(1_080));
            Assert.That(HosekWilkieRgbData.ParamsG, Has.Length.EqualTo(1_080));
            Assert.That(HosekWilkieRgbData.ParamsB, Has.Length.EqualTo(1_080));
            Assert.That(HosekWilkieRgbData.RadiancesR, Has.Length.EqualTo(120));
            Assert.That(HosekWilkieRgbData.RadiancesG, Has.Length.EqualTo(120));
            Assert.That(HosekWilkieRgbData.RadiancesB, Has.Length.EqualTo(120));
            Assert.That(HosekWilkieRgbData.ParamsR[0], Is.EqualTo(-1.099459f).Within(1e-6f));
            Assert.That(HosekWilkieRgbData.ParamsG[540], Is.EqualTo(-1.129907f).Within(1e-6f));
            Assert.That(HosekWilkieRgbData.ParamsB[^1], Is.EqualTo(0.6966285f).Within(1e-6f));
            Assert.That(HosekWilkieRgbData.RadiancesR[0], Is.EqualTo(1.962684f).Within(1e-6f));
            Assert.That(HosekWilkieRgbData.RadiancesG[^1], Is.EqualTo(26.43066f).Within(1e-5f));
            Assert.That(HosekWilkieRgbData.RadiancesB[^1], Is.EqualTo(28.93432f).Within(1e-5f));
        });
    }

    [Test]
    public void ReferenceInterpolation_MatchesKnownRgbCoefficientFrame()
    {
        var settings = CreateDaylightSettings();
        var model = new HosekWilkieSkyModel();
        var frame = new ProceduralAtmosphereFrame();
        Vector3 toSun = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.5f));

        model.UpdateFrame(settings, toSun, authoredSunRadiance: null, frame);

        float[] expectedParameters =
        [
            -1.070171f, -0.17564419f, 1.8383694f,
            3.986803f, -3.6039746f, 1.068095f,
            0.14183913f, 0.28839663f, 0.7053083f,
            -1.0801201f, -0.1823593f, 1.3346455f,
            2.4390585f, -5.005206f, 0.97296935f,
            0.118923016f, 1.7137649f, 0.6843514f,
            -1.0699494f, -0.19482298f, 0.69692755f,
            0.10241258f, -1.826563f, 0.7571999f,
            0.07159887f, 3.03217f, 0.6769342f
        ];
        float[] expectedRadiances = [6.9468775f, 10.695344f, 17.236177f];

        Assert.Multiple(() =>
        {
            for (int index = 0; index < expectedParameters.Length; index++)
            {
                Assert.That(
                    frame.HosekParameters[index],
                    Is.EqualTo(expectedParameters[index]).Within(2e-5f),
                    $"Hosek parameter {index}");
            }
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.That(
                    frame.HosekRadiances[channel],
                    Is.EqualTo(expectedRadiances[channel]).Within(2e-5f),
                    $"Hosek radiance channel {channel}");
            }
        });
    }

    [Test]
    public void StableInputs_DoNotAdvanceAtmosphereContentRevision()
    {
        var settings = CreateDaylightSettings();
        var model = new HosekWilkieSkyModel();
        var frame = new ProceduralAtmosphereFrame();
        Vector3 toSun = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.5f));

        model.UpdateFrame(settings, toSun, authoredSunRadiance: null, frame);
        uint stableRevision = frame.Revision;
        model.UpdateFrame(settings, toSun * 2.0f, authoredSunRadiance: null, frame);
        model.UpdateFrame(settings, toSun, authoredSunRadiance: null, frame);

        settings.Turbidity += 0.25f;
        model.UpdateFrame(settings, toSun, authoredSunRadiance: null, frame);

        Assert.Multiple(() =>
        {
            Assert.That(stableRevision, Is.EqualTo(1U));
            Assert.That(frame.Revision, Is.EqualTo(2U));
            Assert.That(frame.SourceSignature, Is.Not.Zero);
        });
    }

    [Test]
    public void DiffuseSh_ReconstructsIndependentCosineConvolution()
    {
        var settings = CreateDaylightSettings();
        var model = new HosekWilkieSkyModel();
        var frame = new ProceduralAtmosphereFrame();
        model.UpdateFrame(
            settings,
            Vector3.Normalize(new Vector3(-0.4f, 0.55f, 0.73f)),
            authoredSunRadiance: null,
            frame);

        Vector3[] normals =
        [
            Vector3.UnitY,
            Vector3.Normalize(new Vector3(1.0f, 1.0f, 0.0f)),
            Vector3.Normalize(new Vector3(-0.3f, 0.45f, 0.84f))
        ];
        Assert.Multiple(() =>
        {
            foreach (Vector3 normal in normals)
            {
                Vector3 reference = IntegrateDiffuseIrradiance(
                    model,
                    frame,
                    normal,
                    sampleCount: 8_192);
                Vector3 reconstructed =
                    HosekWilkieSkyModel.EvaluateDiffuseIrradianceSh(
                        normal,
                        frame.DiffuseIrradianceSh);
                AssertVectorWithinRelativeTolerance(
                    reconstructed,
                    reference,
                    relativeTolerance: 0.14f,
                    absoluteTolerance: 0.08f,
                    $"normal {normal}");
            }
        });
    }

    [Test]
    public void CelestialDisc_IsExcludedFromDiffuseTransport()
    {
        var settings = CreateDaylightSettings();
        var model = new HosekWilkieSkyModel();
        var frame = new ProceduralAtmosphereFrame();
        Vector3 toSun = Vector3.Normalize(new Vector3(0.2f, 0.8f, 0.56f));
        model.UpdateFrame(settings, toSun, authoredSunRadiance: null, frame);

        Vector3 visual = model.EvaluateSkyRadiance(
            toSun,
            frame,
            ProceduralSkyEvaluationMode.Visual,
            includeCelestialDiscs: true,
            includeStars: false);
        Vector3 diffuse = model.EvaluateSkyRadiance(
            toSun,
            frame,
            ProceduralSkyEvaluationMode.DiffuseTransport,
            includeCelestialDiscs: false,
            includeStars: false);

        Assert.Multiple(() =>
        {
            Assert.That(Luminance(visual), Is.GreaterThan(Luminance(diffuse) * 100.0f));
            AssertFiniteNonNegative(diffuse, "diffuse radiance");
            foreach (Vector3 coefficient in frame.DiffuseIrradianceSh)
                AssertFiniteNonNegativeMagnitude(coefficient, "diffuse SH coefficient");
        });
    }

    [Test]
    public void SunriseToNightSweep_RemainsFiniteAndRetainsNightResidualFloor()
    {
        var settings = CreateDaylightSettings();
        var model = new HosekWilkieSkyModel();
        var frame = new ProceduralAtmosphereFrame();
        float finalNightLuminance = 0.0f;

        for (int elevationDegrees = 80; elevationDegrees >= -24; elevationDegrees -= 4)
        {
            float elevation = elevationDegrees * Pi / 180.0f;
            Vector3 toSun = Vector3.Normalize(new Vector3(
                MathF.Cos(elevation),
                MathF.Sin(elevation),
                0.0f));
            model.UpdateFrame(settings, toSun, authoredSunRadiance: null, frame);
            for (int sample = 0; sample < 128; sample++)
            {
                Vector3 direction = FibonacciSphereDirection(sample, 128);
                Vector3 radiance = model.EvaluateSkyRadiance(
                    direction,
                    frame,
                    ProceduralSkyEvaluationMode.Visual,
                    includeCelestialDiscs: true,
                    includeStars: true);
                AssertFiniteNonNegative(radiance, $"elevation {elevationDegrees}, sample {sample}");
            }

            foreach (Vector3 coefficient in frame.DiffuseIrradianceSh)
                AssertFiniteNonNegativeMagnitude(coefficient, $"SH at {elevationDegrees} degrees");
            if (elevationDegrees == -24)
            {
                finalNightLuminance = Luminance(model.EvaluateSkyRadiance(
                    Vector3.UnitY,
                    frame,
                    ProceduralSkyEvaluationMode.DiffuseTransport,
                    includeCelestialDiscs: false,
                    includeStars: false));
            }
        }

        Assert.That(finalNightLuminance, Is.GreaterThan(0.00001f));
    }

    [Test]
    public void SolarPosition_TracksEquinoxNoonAndEastWestHorizon()
    {
        Vector3 noon = SolarPositionCalculator.CalculateToSunDirection(
            12.0f,
            0.0f,
            80,
            0.0f);
        Vector3 morning = SolarPositionCalculator.CalculateToSunDirection(
            6.0f,
            0.0f,
            80,
            0.0f);
        Vector3 evening = SolarPositionCalculator.CalculateToSunDirection(
            18.0f,
            0.0f,
            80,
            0.0f);

        Assert.Multiple(() =>
        {
            Assert.That(noon.Y, Is.GreaterThan(0.999f));
            Assert.That(morning.X, Is.GreaterThan(0.999f));
            Assert.That(MathF.Abs(morning.Y), Is.LessThan(0.02f));
            Assert.That(evening.X, Is.LessThan(-0.999f));
            Assert.That(MathF.Abs(evening.Y), Is.LessThan(0.02f));
            Assert.That(noon.Length(), Is.EqualTo(1.0f).Within(1e-6f));
        });
    }

    [Test]
    public void GiSunQuantization_IsStableWithinBucketAndChangesAtBoundary()
    {
        Vector3 first = EnvironmentManager.QuantizeGiSunDirection(
            DirectionFromAzimuthElevation(42.05f, 20.05f),
            0.25f);
        Vector3 sameBucket = EnvironmentManager.QuantizeGiSunDirection(
            DirectionFromAzimuthElevation(42.11f, 20.11f),
            0.25f);
        Vector3 nextBucket = EnvironmentManager.QuantizeGiSunDirection(
            DirectionFromAzimuthElevation(42.14f, 20.14f),
            0.25f);

        Assert.Multiple(() =>
        {
            Assert.That(Vector3.Distance(first, sameBucket), Is.LessThan(1e-6f));
            Assert.That(Vector3.Distance(first, nextBucket), Is.GreaterThan(0.003f));
            Assert.That(first.Length(), Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(nextBucket.Length(), Is.EqualTo(1.0f).Within(1e-6f));
        });
    }

    [TestCase(0.0f, 15)]
    [TestCase(8.0f, 480)]
    [TestCase(200.0f, 7_200)]
    [TestCase(float.NaN, 480)]
    public void GiSourceSweepBudget_ConvertsSecondsToBoundedNominalFrames(
        float seconds,
        int expectedFrames)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveGiTargetSourceSweepFrames(seconds),
            Is.EqualTo(expectedFrames));
    }

    [TestCase(8.0f, 30.0f, 240)]
    [TestCase(8.0f, 60.0f, 480)]
    [TestCase(8.0f, 120.0f, 960)]
    [TestCase(8.0f, float.NaN, 480)]
    public void GiSourceSweepBudget_TracksObservedRenderRate(
        float seconds,
        float framesPerSecond,
        int expectedFrames)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveGiTargetSourceSweepFrames(
                seconds,
                framesPerSecond),
            Is.EqualTo(expectedFrames));
    }

    [Test]
    public void ProceduralEnvironmentSettings_ClampEveryRuntimeControl()
    {
        var settings = new EnvironmentSettings
        {
            Turbidity = 99.0f,
            GroundAlbedo = new CoreVector3(-1.0f, 0.4f, 2.0f),
            SunAngularDiameterDegrees = 0.0f,
            MoonAngularDiameterDegrees = 99.0f,
            TimeOfDayHours = 99.0f,
            LatitudeDegrees = -200.0f,
            DayOfYear = 0,
            NorthOffsetDegrees = 999.0f,
            TimeScale = 100_000.0f,
            DirectSunDirection = CoreVector3.Zero,
            AtmosphereIntensity = -1.0f,
            SolarIrradianceScale = 999.0f,
            MoonIrradianceScale = -1.0f,
            StarIntensity = 99.0f,
            AirglowIntensity = -1.0f,
            GiSunStepDegrees = 0.0f,
            GiTargetSourceSweepSeconds = 999.0f,
            SpecularPrefilterMipsPerFrame = 99,
            SpecularPrefilterTransitionFrames = 0
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.Turbidity, Is.EqualTo(10.0f));
            Assert.That(settings.GroundAlbedo.X, Is.EqualTo(0.0f));
            Assert.That(settings.GroundAlbedo.Y, Is.EqualTo(0.4f));
            Assert.That(settings.GroundAlbedo.Z, Is.EqualTo(1.0f));
            // Zero is the exact deterministic hard-sun contract used by the
            // directional ray-shadow sampler.
            Assert.That(settings.SunAngularDiameterDegrees, Is.EqualTo(0.0f));
            Assert.That(settings.MoonAngularDiameterDegrees, Is.EqualTo(2.0f));
            Assert.That(settings.TimeOfDayHours, Is.EqualTo(24.0f));
            Assert.That(settings.LatitudeDegrees, Is.EqualTo(-90.0f));
            Assert.That(settings.DayOfYear, Is.EqualTo(1));
            Assert.That(settings.NorthOffsetDegrees, Is.EqualTo(360.0f));
            Assert.That(settings.TimeScale, Is.EqualTo(86_400.0f));
            Assert.That(settings.DirectSunDirection.Length(), Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(settings.AtmosphereIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.SolarIrradianceScale, Is.EqualTo(128.0f));
            Assert.That(settings.MoonIrradianceScale, Is.EqualTo(0.0f));
            Assert.That(settings.StarIntensity, Is.EqualTo(2.0f));
            Assert.That(settings.AirglowIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.GiSunStepDegrees, Is.EqualTo(0.02f));
            Assert.That(settings.GiTargetSourceSweepSeconds, Is.EqualTo(120.0f));
            Assert.That(settings.SpecularPrefilterMipsPerFrame, Is.EqualTo(5));
            Assert.That(settings.SpecularPrefilterTransitionFrames, Is.EqualTo(1));
        });
    }

    [Test]
    public void ProceduralEnvironmentSettings_RejectNonFiniteRuntimeInputs()
    {
        var settings = new EnvironmentSettings
        {
            Turbidity = float.NaN,
            GroundAlbedo = new CoreVector3(float.NaN, float.PositiveInfinity, 0.4f),
            DirectSunDirection = new CoreVector3(float.NaN, 1.0f, 0.0f),
            AtmosphereIntensity = float.NegativeInfinity,
            SolarIrradianceScale = float.NaN,
            RotationRadians = float.PositiveInfinity
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.Turbidity, Is.EqualTo(1.0f));
            Assert.That(settings.GroundAlbedo, Is.EqualTo(new CoreVector3(0.0f, 0.0f, 0.4f)));
            Assert.That(settings.DirectSunDirection.Length(), Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(settings.AtmosphereIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.SolarIrradianceScale, Is.EqualTo(0.0f));
            Assert.That(settings.RotationRadians, Is.EqualTo(0.0f));
        });
    }

    [Test]
    public void EnvironmentGpuContract_RemainsStd430CompatibleAndUsesDedicatedGiSnapshot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUEnvironmentData>(), Is.EqualTo(480));
            Assert.That(
                BindlessIndex.EnvironmentGiDataBuffer,
                Is.EqualTo(BindlessIndex.EnvironmentPrefilterDataBuffer + 1));
            Assert.That(
                BindlessIndex.SimpleDdgiSchedulerArenaBuffer,
                Is.EqualTo(BindlessIndex.EnvironmentGiDataBuffer + 1));
            Assert.That(
                BindlessIndex.SimpleDdgiReceiverProbeBuffer,
                Is.EqualTo(BindlessIndex.SimpleDdgiSchedulerArenaBuffer + 1));
            Assert.That(
                BindlessIndex.SimpleDdgiResidencyArenaBuffer,
                Is.EqualTo(BindlessIndex.SimpleDdgiReceiverProbeBuffer + 1));
            Assert.That(
                BindlessIndex.SimpleDdgiStorageValidationBufferBase,
                Is.EqualTo(BindlessIndex.SimpleDdgiResidencyArenaBuffer + 1));
            Assert.That(
                BindlessIndex.SimpleDdgiStorageValidationBufferFrame1,
                Is.EqualTo(BindlessIndex.SimpleDdgiStorageValidationBufferBase + 1));
            // Fixed slots are append-only so existing environment/DDGI
            // bindings keep their historical indices while the heap grows at
            // the tail. Impostor view metadata is the current tail slot.
            Assert.That(
                BindlessIndex.StaticBufferCount,
                Is.EqualTo(
                    BindlessIndex.FoliageImpostorViewBuffer + 1));
        });
    }

    [Test]
    public void RuntimeSkyUpdates_DoNotEnterResourceRecreationOrDeviceIdlePath()
    {
        string manager = ReadRepositoryFile(
            "Njulf.Rendering",
            "Resources",
            "EnvironmentManager.cs");
        string updateMethod = SliceMethod(
            manager,
            "public void UpdateFrameLighting(",
            "private void UpdateAtmosphereFrame(");
        string resourceSignature = SliceMethod(
            manager,
            "private ResourceSignature CreateResourceSignature()",
            "private static string? ResolveEnvironmentSourcePath(");

        Assert.Multiple(() =>
        {
            Assert.That(updateMethod, Does.Not.Contain("WaitIdle"));
            Assert.That(updateMethod, Does.Not.Contain("EnsureResourcesCurrent"));
            Assert.That(resourceSignature, Does.Not.Contain("TimeOfDayHours"));
            Assert.That(resourceSignature, Does.Not.Contain("DirectSunDirection"));
            Assert.That(resourceSignature, Does.Not.Contain("Turbidity"));
            Assert.That(manager, Does.Contain("Stepped GI Environment Data Buffer"));
        });
    }

    [Test]
    public void HosekWilkieLicense_IsDistributedWithRepository()
    {
        string notice = ReadRepositoryFile("THIRD-PARTY-NOTICES.txt");
        Assert.Multiple(() =>
        {
            Assert.That(notice, Does.Contain("Hosek-Wilkie"));
            Assert.That(notice, Does.Contain("Copyright (c) 2012 - 2013, Lukas Hosek and Alexander Wilkie"));
            Assert.That(notice, Does.Contain("Redistribution and use in source and binary forms"));
        });
    }

    private static EnvironmentSettings CreateDaylightSettings() => new()
    {
        Turbidity = 3.0f,
        GroundAlbedo = new CoreVector3(0.2f, 0.2f, 0.2f),
        AtmosphereIntensity = 1.0f,
        SolarIrradianceScale = 14.0f,
        MoonIrradianceScale = 0.12f,
        StarIntensity = 0.025f,
        AirglowIntensity = 0.025f,
        SkyIntensity = 1.0f
    };

    private static Vector3 IntegrateDiffuseIrradiance(
        HosekWilkieSkyModel model,
        ProceduralAtmosphereFrame frame,
        Vector3 normal,
        int sampleCount)
    {
        Vector3 sum = Vector3.Zero;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            Vector3 direction = FibonacciSphereDirection(sample, sampleCount);
            float cosine = MathF.Max(Vector3.Dot(normal, direction), 0.0f);
            if (cosine <= 0.0f)
                continue;
            Vector3 radiance = model.EvaluateSkyRadiance(
                direction,
                frame,
                ProceduralSkyEvaluationMode.DiffuseTransport,
                includeCelestialDiscs: false,
                includeStars: false);
            sum += radiance * cosine;
        }
        return sum * (4.0f * Pi / sampleCount);
    }

    private static Vector3 FibonacciSphereDirection(int sample, int count)
    {
        float y = 1.0f - 2.0f * (sample + 0.5f) / count;
        float radius = MathF.Sqrt(MathF.Max(1.0f - y * y, 0.0f));
        float phi = TwoPi * RadicalInverseVdc((uint)sample);
        return new Vector3(radius * MathF.Cos(phi), y, radius * MathF.Sin(phi));
    }

    private static float RadicalInverseVdc(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    private static Vector3 DirectionFromAzimuthElevation(
        float azimuthDegrees,
        float elevationDegrees)
    {
        float azimuth = azimuthDegrees * Pi / 180.0f;
        float elevation = elevationDegrees * Pi / 180.0f;
        float horizontal = MathF.Cos(elevation);
        return Vector3.Normalize(new Vector3(
            MathF.Sin(azimuth) * horizontal,
            MathF.Sin(elevation),
            MathF.Cos(azimuth) * horizontal));
    }

    private static float Luminance(Vector3 value) =>
        Vector3.Dot(value, new Vector3(0.2126f, 0.7152f, 0.0722f));

    private static void AssertVectorWithinRelativeTolerance(
        Vector3 actual,
        Vector3 expected,
        float relativeTolerance,
        float absoluteTolerance,
        string message)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            float expectedChannel = channel == 0 ? expected.X : channel == 1 ? expected.Y : expected.Z;
            float actualChannel = channel == 0 ? actual.X : channel == 1 ? actual.Y : actual.Z;
            float tolerance = MathF.Max(
                absoluteTolerance,
                MathF.Abs(expectedChannel) * relativeTolerance);
            Assert.That(
                actualChannel,
                Is.EqualTo(expectedChannel).Within(tolerance),
                $"{message}, channel {channel}");
        }
    }

    private static void AssertFiniteNonNegative(Vector3 value, string message)
    {
        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(value.X), Is.True, $"{message} X finite");
            Assert.That(float.IsFinite(value.Y), Is.True, $"{message} Y finite");
            Assert.That(float.IsFinite(value.Z), Is.True, $"{message} Z finite");
            Assert.That(value.X, Is.GreaterThanOrEqualTo(0.0f), $"{message} X non-negative");
            Assert.That(value.Y, Is.GreaterThanOrEqualTo(0.0f), $"{message} Y non-negative");
            Assert.That(value.Z, Is.GreaterThanOrEqualTo(0.0f), $"{message} Z non-negative");
        });
    }

    private static void AssertFiniteNonNegativeMagnitude(Vector3 value, string message)
    {
        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(value.X), Is.True, $"{message} X finite");
            Assert.That(float.IsFinite(value.Y), Is.True, $"{message} Y finite");
            Assert.That(float.IsFinite(value.Z), Is.True, $"{message} Z finite");
            Assert.That(float.IsFinite(value.LengthSquared()), Is.True, $"{message} magnitude finite");
        });
    }

    private static string SliceMethod(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), startMarker);
        Assert.That(end, Is.GreaterThan(start), endMarker);
        return source[start..end];
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not find repository file '{Path.Combine(relativeParts)}'.");
    }
}
