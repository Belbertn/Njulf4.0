using System;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Memory;

/// <summary>
/// A concrete descriptor-ready range in one generation of the advanced-GI
/// transient storage arena.  The arena owns <see cref="Buffer"/>; consumers
/// own only the range and must never destroy the backing handle.
/// </summary>
internal readonly record struct AdvancedGiTransientBufferSlice(
    SimpleDdgiAdvancedMemoryCategory Category,
    BufferHandle Buffer,
    ulong NativeBufferHandle,
    ulong Offset,
    ulong Bytes,
    ulong ArenaGeneration,
    ulong LayoutFingerprint)
{
    public bool IsValid =>
        Buffer.IsValid && NativeBufferHandle != 0UL && Bytes != 0UL &&
        ArenaGeneration != 0UL && LayoutFingerprint != 0UL;
}

internal readonly record struct AdvancedGiTransientBufferArenaDiagnostics(
    bool Allocated,
    ulong AllocatedBytes,
    ulong PeakLiveBytes,
    ulong UnaliasedBytes,
    ulong AliasedBytesSaved,
    ulong PlacementOverheadBytes,
    ulong ArenaGeneration,
    ulong LayoutFingerprint,
    int SliceCount,
    string State)
{
    public static AdvancedGiTransientBufferArenaDiagnostics Disabled { get; } =
        new(false, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL,
            GiExperimentScratchAliasing.EmptyLayoutFingerprint, 0, "disabled");
}

/// <summary>
/// Small allocation boundary used by lifecycle tests.  Production uses the
/// BufferManager-backed implementation below; the interface does not expose a
/// map operation because all arena slices are device-local transient storage.
/// </summary>
internal interface IAdvancedGiTransientBufferArenaBackend
{
    BufferHandle Allocate(ulong bytes, bool requireDeviceAddress);

    void Retire(BufferHandle buffer);

    ulong GetNativeHandle(BufferHandle buffer);

    ulong GetSize(BufferHandle buffer);
}

/// <summary>
/// Transactional owner of a single physical buffer containing non-overlapping
/// advanced-GI scratch slices.  Replacement allocates first, waits for every
/// prior descriptor reader, then publishes and retires the old buffer.  A
/// failed allocation or wait leaves the previous generation untouched.
/// </summary>
internal sealed class AdvancedGiTransientBufferArena : IDisposable
{
    private readonly IAdvancedGiTransientBufferArenaBackend _backend;
    private readonly Action _waitForReaders;
    private GiExperimentScratchArenaPlan _plan =
        GiExperimentScratchArenaPlan.Empty;
    private BufferHandle _buffer = BufferHandle.Invalid;
    private ulong _generation;
    private bool _disposed;

    public AdvancedGiTransientBufferArena(
        BufferManager bufferManager,
        Action waitForReaders)
        : this(
            new BufferManagerBackend(bufferManager ??
                throw new ArgumentNullException(nameof(bufferManager))),
            waitForReaders)
    {
    }

    internal AdvancedGiTransientBufferArena(
        IAdvancedGiTransientBufferArenaBackend backend,
        Action waitForReaders)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _waitForReaders = waitForReaders ??
            throw new ArgumentNullException(nameof(waitForReaders));
    }

    public AdvancedGiTransientBufferArenaDiagnostics Diagnostics
    {
        get
        {
            if (!_buffer.IsValid)
                return AdvancedGiTransientBufferArenaDiagnostics.Disabled;

            return new AdvancedGiTransientBufferArenaDiagnostics(
                Allocated: true,
                AllocatedBytes: _plan.RequiredBytes,
                PeakLiveBytes: _plan.PeakLiveBytes,
                UnaliasedBytes: _plan.UnaliasedBytes,
                AliasedBytesSaved: _plan.AliasedBytesSaved,
                PlacementOverheadBytes: _plan.PlacementOverheadBytes,
                ArenaGeneration: _generation,
                LayoutFingerprint: _plan.LayoutFingerprint,
                SliceCount: _plan.Slices.Count,
                State: "ready");
        }
    }

    public bool TryReconcile(
        GiExperimentScratchArenaPlan plan,
        ulong maximumBufferBytes,
        ulong availableMemoryHeadroomBytes,
        out string failure)
    {
        ThrowIfDisposed();
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        if (!ValidateBufferCompatibility(plan, out failure))
            return false;
        if (plan.RequiredBytes == 0UL)
            return TryDisable(out failure);
        if (maximumBufferBytes == 0UL || plan.RequiredBytes > maximumBufferBytes)
        {
            failure = "advanced-gi-transient-arena-maximum-buffer-range-exceeded";
            return false;
        }
        if (plan.RequiredBytes > availableMemoryHeadroomBytes)
        {
            failure = "advanced-gi-transient-arena-memory-headroom-exceeded";
            return false;
        }
        if (_buffer.IsValid &&
            _plan.LayoutFingerprint == plan.LayoutFingerprint &&
            _plan.RequiredBytes == plan.RequiredBytes)
        {
            failure = string.Empty;
            return true;
        }
        if (!TryGetNextGeneration(_generation, out ulong nextGeneration))
        {
            failure = "advanced-gi-transient-arena-generation-exhausted";
            return false;
        }

        BufferHandle replacement = BufferHandle.Invalid;
        try
        {
            replacement = _backend.Allocate(
                plan.RequiredBytes,
                RequiresDeviceAddress(plan));
            if (!replacement.IsValid ||
                _backend.GetSize(replacement) != plan.RequiredBytes ||
                _backend.GetNativeHandle(replacement) == 0UL)
            {
                if (replacement.IsValid)
                    _backend.Retire(replacement);
                failure = "advanced-gi-transient-arena-backend-returned-invalid-buffer";
                return false;
            }
        }
        catch (Exception exception)
        {
            if (replacement.IsValid)
            {
                try
                {
                    _backend.Retire(replacement);
                }
                catch
                {
                    // Preserve the primary allocation failure. A production
                    // backend retirement failure is diagnosed by its owner.
                }
            }
            failure = "advanced-gi-transient-arena-allocation-failed:" +
                exception.GetType().Name;
            return false;
        }

        BufferHandle prior = _buffer;
        try
        {
            if (prior.IsValid)
                _waitForReaders();
        }
        catch (Exception exception)
        {
            _backend.Retire(replacement);
            failure = "advanced-gi-transient-arena-reader-wait-failed:" +
                exception.GetType().Name;
            return false;
        }

        _buffer = replacement;
        _plan = plan;
        _generation = nextGeneration;
        if (prior.IsValid)
            _backend.Retire(prior);
        failure = string.Empty;
        return true;
    }

    public bool TryGetSlice(
        SimpleDdgiAdvancedMemoryCategory category,
        ulong exactBytes,
        ulong requiredAlignment,
        out AdvancedGiTransientBufferSlice slice,
        out string failure)
    {
        ThrowIfDisposed();
        if (!_buffer.IsValid)
        {
            slice = default;
            failure = "advanced-gi-transient-arena-is-not-allocated";
            return false;
        }
        if (!_plan.TryGetSlice(category, out GiExperimentScratchSlice planned))
        {
            slice = default;
            failure = "advanced-gi-transient-arena-category-is-not-planned";
            return false;
        }
        if (exactBytes == 0UL || planned.Bytes != exactBytes)
        {
            slice = default;
            failure = "advanced-gi-transient-arena-slice-size-mismatch";
            return false;
        }
        if (!IsPowerOfTwo(requiredAlignment) ||
            planned.Offset % requiredAlignment != 0UL ||
            planned.Alignment < requiredAlignment)
        {
            slice = default;
            failure = "advanced-gi-transient-arena-slice-alignment-mismatch";
            return false;
        }
        if (planned.EndExclusive > _backend.GetSize(_buffer))
        {
            slice = default;
            failure = "advanced-gi-transient-arena-slice-range-exceeds-buffer";
            return false;
        }

        slice = new AdvancedGiTransientBufferSlice(
            category,
            _buffer,
            _backend.GetNativeHandle(_buffer),
            planned.Offset,
            planned.Bytes,
            _generation,
            _plan.LayoutFingerprint);
        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns true only while <paramref name="slice"/> still names the exact
    /// live arena allocation and placement from which it was captured. Arena
    /// slices are borrowed views; syntactic <see cref="BufferHandle.IsValid"/>
    /// does not make a view live after the arena has replaced its backing
    /// buffer.
    /// </summary>
    public bool IsCurrent(in AdvancedGiTransientBufferSlice slice)
    {
        if (_disposed || !_buffer.IsValid || !slice.IsValid ||
            slice.Buffer != _buffer || slice.ArenaGeneration != _generation ||
            slice.LayoutFingerprint != _plan.LayoutFingerprint ||
            !_plan.TryGetSlice(slice.Category, out GiExperimentScratchSlice planned))
        {
            return false;
        }

        return slice.NativeBufferHandle == _backend.GetNativeHandle(_buffer) &&
            slice.Offset == planned.Offset && slice.Bytes == planned.Bytes;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_buffer.IsValid)
            return;

        _waitForReaders();
        _backend.Retire(_buffer);
        _buffer = BufferHandle.Invalid;
        _plan = GiExperimentScratchArenaPlan.Empty;
        _generation = 0UL;
    }

    private bool TryDisable(out string failure)
    {
        if (!_buffer.IsValid)
        {
            _plan = GiExperimentScratchArenaPlan.Empty;
            _generation = 0UL;
            failure = string.Empty;
            return true;
        }

        try
        {
            _waitForReaders();
        }
        catch (Exception exception)
        {
            failure = "advanced-gi-transient-arena-reader-wait-failed:" +
                exception.GetType().Name;
            return false;
        }

        _backend.Retire(_buffer);
        _buffer = BufferHandle.Invalid;
        _plan = GiExperimentScratchArenaPlan.Empty;
        _generation = 0UL;
        failure = string.Empty;
        return true;
    }

    private static bool ValidateBufferCompatibility(
        GiExperimentScratchArenaPlan plan,
        out string failure)
    {
        for (int index = 0; index < plan.Slices.Count; index++)
        {
            SimpleDdgiAdvancedMemoryCategory category = plan.Slices[index].Category;
            if (category is not (
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch or
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch or
                SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch or
                SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch))
            {
                failure = "advanced-gi-transient-arena-category-is-not-buffer-compatible";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static bool RequiresDeviceAddress(GiExperimentScratchArenaPlan plan) =>
        plan.TryGetSlice(
            SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch,
            out _);

    private static bool TryGetNextGeneration(ulong current, out ulong next)
    {
        if (current == ulong.MaxValue)
        {
            next = 0UL;
            return false;
        }

        next = current + 1UL;
        if (next == 0UL)
            next = 1UL;
        return true;
    }

    private static bool IsPowerOfTwo(ulong value) =>
        value != 0UL && (value & (value - 1UL)) == 0UL;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AdvancedGiTransientBufferArena));
    }

    private sealed class BufferManagerBackend : IAdvancedGiTransientBufferArenaBackend
    {
        private readonly BufferManager _bufferManager;

        public BufferManagerBackend(BufferManager bufferManager)
        {
            _bufferManager = bufferManager;
        }

        public BufferHandle Allocate(ulong bytes, bool requireDeviceAddress) =>
            _bufferManager.CreateDeviceBuffer(
                bytes,
                BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferSrcBit |
                    BufferUsageFlags.TransferDstBit,
                requireDeviceAddress,
                MemoryBudgetCategory.GlobalIllumination,
                "Advanced GI Transient Buffer Arena");

        public void Retire(BufferHandle buffer) =>
            _bufferManager.DestroyBuffer(buffer);

        public ulong GetNativeHandle(BufferHandle buffer) =>
            _bufferManager.GetBuffer(buffer).Handle;

        public ulong GetSize(BufferHandle buffer) =>
            _bufferManager.GetBufferSize(buffer);
    }
}
