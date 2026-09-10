using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Assets.Scenes;
using Njulf.Core.Scene;
using Njulf.Editor;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Graphics.Vulkan;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed partial class GraphicsApiIntegrationTests
{
    private sealed class EditorModelContent(Model model) : IContentManager
    {
        public T Load<T>(string path) => (T)(object)model;
        public T Load<T>(string path, ContentLoadOptions options) => Load<T>(path);

        public Task<T> LoadAsync<T>(string path, ContentLoadOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Load<T>(path));

        public Task<ContentPreloadResult<T>> PreloadAsync<T>(IEnumerable<ContentPreloadRequest> requests,
            ContentPreloadOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Unload<T>(T asset)
        {
        }

        public void UnloadAll()
        {
        }
    }

    [Test]
    public void EditorEnvironmentEditsAreSavedAndReboundWithoutDrawing()
    {
        using var template = new Model();
        using var first = new Scene();
        using var second = new Scene { Environment = new SceneEnvironment { SkyIntensity = 0.25f } };
        var editor = new EditorController(first, new EditorModelContent(template),
            _services.GetRequiredService<LightManager>(), _materials);
        editor.EditableEnvironment.SkyIntensity = 0.75f;
        editor.EditableEnvironment.AnimateTimeOfDay = true;
        editor.CommitEnvironmentEdits();
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(first.Environment!.SkyIntensity, Is.EqualTo(0.75f));
        Assert.That(new SceneDocumentWriter().CreateDocument(first).Environment!.AnimateTimeOfDay, Is.True);
        editor.SetScene(second);
        Assert.That(editor.EditableEnvironment.SkyIntensity, Is.EqualTo(0.25f));
        Assert.That(editor.IsDirty, Is.False);
        editor.EditableEnvironment.SkyIntensity = 0.5f;
        editor.CommitEnvironmentEdits();
        Assert.That(first.Environment.SkyIntensity, Is.EqualTo(0.75f));
        Assert.That(second.Environment!.SkyIntensity, Is.EqualTo(0.5f));
        second.Environment = second.Environment with { SkyIntensity = 2 };
        Assert.That(editor.EditableEnvironment.SkyIntensity, Is.EqualTo(2));
        SceneEnvironment unchanged = second.Environment;
        editor.CommitEnvironmentEdits();
        Assert.That(second.Environment, Is.SameAs(unchanged));
    }

    [Test]
    public void EditorLightsCanBeEditedSavedAndReboundBeforeGpuMirroring()
    {
        using var template = new Model();
        using var first = new Scene();
        using var second = new Scene();
        var manager = _services.GetRequiredService<LightManager>();
        int originalGpuCount = manager.LightCount;
        var editor = new EditorController(first, new EditorModelContent(template), manager, _materials);
        Guid firstId = editor.AddLight(new Light { Type = LightType.Point, Intensity = 3, Range = 7 }, "First");
        Assert.That(manager.TryGetLightHandle(firstId, out _), Is.False);
        Assert.That(editor.SelectEntity(EditorSelectionKind.Light, firstId), Is.True);
        Assert.That(editor.TryGetSelectedLight(out Light draft), Is.True);
        first.Lights.Single().IesProfile = new SceneAssetReference { Path = "unresolved-source.ies" };
        draft.Intensity = 8;
        draft.CastsShadows = true;
        draft.ShadowPriority = 12;
        Assert.That(editor.UpdateSelectedLight(draft), Is.True);
        Assert.That(editor.SetSelectedLightName("Edited before draw"), Is.True);
        SceneLightDocument saved = new SceneDocumentWriter().CreateDocument(first).Lights.Single();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Id, Is.EqualTo(firstId));
            Assert.That(saved.Intensity, Is.EqualTo(8));
            Assert.That(saved.CastsShadows, Is.True);
            Assert.That(saved.ShadowPriority, Is.EqualTo(12));
            Assert.That(saved.Name, Is.EqualTo("Edited before draw"));
            Assert.That(saved.IesProfile?.Path, Is.EqualTo("unresolved-source.ies"));
            Assert.That(editor.GetLights().Single().Id, Is.EqualTo(firstId));
        });
        editor.SetScene(second);
        Assert.That(editor.Selection.IsEmpty, Is.True);
        Assert.That(editor.SelectEntity(EditorSelectionKind.Light, firstId), Is.False);
        Guid secondId = editor.AddLight(new Light { Type = LightType.Spot, Intensity = 2 }, "Second");
        Assert.That(second.Lights.Single().Id, Is.EqualTo(secondId));
        Assert.That(editor.DeleteSelection(), Is.True);
        Assert.That(second.Lights, Is.Empty);
        Assert.That(first.Lights.Single().Id, Is.EqualTo(firstId));
        Assert.That(manager.LightCount, Is.EqualTo(originalGpuCount));
    }

    [Test]
    public void EditorPlacesOnlySelectedSubobjectAndDeletesWholeOwnedPlacements()
    {
        using var template = new Model();
        template.Add(new RenderObject { Name = "first" });
        template.Add(new RenderObject { Name = "second" });
        using var scene = new Scene();
        var editor = new EditorController(scene, new EditorModelContent(template),
            _services.GetRequiredService<LightManager>(), _materials);
        Assert.Throws<InvalidOperationException>(() => editor.AddObject(
            new SceneAssetReference { Path = "test.glb", SubObject = "*" }, Vector3.Zero));
        Assert.That(scene.RenderObjects, Is.Empty);
        RenderObject standalone = editor.AddObject(
            new SceneAssetReference { Path = "test.glb", SubObject = "second" }, new Vector3(1, 2, 3));
        Assert.That(standalone, Is.Not.SameAs(template.RenderObjects[1]));
        Assert.That(scene.RenderObjects.Single(), Is.SameAs(standalone));
        Assert.That(scene.FindOwningInstance(standalone), Is.Null);
        Assert.That(editor.DeleteSelection(), Is.True);
        ModelInstance placement = template.CreateInstance();
        RenderObject child = placement.RenderObjects[0];
        scene.Add(placement);
        scene.Add((Njulf.Core.Interfaces.IUpdateable)child);
        Assert.That(editor.SelectEntity(EditorSelectionKind.Object, child.Id), Is.True);
        Assert.That(editor.DeleteSelection(), Is.True);
        Assert.That(scene.RenderObjects, Is.Empty);
        Assert.That(scene.Updateables, Does.Not.Contain(child));
        Assert.That(scene.ModelInstances, Is.Empty);
        Assert.That(placement.IsDisposed, Is.True);
        Assert.That(template.RenderObjects.Count, Is.EqualTo(2));
    }

    private sealed class EmptyNativePass : IVulkanRenderPass
    {
        public void Initialize(VulkanDeviceInfo device)
        {
        }

        public void ResourcesChanged(VulkanPassContext context)
        {
        }

        public void Record(VulkanPassContext context)
        {
        }

        public void Dispose()
        {
        }
    }

    [Test]
    public void AnimatedSceneLightMirroringDoesNotAllocateAfterWarmup()
    {
        var manager = _services.GetRequiredService<LightManager>();
        var coordinator = new SceneLightingCoordinator(manager);
        var environment = new EnvironmentSettings();
        using var scene = new Scene();
        using var empty = new Scene();
        var light = new SceneLight { Type = SceneLightType.Point, Intensity = 3 };
        scene.Add(light);
        Action<Light, Light> ignoreShadowEdit = static (_, _) => { };
        try
        {
            for (int i = 0; i < 64; i++)
            {
                light.Position = new Vector3(i, 2, 3);
                coordinator.Synchronize(scene, environment, ignoreShadowEdit);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 64; i < 192; i++)
            {
                light.Position = new Vector3(i, 2, 3);
                coordinator.Synchronize(scene, environment, ignoreShadowEdit);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
            Assert.That(manager.TryGetLightHandle(light.Id, out LightHandle handle), Is.True);
            Assert.That(manager.TryGetLight(handle, out Light mirrored), Is.True);
            Assert.That(mirrored.Position.X, Is.EqualTo(191));
        }
        finally
        {
            coordinator.Synchronize(empty, environment, ignoreShadowEdit);
        }
    }

    [Test]
    public void CustomResourceDeclarationsRejectDisposedResourcesFeedbackAndInvalidRanges()
    {
        using var target = _graphics.CreateRenderTarget2D(16, 16);
        using var buffer = _graphics.CreateBuffer(32);
        var image = new VulkanImageUse("image", Njulf.Rendering.Pipeline.RenderGraphResourceAccess.Read,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal, Texture: target);
        Assert.That(() => _graphics.AddVulkanPass(new("Invalid.feedback", VulkanPassKind.Graphics,
            VulkanPassStage.BeforeScene,
            [image, image with { Name = "alias" }]), new EmptyNativePass()), Throws.ArgumentException);
        Assert.That(() => _graphics.AddVulkanPass(new("Invalid.range", VulkanPassKind.Compute,
            VulkanPassStage.BeforeScene,
            [
                new VulkanBufferUse("data", Njulf.Rendering.Pipeline.RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit, buffer, 16, 32)
            ]), new EmptyNativePass()), Throws.TypeOf<ArgumentOutOfRangeException>());
        target.Dispose();
        Assert.That(() => _graphics.AddVulkanPass(new("Invalid.disposed", VulkanPassKind.Graphics,
            VulkanPassStage.BeforeScene,
            [image]), new EmptyNativePass()), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void CapabilitiesDistinguishDeviceSupportFromActualExecution()
    {
        var capabilities = _graphics.Capabilities;
        Assert.That(capabilities.CustomComputePasses, Is.True);
        Assert.That(capabilities.MeshShaders.DeviceEnabled, Is.True);
        Assert.That(capabilities.HasProductionFrame, Is.False);
        Assert.That(capabilities.MeshShaders.Active, Is.False);
        Assert.That(capabilities.OpacityMicromaps.Active, Is.False);
        Assert.That(capabilities.MeshShaders.Reason, Does.Contain("No production frame"));
    }

    [Test]
    public void SceneSwitchRemovesOnlyMirroredLightsAndRestoresEnvironmentDefaults()
    {
        var lights = _services.GetRequiredService<LightManager>();
        LightHandle advanced = lights.AddLightHandle(new Light { Type = LightType.Point, Intensity = 2 });
        var sync = new SceneLightingCoordinator(lights);
        var settings = new EnvironmentSettings { SkyIntensity = 3 };
        using var first = new Njulf.Core.Scene.Scene
        {
            Environment = new Njulf.Core.Scene.SceneEnvironment { SkyIntensity = 7 }
        };
        using var second = new Njulf.Core.Scene.Scene();
        var source = new Njulf.Core.Scene.SceneLight { Intensity = 5 };
        first.Add(source);
        sync.Synchronize(first, settings, (_, _) => { });
        Assert.That(lights.TryGetLightHandle(source.Id, out var mirrored), Is.True);
        Assert.That(settings.SkyIntensity, Is.EqualTo(7));
        source.Intensity = 9;
        sync.Synchronize(first, settings, (_, _) => { });
        Assert.That(lights.TryGetLight(mirrored, out Light changed), Is.True);
        Assert.That(changed.Intensity, Is.EqualTo(9));
        sync.Synchronize(second, settings, (_, _) => { });
        Assert.That(lights.TryGetLight(mirrored, out _), Is.False);
        Assert.That(lights.TryGetLight(advanced, out _), Is.True);
        Assert.That(settings.SkyIntensity, Is.EqualTo(3));
        lights.RemoveLight(advanced);
    }

    private IWindow _window = null!;
    private ServiceProvider _services = null!;
    private VulkanRenderer _renderer = null!;
    private VulkanGraphicsDevice _graphics = null!;
    private VulkanContext _context = null!;
    private BufferManager _buffers = null!;
    private MeshManager _meshes = null!;
    private MaterialManager _materials = null!;
    private bool _initialized;

    [OneTimeSetUp]
    public void OpenDevice()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("The renderer's Vulkan integration fixture requires Windows.");
        var options = WindowOptions.DefaultVulkan;
        options.IsVisible = false;
        options.Size = new Silk.NET.Maths.Vector2D<int>(64, 64);
        _window = Window.Create(options);
        _window.Initialize();
        var services = new ServiceCollection();
        services.AddRendering(_window, options => options.ValidationSettings =
            RendererValidationSettings.Default with
            {
                Mode = RendererValidationMode.Standard, FailOnErrorMessage = true
            });
        _services = services.BuildServiceProvider();
        _renderer = _services.GetRequiredService<VulkanRenderer>();
        _graphics = (VulkanGraphicsDevice)((IRenderer)_renderer).GetGraphicsDevice();
        _context = _services.GetRequiredService<VulkanContext>();
        _buffers = _services.GetRequiredService<BufferManager>();
        _meshes = _services.GetRequiredService<MeshManager>();
        _materials = _services.GetRequiredService<MaterialManager>();
        Assert.That(() => CreateMesh(_graphics), Throws.InvalidOperationException);
        _renderer.Initialize();
        _initialized = true;
    }

    [OneTimeTearDown]
    public void CloseDevice()
    {
        VulkanMesh? lateMesh = _initialized ? CreateMesh(_graphics) : null;
        VulkanMaterial? lateMaterial = _initialized ? CreateMaterial(_graphics) : null;
        try
        {
            _services?.Dispose();
        }
        finally
        {
            _window?.Dispose();
        }

        lateMesh?.Dispose();
        lateMaterial?.Dispose();
        if (_initialized)
            Assert.That(() => _graphics.GetVulkanDeviceInfo(), Throws.TypeOf<ObjectDisposedException>());
        if (_context != null)
            Assert.That(_context.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    [Test]
    public void ProviderReturnsSameDeviceAndUsesExistingNativeDevice()
    {
        Assert.That(((IRenderer)_renderer).GetGraphicsDevice(), Is.SameAs(_graphics));
        VulkanDeviceInfo native = _graphics.GetVulkanDeviceInfo();
        Assert.Multiple(() =>
        {
            Assert.That(native.Api, Is.SameAs(_context.Api));
            Assert.That(native.Instance.Handle, Is.EqualTo(_context.Instance.Handle));
            Assert.That(native.PhysicalDevice.Handle, Is.EqualTo(_context.PhysicalDevice.Handle));
            Assert.That(native.Device.Handle, Is.EqualTo(_context.Device.Handle));
        });
    }

    [Test]
    public void ObjectsRetainResourcesAfterWrappersAndSiblingAreDisposed()
    {
        using var mesh = CreateMesh(_graphics);
        using var material = CreateMaterial(_graphics);
        using var first = _graphics.CreateRenderObject(mesh, material);
        using var second = _graphics.CreateRenderObject(mesh, material);
        MeshHandle meshHandle = mesh.Handle;
        MaterialHandle materialHandle = material.Handle;
        mesh.Dispose();
        material.Dispose();
        mesh.Dispose();
        material.Dispose();
        first.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(_meshes.GetMeshInfo(meshHandle).VertexCount, Is.EqualTo(3));
            Assert.That(_materials.GetMaterialDefinition(materialHandle).Name, Is.EqualTo("Graphics API test"));
            Assert.That(second.LocalMeshBounds!.Value.Min, Is.EqualTo(new Vector3(-1, -1, 0)));
            Assert.That(second.LocalMeshBounds.Value.Max, Is.EqualTo(new Vector3(1, 1, 0)));
            Assert.That(() => _graphics.CreateRenderObject(mesh, material), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => _graphics.GetVulkanMeshBindings(mesh), Throws.TypeOf<ObjectDisposedException>());
        });
        second.Dispose();
        Assert.That(() => _meshes.GetMeshInfo(meshHandle), Throws.InvalidOperationException);
        Assert.That(() => _materials.GetMaterialDefinition(materialHandle), Throws.InvalidOperationException);
    }

    [Test]
    public void WrongOwnerAndWrongThreadAreRejectedWithoutConsumingReferences()
    {
        using var mesh = CreateMesh(_graphics);
        using var material = CreateMaterial(_graphics);
        // Colliding handles from a foreign device must fail before any manager access.
        object foreignOwner = new();
        using var otherMesh = new VulkanMesh(foreignOwner, mesh.Handle, mesh.Bounds,
            _ => Assert.Fail("Foreign mesh retained"), static _ => { }, static () => { });
        using var otherMaterial = new VulkanMaterial(foreignOwner, material.Handle, material.Name,
            _ => Assert.Fail("Foreign material retained"), static _ => { }, static () => { });
        Assert.Multiple(() =>
        {
            Assert.That(() => _graphics.CreateRenderObject(otherMesh, material), Throws.ArgumentException);
            Assert.That(() => _graphics.CreateRenderObject(mesh, otherMaterial), Throws.ArgumentException);
            Assert.That(() => _graphics.GetVulkanMeshBindings(otherMesh), Throws.ArgumentException);
        });
        Task.Run(() =>
        {
            Assert.That(() => CreateMesh(_graphics), Throws.InvalidOperationException);
            Assert.That(() => _graphics.GetVulkanDeviceInfo(), Throws.InvalidOperationException);
            Assert.That(() => mesh.Dispose(), Throws.InvalidOperationException);
        }).GetAwaiter().GetResult();
        Assert.That(mesh.IsDisposed, Is.False);
        using var instance = _graphics.CreateRenderObject(mesh, material);
        Assert.That(_meshes.GetMeshInfo(mesh.Handle).VertexCount, Is.EqualTo(3));
    }

    [Test]
    public unsafe void NativeSlicesDescribeCurrentProductionAllocations()
    {
        using var first = CreateMesh(_graphics);
        using var second = CreateMesh(_graphics);
        VulkanMeshBindings bindings = _graphics.GetVulkanMeshBindings(second);
        MeshInfo info = _meshes.GetMeshInfo(second.Handle);
        Assert.Multiple(() =>
        {
            Assert.That(bindings.Positions.Buffer.Handle,
                Is.EqualTo(_buffers.GetBuffer(_meshes.VertexPositionBuffer).Handle));
            Assert.That(bindings.NormalsAndTangents.Buffer.Handle,
                Is.EqualTo(_buffers.GetBuffer(_meshes.VertexNormalTangentBuffer).Handle));
            Assert.That(bindings.UvsAndColors.Buffer.Handle,
                Is.EqualTo(_buffers.GetBuffer(_meshes.VertexUvColorBuffer).Handle));
            Assert.That(bindings.Indices.Buffer.Handle, Is.EqualTo(_buffers.GetBuffer(_meshes.IndexBuffer).Handle));
            Assert.That(bindings.Positions.Offset, Is.EqualTo((ulong)info.VertexOffset * 16));
            Assert.That(bindings.Positions.Length, Is.EqualTo(3 * 16));
            Assert.That(bindings.NormalsAndTangents.Offset, Is.EqualTo((ulong)info.VertexOffset * 32));
            Assert.That(bindings.UvsAndColors.Offset, Is.EqualTo((ulong)info.VertexOffset * 32));
            Assert.That(bindings.Indices.Offset, Is.EqualTo((ulong)info.IndexOffset * 4));
            Assert.That(bindings.Indices.Length, Is.EqualTo((ulong)info.EffectiveGpuIndexCount * 4));
        });
        foreach (VulkanBufferSlice slice in new[]
                     { bindings.Positions, bindings.NormalsAndTangents, bindings.UvsAndColors, bindings.Indices })
        {
            MemoryRequirements requirements;
            _context.Api.GetBufferMemoryRequirements(_context.Device, slice.Buffer, &requirements);
            Assert.That(requirements.Size, Is.GreaterThanOrEqualTo(slice.Offset + slice.Length));
        }
    }

    [Test]
    public void FailedObjectCreationRollsBackItsMeshReference()
    {
        using var mesh = CreateMesh(_graphics);
        using var material = CreateMaterial(_graphics);
        // Exercise the real manager's stale-generation failure after the mesh retain.
        material.Dispose();
        var stale = new VulkanMaterial(_context, material.Handle, "Stale material",
            _materials.RetainMaterial, handle => _materials.ReleaseMaterial(handle), static () => { });
        Assert.That(() => _graphics.CreateRenderObject(mesh, stale), Throws.InvalidOperationException);
        MeshHandle handle = mesh.Handle;
        mesh.Dispose();
        Assert.That(() => _meshes.GetMeshInfo(handle), Throws.InvalidOperationException);
    }

    private static VulkanMesh CreateMesh(VulkanGraphicsDevice graphics) => graphics.CreateMesh(
        [new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0)], [0u, 1u, 2u]);

    [Test]
    public void TypedTextureBindingsKeepEverySharedReferenceAlive()
    {
        var textures = _services.GetRequiredService<TextureManager>();
        using var texture = _graphics.CreateTexture2D(1, 1, [255, 128, 64, 255], TextureColorSpace.Srgb);
        TextureHandle handle = texture.Handle;
        using var first = _graphics.CreateMaterial(MaterialDefinition.Default,
            [new(MaterialTextureSlot.BaseColor, texture), new(MaterialTextureSlot.Emissive, texture)]);
        using var second = _graphics.CreateMaterial(MaterialDefinition.Default,
            [new(MaterialTextureSlot.BaseColor, texture), new(MaterialTextureSlot.Emissive, texture)]);
        Assert.That(first.Handle, Is.EqualTo(second.Handle),
            "Identical typed bindings still use material deduplication.");
        texture.Dispose();
        first.Dispose();
        Assert.DoesNotThrow(() => textures.GetTextureInfo(handle));
        second.Dispose();
        Assert.Throws<InvalidOperationException>(() => textures.GetTextureInfo(handle));
    }

    [Test]
    public void InvalidTextureCreationAndAssignmentsDoNotConsumeCallerOwnership()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _graphics.CreateTexture2D(0, 1, [], TextureColorSpace.Linear));
        Assert.Throws<ArgumentException>(() => _graphics.CreateTexture2D(1, 1, [1], TextureColorSpace.Linear));
        using var texture = _graphics.CreateTexture2D(1, 1, [255, 255, 255, 255], TextureColorSpace.Linear);
        Assert.Throws<ArgumentException>(() => _graphics.CreateMaterial(MaterialDefinition.Default,
            [new(MaterialTextureSlot.BaseColor, texture), new(MaterialTextureSlot.BaseColor, texture)]));
        Assert.That(texture.IsDisposed, Is.False);
        using var valid =
            _graphics.CreateMaterial(MaterialDefinition.Default, [new(MaterialTextureSlot.BaseColor, texture)]);
    }

    private static VulkanMaterial CreateMaterial(VulkanGraphicsDevice graphics) => graphics.CreateMaterial(
        MaterialDefinition.Default with
        {
            Name = "Graphics API test", BaseColorFactor = new Vector4(0.3f, 0.6f, 0.9f, 1)
        });
}
