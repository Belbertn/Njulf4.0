[CmdletBinding()]
param(
    [ValidateSet("Debug", "Development", "Release")]
    [string]$Configuration = "Release",

    [string]$BridgeDirectory = "artifacts/advanced-gi-source-validation-20260811/OMM-bridge-artifact",

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$solutionRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$assetToolProject = Join-Path $solutionRoot "Njulf.AssetTool/Njulf.AssetTool.csproj"
$sourceModel = Join-Path $solutionRoot (
    "NjulfHelloGame/Assets/ribbon_grass_tbdpec3r_ue_low/standard/" +
    "tbdpec3r_tier_3_nonUE.gltf")
$cookedRoot = Join-Path $solutionRoot "NjulfHelloGame/Cooked"
$cookedModel = Join-Path $cookedRoot (
    "win-x64/models/tbdpec3r_tier_3_nonUE.njmodel")
$resolvedBridgeDirectory = if (
    [System.IO.Path]::IsPathRooted($BridgeDirectory)) {
    [System.IO.Path]::GetFullPath($BridgeDirectory)
} else {
    [System.IO.Path]::GetFullPath(
        (Join-Path $solutionRoot $BridgeDirectory))
}
$bridge = Join-Path $resolvedBridgeDirectory "njulf_omm_bridge.dll"
$provenance = Join-Path $resolvedBridgeDirectory (
    "njulf_omm_bridge.provenance.json")

function Assert-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Role
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Role is missing: $Path"
    }
}

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

if (-not [System.OperatingSystem]::IsWindows()) {
    throw "The pinned C1 qualification bridge currently targets Windows x64."
}
Assert-File -Path $sourceModel -Role "C1 alpha-mask source model"
Assert-File -Path $bridge -Role "Pinned C1 native bridge"
Assert-File -Path $provenance -Role "Pinned C1 bridge provenance"

Push-Location $solutionRoot
try {
    if (-not $SkipBuild) {
        Invoke-DotnetChecked `
            -Arguments @(
                "build", $assetToolProject,
                "-c", $Configuration,
                "--no-restore") `
            -Role "Asset tool build"
    }

    Invoke-DotnetChecked `
        -Arguments @(
            "run",
            "--project", $assetToolProject,
            "-c", $Configuration,
            "--no-build",
            "--",
            "cook", "model", $sourceModel,
            "--out", $cookedRoot,
            "--platform", "win-x64",
            "--backend", "SharpGltf",
            "--texture-format", "rgba8",
            "--max-sampler-anisotropy", "1",
            "--force",
            "--progress", "plain",
            "--progress-detail", "stages",
            "--omm-bridge", $bridge,
            "--omm-provenance", $provenance) `
        -Role "Pinned C1 fixture cook"

    Assert-File -Path $cookedModel -Role "Cooked C1 fixture"
    Invoke-DotnetChecked `
        -Arguments @(
            "run",
            "--project", $assetToolProject,
            "-c", $Configuration,
            "--no-build",
            "--",
            "advanced-gi", "verify-c1-model",
            "--model", $cookedModel) `
        -Role "Cooked C1 fixture verification"

    Write-Output $cookedModel
} finally {
    Pop-Location
}
