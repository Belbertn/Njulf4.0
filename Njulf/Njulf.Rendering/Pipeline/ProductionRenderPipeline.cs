using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Pipeline
{
    internal static class ProductionRenderPipeline
    {
        public static IReadOnlyList<string> PassOrder { get; } = new[]
        {
            "SkinningPass",
            "GpuVisibilityPass",
            "DepthPrePass",
            "MotionVectorPass",
            "HiZBuildPass",
            "GpuOcclusionCompactionPass",
            "AmbientOcclusionPass",
            "AmbientOcclusionBlurPass",
            "TiledLightCullingPass",
            "DirectionalShadowPass",
            "SpotShadowPass",
            "PointShadowPass",
            "ForwardPlusPass",
            "SkyboxPass",
            "TransparentForwardPass",
            "WeightedOitCompositePass",
            "ParticleSimulationPass",
            "ParticlePass",
            "DebugDrawPass",
            "FogPass",
            "AutoExposurePass",
            "BloomPass",
            "ToneMapCompositePass",
            "AntiAliasingPass"
        };

        public static void ValidatePassOrder(IReadOnlyList<string> actualPassOrder)
        {
            if (actualPassOrder == null)
                throw new ArgumentNullException(nameof(actualPassOrder));

            if (actualPassOrder.Count != PassOrder.Count)
                throw new InvalidOperationException(
                    $"Render graph pass count changed. Expected {string.Join(", ", PassOrder)}; actual {string.Join(", ", actualPassOrder)}.");

            for (int i = 0; i < PassOrder.Count; i++)
            {
                if (!string.Equals(actualPassOrder[i], PassOrder[i], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Render graph pass order changed. Expected {string.Join(", ", PassOrder)}; actual {string.Join(", ", actualPassOrder)}.");
                }
            }
        }
    }
}
