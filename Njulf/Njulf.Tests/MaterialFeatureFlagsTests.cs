using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class MaterialFeatureFlagsTests
    {
        [Test]
        public void FeatureFlagHelpers_ClassifyExtensionLightingAndTransmission()
        {
            MaterialFeatureFlags defaultFlags = MaterialFeatureFlags.None;
            MaterialFeatureFlags clearcoat = MaterialFeatureFlags.Clearcoat;
            MaterialFeatureFlags transmission = MaterialFeatureFlags.Transmission;
            MaterialFeatureFlags foliage = MaterialFeatureFlags.Foliage;

            Assert.Multiple(() =>
            {
                Assert.That(defaultFlags.HasAnyExtensionLighting(), Is.False);
                Assert.That(defaultFlags.RequiresExtensionData(), Is.False);
                Assert.That(defaultFlags.RequiresTransparentPass(), Is.False);
                Assert.That(defaultFlags.RequiresOpaqueSceneColorInput(), Is.False);
                Assert.That(clearcoat.HasAnyExtensionLighting(), Is.True);
                Assert.That(clearcoat.RequiresExtensionData(), Is.True);
                Assert.That(clearcoat.RequiresTransparentPass(), Is.False);
                Assert.That(transmission.HasAnyExtensionLighting(), Is.True);
                Assert.That(transmission.RequiresExtensionData(), Is.True);
                Assert.That(transmission.RequiresTransparentPass(), Is.True);
                Assert.That(transmission.RequiresOpaqueSceneColorInput(), Is.True);
                Assert.That(foliage.HasAnyExtensionLighting(), Is.False);
                Assert.That(foliage.RequiresExtensionData(), Is.False);
                Assert.That(foliage.RequiresTransparentPass(), Is.False);
                Assert.That(foliage.RequiresOpaqueSceneColorInput(), Is.False);
            });
        }
    }
}
