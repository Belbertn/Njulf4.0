using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Graphics;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Shaders;
using static Njulf.Rendering.RenderingConstants;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Buffer = Silk.NET.Vulkan.Buffer;
using ICamera = Njulf.Core.Interfaces.ICamera;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Njulf.Rendering.Pipeline;

// Borrowed services and resources. Creation captures references, never ownership or frame state.
internal sealed record ProductionPipelineDependencies
{
    internal required AccelerationStructureManager? AccelerationStructureManager { get; init; }
    internal required AdvancedGiAdmissionCoordinator AdvancedGiAdmission { get; init; }
    internal required AutoExposureManager? AutoExposureManager { get; init; }
    internal required AutomaticPlanarReflectionManager AutomaticPlanarReflectionManager { get; init; }
    internal required BindlessHeap BindlessHeap { get; init; }
    internal required BufferManager BufferManager { get; init; }
    internal required VulkanContext Context { get; init; }
    internal required FenceBasedDeleter Deleter { get; init; }
    internal required DirectionalShadowHistoryResources? DirectionalShadowHistoryResources { get; init; }
    internal required DirectionalShadowResources? DirectionalShadowResources { get; init; }
    internal required EnvironmentManager? EnvironmentManager { get; init; }
    internal required FarFieldClipmapManager? FarFieldClipmapManager { get; init; }
    internal required FoliageManager FoliageManager { get; init; }
    internal required GiCausticFrameCoordinator GiCaustic { get; init; }
    internal required GiPipelineCacheService? GiPipelineCacheService { get; init; }
    internal required GpuParticleRuntimeManager GpuParticleRuntimeManager { get; init; }
    internal required bool GtaoRuntimeSupported { get; init; }
    internal required HiZDepthPyramid? HizDepthPyramid { get; init; }
    internal required HybridReflectionVulkanRuntime? HybridReflectionRuntime { get; init; }
    internal required RendererLifetimeCoordinator Lifetime { get; init; }
    internal required SimpleDdgiNearFieldResidualCoordinator NearFieldResidual { get; init; }
    internal required OverlayDrawDataSource OverlayDrawData { get; init; }
    internal required PointShadowPool? PointShadowCubemapArray { get; init; }
    internal required RaySceneDescriptorBank? RaySceneDescriptorBank { get; init; }
    internal required ReflectionProbeManager? ReflectionProbeManager { get; init; }
    internal required RenderGraph RenderGraph { get; init; }
    internal required RenderTargetManager? RenderTargets { get; init; }
    internal required SceneDataBuilder SceneDataBuilder { get; init; }
    internal required SimpleDdgiGuidingFrameCoordinator? SimpleDdgiGuidingFrameCoordinator { get; init; }
    internal required SimpleDdgiGuidingVulkanRuntime? SimpleDdgiGuidingRuntime { get; init; }
    internal required SimpleDdgiLightTreeGpuResources? SimpleDdgiLightTreeResources { get; init; }
    internal required SimpleDdgiReceiverFeedbackCoordinator? SimpleDdgiReceiverFeedback { get; init; }
    internal required SimpleDdgiVolumeManager? SimpleDdgiVolumeManager { get; init; }
    internal required SkinningManager SkinningManager { get; init; }
    internal required SmaaResources? SmaaResources { get; init; }
    internal required SpotShadowAtlas? SpotShadowAtlas { get; init; }
    internal required StagingRing StagingRing { get; init; }
    internal required SwapchainManager Swapchain { get; init; }
    internal required SynchronizationManager Sync { get; init; }
    internal required TemporalSurfaceValidityResources? TemporalSurfaceValidityResources { get; init; }
    internal required RenderSettings Settings { get; init; }
    internal required Func<SurfaceHistoryConsumer> ResolveSurfaceHistoryConsumers { get; init; }
    internal required Func<bool> HasIncompatibleVariableRateShadingForwardOutput { get; init; }
}
