using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Rendering.Debug;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

public enum SampleMaterialGiSemanticSurface : byte
{
    SingleSidedFront = 0,
    DoubleSidedFrontBackdrop = 1,
    DoubleSidedBack = 2
}

public readonly record struct SampleMaterialGiSemanticRgb(
    float R,
    float G,
    float B);

public sealed record SampleMaterialGiSemanticRoi(
    string Name,
    string FixtureStableId,
    SampleMaterialGiPixelRegion Region,
    SampleMaterialGiSemanticSurface ExpectedSurface,
    SampleMaterialGiSemanticRgb ExpectedRgb,
    string Requirement);

public sealed record SampleMaterialGiSemanticThresholds(
    float MaximumPerComponentError,
    float RequiredMatchingPixelFraction,
    string Justification);

public sealed record SampleMaterialGiSemanticEvidenceContract(
    string SchemaVersion,
    string Fingerprint,
    SampleMaterialGiCaptureSignal Signal,
    int Width,
    int Height,
    SampleMaterialGiSemanticThresholds Thresholds,
    IReadOnlyList<SampleMaterialGiSemanticRoi> Regions);

public sealed record SampleMaterialGiSemanticRoiMetric(
    string Name,
    string FixtureStableId,
    SampleMaterialGiPixelRegion Region,
    SampleMaterialGiSemanticSurface ExpectedSurface,
    SampleMaterialGiSemanticRgb ExpectedRgb,
    int PixelCount,
    double MeanR,
    double MeanG,
    double MeanB,
    double MatchingPixelFraction,
    double MaximumPerComponentError,
    bool Passed);

public sealed record SampleMaterialGiSemanticEvidenceReport(
    string SchemaVersion,
    string Status,
    string FailureReason,
    string ContractFingerprint,
    SampleMaterialGiCaptureSignal Signal,
    string ArtifactRelativePath,
    string ArtifactSha256,
    IReadOnlyList<SampleMaterialGiSemanticRoiMetric> Regions)
{
    [JsonIgnore]
    public bool Passed => string.Equals(Status, "passed", StringComparison.Ordinal);
}

/// <summary>
/// Fail-closed, lighting-independent evidence for winding, sidedness, and
/// alpha discard. The material-sidedness debug view emits fixed semantic
/// colors after the normal depth/sidedness/coverage decisions have run.
/// </summary>
public static class SampleMaterialGiSemanticEvidenceGate
{
    public const string ContractSchemaVersion = "material-gi-semantic-evidence/v1";
    public const string ReportSchemaVersion = "material-gi-semantic-evidence-report/v1";
    private const int RoiHalfExtent = 5;

    private static readonly SampleMaterialGiSemanticRgb SingleFrontRgb =
        new(0.2f, 0.85f, 0.25f);
    private static readonly SampleMaterialGiSemanticRgb DoubleFrontRgb =
        new(0.1f, 0.8f, 1f);
    private static readonly SampleMaterialGiSemanticRgb DoubleBackRgb =
        new(1f, 0.45f, 0.1f);

    public static SampleMaterialGiSemanticEvidenceContract CreateContract(
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures,
        SampleSponzaGiCameraBookmark camera,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(camera);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions must be positive.");

        var regions = new List<SampleMaterialGiSemanticRoi>(9);
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "winding.reference-single-front.visible",
            SampleMaterialGiConformanceSceneLayout.SemanticReferenceSingleFrontId,
            u: 0.5f,
            v: 0.5f,
            expectedSurface: SampleMaterialGiSemanticSurface.SingleSidedFront,
            requirement: "The positive-determinant control must render as a single-sided front face.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "winding.mirrored-single-front.visible",
            SampleMaterialGiConformanceSceneLayout.SemanticMirroredSingleFrontId,
            u: 0.5f,
            v: 0.5f,
            expectedSurface: SampleMaterialGiSemanticSurface.SingleSidedFront,
            requirement: "A negative determinant must not invert logical front-face visibility.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "winding.mirrored-single-back.rejected",
            SampleMaterialGiConformanceSceneLayout.SemanticMirroredSingleBackId,
            u: 0.5f,
            v: 0.5f,
            expectedSurface: SampleMaterialGiSemanticSurface.DoubleSidedFrontBackdrop,
            requirement: "A mirrored single-sided logical back face must reveal its backdrop.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "winding.mirrored-double-back.visible-backface",
            SampleMaterialGiConformanceSceneLayout.SemanticMirroredDoubleBackId,
            u: 0.5f,
            v: 0.5f,
            expectedSurface: SampleMaterialGiSemanticSurface.DoubleSidedBack,
            requirement: "A mirrored double-sided logical back face must render with back-face semantics.");

        // The 8x8 mask has two-texel constant blocks. These sample points are
        // centered in an alpha=1 block and an alpha=0 block respectively.
        const float opaqueU = 0.625f;
        const float transparentU = 0.125f;
        const float alphaSampleV = 0.125f;
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "skinned-mask.single-front.opaque-visible",
            SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskSingleFrontId,
            opaqueU,
            alphaSampleV,
            expectedSurface: SampleMaterialGiSemanticSurface.SingleSidedFront,
            requirement: "An opaque skinned-mask texel on a front face must survive coverage.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "skinned-mask.single-front.transparent-discarded",
            SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskSingleFrontId,
            transparentU,
            alphaSampleV,
            expectedSurface: SampleMaterialGiSemanticSurface.DoubleSidedFrontBackdrop,
            requirement: "A transparent skinned-mask texel must reveal its backdrop.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "skinned-mask.single-back.opaque-rejected",
            SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskSingleBackId,
            opaqueU,
            alphaSampleV,
            expectedSurface: SampleMaterialGiSemanticSurface.DoubleSidedFrontBackdrop,
            requirement: "An opaque texel cannot bypass single-sided back-face rejection.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "skinned-mask.double-back.opaque-visible",
            SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskDoubleBackId,
            opaqueU,
            alphaSampleV,
            expectedSurface: SampleMaterialGiSemanticSurface.DoubleSidedBack,
            requirement: "An opaque skinned-mask texel on a double-sided back face must render.");
        AddRegion(
            regions,
            fixtures,
            camera,
            width,
            height,
            "skinned-mask.double-back.transparent-discarded",
            SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskDoubleBackId,
            transparentU,
            alphaSampleV,
            expectedSurface: SampleMaterialGiSemanticSurface.DoubleSidedFrontBackdrop,
            requirement: "A transparent texel must be discarded even on a double-sided back face.");

        var thresholds = new SampleMaterialGiSemanticThresholds(
            MaximumPerComponentError: 0.035f,
            RequiredMatchingPixelFraction: 0.98f,
            "Fixed shader semantic colors differ by at least 0.55 in one RGB component. " +
            "A 0.035 component tolerance covers RGBA16F quantization, while 98% agreement " +
            "permits at most two conservative raster boundary pixels in an 11x11 interior ROI.");
        string fingerprint = ComputeContractFingerprint(
            ContractSchemaVersion,
            SampleMaterialGiCaptureSignal.MaterialSidedness,
            width,
            height,
            thresholds,
            regions);
        return new SampleMaterialGiSemanticEvidenceContract(
            ContractSchemaVersion,
            fingerprint,
            SampleMaterialGiCaptureSignal.MaterialSidedness,
            width,
            height,
            thresholds,
            regions.ToArray());
    }

    public static SampleMaterialGiSemanticEvidenceReport EvaluateCapture(
        string outputDirectory,
        IReadOnlyList<SampleMaterialGiArtifact> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(artifacts);
        SampleMaterialGiSemanticEvidenceContract contract =
            SampleMaterialGiConformanceCatalog.SemanticEvidence;
        SampleMaterialGiCaptureOutput output =
            SampleMaterialGiConformanceCatalog.RequiredOutputs.Single(
                value => value.Signal == contract.Signal);
        string expectedRelativePath =
            SampleMaterialGiArtifactPublisher.GetRelativeArtifactPath(output);
        SampleMaterialGiArtifact artifact = artifacts.SingleOrDefault(value =>
                value.Signal == contract.Signal &&
                string.Equals(
                    value.RelativePath,
                    expectedRelativePath,
                    StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"Semantic evidence artifact '{expectedRelativePath}' is absent.");
        string path = ResolveContainedPath(outputDirectory, artifact.RelativePath);
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            $"Semantic evidence artifact '{artifact.RelativePath}'");
        byte[] encoded = evidence.Bytes;
        string actualHash = evidence.Sha256;
        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Semantic evidence artifact hash changed after verification.");
        LinearFloatImage image = PfmLinearImageCodec.Decode(encoded);
        return Evaluate(contract, image, artifact.RelativePath, actualHash);
    }

    public static SampleMaterialGiSemanticEvidenceReport Evaluate(
        SampleMaterialGiSemanticEvidenceContract contract,
        LinearFloatImage image,
        string artifactRelativePath,
        string artifactSha256)
    {
        ValidateContract(contract);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRelativePath);
        RequireSha256(artifactSha256, nameof(artifactSha256));
        if (image.Width != contract.Width || image.Height != contract.Height)
        {
            throw new InvalidDataException(
                $"Semantic image is {image.Width}x{image.Height}; " +
                $"{contract.Width}x{contract.Height} is required.");
        }
        if (image.Pixels.Length != checked(image.Width * image.Height * 3))
            throw new InvalidDataException("Semantic image RGB payload length is invalid.");

        var metrics = new List<SampleMaterialGiSemanticRoiMetric>(contract.Regions.Count);
        foreach (SampleMaterialGiSemanticRoi region in contract.Regions)
            metrics.Add(EvaluateRegion(contract, image, region));

        SampleMaterialGiSemanticRoiMetric[] failed =
            metrics.Where(static value => !value.Passed).ToArray();
        return new SampleMaterialGiSemanticEvidenceReport(
            ReportSchemaVersion,
            failed.Length == 0 ? "passed" : "failed",
            failed.Length == 0
                ? string.Empty
                : "Semantic material visibility failed for: " +
                  string.Join(
                      ", ",
                      failed.Select(static value =>
                          $"{value.Name} " +
                          $"(match={value.MatchingPixelFraction:R}, " +
                          $"maxError={value.MaximumPerComponentError:R})")),
            contract.Fingerprint,
            contract.Signal,
            artifactRelativePath,
            artifactSha256.ToLowerInvariant(),
            metrics.ToArray());
    }

    public static void ValidatePublishedEvidence(
        string outputDirectory,
        IReadOnlyList<SampleMaterialGiArtifact> artifacts,
        SampleMaterialGiSemanticEvidenceReport? published)
    {
        if (published == null)
            throw new InvalidDataException("Passed capture manifest has no semantic visibility evidence.");
        if (published.Regions == null)
            throw new InvalidDataException("Published semantic visibility evidence has no ROI metrics.");
        SampleMaterialGiSemanticEvidenceReport recomputed =
            EvaluateCapture(outputDirectory, artifacts);
        if (!recomputed.Passed)
            throw new InvalidDataException(recomputed.FailureReason);
        if (published.SchemaVersion != recomputed.SchemaVersion ||
            published.Status != recomputed.Status ||
            published.FailureReason != recomputed.FailureReason ||
            published.ContractFingerprint != recomputed.ContractFingerprint ||
            published.Signal != recomputed.Signal ||
            published.ArtifactRelativePath != recomputed.ArtifactRelativePath ||
            published.ArtifactSha256 != recomputed.ArtifactSha256 ||
            published.Regions.Count != recomputed.Regions.Count)
        {
            throw new InvalidDataException(
                "Published semantic visibility evidence does not match the captured artifact.");
        }
        for (int index = 0; index < recomputed.Regions.Count; index++)
        {
            if (published.Regions[index] != recomputed.Regions[index])
            {
                throw new InvalidDataException(
                    $"Published semantic ROI '{recomputed.Regions[index].Name}' was modified.");
            }
        }
    }

    private static SampleMaterialGiSemanticRoiMetric EvaluateRegion(
        SampleMaterialGiSemanticEvidenceContract contract,
        LinearFloatImage image,
        SampleMaterialGiSemanticRoi roi)
    {
        ValidateRegionBounds(roi.Region, image.Width, image.Height, roi.Name);
        int pixelCount = checked(roi.Region.Width * roi.Region.Height);
        int matchingPixels = 0;
        double sumR = 0.0;
        double sumG = 0.0;
        double sumB = 0.0;
        double maximumError = 0.0;
        for (int y = roi.Region.Y; y < roi.Region.Y + roi.Region.Height; y++)
            for (int x = roi.Region.X; x < roi.Region.X + roi.Region.Width; x++)
            {
                int component = checked((y * image.Width + x) * 3);
                float r = image.Pixels[component];
                float g = image.Pixels[component + 1];
                float b = image.Pixels[component + 2];
                if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
                    throw new InvalidDataException($"Semantic ROI '{roi.Name}' contains non-finite RGB.");
                double pixelError = Math.Max(
                    Math.Abs(r - roi.ExpectedRgb.R),
                    Math.Max(
                        Math.Abs(g - roi.ExpectedRgb.G),
                        Math.Abs(b - roi.ExpectedRgb.B)));
                if (pixelError <= contract.Thresholds.MaximumPerComponentError)
                    matchingPixels++;
                maximumError = Math.Max(maximumError, pixelError);
                sumR += r;
                sumG += g;
                sumB += b;
            }

        double matchingFraction = (double)matchingPixels / pixelCount;
        return new SampleMaterialGiSemanticRoiMetric(
            roi.Name,
            roi.FixtureStableId,
            roi.Region,
            roi.ExpectedSurface,
            roi.ExpectedRgb,
            pixelCount,
            sumR / pixelCount,
            sumG / pixelCount,
            sumB / pixelCount,
            matchingFraction,
            maximumError,
            matchingFraction >= contract.Thresholds.RequiredMatchingPixelFraction);
    }

    private static void AddRegion(
        ICollection<SampleMaterialGiSemanticRoi> regions,
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures,
        SampleSponzaGiCameraBookmark camera,
        int width,
        int height,
        string name,
        string fixtureStableId,
        float u,
        float v,
        SampleMaterialGiSemanticSurface expectedSurface,
        string requirement)
    {
        SampleMaterialGiSceneFixture fixture = fixtures.SingleOrDefault(value =>
                string.Equals(value.StableId, fixtureStableId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Semantic fixture '{fixtureStableId}' is missing.");
        if (fixture.Primitive is not (
                SampleMaterialGiScenePrimitive.Card or
                SampleMaterialGiScenePrimitive.SkinnedCard))
        {
            throw new InvalidOperationException(
                $"Semantic fixture '{fixtureStableId}' is not a card.");
        }
        if (!fixtures.Any(value =>
                string.Equals(
                    value.StableId,
                    $"{fixtureStableId}.backdrop",
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Semantic fixture '{fixtureStableId}' has no backdrop.");
        }

        SampleMaterialGiPixelRegion region =
            ProjectCardSample(fixture, camera, width, height, u, v);
        SampleMaterialGiSemanticRgb expectedRgb = expectedSurface switch
        {
            SampleMaterialGiSemanticSurface.SingleSidedFront => SingleFrontRgb,
            SampleMaterialGiSemanticSurface.DoubleSidedFrontBackdrop => DoubleFrontRgb,
            SampleMaterialGiSemanticSurface.DoubleSidedBack => DoubleBackRgb,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedSurface), expectedSurface, null)
        };
        regions.Add(new SampleMaterialGiSemanticRoi(
            name,
            fixtureStableId,
            region,
            expectedSurface,
            expectedRgb,
            requirement));
    }

    private static SampleMaterialGiPixelRegion ProjectCardSample(
        SampleMaterialGiSceneFixture fixture,
        SampleSponzaGiCameraBookmark bookmark,
        int width,
        int height,
        float u,
        float v)
    {
        if (!float.IsFinite(u) || !float.IsFinite(v) ||
            u <= 0f || u >= 1f || v <= 0f || v >= 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(u), "Card UV samples must be finite and interior.");
        }

        var camera = new FirstPersonCamera(bookmark.Position, bookmark.Yaw, bookmark.Pitch)
        {
            FieldOfView = bookmark.FieldOfView,
            NearPlane = bookmark.NearPlane,
            FarPlane = bookmark.FarPlane,
            AspectRatio = (float)width / height
        };
        camera.Update();
        CoreVector3 local = new(u - 0.5f, 0.5f - v, 0f);
        CoreVector3 world = local * fixture.Transform.ToWorldMatrix();
        CoreMatrix4x4 matrix = camera.ViewProjectionMatrix;
        float clipX =
            world.X * matrix.M11 + world.Y * matrix.M21 + world.Z * matrix.M31 + matrix.M41;
        float clipY =
            world.X * matrix.M12 + world.Y * matrix.M22 + world.Z * matrix.M32 + matrix.M42;
        float clipW =
            world.X * matrix.M14 + world.Y * matrix.M24 + world.Z * matrix.M34 + matrix.M44;
        if (!float.IsFinite(clipW) || clipW <= 1e-5f)
            throw new InvalidOperationException($"Semantic fixture '{fixture.StableId}' is behind the camera.");
        float screenX = (clipX / clipW + 1f) * 0.5f * width;
        float screenY = (clipY / clipW + 1f) * 0.5f * height;
        int centerX = checked((int)MathF.Round(screenX));
        int centerY = checked((int)MathF.Round(screenY));
        var region = new SampleMaterialGiPixelRegion(
            centerX - RoiHalfExtent,
            centerY - RoiHalfExtent,
            RoiHalfExtent * 2 + 1,
            RoiHalfExtent * 2 + 1);
        ValidateRegionBounds(region, width, height, fixture.StableId);
        return region;
    }

    private static void ValidateContract(SampleMaterialGiSemanticEvidenceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.SchemaVersion != ContractSchemaVersion)
            throw new InvalidDataException($"Semantic contract schema '{contract.SchemaVersion}' is unsupported.");
        if (contract.Signal != SampleMaterialGiCaptureSignal.MaterialSidedness)
            throw new InvalidDataException("Semantic contract must use the material-sidedness signal.");
        if (contract.Thresholds == null)
            throw new InvalidDataException("Semantic contract contains no thresholds.");
        if (contract.Regions == null || contract.Regions.Count == 0)
            throw new InvalidDataException("Semantic contract contains no named regions.");
        if (!float.IsFinite(contract.Thresholds.MaximumPerComponentError) ||
            contract.Thresholds.MaximumPerComponentError < 0f ||
            !float.IsFinite(contract.Thresholds.RequiredMatchingPixelFraction) ||
            contract.Thresholds.RequiredMatchingPixelFraction <= 0f ||
            contract.Thresholds.RequiredMatchingPixelFraction > 1f)
        {
            throw new InvalidDataException("Semantic evidence thresholds are invalid.");
        }
        string expectedFingerprint = ComputeContractFingerprint(
            contract.SchemaVersion,
            contract.Signal,
            contract.Width,
            contract.Height,
            contract.Thresholds,
            contract.Regions);
        if (!string.Equals(expectedFingerprint, contract.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Semantic evidence contract fingerprint is invalid.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SampleMaterialGiSemanticRoi roi in contract.Regions)
        {
            if (string.IsNullOrWhiteSpace(roi.Name) || !names.Add(roi.Name))
                throw new InvalidDataException("Semantic regions require unique stable names.");
            if (string.IsNullOrWhiteSpace(roi.FixtureStableId) ||
                string.IsNullOrWhiteSpace(roi.Requirement))
            {
                throw new InvalidDataException($"Semantic ROI '{roi.Name}' lacks traceable metadata.");
            }
            ValidateRegionBounds(roi.Region, contract.Width, contract.Height, roi.Name);
        }
    }

    private static void ValidateRegionBounds(
        SampleMaterialGiPixelRegion region,
        int width,
        int height,
        string name)
    {
        if (region.X < 0 || region.Y < 0 ||
            region.Width <= 0 || region.Height <= 0 ||
            region.X > width - region.Width ||
            region.Y > height - region.Height)
        {
            throw new InvalidDataException(
                $"Semantic ROI '{name}' ({region.X},{region.Y},{region.Width},{region.Height}) " +
                $"is outside {width}x{height}.");
        }
    }

    private static string ComputeContractFingerprint(
        string schemaVersion,
        SampleMaterialGiCaptureSignal signal,
        int width,
        int height,
        SampleMaterialGiSemanticThresholds thresholds,
        IReadOnlyList<SampleMaterialGiSemanticRoi> regions)
    {
        var builder = new StringBuilder(4_096);
        Append(builder, schemaVersion);
        Append(builder, signal.ToString());
        Append(builder, width);
        Append(builder, height);
        Append(builder, thresholds.MaximumPerComponentError);
        Append(builder, thresholds.RequiredMatchingPixelFraction);
        Append(builder, thresholds.Justification);
        foreach (SampleMaterialGiSemanticRoi roi in regions)
        {
            Append(builder, roi.Name);
            Append(builder, roi.FixtureStableId);
            Append(builder, roi.Region.X);
            Append(builder, roi.Region.Y);
            Append(builder, roi.Region.Width);
            Append(builder, roi.Region.Height);
            Append(builder, roi.ExpectedSurface.ToString());
            Append(builder, roi.ExpectedRgb.R);
            Append(builder, roi.ExpectedRgb.G);
            Append(builder, roi.ExpectedRgb.B);
            Append(builder, roi.Requirement);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string ResolveContainedPath(string outputDirectory, string relativePath)
    {
        string directory = Path.GetFullPath(outputDirectory);
        string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                      Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact path '{relativePath}' escapes the capture directory.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Semantic artifact '{relativePath}' is missing.", path);
        return path;
    }

    private static void RequireSha256(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A lowercase or uppercase SHA-256 hex identity is required.", name);
        }
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');

    private static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, float value) =>
        Append(builder, value.ToString("R", CultureInfo.InvariantCulture));
}
