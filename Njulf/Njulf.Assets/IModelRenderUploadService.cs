using Njulf.Core.Scene;

namespace Njulf.Assets
{
    /// <summary>
    /// Bridges imported asset data to renderer-owned GPU resources without making
    /// Njulf.Assets reference a concrete rendering backend.
    /// </summary>
    public interface IModelRenderUploadService
    {
        ModelRenderUploadDiagnostics LastUploadDiagnostics { get; }

        Model UploadModel(ModelMesh modelMesh);
    }

    public sealed record ModelRenderUploadDiagnostics(
        string ModelName,
        int RenderObjectCount,
        int RegisteredMeshCount,
        int LoadedMaterialCount,
        int LoadedTextureCount,
        int DefaultWhiteSubstitutions,
        int DefaultNormalSubstitutions,
        int DefaultBlackSubstitutions);
}
