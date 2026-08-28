using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal readonly record struct SampleHealthReportEvaluation(
    int GiDiagnosticWarningCount,
    int GiDiagnosticErrorCount,
    GiDiagnosticWarning? FirstGiDiagnosticError)
{
    public static SampleHealthReportEvaluation Evaluate(RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        int warningCount = 0;
        int errorCount = 0;
        GiDiagnosticWarning? firstError = null;
        foreach (GiDiagnosticWarning diagnostic in diagnostics.GiWarnings)
        {
            switch (diagnostic.Severity)
            {
                case GiDiagnosticSeverity.Warning:
                    warningCount++;
                    break;
                case GiDiagnosticSeverity.Error:
                    errorCount++;
                    firstError ??= diagnostic;
                    break;
            }
        }

        return new SampleHealthReportEvaluation(
            warningCount,
            errorCount,
            firstError);
    }

    public static SampleSmokeOperationResult? FindFirstFailedOperation(
        IReadOnlyList<SampleSmokeOperationResult> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (SampleSmokeOperationResult operation in operations)
        {
            if (operation == null)
            {
                return new SampleSmokeOperationResult(
                    "unknown-operation",
                    "failed",
                    0,
                    "The smoke operation stream contained a null result.");
            }

            if (string.Equals(
                    operation.Status,
                    "failed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return operation;
            }
            if (!IsAllowedSuccessfulTerminalStatus(operation))
            {
                return operation with
                {
                    Status = "failed",
                    Detail =
                        $"Smoke operation '{operation.Name}' reported unexpected " +
                        $"non-terminal status '{operation.Status}'."
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Rejects a smoke run that closed cleanly before its required frame or
    /// operation evidence was observed. A process exit is not proof that a
    /// lifecycle mutation, rollback, or post-mutation frame completed.
    /// </summary>
    public static SampleSmokeOperationResult? FindIncompleteSmokeOperation(
        SampleSmokeOptions options,
        IReadOnlyList<SampleSmokeOperationResult> operations,
        int renderedFrameCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);
        if (renderedFrameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(renderedFrameCount));
        if (options.Mode == SampleSmokeMode.None)
            return null;

        bool standaloneBaselineCapture =
            options.Mode == SampleSmokeMode.Startup &&
            !string.IsNullOrWhiteSpace(options.BaselineSnapshotDirectory);
        bool durationOwnedLongRun =
            options.Mode == SampleSmokeMode.LongRun &&
            options.LongRunMinutes > 0.0;
        bool stateMachineOwnedSmoke = options.Mode is
            SampleSmokeMode.QualitySwitch or
            SampleSmokeMode.DdgiResidencySwitch or
            SampleSmokeMode.TextureHotReload or
            SampleSmokeMode.SceneTransition;
        if (!standaloneBaselineCapture &&
            !durationOwnedLongRun &&
            !stateMachineOwnedSmoke &&
            options.FrameCount > 0 &&
            renderedFrameCount < options.FrameCount)
        {
            return Incomplete(
                renderedFrameCount,
                $"Smoke mode '{options.Mode}' closed after {renderedFrameCount}/" +
                $"{options.FrameCount} required rendered frames.");
        }

        return options.Mode switch
        {
            SampleSmokeMode.Startup => null,
            SampleSmokeMode.Resize =>
                RequireExactOperationCount(operations, "resize", 3, renderedFrameCount),
            SampleSmokeMode.Minimize =>
                RequireOperations(
                    operations,
                    renderedFrameCount,
                    ("minimize-zero-framebuffer", 1),
                    ("restore-framebuffer", 1)),
            SampleSmokeMode.Fullscreen =>
                RequireExactOperationCount(operations, "fullscreen", 1, renderedFrameCount),
            SampleSmokeMode.SceneReload =>
                RequireExactOperationCount(
                    operations,
                    "scene-reload",
                    Math.Max(0, options.SceneReloadCount),
                    renderedFrameCount),
            SampleSmokeMode.MissingAssets =>
                RequireExactOperationCount(operations, "missing-assets", 1, renderedFrameCount),
            SampleSmokeMode.LongRun =>
                RequireOperations(
                    operations,
                    renderedFrameCount,
                    ("long-run-stability", 1),
                    ("long-run-duration", durationOwnedLongRun ? 1 : 0)),
            SampleSmokeMode.QualitySwitch =>
                RequireExactOperationCount(operations, "quality-switch", 1, renderedFrameCount),
            SampleSmokeMode.DdgiResidencySwitch =>
                RequireExactOperationCount(
                    operations,
                    "ddgi-residency-switch",
                    1,
                    renderedFrameCount),
            SampleSmokeMode.TextureHotReload =>
                RequireExactOperationCount(operations, "texture-hot-reload", 1, renderedFrameCount),
            SampleSmokeMode.SceneTransition =>
                RequireExactOperationCount(
                    operations,
                    "scene-transition",
                    1,
                    renderedFrameCount),
            SampleSmokeMode.All =>
                RequireOperations(
                    operations,
                    renderedFrameCount,
                    ("resize", 3),
                    ("minimize-zero-framebuffer", 1),
                    ("restore-framebuffer", 1),
                    ("fullscreen", 1),
                    ("scene-reload", Math.Max(0, options.SceneReloadCount))),
            _ => Incomplete(
                renderedFrameCount,
                $"Smoke mode '{options.Mode}' is not covered by the completion contract.")
        };
    }

    private static bool IsAllowedSuccessfulTerminalStatus(
        SampleSmokeOperationResult operation)
    {
        if (string.Equals(operation.Status, "passed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(operation.Status, "skipped", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(operation.Name, "fullscreen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(operation.Name, "missing-assets", StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(
                operation.Status,
                "rejected-unsupported",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(
                operation.Name,
                "device-loss-recovery",
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static SampleSmokeOperationResult? RequireOperations(
        IReadOnlyList<SampleSmokeOperationResult> operations,
        int renderedFrameCount,
        params (string Name, int ExpectedCount)[] requirements)
    {
        foreach ((string name, int expectedCount) in requirements)
        {
            SampleSmokeOperationResult? failure =
                RequireExactOperationCount(
                    operations,
                    name,
                    expectedCount,
                    renderedFrameCount);
            if (failure != null)
                return failure;
        }

        return null;
    }

    private static SampleSmokeOperationResult? RequireExactOperationCount(
        IReadOnlyList<SampleSmokeOperationResult> operations,
        string name,
        int expectedCount,
        int renderedFrameCount)
    {
        int actualCount = 0;
        foreach (SampleSmokeOperationResult? operation in operations)
        {
            if (operation != null &&
                string.Equals(
                    operation.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                actualCount++;
            }
        }

        return actualCount == expectedCount
            ? null
            : Incomplete(
                renderedFrameCount,
                $"Smoke completion evidence for '{name}' is incomplete: " +
                $"observed {actualCount}/{expectedCount} required operation result(s).");
    }

    private static SampleSmokeOperationResult Incomplete(
        int renderedFrameCount,
        string detail) =>
        new(
            "smoke-completion",
            "failed",
            Math.Max(0, renderedFrameCount - 1),
            detail);
}
