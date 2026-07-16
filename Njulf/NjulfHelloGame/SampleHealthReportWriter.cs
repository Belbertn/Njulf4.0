using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal sealed class SampleHealthReportWriter
{
    private readonly RendererHealthReportWriter _writer = new();

    public void TryWrite(
        SampleSmokeOptions options,
        string? startupLogPath,
        IReadOnlyList<SampleSmokeOperationResult> operations,
        RendererDiagnostics diagnostics,
        string status,
        string? failure)
    {
        if (string.IsNullOrWhiteSpace(options.HealthReportPath))
            return;

        try
        {
            _writer.Write(options.HealthReportPath, new
            {
                kind = "renderer-health",
                timestampUtc = DateTimeOffset.UtcNow,
                status,
                failure,
                startupLogPath,
                options,
                operations,
                diagnostics
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Health report write failed: {ex.Message}");
        }
    }
}
