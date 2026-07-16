using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
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
        private const int PreferredLayersPerTexture = 2_048;
        // Large VkBufferImageCopy arrays are disproportionately expensive for
        // drivers to validate and record. Beyond this point, copying the bounded
        // canonical group is cheaper than describing hundreds of sparse layers.
        private const int MaxPartialCopyRegionsPerGroup = 64;
        // Avoid reallocating and idling the device for every small topology
        // adjustment while keeping the reserve below one MiB of atlas data.
        private const int CapacityGrowthQuantum = 256;
        private const ulong AtlasTexelStride = 8;

        private readonly VulkanContext _context;
        private AtlasGroup[] _groups = Array.Empty<AtlasGroup>();
        private BufferImageCopy[] _regionScratch = Array.Empty<BufferImageCopy>();
        private int[] _orderedUpdateProbeIndices = Array.Empty<int>();
        private readonly int[] _groupRegionCounts = new int[MaxTextureGroups];
        private readonly int[] _groupRegionOffsets = new int[MaxTextureGroups];
        private readonly int[] _groupRegionCursors = new int[MaxTextureGroups];
        private Sampler _sampler;
        private int _probeCapacity;
        private int _layersPerTexture;
        private bool _requiresFullSync;
        private bool _disposed;

        public SimpleDdgiSampledAtlas(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
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

        public bool EnsureCapacity(int requiredProbeCount, BindlessHeap? bindlessHeap)
        {
            ThrowIfDisposed();
            if (requiredProbeCount <= 0)
            {
                Release();
                return false;
            }

            int layersPerTexture = ResolveLayersPerTexture();
            int maxProbeCapacity = checked(MaxTextureGroups * layersPerTexture);
            if (requiredProbeCount > maxProbeCapacity)
            {
                LastFailureReason =
                    $"sampled-atlas-needs-{requiredProbeCount}-probe-layers-exceeding-{maxProbeCapacity}";
                Release();
                return false;
            }

            int provisionedProbeCount = CalculateProvisionedProbeCapacity(requiredProbeCount, layersPerTexture);
            int requiredGroups = DivideRoundUp(provisionedProbeCount, layersPerTexture);
            if (requiredGroups > MaxTextureGroups)
            {
                LastFailureReason =
                    $"sampled-atlas-needs-{requiredGroups}-texture-groups-exceeding-{MaxTextureGroups}";
                Release();
                return false;
            }

            if (IsReady &&
                _probeCapacity >= requiredProbeCount &&
                _layersPerTexture == layersPerTexture)
            {
                if (bindlessHeap != null)
                    Register(bindlessHeap);
                return true;
            }

            // Allocation changes are exceptional (tier or scene-topology changes).
            // Retire all previously submitted image work before rebinding fixed
            // descriptors to new image views.
            if (IsReady)
                _context.WaitIdle();
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

            _groups = groups;
            _probeCapacity = provisionedProbeCount;
            _layersPerTexture = layersPerTexture;
            _requiresFullSync = true;
            LastFailureReason = string.Empty;
            EstimatedImageBytes = CalculateEstimatedImageBytes(groups);
            if (bindlessHeap != null)
                Register(bindlessHeap);
            return true;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));
            if (!IsReady)
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
        }

        public void MarkFullSyncRequired()
        {
            if (IsReady)
                _requiresFullSync = true;
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

            int boundedProbeCount = Math.Min(probeCount, _probeCapacity);
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

        public void CopyUpdated(
            CommandBuffer commandBuffer,
            VkBuffer irradianceBuffer,
            ulong irradianceBufferBytes,
            VkBuffer visibilityBuffer,
            ulong visibilityBufferBytes,
            ReadOnlySpan<GPUSimpleDdgiProbeUpdate> updates)
        {
            if (!IsReady || updates.Length == 0)
                return;

            int validUpdateCount = BuildGroupedUpdateIndexRanges(updates);
            if (validUpdateCount == 0)
                return;

            EnsureRegionScratchCapacity(updates.Length);
            TransitionSourceBuffers(
                commandBuffer,
                irradianceBuffer,
                irradianceBufferBytes,
                visibilityBuffer,
                visibilityBufferBytes,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
            TransitionImagesToTransferDestination(commandBuffer);

            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                int updateCount = _groupRegionCounts[groupIndex];
                if (updateCount == 0)
                    continue;
                ReadOnlySpan<int> updatedProbeIndices = new(
                    _orderedUpdateProbeIndices,
                    _groupRegionOffsets[groupIndex],
                    updateCount);
                int contiguousRunCount = CountContiguousProbeRuns(updatedProbeIndices);
                if (ShouldCopyWholeGroup(contiguousRunCount))
                {
                    CopyBoundedContiguousGroup(
                        commandBuffer,
                        irradianceBuffer,
                        irradianceBufferBytes,
                        _groups[groupIndex].IrradianceImage,
                        groupIndex,
                        SimpleDdgiVolumeManager.IrradianceTexelsPerProbe);
                    CopyBoundedContiguousGroup(
                        commandBuffer,
                        visibilityBuffer,
                        visibilityBufferBytes,
                        _groups[groupIndex].VisibilityImage,
                        groupIndex,
                        SimpleDdgiVolumeManager.VisibilityTexelsPerProbe);
                    continue;
                }

                int regionCount = BuildUpdatedRegions(
                    updatedProbeIndices,
                    groupIndex,
                    SimpleDdgiVolumeManager.IrradianceTexelsPerProbe);
                if (regionCount > 0)
                    CopyRegions(commandBuffer, irradianceBuffer, _groups[groupIndex].IrradianceImage, regionCount);

                regionCount = BuildUpdatedRegions(
                    updatedProbeIndices,
                    groupIndex,
                    SimpleDdgiVolumeManager.VisibilityTexelsPerProbe);
                if (regionCount > 0)
                    CopyRegions(commandBuffer, visibilityBuffer, _groups[groupIndex].VisibilityImage, regionCount);
            }

            TransitionImagesToShaderRead(commandBuffer);
            TransitionSourceBuffersForNextShaderUse(
                commandBuffer,
                irradianceBuffer,
                irradianceBufferBytes,
                visibilityBuffer,
                visibilityBufferBytes);
        }

        public void Release()
        {
            if (!IsReady)
                return;

            _context.WaitIdle();
            DestroyImageResources();
            LastFailureReason = string.Empty;
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
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
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

        private void CopyBoundedContiguousGroup(
            CommandBuffer commandBuffer,
            VkBuffer source,
            ulong sourceBytes,
            Image destination,
            int groupIndex,
            int texelsPerProbe)
        {
            ulong bytesPerProbe = BytesPerProbe(texelsPerProbe);
            int sourceProbeCount = checked((int)Math.Min(
                (ulong)_probeCapacity,
                sourceBytes / bytesPerProbe));
            int firstProbe = checked(groupIndex * _layersPerTexture);
            int layerCount = Math.Min(
                _groups[groupIndex].LayerCount,
                Math.Max(0, sourceProbeCount - firstProbe));
            if (layerCount <= 0)
                return;

            CopyContiguousGroup(
                commandBuffer,
                source,
                destination,
                firstProbe,
                layerCount,
                texelsPerProbe);
        }

        private int BuildUpdatedRegions(
            ReadOnlySpan<int> updatedProbeIndices,
            int groupIndex,
            int texelsPerProbe)
        {
            ulong bytesPerProbe = BytesPerProbe(texelsPerProbe);
            int regionCount = 0;
            int firstProbe = checked(groupIndex * _layersPerTexture);
            int runStart = -1;
            int runEnd = -1;
            for (int orderedIndex = 0; orderedIndex < updatedProbeIndices.Length; orderedIndex++)
            {
                int probeIndex = updatedProbeIndices[orderedIndex];
                int layerIndex = checked(probeIndex - firstProbe);
                if ((uint)layerIndex >= (uint)_groups[groupIndex].LayerCount)
                    continue;

                if (runStart < 0)
                {
                    runStart = probeIndex;
                    runEnd = probeIndex;
                    continue;
                }

                if (probeIndex <= runEnd)
                    continue;
                if (probeIndex == runEnd + 1)
                {
                    runEnd = probeIndex;
                    continue;
                }

                WriteUpdatedRegion(
                    regionCount++,
                    runStart,
                    runEnd,
                    firstProbe,
                    bytesPerProbe,
                    texelsPerProbe);
                runStart = probeIndex;
                runEnd = probeIndex;
            }

            if (runStart >= 0)
            {
                WriteUpdatedRegion(
                    regionCount++,
                    runStart,
                    runEnd,
                    firstProbe,
                    bytesPerProbe,
                    texelsPerProbe);
            }

            return regionCount;
        }

        private void WriteUpdatedRegion(
            int regionIndex,
            int runStartProbe,
            int runEndProbe,
            int firstGroupProbe,
            ulong bytesPerProbe,
            int texelsPerProbe)
        {
            int layerCount = checked(runEndProbe - runStartProbe + 1);
            _regionScratch[regionIndex] = new BufferImageCopy
            {
                BufferOffset = checked((ulong)runStartProbe * bytesPerProbe),
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = checked((uint)(runStartProbe - firstGroupProbe)),
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
        }

        private int BuildGroupedUpdateIndexRanges(ReadOnlySpan<GPUSimpleDdgiProbeUpdate> updates)
        {
            Array.Clear(_groupRegionCounts, 0, _groups.Length);
            int validUpdateCount = 0;
            for (int updateIndex = 0; updateIndex < updates.Length; updateIndex++)
            {
                if (!TryGetUpdateGroupIndex(updates[updateIndex].ProbeIndex, out int groupIndex))
                    continue;

                _groupRegionCounts[groupIndex]++;
                validUpdateCount++;
            }

            EnsureOrderedUpdateIndexCapacity(validUpdateCount);
            int offset = 0;
            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                _groupRegionOffsets[groupIndex] = offset;
                _groupRegionCursors[groupIndex] = offset;
                offset += _groupRegionCounts[groupIndex];
            }

            for (int updateIndex = 0; updateIndex < updates.Length; updateIndex++)
            {
                if (!TryGetUpdateGroupIndex(updates[updateIndex].ProbeIndex, out int groupIndex))
                    continue;

                _orderedUpdateProbeIndices[_groupRegionCursors[groupIndex]++] =
                    checked((int)updates[updateIndex].ProbeIndex);
            }

            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                int count = _groupRegionCounts[groupIndex];
                if (count > 1)
                    Array.Sort(_orderedUpdateProbeIndices, _groupRegionOffsets[groupIndex], count);
            }

            return validUpdateCount;
        }

        internal static int CountContiguousProbeRuns(ReadOnlySpan<int> sortedProbeIndices)
        {
            int runCount = 0;
            int previous = -2;
            for (int index = 0; index < sortedProbeIndices.Length; index++)
            {
                int probeIndex = sortedProbeIndices[index];
                if (probeIndex == previous)
                    continue;
                if (probeIndex != previous + 1)
                    runCount++;
                previous = probeIndex;
            }

            return runCount;
        }

        internal static bool ShouldCopyWholeGroup(int contiguousRunCount) =>
            contiguousRunCount > MaxPartialCopyRegionsPerGroup;

        private bool TryGetUpdateGroupIndex(uint probeIndex, out int groupIndex)
        {
            groupIndex = 0;
            if (probeIndex > int.MaxValue ||
                !TryResolveProbeLayer(
                    (int)probeIndex,
                    _layersPerTexture,
                    _groups.Length,
                    out groupIndex,
                    out int layerIndex) ||
                layerIndex >= _groups[groupIndex].LayerCount)
            {
                groupIndex = 0;
                return false;
            }

            return true;
        }

        private void CopyRegions(CommandBuffer commandBuffer, VkBuffer source, Image destination, int regionCount)
        {
            fixed (BufferImageCopy* regions = _regionScratch)
            {
                _context.Api.CmdCopyBufferToImage(
                    commandBuffer,
                    source,
                    destination,
                    ImageLayout.TransferDstOptimal,
                    checked((uint)regionCount),
                    regions);
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
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
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
                ImageLayout.ShaderReadOnlyOptimal =>
                    (PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit),
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
                FormatFeatureFlags.SampledImageFilterLinearBit;
            if ((properties.OptimalTilingFeatures & required) != required)
            {
                throw new VulkanException(
                    "R16G16B16A16 sampled DDGI atlases require optimal-tiling linear filtered sampling support.");
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

        private void EnsureRegionScratchCapacity(int count)
        {
            if (_regionScratch.Length < count)
                _regionScratch = new BufferImageCopy[count];
        }

        private void EnsureOrderedUpdateIndexCapacity(int count)
        {
            if (_orderedUpdateProbeIndices.Length < count)
                _orderedUpdateProbeIndices = new int[count];
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
            if (IsReady)
                _context.WaitIdle();
            DestroyImageResources();
            if (_sampler.Handle != 0)
                _context.Api.DestroySampler(_context.Device, _sampler, null);
            _sampler = default;
            GC.SuppressFinalize(this);
        }

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
