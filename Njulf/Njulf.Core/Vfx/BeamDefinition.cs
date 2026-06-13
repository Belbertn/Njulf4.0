using Njulf.Core.Math;

namespace Njulf.Core.Vfx
{
    public sealed class BeamDefinition
    {
        public string Name { get; init; } = string.Empty;
        public ParticleMaterialDefinition Material { get; init; } = new();
        public Vector3 LocalStart { get; init; }
        public Vector3 LocalEnd { get; init; } = Vector3.UnitZ;
        public ParticleCurve Width { get; init; } = ParticleCurve.Constant(0.05f);
        public ParticleGradient Color { get; init; } = ParticleGradient.White;
        public int SegmentCount { get; init; } = 8;
        public float NoiseAmplitude { get; init; }
        public float UvScrollSpeed { get; init; }
    }
}
