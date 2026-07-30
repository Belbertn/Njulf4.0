using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationPassExecutionPolicyTests
{
    [TestCase(GlobalIlluminationDebugView.None, false, false, true)]
    [TestCase(GlobalIlluminationDebugView.FinalIndirect, false, false, true)]
    [TestCase(GlobalIlluminationDebugView.SsgiRaw, false, true, false)]
    [TestCase(GlobalIlluminationDebugView.SsgiFiltered, false, true, false)]
    [TestCase(GlobalIlluminationDebugView.SsgiHistory, false, true, false)]
    [TestCase(GlobalIlluminationDebugView.SsgiRayHitMask, false, true, false)]
    [TestCase(GlobalIlluminationDebugView.SsgiHistoryRejection, false, true, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiIrradiance, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiVisibility, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiProbeIndex, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiProbeState, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiProbeRelocation, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiLeakClamp, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.RayQueryCost, false, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiCoverage, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiCascadeSelection, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiCascadeBlendWeight, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiUpdateReasons, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiRayBudget, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiGatherLocalVolume, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiGatherClipmap, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiGatherBlendWeight, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiGatherFallback, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiRawDiffuse, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiSuppressionMask, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiEffectiveWeight, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiRelocationNormalized, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiClassificationInvalidScore, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiVisibilityMoments, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiSpatialCoverage, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiSupportCoverage, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiDataConfidence, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiDirectionalSupport, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiVisibilityConfidence, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiConfidenceChain, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiProbeLogicalPosition, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiProbeRelocatedPosition, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiProbeRelocationDirection, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiSampledIrradiance, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiFinalDiffuse, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.DdgiConfidenceBypass, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.FarFieldOccupancySlice, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.FarFieldTraceResult, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.FarFieldSkyVisibility, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.FarFieldSunShadow, true, false, false)]
    [TestCase(GlobalIlluminationDebugView.MaterialTransportSourceOwnership, false, true, true)]
    [TestCase(GlobalIlluminationDebugView.HybridEstimatorOwnership, false, true, true)]
    [TestCase(GlobalIlluminationDebugView.HybridFinalComposition, false, true, true)]
    [TestCase(GlobalIlluminationDebugView.MaterialTransportHitProvenance, false, true, true)]
    public void DebugViews_MapToExpectedExecutionPolicy(
        GlobalIlluminationDebugView view,
        bool expectedDdgiDebug,
        bool expectedSsgiDebug,
        bool expectedComposite)
    {
        var gi = CreateEnabledSsgiSettings(view);

        Assert.Multiple(() =>
        {
            Assert.That(GlobalIlluminationPassExecutionPolicy.IsDdgiDebugView(view), Is.EqualTo(expectedDdgiDebug));
            Assert.That(GlobalIlluminationPassExecutionPolicy.IsSsgiDebugView(view), Is.EqualTo(expectedSsgiDebug));
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(gi), Is.True);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(gi), Is.EqualTo(expectedComposite));
        });
    }

    [Test]
    public void DebugViewClassification_CoversEveryDefinedViewExactlyOnce()
    {
        var neutralViews = new HashSet<GlobalIlluminationDebugView>
        {
            GlobalIlluminationDebugView.None,
            GlobalIlluminationDebugView.FinalIndirect,
            GlobalIlluminationDebugView.RayQueryCost
        };

        foreach (GlobalIlluminationDebugView view in Enum.GetValues<GlobalIlluminationDebugView>())
        {
            int classificationCount =
                (neutralViews.Contains(view) ? 1 : 0) +
                (GlobalIlluminationPassExecutionPolicy.IsDdgiDebugView(view) ? 1 : 0) +
                (GlobalIlluminationPassExecutionPolicy.IsSsgiDebugView(view) ? 1 : 0);

            Assert.That(classificationCount, Is.EqualTo(1), view.ToString());
        }
    }

    [Test]
    public void SsgiProducer_RunsWhileDdgiDebugViewsAreDisplayed()
    {
        foreach (GlobalIlluminationDebugView view in Enum.GetValues<GlobalIlluminationDebugView>())
        {
            if (!GlobalIlluminationPassExecutionPolicy.IsDdgiDebugView(view))
                continue;

            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(CreateEnabledSsgiSettings(view)),
                Is.True,
                view.ToString());
        }
    }

    [Test]
    public void SsgiComposite_AllowsNormalFinalAndStandaloneHybridDiagnosticOutputs()
    {
        var normal = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.None);
        var finalIndirect = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.FinalIndirect);
        var raw = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.SsgiRaw);
        var sourceOwnership = CreateEnabledSsgiSettings(
            GlobalIlluminationDebugView.MaterialTransportSourceOwnership);
        var estimatorOwnership = CreateEnabledSsgiSettings(
            GlobalIlluminationDebugView.HybridEstimatorOwnership);
        var finalComposition = CreateEnabledSsgiSettings(
            GlobalIlluminationDebugView.HybridFinalComposition);
        var transportProvenance = CreateEnabledSsgiSettings(
            GlobalIlluminationDebugView.MaterialTransportHitProvenance);

        Assert.Multiple(() =>
        {
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(
                    normal,
                    GlobalIlluminationPassExecutionPolicy.ForwardDebugViewNone),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(
                    finalIndirect,
                    GlobalIlluminationPassExecutionPolicy.ForwardDebugViewGlobalIlluminationFinalIndirect),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(normal, 1u),
                Is.False);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(raw, 81u),
                Is.False);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(
                    sourceOwnership,
                    GlobalIlluminationPassExecutionPolicy.ForwardDebugViewNone),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(
                    estimatorOwnership,
                    GlobalIlluminationPassExecutionPolicy.ForwardDebugViewNone),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(
                    finalComposition,
                    GlobalIlluminationPassExecutionPolicy.ForwardDebugViewNone),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(
                    transportProvenance,
                    GlobalIlluminationPassExecutionPolicy.ForwardDebugViewNone),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(normal, 1u),
                Is.False);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(raw, 81u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.DdgiCoverage),
                    92u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.DdgiSuppressionMask),
                    102u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.DdgiConfidenceBypass),
                    119u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.FarFieldOccupancySlice),
                    120u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.FarFieldTraceResult),
                    121u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.FarFieldSkyVisibility),
                    122u),
                Is.True);
            Assert.That(
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(
                    CreateEnabledSsgiSettings(GlobalIlluminationDebugView.FarFieldSunShadow),
                    123u),
                Is.True);
        });
    }

    [Test]
    public void SsgiPolicies_RequireEffectiveSsgi()
    {
        var disabled = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.None);
        disabled.Enabled = false;

        var ssgiOff = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.None);
        ssgiOff.UseSsgi = false;

        var ddgiOnly = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.None);
        ddgiOnly.Mode = GlobalIlluminationMode.Ddgi;

        var ddgiOnlyProvenance = CreateEnabledSsgiSettings(
            GlobalIlluminationDebugView.MaterialTransportHitProvenance);
        ddgiOnlyProvenance.UseSsgi = false;
        ddgiOnlyProvenance.Mode = GlobalIlluminationMode.Ddgi;

        Assert.Multiple(() =>
        {
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(disabled), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(disabled), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(ssgiOff), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(ssgiOff), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(ddgiOnly), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(ddgiOnly), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(ddgiOnlyProvenance), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(ddgiOnlyProvenance), Is.True);
        });
    }

    private static GlobalIlluminationSettings CreateEnabledSsgiSettings(GlobalIlluminationDebugView view)
    {
        return new GlobalIlluminationSettings
        {
            Enabled = true,
            UseSsgi = true,
            Mode = GlobalIlluminationMode.Hybrid,
            DebugView = view
        };
    }
}
