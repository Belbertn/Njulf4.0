using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleDebugViewCycleTests
{
    [Test]
    public void GlobalIlluminationCycle_ReachesEveryShippingMaterialGiViewExactlyOnce()
    {
        var visited = new HashSet<GlobalIlluminationDebugView>();
        GlobalIlluminationDebugView current = GlobalIlluminationDebugView.None;
        do
        {
            Assert.That(visited.Add(current), Is.True, $"Cycle repeated {current} before returning to None.");
            current = SampleInputController.NextGlobalIlluminationDebugView(current);
        } while (current != GlobalIlluminationDebugView.None);

        Assert.Multiple(() =>
        {
            Assert.That(visited, Does.Contain(GlobalIlluminationDebugView.MaterialTransportHitProvenance));
            Assert.That(visited, Does.Contain(GlobalIlluminationDebugView.FarFieldOccupancySlice));
            Assert.That(visited, Does.Contain(GlobalIlluminationDebugView.FarFieldSunShadow));
            if (RendererBuildFeatures.DdgiVisualDebugViewsCompiled)
            {
                Assert.That(visited, Does.Contain(GlobalIlluminationDebugView.DdgiIrradiance));
                Assert.That(visited, Does.Contain(GlobalIlluminationDebugView.DdgiProbeState));
                Assert.That(visited, Does.Contain(GlobalIlluminationDebugView.DdgiPhysicalPage));
            }
            Assert.That(
                visited.All(RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable),
                Is.True);
        });
    }

    [Test]
    public void MaterialCycle_ReachesEveryInteractiveTransportViewAndExcludesCaptureOnlyModes()
    {
        var visited = new HashSet<MaterialDebugView>();
        MaterialDebugView current = MaterialDebugView.None;
        do
        {
            Assert.That(visited.Add(current), Is.True, $"Cycle repeated {current} before returning to None.");
            current = SampleInputController.NextMaterialDebugView(current);
        } while (current != MaterialDebugView.None);

        Assert.Multiple(() =>
        {
            for (uint value = (uint)MaterialDebugView.MaterialOcclusion;
                 value <= (uint)MaterialDebugView.MaterialRevisions;
                 value++)
            {
                Assert.That(visited, Does.Contain((MaterialDebugView)value));
            }
            Assert.That(visited, Does.Not.Contain(MaterialDebugView.CaptureLinearDirectDiffuse));
            Assert.That(visited, Does.Not.Contain(MaterialDebugView.CaptureLinearDirectSpecular));
        });
    }
}
