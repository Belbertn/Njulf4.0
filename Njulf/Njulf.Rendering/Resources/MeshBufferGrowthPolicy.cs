using System.Text;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

internal enum MeshBufferStream
{
    VertexPosition,
    VertexNormalTangent,
    VertexUvColor,
    Index,
    MeshMetadata,
    Meshlet,
    MeshletVertexIndex,
    MeshletTriangleIndex,
    SkinningData
}

internal enum MeshBufferGrowthMode
{
    Geometric,
    Tight
}

internal readonly record struct MeshBufferGrowthInput(
    MeshBufferStream Stream,
    ulong CurrentSize,
    ulong RequiredSize);

internal readonly record struct MeshBufferGrowthPlanEntry(
    MeshBufferStream Stream,
    ulong CurrentSize,
    ulong RequiredSize,
    ulong TargetSize)
{
    public bool RequiresReplacement => TargetSize != CurrentSize;
}

internal sealed record MeshBufferGrowthPlan(
    MeshBufferGrowthMode Mode,
    IReadOnlyList<MeshBufferGrowthPlanEntry> Entries,
    ulong TotalCurrentBytes,
    ulong TotalRequiredBytes,
    ulong TotalTargetBytes,
    ulong ReplacementTargetBytes)
{
    internal string Describe()
    {
        var replacements = new StringBuilder();
        foreach (MeshBufferGrowthPlanEntry entry in Entries)
        {
            if (!entry.RequiresReplacement)
                continue;
            if (replacements.Length > 0)
                replacements.Append(", ");
            replacements.Append(entry.Stream);
            replacements.Append(": current=");
            replacements.Append(entry.CurrentSize);
            replacements.Append(", required=");
            replacements.Append(entry.RequiredSize);
            replacements.Append(", target=");
            replacements.Append(entry.TargetSize);
        }

        if (replacements.Length == 0)
            replacements.Append("none");

        return FormattableString.Invariant(
            $"mode={Mode}; currentCapacity={TotalCurrentBytes} bytes; required={TotalRequiredBytes} bytes; plannedCapacity={TotalTargetBytes} bytes; replacementBytes={ReplacementTargetBytes} bytes; replacements=[{replacements}]");
    }
}

internal static class MeshBufferGrowthPlanner
{
    internal static MeshBufferGrowthPlan Create(
        IReadOnlyList<MeshBufferGrowthInput> inputs,
        MeshBufferGrowthMode mode,
        ulong growthFactor = 2)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (growthFactor <= 1)
            throw new ArgumentOutOfRangeException(
                nameof(growthFactor),
                "Mesh buffer growth factor must be greater than one.");

        var entries = new MeshBufferGrowthPlanEntry[inputs.Count];
        var streams = new HashSet<MeshBufferStream>();
        ulong totalCurrentBytes = 0;
        ulong totalRequiredBytes = 0;
        ulong totalTargetBytes = 0;
        ulong replacementTargetBytes = 0;
        for (int index = 0; index < inputs.Count; index++)
        {
            MeshBufferGrowthInput input = inputs[index];
            if (!Enum.IsDefined(input.Stream))
                throw new ArgumentOutOfRangeException(nameof(inputs));
            if (!streams.Add(input.Stream))
            {
                throw new ArgumentException(
                    $"Mesh buffer stream '{input.Stream}' was planned more than once.",
                    nameof(inputs));
            }

            ulong targetSize = CalculateTargetSize(
                input.CurrentSize,
                input.RequiredSize,
                mode,
                growthFactor);
            var entry = new MeshBufferGrowthPlanEntry(
                input.Stream,
                input.CurrentSize,
                input.RequiredSize,
                targetSize);
            entries[index] = entry;
            totalCurrentBytes = checked(
                totalCurrentBytes + input.CurrentSize);
            totalRequiredBytes = checked(
                totalRequiredBytes + input.RequiredSize);
            totalTargetBytes = checked(totalTargetBytes + targetSize);
            if (entry.RequiresReplacement)
            {
                replacementTargetBytes = checked(
                    replacementTargetBytes + targetSize);
            }
        }

        return new MeshBufferGrowthPlan(
            mode,
            Array.AsReadOnly(entries),
            totalCurrentBytes,
            totalRequiredBytes,
            totalTargetBytes,
            replacementTargetBytes);
    }

    internal static ulong CalculateTargetSize(
        ulong currentSize,
        ulong requiredSize,
        MeshBufferGrowthMode mode,
        ulong growthFactor = 2)
    {
        if (requiredSize <= currentSize)
            return currentSize;
        if (mode == MeshBufferGrowthMode.Tight)
            return requiredSize;
        if (currentSize == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentSize),
                "Geometric mesh buffer growth requires a non-zero current size.");
        }

        ulong targetSize = currentSize;
        do
        {
            targetSize = checked(targetSize * growthFactor);
        }
        while (targetSize < requiredSize);

        return targetSize;
    }
}

internal static class MeshBufferGrowthRetry
{
    internal static void Execute(
        Action<MeshBufferGrowthMode> executeAttempt,
        Func<Exception, bool> isRetryable,
        Action resetForRetry,
        Action<Exception> onRetrying,
        Action onRetrySucceeded)
    {
        ArgumentNullException.ThrowIfNull(executeAttempt);
        ArgumentNullException.ThrowIfNull(isRetryable);
        ArgumentNullException.ThrowIfNull(resetForRetry);
        ArgumentNullException.ThrowIfNull(onRetrying);
        ArgumentNullException.ThrowIfNull(onRetrySucceeded);

        Exception firstFailure;
        try
        {
            executeAttempt(MeshBufferGrowthMode.Geometric);
            return;
        }
        catch (Exception failure)
        {
            if (!isRetryable(failure))
                throw;
            firstFailure = failure;
        }

        try
        {
            resetForRetry();
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(
                "Geometric mesh-buffer growth exhausted device memory and cleanup prevented the tight retry.",
                firstFailure,
                cleanupFailure);
        }

        onRetrying(firstFailure);
        try
        {
            executeAttempt(MeshBufferGrowthMode.Tight);
            onRetrySucceeded();
        }
        catch (Exception tightFailure)
        {
            throw new AggregateException(
                "Geometric and tight mesh-buffer growth attempts both failed.",
                firstFailure,
                tightFailure);
        }
    }
}

internal sealed class MeshBufferGrowthAttemptException : Exception
{
    internal MeshBufferGrowthAttemptException(
        MeshBufferGrowthPlan plan,
        Exception innerException)
        : base(
            $"Mesh-buffer allocation attempt failed ({plan.Describe()}): {innerException.Message}",
            innerException)
    {
        Plan = plan;
    }

    internal MeshBufferGrowthPlan Plan { get; }
}

internal static class MeshBufferCompactionFailurePolicy
{
    internal static bool ShouldSkip(Exception failure) =>
        failure is BufferAllocationException
        {
            Result: Result.ErrorOutOfDeviceMemory
        };
}
