using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialManagerDisposedStateTests
{
    [Test]
    public void OperationalEntryPoints_RejectUseAfterDisposal()
    {
        var manager = new MaterialManager();
        MaterialDefinition definition =
            MaterialDefinition.Default with
            {
                Name = "disposed-state",
                RoughnessFactor = 0.37f
            };
        MaterialHandle handle =
            manager.RegisterMaterialDefinition(definition);

        manager.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => manager.RegisterMaterialDefinition(
                    definition),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.RegisterMaterial(
                    MaterialManager.CreateDefaultMaterial()),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.GetMaterialDefinition(handle),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.GetMaterialDataSnapshot(),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.RetainMaterial(handle),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.ReleaseMaterial(handle),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.UpdateMaterialDefinition(
                    handle,
                    definition with
                    {
                        RoughnessFactor = 0.5f
                    }),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.NotifyTextureContentChanged(
                    new TextureHandle(42, 1)),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => manager.SetTransportV2Enabled(
                    manager.TransportV2Enabled),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                manager.FlushTextureReleases,
                Throws.Nothing);
            Assert.That(manager.Dispose, Throws.Nothing);
        });
    }

    [Test]
    public void CheckedReferenceIncrement_FailsBeforeIntegerWrap()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                MaterialManager.CheckedIncrementReferenceCount(1),
                Is.EqualTo(2));
            Assert.That(
                () => MaterialManager.CheckedIncrementReferenceCount(
                    int.MaxValue),
                Throws.TypeOf<OverflowException>());
            Assert.That(
                () => MaterialManager.CheckedIncrementReferenceCount(
                    0),
                Throws.InvalidOperationException);
        });
    }
}
