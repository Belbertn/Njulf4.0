[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ShaderDirectory
)

$resolvedDirectory = (Resolve-Path -LiteralPath $ShaderDirectory).Path
$shaderFiles = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'forward*.frag.spv'
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter 'ddgi_simple_*.comp.spv'
) | Sort-Object FullName -Unique

if ($shaderFiles.Count -eq 0) {
    throw "No production forward or Simple-DDGI SPIR-V modules were found in '$resolvedDirectory'."
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

    if ($atomicAdds -ne 0) {
        $violations.Add("$($shader.Name): $atomicAdds OpAtomicIAdd instruction(s)")
    }
}

if ($violations.Count -ne 0) {
    throw "Production DDGI diagnostic atomic verification failed: $($violations -join '; ')."
}

Write-Host "Verified $($shaderFiles.Count) production forward/Simple-DDGI modules contain no OpAtomicIAdd diagnostics."
