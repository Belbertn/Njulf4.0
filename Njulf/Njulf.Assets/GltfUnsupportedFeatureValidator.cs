using System;
using System.Text.Json;

namespace Njulf.Assets
{
    internal static class GltfUnsupportedFeatureValidator
    {
        public static void Validate(JsonElement root, string modelPath, AssetImportDiagnostics diagnostics)
        {
            bool basisRequired = IsExtensionRequired(root, "KHR_texture_basisu");
            bool basisTextureFound = false;
            if (root.TryGetProperty("textures", out JsonElement texturesElement) &&
                texturesElement.ValueKind == JsonValueKind.Array)
            {
                int textureIndex = 0;
                foreach (JsonElement textureElement in texturesElement.EnumerateArray())
                {
                    bool usesBasis = textureElement.TryGetProperty("extensions", out JsonElement extensions) &&
                                     extensions.ValueKind == JsonValueKind.Object &&
                                     extensions.TryGetProperty("KHR_texture_basisu", out _);
                    if (usesBasis)
                    {
                        basisTextureFound = true;
                        diagnostics.UnsupportedCompressedTextureCount++;
                        diagnostics.Add(
                            basisRequired ? AssetImportSeverity.Error : AssetImportSeverity.Warning,
                            AssetImportMessageCode.CompressedTextureUnsupported,
                            modelPath,
                            $"/textures/{textureIndex}/extensions/KHR_texture_basisu",
                            "KTX2/Basis compressed texture decode is not implemented; provide PNG/JPEG fallback textures for this renderer build.");
                        if (basisRequired)
                            throw new NotSupportedException($"glTF asset '{modelPath}' requires KHR_texture_basisu, but KTX2/Basis decode is not implemented.");
                    }

                    textureIndex++;
                }
            }

            if (basisRequired && !basisTextureFound)
            {
                diagnostics.UnsupportedCompressedTextureCount++;
                diagnostics.Add(
                    AssetImportSeverity.Error,
                    AssetImportMessageCode.CompressedTextureUnsupported,
                    modelPath,
                    "/extensionsRequired",
                    "glTF asset requires KHR_texture_basisu, but no decodable fallback path is implemented.");
                throw new NotSupportedException($"glTF asset '{modelPath}' requires KHR_texture_basisu, but KTX2/Basis decode is not implemented.");
            }

            ValidateMorphTargets(root, modelPath, diagnostics);
        }

        private static void ValidateMorphTargets(JsonElement root, string modelPath, AssetImportDiagnostics diagnostics)
        {
            if (!root.TryGetProperty("meshes", out JsonElement meshesElement) ||
                meshesElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            int meshIndex = 0;
            foreach (JsonElement meshElement in meshesElement.EnumerateArray())
            {
                if (!meshElement.TryGetProperty("primitives", out JsonElement primitivesElement) ||
                    primitivesElement.ValueKind != JsonValueKind.Array)
                {
                    meshIndex++;
                    continue;
                }

                int primitiveIndex = 0;
                foreach (JsonElement primitiveElement in primitivesElement.EnumerateArray())
                {
                    if (primitiveElement.TryGetProperty("targets", out JsonElement targetsElement) &&
                        targetsElement.ValueKind == JsonValueKind.Array &&
                        targetsElement.GetArrayLength() > 0)
                    {
                        diagnostics.UnsupportedMorphTargetMeshCount++;
                        diagnostics.Add(
                            AssetImportSeverity.Warning,
                            AssetImportMessageCode.MorphTargetsUnsupported,
                            modelPath,
                            $"/meshes/{meshIndex}/primitives/{primitiveIndex}/targets",
                            "Morph target import and rendering are not implemented; this primitive will render without blend-shape deformation.");
                    }

                    primitiveIndex++;
                }

                meshIndex++;
            }
        }

        private static bool IsExtensionRequired(JsonElement root, string extensionName)
        {
            if (!root.TryGetProperty("extensionsRequired", out JsonElement required) ||
                required.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement extensionElement in required.EnumerateArray())
            {
                if (string.Equals(extensionElement.GetString(), extensionName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
