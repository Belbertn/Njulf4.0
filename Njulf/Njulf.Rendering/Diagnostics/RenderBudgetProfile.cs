using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public enum RenderBudgetProfileKind
    {
        Development,
        LowSpec1080p30,
        MidSpec1080p60,
        HighSpec1440p60,
        Ultra4k60,
        StressUnlimited
    }

    public sealed record RenderBudgetProfile(
        RenderBudgetProfileKind Kind,
        string Name,
        uint OutputWidth,
        uint OutputHeight,
        float ResolutionScale,
        double TargetFrameMilliseconds,
        double CpuFrameBudgetMilliseconds,
        double GpuFrameBudgetMilliseconds,
        ulong GpuMemoryBudgetBytes,
        ulong UploadBudgetBytesPerFrame,
        int ObjectBudget,
        int MeshletBudget,
        int MaterialBudget,
        int TextureBudget,
        int LightBudget,
        int ShadowedLightBudget,
        int ReflectionProbeBudget,
        int TransparentObjectBudget)
    {
        public static RenderBudgetProfile Development { get; } = new(
            RenderBudgetProfileKind.Development,
            "Development 1080p60",
            1920,
            1080,
            1.0f,
            16.67,
            6.0,
            10.0,
            2UL * 1024UL * 1024UL * 1024UL,
            32UL * 1024UL * 1024UL,
            25_000,
            750_000,
            8_192,
            2_048,
            1_024,
            16,
            64,
            4_096);

        public static RenderBudgetProfile LowSpec1080p30 { get; } = Development with
        {
            Kind = RenderBudgetProfileKind.LowSpec1080p30,
            Name = "Low Spec 1080p30",
            TargetFrameMilliseconds = 33.33,
            CpuFrameBudgetMilliseconds = 10.0,
            GpuFrameBudgetMilliseconds = 18.0,
            GpuMemoryBudgetBytes = 1UL * 1024UL * 1024UL * 1024UL,
            UploadBudgetBytesPerFrame = 8UL * 1024UL * 1024UL,
            ObjectBudget = 12_000,
            MeshletBudget = 350_000,
            MaterialBudget = 2_048,
            TextureBudget = 768,
            LightBudget = 256,
            ShadowedLightBudget = 8,
            ReflectionProbeBudget = 16,
            TransparentObjectBudget = 1_024
        };

        public static RenderBudgetProfile MidSpec1080p60 { get; } = Development with
        {
            Kind = RenderBudgetProfileKind.MidSpec1080p60,
            Name = "Mid Spec 1080p60",
            CpuFrameBudgetMilliseconds = 5.0,
            GpuFrameBudgetMilliseconds = 11.0,
            UploadBudgetBytesPerFrame = 16UL * 1024UL * 1024UL
        };

        public static RenderBudgetProfile HighSpec1440p60 { get; } = Development with
        {
            Kind = RenderBudgetProfileKind.HighSpec1440p60,
            Name = "High Spec 1440p60",
            OutputWidth = 2560,
            OutputHeight = 1440,
            CpuFrameBudgetMilliseconds = 5.0,
            GpuFrameBudgetMilliseconds = 12.0,
            GpuMemoryBudgetBytes = 4UL * 1024UL * 1024UL * 1024UL,
            UploadBudgetBytesPerFrame = 24UL * 1024UL * 1024UL,
            ObjectBudget = 35_000,
            MeshletBudget = 1_000_000,
            MaterialBudget = 12_288,
            TextureBudget = 3_072,
            LightBudget = 1_536,
            ShadowedLightBudget = 24,
            ReflectionProbeBudget = 96,
            TransparentObjectBudget = 6_144
        };

        public static RenderBudgetProfile Ultra4k60 { get; } = Development with
        {
            Kind = RenderBudgetProfileKind.Ultra4k60,
            Name = "Ultra 4k60",
            OutputWidth = 3840,
            OutputHeight = 2160,
            CpuFrameBudgetMilliseconds = 5.0,
            GpuFrameBudgetMilliseconds = 14.0,
            GpuMemoryBudgetBytes = 6UL * 1024UL * 1024UL * 1024UL,
            ObjectBudget = 50_000,
            MeshletBudget = 1_500_000,
            MaterialBudget = 16_384,
            TextureBudget = 4_096,
            LightBudget = 2_048,
            ShadowedLightBudget = 32,
            ReflectionProbeBudget = 128,
            TransparentObjectBudget = 8_192
        };

        public static RenderBudgetProfile StressUnlimited { get; } = new(
            RenderBudgetProfileKind.StressUnlimited,
            "Stress Unlimited",
            3840,
            2160,
            1.0f,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            ulong.MaxValue,
            ulong.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue);

        public static IReadOnlyList<RenderBudgetProfile> Defaults { get; } =
        [
            Development,
            LowSpec1080p30,
            MidSpec1080p60,
            HighSpec1440p60,
            Ultra4k60,
            StressUnlimited
        ];

        public static RenderBudgetProfile GetDefault(RenderBudgetProfileKind kind)
        {
            foreach (RenderBudgetProfile profile in Defaults)
            {
                if (profile.Kind == kind)
                    return profile;
            }

            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown render budget profile.");
        }
    }
}
