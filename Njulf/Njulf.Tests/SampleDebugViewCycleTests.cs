using Njulf.Rendering.Data;
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
