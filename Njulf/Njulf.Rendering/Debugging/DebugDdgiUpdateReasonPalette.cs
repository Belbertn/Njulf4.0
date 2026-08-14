using Njulf.Core.Math;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Debug
{
    /// <summary>
    /// Stable update-reason precedence shared by diagnostics and contract
    /// tests. The debug shader mirrors this ordering exactly.
    /// </summary>
    public static class DebugDdgiUpdateReasonPalette
    {
        private static readonly SimpleDdgiSchedulerCandidateReason[] PrecedenceStorage =
        [
            SimpleDdgiSchedulerCandidateReason.SourceCacheInvalid,
            SimpleDdgiSchedulerCandidateReason.Topology,
            SimpleDdgiSchedulerCandidateReason.RelocationRetry,
            SimpleDdgiSchedulerCandidateReason.InactiveRetry,
            SimpleDdgiSchedulerCandidateReason.Retry,
            SimpleDdgiSchedulerCandidateReason.Fresh,
            SimpleDdgiSchedulerCandidateReason.ScrollExposed,
            SimpleDdgiSchedulerCandidateReason.GlobalDirty,
            SimpleDdgiSchedulerCandidateReason.RegionalDirty,
            SimpleDdgiSchedulerCandidateReason.Visible,
            SimpleDdgiSchedulerCandidateReason.RoutineDue,
            SimpleDdgiSchedulerCandidateReason.ConvergencePending,
            SimpleDdgiSchedulerCandidateReason.SegmentSelective,
            SimpleDdgiSchedulerCandidateReason.RadiometricRelight,
            SimpleDdgiSchedulerCandidateReason.ResidualPropagation,
            SimpleDdgiSchedulerCandidateReason.VisiblePageCohort
        ];

        public static IReadOnlyList<SimpleDdgiSchedulerCandidateReason> Precedence =>
            PrecedenceStorage;

        public static SimpleDdgiSchedulerCandidateReason ResolvePrimary(
            SimpleDdgiSchedulerCandidateReason reasons)
        {
            foreach (SimpleDdgiSchedulerCandidateReason candidate in PrecedenceStorage)
            {
                if ((reasons & candidate) != 0)
                    return candidate;
            }
            return SimpleDdgiSchedulerCandidateReason.None;
        }

        public static int CountReasons(
            SimpleDdgiSchedulerCandidateReason reasons,
            Span<int> counts)
        {
            if (counts.Length < PrecedenceStorage.Length)
            {
                throw new ArgumentException(
                    $"At least {PrecedenceStorage.Length} counters are required.",
                    nameof(counts));
            }

            int setCount = 0;
            for (int index = 0; index < PrecedenceStorage.Length; index++)
            {
                if ((reasons & PrecedenceStorage[index]) == 0)
                    continue;
                counts[index]++;
                setCount++;
            }
            return setCount;
        }

        public static Vector4 Color(SimpleDdgiSchedulerCandidateReason reason) =>
            reason switch
            {
                SimpleDdgiSchedulerCandidateReason.SourceCacheInvalid =>
                    new Vector4(0.86f, 0.08f, 1.00f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.Topology =>
                    new Vector4(1.00f, 0.08f, 0.62f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.RelocationRetry =>
                    new Vector4(1.00f, 0.38f, 0.04f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.InactiveRetry =>
                    new Vector4(1.00f, 0.10f, 0.08f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.Retry =>
                    new Vector4(1.00f, 0.22f, 0.06f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.Fresh =>
                    new Vector4(1.00f, 0.68f, 0.08f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.ScrollExposed =>
                    new Vector4(0.08f, 0.90f, 1.00f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.GlobalDirty =>
                    new Vector4(1.00f, 0.88f, 0.08f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.RegionalDirty =>
                    new Vector4(0.52f, 1.00f, 0.08f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.Visible =>
                    new Vector4(0.18f, 0.65f, 1.00f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.RoutineDue =>
                    new Vector4(0.38f, 0.42f, 1.00f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.ConvergencePending =>
                    new Vector4(0.16f, 0.90f, 0.48f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.SegmentSelective =>
                    new Vector4(0.88f, 0.26f, 1.00f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.RadiometricRelight =>
                    new Vector4(0.62f, 0.32f, 1.00f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.ResidualPropagation =>
                    new Vector4(0.10f, 0.82f, 0.58f, 0.98f),
                SimpleDdgiSchedulerCandidateReason.VisiblePageCohort =>
                    new Vector4(0.10f, 0.72f, 0.92f, 0.98f),
                _ => new Vector4(0.55f, 0.58f, 0.64f, 0.78f)
            };
    }

    public enum DebugDdgiPhysicalSlotAvailability
    {
        Resident = 0,
        Nonresident = 1,
        StaleGeneration = 2
    }

    public readonly record struct DebugDdgiPhysicalSlotIdentity(
        DebugDdgiPhysicalSlotAvailability Availability,
        uint ColorKey)
    {
        public static DebugDdgiPhysicalSlotIdentity Resolve(
            bool generationsMatch,
            in SimpleDdgiProbeAddress address,
            uint physicalPageIndex = uint.MaxValue)
        {
            if (!generationsMatch)
            {
                return new DebugDdgiPhysicalSlotIdentity(
                    DebugDdgiPhysicalSlotAvailability.StaleGeneration,
                    0u);
            }
            if (!address.Resident)
            {
                return new DebugDdgiPhysicalSlotIdentity(
                    DebugDdgiPhysicalSlotAvailability.Nonresident,
                    0u);
            }
            uint key = physicalPageIndex != uint.MaxValue
                ? checked(physicalPageIndex * 8u +
                    (address.PhysicalProbeIndex & 7u))
                : address.PhysicalProbeIndex;
            return new DebugDdgiPhysicalSlotIdentity(
                DebugDdgiPhysicalSlotAvailability.Resident,
                key);
        }
    }
}
