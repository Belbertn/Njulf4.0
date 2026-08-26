using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Core;

internal readonly record struct RendererSubmissionFault(
    string Reason,
    bool DeviceLost);

internal sealed class RendererLifetimeCoordinator
{
    private readonly object _disposalGate = new();
    private readonly string _disposedObjectName;
    private readonly RendererStartupLog? _startupLog;

    private bool _initializationSucceeded;
    private bool _frameInProgress;
    private bool _swapchainRecreationRequested;
    private bool _deviceLost;
    private bool _submissionFaulted;
    private string _submissionFaultReason = string.Empty;
    private bool _disposalStarted;
    private bool _disposalCompleted;
    private StagedDisposalPlan? _disposalPlan;
    private Result _disposalDeviceIdleResult = Result.ErrorUnknown;

    internal RendererLifetimeCoordinator(
        string disposedObjectName,
        RendererStartupLog? startupLog = null)
    {
        if (string.IsNullOrWhiteSpace(disposedObjectName))
        {
            throw new ArgumentException(
                "A disposed object name is required.",
                nameof(disposedObjectName));
        }

        _disposedObjectName = disposedObjectName;
        _startupLog = startupLog;
    }

    internal bool InitializationSucceeded => _initializationSucceeded;

    internal bool FrameInProgress => _frameInProgress;

    internal bool SwapchainRecreationRequested =>
        _swapchainRecreationRequested;

    internal bool DeviceLost => _deviceLost;

    internal bool DisposalStarted
    {
        get
        {
            lock (_disposalGate)
            {
                return _disposalStarted;
            }
        }
    }

    internal bool DisposalCompleted
    {
        get
        {
            lock (_disposalGate)
            {
                return _disposalCompleted;
            }
        }
    }

    internal Result DisposalDeviceIdleResult =>
        _disposalDeviceIdleResult;

    internal bool Initialize(Action initializeCore)
    {
        ArgumentNullException.ThrowIfNull(initializeCore);
        ThrowIfDisposalStarted();
        if (_initializationSucceeded)
            return false;

        initializeCore();
        _initializationSucceeded = true;
        return true;
    }

    internal void ThrowIfInitializationSucceeded(string message)
    {
        if (_initializationSucceeded)
            throw new InvalidOperationException(message);
    }

    internal void RunStartupStep(string name, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_startupLog?.Path == null)
        {
            action();
            return;
        }

        _startupLog.StepStarted(name);
        try
        {
            action();
            _startupLog.StepSucceeded(name);
        }
        catch (Exception exception)
        {
            _startupLog.StepFailed(name, exception);
            throw;
        }
    }

    internal void ThrowIfSubmissionFaulted()
    {
        if (!_submissionFaulted)
            return;

        string prefix = _deviceLost
            ? "The Vulkan device was lost during a frame submission."
            : "A previous frame submission failed and the renderer was stopped before unsafe resource reuse.";
        throw new InvalidOperationException(
            $"{prefix} {_submissionFaultReason}");
    }

    internal void EnsureCanBeginFrame()
    {
        if (_frameInProgress)
        {
            throw new InvalidOperationException(
                "BeginFrame was called while a frame is already in progress.");
        }
    }

    internal void MarkFrameStarted()
    {
        _frameInProgress = true;
    }

    internal void EnsureCanEndFrame()
    {
        if (!_frameInProgress)
        {
            throw new InvalidOperationException(
                "EndFrame was called without a successful BeginFrame.");
        }
    }

    internal void EnsureFrameInProgress(string operation)
    {
        if (!_frameInProgress)
        {
            throw new InvalidOperationException(
                $"{operation} requires a successful BeginFrame call.");
        }
    }

    internal void CompleteFrame()
    {
        _frameInProgress = false;
    }

    internal void AbandonFrame()
    {
        _frameInProgress = false;
    }

    internal void RequestSwapchainRecreation()
    {
        _swapchainRecreationRequested = true;
    }

    internal void ObserveSwapchainRecreationAttempt(bool succeeded)
    {
        if (succeeded)
            _swapchainRecreationRequested = false;
    }

    internal void EnsureSwapchainRecreationAllowed()
    {
        if (_frameInProgress)
        {
            throw new InvalidOperationException(
                "Swapchain cannot be recreated while command recording is in progress.");
        }
    }

    internal void RecordDeviceLoss()
    {
        _deviceLost = true;
    }

    internal RendererSubmissionFault LatchSubmissionFault(
        string? reason,
        bool deviceLost)
    {
        _deviceLost |= deviceLost;
        _submissionFaulted = true;
        _submissionFaultReason = string.IsNullOrWhiteSpace(reason)
            ? "A Vulkan frame submission failed."
            : reason;
        return new RendererSubmissionFault(
            _submissionFaultReason,
            _deviceLost);
    }

    internal bool DrainDisposal(
        Func<StagedDisposalPlan> createPlan,
        Action onDisposalStarted)
    {
        ArgumentNullException.ThrowIfNull(createPlan);
        ArgumentNullException.ThrowIfNull(onDisposalStarted);

        lock (_disposalGate)
        {
            if (_disposalCompleted)
                return false;

            if (_disposalPlan == null)
            {
                StagedDisposalPlan preparedPlan =
                    createPlan() ??
                    throw new InvalidOperationException(
                        "Renderer disposal plan creation returned null.");
                _disposalStarted = true;
                onDisposalStarted();
                _disposalPlan = preparedPlan;
            }

            Exception? failure = _disposalPlan.TryDrain();
            if (failure != null)
                throw failure;

            _disposalCompleted = _disposalPlan.IsComplete;
            if (!_disposalCompleted)
            {
                throw new InvalidOperationException(
                    "Renderer disposal returned without a failure but still has pending stages.");
            }

            return true;
        }
    }

    internal void ThrowIfDisposalStarted()
    {
        lock (_disposalGate)
        {
            if (_disposalStarted)
            {
                throw new ObjectDisposedException(
                    _disposedObjectName);
            }
        }
    }

    internal void RecordDisposalDeviceIdleResult(Result result)
    {
        _disposalDeviceIdleResult = result;
    }
}
