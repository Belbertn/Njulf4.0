[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [ValidateSet("Development", "Release")]
    [string]$Configuration = "Development",
    [switch]$SkipBuild,
    [switch]$SkipCook,
    [switch]$AnalyzeExisting
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$solutionRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath(
        (Join-Path $solutionRoot $OutputDirectory))
}
$assetToolProject = Join-Path $solutionRoot "Njulf.AssetTool/Njulf.AssetTool.csproj"
$gameProject = Join-Path $solutionRoot "NjulfHelloGame/NjulfHelloGame.csproj"
$testProject = Join-Path $solutionRoot "Njulf.Tests/Njulf.Tests.csproj"
$solution = Join-Path $solutionRoot "Njulf.sln"
$bistroSource = Join-Path $solutionRoot (
    "NjulfHelloGame/Assets/Bistro_v5_2/BistroExterior.fbx")
$cookedRoot = Join-Path $solutionRoot "NjulfHelloGame/Cooked"
$sourceReport = Join-Path $outputRoot "bistro-quality-run.json"

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

Push-Location $solutionRoot
try {
    if ($AnalyzeExisting) {
        if (-not (Test-Path -LiteralPath $sourceReport -PathType Leaf)) {
            throw "The existing Bistro report is missing: $sourceReport"
        }
    } else {
        if (-not (Test-Path -LiteralPath $bistroSource -PathType Leaf)) {
            throw "The Amazon Bistro source asset is missing: $bistroSource"
        }
        if (Test-Path -LiteralPath $outputRoot) {
            $existing = @(Get-ChildItem -LiteralPath $outputRoot -Force)
            if ($existing.Count -ne 0) {
                throw "The output directory must be empty for a new qualification run: $outputRoot"
            }
        } else {
            [void](New-Item -ItemType Directory -Path $outputRoot)
        }

        if (-not $SkipBuild) {
            Invoke-DotnetChecked `
                -Arguments @(
                    "build", $solution,
                    "-c", $Configuration,
                    "--no-restore") `
                -Role "Solution build"
        }

        if (-not $SkipCook) {
            Invoke-DotnetChecked `
                -Arguments @(
                    "run",
                    "--project", $assetToolProject,
                    "-c", $Configuration,
                    "--no-build",
                    "--",
                    "cook", "model", $bistroSource,
                    "--out", $cookedRoot,
                    "--platform", "win-x64",
                    "--backend", "Assimp",
                    "--assimp-material-texture-convention", "AmazonBistro",
                    "--force",
                    "--progress", "plain",
                    "--progress-detail", "stages") `
                -Role "Amazon Bistro model cook"
        }

        Invoke-DotnetChecked `
            -Arguments @(
                "test", $testProject,
                "-c", $Configuration,
                "--no-build",
                "--filter",
                "FullyQualifiedName=Njulf.Tests.BistroCookedReflectionIntegrationTests.ExteriorCook_PreservesThinGlassAndImportSemantics") `
            -Role "Cooked Bistro material-contract test"

        # The broader Bistro harness also evaluates DDGI scrolling/tail health.
        # Preserve its exit code and report, then let the scoped analyzer decide
        # the reflection result from authenticated frames and GPU counters.
        & dotnet @(
            "run",
            "--project", $gameProject,
            "-c", $Configuration,
            "--no-build",
            "--",
            "--bistro-quality-capture-dir=$outputRoot",
            "--bistro-quality-variant=hybrid-ray-query-ab",
            "--quality-preset=ddgi-high",
            "--validation=off",
            "--async-compute-mode=disabled",
            "--gpu-timing=true")
        $captureExitCode = $LASTEXITCODE
        if (-not (Test-Path -LiteralPath $sourceReport -PathType Leaf)) {
            throw "The Bistro hardware run did not publish its report (exit=$captureExitCode)."
        }
        if ($captureExitCode -ne 0) {
            Write-Warning (
                "The broader Bistro GI harness exited with code " +
                "$captureExitCode; reflection evidence will still be evaluated independently.")
        }
    }

    Invoke-DotnetChecked `
        -Arguments @(
            "run",
            "--project", $gameProject,
            "-c", $Configuration,
            "--no-build",
            "--",
            "--analyze-bistro-reflection-run", $outputRoot) `
        -Role "Bistro reflection evidence analysis"
} finally {
    Pop-Location
}
