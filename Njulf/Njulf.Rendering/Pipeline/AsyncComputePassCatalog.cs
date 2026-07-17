using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Disposition of every compute-only renderer pass. This is deliberately a small, executable
/// catalog rather than a comment in a renderer method: adding a compute pass now requires an
/// explicit decision about ownership modeling and Auto eligibility.
/// </summary>
public enum AsyncComputePassClassification
{
    ProductionAsyncCandidate,
    ValidationOnlyCandidate,
    GraphicsQueueComputeByDesign
}

/// <summary>
/// Concise synchronization audit record for one compute pass. Producer/consumer fields name the
/// nearest relevant pipeline stages; detailed concrete ranges live in the render-graph declaration.
/// </summary>
public sealed record AsyncComputePassAuditEntry(
    string PassName,
    AsyncComputePassClassification Classification,
    string Producers,
    string Consumers,
    string Rationale);

public static class AsyncComputePassCatalog
{
    private static readonly AsyncComputePassAuditEntry[] Entries =
    [
        Candidate("AmbientOcclusionBlurPass", "AO generation + depth", "Forward+", "Blur output has a late first consumer and independent graphics can overlap."),
        Candidate("HiZBuildPass", "Depth prepass", "Visibility compaction", "Auto timing gate rejects immediate-consumer workloads."),
        Candidate("SsgiTracePass", "Forward trace source, depth, normal, material", "SSGI temporal", "Atomic SSGI chain; never scheduled independently."),
        Candidate("SsgiTemporalPass", "SSGI trace/history/motion", "SSGI denoise", "Atomic SSGI chain; never scheduled independently."),
        Candidate("SsgiDenoisePass", "SSGI trace/temporal history", "SSGI composite", "Atomic SSGI chain; Auto requires unrelated graphics overlap."),
        Candidate("FarFieldClipmapBakePass", "Scene geometry/material uploads", "Simple DDGI trace or next-frame sampling", "Grouped with adjacent DDGI work when it is consumed immediately."),
        Candidate("SimpleDdgiTracePass", "TLAS, scene material/light/environment state", "Simple DDGI relocate/blend", "All ray-query inputs and writable DDGI allocations have concrete contracts."),
        Candidate("SimpleDdgiRelocateClassifyPass", "Simple DDGI trace/state", "Simple DDGI transport/blend", "Part of the indivisible simple-DDGI update segment."),
        Candidate("SimpleDdgiTransportPass", "Simple DDGI cached source and published irradiance", "Simple DDGI blend/publication", "Explicit Jacobi transport remains in the indivisible simple-DDGI update segment."),
        Candidate("SimpleDdgiBlendPass", "Simple DDGI trace/state", "Transparent/next-frame GI sampling", "Atlas/state ownership returns at the first real graphics consumer."),
        Candidate("DdgiSchedulePass", "Full-DDGI scheduler uploads", "Full-DDGI trace", "Optional at runtime; omitted from a plan when the CPU scheduler is selected."),
        Candidate("DdgiTracePass", "TLAS, scene material/light/environment and scheduler state", "Full-DDGI blend", "All ray-query and scheduler resources are allocation-bound."),
        Candidate("DdgiBlendPass", "Full-DDGI trace", "Full-DDGI relocation/publish", "Part of the full-DDGI update segment."),
        Candidate("DdgiRelocateClassifyPass", "Full-DDGI atlas/state", "Full-DDGI publish", "Part of the full-DDGI update segment."),
        Candidate("DdgiPublishPass", "Full-DDGI update state", "Next-frame GI sampling", "Publication remains in the same compute segment and is frame-fence covered."),
        Candidate("FogPass", "Scene color/depth", "Exposure, bloom, tone-map", "Concrete image imports and first-consumer waits are modeled."),
        Candidate("BloomPass", "Scene/fog output", "Tone-map", "Atomic mip-chain scheduling is timing-gated."),
        Candidate("GpuParticleResetPass", "Per-frame particle allocation", "GPU particle simulation", "Grouped reset/simulate/sort segment; zero-emitter frames stay on graphics."),
        Candidate("GpuParticleSimulatePass", "Emitter/frame uploads and particle state", "GPU particle sort", "Per-frame uploads and outputs have explicit ownership handoffs."),
        Candidate("GpuParticleSortPass", "Particle simulation output and frame data", "ParticlePass", "Render instances and indirect arguments acquire immediately before drawing."),

        Graphics("SceneOpaqueCompactionPass", "Previous Hi-Z and scene uploads", "Shadow/depth/forward mesh dispatch", "Early graphics command stream producer; broad scene-submission aliases are intentionally not async-modeled."),
        Graphics("ForwardVisibilityCompactionPass", "Hi-Z and scene submissions", "Forward+ mesh dispatch", "Immediate forward consumer leaves no useful overlap window."),
        Graphics("TiledLightCullingPass", "Depth", "Forward+", "Immediate consumer and shared light-tile buffer retain graphics-queue execution."),
        Graphics("AmbientOcclusionPass", "Depth", "AO blur/forward", "Producer is retained with graphics; only the blur chain is independently profitable."),
        Graphics("AutoExposurePass", "Scene/fog color", "Bloom/tone-map", "Current exposure state and immediate post-processing consumers retain graphics-queue execution."),
        Graphics("FoliageCullPass", "Foliage/scene uploads", "Shadow/depth/forward", "Recorded before graph segments and shares graphics submission resources."),
        Graphics("SkinningPass", "Animation uploads", "Mesh/shadow/depth/forward", "Current skinning buffers are consumed across early graphics passes with no modeled ownership split.")
    ];

    public static IReadOnlyList<AsyncComputePassAuditEntry> All => Entries;

    public static IReadOnlyList<string> ProductionCandidatePasses => Entries
        .Where(entry => entry.Classification == AsyncComputePassClassification.ProductionAsyncCandidate)
        .Select(entry => entry.PassName)
        .ToArray();

    public static AsyncComputePassClassification GetClassification(string passName)
    {
        if (string.IsNullOrWhiteSpace(passName))
            throw new ArgumentException("Pass name is required.", nameof(passName));

        AsyncComputePassAuditEntry? entry = Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.PassName, passName, StringComparison.Ordinal));
        if (entry == null)
            throw new InvalidOperationException($"Compute pass '{passName}' has no async-compute audit classification.");

        return entry.Classification;
    }

    public static bool IsProductionCandidate(string passName) =>
        GetClassification(passName) == AsyncComputePassClassification.ProductionAsyncCandidate;

    private static AsyncComputePassAuditEntry Candidate(string passName, string producers, string consumers, string rationale) =>
        new(passName, AsyncComputePassClassification.ProductionAsyncCandidate, producers, consumers, rationale);

    private static AsyncComputePassAuditEntry Graphics(string passName, string producers, string consumers, string rationale) =>
        new(passName, AsyncComputePassClassification.GraphicsQueueComputeByDesign, producers, consumers, rationale);
}
