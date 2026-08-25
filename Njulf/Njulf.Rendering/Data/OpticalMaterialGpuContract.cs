using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Packing contract for the four legacy padding words at the end of
/// <see cref="GPUMaterialExtensionData"/>. Reusing those words keeps the
/// 548-byte CPU/GPU material-extension ABI stable.
/// </summary>
public static class OpticalMaterialGpuContract
{
    public const int BoundaryKindShift = 0;
    public const int BoundaryKindMask = 0x3 << BoundaryKindShift;
    public const int CausticCasterPolicyShift = 8;
    public const int CausticCasterPolicyMask = 0x7 << CausticCasterPolicyShift;
    public const int VolumeTransmissionFlag = 1 << 16;
    public const int WaterSurfaceFlag = 1 << 17;

    public static int PackFlags(
        OpticalBoundaryKind boundaryKind,
        GiCausticCasterPolicy casterPolicy,
        bool volumeTransmission)
    {
        if (!Enum.IsDefined(boundaryKind))
            throw new ArgumentOutOfRangeException(nameof(boundaryKind));
        if (!Enum.IsDefined(casterPolicy))
            throw new ArgumentOutOfRangeException(nameof(casterPolicy));

        int packed = ((int)boundaryKind << BoundaryKindShift) & BoundaryKindMask;
        packed |= ((int)casterPolicy << CausticCasterPolicyShift) &
                  CausticCasterPolicyMask;
        if (volumeTransmission)
            packed |= VolumeTransmissionFlag;
        if (boundaryKind == OpticalBoundaryKind.WaterSurface)
            packed |= WaterSurfaceFlag;
        return packed;
    }

    public static OpticalBoundaryKind UnpackBoundaryKind(int packed) =>
        (OpticalBoundaryKind)((packed & BoundaryKindMask) >> BoundaryKindShift);

    public static GiCausticCasterPolicy UnpackCasterPolicy(int packed) =>
        (GiCausticCasterPolicy)((packed & CausticCasterPolicyMask) >>
                                CausticCasterPolicyShift);

    public static int PackHalf2(Vector2 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(nameof(value));
        uint low = BitConverter.HalfToUInt16Bits((Half)value.X);
        uint high = BitConverter.HalfToUInt16Bits((Half)value.Y);
        return unchecked((int)(low | (high << 16)));
    }

    public static Vector2 UnpackHalf2(int packed)
    {
        uint bits = unchecked((uint)packed);
        return new Vector2(
            (float)BitConverter.UInt16BitsToHalf((ushort)(bits & 0xffffu)),
            (float)BitConverter.UInt16BitsToHalf((ushort)(bits >> 16)));
    }

    public static GiCausticCasterPolicy ResolveCasterPolicy(
        GiCausticCasterPolicy policy,
        GiCausticParticipationMode legacyParticipation)
    {
        if (policy != GiCausticCasterPolicy.Default ||
            legacyParticipation == GiCausticParticipationMode.None)
        {
            return policy;
        }

        return legacyParticipation switch
        {
            GiCausticParticipationMode.MirrorHero =>
                GiCausticCasterPolicy.Mirror,
            GiCausticParticipationMode.ClosedDielectricHero =>
                GiCausticCasterPolicy.DielectricPriority,
            GiCausticParticipationMode.RoughSpecularReference =>
                GiCausticCasterPolicy.RoughSpecular,
            _ => GiCausticCasterPolicy.Default
        };
    }

    public static GiCausticParticipationMode ToLegacyParticipation(
        GiCausticCasterPolicy policy,
        GiTransmissionPolicy transmissionPolicy)
    {
        return policy switch
        {
            GiCausticCasterPolicy.Disabled or GiCausticCasterPolicy.Default =>
                GiCausticParticipationMode.None,
            GiCausticCasterPolicy.Mirror =>
                GiCausticParticipationMode.MirrorHero,
            GiCausticCasterPolicy.RoughSpecular =>
                GiCausticParticipationMode.RoughSpecularReference,
            GiCausticCasterPolicy.DielectricPriority
                when transmissionPolicy == GiTransmissionPolicy.Volume =>
                GiCausticParticipationMode.ClosedDielectricHero,
            _ => GiCausticParticipationMode.None
        };
    }
}
