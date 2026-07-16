[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TrialCommand,

    [int]$Iterations = 1,
    [int]$RepeatCount = 1,
    [string]$RunDirectory = ".perf-loop-runs",
    [string]$BenchmarkCommand = "",
    [string]$ProjectPath = "NjulfHelloGame/NjulfHelloGame.csproj",
    [string]$Configuration = "Release",
    [string]$Scenario = "Normal",
    [int]$WarmupFrames = 30,
    [int]$MeasureFrames = 120,
    [int]$BenchmarkTimeoutSeconds = 900,
    [int]$TrialTimeoutSeconds = 1800,

    [ValidateSet("powershell", "git-bash")]
    [string]$TrialShell = "powershell",
    [string]$GitBashPath = "",

    [ValidateSet("auto", "cpu", "gpu")]
    [string]$PrimaryMetric = "auto",

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
    return (Get-GitText @("status", "--porcelain=v1", "--untracked-files=all"))
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
    $null = Invoke-Git @("stash", "push", "--include-untracked", "--message", $message)
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
        $null = Invoke-Git @("stash", "push", "--include-untracked", "--message", $message)
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
    param([string]$ReportPath)

    $project = if ([System.IO.Path]::IsPathRooted($ProjectPath)) {
        $ProjectPath
    } else {
        Join-Path $script:SolutionRoot $ProjectPath
    }

    $watchdogFrames = $WarmupFrames + $MeasureFrames + 30
    return "dotnet run --project $(Quote-PSArgument $project) -c $(Quote-PSArgument $Configuration) -- --benchmark --benchmark-report $(Quote-PSArgument $ReportPath) --benchmark-warmup-frames $WarmupFrames --benchmark-measure-frames $MeasureFrames --performance-scenario $(Quote-PSArgument $Scenario) --smoke-mode startup --smoke-frames $watchdogFrames"
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

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree $process.Id
            throw "$Label timed out after $TimeoutSeconds seconds."
        }

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

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree $process.Id
            throw "$Label timed out after $TimeoutSeconds seconds."
        }

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

    $reports = @()
    for ($repeat = 1; $repeat -le $RepeatCount; $repeat++) {
        $iterationDirectory = Join-Path $script:RunRoot ("iteration-{0:000}" -f $Iteration)
        New-Item -ItemType Directory -Force -Path $iterationDirectory | Out-Null
        $reportPath = Join-Path $iterationDirectory ("{0}-{1:00}.json" -f $Phase, $repeat)

        if ([string]::IsNullOrWhiteSpace($BenchmarkCommand)) {
            $command = Get-DefaultBenchmarkCommand $reportPath
        } else {
            $command = Expand-CommandTemplate $BenchmarkCommand $reportPath $Iteration $Phase $repeat
        }

        Invoke-CommandLine $command "$Phase benchmark $repeat/$RepeatCount" $BenchmarkTimeoutSeconds
        $reports += Read-BenchmarkReport $reportPath
    }

    return $reports
}

function Get-TimingValue {
    param(
        $Report,
        [string]$Metric
    )

    if ($Metric -eq "gpu") {
        $validSamples = [int](Get-JsonPropertyValue $Report "GpuTimingValidSampleCount" 0)
        $gpuFrame = Get-JsonPropertyValue $Report "GpuFrameMilliseconds" $null
        $gpuCount = [int](Get-JsonPropertyValue $gpuFrame "Count" 0)
        if ($validSamples -gt 0 -and $gpuCount -gt 0) {
            return [double](Get-JsonPropertyValue $gpuFrame "P95Milliseconds" 0)
        }

        return $null
    }

    $cpuFrame = Get-JsonPropertyValue $Report "CpuFrameMilliseconds" $null
    $cpuCount = [int](Get-JsonPropertyValue $cpuFrame "Count" 0)
    if ($cpuCount -gt 0) {
        return [double](Get-JsonPropertyValue $cpuFrame "P95Milliseconds" 0)
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

function Resolve-PrimaryMetric {
    param(
        $BaselineReports,
        $CandidateReports
    )

    if ($PrimaryMetric -ne "auto") {
        return $PrimaryMetric
    }

    $gpuUsable = $true
    foreach ($report in @(@($BaselineReports) + @($CandidateReports))) {
        if ((Get-TimingValue $report "gpu") -eq $null) {
            $gpuUsable = $false
            break
        }
    }

    if ($gpuUsable) {
        return "gpu"
    }

    return "cpu"
}

function Get-MedianTiming {
    param(
        $Reports,
        [string]$Metric
    )

    $values = @()
    foreach ($report in $Reports) {
        $value = Get-TimingValue $report $Metric
        if ($value -eq $null) {
            throw "Metric '$Metric' is unavailable in at least one benchmark report."
        }

        $values += [double]$value
    }

    return Get-Median $values
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

    $metric = Resolve-PrimaryMetric $BaselineReports $CandidateReports
    $baseline = Get-MedianTiming $BaselineReports $metric
    $candidate = Get-MedianTiming $CandidateReports $metric
    if ($baseline -le 0) {
        throw "Baseline $metric p95 must be greater than zero."
    }

    $improvementPercent = (($baseline - $candidate) / $baseline) * 100.0
    $budgetRegressions = Get-BudgetRegressions $BaselineReports $CandidateReports

    $decision = "rollback"
    $reason = ""

    if ((Get-CollectionCount $budgetRegressions) -gt 0) {
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
        Metric = $metric
        BaselineP95Milliseconds = $baseline
        CandidateP95Milliseconds = $candidate
        ImprovementPercent = $improvementPercent
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

        $expandedTrialCommand = Expand-CommandTemplate $TrialCommand "" $iteration "trial" 0
        Invoke-TrialCommandLine $expandedTrialCommand "trial command"

        $candidateReports = Invoke-BenchmarkSet $iteration "candidate"
        $comparison = Compare-BenchmarkSets $baselineReports $candidateReports
        $decision = $comparison.Decision
        $reason = $comparison.Reason
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
        BudgetRegressions = if ($comparison -eq $null) { @() } else { $comparison.BudgetRegressions }
    }
    $allSummaries += $summary
    Write-IterationSummary $iteration $summary
}

$summaryPath = Join-Path $script:RunRoot "summary.json"
$allSummaries | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath
Write-Host ""
Write-Host "Perf loop complete: $summaryPath"
