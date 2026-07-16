namespace Njulf.Rendering.Diagnostics
{
    public enum MemoryBudgetCategory
    {
        Unknown,
        MeshBuffers,
        ObjectAndInstanceBuffers,
        MaterialBuffers,
        LightBuffers,
        TextureAssets,
        RenderTargets,
        ShadowMaps,
        EnvironmentMaps,
        ReflectionProbes,
        GlobalIllumination,
        StagingBuffers,
        DiagnosticsAndDebug,
        Swapchain,
        SamplersAndDescriptors
    }
}
