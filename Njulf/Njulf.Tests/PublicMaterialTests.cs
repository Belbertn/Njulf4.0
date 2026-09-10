using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PublicMaterialTests
{
    [Test]
    public void SharedEditsReachAliasesAndObjectEditsPreserveSiblings()
    {
        using var manager = new MaterialManager();
        using Material material = manager.AdoptResource(manager.RegisterMaterialDefinition(new() { Name = "Shared" }));
        using var first = new RenderObject(null, material);
        using var second = new RenderObject(null, material);
        MaterialDefinition snapshot = material.Definition;
        var edited = snapshot with { BaseColorFactor = new Vector4(0.2f, 0.4f, 0.6f, 1), RoughnessFactor = 0.3f,
            EmissiveFactor = new Vector3(1, 0.5f, 0), EmissiveStrength = 2 };
        second.Material!.UpdateShared(edited);
        Assert.That(material.Definition.BaseColorFactor, Is.EqualTo(new Vector4(0.2f, 0.4f, 0.6f, 1)));
        Assert.That(material.Definition.RoughnessFactor, Is.EqualTo(0.3f));
        Assert.That(material.Definition.EmissiveFactor, Is.EqualTo(new Vector3(1, 0.5f, 0)));
        Assert.That(material.Definition.EmissiveStrength, Is.EqualTo(2));
        edited = material.Definition; // The compiler also derives authored feature flags for emission.
        Assert.That(first.Material!.Definition, Is.EqualTo(edited));
        Assert.That(material.Definition, Is.EqualTo(edited));
        Assert.That(snapshot.RoughnessFactor, Is.EqualTo(1));

        IMaterial borrowed = first.Material;
        first.UpdateMaterial(edited with { Name = "Private", RoughnessFactor = 0.8f });
        Assert.That(first.Material!.Definition.RoughnessFactor, Is.EqualTo(0.8f));
        Assert.That(second.Material.Definition, Is.EqualTo(edited));
        Assert.That(borrowed.IsDisposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => _ = borrowed.Definition);
        // The replacement wrapper must support subsequent inspection and shared editing too.
        first.Material.UpdateShared(first.Material.Definition with { EmissiveStrength = 4 });
        Assert.That(first.Material.Definition.EmissiveStrength, Is.EqualTo(4));
        Assert.That(second.Material.Definition.EmissiveStrength, Is.EqualTo(2));
        material.Dispose();
        Assert.That(second.Material.Definition, Is.EqualTo(edited));
    }

    [Test]
    public void NoOpAndInvalidEditsDoNotSplitOrPublish()
    {
        using var manager = new MaterialManager();
        using Material material = manager.AdoptResource(manager.RegisterMaterialDefinition(new() { Name = "No-op" }));
        using var target = new RenderObject(null, material);
        IMaterial original = target.Material!;
        uint revision = manager.MaterialDataRevision;
        target.UpdateMaterial(original.Definition);
        target.UpdateMaterial(original.Definition, [new(MaterialTextureSlot.BaseColor, null)]);
        Assert.Throws<ArgumentException>(() => target.UpdateMaterial(original.Definition,
            [new(MaterialTextureSlot.Normal, null), new(MaterialTextureSlot.Normal, null)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => target.UpdateMaterial(original.Definition,
            [new((MaterialTextureSlot)99, null)]));
        Assert.Throws<ArgumentException>(() => target.UpdateMaterial(original.Definition with
            { BaseColor = new() { Texture = new TextureHandle(100, 1) } }));
        Assert.That(target.Material, Is.SameAs(original));
        Assert.That(manager.MaterialDataRevision, Is.EqualTo(revision));
        Assert.That(material.RetainTexture(MaterialTextureSlot.Normal), Is.Null);
        target.Dispose();
        Assert.Throws<ObjectDisposedException>(() => target.UpdateMaterial(material.Definition));
        material.Dispose();
        Assert.Throws<ObjectDisposedException>(() => material.UpdateShared(MaterialDefinition.Default));
    }

    [Test]
    public void PermanentDefaultCanBeEditedOnlyThroughAnObjectCopy()
    {
        using var manager = new MaterialManager();
        IMaterial shared = manager.GetResourceView(manager.DefaultMaterialHandle);
        using var target = new RenderObject(null, shared);
        using var sibling = new RenderObject(null, shared);
        MaterialDefinition edited = shared.Definition with { RoughnessFactor = 0.2f };
        Assert.Throws<InvalidOperationException>(() => shared.UpdateShared(edited));
        target.UpdateMaterial(edited);
        Assert.That(target.Material!.Definition.RoughnessFactor, Is.EqualTo(0.2f));
        Assert.That(sibling.Material!.Definition.RoughnessFactor, Is.EqualTo(1));
        Assert.That(shared.Definition.RoughnessFactor, Is.EqualTo(1));
    }

    [Test]
    public void MissingMaterialCannotBeEdited()
    {
        using var target = new RenderObject();
        Assert.Throws<InvalidOperationException>(() => target.UpdateMaterial(MaterialDefinition.Default));
    }
}
