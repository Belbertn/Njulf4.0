using System;

namespace Njulf.Rendering.Diagnostics;

public sealed record RendererStartupStep(
    string Name,
    RendererStartupStepStatus Status,
    DateTimeOffset TimestampUtc,
    long ElapsedMicroseconds,
    string? Detail,
    string? ExceptionType,
    string? ExceptionMessage,
    string? VulkanResult);
