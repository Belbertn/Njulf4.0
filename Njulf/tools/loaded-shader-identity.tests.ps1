[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'loaded-shader-identity.ps1')
$golden = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Njulf.Tests/Fixtures/loaded-shader-identity-v1.json') -Raw |
    ConvertFrom-Json
Assert-LoadedShaderIdentity $golden 'Golden vector'
$report = [pscustomobject]@{
    LastDiagnostics = [pscustomobject]@{ CaptureRun = [pscustomobject]@{ LoadedShaderIdentity = $golden } }
    CaptureContract = [pscustomobject]@{ LoadedShaders = [pscustomobject]@{
        StartFingerprint = $golden.Fingerprint; EndFingerprint = $golden.Fingerprint
        StartGeneration = $golden.Generation; EndGeneration = $golden.Generation
    } }
}
Assert-LoadedShaderMeasurement $report
Assert-LoadedShaderPair $golden $golden $true
$changed = $golden | ConvertTo-Json -Depth 12 | ConvertFrom-Json
$changed.Modules[0].SourceKind = 'override'
$changed.Modules[0].SourceIdentity = 'another/path'
Assert-LoadedShaderPair $golden $changed $true
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Name)
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "$Name was incorrectly admitted." }
}
Assert-Rejected { Assert-LoadedShaderIdentity $null } 'Legacy evidence'
$changed.Fingerprint = 'sha256:' + ('0' * 64)
Assert-Rejected { Assert-LoadedShaderIdentity $changed } 'Tampered aggregate'
$report.CaptureContract.LoadedShaders.EndGeneration++
Assert-Rejected { Assert-LoadedShaderMeasurement $report } 'Last-frame drift'
$changed = $golden | ConvertTo-Json -Depth 12 | ConvertFrom-Json
$changed.Modules[1].FileName = $changed.Modules[0].FileName
Assert-Rejected { Assert-LoadedShaderIdentity $changed } 'Conflicting modules'
Write-Output 'PASS loaded shader golden vector, provenance independence, measurement boundaries, and rejection cases'
