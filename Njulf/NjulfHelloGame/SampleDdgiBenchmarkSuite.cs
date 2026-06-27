using System.Collections.Generic;
using System.Linq;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkSceneDescriptor(
    string Name,
    SamplePerformanceScenario Scenario,
    string Coverage,
    bool RequiredForProductionGate = true);

public static class SampleDdgiBenchmarkSuite
{
    public static IReadOnlyList<SampleBenchmarkSceneDescriptor> Scenes { get; } =
    [
        new(
            "ddgi-open-plaza",
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            "Open plaza baseline with fixed camera and large-world clipmap coverage."),
        new(
            "ddgi-closed-room",
            SamplePerformanceScenario.GiCornellRoom,
            "Closed room with colored bounce, local DDGI volume, and shadowed point light."),
        new(
            "ddgi-thin-wall",
            SamplePerformanceScenario.GiThinWallLeakTest,
            "Adjacent rooms separated by thin opaque geometry for leak validation."),
        new(
            "ddgi-long-corridor",
            SamplePerformanceScenario.GiLongCorridorOcclusion,
            "Long corridor with alternating occluders for distance falloff, visibility, and clipmap age validation."),
        new(
            "ddgi-foliage-heavy",
            SamplePerformanceScenario.ForestFoliage,
            "Foliage-heavy scene for AS visibility policy and indirect-lighting integration."),
        new(
            "ddgi-moving-object",
            SamplePerformanceScenario.GiMovingRigidObject,
            "Moving rigid occluder for dynamic dirty bounds and TLAS update cost."),
        new(
            "ddgi-moving-light",
            SamplePerformanceScenario.GiMovingPointLight,
            "Moving point light invalidation and probe refresh behavior."),
        new(
            "ddgi-emissive-material",
            SamplePerformanceScenario.GiEmissiveMaterialRoom,
            "Emissive material fixture for hit-lighting emissive contribution and color bleeding."),
        new(
            "ddgi-local-volume-streaming",
            SamplePerformanceScenario.GiLocalVolumeStreaming,
            "Multiple authored DDGI volumes around clipmaps to validate local-volume streaming and clipmap reservation."),
        new(
            "ddgi-fast-traversal-teleport",
            SamplePerformanceScenario.GiFastTraversalTeleport,
            "Fast traversal and teleport reset scene for clipmap invalidation without resource churn."),
        new(
            "ddgi-bright-exterior-room",
            SamplePerformanceScenario.GiBrightExteriorRoom,
            "Small room with bright exterior aperture and emissive/direct-light pressure.",
            RequiredForProductionGate: false),
        new(
            "ddgi-reflection-heavy",
            SamplePerformanceScenario.ReflectionHeavy,
            "Reflection probe memory/timing alongside DDGI and AO diagnostics.",
            RequiredForProductionGate: false),
        new(
            "ddgi-many-lights",
            SamplePerformanceScenario.ManyLights,
            "Many-light stress scene for DDGI hit-lighting upper-bound counters.",
            RequiredForProductionGate: false),
        new(
            "ddgi-combined-worst-case",
            SamplePerformanceScenario.CombinedWorstCase,
            "Combined deterministic stress scene for budget and p95 validation.",
            RequiredForProductionGate: false)
    ];

    public static IReadOnlyList<SampleBenchmarkSceneDescriptor> RequiredProductionGateScenes { get; } =
        Scenes.Where(scene => scene.RequiredForProductionGate).ToArray();
}
