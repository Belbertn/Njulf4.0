using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Defines the resource operations used by a model upload transaction.
/// Keeping the transaction behind this narrow boundary makes failure handling
/// deterministic without changing the renderer's public construction surface.
/// </summary>
internal interface IModelRenderUploadBackend
{
    TextureHandle DefaultWhiteTexture { get; }

    TextureHandle DefaultNormalTexture { get; }

    TextureHandle DefaultBlackTexture { get; }

    void InitializeDefaultTextures();

    IModelTextureUploadBatch BeginTextureUploadBatch(
        ulong maximumStagingBytes = 8UL * 1024UL * 1024UL) =>
        NoopModelTextureUploadBatch.Instance;

    ModelTextureSource PrepareTextureSource(
        ModelTextureSource source,
        TextureSamplerDescription samplerDescription,
        bool srgb,
        TextureSemantic semantic,
        RuntimeTextureMipPolicy mipPolicy) => source;

    TextureHandle LoadTexture(
        ModelTextureSource source,
        TextureSamplerDescription samplerDescription,
        bool generateMipmaps,
        bool srgb,
        bool requireWithinMemoryBudget,
        TextureSemantic semantic,
        RuntimeTextureMipPolicy mipPolicy);

    TextureHandle LoadOptionalTextureFromFile(
        string? path,
        TextureHandle fallback,
        bool generateMipmaps,
        bool srgb,
        TextureSemantic semantic,
        RuntimeTextureMipPolicy mipPolicy);

    int GetBindlessTextureIndex(TextureHandle handle);

    bool TryGetTextureTransportStatistics(
        TextureHandle handle,
        out TextureTransportStatistics statistics);

    void RetainTexture(TextureHandle handle);

    void ReleaseTexture(TextureHandle handle);

    MaterialHandle RegisterMaterialDefinition(MaterialDefinition definition);

    MaterialHandle RegisterMaterialDefinition(
        MaterialDefinition definition,
        GiMaterialTransportProfile primitiveProfile);

    MaterialDefinition GetMaterialDefinition(MaterialHandle handle);

    uint GetMaterialContentRevision(MaterialHandle handle) => 1U;

    IReadOnlyList<TextureHandle> GetMaterialTextures(MaterialHandle handle);

    void RetainMaterial(MaterialHandle handle);

    void ReleaseMaterial(MaterialHandle handle);

    MeshHandle[] RegisterMeshes(IReadOnlyList<MeshManager.MeshRegistrationData> meshes);

    IModelMeshUpload BeginMeshUpload(
        IReadOnlyList<MeshManager.MeshRegistrationData> meshes) =>
        new CompletedModelMeshUpload(RegisterMeshes(meshes));

    IModelMeshUpload BeginMeshUploadWithCapacity(
        IReadOnlyList<MeshManager.MeshRegistrationData> meshes,
        IReadOnlyList<MeshManager.MeshRegistrationData> capacityRegistrations) =>
        BeginMeshUpload(meshes);

    void RetainMesh(MeshHandle handle);

    void ReleaseMesh(MeshHandle handle);
}

internal sealed class ModelRenderUploadBackend : IModelRenderUploadBackend
{
    private readonly MeshManager _meshManager;
    private readonly TextureManager _textureManager;
    private readonly MaterialManager _materialManager;

    public ModelRenderUploadBackend(
        MeshManager meshManager,
        TextureManager textureManager,
        MaterialManager materialManager)
    {
        _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
        _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
    }

    public TextureHandle DefaultWhiteTexture => _textureManager.DefaultWhiteTexture;

    public TextureHandle DefaultNormalTexture => _textureManager.DefaultNormalTexture;

    public TextureHandle DefaultBlackTexture => _textureManager.DefaultBlackTexture;

    public void InitializeDefaultTextures()
    {
        _textureManager.InitializeDefaultTextures();
    }

    public IModelTextureUploadBatch BeginTextureUploadBatch(
        ulong maximumStagingBytes = 8UL * 1024UL * 1024UL) =>
        _textureManager.BeginUploadBatch(maximumStagingBytes);

    public ModelTextureSource PrepareTextureSource(
        ModelTextureSource source,
        TextureSamplerDescription samplerDescription,
        bool srgb,
        TextureSemantic semantic,
        RuntimeTextureMipPolicy mipPolicy) =>
        _textureManager.PrepareTextureSource(
            source,
            samplerDescription,
            srgb,
            semantic,
            mipPolicy);

    public TextureHandle LoadTexture(
        ModelTextureSource source,
        TextureSamplerDescription samplerDescription,
        bool generateMipmaps,
        bool srgb,
        bool requireWithinMemoryBudget,
        TextureSemantic semantic,
        RuntimeTextureMipPolicy mipPolicy)
    {
        return _textureManager.LoadTexture(
            source,
            samplerDescription,
            generateMipmaps,
            srgb,
            requireWithinMemoryBudget,
            semantic,
            mipPolicy);
    }

    public TextureHandle LoadOptionalTextureFromFile(
        string? path,
        TextureHandle fallback,
        bool generateMipmaps,
        bool srgb,
        TextureSemantic semantic,
        RuntimeTextureMipPolicy mipPolicy)
    {
        return _textureManager.LoadOptionalTextureFromFile(
            path,
            fallback,
            generateMipmaps,
            srgb,
            semantic,
            mipPolicy);
    }

    public int GetBindlessTextureIndex(TextureHandle handle)
    {
        return _textureManager.GetBindlessTextureIndex(handle);
    }

    public bool TryGetTextureTransportStatistics(
        TextureHandle handle,
        out TextureTransportStatistics statistics)
    {
        return _textureManager.TryGetTextureTransportStatistics(handle, out statistics);
    }

    public void RetainTexture(TextureHandle handle)
    {
        _textureManager.RetainTexture(handle);
    }

    public void ReleaseTexture(TextureHandle handle)
    {
        _textureManager.ReleaseTexture(handle);
    }

    public MaterialHandle RegisterMaterialDefinition(MaterialDefinition definition)
    {
        return _materialManager.RegisterMaterialDefinition(definition);
    }

    public MaterialHandle RegisterMaterialDefinition(
        MaterialDefinition definition,
        GiMaterialTransportProfile primitiveProfile)
    {
        return _materialManager.RegisterMaterialDefinition(definition, primitiveProfile);
    }

    public MaterialDefinition GetMaterialDefinition(MaterialHandle handle)
    {
        return _materialManager.GetMaterialDefinition(handle);
    }

    public uint GetMaterialContentRevision(MaterialHandle handle)
    {
        if (!handle.IsValid)
            throw new ArgumentOutOfRangeException(nameof(handle));
        return _materialManager.GetMaterialContentRevision(handle.Index);
    }

    public IReadOnlyList<TextureHandle> GetMaterialTextures(MaterialHandle handle)
    {
        return _materialManager.GetMaterialTextures(handle);
    }

    public void ReleaseMaterial(MaterialHandle handle)
    {
        _materialManager.ReleaseMaterial(handle);
    }

    public void RetainMaterial(MaterialHandle handle)
    {
        _materialManager.RetainMaterial(handle);
    }

    public MeshHandle[] RegisterMeshes(IReadOnlyList<MeshManager.MeshRegistrationData> meshes)
    {
        return _meshManager.RegisterMeshes(meshes);
    }

    public IModelMeshUpload BeginMeshUpload(
        IReadOnlyList<MeshManager.MeshRegistrationData> meshes) =>
        _meshManager.BeginRegistrationUpload(meshes);

    public IModelMeshUpload BeginMeshUploadWithCapacity(
        IReadOnlyList<MeshManager.MeshRegistrationData> meshes,
        IReadOnlyList<MeshManager.MeshRegistrationData> capacityRegistrations) =>
        _meshManager.BeginRegistrationUpload(
            meshes,
            capacityRegistrations);

    public void RetainMesh(MeshHandle handle)
    {
        _meshManager.RetainMesh(handle);
    }

    public void ReleaseMesh(MeshHandle handle)
    {
        _meshManager.ReleaseMesh(handle);
    }
}

internal interface IModelTextureUploadBatch : IDisposable
{
    void Complete();

    /// <summary>
    /// Returns true once every submitted texture copy has completed and its
    /// retained staging resources have been released. Must be called from the
    /// render/device-owning thread.
    /// </summary>
    bool TryCompleteGpuWork();
}

internal interface IModelMeshUpload : IDisposable
{
    IReadOnlyList<MeshHandle> Handles { get; }

    bool TryCompleteGpuWork();

    void CompleteGpuWork();

    bool TryCancelGpuWork();
}

internal sealed class CompletedModelMeshUpload : IModelMeshUpload
{
    public CompletedModelMeshUpload(IReadOnlyList<MeshHandle> handles)
    {
        Handles = handles ?? throw new ArgumentNullException(nameof(handles));
    }

    public IReadOnlyList<MeshHandle> Handles { get; }

    public bool TryCompleteGpuWork() => true;

    public void CompleteGpuWork()
    {
    }

    public bool TryCancelGpuWork() => true;

    public void Dispose()
    {
    }
}

internal sealed class NoopModelTextureUploadBatch :
    IModelTextureUploadBatch
{
    public static NoopModelTextureUploadBatch Instance { get; } = new();

    private NoopModelTextureUploadBatch()
    {
    }

    public void Complete()
    {
    }

    public bool TryCompleteGpuWork() => true;

    public void Dispose()
    {
    }
}
