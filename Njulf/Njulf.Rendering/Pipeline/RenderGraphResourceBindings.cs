using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// Typed logical identity for a binding inside one resource plan. Debug names are deliberately
    /// excluded: they are diagnostics, not synchronization identity, and formatting them into keys
    /// made every plan rebuild allocate a large number of short-lived strings.
    /// </summary>
    public readonly record struct RenderGraphBindingIdentity(
        RenderGraphResourceId Resource,
        RenderGraphAllocationIdentity Allocation);

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
        /// Reads layout state owned by an imported image wrapper. Resource plans are immutable and
        /// can outlive a frame, while image layouts continue to change as commands are recorded.
        /// Keeping that state behind a provider avoids rebuilding otherwise-identical bindings.
        /// </summary>
        public Func<ImageLayout>? LayoutProvider { get; init; }

        /// <summary>
        /// Stable typed identity used to reject duplicate registrations within one immutable plan.
        /// Physical ownership is tracked by <see cref="AllocationIdentity"/>.
        /// </summary>
        public RenderGraphBindingIdentity Identity => new(Resource, AllocationIdentity);

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
            AccessFlags2 initialAccessMask = AccessFlags2.None,
            Func<ImageLayout>? layoutProvider = null)
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
                PermittedQueueFamilies = CopyQueueFamilies(permittedQueueFamilies),
                InitialOwnerQueueFamily = initialOwnerQueueFamily,
                FrameIndex = frameIndex,
                HistoryIndex = historyIndex,
                AllocationGeneration = allocationGeneration,
                Lifetime = lifetime,
                LayoutTracker = layoutTracker,
                LayoutProvider = layoutProvider
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
            AccessFlags2 initialAccessMask = AccessFlags2.None,
            Func<ImageLayout>? layoutProvider = null)
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
                PermittedQueueFamilies = CopyQueueFamilies(permittedQueueFamilies),
                InitialOwnerQueueFamily = initialOwnerQueueFamily,
                FrameIndex = frameIndex,
                HistoryIndex = historyIndex,
                AllocationGeneration = allocationGeneration,
                Lifetime = lifetime,
                LayoutTracker = layoutTracker,
                LayoutProvider = layoutProvider
            };
        }

        private static IReadOnlyList<uint> CopyQueueFamilies(IReadOnlyList<uint>? queueFamilies)
        {
            if (queueFamilies == null || queueFamilies.Count == 0)
                return Array.Empty<uint>();

            var copy = new uint[queueFamilies.Count];
            for (int index = 0; index < copy.Length; index++)
                copy[index] = queueFamilies[index];
            return Array.AsReadOnly(copy);
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
                if (LayoutProvider != null)
                    throw new InvalidOperationException($"Concrete buffer binding '{Name}' for '{Resource}' cannot declare an image-layout provider.");
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
    /// Fully validated immutable lookup plan. Construction performs all duplicate, range, alias,
    /// and overlap checks once; frame execution only indexes precomputed read-only arrays.
    /// </summary>
    public sealed class RenderGraphResourcePlan
    {
        private readonly Guid _ownerId;
        private readonly IReadOnlyDictionary<RenderGraphResourceId, IReadOnlyList<RenderGraphConcreteResourceBinding>> _bindings;
        private readonly IReadOnlyDictionary<(RenderGraphResourceId Resource, int FrameIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>> _frameBindings;
        private readonly IReadOnlyDictionary<RenderGraphResourceId, IReadOnlyList<RenderGraphConcreteResourceBinding>> _staticBindings;
        // History selection is precomputed with the rest of the immutable
        // plan. Temporal hot paths must never filter a buffer/image set or
        // accidentally bind both banks because a logical resource has two
        // concrete allocations.
        private readonly IReadOnlyDictionary<(RenderGraphResourceId Resource, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>> _staticHistoryBindings;
        private readonly IReadOnlyDictionary<(RenderGraphResourceId Resource, int FrameIndex, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>> _frameHistoryBindings;

        internal RenderGraphResourcePlan(
            Guid ownerId,
            ulong generation,
            IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            _ownerId = ownerId;
            Generation = generation;

            var replacement = new Dictionary<RenderGraphResourceId, List<RenderGraphConcreteResourceBinding>>();
            var identities = new HashSet<RenderGraphBindingIdentity>();
            var flattened = new List<RenderGraphConcreteResourceBinding>();
            foreach (RenderGraphConcreteResourceBinding binding in bindings)
            {
                if (binding == null)
                    throw new InvalidOperationException("A concrete render-graph binding cannot be null.");

                binding.Validate();
                RenderGraphConcreteResourceBinding stamped = binding with { ResourcePlanGeneration = generation };
                if (!identities.Add(stamped.Identity))
                {
                    throw new InvalidOperationException(
                        $"Duplicate concrete binding identity '{stamped.Identity}' ({stamped.Name}).");
                }

                if (!replacement.TryGetValue(stamped.Resource, out List<RenderGraphConcreteResourceBinding>? list))
                {
                    list = new List<RenderGraphConcreteResourceBinding>();
                    replacement.Add(stamped.Resource, list);
                }

                list.Add(stamped);
                flattened.Add(stamped);
            }

            ValidateNoOverlappingBindings(flattened);

            Bindings = AsReadOnly(flattened);
            BindingCount = flattened.Count;

            var immutableBindings = new Dictionary<RenderGraphResourceId, IReadOnlyList<RenderGraphConcreteResourceBinding>>(replacement.Count);
            var staticBindings = new Dictionary<RenderGraphResourceId, IReadOnlyList<RenderGraphConcreteResourceBinding>>(replacement.Count);
            var selectedBindings = new Dictionary<(RenderGraphResourceId Resource, int FrameIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>>();
            var staticHistoryBindings = new Dictionary<(RenderGraphResourceId Resource, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>>();
            var frameHistoryBindings = new Dictionary<(RenderGraphResourceId Resource, int FrameIndex, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>>();
            var frameIndices = new HashSet<int>();
            foreach (RenderGraphConcreteResourceBinding binding in flattened)
            {
                if (binding.FrameIndex >= 0)
                    frameIndices.Add(binding.FrameIndex);
            }

            foreach ((RenderGraphResourceId resource, List<RenderGraphConcreteResourceBinding> list) in replacement)
            {
                immutableBindings.Add(resource, AsReadOnly(list));
                RenderGraphConcreteResourceBinding[] staticForResource = list
                    .Where(static binding => binding.FrameIndex < 0)
                    .ToArray();
                staticBindings.Add(resource, AsReadOnly(staticForResource));
                AddHistoryBindings(
                    staticHistoryBindings,
                    resource,
                    frameIndex: -1,
                    staticForResource);
                foreach (int frameIndex in frameIndices)
                {
                    RenderGraphConcreteResourceBinding[] frameForResource = list
                        .Where(binding => binding.FrameIndex < 0 || binding.FrameIndex == frameIndex)
                        .ToArray();
                    selectedBindings.Add(
                        (resource, frameIndex),
                        AsReadOnly(frameForResource));
                    AddHistoryBindings(
                        frameHistoryBindings,
                        resource,
                        frameIndex,
                        frameForResource);
                }
            }

            _bindings = new ReadOnlyDictionary<RenderGraphResourceId, IReadOnlyList<RenderGraphConcreteResourceBinding>>(immutableBindings);
            _staticBindings = new ReadOnlyDictionary<RenderGraphResourceId, IReadOnlyList<RenderGraphConcreteResourceBinding>>(staticBindings);
            _frameBindings = new ReadOnlyDictionary<(RenderGraphResourceId Resource, int FrameIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>>(selectedBindings);
            _staticHistoryBindings = new ReadOnlyDictionary<(RenderGraphResourceId Resource, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>>(staticHistoryBindings);
            _frameHistoryBindings = new ReadOnlyDictionary<(RenderGraphResourceId Resource, int FrameIndex, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>>(frameHistoryBindings);
        }

        public ulong Generation { get; }
        public int BindingCount { get; }
        public IReadOnlyList<RenderGraphConcreteResourceBinding> Bindings { get; }

        internal bool BelongsTo(Guid ownerId) => _ownerId == ownerId;

        public IReadOnlyList<RenderGraphConcreteResourceBinding> GetBindings(
            RenderGraphResourceId resource,
            int frameIndex = -1)
        {
            if (!_bindings.TryGetValue(resource, out IReadOnlyList<RenderGraphConcreteResourceBinding>? all))
                return Array.Empty<RenderGraphConcreteResourceBinding>();
            if (frameIndex < 0)
                return all;
            if (_frameBindings.TryGetValue((resource, frameIndex), out IReadOnlyList<RenderGraphConcreteResourceBinding>? selected))
                return selected;
            return _staticBindings[resource];
        }

        /// <summary>
        /// Resolves one physical history bank without allocating or scanning at
        /// frame time.  <see cref="RenderGraphHistoryBindingSelection.All"/>
        /// deliberately preserves the historical set behaviour used by
        /// non-temporal resources.
        /// </summary>
        public IReadOnlyList<RenderGraphConcreteResourceBinding> GetBindings(
            RenderGraphResourceId resource,
            int frameIndex,
            RenderGraphHistoryBindingSelection historyBinding)
        {
            if (historyBinding == RenderGraphHistoryBindingSelection.All)
                return GetBindings(resource, frameIndex);

            int historyIndex = ResolveHistoryIndex(frameIndex, historyBinding);
            if (frameIndex >= 0 &&
                _frameHistoryBindings.TryGetValue(
                    (resource, frameIndex, historyIndex),
                    out IReadOnlyList<RenderGraphConcreteResourceBinding>? frameBindings))
            {
                return frameBindings;
            }

            return _staticHistoryBindings.TryGetValue(
                (resource, historyIndex),
                out IReadOnlyList<RenderGraphConcreteResourceBinding>? staticBindings)
                ? staticBindings
                : Array.Empty<RenderGraphConcreteResourceBinding>();
        }

        internal static int ResolveHistoryIndex(
            int frameIndex,
            RenderGraphHistoryBindingSelection historyBinding)
        {
            return historyBinding switch
            {
                RenderGraphHistoryBindingSelection.Bank0 => 0,
                RenderGraphHistoryBindingSelection.Bank1 => 1,
                RenderGraphHistoryBindingSelection.Current when frameIndex >= 0 =>
                    frameIndex & 1,
                RenderGraphHistoryBindingSelection.Previous when frameIndex >= 0 =>
                    (frameIndex + 1) & 1,
                RenderGraphHistoryBindingSelection.All => -1,
                RenderGraphHistoryBindingSelection.Current or
                    RenderGraphHistoryBindingSelection.Previous => throw new ArgumentOutOfRangeException(
                        nameof(frameIndex),
                        "Current/previous history selection requires a non-negative frame index."),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(historyBinding),
                    historyBinding,
                    "Unknown render-graph history binding selection.")
            };
        }

        private static void AddHistoryBindings(
            IDictionary<(RenderGraphResourceId Resource, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>> destination,
            RenderGraphResourceId resource,
            int frameIndex,
            IReadOnlyList<RenderGraphConcreteResourceBinding> bindings)
        {
            for (int historyIndex = 0; historyIndex <= 1; historyIndex++)
            {
                RenderGraphConcreteResourceBinding[] selected = bindings
                    .Where(binding => binding.HistoryIndex == historyIndex)
                    .ToArray();
                if (selected.Length != 0)
                    destination.Add((resource, historyIndex), AsReadOnly(selected));
            }
        }

        private static void AddHistoryBindings(
            IDictionary<(RenderGraphResourceId Resource, int FrameIndex, int HistoryIndex), IReadOnlyList<RenderGraphConcreteResourceBinding>> destination,
            RenderGraphResourceId resource,
            int frameIndex,
            IReadOnlyList<RenderGraphConcreteResourceBinding> bindings)
        {
            for (int historyIndex = 0; historyIndex <= 1; historyIndex++)
            {
                RenderGraphConcreteResourceBinding[] selected = bindings
                    .Where(binding => binding.HistoryIndex == historyIndex)
                    .ToArray();
                if (selected.Length != 0)
                    destination.Add((resource, frameIndex, historyIndex), AsReadOnly(selected));
            }
        }

        private static IReadOnlyList<RenderGraphConcreteResourceBinding> AsReadOnly(
            IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            RenderGraphConcreteResourceBinding[] array = bindings as RenderGraphConcreteResourceBinding[] ?? bindings.ToArray();
            return array.Length == 0
                ? Array.Empty<RenderGraphConcreteResourceBinding>()
                : Array.AsReadOnly(array);
        }

        private static void ValidateNoOverlappingBindings(
            IReadOnlyList<RenderGraphConcreteResourceBinding> bindings)
        {
            // Validate exact aliases once and collapse them before the range sweep. This keeps a
            // material/environment alias set from turning validation back into quadratic work.
            var physicalRanges = new List<RenderGraphConcreteResourceBinding>(bindings.Count);
            foreach (IGrouping<RenderGraphAllocationIdentity, RenderGraphConcreteResourceBinding> aliasGroup in
                     bindings.GroupBy(static binding => binding.AllocationIdentity))
            {
                RenderGraphConcreteResourceBinding first = aliasGroup.First();
                foreach (RenderGraphConcreteResourceBinding alias in aliasGroup.Skip(1))
                {
                    if (!AreCompatibleExactAliases(first, alias))
                        ThrowOverlap(first, alias);
                }

                physicalRanges.Add(first);
            }

            foreach (IGrouping<PhysicalHandleIdentity, RenderGraphConcreteResourceBinding> handleGroup in
                     physicalRanges.GroupBy(static binding => new PhysicalHandleIdentity(
                         binding.Kind,
                         binding.Kind == RenderGraphConcreteResourceKind.Buffer
                             ? binding.Buffer.Handle
                             : binding.Image.Handle)))
            {
                RenderGraphConcreteResourceBinding[] sorted = handleGroup
                    .OrderBy(static binding => PrimaryOffset(binding))
                    .ThenBy(static binding => SecondaryOffset(binding))
                    .ToArray();
                var active = new List<RenderGraphConcreteResourceBinding>();
                foreach (RenderGraphConcreteResourceBinding current in sorted)
                {
                    ulong currentStart = PrimaryOffset(current);
                    for (int activeIndex = active.Count - 1; activeIndex >= 0; activeIndex--)
                    {
                        RenderGraphConcreteResourceBinding candidate = active[activeIndex];
                        if (PrimaryEnd(candidate) <= currentStart)
                        {
                            active.RemoveAt(activeIndex);
                            continue;
                        }

                        if (Overlaps(candidate, current))
                            ThrowOverlap(candidate, current);
                    }

                    active.Add(current);
                }
            }
        }

        private static ulong PrimaryOffset(RenderGraphConcreteResourceBinding binding) =>
            binding.Kind == RenderGraphConcreteResourceKind.Buffer
                ? binding.ByteOffset
                : binding.SubresourceRange.BaseMipLevel;

        private static ulong SecondaryOffset(RenderGraphConcreteResourceBinding binding) =>
            binding.Kind == RenderGraphConcreteResourceKind.Buffer
                ? 0UL
                : binding.SubresourceRange.BaseArrayLayer;

        private static ulong PrimaryEnd(RenderGraphConcreteResourceBinding binding) =>
            binding.Kind == RenderGraphConcreteResourceKind.Buffer
                ? checked(binding.ByteOffset + binding.ByteSize)
                : checked((ulong)binding.SubresourceRange.BaseMipLevel + binding.SubresourceRange.LevelCount);

        private static void ThrowOverlap(
            RenderGraphConcreteResourceBinding first,
            RenderGraphConcreteResourceBinding second)
        {
            throw new InvalidOperationException(
                $"Concrete bindings '{first.Name}' ({first.Resource}) and '{second.Name}' ({second.Resource}) overlap. " +
                "Split the declaration into disjoint ranges or expose one explicitly synchronized allocation group.");
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

            if (first.Kind == RenderGraphConcreteResourceKind.Image &&
                (!Equals(first.LayoutTracker, second.LayoutTracker) ||
                 !Equals(first.LayoutProvider, second.LayoutProvider)))
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
                !FrameSelectionsOverlap(first.FrameIndex, second.FrameIndex))
            {
                return false;
            }

            // Unlike a frame-slot selection, current and previous history
            // banks can be accessed by one temporal dispatch at the same
            // time.  They must therefore never be allowed to overlap merely
            // because their logical HistoryIndex differs.

            if (first.Kind == RenderGraphConcreteResourceKind.Buffer)
                return RangesOverlap(first.ByteOffset, first.ByteSize, second.ByteOffset, second.ByteSize);

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
            ulong firstEnd = checked(firstOffset + firstSize);
            ulong secondEnd = checked(secondOffset + secondSize);
            return firstOffset < secondEnd && secondOffset < firstEnd;
        }

        private readonly record struct PhysicalHandleIdentity(
            RenderGraphConcreteResourceKind Kind,
            ulong Handle);
    }

    /// <summary>
    /// Mutable owner-state lives separately from immutable binding descriptions.  The scheduler can
    /// therefore reject a plan without mutating ownership, then atomically commit the accepted
    /// plan only after all command buffers were recorded successfully.
    /// </summary>
    public sealed class RenderGraphResourceBindings
    {
        private readonly Guid _planOwnerId = Guid.NewGuid();
        private readonly Dictionary<RenderGraphAllocationIdentity, uint> _owners = new();
        private readonly Dictionary<RenderGraphAllocationIdentity, ImageLayout> _layouts = new();
        private RenderGraphResourcePlan _currentPlan;
        private ulong _nextGeneration;
        private ulong _synchronizationStateGeneration;
        private int _stalePlanRejectionCount;

        public RenderGraphResourceBindings()
        {
            _currentPlan = new RenderGraphResourcePlan(
                _planOwnerId,
                generation: 0,
                Array.Empty<RenderGraphConcreteResourceBinding>());
        }

        public ulong Generation => _currentPlan.Generation;
        public ulong SynchronizationStateGeneration => _synchronizationStateGeneration;
        public int StalePlanRejectionCount => _stalePlanRejectionCount;
        public int BindingCount => _currentPlan.BindingCount;
        public RenderGraphResourcePlan CurrentPlan => _currentPlan;

        public IReadOnlyList<RenderGraphConcreteResourceBinding> GetBindings(
            RenderGraphResourceId resource,
            int frameIndex = -1)
        {
            return _currentPlan.GetBindings(resource, frameIndex);
        }

        /// <summary>Resolves an explicitly selected physical history bank.</summary>
        public IReadOnlyList<RenderGraphConcreteResourceBinding> GetBindings(
            RenderGraphResourceId resource,
            int frameIndex,
            RenderGraphHistoryBindingSelection historyBinding)
        {
            return _currentPlan.GetBindings(resource, frameIndex, historyBinding);
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
            binding != null && binding.ResourcePlanGeneration == Generation;

        public ImageLayout GetCurrentLayout(RenderGraphConcreteResourceBinding binding)
        {
            if (binding == null)
                throw new ArgumentNullException(nameof(binding));
            if (binding.Kind != RenderGraphConcreteResourceKind.Image)
                return ImageLayout.Undefined;
            if (binding.LayoutProvider != null)
                return binding.LayoutProvider();
            return _layouts.TryGetValue(binding.AllocationIdentity, out ImageLayout layout)
                ? layout
                : binding.Layout;
        }

        internal RenderGraphResourcePlan CreatePlan(IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            if (bindings == null)
                throw new ArgumentNullException(nameof(bindings));

            ulong generation = checked(_nextGeneration + 1UL);
            RenderGraphResourcePlan plan = new(_planOwnerId, generation, bindings);
            _nextGeneration = generation;
            return plan;
        }

        internal void Activate(RenderGraphResourcePlan plan, bool resetState)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (!plan.BelongsTo(_planOwnerId))
                throw new InvalidOperationException("A concrete resource plan can only be activated by the binding catalog that created it.");

            _currentPlan = plan;
            _synchronizationStateGeneration = _synchronizationStateGeneration == ulong.MaxValue
                ? 1UL
                : _synchronizationStateGeneration + 1UL;
            if (!resetState)
                return;

            _owners.Clear();
            _layouts.Clear();
            _synchronizationStateGeneration = _synchronizationStateGeneration == ulong.MaxValue
                ? 1UL
                : _synchronizationStateGeneration + 1UL;
        }

        public void Replace(IEnumerable<RenderGraphConcreteResourceBinding> bindings)
        {
            if (bindings == null)
                throw new ArgumentNullException(nameof(bindings));

            RenderGraphResourcePlan plan = CreatePlan(bindings);
            Activate(plan, resetState: true);
        }

        public void Invalidate()
        {
            ulong generation = checked(_nextGeneration + 1UL);
            _nextGeneration = generation;
            _currentPlan = new RenderGraphResourcePlan(
                _planOwnerId,
                generation,
                Array.Empty<RenderGraphConcreteResourceBinding>());
            _owners.Clear();
            _layouts.Clear();
            _synchronizationStateGeneration = _synchronizationStateGeneration == ulong.MaxValue
                ? 1UL
                : _synchronizationStateGeneration + 1UL;
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
                    $"Cannot commit ownership for stale binding '{binding.Name}' (plan {binding.ResourcePlanGeneration}, current {Generation}).");
            }
            if (binding.SharingMode == SharingMode.Concurrent)
                return;
            if (!binding.PermittedQueueFamilies.Contains(ownerQueueFamily))
                throw new InvalidOperationException($"Queue family {ownerQueueFamily} is not permitted for binding '{binding.Name}'.");

            if (!_owners.TryGetValue(binding.AllocationIdentity, out uint priorOwner) ||
                priorOwner != ownerQueueFamily)
            {
                _owners[binding.AllocationIdentity] = ownerQueueFamily;
                _synchronizationStateGeneration = _synchronizationStateGeneration == ulong.MaxValue
                    ? 1UL
                    : _synchronizationStateGeneration + 1UL;
            }
        }

        public void CommitLayout(RenderGraphConcreteResourceBinding binding, ImageLayout layout)
        {
            if (binding == null)
                throw new ArgumentNullException(nameof(binding));
            if (!IsCurrent(binding))
            {
                RecordStalePlanRejection();
                throw new InvalidOperationException(
                    $"Cannot commit layout for stale binding '{binding.Name}' (plan {binding.ResourcePlanGeneration}, current {Generation}).");
            }
            if (binding.Kind != RenderGraphConcreteResourceKind.Image)
                throw new InvalidOperationException($"Buffer binding '{binding.Name}' cannot commit an image layout.");

            if (!_layouts.TryGetValue(binding.AllocationIdentity, out ImageLayout priorLayout) ||
                priorLayout != layout)
            {
                _layouts[binding.AllocationIdentity] = layout;
                _synchronizationStateGeneration = _synchronizationStateGeneration == ulong.MaxValue
                    ? 1UL
                    : _synchronizationStateGeneration + 1UL;
            }
        }
    }
}
