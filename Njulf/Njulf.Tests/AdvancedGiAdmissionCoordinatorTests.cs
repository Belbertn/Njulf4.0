using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiAdmissionCoordinatorTests
{
    [Test]
    public void Defaults_AreFailClosedAndCoherent()
    {
        var coordinator = new AdvancedGiAdmissionCoordinator();

        AdvancedGiAdmissionSnapshot snapshot = coordinator.CaptureSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.GraphModes,
                Is.EqualTo(AdvancedGiRenderGraphModes.Disabled));
            Assert.That(snapshot.RuntimeContentState.Matched, Is.False);
            Assert.That(snapshot.CandidateProfile, Is.Null);
            Assert.That(snapshot.CandidateProfileStatus,
                Is.EqualTo("not-configured"));
            Assert.That(snapshot.HasGiCausticEvidence, Is.False);
            Assert.That(snapshot.HasNearFieldResidualEvidence, Is.False);
            Assert.That(coordinator.EvaluatePrerequisite(
                AdvancedGiPrerequisiteFeature.TaggedCaustics).Passed,
                Is.False);
        });
    }

    [Test]
    public void RuntimeContentMatch_IsObservedAsOneAtomicTransition()
    {
        const string settingsHash =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sceneHash =
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var coordinator = new AdvancedGiAdmissionCoordinator();
        coordinator.PublishSettingsFingerprint(settingsHash);
        coordinator.ConfigureRuntimeContentBinding(
            new AdvancedGiRuntimeContentBinding(
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "bistro",
                sceneHash));

        AdvancedGiRuntimeContentState state =
            coordinator.ObserveRuntimeContent(
                "bistro",
                sceneHash,
                settingsHash);

        Assert.Multiple(() =>
        {
            Assert.That(state.Matched, Is.True);
            Assert.That(state.Reason,
                Is.EqualTo("advanced-gi-runtime-content-binding-matched"));
            Assert.That(coordinator.CaptureSnapshot().RuntimeContentState,
                Is.EqualTo(state));
        });
    }

    [Test]
    public void ResolveStartup_PublishesExactlyTheEffectiveModes()
    {
        var coordinator = new AdvancedGiAdmissionCoordinator();
        var opacity = Effective(
            DdgiOpacityMicromapMode.ExtFourStateExperiment);
        var guiding = Effective(
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment);
        var caustics = Effective(GiCausticMode.WorldCacheExperiment);
        var residual = Effective(
            SimpleDdgiNearFieldResidualMode.HiZAdaptive);

        AdvancedGiRenderGraphModes modes = coordinator.ResolveStartup(
            new AdvancedGiStartupRequest(
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                opacity,
                guiding,
                caustics,
                residual,
                AdvancedGiNearFieldGraphProfile.HalfResolutionReference));

        Assert.Multiple(() =>
        {
            Assert.That(modes.ReceiverFeedback,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(modes.OpacityMicromap,
                Is.EqualTo(opacity.EffectiveMode));
            Assert.That(modes.DirectionalGuiding,
                Is.EqualTo(guiding.EffectiveMode));
            Assert.That(modes.Caustics,
                Is.EqualTo(caustics.EffectiveMode));
            Assert.That(modes.NearFieldResidual,
                Is.EqualTo(residual.EffectiveMode));
            Assert.That(coordinator.GraphModes, Is.EqualTo(modes));
        });
    }

    private static GiExperimentModeState<TMode> Effective<TMode>(TMode mode)
        where TMode : struct, System.Enum =>
        new(
            mode,
            mode,
            mode,
            mode,
            GiExperimentFallbackReason.None,
            "valid",
            string.Empty);
}
