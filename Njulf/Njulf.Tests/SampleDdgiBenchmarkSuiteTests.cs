using System.Linq;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleDdgiBenchmarkSuiteTests
{
    [Test]
    public void BenchmarkScenes_UseSimpleDdgiValidationScenarios()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SampleDdgiBenchmarkSuite.Scenes, Is.Not.Empty);
            Assert.That(SampleDdgiBenchmarkSuite.Scenes.Select(scene => scene.Scenario),
                Does.Contain(SamplePerformanceScenario.GiCornellRoom));
            Assert.That(SampleGlobalIlluminationValidation.Phase7ProductionScenes,
                Has.Some.Matches<SampleGiProductionScene>(scene => scene.Name.StartsWith("simple-ddgi")));
        });
    }
}
