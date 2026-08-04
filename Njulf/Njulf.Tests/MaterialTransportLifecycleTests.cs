using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialTransportLifecycleTests
{
    [Test]
    public void OwnershipDelta_IsDuplicateAwareAndScalesWithLogicalAliases()
    {
        var a = new TextureHandle(10, 1);
        var b = new TextureHandle(11, 1);
        var c = new TextureHandle(12, 1);

        MaterialManager.TextureOwnershipDelta delta = MaterialManager.ComputeTextureOwnershipDelta(
            [a, a, b, TextureHandle.Invalid],
            [a, c, c, TextureHandle.Invalid],
            logicalReferenceCount: 3);

        Assert.Multiple(() =>
        {
            Assert.That(ToCounts(delta.Retains), Is.EquivalentTo(new Dictionary<TextureHandle, int>
            {
                [c] = 6
            }));
            Assert.That(ToCounts(delta.Releases), Is.EquivalentTo(new Dictionary<TextureHandle, int>
            {
                [a] = 3,
                [b] = 3
            }));
        });
    }

    [Test]
    public void AuthoredUpdate_RetainsNewAndReleasesOldDependenciesForEveryAlias()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var oldTexture = new TextureHandle(20, 1);
        var newTexture = new TextureHandle(21, 1);
        MaterialDefinition before = CreateDefinition(oldTexture);
        MaterialDefinition after = CreateDefinition(newTexture);

        // Registration transfers one reference for each logical registration.
        references.AcquireFromCaller(oldTexture, 2);
        MaterialHandle first = manager.RegisterMaterialDefinition(before, CreateCompilationContext());
        MaterialHandle second = manager.RegisterMaterialDefinition(before, CreateCompilationContext());
        references.AcquireFromCaller(newTexture);

        manager.UpdateMaterialDefinition(first, after, CreateCompilationContext());

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(references.RetainsFor(newTexture), Is.EqualTo(2));
            Assert.That(references.ReleasesFor(oldTexture), Is.EqualTo(2));
            Assert.That(references.BalanceFor(oldTexture), Is.Zero);
            Assert.That(references.BalanceFor(newTexture), Is.EqualTo(3));
            Assert.That(manager.GetMaterialDefinition(second).BaseColor.Texture, Is.EqualTo(newTexture));
        });

        // The caller keeps its borrowed reference; both material aliases own
        // the other two until their logical handles are released.
        references.ReleaseTexture(newTexture);
        manager.ReleaseMaterial(first);
        manager.ReleaseMaterial(second);
        Assert.That(references.BalanceFor(newTexture), Is.Zero);
    }

    [Test]
    public void CopyOnWrite_TransfersExistingOwnershipWithoutRetainOrRelease()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var texture = new TextureHandle(30, 1);
        MaterialDefinition definition = CreateDefinition(texture);
        references.AcquireFromCaller(texture, 2);

        MaterialHandle shared = manager.RegisterMaterialDefinition(definition, CreateCompilationContext());
        MaterialHandle alias = manager.RegisterMaterialDefinition(definition, CreateCompilationContext());
        MaterialHandle editable = manager.CreateEditableMaterialCopy(alias);

        Assert.Multiple(() =>
        {
            Assert.That(editable, Is.Not.EqualTo(shared));
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(references.ReleaseCalls, Is.Zero);
            Assert.That(references.BalanceFor(texture), Is.EqualTo(2));
        });

        manager.ReleaseMaterial(shared);
        manager.ReleaseMaterial(editable);
        Assert.That(references.BalanceFor(texture), Is.Zero);
    }

    [Test]
    public void Dispose_ReleasesEveryOutstandingLogicalMaterialReference()
    {
        var references = new RecordingTextureReferences();
        var manager = new MaterialManager(references);
        var texture = new TextureHandle(35, 1);
        MaterialDefinition definition = CreateDefinition(texture);
        references.AcquireFromCaller(texture, 2);
        manager.RegisterMaterialDefinition(definition, CreateCompilationContext());
        manager.RegisterMaterialDefinition(definition, CreateCompilationContext());

        manager.Dispose();
        manager.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(references.BalanceFor(texture), Is.Zero);
            Assert.That(references.ReleasesFor(texture), Is.EqualTo(2));
        });
    }

    [Test]
    public void ForceDestroy_ReleasesTextureOwnershipForEveryInvalidatedAlias()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var texture = new TextureHandle(36, 1);
        MaterialDefinition definition = CreateDefinition(texture);
        references.AcquireFromCaller(texture, 2);
        MaterialHandle first = manager.RegisterMaterialDefinition(definition, CreateCompilationContext());
        MaterialHandle second = manager.RegisterMaterialDefinition(definition, CreateCompilationContext());

        manager.DestroyMaterial(first);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(references.BalanceFor(texture), Is.Zero);
            Assert.That(references.ReleasesFor(texture), Is.EqualTo(2));
            Assert.That(
                () => manager.GetMaterialDefinition(second),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void GiTransportInputRevision_TracksTransportInputsButNotRasterOrFarFieldOnlyChanges()
    {
        using var manager = new MaterialManager();
        MaterialHandle handle = manager.RegisterMaterialDefinition(
            new MaterialDefinition { Name = "GI revision" });
        uint initial = manager.GiTransportInputRevision;

        manager.UpdateMaterialDefinition(
            handle,
            manager.GetMaterialDefinition(handle) with
            {
                EmissiveFactor = new Vector3(0.5f, 0.25f, 0.125f)
            });
        uint afterEmission = manager.GiTransportInputRevision;

        manager.UpdateMaterialDefinition(
            handle,
            manager.GetMaterialDefinition(handle) with { DecalLayer = 3 });
        uint afterRasterOnly = manager.GiTransportInputRevision;

        manager.UpdateMaterialDefinition(
            handle,
            manager.GetMaterialDefinition(handle) with { OcclusionStrength = 0.25f });
        uint afterOcclusion = manager.GiTransportInputRevision;

        manager.UpdateMaterialDefinition(
            handle,
            manager.GetMaterialDefinition(handle) with { AlphaCutoff = 0.75f });
        uint afterCoverage = manager.GiTransportInputRevision;

        Assert.Multiple(() =>
        {
            Assert.That(initial, Is.GreaterThan(0u));
            Assert.That(afterEmission, Is.GreaterThan(initial));
            Assert.That(afterRasterOnly, Is.EqualTo(afterEmission));
            Assert.That(afterOcclusion, Is.GreaterThan(afterRasterOnly));
            Assert.That(afterCoverage, Is.GreaterThan(afterOcclusion));
            Assert.That(MaterialManager.AffectsGiTransportInputs(MaterialChangeMask.FarField), Is.False);
        });

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void PrimitiveTransportInput_SurvivesCopyOnWriteAndRasterOnlyRecompile()
    {
        using var manager = new MaterialManager();
        var primitive = new GiMaterialTransportProfile
        {
            AlgorithmVersion = MaterialCompilationContext.CurrentAlgorithmVersion,
            PrimitiveContentHash = 0x1234uL,
            Flags = GiMaterialTransportFlags.BaseStatisticsValid |
                    GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.EmissionProfileValid |
                    GiMaterialTransportFlags.AlphaProfileValid,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling,
            MeanDiffuseReflectance = new Vector3(0.125f, 0.25f, 0.5f),
            MeanMaterialOcclusion = 0.4f,
            AlphaCoverage = 0.75f,
            MeanMetallic = 0.2f,
            MeanRoughness = 0.6f
        };
        var definition = new MaterialDefinition { Name = "Primitive-authored material" };
        MaterialHandle shared = manager.RegisterMaterialDefinition(definition, primitive);
        MaterialHandle alias = manager.RegisterMaterialDefinition(definition, primitive);
        MaterialHandle editable = manager.CreateEditableMaterialCopy(alias);

        manager.UpdateMaterialDefinition(
            editable,
            manager.GetMaterialDefinition(editable) with
            {
                DecalLayer = 3
            });
        GiMaterialTransportProfile recompiled = manager.GetMaterialTransportProfile(editable);

        Assert.Multiple(() =>
        {
            Assert.That(editable, Is.Not.EqualTo(shared));
            Assert.That(recompiled.Quality, Is.EqualTo(GiTransportProfileQuality.PrimitiveSurfaceSampling));
            Assert.That(recompiled.PrimitiveContentHash, Is.EqualTo(primitive.PrimitiveContentHash));
            Assert.That(recompiled.MeanDiffuseReflectance, Is.EqualTo(primitive.MeanDiffuseReflectance));
            Assert.That(recompiled.MeanMaterialOcclusion, Is.EqualTo(primitive.MeanMaterialOcclusion));
            Assert.That(recompiled.AlphaCoverage, Is.EqualTo(primitive.AlphaCoverage));
        });

        manager.ReleaseMaterial(shared);
        manager.ReleaseMaterial(editable);
    }

    [Test]
    public void PrimitiveProfileMemory_IsGaugedAndHardCappedAtAdmission()
    {
        using var manager = new MaterialManager();
        var primitive = new GiMaterialTransportProfile
        {
            Flags = GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.EmissionProfileValid,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling
        };

        MaterialHandle handle = manager.RegisterMaterialDefinition(
            new MaterialDefinition { Name = "Primitive profile memory" },
            primitive);
        MaterialManagerDiagnostics active = manager.Diagnostics;
        int maximumProfiles = checked((int)(
            MaterialManager.MaximumPrimitiveProfileGpuBytes /
            MaterialManager.PrimitiveProfileGpuStrideBytes));

        Assert.Multiple(() =>
        {
            Assert.That(active.ActivePrimitiveProfileCount, Is.EqualTo(1));
            Assert.That(
                active.PrimitiveProfileGpuBytes,
                Is.EqualTo(MaterialManager.PrimitiveProfileGpuStrideBytes));
            Assert.That(
                active.PrimitiveProfileGpuBudgetBytes,
                Is.EqualTo(MaterialManager.MaximumPrimitiveProfileGpuBytes));
            Assert.That(MaterialManager.CanAdmitPrimitiveProfile(maximumProfiles - 1), Is.True);
            Assert.That(MaterialManager.CanAdmitPrimitiveProfile(maximumProfiles), Is.False);
            Assert.That(
                () => MaterialManager.CanAdmitPrimitiveProfile(-1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });

        manager.ReleaseMaterial(handle);
        Assert.That(manager.Diagnostics.ActivePrimitiveProfileCount, Is.Zero);
    }

    [Test]
    public void PrimitiveProfileMemory_EnforcesConfiguredTierCapAtomically()
    {
        using var manager = new MaterialManager();
        ulong oneProfileBudget = MaterialManager.PrimitiveProfileGpuStrideBytes;
        manager.SetPrimitiveProfileGpuBudgetBytes(oneProfileBudget);
        var primitive = new GiMaterialTransportProfile
        {
            Flags = GiMaterialTransportFlags.DiffuseProfileValid,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling
        };

        MaterialHandle first = manager.RegisterMaterialDefinition(
            new MaterialDefinition { Name = "Tier profile 1" },
            primitive);

        Assert.Multiple(() =>
        {
            Assert.That(manager.Diagnostics.PrimitiveProfileGpuBudgetBytes, Is.EqualTo(oneProfileBudget));
            Assert.That(
                MaterialManager.CanAdmitPrimitiveProfile(0, oneProfileBudget),
                Is.True);
            Assert.That(
                MaterialManager.CanAdmitPrimitiveProfile(1, oneProfileBudget),
                Is.False);
            Assert.That(
                () => manager.RegisterMaterialDefinition(
                    new MaterialDefinition { Name = "Tier profile 2" },
                    primitive),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("active quality-tier cap"));
            Assert.That(
                () => manager.SetPrimitiveProfileGpuBudgetBytes(oneProfileBudget - 1),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(manager.Diagnostics.PrimitiveProfileGpuBudgetBytes, Is.EqualTo(oneProfileBudget));
            Assert.That(
                () => manager.SetPrimitiveProfileGpuBudgetBytes(0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => manager.SetPrimitiveProfileGpuBudgetBytes(
                    MaterialManager.MaximumPrimitiveProfileGpuBytes + 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });

        manager.ReleaseMaterial(first);
        manager.SetPrimitiveProfileGpuBudgetBytes(oneProfileBudget - 1);
        Assert.That(
            manager.Diagnostics.PrimitiveProfileGpuBudgetBytes,
            Is.EqualTo(oneProfileBudget - 1));
    }

    [Test]
    public void AuthoredDiffuseEdit_InvalidatesOnlyStalePrimitiveDiffuseChannels()
    {
        using var manager = new MaterialManager();
        var primitive = new GiMaterialTransportProfile
        {
            AlgorithmVersion = MaterialCompilationContext.CurrentAlgorithmVersion,
            PrimitiveContentHash = 0x5678uL,
            Flags = GiMaterialTransportFlags.BaseStatisticsValid |
                    GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.EmissionProfileValid |
                    GiMaterialTransportFlags.AlphaProfileValid |
                    GiMaterialTransportFlags.NormalProfileValid,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling,
            MeanDiffuseReflectance = new Vector3(0.9f, 0.8f, 0.7f),
            MeanEmissiveRadiance = new Vector3(0.1f, 0.2f, 0.3f),
            MeanMaterialOcclusion = 0.4f,
            AlphaCoverage = 0.75f,
            MeanMetallic = 0.2f,
            MeanRoughness = 0.6f,
            NormalVariance = 0.1f
        };
        var definition = new MaterialDefinition
        {
            Name = "Primitive diffuse invalidation",
            EmissiveFactor = primitive.MeanEmissiveRadiance
        };
        MaterialHandle handle = manager.RegisterMaterialDefinition(definition, primitive);
        Vector3 newBaseColor = new(0.25f, 0.5f, 0.75f);

        manager.UpdateMaterialDefinition(
            handle,
            definition with { BaseColorFactor = new Vector4(newBaseColor, 1f) });
        GiMaterialTransportProfile recompiled = manager.GetMaterialTransportProfile(handle);
        Vector3 expectedDiffuse =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                newBaseColor,
                metallic: 0f,
                ior: 1.5f,
                specularFactor: 1f,
                specularColor: Vector3.One,
                transmission: 0f,
                clearcoat: 0f,
                sheenColor: Vector3.Zero,
                nDotV: 1f);

        Assert.Multiple(() =>
        {
            Assert.That(recompiled.Quality, Is.EqualTo(GiTransportProfileQuality.MaterialFactors));
            Assert.That(recompiled.MeanDiffuseReflectance, Is.EqualTo(expectedDiffuse));
            Assert.That(recompiled.MeanDiffuseReflectance, Is.Not.EqualTo(primitive.MeanDiffuseReflectance));
            Assert.That(recompiled.MeanEmissiveRadiance, Is.EqualTo(primitive.MeanEmissiveRadiance));
            Assert.That(recompiled.AlphaCoverage, Is.EqualTo(primitive.AlphaCoverage));
            Assert.That(recompiled.PrimitiveContentHash, Is.EqualTo(primitive.PrimitiveContentHash));
        });

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void AuthoredEmissionEdit_InvalidatesOnlyStalePrimitiveEmissionChannel()
    {
        using var manager = new MaterialManager();
        var primitive = new GiMaterialTransportProfile
        {
            AlgorithmVersion = MaterialCompilationContext.CurrentAlgorithmVersion,
            PrimitiveContentHash = 0x6789uL,
            Flags = GiMaterialTransportFlags.BaseStatisticsValid |
                    GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.EmissionProfileValid |
                    GiMaterialTransportFlags.AlphaProfileValid,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling,
            MeanDiffuseReflectance = new Vector3(0.2f, 0.3f, 0.4f),
            MeanEmissiveRadiance = new Vector3(0.1f, 0.2f, 0.3f),
            AlphaCoverage = 1f
        };
        var definition = new MaterialDefinition
        {
            Name = "Primitive emission invalidation",
            EmissiveFactor = primitive.MeanEmissiveRadiance
        };
        MaterialHandle handle = manager.RegisterMaterialDefinition(definition, primitive);
        Vector3 newEmission = new(0.75f, 0.5f, 0.25f);

        manager.UpdateMaterialDefinition(
            handle,
            definition with { EmissiveFactor = newEmission });
        GiMaterialTransportProfile recompiled = manager.GetMaterialTransportProfile(handle);

        Assert.Multiple(() =>
        {
            Assert.That(recompiled.Quality, Is.EqualTo(GiTransportProfileQuality.MaterialFactors));
            Assert.That(recompiled.MeanDiffuseReflectance, Is.EqualTo(primitive.MeanDiffuseReflectance));
            Assert.That(recompiled.MeanEmissiveRadiance, Is.EqualTo(newEmission));
            Assert.That(recompiled.MeanEmissiveRadiance, Is.Not.EqualTo(primitive.MeanEmissiveRadiance));
            Assert.That(recompiled.PrimitiveContentHash, Is.EqualTo(primitive.PrimitiveContentHash));
        });

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void PrimitiveTransportInput_UsesCurrentTextureStatisticsAfterHotReload()
    {
        var references = new RecordingTextureReferences();
        Vector4 currentTextureMean = new(0.8f, 0.6f, 0.4f, 1f);
        using var manager = new MaterialManager(
            references,
            (binding, _, _) => MaterialTextureTransportInput.Constant(
                BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                currentTextureMean));
        var texture = new TextureHandle(37, 1);
        references.AcquireFromCaller(texture);
        var primitive = new GiMaterialTransportProfile
        {
            AlgorithmVersion = MaterialCompilationContext.CurrentAlgorithmVersion,
            PrimitiveContentHash = 0x9876uL,
            Flags = GiMaterialTransportFlags.BaseStatisticsValid |
                    GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.EmissionProfileValid |
                    GiMaterialTransportFlags.AlphaProfileValid,
            Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling,
            MeanDiffuseReflectance = new Vector3(0.15f, 0.3f, 0.45f),
            MeanMaterialOcclusion = 0.7f,
            MeanMetallic = 0.1f,
            MeanRoughness = 0.8f
        };
        var definition = new MaterialDefinition
        {
            Name = "Hot-reloaded primitive material",
            BaseColor = CreateBinding(texture)
        };
        MaterialHandle handle = manager.RegisterMaterialDefinition(definition, primitive);
        uint profileRevisionBefore = manager.GetMaterialData(handle).TransportProfileRevision;

        currentTextureMean = new Vector4(0.2f, 0.4f, 0.6f, 1f);
        IReadOnlyList<MaterialChangedEvent> changes = manager.NotifyTextureContentChanged(texture);
        GiMaterialTransportProfile recompiled = manager.GetMaterialTransportProfile(handle);
        Vector3 expectedDiffuse =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                new Vector3(currentTextureMean.X, currentTextureMean.Y, currentTextureMean.Z),
                metallic: 0f,
                ior: 1.5f,
                specularFactor: 1f,
                specularColor: Vector3.One,
                transmission: 0f,
                clearcoat: 0f,
                sheenColor: Vector3.Zero,
                nDotV: 1f);

        Assert.Multiple(() =>
        {
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(recompiled.Quality, Is.EqualTo(GiTransportProfileQuality.TextureStatistics));
            Assert.That(recompiled.PrimitiveContentHash, Is.EqualTo(primitive.PrimitiveContentHash));
            Assert.That(recompiled.MeanDiffuseReflectance, Is.EqualTo(expectedDiffuse));
            Assert.That(recompiled.MeanDiffuseReflectance, Is.Not.EqualTo(primitive.MeanDiffuseReflectance));
            Assert.That(
                manager.GetMaterialData(handle).TransportProfileRevision,
                Is.GreaterThan(profileRevisionBefore));
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(references.ReleaseCalls, Is.Zero);
        });

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void AuthoredUpdate_RetriesAgainstTheLatestTextureRevisionBeforePublishing()
    {
        var references = new RecordingTextureReferences();
        Vector4 currentTextureMean = new(0.8f, 0.6f, 0.4f, 1f);
        using var manager = new MaterialManager(
            references,
            (binding, _, _) => MaterialTextureTransportInput.Constant(
                BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                currentTextureMean));
        var texture = new TextureHandle(38, 1);
        references.AcquireFromCaller(texture);
        MaterialDefinition before = CreateDefinition(texture);
        MaterialHandle handle = manager.RegisterMaterialDefinition(before);
        MaterialDefinition after = before with
        {
            Name = "Concurrent authored update",
            BaseColorFactor = new Vector4(0.5f, 0.75f, 0.25f, 1f)
        };
        using var compileEntered = new ManualResetEventSlim();
        using var allowCompileToFinish = new ManualResetEventSlim();
        int authoredResolveCount = 0;
        var authoredContext = new MaterialCompilationContext
        {
            ResolveTexture = (binding, _) =>
            {
                Vector4 sampledMean = currentTextureMean;
                if (Interlocked.Increment(ref authoredResolveCount) == 1)
                {
                    compileEntered.Set();
                    if (!allowCompileToFinish.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting to release the authored material compile.");
                }

                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                    sampledMean);
            }
        };

        Task<MaterialChangedEvent> authoredUpdate = Task.Run(
            () => manager.UpdateMaterialDefinition(handle, after, authoredContext));
        try
        {
            Assert.That(
                compileEntered.Wait(TimeSpan.FromSeconds(10)),
                Is.True,
                "The authored compile did not reach the deterministic interleaving point.");

            currentTextureMean = new Vector4(0.2f, 0.4f, 0.6f, 1f);
            IReadOnlyList<MaterialChangedEvent> textureChanges =
                manager.NotifyTextureContentChanged(texture);
            uint textureRevisionAfterReload =
                manager.GetMaterialData(handle).TextureContentRevision;

            allowCompileToFinish.Set();
            Assert.That(
                authoredUpdate.Wait(TimeSpan.FromSeconds(10)),
                Is.True,
                "The authored update did not finish after the texture reload.");
            MaterialChangedEvent authoredChange = authoredUpdate.GetAwaiter().GetResult();
            GiMaterialTransportProfile finalProfile =
                manager.GetMaterialTransportProfile(handle);
            Vector3 expectedBaseColor = new(
                currentTextureMean.X * after.BaseColorFactor.X,
                currentTextureMean.Y * after.BaseColorFactor.Y,
                currentTextureMean.Z * after.BaseColorFactor.Z);
            Vector3 expectedDiffuse =
                GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                    expectedBaseColor,
                    metallic: 0f,
                    ior: 1.5f,
                    specularFactor: 1f,
                    specularColor: Vector3.One,
                    transmission: 0f,
                    clearcoat: 0f,
                    sheenColor: Vector3.Zero,
                    nDotV: 1f);

            Assert.Multiple(() =>
            {
                Assert.That(textureChanges, Has.Count.EqualTo(1));
                Assert.That(authoredChange.ChangeMask, Is.Not.EqualTo(MaterialChangeMask.None));
                Assert.That(authoredResolveCount, Is.GreaterThanOrEqualTo(2));
                Assert.That(manager.GetMaterialDefinition(handle), Is.EqualTo(after));
                Assert.That(finalProfile.MeanDiffuseReflectance, Is.EqualTo(expectedDiffuse));
                Assert.That(
                    manager.GetMaterialData(handle).TextureContentRevision,
                    Is.EqualTo(textureRevisionAfterReload));
                Assert.That(textureRevisionAfterReload, Is.Not.Zero);
            });
        }
        finally
        {
            allowCompileToFinish.Set();
        }

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void TextureReload_DoesNotOverwriteAnAuthoredEditThatRemovesItsDependency()
    {
        var references = new RecordingTextureReferences();
        using var compileEntered = new ManualResetEventSlim();
        using var allowCompileToFinish = new ManualResetEventSlim();
        int blockNextResolve = 0;
        using var manager = new MaterialManager(
            references,
            (binding, _, _) =>
            {
                if (Interlocked.Exchange(ref blockNextResolve, 0) == 1)
                {
                    compileEntered.Set();
                    if (!allowCompileToFinish.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting to release the texture material compile.");
                }

                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                    Vector4.One);
            });
        var texture = new TextureHandle(39, 1);
        references.AcquireFromCaller(texture);
        MaterialDefinition before = CreateDefinition(texture);
        MaterialHandle handle = manager.RegisterMaterialDefinition(before);
        MaterialDefinition after = before with
        {
            Name = "Texture-independent authored edit",
            BaseColor = MaterialTextureBinding.Missing,
            BaseColorFactor = new Vector4(0.1f, 0.2f, 0.3f, 1f)
        };

        Volatile.Write(ref blockNextResolve, 1);
        Task<IReadOnlyList<MaterialChangedEvent>> textureReload = Task.Run(
            () => manager.NotifyTextureContentChanged(texture));
        try
        {
            Assert.That(
                compileEntered.Wait(TimeSpan.FromSeconds(10)),
                Is.True,
                "The texture compile did not reach the deterministic interleaving point.");

            manager.UpdateMaterialDefinition(handle, after);
            uint authoredContentRevision = manager.GetMaterialContentRevision(handle.Index);
            allowCompileToFinish.Set();
            Assert.That(
                textureReload.Wait(TimeSpan.FromSeconds(10)),
                Is.True,
                "The texture reload did not finish after the authored edit.");
            IReadOnlyList<MaterialChangedEvent> staleTextureChanges =
                textureReload.GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(staleTextureChanges, Is.Empty);
                Assert.That(manager.GetMaterialDefinition(handle), Is.EqualTo(after));
                Assert.That(
                    manager.GetMaterialContentRevision(handle.Index),
                    Is.EqualTo(authoredContentRevision));
                Assert.That(manager.GetMaterialData(handle).TextureContentRevision, Is.Zero);
                Assert.That(references.BalanceFor(texture), Is.Zero);
            });
        }
        finally
        {
            allowCompileToFinish.Set();
        }

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void ConcurrentTextureReloads_CannotPublishAnOlderCompileLast()
    {
        var references = new RecordingTextureReferences();
        Vector4 currentTextureMean = new(0.9f, 0.7f, 0.5f, 1f);
        using var firstCompileEntered = new ManualResetEventSlim();
        using var allowFirstCompileToFinish = new ManualResetEventSlim();
        int blockNextResolve = 0;
        int resolveCount = 0;
        using var manager = new MaterialManager(
            references,
            (binding, _, _) =>
            {
                Vector4 sampledMean = currentTextureMean;
                Interlocked.Increment(ref resolveCount);
                if (Interlocked.Exchange(ref blockNextResolve, 0) == 1)
                {
                    firstCompileEntered.Set();
                    if (!allowFirstCompileToFinish.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting to release the first texture compile.");
                }

                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                    sampledMean);
            });
        var texture = new TextureHandle(43, 1);
        references.AcquireFromCaller(texture);
        MaterialDefinition definition = CreateDefinition(texture);
        MaterialHandle handle = manager.RegisterMaterialDefinition(definition);
        int registrationResolveCount = resolveCount;

        Volatile.Write(ref blockNextResolve, 1);
        Task<IReadOnlyList<MaterialChangedEvent>> firstReload = Task.Run(
            () => manager.NotifyTextureContentChanged(texture));
        try
        {
            Assert.That(
                firstCompileEntered.Wait(TimeSpan.FromSeconds(10)),
                Is.True,
                "The first texture compile did not reach the deterministic interleaving point.");

            currentTextureMean = new Vector4(0.15f, 0.35f, 0.55f, 1f);
            IReadOnlyList<MaterialChangedEvent> secondReload =
                manager.NotifyTextureContentChanged(texture);
            uint revisionAfterSecondReload =
                manager.GetMaterialData(handle).TextureContentRevision;

            allowFirstCompileToFinish.Set();
            Assert.That(
                firstReload.Wait(TimeSpan.FromSeconds(10)),
                Is.True,
                "The first texture reload did not retry after the newer publication.");
            IReadOnlyList<MaterialChangedEvent> firstReloadChanges =
                firstReload.GetAwaiter().GetResult();
            GiMaterialTransportProfile finalProfile =
                manager.GetMaterialTransportProfile(handle);
            Vector3 expectedDiffuse =
                GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                    new Vector3(currentTextureMean.X, currentTextureMean.Y, currentTextureMean.Z),
                    metallic: 0f,
                    ior: 1.5f,
                    specularFactor: 1f,
                    specularColor: Vector3.One,
                    transmission: 0f,
                    clearcoat: 0f,
                    sheenColor: Vector3.Zero,
                    nDotV: 1f);

            Assert.Multiple(() =>
            {
                Assert.That(secondReload, Has.Count.EqualTo(1));
                Assert.That(firstReloadChanges, Has.Count.EqualTo(1));
                Assert.That(resolveCount - registrationResolveCount, Is.GreaterThanOrEqualTo(3));
                Assert.That(finalProfile.MeanDiffuseReflectance, Is.EqualTo(expectedDiffuse));
                Assert.That(
                    manager.GetMaterialData(handle).TextureContentRevision,
                    Is.GreaterThan(revisionAfterSecondReload));
            });
        }
        finally
        {
            allowFirstCompileToFinish.Set();
        }

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void AuthoredUpdate_FailsClosedAfterBoundedRepeatedPublicationRaces()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(
            references,
            (binding, _, _) => MaterialTextureTransportInput.Constant(
                BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                Vector4.One));
        manager.SetTransportV2Enabled(true);
        var texture = new TextureHandle(44, 1);
        references.AcquireFromCaller(texture);
        MaterialDefinition before = CreateDefinition(texture);
        MaterialHandle handle = manager.RegisterMaterialDefinition(before);
        MaterialDefinition after = before with
        {
            Name = "Never publish stale",
            BaseColorFactor = new Vector4(0.25f, 0.5f, 0.75f, 1f)
        };
        int resolveCount = 0;
        var racingContext = new MaterialCompilationContext
        {
            ResolveTexture = (binding, _) =>
            {
                int attempt = Interlocked.Increment(ref resolveCount);
                manager.SetTransportV2Enabled((attempt & 1) == 0);
                return MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
                    Vector4.One);
            }
        };

        Assert.That(
            () => manager.UpdateMaterialDefinition(handle, after, racingContext),
            Throws.InvalidOperationException.With.Message.Contains("No stale payload was published"));
        Assert.Multiple(() =>
        {
            Assert.That(resolveCount, Is.EqualTo(4));
            Assert.That(manager.GetMaterialDefinition(handle), Is.EqualTo(before));
            Assert.That(manager.TransportV2Enabled, Is.True);
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(references.ReleaseCalls, Is.Zero);
        });

        manager.ReleaseMaterial(handle);
    }

    [Test]
    public void TransportV2KillSwitch_IsAtomicReversibleAndNeverPromotesRawV1()
    {
        using var manager = new MaterialManager();
        manager.SetTransportV2Enabled(true);
        var definition = new MaterialDefinition
        {
            Name = "Kill-switch authored",
            MetallicFactor = 0.25f,
            RoughnessFactor = 0.6f
        };
        MaterialHandle authored = manager.RegisterMaterialDefinition(definition);
#pragma warning disable CS0618
        MaterialHandle rawV1 = manager.RegisterMaterial(MaterialManager.CreateDefaultMaterial());
#pragma warning restore CS0618
        MaterialDefinition authoredDefinition = manager.GetMaterialDefinition(authored);
        GiMaterialTransportProfile authoredProfile = manager.GetMaterialTransportProfile(authored);
        MaterialAspectRevisions authoredBefore = manager.GetMaterialAspectRevisions(authored);
        MaterialAspectRevisions rawBefore = manager.GetMaterialAspectRevisions(rawV1);
        uint dataRevisionBefore = manager.MaterialDataRevision;
        uint giTransportRevisionBefore = manager.GiTransportInputRevision;

        manager.SetTransportV2Enabled(false);
        MaterialAspectRevisions authoredDisabled = manager.GetMaterialAspectRevisions(authored);
        uint disabledDataRevision = manager.MaterialDataRevision;
        uint disabledGiTransportRevision = manager.GiTransportInputRevision;
        manager.SetTransportV2Enabled(false);

        Assert.Multiple(() =>
        {
            Assert.That(manager.TransportV2Enabled, Is.False);
            Assert.That(HasLegacyFallback(manager.GetMaterialData(authored)), Is.True);
            Assert.That(HasLegacyFallback(manager.GetMaterialData(rawV1)), Is.True);
            Assert.That(authoredDisabled.Material, Is.GreaterThan(authoredBefore.Material));
            Assert.That(manager.GetMaterialAspectRevisions(rawV1), Is.EqualTo(rawBefore));
            Assert.That(disabledDataRevision, Is.GreaterThan(dataRevisionBefore));
            Assert.That(disabledGiTransportRevision, Is.GreaterThan(giTransportRevisionBefore));
            Assert.That(manager.MaterialDataRevision, Is.EqualTo(disabledDataRevision));
            Assert.That(manager.GiTransportInputRevision, Is.EqualTo(disabledGiTransportRevision));
            Assert.That(manager.GetMaterialDefinition(authored), Is.EqualTo(authoredDefinition));
            Assert.That(manager.GetMaterialTransportProfile(authored), Is.EqualTo(authoredProfile));
        });

        manager.SetTransportV2Enabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(manager.TransportV2Enabled, Is.True);
            Assert.That(HasLegacyFallback(manager.GetMaterialData(authored)), Is.False);
            Assert.That(HasLegacyFallback(manager.GetMaterialData(rawV1)), Is.True);
            Assert.That(
                manager.GetMaterialAspectRevisions(authored).Material,
                Is.GreaterThan(authoredDisabled.Material));
            Assert.That(manager.GetMaterialAspectRevisions(rawV1), Is.EqualTo(rawBefore));
            Assert.That(manager.GetMaterialDefinition(authored), Is.EqualTo(authoredDefinition));
            Assert.That(manager.GetMaterialTransportProfile(authored), Is.EqualTo(authoredProfile));
        });

        manager.ReleaseMaterial(authored);
        manager.ReleaseMaterial(rawV1);
    }

    [Test]
    public void AuthoredUpdate_RollsBackPartialRetainsWhenAcquisitionFails()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var oldTexture = new TextureHandle(40, 1);
        var firstNewTexture = new TextureHandle(41, 1);
        var secondNewTexture = new TextureHandle(42, 1);
        MaterialDefinition before = CreateDefinition(oldTexture);
        MaterialDefinition after = new()
        {
            BaseColor = CreateBinding(firstNewTexture),
            Normal = CreateBinding(secondNewTexture)
        };
        references.AcquireFromCaller(oldTexture);
        references.AcquireFromCaller(firstNewTexture);
        references.AcquireFromCaller(secondNewTexture);
        MaterialHandle handle = manager.RegisterMaterialDefinition(before, CreateCompilationContext());
        references.FailRetainCall = 2;

        Assert.That(
            () => manager.UpdateMaterialDefinition(handle, after, CreateCompilationContext()),
            Throws.InvalidOperationException.With.Message.Contains("Injected retain failure"));

        Assert.Multiple(() =>
        {
            Assert.That(manager.GetMaterialDefinition(handle), Is.EqualTo(before));
            Assert.That(references.RetainCalls, Is.EqualTo(2));
            Assert.That(references.ReleasesFor(firstNewTexture), Is.EqualTo(1));
            Assert.That(references.BalanceFor(oldTexture), Is.EqualTo(1));
            Assert.That(references.BalanceFor(firstNewTexture), Is.EqualTo(1));
            Assert.That(references.BalanceFor(secondNewTexture), Is.EqualTo(1));
        });

        manager.ReleaseMaterial(handle);
        references.ReleaseTexture(firstNewTexture);
        references.ReleaseTexture(secondNewTexture);
    }

    [Test]
    public void Validator_ValidatesEveryExtensionTextureBinding()
    {
        MaterialTextureBinding invalid = MaterialTextureBinding.Missing with { TexCoordSet = 2 };
        MaterialExtensionDefinition[] definitions =
        [
            new() { Clearcoat = invalid },
            new() { ClearcoatRoughnessTexture = invalid },
            new() { ClearcoatNormal = invalid },
            new() { SheenColor = invalid },
            new() { SheenRoughnessTexture = invalid },
            new() { Anisotropy = invalid },
            new() { Transmission = invalid },
            new() { Thickness = invalid },
            new() { Specular = invalid },
            new() { SpecularColor = invalid },
            new() { Iridescence = invalid },
            new() { IridescenceThickness = invalid },
            new() { Subsurface = invalid }
        ];
        string[] names =
        [
            "Extensions.Clearcoat",
            "Extensions.ClearcoatRoughnessTexture",
            "Extensions.ClearcoatNormal",
            "Extensions.SheenColor",
            "Extensions.SheenRoughnessTexture",
            "Extensions.Anisotropy",
            "Extensions.Transmission",
            "Extensions.Thickness",
            "Extensions.Specular",
            "Extensions.SpecularColor",
            "Extensions.Iridescence",
            "Extensions.IridescenceThickness",
            "Extensions.Subsurface"
        ];

        Assert.That(definitions, Has.Length.EqualTo(names.Length));
        for (int i = 0; i < definitions.Length; i++)
        {
            ArgumentOutOfRangeException? exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    new MaterialDefinition { Extensions = definitions[i] }));
            Assert.That(exception!.ParamName, Is.EqualTo(names[i]));
        }
    }

    [Test]
    public void Validator_RejectsNonFiniteExtensionBindingTransformBeforeOwnershipChanges()
    {
        var references = new RecordingTextureReferences();
        using var manager = new MaterialManager(references);
        var texture = new TextureHandle(50, 1);
        references.AcquireFromCaller(texture);
        MaterialDefinition before = CreateDefinition(texture);
        MaterialHandle handle = manager.RegisterMaterialDefinition(before, CreateCompilationContext());
        MaterialDefinition invalid = before with
        {
            Extensions = new MaterialExtensionDefinition
            {
                Clearcoat = CreateBinding(texture) with
                {
                    Offset = new Vector2(float.NaN, 0f)
                }
            }
        };

        Assert.That(
            () => manager.UpdateMaterialDefinition(handle, invalid, CreateCompilationContext()),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.Multiple(() =>
        {
            Assert.That(references.RetainCalls, Is.Zero);
            Assert.That(references.ReleaseCalls, Is.Zero);
            Assert.That(manager.GetMaterialDefinition(handle), Is.EqualTo(before));
        });

        manager.ReleaseMaterial(handle);
    }

    private static MaterialDefinition CreateDefinition(TextureHandle texture) => new()
    {
        Name = $"Material {texture.Index}",
        BaseColor = CreateBinding(texture)
    };

    private static MaterialTextureBinding CreateBinding(TextureHandle texture) => new()
    {
        Texture = texture
    };

    private static MaterialCompilationContext CreateCompilationContext() => new()
    {
        ResolveTexture = (binding, _) => MaterialTextureTransportInput.Constant(
            BindlessIndex.FirstDynamicTextureIndex + binding.Texture.Index,
            Vector4.One)
    };

    private static Dictionary<TextureHandle, int> ToCounts(
        IEnumerable<MaterialManager.TextureReferenceAdjustment> adjustments) =>
        adjustments.ToDictionary(adjustment => adjustment.Handle, adjustment => adjustment.Count);

    private static bool HasLegacyFallback(GPUMaterialData material) =>
        ((GiMaterialTransportFlags)material.TransportFlags)
        .HasFlag(GiMaterialTransportFlags.LegacyV1Fallback);

    private sealed class RecordingTextureReferences : ITextureReferenceManager
    {
        private readonly Dictionary<TextureHandle, int> _balances = new();
        private readonly Dictionary<TextureHandle, int> _retains = new();
        private readonly Dictionary<TextureHandle, int> _releases = new();

        public int RetainCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int? FailRetainCall { get; set; }

        public void AcquireFromCaller(TextureHandle handle, int count = 1)
        {
            _balances.TryGetValue(handle, out int current);
            _balances[handle] = checked(current + count);
        }

        public void RetainTexture(TextureHandle handle)
        {
            RetainCalls++;
            if (FailRetainCall == RetainCalls)
                throw new InvalidOperationException("Injected retain failure.");

            _retains.TryGetValue(handle, out int retains);
            _retains[handle] = retains + 1;
            AcquireFromCaller(handle);
        }

        public void ReleaseTexture(TextureHandle handle, Fence retireFence = default)
        {
            ReleaseCalls++;
            _balances.TryGetValue(handle, out int balance);
            if (balance <= 0)
                throw new InvalidOperationException($"Texture {handle.Index} was over-released.");
            _balances[handle] = balance - 1;
            _releases.TryGetValue(handle, out int releases);
            _releases[handle] = releases + 1;
        }

        public int BalanceFor(TextureHandle handle) =>
            _balances.TryGetValue(handle, out int value) ? value : 0;

        public int RetainsFor(TextureHandle handle) =>
            _retains.TryGetValue(handle, out int value) ? value : 0;

        public int ReleasesFor(TextureHandle handle) =>
            _releases.TryGetValue(handle, out int value) ? value : 0;
    }
}
