using Njulf.Core.Math;
using Njulf.Core.Vfx;

namespace Njulf.Rendering.Data
{
    public sealed class ParticleSimulationFrame
    {
        public List<ParticleRenderInstance> Instances { get; } = new();
        public List<ParticleBatch> Batches { get; } = new();
        public ParticleSystemFrameStats Stats { get; set; }

        public void Clear()
        {
            Instances.Clear();
            Batches.Clear();
            Stats = default;
        }
    }

    public readonly record struct ParticleRenderInstance(
        Vector3 Position,
        Vector3 Velocity,
        Color Color,
        float Size,
        float RotationRadians,
        float EmissiveIntensity,
        float NormalizedLifetime,
        int FlipbookFrame,
        int EffectId,
        int EmitterId,
        ParticleBillboardMode BillboardMode,
        ParticleMaterialDefinition Material,
        float SortDistanceSquared);

    public readonly record struct ParticleBatch(
        int Start,
        int Count,
        ParticleMaterialDefinition Material,
        ParticleBlendMode BlendMode,
        int TextureKey,
        int BatchId);

    public struct ParticleSystemFrameStats
    {
        public int Effects;
        public int Emitters;
        public int LiveParticles;
        public int SimulatedParticles;
        public int CulledParticles;
        public int RenderedParticles;
        public int Batches;
        public int AlphaParticles;
        public int AdditiveParticles;
        public int SoftParticles;
        public int FlipbookParticles;
        public int Trails;
        public int TrailSegments;
        public int Beams;
        public int ParticleBudgetExceeded;
        public int UploadBudgetExceeded;
        public ulong InstanceUploadBytes;
        public ulong TrailBeamUploadBytes;
        public long SimulationMicroseconds;
        public long BuildMicroseconds;
    }
}
