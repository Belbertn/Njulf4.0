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
        private const ulong AtlasTexelStride = 8;
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
        public ulong RetiredImageBytes { get; private set; }
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
            bool priorGenerationComplete)
        {
            ThrowIfDisposed();
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
                !RequiresStableCapacityReallocation(
                    _probeCapacity,
                    provisionedProbeCount,
                    _layersPerTexture,
                    layersPerTexture))
            {
                return true;
            }

            bool hadPriorAllocation = IsReady;
            if (hadPriorAllocation &&
                !priorGenerationComplete &&
                _retiredAllocations.Count >= MaxRetiredAllocationGenerations)
            {
                LastFailureReason = "sampled-atlas-retirement-capacity-exhausted";
                return false;
            }

            // When the exact last-use fence is complete the old images can be
            // reclaimed before allocation, preserving the hard capacity budget.
            // Otherwise allocate first and keep the old generation alive under
            // its completion token so a failed optional allocation leaves the
            // prior sampled mirror usable.
            if (hadPriorAllocation && priorGenerationComplete)
                DestroyImageResources();

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

            if (hadPriorAllocation && !priorGenerationComplete)
            {
                if (!RetireCurrentAllocation(
                        lastUseFrameFenceValue,
                        completedFrameFenceValue))
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
            LastFailureReason = string.Empty;
            EstimatedImageBytes = CalculateEstimatedImageBytes(groups);
            _allocationGeneration++;
            if (_allocationGeneration == 0)
                _allocationGeneration = 1;
            return true;
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

            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                AtlasGroup group = _groups[groupIndex];
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

        public void CopyAll(
            CommandBuffer commandBuffer,
            VkBuffer irradianceBuffer,
            ulong irradianceBufferBytes,
            VkBuffer visibilityBuffer,
            ulong visibilityBufferBytes,
            int probeCount)
        {
            if (!IsReady || probeCount <= 0)
                return;

            // Image allocation rounds up to a descriptor-stable layer quantum,
            // while the canonical SSBOs contain only real physical payload
            // probes. Clamp against both source byte ranges so the final image
            // group can never read sampled-atlas padding from beyond either
            // buffer, even if a caller accidentally supplies a virtual count.
            int boundedProbeCount = CalculateSafeCopyProbeCount(
                probeCount,
                _probeCapacity,
                irradianceBufferBytes,
                visibilityBufferBytes);
            if (boundedProbeCount <= 0)
                return;
            TransitionSourceBuffers(
                commandBuffer,
                irradianceBuffer,
                irradianceBufferBytes,
                visibilityBuffer,
                visibilityBufferBytes,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.MemoryWriteBit);
            TransitionImagesToTransferDestination(commandBuffer);

            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                int firstProbe = checked(groupIndex * _layersPerTexture);
                int layerCount = Math.Min(_groups[groupIndex].LayerCount, boundedProbeCount - firstProbe);
                if (layerCount <= 0)
                    break;

                AtlasGroup group = _groups[groupIndex];
                CopyContiguousGroup(
                    commandBuffer,
                    irradianceBuffer,
                    group.IrradianceImage,
                    firstProbe,
                    layerCount,
                    SimpleDdgiVolumeManager.IrradianceTexelsPerProbe);
                CopyContiguousGroup(
                    commandBuffer,
                    visibilityBuffer,
                    group.VisibilityImage,
                    firstProbe,
                    layerCount,
                    SimpleDdgiVolumeManager.VisibilityTexelsPerProbe);
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
                return true;

            bool released = RetireCurrentAllocation(
                lastUseFrameFenceValue,
                completedFrameFenceValue);
            if (released)
                LastFailureReason = string.Empty;
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
        }

        private bool RetireCurrentAllocation(
            ulong lastUseFrameFenceValue,
            ulong completedFrameFenceValue)
        {
            if (!IsReady)
                return true;

            if (lastUseFrameFenceValue == 0UL ||
                completedFrameFenceValue >= lastUseFrameFenceValue)
            {
                DestroyImageResources();
                return true;
            }

            if (_retiredAllocations.Count >= MaxRetiredAllocationGenerations)
                return false;

            AtlasGroup[] groups = _groups;
            ulong bytes = EstimatedImageBytes;
            _groups = Array.Empty<AtlasGroup>();
            _probeCapacity = 0;
            _layersPerTexture = 0;
            _requiresFullSync = false;
            EstimatedImageBytes = 0UL;
            _retiredAllocations.Add(new RetiredAtlasAllocation(
                groups,
                bytes,
                GpuCompletionToken.ForFrameFence(lastUseFrameFenceValue)));
            RetiredImageBytes = checked(RetiredImageBytes + bytes);
            return true;
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
                    SimpleDdgiVolumeManager.IrradianceTexelsPerProbe,
                    layerCount,
                    "Simple DDGI Sampled Irradiance",
                    out group.IrradianceImage,
                    out group.IrradianceAllocation,
                    out group.IrradianceView))
            {
                DestroyGroup(group);
                return false;
            }

            if (!TryCreateImage(
                    SimpleDdgiVolumeManager.VisibilityTexelsPerProbe,
                    layerCount,
                    "Simple DDGI Sampled Visibility",
                    out group.VisibilityImage,
                    out group.VisibilityAllocation,
                    out group.VisibilityView))
            {
                DestroyGroup(group);
                return false;
            }

            return true;
        }

        private bool TryCreateImage(
            int texelsPerProbe,
            int layerCount,
            string debugName,
            out Image image,
            out GpuAllocator.Allocation* allocation,
            out ImageView view)
        {
            image = default;
            allocation = null;
            view = default;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.R16G16B16A16Sfloat,
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

            try
            {
                _context.SetDebugName(image.Handle, ObjectType.Image, $"{debugName} {texelsPerProbe}x{texelsPerProbe}x{layerCount}");
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2DArray,
                    Format = Format.R16G16B16A16Sfloat,
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
                throw;
            }
        }

        private void CopyContiguousGroup(
            CommandBuffer commandBuffer,
            VkBuffer source,
            Image destination,
            int firstProbe,
            int layerCount,
            int texelsPerProbe)
        {
            ulong bytesPerProbe = BytesPerProbe(texelsPerProbe);
            var region = new BufferImageCopy
            {
                BufferOffset = checked((ulong)firstProbe * bytesPerProbe),
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = checked((uint)layerCount)
                },
                ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                ImageExtent = new Extent3D
                {
                    Width = checked((uint)texelsPerProbe),
                    Height = checked((uint)texelsPerProbe),
                    Depth = 1
                }
            };
            _context.Api.CmdCopyBufferToImage(
                commandBuffer,
                source,
                destination,
                ImageLayout.TransferDstOptimal,
                1,
                &region);
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
            FormatProperties properties = default;
            _context.Api.GetPhysicalDeviceFormatProperties(
                _context.PhysicalDevice,
                Format.R16G16B16A16Sfloat,
                &properties);
            const FormatFeatureFlags required =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.SampledImageFilterLinearBit |
                FormatFeatureFlags.StorageImageBit;
            if ((properties.OptimalTilingFeatures & required) != required)
            {
                throw new VulkanException(
                    "R16G16B16A16 sampled DDGI atlases require optimal-tiling linear filtered sampling and storage-image support.");
            }

            PhysicalDeviceProperties deviceProperties = default;
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &deviceProperties);
            uint requiredStorageImages = 2u * MaxGpuPublishTextureGroups;
            if (deviceProperties.Limits.MaxPerStageDescriptorStorageImages < requiredStorageImages)
            {
                throw new VulkanException(
                    $"Simple DDGI GPU publication requires {requiredStorageImages} per-stage storage-image descriptors.");
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
            DestroyGroups(_groups);
            _groups = Array.Empty<AtlasGroup>();
            _probeCapacity = 0;
            _layersPerTexture = 0;
            _requiresFullSync = false;
            EstimatedImageBytes = 0;
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
                bytes = checked(bytes + (ulong)group.LayerCount * BytesPerProbe(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe));
                bytes = checked(bytes + (ulong)group.LayerCount * BytesPerProbe(SimpleDdgiVolumeManager.VisibilityTexelsPerProbe));
            }

            return bytes;
        }

        private static ulong BytesPerProbe(int texelsPerProbe) =>
            checked((ulong)texelsPerProbe * (ulong)texelsPerProbe * AtlasTexelStride);

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
        /// Returns the payload bytes for both RGBA16F image atlases at a fixed
        /// probe capacity. Allocation overhead is still enforced by VMA's
        /// WithinBudget admission at creation time.
        /// </summary>
        internal static ulong CalculateEstimatedImageBytesForProbeCapacity(int probeCapacity)
        {
            if (probeCapacity <= 0)
                return 0;

            return checked((ulong)probeCapacity *
                (BytesPerProbe(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe) +
                 BytesPerProbe(SimpleDdgiVolumeManager.VisibilityTexelsPerProbe)));
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
                BytesPerProbe(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe);
            ulong visibilityCapacity = visibilityBufferBytes /
                BytesPerProbe(SimpleDdgiVolumeManager.VisibilityTexelsPerProbe);
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
        }
    }
}
