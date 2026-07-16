using Njulf.Core.Math;
using Njulf.Core.Vfx;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ParticleCurveTests
    {
        [Test]
        public void ParticleCurve_SortsKeysAndSamplesLinearly()
        {
            ParticleCurve curve = ParticleCurve.FromKeys(
                new ParticleCurveKey(1.0f, 10.0f),
                new ParticleCurveKey(0.0f, 2.0f));

            Assert.Multiple(() =>
            {
                Assert.That(curve.Sample(-1.0f), Is.EqualTo(2.0f));
                Assert.That(curve.Sample(0.5f), Is.EqualTo(6.0f));
                Assert.That(curve.Sample(2.0f), Is.EqualTo(10.0f));
            });
        }

        [Test]
        public void ParticleGradient_SortsKeysAndInterpolatesColorAndAlpha()
        {
            ParticleGradient gradient = ParticleGradient.FromKeys(
                new ParticleGradientKey(1.0f, new Color(1.0f, 0.0f, 0.0f, 0.0f)),
                new ParticleGradientKey(0.0f, new Color(0.0f, 0.0f, 1.0f, 1.0f)));

            Color sample = gradient.Sample(0.25f);

            Assert.Multiple(() =>
            {
                Assert.That(sample.R, Is.EqualTo(0.25f).Within(0.0001f));
                Assert.That(sample.G, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(sample.B, Is.EqualTo(0.75f).Within(0.0001f));
                Assert.That(sample.A, Is.EqualTo(0.75f).Within(0.0001f));
            });
        }

        [Test]
        public void ParticleFlipbook_AdvancesFramesDeterministically()
        {
            var flipbook = new ParticleFlipbook
            {
                Columns = 4,
                Rows = 4,
                FrameCount = 16,
                FramesPerSecond = 8.0f,
                Loop = true,
                RandomStartFrame = false
            };

            Assert.Multiple(() =>
            {
                Assert.That(flipbook.GetFrame(0.0f, 1.0f, 0), Is.EqualTo(0));
                Assert.That(flipbook.GetFrame(0.25f, 1.0f, 0), Is.EqualTo(2));
                Assert.That(flipbook.GetFrame(2.25f, 1.0f, 0), Is.EqualTo(2));
            });
        }
    }
}
