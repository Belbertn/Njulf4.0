using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public readonly struct SelectedLocalShadow
    {
        public SelectedLocalShadow(int lightIndex, Light light, float score, uint stableIdentity = 0)
        {
            LightIndex = lightIndex;
            Light = light;
            Score = score;
            StableIdentity = stableIdentity == 0 ? (uint)lightIndex + 1 : stableIdentity;
        }

        public int LightIndex { get; }
        public Light Light { get; }
        public float Score { get; }
        public uint StableIdentity { get; }
    }

    public sealed class LocalShadowSelection
    {
        public SelectedLocalShadow[] SpotLights { get; init; } = [];
        public SelectedLocalShadow[] PointLights { get; init; } = [];
        public SelectedLocalShadow[] AreaLights { get; init; } = [];
        public int SpotCandidateCount { get; init; }
        public int PointCandidateCount { get; init; }
        public int AreaCandidateCount { get; init; }
        public int SpotRejectedByBudgetCount { get; init; }
        public int PointRejectedByBudgetCount { get; init; }
        public int AreaRejectedByBudgetCount { get; init; }
        public int SpotAtlasCapacity { get; init; }
    }
}
