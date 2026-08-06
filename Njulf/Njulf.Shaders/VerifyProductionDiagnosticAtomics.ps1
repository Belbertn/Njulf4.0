[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ShaderDirectory
)

$resolvedDirectory = (Resolve-Path -LiteralPath $ShaderDirectory).Path
$allShaderFiles = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward*.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'ddgi_simple_*.comp.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'fog.comp.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'particle.vert.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_grass.mesh.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'foliage_mesh.mesh.spv'
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
    'ddgi_simple_schedule_classify.comp.spv',
    'ddgi_simple_schedule_commit_local.comp.spv',
    'ddgi_simple_schedule_commit_propagation.comp.spv',
    'ddgi_simple_schedule_compact.comp.spv',
    'ddgi_simple_schedule_emit.comp.spv',
    'ddgi_simple_schedule_feedback.comp.spv',
    'ddgi_simple_schedule_lane_base.comp.spv',
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
    # Lock-free, bounded receiver demand and exact gather attribution.
    'forward.frag.spv' = 11
    'forward_opaque_ddgi.frag.spv' = 11
    'forward_opaque_ddgi_provenance.frag.spv' = 11
    'forward_opaque_simple_ddgi.frag.spv' = 11
    'forward_opaque_simple_ddgi_provenance.frag.spv' = 11
    'forward_opaque_simple_full_input_ddgi.frag.spv' = 11
    'forward_opaque_simple_full_input_ddgi_provenance.frag.spv' = 11
    'forward_weighted_oit.frag.spv' = 11
    'fog.comp.spv' = 11
    'particle.vert.spv' = 11
    'foliage_grass.mesh.spv' = 11
    'foliage_mesh.mesh.spv' = 11

    # Sparse page classification, reconciliation, fixed feedback reduction,
    # and generation-safe update lifecycle attribution.
    # The ninth add is the scheduler outcome failure latch used when any lane
    # observes malformed direction-free ray scratch. It prevents the private
    # blend target from reaching CommitLocal; the CPU scheduler takes the
    # equivalent fail-closed probe-state path without this global atomic.
    'ddgi_simple_blend.comp.spv' = 9
    'ddgi_simple_page_classify.comp.spv' = 8
    # Workgroup-parallel virtual/reverse-map summary reductions use 23
    # additional functional shared-memory atomics. Pin the exact count so a
    # future diagnostic call site or accidental serialization cannot hide.
    'ddgi_simple_page_feedback.comp.spv' = 44
    'ddgi_simple_page_reconcile.comp.spv' = 4
    'ddgi_simple_publish.comp.spv' = 5
    'ddgi_simple_publish_sampled.comp.spv' = 5
    'ddgi_simple_relocate_classify.comp.spv' = 5
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
    'ddgi_simple_transport.comp.spv' = 7
    'ddgi_simple_transport_legacy.comp.spv' = 7
    'ddgi_simple_transport_validate.comp.spv' = 7
    'ddgi_simple_transport_packed.comp.spv' = 7
    'ddgi_simple_transport_solve_legacy.comp.spv' = 7
    'ddgi_simple_transport_solve_validate.comp.spv' = 7
    'ddgi_simple_transport_solve_packed.comp.spv' = 7
    # Transfer initialization and the one-invocation-per-ray operator phase use
    # no additive atomics (the ray phase has only status OR and contraction
    # max). All additive certificate/cache-rejection reductions are isolated in
    # the small second shader so native drivers never lower the recursive
    # operator, workgroup coordination, and certificate reduction together.
    'ddgi_simple_transport_audit.comp.spv' = 27
    'ddgi_simple_transport_audit_reduce_legacy.comp.spv' = 27
    'ddgi_simple_transport_audit_reduce_validate.comp.spv' = 27
    'ddgi_simple_transport_audit_reduce_packed.comp.spv' = 27
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
