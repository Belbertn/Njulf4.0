[CmdletBinding()]
param(
    [string]$TrialCommand = "",

    [int]$Iterations = 1,
    [int]$RepeatCount = 3,
    [string]$RunDirectory = ".perf-loop-runs",
    [string]$BenchmarkCommand = "",
    [string]$BuildCommand = "",
    [string]$ProjectPath = "NjulfHelloGame/NjulfHelloGame.csproj",
    [string]$Configuration = "Release",
    [string]$Scene = "Bistro",
    [string]$Scenario = "Normal",
    [int]$WarmupFrames = 30,
    [int]$MeasureFrames = 240,
    [int]$MaximumSettlingFrames = 4096,
    [int]$BenchmarkTimeoutSeconds = 900,
    [int]$TrialTimeoutSeconds = 1800,
    [string]$HdrReferencePath = "",
    [double]$MaximumHdrRelativeRmse = 0.005,
    [switch]$InitializeHdrReference,
    [switch]$InitializeHdrReferenceOnly,
    [switch]$BaselineOnly,
    [bool]$RequireProductionTiming = $true,
    [double]$TargetP95Milliseconds = 16.67,
    [double]$TargetP99Milliseconds = 20.0,
    [ValidateSet("low", "medium", "high", "ultra", "stress")]
    [string]$BenchmarkBudgetProfile = "stress",
    [string]$ProtectedPath = "NjulfHelloGame/Assets/Bistro_v5_2",

    [ValidateSet("powershell", "git-bash")]
    [string]$TrialShell = "powershell",
    [string]$GitBashPath = "",

    [double]$MinImprovementPercent = 3.0,
    [double]$MaxRegressionPercent = 1.0,
    [bool]$RollbackRejected = $true,
    [switch]$KeepInconclusive,
    [switch]$KeepRejectedStashes
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

if ($Iterations -lt 1) {
    throw "Iterations must be at least 1."
}

if ($RepeatCount -lt 1) {
    throw "RepeatCount must be at least 1."
}

if ($WarmupFrames -lt 0) {
    throw "WarmupFrames cannot be negative."
}

if ($MeasureFrames -lt 1) {
    throw "MeasureFrames must be at least 1."
}

if ($MaximumSettlingFrames -lt 1) {
    throw "MaximumSettlingFrames must be at least 1."
}

if ([double]::IsNaN($MaximumHdrRelativeRmse) -or
    [double]::IsInfinity($MaximumHdrRelativeRmse) -or
    $MaximumHdrRelativeRmse -lt 0.0) {
    throw "MaximumHdrRelativeRmse must be a non-negative finite value."
}

if ([double]::IsNaN($TargetP95Milliseconds) -or
    [double]::IsInfinity($TargetP95Milliseconds) -or
    $TargetP95Milliseconds -le 0.0 -or
    [double]::IsNaN($TargetP99Milliseconds) -or
    [double]::IsInfinity($TargetP99Milliseconds) -or
    $TargetP99Milliseconds -le 0.0) {
    throw "Target frame times must be positive finite values."
}

if ($BenchmarkTimeoutSeconds -lt 0) {
    throw "BenchmarkTimeoutSeconds cannot be negative. Use 0 to disable the timeout."
}

if ($TrialTimeoutSeconds -lt 0) {
    throw "TrialTimeoutSeconds cannot be negative. Use 0 to disable the timeout."
}

$script:SolutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:RunRoot = if ([System.IO.Path]::IsPathRooted($RunDirectory)) {
    $RunDirectory
} else {
    Join-Path $script:SolutionRoot $RunDirectory
}
$script:ResolvedHdrReferencePath = if ([string]::IsNullOrWhiteSpace($HdrReferencePath)) {
    ""
} elseif ([System.IO.Path]::IsPathRooted($HdrReferencePath)) {
    [System.IO.Path]::GetFullPath($HdrReferencePath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $HdrReferencePath))
}

if (-not $InitializeHdrReference -and
    [string]::IsNullOrWhiteSpace($script:ResolvedHdrReferencePath)) {
    throw "A strict performance run requires -HdrReferencePath. Use -InitializeHdrReference once to establish it."
}

if (-not $BaselineOnly -and [string]::IsNullOrWhiteSpace($TrialCommand)) {
    throw "TrialCommand is required unless -BaselineOnly is specified."
}

if ($InitializeHdrReferenceOnly -and -not $InitializeHdrReference) {
    throw "InitializeHdrReferenceOnly requires -InitializeHdrReference."
}

function Quote-PSArgument {
    param([string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-Git {
    param([string[]]$Arguments)

    Push-Location $script:SolutionRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & git @Arguments 2>&1
            $exitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($exitCode -ne 0) {
            throw "git $($Arguments -join ' ') failed with exit code $exitCode.`n$output"
        }

        return @($output)
    } finally {
        Pop-Location
    }
}

function Invoke-GitMaybe {
    param([string[]]$Arguments)

    Push-Location $script:SolutionRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & git @Arguments 2>&1
            $exitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($output)
        }
    } finally {
        Pop-Location
    }
}

function Get-GitText {
    param([string[]]$Arguments)
    return ((Invoke-Git $Arguments) -join "`n").Trim()
}

function Get-WorktreeStatusText {
    $arguments = @("status", "--porcelain=v1", "--untracked-files=all", "--", ".")
    if (-not [string]::IsNullOrWhiteSpace($ProtectedPath)) {
        $arguments += ":(exclude)$($ProtectedPath.TrimEnd('/', '\'))/**"
    }

    return (Get-GitText $arguments)
}

function Get-CheckpointPathspec {
    $pathspec = @(".")
    if (-not [string]::IsNullOrWhiteSpace($ProtectedPath)) {
        $pathspec += ":(exclude)$($ProtectedPath.TrimEnd('/', '\'))/**"
    }

    return $pathspec
}

function Get-ProtectedPathFingerprint {
    if ([string]::IsNullOrWhiteSpace($ProtectedPath)) {
        return "disabled"
    }

    $path = if ([System.IO.Path]::IsPathRooted($ProtectedPath)) {
        [System.IO.Path]::GetFullPath($ProtectedPath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $ProtectedPath))
    }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        return "missing"
    }

    $files = @(Get-ChildItem -LiteralPath $path -Recurse -File | Sort-Object FullName)
    $builder = [System.Text.StringBuilder]::new()
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($path.Length + 1).Replace("\", "/")
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$builder.Append($hash).Append("  ").Append($relative).Append("`n")
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).
            Replace("-", "").
            ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Assert-ProtectedPathUnchanged {
    param([string]$ExpectedFingerprint)

    $actual = Get-ProtectedPathFingerprint
    if (-not [string]::Equals($actual, $ExpectedFingerprint, [StringComparison]::Ordinal)) {
        throw "Protected path '$ProtectedPath' changed during the performance trial. Expected $ExpectedFingerprint, got $actual."
    }
}

function Find-StashRefByHash {
    param([string]$Hash)

    if ([string]::IsNullOrWhiteSpace($Hash)) {
        return $null
    }

    $result = Invoke-GitMaybe @("stash", "list", "--format=%gd %H")
    if ($result.ExitCode -ne 0) {
        return $null
    }

    foreach ($line in $result.Output) {
        $text = [string]$line
        if ($text.EndsWith(" $Hash", [StringComparison]::OrdinalIgnoreCase)) {
            return $text.Split(" ")[0]
        }
    }

    return $null
}

function Drop-StashByHash {
    param([string]$Hash)

    $stashRef = Find-StashRefByHash $Hash
    if ([string]::IsNullOrWhiteSpace($stashRef)) {
        return
    }

    $null = Invoke-Git @("stash", "drop", $stashRef)
}

function New-PretrialCheckpoint {
    param([int]$Iteration)

    $status = Get-WorktreeStatusText
    if ([string]::IsNullOrWhiteSpace($status)) {
        return $null
    }

    $message = "perf-loop pretrial iteration $Iteration $(Get-Date -Format o)"
    $arguments = @("stash", "push", "--include-untracked", "--message", $message, "--")
    $arguments += Get-CheckpointPathspec
    $null = Invoke-Git $arguments
    $stashHash = Get-GitText @("rev-parse", "refs/stash")
    $stashRef = Find-StashRefByHash $stashHash
    if ([string]::IsNullOrWhiteSpace($stashRef)) {
        throw "Could not find the pretrial stash ref for $stashHash."
    }

    $apply = Invoke-GitMaybe @("stash", "apply", "--index", $stashRef)
    if ($apply.ExitCode -ne 0) {
        throw "Could not restore pretrial worktree from $stashRef.`n$($apply.Output -join "`n")"
    }

    return $stashHash
}

function Restore-PretrialCheckpoint {
    param(
        [string]$CheckpointHash,
        [int]$Iteration
    )

    $candidateHash = $null
    $status = Get-WorktreeStatusText
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        $message = "perf-loop rejected candidate iteration $Iteration $(Get-Date -Format o)"
        $arguments = @("stash", "push", "--include-untracked", "--message", $message, "--")
        $arguments += Get-CheckpointPathspec
        $null = Invoke-Git $arguments
        $candidateHash = Get-GitText @("rev-parse", "refs/stash")
    }

    if (-not [string]::IsNullOrWhiteSpace($CheckpointHash)) {
        $checkpointRef = Find-StashRefByHash $CheckpointHash
        if ([string]::IsNullOrWhiteSpace($checkpointRef)) {
            throw "Could not find pretrial checkpoint stash $CheckpointHash. Your rejected candidate was stashed as $candidateHash."
        }

        $apply = Invoke-GitMaybe @("stash", "apply", "--index", $checkpointRef)
        if ($apply.ExitCode -ne 0) {
            throw "Could not restore pretrial checkpoint $checkpointRef.`n$($apply.Output -join "`n")"
        }
    }

    if (-not $KeepRejectedStashes -and -not [string]::IsNullOrWhiteSpace($candidateHash)) {
        Drop-StashByHash $candidateHash
    }
}

function Expand-CommandTemplate {
    param(
        [string]$Template,
        [string]$ReportPath,
        [int]$Iteration,
        [string]$Phase,
        [int]$Repeat
    )

    return $Template.
        Replace("{ReportPath}", $ReportPath).
        Replace("{Iteration}", $Iteration.ToString()).
        Replace("{Phase}", $Phase).
        Replace("{Repeat}", $Repeat.ToString()).
        Replace("{RunDirectory}", $script:RunRoot).
        Replace("{SolutionRoot}", $script:SolutionRoot)
}

function Resolve-GitBashPath {
    if (-not [string]::IsNullOrWhiteSpace($GitBashPath)) {
        $resolved = Resolve-Path -LiteralPath $GitBashPath -ErrorAction Stop
        return $resolved.Path
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += @(
            (Join-Path $env:ProgramFiles "Git\bin\bash.exe"),
            (Join-Path $env:ProgramFiles "Git\usr\bin\bash.exe")
        )
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $candidates += @(
            (Join-Path $programFilesX86 "Git\bin\bash.exe"),
            (Join-Path $programFilesX86 "Git\usr\bin\bash.exe")
        )
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command "bash.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command -and $command.Source -notlike "*\Windows\System32\bash.exe") {
        return $command.Source
    }

    throw "TrialShell is git-bash, but Git Bash was not found. Add Git Bash to PATH or pass -GitBashPath 'C:\Program Files\Git\bin\bash.exe'."
}

function Get-DefaultBenchmarkCommand {
    param(
        [string]$ReportPath,
        [int]$Iteration,
        [string]$Phase,
        [int]$Repeat,
        [switch]$ReferenceInitialization
    )

    $project = if ([System.IO.Path]::IsPathRooted($ProjectPath)) {
        $ProjectPath
    } else {
        Join-Path $script:SolutionRoot $ProjectPath
    }

    $pairId = "bistro-perf-{0:000}" -f $Iteration
    $healthReportPath = [System.IO.Path]::ChangeExtension(
        $ReportPath,
        ".health.json")
    # The loop compares different executable builds; it does not toggle one of
    # the renderer's in-process capture variants. Keep both sides on the exact
    # baseline render path and use report paths to identify the phase.
    $variant = "baseline"
    $arguments = @(
        "dotnet run", "--no-build",
        "--project", (Quote-PSArgument $project),
        "-c", (Quote-PSArgument $Configuration),
        "--",
        "--benchmark",
        "--benchmark-report", (Quote-PSArgument $ReportPath),
        "--health-report", (Quote-PSArgument $healthReportPath),
        "--benchmark-warmup-frames", $WarmupFrames,
        "--benchmark-measure-frames", $MeasureFrames,
        "--benchmark-max-settle-frames", $MaximumSettlingFrames,
        "--benchmark-pair-id", (Quote-PSArgument $pairId),
        "--benchmark-variant", (Quote-PSArgument $variant),
        # This loop owns its explicit p95/p99 acceptance thresholds. Stress is
        # a diagnostics-only budget profile and does not alter render quality.
        "--benchmark-budget-profile", (Quote-PSArgument $BenchmarkBudgetProfile),
        "--scene", (Quote-PSArgument $Scene),
        "--performance-scenario", (Quote-PSArgument $Scenario),
        "--validation", "off",
        "--gpu-timing"
    )

    if ($ReferenceInitialization) {
        $arguments += @(
            "--benchmark-hdr-candidate",
            (Quote-PSArgument $script:ResolvedHdrReferencePath)
        )
    } elseif (-not [string]::IsNullOrWhiteSpace($script:ResolvedHdrReferencePath)) {
        $candidatePath = [System.IO.Path]::ChangeExtension($ReportPath, ".hdr.pfm")
        $arguments += @(
            "--benchmark-hdr-reference",
            (Quote-PSArgument $script:ResolvedHdrReferencePath),
            "--benchmark-hdr-candidate",
            (Quote-PSArgument $candidatePath),
            "--benchmark-hdr-max-relative-rmse",
            $MaximumHdrRelativeRmse.ToString([Globalization.CultureInfo]::InvariantCulture)
        )
        if ($RequireProductionTiming) {
            $arguments += "--benchmark-require-production"
        }
    }

    return $arguments -join " "
}

function Invoke-ValidatedBenchmarkCommand {
    param(
        [string]$Command,
        [string]$Label,
        [string]$HealthReportPath,
        [int]$TimeoutSeconds = 0
    )

    # Do not let a report from an earlier attempt authorize a failed launch.
    Remove-Item -LiteralPath $HealthReportPath -Force -ErrorAction SilentlyContinue

    $commandFailure = $null
    try {
        Invoke-CommandLine $Command $Label $TimeoutSeconds
    } catch {
        $commandFailure = $_
    }

    if (-not (Test-Path -LiteralPath $HealthReportPath -PathType Leaf)) {
        if ($null -ne $commandFailure) {
            throw $commandFailure
        }
        throw "$Label did not publish its required health report: $HealthReportPath"
    }

    $health = Get-Content -LiteralPath $HealthReportPath -Raw | ConvertFrom-Json
    if ([string]$health.status -eq "passed") {
        if ($null -ne $commandFailure) {
            throw $commandFailure
        }
        return
    }

    # Forward GI is integrated into the opaque draw and therefore cannot be
    # timestamped as an exclusive scope. The renderer intentionally keeps its
    # release-budget gate fail-closed until a paired capture supplies that
    # attribution. This loop instead gates the complete GPU frame externally,
    # so its diagnostics-only Stress profile may acknowledge this one exact,
    # machine-readable limitation without suppressing any other health failure.
    $knownAttributionFailure =
        "Benchmark required budget metric 'GI GPU' is unavailable."
    if ($BenchmarkBudgetProfile -eq "stress" -and
        [string]$health.failure -eq $knownAttributionFailure) {
        Write-Warning (
            "$Label completed with the expected forward-GI attribution " +
            "limitation; total GPU-frame and HDR gates remain mandatory.")
        return
    }

    $failure = if ([string]::IsNullOrWhiteSpace([string]$health.failure)) {
        "health status '$($health.status)'"
    } else {
        [string]$health.failure
    }
    throw "$Label failed its health gate: $failure"
}

function Get-DefaultBuildCommand {
    $project = if ([System.IO.Path]::IsPathRooted($ProjectPath)) {
        $ProjectPath
    } else {
        Join-Path $script:SolutionRoot $ProjectPath
    }

    return "dotnet build $(Quote-PSArgument $project) -c $(Quote-PSArgument $Configuration) --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false"
}

function Invoke-BenchmarkBuild {
    param([string]$Label)

    $command = if ([string]::IsNullOrWhiteSpace($BuildCommand)) {
        Get-DefaultBuildCommand
    } else {
        Expand-CommandTemplate $BuildCommand "" 0 "build" 0
    }
    Invoke-CommandLine $command $Label $BenchmarkTimeoutSeconds
}

function Invoke-CommandLine {
    param(
        [string]$Command,
        [string]$Label,
        [int]$TimeoutSeconds = 0
    )

    Write-Host "[$Label] $Command"
    if ($TimeoutSeconds -gt 0) {
        Invoke-PowerShellCommandLineWithTimeout $Command $Label $TimeoutSeconds
        return
    }

    Push-Location $script:SolutionRoot
    try {
        $global:LASTEXITCODE = 0
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            Invoke-Expression $Command
            $succeeded = $?
            $exitCode = $global:LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if (-not $succeeded -or $exitCode -ne 0) {
            throw "$Label failed with exit code $exitCode."
        }
    } finally {
        Pop-Location
    }
}

function Invoke-PowerShellCommandLineWithTimeout {
    param(
        [string]$Command,
        [string]$Label,
        [int]$TimeoutSeconds
    )

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    try {
        $script = @"
Set-Location -LiteralPath $(Quote-PSArgument $script:SolutionRoot)
`$global:LASTEXITCODE = 0
try {
    Invoke-Expression $(Quote-PSArgument $Command)
    `$succeeded = `$?
    `$exitCode = `$global:LASTEXITCODE
    if (-not `$succeeded -or `$exitCode -ne 0) {
        if (`$exitCode -ne 0) { exit `$exitCode }
        exit 1
    }
} catch {
    Write-Error `$_
    exit 1
}
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        $powerShellPath = (Get-Process -Id $PID).Path
        $process = Start-Process `
            -FilePath $powerShellPath `
            -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encoded) `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -NoNewWindow `
            -PassThru
        # Windows PowerShell 5.1 can lose ExitCode after a very short-lived
        # redirected child unless the native process handle is materialized
        # before it exits.
        $null = $process.Handle

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree $process.Id
            throw "$Label timed out after $TimeoutSeconds seconds."
        }
        $process.WaitForExit()
        $process.Refresh()

        Write-ProcessOutput $stdoutPath $stderrPath
        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-BashCommandLine {
    param(
        [string]$Command,
        [string]$Label,
        [int]$TimeoutSeconds = 0
    )

    $bashPath = Resolve-GitBashPath
    Write-Host "[$Label] $bashPath -lc $Command"
    if ($TimeoutSeconds -gt 0) {
        Invoke-BashCommandLineWithTimeout $bashPath $Command $Label $TimeoutSeconds
        return
    }

    Push-Location $script:SolutionRoot
    try {
        $global:LASTEXITCODE = 0
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            & $bashPath -lc $Command
            $succeeded = $?
            $exitCode = $global:LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if (-not $succeeded -or $exitCode -ne 0) {
            throw "$Label failed with exit code $exitCode."
        }
    } finally {
        Pop-Location
    }
}

function Invoke-BashCommandLineWithTimeout {
    param(
        [string]$BashPath,
        [string]$Command,
        [string]$Label,
        [int]$TimeoutSeconds
    )

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    try {
        $process = Start-Process `
            -FilePath $BashPath `
            -ArgumentList @("-lc", "cd $(ConvertTo-BashPath $script:SolutionRoot) && $Command") `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -NoNewWindow `
            -PassThru
        $null = $process.Handle

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree $process.Id
            throw "$Label timed out after $TimeoutSeconds seconds."
        }
        $process.WaitForExit()
        $process.Refresh()

        Write-ProcessOutput $stdoutPath $stderrPath
        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function ConvertTo-BashPath {
    param([string]$Path)

    $fullPath = (Resolve-Path -LiteralPath $Path).Path
    $escaped = $fullPath.Replace("\", "/").Replace("'", "'\''")
    if ($escaped -match "^([A-Za-z]):/(.*)$") {
        $drive = $Matches[1].ToLowerInvariant()
        $rest = $Matches[2]
        return "'/$drive/$rest'"
    }

    return "'$escaped'"
}

function Write-ProcessOutput {
    param(
        [string]$StdoutPath,
        [string]$StderrPath
    )

    if (Test-Path -LiteralPath $StdoutPath) {
        $stdout = Get-Content -LiteralPath $StdoutPath -Raw
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout.TrimEnd()
        }
    }

    if (Test-Path -LiteralPath $StderrPath) {
        $stderr = Get-Content -LiteralPath $StderrPath -Raw
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Host $stderr.TrimEnd()
        }
    }
}

function Stop-ProcessTree {
    param([int]$ProcessId)

    $null = & taskkill.exe /PID $ProcessId /T /F 2>$null
    if ($LASTEXITCODE -ne 0) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-TrialCommandLine {
    param(
        [string]$Command,
        [string]$Label
    )

    if ($TrialShell -eq "git-bash") {
        Invoke-BashCommandLine $Command $Label $TrialTimeoutSeconds
        return
    }

    Invoke-CommandLine $Command $Label $TrialTimeoutSeconds
}

function Read-BenchmarkReport {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Benchmark report was not written: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-WorkloadIdentity {
    param($Report)

    $diagnostics = $Report.LastDiagnostics
    $camera = $diagnostics.CaptureCamera
    $producer = $Report.ProducerIdentity
    $parts = @(
        [string]$Report.Scenario,
        [string]$diagnostics.CaptureRenderWidth,
        [string]$diagnostics.CaptureRenderHeight,
        [string]$diagnostics.ActiveQualityPreset,
        [string]$diagnostics.CaptureSceneAssetHash,
        [string]$diagnostics.CaptureSceneStateHash,
        [string]$camera.ViewHash,
        [string]$camera.ProjectionHash,
        [string]$diagnostics.ResolvedGiSettings.StableHash,
        [string]$diagnostics.ActiveFeatureIsolation,
        [string]$diagnostics.GlobalIlluminationDebugView,
        [string]$producer.SettingsFingerprint
    )
    return $parts -join "|"
}

function Assert-BenchmarkReport {
    param(
        $Report,
        [string]$Label,
        [bool]$ReferenceInitialization = $false
    )

    if ([string]$Report.Kind -ne "njulf-renderer-benchmark") {
        throw "$Label has unexpected report kind '$($Report.Kind)'."
    }
    if ([int]$Report.MeasurementFrameCount -ne $MeasureFrames) {
        throw "$Label captured $($Report.MeasurementFrameCount) frames; expected $MeasureFrames."
    }
    if ([bool]$Report.SettlingWaitTimedOut) {
        throw "$Label exhausted the convergence settling window."
    }
    if ($null -eq $Report.CaptureContract -or -not [bool]$Report.CaptureContract.Comparable) {
        $mismatches = @($Report.CaptureContract.Mismatches) -join "; "
        throw "$Label capture contract is not comparable: $mismatches"
    }
    if (-not $ReferenceInitialization -and $RequireProductionTiming -and
        -not [bool]$Report.CaptureContract.ProductionTiming) {
        throw "$Label is not a production-timing capture."
    }
    if ([int]$Report.GpuTimingSupported -eq 0 -or
        [int]$Report.GpuTimingValidSampleCount -ne $MeasureFrames -or
        [int]$Report.GpuFrameMilliseconds.Count -ne $MeasureFrames) {
        throw "$Label lacks complete GPU timing ($($Report.GpuTimingValidSampleCount)/$MeasureFrames valid samples)."
    }
    if ([int]$Report.CpuFrameMilliseconds.Count -ne $MeasureFrames) {
        throw "$Label lacks complete CPU timing."
    }
    if (-not $ReferenceInitialization -and
        -not [string]::IsNullOrWhiteSpace($script:ResolvedHdrReferencePath)) {
        if ($null -eq $Report.HdrDifference -or -not [bool]$Report.HdrDifference.Available) {
            throw "$Label lacks HDR comparison evidence: $($Report.HdrDifference.FailureReason)"
        }
        if (-not [bool]$Report.HdrDifference.Passed) {
            throw "$Label failed HDR quality: $($Report.HdrDifference.FailureReason)"
        }
        if ([double]$Report.HdrDifference.MaximumRelativeRmse -ne $MaximumHdrRelativeRmse) {
            throw "$Label used HDR RMSE limit $($Report.HdrDifference.MaximumRelativeRmse), expected $MaximumHdrRelativeRmse."
        }
    }
}

function Assert-BenchmarkSet {
    param(
        $Reports,
        [string]$Label
    )

    if ((Get-CollectionCount $Reports) -ne $RepeatCount) {
        throw "$Label produced $((Get-CollectionCount $Reports)) reports; expected $RepeatCount."
    }

    $identity = $null
    $fullIdentity = $null
    foreach ($report in $Reports) {
        Assert-BenchmarkReport $report $Label
        $currentIdentity = Get-WorkloadIdentity $report
        $currentFullIdentity = [string]$report.CaptureContract.FullIdentityHash
        if ($null -eq $identity) {
            $identity = $currentIdentity
            $fullIdentity = $currentFullIdentity
            continue
        }
        if (-not [string]::Equals($identity, $currentIdentity, [StringComparison]::Ordinal)) {
            throw "$Label repeats used different workload identities."
        }
        if (-not [string]::Equals($fullIdentity, $currentFullIdentity, [StringComparison]::Ordinal)) {
            throw "$Label repeats used different exact rendered states."
        }
    }
}

function Assert-CrossPhaseWorkloadIdentity {
    param(
        $BaselineReports,
        $CandidateReports
    )

    $baselineIdentity = Get-WorkloadIdentity $BaselineReports[0]
    $candidateIdentity = Get-WorkloadIdentity $CandidateReports[0]
    if (-not [string]::Equals(
            $baselineIdentity,
            $candidateIdentity,
            [StringComparison]::Ordinal)) {
        throw "Baseline and candidate used different scene, camera, settings, or scene-state identities."
    }
}

function Get-JsonPropertyValue {
    param(
        $Object,
        [string]$Name,
        $DefaultValue
    )

    if ($null -eq $Object) {
        return $DefaultValue
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Get-CollectionCount {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Length
}

function Invoke-BenchmarkSet {
    param(
        [int]$Iteration,
        [string]$Phase
    )

    Invoke-BenchmarkBuild "$Phase build"
    $reports = @()
    for ($repeat = 1; $repeat -le $RepeatCount; $repeat++) {
        $iterationDirectory = Join-Path $script:RunRoot ("iteration-{0:000}" -f $Iteration)
        New-Item -ItemType Directory -Force -Path $iterationDirectory | Out-Null
        $reportPath = Join-Path $iterationDirectory ("{0}-{1:00}.json" -f $Phase, $repeat)

        if ([string]::IsNullOrWhiteSpace($BenchmarkCommand)) {
            $command = Get-DefaultBenchmarkCommand $reportPath $Iteration $Phase $repeat
            $healthReportPath = [System.IO.Path]::ChangeExtension(
                $reportPath,
                ".health.json")
            Invoke-ValidatedBenchmarkCommand `
                $command `
                "$Phase benchmark $repeat/$RepeatCount" `
                $healthReportPath `
                $BenchmarkTimeoutSeconds
        } else {
            $command = Expand-CommandTemplate $BenchmarkCommand $reportPath $Iteration $Phase $repeat
            Invoke-CommandLine $command "$Phase benchmark $repeat/$RepeatCount" $BenchmarkTimeoutSeconds
        }
        $reports += Read-BenchmarkReport $reportPath
    }

    Assert-BenchmarkSet $reports $Phase
    return $reports
}

function Get-TimingValue {
    param(
        $Report,
        [string]$Metric,
        [ValidateSet("p50", "p95", "p99")]
        [string]$Percentile = "p95"
    )

    $propertyName = switch ($Percentile) {
        "p50" { "P50Milliseconds" }
        "p99" { "P99Milliseconds" }
        default { "P95Milliseconds" }
    }

    if ($Metric -eq "gpu") {
        $validSamples = [int](Get-JsonPropertyValue $Report "GpuTimingValidSampleCount" 0)
        $gpuFrame = Get-JsonPropertyValue $Report "GpuFrameMilliseconds" $null
        $gpuCount = [int](Get-JsonPropertyValue $gpuFrame "Count" 0)
        if ($validSamples -gt 0 -and $gpuCount -gt 0) {
            return [double](Get-JsonPropertyValue $gpuFrame $propertyName 0)
        }

        return $null
    }

    $cpuFrame = Get-JsonPropertyValue $Report "CpuFrameMilliseconds" $null
    $cpuCount = [int](Get-JsonPropertyValue $cpuFrame "Count" 0)
    if ($cpuCount -gt 0) {
        return [double](Get-JsonPropertyValue $cpuFrame $propertyName 0)
    }

    return $null
}

function Get-Median {
    param([double[]]$Values)

    $items = @($Values)
    $itemCount = Get-CollectionCount $items
    if ($itemCount -eq 0) {
        throw "Cannot compute a median for an empty value set."
    }

    $sorted = @($items | Sort-Object)
    $sortedCount = Get-CollectionCount $sorted
    $middle = [int]($sortedCount / 2)
    if (($sortedCount % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-MedianTiming {
    param(
        $Reports,
        [string]$Metric,
        [ValidateSet("p50", "p95", "p99")]
        [string]$Percentile = "p95"
    )

    $values = @()
    foreach ($report in $Reports) {
        $value = Get-TimingValue $report $Metric $Percentile
        if ($value -eq $null) {
            throw "Metric '$Metric' is unavailable in at least one benchmark report."
        }

        $values += [double]$value
    }

    return Get-Median $values
}

function Get-ImprovementPercent {
    param(
        [double]$Baseline,
        [double]$Candidate
    )

    if ($Baseline -le 0.0) {
        throw "Baseline timing must be greater than zero."
    }

    return (($Baseline - $Candidate) / $Baseline) * 100.0
}

function Test-TargetMet {
    param($Reports)

    $cpuP95 = Get-MedianTiming $Reports "cpu" "p95"
    $gpuP95 = Get-MedianTiming $Reports "gpu" "p95"
    $cpuP99 = Get-MedianTiming $Reports "cpu" "p99"
    $gpuP99 = Get-MedianTiming $Reports "gpu" "p99"
    return $cpuP95 -le $TargetP95Milliseconds -and
        $gpuP95 -le $TargetP95Milliseconds -and
        $cpuP99 -le $TargetP99Milliseconds -and
        $gpuP99 -le $TargetP99Milliseconds
}

function Convert-BudgetStatus {
    param($Status)

    if ($Status -is [int]) {
        return [int]$Status
    }

    $text = ([string]$Status).Trim()
    switch ($text) {
        "Unknown" { return 0 }
        "WithinBudget" { return 1 }
        "Warning" { return 2 }
        "OverBudget" { return 3 }
        "Unavailable" { return 4 }
        default {
            $parsed = 0
            if ([int]::TryParse($text, [ref]$parsed)) {
                return $parsed
            }

            return 0
        }
    }
}

function Get-WorstBudgetStatusByName {
    param($Reports)

    $statuses = @{}
    foreach ($report in $Reports) {
        foreach ($metric in @($report.BudgetMetrics)) {
            $name = [string]$metric.Name
            $status = Convert-BudgetStatus $metric.Status
            if (-not $statuses.ContainsKey($name) -or $status -gt $statuses[$name]) {
                $statuses[$name] = $status
            }
        }
    }

    return $statuses
}

function Get-BudgetRegressions {
    param(
        $BaselineReports,
        $CandidateReports
    )

    $baseline = Get-WorstBudgetStatusByName $BaselineReports
    $candidate = Get-WorstBudgetStatusByName $CandidateReports
    $regressions = @()

    foreach ($name in $candidate.Keys) {
        $before = 0
        if ($baseline.ContainsKey($name)) {
            $before = $baseline[$name]
        }

        $after = $candidate[$name]
        if ($after -gt $before) {
            $regressions += "$name status $before -> $after"
        }
    }

    return $regressions
}

function Compare-BenchmarkSets {
    param(
        $BaselineReports,
        $CandidateReports
    )

    Assert-CrossPhaseWorkloadIdentity $BaselineReports $CandidateReports

    $baselineCpuP50 = Get-MedianTiming $BaselineReports "cpu" "p50"
    $baselineCpuP95 = Get-MedianTiming $BaselineReports "cpu" "p95"
    $baselineCpuP99 = Get-MedianTiming $BaselineReports "cpu" "p99"
    $baselineGpuP50 = Get-MedianTiming $BaselineReports "gpu" "p50"
    $baselineGpuP95 = Get-MedianTiming $BaselineReports "gpu" "p95"
    $baselineGpuP99 = Get-MedianTiming $BaselineReports "gpu" "p99"
    $candidateCpuP50 = Get-MedianTiming $CandidateReports "cpu" "p50"
    $candidateCpuP95 = Get-MedianTiming $CandidateReports "cpu" "p95"
    $candidateCpuP99 = Get-MedianTiming $CandidateReports "cpu" "p99"
    $candidateGpuP50 = Get-MedianTiming $CandidateReports "gpu" "p50"
    $candidateGpuP95 = Get-MedianTiming $CandidateReports "gpu" "p95"
    $candidateGpuP99 = Get-MedianTiming $CandidateReports "gpu" "p99"
    $baselineBottleneckP95 = [Math]::Max($baselineCpuP95, $baselineGpuP95)
    $candidateBottleneckP95 = [Math]::Max($candidateCpuP95, $candidateGpuP95)
    $improvementPercent = Get-ImprovementPercent $baselineBottleneckP95 $candidateBottleneckP95
    $budgetRegressions = Get-BudgetRegressions $BaselineReports $CandidateReports
    $timingRegressions = @()
    foreach ($comparisonMetric in @(
        @("CPU p95", $baselineCpuP95, $candidateCpuP95),
        @("CPU p99", $baselineCpuP99, $candidateCpuP99),
        @("GPU p95", $baselineGpuP95, $candidateGpuP95),
        @("GPU p99", $baselineGpuP99, $candidateGpuP99))) {
        $regressionPercent = -(Get-ImprovementPercent `
            ([double]$comparisonMetric[1]) `
            ([double]$comparisonMetric[2]))
        if ($regressionPercent -gt $MaxRegressionPercent) {
            $timingRegressions += "$($comparisonMetric[0]) regressed by $([Math]::Round($regressionPercent, 3))%"
        }
    }

    $hdrRelativeRmse = Get-Median @(
        $CandidateReports | ForEach-Object { [double]$_.HdrDifference.RelativeRmse })

    $decision = "rollback"
    $reason = ""

    if ((Get-CollectionCount $timingRegressions) -gt 0) {
        $reason = "timing regression: $($timingRegressions -join '; ')"
    } elseif ((Get-CollectionCount $budgetRegressions) -gt 0) {
        $reason = "budget regression: $($budgetRegressions -join '; ')"
    } elseif ($improvementPercent -ge $MinImprovementPercent) {
        $decision = "keep"
        $reason = "improved by $([Math]::Round($improvementPercent, 3))%"
    } elseif ($improvementPercent -le -$MaxRegressionPercent) {
        $reason = "regressed by $([Math]::Round(-$improvementPercent, 3))%"
    } elseif ($KeepInconclusive) {
        $decision = "keep"
        $reason = "inconclusive, kept by policy: $([Math]::Round($improvementPercent, 3))%"
    } else {
        $reason = "inconclusive: $([Math]::Round($improvementPercent, 3))%"
    }

    return [pscustomobject]@{
        Decision = $decision
        Reason = $reason
        Metric = "cpu+gpu bottleneck"
        BaselineP95Milliseconds = $baselineBottleneckP95
        CandidateP95Milliseconds = $candidateBottleneckP95
        ImprovementPercent = $improvementPercent
        BaselineCpu = [pscustomobject]@{ P50 = $baselineCpuP50; P95 = $baselineCpuP95; P99 = $baselineCpuP99 }
        CandidateCpu = [pscustomobject]@{ P50 = $candidateCpuP50; P95 = $candidateCpuP95; P99 = $candidateCpuP99 }
        BaselineGpu = [pscustomobject]@{ P50 = $baselineGpuP50; P95 = $baselineGpuP95; P99 = $baselineGpuP99 }
        CandidateGpu = [pscustomobject]@{ P50 = $candidateGpuP50; P95 = $candidateGpuP95; P99 = $candidateGpuP99 }
        CandidateHdrRelativeRmse = $hdrRelativeRmse
        TargetMet = Test-TargetMet $CandidateReports
        TimingRegressions = $timingRegressions
        BudgetRegressions = $budgetRegressions
    }
}

function Write-IterationSummary {
    param(
        [int]$Iteration,
        [object]$Summary
    )

    $iterationDirectory = Join-Path $script:RunRoot ("iteration-{0:000}" -f $Iteration)
    New-Item -ItemType Directory -Force -Path $iterationDirectory | Out-Null
    $path = Join-Path $iterationDirectory "decision.json"
    $Summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path
    Write-Host "Decision written: $path"
}

New-Item -ItemType Directory -Force -Path $script:RunRoot | Out-Null
$protectedPathFingerprint = Get-ProtectedPathFingerprint

if ($InitializeHdrReference) {
    if ([string]::IsNullOrWhiteSpace($script:ResolvedHdrReferencePath)) {
        throw "InitializeHdrReference requires -HdrReferencePath."
    }
    if (Test-Path -LiteralPath $script:ResolvedHdrReferencePath) {
        throw "HDR reference already exists and will not be overwritten: $($script:ResolvedHdrReferencePath)"
    }

    $referenceDirectory = Join-Path $script:RunRoot "reference"
    New-Item -ItemType Directory -Force -Path $referenceDirectory | Out-Null
    $referenceReportPath = Join-Path $referenceDirectory "reference.json"
    Invoke-BenchmarkBuild "HDR reference build"
    $referenceCommand = Get-DefaultBenchmarkCommand `
        $referenceReportPath `
        0 `
        "reference" `
        1 `
        -ReferenceInitialization
    $referenceHealthReportPath = [System.IO.Path]::ChangeExtension(
        $referenceReportPath,
        ".health.json")
    Invoke-ValidatedBenchmarkCommand `
        $referenceCommand `
        "HDR reference initialization" `
        $referenceHealthReportPath `
        $BenchmarkTimeoutSeconds
    $referenceReport = Read-BenchmarkReport $referenceReportPath
    Assert-BenchmarkReport $referenceReport "HDR reference initialization" $true
    if (-not (Test-Path -LiteralPath $script:ResolvedHdrReferencePath -PathType Leaf)) {
        throw "HDR reference capture was not written: $($script:ResolvedHdrReferencePath)"
    }
    Assert-ProtectedPathUnchanged $protectedPathFingerprint
    Write-Host "HDR reference established: $($script:ResolvedHdrReferencePath)"
    if ($InitializeHdrReferenceOnly) {
        exit 0
    }
}

if ($BaselineOnly) {
    $baselineReports = Invoke-BenchmarkSet 0 "baseline"
    Assert-ProtectedPathUnchanged $protectedPathFingerprint
    $baselineSummary = [pscustomobject]@{
        Scene = $Scene
        Scenario = $Scenario
        RepeatCount = $RepeatCount
        MeasureFrames = $MeasureFrames
        CpuP50Milliseconds = Get-MedianTiming $baselineReports "cpu" "p50"
        CpuP95Milliseconds = Get-MedianTiming $baselineReports "cpu" "p95"
        CpuP99Milliseconds = Get-MedianTiming $baselineReports "cpu" "p99"
        GpuP50Milliseconds = Get-MedianTiming $baselineReports "gpu" "p50"
        GpuP95Milliseconds = Get-MedianTiming $baselineReports "gpu" "p95"
        GpuP99Milliseconds = Get-MedianTiming $baselineReports "gpu" "p99"
        TargetMet = Test-TargetMet $baselineReports
        HdrReferencePath = $script:ResolvedHdrReferencePath
        ProtectedPathFingerprint = $protectedPathFingerprint
    }
    $baselineSummaryPath = Join-Path $script:RunRoot "baseline-summary.json"
    $baselineSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $baselineSummaryPath
    Write-Host "Baseline complete: $baselineSummaryPath"
    exit 0
}

$allSummaries = @()

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    Write-Host ""
    Write-Host "=== Perf loop iteration $iteration/$Iterations ==="

    $checkpointHash = $null
    $comparison = $null
    $decision = "rollback"
    $reason = ""
    $failed = $false

    try {
        $checkpointHash = New-PretrialCheckpoint $iteration
        $baselineReports = Invoke-BenchmarkSet $iteration "baseline"

        if (Test-TargetMet $baselineReports) {
            $decision = "keep"
            $reason = "target already met before trial"
            $comparison = [pscustomobject]@{
                Decision = $decision
                Reason = $reason
                Metric = "cpu+gpu bottleneck"
                BaselineP95Milliseconds = [Math]::Max(
                    (Get-MedianTiming $baselineReports "cpu" "p95"),
                    (Get-MedianTiming $baselineReports "gpu" "p95"))
                CandidateP95Milliseconds = $null
                ImprovementPercent = 0.0
                BaselineCpu = [pscustomobject]@{
                    P50 = Get-MedianTiming $baselineReports "cpu" "p50"
                    P95 = Get-MedianTiming $baselineReports "cpu" "p95"
                    P99 = Get-MedianTiming $baselineReports "cpu" "p99"
                }
                CandidateCpu = $null
                BaselineGpu = [pscustomobject]@{
                    P50 = Get-MedianTiming $baselineReports "gpu" "p50"
                    P95 = Get-MedianTiming $baselineReports "gpu" "p95"
                    P99 = Get-MedianTiming $baselineReports "gpu" "p99"
                }
                CandidateGpu = $null
                CandidateHdrRelativeRmse = $null
                TargetMet = $true
                TimingRegressions = @()
                BudgetRegressions = @()
            }
        } else {
            $expandedTrialCommand = Expand-CommandTemplate $TrialCommand "" $iteration "trial" 0
            Invoke-TrialCommandLine $expandedTrialCommand "trial command"
            Assert-ProtectedPathUnchanged $protectedPathFingerprint

            $candidateReports = Invoke-BenchmarkSet $iteration "candidate"
            Assert-ProtectedPathUnchanged $protectedPathFingerprint
            $comparison = Compare-BenchmarkSets $baselineReports $candidateReports
            $decision = $comparison.Decision
            $reason = $comparison.Reason
        }
    } catch {
        $failed = $true
        $decision = "rollback"
        $reason = $_.Exception.Message
    }

    if ($decision -eq "keep") {
        if (-not [string]::IsNullOrWhiteSpace($checkpointHash)) {
            Drop-StashByHash $checkpointHash
        }

        Write-Host "KEEP: $reason"
    } else {
        Write-Host "ROLLBACK: $reason"
        if ($RollbackRejected) {
            Restore-PretrialCheckpoint $checkpointHash $iteration
            if (-not [string]::IsNullOrWhiteSpace($checkpointHash)) {
                Drop-StashByHash $checkpointHash
            }

            Write-Host "Pretrial worktree restored."
        } else {
            Write-Host "RollbackRejected is false; candidate changes remain in the worktree."
        }
    }

    $summary = [pscustomobject]@{
        Iteration = $iteration
        Decision = $decision
        Reason = $reason
        Failed = $failed
        Metric = if ($comparison -eq $null) { $null } else { $comparison.Metric }
        BaselineP95Milliseconds = if ($comparison -eq $null) { $null } else { $comparison.BaselineP95Milliseconds }
        CandidateP95Milliseconds = if ($comparison -eq $null) { $null } else { $comparison.CandidateP95Milliseconds }
        ImprovementPercent = if ($comparison -eq $null) { $null } else { $comparison.ImprovementPercent }
        BaselineCpu = if ($comparison -eq $null) { $null } else { $comparison.BaselineCpu }
        CandidateCpu = if ($comparison -eq $null) { $null } else { $comparison.CandidateCpu }
        BaselineGpu = if ($comparison -eq $null) { $null } else { $comparison.BaselineGpu }
        CandidateGpu = if ($comparison -eq $null) { $null } else { $comparison.CandidateGpu }
        CandidateHdrRelativeRmse = if ($comparison -eq $null) { $null } else { $comparison.CandidateHdrRelativeRmse }
        TargetMet = if ($comparison -eq $null) { $false } else { [bool]$comparison.TargetMet }
        TimingRegressions = if ($comparison -eq $null) { @() } else { $comparison.TimingRegressions }
        BudgetRegressions = if ($comparison -eq $null) { @() } else { $comparison.BudgetRegressions }
    }
    $allSummaries += $summary
    Write-IterationSummary $iteration $summary

    if ($summary.TargetMet) {
        Write-Host "60 FPS target met; stopping the loop."
        break
    }
}

$summaryPath = Join-Path $script:RunRoot "summary.json"
$allSummaries | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath
Write-Host ""
Write-Host "Perf loop complete: $summaryPath"
