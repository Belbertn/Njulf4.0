using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Prepares transient scratch, projects traced incident radiance in FP32, then
/// blends and publishes the checked FP16 per-probe SH sidecar. Projection is
/// parallel across probes and reads every source exactly once; the bounded
/// native programs retain one render-graph timing and rollback boundary.
/// </summary>
public sealed unsafe class SimpleDdgiDirectionalRadiancePass :
    SimpleDdgiComputePass
{
    private const string BaselinePrepareShader =
        "ddgi_simple_directional_prepare.comp.spv";
    private const string BaselineProjectShader =
        "ddgi_simple_directional_project.comp.spv";
    private const string GuidedProjectShader =
        "ddgi_simple_directional_project_guided.comp.spv";
    private const string LegacyGuidedStageShader =
        "ddgi_simple_directional_stage_guided_legacy.comp.spv";
    private const string PackedGuidedStageShader =
        "ddgi_simple_directional_stage_guided_packed.comp.spv";
    private readonly bool _directionalGuidingTransport;
    private readonly RenderSettings _directionalSettings;

    public SimpleDdgiDirectionalRadiancePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager,
        FarFieldClipmapManager farFieldClipmapManager,
        bool directionalGuidingTransport = false,
        GiPipelineCacheService? pipelineCacheService = null)
        : base(
            "SimpleDdgiDirectionalRadiancePass",
            ResolveProjectShaderName(directionalGuidingTransport),
            context,
            swapchain,
            bindlessHeap,
            settings,
            volumeManager,
            farFieldClipmapManager,
            accelerationStructureManager: null,
            requiresRayQuery: false,
            pipelineCacheService)
    {
        _directionalGuidingTransport = directionalGuidingTransport;
        _directionalSettings = settings;
    }

    internal static string ResolveProjectShaderName(
        bool directionalGuidingTransport) =>
        directionalGuidingTransport
            ? GuidedProjectShader
            : BaselineProjectShader;

    internal static string ResolvePrepareShaderName(
        bool directionalGuidingTransport,
        SimpleDdgiStoragePackingMode storagePackingMode)
    {
        if (!directionalGuidingTransport)
            return BaselinePrepareShader;

        return storagePackingMode.Sanitize() ==
                SimpleDdgiStoragePackingMode.Packed
            ? PackedGuidedStageShader
            : LegacyGuidedStageShader;
    }

    protected override uint CalculateGroupCount(SceneRenderingData sceneData) =>
        checked((uint)Math.Max(1, VolumeManager.ProbesToUpdate));

    protected override int PipelineDispatchCount => 3;

    protected override string ResolveShaderName(int dispatchIndex) =>
        dispatchIndex switch
        {
            0 => ResolvePrepareShaderName(
                _directionalGuidingTransport,
                _directionalSettings.GlobalIllumination
                    .SimpleDdgiStoragePackingMode),
            1 => ResolveProjectShaderName(_directionalGuidingTransport),
            2 => "ddgi_simple_directional_publish.comp.spv",
            _ => throw new ArgumentOutOfRangeException(nameof(dispatchIndex))
        };

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        ExecutePipelineDispatch(
            cmd,
            sceneData,
            dispatchIndex: 0,
            bindAccelerationStructure: false,
            additionalFlags: 0u);
        ExecutePipelineDispatch(
            cmd,
            sceneData,
            dispatchIndex: 1,
            bindAccelerationStructure: false,
            additionalFlags: 0u);
        ExecutePipelineDispatch(
            cmd,
            sceneData,
            dispatchIndex: 2,
            bindAccelerationStructure: false,
            additionalFlags: 0u);
    }

    protected override SimpleDdgiSchedulerDispatchSlot ResidentDispatchSlot =>
        SimpleDdgiSchedulerDispatchSlot.Blend;

    public override string AsyncComputeReason =>
        "Directional SH projection consumes completed diffuse/source data and publishes its sidecar before compact probe publication.";

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData)
    {
        return VolumeManager.DirectionalRadianceMode !=
                SimpleDdgiDirectionalRadianceMode.Off &&
            base.ShouldExecute(frameIndex, sceneData);
    }
}
