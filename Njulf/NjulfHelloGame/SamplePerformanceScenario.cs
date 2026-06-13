namespace NjulfHelloGame;

internal enum SamplePerformanceScenario
{
    Normal,
    ManyLights,
    ManyMaterials,
    ManyTransparentObjects,
    LargeMeshletCount,
    ReflectionHeavy,
    UploadBurst,
    CombinedWorstCase
}

internal sealed record SamplePerformanceScenarioSummary(
    SamplePerformanceScenario Scenario,
    int ObjectCount,
    int LightCount,
    int MaterialCount,
    int TransparentObjectCount,
    int ReflectionProbeCount,
    string Notes);
