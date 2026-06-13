using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ParticleEmitterSimulationTests
    {
        [Test]
        public void ParticleRandom_IsDeterministicForSameSeed()
        {
            var a = new ParticleRandom(123);
            var b = new ParticleRandom(123);

            for (int i = 0; i < 16; i++)
                Assert.That(a.NextUInt(), Is.EqualTo(b.NextUInt()));
        }

        [Test]
        public void ParticleSystemManager_FixedSeedBurst_IsDeterministic()
        {
            ParticleSimulationFrame a = SimulateBurst(seed: 42);
            ParticleSimulationFrame b = SimulateBurst(seed: 42);

            Assert.That(a.Instances, Has.Count.EqualTo(b.Instances.Count));
            for (int i = 0; i < a.Instances.Count; i++)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(a.Instances[i].Position, Is.EqualTo(b.Instances[i].Position));
                    Assert.That(a.Instances[i].Velocity, Is.EqualTo(b.Instances[i].Velocity));
                    Assert.That(a.Instances[i].Color, Is.EqualTo(b.Instances[i].Color));
                    Assert.That(a.Instances[i].Size, Is.EqualTo(b.Instances[i].Size));
                });
            }
        }

        [Test]
        public void ParticleSystemManager_SpawnRate_IsFrameRateIndependentForOneSecond()
        {
            ParticleSimulationFrame at30 = SimulateContinuous(30, 1.0f / 30.0f);
            ParticleSimulationFrame at60 = SimulateContinuous(60, 1.0f / 60.0f);

            Assert.Multiple(() =>
            {
                Assert.That(at30.Stats.LiveParticles, Is.EqualTo(60));
                Assert.That(at60.Stats.LiveParticles, Is.EqualTo(60));
            });
        }

        [Test]
        public void ParticleSystemManager_BudgetExceeded_IsReported()
        {
            var scene = CreateScene(seed: 7, burstCount: 16, spawnRate: 0.0f, maxParticles: 16);
            var manager = new ParticleSystemManager();
            var settings = new ParticleSettings { MaxParticles = 4 };

            ParticleSimulationFrame frame = manager.Update(scene, settings, Vector3.Zero, 1.0f / 60.0f);

            Assert.Multiple(() =>
            {
                Assert.That(frame.Stats.LiveParticles, Is.EqualTo(4));
                Assert.That(frame.Stats.ParticleBudgetExceeded, Is.EqualTo(1));
            });
        }

        private static ParticleSimulationFrame SimulateBurst(uint seed)
        {
            var scene = CreateScene(seed, burstCount: 8, spawnRate: 0.0f, maxParticles: 32);
            var manager = new ParticleSystemManager();
            return CloneFrame(manager.Update(scene, new ParticleSettings(), new Vector3(0.0f, 0.0f, 4.0f), 1.0f / 60.0f));
        }

        private static ParticleSimulationFrame SimulateContinuous(int steps, float deltaSeconds)
        {
            var scene = CreateScene(seed: 9, burstCount: 0, spawnRate: 60.0f, maxParticles: 128);
            var manager = new ParticleSystemManager();
            ParticleSimulationFrame frame = manager.LastFrame;
            for (int i = 0; i < steps; i++)
                frame = manager.Update(scene, new ParticleSettings(), new Vector3(0.0f, 0.0f, 4.0f), deltaSeconds);
            return CloneFrame(frame);
        }

        private static Scene CreateScene(uint seed, int burstCount, float spawnRate, int maxParticles)
        {
            var scene = new Scene();
            var effect = new ParticleEffect
            {
                Name = "TestParticles",
                Emitters = new[]
                {
                    new ParticleEmitterDefinition
                    {
                        Name = "Emitter",
                        Material = new ParticleMaterialDefinition { Name = "TestMaterial" },
                        SpawnShape = ParticleSpawnShape.Sphere(1.0f),
                        Looping = false,
                        DurationSeconds = 10.0f,
                        BurstCount = burstCount,
                        SpawnRatePerSecond = spawnRate,
                        LifetimeSeconds = ParticleCurve.Constant(10.0f),
                        Size = ParticleCurve.Constant(0.2f),
                        ColorOverLife = ParticleGradient.Constant(Color.White),
                        InitialVelocityMin = new Vector3(-1.0f, 0.0f, -1.0f),
                        InitialVelocityMax = new Vector3(1.0f, 1.0f, 1.0f),
                        MaxParticles = maxParticles
                    }
                }
            };
            scene.Add(new ParticleEffectInstance(effect) { RandomSeed = seed });
            return scene;
        }

        private static ParticleSimulationFrame CloneFrame(ParticleSimulationFrame frame)
        {
            var clone = new ParticleSimulationFrame { Stats = frame.Stats };
            clone.Instances.AddRange(frame.Instances);
            clone.Batches.AddRange(frame.Batches);
            return clone;
        }
    }
}
