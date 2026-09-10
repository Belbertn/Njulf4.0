using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Editor;
using Njulf.Graphics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed partial class GraphicsApiIntegrationTests
{
    [Test]
    public void PublicMaterialTextureEditsPreserveSiblingsAndRetainedSnapshots()
    {
        var textures = _services.GetRequiredService<TextureManager>();
        using var red = _graphics.CreateTexture2D(1, 1, [255, 0, 0, 255], TextureColorSpace.Srgb);
        using var blue = _graphics.CreateTexture2D(1, 1, [0, 0, 255, 255], TextureColorSpace.Srgb);
        using var material = _graphics.CreateMaterial(new() { Name = "Editable textures" },
            [new(MaterialTextureSlot.BaseColor, red), new(MaterialTextureSlot.Emissive, red)]);
        using var first = new RenderObject(null, material);
        using var second = new RenderObject(null, material);
        using Texture snapshot = first.Material!.RetainTexture(MaterialTextureSlot.BaseColor)!;
        TextureHandle oldHandle = ((VulkanTexture)snapshot).Handle;
        red.Dispose();

        first.UpdateMaterial(first.Material.Definition with { RoughnessFactor = 0.25f },
            [new(MaterialTextureSlot.BaseColor, blue), new(MaterialTextureSlot.Emissive, null)]);
        blue.Dispose();
        Assert.That(first.Material!.Definition.Emissive.IsBound, Is.False);
        Assert.That(second.Material!.Definition.BaseColor.Texture, Is.EqualTo(oldHandle));
        using Texture selected = first.Material.RetainTexture(MaterialTextureSlot.BaseColor)!;
        Assert.That(selected.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb));
        Assert.That(selected.Width, Is.EqualTo(1));
        TextureHandle newHandle = ((VulkanTexture)selected).Handle;
        Assert.That(_materials.GetMaterialData(first.Material.GetMaterialHandle()).AlbedoTextureIndex,
            Is.EqualTo(textures.GetBindlessTextureIndex(newHandle)));

        MaterialDefinition beforeSampler = first.Material.Definition;
        first.UpdateMaterial(beforeSampler with
        {
            BaseColor = beforeSampler.BaseColor with
            {
                Sampler = beforeSampler.BaseColor.Sampler with { WrapU = TextureWrapMode.ClampToEdge },
                Offset = new Vector2(0.2f, 0.3f)
            }
        });
        Assert.That(first.Material.Definition.BaseColor.Sampler.WrapU, Is.EqualTo(TextureWrapMode.ClampToEdge));
        Assert.That(first.Material.Definition.BaseColor.Texture, Is.Not.EqualTo(newHandle));
        Assert.That(first.Material.Definition.BaseColor.Offset, Is.EqualTo(new Vector2(0.2f, 0.3f)));
        first.UpdateMaterial(first.Material.Definition, [new(MaterialTextureSlot.BaseColor, null)]);
        Assert.That(first.Material.RetainTexture(MaterialTextureSlot.BaseColor), Is.Null);
        var staging = _services.GetRequiredService<Njulf.Rendering.Memory.StagingRing>();
        staging.BeginFrame(0);
        var upload = _context.BeginSingleTimeCommands();
        try
        {
            _materials.UploadMaterials(upload.CommandBuffer);
            staging.FlushCurrentFrame();
        }
        catch { _context.AbortSingleTimeCommands(upload); throw; }
        _context.EndSingleTimeCommands(upload);
        Assert.DoesNotThrow(() => textures.GetTextureInfo(newHandle)); // retained snapshot survives clearing
        selected.Dispose();
        Assert.Throws<InvalidOperationException>(() => textures.GetTextureInfo(newHandle));
        second.Dispose();
        material.Dispose();
        Assert.DoesNotThrow(() => textures.GetTextureInfo(oldHandle));
        snapshot.Dispose();
        Assert.Throws<InvalidOperationException>(() => textures.GetTextureInfo(oldHandle));
    }

    [Test]
    public void PublicMaterialEditsRejectDisposedForeignAndWrongThreadInputsBeforePublication()
    {
        using var material = CreateMaterial(_graphics);
        using var target = new RenderObject(null, material);
        using var disposed = _graphics.CreateTexture2D(1, 1, [255, 255, 255, 255], TextureColorSpace.Linear);
        disposed.Dispose();
        IMaterial original = target.Material!;
        MaterialDefinition definition = original.Definition;
        Assert.Throws<ObjectDisposedException>(() => target.UpdateMaterial(definition,
            [new(MaterialTextureSlot.Normal, disposed)]));
        Assert.Throws<ArgumentException>(() => target.UpdateMaterial(definition,
            [new(MaterialTextureSlot.Normal, new ForeignTexture())]));
        Assert.Throws<ArgumentOutOfRangeException>(() => target.UpdateMaterial(definition with { RoughnessFactor = float.NaN }));
        Assert.Throws<InvalidOperationException>(() => Task.Run(() => material.UpdateShared(definition)).GetAwaiter().GetResult());
        Assert.That(target.Material, Is.SameAs(original));
        Assert.That(original.Definition, Is.EqualTo(definition));
    }

    private sealed class ForeignTexture : ITexture
    {
        public int Width => 1;
        public int Height => 1;
        public TextureColorSpace ColorSpace => TextureColorSpace.Linear;
        public bool IsDisposed => false;
    }

    [Test]
    public void EditorMaterialScopeAndTextureOverridesRoundTripThroughSceneLoading()
    {
        string root = Path.Combine(TestContext.CurrentContext.TestDirectory, "material-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "pixel.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        try
        {
            using var mesh = CreateMesh(_graphics);
            using var material = CreateMaterial(_graphics);
            using var embedded = _graphics.CreateTexture2D(1, 1, [128, 128, 255, 255], TextureColorSpace.Linear);
            material.UpdateShared(material.Definition, [new(MaterialTextureSlot.Normal, embedded)]);
            using var template = new Model();
            var part = _graphics.CreateRenderObject(mesh, material);
            part.Name = "part";
            template.Add(part);
            using var scene = new Scene();
            var first = _graphics.CreateRenderObject(mesh, material);
            var second = _graphics.CreateRenderObject(mesh, material);
            first.AssetReference = new() { Path = "test.glb", SubObject = "0" };
            second.AssetReference = first.AssetReference;
            scene.Add(first);
            scene.Add(second);
            var content = new EditorModelContent(template);
            var editor = new EditorController(scene, content, _services.GetRequiredService<LightManager>(), _materials);
            editor.SelectEntity(EditorSelectionKind.Object, first.Id);
            Assert.That(editor.MaterialScope, Is.EqualTo(MaterialEditScope.ThisObject));
            editor.MaterialScope = MaterialEditScope.SharedMaterial;
            editor.UpdateSelectedMaterialDefinition(first.Material!.Definition with { RoughnessFactor = 0.3f });
            Assert.That(second.Material!.Definition.RoughnessFactor, Is.EqualTo(0.3f));
            editor.SelectEntity(EditorSelectionKind.Object, second.Id);
            Assert.That(editor.MaterialScope, Is.EqualTo(MaterialEditScope.ThisObject));
            editor.UpdateSelectedMaterialDefinition(second.Material.Definition with { RoughnessFactor = 0.7f });
            Assert.That(first.Material.Definition.RoughnessFactor, Is.EqualTo(0.3f));
            editor.SelectEntity(EditorSelectionKind.Object, first.Id);
            editor.SetSelectedMaterialTexture(MaterialTextureSlot.BaseColor, path);
            editor.SetSelectedMaterialTexture(MaterialTextureSlot.Normal, string.Empty);
            Assert.That(editor.IsDirty, Is.True);
            Assert.That(second.Material.Definition.BaseColor.IsBound, Is.False);

            var store = new MaterialManagerSceneMaterialOverrideStore(_materials);
            SceneDocument saved = new SceneDocumentWriter().CreateDocument(scene, materials: store);
            SceneMaterialOverrideDocument authored = saved.Objects.Single(item => item.Id == first.Id).MaterialOverride!;
            Assert.That(authored.BaseColorTexturePath, Is.EqualTo(path));
            Assert.That(authored.NormalTexturePath, Is.Empty);
            Assert.That(saved.Objects.Single(item => item.Id == second.Id).MaterialOverride!.NormalTexturePath, Is.Null);
            Assert.That(saved.Dependencies.Any(item => item.Path == path), Is.True);
            SceneDocument restored = JsonSerializer.Deserialize<SceneDocument>(SceneDocumentJson.Serialize(saved), SceneDocumentJson.Options)!;
            using Scene loaded = new SceneDocumentLoader(content).Load(restored, materials: store);
            var loadedFirst = (RenderObject)loaded.FindById(first.Id)!;
            var loadedSecond = (RenderObject)loaded.FindById(second.Id)!;
            Assert.That(_materials.GetMaterialTexturePath(loadedFirst.Material!, MaterialTextureSlot.BaseColor), Is.EqualTo(path));
            Assert.That(loadedFirst.Material!.Definition.Normal.IsBound, Is.False);
            Assert.That(loadedSecond.Material!.Definition.Normal.IsBound, Is.True);
            Assert.That(loadedSecond.Material.Definition.RoughnessFactor, Is.EqualTo(0.7f));

            IMaterial unchanged = first.Material;
            MaterialDefinition before = unchanged.Definition;
            Assert.Throws<FileNotFoundException>(() => store.Apply(first, new()
            {
                Roughness = 0.1f, BaseColorTexturePath = path, NormalTexturePath = Path.Combine(root, "missing.png")
            }));
            Assert.That(first.Material, Is.SameAs(unchanged));
            Assert.That(first.Material.Definition, Is.EqualTo(before));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestCase(MaterialTextureSlot.BaseColor, TextureColorSpace.Srgb)]
    [TestCase(MaterialTextureSlot.Emissive, TextureColorSpace.Srgb)]
    [TestCase(MaterialTextureSlot.Normal, TextureColorSpace.Linear)]
    [TestCase(MaterialTextureSlot.MetallicRoughness, TextureColorSpace.Linear)]
    [TestCase(MaterialTextureSlot.Occlusion, TextureColorSpace.Linear)]
    public void MaterialFileTextureLoaderUsesSlotColorSpace(MaterialTextureSlot slot, TextureColorSpace colorSpace)
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, $"material-slot-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        try
        {
            using Texture texture = _materials.LoadMaterialTexture(path, slot);
            Assert.That(texture.ColorSpace, Is.EqualTo(colorSpace));
            Assert.That(texture.Width, Is.EqualTo(1));
        }
        finally { File.Delete(path); }
    }
}
