using System.Security.Cryptography;
using System.Text.Json;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkSponzaSceneAnimationTests
{
    [Test]
    public void AuthoredTopology_RequiresExactDistinctStrutObjects()
    {
        (Scene scene, Animator joints, Animator surface) = CreateScene();

        Assert.DoesNotThrow(() =>
            SampleBenchmarkSponzaSceneAnimationContract
                .ValidateAuthoredObjects(scene));

        var extra = new SkinnedRenderObject("mesh", "material")
        {
            Id = Guid.NewGuid(),
            Name = "AnimatedCharacter.Strut.Extra",
            AssetReference = new SceneAssetReference
            {
                Path = SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                SubObject = "2"
            },
            Animator = CreateAnimator()
        };
        scene.Add(extra);
        Assert.Throws<InvalidDataException>(() =>
            SampleBenchmarkSponzaSceneAnimationContract
                .ValidateAuthoredObjects(scene));

        (Scene sharedScene, Animator shared, _) = CreateScene(
            shareAnimator: true);
        Assert.That(shared, Is.Not.Null);
        Assert.Throws<InvalidDataException>(() =>
            SampleBenchmarkSponzaSceneAnimationContract
                .ValidateAuthoredObjects(sharedScene));
        Assert.That(joints, Is.Not.SameAs(surface));
    }

    [Test]
    public void PhaseZeroTiming_HoldsWithoutPoseOrRevisionMutation()
    {
        (Scene scene, Animator joints, Animator surface) = CreateScene();
        string directory = CreateDirectory();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            3,
            SampleBenchmarkActivation.None,
            SampleBenchmarkTrajectoryKind.SponzaLow);

        observer.PrepareTimingFrame(
            scene,
            authoredRouteFrameIndex: 0,
            measurementFrame: false,
            hold: false);
        ulong jointRevision = joints.PoseRevision;
        ulong surfaceRevision = surface.PoseRevision;
        for (int frame = 0; frame < 3; frame++)
        {
            joints.Update(0.5f);
            surface.Update(0.5f);
            observer.PrepareTimingFrame(
                scene,
                authoredRouteFrameIndex: frame,
                measurementFrame: true,
                hold: false);
            observer.RecordTimingFrame(frame, frame);
        }
        observer.PrepareTimingFrame(
            scene,
            authoredRouteFrameIndex: 2,
            measurementFrame: false,
            hold: true);
        SampleBenchmarkSponzaSceneAnimationBuild build = observer.BuildTiming(
            Path.Combine(directory, "phase-zero.bin"));

        Assert.Multiple(() =>
        {
            Assert.That(build.Evidence.Passed, Is.True);
            Assert.That(build.Frames, Has.Count.EqualTo(3));
            Assert.That(build.Frames.SelectMany(static frame => frame.Animators)
                .All(animator => animator.TimeSeconds == 0f), Is.True);
            Assert.That(joints.PoseRevision, Is.EqualTo(jointRevision));
            Assert.That(surface.PoseRevision, Is.EqualTo(surfaceRevision));
            Assert.That(build.Frames.Select(static frame => frame.FrameHash)
                .Distinct().Count(), Is.EqualTo(1));
        });

        joints.Enabled = true;
        joints.Seek(0.25f);
        Assert.Throws<InvalidDataException>(() =>
            observer.PrepareTimingFrame(
                scene,
                authoredRouteFrameIndex: 2,
                measurementFrame: false,
                hold: true));
    }

    [Test]
    public void StableSkinningOutputState_DoesNotRepublishSceneContent()
    {
        var scene = new Scene();
        var skinned = new SkinnedRenderObject("mesh", "material");
        scene.Add(skinned);

        SkinningManager.ApplySkinningOutputState(
            skinned,
            enabled: true,
            skinnedVertexOffset: 42u);
        ulong enabledRevision = scene.RenderPayloadRevision;

        SkinningManager.ApplySkinningOutputState(
            skinned,
            enabled: true,
            skinnedVertexOffset: 42u);

        Assert.Multiple(() =>
        {
            Assert.That(scene.RenderPayloadRevision, Is.EqualTo(enabledRevision));
            Assert.That(skinned.SkinningEnabled, Is.True);
            Assert.That(skinned.SkinnedVertexOffset, Is.EqualTo(42u));
        });

        SkinningManager.ApplySkinningOutputState(
            skinned,
            enabled: false,
            skinnedVertexOffset: 99u);
        ulong disabledRevision = scene.RenderPayloadRevision;
        SkinningManager.ApplySkinningOutputState(
            skinned,
            enabled: false,
            skinnedVertexOffset: 99u);

        Assert.Multiple(() =>
        {
            Assert.That(disabledRevision, Is.GreaterThan(enabledRevision));
            Assert.That(scene.RenderPayloadRevision, Is.EqualTo(disabledRevision));
            Assert.That(skinned.SkinningEnabled, Is.False);
            Assert.That(skinned.SkinnedVertexOffset, Is.Zero);
        });
    }

    [Test]
    public void DirectionalTiming_AppliesEachMeasuredRoutePoseExactlyOnce()
    {
        (Scene scene, Animator joints, Animator surface) = CreateScene();
        string directory = CreateDirectory();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            4,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaLow);

        observer.PrepareTimingFrame(scene, 0, measurementFrame: false, hold: false);
        ulong jointRouteZeroRevision = joints.PoseRevision;
        ulong surfaceRouteZeroRevision = surface.PoseRevision;
        for (int frame = 0; frame < 4; frame++)
        {
            observer.PrepareTimingFrame(
                scene,
                frame,
                measurementFrame: true,
                hold: false);
            observer.RecordTimingFrame(frame, frame);
        }
        SampleBenchmarkSponzaSceneAnimationBuild build = observer.BuildTiming(
            Path.Combine(directory, "directional.bin"));
        SampleBenchmarkSponzaSceneAnimationSidecarContent admitted =
            SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                build.Evidence.SidecarPath,
                build.Evidence.SidecarSha256,
                build.Evidence.Mode,
                build.Evidence.SampleCount,
                build.Evidence.ConfigurationFingerprint,
                build.Evidence.SequenceHash);

        Assert.Multiple(() =>
        {
            Assert.That(build.Evidence.Passed, Is.True);
            Assert.That(build.Frames[0].Animators[0].PoseRevision,
                Is.EqualTo(jointRouteZeroRevision));
            Assert.That(build.Frames[0].Animators[1].PoseRevision,
                Is.EqualTo(surfaceRouteZeroRevision));
            Assert.That(admitted.Frames.Select(static frame =>
                    frame.Animators[0].PoseRevision),
                Is.EqualTo(new ulong[] { 0, 1, 2, 3 }));
            Assert.That(admitted.Frames.Select(static frame =>
                    frame.Animators[0].TimeSeconds),
                Is.EqualTo(new[]
                {
                    0f,
                    HelloGame.BenchmarkSimulationDeltaSeconds,
                    2f * HelloGame.BenchmarkSimulationDeltaSeconds,
                    3f * HelloGame.BenchmarkSimulationDeltaSeconds
                }));
            Assert.That(joints.PoseRevision,
                Is.EqualTo(jointRouteZeroRevision + 3));
            Assert.That(surface.PoseRevision,
                Is.EqualTo(surfaceRouteZeroRevision + 3));
        });
    }

    [Test]
    public void DirectionalMovingWarmup_EndsAtLastPoseThenAdvancesToRouteZero()
    {
        (Scene scene, Animator joints, _) = CreateScene();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            2,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaHorizontal);

        for (int frame = 0; frame < 300; frame++)
        {
            observer.PrepareTimingFrame(
                scene,
                frame,
                measurementFrame: false,
                hold: false);
        }
        Assert.That(joints.TimeSeconds, Is.EqualTo(
            Normalize(299f * HelloGame.BenchmarkSimulationDeltaSeconds, 2f)));
        ulong lastWarmupRevision = joints.PoseRevision;

        observer.PrepareTimingFrame(
            scene,
            0,
            measurementFrame: true,
            hold: false);
        observer.RecordTimingFrame(0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(joints.TimeSeconds, Is.Zero);
            Assert.That(joints.PoseRevision,
                Is.EqualTo(lastWarmupRevision + 1));
        });
    }

    [Test]
    public void DirectionalQualityWarmup_CyclesThenStartsOneContinuousRoute()
    {
        (Scene scene, Animator joints, _) = CreateScene();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            2,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaHorizontal);
        for (int frame = 0; frame < 300; frame++)
        {
            _ = observer.PrepareQualityFrame(
                scene,
                frame,
                evidenceFrameIndex: null,
                hold: false);
        }
        ulong lastWarmupRevision = joints.PoseRevision;

        SampleBenchmarkActivationFrameState routeZero =
            observer.PrepareQualityFrame(
                scene,
                0,
                evidenceFrameIndex: 0,
                hold: false);
        SampleBenchmarkActivationFrameState routeOne =
            observer.PrepareQualityFrame(
                scene,
                1,
                evidenceFrameIndex: 1,
                hold: false);

        Assert.Multiple(() =>
        {
            Assert.That(routeZero.RouteFrameIndex, Is.Zero);
            Assert.That(routeZero.Animators[0].TimeSeconds, Is.Zero);
            Assert.That(routeZero.Animators[0].PoseRevision,
                Is.EqualTo(lastWarmupRevision + 1));
            Assert.That(routeOne.Animators[0].PoseRevision,
                Is.EqualTo(routeZero.Animators[0].PoseRevision + 1));
        });
    }

    [Test]
    public void QualityHold_RejectsMutationAfterFinalRouteFrame()
    {
        (Scene scene, Animator joints, _) = CreateScene();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            1,
            SampleBenchmarkActivation.None,
            SampleBenchmarkTrajectoryKind.SponzaLow);
        _ = observer.PrepareQualityFrame(
            scene,
            0,
            evidenceFrameIndex: null,
            hold: false);
        _ = observer.PrepareQualityFrame(
            scene,
            0,
            evidenceFrameIndex: 0,
            hold: false);
        joints.Enabled = true;
        joints.Seek(0.25f);

        Assert.That(
            () => observer.PrepareQualityFrame(
                scene,
                0,
                evidenceFrameIndex: null,
                hold: true),
            Throws.Exception.With.Message.Contains("locked route state"));
    }

    [Test]
    public void DirectionalQualityHold_AcceptsOneExactFinalRouteDrainDraw()
    {
        (Scene scene, _, _) = CreateScene();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            3,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaHorizontal);
        for (int routeFrame = 0; routeFrame < 3; routeFrame++)
        {
            _ = observer.PrepareQualityFrame(
                scene,
                routeFrame,
                evidenceFrameIndex: routeFrame,
                hold: false);
        }

        Assert.DoesNotThrow(() =>
            observer.PrepareQualityFrame(
                scene,
                authoredRouteFrameIndex: 2,
                evidenceFrameIndex: null,
                hold: true));
    }

    [Test]
    public void Sidecar_RejectsHashTamperTruncationAndRouteReorder()
    {
        (Scene scene, _, _) = CreateScene();
        string directory = CreateDirectory();
        SampleBenchmarkSponzaSceneAnimationBuild build =
            CreateDirectionalBuild(scene, directory, 3);
        byte[] bytes = File.ReadAllBytes(build.Evidence.SidecarPath);

        string tamperedPath = Path.Combine(directory, "tampered.bin");
        byte[] tampered = bytes.ToArray();
        tampered[^1] ^= 0x40;
        File.WriteAllBytes(tamperedPath, tampered);
        Assert.Throws<InvalidDataException>(() =>
            SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                tamperedPath,
                build.Evidence.SidecarSha256,
                build.Evidence.Mode,
                3,
                build.Evidence.ConfigurationFingerprint,
                build.Evidence.SequenceHash));

        string truncatedPath = Path.Combine(directory, "truncated.bin");
        byte[] truncated = bytes[..^7];
        File.WriteAllBytes(truncatedPath, truncated);
        Assert.That(
            () => SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                truncatedPath,
                Sha256(truncated),
                build.Evidence.Mode,
                3,
                build.Evidence.ConfigurationFingerprint,
                build.Evidence.SequenceHash),
            Throws.InstanceOf<IOException>());

        SampleBenchmarkActivationFrameState[] reordered =
        [build.Frames[0], build.Frames[2], build.Frames[1]];
        string reorderedSequence =
            SampleBenchmarkSponzaSceneAnimationContract.CreateSequenceHash(
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute,
                reordered,
                build.Evidence.ConfigurationFingerprint);
        string reorderedPath = Path.Combine(directory, "reordered.bin");
        SampleEvidenceFileContent reorderedEvidence =
            SampleBenchmarkSponzaSceneAnimationSidecar.Write(
                reorderedPath,
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute,
                reordered,
                build.Evidence.ConfigurationFingerprint,
                reorderedSequence);
        Assert.Throws<InvalidDataException>(() =>
            SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                reorderedPath,
                reorderedEvidence.Sha256,
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute,
                3,
                build.Evidence.ConfigurationFingerprint,
                reorderedSequence));
    }

    [Test]
    public void QualityReferenceCheckpointStates_MustMatchSidecarFramesAndRelativeRevisions()
    {
        string directory = CreateDirectory();
        SampleBenchmarkSponzaSceneAnimationBuild build =
            CreateDirectionalBuild(CreateScene().Scene, directory, 3);
        SampleBenchmarkSponzaSceneAnimationSidecarContent sidecar =
            SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                build.Evidence.SidecarPath,
                build.Evidence.SidecarSha256,
                build.Evidence.Mode,
                build.Evidence.SampleCount,
                build.Evidence.ConfigurationFingerprint,
                build.Evidence.SequenceHash);
        SampleBenchmarkQualitySequenceReferenceCheckpoint[] checkpoints =
        [
            CreateReferenceCheckpoint(0, 0, build.Frames[0]),
            CreateReferenceCheckpoint(1, 2, build.Frames[2])
        ];

        Assert.DoesNotThrow(() =>
            SampleBenchmarkQualitySequenceReferenceLoader
                .ValidateCheckpointAnimationAgainstSidecar(
                    SampleBenchmarkActivation.DirectionalShadowMovingCaster,
                    checkpoints,
                    sidecar));

        SampleBenchmarkActivationAnimatorState[] forgedAnimators =
            build.Frames[2].Animators.ToArray();
        forgedAnimators[0] = forgedAnimators[0] with
        {
            PoseRevision = forgedAnimators[0].PoseRevision + 1
        };
        SampleBenchmarkActivationFrameState forgedFrame =
            build.Frames[2] with
            {
                Animators = Array.AsReadOnly(forgedAnimators)
            };
        SampleBenchmarkQualitySequenceReferenceCheckpoint[] forged =
        [
            checkpoints[0],
            CreateReferenceCheckpoint(1, 2, forgedFrame)
        ];
        Assert.That(
            () => SampleBenchmarkQualitySequenceReferenceLoader
                .ValidateCheckpointAnimationAgainstSidecar(
                    SampleBenchmarkActivation.DirectionalShadowMovingCaster,
                    forged,
                    sidecar),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains(
                "authenticated animation sidecar frame"));
    }

    [Test]
    public void TimingPoseCapture_HasNoMeasuredFrameAllocations()
    {
        WarmTimingCaptureJit();
        (Scene scene, _, _) = CreateScene();
        const int frameCount = 16;
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            frameCount,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaLow);
        observer.PrepareTimingFrame(scene, 0, measurementFrame: false, hold: false);
        observer.PrepareTimingFrame(scene, 0, measurementFrame: true, hold: false);
        observer.RecordTimingFrame(0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 1; frame < frameCount; frame++)
        {
            observer.PrepareTimingFrame(
                scene,
                frame,
                measurementFrame: true,
                hold: false);
            observer.RecordTimingFrame(frame, frame);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void EvidenceJson_DoesNotDuplicateRawPoseRoute()
    {
        (Scene scene, _, _) = CreateScene();
        string directory = CreateDirectory();
        SampleBenchmarkSponzaSceneAnimationBuild build =
            CreateDirectionalBuild(scene, directory, 120);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(build.Evidence);

        Assert.Multiple(() =>
        {
            Assert.That(json.Length, Is.LessThan(4096));
            Assert.That(
                EncodingContains(json, "GlobalMatrixComponentBits"),
                Is.False);
            Assert.That(new FileInfo(build.Evidence.SidecarPath).Length,
                Is.GreaterThan(json.Length));
        });
    }

    [Test]
    public void ActivationValidator_RecomputesRawForwardEvidence()
    {
        const int frameCount = SampleBenchmarkActivation.SponzaActivationFrameCount;
        RendererDiagnostics active = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            SimpleDdgiActive = 1,
            ForwardGiBenchmarkSuppressed = 0,
            ForwardGiBenchmarkForcedExact = 0,
            ForwardGiReceiverCacheConsumed = 1,
            ForwardGiDisabledPipelineUsed = 0,
            ForwardGiExactGatherUsed = 0,
            GpuForwardGiGatherMicroseconds = 100
        };
        SampleBenchmarkActivationExecutionFrameEvidence[] frames =
            Enumerable.Range(0, frameCount)
                .Select(index =>
                    SampleBenchmarkActivationExecutionFrameEvidence.Create(
                        index,
                        active))
                .ToArray();
        SampleBenchmarkActivationEvidence evidence =
            SampleBenchmarkActivationEvidenceEvaluator.Evaluate(
                SampleBenchmarkActivation.SponzaForwardGi,
                SampleBenchmarkCaptureVariant.ForwardGiEnabled,
                frameCount,
                SampleBenchmarkActivationExecutionFrameEvidence.Create(
                    -1,
                    RendererDiagnostics.Empty),
                frames,
                new Dictionary<int, Njulf.Rendering.Resources.ReflectionProbeRecaptureRequestSummary>(),
                Array.Empty<SampleBenchmarkActivationFrameState>(),
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                qualitySequence: false);

        IReadOnlyList<string> valid =
            SampleBenchmarkActivationEvidenceValidator.Validate(
                evidence,
                evidence.Activation,
                SampleBenchmarkCaptureVariant.ForwardGiEnabled,
                frameCount,
                qualitySequence: false,
                trajectory: SampleBenchmarkTrajectoryKind.SponzaHorizontal);
        IReadOnlyList<string> tampered =
            SampleBenchmarkActivationEvidenceValidator.Validate(
                evidence with
                {
                    ForwardGiActiveFrameCount = frameCount - 1
                },
                evidence.Activation,
                SampleBenchmarkCaptureVariant.ForwardGiEnabled,
                frameCount,
                qualitySequence: false,
                trajectory: SampleBenchmarkTrajectoryKind.SponzaHorizontal);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Passed, Is.True);
            Assert.That(valid, Is.Empty);
            Assert.That(tampered, Has.Some.Contains(
                "do not match the persisted raw frame evidence"));
        });
    }

    [Test]
    public void DirectionalProvenance_RejectsDuplicateAndMissingCascadeLayers()
    {
        DirectionalShadowCacheLayerProvenance[] exact = Enumerable.Range(0, 4)
            .Select(CreateReusedDirectionalLayer)
            .ToArray();
        DirectionalShadowRuntimeDiagnostics valid =
            DirectionalShadowRuntimeDiagnostics.Empty with
            {
                StaticCacheActiveMask = 0b1111,
                StaticCacheValidMask = 0b1111,
                StaticCacheRefreshMask = 0,
                StaticCacheReuseMask = 0b1111,
                CacheLayerProvenance = exact
            };
        DirectionalShadowCacheLayerProvenance[] duplicate = exact.ToArray();
        duplicate[^1] = CreateReusedDirectionalLayer(0);
        DirectionalShadowRuntimeDiagnostics duplicated = valid with
        {
            CacheLayerProvenance = duplicate
        };
        DirectionalShadowRuntimeDiagnostics missing = valid with
        {
            CacheLayerProvenance = exact[..^1]
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleBenchmarkActivationEvidenceEvaluator
                    .HasExactDirectionalCacheProvenance(valid),
                Is.True);
            Assert.That(
                SampleBenchmarkActivationEvidenceEvaluator
                    .HasExactDirectionalCacheProvenance(duplicated),
                Is.False);
            Assert.That(
                SampleBenchmarkActivationEvidenceEvaluator
                    .HasExactDirectionalCacheProvenance(missing),
                Is.False);
        });
    }

    [Test]
    public void PairComparer_AuthenticatesCommonSponzaSidecarAndPath()
    {
        string directory = CreateDirectory();
        SampleBenchmarkSponzaSceneAnimationBuild leftBuild =
            CreatePhaseZeroBuild(
                CreateScene().Scene,
                Path.Combine(directory, "left.bin"));
        SampleBenchmarkSponzaSceneAnimationBuild rightBuild =
            CreatePhaseZeroBuild(
                CreateScene().Scene,
                Path.Combine(directory, "right.bin"));
        SampleBenchmarkReport left = CreateSponzaReport(
            "sponza-animation-pair",
            leftBuild.Evidence);
        SampleBenchmarkReport right = CreateSponzaReport(
            "sponza-animation-pair",
            rightBuild.Evidence);

        SampleBenchmarkPairComparison accepted =
            SampleBenchmarkPairComparer.Compare(left, right);
        SampleBenchmarkPairComparison forgedSequence =
            SampleBenchmarkPairComparer.Compare(
                left,
                right with
                {
                    SponzaSceneAnimationEvidence =
                        right.SponzaSceneAnimationEvidence with
                        {
                            SequenceHash = Identity('f')
                        },
                    CaptureContract = right.CaptureContract with
                    {
                        SponzaSceneAnimationSequenceHash = Identity('f')
                    }
                });
        string relativePath = Path.GetRelativePath(
            Environment.CurrentDirectory,
            right.SponzaSceneAnimationEvidence.SidecarPath);
        SampleBenchmarkPairComparison noncanonicalPath =
            SampleBenchmarkPairComparer.Compare(
                left,
                right with
                {
                    SponzaSceneAnimationEvidence =
                        right.SponzaSceneAnimationEvidence with
                        {
                            SidecarPath = relativePath
                        }
                });

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Comparable, Is.True,
                string.Join("; ", accepted.Failures));
            Assert.That(forgedSequence.Comparable, Is.False);
            Assert.That(forgedSequence.Failures,
                Has.Some.Contains("sidecar admission failed"));
            Assert.That(noncanonicalPath.Comparable, Is.False);
            Assert.That(noncanonicalPath.Failures,
                Has.Some.Contains("sidecar path is not canonical"));
        });
    }

    [Test]
    public void ActivationVerificationCli_RecomputesFromReportAndSidecarBytes()
    {
        string directory = CreateDirectory();
        SampleBenchmarkSponzaSceneAnimationBuild build = CreatePhaseZeroBuild(
            CreateScene().Scene,
            Path.Combine(directory, "animation.bin"));
        SampleBenchmarkReport report = CreateSponzaReport(
            "verification",
            build.Evidence);
        string reportPath = Path.Combine(directory, "report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report));
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkActivationVerificationCli.TryRun(
            [
                SampleBenchmarkActivationVerificationCli.VerifyOption,
                reportPath
            ],
            output,
            error,
            out int exitCode);
        SampleBenchmarkActivationVerificationResult result =
            JsonSerializer.Deserialize<
                SampleBenchmarkActivationVerificationResult>(
                output.ToString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.Zero, error.ToString());
            Assert.That(result.Passed, Is.True);
            Assert.That(result.ReportPath, Is.EqualTo(
                Path.GetFullPath(reportPath)));
            Assert.That(result.SponzaSceneAnimationSidecarSha256,
                Is.EqualTo(build.Evidence.SidecarSha256));
            Assert.That(error.ToString(), Is.Empty);
        });
    }

    private static SampleBenchmarkSponzaSceneAnimationBuild
        CreateDirectionalBuild(Scene scene, string directory, int frameCount)
    {
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            frameCount,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaLow);
        observer.PrepareTimingFrame(scene, 0, measurementFrame: false, hold: false);
        for (int frame = 0; frame < frameCount; frame++)
        {
            observer.PrepareTimingFrame(
                scene,
                frame,
                measurementFrame: true,
                hold: false);
            observer.RecordTimingFrame(frame, frame);
        }
        return observer.BuildTiming(Path.Combine(directory, "route.bin"));
    }

    private static SampleBenchmarkSponzaSceneAnimationBuild
        CreatePhaseZeroBuild(Scene scene, string path)
    {
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            1,
            SampleBenchmarkActivation.None,
            SampleBenchmarkTrajectoryKind.SponzaLow);
        observer.PrepareTimingFrame(scene, 0, measurementFrame: false, hold: false);
        observer.PrepareTimingFrame(scene, 0, measurementFrame: true, hold: false);
        observer.RecordTimingFrame(0, 0);
        return observer.BuildTiming(path);
    }

    private static DirectionalShadowCacheLayerProvenance
        CreateReusedDirectionalLayer(int cascadeIndex) => new(
            cascadeIndex,
            Active: 1,
            CacheSignature: 1,
            ResourceGeneration: 1,
            CacheState: DirectionalShadowCacheLayerState.Valid,
            CopiedFromCache: 1,
            RefreshedThisFrame: 0,
            ExplicitlyCleared: 0,
            DynamicWorkAppended: 1,
            FoliageWorkAppended: 0,
            FinalWorkingLayerValid: 1,
            SubmissionSerial: 1);

    private static SampleBenchmarkQualitySequenceReferenceCheckpoint
        CreateReferenceCheckpoint(
            int ordinal,
            int routeFrameIndex,
            SampleBenchmarkActivationFrameState activationFrame) => new(
            ordinal,
            routeFrameIndex,
            routeFrameIndex,
            $"checkpoint-{routeFrameIndex:D4}.pfm",
            new string('a', 64),
            SampleBenchmarkQualityCheckpointCatalog.RequiredWidth,
            SampleBenchmarkQualityCheckpointCatalog.RequiredHeight,
            $"capture-{ordinal}",
            (ulong)routeFrameIndex,
            null!,
            Identity('a'),
            Identity('b'),
            1,
            Identity('c'),
            null!,
            null!)
        {
            ActivationFrameState = activationFrame
        };

    private static SampleBenchmarkReport CreateSponzaReport(
        string pairId,
        SampleBenchmarkSponzaSceneAnimationEvidence animation)
    {
        const SamplePerformanceScenario scenario =
            SamplePerformanceScenario.GiSponzaRightWallStationary;
        const SampleBenchmarkTrajectoryKind trajectory =
            SampleBenchmarkTrajectoryKind.SponzaLow;
        const SampleBistroQualityCaptureVariant bistroVariant =
            SampleBistroQualityCaptureVariant.SunScaleStep;
        string trajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                trajectory,
                bistroVariant);
        RendererDiagnostics lastDiagnostics = RendererDiagnostics.Empty with
        {
            CaptureRun = RendererDiagnostics.Empty.CaptureRun with
            {
                Scenario = scenario.ToString()
            }
        };
        SampleBenchmarkTimingStats timing = new(
            "frame",
            1,
            1.0,
            1.0,
            1.0,
            1.0)
        {
            MedianMilliseconds = 1.0,
            P50Milliseconds = 1.0,
            P99Milliseconds = 1.0
        };
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            new SampleBenchmarkOptions(
                Enabled: true,
                WarmupFrameCount: 1,
                MeasureFrameCount: 1,
                ReportPath: null)
            {
                Trajectory = trajectory,
                TrajectoryBistroVariant = bistroVariant,
                TrajectoryFingerprint = trajectoryFingerprint
            },
            scenario,
            WarmupFrameCount: 1,
            MeasurementFrameCount: 1,
            FirstMeasurementFrameIndex: 1,
            LastMeasurementFrameIndex: 1,
            CpuFrameMilliseconds: timing,
            GpuFrameMilliseconds: timing,
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: 1,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: [],
            CpuStages: [],
            Findings: [],
            BudgetMetrics: [],
            LastDiagnostics: lastDiagnostics)
        {
            ActivationEvidence = new SampleBenchmarkActivationEvidence(
                SampleBenchmarkActivationEvidence.CurrentSchema,
                SampleBenchmarkActivation.None,
                SampleBenchmarkActivation.CreateFingerprint(
                    SampleBenchmarkActivation.None),
                Passed: true,
                MeasuredSampleCount: 1,
                Failures: Array.Empty<string>()),
            SponzaSceneAnimationEvidence = animation,
            CaptureContract = new SampleBenchmarkCaptureContract(
                Comparable: true,
                ProductionTiming: true,
                PairId: pairId,
                Variant: SampleBenchmarkCaptureVariant.Baseline,
                IdentityHash: Identity('a'),
                Mismatches: [])
            {
                FullIdentityHash = Identity('b'),
                Trajectory = SampleBenchmarkTrajectory.SponzaLowName,
                TrajectoryFingerprint = trajectoryFingerprint,
                TrajectoryFrameCount = 1,
                TrajectoryRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
                    trajectory,
                    bistroVariant),
                TrajectorySequenceHash = Identity('e'),
                Activation = SampleBenchmarkActivation.None,
                ActivationFingerprint =
                    SampleBenchmarkActivation.CreateFingerprint(
                        SampleBenchmarkActivation.None),
                SponzaSceneAnimationFingerprint = animation.Fingerprint,
                SponzaSceneAnimationMode = animation.Mode,
                SponzaSceneAnimationConfigurationFingerprint =
                    animation.ConfigurationFingerprint,
                SponzaSceneAnimationSequenceHash = animation.SequenceHash,
                SponzaSceneAnimationSidecarSha256 = animation.SidecarSha256
            }
        };
    }

    private static void WarmTimingCaptureJit()
    {
        (Scene scene, _, _) = CreateScene();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            2,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkTrajectoryKind.SponzaLow);
        observer.PrepareTimingFrame(scene, 0, measurementFrame: false, hold: false);
        for (int frame = 0; frame < 2; frame++)
        {
            observer.PrepareTimingFrame(
                scene,
                frame,
                measurementFrame: true,
                hold: false);
            observer.RecordTimingFrame(frame, frame);
        }
    }

    private static (Scene Scene, Animator Joints, Animator Surface)
        CreateScene(bool shareAnimator = false)
    {
        Animator joints = CreateAnimator();
        Animator surface = shareAnimator ? joints : CreateAnimator();
        var scene = new Scene();
        scene.Add(CreateObject(
            SampleBenchmarkSponzaSceneAnimationContract.JointObjectId,
            SampleBenchmarkSponzaSceneAnimationContract.JointName,
            SampleBenchmarkSponzaSceneAnimationContract.JointSubObject,
            joints));
        scene.Add(CreateObject(
            SampleBenchmarkSponzaSceneAnimationContract.SurfaceObjectId,
            SampleBenchmarkSponzaSceneAnimationContract.SurfaceName,
            SampleBenchmarkSponzaSceneAnimationContract.SurfaceSubObject,
            surface));
        return (scene, joints, surface);
    }

    private static SkinnedRenderObject CreateObject(
        Guid id,
        string name,
        string subObject,
        Animator animator) => new("mesh", "material")
    {
        Id = id,
        Name = name,
        AssetReference = new SceneAssetReference
        {
            Path = SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
            SubObject = subObject
        },
        SkinIndex = 0,
        Animator = animator
    };

    private static Animator CreateAnimator()
    {
        var joint = new SkeletonJoint
        {
            Name = "Root",
            ParentIndex = -1,
            LocalBindPose = AnimationTransform.Identity,
            LocalBindTransform = Matrix4x4.Identity,
            InverseBindMatrix = Matrix4x4.Identity
        };
        var skeleton = new Skeleton
        {
            Name = "StrutSkeleton",
            Joints = [joint],
            RootJointIndex = 0
        };
        var skin = new Skin
        {
            Name = "StrutSkin",
            Skeleton = skeleton,
            JointIndices = [0],
            InverseBindMatrices = [Matrix4x4.Identity]
        };
        var clip = new AnimationClip
        {
            Name = "StrutMove",
            DurationSeconds = 2f,
            Channels =
            [
                new AnimationChannel
                {
                    TargetJointIndex = 0,
                    Path = AnimationChannelPath.Translation,
                    Sampler = new AnimationSampler
                    {
                        InputTimes = [0f, 2f],
                        OutputValues =
                        [
                            new Vector4(0f, 0f, 0f, 0f),
                            new Vector4(2f, 0f, 0f, 0f)
                        ]
                    }
                }
            ]
        };
        return new Animator(skeleton, [skin], [clip]);
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sponza-animation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Identity(char value) =>
        "sha256:" + new string(value, 64);

    private static bool EncodingContains(byte[] utf8, string value) =>
        System.Text.Encoding.UTF8.GetString(utf8).Contains(
            value,
            StringComparison.Ordinal);

    private static float Normalize(float time, float duration)
    {
        float wrapped = time % duration;
        return wrapped < 0f ? wrapped + duration : wrapped;
    }
}
