using Njulf.Assets.Cooked;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Exact device-creation and dispatch facts required by the C1 EXT backend.
/// These are deliberately facts about the <em>enabled logical device</em>, not
/// merely properties advertised by a physical device.  An advertised extension
/// that was omitted from the device-create chain is unusable and must retain the
/// ordinary candidate-tested BLAS path.
/// </summary>
public readonly record struct VulkanExtOpacityMicromapFeatureSnapshot(
    bool ExtensionAdvertised,
    bool ExtensionEnabled,
    bool MicromapFeatureEnabled,
    bool AccelerationStructureExtensionEnabled,
    bool BufferDeviceAddressEnabled,
    bool DeferredHostOperationsExtensionEnabled,
    bool NativeDispatchLoaded,
    bool CommandBufferBuildEnabled,
    bool CompactedSizeQueryEnabled,
    bool BlasOpacityAttachmentEnabled,
    uint MaximumFourStateSubdivisionLevel)
{
    /// <summary>
    /// Creates the portion of the snapshot that comes directly from Silk.NET's
    /// <see cref="PhysicalDeviceOpacityMicromapFeaturesEXT"/> and
    /// <see cref="PhysicalDeviceOpacityMicromapPropertiesEXT"/> query chain.
    /// The caller still supplies the logical-device enablement and renderer
    /// ownership facts; querying a physical device alone cannot establish them.
    /// </summary>
    public static VulkanExtOpacityMicromapFeatureSnapshot FromSilkQuery(
        bool extensionAdvertised,
        bool extensionEnabled,
        bool accelerationStructureExtensionEnabled,
        bool bufferDeviceAddressEnabled,
        bool deferredHostOperationsExtensionEnabled,
        bool nativeDispatchLoaded,
        bool commandBufferBuildEnabled,
        bool compactedSizeQueryEnabled,
        bool blasOpacityAttachmentEnabled,
        in PhysicalDeviceOpacityMicromapFeaturesEXT features,
        in PhysicalDeviceOpacityMicromapPropertiesEXT properties) => new(
            ExtensionAdvertised: extensionAdvertised,
            ExtensionEnabled: extensionEnabled,
            MicromapFeatureEnabled: features.Micromap,
            AccelerationStructureExtensionEnabled:
                accelerationStructureExtensionEnabled,
            BufferDeviceAddressEnabled: bufferDeviceAddressEnabled,
            DeferredHostOperationsExtensionEnabled:
                deferredHostOperationsExtensionEnabled,
            NativeDispatchLoaded: nativeDispatchLoaded,
            CommandBufferBuildEnabled: commandBufferBuildEnabled,
            CompactedSizeQueryEnabled: compactedSizeQueryEnabled,
            BlasOpacityAttachmentEnabled: blasOpacityAttachmentEnabled,
            MaximumFourStateSubdivisionLevel:
                properties.MaxOpacity4StateSubdivisionLevel);
}

public enum OpacityMicromapExtCapabilityFailure : byte
{
    None = 0,
    ExtensionNotAdvertised,
    ExtensionNotEnabled,
    MicromapFeatureNotEnabled,
    AccelerationStructureDependencyNotEnabled,
    BufferDeviceAddressNotEnabled,
    DeferredHostOperationsNotEnabled,
    NativeDispatchNotLoaded,
    CommandBufferBuildNotAvailable,
    FourStateFormatNotAvailable,
    BlasAttachmentNotIntegrated,
    CompactionRequiredButUnavailable
}

/// <summary>
/// The result of evaluating the EXT feature chain.  The common runtime
/// capability struct remains available for the generic backend selector while
/// this result retains the precise reason that C1 could not be activated.
/// </summary>
public readonly record struct OpacityMicromapExtCapabilityReport(
    OpacityMicromapRuntimeCapabilities Capabilities,
    bool NativeBuildPathAvailable,
    OpacityMicromapExtCapabilityFailure Failure,
    string Detail)
{
    public bool SupportsPublication =>
        NativeBuildPathAvailable && Failure == OpacityMicromapExtCapabilityFailure.None;
}

/// <summary>
/// Converts device facts into a fail-closed C1 capability report.  The checks
/// mirror the EXT object's GPU command-buffer path: EXT opacity micromaps,
/// KHR acceleration structures, buffer device addresses, deferred host
/// operations, the <c>micromap</c> device feature, an EXT command dispatch,
/// and non-zero four-state subdivision support are all required.
/// </summary>
public static class VulkanExtOpacityMicromapCapabilityInspector
{
    public const string ExtensionName = "VK_EXT_opacity_micromap";
    public const string AccelerationStructureExtensionName =
        "VK_KHR_acceleration_structure";
    public const string BufferDeviceAddressExtensionName =
        "VK_KHR_buffer_device_address";
    public const string DeferredHostOperationsExtensionName =
        "VK_KHR_deferred_host_operations";

    public static OpacityMicromapExtCapabilityReport Evaluate(
        in VulkanExtOpacityMicromapFeatureSnapshot snapshot,
        bool requireCompaction = false)
    {
        bool extensionAvailable = snapshot.ExtensionAdvertised &&
            snapshot.ExtensionEnabled;
        bool accelerationStructureDependencyAvailable =
            snapshot.AccelerationStructureExtensionEnabled &&
            snapshot.BufferDeviceAddressEnabled &&
            snapshot.DeferredHostOperationsExtensionEnabled &&
            snapshot.BlasOpacityAttachmentEnabled;
        bool commandBufferBuildAvailable = snapshot.NativeDispatchLoaded &&
            snapshot.CommandBufferBuildEnabled;
        bool fourStateFormatAvailable =
            snapshot.MaximumFourStateSubdivisionLevel != 0;

        var capabilities = new OpacityMicromapRuntimeCapabilities(
            ExtensionAvailable: extensionAvailable,
            FeatureEnabled: snapshot.MicromapFeatureEnabled,
            AccelerationStructureDependencyAvailable:
                accelerationStructureDependencyAvailable,
            CommandBufferBuildAvailable: commandBufferBuildAvailable,
            FourStateFormatAvailable: fourStateFormatAvailable,
            MaximumFourStateSubdivisionLevel:
                snapshot.MaximumFourStateSubdivisionLevel,
            CompactionAvailable: commandBufferBuildAvailable &&
                snapshot.CompactedSizeQueryEnabled);

        OpacityMicromapExtCapabilityFailure failure;
        string detail;
        if (!snapshot.ExtensionAdvertised)
        {
            failure = OpacityMicromapExtCapabilityFailure.ExtensionNotAdvertised;
            detail = "VK_EXT_opacity_micromap-not-advertised";
        }
        else if (!snapshot.ExtensionEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure.ExtensionNotEnabled;
            detail = "VK_EXT_opacity_micromap-not-enabled-on-logical-device";
        }
        else if (!snapshot.MicromapFeatureEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure.MicromapFeatureNotEnabled;
            detail = "VkPhysicalDeviceOpacityMicromapFeaturesEXT.micromap-not-enabled";
        }
        else if (!snapshot.AccelerationStructureExtensionEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure
                .AccelerationStructureDependencyNotEnabled;
            detail = "VK_KHR_acceleration_structure-not-enabled";
        }
        else if (!snapshot.BufferDeviceAddressEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure.BufferDeviceAddressNotEnabled;
            detail = "buffer-device-address-not-enabled";
        }
        else if (!snapshot.DeferredHostOperationsExtensionEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure
                .DeferredHostOperationsNotEnabled;
            detail = "VK_KHR_deferred_host_operations-not-enabled";
        }
        else if (!snapshot.NativeDispatchLoaded)
        {
            failure = OpacityMicromapExtCapabilityFailure.NativeDispatchNotLoaded;
            detail = "Silk.NET-ExtOpacityMicromap-dispatch-not-loaded";
        }
        else if (!snapshot.CommandBufferBuildEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure.CommandBufferBuildNotAvailable;
            detail = "GPU-command-buffer-micromap-build-path-not-integrated";
        }
        else if (!fourStateFormatAvailable)
        {
            failure = OpacityMicromapExtCapabilityFailure.FourStateFormatNotAvailable;
            detail = "maxOpacity4StateSubdivisionLevel-is-zero";
        }
        else if (!snapshot.BlasOpacityAttachmentEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure.BlasAttachmentNotIntegrated;
            detail = "matching-BLAS-opacity-micromap-attachment-not-integrated";
        }
        else if (requireCompaction && !snapshot.CompactedSizeQueryEnabled)
        {
            failure = OpacityMicromapExtCapabilityFailure
                .CompactionRequiredButUnavailable;
            detail = "required-micromap-compaction-query-path-not-integrated";
        }
        else
        {
            failure = OpacityMicromapExtCapabilityFailure.None;
            detail = "vulkan-ext-four-state-command-buffer-build-capable";
        }

        return new OpacityMicromapExtCapabilityReport(
            capabilities,
            NativeBuildPathAvailable:
                failure == OpacityMicromapExtCapabilityFailure.None,
            failure,
            detail);
    }
}

/// <summary>
/// Thin strongly-typed view over the Silk.NET 2.23.0 EXT dispatch.  It is kept
/// separate from the lifecycle manager because only the acceleration-structure
/// owner can construct valid device addresses and chain the micromap into a
/// matching BLAS geometry.  This class intentionally exposes only the GPU
/// command-buffer path; C1 never falls back to synchronous host builds.
/// </summary>
public sealed unsafe class SilkNetExtOpacityMicromapCommandApi
{
    private readonly ExtOpacityMicromap _dispatch;

    public SilkNetExtOpacityMicromapCommandApi(ExtOpacityMicromap dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public void GetMicromapBuildSizes(
        Device device,
        AccelerationStructureBuildTypeKHR buildType,
        ref MicromapBuildInfoEXT buildInfo,
        ref MicromapBuildSizesInfoEXT sizes)
    {
        MicromapBuildInfoEXT* buildInfoPointer = stackalloc MicromapBuildInfoEXT[1];
        MicromapBuildSizesInfoEXT* sizesPointer =
            stackalloc MicromapBuildSizesInfoEXT[1];
        buildInfoPointer[0] = buildInfo;
        sizesPointer[0] = sizes;
        _dispatch.GetMicromapBuildSizes(
            device,
            buildType,
            buildInfoPointer,
            sizesPointer);
        sizes = sizesPointer[0];
    }

    public Result CreateMicromap(
        Device device,
        in MicromapCreateInfoEXT createInfo,
        out MicromapEXT micromap)
    {
        MicromapCreateInfoEXT* createInfoPointer =
            stackalloc MicromapCreateInfoEXT[1];
        MicromapEXT* nativeMicromapPointer = stackalloc MicromapEXT[1];
        createInfoPointer[0] = createInfo;
        Result result = _dispatch.CreateMicromap(
            device,
            createInfoPointer,
            null,
            nativeMicromapPointer);
        micromap = nativeMicromapPointer[0];
        return result;
    }

    public void DestroyMicromap(Device device, MicromapEXT micromap)
    {
        if (micromap.Handle != 0UL)
            _dispatch.DestroyMicromap(device, micromap, null);
    }

    public void CmdBuildMicromaps(
        CommandBuffer commandBuffer,
        ReadOnlySpan<MicromapBuildInfoEXT> buildInfos)
    {
        if (buildInfos.IsEmpty)
            throw new ArgumentException("At least one micromap build is required.", nameof(buildInfos));

        fixed (MicromapBuildInfoEXT* buildInfo = buildInfos)
        {
            _dispatch.CmdBuildMicromap(
                commandBuffer,
                checked((uint)buildInfos.Length),
                buildInfo);
        }
    }

    public void CmdWriteCompactedSize(
        CommandBuffer commandBuffer,
        ReadOnlySpan<MicromapEXT> micromaps,
        QueryPool queryPool,
        uint firstQuery)
    {
        if (micromaps.IsEmpty)
            throw new ArgumentException("At least one micromap is required.", nameof(micromaps));

        fixed (MicromapEXT* micromap = micromaps)
        {
            _dispatch.CmdWriteMicromapsProperties(
                commandBuffer,
                checked((uint)micromaps.Length),
                micromap,
                QueryType.MicromapCompactedSizeExt,
                queryPool,
                firstQuery);
        }
    }

    public void CmdCopyMicromap(
        CommandBuffer commandBuffer,
        in CopyMicromapInfoEXT copyInfo)
    {
        CopyMicromapInfoEXT* copyInfoPointer =
            stackalloc CopyMicromapInfoEXT[1];
        copyInfoPointer[0] = copyInfo;
        _dispatch.CmdCopyMicromap(commandBuffer, copyInfoPointer);
    }
}
