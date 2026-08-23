using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiRoadmapExperimentTests
{
    private static readonly GiOpacityMicromapAssetFacts ExactStaticMask = new(
        StaticAlphaMask: true,
        StableUvs: true,
        CompleteResidentMipChain: true,
        ExactSamplerPolicy: true,
        ExactCutoffPolicy: true,
        ThinTransmissionAbsent: true,
        ProceduralMaskAbsent: true);

    [Test]
    public void B5_FailsClosedUntilL2IncidentRadianceExists()
    {
        GiExperimentAdmission admission =
            SimpleDdgiDirectionalFogExperiment.EvaluateAdmission(
                requested: true,
                new SimpleDdgiDirectionalFogCapabilities(
                    L2IncidentRadianceSidecarAvailable: false,
                    FroxelPhaseIntegrationAvailable: false,
                    DirectIndirectOwnershipSeparated: true));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Active, Is.False);
            Assert.That(admission.AllocatedBytes, Is.Zero);
            Assert.That(admission.Status,
                Is.EqualTo("l2-incident-radiance-sidecar-required"));
        });
    }

    [Test]
    public void B5_AdmitsQualifiedL2FroxelConsumerWithOwnedMemory()
    {
        const ulong allocatedBytes = 96UL * 1024UL * 1024UL;
        GiExperimentAdmission admission =
            SimpleDdgiDirectionalFogExperiment.EvaluateAdmission(
                requested: true,
                new SimpleDdgiDirectionalFogCapabilities(
                    L2IncidentRadianceSidecarAvailable: true,
                    FroxelPhaseIntegrationAvailable: true,
                    DirectIndirectOwnershipSeparated: true),
                productionQualified: true,
                allocatedBytes: allocatedBytes);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Active, Is.True);
            Assert.That(admission.Stage, Is.EqualTo(GiExperimentStage.Active));
            Assert.That(admission.AllocatedBytes, Is.EqualTo(allocatedBytes));
            Assert.That(admission.Status, Is.EqualTo("active"));
        });
    }

    [Test]
    public void B5_L2PhaseOracle_IsIsotropicAtGZeroAndDirectionalAtPositiveG()
    {
        float inverseY00 = 1.0f / 0.2820947918f;
        var incident = new SimpleDdgiL2IncidentRadiance(
            C0: new Vector3(inverseY00),
            C1: Vector3.Zero,
            C2: Vector3.Zero,
            C3: new Vector3(-1.0f, 0.0f, 0.0f),
            C4: Vector3.Zero,
            C5: Vector3.Zero,
            C6: Vector3.Zero,
            C7: Vector3.Zero,
            C8: Vector3.Zero);

        Vector3 isotropicPositive =
            SimpleDdgiDirectionalFogExperiment.EvaluateScatteredRadiance(
                incident, Vector3.UnitX, anisotropy: 0.0f);
        Vector3 isotropicNegative =
            SimpleDdgiDirectionalFogExperiment.EvaluateScatteredRadiance(
                incident, -Vector3.UnitX, anisotropy: 0.0f);
        Vector3 forward =
            SimpleDdgiDirectionalFogExperiment.EvaluateScatteredRadiance(
                incident, Vector3.UnitX, anisotropy: 0.8f);
        Vector3 backward =
            SimpleDdgiDirectionalFogExperiment.EvaluateScatteredRadiance(
                incident, -Vector3.UnitX, anisotropy: 0.8f);

        Assert.Multiple(() =>
        {
            Assert.That(isotropicPositive.X, Is.EqualTo(1.0f).Within(1.0e-5f));
            Assert.That(isotropicNegative.X, Is.EqualTo(1.0f).Within(1.0e-5f));
            Assert.That(forward.X, Is.GreaterThan(backward.X));
            Assert.That(forward.Y, Is.EqualTo(1.0f).Within(1.0e-5f));
            Assert.That(forward.Z, Is.EqualTo(1.0f).Within(1.0e-5f));
        });
    }

    [TestCase(0.0f, 0.49f, 0.5f, GiOpacityMicromapState.Transparent)]
    [TestCase(0.5f, 1.0f, 0.5f, GiOpacityMicromapState.Opaque)]
    [TestCase(0.49f, 0.5f, 0.5f, GiOpacityMicromapState.Unknown)]
    public void C1_ClassificationPreservesExactCutoffSemantics(
        float minimum,
        float maximum,
        float cutoff,
        GiOpacityMicromapState expected)
    {
        GiOpacityMicromapClassification classification =
            GiOpacityMicromapExperiment.ClassifyMicrotriangle(
                minimum,
                maximum,
                cutoff,
                ExactStaticMask);

        Assert.That(classification.State, Is.EqualTo(expected));
    }

    [Test]
    public void C1_IneligibleDynamicMaskAlwaysRetainsShaderConfirmation()
    {
        GiOpacityMicromapClassification result =
            GiOpacityMicromapExperiment.ClassifyMicrotriangle(
                1.0f,
                1.0f,
                0.5f,
                ExactStaticMask with { StaticAlphaMask = false });

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(GiOpacityMicromapState.Unknown));
            Assert.That(result.RequiresShaderConfirmation, Is.True);
            Assert.That(result.Reason,
                Is.EqualTo("animated-or-non-mask-alpha"));
        });
    }

    [Test]
    public void C1_CapabilityWithoutRuntimeBackendAllocatesNothing()
    {
        GiExperimentAdmission result =
            GiOpacityMicromapExperiment.EvaluateAdmission(
                requested: true,
                new GiOpacityMicromapHardwareCapabilities(
                    ExtensionAvailable: true,
                    FeatureAvailable: true,
                    HostCommandsAvailable: false,
                    MaximumTwoStateSubdivisionLevel: 12,
                    MaximumFourStateSubdivisionLevel: 12,
                    RuntimeBackendEnabled: false),
                default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stage,
                Is.EqualTo(GiExperimentStage.CapabilityAvailable));
            Assert.That(result.Active, Is.False);
            Assert.That(result.AllocatedBytes, Is.Zero);
        });
    }

    [Test]
    public void C2_SelectorPromotesExtReorderOnlyForQualifiedTotalGiWin()
    {
        var hardware = new GiRayTracingPipelineHardwareCapabilities(
            PipelineExtensionAvailable: true,
            PipelineFeatureAvailable: true,
            InvocationReorderExtensionAvailable: true,
            InvocationReorderFeatureAvailable: true,
            EffectiveReorderingHint: true,
            MaximumShaderBindingTableRecordIndex: 65_535,
            RuntimeBackendEnabled: true);
        GiRayTracingBackendSelection result =
            GiRayTracingInvocationReorderExperiment.Select(
                requested: true,
                hardware,
                Measurement(
                    GiRayTracingExperimentBackend.InlineRayQuery,
                    p95: 1_000.0,
                    mean: 800.0),
                Measurement(
                    GiRayTracingExperimentBackend.RayTracingPipeline,
                    p95: 940.0,
                    mean: 760.0),
                Measurement(
                    GiRayTracingExperimentBackend
                        .RayTracingPipelineWithInvocationReorder,
                    p95: 850.0,
                    mean: 700.0));

        Assert.Multiple(() =>
        {
            Assert.That(result.SelectedBackend, Is.EqualTo(
                GiRayTracingExperimentBackend
                    .RayTracingPipelineWithInvocationReorder));
            Assert.That(result.Admission.Active, Is.True);
            Assert.That(result.Admission.Status,
                Does.Contain("EXT-invocation-reorder"));
        });
    }

    [Test]
    public void C2_IneffectiveReorderingHintCanPromoteOnlyPlainPipeline()
    {
        var hardware = new GiRayTracingPipelineHardwareCapabilities(
            true, true, true, true,
            EffectiveReorderingHint: false,
            MaximumShaderBindingTableRecordIndex: 1_024,
            RuntimeBackendEnabled: true);
        GiRayTracingBackendSelection result =
            GiRayTracingInvocationReorderExperiment.Select(
                true,
                hardware,
                Measurement(GiRayTracingExperimentBackend.InlineRayQuery,
                    1_000.0, 800.0),
                Measurement(GiRayTracingExperimentBackend.RayTracingPipeline,
                    900.0, 700.0),
                Measurement(
                    GiRayTracingExperimentBackend
                        .RayTracingPipelineWithInvocationReorder,
                    600.0,
                    500.0));

        Assert.That(result.SelectedBackend,
            Is.EqualTo(GiRayTracingExperimentBackend.RayTracingPipeline));
    }

    [Test]
    public void C3_MixtureRetainsUniformSupportWhenGuidedPdfIsZero()
    {
        float pdf = SimpleDdgiDirectionalGuidingExperiment.MixturePdf(
            guidedPdf: 0.0f,
            uniformFraction: 0.0f);

        Assert.That(pdf, Is.EqualTo(
            SimpleDdgiDirectionalGuidingExperiment.MinimumUniformFraction *
            SimpleDdgiDirectionalGuidingExperiment.UniformSpherePdf)
            .Within(1.0e-8f));
    }

    [Test]
    public void C3_HistogramEstimatorIsUnbiasedForUnequalSolidAngles()
    {
        float[] energy = [1.0f, 8.0f, 2.0f];
        float[] solidAngles = [1.0f, 2.0f, 4.0f];
        float[] probabilities = new float[3];
        SimpleDdgiDirectionalGuidingExperiment.BuildHistogramMixture(
            energy,
            solidAngles,
            uniformFraction: 0.20f,
            probabilities);

        Vector3 expectation = Vector3.Zero;
        Vector3 exact = Vector3.Zero;
        for (int bin = 0; bin < energy.Length; bin++)
        {
            Vector3 integrand = new(energy[bin], energy[bin] * 0.5f, 1.0f);
            expectation += probabilities[bin] *
                SimpleDdgiDirectionalGuidingExperiment
                    .EstimateHistogramIntegralContribution(
                        integrand,
                        solidAngles[bin],
                        probabilities[bin]);
            exact += integrand * solidAngles[bin];
        }

        Assert.Multiple(() =>
        {
            Assert.That(probabilities.Sum(), Is.EqualTo(1.0f).Within(1.0e-6f));
            Assert.That(expectation.X, Is.EqualTo(exact.X).Within(1.0e-5f));
            Assert.That(expectation.Y, Is.EqualTo(exact.Y).Within(1.0e-5f));
            Assert.That(expectation.Z, Is.EqualTo(exact.Z).Within(1.0e-5f));
            Assert.That(probabilities, Has.All.GreaterThan(0.0f));
        });
    }

    [Test]
    public void C3_AdmissionRequiresDirectionAndAuditRedesign()
    {
        GiExperimentAdmission result =
            SimpleDdgiDirectionalGuidingExperiment.EvaluateAdmission(
                true,
                new SimpleDdgiDirectionalGuidingPrerequisites(
                    SpatialEmissiveSamplingReady: true,
                    CachedRelightingReady: true,
                    VariablePdfDirectionIdentityAvailable: false,
                    MaintenanceSubsetPdfAudited: false,
                    CacheCardinalityAndTailAuditUpdated: false,
                    ReferenceParityPassed: false,
                    QualityPerMillisecondImproved: false));

        Assert.That(result.Status,
            Is.EqualTo("variable-pdf-direction-identity-redesign-required"));
    }

    [Test]
    public void C4_DisabledOrUnqualifiedCacheOwnsNoMemory()
    {
        GiTaggedCausticCachePlan disabled =
            GiTaggedCausticCacheExperiment.CreatePlan(
                new GiTaggedCausticCacheConfiguration(
                    false, 1, 4_096, 4_096, 8, 1_000_000UL,
                    ScreenResolveProfile: new(64, 64)),
                default);
        GiTaggedCausticCachePlan diffuseFeedback =
            GiTaggedCausticCacheExperiment.CreatePlan(
                new GiTaggedCausticCacheConfiguration(
                    true, 1, 4_096, 4_096, 8, 1_000_000UL,
                    ScreenResolveProfile: new(64, 64)),
                new GiTaggedCausticCacheQualification(
                    SeparateOwnershipImplemented: true,
                    DiffuseTransportFeedDisabled: false,
                    ReferenceParityPassed: true,
                    StabilityProofPassed: true,
                    QualityPerMillisecondImproved: true));

        Assert.Multiple(() =>
        {
            Assert.That(disabled.AllocatedBytes, Is.Zero);
            Assert.That(diffuseFeedback.AllocatedBytes, Is.Zero);
            Assert.That(diffuseFeedback.Status,
                Is.EqualTo("diffuse-ddgi-feedback-forbidden"));
        });
    }

    [Test]
    public void C4_ExplicitSelectionBuildsBoundedPlanWithoutEvidenceFiles()
    {
        var configuration = new GiTaggedCausticCacheConfiguration(
            true,
            HeroMaterialCount: 1,
            PhotonTaskCapacity: 1_024,
            MaximumWorldCells: 1_024,
            MaximumPhotonsPerCell: 8,
            MemoryBudgetBytes: 8UL * 1024UL * 1024UL,
            ScreenResolveProfile: new(64, 64));

        GiTaggedCausticCachePlan plan =
            GiTaggedCausticCacheExperiment.CreateExplicitPlan(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.True, plan.Status);
            Assert.That(plan.Status, Is.EqualTo("active-explicit-selection"));
            Assert.That(plan.AllocatedBytes, Is.GreaterThan(0UL));
            Assert.That(plan.EvidenceValidation.Accepted, Is.True);
            Assert.That(plan.EvidenceValidation.EvidenceId, Is.Empty);
            Assert.That(plan.EvidenceValidation.BindingFingerprint,
                Is.Not.Zero);
        });
    }

    [Test]
    public void C4_DefaultInteractiveExplicitLayoutFitsIndependentBudget()
    {
        var configuration = new GiTaggedCausticCacheConfiguration(
            true,
            HeroMaterialCount: 1,
            PhotonTaskCapacity: 4_096,
            MaximumWorldCells: 4_096,
            MaximumPhotonsPerCell: 8,
            MemoryBudgetBytes: 96UL * 1024UL * 1024UL,
            ScreenResolveProfile: new(1_600, 900));

        GiTaggedCausticCachePlan plan =
            GiTaggedCausticCacheExperiment.CreateExplicitPlan(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.True, plan.Status);
            Assert.That(plan.AllocatedBytes,
                Is.LessThanOrEqualTo(configuration.MemoryBudgetBytes));
            Assert.That(plan.GpuLayout.ScreenResolve.Width, Is.EqualTo(1_600));
            Assert.That(plan.GpuLayout.ScreenResolve.Height, Is.EqualTo(900));
        });
    }

    [Test]
    public void C4_QualifiedTaggedCompositeDoesNotClampAgainstDiffuseBaseline()
    {
        var configuration = new GiTaggedCausticCacheConfiguration(
            true, 2, 1_024, 1_024, 8, 1UL * 1024UL * 1024UL,
            ScreenResolveProfile: new(64, 64));
        GiCausticAdmissionContext context = CausticAdmissionContext();
        GiCausticQualificationEvidence evidence = QualifiedCausticEvidence(
            configuration, context);
        GiTaggedCausticCachePlan plan =
            GiTaggedCausticCacheExperiment.CreatePlan(
                configuration,
                new GiTaggedCausticCacheQualification(
                    true, true, true, true, true),
                evidence,
                context);
        Vector3 untagged =
            GiTaggedCausticCacheExperiment.CompositeTaggedContribution(
                Vector3.One,
                new Vector3(10.0f),
                GiCausticPathTag.None);
        Vector3 tagged =
            GiTaggedCausticCacheExperiment.CompositeTaggedContribution(
                Vector3.One,
                new Vector3(10.0f),
                GiCausticPathTag.RefractiveToDiffuse);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.True);
            Assert.That(plan.AllocatedBytes, Is.GreaterThan(0UL));
            Assert.That(plan.AllocatedBytes, Is.EqualTo(plan.GpuLayout.TotalBytes));
            Assert.That(plan.Memory.AllocatedBytes, Is.EqualTo(plan.AllocatedBytes));
            Assert.That(plan.Layout.WriteBankCount, Is.EqualTo(2));
            Assert.That(untagged, Is.EqualTo(Vector3.One));
            Assert.That(tagged, Is.EqualTo(new Vector3(11.0f)));
        });
    }

    [Test]
    public void C4_LegacyBooleanQualificationAndStaleEvidenceFailClosed()
    {
        var configuration = new GiTaggedCausticCacheConfiguration(
            true, 1, 256, 128, 4, 1UL * 1024UL * 1024UL,
            ScreenResolveProfile: new(64, 64));
        var technical = new GiTaggedCausticCacheQualification(
            true, true, true, true, true);
        GiTaggedCausticCachePlan legacy =
            GiTaggedCausticCacheExperiment.CreatePlan(configuration, technical);
        GiCausticAdmissionContext context = CausticAdmissionContext();
        GiCausticQualificationEvidence evidence = QualifiedCausticEvidence(
            configuration, context);
        GiTaggedCausticCachePlan stale =
            GiTaggedCausticCacheExperiment.CreatePlan(
                configuration,
                technical,
                evidence,
                context with { ContentRevision = context.ContentRevision + 1UL });

        Assert.Multiple(() =>
        {
            Assert.That(legacy.Active, Is.False);
            Assert.That(legacy.AllocatedBytes, Is.Zero);
            Assert.That(legacy.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.EvidenceMissing));
            Assert.That(stale.Active, Is.False);
            Assert.That(stale.AllocatedBytes, Is.Zero);
            Assert.That(stale.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.EvidenceBindingMismatch));
            Assert.That(stale.Memory.CausticPhotonRecords.IsZero, Is.True);
            Assert.That(stale.Memory.CausticCellTableAndSortScratch.IsZero, Is.True);
            Assert.That(stale.Memory.CausticHistory.IsZero, Is.True);
        });
    }

    [Test]
    public void C5_AllocatesNothingBeforeB3Qualification()
    {
        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                ResidualConfiguration(enabled: true),
                default);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.AllocatedBytes, Is.Zero);
            Assert.That(plan.Memory.NearFieldTraceTargets.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldHistoryAndMoments.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldFilterScratch.IsZero, Is.True);
            Assert.That(plan.Status,
                Is.EqualTo("B3-refinement-qualification-required"));
        });
    }

    [Test]
    public void C5_DisabledDefaultRemainsOffEvenWhenAFormerLegacySourceDeclarationIsIncomplete()
    {
        SimpleDdgiNearFieldResidualConfiguration disabled = new(
            Enabled: false,
            Width: 1_920,
            Height: 1_080,
            MemoryBudgetBytes: 256UL * 1024UL * 1024UL,
            Profile: SimpleDdgiNearFieldResidualProfile.HalfResolutionReference,
            // This legacy terms/ABI-only form intentionally fails the new
            // source contract, but disabled mode must remain a zero-allocation
            // no-op rather than treating contract hardening as an opt-in.
            SourceContract: new SimpleDdgiNearFieldTraceSourceContract(
                SimpleDdgiNearFieldTraceSourceTerm.DirectDiffuse |
                SimpleDdgiNearFieldTraceSourceTerm.Emissive,
                AbiRevision: 1u));
        var fullyImplementedPrerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);

        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                disabled,
                fullyImplementedPrerequisites);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Requested, Is.False);
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.Status, Is.EqualTo("disabled"));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void C5_ExplicitSelectionRequiresTechnicalContractsButNotPromotionEvidence()
    {
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        var technicalPrerequisites =
            new SimpleDdgiNearFieldResidualPrerequisites(
                RefinementBricksActive: false,
                RefinementQualityGatePassed: false,
                RemainingContactScaleErrorMeasured: false,
                SourceOwnershipImplemented: true,
                DisocclusionRejectionImplemented: true,
                CameraAndScreenEdgeStabilityPassed: false,
                ReferenceErrorPerMillisecondImproved: false,
                NoDoubleCountingOrFalseDarkening: false);

        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreateExplicitPlan(
                configuration,
                technicalPrerequisites);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.True, plan.Status);
            Assert.That(plan.Status, Is.EqualTo("active-explicit-selection"));
            Assert.That(plan.AllocatedBytes, Is.GreaterThan(0UL));
            Assert.That(plan.EvidenceValidation.Accepted, Is.True);
            Assert.That(plan.EvidenceId, Is.Empty);
            Assert.That(plan.EvidenceBindingFingerprint, Is.Not.Zero);
        });
    }

    [Test]
    public void C5_QualifiedPlanIsBoundedAndResidualRejectsInvalidHistory()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                prerequisites,
                QualifiedResidualEvidence(configuration, context),
                context);
        Vector3 valid =
            SimpleDdgiNearFieldResidualExperiment.EvaluateHighFrequencyResidual(
                new Vector3(3.0f, 2.0f, 1.0f),
                Vector3.One,
                new SimpleDdgiNearFieldResidualValidation(1, 1, 1, 1, 1));
        Vector3 invalid =
            SimpleDdgiNearFieldResidualExperiment.EvaluateHighFrequencyResidual(
                new Vector3(3.0f, 2.0f, 1.0f),
                Vector3.One,
                new SimpleDdgiNearFieldResidualValidation(1, 1, 0, 1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.True);
            Assert.That(plan.Width, Is.EqualTo(480));
            Assert.That(plan.Height, Is.EqualTo(270));
            Assert.That(plan.AllocatedBytes, Is.GreaterThan(0UL));
            Assert.That(plan.Layout.TraceSourceBytes, Is.GreaterThan(0UL));
            Assert.That(plan.Layout.HitMetadataBytes, Is.Zero,
                "V12 trace writes directly into the current 48-byte history bank.");
            Assert.That(plan.TraceBytes, Is.EqualTo(
                plan.Memory.NearFieldTraceTargets.AllocatedBytes));
            Assert.That(plan.HistoryBytes, Is.EqualTo(
                plan.Memory.NearFieldHistoryAndMoments.AllocatedBytes));
            Assert.That(plan.AllocatedBytes, Is.EqualTo(plan.Memory.AllocatedBytes));
            Assert.That(plan.TraceBytes, Is.EqualTo(
                plan.Layout.TraceSourceBytes +
                plan.Layout.ReceiverPayloadBytes +
                plan.Layout.TraceFrameConstantsBytes +
                plan.Layout.PreparedDepthFootprintBytes +
                plan.Layout.PreparedReceiverPayloadBytes +
                plan.Layout.PreparedMotionBytes +
                plan.Layout.SourceLuminanceBytes +
                plan.Layout.RawCandidateBytes +
                plan.Layout.SurfaceTableBytes +
                plan.Layout.ActiveTileAndIndirectBytes +
                plan.Layout.TileBuffersBytes +
                plan.Layout.TelemetryReadbackBytes));
            Assert.That(plan.HistoryBytes, Is.EqualTo(
                plan.Layout.HistoryRadianceBytes +
                plan.Layout.MomentBytes +
                plan.Layout.HistoryValidityBytes +
                plan.Layout.HistoryMetadataBytes +
                plan.Layout.HistoryNormalBytes));
            Assert.That(plan.FilterScratchBytes, Is.EqualTo(
                plan.Layout.FilterScratchBytes));
            Assert.That(plan.EvidenceValidation.Accepted, Is.True);
            Assert.That(plan.EvidenceId,
                Is.EqualTo("c5-post-b3-reference-20260810"));
            Assert.That(plan.EvidenceBindingFingerprint, Is.Not.EqualTo(0UL));
            Assert.That(valid, Is.EqualTo(new Vector3(2.0f, 1.0f, 0.0f)));
            Assert.That(invalid, Is.EqualTo(Vector3.Zero));
        });
    }

    [Test]
    public void C5_RuntimeIdentityInvalidationPreservesIntentButReturnsExactZeroMemory()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualPlan active =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                prerequisites,
                QualifiedResidualEvidence(configuration, context),
                context);

        SimpleDdgiNearFieldResidualPlan invalidated =
            SimpleDdgiNearFieldResidualExperiment.InvalidateRuntimePlan(
                active,
                "near-field-resize-requires-new-bound-evidence",
                GiExperimentFallbackReason.EvidenceBindingMismatch);

        Assert.Multiple(() =>
        {
            Assert.That(active.Active, Is.True);
            Assert.That(invalidated.Requested, Is.True);
            Assert.That(invalidated.Active, Is.False);
            Assert.That(invalidated.AllocatedBytes, Is.Zero);
            Assert.That(invalidated.Layout.IsValid, Is.False);
            Assert.That(invalidated.Memory.AllCategoriesZero, Is.True);
            Assert.That(invalidated.Admission.AllocatedBytes, Is.Zero);
            Assert.That(invalidated.EvidenceValidation.Accepted, Is.False);
            Assert.That(invalidated.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.EvidenceBindingMismatch));
            Assert.That(invalidated.EvidenceId, Is.EqualTo(active.EvidenceId));
        });
    }

    [Test]
    public void C5_LegacyBooleanPrerequisitesCannotAllocateWithoutBoundEvidence()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                ResidualConfiguration(enabled: true),
                prerequisites);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.AllocatedBytes, Is.Zero);
            Assert.That(plan.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.EvidenceMissing));
            Assert.That(plan.Memory.NearFieldTraceTargets.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldHistoryAndMoments.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldFilterScratch.IsZero, Is.True);
        });
    }

    [Test]
    public void C5_IndependentMemoryRejectionReturnsZeroC5Categories()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration constrained =
            ResidualConfiguration(enabled: true) with { MemoryBudgetBytes = 1UL };

        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                constrained,
                prerequisites);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.Status,
                Is.EqualTo("independent-near-field-memory-budget"));
            Assert.That(plan.Memory.NearFieldTraceTargets.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldHistoryAndMoments.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldFilterScratch.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldTraceTargets.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.IndependentMemoryBudgetExceeded));
        });
    }

    [Test]
    public void C5_StaleSourceOrLayoutEvidenceIsRejectedWithZeroCentralMemory()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualQualificationEvidence evidence =
            QualifiedResidualEvidence(configuration, context);
        evidence = evidence with
        {
            Binding = evidence.Binding with
            {
                TraceSourceContract = evidence.Binding.TraceSourceContract with
                {
                    LayoutRevision = 99u
                }
            }
        };

        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                prerequisites,
                evidence,
                context);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.EvidenceBindingMismatch));
            Assert.That(plan.Status,
                Is.EqualTo("near-field-evidence-source-profile-or-layout-binding-mismatch"));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void C5_EvidenceCannotCrossDeviceContentOrB3Qualification()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext original = ResidualContext();
        SimpleDdgiNearFieldResidualQualificationEvidence evidence =
            QualifiedResidualEvidence(configuration, original);
        SimpleDdgiNearFieldResidualAdmissionContext[] staleContexts =
        {
            original with { DeviceQualificationKey = "1002-1636-driver-current-c5-v1" },
            original with { ContentRevision = 2UL },
            original with { B3QualificationId = "b3-qualified-reference-v2" },
            original with { B3QualificationRevision = 2u }
        };

        foreach (SimpleDdgiNearFieldResidualAdmissionContext staleContext in staleContexts)
        {
            SimpleDdgiNearFieldResidualPlan plan =
                SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                    configuration,
                    prerequisites,
                    evidence,
                    staleContext);
            Assert.Multiple(() =>
            {
                Assert.That(plan.Active, Is.False);
                Assert.That(plan.EvidenceValidation.FallbackReason,
                    Is.EqualTo(GiExperimentFallbackReason.EvidenceBindingMismatch));
                Assert.That(plan.Status, Is.EqualTo(
                    "near-field-evidence-device-content-or-B3-binding-mismatch"));
                Assert.That(plan.Memory.AllCategoriesZero, Is.True);
            });
        }
    }

    [Test]
    public void C5_EvaluatedNoGoEvidenceLeavesEveryCategoryAtZero()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualQualificationEvidence evidence =
            QualifiedResidualEvidence(configuration, context);
        evidence = evidence with
        {
            Measurement = evidence.Measurement with
            {
                EqualCostAdditionalB3Error = 6.5
            }
        };

        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                prerequisites,
                evidence,
                context);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.Status,
                Is.EqualTo("equal-cost-B3-is-at-least-as-effective"));
            Assert.That(plan.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.QualificationNotPassed));
            Assert.That(plan.Memory.NearFieldTraceTargets.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldHistoryAndMoments.IsZero, Is.True);
            Assert.That(plan.Memory.NearFieldFilterScratch.IsZero, Is.True);
        });
    }

    [Test]
    public void C5_EvidenceAboveProductionP95BudgetIsRejectedWithZeroMemory()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualQualificationEvidence evidence =
            QualifiedResidualEvidence(configuration, context) with
            {
                C5P95Milliseconds =
                    SimpleDdgiNearFieldResidualEvidenceAbi
                        .MaximumProductionP95Milliseconds + 0.001
            };

        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                prerequisites,
                evidence,
                context);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Active, Is.False);
            Assert.That(plan.Status,
                Is.EqualTo("near-field-evidence-P95-GPU-budget-exceeded"));
            Assert.That(plan.EvidenceValidation.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.QualificationNotPassed));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void C5_EvidenceRequiresSixtyMinuteStableBoundedTraversal()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualQualificationEvidence baseline =
            QualifiedResidualEvidence(configuration, context);
        SimpleDdgiNearFieldResidualQualificationEvidence[] invalid =
        [
            baseline with
            {
                LongRunTraversalMinutes =
                    SimpleDdgiNearFieldResidualEvidenceAbi
                        .MinimumLongRunTraversalMinutes - 1u
            },
            baseline with
            {
                PeakSteadyMemoryBytes =
                    SimpleDdgiNearFieldResidualEvidenceAbi
                        .MaximumSteadyMemoryBytes + 1UL
            },
            baseline with { NoRetirementGrowthVerified = false },
            baseline with { NoCounterOverflowVerified = false },
            baseline with { NoNonFiniteOutputVerified = false }
        ];

        foreach (SimpleDdgiNearFieldResidualQualificationEvidence evidence in
                 invalid)
        {
            SimpleDdgiNearFieldResidualPlan plan =
                SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                    configuration,
                    prerequisites,
                    evidence,
                    context);
            Assert.Multiple(() =>
            {
                Assert.That(plan.Active, Is.False);
                Assert.That(plan.AllocatedBytes, Is.Zero);
                Assert.That(plan.EvidenceValidation.FallbackReason,
                    Is.EqualTo(
                        GiExperimentFallbackReason.QualificationNotPassed));
                Assert.That(plan.Status, Does.StartWith("near-field-long-run-"));
            });
        }
    }

    [Test]
    public void C5_ActivePlanAttachesItsExactCategoriesToCentralContentMemory()
    {
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            true, true, true, true, true, true, true, true);
        SimpleDdgiNearFieldResidualConfiguration configuration =
            ResidualConfiguration(enabled: true);
        SimpleDdgiNearFieldResidualAdmissionContext context = ResidualContext();
        SimpleDdgiNearFieldResidualPlan nearField =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                prerequisites,
                QualifiedResidualEvidence(configuration, context),
                context);
        // A memory-plan value can cross an ABI boundary as default(T); C5
        // attachment must normalize that fixed-shape zero value safely.
        SimpleDdgiContentMemoryPlan content =
            default(SimpleDdgiContentMemoryPlan).WithNearFieldResidual(nearField);

        Assert.Multiple(() =>
        {
            Assert.That(nearField.Active, Is.True);
            Assert.That(content.AdvancedExperimentMemory.NearFieldTraceTargets,
                Is.EqualTo(nearField.Memory.NearFieldTraceTargets));
            Assert.That(content.AdvancedExperimentMemory.NearFieldHistoryAndMoments,
                Is.EqualTo(nearField.Memory.NearFieldHistoryAndMoments));
            Assert.That(content.AdvancedExperimentMemory.NearFieldFilterScratch,
                Is.EqualTo(nearField.Memory.NearFieldFilterScratch));
            Assert.That(content.PersistentBytes,
                Is.EqualTo(nearField.HistoryBytes));
            Assert.That(content.WorkBytes, Is.EqualTo(
                nearField.TraceBytes +
                nearField.Memory.NearFieldFilterScratch.PeakLiveBytes));
        });
    }

    [Test]
    public void ExperimentSettingsRoundTripAndProductionDefaults()
    {
        var defaults = new RenderSettings();
        Assert.Multiple(() =>
        {
            Assert.That(defaults.GlobalIllumination
                .SimpleDdgiDirectionalFogEnabled, Is.True);
            Assert.That(defaults.GlobalIllumination
                .DdgiOpacityMicromapExperimentEnabled, Is.True);
            Assert.That(defaults.GlobalIllumination
                .DdgiRayTracingPipelineExperimentEnabled, Is.False);
            Assert.That(defaults.GlobalIllumination
                .SimpleDdgiDirectionalRayGuidingExperimentEnabled, Is.True);
            Assert.That(defaults.GlobalIllumination
                .DdgiTaggedCausticCacheExperimentEnabled, Is.True);
            Assert.That(defaults.GlobalIllumination
                .SimpleDdgiNearFieldResidualExperimentEnabled, Is.True);
        });

        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"gi-roadmap-experiments-{Guid.NewGuid():N}.json");
        try
        {
            GlobalIlluminationSettings source = defaults.GlobalIllumination;
            source.SimpleDdgiDirectionalFogEnabled = true;
            source.DdgiOpacityMicromapExperimentEnabled = true;
            source.DdgiRayTracingPipelineExperimentEnabled = true;
            source.SimpleDdgiDirectionalRayGuidingExperimentEnabled = true;
            source.DdgiTaggedCausticCacheExperimentEnabled = true;
            source.SimpleDdgiNearFieldResidualExperimentEnabled = true;
            defaults.Save(path);

            GlobalIlluminationSettings loaded =
                RenderSettings.Load(path).GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiDirectionalFogEnabled, Is.True);
                Assert.That(loaded.DdgiOpacityMicromapExperimentEnabled, Is.True);
                Assert.That(loaded.DdgiRayTracingPipelineExperimentEnabled,
                    Is.True);
                Assert.That(loaded
                    .SimpleDdgiDirectionalRayGuidingExperimentEnabled, Is.True);
                Assert.That(loaded.DdgiTaggedCausticCacheExperimentEnabled,
                    Is.True);
                Assert.That(loaded.SimpleDdgiNearFieldResidualExperimentEnabled,
                    Is.True);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void HardwareQueryUsesRatifiedExtInterfaceAndNotNvSer()
    {
        string context = ReadRepoText(
            "Njulf.Rendering", "Core", "VulkanContext.cs");

        Assert.Multiple(() =>
        {
            Assert.That(context, Does.Contain("VK_EXT_opacity_micromap"));
            Assert.That(context, Does.Contain(
                "VK_EXT_ray_tracing_invocation_reorder"));
            Assert.That(context, Does.Contain(
                "PhysicalDeviceRayTracingInvocationReorderPropertiesEXT"));
            Assert.That(context, Does.Not.Contain(
                "VK_NV_ray_tracing_invocation_reorder"));
        });
    }

    private static GiCausticAdmissionContext CausticAdmissionContext() => new(
        DeviceQualificationKey: "test-device-driver-toolchain",
        CorpusId: "c4-analytic-reference-corpus-v1",
        ContentRevision: 11UL,
        LightDistributionRevision: 12UL,
        EmissiveDistributionRevision: 13UL,
        HeroSourceRevision: 14UL,
        CurrentPoseTlasSignature: 15UL,
        ShaderBundleHash: "sha256:test-c4-shader-bundle");

    private static GiCausticQualificationEvidence QualifiedCausticEvidence(
        in GiTaggedCausticCacheConfiguration configuration,
        in GiCausticAdmissionContext context)
    {
        GiCausticCacheLayout cache = GiCausticCacheLayoutCompiler.Compile(
            configuration.PhotonTaskCapacity,
            configuration.MaximumPhotonsPerCell,
            configuration.MaximumWorldCells,
            configuration.RecordStride,
            writeBankCount: 2,
            configuration.CacheBankCount,
            configuration.TargetLoadFactor,
            historyBytes: 0UL,
            configuration.MemoryBudgetBytes);
        Assert.That(cache.IsValid, Is.True, cache.FailureReason);
        GiCausticGpuResourceLayout gpu =
            GiCausticGpuResourceLayoutCompiler.Compile(new(
                cache,
                configuration.MemoryBudgetBytes,
                configuration.MaximumStorageBufferRange,
                configuration.MaximumEmitterCount,
                configuration.MaximumHeroCount,
                configuration.MaximumProposalPairCount,
                configuration.ScreenResolveProfile));
        Assert.That(gpu.IsValid, Is.True, gpu.FailureReason);
        GiCausticEvidenceBinding binding = GiCausticEvidenceBinding.Create(
            context, configuration, gpu);
        return new GiCausticQualificationEvidence(
            "c4-qualified-test-evidence",
            binding,
            new GiCausticQualificationMeasurement(
                context.CorpusId,
                context.ContentRevision,
                C4OffMaskedReferenceError: 1.0,
                C4MaskedReferenceError: 0.5,
                RelativeEmittedToResolvedEnergyError: 0.01,
                AddedGpuMilliseconds: 1.0,
                P95TotalGpuMilliseconds: 1.2,
                P99TotalGpuMilliseconds: 1.5,
                PeakLiveMemoryBytes: gpu.TotalBytes),
            ReferenceFrameCount: 240u,
            IndependentRunCount: 5u,
            CpuGpuPdfAndThroughputParity: true,
            MirrorAndDielectricEnergyConservation: true,
            DifferentialReferencePassed: true,
            BottomKUnbiasednessPassed: true,
            DarkReceiverReferencePassed: true,
            OwnershipIsolationPassed: true,
            PublicationAndMotionStabilityPassed: true,
            WholeFrameRegressionPassed: true,
            QualityPerMillisecondImproved: true,
            ZeroWorkFallbackPassed: true);
    }

    private static GiRayTracingBackendMeasurement Measurement(
        GiRayTracingExperimentBackend backend,
        double p95,
        double mean) => new(
            backend,
            SameRayParityPassed: true,
            AlphaAndTransmissionParityPassed: true,
            FarFieldParityPassed: true,
            PrewarmedWithoutFirstUseCreation: true,
            P95TotalGiMicroseconds: p95,
            MeanTotalGiMicroseconds: mean,
            ResidentBytes: 1_024UL);

    private static SimpleDdgiNearFieldResidualConfiguration
        ResidualConfiguration(bool enabled)
    {
        SimpleDdgiNearFieldResidualProfile profile =
            SimpleDdgiNearFieldResidualProfile.ForPreset(
                SimpleDdgiNearFieldResidualQualityPreset.Balanced,
                0.25f);
        const int width = 1_920;
        const int height = 1_080;
        const ulong budgetBytes = 96UL * 1024UL * 1024UL;
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                width, height, profile, budgetBytes);
        Assert.That(layout.IsValid, Is.True, layout.FailureReason);

        return new SimpleDdgiNearFieldResidualConfiguration(
            enabled,
            Width: width,
            Height: height,
            // The qualified fixture keeps the complete double-buffered hit
            // identity/normal history inside the production 96 MiB envelope.
            MemoryBudgetBytes: budgetBytes,
            Profile: profile,
            SourceContract: SimpleDdgiNearFieldTraceSourceContract
                .CreatePreDdgiDirectDiffuseAndEmissive(
                    layout,
                    profile,
                    abiRevision: 1u,
                    layoutRevision: 1u,
                    sourceRevision: 1u));
    }

    private static SimpleDdgiNearFieldResidualAdmissionContext ResidualContext() => new(
        DeviceQualificationKey: "10de-2520-driver-610.62-c5-v2",
        CorpusId: "hands-and-crease-reference-v1",
        ContentRevision: 1UL,
        B3QualificationId: "b3-qualified-reference-v1",
        B3QualificationRevision: 1u)
    {
        ShaderSetHash = "c5-v12-test-shader-set",
        VendorId = 0x10deu,
        DeviceId = 0x2520u,
        DriverVersion = 61062u,
        ApiVersion = 0x00403000u
    };

    private static SimpleDdgiNearFieldResidualQualificationEvidence
        QualifiedResidualEvidence(
            in SimpleDdgiNearFieldResidualConfiguration configuration,
            in SimpleDdgiNearFieldResidualAdmissionContext context)
    {
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                configuration.Width,
                configuration.Height,
                configuration.Profile,
                configuration.MemoryBudgetBytes);
        return new SimpleDdgiNearFieldResidualQualificationEvidence(
            EvidenceId: "c5-post-b3-reference-20260810",
            Binding: SimpleDdgiNearFieldResidualEvidenceBinding.Create(
                context,
                configuration,
                layout),
            Measurement: new SimpleDdgiNearFieldResidualMeasurement(
                CorpusId: context.CorpusId,
                ContentRevision: context.ContentRevision,
                B3QualificationRevision: context.B3QualificationRevision,
                PostB3NearFieldError: 10.0,
                C5OracleError: 7.0,
                EqualCostAdditionalB3Error: 8.5,
                ErrorIsScreenLocal: true,
                ErrorIsObservableByShortDepthRay: true,
                RootCauseIsNotDdgiLivenessOrAlpha: true,
                UsesSceneLinearReference: true),
            ReferenceSequenceCount:
                SimpleDdgiNearFieldResidualEvidenceAbi.MinimumReferenceSequenceCount,
            ReferenceFrameCount:
                SimpleDdgiNearFieldResidualEvidenceAbi.MinimumReferenceFrameCount,
            IndependentRunCount:
                SimpleDdgiNearFieldResidualEvidenceAbi.MinimumIndependentRunCount,
            C5AddedMilliseconds: 0.60,
            C5P95Milliseconds: 0.70,
            EqualCostAdditionalB3Milliseconds: 0.62,
            B3ConvergenceVerified: true,
            CpuOrImageSpaceOracleVerified: true,
            TraceSourceIndependenceVerified: true,
            TemporalStabilityVerified: true,
            SignedResidualEnergyVerified: true,
            WholeFrameRegressionVerified: true)
        {
            C5P99Milliseconds = 0.90,
            SourceMrtCostUpperBoundMilliseconds = 0.05,
            SourceCostAuthoritative = true,
            AbsoluteSignedNetResidualEnergyFraction = 0.005,
            LowFrequencyLeakageFraction = 0.01,
            BenchmarkCaptureId = "c5-v2-test-capture",
            ReferenceManifestId = "c5-v2-test-reference-manifest",
            LongRunTraversalMinutes =
                SimpleDdgiNearFieldResidualEvidenceAbi
                    .MinimumLongRunTraversalMinutes,
            PeakSteadyMemoryBytes = layout.TotalBytes,
            PeakHotSwapMemoryBytes = checked(layout.TotalBytes * 2UL),
            StableMemoryVerified = true,
            NoRetirementGrowthVerified = true,
            NoCounterOverflowVerified = true,
            NoNonFiniteOutputVerified = true
        };
    }

    private static string ReadRepoText(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                Path.Combine(relativeParts));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeParts)} from the test output directory.");
    }
}
