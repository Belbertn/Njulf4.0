using System.Security.Cryptography;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkControlledIsolationTests
{
    private const int TimingFrameCount =
        SampleBenchmarkActivation.DirectionalTimingFrameCount;
    private const int QualityFrameCount =
        SampleBenchmarkActivation.SponzaActivationFrameCount;

    [Test]
    public void Comparer_AcceptsDistinctWorkloadPairIdsAndAuthenticatesRoles()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport cached = CreateTimingReport(
            directory,
            forced: false,
            pairId: "release-cycle-3-directional-cached");
        SampleBenchmarkReport forced = CreateTimingReport(
            directory,
            forced: true,
            pairId: "release-cycle-3-directional-forced");

        SampleBenchmarkControlledIsolationComparison comparison =
            SampleBenchmarkControlledIsolationComparer.Compare(cached, forced);
        SampleBenchmarkControlledIsolationComparison reversed =
            SampleBenchmarkControlledIsolationComparer.Compare(forced, cached);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Passed, Is.True,
                string.Join("; ", comparison.Failures));
            Assert.That(reversed.Passed, Is.True,
                string.Join("; ", reversed.Failures));
            Assert.That(comparison.CachedPairId,
                Is.EqualTo("release-cycle-3-directional-cached"));
            Assert.That(comparison.ForcedPairId,
                Is.EqualTo("release-cycle-3-directional-forced"));
            Assert.That(comparison.ControlledIsolationPairId,
                Does.StartWith("sha256:"));
            Assert.That(reversed.ControlledIsolationPairId,
                Is.EqualTo(comparison.ControlledIsolationPairId));
            Assert.That(comparison.Timing.DirectionalShadowDeltaMilliseconds,
                Is.EqualTo(0.4).Within(1.0e-12));
            Assert.That(comparison.CachedActivationFingerprint,
                Is.Not.EqualTo(comparison.ForcedActivationFingerprint));
            Assert.That(comparison.CachedActivationExecutionSequenceHash,
                Is.Not.EqualTo(
                    comparison.ForcedActivationExecutionSequenceHash));
            Assert.That(comparison.ControlledIsolationSequenceHash,
                Is.EqualTo(cached.CaptureContract
                    .ControlledIsolationSequenceHash));
            Assert.That(comparison.CachedSettingsFingerprint,
                Is.Not.EqualTo(comparison.ForcedSettingsFingerprint));
            Assert.That(comparison.ControlledIsolationSettingsFingerprint,
                Is.EqualTo(cached.CaptureContract
                    .ControlledIsolationSettingsFingerprint));
        });
    }

    [Test]
    public void Comparer_RejectsMidRouteStateAndUnrelatedSettingsDifferences()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport cached = CreateTimingReport(
            directory,
            forced: false,
            pairId: "cached-workload");
        SampleBenchmarkReport forced = CreateTimingReport(
            directory,
            forced: true,
            pairId: "forced-workload");

        SampleBenchmarkControlledIsolationFrameEvidence[] changedFrames =
            forced.CaptureContract.ControlledIsolationFrames.ToArray();
        PerformanceCaptureCameraMetadata camera = changedFrames[119].Camera;
        changedFrames[119] = changedFrames[119] with
        {
            Camera = camera with { PositionX = camera.PositionX + 1f }
        };
        SampleBenchmarkCaptureContract changedSequenceContract =
            forced.CaptureContract with
            {
                ControlledIsolationFrames = Array.AsReadOnly(changedFrames)
            };
        changedSequenceContract = changedSequenceContract with
        {
            ControlledIsolationSequenceHash =
                RecomputeControlledSequenceHash(
                    forced,
                    changedSequenceContract)
        };
        SampleBenchmarkControlledIsolationComparison changedMiddleFrame =
            SampleBenchmarkControlledIsolationComparer.Compare(
                cached,
                forced with { CaptureContract = changedSequenceContract });

        SampleBenchmarkControlledIsolationFrameEvidence[] changedCacheFrames =
            forced.CaptureContract.ControlledIsolationFrames.ToArray();
        SampleBenchmarkControlledIsolationCascadeEvidence[] changedCascades =
            changedCacheFrames[77].Cascades.ToArray();
        changedCascades[0] = changedCascades[0] with
        {
            CacheSignature = changedCascades[0].CacheSignature + 1
        };
        changedCacheFrames[77] = changedCacheFrames[77] with
        {
            Cascades = Array.AsReadOnly(changedCascades)
        };
        SampleBenchmarkCaptureContract changedCacheContract =
            forced.CaptureContract with
            {
                ControlledIsolationFrames =
                    Array.AsReadOnly(changedCacheFrames)
            };
        changedCacheContract = changedCacheContract with
        {
            ControlledIsolationSequenceHash =
                RecomputeControlledSequenceHash(
                    forced,
                    changedCacheContract)
        };
        SampleBenchmarkControlledIsolationComparison changedCacheSignature =
            SampleBenchmarkControlledIsolationComparer.Compare(
                cached,
                forced with { CaptureContract = changedCacheContract });

        RenderSettings changedSettings = CreateRoleRenderSettings(
            forced: true);
        changedSettings.Shadows.MaxShadowDistance += 1f;
        string changedRawSettings = SampleRenderSettingsFingerprint
            .Capture(changedSettings)[7..];
        string changedFamilySettings = SampleRenderSettingsFingerprint
            .CaptureDirectionalIsolationFamily(changedSettings);
        SampleBenchmarkControlledIsolationFrameEvidence[] changedSettingFrames =
            forced.CaptureContract.ControlledIsolationFrames
                .Select(frame => frame with
                {
                    ControlledSettingsFingerprint = changedFamilySettings
                })
                .ToArray();
        SampleBenchmarkCaptureContract changedSettingsContract =
            forced.CaptureContract with
            {
                ControlledIsolationSettingsFingerprint =
                    changedFamilySettings,
                ControlledIsolationFrames =
                    Array.AsReadOnly(changedSettingFrames)
            };
        changedSettingsContract = changedSettingsContract with
        {
            ControlledIsolationSequenceHash =
                RecomputeControlledSequenceHash(
                    forced,
                    changedSettingsContract)
        };
        MaterialGiProducerIdentity changedProducer =
            forced.ProducerIdentity! with
            {
                SettingsFingerprint = changedRawSettings,
                SourceSettingsFingerprints = [changedRawSettings]
            };
        SampleBenchmarkControlledIsolationComparison changedUnrelatedSetting =
            SampleBenchmarkControlledIsolationComparer.Compare(
                cached,
                forced with
                {
                    ProducerIdentity = changedProducer,
                    CaptureContract = changedSettingsContract
                });

        Assert.Multiple(() =>
        {
            Assert.That(changedMiddleFrame.Passed, Is.False);
            Assert.That(changedMiddleFrame.Failures,
                Has.Some.Contains("normalized full-route sequence differs"));
            Assert.That(changedCacheSignature.Passed, Is.False);
            Assert.That(changedCacheSignature.Failures,
                Has.Some.Contains("normalized full-route sequence differs"));
            Assert.That(changedUnrelatedSetting.Passed, Is.False);
            Assert.That(changedUnrelatedSetting.Failures,
                Has.Some.Contains("normalized family identity differs"));
            Assert.That(
                forced.LastDiagnostics,
                Is.EqualTo((forced with
                    {
                        CaptureContract = changedSequenceContract
                    }).LastDiagnostics));
        });
    }

    [Test]
    public void SettingsFingerprint_NormalizesOnlyForcedRefreshAndRestores()
    {
        RenderSettings cached = CreateRoleRenderSettings(forced: false);
        RenderSettings forced = CreateRoleRenderSettings(forced: true);
        string cachedRaw = SampleRenderSettingsFingerprint.Capture(cached);
        string forcedRaw = SampleRenderSettingsFingerprint.Capture(forced);
        string cachedFamily = SampleRenderSettingsFingerprint
            .CaptureDirectionalIsolationFamily(cached);
        string forcedFamily = SampleRenderSettingsFingerprint
            .CaptureDirectionalIsolationFamily(forced);
        RenderSettings unrelated = CreateRoleRenderSettings(forced: true);
        unrelated.Shadows.DirectionalShadowMapSize += 1024;
        string unrelatedFamily = SampleRenderSettingsFingerprint
            .CaptureDirectionalIsolationFamily(unrelated);

        Assert.Multiple(() =>
        {
            Assert.That(cachedRaw, Is.Not.EqualTo(forcedRaw));
            Assert.That(cachedFamily, Is.EqualTo(forcedFamily));
            Assert.That(unrelatedFamily, Is.Not.EqualTo(cachedFamily));
            Assert.That(
                forced.Shadows.ForceStaticCascadeCacheRefresh,
                Is.True,
                "The family capture must restore the exact live role setting.");
        });
    }

    [Test]
    public void Comparer_RejectsPairReuseStructuralTamperAndBuildMismatch()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport cached = CreateTimingReport(
            directory,
            forced: false,
            pairId: "cached-workload");
        SampleBenchmarkReport forced = CreateTimingReport(
            directory,
            forced: true,
            pairId: "forced-workload");

        SampleBenchmarkControlledIsolationComparison reusedPair =
            SampleBenchmarkControlledIsolationComparer.Compare(
                cached,
                forced with
                {
                    Options = forced.Options with
                    {
                        CapturePairId = cached.CaptureContract.PairId
                    },
                    CaptureContract = forced.CaptureContract with
                    {
                        PairId = cached.CaptureContract.PairId
                    }
                });
        SampleBenchmarkControlledIsolationComparison structuralTamper =
            SampleBenchmarkControlledIsolationComparer.Compare(
                cached,
                forced with
                {
                    ActivationEvidence = forced.ActivationEvidence with
                    {
                        ActivationStructuralSequenceHash = Identity('9')
                    }
                });
        PerformanceCaptureRunMetadata changedRun =
            forced.LastDiagnostics.CaptureRun with
            {
                Commit = new string('f', 40)
            };
        SampleBenchmarkControlledIsolationComparison changedBuild =
            SampleBenchmarkControlledIsolationComparer.Compare(
                cached,
                forced with
                {
                    LastDiagnostics = forced.LastDiagnostics with
                    {
                        CaptureRun = changedRun
                    }
                });

        Assert.Multiple(() =>
        {
            Assert.That(reusedPair.Passed, Is.False);
            Assert.That(reusedPair.Failures,
                Has.Some.Contains("two distinct nonempty workload ABBA"));
            Assert.That(structuralTamper.Passed, Is.False);
            Assert.That(structuralTamper.Failures,
                Has.Some.Contains("persisted raw frame evidence"));
            Assert.That(changedBuild.Passed, Is.False);
            Assert.That(changedBuild.Failures,
                Has.Some.Contains("producer or build identity differs"));
        });
    }

    [Test]
    public void ControlledIsolationCli_EmitsExactReportBoundArtifact()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport cached = CreateTimingReport(
            directory,
            forced: false,
            pairId: "cached-workload");
        SampleBenchmarkReport forced = CreateTimingReport(
            directory,
            forced: true,
            pairId: "forced-workload");
        string cachedPath = Path.GetFullPath(
            Path.Combine(directory, "cached.json"));
        string forcedPath = Path.GetFullPath(
            Path.Combine(directory, "forced.json"));
        File.WriteAllText(cachedPath, JsonSerializer.Serialize(cached));
        File.WriteAllText(forcedPath, JsonSerializer.Serialize(forced));
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkControlledIsolationVerificationCli.TryRun(
            [
                SampleBenchmarkControlledIsolationVerificationCli.VerifyOption,
                cachedPath,
                forcedPath
            ],
            output,
            error,
            out int exitCode);
        SampleBenchmarkControlledIsolationVerificationResult result =
            JsonSerializer.Deserialize<
                SampleBenchmarkControlledIsolationVerificationResult>(
                output.ToString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.Zero, error.ToString());
            Assert.That(result.Passed, Is.True,
                string.Join("; ", result.Failures));
            Assert.That(result.CachedReportPath, Is.EqualTo(cachedPath));
            Assert.That(result.ForcedReportPath, Is.EqualTo(forcedPath));
            Assert.That(result.ArtifactIdentityHash,
                Is.EqualTo(
                    SampleBenchmarkControlledIsolationComparer
                        .CreateArtifactIdentityHash(
                            result.Comparison.ControlledIsolationPairId,
                            result.CachedReportSha256,
                            result.ForcedReportSha256)));
            Assert.That(error.ToString(), Is.Empty);
        });
    }

    [Test]
    public void ControlledIsolationCli_RejectsExplicitNullContractWithoutCrash()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport cached = CreateTimingReport(
            directory,
            forced: false,
            pairId: "cached-workload") with
        {
            CaptureContract = null!
        };
        SampleBenchmarkReport forced = CreateTimingReport(
            directory,
            forced: true,
            pairId: "forced-workload");
        string cachedPath = Path.Combine(directory, "cached-null.json");
        string forcedPath = Path.Combine(directory, "forced-valid.json");
        File.WriteAllText(cachedPath, JsonSerializer.Serialize(cached));
        File.WriteAllText(forcedPath, JsonSerializer.Serialize(forced));
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkControlledIsolationVerificationCli.TryRun(
            [
                SampleBenchmarkControlledIsolationVerificationCli.VerifyOption,
                cachedPath,
                forcedPath
            ],
            output,
            error,
            out int exitCode);
        SampleBenchmarkControlledIsolationVerificationResult result =
            JsonSerializer.Deserialize<
                SampleBenchmarkControlledIsolationVerificationResult>(
                output.ToString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(result.Passed, Is.False);
            Assert.That(result.Failures,
                Has.Some.Contains("null required contracts"));
            Assert.That(error.ToString(), Is.Empty);
        });
    }

    [Test]
    public void QualityActivationVerifier_RecomputesDirectionalRowsAndSidecar()
    {
        string directory = CreateDirectory();
        SampleBenchmarkQualitySequenceReport report =
            CreateDirectionalQualityReport(directory);
        IReadOnlyList<string> valid =
            SampleBenchmarkQualityActivationEvidenceValidator.Validate(report);
        SampleBenchmarkQualitySequenceReport aggregateTamper = report with
        {
            ActivationEvidence = report.ActivationEvidence with
            {
                DirectionalActiveFrameCount = QualityFrameCount - 1
            }
        };
        IReadOnlyList<string> aggregateFailures =
            SampleBenchmarkQualityActivationEvidenceValidator.Validate(
                aggregateTamper);
        IReadOnlyList<string> routeFailures =
            SampleBenchmarkQualityActivationEvidenceValidator.Validate(
                report with
                {
                    TrajectoryRouteHash = Identity('8')
                });
        SampleBenchmarkActivationFrameState checkpointState =
            report.Checkpoints[1].ActivationFrameState!;
        SampleBenchmarkActivationAnimatorState[] changedAnimators =
            checkpointState.Animators.ToArray();
        changedAnimators[0] = changedAnimators[0] with
        {
            PoseRevision = changedAnimators[0].PoseRevision + 1
        };
        SampleBenchmarkActivationFrameState changedState = checkpointState with
        {
            Animators = Array.AsReadOnly(changedAnimators)
        };
        SampleBenchmarkQualityCheckpointEvidence[] changedCheckpoints =
            report.Checkpoints.ToArray();
        changedCheckpoints[1] = changedCheckpoints[1] with
        {
            ActivationFrameState = changedState
        };
        IReadOnlyList<string> checkpointFailures =
            SampleBenchmarkQualityActivationEvidenceValidator.Validate(
                report with
                {
                    Checkpoints = Array.AsReadOnly(changedCheckpoints)
                });

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.Empty, string.Join("; ", valid));
            Assert.That(aggregateFailures,
                Has.Some.Contains("persisted raw frame evidence"));
            Assert.That(routeFailures,
                Has.Some.Contains("authored route hash changed"));
            Assert.That(checkpointFailures,
                Has.Some.Contains("differs from its sidecar"));
        });
    }

    [Test]
    public void QualityActivationCli_EmitsExactReportAndSidecarIdentity()
    {
        string directory = CreateDirectory();
        SampleBenchmarkQualitySequenceReport report =
            CreateDirectionalQualityReport(directory);
        string reportPath = Path.GetFullPath(
            Path.Combine(directory, "quality.json"));
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report));
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkQualityActivationVerificationCli.TryRun(
            [
                SampleBenchmarkQualityActivationVerificationCli.VerifyOption,
                reportPath
            ],
            output,
            error,
            out int exitCode);
        SampleBenchmarkQualityActivationVerificationResult result =
            JsonSerializer.Deserialize<
                SampleBenchmarkQualityActivationVerificationResult>(
                output.ToString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.Zero, error.ToString());
            Assert.That(result.Passed, Is.True,
                string.Join("; ", result.Failures));
            Assert.That(result.ReportPath, Is.EqualTo(reportPath));
            Assert.That(result.SponzaSceneAnimationSidecarSha256,
                Is.EqualTo(
                    report.SponzaSceneAnimationEvidence.SidecarSha256));
            Assert.That(result.ActivationExecutionSequenceHash,
                Is.EqualTo(
                    report.ActivationEvidence
                        .ActivationExecutionSequenceHash));
            Assert.That(error.ToString(), Is.Empty);
        });
    }

    [Test]
    public void QualityActivationVerifier_RejectsMutatedSidecarBytes()
    {
        string directory = CreateDirectory();
        SampleBenchmarkQualitySequenceReport report =
            CreateDirectionalQualityReport(directory);
        string path = report.SponzaSceneAnimationEvidence.SidecarPath;
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        IReadOnlyList<string> failures =
            SampleBenchmarkQualityActivationEvidenceValidator.Validate(report);

        Assert.That(failures,
            Has.Some.Contains("sidecar admission failed"));
    }

    private static SampleBenchmarkReport CreateTimingReport(
        string directory,
        bool forced,
        string pairId)
    {
        string activation = forced
            ? SampleBenchmarkActivation.DirectionalShadowForcedRefresh
            : SampleBenchmarkActivation.DirectionalShadowMovingCaster;
        string variant = forced
            ? SampleBenchmarkCaptureVariant.DirectionalShadowForcedRefresh
            : SampleBenchmarkCaptureVariant.Baseline;
        SampleBenchmarkSponzaSceneAnimationBuild animation =
            CreateAnimationBuild(
                Path.Combine(directory, forced ? "forced.bin" : "cached.bin"),
                TimingFrameCount);
        RendererDiagnostics diagnostics = CreateDiagnostics(forced);
        SampleBenchmarkActivationEvidence activationEvidence =
            CreateDirectionalActivationEvidence(
                activation,
                variant,
                animation.Frames,
                diagnostics,
                SampleBenchmarkTrajectoryKind.SponzaLow,
                qualitySequence: false);
        SampleBenchmarkTimingStats cpu = Stats(
            "CPU frame",
            forced ? 4.2 : 4.0,
            TimingFrameCount);
        SampleBenchmarkTimingStats gpu = Stats(
            "GPU frame",
            forced ? 3.3 : 3.0,
            TimingFrameCount);
        SampleBenchmarkTimingStats directional = Stats(
            "DirectionalShadowPass",
            forced ? 1.4 : 1.0,
            TimingFrameCount);
        string trajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.SponzaLow,
                SampleBistroQualityCaptureVariant.SunScaleStep);
        string routeHash = SampleBenchmarkTrajectory.CreateRouteHash(
            SampleBenchmarkTrajectoryKind.SponzaLow,
            SampleBistroQualityCaptureVariant.SunScaleStep);
        string controlledSettingsFingerprint =
            SampleRenderSettingsFingerprint
                .CaptureDirectionalIsolationFamily(
                    CreateRoleRenderSettings(forced));
        IReadOnlyList<SampleBenchmarkControlledIsolationFrameEvidence>
            controlledFrames =
                SampleBenchmarkControlledIsolationSequence.CreateFrames(
                    Enumerable.Repeat(diagnostics, TimingFrameCount).ToArray(),
                    controlledSettingsFingerprint);
        string controlledSequenceHash =
            SampleBenchmarkControlledIsolationSequence
                .ValidateAndCreateHash(
                    controlledFrames,
                    TimingFrameCount,
                    SampleBenchmarkTrajectory.SponzaLowName,
                    trajectoryFingerprint,
                    routeHash,
                    activation,
                    controlledSettingsFingerprint);
        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 2_688,
            MeasureFrameCount: TimingFrameCount,
            ReportPath: null,
            DisableVSync: true,
            BudgetProfileOverride: RenderBudgetProfileKind.StressUnlimited)
        {
            CapturePairId = pairId,
            CaptureVariant = variant,
            Activation = activation,
            ActivationFingerprint =
                SampleBenchmarkActivation.CreateFingerprint(activation),
            Trajectory = SampleBenchmarkTrajectoryKind.SponzaLow,
            SponzaFixtureMode = SampleSponzaFixtureMode.AnimationDemo,
            TrajectoryFingerprint = trajectoryFingerprint,
            RequireProductionTiming = true
        };
        SampleBenchmarkCaptureContract contract = new(
            Comparable: true,
            ProductionTiming: true,
            PairId: pairId,
            Variant: variant,
            IdentityHash: Identity('1'),
            Mismatches: Array.Empty<string>())
        {
            FullIdentityHash = Identity('2'),
            LoadedShaders = LoadedShaderTestEvidence.Measurement,
            Trajectory = SampleBenchmarkTrajectory.SponzaLowName,
            TrajectoryFingerprint = trajectoryFingerprint,
            TrajectoryFrameCount = 1,
            TrajectoryRouteHash = routeHash,
            TrajectorySequenceHash = Identity(forced ? '4' : '3'),
            SponzaFixtureMode = SampleSponzaFixtureMode.AnimationDemo,
            Activation = activation,
            ActivationFingerprint =
                SampleBenchmarkActivation.CreateFingerprint(activation),
            ControlledIsolationIdentityHash =
                SampleBenchmarkAnalyzer.CreateControlledIsolationIdentityHash(
                    diagnostics,
                    activation),
            ControlledIsolationSettingsFingerprint =
                controlledSettingsFingerprint,
            ControlledIsolationSequenceHash = controlledSequenceHash,
            ControlledIsolationFrames = controlledFrames,
            SponzaSceneAnimationFingerprint =
                animation.Evidence.Fingerprint,
            SponzaSceneAnimationMode = animation.Evidence.Mode,
            SponzaSceneAnimationConfigurationFingerprint =
                animation.Evidence.ConfigurationFingerprint,
            SponzaSceneAnimationSequenceHash =
                animation.Evidence.SequenceHash,
            SponzaSceneAnimationSidecarSha256 =
                animation.Evidence.SidecarSha256
        };
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            options,
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            WarmupFrameCount: 2_688,
            MeasurementFrameCount: TimingFrameCount,
            FirstMeasurementFrameIndex: 2_688,
            LastMeasurementFrameIndex: 2_927,
            CpuFrameMilliseconds: cpu,
            GpuFrameMilliseconds: gpu,
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: TimingFrameCount,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: [directional],
            CpuStages: [],
            Findings: [],
            BudgetMetrics: [],
            LastDiagnostics: diagnostics)
        {
            ProducerIdentity = CreateProducer(forced),
            CaptureContract = contract,
            ActivationEvidence = activationEvidence,
            SponzaSceneAnimationEvidence = animation.Evidence
        };
    }

    private static SampleBenchmarkQualitySequenceReport
        CreateDirectionalQualityReport(string directory)
    {
        SampleBenchmarkSponzaSceneAnimationBuild animation =
            CreateAnimationBuild(
                Path.Combine(directory, "quality-animation.bin"),
                QualityFrameCount);
        RendererDiagnostics diagnostics = CreateDiagnostics(forced: false);
        SampleBenchmarkActivationEvidence activationEvidence =
            CreateDirectionalActivationEvidence(
                SampleBenchmarkActivation.DirectionalShadowMovingCaster,
                SampleBenchmarkCaptureVariant.Baseline,
                animation.Frames,
                diagnostics,
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                qualitySequence: true);
        IReadOnlyList<int> indices =
            SampleBenchmarkQualityCheckpointCatalog.GetCheckpointIndices(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal);
        PerformanceCaptureRunMetadata run = diagnostics.CaptureRun;
        MaterialGiProducerIdentity producer = CreateProducer(forced: false);
        SampleBenchmarkQualityCheckpointEvidence[] checkpoints = indices
            .Select((routeFrame, ordinal) =>
                new SampleBenchmarkQualityCheckpointEvidence(
                    ordinal,
                    routeFrame,
                    300 + routeFrame,
                    Path.GetFullPath(Path.Combine(
                        directory,
                        $"checkpoint-{routeFrame:D4}.pfm")),
                    new string('a', 64),
                    SampleBenchmarkQualityCheckpointCatalog.RequiredWidth,
                    SampleBenchmarkQualityCheckpointCatalog.RequiredHeight,
                    $"quality-checkpoint-{ordinal}",
                    (ulong)(300 + routeFrame),
                    diagnostics.CaptureCamera,
                    diagnostics.CaptureSceneAssetHash,
                    diagnostics.CaptureSceneStateHash,
                    diagnostics.CaptureSceneContentRevision,
                    producer.SettingsFingerprint,
                    run,
                    producer,
                    SampleBenchmarkHdrDifference.Unavailable(
                        "Synthetic activation-verifier fixture."))
                {
                    ActivationFrameState = animation.Frames[routeFrame]
                })
            .ToArray();
        return new SampleBenchmarkQualitySequenceReport(
            SampleBenchmarkQualitySequenceReport.CurrentKind,
            SampleBenchmarkQualitySequenceReport.CurrentSchema,
            DateTimeOffset.UtcNow,
            SampleBenchmarkQualitySequenceRole.Candidate,
            "quality-directional-candidate",
            "Sponza",
            SamplePerformanceScenario.GiSponzaRightWallStationary.ToString(),
            SampleBenchmarkCaptureVariant.Baseline,
            SampleBenchmarkTrajectory.SponzaHorizontalName,
            SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                SampleBistroQualityCaptureVariant.SunScaleStep),
            SampleBenchmarkTrajectory.CreateRouteHash(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                SampleBistroQualityCaptureVariant.SunScaleStep),
            Identity('7'),
            QualityFrameCount,
            FirstRouteAbsoluteFrameIndex: 300,
            SampleBenchmarkQualityCheckpointCatalog.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal),
            indices,
            Array.AsReadOnly(checkpoints),
            TemporalResiduals: Array.Empty<
                SampleBenchmarkQualityTemporalResult>(),
            Passed: true,
            Failures: Array.Empty<string>())
        {
            BuildConfiguration = run.BuildConfiguration,
            CaptureRun = run,
            ProducerIdentity = producer,
            SponzaFixtureMode = SampleSponzaFixtureMode.AnimationDemo,
            Activation =
                SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            ActivationFingerprint = SampleBenchmarkActivation.CreateFingerprint(
                SampleBenchmarkActivation.DirectionalShadowMovingCaster),
            ActivationEvidence = activationEvidence,
            SponzaSceneAnimationEvidence = animation.Evidence,
            TimingEligible = false,
            ProductionTiming = false,
            WarmupFrameCount = 300,
            MaximumAdditionalSettlingFrameCount =
                SampleBenchmarkOptions
                    .ProductionMinimumAdditionalSettlingFrameCount,
            MaximumReadbackDrainFrameCount = 240
        };
    }

    private static SampleBenchmarkActivationEvidence
        CreateDirectionalActivationEvidence(
            string activation,
            string variant,
            IReadOnlyList<SampleBenchmarkActivationFrameState> animationFrames,
            RendererDiagnostics diagnostics,
            SampleBenchmarkTrajectoryKind trajectory,
            bool qualitySequence)
    {
        SampleBenchmarkActivationExecutionFrameEvidence baseline =
            SampleBenchmarkActivationExecutionFrameEvidence.Create(
                -1,
                diagnostics);
        SampleBenchmarkActivationExecutionFrameEvidence[] frames =
            Enumerable.Range(0, animationFrames.Count)
                .Select(index =>
                    SampleBenchmarkActivationExecutionFrameEvidence.Create(
                        index,
                        diagnostics))
                .ToArray();
        SampleBenchmarkActivationEvidence evidence =
            SampleBenchmarkActivationEvidenceEvaluator.Evaluate(
                activation,
                variant,
                animationFrames.Count,
                baseline,
                frames,
                new SortedDictionary<int,
                    Njulf.Rendering.Resources
                        .ReflectionProbeRecaptureRequestSummary>(),
                animationFrames,
                trajectory,
                qualitySequence);
        if (!evidence.Passed)
        {
            throw new InvalidOperationException(
                "Synthetic activation evidence is invalid: " +
                string.Join("; ", evidence.Failures));
        }
        return evidence;
    }

    private static SampleBenchmarkSponzaSceneAnimationBuild
        CreateAnimationBuild(string path, int frameCount)
    {
        SampleBenchmarkActivationFrameState[] frames = Enumerable
            .Range(0, frameCount)
            .Select(CreateAnimationFrame)
            .ToArray();
        string configuration = frames[0].ConfigurationFingerprint;
        string sequence =
            SampleBenchmarkSponzaSceneAnimationContract.CreateSequenceHash(
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute,
                frames,
                configuration);
        SampleEvidenceFileContent sidecar =
            SampleBenchmarkSponzaSceneAnimationSidecar.Write(
                Path.GetFullPath(path),
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute,
                frames,
                configuration,
                sequence);
        var evidence = new SampleBenchmarkSponzaSceneAnimationEvidence(
            SampleBenchmarkSponzaSceneAnimationEvidence.CurrentSchema,
            SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
            SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute,
            Passed: true,
            SampleCount: frameCount,
            ConfigurationFingerprint: configuration,
            SequenceHash: sequence,
            SidecarPath: sidecar.Path,
            SidecarSha256: sidecar.Sha256,
            Failures: Array.Empty<string>());
        return new SampleBenchmarkSponzaSceneAnimationBuild(
            evidence,
            Array.AsReadOnly(frames));
    }

    private static SampleBenchmarkActivationFrameState CreateAnimationFrame(
        int routeFrameIndex)
    {
        float time = (routeFrameIndex *
            HelloGame.BenchmarkSimulationDeltaSeconds) % 2f;
        SampleBenchmarkActivationAnimatorState[] animators =
        [
            CreateAnimator(
                SampleBenchmarkSponzaSceneAnimationContract.JointName,
                routeFrameIndex,
                time,
                0f),
            CreateAnimator(
                SampleBenchmarkSponzaSceneAnimationContract.SurfaceName,
                routeFrameIndex,
                time,
                1f)
        ];
        string configuration =
            SampleBenchmarkActivationFrameState
                .CreateConfigurationFingerprint(animators);
        return new SampleBenchmarkActivationFrameState(
            SampleBenchmarkActivationFrameState.CurrentSchema,
            routeFrameIndex,
            configuration,
            SampleBenchmarkActivationFrameState.CreateFrameHash(
                routeFrameIndex,
                configuration,
                animators),
            Array.AsReadOnly(animators));
    }

    private static SampleBenchmarkActivationAnimatorState CreateAnimator(
        string identity,
        int routeFrameIndex,
        float time,
        float offset)
    {
        float[] values =
        [
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            time + offset, 0f, 0f, 1f
        ];
        uint[] bits = values
            .Select(BitConverter.SingleToUInt32Bits)
            .ToArray();
        return new SampleBenchmarkActivationAnimatorState(
            identity,
            "StrutMove",
            2f,
            time,
            (ulong)routeFrameIndex,
            JointCount: 1,
            SkinCount: 1,
            SampleAnimatedCharacter.CreatePoseHash(bits))
        {
            GlobalMatrixComponentBits = Array.AsReadOnly(bits)
        };
    }

    private static RendererDiagnostics CreateDiagnostics(bool forced)
    {
        DirectionalShadowCacheLayerProvenance[] layers = Enumerable
            .Range(0, 4)
            .Select(index => new DirectionalShadowCacheLayerProvenance(
                index,
                Active: 1,
                CacheSignature: 100UL + (ulong)index,
                ResourceGeneration: 2,
                CacheState: forced
                    ? DirectionalShadowCacheLayerState.RefreshRecorded
                    : DirectionalShadowCacheLayerState.Valid,
                CopiedFromCache: 1,
                RefreshedThisFrame: forced ? 1 : 0,
                ExplicitlyCleared: forced ? 1 : 0,
                DynamicWorkAppended: 1,
                FoliageWorkAppended: 0,
                FinalWorkingLayerValid: 1,
                SubmissionSerial: 10))
            .ToArray();
        DirectionalShadowRuntimeDiagnostics runtime =
            DirectionalShadowRuntimeDiagnostics.Empty with
            {
                Enabled = 1,
                StaticCacheActiveMask = 0b1111,
                StaticCacheValidMask = 0b1111,
                StaticCacheRefreshMask = forced ? 0b1111 : 0,
                StaticCacheReuseMask = forced ? 0 : 0b1111,
                CacheLayerProvenance = Array.AsReadOnly(layers)
            };
        return RendererDiagnostics.Empty with
        {
            DirectionalShadowRuntime = runtime,
            DirectionalDynamicShadowMeshletCount = 8,
            DirectionalShadowSkinnedObjectCount = 2,
            PlayingAnimatorCount = 2,
            SkinningDispatchCount = 2,
            SkinnedObjectCount = 2,
            GpuDirectionalShadowMicroseconds = forced ? 1_400 : 1_000,
            CaptureGpuDeviceName = "Test GPU",
            CaptureGpuDriverVersion = "test-driver-1",
            CaptureRenderWidth =
                SampleBenchmarkQualityCheckpointCatalog.RequiredWidth,
            CaptureRenderHeight =
                SampleBenchmarkQualityCheckpointCatalog.RequiredHeight,
            ActiveBudgetProfile = RenderBudgetProfileKind.StressUnlimited,
            CaptureSceneAssetHash = Identity('a'),
            CaptureSceneStateHash = Identity('b'),
            CaptureSceneContentRevision = 9,
            CaptureCamera = new PerformanceCaptureCameraMetadata(
                1f,
                2f,
                3f,
                0.25f,
                -0.1f,
                1.0f,
                0.1f,
                1000f,
                Identity('c'),
                Identity('d'),
                CameraCutSerial: 4),
            CaptureRun = CreateCaptureRun(),
            ResolvedGiSettings = new ResolvedGiSettingsMetadata(
                new string('e', 64),
                "locked test settings",
                Array.Empty<string>())
        };
    }

    private static PerformanceCaptureRunMetadata CreateCaptureRun() => new(
        "Sponza",
        SamplePerformanceScenario.GiSponzaRightWallStationary.ToString(),
        "Release",
        "test-version",
        new string('1', 40),
        Identity('2'),
        SettingsSchemaVersion: 1)
    {
        ExecutableHash = Identity('3'),
        LoadedShaderIdentity = LoadedShaderTestEvidence.Identity,
        DirtyWorktreeState = "clean"
    };

    private static MaterialGiProducerIdentity CreateProducer(bool forced)
    {
        string settings = SampleRenderSettingsFingerprint.Capture(
            CreateRoleRenderSettings(forced))[7..];
        return new MaterialGiProducerIdentity
        {
            BuildCommit = new string('1', 40),
            ShaderFingerprint = new string('2', 64),
            SettingsFingerprint = settings,
            SourceSettingsFingerprints = [settings],
            GpuName = "Test GPU",
            DriverVersion = "test-driver-1",
            QualityTier = RenderBudgetProfileKind.StressUnlimited.ToString()
        };
    }

    private static RenderSettings CreateRoleRenderSettings(bool forced)
    {
        var settings = new RenderSettings();
        SampleBenchmarkCaptureVariant.Apply(
            settings,
            forced
                ? SampleBenchmarkCaptureVariant
                    .DirectionalShadowForcedRefresh
                : SampleBenchmarkCaptureVariant.Baseline);
        return settings;
    }

    private static string RecomputeControlledSequenceHash(
        SampleBenchmarkReport report,
        SampleBenchmarkCaptureContract contract) =>
        SampleBenchmarkControlledIsolationSequence.ValidateAndCreateHash(
            contract.ControlledIsolationFrames,
            report.MeasurementFrameCount,
            contract.Trajectory,
            contract.TrajectoryFingerprint,
            contract.TrajectoryRouteHash,
            contract.Activation,
            contract.ControlledIsolationSettingsFingerprint);

    private static SampleBenchmarkTimingStats Stats(
        string name,
        double p95,
        int count) => new(
        name,
        count,
        AverageMilliseconds: p95 - 0.3,
        MinMilliseconds: p95 - 0.6,
        MaxMilliseconds: p95 + 0.2,
        P95Milliseconds: p95)
    {
        MedianMilliseconds = p95 - 0.4,
        P50Milliseconds = p95 - 0.4,
        P99Milliseconds = p95 + 0.1
    };

    private static string Identity(char value) =>
        "sha256:" + new string(value, 64);

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "controlled-isolation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
