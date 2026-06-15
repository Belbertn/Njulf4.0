using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Pipeline;

[Flags]
public enum FramePassRole
{
    None = 0,
    DirtyUpload = 1 << 0,
    Animation = 1 << 1,
    VisibilityProducer = 1 << 2,
    VisibilityConsumer = 1 << 3,
    DepthProducer = 1 << 4,
    LightCulling = 1 << 5,
    Shadow = 1 << 6,
    Shading = 1 << 7,
    Transparency = 1 << 8,
    Particle = 1 << 9,
    Post = 1 << 10,
    Diagnostics = 1 << 11,
    Presentation = 1 << 12
}

public sealed record FramePassAuditEntry(
    string PassName,
    FramePassRole Roles,
    IReadOnlyList<string> DependsOn,
    bool CanSkipWhenZeroWork,
    string Reason);

public sealed record FrameOrderAudit(
    IReadOnlyList<FramePassAuditEntry> Entries,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public string Dump()
    {
        return string.Join(Environment.NewLine, Entries.Select(entry =>
            $"{entry.PassName}: {entry.Roles} after [{string.Join(", ", entry.DependsOn)}] skipZero={entry.CanSkipWhenZeroWork} - {entry.Reason}"));
    }
}

public static class VisibilityFirstFramePlanner
{
    public static IReadOnlyList<FramePassAuditEntry> ProductionAuditEntries { get; } = new[]
    {
        new FramePassAuditEntry("SkinningPass", FramePassRole.Animation | FramePassRole.VisibilityProducer, Array.Empty<string>(), false, "External frame setup pass updates skinned data before visibility-sensitive rendering."),
        new FramePassAuditEntry("GpuVisibilityPass", FramePassRole.VisibilityProducer | FramePassRole.VisibilityConsumer, new[] { "SkinningPass" }, false, "Runs object visibility, LOD, representation selection, and compact render-list generation before expensive shading."),
        new FramePassAuditEntry("DepthPrePass", FramePassRole.DepthProducer | FramePassRole.VisibilityConsumer, new[] { "GpuVisibilityPass" }, true, "Produces scene-resolution depth for Hi-Z, light culling, SSAO, fog, transparency, and opaque depth rejection."),
        new FramePassAuditEntry("MotionVectorPass", FramePassRole.VisibilityConsumer | FramePassRole.Post, new[] { "DepthPrePass" }, true, "Consumes compact opaque work and stable previous transforms for TAA motion vectors."),
        new FramePassAuditEntry("HiZBuildPass", FramePassRole.VisibilityProducer, new[] { "DepthPrePass" }, true, "Builds occlusion hierarchy immediately after depth."),
        new FramePassAuditEntry("AmbientOcclusionPass", FramePassRole.Post | FramePassRole.VisibilityConsumer, new[] { "HiZBuildPass" }, true, "Uses scene depth before forward shading consumes AO."),
        new FramePassAuditEntry("AmbientOcclusionBlurPass", FramePassRole.Post, new[] { "AmbientOcclusionPass" }, true, "Filters AO before forward shading."),
        new FramePassAuditEntry("TiledLightCullingPass", FramePassRole.LightCulling | FramePassRole.VisibilityConsumer, new[] { "DepthPrePass", "AmbientOcclusionBlurPass" }, true, "Builds light lists from current frame depth before forward shading."),
        new FramePassAuditEntry("DirectionalShadowPass", FramePassRole.Shadow | FramePassRole.VisibilityConsumer, new[] { "GpuVisibilityPass", "TiledLightCullingPass" }, true, "Consumes directional shadow caster lists from the visibility pass."),
        new FramePassAuditEntry("SpotShadowPass", FramePassRole.Shadow | FramePassRole.VisibilityConsumer, new[] { "GpuVisibilityPass", "TiledLightCullingPass" }, true, "Consumes local spot shadow caster lists from the visibility pass."),
        new FramePassAuditEntry("PointShadowPass", FramePassRole.Shadow | FramePassRole.VisibilityConsumer, new[] { "GpuVisibilityPass", "TiledLightCullingPass" }, true, "Consumes local point shadow caster lists from the visibility pass."),
        new FramePassAuditEntry("ForwardPlusPass", FramePassRole.Shading | FramePassRole.VisibilityConsumer, new[] { "TiledLightCullingPass", "PointShadowPass" }, true, "Consumes visible opaque meshlets and light tiles."),
        new FramePassAuditEntry("SkyboxPass", FramePassRole.Shading, new[] { "ForwardPlusPass" }, false, "Adds environment after opaque depth/color."),
        new FramePassAuditEntry("TransparentForwardPass", FramePassRole.Transparency | FramePassRole.VisibilityConsumer, new[] { "ForwardPlusPass" }, true, "Consumes transparent candidates after opaque depth."),
        new FramePassAuditEntry("WeightedOitCompositePass", FramePassRole.Transparency | FramePassRole.Post, new[] { "TransparentForwardPass" }, true, "Composites weighted transparent accumulation back into HDR scene color."),
        new FramePassAuditEntry("ParticleSimulationPass", FramePassRole.Particle | FramePassRole.VisibilityProducer, new[] { "WeightedOitCompositePass" }, true, "Simulates and compacts particle render buffers before graphics particle rendering."),
        new FramePassAuditEntry("ParticlePass", FramePassRole.Particle | FramePassRole.VisibilityConsumer, new[] { "ParticleSimulationPass" }, true, "Uses synchronized particle buffers and scene depth for soft particles."),
        new FramePassAuditEntry("DebugDrawPass", FramePassRole.Diagnostics, new[] { "ParticlePass" }, true, "Draws overlays after scene contributors."),
        new FramePassAuditEntry("FogPass", FramePassRole.Post, new[] { "DebugDrawPass" }, true, "Applies analytic fog after scene color contributors."),
        new FramePassAuditEntry("AutoExposurePass", FramePassRole.Post, new[] { "FogPass" }, true, "Measures HDR luminance before bloom/composite."),
        new FramePassAuditEntry("BloomPass", FramePassRole.Post, new[] { "AutoExposurePass" }, true, "Processes HDR color before composite."),
        new FramePassAuditEntry("ToneMapCompositePass", FramePassRole.Post, new[] { "BloomPass" }, false, "Composites scene to LDR."),
        new FramePassAuditEntry("AntiAliasingPass", FramePassRole.Post | FramePassRole.Presentation, new[] { "ToneMapCompositePass" }, false, "Final AA/upscale/present output.")
    };

    public static FrameOrderAudit Audit(IReadOnlyList<string> passOrder)
    {
        if (passOrder == null)
            throw new ArgumentNullException(nameof(passOrder));

        var errors = new List<string>();
        var indexByName = passOrder.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        var entries = ProductionAuditEntries
            .Where(entry => indexByName.ContainsKey(entry.PassName))
            .OrderBy(entry => indexByName[entry.PassName])
            .ToArray();

        foreach (string passName in passOrder)
        {
            if (!ProductionAuditEntries.Any(entry => string.Equals(entry.PassName, passName, StringComparison.Ordinal)))
                errors.Add($"{passName} is missing from the visibility-first dependency audit.");
        }

        foreach (FramePassAuditEntry entry in entries)
        {
            foreach (string dependency in entry.DependsOn)
            {
                if (indexByName.TryGetValue(dependency, out int dependencyIndex) &&
                    indexByName.TryGetValue(entry.PassName, out int passIndex) &&
                    dependencyIndex > passIndex)
                {
                    errors.Add($"{entry.PassName} must run after {dependency}.");
                }
            }
        }

        RequireBefore("GpuVisibilityPass", "DepthPrePass");
        RequireBefore("GpuVisibilityPass", "DirectionalShadowPass");
        RequireBefore("GpuVisibilityPass", "SpotShadowPass");
        RequireBefore("GpuVisibilityPass", "PointShadowPass");
        RequireBefore("DepthPrePass", "HiZBuildPass");
        RequireBefore("DepthPrePass", "MotionVectorPass");
        RequireBefore("DepthPrePass", "TiledLightCullingPass");
        RequireBefore("TiledLightCullingPass", "ForwardPlusPass");
        RequireBefore("TiledLightCullingPass", "DirectionalShadowPass");
        RequireBefore("TiledLightCullingPass", "SpotShadowPass");
        RequireBefore("TiledLightCullingPass", "PointShadowPass");
        RequireBefore("PointShadowPass", "ForwardPlusPass");
        RequireBefore("ForwardPlusPass", "TransparentForwardPass");
        RequireBefore("TransparentForwardPass", "WeightedOitCompositePass");
        RequireBefore("WeightedOitCompositePass", "ParticlePass");
        RequireBefore("ParticlePass", "FogPass");
        return new FrameOrderAudit(entries, errors);

        void RequireBefore(string producer, string consumer)
        {
            if (!indexByName.TryGetValue(producer, out int producerIndex) ||
                !indexByName.TryGetValue(consumer, out int consumerIndex))
            {
                return;
            }

            if (producerIndex > consumerIndex)
                errors.Add($"{producer} must run before {consumer} for visibility-first frame ordering.");
        }
    }
}
