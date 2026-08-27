using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Fail-closed admission policy for the automatic fragment shading-rate map.
/// The GPU classifier applies the spatial tests; this policy rejects frames
/// whose correctness cannot be established from depth and motion alone.
/// </summary>
internal static class VariableRateShadingPolicy
{
    internal const float ForegroundDistanceMeters = 2.0f;
    internal const float MotionThresholdPixels = 0.5f;
    internal const float AbsoluteDepthThresholdMeters = 0.02f;
    internal const float RelativeDepthThreshold = 0.02f;
    internal const float NormalDotThreshold = 0.98f;
    internal const int MaximumLocalLightCount = 16;
    internal const uint FineRateEncoding = 0u;
    internal const uint Coarse2X2RateEncoding = (1u << 2) | 1u;

    internal static VariableRateShadingDecision Evaluate(
        VariableRateShadingMode mode,
        bool runtimeSupported,
        bool fullFrame,
        bool debugOrCaptureOutput,
        bool incompatiblePerPixelOutput,
        bool currentMotionAvailable,
        int maskedMeshletCount,
        int foliageClusterCount,
        int localLightCount)
    {
        if (mode == VariableRateShadingMode.Off)
            return VariableRateShadingDecision.Disabled("disabled-by-settings");
        if (!runtimeSupported)
            return VariableRateShadingDecision.Disabled("vulkan-fragment-shading-rate-unavailable");
        if (!fullFrame)
            return VariableRateShadingDecision.Disabled("feature-isolation-active");
        if (debugOrCaptureOutput)
            return VariableRateShadingDecision.Disabled("debug-or-capture-output-active");
        if (incompatiblePerPixelOutput)
            return VariableRateShadingDecision.Disabled("per-pixel-forward-output-active");
        if (!currentMotionAvailable)
            return VariableRateShadingDecision.Disabled("current-motion-unavailable");
        // The depth/motion classifier cannot identify alpha-tested ownership.
        // Until an exact visibility-class attachment exists, retain 1x1 for
        // the whole frame whenever masked or foliage geometry can contribute.
        if (maskedMeshletCount > 0)
            return VariableRateShadingDecision.Disabled("alpha-tested-geometry-active");
        if (foliageClusterCount > 0)
            return VariableRateShadingDecision.Disabled("foliage-geometry-active");
        if (localLightCount > MaximumLocalLightCount)
            return VariableRateShadingDecision.Disabled("dense-local-lighting");

        return VariableRateShadingDecision.Enabled;
    }
}

internal readonly record struct VariableRateShadingDecision(
    bool IsEnabled,
    string FallbackReason)
{
    internal static VariableRateShadingDecision Enabled { get; } =
        new(true, string.Empty);

    internal static VariableRateShadingDecision Disabled(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A disabled VRS decision requires a reason.", nameof(reason));
        return new VariableRateShadingDecision(false, reason);
    }
}
