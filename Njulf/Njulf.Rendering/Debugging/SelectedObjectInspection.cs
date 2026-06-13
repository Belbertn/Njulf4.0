using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Debug
{
    public sealed record SelectedObjectInspection(
        int ObjectIndex,
        string ObjectName,
        MeshHandle Mesh,
        MaterialHandle Material,
        BoundingBox WorldBounds,
        bool Visible,
        bool CpuCulled,
        MaterialInspectionResult MaterialInfo);

    public sealed record MaterialInspectionResult(
        int MaterialIndex,
        Vector4 Albedo,
        Vector4 Emissive,
        float Metallic,
        float Roughness,
        float AmbientOcclusion,
        float NormalStrength,
        MaterialRenderMode RenderMode,
        int AlbedoTextureIndex,
        int NormalTextureIndex,
        int MetallicRoughnessTextureIndex,
        int EmissiveTextureIndex)
    {
        public static MaterialInspectionResult FromGpuMaterial(int materialIndex, GPUMaterialData material)
        {
            return new MaterialInspectionResult(
                materialIndex,
                material.Albedo,
                material.Emissive,
                material.MetallicRoughnessAO.X,
                material.MetallicRoughnessAO.Y,
                material.MetallicRoughnessAO.Z,
                material.NormalScaleBias.X,
                MaterialRenderModeExtensions.FromGpuMaterial(material),
                material.AlbedoTextureIndex,
                material.NormalTextureIndex,
                material.MetallicRoughnessTextureIndex,
                material.EmissiveTextureIndex);
        }
    }
}
