using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiGuidingSourceCacheState : byte
{
    Disabled = 0,
    Ready = 1,
    Failed = 2,
    Disposed = 3
}

/// <summary>
/// Observable ownership state for C3's source-cache direction/PDF sidecar.
/// The sidecar is deliberately not part of <see cref="SimpleDdgiGuidingManager"/>
/// so slots 200-202 and source-cache-owned slot 203 cannot share a retirement
/// transaction by accident.
/// </summary>
public readonly record struct SimpleDdgiGuidingSourceCacheSnapshot(
    SimpleDdgiGuidingSourceCacheState State,
    SimpleDdgiGuidingSourceCacheLayout Layout,
    BufferHandle Buffer,
    ulong ResourceGeneration,
    string Reason)
{
    /// <summary>
    /// True only after a readable guide exists and the source cache has
    /// deliberately exposed this allocation to ordinary DDGI transport at
    /// bindless slot 203.  Allocation/handshake readiness alone does not make
    /// a zero-filled bootstrap sidecar visible to trace.
    /// </summary>
    public bool PayloadDescriptorPublished { get; init; }

    public static SimpleDdgiGuidingSourceCacheSnapshot Disabled { get; } = new(
        SimpleDdgiGuidingSourceCacheState.Disabled,
        SimpleDdgiGuidingSourceCacheLayout.Disabled,
        BufferHandle.Invalid,
        0UL,
        "directional-guiding-disabled");

    public bool IsReady => State == SimpleDdgiGuidingSourceCacheState.Ready &&
        Layout.IsAdmitted && Buffer.IsValid;
}

/// <summary>
/// Owns, clears, publishes, and retires only the fixed slot-203 C3 payload
/// range. Reconciliation is a safe-transition operation: descriptor readers
/// are completed before an old allocation is rebound or destroyed.
/// </summary>
public sealed unsafe class SimpleDdgiGuidingSourceCacheSidecar : IDisposable
{
    // A disabled descriptor must be addressable, but it must never look like
    // one complete 64-byte direction/PDF payload.  Binding the whole shared
    // fallback buffer would make the shader's runtime-array length test report
    // a backed probe and turn zero-filled fallback words into a malformed C3
    // transaction instead of the intended uniform-sampling path.
    public const ulong DisabledFallbackRangeBytes = 16UL;

    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly Action _waitForDescriptorReaders;
    private BindlessHeap? _bindlessHeap;
    private BufferHandle _fallbackBuffer;
    private ulong _fallbackBufferBytes;
    private BufferHandle _buffer;
    private SimpleDdgiGuidingSourceCacheLayout _layout =
        SimpleDdgiGuidingSourceCacheLayout.Disabled;
    private ulong _resourceGeneration;
    private bool _payloadDescriptorPublished;
    private bool _disposed;

    public SimpleDdgiGuidingSourceCacheSidecar(
        VulkanContext context,
        BufferManager bufferManager,
        Action waitForDescriptorReaders)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _waitForDescriptorReaders = waitForDescriptorReaders ??
            throw new ArgumentNullException(nameof(waitForDescriptorReaders));
        Snapshot = SimpleDdgiGuidingSourceCacheSnapshot.Disabled;
    }

    public SimpleDdgiGuidingSourceCacheSnapshot Snapshot { get; private set; }

    /// <summary>
    /// Establishes the source-cache descriptor context and explicitly owns the
    /// disabled fallback publication at slot 203. C3's distribution runtime is
    /// forbidden from publishing this slot.
    /// </summary>
    public bool TryRegisterDescriptorContext(
        BindlessHeap bindlessHeap,
        BufferHandle fallbackBuffer,
        ulong fallbackBufferBytes,
        out string reason)
    {
        if (bindlessHeap is null)
            throw new ArgumentNullException(nameof(bindlessHeap));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryValidateStorageRange(
                    fallbackBuffer,
                    fallbackBufferBytes,
                    out reason))
            {
                return false;
            }
            _bindlessHeap = bindlessHeap;
            _fallbackBuffer = fallbackBuffer;
            _fallbackBufferBytes = DisabledFallbackRangeBytes;
            return _payloadDescriptorPublished
                ? TryPublishCurrentNoLock(out reason)
                : TryPublishFallbackNoLock(out reason);
        }
    }

    /// <summary>
    /// Applies an admitted prefix layout and records a deterministic zero fill
    /// for every payload word before the buffer can be sampled. The caller must
    /// place the later C3 sample dispatch before any DDGI trace consumer.
    /// </summary>
    public bool TryReconcile(
        in SimpleDdgiGuidingSourceCacheLayout requested,
        CommandBuffer commandBuffer,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
                throw new ArgumentException(
                    "A valid command buffer is required.",
                    nameof(commandBuffer));
            if (!requested.IsAdmitted)
            {
                DisableNoLock(requested.Reason);
                reason = requested.Reason;
                return false;
            }
            if (!ValidateLayout(requested, out reason))
            {
                FailClosedNoLock(reason);
                return false;
            }
            if (_bindlessHeap is null)
            {
                reason = "guiding-source-cache-descriptor-context-unavailable";
                FailClosedNoLock(reason);
                return false;
            }
            if (_buffer.IsValid && _layout.Equals(requested))
            {
                bool publicationValid = _payloadDescriptorPublished
                    ? TryPublishCurrentNoLock(out reason)
                    : TryPublishFallbackNoLock(out reason);
                if (!publicationValid)
                {
                    FailClosedNoLock(reason);
                    return false;
                }
                Snapshot = new(
                    SimpleDdgiGuidingSourceCacheState.Ready,
                    _layout,
                    _buffer,
                    _resourceGeneration,
                    "guiding-source-cache-sidecar-reused")
                {
                    PayloadDescriptorPublished =
                        _payloadDescriptorPublished
                };
                reason = Snapshot.Reason;
                return true;
            }

            _waitForDescriptorReaders();
            BufferHandle candidate = BufferHandle.Invalid;
            try
            {
                candidate = _bufferManager.CreateDeviceBuffer(
                    requested.AllocatedBytes,
                    BufferUsageFlags.StorageBufferBit |
                        BufferUsageFlags.TransferDstBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "Simple DDGI C3 Direction/PDF Source-Cache Sidecar");
                VkBuffer candidateBuffer = _bufferManager.GetBuffer(candidate);
                _context.Api.CmdFillBuffer(
                    commandBuffer,
                    candidateBuffer,
                    0UL,
                    requested.AllocatedBytes,
                    0u);
                BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                    candidateBuffer,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit |
                        AccessFlags2.ShaderStorageWriteBit,
                    0UL,
                    requested.AllocatedBytes);
                var dependency = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = 1u,
                    PBufferMemoryBarriers = &barrier
                };
                _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
            }
            catch (Exception exception)
            {
                if (candidate.IsValid)
                    _bufferManager.DestroyBuffer(candidate);
                reason = "guiding-source-cache-sidecar-allocation-or-publication-failed:" +
                    exception.GetType().Name;
                FailClosedNoLock(reason);
                return false;
            }

            BufferHandle previous = _buffer;
            _buffer = candidate;
            _layout = requested;
            _resourceGeneration = NextNonZero(_resourceGeneration);
            _payloadDescriptorPublished = false;
            if (!TryPublishFallbackNoLock(out string fallbackReason))
            {
                _buffer = previous;
                _layout = SimpleDdgiGuidingSourceCacheLayout.Disabled;
                _bufferManager.DestroyBuffer(candidate);
                reason = fallbackReason;
                FailClosedNoLock(reason);
                return false;
            }
            if (previous.IsValid)
                _bufferManager.DestroyBuffer(previous);
            Snapshot = new(
                SimpleDdgiGuidingSourceCacheState.Ready,
                _layout,
                _buffer,
                _resourceGeneration,
                "guiding-source-cache-sidecar-created-and-cleared")
            {
                PayloadDescriptorPublished = false
            };
            reason = Snapshot.Reason;
            return true;
        }
    }

    public SimpleDdgiGuidingSourceCacheHandshake CreateHandshake()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!Snapshot.IsReady)
                return SimpleDdgiGuidingSourceCacheHandshake.Unavailable;
            return new SimpleDdgiGuidingSourceCacheHandshake(
                IsAvailable: true,
                GuidingAbiVersion: SimpleDdgiGuidingGpuAbi.Version,
                SourceCacheOwnsDirectionPdfSidecar: true,
                DirectionPdfSidecarBindlessSlot:
                    (uint)SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
                DirectionPdfSidecar: _buffer,
                DirectionPdfSidecarOffsetBytes: 0UL,
                DirectionPdfSidecarBytes: _layout.AllocatedBytes,
                DirectionPdfSidecarCapacity: _layout.PayloadCapacity,
                DirectionPdfSidecarStrideBytes:
                    SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount,
                SourceCachePriorAccessStageMask:
                    PipelineStageFlags2.ComputeShaderBit,
                SourceCachePriorAccessMask:
                    AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                ConsumerAcceptsGenerationTimePdf: true,
                ConsumerSupportsVariablePdfProjection: true,
                ConsumerReadStageMask: PipelineStageFlags2.ComputeShaderBit,
                ConsumerReadAccessMask: AccessFlags2.ShaderStorageReadBit);
        }
    }

    /// <summary>
    /// Publishes the allocated payload range to DDGI transport only after the
    /// caller has a header-validated distribution and will record a complete
    /// sample dispatch before trace.  This is a one-way transition for the
    /// current allocation; disabling or replacing it restores the deliberately
    /// undersized fallback descriptor.
    /// </summary>
    public bool TryPublishForSampling(out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!Snapshot.IsReady || !_buffer.IsValid || !_layout.IsAdmitted)
            {
                reason = "guiding-source-cache-sidecar-not-ready-for-sampling";
                return false;
            }
            if (_payloadDescriptorPublished)
            {
                reason = "guiding-source-cache-sidecar-already-published";
                return true;
            }

            _waitForDescriptorReaders();
            if (!TryPublishCurrentNoLock(out reason))
            {
                FailClosedNoLock(reason);
                return false;
            }
            _payloadDescriptorPublished = true;
            Snapshot = Snapshot with
            {
                PayloadDescriptorPublished = true,
                Reason = "guiding-source-cache-sidecar-published-for-sampling"
            };
            reason = Snapshot.Reason;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _waitForDescriptorReaders();
            _ = TryPublishFallbackNoLock(out _);
            if (_buffer.IsValid)
                _bufferManager.DestroyBuffer(_buffer);
            _buffer = BufferHandle.Invalid;
            _layout = SimpleDdgiGuidingSourceCacheLayout.Disabled;
            _payloadDescriptorPublished = false;
            _bindlessHeap = null;
            _fallbackBuffer = BufferHandle.Invalid;
            _fallbackBufferBytes = 0UL;
            _disposed = true;
            Snapshot = new(
                SimpleDdgiGuidingSourceCacheState.Disposed,
                SimpleDdgiGuidingSourceCacheLayout.Disabled,
                BufferHandle.Invalid,
                _resourceGeneration,
                "guiding-source-cache-sidecar-disposed");
        }
    }

    private bool ValidateLayout(
        in SimpleDdgiGuidingSourceCacheLayout layout,
        out string reason)
    {
        try
        {
            ulong exactBytes = checked(
                (ulong)layout.PayloadCapacity *
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount);
            bool valid = layout.IsAdmitted &&
                layout.AdmittedGuidedPhysicalProbeCapacity > 0 &&
                layout.DirectionSlotsPerProbe > 0 &&
                layout.PayloadCapacity > 0u &&
                layout.PayloadStrideBytes ==
                    SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount &&
                layout.AllocatedBytes == exactBytes;
            reason = valid
                ? string.Empty
                : "guiding-source-cache-sidecar-layout-invalid";
            return valid;
        }
        catch (OverflowException)
        {
            reason = "guiding-source-cache-sidecar-layout-overflow";
            return false;
        }
    }

    private bool TryValidateStorageRange(
        BufferHandle handle,
        ulong bytes,
        out string reason)
    {
        try
        {
            bool valid = handle.IsValid && bytes > 0UL &&
                _bufferManager.GetBufferSize(handle) >= bytes &&
                (_bufferManager.GetBufferUsage(handle) &
                    BufferUsageFlags.StorageBufferBit) != 0;
            reason = valid
                ? string.Empty
                : "guiding-source-cache-fallback-range-or-usage-invalid";
            return valid;
        }
        catch (Exception exception)
        {
            reason = "guiding-source-cache-fallback-not-live:" +
                exception.GetType().Name;
            return false;
        }
    }

    private void DisableNoLock(string reason)
    {
        _waitForDescriptorReaders();
        _ = TryPublishFallbackNoLock(out string fallbackReason);
        if (_buffer.IsValid)
            _bufferManager.DestroyBuffer(_buffer);
        _buffer = BufferHandle.Invalid;
        _layout = SimpleDdgiGuidingSourceCacheLayout.Disabled;
        _payloadDescriptorPublished = false;
        Snapshot = new(
            SimpleDdgiGuidingSourceCacheState.Disabled,
            _layout,
            _buffer,
            _resourceGeneration,
            string.IsNullOrWhiteSpace(fallbackReason)
                ? reason
                : fallbackReason);
    }

    private void FailClosedNoLock(string reason)
    {
        DisableNoLock(reason);
        Snapshot = Snapshot with
        {
            State = SimpleDdgiGuidingSourceCacheState.Failed,
            Reason = reason
        };
    }

    private bool TryPublishCurrentNoLock(out string reason)
    {
        if (_buffer.IsValid && _layout.IsAdmitted)
        {
            try
            {
                _bindlessHeap!.RegisterStorageBuffer(
                    SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
                    _bufferManager.GetBuffer(_buffer),
                    0UL,
                    _layout.AllocatedBytes);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "guiding-source-cache-sidecar-publication-failed:" +
                    exception.GetType().Name;
                return false;
            }
        }
        reason = "guiding-source-cache-sidecar-not-allocated";
        return false;
    }

    private bool TryPublishFallbackNoLock(out string reason)
    {
        if (_bindlessHeap is null)
        {
            reason = "guiding-source-cache-descriptor-context-unavailable";
            return false;
        }
        if (!TryValidateStorageRange(
                _fallbackBuffer,
                _fallbackBufferBytes,
                out reason))
        {
            return false;
        }
        try
        {
            _bindlessHeap.RegisterStorageBuffer(
                SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
                _bufferManager.GetBuffer(_fallbackBuffer),
                0UL,
                _fallbackBufferBytes);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = "guiding-source-cache-fallback-publication-failed:" +
                exception.GetType().Name;
            return false;
        }
    }

    private static ulong NextNonZero(ulong generation) =>
        generation == ulong.MaxValue ? 1UL : generation + 1UL;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
