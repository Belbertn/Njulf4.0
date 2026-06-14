namespace NjulfHelloGame;

internal enum SamplePerformanceScenario
{
    Normal,
    ManyStaticObjects,
    ManySkinnedObjects,
    DenseFoliage,
    ImpostorTransitionField,
    ManyLights,
    ShadowHeavy,
    ManyMaterials,
    ManyTransparentObjects,
    ParticleHeavy,
    PostProcessingDynamicResolution,
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

internal sealed record SamplePerformanceScenarioSnapshotManifest(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    SamplePerformanceScenarioSummary Scenario,
    string PerformanceSnapshotPath,
    IReadOnlyList<SamplePerformanceScenario> AvailableScenarios)
{
    public const int CurrentSchemaVersion = 1;
}
