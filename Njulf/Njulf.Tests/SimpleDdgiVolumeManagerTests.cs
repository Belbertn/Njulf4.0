using Njulf.Rendering.Resources;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiVolumeManagerTests
{
    [Test]
    public void DirtyLatencyPercentiles_AreDeterministicAndSaturateTheFinalBucket()
    {
        uint[] histogram = new uint[16];
        histogram[0] = 10;
        histogram[1] = 5;
        histogram[8] = 5;
        histogram[15] = 1;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 0, 0.95f), Is.EqualTo(0));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 21, 0.50f), Is.EqualTo(1));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 21, 0.95f), Is.EqualTo(8));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 21, 1.0f), Is.EqualTo(15));
        });
    }

    [Test]
    public void BufferResizes_DeferOldGpuBuffersUntilFramesInFlightComplete()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("BeginFrameResourceRetirement();"));
            Assert.That(source, Does.Contain("RetireBufferResource(handle);"));
            Assert.That(source, Does.Contain("RenderingConstants.FramesInFlight + 1UL"));
            Assert.That(source, Does.Not.Contain("_bufferManager.DestroyBuffer(handle);"));
        });
    }

    [Test]
    public void UpdateQuotas_ConsumeConfiguredBudgetBeyondPreferredRingMaximums()
    {
        int[] quotas = new int[3];
        int[] minimums = [512, 96, 24];
        int[] preferredMaximums = [1_024, 324, 128];
        int[] capacities = [10_976, 3_240, 1_152];
        int[] weights = [6, 3, 1];

        SimpleDdgiVolumeManager.AllocateUpdateQuotas(
            quotas,
            minimums,
            preferredMaximums,
            capacities,
            weights,
            updateBudget: 2_048);

        Assert.Multiple(() =>
        {
            Assert.That(quotas.Sum(), Is.EqualTo(2_048));
            Assert.That(quotas, Is.All.GreaterThanOrEqualTo(0));
            Assert.That(quotas[0], Is.GreaterThan(preferredMaximums[0]));
            Assert.That(quotas[1], Is.GreaterThan(preferredMaximums[1]));
            Assert.That(quotas[2], Is.GreaterThan(preferredMaximums[2]));
        });
    }

    [Test]
    public void PerRingGridSelection_UsesExplicitNearMidAndFarSettings()
    {
        var settings = new GlobalIlluminationSettings
        {
            SimpleDdgiNearRingGridSizeX = 28,
            SimpleDdgiNearRingGridSizeY = 14,
            SimpleDdgiNearRingGridSizeZ = 28,
            SimpleDdgiMidRingGridSizeX = 18,
            SimpleDdgiMidRingGridSizeY = 10,
            SimpleDdgiMidRingGridSizeZ = 18,
            SimpleDdgiFarRingGridSizeX = 12,
            SimpleDdgiFarRingGridSizeY = 8,
            SimpleDdgiFarRingGridSizeZ = 12
        };

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 0), Is.EqualTo((28, 14, 28)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 1), Is.EqualTo((18, 10, 18)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 2), Is.EqualTo((12, 8, 12)));

            settings.SimpleDdgiRingGridSizeX = 9;
            settings.SimpleDdgiRingGridSizeY = 7;
            settings.SimpleDdgiRingGridSizeZ = 5;
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 0), Is.EqualTo((9, 7, 5)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 1), Is.EqualTo((9, 7, 5)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 2), Is.EqualTo((9, 7, 5)));
        });
    }

    [Test]
    public void ProbeAgePercentile_UsesExactNearestRankWithinTheRequestedVolume()
    {
        uint[] ages = Enumerable.Range(0, 20).Select(static value => (uint)value).ToArray();
        uint[] scratch = new uint[ages.Length];

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch, 0.50f), Is.EqualTo(9u));
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch, 0.95f), Is.EqualTo(18u));
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch, 1.0f), Is.EqualTo(19u));
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch[..5], 0.95f), Is.Zero);
        });
    }

    [Test]
    public void AuthoredLatticePhase_OffsetsAndWrapsProbePlanesWithoutMovingBounds()
    {
        Vector3 min = new(-2.1f, 0.1f, 4.2f);
        Vector3 origin = SimpleDdgiVolumeManager.ResolveAuthoredLatticeOrigin(
            min,
            spacing: 1.0f,
            latticePhase: new Vector3(0.5f, 0.25f, 0.75f));
        Vector3 wrappedOrigin = SimpleDdgiVolumeManager.ResolveAuthoredLatticeOrigin(
            new Vector3(0.1f, 0.1f, 0.1f),
            spacing: 1.0f,
            latticePhase: new Vector3(-0.25f, 1.25f, float.NaN));

        Assert.Multiple(() =>
        {
            Assert.That(origin, Is.EqualTo(new Vector3(-2.5f, -0.75f, 3.75f)));
            Assert.That(origin.X, Is.LessThanOrEqualTo(min.X));
            Assert.That(origin.Y, Is.LessThanOrEqualTo(min.Y));
            Assert.That(origin.Z, Is.LessThanOrEqualTo(min.Z));
            Assert.That(wrappedOrigin, Is.EqualTo(new Vector3(-0.25f, -0.75f, 0.0f)));
        });
    }

    [Test]
    public void SecondVolumeOwnershipEarlyOutThreshold_ClampsFiniteValuesAndFallsBackForNonFiniteValues()
    {
        var settings = new GlobalIlluminationSettings();

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = -1.0f;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.Zero);

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = 2.0f;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(1.0f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = float.NaN;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(0.95f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = float.PositiveInfinity;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(0.95f));
    }

    [Test]
    public void ProbeUpdateStride_DispersesAndVisitsEveryProbeExactlyOnce()
    {
        const int probeCount = 6_912;
        int stride = SimpleDdgiVolumeManager.ResolveProbeUpdateStride(probeCount);
        bool[] visited = new bool[probeCount];
        int cursor = 0;
        for (int i = 0; i < probeCount; i++)
        {
            Assert.That(visited[cursor], Is.False, $"duplicate at sequence index {i}");
            visited[cursor] = true;
            cursor = (int)((cursor + (long)stride) % probeCount);
        }

        Assert.Multiple(() =>
        {
            Assert.That(stride, Is.GreaterThan(probeCount / 4));
            Assert.That(visited, Is.All.True);
            Assert.That(cursor, Is.Zero);
        });
    }

    [Test]
    public void ProbeUpdateMetadata_PreservesGenerationAndClampsElapsedAge()
    {
        uint metadata = SimpleDdgiVolumeManager.PackProbeUpdateMetadata(0x00abcdeu, 400u);

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.ReadProbeUpdateGeneration(metadata), Is.EqualTo(0x00abcdeu));
            Assert.That(SimpleDdgiVolumeManager.ReadProbeUpdateAge(metadata), Is.EqualTo(255u));
        });
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        string directory = TestContext.CurrentContext.TestDirectory;
        for (int depth = 0; depth < 8; depth++)
        {
            string candidate = Path.Combine(directory, Path.Combine(relativeParts));
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }
}
