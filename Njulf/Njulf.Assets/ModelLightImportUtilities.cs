using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets;

internal static class ModelLightImportUtilities
{
    private const float DirectionEpsilon = 1e-12f;
    private const int MaximumImportedLights = 1024;

    public static void ValidateOptions(ImporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!float.IsFinite(options.DefaultImportedLightRange) ||
            options.DefaultImportedLightRange <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.DefaultImportedLightRange),
                "The default imported-light range must be finite and positive.");
        }
        if (!float.IsFinite(options.MaximumImportedLightRange) ||
            options.MaximumImportedLightRange < options.DefaultImportedLightRange)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaximumImportedLightRange),
                "The maximum imported-light range must be finite and at least the default range.");
        }
        if (!float.IsFinite(options.ImportedLightAttenuationCutoff) ||
            options.ImportedLightAttenuationCutoff <= 0f ||
            options.ImportedLightAttenuationCutoff >= 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ImportedLightAttenuationCutoff),
                "The imported-light attenuation cutoff must be finite and between zero and one.");
        }
    }

    public static float ResolveRange(
        float authoredRange,
        bool hasAuthoredRange,
        ImporterOptions options,
        AssetImportDiagnostics diagnostics,
        string assetPath,
        string source)
    {
        float range;
        if (hasAuthoredRange)
        {
            float scale = MathF.Abs(options.GlobalScale);
            range = authoredRange * scale;
            if (!float.IsFinite(range) || range <= 0f)
            {
                throw InvalidLight(
                    diagnostics,
                    assetPath,
                    source,
                    "The authored light range must remain finite and positive after global scaling.");
            }
        }
        else
        {
            range = options.DefaultImportedLightRange;
            diagnostics.Add(
                AssetImportSeverity.Info,
                AssetImportMessageCode.LightRangeDefaulted,
                assetPath,
                source,
                $"Light '{source}' has no finite range; using {range:R} scene units.");
        }

        if (range <= options.MaximumImportedLightRange)
            return range;

        diagnostics.Add(
            AssetImportSeverity.Warning,
            AssetImportMessageCode.LightRangeClamped,
            assetPath,
            source,
            $"Light '{source}' range {range:R} exceeds the configured maximum and was clamped to {options.MaximumImportedLightRange:R}.");
        return options.MaximumImportedLightRange;
    }

    public static float ResolvePolynomialRange(
        float constant,
        float linear,
        float quadratic,
        ImporterOptions options,
        AssetImportDiagnostics diagnostics,
        string assetPath,
        string source)
    {
        float targetDenominator = 1f / options.ImportedLightAttenuationCutoff;
        float range = float.NaN;
        float target = targetDenominator - constant;
        if (target > 0f)
        {
            if (quadratic > float.Epsilon)
            {
                double discriminant = (double)linear * linear +
                    4d * quadratic * target;
                if (discriminant >= 0d)
                {
                    range = (float)((-linear + Math.Sqrt(discriminant)) /
                        (2d * quadratic));
                }
            }
            else if (linear > float.Epsilon)
            {
                range = target / linear;
            }
        }

        return FinalizeDerivedRange(
            range,
            float.IsFinite(range) && range > 0f,
            options,
            diagnostics,
            assetPath,
            source);
    }

    public static ModelLightDefinition ValidateAndRecord(
        ModelLightDefinition light,
        AssetImportDiagnostics diagnostics,
        string assetPath)
    {
        if (diagnostics.ImportedLightCount >= MaximumImportedLights)
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                light.Name,
                $"The asset exceeds the {MaximumImportedLights}-light runtime limit.");
        }
        string source = string.IsNullOrWhiteSpace(light.Name)
            ? $"light {light.SourceIndex}"
            : light.Name;
        if (!Enum.IsDefined(light.Type))
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                $"Imported light type '{light.Type}' is not supported.");
        }
        if (!Enum.IsDefined(light.AttenuationMode))
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                $"Imported attenuation mode '{light.AttenuationMode}' is not supported.");
        }
        if (!IsFinite(light.Position) || !IsFinite(light.Direction) ||
            !IsFinite(light.Color) || !float.IsFinite(light.Intensity) ||
            !float.IsFinite(light.Range) ||
            !float.IsFinite(light.InnerConeAngle) ||
            !float.IsFinite(light.OuterConeAngle) ||
            !float.IsFinite(light.AttenuationConstant) ||
            !float.IsFinite(light.AttenuationLinear) ||
            !float.IsFinite(light.AttenuationQuadratic))
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                "Imported light data contains a non-finite value.");
        }
        if (light.Color.X < 0f || light.Color.Y < 0f || light.Color.Z < 0f ||
            light.Intensity < 0f || light.Range <= 0f)
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                "Imported light color/intensity must be non-negative and its range must be positive.");
        }
        if (light.Type is ModelLightType.Directional or ModelLightType.Spot &&
            light.Direction.LengthSquared() <= DirectionEpsilon)
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                "A directional or spot light has a zero direction vector.");
        }
        if (light.Type == ModelLightType.Spot &&
            (light.InnerConeAngle < 0f ||
             light.OuterConeAngle <= 0f ||
             light.InnerConeAngle > light.OuterConeAngle ||
             light.OuterConeAngle > MathF.PI))
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                "Spot-light cone angles must satisfy 0 <= inner <= outer <= PI.");
        }
        if (light.AttenuationMode == ModelLightAttenuationMode.Polynomial &&
            (light.AttenuationConstant < 0f ||
             light.AttenuationLinear < 0f ||
             light.AttenuationQuadratic < 0f ||
             light.AttenuationConstant + light.AttenuationLinear +
                 light.AttenuationQuadratic <= 0f))
        {
            throw InvalidLight(
                diagnostics,
                assetPath,
                source,
                "Polynomial attenuation coefficients must be non-negative and not all zero.");
        }

        diagnostics.ImportedLightCount++;
        switch (light.Type)
        {
            case ModelLightType.Point:
                diagnostics.ImportedPointLightCount++;
                break;
            case ModelLightType.Directional:
                diagnostics.ImportedDirectionalLightCount++;
                break;
            case ModelLightType.Spot:
                diagnostics.ImportedSpotLightCount++;
                break;
        }
        diagnostics.Add(
            AssetImportSeverity.Info,
            AssetImportMessageCode.LightImported,
            assetPath,
            light.SourceNodeName,
            $"Imported {light.Type.ToString().ToLowerInvariant()} light '{source}'.");
        return light;
    }

    private static InvalidDataException InvalidLight(
        AssetImportDiagnostics diagnostics,
        string assetPath,
        string source,
        string message)
    {
        diagnostics.Add(
            AssetImportSeverity.Error,
            AssetImportMessageCode.InvalidLight,
            assetPath,
            source,
            message);
        return new InvalidDataException(message);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static float FinalizeDerivedRange(
        float range,
        bool resolved,
        ImporterOptions options,
        AssetImportDiagnostics diagnostics,
        string assetPath,
        string source)
    {
        if (!resolved)
        {
            return ResolveRange(
                0f,
                hasAuthoredRange: false,
                options,
                diagnostics,
                assetPath,
                source);
        }

        if (range <= options.MaximumImportedLightRange)
            return range;

        diagnostics.Add(
            AssetImportSeverity.Warning,
            AssetImportMessageCode.LightRangeClamped,
            assetPath,
            source,
            $"Light '{source}' derived range {range:R} exceeds the configured maximum and was clamped to {options.MaximumImportedLightRange:R}.");
        return options.MaximumImportedLightRange;
    }
}
