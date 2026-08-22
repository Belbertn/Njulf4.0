namespace Njulf.Rendering.Pipeline;

internal static class RendererBuildConfiguration
{
    internal static bool FastPipelineStartup { get; } =
#if NJULF_DEVELOPMENT
        true;
#else
        false;
#endif
}
