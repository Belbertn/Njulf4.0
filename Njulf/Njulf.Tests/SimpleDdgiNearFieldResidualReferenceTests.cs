using System;
using System.Numerics;
using Njulf.Rendering;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualReferenceTests
{
    [Test]
    public void TraceSource_FreezesDirectSceneLinearProducerFormatCoverageAndScaledExtent()
    {
        SimpleDdgiNearFieldResidualProfile profile =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference;
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                1_920, 1_080, profile, 256UL * 1024UL * 1024UL);
        var valid = SimpleDdgiNearFieldTraceSourceContract
            .CreatePreDdgiDirectDiffuseAndEmissive(
                layout,
                profile,
                abiRevision: 3u,
                layoutRevision: 5u,
                sourceRevision: 7u);
        var invalid = valid with
        {
            Terms = valid.Terms | SimpleDdgiNearFieldTraceSourceTerm.DdgiIndirect
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.IsValid, Is.True);
            Assert.That(valid.TryValidateForLayout(layout, out _), Is.True);
            Assert.That(valid.Format, Is.EqualTo(profile.SourceFormat));
            Assert.That(valid.Extent.ResolutionScale,
                Is.EqualTo(layout.TraceResolutionScale));
            Assert.That(valid.Extent.ScaledWidth, Is.EqualTo(960));
            Assert.That(valid.Extent.ScaledHeight, Is.EqualTo(540));
            Assert.That(invalid.IsValid, Is.False);
            Assert.That(invalid.FailureReason,
                Is.EqualTo("trace-source-must-contain-only-direct-diffuse-and-emissive"));
        });
    }

    [Test]
    public void TraceSource_FailsClosedForSemanticOrLayoutMismatch()
    {
        SimpleDdgiNearFieldResidualProfile profile =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference;
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                640, 360, profile, 256UL * 1024UL * 1024UL);
        SimpleDdgiNearFieldTraceSourceContract valid =
            SimpleDdgiNearFieldTraceSourceContract
                .CreatePreDdgiDirectDiffuseAndEmissive(layout, profile);

        SimpleDdgiNearFieldTraceSourceContract[] invalidContracts =
        {
            valid with
            {
                ColorSpace = SimpleDdgiNearFieldTraceSourceColorSpace.DisplayEncoded
            },
            valid with
            {
                Producer = SimpleDdgiNearFieldTraceSourceProducer.FinalSceneColor
            },
            valid with
            {
                AlphaCoverage = SimpleDdgiNearFieldTraceSourceAlphaCoverageSemantics
                    .IncludesBlendedFogOrTransmission
            },
            valid with { LayoutRevision = 0u },
            valid with { SourceRevision = 0u },
            valid with
            {
                Extent = valid.Extent with { ScaledWidth = valid.Extent.ScaledWidth + 1 }
            },
            // 0.499 still quantizes to this layout's 320x180 extent, so this
            // demonstrates that source scale is frozen independently of a
            // coincidentally matching integer footprint.
            valid with
            {
                Extent = valid.Extent with { ResolutionScale = 0.499f }
            },
            valid with
            {
                Format = SimpleDdgiNearFieldResidualFormat.B10G11R11UfloatPack32
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.TryValidateForLayout(layout, out _), Is.True);
            foreach (SimpleDdgiNearFieldTraceSourceContract invalid in invalidContracts)
                Assert.That(invalid.TryValidateForLayout(layout, out _), Is.False);
        });
    }

    [Test]
    public void BandResidual_IsSignedButInvalidInputIsExactlyZero()
    {
        Vector3 valid = SimpleDdgiNearFieldResidualReference.EvaluateBandResidual(
            new Vector3(1.0f, 0.5f, 2.0f),
            new Vector3(2.0f, 0.25f, 1.0f),
            confidence: 1.0f,
            nearEstimateValid: true,
            lowEstimateValid: true);
        Vector3 invalid = SimpleDdgiNearFieldResidualReference.EvaluateBandResidual(
            Vector3.One,
            Vector3.Zero,
            confidence: 1.0f,
            nearEstimateValid: false,
            lowEstimateValid: true);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.EqualTo(new Vector3(-1.0f, 0.25f, 1.0f)));
            Assert.That(invalid, Is.EqualTo(Vector3.Zero));
        });
    }

    [Test]
    public void ResidualConfidence_IsAppliedExactlyOnceByComposite()
    {
        Vector3 signedBand = SimpleDdgiNearFieldResidualReference.EvaluateBandResidual(
            new Vector3(5.0f, 3.0f, 1.0f),
            new Vector3(1.0f, 1.0f, 1.0f),
            confidence: 0.5f,
            nearEstimateValid: true,
            lowEstimateValid: true);
        Vector3 composed = SimpleDdgiNearFieldResidualReference.Composite(
            new Vector3(10.0f, 10.0f, 10.0f),
            signedBand,
            confidence: 0.5f,
            residualValid: true);

        Assert.Multiple(() =>
        {
            Assert.That(signedBand, Is.EqualTo(new Vector3(4.0f, 2.0f, 0.0f)),
                "The signed trace band carries no pre-applied confidence.");
            Assert.That(composed, Is.EqualTo(new Vector3(12.0f, 11.0f, 10.0f)),
                "Composite is the single confidence authority, not a second multiplier.");
        });
    }

    [Test]
    public void Composite_LeavesCanonicalFieldUnchangedOnMissAndBoundsValidatedNegativeBand()
    {
        Vector3 canonical = new(4.0f, 1.0f, 1.0f);
        Vector3 miss = SimpleDdgiNearFieldResidualReference.Composite(
            canonical, new Vector3(100.0f), 1.0f, residualValid: false);
        Vector3 correction = SimpleDdgiNearFieldResidualReference.Composite(
            canonical, new Vector3(-2.0f, 0.0f, 0.0f), 1.0f, residualValid: true);

        Assert.Multiple(() =>
        {
            Assert.That(miss, Is.EqualTo(canonical));
            Assert.That(correction.X, Is.EqualTo(3.2f).Within(1.0e-6f));
            Assert.That(correction.Y, Is.EqualTo(1.0f));
            Assert.That(correction.Z, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void Composite_UniformlyLimitsRareResidualOutliersWithoutChangingHue()
    {
        Vector3 composed = SimpleDdgiNearFieldResidualReference.Composite(
            new Vector3(1.0f, 2.0f, 4.0f),
            new Vector3(100.0f, -100.0f, 100.0f),
            confidence: 1.0f,
            residualValid: true);

        Assert.Multiple(() =>
        {
            Assert.That(composed.X, Is.EqualTo(1.2f).Within(1.0e-6f));
            Assert.That(composed.Y, Is.EqualTo(1.8f).Within(1.0e-6f));
            Assert.That(composed.Z, Is.EqualTo(4.2f).Within(1.0e-6f));
        });
    }

    [Test]
    public void TemporalEvidence_RejectsYoungOrNoisyResidualsAndAcceptsStableHistory()
    {
        float young = SimpleDdgiNearFieldResidualReference
            .EvaluateTemporalEvidenceConfidence(0.01f, 0.000_100_1f, 1);
        float noisy = SimpleDdgiNearFieldResidualReference
            .EvaluateTemporalEvidenceConfidence(0.01f, 0.01f, 64);
        float stable = SimpleDdgiNearFieldResidualReference
            .EvaluateTemporalEvidenceConfidence(0.01f, 0.000_100_1f, 64);
        float zero = SimpleDdgiNearFieldResidualReference
            .EvaluateTemporalEvidenceConfidence(0.0f, 0.0f, 64);

        Assert.Multiple(() =>
        {
            Assert.That(young, Is.Zero);
            Assert.That(noisy, Is.Zero);
            Assert.That(stable, Is.EqualTo(1.0f).Within(1.0e-6f));
            Assert.That(zero, Is.Zero);
        });
    }

    [Test]
    public void History_ReusesStochasticHitChangesButRejectsReceiverAndRevisionChanges()
    {
        SimpleDdgiNearFieldHistoryIdentity baseline = Identity();
        SimpleDdgiNearFieldHistoryValidation stochasticHitChanged =
            SimpleDdgiNearFieldResidualReference.ValidateHistory(
                Identity() with
                {
                    HitObjectId = 12,
                    HitMaterialRevision = 13,
                    HitDepth = float.NaN
                },
                baseline,
                depthTolerance: 0.01f,
                minimumNormalDot: 0.9f);
        SimpleDdgiNearFieldHistoryValidation receiverChanged =
            SimpleDdgiNearFieldResidualReference.ValidateHistory(
                Identity() with { ReceiverObjectId = 12 },
                baseline,
                depthTolerance: 0.01f,
                minimumNormalDot: 0.9f);
        SimpleDdgiNearFieldHistoryValidation abiChanged =
            SimpleDdgiNearFieldResidualReference.ValidateHistory(
                Identity() with { TraceSourceAbiRevision = 2 },
                baseline,
                depthTolerance: 0.01f,
                minimumNormalDot: 0.9f);
        SimpleDdgiNearFieldHistoryValidation sourceLayoutChanged =
            SimpleDdgiNearFieldResidualReference.ValidateHistory(
                Identity() with { TraceSourceLayoutRevision = 2 },
                baseline,
                depthTolerance: 0.01f,
                minimumNormalDot: 0.9f);
        SimpleDdgiNearFieldHistoryValidation valid =
            SimpleDdgiNearFieldResidualReference.ValidateHistory(
                Identity(), baseline, 0.01f, 0.9f);

        Assert.Multiple(() =>
        {
            Assert.That(stochasticHitChanged.Accepted, Is.True);
            Assert.That(receiverChanged.Reason,
                Is.EqualTo(SimpleDdgiNearFieldHistoryRejectionReason.ReceiverObjectMismatch));
            Assert.That(abiChanged.Reason,
                Is.EqualTo(SimpleDdgiNearFieldHistoryRejectionReason.TraceSourceAbiChanged));
            Assert.That(sourceLayoutChanged.Reason,
                Is.EqualTo(SimpleDdgiNearFieldHistoryRejectionReason.TraceSourceLayoutChanged));
            Assert.That(valid.Accepted, Is.True);
        });
    }

    [Test]
    public void Layout_IsCompleteOrEmptyAndAccountsForFullResolutionSource()
    {
        SimpleDdgiNearFieldResidualProfile profile =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference;
        SimpleDdgiNearFieldResidualLayout valid =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                1_920, 1_080, profile, 160UL * 1024UL * 1024UL);
        SimpleDdgiNearFieldResidualLayout rejected =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                1_920, 1_080, profile, 1UL);

        Assert.Multiple(() =>
        {
            Assert.That(valid.IsValid, Is.True);
            Assert.That(valid.TraceWidth, Is.EqualTo(960));
            Assert.That(valid.TraceHeight, Is.EqualTo(540));
            Assert.That(valid.TraceSourceBytes, Is.GreaterThan(1_920UL * 1_080UL * 7UL));
            Assert.That(valid.ReceiverPayloadBytes,
                Is.GreaterThanOrEqualTo(1_920UL * 1_080UL * 16UL));
            Assert.That(valid.TraceFrameConstantsBytes,
                Is.EqualTo(512UL));
            Assert.That(valid.TelemetryReadbackBytes,
                Is.EqualTo(valid.TileBuffersBytes *
                    (ulong)RenderingConstants.FramesInFlight));
            Assert.That(valid.HitMetadataBytes, Is.GreaterThan(0));
            Assert.That(valid.HistoryRadianceBytes, Is.GreaterThan(0));
            Assert.That(valid.HistoryNormalBytes, Is.GreaterThan(0));
            Assert.That(rejected.IsValid, Is.False);
            Assert.That(rejected.TotalBytes, Is.Zero);
        });
    }

    [Test]
    public void ProductionProfiles_HonorTheIndependentNinetySixMiBEnvelope()
    {
        const ulong budget = 96UL * 1024UL * 1024UL;
        SimpleDdgiNearFieldResidualLayout hdHalf =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                1_920, 1_080,
                SimpleDdgiNearFieldResidualProfile.HalfResolutionReference,
                budget);
        SimpleDdgiNearFieldResidualLayout hdQuarter =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                1_920, 1_080,
                SimpleDdgiNearFieldResidualProfile.QuarterResolutionPerformance,
                budget);
        SimpleDdgiNearFieldResidualLayout qhdQuarter =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                2_560, 1_440,
                SimpleDdgiNearFieldResidualProfile.QuarterResolutionPerformance,
                budget);
        SimpleDdgiNearFieldResidualLayout qhdEighth =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                2_560, 1_440,
                SimpleDdgiNearFieldResidualProfile.EighthResolutionMemoryBound,
                budget);
        SimpleDdgiNearFieldResidualLayout uhdEighth =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                3_840, 2_160,
                SimpleDdgiNearFieldResidualProfile.EighthResolutionMemoryBound,
                budget);

        Assert.Multiple(() =>
        {
            Assert.That(hdHalf.IsValid, Is.False);
            Assert.That(hdQuarter.IsValid, Is.True);
            Assert.That(hdQuarter.TraceWidth, Is.EqualTo(480));
            Assert.That(hdQuarter.TraceHeight, Is.EqualTo(270));
            Assert.That(qhdQuarter.IsValid, Is.False);
            Assert.That(qhdEighth.IsValid, Is.True);
            Assert.That(qhdEighth.TraceWidth, Is.EqualTo(320));
            Assert.That(qhdEighth.TraceHeight, Is.EqualTo(180));
            Assert.That(uhdEighth.IsValid, Is.False);
        });
    }

    [Test]
    public void Layout_RejectsAnUnknownSourceFormatWithoutThrowingOrAllocating()
    {
        SimpleDdgiNearFieldResidualProfile invalidProfile =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference with
            {
                SourceFormat = (SimpleDdgiNearFieldResidualFormat)255
            };

        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                640, 360, invalidProfile, 256UL * 1024UL * 1024UL);

        Assert.Multiple(() =>
        {
            Assert.That(layout.IsValid, Is.False);
            Assert.That(layout.TotalBytes, Is.Zero);
            Assert.That(layout.FailureReason, Is.EqualTo("invalid-near-field-profile"));
        });
    }

    [Test]
    public void BoundedTrace_HitsPlaneAndStopsAtScreenEdge()
    {
        var hierarchy = new ConstantDepthHierarchy(0.5f, maxMip: 5);
        var configuration = new SimpleDdgiNearFieldTraceConfiguration(
            MaximumSteps: 8,
            MaximumMipVisits: 11,
            BinaryRefinementSteps: 3,
            Thickness: 0.0f,
            StartBias: 0.0f,
            DepthConvention: SimpleDdgiNearFieldDepthConvention.ForwardZ);
        SimpleDdgiNearFieldTraceResult hit = SimpleDdgiNearFieldTraceReference.Trace(
            hierarchy,
            new Vector2(0.2f, 0.5f),
            new Vector2(0.8f, 0.5f),
            startDepth: 0.1f,
            endDepth: 0.9f,
            configuration);
        SimpleDdgiNearFieldTraceResult edge = SimpleDdgiNearFieldTraceReference.Trace(
            hierarchy,
            new Vector2(0.8f, 0.5f),
            new Vector2(1.4f, 0.5f),
            0.1f,
            0.9f,
            configuration);

        Assert.Multiple(() =>
        {
            Assert.That(hit.Hit, Is.True);
            Assert.That(hit.StepCount, Is.LessThanOrEqualTo(8));
            Assert.That(hit.MipVisitCount, Is.LessThanOrEqualTo(11));
            Assert.That(edge.Hit, Is.False);
            Assert.That(edge.RejectionReason, Is.EqualTo("screen-exit"));
        });
    }

    [Test]
    public void BoundedTrace_RejectsRatherThanExceedingItsHierarchicalVisitBudget()
    {
        var hierarchy = new ConstantDepthHierarchy(0.5f, maxMip: 5);
        var configuration = new SimpleDdgiNearFieldTraceConfiguration(
            MaximumSteps: 8,
            MaximumMipVisits: 2,
            BinaryRefinementSteps: 3,
            Thickness: 0.0f,
            StartBias: 0.0f,
            DepthConvention: SimpleDdgiNearFieldDepthConvention.ForwardZ);

        SimpleDdgiNearFieldTraceResult result = SimpleDdgiNearFieldTraceReference.Trace(
            hierarchy, new Vector2(0.2f, 0.5f), new Vector2(0.8f, 0.5f),
            startDepth: 0.1f, endDepth: 0.9f, configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.False);
            Assert.That(result.MipVisitCount, Is.LessThanOrEqualTo(2));
            Assert.That(result.RejectionReason, Is.EqualTo("mip-visit-budget"));
        });
    }

    [Test]
    public void MeasureBeforeBuild_RequiresARealPostB3Opportunity()
    {
        var materialOpportunity = new SimpleDdgiNearFieldResidualMeasurement(
            CorpusId: "hands-and-crease-reference-v1",
            ContentRevision: 1,
            B3QualificationRevision: 1,
            PostB3NearFieldError: 10.0,
            C5OracleError: 7.0,
            EqualCostAdditionalB3Error: 8.5,
            ErrorIsScreenLocal: true,
            ErrorIsObservableByShortDepthRay: true,
            RootCauseIsNotDdgiLivenessOrAlpha: true,
            UsesSceneLinearReference: true);
        SimpleDdgiNearFieldResidualDecision proceed =
            SimpleDdgiNearFieldResidualEvidenceEvaluator.Evaluate(materialOpportunity);
        SimpleDdgiNearFieldResidualDecision noGo =
            SimpleDdgiNearFieldResidualEvidenceEvaluator.Evaluate(
                materialOpportunity with { EqualCostAdditionalB3Error = 6.5 });

        Assert.Multiple(() =>
        {
            Assert.That(proceed.Proceed, Is.True);
            Assert.That(proceed.C5ErrorReductionFraction, Is.EqualTo(0.3).Within(1.0e-12));
            Assert.That(noGo.Proceed, Is.False);
            Assert.That(noGo.Reason, Is.EqualTo("equal-cost-B3-is-at-least-as-effective"));
        });
    }

    [Test]
    public void HistoryManager_ClearsOnInvalidCandidateAndDoesNotReuseIt()
    {
        var manager = new SimpleDdgiNearFieldResidualHistoryManager();
        SimpleDdgiNearFieldHistoryIdentity identity = Identity();
        manager.EndFrame(identity);
        Assert.That(manager.HasHistory, Is.True);
        Assert.That(manager.BeginFrame(identity, 0.01f, 0.9f).Accepted, Is.True);

        manager.EndFrame(identity with { CurrentCandidateValid = false });
        Assert.Multiple(() =>
        {
            Assert.That(manager.HasHistory, Is.False);
            Assert.That(manager.ClearCount, Is.EqualTo(1));
            Assert.That(manager.BeginFrame(identity, 0.01f, 0.9f).Accepted, Is.False);
        });
    }

    [Test]
    public void HistoryReference_RejectsAnInvalidPriorCandidateBeforeComparingFields()
    {
        SimpleDdgiNearFieldHistoryValidation validation =
            SimpleDdgiNearFieldResidualReference.ValidateHistory(
                Identity(), Identity() with { CurrentCandidateValid = false },
                0.01f, 0.9f);

        Assert.That(validation, Is.EqualTo(
            SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.InvalidPreviousCandidate)));
    }

    private static SimpleDdgiNearFieldHistoryIdentity Identity() => new(
        CurrentCandidateValid: true,
        CameraCut: false,
        ViewportRevision: 1,
        HiZRevision: 1,
        TraceSourceAbiRevision: 1,
        EffectiveModeRevision: 1,
        ExposureDomainRevision: 1,
        ReceiverObjectId: 1,
        ReceiverMaterialRevision: 2,
        HitObjectId: 3,
        HitMaterialRevision: 4,
        ProbeOwnershipRevision: 5,
        ReceiverDepth: 0.5f,
        HitDepth: 0.6f,
        ReceiverGeometricNormal: Vector3.UnitZ,
        ReceiverShadingNormal: Vector3.UnitZ,
        TraceSourceLayoutRevision: 1u);

    private sealed class ConstantDepthHierarchy : ISimpleDdgiNearFieldDepthHierarchy
    {
        private readonly float _depth;

        public ConstantDepthHierarchy(float depth, int maxMip)
        {
            _depth = depth;
            MaximumMipLevel = maxMip;
        }

        public int MaximumMipLevel { get; }

        public bool TrySample(Vector2 uv, int mipLevel, out float depth)
        {
            depth = _depth;
            return mipLevel >= 0 && mipLevel <= MaximumMipLevel;
        }
    }
}
