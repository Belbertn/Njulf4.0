using System;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>Frozen forward MRT contract for deferred reflection receivers.</summary>
public static class ForwardHybridReflectionReceiverContract
{
    public const uint ShaderSemanticVersion = 2u;
    public const Format ReceiverPayloadFormat = Format.R32G32B32A32Uint;
    public const Format LobeExtensionFormat = Format.R32G32Uint;
    public const uint ColorAttachmentCount = 2u;

    public static string ResolveFragmentShader(
        bool simple,
        bool simpleFullInput,
        bool giCaustic,
        bool nearField,
        bool receiverCacheRequired = false,
        bool sparseLobePayload = false)
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
        string receiver = receiverCacheRequired
            ? "cache_required_"
            : string.Empty;
        string sparse = sparseLobePayload ? "_sparse_lobe" : string.Empty;
        return $"forward_opaque_{material}ddgi_{producers}{receiver}hybrid_reflection{sparse}.frag.spv";
    }

    public static uint ResolveColorAttachmentCount(
        bool giCaustic,
        bool nearField,
        bool sparseLobePayload = false) =>
        ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
            hasColorAttachment: true,
            nearFieldDirectSourceEnabled: nearField,
            giCausticReceiverEnabled: giCaustic,
            hybridReflectionReceiverEnabled: true,
            sparseHybridLobePayloadEnabled: sparseLobePayload);

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
        RenderTarget lobeExtension = binding.LobeExtension;
        ImageUsageFlags required = ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.SampledBit;
        if (ReferenceEquals(target, sceneColor) ||
            target.Image.Handle == sceneColor.Image.Handle ||
            ReferenceEquals(lobeExtension, sceneColor) ||
            lobeExtension.Image.Handle == sceneColor.Image.Handle ||
            ReferenceEquals(target, lobeExtension) ||
            target.Image.Handle == lobeExtension.Image.Handle)
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
        if (lobeExtension.Format != LobeExtensionFormat ||
            (lobeExtension.Usage & required) != required ||
            lobeExtension.Image.Handle == 0 ||
            lobeExtension.View.Handle == 0 ||
            lobeExtension.Extent.Width != expectedExtent.Width ||
            lobeExtension.Extent.Height != expectedExtent.Height)
        {
            failure = "hybrid-reflection-lobe-extension-attachment-mismatch";
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
        RenderTarget lobeExtension,
        in ForwardHybridReflectionReceiverPipelineConfiguration configuration)
    {
        ReceiverPayload = receiverPayload ??
            throw new ArgumentNullException(nameof(receiverPayload));
        LobeExtension = lobeExtension ??
            throw new ArgumentNullException(nameof(lobeExtension));
        Configuration = configuration;
    }

    public RenderTarget ReceiverPayload { get; }
    public RenderTarget LobeExtension { get; }
    public ForwardHybridReflectionReceiverPipelineConfiguration Configuration { get; }
}
