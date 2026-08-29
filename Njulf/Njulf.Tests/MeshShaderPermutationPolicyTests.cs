using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshShaderPermutationPolicyTests
{
    [Test]
    public void Auto_UsesCompactTasklessContractForProductionContent()
    {
        MeshShaderSelection selection = MeshShaderPermutationPolicy.Resolve(
            MeshShaderTuningMode.Auto,
            FullSupport(),
            requiredVertices: 48,
            requiredPrimitives: 64);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Permutation,
                Is.EqualTo(MeshShaderPermutationPolicy.Compact64));
            Assert.That(selection.Permutation.Taskless, Is.True);
            Assert.That(selection.UsedFallback, Is.False);
        });
    }

    [Test]
    public void Auto_WidensOnlyWhenContentRequiresIt()
    {
        MeshShaderSelection selection = MeshShaderPermutationPolicy.Resolve(
            MeshShaderTuningMode.Auto,
            FullSupport(),
            requiredVertices: 64,
            requiredPrimitives: 126);

        Assert.That(selection.Permutation,
            Is.EqualTo(MeshShaderPermutationPolicy.Wide64));
    }

    [Test]
    public void ForcedCompactMode_FailsSafelyToWideForWideContent()
    {
        MeshShaderSelection selection = MeshShaderPermutationPolicy.Resolve(
            MeshShaderTuningMode.Taskless48V64P128Threads,
            FullSupport(),
            requiredVertices: 64,
            requiredPrimitives: 126);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Permutation,
                Is.EqualTo(MeshShaderPermutationPolicy.Wide64));
            Assert.That(selection.FallbackReason,
                Is.EqualTo("requested-permutation-exceeds-content-contract"));
        });
    }

    [Test]
    public void UnsupportedThreadWidth_UsesSupportedTasklessPermutation()
    {
        MeshShaderDeviceProperties properties = FullSupport() with
        {
            MaximumMeshWorkGroupInvocations = 64,
            MaximumMeshWorkGroupSizeX = 64
        };
        MeshShaderSelection selection = MeshShaderPermutationPolicy.Resolve(
            MeshShaderTuningMode.Taskless48V64P128Threads,
            properties,
            requiredVertices: 48,
            requiredPrimitives: 64);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Permutation.WorkgroupSize, Is.EqualTo(64));
            Assert.That(selection.UsedFallback, Is.True);
        });
    }

    [Test]
    public void CurrentSettingsFile_RoundTripsExplicitTuningMode()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"njulf-mesh-shader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new RenderSettings();
            settings.Raster.MeshShaderTuningMode =
                MeshShaderTuningMode.Taskless64V126P128Threads;
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.That(
                loaded.Raster.MeshShaderTuningMode,
                Is.EqualTo(
                    MeshShaderTuningMode.Taskless64V126P128Threads));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MeshShaderDeviceProperties FullSupport() => new(
        MaximumTaskWorkGroupInvocations: 128,
        MaximumTaskWorkGroupSizeX: 128,
        MaximumTaskPayloadBytes: 16_384,
        MaximumTaskSharedMemoryBytes: 32_768,
        MaximumMeshWorkGroupInvocations: 128,
        MaximumMeshWorkGroupSizeX: 128,
        MaximumMeshSharedMemoryBytes: 32_768,
        MaximumMeshOutputMemoryBytes: 32_768,
        MaximumMeshPayloadAndOutputMemoryBytes: 32_768,
        MaximumMeshOutputComponents: 128,
        MaximumMeshOutputVertices: 256,
        MaximumMeshOutputPrimitives: 256,
        MeshOutputPerVertexGranularity: 32,
        MeshOutputPerPrimitiveGranularity: 32,
        MaximumPreferredTaskWorkGroupInvocations: 32,
        MaximumPreferredMeshWorkGroupInvocations: 32,
        PrefersLocalInvocationVertexOutput: true,
        PrefersLocalInvocationPrimitiveOutput: true,
        PrefersCompactVertexOutput: true,
        PrefersCompactPrimitiveOutput: true);
}
