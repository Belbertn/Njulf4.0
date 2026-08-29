using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal static class SampleGiAllOnQualificationContract
{
    public const int SchemaVersion = 1;
    public const int DefaultMaximumFrameCount = 1_800;
    public const string Kind = "gi-all-on-runtime-qualification";

    public static bool IsSupportedScene(SampleSceneKind scene) => scene is
        SampleSceneKind.MaterialShowcase or
        SampleSceneKind.SponzaPlaza or
        SampleSceneKind.Bistro;

    // The all-on runner owns an evidence-based early terminal condition. Once
    // it has passed, the generic --smoke-frames ceiling is only a timeout and
    // must not turn a successful early exit into a host failure.
    public static bool RequiresGeneralSmokeCompletion(
        bool allOnQualificationPassed) => !allOnQualificationPassed;

    /// <summary>
    /// Keeps the qualification workload focused on the five GI paths. Thick
    /// transparent ray-query shading and hybrid specular reflections own
    /// separate, exceptionally expensive pipeline families and are not inputs
    /// to C1, C3, C4, the DDGI receiver cache, or the accelerated transport
    /// solver. C4 continues to trace the authored closed dielectric through the
    /// ordinary ray scene.
    /// </summary>
    public static void ApplyIsolationSettings(
        RenderSettings settings,
        SampleSceneKind scene)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Transparency.ThickTransmissionMode =
            ThickTransmissionMode.Approximation;
        settings.Transparency.DispersionMode = DispersionMode.Off;
        settings.Reflections.Enabled = false;
        if (scene == SampleSceneKind.MaterialShowcase)
        {
            // The authored receiver-hero lattice fully contains the showcase
            // and the qualification fixtures. Its additional camera-relative
            // near ring would request 17,000 probes and violate DdgiHigh's
            // immutable 16,384-probe cap, so do not allocate that redundant
            // fallback field for this bounded scene.
            settings.GlobalIllumination.SimpleDdgiRingCount = 0;
        }
    }
}

internal sealed record SampleGiAllOnQualificationCriterion(
    string Name,
    bool Passed,
    string Detail);

internal sealed record SampleGiAllOnQualificationReport(
    int SchemaVersion,
    string Kind,
    string Status,
    bool Passed,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    SampleSceneKind Scene,
    string DeviceIdentity,
    string RenderSettingsFingerprint,
    int RenderedFrameCount,
    GiAllOnRuntimeQualificationSnapshot Runtime,
    IReadOnlyList<SampleGiAllOnQualificationCriterion> Criteria)
{
    public IReadOnlyList<SampleGiAllOnQualificationCriterion> Failures { get; } =
        Criteria.Where(static criterion => !criterion.Passed).ToArray();
}

/// <summary>
/// Owns one uninterrupted, settings-locked all-on run. Publication is atomic
/// and idempotent so both an early pass and application teardown produce one
/// authoritative artifact.
/// </summary>
internal sealed class SampleGiAllOnQualificationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly string _reportPath;
    private readonly SampleSceneKind _scene;
    private readonly string _deviceIdentity;
    private readonly string _renderSettingsFingerprint;
    private readonly Action _exit;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly GiAllOnRuntimeQualificationAccumulator _accumulator = new();
    private bool _completed;
    private int _renderedFrameCount;

    public SampleGiAllOnQualificationRunner(
        string reportPath,
        SampleSceneKind scene,
        string deviceIdentity,
        string renderSettingsFingerprint,
        Action exit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderSettingsFingerprint);
        ArgumentNullException.ThrowIfNull(exit);

        _reportPath = Path.GetFullPath(reportPath);
        _scene = scene;
        _deviceIdentity = deviceIdentity;
        _renderSettingsFingerprint = renderSettingsFingerprint;
        _exit = exit;
    }

    public SampleGiAllOnQualificationReport? Report { get; private set; }

    public void OnFrameRendered(RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (_completed)
            return;

        _renderedFrameCount = checked(_renderedFrameCount + 1);
        _accumulator.Observe(diagnostics);
        if (!_accumulator.Snapshot.Passed)
            return;

        Complete();
        Console.WriteLine(
            $"All-on GI runtime qualification passed: report='{_reportPath}'.");
        _exit();
    }

    public SampleGiAllOnQualificationReport Complete()
    {
        if (_completed)
            return Report!;

        GiAllOnRuntimeQualificationSnapshot snapshot = _accumulator.Snapshot;
        SampleGiAllOnQualificationCriterion[] criteria = Evaluate(snapshot);
        bool passed = criteria.All(static criterion => criterion.Passed);
        var report = new SampleGiAllOnQualificationReport(
            SampleGiAllOnQualificationContract.SchemaVersion,
            SampleGiAllOnQualificationContract.Kind,
            passed ? "passed" : "failed",
            passed,
            _startedAtUtc,
            DateTimeOffset.UtcNow,
            _scene,
            _deviceIdentity,
            _renderSettingsFingerprint,
            _renderedFrameCount,
            snapshot,
            Array.AsReadOnly(criteria));

        PublishAtomically(_reportPath, report);
        Report = report;
        _completed = true;
        return report;
    }

    internal static SampleGiAllOnQualificationCriterion[] Evaluate(
        GiAllOnRuntimeQualificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return
        [
            Criterion(
                "schema",
                snapshot.Schema ==
                    GiAllOnRuntimeQualificationSnapshot.SchemaRevision,
                $"observed={snapshot.Schema}, required=" +
                GiAllOnRuntimeQualificationSnapshot.SchemaRevision),
            Criterion(
                "uninterrupted-all-on-observation",
                snapshot.ObservedAllOnFrameCount >= 3 &&
                snapshot.FirstFrameSerial != 0UL &&
                snapshot.LastFrameSerial >= snapshot.FirstFrameSerial,
                $"frames={snapshot.ObservedAllOnFrameCount}, " +
                $"first={snapshot.FirstFrameSerial}, last={snapshot.LastFrameSerial}, " +
                $"rejected={snapshot.RejectedNonAllOnFrameCount}, " +
                $"lastMismatch='{snapshot.LastRequestMismatchDetail}'"),
            Criterion(
                "simultaneously-effective",
                snapshot.SimultaneouslyEffectiveFrameObserved,
                "Every feature must be supported and effective in the same rendered frame."),
            FeatureCriterion("receiver-cache", snapshot.ReceiverCache),
            FeatureCriterion(
                "accelerated-transport-solver",
                snapshot.AcceleratedTransportSolver),
            FeatureCriterion("c1-opacity-micromaps", snapshot.OpacityMicromaps),
            FeatureCriterion(
                "c3-directional-guiding",
                snapshot.DirectionalGuiding),
            FeatureCriterion("c4-tagged-caustics", snapshot.TaggedCaustics),
            Criterion(
                "current-tail-certificate",
                snapshot.CurrentTailCertificateObserved,
                "A current accelerated-tail convergence certificate must be observed."),
            Criterion(
                "fatal-runtime-health",
                !snapshot.FatalRuntimeFailureObserved,
                string.IsNullOrWhiteSpace(snapshot.FatalRuntimeFailureDetail)
                    ? "No fatal runtime condition was observed."
                    : snapshot.FatalRuntimeFailureDetail)
        ];
    }

    private static SampleGiAllOnQualificationCriterion FeatureCriterion(
        string name,
        in GiAllOnFeatureRuntimeEvidence evidence) => Criterion(
        name,
        evidence.Passed,
        $"requested={evidence.Requested}, supported={evidence.Supported}, " +
        $"effective={evidence.Effective}, executed={evidence.Executed}, " +
        $"consumed={evidence.Consumed}, detail='{evidence.Detail}'");

    private static SampleGiAllOnQualificationCriterion Criterion(
        string name,
        bool passed,
        string detail) => new(name, passed, detail);

    internal static void PublishAtomically(
        string reportPath,
        SampleGiAllOnQualificationReport report)
    {
        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = reportPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(report, JsonOptions));
            File.Move(temporaryPath, reportPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>
/// Publishes an in-progress marker before renderer construction and converts
/// host-level exceptions, cancellation, or an early window shutdown into a
/// terminal failed report. This prevents a stale successful artifact from a
/// previous invocation being mistaken for evidence from the current run.
/// </summary>
internal sealed class SampleGiAllOnQualificationHostFailureGuard : IDisposable
{
    private const string UnavailableIdentity =
        "unavailable-before-renderer-initialization";

    private readonly string _reportPath;
    private readonly SampleSceneKind _scene;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private bool _disposed;

    public SampleGiAllOnQualificationHostFailureGuard(
        string reportPath,
        SampleSceneKind scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        _reportPath = Path.GetFullPath(reportPath);
        _scene = scene;
        Publish(
            "in-progress",
            "The host started and is waiting for rendered all-on evidence.");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public void RecordHostFailure(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        if (TryReadTerminalStatus() is "passed" or "failed")
            return;
        Publish("failed", failure);
    }

    public bool CompleteHostRun(int exitCode)
    {
        string? status = TryReadTerminalStatus();
        if (exitCode == 0 && status == "passed")
            return true;
        if (status == "failed")
            return false;

        RecordHostFailure(
            exitCode == 0
                ? "The all-on GI host exited before publishing a terminal passed report."
                : $"The all-on GI host exited with code {exitCode} before publishing a terminal report.");
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        Console.CancelKeyPress -= OnCancelKeyPress;
    }

    private void Publish(string status, string detail)
    {
        GiAllOnRuntimeQualificationSnapshot snapshot = new();
        SampleGiAllOnQualificationCriterion[] runtimeCriteria =
            SampleGiAllOnQualificationRunner.Evaluate(snapshot);
        var criteria = runtimeCriteria.Append(
            new SampleGiAllOnQualificationCriterion(
                "host-lifecycle",
                false,
                detail)).ToArray();
        var report = new SampleGiAllOnQualificationReport(
            SampleGiAllOnQualificationContract.SchemaVersion,
            SampleGiAllOnQualificationContract.Kind,
            status,
            Passed: false,
            _startedAtUtc,
            DateTimeOffset.UtcNow,
            _scene,
            UnavailableIdentity,
            UnavailableIdentity,
            RenderedFrameCount: 0,
            snapshot,
            Array.AsReadOnly(criteria));
        SampleGiAllOnQualificationRunner.PublishAtomically(_reportPath, report);
    }

    private string? TryReadTerminalStatus()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(_reportPath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("SchemaVersion", out JsonElement schema) ||
                schema.GetInt32() != SampleGiAllOnQualificationContract.SchemaVersion ||
                !root.TryGetProperty("Kind", out JsonElement kind) ||
                kind.GetString() != SampleGiAllOnQualificationContract.Kind ||
                !root.TryGetProperty("Status", out JsonElement status))
            {
                return null;
            }
            return status.GetString()?.Trim().ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException)
        {
            return null;
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        string description = args.ExceptionObject is Exception exception
            ? $"{exception.GetType().Name}: {exception.Message}"
            : args.ExceptionObject?.ToString() ?? "unknown exception";
        RecordHostFailure(
            "Unhandled all-on GI qualification host failure: " + description);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        RecordHostFailure(
            "The all-on GI qualification host was cancelled before completion.");
    }
}
