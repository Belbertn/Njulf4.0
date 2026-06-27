using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed class DdgiRecursiveSnapshotPass : RenderPassBase
    {
        private readonly RenderSettings _settings;
        private readonly DdgiProbeVolumeManager _probeVolumeManager;
        private readonly AccelerationStructureManager _accelerationStructureManager;

        public DdgiRecursiveSnapshotPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiRecursiveSnapshotPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _probeVolumeManager = probeVolumeManager ?? throw new ArgumentNullException(nameof(probeVolumeManager));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "DDGI recursive cache commit is compute/transfer-only probe-buffer work.";

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return gi.Enabled &&
                   gi.EffectiveUseDdgi &&
                   gi.EffectiveUseRayQueryBackend &&
                   _accelerationStructureManager.Active &&
                   sceneData.DdgiProbeVolumeCount > 0 &&
                   sceneData.DdgiProbesUpdated > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            _probeVolumeManager.CommitScheduledProbeUpdatesToRecursiveCache(cmd);
            sceneData.DdgiRecursiveCommitProbeCount = _probeVolumeManager.LastRecursiveCommitProbeCount;
            sceneData.DdgiRecursiveCommitCopyCount = _probeVolumeManager.LastRecursiveCommitCopyCount;
            sceneData.DdgiRecursiveCommitBytes = _probeVolumeManager.LastRecursiveCommitBytes;
        }
    }
}
