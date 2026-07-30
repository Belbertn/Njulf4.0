using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialRenderObjectEditTransactionTests
{
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterPreflight)]
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterFreeSlotReservation)]
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterExtensionPublication)]
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterSlotPublication)]
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterDeduplicationPublication)]
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterDependencyPublication)]
    [TestCase(
        (int)MaterialRegistrationPublicationStage.AfterClassificationPublication)]
    public void CopyOnWrite_PublicationFailureRestoresBindingOwnershipAndManagerState(
        int failedStageValue)
    {
        var failedStage =
            (MaterialRegistrationPublicationStage)
            failedStageValue;
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var sourceTexture = new TextureHandle(101, 1);
        var replacementTexture = new TextureHandle(102, 1);
        references.AcquireFromCaller(sourceTexture, 2);
        references.AcquireFromCaller(replacementTexture);
        MaterialDefinition source =
            CreateDefinition(sourceTexture);
        MaterialHandle shared =
            manager.RegisterMaterialDefinition(
                source,
                CreateCompilationContext());
        MaterialHandle alias =
            manager.RegisterMaterialDefinition(
                source,
                CreateCompilationContext());
        var renderObject = new RenderObject
        {
            Material = alias
        };
        MaterialDefinition replacement =
            CreateDefinition(replacementTexture) with
            {
                Name = $"Replacement {failedStage}"
            };
        int registeredBefore =
            manager.RegisteredMaterialCount;
        uint revisionBefore =
            manager.MaterialDataRevision;
        manager.RegistrationPublicationFaultInjector =
            stage =>
            {
                if (stage == failedStage)
                {
                    throw new InvalidOperationException(
                        $"Injected {stage}.");
                }
            };

        Assert.That(
            () =>
                manager.UpdateRenderObjectMaterialDefinition(
                    renderObject,
                    replacement,
                    CreateCompilationContext()),
            Throws.InvalidOperationException.With.Message.Contains(
                failedStage.ToString()));

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.Material, Is.EqualTo(shared));
            Assert.That(
                manager.RegisteredMaterialCount,
                Is.EqualTo(registeredBefore));
            Assert.That(
                manager.MaterialDataRevision,
                Is.EqualTo(revisionBefore));
            Assert.That(
                manager.GetMaterialDefinition(shared),
                Is.EqualTo(source));
            Assert.That(
                manager.GetReferencedTextureHandles(),
                Does.Not.Contain(replacementTexture));
            Assert.That(
                references.RetainsFor(replacementTexture),
                Is.EqualTo(1));
            Assert.That(
                references.ReleasesFor(replacementTexture),
                Is.EqualTo(1));
            Assert.That(
                references.BalanceFor(sourceTexture),
                Is.EqualTo(2));
            Assert.That(
                references.BalanceFor(replacementTexture),
                Is.EqualTo(1));
        });

        manager.RegistrationPublicationFaultInjector = null;
        manager.ReleaseMaterial(shared);
        Assert.That(
            manager.GetMaterialDefinition(shared),
            Is.EqualTo(source));
        manager.ReleaseMaterial(shared);
        Assert.That(
            () => manager.GetMaterialDefinition(shared),
            Throws.InvalidOperationException);
        references.ReleaseTexture(replacementTexture);
    }

    [Test]
    public void SceneOverride_OwnedObjectPublicationFailureKeepsOriginalReference()
    {
        using var manager = new MaterialManager();
        var source = new MaterialDefinition
        {
            Name = "Owned source",
            RoughnessFactor = 0.25f
        };
        MaterialHandle shared =
            manager.RegisterMaterialDefinition(source);
        MaterialHandle alias =
            manager.RegisterMaterialDefinition(source);
        using var renderObject = new RenderObject
        {
            Material = alias
        };
        int retainCalls = 0;
        int releaseCalls = 0;
        renderObject.AttachResourceLifetime(
            static _ => { },
            static _ => { },
            material =>
            {
                retainCalls++;
                manager.RetainMaterial(
                    (MaterialHandle)material);
            },
            material =>
            {
                releaseCalls++;
                manager.ReleaseMaterial(
                    (MaterialHandle)material);
            },
            retainCurrentResources: false);
        int registeredBefore =
            manager.RegisteredMaterialCount;
        manager.RegistrationPublicationFaultInjector =
            stage =>
            {
                if (stage ==
                    MaterialRegistrationPublicationStage
                        .AfterSlotPublication)
                {
                    throw new InvalidOperationException(
                        "Injected owned publication failure.");
                }
            };
        var store =
            new MaterialManagerSceneMaterialOverrideStore(
                manager);

        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument
                {
                    Roughness = 0.75f
                }),
            Throws.InvalidOperationException.With.Message.Contains(
                "owned publication failure"));

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.Material, Is.EqualTo(shared));
            Assert.That(retainCalls, Is.Zero);
            Assert.That(releaseCalls, Is.Zero);
            Assert.That(
                manager.RegisteredMaterialCount,
                Is.EqualTo(registeredBefore));
            Assert.That(
                manager.GetMaterialDefinition(shared),
                Is.EqualTo(source));
        });

        manager.RegistrationPublicationFaultInjector = null;
        renderObject.Dispose();
        Assert.That(releaseCalls, Is.EqualTo(1));
        Assert.That(
            manager.GetMaterialDefinition(shared),
            Is.EqualTo(source));
        manager.ReleaseMaterial(shared);
    }

    [Test]
    public void SceneOverride_CompilationFailureDoesNotCreatePrivateMaterial()
    {
        var references = new RecordingTextureReferences();
        bool failCompilation = false;
        using var manager = new MaterialManager(
            references,
            (binding, _, _) =>
            {
                if (failCompilation)
                {
                    throw new InvalidOperationException(
                        "Injected material compilation failure.");
                }
                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One);
            });
        var texture = new TextureHandle(111, 1);
        references.AcquireFromCaller(texture, 2);
        MaterialDefinition source =
            CreateDefinition(texture);
        MaterialHandle shared =
            manager.RegisterMaterialDefinition(source);
        MaterialHandle alias =
            manager.RegisterMaterialDefinition(source);
        var renderObject = new RenderObject
        {
            Material = alias
        };
        var store =
            new MaterialManagerSceneMaterialOverrideStore(
                manager);
        int registeredBefore =
            manager.RegisteredMaterialCount;
        failCompilation = true;

        Assert.That(
            () => store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument
                {
                    Roughness = 0.9f
                }),
            Throws.InvalidOperationException.With.Message.Contains(
                "compilation failure"));

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.Material, Is.EqualTo(shared));
            Assert.That(
                manager.RegisteredMaterialCount,
                Is.EqualTo(registeredBefore));
            Assert.That(
                manager.GetMaterialDefinition(shared),
                Is.EqualTo(source));
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(references.ReleaseCalls, Is.Zero);
            Assert.That(
                references.BalanceFor(texture),
                Is.EqualTo(2));
        });

        failCompilation = false;
        manager.ReleaseMaterial(shared);
        manager.ReleaseMaterial(shared);
    }

    [Test]
    public void CopyOnWrite_TextureRetainFailureLeavesSharedOwnershipUntouched()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var sourceTexture = new TextureHandle(121, 1);
        var replacementTexture = new TextureHandle(122, 1);
        references.AcquireFromCaller(sourceTexture, 2);
        references.AcquireFromCaller(replacementTexture);
        MaterialDefinition source =
            CreateDefinition(sourceTexture);
        MaterialHandle shared =
            manager.RegisterMaterialDefinition(
                source,
                CreateCompilationContext());
        MaterialHandle alias =
            manager.RegisterMaterialDefinition(
                source,
                CreateCompilationContext());
        var renderObject = new RenderObject
        {
            Material = alias
        };
        references.FailRetainCall = 1;
        int registeredBefore =
            manager.RegisteredMaterialCount;

        Assert.That(
            () =>
                manager.UpdateRenderObjectMaterialDefinition(
                    renderObject,
                    CreateDefinition(replacementTexture),
                    CreateCompilationContext()),
            Throws.InvalidOperationException.With.Message.Contains(
                "retain failure"));

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.Material, Is.EqualTo(shared));
            Assert.That(
                manager.RegisteredMaterialCount,
                Is.EqualTo(registeredBefore));
            Assert.That(
                manager.GetMaterialDefinition(shared),
                Is.EqualTo(source));
            Assert.That(
                references.BalanceFor(sourceTexture),
                Is.EqualTo(2));
            Assert.That(
                references.BalanceFor(replacementTexture),
                Is.EqualTo(1));
        });

        manager.ReleaseMaterial(shared);
        manager.ReleaseMaterial(shared);
        references.ReleaseTexture(replacementTexture);
    }

    [Test]
    public void CopyOnWrite_RenderObjectChangedDuringCompilationFailsClosed()
    {
        var references = new RecordingTextureReferences();
        RenderObject? renderObject = null;
        MaterialHandle concurrentHandle =
            MaterialHandle.Invalid;
        bool replaceDuringCompilation = false;
        using var manager = new MaterialManager(
            references,
            (binding, _, _) =>
            {
                if (replaceDuringCompilation)
                {
                    replaceDuringCompilation = false;
                    renderObject!.Material =
                        concurrentHandle;
                }
                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One);
            });
        var texture = new TextureHandle(131, 1);
        references.AcquireFromCaller(texture, 2);
        MaterialDefinition source =
            CreateDefinition(texture);
        MaterialHandle shared =
            manager.RegisterMaterialDefinition(source);
        _ = manager.RegisterMaterialDefinition(source);
        concurrentHandle =
            manager.RegisterMaterialDefinition(
                new MaterialDefinition
                {
                    Name = "Concurrent replacement"
                });
        renderObject = new RenderObject
        {
            Material = shared
        };
        int registeredBefore =
            manager.RegisteredMaterialCount;
        replaceDuringCompilation = true;

        Assert.That(
            () =>
                manager.UpdateRenderObjectMaterialDefinition(
                    renderObject,
                    source with
                    {
                        RoughnessFactor = 0.8f
                    }),
            Throws.InvalidOperationException.With.Message.Contains(
                "material changed"));

        Assert.Multiple(() =>
        {
            Assert.That(
                renderObject.Material,
                Is.EqualTo(concurrentHandle));
            Assert.That(
                manager.RegisteredMaterialCount,
                Is.EqualTo(registeredBefore));
            Assert.That(
                manager.GetMaterialDefinition(shared),
                Is.EqualTo(source));
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(references.ReleaseCalls, Is.Zero);
        });

        manager.ReleaseMaterial(shared);
        manager.ReleaseMaterial(shared);
        manager.ReleaseMaterial(concurrentHandle);
    }

    [Test]
    public void CopyOnWrite_SourceHandleRetiredDuringCompilationFailsClosed()
    {
        var references = new RecordingTextureReferences();
        MaterialManager? owner = null;
        MaterialHandle shared = MaterialHandle.Invalid;
        bool retireDuringCompilation = false;
        using var manager = owner = new MaterialManager(
            references,
            (binding, _, _) =>
            {
                if (retireDuringCompilation)
                {
                    retireDuringCompilation = false;
                    owner!.ReleaseMaterial(shared);
                    owner.ReleaseMaterial(shared);
                }
                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One);
            });
        var texture = new TextureHandle(141, 1);
        references.AcquireFromCaller(texture, 2);
        MaterialDefinition source =
            CreateDefinition(texture);
        shared =
            manager.RegisterMaterialDefinition(source);
        _ = manager.RegisterMaterialDefinition(source);
        var renderObject = new RenderObject
        {
            Material = shared
        };
        int registeredBefore =
            manager.RegisteredMaterialCount;
        retireDuringCompilation = true;

        Assert.That(
            () =>
                manager.UpdateRenderObjectMaterialDefinition(
                    renderObject,
                    source with
                    {
                        RoughnessFactor = 0.85f
                    }),
            Throws.InvalidOperationException.With.Message.Contains(
                "destroyed material"));

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.Material, Is.EqualTo(shared));
            Assert.That(
                manager.RegisteredMaterialCount,
                Is.EqualTo(registeredBefore - 1));
            Assert.That(
                () => manager.GetMaterialDefinition(shared),
                Throws.InvalidOperationException);
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(
                references.BalanceFor(texture),
                Is.Zero);
        });
    }

    private static MaterialDefinition CreateDefinition(
        TextureHandle texture) =>
        new()
        {
            Name = $"Material {texture.Index}",
            BaseColor = new MaterialTextureBinding
            {
                Texture = texture
            },
            RoughnessFactor = 0.4f
        };

    private static MaterialCompilationContext
        CreateCompilationContext() =>
        new()
        {
            ResolveTexture = (binding, _) =>
                MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One)
        };

    private sealed class RecordingTextureReferences :
        ITextureReferenceManager
    {
        private readonly Dictionary<TextureHandle, int>
            _balances = new();
        private readonly Dictionary<TextureHandle, int>
            _retains = new();
        private readonly Dictionary<TextureHandle, int>
            _releases = new();

        public int RetainCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public int? FailRetainCall { get; set; }

        public void AcquireFromCaller(
            TextureHandle handle,
            int count = 1)
        {
            _balances.TryGetValue(
                handle,
                out int current);
            _balances[handle] =
                checked(current + count);
        }

        public void RetainTexture(TextureHandle handle)
        {
            RetainCalls++;
            if (FailRetainCall == RetainCalls)
            {
                throw new InvalidOperationException(
                    "Injected retain failure.");
            }

            _retains.TryGetValue(
                handle,
                out int retains);
            _retains[handle] =
                retains + 1;
            AcquireFromCaller(handle);
        }

        public void ReleaseTexture(
            TextureHandle handle,
            Fence retireFence = default)
        {
            ReleaseCalls++;
            _balances.TryGetValue(
                handle,
                out int balance);
            if (balance <= 0)
            {
                throw new InvalidOperationException(
                    $"Texture {handle} was over-released.");
            }
            _balances[handle] =
                balance - 1;
            _releases.TryGetValue(
                handle,
                out int releases);
            _releases[handle] =
                releases + 1;
        }

        public int BalanceFor(TextureHandle handle) =>
            _balances.TryGetValue(
                handle,
                out int value)
                ? value
                : 0;

        public int RetainsFor(TextureHandle handle) =>
            _retains.TryGetValue(
                handle,
                out int value)
                ? value
                : 0;

        public int ReleasesFor(TextureHandle handle) =>
            _releases.TryGetValue(
                handle,
                out int value)
                ? value
                : 0;
    }
}
