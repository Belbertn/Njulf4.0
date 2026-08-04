using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline
{
    internal static class GlobalIlluminationPassExecutionPolicy
    {
        public static bool IsDdgiDebugView(GlobalIlluminationDebugView view)
        {
            return view is GlobalIlluminationDebugView.DdgiIrradiance
                or GlobalIlluminationDebugView.DdgiVisibility
                or GlobalIlluminationDebugView.DdgiProbeIndex
                or GlobalIlluminationDebugView.DdgiProbeState
                or GlobalIlluminationDebugView.DdgiProbeRelocation
                or GlobalIlluminationDebugView.DdgiLeakClamp
                or GlobalIlluminationDebugView.DdgiCoverage
                or GlobalIlluminationDebugView.DdgiCascadeSelection
                or GlobalIlluminationDebugView.DdgiCascadeBlendWeight
                or GlobalIlluminationDebugView.DdgiUpdateReasons
                or GlobalIlluminationDebugView.DdgiRayBudget
                or GlobalIlluminationDebugView.DdgiGatherLocalVolume
                or GlobalIlluminationDebugView.DdgiGatherClipmap
                or GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight
                or GlobalIlluminationDebugView.DdgiGatherFallback
                or GlobalIlluminationDebugView.DdgiRawDiffuse
                or GlobalIlluminationDebugView.DdgiSuppressionMask
                or GlobalIlluminationDebugView.DdgiEffectiveWeight
                or GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight
                or GlobalIlluminationDebugView.DdgiClassificationInvalidScore
                or GlobalIlluminationDebugView.DdgiVisibilityMoments
                or GlobalIlluminationDebugView.DdgiSpatialCoverage
                or GlobalIlluminationDebugView.DdgiSupportCoverage
                or GlobalIlluminationDebugView.DdgiDataConfidence
                or GlobalIlluminationDebugView.DdgiDirectionalSupport
                or GlobalIlluminationDebugView.DdgiSourceCacheRadiance
                or GlobalIlluminationDebugView.DdgiVisibilityConfidence
                or GlobalIlluminationDebugView.DdgiConfidenceChain
                or GlobalIlluminationDebugView.DdgiProbeLogicalPosition
                or GlobalIlluminationDebugView.DdgiProbeRelocatedPosition
                or GlobalIlluminationDebugView.DdgiProbeRelocationDirection
                or GlobalIlluminationDebugView.DdgiGatherBlendWeight
                or GlobalIlluminationDebugView.DdgiSampledIrradiance
                or GlobalIlluminationDebugView.DdgiFinalDiffuse
                or GlobalIlluminationDebugView.DdgiConfidenceBypass
                or GlobalIlluminationDebugView.FarFieldOccupancySlice
                or GlobalIlluminationDebugView.FarFieldTraceResult
                or GlobalIlluminationDebugView.FarFieldSkyVisibility
                or GlobalIlluminationDebugView.FarFieldSunShadow;
        }

    }
}
