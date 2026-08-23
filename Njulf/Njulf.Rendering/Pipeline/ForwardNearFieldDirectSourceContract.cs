using System;
using System.Collections.Generic;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Frozen graphics-pipeline contract for C5's pre-DDGI trace source.  This is
/// intentionally separate from the render-graph declaration: a graph resource
/// name alone must never make a forward MRT variant selectable.
/// </summary>
public static class ForwardNearFieldDirectSourceContract
{
    private static readonly Format[] ReceiverAttachmentFormats =
    [
        RequiredAttachmentFormat,
        ReceiverPayloadFormat
    ];
    /// <summary>
    /// Reference-mode source format.  The packed R11G11B10 experiment is not a
    /// valid graphics attachment until its range and conformance evidence have
    /// been separately qualified.
    /// </summary>
    public const Format RequiredAttachmentFormat = Format.R16G16B16A16Sfloat;

    /// <summary>
    /// Compile-time semantic stamp required by every dedicated fragment
    /// variant.  Bump this whenever the source ownership expression changes.
    /// </summary>
    public const uint ShaderSemanticVersion = 4u;

    // SceneColor plus radiance and a compact 128-bit receiver payload. The
    // payload packs two octahedral normals, a 16-bit frame-local surface-table
    // token, RGB9E5 Lambertian throughput, and RGB565 dielectric F0. Ray
    // projection is reconstructed from depth and frame matrices by the trace
    // pass instead of consuming two more full-resolution MRTs.
    public const uint ColorAttachmentCount = 3u;
    public const Format ReceiverPayloadFormat = Format.R32G32B32A32Uint;

    // The first graph-supported profile is deliberately frozen.  A different
    // trace distance or B3 footprint is a new semantic shader/profile ABI, not
    // a silent runtime reinterpretation of these MRT values.
    public const float ReferenceFullWeightTraceDistance = 4.0f;
    public const float ReferenceMaximumTraceDistance = 8.0f;
    public const float ReferenceB3WorldFootprintRadius = 0.25f;

    public const string OpaqueFragmentShader =
        "forward_opaque_ddgi_near_field_direct_source.frag.spv";
    public const string SimpleOpaqueFragmentShader =
        "forward_opaque_simple_ddgi_near_field_direct_source.frag.spv";
    public const string SimpleFullInputOpaqueFragmentShader =
        "forward_opaque_simple_full_input_ddgi_near_field_direct_source.frag.spv";

    // C5 must not evict the production DDGI receiver cache merely because it
    // adds two MRT outputs. Keep exact-gather fallbacks above, and select these
    // cache-required siblings only after the current-frame cache is published.
    public const string ReceiverCacheOpaqueFragmentShader =
        "forward_opaque_ddgi_near_field_direct_source_cache_required.frag.spv";
    public const string ReceiverCacheSimpleOpaqueFragmentShader =
        "forward_opaque_simple_ddgi_near_field_direct_source_cache_required.frag.spv";
    public const string ReceiverCacheSimpleFullInputOpaqueFragmentShader =
        "forward_opaque_simple_full_input_ddgi_near_field_direct_source_cache_required.frag.spv";

    public static bool TryValidatePipelineConfiguration(
        in ForwardNearFieldDirectSourcePipelineConfiguration configuration,
        out string failure)
    {
        if (!configuration.IsC5EffectivelyEnabled)
        {
            failure = "near-field-direct-source-disabled";
            return false;
        }

        if (configuration.ShaderSemanticVersion != ShaderSemanticVersion)
        {
            failure = "near-field-direct-source-shader-semantics-version-mismatch";
            return false;
        }

        if (!configuration.TraceSourceContract.TryValidate(out string sourceFailure))
        {
            failure = "near-field-direct-source-" + sourceFailure;
            return false;
        }

        if (configuration.TraceSourceContract.Format !=
            SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat)
        {
            failure = "near-field-direct-source-r16g16b16a16-sfloat-required";
            return false;
        }

        failure = "valid";
        return true;
    }

    /// <summary>
    /// Verifies the physical attachment immediately before dynamic rendering.
    /// A stale resize, a graph alias to SceneColor, or a pipeline configuration
    /// that differs from the attachment's immutable source contract fails
    /// closed rather than selecting the MRT pipeline.
    /// </summary>
    public static bool TryValidateAttachmentBinding(
        ForwardNearFieldDirectSourceAttachmentBinding? binding,
        in ForwardNearFieldDirectSourcePipelineConfiguration pipelineConfiguration,
        RenderTarget sceneColor,
        Extent2D expectedExtent,
        out string failure)
    {
        if (binding == null)
        {
            failure = "near-field-direct-source-attachment-binding-unavailable";
            return false;
        }

        if (sceneColor == null)
            throw new ArgumentNullException(nameof(sceneColor));

        if (!binding.Configuration.Equals(pipelineConfiguration))
        {
            failure = "near-field-direct-source-pipeline-attachment-configuration-mismatch";
            return false;
        }

        if (!TryValidatePipelineConfiguration(pipelineConfiguration, out failure))
            return false;

        IReadOnlyList<RenderTarget> targets = binding.Targets;
        var uniqueImages = new HashSet<ulong>();
        ImageUsageFlags requiredUsage = ImageUsageFlags.ColorAttachmentBit |
                                        ImageUsageFlags.SampledBit;
        for (int index = 0; index < targets.Count; index++)
        {
            RenderTarget target = targets[index];
            if (ReferenceEquals(target, sceneColor) ||
                target.Image.Handle == sceneColor.Image.Handle)
            {
                failure = "near-field-receiver-attachments-must-not-alias-scene-color";
                return false;
            }
            if (target.Format != ReceiverAttachmentFormats[index])
            {
                failure = "near-field-receiver-attachment-format-mismatch";
                return false;
            }
            if ((target.Usage & requiredUsage) != requiredUsage)
            {
                failure = "near-field-receiver-attachment-usage-mismatch";
                return false;
            }
            if (target.Image.Handle == 0 || target.View.Handle == 0)
            {
                failure = "near-field-receiver-attachment-handle-unavailable";
                return false;
            }
            if (!uniqueImages.Add(target.Image.Handle))
            {
                failure = "near-field-receiver-attachments-must-not-alias";
                return false;
            }
            if (target.Extent.Width != expectedExtent.Width ||
                target.Extent.Height != expectedExtent.Height)
            {
                failure = "near-field-receiver-attachment-extent-mismatch";
                return false;
            }
        }

        if (expectedExtent.Width > int.MaxValue || expectedExtent.Height > int.MaxValue)
        {
            failure = "near-field-direct-source-render-extent-out-of-range";
            return false;
        }

        SimpleDdgiNearFieldTraceSourceScaledExtent contractExtent =
            pipelineConfiguration.TraceSourceContract.Extent;
        if (contractExtent.FullWidth != checked((int)expectedExtent.Width) ||
            contractExtent.FullHeight != checked((int)expectedExtent.Height))
        {
            failure = "near-field-direct-source-contract-full-extent-mismatch";
            return false;
        }

        failure = "valid";
        return true;
    }
}

/// <summary>
/// Explicit opt-in used while creating the mesh pipeline.  Default construction
/// is disabled, so the normal renderer neither loads nor creates the C5 MRT
/// programs unless an already-admitted integration supplies this exact record.
/// </summary>
public readonly record struct ForwardNearFieldDirectSourcePipelineConfiguration(
    bool IsC5EffectivelyEnabled,
    SimpleDdgiNearFieldTraceSourceContract TraceSourceContract,
    uint ShaderSemanticVersion)
{
    public static ForwardNearFieldDirectSourcePipelineConfiguration Disabled { get; } =
        new(false, default, 0u);
}

/// <summary>
/// Immutable binding between a C5-effective pipeline configuration and the
/// independently owned source target.  The target itself owns layout state; the
/// binding only establishes provenance and prevents it being inferred from a
/// generic scene-colour image.
/// </summary>
public sealed class ForwardNearFieldDirectSourceAttachmentBinding
{
    private readonly RenderTarget[] _targets;

    public ForwardNearFieldDirectSourceAttachmentBinding(
        RenderTarget directSource,
        RenderTarget receiverPayload,
        ForwardNearFieldDirectSourcePipelineConfiguration configuration)
    {
        DirectSource = directSource ??
            throw new ArgumentNullException(nameof(directSource));
        ReceiverPayload = receiverPayload ??
            throw new ArgumentNullException(nameof(receiverPayload));
        _targets =
        [
            DirectSource,
            ReceiverPayload
        ];
        Configuration = configuration;
    }

    /// <summary>Compatibility alias for the primary radiance source.</summary>
    public RenderTarget Target => DirectSource;
    public RenderTarget DirectSource { get; }
    public RenderTarget ReceiverPayload { get; }
    public IReadOnlyList<RenderTarget> Targets => _targets;
    public ForwardNearFieldDirectSourcePipelineConfiguration Configuration { get; }
}
