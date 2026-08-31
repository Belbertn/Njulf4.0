using System;

namespace Njulf.Rendering.Core;

/// <summary>
/// Separates submission ownership from frame-resource and swapchain-image
/// indices. Submission serials start at one; zero is the never-submitted
/// sentinel. Graphics submissions are ordered on one queue, so observing a
/// later serial complete also proves every earlier serial complete.
/// </summary>
internal sealed class FrameSubmissionOwnershipTracker
{
    private readonly ulong[] _frameContextOwners;
    private readonly ulong[] _acquireSemaphoreOwners;
    private ulong[] _swapchainImageOwners;
    private int[] _swapchainImageOwnerContexts;
    private int _preferredFrameContext;
    private int _nextAcquireSemaphore;
    private ulong _completedSubmissionSerial;
    private ulong _lastSubmittedSerial;

    internal FrameSubmissionOwnershipTracker(
        int frameContextCount,
        int acquireSemaphoreCount,
        int swapchainImageCount)
    {
        if (frameContextCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameContextCount));
        if (acquireSemaphoreCount <= frameContextCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acquireSemaphoreCount),
                "Acquire-first scheduling requires one semaphore beyond the maximum in-flight submission count.");
        }
        if (swapchainImageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(swapchainImageCount));

        _frameContextOwners = new ulong[frameContextCount];
        _acquireSemaphoreOwners = new ulong[acquireSemaphoreCount];
        _swapchainImageOwners = new ulong[swapchainImageCount];
        _swapchainImageOwnerContexts = new int[swapchainImageCount];
        Array.Fill(_swapchainImageOwnerContexts, -1);
    }

    internal int PreferredFrameContext => _preferredFrameContext;
    internal ulong CompletedSubmissionSerial => _completedSubmissionSerial;

    internal int SelectAcquireSemaphore()
    {
        for (int offset = 0;
             offset < _acquireSemaphoreOwners.Length;
             offset++)
        {
            int index = (_nextAcquireSemaphore + offset) %
                _acquireSemaphoreOwners.Length;
            ulong owner = _acquireSemaphoreOwners[index];
            if (owner != 0UL && owner > _completedSubmissionSerial)
                continue;

            _nextAcquireSemaphore =
                (index + 1) % _acquireSemaphoreOwners.Length;
            return index;
        }

        throw new InvalidOperationException(
            "No acquire semaphore is reusable. At most one semaphore per in-flight frame context may have an incomplete owner.");
    }

    internal SwapchainImageSubmissionOwner GetSwapchainImageOwner(
        uint imageIndex)
    {
        if (imageIndex >= _swapchainImageOwners.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(imageIndex));
        }

        ulong serial = _swapchainImageOwners[imageIndex];
        return new SwapchainImageSubmissionOwner(
            serial,
            _swapchainImageOwnerContexts[imageIndex],
            serial == 0UL || serial <= _completedSubmissionSerial);
    }

    internal FrameResourceContextSelection SelectFrameResourceContext(
        Func<int, bool> isFenceSignaled)
    {
        ArgumentNullException.ThrowIfNull(isFenceSignaled);

        int oldestContext = -1;
        ulong oldestSerial = ulong.MaxValue;
        for (int offset = 0; offset < _frameContextOwners.Length; offset++)
        {
            int context = (_preferredFrameContext + offset) %
                _frameContextOwners.Length;
            ulong owner = _frameContextOwners[context];
            if (owner == 0UL || owner <= _completedSubmissionSerial)
            {
                return new FrameResourceContextSelection(
                    context,
                    owner,
                    RequiresWait: false);
            }

            if (isFenceSignaled(context))
            {
                ObserveContextCompleted(context);
                return new FrameResourceContextSelection(
                    context,
                    owner,
                    RequiresWait: false);
            }

            if (owner < oldestSerial)
            {
                oldestSerial = owner;
                oldestContext = context;
            }
        }

        if (oldestContext < 0)
        {
            throw new InvalidOperationException(
                "No frame-resource context has an owner or reusable fence state.");
        }

        return new FrameResourceContextSelection(
            oldestContext,
            oldestSerial,
            RequiresWait: true);
    }

    internal ulong ObserveContextCompleted(int frameContext)
    {
        ValidateFrameContext(frameContext);
        ulong serial = _frameContextOwners[frameContext];
        _completedSubmissionSerial = Math.Max(
            _completedSubmissionSerial,
            serial);
        return serial;
    }

    internal void MarkSubmitted(
        int frameContext,
        uint swapchainImageIndex,
        int acquireSemaphoreIndex,
        ulong submissionSerial)
    {
        ValidateFrameContext(frameContext);
        if (swapchainImageIndex >= _swapchainImageOwners.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(swapchainImageIndex));
        }
        if ((uint)acquireSemaphoreIndex >=
            (uint)_acquireSemaphoreOwners.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acquireSemaphoreIndex));
        }
        if (submissionSerial == 0UL ||
            submissionSerial <= _lastSubmittedSerial)
        {
            throw new InvalidOperationException(
                "Graphics submission serials must be non-zero and strictly increasing.");
        }
        if (_frameContextOwners[frameContext] >
            _completedSubmissionSerial)
        {
            throw new InvalidOperationException(
                "A frame-resource context cannot be reassigned before its prior submission completes.");
        }
        if (_acquireSemaphoreOwners[acquireSemaphoreIndex] >
            _completedSubmissionSerial)
        {
            throw new InvalidOperationException(
                "An acquire semaphore cannot be reassigned before its consuming submission completes.");
        }

        _frameContextOwners[frameContext] = submissionSerial;
        _swapchainImageOwners[swapchainImageIndex] = submissionSerial;
        _swapchainImageOwnerContexts[swapchainImageIndex] = frameContext;
        _acquireSemaphoreOwners[acquireSemaphoreIndex] = submissionSerial;
        _lastSubmittedSerial = submissionSerial;
        _preferredFrameContext =
            (frameContext + 1) % _frameContextOwners.Length;
    }

    /// <summary>
    /// Rebuilds image ownership only after the caller has completed a device
    /// idle boundary. Frame-context serials remain as diagnostic history but
    /// are all immediately reusable.
    /// </summary>
    internal void ResetAfterDeviceIdle(int swapchainImageCount)
    {
        if (swapchainImageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(swapchainImageCount));

        _completedSubmissionSerial = Math.Max(
            _completedSubmissionSerial,
            _lastSubmittedSerial);
        Array.Clear(_acquireSemaphoreOwners);
        _swapchainImageOwners = new ulong[swapchainImageCount];
        _swapchainImageOwnerContexts = new int[swapchainImageCount];
        Array.Fill(_swapchainImageOwnerContexts, -1);
        _nextAcquireSemaphore = 0;
    }

    internal void ObserveAllSubmittedCompleted()
    {
        _completedSubmissionSerial = Math.Max(
            _completedSubmissionSerial,
            _lastSubmittedSerial);
    }

    private void ValidateFrameContext(int frameContext)
    {
        if ((uint)frameContext >= (uint)_frameContextOwners.Length)
            throw new ArgumentOutOfRangeException(nameof(frameContext));
    }
}

internal readonly record struct SwapchainImageSubmissionOwner(
    ulong SubmissionSerial,
    int FrameContext,
    bool Completed);

internal readonly record struct FrameResourceContextSelection(
    int FrameContext,
    ulong PreviousSubmissionSerial,
    bool RequiresWait);
