using System;

namespace Njulf.Rendering.Data;

/// <summary>Internal qualification controls; these do not change effect quality.</summary>
internal static class SurfaceInputPolicy
{
    // The four-word producer retained every local history. Keep its allocation,
    // seed shader interface and dispatch dormant until a complete chain pays.
    internal static bool SharedValidityEnabled => false;
    internal static bool DepthMotionFusionRequested { get; } =
        Environment.GetEnvironmentVariable("NJULF_DEPTH_MOTION_FUSION") == "1";

    internal static bool CanFuse(SceneRenderingData scene, bool requested,
        bool compactedReady, bool targetsReady, bool motionRequired,
        bool cameraOnly, bool visibilityActive) =>
        requested && scene.DepthPrePassEnabled && compactedReady && targetsReady &&
        motionRequired && !cameraOnly && !visibilityActive &&
        scene.FoliageClusterCount == 0 && scene.SkinnedObjectCount == 0;
}
