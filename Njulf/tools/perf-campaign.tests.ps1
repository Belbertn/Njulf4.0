[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$solutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$driver = Join-Path $PSScriptRoot "perf-campaign.ps1"
$wrapper = Join-Path $PSScriptRoot "perf-loop.ps1"
$sourceManifest = Join-Path $PSScriptRoot "perf-campaign.bistro-sponza.json"
$testRoot = Join-Path $solutionRoot (
    ".perf-loop-runs/campaign-driver-tests/{0}" -f [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

function Invoke-ManifestCase {
    param(
        [string]$Name,
        [scriptblock]$Mutate,
        [bool]$ExpectSuccess)
    $manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
    if ($null -ne $Mutate) { & $Mutate $manifest }
    $manifestPath = Join-Path $testRoot "$Name.json"
    $manifest | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8
    $runPath = Join-Path $testRoot "run-$Name"
    $output = & pwsh -NoProfile -NonInteractive -File $driver `
        -ManifestPath $manifestPath `
        -RunDirectory $runPath `
        -ValidateOnly 2>&1
    $succeeded = $LASTEXITCODE -eq 0
    if ($succeeded -ne $ExpectSuccess) {
        throw "Case '$Name' success=$succeeded, expected $ExpectSuccess.`n$($output -join "`n")"
    }
    if (Test-Path -LiteralPath $runPath) {
        throw "ValidateOnly case '$Name' created its run directory."
    }
    Write-Host "PASS $Name"
}

function Invoke-SyntheticHealthReportCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the synthetic health test."
    }
    $requiredFunctions = @(
        "Get-PropertyValue",
        "Get-Sha256",
        "Get-RuntimeExecutableBundleHash",
        "Test-CanonicalIdentityText",
        "Assert-HealthReport")
    foreach ($functionName in $requiredFunctions) {
        $definition = @($driverAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
        }, $true))[0]
        if ($null -eq $definition) {
            throw "Campaign driver lacks '$functionName'."
        }
        . ([scriptblock]::Create($definition.Extent.Text))
    }

    $commit = "c" * 40
    $executableHash = "sha256:" + ("a" * 64)
    $shaderHash = "sha256:" + ("b" * 64)
    $benchmarkSettings = "d" * 64
    $healthSettings = "e" * 64
    $captureRun = [ordered]@{
        Commit = $commit
        DirtyWorktreeState = "clean"
        ExecutableHash = $executableHash
        ShaderBundleHash = $shaderHash
        ApplicationVersion = "1.0.0+synthetic"
        SettingsSchemaVersion = 1
        SceneKind = "Bistro"
        Scenario = "Normal"
    }
    $report = [pscustomobject]@{
        ProducerIdentity = [pscustomobject]@{
            Schema = "material-gi-producer-identity/v1"
            BuildCommit = $commit
            ShaderFingerprint = "b" * 64
            SettingsFingerprint = $benchmarkSettings
            GpuName = "Synthetic GPU"
            DriverVersion = "1.2.3"
            QualityTier = "StressUnlimited"
        }
        LastDiagnostics = [pscustomobject]@{
            CaptureRun = [pscustomobject]$captureRun
        }
    }
    $healthJson = [ordered]@{
        kind = "renderer-health"
        schema = "renderer-health/v2"
        producerIdentity = [ordered]@{
            schema = "material-gi-producer-identity/v1"
            buildCommit = $commit
            shaderFingerprint = "b" * 64
            settingsFingerprint = $healthSettings
            sourceSettingsFingerprints = @($healthSettings)
            gpuName = "Synthetic GPU"
            driverVersion = "1.2.3"
            qualityTier = ""
        }
        status = "passed"
        options = [ordered]@{
            Benchmark = [ordered]@{
                WarmupFrameCount = 480
                MeasureFrameCount = 240
                CapturePairId = "synthetic-pair"
            }
        }
        diagnostics = [ordered]@{
            CaptureRenderWidth = 1920
            CaptureRenderHeight = 1080
            CaptureRun = $captureRun
        }
    } | ConvertTo-Json -Depth 8
    $health = $healthJson | ConvertFrom-Json
    $workload = [pscustomobject]@{
        warmupFrames = 480
        measureFrames = 240
    }
    $build = [pscustomobject]@{
        RuntimeExecutableBundleHash = $executableHash
    }
    Assert-HealthReport `
        $null $workload $health $report $build $commit `
        "synthetic-pair" "Synthetic health"

    $health.status = "failed"
    $health | Add-Member -NotePropertyName failure -NotePropertyValue "synthetic failure"
    $failedClosed = $false
    try {
        Assert-HealthReport `
            $null $workload $health $report $build $commit `
            "synthetic-pair" "Synthetic health"
    } catch {
        $failedClosed = $_.Exception.Message -match "synthetic failure"
    }
    if (-not $failedClosed) {
        throw "Synthetic failed health report did not fail closed."
    }
    Write-Host "PASS synthetic-health-contract"

    $bundleRoot = Join-Path $testRoot "runtime-bundle"
    New-Item -ItemType Directory -Path $bundleRoot | Out-Null
    $bundleFiles = [ordered]@{
        "NjulfHelloGame.exe" = "apphost"
        "Njulf.Rendering.dll" = "renderer"
        "Njulf.Engine.dll" = "engine"
        "Other.dll" = "excluded"
    }
    foreach ($entry in $bundleFiles.GetEnumerator()) {
        [System.IO.File]::WriteAllText(
            (Join-Path $bundleRoot ([string]$entry.Key)),
            [string]$entry.Value,
            [System.Text.UTF8Encoding]::new($false))
    }
    $manifestText = [System.Text.StringBuilder]::new()
    foreach ($name in @(
            "Njulf.Engine.dll",
            "Njulf.Rendering.dll",
            "NjulfHelloGame.exe")) {
        [void]$manifestText.Append($name)
        [void]$manifestText.Append(":sha256:")
        [void]$manifestText.Append((Get-Sha256 (Join-Path $bundleRoot $name)))
        [void]$manifestText.Append("`n")
    }
    $expectedBundleHash = "sha256:" + [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes(
                $manifestText.ToString()))).ToLowerInvariant()
    $actualBundleHash = Get-RuntimeExecutableBundleHash (
        Join-Path $bundleRoot "NjulfHelloGame.exe")
    if ($actualBundleHash -ne $expectedBundleHash) {
        throw "Runtime executable bundle hash '$actualBundleHash' does not match '$expectedBundleHash'."
    }
    Write-Host "PASS synthetic-runtime-bundle-hash"
}

function Invoke-SyntheticAcceptanceRefCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    $requiredFunctions = @(
        "Assert-Text",
        "Invoke-Git",
        "Get-GitText",
        "Invoke-GitUpdateRefTransaction",
        "Assert-CampaignWorktreeRoot",
        "Get-AcceptanceRefPrefix",
        "Get-AcceptanceRefName",
        "Get-AcceptanceRefRawEntries",
        "Get-AcceptanceRefSnapshot",
        "Assert-AcceptanceRefSnapshot",
        "Restore-AcceptanceRefSnapshot",
        "Publish-AcceptanceEvidence")
    foreach ($functionName in $requiredFunctions) {
        $definition = @($driverAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
        }, $true))[0]
        if ($null -eq $definition) {
            throw "Campaign driver lacks '$functionName'."
        }
        . ([scriptblock]::Create($definition.Extent.Text))
    }

    $repository = Join-Path $testRoot "acceptance-repository"
    New-Item -ItemType Directory -Path $repository | Out-Null
    $originalSolutionRoot = $script:SolutionRoot
    $originalRepoRootVariable = Get-Variable `
        -Name RepoRoot -Scope Script -ErrorAction SilentlyContinue
    try {
        & git -C $repository init --quiet
        if ($LASTEXITCODE -ne 0) { throw "Could not initialize acceptance test repository." }
        & git -C $repository config user.email "perf-campaign@example.invalid"
        & git -C $repository config user.name "Perf Campaign Test"
        [System.IO.File]::WriteAllText(
            (Join-Path $repository "seed.txt"),
            "seed",
            [System.Text.UTF8Encoding]::new($false))
        & git -C $repository add -- seed.txt
        & git -C $repository commit --quiet -m "seed"
        if ($LASTEXITCODE -ne 0) { throw "Could not commit acceptance test seed." }

        $script:SolutionRoot = [System.IO.Path]::GetFullPath($repository)
        $script:RepoRoot = $script:SolutionRoot
        $manifest = [pscustomobject]@{ campaignId = "synthetic-acceptance" }
        $emptySnapshot = Get-AcceptanceRefSnapshot $manifest
        if ($emptySnapshot.Count -ne 0) {
            throw "Synthetic acceptance namespace was not empty."
        }
        $decisionPath = Join-Path $repository "decision.json"
        [System.IO.File]::WriteAllText(
            $decisionPath,
            '{"decision":"keep"}',
            [System.Text.UTF8Encoding]::new($false))
        $commit = Get-GitText @("rev-parse", "HEAD")
        $decisionBlob = Get-GitText @(
            "hash-object", "-w", "--", $decisionPath)
        $blob = Publish-AcceptanceEvidence `
            $manifest $decisionPath $commit $decisionBlob $emptySnapshot
        $acceptedSnapshot = Get-AcceptanceRefSnapshot $manifest
        Assert-AcceptanceRefSnapshot `
            $manifest $acceptedSnapshot "Synthetic acceptance"
        if ($acceptedSnapshot.Count -ne 1 -or
            $acceptedSnapshot.Values -notcontains $blob) {
            throw "Synthetic acceptance evidence was not published."
        }
        $maliciousRef = (Get-AcceptanceRefPrefix $manifest) + ("f" * 40)
        $null = Invoke-Git @("update-ref", $maliciousRef, $blob)
        $detectedMutation = $false
        try {
            Assert-AcceptanceRefSnapshot `
                $manifest $acceptedSnapshot "Synthetic mutation"
        } catch {
            $detectedMutation = $true
        }
        if (-not $detectedMutation) {
            throw "Acceptance namespace mutation was not detected."
        }
        Restore-AcceptanceRefSnapshot $manifest $acceptedSnapshot
        $acceptedRef = [string]@($acceptedSnapshot.Keys)[0]
        $symrefTarget = "refs/perf-campaign/synthetic-symref-target"
        $null = Invoke-Git @("update-ref", $symrefTarget, $blob)
        $null = Invoke-Git @("symbolic-ref", $acceptedRef, $symrefTarget)
        $detectedSymref = $false
        try {
            $null = Get-AcceptanceRefSnapshot $manifest
        } catch {
            $detectedSymref = $_.Exception.Message -match "forbidden symref"
        }
        if (-not $detectedSymref) {
            throw "Acceptance symref substitution was not detected."
        }
        Restore-AcceptanceRefSnapshot $manifest $acceptedSnapshot
        $null = Invoke-Git @("update-ref", "-d", $symrefTarget, $blob)
        $danglingTarget = "refs/perf-campaign/missing-symref-target"
        $null = Invoke-Git @("symbolic-ref", $acceptedRef, $danglingTarget)
        $detectedDanglingSymref = $false
        try {
            $null = Get-AcceptanceRefSnapshot $manifest
        } catch {
            $detectedDanglingSymref = $_.Exception.Message -match
                "forbidden symref"
        }
        if (-not $detectedDanglingSymref) {
            throw "Dangling acceptance symref was not enumerated and rejected."
        }
        Restore-AcceptanceRefSnapshot $manifest $acceptedSnapshot
        Restore-AcceptanceRefSnapshot $manifest $emptySnapshot
        if ((Get-AcceptanceRefSnapshot $manifest).Count -ne 0) {
            throw "Acceptance namespace rollback did not restore the empty snapshot."
        }
        Write-Host "PASS synthetic-acceptance-ref"
    } finally {
        $script:SolutionRoot = $originalSolutionRoot
        if ($null -eq $originalRepoRootVariable) {
            Remove-Variable -Name RepoRoot -Scope Script -ErrorAction SilentlyContinue
        } else {
            $script:RepoRoot = $originalRepoRootVariable.Value
        }
    }
}

function Invoke-SyntheticComparisonContractCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    $requiredFunctions = @(
        "Get-PropertyValue",
        "Get-Median",
        "Get-Timing",
        "Get-PassTiming",
        "Get-ImprovementPercent",
        "Get-BootstrapLowerBound",
        "Assert-TimingStats",
        "Compare-WorkloadCaptures",
        "Get-ConfigurationWorkloadSelection")
    foreach ($functionName in $requiredFunctions) {
        $definition = @($driverAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
        }, $true))[0]
        if ($null -eq $definition) {
            throw "Campaign driver lacks '$functionName'."
        }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    function Assert-CrossBuildIdentity { param($a, $b, $c) }

    function New-SyntheticTimingReport {
        param([bool]$IncludeTargetPass)
        $timing = [pscustomobject]@{
            P50Milliseconds = 10.0
            P95Milliseconds = 11.0
            P99Milliseconds = 12.0
        }
        $passes = if ($IncludeTargetPass) {
            @([pscustomobject]@{
                Name = "SyntheticPass"
                P95Milliseconds = 3.0
            })
        } else {
            @()
        }
        return [pscustomobject]@{
            CpuFrameMilliseconds = $timing
            GpuFrameMilliseconds = $timing
            GpuPasses = $passes
            CpuStages = @()
        }
    }

    $comparisonManifest = [pscustomobject]@{
        acceptance = [pscustomobject]@{
            bootstrapSamples = 10000
            bootstrapConfidence = 0.95
            maximumRegressionPercent = 1.0
            minimumFrameImprovementPercent = 1.0
            minimumFrameImprovementMilliseconds = 0.1
            minimumPassImprovementPercent = 5.0
            minimumPassImprovementMilliseconds = 0.05
        }
    }
    $comparisonWorkload = [pscustomobject]@{
        id = "synthetic-pass"
        targetPass = "SyntheticPass"
    }
    $missingPassFailedClosed = $false
    try {
        $null = Compare-WorkloadCaptures `
            $comparisonManifest $comparisonWorkload `
            @(
                (New-SyntheticTimingReport $true),
                (New-SyntheticTimingReport $true)) `
            @(
                (New-SyntheticTimingReport $true),
                (New-SyntheticTimingReport $false)) `
            ([double[]]@(0.1, 0.1)) $true
    } catch {
        $missingPassFailedClosed = $_.Exception.Message -match
            "finite positive 'SyntheticPass' sample in every ABBA slot"
    }
    if (-not $missingPassFailedClosed) {
        throw "Missing target-pass slot did not fail closed."
    }
    Write-Host "PASS synthetic-target-pass-completeness"

    $invalidTiming = [pscustomobject]@{
        Count = 240
        AverageMilliseconds = 10.0
        MinMilliseconds = 1.0
        MaxMilliseconds = 12.0
        MedianMilliseconds = 8.0
        P50Milliseconds = 8.0
        P95Milliseconds = 13.0
        P99Milliseconds = 11.0
    }
    $invalidTimingFailedClosed = $false
    try {
        Assert-TimingStats $invalidTiming 240 $true "Synthetic frame"
    } catch {
        $invalidTimingFailedClosed = $_.Exception.Message -match
            "timing percentiles are incoherent"
    }
    if (-not $invalidTimingFailedClosed) {
        throw "Incoherent timing statistics did not fail closed."
    }
    $nonfinitePairFailedClosed = $false
    try {
        $null = Get-BootstrapLowerBound `
            ([double[]]@(0.1, [double]::NaN)) 100 0.95
    } catch {
        $nonfinitePairFailedClosed = $_.Exception.Message -match
            "must all be finite"
    }
    if (-not $nonfinitePairFailedClosed) {
        throw "Non-finite paired difference did not fail closed."
    }
    Write-Host "PASS synthetic-timing-domain"

    $manifest = Get-Content -LiteralPath $sourceManifest -Raw |
        ConvertFrom-Json
    $target = @($manifest.workloads | Where-Object {
        [string]$_.id -eq "bistro-forward-gi-disabled"
    })[0]
    $selectedIds = @(Get-ConfigurationWorkloadSelection `
        $manifest @($target) $false | ForEach-Object { [string]$_.id })
    $expectedIds = @(
        "bistro-forward-gi-disabled",
        "bistro-forward-gi-enabled",
        "bistro-forward-gi-exact",
        "bistro-stationary",
        "bistro-motion",
        "bistro-motion-relight",
        "sponza-low-stationary",
        "sponza-high-stationary",
        "sponza-horizontal-motion",
        "sponza-vertical-motion")
    if (($selectedIds -join "`n") -cne ($expectedIds -join "`n")) {
        throw "Screen workload selection does not match target+isolation+qualification order."
    }
    Write-Host "PASS synthetic-screen-workload-order"
}

try {
    Invoke-SyntheticHealthReportCase
    Invoke-SyntheticAcceptanceRefCase
    Invoke-SyntheticComparisonContractCase
    Invoke-ManifestCase "valid" {} $true
    Invoke-ManifestCase "abba-too-small" {
        param($manifest)
        $manifest.capture.abbaCycles = 2
    } $false
    Invoke-ManifestCase "zero-benchmark-timeout" {
        param($manifest)
        $manifest.capture.benchmarkTimeoutSeconds = 0
    } $false
    Invoke-ManifestCase "unbounded-trial-timeout" {
        param($manifest)
        $manifest.capture.trialTimeoutSeconds = 86401
    } $false
    Invoke-ManifestCase "non-release-iteration" {
        param($manifest)
        $manifest.iterationConfiguration = "ShippingPerformance"
    } $false
    Invoke-ManifestCase "duplicate-final-config" {
        param($manifest)
        $manifest.finalConfigurations = @("Release", "Release")
    } $false
    Invoke-ManifestCase "missing-approved-workload" {
        param($manifest)
        $manifest.workloads = @($manifest.workloads | Select-Object -Skip 1)
    } $false
    Invoke-ManifestCase "reserved-argument" {
        param($manifest)
        $manifest.workloads[0] | Add-Member `
            -NotePropertyName arguments `
            -NotePropertyValue @("--benchmark-measure-frames", "1")
    } $false
    Invoke-ManifestCase "partial-roi" {
        param($manifest)
        $manifest.workloads[0].qualityRois[0].width = 960
    } $false
    Invoke-ManifestCase "trajectory-topology-change" {
        param($manifest)
        $manifest.workloads[1].trajectory = "bistro-presentation"
    } $false
    Invoke-ManifestCase "missing-health-producer-protection" {
        param($manifest)
        $manifest.protectedPaths = @($manifest.protectedPaths | Where-Object {
            [string]$_ -ne "NjulfHelloGame/SampleHealthReportWriter.cs"
        })
    } $false
    $qualificationRunPath = Join-Path $testRoot "qualification-target-run"
    $qualificationOutput = & pwsh -NoProfile -NonInteractive -File $driver `
        -ManifestPath $sourceManifest `
        -RunDirectory $qualificationRunPath `
        -TargetWorkloadId "bistro-stationary" `
        -ValidateOnly 2>&1
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $qualificationRunPath)) {
        throw "Qualification target selection did not fail before campaign state mutation.`n$($qualificationOutput -join "`n")"
    }
    if (($qualificationOutput -join "`n") -notmatch
        "cannot be selected as a target hypothesis") {
        throw "Qualification target selection failed for the wrong reason.`n$($qualificationOutput -join "`n")"
    }
    Write-Host "PASS qualification-target-rejected"
    $wrapperRunPath = Join-Path $testRoot "wrapper-run"
    $wrapperOutput = & pwsh -NoProfile -NonInteractive -File $wrapper `
        -CampaignManifestPath $sourceManifest `
        -CampaignRunDirectory $wrapperRunPath `
        -ValidateCampaign 2>&1
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $wrapperRunPath)) {
        throw "perf-loop campaign dispatch failed or mutated ValidateOnly state.`n$($wrapperOutput -join "`n")"
    }
    Write-Host "PASS perf-loop-dispatch"
} finally {
    $fullTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $fullParent = [System.IO.Path]::GetFullPath(
        (Join-Path $solutionRoot ".perf-loop-runs/campaign-driver-tests"))
    $relative = [System.IO.Path]::GetRelativePath($fullParent, $fullTestRoot)
    if ($relative -ne ".." -and
        -not $relative.StartsWith(
            "..$([System.IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $fullTestRoot -Recurse -Force
    }
}
