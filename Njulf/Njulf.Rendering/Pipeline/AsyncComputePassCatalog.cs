using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;

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
        Candidate("FarFieldClipmapBakePass", "Scene geometry/material uploads", "Simple DDGI trace or next-frame sampling", "Grouped with adjacent DDGI work when it is consumed immediately."),
        Candidate("SimpleDdgiSchedulePass", "Simple DDGI frame/policy/delta uploads", "Simple DDGI trace/relocate consumers", "The resident arena is the producer of all fixed indirect commands."),
        Candidate("SimpleDdgiTracePass", "TLAS, scene material/light/environment state", "Simple DDGI relocate/blend", "All ray-query inputs and writable DDGI allocations have concrete contracts."),
        Candidate("SimpleDdgiRelocateClassifyPass", "Simple DDGI trace/state", "Simple DDGI transport/blend", "Part of the indivisible simple-DDGI update segment."),
        Candidate("SimpleDdgiDirectionalRadiancePass", "Simple DDGI completed blend/source cache", "Directional SH sidecar and compact publication", "The optional FP32 reduction and checked publication are split internally but remain inside the indivisible update segment."),
        Candidate("SimpleDdgiAcceleratedSolvePass", "Simple DDGI cached source and relocation state", "Simple DDGI publication", "Transport, blend, and intermediate canonical publication are serialized within each cached sweep."),
        Candidate("SimpleDdgiTransportPass", "Simple DDGI cached source and published irradiance", "Simple DDGI blend/publication", "Explicit Jacobi transport remains in the indivisible simple-DDGI update segment."),
        Candidate("SimpleDdgiBlendPass", "Simple DDGI trace/state", "Simple DDGI publication", "Private transport remains unobservable until the following publication pass."),
        Candidate("SimpleDdgiPublishPass", "Simple DDGI blend/private transport", "Transparent/next-frame GI sampling", "GPU-driven atlas publication completes the indivisible Simple-DDGI update segment."),
        Candidate("SimpleDdgiTransportAuditPass", "Frozen canonical transport atlas and source cache", "Delayed audit-summary readback", "The audit is cached-source-only and writes only the bounded scheduler summary."),
        Candidate("SimpleDdgiSchedulerCommitPass", "Simple DDGI publication and outcomes", "Delayed feedback/next-frame CPU policy", "Commit is the sole resident lifecycle publication boundary and exports a fixed summary."),
        Candidate("FogPass", "Scene color/depth", "Exposure, bloom, tone-map", "Concrete image imports and first-consumer waits are modeled."),
        Candidate("BloomPass", "Scene/fog output", "Tone-map", "Atomic mip-chain scheduling is timing-gated."),
        Candidate("GpuParticleResetPass", "Per-frame particle allocation", "GPU particle simulation", "Grouped reset/simulate/sort segment; zero-emitter frames stay on graphics."),
        Candidate("GpuParticleSimulatePass", "Emitter/frame uploads and particle state", "GPU particle sort", "Per-frame uploads and outputs have explicit ownership handoffs."),
        Candidate("GpuParticleSortPass", "Particle simulation output and frame data", "ParticlePass", "Render instances and indirect arguments acquire immediately before drawing."),

        Graphics("SceneOpaqueCompactionPass", "Previous Hi-Z and scene uploads", "Shadow/depth/forward mesh dispatch", "Early graphics command stream producer; broad scene-submission aliases are intentionally not async-modeled."),
        Graphics("ForwardVisibilityCompactionPass", "Hi-Z and scene submissions", "Forward+ mesh dispatch", "Immediate forward consumer leaves no useful overlap window."),
        Graphics("DirectionalRayShadowPass", "Visible depth, TLAS, and ray-scene metadata", "Forward+ directional lighting", "The full-resolution mask has an immediate fragment consumer and remains on the graphics queue until measured overlap justifies ownership transfers."),
        Graphics("AreaRayShadowPass", "Visible depth, selected area lights, TLAS, and ray-scene metadata", "Forward+ area lighting", "The packed full-resolution masks have an immediate fragment consumer and remain on the graphics queue."),
        Graphics("HybridReflectionSsrPass", "Opaque receiver payload, SceneColor, Hi-Z, and history metadata", "Ray-query recovery and analytic resolve", "Classification shares SceneColor and one descriptor/history transaction with the immediately adjacent reflection stages."),
        Graphics("HybridReflectionRayQueryPass", "SSR recovery queue and shared TLAS", "Reflection resolve", "The bounded indirect ray-query workload writes the same raw result images consumed immediately by resolve."),
        Graphics("HybridReflectionDdgiBasePass", "Unresolved reflection receivers and directional DDGI", "Reflection resolve", "The material-aware DDGI base fills unresolved pixels immediately before strict source resolution."),
        Graphics("HybridReflectionResolvePass", "SSR/ray results and analytic probes/environment", "Temporal reflection accumulation", "Strict source selection completes immediately before temporal reconstruction."),
        Graphics("HybridReflectionTemporalPass", "Raw reflection result, motion, and previous history", "Spatial reflection filter", "Current history publication is part of the serial reflection denoising transaction."),
        Graphics("HybridReflectionSpatialPass", "Current reflection history and receiver payload", "Reflection composite", "The ping-pong filter has an immediate SceneColor consumer."),
        Graphics("HybridReflectionCompositePass", "Filtered reflection history and SceneColor", "Forward transparency", "SceneColor storage mutation must complete before transparent color-attachment rendering."),
        Graphics("OpaqueSceneColorSnapshotPass", "Opaque SceneColor after reflection composition", "Sorted and weighted transparent SSR", "The immutable snapshot reuses hybrid filter scratch and is consumed immediately by fragment sampling."),
        Graphics("TiledLightCullingPass", "Depth", "Forward+", "Immediate consumer and shared light-tile buffer retain graphics-queue execution."),
        Graphics("SimpleDdgiLightTreePass", "Canonical light buffer and revisions", "Simple DDGI ray-hit shading", "Inactive-bank publication, state verification readback, and the immediate trace consumer share the graphics-queue descriptor transaction."),
        Graphics("AmbientOcclusionPass", "Depth", "AO blur/forward", "Producer is retained with graphics; only the blur chain is independently profitable."),
        Graphics("SimpleDdgiPageDemandPass", "Depth and receiver feedback", "Simple DDGI page reconciliation", "Demand collection opens the serial sparse-residency transaction immediately before mutation."),
        Graphics("SimpleDdgiPageResidencyPass", "Simple DDGI page demand and retained mappings", "Simple DDGI scheduler and physical payload consumers", "Page-table mutation and every same-frame payload consumer remain in one graphics-queue ownership segment."),
        Graphics("SimpleDdgiPageFeedbackPass", "Simple DDGI scheduler commit and page publication", "Delayed residency feedback readback", "Feedback closes the serial sparse-residency transaction after all publication writes."),
        Graphics("AutoExposurePass", "Scene/fog color", "Bloom/tone-map", "Current exposure state and immediate post-processing consumers retain graphics-queue execution."),
        Graphics("FoliageCullPass", "Foliage/scene uploads", "Shadow/depth/forward", "Recorded before graph segments and shares graphics submission resources."),
        Graphics("SkinningPass", "Animation uploads", "Dynamic BLAS and raster geometry", "Current-pose output is consumed by graphics-queue AS builds before any ray query and by raster passes later in the frame."),
        Graphics("DdgiFoliageProxyGenerationPass", "Stable foliage patch records and wind clock", "Procedural foliage BLAS", "Compute output is consumed immediately by graphics-queue acceleration-structure build commands with an explicit compute-to-AS barrier."),
        Graphics("AccelerationStructureBlasPass", "Static, current-pose, and foliage geometry", "TLAS publication", "Vulkan AS build capability and frame-slot ownership remain on the graphics queue."),
        Graphics("OpacityMicromapBuildPass", "Validated cooked OMM streams and ordinary candidate BLAS", "OMM-attached BLAS publication and TLAS", "Micromap build, compaction, attached BLAS construction, and fence-gated publication share the graphics AS submission domain."),
        Graphics("AccelerationStructureTlasPass", "Admitted complete BLAS objects and instance metadata", "Simple DDGI ray queries", "TLAS and metadata publication is the graphics-queue transaction boundary before graph consumers.")
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

    /// <summary>
    /// Correctness promotion is an explicit, evidence-backed source decision. Profitability is a
    /// separate runtime/evidence gate; a true value here does not bypass the Auto timing policy.
    /// </summary>
    public static bool IsCorrectnessCertified(AsyncComputePath path) =>
        AsyncComputeCertificationEvidence.Get(path).CorrectnessCertified;

    /// <summary>
    /// Source-owned production authorization is distinct from optional capture
    /// evidence. The two renderer-wide preferred paths may enter Auto whenever
    /// their concrete queue/resource plan validates; all other candidates keep
    /// the existing evidence gate.
    /// </summary>
    public static bool IsProductionActivationAuthorized(
        AsyncComputePath path) =>
        path is AsyncComputePath.SimpleDdgiUpdate or
            AsyncComputePath.FarFieldClipmapBake ||
        IsCorrectnessCertified(path);

    public static string GetCertificationEvidenceRevision(AsyncComputePath path) =>
        AsyncComputeCertificationEvidence.Get(path).EvidenceRevision;

    public static AsyncComputePathCertificationEvidence GetCertificationEvidence(AsyncComputePath path) =>
        AsyncComputeCertificationEvidence.Get(path);

    internal static bool TryGetPath(string passName, out AsyncComputePath path)
    {
        switch (passName)
        {
            case "SimpleDdgiSchedulePass":
            case "SimpleDdgiTracePass":
            case "SimpleDdgiRelocateClassifyPass":
            case "SimpleDdgiDirectionalRadiancePass":
            case "SimpleDdgiAcceleratedSolvePass":
            case "SimpleDdgiTransportPass":
            case "SimpleDdgiBlendPass":
            case "SimpleDdgiPublishPass":
            case "SimpleDdgiTransportAuditPass":
            case "SimpleDdgiSchedulerCommitPass":
                path = AsyncComputePath.SimpleDdgiUpdate;
                return true;
            case "FarFieldClipmapBakePass":
                path = AsyncComputePath.FarFieldClipmapBake;
                return true;
            case "AmbientOcclusionBlurPass":
                path = AsyncComputePath.AmbientOcclusionBlur;
                return true;
            case "HiZBuildPass":
                path = AsyncComputePath.HiZBuild;
                return true;
            case "FogPass":
                path = AsyncComputePath.Fog;
                return true;
            case "BloomPass":
                path = AsyncComputePath.Bloom;
                return true;
            case "GpuParticleResetPass":
            case "GpuParticleSimulatePass":
            case "GpuParticleSortPass":
                path = AsyncComputePath.GpuParticles;
                return true;
            default:
                path = default;
                return false;
        }
    }

    internal static string GetRepresentativePass(AsyncComputePath path) =>
        path switch
        {
            AsyncComputePath.SimpleDdgiUpdate => "SimpleDdgiSchedulePass",
            AsyncComputePath.FarFieldClipmapBake => "FarFieldClipmapBakePass",
            AsyncComputePath.AmbientOcclusionBlur => "AmbientOcclusionBlurPass",
            AsyncComputePath.HiZBuild => "HiZBuildPass",
            AsyncComputePath.Fog => "FogPass",
            AsyncComputePath.Bloom => "BloomPass",
            AsyncComputePath.GpuParticles => "GpuParticleSimulatePass",
            _ => string.Empty
        };

    private static AsyncComputePassAuditEntry Candidate(string passName, string producers, string consumers, string rationale) =>
        new(passName, AsyncComputePassClassification.ProductionAsyncCandidate, producers, consumers, rationale);

    private static AsyncComputePassAuditEntry Graphics(string passName, string producers, string consumers, string rationale) =>
        new(passName, AsyncComputePassClassification.GraphicsQueueComputeByDesign, producers, consumers, rationale);
}
