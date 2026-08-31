using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal sealed class SampleHealthReportWriter
{
    private readonly RendererHealthReportWriter _writer = new();

    public void Write(
        SampleSmokeOptions options,
        string? startupLogPath,
        IReadOnlyList<SampleSmokeOperationResult> operations,
        RendererDiagnostics diagnostics,
        string status,
        string? failure,
        RenderSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(options.HealthReportPath))
            return;

        SampleHealthReportEvaluation evaluation =
            SampleHealthReportEvaluation.Evaluate(diagnostics);
        MaterialGiProducerIdentity? producerIdentity = null;
        if (string.Equals(status, "passed", StringComparison.Ordinal))
        {
            producerIdentity =
                SampleMaterialGiProducerIdentityFactory.Create(
                    diagnostics,
                    SampleRenderSettingsFingerprint.Capture(
                        settings ?? throw new InvalidOperationException(
                            "A passed health report requires the exact producer render settings.")));
        }
        _writer.Write(options.HealthReportPath, new
        {
            kind = "renderer-health",
            schema = MaterialGiReleaseEvidenceContract.HealthProducerSchema,
            producerIdentity,
            timestampUtc = DateTimeOffset.UtcNow,
            status,
            failure,
            validationWarningCount = diagnostics.ValidationWarningMessageCount,
            validationErrorCount = diagnostics.ValidationErrorMessageCount,
            giDiagnosticWarningCount = evaluation.GiDiagnosticWarningCount,
            giDiagnosticErrorCount = evaluation.GiDiagnosticErrorCount,
            startupLogPath,
            options,
            operations,
            performanceOptimizations = settings == null
                ? null
                : new
                {
                    enabled = settings.PerformanceOptimizations.Enabled,
                    requestedMask = PerformanceOptimizationFeatureMask.Format(
                        settings.PerformanceOptimizations.EnabledFeatures),
                    effectiveMask = PerformanceOptimizationFeatureMask.Format(
                        settings.EffectivePerformanceOptimizationFeatures),
                    asyncMode = settings.AsyncCompute.Mode
                },
            diagnostics
        });
    }
}
