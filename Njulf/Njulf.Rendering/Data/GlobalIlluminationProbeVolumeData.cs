using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Descriptors;

namespace Njulf.Rendering.Data
{
    public static class GlobalIlluminationProbeVolumeData
    {
        public const int EnabledFlag = 1 << 0;
        public const int ProbeRelocationEnabledFlag = 1 << 1;
        public const int ProbeClassificationEnabledFlag = 1 << 2;

        public const uint IrradianceTexelsPerProbe = 8;
        public const uint VisibilityTexelsPerProbe = 16;
        public const ulong Rgba16FloatBytesPerTexel = 8;
        public const ulong Rg16FloatBytesPerTexel = 4;

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
            out int maxProbeUpdatesPerFrame)
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

            int written = 0;
            for (int i = 0; i < authoredVolumes.Count && written < destination.Length; i++)
            {
                GlobalIlluminationProbeVolume? volume = authoredVolumes[i];
                if (volume == null || !volume.Enabled)
                    continue;

                int firstProbeIndex = totalProbeCount;
                int volumeProbeCount = volume.ProbeCount;
                totalProbeCount = checked(totalProbeCount + volumeProbeCount);
                activeProbeCount += volumeProbeCount;
                raysPerProbe = Math.Max(raysPerProbe, volume.RaysPerProbe);
                maxProbeUpdatesPerFrame = checked(maxProbeUpdatesPerFrame + Math.Min(volume.MaxProbeUpdatesPerFrame, volumeProbeCount));
                destination[written] = BuildGpuVolume(volume, firstProbeIndex);
                written++;
            }

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
                flags |= EnabledFlag | ProbeRelocationEnabledFlag | ProbeClassificationEnabledFlag;

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

        private static GPUDdgiProbeVolume BuildGpuVolume(GlobalIlluminationProbeVolume volume, int firstProbeIndex)
        {
            Vector3 spacing = volume.ProbeSpacing;
            return new GPUDdgiProbeVolume
            {
                OriginAndFirstProbeIndex = new Vector4(volume.Origin, firstProbeIndex),
                SizeAndProbeCountX = new Vector4(volume.Size, volume.ProbeCountX),
                ProbeSpacingAndProbeCountY = new Vector4(spacing, volume.ProbeCountY),
                BiasAndProbeCountZ = new Vector4(volume.NormalBias, volume.ViewBias, volume.MaxRayDistance, volume.ProbeCountZ),
                RayAndUpdateParams = new Vector4(volume.RaysPerProbe, volume.MaxProbeUpdatesPerFrame, volume.Intensity, volume.Hysteresis),
                DebugColorAndFlags = new Vector4(0.15f, 0.9f, 0.65f, volume.Enabled ? EnabledFlag : 0)
            };
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
