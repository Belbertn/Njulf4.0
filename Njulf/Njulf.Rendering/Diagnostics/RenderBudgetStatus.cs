namespace Njulf.Rendering.Diagnostics
{
    public enum RenderBudgetStatus
    {
        Unknown,
        WithinBudget,
        Warning,
        OverBudget,
        Unavailable
    }

    public sealed record BudgetMetric(
        string Name,
        double Value,
        double WarningThreshold,
        double FailureThreshold,
        string Unit,
        RenderBudgetStatus Status);
}
