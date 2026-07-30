using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialTextureFanoutTransactionTests
{
    [Test]
    public void MultiDependentCompileFailure_PublishesNoPartialRevision()
    {
        var texture = new TextureHandle(140, 1);
        int resolverCalls = 0;
        bool injectFailure = false;
        using var manager = new MaterialManager(
            new NoOpTextureReferences(),
            (binding, _, _) =>
            {
                int call = ++resolverCalls;
                if (injectFailure && call == 2)
                {
                    throw new InvalidOperationException(
                        "Injected second-dependent compile failure.");
                }

                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One);
            });
        MaterialHandle first =
            manager.RegisterMaterialDefinition(
                CreateDefinition(
                    "first",
                    texture,
                    1f));
        MaterialHandle second =
            manager.RegisterMaterialDefinition(
                CreateDefinition(
                    "second",
                    texture,
                    0.75f));
        MaterialAspectRevisions firstBefore =
            manager.GetMaterialAspectRevisions(first);
        MaterialAspectRevisions secondBefore =
            manager.GetMaterialAspectRevisions(second);
        uint dataRevisionBefore =
            manager.MaterialDataRevision;
        uint textureRevisionBefore =
            manager.Diagnostics.TextureContentRevision;

        resolverCalls = 0;
        injectFailure = true;
        Assert.That(
            () => manager.NotifyTextureContentChanged(texture),
            Throws.InvalidOperationException.With.Message.Contains(
                "second-dependent"));

        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetMaterialAspectRevisions(first),
                Is.EqualTo(firstBefore));
            Assert.That(
                manager.GetMaterialAspectRevisions(second),
                Is.EqualTo(secondBefore));
            Assert.That(
                manager.MaterialDataRevision,
                Is.EqualTo(dataRevisionBefore));
            Assert.That(
                manager.Diagnostics.TextureContentRevision,
                Is.EqualTo(textureRevisionBefore));
        });

        resolverCalls = 0;
        injectFailure = false;
        IReadOnlyList<MaterialChangedEvent> changes =
            manager.NotifyTextureContentChanged(texture);
        Assert.Multiple(() =>
        {
            Assert.That(changes, Has.Count.EqualTo(2));
            Assert.That(
                manager.GetMaterialTextureContentRevision(
                    first.Index),
                Is.GreaterThan(textureRevisionBefore));
            Assert.That(
                manager.GetMaterialTextureContentRevision(
                    second.Index),
                Is.EqualTo(
                    manager.GetMaterialTextureContentRevision(
                        first.Index)));
            Assert.That(
                manager.MaterialDataRevision,
                Is.GreaterThan(dataRevisionBefore));
        });

        manager.ReleaseMaterial(first);
        manager.ReleaseMaterial(second);
    }

    [Test]
    public void EmptyStaleDependencySet_DoesNotAdvanceGlobalRevisions()
    {
        using var manager = new MaterialManager();
        var texture = new TextureHandle(141, 1);
        uint dataBefore = manager.MaterialDataRevision;
        MaterialManagerDiagnostics diagnosticsBefore =
            manager.Diagnostics;

        IReadOnlyList<MaterialChangedEvent> changes =
            manager.NotifyTextureContentChanged(texture);

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.Empty);
            Assert.That(
                manager.MaterialDataRevision,
                Is.EqualTo(dataBefore));
            Assert.That(
                manager.Diagnostics.TextureContentRevision,
                Is.EqualTo(
                    diagnosticsBefore.TextureContentRevision));
        });
    }

    [Test]
    public void NotificationFailure_BlocksRenderingUntilDurableRetrySucceeds()
    {
        var texture = new TextureHandle(142, 1);
        bool fail = false;
        using var manager = new MaterialManager(
            new NoOpTextureReferences(),
            (binding, _, _) =>
            {
                if (fail)
                {
                    throw new InvalidOperationException(
                        "injected notification failure");
                }
                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One);
            });
        MaterialHandle material =
            manager.RegisterMaterialDefinition(
                CreateDefinition(
                    "retry",
                    texture,
                    1f));
        var changed = new TextureContentChangedEvent(
            texture,
            ContentRevision: 2,
            SourceContentHash: 0x1234);

        fail = true;
        Assert.That(
            () => manager.ProcessTextureContentChanged(changed),
            Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.PendingTextureFanoutCount,
                Is.EqualTo(1));
            Assert.That(
                manager.TextureFanoutFailureCount,
                Is.EqualTo(1));
            Assert.That(
                () => manager.EnsureTextureFanoutReady(),
                Throws.InvalidOperationException);
        });

        fail = false;
        manager.ProcessTextureContentChanged(changed);
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.PendingTextureFanoutCount,
                Is.Zero);
            Assert.That(
                () => manager.EnsureTextureFanoutReady(),
                Throws.Nothing);
        });
        manager.ReleaseMaterial(material);
    }

    private static MaterialDefinition CreateDefinition(
        string name,
        TextureHandle texture,
        float factor) =>
        new()
        {
            Name = name,
            BaseColorFactor =
                new Vector4(factor, factor, factor, 1f),
            BaseColor = new MaterialTextureBinding
            {
                Texture = texture
            }
        };

    private sealed class NoOpTextureReferences :
        ITextureReferenceManager
    {
        public void RetainTexture(TextureHandle handle)
        {
        }

        public void ReleaseTexture(
            TextureHandle handle,
            Silk.NET.Vulkan.Fence retireFence = default)
        {
        }
    }
}
