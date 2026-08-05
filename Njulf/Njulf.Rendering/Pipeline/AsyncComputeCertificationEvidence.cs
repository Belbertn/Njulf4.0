using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Checked-in evidence contract for one atomic async-compute path. The record is intentionally
/// descriptive: a correctness certificate is not inferred from a unit test or from a successful
/// plan compilation. The source/declaration/shader revision must be advanced whenever any of the
/// listed contracts changes.
/// </summary>
public sealed record AsyncComputePathCertificationEvidence(
    AsyncComputePath Path,
    string EvidenceRevision,
    string DeclarationShaderEvidenceRevision,
    IReadOnlyList<string> AtomicPasses,
    IReadOnlyList<string> ConcreteResourceRanges,
    string ProducerAndFirstConsumer,
    string InitialFinalLayoutAndOwnerContract,
    string QueueTopologyScope,
    IReadOnlyList<string> LifecycleReportIds,
    string ValidationResult,
    string LinearHdrOrBufferEquivalenceResult,
    bool CorrectnessCertified,
    string ProfitabilityResult,
    string Notes);

/// <summary>
/// The durable, reviewable certification table consumed by <see cref="AsyncComputePassCatalog"/>.
/// Reports named here are generated artifacts; the evidence revision and report identities are
/// checked in so a later source or shader change cannot be mistaken for the captured result.
/// </summary>
public static class AsyncComputeCertificationEvidence
{
    private static readonly AsyncComputePathCertificationEvidence[] Entries =
    [
        new(
            AsyncComputePath.HiZBuild,
            "async-cert-hiz-20260804-r1",
            "ProductionRenderPipelineDeclaration.v8; HiZBuildPass; hiz_downsample.comp; common.glsl",
            ["HiZBuildPass"],
            [
                "SceneDepth image: depth aspect, mip 0, layer 0; compute sampled depth read",
                "HiZPyramid image chain: all generated mips, layer 0; compute storage write/read"
            ],
            "DepthPrePass -> HiZBuildPass -> ForwardVisibilityCompactionPass; previous-frame HiZ is consumed by SceneOpaqueCompactionPass.",
            "SceneDepth enters DepthStencilReadOnlyOptimal owned by graphics; HiZPyramid enters General and is written by compute, then ends ShaderReadOnlyOptimal owned by compute; graph release/acquire transfers the complete chain to graphics.",
            "Hardware scope: NVIDIA GeForce RTX 3060 Laptop, graphics family 0, dedicated compute family 2; separate queue submissions with timeline semaphore edges. Queue-stage capability validation remains required for other topologies.",
            ["async-cert-hiz-final.json"],
            "PASS: 60-frame forced atomic run; validation warnings=0, errors=0; planned/emitted release=4/4 and acquire=4/4; ownership transfers=4; stale-plan rejections=0; validation fallbacks=0.",
            "PASS: async-bench-vfx-hiz-graphics.json vs async-bench-vfx-hiz-async.json; linear HDR relative RMSE=0, MAE=0, maximum absolute error=0. Public image output is equivalent; raw Hi-Z byte capture is not part of the benchmark artifact.",
            true,
            "NOT PROMOTED: the available Debug/Standard pair regressed GPU median 1.760->1.803 ms and P95 1.908->2.045 ms. CPU P95 was 5.276->4.281 ms, but three ShippingPerformance pairs and all other promotion gates are still required.",
            "Correctness is certified for the recorded queue topology only. Auto retains the graphics fallback until a separate profitability evidence bundle passes."),

        new(
            AsyncComputePath.AmbientOcclusionBlur,
            "async-cert-ao-20260804-r1",
            "ProductionRenderPipelineDeclaration.v8; AmbientOcclusionBlurPass; ambient_occlusion_blur.comp; common.glsl",
            ["AmbientOcclusionBlurPass"],
            [
                "AmbientOcclusionRaw image: mip 0, layer 0; compute sampled read",
                "SceneDepth image: depth aspect, mip 0, layer 0; compute sampled depth read",
                "AmbientOcclusionScratch image: mip 0, layer 0; compute read/write storage",
                "AmbientOcclusionBlurred image: mip 0, layer 0; compute storage write"
            ],
            "AmbientOcclusionPass -> AmbientOcclusionBlurPass -> ForwardPlusPass (first blurred-AO consumer).",
            "Raw AO and depth enter ShaderReadOnlyOptimal/DepthStencilReadOnlyOptimal from graphics; scratch enters General and ends ShaderReadOnlyOptimal on compute; blurred AO enters General and ends ShaderReadOnlyOptimal on compute; graph transfers the complete image set back to graphics fragment sampling.",
            "Hardware scope: NVIDIA GeForce RTX 3060 Laptop, graphics family 0, dedicated compute family 2; separate queue submissions with timeline semaphore edges. Queue-stage capability validation remains required for other topologies.",
            ["async-cert-ao-final.json"],
            "PASS: 60-frame forced atomic run; validation warnings=0, errors=0; planned/emitted release=8/8 and acquire=8/8; ownership transfers=8; stale-plan rejections=0; validation fallbacks=0.",
            "PASS: async-bench-vfx-ao-graphics.json vs async-bench-vfx-ao-async.json; linear HDR relative RMSE=0, MAE=0, maximum absolute error=0.",
            true,
            "NOT PROMOTED: the available Debug/Standard pair lowered GPU median 1.881->1.801 ms but regressed GPU P95 1.899->2.064 ms and CPU P95 4.416->4.603 ms; it did not provide the required three locked ShippingPerformance pairs.",
            "Correctness is certified for the recorded queue topology only. Auto retains the graphics fallback until a separate profitability evidence bundle passes."),

        new(
            AsyncComputePath.Bloom,
            "async-cert-bloom-20260804-r1",
            "ProductionRenderPipelineDeclaration.v8; BloomPass; bloom_extract.comp; bloom_downsample.comp; bloom_upsample.comp; common.glsl",
            ["BloomPass"],
            [
                "SceneColor image: mip 0, layer 0; compute sampled read",
                "FogOutput image: mip 0, layer 0; compute sampled read",
                "BloomChain image chain: all allocated downsample/upsample mips, layer 0; compute read/write storage"
            ],
            "SceneColor/FogOutput producers -> BloomPass -> ToneMapCompositePass (first BloomChain consumer).",
            "SceneColor and FogOutput enter ShaderReadOnlyOptimal owned by graphics; BloomChain enters General and each written mip ends ShaderReadOnlyOptimal owned by compute; graph transfers the chain to graphics fragment sampling.",
            "Hardware scope: NVIDIA GeForce RTX 3060 Laptop, graphics family 0, dedicated compute family 2; separate queue submissions with timeline semaphore edges. Queue-stage capability validation remains required for other topologies.",
            ["async-cert-bloom-final.json"],
            "PASS: 60-frame forced atomic run; validation warnings=0, errors=0; planned/emitted release=16/16 and acquire=16/16; ownership transfers=16; stale-plan rejections=0; validation fallbacks=0.",
            "PASS: async-bench-vfx-bloom-graphics.json vs async-bench-vfx-bloom-async.json; linear HDR relative RMSE=0, MAE=0, maximum absolute error=0.",
            true,
            "NOT PROMOTED: the available Debug/Standard pair lowered GPU median 1.882->1.783 ms and P95 1.901->1.792 ms, but raised CPU P95 3.781->4.300 ms and did not provide the required three locked ShippingPerformance pairs.",
            "Correctness is certified for the recorded queue topology only. Auto retains the graphics fallback until a separate profitability evidence bundle passes."),

        new(
            AsyncComputePath.Fog,
            "async-cert-fog-20260804-r1",
            "ProductionRenderPipelineDeclaration.v8; FogPass; fog.comp; common.glsl",
            ["FogPass"],
            [
                "SceneColor image: mip 0, layer 0; compute sampled read",
                "SceneDepth image: depth aspect, mip 0, layer 0; compute sampled depth read",
                "FogOutput image: mip 0, layer 0; compute storage write"
            ],
            "SceneColor/SceneDepth producers -> FogPass -> AutoExposurePass (first FogOutput consumer), then BloomPass/ToneMapCompositePass.",
            "SceneColor enters ShaderReadOnlyOptimal and SceneDepth enters DepthStencilReadOnlyOptimal owned by graphics; FogOutput enters General and ends ShaderReadOnlyOptimal owned by compute; graph transfers FogOutput to graphics/fragment consumers.",
            "Hardware scope: NVIDIA GeForce RTX 3060 Laptop, graphics family 0, dedicated compute family 2; separate queue submissions with timeline semaphore edges. Queue-stage capability validation remains required for other topologies.",
            ["async-cert-fog-final.json"],
            "PASS: 60-frame forced atomic run; validation warnings=0, errors=0; planned/emitted release=6/6 and acquire=6/6; ownership transfers=6; stale-plan rejections=0; validation fallbacks=0.",
            "PASS: async-bench-vfx-fog-graphics.json vs async-bench-vfx-fog-async.json with fixed 1/60 particle timestep; linear HDR relative RMSE=0, MAE=0, maximum absolute error=0.",
            true,
            "NOT PROMOTED: the available Debug/Standard pair regressed GPU median 1.654->1.789 ms and P95 1.920->2.044 ms; CPU P95 also regressed 3.761->4.463 ms. Three locked ShippingPerformance pairs are still required.",
            "The deterministic particle timestep is benchmark-only; interactive rendering remains wall-clock driven. Auto retains the graphics fallback until a separate profitability evidence bundle passes."),

        new(
            AsyncComputePath.GpuParticles,
            "async-cert-gpu-particles-20260804-r1",
            "ProductionRenderPipelineDeclaration.v8; GpuParticleResetPass; GpuParticleSimulatePass; GpuParticleSortPass; particle_reset.comp; particle_simulate.comp; particle_sort.comp",
            ["GpuParticleResetPass", "GpuParticleSimulatePass", "GpuParticleSortPass"],
            [
                "ParticleBuffers: current frame buffer-set slot; compute storage read",
                "GpuParticleState, GpuParticleIndices, GpuParticleEmitterData, GpuParticleCounters: current frame buffer-set slots; compute read/write",
                "GpuParticleUnsortedOutput, GpuParticleRenderOutput, GpuParticleIndirectArguments, GpuParticleSortKeys: current frame buffer-set slots; compute read/write",
                "GpuParticleCounterReadback: current frame buffer-set slot; compute transfer/readback write"
            ],
            "Per-frame particle uploads/Reset -> GpuParticleSimulatePass -> GpuParticleSortPass -> ParticlePass graphics storage/indirect draw.",
            "Buffers have no image layout; compute owns the state and output buffers during reset/simulate/sort. Final ownership is transferred to graphics with ShaderStorageRead for render instances and IndirectCommandRead for draw arguments; compute-only barriers do not advertise vertex/draw-indirect stages.",
            "Hardware scope: NVIDIA GeForce RTX 3060 Laptop, graphics family 0, dedicated compute family 2; separate queue submissions with timeline semaphore edges. Queue-stage capability validation remains required for other topologies.",
            ["async-cert-gpu-particles-final.json"],
            "PASS: 60-frame forced atomic run; validation warnings=0, errors=0; planned/emitted release=28/28 and acquire=28/28; ownership transfers=28; stale-plan rejections=0; validation fallbacks=0.",
            "PASS: async-bench-vfx-gpu-particles-low-graphics.json vs async-bench-vfx-gpu-particles-low-async.json; linear HDR oracle passed with relative RMSE=0.0284912, MAE/max within threshold; both runs reported valid particle counter readback and zero dropped spawns. Exact counter values are not byte-identical (GPU atomic scheduling), so this is an image/invariant equivalence result, not an exact buffer-byte certificate.",
            true,
            "NOT PROMOTED: GPU median regressed 2.462->2.546 ms in the low-profile Debug pair even though P95 improved 2.858->2.574 ms; CPU P95 regressed 4.826->6.855 ms. This is not a ShippingPerformance promotion and no three-pair evidence bundle exists.",
            "Correctness is certified for the recorded queue topology and the declared visual/invariant oracle. Exact buffer-byte equivalence remains an explicit follow-up before any stronger particle determinism claim."),

        new(
            AsyncComputePath.FarFieldClipmapBake,
            "async-cert-far-field-20260804-pending-r1",
            "ProductionRenderPipelineDeclaration.v8; FarFieldClipmapBakePass; farfield_voxelize.comp; farfield_jumpflood.comp; farfield_clipmap.glsl",
            ["FarFieldClipmapBakePass"],
            [
                "MeshGeometryBuffers and MaterialBuffers: complete imported buffer sets; compute storage read",
                "FarFieldParameters, FarFieldVoxels, FarFieldJumpFlood, FarFieldPageTable: complete buffer-set slots; compute read/write",
                "FarFieldInstances and RendererDiagnosticsBuffer: complete buffer-set slots; compute read/write as declared"
            ],
            "Scene geometry/material uploads -> FarFieldClipmapBakePass -> Simple-DDGI trace or next-frame far-field sampling.",
            "Buffer-only contract; no image layouts. Compute owns all declared far-field buffers for the bake segment and releases them to the first DDGI/graphics consumer using exact buffer ranges and queue-family edges.",
            "Attempted on NVIDIA GeForce RTX 3060 Laptop, graphics family 0, dedicated compute family 2; not certified because the run terminated on an unrelated VK-08600 mesh/skybox pipeline-layout mismatch before a clean async lifecycle could be established.",
            ["async-cert-FarFieldClipmapBake.json"],
            "BLOCKED: no correctness certificate; forced run exited on VK-08600 before a clean report. Do not promote or infer transfer correctness from the partial attempt.",
            "PENDING: no valid graphics/async linear-HDR or far-field buffer oracle exists until the pipeline-layout failure is fixed and the path completes a clean run.",
            false,
            "NOT EVALUATED: profitability evidence is prohibited until correctness is established.",
            "Keep graphics/CPU fallback. Re-run after the mesh/skybox descriptor-set layout mismatch is fixed."),

        new(
            AsyncComputePath.SimpleDdgiUpdate,
            "async-cert-simple-ddgi-20260804-pending-r2",
            "ProductionRenderPipelineDeclaration.v10; compact SimpleDdgiReceiverProbes publication; SimpleDdgiSchedulePass; SimpleDdgiPasses; SimpleDdgiAcceleratedSolvePass; SimpleDdgiSchedulerCommitPass; ddgi_simple_schedule_*.comp; ddgi_simple_trace.comp; ddgi_simple_relocate_classify.comp; ddgi_simple_transport.comp; ddgi_simple_blend.comp; ddgi_simple_transport_intermediate_publish.comp; ddgi_simple_publish.comp; ddgi_simple_transport_audit.comp",
            ["SimpleDdgiSchedulePass", "SimpleDdgiTracePass", "SimpleDdgiRelocateClassifyPass", "SimpleDdgiAcceleratedSolvePass", "SimpleDdgiTransportPass", "SimpleDdgiBlendPass", "SimpleDdgiPublishPass", "SimpleDdgiTransportAuditPass", "SimpleDdgiSchedulerCommitPass"],
            [
                "TLAS/RayQueryInstanceMetadata/MeshGeometryBuffers/MaterialBuffers/MaterialTextures/LightBuffers/EnvironmentData/EnvironmentMaps: complete ray-query input ranges",
                "FarFieldParameters/FarFieldVoxels/FarFieldInstances/FarFieldJumpFlood/FarFieldPageTable: complete far-field input ranges",
            "SimpleDdgiParameters/IrradianceAtlas/TransportSourceCache/VisibilityAtlas/RayScratch/ProbeState/ReceiverProbes/UpdateQueue/RelocationData/SchedulerArena: complete update and compact-publication buffer-set ranges"
            ],
            "Scene/lighting/AS inputs -> SimpleDdgiSchedulePass -> SimpleDdgiTracePass -> relocate -> cached transport/blend sweeps -> SimpleDdgiPublishPass -> frozen cached audit -> SimpleDdgiSchedulerCommitPass -> next-frame GI sampling.",
            "Buffer-only contract; no image layouts. Compute owns private update state until publication; canonical atlases and the distinct compact ReceiverProbes range transfer to the first graphics or next-frame GI consumer.",
            "No active certifying topology: the tested Sponza run was rejected because sampled-simple-DDGI atlas ownership remained graphics-visible, so the atomic path did not execute as an eligible isolated workload.",
            ["async-cert-simple-ddgi-debug.json"],
            "BLOCKED: no active forced path with a complete non-sampled-atlas resource contract; no Vulkan-clean lifecycle certificate is claimed.",
            "PENDING: no valid linear-HDR/buffer equivalence pair for an active Simple-DDGI async segment.",
            false,
            "NOT EVALUATED: profitability evidence is prohibited until correctness is established.",
            "Keep graphics/CPU fallback until the sampled-atlas ownership contract and an active isolated scenario are available.")
    ];

    public static IReadOnlyList<AsyncComputePathCertificationEvidence> All => Entries;

    public static AsyncComputePathCertificationEvidence Get(AsyncComputePath path)
    {
        foreach (AsyncComputePathCertificationEvidence entry in Entries)
        {
            if (entry.Path == path)
                return entry;
        }

        throw new ArgumentOutOfRangeException(nameof(path), path, "No async-compute certification record exists.");
    }
}
