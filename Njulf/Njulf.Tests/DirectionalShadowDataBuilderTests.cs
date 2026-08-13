using System;
using Njulf.Core.Camera;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class DirectionalShadowDataBuilderTests
{
    [Test]
    public void CalculateCascadeSplits_AreMonotonicAndEndAtFarPlane()
    {
        float[] splits = DirectionalShadowDataBuilder.CalculateCascadeSplits(0.1f, 80f, 4);

        Assert.Multiple(() =>
        {
            Assert.That(splits[0], Is.GreaterThan(0.1f));
            Assert.That(splits[1], Is.GreaterThan(splits[0]));
            Assert.That(splits[2], Is.GreaterThan(splits[1]));
            Assert.That(splits[3], Is.EqualTo(80f).Within(0.0001f));
        });
    }

    [Test]
    public void CalculateCascadeSplits_ExposesUniformToLogarithmicDistribution()
    {
        float[] uniform = DirectionalShadowDataBuilder.CalculateCascadeSplits(
            1f, 101f, 4, splitLambda: 0f);
        float[] logarithmic = DirectionalShadowDataBuilder.CalculateCascadeSplits(
            1f, 101f, 4, splitLambda: 1f);

        Assert.Multiple(() =>
        {
            Assert.That(uniform[0], Is.EqualTo(26f).Within(0.0001f));
            Assert.That(logarithmic[0], Is.LessThan(uniform[0]));
            Assert.That(uniform[3], Is.EqualTo(101f).Within(0.0001f));
            Assert.That(logarithmic[3], Is.EqualTo(101f).Within(0.0001f));
        });
    }

    [Test]
    public void PersistentBasis_RemainsContinuousAcrossLegacyUpAxisThreshold()
    {
        var state = new DirectionalShadowStabilizationState();
        var settings = new ShadowSettings { DirectionalCascadeCount = 1 };
        var camera = CreateCamera();

        DirectionalShadowDataBuilder.Build(
            camera,
            NumericsVector3.Normalize(new NumericsVector3(0.313f, -0.949f, 0.02f)),
            settings,
            0,
            1f,
            state,
            stableLightIdentity: 17UL,
            shadowResourceGeneration: 1u);
        CoreVector3 firstUp = state.Diagnostics[0].BasisUp;

        DirectionalShadowDataBuilder.Build(
            camera,
            NumericsVector3.Normalize(new NumericsVector3(0.307f, -0.952f, 0.02f)),
            settings,
            0,
            1f,
            state,
            stableLightIdentity: 17UL,
            shadowResourceGeneration: 1u);
        DirectionalShadowCascadeFitDiagnostics second = state.Diagnostics[0];

        Assert.Multiple(() =>
        {
            Assert.That(CoreVector3.Dot(firstUp, second.BasisUp), Is.GreaterThan(0.999f));
            Assert.That(MathF.Abs(CoreVector3.Dot(second.LightDirection, second.BasisUp)),
                Is.LessThan(1.0e-5f));
            Assert.That(second.ResetReason,
                Is.EqualTo(DirectionalShadowStabilizationResetReason.None));
        });
    }

    [Test]
    public void PersistentFitter_ReplaysDeterministicallyAndSnapsSubTexelTranslation()
    {
        var settings = new ShadowSettings
        {
            DirectionalCascadeCount = 1,
            DirectionalShadowMapSize = 2048,
            PcfRadius = 2
        };
        NumericsVector3 light = NumericsVector3.Normalize(
            new NumericsVector3(0.35f, -0.8f, 0.48f));

        (GPUShadowData firstA, GPUShadowData secondA) = RunTrack(new DirectionalShadowStabilizationState());
        (GPUShadowData firstB, GPUShadowData secondB) = RunTrack(new DirectionalShadowStabilizationState());

        Assert.Multiple(() =>
        {
            AssertMatrixEqual(firstA.LightViewProjection0, firstB.LightViewProjection0);
            AssertMatrixEqual(secondA.LightViewProjection0, secondB.LightViewProjection0);
            AssertMatrixEqual(firstA.LightViewProjection0, secondA.LightViewProjection0);
        });

        (GPUShadowData, GPUShadowData) RunTrack(DirectionalShadowStabilizationState state)
        {
            FirstPersonCamera camera = CreateCamera();
            GPUShadowData first = DirectionalShadowDataBuilder.Build(
                camera, light, settings, 0, 1f, state, 5UL, 3u);
            DirectionalShadowCascadeFitDiagnostics fit = state.Diagnostics[0];
            float lightSpaceShift = MathF.Abs(fit.SnappedCenterX - fit.RawCenterX) >
                                    fit.WorldTexelSize * 0.01f
                ? (fit.SnappedCenterX - fit.RawCenterX) * 0.5f
                : fit.WorldTexelSize * 0.1f;
            camera.Position += fit.BasisRight * lightSpaceShift;
            camera.Update();
            GPUShadowData second = DirectionalShadowDataBuilder.Build(
                camera, light, settings, 0, 1f, state, 5UL, 3u);
            return (first, second);
        }
    }

    [Test]
    public void PersistentFitter_ResetsOnStableLightIdentityChange()
    {
        var state = new DirectionalShadowStabilizationState();
        var settings = new ShadowSettings { DirectionalCascadeCount = 1 };
        FirstPersonCamera camera = CreateCamera();
        NumericsVector3 light = NumericsVector3.Normalize(new NumericsVector3(0.2f, -1f, 0.1f));

        DirectionalShadowDataBuilder.Build(camera, light, settings, 0, 1f, state, 9UL, 1u);
        DirectionalShadowDataBuilder.Build(camera, light, settings, 0, 1f, state, 10UL, 1u);

        Assert.That(state.Diagnostics[0].ResetReason,
            Is.EqualTo(DirectionalShadowStabilizationResetReason.LightIdentityChanged));
    }

    [Test]
    public void StabilizedDepth_ContractsAcrossQuantizationBoundaries()
    {
        var state = new DirectionalShadowStabilizationState();
        CoreVector3 direction = new(0.2f, -1f, 0.1f);
        state.BeginFrame(1UL, direction, 2UL);
        state.StabilizeDepth(
            cascade: 0,
            rawMinimum: 0.0,
            rawMaximum: 100.0,
            depthQuantum: 1.0,
            out _,
            out _);

        float contractedMinimum = 0f;
        float contractedMaximum = 100f;
        for (int frame = 0; frame < 20; frame++)
        {
            state.BeginFrame(1UL, direction, 2UL);
            state.StabilizeDepth(
                cascade: 0,
                rawMinimum: 10.0,
                rawMaximum: 90.0,
                depthQuantum: 1.0,
                out contractedMinimum,
                out contractedMaximum);
        }

        Assert.Multiple(() =>
        {
            Assert.That(contractedMinimum, Is.GreaterThan(0f));
            Assert.That(contractedMaximum, Is.LessThan(100f));
            Assert.That(contractedMinimum, Is.LessThanOrEqualTo(10f));
            Assert.That(contractedMaximum, Is.GreaterThanOrEqualTo(90f));
        });
    }

    [Test]
    public void Build_ProducesFiniteMatricesAndExpectedIndices()
    {
        var camera = CreateCamera();
        var settings = new ShadowSettings
        {
            DirectionalCascadeCount = 3,
            DirectionalShadowMapSize = 1024
        };

        GPUShadowData data = DirectionalShadowDataBuilder.Build(
            camera,
            new NumericsVector3(0.2f, -1f, -0.3f),
            settings,
            selectedLightIndex: 2,
            shadowStrength: 0.65f);

        Assert.Multiple(() =>
        {
            AssertMatrixFinite(data.LightViewProjection0);
            AssertMatrixFinite(data.LightViewProjection1);
            AssertMatrixFinite(data.LightViewProjection2);
            Assert.That(data.Indices.X, Is.EqualTo(1f));
            Assert.That(data.Indices.Y, Is.EqualTo(3f));
            Assert.That(data.Indices.W, Is.EqualTo(2f));
            Assert.That(data.Settings.X, Is.EqualTo(0.65f));
            Assert.That(data.Settings.Z, Is.EqualTo(1024f));
            Assert.That(data.CascadeTransitionData.X, Is.EqualTo(settings.DirectionalCascadeBlendFraction));
            Assert.That(data.CascadeTransitionData.Y, Is.EqualTo(camera.NearPlane));
            Assert.That(data.CascadeTransitionData.Z, Is.EqualTo(settings.MaxShadowDistance));
        });
    }

    [Test]
    public void FittedCameraSliceCorners_ProjectInsideTheirOwnCascade()
    {
        var camera = CreateCamera();
        var settings = new ShadowSettings
        {
            DirectionalCascadeCount = 4,
            DirectionalShadowMapSize = 2048,
            DirectionalCascadeBlendFraction = 0.12f
        };
        GPUShadowData data = DirectionalShadowDataBuilder.Build(
            camera,
            new NumericsVector3(0.35f, -0.8f, 0.48f),
            settings,
            selectedLightIndex: 0,
            shadowStrength: 1.0f);
        float[] splits = DirectionalShadowDataBuilder.CalculateCascadeSplits(
            camera.NearPlane,
            settings.MaxShadowDistance,
            settings.DirectionalCascadeCount);

        for (int cascade = 0; cascade < settings.DirectionalCascadeCount; cascade++)
        {
            float near = cascade == 0 ? camera.NearPlane : splits[cascade - 1];
            float far = splits[cascade];
            CoreMatrix4x4 matrix = DirectionalShadowClipReference.GetCascadeMatrix(data, cascade);
            foreach (CoreVector3 corner in DirectionalShadowDataBuilder.BuildFrustumCorners(camera, near, far))
            {
                DirectionalShadowClipReferenceResult result =
                    DirectionalShadowClipReference.EvaluateSphere(corner, 0.0f, matrix);
                Assert.That(result.Accepted, Is.True,
                    $"cascade {cascade} corner {corner} clip={result.ClipCenter}");
            }
        }
    }

    [Test]
    public void DirectClipReference_AcceptsSpheresTouchingEveryVulkanBoundary()
    {
        const float radius = 0.25f;
        var expectedCentres = new (DirectionalShadowClipBoundary Boundary, CoreVector3 Centre)[]
        {
            (DirectionalShadowClipBoundary.Left, new CoreVector3(-1.0f - radius, 0.0f, 0.5f)),
            (DirectionalShadowClipBoundary.Right, new CoreVector3(1.0f + radius, 0.0f, 0.5f)),
            (DirectionalShadowClipBoundary.Bottom, new CoreVector3(0.0f, -1.0f - radius, 0.5f)),
            (DirectionalShadowClipBoundary.Top, new CoreVector3(0.0f, 1.0f + radius, 0.5f)),
            (DirectionalShadowClipBoundary.Near, new CoreVector3(0.0f, 0.0f, -radius)),
            (DirectionalShadowClipBoundary.Far, new CoreVector3(0.0f, 0.0f, 1.0f + radius))
        };

        foreach ((DirectionalShadowClipBoundary boundary, CoreVector3 centre) in expectedCentres)
        {
            DirectionalShadowClipReferenceResult result =
                DirectionalShadowClipReference.EvaluateSphere(centre, radius, CoreMatrix4x4.Identity);
            Assert.Multiple(() =>
            {
                Assert.That(result.Accepted, Is.True, boundary.ToString());
                Assert.That(result.FirstRejectingBoundary, Is.Null, boundary.ToString());
                Assert.That(result.GetSignedDistance(boundary), Is.EqualTo(-radius).Within(1.0e-6f));
            });
        }
    }

    [Test]
    public void DirectClipReference_RejectsSpheresClearlyBeyondEveryVulkanBoundary()
    {
        const float radius = 0.25f;
        const float outside = 0.05f;
        var expectedCentres = new (DirectionalShadowClipBoundary Boundary, CoreVector3 Centre)[]
        {
            (DirectionalShadowClipBoundary.Left, new CoreVector3(-1.0f - radius - outside, 0.0f, 0.5f)),
            (DirectionalShadowClipBoundary.Right, new CoreVector3(1.0f + radius + outside, 0.0f, 0.5f)),
            (DirectionalShadowClipBoundary.Bottom, new CoreVector3(0.0f, -1.0f - radius - outside, 0.5f)),
            (DirectionalShadowClipBoundary.Top, new CoreVector3(0.0f, 1.0f + radius + outside, 0.5f)),
            (DirectionalShadowClipBoundary.Near, new CoreVector3(0.0f, 0.0f, -radius - outside)),
            (DirectionalShadowClipBoundary.Far, new CoreVector3(0.0f, 0.0f, 1.0f + radius + outside))
        };

        foreach ((DirectionalShadowClipBoundary boundary, CoreVector3 centre) in expectedCentres)
        {
            DirectionalShadowClipReferenceResult result =
                DirectionalShadowClipReference.EvaluateSphere(centre, radius, CoreMatrix4x4.Identity);
            Assert.Multiple(() =>
            {
                Assert.That(result.Accepted, Is.False, boundary.ToString());
                Assert.That(result.FirstRejectingBoundary, Is.EqualTo(boundary), boundary.ToString());
                Assert.That(result.GetSignedDistance(boundary), Is.LessThan(-radius));
            });
        }
    }

    [Test]
    public void DirectClipReference_MatchesIndependentPlaneExtractionForRandomizedFixtures()
    {
        var random = new Random(7331);
        for (int sample = 0; sample < 512; sample++)
        {
            CoreMatrix4x4 matrix = CreateRandomSupportedMatrix(random);
            var centre = new CoreVector3(
                NextRange(random, -12.0f, 12.0f),
                NextRange(random, -12.0f, 12.0f),
                NextRange(random, -12.0f, 12.0f));
            float radius = NextRange(random, 0.0f, 2.0f);

            DirectionalShadowClipReferenceResult direct =
                DirectionalShadowClipReference.EvaluateSphere(centre, radius, matrix);
            bool extractedPlanesAccept = ExtractedPlanesAcceptSphere(centre, radius, matrix);
            Assert.That(direct.Accepted, Is.EqualTo(extractedPlanesAccept),
                $"sample {sample}, clip={direct.ClipCenter}");
        }
    }

    [Test]
    public void NonUniformInstanceScale_UsesLongestAxisForConservativeWorldRadius()
    {
        const float localRadius = 1.25f;
        CoreMatrix4x4 transform =
            CoreMatrix4x4.CreateScale(new CoreVector3(0.5f, 3.0f, 1.75f)) *
            CoreMatrix4x4.CreateRotationY(0.73f);

        float radius = DirectionalShadowClipReference.ComputeConservativeWorldRadius(localRadius, transform);

        Assert.That(radius, Is.EqualTo(localRadius * 3.0f).Within(1.0e-5f));
    }

    [Test]
    public void Build_ExtremeSupportedCameraAndLightOrientationsRemainFinite()
    {
        var camera = new FirstPersonCamera(new CoreVector3(8000.0f, -2500.0f, 1200.0f), 2.9f, 1.54f)
        {
            FieldOfView = MathF.PI * 0.98f,
            AspectRatio = 3.5f,
            NearPlane = 0.001f,
            FarPlane = 5000.0f
        };
        camera.Update();
        var settings = new ShadowSettings
        {
            DirectionalCascadeCount = 4,
            DirectionalShadowMapSize = 4096,
            MaxShadowDistance = 4000.0f
        };

        GPUShadowData data = DirectionalShadowDataBuilder.Build(
            camera,
            new NumericsVector3(0.00001f, -0.99999f, 0.00001f),
            settings,
            selectedLightIndex: 0,
            shadowStrength: 1.0f);

        Assert.Multiple(() =>
        {
            AssertMatrixFinite(data.LightViewProjection0);
            AssertMatrixFinite(data.LightViewProjection1);
            AssertMatrixFinite(data.LightViewProjection2);
            AssertMatrixFinite(data.LightViewProjection3);
        });
    }

    [Test]
    public void DirectionalShadowCasterDiagnostics_DecodesBoundedExactAttributionRecord()
    {
        uint[] counters = new uint[RendererDiagnosticsBuffer.CounterCount];
        int header = RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticCounterBase;
        counters[header + 0] = (uint)RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticRecordCapacity + 3u;
        counters[header + 1] = 19u;
        counters[header + 2] = 3u;
        counters[header + 3] = 0x89abcdefu;
        counters[header + 4] = 0x01234567u;
        counters[header + 5] = 91u;
        counters[header + 6] = RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticFrameMetadataMagic;
        int record = header + RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticHeaderWordCount;
        counters[record + 0] = 71u;
        counters[record + 1] = 71u;
        counters[record + 2] = 991u;
        counters[record + 3] = 0u;
        counters[record + 4] = (uint)DirectionalShadowCasterClass.Static;
        counters[record + 5] = 2u;
        counters[record + 6] = 12u;
        counters[record + 7] = 0x37u;
        counters[record + 8] = 0xdecafbadu;
        counters[record + 9] = 0u;
        counters[record + 10] = 0u;
        counters[record + 11] = (uint)DirectionalShadowClipBoundary.Top;
        counters[record + 12] = BitConverter.SingleToUInt32Bits(-2.5f);
        counters[record + 13] = BitConverter.SingleToUInt32Bits(1.0f);
        counters[record + 14] = BitConverter.SingleToUInt32Bits(2.0f);
        counters[record + 15] = BitConverter.SingleToUInt32Bits(3.0f);
        counters[record + 16] = BitConverter.SingleToUInt32Bits(4.0f);
        counters[record + 17] = BitConverter.SingleToUInt32Bits(0.1f);
        counters[record + 18] = BitConverter.SingleToUInt32Bits(0.2f);
        counters[record + 19] = BitConverter.SingleToUInt32Bits(0.3f);
        counters[record + 20] = BitConverter.SingleToUInt32Bits(1.0f);
        for (int plane = 0; plane < 6; plane++)
            counters[record + 21 + plane] = BitConverter.SingleToUInt32Bits(plane - 2.0f);

        DirectionalShadowCasterDiagnostics decoded =
            RendererDiagnosticsBuffer.DecodeDirectionalShadowCasterDiagnostics(counters);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.ReadbackValid, Is.EqualTo(1));
            Assert.That(decoded.SampledCandidateCount, Is.EqualTo(19u));
            Assert.That(decoded.DroppedRecordCount, Is.EqualTo(3u));
            Assert.That(decoded.GpuFrameSerial, Is.EqualTo(0x0123456789abcdefUL));
            Assert.That(decoded.GpuResourceGeneration, Is.EqualTo(91u));
            Assert.That(decoded.FrameMetadataValid, Is.EqualTo(1));
            Assert.That(decoded.Records, Has.Length.EqualTo(RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticRecordCapacity));
            Assert.That(decoded.Records[0].ObjectId, Is.EqualTo(71u));
            Assert.That(decoded.Records[0].MeshletId, Is.EqualTo(991u));
            Assert.That(decoded.Records[0].CasterClass, Is.EqualTo(DirectionalShadowCasterClass.Static));
            Assert.That(decoded.Records[0].CascadeIndex, Is.EqualTo(2u));
            Assert.That(decoded.Records[0].Accepted, Is.Zero);
            Assert.That(decoded.Records[0].FirstRejectingPlane, Is.EqualTo((int)DirectionalShadowClipBoundary.Top));
            Assert.That(decoded.Records[0].FirstRejectingSignedDistance, Is.EqualTo(-2.5f));
            Assert.That(decoded.Records[0].WorldCenter, Is.EqualTo(new CoreVector3(1.0f, 2.0f, 3.0f)));
            Assert.That(decoded.Records[0].WorldRadius, Is.EqualTo(4.0f));
            Assert.That(decoded.Records[0].SignedPlaneDistances[3], Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void DirectionalShadowCasterDiagnostics_AttachesIndependentCpuEvidenceToSameFrameMatrix()
    {
        var shadowData = new GPUShadowData
        {
            LightViewProjection0 = CoreMatrix4x4.Identity,
            LightViewProjection1 = CoreMatrix4x4.Identity,
            LightViewProjection2 = CoreMatrix4x4.Identity,
            LightViewProjection3 = CoreMatrix4x4.Identity
        };
        CoreVector3 centre = new(0.0f, 0.0f, 0.5f);
        DirectionalShadowClipReferenceResult cpu =
            DirectionalShadowClipReference.EvaluateSphere(centre, 0.25f, CoreMatrix4x4.Identity);
        var attribution = new DirectionalShadowCasterAttribution(
            ObjectId: 5u,
            InstanceId: 5u,
            MeshletId: 9u,
            SelectedLod: 0u,
            CasterClass: DirectionalShadowCasterClass.Static,
            CascadeIndex: 0u,
            CandidateIndex: 0u,
            EligibilityFlags: 0x37u,
            MatrixHash: DirectionalShadowClipReference.ComputeMatrixHash32(CoreMatrix4x4.Identity),
            Accepted: 1,
            FirstRejectingPlane: -1,
            FirstRejectingSignedDistance: 0.25f,
            WorldCenter: centre,
            WorldRadius: 0.25f,
            ClipCenter: cpu.ClipCenter,
            SignedPlaneDistances:
            [
                cpu.LeftSignedDistance,
                cpu.RightSignedDistance,
                cpu.BottomSignedDistance,
                cpu.TopSignedDistance,
                cpu.NearSignedDistance,
                cpu.FarSignedDistance
            ]);
        var diagnostics = new DirectionalShadowCasterDiagnostics(1, 1u, 0u, [attribution])
        {
            GpuFrameSerial = 77UL,
            GpuResourceGeneration = 4u,
            FrameMetadataValid = 1
        };
        DirectionalShadowCasterFrameCapture capture = DirectionalShadowCasterFrameCapture.Create(
            frameSerial: 77UL,
            resourceGeneration: 4u,
            cascadeCount: 1,
            cameraPosition: CoreVector3.Zero,
            lightDirection: new CoreVector3(0.0f, -1.0f, 0.0f),
            shadowData: shadowData);

        DirectionalShadowCasterDiagnostics joined =
            DirectionalShadowCasterDiagnosticsEvaluator.AttachCpuReference(diagnostics, capture);

        Assert.Multiple(() =>
        {
            Assert.That(joined.Records[0].CpuReferenceAvailable, Is.EqualTo(1));
            Assert.That(joined.Records[0].MatrixMatchesCapturedBytes, Is.EqualTo(1));
            Assert.That(joined.Records[0].ClipCoordinatesMatch, Is.EqualTo(1));
            Assert.That(joined.Records[0].CpuGpuDecisionMatches, Is.EqualTo(1));
            Assert.That(joined.Records[0].FrameGenerationMatchesCapturedSlot, Is.EqualTo(1));
            Assert.That(joined.Records[0].FrameSerial, Is.EqualTo(77UL));
            Assert.That(joined.Records[0].ResourceGeneration, Is.EqualTo(4u));
            Assert.That(joined.Records[0].CpuReference.Accepted, Is.True);
        });
    }

    [Test]
    public void DirectionalShadowCasterDiagnostics_RejectsMismatchedFrameOwnershipOrCapturedBytes()
    {
        var shadowData = new GPUShadowData
        {
            LightViewProjection0 = CoreMatrix4x4.Identity,
            LightViewProjection1 = CoreMatrix4x4.Identity,
            LightViewProjection2 = CoreMatrix4x4.Identity,
            LightViewProjection3 = CoreMatrix4x4.Identity
        };
        CoreVector3 centre = new(0.0f, 0.0f, 0.5f);
        DirectionalShadowClipReferenceResult cpu =
            DirectionalShadowClipReference.EvaluateSphere(centre, 0.25f, CoreMatrix4x4.Identity);
        var attribution = new DirectionalShadowCasterAttribution(
            ObjectId: 5u,
            InstanceId: 5u,
            MeshletId: 9u,
            SelectedLod: 0u,
            CasterClass: DirectionalShadowCasterClass.Static,
            CascadeIndex: 0u,
            CandidateIndex: 0u,
            EligibilityFlags: 0x37u,
            MatrixHash: DirectionalShadowClipReference.ComputeMatrixHash32(CoreMatrix4x4.Identity),
            Accepted: 1,
            FirstRejectingPlane: -1,
            FirstRejectingSignedDistance: 0.25f,
            WorldCenter: centre,
            WorldRadius: 0.25f,
            ClipCenter: cpu.ClipCenter,
            SignedPlaneDistances: []);
        var diagnostics = new DirectionalShadowCasterDiagnostics(1, 1u, 0u, [attribution])
        {
            GpuFrameSerial = 77UL,
            GpuResourceGeneration = 4u,
            FrameMetadataValid = 1
        };
        DirectionalShadowCasterFrameCapture capture = DirectionalShadowCasterFrameCapture.Create(
            frameSerial: 77UL,
            resourceGeneration: 4u,
            cascadeCount: 1,
            cameraPosition: CoreVector3.Zero,
            lightDirection: new CoreVector3(0.0f, -1.0f, 0.0f),
            shadowData: shadowData);

        byte[] corruptBytes = (byte[])capture.ShadowDataBytes.Clone();
        corruptBytes[0] ^= 0x1;
        DirectionalShadowCasterDiagnostics corruptBytesJoined =
            DirectionalShadowCasterDiagnosticsEvaluator.AttachCpuReference(
                diagnostics,
                capture with { ShadowDataBytes = corruptBytes });
        DirectionalShadowCasterDiagnostics wrongFrameJoined =
            DirectionalShadowCasterDiagnosticsEvaluator.AttachCpuReference(
                diagnostics with { GpuFrameSerial = 78UL },
                capture);

        Assert.Multiple(() =>
        {
            Assert.That(corruptBytesJoined.Records[0].FrameGenerationMatchesCapturedSlot, Is.EqualTo(1));
            Assert.That(corruptBytesJoined.Records[0].MatrixMatchesCapturedBytes, Is.Zero);
            Assert.That(corruptBytesJoined.Records[0].CpuGpuDecisionMatches, Is.Zero);
            Assert.That(wrongFrameJoined.Records[0].FrameGenerationMatchesCapturedSlot, Is.Zero);
            Assert.That(wrongFrameJoined.Records[0].CpuReferenceAvailable, Is.Zero);
            Assert.That(wrongFrameJoined.Records[0].CpuGpuDecisionMatches, Is.Zero);
        });
    }

    [Test]
    public void ShadowDataUpload_SynchronizesComputeAndGraphicsConsumers()
    {
        const PipelineStageFlags2 expected =
            PipelineStageFlags2.ComputeShaderBit |
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.FragmentShaderBit;

        Assert.That(DirectionalShadowResources.ShadowDataConsumerStages, Is.EqualTo(expected));
    }

    [Test]
    public void ShadowDepthTransitions_SynchronizeEarlyAndLateAttachmentAccess()
    {
        DirectionalShadowPass.GetTransitionMasks(
            ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            out PipelineStageFlags2 sourceStage,
            out AccessFlags2 sourceAccess,
            out _,
            out _);
        DirectionalShadowPass.GetTransitionMasks(
            ImageLayout.TransferDstOptimal,
            ImageLayout.DepthStencilAttachmentOptimal,
            out _,
            out _,
            out PipelineStageFlags2 destinationStage,
            out AccessFlags2 destinationAccess);

        const PipelineStageFlags2 expectedStages =
            PipelineStageFlags2.EarlyFragmentTestsBit |
            PipelineStageFlags2.LateFragmentTestsBit;
        const AccessFlags2 expectedAccess =
            AccessFlags2.DepthStencilAttachmentReadBit |
            AccessFlags2.DepthStencilAttachmentWriteBit;
        Assert.Multiple(() =>
        {
            Assert.That(sourceStage, Is.EqualTo(expectedStages));
            Assert.That(sourceAccess, Is.EqualTo(expectedAccess));
            Assert.That(destinationStage, Is.EqualTo(expectedStages));
            Assert.That(destinationAccess, Is.EqualTo(expectedAccess));
        });
    }

    [Test]
    public void PointShadowDepthTransitions_SynchronizeLoadedAttachmentReadsAndWrites()
    {
        PointShadowPass.GetTransitionMasks(
            ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            out PipelineStageFlags2 sourceStage,
            out AccessFlags2 sourceAccess,
            out _,
            out _);
        PointShadowPass.GetTransitionMasks(
            ImageLayout.TransferDstOptimal,
            ImageLayout.DepthStencilAttachmentOptimal,
            out _,
            out _,
            out PipelineStageFlags2 destinationStage,
            out AccessFlags2 destinationAccess);

        const PipelineStageFlags2 expectedStages =
            PipelineStageFlags2.EarlyFragmentTestsBit |
            PipelineStageFlags2.LateFragmentTestsBit;
        const AccessFlags2 expectedAccess =
            AccessFlags2.DepthStencilAttachmentReadBit |
            AccessFlags2.DepthStencilAttachmentWriteBit;
        Assert.Multiple(() =>
        {
            Assert.That(sourceStage, Is.EqualTo(expectedStages));
            Assert.That(sourceAccess, Is.EqualTo(expectedAccess));
            Assert.That(destinationStage, Is.EqualTo(expectedStages));
            Assert.That(destinationAccess, Is.EqualTo(expectedAccess));
        });
    }

    [Test]
    public void StaticShadowCacheSignature_TracksSceneContentAndRasterBias()
    {
        var sceneData = new SceneRenderingData
        {
            SceneContentRevision = 41,
            DirectionalStaticShadowMeshletCount = 12,
            DirectionalStaticShadowMeshletDrawSignature = 99,
            DirectionalShadowMapSize = 2048,
            DirectionalShadowCascadeCount = 3
        };
        var settings = new ShadowSettings
        {
            ConstantDepthBias = 0.0005f,
            SlopeScaledDepthBias = 1.5f
        };

        ulong baseline = DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings);
        sceneData.SceneContentRevision++;
        ulong contentChanged = DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings);
        sceneData.SceneContentRevision--;
        settings.ConstantDepthBias = 0.001f;
        ulong constantBiasChanged = DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings);
        settings.ConstantDepthBias = 0.0005f;
        settings.SlopeScaledDepthBias = 2.0f;
        ulong slopeBiasChanged = DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings);

        Assert.Multiple(() =>
        {
            Assert.That(contentChanged, Is.Not.EqualTo(baseline));
            Assert.That(constantBiasChanged, Is.Not.EqualTo(baseline));
            Assert.That(slopeBiasChanged, Is.Not.EqualTo(baseline));
        });
    }

    [Test]
    public void StaticShadowCacheSignature_TracksOnlySelectedCascadeMatrix()
    {
        var sceneData = new SceneRenderingData
        {
            SceneContentRevision = 12,
            DirectionalStaticShadowMeshletCount = 8,
            DirectionalStaticShadowMeshletDrawSignature = 44,
            DirectionalShadowMapSize = 2048,
            DirectionalShadowCascadeCount = 2,
            ShadowData = new GPUShadowData
            {
                LightViewProjection0 = CoreMatrix4x4.Identity,
                LightViewProjection1 = CoreMatrix4x4.Identity
            }
        };
        var settings = new ShadowSettings();

        ulong cascade0Before = DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings, 0);
        ulong cascade1Before = DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings, 1);
        GPUShadowData changed = sceneData.ShadowData;
        changed.LightViewProjection1.M41 = 3.5f;
        sceneData.ShadowData = changed;

        Assert.Multiple(() =>
        {
            Assert.That(
                DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings, 0),
                Is.EqualTo(cascade0Before));
            Assert.That(
                DirectionalShadowPass.CreateStaticCacheSignature(sceneData, settings, 1),
                Is.Not.EqualTo(cascade1Before));
        });
    }

    [TestCase(0f, 1f)]
    [TestCase(-0.5f, 1f)]
    [TestCase(0.35f, 0.35f)]
    [TestCase(2f, 1f)]
    public void Build_NormalizesDirectionalShadowStrength(float authoredStrength, float expectedStrength)
    {
        GPUShadowData data = DirectionalShadowDataBuilder.Build(
            CreateCamera(),
            new NumericsVector3(0.2f, -1f, -0.3f),
            new ShadowSettings(),
            selectedLightIndex: 0,
            shadowStrength: authoredStrength);

        Assert.That(data.Settings.X, Is.EqualTo(expectedStrength).Within(0.0001f));
    }

    [Test]
    public void BuildParameters_ExposesStableTexelAndModeContract()
    {
        var settings = new ShadowSettings
        {
            RequestedDirectionalShadowMode = DirectionalShadowMode.HybridContact,
            DirectionalFilterMode = DirectionalShadowFilterMode.TentPcf,
            DirectionalBiasMode = DirectionalShadowBiasMode.WorldTexelScaled,
            NormalBias = 0.08f,
            DirectionalContactShadowDistance = 4.5f
        };
        var diagnostics = new DirectionalShadowCascadeFitDiagnostics[ShadowSettings.MaxDirectionalCascades];
        for (int cascade = 0; cascade < diagnostics.Length; cascade++)
            diagnostics[cascade] = new DirectionalShadowCascadeFitDiagnostics(
                cascade, default, default, default,
                0f, 0f, 0f, 0f, 1f, 0.25f * (cascade + 1), 2f,
                -1f, 1f, -1f, 1f,
                DirectionalShadowStabilizationResetReason.None);

        GPUDirectionalShadowParameters parameters = DirectionalShadowDataBuilder.BuildParameters(
            settings,
            diagnostics,
            DirectionalShadowMode.Cascaded);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.CascadeWorldTexelSizes.X, Is.EqualTo(0.25f));
            Assert.That(parameters.CascadeWorldTexelSizes.W, Is.EqualTo(1f));
            Assert.That(parameters.FilterAndBias.X, Is.EqualTo((float)DirectionalShadowFilterMode.TentPcf));
            Assert.That(parameters.FilterAndBias.Y, Is.EqualTo((float)DirectionalShadowBiasMode.WorldTexelScaled));
            Assert.That(parameters.FilterAndBias.W, Is.EqualTo(0.08f));
            Assert.That(parameters.ModeAndRayDistance.X, Is.EqualTo((float)DirectionalShadowMode.HybridContact));
            Assert.That(parameters.ModeAndRayDistance.Y, Is.EqualTo((float)DirectionalShadowMode.Cascaded));
            Assert.That(parameters.ModeAndRayDistance.Z, Is.EqualTo(4.5f));
        });
    }

    [Test]
    public void ShadowSettings_ClampToSupportedRanges()
    {
        var settings = new ShadowSettings
        {
            DirectionalShadowMapSize = 300,
            DirectionalCascadeCount = 99,
            DirectionalShadowPreviewCascade = 99,
            MaxShadowDistance = -1f,
            DirectionalCascadeBlendFraction = 2f,
            DirectionalCascadeSplitLambda = 2f,
            DirectionalCasterExtrusionDistance = -1f,
            DirectionalContactShadowDistance = 200f,
            NormalBias = 2f,
            SlopeScaledDepthBias = 99f,
            ConstantDepthBias = 1f,
            PcfRadius = 99
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.DirectionalShadowMapSize, Is.EqualTo(512));
            Assert.That(settings.DirectionalCascadeCount, Is.EqualTo(ShadowSettings.MaxDirectionalCascades));
            Assert.That(settings.DirectionalShadowPreviewCascade, Is.EqualTo(ShadowSettings.MaxDirectionalCascades - 1));
            Assert.That(settings.MaxShadowDistance, Is.EqualTo(1f));
            Assert.That(settings.DirectionalCascadeBlendFraction, Is.EqualTo(0.30f));
            Assert.That(settings.DirectionalCascadeSplitLambda, Is.EqualTo(1f));
            Assert.That(settings.DirectionalCasterExtrusionDistance, Is.EqualTo(1f));
            Assert.That(settings.DirectionalContactShadowDistance, Is.EqualTo(1f));
            Assert.That(settings.NormalBias, Is.EqualTo(1f));
            Assert.That(settings.SlopeScaledDepthBias, Is.EqualTo(16f));
            Assert.That(settings.ConstantDepthBias, Is.EqualTo(0.1f));
            Assert.That(settings.PcfRadius, Is.EqualTo(3));
        });
    }

    private static FirstPersonCamera CreateCamera()
    {
        var camera = new FirstPersonCamera(new CoreVector3(0f, 1.5f, 5f), yaw: 0.2f, pitch: -0.1f)
        {
            FieldOfView = MathF.PI / 3f,
            AspectRatio = 16f / 9f,
            NearPlane = 0.05f,
            FarPlane = 250f
        };
        camera.Update();
        return camera;
    }

    private static void AssertMatrixFinite(CoreMatrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
                Assert.That(float.IsFinite(matrix[row, column]), Is.True, $"matrix[{row},{column}]");
        }
    }

    private static void AssertMatrixEqual(CoreMatrix4x4 expected, CoreMatrix4x4 actual)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
                Assert.That(actual[row, column], Is.EqualTo(expected[row, column]));
        }
    }

    private static CoreMatrix4x4 CreateRandomSupportedMatrix(Random random)
    {
        CoreMatrix4x4 scale = CoreMatrix4x4.CreateScale(new CoreVector3(
            NextRange(random, 0.2f, 2.0f),
            NextRange(random, 0.2f, 2.0f),
            NextRange(random, 0.2f, 2.0f)));
        CoreMatrix4x4 rotation =
            CoreMatrix4x4.CreateRotationX(NextRange(random, -MathF.PI, MathF.PI)) *
            CoreMatrix4x4.CreateRotationY(NextRange(random, -MathF.PI, MathF.PI));
        CoreMatrix4x4 translation = CoreMatrix4x4.CreateTranslation(new CoreVector3(
            NextRange(random, -4.0f, 4.0f),
            NextRange(random, -4.0f, 4.0f),
            NextRange(random, -4.0f, 4.0f)));
        return scale * rotation * translation;
    }

    private static bool ExtractedPlanesAcceptSphere(
        CoreVector3 centre,
        float radius,
        CoreMatrix4x4 matrix)
    {
        return PlaneAccepts(matrix.M11 + matrix.M14, matrix.M21 + matrix.M24, matrix.M31 + matrix.M34, matrix.M41 + matrix.M44) &&
               PlaneAccepts(matrix.M14 - matrix.M11, matrix.M24 - matrix.M21, matrix.M34 - matrix.M31, matrix.M44 - matrix.M41) &&
               PlaneAccepts(matrix.M12 + matrix.M14, matrix.M22 + matrix.M24, matrix.M32 + matrix.M34, matrix.M42 + matrix.M44) &&
               PlaneAccepts(matrix.M14 - matrix.M12, matrix.M24 - matrix.M22, matrix.M34 - matrix.M32, matrix.M44 - matrix.M42) &&
               PlaneAccepts(matrix.M13, matrix.M23, matrix.M33, matrix.M43) &&
               PlaneAccepts(matrix.M14 - matrix.M13, matrix.M24 - matrix.M23, matrix.M34 - matrix.M33, matrix.M44 - matrix.M43);

        bool PlaneAccepts(float x, float y, float z, float w)
        {
            float length = MathF.Sqrt(x * x + y * y + z * z);
            if (length <= float.Epsilon)
                return w >= 0.0f;
            float distance = (x * centre.X + y * centre.Y + z * centre.Z + w) / length;
            return distance >= -radius - 1.0e-5f * MathF.Max(1.0f, MathF.Max(MathF.Abs(distance), radius));
        }
    }

    private static float NextRange(Random random, float minimum, float maximum) =>
        minimum + (float)random.NextDouble() * (maximum - minimum);
}
