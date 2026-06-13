namespace Njulf.Core.Vfx
{
    public sealed class ParticleMaterialDefinition
    {
        public string Name { get; init; } = string.Empty;
        public string? TexturePath { get; init; }
        public ParticleBlendMode BlendMode { get; init; } = ParticleBlendMode.PremultipliedAlpha;
        public ParticleBillboardMode BillboardMode { get; init; } = ParticleBillboardMode.ViewFacing;
        public ParticleLightingMode LightingMode { get; init; } = ParticleLightingMode.Unlit;
        public ParticleFlipbook? Flipbook { get; init; }
        public bool SoftParticles { get; init; } = true;
        public bool DepthTest { get; init; } = true;
        public bool DepthWrite { get; init; }
        public bool ReceiveFog { get; init; } = true;
        public float AlphaClipThreshold { get; init; } = 0.5f;
    }
}
