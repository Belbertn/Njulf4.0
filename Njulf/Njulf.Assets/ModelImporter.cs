using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Njulf.Core.Math;
using Silk.NET.Assimp;
using Silk.NET.Core.Native;
using File = System.IO.File;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector4 = System.Numerics.Vector4;

namespace Njulf.Assets
{
    public class ModelImporter : IDisposable
    {
        private readonly Assimp _assimp;
        private bool _disposed;

        public ModelImporter()
        {
            _assimp = Assimp.GetApi();
        }

        public ModelMesh Import(string path, ImporterOptions? options = null)
        {
            options ??= ImporterOptions.Default!;

            if (!File.Exists(path))
                throw new FileNotFoundException($"Model file was not found: {Path.GetFullPath(path)}", Path.GetFullPath(path));

            string fullPath = Path.GetFullPath(path);
            GltfAssetManifest? gltfManifest = LoadAndValidateGltfManifest(fullPath);

            var postProcess = PostProcessSteps.Triangulate;
            if (options.GenerateNormals)
                postProcess |= PostProcessSteps.GenerateNormals;
            if (options.GenerateTangents)
                postProcess |= PostProcessSteps.CalculateTangentSpace;
            if (options.JoinIdenticalVertices)
                postProcess |= PostProcessSteps.JoinIdenticalVertices;
            if (options.FlipUVs)
                postProcess |= PostProcessSteps.FlipUVs;
            if (options.SortByPrimitiveType)
                postProcess |= PostProcessSteps.SortByPrimitiveType;

            unsafe
            {
                var scene = _assimp.ImportFile(fullPath, (uint)postProcess);
                if (scene == null)
                {
                    string error = SilkMarshal.PtrToString((nint)_assimp.GetErrorString()) ?? "Unknown error";
                    throw new Exception($"Failed to import model '{fullPath}': {error}");
                }

                try
                {
                    if (((SceneFlags)scene->MFlags & SceneFlags.Incomplete) != 0)
                        throw new Exception("Scene import incomplete");

                    if (scene->MRootNode == null)
                        throw new Exception("No root node in scene");

                    var mesh = ProcessScene(scene, fullPath, options, gltfManifest);
                    return mesh;
                }
                finally
                {
                    _assimp.ReleaseImport(scene);
                }
            }
        }

        private unsafe ModelMesh ProcessScene(Scene* scene, string path, ImporterOptions options, GltfAssetManifest? gltfManifest)
        {
            var mesh = new ModelMesh
            {
                Name = Path.GetFileNameWithoutExtension(path)
            };

            AccumulateNodeMeshTotals(scene, scene->MRootNode, out int vertexCapacity, out int indexCapacity);
            var vertices = new List<Vector3>(vertexCapacity);
            var normals = new List<Vector3>(vertexCapacity);
            var tangents = new List<Vector3>(vertexCapacity);
            var bitangents = new List<Vector3>(vertexCapacity);
            var texCoords = new List<Vector2>(vertexCapacity);
            var indices = new List<uint>(indexCapacity);
            mesh.Materials.AddRange(ProcessMaterials(scene, path, gltfManifest));

            ProcessNode(
                scene,
                scene->MRootNode,
                NumericsMatrix4x4.Identity,
                options,
                mesh,
                vertices,
                normals,
                tangents,
                bitangents,
                texCoords,
                indices);

            mesh.Vertices = vertices.ToArray();
            mesh.Normals = normals.ToArray();
            mesh.Tangents = tangents.ToArray();
            mesh.Bitangents = bitangents.ToArray();
            mesh.TexCoords = texCoords.ToArray();
            mesh.Indices = indices.ToArray();

            ComputeBoundingVolume(vertices, out var bbox, out var bsphere);
            mesh.BoundingBox = bbox;
            mesh.BoundingSphere = bsphere;

            return mesh;
        }

        private static unsafe void AccumulateNodeMeshTotals(Scene* scene, Node* node, out int vertexCount, out int indexCount)
        {
            vertexCount = 0;
            indexCount = 0;
            AccumulateNodeMeshTotalsRecursive(scene, node, ref vertexCount, ref indexCount);
        }

        private static unsafe void AccumulateNodeMeshTotalsRecursive(Scene* scene, Node* node, ref int vertexCount, ref int indexCount)
        {
            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                Mesh* mesh = scene->MMeshes[node->MMeshes[i]];
                if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0)
                    continue;
                if ((PrimitiveType)mesh->MPrimitiveTypes != PrimitiveType.Triangle)
                    continue;

                vertexCount = checked(vertexCount + (int)mesh->MNumVertices);
                indexCount = checked(indexCount + (int)mesh->MNumFaces * 3);
            }

            for (uint i = 0; i < node->MNumChildren; i++)
                AccumulateNodeMeshTotalsRecursive(scene, node->MChildren[i], ref vertexCount, ref indexCount);
        }

        private unsafe void ProcessNode(
            Scene* scene,
            Node* node,
            NumericsMatrix4x4 parentTransform,
            ImporterOptions options,
            ModelMesh model,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector3> tangents,
            List<Vector3> bitangents,
            List<Vector2> texCoords,
            List<uint> indices)
        {
            NumericsMatrix4x4 nodeTransform = ToEngineTransform(node->MTransformation) * parentTransform;

            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                uint meshIndex = node->MMeshes[i];
                AppendMeshInstance(
                    scene->MMeshes[meshIndex],
                    node,
                    meshIndex,
                    nodeTransform,
                    options,
                    model,
                    vertices,
                    normals,
                    tangents,
                    bitangents,
                    texCoords,
                    indices);
            }

            for (uint i = 0; i < node->MNumChildren; i++)
            {
                ProcessNode(
                    scene,
                    node->MChildren[i],
                    nodeTransform,
                    options,
                    model,
                    vertices,
                    normals,
                    tangents,
                    bitangents,
                    texCoords,
                    indices);
            }
        }

        private unsafe void AppendMeshInstance(
            Mesh* aiMesh,
            Node* node,
            uint meshIndex,
            NumericsMatrix4x4 transform,
            ImporterOptions options,
            ModelMesh model,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector3> tangents,
            List<Vector3> bitangents,
            List<Vector2> texCoords,
            List<uint> indices)
        {
            if ((PrimitiveType)aiMesh->MPrimitiveTypes != PrimitiveType.Triangle)
                throw new Exception("Mesh is not triangulated. Enable Triangulate post-process step.");

            if (aiMesh->MNumVertices == 0 || aiMesh->MNumFaces == 0)
                return;

            string nodeName = node->MName.AsString;
            string meshName = aiMesh->MName.AsString;
            var subMesh = new ModelSubMesh
            {
                Name = CreateSubMeshName(model.Name, nodeName, meshName, meshIndex),
                MaterialIndex = aiMesh->MMaterialIndex < model.Materials.Count
                    ? (int)aiMesh->MMaterialIndex
                    : 0
            };

            int subVertexCapacity = checked((int)aiMesh->MNumVertices);
            int subIndexCapacity = checked((int)aiMesh->MNumFaces * 3);
            var subVertices = new List<Vector3>(subVertexCapacity);
            var subNormals = new List<Vector3>(subVertexCapacity);
            var subTangents = new List<Vector3>(subVertexCapacity);
            var subBitangents = new List<Vector3>(subVertexCapacity);
            var subTexCoords = new List<Vector2>(subVertexCapacity);
            var subIndices = new List<uint>(subIndexCapacity);

            NumericsMatrix4x4 normalTransform = NumericsMatrix4x4.Invert(transform, out NumericsMatrix4x4 inverseTransform)
                ? NumericsMatrix4x4.Transpose(inverseTransform)
                : transform;

            for (uint v = 0; v < aiMesh->MNumVertices; v++)
            {
                var pos = aiMesh->MVertices[(int)v];
                var position = TransformPosition(new Vector3(pos.X, pos.Y, pos.Z), transform, options.GlobalScale);
                vertices.Add(position);
                subVertices.Add(position);

                var normal = aiMesh->MNormals != null ? aiMesh->MNormals[(int)v] : default;
                var normalValue = NormalizeOrDefault(TransformDirection(new Vector3(normal.X, normal.Y, normal.Z), normalTransform));
                normals.Add(normalValue);
                subNormals.Add(normalValue);

                var tangent = aiMesh->MTangents != null ? aiMesh->MTangents[(int)v] : default;
                var tangentValue = NormalizeOrDefault(TransformDirection(new Vector3(tangent.X, tangent.Y, tangent.Z), transform));
                tangents.Add(tangentValue);
                subTangents.Add(tangentValue);

                var bitangent = aiMesh->MBitangents != null ? aiMesh->MBitangents[(int)v] : default;
                var bitangentValue = NormalizeOrDefault(TransformDirection(new Vector3(bitangent.X, bitangent.Y, bitangent.Z), transform));
                bitangents.Add(bitangentValue);
                subBitangents.Add(bitangentValue);

                Vector3 tc = default;
                if (aiMesh->MTextureCoords[0] != null)
                {
                    var tcSrc = aiMesh->MTextureCoords[0][(int)v];
                    tc = new Vector3(tcSrc.X, tcSrc.Y, tcSrc.Z);
                }

                var texCoord = new Vector2(tc.X, tc.Y);
                texCoords.Add(texCoord);
                subTexCoords.Add(texCoord);
            }

            int baseVertex = vertices.Count - subVertices.Count;
            for (uint f = 0; f < aiMesh->MNumFaces; f++)
            {
                var face = aiMesh->MFaces[f];
                if (face.MNumIndices != 3)
                    throw new Exception("Face is not a triangle. Enable Triangulate post-process step.");

                if (options.FlipWindingOrder)
                {
                    indices.Add((uint)(baseVertex + face.MIndices[2]));
                    indices.Add((uint)(baseVertex + face.MIndices[1]));
                    indices.Add((uint)(baseVertex + face.MIndices[0]));
                    subIndices.Add(face.MIndices[2]);
                    subIndices.Add(face.MIndices[1]);
                    subIndices.Add(face.MIndices[0]);
                }
                else
                {
                    indices.Add((uint)(baseVertex + face.MIndices[0]));
                    indices.Add((uint)(baseVertex + face.MIndices[1]));
                    indices.Add((uint)(baseVertex + face.MIndices[2]));
                    subIndices.Add(face.MIndices[0]);
                    subIndices.Add(face.MIndices[1]);
                    subIndices.Add(face.MIndices[2]);
                }
            }

            subMesh.Vertices = subVertices.ToArray();
            subMesh.Normals = subNormals.ToArray();
            subMesh.Tangents = subTangents.ToArray();
            subMesh.Bitangents = subBitangents.ToArray();
            subMesh.TexCoords = subTexCoords.ToArray();
            subMesh.Indices = subIndices.ToArray();
            ComputeBoundingVolume(subVertices, out var subBox, out var subSphere);
            subMesh.BoundingBox = subBox;
            subMesh.BoundingSphere = subSphere;
            model.SubMeshes.Add(subMesh);
        }

        private static string CreateSubMeshName(string modelName, string nodeName, string meshName, uint meshIndex)
        {
            if (!string.IsNullOrWhiteSpace(nodeName))
                return nodeName;
            if (!string.IsNullOrWhiteSpace(meshName))
                return meshName;

            return $"{modelName}_mesh_{meshIndex}";
        }

        private static NumericsMatrix4x4 ToEngineTransform(NumericsMatrix4x4 assimpTransform)
        {
            return NumericsMatrix4x4.Transpose(assimpTransform);
        }

        private unsafe List<ModelMaterial> ProcessMaterials(Scene* scene, string modelPath, GltfAssetManifest? gltfManifest)
        {
            var materials = new List<ModelMaterial>();
            string modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? AppContext.BaseDirectory;

            for (uint i = 0; i < scene->MNumMaterials; i++)
            {
                Material* material = scene->MMaterials[i];
                var imported = new ModelMaterial
                {
                    Name = GetMaterialString(material, Assimp.MaterialName, $"Material_{i}"),
                    Albedo = ToCoreVector(GetMaterialColor(material, Assimp.MaterialColorDiffuse, new NumericsVector4(1f, 1f, 1f, 1f))),
                    Emissive = ToCoreVector(GetMaterialColor(material, Assimp.MaterialColorEmissive, NumericsVector4.Zero)),
                    Metallic = GetMaterialFloat(material, 0f, "$mat.metallicFactor", "$mat.gltf.pbrMetallicRoughness.metallicFactor"),
                    Roughness = GetMaterialFloat(material, 1f, "$mat.roughnessFactor", "$mat.gltf.pbrMetallicRoughness.roughnessFactor"),
                    AmbientOcclusion = 1f,
                    NormalScale = GetMaterialFloat(material, 1f, "$mat.normalScale"),
                    AlbedoTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.BaseColor, TextureType.Diffuse), "base-color"),
                    NormalTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.NormalCamera, TextureType.Normals, TextureType.Height), "normal"),
                    MetallicRoughnessTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.Metalness, TextureType.DiffuseRoughness, TextureType.AmbientOcclusion, TextureType.Unknown), "metallic-roughness/occlusion"),
                    EmissiveTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.EmissionColor, TextureType.Emissive), "emissive")
                };

                if (gltfManifest != null && i < gltfManifest.Materials.Count)
                    ApplyGltfMaterial(imported, gltfManifest.Materials[(int)i]);

                materials.Add(imported);
            }

            if (materials.Count == 0)
                materials.Add(ModelMaterial.Default);

            return materials;
        }

        private unsafe string GetMaterialString(Material* material, string key, string fallback)
        {
            AssimpString value = default;
            return _assimp.GetMaterialString(material, key, 0, 0, &value) == Return.Success &&
                   !string.IsNullOrWhiteSpace(value.AsString)
                ? value.AsString
                : fallback;
        }

        private unsafe NumericsVector4 GetMaterialColor(Material* material, string key, NumericsVector4 fallback)
        {
            NumericsVector4 value = fallback;
            return _assimp.GetMaterialColor(material, key, 0, 0, &value) == Return.Success
                ? value
                : fallback;
        }

        private unsafe float GetMaterialFloat(Material* material, float fallback, params string[] keys)
        {
            foreach (string key in keys)
            {
                float value = fallback;
                uint count = 1;
                if (_assimp.GetMaterialFloatArray(material, key, 0, 0, &value, &count) == Return.Success && count > 0)
                    return value;
            }

            return fallback;
        }

        private unsafe string? GetFirstTexturePath(Material* material, params TextureType[] textureTypes)
        {
            foreach (TextureType textureType in textureTypes.Distinct())
            {
                if (_assimp.GetMaterialTextureCount(material, textureType) == 0)
                    continue;

                AssimpString path = default;
                Return result = _assimp.GetMaterialTexture(
                    material,
                    textureType,
                    0,
                    &path,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);

                if (result == Return.Success && !string.IsNullOrWhiteSpace(path.AsString))
                    return path.AsString;
            }

            return default;
        }

        private static string? ResolveTexturePath(string modelDirectory, string? texturePath, string textureRole)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return default;

            if (texturePath.StartsWith("*", StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"Embedded {textureRole} texture '{texturePath}' is not supported. Use an external image file.");
            }

            if (texturePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Embedded data URI {textureRole} textures are not supported. Use an external image file.");
            }

            string decodedPath = Uri.UnescapeDataString(texturePath.Replace('\\', Path.DirectorySeparatorChar));
            return Path.IsPathRooted(decodedPath)
                ? Path.GetFullPath(decodedPath)
                : Path.GetFullPath(Path.Combine(modelDirectory, decodedPath));
        }

        private static void ApplyGltfMaterial(ModelMaterial target, GltfMaterial material)
        {
            target.Name = string.IsNullOrWhiteSpace(material.Name) ? target.Name : material.Name;
            target.Albedo = material.BaseColorFactor ?? target.Albedo;
            target.Emissive = material.EmissiveFactor ?? target.Emissive;
            target.Metallic = material.MetallicFactor ?? target.Metallic;
            target.Roughness = material.RoughnessFactor ?? target.Roughness;
            target.AmbientOcclusion = material.OcclusionStrength ?? target.AmbientOcclusion;
            target.NormalScale = material.NormalScale ?? target.NormalScale;
            target.AlphaMode = material.AlphaMode;
            target.AlphaCutoff = material.AlphaCutoff ?? target.AlphaCutoff;
            target.DoubleSided = material.DoubleSided;
            target.AlbedoTexturePath = material.BaseColorTexturePath ?? target.AlbedoTexturePath;
            target.NormalTexturePath = material.NormalTexturePath ?? target.NormalTexturePath;
            target.MetallicRoughnessTexturePath = material.MetallicRoughnessTexturePath ?? target.MetallicRoughnessTexturePath;
            target.OcclusionTexturePath = material.OcclusionTexturePath ?? target.OcclusionTexturePath;
            target.EmissiveTexturePath = material.EmissiveTexturePath ?? target.EmissiveTexturePath;
        }

        private static GltfAssetManifest? LoadAndValidateGltfManifest(string modelPath)
        {
            if (!string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
                return default;

            using FileStream stream = File.OpenRead(modelPath);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            JsonElement root = document.RootElement;
            string modelDirectory = Path.GetDirectoryName(modelPath) ?? AppContext.BaseDirectory;
            List<string?> imagePaths = ValidateGltfImages(root, modelDirectory);
            List<int?> textureSources = ReadGltfTextureSources(root);

            ValidateGltfBuffers(root, modelDirectory);

            var materials = new List<GltfMaterial>();
            if (root.TryGetProperty("materials", out JsonElement materialsElement) &&
                materialsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement materialElement in materialsElement.EnumerateArray())
                    materials.Add(ReadGltfMaterial(materialElement, textureSources, imagePaths));
            }

            return new GltfAssetManifest(materials);
        }

        private static void ValidateGltfBuffers(JsonElement root, string modelDirectory)
        {
            if (!root.TryGetProperty("buffers", out JsonElement buffersElement) ||
                buffersElement.ValueKind != JsonValueKind.Array)
                return;

            int index = 0;
            foreach (JsonElement bufferElement in buffersElement.EnumerateArray())
            {
                if (!bufferElement.TryGetProperty("uri", out JsonElement uriElement) ||
                    uriElement.ValueKind != JsonValueKind.String)
                {
                    throw new NotSupportedException(
                        $"glTF buffer {index} is embedded or missing a URI. External .bin buffers are required.");
                }

                string uri = uriElement.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"Embedded data URI glTF buffer {index} is not supported.");

                string absolutePath = ResolveExternalGltfPath(modelDirectory, uri);
                if (!File.Exists(absolutePath))
                    throw new FileNotFoundException($"Required external glTF buffer was not found: {absolutePath}", absolutePath);

                index++;
            }
        }

        private static List<string?> ValidateGltfImages(JsonElement root, string modelDirectory)
        {
            var imagePaths = new List<string?>();
            if (!root.TryGetProperty("images", out JsonElement imagesElement) ||
                imagesElement.ValueKind != JsonValueKind.Array)
                return imagePaths;

            int index = 0;
            foreach (JsonElement imageElement in imagesElement.EnumerateArray())
            {
                if (imageElement.TryGetProperty("bufferView", out _))
                {
                    throw new NotSupportedException(
                        $"glTF image {index} uses a bufferView. Embedded/buffer-view textures are not supported.");
                }

                if (!imageElement.TryGetProperty("uri", out JsonElement uriElement) ||
                    uriElement.ValueKind != JsonValueKind.String)
                {
                    throw new NotSupportedException(
                        $"glTF image {index} is embedded or missing a URI. External image files are required.");
                }

                string uri = uriElement.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"glTF image {index} uses an embedded data URI texture, which is not supported.");

                string absolutePath = ResolveExternalGltfPath(modelDirectory, uri);
                if (!File.Exists(absolutePath))
                    throw new FileNotFoundException($"Required external glTF image was not found: {absolutePath}", absolutePath);

                imagePaths.Add(absolutePath);
                index++;
            }

            return imagePaths;
        }

        private static List<int?> ReadGltfTextureSources(JsonElement root)
        {
            var textureSources = new List<int?>();
            if (!root.TryGetProperty("textures", out JsonElement texturesElement) ||
                texturesElement.ValueKind != JsonValueKind.Array)
                return textureSources;

            foreach (JsonElement textureElement in texturesElement.EnumerateArray())
            {
                textureSources.Add(textureElement.TryGetProperty("source", out JsonElement sourceElement) &&
                                   sourceElement.TryGetInt32(out int source)
                    ? source
                    : null);
            }

            return textureSources;
        }

        private static GltfMaterial ReadGltfMaterial(
            JsonElement materialElement,
            IReadOnlyList<int?> textureSources,
            IReadOnlyList<string?> imagePaths)
        {
            string? name = materialElement.TryGetProperty("name", out JsonElement nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            var material = new GltfMaterial
            {
                Name = name,
                AlphaMode = ReadAlphaMode(materialElement),
                AlphaCutoff = ReadFloat(materialElement, "alphaCutoff"),
                DoubleSided = ReadBool(materialElement, "doubleSided")
            };

            if (materialElement.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) &&
                pbr.ValueKind == JsonValueKind.Object)
            {
                material.BaseColorFactor = ReadVector4(pbr, "baseColorFactor");
                material.MetallicFactor = ReadFloat(pbr, "metallicFactor");
                material.RoughnessFactor = ReadFloat(pbr, "roughnessFactor");
                material.BaseColorTexturePath = ReadTexturePath(pbr, "baseColorTexture", textureSources, imagePaths);
                material.MetallicRoughnessTexturePath = ReadTexturePath(pbr, "metallicRoughnessTexture", textureSources, imagePaths);
            }

            material.NormalTexturePath = ReadTexturePath(materialElement, "normalTexture", textureSources, imagePaths);
            material.EmissiveTexturePath = ReadTexturePath(materialElement, "emissiveTexture", textureSources, imagePaths);
            material.OcclusionTexturePath = ReadTexturePath(materialElement, "occlusionTexture", textureSources, imagePaths);
            material.NormalScale = ReadNestedFloat(materialElement, "normalTexture", "scale");
            material.OcclusionStrength = ReadNestedFloat(materialElement, "occlusionTexture", "strength");
            material.EmissiveFactor = ReadVector3AsColor(materialElement, "emissiveFactor");

            return material;
        }

        private static string? ReadTexturePath(
            JsonElement owner,
            string propertyName,
            IReadOnlyList<int?> textureSources,
            IReadOnlyList<string?> imagePaths)
        {
            if (!owner.TryGetProperty(propertyName, out JsonElement textureInfo) ||
                textureInfo.ValueKind != JsonValueKind.Object ||
                !textureInfo.TryGetProperty("index", out JsonElement indexElement) ||
                !indexElement.TryGetInt32(out int textureIndex) ||
                textureIndex < 0 ||
                textureIndex >= textureSources.Count)
                return default;

            int? imageIndex = textureSources[textureIndex];
            if (!imageIndex.HasValue || imageIndex.Value < 0 || imageIndex.Value >= imagePaths.Count)
                return default;

            return imagePaths[imageIndex.Value];
        }

        private static ModelAlphaMode ReadAlphaMode(JsonElement materialElement)
        {
            if (!materialElement.TryGetProperty("alphaMode", out JsonElement alphaModeElement) ||
                alphaModeElement.ValueKind != JsonValueKind.String)
                return ModelAlphaMode.Opaque;

            return alphaModeElement.GetString() switch
            {
                "MASK" => ModelAlphaMode.Mask,
                "BLEND" => ModelAlphaMode.Blend,
                _ => ModelAlphaMode.Opaque
            };
        }

        private static bool ReadBool(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.True;
        }

        private static float? ReadFloat(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetSingle(out float result)
                ? result
                : null;
        }

        private static float? ReadNestedFloat(JsonElement owner, string objectName, string propertyName)
        {
            return owner.TryGetProperty(objectName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                ? ReadFloat(nested, propertyName)
                : null;
        }

        private static Vector4? ReadVector4(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() != 4)
                return default;

            float[] values = array.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        private static Vector4? ReadVector3AsColor(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() != 3)
                return default;

            float[] values = array.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Vector4(values[0], values[1], values[2], 1f);
        }

        private static string ResolveExternalGltfPath(string modelDirectory, string uri)
        {
            string decodedPath = Uri.UnescapeDataString(uri).Replace('\\', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(decodedPath)
                ? Path.GetFullPath(decodedPath)
                : Path.GetFullPath(Path.Combine(modelDirectory, decodedPath));
        }

        private static Vector4 ToCoreVector(NumericsVector4 value)
        {
            return new Vector4(value.X, value.Y, value.Z, value.W);
        }

        private static Vector3 TransformPosition(Vector3 position, NumericsMatrix4x4 transform, float globalScale)
        {
            return new Vector3(
                (position.X * transform.M11 + position.Y * transform.M21 + position.Z * transform.M31 + transform.M41) * globalScale,
                (position.X * transform.M12 + position.Y * transform.M22 + position.Z * transform.M32 + transform.M42) * globalScale,
                (position.X * transform.M13 + position.Y * transform.M23 + position.Z * transform.M33 + transform.M43) * globalScale);
        }

        private static Vector3 TransformDirection(Vector3 direction, NumericsMatrix4x4 transform)
        {
            return new Vector3(
                direction.X * transform.M11 + direction.Y * transform.M21 + direction.Z * transform.M31,
                direction.X * transform.M12 + direction.Y * transform.M22 + direction.Z * transform.M32,
                direction.X * transform.M13 + direction.Y * transform.M23 + direction.Z * transform.M33);
        }

        private static Vector3 NormalizeOrDefault(Vector3 value)
        {
            float lengthSquared = value.LengthSquared();
            if (lengthSquared <= float.Epsilon)
                return Vector3.Zero;

            return value / (float)System.Math.Sqrt(lengthSquared);
        }

        private static void ComputeBoundingVolume(List<Vector3> vertices, out BoundingBox bbox, out BoundingSphere bsphere)
        {
            if (vertices.Count == 0)
            {
                bbox = new BoundingBox();
                bsphere = new BoundingSphere();
                return;
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = System.Math.Min(min.X, v.X);
                min.Y = System.Math.Min(min.Y, v.Y);
                min.Z = System.Math.Min(min.Z, v.Z);
                max.X = System.Math.Max(max.X, v.X);
                max.Y = System.Math.Max(max.Y, v.Y);
                max.Z = System.Math.Max(max.Z, v.Z);
            }

            bbox = new BoundingBox(min, max);
            bsphere = BoundingSphere.FromBox(bbox);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }
    }

    public class ModelMesh
    {
        public string Name { get; set; } = "ModelMesh";
        public Vector3[] Vertices { get; set; }
        public Vector3[] Normals { get; set; }
        public Vector3[] Tangents { get; set; }
        public Vector3[] Bitangents { get; set; }
        public Vector2[] TexCoords { get; set; }
        public uint[] Indices { get; set; }
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
        public List<ModelSubMesh> SubMeshes { get; } = new();
        public List<ModelMaterial> Materials { get; } = new();

        public ModelMesh()
        {
            Vertices = Array.Empty<Vector3>();
            Normals = Array.Empty<Vector3>();
            Tangents = Array.Empty<Vector3>();
            Bitangents = Array.Empty<Vector3>();
            TexCoords = Array.Empty<Vector2>();
            Indices = Array.Empty<uint>();
        }
    }

    public sealed class ModelSubMesh
    {
        public string Name { get; set; } = "SubMesh";
        public int MaterialIndex { get; set; }
        public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Tangents { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Bitangents { get; set; } = Array.Empty<Vector3>();
        public Vector2[] TexCoords { get; set; } = Array.Empty<Vector2>();
        public uint[] Indices { get; set; } = Array.Empty<uint>();
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
    }

    public sealed class ModelMaterial
    {
        public static ModelMaterial Default => new ModelMaterial();

        public string Name { get; set; } = "DefaultMaterial";
        public Vector4 Albedo { get; set; } = new Vector4(1f, 1f, 1f, 1f);
        public Vector4 Emissive { get; set; } = Vector4.Zero;
        public float Metallic { get; set; } = 0f;
        public float Roughness { get; set; } = 1f;
        public float AmbientOcclusion { get; set; } = 1f;
        public float NormalScale { get; set; } = 1f;
        public ModelAlphaMode AlphaMode { get; set; } = ModelAlphaMode.Opaque;
        public float AlphaCutoff { get; set; } = 0.5f;
        public bool DoubleSided { get; set; }
        public bool IsGeometryDecal { get; set; }
        public int DecalLayer { get; set; }
        public float DecalDepthBias { get; set; }
        public string? AlbedoTexturePath { get; set; }
        public string? NormalTexturePath { get; set; }
        public string? MetallicRoughnessTexturePath { get; set; }
        public string? OcclusionTexturePath { get; set; }
        public string? EmissiveTexturePath { get; set; }
    }

    public enum ModelAlphaMode
    {
        Opaque,
        Mask,
        Blend
    }

    internal sealed class GltfAssetManifest
    {
        public GltfAssetManifest(IReadOnlyList<GltfMaterial> materials)
        {
            Materials = materials;
        }

        public IReadOnlyList<GltfMaterial> Materials { get; }
    }

    internal sealed class GltfMaterial
    {
        public string? Name { get; set; }
        public Vector4? BaseColorFactor { get; set; }
        public Vector4? EmissiveFactor { get; set; }
        public float? MetallicFactor { get; set; }
        public float? RoughnessFactor { get; set; }
        public float? OcclusionStrength { get; set; }
        public float? NormalScale { get; set; }
        public ModelAlphaMode AlphaMode { get; set; } = ModelAlphaMode.Opaque;
        public float? AlphaCutoff { get; set; }
        public bool DoubleSided { get; set; }
        public string? BaseColorTexturePath { get; set; }
        public string? NormalTexturePath { get; set; }
        public string? MetallicRoughnessTexturePath { get; set; }
        public string? OcclusionTexturePath { get; set; }
        public string? EmissiveTexturePath { get; set; }
    }
}
