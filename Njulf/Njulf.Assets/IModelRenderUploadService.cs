using Njulf.Core.Scene;
using Njulf.Assets.Cooked;

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
        Model UploadCookedModel(CookedModelAsset model) =>
            throw new NotSupportedException($"{GetType().Name} does not support cooked model uploads.");
    }

    public readonly record struct ModelUploadWorkProgress(
        ContentLoadStage Stage,
        long CompletedBytes,
        long TotalBytes,
        string Detail);

    /// <summary>
    /// Optional renderer capability used by asynchronous content loading.
    /// CPU conversion and source authentication happen while the work is
    /// created on a background worker; renderer mutation remains sliced by
    /// <see cref="IContentUploadWork{T}"/> on the render thread.
    /// </summary>
    public interface ICooperativeModelRenderUploadService
    {
        IContentUploadWork<Model> PrepareCookedModelUpload(
            CookedModelAsset model,
            Action<ModelUploadWorkProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional renderer capability for preparing source-imported models away
    /// from the render thread and publishing their GPU resources in bounded
    /// upload slices.
    /// </summary>
    public interface ICooperativeSourceModelRenderUploadService
    {
        IContentUploadWork<Model> PrepareModelUpload(
            ModelMesh modelMesh,
            Action<ModelUploadWorkProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }

    public sealed record ModelRenderUploadDiagnostics(
        string ModelName,
        int RenderObjectCount,
        int RegisteredMeshCount,
        int LoadedMaterialCount,
        int LoadedTextureCount,
        int DefaultWhiteSubstitutions,
        int DefaultNormalSubstitutions,
        int DefaultBlackSubstitutions,
        int BlendMaterialCount,
        int CompletePrimitiveProfileCount = 0,
        int InvalidPrimitiveProfileCount = 0,
        int PrimitiveProfileCacheHitCount = 0,
        int PrimitiveProfileCacheMissCount = 0,
        int PrimitiveTextureAnalysisFailureCount = 0,
        int OmittedEmissiveTriangleRecordCount = 0,
        string PrimitiveProfileDiagnostic = "",
        int OpacityMicromapPayloadAcceptedCount = 0,
        int OpacityMicromapRuntimeRegistrationCount = 0,
        string OpacityMicromapRuntimeDetail = "opacity-micromap-section-absent");
}
