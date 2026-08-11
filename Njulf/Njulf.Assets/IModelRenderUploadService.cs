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
