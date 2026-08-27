using Njulf.Core.Math;

namespace Njulf.Rendering.Data
{
    internal static class TransparentForwardPushConstants
    {
        public static GPUForwardPushConstants Create(
            SceneRenderingData sceneData,
            TransparencySettings settings)
        {
            return new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(
                    sceneData.ScreenWidth,
                    sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                PackedLightDispatch =
                    GPUForwardPushConstants.PackLightDispatch(
                        sceneData.LightCount,
                        sceneData.LocalLightCount,
                        sceneData.DirectionalLightIndex0,
                        sceneData.DirectionalLightIndex1),
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZMipCount =
                    GPUForwardPushConstants.PackThickTransmissionLimits(
                        settings.ThickTransmissionMaximumInterfaces,
                        settings.ThickTransmissionMaximumMediaDepth,
                        settings
                            .ThickTransmissionMaximumCandidatesPerInterface),
                OcclusionCullingEnabled =
                    GPUForwardPushConstants
                        .PackThickTransmissionTaskBudget(
                            settings.ThickTransmissionRayTaskBudget),
                OcclusionBias = settings.ThickTransmissionMaximumDistance,
                DebugAndAoFlags =
                    GPUForwardPushConstants.PackDebugAndAoFlags(
                        sceneData.DebugViewMode,
                        ambientOcclusionEnabled: false,
                        ambientOcclusionDebugView:
                            (uint)sceneData.AmbientOcclusionDebugView,
                        transparentReceiveShadows:
                            sceneData.TransparentReceiveShadows,
                        transparencyDebugView:
                            (uint)sceneData.TransparencyDebugView,
                        ambientOcclusionForwardSamplingMode:
                            (uint)AmbientOcclusionForwardSamplingMode
                                .Disabled,
                        globalIlluminationEnabled:
                            sceneData
                                .TransparentReceiveGlobalIllumination),
                DiagnosticFlags =
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        ddgiForwardEstimateCountersEnabled: false,
                        directionalShadowPreviewCascade:
                            (uint)sceneData
                                .DirectionalShadowPreviewCascade,
                        decalGlobalIlluminationEnabled:
                            sceneData.DecalReceiveGlobalIllumination,
                        ddgiLayeredReceiverCountersEnabled:
                            sceneData
                                .TransparentDdgiReceiverCountersEnabled,
                        decalReceiveShadows:
                            sceneData.DecalReceiveShadows,
                        thickTransmissionRayQueryEnabled:
                            sceneData.EffectiveThickTransmissionMode ==
                            ThickTransmissionMode.RayQuery,
                        thickTransmissionDispersionEnabled:
                            sceneData
                                .ThickTransmissionDispersionEnabled,
                        effectiveReflectionMode:
                            sceneData.EffectiveReflectionMode,
                        transparentSampleReflections:
                            sceneData.TransparentSampleReflections,
                        opaqueSceneColorSnapshotAvailable:
                            sceneData.OpaqueSceneColorSnapshotAvailable)
            };
        }
    }
}
