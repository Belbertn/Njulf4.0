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
    public void SsgiComposite_AllowsOnlyNormalAndFinalIndirectForwardOutputs()
    {
        var normal = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.None);
        var finalIndirect = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.FinalIndirect);
        var raw = CreateEnabledSsgiSettings(GlobalIlluminationDebugView.SsgiRaw);

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

        Assert.Multiple(() =>
        {
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(disabled), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(disabled), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(ssgiOff), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(ssgiOff), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(ddgiOnly), Is.False);
            Assert.That(GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi(ddgiOnly), Is.False);
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
