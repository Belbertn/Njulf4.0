using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>Whether a concrete graph binding identifies a Vulkan buffer or image.</summary>
    public enum RenderGraphConcreteResourceKind
    {
        Buffer,
        Image
    }

    /// <summary>
    /// Value-type identity of one physical Vulkan allocation range. It deliberately excludes the
    /// abstract graph ID so exact logical aliases share synchronization state without allocating
    /// string keys on the scheduler hot path.
    /// </summary>
    public readonly record struct RenderGraphAllocationIdentity(
        RenderGraphConcreteResourceKind Kind,
        ulong Handle,
        ulong ByteOffset,
        ulong ByteSize,
        ImageAspectFlags AspectMask,
        uint BaseMipLevel,
        uint LevelCount,
        uint BaseArrayLayer,
        uint LayerCount,
        int FrameIndex,
        int HistoryIndex,
        ulong AllocationGeneration);

    /// <summary>
    /// Resolves an abstract render-graph resource to one Vulkan allocation.  A graph resource may
    /// intentionally have several bindings (mip chains, ping-pong histories, or buffer sets).
    /// Handles are never inferred from bindless indices: the synchronizer needs the allocation,
    /// exact range, sharing mode, and lifetime generation that Vulkan actually sees.
    /// </summary>
    public sealed record RenderGraphConcreteResourceBinding
    {
        public RenderGraphResourceId Resource { get; init; }
        public string Name { get; init; } = string.Empty;
        public RenderGraphConcreteResourceKind Kind { get; init; }
        public Buffer Buffer { get; init; }
        public Image Image { get; init; }
        public ulong ByteOffset { get; init; }
        public ulong ByteSize { get; init; }
        /// <summary>
        /// Optional allocation capacity for a buffer binding. A value of zero means the producer
        /// does not expose a capacity, while a non-zero value lets the binding layer reject a
        /// range that would run past the Vulkan allocation.
        /// </summary>
        public ulong AllocationSize { get; init; }
        public ImageSubresourceRange SubresourceRange { get; init; }
        public ImageLayout Layout { get; init; } = ImageLayout.Undefined;
        /// <summary>
        /// Optional last-access scope for an imported allocation before graph recording begins.
        /// Supplying this pair lets the first graphics-to-compute ownership release use the
        /// producer's real synchronization scope instead of the conservative all-commands
        /// fallback. Both values must be supplied together.
        /// </summary>
        public PipelineStageFlags2 InitialStageMask { get; init; } = PipelineStageFlags2.None;
        public AccessFlags2 InitialAccessMask { get; init; } = AccessFlags2.None;
        public SharingMode SharingMode { get; init; } = SharingMode.Exclusive;
        public IReadOnlyList<uint> PermittedQueueFamilies { get; init; } = Array.Empty<uint>();
        public uint? InitialOwnerQueueFamily { get; init; }
        public int FrameIndex { get; init; } = -1;
        public int HistoryIndex { get; init; } = -1;
        public ulong AllocationGeneration { get; init; }
        public ulong ResourcePlanGeneration { get; internal init; }
        public RenderGraphResourceLifetime Lifetime { get; init; } = RenderGraphResourceLifetime.Imported;
        /// <summary>Updates an owning image wrapper after an externally emitted sync2 layout barrier.</summary>
        public Action<ImageLayout>? LayoutTracker { get; init; }

        /// <summary>
        /// Stable logical binding identity used to reject duplicate registrations within one
        /// resource-plan refresh. Physical ownership is tracked by <see cref="AllocationIdentity"/>.
        /// </summary>
        public string Key => Kind == RenderGraphConcreteResourceKind.Buffer
            ? $"{Resource}:{Name}:{Kind}:{Buffer.Handle}:{ByteOffset}:{ByteSize}:{AllocationSize}:{FrameIndex}:{HistoryIndex}:{AllocationGeneration}"
            : $"{Resource}:{Name}:{Kind}:{Image.Handle}:{SubresourceRange.AspectMask}:{SubresourceRange.BaseMipLevel}:{SubresourceRange.LevelCount}:{SubresourceRange.BaseArrayLayer}:{SubresourceRange.LayerCount}:{FrameIndex}:{HistoryIndex}:{AllocationGeneration}";

        /// <summary>
        /// Identifies the physical allocation range independently of its graph-resource view.
        /// Exact aliases (for example, a texture referenced as both a material and environment
        /// texture) share this identity so the scheduler emits one ownership timeline for the
        /// Vulkan allocation instead of maintaining unsafe state per logical ID.
        /// </summary>
        public RenderGraphAllocationIdentity AllocationIdentity => Kind == RenderGraphConcreteResourceKind.Buffer
            ? new RenderGraphAllocationIdentity(
                Kind,
                Buffer.Handle,
                ByteOffset,
                ByteSize,
                ImageAspectFlags.None,
                0,
                0,
                0,
                0,
                FrameIndex,
                HistoryIndex,
                AllocationGeneration)
            : new RenderGraphAllocationIdentity(
                Kind,
                Image.Handle,
                0,
                0,
                SubresourceRange.AspectMask,
                SubresourceRange.BaseMipLevel,
                SubresourceRange.LevelCount,
                SubresourceRange.BaseArrayLayer,
                SubresourceRange.LayerCount,
                FrameIndex,
                HistoryIndex,
                AllocationGeneration);

        public static RenderGraphConcreteResourceBinding ForBuffer(
            RenderGraphResourceId resource,
            string name,
            Buffer buffer,
            ulong byteSize,
            IReadOnlyList<uint> permittedQueueFamilies,
            uint? initialOwnerQueueFamily,
            SharingMode sharingMode = SharingMode.Exclusive,
            ulong byteOffset = 0,
            int frameIndex = -1,
            int historyIndex = -1,
            ulong allocationGeneration = 1,
            RenderGraphResourceLifetime lifetime = RenderGraphResourceLifetime.Imported,
            Action<ImageLayout>? layoutTracker = null,
            ulong allocationSize = 0,
            PipelineStageFlags2 initialStageMask = PipelineStageFlags2.None,
            AccessFlags2 initialAccessMask = AccessFlags2.None)
        {
            return new RenderGraphConcreteResourceBinding
            {
                Resource = resource,
                Name = name,
                Kind = RenderGraphConcreteResourceKind.Buffer,
                Buffer = buffer,
                ByteOffset = byteOffset,
                ByteSize = byteSize,
                AllocationSize = allocationSize,
                InitialStageMask = initialStageMask,
                InitialAccessMask = initialAccessMask,
                SharingMode = sharingMode,
                PermittedQueueFamilies = permittedQueueFamilies ?? Array.Empty<uint>(),
                InitialOwnerQueueFamily = initialOwnerQueueFamily,
                FrameIndex = frameIndex,
                HistoryIndex = historyIndex,
                AllocationGeneration = allocationGeneration,
                Lifetime = lifetime,
                LayoutTracker = layoutTracker
            };
        }

        public static RenderGraphConcreteResourceBinding ForImage(
            RenderGraphResourceId resource,
            string name,
            Image image,
            ImageSubresourceRange subresourceRange,
            ImageLayout layout,
            IReadOnlyList<uint> permittedQueueFamilies,
            uint? initialOwnerQueueFamily,
            SharingMode sharingMode = SharingMode.Exclusive,
            int frameIndex = -1,
            int historyIndex = -1,
            ulong allocationGeneration = 1,
            RenderGraphResourceLifetime lifetime = RenderGraphResourceLifetime.Imported,
            Action<ImageLayout>? layoutTracker = null,
            PipelineStageFlags2 initialStageMask = PipelineStageFlags2.None,
            AccessFlags2 initialAccessMask = AccessFlags2.None)
        {
            return new RenderGraphConcreteResourceBinding
            {
                Resource = resource,
                Name = name,
                Kind = RenderGraphConcreteResourceKind.Image,
                Image = image,
                SubresourceRange = subresourceRange,
                Layout = layout,
                InitialStageMask = initialStageMask,
                InitialAccessMask = initialAccessMask,
                SharingMode = sharingMode,
                PermittedQueueFamilies = permittedQueueFamilies ?? Array.Empty<uint>(),
                InitialOwnerQueueFamily = initialOwnerQueueFamily,
                FrameIndex = frameIndex,
                HistoryIndex = historyIndex,
                AllocationGeneration = allocationGeneration,
                Lifetime = lifetime,
                LayoutTracker = layoutTracker
            };
        }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new InvalidOperationException($"Concrete binding for '{Resource}' requires a name.");
            if (AllocationGeneration == 0)
                throw new InvalidOperationException($"Concrete binding '{Name}' for '{Resource}' has no allocation generation.");
            if (PermittedQueueFamilies == null || PermittedQueueFamilies.Count == 0)
                throw new InvalidOperationException($"Concrete binding '{Name}' for '{Resource}' has no permitted queue family.");
            if (PermittedQueueFamilies.Distinct().Count() != PermittedQueueFamilies.Count)
                throw new InvalidOperationException($"Concrete binding '{Name}' for '{Resource}' repeats a permitted queue family.");
            if (SharingMode == SharingMode.Concurrent && PermittedQueueFamilies.Count < 2)
                throw new InvalidOperationException($"Concurrent binding '{Name}' for '{Resource}' requires at least two queue families.");
            if (SharingMode == SharingMode.Concurrent && InitialOwnerQueueFamily.HasValue)
            {
                throw new InvalidOperationException(
                    $"Concurrent binding '{Name}' for '{Resource}' cannot declare an exclusive current owner.");
            }
            if (InitialOwnerQueueFamily.HasValue &&
                !PermittedQueueFamilies.Contains(InitialOwnerQueueFamily.Value))
            {
                throw new InvalidOperationException(
                    $"Concrete binding '{Name}' for '{Resource}' names an owner outside its permitted queue families.");
            }
            bool hasInitialStage = InitialStageMask != PipelineStageFlags2.None;
            bool hasInitialAccess = InitialAccessMask != AccessFlags2.None;
            if (hasInitialStage != hasInitialAccess)
            {
                throw new InvalidOperationException(
                    $"Concrete binding '{Name}' for '{Resource}' must declare both initial stage and access masks, or neither.");
            }

            if (Kind == RenderGraphConcreteResourceKind.Buffer)
            {
                if (Buffer.Handle == 0)
                    throw new InvalidOperationException($"Concrete buffer binding '{Name}' for '{Resource}' has no Vulkan buffer handle.");
                if (ByteSize == 0)
                    throw new InvalidOperationException($"Concrete buffer binding '{Name}' for '{Resource}' has an empty range.");
                if (ByteOffset > ulong.MaxValue - ByteSize)
                    throw new InvalidOperationException($"Concrete buffer binding '{Name}' for '{Resource}' overflows its byte range.");
                if (AllocationSize != 0 && ByteOffset + ByteSize > AllocationSize)
                {
                    throw new InvalidOperationException(
                        $"Concrete buffer binding '{Name}' for '{Resource}' extends past its declared allocation capacity.");
                }
                return;
            }

            if (Image.Handle == 0)
                throw new InvalidOperationException($"Concrete image binding '{Name}' for '{Resource}' has no Vulkan image handle.");
            if (SubresourceRange.AspectMask == ImageAspectFlags.None ||
                SubresourceRange.LevelCount == 0 ||
                SubresourceRange.LayerCount == 0)
            {
                throw new InvalidOperationException($"Concrete image binding '{Name}' for '{Resource}' has an invalid subresource range.");
            }
            if ((ulong)SubresourceRange.BaseMipLevel + SubresourceRange.LevelCount > (ulong)uint.MaxValue + 1UL ||
                (ulong)SubresourceRange.BaseArrayLayer + SubresourceRange.LayerCount > (ulong)uint.MaxValue + 1UL)
            {
                throw new InvalidOperationException(
                    $"Concrete image binding '{Name}' for '{Resource}' overflows its subresource range.");
            }
        }
    }

    /// <summary>
    /// Mutable owner-state lives separately from immutable binding descriptions.  The scheduler can
    /// therefore reject a plan without mutating ownership, then atomically commit the accepted
    /// plan only after all command buffers were recorded successfully.
    /// </summary>
    public sealed class RenderGraphResourceBindings
    {
        private readonly Dictionary<RenderGraphResourceId, List<RenderGraphConcreteResourceBinding>> _bindings = new();
        private readonly Dictionary<(RenderGraphResourceId Resource, int FrameIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>> _frameBindingCache = new();
        private readonly Dictionary<RenderGraphAllocationIdentity, uint> _owners = new();
        private ulong _generation;
        private int _stalePlanRejectionCount;

        public ulong Generation => _generation;
        public int StalePlanRejectionCount => _stalePlanRejectionCount;
        public int BindingCount => _bindings.Values.Sum(list => list.Count);

        public IReadOnlyList<RenderGraphConcreteResourceBinding> GetBindings(
            RenderGraphResourceId resource,
            int frameIndex = -1)
        {
            if (!_bindings.TryGetValue(resource, out List<RenderGraphConcreteResourceBinding>? candidates))
                return Array.Empty<RenderGraphConcreteResourceBinding>();

            if (frameIndex < 0)
                return candidates.ToArray();

            var key = (resource, frameIndex);
            if (_frameBindingCache.TryGetValue(key, out IReadOnlyList<RenderGraphConcreteResourceBinding>? cached))
                return cached;

            RenderGraphConcreteResourceBinding[] resolved = candidates
                .Where(binding => binding.FrameIndex < 0 || binding.FrameIndex == frameIndex)
                .ToArray();
            _frameBindingCache.Add(key, resolved);
            return resolved;
        }

        public bool HasCompleteBinding(RenderGraphResourceId resource, int frameIndex = -1) =>
            GetBindings(resource, frameIndex).Count > 0;

        public uint? GetCurrentOwner(RenderGraphConcreteResourceBinding binding)
        {
            if (binding == null)
                throw new ArgumentNullException(nameof(binding));

            if (binding.SharingMode == SharingMode.Concurrent)
                return null;
            return _owners.TryGetValue(binding.AllocationIdentity, out uint owner)
                ? owner
                : binding.InitialOwnerQueueFamily;
        }

        public bool IsCurrent(RenderGraphConcreteResourceBinding binding) =>
            binding != null && binding.ResourcePlanGeneration == _generation;

        public void Replace(IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            if (bindings == null)
                throw new ArgumentNullException(nameof(bindings));

            var replacement = new Dictionary<RenderGraphResourceId, List<RenderGraphConcreteResourceBinding>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            ulong nextGeneration = checked(_generation + 1UL);
            foreach (RenderGraphConcreteResourceBinding binding in bindings)
            {
                if (binding == null)
                    throw new InvalidOperationException("A concrete render-graph binding cannot be null.");
                binding.Validate();
                RenderGraphConcreteResourceBinding stamped = binding with { ResourcePlanGeneration = nextGeneration };
                if (!keys.Add(stamped.Key))
                    throw new InvalidOperationException($"Duplicate concrete binding key '{stamped.Key}'.");
                if (!replacement.TryGetValue(stamped.Resource, out List<RenderGraphConcreteResourceBinding>? list))
                {
                    list = new List<RenderGraphConcreteResourceBinding>();
                    replacement.Add(stamped.Resource, list);
                }

                list.Add(stamped);
            }

            ValidateNoOverlappingBindings(replacement);

            _bindings.Clear();
            foreach ((RenderGraphResourceId resource, List<RenderGraphConcreteResourceBinding> list) in replacement)
                _bindings.Add(resource, list);
            _frameBindingCache.Clear();
            _owners.Clear();
            _generation = nextGeneration;
        }

        public void Invalidate()
        {
            _bindings.Clear();
            _frameBindingCache.Clear();
            _owners.Clear();
            _generation = checked(_generation + 1UL);
        }

        public void RecordStalePlanRejection() => _stalePlanRejectionCount++;

        public void CommitOwner(RenderGraphConcreteResourceBinding binding, uint ownerQueueFamily)
        {
            if (binding == null)
                throw new ArgumentNullException(nameof(binding));
            if (!IsCurrent(binding))
            {
                RecordStalePlanRejection();
                throw new InvalidOperationException(
                    $"Cannot commit ownership for stale binding '{binding.Name}' (plan {binding.ResourcePlanGeneration}, current {_generation}).");
            }
            if (binding.SharingMode == SharingMode.Concurrent)
                return;
            if (!binding.PermittedQueueFamilies.Contains(ownerQueueFamily))
                throw new InvalidOperationException($"Queue family {ownerQueueFamily} is not permitted for binding '{binding.Name}'.");

            _owners[binding.AllocationIdentity] = ownerQueueFamily;
        }

        private static void ValidateNoOverlappingBindings(
            IReadOnlyDictionary<RenderGraphResourceId, List<RenderGraphConcreteResourceBinding>> bindings)
        {
            // Partially overlapping aliases cannot safely share one ownership timeline because
            // the scheduler has no subrange-splitting model for arbitrary aliases. Exact aliases
            // are permitted only when their synchronization contracts are identical; they share
            // AllocationIdentity owner state and are treated as one physical range by the scheduler.
            RenderGraphConcreteResourceBinding[] allBindings = bindings
                .SelectMany(pair => pair.Value)
                .ToArray();
            for (int firstIndex = 0; firstIndex < allBindings.Length; firstIndex++)
            {
                RenderGraphConcreteResourceBinding first = allBindings[firstIndex];
                for (int secondIndex = firstIndex + 1; secondIndex < allBindings.Length; secondIndex++)
                {
                    RenderGraphConcreteResourceBinding second = allBindings[secondIndex];
                    if (!Overlaps(first, second))
                        continue;

                    if (AreCompatibleExactAliases(first, second))
                        continue;

                    throw new InvalidOperationException(
                        $"Concrete bindings '{first.Name}' ({first.Resource}) and '{second.Name}' ({second.Resource}) overlap. " +
                        "Split the declaration into disjoint ranges or expose one explicitly synchronized allocation group.");
                }
            }
        }

        private static bool AreCompatibleExactAliases(
            RenderGraphConcreteResourceBinding first,
            RenderGraphConcreteResourceBinding second)
        {
            if (first.Resource == second.Resource ||
                first.AllocationIdentity != second.AllocationIdentity ||
                first.Layout != second.Layout ||
                first.SharingMode != second.SharingMode ||
                first.InitialOwnerQueueFamily != second.InitialOwnerQueueFamily ||
                first.InitialStageMask != second.InitialStageMask ||
                first.InitialAccessMask != second.InitialAccessMask ||
                first.Lifetime != second.Lifetime ||
                first.PermittedQueueFamilies.Count != second.PermittedQueueFamilies.Count)
            {
                return false;
            }

            // If an image wrapper tracks layout externally, each alias must update the same
            // tracker. Otherwise one logical view could retain a stale layout after a handoff.
            if (first.Kind == RenderGraphConcreteResourceKind.Image &&
                !Equals(first.LayoutTracker, second.LayoutTracker))
            {
                return false;
            }

            for (int index = 0; index < first.PermittedQueueFamilies.Count; index++)
            {
                if (first.PermittedQueueFamilies[index] != second.PermittedQueueFamilies[index])
                    return false;
            }

            return true;
        }

        private static bool Overlaps(
            RenderGraphConcreteResourceBinding first,
            RenderGraphConcreteResourceBinding second)
        {
            if (first.Kind != second.Kind ||
                !FrameSelectionsOverlap(first.FrameIndex, second.FrameIndex) ||
                !FrameSelectionsOverlap(first.HistoryIndex, second.HistoryIndex))
            {
                return false;
            }

            if (first.Kind == RenderGraphConcreteResourceKind.Buffer)
            {
                if (first.Buffer.Handle != second.Buffer.Handle)
                    return false;
                return RangesOverlap(first.ByteOffset, first.ByteSize, second.ByteOffset, second.ByteSize);
            }

            if (first.Image.Handle != second.Image.Handle)
                return false;

            ImageSubresourceRange a = first.SubresourceRange;
            ImageSubresourceRange b = second.SubresourceRange;
            return (a.AspectMask & b.AspectMask) != 0 &&
                   RangesOverlap(a.BaseMipLevel, a.LevelCount, b.BaseMipLevel, b.LevelCount) &&
                   RangesOverlap(a.BaseArrayLayer, a.LayerCount, b.BaseArrayLayer, b.LayerCount);
        }

        private static bool FrameSelectionsOverlap(int first, int second) =>
            first < 0 || second < 0 || first == second;

        private static bool RangesOverlap(ulong firstOffset, ulong firstSize, ulong secondOffset, ulong secondSize)
        {
            if (firstSize == 0 || secondSize == 0)
                return false;

            ulong firstEnd = checked(firstOffset + firstSize);
            ulong secondEnd = checked(secondOffset + secondSize);
            return firstOffset < secondEnd && secondOffset < firstEnd;
        }
    }
}
