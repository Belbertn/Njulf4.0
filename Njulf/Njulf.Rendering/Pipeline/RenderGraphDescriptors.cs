using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public enum RenderGraphResolutionClass
    {
        Swapchain,
        Scene,
        HalfScene,
        QuarterScene,
        Fixed,
        CustomScale,
        HistoryMatchedScene
    }

    public enum RenderGraphResourcePersistence
    {
        Transient,
        Imported,
        History,
        External
    }

    public enum RenderGraphQueueClass
    {
        Graphics,
        Compute,
        Transfer
    }

    public enum RenderGraphDependencyUrgency
    {
        Normal,
        ImmediateGraphicsConsumer,
        LongIndependentWork
    }

    public enum RenderGraphCpuUploadPolicy
    {
        None,
        PerFrameUpload,
        PersistentMapped
    }

    public enum RenderGraphResourceAccess
    {
        SampledRead,
        ColorAttachmentRead,
        ColorAttachmentWrite,
        DepthStencilAttachmentRead,
        DepthStencilAttachmentWrite,
        StorageRead,
        StorageWrite,
        TransferRead,
        TransferWrite,
        VertexBufferRead,
        IndexBufferRead,
        IndirectCommandRead,
        UniformRead
    }

    public sealed record RenderGraphImageDesc(
        string Name,
        Format Format,
        RenderGraphResolutionClass ResolutionClass,
        RenderGraphResourcePersistence Persistence)
    {
        public uint Width { get; init; }
        public uint Height { get; init; }
        public float CustomResolutionScale { get; init; } = 1.0f;
        public uint MipCount { get; init; } = 1;
        public uint ArrayLayers { get; init; } = 1;
        public SampleCountFlags Samples { get; init; } = SampleCountFlags.Count1Bit;
        public ClearValue ClearValue { get; init; }
        public string HistoryInvalidationRule { get; init; } = string.Empty;
        public bool AllowDriverCompression { get; init; }
        public ImageUsageFlags UsageHint { get; init; }
    }

    public sealed record RenderGraphBufferDesc(
        string Name,
        RenderGraphResourcePersistence Persistence)
    {
        public uint Stride { get; init; }
        public uint Count { get; init; }
        public ulong ByteSize { get; init; }
        public BufferUsageFlags Usage { get; init; }
        public RenderGraphCpuUploadPolicy CpuUploadPolicy { get; init; } = RenderGraphCpuUploadPolicy.None;
    }

    public sealed record RenderGraphResourceUse(
        RenderGraphResourceHandle Handle,
        RenderGraphResourceAccess Access,
        PipelineStageFlags2 Stages)
    {
        public AttachmentLoadOp? LoadOp { get; init; }
        public AttachmentStoreOp? StoreOp { get; init; }
        public ClearValue ClearValue { get; init; }
        public bool UsesAcrossFrames { get; init; }
        public uint BaseMipLevel { get; init; }
        public uint LevelCount { get; init; } = Vk.RemainingMipLevels;
        public uint BaseArrayLayer { get; init; }
        public uint LayerCount { get; init; } = Vk.RemainingArrayLayers;
    }

    public sealed class RenderGraphPassDesc
    {
        private readonly List<RenderGraphResourceUse> _reads = new();
        private readonly List<RenderGraphResourceUse> _writes = new();
        private readonly List<RenderGraphResourceUse> _readWrites = new();
        private readonly List<string> _dependsOn = new();
        private readonly HashSet<RenderGraphQueueClass> _supportedQueues = new();

        public RenderGraphPassDesc(string name, RenderGraphQueueClass queue)
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? throw new System.ArgumentException("Pass name is required.", nameof(name))
                : name;
            Queue = queue;
            PreferredQueue = queue;
            _supportedQueues.Add(queue);
        }

        public string Name { get; }
        public RenderGraphQueueClass Queue { get; }
        public RenderGraphQueueClass PreferredQueue { get; init; }
        public IReadOnlyCollection<RenderGraphQueueClass> SupportedQueues => _supportedQueues;
        public IReadOnlyList<RenderGraphResourceUse> Reads => _reads;
        public IReadOnlyList<RenderGraphResourceUse> Writes => _writes;
        public IReadOnlyList<RenderGraphResourceUse> ReadWrites => _readWrites;
        public IReadOnlyList<string> DependsOn => _dependsOn;
        public bool AsyncEligible { get; init; }
        public int ExpectedWorkloadScore { get; init; }
        public bool BandwidthHeavy { get; init; }
        public RenderGraphDependencyUrgency DependencyUrgency { get; init; } = RenderGraphDependencyUrgency.Normal;
        public bool SupportsSecondaryCommandBuffer { get; init; }
        public string TimingLabel { get; init; } = string.Empty;
        public bool HasExternalSideEffect { get; init; }
        public bool IsEnabled { get; init; } = true;
        public bool NeverCull { get; init; }

        public RenderGraphPassDesc SupportsQueue(RenderGraphQueueClass queue)
        {
            _supportedQueues.Add(queue);
            return this;
        }

        public RenderGraphPassDesc Read(
            RenderGraphResourceHandle handle,
            RenderGraphResourceAccess access,
            PipelineStageFlags2 stages,
            bool usesAcrossFrames = false,
            uint baseMipLevel = 0,
            uint levelCount = Vk.RemainingMipLevels,
            uint baseArrayLayer = 0,
            uint layerCount = Vk.RemainingArrayLayers)
        {
            _reads.Add(new RenderGraphResourceUse(handle, access, stages)
            {
                UsesAcrossFrames = usesAcrossFrames,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = layerCount
            });
            return this;
        }

        public RenderGraphPassDesc Write(
            RenderGraphResourceHandle handle,
            RenderGraphResourceAccess access,
            PipelineStageFlags2 stages,
            AttachmentLoadOp? loadOp = null,
            AttachmentStoreOp? storeOp = null,
            ClearValue clearValue = default,
            bool usesAcrossFrames = false,
            uint baseMipLevel = 0,
            uint levelCount = Vk.RemainingMipLevels,
            uint baseArrayLayer = 0,
            uint layerCount = Vk.RemainingArrayLayers)
        {
            _writes.Add(new RenderGraphResourceUse(handle, access, stages)
            {
                LoadOp = loadOp,
                StoreOp = storeOp,
                ClearValue = clearValue,
                UsesAcrossFrames = usesAcrossFrames,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = layerCount
            });
            return this;
        }

        public RenderGraphPassDesc ReadWrite(
            RenderGraphResourceHandle handle,
            RenderGraphResourceAccess access,
            PipelineStageFlags2 stages,
            AttachmentLoadOp? loadOp = null,
            AttachmentStoreOp? storeOp = null,
            ClearValue clearValue = default,
            bool usesAcrossFrames = false,
            uint baseMipLevel = 0,
            uint levelCount = Vk.RemainingMipLevels,
            uint baseArrayLayer = 0,
            uint layerCount = Vk.RemainingArrayLayers)
        {
            _readWrites.Add(new RenderGraphResourceUse(handle, access, stages)
            {
                LoadOp = loadOp,
                StoreOp = storeOp,
                ClearValue = clearValue,
                UsesAcrossFrames = usesAcrossFrames,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = layerCount
            });
            return this;
        }

        public RenderGraphPassDesc After(string passName)
        {
            if (!string.IsNullOrWhiteSpace(passName))
                _dependsOn.Add(passName);
            return this;
        }
    }
}
