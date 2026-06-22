using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline
{
    internal static class RenderFeatureIsolationPolicy
    {
        public static bool AllowsShadows(RenderFeatureIsolationMode mode)
        {
            return mode is RenderFeatureIsolationMode.FullFrame or RenderFeatureIsolationMode.Shadows;
        }

        public static bool AllowsPostProcessing(RenderFeatureIsolationMode mode)
        {
            return mode is RenderFeatureIsolationMode.FullFrame or RenderFeatureIsolationMode.PostProcessing;
        }

        public static bool AllowsReflections(RenderFeatureIsolationMode mode)
        {
            return mode is RenderFeatureIsolationMode.FullFrame or RenderFeatureIsolationMode.Reflections;
        }

        public static bool AllowsAnimation(RenderFeatureIsolationMode mode)
        {
            return mode is RenderFeatureIsolationMode.FullFrame or RenderFeatureIsolationMode.Animation;
        }

        public static bool AllowsParticles(RenderFeatureIsolationMode mode)
        {
            return mode is RenderFeatureIsolationMode.FullFrame or RenderFeatureIsolationMode.Particles;
        }

        public static bool ShouldExecutePass(RenderFeatureIsolationMode mode, string passName)
        {
            if (string.IsNullOrWhiteSpace(passName))
                throw new ArgumentException("Pass name must be non-empty.", nameof(passName));

            if (mode == RenderFeatureIsolationMode.FullFrame)
                return true;

            return passName switch
            {
                "DirectionalShadowPass" or "SpotShadowPass" or "PointShadowPass" => AllowsShadows(mode),
                "AmbientOcclusionPass" or "AmbientOcclusionBlurPass" or "SsgiTracePass" or "SsgiTemporalPass" or "SsgiDenoisePass" or "SsgiCompositePass" or "DdgiUpdatePass" or "FogPass" or "AutoExposurePass" or "BloomPass" => AllowsPostProcessing(mode),
                "ParticlePass" => AllowsParticles(mode),
                _ => true
            };
        }
    }
}
