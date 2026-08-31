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
    bool HasTransparentReflectionReceiver = false)
{
    internal static ScenePipelineManifest Empty => new(
        SceneMaterialPipelineKinds.None);

    internal bool Requires(SceneMaterialPipelineKinds kind) =>
        (MaterialKinds & kind) != 0;

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
            SceneDataBuilder.ReceivesSceneReflections(metadata));
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
