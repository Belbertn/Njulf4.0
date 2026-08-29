using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ShadowFramePlannerTests
{
    [Test]
    public void ResolveCandidate_ZeroRadiusSoftModeBecomesDeterministicHard()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode =
                DirectionalShadowMode.RayQuerySoft
        };
        var planner = new ShadowFramePlanner();

        ShadowFrameCandidate candidate = planner.ResolveCandidate(
            CreateCandidateInput(settings) with
            {
                SoftCollapsesToHard = true,
                SoftHistoryAvailable = false
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                candidate.EffectiveMode,
                Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(
                candidate.FallbackReason,
                Is.EqualTo(DirectionalShadowFallbackReason.None));
            Assert.That(
                candidate.FallbackDetail,
                Is.EqualTo(
                    "zero directional soft angular diameter resolves to deterministic hard rays"));
        });
    }

    [Test]
    public void ResolveCandidate_PreservesConcreteFailureAndUniversalFallbackPrecedence()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode =
                DirectionalShadowMode.RayQueryHard
        };
        var planner = new ShadowFramePlanner();
        ShadowFrameCandidateInput input =
            CreateCandidateInput(settings) with
            {
                RayMaskAvailable = false,
                RayResourceFailureDetail =
                    "directional ray-mask allocation failed: out of memory"
            };

        ShadowFrameCandidate concreteFailure =
            planner.ResolveCandidate(input);
        ShadowFrameCandidate universalFailure =
            planner.ResolveCandidate(input with
            {
                UniversalCsmFallbackAvailable = false
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                concreteFailure.FallbackReason,
                Is.EqualTo(
                    DirectionalShadowFallbackReason.ResourceAllocationFailed));
            Assert.That(
                concreteFailure.FallbackDetail,
                Is.EqualTo(input.RayResourceFailureDetail));
            Assert.That(
                universalFailure.FallbackReason,
                Is.EqualTo(
                    DirectionalShadowFallbackReason.ResourceAllocationFailed));
            Assert.That(
                universalFailure.FallbackDetail,
                Is.EqualTo(
                    "the universal directional cascade fallback resources are unavailable"));
        });
    }

    [Test]
    public void CreatePlan_PublishesQualifiedFullRayPolicyAndIdentity()
    {
        RenderSettings settings = CreateSettings(
            DirectionalShadowMode.RayQueryHard);
        var candidate = new ShadowFrameCandidate(
            DirectionalShadowMode.RayQueryHard,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        DirectionalShadowQualificationGateResult qualification =
            QualifiedGate(csmTemporalApproved: false);
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan plan = planner.CreatePlan(
            CreatePlanInput(settings, candidate) with
            {
                RayQualification = qualification,
                CascadeCount = 9,
                StableLightIdentity = 42UL,
                GeometryDecalCsmFallbackRequired = true,
                ScreenResourceGeneration = 19u,
                SunAngularRadiusRadians = 0.0125f
            });

        Assert.Multiple(() =>
        {
            Assert.That(plan.StableLightIdentity, Is.EqualTo(42UL));
            Assert.That(
                plan.RequestedMode,
                Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(
                plan.EffectiveMode,
                Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(
                plan.FallbackReason,
                Is.EqualTo(DirectionalShadowFallbackReason.None));
            Assert.That(plan.CascadedReceiverFallbackRequired, Is.True);
            Assert.That(plan.ActiveCascadeMask, Is.EqualTo(0b1111u));
            Assert.That(plan.StaticRefreshMask, Is.Zero);
            Assert.That(plan.StaticReuseMask, Is.Zero);
            Assert.That(plan.WorkingCompositionMask, Is.EqualTo(0b1111u));
            Assert.That(
                plan.RaySceneRequirement,
                Is.EqualTo(RaySceneConsumer.DirectionalFull));
            Assert.That(plan.HistoryConsumers, Is.EqualTo(
                SurfaceHistoryConsumer.None));
            Assert.That(plan.RaySceneResourceGeneration, Is.EqualTo(7u));
            Assert.That(plan.RaySceneContentEpoch, Is.EqualTo(11UL));
            Assert.That(
                plan.OpaqueReceiverPolicy,
                Is.EqualTo(
                    DirectionalShadowReceiverPolicy.OpaqueScreenMask));
            Assert.That(
                plan.TransparentReceiverPolicy,
                Is.EqualTo(
                    DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery));
            Assert.That(
                plan.DecalReceiverPolicy,
                Is.EqualTo(
                    DirectionalShadowReceiverPolicy.DecalDepthOwnerMask));
            Assert.That(plan.ScreenResourceGeneration, Is.EqualTo(19u));
            Assert.That(plan.SunAngularRadiusRadians, Is.EqualTo(0.0125f));
            Assert.That(
                plan.QualificationLevel,
                Is.EqualTo(DirectionalShadowQualificationLevel.Production));
            Assert.That(plan.QualificationId, Is.EqualTo("qualified-shadow"));
            Assert.That(plan.QualificationDeviceRuleId, Is.EqualTo("device"));
            Assert.That(plan.QualificationTrackId, Is.EqualTo("track"));
            Assert.That(plan.QualifiedGpuBudgetMicroseconds, Is.EqualTo(900.0));
            Assert.That(plan.QualifiedMemoryBudgetBytes, Is.EqualTo(4096UL));
        });
    }

    [TestCase(false, DirectionalShadowReceiverPolicy.Cascaded)]
    [TestCase(
        true,
        DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery)]
    public void CreatePlan_HybridTransparentPolicyTracksVariantAvailability(
        bool transparentVariantAvailable,
        DirectionalShadowReceiverPolicy expectedPolicy)
    {
        RenderSettings settings = CreateSettings(
            DirectionalShadowMode.HybridContact);
        var candidate = new ShadowFrameCandidate(
            DirectionalShadowMode.HybridContact,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan plan = planner.CreatePlan(
            CreatePlanInput(settings, candidate) with
            {
                RayQualification = QualifiedGate(false),
                TransparentRayVariantAvailable =
                    transparentVariantAvailable,
                GeometryDecalCsmFallbackRequired = true,
                CsmDebugFallbackRequired = true
            });

        Assert.Multiple(() =>
        {
            Assert.That(plan.TransparentReceiverPolicy, Is.EqualTo(expectedPolicy));
            Assert.That(plan.CascadedReceiverFallbackRequired, Is.False);
            Assert.That(
                plan.RaySceneRequirement,
                Is.EqualTo(RaySceneConsumer.DirectionalContact));
        });
    }

    [Test]
    public void CreatePlan_UnqualifiedRayRemainsExperimentalAndEffective()
    {
        RenderSettings settings = CreateSettings(
            DirectionalShadowMode.RayQueryHard);
        var candidate = new ShadowFrameCandidate(
            DirectionalShadowMode.RayQueryHard,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan plan = planner.CreatePlan(
            CreatePlanInput(settings, candidate) with
            {
                RayQualification =
                    DirectionalShadowQualificationGateResult.Reject(
                        "qualification-missing")
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                plan.EffectiveMode,
                Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(
                plan.QualificationLevel,
                Is.EqualTo(DirectionalShadowQualificationLevel.Experimental));
            Assert.That(
                plan.QualificationDetail,
                Is.EqualTo("qualification-missing"));
        });
    }

    [Test]
    public void CreatePlan_DeveloperCsmTemporalPublishesHistoryAndDeveloperLabel()
    {
        RenderSettings settings = CreateSettings(
            DirectionalShadowMode.Cascaded);
        settings.Shadows.DirectionalCsmTemporalMode =
            DirectionalCsmTemporalMode.DeveloperForce;
        var candidate = new ShadowFrameCandidate(
            DirectionalShadowMode.Cascaded,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan plan = planner.CreatePlan(
            CreatePlanInput(settings, candidate) with
            {
                CsmTemporalActive = true,
                NearFieldResidualHistoryActive = true,
                ScreenResourceGeneration = 31u
            });

        Assert.Multiple(() =>
        {
            Assert.That(plan.UsesCsmTemporal, Is.True);
            Assert.That(
                plan.HistoryConsumers,
                Is.EqualTo(
                    SurfaceHistoryConsumer.DirectionalCsmTemporal |
                    SurfaceHistoryConsumer.NearFieldResidual));
            Assert.That(plan.UsesScreenHistory, Is.True);
            Assert.That(
                plan.DecalReceiverPolicy,
                Is.EqualTo(
                    DirectionalShadowReceiverPolicy.DecalDepthOwnerMask));
            Assert.That(
                plan.QualificationLevel,
                Is.EqualTo(DirectionalShadowQualificationLevel.Developer));
            Assert.That(
                plan.QualificationDetail,
                Is.EqualTo(
                    "directional-shadow-csm-temporal-developer-force"));
        });
    }

    [Test]
    public void CreatePlan_BaselineCsmUsesProductionSyntheticQualification()
    {
        RenderSettings settings = CreateSettings(
            DirectionalShadowMode.Cascaded);
        var candidate = new ShadowFrameCandidate(
            DirectionalShadowMode.Cascaded,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan plan = planner.CreatePlan(
            CreatePlanInput(settings, candidate) with
            {
                CascadeCount = -4
            });

        Assert.Multiple(() =>
        {
            Assert.That(plan.ActiveCascadeMask, Is.Zero);
            Assert.That(
                plan.QualificationLevel,
                Is.EqualTo(DirectionalShadowQualificationLevel.Production));
            Assert.That(
                plan.QualificationDetail,
                Is.EqualTo(
                    "directional-shadow-baseline-csm-does-not-require-manifest"));
            Assert.That(
                plan.OpaqueReceiverPolicy,
                Is.EqualTo(DirectionalShadowReceiverPolicy.Cascaded));
            Assert.That(
                plan.TransparentReceiverPolicy,
                Is.EqualTo(DirectionalShadowReceiverPolicy.Cascaded));
            Assert.That(
                plan.DecalReceiverPolicy,
                Is.EqualTo(DirectionalShadowReceiverPolicy.Cascaded));
        });
    }

    [Test]
    public void CreatePlan_AutoCsmTemporalRunsWithoutManifestAuthority()
    {
        RenderSettings settings = CreateSettings(
            DirectionalShadowMode.Cascaded);
        settings.Shadows.DirectionalCsmTemporalMode =
            DirectionalCsmTemporalMode.Auto;
        var candidate = new ShadowFrameCandidate(
            DirectionalShadowMode.Cascaded,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        DirectionalShadowQualificationGateResult qualification =
            QualifiedGate(
                csmTemporalApproved: true,
                gpuBudgetMicroseconds: 100.0,
                memoryBudgetBytes: 1024UL);
        DirectionalShadowRuntimeDiagnostics completed =
            DirectionalShadowRuntimeDiagnostics.Empty with
            {
                EffectiveMode = DirectionalShadowMode.Cascaded,
                QualificationLevel =
                    DirectionalShadowQualificationLevel.Production,
                GpuCsmMicroseconds = 100,
                HistoryBytes = 1024UL
            };
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan plan = default;
        for (ulong frame = 20UL; frame < 23UL; frame++)
        {
            plan = planner.CreatePlan(
                CreatePlanInput(settings, candidate) with
                {
                    CsmTemporalActive = true,
                    CsmTemporalQualification = qualification,
                    CompletedRuntime = completed,
                    FrameSerial = frame
                });
        }

        Assert.Multiple(() =>
        {
            Assert.That(plan.UsesCsmTemporal, Is.True);
            Assert.That(
                plan.FallbackReason,
                Is.EqualTo(DirectionalShadowFallbackReason.None));
            Assert.That(
                plan.QualificationLevel,
                Is.EqualTo(DirectionalShadowQualificationLevel.Production));
            Assert.That(plan.QualificationId, Is.Empty);
            Assert.That(plan.QualificationDetail, Is.EqualTo(
                "directional-shadow-csm-temporal-production-enabled"));
            Assert.That(plan.QualifiedGpuBudgetMicroseconds, Is.Zero);
            Assert.That(plan.QualifiedMemoryBudgetBytes, Is.Zero);
        });
    }

    [Test]
    public void CreatePlan_ThirdQualifiedRayOverrunDoesNotDisableProductionCsmTemporal()
    {
        RenderSettings raySettings = CreateSettings(
            DirectionalShadowMode.RayQueryHard);
        var rayCandidate = new ShadowFrameCandidate(
            DirectionalShadowMode.RayQueryHard,
            DirectionalShadowFallbackReason.None,
            string.Empty);
        DirectionalShadowQualificationGateResult qualification =
            QualifiedGate(
                csmTemporalApproved: true,
                gpuBudgetMicroseconds: 100.0,
                memoryBudgetBytes: 1024UL);
        DirectionalShadowRuntimeDiagnostics completedRay =
            DirectionalShadowRuntimeDiagnostics.Empty with
            {
                EffectiveMode = DirectionalShadowMode.RayQueryHard,
                QualificationLevel =
                    DirectionalShadowQualificationLevel.Production,
                GpuCsmMicroseconds = 40,
                GpuRayTraceMicroseconds = 70,
                RayMaskBytes = 1024UL,
                HistoryBytes = 1UL
            };
        var planner = new ShadowFramePlanner();

        DirectionalShadowFramePlan first = planner.CreatePlan(
            CreatePlanInput(raySettings, rayCandidate) with
            {
                RayQualification = qualification,
                CompletedRuntime = completedRay,
                FrameSerial = 10UL
            });
        DirectionalShadowFramePlan second = planner.CreatePlan(
            CreatePlanInput(raySettings, rayCandidate) with
            {
                RayQualification = qualification,
                CompletedRuntime = completedRay,
                FrameSerial = 11UL
            });
        DirectionalShadowFramePlan third = planner.CreatePlan(
            CreatePlanInput(raySettings, rayCandidate) with
            {
                RayQualification = qualification,
                CompletedRuntime = completedRay,
                FrameSerial = 12UL
            });

        RenderSettings csmSettings = CreateSettings(
            DirectionalShadowMode.Cascaded);
        csmSettings.Shadows.DirectionalCsmTemporalMode =
            DirectionalCsmTemporalMode.Auto;
        DirectionalShadowFramePlan crossTrackCooldown = planner.CreatePlan(
            CreatePlanInput(
                csmSettings,
                new ShadowFrameCandidate(
                    DirectionalShadowMode.Cascaded,
                    DirectionalShadowFallbackReason.None,
                    string.Empty)) with
            {
                CsmTemporalActive = true,
                CsmTemporalQualification = qualification,
                CompletedRuntime =
                    DirectionalShadowRuntimeDiagnostics.Empty with
                    {
                        EffectiveMode = DirectionalShadowMode.Cascaded,
                        QualificationLevel =
                            DirectionalShadowQualificationLevel.Production
                    },
                FrameSerial = 13UL
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                first.EffectiveMode,
                Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(
                second.EffectiveMode,
                Is.EqualTo(DirectionalShadowMode.RayQueryHard));
            Assert.That(
                third.EffectiveMode,
                Is.EqualTo(DirectionalShadowMode.Cascaded));
            Assert.That(
                third.FallbackReason,
                Is.EqualTo(
                    DirectionalShadowFallbackReason.GpuBudgetDemotion));
            Assert.That(
                third.FallbackDetail,
                Does.Contain("gpu=110us/100us"));
            Assert.That(crossTrackCooldown.UsesCsmTemporal, Is.True);
            Assert.That(
                crossTrackCooldown.FallbackReason,
                Is.EqualTo(DirectionalShadowFallbackReason.None));
            Assert.That(crossTrackCooldown.FallbackDetail, Is.Empty);
        });
    }

    private static ShadowFrameCandidateInput CreateCandidateInput(
        ShadowSettings settings) => new(
        settings,
        HasShadowCastingDirectionalLight: true,
        RayQuerySupported: true,
        RaySceneReadiness: CreateReadyRayScene(),
        RayMaskAvailable: true,
        SoftHistoryAvailable: true,
        TransparentRayReceiverRequired: false,
        TransparentRayVariantAvailable: true,
        SoftCollapsesToHard: false,
        UniversalCsmFallbackAvailable: true,
        RayResourceProviderPresent: true,
        RayResourceFailureDetail: string.Empty);

    private static ShadowFramePlanInput CreatePlanInput(
        RenderSettings settings,
        ShadowFrameCandidate candidate) => new(
        settings,
        candidate,
        CsmTemporalActive: false,
        CsmTemporalQualification:
            DirectionalShadowQualificationGateResult.Reject(
                "directional-shadow-csm-temporal-auto-not-requested"),
        RayQualification:
            DirectionalShadowQualificationGateResult.Reject(
                "directional-shadow-ray-mode-not-effective"),
        FrameSerial: 1UL,
        CompletedRuntime: DirectionalShadowRuntimeDiagnostics.Empty,
        CascadeCount: 3,
        StableLightIdentity: 1UL,
        NearFieldResidualHistoryActive: false,
        GeometryDecalCsmFallbackRequired: false,
        CsmDebugFallbackRequired: false,
        TransparentRayVariantAvailable: true,
        ScreenResourceGeneration: 9u,
        SunAngularRadiusRadians: 0.01f,
        RaySceneReadiness: CreateReadyRayScene());

    private static RenderSettings CreateSettings(
        DirectionalShadowMode requestedMode)
    {
        var settings = new RenderSettings();
        settings.AntiAliasing.Mode = AntiAliasingMode.SmaaHigh;
        settings.Shadows.RequestedDirectionalShadowMode = requestedMode;
        return settings;
    }

    private static RaySceneReadinessSnapshot CreateReadyRayScene() => new(
        RaySceneConsumer.DirectionalContact |
        RaySceneConsumer.DirectionalFull,
        RaySceneConsumer.DirectionalContact |
        RaySceneConsumer.DirectionalFull,
        RaySceneGeometryCategory.DirectionalShadowDefault,
        RaySceneGeometryCategory.DirectionalShadowDefault,
        ResourceGeneration: 7u,
        ContentEpoch: 11UL,
        FailureDetail: string.Empty)
    {
        CoverageMinimum = new(-100f, -100f, -100f),
        CoverageMaximum = new(100f, 100f, 100f),
        ExactCategories =
            RaySceneGeometryCategory.DirectionalShadowDefault
    };

    private static DirectionalShadowQualificationGateResult QualifiedGate(
        bool csmTemporalApproved,
        double gpuBudgetMicroseconds = 900.0,
        ulong memoryBudgetBytes = 4096UL) => new(
        Passed: true,
        Level: DirectionalShadowQualificationLevel.Production,
        QualificationId: "qualified-shadow",
        FailureDetail: string.Empty,
        MatchedDeviceRuleId: "device",
        MatchedTrackId: "track",
        CsmTemporalApproved: csmTemporalApproved,
        DirectionalShadowGpuBudgetMicroseconds: gpuBudgetMicroseconds,
        DirectionalShadowMemoryBudgetBytes: memoryBudgetBytes);
}
