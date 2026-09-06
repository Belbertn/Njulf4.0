using System.Diagnostics;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using Vma;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed unsafe class SecondaryViewResourceTests
{
    [Test]
    public void RecordedViewsKeepIndependentDrawFrustumAndFoliageBindings()
    {
        string compiler = Path.Combine(Environment.GetEnvironmentVariable("VULKAN_SDK") ?? "", "Bin", "glslangValidator.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(compiler))
            Assert.Ignore("This GPU fixture needs Windows and the Vulkan SDK.");
        byte[] code = Compile(compiler);
        WindowOptions options = WindowOptions.DefaultVulkan;
        options.IsVisible = false;
        options.Size = new Silk.NET.Maths.Vector2D<int>(32, 32);
        using IWindow window = Window.Create(options);
        window.Initialize();
        using var context = new VulkanContext(window, debug: true);
        using var buffers = new BufferManager(context);
        using var heap = new BindlessHeap(context);
        using var views = new SecondaryViewResources(context, buffers, heap);
        BufferHandle main = buffers.CreateBuffer(256, BufferUsageFlags.StorageBufferBit,
            MemoryUsage.AutoPreferHost, AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessSequentialWriteBit);
        var mainWords = new Span<uint>(buffers.GetMappedPointer(main), 64);
        mainWords.Clear(); mainWords[0] = 999; mainWords[1] = 99; mainWords[3] = 123;
        buffers.FlushBuffer(main, 0, 256);
        const int count = SecondaryViewResources.MaximumViews;
        ulong outputSize = (count + 1) * 3 * sizeof(uint);
        BufferHandle output = buffers.CreateBuffer(outputSize, BufferUsageFlags.StorageBufferBit,
            MemoryUsage.AutoPreferHost, AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit);
        foreach (int index in new[] { BindlessIndex.MeshletDrawBufferBase,
                     BindlessIndex.MeshletTaskFrameDataBufferBase, BindlessIndex.FoliageCounterBufferBase })
            heap.RegisterStorageBuffer(index, buffers.GetBuffer(main), 0, 256);
        heap.RegisterStorageBuffer(BindlessIndex.StaticBufferCount - 1,
            buffers.GetBuffer(output), 0, outputSize);

        ShaderModule module = default;
        PipelineLayout layout = default;
        VkPipeline pipeline = default;
        try
        {
            fixed (byte* bytes = code)
            {
                var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length, PCode = (uint*)bytes };
                Check(context.Api.CreateShaderModule(context.Device, &info, null, out module));
            }
            DescriptorSetLayout setLayout = heap.StorageBufferSetLayout;
            var range = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, 4);
            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = 1, PPushConstantRanges = &range };
            Check(context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out layout));
            byte* entry = stackalloc byte[] { 109, 97, 105, 110, 0 };
            var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo,
                Layout = layout, Stage = new PipelineShaderStageCreateInfo {
                    SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                    Module = module, PName = entry } };
            Check(context.Api.CreateComputePipelines(context.Device, default, 1, &pipelineInfo, null, out pipeline));
            var recording = context.BeginSingleTimeCommands();
            CommandBuffer cmd = recording.CommandBuffer;
            uint[] expected = new uint[(count + 1) * 3];
            for (int slot = 0; slot < count; slot++)
            {
                var view = new SecondaryViewContext(Matrix4x4.CreateTranslation(new Vector3(slot, 0, 0)),
                    Matrix4x4.Identity, Vector3.Zero, 32, 32, slot, true, false, default, []);
                var resources = views.Acquire(0, slot, 1);
                resources.Draws.Opaque[0].Add(new GPUMeshletDrawCommand { InstanceId = (uint)slot + 1 });
                views.Prepare(resources, view, 0, new FoliageRuntimeBuffers {
                    ClusterCount = 1, VisibleClusterCapacity = 1, MeshletDrawCapacity = 1,
                    VisibleClusterBufferSize = 256, AuthoredInstanceCommandBufferSize = 256,
                    MeshletDrawBufferSize = 256, CounterBufferSize = 256, IndirectDispatchBufferSize = 256 });
                context.Api.CmdFillBuffer(cmd, buffers.GetBuffer(resources.Foliage.CounterBuffer), 0, 256, (uint)slot + 100);
                var transfer = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
                context.Api.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit,
                    0, 1, &transfer, 0, null, 0, null);
                Dispatch(context, cmd, layout, pipeline, resources.StorageSet, (uint)slot);
                expected[slot * 3] = (uint)slot + 1;
                expected[slot * 3 + 1] = (uint)slot + 100;
                expected[slot * 3 + 2] = BitConverter.SingleToUInt32Bits(SceneDataBuilder.ExtractFrustum(view.ViewProjection).Left.W);
            }
            Dispatch(context, cmd, layout, pipeline, heap.StorageBufferSet, count);
            expected[count * 3] = 99; expected[count * 3 + 1] = 999; expected[count * 3 + 2] = 123;
            var host = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
            context.Api.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit,
                0, 1, &host, 0, null, 0, null);
            context.EndSingleTimeCommands(recording);
            buffers.InvalidateBuffer(output, 0, outputSize);
            Assert.That(new ReadOnlySpan<uint>(buffers.GetMappedPointer(output), expected.Length).ToArray(), Is.EqualTo(expected));
            Assert.Throws<InvalidOperationException>(() => views.Acquire(0, 0, 1));
            context.ThrowIfValidationFailure();
        }
        finally
        {
            if (pipeline.Handle != 0) context.Api.DestroyPipeline(context.Device, pipeline, null);
            if (layout.Handle != 0) context.Api.DestroyPipelineLayout(context.Device, layout, null);
            if (module.Handle != 0) context.Api.DestroyShaderModule(context.Device, module, null);
        }
    }

    private static void Dispatch(VulkanContext context, CommandBuffer cmd, PipelineLayout layout,
        VkPipeline pipeline, DescriptorSet set, uint slot)
    {
        context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &set, 0, null);
        context.Api.CmdPushConstants(cmd, layout, ShaderStageFlags.ComputeBit, 0, 4, &slot);
        context.Api.CmdDispatch(cmd, 1, 1, 1);
    }

    private static byte[] Compile(string compiler)
    {
        string source = Path.Combine(TestContext.CurrentContext.WorkDirectory, "secondary-bindings.comp");
        File.WriteAllText(source, $$"""
            #version 460
            #extension GL_EXT_nonuniform_qualifier : require
            layout(local_size_x=1) in;
            layout(set=0,binding=0,std430) buffer Storage { uint words[]; } buffers[];
            layout(push_constant) uniform Push { uint slot; } pc;
            void main() {
                buffers[{{BindlessIndex.StaticBufferCount - 1}}].words[pc.slot*3] = buffers[{{BindlessIndex.MeshletDrawBufferBase}}].words[1];
                buffers[{{BindlessIndex.StaticBufferCount - 1}}].words[pc.slot*3+1] = buffers[{{BindlessIndex.FoliageCounterBufferBase}}].words[0];
                buffers[{{BindlessIndex.StaticBufferCount - 1}}].words[pc.slot*3+2] = buffers[{{BindlessIndex.MeshletTaskFrameDataBufferBase}}].words[3];
            }
            """);
        var start = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-V", "--target-env", "vulkan1.3", "-o", source + ".spv", source })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            throw new InvalidOperationException("Secondary binding fixture shader compilation failed.");
        return File.ReadAllBytes(source + ".spv");
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Vulkan fixture failed: {result}");
    }
}
