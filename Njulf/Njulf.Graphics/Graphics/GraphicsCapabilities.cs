namespace Njulf.Graphics;

/// <summary>Supported and device-enabled are independent of requested and last-frame active state.</summary>
/// <param name="HardwareSupported">Whether hardware reports support.</param>
/// <param name="DeviceEnabled">Whether support was enabled when this device was created.</param>
/// <param name="Requested">Whether the current settings request this feature.</param>
/// <param name="Active">Whether the latest production frame actually used this feature.</param>
/// <param name="Reason">Explanation of availability or fallback; suitable for diagnostics.</param>
public sealed record GraphicsFeatureStatus(bool HardwareSupported, bool DeviceEnabled,
    bool Requested, bool Active, string Reason);

/// <summary>A snapshot of capabilities and the latest production-frame execution diagnostics.</summary>
public sealed record GraphicsCapabilities(
    bool HasProductionFrame,
    bool CustomGraphicsPasses,
    bool CustomComputePasses,
    bool StorageTransferBuffers,
    bool Rgba16FloatRenderTargets,
    GraphicsFeatureStatus MeshShaders,
    GraphicsFeatureStatus RayQueries,
    GraphicsFeatureStatus AsyncCompute,
    GraphicsFeatureStatus VariableRateShading,
    GraphicsFeatureStatus AmbientOcclusion,
    GraphicsFeatureStatus Shadows,
    GraphicsFeatureStatus Reflections,
    GraphicsFeatureStatus GlobalIllumination,
    GraphicsFeatureStatus OpacityMicromaps);
