namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Single attachment-count contract shared by the forward dynamic-rendering
/// pass and every mesh/foliage pipeline variant bound inside it.
/// </summary>
internal static class ForwardDynamicRenderingContract
{
    public const uint SceneColorAttachmentCount = 1;
    public const uint ProvenanceColorAttachmentCount = 2;

    public static uint ResolveColorAttachmentCount(
        bool hasColorAttachment,
        bool materialTransportProvenanceEnabled = false)
    {
        if (!hasColorAttachment)
            return 0;

        return materialTransportProvenanceEnabled
            ? ProvenanceColorAttachmentCount
            : SceneColorAttachmentCount;
    }
}
