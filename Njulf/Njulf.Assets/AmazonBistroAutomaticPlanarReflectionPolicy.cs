using System;
using System.IO;

namespace Njulf.Assets;

/// <summary>
/// Reviewed automatic-planar authoring for Bistro's FBX source, which cannot
/// carry the Njulf glTF material extra. Admission requires an exact asset and
/// material identity; this is not a reflectivity heuristic.
/// </summary>
internal static class AmazonBistroAutomaticPlanarReflectionPolicy
{
    internal const string PolicyRevision =
        "amazon-bistro-automatic-planar-reflection/v2";
    internal const string ExteriorAssetName = "BistroExterior.fbx";
    internal const string WetPavementMaterialName = "Pavement_Ground_Wet";
    internal const string WetPavementBaseColorIdentity =
        "Pavement_Ground_Wet_BaseColor";

    public static bool Apply(string modelPath, ModelMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(material);

        if (!Path.GetFileName(modelPath).Equals(
                ExteriorAssetName,
                StringComparison.OrdinalIgnoreCase) ||
            !HasWetPavementBaseColorIdentity(material))
        {
            return false;
        }

        // Assimp exposes this FBX's authored material slots as Material_N.
        // Canonicalize only the reviewed source identity so scene overrides,
        // diagnostics, and cooked evidence retain the authored material name.
        material.Name = WetPavementMaterialName;
        material.AutomaticPlanarReflectionEnabled = true;
        return true;
    }

    private static bool HasWetPavementBaseColorIdentity(
        ModelMaterial material)
    {
        ModelTextureSource? source = material.BaseColorTexture?.Source;
        string identity = source?.CacheIdentity ??
                          source?.FilePath ??
                          source?.DebugName ??
                          material.AlbedoTexturePath ??
                          string.Empty;
        return Path.GetFileNameWithoutExtension(identity).Equals(
            WetPavementBaseColorIdentity,
            StringComparison.OrdinalIgnoreCase);
    }
}
