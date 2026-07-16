using Njulf.Core.Math;

namespace Njulf.Core.Vfx
{
    public sealed class TrailDefinition
    {
        public string Name { get; init; } = string.Empty;
        public ParticleMaterialDefinition Material { get; init; } = new();
        public ParticleCurve Width { get; init; } = ParticleCurve.Constant(0.1f);
        public ParticleGradient ColorOverLife { get; init; } = ParticleGradient.White;
        public float LifetimeSeconds { get; init; } = 0.5f;
        public int MaxSegments { get; init; } = 64;
        public Vector3 LocalOffset { get; init; }
    }
}
