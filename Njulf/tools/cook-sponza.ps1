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
$contentRoot = Join-Path $solutionRoot "NjulfHelloGame"
$cookedRoot = Join-Path $contentRoot "Cooked"
$sponzaSources = @(
    (Join-Path $contentRoot "NewSponza_Main_glTF_003.gltf"),
    (Join-Path $contentRoot "NewSponza_Curtains_glTF.gltf")
)
$cookedModels = @(
    (Join-Path $cookedRoot "win-x64/models/NewSponza_Main_glTF_003.njmodel"),
    (Join-Path $cookedRoot "win-x64/models/NewSponza_Curtains_glTF.njmodel")
)
$testFilter =
    "FullyQualifiedName=Njulf.Tests.SponzaCookedIntegrationTests.BothSponzaCooks_ResolveUnderExactRuntimeImportContracts"

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
        foreach ($source in $sponzaSources) {
            Assert-File -Path $source -Role "New Sponza source model"
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
        foreach ($source in $sponzaSources) {
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
                    "--backend", "SharpGltf",
                    "--texture-format", "AutoBc",
                    "--force",
                    "--progress", "plain",
                    "--progress-detail", "stages") `
                -Role "New Sponza cook for $(Split-Path $source -Leaf)"
        }

        foreach ($model in $cookedModels) {
            Assert-File -Path $model -Role "Cooked New Sponza model"
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
        -Role "Cooked Sponza contract test"

    if (-not $SkipBuild) {
        Invoke-DotnetChecked `
            -Arguments @(
                "build", $gameProject,
                "-c", $Configuration,
                "--no-restore") `
            -Role "Game rebuild with cooked Sponza assets"
    }

    $cookedModels | Write-Output
} finally {
    Pop-Location
}
