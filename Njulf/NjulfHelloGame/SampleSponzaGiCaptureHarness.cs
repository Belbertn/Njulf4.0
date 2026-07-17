using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// A named, immutable camera transform used by the Sponza GI closure capture.
/// Keeping all camera properties here prevents a screenshot from depending on
/// an interactive camera's previous FOV, clipping planes, or orientation.
/// </summary>
public sealed record SampleSponzaGiCameraBookmark(
    string Name,
    Vector3 Position,
    float Yaw,
    float Pitch,
    float FieldOfView,
    float NearPlane,
    float FarPlane);

/// <summary>
/// A world-space receiver region whose indirect lighting is evaluated by the
/// locked low/high capture. Bounds are deliberately scene coordinates rather
/// than screen rectangles so the evidence remains valid across resolution.
/// </summary>
public sealed record SampleSponzaGiReceiverRoi(
    string Name,
    BoundingBox Bounds,
    float MaximumPrimarySpacing,
    string Description,
    bool RequireCoarserFallback = true);

/// <summary>
/// One image that must be emitted at each endpoint. <see cref="DisableGlobalIllumination"/>
/// gives the direct-only reference a concrete, reversible definition rather
/// than relying on a post-processing interpretation of a beauty image.
/// </summary>
public sealed record SampleSponzaGiCaptureOutput(
    string Name,
    string FileStem,
    GlobalIlluminationDebugView DebugView,
    bool DisableGlobalIllumination,
    string Description);

public enum SampleSponzaGiCaptureStage : byte
{
    Warmup = 0,
    CaptureLowBookmark = 1,
    VerticalTraversal = 2,
    CaptureHighBookmark = 3,
    Complete = 4
}

/// <summary>
/// The current deterministic instruction for an active capture sequence.
/// A non-null output is captured after the frame rendered with this camera and
/// debug-view state.
/// </summary>
public sealed record SampleSponzaGiCaptureInstruction(
    SampleSponzaGiCaptureStage Stage,
    int StageFrameIndex,
    int StageFrameCount,
    SampleSponzaGiCameraBookmark Camera,
    SampleSponzaGiCaptureOutput? Output,
    string BookmarkName,
    bool CaptureWindowAfterRenderedFrame);

/// <summary>
/// Stable artifact metadata written next to a runtime capture. Paths are
/// relative to the capture directory so a result can be moved as one unit.
/// </summary>
public sealed record SampleSponzaGiCapturedArtifact(
    string Bookmark,
    string Output,
    string Kind,
    string RelativePath,
    string? Sha256 = null,
    long ByteLength = 0,
    string VerificationStatus = "unverified");

/// <summary>
/// Separates a timing-valid endpoint run from a verbose debug-image review.
/// Diagnostic views intentionally do not contribute timing samples because they
/// exercise different shader paths than the production beauty frame.
/// </summary>
public enum SampleSponzaGiCaptureMode : byte
{
    ProductionTiming = 0,
    DetailedDiagnostics = 1
}

/// <summary>
/// A machine-readable, baseline-aware visual-review requirement. The contract
/// deliberately contains no invented thresholds while the approved reference
/// images are unavailable; an external image analyzer supplies those only when
/// a reviewed baseline is imported.
/// </summary>
public sealed record SampleSponzaGiVisualMetricRule(
    string Name,
    string Unit,
    string Description,
    bool RequiresApprovedBaseline);

/// <summary>Metric requirements for one locked world-space receiver ROI.</summary>
public sealed record SampleSponzaGiVisualMetricRoi(
    string Name,
    BoundingBox Bounds,
    IReadOnlyList<string> RequiredOutputs,
    IReadOnlyList<SampleSponzaGiVisualMetricRule> RequiredMetrics);

/// <summary>
/// Deterministic hand-off file for an offline visual metric evaluator. Its
/// status cannot be interpreted as a visual pass until approved reference
/// images and thresholds have been supplied.
/// </summary>
public sealed record SampleSponzaGiVisualMetricGate(
    string SchemaVersion,
    string ContractFingerprint,
    SampleSponzaGiCaptureMode CaptureMode,
    string TimingClassification,
    string EvaluationStatus,
    IReadOnlyList<SampleSponzaGiVisualMetricRoi> ReceiverRois);

/// <summary>
/// Locked capture data for the 2026-07-16 Sponza GI transport/support closure. This
/// type is intentionally renderer-independent: it can be validated in CI and
/// serialized before a Vulkan device is created.
/// </summary>
public sealed class SampleSponzaGiCaptureContract
{
    public const string CurrentSchemaVersion = "realtime-gi-closure-sponza-capture/v4";
    public const string VisualMetricGateSchemaVersion = "realtime-gi-closure-sponza-visual-metrics/v1";
    public const string CoverageOracleSchemaVersion = "realtime-gi-closure-sponza-coverage-oracle/v1";
    public const int LockedWidth = 1600;
    public const int LockedHeight = 900;
    public const int FixedFramesPerSecond = 60;
    public const int WarmupFrameCount = 360;
    public const int VerticalTraversalDurationSeconds = 16;
    // One frame presents the requested state, one spans the two-frame GPU timing
    // latency, and the final frame captures the held state with settled telemetry.
    public const int FramesPerEndpointOutput = 3;
    public const uint FixedRandomSeed = 0x2026_0715u;

    private static readonly JsonSerializerOptions ContractJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly SampleSponzaGiCaptureContract DefaultContract = CreateDefault();

    public string SchemaVersion { get; }
    public SampleSceneKind SceneKind { get; }
    public SamplePerformanceScenario Scenario { get; }
    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public int WarmupFrames { get; }
    public int VerticalPathDurationSeconds { get; }
    public uint RandomSeed { get; }
    public SampleSponzaGiCameraBookmark LowBookmark { get; }
    public SampleSponzaGiCameraBookmark HighBookmark { get; }
    public BoundingBox SceneBounds { get; }
    public IReadOnlyList<SampleSponzaGiReceiverRoi> ReceiverRois { get; }
    public IReadOnlyList<SampleSponzaGiCaptureOutput> Outputs { get; }
    public string Fingerprint { get; }

    public int VerticalTraversalFrameCount => checked(VerticalPathDurationSeconds * FramesPerSecond);

    /// <summary>Frames from the initial low warmup through the final high image.</summary>
    public int TotalCaptureFrameCount => checked(
        WarmupFrames +
        VerticalTraversalFrameCount +
        Outputs.Count * 2 * FramesPerEndpointOutput);

    public static SampleSponzaGiCaptureContract Default => DefaultContract;

    public static bool UsesDetailedInvestigationCounters(SampleSponzaGiCaptureMode captureMode) => captureMode switch
    {
        SampleSponzaGiCaptureMode.ProductionTiming => false,
        SampleSponzaGiCaptureMode.DetailedDiagnostics => true,
        _ => throw new ArgumentOutOfRangeException(nameof(captureMode))
    };

    public SampleSponzaGiCaptureContract(
        string schemaVersion,
        SampleSceneKind sceneKind,
        SamplePerformanceScenario scenario,
        int width,
        int height,
        int framesPerSecond,
        int warmupFrames,
        int verticalPathDurationSeconds,
        uint randomSeed,
        SampleSponzaGiCameraBookmark lowBookmark,
        SampleSponzaGiCameraBookmark highBookmark,
        BoundingBox sceneBounds,
        IReadOnlyList<SampleSponzaGiReceiverRoi> receiverRois,
        IReadOnlyList<SampleSponzaGiCaptureOutput> outputs)
    {
        SchemaVersion = schemaVersion ?? throw new ArgumentNullException(nameof(schemaVersion));
        SceneKind = sceneKind;
        Scenario = scenario;
        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;
        WarmupFrames = warmupFrames;
        VerticalPathDurationSeconds = verticalPathDurationSeconds;
        RandomSeed = randomSeed;
        LowBookmark = lowBookmark ?? throw new ArgumentNullException(nameof(lowBookmark));
        HighBookmark = highBookmark ?? throw new ArgumentNullException(nameof(highBookmark));
        SceneBounds = sceneBounds;
        ReceiverRois = Array.AsReadOnly(receiverRois?.ToArray() ?? throw new ArgumentNullException(nameof(receiverRois)));
        Outputs = Array.AsReadOnly(outputs?.ToArray() ?? throw new ArgumentNullException(nameof(outputs)));

        Validate();
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>
    /// The fixed low-to-high trajectory. Frame zero is exactly the low bookmark;
    /// the final fixed-timestep frame is exactly the high bookmark. Smoothstep
    /// easing removes an artificial acceleration discontinuity without adding
    /// any nondeterministic time dependence.
    /// </summary>
    public SampleSponzaGiCameraBookmark SampleVerticalTraversalFrame(int frameIndex)
    {
        int frameCount = VerticalTraversalFrameCount;
        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameIndex),
                frameIndex,
                $"The vertical traversal contains frames 0 through {frameCount - 1}.");
        }

        float linear = frameCount <= 1 ? 1.0f : frameIndex / (float)(frameCount - 1);
        float t = linear * linear * (3.0f - 2.0f * linear);
        return new SampleSponzaGiCameraBookmark(
            "SponzaPlazaUpperFacadeVerticalTraversal",
            Vector3.Lerp(LowBookmark.Position, HighBookmark.Position, t),
            Lerp(LowBookmark.Yaw, HighBookmark.Yaw, t),
            Lerp(LowBookmark.Pitch, HighBookmark.Pitch, t),
            Lerp(LowBookmark.FieldOfView, HighBookmark.FieldOfView, t),
            Lerp(LowBookmark.NearPlane, HighBookmark.NearPlane, t),
            Lerp(LowBookmark.FarPlane, HighBookmark.FarPlane, t));
    }

    /// <summary>
    /// Creates every fixed-timestep sample in the locked vertical trajectory
    /// for the CPU receiver-coverage oracle. Checking only endpoints or a
    /// midpoint is insufficient: a ring can recenter or lose its transition
    /// fallback between otherwise-valid bookmarks.
    /// </summary>
    public IReadOnlyList<SimpleDdgiCoverageCameraSample> CreateCoverageCameraPath()
    {
        var path = new SimpleDdgiCoverageCameraSample[VerticalTraversalFrameCount];
        for (int frameIndex = 0; frameIndex < path.Length; frameIndex++)
        {
            SampleSponzaGiCameraBookmark camera = SampleVerticalTraversalFrame(frameIndex);
            string name = frameIndex switch
            {
                0 => LowBookmark.Name,
                _ when frameIndex == path.Length - 1 => HighBookmark.Name,
                _ => $"SponzaPlazaUpperFacadeVerticalTraversal-{frameIndex:D4}"
            };
            path[frameIndex] = new SimpleDdgiCoverageCameraSample(name, camera.Position);
        }

        return Array.AsReadOnly(path);
    }

    public IReadOnlyList<SimpleDdgiReceiverCoverageRegion> CreateCoverageRegions()
    {
        var regions = new SimpleDdgiReceiverCoverageRegion[ReceiverRois.Count];
        for (int i = 0; i < regions.Length; i++)
        {
            SampleSponzaGiReceiverRoi roi = ReceiverRois[i];
            regions[i] = new SimpleDdgiReceiverCoverageRegion(
                roi.Name,
                roi.Bounds,
                roi.MaximumPrimarySpacing,
                roi.RequireCoarserFallback);
        }

        return regions;
    }

    /// <summary>
    /// Returns lock violations instead of mutating settings. The caller can
    /// fail a scripted capture before producing misleading evidence.
    /// </summary>
    public IReadOnlyList<string> ValidateLockedSettings(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        var violations = new List<string>();
        if (settings.AutoExposure.Enabled)
            violations.Add("Auto exposure must be disabled.");
        if (!NearlyEqual(settings.Exposure, 1.0f))
            violations.Add($"Exposure must be 1.0, not {settings.Exposure.ToString("0.###", CultureInfo.InvariantCulture)}.");
        if (settings.DynamicResolution.Enabled ||
            !NearlyEqual(settings.DynamicResolution.MinimumScale, 1.0f) ||
            !NearlyEqual(settings.DynamicResolution.MaximumScale, 1.0f))
        {
            violations.Add("Dynamic resolution must be disabled at a fixed 1.0 scale.");
        }
        if (!NearlyEqual(settings.ResolutionScale, 1.0f))
            violations.Add("Render resolution scale must be 1.0.");
        if (settings.Bloom.Enabled)
            violations.Add("Bloom must be disabled for the locked GI comparison.");
        if (settings.Fog.Enabled)
            violations.Add("Fog must be disabled for the locked GI comparison.");
        if (settings.Particles.Enabled)
            violations.Add("Particles must be disabled for the locked GI comparison.");
        if (settings.Animation.Enabled)
            violations.Add("Animation must be disabled for the locked GI comparison.");
        if (!settings.Environment.Enabled ||
            !NearlyEqual(settings.Environment.SkyIntensity, 0.45f) ||
            !NearlyEqual(settings.Environment.DiffuseIntensity, 0.10f) ||
            !NearlyEqual(settings.Environment.SpecularIntensity, 0.25f) ||
            !NearlyEqual(settings.Environment.RotationRadians, 0.0f))
        {
            violations.Add("The canonical static Sponza environment must be enabled with locked intensities and rotation.");
        }
        if (!settings.Shadows.DirectionalShadowsEnabled ||
            settings.Shadows.DirectionalShadowMapSize != 2048 ||
            settings.Shadows.DirectionalCascadeCount != 3 ||
            settings.Shadows.PcfRadius != 1)
        {
            violations.Add("The canonical directional sun and shadow settings must be locked.");
        }
        if (!settings.GlobalIllumination.Enabled ||
            !settings.GlobalIllumination.EffectiveUseSimpleDdgi)
        {
            violations.Add("Simple DDGI must be enabled for the non-direct-only capture outputs.");
        }
        if (!NearlyEqual(settings.GlobalIllumination.IndirectIntensity, 1.0f))
            violations.Add("Sponza physical indirect intensity must be 1.0.");
        if (!NearlyEqual(settings.GlobalIllumination.EnvironmentFallbackIntensity, 1.0f))
            violations.Add("Sponza environment fallback intensity must be 1.0.");

        return violations;
    }

    public string GetRelativeImagePath(string bookmarkName, SampleSponzaGiCaptureOutput output)
    {
        if (string.IsNullOrWhiteSpace(bookmarkName))
            throw new ArgumentException("Bookmark name is required.", nameof(bookmarkName));
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        return Path.Combine(ToFileSegment(bookmarkName), $"{output.FileStem}.png");
    }

    public string GetRelativeWindowFallbackImagePath(string bookmarkName, SampleSponzaGiCaptureOutput output)
    {
        string imagePath = GetRelativeImagePath(bookmarkName, output);
        string directory = Path.GetDirectoryName(imagePath) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(imagePath);
        return Path.Combine(directory, $"{fileName}.window.png");
    }

    public void WriteContract(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "sponza-gi-capture-contract.json");
        WriteJsonAtomically(path, this);
    }

    /// <summary>
    /// Writes the deterministic visual-review hand-off. It names every ROI and
    /// required signal but intentionally leaves quality thresholds unset until
    /// reviewed source captures are imported.
    /// </summary>
    public void WriteVisualMetricGate(string outputDirectory, SampleSponzaGiCaptureMode captureMode)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        if (!Enum.IsDefined(captureMode))
            throw new ArgumentOutOfRangeException(nameof(captureMode));

        Directory.CreateDirectory(outputDirectory);
        WriteJsonAtomically(
            Path.Combine(outputDirectory, "sponza-gi-visual-metric-gate.json"),
            CreateVisualMetricGate(captureMode));
    }

    /// <summary>
    /// Writes a compact, reproducible coverage-oracle result. Full per-point
    /// samples remain available from the in-memory report; the sidecar retains
    /// the path cardinality, layout decision, and actionable failures without
    /// needlessly emitting tens of thousands of near-duplicate samples.
    /// </summary>
    public void WriteCoverageOracleReport(
        string outputDirectory,
        SimpleDdgiReceiverCoverageReport report)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        if (report == null)
            throw new ArgumentNullException(nameof(report));

        Directory.CreateDirectory(outputDirectory);
        var payload = new
        {
            schemaVersion = CoverageOracleSchemaVersion,
            contractFingerprint = Fingerprint,
            verticalTrajectoryFrameCount = VerticalTraversalFrameCount,
            receiverRoiCount = ReceiverRois.Count,
            representativePointCount = report.Samples.Count / Math.Max(1, ReceiverRois.Count * VerticalTraversalFrameCount),
            isCovered = report.IsCovered,
            expectedRingRecenterEvents = report.ExpectedRingRecenterEvents,
            layout = report.Layout,
            issues = report.Issues
        };
        WriteJsonAtomically(Path.Combine(outputDirectory, "sponza-gi-coverage-oracle.json"), payload);
    }

    public void WriteRunManifest(
        string outputDirectory,
        IReadOnlyList<SampleSponzaGiCapturedArtifact> artifacts,
        string status,
        string? failureReason = null,
        SampleSponzaGiCaptureMode captureMode = SampleSponzaGiCaptureMode.DetailedDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        if (artifacts == null)
            throw new ArgumentNullException(nameof(artifacts));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("A capture status is required.", nameof(status));
        if (!Enum.IsDefined(captureMode))
            throw new ArgumentOutOfRangeException(nameof(captureMode));

        string normalizedStatus = status.Trim().ToLowerInvariant();
        if (normalizedStatus is not ("running" or "awaiting-renderer-screenshots" or "completed" or "failed"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "The capture status must be running, awaiting-renderer-screenshots, completed, or failed.");
        }

        Directory.CreateDirectory(outputDirectory);
        SampleSponzaGiCapturedArtifact[] manifestArtifacts = artifacts
            .Select(static artifact => artifact with { RelativePath = NormalizeRelativePath(artifact.RelativePath) })
            .ToArray();
        if (string.Equals(normalizedStatus, "completed", StringComparison.Ordinal))
        {
            IReadOnlyList<string> blockers = GetCompletionBlockers(outputDirectory, manifestArtifacts);
            if (blockers.Count != 0)
            {
                throw new InvalidOperationException(
                    "The Sponza GI capture cannot be completed until every required renderer screenshot and artifact is verified: " +
                    string.Join(" ", blockers));
            }
        }

        var manifest = new
        {
            schemaVersion = SchemaVersion,
            contractFingerprint = Fingerprint,
            status = normalizedStatus,
            failureReason,
            captureMode,
            timingClassification = GetTimingClassification(captureMode),
            artifactHashAlgorithm = "SHA-256",
            artifacts = manifestArtifacts
        };
        WriteJsonAtomically(Path.Combine(outputDirectory, "sponza-gi-capture-run.json"), manifest);
    }

    /// <summary>
    /// Hashes and validates an artifact inside the capture directory. PNG
    /// artifacts must include a complete PNG signature, so a zero-byte or
    /// partially-created renderer target cannot satisfy the completion gate.
    /// </summary>
    public bool TryVerifyArtifact(
        string outputDirectory,
        SampleSponzaGiCapturedArtifact artifact,
        out SampleSponzaGiCapturedArtifact verifiedArtifact,
        out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        if (artifact == null)
            throw new ArgumentNullException(nameof(artifact));

        verifiedArtifact = artifact;
        string relativePath;
        try
        {
            relativePath = NormalizeRelativePath(artifact.RelativePath);
        }
        catch (ArgumentException ex)
        {
            failureReason = ex.Message;
            return false;
        }

        string root;
        string fullPath;
        try
        {
            root = Path.GetFullPath(outputDirectory);
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (ArgumentException ex)
        {
            failureReason = $"Artifact '{relativePath}' has an invalid filesystem path: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            failureReason = $"Artifact '{relativePath}' has an unsupported filesystem path: {ex.Message}";
            return false;
        }
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"Artifact '{relativePath}' resolves outside the capture directory.";
            return false;
        }
        if (!File.Exists(fullPath))
        {
            failureReason = $"Artifact '{relativePath}' does not exist yet.";
            return false;
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (ArgumentException ex)
        {
            failureReason = $"Artifact '{relativePath}' could not be inspected: {ex.Message}";
            return false;
        }
        long byteLength;
        try
        {
            byteLength = fileInfo.Length;
        }
        catch (IOException ex)
        {
            failureReason = $"Artifact '{relativePath}' could not be inspected: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            failureReason = $"Artifact '{relativePath}' could not be inspected: {ex.Message}";
            return false;
        }
        if (byteLength <= 0)
        {
            failureReason = $"Artifact '{relativePath}' is empty.";
            return false;
        }
        if (string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase) &&
            !HasPngSignature(fullPath))
        {
            failureReason = $"Artifact '{relativePath}' is not a complete PNG file.";
            return false;
        }

        string sha256;
        try
        {
            using FileStream stream = File.OpenRead(fullPath);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException ex)
        {
            failureReason = $"Artifact '{relativePath}' could not be read for hashing: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            failureReason = $"Artifact '{relativePath}' could not be read for hashing: {ex.Message}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(artifact.Sha256) &&
            !string.Equals(artifact.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"Artifact '{relativePath}' changed after it was hashed.";
            return false;
        }
        if (artifact.ByteLength > 0 && artifact.ByteLength != byteLength)
        {
            failureReason = $"Artifact '{relativePath}' changed size after it was hashed.";
            return false;
        }

        verifiedArtifact = artifact with
        {
            RelativePath = relativePath,
            Sha256 = sha256,
            ByteLength = byteLength,
            VerificationStatus = "verified"
        };
        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns every condition that prevents a terminal manifest. This is used
    /// by the runtime to wait for asynchronous renderer-target screenshots and
    /// by <see cref="WriteRunManifest"/> as a final fail-closed guard.
    /// </summary>
    public IReadOnlyList<string> GetCompletionBlockers(
        string outputDirectory,
        IReadOnlyList<SampleSponzaGiCapturedArtifact> artifacts)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        if (artifacts == null)
            throw new ArgumentNullException(nameof(artifacts));

        var blockers = new List<string>();
        AddExpectedArtifactBlockers(
            blockers,
            outputDirectory,
            artifacts,
            string.Empty,
            string.Empty,
            "capture-contract",
            "sponza-gi-capture-contract.json");
        AddExpectedArtifactBlockers(
            blockers,
            outputDirectory,
            artifacts,
            string.Empty,
            string.Empty,
            "visual-metric-gate",
            "sponza-gi-visual-metric-gate.json");
        AddExpectedArtifactBlockers(
            blockers,
            outputDirectory,
            artifacts,
            string.Empty,
            string.Empty,
            "coverage-oracle",
            "sponza-gi-coverage-oracle.json");
        foreach (SampleSponzaGiCameraBookmark bookmark in new[] { LowBookmark, HighBookmark })
        {
            foreach (SampleSponzaGiCaptureOutput output in Outputs)
            {
                string imagePath = GetRelativeImagePath(bookmark.Name, output);
                AddExpectedArtifactBlockers(
                    blockers,
                    outputDirectory,
                    artifacts,
                    bookmark.Name,
                    output.Name,
                    "window-screenshot",
                    imagePath);
                AddExpectedArtifactBlockers(
                    blockers,
                    outputDirectory,
                    artifacts,
                    bookmark.Name,
                    output.Name,
                    "renderer-screenshot",
                    Path.ChangeExtension(imagePath, ".renderer.png"));
            }

            AddExpectedArtifactBlockers(
                blockers,
                outputDirectory,
                artifacts,
                bookmark.Name,
                "beauty",
                "performance-snapshot",
                expectedRelativePath: null);
        }

        // A completed run is a content-addressed artifact set. Re-hashing each
        // listed file catches post-capture mutation, including a renderer image
        // that was observed before its asynchronous writer finished.
        foreach (SampleSponzaGiCapturedArtifact artifact in artifacts)
        {
            if (!string.Equals(artifact.VerificationStatus, "verified", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(artifact.Sha256) || artifact.ByteLength <= 0)
            {
                blockers.Add($"Artifact '{artifact.RelativePath}' is not hash-verified.");
                continue;
            }

            if (!TryVerifyArtifact(outputDirectory, artifact, out _, out string reason))
                blockers.Add(reason);
        }

        return Array.AsReadOnly(blockers.ToArray());
    }

    /// <summary>Builds a baseline-aware visual metric contract without inventing a baseline.</summary>
    public SampleSponzaGiVisualMetricGate CreateVisualMetricGate(SampleSponzaGiCaptureMode captureMode)
    {
        if (!Enum.IsDefined(captureMode))
            throw new ArgumentOutOfRangeException(nameof(captureMode));

        string[] requiredOutputs = Outputs.Select(static output => output.Name).ToArray();
        var rois = new SampleSponzaGiVisualMetricRoi[ReceiverRois.Count];
        for (int i = 0; i < rois.Length; i++)
        {
            SampleSponzaGiReceiverRoi roi = ReceiverRois[i];
            rois[i] = new SampleSponzaGiVisualMetricRoi(
                roi.Name,
                roi.Bounds,
                Array.AsReadOnly(requiredOutputs.ToArray()),
                CreateRequiredVisualMetrics());
        }

        return new SampleSponzaGiVisualMetricGate(
            VisualMetricGateSchemaVersion,
            Fingerprint,
            captureMode,
            GetTimingClassification(captureMode),
            "not-evaluated-no-approved-baseline",
            Array.AsReadOnly(rois));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SchemaVersion))
            throw new InvalidOperationException("The capture schema version is required.");
        if (SceneKind != SampleSceneKind.SponzaPlaza)
            throw new InvalidOperationException("The closure capture must run on the Sponza plaza scene.");
        if (Scenario != SamplePerformanceScenario.GiSponzaRightWallStationary)
            throw new InvalidOperationException("The closure capture must use the stationary Sponza GI scenario.");
        if (Width <= 0 || Height <= 0)
            throw new InvalidOperationException("The locked capture resolution must be positive.");
        if (FramesPerSecond <= 0 || WarmupFrames <= 0)
            throw new InvalidOperationException("The capture cadence and warmup must be positive.");
        if (VerticalPathDurationSeconds is < 10 or > 20)
            throw new InvalidOperationException("The vertical traversal must last from 10 through 20 seconds.");
        if (string.Equals(LowBookmark.Name, HighBookmark.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("Low and high bookmarks require distinct stable names.");

        ValidateBookmark(LowBookmark, nameof(LowBookmark));
        ValidateBookmark(HighBookmark, nameof(HighBookmark));
        ValidateBounds(SceneBounds, nameof(SceneBounds));

        if (ReceiverRois.Count != 6)
            throw new InvalidOperationException(
                "The closure capture requires central façade, both side galleries, right wall, arcade, and outdoor receiver ROIs.");
        if (Outputs.Count != 13)
            throw new InvalidOperationException(
                "The closure capture requires the thirteen locked beauty/direct/GI diagnostic outputs.");

        ValidateDistinctNames(ReceiverRois.Select(static roi => roi.Name), "receiver ROI");
        ValidateDistinctNames(Outputs.Select(static output => output.Name), "output");
        ValidateDistinctNames(Outputs.Select(static output => output.FileStem), "output file stem");

        foreach (SampleSponzaGiReceiverRoi roi in ReceiverRois)
        {
            ValidateBounds(roi.Bounds, roi.Name);
            if (!float.IsFinite(roi.MaximumPrimarySpacing) || roi.MaximumPrimarySpacing <= 0.0f)
                throw new InvalidOperationException($"Receiver ROI '{roi.Name}' needs a positive finite primary-spacing target.");
            if (!roi.RequireCoarserFallback)
            {
                throw new InvalidOperationException(
                    $"Locked receiver ROI '{roi.Name}' must require a coarser transition fallback. " +
                    "Relaxing this requires a new capture schema and visual review contract.");
            }
        }

        foreach (SampleSponzaGiCaptureOutput output in Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.FileStem))
                throw new InvalidOperationException($"Capture output '{output.Name}' needs a file stem.");
            if (!Enum.IsDefined(output.DebugView))
                throw new InvalidOperationException($"Capture output '{output.Name}' has an unknown GI debug view.");
        }

        string[] requiredReceiverRois =
        [
            "central-upper-facade", "right-upper-wall", "left-gallery-interior",
            "right-gallery-interior", "arcade-interior", "outdoor-reference-patch"
        ];
        if (!ReceiverRois.Select(static roi => roi.Name).OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(
                requiredReceiverRois.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The locked receiver set changed. Update the contract schema and visual review gate together.");
        }

        string[] requiredOutputs =
        [
            "beauty", "direct-only", "final-indirect", "sampled-irradiance", "final-diffuse",
            "volume-contributor", "gather-clipmap", "gather-blend-weight", "gather-fallback",
            "support", "visibility", "ownership", "fallback"
        ];
        if (!Outputs.Select(static output => output.Name).OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(
                requiredOutputs.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The locked output set changed. Update the contract schema and visual review gate together.");
        }
    }

    private static SampleSponzaGiCaptureContract CreateDefault()
    {
        const float fov = MathF.PI / 3.2f;
        return new SampleSponzaGiCaptureContract(
            CurrentSchemaVersion,
            SampleSceneKind.SponzaPlaza,
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            LockedWidth,
            LockedHeight,
            FixedFramesPerSecond,
            WarmupFrameCount,
            VerticalTraversalDurationSeconds,
            FixedRandomSeed,
            new SampleSponzaGiCameraBookmark(
                "SponzaPlazaUpperFacadeLow",
                new Vector3(0.0f, 1.35f, 0.0f),
                -MathF.PI,
                -0.16f,
                fov,
                0.05f,
                250.0f),
            new SampleSponzaGiCameraBookmark(
                "SponzaPlazaUpperFacadeHigh",
                // The reported camera-relative near-ring origin moved by nine
                // metres between the supplied low/high captures. The source
                // attachment lacks camera metadata, so this is the named,
                // deterministic transform used by all subsequent captures.
                new Vector3(0.0f, 10.35f, 0.0f),
                -MathF.PI,
                -0.16f,
                fov,
                0.05f,
                250.0f),
            new BoundingBox(
                new Vector3(-17.0f, -1.0f, -10.0f),
                new Vector3(21.0f, 20.0f, 15.0f)),
            [
                new SampleSponzaGiReceiverRoi(
                    "central-upper-facade",
                    new BoundingBox(new Vector3(-4.5f, 10.0f, -4.5f), new Vector3(7.5f, 18.0f, 9.5f)),
                    3.75f,
                    "Shared central upper-façade receivers that must remain inside the generic camera rings.",
                    RequireCoarserFallback: true),
                new SampleSponzaGiReceiverRoi(
                    "right-upper-wall",
                    new BoundingBox(new Vector3(11.5f, 10.0f, -6.5f), new Vector3(18.5f, 18.0f, 11.5f)),
                    3.75f,
                    "Right upper wall, retained to tie the reproduction to the original right-wall scenario.",
                    RequireCoarserFallback: true),
                new SampleSponzaGiReceiverRoi(
                    "left-gallery-interior",
                    new BoundingBox(new Vector3(-14.5f, 5.0f, -6.5f), new Vector3(-8.5f, 8.5f, 11.5f)),
                    3.75f,
                    "Left colonnade/gallery receivers that exposed the zero-support black region.",
                    RequireCoarserFallback: true),
                new SampleSponzaGiReceiverRoi(
                    "right-gallery-interior",
                    new BoundingBox(new Vector3(11.5f, 5.0f, -6.5f), new Vector3(18.5f, 8.5f, 11.5f)),
                    3.75f,
                    "Right gallery counterpart used to detect asymmetric receiver-coverage seams.",
                    RequireCoarserFallback: true),
                new SampleSponzaGiReceiverRoi(
                    "arcade-interior",
                    new BoundingBox(new Vector3(-4.5f, 5.0f, -4.5f), new Vector3(7.5f, 8.5f, 9.5f)),
                    3.75f,
                    "Balcony and arcade coverage below the upper façade.",
                    RequireCoarserFallback: true),
                new SampleSponzaGiReceiverRoi(
                    "outdoor-reference-patch",
                    new BoundingBox(new Vector3(-2.0f, 1.0f, 4.0f), new Vector3(2.0f, 3.0f, 7.0f)),
                    3.75f,
                    "Sunlit outdoor/courtyard reference patch for distinguishing transport changes from exposure changes.",
                    RequireCoarserFallback: true)
            ],
            [
                new SampleSponzaGiCaptureOutput("beauty", "beauty", GlobalIlluminationDebugView.None, false, "Full reference beauty image."),
                new SampleSponzaGiCaptureOutput("direct-only", "direct-only", GlobalIlluminationDebugView.None, true, "GI-disabled direct-light reference."),
                new SampleSponzaGiCaptureOutput("final-indirect", "final-indirect", GlobalIlluminationDebugView.FinalIndirect, false, "Final indirect debug output."),
                new SampleSponzaGiCaptureOutput("sampled-irradiance", "sampled-irradiance", GlobalIlluminationDebugView.DdgiSampledIrradiance, false, "Sampled DDGI irradiance before final diffuse composition."),
                new SampleSponzaGiCaptureOutput("final-diffuse", "final-diffuse", GlobalIlluminationDebugView.DdgiFinalDiffuse, false, "Final diffuse GI after material composition."),
                new SampleSponzaGiCaptureOutput("volume-contributor", "volume-contributor", GlobalIlluminationDebugView.DdgiGatherLocalVolume, false, "Local authored-volume contribution; empty in the default Sponza profile."),
                new SampleSponzaGiCaptureOutput("gather-clipmap", "gather-clipmap", GlobalIlluminationDebugView.DdgiGatherClipmap, false, "Selected camera-ring contribution and coverage."),
                new SampleSponzaGiCaptureOutput("gather-blend-weight", "gather-blend-weight", GlobalIlluminationDebugView.DdgiGatherBlendWeight, false, "Secondary-volume contribution weight."),
                new SampleSponzaGiCaptureOutput("gather-fallback", "gather-fallback", GlobalIlluminationDebugView.DdgiGatherFallback, false, "Receivers that required a fallback volume gather."),
                new SampleSponzaGiCaptureOutput("support", "support", GlobalIlluminationDebugView.DdgiSupportCoverage, false, "Valid DDGI support coverage."),
                new SampleSponzaGiCaptureOutput("visibility", "visibility", GlobalIlluminationDebugView.DdgiVisibility, false, "DDGI visibility term."),
                new SampleSponzaGiCaptureOutput("ownership", "ownership", GlobalIlluminationDebugView.DdgiEffectiveWeight, false, "Effective normalized DDGI ownership weight."),
                new SampleSponzaGiCaptureOutput("fallback", "fallback", GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight, false, "Environment fallback composition weight.")
            ]);
    }

    private string ComputeFingerprint()
    {
        var builder = new StringBuilder(1024);
        Append(builder, SchemaVersion);
        Append(builder, SceneKind.ToString());
        Append(builder, Scenario.ToString());
        Append(builder, Width);
        Append(builder, Height);
        Append(builder, FramesPerSecond);
        Append(builder, WarmupFrames);
        Append(builder, VerticalPathDurationSeconds);
        Append(builder, "coverage-oracle-full-fixed-trajectory");
        Append(builder, VerticalTraversalFrameCount);
        Append(builder, FramesPerEndpointOutput);
        Append(builder, VisualMetricGateSchemaVersion);
        foreach (SampleSponzaGiVisualMetricRule metric in CreateRequiredVisualMetrics())
        {
            Append(builder, metric.Name);
            Append(builder, metric.Unit);
            Append(builder, metric.Description);
            Append(builder, metric.RequiresApprovedBaseline ? 1 : 0);
        }
        Append(builder, RandomSeed);
        AppendBookmark(builder, LowBookmark);
        AppendBookmark(builder, HighBookmark);
        AppendVector(builder, SceneBounds.Min);
        AppendVector(builder, SceneBounds.Max);
        foreach (SampleSponzaGiReceiverRoi roi in ReceiverRois)
        {
            Append(builder, roi.Name);
            AppendVector(builder, roi.Bounds.Min);
            AppendVector(builder, roi.Bounds.Max);
            Append(builder, roi.MaximumPrimarySpacing);
            Append(builder, roi.Description);
            Append(builder, roi.RequireCoarserFallback ? 1 : 0);
        }
        foreach (SampleSponzaGiCaptureOutput output in Outputs)
        {
            Append(builder, output.Name);
            Append(builder, output.FileStem);
            Append(builder, output.DebugView.ToString());
            Append(builder, output.DisableGlobalIllumination ? 1 : 0);
            Append(builder, output.Description);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void AddExpectedArtifactBlockers(
        List<string> blockers,
        string outputDirectory,
        IReadOnlyList<SampleSponzaGiCapturedArtifact> artifacts,
        string bookmark,
        string output,
        string kind,
        string? expectedRelativePath)
    {
        SampleSponzaGiCapturedArtifact[] matches = artifacts
            .Where(artifact =>
                string.Equals(artifact.Bookmark, bookmark, StringComparison.Ordinal) &&
                string.Equals(artifact.Output, output, StringComparison.Ordinal) &&
                string.Equals(artifact.Kind, kind, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            blockers.Add(
                $"Expected exactly one {kind} artifact for '{bookmark}' / '{output}', found {matches.Length}.");
            return;
        }

        SampleSponzaGiCapturedArtifact artifact = matches[0];
        string? normalizedActualPath = null;
        string? normalizedExpectedPath = null;
        try
        {
            normalizedActualPath = NormalizeRelativePath(artifact.RelativePath);
            normalizedExpectedPath = expectedRelativePath == null
                ? null
                : NormalizeRelativePath(expectedRelativePath);
        }
        catch (ArgumentException ex)
        {
            blockers.Add($"{kind} artifact for '{bookmark}' / '{output}' has an invalid path: {ex.Message}");
            return;
        }
        if (normalizedExpectedPath != null &&
            !string.Equals(normalizedActualPath, normalizedExpectedPath, StringComparison.Ordinal))
        {
            blockers.Add(
                $"{kind} artifact for '{bookmark}' / '{output}' has an unexpected path '{artifact.RelativePath}'.");
            return;
        }
        if (!string.Equals(artifact.VerificationStatus, "verified", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(artifact.Sha256) || artifact.ByteLength <= 0)
        {
            blockers.Add($"{kind} artifact for '{bookmark}' / '{output}' is not hash-verified.");
            return;
        }
        if (!TryVerifyArtifact(outputDirectory, artifact, out _, out string reason))
            blockers.Add(reason);
    }

    private static IReadOnlyList<SampleSponzaGiVisualMetricRule> CreateRequiredVisualMetrics()
    {
        SampleSponzaGiVisualMetricRule[] rules =
        [
            new("roi-mean-luminance", "cd/m²", "Measure mean luminance in the projected world-space ROI.", true),
            new("roi-p05-luminance", "cd/m²", "Measure low-tail luminance to expose under-lit transport failures.", true),
            new("roi-p95-luminance", "cd/m²", "Measure high-tail luminance to expose leaks or hot spots.", true),
            new("indirect-delta-vs-direct", "cd/m²", "Compare beauty and direct-only captures to isolate indirect transport.", true),
            new("baseline-structural-similarity", "ratio", "Compare against the approved matching endpoint baseline only.", true),
            new("invalid-pixel-ratio", "ratio", "Reject NaN, Inf, or invalid diagnostic pixels.", false)
        ];
        return Array.AsReadOnly(rules);
    }

    private static string GetTimingClassification(SampleSponzaGiCaptureMode captureMode) => captureMode switch
    {
        SampleSponzaGiCaptureMode.ProductionTiming =>
            "production-timing: only the non-debug beauty endpoint is timing-eligible; debug images are evidence only",
        SampleSponzaGiCaptureMode.DetailedDiagnostics =>
            "detailed-diagnostics: debug views are timing-ineligible and GPU timing collection is disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(captureMode))
    };

    private static bool HasPngSignature(string path)
    {
        ReadOnlySpan<byte> expectedSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        Span<byte> signature = stackalloc byte[8];
        Span<byte> chunkHeader = stackalloc byte[8];
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(expectedSignature))
                return false;

            bool sawHeader = false;
            bool sawImageData = false;
            while (stream.Position < stream.Length)
            {
                if (!TryReadExactly(stream, chunkHeader))
                    return false;

                uint payloadLength =
                    ((uint)chunkHeader[0] << 24) |
                    ((uint)chunkHeader[1] << 16) |
                    ((uint)chunkHeader[2] << 8) |
                    chunkHeader[3];
                ReadOnlySpan<byte> chunkType = chunkHeader[4..];
                long remainingIncludingCrc = stream.Length - stream.Position;
                if (payloadLength > remainingIncludingCrc - sizeof(uint))
                    return false;

                bool isIhdr = chunkType.SequenceEqual("IHDR"u8);
                bool isIdat = chunkType.SequenceEqual("IDAT"u8);
                bool isIend = chunkType.SequenceEqual("IEND"u8);
                if (!sawHeader)
                {
                    if (!isIhdr || payloadLength != 13)
                        return false;
                    sawHeader = true;
                }
                else if (isIhdr)
                {
                    return false;
                }

                if (isIdat)
                    sawImageData = true;

                stream.Seek(checked((long)payloadLength + sizeof(uint)), SeekOrigin.Current);
                if (isIend)
                    return sawHeader && sawImageData && payloadLength == 0 && stream.Position == stream.Length;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = stream.Read(destination[offset..]);
            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }

    private static void AppendBookmark(StringBuilder builder, SampleSponzaGiCameraBookmark bookmark)
    {
        Append(builder, bookmark.Name);
        AppendVector(builder, bookmark.Position);
        Append(builder, bookmark.Yaw);
        Append(builder, bookmark.Pitch);
        Append(builder, bookmark.FieldOfView);
        Append(builder, bookmark.NearPlane);
        Append(builder, bookmark.FarPlane);
    }

    private static void AppendVector(StringBuilder builder, Vector3 value)
    {
        Append(builder, value.X);
        Append(builder, value.Y);
        Append(builder, value.Z);
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value).Append('\n');
    }

    private static void Append(StringBuilder builder, int value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void Append(StringBuilder builder, uint value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void Append(StringBuilder builder, float value)
    {
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void ValidateBookmark(SampleSponzaGiCameraBookmark bookmark, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(bookmark.Name))
            throw new InvalidOperationException($"{parameterName} needs a stable name.");
        if (!IsFinite(bookmark.Position) ||
            !float.IsFinite(bookmark.Yaw) ||
            !float.IsFinite(bookmark.Pitch) ||
            !float.IsFinite(bookmark.FieldOfView) ||
            !float.IsFinite(bookmark.NearPlane) ||
            !float.IsFinite(bookmark.FarPlane) ||
            bookmark.FieldOfView <= 0.0f ||
            bookmark.NearPlane <= 0.0f ||
            bookmark.FarPlane <= bookmark.NearPlane)
        {
            throw new InvalidOperationException($"{parameterName} has an invalid camera transform or projection.");
        }
    }

    private static void ValidateBounds(BoundingBox bounds, string name)
    {
        if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max) ||
            bounds.Min.X >= bounds.Max.X ||
            bounds.Min.Y >= bounds.Max.Y ||
            bounds.Min.Z >= bounds.Max.Z)
        {
            throw new InvalidOperationException($"Bounds for '{name}' must be finite and have positive extents.");
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void ValidateDistinctNames(IEnumerable<string> names, string kind)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !set.Add(name))
                throw new InvalidOperationException($"Every {kind} needs a unique non-empty stable name.");
        }
    }

    private static string ToFileSegment(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c) && i > 0)
                builder.Append('-');
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("Capture artifact paths must be non-empty relative paths.", nameof(path));

        string[] segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new ArgumentException("Capture artifact paths must not escape their capture directory.", nameof(path));

        return string.Join('/', segments);
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        string json = JsonSerializer.Serialize(value, ContractJsonOptions);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static bool NearlyEqual(float left, float right) => MathF.Abs(left - right) <= 0.0001f;

    private static float Lerp(float left, float right, float t) => left + (right - left) * t;
}

/// <summary>
/// Fixed-frame state machine used by the runtime driver. It contains no window,
/// renderer, filesystem, or wall-clock dependencies, making the entire capture
/// order unit-testable.
/// </summary>
public sealed class SampleSponzaGiCaptureSequence
{
    private readonly SampleSponzaGiCaptureContract _contract;
    private SampleSponzaGiCaptureStage _stage = SampleSponzaGiCaptureStage.Warmup;
    private int _stageFrameIndex;

    public SampleSponzaGiCaptureSequence(SampleSponzaGiCaptureContract? contract = null)
    {
        _contract = contract ?? SampleSponzaGiCaptureContract.Default;
    }

    public SampleSponzaGiCaptureContract Contract => _contract;
    public SampleSponzaGiCaptureStage Stage => _stage;
    public bool IsComplete => _stage == SampleSponzaGiCaptureStage.Complete;

    public SampleSponzaGiCaptureInstruction CurrentInstruction
    {
        get
        {
            return _stage switch
            {
                SampleSponzaGiCaptureStage.Warmup => new SampleSponzaGiCaptureInstruction(
                    _stage,
                    _stageFrameIndex,
                    _contract.WarmupFrames,
                    _contract.LowBookmark,
                    null,
                    _contract.LowBookmark.Name,
                    false),
                SampleSponzaGiCaptureStage.CaptureLowBookmark => CaptureBookmarkInstruction(_contract.LowBookmark),
                SampleSponzaGiCaptureStage.VerticalTraversal => new SampleSponzaGiCaptureInstruction(
                    _stage,
                    _stageFrameIndex,
                    _contract.VerticalTraversalFrameCount,
                    _contract.SampleVerticalTraversalFrame(_stageFrameIndex),
                    null,
                    "SponzaPlazaUpperFacadeVerticalTraversal",
                    false),
                SampleSponzaGiCaptureStage.CaptureHighBookmark => CaptureBookmarkInstruction(_contract.HighBookmark),
                _ => new SampleSponzaGiCaptureInstruction(
                    SampleSponzaGiCaptureStage.Complete,
                    0,
                    0,
                    _contract.HighBookmark,
                    null,
                    _contract.HighBookmark.Name,
                    false)
            };
        }
    }

    /// <summary>
    /// Advances exactly once after a rendered frame. Returns true only on the
    /// transition into the terminal state.
    /// </summary>
    public bool AdvanceAfterRenderedFrame()
    {
        if (IsComplete)
            return false;

        _stageFrameIndex++;
        switch (_stage)
        {
            case SampleSponzaGiCaptureStage.Warmup when _stageFrameIndex >= _contract.WarmupFrames:
                MoveTo(SampleSponzaGiCaptureStage.CaptureLowBookmark);
                break;
            case SampleSponzaGiCaptureStage.CaptureLowBookmark when
                _stageFrameIndex >= _contract.Outputs.Count * SampleSponzaGiCaptureContract.FramesPerEndpointOutput:
                MoveTo(SampleSponzaGiCaptureStage.VerticalTraversal);
                break;
            case SampleSponzaGiCaptureStage.VerticalTraversal when _stageFrameIndex >= _contract.VerticalTraversalFrameCount:
                MoveTo(SampleSponzaGiCaptureStage.CaptureHighBookmark);
                break;
            case SampleSponzaGiCaptureStage.CaptureHighBookmark when
                _stageFrameIndex >= _contract.Outputs.Count * SampleSponzaGiCaptureContract.FramesPerEndpointOutput:
                MoveTo(SampleSponzaGiCaptureStage.Complete);
                return true;
        }

        return false;
    }

    private SampleSponzaGiCaptureInstruction CaptureBookmarkInstruction(SampleSponzaGiCameraBookmark bookmark)
    {
        int outputIndex = _stageFrameIndex / SampleSponzaGiCaptureContract.FramesPerEndpointOutput;
        bool captureWindowAfterRenderedFrame =
            _stageFrameIndex % SampleSponzaGiCaptureContract.FramesPerEndpointOutput ==
            SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1;
        return new SampleSponzaGiCaptureInstruction(
            _stage,
            _stageFrameIndex,
            checked(_contract.Outputs.Count * SampleSponzaGiCaptureContract.FramesPerEndpointOutput),
            bookmark,
            _contract.Outputs[outputIndex],
            bookmark.Name,
            captureWindowAfterRenderedFrame);
    }

    private void MoveTo(SampleSponzaGiCaptureStage stage)
    {
        _stage = stage;
        _stageFrameIndex = 0;
    }
}
