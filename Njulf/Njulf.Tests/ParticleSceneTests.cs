using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ParticleSceneTests
    {
        [Test]
        public void Scene_AddRemoveAndClear_TracksParticleEffects()
        {
            var scene = new Scene();
            var instance = new ParticleEffectInstance(new ParticleEffect { Name = "TestEffect" });

            scene.Add(instance);
            scene.Remove(instance);
            scene.Add(instance);
            scene.Clear();

            Assert.That(scene.ParticleEffects, Is.Empty);
        }

        [Test]
        public void ParticleEffectInstance_PlaybackState_IsExplicit()
        {
            var instance = new ParticleEffectInstance(new ParticleEffect());

            instance.Pause();
            Assert.Multiple(() =>
            {
                Assert.That(instance.Playing, Is.False);
                Assert.That(instance.Paused, Is.True);
                Assert.That(instance.Stopped, Is.False);
            });

            instance.Stop(clearParticles: true);
            Assert.Multiple(() =>
            {
                Assert.That(instance.Playing, Is.False);
                Assert.That(instance.Paused, Is.False);
                Assert.That(instance.Stopped, Is.True);
                Assert.That(instance.ClearRequested, Is.True);
            });

            instance.Restart(seed: 99);
            Assert.Multiple(() =>
            {
                Assert.That(instance.Playing, Is.True);
                Assert.That(instance.Paused, Is.False);
                Assert.That(instance.Stopped, Is.False);
                Assert.That(instance.RandomSeed, Is.EqualTo(99));
                Assert.That(instance.ClearRequested, Is.True);
            });
        }
    }
}
