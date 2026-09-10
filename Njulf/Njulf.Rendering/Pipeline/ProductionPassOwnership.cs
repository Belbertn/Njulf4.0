using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline;

/// <summary>Retains constructed passes until the graph accepts responsibility for their cleanup.</summary>
internal sealed class ProductionPassOwnership
{
    private readonly List<RenderPassBase> _pending = [];
    private StagedDisposalPlan? _cleanup;

    internal T Track<T>(T pass) where T : RenderPassBase
    {
        if (_cleanup != null) throw new ObjectDisposedException(nameof(ProductionPassOwnership));
        _pending.Add(pass);
        return pass;
    }

    internal void Register(RenderGraph graph, RenderPassBase pass)
    {
        if (_cleanup != null) throw new ObjectDisposedException(nameof(ProductionPassOwnership));
        if (!_pending.Contains(pass)) throw new InvalidOperationException("The production pass is not owned by this construction attempt.");
        graph.AddPass(pass);
        _pending.Remove(pass);
    }

    internal void CleanupUnregistered()
    {
        _cleanup ??= new StagedDisposalPlan(_pending.Select((pass, index) =>
            new StagedDisposalStep($"unregistered-pass-{index}:{pass.Name}", pass.Cleanup)).ToArray());
        if (_cleanup.TryDrain() is { } failure) throw failure;
        _pending.Clear();
    }
}
