using System.Diagnostics;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>Records independent scene views without modifying the main camera or its visible streams.</summary>
internal sealed unsafe class SecondaryViewRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BindlessHeap _heap;
    private readonly MeshPipeline _mesh;
    private readonly FoliagePipeline? _foliage;
    private readonly FoliageManager? _foliageManager;
    private readonly FoliageCullPass _foliageCull;
    private readonly SkyboxPipeline? _sky;
    private readonly BufferManager _buffers;
    private readonly SceneDataBuilder _scene;
    private readonly RenderSettings _settings;
    private readonly AutomaticPlanarReflectionManager _planar;
    private readonly ForwardPlusPass _feedback;
    private readonly SecondaryViewResources _resources;
    private readonly bool _cull = Environment.GetEnvironmentVariable("NJULF_SECONDARY_VIEW_CULLING") != "0";
    private readonly bool _trace = Environment.GetEnvironmentVariable("NJULF_SECONDARY_VIEW_TRACE") == "1";
    private ulong _frameSerial;
    private int _nextProbeSlot;
    private readonly Njulf.Rendering.Debug.RenderDocCaptureService? _captureInspection =
        Environment.GetEnvironmentVariable("NJULF_SECONDARY_VIEW_RENDERDOC") == "1" ? new() : null;
    private bool _inspectionRequested;

    internal SecondaryViewRenderer(VulkanContext context, BindlessHeap heap, MeshPipeline mesh,
        FoliagePipeline? foliage, FoliageManager? foliageManager, FoliageCullPass foliageCull,
        SkyboxPipeline? sky, BufferManager buffers, SceneDataBuilder scene, RenderSettings settings,
        AutomaticPlanarReflectionManager planar, ForwardPlusPass feedback)
    {
        _context = context; _heap = heap; _mesh = mesh; _foliage = foliage;
        _foliageManager = foliageManager; _foliageCull = foliageCull; _sky = sky;
        _buffers = buffers; _scene = scene; _settings = settings; _planar = planar; _feedback = feedback;
        _resources = new(context, buffers, heap);
    }

    internal void RecordPlanar(CommandBuffer cmd, int frameIndex, SceneRenderingData scene,
        in AutomaticPlanarCaptureView view, ImageView color, ImageView depth)
    {
        if (_captureInspection is not null && !_inspectionRequested && scene.DdgiFrameSerial >= 300)
        {
            _inspectionRequested = true;
            _captureInspection.RequestCapture();
            if (_captureInspection.CaptureRequested)
                _planar.RequestDeterministicCapturePhaseReset();
        }
        AutomaticPlanarPreparedCapture capture = _planar.PreparedCaptures[view.Slot];
        var secondary = new SecondaryViewContext(view.View, view.Projection, view.Position,
            view.Width, view.Height, checked((int)(AutomaticPlanarReflectionManager.AutomaticCaptureLayerFlag |
            (uint)view.Slot)), true, _settings.Transparency.Enabled, view.ClipPlane, capture.ExcludedObjectIndices)
        {
            Region = view.Region,
            MaximumTransparentMeshlets = Math.Max(0, _settings.Transparency.MaxTransparentMeshlets),
            ClipTolerance = MathF.Max(0.0005f, capture.WorldDiagonal * 0.0001f)
        };
        Record(cmd, frameIndex, scene, secondary, view.Slot, color, depth, false);
    }

    internal void RecordProbe(CommandBuffer cmd, int frameIndex, SceneRenderingData scene,
        in ReflectionCaptureViewContext view, ImageView color, ImageView depth)
    {
        if (_frameSerial != scene.DdgiFrameSerial)
        {
            _frameSerial = scene.DdgiFrameSerial;
            _nextProbeSlot = 2;
        }
        var secondary = new SecondaryViewContext(view.View, view.Projection, view.Position,
            view.Resolution, view.Resolution, view.CubemapArrayLayer, view.IncludesDdgi,
            false, default, Array.Empty<uint>());
        bool feedback = _feedback.BeginSecondaryProbeFeedback(frameIndex, scene, view);
        bool completed = false;
        try
        {
            Record(cmd, frameIndex, scene, secondary, _nextProbeSlot++, color, depth, feedback);
            completed = true;
        }
        finally { _feedback.EndSecondaryProbeFeedback(completed); }
    }

    private void Record(CommandBuffer cmd, int frameIndex, SceneRenderingData scene,
        in SecondaryViewContext view, int slot, ImageView colorView, ImageView depthView, bool feedback)
    {
        if (colorView.Handle == 0 || depthView.Handle == 0)
            throw new InvalidOperationException("Secondary capture attachments are unavailable.");
        long start = Stopwatch.GetTimestamp();
        SecondaryViewResources.ViewResources resources = _resources.Acquire(frameIndex, slot, scene.DdgiFrameSerial);
        _scene.BuildSecondaryDrawLists(view, frameIndex, resources.Draws, _cull);
        _resources.Prepare(resources, view, frameIndex, _foliageManager?.GetBuffers(frameIndex) ?? default);
        _foliageCull.ExecuteSecondary(cmd, frameIndex, view, resources.Foliage, resources.StorageSet);
        if (_trace)
            Console.Error.WriteLine($"Secondary view frame={scene.DdgiFrameSerial} slot={slot} planar={view.IsPlanar} " +
                $"candidates={resources.Draws.CandidateMeshlets} draws={resources.Draws.Opaque[0].Count}/" +
                $"{resources.Draws.Opaque[1].Count}/{resources.Draws.Opaque[2].Count}/{resources.Draws.TransparentCommands.Count} " +
                $"excluded={resources.Draws.ExcludedObjects} culledObjects={resources.Draws.CulledObjects} " +
                $"culledMeshlets={resources.Draws.CulledMeshlets} region={view.Region.Resolve(view.Width, view.Height)} " +
                $"prepareUs={Stopwatch.GetElapsedTime(start).TotalMicroseconds:F0}");

        var viewport = new Viewport(0, 0, view.Width, view.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(view.Width, view.Height));
        SecondaryViewRegion region = view.Region.Resolve(view.Width, view.Height);
        var drawScissor = new Rect2D(new Offset2D((int)region.X, (int)region.Y),
            new Extent2D(region.Width, region.Height));
        _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
        _context.Api.CmdSetScissor(cmd, 0, 1, &drawScissor);
        var color = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = colorView,
            ImageLayout = ImageLayout.ColorAttachmentOptimal, LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store, ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f))
        };
        var depth = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = depthView,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal, LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store, ClearValue = new ClearValue(null, new ClearDepthStencilValue(0f, 0))
        };
        var info = new RenderingInfo
        {
            SType = StructureType.RenderingInfo, RenderArea = scissor, LayerCount = 1,
            ColorAttachmentCount = 0, PDepthAttachment = &depth
        };
        if (_mesh.AutomaticPlanarDepthPrepassEnabled)
        {
            _context.BeginDebugLabel(cmd, "Secondary View Depth");
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &info);
            try { DrawOpaque(cmd, scene, view, resources, true, false); }
            finally { _context.KhrDynamicRendering.CmdEndRendering(cmd); _context.EndDebugLabel(cmd); }
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
            };
            PipelineStageFlags stages = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            _context.Api.CmdPipelineBarrier(cmd, stages, stages, 0, 1, &barrier, 0, null, 0, null);
            depth.LoadOp = AttachmentLoadOp.Load;
        }
        info.ColorAttachmentCount = 1;
        info.PColorAttachments = &color;
        _context.KhrDynamicRendering.CmdBeginRendering(cmd, &info);
        try
        {
            DrawSky(cmd, view, resources.StorageSet);
            _context.BeginDebugLabel(cmd, "Secondary View Opaque");
            try { DrawOpaque(cmd, scene, view, resources, false, feedback); }
            finally { _context.EndDebugLabel(cmd); }
            _context.BeginDebugLabel(cmd, "Secondary View Foliage");
            try { DrawFoliage(cmd, scene, view, resources, feedback); }
            finally { _context.EndDebugLabel(cmd); }
            if (view.IncludesTransparency && resources.Draws.TransparentCommands.Count != 0)
            {
                _context.BeginDebugLabel(cmd, "Secondary View Transparency");
                try
                {
                    Bind(cmd, _mesh.Layout, resources.StorageSet);
                    _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _mesh.TransparentForwardPipeline);
                    DrawMeshlets(cmd, scene, view, resources.Draws.TransparentCommands.Count,
                        BindlessIndex.TransparentMeshletDrawBufferBase, true);
                }
                finally { _context.EndDebugLabel(cmd); }
            }
        }
        finally { _context.KhrDynamicRendering.CmdEndRendering(cmd); }
    }

    private void DrawOpaque(CommandBuffer cmd, SceneRenderingData scene, in SecondaryViewContext view,
        SecondaryViewResources.ViewResources resources, bool prepass, bool feedback)
    {
        Bind(cmd, _mesh.Layout, resources.StorageSet);
        ReadOnlySpan<MaterialForwardClass> classes = [MaterialForwardClass.SimpleOpaque,
            MaterialForwardClass.SimpleOpaqueNormal, MaterialForwardClass.FullOpaque];
        ReadOnlySpan<int> bases = [BindlessIndex.MeshletDrawBufferBase,
            BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase, BindlessIndex.FullOpaqueMeshletDrawBufferBase];
        for (int bucket = 0; bucket < 3; bucket++)
        {
            int count = resources.Draws.Opaque[bucket].Count;
            if (count == 0) continue;
            var key = new ForwardOpaquePipelineKey(AutomaticPlanarCapturePipelineBank.ResolveFamily(
                classes[bucket], !prepass, _mesh.TasklessSubmissionEnabled), feedback
                ? ForwardOpaquePipelineFeatures.AlphaMaskReceiverFeedback : ForwardOpaquePipelineFeatures.None);
            if (!_mesh.TryResolveAutomaticPlanarCapturePipeline(key, prepass, out VkPipeline pipeline))
                throw new InvalidOperationException($"Secondary capture pipeline is unavailable: {key}.");
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            if (!prepass)
            {
                _context.Api.CmdSetCullMode(cmd, CullModeFlags.None);
                _context.Api.CmdSetDepthCompareOp(cmd, CompareOp.GreaterOrEqual);
            }
            DrawMeshlets(cmd, scene, view, count, bases[bucket], false);
        }
    }

    private void DrawMeshlets(CommandBuffer cmd, SceneRenderingData scene, in SecondaryViewContext view,
        int count, int drawBase, bool transparent)
    {
        var push = new GPUForwardPushConstants
        {
            ViewProjectionMatrix = view.ViewProjection, InverseViewMatrix = view.View.Invert(),
            InverseProjectionMatrix = view.Projection.Invert(), CameraPosition = view.Position,
            Time = scene.Time, ScreenDimensions = new Vector2(view.Width, view.Height),
            CurrentFrameIndex = scene.CurrentFrameIndex, MeshletDrawCount = checked((uint)count),
            MeshletDrawBufferBaseIndex = checked((uint)drawBase),
            PackedLightDispatch = GPUForwardPushConstants.PackLightDispatch(scene.LightCount,
                scene.LocalLightCount, scene.DirectionalLightIndex0, scene.DirectionalLightIndex1),
            LocalLightCount = checked((uint)scene.LocalLightCount),
            DebugAndAoFlags = GPUForwardPushConstants.PackDebugAndAoFlags(0, false, 0,
                transparentReceiveShadows: !transparent || scene.TransparentReceiveShadows,
                globalIlluminationEnabled: view.IncludesDdgi && (!transparent || scene.TransparentReceiveGlobalIllumination)),
            DiagnosticFlags = GPUForwardPushConstants.PackDiagnosticFlags(false,
                effectiveReflectionMode: ReflectionMode.Disabled, transparentSampleReflections: false,
                opaqueSceneColorSnapshotAvailable: false,
                geometricSpecularAntialiasingEnabled: scene.SpecularAntialiasingMode == SpecularAntialiasingMode.GeometricVariance),
            CaptureFlags = GPUForwardPushConstants.PackCaptureFlags(true, view.CaptureLayer)
        };
        _context.Api.CmdPushConstants(cmd, _mesh.Layout,
            ShaderStageFlags.MeshBitExt | ShaderStageFlags.TaskBitExt | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(GPUForwardPushConstants), &push);
        _context.ExtMeshShader.CmdDrawMeshTask(cmd, checked((uint)count), 1, 1);
    }

    private void DrawFoliage(CommandBuffer cmd, SceneRenderingData scene, in SecondaryViewContext view,
        SecondaryViewResources.ViewResources resources, bool feedback)
    {
        FoliageRuntimeBuffers buffers = resources.Foliage;
        if (_foliage is null || buffers.ClusterCount <= 0) return;
        Bind(cmd, _foliage.GraphicsLayout, resources.StorageSet);
        var push = new GPUFoliageDrawPushConstants
        {
            ViewProjectionMatrix = view.ViewProjection,
            CameraPositionTime = new Vector4(view.Position.X, view.Position.Y, view.Position.Z, scene.Time),
            ScreenDimensions = new Vector4(view.Width, view.Height, 1f / view.Width, 1f / view.Height),
            CurrentFrameIndex = scene.CurrentFrameIndex, ClusterDrawCount = checked((uint)buffers.VisibleClusterCapacity),
            VisibleClusterBufferBaseIndex = BindlessIndex.FoliageVisibleClusterBufferBase,
            Flags = GPUFoliageDrawPushConstants.PackFlags(false, feedback, view.CaptureLayer, true) |
                (view.IncludesDdgi ? 0u : GPUFoliageDrawPushConstants.DisableGlobalIlluminationFlag),
            ShadowDensityScale = 1f, Padding2 = checked((uint)scene.ObjectCount)
        };
        for (int authored = 0; authored < 2; authored++)
        {
            push.ClusterDrawCount = checked((uint)(authored == 0
                ? buffers.VisibleClusterCapacity : buffers.MeshletDrawCapacity));
            if (!_foliage.TryResolveAutomaticPlanarCapturePipeline(authored != 0, feedback, out VkPipeline pipeline))
                throw new InvalidOperationException("Secondary foliage pipeline is unavailable.");
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            _context.Api.CmdPushConstants(cmd, _foliage.GraphicsLayout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.TaskBitExt | ShaderStageFlags.FragmentBit,
                0, (uint)sizeof(GPUFoliageDrawPushConstants), &push);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(cmd, _buffers.GetBuffer(buffers.IndirectDispatchBuffer),
                authored == 0 ? FoliageManager.ProceduralIndirectDispatchOffset : FoliageManager.AuthoredIndirectDispatchOffset,
                1, (uint)sizeof(DrawMeshTasksIndirectCommandEXT));
        }
    }

    private void DrawSky(CommandBuffer cmd, in SecondaryViewContext view, DescriptorSet storage)
    {
        if (_sky is null || !_settings.Environment.Enabled) return;
        Bind(cmd, _sky.Layout, storage);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _sky.Pipeline);
        var push = new GPUSkyboxPushConstants
        {
            InverseViewMatrix = view.View.Invert(), InverseProjectionMatrix = view.Projection.Invert(),
            EnvironmentTextureIndex = BindlessIndex.EnvironmentCubemapTexture,
            SkyIntensity = _settings.Environment.SkyIntensity, RotationRadians = _settings.Environment.RotationRadians
        };
        _context.Api.CmdPushConstants(cmd, _sky.Layout, ShaderStageFlags.FragmentBit, 0,
            (uint)sizeof(GPUSkyboxPushConstants), &push);
        _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
    }

    private void Bind(CommandBuffer cmd, PipelineLayout layout, DescriptorSet storage)
    {
        DescriptorSet* sets = stackalloc DescriptorSet[2] { storage, _heap.TextureSamplerSet };
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 0, 2, sets, 0, null);
    }

    public void Dispose() => _resources.Dispose();
}
