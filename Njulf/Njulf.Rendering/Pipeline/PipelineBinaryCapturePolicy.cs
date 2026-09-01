namespace Njulf.Rendering.Pipeline;

internal static class PipelineBinaryCapturePolicy
{
    internal static bool ShouldCapture(
        RendererPipelineBinaryCacheMode mode,
        bool storeAvailable,
        bool driverInternalCache,
        bool applicationCacheLikelyWarm,
        bool autoCaptureEnabled)
    {
        if (!storeAvailable)
            return false;
        if (mode == RendererPipelineBinaryCacheMode.Capture)
            return true;
        return mode == RendererPipelineBinaryCacheMode.Auto &&
               autoCaptureEnabled &&
               !driverInternalCache &&
               !applicationCacheLikelyWarm;
    }
}
