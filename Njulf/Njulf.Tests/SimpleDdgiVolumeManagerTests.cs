using Njulf.Rendering.Resources;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

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
        int[] minimums = [384, 144, 48];
        int[] preferredMaximums = [672, 288, 120];
        int[] capacities = [6_912, 6_912, 6_912];
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
