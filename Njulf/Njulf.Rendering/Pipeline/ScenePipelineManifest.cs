using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline;

[Flags]
internal enum SceneMaterialPipelineKinds : byte
{
    None = 0,
    Masked = 1 << 0,
    OrdinaryTransparent = 1 << 1,
    ThinGlass = 1 << 2,
    GeometryDecal = 1 << 3,
    ThickTransmission = 1 << 4
}

[Flags]
internal enum SceneForwardOpaquePipelineKinds : byte
{
    None = 0,
    Simple = 1 << 0,
    SimpleFullInput = 1 << 1,
    Full = 1 << 2,
    All = Simple | SimpleFullInput | Full
}

internal enum ScenePipelinePreparationScope : byte
{
    FirstPresentCritical,
    Complete
}

/// <summary>
/// Scene-derived material families whose pipelines must be available before
/// command recording begins. The mask is intentionally monotonic and cheap to
/// merge while scanning large scenes.
/// </summary>
internal readonly record struct ScenePipelineManifest(
    SceneMaterialPipelineKinds MaterialKinds,
    bool HasRealTransparentShadowReceiver = false,
    bool HasGeometryDecalShadowReceiver = false,
    bool HasTransparentReflectionReceiver = false,
    SceneForwardOpaquePipelineKinds ForwardOpaqueKinds =
        SceneForwardOpaquePipelineKinds.None)
{
    internal static ScenePipelineManifest Empty => new(
        SceneMaterialPipelineKinds.None);

    internal bool Requires(SceneMaterialPipelineKinds kind) =>
        (MaterialKinds & kind) != 0;

    internal bool Requires(SceneForwardOpaquePipelineKinds kind) =>
        (ForwardOpaqueKinds & kind) != 0;

    internal bool HasTransparentSurface =>
        (MaterialKinds &
         (SceneMaterialPipelineKinds.OrdinaryTransparent |
          SceneMaterialPipelineKinds.ThinGlass |
          SceneMaterialPipelineKinds.GeometryDecal |
          SceneMaterialPipelineKinds.ThickTransmission)) != 0;

    internal bool HasRealTransparentSurface =>
        (MaterialKinds &
         (SceneMaterialPipelineKinds.OrdinaryTransparent |
          SceneMaterialPipelineKinds.ThinGlass |
          SceneMaterialPipelineKinds.ThickTransmission)) != 0;

    internal ScenePipelineManifest Include(
        MaterialRenderMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        SceneMaterialPipelineKinds kind = Classify(metadata);
        bool realTransparent = kind is
            SceneMaterialPipelineKinds.OrdinaryTransparent or
            SceneMaterialPipelineKinds.ThinGlass or
            SceneMaterialPipelineKinds.ThickTransmission;
        return new ScenePipelineManifest(
            MaterialKinds | kind,
            HasRealTransparentShadowReceiver ||
            realTransparent && metadata.ReceivesShadows,
            HasGeometryDecalShadowReceiver ||
            kind == SceneMaterialPipelineKinds.GeometryDecal &&
            metadata.ReceivesShadows,
            HasTransparentReflectionReceiver ||
            SceneDataBuilder.ReceivesSceneReflections(metadata),
            ForwardOpaqueKinds);
    }

    internal ScenePipelineManifest Include(
        GPUMaterialData material,
        MaterialRenderMetadata metadata,
        bool hasVertexColor)
    {
        ScenePipelineManifest included = Include(metadata);
        MaterialForwardClass forwardClass =
            MaterialForwardClassifier.Classify(material, metadata);
        MaterialForwardClass bucket =
            SceneDataBuilder.ResolveOpaqueForwardBucket(
                forwardClass,
                hasVertexColor);
        SceneForwardOpaquePipelineKinds kind = bucket switch
        {
            MaterialForwardClass.SimpleOpaque =>
                SceneForwardOpaquePipelineKinds.Simple,
            MaterialForwardClass.SimpleOpaqueNormal =>
                SceneForwardOpaquePipelineKinds.SimpleFullInput,
            MaterialForwardClass.FullOpaque =>
                SceneForwardOpaquePipelineKinds.Full,
            _ => SceneForwardOpaquePipelineKinds.None
        };
        return included with
        {
            ForwardOpaqueKinds = included.ForwardOpaqueKinds | kind
        };
    }

    internal static SceneMaterialPipelineKinds Classify(
        MaterialRenderMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.IsGeometryDecal)
            return SceneMaterialPipelineKinds.GeometryDecal;
        if (metadata.RenderMode == MaterialRenderMode.Mask)
            return SceneMaterialPipelineKinds.Masked;
        if (metadata.RenderMode != MaterialRenderMode.Blend)
            return SceneMaterialPipelineKinds.None;
        if (metadata.ShadingModel == MaterialShadingModel.ThinGlass &&
            metadata.TransmissionPolicy == GiTransmissionPolicy.ThinSurface)
        {
            return SceneMaterialPipelineKinds.ThinGlass;
        }
        if (metadata.TransmissionPolicy == GiTransmissionPolicy.Volume)
            return SceneMaterialPipelineKinds.ThickTransmission;
        return SceneMaterialPipelineKinds.OrdinaryTransparent;
    }
}
