using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Descriptors;

namespace Njulf.Rendering.Data
{
    public enum DdgiProbeVolumeKind : uint
    {
        Authored = 0,
        CameraClipmap = 1
    }

    public readonly record struct DdgiProbeVolumeRuntimeMetadata(
        DdgiProbeVolumeKind Kind,
        int CascadeIndex,
        int LogicalGridMinX,
        int LogicalGridMinY,
        int LogicalGridMinZ,
        int RingOffsetX,
        int RingOffsetY,
        int RingOffsetZ,
        float EdgeBlendFraction,
        uint Flags)
    {
        public static DdgiProbeVolumeRuntimeMetadata Authored { get; } = new(
            DdgiProbeVolumeKind.Authored,
            -1,
            0,
            0,
            0,
            0,
            0,
            0,
            0.0f,
            GlobalIlluminationProbeVolumeData.VolumeInitializedFlag |
            GlobalIlluminationProbeVolumeData.VolumeAuthoredPriorityFlag);
    }

    public static class GlobalIlluminationProbeVolumeData
    {
        public const int EnabledFlag = 1 << 0;
        public const int ProbeRelocationEnabledFlag = 1 << 1;
        public const int ProbeClassificationEnabledFlag = 1 << 2;
        public const uint VolumeInitializedFlag = 1u << 0;
        public const uint VolumeCameraRelativeFlag = 1u << 1;
        public const uint VolumeAuthoredPriorityFlag = 1u << 2;
        public const uint VolumeDebugDisplayFlag = 1u << 3;
        public const uint ProbeUpdateReasonNewCellFlag = 1u << 0;
        public const uint ProbeUpdateReasonDirtyBoundsFlag = 1u << 1;
        public const uint ProbeUpdateReasonVisibleFrustumFlag = 1u << 2;
        public const uint ProbeUpdateReasonAgeRefreshFlag = 1u << 3;
        public const uint ProbeUpdateReasonTeleportWarmupFlag = 1u << 4;
        public const uint ProbeUpdateReasonAuthoredVolumeFlag = 1u << 5;
        public const uint ProbeUpdateReasonOutsideFrustumSafetyFlag = 1u << 6;
        public const uint ProbeUpdateReasonCameraRelativeFlag = 1u << 7;

        public const uint IrradianceTexelsPerProbe = 8;
        public const uint VisibilityTexelsPerProbe = 16;
        public const int ShaderMaxRaysPerProbe = 256;
        public const ulong Rgba16FloatBytesPerTexel = 8;
        public const ulong Rg16FloatBytesPerTexel = 4;
        public const ulong IrradianceBytesPerProbe =
            IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * Rgba16FloatBytesPerTexel;
        public const ulong VisibilityBytesPerProbe =
            VisibilityTexelsPerProbe * VisibilityTexelsPerProbe * Rg16FloatBytesPerTexel;
        public const ulong AtlasBytesPerProbe =
            IrradianceBytesPerProbe + VisibilityBytesPerProbe;

        public static readonly ulong HeaderSize = (ulong)Marshal.SizeOf<GPUDdgiProbeVolumeHeader>();
        public static readonly ulong VolumeStride = (ulong)Marshal.SizeOf<GPUDdgiProbeVolume>();
        public static readonly ulong ProbeStateStride = (ulong)Marshal.SizeOf<GPUDdgiProbeState>();
        public static readonly ulong ProbeUpdateRequestStride = (ulong)Marshal.SizeOf<GPUDdgiProbeUpdateRequest>();
        public static readonly ulong ProbeRelocationClassificationStride =
            (ulong)Marshal.SizeOf<GPUDdgiProbeRelocationClassification>();

        public static int BuildVolumes(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeVolume> destination,
            out int totalProbeCount,
            out int activeProbeCount,
            out int raysPerProbe,
            out int maxProbeUpdatesPerFrame,
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata>? runtimeMetadata = null)
        {
            if (authoredVolumes == null)
                throw new ArgumentNullException(nameof(authoredVolumes));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            totalProbeCount = 0;
            activeProbeCount = 0;
            raysPerProbe = 0;
            maxProbeUpdatesPerFrame = 0;

            if (!settings.EffectiveUseDdgi || destination.IsEmpty)
                return 0;

            int activeProbeBudget = CalculateActiveProbeBudget(settings);
            int written = 0;
            for (int i = 0; i < authoredVolumes.Count && written < destination.Length; i++)
            {
                GlobalIlluminationProbeVolume? volume = authoredVolumes[i];
                if (volume == null || !volume.Enabled)
                    continue;

                int volumeProbeCount = volume.ProbeCount;
                if (volumeProbeCount > Math.Max(0, activeProbeBudget - activeProbeCount))
                    continue;

                int firstProbeIndex = totalProbeCount;
                totalProbeCount = checked(totalProbeCount + volumeProbeCount);
                activeProbeCount += volumeProbeCount;
                DdgiProbeVolumeRuntimeMetadata metadata = runtimeMetadata != null && i < runtimeMetadata.Count
                    ? runtimeMetadata[i]
                    : DdgiProbeVolumeRuntimeMetadata.Authored;
                int volumeRaysPerProbe = EffectiveRaysPerProbe(volume, settings, metadata);
                int volumeMaxProbeUpdatesPerFrame = Math.Min(volume.MaxProbeUpdatesPerFrame, volumeProbeCount);
                raysPerProbe = Math.Max(raysPerProbe, volumeRaysPerProbe);
                maxProbeUpdatesPerFrame = checked(maxProbeUpdatesPerFrame + volumeMaxProbeUpdatesPerFrame);
                destination[written] = BuildGpuVolume(volume, firstProbeIndex, metadata, settings, volumeRaysPerProbe, volumeMaxProbeUpdatesPerFrame);
                written++;
            }

            maxProbeUpdatesPerFrame = Math.Min(
                Math.Min(maxProbeUpdatesPerFrame, settings.DdgiMaxProbeUpdatesPerFrame),
                activeProbeCount);
            return written;
        }

        public static GPUDdgiProbeVolumeHeader BuildHeader(
            int volumeCount,
            int totalProbeCount,
            int activeProbeCount,
            int raysPerProbe,
            int maxProbeUpdatesPerFrame,
            GlobalIlluminationSettings settings,
            int probeStateBufferIndex)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            uint flags = 0;
            if (settings.EffectiveUseDdgi && volumeCount > 0)
            {
                flags |= EnabledFlag;
                if (settings.DdgiProbeRelocationEnabled)
                    flags |= ProbeRelocationEnabledFlag;
                if (settings.DdgiProbeClassificationEnabled)
                    flags |= ProbeClassificationEnabledFlag;
            }

            return new GPUDdgiProbeVolumeHeader
            {
                VolumeCount = volumeCount,
                ProbeCount = totalProbeCount,
                ActiveProbeCount = activeProbeCount,
                RaysPerProbe = raysPerProbe,
                MaxProbeUpdatesPerFrame = maxProbeUpdatesPerFrame,
                IrradianceTextureIndex = BindlessIndex.DefaultBlackTexture,
                VisibilityTextureIndex = BindlessIndex.DefaultBlackTexture,
                ProbeStateBufferIndex = probeStateBufferIndex,
                Flags = flags,
                DebugView = (uint)settings.DebugView,
                IrradianceTexelsPerProbe = IrradianceTexelsPerProbe,
                VisibilityTexelsPerProbe = VisibilityTexelsPerProbe,
                Intensity = settings.IndirectIntensity
            };
        }

        public static ulong EstimateIrradianceAtlasBytes(int probeCount)
        {
            return EstimateProbeAtlasBytes(probeCount, IrradianceTexelsPerProbe, Rgba16FloatBytesPerTexel);
        }

        public static ulong EstimateVisibilityAtlasBytes(int probeCount)
        {
            return EstimateProbeAtlasBytes(probeCount, VisibilityTexelsPerProbe, Rg16FloatBytesPerTexel);
        }

        public static ulong EstimateTextureBytes(int probeCount)
        {
            return checked(EstimateIrradianceAtlasBytes(probeCount) + EstimateVisibilityAtlasBytes(probeCount));
        }

        public static int CalculateAtlasProbeCapacity(ulong atlasBudgetBytes)
        {
            if (atlasBudgetBytes < AtlasBytesPerProbe)
                return 0;

            ulong capacity = atlasBudgetBytes / AtlasBytesPerProbe;
            return capacity > GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget
                ? GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget
                : (int)capacity;
        }

        public static int CalculateActiveProbeBudget(GlobalIlluminationSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            int atlasProbeBudget = CalculateAtlasProbeCapacity(settings.DdgiAtlasMemoryBudgetBytes);
            return Math.Clamp(
                Math.Min(settings.DdgiMaxActiveProbes, atlasProbeBudget),
                0,
                GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget);
        }

        private static GPUDdgiProbeVolume BuildGpuVolume(
            GlobalIlluminationProbeVolume volume,
            int firstProbeIndex,
            DdgiProbeVolumeRuntimeMetadata metadata,
            GlobalIlluminationSettings settings,
            int raysPerProbe,
            int maxProbeUpdatesPerFrame)
        {
            Vector3 spacing = volume.ProbeSpacing;
            float maxRayDistance = EffectiveMaxRayDistance(volume);
            float edgeBlendDistance = metadata.EdgeBlendFraction * MinAxis(volume.Size);
            return new GPUDdgiProbeVolume
            {
                OriginAndFirstProbeIndex = new Vector4(volume.Origin, firstProbeIndex),
                SizeAndProbeCountX = new Vector4(volume.Size, volume.ProbeCountX),
                ProbeSpacingAndProbeCountY = new Vector4(spacing, volume.ProbeCountY),
                BiasAndProbeCountZ = new Vector4(volume.NormalBias, volume.ViewBias, maxRayDistance, volume.ProbeCountZ),
                RayAndUpdateParams = new Vector4(raysPerProbe, Math.Min(maxProbeUpdatesPerFrame, settings.DdgiMaxProbeUpdatesPerFrame), volume.Intensity, volume.Hysteresis),
                DebugColorAndFlags = ResolveDebugColorAndFlags(volume, metadata),
                ClipmapGridMinAndKind = new Vector4(
                    metadata.LogicalGridMinX,
                    metadata.LogicalGridMinY,
                    metadata.LogicalGridMinZ,
                    (uint)metadata.Kind),
                ClipmapRingOffsetAndCascade = new Vector4(
                    metadata.RingOffsetX,
                    metadata.RingOffsetY,
                    metadata.RingOffsetZ,
                    metadata.CascadeIndex),
                ClipmapBlendAndFlags = new Vector4(
                    metadata.EdgeBlendFraction,
                    edgeBlendDistance,
                    firstProbeIndex,
                    metadata.Flags)
            };
        }

        private static Vector4 ResolveDebugColorAndFlags(
            GlobalIlluminationProbeVolume volume,
            DdgiProbeVolumeRuntimeMetadata metadata)
        {
            Vector3 color = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap
                ? new Vector3(0.25f, 0.55f, 1.0f)
                : new Vector3(0.15f, 0.9f, 0.65f);
            return new Vector4(color, volume.Enabled ? EnabledFlag : 0);
        }

        private static int EffectiveRaysPerProbe(
            GlobalIlluminationProbeVolume volume,
            GlobalIlluminationSettings settings,
            DdgiProbeVolumeRuntimeMetadata metadata)
        {
            int cascadeIndex = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap ? metadata.CascadeIndex : -1;
            return settings.ResolveDdgiRaysPerProbe(volume.RaysPerProbe, cascadeIndex);
        }

        private static float EffectiveMaxRayDistance(GlobalIlluminationProbeVolume volume)
        {
            return MathF.Max(volume.MaxRayDistance, 0.1f);
        }

        private static float MinAxis(Vector3 value)
        {
            return MathF.Min(value.X, MathF.Min(value.Y, value.Z));
        }

        private static ulong EstimateProbeAtlasBytes(int probeCount, uint texelsPerProbe, ulong bytesPerTexel)
        {
            if (probeCount <= 0 || texelsPerProbe == 0)
                return 0;

            ulong tileTexels = (ulong)texelsPerProbe * texelsPerProbe;
            return checked((ulong)probeCount * tileTexels * bytesPerTexel);
        }
    }
}
