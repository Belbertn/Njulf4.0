using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Pipeline
{
    public sealed record RenderGraphAliasSettings(bool Enabled)
    {
        public static RenderGraphAliasSettings Default { get; } = new(Enabled: true);
        public static RenderGraphAliasSettings Disabled { get; } = new(Enabled: false);
    }

    public sealed record RenderGraphAliasGroup(
        int GroupIndex,
        IReadOnlyList<RenderGraphImageAllocationRequest> Images,
        ulong ReservedBytes,
        ulong AliasedBytesSaved);

    public sealed record RenderGraphAliasPlan(
        IReadOnlyList<RenderGraphAliasGroup> Groups,
        ulong UnaliasedTransientBytes,
        ulong AliasedTransientBytes,
        ulong PeakTransientBytes,
        ulong AliasedBytesSaved)
    {
        public static RenderGraphAliasPlan Empty { get; } = new([], 0, 0, 0, 0);
    }

    public static class RenderGraphAliasPlanner
    {
        public static RenderGraphAliasPlan Build(
            RenderGraphDeclarationPlan declarationPlan,
            RenderGraphImageAllocationPlan allocationPlan,
            RenderGraphAliasSettings? settings = null)
        {
            if (declarationPlan == null)
                throw new ArgumentNullException(nameof(declarationPlan));
            if (allocationPlan == null)
                throw new ArgumentNullException(nameof(allocationPlan));

            settings ??= RenderGraphAliasSettings.Default;
            RenderGraphImageAllocationRequest[] candidates = allocationPlan.Images
                .Where(IsAliasCandidate)
                .OrderByDescending(image => image.EstimatedBytes)
                .ThenBy(image => image.Descriptor.Name, StringComparer.Ordinal)
                .ToArray();

            ulong unaliasedBytes = Sum(candidates);
            if (!settings.Enabled || candidates.Length == 0)
                return new RenderGraphAliasPlan([], unaliasedBytes, 0, unaliasedBytes, 0);

            Dictionary<RenderGraphResourceHandle, RenderGraphResourceLifetime> lifetimes = declarationPlan.Diagnostics.ResourceLifetimes
                .ToDictionary(lifetime => lifetime.Handle);
            var groups = new List<List<RenderGraphImageAllocationRequest>>();

            foreach (RenderGraphImageAllocationRequest candidate in candidates)
            {
                if (!lifetimes.TryGetValue(candidate.Handle, out RenderGraphResourceLifetime? candidateLifetime) ||
                    candidateLifetime == null)
                    continue;

                List<RenderGraphImageAllocationRequest>? targetGroup = null;
                foreach (List<RenderGraphImageAllocationRequest> group in groups)
                {
                    if (CanAliasWithGroup(group, candidate, candidateLifetime, lifetimes))
                    {
                        targetGroup = group;
                        break;
                    }
                }

                if (targetGroup == null)
                {
                    targetGroup = new List<RenderGraphImageAllocationRequest>();
                    groups.Add(targetGroup);
                }

                targetGroup.Add(candidate);
            }

            var aliasGroups = new List<RenderGraphAliasGroup>(groups.Count);
            ulong peakBytes = 0;
            ulong aliasedBytes = 0;
            ulong savedBytes = 0;

            for (int i = 0; i < groups.Count; i++)
            {
                List<RenderGraphImageAllocationRequest> group = groups[i];
                ulong groupBytes = group.Max(image => image.EstimatedBytes);
                ulong groupUnaliasedBytes = Sum(group);
                ulong groupSavedBytes = groupUnaliasedBytes - groupBytes;
                peakBytes = checked(peakBytes + groupBytes);
                aliasedBytes = checked(aliasedBytes + groupUnaliasedBytes);
                savedBytes = checked(savedBytes + groupSavedBytes);
                aliasGroups.Add(new RenderGraphAliasGroup(i, group, groupBytes, groupSavedBytes));
            }

            return new RenderGraphAliasPlan(aliasGroups, unaliasedBytes, aliasedBytes, peakBytes, savedBytes);
        }

        private static bool IsAliasCandidate(RenderGraphImageAllocationRequest image)
        {
            return image.ShouldAllocate &&
                   image.Category == RenderGraphImageAllocationCategory.TransientRenderTarget &&
                   image.Descriptor.Persistence == RenderGraphResourcePersistence.Transient &&
                   image.EstimatedBytes > 0;
        }

        private static bool AreAliasCompatible(RenderGraphImageAllocationRequest left, RenderGraphImageAllocationRequest right)
        {
            return left.Descriptor.Format == right.Descriptor.Format &&
                   left.Descriptor.Width == right.Descriptor.Width &&
                   left.Descriptor.Height == right.Descriptor.Height &&
                   left.Descriptor.MipCount == right.Descriptor.MipCount &&
                   left.Descriptor.ArrayLayers == right.Descriptor.ArrayLayers &&
                   left.Descriptor.Samples == right.Descriptor.Samples;
        }

        private static bool CanAliasWithGroup(
            IReadOnlyList<RenderGraphImageAllocationRequest> group,
            RenderGraphImageAllocationRequest candidate,
            RenderGraphResourceLifetime candidateLifetime,
            IReadOnlyDictionary<RenderGraphResourceHandle, RenderGraphResourceLifetime> lifetimes)
        {
            foreach (RenderGraphImageAllocationRequest existing in group)
            {
                if (!lifetimes.TryGetValue(existing.Handle, out RenderGraphResourceLifetime? existingLifetime) ||
                    existingLifetime == null)
                    return false;
                if (!AreAliasCompatible(existing, candidate))
                    return false;
                if (LifetimesOverlap(existingLifetime, candidateLifetime))
                    return false;
            }

            return true;
        }

        private static bool LifetimesOverlap(RenderGraphResourceLifetime left, RenderGraphResourceLifetime right)
        {
            return left.FirstUsePassIndex <= right.LastUsePassIndex &&
                   right.FirstUsePassIndex <= left.LastUsePassIndex;
        }

        private static ulong Sum(IEnumerable<RenderGraphImageAllocationRequest> images)
        {
            ulong total = 0;
            foreach (RenderGraphImageAllocationRequest image in images)
                total = checked(total + image.EstimatedBytes);
            return total;
        }
    }
}
