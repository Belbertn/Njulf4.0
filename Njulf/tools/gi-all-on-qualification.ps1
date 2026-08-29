[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateSet("material-showcase", "sponza", "bistro")]
    [string]$Scene = "material-showcase",

    [ValidateRange(3, 10000)]
    [int]$MaximumFrameCount = 1800,

    [ValidateSet("Debug", "Development", "Release")]
    [string]$Configuration = "Release",

    [string]$BridgeDirectory = "artifacts/advanced-gi-source-validation-20260811/OMM-bridge-artifact",

    [switch]$SkipBuild,
    [switch]$SkipCook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$solutionRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath(
        (Join-Path $solutionRoot $OutputDirectory))
}
$solution = Join-Path $solutionRoot "Njulf.sln"
$gameProject = Join-Path $solutionRoot "NjulfHelloGame/NjulfHelloGame.csproj"
$cookScript = Join-Path $PSScriptRoot "cook-gi-all-on-c1.ps1"
$reportPath = Join-Path $outputRoot "gi-all-on-runtime.json"

function Invoke-DotnetChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Role
    )
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Role failed with exit code $LASTEXITCODE."
    }
}

if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
    throw "A qualification report already exists; use a fresh output directory: $reportPath"
}
[void](New-Item -ItemType Directory -Path $outputRoot -Force)

Push-Location $solutionRoot
try {
    if (-not $SkipBuild) {
        Invoke-DotnetChecked `
            -Arguments @(
                "build", $solution,
                "-c", $Configuration,
                "--no-restore") `
            -Role "Solution build"
    }

    if (-not $SkipCook) {
        & $cookScript `
            -Configuration $Configuration `
            -BridgeDirectory $BridgeDirectory `
            -SkipBuild
    }

    & dotnet @(
        "run",
        "--project", $gameProject,
        "-c", $Configuration,
        "--no-build",
        "--",
        "--gi-all-on-qualification-report", $reportPath,
        "--scene", $Scene,
        "--smoke-frames", $MaximumFrameCount)
    $gameExitCode = $LASTEXITCODE

    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "The runtime did not publish its required report (exit=$gameExitCode)."
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ([int]$report.SchemaVersion -ne 1 -or
        [string]$report.Kind -cne "gi-all-on-runtime-qualification") {
        throw "The runtime report does not match the all-on qualification contract."
    }
    if ($gameExitCode -ne 0 -or
        -not [bool]$report.Passed -or
        [string]$report.Status -cne "passed") {
        $failures = @($report.Failures | ForEach-Object {
            "{0}: {1}" -f $_.Name, $_.Detail
        }) -join " "
        throw (
            "All-on GI qualification failed (exit=$gameExitCode). " +
            "$failures Report: $reportPath")
    }

    Write-Host (
        ("All-on GI qualification passed: scene={0}, frames={1}, " +
         "firstSerial={2}, lastSerial={3}, report='{4}'.") -f
        $report.Scene,
        $report.RenderedFrameCount,
        $report.Runtime.FirstFrameSerial,
        $report.Runtime.LastFrameSerial,
        $reportPath)
} finally {
    Pop-Location
}
