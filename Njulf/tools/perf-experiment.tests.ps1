[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$driver = Join-Path $PSScriptRoot "perf-experiment.ps1"
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $driver, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Experiment driver does not parse: $($parseErrors -join '; ')"
}
foreach ($definition in @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
        }, $true))) {
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Assert-True {
    param([bool]$Value, [string]$Label)
    if (-not $Value) { throw "FAIL $Label" }
    Write-Host "PASS $Label"
}

function New-TestManifest {
    return [pscustomobject]@{
        finalConfigurations = @("Release", "ShippingPerformance")
        capture = [pscustomobject]@{ abbaCycles = 3 }
        acceptance = [pscustomobject]@{
            minimumFrameImprovementPercent = 1.0
            minimumFrameImprovementMilliseconds = 0.10
            minimumPassImprovementPercent = 5.0
            minimumPassImprovementMilliseconds = 0.05
            maximumRegressionPercent = 1.0
            bootstrapSamples = 10000
            bootstrapConfidence = 0.95
        }
        workloads = @(
            [pscustomobject]@{ id = "bistro-test"; qualification = $true })
    }
}

function New-TestReport {
    param([double]$Frame, [double]$Target, [double]$Other = 1.0)
    return [pscustomobject]@{
        CpuFrameMilliseconds = [pscustomobject]@{
            P95Milliseconds = $Frame - 1.0
            P99Milliseconds = $Frame - 0.5
        }
        GpuFrameMilliseconds = [pscustomobject]@{
            P95Milliseconds = $Frame
            P99Milliseconds = $Frame + 0.5
        }
        CpuStages = @([pscustomobject]@{
            Name = "CpuStage"
            P95Milliseconds = $Other
        })
        GpuPasses = @(
            [pscustomobject]@{ Name = "TargetPass"; P95Milliseconds = $Target },
            [pscustomobject]@{ Name = "OtherPass"; P95Milliseconds = $Other })
    }
}

function New-AbbaReports {
    param(
        [double]$BaselineFrame,
        [double]$CandidateFrame,
        [double]$BaselinePass,
        [double]$CandidatePass,
        [double]$BaselineOther = 1.0,
        [double]$CandidateOther = 1.0)
    $reports = @()
    for ($cycle = 0; $cycle -lt 3; $cycle++) {
        $reports += New-TestReport $BaselineFrame $BaselinePass $BaselineOther
        $reports += New-TestReport $CandidateFrame $CandidatePass $CandidateOther
        $reports += New-TestReport $CandidateFrame $CandidatePass $CandidateOther
        $reports += New-TestReport $BaselineFrame $BaselinePass $BaselineOther
    }
    return $reports
}

$manifest = New-TestManifest
$claim = [pscustomobject]@{
    workloadId = "bistro-test"
    targetDomain = "gpu"
    targetPass = "TargetPass"
}

$firstBootstrap = Get-BootstrapLowerBound ([double[]]@(1.0, 1.1, 0.9, 1.0, 1.2, 0.8)) 10000 0.95
$secondBootstrap = Get-BootstrapLowerBound ([double[]]@(1.0, 1.1, 0.9, 1.0, 1.2, 0.8)) 10000 0.95
Assert-True ($firstBootstrap -eq $secondBootstrap -and $firstBootstrap -gt 0.0) `
    "bootstrap-is-deterministic-and-positive"

$loopWin = Compare-ExperimentReports $manifest $claim `
    (New-AbbaReports 20.0 18.8 5.0 4.5) "loop-frame-1ms" "ab"
Assert-True ($loopWin.accepted -and $loopWin.frameImprovementMilliseconds -ge 1.0) `
    "loop-requires-and-accepts-one-millisecond"

$subMillisecond = Compare-ExperimentReports $manifest $claim `
    (New-AbbaReports 20.0 19.2 5.0 4.5) "loop-frame-1ms" "ab"
Assert-True (-not $subMillisecond.accepted) "loop-rejects-sub-millisecond-win"

$passOnly = Compare-ExperimentReports $manifest $claim `
    (New-AbbaReports 20.0 20.0 5.0 4.5) "pass-only" "ab"
Assert-True $passOnly.accepted "pass-only-mode-accepts-isolated-pass-win"

$bothRequired = Compare-ExperimentReports $manifest $claim `
    (New-AbbaReports 20.0 19.8 5.0 4.5) "frame-and-pass" "ab"
Assert-True (-not $bothRequired.accepted) "frame-and-pass-requires-both-gates"

$aa = Compare-ExperimentReports $manifest $claim `
    (New-AbbaReports 20.0 20.0 5.0 5.0) "manifest-either" "aa"
Assert-True $aa.accepted "aa-accepts-stable-noise-without-a-win"

$regressed = Compare-ExperimentReports $manifest $claim `
    (New-AbbaReports 20.0 18.8 5.0 4.5 1.0 1.02) "loop-frame-1ms" "ab"
Assert-True (-not $regressed.accepted -and $regressed.regressions.Count -gt 0) `
    "secondary-regression-rejects-candidate"

$control = Compare-ControlReports $manifest "control" `
    (New-AbbaReports 20.0 20.3 5.0 5.0)
Assert-True (-not $control.accepted) "control-workload-detects-frame-regression"

$missingFailed = $false
try {
    $badClaim = [pscustomobject]@{
        workloadId = "bistro-test"
        targetDomain = "gpu"
        targetPass = "MissingPass"
    }
    $null = Compare-ExperimentReports $manifest $badClaim `
        (New-AbbaReports 20.0 18.0 5.0 4.0) "manifest-either" "ab"
} catch {
    $missingFailed = $_.Exception.Message -match "missing or duplicated"
}
Assert-True $missingFailed "missing-target-pass-fails-closed"

$driverText = Get-Content -LiteralPath $driver -Raw
$gitCallSites = @([regex]::Matches($driverText, 'Get-GitText\s+\$root\s+@\("([^"]+)"'))
Assert-True (
    $gitCallSites.Count -eq 2 -and
    @($gitCallSites | ForEach-Object { $_.Groups[1].Value } | Sort-Object) -join ',' -ceq
        'rev-parse,status') "driver-git-usage-is-read-only"

$testSpec = [pscustomobject]@{
    schema = "njulf-perf-experiment/v1"
    experimentId = "synthetic-aa"
    mode = "aa"
    campaignRunDirectory = ".perf-loop-runs/campaign"
    cookedAssetRoot = "C:\synthetic"
    baseline = [pscustomobject]@{
        sourceRoot = "C:\baseline"
        commit = "a" * 40
        arguments = @("--synthetic-toggle", "on")
        workloadArguments = [pscustomobject]@{}
    }
    candidate = [pscustomobject]@{
        sourceRoot = "C:\baseline"
        commit = "a" * 40
        arguments = @("--synthetic-toggle", "on")
        workloadArguments = [pscustomobject]@{}
    }
    configurations = @("Release", "ShippingPerformance")
    claims = @($claim)
    acceptanceMode = "manifest-either"
    focusedTestFilter = ""
}
Assert-ExperimentSpec $testSpec $manifest
Write-Host "PASS strict-valid-spec"

$testSpec.candidate.arguments = @("--synthetic-toggle", "off")
$aaMismatchFailed = $false
try { Assert-ExperimentSpec $testSpec $manifest } catch {
    $aaMismatchFailed = $_.Exception.Message -match "A/A mode requires identical"
}
Assert-True $aaMismatchFailed "aa-spec-rejects-argument-mismatch"

Write-Host "All perf-experiment tests passed."

