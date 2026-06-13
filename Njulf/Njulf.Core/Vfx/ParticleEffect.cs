namespace Njulf.Core.Vfx
{
    public sealed class ParticleEffect
    {
        public string Name { get; init; } = string.Empty;
        public IReadOnlyList<ParticleEmitterDefinition> Emitters { get; init; } = Array.Empty<ParticleEmitterDefinition>();
        public IReadOnlyList<TrailDefinition> Trails { get; init; } = Array.Empty<TrailDefinition>();
        public IReadOnlyList<BeamDefinition> Beams { get; init; } = Array.Empty<BeamDefinition>();
        public int MaxParticles { get; init; } = int.MaxValue;
        public int Priority { get; init; }
    }
}
