namespace Njulf.Rendering.Resources;

/// <summary>
/// Checked arithmetic for the mesh stream fragmentation budget. Keeping this
/// policy separate makes persistent-tail churn behavior deterministic and
/// directly testable without a Vulkan device.
/// </summary>
internal static class MeshRetentionBudget
{
    public static ulong CalculateDeadBytes(
        ulong retainedBytes,
        ulong liveBytes)
    {
        if (liveBytes > retainedBytes)
        {
            throw new InvalidOperationException(
                "Live mesh stream bytes exceed the retained high-water marks.");
        }

        return retainedBytes - liveBytes;
    }

    public static bool CanRegister(
        ulong retainedBytes,
        ulong liveBytes,
        ulong maximumDeadBytes) =>
        CalculateDeadBytes(retainedBytes, liveBytes) <=
        maximumDeadBytes;

    public static bool CanRetainBelowTail(
        ulong retainedPrefixBytes,
        ulong maximumDeadBytes) =>
        retainedPrefixBytes <= maximumDeadBytes;
}
