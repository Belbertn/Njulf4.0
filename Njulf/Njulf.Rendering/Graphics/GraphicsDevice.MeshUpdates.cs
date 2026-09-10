using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

internal sealed class MeshChangeSource
{
    internal event Action? Changed;
    internal void Publish() => Changed?.Invoke();
}

internal sealed partial class VulkanGraphicsDevice
{
    public override Mesh CreateMesh(ReadOnlySpan<VertexPositionNormalTexture> vertices, ReadOnlySpan<uint> indices,
        MeshUsage usage = MeshUsage.Static)
        => CreateStandardMesh(ConvertVertices(vertices), indices, usage);

    public override Mesh CreateMesh(ReadOnlySpan<VertexPositionNormalTextureTangent> vertices,
        ReadOnlySpan<uint> indices, MeshUsage usage = MeshUsage.Static)
        => CreateStandardMesh(ConvertVertices(vertices), indices, usage);

    private Mesh CreateStandardMesh(GPUVertex[] vertices, ReadOnlySpan<uint> indices, MeshUsage usage)
    {
        EnsureUsable();
        if (!Enum.IsDefined(usage)) throw new ArgumentOutOfRangeException(nameof(usage));
        var handle = _meshes.RegisterStandardMesh(vertices, indices.ToArray(), usage == MeshUsage.Dynamic);
        try
        {
            return new VulkanMesh(this, handle);
        }
        catch
        {
            ReleaseMesh(handle);
            throw;
        }
    }

    private static GPUVertex[] ConvertVertices(ReadOnlySpan<VertexPositionNormalTexture> source)
    {
        var output = new GPUVertex[source.Length];
        for (int i = 0; i < source.Length; i++)
            output[i] = ConvertVertex(source[i].Position, source[i].Normal, source[i].TextureCoordinate,
                new Vector4(1, 0, 0, 1));
        return output;
    }

    private static GPUVertex[] ConvertVertices(ReadOnlySpan<VertexPositionNormalTextureTangent> source)
    {
        var output = new GPUVertex[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i].Tangent.W is not (1f or -1f))
                throw new ArgumentException("Tangent handedness must be +1 or -1.", nameof(source));
            output[i] = ConvertVertex(source[i].Position, source[i].Normal, source[i].TextureCoordinate,
                source[i].Tangent);
        }

        return output;
    }

    private static GPUVertex ConvertVertex(Vector3 position, Vector3 normal, Vector2 uv, Vector4 tangent)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            !float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z) ||
            !float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || !float.IsFinite(tangent.X) ||
            !float.IsFinite(tangent.Y) || !float.IsFinite(tangent.Z) || !float.IsFinite(tangent.W))
            throw new ArgumentException("Vertex attributes must be finite.");
        return new GPUVertex
            { Position = position, Normal = normal, TexCoord = uv, Tangent = tangent, Color = GPUVertex.DefaultColor };
    }

    public override void UpdateMeshVertices(Mesh mesh, ReadOnlySpan<VertexPositionNormalTexture> vertices) =>
        QueueMeshUpdate(mesh, ConvertVertices(vertices));

    public override void UpdateMeshVertices(Mesh mesh, ReadOnlySpan<VertexPositionNormalTextureTangent> vertices) =>
        QueueMeshUpdate(mesh, ConvertVertices(vertices));

    private void QueueMeshUpdate(Mesh mesh, GPUVertex[] vertices)
    {
        EnsureTransferAllowed();
        var handle = ValidateMesh(mesh);
        var info = _meshes.GetMeshInfo(handle);
        if (!info.IsDynamic) throw new InvalidOperationException("Only dynamic meshes accept vertex updates.");
        if (vertices.Length != info.VertexCount)
            throw new ArgumentException("Dynamic vertex count must remain unchanged.", nameof(vertices));
        _meshes.RetainMesh(handle);
        _uploads.Enqueue((cmd => _meshes.RecordVertexUpdate(this, cmd, handle, vertices),
            () => _meshes.ReleaseMesh(handle)));
    }
}