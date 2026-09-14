using System.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class HybridReflectionSparseGpuTests
{
    private VulkanMaterialConformanceHarness? _gpu;
    private const int Width = 12;

    [OneTimeSetUp]
    public void CreateGpu()
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Njulf.Shaders")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null);
        string directory = Path.Combine(root!.FullName, "artifacts", "tests", "reflection-sparse");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "filter.spv");
        var start = new ProcessStartInfo("glslangValidator")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-V", "--target-env", "vulkan1.3", "-Os", "-o", output,
                     Path.Combine(root.FullName, "Njulf.Tests", "Fixtures", "Shaders", "hybrid_reflection_sparse.comp") })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(true);
            Assert.Fail("Reflection test shader compilation timed out.");
        }
        Assert.That(process.ExitCode, Is.Zero, stdout.Result + stderr.Result);
        if (!VulkanMaterialConformanceHarness.TryCreate(File.ReadAllBytes(output), out _gpu, out string reason))
            Assert.Ignore(reason);
        TestContext.Out.WriteLine($"GPU: {_gpu!.DeviceName}");
    }

    [OneTimeTearDown]
    public void DisposeGpu() => _gpu?.Dispose();

    [TestCase(1u, 14537u, 0u)]
    [TestCase(2u, 14537u, 0u)]
    [TestCase(4u, 14537u, 0u)]
    [TestCase(4u, uint.MaxValue - 31u, 1u)]
    public void EverySlidingCycleVisitsEveryLane(uint tier, uint firstFrame, uint lobe)
    {
        uint[] input = [0, 0, 0, tier, firstFrame, lobe, 0, 0];
        uint[] result = _gpu!.RunCompute(input, 4096, 4096);
        int period = checked((int)(tier * tier));
        for (int block = 0; block < 64; block++)
        for (int start = 0; start <= 64 - period; start++)
        {
            uint[] lanes = result.AsSpan(block * 64 + start, period).ToArray();
            Assert.That(lanes, Is.EquivalentTo(Enumerable.Range(0, period).Select(i => (uint)i)),
                $"block={block}, start={start}, tier={tier}");
        }
        if (tier > 1)
            Assert.That(Enumerable.Range(0, 64).Select(b => result[b * 64]).Distinct().Count(),
                Is.GreaterThan(1), "Blocks must not share a screen-wide phase.");
    }

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    [TestCase(0f)]
    [TestCase(4f)]
    public void ReprojectionUncertaintyChangesConfidenceWithoutChangingObservedEnergy(float radiance)
    {
        uint[] result = _gpu!.RunCompute([3u, F(radiance), 0, 0, 0, 0, 0, 0], 10, 5);
        for (int i = 0; i < 5; i++)
        {
            Assert.That(BitConverter.UInt32BitsToSingle(result[i * 2]), Is.EqualTo(radiance));
            Assert.That(BitConverter.UInt32BitsToSingle(result[i * 2 + 1]),
                Is.EqualTo(.8f * i / 4).Within(1e-6));
        }
    }

    [Test]
    public void RayMissesRequestBackgroundShadingButRemainObservedDuringSparseReuse()
    {
        uint[] result = _gpu!.RunCompute([2u, 0, 0, 0, 0, 0, 0, 0], 12, 4);
        Assert.That(result, Is.EqualTo(new uint[]
        {
            0, 2, 1, // Traced miss: shade the background, then retain query provenance.
            0, 3, 0, // Budget rejection: background remains an unobserved fallback.
            0, 3, 0, // Resolution skip: likewise cannot supply a geometric history tap.
            1, 1, 1  // SSR hit remains a valid measured observation.
        }));
    }
    private static int Record(int x, int y) => 8 + (y * Width + x) * 16;
    private static void Radiance(uint[] data, int record, float value)
    {
        for (int c = 8; c <= 10; c++) data[record + c] = F(value);
    }

    private static uint[] MissingScene()
    {
        var data = new uint[8 + Width * Width * 16];
        data[0] = 1; data[1] = data[2] = Width; data[7] = 1;
        for (int y = 0; y < Width; y++)
        for (int x = 0; x < Width; x++)
        {
            int r = Record(x, y);
            data[r] = 1; data[r + 1] = F(.5f); data[r + 4] = F(1);
            data[r + 5] = F(.42f); data[r + 6] = 3;
            // Both resolution skips and rejected ray admissions need support.
            data[r + 7] = (uint)(1 + (x + y) % 2);
            Radiance(data, r, 10); data[r + 11] = F(1); data[r + 12] = 1;
        }
        return data;
    }

    private uint[] Filter(uint[] data) => _gpu!.RunCompute(data, Width * Width * 4, Width * Width);

    [TestCase(2u, 0u, 2f)] // Fresh geometric hit.
    [TestCase(2u, 2u, 2f)] // Valid bounded history after budget rejection.
    [TestCase(3u, 0u, 0f)] // Measured miss: black is not missing data.
    public void SparseZeroVarianceFieldReconstructsConstantObservations(uint source, uint sparseState, float value)
    {
        uint[] data = MissingScene();
        for (int y = 3; y < Width; y += 4)
        for (int x = 3; x < Width; x += 4)
        {
            int r = Record(x, y);
            data[r + 6] = source; data[r + 7] = sparseState;
            Radiance(data, r, value);
        }
        uint[] result = Filter(data);
        for (int pixel = 0; pixel < Width * Width; pixel++)
        for (int c = 0; c < 3; c++)
            Assert.That(BitConverter.UInt32BitsToSingle(result[pixel * 4 + c]),
                Is.EqualTo(value).Within(1e-5), $"pixel={pixel}, channel={c}");

        // The second iteration consumes the first spatial output without
        // promoting reconstructed holes to temporal observations.
        for (int pixel = 0; pixel < Width * Width; pixel++)
            result.AsSpan(pixel * 4, 4).CopyTo(data.AsSpan(8 + pixel * 16 + 8, 4));
        data[6] = 1;
        result = Filter(data);
        Assert.That(result.Where((_, i) => i % 4 != 3).Select(BitConverter.UInt32BitsToSingle),
            Is.All.EqualTo(value).Within(1e-5));
    }

    [TestCase("compatible", 2f)]
    [TestCase("cached-analytic", 2f)]
    [TestCase("identity", 10f)]
    [TestCase("depth", 10f)]
    [TestCase("normal", 10f)]
    [TestCase("roughness", 10f)]
    [TestCase("invalid-history", 10f)]
    [TestCase("missing-fallback", 10f)]
    public void ReconstructionRejectsUnrelatedOrUnobservedNeighbors(string boundary, float expected)
    {
        uint[] data = MissingScene();
        int r = Record(6, 5);
        data[r + 6] = 2; data[r + 7] = 0; Radiance(data, r, 2);
        switch (boundary)
        {
            case "identity": data[r] = 2; break;
            case "depth": data[r + 1] = F(.6f); break;
            case "normal": data[r + 2] = F(1); data[r + 4] = 0; break;
            case "roughness": data[r + 5] = F(.8f); break;
            case "invalid-history": data[r + 12] = 0; break;
            case "missing-fallback": data[r + 6] = 3; data[r + 7] = 2; break;
            case "cached-analytic": data[r + 6] = 3; data[r + 7] = 1; data[r + 13] = 8; break;
        }
        uint[] result = Filter(data);
        Assert.That(BitConverter.UInt32BitsToSingle(result[(5 * Width + 5) * 4]),
            Is.EqualTo(expected).Within(1e-5));
    }

    [Test]
    public void RejectedLocalRayBlockUsesWiderCompatibleSupport()
    {
        uint[] data = MissingScene();
        int donor = Record(10, 5);
        data[donor + 6] = 2; data[donor + 7] = 0; Radiance(data, donor, 2);
        uint[] result = Filter(data);
        Assert.That(BitConverter.UInt32BitsToSingle(result[(5 * Width + 5) * 4]),
            Is.EqualTo(2f).Within(1e-5));
        data[donor] = 2;
        result = Filter(data);
        Assert.That(BitConverter.UInt32BitsToSingle(result[(5 * Width + 5) * 4]),
            Is.EqualTo(10f), "Wider support must still reject other receivers.");
    }

    [Test]
    public void ObservedMirrorDetailIsNotBlurredByMissingNeighbors()
    {
        uint[] data = MissingScene();
        for (int y = 0; y < Width; y++)
        for (int x = 0; x < Width; x++)
            data[Record(x, y) + 5] = F(.04f);
        int center = Record(5, 5), neighbor = Record(6, 5);
        data[center + 6] = data[neighbor + 6] = 2;
        data[center + 7] = data[neighbor + 7] = 0;
        Radiance(data, center, 2); Radiance(data, neighbor, 100);
        uint[] result = Filter(data);
        Assert.That(BitConverter.UInt32BitsToSingle(result[(5 * Width + 5) * 4]), Is.EqualTo(2f));
    }

    [Test]
    public void NoSupportRetainsFiniteAnalyticFallback()
    {
        uint[] result = Filter(MissingScene());
        Assert.That(result.Where((_, i) => i % 4 != 3).Select(BitConverter.UInt32BitsToSingle),
            Is.All.EqualTo(10f));
    }
}
