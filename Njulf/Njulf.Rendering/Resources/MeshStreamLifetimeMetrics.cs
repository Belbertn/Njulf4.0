namespace Njulf.Rendering.Resources;

internal static class MeshStreamLifetimeMetrics
{
    public static MeshStreamHighWater CalculateHighWater(
        IReadOnlyList<MeshInfo> meshes,
        MeshSlotLifetimeTable lifetimes,
        int excludedMeshIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(lifetimes);
        ulong vertexElements = 0;
        ulong indexElements = 0;
        ulong meshletElements = 0;
        ulong meshletVertexIndexElements = 0;
        ulong meshletTriangleIndexElements = 0;
        ulong skinningElements = 0;

        for (int index = 0; index < meshes.Count; index++)
        {
            if (index == excludedMeshIndex ||
                !lifetimes.IsSlotLive(index))
            {
                continue;
            }

            MeshInfo live = meshes[index];
            vertexElements = Math.Max(
                vertexElements,
                checked((ulong)live.VertexOffset +
                        live.VertexCount));
            indexElements = Math.Max(
                indexElements,
                checked((ulong)live.IndexOffset +
                        live.EffectiveGpuIndexCount));
            meshletElements = Math.Max(
                meshletElements,
                checked((ulong)live.EffectivePhysicalMeshletOffset +
                        live.EffectiveGpuMeshletRecordCount));
            meshletVertexIndexElements = Math.Max(
                meshletVertexIndexElements,
                checked((ulong)live.LocalVertexIndexOffset +
                        live.LocalVertexIndexCount));
            meshletTriangleIndexElements = Math.Max(
                meshletTriangleIndexElements,
                checked((ulong)live.LocalTriangleIndexOffset +
                        live.LocalTriangleIndexCount));
            skinningElements = Math.Max(
                skinningElements,
                checked((ulong)live.SkinningDataOffset +
                        live.SkinningDataCount));
        }

        return new MeshStreamHighWater(
            vertexElements,
            indexElements,
            meshletElements,
            meshletVertexIndexElements,
            meshletTriangleIndexElements,
            skinningElements);
    }
}

internal readonly record struct MeshStreamHighWater(
    ulong VertexElements,
    ulong IndexElements,
    ulong MeshletElements,
    ulong MeshletVertexIndexElements,
    ulong MeshletTriangleIndexElements,
    ulong SkinningElements);
