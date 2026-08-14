using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Lazily-owned full-resolution temporal/filter storage for directional
/// shadows. Hard and hybrid modes keep using their compact packed-R8 banks and
/// never instantiate this allocation set.
/// </summary>
public sealed unsafe class DirectionalShadowHistoryResources : IDisposable
{
    public const ulong RawBytesPerPixel = 4UL;
    public const ulong HistoryBytesPerPixel = 12UL;
    public const ulong ScratchBytesPerPixel = 4UL;
    public const ulong DiagnosticBytesPerPixel = 4UL;
    public const ulong CounterBytes = 64UL * sizeof(uint);

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferHandle[] _raw = new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _history = new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _scratch = new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _diagnostic = new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _counters = new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly bool[] _counterFrameSubmitted =
        new bool[RenderingConstants.FramesInFlight];
    private readonly DirectionalShadowRayCounters[] _lastCompletedCounters =
        new DirectionalShadowRayCounters[RenderingConstants.FramesInFlight];
    private bool _disposed;

    public DirectionalShadowHistoryResources(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
    }

    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint ResourceGeneration { get; private set; }
    public bool DetailedDiagnosticsAllocated { get; private set; }
    public ulong RawBufferBytes { get; private set; }
    public ulong HistoryBufferBytes { get; private set; }
    public ulong ScratchBufferBytes { get; private set; }
    public ulong DiagnosticBufferBytes { get; private set; }
    public ulong EstimatedBytes => checked((RawBufferBytes + HistoryBufferBytes +
        ScratchBufferBytes + DiagnosticBufferBytes) *
        RenderingConstants.FramesInFlight +
        CounterBytes * RenderingConstants.FramesInFlight);
    public bool CountersAllocated => AllValid(_counters);
    public bool IsAllocated => Width != 0u && Height != 0u && AllValid(_raw) &&
        AllValid(_history) && AllValid(_scratch) && AllValid(_counters);

    public BufferHandle GetRaw(int frameIndex) => Get(_raw, frameIndex);
    public BufferHandle GetHistory(int frameIndex) => Get(_history, frameIndex);
    public BufferHandle GetScratch(int frameIndex) => Get(_scratch, frameIndex);
    public BufferHandle GetDiagnostic(int frameIndex) =>
        DetailedDiagnosticsAllocated ? Get(_diagnostic, frameIndex) : Get(_scratch, frameIndex);
    public BufferHandle GetCounters(int frameIndex) => Get(_counters, frameIndex);

    public void EnsureCounters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CountersAllocated)
            return;

        var replacements = new BufferHandle[RenderingConstants.FramesInFlight];
        try
        {
            for (int frame = 0; frame < replacements.Length; frame++)
            {
                replacements[frame] = Create(
                    CounterBytes,
                    $"Directional shadow counters frame {frame}",
                    hostVisible: true);
            }
            Replace(_counters, replacements);
            for (int frame = 0; frame < RenderingConstants.FramesInFlight; frame++)
            {
                Register(
                    BindlessIndex.DirectionalShadowCounterBufferBase + frame,
                    _counters[frame],
                    CounterBytes);
            }
        }
        catch
        {
            Destroy(replacements);
            throw;
        }
    }

    public void MarkCountersSubmitted(int frameIndex)
    {
        if ((uint)frameIndex < (uint)_counterFrameSubmitted.Length)
            _counterFrameSubmitted[frameIndex] = true;
    }

    public void ReadCompletedFrame(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_counters.Length ||
            !_counters[frameIndex].IsValid ||
            !_counterFrameSubmitted[frameIndex])
        {
            if ((uint)frameIndex < (uint)_lastCompletedCounters.Length)
                _lastCompletedCounters[frameIndex] = DirectionalShadowRayCounters.Empty;
            return;
        }

        _bufferManager.InvalidateBuffer(
            _counters[frameIndex],
            0,
            CounterBytes);
        uint* values = (uint*)_bufferManager.GetMappedPointer(
            _counters[frameIndex]);
        _lastCompletedCounters[frameIndex] = new DirectionalShadowRayCounters(
            ReadbackValid: 1,
            OpaqueRaysIssued: values[0],
            OpaqueRaysSkipped: values[1],
            OpaqueHits: values[2],
            OpaqueMisses: values[3],
            OpaqueCandidateCount: values[4],
            OpaqueAlphaSampleCount: values[5],
            OpaqueCandidateCapHits: values[6],
            InvalidReceiverCount: values[7],
            BoundsRejectionCount: values[8],
            TemporalAcceptedCount: values[9],
            TemporalRejectedCount: values[10],
            SpatialFilteredPixelCount: values[11],
            TransparentRaysIssued: values[12],
            TransparentHits: values[13],
            TransparentMisses: values[14],
            TransparentCandidateCount: values[15],
            TransparentAlphaSampleCount: values[16],
            TransparentCandidateCapHits: values[17],
            TransparentBoundsRejectionCount: values[18]);
        _counterFrameSubmitted[frameIndex] = false;
    }

    public DirectionalShadowRayCounters GetLastCompletedCounters(int frameIndex) =>
        (uint)frameIndex < (uint)_lastCompletedCounters.Length
            ? _lastCompletedCounters[frameIndex]
            : DirectionalShadowRayCounters.Empty;

    public bool Ensure(uint width, uint height, bool detailedDiagnostics)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCounters();
        if (width == 0u || height == 0u)
            return false;
        if (IsAllocated && Width == width && Height == height &&
            (!detailedDiagnostics || DetailedDiagnosticsAllocated))
        {
            return true;
        }

        ulong pixels = checked((ulong)width * height);
        ulong rawBytes = checked(pixels * RawBytesPerPixel);
        ulong historyBytes = checked(pixels * HistoryBytesPerPixel);
        ulong scratchBytes = checked(pixels * ScratchBytesPerPixel);
        ulong diagnosticBytes = detailedDiagnostics
            ? checked(pixels * DiagnosticBytesPerPixel)
            : 0UL;
        ValidateStorageRange(rawBytes, nameof(rawBytes));
        ValidateStorageRange(historyBytes, nameof(historyBytes));
        ValidateStorageRange(scratchBytes, nameof(scratchBytes));
        if (diagnosticBytes != 0UL)
            ValidateStorageRange(diagnosticBytes, nameof(diagnosticBytes));

        var raw = new BufferHandle[RenderingConstants.FramesInFlight];
        var history = new BufferHandle[RenderingConstants.FramesInFlight];
        var scratch = new BufferHandle[RenderingConstants.FramesInFlight];
        var diagnostic = new BufferHandle[RenderingConstants.FramesInFlight];
        try
        {
            for (int frame = 0; frame < RenderingConstants.FramesInFlight; frame++)
            {
                raw[frame] = Create(rawBytes, $"Directional shadow raw frame {frame}");
                history[frame] = Create(historyBytes, $"Directional shadow history frame {frame}");
                scratch[frame] = Create(scratchBytes, $"Directional shadow scratch frame {frame}");
                if (detailedDiagnostics)
                {
                    diagnostic[frame] = Create(
                        diagnosticBytes,
                        $"Directional shadow diagnostics frame {frame}");
                }
            }

            if (IsAllocated)
            {
                Result idleResult = _context.Api.DeviceWaitIdle(_context.Device);
                if (idleResult != Result.Success)
                    throw new VulkanException("Failed to wait before replacing directional-shadow history", idleResult);
            }
            Replace(_raw, raw);
            Replace(_history, history);
            Replace(_scratch, scratch);
            Replace(_diagnostic, diagnostic);
            Width = width;
            Height = height;
            RawBufferBytes = rawBytes;
            HistoryBufferBytes = historyBytes;
            ScratchBufferBytes = scratchBytes;
            DiagnosticBufferBytes = diagnosticBytes;
            DetailedDiagnosticsAllocated = detailedDiagnostics;
            ResourceGeneration = ResourceGeneration == uint.MaxValue
                ? 1u
                : Math.Max(1u, ResourceGeneration + 1u);
            Register();
            return true;
        }
        catch
        {
            Destroy(raw);
            Destroy(history);
            Destroy(scratch);
            Destroy(diagnostic);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The renderer owns this object and disposes it after device-idle pass
        // cleanup, matching every other persistent render resource.
        Destroy(_raw);
        Destroy(_history);
        Destroy(_scratch);
        Destroy(_diagnostic);
        Destroy(_counters);
        Width = 0u;
        Height = 0u;
    }

    private BufferHandle Create(
        ulong bytes,
        string name,
        bool hostVisible = false) =>
        _bufferManager.CreateBuffer(
            bytes,
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.TransferDstBit |
            BufferUsageFlags.TransferSrcBit,
            hostVisible ? MemoryUsage.AutoPreferHost : MemoryUsage.AutoPreferDevice,
            hostVisible
                ? AllocationCreateFlags.MappedBit |
                    AllocationCreateFlags.HostAccessRandomBit
                : (AllocationCreateFlags)0,
            debugName: name,
            category: MemoryBudgetCategory.ShadowMaps);

    private void Register()
    {
        for (int frame = 0; frame < RenderingConstants.FramesInFlight; frame++)
        {
            Register(BindlessIndex.DirectionalShadowRawBufferBase + frame, _raw[frame], RawBufferBytes);
            Register(BindlessIndex.DirectionalShadowHistoryBufferBase + frame, _history[frame], HistoryBufferBytes);
            Register(BindlessIndex.DirectionalShadowScratchBufferBase + frame, _scratch[frame], ScratchBufferBytes);
            BufferHandle diagnostic = DetailedDiagnosticsAllocated
                ? _diagnostic[frame]
                : _scratch[frame];
            ulong diagnosticRange = DetailedDiagnosticsAllocated
                ? DiagnosticBufferBytes
                : ScratchBufferBytes;
            Register(BindlessIndex.DirectionalShadowDiagnosticBufferBase + frame, diagnostic, diagnosticRange);
            Register(BindlessIndex.DirectionalShadowCounterBufferBase + frame, _counters[frame], CounterBytes);
        }
    }

    private void Register(int index, BufferHandle handle, ulong bytes) =>
        _bindlessHeap.RegisterStorageBuffer(
            index,
            _bufferManager.GetBuffer(handle),
            0UL,
            bytes);

    private void ValidateStorageRange(ulong bytes, string owner)
    {
        if (bytes == 0UL ||
            _context.MaximumStorageBufferRange != 0UL &&
            bytes > _context.MaximumStorageBufferRange)
        {
            throw new InvalidOperationException(
                $"{owner} requires {bytes} bytes; maximum storage-buffer range is " +
                $"{_context.MaximumStorageBufferRange} bytes");
        }
    }

    private void Replace(BufferHandle[] destination, BufferHandle[] source)
    {
        Destroy(destination);
        Array.Copy(source, destination, destination.Length);
        Array.Fill(source, BufferHandle.Invalid);
    }

    private void Destroy(Span<BufferHandle> buffers)
    {
        for (int index = 0; index < buffers.Length; index++)
        {
            if (buffers[index].IsValid)
                _bufferManager.DestroyBuffer(buffers[index]);
            buffers[index] = BufferHandle.Invalid;
        }
    }

    private static bool AllValid(ReadOnlySpan<BufferHandle> buffers)
    {
        foreach (BufferHandle buffer in buffers)
        {
            if (!buffer.IsValid)
                return false;
        }
        return true;
    }

    private static BufferHandle Get(BufferHandle[] buffers, int frameIndex) =>
        (uint)frameIndex < (uint)buffers.Length
            ? buffers[frameIndex]
            : BufferHandle.Invalid;
}
