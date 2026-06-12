using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public readonly struct SelectedLocalShadow
    {
        public SelectedLocalShadow(int lightIndex, Light light, float score)
        {
            LightIndex = lightIndex;
            Light = light;
            Score = score;
        }

        public int LightIndex { get; }
        public Light Light { get; }
        public float Score { get; }
    }

    public sealed class LocalShadowSelection
    {
        public SelectedLocalShadow[] SpotLights { get; init; } = [];
        public SelectedLocalShadow[] PointLights { get; init; } = [];
        public int SpotCandidateCount { get; init; }
        public int PointCandidateCount { get; init; }
        public int SpotRejectedByBudgetCount { get; init; }
        public int PointRejectedByBudgetCount { get; init; }
        public int SpotAtlasCapacity { get; init; }
    }
}
