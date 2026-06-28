using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    internal struct DdgiProbeSchedulerFeedback
    {
        public float LuminanceMean;
        public float LuminanceChange;
        public uint AgeFrames;
        public float IrradianceConfidence;
        public float VisibilityConfidence;
        public uint LastDirtyReasonFlags;
        public byte Initialized;

        public readonly bool HasSample => Initialized != 0;

        public readonly float CombinedConfidence =>
            Math.Clamp(MathF.Min(IrradianceConfidence, VisibilityConfidence), 0.0f, 1.0f);

        public readonly float VariabilityScore
        {
            get
            {
                float confidenceDeficit = 1.0f - CombinedConfidence;
                float dirtyImpulse = IsDirtyReason(LastDirtyReasonFlags) ? 0.35f : 0.0f;
                return Math.Clamp(LuminanceChange * 0.65f + confidenceDeficit * 0.35f + dirtyImpulse, 0.0f, 1.0f);
            }
        }

        public readonly bool IsStable =>
            HasSample &&
            LuminanceChange < 0.05f &&
            IrradianceConfidence >= 0.82f &&
            VisibilityConfidence >= 0.75f &&
            !IsDirtyReason(LastDirtyReasonFlags);

        public readonly bool IsLowConfidence => HasSample && CombinedConfidence < 0.55f;

        public static bool IsDirtyReason(uint flags)
        {
            const uint dirtyMask =
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryAddedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryRemovedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTransformChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonMaterialChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonEmissiveChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonLocalLightChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirectionalLightChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamInFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamOutFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTeleportWarmupFlag;

            return (flags & dirtyMask) != 0;
        }
    }
}
