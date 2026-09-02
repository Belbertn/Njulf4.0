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
    $manifest = Get-Content -LiteralPath $sourceManifest -Raw |
        ConvertFrom-Json -DateKind String
    if ($null -ne $Mutate) { & $Mutate $manifest }
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for manifest case '$Name'."
    }
    foreach ($definition in @($driverAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
            }, $true))) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $script:SolutionRoot = $solutionRoot
    $script:SolutionRelativePath = "Njulf"
    $script:ManifestFile = $sourceManifest
    $script:CampaignManifestSha256 = Get-Sha256 $sourceManifest
    $succeeded = $true
    $failure = ""
    try {
        Assert-CampaignManifest $manifest
    } catch {
        $succeeded = $false
        $failure = $_.Exception.Message
    }
    if ($succeeded -ne $ExpectSuccess) {
        throw "Case '$Name' success=$succeeded, expected $ExpectSuccess. $failure"
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
        "Test-WorkloadUsesSponzaAnimation",
        "Get-WorkloadSponzaFixtureValue",
        "Get-Sha256",
        "Get-RuntimeExecutableBundleHash",
        "Test-CanonicalIdentityText",
        "Assert-BenchmarkReport",
        "Assert-PretargetReferenceHealth",
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
        Kind = "njulf-renderer-benchmark"
        Schema = "njulf-renderer-benchmark/v5"
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
        schema = "renderer-health/v3"
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
    $health = $healthJson | ConvertFrom-Json -DateKind String
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

    $legacyBenchmarkFailedClosed = $false
    try {
        Assert-BenchmarkReport `
            $null $null `
            ([pscustomobject]@{
                Kind = "njulf-renderer-benchmark"
                Schema = "njulf-renderer-benchmark/v3"
            }) `
            "Release" "Synthetic legacy benchmark" $true `
            "synthetic-pair" $commit $null $null ""
    } catch {
        $legacyBenchmarkFailedClosed = $_.Exception.Message -match
            "unexpected report kind/schema"
    }
    if (-not $legacyBenchmarkFailedClosed) {
        throw "Legacy benchmark schema did not fail closed against v4."
    }

    $health.schema = "renderer-health/v2"
    $legacyHealthFailedClosed = $false
    try {
        Assert-HealthReport `
            $null $workload $health $report $build $commit `
            "synthetic-pair" "Synthetic legacy health"
    } catch {
        $legacyHealthFailedClosed = $_.Exception.Message -match
            "unexpected health-report contract"
    }
    if (-not $legacyHealthFailedClosed) {
        throw "Legacy health schema did not fail closed against v3."
    }
    $health.schema = "renderer-health/v3"

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

    $report | Add-Member -NotePropertyName Options -NotePropertyValue ([pscustomobject]@{
        RequireRealtime1080p60Target = $false
    })
    $report | Add-Member -NotePropertyName DdgiProductionGate -NotePropertyValue $null
    $report | Add-Member -NotePropertyName BudgetMetrics -NotePropertyValue @(
        [pscustomobject]@{
            Name = "DDGI total memory"
            Status = 3
            Value = 210287208
            Unit = "bytes"
            FailureThreshold = 201326592
        })
    $health.failure = "Benchmark exceeded 'DDGI total memory': 210287208 bytes > 201326592 bytes."
    $health | Add-Member -NotePropertyName validationWarningCount -NotePropertyValue 0
    $health | Add-Member -NotePropertyName validationErrorCount -NotePropertyValue 0
    $health | Add-Member -NotePropertyName operations -NotePropertyValue @()
    $health.diagnostics | Add-Member -NotePropertyName ValidationWarningMessageCount -NotePropertyValue 0
    $health.diagnostics | Add-Member -NotePropertyName ValidationErrorMessageCount -NotePropertyValue 0
    $health.diagnostics | Add-Member -NotePropertyName GiWarnings -NotePropertyValue @(
        [pscustomobject]@{ Severity = "Error"; Code = "GiBudgetOverrun" })
    $gateFailure = [pscustomobject]@{
        Name = "budget-metrics-within-gate"
        Passed = $false
        Detail = "overBudget=DDGI total memory"
    }
    $report.DdgiProductionGate = [pscustomobject]@{
        Passed = $false
        Criteria = @($gateFailure)
        Failures = @($gateFailure)
    }
    Assert-HealthReport `
        $null $workload $health $report $build $commit `
        "synthetic-pair" "Synthetic pre-target reference" $true

    $gateFailure.Detail = "overBudget=Upload budget"
    $unexpectedGateFailureFailed = $false
    try {
        Assert-HealthReport `
            $null $workload $health $report $build $commit `
            "synthetic-pair" "Synthetic invalid gate reference" $true
    } catch {
        $unexpectedGateFailureFailed = $_.Exception.Message -match
            "production gate outside the admitted reference budgets"
    }
    if (-not $unexpectedGateFailureFailed) {
        throw "Reference initialization admitted a mismatched production-gate failure."
    }
    $report.DdgiProductionGate = $null

    $report.BudgetMetrics[0].Name = "Upload budget"
    $health.failure = "Benchmark exceeded 'Upload budget': 210287208 bytes > 201326592 bytes."
    $unexpectedReferenceBudgetFailed = $false
    try {
        Assert-HealthReport `
            $null $workload $health $report $build $commit `
            "synthetic-pair" "Synthetic invalid reference" $true
    } catch {
        $unexpectedReferenceBudgetFailed = $_.Exception.Message -match
            "unapproved reference budget"
    }
    if (-not $unexpectedReferenceBudgetFailed) {
        throw "Reference initialization admitted an unexpected budget failure."
    }
    Write-Host "PASS synthetic-benchmark-v5-health-v3-contract"

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

function Invoke-SyntheticQualityHealthBudgetCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the quality health test."
    }
    foreach ($functionName in @(
            "Get-PropertyValue",
            "Test-WorkloadUsesSponzaAnimation",
            "Get-WorkloadSponzaFixtureValue",
            "Get-WorkloadSponzaFixtureName",
            "Get-ExpectedQualityTier",
            "Test-Sha256Identity",
            "Test-CanonicalIdentityText",
            "Get-QualitySequenceRoleName",
            "Get-QualitySequenceRoleValue",
            "Assert-QualitySequenceCaptureRun",
            "Assert-PretargetQualityReferenceHealth",
            "Assert-QualitySequenceHealthReport")) {
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

    $manifest = Get-Content -LiteralPath $sourceManifest -Raw |
        ConvertFrom-Json -DateKind String
    $workload = @($manifest.workloads)[0]
    $commit = "a" * 40
    $executableHash = "sha256:" + ("b" * 64)
    $shaderHash = "sha256:" + ("c" * 64)
    $reportSettingsHash = "d" * 64
    $healthSettingsHash = "f" * 64
    $activationFingerprint = "sha256:" + ("e" * 64)
    $reportPath = [System.IO.Path]::GetFullPath(
        (Join-Path $testRoot "quality-health-report.json"))
    $outputDirectory = [System.IO.Path]::GetFullPath(
        (Join-Path $testRoot "quality-health-checkpoints"))
    $sequenceId = "synthetic-quality-health"
    $captureRun = [pscustomobject]@{
        SceneKind = "Bistro"
        Scenario = "Normal"
        BuildConfiguration = "Release; validation=Off"
        ApplicationVersion = "1.0.0+synthetic"
        Commit = $commit
        ShaderBundleHash = $shaderHash
        SettingsSchemaVersion = 1
        ExecutableHash = $executableHash
        DirtyWorktreeState = "clean"
    }
    $reportProducer = [pscustomobject]@{
        Schema = "material-gi-producer-identity/v1"
        BuildCommit = $commit
        ShaderFingerprint = "c" * 64
        SettingsFingerprint = $reportSettingsHash
        SourceSettingsFingerprints = @($reportSettingsHash)
        GpuName = "Synthetic GPU"
        DriverVersion = "1.2.3"
        QualityTier = "StressUnlimited"
    }
    $report = [pscustomobject]@{
        ActivationFingerprint = $activationFingerprint
        CaptureRun = $captureRun
        ProducerIdentity = $reportProducer
    }
    $qualityOptions = [pscustomobject]@{
        Enabled = $true
        Role = "Canonical"
        SequenceId = $sequenceId
        WarmupFrameCount = [int]$workload.warmupFrames
        MaximumAdditionalSettlingFrameCount =
            [int]$manifest.capture.maximumSettlingFrames
        MaximumReadbackDrainFrameCount =
            [int]$manifest.qualitySequence.maximumReadbackDrainFrames
        ReportPath = $reportPath
        OutputDirectory = $outputDirectory
        ReferenceContractPath = ""
        HdrQualityContractPath = ""
        BudgetProfileOverride = "StressUnlimited"
        CaptureVariant = [string]$workload.captureVariant
        SponzaFixtureMode = "Architecture"
        Activation = [string]$workload.activation
        ActivationFingerprint = $activationFingerprint
        SceneKind = "Bistro"
        Scenario = "Normal"
        Trajectory = "BistroPresentation"
        TrajectoryBistroVariant = "Presentation"
        HdrMaximumRelativeRmse = [double]$manifest.quality.maximumRelativeRmse
        HdrMaximumFlipP95 = [double]$manifest.quality.maximumFlipP95
    }
    $health = [pscustomobject]@{
        kind = "renderer-health"
        schema = "renderer-health/v3"
        status = "passed"
        options = [pscustomobject]@{
            BenchmarkQualitySequence = $qualityOptions
        }
        diagnostics = [pscustomobject]@{
            CaptureRun = $captureRun
            CaptureRenderWidth = 1920
            CaptureRenderHeight = 1080
        }
        producerIdentity = [pscustomobject]@{
            schema = "material-gi-producer-identity/v1"
            buildCommit = $commit
            shaderFingerprint = "c" * 64
            settingsFingerprint = $healthSettingsHash
            sourceSettingsFingerprints = @($healthSettingsHash)
            gpuName = "Synthetic GPU"
            driverVersion = "1.2.3"
            qualityTier = ""
        }
    }
    $build = [pscustomobject]@{
        RuntimeExecutableBundleHash = $executableHash
    }

    Assert-QualitySequenceHealthReport `
        $manifest $workload $health $report $build "Release" "canonical" `
        $sequenceId $commit $reportPath $outputDirectory "" "" `
        "Synthetic quality health"

    $qualityOptions.SponzaFixtureMode = "architecture"
    $wrongFixtureNameFailedClosed = $false
    try {
        Assert-QualitySequenceHealthReport `
            $manifest $workload $health $report $build "Release" "canonical" `
            $sequenceId $commit $reportPath $outputDirectory "" "" `
            "Synthetic quality health"
    } catch {
        $wrongFixtureNameFailedClosed = $_.Exception.Message -match
            "health options differ"
    }
    if (-not $wrongFixtureNameFailedClosed) {
        throw "Quality health accepted a noncanonical Sponza fixture enum name."
    }
    $qualityOptions.SponzaFixtureMode = "Architecture"

    $qualityOptions.BudgetProfileOverride = "Stress"
    $wrongBudgetFailedClosed = $false
    try {
        Assert-QualitySequenceHealthReport `
            $manifest $workload $health $report $build "Release" "canonical" `
            $sequenceId $commit $reportPath $outputDirectory "" "" `
            "Synthetic quality health"
    } catch {
        $wrongBudgetFailedClosed = $_.Exception.Message -match
            "health options differ"
    }
    if (-not $wrongBudgetFailedClosed) {
        throw "Quality health accepted a noncanonical stress budget enum."
    }
    $qualityOptions.BudgetProfileOverride = "StressUnlimited"
    $health.producerIdentity.sourceSettingsFingerprints = @($reportSettingsHash)
    $wrongHealthSourceFailedClosed = $false
    try {
        Assert-QualitySequenceHealthReport `
            $manifest $workload $health $report $build "Release" "canonical" `
            $sequenceId $commit $reportPath $outputDirectory "" "" `
            "Synthetic quality health"
    } catch {
        $wrongHealthSourceFailedClosed = $_.Exception.Message -match
            "health producer/render identity is invalid"
    }
    if (-not $wrongHealthSourceFailedClosed) {
        throw "Quality health accepted a mismatched settings source identity."
    }
    $health.producerIdentity.sourceSettingsFingerprints = @($healthSettingsHash)
    $health.status = "failed"
    $health | Add-Member -NotePropertyName failure -NotePropertyValue (
        "GI diagnostic GiBudgetOverrun reported an error for 'ddgi-storage': " +
        "Live DDGI storage exceeds its configured hard tier budget.")
    $health | Add-Member -NotePropertyName validationWarningCount -NotePropertyValue 0
    $health | Add-Member -NotePropertyName validationErrorCount -NotePropertyValue 0
    $health | Add-Member -NotePropertyName operations -NotePropertyValue @()
    $health.diagnostics | Add-Member -NotePropertyName ValidationWarningMessageCount -NotePropertyValue 0
    $health.diagnostics | Add-Member -NotePropertyName ValidationErrorMessageCount -NotePropertyValue 0
    $health.diagnostics | Add-Member -NotePropertyName GiWarnings -NotePropertyValue @(
        [pscustomobject]@{ Severity = "Error"; Code = "GiBudgetOverrun" })
    Assert-QualitySequenceHealthReport `
        $manifest $workload $health $report $build "Release" "canonical" `
        $sequenceId $commit $reportPath $outputDirectory "" "" `
        "Synthetic pre-target quality reference" $true

    $strictQualityFailed = $false
    try {
        Assert-QualitySequenceHealthReport `
            $manifest $workload $health $report $build "Release" "canonical" `
            $sequenceId $commit $reportPath $outputDirectory "" "" `
            "Synthetic strict candidate quality"
    } catch {
        $strictQualityFailed = $_.Exception.Message -match "quality health gate failed"
    }
    if (-not $strictQualityFailed) {
        throw "Candidate quality admitted a pre-target reference exception."
    }

    $health.diagnostics.GiWarnings[0].Code = "NonFiniteTransport"
    $unexpectedQualityGiFailed = $false
    try {
        Assert-QualitySequenceHealthReport `
            $manifest $workload $health $report $build "Release" "canonical" `
            $sequenceId $commit $reportPath $outputDirectory "" "" `
            "Synthetic invalid pre-target quality" $true
    } catch {
        $unexpectedQualityGiFailed = $_.Exception.Message -match
            "unexpected quality-reference GI error"
    }
    if (-not $unexpectedQualityGiFailed) {
        throw "Quality reference admitted an unexpected GI error."
    }
    Write-Host "PASS synthetic-quality-health-budget-contract"
}

function Invoke-SyntheticAcceptanceRefCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    $requiredFunctions = @(
        "Assert-ExactPropertyNames",
        "Assert-Text",
        "Get-Sha256Bytes",
        "Get-Sha256Text",
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
        "Publish-AcceptanceEvidence",
        "Get-TimingAttemptRefName",
        "Test-TimingAttemptReserved",
        "Reserve-TimingAttempt",
        "Assert-TimingAttemptEvidence")
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
        $script:CampaignLockSha256 = "d" * 64
        function Get-AdmittedCampaignManifestSha256 { return "e" * 64 }
        $attempt = Reserve-TimingAttempt `
            $manifest "gpu" "SyntheticPass" "synthetic-candidate" $commit
        $attemptDecision = [pscustomobject]@{
            targetDomain = "gpu"
            targetPass = "SyntheticPass"
            candidate = [pscustomobject]@{ id = "synthetic-candidate" }
            acceptedHead = $commit
        }
        Assert-TimingAttemptEvidence $manifest $attempt $attemptDecision
        $duplicateAttemptFailed = $false
        try {
            $null = Reserve-TimingAttempt `
                $manifest "gpu" "SyntheticPass" "second" $commit
        } catch {
            $duplicateAttemptFailed = $_.Exception.Message -match
                "already consumed its one bounded attempt"
        }
        if (-not $duplicateAttemptFailed) {
            throw "Duplicate timing-identity attempt was not rejected atomically."
        }
        $decisionBlob = Get-GitText @(
            "hash-object", "--no-filters", "-w", "--", $decisionPath)
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
        Write-Host "PASS synthetic-acceptance-ref-and-attempt-ledger"
    } finally {
        $script:SolutionRoot = $originalSolutionRoot
        if ($null -eq $originalRepoRootVariable) {
            Remove-Variable -Name RepoRoot -Scope Script -ErrorAction SilentlyContinue
        } else {
            $script:RepoRoot = $originalRepoRootVariable.Value
        }
    }
}

function Invoke-SyntheticVerifierByteContractCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for verifier byte-contract tests."
    }
    foreach ($functionName in @(
            "Get-PropertyValue",
            "Get-ItemCount",
            "Assert-ExactPropertyNames",
            "Assert-NoDuplicateJsonProperties",
            "ConvertFrom-FrozenVerifierBytes",
            "ConvertFrom-QualityMetricVerifierBytes",
            "Assert-FrozenVerifierResultHeader",
            "Assert-ProtectedFileByteIdentity")) {
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

    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $savedProtectedFingerprints = Get-Variable `
        -Scope Script -Name ProtectedFingerprints -ErrorAction SilentlyContinue
    try {
        $protectedBytes = $utf8.GetBytes("synthetic protected helper")
        $protectedHash = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData(
                $protectedBytes)).ToLowerInvariant()
        $script:ProtectedFingerprints = [ordered]@{
            "tools/perf-quality-verify.ps1" = "file:sha256:$protectedHash"
        }
        Assert-ProtectedFileByteIdentity `
            "tools/perf-quality-verify.ps1" $protectedBytes `
            "Synthetic protected helper"
        $tamperedProtectedBytes = $utf8.GetBytes("tampered protected helper")
        $protectedTamperFailedClosed = $false
        try {
            Assert-ProtectedFileByteIdentity `
                "tools/perf-quality-verify.ps1" $tamperedProtectedBytes `
                "Synthetic protected helper"
        } catch {
            $protectedTamperFailedClosed = $_.Exception.Message -match
                "differs from its admitted bytes"
        }
        if (-not $protectedTamperFailedClosed) {
            throw "Protected helper byte tampering did not fail closed."
        }
    } finally {
        if ($null -eq $savedProtectedFingerprints) {
            Remove-Variable -Scope Script -Name ProtectedFingerprints `
                -ErrorAction SilentlyContinue
        } else {
            $script:ProtectedFingerprints = $savedProtectedFingerprints.Value
        }
    }
    $metricVerifierDefinition = @($driverAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq "Invoke-QualityMetricVerifier"
    }, $true))[0]
    if ($null -eq $metricVerifierDefinition) {
        throw "Campaign driver lacks Invoke-QualityMetricVerifier."
    }
    $helperHashAssignments = @($metricVerifierDefinition.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -ceq '$expectedHelperHash' -and
            $node.Right.Extent.Text -match 'HashData' -and
            $node.Right.Extent.Text -match '\$helperBytes'
    }, $true))
    $helperHashReferences = @($metricVerifierDefinition.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.VariablePath.UserPath -ceq 'expectedHelperHash'
    }, $true))
    if ($helperHashAssignments.Count -ne 1 -or
        $helperHashReferences.Count -lt 2 -or
        $helperHashAssignments[0].Extent.StartOffset -ge
            $helperHashReferences[-1].Extent.StartOffset -or
        $metricVerifierDefinition.Extent.Text -notmatch
            '\(Get-Sha256 \$helperPath\) -cne \$expectedHelperHash') {
        throw "Quality verifier helper hash is not captured before its TOCTOU recheck."
    }
    $expectedHeader = @("kind", "schema", "passed", "failures")
    $frozenJson =
        '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":true,"failures":[]}'
    $crlfResult = ConvertFrom-FrozenVerifierBytes `
        ($utf8.GetBytes($frozenJson + "`r`n")) "Synthetic CRLF verifier"
    Assert-FrozenVerifierResultHeader `
        $crlfResult $expectedHeader "synthetic-verifier" `
        "synthetic-verifier/v1" "Synthetic CRLF verifier"

    $orderedArtifact = [ordered]@{
        artifactPath = "synthetic.json"
        artifactSha256 = "a" * 64
    }
    Assert-ExactPropertyNames `
        $orderedArtifact @("artifactPath", "artifactSha256") `
        "Synthetic in-memory ordered artifact"
    if ([string](Get-PropertyValue $orderedArtifact "artifactPath" "") -cne
            "synthetic.json" -or
        [string](Get-PropertyValue $orderedArtifact "missing" "fallback") -cne
            "fallback") {
        throw "In-memory ordered artifact property lookup differs."
    }
    $orderedArtifact["unknown"] = 1
    $orderedArtifactFailedClosed = $false
    try {
        Assert-ExactPropertyNames `
            $orderedArtifact @("artifactPath", "artifactSha256") `
            "Synthetic forged ordered artifact"
    } catch {
        $orderedArtifactFailedClosed = $_.Exception.Message -match
            "property topology differs"
    }
    if (-not $orderedArtifactFailedClosed) {
        throw "In-memory ordered artifact topology did not fail closed."
    }

    $missingTerminatorFailedClosed = $false
    try {
        $null = ConvertFrom-FrozenVerifierBytes `
            ($utf8.GetBytes($frozenJson)) "Synthetic unterminated verifier"
    } catch {
        $missingTerminatorFailedClosed = $_.Exception.Message -match
            "newline-terminated JSON object"
    }
    if (-not $missingTerminatorFailedClosed) {
        throw "Frozen verifier stdout without a line terminator did not fail closed."
    }

    $duplicateFailedClosed = $false
    try {
        $null = ConvertFrom-FrozenVerifierBytes `
            ($utf8.GetBytes(
                '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":true,"failures":[],"nested":{"value":1,"value":2}}' +
                "`n")) `
            "Synthetic duplicate verifier"
    } catch {
        $duplicateFailedClosed = $_.Exception.Message -match
            "duplicate JSON property 'value'"
    }
    if (-not $duplicateFailedClosed) {
        throw "Duplicate frozen-verifier JSON property did not fail closed."
    }

    $unknownResult = ConvertFrom-FrozenVerifierBytes `
        ($utf8.GetBytes(
            '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":true,"failures":[],"unknown":1}' +
            "`n")) `
        "Synthetic unknown verifier"
    $unknownFailedClosed = $false
    try {
        Assert-FrozenVerifierResultHeader `
            $unknownResult $expectedHeader "synthetic-verifier" `
            "synthetic-verifier/v1" "Synthetic unknown verifier"
    } catch {
        $unknownFailedClosed = $_.Exception.Message -match
            "property topology differs"
    }
    if (-not $unknownFailedClosed) {
        throw "Unknown frozen-verifier result property did not fail closed."
    }

    $failedTopology = ConvertFrom-FrozenVerifierBytes `
        ($utf8.GetBytes(
            '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":false,"failures":["corrupt"]}' +
            "`n")) `
        "Synthetic failed verifier"
    $failedResultFailedClosed = $false
    try {
        Assert-FrozenVerifierResultHeader `
            $failedTopology $expectedHeader "synthetic-verifier" `
            "synthetic-verifier/v1" "Synthetic failed verifier"
    } catch {
        $failedResultFailedClosed = $_.Exception.Message -match
            "rejected its authenticated evidence"
    }
    if (-not $failedResultFailedClosed) {
        throw "Failed frozen-verifier result topology did not fail closed."
    }

    foreach ($scalarCorruption in @(
            [pscustomobject]@{
                name = "string passed"
                json = '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":"false","failures":[]}'
            },
            [pscustomobject]@{
                name = "numeric passed"
                json = '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":1,"failures":[]}'
            },
            [pscustomobject]@{
                name = "null failures"
                json = '{"kind":"synthetic-verifier","schema":"synthetic-verifier/v1","passed":true,"failures":null}'
            })) {
        $corruptResult = ConvertFrom-FrozenVerifierBytes `
            ($utf8.GetBytes([string]$scalarCorruption.json + "`n")) `
            "Synthetic $($scalarCorruption.name) verifier"
        $scalarFailedClosed = $false
        try {
            Assert-FrozenVerifierResultHeader `
                $corruptResult $expectedHeader "synthetic-verifier" `
                "synthetic-verifier/v1" `
                "Synthetic $($scalarCorruption.name) verifier"
        } catch {
            $scalarFailedClosed = $true
        }
        if (-not $scalarFailedClosed) {
            throw "Frozen verifier admitted $($scalarCorruption.name) topology."
        }
    }

    $metricJson =
        '{"schema":"njulf-perf-quality-verify-result/v1","results":[]}'
    $metricResult = ConvertFrom-QualityMetricVerifierBytes `
        ($utf8.GetBytes($metricJson)) "Synthetic metric verifier"
    Assert-ExactPropertyNames `
        $metricResult @("schema", "results") "Synthetic metric verifier"
    if ([string]$metricResult.schema -cne
        "njulf-perf-quality-verify-result/v1") {
        throw "Unterminated compact metric-verifier JSON was not preserved."
    }

    $metricNewlineFailedClosed = $false
    try {
        $null = ConvertFrom-QualityMetricVerifierBytes `
            ($utf8.GetBytes($metricJson + "`n")) `
            "Synthetic newline metric verifier"
    } catch {
        $metricNewlineFailedClosed = $_.Exception.Message -match
            "exactly one compact JSON object"
    }
    if (-not $metricNewlineFailedClosed) {
        throw "Metric-verifier JSON with a line terminator did not fail closed."
    }

    $metricDuplicateFailedClosed = $false
    try {
        $null = ConvertFrom-QualityMetricVerifierBytes `
            ($utf8.GetBytes(
                '{"schema":"njulf-perf-quality-verify-result/v1","results":[{"value":{"relativeResidual":0,"relativeResidual":1}}]}')) `
            "Synthetic duplicate metric verifier"
    } catch {
        $metricDuplicateFailedClosed = $_.Exception.Message -match
            "duplicate JSON property 'relativeResidual'"
    }
    if (-not $metricDuplicateFailedClosed) {
        throw "Duplicate metric-verifier JSON property did not fail closed."
    }
    $metricUnknown = ConvertFrom-QualityMetricVerifierBytes `
        ($utf8.GetBytes(
            '{"schema":"njulf-perf-quality-verify-result/v1","results":[],"unknown":1}')) `
        "Synthetic unknown metric verifier"
    $metricUnknownFailedClosed = $false
    try {
        Assert-ExactPropertyNames `
            $metricUnknown @("schema", "results") `
            "Synthetic unknown metric verifier"
    } catch {
        $metricUnknownFailedClosed = $_.Exception.Message -match
            "property topology differs"
    }
    if (-not $metricUnknownFailedClosed) {
        throw "Unknown metric-verifier result property did not fail closed."
    }
    Write-Host "PASS synthetic-frozen-and-metric-verifier-byte-contracts"
}

function Invoke-SyntheticQualityAnimationContractCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for quality animation tests."
    }
    foreach ($functionName in @(
            "Get-PropertyValue",
            "Test-WorkloadUsesSponzaAnimation",
            "Get-Sha256",
            "Test-Sha256Identity",
            "Assert-PathIdentity",
            "Get-QualitySequenceRoleValue",
            "Assert-ResultReportIdentity",
            "Assert-SponzaAnimationVerifierIdentity",
            "Assert-QualityActivationVerifierResult",
            "ConvertTo-QualityCaptureRunContract",
            "ConvertTo-QualityProducerContract",
            "ConvertTo-QualityCameraContract",
            "New-QualitySequenceReferenceContract",
            "Write-QualitySequenceReferenceContract")) {
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

    $manifest = Get-Content -LiteralPath $sourceManifest -Raw |
        ConvertFrom-Json -DateKind String
    $qualityContractPath = Join-Path $testRoot "synthetic-quality-contract.json"
    [System.IO.File]::WriteAllText(
        $qualityContractPath,
        '{}',
        [System.Text.UTF8Encoding]::new($false))
    $canonicalReport = [pscustomobject]@{
        SceneKind = "Bistro"
        Scenario = "Normal"
        CaptureVariant = "baseline"
        BuildConfiguration = "Release"
        Trajectory = "bistro-presentation"
        TrajectoryFingerprint = "sha256:" + ("1" * 64)
        TrajectoryRouteHash = "sha256:" + ("2" * 64)
        TrajectorySequenceHash = "sha256:" + ("3" * 64)
        TrajectoryFrameCount = 1
        WarmupFrameCount = 480
        MaximumAdditionalSettlingFrameCount =
            [int]$manifest.capture.maximumSettlingFrames
        MaximumReadbackDrainFrameCount =
            [int]$manifest.qualitySequence.maximumReadbackDrainFrames
        FirstRouteAbsoluteFrameIndex = 480
        CheckpointContractFingerprint = "sha256:" + ("4" * 64)
        CheckpointIndices = @()
        Checkpoints = @()
        CaptureRun = [pscustomobject]@{
            SceneKind = "Bistro"
            Scenario = "Normal"
            BuildConfiguration = "Release"
            ApplicationVersion = "1.0.0+synthetic"
            Commit = "9" * 40
            ShaderBundleHash = "sha256:" + ("a" * 64)
            SettingsSchemaVersion = 1
            ExecutableHash = "sha256:" + ("b" * 64)
            DirtyWorktreeState = "clean"
        }
        ProducerIdentity = [pscustomobject]@{
            Schema = "material-gi-producer-identity/v1"
            BuildCommit = "9" * 40
            ShaderFingerprint = "a" * 64
            SettingsFingerprint = "c" * 64
            SourceSettingsFingerprints = @("c" * 64)
            GpuName = "Synthetic GPU"
            DriverVersion = "1.2.3"
            QualityTier = "StressUnlimited"
        }
        SponzaFixtureMode = 0
        Activation = "none"
        ActivationFingerprint = "sha256:" + ("5" * 64)
        ActivationEvidence = [pscustomobject]@{
            AnimationConfigurationFingerprint = "unavailable"
            AnimationSequenceHash = "unavailable"
            ActivationStructuralSequenceHash = "sha256:" + ("6" * 64)
            ActivationExecutionSequenceHash = "sha256:" + ("7" * 64)
        }
    }
    $bistroWorkload = [pscustomobject]@{ scene = "Bistro" }
    $contract = New-QualitySequenceReferenceContract `
        $manifest $bistroWorkload $canonicalReport `
        $qualityContractPath ("8" * 64)
    if ([string]$contract.sponzaSceneAnimationFingerprint -cne "unavailable" -or
        [int]$contract.sponzaSceneAnimationMode -ne 0 -or
        [string]$contract.sponzaSceneAnimationConfigurationFingerprint -cne
            "unavailable" -or
        [string]$contract.sponzaSceneAnimationSequenceHash -cne "unavailable" -or
        -not [string]::IsNullOrEmpty(
            [string]$contract.sponzaSceneAnimationSidecarPath) -or
        -not [string]::IsNullOrEmpty(
            [string]$contract.sponzaSceneAnimationSidecarSha256)) {
        throw "Non-Sponza quality contract does not use canonical sentinels."
    }

    $singleFov = [single]0.98174775
    $wireContractPath = Join-Path $testRoot "synthetic-quality-wire-contract.json"
    $wireContract = Write-QualitySequenceReferenceContract `
        $wireContractPath ([ordered]@{
            camera = [ordered]@{ fieldOfViewRadians = $singleFov }
        })
    $wireFov = $wireContract.contract.camera.fieldOfViewRadians
    if ($wireFov.GetType() -ne [double] -or
        [string]$wireFov -cne "0.98174775" -or
        [string]$wireFov -ceq [string]$singleFov) {
        throw "Quality contract did not return its exact serialized camera value."
    }

    $reportPath = [System.IO.Path]::GetFullPath(
        (Join-Path $testRoot "sponza-quality-report.json"))
    [System.IO.File]::WriteAllText(
        $reportPath,
        '{}',
        [System.Text.UTF8Encoding]::new($false))
    $sidecarPath = $reportPath + ".sponza-animation.bin"
    [System.IO.File]::WriteAllBytes($sidecarPath, [byte[]]@(1, 2, 3, 4))
    $sidecarSha256 = Get-Sha256 $sidecarPath
    $animationFingerprint = "sha256:" + ("a" * 64)
    $configurationFingerprint = "sha256:" + ("b" * 64)
    $sequenceHash = "sha256:" + ("c" * 64)
    $activationFingerprint = "sha256:" + ("d" * 64)
    $activationStructural = "sha256:" + ("e" * 64)
    $activationExecution = "sha256:" + ("f" * 64)
    $sponzaWorkload = [pscustomobject]@{
        scene = "Sponza"
        sponzaFixtureMode = "animation"
        activation = "sponza-forward-gi"
    }
    $sponzaReport = [pscustomobject]@{
        Activation = "sponza-forward-gi"
        ActivationFingerprint = $activationFingerprint
        ActivationEvidence = [pscustomobject]@{
            Fingerprint = $activationFingerprint
            ActivationStructuralSequenceHash = $activationStructural
            ActivationExecutionSequenceHash = $activationExecution
        }
        SponzaSceneAnimationEvidence = [pscustomobject]@{
            Fingerprint = $animationFingerprint
            Mode = 1
            ConfigurationFingerprint = $configurationFingerprint
            SequenceHash = $sequenceHash
            SidecarPath = $sidecarPath
            SidecarSha256 = $sidecarSha256
        }
    }
    $sponzaResult = [pscustomobject]@{
        reportPath = $reportPath
        reportSha256 = Get-Sha256 $reportPath
        sequenceId = "synthetic-sequence"
        role = 2
        activation = "sponza-forward-gi"
        activationFingerprint = $activationFingerprint
        activationStructuralSequenceHash = $activationStructural
        activationExecutionSequenceHash = $activationExecution
        sponzaSceneAnimationFingerprint = $animationFingerprint
        sponzaSceneAnimationMode = 1
        sponzaSceneAnimationConfigurationFingerprint =
            $configurationFingerprint
        sponzaSceneAnimationSequenceHash = $sequenceHash
        sponzaSceneAnimationSidecarPath = $sidecarPath
        sponzaSceneAnimationSidecarSha256 = $sidecarSha256
    }
    $sidecar = Assert-QualityActivationVerifierResult `
        $sponzaWorkload $sponzaReport "candidate" "synthetic-sequence" `
        $sponzaResult $reportPath (Get-Sha256 $reportPath) `
        "Synthetic Sponza activation"
    if ([string]$sidecar.path -cne $sidecarPath -or
        [string]$sidecar.sha256 -cne $sidecarSha256) {
        throw "Sponza per-run sidecar identity was not preserved."
    }

    $wrongSidecarPath = Join-Path $testRoot "shared-reference-sidecar.bin"
    [System.IO.File]::WriteAllBytes($wrongSidecarPath, [byte[]]@(1, 2, 3, 4))
    $sponzaReport.SponzaSceneAnimationEvidence.SidecarPath = $wrongSidecarPath
    $sponzaResult.sponzaSceneAnimationSidecarPath = $wrongSidecarPath
    $sharedSidecarFailedClosed = $false
    try {
        $null = Assert-QualityActivationVerifierResult `
            $sponzaWorkload $sponzaReport "candidate" "synthetic-sequence" `
            $sponzaResult $reportPath (Get-Sha256 $reportPath) `
            "Synthetic shared Sponza activation"
    } catch {
        $sharedSidecarFailedClosed = $_.Exception.Message -match
            "campaign-owned animation sidecar"
    }
    if (-not $sharedSidecarFailedClosed) {
        throw "Sponza verifier accepted a non-per-run sidecar path."
    }
    Write-Host "PASS synthetic-quality-animation-contract"
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
        "Get-BootstrapLowerBound",
        "Assert-TimingStats",
        "Get-Median",
        "Get-Timing",
        "Get-PassTiming",
        "Get-ScopedTiming",
        "Get-TargetHypothesis",
        "Get-HypothesisWorkloads",
        "Get-TargetPassPairedDifferences",
        "Get-ImprovementPercent",
        "Assert-CrossBuildIdentity",
        "Compare-WorkloadCaptures",
        "New-ConfigurationHypothesisResults",
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
        ConvertFrom-Json -DateKind String
    $target = @($manifest.workloads | Where-Object {
        [string]$_.id -eq "bistro-forward-gi-enabled"
    })[0]
    $selectedIds = @(Get-ConfigurationWorkloadSelection `
        $manifest @($target) $false | ForEach-Object { [string]$_.id })
    $expectedIds = @(
        "bistro-forward-gi-enabled",
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

    $aoHypotheses = @(
        Get-TargetHypothesis $manifest "ambient-occlusion"
        Get-TargetHypothesis $manifest "ambient-occlusion-blur")
    $hypothesisWorkloads = @(Get-HypothesisWorkloads `
        $manifest $aoHypotheses)
    if (($hypothesisWorkloads.id -join ',') -cne
            "bistro-motion,sponza-horizontal-motion" -or
        @($hypothesisWorkloads[0].campaignTargetClaims).Count -ne 2 -or
        @($hypothesisWorkloads[1].campaignTargetClaims).Count -ne 2) {
        throw "Hypothesis expansion did not deduplicate workloads while retaining claims."
    }
    Write-Host "PASS synthetic-hypothesis-workload-deduplication"

    $passReports = @(
        [pscustomobject]@{ GpuPasses = @([pscustomobject]@{
            Name = "SyntheticPass"; P95Milliseconds = 10.0 }) ; CpuStages = @() },
        [pscustomobject]@{ GpuPasses = @([pscustomobject]@{
            Name = "SyntheticPass"; P95Milliseconds = 8.0 }) ; CpuStages = @() },
        [pscustomobject]@{ GpuPasses = @([pscustomobject]@{
            Name = "SyntheticPass"; P95Milliseconds = 7.0 }) ; CpuStages = @() },
        [pscustomobject]@{ GpuPasses = @([pscustomobject]@{
            Name = "SyntheticPass"; P95Milliseconds = 11.0 }) ; CpuStages = @() })
    $passWorkload = [pscustomobject]@{
        id = "synthetic-target"
        campaignTargetClaims = @([pscustomobject]@{
            hypothesisId = "synthetic-hypothesis"
            scene = "Synthetic"
            workloadId = "synthetic-target"
            targetDomain = "gpu"
            targetPass = "SyntheticPass"
        })
    }
    $passDifferences = Get-TargetPassPairedDifferences `
        $passWorkload $passReports
    $syntheticPassDifferences = Get-PropertyValue `
        $passDifferences "gpu::SyntheticPass" @()
    if ((@($syntheticPassDifferences) -join ',') -cne "2,4") {
        throw "Target-pass ABBA differences do not use independent B1-C1/B2-C2 pairs."
    }

    function New-SyntheticComparisonReport {
        param([double]$PassP95)
        return [pscustomobject]@{
            Scenario = "Synthetic"
            CaptureContract = [pscustomobject]@{
                Trajectory = "synthetic"
                TrajectoryFingerprint = "same"
                TrajectoryFrameCount = 1
                TrajectoryRouteHash = "same"
                TrajectorySequenceHash = "same"
            }
            LastDiagnostics = [pscustomobject]@{
                CaptureSceneAssetHash = "same"
                CaptureRun = [pscustomobject]@{ SettingsSchemaVersion = 1 }
                ActiveQualityPreset = "same"
                ResolvedGiSettings = [pscustomobject]@{ StableHash = "same" }
                ActiveFeatureIsolation = "same"
                GlobalIlluminationDebugView = "same"
            }
            ProducerIdentity = [pscustomobject]@{
                Schema = "same"
                SettingsFingerprint = "same"
                GpuName = "same"
                DriverVersion = "same"
                QualityTier = "same"
            }
            CpuFrameMilliseconds = [pscustomobject]@{
                P50Milliseconds = 10.0
                P95Milliseconds = 10.0
                P99Milliseconds = 10.0
            }
            GpuFrameMilliseconds = [pscustomobject]@{
                P50Milliseconds = 10.0
                P95Milliseconds = 10.0
                P99Milliseconds = 10.0
            }
            GpuPasses = @([pscustomobject]@{
                Name = "SyntheticPass"
                P95Milliseconds = $PassP95
            })
            CpuStages = @()
            HdrDifference = [pscustomobject]@{
                RelativeRmse = 0.0
                FlipP95 = 0.0
                RoiResults = @()
            }
        }
    }
    $comparisonManifest = [pscustomobject]@{
        capture = [pscustomobject]@{ abbaCycles = 3 }
        acceptance = [pscustomobject]@{
            minimumFrameImprovementPercent = 1.0
            minimumFrameImprovementMilliseconds = 0.1
            minimumPassImprovementPercent = 5.0
            minimumPassImprovementMilliseconds = 0.05
            maximumRegressionPercent = 1.0
            bootstrapSamples = 100
            bootstrapConfidence = 0.95
        }
    }
    $baselineReports = @(1..6 | ForEach-Object {
        New-SyntheticComparisonReport 5.0
    })
    $candidateReports = @(1..6 | ForEach-Object {
        New-SyntheticComparisonReport 4.0
    })
    $positivePass = [pscustomobject]([ordered]@{
        "gpu::SyntheticPass" = [double[]]@(1, 1, 1, 1, 1, 1)
    })
    $negativePass = [pscustomobject]([ordered]@{
        "gpu::SyntheticPass" = [double[]]@(-1, -1, -1, -1, -1, -1)
    })
    $positive = Compare-WorkloadCaptures `
        $comparisonManifest $passWorkload $baselineReports $candidateReports `
        ([double[]]@(1, 1, 1, 1, 1, 1)) $positivePass $true
    $negative = Compare-WorkloadCaptures `
        $comparisonManifest $passWorkload $baselineReports $candidateReports `
        ([double[]]@(1, 1, 1, 1, 1, 1)) $negativePass $true
    if ([string]$positive.Decision -cne "keep" -or
        [bool]$positive.TargetClaimResults[0].FrameWin -or
        -not [bool]$positive.TargetClaimResults[0].PassWin -or
        [double]$positive.FrameBootstrapLower95Milliseconds -le 0.0 -or
        [double]$positive.TargetClaimResults[0].TargetPassBootstrapLower95Milliseconds -le 0.0 -or
        [string]$negative.Decision -cne "rollback" -or
        [double]$negative.FrameBootstrapLower95Milliseconds -le 0.0 -or
        [double]$negative.TargetClaimResults[0].TargetPassBootstrapLower95Milliseconds -ge 0.0) {
        throw "Target-pass acceptance reused frame bootstrap evidence."
    }
    Write-Host "PASS synthetic-independent-frame-pass-bootstrap"
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
            "Get-QualitySequenceCheckpointFingerprint",
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
    $checkpointFingerprints = @($expectedCheckpoints.Keys | ForEach-Object {
        Get-QualitySequenceCheckpointFingerprint ([string]$_)
    })
    if (@($checkpointFingerprints | Where-Object {
                [string]$_ -notmatch '^sha256:[0-9a-f]{64}$'
            }).Count -ne 0 -or
        @($checkpointFingerprints | Sort-Object -Unique).Count -ne
            $expectedCheckpoints.Count) {
        throw "Quality checkpoint fingerprints are invalid or collide."
    }

    $manifest = Get-Content -LiteralPath $sourceManifest -Raw |
        ConvertFrom-Json -DateKind String
    $workload = [pscustomobject]@{ qualityTrajectory = "bistro-loop" }
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

function Invoke-SyntheticManifestSnapshotCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for manifest snapshot test."
    }
    foreach ($functionName in @(
            "Get-Sha256", "Get-Sha256Bytes", "Assert-NoLinkedPathComponents",
            "Test-PathContainedBy", "Write-AtomicByteArtifact",
            "Assert-CampaignManifestIntegrity",
            "Initialize-CampaignManifestSnapshot")) {
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
    $variableNames = @(
        "ManifestFile", "RunRoot", "CampaignManifestBytes",
        "CampaignManifestSha256", "CampaignManifestSnapshotPath")
    $absent = [object]::new()
    $savedVariables = [ordered]@{}
    foreach ($variableName in $variableNames) {
        $variable = Get-Variable -Scope Script -Name $variableName `
            -ErrorAction SilentlyContinue
        $savedVariables[$variableName] = if ($null -eq $variable) {
            $absent
        } else { $variable.Value }
    }
    try {
        $caseRoot = Join-Path $testRoot "manifest-snapshot"
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        $manifestPath = Join-Path $caseRoot "manifest.json"
        $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
            '{"schema":"synthetic"}')
        [System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
        $script:ManifestFile = $manifestPath
        $script:RunRoot = $caseRoot
        $script:CampaignManifestBytes = $manifestBytes
        $script:CampaignManifestSha256 = Get-Sha256Bytes $manifestBytes
        Initialize-CampaignManifestSnapshot
        $snapshotPath = Join-Path $caseRoot "campaign.manifest.snapshot.json"
        if ([string]$script:CampaignManifestSnapshotPath -cne
                [System.IO.Path]::GetFullPath($snapshotPath) -or
            -not (Test-Path -LiteralPath $snapshotPath -PathType Leaf) -or
            (Get-Sha256 $snapshotPath) -cne $script:CampaignManifestSha256) {
            throw "Campaign manifest snapshot was not published byte-exactly."
        }
        $overwriteFailed = $false
        try { Initialize-CampaignManifestSnapshot } catch {
            $overwriteFailed = $_.Exception.Message -match "already exists"
        }
        if (-not $overwriteFailed) {
            throw "Campaign manifest snapshot allowed an overwrite."
        }
        Write-Host "PASS synthetic-manifest-snapshot"
    } finally {
        foreach ($variableName in $variableNames) {
            if ([object]::ReferenceEquals(
                    $savedVariables[$variableName], $absent)) {
                Remove-Variable -Scope Script -Name $variableName `
                    -ErrorAction SilentlyContinue
            } else {
                Set-Variable -Scope Script -Name $variableName `
                    -Value $savedVariables[$variableName]
            }
        }
    }
}

function Invoke-SyntheticHotspotDiscoveryCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for hotspot discovery test."
    }
    foreach ($functionName in @(
            "Get-PropertyValue", "Get-Sha256", "Get-Timing",
            "Get-CampaignConfigurations", "New-HotspotDiscoveryData")) {
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
    function Get-AdmittedCampaignManifestSha256 { return "a" * 64 }

    $manifest = [pscustomobject]@{
        campaignId = "synthetic-hotspots"
        iterationConfiguration = "Release"
        finalConfigurations = @("Release", "ShippingPerformance")
        discoveryPolicy = [pscustomobject][ordered]@{
            enabled = $true
            domains = @("gpu", "cpu")
            minimumSharePercent = 5.0
            minimumP95Milliseconds = 0.25
            requireBothConfigurations = $true
            requireBistroAndSponza = $true
            attemptsPerTiming = 1
            fullMatrixAfterCandidate = $true
        }
        workloads = @(
            [pscustomobject]@{ id = "bistro"; scene = "Bistro" },
            [pscustomobject]@{ id = "sponza"; scene = "Sponza" })
    }
    $entries = [System.Collections.Generic.List[object]]::new()
    $entryIndex = 0
    foreach ($configuration in @("Release", "ShippingPerformance")) {
        foreach ($workload in @($manifest.workloads)) {
            $reportPath = Join-Path $testRoot (
                "hotspot-$entryIndex.json")
            [System.IO.File]::WriteAllText(
                $reportPath, "{}", [System.Text.UTF8Encoding]::new($false))
            $gpuHot = if ([string]$workload.scene -ceq "Bistro") {
                2.0
            } else { 1.5 }
            $report = [pscustomobject]@{
                LastDiagnostics = [pscustomobject]@{
                    CaptureRun = [pscustomobject]@{ Commit = "b" * 40 }
                }
                CpuFrameMilliseconds = [pscustomobject]@{ P95Milliseconds = 10.0 }
                GpuFrameMilliseconds = [pscustomobject]@{ P95Milliseconds = 10.0 }
                GpuPasses = @(
                    [pscustomobject]@{ Name = "GpuHot"; P95Milliseconds = $gpuHot },
                    [pscustomobject]@{ Name = "Tiny"; P95Milliseconds = 0.1 },
                    [pscustomobject]@{
                        Name = "ReleaseOnly"
                        P95Milliseconds = if ($configuration -ceq "Release") {
                            3.0
                        } else { 0.0 }
                    })
                CpuStages = @([pscustomobject]@{
                    Name = "CpuHot"; P95Milliseconds = 1.0
                })
                CampaignFrozenVerifierEvidence = [pscustomobject]@{ marker = $entryIndex }
            }
            $entries.Add([pscustomobject]@{
                Configuration = $configuration
                Workload = $workload
                ReportPath = $reportPath
                ReportSha256 = Get-Sha256 $reportPath
                Report = $report
                BuildIdentity = [pscustomobject]@{ marker = $entryIndex }
            })
            $entryIndex++
        }
    }
    $artifact = New-HotspotDiscoveryData `
        $manifest @($entries) ("b" * 40) `
        ([DateTimeOffset]::Parse("2026-08-20T00:00:00.0000000+00:00"))
    if ([string]$artifact.schema -cne "njulf-perf-hotspot-discovery/v1" -or
        @($artifact.reports).Count -ne 4 -or
        @($artifact.eligibleHotspots).Count -ne 2 -or
        [string]$artifact.eligibleHotspots[0].domain -cne "gpu" -or
        [string]$artifact.eligibleHotspots[0].name -cne "GpuHot" -or
        [string]$artifact.eligibleHotspots[1].domain -cne "cpu" -or
        [string]$artifact.eligibleHotspots[1].name -cne "CpuHot" -or
        ((@($artifact.eligibleHotspots[0].claims).workloadId -join ',') -cne
            "bistro,sponza")) {
        throw "Hotspot discovery did not rank exact cross-config CPU/GPU evidence."
    }
    $missingFailed = $false
    try {
        $null = New-HotspotDiscoveryData `
            $manifest @($entries | Select-Object -Skip 1) ("b" * 40) `
            ([DateTimeOffset]::UtcNow)
    } catch { $missingFailed = $_.Exception.Message -match "one authenticated report" }
    $entries[0].Report.GpuPasses += [pscustomobject]@{
        Name = "GpuHot"; P95Milliseconds = 1.0
    }
    $duplicateFailed = $false
    try {
        $null = New-HotspotDiscoveryData `
            $manifest @($entries) ("b" * 40) ([DateTimeOffset]::UtcNow)
    } catch { $duplicateFailed = $_.Exception.Message -match "malformed or duplicate" }
    if (-not $missingFailed -or -not $duplicateFailed) {
        throw "Hotspot discovery did not fail closed on missing/duplicate rows."
    }
    Write-Host "PASS synthetic-hotspot-discovery"
}

function Invoke-ProcessTimeoutContainmentCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for timeout containment test."
    }
    foreach ($functionName in @(
            "Stop-ProcessTreeAndDrain", "Invoke-ProcessChecked")) {
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
    $caseRoot = Join-Path $testRoot "timeout-containment"
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $marker = Join-Path $caseRoot "late-write.txt"
    $childPath = Join-Path $caseRoot "child.ps1"
    $parentPath = Join-Path $caseRoot "parent.ps1"
    [System.IO.File]::WriteAllText(
        $childPath,
        "Start-Sleep -Seconds 4`n[System.IO.File]::WriteAllText('$($marker.Replace("'", "''"))','late')`n",
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        $parentPath,
        @"
`$info = [System.Diagnostics.ProcessStartInfo]::new()
`$info.FileName = (Join-Path `$PSHOME 'pwsh.exe')
`$info.UseShellExecute = `$false
`$info.CreateNoWindow = `$true
[void]`$info.ArgumentList.Add('-NoProfile')
[void]`$info.ArgumentList.Add('-File')
[void]`$info.ArgumentList.Add('$($childPath.Replace("'", "''"))')
`$child = [System.Diagnostics.Process]::Start(`$info)
Start-Sleep -Seconds 30
"@,
        [System.Text.UTF8Encoding]::new($false))
    $timedOut = $false
    try {
        Invoke-ProcessChecked `
            (Join-Path $PSHOME "pwsh.exe") `
            @("-NoProfile", "-File", $parentPath) `
            "Synthetic child-writer timeout" 1 $caseRoot
    } catch {
        $timedOut = $_.Exception.Message -match
            "timed out.*terminal process-tree cleanup"
    }
    Start-Sleep -Seconds 5
    if (-not $timedOut -or (Test-Path -LiteralPath $marker)) {
        throw "Timed-out process tree wrote after terminal cleanup returned."
    }
    Write-Host "PASS process-timeout-tree-containment"
}

function Invoke-SyntheticCandidateEnvelopeCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for candidate envelope test."
    }
    foreach ($functionName in @(
            "Get-Sha256Bytes", "Get-Sha256Text", "Read-BoundedFileBytes",
            "Assert-NoDuplicateJsonProperties", "Read-StrictJsonFile",
            "Assert-JsonString", "Assert-JsonInteger", "Assert-JsonArray",
            "Assert-ExactPropertyNames", "Test-PathContainedBy",
            "Read-CandidateEnvelope")) {
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
    $script:RunRoot = [System.IO.Path]::GetFullPath($testRoot)
    $script:CampaignLockSha256 = "d" * 64
    $script:ProtectedFingerprints = [ordered]@{}
    function Assert-NoLinkedPathComponents {
        param([string]$Path, [string]$Label)
        return [System.IO.Path]::GetFullPath($Path)
    }
    function Get-AdmittedCampaignManifestSha256 { return "a" * 64 }
    function Get-StablePatchId { param([string]$Commit); return "e" * 40 }
    function Get-CommitChangedPaths {
        param([string]$Commit)
        return @($script:SyntheticCandidateChangedPaths)
    }
    function Resolve-SolutionPath {
        param([string]$Path)
        return [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $Path))
    }
    function Get-GitText {
        param([string[]]$Arguments)
        if ($Arguments[0] -ceq "rev-list") {
            return ("c" * 40) + " " + ("b" * 40)
        }
        throw "Unexpected synthetic git text command: $($Arguments -join ' ')"
    }
    function Invoke-Git {
        param([string[]]$Arguments)
        if ($Arguments[0] -ceq "diff") {
            return @("10`t5`tNjulf.Shaders/synthetic.comp")
        }
        throw "Unexpected synthetic git command: $($Arguments -join ' ')"
    }
    function Test-TimingAttemptReserved {
        param($Manifest, [string]$Domain, [string]$Name)
        return $false
    }
    $script:SyntheticCandidateChangedPaths = @(
        "Njulf.Shaders/synthetic.comp")
    $hotspot = [pscustomobject][ordered]@{
        rank = 1
        domain = "gpu"
        name = "SyntheticPass"
        maximumP95Milliseconds = 2.0
        maximumSharePercent = 20.0
        claims = @(
            [pscustomobject][ordered]@{ scene = "Bistro"; workloadId = "bistro" },
            [pscustomobject][ordered]@{ scene = "Sponza"; workloadId = "sponza" })
    }
    $secondHotspot = [pscustomobject][ordered]@{
        rank = 2
        domain = "cpu"
        name = "SecondPass"
        maximumP95Milliseconds = 1.0
        maximumSharePercent = 10.0
        claims = $hotspot.claims
    }
    function Assert-HotspotDiscoveryArtifact {
        param($Manifest, $Lock, [string]$Path, [string]$ExpectedSha256,
            [string]$ExpectedRetainedCommit, [string]$Label)
        return [pscustomobject]@{
            eligibleHotspots = @($hotspot, $secondHotspot)
        }
    }
    $manifest = [pscustomobject]@{ campaignId = "synthetic-envelope" }
    $envelope = [pscustomobject][ordered]@{
        schema = "njulf-perf-candidate-envelope/v1"
        campaignId = "synthetic-envelope"
        manifestSha256 = "a" * 64
        lockSha256 = "d" * 64
        acceptedHead = "b" * 40
        discoveryArtifactPath = (Join-Path $testRoot "synthetic-discovery.json")
        discoveryArtifactSha256 = "f" * 64
        hotspot = $hotspot
        attempt = 1
        candidate = [pscustomobject][ordered]@{
            id = "auto-synthetic"
            sourceCommit = "c" * 40
            patchId = "e" * 40
            allowedPaths = @("Njulf.Shaders/synthetic.comp")
            focusedTestFilter = "FullyQualifiedName~Synthetic"
        }
    }
    $path = Join-Path $testRoot "synthetic-envelope.json"
    [System.IO.File]::WriteAllText(
        $path,
        ($envelope | ConvertTo-Json -Depth 12),
        [System.Text.UTF8Encoding]::new($false))
    $admission = Read-CandidateEnvelope `
        $manifest ([pscustomobject]@{}) $path ("b" * 40) $true
    if ([string]$admission.DecisionIdentity.kind -cne "discovered" -or
        [string]$admission.Hypothesis.targetDomain -cne "gpu" -or
        [string]$admission.Hypothesis.targetPass -cne "SyntheticPass" -or
        @($admission.Hypothesis.claims).Count -ne 2) {
        throw "Automatic candidate envelope was not admitted exactly."
    }
    $envelope.attempt = "1"
    $badPath = Join-Path $testRoot "synthetic-envelope-bad.json"
    [System.IO.File]::WriteAllText(
        $badPath,
        ($envelope | ConvertTo-Json -Depth 12),
        [System.Text.UTF8Encoding]::new($false))
    $wrongTypeFailed = $false
    try {
        $null = Read-CandidateEnvelope `
            $manifest ([pscustomobject]@{}) $badPath ("b" * 40) $true
    } catch { $wrongTypeFailed = $_.Exception.Message -match "must be a JSON integer" }
    if (-not $wrongTypeFailed) {
        throw "Candidate envelope accepted a coercible string attempt."
    }
    $envelope.attempt = 1
    $envelope.candidate.allowedPaths = @(
        "Njulf.Shaders/Njulf.Shaders.csproj")
    $script:SyntheticCandidateChangedPaths = @(
        "Njulf.Shaders/Njulf.Shaders.csproj")
    $buildGraphPath = Join-Path $testRoot "synthetic-envelope-build-graph.json"
    [System.IO.File]::WriteAllText(
        $buildGraphPath,
        ($envelope | ConvertTo-Json -Depth 12),
        [System.Text.UTF8Encoding]::new($false))
    $buildGraphFailed = $false
    try {
        $null = Read-CandidateEnvelope `
            $manifest ([pscustomobject]@{}) $buildGraphPath ("b" * 40) $true
    } catch {
        $buildGraphFailed = $_.Exception.Message -match
            "not an admitted source extension"
    }
    if (-not $buildGraphFailed) {
        throw "Automatic candidate envelope admitted build-graph mutation."
    }
    Write-Host "PASS synthetic-candidate-envelope"
}

function Invoke-SyntheticBuildPathBudgetCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for build path budget test."
    }
    foreach ($functionName in @(
            "Assert-Text",
            "New-CampaignBuildIsolationLayout")) {
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

    $layout = New-CampaignBuildIsolationLayout "ShippingPerformance"
    $expectedParent = [System.IO.Path]::GetFullPath(
        (Join-Path ([System.IO.Path]::GetTempPath()) "njb"))
    if (-not ([string]$layout.SourceRoot).StartsWith(
            $expectedParent + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName([string]$layout.ArtifactRoot) -cne ".a" -or
        ([string]$layout.PathBudgetProbe).Length -gt 240 -or
        ([string]$layout.PathBudgetProbe) -cnotmatch
            '[\\/]shippingperformance[\\/]Shaders[\\/]' -or
        (Test-Path -LiteralPath ([string]$layout.SourceRoot))) {
        throw "Hermetic ShippingPerformance build paths exceed the admitted short-path contract."
    }
    Write-Host "PASS synthetic-build-path-budget"
}

function Invoke-SyntheticShaderCacheWiringCase {
    $source = Get-Content -LiteralPath $driver -Raw
    $cacheArgument = '"-p:NjulfShaderCacheDirectory=$script:ShaderCacheRoot"'
    $cacheArgumentCount = [regex]::Matches(
        $source,
        [regex]::Escape($cacheArgument)).Count
    if ($cacheArgumentCount -ne 2) {
        throw "Hermetic build and focused-test invocations must both use the persistent shader cache."
    }
    if ($source -notmatch
            '(?s)Join-Path\s+\$script:SolutionRoot\s+"artifacts/shader-cache/v1"' -or
        $source -notmatch
            'Test-PathContainedBy\s+\$script:ShaderCacheRoot\s+\$artifactRoot' -or
        $source -notmatch 'check-ignore --quiet --no-index') {
        throw "Persistent shader cache path safety is not fail-closed."
    }
    Write-Host "PASS synthetic-shader-cache-wiring"
}

function Invoke-SyntheticRuntimeCacheIsolationCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the runtime cache isolation test."
    }
    foreach ($functionName in @(
            "Get-PropertyValue", "Assert-NoLinkedPathComponents",
            "Test-PathContainedBy", "Get-RuntimeCacheRoot",
            "Get-RuntimeCacheEnvironment", "Get-RuntimeCachePrimeRoot",
            "Get-RuntimeCaptureCacheRoot",
            "Assert-RuntimeCachePrimeCaptureEvidence",
            "Assert-RuntimeCacheCapture",
            "Stop-ProcessTreeAndDrain", "Invoke-ProcessChecked")) {
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
    foreach ($captureFunctionName in @(
            "Invoke-BenchmarkCapture", "Invoke-QualitySequenceCapture")) {
        $captureDefinition = @($driverAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $captureFunctionName
        }, $true))[0]
        $captureSource = $captureDefinition.Extent.Text
        if ($captureSource -notmatch 'Initialize-RuntimeCachePrime' -or
            $captureSource -notmatch 'New-RuntimeCaptureCacheEnvironment' -or
            $captureSource -notmatch 'Assert-RuntimeCacheCapture') {
            throw "$captureFunctionName does not enforce the prime/clone/cache gate."
        }
    }
    $primeDefinition = @($driverAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq "Initialize-RuntimeCachePrime"
    }, $true))[0]
    $primeSource = $primeDefinition.Extent.Text
    if (($primeSource | Select-String -Pattern 'Get-BenchmarkArguments' -AllMatches).
            Matches.Count -ne 2 -or
        $primeSource -notmatch '\$bootstrapHdrPath\s+\$true' -or
        $primeSource -notmatch '\$QualityContractPath\s+\$bootstrapHdrPath\s+\$false' -or
        $primeSource -notmatch 'Where-Object\s+\{\s*\$_\s+-cne\s+"--benchmark-require-1080p60"' -or
        $primeSource -match 'primeWorkload\.measureFrames\s*=') {
        throw "Runtime cache prime does not preserve the two-phase production contract."
    }

    $caseRoot = Join-Path $testRoot "runtime-cache-isolation"
    $script:RunRoot = Join-Path $caseRoot "run"
    New-Item -ItemType Directory -Force -Path $script:RunRoot | Out-Null
    $releaseBuild = [pscustomobject]@{
        BundleFingerprint = "directory:sha256:" + ("a" * 64)
        RootPath = Join-Path $script:RunRoot "builds/release"
        RuntimeExecutableBundleHash = "sha256:" + ("c" * 64)
        BuildCommit = "d" * 40
    }
    $shippingBuild = [pscustomobject]@{
        BundleFingerprint = "directory:sha256:" + ("a" * 64)
        RootPath = Join-Path $script:RunRoot "builds/shipping"
        RuntimeExecutableBundleHash = "sha256:" + ("c" * 64)
        BuildCommit = "d" * 40
    }
    $candidateBuild = [pscustomobject]@{
        BundleFingerprint = "directory:sha256:" + ("b" * 64)
        RootPath = Join-Path $script:RunRoot "builds/candidate"
        RuntimeExecutableBundleHash = "sha256:" + ("e" * 64)
        BuildCommit = "f" * 40
    }
    $releaseFirst = Get-RuntimeCacheEnvironment $releaseBuild "Release"
    $releaseRepeat = Get-RuntimeCacheEnvironment $releaseBuild "Release"
    $shipping = Get-RuntimeCacheEnvironment `
        $shippingBuild "ShippingPerformance"
    $candidate = Get-RuntimeCacheEnvironment $candidateBuild "Release"
    $expectedNames = @(
        "NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY",
        "NJULF_VULKAN_PIPELINE_CACHE_SEED_DIRECTORY",
        "NJULF_PIPELINE_BINARY_CACHE_DIRECTORY",
        "NJULF_PIPELINE_BINARY_SEED_DIRECTORY",
        "NJULF_DDGI_WARM_CACHE_DIR",
        "NJULF_ENVIRONMENT_CACHE_DIR")
    if ((@($releaseFirst.Keys) -join "`n") -cne
        ($expectedNames -join "`n")) {
        throw "Runtime cache environment variable topology differs."
    }
    foreach ($name in $expectedNames) {
        $releasePath = [string]$releaseFirst[$name]
        if ($releasePath -cne [string]$releaseRepeat[$name]) {
            throw "Identical Release build captures do not reuse '$name'."
        }
        if ([string]::Equals(
                $releasePath,
                [string]$shipping[$name],
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals(
                $releasePath,
                [string]$candidate[$name],
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime cache '$name' is shared across configuration or build identity."
        }
        if (-not (Test-PathContainedBy $releasePath $script:RunRoot)) {
            throw "Runtime cache '$name' is not a run-root child."
        }
        if ($name -notmatch 'SEED_DIRECTORY$' -and
            -not (Test-Path -LiteralPath $releasePath -PathType Container)) {
            throw "Runtime writable cache '$name' was not materialized."
        }
    }

    $workload = [pscustomobject]@{ id = "bistro-stationary" }
    $primeRoot = Get-RuntimeCachePrimeRoot `
        $releaseBuild "Release" $workload
    $reportPath = Join-Path $script:RunRoot "captures/reference.json"
    $captureRoot = Get-RuntimeCaptureCacheRoot $reportPath
    if (-not (Test-PathContainedBy $primeRoot $script:RunRoot) -or
        -not (Test-PathContainedBy $captureRoot $script:RunRoot) -or
        [System.IO.Path]::GetFileName($captureRoot) -cne
            "reference.json.runtime-cache") {
        throw "Runtime prime or per-capture cache path is not deterministic."
    }

    foreach ($invalidCase in @(
            @($releaseBuild, "../Release"),
            @([pscustomobject]@{
                BundleFingerprint = "directory:sha256:forged"
                RootPath = $script:RunRoot
            }, "Release"))) {
        $failedClosed = $false
        try {
            $null = Get-RuntimeCacheEnvironment $invalidCase[0] $invalidCase[1]
        } catch {
            $failedClosed = $true
        }
        if (-not $failedClosed) {
            throw "Invalid runtime cache identity did not fail closed."
        }
    }
    foreach ($invalidPathCase in @(
            { Get-RuntimeCachePrimeRoot $releaseBuild "Release" `
                ([pscustomobject]@{ id = "../forged" }) },
            { Get-RuntimeCaptureCacheRoot `
                (Join-Path (Split-Path -Parent $script:RunRoot) "outside.json") })) {
        $failedClosed = $false
        try {
            $null = & $invalidPathCase
        } catch {
            $failedClosed = $true
        }
        if (-not $failedClosed) {
            throw "Invalid runtime cache path did not fail closed."
        }
    }

    $syntheticVulkanCache = Join-Path `
        ([string]$releaseFirst.NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY) `
        "gi-000010de-00002560.njvkcache"
    [System.IO.File]::WriteAllBytes($syntheticVulkanCache, [byte[]]@(1))
    $syntheticBinaryStore = Join-Path `
        ([string]$releaseFirst.NJULF_PIPELINE_BINARY_CACHE_DIRECTORY) `
        "synthetic-global-key"
    New-Item -ItemType Directory -Path $syntheticBinaryStore | Out-Null
    $syntheticHealth = [pscustomobject]@{
        diagnostics = [pscustomobject]@{
            GiPipelineCacheLoaded = 1
            GiPipelineCacheRejected = 0
            GiPipelineCacheSaved = 1
            GiPipelineCacheLoadedPayloadBytes = 1
            GiPipelineCacheSavedPayloadBytes = 1
            GiPipelineCachePath = $syntheticVulkanCache
            GiPipelineBinaryStorePath = $syntheticBinaryStore
            GiPipelineCacheStatus =
                "Compatible cache loaded and refreshed."
        }
    }
    Assert-RuntimeCacheCapture `
        $syntheticHealth $releaseFirst "Synthetic current cache"
    $syntheticPrimeReport = [pscustomobject]@{
        Kind = "njulf-renderer-benchmark"
        Schema = "njulf-renderer-benchmark/v5"
        MeasurementFrameCount = 240
        LastDiagnostics = [pscustomobject]@{
            CaptureRun = [pscustomobject]@{
                Commit = [string]$releaseBuild.BuildCommit
                ExecutableHash =
                    [string]$releaseBuild.RuntimeExecutableBundleHash
            }
        }
    }
    $syntheticPrimeHealth = [pscustomobject]@{
        kind = "renderer-health"
        schema = "renderer-health/v3"
        diagnostics = $syntheticHealth.diagnostics
    }
    Assert-RuntimeCachePrimeCaptureEvidence `
        $syntheticPrimeReport $syntheticPrimeHealth `
        ([pscustomobject]@{ measureFrames = 240 }) $releaseBuild `
        ([string]$releaseBuild.BuildCommit) `
        ([string]$releaseFirst.NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY) `
        "Synthetic prime evidence"
    $syntheticHealth.diagnostics.GiPipelineCacheSaved = 0
    $syntheticHealth.diagnostics.GiPipelineCacheSavedPayloadBytes = 0
    $syntheticHealth.diagnostics.GiPipelineCachePath = Join-Path `
        ([string]$releaseFirst.NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY) `
        "gi-000010de-00002561.njvkcache"
    $syntheticHealth.diagnostics.GiPipelineCacheStatus =
        "Read-only pipeline cache seed: Compatible cache loaded."
    Assert-RuntimeCacheCapture `
        $syntheticHealth $releaseFirst "Synthetic frozen seed cache"
    $syntheticHealth.diagnostics.GiPipelineCacheStatus =
        "Compatible cache with stale or legacy provenance loaded and refreshed."
    $staleFailedClosed = $false
    try {
        Assert-RuntimeCacheCapture `
            $syntheticHealth $releaseFirst "Synthetic stale cache"
    } catch {
        $staleFailedClosed = $true
    }
    if (-not $staleFailedClosed) {
        throw "A measured capture admitted stale cache provenance."
    }

    $probeScript = Join-Path $caseRoot "environment-probe.ps1"
    $probeOutput = Join-Path $caseRoot "environment-probe.txt"
    [System.IO.File]::WriteAllText(
        $probeScript,
        @'
param([string]$OutputPath)
[System.IO.File]::WriteAllText(
    $OutputPath,
    [Environment]::GetEnvironmentVariable(
        "NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY"))
'@,
        [System.Text.UTF8Encoding]::new($false))
    Invoke-ProcessChecked `
        (Join-Path $PSHOME "pwsh.exe") `
        @("-NoProfile", "-NonInteractive", "-File", $probeScript,
            "-OutputPath", $probeOutput) `
        "Synthetic runtime cache environment" 30 $caseRoot @(0) $releaseFirst
    if ((Get-Content -LiteralPath $probeOutput -Raw) -cne
        [string]$releaseFirst.NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY) {
        throw "Runtime cache environment was not propagated to the child process."
    }
    Write-Host "PASS synthetic-runtime-cache-isolation"
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
        $result = $output | ConvertFrom-Json -DateKind String
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

function Invoke-SyntheticCookedAssetStagingCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the cooked asset staging test."
    }
    $requiredFunctions = @(
        "Get-Sha256", "Get-Sha256Bytes", "Get-Sha256Text",
        "Get-RuntimeExecutableBundleHash",
        "Read-BoundedFileBytes", "Assert-JsonObject", "Assert-Text",
        "Assert-ExactPropertyNames", "Assert-NoLinkedPathComponents",
        "Test-PathContainedBy", "Assert-NoDuplicateJsonProperties",
        "Read-StrictJsonFile", "Write-JsonArtifact",
        "Get-CookedAssetInventoryValue", "Resolve-CookedAssetBundle",
        "Initialize-CampaignHardLinkInterop", "Install-CookedAssetBundle",
        "Assert-CookedAssetStaging", "Get-BuildBundleFingerprint",
        "Assert-BuildIdentity")
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
    $sourceRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        "njulf-cooked-staging-test-" + [Guid]::NewGuid().ToString("N"))
    $buildRoot = Join-Path $testRoot "cooked-staging-build"
    try {
        $platformRoot = Join-Path $sourceRoot "win-x64"
        New-Item -ItemType Directory -Path (Join-Path $platformRoot "models") `
            -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $platformRoot "textures") `
            -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $platformRoot "reports") `
            -Force | Out-Null
        $sharedTexture = Join-Path $platformRoot "textures/shared.ktx2"
        Set-Content -LiteralPath $sharedTexture -Value "shared" -NoNewline
        $manifest = Get-Content -LiteralPath $sourceManifest -Raw |
            ConvertFrom-Json -DateKind String
        $hashIndex = 10
        foreach ($model in @($manifest.cookedAssets.requiredModels)) {
            $modelName = [string]$model
            $modelPath = Join-Path $platformRoot "models/$modelName.njmodel"
            Set-Content -LiteralPath $modelPath -Value "model-$modelName" -NoNewline
            $outputs = [ordered]@{}
            $outputs["models/$modelName.njmodel"] = $hashIndex
            $outputs["textures/shared.ktx2"] = 1
            $hashIndex++
            $report = [ordered]@{
                sourcePath = "synthetic/$modelName"
                assetId = [Guid]::NewGuid()
                status = "Succeeded"
                outputs = $outputs
            }
            Write-JsonArtifact `
                (Join-Path $platformRoot "reports/$modelName.cook-report.json") `
                $report
        }
        $script:SolutionRoot = $solutionRoot
        $script:RepoRoot = $solutionRoot
        $script:RunRoot = $testRoot
        $bundle = Resolve-CookedAssetBundle `
            $manifest $sourceRoot "Synthetic"
        if ([int]$bundle.Identity.fileCount -ne 11 -or
            [string]$bundle.Identity.identityHash -cnotmatch
                '^sha256:[0-9a-f]{64}$') {
            throw "Synthetic cooked asset identity is not canonical."
        }
        New-Item -ItemType Directory -Path $buildRoot | Out-Null
        Set-Content -LiteralPath (Join-Path $buildRoot "NjulfHelloGame.exe") `
            -Value "synthetic executable" -NoNewline
        $staging = Install-CookedAssetBundle $bundle $buildRoot "Synthetic"
        $script:CookedAssetBundle = $bundle
        Assert-CookedAssetStaging $staging $buildRoot "Synthetic"
        $fingerprintBefore = Get-BuildBundleFingerprint $buildRoot
        $syntheticExecutable = Join-Path $buildRoot "NjulfHelloGame.exe"
        $buildIdentity = [pscustomobject][ordered]@{
            RootPath = $buildRoot
            ExecutablePath = $syntheticExecutable
            ExecutableFileSha256 = Get-Sha256 $syntheticExecutable
            RuntimeExecutableBundleHash =
                Get-RuntimeExecutableBundleHash $syntheticExecutable
            BundleFingerprint = $fingerprintBefore
            CookedAssetBundle = $staging
            BuildCommit = "a" * 40
            ProjectPath = "NjulfHelloGame/NjulfHelloGame.csproj"
            SourceProvenance = "git-worktree-exact-commit"
            IntermediateIsolation = "dotnet-artifacts-path"
        }
        Assert-BuildIdentity $buildIdentity "Synthetic"
        $extraCooked = Join-Path $buildRoot "Cooked/win-x64/textures/extra.ktx2"
        Set-Content -LiteralPath $extraCooked -Value "extra" -NoNewline
        $failedClosed = $false
        try {
            Assert-CookedAssetStaging $staging $buildRoot "Synthetic tamper"
        } catch {
            $failedClosed = $true
        }
        if (-not $failedClosed) {
            throw "Cooked staging accepted an unexpected output."
        }
        Remove-Item -LiteralPath $extraCooked -Force
        Set-Content -LiteralPath (Join-Path $buildRoot "runtime.dll") `
            -Value "runtime" -NoNewline
        if ((Get-BuildBundleFingerprint $buildRoot) -ceq $fingerprintBefore) {
            throw "Build fingerprint ignored a non-cooked runtime mutation."
        }
        Write-Host "PASS cooked-asset-staging"
    } finally {
        if (Test-Path -LiteralPath $sourceRoot) {
            $fullSource = [System.IO.Path]::GetFullPath($sourceRoot)
            $tempRoot = [System.IO.Path]::GetFullPath(
                [System.IO.Path]::GetTempPath())
            if (-not $fullSource.StartsWith(
                    $tempRoot,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Synthetic cooked source escaped the temporary root."
            }
            Remove-Item -LiteralPath $fullSource -Recurse -Force
        }
    }
}

function Invoke-SyntheticFrozenVerifierReturnShapeCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the frozen verifier return-shape test."
    }
    foreach ($functionName in @(
            "Get-Sha256",
            "Assert-NoDuplicateJsonProperties",
            "ConvertFrom-FrozenVerifierBytes",
            "Stop-ProcessTreeAndDrain",
            "Invoke-FrozenVerifierProcess")) {
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
    function Assert-BuildIdentity {
        param($BuildIdentity, [string]$Label)
    }
    function Assert-CampaignLockIntegrity {}

    $inputPath = Join-Path $testRoot "frozen-verifier-input.json"
    [System.IO.File]::WriteAllText(
        $inputPath,
        "{}",
        [System.Text.UTF8Encoding]::new($false))
    $pwsh = (Get-Command pwsh).Source
    $build = [pscustomobject]@{
        RootPath = $testRoot
        ExecutablePath = $pwsh
    }
    $invocation = Invoke-FrozenVerifierProcess `
        $build `
        @(
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "[Console]::Out.Write('{`"kind`":`"synthetic`"}' + [Environment]::NewLine)") `
        @($inputPath) `
        @((Get-Sha256 $inputPath)) `
        "Synthetic frozen verifier" `
        30
    if (@($invocation).Count -ne 1 -or
        $null -eq $invocation.PSObject.Properties["Bytes"] -or
        $null -eq $invocation.PSObject.Properties["Result"] -or
        [string]$invocation.Result.kind -cne "synthetic") {
        throw "Frozen verifier invocation leaked task completion values into its return shape."
    }
    Write-Host "PASS frozen-verifier-return-shape"
}

function Invoke-SyntheticNonReflectionC3VerifierCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the non-reflection C3 test."
    }
    foreach ($functionName in @(
            "Get-PropertyValue",
            "Test-WorkloadUsesSponzaAnimation",
            "Test-Sha256Identity",
            "Assert-PathIdentity",
            "Assert-ResultReportIdentity",
            "Assert-SponzaAnimationVerifierIdentity",
            "Assert-TimingActivationVerifierResult")) {
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

    $reportPath = Join-Path $testRoot "non-reflection-c3-report.json"
    $reportSha256 = -join ("b" * 64)
    $fingerprint = "sha256:" + (-join ("c" * 64))
    $workload = [pscustomobject]@{
        scene = "Bistro"
        activation = "none"
        measureFrames = 240
    }
    $report = [pscustomobject]@{
        ActivationEvidence = [pscustomobject]@{
            Fingerprint = $fingerprint
            ActivationStructuralSequenceHash = "unavailable"
            ActivationExecutionSequenceHash = "unavailable"
        }
        SponzaSceneAnimationEvidence = [pscustomobject]@{
            Fingerprint = $fingerprint
            Mode = 0
        }
    }
    $result = [pscustomobject]@{
        reportPath = $reportPath
        reportSha256 = $reportSha256
        activation = "none"
        activationFingerprint = $fingerprint
        activationStructuralSequenceHash = "unavailable"
        activationExecutionSequenceHash = "unavailable"
        reflectionProbeCaptureEvidenceDigest = "sha256:" + (-join ("d" * 64))
        reflectionProbeCaptureRawRowCount = 0
        reflectionProbeCaptureResultRowCount = 0
        sponzaSceneAnimationFingerprint = $fingerprint
        sponzaSceneAnimationMode = 0
        sponzaSceneAnimationConfigurationFingerprint = "unavailable"
        sponzaSceneAnimationSequenceHash = "unavailable"
        sponzaSceneAnimationSidecarPath = ""
        sponzaSceneAnimationSidecarSha256 = ""
    }
    $sidecar = Assert-TimingActivationVerifierResult `
        $workload $report $result $reportPath $reportSha256 `
        "Synthetic non-reflection C3"
    if ([string]$sidecar.path -cne "" -or [string]$sidecar.sha256 -cne "") {
        throw "Non-reflection C3 admission returned a noncanonical sidecar."
    }

    $result.reflectionProbeCaptureEvidenceDigest = "unavailable"
    $failedClosed = $false
    try {
        $null = Assert-TimingActivationVerifierResult `
            $workload $report $result $reportPath $reportSha256 `
            "Synthetic non-reflection C3 invalid digest"
    } catch {
        $failedClosed = $_.Exception.Message -like
            "*non-reflection C3 evidence is not canonical unavailable*"
    }
    if (-not $failedClosed) {
        throw "Non-reflection C3 admission accepted an unavailable digest."
    }
    Write-Host "PASS non-reflection-c3-verifier-contract"
}

function Invoke-SyntheticSceneProfileArgumentCase {
    $tokens = $null
    $parseErrors = $null
    $driverAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $driver, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Campaign driver did not parse for the scene-profile argument test."
    }
    foreach ($functionName in @(
            "Get-PropertyValue",
            "Get-BenchmarkArguments",
            "Get-QualitySequenceArguments")) {
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

    $manifest = [pscustomobject]@{
        capture = [pscustomobject]@{
            maximumSettlingFrames = 4096
            budgetProfile = "stress"
        }
        quality = [pscustomobject]@{
            maximumRelativeRmse = 0.005
            maximumFlipP95 = 0.02
        }
        qualitySequence = [pscustomobject]@{
            maximumReadbackDrainFrames = 120
        }
    }
    foreach ($scene in @("Bistro", "Sponza")) {
        $workload = [pscustomobject]@{
            scene = $scene
            scenario = "Synthetic"
            warmupFrames = 480
            measureFrames = 240
            captureVariant = "baseline"
            trajectory = if ($scene -eq "Bistro") {
                "bistro-loop"
            } else {
                "sponza-horizontal"
            }
            qualityTrajectory = if ($scene -eq "Bistro") {
                "bistro-loop"
            } else {
                "sponza-horizontal"
            }
            activation = "none"
            bistroQualityVariant = ""
            arguments = @()
        }
        $referenceTiming = @(Get-BenchmarkArguments `
            $manifest $workload "report.json" "health.json" "pair" `
            "quality.json" "reference.pfm" $true)
        $timing = @(Get-BenchmarkArguments `
            $manifest $workload "report.json" "health.json" "pair" `
            "quality.json" "reference.pfm" $false)
        $quality = @(Get-QualitySequenceArguments `
            $manifest $workload "canonical" "sequence" "quality-report.json" `
            "quality-health.json" "quality-output" "reference.json" `
            "quality.json")
        if ($timing -contains "--quality-preset" -or
            $quality -contains "--quality-preset") {
            throw "$scene benchmark arguments overwrite its authored scene profile."
        }
        if ($timing -notcontains "--benchmark-activation" -or
            $timing -contains "--benchmark-quality-sequence-activation") {
            throw "$scene timing arguments do not own the timing activation namespace."
        }
        if ($referenceTiming -contains "--benchmark-require-1080p60" -or
            $timing -notcontains "--benchmark-require-1080p60") {
            throw "$scene reference and candidate timing do not separate the final target gate."
        }
        if ($quality -notcontains "--benchmark-quality-sequence-activation" -or
            $quality -contains "--benchmark-activation" -or
            $quality -contains "--benchmark") {
            throw "$scene quality arguments mix timing and quality-sequence modes."
        }
    }
    Write-Host "PASS synthetic-scene-profile-arguments"
}

try {
    Invoke-SyntheticManifestSnapshotCase
    Invoke-SyntheticHealthReportCase
    Invoke-SyntheticQualityHealthBudgetCase
    Invoke-SyntheticAcceptanceRefCase
    Invoke-SyntheticVerifierByteContractCase
    Invoke-SyntheticFrozenVerifierReturnShapeCase
    Invoke-SyntheticNonReflectionC3VerifierCase
    Invoke-SyntheticSceneProfileArgumentCase
    Invoke-SyntheticQualityAnimationContractCase
    Invoke-SyntheticComparisonContractCase
    Invoke-SyntheticQualitySequencePolicyCase
    Invoke-SyntheticHotspotDiscoveryCase
    Invoke-SyntheticCandidateEnvelopeCase
    Invoke-SyntheticCookedAssetStagingCase
    Invoke-ProcessTimeoutContainmentCase
    Invoke-SyntheticBuildPathBudgetCase
    Invoke-SyntheticShaderCacheWiringCase
    Invoke-SyntheticRuntimeCacheIsolationCase
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
    Invoke-ManifestCase "workload-order-change" {
        param($manifest)
        $first = $manifest.workloads[0]
        $manifest.workloads[0] = $manifest.workloads[1]
        $manifest.workloads[1] = $first
    } $false
    Invoke-ManifestCase "activation-topology-change" {
        param($manifest)
        $manifest.workloads[8].activation = "none"
    } $false
    Invoke-ManifestCase "wrong-cooked-platform" {
        param($manifest)
        $manifest.cookedAssets.platform = "linux-x64"
    } $false
    Invoke-ManifestCase "missing-cooked-model" {
        param($manifest)
        $manifest.cookedAssets.requiredModels = @(
            $manifest.cookedAssets.requiredModels | Select-Object -First 3)
    } $false
    Invoke-ManifestCase "wrong-cooked-file-bound" {
        param($manifest)
        $manifest.cookedAssets.maximumFiles = 4096
    } $false
    Invoke-ManifestCase "wrong-cooked-byte-bound" {
        param($manifest)
        $manifest.cookedAssets.maximumBytes = 8589934592
    } $false
    Invoke-ManifestCase "cooked-assets-unknown-property" {
        param($manifest)
        $manifest.cookedAssets | Add-Member `
            -NotePropertyName allowSourceFallback `
            -NotePropertyValue $true
    } $false
    Invoke-ManifestCase "quality-trajectory-topology-change" {
        param($manifest)
        $manifest.workloads[8].qualityTrajectory = "sponza-low"
    } $false
    Invoke-ManifestCase "reserved-argument" {
        param($manifest)
        $manifest.workloads[0] | Add-Member `
            -NotePropertyName arguments `
            -NotePropertyValue @("--benchmark-measure-frames", "1")
    } $false
    Invoke-ManifestCase "unapproved-workload-argument" {
        param($manifest)
        $manifest.workloads[0] | Add-Member `
            -NotePropertyName arguments `
            -NotePropertyValue @("--simple-ddgi-scheduler-mode", "cpu-reference")
    } $false
    Invoke-ManifestCase "nonfinite-quality-threshold" {
        param($manifest)
        $manifest.quality.maximumRelativeRmse = "NaN"
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
    foreach ($reservedVerifierSwitch in @(
            "--benchmark-activation",
            "--verify-benchmark-activation-report",
            "--verify-benchmark-ddgi-transient-report",
            "--verify-benchmark-quality-activation-report",
            "--verify-directional-controlled-isolation")) {
        $caseName = "reserved-" +
            $reservedVerifierSwitch.TrimStart('-').Replace('-', '_')
        Invoke-ManifestCase $caseName {
            param($manifest)
            $manifest.workloads[0] | Add-Member `
                -NotePropertyName arguments `
                -NotePropertyValue @($reservedVerifierSwitch, "forged.json")
        } $false
    }
    Invoke-ManifestCase "missing-target-hypothesis" {
        param($manifest)
        $manifest.targetHypotheses = @(
            $manifest.targetHypotheses | Select-Object -First 3)
    } $false
    Invoke-ManifestCase "target-hypothesis-order-change" {
        param($manifest)
        $first = $manifest.targetHypotheses[0]
        $manifest.targetHypotheses[0] = $manifest.targetHypotheses[1]
        $manifest.targetHypotheses[1] = $first
    } $false
    Invoke-ManifestCase "target-hypothesis-pass-change" {
        param($manifest)
        $manifest.targetHypotheses[0].targetPass = "ForwardPlusPass"
    } $false
    Invoke-ManifestCase "target-hypothesis-claim-change" {
        param($manifest)
        $manifest.targetHypotheses[1].claims[1].workloadId =
            "sponza-vertical-motion"
    } $false
    Invoke-ManifestCase "target-hypothesis-unknown-property" {
        param($manifest)
        $manifest.targetHypotheses[0] | Add-Member `
            -NotePropertyName workloadId `
            -NotePropertyValue "bistro-forward-gi-enabled"
    } $false
    Invoke-ManifestCase "missing-reviewed-candidate" {
        param($manifest)
        $manifest.candidates = @($manifest.candidates | Select-Object -Skip 1)
    } $false
    Invoke-ManifestCase "reviewed-candidate-patch-change" {
        param($manifest)
        $manifest.candidates[0].patchId = "0" * 40
    } $false
    Invoke-ManifestCase "reviewed-candidate-path-change" {
        param($manifest)
        $manifest.candidates[0].allowedPaths[0] = "Njulf.Shaders/forged.comp"
    } $false
    Invoke-ManifestCase "discovery-threshold-change" {
        param($manifest)
        $manifest.discoveryPolicy.minimumP95Milliseconds = 0.5
    } $false
    foreach ($requiredTrustRoot in @(
            "NjulfHelloGame/SampleBenchmarkGateEvaluation.cs",
            "NjulfHelloGame/SampleBudgetMetricCoverage.cs",
            "NjulfHelloGame/SampleDdgiProductionGate.cs",
            "NjulfHelloGame/SampleDdgiBenchmarkSuite.cs",
            "NjulfHelloGame/SampleAssetManifest.cs",
            "NjulfHelloGame/SampleAssetValidationGate.cs",
            "Njulf.Assets/ContentManager.cs",
            "Njulf.Assets/Cooked",
            "Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs",
            "tools/perf-campaign.tests.ps1")) {
        $caseName = "missing-trust-" +
            ([System.IO.Path]::GetFileNameWithoutExtension($requiredTrustRoot))
        Invoke-ManifestCase $caseName {
            param($manifest)
            $manifest.protectedPaths = @($manifest.protectedPaths | Where-Object {
                [string]$_ -cne $requiredTrustRoot
            })
        } $false
    }
    $unknownHypothesisRun = Join-Path $testRoot "unknown-hypothesis-run"
    $unknownHypothesisOutput = & pwsh -NoProfile -NonInteractive `
        -File $driver `
        -ManifestPath $sourceManifest `
        -RunDirectory $unknownHypothesisRun `
        -TargetHypothesisId "not-an-approved-hypothesis" `
        -ValidateOnly 2>&1
    if ($LASTEXITCODE -eq 0 -or
        (Test-Path -LiteralPath $unknownHypothesisRun) -or
        ($unknownHypothesisOutput -join "`n") -notmatch
            "TargetHypothesisId is derived from the admitted candidate") {
        throw "Explicit target hypothesis did not fail before run-directory mutation."
    }
    Write-Host "PASS unknown-target-hypothesis-preflight"
    $modeConflictRun = Join-Path $testRoot "mode-conflict-run"
    $modeConflictOutput = & pwsh -NoProfile -NonInteractive -File $driver `
        -ManifestPath $sourceManifest `
        -RunDirectory $modeConflictRun `
        -CandidateId "ao-center-depth-reuse" `
        -ValidateOnly 2>&1
    if ($LASTEXITCODE -eq 0 -or
        (Test-Path -LiteralPath $modeConflictRun) -or
        ($modeConflictOutput -join "`n") -notmatch "Choose exactly one campaign mode") {
        throw "Conflicting campaign modes did not fail before run-directory mutation."
    }
    Write-Host "PASS campaign-mode-conflict-preflight"
    $externalManifest = Join-Path $testRoot "external-manifest.json"
    Copy-Item -LiteralPath $sourceManifest -Destination $externalManifest
    $externalRun = Join-Path $testRoot "external-manifest-run"
    $externalOutput = & pwsh -NoProfile -NonInteractive -File $driver `
        -ManifestPath $externalManifest `
        -RunDirectory $externalRun `
        -ValidateOnly 2>&1
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $externalRun) -or
        ($externalOutput -join "`n") -notmatch "only the built-in pinned manifest") {
        throw "External manifest path did not fail before run-directory mutation."
    }
    Write-Host "PASS pinned-manifest-path-preflight"
    $wrapperRunPath = Join-Path $testRoot "wrapper-run"
    $wrapperOutput = & pwsh -NoProfile -NonInteractive -File $wrapper `
        -CampaignManifestPath $sourceManifest `
        -CampaignRunDirectory $wrapperRunPath `
        -CampaignCookedAssetRoot (Join-Path $testRoot "unused-cooked") `
        -ValidateCampaign 2>&1
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $wrapperRunPath)) {
        throw "perf-loop campaign dispatch failed or mutated ValidateOnly state.`n$($wrapperOutput -join "`n")"
    }
    Write-Host "PASS perf-loop-dispatch"
    $wrapperConflictRun = Join-Path $testRoot "wrapper-conflict-run"
    $wrapperConflictOutput = & pwsh -NoProfile -NonInteractive -File $wrapper `
        -CampaignManifestPath $sourceManifest `
        -CampaignRunDirectory $wrapperConflictRun `
        -TrialCommand "Write-Output forged" `
        -ValidateCampaign 2>&1
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $wrapperConflictRun) -or
        ($wrapperConflictOutput -join "`n") -notmatch "rejects explicitly bound legacy parameters") {
        throw "Campaign wrapper accepted a legacy trial command."
    }
    Write-Host "PASS perf-loop-legacy-mode-conflict"
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
