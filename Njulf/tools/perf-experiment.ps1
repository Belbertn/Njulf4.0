[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SpecPath,
    [string]$RunDirectory = ".perf-loop-runs/experiments",
    [switch]$ValidateOnly,
    [switch]$AnalyzeOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$script:SolutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:ManifestPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "perf-campaign.bistro-sponza.json"))
$script:CampaignDriverPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "perf-campaign.ps1"))
$script:RunBase = if ([System.IO.Path]::IsPathRooted($RunDirectory)) {
    [System.IO.Path]::GetFullPath($RunDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $RunDirectory))
}

function Get-Sha256 {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot hash missing file '$Path'."
    }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    $json = $Value | ConvertTo-Json -Depth 64
    [System.IO.File]::WriteAllText(
        $Path,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Read-JsonFile {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -DateKind String
}

function Get-PropertyValue {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Assert-ExactProperties {
    param($Object, [string[]]$Names, [string]$Label)
    if ($null -eq $Object) { throw "$Label is missing." }
    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $expected = @($Names | Sort-Object)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Label properties differ. Expected [$($expected -join ', ')], found [$($actual -join ', ')]."
    }
}

function Assert-SafeIdentifier {
    param([string]$Value, [string]$Label)
    if ($Value -cnotmatch '^[a-z0-9][a-z0-9._-]{0,95}$') {
        throw "$Label must be a lowercase filesystem-safe identifier."
    }
}

function Get-GitText {
    param([string]$SourceRoot, [string[]]$Arguments)
    $output = & git -C $SourceRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git read failed in '$SourceRoot': $($output -join [Environment]::NewLine)"
    }
    return (($output -join "`n").Trim())
}

function Assert-SourceIdentity {
    param($Variant, [string]$Label)
    $root = [System.IO.Path]::GetFullPath([string]$Variant.sourceRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $root "Njulf.sln") -PathType Leaf)) {
        throw "$Label sourceRoot must contain Njulf.sln: $root"
    }
    if ([string]$Variant.commit -cnotmatch '^[0-9a-f]{40}$') {
        throw "$Label commit must be an exact lowercase SHA-1."
    }
    $actual = Get-GitText $root @("rev-parse", "HEAD")
    if ($actual -cne [string]$Variant.commit) {
        throw "$Label source HEAD '$actual' differs from '$($Variant.commit)'."
    }
    $status = Get-GitText $root @("status", "--short")
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "$Label source must be clean before qualification.`n$status"
    }
    return $root
}

function Assert-VariantArguments {
    param([object[]]$Arguments, [string]$Label)
    $reserved = @(
        "--benchmark", "--benchmark-report", "--health-report",
        "--benchmark-warmup-frames", "--benchmark-measure-frames",
        "--benchmark-max-settle-frames", "--benchmark-pair-id",
        "--benchmark-variant", "--benchmark-trajectory",
        "--benchmark-activation", "--benchmark-budget-profile",
        "--benchmark-require-1080p60", "--scene", "--performance-scenario",
        "--validation", "--gpu-timing", "--benchmark-hdr-reference",
        "--benchmark-hdr-candidate", "--benchmark-hdr-max-relative-rmse",
        "--benchmark-hdr-max-flip-p95", "--benchmark-hdr-quality-contract",
        "--benchmark-require-production")
    foreach ($argument in @($Arguments)) {
        $text = [string]$argument
        if ([string]::IsNullOrWhiteSpace($text)) {
            throw "$Label contains an empty runtime argument."
        }
        $name = $text.Split('=', 2)[0]
        if ($reserved -contains $name) {
            throw "$Label may not override frozen argument '$name'."
        }
    }
}

function Assert-VariantEnvironment {
    param($Environment, [string]$Label)
    if ($null -eq $Environment) {
        throw "$Label is missing."
    }
    foreach ($property in @($Environment.PSObject.Properties)) {
        $name = [string]$property.Name
        if ($name -cnotmatch '^NJULF_[A-Z0-9_]{1,121}$') {
            throw "$Label contains unsupported variable '$name'; only uppercase NJULF_ variables are allowed."
        }
        if ($null -eq $property.Value -or
            $property.Value -isnot [string] -or
            ([string]$property.Value).Contains([char]0)) {
            throw "$Label variable '$name' must have a non-null string value without NUL characters."
        }
    }
}

function Get-CanonicalEnvironmentJson {
    param($Environment)
    $ordered = [ordered]@{}
    foreach ($property in @($Environment.PSObject.Properties |
            Sort-Object Name)) {
        $ordered[[string]$property.Name] = [string]$property.Value
    }
    return ($ordered | ConvertTo-Json -Compress)
}

function Assert-ExperimentSpec {
    param($Spec, $Manifest)
    Assert-ExactProperties $Spec @(
        "schema", "experimentId", "mode", "campaignRunDirectory",
        "cookedAssetRoot", "baseline", "candidate", "configurations",
        "claims", "acceptanceMode", "focusedTestFilter") "Experiment spec"
    if ([string]$Spec.schema -cne "njulf-perf-experiment/v1") {
        throw "Unsupported experiment spec '$($Spec.schema)'."
    }
    Assert-SafeIdentifier ([string]$Spec.experimentId) "experimentId"
    if ([string]$Spec.mode -cnotin @("aa", "ab")) {
        throw "Experiment mode must be 'aa' or 'ab'."
    }
    if ([string]$Spec.acceptanceMode -cnotin @(
            "manifest-either", "frame-and-pass", "pass-only", "loop-frame-1ms")) {
        throw "Unsupported acceptanceMode '$($Spec.acceptanceMode)'."
    }
    foreach ($phase in @("baseline", "candidate")) {
        $variant = $Spec.$phase
        Assert-ExactProperties $variant @(
            "sourceRoot", "commit", "arguments", "workloadArguments",
            "environment") "$phase variant"
        Assert-VariantArguments @($variant.arguments) "$phase arguments"
        Assert-VariantEnvironment $variant.environment "$phase environment"
        foreach ($property in @($variant.workloadArguments.PSObject.Properties)) {
            Assert-VariantArguments @($property.Value) "$phase workload '$($property.Name)' arguments"
        }
    }
    if ([string]$Spec.mode -ceq "aa") {
        if ([string]$Spec.baseline.commit -cne [string]$Spec.candidate.commit -or
            ((@($Spec.baseline.arguments) | ConvertTo-Json -Compress) -cne
             (@($Spec.candidate.arguments) | ConvertTo-Json -Compress)) -or
            ((Get-CanonicalEnvironmentJson $Spec.baseline.environment) -cne
             (Get-CanonicalEnvironmentJson $Spec.candidate.environment)) -or
            (($Spec.baseline.workloadArguments | ConvertTo-Json -Depth 8 -Compress) -cne
             ($Spec.candidate.workloadArguments | ConvertTo-Json -Depth 8 -Compress))) {
            throw "A/A mode requires identical commits, runtime arguments, and environment."
        }
    }
    $expectedConfigurations = @($Manifest.finalConfigurations | ForEach-Object { [string]$_ })
    $actualConfigurations = @($Spec.configurations | ForEach-Object { [string]$_ })
    if ($actualConfigurations.Count -eq 0 -or
        @($actualConfigurations | Select-Object -Unique).Count -ne $actualConfigurations.Count -or
        @($actualConfigurations | Where-Object { $_ -notin $expectedConfigurations }).Count -ne 0) {
        throw "configurations must be a unique non-empty subset of the final manifest configurations."
    }
    if (@($Spec.claims).Count -eq 0) { throw "At least one target claim is required." }
    $claimKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($claim in @($Spec.claims)) {
        Assert-ExactProperties $claim @("workloadId", "targetDomain", "targetPass") "Target claim"
        $workload = @($Manifest.workloads | Where-Object {
            [string]$_.id -ceq [string]$claim.workloadId
        })
        if ($workload.Count -ne 1) { throw "Unknown claim workload '$($claim.workloadId)'." }
        if ([string]$claim.targetDomain -cnotin @("gpu", "cpu") -or
            [string]::IsNullOrWhiteSpace([string]$claim.targetPass)) {
            throw "Claim '$($claim.workloadId)' has an invalid target timing."
        }
        if (-not $claimKeys.Add("$($claim.workloadId)`0$($claim.targetDomain)`0$($claim.targetPass)")) {
            throw "Target claim is duplicated for '$($claim.workloadId)'."
        }
    }
    foreach ($variant in @($Spec.baseline, $Spec.candidate)) {
        foreach ($name in @($variant.workloadArguments.PSObject.Properties |
                ForEach-Object { [string]$_.Name })) {
            if (@($Manifest.workloads | Where-Object { [string]$_.id -ceq $name }).Count -ne 1) {
                throw "Variant arguments reference unknown workload '$name'."
            }
        }
    }
}

function Invoke-CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$LogPath,
        [int]$TimeoutSeconds,
        [string]$Label,
        $Environment = $null,
        [int[]]$AllowedExitCodes = @(0))
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $environmentLog = @()
    if ($null -ne $Environment) {
        foreach ($property in @($Environment.PSObject.Properties |
                Sort-Object Name)) {
            $name = [string]$property.Name
            $value = [string]$property.Value
            $info.Environment[$name] = $value
            $environmentLog += "$name=$value"
        }
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    Write-Host $Label
    try {
        if (-not $process.Start()) { throw "$Label failed to start." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Label timed out after $TimeoutSeconds seconds."
        }
        $outText = $stdout.GetAwaiter().GetResult()
        $errText = $stderr.GetAwaiter().GetResult()
        $log = "COMMAND: $FilePath $($Arguments -join ' ')`nENVIRONMENT: $($environmentLog -join '; ')`nEXIT: $($process.ExitCode)`nSTDOUT:`n$outText`nSTDERR:`n$errText"
        [System.IO.File]::WriteAllText($LogPath, $log, [System.Text.UTF8Encoding]::new($false))
        if ($process.ExitCode -notin $AllowedExitCodes) {
            throw "$Label failed with exit code $($process.ExitCode); see $LogPath"
        }
    } finally {
        $process.Dispose()
    }
}

function Assert-PinnedCampaign {
    $output = & pwsh -NoProfile -File $script:CampaignDriverPath `
        -ManifestPath $script:ManifestPath -ValidateOnly 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Pinned campaign validation failed: $($output -join [Environment]::NewLine)"
    }
}

function Get-CookedFiles {
    param($Manifest, [string]$CookedAssetRoot)
    $platformRoot = Join-Path ([System.IO.Path]::GetFullPath($CookedAssetRoot)) ([string]$Manifest.cookedAssets.platform)
    if (-not (Test-Path -LiteralPath $platformRoot -PathType Container)) {
        throw "Cooked platform root is missing: $platformRoot"
    }
    $files = [ordered]@{}
    foreach ($model in @($Manifest.cookedAssets.requiredModels)) {
        $reportRelative = "reports/$model.cook-report.json"
        $reportPath = Join-Path $platformRoot $reportRelative.Replace('/', '\')
        $report = Read-JsonFile $reportPath "Cook report '$model'"
        if ([string]$report.status -cne "Succeeded") { throw "Cook report '$model' is not successful." }
        foreach ($property in @($report.outputs.PSObject.Properties)) {
            $relative = [string]$property.Name
            if ([System.IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)') {
                throw "Cook output path is unsafe: '$relative'."
            }
            $source = Join-Path $platformRoot $relative.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Cook output is missing: $source"
            }
            $files[$relative] = $source
        }
        $files[$reportRelative] = $reportPath
    }
    if ($files.Count -gt [int]$Manifest.cookedAssets.maximumFiles) {
        throw "Cooked bundle exceeds the manifest file bound."
    }
    return $files
}

function Install-CookedFiles {
    param($Files, [string]$BuildRoot)
    $targetRoot = Join-Path $BuildRoot "Cooked\win-x64"
    New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
    [long]$bytes = 0
    $inventory = @()
    foreach ($entry in $Files.GetEnumerator()) {
        $relative = [string]$entry.Key
        $source = [string]$entry.Value
        $target = Join-Path $targetRoot $relative.Replace('/', '\')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        try {
            New-Item -ItemType HardLink -Path $target -Target $source -ErrorAction Stop | Out-Null
        } catch {
            [System.IO.File]::Copy($source, $target, $false)
        }
        $length = [long](Get-Item -LiteralPath $target).Length
        $bytes += $length
        $inventory += [ordered]@{ relativePath = $relative; length = $length; sha256 = Get-Sha256 $target }
    }
    Write-JsonFile (Join-Path $BuildRoot "experiment-cooked-assets.json") ([ordered]@{
        schema = "njulf-perf-experiment-cooked-assets/v1"
        fileCount = $inventory.Count
        totalBytes = $bytes
        files = $inventory
    })
}

function New-VariantBuild {
    param($Manifest, $Variant, [string]$Configuration, [string]$Phase, [string]$Root, $CookedFiles)
    $sourceRoot = Assert-SourceIdentity $Variant $Phase
    $output = Join-Path $Root "build\$Configuration\$Phase"
    $artifacts = Join-Path $Root "intermediate\$Configuration\$Phase"
    if (Test-Path -LiteralPath $output) { throw "Build output already exists: $output" }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $project = Join-Path $sourceRoot ([string]$Manifest.projectPath)
    $props = Join-Path $sourceRoot "Directory.Build.props"
    $args = @(
        "build", $project, "-c", $Configuration, "-o", $output,
        "--artifacts-path", $artifacts, "--no-incremental", "--nologo",
        "-p:RestoreLockedMode=true", "-p:UseSharedCompilation=false",
        "-p:ImportDirectoryBuildTargets=false", "-p:DirectoryBuildPropsPath=$props",
        "-p:NjulfShaderCacheDirectory=$(Join-Path $script:SolutionRoot 'artifacts\shader-cache\v1')",
        "-nodeReuse:false")
    $log = Join-Path $Root "logs\build-$Configuration-$Phase.log"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
    Invoke-CheckedProcess "dotnet" $args $sourceRoot $log 1800 "$Configuration $Phase build"
    if (-not [string]::IsNullOrWhiteSpace([string]$Spec.focusedTestFilter)) {
        $testLog = Join-Path $Root "logs\test-$Configuration-$Phase.log"
        Invoke-CheckedProcess "dotnet" @(
            "test", (Join-Path $sourceRoot "Njulf.Tests\Njulf.Tests.csproj"),
            "-c", $Configuration, "--artifacts-path", $artifacts, "--nologo",
            "--filter", [string]$Spec.focusedTestFilter,
            "--logger", "console;verbosity=minimal", "-p:RestoreLockedMode=true",
            "-p:UseSharedCompilation=false", "-p:ImportDirectoryBuildTargets=false",
            "-p:DirectoryBuildPropsPath=$props", "-nodeReuse:false") `
            $sourceRoot $testLog 3600 "$Configuration $Phase focused tests"
    }
    Install-CookedFiles $CookedFiles $output
    $executable = Join-Path $output "NjulfHelloGame.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "$Configuration $Phase build did not produce NjulfHelloGame.exe."
    }
    return [pscustomobject][ordered]@{
        phase = $Phase
        configuration = $Configuration
        commit = [string]$Variant.commit
        sourceRoot = $sourceRoot
        rootPath = [System.IO.Path]::GetFullPath($output)
        executablePath = [System.IO.Path]::GetFullPath($executable)
        executableSha256 = Get-Sha256 $executable
    }
}

function Get-WorkloadArguments {
    param($Manifest, $Workload, $Variant, [string]$ReportPath, [string]$HealthPath,
        [string]$PairId, $ReferenceEntry)
    $arguments = @(
        "--benchmark", "--benchmark-report", $ReportPath,
        "--health-report", $HealthPath,
        "--benchmark-warmup-frames", ([int]$Workload.warmupFrames).ToString(),
        "--benchmark-measure-frames", ([int]$Workload.measureFrames).ToString(),
        "--benchmark-max-settle-frames", ([int]$Manifest.capture.maximumSettlingFrames).ToString(),
        "--benchmark-pair-id", $PairId,
        "--benchmark-variant", ([string]$Workload.captureVariant),
        "--benchmark-trajectory", ([string]$Workload.trajectory),
        "--benchmark-activation", ([string]$Workload.activation),
        "--benchmark-budget-profile", ([string]$Manifest.capture.budgetProfile),
        "--scene", ([string]$Workload.scene),
        "--performance-scenario", ([string]$Workload.scenario),
        "--validation", "off", "--gpu-timing")
    $bistro = [string](Get-PropertyValue $Workload "bistroQualityVariant" "")
    if (-not [string]::IsNullOrWhiteSpace($bistro)) { $arguments += @("--bistro-quality-variant", $bistro) }
    $sponza = [string](Get-PropertyValue $Workload "sponzaFixtureMode" "")
    if (-not [string]::IsNullOrWhiteSpace($sponza)) { $arguments += @("--sponza-fixture-mode", $sponza) }
    $arguments += @((Get-PropertyValue $Workload "arguments" @()) | ForEach-Object { [string]$_ })
    $candidatePath = [System.IO.Path]::ChangeExtension($ReportPath, ".hdr.pfm")
    $arguments += @(
        "--benchmark-hdr-reference", ([string]$ReferenceEntry.path),
        "--benchmark-hdr-candidate", $candidatePath,
        "--benchmark-hdr-max-relative-rmse", ([double]$Manifest.quality.maximumRelativeRmse).ToString([Globalization.CultureInfo]::InvariantCulture),
        "--benchmark-hdr-max-flip-p95", ([double]$Manifest.quality.maximumFlipP95).ToString([Globalization.CultureInfo]::InvariantCulture),
        "--benchmark-hdr-quality-contract", ([string]$ReferenceEntry.qualityContractPath),
        "--benchmark-require-production")
    $arguments += @($Variant.arguments | ForEach-Object { [string]$_ })
    $specific = $Variant.workloadArguments.PSObject.Properties[[string]$Workload.id]
    if ($null -ne $specific) { $arguments += @($specific.Value | ForEach-Object { [string]$_ }) }
    return $arguments
}

function Get-ReferenceEntry {
    param($Lock, [string]$Configuration, [string]$WorkloadId)
    $configurationProperty = $Lock.references.PSObject.Properties[$Configuration]
    if ($null -eq $configurationProperty) { throw "Campaign lock lacks $Configuration references." }
    $entry = $configurationProperty.Value.PSObject.Properties[$WorkloadId]
    if ($null -eq $entry) { throw "Campaign lock lacks $Configuration/$WorkloadId reference." }
    foreach ($pair in @(
            @([string]$entry.Value.path, [string]$entry.Value.sha256),
            @([string]$entry.Value.qualityContractPath, [string]$entry.Value.qualityContractSha256))) {
        if ((Get-Sha256 $pair[0]) -cne $pair[1]) { throw "Locked reference changed: $($pair[0])" }
    }
    return $entry.Value
}

function Get-PretargetOperationalFindings {
    param($Manifest, $Report, $Health, [string]$Label)
    $allowedBudgetNames = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($name in @(
            "DDGI Topology certified latency",
            "DDGI Topology first-visible latency",
            "DDGI total memory")) {
        [void]$allowedBudgetNames.Add($name)
    }

    if ([bool]$Report.Options.RequireRealtime1080p60Target) {
        throw "$Label unexpectedly enabled the final 1080p60 gate during pre-target measurement."
    }
    if ([int]$Report.MeasurementFrameCount -le 0 -or
        [int]$Report.GpuTimingSupported -ne 1 -or
        [int]$Report.GpuTimingValidSampleCount -ne [int]$Report.MeasurementFrameCount -or
        [int]$Report.GpuFrameMilliseconds.Count -ne [int]$Report.MeasurementFrameCount -or
        [bool]$Report.SettlingWaitTimedOut) {
        throw "$Label lacks complete settled GPU timing."
    }
    if ($null -eq $Report.CaptureContract -or
        -not [bool]$Report.CaptureContract.Comparable -or
        @($Report.CaptureContract.Mismatches).Count -ne 0) {
        throw "$Label is not a comparable capture."
    }
    if ($null -ne $Report.DdgiProductionGate -and
        -not [bool]$Report.DdgiProductionGate.Passed) {
        throw "$Label failed the DDGI production gate."
    }

    $overBudget = @($Report.BudgetMetrics | Where-Object { [int]$_.Status -eq 3 })
    $unexpectedBudget = @($overBudget | Where-Object {
            -not $allowedBudgetNames.Contains([string]$_.Name)
        })
    if ($unexpectedBudget.Count -ne 0) {
        throw "$Label exceeded an unapproved operational budget: $(@($unexpectedBudget.Name) -join ', ')."
    }

    $giErrors = @($Health.diagnostics.GiWarnings | Where-Object {
            [string]$_.Severity -ceq "Error"
        })
    $ddgiMemoryExceeded = @($overBudget | Where-Object {
            [string]$_.Name -ceq "DDGI total memory"
        }).Count -eq 1
    $unexpectedGiErrors = @($giErrors | Where-Object {
            [string]$_.Code -cne "GiBudgetOverrun" -or -not $ddgiMemoryExceeded
        })
    if ($unexpectedGiErrors.Count -ne 0) {
        throw "$Label reported an unexpected GI diagnostic error: $(@($unexpectedGiErrors.Code) -join ', ')."
    }
    if ([int]$Health.validationWarningCount -ne 0 -or
        [int]$Health.validationErrorCount -ne 0 -or
        [int]$Health.diagnostics.ValidationWarningMessageCount -ne 0 -or
        [int]$Health.diagnostics.ValidationErrorMessageCount -ne 0) {
        throw "$Label emitted Vulkan validation messages."
    }
    $failedOperations = @($Health.operations | Where-Object {
            [string]$_.status -ceq "failed"
        })
    if ($failedOperations.Count -ne 0) {
        throw "$Label reported a failed runtime operation: $(@($failedOperations.name) -join ', ')."
    }

    $failure = [string](Get-PropertyValue $Health "failure" "")
    if ([string]$Health.status -ceq "passed") {
        if (-not [string]::IsNullOrWhiteSpace($failure)) {
            throw "$Label has a passing health status with a failure detail."
        }
    } elseif ([string]$Health.status -ceq "failed") {
        $budgetFailure = [regex]::Match(
            $failure,
            "^Benchmark exceeded '([^']+)':")
        $allowedFailure = $budgetFailure.Success -and
            $allowedBudgetNames.Contains($budgetFailure.Groups[1].Value) -and
            @($overBudget | Where-Object {
                    [string]$_.Name -ceq $budgetFailure.Groups[1].Value
                }).Count -eq 1
        $allowedFailure = $allowedFailure -or (
            $failure.StartsWith(
                "GI diagnostic GiBudgetOverrun ",
                [StringComparison]::Ordinal) -and
            $ddgiMemoryExceeded)
        if (-not $allowedFailure) {
            throw "$Label health failed outside the admitted pre-target blockers: $failure"
        }
    } else {
        throw "$Label has unknown health status '$($Health.status)'."
    }

    $findings = @($overBudget | ForEach-Object {
            [pscustomobject][ordered]@{
                kind = "admitted-pretarget-budget"
                name = [string]$_.Name
                value = [double]$_.Value
                unit = [string]$_.Unit
                threshold = [double]$_.FailureThreshold
            }
        })
    foreach ($target in @(
            [pscustomobject]@{
                name = "CPU p95"
                value = [double]$Report.CpuFrameMilliseconds.P95Milliseconds
                threshold = [double]$Manifest.performanceTarget.cpuP95Milliseconds
            },
            [pscustomobject]@{
                name = "GPU p95"
                value = [double]$Report.GpuFrameMilliseconds.P95Milliseconds
                threshold = [double]$Manifest.performanceTarget.gpuP95Milliseconds
            },
            [pscustomobject]@{
                name = "Frame p99"
                value = [Math]::Max(
                    [double]$Report.CpuFrameMilliseconds.P99Milliseconds,
                    [double]$Report.GpuFrameMilliseconds.P99Milliseconds)
                threshold = [double]$Manifest.performanceTarget.frameP99Milliseconds
            })) {
        if ($target.value -gt $target.threshold) {
            $findings += [pscustomobject][ordered]@{
                kind = "unmet-final-target"
                name = $target.name
                value = $target.value
                unit = "ms"
                threshold = $target.threshold
            }
        }
    }
    return @($findings)
}

function Assert-AutomaticPlanarCaptureEvidence {
    param($Report, $Variant, [string]$Label)
    $evidence = $Report.AutomaticPlanarEvidence
    if ($null -eq $evidence -or -not [bool]$evidence.Available -or
        [int]$evidence.CompletedFrameCount -ne [int]$Report.MeasurementFrameCount -or
        [int]$evidence.CaptureFrameCount -le 0 -or
        [int]$evidence.CaptureFrameMilliseconds.Count -le 0) {
        throw "$Label lacks classified automatic-planar capture timing."
    }
    $frames = @($evidence.Frames)
    if ($frames.Count -ne [int]$evidence.CompletedFrameCount -or
        @($frames | Where-Object {
                -not [bool]$_.CompletedLifecycle.Valid -or
                -not [bool]$_.CompletedLifecycle.GpuTimingRecorded -or
                [int]$_.CompletedLifecycle.SelectedCount -le 0 -or
                [int]$_.CompletedLifecycle.MetadataCapacityRejectionCount -ne 0
            }).Count -ne 0) {
        throw "$Label has incomplete or rejected automatic-planar lifecycle evidence."
    }
    $selector = [string](Get-PropertyValue `
        $Variant.environment "NJULF_AUTOMATIC_PLANAR_EXCLUSION_ENCODING" "")
    if ($selector -ceq "BitsetAuto") {
        if (@($frames | Where-Object {
                    [int]$_.CompletedLifecycle.BitsetCaptureCount -le 0 -or
                    [int]$_.CompletedLifecycle.SortedListFallbackCount -ne 0
                }).Count -ne 0) {
            throw "$Label did not prove exclusive BitsetAuto encoding."
        }
    } elseif ($selector -ceq "SortedList") {
        if (@($frames | Where-Object {
                    [int]$_.CompletedLifecycle.BitsetCaptureCount -ne 0 -or
                    [int]$_.CompletedLifecycle.SortedListFallbackCount -le 0
                }).Count -ne 0) {
            throw "$Label did not prove exclusive SortedList encoding."
        }
    } else {
        throw "$Label automatic-planar claim lacks an exact encoding selector."
    }
}

function Assert-CaptureReport {
    param($Report, $Health, $Workload, $Build, $Variant,
        [string]$PairId, [string]$Label, [bool]$AutomaticPlanarClaim)
    $preTargetFindings = @(Get-PretargetOperationalFindings `
        $Manifest $Report $Health $Label)
    if ([string]$Report.Kind -cne "njulf-renderer-benchmark" -or
        [string]$Report.Schema -cne "njulf-renderer-benchmark/v5") {
        throw "$Label has an unsupported benchmark report."
    }
    $capture = $Report.LastDiagnostics.CaptureRun
    if ([string]$capture.Commit -cne [string]$Build.commit -or
        [string]$capture.DirtyWorktreeState -cne "clean" -or
        [string]$Report.CaptureContract.Trajectory -cne [string]$Workload.trajectory -or
        [string]$Health.options.Benchmark.CapturePairId -cne $PairId -or
        [int]$Health.diagnostics.CaptureRenderWidth -ne 1920 -or
        [int]$Health.diagnostics.CaptureRenderHeight -ne 1080) {
        throw "$Label identity differs from the frozen workload/build."
    }
    if ([double]$Report.HdrDifference.RelativeRmse -gt [double]$Manifest.quality.maximumRelativeRmse -or
        [double]$Report.HdrDifference.FlipP95 -gt [double]$Manifest.quality.maximumFlipP95) {
        throw "$Label exceeds the hard image-quality gate."
    }
    if ($AutomaticPlanarClaim) {
        Assert-AutomaticPlanarCaptureEvidence $Report $Variant $Label
    }
    return @($preTargetFindings)
}

function Get-Median {
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw "Cannot compute an empty median." }
    $middle = [int]($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-BootstrapLowerBound {
    param([double[]]$Differences, [int]$Samples, [double]$Confidence)
    if ($Differences.Count -eq 0) { throw "Bootstrap requires paired differences." }
    $random = [Random]::new(20260820)
    $estimates = [double[]]::new($Samples)
    for ($sample = 0; $sample -lt $Samples; $sample++) {
        $draw = [double[]]::new($Differences.Count)
        for ($index = 0; $index -lt $Differences.Count; $index++) {
            $draw[$index] = $Differences[$random.Next($Differences.Count)]
        }
        $estimates[$sample] = Get-Median $draw
    }
    [Array]::Sort($estimates)
    $index = [Math]::Floor(((1.0 - $Confidence) / 2.0) * $Samples)
    return $estimates[[Math]::Max(0, [Math]::Min($Samples - 1, $index))]
}

function Get-TimingValue {
    param($Report, [string]$Domain, [string]$Name)
    if ($Domain -ceq "gpu" -and $Name -ceq "__automatic_planar_capture__") {
        if ($null -eq $Report.AutomaticPlanarEvidence -or
            -not [bool]$Report.AutomaticPlanarEvidence.Available -or
            [int]$Report.AutomaticPlanarEvidence.CaptureFrameMilliseconds.Count -le 0) {
            throw "Classified automatic-planar capture timing is unavailable."
        }
        return [double]$Report.AutomaticPlanarEvidence.CaptureFrameMilliseconds.P95Milliseconds
    }
    if ($Name -ceq "__frame__") {
        $stats = if ($Domain -ceq "cpu") { $Report.CpuFrameMilliseconds } else { $Report.GpuFrameMilliseconds }
        return [double]$stats.P95Milliseconds
    }
    $items = if ($Domain -ceq "cpu") { @($Report.CpuStages) } else { @($Report.GpuPasses) }
    $match = @($items | Where-Object { [string]$_.Name -ceq $Name })
    if ($match.Count -ne 1) { throw "Timing '$Domain::$Name' is missing or duplicated." }
    return [double]$match[0].P95Milliseconds
}

function Get-ImprovementPercent {
    param([double]$Baseline, [double]$Candidate)
    if ($Baseline -le 0.0) { return 0.0 }
    return (($Baseline - $Candidate) / $Baseline) * 100.0
}

function Get-ExperimentRegressionFailures {
    param($Manifest, [object[]]$BaselineReports, [object[]]$CandidateReports)
    $maximum = [double]$Manifest.acceptance.maximumRegressionPercent
    $failures = [System.Collections.Generic.List[string]]::new()
    $metricKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($report in @($BaselineReports) + @($CandidateReports)) {
        [void]$metricKeys.Add("cpu::__frame_p95")
        [void]$metricKeys.Add("cpu::__frame_p99")
        [void]$metricKeys.Add("gpu::__frame_p95")
        [void]$metricKeys.Add("gpu::__frame_p99")
        foreach ($stage in @($report.CpuStages)) { [void]$metricKeys.Add("cpu::$([string]$stage.Name)") }
        foreach ($pass in @($report.GpuPasses)) { [void]$metricKeys.Add("gpu::$([string]$pass.Name)") }
    }
    foreach ($key in @($metricKeys | Sort-Object)) {
        $parts = $key.Split("::", 2)
        $domain = $parts[0]
        $name = $parts[1]
        $baselineValues = @()
        $candidateValues = @()
        if ($name -in @("__frame_p95", "__frame_p99")) {
            $property = if ($name -ceq "__frame_p99") { "P99Milliseconds" } else { "P95Milliseconds" }
            foreach ($report in $BaselineReports) {
                $stats = if ($domain -ceq "cpu") { $report.CpuFrameMilliseconds } else { $report.GpuFrameMilliseconds }
                $baselineValues += [double]$stats.$property
            }
            foreach ($report in $CandidateReports) {
                $stats = if ($domain -ceq "cpu") { $report.CpuFrameMilliseconds } else { $report.GpuFrameMilliseconds }
                $candidateValues += [double]$stats.$property
            }
        } else {
            foreach ($report in $BaselineReports) {
                try { $baselineValues += Get-TimingValue $report $domain $name } catch { }
            }
            foreach ($report in $CandidateReports) {
                try { $candidateValues += Get-TimingValue $report $domain $name } catch { }
            }
        }
        if ($baselineValues.Count -ne $BaselineReports.Count -or
            $candidateValues.Count -ne $CandidateReports.Count) {
            $failures.Add("$key timing topology differs")
            continue
        }
        $baseline = Get-Median ([double[]]$baselineValues)
        $candidate = Get-Median ([double[]]$candidateValues)
        if ($baseline -gt 0.0 -and $candidate -gt $baseline * (1.0 + ($maximum / 100.0))) {
            $regression = (($candidate - $baseline) / $baseline) * 100.0
            $failures.Add("$key regressed $($regression.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))%")
        }
    }
    return @($failures)
}

function Compare-ExperimentReports {
    param($Manifest, $Claim, [object[]]$OrderedReports, [string]$AcceptanceMode,
        [string]$Mode = "ab")
    if ($OrderedReports.Count -ne ([int]$Manifest.capture.abbaCycles * 4)) {
        throw "ABBA report topology is incomplete for '$($Claim.workloadId)'."
    }
    $baselineReports = @()
    $candidateReports = @()
    $frameDifferences = @()
    $passDifferences = @()
    for ($cycle = 0; $cycle -lt [int]$Manifest.capture.abbaCycles; $cycle++) {
        $slot = $cycle * 4
        $a1 = $OrderedReports[$slot]
        $b1 = $OrderedReports[$slot + 1]
        $b2 = $OrderedReports[$slot + 2]
        $a2 = $OrderedReports[$slot + 3]
        $baselineReports += @($a1, $a2)
        $candidateReports += @($b1, $b2)
        foreach ($pair in @(@($a1, $b1), @($a2, $b2))) {
            $aFrame = [Math]::Max((Get-TimingValue $pair[0] "cpu" "__frame__"), (Get-TimingValue $pair[0] "gpu" "__frame__"))
            $bFrame = [Math]::Max((Get-TimingValue $pair[1] "cpu" "__frame__"), (Get-TimingValue $pair[1] "gpu" "__frame__"))
            $frameDifferences += $aFrame - $bFrame
            $passDifferences += (Get-TimingValue $pair[0] ([string]$Claim.targetDomain) ([string]$Claim.targetPass)) -
                (Get-TimingValue $pair[1] ([string]$Claim.targetDomain) ([string]$Claim.targetPass))
        }
    }
    $baselineFrame = Get-Median @($baselineReports | ForEach-Object {
        [Math]::Max((Get-TimingValue $_ "cpu" "__frame__"), (Get-TimingValue $_ "gpu" "__frame__"))
    })
    $candidateFrame = Get-Median @($candidateReports | ForEach-Object {
        [Math]::Max((Get-TimingValue $_ "cpu" "__frame__"), (Get-TimingValue $_ "gpu" "__frame__"))
    })
    $baselinePass = Get-Median @($baselineReports | ForEach-Object {
        Get-TimingValue $_ ([string]$Claim.targetDomain) ([string]$Claim.targetPass)
    })
    $candidatePass = Get-Median @($candidateReports | ForEach-Object {
        Get-TimingValue $_ ([string]$Claim.targetDomain) ([string]$Claim.targetPass)
    })
    $frameMs = $baselineFrame - $candidateFrame
    $passMs = $baselinePass - $candidatePass
    $frameLower = Get-BootstrapLowerBound ([double[]]$frameDifferences) ([int]$Manifest.acceptance.bootstrapSamples) ([double]$Manifest.acceptance.bootstrapConfidence)
    $passLower = Get-BootstrapLowerBound ([double[]]$passDifferences) ([int]$Manifest.acceptance.bootstrapSamples) ([double]$Manifest.acceptance.bootstrapConfidence)
    $frameMinimum = if ($AcceptanceMode -ceq "loop-frame-1ms") { 1.0 } else { [double]$Manifest.acceptance.minimumFrameImprovementMilliseconds }
    $frameWin = $frameMs -ge $frameMinimum -and
        (Get-ImprovementPercent $baselineFrame $candidateFrame) -ge [double]$Manifest.acceptance.minimumFrameImprovementPercent -and
        $frameLower -gt 0.0
    $passWin = $passMs -ge [double]$Manifest.acceptance.minimumPassImprovementMilliseconds -and
        (Get-ImprovementPercent $baselinePass $candidatePass) -ge [double]$Manifest.acceptance.minimumPassImprovementPercent -and
        $passLower -gt 0.0 -and $candidateFrame -le $baselineFrame
    $regressions = @(Get-ExperimentRegressionFailures $Manifest $baselineReports $candidateReports)
    $performanceAccepted = switch ($AcceptanceMode) {
        "frame-and-pass" { $frameWin -and $passWin }
        "pass-only" { $passWin }
        "loop-frame-1ms" { $frameWin }
        default { $frameWin -or $passWin }
    }
    $accepted = if ($Mode -ceq "aa") {
        $regressions.Count -eq 0
    } else {
        $performanceAccepted -and $regressions.Count -eq 0
    }
    return [pscustomobject][ordered]@{
        workloadId = [string]$Claim.workloadId
        targetDomain = [string]$Claim.targetDomain
        targetPass = [string]$Claim.targetPass
        baselineBottleneckP95Milliseconds = $baselineFrame
        candidateBottleneckP95Milliseconds = $candidateFrame
        frameImprovementMilliseconds = $frameMs
        frameImprovementPercent = Get-ImprovementPercent $baselineFrame $candidateFrame
        frameBootstrapLower95Milliseconds = $frameLower
        baselineTargetP95Milliseconds = $baselinePass
        candidateTargetP95Milliseconds = $candidatePass
        targetImprovementMilliseconds = $passMs
        targetImprovementPercent = Get-ImprovementPercent $baselinePass $candidatePass
        targetBootstrapLower95Milliseconds = $passLower
        frameWin = $frameWin
        passWin = $passWin
        regressions = $regressions
        accepted = $accepted
    }
}

function Compare-ControlReports {
    param($Manifest, [string]$WorkloadId, [object[]]$OrderedReports)
    if ($OrderedReports.Count -ne ([int]$Manifest.capture.abbaCycles * 4)) {
        throw "ABBA report topology is incomplete for control '$WorkloadId'."
    }
    $baselineReports = @()
    $candidateReports = @()
    for ($cycle = 0; $cycle -lt [int]$Manifest.capture.abbaCycles; $cycle++) {
        $slot = $cycle * 4
        $baselineReports += @($OrderedReports[$slot], $OrderedReports[$slot + 3])
        $candidateReports += @($OrderedReports[$slot + 1], $OrderedReports[$slot + 2])
    }
    $regressions = @(Get-ExperimentRegressionFailures $Manifest $baselineReports $candidateReports)
    return [pscustomobject][ordered]@{
        workloadId = $WorkloadId
        controlOnly = $true
        regressions = $regressions
        accepted = $regressions.Count -eq 0
    }
}

function Invoke-PairValidation {
    param($VerifierBuild, [string]$BaselineReport, [string]$CandidateReport,
        [string]$OutputPath, [string]$LogPath, [bool]$AbComparison)
    $arguments = @(
        "--compare-benchmark-pair", $BaselineReport, $CandidateReport,
        "--benchmark-pair-report", $OutputPath)
    if ($AbComparison) { $arguments += "--benchmark-pair-ab" }
    Invoke-CheckedProcess ([string]$VerifierBuild.ExecutablePath) $arguments `
        ([string]$VerifierBuild.RootPath) $LogPath 120 "Frozen A/B pair validation"
}

$specFile = [System.IO.Path]::GetFullPath($SpecPath)
$Spec = Read-JsonFile $specFile "Experiment spec"
$Manifest = Read-JsonFile $script:ManifestPath "Pinned performance manifest"
Assert-PinnedCampaign
Assert-ExperimentSpec $Spec $Manifest

$baselineRoot = Assert-SourceIdentity $Spec.baseline "baseline"
$candidateRoot = Assert-SourceIdentity $Spec.candidate "candidate"
if ([string]$Spec.mode -ceq "aa" -and
    -not [string]::Equals($baselineRoot, $candidateRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "A/A mode requires the same source root."
}

if ($ValidateOnly) {
    Write-Host "Experiment spec valid: $specFile"
    Write-Host "Experiment: $($Spec.experimentId); mode=$($Spec.mode); claims=$(@($Spec.claims).Count)"
    exit 0
}

$campaignRun = if ([System.IO.Path]::IsPathRooted([string]$Spec.campaignRunDirectory)) {
    [System.IO.Path]::GetFullPath([string]$Spec.campaignRunDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot ([string]$Spec.campaignRunDirectory)))
}
$lockPath = Join-Path $campaignRun "campaign.lock.json"
$lock = Read-JsonFile $lockPath "Campaign reference lock"
if ([string]$lock.manifestSha256 -cne (Get-Sha256 $script:ManifestPath)) {
    throw "Campaign lock does not match the pinned manifest."
}

$experimentRoot = Join-Path $script:RunBase ([string]$Spec.experimentId)
if (-not $AnalyzeOnly -and (Test-Path -LiteralPath $experimentRoot)) {
    throw "Experiment output already exists: $experimentRoot"
}
New-Item -ItemType Directory -Force -Path $experimentRoot | Out-Null
$specSnapshot = Join-Path $experimentRoot "experiment.spec.snapshot.json"
if (-not (Test-Path -LiteralPath $specSnapshot)) {
    [System.IO.File]::Copy($specFile, $specSnapshot, $false)
}
Write-JsonFile (Join-Path $experimentRoot "experiment.identity.json") ([ordered]@{
    schema = "njulf-perf-experiment-identity/v1"
    experimentId = [string]$Spec.experimentId
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    specSha256 = Get-Sha256 $specSnapshot
    manifestPath = $script:ManifestPath
    manifestSha256 = Get-Sha256 $script:ManifestPath
    campaignDriverPath = $script:CampaignDriverPath
    campaignDriverSha256 = Get-Sha256 $script:CampaignDriverPath
    campaignLockPath = $lockPath
    campaignLockSha256 = Get-Sha256 $lockPath
    baselineCommit = [string]$Spec.baseline.commit
    candidateCommit = [string]$Spec.candidate.commit
})

$selectedWorkloads = [System.Collections.Generic.List[object]]::new()
$selectedIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($claim in @($Spec.claims)) {
    $workload = @($Manifest.workloads | Where-Object { [string]$_.id -ceq [string]$claim.workloadId })[0]
    if ($selectedIds.Add([string]$workload.id)) { $selectedWorkloads.Add($workload) }
}
foreach ($workload in @($Manifest.workloads | Where-Object { [bool]$_.qualification })) {
    if ($selectedIds.Add([string]$workload.id)) { $selectedWorkloads.Add($workload) }
}

$builds = [ordered]@{}
if (-not $AnalyzeOnly) {
    $cookedFiles = Get-CookedFiles $Manifest ([string]$Spec.cookedAssetRoot)
    foreach ($configuration in @($Spec.configurations)) {
        $baselineBuild = New-VariantBuild $Manifest $Spec.baseline $configuration "baseline" $experimentRoot $cookedFiles
        $candidateBuild = if ([string]$Spec.mode -ceq "aa") {
            $baselineBuild
        } else {
            New-VariantBuild $Manifest $Spec.candidate $configuration "candidate" $experimentRoot $cookedFiles
        }
        $builds[$configuration] = [ordered]@{ baseline = $baselineBuild; candidate = $candidateBuild }
    }
    Write-JsonFile (Join-Path $experimentRoot "builds.json") $builds
} else {
    $builds = Read-JsonFile (Join-Path $experimentRoot "builds.json") "Experiment builds"
}

$results = @()
$preTargetEvidence = @()
foreach ($configuration in @($Spec.configurations)) {
    $configurationBuilds = $builds.PSObject.Properties[$configuration]
    if ($null -eq $configurationBuilds) { $configurationBuilds = $builds[$configuration] }
    else { $configurationBuilds = $configurationBuilds.Value }
    foreach ($workload in @($selectedWorkloads)) {
        $claim = @($Spec.claims | Where-Object { [string]$_.workloadId -ceq [string]$workload.id })
        $orderedReports = @()
        for ($cycle = 1; $cycle -le [int]$Manifest.capture.abbaCycles; $cycle++) {
            $slots = @(
                [pscustomobject]@{ phase = "baseline"; build = $configurationBuilds.baseline; variant = $Spec.baseline },
                [pscustomobject]@{ phase = "candidate"; build = $configurationBuilds.candidate; variant = $Spec.candidate },
                [pscustomobject]@{ phase = "candidate"; build = $configurationBuilds.candidate; variant = $Spec.candidate },
                [pscustomobject]@{ phase = "baseline"; build = $configurationBuilds.baseline; variant = $Spec.baseline })
            for ($slotIndex = 0; $slotIndex -lt $slots.Count; $slotIndex++) {
                $slot = $slots[$slotIndex]
                $captureRoot = Join-Path $experimentRoot "captures\$configuration\$($workload.id)"
                $stem = "cycle-{0:D2}-slot-{1}-{2}" -f $cycle, ($slotIndex + 1), $slot.phase
                $reportPath = Join-Path $captureRoot "$stem.json"
                $healthPath = Join-Path $captureRoot "$stem.health.json"
                $pairId = "$($Spec.experimentId)-$configuration-$($workload.id)-cycle-$cycle"
                if (-not $AnalyzeOnly) {
                    New-Item -ItemType Directory -Force -Path $captureRoot | Out-Null
                    $reference = Get-ReferenceEntry $lock $configuration ([string]$workload.id)
                    $arguments = Get-WorkloadArguments $Manifest $workload $slot.variant $reportPath $healthPath $pairId $reference
                    $logPath = Join-Path $experimentRoot "logs\capture-$configuration-$($workload.id)-$stem.log"
                    Invoke-CheckedProcess ([string]$slot.build.executablePath) $arguments `
                        ([string]$slot.build.rootPath) $logPath ([int]$Manifest.capture.benchmarkTimeoutSeconds) `
                        "$configuration/$($workload.id) cycle $cycle slot $($slotIndex + 1) $($slot.phase)" `
                        $slot.variant.environment @(0, 1)
                }
                $report = Read-JsonFile $reportPath "Benchmark report"
                $health = Read-JsonFile $healthPath "Health report"
                $automaticPlanarClaim = @($claim | Where-Object {
                        [string]$_.targetDomain -ceq "gpu" -and
                        [string]$_.targetPass -ceq "__automatic_planar_capture__"
                    }).Count -eq 1
                $findings = @(Assert-CaptureReport `
                    $report $health $workload $slot.build $slot.variant $pairId `
                    "$configuration/$($workload.id)/$stem" $automaticPlanarClaim)
                $preTargetEvidence += [pscustomobject][ordered]@{
                    configuration = $configuration
                    workloadId = [string]$workload.id
                    capture = $stem
                    phase = [string]$slot.phase
                    findings = @($findings)
                }
                $orderedReports += $report
            }
            if (-not $AnalyzeOnly) {
                foreach ($pair in @(@(0, 1), @(3, 2))) {
                    $leftStem = "cycle-{0:D2}-slot-{1}-{2}" -f $cycle, ($pair[0] + 1), "baseline"
                    $rightStem = "cycle-{0:D2}-slot-{1}-{2}" -f $cycle, ($pair[1] + 1), "candidate"
                    $captureRoot = Join-Path $experimentRoot "captures\$configuration\$($workload.id)"
                    $pairRoot = Join-Path $experimentRoot "pairs\$configuration\$($workload.id)"
                    New-Item -ItemType Directory -Force -Path $pairRoot | Out-Null
                    Invoke-PairValidation $lock.referenceBuilds.$configuration `
                        (Join-Path $captureRoot "$leftStem.json") (Join-Path $captureRoot "$rightStem.json") `
                        (Join-Path $pairRoot "cycle-$cycle-$($pair[0])-$($pair[1]).json") `
                        (Join-Path $experimentRoot "logs\pair-$configuration-$($workload.id)-$cycle-$($pair[0])-$($pair[1]).log") `
                        ([string]$Spec.mode -ceq "ab")
                }
            }
        }
        if ($claim.Count -eq 1) {
            $comparison = Compare-ExperimentReports $Manifest $claim[0] $orderedReports `
                ([string]$Spec.acceptanceMode) ([string]$Spec.mode)
            $comparison | Add-Member -NotePropertyName configuration -NotePropertyValue $configuration
            $results += $comparison
        } else {
            $comparison = Compare-ControlReports $Manifest ([string]$workload.id) $orderedReports
            $comparison | Add-Member -NotePropertyName configuration -NotePropertyValue $configuration
            $results += $comparison
        }
    }
}

$targetResults = @($results | Where-Object { -not [bool](Get-PropertyValue $_ "controlOnly" $false) })
$completeConfigurations = (@($Spec.configurations | Sort-Object) -join "`n") -ceq
    (@($Manifest.finalConfigurations | ForEach-Object { [string]$_ } | Sort-Object) -join "`n")
$decision = if (-not $completeConfigurations) {
    "inconclusive"
} elseif (@($results | Where-Object { -not [bool]$_.accepted }).Count -eq 0) {
    if ([string]$Spec.mode -ceq "aa") { "aa-pass" } else { "keep" }
} else {
    if ([string]$Spec.mode -ceq "aa") { "aa-fail" } else { "reject" }
}
$decisionValue = [ordered]@{
    schema = "njulf-perf-experiment-decision/v1"
    experimentId = [string]$Spec.experimentId
    mode = [string]$Spec.mode
    acceptanceMode = [string]$Spec.acceptanceMode
    completeConfigurations = $completeConfigurations
    decision = $decision
    preTargetEvidence = $preTargetEvidence
    results = $results
}
Write-JsonFile (Join-Path $experimentRoot "decision.json") $decisionValue
Write-Host "Experiment decision: $decision"
