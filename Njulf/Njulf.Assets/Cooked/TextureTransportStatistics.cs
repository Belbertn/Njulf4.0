using System.Text.Json.Serialization;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

[Flags]
public enum TextureTransportStatisticsValidity : uint
{
    None = 0,
    SourceContentHash = 1u << 0,
    DecodedPixels = 1u << 1,
    LinearChannelMoments = 1u << 2,
    EmissiveLuminanceMoments = 1u << 3,
    AlphaHistogram = 1u << 4,
    NormalVariance = 1u << 5
}

public enum TextureTransportStatisticsStatus
{
    Valid,
    LegacyMissing,
    UnsupportedEncoding,
    InvalidData
}

/// <summary>
/// A JSON-friendly, double-precision vector used by cooked transport metadata.
/// Accumulation and persistence remain in double precision; conversion to the
/// renderer's float vectors is explicit.
/// </summary>
public readonly record struct TextureTransportVector4(
    double X,
    double Y,
    double Z,
    double W)
{
    public static TextureTransportVector4 Zero { get; } = new(0.0, 0.0, 0.0, 0.0);
    public static TextureTransportVector4 One { get; } = new(1.0, 1.0, 1.0, 1.0);

    public Vector4 ToVector4() => new((float)X, (float)Y, (float)Z, (float)W);
}

/// <summary>
/// Versioned, source-resolution statistics used for material and GI transport.
/// Missing or unsupported data is represented by <see cref="Status"/> and
/// <see cref="Validity"/> rather than inferred from zero-valued sentinels.
/// </summary>
public sealed record TextureTransportStatistics
{
    public const int CurrentSchemaVersion = 2;
    public const uint CurrentAlgorithmVersion = 5;
    public const int AlphaHistogramBinCount = 256;
    public const string StbDecoderVersion = "StbImageSharp/2.30.15";
    public const string WebPDecoderVersion = WebPTextureDecoder.DecoderVersion;
    public const string BcDecoderVersion = "BCnEncoder.Net/2.3.0";
    public const string DdsDecoderVersion = "BCnEncoder.Net/2.3.0 DDS/1";
    public const string KtxContainerDecoderVersion = "Njulf KTX2 container/2";
    public const string KtxRawDecoderVersion = "Njulf KTX2 raw/2";
    public const string BasisDecoderVersion = "Ktx2.NET/1.0.5 (libktx RGBA32)";
    public const string ZstdDecoderVersion = "ZstdSharp.Port/0.8.8";
    public const string ZlibDecoderVersion = "System.IO.Compression.ZLibStream/net10.0";

    /// <summary>
    /// Stable cache discriminator for every decoder that can affect KTX2
    /// transport statistics. Update this value whenever a decoder, container
    /// validation rule, or decoded channel convention changes.
    /// </summary>
    public const string KtxStatisticsDecoderVersion =
        "Njulf.KTX2.Container/2|Njulf.KTX2.Raw/2|Ktx2.NET/1.0.5-libktx-RGBA32|" +
        "BCnEncoder.Net/2.3.0|ZstdSharp.Port/0.8.8|ZLibStream/net10.0";
    private const TextureTransportStatisticsValidity KnownValidity =
        TextureTransportStatisticsValidity.SourceContentHash |
        TextureTransportStatisticsValidity.DecodedPixels |
        TextureTransportStatisticsValidity.LinearChannelMoments |
        TextureTransportStatisticsValidity.EmissiveLuminanceMoments |
        TextureTransportStatisticsValidity.AlphaHistogram |
        TextureTransportStatisticsValidity.NormalVariance;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public uint AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;
    public TextureTransportStatisticsStatus Status { get; init; } = TextureTransportStatisticsStatus.LegacyMissing;
    public TextureTransportStatisticsValidity Validity { get; init; }
    public ulong SourceContentHash { get; init; }
    public TextureSemantic Semantic { get; init; } = TextureSemantic.Data;
    public TextureColorSpace ColorSpace { get; init; } = TextureColorSpace.Linear;
    public int Width { get; init; }
    public int Height { get; init; }
    public long PixelCount { get; init; }
    public TextureTransportVector4 LinearChannelMean { get; init; }
    public TextureTransportVector4 LinearChannelSecondMoment { get; init; }
    public double EmissiveLuminanceMean { get; init; }
    public double EmissiveLuminanceSecondMoment { get; init; }
    public double EmissiveLuminanceMaximum { get; init; }
    public ulong[] AlphaHistogram { get; init; } = Array.Empty<ulong>();
    public double NormalVariance { get; init; }
    public string Decoder { get; init; } = string.Empty;
    public string? InvalidReason { get; init; }

    [JsonIgnore]
    public bool HasLinearChannelMoments =>
        SchemaVersion == CurrentSchemaVersion &&
        AlgorithmVersion == CurrentAlgorithmVersion &&
        Status == TextureTransportStatisticsStatus.Valid &&
        Validity.HasFlag(TextureTransportStatisticsValidity.LinearChannelMoments) &&
        IsFinite(LinearChannelMean);

    [JsonIgnore]
    public bool IsValid =>
        Status == TextureTransportStatisticsStatus.Valid &&
        Validate().Count == 0;

    public bool TryGetLinearMean(out Vector4 mean)
    {
        if (!HasLinearChannelMoments)
        {
            mean = default;
            return false;
        }

        mean = LinearChannelMean.ToVector4();
        return true;
    }

    /// <summary>
    /// Returns the source-resolution fraction whose alpha is greater than or
    /// equal to <paramref name="cutoff"/>. Cutoffs above one are legal and
    /// therefore return zero for normalized-alpha sources.
    /// </summary>
    public double GetAlphaCoverage(double cutoff)
    {
        if (!double.IsFinite(cutoff))
            throw new ArgumentOutOfRangeException(nameof(cutoff), "Alpha cutoff must be finite.");
        if (SchemaVersion != CurrentSchemaVersion ||
            AlgorithmVersion != CurrentAlgorithmVersion ||
            Status != TextureTransportStatisticsStatus.Valid ||
            !Validity.HasFlag(TextureTransportStatisticsValidity.AlphaHistogram) ||
            AlphaHistogram is not { Length: AlphaHistogramBinCount } histogram ||
            PixelCount <= 0)
        {
            throw new InvalidOperationException("Alpha coverage is unavailable because the alpha histogram is invalid.");
        }
        if (cutoff <= 0.0)
            return 1.0;
        if (cutoff > 1.0)
            return 0.0;

        int firstBin = Math.Clamp((int)Math.Ceiling(cutoff * 255.0 - 1e-12), 0, 255);
        ulong covered = 0;
        for (int bin = firstBin; bin < histogram.Length; bin++)
            covered = checked(covered + histogram[bin]);
        return covered / (double)PixelCount;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Unsupported texture-statistics schema version {SchemaVersion}.");
        if (AlgorithmVersion != CurrentAlgorithmVersion)
            errors.Add($"Unsupported texture-statistics algorithm version {AlgorithmVersion}.");
        if (!Enum.IsDefined(Status))
            errors.Add($"Unknown texture-statistics status {Status}.");
        if (!Enum.IsDefined(Semantic))
            errors.Add($"Unknown texture semantic {Semantic}.");
        if (!Enum.IsDefined(ColorSpace))
            errors.Add($"Unknown texture color space {ColorSpace}.");
        if ((Validity & ~KnownValidity) != 0)
            errors.Add($"Texture-statistics validity contains unknown bits: {Validity & ~KnownValidity}.");

        if (Status != TextureTransportStatisticsStatus.Valid)
        {
            if (string.IsNullOrWhiteSpace(InvalidReason))
                errors.Add("Invalid statistics must provide an observable reason.");
            return errors;
        }
        if (!string.IsNullOrWhiteSpace(InvalidReason))
            errors.Add("Valid statistics cannot provide an invalid-data reason.");

        TextureTransportStatisticsValidity required =
            TextureTransportStatisticsValidity.SourceContentHash |
            TextureTransportStatisticsValidity.DecodedPixels |
            TextureTransportStatisticsValidity.LinearChannelMoments |
            TextureTransportStatisticsValidity.EmissiveLuminanceMoments |
            TextureTransportStatisticsValidity.AlphaHistogram;
        if ((Validity & required) != required)
            errors.Add($"Valid statistics are missing required flags: {required & ~Validity}.");
        if (string.IsNullOrWhiteSpace(Decoder))
            errors.Add("Valid statistics must identify the pinned decoder implementation.");
        if (Width <= 0 || Height <= 0 || PixelCount != (long)Width * Height)
            errors.Add($"Invalid decoded dimensions {Width}x{Height} for {PixelCount} pixels.");
        ValidateFinite(LinearChannelMean, nameof(LinearChannelMean), errors);
        ValidateFinite(LinearChannelSecondMoment, nameof(LinearChannelSecondMoment), errors);
        ValidateMoment(LinearChannelMean.X, LinearChannelSecondMoment.X, "red", errors);
        ValidateMoment(LinearChannelMean.Y, LinearChannelSecondMoment.Y, "green", errors);
        ValidateMoment(LinearChannelMean.Z, LinearChannelSecondMoment.Z, "blue", errors);
        ValidateMoment(LinearChannelMean.W, LinearChannelSecondMoment.W, "alpha", errors);

        if (!double.IsFinite(EmissiveLuminanceMean) ||
            !double.IsFinite(EmissiveLuminanceSecondMoment) ||
            !double.IsFinite(EmissiveLuminanceMaximum) ||
            IsSecondMomentInvalid(EmissiveLuminanceMean, EmissiveLuminanceSecondMoment) ||
            EmissiveLuminanceMaximum + RelativeTolerance(EmissiveLuminanceMaximum, EmissiveLuminanceMean) <
            EmissiveLuminanceMean)
        {
            errors.Add("Emissive luminance moments are not finite and ordered.");
        }

        ulong[] histogram = AlphaHistogram ?? Array.Empty<ulong>();
        if (AlphaHistogram is null)
            errors.Add("Alpha histogram cannot be null.");
        if (histogram.Length != AlphaHistogramBinCount)
        {
            errors.Add($"Alpha histogram must have exactly {AlphaHistogramBinCount} bins.");
        }
        else
        {
            ulong count = 0;
            try
            {
                for (int i = 0; i < histogram.Length; i++)
                    count = checked(count + histogram[i]);
            }
            catch (OverflowException)
            {
                errors.Add("Alpha histogram count overflowed.");
            }
            if (count != (ulong)Math.Max(0, PixelCount))
                errors.Add($"Alpha histogram contains {count} samples, expected {PixelCount}.");
        }

        if (Validity.HasFlag(TextureTransportStatisticsValidity.NormalVariance) &&
            (!double.IsFinite(NormalVariance) || NormalVariance < -1e-9 || NormalVariance > 1.0 + 1e-9))
        {
            errors.Add($"Normal variance {NormalVariance} is outside [0, 1].");
        }

        if (ColorSpace != TextureColorSpace.HdrLinear)
        {
            ValidateNormalized(LinearChannelMean, nameof(LinearChannelMean), errors);
            ValidateNormalized(LinearChannelSecondMoment, nameof(LinearChannelSecondMoment), errors);
        }
        else
        {
            ValidateHdrLinear(LinearChannelMean, nameof(LinearChannelMean), errors);
            ValidateHdrSecondMoment(
                LinearChannelSecondMoment,
                nameof(LinearChannelSecondMoment),
                errors);
        }

        return errors;
    }

    public void EnsureValid(string sourceName)
    {
        IReadOnlyList<string> errors = Validate();
        if (errors.Count > 0)
            throw new InvalidDataException($"Texture transport statistics for '{sourceName}' are invalid: {string.Join(" ", errors)}");
    }

    public static TextureTransportStatistics Invalid(
        TextureTransportStatisticsStatus status,
        string reason,
        ulong sourceContentHash,
        TextureSemantic semantic,
        TextureColorSpace colorSpace,
        string decoder = "")
    {
        if (status == TextureTransportStatisticsStatus.Valid)
            throw new ArgumentOutOfRangeException(nameof(status), "Use decoded statistics for valid data.");
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new TextureTransportStatistics
        {
            Status = status,
            Validity = TextureTransportStatisticsValidity.SourceContentHash,
            SourceContentHash = sourceContentHash,
            Semantic = semantic,
            ColorSpace = colorSpace,
            Decoder = decoder,
            InvalidReason = reason
        };
    }

    internal static TextureTransportStatistics Create(
        ulong sourceContentHash,
        TextureSemantic semantic,
        TextureColorSpace colorSpace,
        int width,
        int height,
        ReadOnlySpan<double> linearRgba,
        string decoder)
    {
        if (width <= 0 || height <= 0 || linearRgba.Length != checked(width * height * 4))
            throw new ArgumentException("Decoded texture data does not match its dimensions.", nameof(linearRgba));

        var sums = new double[4];
        var compensatedSums = new double[4];
        var secondSums = new double[4];
        var compensatedSecondSums = new double[4];
        var alphaHistogram = new ulong[AlphaHistogramBinCount];
        double luminanceSum = 0.0;
        double luminanceCompensation = 0.0;
        double luminanceSecondSum = 0.0;
        double luminanceSecondCompensation = 0.0;
        double luminanceMaximum = double.NegativeInfinity;
        double normalX = 0.0;
        double normalY = 0.0;
        double normalZ = 0.0;

        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int offset = pixel * 4;
            double r = linearRgba[offset];
            double g = linearRgba[offset + 1];
            double b = linearRgba[offset + 2];
            double a = linearRgba[offset + 3];
            if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b) || !double.IsFinite(a))
            {
                return Invalid(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"Decoded pixel {pixel} contains a non-finite channel.",
                    sourceContentHash,
                    semantic,
                    colorSpace,
                    decoder);
            }
            bool normalizedColor = colorSpace != TextureColorSpace.HdrLinear;
            if (r < 0.0 || g < 0.0 || b < 0.0 ||
                a is < 0.0 or > 1.0 ||
                normalizedColor && (r > 1.0 || g > 1.0 || b > 1.0))
            {
                return Invalid(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"Decoded pixel {pixel} contains a channel outside the legal " +
                    $"{(normalizedColor ? "normalized" : "linear-HDR")} range.",
                    sourceContentHash,
                    semantic,
                    colorSpace,
                    decoder);
            }

            AddCompensated(r, ref sums[0], ref compensatedSums[0]);
            AddCompensated(g, ref sums[1], ref compensatedSums[1]);
            AddCompensated(b, ref sums[2], ref compensatedSums[2]);
            AddCompensated(a, ref sums[3], ref compensatedSums[3]);
            AddCompensated(r * r, ref secondSums[0], ref compensatedSecondSums[0]);
            AddCompensated(g * g, ref secondSums[1], ref compensatedSecondSums[1]);
            AddCompensated(b * b, ref secondSums[2], ref compensatedSecondSums[2]);
            AddCompensated(a * a, ref secondSums[3], ref compensatedSecondSums[3]);

            double luminance = r * 0.2126 + g * 0.7152 + b * 0.0722;
            AddCompensated(luminance, ref luminanceSum, ref luminanceCompensation);
            AddCompensated(luminance * luminance, ref luminanceSecondSum, ref luminanceSecondCompensation);
            luminanceMaximum = Math.Max(luminanceMaximum, luminance);

            int alphaBin = Math.Clamp((int)Math.Round(Math.Clamp(a, 0.0, 1.0) * 255.0), 0, 255);
            alphaHistogram[alphaBin]++;

            if (semantic == TextureSemantic.Normal)
            {
                double x = r * 2.0 - 1.0;
                double y = g * 2.0 - 1.0;
                double z = b * 2.0 - 1.0;
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (length <= 1e-20)
                {
                    z = 1.0;
                    length = 1.0;
                }
                normalX += x / length;
                normalY += y / length;
                normalZ += z / length;
            }
        }

        double inverseCount = 1.0 / (width * (double)height);
        TextureTransportStatisticsValidity validity =
            TextureTransportStatisticsValidity.SourceContentHash |
            TextureTransportStatisticsValidity.DecodedPixels |
            TextureTransportStatisticsValidity.LinearChannelMoments |
            TextureTransportStatisticsValidity.EmissiveLuminanceMoments |
            TextureTransportStatisticsValidity.AlphaHistogram;
        double normalVariance = 0.0;
        if (semantic == TextureSemantic.Normal)
        {
            validity |= TextureTransportStatisticsValidity.NormalVariance;
            double meanX = normalX * inverseCount;
            double meanY = normalY * inverseCount;
            double meanZ = normalZ * inverseCount;
            normalVariance = Math.Clamp(1.0 - (meanX * meanX + meanY * meanY + meanZ * meanZ), 0.0, 1.0);
        }

        var result = new TextureTransportStatistics
        {
            Status = TextureTransportStatisticsStatus.Valid,
            Validity = validity,
            SourceContentHash = sourceContentHash,
            Semantic = semantic,
            ColorSpace = colorSpace,
            Width = width,
            Height = height,
            PixelCount = (long)width * height,
            LinearChannelMean = new TextureTransportVector4(
                sums[0] * inverseCount,
                sums[1] * inverseCount,
                sums[2] * inverseCount,
                sums[3] * inverseCount),
            LinearChannelSecondMoment = new TextureTransportVector4(
                secondSums[0] * inverseCount,
                secondSums[1] * inverseCount,
                secondSums[2] * inverseCount,
                secondSums[3] * inverseCount),
            EmissiveLuminanceMean = luminanceSum * inverseCount,
            EmissiveLuminanceSecondMoment = luminanceSecondSum * inverseCount,
            EmissiveLuminanceMaximum = luminanceMaximum,
            AlphaHistogram = alphaHistogram,
            NormalVariance = normalVariance,
            Decoder = decoder
        };
        result.EnsureValid("decoded image");
        return result;
    }

    private static void AddCompensated(double value, ref double sum, ref double compensation)
    {
        double adjusted = value - compensation;
        double next = sum + adjusted;
        compensation = (next - sum) - adjusted;
        sum = next;
    }

    private static void ValidateFinite(TextureTransportVector4 value, string name, ICollection<string> errors)
    {
        if (!IsFinite(value))
        {
            errors.Add($"{name} contains a non-finite channel.");
        }
    }

    private static bool IsFinite(TextureTransportVector4 value) =>
        double.IsFinite(value.X) &&
        double.IsFinite(value.Y) &&
        double.IsFinite(value.Z) &&
        double.IsFinite(value.W);

    private static void ValidateMoment(double mean, double secondMoment, string channel, ICollection<string> errors)
    {
        if (IsSecondMomentInvalid(mean, secondMoment))
            errors.Add($"The {channel} second moment is smaller than the squared mean.");
    }

    private static bool IsSecondMomentInvalid(double mean, double secondMoment)
    {
        double squaredMean = mean * mean;
        return secondMoment + RelativeTolerance(secondMoment, squaredMean) < squaredMean;
    }

    private static double RelativeTolerance(double first, double second) =>
        1e-12 * Math.Max(1.0, Math.Max(Math.Abs(first), Math.Abs(second)));

    private static void ValidateNormalized(TextureTransportVector4 value, string name, ICollection<string> errors)
    {
        if (value.X is < -1e-9 or > 1.0 + 1e-9 ||
            value.Y is < -1e-9 or > 1.0 + 1e-9 ||
            value.Z is < -1e-9 or > 1.0 + 1e-9 ||
            value.W is < -1e-9 or > 1.0 + 1e-9)
        {
            errors.Add($"{name} is outside normalized texture range.");
        }
    }

    private static void ValidateHdrLinear(
        TextureTransportVector4 value,
        string name,
        ICollection<string> errors)
    {
        if (value.X < -1e-9 ||
            value.Y < -1e-9 ||
            value.Z < -1e-9 ||
            value.W is < -1e-9 or > 1.0 + 1e-9)
        {
            errors.Add(
                $"{name} contains negative HDR radiance or non-normalized alpha.");
        }
    }

    private static void ValidateHdrSecondMoment(
        TextureTransportVector4 value,
        string name,
        ICollection<string> errors)
    {
        if (value.X < -1e-9 ||
            value.Y < -1e-9 ||
            value.Z < -1e-9 ||
            value.W is < -1e-9 or > 1.0 + 1e-9)
        {
            errors.Add(
                $"{name} contains a negative HDR moment or non-normalized alpha moment.");
        }
    }
}

/// <summary>
/// Source-resolution decoded pixels retained only during cooking so primitive
/// profiles can sample the same linear data used to produce persisted moments.
/// </summary>
public sealed class TextureTransportImage
{
    private readonly double[] _linearRgba;

    private TextureTransportImage(
        int width,
        int height,
        double[] linearRgba,
        TextureTransportStatistics statistics)
    {
        Width = width;
        Height = height;
        _linearRgba = linearRgba;
        Statistics = statistics;
    }

    public int Width { get; }
    public int Height { get; }
    public TextureTransportStatistics Statistics { get; }

    /// <summary>
    /// Creates a non-sampleable, hash-carrying input for a source that could
    /// not be decoded. Primitive profiles can therefore authenticate and
    /// invalidate on the exact source bytes without substituting neutral texels.
    /// </summary>
    public static TextureTransportImage Unavailable(TextureTransportStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (statistics.IsValid)
        {
            throw new ArgumentException(
                "Valid transport statistics require decoded source pixels.",
                nameof(statistics));
        }
        return new TextureTransportImage(0, 0, Array.Empty<double>(), statistics);
    }

    public static TextureTransportImage FromRgba8(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        TextureColorSpace colorSpace,
        TextureSemantic semantic,
        ulong sourceContentHash,
        string decoder = TextureTransportStatistics.StbDecoderVersion)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA8 data does not match its dimensions.", nameof(rgba));
        var linear = new double[rgba.Length];
        bool srgb = colorSpace == TextureColorSpace.Srgb;
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            linear[offset] = srgb ? TextureColorAverages.SrgbByteToLinear(rgba[offset]) : rgba[offset] / 255.0;
            linear[offset + 1] = srgb ? TextureColorAverages.SrgbByteToLinear(rgba[offset + 1]) : rgba[offset + 1] / 255.0;
            linear[offset + 2] = srgb ? TextureColorAverages.SrgbByteToLinear(rgba[offset + 2]) : rgba[offset + 2] / 255.0;
            linear[offset + 3] = rgba[offset + 3] / 255.0;
        }
        TextureTransportStatistics statistics = TextureTransportStatistics.Create(
            sourceContentHash, semantic, colorSpace, width, height, linear, decoder);
        return new TextureTransportImage(width, height, linear, statistics);
    }

    public static TextureTransportImage FromRgbaFloat(
        ReadOnlySpan<float> rgba,
        int width,
        int height,
        TextureColorSpace colorSpace,
        TextureSemantic semantic,
        ulong sourceContentHash,
        string decoder = TextureTransportStatistics.StbDecoderVersion)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA float data does not match its dimensions.", nameof(rgba));
        var linear = new double[rgba.Length];
        for (int i = 0; i < rgba.Length; i++)
            linear[i] = rgba[i];
        TextureTransportStatistics statistics = TextureTransportStatistics.Create(
            sourceContentHash, semantic, colorSpace, width, height, linear, decoder);
        return new TextureTransportImage(width, height, linear, statistics);
    }

    public TextureTransportVector4 Sample(ModelTextureSlot binding, Vector2 texCoord)
    {
        ArgumentNullException.ThrowIfNull(binding);
        double scaledX = texCoord.X * binding.Scale.X;
        double scaledY = texCoord.Y * binding.Scale.Y;
        double sine = Math.Sin(binding.RotationRadians);
        double cosine = Math.Cos(binding.RotationRadians);
        double u = binding.Offset.X + scaledX * cosine - scaledY * sine;
        double v = binding.Offset.Y + scaledX * sine + scaledY * cosine;

        return binding.Sampler.MagFilter == TextureFilterMode.Nearest
            ? SampleNearest(u, v, binding.Sampler.WrapU, binding.Sampler.WrapV)
            : SampleLinear(u, v, binding.Sampler.WrapU, binding.Sampler.WrapV);
    }

    /// <summary>
    /// Copies the decoded alpha channel as tightly packed FP32 values. This is
    /// intentionally an explicit copy: offline native bakers must never retain
    /// a pointer into the cooker's managed, double-precision working image.
    /// </summary>
    public void CopyAlphaTo(Span<float> destination)
    {
        int pixelCount = checked(Width * Height);
        if (_linearRgba.Length != checked(pixelCount * 4))
        {
            throw new InvalidOperationException(
                "Decoded texture pixels are unavailable or inconsistent with their dimensions.");
        }
        if (destination.Length != pixelCount)
        {
            throw new ArgumentException(
                $"Alpha destination contains {destination.Length} values; expected exactly {pixelCount}.",
                nameof(destination));
        }

        for (int pixel = 0, source = 3; pixel < pixelCount; pixel++, source += 4)
        {
            double alpha = _linearRgba[source];
            if (!double.IsFinite(alpha) || alpha is < 0.0 or > 1.0)
            {
                throw new InvalidDataException(
                    $"Decoded alpha sample {pixel} is not finite and normalized.");
            }
            destination[pixel] = (float)alpha;
        }
    }

    private TextureTransportVector4 SampleNearest(
        double u,
        double v,
        TextureWrapMode wrapU,
        TextureWrapMode wrapV)
    {
        int x = AddressIndex((int)Math.Floor(u * Width), Width, wrapU);
        int y = AddressIndex((int)Math.Floor(v * Height), Height, wrapV);
        return GetPixel(x, y);
    }

    private TextureTransportVector4 SampleLinear(
        double u,
        double v,
        TextureWrapMode wrapU,
        TextureWrapMode wrapV)
    {
        double x = u * Width - 0.5;
        double y = v * Height - 0.5;
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double tx = x - x0;
        double ty = y - y0;
        TextureTransportVector4 p00 = GetPixel(AddressIndex(x0, Width, wrapU), AddressIndex(y0, Height, wrapV));
        TextureTransportVector4 p10 = GetPixel(AddressIndex(x0 + 1, Width, wrapU), AddressIndex(y0, Height, wrapV));
        TextureTransportVector4 p01 = GetPixel(AddressIndex(x0, Width, wrapU), AddressIndex(y0 + 1, Height, wrapV));
        TextureTransportVector4 p11 = GetPixel(AddressIndex(x0 + 1, Width, wrapU), AddressIndex(y0 + 1, Height, wrapV));
        return Lerp(Lerp(p00, p10, tx), Lerp(p01, p11, tx), ty);
    }

    private TextureTransportVector4 GetPixel(int x, int y)
    {
        int offset = (y * Width + x) * 4;
        return new TextureTransportVector4(
            _linearRgba[offset],
            _linearRgba[offset + 1],
            _linearRgba[offset + 2],
            _linearRgba[offset + 3]);
    }

    private static int AddressIndex(int index, int size, TextureWrapMode mode)
    {
        if (size <= 1)
            return 0;
        return mode switch
        {
            TextureWrapMode.ClampToEdge => Math.Clamp(index, 0, size - 1),
            TextureWrapMode.Repeat => PositiveModulo(index, size),
            TextureWrapMode.MirroredRepeat => MirrorIndex(index, size),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported texture wrap mode.")
        };
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static int MirrorIndex(int value, int size)
    {
        int period = checked(size * 2);
        int mirrored = PositiveModulo(value, period);
        return mirrored < size ? mirrored : period - mirrored - 1;
    }

    private static TextureTransportVector4 Lerp(
        TextureTransportVector4 first,
        TextureTransportVector4 second,
        double amount) => new(
        first.X + (second.X - first.X) * amount,
        first.Y + (second.Y - first.Y) * amount,
        first.Z + (second.Z - first.Z) * amount,
        first.W + (second.W - first.W) * amount);
}

/// <summary>
/// Coverage preservation for normalized-alpha mip chains. The source histogram
/// remains untouched in transport statistics; only cooked mip alpha is scaled.
/// </summary>
public static class AlphaCoverageMipGenerator
{
    public static double CalculateCoverage(ReadOnlySpan<byte> rgba, double cutoff)
    {
        ValidateRgba(rgba);
        if (!double.IsFinite(cutoff))
            throw new ArgumentOutOfRangeException(nameof(cutoff), "Alpha cutoff must be finite.");
        if (cutoff <= 0.0)
            return 1.0;
        if (cutoff > 1.0)
            return 0.0;
        int threshold = Math.Clamp((int)Math.Ceiling(cutoff * 255.0 - 1e-12), 0, 255);
        int covered = 0;
        for (int offset = 3; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset] >= threshold)
                covered++;
        }
        return covered / (double)(rgba.Length / 4);
    }

    public static void PreserveCoverage(Span<byte> rgba, double cutoff, double targetCoverage)
    {
        ValidateRgba(rgba);
        if (!double.IsFinite(cutoff) || !double.IsFinite(targetCoverage) ||
            targetCoverage is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCoverage), "Cutoff and target coverage must be finite; coverage must be in [0, 1].");
        }
        if (cutoff <= 0.0 || cutoff > 1.0 || targetCoverage <= 0.0)
            return;

        double lower = 0.0;
        double upper = 16.0;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            double middle = (lower + upper) * 0.5;
            double coverage = CalculateScaledCoverage(rgba, cutoff, middle);
            if (coverage < targetCoverage)
                lower = middle;
            else
                upper = middle;
        }

        double lowerError = Math.Abs(CalculateScaledCoverage(rgba, cutoff, lower) - targetCoverage);
        double upperError = Math.Abs(CalculateScaledCoverage(rgba, cutoff, upper) - targetCoverage);
        double scale = upperError < lowerError ? upper : lower;
        for (int offset = 3; offset < rgba.Length; offset += 4)
            rgba[offset] = (byte)Math.Clamp((int)Math.Round(rgba[offset] * scale), 0, 255);
    }

    private static double CalculateScaledCoverage(ReadOnlySpan<byte> rgba, double cutoff, double scale)
    {
        int threshold = Math.Clamp((int)Math.Ceiling(cutoff * 255.0 - 1e-12), 0, 255);
        int covered = 0;
        for (int offset = 3; offset < rgba.Length; offset += 4)
        {
            int alpha = Math.Clamp((int)Math.Round(rgba[offset] * scale), 0, 255);
            if (alpha >= threshold)
                covered++;
        }
        return covered / (double)(rgba.Length / 4);
    }

    private static void ValidateRgba(ReadOnlySpan<byte> rgba)
    {
        if (rgba.IsEmpty || rgba.Length % 4 != 0)
            throw new ArgumentException("RGBA8 data must contain complete pixels.", nameof(rgba));
    }
}
