using System;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>Frozen forward MRT contract for deferred reflection receivers.</summary>
public static class ForwardHybridReflectionReceiverContract
{
    public const uint ShaderSemanticVersion = 1u;
    public const Format ReceiverPayloadFormat = Format.R32G32B32A32Uint;

    public static string ResolveFragmentShader(
        bool simple,
        bool simpleFullInput,
        bool giCaustic,
        bool nearField)
    {
        string material = simple
            ? (simpleFullInput ? "simple_full_input_" : "simple_")
            : string.Empty;
        string producers = (giCaustic, nearField) switch
        {
            (true, true) => "c4_c5_",
            (true, false) => "c4_",
            (false, true) => "c5_",
            _ => string.Empty
        };
        return $"forward_opaque_{material}ddgi_{producers}hybrid_reflection.frag.spv";
    }

    public static uint ResolveColorAttachmentCount(
        bool giCaustic,
        bool nearField) =>
        ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
            hasColorAttachment: true,
            nearFieldDirectSourceEnabled: nearField,
            giCausticReceiverEnabled: giCaustic,
            hybridReflectionReceiverEnabled: true);

    public static bool TryValidateAttachmentBinding(
        ForwardHybridReflectionReceiverAttachmentBinding? binding,
        RenderTarget sceneColor,
        Extent2D expectedExtent,
        out string failure)
    {
        if (binding is null)
        {
            failure = "hybrid-reflection-receiver-binding-unavailable";
            return false;
        }
        if (!binding.Configuration.Enabled ||
            binding.Configuration.ShaderSemanticVersion != ShaderSemanticVersion)
        {
            failure = "hybrid-reflection-receiver-configuration-invalid";
            return false;
        }

        RenderTarget target = binding.ReceiverPayload;
        ImageUsageFlags required = ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.SampledBit;
        if (ReferenceEquals(target, sceneColor) ||
            target.Image.Handle == sceneColor.Image.Handle)
        {
            failure = "hybrid-reflection-receiver-must-not-alias-scene-color";
            return false;
        }
        if (target.Format != ReceiverPayloadFormat ||
            (target.Usage & required) != required ||
            target.Image.Handle == 0 || target.View.Handle == 0 ||
            target.Extent.Width != expectedExtent.Width ||
            target.Extent.Height != expectedExtent.Height)
        {
            failure = "hybrid-reflection-receiver-attachment-mismatch";
            return false;
        }

        failure = "valid";
        return true;
    }
}

public readonly record struct ForwardHybridReflectionReceiverPipelineConfiguration(
    bool Enabled,
    uint ShaderSemanticVersion)
{
    public static ForwardHybridReflectionReceiverPipelineConfiguration Disabled =>
        default;

    public static ForwardHybridReflectionReceiverPipelineConfiguration Production =>
        new(true, ForwardHybridReflectionReceiverContract.ShaderSemanticVersion);
}

public sealed class ForwardHybridReflectionReceiverAttachmentBinding
{
    public ForwardHybridReflectionReceiverAttachmentBinding(
        RenderTarget receiverPayload,
        in ForwardHybridReflectionReceiverPipelineConfiguration configuration)
    {
        ReceiverPayload = receiverPayload ??
            throw new ArgumentNullException(nameof(receiverPayload));
        Configuration = configuration;
    }

    public RenderTarget ReceiverPayload { get; }
    public ForwardHybridReflectionReceiverPipelineConfiguration Configuration { get; }
}
