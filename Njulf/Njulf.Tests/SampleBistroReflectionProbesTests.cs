using System.Linq;
using Njulf.Core.Scene;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBistroReflectionProbesTests
{
    [Test]
    public void Configure_AddsBroadAndFocusedBoxProjectedProbes()
    {
        using var scene = new Scene();

        SampleBistroReflectionProbes.Configure(scene);

        Assert.Multiple(() =>
        {
            Assert.That(scene.ReflectionProbes, Has.Count.EqualTo(2));
            Assert.That(
                scene.ReflectionProbes.Select(static probe => probe.Name),
                Is.EquivalentTo(new[] { "BistroCourtyard", "BistroCafeCorner" }));
            Assert.That(
                scene.ReflectionProbes,
                Has.All.Matches<ReflectionProbe>(static probe =>
                    probe.Shape == ReflectionProbeShape.Box &&
                    probe.BoxProjection));
            Assert.That(
                scene.ReflectionProbes.Max(static probe => probe.Priority),
                Is.EqualTo(1));
        });
    }
}
