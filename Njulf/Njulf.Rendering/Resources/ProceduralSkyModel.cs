using System;
using System.Numerics;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

internal enum ProceduralSkyEvaluationMode
{
    Visual,
    DiffuseTransport
}

/// <summary>
/// Stable boundary between the environment system and an analytic atmosphere.
/// Consumers depend only on the frame coefficients and can therefore migrate to
/// a spectral/precomputed model without changing DDGI or IBL integration.
/// </summary>
internal interface IProceduralSkyModel
{
    void UpdateFrame(
        EnvironmentSettings settings,
        Vector3 toSunDirection,
        Vector3? authoredSunRadiance,
        ProceduralAtmosphereFrame destination);

    Vector3 EvaluateSkyRadiance(
        Vector3 direction,
        ProceduralAtmosphereFrame frame,
        ProceduralSkyEvaluationMode mode,
        bool includeCelestialDiscs,
        bool includeStars);
}

internal sealed class ProceduralAtmosphereFrame
{
    public float[] HosekParameters { get; } = new float[27];
    public float[] HosekRadiances { get; } = new float[3];
    public Vector3[] DiffuseIrradianceSh { get; } = new Vector3[9];

    public Vector3 ToSunDirection { get; set; } = Vector3.UnitY;
    public Vector3 SunRadiance { get; set; }
    public Vector3 ToMoonDirection { get; set; } = -Vector3.UnitY;
    public Vector3 MoonRadiance { get; set; }
    public Vector3 GroundAlbedo { get; set; } = new(0.2f);
    public Vector3 GroundRadiance { get; set; }
    public float SunAngularRadiusRadians { get; set; } = 0.00462512f;
    public float MoonAngularRadiusRadians { get; set; } = 0.004522f;
    public float SunElevationRadians { get; set; }
    public float Turbidity { get; set; } = 3.0f;
    public float AtmosphereIntensity { get; set; } = 1.0f;
    public float DayBlend { get; set; } = 1.0f;
    public float TwilightBlend { get; set; }
    public float NightBlend { get; set; }
    public float StarIntensity { get; set; }
    public float AirglowIntensity { get; set; }
    public ulong SourceSignature { get; set; }
    public uint Revision { get; set; }
}

/// <summary>
/// RGB Hosek-Wilkie clear-sky implementation with an explicit twilight/night
/// extension. The daylight coefficient interpolation is the reference model;
/// the policy below the horizon is intentionally separate because the original
/// model is not defined there.
/// </summary>
internal sealed class HosekWilkieSkyModel : IProceduralSkyModel
{
    private const int DiffuseProjectionSampleCount = 512;
    private const int GroundIrradianceSampleCount = 192;
    private const float Pi = MathF.PI;
    private const float TwoPi = 2.0f * MathF.PI;
    private const float DegreesToRadians = MathF.PI / 180.0f;

    public void UpdateFrame(
        EnvironmentSettings settings,
        Vector3 toSunDirection,
        Vector3? authoredSunRadiance,
        ProceduralAtmosphereFrame destination)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(destination);

        Vector3 toSun = NormalizeOr(toSunDirection, Vector3.UnitY);
        float elevation = MathF.Asin(Math.Clamp(toSun.Y, -1.0f, 1.0f));
        float daylightElevation = Math.Clamp(elevation, 0.0f, Pi * 0.5f);
        float turbidity = Math.Clamp(settings.Turbidity, 1.0f, 10.0f);
        Vector3 groundAlbedo = Vector3.Clamp(
            new Vector3(
                settings.GroundAlbedo.X,
                settings.GroundAlbedo.Y,
                settings.GroundAlbedo.Z),
            Vector3.Zero,
            Vector3.One);
        Vector3? effectiveAuthoredSunRadiance = authoredSunRadiance.HasValue
            ? Vector3.Max(authoredSunRadiance.Value, Vector3.Zero)
            : null;
        ulong sourceSignature = CreateSourceSignature(
            settings,
            toSun,
            effectiveAuthoredSunRadiance,
            turbidity,
            groundAlbedo);
        if (destination.SourceSignature == sourceSignature)
            return;

        InitializeChannel(
            destination.HosekParameters.AsSpan(0, 9),
            out destination.HosekRadiances[0],
            HosekWilkieRgbData.ParamsR,
            HosekWilkieRgbData.RadiancesR,
            daylightElevation,
            turbidity,
            groundAlbedo.X);
        InitializeChannel(
            destination.HosekParameters.AsSpan(9, 9),
            out destination.HosekRadiances[1],
            HosekWilkieRgbData.ParamsG,
            HosekWilkieRgbData.RadiancesG,
            daylightElevation,
            turbidity,
            groundAlbedo.Y);
        InitializeChannel(
            destination.HosekParameters.AsSpan(18, 9),
            out destination.HosekRadiances[2],
            HosekWilkieRgbData.ParamsB,
            HosekWilkieRgbData.RadiancesB,
            daylightElevation,
            turbidity,
            groundAlbedo.Z);

        destination.ToSunDirection = toSun;
        destination.SunElevationRadians = elevation;
        destination.Turbidity = turbidity;
        destination.AtmosphereIntensity = settings.AtmosphereIntensity;
        destination.GroundAlbedo = groundAlbedo;
        destination.SunAngularRadiusRadians =
            settings.SunAngularDiameterDegrees * 0.5f * DegreesToRadians;
        destination.MoonAngularRadiusRadians =
            settings.MoonAngularDiameterDegrees * 0.5f * DegreesToRadians;
        destination.DayBlend = Smoothstep(-6.0f * DegreesToRadians, 1.0f * DegreesToRadians, elevation);
        float twilightArrival = Smoothstep(
            -18.0f * DegreesToRadians,
            -5.0f * DegreesToRadians,
            elevation);
        float twilightDeparture = 1.0f - Smoothstep(
            -3.0f * DegreesToRadians,
            5.0f * DegreesToRadians,
            elevation);
        destination.TwilightBlend = Math.Clamp(twilightArrival * twilightDeparture, 0.0f, 1.0f);
        destination.NightBlend = 1.0f - Smoothstep(
            -12.0f * DegreesToRadians,
            -4.0f * DegreesToRadians,
            elevation);
        destination.StarIntensity = settings.StarIntensity;
        destination.AirglowIntensity = settings.AirglowIntensity;

        destination.SunRadiance = effectiveAuthoredSunRadiance.HasValue
            ? effectiveAuthoredSunRadiance.Value
            : CalculateAtmosphericSunRadiance(
                elevation,
                turbidity,
                settings.SolarIrradianceScale);

        // A simple opposition moon is preferable to an independently animated
        // light: phase and visibility remain coupled to the solar driver. A
        // small inclination avoids a perfectly degenerate horizon crossing.
        destination.ToMoonDirection = NormalizeOr(
            -toSun + new Vector3(0.0f, 0.0872f, 0.0f),
            -Vector3.UnitY);
        float moonVisibility = Smoothstep(
            -2.0f * DegreesToRadians,
            4.0f * DegreesToRadians,
            MathF.Asin(Math.Clamp(destination.ToMoonDirection.Y, -1.0f, 1.0f)));
        destination.MoonRadiance = new Vector3(0.72f, 0.82f, 1.0f) *
            settings.MoonIrradianceScale *
            moonVisibility *
            destination.NightBlend;

        destination.GroundRadiance = Vector3.Zero;
        Vector3 diffuseSkyIrradiance = EstimateUpperHemisphereIrradiance(destination);
        Vector3 directHorizontalIrradiance = destination.SunRadiance *
            MathF.Max(toSun.Y, 0.0f);
        destination.GroundRadiance = groundAlbedo *
            (diffuseSkyIrradiance + directHorizontalIrradiance) / Pi;

        ProjectDiffuseIrradianceSh(destination, destination.DiffuseIrradianceSh);
        destination.SourceSignature = sourceSignature;
        destination.Revision++;
        if (destination.Revision == 0)
            destination.Revision = 1;
    }

    public Vector3 EvaluateSkyRadiance(
        Vector3 direction,
        ProceduralAtmosphereFrame frame,
        ProceduralSkyEvaluationMode mode,
        bool includeCelestialDiscs,
        bool includeStars)
    {
        Vector3 safeDirection = NormalizeOr(direction, Vector3.UnitY);
        if (safeDirection.Y < 0.0f)
            return Vector3.Max(frame.GroundRadiance, Vector3.Zero);

        float gamma = MathF.Acos(Math.Clamp(
            Vector3.Dot(safeDirection, frame.ToSunDirection),
            -1.0f,
            1.0f));
        Vector3 daylight = new(
            EvaluateHosekChannel(safeDirection.Y, gamma, frame.HosekParameters, 0, frame.HosekRadiances[0]),
            EvaluateHosekChannel(safeDirection.Y, gamma, frame.HosekParameters, 9, frame.HosekRadiances[1]),
            EvaluateHosekChannel(safeDirection.Y, gamma, frame.HosekParameters, 18, frame.HosekRadiances[2]));
        daylight = Vector3.Max(daylight, Vector3.Zero) *
            frame.DayBlend *
            frame.AtmosphereIntensity;

        if (mode == ProceduralSkyEvaluationMode.DiffuseTransport)
        {
            // The directional light owns direct solar transport. Suppressing
            // the narrow circumsolar lobe keeps its energy out of SH and probe
            // diffuse while retaining the broad atmospheric dome.
            daylight *= Smoothstep(
                3.0f * DegreesToRadians,
                10.0f * DegreesToRadians,
                gamma);
        }

        float horizonBand = MathF.Exp(-safeDirection.Y * 7.0f);
        Vector2 directionAzimuth = NormalizeOr(
            new Vector2(safeDirection.X, safeDirection.Z),
            Vector2.UnitY);
        Vector2 sunAzimuth = NormalizeOr(
            new Vector2(frame.ToSunDirection.X, frame.ToSunDirection.Z),
            Vector2.UnitY);
        float towardSun = MathF.Pow(
            MathF.Max(Vector2.Dot(directionAzimuth, sunAzimuth), 0.0f),
            4.0f);
        Vector3 twilightColor = Vector3.Lerp(
            new Vector3(0.012f, 0.024f, 0.080f),
            new Vector3(1.15f, 0.18f, 0.025f),
            towardSun);
        Vector3 twilight = twilightColor *
            frame.TwilightBlend *
            (0.12f + 0.88f * horizonBand) *
            frame.AtmosphereIntensity;

        Vector3 nightGradient = Vector3.Lerp(
            new Vector3(0.12f, 0.18f, 0.34f),
            new Vector3(0.018f, 0.035f, 0.095f),
            MathF.Sqrt(Math.Clamp(safeDirection.Y, 0.0f, 1.0f)));
        Vector3 result = daylight + twilight +
            nightGradient * frame.AirglowIntensity * frame.NightBlend;

        if (includeCelestialDiscs)
        {
            result += EvaluateDisc(
                safeDirection,
                frame.ToSunDirection,
                frame.SunAngularRadiusRadians,
                frame.SunRadiance);
            result += EvaluateDisc(
                safeDirection,
                frame.ToMoonDirection,
                frame.MoonAngularRadiusRadians,
                frame.MoonRadiance);
        }

        if (includeStars && frame.NightBlend > 0.0f)
            result += EvaluateCpuStarField(safeDirection) * frame.StarIntensity * frame.NightBlend;

        return Vector3.Max(result, Vector3.Zero);
    }

    private static void InitializeChannel(
        Span<float> outputParameters,
        out float outputRadiance,
        ReadOnlySpan<float> parameterDataset,
        ReadOnlySpan<float> radianceDataset,
        float elevation,
        float turbidity,
        float albedo)
    {
        outputParameters.Clear();
        outputRadiance = 0.0f;
        float t = MathF.Pow(elevation / (0.5f * Pi), 1.0f / 3.0f);
        int turbidityInteger = (int)MathF.Truncate(turbidity);
        float turbidityRemainder = turbidity - turbidityInteger;
        int turbidityMinimum = Math.Max(turbidityInteger - 1, 0);
        int turbidityMaximum = Math.Min(turbidityInteger, 9);
        float s0 = (1.0f - albedo) * (1.0f - turbidityRemainder);
        float s1 = (1.0f - albedo) * turbidityRemainder;
        float s2 = albedo * (1.0f - turbidityRemainder);
        float s3 = albedo * turbidityRemainder;

        int parameterAlbedoStride = 9 * 6 * 10;
        int p0 = 9 * 6 * turbidityMinimum;
        int p1 = 9 * 6 * turbidityMaximum;
        int p2 = parameterAlbedoStride + p0;
        int p3 = parameterAlbedoStride + p1;
        for (int coefficient = 0; coefficient < 9; coefficient++)
        {
            outputParameters[coefficient] =
                s0 * Quintic(parameterDataset, p0 + coefficient, 9, t) +
                s1 * Quintic(parameterDataset, p1 + coefficient, 9, t) +
                s2 * Quintic(parameterDataset, p2 + coefficient, 9, t) +
                s3 * Quintic(parameterDataset, p3 + coefficient, 9, t);
        }

        int radianceAlbedoStride = 6 * 10;
        int r0 = 6 * turbidityMinimum;
        int r1 = 6 * turbidityMaximum;
        int r2 = radianceAlbedoStride + r0;
        int r3 = radianceAlbedoStride + r1;
        outputRadiance =
            s0 * Quintic(radianceDataset, r0, 1, t) +
            s1 * Quintic(radianceDataset, r1, 1, t) +
            s2 * Quintic(radianceDataset, r2, 1, t) +
            s3 * Quintic(radianceDataset, r3, 1, t);
    }

    private static float Quintic(
        ReadOnlySpan<float> data,
        int offset,
        int stride,
        float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t2 * t2;
        float t5 = t4 * t;
        float inverse = 1.0f - t;
        float inverse2 = inverse * inverse;
        float inverse3 = inverse2 * inverse;
        float inverse4 = inverse2 * inverse2;
        float inverse5 = inverse4 * inverse;
        return data[offset] * inverse5 +
            data[offset + stride] * 5.0f * inverse4 * t +
            data[offset + 2 * stride] * 10.0f * inverse3 * t2 +
            data[offset + 3 * stride] * 10.0f * inverse2 * t3 +
            data[offset + 4 * stride] * 5.0f * inverse * t4 +
            data[offset + 5 * stride] * t5;
    }

    private static float EvaluateHosekChannel(
        float directionY,
        float gamma,
        float[] parameters,
        int offset,
        float radianceScale)
    {
        float cosGamma = MathF.Cos(gamma);
        float cosGamma2 = cosGamma * cosGamma;
        float cosTheta = MathF.Abs(directionY);
        float expM = MathF.Exp(parameters[offset + 4] * gamma);
        float mieDenominator = MathF.Pow(MathF.Max(
            1.0f + parameters[offset + 8] * parameters[offset + 8] -
            2.0f * parameters[offset + 8] * cosGamma,
            0.00001f), 1.5f);
        float mieM = (1.0f + cosGamma2) / mieDenominator;
        float lhs = 1.0f + parameters[offset] *
            MathF.Exp(parameters[offset + 1] / (cosTheta + 0.01f));
        float rhs = parameters[offset + 2] +
            parameters[offset + 3] * expM +
            parameters[offset + 5] * cosGamma2 +
            parameters[offset + 6] * mieM +
            parameters[offset + 7] * MathF.Sqrt(cosTheta);
        return radianceScale * lhs * rhs;
    }

    private Vector3 EstimateUpperHemisphereIrradiance(
        ProceduralAtmosphereFrame frame)
    {
        Vector3 sum = Vector3.Zero;
        for (int sampleIndex = 0; sampleIndex < GroundIrradianceSampleCount; sampleIndex++)
        {
            float u = (sampleIndex + 0.5f) / GroundIrradianceSampleCount;
            float v = RadicalInverseVdc((uint)sampleIndex);
            float y = u;
            float radius = MathF.Sqrt(MathF.Max(1.0f - y * y, 0.0f));
            float phi = TwoPi * v;
            Vector3 direction = new(
                radius * MathF.Cos(phi),
                y,
                radius * MathF.Sin(phi));
            Vector3 radiance = EvaluateSkyRadiance(
                direction,
                frame,
                ProceduralSkyEvaluationMode.DiffuseTransport,
                includeCelestialDiscs: false,
                includeStars: false);
            sum += radiance * y;
        }

        return sum * (TwoPi / GroundIrradianceSampleCount);
    }

    private void ProjectDiffuseIrradianceSh(
        ProceduralAtmosphereFrame frame,
        Vector3[] output)
    {
        Array.Clear(output);
        Span<float> basis = stackalloc float[9];
        float sampleWeight = 4.0f * Pi / DiffuseProjectionSampleCount;
        for (int sampleIndex = 0; sampleIndex < DiffuseProjectionSampleCount; sampleIndex++)
        {
            float y = 1.0f - 2.0f * (sampleIndex + 0.5f) / DiffuseProjectionSampleCount;
            float radius = MathF.Sqrt(MathF.Max(1.0f - y * y, 0.0f));
            float phi = TwoPi * RadicalInverseVdc((uint)sampleIndex);
            Vector3 direction = new(
                radius * MathF.Cos(phi),
                y,
                radius * MathF.Sin(phi));
            Vector3 radiance = EvaluateSkyRadiance(
                direction,
                frame,
                ProceduralSkyEvaluationMode.DiffuseTransport,
                includeCelestialDiscs: false,
                includeStars: false);
            EvaluateShBasis(direction, basis);
            for (int coefficient = 0; coefficient < 9; coefficient++)
                output[coefficient] += radiance * (basis[coefficient] * sampleWeight);
        }

        for (int coefficient = 0; coefficient < 9; coefficient++)
        {
            float cosineKernel = coefficient == 0
                ? Pi
                : coefficient <= 3
                    ? 2.0f * Pi / 3.0f
                    : Pi / 4.0f;
            output[coefficient] *= cosineKernel;
        }
    }

    internal static Vector3 EvaluateDiffuseIrradianceSh(
        Vector3 normal,
        ReadOnlySpan<Vector3> coefficients)
    {
        if (coefficients.Length < 9)
            throw new ArgumentException("Nine SH coefficients are required.", nameof(coefficients));
        Span<float> basis = stackalloc float[9];
        EvaluateShBasis(NormalizeOr(normal, Vector3.UnitY), basis);
        Vector3 result = Vector3.Zero;
        for (int coefficient = 0; coefficient < 9; coefficient++)
            result += coefficients[coefficient] * basis[coefficient];
        return Vector3.Max(result, Vector3.Zero);
    }

    internal static void EvaluateShBasis(Vector3 direction, Span<float> output)
    {
        float x = direction.X;
        float y = direction.Y;
        float z = direction.Z;
        output[0] = 0.2820947918f;
        output[1] = 0.4886025119f * z;
        output[2] = 0.4886025119f * y;
        output[3] = 0.4886025119f * x;
        output[4] = 1.0925484306f * x * z;
        output[5] = 1.0925484306f * z * y;
        output[6] = 0.3153915653f * (3.0f * y * y - 1.0f);
        output[7] = 1.0925484306f * x * y;
        output[8] = 0.5462742153f * (x * x - z * z);
    }

    private static Vector3 CalculateAtmosphericSunRadiance(
        float elevation,
        float turbidity,
        float scale)
    {
        if (elevation <= -2.0f * DegreesToRadians || scale <= 0.0f)
            return Vector3.Zero;

        float elevationDegrees = MathF.Max(elevation / DegreesToRadians, 0.0f);
        float airMass = 1.0f / MathF.Max(
            MathF.Sin(MathF.Max(elevation, 0.0f)) +
            0.50572f * MathF.Pow(elevationDegrees + 6.07995f, -1.6364f),
            0.001f);
        // RGB optical depths sampled near 680, 550 and 440 nm. Aerosol
        // extinction grows with turbidity; Rayleigh extinction provides the
        // characteristic warm horizon shift.
        Vector3 rayleighOpticalDepth = new(0.055f, 0.100f, 0.220f);
        Vector3 aerosolOpticalDepth = new Vector3(0.018f, 0.028f, 0.045f) *
            MathF.Max(turbidity - 1.0f, 0.0f);
        Vector3 opticalDepth = (rayleighOpticalDepth + aerosolOpticalDepth) * airMass;
        Vector3 transmission = new(
            MathF.Exp(-opticalDepth.X),
            MathF.Exp(-opticalDepth.Y),
            MathF.Exp(-opticalDepth.Z));
        float horizonFade = Smoothstep(-2.0f * DegreesToRadians, 0.5f * DegreesToRadians, elevation);
        return new Vector3(1.0f, 0.985f, 0.965f) * transmission * scale * horizonFade;
    }

    private static Vector3 EvaluateDisc(
        Vector3 direction,
        Vector3 toDisc,
        float angularRadius,
        Vector3 irradiance)
    {
        if (irradiance.LengthSquared() <= 0.0f)
            return Vector3.Zero;
        float radius = Math.Clamp(angularRadius, 0.0005f, 0.05f);
        float cosine = Vector3.Dot(direction, toDisc);
        float disc = Smoothstep(
            MathF.Cos(radius * 1.08f),
            MathF.Cos(radius * 0.92f),
            cosine);
        float solidAngle = TwoPi * (1.0f - MathF.Cos(radius));
        return Vector3.Min(
            irradiance / MathF.Max(solidAngle, 0.000001f),
            new Vector3(60_000.0f)) * disc;
    }

    private static Vector3 EvaluateCpuStarField(Vector3 direction)
    {
        uint x = BitConverter.SingleToUInt32Bits(direction.X * 173.17f);
        uint y = BitConverter.SingleToUInt32Bits(direction.Y * 317.11f);
        uint z = BitConverter.SingleToUInt32Bits(direction.Z * 619.73f);
        uint hash = Hash(x ^ RotateLeft(y, 11) ^ RotateLeft(z, 22));
        float selector = (hash & 0x00ffffffu) / 16_777_215.0f;
        if (selector < 0.9985f)
            return Vector3.Zero;
        float brightness = MathF.Pow((selector - 0.9985f) / 0.0015f, 6.0f);
        return Vector3.Lerp(new Vector3(0.62f, 0.75f, 1.0f), Vector3.One, (hash >> 24) / 255.0f) * brightness;
    }

    private static uint RotateLeft(uint value, int amount) =>
        (value << amount) | (value >> (32 - amount));

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    private static ulong CreateSourceSignature(
        EnvironmentSettings settings,
        Vector3 toSun,
        Vector3? authoredSunRadiance,
        float turbidity,
        Vector3 groundAlbedo)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        ulong hash = offsetBasis;
        hash = HashSourceValue(hash, toSun);
        hash = HashSourceValue(hash, authoredSunRadiance.HasValue ? 1U : 0U);
        if (authoredSunRadiance.HasValue)
            hash = HashSourceValue(hash, authoredSunRadiance.Value);
        hash = HashSourceValue(hash, turbidity);
        hash = HashSourceValue(hash, groundAlbedo);
        hash = HashSourceValue(hash, settings.AtmosphereIntensity);
        hash = HashSourceValue(hash, settings.SunAngularDiameterDegrees);
        hash = HashSourceValue(hash, settings.MoonAngularDiameterDegrees);
        hash = HashSourceValue(hash, settings.StarIntensity);
        hash = HashSourceValue(hash, settings.AirglowIntensity);
        hash = HashSourceValue(hash, settings.SolarIrradianceScale);
        hash = HashSourceValue(hash, settings.MoonIrradianceScale);
        return hash == 0UL ? 1UL : hash;
    }

    private static ulong HashSourceValue(ulong hash, Vector3 value)
    {
        hash = HashSourceValue(hash, value.X);
        hash = HashSourceValue(hash, value.Y);
        return HashSourceValue(hash, value.Z);
    }

    private static ulong HashSourceValue(ulong hash, float value) =>
        HashSourceValue(hash, BitConverter.SingleToUInt32Bits(value));

    private static ulong HashSourceValue(ulong hash, uint value)
    {
        const ulong prime = 1099511628211UL;
        hash ^= value;
        return hash * prime;
    }

    private static float RadicalInverseVdc(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(edge1 - edge0, 0.000001f), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : fallback;

    private static Vector2 NormalizeOr(Vector2 value, Vector2 fallback) =>
        value.LengthSquared() > 0.000001f ? Vector2.Normalize(value) : fallback;
}

internal static class SolarPositionCalculator
{
    private const float DegreesToRadians = MathF.PI / 180.0f;

    public static Vector3 CalculateToSunDirection(
        float solarTimeHours,
        float latitudeDegrees,
        int dayOfYear,
        float northOffsetDegrees)
    {
        float latitude = Math.Clamp(latitudeDegrees, -90.0f, 90.0f) * DegreesToRadians;
        int day = Math.Clamp(dayOfYear, 1, 366);
        float yearAngle = 2.0f * MathF.PI / 365.0f * (day - 1);
        float declination = 0.006918f -
            0.399912f * MathF.Cos(yearAngle) +
            0.070257f * MathF.Sin(yearAngle) -
            0.006758f * MathF.Cos(2.0f * yearAngle) +
            0.000907f * MathF.Sin(2.0f * yearAngle) -
            0.002697f * MathF.Cos(3.0f * yearAngle) +
            0.00148f * MathF.Sin(3.0f * yearAngle);
        float wrappedTime = solarTimeHours % 24.0f;
        if (wrappedTime < 0.0f)
            wrappedTime += 24.0f;
        float hourAngle = (wrappedTime - 12.0f) * 15.0f * DegreesToRadians;

        float cosDeclination = MathF.Cos(declination);
        float sinDeclination = MathF.Sin(declination);
        float cosLatitude = MathF.Cos(latitude);
        float sinLatitude = MathF.Sin(latitude);
        Vector3 direction = new(
            -cosDeclination * MathF.Sin(hourAngle),
            sinLatitude * sinDeclination +
                cosLatitude * cosDeclination * MathF.Cos(hourAngle),
            sinDeclination * cosLatitude -
                cosDeclination * MathF.Cos(hourAngle) * sinLatitude);

        float northOffset = northOffsetDegrees * DegreesToRadians;
        float cosine = MathF.Cos(northOffset);
        float sine = MathF.Sin(northOffset);
        return Vector3.Normalize(new Vector3(
            direction.X * cosine + direction.Z * sine,
            direction.Y,
            -direction.X * sine + direction.Z * cosine));
    }
}
