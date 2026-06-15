using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline
{
    internal static class AdaptiveHiZPolicy
    {
        public const int SmallRenderableObjectCount = 256;
        public const int MinRecentVisibilityTests = 64;
        public const int MinMeasuredOcclusionTests = 512;
        public const float MinUsefulOcclusionCullRate = 0.03f;
        private const double RequiredBenefitOverCost = 1.25;

        public static bool ShouldSuppress(
            SceneRenderingData sceneData,
            GpuMeshletCounters meshletCounters,
            GPUVisibilityCounters visibilityCounters,
            long lastHiZCostMicroseconds,
            long lastForwardCostMicroseconds)
        {
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));

            int renderableObjects = ResolveRenderableObjectCount(sceneData);
            if (renderableObjects > 0 && renderableObjects <= SmallRenderableObjectCount)
                return true;

            int recentForwardWork = ResolveRecentForwardWork(meshletCounters, visibilityCounters);
            int recentOcclusionTests = ResolveRecentOcclusionTests(meshletCounters, visibilityCounters);
            int recentOcclusionCulled = ResolveRecentOcclusionCulled(meshletCounters, visibilityCounters);

            if (recentForwardWork == 0 && recentOcclusionTests == 0)
                return true;

            if (recentOcclusionTests < MinRecentVisibilityTests &&
                Math.Max(renderableObjects, recentForwardWork) <= SmallRenderableObjectCount * 2)
            {
                return true;
            }

            if (recentOcclusionTests >= MinMeasuredOcclusionTests)
            {
                float cullRate = (float)recentOcclusionCulled / Math.Max(1, recentOcclusionTests);
                if (cullRate < MinUsefulOcclusionCullRate)
                    return true;
            }

            if (lastHiZCostMicroseconds > 0 &&
                lastForwardCostMicroseconds > 0 &&
                recentOcclusionTests > 0 &&
                recentOcclusionCulled >= 0)
            {
                double cullRate = recentOcclusionCulled / (double)Math.Max(1, recentOcclusionTests);
                double estimatedBenefit = lastForwardCostMicroseconds * cullRate;
                if (lastHiZCostMicroseconds > estimatedBenefit * RequiredBenefitOverCost)
                    return true;
            }

            return false;
        }

        private static int ResolveRenderableObjectCount(SceneRenderingData sceneData)
        {
            int classified = sceneData.SolidObjectCount +
                sceneData.MaskedObjectCount +
                sceneData.TransparentObjectCount +
                sceneData.GeometryDecalObjectCount;
            if (classified > 0)
                return classified;
            if (sceneData.ObjectCount > 0)
                return sceneData.ObjectCount;
            if (sceneData.ObjectDataCount > 0)
                return sceneData.ObjectDataCount;
            if (sceneData.GpuSceneInstanceCount > 0)
                return sceneData.GpuSceneInstanceCount;
            return sceneData.GpuSceneObjectCount;
        }

        private static int ResolveRecentForwardWork(GpuMeshletCounters meshletCounters, GPUVisibilityCounters visibilityCounters)
        {
            if (visibilityCounters.OpaqueMeshletCount > 0)
                return checked((int)visibilityCounters.OpaqueMeshletCount);
            return meshletCounters.ForwardCandidates;
        }

        private static int ResolveRecentOcclusionTests(GpuMeshletCounters meshletCounters, GPUVisibilityCounters visibilityCounters)
        {
            if (visibilityCounters.OcclusionTestedObjectCount > 0)
                return checked((int)visibilityCounters.OcclusionTestedObjectCount);
            return meshletCounters.ForwardOcclusionTested;
        }

        private static int ResolveRecentOcclusionCulled(GpuMeshletCounters meshletCounters, GPUVisibilityCounters visibilityCounters)
        {
            if (visibilityCounters.OcclusionTestedObjectCount > 0 ||
                visibilityCounters.OcclusionRejectedObjectCount > 0)
            {
                return checked((int)visibilityCounters.OcclusionRejectedObjectCount);
            }

            return meshletCounters.ForwardOcclusionCulled;
        }
    }
}
