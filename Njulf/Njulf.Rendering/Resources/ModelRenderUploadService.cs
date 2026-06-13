using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Njulf.Assets;
using Njulf.Core.Animation;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
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
                        JointIndices0 = modelMesh.JointIndices0,
                        JointWeights0 = modelMesh.JointWeights0,
                        Indices = modelMesh.Indices,
                        BoundingBox = modelMesh.BoundingBox,
                        BoundingSphere = modelMesh.BoundingSphere
                    }
                };

            var meshRegistrations = new MeshManager.MeshRegistrationData[subMeshes.Count];
            var subMeshMaterialIndices = new int[subMeshes.Count];
            var subMeshNames = new string[subMeshes.Count];
            for (int i = 0; i < subMeshes.Count; i++)
            {
                ModelSubMesh subMesh = subMeshes[i];
                ValidateSubMesh(subMesh, nameof(modelMesh));

                GPUVertex[] vertices = BuildGpuVertices(subMesh);
                GPUVertexSkinningData[] skinningData = BuildGpuSkinningData(subMesh, model);
                meshRegistrations[i] = new MeshManager.MeshRegistrationData(
                    vertices,
                    subMesh.Indices,
                    generateMeshlets: true,
                    skinningData: skinningData.Length == 0 ? null : skinningData);
                subMeshMaterialIndices[i] = ResolveSubMeshMaterialIndex(subMesh, materials.Length);
                subMeshNames[i] = string.IsNullOrWhiteSpace(subMesh.Name) ? model.Name : subMesh.Name;
            }

            MeshHandle[] meshHandles = _meshManager.RegisterMeshes(meshRegistrations);
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

        private static GPUVertex[] BuildGpuVertices(ModelMesh modelMesh)
        {
            Vector3[] fallbackNormals = modelMesh.Normals.Length == modelMesh.Vertices.Length
                ? Array.Empty<Vector3>()
                : ComputeNormals(modelMesh.Vertices, modelMesh.Indices);

            var vertices = new GPUVertex[modelMesh.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                CoreVector3 normal = modelMesh.Normals.Length == modelMesh.Vertices.Length
                    ? NormalizeOrDefault(modelMesh.Normals[i], new CoreVector3(0f, 0f, 1f))
                    : ToCoreVector(fallbackNormals[i]);

                CoreVector3 tangent = modelMesh.Tangents.Length == modelMesh.Vertices.Length
                    ? NormalizeOrDefault(modelMesh.Tangents[i], new CoreVector3(1f, 0f, 0f))
                    : new CoreVector3(1f, 0f, 0f);
                CoreVector3 bitangent = modelMesh.Bitangents.Length == modelMesh.Vertices.Length
                    ? NormalizeOrDefault(modelMesh.Bitangents[i], CoreVector3.Zero)
                    : CoreVector3.Zero;
                float tangentHandedness = CalculateTangentHandedness(normal, tangent, bitangent);

                CoreVector2 texCoord = modelMesh.TexCoords.Length == modelMesh.Vertices.Length
                    ? modelMesh.TexCoords[i]
                    : CoreVector2.Zero;

                vertices[i] = new GPUVertex
                {
                    Position = modelMesh.Vertices[i],
                    Padding0 = 0f,
                    Normal = normal,
                    Padding1 = 0f,
                    TexCoord = texCoord,
                    TexCoord2 = CoreVector2.Zero,
                    Tangent = new CoreVector4(tangent.X, tangent.Y, tangent.Z, tangentHandedness)
                };
            }

            return vertices;
        }

        private static GPUVertex[] BuildGpuVertices(ModelSubMesh subMesh)
        {
            Vector3[] fallbackNormals = subMesh.Normals.Length == subMesh.Vertices.Length
                ? Array.Empty<Vector3>()
                : ComputeNormals(subMesh.Vertices, subMesh.Indices);

            var vertices = new GPUVertex[subMesh.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                CoreVector3 normal = subMesh.Normals.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Normals[i], new CoreVector3(0f, 0f, 1f))
                    : ToCoreVector(fallbackNormals[i]);

                CoreVector3 tangent = subMesh.Tangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Tangents[i], new CoreVector3(1f, 0f, 0f))
                    : new CoreVector3(1f, 0f, 0f);
                CoreVector3 bitangent = subMesh.Bitangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Bitangents[i], CoreVector3.Zero)
                    : CoreVector3.Zero;
                float tangentHandedness = CalculateTangentHandedness(normal, tangent, bitangent);

                CoreVector2 texCoord = subMesh.TexCoords.Length == subMesh.Vertices.Length
                    ? subMesh.TexCoords[i]
                    : CoreVector2.Zero;

                vertices[i] = new GPUVertex
                {
                    Position = subMesh.Vertices[i],
                    Padding0 = 0f,
                    Normal = normal,
                    Padding1 = 0f,
                    TexCoord = texCoord,
                    TexCoord2 = CoreVector2.Zero,
                    Tangent = new CoreVector4(tangent.X, tangent.Y, tangent.Z, tangentHandedness)
                };
            }

            return vertices;
        }

        private static GPUVertexSkinningData[] BuildGpuSkinningData(ModelSubMesh subMesh, Model model)
        {
            if (subMesh.SkinIndex < 0)
                return Array.Empty<GPUVertexSkinningData>();
            if (subMesh.SkinIndex >= model.Skins.Count)
                throw new InvalidOperationException(
                    $"Imported submesh '{subMesh.Name}' references skin index {subMesh.SkinIndex}, but the model only has {model.Skins.Count} skins.");
            if (subMesh.JointIndices0.Length != subMesh.Vertices.Length || subMesh.JointWeights0.Length != subMesh.Vertices.Length)
                throw new InvalidOperationException(
                    $"Skinned submesh '{subMesh.Name}' must provide JOINTS_0 and WEIGHTS_0 streams for every vertex.");

            int jointCount = model.Skins[subMesh.SkinIndex].JointIndices.Count;
            var skinningData = new GPUVertexSkinningData[subMesh.Vertices.Length];
            for (int i = 0; i < skinningData.Length; i++)
            {
                VertexJointIndices joints = subMesh.JointIndices0[i];
                VertexJointWeights weights = subMesh.JointWeights0[i].Normalized();

                ValidateJointIndex(subMesh.Name, i, joints.X, jointCount);
                ValidateJointIndex(subMesh.Name, i, joints.Y, jointCount);
                ValidateJointIndex(subMesh.Name, i, joints.Z, jointCount);
                ValidateJointIndex(subMesh.Name, i, joints.W, jointCount);

                skinningData[i] = new GPUVertexSkinningData
                {
                    Joint0 = joints.X,
                    Joint1 = joints.Y,
                    Joint2 = joints.Z,
                    Joint3 = joints.W,
                    Weight0 = weights.X,
                    Weight1 = weights.Y,
                    Weight2 = weights.Z,
                    Weight3 = weights.W
                };
            }

            return skinningData;
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

                GPUMaterialData gpuMaterial = BuildGpuMaterialData(material, textureBindings.TextureIndices);
                MaterialRenderMetadata metadata = BuildMaterialRenderMetadata(material);
                materials[i] = _materialManager.RegisterMaterial(gpuMaterial, metadata, textureBindings.TextureHandles);
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
                material.AlbedoTexturePath,
                _textureManager.DefaultWhiteTexture,
                ref defaultWhiteSubstitutions,
                generateMipmaps: ShouldGenerateAlbedoMipmaps(material),
                srgb: true);
            TextureHandle normalTexture = ResolveTextureHandle(
                material.NormalTexturePath,
                _textureManager.DefaultNormalTexture,
                ref defaultNormalSubstitutions,
                generateMipmaps: true,
                srgb: false);

            TextureHandle metallicRoughnessTexture = ResolveTextureHandle(
                material.MetallicRoughnessTexturePath,
                _textureManager.DefaultBlackTexture,
                ref defaultBlackSubstitutions,
                generateMipmaps: true,
                srgb: false);
            TextureHandle emissiveTexture = ResolveTextureHandle(
                material.EmissiveTexturePath,
                _textureManager.DefaultBlackTexture,
                ref defaultBlackSubstitutions,
                generateMipmaps: true,
                srgb: true);

            return new MaterialTextureBindings(
                new MaterialTextureIndices(
                    _textureManager.GetBindlessTextureIndex(albedoTexture),
                    _textureManager.GetBindlessTextureIndex(normalTexture),
                    _textureManager.GetBindlessTextureIndex(metallicRoughnessTexture),
                    _textureManager.GetBindlessTextureIndex(emissiveTexture)),
                new[]
                {
                    albedoTexture,
                    normalTexture,
                    metallicRoughnessTexture,
                    emissiveTexture
                });
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
                TexCoordOffsetScale = new CoreVector4(0f, 0f, 1f, 1f),
                AlbedoTextureIndex = textureIndices.AlbedoTextureIndex,
                NormalTextureIndex = textureIndices.NormalTextureIndex,
                MetallicRoughnessTextureIndex = textureIndices.MetallicRoughnessTextureIndex,
                EmissiveTextureIndex = textureIndices.EmissiveTextureIndex
            };
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
                BlendMode = material.AlphaMode switch
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
            string? texturePath,
            TextureHandle fallback,
            ref int defaultSubstitutions,
            bool generateMipmaps,
            bool srgb)
        {
            if (!fallback.IsValid)
                throw new InvalidOperationException("Default textures must be initialized before material upload.");

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

        private static Vector3[] ComputeNormals(CoreVector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];

            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 p0 = ToNumericsVector(positions[i0]);
                Vector3 p1 = ToNumericsVector(positions[i1]);
                Vector3 p2 = ToNumericsVector(positions[i2]);

                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
                if (faceNormal.LengthSquared() > 0f)
                    faceNormal = Vector3.Normalize(faceNormal);

                normals[i0] += faceNormal;
                normals[i1] += faceNormal;
                normals[i2] += faceNormal;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = normals[i].LengthSquared() > 0f
                    ? Vector3.Normalize(normals[i])
                    : Vector3.UnitZ;
            }

            return normals;
        }

        private static CoreVector3 NormalizeOrDefault(CoreVector3 value, CoreVector3 fallback)
        {
            float lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
            if (lengthSquared <= float.Epsilon)
                return fallback;

            float inverseLength = 1f / MathF.Sqrt(lengthSquared);
            return new CoreVector3(value.X * inverseLength, value.Y * inverseLength, value.Z * inverseLength);
        }

        private static float CalculateTangentHandedness(CoreVector3 normal, CoreVector3 tangent, CoreVector3 bitangent)
        {
            if (bitangent.X * bitangent.X + bitangent.Y * bitangent.Y + bitangent.Z * bitangent.Z <= float.Epsilon)
                return 1f;

            CoreVector3 derivedBitangent = CoreVector3.Cross(normal, tangent);
            float sign = CoreVector3.Dot(derivedBitangent, bitangent);
            return sign < 0f ? -1f : 1f;
        }

        private static Vector3 ToNumericsVector(CoreVector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static CoreVector3 ToCoreVector(Vector3 value)
        {
            return new CoreVector3(value.X, value.Y, value.Z);
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

    internal sealed record MaterialTextureBindings(
        MaterialTextureIndices TextureIndices,
        IReadOnlyList<TextureHandle> TextureHandles);
}
