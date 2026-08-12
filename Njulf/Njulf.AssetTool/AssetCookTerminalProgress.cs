using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Assets.Cooked;

namespace Njulf.AssetTool;

internal enum AssetCookTerminalProgressMode
{
    Plain,
    JsonLines,
    Off
}

internal enum AssetCookTerminalProgressDetail
{
    Stages,
    Items
}

/// <summary>
/// Renders typed cooker events as line-oriented terminal records. It owns
/// presentation and heartbeat timing only; it never calls back into cooking.
/// </summary>
internal sealed class AssetCookTerminalProgress : IAssetCookProgressSink, IDisposable
{
    private const int JsonSchemaVersion = 1;
    private readonly object _gate = new();
    private readonly TextWriter _writer;
    private readonly AssetCookTerminalProgressMode _mode;
    private readonly AssetCookTerminalProgressDetail _detail;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _heartbeatInterval;
    private readonly Timer? _heartbeatTimer;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    private long _sequence;
    private long _lastEmissionMilliseconds;
    private long? _activeStageStartedMilliseconds;
    private AssetCookStage? _activeStage;
    private AssetCookProgressEvent? _latest;
    private string? _sourceRoot;
    private string? _outputRoot;
    private bool _terminated;
    private bool _writerFailed;

    public AssetCookTerminalProgress(
        TextWriter writer,
        AssetCookTerminalProgressMode mode,
        AssetCookTerminalProgressDetail detail,
        TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _mode = mode;
        _detail = detail;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(10);
        if (_heartbeatInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

        if (_mode != AssetCookTerminalProgressMode.Off)
        {
            // Wake frequently enough to respect an arbitrary test interval
            // while normal production still emits only after ten silent seconds.
            TimeSpan poll = TimeSpan.FromMilliseconds(
                Math.Max(100, Math.Min(1_000, _heartbeatInterval.TotalMilliseconds / 4)));
            _heartbeatTimer = new Timer(
                static state => ((AssetCookTerminalProgress)state!).OnHeartbeatTimer(),
                this,
                poll,
                poll);
        }
    }

    public string RunId => _runId;

    public void Report(AssetCookProgressEvent progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_mode == AssetCookTerminalProgressMode.Off)
            return;

        lock (_gate)
        {
            if (_terminated || _writerFailed)
                return;

            if (progress.Kind == AssetCookProgressEventKind.RunStarted)
            {
                _sourceRoot = TryFullPath(progress.SourcePath);
                _outputRoot = TryFullPath(progress.OutputPath);
            }

            _latest = progress;
            if (progress.Kind == AssetCookProgressEventKind.StageStarted)
            {
                _activeStageStartedMilliseconds = _clock.ElapsedMilliseconds;
                _activeStage = progress.Stage;
            }
            else if (progress.Kind == AssetCookProgressEventKind.StageCompleted)
            {
                _activeStageStartedMilliseconds = null;
                _activeStage = null;
            }

            if (ShouldRender(progress.Kind))
                WriteLocked(progress, EventName(progress.Kind));

            if (IsTerminalRunEvent(progress.Kind))
            {
                _terminated = true;
                _heartbeatTimer?.Dispose();
            }
        }
    }

    private void OnHeartbeatTimer()
    {
        if (_mode == AssetCookTerminalProgressMode.Off)
            return;

        lock (_gate)
        {
            if (_terminated || _writerFailed || _latest is null)
                return;

            long now = _clock.ElapsedMilliseconds;
            if (now - _lastEmissionMilliseconds < _heartbeatInterval.TotalMilliseconds)
                return;

            AssetCookProgressEvent current = _latest;
            long? stageElapsed = _activeStageStartedMilliseconds is { } started
                ? now - started
                : null;
            var heartbeat = new AssetCookProgressEvent(AssetCookProgressEventKind.StageStarted)
            {
                SourcePath = current.SourcePath,
                AssetIndex = current.AssetIndex,
                AssetCount = current.AssetCount,
                Stage = _activeStage ?? current.Stage,
                ItemIndex = current.ItemIndex,
                ItemCount = current.ItemCount,
                ItemName = current.ItemName,
                StageElapsedMilliseconds = stageElapsed,
                TotalElapsedMilliseconds = now
            };
            WriteLocked(heartbeat, "heartbeat");
        }
    }

    private bool ShouldRender(AssetCookProgressEventKind kind) =>
        _detail == AssetCookTerminalProgressDetail.Items ||
        kind is not AssetCookProgressEventKind.MaterialStarted and
            not AssetCookProgressEventKind.MaterialCompleted and
            not AssetCookProgressEventKind.TextureStarted and
            not AssetCookProgressEventKind.TextureCompleted;

    private void WriteLocked(AssetCookProgressEvent progress, string eventName)
    {
        try
        {
            if (_mode == AssetCookTerminalProgressMode.JsonLines)
                WriteJsonLine(progress, eventName);
            else
                WritePlainLine(progress, eventName);
            _writer.Flush();
            _lastEmissionMilliseconds = _clock.ElapsedMilliseconds;
        }
        catch
        {
            // Logging must never make a package transaction fail. Stop the
            // timer as well so a broken redirected stderr is not repeatedly hit.
            _writerFailed = true;
            _heartbeatTimer?.Dispose();
        }
    }

    private void WritePlainLine(AssetCookProgressEvent progress, string eventName)
    {
        var line = new StringBuilder("[assetcook] ");
        line.Append(eventName);
        AppendPlainToken(line, "run_id", _runId);
        if (progress.AssetIndex.HasValue && progress.AssetCount.HasValue)
            AppendPlainToken(line, "asset", $"{progress.AssetIndex}/{progress.AssetCount}");
        AppendPlainPath(line, "source", progress.SourcePath, _sourceRoot);
        AppendPlainPath(line, "output", progress.OutputPath, _outputRoot);
        AppendPlainToken(line, "mode", progress.CookMode);
        AppendPlainEnum(line, "stage", progress.Stage, ToKebabCase);
        AppendPlainEnum(line, "outcome", progress.Outcome, ToKebabCase);
        AppendPlainEnum(line, "decision", progress.IncrementalDecision, ToKebabCase);
        AppendPlainEnum(line, "reason", progress.IncrementalReason, ToKebabCase);
        if (progress.ItemIndex.HasValue && progress.ItemCount.HasValue)
            AppendPlain(line, "slot", $"{progress.ItemIndex}/{progress.ItemCount}");
        AppendPlain(line, "item", progress.ItemName);
        AppendPlain(line, "materials", progress.MaterialCount);
        AppendPlain(line, "texture_slots", progress.TextureSlotCount);
        AppendPlain(line, "meshes", progress.MeshCount);
        AppendPlain(line, "textures", progress.TextureCount);
        AppendPlain(line, "warnings", progress.WarningCount);
        AppendPlain(line, "cooked", progress.CookedCount);
        AppendPlain(line, "skipped", progress.SkippedCount);
        AppendPlain(line, "failed", progress.FailedCount);
        AppendPlainToken(line, "backend", progress.Backend);
        AppendPlain(line, "jobs", progress.Jobs);
        AppendPlain(line, "max_inflight_bytes", progress.MaxInflightBytes);
        AppendPlain(line, "stage_elapsed_ms", progress.StageElapsedMilliseconds);
        AppendPlain(line, "item_elapsed_ms", progress.ItemElapsedMilliseconds);
        AppendPlain(line, "asset_elapsed_ms", progress.AssetElapsedMilliseconds);
        AppendPlain(line, "total_elapsed_ms", progress.TotalElapsedMilliseconds ?? _clock.ElapsedMilliseconds);
        AppendPlain(line, "message", progress.Message);
        _writer.Write(line);
        _writer.Write('\n');
    }

    private void WriteJsonLine(AssetCookProgressEvent progress, string eventName)
    {
        var fields = new Dictionary<string, object?>
        {
            ["schema"] = JsonSchemaVersion,
            ["runId"] = _runId,
            ["sequence"] = ++_sequence,
            ["event"] = eventName,
            ["sourcePath"] = progress.SourcePath,
            ["outputPath"] = progress.OutputPath,
            ["mode"] = progress.CookMode,
            ["assetIndex"] = progress.AssetIndex,
            ["assetCount"] = progress.AssetCount,
            ["stage"] = progress.Stage is { } stage ? ToKebabCase(stage) : null,
            ["outcome"] = progress.Outcome is { } outcome ? ToKebabCase(outcome) : null,
            ["decision"] = progress.IncrementalDecision is { } decision ? ToKebabCase(decision) : null,
            ["reason"] = progress.IncrementalReason is { } reason ? ToKebabCase(reason) : null,
            ["itemIndex"] = progress.ItemIndex,
            ["itemCount"] = progress.ItemCount,
            ["itemName"] = progress.ItemName,
            ["materialCount"] = progress.MaterialCount,
            ["textureSlotCount"] = progress.TextureSlotCount,
            ["meshCount"] = progress.MeshCount,
            ["textureCount"] = progress.TextureCount,
            ["warningCount"] = progress.WarningCount,
            ["cookedCount"] = progress.CookedCount,
            ["skippedCount"] = progress.SkippedCount,
            ["failedCount"] = progress.FailedCount,
            ["backend"] = progress.Backend,
            ["jobs"] = progress.Jobs,
            ["maxInflightBytes"] = progress.MaxInflightBytes,
            ["stageElapsedMs"] = progress.StageElapsedMilliseconds,
            ["itemElapsedMs"] = progress.ItemElapsedMilliseconds,
            ["assetElapsedMs"] = progress.AssetElapsedMilliseconds,
            ["totalElapsedMs"] = progress.TotalElapsedMilliseconds ?? _clock.ElapsedMilliseconds,
            ["message"] = progress.Message
        };
        Dictionary<string, object?> populated = fields
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Key, field => field.Value);
        string json = JsonSerializer.Serialize(populated, _jsonOptions);
        _writer.Write(json);
        _writer.Write('\n');
    }

    private static bool IsTerminalRunEvent(AssetCookProgressEventKind kind) =>
        kind is AssetCookProgressEventKind.RunCompleted or
            AssetCookProgressEventKind.RunFailed or
            AssetCookProgressEventKind.RunCancelled;

    private static string EventName(AssetCookProgressEventKind kind) => kind switch
    {
        AssetCookProgressEventKind.RunStarted => "run.start",
        AssetCookProgressEventKind.RunCompleted => "run.done",
        AssetCookProgressEventKind.RunFailed => "run.failed",
        AssetCookProgressEventKind.RunCancelled => "run.cancelled",
        AssetCookProgressEventKind.DiscoveryStarted => "discovery.start",
        AssetCookProgressEventKind.DiscoveryCompleted => "discovery.done",
        AssetCookProgressEventKind.AssetStarted => "asset.start",
        AssetCookProgressEventKind.AssetSkipped => "asset.skipped",
        AssetCookProgressEventKind.AssetCompleted => "asset.done",
        AssetCookProgressEventKind.AssetFailed => "asset.failed",
        AssetCookProgressEventKind.AssetCancelled => "asset.cancelled",
        AssetCookProgressEventKind.StageStarted => "stage.start",
        AssetCookProgressEventKind.StageCompleted => "stage.done",
        AssetCookProgressEventKind.IncrementalCompleted => "incremental.done",
        AssetCookProgressEventKind.MaterialStarted => "material.start",
        AssetCookProgressEventKind.MaterialCompleted => "material.done",
        AssetCookProgressEventKind.TextureStarted => "texture.start",
        AssetCookProgressEventKind.TextureCompleted => "texture.done",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static void AppendPlain(StringBuilder line, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        line.Append(' ').Append(key).Append("=\"");
        AppendEscaped(line, value);
        line.Append('"');
    }

    private static void AppendPlainToken(StringBuilder line, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        line.Append(' ').Append(key).Append('=').Append(value);
    }

    private static void AppendPlain(StringBuilder line, string key, int? value)
    {
        if (!value.HasValue)
            return;
        line.Append(' ').Append(key).Append('=').Append(
            value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendPlain(StringBuilder line, string key, long? value)
    {
        if (!value.HasValue)
            return;
        line.Append(' ').Append(key).Append('=').Append(
            value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendPlainEnum<T>(
        StringBuilder line,
        string key,
        T? value,
        Func<T, string> formatter)
        where T : struct, Enum
    {
        if (value.HasValue)
            AppendPlainToken(line, key, formatter(value.Value));
    }

    private static void AppendPlainPath(
        StringBuilder line,
        string key,
        string? path,
        string? root)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string displayed = path;
        if (!string.IsNullOrWhiteSpace(root))
        {
            try
            {
                string relative = Path.GetRelativePath(root, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative))
                    displayed = relative;
            }
            catch (ArgumentException)
            {
                // Different Windows volumes cannot be made relative. Keep the
                // original full path in that case.
            }
        }
        AppendPlain(line, key, displayed);
    }

    private static void AppendEscaped(StringBuilder line, string value)
    {
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': line.Append("\\\\"); break;
                case '"': line.Append("\\\""); break;
                case '\r': line.Append("\\r"); break;
                case '\n': line.Append("\\n"); break;
                case '\t': line.Append("\\t"); break;
                default: line.Append(character); break;
            }
        }
    }

    private static string ToKebabCase<T>(T value) where T : struct, Enum =>
        ToKebabCase(value.ToString());

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                    builder.Append('-');
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _terminated = true;
            _heartbeatTimer?.Dispose();
        }
    }
}
