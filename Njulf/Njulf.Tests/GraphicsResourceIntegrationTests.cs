using Njulf.Core.Math;
using Njulf.Graphics;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Data;
using Silk.NET.Windowing;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class GraphicsResourceIntegrationTests
{
    private IWindow _window = null!;
    private ServiceProvider _services = null!;
    private VulkanRenderer _renderer = null!;
    private VulkanGraphicsDevice _graphics = null!;
    private VulkanContext _context = null!;
    private MeshManager _meshes = null!;
    private BufferManager _buffers = null!;

    [OneTimeSetUp]
    public void OpenDevice()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Vulkan integration fixture requires Windows.");
        var options = WindowOptions.DefaultVulkan;
        options.IsVisible = false;
        options.Size = new(64, 64);
        _window = Window.Create(options);
        _window.Initialize();
        var services = new ServiceCollection();
        services.AddRendering(_window, settings => settings.ValidationSettings = RendererValidationSettings.Default with
        {
            Mode = RendererValidationMode.Standard, FailOnErrorMessage = true
        });
        _services = services.BuildServiceProvider();
        _renderer = _services.GetRequiredService<VulkanRenderer>();
        _graphics = (VulkanGraphicsDevice)((Njulf.Core.Interfaces.IRenderer)_renderer).GetGraphicsDevice();
        _context = _services.GetRequiredService<VulkanContext>();
        _meshes = _services.GetRequiredService<MeshManager>();
        _buffers = _services.GetRequiredService<BufferManager>();
        _renderer.Initialize();
    }

    [OneTimeTearDown]
    public void CloseDevice()
    {
        Task<byte[]>? submitted = null, queued = null;
        if (_graphics != null && _context?.Device.Handle != 0)
        {
            var buffer = _graphics.CreateBuffer(4, new byte[] { 1, 2, 3, 4 });
            submitted = _graphics.ReadBufferAsync(buffer, 0, 4);
            if (_renderer.BeginFrame()) _renderer.EndFrame();
            queued = _graphics.ReadBufferAsync(buffer, 0, 4);
            buffer.Dispose();
        }

        try
        {
            _services?.Dispose();
        }
        finally
        {
            _window?.Dispose();
        }

        if (submitted != null) Assert.That(submitted.IsFaulted, Is.True, "Shutdown must settle submitted readback.");
        if (queued != null) Assert.That(queued.IsFaulted, Is.True, "Shutdown must settle queued readback.");
        if (_context != null) Assert.That(_context.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    private static Material CreateMaterial(GraphicsDevice device) => device.CreateMaterial(MaterialDefinition.Default);

    [TestCase(TextureFormat.R8Unorm, 1)]
    [TestCase(TextureFormat.Rg8Unorm, 2)]
    [TestCase(TextureFormat.Rgba8Unorm, 4)]
    [TestCase(TextureFormat.Rgba8Srgb, 4)]
    [TestCase(TextureFormat.Bgra8Unorm, 4)]
    [TestCase(TextureFormat.Bgra8Srgb, 4)]
    [TestCase(TextureFormat.Rgba16Float, 8)]
    [TestCase(TextureFormat.R32Float, 4)]
    public void ResourceFormatsRoundTripNativeBytes(TextureFormat format, int pixelSize)
    {
        byte[] bytes = Enumerable.Range(1, 6 * pixelSize).Select(i => (byte)i).ToArray();
        using var texture = _graphics.CreateTexture2D(new(2, 3, format), new ReadOnlyMemory<byte>[] { bytes });
        var read = _graphics.ReadTexture2DAsync(texture);
        PumpResourceFrames(read);
        Assert.That(read.Result.Format, Is.EqualTo(format));
        Assert.That(read.Result.Data, Is.EqualTo(bytes));
    }

    [Test]
    public void ResourceValidationRejectsInputsBeforeGpuWork()
    {
        VertexPositionNormalTexture[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero)];
        Assert.Throws<ArgumentOutOfRangeException>(() => _graphics.CreateMesh(vertices, new uint[] { 0, 0, 1 }));
        Assert.Throws<ArgumentException>(() => _graphics.CreateMesh(vertices, new uint[] { 0 }));
        Assert.Throws<ArgumentException>(() =>
            _graphics.CreateMesh(
                new VertexPositionNormalTexture[] { new(new(float.NaN, 0, 0), Vector3.UnitZ, Vector2.Zero) },
                new uint[] { 0, 0, 0 }));
        Assert.Throws<NotSupportedException>(() =>
            _graphics.CreateTexture2D(new(1, 1, TextureFormat.Unknown), new ReadOnlyMemory<byte>[] { new byte[4] }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _graphics.CreateTexture2D(new(1, 1, TextureFormat.Rgba8Unorm, 2),
                new ReadOnlyMemory<byte>[] { new byte[4] }));
        using var texture = _graphics.CreateTexture2D(1, 1, new byte[4], TextureColorSpace.Linear);
        Assert.Throws<ArgumentException>(() => _graphics.UpdateTexture2D(texture, 0, new(0, 0, 1, 1), new byte[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _graphics.UpdateTexture2D(texture, 1, new(0, 0, 1, 1), new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _graphics.UpdateTexture2D(texture, 0, new(1, 0, 1, 1), new byte[4]));
        using var buffer = _graphics.CreateBuffer(4);
        Assert.Throws<InvalidOperationException>(() =>
            Task.Run(() => _graphics.UpdateBuffer(buffer, 0, new byte[4])).GetAwaiter().GetResult());
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _graphics.ReadBufferAsync(buffer, 0, 4));
    }

    private void PumpResourceFrames(Task task)
    {
        for (int frame = 0; frame < 12 && !task.IsCompleted; frame++)
        {
            _window.DoEvents();
            if (!_renderer.BeginFrame()) continue;
            _renderer.EndFrame();
        }

        Assert.That(task.IsCompleted, Is.True, "Readback did not complete while pumping frames.");
        task.GetAwaiter().GetResult();
    }

    [Test]
    public void ResourceBufferUploadsRoundTripAndRetainDisposedWrapper()
    {
        var buffer = _graphics.CreateBuffer(8, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        _graphics.UpdateBuffer(buffer, 3, new byte[] { 91, 92 });
        Task<byte[]> result = _graphics.ReadBufferAsync(buffer, 1, 6);
        Assert.That(result.IsCompleted, Is.False);
        buffer.Dispose();
        PumpResourceFrames(result);
        Assert.That(result.Result, Is.EqualTo(new byte[] { 2, 3, 91, 92, 6, 7 }));
    }

    [Test]
    public void ResourceTextureUpdatesPreservePixelsAndAuthoredMips()
    {
        byte[] original = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using var texture = _graphics.CreateTexture2D(new(2, 2, TextureFormat.Rgba8Unorm, 2),
            new ReadOnlyMemory<byte>[] { original, new byte[] { 61, 62, 63, 64 } });
        _graphics.UpdateTexture2D(texture, 0, new(1, 0, 1, 1), new byte[] { 101, 102, 103, 104 });
        var first = _graphics.ReadTexture2DAsync(texture);
        var mip = _graphics.ReadTexture2DAsync(texture, 1);
        PumpResourceFrames(Task.WhenAll(first, mip));
        byte[] expected = original.ToArray();
        new byte[] { 101, 102, 103, 104 }.CopyTo(expected, 4);
        Assert.That(first.Result.Data, Is.EqualTo(expected));
        Assert.That(first.Result.RowPitch, Is.EqualTo(8));
        Assert.That(mip.Result.Data, Is.EqualTo(new byte[] { 61, 62, 63, 64 }));
        _graphics.GenerateMipmaps(texture);
        var regenerated = _graphics.ReadTexture2DAsync(texture, 1);
        PumpResourceFrames(regenerated);
        Assert.That(regenerated.Result.Data, Is.Not.EqualTo(mip.Result.Data));
    }

    [Test]
    public void ResourceGeneratedMipsAndHdrPreserveNativeValues()
    {
        byte[] uniform = Enumerable.Repeat(new byte[] { 32, 64, 96, 255 }, 15).SelectMany(x => x).ToArray();
        using var texture = _graphics.CreateTexture2D(new(5, 3, TextureFormat.Rgba8Unorm, 3),
            new ReadOnlyMemory<byte>[] { uniform }, true);
        var mip = _graphics.ReadTexture2DAsync(texture, 2);
        byte[] hdr = System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(new Half[] { (Half)4, (Half)2, (Half).5f, (Half)1 }.AsSpan()).ToArray();
        using var high =
            _graphics.CreateTexture2D(new(1, 1, TextureFormat.Rgba16Float), new ReadOnlyMemory<byte>[] { hdr });
        var readHdr = _graphics.ReadTexture2DAsync(high);
        PumpResourceFrames(Task.WhenAll(mip, readHdr));
        Assert.That(mip.Result.Data, Is.EqualTo(new byte[] { 32, 64, 96, 255 }));
        Assert.That(readHdr.Result.Data, Is.EqualTo(hdr));
    }

    [Test]
    public void ResourceDynamicUpdatesRefreshRetainedBoundsAndCpuGeometry()
    {
        VertexPositionNormalTextureTangent[] vertices =
        [
            new(new(-1, 0, 0), Vector3.UnitZ, new(0, 0), new(1, 0, 0, -1)),
            new(new(1, 0, 0), Vector3.UnitZ, new(1, 0), new(1, 0, 0, -1)),
            new(new(0, 1, 0), Vector3.UnitZ, new(0, 1), new(1, 0, 0, -1))
        ];
        using var mesh = _graphics.CreateMesh(vertices, new uint[] { 0, 1, 2 }, MeshUsage.Dynamic);
        using var material = CreateMaterial(_graphics);
        using var first = _graphics.CreateRenderObject(mesh, material);
        using var second = _graphics.CreateRenderObject(mesh, material);
        ulong revision = first.Revision;
        vertices[2] = vertices[2] with { Position = new(0, 9, 0) };
        _graphics.UpdateMeshVertices(mesh, vertices);
        using var marker = _graphics.CreateBuffer(4, new byte[] { 0, 0, 0, 0 });
        var read = _graphics.ReadBufferAsync(marker, 0, 4);
        PumpResourceFrames(read);
        Assert.That(first.Revision, Is.GreaterThan(revision));
        Assert.That(first.Mesh!.Bounds, Is.EqualTo(mesh.Bounds));
        Assert.That(second.Mesh!.Bounds, Is.EqualTo(mesh.Bounds));
        var native = (VulkanMesh)mesh;
        Assert.That(_meshes.GetMeshInfo(native.Handle).BoundingBoxMax.Y, Is.EqualTo(9));
        Assert.That(_meshes.GetTransportGeometry(native.Handle).VertexPositions.Span[2].Position.Y, Is.EqualTo(9));
        var normals = ReadMeshStream(_meshes.VertexNormalTangentBuffer,
            (ulong)_meshes.GetMeshInfo(native.Handle).VertexOffset * 32, 32);
        var normalFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(normals);
        Assert.That(normalFloats[2], Is.EqualTo(1));
        Assert.That(normalFloats[4], Is.EqualTo(1));
        Assert.That(normalFloats[7], Is.EqualTo(-1));
        var uvBytes = ReadMeshStream(_meshes.VertexUvColorBuffer,
            (ulong)(_meshes.GetMeshInfo(native.Handle).VertexOffset + 1) * 32, 8);
        Assert.That(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(uvBytes)[0], Is.EqualTo(1));
        Assert.That(() => _graphics.UpdateMeshVertices(mesh, vertices.AsSpan(0, 2)), Throws.ArgumentException);
    }

    private unsafe byte[] ReadMeshStream(BufferHandle source, ulong offset, int length)
    {
        using var destination = _graphics.CreateBuffer((ulong)length);
        var upload = _context.BeginSingleTimeCommands();
        var copy = new Silk.NET.Vulkan.BufferCopy(offset, 0, (ulong)length);
        _context.Api.CmdCopyBuffer(upload.CommandBuffer, _buffers.GetBuffer(source),
            _buffers.GetBuffer(destination.Handle), 1, &copy);
        _context.EndSingleTimeCommands(upload);
        var readback = _graphics.ReadBufferAsync(destination, 0, length);
        PumpResourceFrames(readback);
        return readback.Result;
    }

    [Test]
    public void ResourceSamplerAliasesObserveUpdatesAndContentRevisions()
    {
        using var texture = _graphics.CreateTexture2D(1, 1, new byte[] { 10, 20, 30, 255 }, TextureColorSpace.Srgb);
        var definition = MaterialDefinition.Default;
        definition = definition with
        {
            BaseColor = definition.BaseColor with
            {
                Sampler = definition.BaseColor.Sampler with { WrapU = TextureWrapMode.ClampToEdge }
            }
        };
        using var material = _graphics.CreateMaterial(definition, [new(MaterialTextureSlot.BaseColor, texture)]);
        using var alias = material.RetainTexture(MaterialTextureSlot.BaseColor)!;
        var textures = _services.GetRequiredService<TextureManager>();
        uint revision = textures.GetTextureContentRevision(texture.Handle);
        _graphics.UpdateTexture2D(texture, 0, new(0, 0, 1, 1), new byte[] { 91, 92, 93, 255 });
        var read = _graphics.ReadTexture2DAsync(alias);
        PumpResourceFrames(read);
        Assert.That(read.Result.Data, Is.EqualTo(new byte[] { 91, 92, 93, 255 }));
        Assert.That(textures.GetTextureContentRevision(texture.Handle), Is.GreaterThan(revision));
        Assert.That(textures.GetTextureContentRevision(((VulkanTexture)alias).Handle),
            Is.EqualTo(textures.GetTextureContentRevision(texture.Handle)));
    }

    [Test]
    public void ResourceCancellationAfterSubmissionRetainsStorageUntilCompletion()
    {
        var buffer = _graphics.CreateBuffer(4, new byte[] { 1, 2, 3, 4 });
        using var cancellation = new CancellationTokenSource();
        var read = _graphics.ReadBufferAsync(buffer, 0, 4, cancellation.Token);
        Assert.That(_renderer.BeginFrame(), Is.True);
        _renderer.EndFrame();
        cancellation.Cancel();
        buffer.Dispose();
        Assert.That(read.IsCanceled, Is.True);
        using var marker = _graphics.CreateBuffer(4, new byte[4]);
        var completed = _graphics.ReadBufferAsync(marker, 0, 4);
        PumpResourceFrames(completed);
    }

    [Test]
    public void ResourceDynamicMeshBuildsAndRefitsRealAccelerationStructures()
    {
        if (!_context.RayQuerySupported) Assert.Ignore("Ray queries are unavailable on this device.");
        using var rays = new AccelerationStructureManager(_context, _buffers, _meshes,
            _services.GetRequiredService<MaterialManager>());
        VertexPositionNormalTexture[] vertices =
        [
            new(new(-1, 0, 0), Vector3.UnitZ, Vector2.Zero),
            new(new(1, 0, 0), Vector3.UnitZ, Vector2.UnitX), new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY)
        ];
        using var mesh = _graphics.CreateMesh(vertices, new uint[] { 0, 1, 2 }, MeshUsage.Dynamic);
        using var material = CreateMaterial(_graphics);
        using var scene = new Njulf.Core.Scene.Scene();
        scene.Add(_graphics.CreateRenderObject(mesh, material));
        var policy = DdgiDynamicRayScenePolicy.LegacyBaseline with
        {
            DynamicStorageBudgetBytes = 64 * 1024 * 1024,
            DynamicScratchBudgetBytes = 16 * 1024 * 1024, MaximumBuildsPerFrame = 16, MaximumPrimitivesPerFrame = 1024
        };
        var first = Build();
        Assert.That(first.DynamicFullBuildCount, Is.EqualTo(1));
        Assert.That(first.DynamicProxyFallbackCount, Is.Zero);
        vertices[2] = vertices[2] with { Position = new(0, 3, 0) };
        _graphics.UpdateMeshVertices(mesh, vertices);
        using var marker = _graphics.CreateBuffer(4, new byte[4]);
        PumpResourceFrames(_graphics.ReadBufferAsync(marker, 0, 4));
        var second = Build();
        Assert.That(second.DynamicRefitCount, Is.EqualTo(1));
        Assert.That(second.TopLevelInstanceCount, Is.EqualTo(1));
        Assert.That(rays.StaticBottomLevelCount, Is.Zero);
        var native = (VulkanMesh)mesh;
        byte[] positions = ReadMeshStream(_meshes.VertexPositionBuffer,
            (ulong)(_meshes.GetMeshInfo(native.Handle).VertexOffset + 2) * 16, 16);
        Assert.That(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(positions)[1], Is.EqualTo(3));

        AccelerationStructureFrameStats Build()
        {
            var plan = rays.PrepareFrameRayScene(scene, true, 0, null, policy);
            Assert.That(plan.CurrentPoseInstanceCount, Is.EqualTo(1));
            _context.WaitIdle();
            var staging = _services.GetRequiredService<StagingRing>();
            staging.BeginFrame(0);
            var upload = _context.BeginSingleTimeCommands();
            AccelerationStructureFrameStats result;
            try
            {
                result = rays.RecordDynamicRaySceneBuilds(staging, upload.CommandBuffer, BufferHandle.Invalid);
                staging.FlushCurrentFrame();
            }
            catch
            {
                _context.AbortSingleTimeCommands(upload);
                throw;
            }

            _context.EndSingleTimeCommands(upload);
            return result;
        }
    }

    [Test]
    public void ResourceTransfersRejectInvalidRangesAndBoundReadbackQueue()
    {
        using var buffer = _graphics.CreateBuffer(8);
        Assert.That(() => _graphics.UpdateBuffer(buffer, ulong.MaxValue, new byte[] { 1 }),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        using var cancellation = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 8).Select(_ => _graphics.ReadBufferAsync(buffer, 0, 4, cancellation.Token))
            .ToArray();
        Assert.That(() => _graphics.ReadBufferAsync(buffer, 0, 4), Throws.InvalidOperationException);
        cancellation.Cancel();
        for (int i = 0; i < 3; i++)
        {
            if (_renderer.BeginFrame()) _renderer.EndFrame();
        }

        Assert.That(requests.All(r => r.IsCanceled), Is.True);
    }
}