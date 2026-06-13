using System;

namespace Njulf.Rendering.Diagnostics;

public sealed record RendererFailureReport(
    string OperationName,
    string? LastSuccessfulStep,
    string ExceptionType,
    string ExceptionMessage,
    string? StartupLogPath)
{
    public static RendererFailureReport FromException(
        string operationName,
        string? lastSuccessfulStep,
        Exception exception,
        string? startupLogPath)
    {
        return new RendererFailureReport(
            operationName,
            lastSuccessfulStep,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            startupLogPath);
    }
}
