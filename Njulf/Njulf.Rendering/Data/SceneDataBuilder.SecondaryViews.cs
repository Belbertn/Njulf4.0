using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

public sealed unsafe partial class SceneDataBuilder
{
    private Scene? _secondaryScene;
    private bool _secondaryGeometryDecalsEnabled;
    private int _secondaryIsolatedDecalMaterialIndex;
    private readonly HashSet<uint> _secondaryResidencyDemand = [];

    internal void BuildSecondaryDrawLists(in SecondaryViewContext view, int frameIndex,
        SecondaryViewDrawLists output, bool cull)
    {
        Scene scene = _secondaryScene ?? throw new InvalidOperationException("Scene submission must precede a secondary view.");
        output.Clear();
        _secondaryResidencyDemand.Clear();
        Frustum frustum = ExtractFrustum(view.CullingViewProjection);
        uint instanceId = 0;
        foreach (RenderObject obj in scene.RenderObjects)
        {
            if (!obj.Visible || obj.Mesh is not MeshHandle mesh || !mesh.IsValid) continue;
            MaterialHandle material = ResolveRenderObjectMaterialHandle(obj.Material,
                _materialManager.DefaultMaterialHandle, obj.Name ?? string.Empty);
            AppendSecondaryInstance(view, frustum, frameIndex, output, cull, instanceId++, mesh,
                material, GetCullingMatrix(obj), obj is SkinnedRenderObject { SkinningEnabled: true });
        }
        foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
        {
            if (!batch.Visible || batch.Mesh is not MeshHandle mesh || !mesh.IsValid) continue;
            MaterialHandle material = ResolveRenderObjectMaterialHandle(batch.Material,
                _materialManager.DefaultMaterialHandle, batch.Name);
            foreach (Matrix4x4 world in batch.WorldMatrices)
                AppendSecondaryInstance(view, frustum, frameIndex, output, cull, instanceId++, mesh,
                    material, world, false);
        }
        if (instanceId != _objectData.Count)
            throw new InvalidOperationException("Secondary view instance identities do not match the submitted scene.");
        _cpuSceneResidencyDemandRanges.UnionWith(_secondaryResidencyDemand);
        RefreshCpuMeshletResidencyDemand();
        output.SortTransparency();
    }

    private void AppendSecondaryInstance(in SecondaryViewContext view, in Frustum frustum,
        int frameIndex, SecondaryViewDrawLists output, bool cull, uint instanceId, MeshHandle mesh,
        MaterialHandle material, Matrix4x4 world, bool deforming)
    {
        if (Array.BinarySearch(view.ExcludedObjects, instanceId) >= 0)
        {
            output.ExcludedObjects++;
            return;
        }
        MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(material);
        int resolvedMaterialIndex = _materialManager.ResolveMaterialIndex(material);
        if (metadata.IsGeometryDecal && (!_secondaryGeometryDecalsEnabled ||
            (_secondaryIsolatedDecalMaterialIndex >= 0 &&
             _secondaryIsolatedDecalMaterialIndex != resolvedMaterialIndex))) return;
        bool transparent = metadata.RenderMode == MaterialRenderMode.Blend || metadata.IsGeometryDecal;
        if (transparent && !view.IncludesTransparency) return;
        MeshInfo info = GetValidatedMeshInfo(mesh);
        BoundingBox bounds = TransformBoundingBox(new BoundingBox(
            ToCoreVector(info.BoundingBoxMin), ToCoreVector(info.BoundingBoxMax)), world);
        // Skinned bounds are not a conservative oracle for the current deformation.
        if (cull && !deforming && !SecondaryViewVisibility.IsVisible(bounds, frustum, view.ClipPlane, view.ClipTolerance))
        {
            output.CulledObjects++;
            return;
        }
        bool fullyInside = !cull || deforming || ContainsFrustum(bounds, frustum);
        uint materialIndex = checked((uint)resolvedMaterialIndex);
        MaterialForwardClass family = MaterialForwardClassifier.Classify(
            _materialManager.GetMaterialData(material), metadata);
        int bucket = family switch
        {
            MaterialForwardClass.SimpleOpaque => 0,
            MaterialForwardClass.SimpleOpaqueNormal => 1,
            _ => 2
        };
        // Match the existing capture's full-detail geometry; no main-camera LOD state.
        MeshletLodRange range = ResolveMeshletLodRange(info, 0, frameIndex,
            _secondaryResidencyDemand, out _);
        output.CandidateMeshlets += checked((int)range.Count);
        uint flags = CreateMeshletCommandFlags(metadata, world, deforming, metadata.RenderMode, metadata.IsGeometryDecal);
        float distance = DistanceSquared(view.Position, bounds.Center);
        for (uint index = 0; index < range.Count; index++)
        {
            uint address = range.Offset + index;
            if (!fullyInside)
            {
                var meshlet = _meshManager.GetMeshlet(mesh, address);
                BoundingBox meshletBounds = SecondaryViewVisibility.TransformSphere(
                    ToCoreVector(meshlet.BoundingSphereCenter), meshlet.BoundingSphereRadius, world);
                if (!SecondaryViewVisibility.IsVisible(meshletBounds, frustum, view.ClipPlane, view.ClipTolerance))
                {
                    output.CulledMeshlets++;
                    continue;
                }
            }
            var command = new GPUMeshletDrawCommand
            {
                MeshletIndex = address, InstanceId = instanceId, MaterialIndex = materialIndex, Flags = flags
            };
            if (transparent)
            {
                if (output.Transparent.Count < view.MaximumTransparentMeshlets)
                    output.Transparent.Add(new(command, distance, metadata.DecalLayer));
            }
            else
                output.Opaque[bucket].Add(command);
        }
    }
}
