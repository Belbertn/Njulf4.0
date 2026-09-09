using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Graphics;

internal static class VulkanGraphicsCapabilities
{
    internal static bool SupportsRenderTargets(VulkanContext context)
    {
        context.Api.GetPhysicalDeviceFormatProperties(context.PhysicalDevice, Format.R16G16B16A16Sfloat, out var properties);
        const FormatFeatureFlags required = FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.StorageImageBit |
            FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit;
        return (properties.OptimalTilingFeatures & required) == required;
    }
    internal static GraphicsCapabilities Capture(VulkanContext c, RenderSettings s, RendererDiagnostics d,
        SceneRenderingData? scene, bool targetSupport)
    {
        bool frame = scene != null;
        GraphicsFeatureStatus Status(bool hardware, bool enabled, bool requested, bool active, string reason) =>
            new(hardware, enabled, requested, frame && active, !frame ? "No production frame has executed yet." :
                !string.IsNullOrWhiteSpace(reason) ? reason : active ? "Active in the latest production frame." :
                !requested ? "Not requested." : !hardware ? "Hardware support is unavailable." :
                !enabled ? "Not enabled on this device." : "Requested; no active work in the latest production frame.");
        var omm = d.GiRoadmapExperiments.OpacityMicromapRuntime;
        return new(frame, true, true, true, targetSupport,
            Status(true, true, true, d.MeshShaderMaximumVertices > 0, d.MeshShaderFallbackReason),
            Status(c.RayQuerySupported, c.RayQuerySupported, (s.GlobalIllumination.Enabled && s.GlobalIllumination.UseRayQueryBackend) ||
                s.Reflections.Mode == ReflectionMode.HybridRayQuery || s.Shadows.RequestedDirectionalShadowMode != DirectionalShadowMode.Cascaded || s.Shadows.AreaShadowsEnabled,
                d.GlobalIlluminationRayQueryActive != 0 || d.HybridReflectionPassEnabled != 0 || d.AreaRayShadowPassEnabled != 0,
                d.GlobalIlluminationFallbackReason),
            Status(c.HasIndependentComputeQueue, c.HasIndependentComputeQueue, s.AsyncCompute.Mode != AsyncComputeMode.Disabled,
                d.AsyncComputeEnabled != 0, d.AsyncComputeLastFallbackReason),
            Status(c.FragmentShadingRateSupported, c.FragmentShadingRateSupported, s.Raster.VariableRateShadingMode != VariableRateShadingMode.Off,
                scene?.VariableRateShadingActive != 0 && scene != null, scene?.VariableRateShadingFallbackReason ?? ""),
            Status(true, true, s.AmbientOcclusion.Enabled, d.AmbientOcclusionMode != AmbientOcclusionMode.Disabled, d.AmbientOcclusionMode.ToString()),
            Status(true, true, s.Shadows.DirectionalShadowsEnabled || s.Shadows.SpotShadowsEnabled || s.Shadows.PointShadowsEnabled || s.Shadows.AreaShadowsEnabled,
                d.DirectionalShadowsEnabled != 0 || d.SpotShadowSelectedCount > 0 || d.PointShadowSelectedCount > 0 || d.AreaRayShadowPassEnabled != 0,
                "Actual shadow activity depends on authored lights and shadow admission."),
            Status(true, true, s.Reflections.Mode != ReflectionMode.Disabled, d.EffectiveReflectionMode != ReflectionMode.Disabled, d.ReflectionFallbackReason.ToString()),
            Status(true, true, s.GlobalIllumination.Enabled, d.GlobalIlluminationEnabled != 0, d.GlobalIlluminationFallbackReason),
            Status(c.GiHardwareResearchCapabilities.OpacityMicromap.FeatureAvailable, c.OpacityMicromapExtCommandApi != null,
                s.GlobalIllumination.DdgiOpacityMicromapMode != DdgiOpacityMicromapMode.Off,
                omm.Enabled && omm.PublishedVariantCount > 0, omm.Detail));
    }
}
