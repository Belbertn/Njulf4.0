using System;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Every physical object owned by one production C5 generation. The image
/// bank remains graph-owned while the runtime owns buffers, descriptor sets,
/// pipelines, history state, and command-recording bindings.
/// </summary>
internal sealed record SimpleDdgiNearFieldResidualVulkanGenerationResources(
    SimpleDdgiNearFieldResidualRenderTargetGeneration Targets,
    SimpleDdgiNearFieldResidualVulkanRuntime Runtime);

/// <summary>
/// Vulkan allocation boundary used by the generic fence-driven generation
/// transaction. The constructor-created image bank is adopted for generation
/// one; all later generations are allocated independently and remain
/// unpublished until the transaction commits them.
/// </summary>
internal sealed class SimpleDdgiNearFieldResidualVulkanGenerationBackend :
    ISimpleDdgiNearFieldResidualGenerationBackend<
        SimpleDdgiNearFieldResidualVulkanGenerationResources>
{
    private readonly RenderTargetManager _renderTargets;
    private readonly Func<
        SimpleDdgiNearFieldResidualLayout,
        SimpleDdgiNearFieldResidualRenderTargetGeneration,
        SimpleDdgiNearFieldResidualVulkanRuntime> _runtimeFactory;
    private bool _initialGenerationClaimed;

    public SimpleDdgiNearFieldResidualVulkanGenerationBackend(
        RenderTargetManager renderTargets,
        Func<
            SimpleDdgiNearFieldResidualLayout,
            SimpleDdgiNearFieldResidualRenderTargetGeneration,
            SimpleDdgiNearFieldResidualVulkanRuntime> runtimeFactory)
    {
        _renderTargets = renderTargets ??
            throw new ArgumentNullException(nameof(renderTargets));
        _runtimeFactory = runtimeFactory ??
            throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public SimpleDdgiNearFieldResidualGenerationAllocation<
        SimpleDdgiNearFieldResidualVulkanGenerationResources> Allocate(
        ulong generation,
        in SimpleDdgiNearFieldResidualLayout layout)
    {
        if (generation == 0UL)
            throw new ArgumentOutOfRangeException(nameof(generation));

        SimpleDdgiNearFieldResidualRenderTargetGeneration targets;
        SimpleDdgiNearFieldResidualRenderTargetGeneration? initial =
            _renderTargets.CurrentNearFieldResidualGeneration;
        if (!_initialGenerationClaimed && initial is not null &&
            initial.Layout.Equals(layout))
        {
            _initialGenerationClaimed = true;
            targets = initial;
        }
        else
        {
            _initialGenerationClaimed = true;
            targets = _renderTargets.AllocateNearFieldResidualGeneration(
                generation,
                layout);
        }

        SimpleDdgiNearFieldResidualVulkanRuntime? runtime = null;
        try
        {
            runtime = _runtimeFactory(layout, targets);
            ulong chargedBytes = Math.Max(
                layout.TotalBytes,
                runtime.ActualAllocationBytes);
            return new SimpleDdgiNearFieldResidualGenerationAllocation<
                SimpleDdgiNearFieldResidualVulkanGenerationResources>(
                generation,
                layout,
                chargedBytes,
                new SimpleDdgiNearFieldResidualVulkanGenerationResources(
                    targets,
                    runtime));
        }
        catch
        {
            runtime?.Dispose();
            _renderTargets.ReleaseNearFieldResidualGeneration(targets);
            throw;
        }
    }

    public void Destroy(
        SimpleDdgiNearFieldResidualGenerationAllocation<
            SimpleDdgiNearFieldResidualVulkanGenerationResources> allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        try
        {
            allocation.Resources.Runtime.Dispose();
        }
        finally
        {
            _renderTargets.ReleaseNearFieldResidualGeneration(
                allocation.Resources.Targets);
        }
    }
}
