using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Njulf.Core.Interfaces;

namespace Njulf.Rendering.Diagnostics;

public sealed class RendererStartupLog : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Stopwatch> _activeSteps = new(StringComparer.Ordinal);
    private StreamWriter? _writer;
    private bool _disposed;
    private RendererStartupPhase? _lastSnapshotPhase;
    private long _lastSnapshotElapsedMicroseconds = long.MinValue;

    private const long SnapshotHeartbeatMicroseconds = 10_000_000L;

    public RendererStartupLog(string? path, IEnumerable<string>? commandLineArgs = null)
    {
        Path = RendererValidationSettings.NormalizeOptionalPath(path);
        if (Path == null)
            return;

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };

            WriteObject(new
            {
                kind = "header",
                timestampUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId,
                osVersion = Environment.OSVersion.VersionString,
                dotnetVersion = Environment.Version.ToString(),
                executablePath = Environment.ProcessPath,
                workingDirectory = Environment.CurrentDirectory,
                commandLineArgs = commandLineArgs ?? Environment.GetCommandLineArgs(),
                rendererValidation = Environment.GetEnvironmentVariable("NJULF_RENDERER_VALIDATION"),
                rendererSmokeMode = Environment.GetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE"),
                rendererStartupLog = Path,
                rendererHealthReport = Environment.GetEnvironmentVariable("NJULF_RENDERER_HEALTH_REPORT")
            });
        }
        catch (Exception ex)
        {
            StartupWarning = $"Startup log path '{Path}' could not be opened: {ex.Message}";
            _writer = null;
        }
    }

    public string? Path { get; }
    public string? StartupWarning { get; private set; }

    public void StepStarted(string name, string? detail = null)
    {
        lock (_sync)
        {
            _activeSteps[name] = Stopwatch.StartNew();
            WriteStep(new RendererStartupStep(
                name,
                RendererStartupStepStatus.Started,
                DateTimeOffset.UtcNow,
                0,
                detail,
                null,
                null,
                null));
        }
    }

    public void StepSucceeded(string name, string? detail = null)
    {
        lock (_sync)
        {
            long elapsed = StopElapsedMicroseconds(name);
            WriteStep(new RendererStartupStep(
                name,
                RendererStartupStepStatus.Succeeded,
                DateTimeOffset.UtcNow,
                elapsed,
                detail,
                null,
                null,
                null));
        }
    }

    public void StepFailed(string name, Exception exception, string? detail = null, string? vulkanResult = null)
    {
        lock (_sync)
        {
            long elapsed = StopElapsedMicroseconds(name);
            WriteStep(new RendererStartupStep(
                name,
                RendererStartupStepStatus.Failed,
                DateTimeOffset.UtcNow,
                elapsed,
                detail,
                exception.GetType().FullName,
                exception.Message,
                vulkanResult));
            _writer?.Flush();
        }
    }

    public void DeviceSelected(DeviceRequirementReport report)
    {
        lock (_sync)
        {
            WriteObject(new
            {
                kind = "device",
                timestampUtc = DateTimeOffset.UtcNow,
                report
            });
        }
    }

    public void WriteFailure(RendererFailureReport report)
    {
        lock (_sync)
        {
            WriteObject(new
            {
                kind = "failure",
                timestampUtc = DateTimeOffset.UtcNow,
                report
            });
            _writer?.Flush();
        }
    }

    /// <summary>
    /// Records phase transitions and a bounded ten-second heartbeat while a
    /// progressive startup phase is active. The immutable snapshot is written
    /// as one JSON object so pipeline progress and the active native creation
    /// identities cannot be sampled from different instants.
    /// </summary>
    public void WriteSnapshot(in RendererStartupSnapshot snapshot)
    {
        lock (_sync)
        {
            bool phaseChanged = _lastSnapshotPhase != snapshot.Phase;
            bool heartbeatDue = _lastSnapshotElapsedMicroseconds == long.MinValue ||
                snapshot.ElapsedMicroseconds - _lastSnapshotElapsedMicroseconds >=
                SnapshotHeartbeatMicroseconds;
            if (!phaseChanged && !heartbeatDue)
                return;

            _lastSnapshotPhase = snapshot.Phase;
            _lastSnapshotElapsedMicroseconds = snapshot.ElapsedMicroseconds;
            WriteObject(new
            {
                kind = "snapshot",
                timestampUtc = DateTimeOffset.UtcNow,
                phase = snapshot.Phase,
                elapsedMicroseconds = snapshot.ElapsedMicroseconds,
                phaseElapsedMicroseconds = snapshot.PhaseElapsedMicroseconds,
                bootstrapPresented = snapshot.BootstrapPresented,
                scenePresented = snapshot.ScenePresented,
                fullQualityPresented = snapshot.FullQualityPresented,
                pipelinesCompleted = snapshot.PipelinesCompleted,
                activePipelineCount = snapshot.ActivePipelineCount,
                oldestActivePipelineMicroseconds =
                    snapshot.OldestActivePipelineMicroseconds,
                activePipelineSummary = snapshot.ActivePipelineSummary,
                detail = snapshot.Detail
            });
        }
    }

    private long StopElapsedMicroseconds(string name)
    {
        if (!_activeSteps.Remove(name, out Stopwatch? stopwatch))
            return 0;

        stopwatch.Stop();
        return stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
    }

    private void WriteStep(RendererStartupStep step)
    {
        WriteObject(new
        {
            kind = "step",
            step
        });
    }

    private void WriteObject<T>(T value)
    {
        if (_writer == null || _disposed)
            return;

        _writer.WriteLine(JsonSerializer.Serialize(value, RendererHealthReportWriter.JsonOptions));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _writer?.Dispose();
    }
}
