using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Njulf.Shaders;
using NUnit.Framework;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Tests;

[TestFixture]
public sealed class AutomaticPlanarCaptureRasterTests
{
    [Test]
    public void CroppedCaptureHistoryMarksOnlyRenderedPixelsValid()
    {
        using var raster = new PlanarCaptureRaster();
        (float[] alpha, uint[] depth) result;
        try { result = raster.RenderCoverage(); }
        catch (NotSupportedException exception) { Assert.Ignore(exception.Message); return; }
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 8; x++)
        {
            bool rendered = x >= 2 && x < 6 && y >= 1 && y < 3;
            Assert.That(result.alpha[y * 8 + x], Is.EqualTo(rendered ? 1f : 0f), $"confidence ({x},{y})");
            Assert.That(result.depth[y * 8 + x], Is.EqualTo(rendered ? BitConverter.SingleToUInt32Bits(.5f) : 0u), $"depth ({x},{y})");
        }
    }

    [Test]
    public void ProbeDepthDoesNotApplyPlanarExclusionsOrClipPlane()
    {
        using var raster = new PlanarCaptureRaster();
        float[] depth;
        try { depth = raster.Render(true, 5); }
        catch (NotSupportedException exception) { Assert.Ignore(exception.Message); return; }
        float[] expected = [0f, .75f, .25f, .9f, .9f, .9f, .25f, .75f];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < expected.Length; x++)
            Assert.That(depth[y * 8 + x], Is.EqualTo(expected[x]).Within(1e-6f), $"probe depth at ({x},{y})");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CaptureDepth_PreservesNearestSurvivingSurfaceAndDiscardedHoles(bool prepass)
    {
        using var raster = new PlanarCaptureRaster();
        float[] depth;
        try { depth = raster.Render(prepass, 4096); }
        catch (NotSupportedException exception) { Assert.Ignore(exception.Message); return; }
        // These columns are independently constructed draw sequences. In
        // particular, rejected foreground samples must not hide the rear wall.
        float[] expected = [0f, .75f, .25f, .25f, .25f, 0f, .25f, .75f];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < expected.Length; x++)
            Assert.That(depth[y * 8 + x], Is.EqualTo(expected[x]).Within(1e-6f),
                $"capture depth at ({x},{y})");
    }
}

/// <summary>
/// Executes the embedded production Full capture fragment program against an
/// eight-column offscreen fixture. Only the vertex producer is synthetic; the
/// material decoder, alpha expression, exclusion lookup and plane clip are the
/// shipping shader. Its meshlet debug output stops before unrelated lighting.
/// </summary>
internal sealed unsafe class PlanarCaptureRaster : IDisposable
{
    private const uint Width = 8, Height = 4, StorageDescriptors = 1024;
    private readonly Vk _vk = Vk.GetApi();
    private readonly List<Action> _release = [];
    private Instance _instance;
    private PhysicalDevice _physical;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private int _captureLayer;
    private PhysicalDeviceMemoryProperties _memory;

    private readonly record struct HostBuffer(VkBuffer Buffer, DeviceMemory Memory, nint Pointer, ulong Size);
    private readonly record struct Target(VkImage Image, ImageView View, ImageAspectFlags Aspect);

    public float[] Render(bool prepass, int captureLayer)
    {
        _captureLayer = captureLayer;
        byte[] vertex = CompileFixtureVertex();
        Initialize();
        HostBuffer zero = Buffer(8192), materials = Buffer(4096), hotMaterials = Buffer(4096), metadata = Buffer(8192);
        GPUMaterialData solid = MaterialManager.CreateDefaultMaterial();
        solid.NormalScaleBias.W = 1f;
        GPUMaterialData mask = solid;
        mask.NormalScaleBias.Y = 1f;
        mask.NormalScaleBias.Z = .5f;
        GPUMaterialData oneSided = solid;
        oneSided.NormalScaleBias.W = 0f;
        GPUMaterialData[] all = [solid, mask, oneSided];
        all.AsSpan().CopyTo(new Span<GPUMaterialData>((void*)materials.Pointer, all.Length));
        var hot = new Span<GPUForwardMaterialData>((void*)hotMaterials.Pointer, all.Length);
        for (int i = 0; i < all.Length; i++) hot[i] = GPUForwardMaterialData.FromMaterial(all[i]);
        var words = new Span<uint>((void*)metadata.Pointer, 2048);
        words[0] = AutomaticPlanarReflectionManager.MetadataMagic;
        words[1] = AutomaticPlanarReflectionManager.MetadataVersion;
        words[2] = 1;
        words[16 + 2] = BitConverter.SingleToUInt32Bits(1f); // plane z=0
        words[16 + 18] = BitConverter.SingleToUInt32Bits(1f);
        words[16 + 90] = 1; // exact list containing object 2
        words[16 + 91] = AutomaticPlanarReflectionManager.VariableDataWordOffset;
        words[AutomaticPlanarReflectionManager.VariableDataWordOffset] = 2;

        DescriptorSetLayout storageLayout = DescriptorLayout(DescriptorType.StorageBuffer, StorageDescriptors);
        DescriptorSetLayout textureLayout = DescriptorLayout(DescriptorType.CombinedImageSampler, 1);
        var sizes = stackalloc DescriptorPoolSize[] {
            new(DescriptorType.StorageBuffer, StorageDescriptors), new(DescriptorType.CombinedImageSampler, 1) };
        var poolInfo = new DescriptorPoolCreateInfo {
            SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 2, PoolSizeCount = 2, PPoolSizes = sizes };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out DescriptorPool pool));
        _release.Add(() => _vk.DestroyDescriptorPool(_device, pool, null));
        var layouts = stackalloc DescriptorSetLayout[] { storageLayout, textureLayout };
        var allocateSets = new DescriptorSetAllocateInfo {
            SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool,
            DescriptorSetCount = 2, PSetLayouts = layouts };
        var sets = stackalloc DescriptorSet[2];
        Check(_vk.AllocateDescriptorSets(_device, &allocateSets, sets));
        var infos = new DescriptorBufferInfo[StorageDescriptors];
        for (int i = 0; i < infos.Length; i++) infos[i] = new(zero.Buffer, 0, zero.Size);
        infos[BindlessIndex.MaterialDataBuffer] = new(materials.Buffer, 0, materials.Size);
        infos[BindlessIndex.ForwardMaterialDataBuffer] = new(hotMaterials.Buffer, 0, hotMaterials.Size);
        infos[BindlessIndex.AutomaticPlanarReflectionBuffer] = new(metadata.Buffer, 0, metadata.Size);
        fixed (DescriptorBufferInfo* infoPointer = infos)
        {
            var write = new WriteDescriptorSet {
                SType = StructureType.WriteDescriptorSet, DstSet = sets[0],
                DescriptorCount = StorageDescriptors, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infoPointer };
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        }
        var range = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 256);
        var layoutInfo = new PipelineLayoutCreateInfo {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 2, PSetLayouts = layouts,
            PushConstantRangeCount = 1, PPushConstantRanges = &range };
        Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out PipelineLayout layout));
        _release.Add(() => _vk.DestroyPipelineLayout(_device, layout, null));
        VkPipeline pipeline = Pipeline(vertex, LoadShader(prepass
            ? "planar_capture_depth.frag.spv" : "forward_planar_capture_ddgi.frag.spv"), layout);
        Target color = Image(Format.R32G32B32A32Sfloat, ImageAspectFlags.ColorBit, ImageUsageFlags.ColorAttachmentBit);
        Target depth = Image(Format.D32Sfloat, ImageAspectFlags.DepthBit, ImageUsageFlags.DepthStencilAttachmentBit);
        HostBuffer readback = Buffer(Width * Height * sizeof(float));
        CommandBuffer cmd = BeginCommands();
        Transition(cmd, color, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);
        Transition(cmd, depth, ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal);
        var colorAttachment = new RenderingAttachmentInfo {
            SType = StructureType.RenderingAttachmentInfo, ImageView = color.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal, LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store };
        var depthAttachment = new RenderingAttachmentInfo {
            SType = StructureType.RenderingAttachmentInfo, ImageView = depth.View,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal, LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store, ClearValue = new ClearValue(null, new ClearDepthStencilValue(0, 0)) };
        var rendering = new RenderingInfo {
            SType = StructureType.RenderingInfo, RenderArea = new Rect2D(new Offset2D(), new Extent2D(Width, Height)),
            LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment };
        _vk.CmdBeginRendering(cmd, &rendering);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 0, 2, sets, 0, null);
        var viewport = new Viewport(0, 0, Width, Height, 0, 1);
        _vk.CmdSetViewport(cmd, 0, 1, &viewport);
        // Rear wall, plus near/far draws deliberately submitted in both orders.
        for (int x = 1; x < 8; x++) if (x != 5) Draw(cmd, layout, x, .25f);
        Draw(cmd, layout, 1, .75f);
        Draw(cmd, layout, 1, .5f);
        Draw(cmd, layout, 2, .9f, objectIndex: 3, alpha: 0f);
        Draw(cmd, layout, 3, .9f, worldZ: -1f);
        Draw(cmd, layout, 4, .9f, objectIndex: 2);
        Draw(cmd, layout, 5, .9f, objectIndex: 2);
        Draw(cmd, layout, 6, .9f, objectIndex: 4);
        Draw(cmd, layout, 7, .75f, objectIndex: 3);
        _vk.CmdEndRendering(cmd);
        Transition(cmd, depth, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal);
        var copy = new BufferImageCopy {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
            ImageExtent = new Extent3D(Width, Height, 1) };
        _vk.CmdCopyImageToBuffer(cmd, depth.Image, ImageLayout.TransferSrcOptimal, readback.Buffer, 1, &copy);
        var hostBarrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0,
            1, &hostBarrier, 0, null, 0, null);
        Check(_vk.EndCommandBuffer(cmd));
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Check(_vk.QueueSubmit(_queue, 1, &submit, default));
        Check(_vk.QueueWaitIdle(_queue));
        return new ReadOnlySpan<float>((void*)readback.Pointer, (int)(Width * Height)).ToArray();
    }

    public (float[] alpha, uint[] depth) RenderCoverage()
    {
        Initialize();
        Target color = Image(Format.R16G16B16A16Sfloat, ImageAspectFlags.ColorBit,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        Target depth = Image(Format.D32Sfloat, ImageAspectFlags.DepthBit,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        Target history = Image(Format.R32Uint, ImageAspectFlags.ColorBit, ImageUsageFlags.StorageBit);
        HostBuffer colorReadback = Buffer(Width * Height * 8), depthReadback = Buffer(Width * Height * 4);
        DescriptorSetLayout storage = DescriptorLayout(DescriptorType.StorageBuffer, StorageDescriptors);
        DescriptorSetLayout textures = DescriptorLayout(DescriptorType.CombinedImageSampler, 1);
        var bindings = stackalloc DescriptorSetLayoutBinding[5];
        for (uint i = 0; i < 5; i++) bindings[i] = new(i,
            i < 2 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        var localInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 5, PBindings = bindings };
        Check(_vk.CreateDescriptorSetLayout(_device, &localInfo, null, out DescriptorSetLayout local));
        _release.Add(() => _vk.DestroyDescriptorSetLayout(_device, local, null));
        var sizes = stackalloc DescriptorPoolSize[] { new(DescriptorType.StorageBuffer, StorageDescriptors),
            new(DescriptorType.CombinedImageSampler, 3), new(DescriptorType.StorageImage, 3) };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit, MaxSets = 3, PoolSizeCount = 3, PPoolSizes = sizes };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out DescriptorPool pool));
        _release.Add(() => _vk.DestroyDescriptorPool(_device, pool, null));
        var layouts = stackalloc DescriptorSetLayout[] { storage, textures, local };
        var allocate = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool, DescriptorSetCount = 3, PSetLayouts = layouts };
        var sets = stackalloc DescriptorSet[3];
        Check(_vk.AllocateDescriptorSets(_device, &allocate, sets));
        var samplerInfo = new SamplerCreateInfo { SType = StructureType.SamplerCreateInfo,
            MinFilter = Filter.Nearest, MagFilter = Filter.Nearest, MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge };
        Check(_vk.CreateSampler(_device, &samplerInfo, null, out Sampler sampler));
        _release.Add(() => _vk.DestroySampler(_device, sampler, null));
        var images = stackalloc DescriptorImageInfo[] {
            new(sampler, color.View, ImageLayout.General), new(sampler, depth.View, ImageLayout.General),
            new(default, history.View, ImageLayout.General), new(default, history.View, ImageLayout.General),
            new(default, color.View, ImageLayout.General) };
        var writes = stackalloc WriteDescriptorSet[5];
        for (uint i = 0; i < 5; i++) writes[i] = new WriteDescriptorSet {
            SType = StructureType.WriteDescriptorSet, DstSet = sets[2], DstBinding = i, DescriptorCount = 1,
            DescriptorType = bindings[i].DescriptorType, PImageInfo = images + i };
        _vk.UpdateDescriptorSets(_device, 5, writes, 0, null);
        var range = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, 32);
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3, PSetLayouts = layouts, PushConstantRangeCount = 1, PPushConstantRanges = &range };
        Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out PipelineLayout layout));
        _release.Add(() => _vk.DestroyPipelineLayout(_device, layout, null));
        ShaderModule shader = Module(LoadShader("automatic_planar_reproject.comp.spv"));
        byte* entry = stackalloc byte[] { 109, 97, 105, 110, 0 };
        var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo,
            Layout = layout, Stage = new PipelineShaderStageCreateInfo {
                SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                Module = shader, PName = entry } };
        Check(_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, out VkPipeline pipeline));
        _release.Add(() => _vk.DestroyPipeline(_device, pipeline, null));
        CommandBuffer cmd = BeginCommands();
        Transition(cmd, color, ImageLayout.Undefined, ImageLayout.General);
        Transition(cmd, depth, ImageLayout.Undefined, ImageLayout.General);
        Transition(cmd, history, ImageLayout.Undefined, ImageLayout.General);
        var colorRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var depthRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1);
        var colorClear = new ClearColorValue(1f, 2f, 3f, 0f);
        var depthClear = new ClearDepthStencilValue(.5f, 0);
        _vk.CmdClearColorImage(cmd, color.Image, ImageLayout.General, &colorClear, 1, &colorRange);
        _vk.CmdClearDepthStencilImage(cmd, depth.Image, ImageLayout.General, &depthClear, 1, &depthRange);
        Transition(cmd, color, ImageLayout.General, ImageLayout.General);
        Transition(cmd, depth, ImageLayout.General, ImageLayout.General);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 3, sets, 0, null);
        uint* push = stackalloc uint[] { 0, 0, 0, Width, Height, 2u | (1u << 16), 6u | (3u << 16), 0 };
        _vk.CmdPushConstants(cmd, layout, ShaderStageFlags.ComputeBit, 0, 32, push);
        _vk.CmdDispatch(cmd, 1, 1, 1);
        Transition(cmd, color, ImageLayout.General, ImageLayout.TransferSrcOptimal);
        Transition(cmd, history, ImageLayout.General, ImageLayout.TransferSrcOptimal);
        var copy = new BufferImageCopy { ImageSubresource = new(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(Width, Height, 1) };
        _vk.CmdCopyImageToBuffer(cmd, color.Image, ImageLayout.TransferSrcOptimal, colorReadback.Buffer, 1, &copy);
        _vk.CmdCopyImageToBuffer(cmd, history.Image, ImageLayout.TransferSrcOptimal, depthReadback.Buffer, 1, &copy);
        var host = new MemoryBarrier { SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0,
            1, &host, 0, null, 0, null);
        Check(_vk.EndCommandBuffer(cmd));
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Check(_vk.QueueSubmit(_queue, 1, &submit, default));
        Check(_vk.QueueWaitIdle(_queue));
        float[] alpha = new float[Width * Height];
        var pixels = new ReadOnlySpan<Half>((void*)colorReadback.Pointer, alpha.Length * 4);
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (float)pixels[i * 4 + 3];
        return (alpha, new ReadOnlySpan<uint>((void*)depthReadback.Pointer, alpha.Length).ToArray());
    }

    private void Draw(CommandBuffer cmd, PipelineLayout layout, int x, float depth,
        uint objectIndex = 0, float alpha = 1f, float worldZ = 1f)
    {
        var scissor = new Rect2D(new Offset2D(x, 0), new Extent2D(1, Height));
        _vk.CmdSetScissor(cmd, 0, 1, &scissor);
        var push = new GPUForwardPushConstants {
            CameraPosition = new Vector3(0, 0, -1), DebugAndAoFlags = 1,
            CaptureFlags = GPUForwardPushConstants.PackCaptureFlags(true, _captureLayer) };
        // The fixture vertex consumes the first matrix row; the production
        // fragment receives its normal 256-byte push-constant interface.
        float* geometry = (float*)&push;
        geometry[0] = depth; geometry[1] = worldZ; geometry[2] = alpha;
        geometry[3] = BitConverter.UInt32BitsToSingle(objectIndex);
        _vk.CmdPushConstants(cmd, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 256, &push);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
    }

    private void Initialize()
    {
        var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version13 };
        var instanceInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
        Check(_vk.CreateInstance(&instanceInfo, null, out _instance));
        uint count = 0;
        Check(_vk.EnumeratePhysicalDevices(_instance, &count, null));
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices) Check(_vk.EnumeratePhysicalDevices(_instance, &count, pointer));
        foreach (PhysicalDevice candidate in devices)
        {
            var properties = new PhysicalDeviceProperties();
            _vk.GetPhysicalDeviceProperties(candidate, &properties);
            if (properties.ApiVersion < Vk.Version13 || properties.Limits.MaxPushConstantsSize < 256) continue;
            var features13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
            var features12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &features13 };
            var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features12 };
            _vk.GetPhysicalDeviceFeatures2(candidate, &features);
            if (!features13.DynamicRendering || !features13.ShaderDemoteToHelperInvocation ||
                !features12.RuntimeDescriptorArray || !features12.DescriptorBindingPartiallyBound ||
                !features12.DescriptorBindingStorageBufferUpdateAfterBind ||
                !features12.ShaderStorageBufferArrayNonUniformIndexing ||
                !features12.ShaderSampledImageArrayNonUniformIndexing) continue;
            uint familyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, &familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* pointer = families)
                _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, &familyCount, pointer);
            int family = Array.FindIndex(families, value => (value.QueueFlags & QueueFlags.GraphicsBit) != 0);
            if (family < 0) continue;
            _physical = candidate; _queueFamily = (uint)family;
            float priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamily, QueueCount = 1, PQueuePriorities = &priority };
            var deviceInfo = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo,
                PNext = &features, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo };
            Check(_vk.CreateDevice(_physical, &deviceInfo, null, out _device));
            _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
            _vk.GetPhysicalDeviceMemoryProperties(_physical, out _memory);
            return;
        }
        throw new NotSupportedException("The planar raster test needs Vulkan 1.3, descriptor indexing and 256-byte push constants.");
    }

    private DescriptorSetLayout DescriptorLayout(DescriptorType type, uint count)
    {
        var binding = new DescriptorSetLayoutBinding(0, type, count, ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit);
        DescriptorBindingFlags flags = DescriptorBindingFlags.PartiallyBoundBit;
        if (type == DescriptorType.StorageBuffer) flags |= DescriptorBindingFlags.UpdateAfterBindBit;
        var indexing = new DescriptorSetLayoutBindingFlagsCreateInfo {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = 1, PBindingFlags = &flags };
        var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &indexing, Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 1, PBindings = &binding };
        Check(_vk.CreateDescriptorSetLayout(_device, &info, null, out DescriptorSetLayout result));
        _release.Add(() => _vk.DestroyDescriptorSetLayout(_device, result, null));
        return result;
    }

    private VkPipeline Pipeline(byte[] vertex, byte[] fragment, PipelineLayout layout)
    {
        ShaderModule vert = Module(vertex), frag = Module(fragment);
        byte* entry = stackalloc byte[] { 109, 97, 105, 110, 0 };
        var stages = stackalloc PipelineShaderStageCreateInfo[] {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vert, PName = entry },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = frag, PName = entry } };
        var input = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise, LineWidth = 1 };
        var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.GreaterOrEqual };
        var attachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
        var states = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = states };
        Format colorFormat = Format.R32G32B32A32Sfloat;
        var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &colorFormat, DepthAttachmentFormat = Format.D32Sfloat };
        var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo,
            PNext = &rendering, StageCount = 2, PStages = stages, PVertexInputState = &input,
            PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &raster,
            PMultisampleState = &samples, PDepthStencilState = &depth, PColorBlendState = &blend,
            PDynamicState = &dynamic, Layout = layout };
        Check(_vk.CreateGraphicsPipelines(_device, default, 1, &info, null, out VkPipeline result));
        _release.Add(() => _vk.DestroyPipeline(_device, result, null));
        return result;
    }

    private ShaderModule Module(byte[] bytes)
    {
        fixed (byte* pointer = bytes)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)bytes.Length, PCode = (uint*)pointer };
            Check(_vk.CreateShaderModule(_device, &info, null, out ShaderModule result));
            _release.Add(() => _vk.DestroyShaderModule(_device, result, null));
            return result;
        }
    }

    private HostBuffer Buffer(ulong size)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit };
        Check(_vk.CreateBuffer(_device, &info, null, out VkBuffer buffer));
        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);
        DeviceMemory memory = Allocate(requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0));
        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped));
        new Span<byte>(mapped, checked((int)size)).Clear();
        _release.Add(() => { _vk.UnmapMemory(_device, memory); _vk.DestroyBuffer(_device, buffer, null); _vk.FreeMemory(_device, memory, null); });
        return new HostBuffer(buffer, memory, (nint)mapped, size);
    }

    private Target Image(Format format, ImageAspectFlags aspect, ImageUsageFlags usage)
    {
        var info = new ImageCreateInfo { SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
            Format = format, Extent = new Extent3D(Width, Height, 1), MipLevels = 1, ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal, Usage = usage | ImageUsageFlags.TransferSrcBit };
        Check(_vk.CreateImage(_device, &info, null, out VkImage image));
        _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements requirements);
        DeviceMemory memory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        Check(_vk.BindImageMemory(_device, image, memory, 0));
        var viewInfo = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image,
            ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1) };
        Check(_vk.CreateImageView(_device, &viewInfo, null, out ImageView view));
        _release.Add(() => { _vk.DestroyImageView(_device, view, null); _vk.DestroyImage(_device, image, null); _vk.FreeMemory(_device, memory, null); });
        return new Target(image, view, aspect);
    }

    private DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags flags)
    {
        for (uint i = 0; i < _memory.MemoryTypeCount; i++)
        {
            if ((requirements.MemoryTypeBits & (1u << (int)i)) == 0 ||
                (_memory.MemoryTypes[(int)i].PropertyFlags & flags) != flags) continue;
            var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = i };
            Check(_vk.AllocateMemory(_device, &info, null, out DeviceMemory result));
            return result;
        }
        throw new NotSupportedException($"No memory type supports {flags} for the planar raster fixture.");
    }

    private CommandBuffer BeginCommands()
    {
        var info = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _queueFamily };
        Check(_vk.CreateCommandPool(_device, &info, null, out CommandPool pool));
        _release.Add(() => _vk.DestroyCommandPool(_device, pool, null));
        var allocate = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Check(_vk.AllocateCommandBuffers(_device, &allocate, out CommandBuffer cmd));
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(cmd, &begin));
        return cmd;
    }

    private void Transition(CommandBuffer cmd, Target image, ImageLayout before, ImageLayout after)
    {
        var barrier = new ImageMemoryBarrier { SType = StructureType.ImageMemoryBarrier,
            OldLayout = before, NewLayout = after, Image = image.Image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SrcAccessMask = before == ImageLayout.Undefined ? 0 : AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            SubresourceRange = new ImageSubresourceRange(image.Aspect, 0, 1, 0, 1) };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0,
            0, null, 0, null, 1, &barrier);
    }

    private static byte[] LoadShader(string name)
    {
        using Stream stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream($"Njulf.Shaders.{Path.GetFileNameWithoutExtension(name)}")
            ?? throw new InvalidOperationException($"Missing production shader {name}.");
        using var result = new MemoryStream(); stream.CopyTo(result); return result.ToArray();
    }

    private static byte[] CompileFixtureVertex()
    {
        string compiler = Path.Combine(Environment.GetEnvironmentVariable("VULKAN_SDK") ?? string.Empty,
            "Bin", OperatingSystem.IsWindows() ? "glslangValidator.exe" : "glslangValidator");
        if (!File.Exists(compiler))
            throw new NotSupportedException("The planar raster fixture needs glslangValidator in VULKAN_SDK/Bin.");
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "planar-raster-fixture");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "fixture.vert"), output = source + ".spv";
        File.WriteAllText(source, """
            #version 460
            layout(push_constant) uniform Fixture { vec4 geometry; } pc;
            layout(location=0) out vec3 normal;
            layout(location=1) out vec2 uv;
            layout(location=2) flat out uint materialIndex;
            layout(location=3) flat out uint objectIndex;
            layout(location=4) out vec3 worldPosition;
            layout(location=5) out vec4 tangent;
            layout(location=6) flat out uint meshletIndex;
            layout(location=7) out vec2 uv2;
            layout(location=8) out vec4 vertexColor;
            void main() {
                objectIndex = floatBitsToUint(pc.geometry.w);
                materialIndex = objectIndex == 3u ? 1u : objectIndex == 4u ? 2u : 0u;
                vec2 positions[3] = vec2[](vec2(-1,-1), vec2(-1,3), vec2(3,-1));
                uint index = uint(gl_VertexIndex);
                if (objectIndex == 4u) index = index == 1u ? 2u : index == 2u ? 1u : 0u;
                gl_Position = vec4(positions[index], pc.geometry.x, 1);
                worldPosition = vec3(positions[index], pc.geometry.y);
                normal = vec3(0,0,1); tangent = vec4(1,0,0,1);
                uv = vec2(0); uv2 = vec2(0); meshletIndex = 1u;
                vertexColor = vec4(1,1,1,pc.geometry.z);
            }
            """);
        var start = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-V", "--target-env", "vulkan1.3", "-o", output, source }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, stdout + stderr);
        return File.ReadAllBytes(output);
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Planar raster Vulkan operation failed: {result}.");
    }

    public void Dispose()
    {
        if (_device.Handle != 0) _vk.DeviceWaitIdle(_device);
        for (int i = _release.Count - 1; i >= 0; i--) _release[i]();
        if (_device.Handle != 0) _vk.DestroyDevice(_device, null);
        if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }
}
