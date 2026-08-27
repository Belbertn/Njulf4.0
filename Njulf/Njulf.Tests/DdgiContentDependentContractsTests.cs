using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiContentDependentContractsTests
{
    [Test]
    public void ManyLightPushFlags_AreStrictlyTracePrivate()
    {
        const uint solveControlMask = 0xff80_0000u;
        uint nonTrace =
            SimpleDdgiComputePass.PackContentDependentLocalLightSamplingFlags(
                tracePrivateAbi: false,
                samplingEnabled: true,
                SimpleDdgiLocalLightSamplingMode.LightTree,
                exactLightThreshold: 1_024,
                uniformMixtureProbability: 0.02f);
        uint disabledTrace =
            SimpleDdgiComputePass.PackContentDependentLocalLightSamplingFlags(
                tracePrivateAbi: true,
                samplingEnabled: false,
                SimpleDdgiLocalLightSamplingMode.LightTree,
                exactLightThreshold: 1_024,
                uniformMixtureProbability: 0.02f);
        uint trace =
            SimpleDdgiComputePass.PackContentDependentLocalLightSamplingFlags(
                tracePrivateAbi: true,
                samplingEnabled: true,
                SimpleDdgiLocalLightSamplingMode.LightTree,
                exactLightThreshold: 1_024,
                uniformMixtureProbability: 0.02f);

        Assert.Multiple(() =>
        {
            Assert.That(nonTrace, Is.Zero,
                "Cached solve/blend flags alias the trace payload's upper bits.");
            Assert.That(nonTrace & solveControlMask, Is.Zero);
            Assert.That(disabledTrace, Is.Zero);
            Assert.That(trace & (1u << 31), Is.Not.Zero);
            Assert.That((trace >> 10) & 0x3u,
                Is.EqualTo((uint)SimpleDdgiLocalLightSamplingMode.LightTree));
            Assert.That((trace >> 12) & 0x7ffu, Is.EqualTo(1_024u));
            Assert.That((trace >> 23) & 0xffu, Is.EqualTo(20u));
        });
    }

    [Test]
    public void StableIdentity_MatchesFrozenGoldenVectorAndSeparatesDomains()
    {
        var identity = new DdgiStochasticIdentity(
            0x0123_4567_89AB_CDEFUL,
            DirectionRayOrdinal: 17,
            SourceLightingEpoch: 23,
            SamplingSequenceEpoch: 5,
            DdgiStochasticDecisionDomain.LocalLightTreeTraversal,
            InstanceIdentity: 91,
            PrimitiveIdentity: 7);

        Assert.Multiple(() =>
        {
            Assert.That(identity.Hash32(), Is.EqualTo(0x6ACD_7073u));
            Assert.That(identity.Hash64(), Is.EqualTo(0x1AD0_771B_6ACD_7073UL));
            Assert.That(identity.UnitFloat(), Is.EqualTo(0.41719726f));
            Assert.That(
                identity.WithDomain(DdgiStochasticDecisionDomain.AlphaCoverage).Hash32(),
                Is.Not.EqualTo(identity.Hash32()));
        });
    }

    [Test]
    public void RevisionTracker_SeparatesTopologyContentAndResourceChanges()
    {
        var tracker = new DdgiContentRevisionTracker();
        DdgiContentRevisions initial = tracker.Snapshot;
        DdgiContentRevisions movedLight = tracker.RecordLightChange(topologyChanged: false);
        DdgiContentRevisions addedLight = tracker.RecordLightChange(topologyChanged: true);
        DdgiContentRevisions pose = tracker.RecordRaySceneContentChange();
        DdgiContentRevisions recreated = tracker.RecordRaySceneResourceChange();

        Assert.Multiple(() =>
        {
            Assert.That(movedLight.LightBufferRevision, Is.EqualTo(1));
            Assert.That(movedLight.LightTreeContentRevision, Is.EqualTo(1));
            Assert.That(movedLight.LightTreeTopologyRevision, Is.Zero);
            Assert.That(addedLight.LightTreeTopologyRevision, Is.EqualTo(1));
            Assert.That(pose.RaySceneResourceGeneration,
                Is.EqualTo(initial.RaySceneResourceGeneration));
            Assert.That(pose.RaySceneContentEpoch,
                Is.GreaterThan(initial.RaySceneContentEpoch));
            Assert.That(recreated.RaySceneResourceGeneration,
                Is.GreaterThan(initial.RaySceneResourceGeneration));
        });
    }

    [Test]
    public void HighPreset_ProvisionsDirectionalL2ForFroxelFog()
    {
        var settings = new GlobalIlluminationSettings();
        settings.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);

        Assert.Multiple(() =>
        {
            Assert.That(settings.SimpleDdgiLocalLightSamplingMode,
                Is.EqualTo(SimpleDdgiLocalLightSamplingMode.Auto));
            Assert.That(settings.DdgiSkinnedGeometryMode,
                Is.EqualTo(DdgiSkinnedGeometryMode.ConservativeProxy));
            Assert.That(settings.EffectiveDdgiSkinnedGeometryMode,
                Is.EqualTo(DdgiSkinnedGeometryMode.ConservativeProxy));
            Assert.That(settings.SimpleDdgiDirectionalRadianceMode,
                Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
            Assert.That(settings.ContentDependentRollout.ApprovedFeatures,
                Is.EqualTo(DdgiContentRolloutPolicy.ProductionBaseline));
            Assert.That(settings.ActiveContentDependentFeatures,
                Is.EqualTo(
                    settings.ConfiguredContentDependentFeatures &
                    DdgiContentRolloutPolicy.ProductionBaseline));
            Assert.That(settings.ActiveContentDependentFeatures &
                DdgiContentFeature.FoliageGeometry,
                Is.EqualTo(DdgiContentFeature.None),
                "The RTX 3060-oriented High tier keeps foliage proxies Ultra-only.");
            Assert.That(settings.EffectiveSimpleDdgiDirectionalRadianceMode,
                Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
            Assert.That(settings.EffectiveSimpleDdgiGlossyTransportMode,
                Is.EqualTo(SimpleDdgiGlossyTransportMode.ReceiverOnly));
        });

        // One-bounce publication remains independently qualified; without it
        // High deterministically falls back to its receiver-only glossy base.
        settings.SimpleDdgiDirectionalRadianceMode =
            SimpleDdgiDirectionalRadianceMode.L2;
        settings.SimpleDdgiGlossyTransportMode =
            SimpleDdgiGlossyTransportMode.OneBounce;
        settings.EnableContentDependentFeaturesForConformance(
            DdgiContentFeature.ManyLightSampling |
            DdgiContentFeature.CurrentPoseGeometry |
            DdgiContentFeature.DirectionalRadiance);

        Assert.Multiple(() =>
        {
            Assert.That(settings.EffectiveSimpleDdgiManyLightSamplingEnabled, Is.True);
            Assert.That(settings.EffectiveSimpleDdgiDirectionalRadianceMode,
                Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
            Assert.That(settings.EffectiveSimpleDdgiGlossyTransportMode,
                Is.EqualTo(SimpleDdgiGlossyTransportMode.ReceiverOnly));
        });
    }

    [Test]
    public void UltraPreset_RetainsDirectionalOneBounceTransport()
    {
        var settings = new GlobalIlluminationSettings();
        settings.ApplyDdgiQualityTier(DdgiQualityTier.DdgiUltra);

        Assert.Multiple(() =>
        {
            Assert.That(settings.EffectiveSimpleDdgiDirectionalRadianceMode,
                Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
            Assert.That(settings.EffectiveSimpleDdgiGlossyTransportMode,
                Is.EqualTo(SimpleDdgiGlossyTransportMode.OneBounce));
        });
    }

    [Test]
    public void CertifiedTransport_PreservesQualifiedCurrentPoseMode()
    {
        var settings = new GlobalIlluminationSettings
        {
            DdgiSkinnedGeometryMode = DdgiSkinnedGeometryMode.CurrentPose,
            SimpleDdgiTransportV2Enabled = true,
            SimpleDdgiTransportTailCertificationEnabled = true
        };

        Assert.That(settings.EffectiveDdgiSkinnedGeometryMode,
            Is.EqualTo(DdgiSkinnedGeometryMode.CurrentPose));

        settings.ApplyContentDependentReleaseQualification(
            DdgiContentFeature.None);

        Assert.That(settings.EffectiveDdgiSkinnedGeometryMode,
            Is.EqualTo(DdgiSkinnedGeometryMode.Excluded));
    }

    [Test]
    public void RecursiveGlossy_RequiresItsIndependentQualificationFlag()
    {
        var settings = new GlobalIlluminationSettings
        {
            SimpleDdgiDirectionalRadianceMode = SimpleDdgiDirectionalRadianceMode.L2,
            SimpleDdgiGlossyTransportMode =
                SimpleDdgiGlossyTransportMode.RecursiveCertified
        };
        settings.ApplyContentDependentReleaseQualification(
            DdgiContentFeature.DirectionalRadiance |
            DdgiContentFeature.OneBounceGlossyTransport);

        Assert.That(settings.EffectiveSimpleDdgiGlossyTransportMode,
            Is.EqualTo(SimpleDdgiGlossyTransportMode.OneBounce));

        settings.ApplyContentDependentReleaseQualification(
            DdgiContentFeature.DirectionalRadiance |
            DdgiContentFeature.OneBounceGlossyTransport |
            DdgiContentFeature.RecursiveGlossyTransport);

        Assert.That(settings.EffectiveSimpleDdgiGlossyTransportMode,
            Is.EqualTo(SimpleDdgiGlossyTransportMode.RecursiveCertified));
    }

    [Test]
    public void GpuContentStructs_HaveFrozenSizes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUDdgiLightBufferState>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUDdgiLightTreeNode>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUDdgiLightTreeLeaf>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUDdgiLightTreeState>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiRadianceShL2>(), Is.EqualTo(64));
            Assert.That(
                Marshal.SizeOf<GPUDdgiRayQueryInstance>(),
                Is.EqualTo(DdgiRayQueryInstanceAbi.SizeInBytes));
            Assert.That(Marshal.OffsetOf<GPUDdgiRayQueryInstance>(
                nameof(GPUDdgiRayQueryInstance.WorldMatrixInverseTranspose)).ToInt32(),
                Is.EqualTo(96));
        });
    }

    [Test]
    public void LightBufferAndTreeStateChecksums_CoverHighRevisionWords()
    {
        const ulong lightRevision = 0x0123_4567_89AB_CDEFUL;
        const ulong topologyRevision = 0x1020_3040_5060_7080UL;
        const ulong contentRevision = 0xFFE0_D0C0_B0A0_9080UL;
        uint lightChecksum = GPUDdgiLightBufferState.ComputeChecksum(
            lightRevision,
            topologyRevision,
            contentRevision,
            lightCount: 73,
            localLightCount: 71);
        uint treeChecksum = GPUDdgiLightTreeState.ComputeValidationChecksum(
            publicationGeneration: 19,
            leafCount: 71,
            nodeCount: 255,
            lightRevision,
            topologyRevision,
            contentRevision);

        Assert.Multiple(() =>
        {
            Assert.That(lightChecksum, Is.Not.Zero);
            Assert.That(treeChecksum, Is.Not.Zero);
            Assert.That(
                GPUDdgiLightBufferState.ComputeChecksum(
                    lightRevision ^ (1UL << 47),
                    topologyRevision,
                    contentRevision,
                    73,
                    71),
                Is.Not.EqualTo(lightChecksum));
            Assert.That(
                GPUDdgiLightTreeState.ComputeValidationChecksum(
                    19,
                    71,
                    255,
                    lightRevision,
                    topologyRevision ^ (1UL << 61),
                    contentRevision),
                Is.Not.EqualTo(treeChecksum));
        });
    }

    [Test]
    public void ManyLightCounterDecode_ReportsPdfAndEstimatorWeightEvidence()
    {
        var counters = new DdgiManyLightGpuCounters(
            BypassHitCount: 0,
            ExactHitCount: 0,
            TreeAttemptHitCount: 1,
            TreeSuccessHitCount: 1,
            TreeFallbackHitCount: 0,
            SampledLightCount: 4,
            DuplicateDrawCount: 1,
            VisibilityEvaluationCount: 4,
            RejectedZeroTermCount: 0,
            UniformRepairCount: 0,
            InvalidSampleOrPdfCount: 0,
            QuantizedPdfSum: 1_048_576,
            QuantizedNegativeLog2PdfSum: 8_192,
            QuantizedMaximumNegativeLog2Pdf: 3_072,
            QuantizedMaximumEstimatorWeight: 12_288,
            ExactLightEvaluationCount: 0);

        Assert.Multiple(() =>
        {
            Assert.That(counters.MeanPdf, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(counters.GeometricMeanPdf,
                Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(counters.MinimumPdf,
                Is.EqualTo(0.125f).Within(1e-6f));
            Assert.That(counters.MaximumEstimatorWeight, Is.EqualTo(12f));
        });
    }

    [Test]
    public void ContentMemoryPlan_HasZeroCostBypassAndExactDirectionalParity()
    {
        var settings = new GlobalIlluminationSettings();
        settings.ApplyDdgiQualityTier(DdgiQualityTier.DdgiUltra);
        settings.EnableContentDependentFeaturesForConformance(
            DdgiContentFeature.ManyLightSampling |
            DdgiContentFeature.DirectionalRadiance |
            DdgiContentFeature.OneBounceGlossyTransport);

        settings.SimpleDdgiGlossyTransportMode =
            SimpleDdgiGlossyTransportMode.ReceiverOnly;

        SimpleDdgiContentMemoryPlan noLocals = SimpleDdgiContentMemoryPlan.Compile(
            settings,
            localLightCount: 0,
            physicalProbeCapacity: 1_000);
        settings.SimpleDdgiGlossyTransportMode = SimpleDdgiGlossyTransportMode.OneBounce;
        SimpleDdgiContentMemoryPlan oneBounce = SimpleDdgiContentMemoryPlan.Compile(
            settings,
            localLightCount: 64,
            physicalProbeCapacity: 1_000);

        Assert.Multiple(() =>
        {
            Assert.That(noLocals.LightTreePersistentBytes, Is.Zero);
            Assert.That(noLocals.LightTreeWorkBytes, Is.Zero);
            Assert.That(noLocals.DirectionalRadianceCanonicalBytes, Is.EqualTo(64_000UL));
            Assert.That(noLocals.DirectionalRadianceParityBytes, Is.Zero);
            Assert.That(oneBounce.DirectionalRadianceCanonicalBytes, Is.EqualTo(64_000UL));
            Assert.That(oneBounce.DirectionalRadianceParityBytes, Is.EqualTo(64_000UL));
            Assert.That(oneBounce.LightTreeNodeCount, Is.EqualTo(127));
            Assert.That(oneBounce.LightTreeNodeBytes, Is.EqualTo(127UL * 64UL * 2UL));
            Assert.That(oneBounce.LightTreeLeafBytes, Is.EqualTo(64UL * 32UL * 2UL));
        });
    }

    [Test]
    public void CoreMemoryPlan_ChargesExactlySixtyFourBytesPerL2Probe()
    {
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probeCount: 128,
            updateRequestCapacity: 32,
            rayCapacity: 32,
            sampledAtlasRequested: false,
            concreteTransportBuffers: false,
            readbackBufferCount: 0,
            directionalRadianceMode: SimpleDdgiDirectionalRadianceMode.L2,
            glossyTransportMode: SimpleDdgiGlossyTransportMode.ReceiverOnly,
            directionalRadianceBudgetBytes: 128UL * 64UL);

        Assert.Multiple(() =>
        {
            Assert.That(plan.DirectionalRadianceAdmitted, Is.True);
            Assert.That(plan.DirectionalRadianceCanonicalBytes, Is.EqualTo(8_192UL));
            Assert.That(plan.DirectionalRadianceParityBytes, Is.Zero);
            Assert.That(plan.DirectionalRadianceAbiVersion,
                Is.EqualTo(DdgiDirectionalRadianceAbi.L2));
        });
    }

    [Test]
    public void ContentMemoryPlan_AccountsExactGpuFoliageAbiPerFrameSlot()
    {
        var settings = new GlobalIlluminationSettings
        {
            DdgiFoliageGeometryMode =
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            DdgiFoliageProxyTriangleBudget = 1_024
        };
        settings.EnableContentDependentFeaturesForConformance(
            DdgiContentFeature.FoliageGeometry);

        SimpleDdgiContentMemoryPlan plan =
            SimpleDdgiContentMemoryPlan.Compile(
                settings,
                localLightCount: 0,
                physicalProbeCapacity: 0,
                foliageProxyTriangleCount: 40,
                foliageProxyPatchCount: 3,
                frameSlotCount: 2);

        Assert.Multiple(() =>
        {
            Assert.That(plan.FoliageProxyTriangleCapacity, Is.EqualTo(40));
            Assert.That(plan.FoliageProxyVertexBytes,
                Is.EqualTo(10UL * 8UL * 80UL * 2UL));
            Assert.That(plan.FoliageProxyIndexBytes,
                Is.EqualTo(10UL * 12UL * 4UL * 2UL));
            Assert.That(plan.FoliageProxyPatchBytes,
                Is.EqualTo(3UL * 80UL * 2UL));
            Assert.That(plan.FoliageProxyBytes,
                Is.EqualTo(
                    plan.FoliageProxyVertexBytes +
                    plan.FoliageProxyIndexBytes +
                    plan.FoliageProxyPatchBytes));
        });
    }

    [Test]
    public void LightTree_PdfsNormalizeAndStableOrderingIgnoresPackedPermutation()
    {
        DdgiLocalLightTreeInput[] ordered = CreateSymmetricLights(64, reversePackedOrder: false);
        DdgiLocalLightTreeInput[] permuted = CreateSymmetricLights(64, reversePackedOrder: true);
        SimpleDdgiLightTreeReference a = SimpleDdgiLightTreeReference.Build(ordered);
        SimpleDdgiLightTreeReference b = SimpleDdgiLightTreeReference.Build(permuted);
        Vector3 hit = Vector3.Zero;
        float pdfSum = 0f;
        for (int leaf = 0; leaf < a.LocalLightCount; leaf++)
            pdfSum += a.ComputeTreePdf(leaf, hit);

        var identity = new DdgiStochasticIdentity(
            77,
            4,
            2,
            1,
            DdgiStochasticDecisionDomain.LocalLightTreeTraversal);
        DdgiLightTreeSample sampleA = a.Sample(hit, identity, 0, 0.02f);
        DdgiLightTreeSample sampleB = b.Sample(hit, identity, 0, 0.02f);

        Assert.Multiple(() =>
        {
            Assert.That(pdfSum, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(a.Diagnostics.StableOrderHash,
                Is.EqualTo(b.Diagnostics.StableOrderHash));
            Assert.That(sampleA.StableLightIdentity,
                Is.EqualTo(sampleB.StableLightIdentity));
            Assert.That(sampleA.Pdf, Is.EqualTo(sampleB.Pdf).Within(1e-7f));
        });
    }

    [Test]
    public void LightTree_MixtureEstimatorContainsExactSumWithoutRadianceClamp()
    {
        DdgiLocalLightTreeInput[] lights = CreateSymmetricLights(32, reversePackedOrder: false);
        SimpleDdgiLightTreeReference tree = SimpleDdgiLightTreeReference.Build(lights);
        const int draws = 40_000;
        double accumulated = 0;
        for (int draw = 0; draw < draws; draw++)
        {
            var identity = new DdgiStochasticIdentity(
                (ulong)(draw + 1),
                (uint)draw,
                9,
                1,
                DdgiStochasticDecisionDomain.LocalLightTreeTraversal);
            DdgiLightTreeSample sample = tree.Sample(Vector3.Zero, identity, 0, 0.02f);
            Assert.That(sample.HasSample, Is.True);
            double contribution = 0.25 + (sample.StableLightIdentity % 11) * 0.125;
            accumulated += contribution / sample.Pdf;
        }

        double exact = lights.Sum(static light =>
            0.25 + (light.StableLightIdentity % 11) * 0.125);
        double estimate = accumulated / draws;
        Assert.That(estimate, Is.EqualTo(exact).Within(exact * 0.02));
    }

    [Test]
    public void RadianceSh_ConstantFieldProjectsToDcAndRoundTripsPackedRecord()
    {
        const int sampleCount = 8_192;
        var samples = new SimpleDdgiRadianceSample[sampleCount];
        Vector3 constant = new(2f, 0.5f, 4f);
        float goldenAngle = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int index = 0; index < sampleCount; index++)
        {
            float y = 1f - 2f * ((index + 0.5f) / sampleCount);
            float radius = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            float phi = index * goldenAngle;
            samples[index] = new SimpleDdgiRadianceSample(
                new Vector3(MathF.Cos(phi) * radius, y, MathF.Sin(phi) * radius),
                constant);
        }

        SimpleDdgiRadianceProjection projection = SimpleDdgiRadianceShL2.Project(samples);
        bool packed = SimpleDdgiRadianceShL2.TryPack(
            projection.Coefficients,
            slotGeneration: 37,
            validSampleCount: projection.ValidSampleCount,
            qualityLevel: 3,
            hasHistory: true,
            out GPUSimpleDdgiRadianceShL2 record);
        bool unpacked = SimpleDdgiRadianceShL2.TryUnpack(
            record,
            expectedSlotGeneration: 37,
            out Vector3[] coefficients,
            out int storedSamples,
            out int quality,
            out bool history);

        float dcScale = MathF.Sqrt(4f * MathF.PI);
        Assert.Multiple(() =>
        {
            Assert.That(projection.Coefficients[0].X,
                Is.EqualTo(constant.X * dcScale).Within(2e-3f));
            Assert.That(projection.Coefficients[0].Y,
                Is.EqualTo(constant.Y * dcScale).Within(2e-3f));
            Assert.That(projection.Coefficients[0].Z,
                Is.EqualTo(constant.Z * dcScale).Within(3e-3f));
            Assert.That(projection.Coefficients.Skip(1).Max(static value => value.Length()),
                Is.LessThan(0.01f));
            Assert.That(packed, Is.True);
            Assert.That(unpacked, Is.True);
            Assert.That(coefficients[0].X,
                Is.EqualTo(projection.Coefficients[0].X).Within(0.01f));
            Assert.That(storedSamples, Is.EqualTo(255));
            Assert.That(quality, Is.EqualTo(3));
            Assert.That(history, Is.True);
        });
    }

    [Test]
    public void RadianceSh_FailsClosedOnOverflowGenerationMismatchAndCorruption()
    {
        Vector3[] coefficients = Enumerable.Repeat(Vector3.One, 9).ToArray();
        coefficients[4] = new Vector3(70_000f, 0f, 0f);
        Assert.That(SimpleDdgiRadianceShL2.TryPack(
            coefficients, 1, 32, 1, false, out _), Is.False);

        coefficients[4] = Vector3.One;
        Assert.That(SimpleDdgiRadianceShL2.TryPack(
            coefficients, 2, 32, 1, false, out GPUSimpleDdgiRadianceShL2 record), Is.True);
        Assert.That(SimpleDdgiRadianceShL2.TryUnpack(
            record, 3, out _, out _, out _, out _), Is.False);
        record.Word5 ^= 1u;
        Assert.That(SimpleDdgiRadianceShL2.TryUnpack(
            record, 2, out _, out _, out _, out _), Is.False);
    }

    [Test]
    public void RadianceSh_ZeroSamplePublicationCommitsButHasNoReceiverOwnership()
    {
        Vector3[] coefficients = new Vector3[SimpleDdgiRadianceShL2.CoefficientCount];
        bool packed = SimpleDdgiRadianceShL2.TryPack(
            coefficients,
            slotGeneration: 7,
            validSampleCount: 0,
            qualityLevel: 0,
            hasHistory: false,
            out GPUSimpleDdgiRadianceShL2 record);
        bool structurallyValid = SimpleDdgiRadianceShL2.TryUnpack(
            record,
            expectedSlotGeneration: 7,
            out _,
            out int validSampleCount,
            out _,
            out _);
        bool receiverValid = SimpleDdgiRadianceShL2.TryEvaluateRecord(
            record,
            expectedSlotGeneration: 7,
            direction: Vector3.UnitZ,
            perceptualRoughness: 0.8f,
            out Vector3 radiance,
            out Vector3 negativeReconstruction);
        DdgiIndirectSpecularOwnership ownership =
            DdgiIndirectSpecularSelector.Select(
                screenOrGeometricConfidence: 0f,
                localReflectionProbeConfidence: 0f,
                ddgiConfidence: receiverValid ? 1f : 0f,
                perceptualRoughness: 0.8f,
                ddgiMinimumRoughness: 0.55f,
                ddgiFullWeightRoughness: 0.70f);

        Assert.Multiple(() =>
        {
            Assert.That(packed, Is.True);
            Assert.That(structurallyValid, Is.True);
            Assert.That(validSampleCount, Is.Zero);
            Assert.That(receiverValid, Is.False);
            Assert.That(radiance, Is.EqualTo(Vector3.Zero));
            Assert.That(negativeReconstruction, Is.EqualTo(Vector3.Zero));
            Assert.That(ownership.DdgiDirectionalRadianceWeight, Is.Zero);
            Assert.That(ownership.EnvironmentWeight, Is.EqualTo(1f));
            Assert.That(ownership.Sum, Is.EqualTo(1f));
        });
    }

    [Test]
    public void GgxTablePreservesDcAndSpecularOwnershipAlwaysSumsToOne()
    {
        for (int index = 0; index <= 100; index++)
        {
            Vector3 bands = SimpleDdgiGgxBandScaleTable.Evaluate(index / 100f);
            Assert.That(bands.X, Is.EqualTo(1f).Within(1e-7f));
            Assert.That(bands.Y, Is.InRange(0f, 1f));
            Assert.That(bands.Z, Is.InRange(0f, 1f));
        }

        DdgiIndirectSpecularOwnership sharp = DdgiIndirectSpecularSelector.Select(
            0f, 0f, 1f, 0.4f, 0.55f, 0.70f);
        DdgiIndirectSpecularOwnership rough = DdgiIndirectSpecularSelector.Select(
            0.25f, 0.5f, 1f, 0.9f, 0.55f, 0.70f);
        Assert.Multiple(() =>
        {
            Assert.That(sharp.DdgiDirectionalRadianceWeight, Is.Zero);
            Assert.That(sharp.EnvironmentWeight, Is.EqualTo(1f));
            Assert.That(rough.Sum, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(rough.ScreenOrGeometricWeight, Is.EqualTo(0.25f));
            Assert.That(rough.LocalReflectionProbeWeight, Is.EqualTo(0.375f));
            Assert.That(rough.DdgiDirectionalRadianceWeight, Is.EqualTo(0.375f));
            Assert.That(rough.EnvironmentWeight, Is.Zero.Within(1e-6f));
        });
    }

    private static DdgiLocalLightTreeInput[] CreateSymmetricLights(
        int count,
        bool reversePackedOrder)
    {
        var result = new DdgiLocalLightTreeInput[count];
        for (int index = 0; index < count; index++)
        {
            float angle = 2f * MathF.PI * index / count;
            int packedIndex = reversePackedOrder ? count - 1 - index : index;
            result[packedIndex] = new DdgiLocalLightTreeInput(
                packedIndex,
                StableLightIdentity: checked((uint)(index + 1)),
                LightBufferRevision: 7,
                Position: new Vector3(MathF.Cos(angle) * 2f, 0f, MathF.Sin(angle) * 2f),
                Color: Vector3.One,
                Intensity: 1f,
                Range: 8f,
                Direction: -Vector3.UnitY,
                SpotAngle: 0f,
                Type: LightType.Point);
        }
        return result;
    }
}
