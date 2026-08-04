[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ShaderDirectory
)

$resolvedDirectory = (Resolve-Path -LiteralPath $ShaderDirectory).Path
$allShaderFiles = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward*.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'ddgi_simple_*.comp.spv'
) | Sort-Object FullName -Unique

if ($allShaderFiles.Count -eq 0) {
    throw "No production forward or Simple-DDGI SPIR-V modules were found in '$resolvedDirectory'."
}

# Scheduler stages use bounded atomics for deterministic compaction, admission,
# and lifecycle accounting. They are algorithmic synchronization, not renderer
# diagnostic instrumentation. Keep the no-diagnostic-atomic gate strict for
# forward and update-consumer modules, and make the scheduler exclusion explicit
# so a newly named scheduler module cannot slip through unnoticed.
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

    if ($atomicAdds -ne 0) {
        $violations.Add("$($shader.Name): $atomicAdds OpAtomicIAdd instruction(s)")
    }
}

if ($violations.Count -ne 0) {
    throw "Production DDGI diagnostic atomic verification failed: $($violations -join '; ')."
}

Write-Host "Verified $($shaderFiles.Count) production forward/non-scheduler Simple-DDGI modules contain no OpAtomicIAdd diagnostics; $($schedulerModuleNames.Count) bounded scheduler modules are intentionally excluded."
