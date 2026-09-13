namespace Njulf.Core.Enums
{
    /// <summary>Primary intended role of a buffer; these values are alternatives, not usage bits.</summary>
    public enum BufferUsage
    {
        VertexBuffer,
        IndexBuffer,
        UniformBuffer,
        StorageBuffer,
        StagingBuffer,
        ReadbackBuffer
    }
}
