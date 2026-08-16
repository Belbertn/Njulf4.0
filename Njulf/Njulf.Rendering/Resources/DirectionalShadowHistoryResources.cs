using System;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources;

/// <summary>
/// CPU-side identity for one directional-shadow history stream. It is resolved
/// before ray dispatch so recovery sampling and temporal rejection observe the
/// same reset decision in a frame.
/// </summary>
public readonly record struct DirectionalShadowHistoryRevision(
    ulong StableLightIdentity,
    DirectionalShadowMode EffectiveMode,
    Vector3 LightDirection,
    float SunAngularRadiusRadians,
    float MaximumRayDistance,
    uint Width,
    uint Height,
    uint ScreenResourceGeneration,
    uint RaySceneResourceGeneration)
{
    public static DirectionalShadowHistoryRevision Capture(
        SceneRenderingData sceneData,
        DirectionalShadowHistoryResources resources,
        float maximumRayDistance)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        ArgumentNullException.ThrowIfNull(resources);
        DirectionalShadowFramePlan plan = sceneData.DirectionalShadowFramePlan;
        return new DirectionalShadowHistoryRevision(
            plan.StableLightIdentity,
            plan.EffectiveMode,
            sceneData.DirectionalShadowLightDirection,
            plan.SunAngularRadiusRadians,
            maximumRayDistance,
            resources.Width,
            resources.Height,
            resources.ResourceGeneration,
            plan.RaySceneResourceGeneration);
    }
}

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
    private DirectionalShadowHistoryRevision _committedHistoryRevision;
    private DirectionalShadowHistoryResetReason _pendingResetReasons;
    private bool _historyStateValid;
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

    /// <summary>
    /// Resolves reset causes without committing the current revision. The ray
    /// and temporal passes can therefore query this during the same frame and
    /// receive an identical answer.
    /// </summary>
    public DirectionalShadowHistoryResetReason ResolveHistoryResetReasons(
        in DirectionalShadowHistoryRevision revision,
        bool motionVectorsValid)
    {
        DirectionalShadowHistoryResetReason reasons =
            DirectionalShadowHistoryResetReason.None;
        if (!_historyStateValid)
        {
            reasons |= DirectionalShadowHistoryResetReason.InitialFrame;
            reasons |= _pendingResetReasons;
        }
        if (!motionVectorsValid)
            reasons |= DirectionalShadowHistoryResetReason.InvalidMotion;
        if (!_historyStateValid)
            return reasons;

        DirectionalShadowHistoryRevision previous = _committedHistoryRevision;
        if (previous.StableLightIdentity != revision.StableLightIdentity ||
            DirectionChanged(previous, revision) ||
            SoftSourceChanged(previous, revision) ||
            DistanceChanged(previous.MaximumRayDistance, revision.MaximumRayDistance))
        {
            reasons |= DirectionalShadowHistoryResetReason.LightChanged;
        }
        if (previous.EffectiveMode != revision.EffectiveMode)
            reasons |= DirectionalShadowHistoryResetReason.ModeChanged;
        if (previous.Width != revision.Width || previous.Height != revision.Height)
            reasons |= DirectionalShadowHistoryResetReason.ExtentChanged;
        if (previous.ScreenResourceGeneration != revision.ScreenResourceGeneration)
            reasons |= DirectionalShadowHistoryResetReason.ResourceRecreated;
        if (revision.EffectiveMode == DirectionalShadowMode.RayQuerySoft &&
            previous.RaySceneResourceGeneration != revision.RaySceneResourceGeneration)
        {
            reasons |= DirectionalShadowHistoryResetReason.RaySceneChanged;
        }
        return reasons;
    }

    public void CommitHistoryRevision(in DirectionalShadowHistoryRevision revision)
    {
        _committedHistoryRevision = revision;
        _pendingResetReasons = DirectionalShadowHistoryResetReason.None;
        _historyStateValid = true;
    }

    public void InvalidateHistoryState(
        DirectionalShadowHistoryResetReason reason =
            DirectionalShadowHistoryResetReason.None)
    {
        _committedHistoryRevision = default;
        _pendingResetReasons |= reason;
        _historyStateValid = false;
    }

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
            InvalidateHistoryState(
                DirectionalShadowHistoryResetReason.ResourceRecreated);
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
        InvalidateHistoryState();
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

    private static bool DirectionChanged(
        in DirectionalShadowHistoryRevision previous,
        in DirectionalShadowHistoryRevision current)
    {
        Vector3 previousDirection = previous.LightDirection;
        Vector3 currentDirection = current.LightDirection;
        float previousLength = previousDirection.LengthSquared();
        float currentLength = currentDirection.LengthSquared();
        if (!float.IsFinite(previousLength) || !float.IsFinite(currentLength) ||
            previousLength <= 1.0e-8f || currentLength <= 1.0e-8f)
        {
            return true;
        }

        previousDirection /= MathF.Sqrt(previousLength);
        currentDirection /= MathF.Sqrt(currentLength);
        float angularRadius = MathF.Max(
            previous.SunAngularRadiusRadians,
            current.SunAngularRadiusRadians);
        float threshold = MathF.Max(
            angularRadius * 0.25f,
            0.05f * (MathF.PI / 180f));
        return Vector3.Dot(previousDirection, currentDirection) <
            MathF.Cos(threshold);
    }

    private static bool SoftSourceChanged(
        in DirectionalShadowHistoryRevision previous,
        in DirectionalShadowHistoryRevision current)
    {
        if (previous.EffectiveMode != DirectionalShadowMode.RayQuerySoft &&
            current.EffectiveMode != DirectionalShadowMode.RayQuerySoft)
        {
            return false;
        }

        float radiusScale = MathF.Max(
            MathF.Abs(previous.SunAngularRadiusRadians),
            MathF.Abs(current.SunAngularRadiusRadians));
        float tolerance = MathF.Max(1.0e-6f, radiusScale * 0.01f);
        return !float.IsFinite(previous.SunAngularRadiusRadians) ||
            !float.IsFinite(current.SunAngularRadiusRadians) ||
            MathF.Abs(previous.SunAngularRadiusRadians -
                current.SunAngularRadiusRadians) > tolerance;
    }

    private static bool DistanceChanged(float previous, float current)
    {
        if (!float.IsFinite(previous) || !float.IsFinite(current))
            return true;
        float tolerance = MathF.Max(
            0.01f,
            MathF.Max(MathF.Abs(previous), MathF.Abs(current)) * 0.001f);
        return MathF.Abs(previous - current) > tolerance;
    }
}
