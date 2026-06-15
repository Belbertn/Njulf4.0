using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline;

internal static class FramePassRuntimePolicy
{
    public static bool ShouldExecute(string passName, SceneRenderingData sceneData)
    {
        if (passName == null)
            throw new ArgumentNullException(nameof(passName));
        if (sceneData == null)
            throw new ArgumentNullException(nameof(sceneData));

        return passName switch
        {
            "DepthPrePass" => sceneData.DepthPrePassEnabled,
            "HiZBuildPass" => sceneData.DepthPrePassEnabled && sceneData.HiZBuildEnabled && sceneData.HiZMipCount > 0,
            "TiledLightCullingPass" => sceneData.DepthPrePassEnabled && sceneData.LocalLightCount > 0,
            "DirectionalShadowPass" => sceneData.DirectionalShadowPassEnabled &&
                                       !sceneData.DirectionalShadowRecordSkipped &&
                                       sceneData.DirectionalShadowCascadeCount > 0 &&
                                       HasDirectionalShadowWork(sceneData),
            "SpotShadowPass" => sceneData.SpotShadowsEnabled &&
                                !sceneData.SpotShadowRecordSkipped &&
                                sceneData.SpotShadowSelectedCount > 0 &&
                                HasLocalShadowWork(sceneData),
            "PointShadowPass" => sceneData.PointShadowsEnabled &&
                                 !sceneData.PointShadowRecordSkipped &&
                                 sceneData.PointShadowSelectedCount > 0 &&
                                 sceneData.PointShadowRenderedFaceCount > 0 &&
                                 HasLocalShadowWork(sceneData),
            "TransparentForwardPass" => sceneData.TransparentPassEnabled && HasTransparentWork(sceneData),
            "WeightedOitCompositePass" => sceneData.TransparentPassEnabled &&
                                          sceneData.TransparencyMode == TransparencyMode.WeightedBlendedOit &&
                                          HasTransparentWork(sceneData),
            "ParticlePass" => sceneData.ParticlesEnabled &&
                              sceneData.RenderedParticleCount > 0 &&
                              sceneData.ParticleBatches.Count > 0,
            "DebugDrawPass" => sceneData.DebugDrawSnapshot.LineCount > 0,
            _ => true
        };
    }

    private static bool HasTransparentWork(SceneRenderingData sceneData)
    {
        return sceneData.GpuVisibilityDrawCapacity > 0;
    }

    private static bool HasDirectionalShadowWork(SceneRenderingData sceneData)
    {
        return sceneData.DirectionalShadowMeshletDrawBufferSize > 0;
    }

    private static bool HasLocalShadowWork(SceneRenderingData sceneData)
    {
        return sceneData.LocalShadowMeshletDrawBufferSize > 0;
    }
}
