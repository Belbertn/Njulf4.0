[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'loaded-shader-identity.ps1')
$script:LoadedShaderFixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Njulf.Tests/Fixtures/loaded-shader-identity-v1.json') -Raw | ConvertFrom-Json

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
        performanceTarget = [pscustomobject]@{
            cpuP95Milliseconds = 6.0
            gpuP95Milliseconds = 10.0
            frameP99Milliseconds = 16.666666666666668
        }
        workloads = @(
            [pscustomobject]@{ id = "bistro-test"; qualification = $true })
    }
}

function New-TestReport {
    param([double]$Frame, [double]$Target, [double]$Other = 1.0)
    return [pscustomobject]@{
        LastDiagnostics = [pscustomobject]@{ CaptureRun = [pscustomobject]@{ LoadedShaderIdentity = $script:LoadedShaderFixture } }
        CaptureContract = [pscustomobject]@{ LoadedShaders = [pscustomobject]@{
            StartFingerprint = $script:LoadedShaderFixture.Fingerprint
            EndFingerprint = $script:LoadedShaderFixture.Fingerprint
            StartGeneration = $script:LoadedShaderFixture.Generation
            EndGeneration = $script:LoadedShaderFixture.Generation
        } }
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
        AutomaticPlanarEvidence = [pscustomobject]@{
            Available = $true
            CaptureFrameMilliseconds = [pscustomobject]@{
                Count = 2
                P95Milliseconds = $Target
            }
        }
    }
}

function New-PretargetCapture {
    param([string]$BudgetName = "DDGI total memory", [string]$GiError = "GiBudgetOverrun")
    $report = New-TestReport 20.0 5.0
    $report | Add-Member -NotePropertyName Options -NotePropertyValue ([pscustomobject]@{
        RequireRealtime1080p60Target = $false
    })
    $report | Add-Member -NotePropertyName MeasurementFrameCount -NotePropertyValue 1
    $report | Add-Member -NotePropertyName GpuTimingSupported -NotePropertyValue 1
    $report | Add-Member -NotePropertyName GpuTimingValidSampleCount -NotePropertyValue 1
    $report.GpuFrameMilliseconds | Add-Member -NotePropertyName Count -NotePropertyValue 1
    $report | Add-Member -NotePropertyName SettlingWaitTimedOut -NotePropertyValue $false
    $report.CaptureContract | Add-Member -NotePropertyName Comparable -NotePropertyValue $true
    $report.CaptureContract | Add-Member -NotePropertyName Mismatches -NotePropertyValue @()
    $report | Add-Member -NotePropertyName DdgiProductionGate -NotePropertyValue $null
    $report | Add-Member -NotePropertyName BudgetMetrics -NotePropertyValue @(
        [pscustomobject]@{
            Name = $BudgetName
            Status = 3
            Value = 210287208
            Unit = "bytes"
            FailureThreshold = 201326592
        })
    $health = [pscustomobject]@{
        status = "failed"
        failure = "Benchmark exceeded '$BudgetName': 210287208 bytes > 201326592 bytes."
        validationWarningCount = 0
        validationErrorCount = 0
        operations = @()
        diagnostics = [pscustomobject]@{
            ValidationWarningMessageCount = 0
            ValidationErrorMessageCount = 0
            GiWarnings = if ([string]::IsNullOrEmpty($GiError)) { @() } else {
                @([pscustomobject]@{ Severity = "Error"; Code = $GiError })
            }
        }
    }
    return [pscustomobject]@{ Report = $report; Health = $health }
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

$planarClaim = [pscustomobject]@{
    workloadId = "bistro-test"
    targetDomain = "gpu"
    targetPass = "__automatic_planar_capture__"
}
$planarComparison = Compare-ExperimentReports $manifest $planarClaim `
    (New-AbbaReports 20.0 20.0 5.0 4.5) "pass-only" "ab"
Assert-True ($planarComparison.accepted -and
    $planarComparison.baselineTargetP95Milliseconds -eq 5.0 -and
    $planarComparison.candidateTargetP95Milliseconds -eq 4.5) `
    "classified-planar-capture-timing-is-addressable"

$pretarget = New-PretargetCapture
$pretargetFindings = @(Get-PretargetOperationalFindings `
    $manifest $pretarget.Report $pretarget.Health "synthetic-pretarget")
Assert-True (@($pretargetFindings | Where-Object {
            [string]$_.kind -ceq "admitted-pretarget-budget" -and
            [string]$_.name -ceq "DDGI total memory"
        }).Count -eq 1) "known-pretarget-blocker-is-recorded"

$unexpectedBudgetFailed = $false
$unexpectedBudget = New-PretargetCapture "Upload budget" ""
try {
    $null = Get-PretargetOperationalFindings `
        $manifest $unexpectedBudget.Report $unexpectedBudget.Health "unexpected-budget"
} catch {
    $unexpectedBudgetFailed = $_.Exception.Message -match "unapproved operational budget"
}
Assert-True $unexpectedBudgetFailed "unexpected-pretarget-budget-fails-closed"

$unexpectedGiFailed = $false
$unexpectedGi = New-PretargetCapture "DDGI total memory" "NonFiniteTransport"
try {
    $null = Get-PretargetOperationalFindings `
        $manifest $unexpectedGi.Report $unexpectedGi.Health "unexpected-gi"
} catch {
    $unexpectedGiFailed = $_.Exception.Message -match "unexpected GI diagnostic"
}
Assert-True $unexpectedGiFailed "unexpected-pretarget-gi-error-fails-closed"

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
        environment = [pscustomobject]@{
            NJULF_SYNTHETIC_SELECTOR = "baseline"
        }
    }
    candidate = [pscustomobject]@{
        sourceRoot = "C:\baseline"
        commit = "a" * 40
        arguments = @("--synthetic-toggle", "on")
        workloadArguments = [pscustomobject]@{}
        environment = [pscustomobject]@{
            NJULF_SYNTHETIC_SELECTOR = "baseline"
        }
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

$testSpec.candidate.arguments = @("--synthetic-toggle", "on")
$testSpec.candidate.environment.NJULF_SYNTHETIC_SELECTOR = "candidate"
$environmentMismatchFailed = $false
try { Assert-ExperimentSpec $testSpec $manifest } catch {
    $environmentMismatchFailed = $_.Exception.Message -match "A/A mode requires identical"
}
Assert-True $environmentMismatchFailed "aa-spec-rejects-environment-mismatch"

$testSpec.candidate.environment = [pscustomobject]@{
    PATH = "unsupported"
}
$unsafeEnvironmentFailed = $false
try { Assert-ExperimentSpec $testSpec $manifest } catch {
    $unsafeEnvironmentFailed = $_.Exception.Message -match "only uppercase NJULF_"
}
Assert-True $unsafeEnvironmentFailed "spec-rejects-non-njulf-environment"

$environmentLog = Join-Path ([System.IO.Path]::GetTempPath()) (
    "njulf-perf-experiment-environment-{0}.log" -f [Guid]::NewGuid().ToString("N"))
try {
    Invoke-CheckedProcess (Get-Command pwsh).Source @(
        "-NoProfile",
        "-Command",
        'if ($env:NJULF_SYNTHETIC_SELECTOR -cne "candidate") { exit 9 }') `
        (Get-Location).Path $environmentLog 10 "variant-environment-test" `
        ([pscustomobject]@{ NJULF_SYNTHETIC_SELECTOR = "candidate" })
    $environmentLogText = Get-Content -LiteralPath $environmentLog -Raw
    Assert-True ($environmentLogText -match
        'NJULF_SYNTHETIC_SELECTOR=candidate') `
        "variant-environment-reaches-child-and-log"
} finally {
    if (Test-Path -LiteralPath $environmentLog -PathType Leaf) {
        Remove-Item -LiteralPath $environmentLog -Force
    }
}

$allowedExitLog = Join-Path ([System.IO.Path]::GetTempPath()) (
    "njulf-perf-experiment-allowed-exit-{0}.log" -f [Guid]::NewGuid().ToString("N"))
try {
    Invoke-CheckedProcess (Get-Command pwsh).Source @(
        "-NoProfile", "-Command", "exit 1") `
        (Get-Location).Path $allowedExitLog 10 "allowed-pretarget-exit" $null @(0, 1)
    Assert-True ((Get-Content -LiteralPath $allowedExitLog -Raw) -match 'EXIT: 1') `
        "pretarget-process-allows-recorded-health-exit"
} finally {
    if (Test-Path -LiteralPath $allowedExitLog -PathType Leaf) {
        Remove-Item -LiteralPath $allowedExitLog -Force
    }
}

Write-Host "All perf-experiment tests passed."
