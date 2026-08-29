using System.Runtime.InteropServices;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Decodes meshlet payloads. Model/mesh 2.0 is a hard recook boundary; the
/// legacy branches remain isolated for non-mesh package sections and tooling
/// diagnostics, but a runtime reader rejects 1.x mesh headers before this path.
/// </summary>
internal static class CookedMeshletCompatibility
{
    public const ushort NormalConeRecordFormatMinor = 3;
    public const ushort CosineNormalConeFormatMinor = 4;

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
        Meshlet[] meshlets;
        if (UsesNormalConePayload(reader))
            meshlets = reader.ReadSection<Meshlet>(sectionId);
        else
            meshlets = Upgrade(reader.ReadSection<MeshletV12>(sectionId));

        UpgradeLegacySineCones(reader, meshlets);
        ValidateCones(reader, meshlets);
        return meshlets;
    }

    public static bool TryRead(
        CookedAssetReader reader,
        uint sectionId,
        out Meshlet[] meshlets)
    {
        if (UsesNormalConePayload(reader))
        {
            if (!reader.TryReadSection(sectionId, out meshlets))
                return false;

            UpgradeLegacySineCones(reader, meshlets);
            ValidateCones(reader, meshlets);
            return true;
        }

        if (!reader.TryReadSection(sectionId, out MeshletV12[] legacy))
        {
            meshlets = [];
            return false;
        }

        meshlets = Upgrade(legacy);
        UpgradeLegacySineCones(reader, meshlets);
        ValidateCones(reader, meshlets);
        return true;
    }

    private static bool UsesNormalConePayload(CookedAssetReader reader) =>
        reader.Header.AssetKind != CookedAssetKind.Mesh ||
        reader.Header.FormatMajor >= 2 ||
        reader.Header.FormatMinor >= NormalConeRecordFormatMinor;

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
                normalConeCutoff: -1.0f);
        }

        return upgraded;
    }

    private static void UpgradeLegacySineCones(
        CookedAssetReader reader,
        Span<Meshlet> meshlets)
    {
        if (reader.Header.AssetKind != CookedAssetKind.Mesh ||
            reader.Header.FormatMajor >= 2 ||
            reader.Header.FormatMinor != NormalConeRecordFormatMinor)
        {
            return;
        }

        const float cutoffSafetyMargin = 1e-5f;
        for (int i = 0; i < meshlets.Length; i++)
        {
            Meshlet meshlet = meshlets[i];
            Vector3 axis = meshlet.NormalConeAxis;
            float legacySineCutoff = meshlet.NormalConeCutoff;
            float axisLengthSquared = axis.LengthSquared();
            if (!float.IsFinite(axis.X) ||
                !float.IsFinite(axis.Y) ||
                !float.IsFinite(axis.Z) ||
                !float.IsFinite(legacySineCutoff) ||
                !float.IsFinite(axisLengthSquared) ||
                legacySineCutoff < 0.0f ||
                legacySineCutoff > 1.0f)
            {
                throw new CookedAssetFormatException(
                    reader.Path,
                    $"meshlet {i} has an invalid legacy 1.3 normal cone");
            }

            if (axisLengthSquared <= 1e-12f || legacySineCutoff >= 1.0f)
            {
                meshlet.NormalConeAxis = Vector3.Zero;
                meshlet.NormalConeCutoff = -1.0f;
            }
            else
            {
                float cosineCutoff = MathF.Sqrt(MathF.Max(
                    1.0f - legacySineCutoff * legacySineCutoff,
                    0.0f)) - cutoffSafetyMargin;
                if (cosineCutoff <= 0.0f)
                {
                    meshlet.NormalConeAxis = Vector3.Zero;
                    meshlet.NormalConeCutoff = -1.0f;
                }
                else
                {
                    meshlet.NormalConeAxis = axis / MathF.Sqrt(axisLengthSquared);
                    meshlet.NormalConeCutoff = cosineCutoff;
                }
            }

            meshlets[i] = meshlet;
        }
    }

    private static void ValidateCones(
        CookedAssetReader reader,
        ReadOnlySpan<Meshlet> meshlets)
    {
        const float unitAxisTolerance = 1e-3f;
        for (int i = 0; i < meshlets.Length; i++)
        {
            ref readonly Meshlet meshlet = ref meshlets[i];
            Vector3 axis = meshlet.NormalConeAxis;
            float cutoff = meshlet.NormalConeCutoff;
            bool axisFinite = float.IsFinite(axis.X) &&
                              float.IsFinite(axis.Y) &&
                              float.IsFinite(axis.Z);
            if (!axisFinite || !float.IsFinite(cutoff))
            {
                throw new CookedAssetFormatException(
                    reader.Path,
                    $"meshlet {i} has a non-finite normal cone");
            }

            float axisLengthSquared = axis.LengthSquared();
            if (cutoff == -1.0f)
            {
                if (axisLengthSquared > 1e-12f)
                {
                    throw new CookedAssetFormatException(
                        reader.Path,
                        $"meshlet {i} uses the disabled cutoff with a non-zero normal-cone axis");
                }
                continue;
            }

            if (!(cutoff > 0.0f && cutoff <= 1.0f) ||
                MathF.Abs(axisLengthSquared - 1.0f) > unitAxisTolerance)
            {
                throw new CookedAssetFormatException(
                    reader.Path,
                    $"meshlet {i} has an invalid normal cone (cutoff {cutoff}, axis length squared {axisLengthSquared})");
            }
        }
    }
}
