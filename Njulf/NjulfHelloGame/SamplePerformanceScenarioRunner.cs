using System;

namespace NjulfHelloGame;

internal sealed class SamplePerformanceScenarioRunner
{
    private readonly SampleStressSceneBuilder _builder;
    private SamplePerformanceScenario _scenario;

    public SamplePerformanceScenarioRunner(SampleStressSceneBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public SamplePerformanceScenario CurrentScenario => _scenario;

    public SamplePerformanceScenarioSummary CycleNext()
    {
        SamplePerformanceScenario[] scenarios = Enum.GetValues<SamplePerformanceScenario>();
        int index = Array.IndexOf(scenarios, _scenario);
        _scenario = scenarios[(index + 1) % scenarios.Length];
        return _builder.Apply(_scenario);
    }

    public SamplePerformanceScenarioSummary Apply(SamplePerformanceScenario scenario)
    {
        _scenario = scenario;
        return _builder.Apply(_scenario);
    }
}
