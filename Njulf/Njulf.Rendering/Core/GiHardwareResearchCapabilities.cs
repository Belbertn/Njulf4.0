namespace Njulf.Rendering.Core;

public readonly record struct OpacityMicromapCapabilitySnapshot(
    bool ExtensionAvailable,
    bool FeatureAvailable,
    bool HostCommandsAvailable,
    uint MaximumTwoStateSubdivisionLevel,
    uint MaximumFourStateSubdivisionLevel);

public readonly record struct RayTracingInvocationReorderCapabilitySnapshot(
    bool RayTracingPipelineExtensionAvailable,
    bool RayTracingPipelineFeatureAvailable,
    bool InvocationReorderExtensionAvailable,
    bool InvocationReorderFeatureAvailable,
    uint ReorderingHint,
    uint MaximumShaderBindingTableRecordIndex)
{
    public bool EffectiveReorderingHint => ReorderingHint != 0u;
}

public readonly record struct GiHardwareResearchCapabilities(
    OpacityMicromapCapabilitySnapshot OpacityMicromap,
    RayTracingInvocationReorderCapabilitySnapshot RayTracingInvocationReorder)
{
    public static GiHardwareResearchCapabilities None { get; } = new(
        default,
        default);
}
