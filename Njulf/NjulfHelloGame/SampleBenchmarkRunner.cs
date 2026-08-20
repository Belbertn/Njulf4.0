using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed class SampleBenchmarkRunner
{
    // A production tail run may consume the complete source interval, one
    // solve epoch, every audit chunk, and an equal scheduling/readback margin
    // after scene/resource startup. Keep the harness fail-closed, but do not
    // terminate at the exact frame that source repair hands off to solving.
    private const int RequiredConsecutiveReadyFrameCount = 30;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly SampleBenchmarkOptions _options;
    private readonly SamplePerformanceScenario _scenario;
    private readonly Action _exit;
    private readonly Func<string> _getSettingsFingerprint;
    private readonly Func<string, bool>? _requestLinearHdrCapture;
    private readonly Func<string, LinearHdrCaptureResult>? _getLinearHdrCaptureResult;
    private readonly SampleBenchmarkAnalyzer _analyzer;
    private readonly SampleTailDdgiRunObserver _tailDdgiObserver = new();
    private int _samplesCaptured;
    private int _firstMeasurementFrame = -1;
    private int _lastMeasurementFrame = -1;
    private int _hdrCaptureWaitFrameCount;
    private string _hdrCandidatePath = string.Empty;
    private bool _waitingForHdrCapture;
    private bool _completed;
    private int _additionalSettlingFrameCount;
    private bool _settlingWaitTimedOut;
    private RendererDiagnostics? _lastPreMeasurementDiagnostics;
    private int _consecutiveReadyFrameCount;
    private string? _measurementSettingsFingerprint;

    public SampleBenchmarkRunner(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        Action exit,
        Func<string> getSettingsFingerprint,
        Func<string, bool>? requestLinearHdrCapture = null,
        Func<string, LinearHdrCaptureResult>? getLinearHdrCaptureResult = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analyzer = new SampleBenchmarkAnalyzer(
            SampleBenchmarkCaptureVariant.IsTailVariant(
                _options.CaptureVariant));
        _scenario = scenario;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _getSettingsFingerprint = getSettingsFingerprint ??
            throw new ArgumentNullException(nameof(getSettingsFingerprint));
        _requestLinearHdrCapture = requestLinearHdrCapture;
        _getLinearHdrCaptureResult = getLinearHdrCaptureResult;
    }

    public SampleBenchmarkReport? Report { get; private set; }
    public string? ReportPath { get; private set; }
    /// <summary>
    /// The timed interval has ended and the renderer must retain the last
    /// deterministic trajectory pose while the out-of-band HDR readback runs.
    /// </summary>
    public bool HoldTrajectoryForPostMeasurementEvidence =>
        _waitingForHdrCapture;

    public void OnFrameRendered(int frameIndex, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (!_options.Enabled || _completed)
            return;
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        _tailDdgiObserver.Observe(diagnostics);

        if (_waitingForHdrCapture)
        {
            PollHdrCapture();
            return;
        }

        if (_samplesCaptured == 0)
        {
            _consecutiveReadyFrameCount = IsReadyForMeasurement(diagnostics)
                ? Math.Min(
                    RequiredConsecutiveReadyFrameCount,
                    _consecutiveReadyFrameCount + 1)
                : 0;

            if (frameIndex < _options.WarmupFrameCount)
            {
                _lastPreMeasurementDiagnostics = diagnostics;
                return;
            }

            if (_consecutiveReadyFrameCount < RequiredConsecutiveReadyFrameCount)
            {
                if (_additionalSettlingFrameCount <
                    _options.MaximumAdditionalSettlingFrameCount)
                {
                    _additionalSettlingFrameCount++;
                    _lastPreMeasurementDiagnostics = diagnostics;
                    return;
                }

                _settlingWaitTimedOut = true;
            }
            else if (!SampleBenchmarkTrajectory.IsMeasurementStartFrame(
                         _options.Trajectory,
                         frameIndex))
            {
                if (_additionalSettlingFrameCount <
                    _options.MaximumAdditionalSettlingFrameCount)
                {
                    _additionalSettlingFrameCount++;
                    _lastPreMeasurementDiagnostics = diagnostics;
                    return;
                }

                _settlingWaitTimedOut = true;
            }
        }

        if (_samplesCaptured == 0)
        {
            _firstMeasurementFrame = frameIndex;
            _analyzer.SetMeasurementBaseline(
                _lastPreMeasurementDiagnostics ?? diagnostics);
        }
        _lastMeasurementFrame = frameIndex;
        _analyzer.AddSample(diagnostics, budget);
        _samplesCaptured++;

        if (_samplesCaptured < _options.MeasureFrameCount)
            return;

        // Freeze producer identity at the exact end of the measurement window.
        // Post-measurement HDR capture intentionally enables debug/screenshot
        // permissions and must not make an otherwise identical timing run look
        // like it used different render settings.
        _measurementSettingsFingerprint = _getSettingsFingerprint();
        BeginPostMeasurementEvidence();
    }

    private void BeginPostMeasurementEvidence()
    {
        if (string.IsNullOrWhiteSpace(_options.HdrReferencePath) &&
            string.IsNullOrWhiteSpace(_options.HdrCandidatePath))
        {
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                "No benchmark HDR reference path was supplied."));
            return;
        }

        if (_requestLinearHdrCapture == null || _getLinearHdrCaptureResult == null)
        {
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                "The benchmark host does not expose linear HDR capture callbacks."));
            return;
        }

        _hdrCandidatePath = ResolveHdrCandidatePath(_options);
        try
        {
            if (!_requestLinearHdrCapture(_hdrCandidatePath))
            {
                Complete(SampleBenchmarkHdrDifference.Unavailable(
                    "The renderer rejected the post-measurement linear HDR capture request."));
                return;
            }
        }
        catch (Exception exception)
        {
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                $"HDR capture request failed: {exception.GetType().Name}: {exception.Message}"));
            return;
        }

        _waitingForHdrCapture = true;
        _hdrCaptureWaitFrameCount = 0;
    }

    private void PollHdrCapture()
    {
        const int maximumWaitFrames = 120;
        _hdrCaptureWaitFrameCount++;
        LinearHdrCaptureResult result;
        try
        {
            result = _getLinearHdrCaptureResult!(_hdrCandidatePath);
        }
        catch (Exception exception)
        {
            _waitingForHdrCapture = false;
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                $"HDR capture status failed: {exception.GetType().Name}: {exception.Message}"));
            return;
        }

        if (result.State == LinearHdrCaptureState.Completed)
        {
            _waitingForHdrCapture = false;
            if (string.IsNullOrWhiteSpace(_options.HdrReferencePath))
            {
                Complete(SampleBenchmarkHdrDifference.Unavailable(
                    $"HDR candidate captured at '{_hdrCandidatePath}'; no reference was supplied."));
                return;
            }

            try
            {
                Complete(SampleBenchmarkHdrComparer.Compare(
                    _options.HdrReferencePath,
                    _hdrCandidatePath,
                    _options.HdrMaximumRelativeRmse,
                    _options.HdrMaximumFlipP95,
                    _options.HdrQualityContractPath));
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    IOException or
                    InvalidDataException or
                    UnauthorizedAccessException)
            {
                Complete(SampleBenchmarkHdrDifference.Unavailable(
                    $"HDR comparison failed: {exception.GetType().Name}: {exception.Message}"));
            }
            return;
        }

        if (result.State == LinearHdrCaptureState.Failed)
        {
            _waitingForHdrCapture = false;
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "The renderer failed the linear HDR capture."
                    : result.Error));
            return;
        }

        if (_hdrCaptureWaitFrameCount >= maximumWaitFrames)
        {
            _waitingForHdrCapture = false;
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                $"Linear HDR capture did not complete within {maximumWaitFrames} frames."));
        }
    }

    private void Complete(SampleBenchmarkHdrDifference hdrDifference)
    {
        _completed = true;
        Report = _analyzer.CreateReport(
            _options,
            _scenario,
            _options.WarmupFrameCount,
            _samplesCaptured,
            _firstMeasurementFrame,
            _lastMeasurementFrame,
            _tailDdgiObserver.Snapshot());
        Report = Report with
        {
            HdrDifference = hdrDifference,
            AdditionalSettlingFrameCount = _additionalSettlingFrameCount,
            SettlingWaitTimedOut = _settlingWaitTimedOut,
            ShaderProfile = SampleShaderProfileEvidenceLoader.Load(
                _options.ShaderProfileArtifactPath,
                Report.LastDiagnostics),
            ProducerIdentity =
                SampleMaterialGiProducerIdentityFactory.Create(
                    Report.LastDiagnostics,
                    _measurementSettingsFingerprint ??
                        throw new InvalidOperationException(
                            "Benchmark completion requires the measurement-window settings fingerprint."),
                    ResolveQualityTier(
                        Report.LastDiagnostics.ActiveBudgetProfile))
        };
        Report = Report with
        {
            CaptureContract = ApplyEvidenceContract(
                ApplySettlingWaitContract(
                    Report.CaptureContract,
                    _settlingWaitTimedOut,
                    _options.MaximumAdditionalSettlingFrameCount),
                _options,
                Report.HdrDifference,
                Report.ShaderProfile)
        };
        if (SampleDdgiBenchmarkSuite.RequiredProductionGateScenes.Any(scene => scene.Scenario == _scenario))
        {
            SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(Report);
            Report = Report with { DdgiProductionGate = gate };
        }
        ReportPath = WriteReport(Report, _options.ReportPath);
        Console.WriteLine(
            $"Benchmark report exported: {ReportPath} " +
            $"cpuP95={Report.CpuFrameMilliseconds.P95Milliseconds:F3}ms " +
            $"gpuP95={Report.GpuFrameMilliseconds.P95Milliseconds:F3}ms " +
            $"top='{Report.Findings.FirstOrDefault()?.Subject ?? "none"}'");
        if (Report.DdgiProductionGate != null)
        {
            Console.WriteLine(
                $"DDGI production gate: {(Report.DdgiProductionGate.Passed ? "passed" : "failed")} " +
                $"failures={Report.DdgiProductionGate.Failures.Count}");
        }
        _exit();
    }

    internal static bool IsReadyForMeasurement(RendererDiagnostics diagnostics)
    {
        bool acceptedTailCertificate =
            HasAcceptedCurrentSimpleDdgiTailCertificate(diagnostics);
        if (diagnostics.GpuTimingValid == 0 ||
            diagnostics.CaptureFrame.WarmupState != DdgiRuntimeWarmupState.SteadyState ||
            (diagnostics.CaptureFrame.TransportConvergencePending &&
                !acceptedTailCertificate))
        {
            return false;
        }

        if (diagnostics.SimpleDdgiActive == 0)
            return true;
        if (!diagnostics.SimpleDdgiUploadTiming.CapacityDetails.StableKeyHit)
            return false;

        if (diagnostics.SimpleDdgiTransportV2Active == 0)
            return true;

        return diagnostics.SimpleDdgiTransportTailCertificationEnabled
            ? acceptedTailCertificate
            : HasSourceReadySimpleDdgiTransportPopulation(diagnostics);
    }

    internal static bool HasAcceptedCurrentSimpleDdgiTailCertificate(
        RendererDiagnostics diagnostics)
    {
        if (diagnostics.SimpleDdgiActive == 0 ||
            diagnostics.SimpleDdgiTransportV2Active == 0 ||
            !diagnostics.SimpleDdgiTransportTailCertificationEnabled)
        {
            return false;
        }

        SimpleDdgiTransportConvergenceTelemetry tail =
            diagnostics.SimpleDdgiTransportConvergence;
        return tail.TailCertificateCurrent &&
            tail.TailAuditComplete &&
            tail.TailExpectedParticipantCount > 0u &&
            tail.TailAuditedParticipantCount ==
                tail.TailExpectedParticipantCount &&
            tail.TailExpectedTexelCount > 0u &&
            tail.TailAuditedTexelCount == tail.TailExpectedTexelCount &&
            tail.TailExcludedStaleSourceCount == 0u &&
            tail.TailExcludedInvalidCacheCount == 0u &&
            tail.TailCacheIdentityFailureCount == 0u &&
            tail.TailCacheCardinalityFailureCount == 0u &&
            tail.TailCacheSourceGenerationFailureCount == 0u &&
            tail.TailCacheSourceEpochFailureCount == 0u &&
            tail.TailCachePhysicalGenerationFailureCount == 0u &&
            tail.TailNonFiniteCount == 0u &&
            tail.TailCounterOverflowCount == 0u;
    }

    internal static bool HasSourceReadySimpleDdgiTransportPopulation(
        RendererDiagnostics diagnostics)
    {
        SimpleDdgiTransportConvergenceTelemetry convergence =
            diagnostics.SimpleDdgiTransportConvergence;
        int participants = Math.Max(0, convergence.ParticipatingProbeCount);
        int sourceRepair = Math.Clamp(
            convergence.SourceRepairProbeCount,
            0,
            participants);
        int routineSourceRepair = Math.Clamp(
            convergence.RoutineSourceRepairProbeCount,
            0,
            sourceRepair);
        int converged = Math.Clamp(
            convergence.ConvergedProbeCount,
            0,
            participants - sourceRepair);
        int routineMaintenance = Math.Clamp(
            convergence.RoutineMaintenancePendingProbeCount,
            0,
            Math.Max(0, participants - sourceRepair - converged));
        int qualified = Math.Min(
            participants,
            converged + routineSourceRepair + routineMaintenance);
        return convergence.ReadbackValid != 0 &&
            participants > 0 &&
            (long)qualified * 100L >=
                (long)participants * 95L;
    }

    private static SampleBenchmarkCaptureContract ApplySettlingWaitContract(
        SampleBenchmarkCaptureContract contract,
        bool timedOut,
        int maximumAdditionalSettlingFrameCount)
    {
        if (!timedOut)
            return contract;

        string[] mismatches = contract.Mismatches
            .Append(
                $"The benchmark did not settle within " +
                $"{maximumAdditionalSettlingFrameCount} additional frames.")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return contract with
        {
            Comparable = false,
            Mismatches = Array.AsReadOnly(mismatches)
        };
    }

    private static SampleBenchmarkCaptureContract ApplyEvidenceContract(
        SampleBenchmarkCaptureContract contract,
        SampleBenchmarkOptions options,
        SampleBenchmarkHdrDifference hdrDifference,
        SampleShaderProfileEvidence shaderProfile)
    {
        var mismatches = new List<string>(contract.Mismatches);
        bool hdrRequested = !string.IsNullOrWhiteSpace(options.HdrReferencePath);
        if ((options.RequireProductionTiming || hdrRequested) && !hdrDifference.Available)
        {
            mismatches.Add("HDR evidence is unavailable: " + hdrDifference.FailureReason);
        }
        else if (hdrRequested && !hdrDifference.Passed)
        {
            mismatches.Add("HDR image comparison failed: " + hdrDifference.FailureReason);
        }

        bool shaderProfileRequested =
            !string.IsNullOrWhiteSpace(options.ShaderProfileArtifactPath);
        if ((options.RequireShaderProfileEvidence || shaderProfileRequested) &&
            !shaderProfile.Available)
        {
            mismatches.Add(
                "Nsight shader-profile evidence is unavailable: " +
                shaderProfile.UnavailableReason);
        }

        string[] distinct = mismatches.Distinct(StringComparer.Ordinal).ToArray();
        return contract with
        {
            Comparable = contract.Comparable && distinct.Length == 0,
            Mismatches = Array.AsReadOnly(distinct)
        };
    }

    private static string ResolveHdrCandidatePath(SampleBenchmarkOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.HdrCandidatePath))
            return Path.GetFullPath(options.HdrCandidatePath);

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            string reportPath = Path.GetFullPath(options.ReportPath);
            string directory = Path.GetDirectoryName(reportPath) ?? AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(reportPath);
            return Path.Combine(directory, name + ".hdr.pfm");
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "BenchmarkReports",
            $"benchmark-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.hdr.pfm");
    }

    private static string ResolveQualityTier(
        RenderBudgetProfileKind profile) => profile switch
        {
            RenderBudgetProfileKind.LowSpec1080p30 => "Low",
            RenderBudgetProfileKind.MidSpec1080p60 => "Medium",
            RenderBudgetProfileKind.HighSpec1440p60 => "High",
            RenderBudgetProfileKind.Ultra4k60 => "Ultra",
            _ => profile.ToString()
        };

    internal static string WriteReport(SampleBenchmarkReport report, string? path)
    {
        string targetPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppContext.BaseDirectory, "BenchmarkReports", $"benchmark-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json")
            : Path.GetFullPath(path);
        byte[] payload =
            JsonSerializer.SerializeToUtf8Bytes(report, SerializerOptions);
        return SampleEvidenceFileIo.WriteAtomic(
            targetPath,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark report").Path;
    }
}

public sealed class SampleBenchmarkAnalyzer
{
    private readonly bool _tailDdgiTimingProjection;
    private static readonly IReadOnlyList<TimingSelector> GpuTimings =
    [
        new("DepthPrePass", d => d.GpuDepthPrePassMicroseconds),
        new("MotionVectorPass", d => d.GpuMotionVectorMicroseconds),
        new("DirectionalShadowPass", d => d.GpuDirectionalShadowMicroseconds),
        new("SpotShadowPass", d => d.GpuSpotShadowMicroseconds),
        new("PointShadowPass", d => d.GpuPointShadowMicroseconds),
        new("HiZBuildPass", d => d.GpuHiZBuildMicroseconds),
        new("AmbientOcclusionPass", d => d.GpuAmbientOcclusionMicroseconds),
        new("AmbientOcclusionBlurPass", d => d.GpuAmbientOcclusionBlurMicroseconds),
        new("AccelerationStructureBlasPass", d => d.GpuAccelerationStructureBlasMicroseconds),
        new("AccelerationStructureTlasPass", d => d.GpuAccelerationStructureTlasMicroseconds),
        new("SimpleDdgiPageDemandPass", d => d.GpuSimpleDdgiPageDemandMicroseconds),
        new("SimpleDdgiPageResidencyPass", d => d.GpuSimpleDdgiPageResidencyMicroseconds),
        new("SimpleDdgiPageFeedbackPass", d => d.GpuSimpleDdgiPageFeedbackMicroseconds),
        new("SimpleDdgiSchedulePass", d => d.GpuSimpleDdgiScheduleMicroseconds),
        new("SimpleDdgiSchedule.Reset", d => d.GpuSimpleDdgiScheduleResetMicroseconds),
        new("SimpleDdgiSchedule.Classify", d => d.GpuSimpleDdgiScheduleClassifyMicroseconds),
        new("SimpleDdgiSchedule.Prefix", d => d.GpuSimpleDdgiSchedulePrefixMicroseconds),
        new("SimpleDdgiSchedule.LaneBase", d => d.GpuSimpleDdgiScheduleLaneBaseMicroseconds),
        new("SimpleDdgiSchedule.Compact", d => d.GpuSimpleDdgiScheduleCompactMicroseconds),
        new("SimpleDdgiSchedule.TailAdmit", d => d.GpuSimpleDdgiScheduleTailAdmitMicroseconds),
        new("SimpleDdgiSchedule.Admit", d => d.GpuSimpleDdgiScheduleAdmitMicroseconds),
        new("SimpleDdgiSchedule.Materialize", d => d.GpuSimpleDdgiScheduleMaterializeMicroseconds),
        new("SimpleDdgiSchedule.Emit", d => d.GpuSimpleDdgiScheduleEmitMicroseconds),
        new("SimpleDdgiTracePass", d => d.GpuSimpleDdgiTraceMicroseconds),
        new("SimpleDdgiAcceleratedSolvePass", d => d.GpuSimpleDdgiAcceleratedSolveMicroseconds),
        new("SimpleDdgiTransportPass", d => d.GpuSimpleDdgiTransportMicroseconds),
        new("SimpleDdgiDirectionalRadiancePass", d => d.GpuSimpleDdgiDirectionalRadianceMicroseconds),
        new("SimpleDdgiBlendPass", d => d.GpuSimpleDdgiBlendMicroseconds),
        new("SimpleDdgiRelocateClassifyPass", d => d.GpuSimpleDdgiRelocateClassifyMicroseconds),
        new("SimpleDdgiPublishPass", d => d.GpuSimpleDdgiPublishMicroseconds),
        new("SimpleDdgiTransportAuditPass", d => d.GpuSimpleDdgiTransportAuditMicroseconds),
        new("SimpleDdgiSchedulerCommitPass", d => d.GpuSimpleDdgiCommitMicroseconds),
        new("GlobalIlluminationCompositePass", d => d.GpuGiCompositeMicroseconds),
        new("TiledLightCullingPass", d => d.GpuLightCullMicroseconds),
        new("ForwardPlusPass", d => d.GpuForwardOpaqueMicroseconds),
        new("ForwardGiGatherPass", d => d.GpuForwardGiGatherMicroseconds),
        new("SimpleDdgiReceiverCachePass", d => d.GpuSimpleDdgiReceiverCacheMicroseconds),
        new("TransparentPasses", d => d.GpuTransparentMicroseconds),
        new("ParticlePasses", d => d.GpuParticleMicroseconds),
        new("TrailBeamPass", d => d.GpuTrailBeamMicroseconds),
        new("FogPass", d => d.GpuFogMicroseconds),
        new("AutoExposurePass", d => d.GpuAutoExposureMicroseconds),
        new("AntiAliasingPass", d => d.GpuAntiAliasingMicroseconds),
        new("BloomExtractPass", d => d.GpuBloomExtractMicroseconds),
        new("BloomDownsamplePass", d => d.GpuBloomDownsampleMicroseconds),
        new("BloomUpsamplePass", d => d.GpuBloomUpsampleMicroseconds),
        new("ToneMapCompositePass", d => d.GpuCompositeMicroseconds),
        new("SkinningPass", d => d.GpuSkinningMicroseconds),
        new("ReflectionProbeCapture", d => d.GpuReflectionProbeCaptureMicroseconds),
        new("ReflectionProbePrefilter", d => d.GpuReflectionProbePrefilterMicroseconds),
        new("FoliageCullPass", d => d.GpuFoliageCullMicroseconds),
        new("FoliageDepth", d => d.GpuFoliageDepthMicroseconds),
        new("FoliageForward", d => d.GpuFoliageForwardMicroseconds),
        new("FoliageShadow", d => d.GpuFoliageShadowMicroseconds),
        new("DebugDrawPass", d => d.GpuDebugDrawMicroseconds),
        new("DebugOverlay", d => d.GpuDebugOverlayMicroseconds)
    ];

    private static readonly IReadOnlyList<TimingSelector> GpuIndependentTimings =
        GpuTimings
            // Foliage shadow telemetry aliases the directional-shadow pass.
            // Scheduler stage timestamps are nested inside SchedulePass. They
            // remain first-class attribution rows, but summing both parent and
            // children makes a valid frame appear over-accounted by exactly the
            // scheduler duration and invalidates otherwise locked captures.
            .Where(static selector =>
                selector.Name != "FoliageShadow" &&
                selector.Name != "ForwardGiGatherPass" &&
                selector.Name != "SimpleDdgiReceiverCachePass" &&
                !selector.Name.StartsWith(
                    "SimpleDdgiSchedule.",
                    StringComparison.Ordinal))
            .ToArray();

    private static readonly IReadOnlyList<TimingSelector> CpuTimings =
    [
        new("DrawSceneTotal", d => d.CpuTotalDrawSceneMicroseconds),
        new("SceneBuild", d => d.CpuSceneBuildMicroseconds),
        new("ObjectCull", d => d.CpuObjectCullMicroseconds),
        new("MeshletCull", d => d.CpuMeshletCullMicroseconds),
        new("Upload", d => d.CpuUploadMicroseconds),
        new("MaterialUpload", d => d.CpuMaterialUploadMicroseconds),
        new("SimpleDdgiUpload", d => d.SimpleDdgiUploadTiming.TotalMicroseconds),
        new("SimpleDdgiUpload.Layout", d => d.SimpleDdgiUploadTiming.LayoutMicroseconds),
        new("SimpleDdgiUpload.Readback", d => d.SimpleDdgiUploadTiming.ReadbackMicroseconds),
        new("SimpleDdgiUpload.Capacity", d => d.SimpleDdgiUploadTiming.CapacityMicroseconds),
        new("SimpleDdgiUpload.Invalidation", d => d.SimpleDdgiUploadTiming.InvalidationMicroseconds),
        new("SimpleDdgiUpload.SchedulerRefresh", d => d.SimpleDdgiUploadTiming.SchedulerRefreshMicroseconds),
        new("SimpleDdgiUpload.Importance", d => d.SimpleDdgiUploadTiming.ImportanceMicroseconds),
        new("SimpleDdgiUpload.QueueBuild", d => d.SimpleDdgiUploadTiming.QueueBuildMicroseconds),
        new("SimpleDdgiUpload.LifecycleTelemetry", d => d.SimpleDdgiUploadTiming.LifecycleTelemetryMicroseconds),
        new("SimpleDdgiUpload.AtlasMaintenance", d => d.SimpleDdgiUploadTiming.AtlasMaintenanceMicroseconds),
        new("SimpleDdgiUpload.BufferUpload", d => d.SimpleDdgiUploadTiming.BufferUploadMicroseconds),
        new("SimpleDdgiUpload.Other", d => d.SimpleDdgiUploadTiming.OtherMicroseconds),
        new("SimpleDdgiCapacity.CpuProbeState", d => d.SimpleDdgiUploadTiming.CapacityDetails.CpuProbeStateMicroseconds),
        new("SimpleDdgiCapacity.PlanCreation", d => d.SimpleDdgiUploadTiming.CapacityDetails.PlanCreationMicroseconds),
        new("SimpleDdgiCapacity.Predicate", d => d.SimpleDdgiUploadTiming.CapacityDetails.PredicateMicroseconds),
        new("SimpleDdgiCapacity.BufferSizeLookup", d => d.SimpleDdgiUploadTiming.CapacityDetails.BufferSizeLookupMicroseconds),
        new("SimpleDdgiCapacity.DeviceIdleWait", d => d.SimpleDdgiUploadTiming.CapacityDetails.DeviceIdleWaitMicroseconds),
        new("SimpleDdgiCapacity.BufferTransition", d => d.SimpleDdgiUploadTiming.CapacityDetails.BufferTransitionMicroseconds),
        new("SimpleDdgiCapacity.ReadbackReconciliation", d => d.SimpleDdgiUploadTiming.CapacityDetails.ReadbackReconciliationMicroseconds),
        new("SimpleDdgiCapacity.SampledAtlasBudget", d => d.SimpleDdgiUploadTiming.CapacityDetails.SampledAtlasBudgetMicroseconds),
        new("SimpleDdgiCapacity.SampledAtlasEnsure", d => d.SimpleDdgiUploadTiming.CapacityDetails.SampledAtlasEnsureMicroseconds),
        new("SimpleDdgiCapacity.DescriptorRegistration", d => d.SimpleDdgiUploadTiming.CapacityDetails.DescriptorRegistrationMicroseconds),
        new("SimpleDdgiCapacity.RetiredResourceDestruction", d => d.SimpleDdgiUploadTiming.CapacityDetails.RetiredResourceDestructionMicroseconds),
        new("DepthPrePassRecord", d => d.CpuDepthPrePassRecordMicroseconds),
        new("HiZBuildRecord", d => d.CpuHiZBuildRecordMicroseconds),
        new("LightCullRecord", d => d.CpuLightCullRecordMicroseconds),
        new("ForwardOpaqueRecord", d => d.CpuForwardOpaqueRecordMicroseconds),
        new("TransparentRecord", d => d.CpuTransparentRecordMicroseconds),
        new("DirectionalShadowRecord", d => d.CpuDirectionalShadowRecordMicroseconds),
        new("SpotShadowRecord", d => d.CpuSpotShadowRecordMicroseconds),
        new("PointShadowRecord", d => d.CpuPointShadowRecordMicroseconds),
        new("AmbientOcclusionRecord", d => d.CpuAmbientOcclusionRecordMicroseconds),
        new("AmbientOcclusionBlurRecord", d => d.CpuAmbientOcclusionBlurRecordMicroseconds),
        new("AccelerationStructureBuild", d => d.CpuAccelerationStructureBuildMicroseconds),
        new("AccelerationStructureBlasBuild", d => d.CpuAccelerationStructureBlasBuildMicroseconds),
        new("AccelerationStructureBlasCompaction", d => d.CpuAccelerationStructureBlasCompactionMicroseconds),
        new("AccelerationStructureTlasBuild", d => d.CpuAccelerationStructureTlasBuildMicroseconds),
        new("AccelerationStructureInstanceUpload", d => d.CpuAccelerationStructureInstanceUploadMicroseconds),
        new("BloomExtractRecord", d => d.CpuBloomExtractRecordMicroseconds),
        new("BloomDownsampleRecord", d => d.CpuBloomDownsampleRecordMicroseconds),
        new("BloomUpsampleRecord", d => d.CpuBloomUpsampleRecordMicroseconds),
        new("FogRecord", d => d.CpuFogRecordMicroseconds),
        new("CompositeRecord", d => d.CpuCompositeRecordMicroseconds),
        new("AutoExposureRecord", d => d.CpuAutoExposureRecordMicroseconds),
        new("FxaaRecord", d => d.CpuFxaaRecordMicroseconds),
        new("SmaaEdgeRecord", d => d.CpuSmaaEdgeRecordMicroseconds),
        new("SmaaBlendRecord", d => d.CpuSmaaBlendRecordMicroseconds),
        new("SmaaNeighborhoodRecord", d => d.CpuSmaaNeighborhoodRecordMicroseconds),
        new("ReflectionProbeCaptureRecord", d => d.CpuReflectionProbeCaptureRecordMicroseconds),
        new("ReflectionProbePrefilterRecord", d => d.CpuReflectionProbePrefilterRecordMicroseconds),
        new("SkinningRecord", d => d.CpuSkinningRecordMicroseconds),
        new("ParticleRecord", d => d.CpuParticleRecordMicroseconds),
        new("ParticleSimulation", d => d.CpuParticleSimulationMicroseconds),
        new("ParticleBuild", d => d.CpuParticleBuildMicroseconds),
        new("FoliageBuild", d => d.CpuFoliageBuildMicroseconds),
        new("FoliageUpload", d => d.CpuFoliageUploadMicroseconds),
        new("PrimaryCommandRecord", d => d.CpuPrimaryCommandRecordMicroseconds),
        new("SecondaryCommandRecord", d => d.CpuSecondaryCommandRecordMicroseconds),
        new("AcquireImage", d => d.CpuAcquireImageMicroseconds),
        new("QueueSubmit", d => d.CpuQueueSubmitMicroseconds),
        new("Present", d => d.CpuPresentMicroseconds),
        new("WaitForFrameFence", d => d.CpuWaitForFrameFenceMicroseconds),
        new("RuntimeStall", d => d.RuntimeStallMicrosecondsThisFrame)
    ];

    private readonly List<RendererDiagnostics> _samples = new();
    private readonly Dictionary<string, BudgetMetric> _worstBudgetMetrics =
        new(StringComparer.Ordinal);
    private RendererDiagnostics? _measurementBaseline;

    public SampleBenchmarkAnalyzer(bool tailDdgiTimingProjection = false)
    {
        _tailDdgiTimingProjection = tailDdgiTimingProjection;
    }

    internal void SetMeasurementBaseline(RendererDiagnostics diagnostics)
    {
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (_samples.Count != 0)
            throw new InvalidOperationException(
                "The measurement baseline must be set before the first sample.");

        _measurementBaseline = diagnostics;
    }

    public void AddSample(RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        _samples.Add(diagnostics);
        RenderBudgetSnapshot measuredBudget = budget;
        if (_tailDdgiTimingProjection)
        {
            measuredBudget = SampleTailDdgiLongSoakProfile.ProjectBudget(
                budget,
                diagnostics,
                materialStressMetricsNotApplicable: false).Budget;
        }
        AccumulateWorstBudgetMetrics(measuredBudget.Metrics);
    }

    public SampleBenchmarkReport CreateReport(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        int warmupFrameCount,
        int measurementFrameCount,
        int firstMeasurementFrameIndex,
        int lastMeasurementFrameIndex,
        SampleTailDdgiRunObservation? tailObservation = null)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        RendererDiagnostics last = _samples.Count == 0 ? RendererDiagnostics.Empty : _samples[^1];
        IReadOnlyList<SampleBenchmarkTimingStats> gpuPasses = BuildTimingStats(GpuTimings, requireGpuTiming: true);
        IReadOnlyList<SampleBenchmarkTimingStats> cpuStages = BuildTimingStats(CpuTimings, requireGpuTiming: false);
        SampleBenchmarkTimingStats cpuFrame = BuildStats("CPU frame", _samples.Select(d => MicrosecondsToMilliseconds(d.CpuTotalDrawSceneMicroseconds)));
        SampleBenchmarkTimingStats gpuFrame = BuildStats(
            "GPU frame",
            _samples.Where(d => d.GpuTimingValid != 0).Select(d => MicrosecondsToMilliseconds(d.GpuFrameMicroseconds)));
        int gpuValidSamples = _samples.Count(d => d.GpuTimingValid != 0);
        SampleBenchmarkTimingStats gpuPassSum = BuildStats(
            "GPU independent pass sum",
            _samples
                .Where(static d => d.GpuTimingValid != 0)
                .Select(d => MicrosecondsToMilliseconds(
                    GpuIndependentTimings.Sum(selector =>
                        Math.Max(0L, selector.GetMicroseconds(d))))));
        SampleBenchmarkTimingStats gpuUnexplained = BuildStats(
            "GPU unexplained",
            _samples
                .Where(static d => d.GpuTimingValid != 0)
                .Select(d => MicrosecondsToMilliseconds(
                    d.GpuFrameMicroseconds -
                    GpuIndependentTimings.Sum(selector =>
                        Math.Max(0L, selector.GetMicroseconds(d))))));
        SampleBenchmarkTimingStats simpleDdgiTransportBlend = BuildStats(
            "Simple DDGI transport + blend",
            _samples
                .Where(static d => d.GpuTimingValid != 0)
                .Select(d => MicrosecondsToMilliseconds(
                    Math.Max(0L, d.GpuSimpleDdgiTransportMicroseconds) +
                    Math.Max(0L, d.GpuSimpleDdgiBlendMicroseconds))));
        SampleDdgiSchedulerRefreshEvidence schedulerRefreshEvidence =
            BuildSimpleDdgiSchedulerRefreshEvidence();
        BudgetMetric[] budgetMetrics = _worstBudgetMetrics.Values
            .OrderBy(static metric => metric.Name, StringComparer.Ordinal)
            .ToArray();
        MaterialWindowTiming materialTiming =
            ApplyMeasurementWindowTimingMetrics(
                budgetMetrics,
                cpuFrame,
                gpuFrame);

        return new SampleBenchmarkReport(
            Kind: "njulf-renderer-benchmark",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Options: options,
            Scenario: scenario,
            WarmupFrameCount: warmupFrameCount,
            MeasurementFrameCount: measurementFrameCount,
            FirstMeasurementFrameIndex: firstMeasurementFrameIndex,
            LastMeasurementFrameIndex: lastMeasurementFrameIndex,
            CpuFrameMilliseconds: cpuFrame,
            GpuFrameMilliseconds: gpuFrame,
            GpuTimingSupported: last.GpuTimingSupported,
            GpuTimingValidSampleCount: gpuValidSamples,
            GpuTimingUnavailableReason: last.GpuTimingValid == 0 ? last.GpuTimingUnavailableReason : string.Empty,
            GpuPasses: gpuPasses,
            CpuStages: cpuStages,
            Findings: BuildFindings(cpuFrame, gpuFrame, gpuPasses, cpuStages, budgetMetrics),
            BudgetMetrics: budgetMetrics,
            LastDiagnostics: last)
        {
            AccuracyOracleResults = SampleGiAccuracyOracleEvaluator.Evaluate(scenario, _samples),
            CaptureContract = BuildCaptureContract(options),
            GpuIndependentPassSumMilliseconds = gpuPassSum,
            GpuUnexplainedMilliseconds = gpuUnexplained,
            SimpleDdgiTransportBlendMilliseconds = simpleDdgiTransportBlend,
            SimpleDdgiSchedulerRefresh = schedulerRefreshEvidence,
            TailDdgiEvidence = SampleTailDdgiRuntimeEvidenceBuilder.Create(
                _samples,
                tailObservation ?? SampleTailDdgiRunObservation.Empty,
                options.CaptureVariant),
            MaterialTimingEvidence =
                new SampleBenchmarkMaterialTimingEvidence(
                    materialTiming.Compile,
                    materialTiming.Upload,
                    materialTiming.Pipeline,
                    materialTiming.CompileExact,
                    materialTiming.UploadExact)
        };
    }

    private IReadOnlyList<SampleBenchmarkTimingStats> BuildTimingStats(
        IReadOnlyList<TimingSelector> selectors,
        bool requireGpuTiming)
    {
        return selectors
            .Select(selector =>
            {
                bool simpleDdgiTiming = selector.Name.StartsWith(
                    "SimpleDdgi",
                    StringComparison.Ordinal);
                double[] samples = _samples
                    .Where(d => (!requireGpuTiming || d.GpuTimingValid != 0) &&
                        (!simpleDdgiTiming || d.SimpleDdgiActive != 0))
                    .Select(d => MicrosecondsToMilliseconds(
                        selector.GetMicroseconds(d)))
                    .ToArray();
                // A selected pass/stage owns the entire measurement window.
                // Preserve zero-duration timestamp quantization samples so its
                // percentile count remains exactly 120; omit only selectors that
                // were wholly inactive for the scenario.
                return simpleDdgiTiming || samples.Any(static value => value > 0.0)
                    ? BuildStats(selector.Name, samples)
                    : SampleBenchmarkTimingStats.Empty(selector.Name);
            })
            .Where(stats => stats.Count > 0)
            .OrderByDescending(stats => stats.P95Milliseconds)
            .ThenByDescending(stats => stats.AverageMilliseconds)
            .ToArray();
    }

    private static IReadOnlyList<SampleBenchmarkFinding> BuildFindings(
        SampleBenchmarkTimingStats cpuFrame,
        SampleBenchmarkTimingStats gpuFrame,
        IReadOnlyList<SampleBenchmarkTimingStats> gpuPasses,
        IReadOnlyList<SampleBenchmarkTimingStats> cpuStages,
        IReadOnlyList<BudgetMetric> budgetMetrics)
    {
        var findings = new List<SampleBenchmarkFinding>();
        SampleBenchmarkTimingStats? topGpu = gpuPasses.FirstOrDefault();
        SampleBenchmarkTimingStats? topCpu = cpuStages.FirstOrDefault(stage => stage.Name != "DrawSceneTotal");

        if (gpuFrame.Count > 0 && gpuFrame.P95Milliseconds >= cpuFrame.P95Milliseconds && topGpu != null)
        {
            findings.Add(new SampleBenchmarkFinding(
                "likely-bound",
                topGpu.Name,
                $"GPU dominated this sample set; pass p95={topGpu.P95Milliseconds:F3}ms avg={topGpu.AverageMilliseconds:F3}ms."));
        }
        else if (topCpu != null)
        {
            findings.Add(new SampleBenchmarkFinding(
                "likely-bound",
                topCpu.Name,
                $"CPU dominated this sample set; stage p95={topCpu.P95Milliseconds:F3}ms avg={topCpu.AverageMilliseconds:F3}ms."));
        }

        foreach (BudgetMetric metric in budgetMetrics.Where(
                     static metric => metric.Status is
                         RenderBudgetStatus.OverBudget or RenderBudgetStatus.Warning))
        {
            findings.Add(new SampleBenchmarkFinding(
                "budget",
                metric.Name,
                $"{metric.Status}: {metric.Value:F3} {metric.Unit}, budget={metric.FailureThreshold:F3} {metric.Unit}."));
        }

        if (gpuFrame.Count == 0)
        {
            findings.Add(new SampleBenchmarkFinding(
                "gpu-timing",
                "GPU frame",
                "No valid GPU timestamp samples were captured; CPU timings and counters are still reported."));
        }

        return findings;
    }

    private void AccumulateWorstBudgetMetrics(IReadOnlyList<BudgetMetric> metrics)
    {
        foreach (BudgetMetric metric in metrics)
        {
            string key = metric.Name + "\u001f" + metric.Unit;
            if (!_worstBudgetMetrics.TryGetValue(key, out BudgetMetric? current) ||
                IsWorse(metric, current))
            {
                _worstBudgetMetrics[key] = metric;
            }
        }
    }

    private static bool IsWorse(BudgetMetric candidate, BudgetMetric current)
    {
        int candidateRank = GetBudgetStatusRank(candidate.Status);
        int currentRank = GetBudgetStatusRank(current.Status);
        if (candidateRank != currentRank)
            return candidateRank > currentRank;

        return GetBudgetPressure(candidate) > GetBudgetPressure(current);
    }

    private static int GetBudgetStatusRank(RenderBudgetStatus status)
    {
        if (!Enum.IsDefined(status))
            return 7;

        // Availability is a coverage contract, not a benign low-pressure sample.
        // Retain it when any measurement frame loses a metric so the release gate
        // can fail closed for metrics required by the measured scenario.
        return status switch
        {
            RenderBudgetStatus.Unknown => 6,
            RenderBudgetStatus.Unavailable => 5,
            RenderBudgetStatus.OverBudget => 4,
            RenderBudgetStatus.Warning => 3,
            RenderBudgetStatus.WithinBudget => 2,
            _ => 0
        };
    }

    private static double GetBudgetPressure(BudgetMetric metric)
    {
        if (double.IsFinite(metric.FailureThreshold) && metric.FailureThreshold > 0.0)
            return metric.Value / metric.FailureThreshold;
        return metric.Value;
    }

    private MaterialWindowTiming ApplyMeasurementWindowTimingMetrics(
        BudgetMetric[] metrics,
        SampleBenchmarkTimingStats cpuFrame,
        SampleBenchmarkTimingStats gpuFrame)
    {
        ReplaceTimingMetric(metrics, "CPU renderer", cpuFrame);
        ReplaceTimingMetric(
            metrics,
            "GPU frame",
            gpuFrame,
            gpuFrame.Count == _samples.Count && _samples.Count > 0);

        RendererDiagnostics[] giSamples = _samples
            .Where(static sample => sample.GlobalIlluminationEnabled != 0)
            .ToArray();
        SampleBenchmarkTimingStats giCpu = BuildStats(
            "GI CPU scheduling and upload",
            giSamples.Select(static sample =>
                MicrosecondsToMilliseconds(
                    sample.CpuGlobalIlluminationRecordMicroseconds)));
        bool giCpuAvailable = giSamples.Length > 0 &&
            giSamples.All(static sample =>
                sample.GlobalIlluminationCpuTimingSampleCount > 0);
        ReplaceTimingMetric(
            metrics,
            "GI CPU scheduling and upload",
            giCpu,
            giCpuAvailable);

        SampleBenchmarkTimingStats giGpu = BuildStats(
            "GI GPU",
            giSamples.Select(sample =>
                _tailDdgiTimingProjection
                    ? ResolveTailDdgiGpuMilliseconds(sample)
                    : ResolveGlobalIlluminationGpuMilliseconds(sample)));
        bool giGpuAvailable = giSamples.Length > 0 &&
            giGpu.Count == giSamples.Length;
        ReplaceTimingMetric(metrics, "GI GPU", giGpu, giGpuAvailable);

        SampleBenchmarkTimingStats giForwardIncremental = BuildStats(
            "GI forward gather incremental",
            giSamples.Select(static sample =>
                HasForwardGiIncrementalTiming(sample)
                    ? MicrosecondsToMilliseconds(
                        sample.GpuForwardGiIncrementalMicroseconds)
                    : double.NaN));
        bool forwardRequired = giSamples.Any(static sample =>
            sample.GlobalIlluminationDdgiActive != 0 ||
            sample.SimpleDdgiActive != 0);
        bool giForwardIncrementalAvailable = forwardRequired &&
            giForwardIncremental.Count == giSamples.Length;
        ReplaceTimingMetric(
            metrics,
            "GI forward gather incremental",
            giForwardIncremental,
            giForwardIncrementalAvailable);

        MaterialWindowTiming materialTiming = BuildMaterialWindowTiming();
        ReplaceTimingMetric(
            metrics,
            RenderBudgetEvaluator.MaterialGiCompileP95MetricName,
            materialTiming.Compile,
            materialTiming.CompileExact && materialTiming.Compile.Count > 0);
        ReplaceTimingMetric(
            metrics,
            RenderBudgetEvaluator.MaterialGiUploadP95MetricName,
            materialTiming.Upload,
            materialTiming.UploadExact && materialTiming.Upload.Count > 0);
        ReplaceTimingMetric(
            metrics,
            RenderBudgetEvaluator.MaterialGiPipelineP95MetricName,
            materialTiming.Pipeline,
            materialTiming.CompileExact &&
                materialTiming.UploadExact &&
                materialTiming.Pipeline.Count > 0);
        return materialTiming;
    }

    private MaterialWindowTiming BuildMaterialWindowTiming()
    {
        var compile = new List<double>();
        var upload = new List<double>();
        var pipeline = new List<double>();
        RendererDiagnostics baseline = _measurementBaseline ??
            (_samples.Count > 0 ? _samples[0] : RendererDiagnostics.Empty);
        int previousCompileCount = baseline.MaterialCompileTimingSampleCount;
        int previousUploadCount = baseline.MaterialUploadTimingSampleCount;
        bool compileExact = true;
        bool uploadExact = true;

        foreach (RendererDiagnostics sample in _samples)
        {
            int compileDelta = sample.MaterialCompileTimingSampleCount -
                previousCompileCount;
            int uploadDelta = sample.MaterialUploadTimingSampleCount -
                previousUploadCount;
            if (compileDelta is < 0 or > 1)
                compileExact = false;
            if (uploadDelta is < 0 or > 1)
                uploadExact = false;

            double compileMilliseconds = 0.0;
            double uploadMilliseconds = 0.0;
            if (compileDelta == 1)
            {
                compileMilliseconds = MicrosecondsToMilliseconds(
                    sample.MaterialLastCompileMicroseconds);
                compile.Add(compileMilliseconds);
            }
            if (uploadDelta == 1)
            {
                uploadMilliseconds = MicrosecondsToMilliseconds(
                    sample.MaterialLastUploadMicroseconds);
                upload.Add(uploadMilliseconds);
            }
            if (compileDelta == 1 || uploadDelta == 1)
                pipeline.Add(compileMilliseconds + uploadMilliseconds);

            previousCompileCount = sample.MaterialCompileTimingSampleCount;
            previousUploadCount = sample.MaterialUploadTimingSampleCount;
        }

        return new MaterialWindowTiming(
            BuildStats("Material GI compile P95", compile),
            BuildStats("Material GI upload P95", upload),
            BuildStats("Material GI compile/upload P95", pipeline),
            compileExact,
            uploadExact);
    }

    private static double ResolveGlobalIlluminationGpuMilliseconds(
        RendererDiagnostics diagnostics)
    {
        bool forwardRequired = diagnostics.GlobalIlluminationDdgiActive != 0 ||
            diagnostics.SimpleDdgiActive != 0;
        bool hasForwardTiming = HasForwardGiIncrementalTiming(diagnostics);
        if (diagnostics.GpuTimingValid == 0 ||
            (forwardRequired && !hasForwardTiming))
        {
            return double.NaN;
        }

        long microseconds = diagnostics.GpuDdgiUpdateMicroseconds +
            diagnostics.GpuGiCompositeMicroseconds +
            diagnostics.GpuFarFieldUpdateMicroseconds +
            diagnostics.GpuAccelerationStructureBlasMicroseconds +
            diagnostics.GpuAccelerationStructureTlasMicroseconds +
            (hasForwardTiming
                ? diagnostics.GpuForwardGiIncrementalMicroseconds
                : 0);
        return MicrosecondsToMilliseconds(microseconds);
    }

    private static double ResolveTailDdgiGpuMilliseconds(
        RendererDiagnostics diagnostics) =>
        diagnostics.GpuTimingValid != 0 &&
        diagnostics.SimpleDdgiActive != 0 &&
        diagnostics.SimpleDdgiTransportV2Active != 0 &&
        diagnostics.SimpleDdgiTransportTailCertificationEnabled
            ? MicrosecondsToMilliseconds(
                diagnostics.GpuDdgiUpdateMicroseconds)
            : double.NaN;

    private static bool HasForwardGiIncrementalTiming(
        RendererDiagnostics diagnostics) =>
        diagnostics.GpuForwardGiIncrementalAttribution is
            GiTimingAttribution.Exclusive or GiTimingAttribution.PairedEstimate;

    private static void ReplaceTimingMetric(
        BudgetMetric[] metrics,
        string name,
        SampleBenchmarkTimingStats stats,
        bool available = true)
    {
        int index = Array.FindIndex(
            metrics,
            metric => string.Equals(metric.Name, name, StringComparison.Ordinal));
        if (index < 0)
            return;

        BudgetMetric template = metrics[index];
        // Coverage loss in any measured frame is itself evidence. Exact P95
        // replacement must not turn an Unknown/Unavailable timing stream into
        // an apparently valid one merely because other frames had timestamps.
        if (template.Status is RenderBudgetStatus.Unknown or
            RenderBudgetStatus.Unavailable)
        {
            return;
        }
        double value = available && stats.Count > 0
            ? stats.P95Milliseconds
            : 0.0;
        metrics[index] = template with
        {
            Value = value,
            Status = available && stats.Count > 0
                ? RenderBudgetEvaluator.Classify(value, template.FailureThreshold)
                : RenderBudgetStatus.Unavailable
        };
    }

    private sealed record MaterialWindowTiming(
        SampleBenchmarkTimingStats Compile,
        SampleBenchmarkTimingStats Upload,
        SampleBenchmarkTimingStats Pipeline,
        bool CompileExact,
        bool UploadExact);

    internal static SampleBenchmarkTimingStats BuildStats(string name, IEnumerable<double> values)
    {
        double[] samples = values.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        if (samples.Length == 0)
            return new SampleBenchmarkTimingStats(name, 0, 0, 0, 0, 0);

        Array.Sort(samples);
        double sum = samples.Sum();
        int p95Index = PercentileIndex(samples.Length, 0.95);
        int p99Index = PercentileIndex(samples.Length, 0.99);
        double median = samples.Length % 2 == 0
            ? (samples[samples.Length / 2 - 1] + samples[samples.Length / 2]) * 0.5
            : samples[samples.Length / 2];
        return new SampleBenchmarkTimingStats(
            name,
            samples.Length,
            sum / samples.Length,
            samples[0],
            samples[^1],
            samples[p95Index])
        {
            MedianMilliseconds = median,
            P50Milliseconds = median,
            P99Milliseconds = samples[p99Index]
        };
    }

    private static int PercentileIndex(int sampleCount, double percentile) =>
        Math.Min(sampleCount - 1, (int)Math.Ceiling(sampleCount * percentile) - 1);

    private SampleDdgiSchedulerRefreshEvidence BuildSimpleDdgiSchedulerRefreshEvidence()
    {
        RendererDiagnostics[] samples = _samples
            .Where(static sample => sample.SimpleDdgiActive != 0)
            .ToArray();
        if (samples.Length == 0)
            return SampleDdgiSchedulerRefreshEvidence.Empty;

        SampleDdgiSchedulerSlowFrame[] slowest = samples
            .Select((sample, index) => new SampleDdgiSchedulerSlowFrame(
                index,
                sample.SimpleDdgiUploadTiming.SchedulerRefreshMicroseconds,
                sample.SimpleDdgiUploadTiming.SchedulerEntryRefreshCount,
                sample.SimpleDdgiUploadTiming.SchedulerWakeEntryRefreshCount,
                sample.SimpleDdgiUploadTiming.SchedulerWakeRefreshBudget,
                sample.SimpleDdgiUploadTiming.SchedulerWakeBudgetSaturated,
                sample.SimpleDdgiUploadTiming.SchedulerFullRebuildCount,
                sample.SimpleDdgiUploadTiming.VisibilityEntryRefreshCount,
                sample.SimpleDdgiUploadTiming.ReadbackProbeCount,
                sample.DdgiProbesUpdated,
                sample.SimpleDdgiTransportSourceReadyProbeCount,
                sample.SimpleDdgiTransportConvergedProbeCount,
                sample.SimpleDdgiTransportGlobalConvergencePending)
            {
                RoutineSourceRepairProbeCount =
                    sample.SimpleDdgiTransportConvergence.RoutineSourceRepairProbeCount,
                RoutineMaintenancePendingProbeCount =
                    sample.SimpleDdgiTransportConvergence.RoutineMaintenancePendingProbeCount
            })
            .OrderByDescending(static sample => sample.SchedulerRefreshMicroseconds)
            .ThenBy(static sample => sample.MeasurementSampleIndex)
            .Take(8)
            .ToArray();

        return new SampleDdgiSchedulerRefreshEvidence(
            BuildIntegerStats(
                "Scheduler entries refreshed",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.SchedulerEntryRefreshCount)),
            BuildIntegerStats(
                "Scheduler wake entries refreshed",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.SchedulerWakeEntryRefreshCount)),
            BuildIntegerStats(
                "Visibility entries refreshed",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.VisibilityEntryRefreshCount)),
            BuildIntegerStats(
                "Probe readback entries",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.ReadbackProbeCount)),
            samples.Count(static sample =>
                sample.SimpleDdgiUploadTiming.SchedulerWakeBudgetSaturated != 0),
            samples.Count(static sample =>
                sample.SimpleDdgiUploadTiming.SchedulerFullRebuildCount != 0),
            slowest);
    }

    private static SampleBenchmarkIntegerStats BuildIntegerStats(
        string name,
        IEnumerable<int> values)
    {
        int[] samples = values.ToArray();
        if (samples.Length == 0)
            return SampleBenchmarkIntegerStats.Empty(name);

        Array.Sort(samples);
        long sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i];
        int p95Index = Math.Min(
            samples.Length - 1,
            (int)Math.Ceiling(samples.Length * 0.95) - 1);
        double median = samples.Length % 2 == 0
            ? (samples[samples.Length / 2 - 1] +
                (double)samples[samples.Length / 2]) * 0.5
            : samples[samples.Length / 2];
        return new SampleBenchmarkIntegerStats(
            name,
            samples.Length,
            sum / (double)samples.Length,
            samples[0],
            samples[^1],
            samples[p95Index],
            median);
    }

    private SampleBenchmarkCaptureContract BuildCaptureContract(
        SampleBenchmarkOptions options)
    {
        if (_samples.Count == 0)
            return SampleBenchmarkCaptureContract.Unavailable;

        RendererDiagnostics first = _samples[0];
        var mismatches = new List<string>();
        bool movingTrajectory =
            SampleBenchmarkTrajectory.IsMoving(options.Trajectory);
        int trajectoryFrameCount =
            SampleBenchmarkTrajectory.GetFrameCount(options.Trajectory);
        string expectedTrajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                options.Trajectory,
                options.TrajectoryBistroVariant);
        if (string.IsNullOrWhiteSpace(options.TrajectoryFingerprint) ||
            !string.Equals(
                options.TrajectoryFingerprint,
                expectedTrajectoryFingerprint,
                StringComparison.Ordinal))
        {
            mismatches.Add(
                "Benchmark trajectory fingerprint is absent or does not match " +
                $"'{SampleBenchmarkTrajectory.GetName(options.Trajectory)}'.");
        }
        if (movingTrajectory && _samples.Count != trajectoryFrameCount)
        {
            mismatches.Add(
                $"Moving trajectory '{SampleBenchmarkTrajectory.GetName(options.Trajectory)}' " +
                $"requires exactly {trajectoryFrameCount} measured frames; captured {_samples.Count}.");
        }
        if (_samples.Count < 120)
            mismatches.Add($"Production timing requires at least 120 frames; captured {_samples.Count}.");
        if (first.GiMeasurement.Mode != GiMeasurementMode.Production)
            mismatches.Add($"Measurement mode is {first.GiMeasurement.Mode}, not Production.");
        if (first.ValidationMode != RendererValidationMode.Off)
            mismatches.Add($"Validation mode is {first.ValidationMode}, not Off.");
        if (first.DdgiDetailedCountersCompiled != 0 ||
            first.DdgiDetailedCountersEnabled != 0 ||
            first.DdgiDetailedCountersRequested != 0 ||
            first.GiMeasurement.DetailedCountersReadbackValid)
        {
            mismatches.Add("Detailed DDGI diagnostics are compiled or enabled in a production timing run.");
        }
        if (!IsProductionBuildConfiguration(first.CaptureRun.BuildConfiguration))
        {
            mismatches.Add(
                $"Build configuration '{first.CaptureRun.BuildConfiguration}' is not a production configuration.");
        }
        if (first.GiMeasurement.Mode == GiMeasurementMode.Production &&
            (first.CaptureRenderWidth != 1920 || first.CaptureRenderHeight != 1080))
        {
            mismatches.Add(
                $"Production timing requires an exact 1920x1080 framebuffer; " +
                $"captured {first.CaptureRenderWidth}x{first.CaptureRenderHeight}.");
        }
        RequireIdentity(mismatches, "GPU", first.CaptureGpuDeviceName);
        RequireIdentity(mismatches, "driver", first.CaptureGpuDriverVersion);
        RequireIdentity(mismatches, "scene asset hash", first.CaptureSceneAssetHash);
        RequireIdentity(mismatches, "scene state hash", first.CaptureSceneStateHash);
        RequireIdentity(mismatches, "camera view hash", first.CaptureCamera.ViewHash);
        RequireIdentity(mismatches, "camera projection hash", first.CaptureCamera.ProjectionHash);
        RequireIdentity(mismatches, "executable hash", first.CaptureRun.ExecutableHash);
        RequireIdentity(mismatches, "commit", first.CaptureRun.Commit);
        RequireIdentity(mismatches, "dirty-worktree state", first.CaptureRun.DirtyWorktreeState);
        RequireIdentity(mismatches, "shader bundle hash", first.CaptureRun.ShaderBundleHash);
        RequireIdentity(mismatches, "resolved GI settings hash", first.ResolvedGiSettings.StableHash);
        if (options.RequireProductionTiming && string.IsNullOrWhiteSpace(options.CapturePairId))
            mismatches.Add("Production timing requires a non-empty paired-capture identity.");

        long passTimestampToleranceMicroseconds = 0;
        int resolvedGiSettingsMismatchFrameCount = 0;
        int resolvedGiSettingsDetailBudget = 8;

        for (int index = 0; index < _samples.Count; index++)
        {
            RendererDiagnostics sample = _samples[index];
            CompareInvariant(mismatches, index, "GPU", first.CaptureGpuDeviceName, sample.CaptureGpuDeviceName);
            CompareInvariant(mismatches, index, "driver", first.CaptureGpuDriverVersion, sample.CaptureGpuDriverVersion);
            CompareInvariant(mismatches, index, "width", first.CaptureRenderWidth, sample.CaptureRenderWidth);
            CompareInvariant(mismatches, index, "height", first.CaptureRenderHeight, sample.CaptureRenderHeight);
            CompareInvariant(mismatches, index, "quality", first.ActiveQualityPreset, sample.ActiveQualityPreset);
            CompareInvariant(mismatches, index, "scene revision", first.CaptureSceneContentRevision, sample.CaptureSceneContentRevision);
            if (movingTrajectory)
            {
                IReadOnlyList<string> cameraMismatches =
                    SampleBenchmarkTrajectory.ValidateCamera(
                        options.Trajectory,
                        index,
                        options.TrajectoryBistroVariant,
                        sample.CaptureCamera);
                foreach (string mismatch in cameraMismatches)
                {
                    mismatches.Add(
                        $"Frame {index} trajectory camera {mismatch}.");
                }
                CompareInvariant(
                    mismatches,
                    index,
                    "camera cut serial",
                    first.CaptureCamera.CameraCutSerial,
                    sample.CaptureCamera.CameraCutSerial);
            }
            else
            {
                CompareInvariant(mismatches, index, "scene hash", first.CaptureSceneStateHash, sample.CaptureSceneStateHash);
                CompareInvariant(mismatches, index, "camera", first.CaptureCamera, sample.CaptureCamera);
            }
            CompareInvariant(mismatches, index, "executable", first.CaptureRun.ExecutableHash, sample.CaptureRun.ExecutableHash);
            CompareInvariant(mismatches, index, "commit", first.CaptureRun.Commit, sample.CaptureRun.Commit);
            CompareInvariant(mismatches, index, "dirty state", first.CaptureRun.DirtyWorktreeState, sample.CaptureRun.DirtyWorktreeState);
            CompareInvariant(mismatches, index, "shader bundle", first.CaptureRun.ShaderBundleHash, sample.CaptureRun.ShaderBundleHash);
            CompareInvariant(mismatches, index, "timestamp period", first.GpuTimestampPeriodNanoseconds, sample.GpuTimestampPeriodNanoseconds);
            if (!string.Equals(
                    first.ResolvedGiSettings.StableHash,
                    sample.ResolvedGiSettings.StableHash,
                    StringComparison.Ordinal))
            {
                resolvedGiSettingsMismatchFrameCount++;
                if (resolvedGiSettingsDetailBudget > 0)
                {
                    IReadOnlyList<string> details = DescribeResolvedGiSettingsDifferences(
                        first.ResolvedGiSettings,
                        sample.ResolvedGiSettings,
                        resolvedGiSettingsDetailBudget);
                    if (details.Count == 0)
                    {
                        mismatches.Add(
                            $"Frame {index} changed the resolved GI settings hash " +
                            "without exposing a changed effective setting.");
                        resolvedGiSettingsDetailBudget--;
                    }
                    else
                    {
                        foreach (string detail in details)
                            mismatches.Add($"Frame {index} changed capture GI setting {detail}.");
                        resolvedGiSettingsDetailBudget -= details.Count;
                    }
                }
            }
            CompareInvariant(mismatches, index, "feature isolation", first.ActiveFeatureIsolation, sample.ActiveFeatureIsolation);
            CompareInvariant(mismatches, index, "debug view", first.GlobalIlluminationDebugView, sample.GlobalIlluminationDebugView);
            CompareInvariant(mismatches, index, "DDGI cache generation", first.CaptureFrame.DdgiCacheGeneration, sample.CaptureFrame.DdgiCacheGeneration);
            if (sample.CaptureFrame.WarmupState != DdgiRuntimeWarmupState.SteadyState)
                mismatches.Add($"Frame {index} warmup state is {sample.CaptureFrame.WarmupState}.");
            bool acceptedTailCertificate =
                SampleBenchmarkRunner.HasAcceptedCurrentSimpleDdgiTailCertificate(
                    sample);
            if (!movingTrajectory &&
                sample.CaptureFrame.TransportConvergencePending &&
                !acceptedTailCertificate)
                mismatches.Add($"Frame {index} still has pending transport convergence.");
            if (!movingTrajectory &&
                sample.SimpleDdgiActive != 0 &&
                sample.SimpleDdgiTransportV2Active != 0 &&
                !(sample.SimpleDdgiTransportTailCertificationEnabled
                    ? acceptedTailCertificate
                    : SampleBenchmarkRunner.HasSourceReadySimpleDdgiTransportPopulation(
                        sample)))
            {
                SimpleDdgiTransportConvergenceTelemetry convergence =
                    sample.SimpleDdgiTransportConvergence;
                int qualified = Math.Min(
                    Math.Max(0, convergence.ParticipatingProbeCount),
                    Math.Max(0, convergence.ConvergedProbeCount) +
                        Math.Max(0, convergence.RoutineSourceRepairProbeCount) +
                        Math.Max(0, convergence.RoutineMaintenancePendingProbeCount));
                mismatches.Add(
                    $"Frame {index} has only " +
                    $"{qualified}/" +
                    $"{Math.Max(0, convergence.ParticipatingProbeCount)} converged or scheduled-refresh transport probes; " +
                    "at least 95% are required.");
            }
            if (sample.DebugOverlayEnabled != 0 || sample.GpuDebugOverlayMicroseconds > 0)
                mismatches.Add($"Frame {index} rendered a debug overlay.");
            if (sample.ScreenshotRequested != 0 || sample.ScreenshotPendingCount != 0)
                mismatches.Add($"Frame {index} contained screenshot capture work.");
            if (sample.RenderDocCaptureRequested != 0)
                mismatches.Add($"Frame {index} requested a RenderDoc capture.");
            if (sample.DdgiInvestigationCountersReadbackValid != 0)
                mismatches.Add($"Frame {index} contains detailed DDGI counter readback.");
            if (sample.SimpleDdgiActive != 0)
            {
                SimpleDdgiCapacityTiming capacity =
                    sample.SimpleDdgiUploadTiming.CapacityDetails;
                if (!capacity.StableKeyHit)
                    mismatches.Add($"Frame {index} missed the stable DDGI capacity key.");
                if (capacity.TransitionCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.TransitionCount} DDGI capacity transitions.");
                if (capacity.DeviceIdleWaitCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.DeviceIdleWaitCount} DDGI device-idle waits.");
                if (capacity.BufferSizeLookupCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.BufferSizeLookupCount} stable-path buffer-size lookups.");
                if (capacity.DescriptorRegistrationCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.DescriptorRegistrationCount} stable-path descriptor registrations.");
            }
            if (sample.GpuTimingValid != 0)
            {
                long tolerance = ResolvePassTimestampReconciliationToleranceMicroseconds(sample);
                passTimestampToleranceMicroseconds = Math.Max(
                    passTimestampToleranceMicroseconds,
                    tolerance);
                long passSum = GpuIndependentTimings.Sum(selector =>
                    Math.Max(0L, selector.GetMicroseconds(sample)));
                long unexplained = sample.GpuFrameMicroseconds - passSum;
                if (Math.Abs(unexplained) > tolerance)
                {
                    mismatches.Add(
                        $"Frame {index} GPU pass sum differs from the frame by " +
                        $"{unexplained} us; tolerance is {tolerance} us.");
                }
            }
        }

        if (resolvedGiSettingsMismatchFrameCount > 0)
        {
            mismatches.Add(
                $"Resolved GI settings changed in {resolvedGiSettingsMismatchFrameCount} " +
                $"of {_samples.Count} measured frames; at most eight field differences are shown.");
        }

        string identityHash = CreateCaptureIdentityHash(first, includeTargetState: false);
        string fullIdentityHash = CreateCaptureIdentityHash(first, includeTargetState: true);
        string trajectorySequenceHash = CreateTrajectorySequenceHash(
            _samples,
            options);
        bool production = first.GiMeasurement.Mode == GiMeasurementMode.Production &&
            first.ValidationMode == RendererValidationMode.Off &&
            first.DdgiDetailedCountersCompiled == 0 &&
            first.DdgiDetailedCountersEnabled == 0 &&
            IsProductionBuildConfiguration(first.CaptureRun.BuildConfiguration);
        bool comparable = mismatches.Count == 0 &&
            (!options.RequireProductionTiming || production);
        return new SampleBenchmarkCaptureContract(
            comparable,
            production,
            options.CapturePairId?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(options.CaptureVariant)
                ? "baseline"
                : options.CaptureVariant.Trim(),
            identityHash,
            Array.AsReadOnly(mismatches.Distinct(StringComparer.Ordinal).ToArray()))
        {
            FullIdentityHash = fullIdentityHash,
            Trajectory = SampleBenchmarkTrajectory.GetName(options.Trajectory),
            TrajectoryFingerprint = expectedTrajectoryFingerprint,
            TrajectoryFrameCount = trajectoryFrameCount,
            TrajectorySequenceHash = trajectorySequenceHash,
            PassTimestampReconciliationToleranceMicroseconds =
                passTimestampToleranceMicroseconds
        };
    }

    private static bool IsProductionBuildConfiguration(string? value)
    {
        string configuration = value?.Split(';', 2)[0].Trim() ?? string.Empty;
        return string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration, "ShippingPerformance", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration, "ProfileSymbols", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireIdentity(
        ICollection<string> mismatches,
        string role,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"Capture {role} is unavailable.");
        }
    }

    private static long ResolvePassTimestampReconciliationToleranceMicroseconds(
        RendererDiagnostics diagnostics)
    {
        int roundedIntervalCount = 1 + GpuIndependentTimings.Count(selector =>
            selector.GetMicroseconds(diagnostics) > 0);
        double timestampPeriodMicroseconds = Math.Max(
            diagnostics.GpuTimestampPeriodNanoseconds,
            0.0f) / 1000.0;
        // Each duration subtracts two raw timestamps, then is rounded to an
        // integer microsecond. Include both device timestamp quantization and
        // independent integer-rounding error for every interval.
        double tolerance = roundedIntervalCount *
            (1.0 + timestampPeriodMicroseconds * 2.0);
        return Math.Max(1L, checked((long)Math.Ceiling(tolerance)));
    }

    private static void CompareInvariant<T>(
        List<string> mismatches,
        int frameIndex,
        string name,
        T expected,
        T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            mismatches.Add($"Frame {frameIndex} changed capture {name}.");
    }

    internal static IReadOnlyList<string> DescribeResolvedGiSettingsDifferences(
        ResolvedGiSettingsMetadata expected,
        ResolvedGiSettingsMetadata actual,
        int maximumDifferenceCount)
    {
        if (maximumDifferenceCount <= 0)
            return Array.Empty<string>();

        IReadOnlyDictionary<string, string> expectedSettings =
            IndexResolvedGiSettings(expected.EffectiveSettings);
        IReadOnlyDictionary<string, string> actualSettings =
            IndexResolvedGiSettings(actual.EffectiveSettings);
        var keys = new SortedSet<string>(expectedSettings.Keys, StringComparer.Ordinal);
        keys.UnionWith(actualSettings.Keys);
        var differences = new List<string>(Math.Min(maximumDifferenceCount, keys.Count));
        foreach (string key in keys)
        {
            bool hasExpected = expectedSettings.TryGetValue(key, out string? expectedValue);
            bool hasActual = actualSettings.TryGetValue(key, out string? actualValue);
            if (hasExpected && hasActual &&
                string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                continue;
            }

            differences.Add(
                $"'{key}' from {FormatResolvedGiSettingValue(hasExpected, expectedValue)} " +
                $"to {FormatResolvedGiSettingValue(hasActual, actualValue)}");
            if (differences.Count >= maximumDifferenceCount)
                break;
        }
        return Array.AsReadOnly(differences.ToArray());
    }

    private static IReadOnlyDictionary<string, string> IndexResolvedGiSettings(
        IReadOnlyList<string> settings)
    {
        var indexed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string setting in settings)
        {
            int separator = setting.IndexOf('=');
            string key = separator < 0 ? setting : setting[..separator];
            string value = separator < 0 ? string.Empty : setting[(separator + 1)..];
            indexed[key] = value;
        }
        return indexed;
    }

    private static string FormatResolvedGiSettingValue(bool present, string? value)
    {
        if (!present)
            return "<missing>";
        const int maximumLength = 160;
        string bounded = value ?? string.Empty;
        if (bounded.Length > maximumLength)
            bounded = bounded[..maximumLength] + "...";
        return "'" + bounded + "'";
    }

    private static string CreateCaptureIdentityHash(
        RendererDiagnostics diagnostics,
        bool includeTargetState)
    {
        var parts = new List<string>
        {
            diagnostics.CaptureGpuDeviceName,
            diagnostics.CaptureGpuDriverVersion,
            diagnostics.CaptureRenderWidth.ToString(CultureInfo.InvariantCulture),
            diagnostics.CaptureRenderHeight.ToString(CultureInfo.InvariantCulture),
            diagnostics.ActiveQualityPreset.ToString(),
            diagnostics.CaptureSceneAssetHash,
            diagnostics.CaptureSceneContentRevision.ToString(CultureInfo.InvariantCulture),
            diagnostics.CaptureCamera.ViewHash,
            diagnostics.CaptureCamera.ProjectionHash,
            diagnostics.CaptureRun.BuildConfiguration,
            diagnostics.CaptureRun.ExecutableHash,
            diagnostics.CaptureRun.Commit,
            diagnostics.CaptureRun.DirtyWorktreeState,
            diagnostics.CaptureRun.ShaderBundleHash,
            diagnostics.CaptureRun.SettingsSchemaVersion.ToString(
                CultureInfo.InvariantCulture),
            diagnostics.ResolvedGiSettings.StableHash,
            diagnostics.ActiveFeatureIsolation.ToString(),
            diagnostics.GlobalIlluminationDebugView.ToString(),
            diagnostics.CaptureFrame.DdgiCacheGeneration.ToString(
                CultureInfo.InvariantCulture)
        };
        if (includeTargetState)
        {
            parts.Add(diagnostics.CaptureSceneStateHash);
        }
        string canonical = string.Join("|", parts);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateTrajectorySequenceHash(
        IReadOnlyList<RendererDiagnostics> samples,
        SampleBenchmarkOptions options)
    {
        var canonical = new StringBuilder();
        canonical.Append("njulf-benchmark-trajectory-sequence/v1|")
            .Append(SampleBenchmarkTrajectory.GetName(options.Trajectory))
            .Append('|')
            .Append(options.TrajectoryFingerprint)
            .Append('\n');
        for (int index = 0; index < samples.Count; index++)
        {
            RendererDiagnostics sample = samples[index];
            PerformanceCaptureCameraMetadata camera = sample.CaptureCamera;
            canonical.Append(index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionX.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionY.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionZ.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.YawRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PitchRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FieldOfViewRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FarPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.ViewHash).Append('|')
                .Append(camera.ProjectionHash).Append('|')
                .Append(camera.CameraCutSerial.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(sample.CaptureSceneStateHash).Append('|')
                .Append(sample.ResolvedGiSettings.StableHash).Append('|')
                .Append(sample.ActiveFeatureIsolation).Append('|')
                .Append(sample.GlobalIlluminationDebugView)
                .Append('\n');
        }

        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static double MicrosecondsToMilliseconds(long microseconds)
    {
        return microseconds / 1000.0;
    }

    private sealed record TimingSelector(string Name, Func<RendererDiagnostics, long> GetMicroseconds);
}
