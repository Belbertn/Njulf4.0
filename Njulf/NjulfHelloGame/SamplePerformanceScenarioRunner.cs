using System;
using System.IO;
using System.Text.Json;

namespace NjulfHelloGame;

internal sealed class SamplePerformanceScenarioRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly SampleStressSceneBuilder _builder;
    private SamplePerformanceScenario _scenario;
    private SamplePerformanceScenarioSummary _lastSummary = new(
        SamplePerformanceScenario.Normal,
        0,
        0,
        0,
        0,
        0,
        "Normal sample scene");

    public SamplePerformanceScenarioRunner(SampleStressSceneBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public SamplePerformanceScenario CurrentScenario => _scenario;

    public SamplePerformanceScenarioSummary CurrentSummary => _lastSummary;

    public SamplePerformanceScenarioSummary CycleNext()
    {
        SamplePerformanceScenario[] scenarios = Enum.GetValues<SamplePerformanceScenario>();
        int index = Array.IndexOf(scenarios, _scenario);
        _scenario = scenarios[(index + 1) % scenarios.Length];
        _lastSummary = _builder.Apply(_scenario);
        return _lastSummary;
    }

    public SamplePerformanceScenarioSummary Apply(SamplePerformanceScenario scenario)
    {
        _scenario = scenario;
        _lastSummary = _builder.Apply(_scenario);
        return _lastSummary;
    }

    public string ExportCurrentSnapshot(string rootDirectory, Func<string, string> performanceSnapshotExporter)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Snapshot root directory is required.", nameof(rootDirectory));
        if (performanceSnapshotExporter == null)
            throw new ArgumentNullException(nameof(performanceSnapshotExporter));

        string scenarioDirectory = Path.Combine(rootDirectory, _lastSummary.Scenario.ToString());
        Directory.CreateDirectory(scenarioDirectory);
        string performancePath = performanceSnapshotExporter(scenarioDirectory);
        var manifest = new SamplePerformanceScenarioSnapshotManifest(
            SamplePerformanceScenarioSnapshotManifest.CurrentSchemaVersion,
            DateTimeOffset.Now,
            _lastSummary,
            performancePath,
            Enum.GetValues<SamplePerformanceScenario>());
        string manifestPath = Path.Combine(scenarioDirectory, $"scenario-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, SerializerOptions));
        return manifestPath;
    }
}
