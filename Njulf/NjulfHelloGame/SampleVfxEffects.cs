using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;

namespace NjulfHelloGame;

internal static class SampleVfxEffects
{
    public static IReadOnlyList<ParticleEffectInstance> Configure(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        var instances = new List<ParticleEffectInstance>
        {
            Add(scene, CreateFirePit(), new Vector3(-2.2f, 0.12f, 0.6f), 1301u),
            Add(scene, CreateImpactBurst(), new Vector3(0.2f, 0.08f, -1.0f), 2402u),
            Add(scene, CreateSmokeBank(), new Vector3(1.8f, 0.2f, 0.2f), 3503u),
            Add(scene, CreateMagicOrb(), new Vector3(2.0f, 1.1f, 0.2f), 4604u)
        };

        Console.WriteLine(
            $"Configured sample VFX: effects={instances.Count}, emitters={CountEmitters(instances)}, fixedSeeds=on.");
        return instances;
    }

    private static ParticleEffectInstance Add(Scene scene, ParticleEffect effect, Vector3 position, uint seed)
    {
        var instance = new ParticleEffectInstance(effect)
        {
            WorldMatrix = Matrix4x4.CreateTranslation(position),
            RandomSeed = seed
        };
        scene.Add(instance);
        return instance;
    }

    private static ParticleEffect CreateFirePit()
    {
        var flameMaterial = new ParticleMaterialDefinition
        {
            Name = "Vfx.FirePit.Flame",
            BlendMode = ParticleBlendMode.SoftAdditive,
            SoftParticles = true,
            Flipbook = new ParticleFlipbook { Columns = 4, Rows = 4, FrameCount = 16, FramesPerSecond = 18.0f }
        };
        var smokeMaterial = new ParticleMaterialDefinition
        {
            Name = "Vfx.FirePit.Smoke",
            BlendMode = ParticleBlendMode.PremultipliedAlpha,
            SoftParticles = true
        };
        var sparkMaterial = new ParticleMaterialDefinition
        {
            Name = "Vfx.FirePit.Sparks",
            BlendMode = ParticleBlendMode.Additive,
            BillboardMode = ParticleBillboardMode.StretchedVelocity,
            SoftParticles = false
        };

        return new ParticleEffect
        {
            Name = "Vfx.FirePit",
            Emitters = new[]
            {
                new ParticleEmitterDefinition
                {
                    Name = "Flame",
                    Material = flameMaterial,
                    SpawnShape = ParticleSpawnShape.Cone(0.25f, 0.35f, 0.8f),
                    SpawnRatePerSecond = 55.0f,
                    LifetimeSeconds = ParticleCurve.Linear(0.45f, 0.9f),
                    Size = ParticleCurve.FromKeys(
                        new ParticleCurveKey(0.0f, 0.15f),
                        new ParticleCurveKey(0.35f, 0.5f),
                        new ParticleCurveKey(1.0f, 0.08f)),
                    ColorOverLife = ParticleGradient.FromKeys(
                        new ParticleGradientKey(0.0f, new Color(1.0f, 0.72f, 0.22f, 0.85f)),
                        new ParticleGradientKey(0.55f, new Color(1.0f, 0.2f, 0.04f, 0.5f)),
                        new ParticleGradientKey(1.0f, Color.Transparent)),
                    EmissiveOverLife = ParticleCurve.FromKeys(
                        new ParticleCurveKey(0.0f, 6.0f),
                        new ParticleCurveKey(1.0f, 1.5f)),
                    GlobalIlluminationEmission = ParticleGiEmissionMode.Force,
                    GlobalIlluminationPower = new Vector3(35f, 12f, 2f),
                    GlobalIlluminationSourceShape = ParticleGiSourceShape.Cone,
                    InitialVelocityMin = new Vector3(-0.08f, 0.8f, -0.08f),
                    InitialVelocityMax = new Vector3(0.08f, 1.45f, 0.08f),
                    Acceleration = new Vector3(0.0f, 0.15f, 0.0f),
                    Drag = 0.4f,
                    MaxParticles = 256
                },
                new ParticleEmitterDefinition
                {
                    Name = "Smoke",
                    Material = smokeMaterial,
                    SpawnShape = ParticleSpawnShape.Sphere(0.2f),
                    SpawnRatePerSecond = 18.0f,
                    LifetimeSeconds = ParticleCurve.Linear(1.8f, 3.2f),
                    Size = ParticleCurve.FromKeys(
                        new ParticleCurveKey(0.0f, 0.2f),
                        new ParticleCurveKey(1.0f, 1.1f)),
                    ColorOverLife = ParticleGradient.FromKeys(
                        new ParticleGradientKey(0.0f, new Color(0.17f, 0.16f, 0.15f, 0.0f)),
                        new ParticleGradientKey(0.25f, new Color(0.18f, 0.17f, 0.16f, 0.42f)),
                        new ParticleGradientKey(1.0f, new Color(0.24f, 0.24f, 0.24f, 0.0f))),
                    InitialVelocityMin = new Vector3(-0.15f, 0.35f, -0.15f),
                    InitialVelocityMax = new Vector3(0.15f, 0.75f, 0.15f),
                    Acceleration = new Vector3(0.0f, 0.05f, 0.0f),
                    Drag = 0.25f,
                    MaxParticles = 192,
                    VolumetricInjectionEnabled = true,
                    VolumetricDensity = 0.14f,
                    VolumetricRadiusScale = 1.25f,
                    VolumetricScatteringAlbedo = new Vector3(0.65f, 0.62f, 0.58f),
                    VolumetricAnisotropy = 0.35f,
                    VolumetricPriority = 100
                },
                new ParticleEmitterDefinition
                {
                    Name = "Sparks",
                    Material = sparkMaterial,
                    SpawnShape = ParticleSpawnShape.SphereShell(0.12f),
                    SpawnRatePerSecond = 28.0f,
                    LifetimeSeconds = ParticleCurve.Linear(0.35f, 1.0f),
                    Size = ParticleCurve.Linear(0.035f, 0.005f),
                    ColorOverLife = ParticleGradient.Linear(
                        new Color(1.0f, 0.8f, 0.2f, 1.0f),
                        new Color(1.0f, 0.15f, 0.02f, 0.0f)),
                    EmissiveOverLife = ParticleCurve.Linear(9.0f, 0.0f),
                    InitialVelocityMin = new Vector3(-1.2f, 1.4f, -1.2f),
                    InitialVelocityMax = new Vector3(1.2f, 2.7f, 1.2f),
                    Acceleration = new Vector3(0.0f, -3.2f, 0.0f),
                    Drag = 0.15f,
                    MaxParticles = 160
                }
            }
        };
    }

    private static ParticleEffect CreateImpactBurst()
    {
        var dust = new ParticleMaterialDefinition
        {
            Name = "Vfx.ImpactBurst.Dust",
            BlendMode = ParticleBlendMode.PremultipliedAlpha,
            SoftParticles = true
        };
        var sparks = new ParticleMaterialDefinition
        {
            Name = "Vfx.ImpactBurst.Sparks",
            BlendMode = ParticleBlendMode.Additive,
            BillboardMode = ParticleBillboardMode.StretchedVelocity,
            SoftParticles = false
        };

        return new ParticleEffect
        {
            Name = "Vfx.ImpactBurst",
            Emitters = new[]
            {
                new ParticleEmitterDefinition
                {
                    Name = "Dust",
                    Material = dust,
                    Looping = true,
                    DurationSeconds = 2.4f,
                    BurstCount = 28,
                    LifetimeSeconds = ParticleCurve.Linear(1.2f, 2.4f),
                    Size = ParticleCurve.Linear(0.12f, 0.6f),
                    ColorOverLife = ParticleGradient.Linear(
                        new Color(0.48f, 0.43f, 0.36f, 0.55f),
                        new Color(0.5f, 0.48f, 0.44f, 0.0f)),
                    InitialVelocityMin = new Vector3(-1.1f, 0.25f, -1.1f),
                    InitialVelocityMax = new Vector3(1.1f, 1.3f, 1.1f),
                    Acceleration = new Vector3(0.0f, -0.25f, 0.0f),
                    Drag = 0.7f,
                    MaxParticles = 96,
                    VolumetricInjectionEnabled = true,
                    VolumetricDensity = 0.11f,
                    VolumetricRadiusScale = 1.25f,
                    VolumetricScatteringAlbedo = new Vector3(0.72f, 0.62f, 0.48f),
                    VolumetricAnisotropy = 0.15f,
                    VolumetricPriority = 80
                },
                new ParticleEmitterDefinition
                {
                    Name = "HotSparks",
                    Material = sparks,
                    Looping = false,
                    BurstCount = 32,
                    LifetimeSeconds = ParticleCurve.Linear(0.25f, 0.8f),
                    Size = ParticleCurve.Linear(0.025f, 0.0f),
                    ColorOverLife = ParticleGradient.Linear(
                        new Color(1.0f, 0.72f, 0.2f, 1.0f),
                        Color.Transparent),
                    EmissiveOverLife = ParticleCurve.Linear(12.0f, 0.0f),
                    InitialVelocityMin = new Vector3(-2.8f, 1.1f, -2.8f),
                    InitialVelocityMax = new Vector3(2.8f, 3.2f, 2.8f),
                    Acceleration = new Vector3(0.0f, -5.0f, 0.0f),
                    Drag = 0.1f,
                    MaxParticles = 64
                }
            }
        };
    }

    private static ParticleEffect CreateSmokeBank()
    {
        return new ParticleEffect
        {
            Name = "Vfx.SmokeBank",
            Emitters = new[]
            {
                new ParticleEmitterDefinition
                {
                    Name = "DenseSmoke",
                    Material = new ParticleMaterialDefinition
                    {
                        Name = "Vfx.SmokeBank.DenseSmoke",
                        BlendMode = ParticleBlendMode.PremultipliedAlpha,
                        SoftParticles = true
                    },
                    SpawnShape = ParticleSpawnShape.Box(
                        new Vector3(0.7f, 0.12f, 0.7f)),
                    SpawnRatePerSecond = 30.0f,
                    LifetimeSeconds = ParticleCurve.Linear(3.8f, 5.8f),
                    Size = ParticleCurve.FromKeys(
                        new ParticleCurveKey(0.0f, 0.32f),
                        new ParticleCurveKey(0.45f, 0.95f),
                        new ParticleCurveKey(1.0f, 1.45f)),
                    ColorOverLife = ParticleGradient.FromKeys(
                        new ParticleGradientKey(0.0f,
                            new Color(0.11f, 0.13f, 0.17f, 0.0f)),
                        new ParticleGradientKey(0.2f,
                            new Color(0.13f, 0.16f, 0.21f, 0.48f)),
                        new ParticleGradientKey(1.0f,
                            new Color(0.2f, 0.23f, 0.28f, 0.0f))),
                    InitialVelocityMin = new Vector3(-0.12f, 0.12f, -0.1f),
                    InitialVelocityMax = new Vector3(0.18f, 0.38f, 0.08f),
                    Acceleration = new Vector3(0.03f, 0.025f, -0.015f),
                    Drag = 0.18f,
                    MaxParticles = 224,
                    MaxDrawDistance = 24.0f,
                    VolumetricInjectionEnabled = true,
                    VolumetricDensity = 0.22f,
                    VolumetricRadiusScale = 1.35f,
                    VolumetricScatteringAlbedo =
                        new Vector3(0.5f, 0.57f, 0.68f),
                    VolumetricAnisotropy = 0.42f,
                    VolumetricPriority = 120
                }
            }
        };
    }

    private static ParticleEffect CreateMagicOrb()
    {
        return new ParticleEffect
        {
            Name = "Vfx.MagicOrb",
            Beams = new[] { new BeamDefinition { Name = "ValidationBeam", SegmentCount = 12, NoiseAmplitude = 0.08f } },
            Trails = new[] { new TrailDefinition { Name = "ValidationTrail", MaxSegments = 32 } },
            Emitters = new[]
            {
                new ParticleEmitterDefinition
                {
                    Name = "Glow",
                    Material = new ParticleMaterialDefinition
                    {
                        Name = "Vfx.MagicOrb.Glow",
                        BlendMode = ParticleBlendMode.Additive,
                        SoftParticles = true
                    },
                    SpawnShape = ParticleSpawnShape.Ring(0.25f, 0.75f),
                    SpawnRatePerSecond = 80.0f,
                    LifetimeSeconds = ParticleCurve.Linear(0.9f, 1.8f),
                    Size = ParticleCurve.FromKeys(
                        new ParticleCurveKey(0.0f, 0.08f),
                        new ParticleCurveKey(0.5f, 0.2f),
                        new ParticleCurveKey(1.0f, 0.0f)),
                    ColorOverLife = ParticleGradient.Linear(
                        new Color(0.35f, 0.95f, 1.0f, 0.75f),
                        new Color(0.9f, 0.35f, 1.0f, 0.0f)),
                    EmissiveOverLife = ParticleCurve.Linear(4.0f, 0.0f),
                    GlobalIlluminationEmission = ParticleGiEmissionMode.Force,
                    GlobalIlluminationPower = new Vector3(5f, 18f, 25f),
                    GlobalIlluminationSourceShape = ParticleGiSourceShape.Disk,
                    InitialVelocityMin = new Vector3(-0.25f, -0.05f, -0.25f),
                    InitialVelocityMax = new Vector3(0.25f, 0.45f, 0.25f),
                    Drag = 0.1f,
                    MaxParticles = 256
                }
            }
        };
    }

    private static int CountEmitters(IReadOnlyList<ParticleEffectInstance> instances)
    {
        int count = 0;
        for (int i = 0; i < instances.Count; i++)
            count += instances[i].Effect.Emitters.Count;
        return count;
    }
}
