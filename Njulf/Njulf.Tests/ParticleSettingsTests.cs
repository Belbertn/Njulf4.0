using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ParticleSettingsTests
    {
        [Test]
        public void ParticleSettings_DefaultsMatchPhaseContract()
        {
            var settings = new RenderSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Particles.Enabled, Is.True);
                Assert.That(settings.Particles.SimulationMode, Is.EqualTo(ParticleSimulationMode.Cpu));
                Assert.That(settings.Particles.DebugView, Is.EqualTo(ParticleDebugView.None));
                Assert.That(settings.Particles.MaxParticles, Is.EqualTo(65536));
                Assert.That(settings.Particles.MaxEmitters, Is.EqualTo(1024));
                Assert.That(settings.Particles.MaxBatches, Is.EqualTo(4096));
                Assert.That(settings.Particles.SoftParticlesEnabled, Is.True);
                Assert.That(settings.Particles.SoftParticleDistance, Is.EqualTo(0.35f));
                Assert.That(settings.Particles.GlobalSpawnRateScale, Is.EqualTo(1.0f));
                Assert.That(settings.Particles.GlobalEmissiveScale, Is.EqualTo(1.0f));
                Assert.That(settings.Particles.MaxUploadBytesPerFrame, Is.EqualTo(8 * 1024 * 1024));
            });
        }

        [Test]
        public void ParticleSettings_ClampToSupportedRanges()
        {
            var settings = new ParticleSettings
            {
                MaxParticles = 2_000_000,
                MaxEmitters = -10,
                MaxBatches = 999999,
                MaxTrails = -1,
                MaxTrailSegments = 2_000_000,
                SoftParticleDistance = -1.0f,
                GlobalSpawnRateScale = 99.0f,
                GlobalVelocityScale = -1.0f,
                GlobalEmissiveScale = 99.0f,
                DistanceCullMultiplier = -3.0f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.MaxParticles, Is.EqualTo(1_000_000));
                Assert.That(settings.MaxEmitters, Is.EqualTo(0));
                Assert.That(settings.MaxBatches, Is.EqualTo(65535));
                Assert.That(settings.MaxTrails, Is.EqualTo(0));
                Assert.That(settings.MaxTrailSegments, Is.EqualTo(1_000_000));
                Assert.That(settings.SoftParticleDistance, Is.EqualTo(0.0f));
                Assert.That(settings.GlobalSpawnRateScale, Is.EqualTo(10.0f));
                Assert.That(settings.GlobalVelocityScale, Is.EqualTo(0.0f));
                Assert.That(settings.GlobalEmissiveScale, Is.EqualTo(64.0f));
                Assert.That(settings.DistanceCullMultiplier, Is.EqualTo(0.0f));
            });
        }
    }
}
