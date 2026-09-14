using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSpatialInvalidationGpuTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void MovedGeometryInvalidatesTheSameWorldProbesAcrossSchedulingOrders(
        bool permuted, bool scrolled)
    {
        // The near-grid dimensions, spacing and origin are from the physics example.
        const uint countX = 28, countY = 14, countZ = 28;
        const uint count = countX * countY * countZ;
        const float spacing = 0.875f;
        var origin = new Vector3(-9.1875f, -3.9375f, -7.4375f);
        uint offsetX = scrolled ? 7u : 0u;
        uint offsetY = scrolled ? 3u : 0u;
        uint offsetZ = scrolled ? 11u : 0u;
        var policy = new GPUSimpleDdgiSchedulerVolumePolicy
        {
            FirstProbe = 37,
            ProbeCount = count,
            CurrentCountX = countX,
            CurrentCountY = countY,
            CurrentCountZ = countZ,
            CurrentOriginAndSpacing = new Vector4(origin.X, origin.Y, origin.Z, spacing),
            PhysicalOffsetX = offsetX,
            PhysicalOffsetY = offsetY,
            PhysicalOffsetZ = offsetZ,
            SequenceStride = permuted
                ? (uint)SimpleDdgiVolumeManager.ResolveProbeUpdateStride((int)count)
                : 1u
        };
        // Disjoint old/new footprints make missing either side observable.
        GPUSimpleDdgiSchedulerDirtyRegion[] regions =
        [
            new() { Minimum = new(-3.5f, -1f, -1.5f, 0),
                Maximum = new(-0.5f, 2f, 1.5f, 0), ReasonFlags = 8 },
            new() { Minimum = new(3.5f, -1f, -1.5f, 0),
                Maximum = new(6.5f, 2f, 1.5f, 0), ReasonFlags = 2 }
        ];
        var input = new uint[48 + 2 * 20];
        MemoryMarshal.Cast<GPUSimpleDdgiSchedulerVolumePolicy, uint>(
            new[] { policy }.AsSpan()).CopyTo(input);
        MemoryMarshal.Cast<GPUSimpleDdgiSchedulerDirtyRegion, uint>(
            regions.AsSpan()).CopyTo(input.AsSpan(48));
        if (!VulkanMaterialConformanceHarness.TryCreate(CompileShader(),
                out var harness, out string unavailableReason))
        {
            Assert.Ignore(unavailableReason);
            return;
        }
        using var gpu = harness!;
        uint[] output = gpu.RunCompute(input, checked((int)count * 2), count);

        // Build the oracle in world lattice order, without using the scheduling
        // permutation. Physical wrapping changes storage, never world coverage.
        var expected = new uint[count];
        for (uint z = 0; z < countZ; z++)
        for (uint y = 0; y < countY; y++)
        for (uint x = 0; x < countX; x++)
        {
            var position = origin + new Vector3(x * spacing, y * spacing, z * spacing);
            uint physical = (x + offsetX) % countX +
                ((y + offsetY) % countY) * countX +
                ((z + offsetZ) % countZ) * countX * countY;
            foreach (var region in regions)
            {
                if (position.X >= region.Minimum.X - spacing && position.X <= region.Maximum.X + spacing &&
                    position.Y >= region.Minimum.Y - spacing && position.Y <= region.Maximum.Y + spacing &&
                    position.Z >= region.Minimum.Z - spacing && position.Z <= region.Maximum.Z + spacing)
                    expected[physical] |= region.ReasonFlags;
            }
        }
        var visited = new bool[count];
        int missed = 0, spurious = 0;
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            uint probe = output[ordinal * 2] - policy.FirstProbe;
            Assert.That(probe, Is.LessThan(count));
            Assert.That(visited[probe], Is.False, "Each physical slot must be visited once.");
            visited[probe] = true;
            uint actual = output[ordinal * 2 + 1];
            if ((expected[probe] & ~actual) != 0) missed++;
            if ((actual & ~expected[probe]) != 0) spurious++;
        }
        TestContext.Out.WriteLine($"GPU={gpu.DeviceName}; stride={policy.SequenceStride}; " +
            $"scrolled={scrolled}; missed={missed}; spurious={spurious}");
        Assert.Multiple(() =>
        {
            Assert.That(expected.Count(value => value != 0), Is.GreaterThan(0));
            Assert.That(missed, Is.Zero, "Changed geometry must refresh every affected world probe.");
            Assert.That(spurious, Is.Zero, "Scheduling order must not move the dirty footprint.");
        });
    }

    private static byte[] CompileShader()
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Njulf.Shaders")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null, "The production shader source must be available.");
        string directory = Path.Combine(root!.FullName, "artifacts", "tests");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, $"ddgi-spatial-{Guid.NewGuid():N}.spv");
        var start = new ProcessStartInfo("glslangValidator")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-V", "--target-env", "vulkan1.1", "-Os", "-o", output,
            Path.Combine(root.FullName, "Njulf.Tests", "Fixtures", "Shaders", "ddgi_scheduler_spatial.comp") })
            start.ArgumentList.Add(argument);
        try
        {
            using Process compiler = Process.Start(start)!;
            Task<string> stdout = compiler.StandardOutput.ReadToEndAsync();
            Task<string> stderr = compiler.StandardError.ReadToEndAsync();
            if (!compiler.WaitForExit(30_000))
            {
                compiler.Kill(entireProcessTree: true);
                Assert.Fail("Spatial shader compilation timed out.");
            }
            Assert.That(compiler.ExitCode, Is.Zero, stdout.Result + stderr.Result);
            return File.ReadAllBytes(output);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
