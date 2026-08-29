using Njulf.Rendering.Data;

namespace Njulf.Rendering.Core;

/// <summary>
/// Correctness-relevant VK_EXT_mesh_shader properties captured from the
/// selected physical device. Preference bits are diagnostic facts only and
/// never qualify or disable a renderer path.
/// </summary>
public readonly record struct MeshShaderDeviceProperties(
    uint MaximumTaskWorkGroupInvocations,
    uint MaximumTaskWorkGroupSizeX,
    uint MaximumTaskPayloadBytes,
    uint MaximumTaskSharedMemoryBytes,
    uint MaximumMeshWorkGroupInvocations,
    uint MaximumMeshWorkGroupSizeX,
    uint MaximumMeshSharedMemoryBytes,
    uint MaximumMeshOutputMemoryBytes,
    uint MaximumMeshPayloadAndOutputMemoryBytes,
    uint MaximumMeshOutputComponents,
    uint MaximumMeshOutputVertices,
    uint MaximumMeshOutputPrimitives,
    uint MeshOutputPerVertexGranularity,
    uint MeshOutputPerPrimitiveGranularity,
    uint MaximumPreferredTaskWorkGroupInvocations,
    uint MaximumPreferredMeshWorkGroupInvocations,
    bool PrefersLocalInvocationVertexOutput,
    bool PrefersLocalInvocationPrimitiveOutput,
    bool PrefersCompactVertexOutput,
    bool PrefersCompactPrimitiveOutput)
{
    public bool IsPopulated =>
        MaximumMeshWorkGroupInvocations != 0 &&
        MaximumMeshWorkGroupSizeX != 0 &&
        MaximumMeshOutputVertices != 0 &&
        MaximumMeshOutputPrimitives != 0;

    public bool Supports(in MeshShaderPermutation permutation)
    {
        if (!IsPopulated ||
            permutation.WorkgroupSize > MaximumMeshWorkGroupInvocations ||
            permutation.WorkgroupSize > MaximumMeshWorkGroupSizeX ||
            permutation.MaximumVertices > MaximumMeshOutputVertices ||
            permutation.MaximumPrimitives > MaximumMeshOutputPrimitives ||
            MaximumMeshSharedMemoryBytes < sizeof(uint) ||
            MaximumMeshOutputMemoryBytes == 0 ||
            MaximumMeshPayloadAndOutputMemoryBytes == 0 ||
            MaximumMeshOutputComponents < 24)
        {
            return false;
        }

        return permutation.Taskless ||
            MaximumTaskWorkGroupInvocations != 0 &&
            MaximumTaskWorkGroupSizeX != 0 &&
            MaximumTaskPayloadBytes >= 8 &&
            MaximumTaskSharedMemoryBytes >= sizeof(uint);
    }
}

public readonly record struct MeshShaderPermutation(
    MeshShaderTuningMode Mode,
    uint MaximumVertices,
    uint MaximumPrimitives,
    uint WorkgroupSize,
    bool Taskless,
    string ArtifactSuffix)
{
    public string SelectTasklessArtifact(string stem) =>
        $"{stem}{ArtifactSuffix}.mesh.spv";
}

public readonly record struct MeshShaderSelection(
    MeshShaderTuningMode RequestedMode,
    MeshShaderPermutation Permutation,
    string FallbackReason)
{
    public bool UsedFallback => FallbackReason.Length != 0;
}

public static class MeshShaderPermutationPolicy
{
    public static readonly MeshShaderPermutation Compact64 = new(
        MeshShaderTuningMode.Taskless48V64P64Threads,
        48,
        64,
        64,
        Taskless: true,
        ArtifactSuffix: string.Empty);

    public static readonly MeshShaderPermutation Compact128 = new(
        MeshShaderTuningMode.Taskless48V64P128Threads,
        48,
        64,
        128,
        Taskless: true,
        ArtifactSuffix: "_48v64p_128t");

    public static readonly MeshShaderPermutation Wide64 = new(
        MeshShaderTuningMode.Taskless64V126P64Threads,
        64,
        126,
        64,
        Taskless: true,
        ArtifactSuffix: "_64v126p_64t");

    public static readonly MeshShaderPermutation Wide128 = new(
        MeshShaderTuningMode.Taskless64V126P128Threads,
        64,
        126,
        128,
        Taskless: true,
        ArtifactSuffix: "_64v126p_128t");

    public static readonly MeshShaderPermutation CompatibilityTask = new(
        MeshShaderTuningMode.CompatibilityTask,
        64,
        126,
        128,
        Taskless: false,
        ArtifactSuffix: "_64v126p_128t");

    public static MeshShaderSelection Resolve(
        MeshShaderTuningMode requestedMode,
        in MeshShaderDeviceProperties properties,
        uint requiredVertices,
        uint requiredPrimitives)
    {
        MeshShaderPermutation requested = ResolveRequested(
            requestedMode,
            requiredVertices,
            requiredPrimitives);
        if (FitsContent(requested, requiredVertices, requiredPrimitives) &&
            properties.Supports(requested))
        {
            return new MeshShaderSelection(
                requestedMode,
                requested,
                string.Empty);
        }

        foreach (MeshShaderPermutation candidate in FallbackOrder(requested))
        {
            if (FitsContent(candidate, requiredVertices, requiredPrimitives) &&
                properties.Supports(candidate))
            {
                string reason = FitsContent(
                        requested,
                        requiredVertices,
                        requiredPrimitives)
                    ? "requested-permutation-exceeds-device-limits"
                    : "requested-permutation-exceeds-content-contract";
                return new MeshShaderSelection(
                    requestedMode,
                    candidate,
                    reason);
            }
        }

        throw new InvalidOperationException(
            "The selected Vulkan device cannot satisfy any mesh-shader " +
            $"permutation for content requiring {requiredVertices} vertices " +
            $"and {requiredPrimitives} primitives per meshlet.");
    }

    private static MeshShaderPermutation ResolveRequested(
        MeshShaderTuningMode mode,
        uint requiredVertices,
        uint requiredPrimitives) => mode switch
    {
        MeshShaderTuningMode.Auto =>
            requiredVertices <= Compact64.MaximumVertices &&
            requiredPrimitives <= Compact64.MaximumPrimitives
                ? Compact64
                : Wide64,
        MeshShaderTuningMode.Taskless48V64P64Threads => Compact64,
        MeshShaderTuningMode.Taskless48V64P128Threads => Compact128,
        MeshShaderTuningMode.Taskless64V126P64Threads => Wide64,
        MeshShaderTuningMode.Taskless64V126P128Threads => Wide128,
        MeshShaderTuningMode.CompatibilityTask => CompatibilityTask,
        _ => Compact64
    };

    private static IEnumerable<MeshShaderPermutation> FallbackOrder(
        MeshShaderPermutation requested)
    {
        if (!requested.Taskless)
            yield return Wide128;
        yield return Wide64;
        yield return Wide128;
        yield return Compact64;
        yield return Compact128;
        yield return CompatibilityTask;
    }

    private static bool FitsContent(
        in MeshShaderPermutation permutation,
        uint requiredVertices,
        uint requiredPrimitives) =>
        requiredVertices <= permutation.MaximumVertices &&
        requiredPrimitives <= permutation.MaximumPrimitives;
}
