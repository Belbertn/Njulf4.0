[CmdletBinding()]
param(
    [ValidateSet(
        "Debug",
        "Development",
        "Release",
        "ShippingPerformance",
        "ProfileSymbols",
        "DetailedInvestigation")]
    [string]$Configuration = "Development",

    [switch]$SkipBuild,
    [switch]$SkipCook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$solutionRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$assetToolProject = Join-Path $solutionRoot (
    "Njulf.AssetTool/Njulf.AssetTool.csproj")
$testProject = Join-Path $solutionRoot "Njulf.Tests/Njulf.Tests.csproj"
$gameProject = Join-Path $solutionRoot (
    "NjulfHelloGame/NjulfHelloGame.csproj")
$bistroRoot = Join-Path $solutionRoot (
    "NjulfHelloGame/Assets/Bistro_v5_2")
$cookedRoot = Join-Path $solutionRoot "NjulfHelloGame/Cooked"
$bistroSources = @(
    (Join-Path $bistroRoot "BistroExterior.fbx"),
    (Join-Path $bistroRoot "BistroInterior.fbx")
)
$cookedModels = @(
    (Join-Path $cookedRoot "win-x64/models/BistroExterior.njmodel"),
    (Join-Path $cookedRoot "win-x64/models/BistroInterior.njmodel")
)
$testFilter =
    "FullyQualifiedName=Njulf.Tests.BistroCookedReflectionIntegrationTests.BothBistroCooks_ResolveUnderExactRuntimeImportContracts|" +
    "FullyQualifiedName=Njulf.Tests.BistroCookedReflectionIntegrationTests.ExteriorCook_PreservesThinGlassAndImportSemantics|" +
    "FullyQualifiedName=Njulf.Tests.BistroCookedReflectionIntegrationTests.BothBistroCooks_PreserveCompressedMaterialTextureBindings"

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

Push-Location $solutionRoot
try {
    if (-not $SkipCook) {
        foreach ($source in $bistroSources) {
            Assert-File -Path $source -Role "Amazon Bistro source model"
        }
    }

    if (-not $SkipBuild) {
        Invoke-DotnetChecked `
            -Arguments @(
                "build", $assetToolProject,
                "-c", $Configuration) `
            -Role "Asset tool build"
    }

    if (-not $SkipCook) {
        foreach ($source in $bistroSources) {
            # AutoBc maps color/data to BC7 and normals to BC5. TextureCooker
            # generates every level down to 1x1 before encoding the KTX2.
            Invoke-DotnetChecked `
                -Arguments @(
                    "run",
                    "--project", $assetToolProject,
                    "-c", $Configuration,
                    "--no-build",
                    "--",
                    "cook", "model", $source,
                    "--out", $cookedRoot,
                    "--platform", "win-x64",
                    "--backend", "Assimp",
                    "--assimp-material-texture-convention", "AmazonBistro",
                    "--texture-format", "AutoBc",
                    "--force",
                    "--progress", "plain",
                    "--progress-detail", "stages") `
                -Role "Amazon Bistro cook for $(Split-Path $source -Leaf)"
        }

        foreach ($model in $cookedModels) {
            Assert-File -Path $model -Role "Cooked Amazon Bistro model"
        }
    }

    $testArguments = @(
        "test", $testProject,
        "-c", $Configuration,
        "--filter", $testFilter)
    if ($SkipBuild) {
        $testArguments += "--no-build"
    }
    Invoke-DotnetChecked `
        -Arguments $testArguments `
        -Role "Cooked Bistro contract and material tests"

    if (-not $SkipBuild) {
        Invoke-DotnetChecked `
            -Arguments @(
                "build", $gameProject,
                "-c", $Configuration,
                "--no-restore") `
            -Role "Game rebuild with cooked Bistro assets"
    }

    $cookedModels | Write-Output
} finally {
    Pop-Location
}
