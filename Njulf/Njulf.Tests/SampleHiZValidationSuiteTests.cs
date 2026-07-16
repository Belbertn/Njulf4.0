using System;
using System.Linq;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleHiZValidationSuiteTests
{
    [Test]
    public void Scenes_CoverHiZFallbackAndValidationSet()
    {
        SampleHiZValidationSceneDescriptor[] scenes = SampleHiZValidationSuite.Scenes.ToArray();
        string[] names = scenes.Select(scene => scene.Name).ToArray();
        string[] distinctNames = names.Distinct(StringComparer.Ordinal).ToArray();
        string[] requiredNames = SampleHiZValidationSuite.RequiredProductionGateScenes
            .Select(scene => scene.Name)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("hiz-occluder-wall"));
            Assert.That(names, Does.Contain("hiz-open-courtyard"));
            Assert.That(names, Does.Contain("hiz-fast-camera-pan"));
            Assert.That(names, Does.Contain("hiz-teleport-camera-cut"));
            Assert.That(names, Does.Contain("hiz-animated-skinned-objects"));
            Assert.That(requiredNames, Has.Length.EqualTo(4));
            Assert.That(distinctNames, Has.Length.EqualTo(scenes.Length));
            Assert.That(scenes.Select(scene => scene.Coverage), Has.All.Not.Empty);
            Assert.That(scenes.Select(scene => scene.Scenario), Does.Contain(SamplePerformanceScenario.GiLongCorridorOcclusion));
            Assert.That(scenes.Select(scene => scene.Scenario), Does.Contain(SamplePerformanceScenario.GiSponzaRightWallStationary));
            Assert.That(scenes.Select(scene => scene.Scenario), Does.Contain(SamplePerformanceScenario.GiFastTraversalTeleport));
        });
    }
}
