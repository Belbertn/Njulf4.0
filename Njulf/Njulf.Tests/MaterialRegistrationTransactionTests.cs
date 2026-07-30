using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialRegistrationTransactionTests
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
    public void FailureAtEveryPublicationStage_RestoresAllManagerState(
        int failedStageValue)
    {
        var failedStage =
            (MaterialRegistrationPublicationStage)
            failedStageValue;
        using var manager = new MaterialManager();
        MaterialHandle retired =
            manager.RegisterMaterialDefinition(
                new MaterialDefinition
                {
                    Name = "retired-slot"
                });
        manager.ReleaseMaterial(retired);
        MaterialManagerDiagnostics before =
            manager.Diagnostics;
        uint dataRevisionBefore =
            manager.MaterialDataRevision;
        var texture = new TextureHandle(160, 1);
        MaterialDefinition candidate = new()
        {
            Name = $"fault-{failedStage}",
            BaseColor = new MaterialTextureBinding
            {
                Texture = texture
            },
            FeatureFlags =
                MaterialFeatureFlags.Clearcoat,
            Extensions = new MaterialExtensionDefinition
            {
                ClearcoatFactor = 0.5f
            }
        };
        manager.RegistrationPublicationFaultInjector =
            stage =>
            {
                if (stage == failedStage)
                {
                    throw new InvalidOperationException(
                        $"injected {stage}");
                }
            };

        Assert.That(
            () => manager.RegisterMaterialDefinition(
                candidate,
                CreateCompilationContext()),
            Throws.InvalidOperationException.With.Message.Contains(
                failedStage.ToString()));

        MaterialManagerDiagnostics after =
            manager.Diagnostics;
        Assert.Multiple(() =>
        {
            Assert.That(
                after.RegisteredMaterialCount,
                Is.EqualTo(before.RegisteredMaterialCount));
            Assert.That(
                after.UploadedMaterialCount,
                Is.EqualTo(before.UploadedMaterialCount));
            Assert.That(
                after.MaterialExtensionDataCount,
                Is.EqualTo(before.MaterialExtensionDataCount));
            Assert.That(
                after.TrackedTextureDependencyCount,
                Is.EqualTo(before.TrackedTextureDependencyCount));
            Assert.That(
                after.ActivePrimitiveProfileCount,
                Is.EqualTo(before.ActivePrimitiveProfileCount));
            Assert.That(
                manager.MaterialDataRevision,
                Is.EqualTo(dataRevisionBefore));
            Assert.That(
                manager.GetReferencedTextureHandles(),
                Does.Not.Contain(texture));
        });

        manager.RegistrationPublicationFaultInjector = null;
        MaterialHandle retry =
            manager.RegisterMaterialDefinition(
                candidate,
                CreateCompilationContext());
        Assert.Multiple(() =>
        {
            Assert.That(retry.Index, Is.EqualTo(retired.Index));
            Assert.That(
                manager.GetMaterialDefinition(retry),
                Is.EqualTo(
                    MaterialDefinitionValidator
                        .ValidateAndNormalize(candidate)));
            Assert.That(
                manager.GetReferencedTextureHandles(),
                Does.Contain(texture));
        });
        manager.ReleaseMaterial(retry);
    }

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
}
