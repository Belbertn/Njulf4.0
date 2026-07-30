using System;
using System.Threading;

namespace Njulf.Rendering.Diagnostics;

public enum RendererValidationMessageSeverity : byte
{
    Verbose,
    Information,
    Warning,
    Error
}

public sealed record RendererValidationMessageSnapshot(
    int VerboseCount,
    int InformationCount,
    int WarningCount,
    int ErrorCount,
    string FirstWarningMessage,
    string LastWarningMessage,
    string FirstErrorMessage,
    string LastErrorMessage)
{
    public static RendererValidationMessageSnapshot Empty { get; } = new(
        VerboseCount: 0,
        InformationCount: 0,
        WarningCount: 0,
        ErrorCount: 0,
        FirstWarningMessage: string.Empty,
        LastWarningMessage: string.Empty,
        FirstErrorMessage: string.Empty,
        LastErrorMessage: string.Empty);

    public int TotalCount => SaturatingAdd(
        SaturatingAdd(VerboseCount, InformationCount),
        SaturatingAdd(WarningCount, ErrorCount));

    private static int SaturatingAdd(int left, int right)
    {
        long result = (long)left + right;
        return result >= int.MaxValue ? int.MaxValue : (int)result;
    }
}

/// <summary>
/// Thread-safe session state populated by the Vulkan debug-utils callback.
/// Callback threads only increment counters and retain bounded error text;
/// exceptions are raised later at a managed frame boundary.
/// </summary>
public sealed class RendererValidationMessageState
{
    private const int MaximumRetainedMessageLength = 4096;

    private readonly object _messageLock = new();
    private long _verboseCount;
    private long _informationCount;
    private long _warningCount;
    private long _errorCount;
    private string _firstWarningMessage = string.Empty;
    private string _lastWarningMessage = string.Empty;
    private string _firstErrorMessage = string.Empty;
    private string _lastErrorMessage = string.Empty;

    public void Record(RendererValidationMessageSeverity severity, string? message)
    {
        switch (severity)
        {
            case RendererValidationMessageSeverity.Verbose:
                Interlocked.Increment(ref _verboseCount);
                break;
            case RendererValidationMessageSeverity.Information:
                Interlocked.Increment(ref _informationCount);
                break;
            case RendererValidationMessageSeverity.Warning:
                Interlocked.Increment(ref _warningCount);
                string retainedWarning = RetainBoundedMessage(message);
                lock (_messageLock)
                {
                    if (_firstWarningMessage.Length == 0)
                        _firstWarningMessage = retainedWarning;
                    _lastWarningMessage = retainedWarning;
                }
                break;
            case RendererValidationMessageSeverity.Error:
                Interlocked.Increment(ref _errorCount);
                string retainedMessage = RetainBoundedMessage(message);
                lock (_messageLock)
                {
                    if (_firstErrorMessage.Length == 0)
                        _firstErrorMessage = retainedMessage;
                    _lastErrorMessage = retainedMessage;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown validation-message severity.");
        }
    }

    public RendererValidationMessageSnapshot Snapshot()
    {
        string firstWarningMessage;
        string lastWarningMessage;
        string firstErrorMessage;
        string lastErrorMessage;
        lock (_messageLock)
        {
            firstWarningMessage = _firstWarningMessage;
            lastWarningMessage = _lastWarningMessage;
            firstErrorMessage = _firstErrorMessage;
            lastErrorMessage = _lastErrorMessage;
        }

        return new RendererValidationMessageSnapshot(
            SaturateToInt(Interlocked.Read(ref _verboseCount)),
            SaturateToInt(Interlocked.Read(ref _informationCount)),
            SaturateToInt(Interlocked.Read(ref _warningCount)),
            SaturateToInt(Interlocked.Read(ref _errorCount)),
            firstWarningMessage,
            lastWarningMessage,
            firstErrorMessage,
            lastErrorMessage);
    }

    public void ThrowIfErrorRequested(bool failOnErrorMessage)
    {
        if (!failOnErrorMessage)
            return;

        RendererValidationMessageSnapshot snapshot = Snapshot();
        if (snapshot.ErrorCount == 0)
            return;

        string firstError = string.IsNullOrWhiteSpace(snapshot.FirstErrorMessage)
            ? "No validation message text was supplied."
            : snapshot.FirstErrorMessage;
        throw new RendererValidationException(
            $"Vulkan validation emitted {snapshot.ErrorCount} error message(s). First error: {firstError}");
    }

    private static string RetainBoundedMessage(string? message)
    {
        string normalized = string.IsNullOrWhiteSpace(message)
            ? "No validation message text was supplied."
            : message.Trim();
        return normalized.Length <= MaximumRetainedMessageLength
            ? normalized
            : normalized[..MaximumRetainedMessageLength];
    }

    private static int SaturateToInt(long value)
    {
        if (value <= 0)
            return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}

public sealed class RendererValidationException : InvalidOperationException
{
    public RendererValidationException(string message)
        : base(message)
    {
    }
}
