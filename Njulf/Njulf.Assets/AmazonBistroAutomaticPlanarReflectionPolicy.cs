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
        "amazon-bistro-automatic-planar-reflection/v1";
    internal const string ExteriorAssetName = "BistroExterior.fbx";
    internal const string WetPavementMaterialName = "Pavement_Ground_Wet";

    public static bool Apply(string modelPath, ModelMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(material);

        if (!Path.GetFileName(modelPath).Equals(
                ExteriorAssetName,
                StringComparison.OrdinalIgnoreCase) ||
            !material.Name.Equals(
                WetPavementMaterialName,
                StringComparison.Ordinal))
        {
            return false;
        }

        material.AutomaticPlanarReflectionEnabled = true;
        return true;
    }
}
