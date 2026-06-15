using Njulf.Core.Math;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.GpuScene;

public readonly record struct GpuSceneObjectDesc(
    MeshHandle Mesh,
    MaterialHandle Material,
    Matrix4x4 WorldMatrix,
    BoundingBox LocalBounds,
    BoundingSphere LocalBoundingSphere,
    GpuSceneObjectFlags Flags,
    uint VisibilityMask = uint.MaxValue,
    int SkinningDataOffset = -1,
    int SkinnedVertexOffset = 0);

public readonly record struct GpuSceneStaticBatchDesc(
    MeshHandle Mesh,
    MaterialHandle Material,
    IReadOnlyList<Matrix4x4> WorldMatrices,
    BoundingBox LocalBounds,
    BoundingSphere LocalBoundingSphere,
    GpuSceneObjectFlags Flags,
    uint VisibilityMask = uint.MaxValue);

public sealed record GpuSceneStats(
    int ObjectCount,
    int InstanceCount,
    int ObjectCapacity,
    int InstanceCapacity,
    int ObjectHighWaterMark,
    int InstanceHighWaterMark,
    int ObjectResizeCount,
    int InstanceResizeCount,
    ulong LastUploadBytes,
    ulong LastTransformUploadBytes,
    ulong LastMaterialUploadBytes,
    ulong LastMeshUploadBytes,
    ulong LastBoundsUploadBytes,
    ulong LastVisibilityUploadBytes,
    ulong TotalUploadBytes);

public readonly record struct GpuSceneUploadRange(int Start, int Count)
{
    public int EndExclusive => checked(Start + Count);
}

public sealed record GpuSceneUploadPlan(
    IReadOnlyList<GpuSceneUploadRange> ObjectRanges,
    IReadOnlyList<GpuSceneUploadRange> InstanceRanges,
    IReadOnlyList<GpuSceneUploadRange> TransformRanges,
    IReadOnlyList<GpuSceneUploadRange> PreviousTransformRanges,
    IReadOnlyList<GpuSceneUploadRange> BoundsRanges,
    IReadOnlyList<GpuSceneUploadRange> VisibilityRanges,
    ulong ObjectBytes,
    ulong InstanceBytes,
    ulong TransformBytes,
    ulong PreviousTransformBytes,
    ulong BoundsBytes,
    ulong VisibilityBytes)
{
    public ulong TotalBytes => ObjectBytes + InstanceBytes + TransformBytes + PreviousTransformBytes + BoundsBytes + VisibilityBytes;
}
