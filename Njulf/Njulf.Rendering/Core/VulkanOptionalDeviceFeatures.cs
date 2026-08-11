namespace Njulf.Rendering.Core;

/// <summary>
/// Requested optional device-create chains. These controls are intentionally
/// separate from physical-device capability discovery: startup settings or an
/// explicit caller/environment override must request a feature, and a driver
/// advertisement alone never enables it.
/// </summary>
public readonly record struct VulkanOptionalDeviceFeatures(
    bool EnableExtOpacityMicromap)
{
    public static VulkanOptionalDeviceFeatures Disabled => default;

    public static VulkanOptionalDeviceFeatures FromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(
            "NJULF_ENABLE_EXT_OPACITY_MICROMAP");
        return new VulkanOptionalDeviceFeatures(ParseBoolean(value));
    }

    internal static bool ParseBoolean(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal) ||
        bool.TryParse(value, out bool enabled) && enabled;
}

/// <summary>
/// Physical-device facts sampled before logical-device creation.  This data is
/// kept independent from the enabled-device report so the latter can honestly
/// distinguish unsupported hardware from an optional chain that was not
/// requested or could not be enabled.
/// </summary>
internal readonly record struct VulkanExtOpacityMicromapDeviceSupport(
    bool ExtensionAdvertised,
    bool MicromapFeatureSupported,
    bool AccelerationStructureExtensionSupported,
    bool AccelerationStructureFeatureSupported,
    bool BufferDeviceAddressSupported,
    bool DeferredHostOperationsExtensionSupported,
    uint MaximumFourStateSubdivisionLevel)
{
    public bool CanEnable =>
        ExtensionAdvertised &&
        MicromapFeatureSupported &&
        AccelerationStructureExtensionSupported &&
        AccelerationStructureFeatureSupported &&
        BufferDeviceAddressSupported &&
        DeferredHostOperationsExtensionSupported &&
        MaximumFourStateSubdivisionLevel != 0U;
}
