using System;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Keeps bundled sample content probe-free while preserving reflection-probe
/// support in the renderer for external content and compatibility tests.
/// </summary>
internal static class SampleReflectionPolicy
{
    public static void Apply(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReflectionSettings reflections = settings.Reflections;
        reflections.MaxProbes = 0;
        reflections.CaptureOnLoad = false;
        reflections.CaptureIncludesDdgi = false;
        reflections.MaxProbeCapturesPerFrame = 0;
        reflections.MaxProbeCaptureFacesPerFrame = 0;
        reflections.MaxProbePrefilterMipsPerFrame = 0;
        reflections.ReflectionCaptureGpuBudgetMicroseconds = 0;
    }

    public static void EnsureProbeFree(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.ReflectionProbes.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Bundled sample scene '{scene.Name}' authored " +
            $"{scene.ReflectionProbes.Count} manual reflection probe(s). " +
            "Sample scenes must use SSR, ray-query recovery, and the global environment.");
    }
}
