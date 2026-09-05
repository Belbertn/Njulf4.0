using System.Threading;
using Njulf.Rendering.Data;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects;

/// <summary>
/// Publishes complete families from pipeline preparation to command recording.
/// Resolution does not cache a temporary Full fallback. MeshPipeline owns the
/// native handles and retires the bank before destroying them at device idle.
/// </summary>
internal sealed class AutomaticPlanarCapturePipelineBank
{
    private sealed record Family(VkPipeline Color, VkPipeline Feedback);
    private readonly Family?[] _families = new Family?[3];

    internal bool IsPrepared(ForwardOpaquePipelineFamily family) =>
        Volatile.Read(ref _families[Index(family)]) is not null;

    internal void Publish(ForwardOpaquePipelineFamily family,
        VkPipeline color, VkPipeline feedback, bool feedbackRequired)
    {
        if (color.Handle == 0 || feedbackRequired && feedback.Handle == 0)
            throw new InvalidOperationException("A capture family must be complete before publication.");
        Volatile.Write(ref _families[Index(family)], new Family(color, feedback));
    }

    internal bool TryResolve(in ForwardOpaquePipelineKey key, out VkPipeline pipeline)
    {
        Family? requested = Volatile.Read(ref _families[Index(key.Family)]);
        bool feedback = key.Has(ForwardOpaquePipelineFeatures.AlphaMaskReceiverFeedback);
        pipeline = Select(requested, feedback);
        if (pipeline.Handle == 0)
            pipeline = Select(Volatile.Read(ref _families[0]), feedback);
        return pipeline.Handle != 0;
    }

    internal void Clear()
    {
        for (int i = 0; i < _families.Length; i++)
            Volatile.Write(ref _families[i], null);
    }

    private static VkPipeline Select(Family? family, bool feedback) =>
        family is null ? default : feedback ? family.Feedback : family.Color;

    internal static int Index(ForwardOpaquePipelineFamily family) => family switch
    {
        ForwardOpaquePipelineFamily.Full or ForwardOpaquePipelineFamily.CompactedFull => 0,
        ForwardOpaquePipelineFamily.Simple or ForwardOpaquePipelineFamily.CompactedSimple => 1,
        ForwardOpaquePipelineFamily.SimpleFullInput or ForwardOpaquePipelineFamily.CompactedSimpleFullInput => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    internal static ForwardOpaquePipelineFamily ResolveFamily(
        MaterialForwardClass bucket, bool simpleEligible, bool taskless) => (bucket, simpleEligible, taskless) switch
    {
        (MaterialForwardClass.SimpleOpaque, true, true) => ForwardOpaquePipelineFamily.CompactedSimple,
        (MaterialForwardClass.SimpleOpaque, true, false) => ForwardOpaquePipelineFamily.Simple,
        (MaterialForwardClass.SimpleOpaqueNormal, true, true) => ForwardOpaquePipelineFamily.CompactedSimpleFullInput,
        (MaterialForwardClass.SimpleOpaqueNormal, true, false) => ForwardOpaquePipelineFamily.SimpleFullInput,
        (MaterialForwardClass.SimpleOpaque or MaterialForwardClass.SimpleOpaqueNormal or MaterialForwardClass.FullOpaque, _, true)
            => ForwardOpaquePipelineFamily.CompactedFull,
        (MaterialForwardClass.SimpleOpaque or MaterialForwardClass.SimpleOpaqueNormal or MaterialForwardClass.FullOpaque, _, false)
            => ForwardOpaquePipelineFamily.Full,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };
}
