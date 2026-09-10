namespace Njulf.Graphics;

public abstract partial class GraphicsDevice
{
    /// <summary>Creates standard indexed surface geometry, copying all input before return.</summary>
    public virtual Mesh CreateMesh(ReadOnlySpan<VertexPositionNormalTexture> vertices, ReadOnlySpan<uint> indices,
        MeshUsage usage = MeshUsage.Static) => throw new NotSupportedException();

    /// <inheritdoc cref="CreateMesh(ReadOnlySpan{VertexPositionNormalTexture}, ReadOnlySpan{uint}, MeshUsage)"/>
    public virtual Mesh CreateMesh(ReadOnlySpan<VertexPositionNormalTextureTangent> vertices,
        ReadOnlySpan<uint> indices, MeshUsage usage = MeshUsage.Static) => throw new NotSupportedException();

    /// <summary>Queues a complete vertex replacement for a dynamic mesh at the next frame boundary. Count and topology remain fixed.</summary>
    public virtual void UpdateMeshVertices(Mesh mesh, ReadOnlySpan<VertexPositionNormalTexture> vertices) =>
        throw new NotSupportedException();

    /// <inheritdoc cref="UpdateMeshVertices(Mesh, ReadOnlySpan{VertexPositionNormalTexture})"/>
    public virtual void UpdateMeshVertices(Mesh mesh, ReadOnlySpan<VertexPositionNormalTextureTangent> vertices) =>
        throw new NotSupportedException();

    /// <summary>Creates a texture from one tightly packed payload per declared mip, or just the base mip when generation is requested.</summary>
    public virtual Texture CreateTexture2D(Texture2DDescription description, ReadOnlySpan<ReadOnlyMemory<byte>> mipData,
        bool generateMipmaps = false) => throw new NotSupportedException();

    /// <summary>Queues a tightly packed rectangle update; other pixels and mip levels are preserved.</summary>
    public virtual void UpdateTexture2D(Texture texture, int mipLevel, TextureRectangle rectangle,
        ReadOnlySpan<byte> data) => throw new NotSupportedException();

    /// <summary>Queues regeneration of all allocated lower mip levels from mip zero.</summary>
    public virtual void GenerateMipmaps(Texture texture) => throw new NotSupportedException();

    /// <summary>Creates storage and initializes a prefix. Remaining bytes are undefined.</summary>
    public virtual GraphicsBuffer CreateBuffer(ulong sizeInBytes, ReadOnlySpan<byte> initialData) =>
        throw new NotSupportedException();

    /// <summary>Copies caller bytes before return and queues a range update for the next frame.</summary>
    public virtual void UpdateBuffer(GraphicsBuffer buffer, ulong destinationOffsetBytes, ReadOnlySpan<byte> data) =>
        throw new NotSupportedException();

    /// <summary>Reads the range after the next frame's producers. Keep pumping frames; never block the device thread awaiting this task.</summary>
    public virtual Task<byte[]> ReadBufferAsync(GraphicsBuffer buffer, ulong offsetBytes, int byteCount,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Reads a complete mip after the next frame's producers. Completion requires continued frame pumping.</summary>
    public virtual Task<TextureReadback> ReadTexture2DAsync(ITexture texture, int mipLevel = 0,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}