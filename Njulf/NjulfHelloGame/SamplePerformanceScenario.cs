namespace NjulfHelloGame;

public enum SamplePerformanceScenario
{
    Normal,
    ManyLights,
    ManyMaterials,
    ManyTransparentObjects,
    LargeMeshletCount,
    FoliageLikeStaticInstances,
    FoliageDebugFallback,
    DenseGrassField,
    ShrubFoliage,
    MixedTreeLineFoliage,
    MixedTreeLineFoliageNoShadows,
    ForestFoliage,
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
