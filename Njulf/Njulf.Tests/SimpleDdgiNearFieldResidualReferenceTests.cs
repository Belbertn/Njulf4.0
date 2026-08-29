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
    public void ProductionQualityPresets_AreFrozenToTheV2Contract()
    {
        SimpleDdgiNearFieldResidualProfile performance =
            SimpleDdgiNearFieldResidualProfile.Performance;
        SimpleDdgiNearFieldResidualProfile balanced =
            SimpleDdgiNearFieldResidualProfile.Balanced;
        SimpleDdgiNearFieldResidualProfile quality =
            SimpleDdgiNearFieldResidualProfile.Quality;

        Assert.Multiple(() =>
        {
            AssertProfile(performance, 3.0f, 6.0f, 48, 1, 1,
                SimpleDdgiNearFieldResidualResolutionScales.Eighth |
                SimpleDdgiNearFieldResidualResolutionScales.Quarter |
                SimpleDdgiNearFieldResidualResolutionScales.Half);
            AssertProfile(balanced, 4.0f, 8.0f, 64, 2, 2,
                SimpleDdgiNearFieldResidualResolutionScales.Eighth |
                SimpleDdgiNearFieldResidualResolutionScales.Quarter |
                SimpleDdgiNearFieldResidualResolutionScales.Half);
            AssertProfile(quality, 6.0f, 12.0f, 96, 4, 3,
                SimpleDdgiNearFieldResidualResolutionScales.Eighth |
                SimpleDdgiNearFieldResidualResolutionScales.Quarter |
                SimpleDdgiNearFieldResidualResolutionScales.Half);
            Assert.That(performance.BinaryRefinementSteps, Is.EqualTo(4));
            Assert.That(balanced.BinaryRefinementSteps, Is.EqualTo(4));
            Assert.That(quality.BinaryRefinementSteps, Is.EqualTo(4));
        });
    }

    [Test]
    public void V2Sampling_IsStableAndAveragesMissesAsZero()
    {
        var key = new SimpleDdgiNearFieldSampleKey(
            SequenceIndex: 7,
            RayOrdinal: 1,
            StableSurfaceIdentity: 0x1234u,
            PixelX: 19,
            PixelY: 37);
        Vector2 first = SimpleDdgiNearFieldSamplingReference.OwenSobol2D(key);
        Vector2 repeated = SimpleDdgiNearFieldSamplingReference.OwenSobol2D(key);
        Vector2 nextRay = SimpleDdgiNearFieldSamplingReference.OwenSobol2D(
            key with { RayOrdinal = 2 });
        float guided = SimpleDdgiNearFieldSamplingReference
            .GuidedTexelToSolidAnglePdf(0.125f, 4.0f, 0.5f, 0.25f);
        float mixture = SimpleDdgiNearFieldSamplingReference.MixturePdf(
            cosinePdf: 0.2f, guidedPdf: guided, guidedWeight: 0.5f);
        Vector3[] contributions = [new(2.0f, 0.0f, 0.0f), new(50.0f)];
        bool[] hits = [true, false];
        (Vector3 mean, float coverage) = SimpleDdgiNearFieldSamplingReference
            .AggregateLaunchedRays(contributions, hits);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(repeated));
            Assert.That(nextRay, Is.Not.EqualTo(first));
            Assert.That(first.X, Is.InRange(0.0f, 1.0f));
            Assert.That(first.Y, Is.InRange(0.0f, 1.0f));
            Assert.That(guided, Is.EqualTo(4.0f).Within(1.0e-6f));
            Assert.That(mixture, Is.EqualTo(2.1f).Within(1.0e-6f));
            Assert.That(mean, Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
            Assert.That(coverage, Is.EqualTo(0.5f));
        });
    }

    [Test]
    public void V2ViewSpaceConfiguration_UsesMetricFootprintMinima()
    {
        var tiny = new SimpleDdgiNearFieldViewTraceConfiguration(
            MaximumTraceSteps: 64,
            MipZeroRefinementSteps: 4,
            NearPlaneMeters: 0.1f,
            MaximumDistanceMeters: 8.0f,
            ReceiverPixelFootprintMeters: 0.0001f,
            BiasFootprintScale: 1.0f,
            ThicknessFootprintScale: 2.0f,
            DepthDiscontinuityScale: 2.0f,
            ViewportExtent: new Vector2(1920.0f, 1080.0f));
        var large = tiny with { ReceiverPixelFootprintMeters = 0.05f };

        Assert.Multiple(() =>
        {
            Assert.That(tiny.StartBiasMeters, Is.EqualTo(0.001f));
            Assert.That(tiny.ThicknessMeters, Is.EqualTo(0.02f));
            Assert.That(large.StartBiasMeters, Is.EqualTo(0.05f));
            Assert.That(large.ThicknessMeters, Is.EqualTo(0.1f));
            Assert.That(() => tiny.Validate(), Throws.Nothing);
        });
    }

    [Test]
    public void ViewSpaceDda_UsesPerspectiveInterpolationAndMipZeroRefinement()
    {
        var hierarchy = new ConstantLinearDepthHierarchy(3.0f, maxMip: 6);
        SimpleDdgiNearFieldViewTraceConfiguration configuration =
            ViewTraceConfiguration(maximumTraceSteps: 64) with
            {
                ReceiverPixelFootprintMeters = 0.05f
            };

        SimpleDdgiNearFieldTraceResult result =
            SimpleDdgiNearFieldViewSpaceTraceReference.Trace(
                hierarchy,
                receiverViewPosition: new Vector3(0.0f, 0.0f, -1.0f),
                viewDirection: new Vector3(0.75f, 0.0f, -1.0f),
                projection: ViewProjection(),
                configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.True, result.RejectionReason);
            Assert.That(result.RayDepth, Is.EqualTo(result.SceneDepth)
                .Within(configuration.ThicknessMeters *
                    configuration.DepthDiscontinuityScale));
            Assert.That(result.HitUv.X, Is.GreaterThan(0.5f));
            Assert.That(result.HitUv.X, Is.LessThan(1.0f));
            Assert.That(result.RefinementCount,
                Is.EqualTo(configuration.MipZeroRefinementSteps));
            Assert.That(result.StepCount,
                Is.LessThanOrEqualTo(configuration.MaximumTraceSteps));
        });
    }

    [Test]
    public void ViewSpaceDda_ConsumesTheCompleteSixtyFourDepthTestBudget()
    {
        var hierarchy = new ConstantLinearDepthHierarchy(1_000.0f, maxMip: 0);
        SimpleDdgiNearFieldViewTraceConfiguration configuration =
            ViewTraceConfiguration(maximumTraceSteps: 64) with
            {
                ViewportExtent = new Vector2(512.0f, 512.0f)
            };

        SimpleDdgiNearFieldTraceResult result =
            SimpleDdgiNearFieldViewSpaceTraceReference.Trace(
                hierarchy,
                receiverViewPosition: new Vector3(0.0f, 0.0f, -1.0f),
                viewDirection: new Vector3(0.75f, 0.0f, -1.0f),
                projection: ViewProjection(),
                configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.False);
            Assert.That(result.RejectionReason, Is.EqualTo("step-limit"));
            Assert.That(result.StepCount, Is.EqualTo(64));
            Assert.That(result.MipVisitCount, Is.EqualTo(64));
        });
    }

    [Test]
    public void ViewSpaceDda_TraversesAThinGapAndRefinesTheDepthStepBehindIt()
    {
        // Coarse levels conservatively contain the two-metre foreground.
        // Mip zero exposes a narrow empty interval followed by a four-metre
        // surface. The ray stays in front of the foreground, passes through
        // the gap, and must not turn the coarse minimum into a false hit.
        var hierarchy = new FunctionalLinearDepthHierarchy(
            maximumMipLevel: 6,
            sample: (uv, mip) => mip > 0
                ? 2.0f
                : uv.X < 0.64f
                    ? 2.0f
                    : uv.X < 0.66f
                        ? null
                        : 4.0f);
        SimpleDdgiNearFieldViewTraceConfiguration configuration =
            ViewTraceConfiguration(maximumTraceSteps: 96) with
            {
                ReceiverPixelFootprintMeters = 0.05f,
                ViewportExtent = new Vector2(512.0f, 512.0f)
            };

        SimpleDdgiNearFieldTraceResult result =
            SimpleDdgiNearFieldViewSpaceTraceReference.Trace(
                hierarchy,
                receiverViewPosition: new Vector3(0.0f, 0.0f, -1.0f),
                viewDirection: new Vector3(0.75f, 0.0f, -1.0f),
                projection: ViewProjection(),
                configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.Hit, Is.True, result.RejectionReason);
            Assert.That(result.SceneDepth, Is.EqualTo(4.0f));
            Assert.That(result.HitUv.X, Is.GreaterThan(0.66f));
            Assert.That(result.RefinementCount,
                Is.EqualTo(configuration.MipZeroRefinementSteps));
            Assert.That(result.StepCount,
                Is.LessThanOrEqualTo(configuration.MaximumTraceSteps));
        });
    }

    [Test]
    public void ViewSpaceDda_ClipsNearPlaneAndRejectsSegmentsOutsideTheFrustum()
    {
        var hierarchy = new ConstantLinearDepthHierarchy(0.5f, maxMip: 5);
        SimpleDdgiNearFieldViewTraceConfiguration configuration =
            ViewTraceConfiguration(maximumTraceSteps: 64) with
            {
                ReceiverPixelFootprintMeters = 1.0f,
                BiasFootprintScale = 0.0f
            };

        SimpleDdgiNearFieldTraceResult clippedHit =
            SimpleDdgiNearFieldViewSpaceTraceReference.Trace(
                hierarchy,
                receiverViewPosition: new Vector3(0.0f, 0.0f, -0.05f),
                viewDirection: new Vector3(0.25f, 0.0f, -1.0f),
                projection: ViewProjection(),
                configuration);
        SimpleDdgiNearFieldTraceResult behindNearPlane =
            SimpleDdgiNearFieldViewSpaceTraceReference.Trace(
                hierarchy,
                receiverViewPosition: new Vector3(0.0f, 0.0f, -0.05f),
                viewDirection: new Vector3(0.25f, 0.0f, 1.0f),
                projection: ViewProjection(),
                configuration);
        SimpleDdgiNearFieldTraceResult outsideViewport =
            SimpleDdgiNearFieldViewSpaceTraceReference.Trace(
                hierarchy,
                receiverViewPosition: new Vector3(4.0f, 0.0f, -1.0f),
                viewDirection: new Vector3(1.0f, 0.0f, -1.0f),
                projection: ViewProjection(),
                configuration);

        Assert.Multiple(() =>
        {
            Assert.That(clippedHit.Hit, Is.True, clippedHit.RejectionReason);
            Assert.That(clippedHit.RayDepth,
                Is.GreaterThanOrEqualTo(configuration.NearPlaneMeters));
            Assert.That(behindNearPlane.RejectionReason,
                Is.EqualTo("near-plane-clipped"));
            Assert.That(outsideViewport.RejectionReason,
                Is.EqualTo("screen-exit"));
        });
    }

    [Test]
    public void DepthConventionReference_ProducesForwardAndReversedZParity()
    {
        var hierarchy = new ConstantDepthHierarchy(0.5f, maxMip: 5);
        var forwardConfiguration = new SimpleDdgiNearFieldTraceConfiguration(
            MaximumSteps: 64,
            MaximumMipVisits: 32,
            BinaryRefinementSteps: 4,
            Thickness: 0.0f,
            StartBias: 0.0f,
            DepthConvention: SimpleDdgiNearFieldDepthConvention.ForwardZ);
        SimpleDdgiNearFieldTraceConfiguration reversedConfiguration =
            forwardConfiguration with
            {
                DepthConvention = SimpleDdgiNearFieldDepthConvention.ReversedZ
            };

        SimpleDdgiNearFieldTraceResult forward =
            SimpleDdgiNearFieldTraceReference.Trace(
                hierarchy, new Vector2(0.2f, 0.5f), new Vector2(0.8f, 0.5f),
                startDepth: 0.1f, endDepth: 0.9f, forwardConfiguration);
        SimpleDdgiNearFieldTraceResult reversed =
            SimpleDdgiNearFieldTraceReference.Trace(
                hierarchy, new Vector2(0.2f, 0.5f), new Vector2(0.8f, 0.5f),
                startDepth: 0.9f, endDepth: 0.1f, reversedConfiguration);

        Assert.Multiple(() =>
        {
            Assert.That(forward.Hit, Is.True);
            Assert.That(reversed.Hit, Is.True);
            Assert.That(reversed.HitUv.X,
                Is.EqualTo(forward.HitUv.X).Within(1.0f / 1_024.0f));
            Assert.That(Math.Abs(reversed.StepCount - forward.StepCount),
                Is.LessThanOrEqualTo(1));
        });
    }

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
    public void History_RejectsChangedHitIdentityReceiverAndGlobalRevisions()
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
            Assert.That(stochasticHitChanged.Accepted, Is.False);
            Assert.That(stochasticHitChanged.Reason,
                Is.EqualTo(SimpleDdgiNearFieldHistoryRejectionReason
                    .HitMaterialRevisionMismatch));
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
    public void Layout_IsCompleteOrEmptyAndAccountsForTraceResolutionSource()
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
            Assert.That(valid.SourceProducerMode,
                Is.EqualTo(SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster));
            Assert.That(valid.TraceSourceBytes,
                Is.GreaterThanOrEqualTo(960UL * 540UL * 8UL));
            Assert.That(valid.TraceSourceBytes,
                Is.LessThan(1_920UL * 1_080UL * 8UL));
            Assert.That(valid.ReceiverPayloadBytes,
                Is.GreaterThanOrEqualTo(960UL * 540UL * 16UL));
            Assert.That(valid.ReceiverPayloadBytes,
                Is.LessThan(1_920UL * 1_080UL * 16UL));
            Assert.That(valid.TraceRasterDepthBytes,
                Is.GreaterThanOrEqualTo(960UL * 540UL * 4UL));
            Assert.That(valid.TraceFrameConstantsBytes,
                Is.EqualTo(1_024UL));
            Assert.That(valid.TelemetryReadbackBytes,
                Is.EqualTo(valid.TileBuffersBytes *
                    (ulong)RenderingConstants.FramesInFlight));
            Assert.That(valid.HitMetadataBytes, Is.Zero,
                "V13 writes trace metadata directly into the current history bank.");
            Assert.That(valid.HistoryRadianceBytes, Is.GreaterThan(0));
            Assert.That(valid.HistoryNormalBytes, Is.GreaterThan(0));
            Assert.That(rejected.IsValid, Is.False);
            Assert.That(rejected.TotalBytes, Is.Zero);
        });
    }

    [Test]
    public void V16Layout_PacksHistoryAndAllocatesOneAliasedFilterPeer()
    {
        const ulong budget = 512UL * 1024UL * 1024UL;
        SimpleDdgiNearFieldResidualProfile baseProfile =
            SimpleDdgiNearFieldResidualProfile.Balanced with
            {
                ImageRowAlignment = 1,
                ImageAllocationGranularity = 1
            };
        SimpleDdgiNearFieldResidualLayout noFilter =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                64, 32, baseProfile with { FilterIterationCount = 0 }, budget);
        SimpleDdgiNearFieldResidualLayout oneFilter =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                64, 32, baseProfile with { FilterIterationCount = 1 }, budget);
        SimpleDdgiNearFieldResidualLayout twoFilters =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                64, 32, baseProfile with { FilterIterationCount = 2 }, budget);
        SimpleDdgiNearFieldResidualLayout threeFilters =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                64, 32, baseProfile with { FilterIterationCount = 3 }, budget);

        const ulong tracePixels = 32UL * 16UL;
        Assert.Multiple(() =>
        {
            Assert.That(noFilter.IsValid, Is.True);
            Assert.That(oneFilter.IsValid, Is.True);
            Assert.That(twoFilters.IsValid, Is.True);
            Assert.That(threeFilters.IsValid, Is.True);
            Assert.That(oneFilter.HistoryValidityBytes,
                Is.EqualTo(2UL * tracePixels * 2UL),
                "Two R16_UINT banks must be charged exactly.");
            Assert.That(oneFilter.HistoryNormalBytes,
                Is.EqualTo(2UL * tracePixels * 4UL),
                "Two packed R32_UINT normal banks must be charged exactly.");
            Assert.That(noFilter.FilterScratchBytes, Is.Zero);
            Assert.That(noFilter.AliasedFilterScratchBytes, Is.Zero);
            Assert.That(noFilter.PhysicalFilterScratchImageCount, Is.Zero);
            Assert.That(oneFilter.FilterScratchBytes,
                Is.EqualTo(oneFilter.RawCandidateBytes));
            Assert.That(twoFilters.FilterScratchBytes,
                Is.EqualTo(oneFilter.FilterScratchBytes));
            Assert.That(threeFilters.FilterScratchBytes,
                Is.EqualTo(oneFilter.FilterScratchBytes));
            Assert.That(threeFilters.AliasedFilterScratchBytes,
                Is.EqualTo(threeFilters.RawCandidateBytes));
            Assert.That(threeFilters.PhysicalFilterScratchImageCount, Is.EqualTo(1));
            Assert.That(oneFilter.TotalBytes - noFilter.TotalBytes,
                Is.EqualTo(oneFilter.FilterScratchBytes),
                "Filtering must add one physical peer regardless of iteration count.");
            Assert.That(twoFilters.TotalBytes, Is.EqualTo(oneFilter.TotalBytes));
            Assert.That(threeFilters.TotalBytes, Is.EqualTo(oneFilter.TotalBytes));
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
            Assert.That(qhdQuarter.IsValid, Is.True);
            Assert.That(qhdQuarter.TotalBytes, Is.LessThanOrEqualTo(budget));
            Assert.That(qhdEighth.IsValid, Is.True);
            Assert.That(qhdEighth.TotalBytes, Is.LessThanOrEqualTo(budget));
            Assert.That(uhdEighth.IsValid, Is.True);
            Assert.That(uhdEighth.TotalBytes, Is.LessThanOrEqualTo(budget));
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
    public void BoundedTrace_ChargesDepthTestsToTheStepBudgetNotAMipVisitBudget()
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
            Assert.That(result.Hit, Is.True);
            Assert.That(result.StepCount, Is.LessThanOrEqualTo(8));
            Assert.That(result.MipVisitCount, Is.GreaterThan(2));
            Assert.That(result.RejectionReason, Is.EqualTo("hit"));
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

    private static void AssertProfile(
        SimpleDdgiNearFieldResidualProfile profile,
        float fullDistance,
        float maximumDistance,
        int steps,
        int rays,
        int filters,
        SimpleDdgiNearFieldResidualResolutionScales scales)
    {
        Assert.That(profile.FullWeightTraceDistanceMeters,
            Is.EqualTo(fullDistance));
        Assert.That(profile.MaximumTraceDistanceMeters,
            Is.EqualTo(maximumDistance));
        Assert.That(profile.MaximumTraceSteps, Is.EqualTo(steps));
        Assert.That(profile.MaximumRaysPerPixel, Is.EqualTo(rays));
        Assert.That(profile.FilterIterationCount, Is.EqualTo(filters));
        Assert.That(profile.AllowedResolutionScales, Is.EqualTo(scales));
    }

    private static SimpleDdgiNearFieldViewTraceConfiguration
        ViewTraceConfiguration(int maximumTraceSteps) => new(
            MaximumTraceSteps: maximumTraceSteps,
            MipZeroRefinementSteps: 4,
            NearPlaneMeters: 0.1f,
            MaximumDistanceMeters: 8.0f,
            ReceiverPixelFootprintMeters: 0.01f,
            BiasFootprintScale: 1.0f,
            ThicknessFootprintScale: 2.0f,
            DepthDiscontinuityScale: 2.0f,
            ViewportExtent: new Vector2(256.0f, 256.0f));

    private static Matrix4x4 ViewProjection() =>
        Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 2.0f, 1.0f, 0.1f, 100.0f);

    private sealed class ConstantLinearDepthHierarchy :
        ISimpleDdgiNearFieldLinearDepthHierarchy
    {
        private readonly float _depth;

        public ConstantLinearDepthHierarchy(float depth, int maxMip)
        {
            _depth = depth;
            MaximumMipLevel = maxMip;
        }

        public int MaximumMipLevel { get; }

        public bool TrySampleLinearDepth(
            Vector2 uv, int mipLevel, out float linearDepth)
        {
            linearDepth = _depth;
            return mipLevel >= 0 && mipLevel <= MaximumMipLevel;
        }
    }

    private sealed class FunctionalLinearDepthHierarchy :
        ISimpleDdgiNearFieldLinearDepthHierarchy
    {
        private readonly Func<Vector2, int, float?> _sample;

        public FunctionalLinearDepthHierarchy(
            int maximumMipLevel,
            Func<Vector2, int, float?> sample)
        {
            MaximumMipLevel = maximumMipLevel;
            _sample = sample;
        }

        public int MaximumMipLevel { get; }

        public bool TrySampleLinearDepth(
            Vector2 uv, int mipLevel, out float linearDepth)
        {
            float? sampled = mipLevel >= 0 && mipLevel <= MaximumMipLevel
                ? _sample(uv, mipLevel)
                : null;
            linearDepth = sampled.GetValueOrDefault();
            return sampled.HasValue;
        }
    }

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
