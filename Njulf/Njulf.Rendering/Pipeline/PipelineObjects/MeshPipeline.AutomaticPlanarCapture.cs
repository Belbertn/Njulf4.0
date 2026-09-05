using System.Threading;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects;

public sealed unsafe partial class MeshPipeline
{
    private readonly AutomaticPlanarCapturePipelineBank _automaticPlanarPipelineBank = new();
    private readonly VkPipeline[] _automaticPlanarColorPipelines = new VkPipeline[6];
    private VkPipeline _automaticPlanarDepthPipeline;
    private readonly long[] _automaticPlanarLastTracedHandles = new long[ForwardOpaquePipelineKey.FamilyCount];
    private readonly bool _automaticPlanarPipelineTrace =
        Environment.GetEnvironmentVariable("NJULF_AUTOMATIC_PLANAR_PIPELINE_TRACE") == "1";
    private readonly bool _automaticPlanarSpecializationEnabled =
        Environment.GetEnvironmentVariable("NJULF_AUTOMATIC_PLANAR_CAPTURE_SPECIALIZATION") != "0";

    // Independent A/B controls, without adding public renderer settings.
    internal bool AutomaticPlanarDepthPrepassEnabled { get; } =
        Environment.GetEnvironmentVariable("NJULF_AUTOMATIC_PLANAR_DEPTH_PREPASS") != "0";

    internal void PrepareAutomaticPlanarCapturePipelines(bool prepareSpecializations)
    {
        if (AutomaticPlanarDepthPrepassEnabled && _automaticPlanarDepthPipeline.Handle == 0)
        {
            _automaticPlanarDepthPipeline = CreateAutomaticPlanarCapturePipeline(
                ResolveAutomaticPlanarMeshShader(0), "planar_capture_depth.frag.spv",
                hasColor: false, depthWrite: true);
        }

        // The critical bank is Full, with the correct depth ownership for
        // this capture. Optional families are published only after both their
        // color and required feedback programs have finished preparation.
        PrepareAutomaticPlanarFamily(0);
        if (prepareSpecializations && _automaticPlanarSpecializationEnabled)
        {
            PrepareAutomaticPlanarFamily(1);
            PrepareAutomaticPlanarFamily(2);
        }
    }

    private void PrepareAutomaticPlanarFamily(int family)
    {
        ForwardOpaquePipelineFamily pipelineFamily = family switch
        {
            1 => ForwardOpaquePipelineFamily.Simple,
            2 => ForwardOpaquePipelineFamily.SimpleFullInput,
            _ => ForwardOpaquePipelineFamily.Full
        };
        if (_automaticPlanarPipelineBank.IsPrepared(pipelineFamily))
            return;
        for (int feedback = 0; feedback < (_receiverFeedbackPipelinesEnabled ? 2 : 1); feedback++)
        {
            ref VkPipeline pipeline = ref _automaticPlanarColorPipelines[family * 2 + feedback];
            if (pipeline.Handle == 0)
                pipeline = CreateAutomaticPlanarCapturePipeline(
                    ResolveAutomaticPlanarMeshShader(family),
                    ResolveAutomaticPlanarFragmentShader(family, feedback != 0),
                    hasColor: true, depthWrite: !AutomaticPlanarDepthPrepassEnabled);
        }
        _automaticPlanarPipelineBank.Publish(pipelineFamily, _automaticPlanarColorPipelines[family * 2],
            _automaticPlanarColorPipelines[family * 2 + 1], _receiverFeedbackPipelinesEnabled);
    }

    internal bool TryResolveAutomaticPlanarCapturePipeline(
        in ForwardOpaquePipelineKey key, bool depthPrepass, out VkPipeline pipeline)
    {
        if (depthPrepass)
        {
            pipeline = _automaticPlanarDepthPipeline;
            return pipeline.Handle != 0;
        }
        bool resolved = _automaticPlanarPipelineBank.TryResolve(key, out pipeline);
        if (resolved && _automaticPlanarPipelineTrace &&
            Interlocked.Exchange(ref _automaticPlanarLastTracedHandles[(int)key.Family],
                unchecked((long)pipeline.Handle)) != unchecked((long)pipeline.Handle))
        {
            for (int i = 0; i < _automaticPlanarColorPipelines.Length; i++)
            {
                if (_automaticPlanarColorPipelines[i].Handle != pipeline.Handle)
                    continue;
                Console.Error.WriteLine($"Automatic planar capture: requested={key.Family}; " +
                    $"shader={ResolveAutomaticPlanarFragmentShader(i / 2, (i & 1) != 0)}; " +
                    $"mesh={ResolveAutomaticPlanarMeshShader(i / 2)}; stream=raw; " +
                    $"depthPrepass={AutomaticPlanarDepthPrepassEnabled}; pipeline=0x{pipeline.Handle:X}");
                break;
            }
        }
        return resolved;
    }

    private string ResolveAutomaticPlanarMeshShader(int family) => family switch
    {
        1 => TasklessSubmissionEnabled ? _compactedForwardSimpleMeshShaderName : "forward_simple.mesh.spv",
        2 => TasklessSubmissionEnabled ? _compactedForwardSimpleFullInputMeshShaderName : ForwardSimpleFullInputMeshShaderName,
        _ => TasklessSubmissionEnabled ? _compactedForwardMeshShaderName : "forward.mesh.spv"
    };

    private string ResolveAutomaticPlanarFragmentShader(int family, bool feedback)
    {
        // Ordinary DDGI retains directional reflected radiance. Main-view
        // hybrid programs defer that lighting to a pass absent from captures.
        string prefix = AutomaticPlanarDepthPrepassEnabled ? "forward_opaque" : "forward_planar_capture";
        string material = family switch { 1 => "_simple", 2 => "_simple_full_input", _ => "" };
        return $"{prefix}{material}_ddgi{(feedback ? "_b1" : "")}.frag.spv";
    }

    private VkPipeline CreateAutomaticPlanarCapturePipeline(
        string meshShader, string fragmentShader, bool hasColor, bool depthWrite)
    {
        VkPipeline pipeline = CreateGraphicsPipeline(
            TasklessSubmissionEnabled ? null : "forward_planar_capture.task.spv",
            meshShader, fragmentShader, _colorFormat, _depthFormat,
            hasColorAttachment: hasColor, depthWriteEnable: depthWrite, blendEnable: false,
            cullMode: CullModeFlags.None, depthBiasEnable: false);
        _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline,
            $"Automatic Planar Capture (depthWrite={depthWrite}, {fragmentShader}, {meshShader})");
        return pipeline;
    }

    private void DestroyAutomaticPlanarCapturePipelines()
    {
        _automaticPlanarPipelineBank.Clear();
        for (int i = 0; i < _automaticPlanarColorPipelines.Length; i++)
            DestroyOptionalPipeline(ref _automaticPlanarColorPipelines[i]);
        DestroyOptionalPipeline(ref _automaticPlanarDepthPipeline);
        Array.Clear(_automaticPlanarLastTracedHandles);
    }
}
