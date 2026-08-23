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

        /// <summary>Whether live particles inject participating medium into the froxel grid.</summary>
        public bool VolumetricInjectionEnabled { get; init; }

        /// <summary>Extinction contribution in inverse metres at the particle centre.</summary>
        public float VolumetricDensity { get; init; } = 0.08f;

        /// <summary>Multiplier applied to the visual particle radius for medium injection.</summary>
        public float VolumetricRadiusScale { get; init; } = 1.0f;

        public Vector3 VolumetricScatteringAlbedo { get; init; } = new(0.9f, 0.9f, 0.9f);
        public float VolumetricAnisotropy { get; init; } = 0.2f;
        public int VolumetricPriority { get; init; }

        /// <summary>
        /// Admission policy for the DDGI macro-emitter representation. The
        /// default keeps sustained fire/smoke-like emission automatic while
        /// excluding short sparks and muzzle flashes.
        /// </summary>
        public ParticleGiEmissionMode GlobalIlluminationEmission { get; init; } =
            ParticleGiEmissionMode.AutoSustained;

        /// <summary>
        /// Exposure-independent integrated RGB radiant power. A positive value
        /// makes GI energy independent of particle count/tessellation. Zero
        /// asks the runtime to derive power from the authored particle envelope.
        /// </summary>
        public Vector3 GlobalIlluminationPower { get; init; } = Vector3.Zero;

        public ParticleGiSourceShape GlobalIlluminationSourceShape { get; init; } =
            ParticleGiSourceShape.Auto;

        /// <summary>Relative deadband used only for macro-source refits.</summary>
        public float GlobalIlluminationEnergyHysteresis { get; init; } = 0.02f;
    }
}
