Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$BuildRoot = [string]$env:NJULF_PERF_VERIFY_BUILD_ROOT
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    throw "NJULF_PERF_VERIFY_BUILD_ROOT is required."
}
$buildDirectory = [System.IO.Path]::GetFullPath($BuildRoot)
$assemblyPath = Join-Path $buildDirectory "NjulfHelloGame.dll"
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "Quality verifier assembly is missing: $assemblyPath"
}

$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
$hdrType = $assembly.GetType(
    "NjulfHelloGame.SampleBenchmarkHdrComparer", $true, $false)
$temporalType = $assembly.GetType(
    "NjulfHelloGame.SampleBenchmarkQualityTemporalComparer", $true, $false)
$evidenceIoType = $assembly.GetType(
    "NjulfHelloGame.SampleEvidenceFileIo", $true, $false)
$evidenceType = $assembly.GetType(
    "NjulfHelloGame.SampleEvidenceFileContent", $true, $false)
$nullableEvidenceType = [System.Type]::GetType("System.Nullable``1").MakeGenericType(
    [System.Type[]]@($evidenceType))
$readMethod = $evidenceIoType.GetMethod(
    "Read",
    [System.Reflection.BindingFlags]::Public -bor
        [System.Reflection.BindingFlags]::Static)
$hdrMethods = @($hdrType.GetMethods(
    [System.Reflection.BindingFlags]::NonPublic -bor
        [System.Reflection.BindingFlags]::Static) | Where-Object {
            $parameters = $_.GetParameters()
            $_.Name -eq "Compare" -and $parameters.Count -eq 5 -and
                $parameters[0].ParameterType -eq $evidenceType -and
                $parameters[1].ParameterType -eq $evidenceType -and
                $parameters[2].ParameterType -eq [double] -and
                $parameters[3].ParameterType -eq [double] -and
                $parameters[4].ParameterType -eq $nullableEvidenceType
        })
$temporalMethods = @($temporalType.GetMethods(
    [System.Reflection.BindingFlags]::NonPublic -bor
        [System.Reflection.BindingFlags]::Static) | Where-Object {
            $parameters = $_.GetParameters()
            $_.Name -eq "Compare" -and $parameters.Count -eq 4 -and
                @($parameters | Where-Object {
                    $_.ParameterType -ne $evidenceType
                }).Count -eq 0
        })
if ($null -eq $readMethod -or $hdrMethods.Count -ne 1 -or
    $temporalMethods.Count -ne 1) {
    throw "The frozen build does not expose the approved quality verifier API."
}
$hdrMethod = $hdrMethods[0]
$temporalMethod = $temporalMethods[0]

if (-not [System.OperatingSystem]::IsWindows()) {
    throw "The approved HDR-FLIP verifier is available only on Windows."
}
$runtimeIdentifier = switch (
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    ([System.Runtime.InteropServices.Architecture]::X64) { "win-x64"; break }
    ([System.Runtime.InteropServices.Architecture]::Arm64) { "win-arm64"; break }
    default {
        throw "The approved HDR-FLIP verifier does not support architecture '$($_)'."
    }
}
$flipNativePath = Join-Path $buildDirectory (
    "runtimes/{0}/native/flip_native.dll" -f $runtimeIdentifier)
if (-not (Test-Path -LiteralPath $flipNativePath -PathType Leaf)) {
    throw "The approved HDR-FLIP native runtime is missing: $flipNativePath"
}
# Keep this handle alive until every reflected comparison has completed. A
# PowerShell reflection host does not participate in the apphost RID resolver.
$flipNativeHandle = [System.Runtime.InteropServices.NativeLibrary]::Load(
    $flipNativePath)

function Read-AdmittedEvidence {
    param([string]$Path, [string]$ExpectedSha256, [long]$MaximumBytes, [string]$Role)
    $evidence = $readMethod.Invoke(
        $null,
        [object[]]@(
            [System.IO.Path]::GetFullPath($Path),
            $MaximumBytes,
            $Role))
    if ([string]$evidence.Sha256 -cne $ExpectedSha256) {
        throw "$Role hash differs from the authenticated request."
    }
    return $evidence
}

$requestText = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($requestText)) {
    throw "Quality verifier request was not supplied on standard input."
}
$request = $requestText | ConvertFrom-Json
if ([string]$request.schema -cne "njulf-perf-quality-verify-request/v1") {
    throw "Unsupported quality verifier request schema '$($request.schema)'."
}
$results = @()
foreach ($operation in @($request.operations)) {
    $id = [string]$operation.id
    if ([string]::IsNullOrWhiteSpace($id)) {
        throw "Quality verifier operation id is required."
    }
    switch ([string]$operation.kind) {
        "spatial" {
            $reference = Read-AdmittedEvidence `
                ([string]$operation.referencePath) `
                ([string]$operation.referenceSha256) `
                (256L * 1024L * 1024L) "$id reference"
            $candidate = Read-AdmittedEvidence `
                ([string]$operation.candidatePath) `
                ([string]$operation.candidateSha256) `
                (256L * 1024L * 1024L) "$id candidate"
            $quality = Read-AdmittedEvidence `
                ([string]$operation.qualityContractPath) `
                ([string]$operation.qualityContractSha256) `
                (16L * 1024L * 1024L) "$id quality contract"
            $value = $hdrMethod.Invoke(
                $null,
                [object[]]@(
                    $reference,
                    $candidate,
                    [double]$operation.maximumRelativeRmse,
                    [double]$operation.maximumFlipP95,
                    $quality))
            $results += [ordered]@{
                id = $id
                kind = "spatial"
                value = $value
                inputs = @(
                    [ordered]@{ path = $reference.Path; sha256 = $reference.Sha256 },
                    [ordered]@{ path = $candidate.Path; sha256 = $candidate.Sha256 },
                    [ordered]@{ path = $quality.Path; sha256 = $quality.Sha256 })
            }
        }
        "temporal" {
            $referenceFrom = Read-AdmittedEvidence `
                ([string]$operation.referenceFromPath) `
                ([string]$operation.referenceFromSha256) `
                (256L * 1024L * 1024L) "$id reference-from"
            $referenceTo = Read-AdmittedEvidence `
                ([string]$operation.referenceToPath) `
                ([string]$operation.referenceToSha256) `
                (256L * 1024L * 1024L) "$id reference-to"
            $candidateFrom = Read-AdmittedEvidence `
                ([string]$operation.candidateFromPath) `
                ([string]$operation.candidateFromSha256) `
                (256L * 1024L * 1024L) "$id candidate-from"
            $candidateTo = Read-AdmittedEvidence `
                ([string]$operation.candidateToPath) `
                ([string]$operation.candidateToSha256) `
                (256L * 1024L * 1024L) "$id candidate-to"
            $value = [double]$temporalMethod.Invoke(
                $null,
                [object[]]@(
                    $referenceFrom,
                    $referenceTo,
                    $candidateFrom,
                    $candidateTo))
            $results += [ordered]@{
                id = $id
                kind = "temporal"
                value = [ordered]@{
                    relativeResidual = $value
                    inputs = @(
                        [ordered]@{ path = $referenceFrom.Path; sha256 = $referenceFrom.Sha256 },
                        [ordered]@{ path = $referenceTo.Path; sha256 = $referenceTo.Sha256 },
                        [ordered]@{ path = $candidateFrom.Path; sha256 = $candidateFrom.Sha256 },
                        [ordered]@{ path = $candidateTo.Path; sha256 = $candidateTo.Sha256 })
                }
            }
        }
        default { throw "Unknown quality verifier operation '$($operation.kind)'." }
    }
}

$payload = [ordered]@{
    schema = "njulf-perf-quality-verify-result/v1"
    results = @($results)
}
$json = $payload | ConvertTo-Json -Depth 16 -Compress
[Console]::Out.Write($json)
