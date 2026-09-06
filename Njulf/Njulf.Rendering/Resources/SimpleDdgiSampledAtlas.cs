using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    internal readonly record struct SimpleDdgiSampledAtlasImageGraphBinding(
        string Name,
        Image Image,
        uint LayerCount,
        Func<ImageLayout> LayoutProvider,
        Action<ImageLayout> LayoutTracker);

    internal readonly record struct SimpleDdgiSampledAtlasGraphResourceSnapshot(
        ulong AllocationGeneration,
        SharingMode SharingMode,
        IReadOnlyList<SimpleDdgiSampledAtlasImageGraphBinding>? Irradiance,
        IReadOnlyList<SimpleDdgiSampledAtlasImageGraphBinding>? Visibility)
    {
        public bool IsComplete =>
            AllocationGeneration != 0UL &&
            Irradiance is { Count: > 0 } &&
            Visibility is { Count: > 0 } &&
            Irradiance.Count == Visibility.Count;
    }

    /// <summary>
    /// Optional sampled-image view of the canonical Simple DDGI SSBO atlases.
    ///
    /// The SSBO representation remains the writer and correctness reference while
    /// this resource is enabled for A/B captures.  Images are split into bounded
    /// 2D-array groups so devices with modest maxImageArrayLayers can still cover
    /// the full Simple-DDGI probe budget.  A probe always maps to one array layer,
    /// which prevents hardware bilinear filtering from ever crossing into another
    /// probe's octahedral tile.
    /// </summary>
    internal sealed unsafe class SimpleDdgiSampledAtlas : IDisposable
    {
        private const int MaxTextureGroups = BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups;
        // Kept in lockstep with ddgi_simple_publish_sampled.comp. The sampled
        // mirror is optional; devices that would need a wider storage-image
        // table retain the canonical SSBO path without CPU publication.
        internal const int MaxGpuPublishTextureGroups = 16;
        private const int PreferredLayersPerTexture = 2_048;
        // Avoid reallocating and idling the device for every small topology
        // adjustment while keeping the reserve below one MiB of atlas data.
        private const int CapacityGrowthQuantum = 256;
        private const ulong IrradianceTexelStride = 8;
        private const ulong VisibilityTexelStride = 4;
        // Canonical source strides, excluding the sampled image's border.
        internal const ulong IrradianceBytesPerProbe =
            (ulong)SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
            SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
            IrradianceTexelStride;
        internal const ulong VisibilityBytesPerProbe =
            (ulong)SimpleDdgiVolumeManager.VisibilityTexelsPerProbe *
            SimpleDdgiVolumeManager.VisibilityTexelsPerProbe *
            VisibilityTexelStride;
        private const int MaxRetiredAllocationGenerations = 16;

        private readonly VulkanContext _context;
        private readonly int _resolvedLayersPerTexture;
        private AtlasGroup[] _groups = Array.Empty<AtlasGroup>();
        private readonly List<RetiredAtlasAllocation> _retiredAllocations = new();
        private Sampler _sampler;
        private int _probeCapacity;
        private int _layersPerTexture;
        private ulong _allocationGeneration;
        private BindlessHeap? _publishedHeap;
        private ulong _publishedGeneration;
        private bool _requiresFullSync;
        private bool _releasePending;
        private ulong _pendingReleaseFenceValue;
        private SimpleDdgiSampledAtlasGraphResourceSnapshot
            _graphResourceSnapshot;
        private bool _disposed;

        public SimpleDdgiSampledAtlas(
            VulkanContext context,
            Action<RuntimeStallReason, string, Action> recordRuntimeStall)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _ = recordRuntimeStall ??
                throw new ArgumentNullException(nameof(recordRuntimeStall));
            // The physical-device limit is immutable for this context. Resolve it
            // once so the per-frame stable-capacity path never enters the driver.
            _resolvedLayersPerTexture = ResolveLayersPerTexture();
            ValidateFormatSupport();
            CreateSampler();
        }

        public bool IsReady => _groups.Length > 0 &&
            _groups[0].IrradianceImage.Handle != 0 &&
            _groups[0].VisibilityImage.Handle != 0;
        public int ProbeCapacity => _probeCapacity;
        public int LayersPerTexture => _layersPerTexture;
        public int GroupCount => _groups.Length;
        public bool RequiresFullSync => _requiresFullSync;
        public string LastFailureReason { get; private set; } = string.Empty;
        public ulong EstimatedImageBytes { get; private set; }
        /// <summary>Exact VMA allocation sizes, including device alignment/padding.</summary>
        public ulong AllocatedImageBytes { get; private set; }
        public ulong RetiredImageBytes { get; private set; }
        public int RetiredImageCount
        {
            get
            {
                int count = 0;
                foreach (RetiredAtlasAllocation retired in _retiredAllocations)
                    count = checked(count + retired.Groups.Length * 2);
                return count;
            }
        }
        public ulong AllocationGeneration => _allocationGeneration;

        public bool EnsureCapacity(int requiredProbeCount)
        {
            if (IsReady && RequiresCapacityTransition(requiredProbeCount))
            {
                throw new InvalidOperationException(
                    "A sampled-atlas resize requires renderer completion progress.");
            }

            return EnsureCapacity(
                requiredProbeCount,
                lastUseFrameFenceValue: 0UL,
                completedFrameFenceValue: 0UL,
                priorGenerationComplete: true);
        }

        internal bool EnsureCapacity(
            int requiredProbeCount,
            ulong lastUseFrameFenceValue,
            ulong completedFrameFenceValue,
            bool priorGenerationComplete,
            bool forceRecreate = false)
        {
            ThrowIfDisposed();
            // Fence completion protects submitted readers, but descriptor
            // replacement is published by the owner only after this call.
            _ = priorGenerationComplete;
            if (requiredProbeCount <= 0)
            {
                Release(lastUseFrameFenceValue, completedFrameFenceValue);
                return false;
            }

            int layersPerTexture = _resolvedLayersPerTexture;
            int maxProbeCapacity = checked(MaxTextureGroups * layersPerTexture);
            if (requiredProbeCount > maxProbeCapacity)
            {
                LastFailureReason =
                    $"sampled-atlas-needs-{requiredProbeCount}-probe-layers-exceeding-{maxProbeCapacity}";
                Release(lastUseFrameFenceValue, completedFrameFenceValue);
                return false;
            }

            int provisionedProbeCount = CalculateProvisionedProbeCapacity(requiredProbeCount, layersPerTexture);
            int requiredGroups = DivideRoundUp(provisionedProbeCount, layersPerTexture);
            if (requiredGroups > MaxGpuPublishTextureGroups)
            {
                LastFailureReason =
                    $"sampled-atlas-needs-{requiredGroups}-gpu-publish-groups-exceeding-{MaxGpuPublishTextureGroups}";
                Release(lastUseFrameFenceValue, completedFrameFenceValue);
                return false;
            }
            if (requiredGroups > MaxTextureGroups)
            {
                LastFailureReason =
                    $"sampled-atlas-needs-{requiredGroups}-texture-groups-exceeding-{MaxTextureGroups}";
                Release(lastUseFrameFenceValue, completedFrameFenceValue);
                return false;
            }

            if (IsReady &&
                !forceRecreate &&
                !RequiresStableCapacityReallocation(
                    _probeCapacity,
                    provisionedProbeCount,
                    _layersPerTexture,
                    layersPerTexture))
            {
                _releasePending = false;
                _pendingReleaseFenceValue = 0UL;
                return true;
            }

            bool hadPriorAllocation = IsReady;
            if (hadPriorAllocation &&
                _retiredAllocations.Count >= MaxRetiredAllocationGenerations)
            {
                LastFailureReason = "sampled-atlas-retirement-capacity-exhausted";
                return false;
            }

            // Allocate the replacement before detaching the old generation.
            // Even after submitted readers complete, both the bindless and GPU
            // publication descriptor sets still name the old views until the
            // owner republishes the new generation after this method returns.
            var groups = new AtlasGroup[requiredGroups];
            try
            {
                for (int groupIndex = 0; groupIndex < requiredGroups; groupIndex++)
                {
                    int firstProbe = checked(groupIndex * layersPerTexture);
                    int layerCount = Math.Min(layersPerTexture, provisionedProbeCount - firstProbe);
                    if (!TryCreateGroup(layerCount, out AtlasGroup group))
                    {
                        LastFailureReason = "sampled-atlas-memory-budget-exhausted";
                        DestroyGroups(groups);
                        return false;
                    }

                    groups[groupIndex] = group;
                }
            }
            catch
            {
                DestroyGroups(groups);
                throw;
            }

            if (hadPriorAllocation)
            {
                if (!RetireCurrentAllocation(
                        lastUseFrameFenceValue,
                        completedFrameFenceValue,
                        deferDestructionUntilDescriptorReplacement: true))
                {
                    DestroyGroups(groups);
                    LastFailureReason = "sampled-atlas-retirement-admission-failed";
                    return false;
                }
            }

            _groups = groups;
            _probeCapacity = provisionedProbeCount;
            _layersPerTexture = layersPerTexture;
            _requiresFullSync = true;
            _releasePending = false;
            _pendingReleaseFenceValue = 0UL;
            LastFailureReason = string.Empty;
            EstimatedImageBytes = CalculateEstimatedImageBytes(groups);
            AllocatedImageBytes = CalculateAllocatedImageBytes(groups);
            _allocationGeneration++;
            if (_allocationGeneration == 0)
                _allocationGeneration = 1;
            RebuildGraphResourceSnapshot();
            return true;
        }

        internal bool TryGetGraphResourceSnapshot(
            out SimpleDdgiSampledAtlasGraphResourceSnapshot snapshot)
        {
            snapshot = _graphResourceSnapshot;
            return IsReady && snapshot.IsComplete;
        }

        internal static bool RequiresStableCapacityReallocation(
            int provisionedProbeCapacity,
            int requiredProbeCapacity,
            int provisionedLayersPerTexture,
            int requiredLayersPerTexture) =>
            provisionedProbeCapacity != requiredProbeCapacity ||
            provisionedLayersPerTexture != requiredLayersPerTexture;

        internal bool RequiresCapacityTransition(int requiredProbeCount)
        {
            if (requiredProbeCount <= 0)
                return IsReady;

            int provisionedProbeCount = CalculateProvisionedProbeCapacity(
                requiredProbeCount,
                _resolvedLayersPerTexture);
            return !IsReady || RequiresStableCapacityReallocation(
                _probeCapacity,
                provisionedProbeCount,
                _layersPerTexture,
                _resolvedLayersPerTexture);
        }

        internal bool CanRetireCurrentAllocation(
            ulong lastUseFrameFenceValue,
            ulong completedFrameFenceValue) =>
            !IsReady ||
            lastUseFrameFenceValue == 0UL ||
            completedFrameFenceValue >= lastUseFrameFenceValue ||
            _retiredAllocations.Count < MaxRetiredAllocationGenerations;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));
            if (!IsReady)
                return;
            if (ReferenceEquals(_publishedHeap, bindlessHeap) &&
                _publishedGeneration == _allocationGeneration)
                return;

            // The sampled publication shader is capped at sixteen groups. Fill
            // the entire reachable bindless range and alias unused slots to
            // group zero so shrinking an atlas generation never leaves a slot
            // pointing at a retired image view.
            AtlasGroup fallback = _groups[0];
            for (int groupIndex = 0;
                 groupIndex < MaxGpuPublishTextureGroups;
                 groupIndex++)
            {
                AtlasGroup group = groupIndex < _groups.Length
                    ? _groups[groupIndex]
                    : fallback;
                bindlessHeap.RegisterTexture(
                    BindlessIndex.SimpleDdgiSampledIrradianceTextureBase + groupIndex,
                    group.IrradianceView,
                    _sampler,
                    ImageLayout.ShaderReadOnlyOptimal);
                bindlessHeap.RegisterTexture(
                    BindlessIndex.SimpleDdgiSampledVisibilityTextureBase + groupIndex,
                    group.VisibilityView,
                    _sampler,
                    ImageLayout.ShaderReadOnlyOptimal);
            }

            _publishedHeap = bindlessHeap;
            _publishedGeneration = _allocationGeneration;
        }

        public void MarkFullSyncRequired()
        {
            if (IsReady)
                _requiresFullSync = true;
        }

        /// <summary>
        /// Binds every sampled-atlas image as a storage-image publication target.
        /// Unused descriptor slots alias group zero so the dynamically indexed
        /// shader table is fully initialized without partially-bound descriptors.
        /// </summary>
        public void UpdateGpuPublishDescriptors(DescriptorSet descriptorSet)
        {
            if (!IsReady || descriptorSet.Handle == 0)
                return;

            DescriptorImageInfo* irradiance = stackalloc DescriptorImageInfo[MaxGpuPublishTextureGroups];
            DescriptorImageInfo* visibility = stackalloc DescriptorImageInfo[MaxGpuPublishTextureGroups];
            AtlasGroup fallback = _groups[0];
            for (int index = 0; index < MaxGpuPublishTextureGroups; index++)
            {
                AtlasGroup group = index < _groups.Length ? _groups[index] : fallback;
                irradiance[index] = new DescriptorImageInfo
                {
                    ImageView = group.IrradianceView,
                    ImageLayout = ImageLayout.General
                };
                visibility[index] = new DescriptorImageInfo
                {
                    ImageView = group.VisibilityView,
                    ImageLayout = ImageLayout.General
                };
            }

            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 0,
                DescriptorCount = MaxGpuPublishTextureGroups,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = irradiance
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 1,
                DescriptorCount = MaxGpuPublishTextureGroups,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = visibility
            };
            _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        }

        public void BeginGpuPublication(CommandBuffer commandBuffer)
        {
            if (!IsReady)
                return;
            TransitionImages(
                commandBuffer,
                ImageLayout.General,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
        }

        public void EndGpuPublication(CommandBuffer commandBuffer)
        {
            if (!IsReady)
                return;
            TransitionImagesToShaderRead(commandBuffer);
            _requiresFullSync = false;
        }

        public void CopyRanges(
            CommandBuffer commandBuffer,
            VkBuffer irradianceBuffer,
            ulong irradianceBufferBytes,
            VkBuffer visibilityBuffer,
            ulong visibilityBufferBytes,
            SimpleDdgiSampledAtlasLayout layout)
        {
            ArgumentNullException.ThrowIfNull(layout);
            if (!IsReady || layout.AdmittedProbeCount <= 0)
                return;

            ValidateCopyLayout(
                layout,
                irradianceBufferBytes,
                visibilityBufferBytes);
            TransitionSourceBuffers(
                commandBuffer,
                irradianceBuffer,
                irradianceBufferBytes,
                visibilityBuffer,
                visibilityBufferBytes,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.MemoryWriteBit);
            TransitionImagesToTransferDestination(commandBuffer);

            foreach (SimpleDdgiSampledAtlasRange range in layout.Ranges)
            {
                CopyContiguousRange(
                    commandBuffer,
                    irradianceBuffer,
                    range,
                    irradiance: true);
                CopyContiguousRange(
                    commandBuffer,
                    visibilityBuffer,
                    range,
                    irradiance: false);
            }

            TransitionImagesToShaderRead(commandBuffer);
            TransitionSourceBuffersForNextShaderUse(
                commandBuffer,
                irradianceBuffer,
                irradianceBufferBytes,
                visibilityBuffer,
                visibilityBufferBytes);
            _requiresFullSync = false;
        }

        public void Release()
        {
            if (IsReady)
            {
                throw new InvalidOperationException(
                    "Releasing a live sampled atlas requires renderer completion progress.");
            }
        }

        internal bool Release(
            ulong lastUseFrameFenceValue,
            ulong completedFrameFenceValue)
        {
            if (!IsReady)
            {
                _releasePending = false;
                _pendingReleaseFenceValue = 0UL;
                return true;
            }

            bool released = RetireCurrentAllocation(
                lastUseFrameFenceValue,
                completedFrameFenceValue);
            if (released)
            {
                LastFailureReason = string.Empty;
                _releasePending = false;
                _pendingReleaseFenceValue = 0UL;
            }
            else
            {
                // Retirement capacity is transient. Keep the exact completion
                // token and finish the requested release as soon as an older
                // generation drains, even if no new capacity transition occurs.
                _releasePending = true;
                _pendingReleaseFenceValue = lastUseFrameFenceValue;
            }
            return released;
        }

        /// <summary>
        /// Releases image capacity after the owning renderer has established
        /// device idle during terminal shutdown.
        /// </summary>
        internal void ReleaseAfterDeviceIdle()
        {
            DestroyImageResources();
            DestroyRetiredAllocationsAfterDeviceIdle();
            _releasePending = false;
            _pendingReleaseFenceValue = 0UL;
            LastFailureReason = string.Empty;
        }

        internal void CollectRetired(ulong completedFrameFenceValue)
        {
            for (int index = _retiredAllocations.Count - 1; index >= 0; index--)
            {
                RetiredAtlasAllocation retired = _retiredAllocations[index];
                if (completedFrameFenceValue < retired.Completion.Value)
                    continue;

                DestroyGroups(retired.Groups);
                RetiredImageBytes = retired.Bytes >= RetiredImageBytes
                    ? 0UL
                    : RetiredImageBytes - retired.Bytes;
                _retiredAllocations.RemoveAt(index);
            }

            if (_releasePending && IsReady &&
                RetireCurrentAllocation(
                    _pendingReleaseFenceValue,
                    completedFrameFenceValue))
            {
                _releasePending = false;
                _pendingReleaseFenceValue = 0UL;
            }
        }

        private bool RetireCurrentAllocation(
            ulong lastUseFrameFenceValue,
            ulong completedFrameFenceValue,
            bool deferDestructionUntilDescriptorReplacement = false)
        {
            if (!IsReady)
                return true;

            if (!deferDestructionUntilDescriptorReplacement &&
                (lastUseFrameFenceValue == 0UL ||
                 completedFrameFenceValue >= lastUseFrameFenceValue))
            {
                DestroyImageResources();
                return true;
            }

            if (_retiredAllocations.Count >= MaxRetiredAllocationGenerations)
                return false;

            AtlasGroup[] groups = _groups;
            ulong bytes = AllocatedImageBytes;
            _graphResourceSnapshot = default;
            _groups = Array.Empty<AtlasGroup>();
            _probeCapacity = 0;
            _layersPerTexture = 0;
            _requiresFullSync = false;
            EstimatedImageBytes = 0UL;
            AllocatedImageBytes = 0UL;
            ulong retirementFenceValue = lastUseFrameFenceValue;
            if (deferDestructionUntilDescriptorReplacement)
            {
                retirementFenceValue =
                    ResolveDescriptorReplacementRetirementFence(
                        retirementFenceValue,
                        completedFrameFenceValue);
            }
            _retiredAllocations.Add(new RetiredAtlasAllocation(
                groups,
                bytes,
                GpuCompletionToken.ForFrameFence(retirementFenceValue)));
            RetiredImageBytes = checked(RetiredImageBytes + bytes);
            return true;
        }

        internal static ulong ResolveDescriptorReplacementRetirementFence(
            ulong lastUseFrameFenceValue,
            ulong completedFrameFenceValue)
        {
            if (lastUseFrameFenceValue > completedFrameFenceValue)
                return lastUseFrameFenceValue;
            return completedFrameFenceValue == ulong.MaxValue
                ? ulong.MaxValue
                : completedFrameFenceValue + 1UL;
        }

        private void DestroyRetiredAllocationsAfterDeviceIdle()
        {
            foreach (RetiredAtlasAllocation retired in _retiredAllocations)
                DestroyGroups(retired.Groups);
            _retiredAllocations.Clear();
            RetiredImageBytes = 0UL;
        }

        private bool TryCreateGroup(int layerCount, out AtlasGroup group)
        {
            group = new AtlasGroup(layerCount);
            if (!TryCreateImage(
                    SimpleDdgiSampledAtlasLayoutCompiler.IrradianceImageTexels,
                    layerCount,
                    Format.R16G16B16A16Sfloat,
                    "Simple DDGI Sampled Irradiance",
                    out group.IrradianceImage,
                    out group.IrradianceAllocation,
                    out group.IrradianceView,
                    out group.IrradianceAllocationBytes))
            {
                DestroyGroup(group);
                return false;
            }

            if (!TryCreateImage(
                    SimpleDdgiSampledAtlasLayoutCompiler.VisibilityImageTexels,
                    layerCount,
                    Format.R16G16Sfloat,
                    "Simple DDGI Sampled Visibility",
                    out group.VisibilityImage,
                    out group.VisibilityAllocation,
                    out group.VisibilityView,
                    out group.VisibilityAllocationBytes))
            {
                DestroyGroup(group);
                return false;
            }

            return true;
        }

        private bool TryCreateImage(
            int texelsPerProbe,
            int layerCount,
            Format format,
            string debugName,
            out Image image,
            out GpuAllocator.Allocation* allocation,
            out ImageView view,
            out ulong allocationBytes)
        {
            image = default;
            allocation = null;
            view = default;
            allocationBytes = 0UL;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D
                {
                    Width = checked((uint)texelsPerProbe),
                    Height = checked((uint)texelsPerProbe),
                    Depth = 1
                },
                MipLevels = 1,
                ArrayLayers = checked((uint)layerCount),
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit,
                InitialLayout = ImageLayout.Undefined
            };
            uint* queueFamilies = stackalloc uint[2]
            {
                _context.GraphicsQueueFamilyIndex,
                _context.ComputeQueueFamilyIndex
            };
            if (_context.GraphicsQueueFamilyIndex != _context.ComputeQueueFamilyIndex)
            {
                imageInfo.SharingMode = SharingMode.Concurrent;
                imageInfo.QueueFamilyIndexCount = 2;
                imageInfo.PQueueFamilyIndices = queueFamilies;
            }
            else
            {
                imageInfo.SharingMode = SharingMode.Exclusive;
            }
            var allocationInfo = new GpuAllocator.AllocationCreateInfo
            {
                Usage = GpuAllocator.MemoryUsage.AutoPreferDevice,
                Flags = _context.MemoryBudgetExtensionEnabled
                    ? GpuAllocator.AllocationCreateFlags.WithinBudgetBit
                    : default
            };

            Image createdImage = default;
            GpuAllocator.Allocation* createdAllocation = null;
            GpuAllocator.AllocationInfo createdAllocationInfo;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator,
                &imageInfo,
                &allocationInfo,
                &createdImage,
                &createdAllocation,
                &createdAllocationInfo);
            if (result != Result.Success)
            {
                if (_context.IsMemoryBudgetExceeded(result))
                    return false;
                throw new VulkanException($"Failed to create {debugName} sampled atlas image", result);
            }

            image = createdImage;
            allocation = createdAllocation;
            allocationBytes = checked((ulong)createdAllocationInfo.Size);

            try
            {
                _context.SetDebugName(image.Handle, ObjectType.Image, $"{debugName} {texelsPerProbe}x{texelsPerProbe}x{layerCount}");
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2DArray,
                    Format = format,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = checked((uint)layerCount)
                    }
                };
                result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out view);
                if (result != Result.Success)
                    throw new VulkanException($"Failed to create {debugName} sampled atlas view", result);
                _context.SetDebugName(view.Handle, ObjectType.ImageView, $"{debugName} Array View");
                return true;
            }
            catch
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
                view = default;
                GpuAllocator.Apis.DestroyImage(_context.Allocator, image, allocation);
                image = default;
                allocation = null;
                allocationBytes = 0UL;
                throw;
            }
        }

        // One interior region plus one region for each border texel. Each
        // region spans the whole contiguous layer chunk, not one CPU call per
        // probe. Explicit source pitches retain the unpadded canonical stride.
        internal static int BuildCopyRegions(
            Span<BufferImageCopy> regions,
            int texelsPerProbe,
            ulong texelStride,
            int firstSourceProbe,
            int firstLayer,
            int layerCount)
        {
            int n = texelsPerProbe;
            ulong sourceOffset = checked((ulong)firstSourceProbe * (ulong)n * (ulong)n * texelStride);
            var copy = new BufferImageCopy
            {
                BufferOffset = sourceOffset,
                BufferRowLength = checked((uint)n),
                BufferImageHeight = checked((uint)n),
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = checked((uint)firstLayer),
                    LayerCount = checked((uint)layerCount)
                },
                ImageOffset = new Offset3D(1, 1, 0),
                ImageExtent = new Extent3D(checked((uint)n), checked((uint)n), 1)
            };
            regions[0] = copy;
            copy.ImageExtent = new Extent3D(1, 1, 1);
            int count = 1;
            for (int y = 0; y < n + 2; y++)
            for (int x = 0; x < n + 2; x++)
            {
                if (x > 0 && x <= n && y > 0 && y <= n)
                    continue;
                // Same X-then-Y fold as SimpleDdgiMirrorOctTexelIndex in GLSL;
                // applying both folds gives the opposite interior corner.
                int sx = x - 1, sy = y - 1;
                if (sx < 0 || sx >= n)
                {
                    sx = sx < 0 ? -sx - 1 : 2 * n - sx - 1;
                    sy = n - 1 - sy;
                }
                if (sy < 0 || sy >= n)
                {
                    sy = sy < 0 ? -sy - 1 : 2 * n - sy - 1;
                    sx = n - 1 - sx;
                }
                copy.BufferOffset = checked(sourceOffset + (ulong)(sy * n + sx) * texelStride);
                copy.ImageOffset = new Offset3D(x, y, 0);
                regions[count++] = copy;
            }
            return count;
        }

        private void CopyContiguousRange(
            CommandBuffer commandBuffer,
            VkBuffer source,
            in SimpleDdgiSampledAtlasRange range,
            bool irradiance)
        {
            int texelsPerProbe = irradiance
                ? SimpleDdgiVolumeManager.IrradianceTexelsPerProbe
                : SimpleDdgiVolumeManager.VisibilityTexelsPerProbe;
            ulong texelStride = irradiance ? IrradianceTexelStride : VisibilityTexelStride;
            int copyCapacity = 1 + 4 * texelsPerProbe + 4;
            BufferImageCopy* copies = stackalloc BufferImageCopy[copyCapacity];
            int sourceProbe = range.CanonicalFirstProbe;
            int compactLayer = range.CompactFirstLayer;
            int remaining = range.ProbeCount;
            while (remaining > 0)
            {
                int groupIndex = compactLayer / _layersPerTexture;
                int groupLayer = compactLayer - groupIndex * _layersPerTexture;
                AtlasGroup group = _groups[groupIndex];
                int layerCount = Math.Min(remaining, group.LayerCount - groupLayer);
                int copyCount = BuildCopyRegions(
                    new Span<BufferImageCopy>(copies, copyCapacity),
                    texelsPerProbe, texelStride, sourceProbe, groupLayer, layerCount);
                _context.Api.CmdCopyBufferToImage(
                    commandBuffer,
                    source,
                    irradiance ? group.IrradianceImage : group.VisibilityImage,
                    ImageLayout.TransferDstOptimal,
                    checked((uint)copyCount),
                    copies);
                sourceProbe = checked(sourceProbe + layerCount);
                compactLayer = checked(compactLayer + layerCount);
                remaining -= layerCount;
            }
        }

        private void ValidateCopyLayout(
            SimpleDdgiSampledAtlasLayout layout,
            ulong irradianceBufferBytes,
            ulong visibilityBufferBytes)
        {
            if (layout.AdmittedProbeCount > _probeCapacity ||
                layout.ProvisionedProbeCount > _probeCapacity)
            {
                throw new InvalidOperationException(
                    $"Sampled-atlas layout requires {layout.ProvisionedProbeCount} layers, but only {_probeCapacity} are provisioned.");
            }

            int expectedCompactLayer = 0;
            foreach (SimpleDdgiSampledAtlasRange range in layout.Ranges)
            {
                if (range.CanonicalFirstProbe < 0 || range.ProbeCount < 0 ||
                    range.CompactFirstLayer != expectedCompactLayer)
                {
                    throw new InvalidOperationException(
                        "Sampled-atlas compact ranges must be non-negative, contiguous, and ordered.");
                }

                int canonicalEnd = checked(range.CanonicalFirstProbe + range.ProbeCount);
                int compactEnd = checked(range.CompactFirstLayer + range.ProbeCount);
                if (compactEnd > _probeCapacity ||
                    checked((ulong)canonicalEnd * IrradianceBytesPerProbe) > irradianceBufferBytes ||
                    checked((ulong)canonicalEnd * VisibilityBytesPerProbe) > visibilityBufferBytes)
                {
                    throw new InvalidOperationException(
                        $"Sampled-atlas range '{range.Identity}' exceeds its canonical source or compact destination allocation.");
                }

                expectedCompactLayer = compactEnd;
            }

            if (expectedCompactLayer != layout.AdmittedProbeCount)
            {
                throw new InvalidOperationException(
                    $"Sampled-atlas ranges cover {expectedCompactLayer} layers, but the layout declares {layout.AdmittedProbeCount}.");
            }
        }

        private void TransitionSourceBuffers(
            CommandBuffer commandBuffer,
            VkBuffer irradianceBuffer,
            ulong irradianceBufferBytes,
            VkBuffer visibilityBuffer,
            ulong visibilityBufferBytes,
            PipelineStageFlags2 sourceStage,
            AccessFlags2 sourceAccess)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
            barriers[0] = CreateBufferBarrier(
                irradianceBuffer,
                irradianceBufferBytes,
                sourceStage,
                sourceAccess,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit);
            barriers[1] = CreateBufferBarrier(
                visibilityBuffer,
                visibilityBufferBytes,
                sourceStage,
                sourceAccess,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit);
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 2,
                PBufferMemoryBarriers = barriers
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void TransitionSourceBuffersForNextShaderUse(
            CommandBuffer commandBuffer,
            VkBuffer irradianceBuffer,
            ulong irradianceBufferBytes,
            VkBuffer visibilityBuffer,
            ulong visibilityBufferBytes)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
            barriers[0] = CreateBufferBarrier(
                irradianceBuffer,
                irradianceBufferBytes,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            barriers[1] = CreateBufferBarrier(
                visibilityBuffer,
                visibilityBufferBytes,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 2,
                PBufferMemoryBarriers = barriers
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private static BufferMemoryBarrier2 CreateBufferBarrier(
            VkBuffer buffer,
            ulong size,
            PipelineStageFlags2 sourceStage,
            AccessFlags2 sourceAccess,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = sourceStage,
                SrcAccessMask = sourceAccess,
                DstStageMask = destinationStage,
                DstAccessMask = destinationAccess,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = 0,
                Size = Math.Max(size, 1UL)
            };
        }

        private void TransitionImagesToTransferDestination(CommandBuffer commandBuffer)
        {
            TransitionImages(
                commandBuffer,
                ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit);
        }

        private void TransitionImagesToShaderRead(CommandBuffer commandBuffer)
        {
            TransitionImages(
                commandBuffer,
                ImageLayout.ShaderReadOnlyOptimal,
                // Publication can run on a compute-only queue while the atlas is
                // later sampled by graphics. ALL_COMMANDS is valid on both queue
                // types and lets the semaphore dependency carry visibility to
                // whichever shader stages consume the image next.
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.ShaderSampledReadBit);
        }

        private void TransitionImages(
            CommandBuffer commandBuffer,
            ImageLayout destinationLayout,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            ImageMemoryBarrier2* barriers = stackalloc ImageMemoryBarrier2[checked(_groups.Length * 2)];
            uint barrierCount = 0;
            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                AtlasGroup group = _groups[groupIndex];
                barriers[barrierCount++] = CreateImageBarrier(
                    group.IrradianceImage,
                    group.LayerCount,
                    group.IrradianceLayout,
                    destinationLayout,
                    destinationStage,
                    destinationAccess);
                barriers[barrierCount++] = CreateImageBarrier(
                    group.VisibilityImage,
                    group.LayerCount,
                    group.VisibilityLayout,
                    destinationLayout,
                    destinationStage,
                    destinationAccess);
                group.IrradianceLayout = destinationLayout;
                group.VisibilityLayout = destinationLayout;
            }

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = barrierCount,
                PImageMemoryBarriers = barriers
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private static ImageMemoryBarrier2 CreateImageBarrier(
            Image image,
            int layerCount,
            ImageLayout sourceLayout,
            ImageLayout destinationLayout,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            (PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess) = sourceLayout switch
            {
                ImageLayout.Undefined => (PipelineStageFlags2.None, AccessFlags2.None),
                ImageLayout.TransferDstOptimal => (PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit),
                ImageLayout.General =>
                    (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
                ImageLayout.ShaderReadOnlyOptimal =>
                    (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit),
                _ => (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit)
            };
            return new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = sourceStage,
                SrcAccessMask = sourceAccess,
                DstStageMask = destinationStage,
                DstAccessMask = destinationAccess,
                OldLayout = sourceLayout,
                NewLayout = destinationLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = checked((uint)layerCount)
                }
            };
        }

        private int ResolveLayersPerTexture()
        {
            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            return CalculateLayersPerTexture(properties.Limits.MaxImageArrayLayers);
        }

        private void ValidateFormatSupport()
        {
            ValidateFormatSupport(
                Format.R16G16B16A16Sfloat,
                "R16G16B16A16 sampled DDGI irradiance atlas");
            ValidateFormatSupport(
                Format.R16G16Sfloat,
                "R16G16 sampled DDGI visibility atlas");

            PhysicalDeviceProperties deviceProperties = default;
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &deviceProperties);
            uint requiredStorageImages = 2u * MaxGpuPublishTextureGroups;
            if (deviceProperties.Limits.MaxPerStageDescriptorStorageImages < requiredStorageImages)
            {
                throw new VulkanException(
                    $"Simple DDGI GPU publication requires {requiredStorageImages} per-stage storage-image descriptors.");
            }
        }

        private void ValidateFormatSupport(Format format, string label)
        {
            FormatProperties properties = default;
            _context.Api.GetPhysicalDeviceFormatProperties(
                _context.PhysicalDevice,
                format,
                &properties);
            const FormatFeatureFlags required =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.SampledImageFilterLinearBit |
                FormatFeatureFlags.StorageImageBit |
                FormatFeatureFlags.TransferDstBit;
            if ((properties.OptimalTilingFeatures & required) != required)
            {
                throw new VulkanException(
                    $"{label} requires optimal-tiling linear filtered sampling and storage-image support.");
            }
        }

        private void CreateSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MinLod = 0.0f,
                MaxLod = 0.0f,
                MaxAnisotropy = 1.0f,
                BorderColor = BorderColor.FloatTransparentBlack
            };
            Result result = _context.Api.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI sampled atlas sampler", result);
            _context.SetDebugName(_sampler.Handle, ObjectType.Sampler, "Simple DDGI Sampled Atlas Linear Clamp Sampler");
        }

        private void DestroyImageResources()
        {
            _graphResourceSnapshot = default;
            DestroyGroups(_groups);
            _groups = Array.Empty<AtlasGroup>();
            _probeCapacity = 0;
            _layersPerTexture = 0;
            _requiresFullSync = false;
            EstimatedImageBytes = 0;
            AllocatedImageBytes = 0;
        }

        private void RebuildGraphResourceSnapshot()
        {
            if (!IsReady || _allocationGeneration == 0UL)
            {
                _graphResourceSnapshot = default;
                return;
            }

            var irradiance =
                new SimpleDdgiSampledAtlasImageGraphBinding[_groups.Length];
            var visibility =
                new SimpleDdgiSampledAtlasImageGraphBinding[_groups.Length];
            for (int groupIndex = 0;
                 groupIndex < _groups.Length;
                 groupIndex++)
            {
                AtlasGroup group = _groups[groupIndex];
                int capturedIndex = groupIndex;
                irradiance[groupIndex] =
                    new SimpleDdgiSampledAtlasImageGraphBinding(
                        $"Simple DDGI sampled irradiance group {capturedIndex}",
                        group.IrradianceImage,
                        checked((uint)group.LayerCount),
                        () => group.IrradianceLayout,
                        layout => group.IrradianceLayout = layout);
                visibility[groupIndex] =
                    new SimpleDdgiSampledAtlasImageGraphBinding(
                        $"Simple DDGI sampled visibility group {capturedIndex}",
                        group.VisibilityImage,
                        checked((uint)group.LayerCount),
                        () => group.VisibilityLayout,
                        layout => group.VisibilityLayout = layout);
            }

            SharingMode sharingMode =
                _context.GraphicsQueueFamilyIndex ==
                    _context.ComputeQueueFamilyIndex
                    ? SharingMode.Exclusive
                    : SharingMode.Concurrent;
            _graphResourceSnapshot =
                new SimpleDdgiSampledAtlasGraphResourceSnapshot(
                    _allocationGeneration,
                    sharingMode,
                    irradiance,
                    visibility);
        }

        private void DestroyGroups(AtlasGroup[] groups)
        {
            foreach (AtlasGroup group in groups)
                DestroyGroup(group);
        }

        private void DestroyGroup(AtlasGroup group)
        {
            if (group == null)
                return;
            if (group.IrradianceView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, group.IrradianceView, null);
            if (group.VisibilityView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, group.VisibilityView, null);
            if (group.IrradianceAllocation != null)
                GpuAllocator.Apis.DestroyImage(_context.Allocator, group.IrradianceImage, group.IrradianceAllocation);
            if (group.VisibilityAllocation != null)
                GpuAllocator.Apis.DestroyImage(_context.Allocator, group.VisibilityImage, group.VisibilityAllocation);
            group.IrradianceImage = default;
            group.VisibilityImage = default;
            group.IrradianceView = default;
            group.VisibilityView = default;
            group.IrradianceAllocation = null;
            group.VisibilityAllocation = null;
            group.IrradianceLayout = ImageLayout.Undefined;
            group.VisibilityLayout = ImageLayout.Undefined;
        }

        private static ulong CalculateEstimatedImageBytes(AtlasGroup[] groups)
        {
            ulong bytes = 0;
            foreach (AtlasGroup group in groups)
            {
                if (group == null)
                    continue;
                bytes = checked(bytes + (ulong)group.LayerCount * SimpleDdgiSampledAtlasLayoutCompiler.IrradianceBytesPerProbe);
                bytes = checked(bytes + (ulong)group.LayerCount * SimpleDdgiSampledAtlasLayoutCompiler.VisibilityBytesPerProbe);
            }

            return bytes;
        }

        private static ulong CalculateAllocatedImageBytes(AtlasGroup[] groups)
        {
            ulong bytes = 0UL;
            foreach (AtlasGroup group in groups)
            {
                if (group == null)
                    continue;
                bytes = checked(bytes + group.IrradianceAllocationBytes);
                bytes = checked(bytes + group.VisibilityAllocationBytes);
            }
            return bytes;
        }

        private static int DivideRoundUp(int numerator, int denominator) =>
            checked((numerator + denominator - 1) / denominator);

        internal static int CalculateProvisionedProbeCapacity(int requiredProbeCount, int layersPerTexture)
        {
            if (requiredProbeCount <= 0)
                return 0;
            if (layersPerTexture <= 0)
                throw new ArgumentOutOfRangeException(nameof(layersPerTexture));

            int quantum = Math.Min(CapacityGrowthQuantum, layersPerTexture);
            int rounded = checked(DivideRoundUp(requiredProbeCount, quantum) * quantum);
            return Math.Min(rounded, checked(MaxTextureGroups * layersPerTexture));
        }

        /// <summary>
        /// Resolves the common array-layer limit used by both allocation and
        /// manager-side memory admission. Keeping the calculation shared avoids
        /// accepting an image path that later needs a larger rounded allocation.
        /// </summary>
        internal static int CalculateLayersPerTexture(uint maxImageArrayLayers)
        {
            int deviceLimit = checked((int)maxImageArrayLayers);
            return Math.Max(1, Math.Min(PreferredLayersPerTexture, deviceLimit));
        }

        /// <summary>
        /// Returns the payload bytes for the RGBA16F irradiance and RG16F
        /// visibility image atlases at a fixed
        /// probe capacity. Allocation overhead is still enforced by VMA's
        /// WithinBudget admission at creation time.
        /// </summary>
        internal static ulong CalculateEstimatedImageBytesForProbeCapacity(int probeCapacity)
        {
            if (probeCapacity <= 0)
                return 0;

            return checked((ulong)probeCapacity *
                (SimpleDdgiSampledAtlasLayoutCompiler.IrradianceBytesPerProbe +
                    SimpleDdgiSampledAtlasLayoutCompiler.VisibilityBytesPerProbe));
        }

        internal static int CalculateSafeCopyProbeCount(
            int requestedProbeCount,
            int sampledProbeCapacity,
            ulong irradianceBufferBytes,
            ulong visibilityBufferBytes)
        {
            if (requestedProbeCount <= 0 || sampledProbeCapacity <= 0)
                return 0;

            ulong irradianceCapacity = irradianceBufferBytes /
                IrradianceBytesPerProbe;
            ulong visibilityCapacity = visibilityBufferBytes /
                VisibilityBytesPerProbe;
            ulong sourceCapacity = Math.Min(
                irradianceCapacity,
                visibilityCapacity);
            ulong bounded = Math.Min(
                Math.Min(
                    checked((ulong)requestedProbeCount),
                    checked((ulong)sampledProbeCapacity)),
                sourceCapacity);
            return checked((int)bounded);
        }

        internal static bool TryResolveProbeLayer(
            int probeIndex,
            int layersPerTexture,
            int groupCount,
            out int groupIndex,
            out int layerIndex)
        {
            groupIndex = 0;
            layerIndex = 0;
            if (probeIndex < 0 || layersPerTexture <= 0 || groupCount <= 0)
                return false;

            groupIndex = probeIndex / layersPerTexture;
            if (groupIndex >= groupCount)
            {
                groupIndex = 0;
                return false;
            }

            layerIndex = probeIndex - groupIndex * layersPerTexture;
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SimpleDdgiSampledAtlas));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DestroyImageResources();
            DestroyRetiredAllocationsAfterDeviceIdle();
            if (_sampler.Handle != 0)
                _context.Api.DestroySampler(_context.Device, _sampler, null);
            _sampler = default;
            GC.SuppressFinalize(this);
        }

        private readonly record struct RetiredAtlasAllocation(
            AtlasGroup[] Groups,
            ulong Bytes,
            GpuCompletionToken Completion);

        private sealed class AtlasGroup
        {
            public AtlasGroup(int layerCount)
            {
                LayerCount = layerCount;
            }

            public int LayerCount { get; }
            public GpuAllocator.Allocation* IrradianceAllocation;
            public GpuAllocator.Allocation* VisibilityAllocation;
            public Image IrradianceImage;
            public Image VisibilityImage;
            public ImageView IrradianceView;
            public ImageView VisibilityView;
            public ImageLayout IrradianceLayout;
            public ImageLayout VisibilityLayout;
            public ulong IrradianceAllocationBytes;
            public ulong VisibilityAllocationBytes;
        }
    }
}
