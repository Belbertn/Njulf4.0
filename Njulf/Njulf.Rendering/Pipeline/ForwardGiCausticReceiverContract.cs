using System;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Frozen forward MRT contract for the C4 visible-receiver payload.  It stores
/// only current-frame surface facts required to evaluate the directional,
/// energy-conserving diffuse BRDF during photon gather; no C4 energy is ever
/// written by the forward producer.
/// </summary>
public static class ForwardGiCausticReceiverContract
{
    public const uint ShaderSemanticVersion = 1u;
    public const uint ColorAttachmentCount = 2u;
    public const Format ReceiverPayloadFormat =
        GiCausticScreenGpuAbi.ReceiverPayloadFormat;

    public const string OpaqueFragmentShader =
        "forward_opaque_ddgi_c4_receiver.frag.spv";
    public const string SimpleOpaqueFragmentShader =
        "forward_opaque_simple_ddgi_c4_receiver.frag.spv";
    public const string SimpleFullInputOpaqueFragmentShader =
        "forward_opaque_simple_full_input_ddgi_c4_receiver.frag.spv";
    public const string ReceiverCacheOpaqueFragmentShader =
        "forward_opaque_ddgi_c4_receiver_cache_required.frag.spv";
    public const string ReceiverCacheSimpleOpaqueFragmentShader =
        "forward_opaque_simple_ddgi_c4_receiver_cache_required.frag.spv";
    public const string ReceiverCacheSimpleFullInputOpaqueFragmentShader =
        "forward_opaque_simple_full_input_ddgi_c4_receiver_cache_required.frag.spv";

    public static bool TryValidatePipelineConfiguration(
        in ForwardGiCausticReceiverPipelineConfiguration configuration,
        out string failure)
    {
        if (!configuration.IsC4EffectivelyEnabled)
        {
            failure = "caustic-forward-receiver-disabled";
            return false;
        }
        if (configuration.ShaderSemanticVersion != ShaderSemanticVersion)
        {
            failure = "caustic-forward-receiver-shader-semantics-version-mismatch";
            return false;
        }
        if (configuration.TransportAbiVersion != GiCausticGpuAbi.Version ||
            configuration.ScreenResolveAbiVersion != GiCausticScreenGpuAbi.Version)
        {
            failure = "caustic-forward-receiver-ABI-version-mismatch";
            return false;
        }
        if (configuration.EvidenceBindingFingerprint == 0UL)
        {
            failure = "caustic-forward-receiver-evidence-binding-missing";
            return false;
        }
        if (!configuration.Profile.TryValidate(out failure))
            return false;

        failure = "valid";
        return true;
    }

    public static bool TryValidateAttachmentBinding(
        ForwardGiCausticReceiverAttachmentBinding? binding,
        in ForwardGiCausticReceiverPipelineConfiguration pipelineConfiguration,
        RenderTarget sceneColor,
        Extent2D expectedExtent,
        out string failure)
    {
        if (binding is null)
        {
            failure = "caustic-forward-receiver-attachment-unavailable";
            return false;
        }
        ArgumentNullException.ThrowIfNull(sceneColor);
        if (binding.Configuration != pipelineConfiguration)
        {
            failure = "caustic-forward-receiver-pipeline-attachment-configuration-mismatch";
            return false;
        }
        if (!TryValidatePipelineConfiguration(pipelineConfiguration, out failure))
            return false;

        RenderTarget target = binding.ReceiverPayload;
        ImageUsageFlags required = ImageUsageFlags.ColorAttachmentBit |
                                   ImageUsageFlags.SampledBit;
        if (ReferenceEquals(target, sceneColor) ||
            target.Image.Handle == sceneColor.Image.Handle)
        {
            failure = "caustic-forward-receiver-must-not-alias-scene-color";
            return false;
        }
        if (target.Format != ReceiverPayloadFormat)
        {
            failure = "caustic-forward-receiver-format-mismatch";
            return false;
        }
        if ((target.Usage & required) != required)
        {
            failure = "caustic-forward-receiver-usage-mismatch";
            return false;
        }
        if (target.Image.Handle == 0UL || target.View.Handle == 0UL)
        {
            failure = "caustic-forward-receiver-handle-unavailable";
            return false;
        }
        if (target.Extent.Width != expectedExtent.Width ||
            target.Extent.Height != expectedExtent.Height ||
            pipelineConfiguration.Profile.Width != checked((int)expectedExtent.Width) ||
            pipelineConfiguration.Profile.Height != checked((int)expectedExtent.Height))
        {
            failure = "caustic-forward-receiver-extent-mismatch";
            return false;
        }

        failure = "valid";
        return true;
    }
}

public readonly record struct ForwardGiCausticReceiverPipelineConfiguration(
    bool IsC4EffectivelyEnabled,
    GiCausticScreenResolveProfile Profile,
    ulong EvidenceBindingFingerprint,
    uint TransportAbiVersion,
    uint ScreenResolveAbiVersion,
    uint ShaderSemanticVersion)
{
    public static ForwardGiCausticReceiverPipelineConfiguration Disabled { get; } =
        new(false, default, 0UL, 0u, 0u, 0u);
}

public sealed class ForwardGiCausticReceiverAttachmentBinding
{
    public ForwardGiCausticReceiverAttachmentBinding(
        RenderTarget receiverPayload,
        ForwardGiCausticReceiverPipelineConfiguration configuration)
    {
        ReceiverPayload = receiverPayload ??
            throw new ArgumentNullException(nameof(receiverPayload));
        Configuration = configuration;
    }

    public RenderTarget ReceiverPayload { get; }
    public ForwardGiCausticReceiverPipelineConfiguration Configuration { get; }
}
