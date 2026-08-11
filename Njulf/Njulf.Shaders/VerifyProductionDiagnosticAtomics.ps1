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
    'ddgi_simple_schedule_feedback.comp.spv',
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
    'forward.frag.spv' = 14
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
    'forward_weighted_oit.frag.spv' = 14
    'fog.comp.spv' = 14
    'particle.vert.spv' = 14
    'foliage_grass.mesh.spv' = 14
    'foliage_mesh.mesh.spv' = 14
    # Exact B1 production variants add only bounded reservation, publication,
    # and overflow accounting. Surface programs have seven additional adds;
    # fog/particle and the receiver cache have six because their producer
    # completion ownership is emitted by the enclosing pass.
    'forward_opaque_ddgi_b1.frag.spv' = 21
    'forward_opaque_ddgi_b1_provenance.frag.spv' = 21
    'forward_opaque_simple_ddgi_b1.frag.spv' = 21
    'forward_opaque_simple_ddgi_b1_provenance.frag.spv' = 21
    'forward_opaque_simple_full_input_ddgi_b1.frag.spv' = 21
    'forward_opaque_simple_full_input_ddgi_b1_provenance.frag.spv' = 21
    'forward_transparent_ddgi_b1.frag.spv' = 21
    'forward_weighted_oit_ddgi_b1.frag.spv' = 21
    'foliage_forward_ddgi_b1.frag.spv' = 21
    'foliage_forward_ddgi_b1_provenance.frag.spv' = 21
    'fog_b1.comp.spv' = 20
    'particle_b1.vert.spv' = 20
    # The frame-local opaque cache executes the same three exact gather sites
    # while residency demand stays disabled.
    'ddgi_simple_receiver_cache.comp.spv' = 3
    'ddgi_simple_receiver_cache_b1.comp.spv' = 9

    # Sparse page classification, reconciliation, fixed feedback reduction,
    # and generation-safe update lifecycle attribution.
    # The ninth add is the scheduler outcome failure latch used when any lane
    # observes malformed direction-free ray scratch. It prevents the private
    # blend target from reaching CommitLocal; the CPU scheduler takes the
    # equivalent fail-closed probe-state path without this global atomic.
    'ddgi_simple_blend.comp.spv' = 9
    'ddgi_simple_blend_guided.comp.spv' = 9
    # Directional prepare/project each inline the sparse live-address and
    # generation validation contract: stale-resource, two distinct out-of-range
    # branches, stale-mapping, and stale-virtual attribution. Publication has
    # the same five sites plus five inlined CPU-scheduler failure revalidation
    # paths. These bounded integrity counters are functional transaction
    # evidence, not optional renderer diagnostics. Project no longer owns a
    # per-ray completion add after the native-safe one-lane-per-probe split.
    'ddgi_simple_directional_prepare.comp.spv' = 5
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
    # Two additional functional reductions derive the frozen expected
    # participant and texel population on-GPU from the shared eligibility
    # predicate. They replace the delayed host-count witness.
    'ddgi_simple_transport_audit.comp.spv' = 29
    'ddgi_simple_transport_audit_reduce_legacy.comp.spv' = 29
    'ddgi_simple_transport_audit_reduce_validate.comp.spv' = 29
    'ddgi_simple_transport_audit_reduce_packed.comp.spv' = 29
    'ddgi_simple_transport_intermediate_publish.comp.spv' = 5
}
$missingAlgorithmicModules = @($algorithmicAtomicCounts.Keys | Where-Object {
    -not $availableNames.Contains($_)
})
if ($missingAlgorithmicModules.Count -ne 0) {
    throw "Expected production Simple-DDGI algorithmic-atomic module(s) missing from '$resolvedDirectory': $($missingAlgorithmicModules -join ', ')."
}

$spirvMagic = [uint32]0x07230203
$opAtomicIAdd = 234
$violations = [System.Collections.Generic.List[string]]::new()
foreach ($shader in $shaderFiles) {
    [byte[]] $bytes = [System.IO.File]::ReadAllBytes($shader.FullName)
    if ($bytes.Length -lt 20 -or ($bytes.Length % 4) -ne 0) {
        throw "'$($shader.FullName)' is not a complete SPIR-V word stream."
    }
    if ([BitConverter]::ToUInt32($bytes, 0) -ne $spirvMagic) {
        throw "'$($shader.FullName)' does not have the SPIR-V magic word."
    }

    $atomicAdds = 0
    for ($byteOffset = 20; $byteOffset -lt $bytes.Length;) {
        [uint32] $instruction = [BitConverter]::ToUInt32($bytes, $byteOffset)
        $wordCount = $instruction -shr 16
        $opcode = $instruction -band 0xffff
        if ($wordCount -le 0 -or $byteOffset + $wordCount * 4 -gt $bytes.Length) {
            throw "'$($shader.FullName)' contains a malformed SPIR-V instruction at byte $byteOffset."
        }
        if ($opcode -eq $opAtomicIAdd) {
            $atomicAdds++
        }
        $byteOffset += $wordCount * 4
    }

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
