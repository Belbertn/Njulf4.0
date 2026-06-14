using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Assets;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Rendering.Resources
{
    public sealed class ModelRenderUploadService : IModelRenderUploadService
    {
        private readonly MeshManager _meshManager;
        private readonly TextureManager _textureManager;
        private readonly MaterialManager _materialManager;
        private readonly object _diagnosticsLock = new object();
        private ModelRenderUploadDiagnostics _lastUploadDiagnostics =
            new ModelRenderUploadDiagnostics(string.Empty, 0, 0, 0, 0, 0, 0, 0, 0);

        public ModelRenderUploadService(
            MeshManager meshManager,
            TextureManager textureManager,
            MaterialManager materialManager)
        {
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        }

        public ModelRenderUploadDiagnostics LastUploadDiagnostics
        {
            get
            {
                lock (_diagnosticsLock)
                    return _lastUploadDiagnostics;
            }
        }

        public Model UploadModel(ModelMesh modelMesh)
        {
            if (modelMesh == null)
                throw new ArgumentNullException(nameof(modelMesh));

            ValidateModelMesh(modelMesh);

            var model = new Model
            {
                Name = string.IsNullOrWhiteSpace(modelMesh.Name) ? "Model" : modelMesh.Name,
                BoundingBox = modelMesh.BoundingBox,
                BoundingSphere = modelMesh.BoundingSphere
            };
            model.AddSkeletons(modelMesh.Skeletons);
            model.AddSkins(modelMesh.Skins);
            model.AddAnimationClips(modelMesh.AnimationClips);

            _textureManager.InitializeDefaultTextures();
            MaterialUploadResult materialUpload = RegisterImportedMaterials(modelMesh.Materials);
            MaterialHandle[] materials = materialUpload.Materials;
            IReadOnlyList<ModelSubMesh> subMeshes = modelMesh.SubMeshes.Count > 0
                ? modelMesh.SubMeshes
                : new[]
                {
                    new ModelSubMesh
                    {
                        Name = string.IsNullOrWhiteSpace(modelMesh.Name) ? "Mesh" : modelMesh.Name,
                        MaterialIndex = 0,
                        Vertices = modelMesh.Vertices,
                        Normals = modelMesh.Normals,
                        Tangents = modelMesh.Tangents,
                        Bitangents = modelMesh.Bitangents,
                        TexCoords = modelMesh.TexCoords,
                        TexCoords1 = modelMesh.TexCoords1,
                        VertexColors = modelMesh.VertexColors,
                        JointIndices0 = modelMesh.JointIndices0,
                        JointWeights0 = modelMesh.JointWeights0,
                        Indices = modelMesh.Indices,
                        BoundingBox = modelMesh.BoundingBox,
                        BoundingSphere = modelMesh.BoundingSphere
                    }
                };

            var processedAssets = new ProcessedMeshAsset[subMeshes.Count];
            var skinningDataByMesh = new GPUVertexSkinningData[subMeshes.Count][];
            var subMeshMaterialIndices = new int[subMeshes.Count];
            var subMeshNames = new string[subMeshes.Count];
            var processedBuilder = new ProcessedMeshAssetBuilder();
            for (int i = 0; i < subMeshes.Count; i++)
            {
                ModelSubMesh subMesh = subMeshes[i];
                ValidateSubMesh(subMesh, nameof(modelMesh));

                ProcessedMeshAsset processedAsset = processedBuilder.Build(
                    CreateSubmeshModelMesh(modelMesh, subMesh, i),
                    new ProcessedMeshBuildOptions
                    {
                        AssetId = $"{model.Name}/{subMesh.Name}/{i}",
                        SourcePath = modelMesh.Name,
                        GenerateFallbackLods = true,
                        IsFoliage = IsFoliageSubmesh(subMesh, modelMesh.Materials),
                        GenerateImpostorMetadata = IsFoliageSubmesh(subMesh, modelMesh.Materials)
                    });

                processedAssets[i] = processedAsset;
                skinningDataByMesh[i] = BuildGpuSkinningData(processedAsset, subMesh, model);
                subMeshMaterialIndices[i] = ResolveSubMeshMaterialIndex(subMesh, materials.Length);
                subMeshNames[i] = string.IsNullOrWhiteSpace(subMesh.Name) ? model.Name : subMesh.Name;
            }

            MeshHandle[] meshHandles = _meshManager.RegisterProcessedMeshAssets(processedAssets, skinningDataByMesh);
            for (int i = 0; i < meshHandles.Length; i++)
            {
                int materialIndex = subMeshMaterialIndices[i];

                RenderObject renderObject = subMeshes[i].SkinIndex >= 0 && subMeshes[i].SkinIndex < model.Skins.Count
                    ? new SkinnedRenderObject(meshHandles[i], materials[materialIndex])
                    {
                        SkinIndex = subMeshes[i].SkinIndex,
                        Animator = CreateAnimator(model, subMeshes[i].SkinIndex),
                        SkinningBindTransform = subMeshes[i].SkinningBindTransform
                    }
                    : new RenderObject(meshHandles[i], materials[materialIndex]);

                renderObject.Name = subMeshNames[i];
                model.Add(renderObject);
            }

            RegisterModelMaterialLifetime(model, materials);

            SetLastUploadDiagnostics(new ModelRenderUploadDiagnostics(
                model.Name,
                model.RenderObjects.Count,
                subMeshes.Count,
                materials.Length,
                materialUpload.DynamicTextureIndices.Count,
                materialUpload.DefaultWhiteSubstitutions,
                materialUpload.DefaultNormalSubstitutions,
                materialUpload.DefaultBlackSubstitutions,
                materialUpload.BlendMaterialCount));

            return model;
        }

        private void SetLastUploadDiagnostics(ModelRenderUploadDiagnostics diagnostics)
        {
            lock (_diagnosticsLock)
                _lastUploadDiagnostics = diagnostics;
        }

        private static void ValidateModelMesh(ModelMesh modelMesh)
        {
            if (modelMesh.Vertices.Length == 0)
                throw new ArgumentException("Imported model contains no vertices.", nameof(modelMesh));
            if (modelMesh.Indices.Length == 0)
                throw new ArgumentException("Imported model contains no indices.", nameof(modelMesh));
            if (modelMesh.Indices.Length % 3 != 0)
                throw new ArgumentException("Imported model index count must be divisible by 3.", nameof(modelMesh));

            ValidateOptionalStream(modelMesh.Normals, modelMesh.Vertices.Length, nameof(modelMesh.Normals));
            ValidateOptionalStream(modelMesh.Tangents, modelMesh.Vertices.Length, nameof(modelMesh.Tangents));
            ValidateOptionalStream(modelMesh.Bitangents, modelMesh.Vertices.Length, nameof(modelMesh.Bitangents));
            ValidateOptionalStream(modelMesh.TexCoords, modelMesh.Vertices.Length, nameof(modelMesh.TexCoords));
            ValidateOptionalStream(modelMesh.TexCoords1, modelMesh.Vertices.Length, nameof(modelMesh.TexCoords1));
            ValidateOptionalStream(modelMesh.VertexColors, modelMesh.Vertices.Length, nameof(modelMesh.VertexColors));

            for (int i = 0; i < modelMesh.Indices.Length; i++)
            {
                if (modelMesh.Indices[i] >= modelMesh.Vertices.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(modelMesh),
                        $"Imported model index {i} references vertex {modelMesh.Indices[i]}, but vertex count is {modelMesh.Vertices.Length}.");
                }
            }
        }

        private static void ValidateSubMesh(ModelSubMesh subMesh, string argumentName)
        {
            if (subMesh.Vertices.Length == 0)
                throw new ArgumentException("Imported submesh contains no vertices.", argumentName);
            if (subMesh.Indices.Length == 0)
                throw new ArgumentException("Imported submesh contains no indices.", argumentName);
            if (subMesh.Indices.Length % 3 != 0)
                throw new ArgumentException("Imported submesh index count must be divisible by 3.", argumentName);

            ValidateOptionalStream(subMesh.Normals, subMesh.Vertices.Length, nameof(subMesh.Normals));
            ValidateOptionalStream(subMesh.Tangents, subMesh.Vertices.Length, nameof(subMesh.Tangents));
            ValidateOptionalStream(subMesh.Bitangents, subMesh.Vertices.Length, nameof(subMesh.Bitangents));
            ValidateOptionalStream(subMesh.TexCoords, subMesh.Vertices.Length, nameof(subMesh.TexCoords));
            ValidateOptionalStream(subMesh.TexCoords1, subMesh.Vertices.Length, nameof(subMesh.TexCoords1));
            ValidateOptionalStream(subMesh.VertexColors, subMesh.Vertices.Length, nameof(subMesh.VertexColors));
            ValidateOptionalStream(subMesh.JointIndices0, subMesh.Vertices.Length, nameof(subMesh.JointIndices0));
            ValidateOptionalStream(subMesh.JointWeights0, subMesh.Vertices.Length, nameof(subMesh.JointWeights0));

            for (int i = 0; i < subMesh.Indices.Length; i++)
            {
                if (subMesh.Indices[i] >= subMesh.Vertices.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        argumentName,
                        $"Imported submesh index {i} references vertex {subMesh.Indices[i]}, but vertex count is {subMesh.Vertices.Length}.");
                }
            }
        }

        private static void ValidateOptionalStream<T>(T[] stream, int vertexCount, string streamName)
        {
            if (stream.Length != 0 && stream.Length != vertexCount)
                throw new ArgumentException($"Imported {streamName} stream length must be either 0 or match vertex count.", streamName);
        }

        private static GPUVertexSkinningData[] BuildGpuSkinningData(ProcessedMeshAsset asset, ModelSubMesh subMesh, Model model)
        {
            if (subMesh.SkinIndex < 0)
                return Array.Empty<GPUVertexSkinningData>();
            if (subMesh.SkinIndex >= model.Skins.Count)
                throw new InvalidOperationException(
                    $"Imported submesh '{subMesh.Name}' references skin index {subMesh.SkinIndex}, but the model only has {model.Skins.Count} skins.");

            int jointCount = model.Skins[subMesh.SkinIndex].JointIndices.Count;
            var skinningData = new GPUVertexSkinningData[asset.Vertices.Count];
            for (int i = 0; i < skinningData.Length; i++)
            {
                VertexSkinningPayload skinning = asset.Vertices[i].Skinning;
                ValidateJointIndex(subMesh.Name, i, skinning.Joint0, jointCount);
                ValidateJointIndex(subMesh.Name, i, skinning.Joint1, jointCount);
                ValidateJointIndex(subMesh.Name, i, skinning.Joint2, jointCount);
                ValidateJointIndex(subMesh.Name, i, skinning.Joint3, jointCount);

                skinningData[i] = new GPUVertexSkinningData
                {
                    Joint0 = skinning.Joint0,
                    Joint1 = skinning.Joint1,
                    Joint2 = skinning.Joint2,
                    Joint3 = skinning.Joint3,
                    Weight0 = skinning.Weight0,
                    Weight1 = skinning.Weight1,
                    Weight2 = skinning.Weight2,
                    Weight3 = skinning.Weight3
                };
            }

            return skinningData;
        }

        private static ModelMesh CreateSubmeshModelMesh(ModelMesh source, ModelSubMesh subMesh, int subMeshIndex)
        {
            var mesh = new ModelMesh
            {
                Name = string.IsNullOrWhiteSpace(subMesh.Name) ? $"{source.Name}_Submesh{subMeshIndex}" : subMesh.Name,
                BoundingBox = subMesh.BoundingBox,
                BoundingSphere = subMesh.BoundingSphere
            };
            mesh.Materials.AddRange(source.Materials.Count > 0 ? source.Materials : new[] { ModelMaterial.Default });
            mesh.Skeletons.AddRange(source.Skeletons);
            mesh.Skins.AddRange(source.Skins);
            mesh.AnimationClips.AddRange(source.AnimationClips);
            mesh.AnimationDiagnostics = source.AnimationDiagnostics;
            mesh.ImportDiagnostics = source.ImportDiagnostics;
            mesh.SubMeshes.Add(subMesh);
            return mesh;
        }

        private static bool IsFoliageSubmesh(ModelSubMesh subMesh, IReadOnlyList<ModelMaterial> materials)
        {
            if (subMesh.Name.Contains("foliage", StringComparison.OrdinalIgnoreCase) ||
                subMesh.Name.Contains("grass", StringComparison.OrdinalIgnoreCase) ||
                subMesh.Name.Contains("leaf", StringComparison.OrdinalIgnoreCase) ||
                subMesh.Name.Contains("leaves", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return subMesh.MaterialIndex >= 0 &&
                   subMesh.MaterialIndex < materials.Count &&
                   materials[subMesh.MaterialIndex].AlphaMode == ModelAlphaMode.Mask &&
                   materials[subMesh.MaterialIndex].DoubleSided;
        }

        private static void ValidateJointIndex(string subMeshName, int vertexIndex, ushort jointIndex, int jointCount)
        {
            if (jointIndex >= jointCount)
            {
                throw new InvalidOperationException(
                    $"Skinned submesh '{subMeshName}' vertex {vertexIndex} references joint {jointIndex}, but the skin only has {jointCount} joints.");
            }
        }

        private static int ResolveSubMeshMaterialIndex(ModelSubMesh subMesh, int materialCount)
        {
            if (materialCount <= 0)
                throw new InvalidOperationException("No GPU materials were built for the imported model.");

            if (subMesh.MaterialIndex < 0 || subMesh.MaterialIndex >= materialCount)
            {
                throw new InvalidOperationException(
                    $"Imported submesh '{subMesh.Name}' references material index {subMesh.MaterialIndex}, " +
                    $"but the imported material count is {materialCount}.");
            }

            return subMesh.MaterialIndex;
        }

        private static Animator? CreateAnimator(Model model, int skinIndex)
        {
            if (skinIndex < 0 || skinIndex >= model.Skins.Count)
                return null;

            Skin skin = model.Skins[skinIndex];
            return new Animator(skin.Skeleton, model.Skins, model.AnimationClips);
        }

        private MaterialUploadResult RegisterImportedMaterials(IReadOnlyList<ModelMaterial> importedMaterials)
        {
            if (importedMaterials.Count == 0)
                importedMaterials = new[] { ModelMaterial.Default };

            var materials = new MaterialHandle[importedMaterials.Count];
            var dynamicTextureIndices = new HashSet<int>();
            int defaultWhiteSubstitutions = 0;
            int defaultNormalSubstitutions = 0;
            int defaultBlackSubstitutions = 0;
            int blendMaterialCount = 0;

            for (int i = 0; i < importedMaterials.Count; i++)
            {
                ModelMaterial material = importedMaterials[i];
                if (material.AlphaMode == ModelAlphaMode.Blend)
                    blendMaterialCount++;

                MaterialTextureBindings textureBindings = ResolveMaterialTextureBindings(
                    material,
                    ref defaultWhiteSubstitutions,
                    ref defaultNormalSubstitutions,
                    ref defaultBlackSubstitutions);

                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.AlbedoTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.NormalTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.MetallicRoughnessTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.EmissiveTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ClearcoatTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ClearcoatRoughnessTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ClearcoatNormalTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SheenColorTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SheenRoughnessTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.AnisotropyTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.TransmissionTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ThicknessTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SubsurfaceTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SpecularTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SpecularColorTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.IridescenceTextureIndex);
                AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.IridescenceThicknessTextureIndex);

                GPUMaterialData gpuMaterial = BuildGpuMaterialData(material, textureBindings.TextureIndices);
                GPUMaterialExtensionData? extensionData =
                    (MaterialFeatureFlags)gpuMaterial.FeatureFlags == MaterialFeatureFlags.None
                        ? null
                        : BuildGpuMaterialExtensionData(material, textureBindings.ExtensionTextureIndices);
                MaterialRenderMetadata metadata = BuildMaterialRenderMetadata(material);
                materials[i] = _materialManager.RegisterMaterial(gpuMaterial, extensionData, metadata, textureBindings.TextureHandles);
            }

            return new MaterialUploadResult(
                materials,
                dynamicTextureIndices,
                defaultWhiteSubstitutions,
                defaultNormalSubstitutions,
                defaultBlackSubstitutions,
                blendMaterialCount);
        }

        private MaterialTextureBindings ResolveMaterialTextureBindings(
            ModelMaterial material,
            ref int defaultWhiteSubstitutions,
            ref int defaultNormalSubstitutions,
            ref int defaultBlackSubstitutions)
        {
            TextureHandle albedoTexture = ResolveTextureHandle(
                material.BaseColorTexture,
                material.AlbedoTexturePath,
                _textureManager.DefaultWhiteTexture,
                ref defaultWhiteSubstitutions,
                generateMipmaps: ShouldGenerateAlbedoMipmaps(material),
                srgb: true);
            TextureHandle normalTexture = ResolveTextureHandle(
                material.NormalTexture,
                material.NormalTexturePath,
                _textureManager.DefaultNormalTexture,
                ref defaultNormalSubstitutions,
                generateMipmaps: true,
                srgb: false);

            TextureHandle metallicRoughnessTexture = ResolveTextureHandle(
                material.MetallicRoughnessTexture,
                material.MetallicRoughnessTexturePath,
                _textureManager.DefaultBlackTexture,
                ref defaultBlackSubstitutions,
                generateMipmaps: true,
                srgb: false);
            TextureHandle emissiveTexture = ResolveTextureHandle(
                material.EmissiveTexture,
                material.EmissiveTexturePath,
                _textureManager.DefaultBlackTexture,
                ref defaultBlackSubstitutions,
                generateMipmaps: true,
                srgb: true);

            TextureHandle clearcoatTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle clearcoatRoughnessTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle clearcoatNormalTexture = _textureManager.DefaultNormalTexture;
            TextureHandle sheenColorTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle sheenRoughnessTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle anisotropyTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle transmissionTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle thicknessTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle subsurfaceTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle specularTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle specularColorTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle iridescenceTexture = _textureManager.DefaultWhiteTexture;
            TextureHandle iridescenceThicknessTexture = _textureManager.DefaultWhiteTexture;

            if ((MaterialFeatureFlags)material.FeatureFlags != MaterialFeatureFlags.None)
            {
                clearcoatTexture = ResolveTextureHandle(
                    material.ClearcoatTexture,
                    material.ClearcoatTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                clearcoatRoughnessTexture = ResolveTextureHandle(
                    material.ClearcoatRoughnessTexture,
                    material.ClearcoatRoughnessTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                clearcoatNormalTexture = ResolveTextureHandle(
                    material.ClearcoatNormalTexture,
                    material.ClearcoatNormalTexturePath,
                    _textureManager.DefaultNormalTexture,
                    ref defaultNormalSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                sheenColorTexture = ResolveTextureHandle(
                    material.SheenColorTexture,
                    material.SheenColorTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: true);
                sheenRoughnessTexture = ResolveTextureHandle(
                    material.SheenRoughnessTexture,
                    material.SheenRoughnessTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                anisotropyTexture = ResolveTextureHandle(
                    material.AnisotropyTexture,
                    material.AnisotropyTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                transmissionTexture = ResolveTextureHandle(
                    material.TransmissionTexture,
                    material.TransmissionTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                thicknessTexture = ResolveTextureHandle(
                    material.ThicknessTexture,
                    material.ThicknessTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                subsurfaceTexture = ResolveTextureHandle(
                    material.SubsurfaceTexture,
                    material.SubsurfaceTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: true);
                specularTexture = ResolveTextureHandle(
                    material.SpecularTexture,
                    material.SpecularTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                specularColorTexture = ResolveTextureHandle(
                    material.SpecularColorTexture,
                    material.SpecularColorTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: true);
                iridescenceTexture = ResolveTextureHandle(
                    material.IridescenceTexture,
                    material.IridescenceTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
                iridescenceThicknessTexture = ResolveTextureHandle(
                    material.IridescenceThicknessTexture,
                    material.IridescenceThicknessTexturePath,
                    _textureManager.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false);
            }

            TextureHandle[] textureHandles = (MaterialFeatureFlags)material.FeatureFlags == MaterialFeatureFlags.None
                ? new[]
                {
                    albedoTexture,
                    normalTexture,
                    metallicRoughnessTexture,
                    emissiveTexture
                }
                : new[]
                {
                    albedoTexture,
                    normalTexture,
                    metallicRoughnessTexture,
                    emissiveTexture,
                    clearcoatTexture,
                    clearcoatRoughnessTexture,
                    clearcoatNormalTexture,
                    sheenColorTexture,
                    sheenRoughnessTexture,
                    anisotropyTexture,
                    transmissionTexture,
                    thicknessTexture,
                    subsurfaceTexture,
                    specularTexture,
                    specularColorTexture,
                    iridescenceTexture,
                    iridescenceThicknessTexture
                };

            return new MaterialTextureBindings(
                new MaterialTextureIndices(
                    _textureManager.GetBindlessTextureIndex(albedoTexture),
                    _textureManager.GetBindlessTextureIndex(normalTexture),
                    _textureManager.GetBindlessTextureIndex(metallicRoughnessTexture),
                    _textureManager.GetBindlessTextureIndex(emissiveTexture)),
                new MaterialExtensionTextureIndices(
                    _textureManager.GetBindlessTextureIndex(clearcoatTexture),
                    _textureManager.GetBindlessTextureIndex(clearcoatRoughnessTexture),
                    _textureManager.GetBindlessTextureIndex(clearcoatNormalTexture),
                    _textureManager.GetBindlessTextureIndex(sheenColorTexture),
                    _textureManager.GetBindlessTextureIndex(sheenRoughnessTexture),
                    _textureManager.GetBindlessTextureIndex(anisotropyTexture),
                    _textureManager.GetBindlessTextureIndex(transmissionTexture),
                    _textureManager.GetBindlessTextureIndex(thicknessTexture),
                    _textureManager.GetBindlessTextureIndex(subsurfaceTexture),
                    _textureManager.GetBindlessTextureIndex(specularTexture),
                    _textureManager.GetBindlessTextureIndex(specularColorTexture),
                    _textureManager.GetBindlessTextureIndex(iridescenceTexture),
                    _textureManager.GetBindlessTextureIndex(iridescenceThicknessTexture)),
                textureHandles);
        }

        public static GPUMaterialData BuildGpuMaterialData(ModelMaterial material, MaterialTextureIndices textureIndices)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            return new GPUMaterialData
            {
                Albedo = material.Albedo,
                Emissive = material.Emissive,
                NormalScaleBias = new CoreVector4(
                    material.NormalScale,
                    ToGpuAlphaModeCode(material.AlphaMode),
                    Math.Clamp(material.AlphaCutoff, 0f, 1f),
                    material.DoubleSided ? 1f : 0f),
                MetallicRoughnessAO = new CoreVector4(
                    Math.Clamp(material.Metallic, 0f, 1f),
                    Math.Clamp(material.Roughness, 0.04f, 1f),
                    Math.Clamp(material.AmbientOcclusion, 0f, 1f),
                    ShouldSampleOcclusionFromMetallicRoughnessTexture(material) ? 1f : 0f),
                BaseColorOffsetScale = ToOffsetScale(material.BaseColorTexture),
                NormalOffsetScale = ToOffsetScale(material.NormalTexture),
                MetallicRoughnessOffsetScale = ToOffsetScale(material.MetallicRoughnessTexture),
                EmissiveOffsetScale = ToOffsetScale(material.EmissiveTexture),
                TextureRotations = new CoreVector4(
                    material.BaseColorTexture?.RotationRadians ?? 0f,
                    material.NormalTexture?.RotationRadians ?? 0f,
                    material.MetallicRoughnessTexture?.RotationRadians ?? 0f,
                    material.EmissiveTexture?.RotationRadians ?? 0f),
                TextureTexCoordSets = new CoreVector4(
                    Math.Clamp(material.BaseColorTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.NormalTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.MetallicRoughnessTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.EmissiveTexture?.TexCoordSet ?? 0, 0, 1)),
                AlbedoTextureIndex = textureIndices.AlbedoTextureIndex,
                NormalTextureIndex = textureIndices.NormalTextureIndex,
                MetallicRoughnessTextureIndex = textureIndices.MetallicRoughnessTextureIndex,
                EmissiveTextureIndex = textureIndices.EmissiveTextureIndex,
                FeatureFlags = material.FeatureFlags,
                ExtensionDataIndex = -1,
                Reserved0 = 0u,
                Reserved1 = 0u
            };
        }

        public static GPUMaterialExtensionData BuildGpuMaterialExtensionData(
            ModelMaterial material,
            MaterialExtensionTextureIndices textureIndices)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            float attenuationDistance = float.IsFinite(material.AttenuationDistance)
                ? Math.Max(material.AttenuationDistance, 0f)
                : 0f;

            return new GPUMaterialExtensionData
            {
                Clearcoat = new CoreVector4(
                    Math.Clamp(material.ClearcoatFactor, 0f, 1f),
                    Math.Clamp(material.ClearcoatRoughness, 0f, 1f),
                    Math.Clamp(material.ClearcoatNormalScale, 0f, 4f),
                    Math.Clamp(material.EmissiveStrength, 0f, 128f)),
                SheenColor = new CoreVector4(
                    Math.Max(material.SheenColor.X, 0f),
                    Math.Max(material.SheenColor.Y, 0f),
                    Math.Max(material.SheenColor.Z, 0f),
                    Math.Clamp(material.SheenRoughness, 0f, 1f)),
                Anisotropy = new CoreVector4(
                    Math.Clamp(material.AnisotropyStrength, 0f, 1f),
                    material.AnisotropyRotation,
                    0f,
                    0f),
                Transmission = new CoreVector4(
                    Math.Clamp(material.TransmissionFactor, 0f, 1f),
                    Math.Clamp(material.Ior, 1f, 3f),
                    Math.Max(material.ThicknessFactor, 0f),
                    attenuationDistance),
                AttenuationColor = new CoreVector4(
                    Math.Max(material.AttenuationColor.X, 0f),
                    Math.Max(material.AttenuationColor.Y, 0f),
                    Math.Max(material.AttenuationColor.Z, 0f),
                    0f),
                Subsurface = new CoreVector4(
                    Math.Max(material.SubsurfaceColor.X, 0f),
                    Math.Max(material.SubsurfaceColor.Y, 0f),
                    Math.Max(material.SubsurfaceColor.Z, 0f),
                    Math.Clamp(material.SubsurfaceStrength, 0f, 1f)),
                SpecularColor = new CoreVector4(
                    Math.Max(material.SpecularColor.X, 0f),
                    Math.Max(material.SpecularColor.Y, 0f),
                    Math.Max(material.SpecularColor.Z, 0f),
                    Math.Clamp(material.SpecularFactor, 0f, 1f)),
                Iridescence = new CoreVector4(
                    Math.Clamp(material.IridescenceFactor, 0f, 1f),
                    Math.Clamp(material.IridescenceIor, 1f, 3f),
                    Math.Max(material.IridescenceThicknessMinimum, 0f),
                    Math.Max(material.IridescenceThicknessMaximum, 0f)),
                Dispersion = new CoreVector4(
                    Math.Max(material.Dispersion, 0f),
                    0f,
                    0f,
                    0f),
                ClearcoatOffsetScale = ToOffsetScale(material.ClearcoatTexture),
                ClearcoatRoughnessOffsetScale = ToOffsetScale(material.ClearcoatRoughnessTexture),
                ClearcoatNormalOffsetScale = ToOffsetScale(material.ClearcoatNormalTexture),
                SheenColorOffsetScale = ToOffsetScale(material.SheenColorTexture),
                SheenRoughnessOffsetScale = ToOffsetScale(material.SheenRoughnessTexture),
                AnisotropyOffsetScale = ToOffsetScale(material.AnisotropyTexture),
                TransmissionOffsetScale = ToOffsetScale(material.TransmissionTexture),
                ThicknessOffsetScale = ToOffsetScale(material.ThicknessTexture),
                SpecularOffsetScale = ToOffsetScale(material.SpecularTexture),
                SpecularColorOffsetScale = ToOffsetScale(material.SpecularColorTexture),
                IridescenceOffsetScale = ToOffsetScale(material.IridescenceTexture),
                IridescenceThicknessOffsetScale = ToOffsetScale(material.IridescenceThicknessTexture),
                SubsurfaceOffsetScale = ToOffsetScale(material.SubsurfaceTexture),
                ExtensionTextureRotations0 = new CoreVector4(
                    material.ClearcoatTexture?.RotationRadians ?? 0f,
                    material.ClearcoatRoughnessTexture?.RotationRadians ?? 0f,
                    material.ClearcoatNormalTexture?.RotationRadians ?? 0f,
                    material.SheenColorTexture?.RotationRadians ?? 0f),
                ExtensionTextureRotations1 = new CoreVector4(
                    material.SheenRoughnessTexture?.RotationRadians ?? 0f,
                    material.AnisotropyTexture?.RotationRadians ?? 0f,
                    material.TransmissionTexture?.RotationRadians ?? 0f,
                    material.ThicknessTexture?.RotationRadians ?? 0f),
                ExtensionTextureRotations2 = new CoreVector4(
                    material.SpecularTexture?.RotationRadians ?? 0f,
                    material.SpecularColorTexture?.RotationRadians ?? 0f,
                    material.IridescenceTexture?.RotationRadians ?? 0f,
                    material.IridescenceThicknessTexture?.RotationRadians ?? 0f),
                ExtensionTextureRotations3 = new CoreVector4(
                    material.SubsurfaceTexture?.RotationRadians ?? 0f,
                    0f,
                    0f,
                    0f),
                ExtensionTextureTexCoordSets0 = new CoreVector4(
                    Math.Clamp(material.ClearcoatTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.ClearcoatRoughnessTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.ClearcoatNormalTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.SheenColorTexture?.TexCoordSet ?? 0, 0, 1)),
                ExtensionTextureTexCoordSets1 = new CoreVector4(
                    Math.Clamp(material.SheenRoughnessTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.AnisotropyTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.TransmissionTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.ThicknessTexture?.TexCoordSet ?? 0, 0, 1)),
                ExtensionTextureTexCoordSets2 = new CoreVector4(
                    Math.Clamp(material.SpecularTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.SpecularColorTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.IridescenceTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.IridescenceThicknessTexture?.TexCoordSet ?? 0, 0, 1)),
                ExtensionTextureTexCoordSets3 = new CoreVector4(
                    Math.Clamp(material.SubsurfaceTexture?.TexCoordSet ?? 0, 0, 1),
                    0f,
                    0f,
                    0f),
                ClearcoatTextureIndex = textureIndices.ClearcoatTextureIndex,
                ClearcoatRoughnessTextureIndex = textureIndices.ClearcoatRoughnessTextureIndex,
                ClearcoatNormalTextureIndex = textureIndices.ClearcoatNormalTextureIndex,
                SheenColorTextureIndex = textureIndices.SheenColorTextureIndex,
                SheenRoughnessTextureIndex = textureIndices.SheenRoughnessTextureIndex,
                AnisotropyTextureIndex = textureIndices.AnisotropyTextureIndex,
                TransmissionTextureIndex = textureIndices.TransmissionTextureIndex,
                ThicknessTextureIndex = textureIndices.ThicknessTextureIndex,
                SubsurfaceTextureIndex = textureIndices.SubsurfaceTextureIndex,
                SpecularTextureIndex = textureIndices.SpecularTextureIndex,
                SpecularColorTextureIndex = textureIndices.SpecularColorTextureIndex,
                IridescenceTextureIndex = textureIndices.IridescenceTextureIndex,
                IridescenceThicknessTextureIndex = textureIndices.IridescenceThicknessTextureIndex,
                Padding0 = 0,
                Padding1 = 0,
                Padding2 = 0,
                Padding3 = 0
            };
        }

        private static CoreVector4 ToOffsetScale(ModelTextureSlot? slot)
        {
            return slot == null
                ? new CoreVector4(0f, 0f, 1f, 1f)
                : new CoreVector4(slot.Offset.X, slot.Offset.Y, slot.Scale.X, slot.Scale.Y);
        }

        private static float ToGpuAlphaModeCode(ModelAlphaMode alphaMode)
        {
            return alphaMode switch
            {
                ModelAlphaMode.Mask => MaterialRenderMode.Mask.ToGpuAlphaModeCode(),
                ModelAlphaMode.Blend => MaterialRenderMode.Blend.ToGpuAlphaModeCode(),
                _ => MaterialRenderMode.Opaque.ToGpuAlphaModeCode()
            };
        }

        public static MaterialRenderMetadata BuildMaterialRenderMetadata(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            MaterialSurfaceFlags flags = MaterialSurfaceFlags.ReceivesShadows;
            if (material.DoubleSided)
                flags |= MaterialSurfaceFlags.DoubleSided;
            if (material.IsGeometryDecal)
                flags |= MaterialSurfaceFlags.GeometryDecal;

            return new MaterialRenderMetadata
            {
                BlendMode = ((MaterialFeatureFlags)material.FeatureFlags).RequiresTransparentPass()
                    ? MaterialBlendMode.AlphaBlend
                    : material.AlphaMode switch
                {
                    ModelAlphaMode.Mask => MaterialBlendMode.Mask,
                    ModelAlphaMode.Blend => MaterialBlendMode.AlphaBlend,
                    _ => MaterialBlendMode.Opaque
                },
                SurfaceFlags = flags,
                AlphaCutoff = Math.Clamp(material.AlphaCutoff, 0f, 1f),
                DecalLayer = material.DecalLayer,
                DecalDepthBias = Math.Clamp(material.DecalDepthBias, 0f, 0.01f)
            };
        }

        private static bool ShouldSampleOcclusionFromMetallicRoughnessTexture(ModelMaterial material)
        {
            if (material.MetallicRoughnessTexture?.Source != null && material.OcclusionTexture?.Source != null)
            {
                return string.Equals(
                    material.MetallicRoughnessTexture.Source.CacheIdentity,
                    material.OcclusionTexture.Source.CacheIdentity,
                    StringComparison.Ordinal);
            }

            return !string.IsNullOrWhiteSpace(material.MetallicRoughnessTexturePath) &&
                   !string.IsNullOrWhiteSpace(material.OcclusionTexturePath) &&
                   string.Equals(
                       Path.GetFullPath(material.MetallicRoughnessTexturePath),
                       Path.GetFullPath(material.OcclusionTexturePath),
                       StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldGenerateAlbedoMipmaps(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            return material.AlphaMode != ModelAlphaMode.Blend;
        }

        private TextureHandle ResolveTextureHandle(
            ModelTextureSlot? textureSlot,
            string? texturePath,
            TextureHandle fallback,
            ref int defaultSubstitutions,
            bool generateMipmaps,
            bool srgb)
        {
            if (!fallback.IsValid)
                throw new InvalidOperationException("Default textures must be initialized before material upload.");

            if (textureSlot?.Source != null)
            {
                bool slotSrgb = textureSlot.ColorSpace == TextureColorSpace.Srgb;
                TextureHandle slotTexture = _textureManager.LoadTexture(
                    textureSlot.Source,
                    textureSlot.Sampler,
                    generateMipmaps,
                    slotSrgb);
                return slotTexture.IsValid ? slotTexture : fallback;
            }

            if (!string.IsNullOrWhiteSpace(texturePath) && !File.Exists(Path.GetFullPath(texturePath)))
            {
                string fullPath = Path.GetFullPath(texturePath);
                throw new FileNotFoundException($"Imported material texture was not found: {fullPath}", fullPath);
            }

            bool useFallback = string.IsNullOrWhiteSpace(texturePath);
            TextureHandle texture = _textureManager.LoadOptionalTextureFromFile(
                texturePath,
                fallback,
                generateMipmaps: generateMipmaps,
                srgb: srgb);

            if (useFallback || texture == fallback)
                defaultSubstitutions++;

            return texture;
        }

        private void RegisterModelMaterialLifetime(Model model, IReadOnlyList<MaterialHandle> materials)
        {
            bool released = false;
            model.AddDisposeAction(() =>
            {
                if (released)
                    return;

                released = true;
                foreach (MaterialHandle material in materials)
                    _materialManager.ReleaseMaterial(material);
            });
        }

        private static void AddDynamicTextureIndex(HashSet<int> indices, int textureIndex)
        {
            if (textureIndex >= BindlessIndex.FirstDynamicTextureIndex)
                indices.Add(textureIndex);
        }

        private sealed record MaterialUploadResult(
            MaterialHandle[] Materials,
            HashSet<int> DynamicTextureIndices,
            int DefaultWhiteSubstitutions,
            int DefaultNormalSubstitutions,
            int DefaultBlackSubstitutions,
            int BlendMaterialCount);
    }

    public readonly record struct MaterialTextureIndices(
        int AlbedoTextureIndex,
        int NormalTextureIndex,
        int MetallicRoughnessTextureIndex,
        int EmissiveTextureIndex);

    public readonly record struct MaterialExtensionTextureIndices(
        int ClearcoatTextureIndex,
        int ClearcoatRoughnessTextureIndex,
        int ClearcoatNormalTextureIndex,
        int SheenColorTextureIndex,
        int SheenRoughnessTextureIndex,
        int AnisotropyTextureIndex,
        int TransmissionTextureIndex,
        int ThicknessTextureIndex,
        int SubsurfaceTextureIndex,
        int SpecularTextureIndex,
        int SpecularColorTextureIndex,
        int IridescenceTextureIndex,
        int IridescenceThicknessTextureIndex);

    internal sealed record MaterialTextureBindings(
        MaterialTextureIndices TextureIndices,
        MaterialExtensionTextureIndices ExtensionTextureIndices,
        IReadOnlyList<TextureHandle> TextureHandles);
}
