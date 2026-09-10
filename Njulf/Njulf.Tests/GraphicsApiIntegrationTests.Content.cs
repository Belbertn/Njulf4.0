using Njulf.Assets;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Resources;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed partial class GraphicsApiIntegrationTests
{
    [Test]
    public void ContentScopesPreserveGpuTextureUntilLastMaterialUserReleasesIt()
    {
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "content-gpu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
            File.WriteAllText(Path.Combine(directory, "material.njmaterial.json"), """{"baseColorTexturePath":"pixel.png"}""");
            using var content = new ContentManager(directory) { GraphicsDeviceProvider = () => _graphics };
            using var a = content.CreateScope();
            using var b = content.CreateScope();
            var texture = (VulkanTexture)content.Load<Texture>("pixel.png");
            var textures = _services.GetRequiredService<TextureManager>();
            Material material = a.Load<Material>("material.njmaterial.json");
            Assert.That(b.Load<Material>("material.njmaterial.json"), Is.SameAs(material));
            using var placement = new RenderObject(null, material);
            content.Unload(texture);
            a.Dispose();
            Assert.DoesNotThrow(() => textures.GetTextureInfo(texture.Handle));
            b.Dispose();
            Assert.That(texture.IsDisposed, Is.True);
            Assert.That(material.IsDisposed, Is.True);
            Assert.DoesNotThrow(() => textures.GetTextureInfo(texture.Handle));
            Assert.That(placement.Material!.Definition.BaseColor.Texture.IsValid, Is.True);
            placement.Dispose();
            Assert.Throws<InvalidOperationException>(() => textures.GetTextureInfo(texture.Handle));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
