using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Assets.Validation;
using Njulf.Core.Camera;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace NjulfHelloGame;

public enum SampleKhronosMaterialGiRenderedGateStage
{
    Warmup,
    AwaitReadback,
    Complete,
    Failed
}

/// <summary>
/// Small, pure frame state machine. It gives the GPU work a fixed warmup and
/// bounds readback latency so a broken renderer cannot leave CI hanging.
/// </summary>
public sealed class SampleKhronosMaterialGiRenderedGateSequence
{
    public const int WarmupFrameCount = 180;
    public const int ReadbackTimeoutFrameCount = 180;

    private bool _captureRequested;
    private int _readbackFrames;

    public SampleKhronosMaterialGiRenderedGateStage Stage { get; private set; } =
        SampleKhronosMaterialGiRenderedGateStage.Warmup;
    public int RenderedFrameCount { get; private set; }
    public int ReadbackFrameCount => _readbackFrames;
    public bool CaptureRequested => _captureRequested;
    public bool ShouldQueueCapture =>
        Stage == SampleKhronosMaterialGiRenderedGateStage.Warmup &&
        !_captureRequested &&
        RenderedFrameCount >= WarmupFrameCount;
    public bool IsComplete => Stage == SampleKhronosMaterialGiRenderedGateStage.Complete;
    public bool IsFailed => Stage == SampleKhronosMaterialGiRenderedGateStage.Failed;
    public string FailureReason { get; private set; } = string.Empty;

    public void MarkCaptureQueued()
    {
        if (!ShouldQueueCapture)
            throw new InvalidOperationException("Linear HDR capture was queued outside the locked gate frame.");
        _captureRequested = true;
        Stage = SampleKhronosMaterialGiRenderedGateStage.AwaitReadback;
    }

    public void AdvanceAfterRenderedFrame(LinearHdrCaptureState readbackState)
    {
        if (IsComplete || IsFailed)
            return;

        RenderedFrameCount = checked(RenderedFrameCount + 1);
        if (!_captureRequested)
        {
            if (RenderedFrameCount > WarmupFrameCount)
                Fail("Linear HDR capture was not queued at the end of the bounded warmup.");
            return;
        }

        _readbackFrames = checked(_readbackFrames + 1);
        switch (readbackState)
        {
            case LinearHdrCaptureState.Completed:
                Stage = SampleKhronosMaterialGiRenderedGateStage.Complete;
                return;
            case LinearHdrCaptureState.Failed:
                Fail("The renderer reported a failed linear HDR readback.");
                return;
            case LinearHdrCaptureState.Queued:
            case LinearHdrCaptureState.Submitted:
                if (_readbackFrames > ReadbackTimeoutFrameCount)
                {
                    Fail(
                        $"Linear HDR readback exceeded the {ReadbackTimeoutFrameCount}-frame timeout.");
                }
                return;
            default:
                Fail($"Linear HDR readback entered unexpected state '{readbackState}'.");
                return;
        }
    }

    public void Fail(string reason)
    {
        if (IsComplete || IsFailed)
            return;
        FailureReason = string.IsNullOrWhiteSpace(reason)
            ? "Khronos material/GI rendered gate failed."
            : reason;
        Stage = SampleKhronosMaterialGiRenderedGateStage.Failed;
    }
}

public sealed record SampleKhronosMaterialGiVerifiedCapture(
    string Path,
    string Format,
    string Encoding,
    string ColorSpace,
    int Width,
    int Height,
    long Bytes,
    long RgbComponentCount,
    string Sha256,
    float MinimumComponent,
    float MaximumComponent,
    double MeanComponent);

public sealed record SampleKhronosMaterialGiValidationEvidence(
    RendererValidationMode Mode,
    int VerboseMessageCount,
    int InfoMessageCount,
    int WarningMessageCount,
    int ErrorMessageCount,
    string FirstWarningMessage,
    string LastWarningMessage,
    string FirstErrorMessage,
    string LastErrorMessage);

public sealed record SampleKhronosMaterialGiCaptureMetadata(
    SampleKhronosMaterialGiVerifiedCapture Artifact,
    int WarmupFrameCount,
    int ReadbackFrameCount,
    float Exposure,
    string QualityPreset,
    string GlobalIlluminationMode,
    string MaterialGiV2Features,
    string AsyncComputeMode,
    SampleSponzaGiCameraBookmark Camera);

public sealed record SampleKhronosMaterialGiEmissionStrengthEvidence(
    float Strength,
    int PixelCount,
    double MaximumRelativeRadianceError,
    double BeautyEmissionCoverageRatio);

public sealed record SampleKhronosMaterialGiSemanticMetrics(
    int UnlitPixelCount,
    double UnlitLightingRelativeRmse,
    int LightingResponsivePbrPixelCount,
    double MeanPbrLightingResponse,
    IReadOnlyList<SampleKhronosMaterialGiEmissionStrengthEvidence> EmissiveStrengths);

public sealed record SampleKhronosMaterialGiSemanticRenderEvidence(
    SampleKhronosMaterialGiVerifiedCapture LightingOffCapture,
    SampleKhronosMaterialGiVerifiedCapture ShadingModelCapture,
    SampleKhronosMaterialGiVerifiedCapture CompiledEmissionCapture,
    SampleKhronosMaterialGiSemanticMetrics Metrics);

/// <summary>
/// Pixel-level semantic gate over lossless production SceneColor captures.
/// The shading-model capture supplies the spatial mask; no RGB radiance
/// threshold is used as a visibility proxy. The official Khronos emissive
/// fixture has strengths 1, 2, 4, 8, and 16 with factor (0.1, 0.5, 0.9).
/// </summary>
public static class SampleKhronosMaterialGiSemanticRenderGate
{
    public const int MinimumPixelsPerSemanticClass = 32;
    public const double MaximumUnlitLightingRelativeRmse = 0.005;
    public const double MaximumEmissiveRelativeError = 0.01;
    public const double MinimumBeautyEmissionCoverageRatio = 0.98;
    public const double MinimumMeanPbrLightingResponse = 0.01;

    private const float MaskTolerance = 0.0025f;
    private const float EmissionDisplayTolerance = 0.0025f;
    private const float LightingResponsiveThreshold = 0.02f;
    private static readonly NumericsVector3 UnlitDebugColor = new(1f, 0.65f, 0.1f);
    private static readonly NumericsVector3 PbrDebugColor = new(0.2f, 0.55f, 1f);
    private static readonly NumericsVector3 EmissiveFactor = new(0.1f, 0.5f, 0.9f);
    private static readonly float[] OfficialEmissiveStrengths = [1f, 2f, 4f, 8f, 16f];

    public static SampleKhronosMaterialGiSemanticMetrics Evaluate(
        LinearFloatImage lit,
        LinearFloatImage lightingOff,
        LinearFloatImage shadingModel,
        LinearFloatImage compiledEmission)
    {
        ArgumentNullException.ThrowIfNull(lit);
        ArgumentNullException.ThrowIfNull(lightingOff);
        ArgumentNullException.ThrowIfNull(shadingModel);
        ArgumentNullException.ThrowIfNull(compiledEmission);
        ValidateImages(lit, lightingOff, shadingModel, compiledEmission);

        int pixelCount = checked(lit.Width * lit.Height);
        int unlitPixels = 0;
        double unlitSquaredDifference = 0.0;
        double unlitSquaredReference = 0.0;
        int responsivePbrPixels = 0;
        double pbrResponseSum = 0.0;

        var emissionPixelCounts = new int[OfficialEmissiveStrengths.Length];
        var emissionRecoveredSums = new NumericsVector3[OfficialEmissiveStrengths.Length];
        var emissionBeautyMatches = new int[OfficialEmissiveStrengths.Length];

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int component = pixel * 3;
            NumericsVector3 litValue = Read(lit.Pixels, component);
            NumericsVector3 offValue = Read(lightingOff.Pixels, component);
            NumericsVector3 modelValue = Read(shadingModel.Pixels, component);
            NumericsVector3 emissionDisplay = Read(compiledEmission.Pixels, component);

            if (Matches(modelValue, UnlitDebugColor, MaskTolerance))
            {
                NumericsVector3 difference = litValue - offValue;
                unlitSquaredDifference += NumericsVector3.Dot(difference, difference);
                unlitSquaredReference += NumericsVector3.Dot(litValue, litValue);
                unlitPixels++;
            }

            if (Matches(modelValue, PbrDebugColor, MaskTolerance) &&
                emissionDisplay.LengthSquared() <= 1e-8f)
            {
                float response = MathF.Abs(Luminance(litValue) - Luminance(offValue));
                if (response >= LightingResponsiveThreshold)
                {
                    responsivePbrPixels++;
                    pbrResponseSum += response;
                }
            }

            for (int strengthIndex = 0;
                 strengthIndex < OfficialEmissiveStrengths.Length;
                 strengthIndex++)
            {
                float strength = OfficialEmissiveStrengths[strengthIndex];
                NumericsVector3 expectedRadiance = EmissiveFactor * strength;
                NumericsVector3 expectedDisplay = expectedRadiance /
                    (NumericsVector3.One + expectedRadiance);
                if (!Matches(emissionDisplay, expectedDisplay, EmissionDisplayTolerance))
                    continue;

                emissionPixelCounts[strengthIndex]++;
                emissionRecoveredSums[strengthIndex] += DecodeEmission(emissionDisplay);
                if (BeautyContainsEmission(litValue, expectedRadiance))
                    emissionBeautyMatches[strengthIndex]++;
                break;
            }
        }

        double unlitRelativeRmse = Math.Sqrt(
            unlitSquaredDifference /
            Math.Max(unlitSquaredReference, unlitPixels * 1e-12));
        double meanPbrResponse = responsivePbrPixels == 0
            ? 0.0
            : pbrResponseSum / responsivePbrPixels;
        var emissionEvidence = new SampleKhronosMaterialGiEmissionStrengthEvidence[
            OfficialEmissiveStrengths.Length];
        var failures = new List<string>();

        if (unlitPixels < MinimumPixelsPerSemanticClass)
        {
            failures.Add(
                $"Shading-model evidence contains only {unlitPixels} Unlit pixels; " +
                $"at least {MinimumPixelsPerSemanticClass} are required.");
        }
        if (!double.IsFinite(unlitRelativeRmse) ||
            unlitRelativeRmse > MaximumUnlitLightingRelativeRmse)
        {
            failures.Add(
                $"Unlit lighting-on/off relative RMSE {unlitRelativeRmse:R} exceeds " +
                $"{MaximumUnlitLightingRelativeRmse:R}.");
        }
        if (responsivePbrPixels < MinimumPixelsPerSemanticClass ||
            !double.IsFinite(meanPbrResponse) ||
            meanPbrResponse < MinimumMeanPbrLightingResponse)
        {
            failures.Add(
                "The lighting-off perturbation did not produce a material PBR response " +
                $"(pixels={responsivePbrPixels}, mean={meanPbrResponse:R}).");
        }

        for (int index = 0; index < OfficialEmissiveStrengths.Length; index++)
        {
            float strength = OfficialEmissiveStrengths[index];
            int count = emissionPixelCounts[index];
            NumericsVector3 expected = EmissiveFactor * strength;
            NumericsVector3 recovered = count == 0
                ? NumericsVector3.Zero
                : emissionRecoveredSums[index] / count;
            double maximumRelativeError = MaxRelativeError(recovered, expected);
            double beautyCoverage = count == 0
                ? 0.0
                : (double)emissionBeautyMatches[index] / count;
            emissionEvidence[index] = new SampleKhronosMaterialGiEmissionStrengthEvidence(
                strength,
                count,
                maximumRelativeError,
                beautyCoverage);

            if (count < MinimumPixelsPerSemanticClass)
            {
                failures.Add(
                    $"Official emissive-strength {strength:R} was rendered in only {count} pixels; " +
                    $"at least {MinimumPixelsPerSemanticClass} are required.");
            }
            if (!double.IsFinite(maximumRelativeError) ||
                maximumRelativeError > MaximumEmissiveRelativeError)
            {
                failures.Add(
                    $"Official emissive-strength {strength:R} relative radiance error " +
                    $"{maximumRelativeError:R} exceeds {MaximumEmissiveRelativeError:R}.");
            }
            if (!double.IsFinite(beautyCoverage) ||
                beautyCoverage < MinimumBeautyEmissionCoverageRatio)
            {
                failures.Add(
                    $"Official emissive-strength {strength:R} contributes its compiled radiance " +
                    $"to only {beautyCoverage:P3} of matched beauty pixels; " +
                    $"{MinimumBeautyEmissionCoverageRatio:P0} is required.");
            }
        }

        if (failures.Count != 0)
        {
            throw new InvalidDataException(
                "Khronos material render semantics failed: " + string.Join(" ", failures));
        }

        return new SampleKhronosMaterialGiSemanticMetrics(
            unlitPixels,
            unlitRelativeRmse,
            responsivePbrPixels,
            meanPbrResponse,
            emissionEvidence);
    }

    private static void ValidateImages(params LinearFloatImage[] images)
    {
        LinearFloatImage first = images[0];
        if (first.Width <= 0 || first.Height <= 0)
            throw new InvalidDataException("Semantic capture dimensions must be positive.");
        int required = checked(first.Width * first.Height * 3);
        foreach (LinearFloatImage image in images)
        {
            if (image.Width != first.Width || image.Height != first.Height ||
                image.Pixels.Length != required)
            {
                throw new InvalidDataException(
                    "Khronos semantic captures must have identical RGB dimensions.");
            }
            if (image.Pixels.Any(static value => !float.IsFinite(value)))
                throw new InvalidDataException("Khronos semantic captures contain non-finite pixels.");
        }
    }

    private static NumericsVector3 Read(float[] pixels, int component) =>
        new(pixels[component], pixels[component + 1], pixels[component + 2]);

    private static bool Matches(
        NumericsVector3 actual,
        NumericsVector3 expected,
        float tolerance) =>
        MathF.Abs(actual.X - expected.X) <= tolerance &&
        MathF.Abs(actual.Y - expected.Y) <= tolerance &&
        MathF.Abs(actual.Z - expected.Z) <= tolerance;

    private static NumericsVector3 DecodeEmission(NumericsVector3 display) =>
        new(
            display.X / MathF.Max(1f - display.X, 1e-6f),
            display.Y / MathF.Max(1f - display.Y, 1e-6f),
            display.Z / MathF.Max(1f - display.Z, 1e-6f));

    private static bool BeautyContainsEmission(
        NumericsVector3 beauty,
        NumericsVector3 expected) =>
        beauty.X + 0.002f >= expected.X * 0.995f &&
        beauty.Y + 0.002f >= expected.Y * 0.995f &&
        beauty.Z + 0.002f >= expected.Z * 0.995f;

    private static double MaxRelativeError(
        NumericsVector3 actual,
        NumericsVector3 expected) =>
        Math.Max(
            Math.Abs(actual.X - expected.X) / expected.X,
            Math.Max(
                Math.Abs(actual.Y - expected.Y) / expected.Y,
                Math.Abs(actual.Z - expected.Z) / expected.Z));

    private static float Luminance(NumericsVector3 value) =>
        value.X * 0.2126f + value.Y * 0.7152f + value.Z * 0.0722f;
}

public sealed record SampleKhronosMaterialGiRenderedGateReport
{
    public const int CurrentSchemaVersion = 3;
    public const string CurrentSchema = "khronos-material-gi-rendered/v3";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Schema { get; init; } = CurrentSchema;
    public string Status { get; init; } = "InProgress";
    [JsonPropertyName("producerIdentity")]
    public MaterialGiProducerIdentity? ProducerIdentity { get; init; }
    public string Repository { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public string SemanticGateReportPath { get; init; } = string.Empty;
    public string SemanticGateReportSha256 { get; init; } = string.Empty;
    public string CookedRoot { get; init; } = string.Empty;
    public string PackageHashAlgorithm { get; init; } = "sha256-framed-model-packages-v1";
    public string PackageSha256 { get; init; } = string.Empty;
    public string CaptureSha256 { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public int AssetCount { get; init; }
    public int SemanticMaterialCount { get; init; }
    public int SemanticSubMeshCount { get; init; }
    public int RuntimeMaterialCount { get; init; }
    public int RuntimeSubMeshCount { get; init; }
    public int RuntimeUnlitMaterialCount { get; init; }
    public int RuntimeUnlitRenderObjectCount { get; init; }
    public int RenderObjectCount { get; init; }
    public int RenderedFrameCount { get; init; }
    public string GpuDevice { get; init; } = string.Empty;
    public string GpuDriver { get; init; } = string.Empty;
    public bool StrictCookedPolicy { get; init; }
    public bool SourceFallbackEnabled { get; init; }
    public SampleKhronosMaterialGiValidationEvidence Validation { get; init; } =
        new(
            RendererValidationMode.Off,
            0,
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    public SampleKhronosMaterialGiCaptureMetadata? Capture { get; init; }
    public SampleKhronosMaterialGiSemanticRenderEvidence? SemanticRender { get; init; }
    public IReadOnlyList<SampleKhronosMaterialGiRenderedAssetEvidence> Assets { get; init; } =
        Array.Empty<SampleKhronosMaterialGiRenderedAssetEvidence>();
    public string? Failure { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

public sealed record SampleKhronosMaterialGiCompletionSnapshot(
    RendererValidationMode ValidationMode,
    int ValidationWarningCount,
    int ValidationErrorCount,
    bool SawDrawSubmission,
    int RenderedFrameCount,
    MaterialGiV2Feature DiagnosticsMaterialGiV2Features,
    MaterialGiV2Feature SettingsMaterialGiV2Features,
    int AssetCount,
    int RenderObjectCount,
    int ExpectedUnlitRenderObjectCount,
    int RuntimeUnlitRenderObjectCount,
    string GpuDevice,
    string GpuDriver,
    int CaptureWidth,
    int CaptureHeight,
    float CaptureMaximumComponent);

public static class SampleKhronosMaterialGiCompletionGate
{
    public static IReadOnlyList<string> Evaluate(
        SampleKhronosMaterialGiCompletionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var failures = new List<string>();
        if (snapshot.ValidationMode == RendererValidationMode.Off)
            failures.Add("Vulkan validation was not active.");
        if (snapshot.ValidationWarningCount != 0 || snapshot.ValidationErrorCount != 0)
        {
            failures.Add(
                $"Vulkan validation emitted {snapshot.ValidationWarningCount} warning(s) and " +
                $"{snapshot.ValidationErrorCount} error(s).");
        }
        if (!snapshot.SawDrawSubmission)
            failures.Add("No non-empty draw submission was observed.");
        if (snapshot.RenderedFrameCount <=
            SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount)
        {
            failures.Add("The rendered frame count did not exceed the locked warmup.");
        }
        if (snapshot.DiagnosticsMaterialGiV2Features != MaterialGiV2Feature.All ||
            snapshot.SettingsMaterialGiV2Features != MaterialGiV2Feature.All)
        {
            failures.Add(
                "The complete Material/GI V2 feature set was not active in both settings and diagnostics.");
        }
        if (snapshot.AssetCount <= 0 || snapshot.RenderObjectCount <= 0)
            failures.Add("The authenticated scene contained no renderable Khronos content.");
        if (snapshot.ExpectedUnlitRenderObjectCount <= 0 ||
            snapshot.RuntimeUnlitRenderObjectCount < snapshot.ExpectedUnlitRenderObjectCount)
        {
            failures.Add(
                $"Runtime Unlit evidence contains {snapshot.RuntimeUnlitRenderObjectCount} object(s); " +
                $"{snapshot.ExpectedUnlitRenderObjectCount} are required.");
        }
        if (string.IsNullOrWhiteSpace(snapshot.GpuDevice) ||
            snapshot.GpuDevice.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.GpuDriver) ||
            snapshot.GpuDriver.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("GPU device or driver provenance is unavailable.");
        }
        if (snapshot.CaptureWidth != SampleKhronosMaterialGiRenderedGateRunner.LockedWidth ||
            snapshot.CaptureHeight != SampleKhronosMaterialGiRenderedGateRunner.LockedHeight)
        {
            failures.Add(
                $"Linear HDR capture extent is {snapshot.CaptureWidth}x{snapshot.CaptureHeight}; " +
                $"{SampleKhronosMaterialGiRenderedGateRunner.LockedWidth}x" +
                $"{SampleKhronosMaterialGiRenderedGateRunner.LockedHeight} is required.");
        }
        if (!float.IsFinite(snapshot.CaptureMaximumComponent) ||
            snapshot.CaptureMaximumComponent <= 1e-6f)
        {
            failures.Add("Linear HDR capture contains no positive rendered signal.");
        }
        return failures;
    }
}

public static class SampleKhronosMaterialGiRenderedGateReportPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SampleKhronosMaterialGiVerifiedCapture VerifyCapture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            fullPath,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            "Khronos rendered-gate linear HDR capture");
        byte[] encoded = evidence.Bytes;
        LinearFloatImage image = PfmLinearImageCodec.Decode(encoded);
        if (image.Pixels.Length == 0)
            throw new InvalidDataException($"Linear HDR capture '{fullPath}' contains no pixels.");

        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        double sum = 0.0;
        foreach (float component in image.Pixels)
        {
            if (!float.IsFinite(component))
                throw new InvalidDataException($"Linear HDR capture '{fullPath}' contains non-finite data.");
            minimum = MathF.Min(minimum, component);
            maximum = MathF.Max(maximum, component);
            sum += component;
        }
        if (!double.IsFinite(sum))
            throw new InvalidDataException($"Linear HDR capture '{fullPath}' component sum overflowed.");

        return new SampleKhronosMaterialGiVerifiedCapture(
            fullPath,
            "PFM",
            "RGB32F little-endian, bottom-up rows, lossless",
            "linear-scene-referred-rgb",
            image.Width,
            image.Height,
            encoded.LongLength,
            image.Pixels.LongLength,
            evidence.Sha256,
            minimum,
            maximum,
            sum / image.Pixels.LongLength);
    }

    public static void WriteInProgress(SampleKhronosMaterialGiRenderedSceneBuild scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        WriteAtomic(
            scene.Options.ReportPath,
            CreateReport(
                scene,
                "InProgress",
                completedAtUtc: null,
                diagnostics: RendererDiagnostics.Empty,
                renderedFrameCount: 0,
                capture: null,
                captureReadbackFrameCount: 0,
                semanticRender: null,
                producerIdentity: null,
                failure: null));
    }

    public static void WriteHostStarting(
        SampleKhronosMaterialGiRenderedGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WriteAtomic(
            options.ReportPath,
            CreatePreflightReport(
                options,
                status: "InProgress",
                failure: null));
    }

    public static void WritePassed(
        SampleKhronosMaterialGiRenderedSceneBuild scene,
        RendererDiagnostics diagnostics,
        string settingsFingerprint,
        int renderedFrameCount,
        SampleKhronosMaterialGiVerifiedCapture capture,
        int captureReadbackFrameCount,
        SampleKhronosMaterialGiSemanticRenderEvidence semanticRender)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(semanticRender);
        MaterialGiProducerIdentity producerIdentity =
            SampleMaterialGiProducerIdentityFactory.Create(
                diagnostics,
                settingsFingerprint);
        WriteAtomic(
            scene.Options.ReportPath,
            CreateReport(
                scene,
                "Passed",
                DateTimeOffset.UtcNow,
                diagnostics,
                renderedFrameCount,
                capture,
                captureReadbackFrameCount,
                semanticRender,
                producerIdentity,
                null));
    }

    public static void WriteFailed(
        SampleKhronosMaterialGiRenderedGateOptions options,
        string failure,
        SampleKhronosMaterialGiRenderedSceneBuild? scene = null,
        RendererDiagnostics? diagnostics = null,
        int renderedFrameCount = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        SampleKhronosMaterialGiRenderedGateReport report = scene is null
            ? CreatePreflightReport(options, "Failed", failure)
            : CreateReport(
                scene,
                "Failed",
                DateTimeOffset.UtcNow,
                diagnostics ?? RendererDiagnostics.Empty,
                renderedFrameCount,
                capture: null,
                captureReadbackFrameCount: 0,
                semanticRender: null,
                producerIdentity: null,
                failure);
        WriteAtomic(options.ReportPath, report);
    }

    public static bool TryFinalizeInProgress(
        SampleKhronosMaterialGiRenderedGateOptions options,
        string failure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        try
        {
            SampleKhronosMaterialGiRenderedGateReport? existing = null;
            if (File.Exists(options.ReportPath))
            {
                try
                {
                    existing = ReadReport(options.ReportPath);
                }
                catch (Exception exception)
                {
                    failure +=
                        $" The prior in-progress report was unreadable: {DescribeException(exception)}";
                }
            }

            if (existing is not null &&
                string.Equals(existing.Status, "Failed", StringComparison.Ordinal))
            {
                return true;
            }

            SampleKhronosMaterialGiRenderedGateReport failed = existing is null
                ? CreatePreflightReport(options, "Failed", failure)
                : existing with
                {
                    Status = "Failed",
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Failure = failure,
                    Failures = [failure]
                };
            WriteAtomic(options.ReportPath, failed);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Could not finalize Khronos rendered-gate report '{options.ReportPath}': " +
                $"{DescribeException(exception)}");
            return false;
        }
    }

    public static string? TryReadStatus(
        SampleKhronosMaterialGiRenderedGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            if (!File.Exists(options.ReportPath))
                return null;
            SampleKhronosMaterialGiRenderedGateReport report =
                ReadReport(options.ReportPath);
            return IsValidStatusReport(report) ? report.Status : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryWriteFailed(
        SampleKhronosMaterialGiRenderedGateOptions options,
        string failure,
        SampleKhronosMaterialGiRenderedSceneBuild? scene = null,
        RendererDiagnostics? diagnostics = null,
        int renderedFrameCount = 0)
    {
        try
        {
            WriteFailed(options, failure, scene, diagnostics, renderedFrameCount);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Could not publish Khronos rendered-gate failure report '{options.ReportPath}': " +
                $"{DescribeException(exception)}");
            return false;
        }
    }

    public static void WriteAtomic(
        string path,
        SampleKhronosMaterialGiRenderedGateReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != SampleKhronosMaterialGiRenderedGateReport.CurrentSchemaVersion ||
            !string.Equals(
                report.Schema,
                SampleKhronosMaterialGiRenderedGateReport.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Khronos rendered-gate report has an unsupported schema.");
        }

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new IOException($"Could not resolve report directory for '{fullPath}'.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            if (json.LongLength > SampleEvidenceFileIo.MaximumJsonBytes)
            {
                throw new InvalidDataException(
                    $"Khronos rendered-gate report contains {json.LongLength} bytes; " +
                    $"the bounded limit is {SampleEvidenceFileIo.MaximumJsonBytes} bytes.");
            }
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       32 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(json);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            SampleEvidenceFileContent published = SampleEvidenceFileIo.Read(
                fullPath,
                SampleEvidenceFileIo.MaximumJsonBytes,
                "Published Khronos rendered-gate report");
            if (!published.Bytes.AsSpan().SequenceEqual(json))
            {
                throw new IOException(
                    $"Published Khronos rendered-gate report '{fullPath}' differs from the committed payload.");
            }
            SampleKhronosMaterialGiRenderedGateReport verified =
                DeserializeReport(published.Bytes, fullPath);
            if (verified.SchemaVersion != report.SchemaVersion ||
                !string.Equals(verified.Status, report.Status, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Published Khronos rendered-gate report '{fullPath}' failed verification.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static SampleKhronosMaterialGiRenderedGateReport CreateReport(
        SampleKhronosMaterialGiRenderedSceneBuild scene,
        string status,
        DateTimeOffset? completedAtUtc,
        RendererDiagnostics diagnostics,
        int renderedFrameCount,
        SampleKhronosMaterialGiVerifiedCapture? capture,
        int captureReadbackFrameCount,
        SampleKhronosMaterialGiSemanticRenderEvidence? semanticRender,
        MaterialGiProducerIdentity? producerIdentity,
        string? failure)
    {
        KhronosMaterialGiManifest manifest = scene.AuthenticatedGate.Manifest;
        int semanticMaterialCount =
            scene.AuthenticatedGate.GateReport.Entries.Sum(static entry => entry.MaterialCount);
        int semanticSubMeshCount =
            scene.AuthenticatedGate.GateReport.Entries.Sum(static entry => entry.SubMeshCount);
        var validation = new SampleKhronosMaterialGiValidationEvidence(
            diagnostics.ValidationMode,
            diagnostics.ValidationVerboseMessageCount,
            diagnostics.ValidationInfoMessageCount,
            diagnostics.ValidationWarningMessageCount,
            diagnostics.ValidationErrorMessageCount,
            diagnostics.ValidationFirstWarningMessage,
            diagnostics.ValidationLastWarningMessage,
            diagnostics.ValidationFirstErrorMessage,
            diagnostics.ValidationLastErrorMessage);
        SampleKhronosMaterialGiCaptureMetadata? metadata = capture is null
            ? null
            : new SampleKhronosMaterialGiCaptureMetadata(
                capture,
                SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount,
                Math.Max(0, captureReadbackFrameCount),
                diagnostics.Exposure,
                diagnostics.ActiveQualityPreset.ToString(),
                diagnostics.GlobalIlluminationMode.ToString(),
                diagnostics.MaterialGiV2ActiveFeatures.ToString(),
                diagnostics.AsyncComputeEffectiveMode.ToString(),
                SampleKhronosMaterialGiRenderedGateRunner.Camera);

        return new SampleKhronosMaterialGiRenderedGateReport
        {
            Status = status,
            ProducerIdentity = producerIdentity,
            Repository = manifest.Repository,
            Commit = manifest.Commit,
            ManifestPath = scene.AuthenticatedGate.ManifestPath,
            ManifestSha256 = scene.AuthenticatedGate.ManifestSha256,
            SemanticGateReportPath = scene.AuthenticatedGate.GateReportPath,
            SemanticGateReportSha256 = scene.AuthenticatedGate.GateReportSha256,
            CookedRoot = scene.Options.CookedRoot,
            PackageSha256 = scene.PackageSha256,
            CaptureSha256 = capture?.Sha256 ?? string.Empty,
            StartedAtUtc = scene.StartedAtUtc,
            CompletedAtUtc = completedAtUtc,
            AssetCount = scene.Assets.Count,
            SemanticMaterialCount = semanticMaterialCount,
            SemanticSubMeshCount = semanticSubMeshCount,
            RuntimeMaterialCount = scene.RuntimeMaterialCount,
            RuntimeSubMeshCount = scene.RuntimeSubMeshCount,
            RuntimeUnlitMaterialCount = scene.RuntimeUnlitMaterialCount,
            RuntimeUnlitRenderObjectCount = scene.RuntimeUnlitRenderObjectCount,
            RenderObjectCount = scene.RenderObjectCount,
            RenderedFrameCount = renderedFrameCount,
            GpuDevice = diagnostics.CaptureGpuDeviceName,
            GpuDriver = diagnostics.CaptureGpuDriverVersion,
            StrictCookedPolicy = Njulf.Assets.Cooked.CookedRuntimePolicy.Strict,
            SourceFallbackEnabled = Njulf.Assets.Cooked.CookedRuntimePolicy.AllowSourceFallback,
            Validation = validation,
            Capture = metadata,
            SemanticRender = semanticRender,
            Assets = scene.Assets,
            Failure = failure,
            Failures = failure is null ? Array.Empty<string>() : [failure]
        };
    }

    private static SampleKhronosMaterialGiRenderedGateReport CreatePreflightReport(
        SampleKhronosMaterialGiRenderedGateOptions options,
        string status,
        string? failure)
    {
        string repository = string.Empty;
        string commit = string.Empty;
        string manifestSha256 = TryComputeFileSha256(options.ManifestPath);
        int assetCount = 0;
        try
        {
            KhronosMaterialGiManifest manifest =
                KhronosMaterialGiConformance.LoadManifest(options.ManifestPath);
            repository = manifest.Repository;
            commit = manifest.Commit;
            assetCount = manifest.Assets.Count;
        }
        catch
        {
            // The failure report must remain publishable for a missing or
            // malformed manifest. Unauthenticated identity is never copied.
        }

        return new SampleKhronosMaterialGiRenderedGateReport
        {
            Status = status,
            Repository = repository,
            Commit = commit,
            ManifestPath = options.ManifestPath,
            ManifestSha256 = manifestSha256,
            SemanticGateReportPath = options.GateReportPath,
            SemanticGateReportSha256 = TryComputeFileSha256(options.GateReportPath),
            CookedRoot = options.CookedRoot,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = string.Equals(status, "InProgress", StringComparison.Ordinal)
                ? null
                : DateTimeOffset.UtcNow,
            AssetCount = assetCount,
            StrictCookedPolicy = Njulf.Assets.Cooked.CookedRuntimePolicy.Strict,
            SourceFallbackEnabled = Njulf.Assets.Cooked.CookedRuntimePolicy.AllowSourceFallback,
            Failure = failure,
            Failures = failure is null ? Array.Empty<string>() : [failure]
        };
    }

    private static string TryComputeFileSha256(string path)
    {
        try
        {
            return SampleEvidenceFileIo.Read(
                    path,
                    SampleEvidenceFileIo.MaximumJsonBytes,
                    "Khronos gate input")
                .Sha256;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static SampleKhronosMaterialGiRenderedGateReport ReadReport(
        string path)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Khronos rendered-gate report");
        return DeserializeReport(evidence.Bytes, evidence.Path);
    }

    private static SampleKhronosMaterialGiRenderedGateReport DeserializeReport(
        ReadOnlySpan<byte> bytes,
        string path)
    {
        SampleEvidenceFileIo.ValidateStrictJson(
            bytes,
            JsonOptions.MaxDepth,
            "Khronos rendered-gate report");
        return JsonSerializer.Deserialize<SampleKhronosMaterialGiRenderedGateReport>(
                   bytes,
                   JsonOptions) ??
               throw new InvalidDataException(
                   $"Khronos rendered-gate report '{path}' deserialized to null.");
    }

    private static bool IsValidStatusReport(
        SampleKhronosMaterialGiRenderedGateReport report)
    {
        if (report.SchemaVersion !=
                SampleKhronosMaterialGiRenderedGateReport.CurrentSchemaVersion ||
            !string.Equals(
                report.Schema,
                SampleKhronosMaterialGiRenderedGateReport.CurrentSchema,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(report.Status, "InProgress", StringComparison.Ordinal))
            return !report.CompletedAtUtc.HasValue;
        if (string.Equals(report.Status, "Failed", StringComparison.Ordinal))
        {
            return report.CompletedAtUtc.HasValue &&
                   !string.IsNullOrWhiteSpace(report.Failure) &&
                   report.Failures is { Count: > 0 };
        }
        if (!string.Equals(report.Status, "Passed", StringComparison.Ordinal))
            return false;

        SampleKhronosMaterialGiVerifiedCapture? capture =
            report.Capture?.Artifact;
        return report.CompletedAtUtc.HasValue &&
               string.IsNullOrEmpty(report.Failure) &&
               report.Failures is { Count: 0 } &&
               report.StrictCookedPolicy &&
               !report.SourceFallbackEnabled &&
               report.RenderedFrameCount >
                   SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount &&
               report.AssetCount > 0 &&
               report.RenderObjectCount > 0 &&
               report.Validation.Mode != RendererValidationMode.Off &&
               report.Validation.WarningMessageCount == 0 &&
               report.Validation.ErrorMessageCount == 0 &&
               capture is not null &&
               capture.Width == SampleKhronosMaterialGiRenderedGateRunner.LockedWidth &&
               capture.Height == SampleKhronosMaterialGiRenderedGateRunner.LockedHeight &&
               capture.Bytes > 0 &&
               capture.RgbComponentCount ==
                   (long)capture.Width * capture.Height * 3L &&
               float.IsFinite(capture.MaximumComponent) &&
               capture.MaximumComponent > 1e-6f &&
               IsSha256(report.ManifestSha256) &&
               IsSha256(report.SemanticGateReportSha256) &&
               IsSha256(report.PackageSha256) &&
               IsSha256(report.CaptureSha256) &&
               string.Equals(
                   report.CaptureSha256,
                   capture.Sha256,
                   StringComparison.Ordinal) &&
               report.SemanticRender is not null &&
               !string.IsNullOrWhiteSpace(report.GpuDevice) &&
               !report.GpuDevice.Contains("unknown", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(report.GpuDriver) &&
               !report.GpuDriver.Contains("unknown", StringComparison.OrdinalIgnoreCase) &&
               IsValidProducerIdentity(report);
    }

    private static bool IsValidProducerIdentity(
        SampleKhronosMaterialGiRenderedGateReport report)
    {
        MaterialGiProducerIdentity? identity = report.ProducerIdentity;
        if (identity is null ||
            !string.Equals(
                identity.Schema,
                MaterialGiProducerIdentity.CurrentSchema,
                StringComparison.Ordinal) ||
            identity.BuildCommit is not { Length: 40 } ||
            identity.BuildCommit.Any(static character =>
                character is not (
                    >= '0' and <= '9' or
                    >= 'a' and <= 'f')) ||
            !IsSha256(identity.ShaderFingerprint) ||
            !IsSha256(identity.SettingsFingerprint) ||
            identity.SourceSettingsFingerprints is not { Length: 1 } ||
            !string.Equals(
                identity.SourceSettingsFingerprints[0],
                identity.SettingsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.GpuName,
                report.GpuDevice,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.DriverVersion,
                report.GpuDriver,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.QualityTier,
                string.Empty,
                StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string DescribeException(Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return $"{exception.GetType().Name}: {message}";
    }
}

public sealed class SampleKhronosMaterialGiRenderedGateRunner
{
    private enum SemanticCaptureStage
    {
        PrimaryBeauty,
        LightingOffWarmup,
        LightingOffReadback,
        ShadingModelWarmup,
        ShadingModelReadback,
        CompiledEmissionWarmup,
        CompiledEmissionReadback,
        Complete
    }

    public const int LockedWidth = 1600;
    public const int LockedHeight = 900;
    public const float LockedExposure = 1f;
    public const int LightingOffWarmupFrameCount = 30;
    public const int DiagnosticWarmupFrameCount = 2;
    public const int SemanticReadbackTimeoutFrameCount = 180;

    public static SampleSponzaGiCameraBookmark Camera { get; } = new(
        "OfficialKhronosMaterialGiOverview",
        new CoreVector3(0f, 1.75f, 8.4f),
        0f,
        -0.105f,
        MathF.PI / 3f,
        0.05f,
        100f);

    private readonly SampleKhronosMaterialGiRenderedSceneBuild _scene;
    private readonly VulkanRenderer _renderer;
    private readonly FirstPersonCamera _camera;
    private readonly LightManager _lightManager;
    private readonly Func<(int Width, int Height)> _getWindowSize;
    private readonly Action _exit;
    private readonly SampleKhronosMaterialGiRenderedGateSequence _sequence = new();
    private readonly string _lightingOffCapturePath;
    private readonly string _shadingModelCapturePath;
    private readonly string _compiledEmissionCapturePath;
    private RendererDiagnostics _lastSubmittedDiagnostics = RendererDiagnostics.Empty;
    private SemanticCaptureStage _semanticStage = SemanticCaptureStage.PrimaryBeauty;
    private SampleKhronosMaterialGiVerifiedCapture? _primaryCapture;
    private SampleKhronosMaterialGiVerifiedCapture? _lightingOffCapture;
    private SampleKhronosMaterialGiVerifiedCapture? _shadingModelCapture;
    private SampleKhronosMaterialGiVerifiedCapture? _compiledEmissionCapture;
    private int _semanticWarmupFrames;
    private int _semanticReadbackFrames;
    private int _totalRenderedFrameCount;
    private bool _sawDrawSubmission;
    private bool _terminalReportWritten;
    private bool _exitRequested;

    public SampleKhronosMaterialGiRenderedGateRunner(
        SampleKhronosMaterialGiRenderedSceneBuild scene,
        VulkanRenderer renderer,
        FirstPersonCamera camera,
        LightManager lightManager,
        Func<(int Width, int Height)> getWindowSize,
        Action exit)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
        _getWindowSize = getWindowSize ?? throw new ArgumentNullException(nameof(getWindowSize));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        string? captureDirectory = Path.GetDirectoryName(_scene.Options.CapturePath);
        if (string.IsNullOrWhiteSpace(captureDirectory))
            throw new IOException($"Could not resolve capture directory for '{_scene.Options.CapturePath}'.");
        Directory.CreateDirectory(captureDirectory);
        _lightingOffCapturePath = CreateCompanionCapturePath(
            _scene.Options.CapturePath,
            "lighting-off");
        _shadingModelCapturePath = CreateCompanionCapturePath(
            _scene.Options.CapturePath,
            "shading-model");
        _compiledEmissionCapturePath = CreateCompanionCapturePath(
            _scene.Options.CapturePath,
            "compiled-emission");
        ValidateCompanionPaths();

        ApplyLockedCamera(_camera);
        ApplyLockedSettings(_renderer.Settings);
        ConfigureLockedLighting(_lightManager);
        _renderer.CaptureScenario = SampleKhronosMaterialGiRenderedGateReport.CurrentSchema;
        SampleKhronosMaterialGiRenderedGateReportPublisher.WriteInProgress(_scene);
    }

    public SampleKhronosMaterialGiRenderedGateSequence Sequence => _sequence;

    public static string CreateCompanionCapturePath(string primaryPath, string semanticName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticName);
        string fullPath = Path.GetFullPath(primaryPath);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new IOException($"Could not resolve capture directory for '{fullPath}'.");
        string fileName = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, $"{fileName}.{semanticName}.pfm");
    }

    public void PrepareFrame()
    {
        if (_terminalReportWritten || _sequence.IsFailed ||
            _semanticStage == SemanticCaptureStage.Complete)
        {
            return;
        }

        try
        {
            _lastSubmittedDiagnostics = _renderer.LastDiagnostics;
            ObserveDrawSubmission(_lastSubmittedDiagnostics);
            if (_lastSubmittedDiagnostics.ValidationWarningMessageCount != 0 ||
                _lastSubmittedDiagnostics.ValidationErrorMessageCount != 0)
            {
                string firstMessage =
                    _lastSubmittedDiagnostics.ValidationWarningMessageCount != 0
                        ? _lastSubmittedDiagnostics.ValidationFirstWarningMessage
                        : _lastSubmittedDiagnostics.ValidationFirstErrorMessage;
                string context = string.IsNullOrWhiteSpace(firstMessage)
                    ? string.Empty
                    : $" First message: {firstMessage}";
                throw new InvalidOperationException(
                    $"Vulkan validation emitted " +
                    $"{_lastSubmittedDiagnostics.ValidationWarningMessageCount} warning(s) and " +
                    $"{_lastSubmittedDiagnostics.ValidationErrorMessageCount} error(s)." +
                    context);
            }
            ValidateWindowSize();
            ApplyLockedCamera(_camera);
            ApplyLockedSettings(_renderer.Settings);
            ApplySemanticDebugView();

            if (_semanticStage == SemanticCaptureStage.PrimaryBeauty &&
                _sequence.ShouldQueueCapture)
            {
                QueueCapture(_scene.Options.CapturePath);
                _sequence.MarkCaptureQueued();
            }
            else if (IsSemanticWarmupComplete())
            {
                QueueCurrentSemanticCapture();
            }
        }
        catch (Exception exception)
        {
            Abort($"Khronos rendered-gate frame preparation failed: {DescribeException(exception)}");
        }
    }

    public void OnFrameRendered()
    {
        if (_terminalReportWritten || _sequence.IsFailed ||
            _semanticStage == SemanticCaptureStage.Complete)
        {
            return;
        }

        try
        {
            _totalRenderedFrameCount = checked(_totalRenderedFrameCount + 1);
            if (_semanticStage == SemanticCaptureStage.PrimaryBeauty)
            {
                AdvancePrimaryCapture();
                return;
            }

            if (_semanticStage is SemanticCaptureStage.LightingOffWarmup or
                SemanticCaptureStage.ShadingModelWarmup or
                SemanticCaptureStage.CompiledEmissionWarmup)
            {
                _semanticWarmupFrames = checked(_semanticWarmupFrames + 1);
                return;
            }

            AdvanceSemanticReadback();
        }
        catch (Exception exception)
        {
            Abort($"Khronos rendered-gate completion failed: {DescribeException(exception)}");
        }
    }

    public void CancelIfIncomplete(string reason)
    {
        if (_terminalReportWritten)
            return;
        Abort(reason);
    }

    public static void ApplyLockedSettings(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SampleMaterialGiCaptureRunner.ApplyLockedSettings(
            settings,
            AsyncComputeMode.Disabled);
        settings.Exposure = LockedExposure;
        settings.GlobalIllumination.EnableMaterialGiV2ForConformance(
            MaterialGiV2Feature.All);
        settings.Debug.Enabled = true;
        settings.Debug.AllowGpuTiming = true;
        settings.Debug.AllowScreenshots = true;
    }

    public static void ApplyLockedCamera(FirstPersonCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.Position = Camera.Position;
        camera.Yaw = Camera.Yaw;
        camera.Pitch = Camera.Pitch;
        camera.FieldOfView = Camera.FieldOfView;
        camera.NearPlane = Camera.NearPlane;
        camera.FarPlane = Camera.FarPlane;
        camera.AspectRatio = (float)LockedWidth / LockedHeight;
        camera.Update();
    }

    public static void ConfigureLockedLighting(LightManager lightManager)
    {
        ArgumentNullException.ThrowIfNull(lightManager);
        lightManager.ClearLights();
        lightManager.AddLight(new Light
        {
            Type = LightType.Directional,
            Direction = NumericsVector3.Normalize(new NumericsVector3(-0.44f, -0.82f, -0.36f)),
            Color = NumericsVector3.One,
            Intensity = 3.5f,
            CastsShadows = true,
            ShadowStrength = 1f,
            ShadowPriority = 100
        });
    }

    private void AdvancePrimaryCapture()
    {
        LinearHdrCaptureResult? result = _sequence.CaptureRequested
            ? _renderer.GetLinearHdrCaptureResult(_scene.Options.CapturePath)
            : null;
        _sequence.AdvanceAfterRenderedFrame(result?.State ?? LinearHdrCaptureState.Unknown);
        if (_sequence.IsFailed)
        {
            string rendererFailure = result is { State: LinearHdrCaptureState.Failed } &&
                                     !string.IsNullOrWhiteSpace(result.Error)
                ? $" Renderer: {result.Error}"
                : string.Empty;
            Abort(_sequence.FailureReason + rendererFailure);
            return;
        }
        if (!_sequence.IsComplete)
            return;

        _primaryCapture = VerifyLockedCapture(_scene.Options.CapturePath);
        BeginSemanticStage(SemanticCaptureStage.LightingOffWarmup);
    }

    private void AdvanceSemanticReadback()
    {
        string path = CurrentSemanticCapturePath();
        LinearHdrCaptureResult result = _renderer.GetLinearHdrCaptureResult(path);
        _semanticReadbackFrames = checked(_semanticReadbackFrames + 1);
        switch (result.State)
        {
            case LinearHdrCaptureState.Completed:
                CompleteCurrentSemanticCapture(VerifyLockedCapture(path));
                return;
            case LinearHdrCaptureState.Failed:
                throw new InvalidOperationException(
                    $"Semantic capture '{Path.GetFileName(path)}' failed: {result.Error}");
            case LinearHdrCaptureState.Queued:
            case LinearHdrCaptureState.Submitted:
                if (_semanticReadbackFrames > SemanticReadbackTimeoutFrameCount)
                {
                    throw new TimeoutException(
                        $"Semantic capture '{Path.GetFileName(path)}' exceeded the " +
                        $"{SemanticReadbackTimeoutFrameCount}-frame timeout.");
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Semantic capture '{Path.GetFileName(path)}' entered unexpected state " +
                    $"'{result.State}'.");
        }
    }

    private void CompleteCurrentSemanticCapture(
        SampleKhronosMaterialGiVerifiedCapture capture)
    {
        switch (_semanticStage)
        {
            case SemanticCaptureStage.LightingOffReadback:
                _lightingOffCapture = capture;
                BeginSemanticStage(SemanticCaptureStage.ShadingModelWarmup);
                return;
            case SemanticCaptureStage.ShadingModelReadback:
                _shadingModelCapture = capture;
                BeginSemanticStage(SemanticCaptureStage.CompiledEmissionWarmup);
                return;
            case SemanticCaptureStage.CompiledEmissionReadback:
                _compiledEmissionCapture = capture;
                CompleteGate();
                return;
            default:
                throw new InvalidOperationException(
                    $"Semantic capture completed during invalid stage '{_semanticStage}'.");
        }
    }

    private void CompleteGate()
    {
        SampleKhronosMaterialGiVerifiedCapture primary = _primaryCapture ??
            throw new InvalidOperationException("Primary beauty evidence is unavailable.");
        SampleKhronosMaterialGiVerifiedCapture lightingOff = _lightingOffCapture ??
            throw new InvalidOperationException("Lighting-off evidence is unavailable.");
        SampleKhronosMaterialGiVerifiedCapture shadingModel = _shadingModelCapture ??
            throw new InvalidOperationException("Shading-model evidence is unavailable.");
        SampleKhronosMaterialGiVerifiedCapture compiledEmission = _compiledEmissionCapture ??
            throw new InvalidOperationException("Compiled-emission evidence is unavailable.");

        SampleKhronosMaterialGiSemanticMetrics metrics =
            SampleKhronosMaterialGiSemanticRenderGate.Evaluate(
                DecodeCapture(primary),
                DecodeCapture(lightingOff),
                DecodeCapture(shadingModel),
                DecodeCapture(compiledEmission));
        var semanticEvidence = new SampleKhronosMaterialGiSemanticRenderEvidence(
            lightingOff,
            shadingModel,
            compiledEmission,
            metrics);

        int expectedUnlitCount = _scene.AuthenticatedGate.Manifest.Assets.Sum(
            static asset => asset.Expectations.MinimumUnlitCount);
        var snapshot = new SampleKhronosMaterialGiCompletionSnapshot(
            _lastSubmittedDiagnostics.ValidationMode,
            _lastSubmittedDiagnostics.ValidationWarningMessageCount,
            _lastSubmittedDiagnostics.ValidationErrorMessageCount,
            _sawDrawSubmission,
            _totalRenderedFrameCount,
            _lastSubmittedDiagnostics.MaterialGiV2ActiveFeatures,
            _renderer.Settings.GlobalIllumination.ActiveMaterialGiV2Features,
            _scene.Assets.Count,
            _scene.RenderObjectCount,
            expectedUnlitCount,
            _scene.RuntimeUnlitRenderObjectCount,
            _lastSubmittedDiagnostics.CaptureGpuDeviceName,
            _lastSubmittedDiagnostics.CaptureGpuDriverVersion,
            primary.Width,
            primary.Height,
            primary.MaximumComponent);
        IReadOnlyList<string> failures =
            SampleKhronosMaterialGiCompletionGate.Evaluate(snapshot);
        if (failures.Count != 0)
        {
            Abort(string.Join(" ", failures));
            return;
        }

        SampleKhronosMaterialGiRenderedGateReportPublisher.WritePassed(
            _scene,
            _lastSubmittedDiagnostics,
            SampleRenderSettingsFingerprint.Capture(_renderer.Settings),
            _totalRenderedFrameCount,
            primary,
            _sequence.ReadbackFrameCount,
            semanticEvidence);
        _semanticStage = SemanticCaptureStage.Complete;
        _terminalReportWritten = true;
        Environment.ExitCode = 0;
        Console.WriteLine(
            $"Official Khronos Material/GI rendered gate passed: " +
            $"assets={_scene.Assets.Count}, frames={_totalRenderedFrameCount}, " +
            $"unlitRmse={metrics.UnlitLightingRelativeRmse:R}, " +
            $"emissiveStrengths={string.Join(',', metrics.EmissiveStrengths.Select(static value => value.Strength))}, " +
            $"packageSha256={_scene.PackageSha256}, captureSha256={primary.Sha256}, " +
            $"report='{_scene.Options.ReportPath}'.");
        RequestExit();
    }

    private void BeginSemanticStage(SemanticCaptureStage stage)
    {
        _semanticStage = stage;
        _semanticWarmupFrames = 0;
        _semanticReadbackFrames = 0;
        if (stage == SemanticCaptureStage.LightingOffWarmup)
            _lightManager.ClearLights();
    }

    private bool IsSemanticWarmupComplete() =>
        _semanticStage switch
        {
            SemanticCaptureStage.LightingOffWarmup =>
                _semanticWarmupFrames >= LightingOffWarmupFrameCount,
            SemanticCaptureStage.ShadingModelWarmup or
            SemanticCaptureStage.CompiledEmissionWarmup =>
                _semanticWarmupFrames >= DiagnosticWarmupFrameCount,
            _ => false
        };

    private void QueueCurrentSemanticCapture()
    {
        string path = _semanticStage switch
        {
            SemanticCaptureStage.LightingOffWarmup => _lightingOffCapturePath,
            SemanticCaptureStage.ShadingModelWarmup => _shadingModelCapturePath,
            SemanticCaptureStage.CompiledEmissionWarmup => _compiledEmissionCapturePath,
            _ => throw new InvalidOperationException(
                $"Cannot queue a semantic capture during stage '{_semanticStage}'.")
        };
        QueueCapture(path);
        _semanticStage = _semanticStage switch
        {
            SemanticCaptureStage.LightingOffWarmup => SemanticCaptureStage.LightingOffReadback,
            SemanticCaptureStage.ShadingModelWarmup => SemanticCaptureStage.ShadingModelReadback,
            SemanticCaptureStage.CompiledEmissionWarmup => SemanticCaptureStage.CompiledEmissionReadback,
            _ => _semanticStage
        };
        _semanticReadbackFrames = 0;
    }

    private void QueueCapture(string path)
    {
        if (!_renderer.RequestLinearHdrCapture(path))
        {
            throw new InvalidOperationException(
                $"Renderer rejected lossless linear HDR capture '{path}'.");
        }
    }

    private void ApplySemanticDebugView()
    {
        _renderer.Settings.Materials.DebugView = _semanticStage switch
        {
            SemanticCaptureStage.ShadingModelWarmup or
            SemanticCaptureStage.ShadingModelReadback => MaterialDebugView.ShadingModel,
            SemanticCaptureStage.CompiledEmissionWarmup or
            SemanticCaptureStage.CompiledEmissionReadback => MaterialDebugView.CompiledEmission,
            _ => MaterialDebugView.None
        };
    }

    private string CurrentSemanticCapturePath() =>
        _semanticStage switch
        {
            SemanticCaptureStage.LightingOffReadback => _lightingOffCapturePath,
            SemanticCaptureStage.ShadingModelReadback => _shadingModelCapturePath,
            SemanticCaptureStage.CompiledEmissionReadback => _compiledEmissionCapturePath,
            _ => throw new InvalidOperationException(
                $"Stage '{_semanticStage}' has no active semantic readback.")
        };

    private static LinearFloatImage DecodeCapture(
        SampleKhronosMaterialGiVerifiedCapture capture)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            capture.Path,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            "Khronos semantic linear HDR capture");
        if (!string.Equals(
                evidence.Sha256,
                capture.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Khronos semantic capture '{capture.Path}' changed after verification.");
        }
        return PfmLinearImageCodec.Decode(evidence.Bytes);
    }

    private static SampleKhronosMaterialGiVerifiedCapture VerifyLockedCapture(string path)
    {
        SampleKhronosMaterialGiVerifiedCapture capture =
            SampleKhronosMaterialGiRenderedGateReportPublisher.VerifyCapture(path);
        if (capture.Width != LockedWidth || capture.Height != LockedHeight)
        {
            throw new InvalidDataException(
                $"Capture '{path}' is {capture.Width}x{capture.Height}; " +
                $"{LockedWidth}x{LockedHeight} is required.");
        }
        return capture;
    }

    private void ValidateCompanionPaths()
    {
        string[] paths =
        [
            _scene.Options.CapturePath,
            _lightingOffCapturePath,
            _shadingModelCapturePath,
            _compiledEmissionCapturePath,
            _scene.Options.ReportPath
        ];
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Khronos semantic evidence paths must be distinct.");
    }

    private void ValidateWindowSize()
    {
        (int width, int height) = _getWindowSize();
        if (width != LockedWidth || height != LockedHeight)
        {
            throw new InvalidOperationException(
                $"Khronos rendered gate requires a locked {LockedWidth}x{LockedHeight} window; " +
                $"the current size is {width}x{height}.");
        }
    }

    private void ObserveDrawSubmission(RendererDiagnostics diagnostics)
    {
        _sawDrawSubmission |=
            diagnostics.VisibleObjectCount > 0 &&
            (diagnostics.VisibleMeshletCount > 0 ||
             diagnostics.SubmittedOpaqueMeshlets > 0 ||
             diagnostics.MeshletCountSubmittedCpu > 0 ||
             diagnostics.ForwardEmittedMeshletsGpu > 0);
    }

    private void Abort(string reason)
    {
        if (_terminalReportWritten)
            return;
        _sequence.Fail(reason);
        SampleKhronosMaterialGiRenderedGateReportPublisher.TryWriteFailed(
            _scene.Options,
            reason,
            _scene,
            _lastSubmittedDiagnostics,
            _totalRenderedFrameCount);
        _terminalReportWritten = true;
        Environment.ExitCode = 1;
        Console.Error.WriteLine($"Official Khronos Material/GI rendered gate failed: {reason}");
        RequestExit();
    }

    private void RequestExit()
    {
        if (_exitRequested)
            return;
        _exitRequested = true;
        _exit();
    }

    private static string DescribeException(Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return $"{exception.GetType().Name}: {message}";
    }
}

/// <summary>
/// Converts host-level exceptions and early window shutdown into a terminal
/// atomic report. Renderer-owned failures normally terminate through the
/// runner; this guard covers failures raised outside its callbacks.
/// </summary>
internal sealed class SampleKhronosMaterialGiRenderedGateHostFailureGuard : IDisposable
{
    private readonly SampleKhronosMaterialGiRenderedGateOptions _options;
    private bool _disposed;

    public SampleKhronosMaterialGiRenderedGateHostFailureGuard(
        SampleKhronosMaterialGiRenderedGateOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        SampleKhronosMaterialGiRenderedGateReportPublisher.WriteHostStarting(_options);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public void RecordHostFailure(string failure)
    {
        SampleKhronosMaterialGiRenderedGateReportPublisher.TryFinalizeInProgress(
            _options,
            failure);
    }

    public bool CompleteHostRun(int exitCode)
    {
        string? status =
            SampleKhronosMaterialGiRenderedGateReportPublisher.TryReadStatus(_options);
        if (exitCode == 0 && string.Equals(status, "Passed", StringComparison.Ordinal))
            return true;
        if (string.Equals(status, "Failed", StringComparison.Ordinal))
            return false;

        string failure = exitCode == 0
            ? "Khronos rendered-gate host exited before publishing a terminal Passed report."
            : $"Khronos rendered-gate host exited with code {exitCode} before publishing a terminal report.";
        RecordHostFailure(failure);
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        Console.CancelKeyPress -= OnCancelKeyPress;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        string description = args.ExceptionObject is Exception exception
            ? $"{exception.GetType().Name}: {exception.Message}"
            : args.ExceptionObject?.ToString() ?? "unknown exception";
        RecordHostFailure($"Unhandled Khronos rendered-gate host failure: {description}");
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        RecordHostFailure("Khronos rendered-gate host was cancelled before completion.");
    }
}
