using System;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Stable one-byte ABI written to the material-transport provenance attachment.
/// Zero is reserved for the render-target clear so untouched pixels are
/// unambiguously background. Unknown is intentionally conspicuous and distinct
/// from background when a shaded pixel cannot name its transport source.
/// </summary>
public enum MaterialTransportProvenanceCode : byte
{
    Background = 0,
    DetailedMesh = 1,
    CompactPrimitive = 2,
    FarField = 3,
    Unknown = byte.MaxValue
}

public static class MaterialTransportProvenanceEncoding
{
    public static float EncodeUnorm(MaterialTransportProvenanceCode code)
    {
        byte encoded = code switch
        {
            MaterialTransportProvenanceCode.Background => 0,
            MaterialTransportProvenanceCode.DetailedMesh => 1,
            MaterialTransportProvenanceCode.CompactPrimitive => 2,
            MaterialTransportProvenanceCode.FarField => 3,
            _ => byte.MaxValue
        };
        return encoded / (float)byte.MaxValue;
    }

    public static MaterialTransportProvenanceCode DecodeUnorm(float value)
    {
        if (!float.IsFinite(value))
            return MaterialTransportProvenanceCode.Unknown;

        byte encoded = (byte)Math.Clamp(
            (int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * byte.MaxValue),
            byte.MinValue,
            byte.MaxValue);
        return encoded switch
        {
            0 => MaterialTransportProvenanceCode.Background,
            1 => MaterialTransportProvenanceCode.DetailedMesh,
            2 => MaterialTransportProvenanceCode.CompactPrimitive,
            3 => MaterialTransportProvenanceCode.FarField,
            _ => MaterialTransportProvenanceCode.Unknown
        };
    }
}
