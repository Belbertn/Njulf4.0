namespace Njulf.Rendering.Data
{
    public sealed record RendererDiagnostics(
        int VisibleObjectCount,
        int VisibleMeshletCount,
        ulong UploadedBytes,
        int LightCount,
        uint TileCountX,
        uint TileCountY,
        int MaterialCount,
        int TextureCount,
        int LoadedFileTextureCount,
        int MipmapFallbackCount,
        string LoadedModelName,
        int ModelRenderObjectCount,
        int RegisteredMeshCount,
        int LoadedMaterialCount,
        int LoadedTextureCount,
        int DefaultWhiteSubstitutions,
        int DefaultNormalSubstitutions,
        int DefaultBlackSubstitutions)
    {
        public static RendererDiagnostics Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

        public uint TileCount => TileCountX * TileCountY;
    }
}
