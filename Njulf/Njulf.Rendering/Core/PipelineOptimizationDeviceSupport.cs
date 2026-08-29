namespace Njulf.Rendering.Core;

/// <summary>
/// Physical and device-enabled capabilities used by the renderer pipeline
/// compiler. Optional capabilities never participate in device admission.
/// </summary>
public readonly record struct PipelineOptimizationDeviceSupport(
    bool PipelineCreationFeedback,
    bool PipelineCreationCacheControl,
    bool PipelineBinary,
    bool PipelineBinaryMaintenance5,
    bool PipelineBinaryInternalCache,
    bool PipelineBinaryInternalCacheControl,
    bool PipelineBinaryPrefersInternalCache,
    bool PipelineBinaryPrecompiledInternalCache,
    bool PipelineBinaryCompressedData,
    bool GraphicsPipelineLibrary,
    bool GraphicsPipelineLibraryFastLinking,
    bool GraphicsPipelineLibraryIndependentInterpolation)
{
    public static PipelineOptimizationDeviceSupport None => default;
}
