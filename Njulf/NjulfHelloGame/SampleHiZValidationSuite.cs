using System.Collections.Generic;
using System.Linq;

namespace NjulfHelloGame;

public sealed record SampleHiZValidationSceneDescriptor(
    string Name,
    SamplePerformanceScenario Scenario,
    string Coverage,
    bool RequiredForProductionGate = true);

public static class SampleHiZValidationSuite
{
    public static IReadOnlyList<SampleHiZValidationSceneDescriptor> Scenes { get; } =
    [
        new(
            "hiz-occluder-wall",
            SamplePerformanceScenario.GiLongCorridorOcclusion,
            "High-occlusion corridor with repeated opaque occluders for nonzero tested and culled counts."),
        new(
            "hiz-open-courtyard",
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            "Open/no-occlusion scene for adaptive suppression and build-with-zero-tested warnings."),
        new(
            "hiz-fast-camera-pan",
            SamplePerformanceScenario.GiFastTraversalTeleport,
            "Fast traversal path for previous-frame Hi-Z camera-motion suppression."),
        new(
            "hiz-teleport-camera-cut",
            SamplePerformanceScenario.GiFastTraversalTeleport,
            "Teleport/camera-cut path for previous-pyramid invalidation and compacted-no-Hi-Z fallback."),
        new(
            "hiz-animated-skinned-objects",
            SamplePerformanceScenario.Normal,
            "Default sample scene with animated/skinned content for correctness when Hi-Z paths are disabled.",
            RequiredForProductionGate: false)
    ];

    public static IReadOnlyList<SampleHiZValidationSceneDescriptor> RequiredProductionGateScenes { get; } =
        Scenes.Where(scene => scene.RequiredForProductionGate).ToArray();
}
