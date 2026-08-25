using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DirectionalShadowContractsTests
{
    [Test]
    public void ShadowSettings_InvalidEnumValuesFailToLegacyCascadedContract()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode = (DirectionalShadowMode)99,
            DirectionalFilterMode = (DirectionalShadowFilterMode)99,
            DirectionalBiasMode = (DirectionalShadowBiasMode)99,
            DirectionalPcfRadiusMode = (DirectionalPcfRadiusMode)99
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.RequestedDirectionalShadowMode,
                Is.EqualTo(DirectionalShadowMode.Cascaded));
            Assert.That(settings.DirectionalFilterMode,
                Is.EqualTo(DirectionalShadowFilterMode.LegacyBoxPcf));
            Assert.That(settings.DirectionalBiasMode,
                Is.EqualTo(DirectionalShadowBiasMode.Legacy));
            Assert.That(settings.DirectionalPcfRadiusMode,
                Is.EqualTo(DirectionalPcfRadiusMode.Constant));
        });
    }

    [Test]
    public void SurfaceHistoryPolicy_UnifiesAllActiveConsumers()
    {
        var settings = new RenderSettings();
        settings.AntiAliasing.Mode = AntiAliasingMode.Taa;
        settings.Shadows.RequestedDirectionalShadowMode =
            DirectionalShadowMode.RayQuerySoft;

        SurfaceHistoryConsumer consumers = SurfaceHistoryPolicy.Resolve(
            settings,
            nearFieldResidualActive: true,
            directionalCsmTemporalActive: true,
            directionalRaySoftActive: true);

        Assert.Multiple(() =>
        {
            Assert.That(consumers.HasFlag(SurfaceHistoryConsumer.TemporalAntiAliasing), Is.True);
            Assert.That(consumers.HasFlag(SurfaceHistoryConsumer.DirectionalCsmTemporal), Is.True);
            Assert.That(consumers.HasFlag(SurfaceHistoryConsumer.DirectionalRaySoft), Is.True);
            Assert.That(consumers.HasFlag(SurfaceHistoryConsumer.NearFieldResidual), Is.True);
            Assert.That(consumers.RequiresMotionVectors(), Is.True);
        });
    }

    [Test]
    public void SurfaceHistoryPolicy_DoesNotChargeHistoryForGatedSoftIntent()
    {
        var settings = new RenderSettings();
        settings.AntiAliasing.Mode = AntiAliasingMode.SmaaHigh;
        settings.Shadows.RequestedDirectionalShadowMode =
            DirectionalShadowMode.RayQuerySoft;

        SurfaceHistoryConsumer consumers = SurfaceHistoryPolicy.Resolve(
            settings,
            nearFieldResidualActive: false);

        Assert.That(consumers, Is.EqualTo(SurfaceHistoryConsumer.None));
    }

    [Test]
    public void RaySceneReadiness_RequiresConsumerCategoryCompletenessAndGeneration()
    {
        var snapshot = new RaySceneReadinessSnapshot(
            RaySceneConsumer.Ddgi | RaySceneConsumer.DirectionalFull,
            RaySceneConsumer.DirectionalFull,
            RaySceneGeometryCategory.DirectionalShadowDefault,
            RaySceneGeometryCategory.DirectionalShadowDefault,
            ResourceGeneration: 7u,
            ContentEpoch: 11UL,
            FailureDetail: string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsReady(
                RaySceneConsumer.DirectionalFull,
                RaySceneGeometryCategory.DirectionalShadowDefault), Is.True);
            Assert.That(snapshot.IsReady(
                RaySceneConsumer.Ddgi,
                RaySceneGeometryCategory.StaticOpaque), Is.False);
            Assert.That(snapshot.IsReady(
                RaySceneConsumer.DirectionalFull,
                RaySceneGeometryCategory.ThinTransmission), Is.False);
        });
    }

    [Test]
    public void RaySceneRequirements_UnionConsumersAndStrictestCoverage()
    {
        var contact = new RaySceneRequirement(
            RaySceneConsumer.DirectionalContact,
            RaySceneGeometryCategory.StaticOpaque | RaySceneGeometryCategory.AlphaTested,
            3f,
            RequiresCurrentPose: false);
        var ddgi = new RaySceneRequirement(
            RaySceneConsumer.Ddgi,
            RaySceneGeometryCategory.DynamicOpaque | RaySceneGeometryCategory.SkinnedCurrentPose,
            80f,
            RequiresCurrentPose: true);

        RaySceneRequirement union = contact.Union(ddgi);

        Assert.Multiple(() =>
        {
            Assert.That(union.Consumers, Is.EqualTo(RaySceneConsumer.DirectionalContact | RaySceneConsumer.Ddgi));
            Assert.That(union.RequiredCategories.HasFlag(RaySceneGeometryCategory.StaticOpaque), Is.True);
            Assert.That(union.RequiredCategories.HasFlag(RaySceneGeometryCategory.SkinnedCurrentPose), Is.True);
            Assert.That(union.MaximumRayDistance, Is.EqualTo(80f));
            Assert.That(union.RequiresCurrentPose, Is.True);
        });
    }

    [Test]
    public void RaySceneRequirements_PrepareExplicitSoftIntentForExperimentalUse()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode = DirectionalShadowMode.RayQuerySoft
        };

        RaySceneRequirement requirement =
            RaySceneRequirement.ForDirectionalShadows(settings);

        Assert.Multiple(() =>
        {
            Assert.That(requirement.Consumers,
                Is.EqualTo(RaySceneConsumer.DirectionalFull));
            Assert.That(requirement.RequiredCategories,
                Is.EqualTo(RaySceneGeometryCategory.DirectionalShadowDefault));
            Assert.That(requirement.RequiresCurrentPose, Is.True);
        });
    }

    [Test]
    public void RaySceneRequirements_AreaShadowsRequireSelectedEmitterAndFullCoverage()
    {
        var settings = new ShadowSettings
        {
            AreaShadowsEnabled = true
        };

        RaySceneRequirement enabled =
            RaySceneRequirement.ForAreaLightShadows(
                settings,
                hasSelectedAreaLight: true,
                maximumRayDistance: 18f);
        RaySceneRequirement empty =
            RaySceneRequirement.ForAreaLightShadows(
                settings,
                hasSelectedAreaLight: false,
                maximumRayDistance: 18f);

        Assert.Multiple(() =>
        {
            Assert.That(enabled.Consumers,
                Is.EqualTo(RaySceneConsumer.AreaLightShadows));
            Assert.That(enabled.RequiredCategories,
                Is.EqualTo(RaySceneGeometryCategory.DirectionalShadowDefault));
            Assert.That(enabled.MaximumRayDistance, Is.EqualTo(18f));
            Assert.That(enabled.RequiresCurrentPose, Is.True);
            Assert.That(empty.Enabled, Is.False);
        });
    }

    [Test]
    public void DirectionalModeResolver_FailsClosedUntilSharedRaySceneIsComplete()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode = DirectionalShadowMode.RayQueryHard
        };
        RaySceneReadinessSnapshot incomplete = RaySceneReadinessSnapshot.Unavailable(
            RaySceneConsumer.DirectionalFull,
            "current-pose foliage is not resident");

        var fallback = DirectionalShadowModeResolver.Resolve(
            settings,
            hasShadowCastingDirectionalLight: true,
            rayQuerySupported: true,
            incomplete);
        var complete = DirectionalShadowModeResolver.Resolve(
            settings,
            hasShadowCastingDirectionalLight: true,
            rayQuerySupported: true,
            new RaySceneReadinessSnapshot(
                RaySceneConsumer.DirectionalFull,
                RaySceneConsumer.DirectionalFull,
                RaySceneGeometryCategory.DirectionalShadowDefault,
                RaySceneGeometryCategory.DirectionalShadowDefault,
                4u,
                8UL,
                string.Empty)
            {
                CoverageMinimum = new(-100f, -100f, -100f),
                CoverageMaximum = new(100f, 100f, 100f),
                ExactCategories =
                    RaySceneGeometryCategory.DirectionalShadowDefault
            });

        Assert.Multiple(() =>
        {
            Assert.That(fallback.Effective, Is.EqualTo(DirectionalShadowMode.Cascaded));
            Assert.That(fallback.Reason, Is.EqualTo(DirectionalShadowFallbackReason.RaySceneIncomplete));
            Assert.That(fallback.Detail, Does.Contain("foliage"));
            Assert.That(complete.Effective, Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(complete.Reason, Is.EqualTo(DirectionalShadowFallbackReason.None));
        });
    }

    [Test]
    public void DirectionalModeResolver_RequiresConcreteMaskAndKeepsSoftModeGated()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode = DirectionalShadowMode.RayQueryHard
        };
        var ready = new RaySceneReadinessSnapshot(
            RaySceneConsumer.DirectionalFull,
            RaySceneConsumer.DirectionalFull,
            RaySceneGeometryCategory.DirectionalShadowDefault,
            RaySceneGeometryCategory.DirectionalShadowDefault,
            9u,
            12UL,
            string.Empty);

        var missingMask = DirectionalShadowModeResolver.Resolve(
            settings,
            hasShadowCastingDirectionalLight: true,
            rayQuerySupported: true,
            ready,
            rayMaskAvailable: false);
        settings.RequestedDirectionalShadowMode =
            DirectionalShadowMode.RayQuerySoft;
        var softUnqualified = DirectionalShadowModeResolver.Resolve(
            settings,
            hasShadowCastingDirectionalLight: true,
            rayQuerySupported: true,
            ready,
            rayMaskAvailable: true,
            softRayAvailable: false);

        Assert.Multiple(() =>
        {
            Assert.That(missingMask.Effective,
                Is.EqualTo(DirectionalShadowMode.Cascaded));
            Assert.That(missingMask.Reason,
                Is.EqualTo(DirectionalShadowFallbackReason
                    .RequiredReceiverResourceUnavailable));
            Assert.That(softUnqualified.Effective,
                Is.EqualTo(DirectionalShadowMode.Cascaded));
            Assert.That(softUnqualified.Detail, Does.Contain("finite-sun"));
        });
    }
}
