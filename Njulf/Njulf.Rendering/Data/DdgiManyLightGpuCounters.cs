using System;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>
/// Completed detailed GPU evidence for the point-dependent local-light
/// estimator. Raw integer fields preserve the capture ABI; derived PDF values
/// decode the frozen shader quantization.
/// </summary>
public readonly record struct DdgiManyLightGpuCounters(
    uint BypassHitCount,
    uint ExactHitCount,
    uint TreeAttemptHitCount,
    uint TreeSuccessHitCount,
    uint TreeFallbackHitCount,
    uint SampledLightCount,
    uint DuplicateDrawCount,
    uint VisibilityEvaluationCount,
    uint RejectedZeroTermCount,
    uint UniformRepairCount,
    uint InvalidSampleOrPdfCount,
    uint QuantizedPdfSum,
    uint QuantizedNegativeLog2PdfSum,
    uint QuantizedMaximumNegativeLog2Pdf,
    uint QuantizedMaximumEstimatorWeight,
    uint ExactLightEvaluationCount)
{
    public float MeanPdf => SampledLightCount == 0
        ? 0f
        : QuantizedPdfSum /
            (RendererDiagnosticsBuffer.DdgiManyLightPdfScale * SampledLightCount);

    public float GeometricMeanPdf => SampledLightCount == 0
        ? 0f
        : MathF.Pow(
            2f,
            -QuantizedNegativeLog2PdfSum /
                (RendererDiagnosticsBuffer.DdgiManyLightLogPdfScale *
                 SampledLightCount));

    public float MinimumPdf => SampledLightCount == 0
        ? 0f
        : MathF.Pow(
            2f,
            -QuantizedMaximumNegativeLog2Pdf /
                RendererDiagnosticsBuffer.DdgiManyLightLogPdfScale);

    public float MaximumEstimatorWeight =>
        QuantizedMaximumEstimatorWeight /
        RendererDiagnosticsBuffer.DdgiManyLightEstimatorWeightScale;
}
