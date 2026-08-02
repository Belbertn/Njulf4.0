param(
    [Parameter(Mandatory = $true)]
    [string] $ShippingShaderDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ProfileShaderDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$shippingRoot = (Resolve-Path -LiteralPath $ShippingShaderDirectory).Path
$profileRoot = (Resolve-Path -LiteralPath $ProfileShaderDirectory).Path
$spirvOpt = (Get-Command spirv-opt -ErrorAction Stop).Source
$compiler = (Get-Command glslangValidator -ErrorAction Stop).Source
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("njulf-shader-parity-" + [Guid]::NewGuid().ToString('N'))
$manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $shippingFiles = @(Get-ChildItem -LiteralPath $shippingRoot -Filter *.spv -File -Recurse |
        Sort-Object { [IO.Path]::GetRelativePath($shippingRoot, $_.FullName) })
    if ($shippingFiles.Count -eq 0) {
        throw "No shipping SPIR-V artifacts were found under '$shippingRoot'."
    }

    $entries = @(foreach ($shippingFile in $shippingFiles) {
        $relativePath = [IO.Path]::GetRelativePath($shippingRoot, $shippingFile.FullName)
        $profileFile = Join-Path $profileRoot $relativePath
        if (-not (Test-Path -LiteralPath $profileFile -PathType Leaf)) {
            throw "ProfileSymbols artifact '$relativePath' is missing."
        }

        $shippingStripped = Join-Path $temporaryRoot ("shipping-" + [Guid]::NewGuid().ToString('N') + '.spv')
        $profileStripped = Join-Path $temporaryRoot ("profile-" + [Guid]::NewGuid().ToString('N') + '.spv')
        & $spirvOpt --strip-debug $shippingFile.FullName -o $shippingStripped
        if ($LASTEXITCODE -ne 0) {
            throw "spirv-opt failed while stripping '$($shippingFile.FullName)'."
        }
        & $spirvOpt --strip-debug $profileFile -o $profileStripped
        if ($LASTEXITCODE -ne 0) {
            throw "spirv-opt failed while stripping '$profileFile'."
        }

        $shippingSemanticHash = (Get-FileHash -LiteralPath $shippingStripped -Algorithm SHA256).Hash.ToLowerInvariant()
        $profileSemanticHash = (Get-FileHash -LiteralPath $profileStripped -Algorithm SHA256).Hash.ToLowerInvariant()
        [ordered]@{
            path = $relativePath.Replace('\', '/')
            shippingHash = (Get-FileHash -LiteralPath $shippingFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            profileHash = (Get-FileHash -LiteralPath $profileFile -Algorithm SHA256).Hash.ToLowerInvariant()
            shippingSemanticHash = $shippingSemanticHash
            profileSemanticHash = $profileSemanticHash
            semanticParity = $shippingSemanticHash -eq $profileSemanticHash
        }
    })

    $failed = @($entries | Where-Object { -not $_.semanticParity })
    $gitCommit = (& git rev-parse HEAD 2>$null)
    $gitCommitAvailable = $LASTEXITCODE -eq 0
    $gitDirty = [bool](& git status --porcelain 2>$null)
    $manifest = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('O')
        gitCommit = if ($gitCommitAvailable) { $gitCommit } else { $null }
        gitDirty = $gitDirty
        compilerExecutable = $compiler
        compilerVersion = ((& $compiler --version) -join "`n")
        spirvOptimizerExecutable = $spirvOpt
        shippingConfiguration = 'ShippingPerformance'
        profileConfiguration = 'ProfileSymbols'
        shaderCount = @($entries).Count
        semanticParity = $failed.Count -eq 0
        shaders = @($entries)
    }

    $manifestDirectory = Split-Path -Parent $manifestFullPath
    if ($manifestDirectory) {
        New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestFullPath -Encoding utf8NoBOM

    if ($failed.Count -ne 0) {
        throw "$($failed.Count) ProfileSymbols shader artifact(s) differ semantically from ShippingPerformance after debug stripping. See '$manifestFullPath'."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
