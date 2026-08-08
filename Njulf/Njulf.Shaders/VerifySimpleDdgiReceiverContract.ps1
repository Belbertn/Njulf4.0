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

$receiverCacheFragmentModuleNames = @(
    'forward_opaque_ddgi_cache_required.frag.spv',
    'forward_opaque_simple_ddgi_cache_required.frag.spv',
    'forward_opaque_simple_full_input_ddgi_cache_required.frag.spv'
)

$giDisabledControlModuleNames = @(
    'forward_opaque_gi_disabled.frag.spv',
    'forward_opaque_simple_gi_disabled.frag.spv',
    'forward_opaque_simple_full_input_gi_disabled.frag.spv'
)

$receiverCacheResolveModuleName =
    'ddgi_simple_receiver_cache_resolve.comp.spv'

$expectedModuleNames = @(
    $receiverModuleNames
    $receiverCacheFragmentModuleNames
    $giDisabledControlModuleNames
    $receiverCacheResolveModuleName
)
$missingModules = @($expectedModuleNames | Where-Object {
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

# The screen-space cache is the production Sparse forward fast path. Its
# fragment artifacts bind one fixed set-2 storage buffer and issue one aligned
# vector read; paired GI-disabled controls declare and read none. Neither kind may
# retain the exact gather graph or its paging/source/compute-state protocol.
foreach ($moduleName in @($receiverCacheFragmentModuleNames + $giDisabledControlModuleNames)) {
    $modulePath = Join-Path $resolvedDirectory $moduleName
    $validation = (& $spirvVal --target-env vulkan1.3 $modulePath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "spirv-val failed for '$modulePath': $validation"
    }

    $disassembly = (& $spirvDis $modulePath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "spirv-dis failed for '$modulePath': $disassembly"
    }

    $atomicInstructions = [regex]::Matches(
        $disassembly,
        '\bOpAtomic\w*\b').Count
    $receiverConstants = [regex]::Matches(
        $disassembly,
        '%(?:u?int)_' + $receiverIndex + '\b').Count
    $computeStateConstants = [regex]::Matches(
        $disassembly,
        '%(?:u?int)_' + $computeStateIndex + '\b').Count
    $sourceCacheConstants = [regex]::Matches(
        $disassembly,
        '%(?:u?int)_' + $sourceCacheIndex + '\b').Count
    $cacheDescriptorSetCount = [regex]::Matches(
        $disassembly,
        '(?m)^\s*OpDecorate\s+%ForwardDdgiReceiverCache\s+' +
        'DescriptorSet\s+2\s*$').Count
    $cacheBindingCount = [regex]::Matches(
        $disassembly,
        '(?m)^\s*OpDecorate\s+%ForwardDdgiReceiverCache\s+' +
        'Binding\s+0\s*$').Count
    $cacheEntryAccesses = [regex]::Matches(
        $disassembly,
        '(?m)^\s*(?<result>%\w+)\s*=\s*Op(?:InBounds)?AccessChain\s+' +
        '%\w+\s+%ForwardDdgiReceiverCache\b')
    $cacheEntryReadCount = 0
    foreach ($cacheEntryAccess in $cacheEntryAccesses) {
        $entryPointerId = [regex]::Escape(
            $cacheEntryAccess.Groups['result'].Value)
        $cacheEntryReadCount += [regex]::Matches(
            $disassembly,
            '\bOpLoad\s+%v4uint\s+' + $entryPointerId + '\b').Count
    }
    $cacheEfficientAddressCount = 0
    foreach ($cacheEntryAccess in $cacheEntryAccesses) {
        $prefixStart = [Math]::Max(0, $cacheEntryAccess.Index - 1200)
        $addressPrefix = $disassembly.Substring(
            $prefixStart,
            $cacheEntryAccess.Index - $prefixStart)
        if ([regex]::IsMatch(
                $addressPrefix,
                '\bOpShiftRightLogical\s+%v2uint\b') -and
            [regex]::IsMatch(
                $addressPrefix,
                '\bOpIMul\s+%uint\b') -and
            -not [regex]::IsMatch(
                $addressPrefix,
                '\bOpUDiv\b')) {
            $cacheEfficientAddressCount++
        }
    }

    if ($atomicInstructions -ne 0) {
        $violations.Add("${moduleName}: found $atomicInstructions forbidden atomic instruction(s)")
    }
    if ($receiverConstants -ne 0 -or
        $computeStateConstants -ne 0 -or
        $sourceCacheConstants -ne 0) {
        $violations.Add(
            "${moduleName}: retained exact gather ABI constants " +
            "receiver=$receiverConstants computeState=$computeStateConstants sourceCache=$sourceCacheConstants")
    }

    $isCacheModule = $receiverCacheFragmentModuleNames -contains $moduleName
    $expectedCacheSamples = if ($isCacheModule) { 1 } else { 0 }
    if ($cacheDescriptorSetCount -ne $expectedCacheSamples -or
        $cacheBindingCount -ne $expectedCacheSamples) {
        $violations.Add(
            "${moduleName}: found $cacheDescriptorSetCount set-2 and $cacheBindingCount binding-0 receiver-cache decoration(s), expected $expectedCacheSamples each")
    }
    if ($cacheEntryAccesses.Count -ne $expectedCacheSamples -or
        $cacheEntryReadCount -ne $expectedCacheSamples) {
        $violations.Add(
            "${moduleName}: found $($cacheEntryAccesses.Count) receiver-cache entry access(es) and $cacheEntryReadCount raw aligned uvec4 read(s), expected $expectedCacheSamples each")
    }
    if ($cacheEfficientAddressCount -ne $expectedCacheSamples) {
        $violations.Add(
            "${moduleName}: found $cacheEfficientAddressCount division-free receiver-cache address(es), expected $expectedCacheSamples")
    }
}

$resolvePath = Join-Path $resolvedDirectory $receiverCacheResolveModuleName
$resolveValidation =
    (& $spirvVal --target-env vulkan1.3 $resolvePath 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    throw "spirv-val failed for '$resolvePath': $resolveValidation"
}
$resolveDisassembly = (& $spirvDis $resolvePath 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    throw "spirv-dis failed for '$resolvePath': $resolveDisassembly"
}
$resolveOutputAccesses = [regex]::Matches(
    $resolveDisassembly,
    '(?m)^\s*(?<result>%\w+)\s*=\s*Op(?:InBounds)?AccessChain\s+' +
    '%\w+\s+%ReceiverCacheOutput\b')
$resolveOutputWrites = 0
foreach ($resolveOutputAccess in $resolveOutputAccesses) {
    $outputPointerId = [regex]::Escape(
        $resolveOutputAccess.Groups['result'].Value)
    $resolveOutputWrites += [regex]::Matches(
        $resolveDisassembly,
        '\bOpStore\s+' + $outputPointerId + '\s+%\w+\b').Count
}
$resolveAtomics = [regex]::Matches(
    $resolveDisassembly,
    '\bOpAtomic\w*\b').Count
$setTwoMatches = [regex]::Matches(
    $resolveDisassembly,
    '(?m)^\s*OpDecorate\s+(?<id>%\w+)\s+DescriptorSet\s+2\s*$')
$setTwoBindingZeroCount = 0
foreach ($setTwoMatch in $setTwoMatches) {
    $setTwoId = [regex]::Escape($setTwoMatch.Groups['id'].Value)
    $setTwoBindingZeroCount += [regex]::Matches(
        $resolveDisassembly,
        '(?m)^\s*OpDecorate\s+' + $setTwoId + '\s+Binding\s+0\s*$').Count
}
if ($resolveOutputAccesses.Count -ne 1 -or
    $resolveOutputWrites -ne 1 -or
    $setTwoBindingZeroCount -ne 1) {
    $violations.Add(
        "${receiverCacheResolveModuleName}: found $($resolveOutputAccesses.Count) output access(es), " +
        "$resolveOutputWrites aligned write(s), and $setTwoBindingZeroCount set-2/binding-0 output(s), expected one each")
}
if ($resolveAtomics -ne 0) {
    $violations.Add(
        "${receiverCacheResolveModuleName}: found $resolveAtomics forbidden atomic instruction(s)")
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
Write-Host "Validated $($receiverCacheFragmentModuleNames.Count) cache-required forward modules each perform one aligned fixed set-2 FP16 receiver-cache vector read; the $($giDisabledControlModuleNames.Count) paired controls contain no cache descriptor or read, and all six exclude exact gather ABI resources and atomics."
Write-Host "Validated the receiver-cache resolve publishes exactly one aligned set-2/binding-0 FP16 cache-buffer write path and contains no atomics."
