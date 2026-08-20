using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Debug;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkHdrRoiContract(
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    double MaximumMeanLuminanceShift = 0.02,
    double MaximumP95LuminanceShift = 0.03);

public sealed record SampleBenchmarkHdrQualityContract(
    string Schema,
    int Width,
    int Height,
    IReadOnlyList<SampleBenchmarkHdrRoiContract> Rois)
{
    public const string CurrentSchema = "njulf-benchmark-hdr-quality/v1";
}

public sealed record SampleBenchmarkHdrRoiResult(
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    double MeanLuminanceShift,
    double P95LuminanceShift,
    double MaximumMeanLuminanceShift,
    double MaximumP95LuminanceShift,
    bool Passed);

internal sealed record SampleBenchmarkHdrRoiEvaluation(
    string ContractPath,
    string ContractSha256,
    IReadOnlyList<SampleBenchmarkHdrRoiResult> Results)
{
    public static SampleBenchmarkHdrRoiEvaluation None { get; } =
        new(string.Empty, string.Empty, Array.Empty<SampleBenchmarkHdrRoiResult>());
}

internal static class SampleBenchmarkHdrQualityContractEvaluator
{
    private const double RelativeFloor = 1.0e-6;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static SampleBenchmarkHdrRoiEvaluation Evaluate(
        string? contractPath,
        LinearFloatImage reference,
        LinearFloatImage candidate)
    {
        if (string.IsNullOrWhiteSpace(contractPath))
            return SampleBenchmarkHdrRoiEvaluation.None;

        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            Path.GetFullPath(contractPath),
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark HDR quality contract");
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            JsonOptions.MaxDepth,
            "Benchmark HDR quality contract");
        SampleBenchmarkHdrQualityContract contract =
            JsonSerializer.Deserialize<SampleBenchmarkHdrQualityContract>(
                evidence.Bytes,
                JsonOptions) ??
            throw new InvalidDataException(
                "Benchmark HDR quality contract deserialized to null.");
        Validate(contract, reference);

        var results = new List<SampleBenchmarkHdrRoiResult>(contract.Rois.Count);
        foreach (SampleBenchmarkHdrRoiContract roi in contract.Rois)
        {
            double referenceMean = MeanLuminance(reference, roi);
            double candidateMean = MeanLuminance(candidate, roi);
            double meanShift = Math.Abs(candidateMean - referenceMean) /
                Math.Max(Math.Abs(referenceMean), RelativeFloor);
            double[] pixelShifts = PixelLuminanceShifts(reference, candidate, roi);
            Array.Sort(pixelShifts);
            int p95Index = Math.Clamp(
                (int)Math.Ceiling(pixelShifts.Length * 0.95) - 1,
                0,
                pixelShifts.Length - 1);
            double p95Shift = pixelShifts[p95Index];
            bool passed =
                meanShift <= roi.MaximumMeanLuminanceShift &&
                p95Shift <= roi.MaximumP95LuminanceShift;
            results.Add(new SampleBenchmarkHdrRoiResult(
                roi.Name,
                roi.X,
                roi.Y,
                roi.Width,
                roi.Height,
                meanShift,
                p95Shift,
                roi.MaximumMeanLuminanceShift,
                roi.MaximumP95LuminanceShift,
                passed));
        }

        return new SampleBenchmarkHdrRoiEvaluation(
            evidence.Path,
            evidence.Sha256,
            Array.AsReadOnly(results.ToArray()));
    }

    private static void Validate(
        SampleBenchmarkHdrQualityContract contract,
        LinearFloatImage image)
    {
        if (!string.Equals(
                contract.Schema,
                SampleBenchmarkHdrQualityContract.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"HDR quality schema '{contract.Schema}' is not " +
                $"'{SampleBenchmarkHdrQualityContract.CurrentSchema}'.");
        }
        if (contract.Width != image.Width || contract.Height != image.Height)
        {
            throw new InvalidDataException(
                $"HDR quality extent {contract.Width}x{contract.Height} does not " +
                $"match the reference {image.Width}x{image.Height}.");
        }
        if (contract.Rois == null || contract.Rois.Count == 0)
            throw new InvalidDataException("HDR quality contract contains no named ROIs.");
        if (contract.Rois.Any(static roi => roi == null))
            throw new InvalidDataException("HDR quality contract contains a null ROI.");
        if (contract.Rois.Select(static roi => roi.Name)
            .Distinct(StringComparer.Ordinal).Count() != contract.Rois.Count)
        {
            throw new InvalidDataException("HDR quality ROI names must be unique.");
        }

        foreach (SampleBenchmarkHdrRoiContract roi in contract.Rois)
        {
            if (string.IsNullOrWhiteSpace(roi.Name))
                throw new InvalidDataException("HDR quality ROI name is empty.");
            if (roi.X < 0 || roi.Y < 0 || roi.Width <= 0 || roi.Height <= 0 ||
                roi.X > contract.Width - roi.Width ||
                roi.Y > contract.Height - roi.Height)
            {
                throw new InvalidDataException(
                    $"HDR quality ROI '{roi.Name}' is outside the image extent.");
            }
            ValidateThreshold(
                roi.MaximumMeanLuminanceShift,
                roi.Name,
                "mean luminance");
            ValidateThreshold(
                roi.MaximumP95LuminanceShift,
                roi.Name,
                "P95 luminance");
        }
    }

    private static void ValidateThreshold(
        double value,
        string name,
        string role)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new InvalidDataException(
                $"HDR quality ROI '{name}' has invalid {role} threshold {value:R}.");
        }
    }

    private static double MeanLuminance(
        LinearFloatImage image,
        SampleBenchmarkHdrRoiContract roi)
    {
        double sum = 0.0;
        for (int y = roi.Y; y < roi.Y + roi.Height; y++)
        {
            for (int x = roi.X; x < roi.X + roi.Width; x++)
                sum += Luminance(image, x, y);
        }
        return sum / checked((long)roi.Width * roi.Height);
    }

    private static double[] PixelLuminanceShifts(
        LinearFloatImage reference,
        LinearFloatImage candidate,
        SampleBenchmarkHdrRoiContract roi)
    {
        var shifts = new double[checked(roi.Width * roi.Height)];
        int index = 0;
        for (int y = roi.Y; y < roi.Y + roi.Height; y++)
        {
            for (int x = roi.X; x < roi.X + roi.Width; x++)
            {
                double referenceLuminance = Luminance(reference, x, y);
                double candidateLuminance = Luminance(candidate, x, y);
                shifts[index++] = Math.Abs(
                    candidateLuminance - referenceLuminance) /
                    Math.Max(Math.Abs(referenceLuminance), RelativeFloor);
            }
        }
        return shifts;
    }

    private static double Luminance(LinearFloatImage image, int x, int y)
    {
        int component = checked((y * image.Width + x) * 3);
        double red = RequireFinite(image.Pixels[component], x, y);
        double green = RequireFinite(image.Pixels[component + 1], x, y);
        double blue = RequireFinite(image.Pixels[component + 2], x, y);
        return Math.Max(red, 0.0) * 0.2126 +
            Math.Max(green, 0.0) * 0.7152 +
            Math.Max(blue, 0.0) * 0.0722;
    }

    private static double RequireFinite(float value, int x, int y)
    {
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException(
                $"HDR quality image contains a non-finite value at ({x}, {y}).");
        }
        return value;
    }
}
