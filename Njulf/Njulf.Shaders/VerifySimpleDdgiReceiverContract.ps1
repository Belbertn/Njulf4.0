[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ShaderDirectory
)

$resolvedDirectory = (Resolve-Path -LiteralPath $ShaderDirectory).Path
$spirvDis = (Get-Command spirv-dis -ErrorAction Stop).Source
$spirvOpt = (Get-Command spirv-opt -ErrorAction Stop).Source
$spirvVal = (Get-Command spirv-val -ErrorAction Stop).Source

function Read-SpirvDisassembly {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ModulePath
    )

    # Capturing multi-megabyte disassemblies through the PowerShell pipeline
    # materializes one managed string per output line. Let spirv-dis stream to
    # a private file instead, then read it once as a single string for the
    # contract regexes below.
    $temporaryPath = [IO.Path]::Combine(
        [IO.Path]::GetTempPath(),
        'njulf-spirv-dis-' + [Guid]::NewGuid().ToString('N') + '.txt')
    try {
        $diagnostics =
            (& $spirvDis --no-color -o $temporaryPath $ModulePath 2>&1) -join
                [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) {
            throw "spirv-dis failed for '$ModulePath': $diagnostics"
        }

        return [IO.File]::ReadAllText($temporaryPath)
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function Read-ProductionSpecializedSpirvDisassembly {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ModulePath
    )

    # Forward performance controls are specialization constants. Embedded
    # modules retain the exact rollback graph; production pipelines freeze the
    # default all-enabled mask before native compilation. Verify that same IR.
    $temporaryPath = [IO.Path]::Combine(
        [IO.Path]::GetTempPath(),
        'njulf-spirv-opt-' + [Guid]::NewGuid().ToString('N') + '.spv')
    try {
        $optimizationArguments = @(
            '--target-env=vulkan1.3'
            '--freeze-spec-const'
            '--eliminate-dead-branches'
            '--eliminate-dead-code-aggressive'
            '-O'
            $ModulePath
            '-o'
            $temporaryPath)
        $diagnostics =
            (& $spirvOpt @optimizationArguments 2>&1) -join
                [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) {
            throw "spirv-opt failed for '$ModulePath': $diagnostics"
        }

        $validation =
            (& $spirvVal --target-env vulkan1.3 $temporaryPath 2>&1) -join
                [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) {
            throw "spirv-val failed for specialized '$ModulePath': $validation"
        }

        return Read-SpirvDisassembly -ModulePath $temporaryPath
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

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

$transparentReflectionTelemetryModuleNames = @(
    'forward.frag.spv',
    'forward_weighted_oit.frag.spv'
)

$receiverCacheFragmentModuleNames = @(
    'forward_opaque_ddgi_cache_required.frag.spv',
    'forward_opaque_simple_ddgi_cache_required.frag.spv',
    'forward_opaque_simple_full_input_ddgi_cache_required.frag.spv',
    'forward_opaque_ddgi_near_field_direct_source_cache_required.frag.spv',
    'forward_opaque_simple_ddgi_near_field_direct_source_cache_required.frag.spv',
    'forward_opaque_simple_full_input_ddgi_near_field_direct_source_cache_required.frag.spv'
)

# These production DdgiHigh artifacts have an exclusive opaque GI owner split:
# the receiver cache owns admitted diffuse/visibility and the deferred hybrid
# pass owns indirect specular. The default split uses complementary accepted
# and exact-fallback native programs; combined programs remain the immediate
# rollback. Compact directional L2 (static bindless slots 178/179) must be
# absent from every production-specialized ownership-locked module.
$ownershipLockedReceiverCacheAcceptedModuleNames = @(
    'forward_opaque_ddgi_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_ddgi_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_full_input_ddgi_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_ddgi_c4_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_ddgi_c4_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_full_input_ddgi_c4_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_ddgi_c5_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_ddgi_c5_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_full_input_ddgi_c5_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_ddgi_c4_c5_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_ddgi_c4_c5_cache_required_hybrid_reflection.frag.spv',
    'forward_opaque_simple_full_input_ddgi_c4_c5_cache_required_hybrid_reflection.frag.spv'
)
$ownershipLockedReceiverCacheAcceptedModuleNames += @(
    $ownershipLockedReceiverCacheAcceptedModuleNames | ForEach-Object {
        $_ -replace '\.frag\.spv$', '_sparse_lobe.frag.spv'
    }
)
$ownershipLockedReceiverCacheExactFallbackModuleNames = @(
    $ownershipLockedReceiverCacheAcceptedModuleNames | ForEach-Object {
        $_ -replace 'cache_required_', 'cache_exact_fallback_'
    }
)
$ownershipLockedReceiverCacheCombinedModuleNames = @(
    $ownershipLockedReceiverCacheAcceptedModuleNames | ForEach-Object {
        $_ -replace 'cache_required_', 'cache_combined_'
    }
)
$ownershipLockedReceiverCacheFragmentModuleNames = @(
    $ownershipLockedReceiverCacheAcceptedModuleNames
    $ownershipLockedReceiverCacheExactFallbackModuleNames
    $ownershipLockedReceiverCacheCombinedModuleNames
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
    $ownershipLockedReceiverCacheFragmentModuleNames
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

    $disassembly = Read-SpirvDisassembly -ModulePath $modulePath

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
    $functionalAtomicOrs = [regex]::Matches(
        $disassembly,
        '\bOpAtomicOr\b').Count

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
    # receiver-demand claim, overflow rollback, exact fixed-summary counters,
    # and one B1 interpolation-mass accumulation each. Transparent receivers
    # append sparse source estimates plus exact SSR admission, reservation,
    # sampling, and hit-budget evidence without changing the DDGI receiver
    # protocol itself. Universal transparent programs also retain one bounded
    # invalid physical-page mapping feedback add.
    $expectedAtomicAdds = if ($moduleName -eq
        'foliage_mesh.mesh.spv') {
        # Physical meshlet residency contributes eight bounded range-demand
        # bookkeeping adds plus ten subgroup-aggregated resolved-mapping
        # validation/attribution adds in addition to the receiver protocol.
        32
    } elseif (
        $transparentReflectionTelemetryModuleNames -contains $moduleName) {
        27
    } else {
        14
    }
    if ($functionalAtomicAdds -ne $expectedAtomicAdds) {
        $violations.Add("${moduleName}: found $functionalAtomicAdds OpAtomicIAdd instruction(s), expected $expectedAtomicAdds")
    }
    $expectedAtomicOrs = if ($moduleName -eq
        'foliage_mesh.mesh.spv') { 4 } else { 3 }
    if ($functionalAtomicOrs -ne $expectedAtomicOrs) {
        $violations.Add("${moduleName}: found $functionalAtomicOrs OpAtomicOr instruction(s), expected $expectedAtomicOrs")
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

# The screen-space cache is the production Sparse forward fast path. Surface-aware
# fragments first read one fixed set-2/binding-1 uvec2 sidecar entry. Accepted
# fragments then read the matching set-2/binding-0 uvec4 radiance entry; rejected
# fragments retain the exact three-gather fallback and its paging protocol. Paired
# GI-disabled controls declare and execute none of those paths.
foreach ($moduleName in @(
        $receiverCacheFragmentModuleNames +
        $ownershipLockedReceiverCacheFragmentModuleNames +
        $giDisabledControlModuleNames)) {
    $modulePath = Join-Path $resolvedDirectory $moduleName
    $validation = (& $spirvVal --target-env vulkan1.3 $modulePath 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "spirv-val failed for '$modulePath': $validation"
    }

    $disassembly = Read-SpirvDisassembly -ModulePath $modulePath

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
    $exactReceiverAccesses = [regex]::Matches(
        $disassembly,
        '(?m)^\s*%\w+\s*=\s*Op(?:InBounds)?AccessChain\s+' +
        '%\w+\s+%BindlessStorageVectorBuffers\s+%uint_' +
        $receiverIndex + '\b').Count
    $cacheDescriptorSetCount = [regex]::Matches(
        $disassembly,
        '(?m)^\s*OpDecorate\s+%ForwardDdgiReceiverCache\s+' +
        'DescriptorSet\s+2\s*$').Count
    $cacheBindingCount = [regex]::Matches(
        $disassembly,
        '(?m)^\s*OpDecorate\s+%ForwardDdgiReceiverCache\s+' +
        'Binding\s+0\s*$').Count
    $surfaceDescriptorSetCount = [regex]::Matches(
        $disassembly,
        '(?m)^\s*OpDecorate\s+%ForwardDdgiReceiverSurface\s+' +
        'DescriptorSet\s+2\s*$').Count
    $surfaceBindingCount = [regex]::Matches(
        $disassembly,
        '(?m)^\s*OpDecorate\s+%ForwardDdgiReceiverSurface\s+' +
        'Binding\s+1\s*$').Count
    $cacheEntryAccesses = [regex]::Matches(
        $disassembly,
        '(?m)^\s*(?<result>%\w+)\s*=\s*Op(?:InBounds)?AccessChain\s+' +
        '%\w+\s+%ForwardDdgiReceiverCache\s+%int_0\s+' +
        '(?<index>%\w+)\s*$')
    $surfaceEntryAccesses = [regex]::Matches(
        $disassembly,
        '(?m)^\s*(?<result>%\w+)\s*=\s*Op(?:InBounds)?AccessChain\s+' +
        '%\w+\s+%ForwardDdgiReceiverSurface\s+%int_0\s+' +
        '(?<index>%\w+)\s*$')
    $cacheEntryReadCount = 0
    foreach ($cacheEntryAccess in $cacheEntryAccesses) {
        $entryPointerId = [regex]::Escape(
            $cacheEntryAccess.Groups['result'].Value)
        $cacheEntryReadCount += [regex]::Matches(
            $disassembly,
            '\bOpLoad\s+%v4uint\s+' + $entryPointerId + '\b').Count
    }
    $surfaceEntryReadCount = 0
    foreach ($surfaceEntryAccess in $surfaceEntryAccesses) {
        $entryPointerId = [regex]::Escape(
            $surfaceEntryAccess.Groups['result'].Value)
        $surfaceEntryReadCount += [regex]::Matches(
            $disassembly,
            '\bOpLoad\s+%v2uint\s+' + $entryPointerId + '\b').Count
    }
    $surfaceEfficientAddressCount = 0
    foreach ($surfaceEntryAccess in $surfaceEntryAccesses) {
        $prefixStart = [Math]::Max(0, $surfaceEntryAccess.Index - 1200)
        $addressPrefix = $disassembly.Substring(
            $prefixStart,
            $surfaceEntryAccess.Index - $prefixStart)
        if ([regex]::IsMatch(
                $addressPrefix,
                '\bOpShiftRightLogical\s+%v2uint\b') -and
            [regex]::IsMatch(
                $addressPrefix,
                '\bOpIMul\s+%uint\b') -and
            -not [regex]::IsMatch(
                $addressPrefix,
                '\bOpUDiv\b')) {
            $surfaceEfficientAddressCount++
        }
    }

    $isOwnershipLockedModule =
        $ownershipLockedReceiverCacheFragmentModuleNames -contains $moduleName
    $isOwnershipLockedAcceptedModule =
        $ownershipLockedReceiverCacheAcceptedModuleNames -contains $moduleName
    $isOwnershipLockedExactFallbackModule =
        $ownershipLockedReceiverCacheExactFallbackModuleNames -contains $moduleName
    $isOwnershipLockedCombinedModule =
        $ownershipLockedReceiverCacheCombinedModuleNames -contains $moduleName
    $isCacheModule =
        $receiverCacheFragmentModuleNames -contains $moduleName -or
        $isOwnershipLockedModule
    $ownershipDisassembly = if ($isOwnershipLockedModule) {
        Read-ProductionSpecializedSpirvDisassembly -ModulePath $modulePath
    } else {
        $disassembly
    }
    $compactDirectionalBaseReferences = [regex]::Matches(
        $ownershipDisassembly,
        '%(?:u?int)_178\b').Count
    $compactDirectionalNextBankReferences = [regex]::Matches(
        $ownershipDisassembly,
        '%(?:u?int)_179\b').Count
    $expectedSurfaceSamples = if ($isCacheModule) { 1 } else { 0 }
    $expectedRadianceSamples = if (
        $isCacheModule -and
        -not $isOwnershipLockedExactFallbackModule) { 1 } else { 0 }
    $expectedExactReceiverConstants = if (
        $isCacheModule -and
        -not $isOwnershipLockedAcceptedModule) { 4 } else { 0 }
    $expectedExactReceiverAccesses = if (
        $isCacheModule -and
        -not $isOwnershipLockedAcceptedModule) { 3 } else { 0 }
    # Cache-capable opaque programs retain both independent fail-closed lanes:
    # the canonical rejection gather and exact B1 ownership. The optimized
    # masked path adds three bounded list operations: candidate high-water,
    # overflow fallback, and dense publication maximum. Overflow still executes
    # the original exact gather in the same fragment.
    $expectedAtomicInstructions = if (
        $isOwnershipLockedAcceptedModule) {
        0
    } elseif ($isOwnershipLockedExactFallbackModule) {
        29
    } elseif ($isCacheModule) {
        46
    } else {
        0
    }

    if ($atomicInstructions -ne $expectedAtomicInstructions) {
        $violations.Add(
            "${moduleName}: found $atomicInstructions exact-fallback atomic instruction(s), expected $expectedAtomicInstructions")
    }
    if ($isOwnershipLockedModule -and
        ($compactDirectionalBaseReferences -ne 0 -or
         $compactDirectionalNextBankReferences -ne 0)) {
        $violations.Add(
            "${moduleName}: specialized ownership-locked artifact retains compact directional L2 bindless references " +
            "base178=$compactDirectionalBaseReferences bank179=$compactDirectionalNextBankReferences; expected 0/0")
    }
    if ($receiverConstants -ne $expectedExactReceiverConstants -or
        $exactReceiverAccesses -ne $expectedExactReceiverAccesses -or
        $computeStateConstants -ne 0 -or
        $sourceCacheConstants -ne 0) {
        $violations.Add(
            "${moduleName}: exact fallback ABI mismatch " +
            "receiverConstants=$receiverConstants receiverAccesses=$exactReceiverAccesses " +
            "computeState=$computeStateConstants sourceCache=$sourceCacheConstants; expected " +
            "receiverConstants=$expectedExactReceiverConstants receiverAccesses=$expectedExactReceiverAccesses computeState=0 sourceCache=0")
    }
    if ($cacheDescriptorSetCount -ne $expectedRadianceSamples -or
        $cacheBindingCount -ne $expectedRadianceSamples -or
        $surfaceDescriptorSetCount -ne $expectedSurfaceSamples -or
        $surfaceBindingCount -ne $expectedSurfaceSamples) {
        $violations.Add(
            "${moduleName}: found radiance set=$cacheDescriptorSetCount binding0=$cacheBindingCount and " +
            "surface set=$surfaceDescriptorSetCount binding1=$surfaceBindingCount decoration(s), expected radiance=$expectedRadianceSamples surface=$expectedSurfaceSamples")
    }
    if ($cacheEntryAccesses.Count -ne $expectedRadianceSamples -or
        $cacheEntryReadCount -ne $expectedRadianceSamples -or
        $surfaceEntryAccesses.Count -ne $expectedSurfaceSamples -or
        $surfaceEntryReadCount -ne $expectedSurfaceSamples) {
        $violations.Add(
            "${moduleName}: found radiance accesses=$($cacheEntryAccesses.Count)/uvec4 reads=$cacheEntryReadCount and " +
            "surface accesses=$($surfaceEntryAccesses.Count)/uvec2 reads=$surfaceEntryReadCount, expected radiance=$expectedRadianceSamples surface=$expectedSurfaceSamples")
    }
    if ($surfaceEfficientAddressCount -ne $expectedSurfaceSamples) {
        $violations.Add(
            "${moduleName}: found $surfaceEfficientAddressCount division-free receiver-surface address(es), expected $expectedSurfaceSamples")
    }
    if ($expectedRadianceSamples -eq 1 -and
        $expectedSurfaceSamples -eq 1 -and
        $cacheEntryAccesses.Count -eq 1 -and
        $surfaceEntryAccesses.Count -eq 1 -and
        $cacheEntryAccesses[0].Groups['index'].Value -ne
            $surfaceEntryAccesses[0].Groups['index'].Value) {
        $violations.Add(
            "${moduleName}: radiance and receiver-surface sidecar reads do not use the same cache entry address")
    }
}

$resolvePath = Join-Path $resolvedDirectory $receiverCacheResolveModuleName
$resolveValidation =
    (& $spirvVal --target-env vulkan1.3 $resolvePath 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    throw "spirv-val failed for '$resolvePath': $resolveValidation"
}
$resolveDisassembly = Read-SpirvDisassembly -ModulePath $resolvePath
$resolveRadianceOutputAccesses = [regex]::Matches(
    $resolveDisassembly,
    '(?m)^\s*(?<result>%\w+)\s*=\s*Op(?:InBounds)?AccessChain\s+' +
    '%\w+\s+%ReceiverCacheOutput\b')
$resolveSurfaceOutputAccesses = [regex]::Matches(
    $resolveDisassembly,
    '(?m)^\s*(?<result>%\w+)\s*=\s*Op(?:InBounds)?AccessChain\s+' +
    '%\w+\s+%ReceiverSurfaceOutput\b')
$resolveRadianceOutputWrites = 0
foreach ($resolveOutputAccess in $resolveRadianceOutputAccesses) {
    $outputPointerId = [regex]::Escape(
        $resolveOutputAccess.Groups['result'].Value)
    $resolveRadianceOutputWrites += [regex]::Matches(
        $resolveDisassembly,
        '\bOpStore\s+' + $outputPointerId + '\s+%\w+\b').Count
}
$resolveSurfaceOutputWrites = 0
foreach ($resolveOutputAccess in $resolveSurfaceOutputAccesses) {
    $outputPointerId = [regex]::Escape(
        $resolveOutputAccess.Groups['result'].Value)
    $resolveSurfaceOutputWrites += [regex]::Matches(
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
$setTwoBindingOneCount = 0
foreach ($setTwoMatch in $setTwoMatches) {
    $setTwoId = [regex]::Escape($setTwoMatch.Groups['id'].Value)
    $setTwoBindingZeroCount += [regex]::Matches(
        $resolveDisassembly,
        '(?m)^\s*OpDecorate\s+' + $setTwoId + '\s+Binding\s+0\s*$').Count
    $setTwoBindingOneCount += [regex]::Matches(
        $resolveDisassembly,
        '(?m)^\s*OpDecorate\s+' + $setTwoId + '\s+Binding\s+1\s*$').Count
}
if ($resolveRadianceOutputAccesses.Count -ne 4 -or
    $resolveRadianceOutputWrites -ne 4 -or
    $resolveSurfaceOutputAccesses.Count -ne 4 -or
    $resolveSurfaceOutputWrites -ne 4 -or
    $setTwoBindingZeroCount -ne 1 -or
    $setTwoBindingOneCount -ne 1) {
    $violations.Add(
        "${receiverCacheResolveModuleName}: found radiance accesses=$($resolveRadianceOutputAccesses.Count)/writes=$resolveRadianceOutputWrites, " +
        "surface accesses=$($resolveSurfaceOutputAccesses.Count)/writes=$resolveSurfaceOutputWrites, " +
        "set-2 binding0=$setTwoBindingZeroCount/binding1=$setTwoBindingOneCount; expected 4/4, 4/4, and one each")
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

Write-Host "Validated and verified $($receiverModuleNames.Count) production Simple-DDGI receiver modules use exactly one compact uvec4 load per inlined gather site, the exact bounded paging-demand/B1 contribution atomic protocol, no compute-state/source-cache access, and no obsolete SSGI artifact."
Write-Host "Validated $($receiverCacheFragmentModuleNames.Count) cache-required forward modules each perform one aligned set-2/binding-1 receiver-surface admission read, conditionally read the matching binding-0 FP16 radiance entry, and retain the exact three-gather fallback; the $($giDisabledControlModuleNames.Count) paired controls contain none of those paths."
Write-Host "Validated $($ownershipLockedReceiverCacheFragmentModuleNames.Count) ownership-locked cache/hybrid modules implement complementary accepted/exact-fallback plus combined rollback ABIs while their production-specialized IR contains no compact directional L2 bindless references."
Write-Host "Validated the receiver-cache resolve publishes all four deterministic radiance and receiver-surface write paths through set-2 bindings 0 and 1 and contains no atomics."
