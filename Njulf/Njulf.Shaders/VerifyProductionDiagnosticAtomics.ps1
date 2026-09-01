[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ShaderDirectory
)

$resolvedDirectory = (Resolve-Path -LiteralPath $ShaderDirectory).Path
$allShaderFiles = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward*.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_forward_ddgi_b1.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_forward_ddgi_b1_provenance.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'ddgi_simple_*.comp.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'ddgi_masked_feedback_compact.comp.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'fog.comp.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'fog_b1.comp.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'particle.vert.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'particle_b1.vert.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_grass.mesh.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_grass_b1.mesh.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_mesh.mesh.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_mesh_b1.mesh.spv'
) | Sort-Object FullName -Unique

if ($allShaderFiles.Count -eq 0) {
    throw "No production forward or Simple-DDGI SPIR-V modules were found in '$resolvedDirectory'."
}

# Scheduler stages use bounded atomics for deterministic compaction, admission,
# and lifecycle accounting. They are algorithmic synchronization, not renderer
# diagnostic instrumentation. Sparse residency adds bounded demand handshakes,
# lifecycle counters, and fixed-summary reductions to the remaining producers
# and consumers. Pin every optimized OpAtomicIAdd count instead of broadly
# exempting those modules. This keeps the no-unreviewed-atomic gate strict and
# makes any new production atomic an intentional ABI review.
$schedulerModuleNames = @(
    'ddgi_simple_schedule_admit.comp.spv',
    'ddgi_simple_schedule_admit_tail.comp.spv',
    'ddgi_simple_schedule_classify.comp.spv',
    'ddgi_simple_schedule_commit_local.comp.spv',
    'ddgi_simple_schedule_commit_propagation.comp.spv',
    'ddgi_simple_schedule_compact.comp.spv',
    'ddgi_simple_schedule_emit.comp.spv',
    'ddgi_simple_schedule_emit_classify.comp.spv',
    'ddgi_simple_schedule_emit_scatter.comp.spv',
    'ddgi_simple_schedule_feedback.comp.spv',
    'ddgi_simple_schedule_feedback_partial.comp.spv',
    'ddgi_simple_schedule_lane_base.comp.spv',
    'ddgi_simple_schedule_materialize.comp.spv',
    'ddgi_simple_schedule_prefix.comp.spv',
    'ddgi_simple_schedule_reset.comp.spv'
)
$availableNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@($allShaderFiles | ForEach-Object Name),
    [System.StringComparer]::Ordinal)
$missingSchedulerModules = @($schedulerModuleNames | Where-Object {
    -not $availableNames.Contains($_)
})
if ($missingSchedulerModules.Count -ne 0) {
    throw "Expected production Simple-DDGI scheduler module(s) missing from '$resolvedDirectory': $($missingSchedulerModules -join ', ')."
}

$shaderFiles = @($allShaderFiles | Where-Object {
    $_.Name -notin $schedulerModuleNames
})

$algorithmicAtomicCounts = @{
    # Lock-free, bounded receiver demand, exact gather attribution, and one
    # accumulated B1 interpolation-mass add per optimized gather site.
    # Transparent scene reflections retain four sparse source-estimate atomics
    # and add exact, frame-global SSR admission/reservation/sample/hit budget
    # accounting. Opaque siblings compile the feature out and retain their
    # established counts below.
    # Physical meshlet-page consumers fail closed on a stale/invalid virtual
    # mapping and attribute that event through one bounded streaming-feedback
    # add. Transparent ray-query programs preserve seven outlined copies of
    # that guarded lookup; non-ray programs retain one optimized copy.
    'forward.frag.spv' = 27
    'forward_opaque_ddgi.frag.spv' = 14
    'forward_opaque_ddgi_provenance.frag.spv' = 14
    'forward_opaque_simple_ddgi.frag.spv' = 14
    'forward_opaque_simple_ddgi_provenance.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_provenance.frag.spv' = 14
    # C5's qualified opaque direct-source variants only add the separately
    # validated direct-diffuse/emissive color attachment. They retain the
    # exact same bounded receiver-gather and interpolation-mass atomics as
    # their corresponding opaque forward programs.
    'forward_opaque_ddgi_near_field_direct_source.frag.spv' = 14
    'forward_opaque_simple_ddgi_near_field_direct_source.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_near_field_direct_source.frag.spv' = 14
    # C4 receiver identity is payload-only. Its standalone and C4+C5 combined
    # variants retain exactly the canonical receiver-gather atomics.
    'forward_opaque_ddgi_c4_receiver.frag.spv' = 14
    'forward_opaque_simple_ddgi_c4_receiver.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c4_receiver.frag.spv' = 14
    'forward_opaque_ddgi_c4_c5.frag.spv' = 14
    'forward_opaque_simple_ddgi_c4_c5.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c4_c5.frag.spv' = 14
    # Hybrid-reflection receiver variants only append the deferred specular
    # payload MRT. Their DDGI receiver synchronization remains identical to
    # the matching canonical/C4/C5 variants.
    'forward_opaque_ddgi_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_c4_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_c4_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c4_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_c5_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_c5_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c5_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_c4_c5_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_c4_c5_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c4_c5_hybrid_reflection.frag.spv' = 14
    'forward_weighted_oit.frag.spv' = 27
    # Surface-aware cache programs retain the canonical exact gather behind a
    # fail-closed rejection branch. Seven bounded B1 ownership operations own
    # dense/rejected samples; the compact path adds exactly two list atomics
    # (measured high-water and overflow fallback). Record publication uses a
    # bounded maximum operation and is audited separately by SPIR-V validation.
    'forward_opaque_ddgi_cache_required.frag.spv' = 23
    'forward_opaque_simple_ddgi_cache_required.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_cache_required.frag.spv' = 23
    'forward_opaque_ddgi_near_field_direct_source_cache_required.frag.spv' = 23
    'forward_opaque_simple_ddgi_near_field_direct_source_cache_required.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_near_field_direct_source_cache_required.frag.spv' = 23
    # Hybrid DdgiHigh is split into complementary native programs. Accepted
    # modules contain only admission/cache shading and therefore no functional
    # receiver atomics. Exact-fallback modules preserve the canonical 14, while
    # combined rollback modules preserve the prior 23-operation graph.
    'forward_opaque_ddgi_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_ddgi_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_full_input_ddgi_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_ddgi_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_ddgi_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_ddgi_c4_receiver_cache_required.frag.spv' = 23
    'forward_opaque_simple_ddgi_c4_receiver_cache_required.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_c4_receiver_cache_required.frag.spv' = 23
    'forward_opaque_ddgi_c4_c5_cache_required.frag.spv' = 23
    'forward_opaque_simple_ddgi_c4_c5_cache_required.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_c4_c5_cache_required.frag.spv' = 23
    'forward_opaque_ddgi_c4_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_ddgi_c4_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_full_input_ddgi_c4_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_ddgi_c4_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_c4_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c4_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_c4_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_ddgi_c4_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_c4_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_ddgi_c5_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_ddgi_c5_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_full_input_ddgi_c5_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_ddgi_c5_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_c5_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c5_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_c5_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_ddgi_c5_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_c5_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_ddgi_c4_c5_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_ddgi_c4_c5_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_simple_full_input_ddgi_c4_c5_cache_required_hybrid_reflection.frag.spv' = 0
    'forward_opaque_ddgi_c4_c5_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_ddgi_c4_c5_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_simple_full_input_ddgi_c4_c5_cache_exact_fallback_hybrid_reflection.frag.spv' = 14
    'forward_opaque_ddgi_c4_c5_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_ddgi_c4_c5_cache_combined_hybrid_reflection.frag.spv' = 23
    'forward_opaque_simple_full_input_ddgi_c4_c5_cache_combined_hybrid_reflection.frag.spv' = 23
    # Legacy cache variants do not compile the canonical rejection gather, but
    # still carry the same seven exact B1 ownership operations.
    'forward_opaque_ddgi_cache_legacy.frag.spv' = 7
    'forward_opaque_simple_ddgi_cache_legacy.frag.spv' = 7
    'forward_opaque_simple_full_input_ddgi_cache_legacy.frag.spv' = 7
    # The transparent compatibility artifact retains its 12 reflection-source
    # operations plus the same 14 exact rejection-path receiver operations.
    'forward_transparent_ddgi_cache_required.frag.spv' = 27
    # The directional-only ThinGlass program touches only the four continuous
    # tetrahedral owners. Its bounded atomics are the sparse receiver-demand
    # and contribution handshake; diffuse visibility/recovery sites are absent.
    'forward_transparent_thin_glass.frag.spv' = 17
    # Normal ray-query transparent variants contain both bounded optical-task
    # admission and the DDGI gather used to shade a committed reflection hit.
    # Their production compile deliberately preserves function boundaries to
    # avoid glslang's exhaustive-inlining ID overflow, so shared atomic sites
    # appear once in the module rather than once per inlined call path.
    'forward_transparent_ray.frag.spv' = 17
    'forward_weighted_oit_ray.frag.spv' = 17
    # Partitioned transparent programs preserve the same functional sparse
    # DDGI/reflection accounting as their universal siblings.  The ray-query
    # programs keep only the outlined optical-task/hit-gather operations.
    'forward_transparent_ordinary.frag.spv' = 27
    'forward_transparent_thick.frag.spv' = 27
    'forward_transparent_decal_cache_required.frag.spv' = 27
    'forward_weighted_oit_ordinary.frag.spv' = 27
    'forward_weighted_oit_thick.frag.spv' = 27
    'forward_weighted_oit_decal.frag.spv' = 27
    'forward_weighted_oit_decal_cache_required.frag.spv' = 27
    'forward_transparent_ordinary_ray.frag.spv' = 17
    'forward_transparent_thick_ray.frag.spv' = 17
    'forward_transparent_decal_ray.frag.spv' = 17
    'forward_weighted_oit_ordinary_ray.frag.spv' = 17
    'forward_weighted_oit_thick_ray.frag.spv' = 17
    'forward_weighted_oit_decal_ray.frag.spv' = 17
    'fog.comp.spv' = 14
    'particle.vert.spv' = 14
    'foliage_grass.mesh.spv' = 14
    # Authored foliage additionally performs the bounded physical-residency
    # range-demand transaction. Receiver-attribution programs intentionally
    # preserve function boundaries (-Od), so their shared validation sites
    # appear once in static SPIR-V instead of once per inlined call path.
    # These remain functional streaming atomics, not optional diagnostics.
    'foliage_mesh.mesh.spv' = 32
    'foliage_mesh_b1.mesh.spv' = 6
    # Exact B1 programs are compiled with preserved function boundaries to
    # avoid pathological native-driver compilation. The counts below pin the
    # outlined reservation, publication, overflow, and receiver operations;
    # runtime call multiplicity is intentionally not represented by duplicate
    # static instructions.
    'forward_opaque_ddgi_b1.frag.spv' = 9
    'forward_opaque_ddgi_b1_provenance.frag.spv' = 9
    'forward_opaque_simple_ddgi_b1.frag.spv' = 9
    'forward_opaque_simple_ddgi_b1_provenance.frag.spv' = 9
    'forward_opaque_simple_full_input_ddgi_b1.frag.spv' = 9
    'forward_opaque_simple_full_input_ddgi_b1_provenance.frag.spv' = 9
    'forward_transparent_ddgi_b1.frag.spv' = 16
    # ThinGlass omits one ordinary transparent reflection-owner site.
    'forward_transparent_thin_glass_ddgi_b1.frag.spv' = 15
    'forward_weighted_oit_ddgi_b1.frag.spv' = 16
    # glslc outlines the shared receiver/hit-gather machinery in the combined
    # ray+B1 programs, so their static SPIR-V instruction count is lower than
    # the non-ray B1 siblings while preserving the bounded runtime operations.
    'forward_transparent_ray_ddgi_b1.frag.spv' = 20
    'forward_weighted_oit_ray_ddgi_b1.frag.spv' = 20
    'foliage_forward_ddgi_b1.frag.spv' = 9
    'foliage_forward_ddgi_b1_provenance.frag.spv' = 9
    'fog_b1.comp.spv' = 9
    'particle_b1.vert.spv' = 9
    # The frame-local opaque cache executes the same three exact gather sites
    # while residency demand stays disabled.
    'ddgi_simple_receiver_cache.comp.spv' = 3
    # Adaptive generation retains the same three gather sites. The B1 variants
    # share two outlined attribution operations with those gathers. Its
    # classifier owns both specialization branches: the compact rollback
    # reservation and the row-major selected-count publication.
    'ddgi_simple_receiver_cache_adaptive.comp.spv' = 3
    'ddgi_simple_receiver_cache_adaptive_b1.comp.spv' = 5
    'ddgi_simple_receiver_cache_adaptive_b1_missing.comp.spv' = 5
    # Three subgroup-aggregated publication counters are part of the default
    # generation-reuse specialization (hit, dirty, and skipped-tile).
    'ddgi_simple_receiver_cache_classify.comp.spv' = 14
    # The frozen depth-only benchmark uses the identical gather producer but
    # deliberately omits the surface sidecar. Its three functional gather
    # atomics must remain equivalent to the pre-surface-cache implementation.
    'ddgi_simple_receiver_cache_legacy.comp.spv' = 3
    'ddgi_simple_receiver_cache_b1.comp.spv' = 5
    # The post-forward masked list performs the same outlined B1 reservation
    # and publication operations after its exact surface gather.
    'ddgi_masked_feedback_compact.comp.spv' = 5

    # Qualification counters exist only in explicitly selected diagnostic
    # artifacts. Pin their complete static add count here so no diagnostic
    # operation can leak into the zero-add production/cache-debug siblings.
    'ddgi_simple_receiver_cache_resolve_diagnostics.comp.spv' = 6
    'forward_opaque_ddgi_cache_required_diagnostics.frag.spv' = 28
    'forward_opaque_simple_ddgi_cache_required_diagnostics.frag.spv' = 28
    'forward_opaque_simple_full_input_ddgi_cache_required_diagnostics.frag.spv' = 28

    # Sparse page classification, reconciliation, fixed feedback reduction,
    # and generation-safe update lifecycle attribution.
    # The ninth add is the scheduler outcome failure latch used when any lane
    # observes malformed direction-free ray scratch. It prevents the private
    # blend target from reaching CommitLocal; the CPU scheduler takes the
    # equivalent fail-closed probe-state path without this global atomic.
    'ddgi_simple_blend.comp.spv' = 9
    'ddgi_simple_blend_guided.comp.spv' = 9
    # Directional prepare/stage/project each inline the sparse live-address and
    # generation validation contract: stale-resource, two distinct out-of-range
    # branches, stale-mapping, and stale-virtual attribution. Publication has
    # the same five sites plus five inlined CPU-scheduler failure revalidation
    # paths. These bounded integrity counters are functional transaction
    # evidence, not optional renderer diagnostics. The two storage-specialized
    # stage modules deliberately retain the same five sites. Project no longer
    # owns a per-ray completion add after the native-safe one-lane-per-probe
    # split.
    'ddgi_simple_directional_prepare.comp.spv' = 5
    'ddgi_simple_directional_stage_guided_legacy.comp.spv' = 5
    'ddgi_simple_directional_stage_guided_packed.comp.spv' = 5
    'ddgi_simple_directional_project.comp.spv' = 5
    'ddgi_simple_directional_project_guided.comp.spv' = 5
    'ddgi_simple_directional_publish.comp.spv' = 10
    # Confirmed-empty pages now reopen only through geometry-generation or
    # explicit-pin invalidation, so the obsolete timed-retry counter atomic is
    # intentionally absent.
    'ddgi_simple_page_classify.comp.spv' = 7
    # Workgroup-parallel virtual/reverse-map summary reductions use 23
    # additional functional shared-memory atomics. The two additional
    # reductions separately classify visible-demand pages as intentionally
    # suppressed or initializing/unpublished for the liveness watchdog.
    # Pin the exact count so a future diagnostic call site or accidental
    # serialization cannot hide.
    'ddgi_simple_page_feedback.comp.spv' = 46
    'ddgi_simple_page_reconcile.comp.spv' = 4
    'ddgi_simple_publish.comp.spv' = 5
    'ddgi_simple_publish_sampled.comp.spv' = 5
    'ddgi_simple_relocate_classify.comp.spv' = 5
    'ddgi_simple_relocate_classify_guided.comp.spv' = 5
    'ddgi_simple_trace.comp.spv' = 7
    'ddgi_simple_trace_legacy_source.comp.spv' = 5
    'ddgi_simple_trace_legacy_reuse.comp.spv' = 6
    'ddgi_simple_trace_legacy_final.comp.spv' = 6
    'ddgi_simple_trace_validate_source.comp.spv' = 5
    'ddgi_simple_trace_validate_reuse.comp.spv' = 6
    'ddgi_simple_trace_validate_final.comp.spv' = 6
    'ddgi_simple_trace_packed_source.comp.spv' = 5
    'ddgi_simple_trace_packed_reuse.comp.spv' = 6
    'ddgi_simple_trace_packed_final.comp.spv' = 6
    # Guided trace variants change only direction/PDF generation. Sparse
    # lifecycle accounting remains byte-for-byte equivalent to the matching
    # uniform source/reuse/final role.
    'ddgi_simple_trace_legacy_guided_source.comp.spv' = 5
    'ddgi_simple_trace_legacy_guided_reuse.comp.spv' = 6
    'ddgi_simple_trace_legacy_guided_final.comp.spv' = 6
    'ddgi_simple_trace_validate_guided_source.comp.spv' = 5
    'ddgi_simple_trace_validate_guided_reuse.comp.spv' = 6
    'ddgi_simple_trace_validate_guided_final.comp.spv' = 6
    'ddgi_simple_trace_packed_guided_source.comp.spv' = 5
    'ddgi_simple_trace_packed_guided_reuse.comp.spv' = 6
    'ddgi_simple_trace_packed_guided_final.comp.spv' = 6
    # The packed fast-path programs specialize material/light/far-field
    # branches, but intentionally retain the same sparse-residency lifecycle
    # accounting as their canonical source/final roles. Pin every artifact so
    # adding a specialization cannot silently bypass this production gate.
    'ddgi_simple_trace_packed_general_complete_source.comp.spv' = 5
    'ddgi_simple_trace_packed_general_complete_final.comp.spv' = 6
    'ddgi_simple_trace_packed_general_split_source.comp.spv' = 5
    'ddgi_simple_trace_packed_general_split_final.comp.spv' = 6
    'ddgi_simple_trace_packed_opaque_complete_source.comp.spv' = 5
    'ddgi_simple_trace_packed_opaque_complete_final.comp.spv' = 6
    'ddgi_simple_trace_packed_opaque_split_source.comp.spv' = 5
    'ddgi_simple_trace_packed_opaque_split_final.comp.spv' = 6
    'ddgi_simple_trace_packed_opaque_sun_complete_source.comp.spv' = 5
    'ddgi_simple_trace_packed_opaque_sun_complete_final.comp.spv' = 6
    'ddgi_simple_trace_packed_opaque_sun_split_source.comp.spv' = 5
    'ddgi_simple_trace_packed_opaque_sun_split_final.comp.spv' = 6
    'ddgi_simple_transport.comp.spv' = 7
    'ddgi_simple_transport_legacy.comp.spv' = 7
    'ddgi_simple_transport_validate.comp.spv' = 7
    'ddgi_simple_transport_packed.comp.spv' = 7
    'ddgi_simple_transport_solve_legacy.comp.spv' = 7
    'ddgi_simple_transport_solve_validate.comp.spv' = 7
    'ddgi_simple_transport_solve_packed.comp.spv' = 7
    # Guided transport consumes the generation-time PDF but does not add a
    # global additive reduction beyond the canonical transport transaction.
    'ddgi_simple_transport_guided_legacy.comp.spv' = 7
    'ddgi_simple_transport_guided_validate.comp.spv' = 7
    'ddgi_simple_transport_guided_packed.comp.spv' = 7
    'ddgi_simple_transport_solve_guided_legacy.comp.spv' = 7
    'ddgi_simple_transport_solve_guided_validate.comp.spv' = 7
    'ddgi_simple_transport_solve_guided_packed.comp.spv' = 7
    # Transfer initialization and the one-invocation-per-ray operator phase use
    # no additive atomics (the ray phase has only status OR and contraction
    # max). All additive certificate/cache-rejection reductions are isolated in
    # the small second shader so native drivers never lower the recursive
    # operator, workgroup coordination, and certificate reduction together.
    # The reduce role derives the frozen expected participant and texel
    # populations on-GPU and performs one workgroup reduction for all scalar
    # and RGB certificate maxima. Guided and uniform projections deliberately
    # retain the same fixed global-add count.
    'ddgi_simple_transport_audit.comp.spv' = 28
    'ddgi_simple_transport_audit_reduce_legacy.comp.spv' = 28
    'ddgi_simple_transport_audit_reduce_validate.comp.spv' = 28
    'ddgi_simple_transport_audit_reduce_packed.comp.spv' = 28
    'ddgi_simple_transport_audit_reduce_legacy_guided.comp.spv' = 28
    'ddgi_simple_transport_audit_reduce_validate_guided.comp.spv' = 28
    'ddgi_simple_transport_audit_reduce_packed_guided.comp.spv' = 28
    'ddgi_simple_transport_intermediate_publish.comp.spv' = 5
}
# Sparse-lobe forward programs change only the exact uvec2 transport. They
# retain the corresponding baseline program's functional DDGI atomic graph.
foreach ($baselineName in @($algorithmicAtomicCounts.Keys | Where-Object {
    $_ -like '*hybrid_reflection.frag.spv'
})) {
    $sparseName = $baselineName -replace
        '\.frag\.spv$', '_sparse_lobe.frag.spv'
    $algorithmicAtomicCounts[$sparseName] =
        $algorithmicAtomicCounts[$baselineName]
}
$missingAlgorithmicModules = @($algorithmicAtomicCounts.Keys | Where-Object {
    -not $availableNames.Contains($_)
})
if ($missingAlgorithmicModules.Count -ne 0) {
    throw "Expected production Simple-DDGI algorithmic-atomic module(s) missing from '$resolvedDirectory': $($missingAlgorithmicModules -join ', ')."
}

$opAtomicIAdd = 234
$spirvInspectorTypeName =
    'Njulf.ShaderBuildValidation.SpirvInstructionInspector'
if ($null -eq ($spirvInspectorTypeName -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;

namespace Njulf.ShaderBuildValidation
{
    public static class SpirvInstructionInspector
    {
        private const uint SpirvMagic = 0x07230203u;

        public static int CountOpcode(string path, int expectedOpcode)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20 || (bytes.Length % sizeof(uint)) != 0)
                throw new InvalidDataException("'" + path + "' is not a complete SPIR-V word stream.");
            if (BitConverter.ToUInt32(bytes, 0) != SpirvMagic)
                throw new InvalidDataException("'" + path + "' does not have the SPIR-V magic word.");

            int opcodeCount = 0;
            for (int byteOffset = 20; byteOffset < bytes.Length;)
            {
                uint instruction = BitConverter.ToUInt32(bytes, byteOffset);
                int wordCount = (int)(instruction >> 16);
                int opcode = (int)(instruction & 0xffffu);
                long nextByteOffset = byteOffset + (long)wordCount * sizeof(uint);
                if (wordCount <= 0 || nextByteOffset > bytes.Length)
                {
                    throw new InvalidDataException(
                        "'" + path + "' contains a malformed SPIR-V instruction at byte " + byteOffset + ".");
                }

                if (opcode == expectedOpcode)
                    opcodeCount++;
                byteOffset = (int)nextByteOffset;
            }

            return opcodeCount;
        }
    }
}
'@ -ErrorAction Stop
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($shader in $shaderFiles) {
    $atomicAdds =
        [Njulf.ShaderBuildValidation.SpirvInstructionInspector]::CountOpcode(
            $shader.FullName,
            $opAtomicIAdd)

    $expectedAtomicAdds = if ($algorithmicAtomicCounts.ContainsKey($shader.Name)) {
        [int]$algorithmicAtomicCounts[$shader.Name]
    }
    else {
        0
    }
    if ($atomicAdds -ne $expectedAtomicAdds) {
        $violations.Add("$($shader.Name): found $atomicAdds OpAtomicIAdd instruction(s), expected $expectedAtomicAdds")
    }
}

if ($violations.Count -ne 0) {
    throw "Production DDGI diagnostic atomic verification failed: $($violations -join '; ')."
}

Write-Host "Verified $($shaderFiles.Count) production forward/non-scheduler Simple-DDGI modules contain no unexpected OpAtomicIAdd diagnostics; $($algorithmicAtomicCounts.Count) receiver/update modules have exact pinned functional counts and $($schedulerModuleNames.Count) bounded scheduler modules are intentionally excluded."
