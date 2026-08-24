using System;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Single attachment-count contract shared by the forward dynamic-rendering
/// pass and every mesh/foliage pipeline variant bound inside it.
/// </summary>
internal static class ForwardDynamicRenderingContract
{
    public const uint SceneColorAttachmentCount = 1;
    public const uint ProvenanceColorAttachmentCount = 2;
    public const uint NearFieldDirectSourceColorAttachmentCount =
        ForwardNearFieldDirectSourceContract.ColorAttachmentCount;
    public const uint GiCausticReceiverColorAttachmentCount =
        ForwardGiCausticReceiverContract.ColorAttachmentCount;
    public const uint CombinedAdvancedGiColorAttachmentCount =
        ForwardAdvancedGiCombinedContract.ColorAttachmentCount;
    public const uint HybridReflectionReceiverColorAttachmentCount = 2;

    public static uint ResolveColorAttachmentCount(
        bool hasColorAttachment,
        bool materialTransportProvenanceEnabled = false,
        bool nearFieldDirectSourceEnabled = false,
        bool giCausticReceiverEnabled = false,
        bool hybridReflectionReceiverEnabled = false)
    {
        if (!hasColorAttachment)
            return 0;

        // Provenance owns location one and has no combined semantic ABI. C4
        // and C5 do have an explicitly compiled four-attachment ABI and may be
        // enabled together without re-rendering opaque geometry.
        if (materialTransportProvenanceEnabled &&
            (nearFieldDirectSourceEnabled || giCausticReceiverEnabled ||
             hybridReflectionReceiverEnabled))
        {
            throw new InvalidOperationException(
                "Material provenance cannot share a forward MRT variant with C4, C5, or deferred reflection outputs.");
        }

        if (hybridReflectionReceiverEnabled)
        {
            uint producerCount = nearFieldDirectSourceEnabled &&
                                 giCausticReceiverEnabled
                ? CombinedAdvancedGiColorAttachmentCount
                : nearFieldDirectSourceEnabled
                    ? NearFieldDirectSourceColorAttachmentCount
                    : giCausticReceiverEnabled
                        ? GiCausticReceiverColorAttachmentCount
                        : SceneColorAttachmentCount;
            return producerCount + 1u;
        }

        if (nearFieldDirectSourceEnabled && giCausticReceiverEnabled)
            return CombinedAdvancedGiColorAttachmentCount;
        if (nearFieldDirectSourceEnabled)
            return NearFieldDirectSourceColorAttachmentCount;
        if (giCausticReceiverEnabled)
            return GiCausticReceiverColorAttachmentCount;

        return materialTransportProvenanceEnabled
            ? ProvenanceColorAttachmentCount
            : SceneColorAttachmentCount;
    }
}
