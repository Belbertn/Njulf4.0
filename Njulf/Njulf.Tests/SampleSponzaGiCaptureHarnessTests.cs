using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Njulf.Core.Math;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSponzaGiCaptureHarnessTests
{
    [Test]
    public void ReceiverCacheIncidentBookmark_PreservesRegressionCamera()
    {
        SampleSponzaGiCameraBookmark camera =
            SampleSponzaGiCaptureContract.ReceiverCacheIncidentBookmark;

        Assert.Multiple(() =>
        {
            Assert.That(camera.Name,
                Is.EqualTo("SponzaReceiverCacheCurtainMasonryIncident"));
            Assert.That(camera.Position.X, Is.EqualTo(5.423569f));
            Assert.That(camera.Position.Y, Is.EqualTo(1.5170902f));
            Assert.That(camera.Position.Z, Is.EqualTo(1.0029265f));
            Assert.That(camera.Yaw, Is.EqualTo(-1.3008178f));
            Assert.That(camera.Pitch, Is.EqualTo(-0.55801713f));
            Assert.That(camera.NearPlane, Is.EqualTo(0.05f));
            Assert.That(camera.FarPlane, Is.EqualTo(250.0f));
        });
    }

    [Test]
    public void LiveReceiverHealth_RejectsTheZeroResidencyFlatField()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        RendererDiagnostics failed = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 16_122,
            DdgiActiveProbeCount = 16_122,
            SimpleDdgiProbeResidency = new SimpleDdgiProbeResidencyTelemetry(
                true,
                SimpleDdgiProbeResidencyMode.SparseNearRing,
                true,
                string.Empty)
            {
                FeedbackValid = true,
                ResidencyStateValid = true
            },
            DdgiForwardEstimateCountersReadbackValid = 1,
            DdgiForwardEstimateSampleCount = 5_298,
            DdgiForwardEstimateEnvironmentFallbackWeight = 1.0f
        };

        IReadOnlyList<string> blockers = contract.GetLiveReceiverHealthBlockers(failed);

        Assert.Multiple(() =>
        {
            Assert.That(blockers, Has.Some.Contains("no published receiver payload"));
            Assert.That(blockers, Has.Some.Contains("receiver delivery is collapsed"));
        });
    }

    [Test]
    public void LiveReceiverHealth_AcceptsPublishedAutomaticRingField()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        RendererDiagnostics healthy = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 16_122,
            DdgiActiveProbeCount = 16_122,
            SimpleDdgiProbeResidency = new SimpleDdgiProbeResidencyTelemetry(
                true,
                SimpleDdgiProbeResidencyMode.SparseNearRing,
                true,
                string.Empty)
            {
                FeedbackValid = true,
                ResidencyStateValid = true,
                ResidentPageCount = 294,
                PublishedPageCount = 294,
                ActiveResidentProbeCount = 6_177
            },
            DdgiForwardEstimateCountersReadbackValid = 1,
            DdgiForwardEstimateSampleCount = 5_298,
            DdgiAverageSpatialCoverageEstimate = 1.0f,
            DdgiAverageSupportCoverageEstimate = 0.923f,
            DdgiAverageEffectiveContributionEstimate = 1.0f,
            DdgiAverageOwnershipConsumedEstimate = 1.0f,
            DdgiForwardEstimateSampledIrradianceLuminance = 0.99f
        };

        Assert.That(contract.GetLiveReceiverHealthBlockers(healthy), Is.Empty);
    }

    [Test]
    public void TransportCaptureReadiness_AcceptsCertifiedReceiverReadyField()
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        RendererDiagnostics diagnostics = ReadyTransportDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(
                contract.GetTransportCaptureReadinessBlockers(diagnostics),
                Is.Empty);
            Assert.That(
                contract.GetTransportCaptureReadinessTimeoutFrames(diagnostics),
                Is.EqualTo(
                    120 + 12 +
                    SimpleDdgiRefinementPublicationBlendState
                        .TransitionFrameCount +
                    SampleSponzaGiCaptureContract.TransportReadinessStableFrameCount));
        });
    }

    [Test]
    public void TransportCaptureReadiness_RejectsIncompleteRefinementHandoff()
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        RendererDiagnostics ready = ReadyTransportDiagnostics();
        RendererDiagnostics blending = ready with
        {
            DdgiWarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
            SimpleDdgiRefinement = ready.SimpleDdgiRefinement with
            {
                ReceiverReadyBrickCount = 0,
                BaseFallbackBrickCount = 1,
                ReceiverBlendWeight = 0.5f,
                AdmissionStatus = "receiver-blending"
            }
        };

        IReadOnlyList<string> blockers =
            contract.GetTransportCaptureReadinessBlockers(blending);

        Assert.That(blockers, Has.Some.Contains("handoff is incomplete"));
    }

    [Test]
    public void TransportCaptureReadiness_RejectsUncertifiedFallbackBrick()
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        RendererDiagnostics diagnostics = ReadyTransportDiagnostics() with
        {
            DdgiWarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
            SimpleDdgiTransportGlobalConvergencePending = 1,
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    TailPhase = SimpleDdgiTransportPhase.AuditFrozen,
                    TailReason =
                        SimpleDdgiTransportCertificationReason.AuditInProgress,
                    TailExpectedParticipantCount = 64,
                    TailAuditedParticipantCount = 32,
                    TailCertificateCurrent = false,
                    TailConvergenceDeadlineFrames = 120,
                    TailAuditReadbackDeadlineFrames = 12
                },
            SimpleDdgiRefinement = new SimpleDdgiRefinementBrickDiagnostics(
                Requested: true,
                RequestedBrickCount: 1,
                AdmittedBrickCount: 1,
                ReceiverReadyBrickCount: 0,
                BaseFallbackBrickCount: 1,
                AllocatedProbeCount: 216,
                EvictionCount: 1,
                TopologyChangedThisFrame: true,
                AdmissionStatus: "warming")
        };

        IReadOnlyList<string> blockers =
            contract.GetTransportCaptureReadinessBlockers(diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(blockers, Has.Some.Contains("not SteadyState"));
            Assert.That(blockers, Has.Some.Contains("not current"));
            Assert.That(blockers, Has.Some.Contains("Refinement topology changed"));
            Assert.That(blockers, Has.Some.Contains("receiver-ready"));
            Assert.That(blockers, Has.Some.Contains("base fallback"));
        });
    }

    [Test]
    public void DefaultContract_DefinesLockedNamedEndpointsRoisAndOutputs()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        contract.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(contract.SceneKind, Is.EqualTo(SampleSceneKind.SponzaPlaza));
            Assert.That(contract.Scenario, Is.EqualTo(SamplePerformanceScenario.GiSponzaRightWallStationary));
            Assert.That(contract.Width, Is.EqualTo(1920));
            Assert.That(contract.Height, Is.EqualTo(1080));
            Assert.That(contract.WarmupFrames, Is.EqualTo(SampleSponzaGiCaptureContract.FullSourceRefreshSweepFrameCount));
            Assert.That(
                SampleSponzaGiCaptureContract.HighBookmarkStationarySettleFrameCount,
                Is.EqualTo(
                    SampleSponzaGiCaptureContract.FullSourceRefreshSweepFrameCount +
                    SampleSponzaGiCaptureContract.TailCertificationSettleFrameCount));
            Assert.That(contract.VerticalPathDurationSeconds, Is.InRange(10, 20));
            Assert.That(contract.VerticalTraversalFrameCount, Is.EqualTo(960));
            Assert.That(contract.MotionTraversalFrameCount, Is.EqualTo(300));
                Assert.That(contract.SchemaVersion, Is.EqualTo("realtime-gi-closure-sponza-capture/v24"));
            Assert.That(SampleSponzaGiTemporalTrace.SchemaVersion,
                Is.EqualTo("simple-ddgi-sponza-temporal-trace/v7"));
            Assert.That(SampleSponzaGiTemporalTrace.Capacity, Is.EqualTo(960));
                Assert.That(contract.TotalCaptureFrameCount, Is.EqualTo(6_170));
            Assert.That(contract.LowBookmark.Name, Is.EqualTo("SponzaPlazaUpperFacadeLow"));
            Assert.That(contract.LowBookmark.Position.Y, Is.EqualTo(1.35f));
            Assert.That(contract.LowBookmark.Pitch, Is.EqualTo(-0.16f));
            Assert.That(contract.HighBookmark.Name, Is.EqualTo("SponzaPlazaUpperFacadeHigh"));
            Assert.That(
                SampleSponzaGiCaptureContract.VerticalTraversalName,
                Is.EqualTo("SponzaPlazaUpperFacadeVerticalTraversal"));
            Assert.That(contract.HighBookmark.Position.X, Is.EqualTo(4.443325f));
            Assert.That(contract.HighBookmark.Position.Y, Is.EqualTo(8.158655f));
            Assert.That(contract.HighBookmark.Position.Z, Is.EqualTo(0.3589885f));
            Assert.That(contract.HighBookmark.Yaw, Is.EqualTo(-2.8296268f));
            Assert.That(contract.HighBookmark.Pitch, Is.EqualTo(-0.16935858f));
            Assert.That(contract.ReceiverRois.Select(static roi => roi.Name), Is.EquivalentTo(new[]
            {
                "central-upper-facade",
                "right-upper-wall",
                "upper-gallery-hotspot-pair",
                "left-gallery-interior",
                "right-gallery-interior",
                "arcade-interior",
                "outdoor-reference-patch",
                "curtain-lit-side-floor",
                "curtain-shadow-side-receiver",
                "curtain-adjacent-bounce",
                "former-plaza-transition-strip"
            }));
            Assert.That(contract.Outputs.Select(static output => output.Name), Is.EquivalentTo(new[]
            {
                "beauty",
                "beauty-no-indirect-specular",
                "direct-only",
                "final-indirect",
                "irradiance-log",
                "sampled-irradiance",
                "final-diffuse",
                "volume-contributor",
                "gather-clipmap",
                "gather-blend-weight",
                "gather-fallback",
                "spatial-coverage",
                "support",
                "data-confidence",
                "directional-support",
                "confidence-chain",
                "visibility",
                "ownership",
                "fallback",
                "probe-state",
                "probe-index",
                "ray-budget",
                "visibility-moments",
                "probe-relocation",
                "probe-residency",
                "residency-fallback",
                "page-age",
                "physical-page",
                "receiver-cache-rejection"
            }));
            SampleSponzaGiCaptureOutput directOnly = contract.Outputs.Single(static output => output.Name == "direct-only");
            Assert.That(directOnly.DisableGlobalIllumination, Is.True);
            Assert.That(directOnly.DisableEnvironmentLighting, Is.True);
            SampleSponzaGiCaptureOutput noIndirectSpecular = contract.Outputs.Single(static output =>
                output.Name == "beauty-no-indirect-specular");
            Assert.That(noIndirectSpecular.DisableIndirectSpecularLighting, Is.True);
            Assert.That(noIndirectSpecular.DisableGlobalIllumination, Is.False);
            Assert.That(noIndirectSpecular.DisableEnvironmentLighting, Is.False);
            Assert.That(contract.Outputs[^1], Is.SameAs(directOnly));
            Assert.That(
                contract.Outputs.Where(static output => output.Name != "direct-only"),
                Has.None.Matches<SampleSponzaGiCaptureOutput>(static output => output.DisableEnvironmentLighting));
            Assert.That(
                contract.Outputs.Where(static output => output.Name != "beauty-no-indirect-specular"),
                Has.None.Matches<SampleSponzaGiCaptureOutput>(static output => output.DisableIndirectSpecularLighting));
            Assert.That(contract.Outputs.Single(static output => output.Name == "ownership").DebugView,
                Is.EqualTo(GlobalIlluminationDebugView.DdgiEffectiveWeight));
            Assert.That(contract.ReceiverRois, Has.All.Matches<SampleSponzaGiReceiverRoi>(roi => roi.RequireCoarserFallback));
            Assert.That(contract.Fingerprint, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void VerticalTraversal_IsFixedAndReachesBothBookmarksExactly()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        SampleSponzaGiCameraBookmark first = contract.SampleVerticalTraversalFrame(0);
        SampleSponzaGiCameraBookmark middle = contract.SampleVerticalTraversalFrame(contract.VerticalTraversalFrameCount / 2);
        SampleSponzaGiCameraBookmark last = contract.SampleVerticalTraversalFrame(contract.VerticalTraversalFrameCount - 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.Position, Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(last.Position, Is.EqualTo(contract.HighBookmark.Position));
            Assert.That(first.Yaw, Is.EqualTo(contract.LowBookmark.Yaw));
            Assert.That(last.Pitch, Is.EqualTo(contract.HighBookmark.Pitch));
            Assert.That(middle.Position.X, Is.InRange(contract.HighBookmark.Position.X, contract.LowBookmark.Position.X));
            Assert.That(middle.Position.Z, Is.InRange(contract.LowBookmark.Position.Z, contract.HighBookmark.Position.Z));
            Assert.That(middle.Position.Y, Is.GreaterThan(contract.LowBookmark.Position.Y));
            Assert.That(middle.Position.Y, Is.LessThan(contract.HighBookmark.Position.Y));
            Assert.That(
                () => contract.SampleVerticalTraversalFrame(contract.VerticalTraversalFrameCount),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void MotionTraversal_MovesTwoPointFiveMetresPausesAndReturnsExactly()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        SampleSponzaGiCameraBookmark first = contract.SampleMotionTraversalFrame(0);
        SampleSponzaGiCameraBookmark outbound = contract.SampleMotionTraversalFrame(
            SampleSponzaGiCaptureContract.MotionOutboundFrameCount - 1);
        SampleSponzaGiCameraBookmark pauseLast = contract.SampleMotionTraversalFrame(
            SampleSponzaGiCaptureContract.MotionOutboundFrameCount +
            SampleSponzaGiCaptureContract.MotionPauseFrameCount - 1);
        SampleSponzaGiCameraBookmark last = contract.SampleMotionTraversalFrame(
            contract.MotionTraversalFrameCount - 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.Position, Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(outbound.Position.Z - first.Position.Z,
                Is.EqualTo(SampleSponzaGiCaptureContract.MotionTraversalDistance).Within(1e-6f));
            Assert.That(pauseLast.Position, Is.EqualTo(outbound.Position));
            Assert.That(last.Position, Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(last.Yaw, Is.EqualTo(contract.LowBookmark.Yaw));
            Assert.That(
                () => contract.SampleMotionTraversalFrame(contract.MotionTraversalFrameCount),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void CameraMotionValidationRoutes_CoverWorldXWorldZCutsAndTeleport()
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        int outboundFrame =
            SampleSponzaGiCaptureContract.MotionOutboundFrameCount - 1;
        SampleSponzaGiCameraBookmark worldX =
            contract.SampleWorldXMotionTraversalFrame(outboundFrame);
        SampleSponzaGiCameraBookmark worldZ =
            contract.SampleWorldZMotionTraversalFrame(outboundFrame);

        Assert.Multiple(() =>
        {
            Assert.That(
                worldX.Position.X - contract.LowBookmark.Position.X,
                Is.EqualTo(
                    SampleSponzaGiCaptureContract.MotionTraversalDistance)
                    .Within(1e-6f));
            Assert.That(worldX.Position.Z,
                Is.EqualTo(contract.LowBookmark.Position.Z));
            Assert.That(
                worldZ.Position.Z - contract.LowBookmark.Position.Z,
                Is.EqualTo(
                    SampleSponzaGiCaptureContract.MotionTraversalDistance)
                    .Within(1e-6f));
            Assert.That(worldZ.Position.X,
                Is.EqualTo(contract.LowBookmark.Position.X));
            Assert.That(
                Vector3.Distance(
                    contract.FastOverlappingMovementBookmark.Position,
                    contract.LowBookmark.Position),
                Is.EqualTo(6.0f).Within(1e-6f));
            Assert.That(
                MathF.Abs(
                    contract.RotationCutBookmark.Yaw -
                    contract.LowBookmark.Yaw),
                Is.GreaterThan(MathF.PI / 3.0f));
            Assert.That(
                contract.RotationCutBookmark.Position,
                Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(
                Vector3.Distance(
                    contract.TrueTeleportBookmark.Position,
                    contract.LowBookmark.Position),
                Is.EqualTo(
                    SampleSponzaGiCaptureContract.TrueTeleportDistance)
                    .Within(1e-6f));
        });
    }

    [Test]
    public void CoveragePath_ContainsEveryFixedTimestepOfBothLockedTraversals()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        IReadOnlyList<SimpleDdgiCoverageCameraSample> path = contract.CreateCoverageCameraPath();

        Assert.Multiple(() =>
        {
            Assert.That(path, Has.Count.EqualTo(1_260));
            Assert.That(path[0].Name,
                Does.StartWith(SampleSponzaGiCaptureContract.MotionTraversalName));
            Assert.That(path[^1].Name, Is.EqualTo(contract.HighBookmark.Name));
            Assert.That(path[0].Position, Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(path[^1].Position, Is.EqualTo(contract.HighBookmark.Position));
            Assert.That(path.Select(static sample => sample.Name).Distinct().Count(), Is.EqualTo(path.Count));
            Assert.That(path[contract.MotionTraversalFrameCount + 480].Position,
                Is.EqualTo(contract.SampleVerticalTraversalFrame(480).Position));
        });
    }

    [Test]
    public void TemporalTrace_IsBoundedAndReturnsOldestToNewest()
    {
        var trace = new SampleSponzaGiTemporalTrace();
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        for (int i = 0; i < SampleSponzaGiTemporalTrace.Capacity + 37; i++)
        {
            trace.Record(
                new SampleSponzaGiCaptureInstruction(
                    SampleSponzaGiCaptureStage.MotionTraversal,
                    i,
                    SampleSponzaGiTemporalTrace.Capacity + 37,
                    contract.LowBookmark,
                    null,
                    SampleSponzaGiCaptureContract.MotionTraversalName,
                    false),
                RendererDiagnostics.Empty);
        }

        IReadOnlyList<SampleSponzaGiTemporalTraceEntry> snapshot = trace.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.EqualTo(SampleSponzaGiTemporalTrace.Capacity));
            Assert.That(trace.TotalSampleCount,
                Is.EqualTo((ulong)(SampleSponzaGiTemporalTrace.Capacity + 37)));
            Assert.That(snapshot[0].SampleIndex, Is.EqualTo(37));
            Assert.That(snapshot[^1].SampleIndex,
                Is.EqualTo((ulong)(SampleSponzaGiTemporalTrace.Capacity + 36)));
        });
    }

    [Test]
    public void TemporalTrace_RetainsEveryFrameOfTheVerticalTraversal()
    {
        var trace = new SampleSponzaGiTemporalTrace();
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        trace.Reset();
        for (int frameIndex = 0;
             frameIndex < contract.VerticalTraversalFrameCount;
             frameIndex++)
        {
            trace.Record(
                new SampleSponzaGiCaptureInstruction(
                    SampleSponzaGiCaptureStage.VerticalTraversal,
                    frameIndex,
                    contract.VerticalTraversalFrameCount,
                    contract.SampleVerticalTraversalFrame(frameIndex),
                    null,
                    SampleSponzaGiCaptureContract.VerticalTraversalName,
                    false),
                RendererDiagnostics.Empty);
        }

        IReadOnlyList<SampleSponzaGiTemporalTraceEntry> snapshot = trace.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(contract.VerticalTraversalFrameCount,
                Is.EqualTo(SampleSponzaGiTemporalTrace.Capacity));
            Assert.That(trace.Count, Is.EqualTo(contract.VerticalTraversalFrameCount));
            Assert.That(trace.TotalSampleCount,
                Is.EqualTo((ulong)contract.VerticalTraversalFrameCount));
            Assert.That(snapshot, Has.Count.EqualTo(contract.VerticalTraversalFrameCount));
            Assert.That(snapshot[0].SampleIndex, Is.Zero);
            Assert.That(snapshot[0].StageFrameIndex, Is.Zero);
            Assert.That(snapshot[^1].SampleIndex,
                Is.EqualTo((ulong)(contract.VerticalTraversalFrameCount - 1)));
            Assert.That(snapshot[^1].StageFrameIndex,
                Is.EqualTo(contract.VerticalTraversalFrameCount - 1));
            Assert.That(snapshot.Select(static entry => entry.Stage),
                Is.All.EqualTo(SampleSponzaGiCaptureStage.VerticalTraversal));
            Assert.That(snapshot.Select(static entry => entry.StageFrameIndex),
                Is.EqualTo(Enumerable.Range(0, contract.VerticalTraversalFrameCount)));
        });
    }

    [Test]
    public void TemporalTrace_ResetIsolatesTheCompleteVerticalTraversalFromMotion()
    {
        var trace = new SampleSponzaGiTemporalTrace();
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        for (int frameIndex = 0;
             frameIndex < contract.MotionTraversalFrameCount;
             frameIndex++)
        {
            trace.Record(
                new SampleSponzaGiCaptureInstruction(
                    SampleSponzaGiCaptureStage.MotionTraversal,
                    frameIndex,
                    contract.MotionTraversalFrameCount,
                    contract.SampleMotionTraversalFrame(frameIndex),
                    null,
                    SampleSponzaGiCaptureContract.MotionTraversalName,
                    false),
                RendererDiagnostics.Empty);
        }

        trace.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.Zero);
            Assert.That(trace.TotalSampleCount, Is.Zero);
            Assert.That(trace.Snapshot(), Is.Empty);
        });

        for (int frameIndex = 0;
             frameIndex < contract.VerticalTraversalFrameCount;
             frameIndex++)
        {
            trace.Record(
                new SampleSponzaGiCaptureInstruction(
                    SampleSponzaGiCaptureStage.VerticalTraversal,
                    frameIndex,
                    contract.VerticalTraversalFrameCount,
                    contract.SampleVerticalTraversalFrame(frameIndex),
                    null,
                    SampleSponzaGiCaptureContract.VerticalTraversalName,
                    false),
                RendererDiagnostics.Empty);
        }

        IReadOnlyList<SampleSponzaGiTemporalTraceEntry> snapshot = trace.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.EqualTo(960));
            Assert.That(trace.TotalSampleCount, Is.EqualTo(960));
            Assert.That(snapshot, Has.Count.EqualTo(960));
            Assert.That(snapshot.Select(static entry => entry.Stage),
                Is.All.EqualTo(SampleSponzaGiCaptureStage.VerticalTraversal));
            Assert.That(snapshot.Select(static entry => entry.SampleIndex),
                Is.EqualTo(Enumerable.Range(0, 960).Select(static index => (ulong)index)));
            Assert.That(snapshot.Select(static entry => entry.StageFrameIndex),
                Is.EqualTo(Enumerable.Range(0, 960)));
        });
    }

    [Test]
    public void TemporalTrace_ResetDoesNotAllocateOrClearTheBackingStore()
    {
        var trace = new SampleSponzaGiTemporalTrace();
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        trace.Record(
            new SampleSponzaGiCaptureInstruction(
                SampleSponzaGiCaptureStage.MotionTraversal,
                23,
                contract.MotionTraversalFrameCount,
                contract.SampleMotionTraversalFrame(23),
                null,
                SampleSponzaGiCaptureContract.MotionTraversalName,
                false),
            RendererDiagnostics.Empty);
        var backingStore = (SampleSponzaGiTemporalTraceEntry[])typeof(
                SampleSponzaGiTemporalTrace)
            .GetField("_entries", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(trace)!;
        trace.Reset();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            trace.Reset();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Multiple(() =>
        {
            Assert.That(allocatedBytes, Is.Zero);
            Assert.That(trace.Count, Is.Zero);
            Assert.That(trace.TotalSampleCount, Is.Zero);
            Assert.That(backingStore[0].StageFrameIndex, Is.EqualTo(23));
        });
    }

    [Test]
    public void TemporalTraceV7_WritesDdgiOwnershipScrollAndAlignedProbeLifecycleJson()
    {
        var trace = new SampleSponzaGiTemporalTrace();
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        ReflectionProbeLifecycleFrameSnapshot current =
            CreateReflectionLifecycleFrame(
                frameSlot: 1,
                frameSerial: 72,
                captureFaceUnits: 2,
                prefilterMipUnits: 3,
                publishCopyUnits: 0);
        ReflectionProbeLifecycleFrameSnapshot completed =
            CreateReflectionLifecycleFrame(
                frameSlot: 1,
                frameSerial: 70,
                captureFaceUnits: 6,
                prefilterMipUnits: 7,
                publishCopyUnits: 1);
        ReflectionProbeGpuBudgetSnapshot budget = new(
            BudgetMicroseconds: 900,
            ReservedMicroseconds: 325,
            FaceEstimateMicroseconds: 115,
            PrefilterEstimateMicroseconds: 135,
            CopyEstimateMicroseconds: 40,
            HasTimingHistory: true,
            BudgetExhausted: false);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ReflectionProbeCurrentLifecycle = current,
            ReflectionProbeCurrentCaptureBudget = budget,
            ReflectionProbeCompletedLifecycle = completed,
            GpuReflectionProbeCaptureMicroseconds = 611,
            GpuReflectionProbePrefilterMicroseconds = 277,
            GpuReflectionProbePublishMicroseconds = 43,
            HybridReflectionCountersReadbackValid = 1,
            HybridReflectionDdgiFallbackCount = 17,
            HybridReflectionProbeFallbackCount = 0,
            HybridReflectionEnvironmentFallbackCount = 4,
            GpuHybridReflectionDdgiBaseMicroseconds = 211,
            SimpleDdgiFrameRayBucket0 = 128,
            SimpleDdgiFrameRayBucket1 = 32,
            SimpleDdgiFrameRayBucket2 = 64,
            SimpleDdgiNearScrollCardinality = 64,
            SimpleDdgiScrollGpuExpectedCount = 392,
            SimpleDdgiScrollGpuAcceptedCount = 392,
            SimpleDdgiScrollGpuTracedCount = 392,
            SimpleDdgiScrollGpuCommittedCount = 392
        };
        trace.Record(
            new SampleSponzaGiCaptureInstruction(
                SampleSponzaGiCaptureStage.VerticalTraversal,
                0,
                contract.VerticalTraversalFrameCount,
                contract.LowBookmark,
                null,
                SampleSponzaGiCaptureContract.VerticalTraversalName,
                false),
            diagnostics);
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sponza-reflection-trace-{Guid.NewGuid():N}.json");

        try
        {
            trace.Write(path, contract.Fingerprint, "reflection-alignment");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement entry = document.RootElement.GetProperty("entries")[0];
            Assert.Multiple(() =>
            {
                Assert.That(
                    document.RootElement.GetProperty("schemaVersion").GetString(),
                    Is.EqualTo("simple-ddgi-sponza-temporal-trace/v7"));
                Assert.That(
                    document.RootElement.GetProperty("contractFingerprint").GetString(),
                    Is.EqualTo(contract.Fingerprint));
                Assert.That(trace.Snapshot()[0].ReflectionProbeCurrentLifecycle,
                    Is.EqualTo(current));
                Assert.That(trace.Snapshot()[0].ReflectionProbeCompletedLifecycle,
                    Is.EqualTo(completed));
                Assert.That(
                    entry.GetProperty("reflectionProbeCurrentLifecycle")
                        .GetProperty("frameSerial").GetUInt64(),
                    Is.EqualTo(72UL));
                Assert.That(
                    entry.GetProperty("reflectionProbeCurrentCaptureBudget")
                        .GetProperty("reservedMicroseconds").GetInt32(),
                    Is.EqualTo(325));
                Assert.That(
                    entry.GetProperty("reflectionProbeCompletedLifecycle")
                        .GetProperty("lifecycle")
                        .GetProperty("publishCopyUnitsThisFrame").GetInt32(),
                    Is.EqualTo(1));
                Assert.That(
                    entry.GetProperty("gpuReflectionProbePublishMicroseconds")
                        .GetInt64(),
                    Is.EqualTo(43));
                Assert.That(
                    entry.GetProperty("nearScrollCardinality").GetInt32(),
                    Is.EqualTo(64));
                Assert.That(
                    document.RootElement.GetProperty("scrollSummary")
                        .GetProperty("completeScrollFrameCount").GetInt32(),
                    Is.EqualTo(1));
                Assert.That(
                    entry.GetProperty("hybridReflectionDdgiFallbackCount")
                        .GetUInt32(),
                    Is.EqualTo(17u));
                Assert.That(
                    document.RootElement.GetProperty("reflectionGate")
                        .GetProperty("passed").GetBoolean(),
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
    public void ReflectionGate_RequiresDdgiAndRejectsEveryManualProbePath()
    {
        SampleSponzaGiReflectionGateResult passing =
            SampleSponzaGiReflectionGate.Evaluate(
            [
                new SampleSponzaGiTemporalTraceEntry
                {
                    HybridReflectionCountersReadbackValid = 1,
                    HybridReflectionDdgiFallbackCount = 12,
                    HybridReflectionEnvironmentFallbackCount = 3
                }
            ]);
        SampleSponzaGiReflectionGateResult failing =
            SampleSponzaGiReflectionGate.Evaluate(
            [
                new SampleSponzaGiTemporalTraceEntry
                {
                    ReflectionProbeCount = 1,
                    HybridReflectionCountersReadbackValid = 1,
                    HybridReflectionDdgiFallbackCount = 12,
                    HybridReflectionProbeFallbackCount = 2
                }
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(passing.Passed, Is.True,
                string.Join(" ", passing.Failures));
            Assert.That(passing.DdgiReceiverCount, Is.EqualTo(12UL));
            Assert.That(failing.Passed, Is.False);
            Assert.That(failing.Failures, Has.Some.Contains("manual"));
        });
    }

    [Test]
    public void Sequence_ProducesEndpointOutputSetsInAStableFrameOrder()
    {
        var sequence = new SampleSponzaGiCaptureSequence();
        var captured = new List<(string Bookmark, string Output)>();
        int frames = 0;

        while (!sequence.IsComplete)
        {
            SampleSponzaGiCaptureInstruction instruction = sequence.CurrentInstruction;
            if (instruction.Output != null && instruction.CaptureWindowAfterRenderedFrame)
                captured.Add((instruction.BookmarkName, instruction.Output.Name));
            sequence.AdvanceAfterRenderedFrame();
            frames++;
        }

        SampleSponzaGiCaptureContract contract = sequence.Contract;
        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.EqualTo(contract.TotalCaptureFrameCount));
            Assert.That(captured, Has.Count.EqualTo(contract.Outputs.Count * 2));
            Assert.That(captured.Take(contract.Outputs.Count).Select(static capture => capture.Bookmark),
                Is.All.EqualTo(contract.LowBookmark.Name));
            Assert.That(captured.Skip(contract.Outputs.Count).Select(static capture => capture.Bookmark),
                Is.All.EqualTo(contract.HighBookmark.Name));
            Assert.That(captured.Take(contract.Outputs.Count).Select(static capture => capture.Output),
                Is.EqualTo(contract.Outputs.Select(static output => output.Name)));
            Assert.That(captured.Skip(contract.Outputs.Count).Select(static capture => capture.Output),
                Is.EqualTo(contract.Outputs.Select(static output => output.Name)));
            Assert.That(sequence.Stage, Is.EqualTo(SampleSponzaGiCaptureStage.Complete));
        });
    }

    [Test]
    public void Sequence_PresentsEveryEndpointOutputBeforeCapturingTheHeldState()
    {
        var sequence = new SampleSponzaGiCaptureSequence();
        var endpointFrames = new List<SampleSponzaGiCaptureInstruction>();

        while (!sequence.IsComplete)
        {
            SampleSponzaGiCaptureInstruction instruction = sequence.CurrentInstruction;
            if (instruction.Output != null)
                endpointFrames.Add(instruction);
            sequence.AdvanceAfterRenderedFrame();
        }

        SampleSponzaGiCaptureContract contract = sequence.Contract;
        Assert.That(endpointFrames, Has.Count.EqualTo(
            contract.Outputs.Count * 2 * SampleSponzaGiCaptureContract.FramesPerEndpointOutput));
        for (int i = 0; i < endpointFrames.Count; i += SampleSponzaGiCaptureContract.FramesPerEndpointOutput)
        {
            SampleSponzaGiCaptureInstruction presentation = endpointFrames[i];
            SampleSponzaGiCaptureInstruction capture = endpointFrames[
                i + SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1];
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Output, Is.EqualTo(capture.Output));
                Assert.That(presentation.Camera, Is.EqualTo(capture.Camera));
                Assert.That(presentation.BookmarkName, Is.EqualTo(capture.BookmarkName));
                Assert.That(presentation.CaptureWindowAfterRenderedFrame, Is.False);
                Assert.That(capture.CaptureWindowAfterRenderedFrame, Is.True);
                Assert.That(capture.StageFrameIndex, Is.EqualTo(
                    presentation.StageFrameIndex + SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1));
                Assert.That(
                    endpointFrames
                        .Skip(i)
                        .Take(SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1)
                        .Select(static frame => frame.CaptureWindowAfterRenderedFrame),
                    Is.All.False);
            });
        }
    }

    [Test]
    public void CaptureMode_SeparatesProductionTimingFromDetailedInvestigationCounters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleSponzaGiCaptureContract.UsesDetailedInvestigationCounters(
                    SampleSponzaGiCaptureMode.ProductionTiming),
                Is.False);
            Assert.That(
                SampleSponzaGiCaptureContract.UsesDetailedInvestigationCounters(
                    SampleSponzaGiCaptureMode.DetailedDiagnostics),
                Is.True);
            Assert.That(
                SampleSponzaGiCaptureContract.UsesDetailedInvestigationCounters(
                    SampleSponzaGiCaptureMode.PresentationReview),
                Is.False);
        });
    }

    [Test]
    public void CanonicalProfile_SatisfiesCaptureLockAndReceiverCoverageOracle()
    {
        var settings = new RenderSettings();
        SampleGlobalIlluminationValidation.ConfigureSponzaCaptureSettings(
            settings);
        settings.Particles.Enabled = false;
        settings.Animation.Enabled = false;
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        IReadOnlyList<string> violations = contract.ValidateLockedSettings(settings);
        SimpleDdgiReceiverCoverageReport report = SimpleDdgiReceiverCoverageValidator.Validate(
            settings.GlobalIllumination,
            contract.SceneBounds,
            contract.CreateCoverageRegions(),
            contract.CreateCoverageCameraPath());

        Assert.Multiple(() =>
        {
            Assert.That(violations, Is.Empty);
            Assert.That(report.Layout.WasDegraded, Is.False, report.Layout.Summary);
            Assert.That(report.IsCovered, Is.True,
                string.Join(Environment.NewLine, report.Issues.Select(static issue => issue.Message)));
            Assert.That(report.Samples, Has.Count.EqualTo(
                contract.ReceiverRois.Count * contract.CoverageCameraFrameCount * 15));
            Assert.That(report.Samples.Where(static sample => sample.IsInTransitionBand), Is.Not.Empty);
            Assert.That(report.Samples.Where(static sample => sample.IsInTransitionBand),
                Has.All.Matches<SimpleDdgiReceiverCoverageSample>(sample => sample.HasCoarserFallback));
        });
    }

    [Test]
    public void CaptureLock_RejectsFrozenAstronomicalSourceDrift()
    {
        var settings = new RenderSettings();
        SampleGlobalIlluminationValidation.ConfigureSponzaCaptureSettings(
            settings);
        settings.Particles.Enabled = false;
        settings.Animation.Enabled = false;
        settings.Environment.TimeOfDayHours += 0.25f;

        Assert.That(
            SampleSponzaGiCaptureContract.Default.ValidateLockedSettings(settings),
            Has.Some.Contains("frozen astronomical source"));
    }

    [Test]
    public void CoverageOracleReport_SerializesDerivedTraceDistanceAsStrictJsonNull()
    {
        var settings = new RenderSettings();
        SampleGlobalIlluminationValidation.ConfigureSponzaCaptureSettings(
            settings);
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        SimpleDdgiReceiverCoverageReport report = SimpleDdgiReceiverCoverageValidator.Validate(
            settings.GlobalIllumination,
            contract.SceneBounds,
            contract.CreateCoverageRegions(),
            contract.CreateCoverageCameraPath());
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sponza-gi-coverage-{Guid.NewGuid():N}");

        try
        {
            contract.WriteCoverageOracleReport(directory, report);
            string json = File.ReadAllText(
                Path.Combine(directory, "sponza-gi-coverage-oracle.json"));
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement maximumTraceDistance = document.RootElement
                .GetProperty("layout")
                .GetProperty("volumes")[0]
                .GetProperty("request")
                .GetProperty("maximumTraceDistance");

            Assert.Multiple(() =>
            {
                Assert.That(maximumTraceDistance.ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(json, Does.Not.Contain("NaN"));
                Assert.That(json, Does.Not.Contain("Infinity"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void CanonicalLighting_SatisfiesCaptureLockAndRejectsOccludedSunProfilesAndShadowLeak()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        Light canonical = SampleSponzaLightingProfile.CreateDirectionalKey();
        Light disabledSourceSun = canonical;
        disabledSourceSun.Direction = SampleSponzaLightingProfile.SourceSunDirection;
        Light formerSyntheticSun = canonical;
        formerSyntheticSun.Direction = System.Numerics.Vector3.Normalize(
            new System.Numerics.Vector3(0.18f, -0.82f, 0.54f));
        Light partialStrength = canonical;
        partialStrength.ShadowStrength = 0.85f;
        Light localLight = new() { Type = LightType.Point };

        Assert.Multiple(() =>
        {
            Assert.That(contract.ValidateLockedLighting(new[] { canonical }), Is.Empty);
            Assert.That(
                contract.ValidateLockedLighting(new[] { disabledSourceSun }),
                Has.Some.Contains("locked directional key"));
            Assert.That(
                contract.ValidateLockedLighting(new[] { formerSyntheticSun }),
                Has.Some.Contains("locked directional key"));
            Assert.That(
                contract.ValidateLockedLighting(new[] { partialStrength }),
                Has.Some.Contains("fully occluding"));
            Assert.That(
                contract.ValidateLockedLighting(new[] { canonical, localLight }),
                Has.Some.Contains("exactly one light"));
        });
    }

    [Test]
    public void CompletedManifest_RejectsUnverifiedRendererRequests()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sponza-gi-capture-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        try
        {
            SampleSponzaGiCaptureOutput beauty = contract.Outputs.Single(static output => output.Name == "beauty");
            var artifacts = new[]
            {
                new SampleSponzaGiCapturedArtifact(
                    contract.LowBookmark.Name,
                    beauty.Name,
                    "renderer-screenshot-request",
                    Path.ChangeExtension(contract.GetRelativeImagePath(contract.LowBookmark.Name, beauty), ".renderer.png"),
                    VerificationStatus: "requested")
            };

            Assert.That(
                () => contract.WriteRunManifest(directory, artifacts, "completed"),
                Throws.InvalidOperationException.With.Message.Contains("cannot be completed"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ArtifactVerification_RejectsOversizedPngBeforeReadingPayload()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sponza-gi-artifact-bound-{Guid.NewGuid():N}");
        string relativePath = "captures/oversized.png";
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            using (var output = new FileStream(
                       fullPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                output.SetLength(
                    SampleEvidenceFileIo.MaximumLinearFloatImageBytes + 1);
            }
            var artifact = new SampleSponzaGiCapturedArtifact(
                string.Empty,
                string.Empty,
                "renderer-screenshot-request",
                relativePath);

            bool verified =
                SampleSponzaGiCaptureContract.Default.TryVerifyArtifact(
                    directory,
                    artifact,
                    out _,
                    out string failureReason);

            Assert.Multiple(() =>
            {
                Assert.That(verified, Is.False);
                Assert.That(
                    failureReason,
                    Does.Contain("bounded limit"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void FloorReceiverGate_UsesSceneLinearFinalIndirectAtLockedFloorPixels()
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        float[] pixels = CreateLockedLinearImagePixels(0.02f);
        var image = new LinearFloatImage(
            SampleSponzaGiCaptureContract.LockedWidth,
            SampleSponzaGiCaptureContract.LockedHeight,
            pixels);

        SampleSponzaGiFloorReceiverEvidence evidence =
            SampleSponzaGiFloorReceiverGate.Evaluate(
                image,
                contract.Fingerprint,
                contract.LowBookmark.Name);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Passed, Is.True, evidence.FailureReason);
            Assert.That(evidence.Samples, Has.Count.EqualTo(5));
            Assert.That(evidence.ObservedMinimumLuminance,
                Is.EqualTo(0.02f).Within(1e-6f));
            Assert.That(evidence.ObservedAlignedMeanLuminance,
                Is.EqualTo(0.02f).Within(1e-6f));
            Assert.That(evidence.Samples.Select(static sample => sample.Name),
                Does.Contain("base-floor-far"));
        });
    }

    [Test]
    public void FloorReceiverGate_RejectsOneBlackKnownFloorReceiver()
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        float[] pixels = CreateLockedLinearImagePixels(0.02f);
        int blackPixel = checked(
            (984 * SampleSponzaGiCaptureContract.LockedWidth + 1440) * 3);
        pixels[blackPixel] = 0.0f;
        pixels[blackPixel + 1] = 0.0f;
        pixels[blackPixel + 2] = 0.0f;

        SampleSponzaGiFloorReceiverEvidence evidence =
            SampleSponzaGiFloorReceiverGate.Evaluate(
                new LinearFloatImage(
                    SampleSponzaGiCaptureContract.LockedWidth,
                    SampleSponzaGiCaptureContract.LockedHeight,
                    pixels),
                contract.Fingerprint,
                contract.LowBookmark.Name);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Passed, Is.False);
            Assert.That(evidence.FailureReason,
                Does.Contain("known +Y floor receiver"));
        });
    }

    [Test]
    public void ArtifactVerification_AcceptsOnlyLockedExtentLinearPfm()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sponza-gi-pfm-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        try
        {
            string relativePath =
                contract.GetRelativeLinearFinalIndirectPath(
                    contract.LowBookmark.Name);
            string fullPath = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            PfmLinearImageCodec.WriteAtomic(
                fullPath,
                CreateLockedLinearImagePixels(0.02f),
                SampleSponzaGiCaptureContract.LockedWidth,
                SampleSponzaGiCaptureContract.LockedHeight);

            bool verified = contract.TryVerifyArtifact(
                directory,
                new SampleSponzaGiCapturedArtifact(
                    contract.LowBookmark.Name,
                    "final-indirect",
                    "linear-final-indirect",
                    relativePath),
                out SampleSponzaGiCapturedArtifact artifact,
                out string reason);

            Assert.Multiple(() =>
            {
                Assert.That(verified, Is.True, reason);
                Assert.That(artifact.ByteLength, Is.GreaterThan(0));
                Assert.That(artifact.Sha256, Has.Length.EqualTo(64));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void CompletedManifest_RequiresAndRecordsHashVerifiedRendererArtifacts()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sponza-gi-capture-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        try
        {
            var artifacts = new List<SampleSponzaGiCapturedArtifact>();
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                string.Empty,
                string.Empty,
                "capture-contract",
                "sponza-gi-capture-contract.json",
                "{\"schemaVersion\":\"test\"}"));
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                string.Empty,
                string.Empty,
                "visual-metric-gate",
                "sponza-gi-visual-metric-gate.json",
                "{\"schemaVersion\":\"test\"}"));
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                string.Empty,
                string.Empty,
                "coverage-oracle",
                "sponza-gi-coverage-oracle.json",
                "{\"schemaVersion\":\"test\"}"));
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                contract.LowBookmark.Name,
                "final-indirect",
                "floor-receiver-evidence",
                SampleSponzaGiCaptureContract.FloorReceiverEvidenceFileName,
                "{\"schemaVersion\":\"test\",\"passed\":true}"));
            foreach (string traceName in new[]
                     {
                         contract.LowBookmark.Name,
                         SampleSponzaGiCaptureContract.MotionTraversalName,
                         SampleSponzaGiCaptureContract.VerticalTraversalName,
                         contract.HighBookmark.Name
                     })
            {
                artifacts.Add(CreateVerifiedTextArtifact(
                    contract,
                    directory,
                    traceName,
                    "temporal-trace",
                    "temporal-trace",
                    contract.GetRelativeTemporalTracePath(traceName),
                    "{\"schemaVersion\":\"test\"}"));
            }
            foreach (SampleSponzaGiCameraBookmark bookmark in new[] { contract.LowBookmark, contract.HighBookmark })
            {
                foreach (SampleSponzaGiCaptureOutput output in contract.Outputs)
                {
                    string imagePath = contract.GetRelativeImagePath(bookmark.Name, output);
                    artifacts.Add(CreateVerifiedPngArtifact(
                        contract, directory, bookmark.Name, output.Name, "window-screenshot", imagePath));
                    artifacts.Add(CreateVerifiedPngArtifact(
                        contract,
                        directory,
                        bookmark.Name,
                        output.Name,
                        "renderer-screenshot",
                        Path.ChangeExtension(imagePath, ".renderer.png")));
                }

                artifacts.Add(CreateVerifiedPfmArtifact(
                    contract,
                    directory,
                    bookmark.Name,
                    "final-indirect",
                    "linear-final-indirect",
                    contract.GetRelativeLinearFinalIndirectPath(bookmark.Name)));

                string snapshotPath = Path.Combine(
                    Path.GetDirectoryName(contract.GetRelativeImagePath(bookmark.Name, contract.Outputs[0]))!,
                    "performance-snapshot.json");
                artifacts.Add(CreateVerifiedTextArtifact(
                    contract,
                    directory,
                    bookmark.Name,
                    "beauty",
                    "performance-snapshot",
                    snapshotPath,
                    "{\"schemaVersion\":\"test\"}"));
            }

            contract.WriteRunManifest(
                directory,
                artifacts,
                "completed",
                captureMode: SampleSponzaGiCaptureMode.ProductionTiming,
                storagePackingMode: SimpleDdgiStoragePackingMode.Packed,
                sampledAtlasCoverageMode:
                    SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant);

            string runJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-capture-run.json"));
            Assert.Multiple(() =>
            {
                Assert.That(contract.GetCompletionBlockers(directory, artifacts), Is.Empty);
                Assert.That(runJson, Does.Contain("\"status\": \"completed\""));
                Assert.That(runJson, Does.Contain("\"captureMode\": \"ProductionTiming\""));
                Assert.That(runJson, Does.Contain("\"simpleDdgiStoragePackingMode\": \"Packed\""));
                Assert.That(runJson, Does.Contain("\"simpleDdgiSampledAtlasCoverageMode\": \"ReceiverRelevant\""));
                Assert.That(runJson, Does.Contain("\"sha256\":"));
                Assert.That(runJson, Does.Not.Contain("renderer-screenshot-request"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void VisualMetricGate_IsDeterministicAndExplicitlyRequiresAnApprovedBaseline()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        SampleSponzaGiVisualMetricGate gate = contract.CreateVisualMetricGate(
            SampleSponzaGiCaptureMode.DetailedDiagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(gate.ContractFingerprint, Is.EqualTo(contract.Fingerprint));
            Assert.That(gate.EvaluationStatus, Is.EqualTo("not-evaluated-no-approved-baseline"));
            Assert.That(gate.TimingClassification, Does.Contain("timing-ineligible"));
            Assert.That(gate.ReceiverRois.Select(static roi => roi.Name),
                Is.EquivalentTo(contract.ReceiverRois.Select(static roi => roi.Name)));
            Assert.That(gate.ReceiverRois, Has.All.Matches<SampleSponzaGiVisualMetricRoi>(roi =>
                roi.RequiredMetrics.Any(static metric => metric.RequiresApprovedBaseline) &&
                roi.RequiredOutputs.Contains("direct-only") &&
                roi.RequiredOutputs.Contains("volume-contributor") &&
                roi.RequiredOutputs.Contains("fallback")));
        });
    }

    [Test]
    public void ContractAndRunManifest_KeepTheFingerprintAndRelativeArtifactsTogether()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sponza-gi-capture-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        try
        {
            contract.WriteContract(directory);
            contract.WriteVisualMetricGate(directory, SampleSponzaGiCaptureMode.DetailedDiagnostics);
            contract.WriteRunManifest(
                directory,
                [new SampleSponzaGiCapturedArtifact(
                    contract.LowBookmark.Name,
                    "beauty",
                    "renderer-screenshot-request",
                    contract.GetRelativeImagePath(contract.LowBookmark.Name, contract.Outputs[0]))],
                "running");

            string contractJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-capture-contract.json"));
            string visualMetricJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-visual-metric-gate.json"));
            string runJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-capture-run.json"));
            Assert.Multiple(() =>
            {
                Assert.That(contractJson, Does.Contain(contract.Fingerprint));
                Assert.That(visualMetricJson, Does.Contain(contract.Fingerprint));
                Assert.That(visualMetricJson, Does.Contain("not-evaluated-no-approved-baseline"));
                Assert.That(runJson, Does.Contain(contract.Fingerprint));
                Assert.That(runJson, Does.Contain("renderer-screenshot-request"));
                Assert.That(runJson, Does.Contain("\"simpleDdgiStoragePackingMode\": \"Packed\""));
                Assert.That(runJson, Does.Contain("\"simpleDdgiSampledAtlasCoverageMode\": \"ReceiverRelevant\""));
                Assert.That(runJson, Does.Not.Contain(Path.GetFullPath(directory)));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SampleSponzaGiCapturedArtifact CreateVerifiedPngArtifact(
        SampleSponzaGiCaptureContract contract,
        string directory,
        string bookmark,
        string output,
        string kind,
        string relativePath)
    {
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, ValidPng);
        return Verify(contract, directory, new SampleSponzaGiCapturedArtifact(bookmark, output, kind, relativePath));
    }

    private static SampleSponzaGiCapturedArtifact CreateVerifiedPfmArtifact(
        SampleSponzaGiCaptureContract contract,
        string directory,
        string bookmark,
        string output,
        string kind,
        string relativePath)
    {
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        PfmLinearImageCodec.WriteAtomic(
            fullPath,
            CreateLockedLinearImagePixels(0.02f),
            SampleSponzaGiCaptureContract.LockedWidth,
            SampleSponzaGiCaptureContract.LockedHeight);
        return Verify(
            contract,
            directory,
            new SampleSponzaGiCapturedArtifact(
                bookmark,
                output,
                kind,
                relativePath));
    }

    private static float[] CreateLockedLinearImagePixels(float value)
    {
        var pixels = new float[
            SampleSponzaGiCaptureContract.LockedWidth *
            SampleSponzaGiCaptureContract.LockedHeight * 3];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static SampleSponzaGiCapturedArtifact CreateVerifiedTextArtifact(
        SampleSponzaGiCaptureContract contract,
        string directory,
        string bookmark,
        string output,
        string kind,
        string relativePath,
        string content)
    {
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return Verify(contract, directory, new SampleSponzaGiCapturedArtifact(bookmark, output, kind, relativePath));
    }

    private static SampleSponzaGiCapturedArtifact Verify(
        SampleSponzaGiCaptureContract contract,
        string directory,
        SampleSponzaGiCapturedArtifact artifact)
    {
        bool verified = contract.TryVerifyArtifact(directory, artifact, out SampleSponzaGiCapturedArtifact result, out string reason);
        Assert.That(verified, Is.True, reason);
        return result;
    }

    private static ReflectionProbeLifecycleFrameSnapshot CreateReflectionLifecycleFrame(
        int frameSlot,
        ulong frameSerial,
        int captureFaceUnits,
        int prefilterMipUnits,
        int publishCopyUnits) => new(
        Valid: true,
        FrameSlot: frameSlot,
        FrameSerial: frameSerial,
        GpuTimingRecorded: true,
        Lifecycle: new ReflectionProbeLifecycleSnapshot(
            QueuedCount: 1,
            ActiveCount: 1,
            State: ReflectionProbeCaptureState.CapturingFaces,
            AwaitingGpuCompletionCount: 0,
            PublishedCount: 0,
            CapturesStartedThisFrame: 1,
            CapturesCompletedThisFrame: 0,
            CaptureFaceUnitsThisFrame: captureFaceUnits,
            PrefilterMipUnitsThisFrame: prefilterMipUnits,
            PublishCopyUnitsThisFrame: publishCopyUnits,
            CapturesStartedTotal: frameSerial,
            CapturesCompletedTotal: frameSerial - 1,
            CapturesPublishedTotal: frameSerial - 1,
            CaptureFaceUnitsTotal: (ulong)captureFaceUnits,
            PrefilterMipUnitsTotal: (ulong)prefilterMipUnits,
            PublishCopyUnitsTotal: (ulong)publishCopyUnits));

    private static RendererDiagnostics ReadyTransportDiagnostics() =>
        RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiWarmupState = DdgiRuntimeWarmupState.SteadyState,
            SimpleDdgiTransportGlobalConvergencePending = 0,
            SimpleDdgiTransportTailCertificationEnabled = true,
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    TailPhase = SimpleDdgiTransportPhase.Certified,
                    TailReason =
                        SimpleDdgiTransportCertificationReason.Certified,
                    TailExpectedParticipantCount = 64,
                    TailAuditedParticipantCount = 64,
                    TailCertificateCurrent = true,
                    TailConvergenceDeadlineFrames = 120,
                    TailAuditReadbackDeadlineFrames = 12
                },
            SimpleDdgiRefinement = new SimpleDdgiRefinementBrickDiagnostics(
                Requested: true,
                RequestedBrickCount: 1,
                AdmittedBrickCount: 1,
                ReceiverReadyBrickCount: 1,
                BaseFallbackBrickCount: 0,
                AllocatedProbeCount: 216,
                EvictionCount: 0,
                TopologyChangedThisFrame: false,
                AdmissionStatus: "ready")
            {
                ReceiverBlendWeight = 1.0f
            }
        };

    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL0NwAAAABJRU5ErkJggg==");
}
