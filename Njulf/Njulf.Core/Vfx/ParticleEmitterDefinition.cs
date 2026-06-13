using Njulf.Core.Math;

namespace Njulf.Core.Vfx
{
    public sealed class ParticleEmitterDefinition
    {
        public string Name { get; init; } = string.Empty;
        public ParticleMaterialDefinition Material { get; init; } = new();
        public ParticleSpawnShape SpawnShape { get; init; } = ParticleSpawnShape.Point();
        public bool Looping { get; init; } = true;
        public float DurationSeconds { get; init; } = 1.0f;
        public float StartDelaySeconds { get; init; }
        public float SpawnRatePerSecond { get; init; } = 10.0f;
        public int BurstCount { get; init; }
        public float BurstTimeSeconds { get; init; }
        public ParticleCurve LifetimeSeconds { get; init; } = ParticleCurve.Constant(1.0f);
        public ParticleCurve Size { get; init; } = ParticleCurve.Constant(1.0f);
        public ParticleGradient ColorOverLife { get; init; } = ParticleGradient.White;
        public ParticleCurve EmissiveOverLife { get; init; } = ParticleCurve.Constant(0.0f);
        public ParticleCurve RotationRadians { get; init; } = ParticleCurve.Constant(0.0f);
        public ParticleCurve AngularVelocityRadiansPerSecond { get; init; } = ParticleCurve.Constant(0.0f);
        public Vector3 InitialVelocityMin { get; init; }
        public Vector3 InitialVelocityMax { get; init; }
        public Vector3 Acceleration { get; init; }
        public float Drag { get; init; }
        public bool LocalSpace { get; init; }
        public int MaxParticles { get; init; } = 1024;
        public float MaxDrawDistance { get; init; } = 1000.0f;
        public int Priority { get; init; }
    }
}
