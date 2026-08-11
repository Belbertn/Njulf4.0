using System;

namespace Njulf.Rendering.Data;

/// <summary>
/// Immutable content classes used to select prewarmed Simple-DDGI trace
/// programs. A specialized class may remove work only when every fact needed
/// by that program is known for the complete published ray scene.
/// </summary>
public enum SimpleDdgiTraceContentProfile : byte
{
    General = 0,
    Opaque = 1,
    OpaqueSingleSun = 2
}

public enum SimpleDdgiTraceDistanceProfile : byte
{
    Dynamic = 0,
    CompleteRayScene = 1,
    SplitFarField = 2
}

public readonly record struct SimpleDdgiTraceContentFacts(
    SimpleDdgiStoragePackingMode StoragePackingMode,
    bool DetailedDiagnosticsCompiled,
    bool HasAlphaCandidateGeometry,
    bool HasThinTransmissionGeometry,
    int DirectionalLightCount,
    int LocalLightCount,
    int EmissiveSourceCount,
    bool CompleteRayScene,
    bool FarFieldCoverageReady);

public readonly record struct SimpleDdgiTraceVariantSelection(
    SimpleDdgiTraceContentProfile ContentProfile,
    SimpleDdgiTraceDistanceProfile DistanceProfile,
    int WorkgroupSize,
    bool Specialized)
{
    public static SimpleDdgiTraceVariantSelection General64 { get; } = new(
        SimpleDdgiTraceContentProfile.General,
        SimpleDdgiTraceDistanceProfile.Dynamic,
        64,
        false);
}

/// <summary>
/// Quality-neutral selector for content-specialized ray-query programs. It
/// never infers facts from vendor names or delayed counters: unsupported,
/// diagnostic, validation, and mixed-content frames use the general shader.
/// </summary>
public static class SimpleDdgiTraceVariantSelector
{
    public static SimpleDdgiTraceVariantSelection Select(
        SimpleDdgiTraceContentFacts facts,
        int measuredWorkgroupSize = 64)
    {
        if (facts.StoragePackingMode.Sanitize() !=
                SimpleDdgiStoragePackingMode.Packed ||
            facts.DetailedDiagnosticsCompiled)
        {
            return SimpleDdgiTraceVariantSelection.General64;
        }

        int workgroupSize = measuredWorkgroupSize is 32 or 64
            ? measuredWorkgroupSize
            : 64;
        // Only the locked 64-lane program is currently admitted. The selector
        // accepts measured evidence now so a future 32-lane artifact can be
        // enabled without changing content classification or stable ray IDs.
        if (workgroupSize != 64)
            return SimpleDdgiTraceVariantSelection.General64;

        SimpleDdgiTraceDistanceProfile distanceProfile =
            facts.FarFieldCoverageReady && !facts.CompleteRayScene
                ? SimpleDdgiTraceDistanceProfile.SplitFarField
                : SimpleDdgiTraceDistanceProfile.CompleteRayScene;

        SimpleDdgiTraceContentProfile contentProfile =
            facts.HasAlphaCandidateGeometry || facts.HasThinTransmissionGeometry
                ? SimpleDdgiTraceContentProfile.General
                : facts.DirectionalLightCount == 1 &&
                  facts.LocalLightCount == 0 &&
                  facts.EmissiveSourceCount == 0
                    ? SimpleDdgiTraceContentProfile.OpaqueSingleSun
                    : SimpleDdgiTraceContentProfile.Opaque;

        return new SimpleDdgiTraceVariantSelection(
            contentProfile,
            distanceProfile,
            workgroupSize,
            true);
    }

    public static string ResolveShaderStem(
        SimpleDdgiTraceVariantSelection selection)
    {
        if (!selection.Specialized ||
            selection.DistanceProfile == SimpleDdgiTraceDistanceProfile.Dynamic)
        {
            return "packed";
        }

        string content = selection.ContentProfile switch
        {
            SimpleDdgiTraceContentProfile.General => "general",
            SimpleDdgiTraceContentProfile.Opaque => "opaque",
            SimpleDdgiTraceContentProfile.OpaqueSingleSun => "opaque_sun",
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
        string distance = selection.DistanceProfile switch
        {
            SimpleDdgiTraceDistanceProfile.CompleteRayScene => "complete",
            SimpleDdgiTraceDistanceProfile.SplitFarField => "split",
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
        return $"packed_{content}_{distance}";
    }
}
