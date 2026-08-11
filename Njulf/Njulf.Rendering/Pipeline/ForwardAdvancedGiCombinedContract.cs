namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Frozen four-attachment ABI used only when the independently qualified C4
/// and C5 producers are both effective. The program writes both contracts in a
/// single opaque submission and never merges or reinterprets their payloads.
/// </summary>
public static class ForwardAdvancedGiCombinedContract
{
    public const uint ColorAttachmentCount = 4u;

    public const string OpaqueFragmentShader =
        "forward_opaque_ddgi_c4_c5.frag.spv";
    public const string SimpleOpaqueFragmentShader =
        "forward_opaque_simple_ddgi_c4_c5.frag.spv";
    public const string SimpleFullInputOpaqueFragmentShader =
        "forward_opaque_simple_full_input_ddgi_c4_c5.frag.spv";

    public static bool TryValidatePipelineConfigurations(
        in ForwardGiCausticReceiverPipelineConfiguration caustic,
        in ForwardNearFieldDirectSourcePipelineConfiguration nearField,
        out string failure)
    {
        if (!ForwardGiCausticReceiverContract.TryValidatePipelineConfiguration(
                caustic,
                out failure))
        {
            failure = "combined-advanced-GI-" + failure;
            return false;
        }

        if (!ForwardNearFieldDirectSourceContract.TryValidatePipelineConfiguration(
                nearField,
                out failure))
        {
            failure = "combined-advanced-GI-" + failure;
            return false;
        }

        failure = "valid";
        return true;
    }
}
