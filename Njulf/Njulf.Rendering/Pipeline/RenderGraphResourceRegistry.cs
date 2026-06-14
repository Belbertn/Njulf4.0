using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed class RenderGraphResourceRegistry
    {
        private readonly List<ResourceSlot<RenderGraphImageDesc>> _images = new();
        private readonly List<ResourceSlot<RenderGraphBufferDesc>> _buffers = new();
        private readonly Dictionary<string, RenderGraphResourceHandle> _imageHandlesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RenderGraphResourceHandle> _bufferHandlesByName = new(StringComparer.Ordinal);
        private readonly List<RenderGraphPassDesc> _passes = new();

        public IReadOnlyList<RenderGraphImageDesc> Images => _images.Select(slot => slot.Descriptor).ToArray();
        public IReadOnlyList<RenderGraphBufferDesc> Buffers => _buffers.Select(slot => slot.Descriptor).ToArray();
        public IReadOnlyList<RenderGraphPassDesc> Passes => _passes;

        public RenderGraphResourceHandle GetOrCreateImage(RenderGraphImageDesc desc)
        {
            ValidateImageDesc(desc);
            if (_imageHandlesByName.TryGetValue(desc.Name, out RenderGraphResourceHandle existing))
            {
                RenderGraphImageDesc merged = MergeCompatibleImageDesc(_images[existing.Index].Descriptor, desc);
                _images[existing.Index] = new ResourceSlot<RenderGraphImageDesc>(merged, existing.Generation);
                return existing;
            }

            var handle = new RenderGraphResourceHandle(RenderGraphResourceKind.Image, _images.Count, 1);
            _images.Add(new ResourceSlot<RenderGraphImageDesc>(desc, handle.Generation));
            _imageHandlesByName.Add(desc.Name, handle);
            return handle;
        }

        public RenderGraphResourceHandle GetOrCreateBuffer(RenderGraphBufferDesc desc)
        {
            ValidateBufferDesc(desc);
            if (_bufferHandlesByName.TryGetValue(desc.Name, out RenderGraphResourceHandle existing))
                return existing;

            var handle = new RenderGraphResourceHandle(RenderGraphResourceKind.Buffer, _buffers.Count, 1);
            _buffers.Add(new ResourceSlot<RenderGraphBufferDesc>(desc, handle.Generation));
            _bufferHandlesByName.Add(desc.Name, handle);
            return handle;
        }

        public RenderGraphResourceHandle FindImage(string name)
        {
            return _imageHandlesByName.TryGetValue(name, out RenderGraphResourceHandle handle)
                ? handle
                : RenderGraphResourceHandle.InvalidImage;
        }

        public RenderGraphResourceHandle FindBuffer(string name)
        {
            return _bufferHandlesByName.TryGetValue(name, out RenderGraphResourceHandle handle)
                ? handle
                : RenderGraphResourceHandle.InvalidBuffer;
        }

        public void AddPass(RenderGraphPassDesc pass)
        {
            if (pass == null)
                throw new ArgumentNullException(nameof(pass));
            if (_passes.Any(existing => string.Equals(existing.Name, pass.Name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Render graph pass '{pass.Name}' was declared more than once.");

            _passes.Add(pass);
        }

        public RenderGraphDeclarationPlan Compile()
        {
            ValidatePasses();
            RenderGraphImageDesc[] images = _images.Select(slot => slot.Descriptor).ToArray();
            RenderGraphBufferDesc[] buffers = _buffers.Select(slot => slot.Descriptor).ToArray();
            RenderGraphPassDesc[] passes = _passes.ToArray();
            RenderGraphUsagePlan usage = BuildUsageFlags();
            RenderGraphCompilationDiagnostics diagnostics = RenderGraphDeclarationCompiler.Compile(passes, images, buffers, usage);
            return new RenderGraphDeclarationPlan(images, buffers, passes, usage, diagnostics);
        }

        private void ValidatePasses()
        {
            var produced = new HashSet<RenderGraphResourceHandle>();
            var lastWriterByResource = new Dictionary<RenderGraphResourceHandle, string>();

            foreach (RenderGraphPassDesc pass in _passes)
            {
                if (!pass.IsEnabled)
                    continue;

                ValidateQueueContract(pass);
                ValidateUses(pass, pass.Reads, produced, lastWriterByResource, writes: false, readWrites: false);
                ValidateUses(pass, pass.ReadWrites, produced, lastWriterByResource, writes: true, readWrites: true);
                ValidateUses(pass, pass.Writes, produced, lastWriterByResource, writes: true, readWrites: false);
            }
        }

        private static void ValidateQueueContract(RenderGraphPassDesc pass)
        {
            if (!pass.SupportedQueues.Contains(pass.Queue))
                throw new InvalidOperationException($"Render graph pass '{pass.Name}' does not support its declared queue '{pass.Queue}'.");
            if (!pass.SupportedQueues.Contains(pass.PreferredQueue))
                throw new InvalidOperationException($"Render graph pass '{pass.Name}' prefers unsupported queue '{pass.PreferredQueue}'.");
            if (pass.AsyncEligible && pass.PreferredQueue != RenderGraphQueueClass.Compute)
                throw new InvalidOperationException($"Render graph pass '{pass.Name}' is async eligible but does not prefer the compute queue.");
        }

        private void ValidateUses(
            RenderGraphPassDesc pass,
            IReadOnlyList<RenderGraphResourceUse> uses,
            HashSet<RenderGraphResourceHandle> produced,
            Dictionary<RenderGraphResourceHandle, string> lastWriterByResource,
            bool writes,
            bool readWrites)
        {
            foreach (RenderGraphResourceUse use in uses)
            {
                ValidateHandle(use.Handle, pass.Name);
                RenderGraphResourcePersistence persistence = GetPersistence(use.Handle);
                if (use.UsesAcrossFrames && persistence == RenderGraphResourcePersistence.Transient)
                    throw new InvalidOperationException($"Pass '{pass.Name}' uses transient resource '{GetName(use.Handle)}' across frames.");

                if (use.Handle.Kind == RenderGraphResourceKind.Image && persistence == RenderGraphResourcePersistence.History)
                {
                    RenderGraphImageDesc image = _images[use.Handle.Index].Descriptor;
                    if (string.IsNullOrWhiteSpace(image.HistoryInvalidationRule))
                        throw new InvalidOperationException($"History resource '{image.Name}' is missing a history invalidation rule.");
                }

                if (!writes || (readWrites && RequiresPriorProducer(use.Access)))
                {
                    if (persistence == RenderGraphResourcePersistence.Transient && !produced.Contains(use.Handle))
                        throw new InvalidOperationException($"Pass '{pass.Name}' reads transient resource '{GetName(use.Handle)}' before any producer.");
                }

                if (writes)
                {
                    if (lastWriterByResource.TryGetValue(use.Handle, out string? previousWriter) &&
                        !pass.DependsOn.Contains(previousWriter, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Pass '{pass.Name}' writes resource '{GetName(use.Handle)}' after '{previousWriter}' without an explicit dependency edge.");
                    }

                    produced.Add(use.Handle);
                    lastWriterByResource[use.Handle] = pass.Name;
                }
            }
        }

        private void ValidateHandle(RenderGraphResourceHandle handle, string passName)
        {
            if (!handle.IsValid)
                throw new InvalidOperationException($"Pass '{passName}' declared an invalid {handle.Kind} handle.");

            if (handle.Kind == RenderGraphResourceKind.Image)
            {
                if ((uint)handle.Index >= (uint)_images.Count || _images[handle.Index].Generation != handle.Generation)
                    throw new InvalidOperationException($"Pass '{passName}' declared a stale or undeclared image handle {handle}.");
                return;
            }

            if ((uint)handle.Index >= (uint)_buffers.Count || _buffers[handle.Index].Generation != handle.Generation)
                throw new InvalidOperationException($"Pass '{passName}' declared a stale or undeclared buffer handle {handle}.");
        }

        private RenderGraphResourcePersistence GetPersistence(RenderGraphResourceHandle handle)
        {
            return handle.Kind == RenderGraphResourceKind.Image
                ? _images[handle.Index].Descriptor.Persistence
                : _buffers[handle.Index].Descriptor.Persistence;
        }

        private string GetName(RenderGraphResourceHandle handle)
        {
            return handle.Kind == RenderGraphResourceKind.Image
                ? _images[handle.Index].Descriptor.Name
                : _buffers[handle.Index].Descriptor.Name;
        }

        private RenderGraphUsagePlan BuildUsageFlags()
        {
            var imageUsages = new Dictionary<RenderGraphResourceHandle, ImageUsageFlags>();
            var bufferUsages = new Dictionary<RenderGraphResourceHandle, BufferUsageFlags>();

            foreach (RenderGraphPassDesc pass in _passes)
            {
                AddUses(pass.Reads);
                AddUses(pass.Writes);
                AddUses(pass.ReadWrites);
            }

            return new RenderGraphUsagePlan(imageUsages, bufferUsages);

            void AddUses(IReadOnlyList<RenderGraphResourceUse> uses)
            {
                foreach (RenderGraphResourceUse use in uses)
                {
                    if (use.Handle.Kind == RenderGraphResourceKind.Image)
                        imageUsages[use.Handle] = imageUsages.GetValueOrDefault(use.Handle) | ToImageUsage(use.Access);
                    else
                        bufferUsages[use.Handle] = bufferUsages.GetValueOrDefault(use.Handle) | ToBufferUsage(use.Access);
                }
            }
        }

        private static ImageUsageFlags ToImageUsage(RenderGraphResourceAccess access)
        {
            return access switch
            {
                RenderGraphResourceAccess.SampledRead => ImageUsageFlags.SampledBit,
                RenderGraphResourceAccess.ColorAttachmentRead or RenderGraphResourceAccess.ColorAttachmentWrite => ImageUsageFlags.ColorAttachmentBit,
                RenderGraphResourceAccess.DepthStencilAttachmentRead or RenderGraphResourceAccess.DepthStencilAttachmentWrite => ImageUsageFlags.DepthStencilAttachmentBit,
                RenderGraphResourceAccess.StorageRead or RenderGraphResourceAccess.StorageWrite => ImageUsageFlags.StorageBit,
                RenderGraphResourceAccess.TransferRead => ImageUsageFlags.TransferSrcBit,
                RenderGraphResourceAccess.TransferWrite => ImageUsageFlags.TransferDstBit,
                _ => 0
            };
        }

        private static bool RequiresPriorProducer(RenderGraphResourceAccess access)
        {
            return access is not (
                RenderGraphResourceAccess.ColorAttachmentWrite or
                RenderGraphResourceAccess.DepthStencilAttachmentWrite or
                RenderGraphResourceAccess.StorageWrite or
                RenderGraphResourceAccess.TransferWrite);
        }

        private static BufferUsageFlags ToBufferUsage(RenderGraphResourceAccess access)
        {
            return access switch
            {
                RenderGraphResourceAccess.VertexBufferRead => BufferUsageFlags.VertexBufferBit,
                RenderGraphResourceAccess.IndexBufferRead => BufferUsageFlags.IndexBufferBit,
                RenderGraphResourceAccess.IndirectCommandRead => BufferUsageFlags.IndirectBufferBit,
                RenderGraphResourceAccess.StorageRead or RenderGraphResourceAccess.StorageWrite => BufferUsageFlags.StorageBufferBit,
                RenderGraphResourceAccess.TransferRead => BufferUsageFlags.TransferSrcBit,
                RenderGraphResourceAccess.TransferWrite => BufferUsageFlags.TransferDstBit,
                RenderGraphResourceAccess.UniformRead => BufferUsageFlags.UniformBufferBit,
                _ => 0
            };
        }

        private static void ValidateImageDesc(RenderGraphImageDesc desc)
        {
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));
            if (string.IsNullOrWhiteSpace(desc.Name))
                throw new ArgumentException("Image resource name is required.", nameof(desc));
            if (desc.Format == Format.Undefined)
                throw new ArgumentException($"Image resource '{desc.Name}' must declare a format.", nameof(desc));
            if (desc.MipCount == 0)
                throw new ArgumentException($"Image resource '{desc.Name}' must have at least one mip.", nameof(desc));
            if (desc.ArrayLayers == 0)
                throw new ArgumentException($"Image resource '{desc.Name}' must have at least one array layer.", nameof(desc));
        }

        private static RenderGraphImageDesc MergeCompatibleImageDesc(RenderGraphImageDesc existing, RenderGraphImageDesc requested)
        {
            if (existing.Format != requested.Format ||
                existing.ResolutionClass != requested.ResolutionClass ||
                existing.Width != requested.Width ||
                existing.Height != requested.Height ||
                existing.MipCount != requested.MipCount ||
                existing.ArrayLayers != requested.ArrayLayers ||
                existing.Samples != requested.Samples)
            {
                throw new InvalidOperationException(
                    $"Image resource '{existing.Name}' was requested with incompatible descriptors. " +
                    $"Existing {existing.Format}/{existing.ResolutionClass}/{existing.Width}x{existing.Height}/mips {existing.MipCount}/layers {existing.ArrayLayers}; " +
                    $"requested {requested.Format}/{requested.ResolutionClass}/{requested.Width}x{requested.Height}/mips {requested.MipCount}/layers {requested.ArrayLayers}.");
            }

            return existing with
            {
                AllowDriverCompression = existing.AllowDriverCompression || requested.AllowDriverCompression,
                UsageHint = existing.UsageHint | requested.UsageHint
            };
        }

        private static void ValidateBufferDesc(RenderGraphBufferDesc desc)
        {
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));
            if (string.IsNullOrWhiteSpace(desc.Name))
                throw new ArgumentException("Buffer resource name is required.", nameof(desc));
            if (desc.ByteSize == 0 && (desc.Stride == 0 || desc.Count == 0))
                throw new ArgumentException($"Buffer resource '{desc.Name}' must declare either ByteSize or Stride and Count.", nameof(desc));
        }

        private readonly record struct ResourceSlot<T>(T Descriptor, int Generation);
    }

    public sealed record RenderGraphDeclarationPlan(
        IReadOnlyList<RenderGraphImageDesc> Images,
        IReadOnlyList<RenderGraphBufferDesc> Buffers,
        IReadOnlyList<RenderGraphPassDesc> Passes,
        RenderGraphUsagePlan Usage,
        RenderGraphCompilationDiagnostics Diagnostics);

    public sealed record RenderGraphUsagePlan(
        IReadOnlyDictionary<RenderGraphResourceHandle, ImageUsageFlags> ImageUsages,
        IReadOnlyDictionary<RenderGraphResourceHandle, BufferUsageFlags> BufferUsages);

    public sealed record RenderGraphResourceLifetime(
        RenderGraphResourceHandle Handle,
        string Name,
        RenderGraphResourceKind Kind,
        int FirstUsePassIndex,
        int LastUsePassIndex);

    public sealed record RenderGraphCompilationDiagnostics(
        IReadOnlyList<string> CompiledPassOrder,
        IReadOnlyList<string> CulledPasses,
        IReadOnlyList<RenderGraphResourceLifetime> ResourceLifetimes,
        ulong EstimatedResourceBytes,
        int BarrierCount);
}
