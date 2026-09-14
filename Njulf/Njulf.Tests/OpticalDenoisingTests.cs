using System.Diagnostics;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class OpticalDenoisingTests
{
    [Test]
    public void FrameBudgetIncludesAllOpticalPasses()
    {
        var scene = new SceneRenderingData
        {
            GpuTransparentMicroseconds = 100,
            GpuOpticalLayerClearMicroseconds = 1,
            GpuOpticalTemporalMicroseconds = 2,
            GpuOpticalSpatialMicroseconds = 3,
            GpuOpticalCorrectionMicroseconds = 4
        };
        Assert.That(Njulf.Rendering.Diagnostics.RendererDiagnosticsAssembler.CalculateGpuFrameMicroseconds(scene), Is.EqualTo(110));
    }

    [TestCase(1600u, 900u, 512, 134217728UL)]
    [TestCase(3840u, 2160u, 16, 134217728UL)]
    [TestCase(1u, 1u, 16, 1048576UL)]
    public void AllocationRespectsTotalBudgetAndDescriptorRange(uint width, uint height, int budget, ulong range)
    {
        var allocation = OpticalDenoisingGpuContract.Allocation(width, height, budget, range);
        Assert.That(allocation.Bytes * 2, Is.LessThanOrEqualTo((ulong)budget * 1024 * 1024));
        Assert.That(allocation.Bytes, Is.LessThanOrEqualTo(range));
        Assert.That(allocation.Capacity, Is.LessThanOrEqualTo((ulong)width * height * 4));
        if (allocation.Capacity > 0)
            Assert.That(allocation.Bytes, Is.EqualTo((64UL + (ulong)width * height * 5 + allocation.Capacity * 64UL) * 4));
    }

    [Test]
    public void SettingsSnapshotDoesNotShareMutableOpticalSettings()
    {
        var settings = new RenderSettings();
        settings.OpticalDenoising.Enabled = false;
        settings.OpticalDenoising.BypassFilter = true;
        settings.OpticalDenoising.MemoryBudgetMiB = 128;
        settings.OpticalDenoising.LayerLimit = 2;
        var snapshot = settings.CreateSnapshot();
        settings.OpticalDenoising.LayerLimit = 4;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.OpticalDenoising.Enabled, Is.False);
            Assert.That(snapshot.OpticalDenoising.BypassFilter, Is.True);
            Assert.That(snapshot.OpticalDenoising.MemoryBudgetMiB, Is.EqualTo(128));
            Assert.That(snapshot.OpticalDenoising.LayerLimit, Is.EqualTo(2));
        });
    }
}

[TestFixture]
public sealed class OpticalDenoisingGpuTests
{
    private const int Width = 9, Pixels = Width * Width, Prefix = 64 + Pixels * 5;
    private const int BankWords = Prefix + Pixels * 2 * 64;
    private VulkanMaterialConformanceHarness? _gpu;

    [OneTimeSetUp]
    public void CreateGpu()
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Njulf.Shaders"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        string directory = Path.Combine(root!.FullName, "artifacts", "tests", "optical");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "filter.spv");
        var start = new ProcessStartInfo("glslangValidator") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-V", "--target-env", "vulkan1.1", "-Os", "-o", output,
            Path.Combine(root.FullName, "Njulf.Tests", "Fixtures", "Shaders", "optical_filter.comp") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); Assert.Fail("Optical test shader compilation timed out."); }
        Assert.That(process.ExitCode, Is.Zero, stdout.Result + stderr.Result);
        if (!VulkanMaterialConformanceHarness.TryCreate(File.ReadAllBytes(output), out _gpu, out string reason)) Assert.Ignore(reason);
        TestContext.Out.WriteLine($"GPU: {_gpu!.DeviceName}");
    }
    [OneTimeTearDown] public void DisposeGpu() => _gpu?.Dispose();
    private static uint F(float v) => BitConverter.SingleToUInt32Bits(v);
    private static float V(uint[] data, int offset) => BitConverter.UInt32BitsToSingle(data[offset]);
    private static int Record(int pixel, int layer = 0) => Prefix + (pixel * 2 + layer) * 64;

    private static uint[] Scene(bool noisy = false)
    {
        var data = new uint[BankWords * 2];
        for (int bank = 0; bank < 2; bank++)
        {
            int start = bank * BankWords;
            data[start + 1] = Width; data[start + 2] = Width; data[start + 3] = Pixels * 2; data[start + 4] = 4;
            data[start + 9] = 1; data[start + 11] = BankWords;
            for (int pixel = 0; pixel < Pixels; pixel++)
            {
                int x = pixel % Width, y = pixel / Width;
                int p = start + 64 + pixel * 5;
                data[p] = 2; data[p + 1] = (uint)(pixel * 2); data[p + 2] = (uint)(pixel * 2 + 1);
                for (int layer = 0; layer < 2; layer++)
                {
                    int r = start + Record(pixel, layer);
                    data[r] = 1; data[r + 1] = 2;
                    data[r + 4] = data[r + 8] = F(x * .001f);
                    data[r + 5] = data[r + 9] = F(y * .001f);
                    data[r + 6] = data[r + 10] = F(layer);
                    data[r + 7] = F(.5f); data[r + 11] = F(10);
                    foreach(int offset in new[] {20,21,22,24,25,26}) data[r+offset]=F(1);
                    float value = layer == 1 ? 20 : noisy ? (x + y) % 2 * 2 : 2;
                    foreach (int offset in new[] { 12, 16, 36, 40 })
                    {
                        data[r + offset] = data[r + offset + 1] = data[r + offset + 2] = F(value);
                        data[r + offset + 3] = F(offset >= 36 ? 4 : 1);
                    }
                    data[r + 33] = 1;
                    data[r + 34] = F((x + .5f) / Width); data[r + 35] = F((y + .5f) / Width);
                    data[r + 60] = data[r + 62] = F(value); data[r + 61] = data[r + 63] = F(value * value);
                }
            }
        }
        return data;
    }
    private uint[] Run(uint[] data) => _gpu!.RunCompute(data, BankWords, Pixels);

    [TestCase(false)]
    [TestCase(true)]
    public void CompositionUsesExistingBlendWeights(bool weighted)
    {
        uint[] scene = Scene(); scene[8] = 2; scene[12] = weighted ? 1u : 0u;
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int back = Record(pixel), front = Record(pixel, 1);
            scene[back + 23] = F(.4f); scene[front + 23] = F(.5f);
            scene[back + 31] = 1; scene[front + 31] = 2;
            scene[front + 27] = F(.4f);
        }
        uint[] result = Run(scene);
        if (weighted)
            Assert.That(V(result, Record(40, 1) + 44), Is.EqualTo(491.7660773).Within(.001));
        else
        {
            // Back glass is attenuated by the front glass; front remains unattenuated.
            Assert.That(V(result, Record(40) + 44), Is.EqualTo(.2f).Within(1e-6));
            Assert.That(V(result, Record(40, 1) + 44), Is.EqualTo(.5f).Within(1e-6));
        }
    }

    [Test]
    public void ConstantLayersKeepEnergyAndRemainSeparate()
    {
        uint[] result = Run(Scene());
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            Assert.That(V(result, Record(pixel) + 44), Is.EqualTo(2).Within(1e-5));
            Assert.That(V(result, Record(pixel, 1) + 44), Is.EqualTo(20).Within(1e-5));
        }
    }

    [Test]
    public void NoiseVarianceFallsWithoutMixingOverlappingGlass()
    {
        uint[] result = Run(Scene(noisy: true));
        double variance = 0;
        for (int y = 1; y < Width - 1; y++) for (int x = 1; x < Width - 1; x++)
        {
            float value = V(result, Record(y * Width + x) + 44);
            variance += (value - 1) * (value - 1);
            Assert.That(V(result, Record(y * Width + x, 1) + 44), Is.EqualTo(20).Within(1e-5));
        }
        Assert.That(variance / 49, Is.LessThan(.1), "Input variance is one.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LayerSlotsDoNotDefineIdentity(bool permute)
    {
        uint[] scene = Scene();
        if (permute) for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int p = 64 + pixel * 5;
            (scene[p + 1], scene[p + 2]) = (scene[p + 2], scene[p + 1]);
        }
        uint[] result = Run(scene);
        Assert.That(V(result, Record(40) + 44), Is.EqualTo(2).Within(1e-5));
        Assert.That(V(result, Record(40, 1) + 44), Is.EqualTo(20).Within(1e-5));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void ZeroWeightLobesDoNotAcquireFilteredConfidence(int temporal)
    {
        uint[] scene = Scene(); scene[8] = (uint)temporal;
        int r = Record(40);
        scene[r + 20] = scene[r + 21] = scene[r + 22] = 0;
        uint[] result = Run(scene);
        int target = temporal == 1 ? 36 : 44;
        Assert.That(V(result, r + target + 3), Is.Zero);
        Assert.That(V(result, r + target + 4), Is.EqualTo(2).Within(1e-5));
        Assert.That(V(result, r + target + 7), Is.GreaterThan(0));
    }

    [Test]
    public void SharpSecondIterationPreservesFineDetail()
    {
        uint[] scene = Scene(); scene[9] = 2;
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int r = Record(pixel);
            scene[r + 7] = F(.05f);
            scene[r + 44] = scene[r + 45] = scene[r + 46] = F(pixel % 2);
            scene[r + 47] = F(8);
        }
        uint[] result = Run(scene);
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            Assert.That(V(result, Record(pixel) + 52), Is.EqualTo(pixel % 2));
            Assert.That(V(result, Record(pixel) + 55), Is.EqualTo(8));
        }
    }

    [Test]
    public void SpatialRejectionPreservesTheEnergyOfAnIsolatedBrightSample()
    {
        uint[] scene = Scene();
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int r = Record(pixel);
            scene[r + 7] = F(.05f);
            scene[r + 36] = scene[r + 37] = scene[r + 38] = F(pixel == 40 ? 100 : 0);
            scene[r + 39] = F(16);
        }
        uint[] result = Run(scene);
        double sum = Enumerable.Range(0, Pixels).Sum(pixel => (double)V(result, Record(pixel) + 44));
        Assert.That(sum, Is.EqualTo(100).Within(1e-4));
        Assert.That(V(result, Record(40) + 44), Is.LessThan(100));
    }

    [TestCase("object")]
    [TestCase("material")]
    [TestCase("normal")]
    [TestCase("facing")]
    public void SurfaceEdgesDoNotExchangeRadiance(string boundary)
    {
        uint[] scene = Scene();
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            if (pixel % Width < 4) continue;
            int r = Record(pixel);
            int field = boundary switch { "object" => 0, "material" => 1, "normal" => 2, _ => 33 };
            scene[r + field] = boundary == "normal" ? 32767u : boundary == "facing" ? 0u : 99u;
            scene[r + 36] = scene[r + 37] = scene[r + 38] = F(50);
        }
        uint[] result = Run(scene);
        Assert.That(V(result, Record(39) + 44), Is.EqualTo(2).Within(1e-5));
        Assert.That(V(result, Record(40) + 44), Is.EqualTo(50).Within(1e-5));
    }

    [Test]
    public void MovingReceiverReprojectsItsPreviousWorldPosition()
    {
        uint[] scene = Scene(); scene[8] = 1;
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int r = Record(pixel);
            scene[r + 4] = F(V(scene, r + 4) + 1);
            scene[BankWords + r + 39] = F(8);
        }
        uint[] result = Run(scene);
        Assert.That(V(result, Record(40) + 39), Is.EqualTo(9));
    }

    [Test]
    public void RandomTransmissionPathLengthDoesNotResetAccumulation()
    {
        uint[] scene = Scene(); scene[8] = 1;
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int r = Record(pixel);
            scene[r + 29] = F(4); scene[BankWords + r + 29] = F(100);
            scene[BankWords + r + 43] = F(8);
        }
        uint[] result = Run(scene);
        Assert.That(V(result, Record(40) + 43), Is.EqualTo(9));
    }

    [Test]
    public void SpatialReconstructionFillsAnAbsentObservation()
    {
        uint[] scene = Scene(); int r = Record(40);
        scene[r + 36] = scene[r + 37] = scene[r + 38] = scene[r + 39] = 0;
        uint[] result = Run(scene);
        Assert.That(V(result, r + 44), Is.EqualTo(2).Within(1e-5));
        Assert.That(V(result, r + 47), Is.GreaterThan(0));
    }

    [TestCase(0f, 8f)]
    [TestCase(1f, 9f)]
    public void MissingObservationsDoNotIncreaseHistoryCount(float observed, float expectedCount)
    {
        uint[] scene = Scene(); scene[8] = 1;
        for (int pixel = 0; pixel < Pixels; pixel++)
        {
            int r = Record(pixel);
            scene[r + 15] = F(observed); scene[BankWords + r + 39] = F(8);
        }
        uint[] result = Run(scene);
        Assert.That(V(result, Record(40) + 39), Is.EqualTo(expectedCount));
        Assert.That(V(result, Record(40) + 36), Is.EqualTo(2).Within(1e-5));
    }

    [Test]
    public void MeasuredBlackIsNotMissingAndResetRejectsOldHistory()
    {
        uint[] scene = Scene(); scene[8] = 1; scene[10] = 1;
        int r = Record(40); scene[r + 12] = scene[r + 13] = scene[r + 14] = 0;
        uint[] result = Run(scene);
        Assert.That(V(result, r + 36), Is.Zero);
        Assert.That(V(result, r + 39), Is.EqualTo(1));
    }

    [Test]
    public void AlternatingMeasuredSamplesRetainTheirMeanEnergy()
    {
        uint[]? previous = null;
        for (int frame = 0; frame < 16; frame++)
        {
            uint[] scene = Scene(); scene[8] = 1; scene[10] = frame == 0 ? 1u : 0u;
            float sample = frame % 2 * 2;
            for (int pixel = 0; pixel < Pixels; pixel++)
            {
                int r = Record(pixel);
                scene[r + 12] = scene[r + 13] = scene[r + 14] = F(sample);
                if (previous == null) continue;
                Array.Copy(previous, r + 36, scene, BankWords + r + 36, 8);
                Array.Copy(previous, r + 60, scene, BankWords + r + 60, 4);
            }
            previous = Run(scene);
            float expected = ((frame + 1) / 2 * 2f) / (frame + 1);
            Assert.That(V(previous, Record(40) + 36), Is.EqualTo(expected).Within(1e-5), $"frame {frame}");
        }
    }

    [Test]
    public void OverflowAndDisocclusionCannotPublishBorrowedHistory()
    {
        uint[] scene = Scene(); scene[8] = 1;
        scene[64 + 40 * 5] = 5;
        scene[BankWords + Record(41)] = 99;
        uint[] result = Run(scene);
        Assert.That(V(result, Record(40) + 39), Is.Zero);
        Assert.That(V(result, Record(41) + 39), Is.EqualTo(1));
    }
}
