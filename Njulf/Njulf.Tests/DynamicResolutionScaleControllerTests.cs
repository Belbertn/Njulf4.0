using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DynamicResolutionScaleControllerTests
    {
        [Test]
        public void Resolve_DynamicFrameTimeChangeUpdatesRequestedScaleWithoutImmediateCommit()
        {
            var settings = CreateDynamicSettings();
            var controller = new DynamicResolutionScaleController();
            controller.Resolve(settings, frameMicroseconds: 0);

            DynamicResolutionScaleDecision decision = controller.Resolve(settings, frameMicroseconds: 25_000);

            Assert.Multiple(() =>
            {
                Assert.That(decision.RequestedScale, Is.LessThan(1.0f));
                Assert.That(decision.CommittedScale, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(decision.CommittedScaleChanged, Is.False);
                Assert.That(decision.CommitReason, Is.EqualTo(string.Empty));
            });
        }

        [Test]
        public void Resolve_SustainedSlowFramesCommitQuantizedScaleAfterCooldown()
        {
            var settings = CreateDynamicSettings();
            var controller = new DynamicResolutionScaleController();
            controller.Resolve(settings, frameMicroseconds: 0);

            DynamicResolutionScaleDecision decision = default;
            for (int i = 0; i < 18; i++)
            {
                decision = controller.Resolve(settings, frameMicroseconds: 25_000);
                if (decision.CommittedScaleChanged)
                    break;
            }

            Assert.Multiple(() =>
            {
                Assert.That(decision.CommittedScaleChanged, Is.True);
                Assert.That(decision.CommitReason, Is.EqualTo("Dynamic resolution scale"));
                Assert.That(decision.CommittedScale, Is.LessThan(1.0f));
                Assert.That((decision.CommittedScale * 100) % 2, Is.EqualTo(0).Within(0.001f));
            });
        }

        [Test]
        public void Resolve_ExplicitResolutionScaleChangeCommitsImmediately()
        {
            var settings = CreateDynamicSettings();
            var controller = new DynamicResolutionScaleController();
            controller.Resolve(settings, frameMicroseconds: 0);

            settings.ResolutionScale = 0.8f;
            DynamicResolutionScaleDecision decision = controller.Resolve(settings, frameMicroseconds: 0);

            Assert.Multiple(() =>
            {
                Assert.That(decision.CommittedScaleChanged, Is.True);
                Assert.That(decision.CommittedScale, Is.EqualTo(0.8f).Within(0.0001f));
                Assert.That(decision.CommitReason, Is.EqualTo("Resolution scale setting"));
            });
        }

        [Test]
        public void Resolve_DisablingDynamicResolutionCommitsConfiguredScaleImmediately()
        {
            var settings = CreateDynamicSettings();
            var controller = new DynamicResolutionScaleController();
            controller.Resolve(settings, frameMicroseconds: 0);

            settings.DynamicResolution.Enabled = false;
            settings.ResolutionScale = 0.9f;
            DynamicResolutionScaleDecision decision = controller.Resolve(settings, frameMicroseconds: 0);

            Assert.Multiple(() =>
            {
                Assert.That(decision.CommittedScaleChanged, Is.True);
                Assert.That(decision.RequestedScale, Is.EqualTo(0.9f).Within(0.0001f));
                Assert.That(decision.CommittedScale, Is.EqualTo(0.9f).Within(0.0001f));
                Assert.That(decision.CommitReason, Is.EqualTo("Resolution scale setting"));
            });
        }

        private static RenderSettings CreateDynamicSettings()
        {
            var settings = new RenderSettings
            {
                ResolutionScale = 1.0f
            };
            settings.DynamicResolution.Enabled = true;
            settings.DynamicResolution.MinimumScale = 0.5f;
            settings.DynamicResolution.MaximumScale = 1.0f;
            settings.DynamicResolution.TargetFrameMilliseconds = 16.67f;
            settings.DynamicResolution.AdjustmentRate = 0.05f;
            return settings;
        }
    }
}
