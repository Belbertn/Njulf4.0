using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialGiDiagnosticRevisionTests
{
    [Test]
    public void AuthoredAndTexturePublishes_ExposeThreeIndependentRevisionChannels()
    {
        var texture = new TextureHandle(91, 1);
        using var manager = new MaterialManager(
            new NoOpTextureReferences(),
            (binding, _, _) => MaterialTextureTransportInput.Constant(
                BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                Vector4.One));
        var definition = new MaterialDefinition
        {
            Name = "Revision diagnostics",
            BaseColor = new MaterialTextureBinding { Texture = texture }
        };
        MaterialHandle handle = manager.RegisterMaterialDefinition(definition);
        GPUMaterialData registered = manager.GetMaterialData(handle);

        manager.UpdateMaterialDefinition(
            handle,
            manager.GetMaterialDefinition(handle) with
            {
                EmissiveFactor = new Vector3(0.25f, 0.5f, 1.0f)
            });
        GPUMaterialData afterAuthoredEdit = manager.GetMaterialData(handle);

        IReadOnlyList<MaterialChangedEvent> textureChanges =
            manager.NotifyTextureContentChanged(texture);
        GPUMaterialData afterTexturePublish = manager.GetMaterialData(handle);
        MaterialManagerDiagnostics diagnostics = manager.Diagnostics;

        Assert.Multiple(() =>
        {
            Assert.That(registered.TextureContentRevision, Is.Zero);
            Assert.That(
                afterAuthoredEdit.MaterialRevision,
                Is.GreaterThan(registered.MaterialRevision));
            Assert.That(
                afterAuthoredEdit.TransportProfileRevision,
                Is.GreaterThan(registered.TransportProfileRevision));
            Assert.That(
                afterAuthoredEdit.TextureContentRevision,
                Is.EqualTo(registered.TextureContentRevision));

            Assert.That(textureChanges, Has.Count.EqualTo(1));
            Assert.That(
                afterTexturePublish.MaterialRevision,
                Is.GreaterThan(afterAuthoredEdit.MaterialRevision));
            Assert.That(
                afterTexturePublish.TextureContentRevision,
                Is.GreaterThan(afterAuthoredEdit.TextureContentRevision));
            Assert.That(
                afterTexturePublish.TransportProfileRevision,
                Is.GreaterThan(afterAuthoredEdit.TransportProfileRevision));
            Assert.That(
                manager.GetMaterialTextureContentRevision(handle.Index),
                Is.EqualTo(afterTexturePublish.TextureContentRevision));

            Assert.That(
                diagnostics.MaterialRevision,
                Is.EqualTo(afterTexturePublish.MaterialRevision));
            Assert.That(
                diagnostics.TextureContentRevision,
                Is.EqualTo(afterTexturePublish.TextureContentRevision));
            Assert.That(
                diagnostics.MaximumTransportProfileRevision,
                Is.EqualTo(afterTexturePublish.TransportProfileRevision));
        });

        manager.ReleaseMaterial(handle);
    }

    private sealed class NoOpTextureReferences : ITextureReferenceManager
    {
        public void RetainTexture(TextureHandle handle)
        {
        }

        public void ReleaseTexture(TextureHandle handle, Fence retireFence = default)
        {
        }
    }
}
