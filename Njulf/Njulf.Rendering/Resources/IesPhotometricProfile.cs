using System;
using System.Globalization;
using System.IO;

namespace Njulf.Rendering.Resources;

/// <summary>Validated LM-63 Type-C photometric data normalized to its peak candela.</summary>
public sealed class IesPhotometricProfile
{
    internal IesPhotometricProfile(
        float[] verticalAngles,
        float[] horizontalAngles,
        float[] candela,
        float peakCandela,
        float inputWatts)
    {
        VerticalAngles = verticalAngles;
        HorizontalAngles = horizontalAngles;
        Candela = candela;
        PeakCandela = peakCandela;
        InputWatts = inputWatts;
    }

    public ReadOnlyMemory<float> VerticalAngles { get; }
    public ReadOnlyMemory<float> HorizontalAngles { get; }
    /// <summary>Horizontal-major normalized candela values.</summary>
    public ReadOnlyMemory<float> Candela { get; }
    public float PeakCandela { get; }
    public float InputWatts { get; }

    public float Evaluate(float horizontalDegrees, float verticalDegrees)
    {
        ReadOnlySpan<float> horizontal = HorizontalAngles.Span;
        ReadOnlySpan<float> vertical = VerticalAngles.Span;
        float mappedHorizontal = MapHorizontalAngle(horizontal, horizontalDegrees);
        float mappedVertical = Math.Clamp(verticalDegrees, vertical[0], vertical[^1]);
        FindInterval(horizontal, mappedHorizontal, out int h0, out int h1, out float ht);
        FindInterval(vertical, mappedVertical, out int v0, out int v1, out float vt);
        int verticalCount = vertical.Length;
        ReadOnlySpan<float> values = Candela.Span;
        float lower = Lerp(values[h0 * verticalCount + v0], values[h0 * verticalCount + v1], vt);
        float upper = Lerp(values[h1 * verticalCount + v0], values[h1 * verticalCount + v1], vt);
        return Math.Clamp(Lerp(lower, upper, ht), 0f, 1f);
    }

    public float[] Resample(int width, int height)
    {
        if (width < 2)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 2)
            throw new ArgumentOutOfRangeException(nameof(height));
        var result = new float[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            float vertical = 180f * y / (height - 1f);
            for (int x = 0; x < width; x++)
            {
                // U repeats, so store samples at texel centers around the full azimuth.
                float horizontal = 360f * (x + 0.5f) / width;
                result[y * width + x] = Evaluate(horizontal, vertical);
            }
        }
        return result;
    }

    private static float MapHorizontalAngle(ReadOnlySpan<float> angles, float degrees)
    {
        if (angles.Length == 1)
            return angles[0];
        float angle = degrees % 360f;
        if (angle < 0f)
            angle += 360f;
        float maximum = angles[^1];
        if (maximum <= 90.0001f)
        {
            angle %= 180f;
            if (angle > 90f)
                angle = 180f - angle;
        }
        else if (maximum <= 180.0001f && angle > 180f)
        {
            angle = 360f - angle;
        }
        return Math.Clamp(angle, angles[0], maximum);
    }

    private static void FindInterval(
        ReadOnlySpan<float> values,
        float value,
        out int lower,
        out int upper,
        out float amount)
    {
        if (values.Length == 1 || value <= values[0])
        {
            lower = upper = 0;
            amount = 0f;
            return;
        }
        if (value >= values[^1])
        {
            lower = upper = values.Length - 1;
            amount = 0f;
            return;
        }
        int index = values.BinarySearch(value);
        if (index >= 0)
        {
            lower = upper = index;
            amount = 0f;
            return;
        }
        upper = ~index;
        lower = upper - 1;
        amount = (value - values[lower]) / (values[upper] - values[lower]);
    }

    private static float Lerp(float left, float right, float amount) =>
        left + (right - left) * amount;
}

/// <summary>Strict, allocation-bounded parser for LM-63 files used by analytical lights.</summary>
public static class IesPhotometricProfileParser
{
    public const int MaximumTextLength = 4 * 1024 * 1024;
    public const int MaximumAngleCount = 4096;
    public const int MaximumCandelaCount = 4 * 1024 * 1024;

    public static IesPhotometricProfile Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0 || source.Length > MaximumTextLength)
            throw new InvalidDataException("IES text is empty or exceeds the 4 MiB safety limit.");
        if (!source.AsSpan().TrimStart().StartsWith("IESNA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The photometric profile does not have an LM-63 IESNA header.");

        int numericOffset = FindTiltLine(source, out string tiltMode);
        if (!string.Equals(tiltMode, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"IES tilt mode '{tiltMode}' is not supported; export a TILT=NONE Type-C profile.");
        }

        string numericText = source[numericOffset..].Replace(',', ' ');
        string[] tokens = numericText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var reader = new NumberReader(tokens);
        int lampCount = reader.ReadInt("lamp count", 1, 1_000_000);
        _ = reader.ReadFloat("lumens per lamp", allowNegativeOne: true);
        float candelaMultiplier = reader.ReadPositiveFloat("candela multiplier");
        int verticalCount = reader.ReadInt("vertical angle count", 2, MaximumAngleCount);
        int horizontalCount = reader.ReadInt("horizontal angle count", 1, MaximumAngleCount);
        int photometricType = reader.ReadInt("photometric type", 1, 3);
        if (photometricType != 1)
            throw new NotSupportedException("Only LM-63 Type-C photometric profiles are supported.");
        _ = reader.ReadInt("unit type", 1, 2);
        _ = reader.ReadNonNegativeFloat("luminaire width");
        _ = reader.ReadNonNegativeFloat("luminaire length");
        _ = reader.ReadNonNegativeFloat("luminaire height");
        float ballastFactor = reader.ReadPositiveFloat("ballast factor");
        float ballastLampFactor = reader.ReadPositiveFloat("ballast-lamp photometric factor");
        float inputWatts = reader.ReadNonNegativeFloat("input watts");

        long sampleCount = (long)verticalCount * horizontalCount;
        if (sampleCount > MaximumCandelaCount)
            throw new InvalidDataException("IES candela table exceeds the bounded sample limit.");
        var verticalAngles = new float[verticalCount];
        var horizontalAngles = new float[horizontalCount];
        for (int i = 0; i < verticalAngles.Length; i++)
            verticalAngles[i] = reader.ReadFiniteFloat($"vertical angle {i}");
        for (int i = 0; i < horizontalAngles.Length; i++)
            horizontalAngles[i] = reader.ReadFiniteFloat($"horizontal angle {i}");
        ValidateAngles(verticalAngles, 0f, 180f, "vertical");
        ValidateAngles(horizontalAngles, 0f, 360f, "horizontal");
        if (MathF.Abs(horizontalAngles[0]) > 0.001f)
            throw new InvalidDataException("Type-C horizontal angles must start at zero degrees.");
        if (horizontalAngles[^1] > 180.0001f &&
            MathF.Abs(horizontalAngles[^1] - 360f) > 0.001f)
        {
            throw new InvalidDataException(
                "A full Type-C horizontal distribution must end at 360 degrees.");
        }

        var candela = new float[checked((int)sampleCount)];
        float scale = checked(candelaMultiplier * ballastFactor * ballastLampFactor);
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new InvalidDataException("IES photometric scale is invalid.");
        float peak = 0f;
        for (int i = 0; i < candela.Length; i++)
        {
            float value = reader.ReadNonNegativeFloat($"candela sample {i}") * scale;
            if (!float.IsFinite(value))
                throw new InvalidDataException($"IES candela sample {i} overflows.");
            candela[i] = value;
            peak = MathF.Max(peak, value);
        }
        if (!(peak > 0f))
            throw new InvalidDataException("IES profile contains no positive candela values.");
        for (int i = 0; i < candela.Length; i++)
            candela[i] /= peak;

        _ = lampCount; // The candela table is already the complete luminaire distribution.
        return new IesPhotometricProfile(
            verticalAngles,
            horizontalAngles,
            candela,
            peak,
            inputWatts);
    }

    private static int FindTiltLine(string source, out string tiltMode)
    {
        int offset = 0;
        while (offset < source.Length)
        {
            int end = source.IndexOfAny(['\r', '\n'], offset);
            if (end < 0)
                end = source.Length;
            ReadOnlySpan<char> line = source.AsSpan(offset, end - offset).Trim();
            if (line.StartsWith("TILT=", StringComparison.OrdinalIgnoreCase))
            {
                tiltMode = line[5..].Trim().ToString();
                while (end < source.Length && (source[end] == '\r' || source[end] == '\n'))
                    end++;
                return end;
            }
            offset = end;
            while (offset < source.Length && (source[offset] == '\r' || source[offset] == '\n'))
                offset++;
        }
        throw new InvalidDataException("IES profile is missing its TILT declaration.");
    }

    private static void ValidateAngles(float[] values, float minimum, float maximum, string label)
    {
        float previous = float.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            float value = values[i];
            if (!float.IsFinite(value) || value < minimum || value > maximum || value <= previous)
            {
                throw new InvalidDataException(
                    $"IES {label} angle {i} must be finite, in [{minimum}, {maximum}], and strictly increasing.");
            }
            previous = value;
        }
    }

    private ref struct NumberReader
    {
        private readonly ReadOnlySpan<string> _tokens;
        private int _index;

        public NumberReader(string[] tokens) => _tokens = tokens;

        public int ReadInt(string label, int minimum, int maximum)
        {
            string token = Next(label);
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value < minimum || value > maximum)
            {
                throw new InvalidDataException(
                    $"IES {label} '{token}' is outside [{minimum}, {maximum}].");
            }
            return value;
        }

        public float ReadPositiveFloat(string label)
        {
            float value = ReadFiniteFloat(label);
            if (!(value > 0f))
                throw new InvalidDataException($"IES {label} must be positive.");
            return value;
        }

        public float ReadNonNegativeFloat(string label)
        {
            float value = ReadFiniteFloat(label);
            if (value < 0f)
                throw new InvalidDataException($"IES {label} cannot be negative.");
            return value;
        }

        public float ReadFloat(string label, bool allowNegativeOne)
        {
            float value = ReadFiniteFloat(label);
            if (value < 0f && !(allowNegativeOne && MathF.Abs(value + 1f) <= 0.0001f))
                throw new InvalidDataException($"IES {label} cannot be negative.");
            return value;
        }

        public float ReadFiniteFloat(string label)
        {
            string token = Next(label);
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                !float.IsFinite(value))
            {
                throw new InvalidDataException($"IES {label} '{token}' is not finite numeric data.");
            }
            return value;
        }

        private string Next(string label)
        {
            if (_index >= _tokens.Length)
                throw new InvalidDataException($"IES data ended before {label}.");
            return _tokens[_index++];
        }
    }
}
