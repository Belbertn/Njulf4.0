using Njulf.Assets.Cooked;
using Njulf.Core.Math;

namespace Njulf.Assets;

/// <summary>Opt-in CPU collision data in the same coordinates as the uploaded mesh. No physics backend dependency.</summary>
public sealed record CollisionGeometry(string Name, int NodeIndex, Vector3[] Positions, uint[] Indices)
{
    /// <summary>Copies only the requested submesh before its processed CPU data is released.</summary>
    public static CollisionGeometry Extract(ProcessedSubMeshAsset subMesh)
    {
        ArgumentNullException.ThrowIfNull(subMesh);
        RejectSkin(subMesh.SkinIndex);
        return new(subMesh.Name, subMesh.NodeIndex, (Vector3[])subMesh.Vertices.Clone(), (uint[])subMesh.Indices.Clone());
    }

    /// <summary>Equivalent extraction from a decoded cooked payload, before upload releases CPU streams.
    /// Indices in cooked submesh ranges are local to that submesh's vertex range.</summary>
    public static CollisionGeometry Extract(CookedMeshPayload mesh, int subMeshIndex)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var subMesh = mesh.SubMeshes[subMeshIndex];
        RejectSkin(subMesh.SkinIndex);
        var source = mesh.VertexPositions.AsSpan(subMesh.VertexOffset, subMesh.VertexCount);
        var positions = new Vector3[source.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            var p = source[i].Position; positions[i] = new(p.X, p.Y, p.Z);
        }
        return new(subMesh.Name, subMesh.NodeIndex, positions, mesh.Indices.AsSpan(subMesh.IndexOffset, subMesh.IndexCount).ToArray());
    }
    private static void RejectSkin(int skinIndex)
    {
        if (skinIndex >= 0) throw new NotSupportedException("Skinned collision geometry requires a separately authored rigid collision mesh.");
    }
}
