using System.Runtime.InteropServices;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Reads the 48-byte meshlet payload used through mesh format 1.2. Legacy
/// assets remain renderable, but use a disabled cone until they are recooked.
/// </summary>
internal static class CookedMeshletCompatibility
{
    public const ushort NormalConeFormatMinor = 3;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MeshletV12
    {
        public Vector3 BoundingSphereCenter;
        public float BoundingSphereRadius;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint LocalVertexOffset;
        public uint LocalVertexCount;
        public uint LocalTriangleOffset;
        public uint LocalTriangleCount;
    }

    public static Meshlet[] ReadRequired(
        CookedAssetReader reader,
        uint sectionId)
    {
        if (UsesNormalConePayload(reader))
            return reader.ReadSection<Meshlet>(sectionId);

        return Upgrade(reader.ReadSection<MeshletV12>(sectionId));
    }

    public static bool TryRead(
        CookedAssetReader reader,
        uint sectionId,
        out Meshlet[] meshlets)
    {
        if (UsesNormalConePayload(reader))
            return reader.TryReadSection(sectionId, out meshlets);

        if (!reader.TryReadSection(sectionId, out MeshletV12[] legacy))
        {
            meshlets = [];
            return false;
        }

        meshlets = Upgrade(legacy);
        return true;
    }

    private static bool UsesNormalConePayload(CookedAssetReader reader) =>
        reader.Header.AssetKind != CookedAssetKind.Mesh ||
        reader.Header.FormatMinor >= NormalConeFormatMinor;

    private static Meshlet[] Upgrade(ReadOnlySpan<MeshletV12> legacy)
    {
        var upgraded = new Meshlet[legacy.Length];
        for (int i = 0; i < legacy.Length; i++)
        {
            ref readonly MeshletV12 source = ref legacy[i];
            upgraded[i] = new Meshlet(
                source.BoundingSphereCenter,
                source.BoundingSphereRadius,
                source.VertexOffset,
                source.VertexCount,
                source.IndexOffset,
                source.IndexCount,
                source.LocalVertexOffset,
                source.LocalVertexCount,
                source.LocalTriangleOffset,
                source.LocalTriangleCount,
                Vector3.Zero,
                normalConeCutoff: 1.0f);
        }

        return upgraded;
    }
}
