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

function Invoke-SyntheticQualitySequencePolicyCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for quality-sequence policy tests."
    }
    foreach ($functionName in @(
            "Assert-FiniteNumber",
            "Get-QualitySequenceTrajectoryFrameCount",
            "Get-QualitySequenceCheckpointIndices",
            "Get-QualitySequenceTemporalPairs",
            "New-QualitySequenceTemporalGates",
            "New-QualitySequenceSpatialEnvelope",
            "Assert-QualitySequenceSpatialEnvelope")) {
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

    $expectedCheckpoints = [ordered]@{
        stationary = @(0)
        "bistro-loop" = @(0, 59, 60, 61, 68, 76, 179, 180, 181, 239)
        "sponza-horizontal" = @(0, 1, 118, 119, 120, 121, 178, 179, 180, 181, 298, 299)
        "sponza-vertical" = @(0, 1, 239, 240, 479, 480, 719, 720, 958, 959)
    }
    foreach ($entry in $expectedCheckpoints.GetEnumerator()) {
        $actual = @(Get-QualitySequenceCheckpointIndices ([string]$entry.Key))
        if (($actual -join ",") -cne (@($entry.Value) -join ",")) {
            throw "Quality checkpoint topology differs for '$($entry.Key)'."
        }
    }
    $expectedPairs = [ordered]@{
        stationary = @()
        "bistro-loop" = @("59->60", "60->61", "179->180", "180->181")
        "sponza-horizontal" = @(
            "0->1", "118->119", "119->120", "120->121",
            "178->179", "179->180", "180->181", "298->299")
        "sponza-vertical" = @(
            "0->1", "239->240", "479->480", "719->720", "958->959")
    }
    foreach ($entry in $expectedPairs.GetEnumerator()) {
        $actual = @(Get-QualitySequenceTemporalPairs ([string]$entry.Key) |
            ForEach-Object {
                "$([int]$_.fromRouteFrameIndex)->$([int]$_.toRouteFrameIndex)"
            })
        if (($actual -join ",") -cne (@($entry.Value) -join ",")) {
            throw "Quality temporal-pair topology differs for '$($entry.Key)'."
        }
    }

    $manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
    $workload = [pscustomobject]@{ trajectory = "bistro-loop" }
    $repeatOne = [pscustomobject]@{
        temporal = @(
            [pscustomobject]@{ relativeResidual = 0.0000001 },
            [pscustomobject]@{ relativeResidual = 0.0010 },
            [pscustomobject]@{ relativeResidual = 0.0020 },
            [pscustomobject]@{ relativeResidual = 0.0024 })
    }
    $repeatTwo = [pscustomobject]@{
        temporal = @(
            [pscustomobject]@{ relativeResidual = 0.0000002 },
            [pscustomobject]@{ relativeResidual = 0.0015 },
            [pscustomobject]@{ relativeResidual = 0.0010 },
            [pscustomobject]@{ relativeResidual = 0.0020 })
    }
    $gates = @(New-QualitySequenceTemporalGates `
        $manifest $workload @($repeatOne, $repeatTwo))
    $expectedGates = @(0.000001, 0.003, 0.004, 0.0048)
    if ($gates.Count -ne $expectedGates.Count) {
        throw "Temporal gate topology differs."
    }
    for ($index = 0; $index -lt $gates.Count; $index++) {
        if ([double]$gates[$index].maximumRelativeResidual -ne
            [double]$expectedGates[$index]) {
            throw "Temporal gate $index did not use max(floor, max(repeats)*2)."
        }
    }
    $repeatTwo.temporal[3].relativeResidual = 0.0026
    $ceilingFailedClosed = $false
    try {
        $null = New-QualitySequenceTemporalGates `
            $manifest $workload @($repeatOne, $repeatTwo)
    } catch {
        $ceilingFailedClosed = $_.Exception.Message -match "hard ceiling"
    }
    if (-not $ceilingFailedClosed) {
        throw "Derived temporal gate above 0.005 did not fail closed."
    }

    function New-SyntheticSpatialMetrics {
        param([double]$Offset)
        $indices = @(Get-QualitySequenceCheckpointIndices "bistro-loop")
        return [pscustomobject]@{
            spatial = @($indices | ForEach-Object {
                [pscustomobject]@{
                    ordinal = [array]::IndexOf($indices, $_)
                    routeFrameIndex = [int]$_
                    relativeRmse = 0.001 + $Offset
                    flipP95 = 0.002 + $Offset
                    rois = @([pscustomobject]@{
                        name = "all"
                        meanLuminanceShift = 0.003 + $Offset
                        p95LuminanceShift = 0.004 + $Offset
                    })
                }
            })
        }
    }
    $spatialOne = New-SyntheticSpatialMetrics 0.0
    $spatialTwo = New-SyntheticSpatialMetrics 0.0001
    $envelope = @(New-QualitySequenceSpatialEnvelope `
        $manifest $workload @($spatialOne, $spatialTwo))
    Assert-QualitySequenceSpatialEnvelope $spatialTwo $envelope "Synthetic spatial"
    $degraded = New-SyntheticSpatialMetrics 0.0002
    $spatialFailedClosed = $false
    try {
        Assert-QualitySequenceSpatialEnvelope $degraded $envelope "Synthetic degraded"
    } catch {
        $spatialFailedClosed = $_.Exception.Message -match "baseline"
    }
    if (-not $spatialFailedClosed) {
        throw "Candidate spatial repeatability regression did not fail closed."
    }
    $nullFailedClosed = $false
    try { $null = Assert-FiniteNumber $null "Synthetic null metric" } catch {
        $nullFailedClosed = $_.Exception.Message -match "absent"
    }
    if (-not $nullFailedClosed) {
        throw "Null quality metric did not fail closed."
    }
    Write-Host "PASS synthetic-quality-sequence-policy"
}

function Invoke-QualityVerifierSmokeCase {
    $buildRoot = Join-Path $solutionRoot "NjulfHelloGame/bin/Release/net10.0"
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot "NjulfHelloGame.dll") -PathType Leaf)) {
        throw "Release build is required before the quality verifier smoke test."
    }
    $caseRoot = Join-Path $testRoot "quality-verifier"
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $pfmPath = Join-Path $caseRoot "one.pfm"
    $header = [System.Text.Encoding]::ASCII.GetBytes(
        "PF`n# NJULF_LINEAR_FLOAT_IMAGE_VERSION=1 COLOR_SPACE=linear-scRGB LOGICAL_ORIGIN=top-left`n1 1`n-1.0`n")
    $payload = [byte[]]::new(12)
    [System.Buffer]::BlockCopy([single[]]@(1, 1, 1), 0, $payload, 0, 12)
    $pfmBytes = [byte[]]::new($header.Length + $payload.Length)
    [System.Buffer]::BlockCopy($header, 0, $pfmBytes, 0, $header.Length)
    [System.Buffer]::BlockCopy($payload, 0, $pfmBytes, $header.Length, $payload.Length)
    [System.IO.File]::WriteAllBytes($pfmPath, $pfmBytes)
    $qualityPath = Join-Path $caseRoot "quality.json"
    [System.IO.File]::WriteAllText(
        $qualityPath,
        '{"schema":"njulf-benchmark-hdr-quality/v1","width":1,"height":1,"rois":[{"name":"all","x":0,"y":0,"width":1,"height":1,"maximumMeanLuminanceShift":0.01,"maximumP95LuminanceShift":0.01}]}',
        [System.Text.UTF8Encoding]::new($false))
    $pfmSha = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($pfmBytes)).ToLowerInvariant()
    $qualitySha = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.IO.File]::ReadAllBytes($qualityPath))).ToLowerInvariant()
    $operations = @(
        [ordered]@{
            id = "spatial-0"; kind = "spatial"
            referencePath = $pfmPath; referenceSha256 = $pfmSha
            candidatePath = $pfmPath; candidateSha256 = $pfmSha
            maximumRelativeRmse = 0.005; maximumFlipP95 = 0.02
            qualityContractPath = $qualityPath
            qualityContractSha256 = $qualitySha
        },
        [ordered]@{
            id = "temporal-0"; kind = "temporal"
            referenceFromPath = $pfmPath; referenceFromSha256 = $pfmSha
            referenceToPath = $pfmPath; referenceToSha256 = $pfmSha
            candidateFromPath = $pfmPath; candidateFromSha256 = $pfmSha
            candidateToPath = $pfmPath; candidateToSha256 = $pfmSha
        })
    $request = [ordered]@{
        schema = "njulf-perf-quality-verify-request/v1"
        operations = $operations
    } | ConvertTo-Json -Depth 10 -Compress
    $helperBytes = [System.IO.File]::ReadAllBytes(
        (Join-Path $PSScriptRoot "perf-quality-verify.ps1"))
    $helperText = [System.Text.UTF8Encoding]::new($false, $true).GetString(
        $helperBytes)
    $encoded = [Convert]::ToBase64String(
        [System.Text.Encoding]::Unicode.GetBytes($helperText))
    $savedBuildRoot = [string]$env:NJULF_PERF_VERIFY_BUILD_ROOT
    try {
        $env:NJULF_PERF_VERIFY_BUILD_ROOT = $buildRoot
        $output = $request | & (Join-Path $PSHOME "pwsh.exe") `
            -NoProfile -NonInteractive -EncodedCommand $encoded
        if ($LASTEXITCODE -ne 0) {
            throw "Quality verifier smoke exited $LASTEXITCODE."
        }
        $result = $output | ConvertFrom-Json
        if ([string]$result.schema -cne "njulf-perf-quality-verify-result/v1" -or
            @($result.results).Count -ne 2 -or
            [double]$result.results[0].value.FlipP95 -ne 0.0 -or
            [double]$result.results[1].value.relativeResidual -ne 0.0) {
            throw "Quality verifier smoke returned unexpected metrics."
        }
        $operations[0].referenceSha256 = "0" * 64
        $badRequest = [ordered]@{
            schema = "njulf-perf-quality-verify-request/v1"
            operations = @($operations[0])
        } | ConvertTo-Json -Depth 10 -Compress
        $badOutput = $badRequest | & (Join-Path $PSHOME "pwsh.exe") `
            -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1
        if ($LASTEXITCODE -eq 0 -or
            ($badOutput -join "`n") -notmatch "hash differs") {
            throw "Quality verifier hash mismatch did not fail closed."
        }
    } finally {
        $env:NJULF_PERF_VERIFY_BUILD_ROOT = $savedBuildRoot
    }
    Write-Host "PASS quality-verifier-spatial-temporal-pipe"
}

try {
    Invoke-SyntheticHealthReportCase
    Invoke-SyntheticAcceptanceRefCase
    Invoke-SyntheticComparisonContractCase
    Invoke-SyntheticQualitySequencePolicyCase
    Invoke-QualityVerifierSmokeCase
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
    Invoke-ManifestCase "wrong-quality-repeat-count" {
        param($manifest)
        $manifest.qualitySequence.baselineRepeatCount = 3
    } $false
    Invoke-ManifestCase "wrong-quality-drain" {
        param($manifest)
        $manifest.qualitySequence.maximumReadbackDrainFrames = 239
    } $false
    Invoke-ManifestCase "wrong-quality-floor" {
        param($manifest)
        $manifest.qualitySequence.temporalResidualFloor = 0.0
    } $false
    Invoke-ManifestCase "wrong-quality-multiplier" {
        param($manifest)
        $manifest.qualitySequence.temporalResidualMultiplier = 3.0
    } $false
    Invoke-ManifestCase "wrong-quality-ceiling" {
        param($manifest)
        $manifest.qualitySequence.temporalResidualHardCeiling = 0.02
    } $false
    Invoke-ManifestCase "missing-quality-verifier-protection" {
        param($manifest)
        $manifest.protectedPaths = @($manifest.protectedPaths | Where-Object {
            [string]$_ -ne "tools/perf-quality-verify.ps1"
        })
    } $false
    Invoke-ManifestCase "reserved-health-report" {
        param($manifest)
        $manifest.workloads[0] | Add-Member `
            -NotePropertyName arguments `
            -NotePropertyValue @("--health-report", "forged.json")
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
