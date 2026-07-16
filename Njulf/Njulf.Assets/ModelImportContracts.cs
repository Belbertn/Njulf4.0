using System;
using Njulf.Assets.Gltf;

namespace Njulf.Assets;

public enum ModelImportBackend
{
    Auto,
    Assimp,
    SharpGltf
}

public enum ModelImportStatus
{
    Imported,
    Unsupported,
    Failed
}

public sealed record ModelImportResult(
    ModelImportStatus Status,
    ModelImportBackend Backend,
    string BackendName,
    string BackendVersion,
    string AssetPath,
    ModelMesh? Mesh,
    AssetImportDiagnostics Diagnostics,
    SharpGltfCapabilityReport? SharpGltfCapability,
    string? FailureType,
    string? FailureMessage)
{
    public bool ImportedSuccessfully => Status == ModelImportStatus.Imported && Mesh != null;

    public ModelMesh EnsureImported()
    {
        if (Mesh != null)
            return Mesh;

        throw new InvalidOperationException(FailureMessage ?? $"Model import failed with status {Status}.");
    }

    public static ModelImportResult Imported(
        ModelImportBackend backend,
        string backendName,
        string backendVersion,
        string assetPath,
        ModelMesh mesh,
        AssetImportDiagnostics? diagnostics = null,
        SharpGltfCapabilityReport? sharpGltfCapability = null)
    {
        return new ModelImportResult(
            ModelImportStatus.Imported,
            backend,
            backendName,
            backendVersion,
            assetPath,
            mesh,
            diagnostics ?? mesh.ImportDiagnostics,
            sharpGltfCapability,
            null,
            null);
    }

    public static ModelImportResult Unsupported(
        ModelImportBackend backend,
        string backendName,
        string backendVersion,
        string assetPath,
        AssetImportDiagnostics diagnostics,
        string failureType,
        string failureMessage,
        SharpGltfCapabilityReport? sharpGltfCapability = null)
    {
        return new ModelImportResult(
            ModelImportStatus.Unsupported,
            backend,
            backendName,
            backendVersion,
            assetPath,
            null,
            diagnostics,
            sharpGltfCapability,
            failureType,
            failureMessage);
    }

    public static ModelImportResult Failed(
        ModelImportBackend backend,
        string backendName,
        string backendVersion,
        string assetPath,
        AssetImportDiagnostics diagnostics,
        Exception exception,
        SharpGltfCapabilityReport? sharpGltfCapability = null)
    {
        return new ModelImportResult(
            ModelImportStatus.Failed,
            backend,
            backendName,
            backendVersion,
            assetPath,
            null,
            diagnostics,
            sharpGltfCapability,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message);
    }
}

internal interface IModelAssetImporter
{
    ModelImportBackend Backend { get; }
    string BackendName { get; }
    string BackendVersion { get; }
    bool CanImport(string fullPath);
    ModelImportResult Import(string fullPath, ImporterOptions options);
}
