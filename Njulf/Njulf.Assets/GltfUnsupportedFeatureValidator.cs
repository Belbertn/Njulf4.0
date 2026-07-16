using System;
using System.Text.Json;

namespace Njulf.Assets
{
    internal static class GltfUnsupportedFeatureValidator
    {
        public static void Validate(JsonElement root, string modelPath, AssetImportDiagnostics diagnostics)
        {
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
                        diagnostics.Add(
                            AssetImportSeverity.Info,
                            AssetImportMessageCode.ColorSpaceAssigned,
                            modelPath,
                            $"/textures/{textureIndex}/extensions/KHR_texture_basisu",
                            "KHR_texture_basisu texture source will be imported as a native KTX2 texture payload.");
                    }

                    textureIndex++;
                }
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

    }
}
