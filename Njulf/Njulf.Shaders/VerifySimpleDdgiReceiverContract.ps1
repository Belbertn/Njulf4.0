[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ShaderDirectory
)

$resolvedDirectory = (Resolve-Path -LiteralPath $ShaderDirectory).Path
$spirvDis = (Get-Command spirv-dis -ErrorAction Stop).Source
$spirvVal = (Get-Command spirv-val -ErrorAction Stop).Source

# These are the production receiver artifacts that execute Simple-DDGI gather
# code. Fragment-only foliage artifacts consume interpolated ambient and are not
# receiver modules themselves.
$receiverModuleNames = @(
    'forward.frag.spv',
    'forward_opaque_ddgi.frag.spv',
    'forward_opaque_ddgi_provenance.frag.spv',
    'forward_opaque_simple_ddgi.frag.spv',
    'forward_opaque_simple_ddgi_provenance.frag.spv',
    'forward_opaque_simple_full_input_ddgi.frag.spv',
    'forward_opaque_simple_full_input_ddgi_provenance.frag.spv',
    'forward_weighted_oit.frag.spv',
    'particle.vert.spv',
    'foliage_grass.mesh.spv',
    'foliage_mesh.mesh.spv',
    'fog.comp.spv'
)

$exactOpaqueDemandModuleNames = @(
    'forward_opaque_ddgi.frag.spv',
    'forward_opaque_ddgi_provenance.frag.spv',
    'forward_opaque_simple_ddgi.frag.spv',
    'forward_opaque_simple_ddgi_provenance.frag.spv',
    'forward_opaque_simple_full_input_ddgi.frag.spv',
    'forward_opaque_simple_full_input_ddgi_provenance.frag.spv',
    'foliage_grass.mesh.spv',
    'foliage_mesh.mesh.spv'
)

$missingModules = @($receiverModuleNames | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $resolvedDirectory $_) -PathType Leaf)
})
if ($missingModules.Count -ne 0) {
    throw "Expected production Simple-DDGI receiver module(s) missing from '$resolvedDirectory': $($missingModules -join ', ')."
}

# Appended static bindless ABI. BindlessIndexTests mirror these values from
# common.glsl and BindlessIndexTable.cs.
$computeStateIndex = 156
$sourceCacheIndex = 160
$receiverIndex = 174
$violations = [System.Collections.Generic.List[string]]::new()

foreach ($moduleName in $receiverModuleNames) {
    $modulePath = Join-Path $resolvedDirectory $moduleName
    $validation = (& $spirvVal --target-env vulkan1.3 $modulePath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "spirv-val failed for '$modulePath': $validation"
    }

    $disassembly = (& $spirvDis $modulePath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "spirv-dis failed for '$modulePath': $disassembly"
    }

    $accessPatternPrefix = 'Op(?:InBounds)?AccessChain[^\r\n]*%BindlessStorage(?:Vector)?Buffers[^\r\n]*'
    $receiverAccesses = [regex]::Matches(
        $disassembly,
        $accessPatternPrefix + '%(?:u?int)_' + $receiverIndex + '\b').Count
    $computeStateAccesses = [regex]::Matches(
        $disassembly,
        $accessPatternPrefix + '%(?:u?int)_' + $computeStateIndex + '\b').Count
    $sourceCacheAccesses = [regex]::Matches(
        $disassembly,
        $accessPatternPrefix + '%(?:u?int)_' + $sourceCacheIndex + '\b').Count
    $functionalAtomicAdds = [regex]::Matches($disassembly, '\bOpAtomicIAdd\b').Count
    $functionalAtomicExchanges = [regex]::Matches(
        $disassembly,
        '\bOpAtomicExchange\b').Count
    $functionalAtomicCompareExchanges = [regex]::Matches(
        $disassembly,
        '\bOpAtomicCompareExchange\b').Count
    $functionalAtomicUnsignedMaximums = [regex]::Matches(
        $disassembly,
        '\bOpAtomicUMax\b').Count

    # The optimized source currently contains three inlined gather call sites;
    # each site has exactly one aligned uvec4 receiver-record load instruction.
    # Pinning this count catches accidental scalarization or duplicate state
    # fetches while allowing each invocation to iterate its eight corners.
    if ($receiverAccesses -ne 3) {
        $violations.Add("${moduleName}: found $receiverAccesses compact receiver access chain(s), expected 3")
    }
    if ($computeStateAccesses -ne 0) {
        $violations.Add("${moduleName}: found $computeStateAccesses compute-state access chain(s)")
    }
    if ($sourceCacheAccesses -ne 0) {
        $violations.Add("${moduleName}: found $sourceCacheAccesses source-cache access chain(s)")
    }
    # Three optimized gather sites share the lock-free epoch marker, deduplicated
    # receiver-demand claim, overflow rollback, and exact fixed-summary counters.
    # Pin the complete protocol so it cannot silently grow or disappear.
    if ($functionalAtomicAdds -ne 11) {
        $violations.Add("${moduleName}: found $functionalAtomicAdds OpAtomicIAdd instruction(s), expected 11")
    }
    if ($functionalAtomicExchanges -ne 3) {
        $violations.Add("${moduleName}: found $functionalAtomicExchanges OpAtomicExchange instruction(s), expected 3")
    }
    if ($functionalAtomicCompareExchanges -ne 3) {
        $violations.Add("${moduleName}: found $functionalAtomicCompareExchanges OpAtomicCompareExchange instruction(s), expected 3")
    }
    # Non-opaque receivers contain only their three inlined receiver-demand
    # maxima. Opaque artifacts additionally contain the exact Shadow-oracle
    # path at each gather site; Sparse keeps its bounded receiver feedback.
    $expectedAtomicUnsignedMaximums = if ($exactOpaqueDemandModuleNames -contains $moduleName) { 6 } else { 3 }
    if ($functionalAtomicUnsignedMaximums -ne $expectedAtomicUnsignedMaximums) {
        $violations.Add("${moduleName}: found $functionalAtomicUnsignedMaximums OpAtomicUMax instruction(s), expected $expectedAtomicUnsignedMaximums")
    }
}

$forbiddenArtifacts = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter '*ssgi*.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward_opaque.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward_opaque_provenance.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward_opaque_simple.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward_opaque_simple_provenance.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward_opaque_simple_full_input.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward_opaque_simple_full_input_provenance.frag.spv'
) | Sort-Object FullName -Unique
if ($forbiddenArtifacts.Count -ne 0) {
    $violations.Add("obsolete SSGI/legacy receiver artifact(s) remain: $($forbiddenArtifacts.Name -join ', ')")
}

if ($violations.Count -ne 0) {
    throw "Production Simple-DDGI receiver SPIR-V verification failed: $($violations -join '; ')."
}

Write-Host "Validated and verified $($receiverModuleNames.Count) production Simple-DDGI receiver modules use exactly one compact uvec4 load per inlined gather site, the exact bounded paging-demand atomic protocol, no compute-state/source-cache access, and no obsolete SSGI artifact."
