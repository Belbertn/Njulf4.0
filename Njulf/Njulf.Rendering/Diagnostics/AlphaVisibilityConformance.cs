using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Assets.Cooked;
using Njulf.Shaders;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Locked executable contract for comparing real masked raster coverage with
/// the DDGI ray-query candidate path. The gate intentionally measures binary
/// visibility; color and radiance are not accepted as coverage evidence.
/// </summary>
public static class AlphaVisibilityConformanceContract
{
    public const int ReportSchemaVersion = 2;
    public const int EvidenceSchemaVersion = 1;
    public const string GateId = "material-gi-alpha-visibility/v1";
    public const int Width = 256;
    public const int Height = 256;
    public const int TextureWidth = 512;
    public const int TextureHeight = 512;
    public const int TextureMipLevelCount = 7;
    public const float AlphaCutoff = 0.5f;
    public const float RayTextureLod = 0.0f;
    public const double MaximumCoverageDifference = 0.02;
    public const int MinimumCandidateSamples = 1024;
    public const int ResultPlaneCount = 4;
    public const int MaximumReportBytes = 1 * 1024 * 1024;
    public const int MaximumEvidenceBytes = 4 * 1024 * 1024;

    public const string VertexShaderResourceName =
        "alpha_visibility_conformance.vert";
    public const string FragmentShaderResourceName =
        "alpha_visibility_conformance.frag";
    public const string RayQueryShaderResourceName =
        "alpha_visibility_conformance.comp";

    private static readonly float[] LockedDistances = [2.0f, 4.0f, 8.0f];

    public static IReadOnlyList<float> Distances => LockedDistances;

    public static int SamplesPerDistance => checked(Width * Height);

    public static int TotalSamples => checked(SamplesPerDistance * LockedDistances.Length);

    public static int ResultWordCount => checked(TotalSamples * ResultPlaneCount);

    public static string ContractFingerprint { get; } = ComputeContractFingerprint();

    public static AlphaVisibilityTextureData CreateTextureData()
    {
        var mipLevels = new List<AlphaVisibilityTextureMip>(TextureMipLevelCount);
        byte[] level = CreateBaseTexture();
        double targetCoverage =
            AlphaCoverageMipGenerator.CalculateCoverage(level, AlphaCutoff);
        int width = TextureWidth;
        int height = TextureHeight;
        int byteOffset = 0;
        var contiguous = new List<byte>(CalculateTextureByteCount());

        for (int mip = 0; mip < TextureMipLevelCount; mip++)
        {
            if (mip > 0)
            {
                level = DownsampleRgba8(level, width, height);
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
                AlphaCoverageMipGenerator.PreserveCoverage(
                    level,
                    AlphaCutoff,
                    targetCoverage);
            }

            double coverage =
                AlphaCoverageMipGenerator.CalculateCoverage(level, AlphaCutoff);
            mipLevels.Add(new AlphaVisibilityTextureMip(
                mip,
                width,
                height,
                byteOffset,
                level.Length,
                coverage));
            contiguous.AddRange(level);
            byteOffset = checked(byteOffset + level.Length);
        }

        byte[] pixels = contiguous.ToArray();
        return new AlphaVisibilityTextureData(
            pixels,
            mipLevels,
            Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant(),
            targetCoverage);
    }

    public static IReadOnlyList<AlphaVisibilityShaderEvidence> LoadShaderEvidence()
    {
        string[] names =
        [
            VertexShaderResourceName,
            FragmentShaderResourceName,
            RayQueryShaderResourceName
        ];
        var evidence = new AlphaVisibilityShaderEvidence[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            byte[] bytes = LoadShaderBytes(names[index]);
            evidence[index] = new AlphaVisibilityShaderEvidence(
                names[index],
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        return evidence;
    }

    public static byte[] LoadShaderBytes(string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
            throw new ArgumentException("A shader resource name is required.", nameof(resourceName));
        if (resourceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            resourceName.Contains('/') ||
            resourceName.Contains('\\'))
        {
            throw new ArgumentException("Shader resource names must be safe file names.", nameof(resourceName));
        }

        string manifestName = $"Njulf.Shaders.{resourceName}";
        using Stream stream =
            typeof(ShaderLibrary).Assembly.GetManifestResourceStream(manifestName)
            ?? throw new InvalidOperationException(
                $"Embedded alpha-visibility shader '{manifestName}' is unavailable.");
        if (stream.Length <= 0 || stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException($"Shader '{manifestName}' has an invalid byte length.");

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if ((bytes.Length & 3) != 0)
            throw new InvalidDataException($"Shader '{manifestName}' is not valid SPIR-V word data.");
        return bytes;
    }

    private static string ComputeContractFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, GateId);
        AppendInt32(hash, ReportSchemaVersion);
        AppendInt32(hash, EvidenceSchemaVersion);
        AppendInt32(hash, Width);
        AppendInt32(hash, Height);
        AppendInt32(hash, TextureWidth);
        AppendInt32(hash, TextureHeight);
        AppendInt32(hash, TextureMipLevelCount);
        AppendInt32(hash, BitConverter.SingleToInt32Bits(AlphaCutoff));
        AppendInt32(hash, BitConverter.SingleToInt32Bits(RayTextureLod));
        AppendInt64(hash, BitConverter.DoubleToInt64Bits(MaximumCoverageDifference));
        AppendInt32(hash, MinimumCandidateSamples);
        foreach (float distance in LockedDistances)
            AppendInt32(hash, BitConverter.SingleToInt32Bits(distance));
        AppendUtf8(hash, VertexShaderResourceName);
        AppendUtf8(hash, FragmentShaderResourceName);
        AppendUtf8(hash, RayQueryShaderResourceName);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] CreateBaseTexture()
    {
        var pixels = new byte[checked(TextureWidth * TextureHeight * 4)];
        for (int y = 0; y < TextureHeight; y++)
        {
            for (int x = 0; x < TextureWidth; x++)
            {
                double u = (x + 0.5) / TextureWidth;
                double v = (y + 0.5) / TextureHeight;
                double centeredX = u * 2.0 - 1.0;
                double centeredY = v * 2.0 - 1.0;
                double radial = centeredX * centeredX + centeredY * centeredY;
                double wave =
                    Math.Sin(u * Math.PI * 18.0) * 0.32 +
                    Math.Cos(v * Math.PI * 14.0) * 0.26 +
                    Math.Sin((u + v) * Math.PI * 10.0) * 0.18;
                double field =
                    0.52 - radial * 0.46 + wave +
                    Math.Sin((u - v) * Math.PI * 6.0) * 0.12;
                byte alpha = field >= 0.0 ? byte.MaxValue : (byte)0;
                int offset = checked((y * TextureWidth + x) * 4);
                pixels[offset + 0] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = alpha;
            }
        }
        return pixels;
    }

    private static byte[] DownsampleRgba8(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight)
    {
        int targetWidth = Math.Max(1, sourceWidth / 2);
        int targetHeight = Math.Max(1, sourceHeight / 2);
        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX0 = Math.Min(sourceWidth - 1, x * 2);
                int sourceY0 = Math.Min(sourceHeight - 1, y * 2);
                int sourceX1 = Math.Min(sourceWidth - 1, sourceX0 + 1);
                int sourceY1 = Math.Min(sourceHeight - 1, sourceY0 + 1);
                int targetOffset = checked((y * targetWidth + x) * 4);
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum =
                        source[(sourceY0 * sourceWidth + sourceX0) * 4 + channel] +
                        source[(sourceY0 * sourceWidth + sourceX1) * 4 + channel] +
                        source[(sourceY1 * sourceWidth + sourceX0) * 4 + channel] +
                        source[(sourceY1 * sourceWidth + sourceX1) * 4 + channel];
                    target[targetOffset + channel] =
                        checked((byte)((sum + 2) / 4));
                }
            }
        }
        return target;
    }

    private static int CalculateTextureByteCount()
    {
        int count = 0;
        int width = TextureWidth;
        int height = TextureHeight;
        for (int mip = 0; mip < TextureMipLevelCount; mip++)
        {
            count = checked(count + width * height * 4);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return count;
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

public sealed record AlphaVisibilityTextureMip(
    int Level,
    int Width,
    int Height,
    int ByteOffset,
    int ByteLength,
    double Coverage);

public sealed record AlphaVisibilityTextureData(
    byte[] Pixels,
    IReadOnlyList<AlphaVisibilityTextureMip> MipLevels,
    string Sha256,
    double BaseCoverage);

public sealed record AlphaVisibilityShaderEvidence(
    string ResourceName,
    long ByteLength,
    string Sha256);

public sealed record AlphaVisibilityDistanceResult(
    float Distance,
    float RayTextureLod,
    int RasterCandidateCount,
    int RasterCoveredCount,
    double RasterCoverage,
    int RayCandidateCount,
    int RayCoveredCount,
    double RayCoverage,
    double AbsoluteCoverageDifference,
    bool Passed);

public sealed record AlphaVisibilityEvidenceReference(
    string FileName,
    long ByteLength,
    string Sha256);

public sealed record AlphaVisibilityValidationMessage(
    string Severity,
    uint MessageTypes,
    int MessageIdNumber,
    string MessageIdName,
    string Message,
    bool TextTruncated);

public sealed record AlphaVisibilityConformanceReport(
    int SchemaVersion,
    string GateId,
    string Status,
    string ContractFingerprint,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string DeviceName,
    uint DeviceApiVersion,
    uint DriverVersion,
    bool ValidationEnabled,
    int ValidationWarningCount,
    int ValidationErrorCount,
    string FirstValidationError,
    IReadOnlyList<AlphaVisibilityValidationMessage> ValidationMessages,
    bool ValidationMessagesTruncated,
    string InputTextureSha256,
    IReadOnlyList<AlphaVisibilityShaderEvidence> Shaders,
    double MaximumCoverageDifference,
    int MinimumCandidateSamples,
    AlphaVisibilityEvidenceReference? Evidence,
    string EvidenceAuthenticationSha256,
    IReadOnlyList<AlphaVisibilityDistanceResult> Distances,
    IReadOnlyList<string> Failures);

public sealed record AlphaVisibilityHardwareOutput(
    string DeviceName,
    uint DeviceApiVersion,
    uint DriverVersion,
    bool ValidationEnabled,
    int ValidationWarningCount,
    int ValidationErrorCount,
    string FirstValidationError,
    IReadOnlyList<AlphaVisibilityValidationMessage> ValidationMessages,
    bool ValidationMessagesTruncated,
    uint[] ResultWords);

public sealed record AlphaVisibilityRawEvidence(
    byte[] RasterCandidates,
    byte[] RasterCovered,
    byte[] RayCandidates,
    byte[] RayCovered)
{
    public static AlphaVisibilityRawEvidence FromGpuWords(ReadOnlySpan<uint> words)
    {
        if (words.Length != AlphaVisibilityConformanceContract.ResultWordCount)
        {
            throw new InvalidDataException(
                $"Alpha-visibility GPU output contains {words.Length} words; " +
                $"{AlphaVisibilityConformanceContract.ResultWordCount} are required.");
        }

        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        byte[] rasterCandidates = ConvertPlane(words[..samples], "raster candidate");
        byte[] rasterCovered = ConvertPlane(words.Slice(samples, samples), "raster covered");
        byte[] rayCandidates = ConvertPlane(words.Slice(samples * 2, samples), "ray candidate");
        byte[] rayCovered = ConvertPlane(words.Slice(samples * 3, samples), "ray covered");
        return CreateValidated(
            rasterCandidates,
            rasterCovered,
            rayCandidates,
            rayCovered);
    }

    public static AlphaVisibilityRawEvidence CreateValidated(
        byte[] rasterCandidates,
        byte[] rasterCovered,
        byte[] rayCandidates,
        byte[] rayCovered)
    {
        ArgumentNullException.ThrowIfNull(rasterCandidates);
        ArgumentNullException.ThrowIfNull(rasterCovered);
        ArgumentNullException.ThrowIfNull(rayCandidates);
        ArgumentNullException.ThrowIfNull(rayCovered);
        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        if (rasterCandidates.Length != samples ||
            rasterCovered.Length != samples ||
            rayCandidates.Length != samples ||
            rayCovered.Length != samples)
        {
            throw new InvalidDataException(
                $"Every alpha-visibility evidence plane must contain exactly {samples} samples.");
        }

        ValidatePlane(rasterCandidates, "raster candidate");
        ValidatePlane(rasterCovered, "raster covered");
        ValidatePlane(rayCandidates, "ray candidate");
        ValidatePlane(rayCovered, "ray covered");
        for (int index = 0; index < samples; index++)
        {
            if (rasterCovered[index] > rasterCandidates[index])
                throw new InvalidDataException("Raster coverage exists outside raster candidate geometry.");
            if (rayCovered[index] > rayCandidates[index])
                throw new InvalidDataException("Ray coverage exists without a ray-query triangle candidate.");
        }

        return new AlphaVisibilityRawEvidence(
            (byte[])rasterCandidates.Clone(),
            (byte[])rasterCovered.Clone(),
            (byte[])rayCandidates.Clone(),
            (byte[])rayCovered.Clone());
    }

    private static byte[] ConvertPlane(ReadOnlySpan<uint> words, string name)
    {
        var plane = new byte[words.Length];
        for (int index = 0; index < words.Length; index++)
        {
            uint value = words[index];
            if (value > 1u)
                throw new InvalidDataException($"The {name} plane contains non-binary value {value}.");
            plane[index] = (byte)value;
        }
        return plane;
    }

    private static void ValidatePlane(ReadOnlySpan<byte> plane, string name)
    {
        foreach (byte value in plane)
        {
            if (value > 1)
                throw new InvalidDataException($"The {name} plane contains non-binary value {value}.");
        }
    }
}

public static class AlphaVisibilityConformanceEvaluator
{
    public static IReadOnlyList<AlphaVisibilityDistanceResult> Evaluate(
        AlphaVisibilityRawEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        int samples = AlphaVisibilityConformanceContract.SamplesPerDistance;
        var results =
            new AlphaVisibilityDistanceResult[AlphaVisibilityConformanceContract.Distances.Count];
        for (int distanceIndex = 0; distanceIndex < results.Length; distanceIndex++)
        {
            int start = checked(distanceIndex * samples);
            int rasterCandidates = Count(evidence.RasterCandidates, start, samples);
            int rasterCovered = Count(evidence.RasterCovered, start, samples);
            int rayCandidates = Count(evidence.RayCandidates, start, samples);
            int rayCovered = Count(evidence.RayCovered, start, samples);
            double rasterCoverage =
                rasterCandidates == 0 ? 0.0 : rasterCovered / (double)rasterCandidates;
            double rayCoverage =
                rayCandidates == 0 ? 0.0 : rayCovered / (double)rayCandidates;
            double difference = Math.Abs(rasterCoverage - rayCoverage);
            bool passed =
                rasterCandidates >= AlphaVisibilityConformanceContract.MinimumCandidateSamples &&
                rayCandidates >= AlphaVisibilityConformanceContract.MinimumCandidateSamples &&
                rasterCovered <= rasterCandidates &&
                rayCovered <= rayCandidates &&
                IsWithinMaximumDifference(
                    rasterCandidates,
                    rasterCovered,
                    rayCandidates,
                    rayCovered);
            results[distanceIndex] = new AlphaVisibilityDistanceResult(
                AlphaVisibilityConformanceContract.Distances[distanceIndex],
                AlphaVisibilityConformanceContract.RayTextureLod,
                rasterCandidates,
                rasterCovered,
                rasterCoverage,
                rayCandidates,
                rayCovered,
                rayCoverage,
                difference,
                passed);
        }
        return results;
    }

    private static int Count(ReadOnlySpan<byte> values, int start, int count)
    {
        int total = 0;
        ReadOnlySpan<byte> range = values.Slice(start, count);
        foreach (byte value in range)
            total = checked(total + value);
        return total;
    }

    private static bool IsWithinMaximumDifference(
        int rasterCandidates,
        int rasterCovered,
        int rayCandidates,
        int rayCovered)
    {
        if (rasterCandidates <= 0 || rayCandidates <= 0)
            return false;

        // The locked threshold is exactly 2/100. Compare the two rational
        // coverages with integer arithmetic so an exact 2% result cannot fail
        // because of a binary floating-point rounding bit.
        long numerator = Math.Abs(
            checked(
                (long)rasterCovered * rayCandidates -
                (long)rayCovered * rasterCandidates));
        long denominator =
            checked((long)rasterCandidates * rayCandidates);
        return checked(numerator * 100L) <= checked(denominator * 2L);
    }
}

public static class AlphaVisibilityEvidenceCodec
{
    private static ReadOnlySpan<byte> Magic => "NJALPHV1"u8;
    private const int HeaderBytes = 8 + sizeof(int) * 5 + 32;

    public static byte[] Encode(AlphaVisibilityRawEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _ = AlphaVisibilityRawEvidence.CreateValidated(
            evidence.RasterCandidates,
            evidence.RasterCovered,
            evidence.RayCandidates,
            evidence.RayCovered);

        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        int packedPlaneBytes = checked((samples + 7) / 8);
        int distanceMetadataBytes =
            checked(AlphaVisibilityConformanceContract.Distances.Count * sizeof(float) * 2);
        var output = new byte[checked(
            HeaderBytes +
            distanceMetadataBytes +
            packedPlaneBytes * AlphaVisibilityConformanceContract.ResultPlaneCount)];
        Span<byte> destination = output;
        Magic.CopyTo(destination);
        int offset = Magic.Length;
        WriteInt32(destination, ref offset, AlphaVisibilityConformanceContract.EvidenceSchemaVersion);
        WriteInt32(destination, ref offset, AlphaVisibilityConformanceContract.Width);
        WriteInt32(destination, ref offset, AlphaVisibilityConformanceContract.Height);
        WriteInt32(destination, ref offset, AlphaVisibilityConformanceContract.Distances.Count);
        WriteInt32(destination, ref offset, AlphaVisibilityConformanceContract.ResultPlaneCount);
        Convert.FromHexString(AlphaVisibilityConformanceContract.ContractFingerprint)
            .CopyTo(destination[offset..]);
        offset += 32;
        foreach (float distance in AlphaVisibilityConformanceContract.Distances)
        {
            WriteInt32(destination, ref offset, BitConverter.SingleToInt32Bits(distance));
            WriteInt32(
                destination,
                ref offset,
                BitConverter.SingleToInt32Bits(
                    AlphaVisibilityConformanceContract.RayTextureLod));
        }

        PackPlane(evidence.RasterCandidates, destination.Slice(offset, packedPlaneBytes));
        offset += packedPlaneBytes;
        PackPlane(evidence.RasterCovered, destination.Slice(offset, packedPlaneBytes));
        offset += packedPlaneBytes;
        PackPlane(evidence.RayCandidates, destination.Slice(offset, packedPlaneBytes));
        offset += packedPlaneBytes;
        PackPlane(evidence.RayCovered, destination.Slice(offset, packedPlaneBytes));
        offset += packedPlaneBytes;
        if (offset != output.Length)
            throw new InvalidOperationException("Alpha-visibility evidence encoder length drifted.");
        return output;
    }

    public static AlphaVisibilityRawEvidence Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > AlphaVisibilityConformanceContract.MaximumEvidenceBytes)
            throw new InvalidDataException("Alpha-visibility evidence has an invalid bounded length.");
        if (!bytes[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Alpha-visibility evidence magic is invalid.");

        int offset = Magic.Length;
        RequireInt32(
            bytes,
            ref offset,
            AlphaVisibilityConformanceContract.EvidenceSchemaVersion,
            "schema version");
        RequireInt32(bytes, ref offset, AlphaVisibilityConformanceContract.Width, "width");
        RequireInt32(bytes, ref offset, AlphaVisibilityConformanceContract.Height, "height");
        RequireInt32(
            bytes,
            ref offset,
            AlphaVisibilityConformanceContract.Distances.Count,
            "distance count");
        RequireInt32(
            bytes,
            ref offset,
            AlphaVisibilityConformanceContract.ResultPlaneCount,
            "plane count");
        byte[] expectedFingerprint =
            Convert.FromHexString(AlphaVisibilityConformanceContract.ContractFingerprint);
        if (!bytes.Slice(offset, expectedFingerprint.Length)
                .SequenceEqual(expectedFingerprint))
        {
            throw new InvalidDataException("Alpha-visibility evidence contract fingerprint is invalid.");
        }
        offset += expectedFingerprint.Length;

        foreach (float distance in AlphaVisibilityConformanceContract.Distances)
        {
            RequireInt32(
                bytes,
                ref offset,
                BitConverter.SingleToInt32Bits(distance),
                "distance");
            RequireInt32(
                bytes,
                ref offset,
                BitConverter.SingleToInt32Bits(
                    AlphaVisibilityConformanceContract.RayTextureLod),
                "ray texture LOD");
        }

        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        int packedPlaneBytes = checked((samples + 7) / 8);
        int expectedLength = checked(offset + packedPlaneBytes * 4);
        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Alpha-visibility evidence length {bytes.Length} does not match {expectedLength}.");
        }

        byte[] rasterCandidates = UnpackPlane(bytes.Slice(offset, packedPlaneBytes), samples);
        offset += packedPlaneBytes;
        byte[] rasterCovered = UnpackPlane(bytes.Slice(offset, packedPlaneBytes), samples);
        offset += packedPlaneBytes;
        byte[] rayCandidates = UnpackPlane(bytes.Slice(offset, packedPlaneBytes), samples);
        offset += packedPlaneBytes;
        byte[] rayCovered = UnpackPlane(bytes.Slice(offset, packedPlaneBytes), samples);
        return AlphaVisibilityRawEvidence.CreateValidated(
            rasterCandidates,
            rasterCovered,
            rayCandidates,
            rayCovered);
    }

    private static void PackPlane(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        destination.Clear();
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] != 0)
                destination[index >> 3] |= checked((byte)(1 << (index & 7)));
        }
    }

    private static byte[] UnpackPlane(ReadOnlySpan<byte> source, int sampleCount)
    {
        var destination = new byte[sampleCount];
        for (int index = 0; index < sampleCount; index++)
            destination[index] = (byte)((source[index >> 3] >> (index & 7)) & 1);

        int usedBits = sampleCount & 7;
        if (usedBits != 0)
        {
            int invalidMask = ~((1 << usedBits) - 1) & 0xff;
            if ((source[^1] & invalidMask) != 0)
                throw new InvalidDataException("Alpha-visibility evidence contains nonzero padding bits.");
        }
        return destination;
    }

    private static void WriteInt32(Span<byte> destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(offset, sizeof(int)),
            value);
        offset += sizeof(int);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset > source.Length - sizeof(int))
            throw new InvalidDataException("Alpha-visibility evidence is truncated.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(
            source.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static void RequireInt32(
        ReadOnlySpan<byte> source,
        ref int offset,
        int expected,
        string name)
    {
        int actual = ReadInt32(source, ref offset);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Alpha-visibility evidence {name} {actual} does not match {expected}.");
        }
    }
}

public static class AlphaVisibilityConformanceReports
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    public static AlphaVisibilityConformanceReport Create(
        DateTimeOffset startedAtUtc,
        DateTimeOffset finishedAtUtc,
        AlphaVisibilityHardwareOutput hardware,
        string evidenceFileName,
        byte[] evidenceBytes)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceFileName);
        ArgumentNullException.ThrowIfNull(evidenceBytes);
        if (Path.GetFileName(evidenceFileName) != evidenceFileName)
            throw new ArgumentException("Evidence file name must not contain a path.", nameof(evidenceFileName));

        AlphaVisibilityRawEvidence evidence =
            AlphaVisibilityEvidenceCodec.Decode(evidenceBytes);
        AlphaVisibilityRawEvidence hardwareEvidence =
            AlphaVisibilityRawEvidence.FromGpuWords(hardware.ResultWords);
        byte[] hardwareEvidenceBytes =
            AlphaVisibilityEvidenceCodec.Encode(hardwareEvidence);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(evidenceBytes),
                SHA256.HashData(hardwareEvidenceBytes)))
        {
            throw new InvalidDataException(
                "Alpha-visibility artifact does not match the supplied GPU readback.");
        }
        ValidateHardwareValidationDiagnostics(hardware);
        IReadOnlyList<AlphaVisibilityDistanceResult> distances =
            AlphaVisibilityConformanceEvaluator.Evaluate(evidence);
        AlphaVisibilityTextureData texture =
            AlphaVisibilityConformanceContract.CreateTextureData();
        IReadOnlyList<AlphaVisibilityShaderEvidence> shaders =
            AlphaVisibilityConformanceContract.LoadShaderEvidence();
        string evidenceSha256 =
            Convert.ToHexString(SHA256.HashData(evidenceBytes)).ToLowerInvariant();
        string authentication = ComputeAuthenticationDigest(
            texture.Sha256,
            shaders,
            evidenceSha256,
            evidenceBytes.LongLength);
        var failures = new List<string>();
        if (!hardware.ValidationEnabled)
            failures.Add("Vulkan validation was not enabled.");
        if (hardware.ValidationWarningCount != 0)
            failures.Add($"Vulkan validation emitted {hardware.ValidationWarningCount} warning(s).");
        if (hardware.ValidationErrorCount != 0)
            failures.Add($"Vulkan validation emitted {hardware.ValidationErrorCount} error(s).");
        if (hardware.ValidationMessagesTruncated ||
            hardware.ValidationMessages.Any(
                static message => message.TextTruncated))
        {
            failures.Add("Vulkan validation diagnostics were truncated.");
        }
        if (string.IsNullOrWhiteSpace(hardware.DeviceName))
            failures.Add("Vulkan device identity is missing.");
        foreach (AlphaVisibilityDistanceResult distance in distances)
        {
            if (!distance.Passed)
            {
                failures.Add(
                    $"Distance {distance.Distance:R} failed: raster={distance.RasterCoverage:R}, " +
                    $"ray={distance.RayCoverage:R}, difference={distance.AbsoluteCoverageDifference:R}, " +
                    $"rasterCandidates={distance.RasterCandidateCount}, " +
                    $"rayCandidates={distance.RayCandidateCount}.");
            }
        }

        bool passed = failures.Count == 0;
        return new AlphaVisibilityConformanceReport(
            AlphaVisibilityConformanceContract.ReportSchemaVersion,
            AlphaVisibilityConformanceContract.GateId,
            passed ? "Passed" : "Failed",
            AlphaVisibilityConformanceContract.ContractFingerprint,
            startedAtUtc,
            finishedAtUtc,
            hardware.DeviceName,
            hardware.DeviceApiVersion,
            hardware.DriverVersion,
            hardware.ValidationEnabled,
            hardware.ValidationWarningCount,
            hardware.ValidationErrorCount,
            RetainOptionalDiagnostic(hardware.FirstValidationError),
            hardware.ValidationMessages,
            hardware.ValidationMessagesTruncated,
            texture.Sha256,
            shaders,
            AlphaVisibilityConformanceContract.MaximumCoverageDifference,
            AlphaVisibilityConformanceContract.MinimumCandidateSamples,
            new AlphaVisibilityEvidenceReference(
                evidenceFileName,
                evidenceBytes.LongLength,
                evidenceSha256),
            authentication,
            distances,
            failures);
    }

    public static AlphaVisibilityConformanceReport CreateFailed(
        DateTimeOffset startedAtUtc,
        DateTimeOffset finishedAtUtc,
        string failure)
    {
        string retainedFailure = RetainFailure(failure);
        return new AlphaVisibilityConformanceReport(
            AlphaVisibilityConformanceContract.ReportSchemaVersion,
            AlphaVisibilityConformanceContract.GateId,
            "Failed",
            AlphaVisibilityConformanceContract.ContractFingerprint,
            startedAtUtc,
            finishedAtUtc,
            string.Empty,
            0,
            0,
            false,
            0,
            0,
            string.Empty,
            Array.Empty<AlphaVisibilityValidationMessage>(),
            false,
            AlphaVisibilityConformanceContract.CreateTextureData().Sha256,
            AlphaVisibilityConformanceContract.LoadShaderEvidence(),
            AlphaVisibilityConformanceContract.MaximumCoverageDifference,
            AlphaVisibilityConformanceContract.MinimumCandidateSamples,
            null,
            string.Empty,
            Array.Empty<AlphaVisibilityDistanceResult>(),
            [retainedFailure]);
    }

    public static void WriteAtomically(
        string reportPath,
        AlphaVisibilityConformanceReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(report);
        string fullPath = Path.GetFullPath(reportPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Report path has no parent directory.");
        Directory.CreateDirectory(directory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (json.Length > AlphaVisibilityConformanceContract.MaximumReportBytes)
            throw new InvalidDataException("Alpha-visibility report exceeds its bounded size.");
        WriteBytesAtomically(fullPath, json);
        AlphaVisibilityConformanceReport published = ReadReport(fullPath);
        if (published.SchemaVersion != report.SchemaVersion ||
            !string.Equals(published.Status, report.Status, StringComparison.Ordinal))
        {
            throw new IOException("Published alpha-visibility report failed post-write verification.");
        }
    }

    public static void WriteEvidenceAtomically(string evidencePath, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length <= 0 ||
            bytes.Length > AlphaVisibilityConformanceContract.MaximumEvidenceBytes)
        {
            throw new InvalidDataException("Alpha-visibility evidence has an invalid bounded size.");
        }
        string fullPath = Path.GetFullPath(evidencePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Evidence path has no parent directory.");
        Directory.CreateDirectory(directory);
        WriteBytesAtomically(fullPath, bytes);
        byte[] published = ReadBoundedFile(
            fullPath,
            AlphaVisibilityConformanceContract.MaximumEvidenceBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                SHA256.HashData(published)))
        {
            throw new IOException("Published alpha-visibility evidence failed post-write verification.");
        }
    }

    public static AlphaVisibilityConformanceReport AuthenticatePassed(
        string reportPath,
        string evidencePath)
    {
        byte[] reportBytes = ReadBoundedFile(
            reportPath,
            AlphaVisibilityConformanceContract.MaximumReportBytes);
        byte[] evidenceBytes = ReadBoundedFile(
            evidencePath,
            AlphaVisibilityConformanceContract.MaximumEvidenceBytes);
        return AuthenticatePassed(
            reportBytes,
            Path.GetFullPath(reportPath),
            evidenceBytes,
            Path.GetFullPath(evidencePath));
    }

    internal static AlphaVisibilityConformanceReport AuthenticatePassed(
        ReadOnlySpan<byte> reportBytes,
        string reportPath,
        ReadOnlySpan<byte> evidenceBytes,
        string evidencePath)
    {
        AlphaVisibilityConformanceReport report =
            DeserializeReport(reportBytes, "Alpha-visibility report");
        if (!string.Equals(report.Status, "Passed", StringComparison.Ordinal))
            throw new InvalidDataException($"Alpha-visibility gate status is '{report.Status}', not Passed.");
        ValidateCommonReport(report);
        if (!report.ValidationEnabled ||
            report.ValidationWarningCount != 0 ||
            report.ValidationErrorCount != 0)
        {
            throw new InvalidDataException("Passed alpha-visibility evidence requires clean Vulkan validation.");
        }
        if (!string.IsNullOrEmpty(report.FirstValidationError) ||
            report.ValidationMessages is null ||
            report.ValidationMessages.Count != 0 ||
            report.ValidationMessagesTruncated)
        {
            throw new InvalidDataException(
                "Passed alpha-visibility evidence contains validation diagnostics.");
        }
        if (string.IsNullOrWhiteSpace(report.DeviceName) ||
            report.DeviceApiVersion == 0 ||
            report.DriverVersion == 0)
        {
            throw new InvalidDataException("Passed alpha-visibility evidence has incomplete device identity.");
        }
        if (report.Failures is null || report.Failures.Count != 0)
            throw new InvalidDataException("Passed alpha-visibility evidence contains failures.");
        if (report.Evidence is null)
            throw new InvalidDataException("Passed alpha-visibility evidence has no artifact reference.");

        string fullEvidencePath = Path.GetFullPath(evidencePath);
        if (!string.Equals(
                Path.GetFileName(fullEvidencePath),
                report.Evidence.FileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Alpha-visibility evidence file name does not match the report.");
        }
        if (evidenceBytes.Length != report.Evidence.ByteLength)
            throw new InvalidDataException("Alpha-visibility evidence byte length does not match the report.");
        RequireSha256(
            evidenceBytes,
            report.Evidence.Sha256,
            "alpha-visibility evidence");
        AlphaVisibilityRawEvidence evidence =
            AlphaVisibilityEvidenceCodec.Decode(evidenceBytes);
        IReadOnlyList<AlphaVisibilityDistanceResult> recomputed =
            AlphaVisibilityConformanceEvaluator.Evaluate(evidence);
        ValidateDistanceResults(report.Distances, recomputed);
        if (recomputed.Any(static result => !result.Passed))
            throw new InvalidDataException("Alpha-visibility evidence exceeds the 2% acceptance limit.");

        string authentication = ComputeAuthenticationDigest(
            report.InputTextureSha256,
            report.Shaders,
            report.Evidence.Sha256,
            report.Evidence.ByteLength);
        RequireFixedSha256(
            authentication,
            report.EvidenceAuthenticationSha256,
            "alpha-visibility authentication digest");
        return report;
    }

    public static AlphaVisibilityConformanceReport ReadReport(string reportPath)
    {
        byte[] json = ReadBoundedFile(
            reportPath,
            AlphaVisibilityConformanceContract.MaximumReportBytes);
        return DeserializeReport(json, "Alpha-visibility report");
    }

    private static AlphaVisibilityConformanceReport DeserializeReport(
        ReadOnlySpan<byte> json,
        string role)
    {
        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                json,
                JsonOptions.MaxDepth,
                role);
            return JsonSerializer.Deserialize<AlphaVisibilityConformanceReport>(
                    json,
                    JsonOptions)
                ?? throw new InvalidDataException("Alpha-visibility report JSON is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Alpha-visibility report JSON is invalid.", exception);
        }
    }

    private static void ValidateCommonReport(AlphaVisibilityConformanceReport report)
    {
        if (report.SchemaVersion != AlphaVisibilityConformanceContract.ReportSchemaVersion)
            throw new InvalidDataException("Alpha-visibility report schema version is unsupported.");
        if (!string.Equals(
                report.GateId,
                AlphaVisibilityConformanceContract.GateId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Alpha-visibility report gate ID is invalid.");
        }
        if (!string.Equals(
                report.ContractFingerprint,
                AlphaVisibilityConformanceContract.ContractFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Alpha-visibility report contract fingerprint is invalid.");
        }
        if (report.FinishedAtUtc < report.StartedAtUtc)
            throw new InvalidDataException("Alpha-visibility report timestamps are reversed.");
        if (BitConverter.DoubleToInt64Bits(report.MaximumCoverageDifference) !=
            BitConverter.DoubleToInt64Bits(
                AlphaVisibilityConformanceContract.MaximumCoverageDifference))
        {
            throw new InvalidDataException("Alpha-visibility acceptance threshold was modified.");
        }
        if (report.MinimumCandidateSamples !=
            AlphaVisibilityConformanceContract.MinimumCandidateSamples)
        {
            throw new InvalidDataException("Alpha-visibility minimum sample count was modified.");
        }

        AlphaVisibilityTextureData texture =
            AlphaVisibilityConformanceContract.CreateTextureData();
        RequireFixedSha256(
            texture.Sha256,
            report.InputTextureSha256,
            "alpha-visibility input texture");
        ValidateShaderEvidence(
            report.Shaders,
            AlphaVisibilityConformanceContract.LoadShaderEvidence());
    }

    private static void ValidateHardwareValidationDiagnostics(
        AlphaVisibilityHardwareOutput hardware)
    {
        if (hardware.ValidationWarningCount < 0 ||
            hardware.ValidationErrorCount < 0)
        {
            throw new InvalidDataException(
                "Vulkan validation callback counts cannot be negative.");
        }
        if (hardware.ValidationMessages is null)
        {
            throw new InvalidDataException(
                "Vulkan validation callback diagnostics are missing.");
        }

        int retainedWarnings = 0;
        int retainedErrors = 0;
        string firstRetainedError = string.Empty;
        foreach (AlphaVisibilityValidationMessage message in
                 hardware.ValidationMessages)
        {
            if (message is null)
            {
                throw new InvalidDataException(
                    "Vulkan validation callback diagnostics contain a null entry.");
            }
            if (string.Equals(message.Severity, "Warning", StringComparison.Ordinal))
            {
                retainedWarnings = checked(retainedWarnings + 1);
            }
            else if (string.Equals(message.Severity, "Error", StringComparison.Ordinal))
            {
                retainedErrors = checked(retainedErrors + 1);
                if (firstRetainedError.Length == 0)
                    firstRetainedError = message.Message;
            }
            else
            {
                throw new InvalidDataException(
                    $"Vulkan validation callback severity '{message.Severity}' is invalid.");
            }

            if (message.MessageIdName is null || message.Message is null)
            {
                throw new InvalidDataException(
                    "Vulkan validation callback text must not be null.");
            }
        }

        if (retainedWarnings > hardware.ValidationWarningCount ||
            retainedErrors > hardware.ValidationErrorCount ||
            (!hardware.ValidationMessagesTruncated &&
             (retainedWarnings != hardware.ValidationWarningCount ||
              retainedErrors != hardware.ValidationErrorCount)))
        {
            throw new InvalidDataException(
                "Vulkan validation callback diagnostics do not match their counts.");
        }
        if (hardware.ValidationErrorCount == 0)
        {
            if (!string.IsNullOrEmpty(hardware.FirstValidationError))
            {
                throw new InvalidDataException(
                    "Vulkan validation retained an error without an error callback.");
            }
        }
        else if (firstRetainedError.Length != 0 &&
                 !string.Equals(
                     firstRetainedError,
                     hardware.FirstValidationError,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The first Vulkan validation error does not match retained diagnostics.");
        }
    }

    private static void ValidateShaderEvidence(
        IReadOnlyList<AlphaVisibilityShaderEvidence>? actual,
        IReadOnlyList<AlphaVisibilityShaderEvidence> expected)
    {
        if (actual is null || actual.Count != expected.Count)
            throw new InvalidDataException("Alpha-visibility shader evidence set is incomplete.");
        for (int index = 0; index < expected.Count; index++)
        {
            AlphaVisibilityShaderEvidence actualShader = actual[index];
            AlphaVisibilityShaderEvidence expectedShader = expected[index];
            if (!string.Equals(
                    actualShader.ResourceName,
                    expectedShader.ResourceName,
                    StringComparison.Ordinal) ||
                actualShader.ByteLength != expectedShader.ByteLength)
            {
                throw new InvalidDataException("Alpha-visibility shader evidence identity is invalid.");
            }
            RequireFixedSha256(
                expectedShader.Sha256,
                actualShader.Sha256,
                $"shader '{expectedShader.ResourceName}'");
        }
    }

    private static void ValidateDistanceResults(
        IReadOnlyList<AlphaVisibilityDistanceResult>? actual,
        IReadOnlyList<AlphaVisibilityDistanceResult> expected)
    {
        if (actual is null || actual.Count != expected.Count)
            throw new InvalidDataException("Alpha-visibility distance evidence set is incomplete.");
        for (int index = 0; index < expected.Count; index++)
        {
            AlphaVisibilityDistanceResult a = actual[index];
            AlphaVisibilityDistanceResult e = expected[index];
            if (BitConverter.SingleToInt32Bits(a.Distance) !=
                    BitConverter.SingleToInt32Bits(e.Distance) ||
                BitConverter.SingleToInt32Bits(a.RayTextureLod) !=
                    BitConverter.SingleToInt32Bits(e.RayTextureLod) ||
                a.RasterCandidateCount != e.RasterCandidateCount ||
                a.RasterCoveredCount != e.RasterCoveredCount ||
                a.RayCandidateCount != e.RayCandidateCount ||
                a.RayCoveredCount != e.RayCoveredCount ||
                BitConverter.DoubleToInt64Bits(a.RasterCoverage) !=
                    BitConverter.DoubleToInt64Bits(e.RasterCoverage) ||
                BitConverter.DoubleToInt64Bits(a.RayCoverage) !=
                    BitConverter.DoubleToInt64Bits(e.RayCoverage) ||
                BitConverter.DoubleToInt64Bits(a.AbsoluteCoverageDifference) !=
                    BitConverter.DoubleToInt64Bits(e.AbsoluteCoverageDifference) ||
                a.Passed != e.Passed)
            {
                throw new InvalidDataException(
                    $"Alpha-visibility distance result {index} does not match raw evidence.");
            }
        }
    }

    private static string ComputeAuthenticationDigest(
        string textureSha256,
        IReadOnlyList<AlphaVisibilityShaderEvidence> shaders,
        string evidenceSha256,
        long evidenceLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAuthenticationField(hash, AlphaVisibilityConformanceContract.ContractFingerprint);
        AppendAuthenticationField(hash, textureSha256);
        foreach (AlphaVisibilityShaderEvidence shader in shaders)
        {
            AppendAuthenticationField(hash, shader.ResourceName);
            AppendAuthenticationField(hash, shader.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendAuthenticationField(hash, shader.Sha256);
        }
        AppendAuthenticationField(hash, evidenceLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendAuthenticationField(hash, evidenceSha256);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendAuthenticationField(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        return BoundedFileReader.ReadStable(
            path,
            maximumBytes,
            "Required alpha-visibility evidence file");
    }

    private static void RequireSha256(
        ReadOnlySpan<byte> bytes,
        string expected,
        string name)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        RequireFixedSha256(actual, expected, name);
    }

    private static void RequireFixedSha256(string actual, string expected, string name)
    {
        if (!TryDecodeSha256(actual, out byte[] actualBytes) ||
            !TryDecodeSha256(expected, out byte[] expectedBytes) ||
            !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
        {
            throw new InvalidDataException($"{name} SHA-256 is invalid.");
        }
    }

    private static bool TryDecodeSha256(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is null || value.Length != 64)
            return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string RetainFailure(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "No failure detail was supplied."
            : value.Trim();
        return normalized.Length <= 4096 ? normalized : normalized[..4096];
    }

    private static string RetainOptionalDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Length <= 4096 ? value : value[..4096];
    }

    private static void WriteBytesAtomically(string fullPath, ReadOnlySpan<byte> bytes)
    {
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Output path has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
