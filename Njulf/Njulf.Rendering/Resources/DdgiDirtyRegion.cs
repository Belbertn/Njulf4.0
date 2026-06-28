using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public enum DdgiDirtyReason : uint
    {
        Unknown = 0,
        GeometryAdded = 1,
        GeometryRemoved = 2,
        TransformChanged = 3,
        MaterialChanged = 4,
        EmissiveChanged = 5,
        LocalLightChanged = 6,
        DirectionalLightChanged = 7,
        StreamIn = 8,
        StreamOut = 9,
        Teleport = 10,
        AgeRefresh = 11
    }

    public readonly record struct DdgiDirtyRegion(
        BoundingBox Bounds,
        DdgiDirtyReason Reason);

    internal static class DdgiDirtyReasonPolicy
    {
        public static uint ToProbeUpdateFlags(DdgiDirtyReason reason)
        {
            uint flags = GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag;
            return reason switch
            {
                DdgiDirtyReason.GeometryAdded => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryAddedFlag,
                DdgiDirtyReason.GeometryRemoved => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryRemovedFlag,
                DdgiDirtyReason.TransformChanged => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTransformChangedFlag,
                DdgiDirtyReason.MaterialChanged => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonMaterialChangedFlag,
                DdgiDirtyReason.EmissiveChanged => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonEmissiveChangedFlag,
                DdgiDirtyReason.LocalLightChanged => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonLocalLightChangedFlag,
                DdgiDirtyReason.DirectionalLightChanged => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirectionalLightChangedFlag,
                DdgiDirtyReason.StreamIn => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamInFlag,
                DdgiDirtyReason.StreamOut => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamOutFlag,
                DdgiDirtyReason.Teleport => flags |
                    GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTeleportWarmupFlag,
                DdgiDirtyReason.AgeRefresh => GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag,
                _ => flags
            };
        }

        public static uint ResolvePriority(DdgiDirtyReason reason)
        {
            return reason switch
            {
                DdgiDirtyReason.GeometryAdded or
                DdgiDirtyReason.GeometryRemoved or
                DdgiDirtyReason.TransformChanged or
                DdgiDirtyReason.StreamIn or
                DdgiDirtyReason.StreamOut or
                DdgiDirtyReason.Teleport => DdgiProbeUpdateScheduler.PriorityDirtyGeometry,
                DdgiDirtyReason.MaterialChanged or
                DdgiDirtyReason.EmissiveChanged or
                DdgiDirtyReason.LocalLightChanged => DdgiProbeUpdateScheduler.PriorityDirtyLighting,
                DdgiDirtyReason.DirectionalLightChanged => DdgiProbeUpdateScheduler.PriorityDirectionalLight,
                DdgiDirtyReason.AgeRefresh => DdgiProbeUpdateScheduler.PriorityAgeRefresh,
                _ => DdgiProbeUpdateScheduler.PriorityDirtyBounds
            };
        }
    }
}
