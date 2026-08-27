[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Reference', 'LowerMemoryRayQuery')]
    [string] $DeviceClass,

    [Parameter(Mandatory = $true)]
    [string] $ApprovedHdrManifest,

    [Parameter()]
    [string] $ArtifactRoot = 'artifacts/material-gi-release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $DotnetArguments
    )

    $gateVariable = 'NJULF_STARTUP_LATENCY_GATE'
    $hadGateOverride = Test-Path -LiteralPath "Env:$gateVariable"
    $previousGateOverride = [Environment]::GetEnvironmentVariable(
        $gateVariable,
        [EnvironmentVariableTarget]::Process)
    $runsRendererHost = $DotnetArguments -contains `
        'NjulfHelloGame/NjulfHelloGame.csproj'
    $exitCode = -1
    try {
        if ($runsRendererHost) {
            [Environment]::SetEnvironmentVariable(
                $gateVariable,
                'enforce',
                [EnvironmentVariableTarget]::Process)
        }
        & dotnet @DotnetArguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($hadGateOverride) {
            [Environment]::SetEnvironmentVariable(
                $gateVariable,
                $previousGateOverride,
                [EnvironmentVariableTarget]::Process)
        }
        else {
            Remove-Item -LiteralPath "Env:$gateVariable" `
                -ErrorAction SilentlyContinue
        }
    }
    if ($exitCode -ne 0) {
        throw "dotnet exited with code $exitCode while running: dotnet $($DotnetArguments -join ' ')"
    }
}

function Get-CanonicalWorkspacePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Role
    )

    $workspace = [IO.Path]::GetFullPath((Get-Location).Path)
    $candidate = if ([IO.Path]::IsPathFullyQualified($Path)) {
        [IO.Path]::GetFullPath($Path)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $workspace $Path))
    }
    $boundary = $workspace.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith(
            $boundary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must resolve inside the checked-out workspace."
    }
    return $candidate
}

function Get-Sha256Text {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Text
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    try {
        return [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

$approvedManifestPath = Get-CanonicalWorkspacePath `
    -Path $ApprovedHdrManifest `
    -Role 'Approved HDR manifest'
if (-not (Test-Path -LiteralPath $approvedManifestPath -PathType Leaf)) {
    throw "Approved HDR manifest '$approvedManifestPath' does not exist. Qualification evidence cannot substitute an unreviewed baseline."
}

$artifactDirectory = Get-CanonicalWorkspacePath `
    -Path $ArtifactRoot `
    -Role 'Artifact root'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$testResultDirectory = Join-Path $artifactDirectory 'test-results'
New-Item -ItemType Directory -Path $testResultDirectory -Force | Out-Null

Get-ChildItem Env: |
    Where-Object {
        $_.Name -like 'NJULF_RENDERER_*' -or
        $_.Name -like 'NJULF_MATERIAL_GI_*' -or
        $_.Name -like 'NJULF_SPONZA_*'
    } |
    ForEach-Object {
        Remove-Item -LiteralPath "Env:$($_.Name)"
    }
Remove-Item Env:VK_INSTANCE_LAYERS -ErrorAction SilentlyContinue
$emptyLayerDirectory = Join-Path $artifactDirectory 'empty-vulkan-implicit-layers'
New-Item -ItemType Directory -Path $emptyLayerDirectory -Force | Out-Null
$env:VK_LOADER_LAYERS_DISABLE = '~implicit~'
$env:VK_IMPLICIT_LAYER_PATH = $emptyLayerDirectory

$commit = (& git rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or
    $commit.Length -ne 40 -or
    $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'A canonical checked-out Git commit is required for release evidence.'
}

Invoke-Dotnet @(
    'restore',
    'Njulf.sln',
    '--locked-mode'
)
Invoke-Dotnet @(
    'build',
    'Njulf.sln',
    '--configuration',
    'Release',
    '--no-restore'
)

$cpuOracleTrx = Join-Path $testResultDirectory 'cpu-oracle.trx'
$gpuOracleTrx = Join-Path $testResultDirectory 'gpu-oracle.trx'
$releaseTestsTrx = Join-Path $testResultDirectory 'release-tests.trx'
Invoke-Dotnet @(
    'test',
    'Njulf.Tests/Njulf.Tests.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--filter',
    'FullyQualifiedName~Njulf.Tests.MaterialTransportV2Tests',
    '--logger',
    "trx;LogFileName=$([IO.Path]::GetFileName($cpuOracleTrx))",
    '--results-directory',
    $testResultDirectory
)
Invoke-Dotnet @(
    'test',
    'Njulf.Tests/Njulf.Tests.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--filter',
    'FullyQualifiedName=Njulf.Tests.GiMaterialGpuConformanceTests.WindowlessVulkanShader_MatchesCpuOracleAndRoundTripsExactAbi',
    '--logger',
    "trx;LogFileName=$([IO.Path]::GetFileName($gpuOracleTrx))",
    '--results-directory',
    $testResultDirectory
)
Invoke-Dotnet @(
    'test',
    'Njulf.Tests/Njulf.Tests.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--logger',
    "trx;LogFileName=$([IO.Path]::GetFileName($releaseTestsTrx))",
    '--results-directory',
    $testResultDirectory
)

$khronosCache = Join-Path $artifactDirectory 'khronos-cache'
$khronosCooked = Join-Path $artifactDirectory 'khronos-cooked'
$khronosSemanticReport = Join-Path $artifactDirectory 'khronos-semantic.json'
Invoke-Dotnet @(
    'run',
    '--project',
    'Njulf.AssetTool/Njulf.AssetTool.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    'khronos-material-gi',
    '--cache',
    $khronosCache,
    '--out',
    $khronosCooked,
    '--report',
    $khronosSemanticReport
)

$khronosManifest = [IO.Path]::GetFullPath(
    'Njulf.AssetTool/khronos-material-gi-assets.json')
$khronosRenderedCapture = Join-Path $artifactDirectory 'khronos-rendered.pfm'
$khronosRenderedReport = Join-Path $artifactDirectory 'khronos-rendered.json'
Invoke-Dotnet @(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--khronos-material-gi-render-manifest',
    $khronosManifest,
    '--khronos-material-gi-gate-report',
    $khronosSemanticReport,
    '--khronos-material-gi-cooked-root',
    (Join-Path $khronosCooked 'win-x64'),
    '--khronos-material-gi-render-capture',
    $khronosRenderedCapture,
    '--khronos-material-gi-render-report',
    $khronosRenderedReport,
    '--validation',
    'standard'
)

$alphaReport = Join-Path $artifactDirectory 'alpha-visibility.json'
$alphaEvidence = Join-Path $artifactDirectory 'alpha-visibility.bin'
Invoke-Dotnet @(
    'run',
    '--project',
    'Njulf.AssetTool/Njulf.AssetTool.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    'alpha-visibility-gate',
    '--report',
    $alphaReport,
    '--evidence',
    $alphaEvidence
)

$commonSmokeArguments = @(
    '--scene',
    'global-illumination-test',
    '--quality-preset',
    'ddgi-high',
    '--gpu-timing',
    '--validation',
    'standard',
    '--fail-on-validation-message'
)
Invoke-Dotnet -DotnetArguments (@(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--smoke-mode',
    'all',
    '--smoke-frames',
    '8',
    '--health-report',
    (Join-Path $artifactDirectory 'health-lifecycle.json'),
    '--startup-log',
    (Join-Path $artifactDirectory 'startup-lifecycle.jsonl')
) + $commonSmokeArguments)
Invoke-Dotnet -DotnetArguments (@(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--smoke-mode',
    'quality-switch',
    '--health-report',
    (Join-Path $artifactDirectory 'health-quality-switch.json'),
    '--startup-log',
    (Join-Path $artifactDirectory 'startup-quality-switch.jsonl')
) + $commonSmokeArguments)
Invoke-Dotnet -DotnetArguments (@(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--smoke-mode',
    'texture-hot-reload',
    '--health-report',
    (Join-Path $artifactDirectory 'health-texture-hot-reload.json'),
    '--startup-log',
    (Join-Path $artifactDirectory 'startup-texture-hot-reload.jsonl')
) + $commonSmokeArguments)

$benchmarkReports = @{}
foreach ($tier in @('low', 'medium', 'high', 'ultra')) {
    $benchmarkReport = Join-Path $artifactDirectory "benchmark-$tier.json"
    $benchmarkReports[$tier] = $benchmarkReport
    Invoke-Dotnet @(
        'run',
        '--project',
        'NjulfHelloGame/NjulfHelloGame.csproj',
        '--configuration',
        'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--benchmark',
        '--benchmark-report',
        $benchmarkReport,
        '--benchmark-warmup-frames',
        '30',
        '--benchmark-measure-frames',
        '120',
        '--benchmark-budget-profile',
        $tier,
        '--material-gi-qualification-candidate',
        '--performance-scenario',
        'gi-simple-ddgi-furnace',
        '--quality-preset',
        'ddgi-high',
        '--gpu-timing',
        '--validation',
        'standard',
        '--fail-on-validation-message',
        '--health-report',
        (Join-Path $artifactDirectory "health-benchmark-$tier.json"),
        '--startup-log',
        (Join-Path $artifactDirectory "startup-benchmark-$tier.jsonl")
    )
}

$graphicsCaptureDirectory = Join-Path $artifactDirectory 'capture-graphics'
$asyncCaptureDirectory = Join-Path $artifactDirectory 'capture-async'
Invoke-Dotnet @(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--material-gi-capture-dir',
    $graphicsCaptureDirectory,
    '--async-compute-mode',
    'disabled',
    '--validation',
    'standard',
    '--fail-on-validation-message'
)
Invoke-Dotnet @(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--material-gi-capture-dir',
    $asyncCaptureDirectory,
    '--async-compute-mode',
    'forced',
    '--validation',
    'standard',
    '--fail-on-validation-message'
)
Invoke-Dotnet @(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--compare-material-gi-captures',
    $graphicsCaptureDirectory,
    $asyncCaptureDirectory,
    '--material-gi-comparison-report',
    (Join-Path $artifactDirectory 'graphics-async-comparison.json')
)
Invoke-Dotnet @(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--compare-material-gi-approved-hdr',
    $approvedManifestPath,
    $graphicsCaptureDirectory,
    '--material-gi-approved-hdr-report',
    (Join-Path $artifactDirectory 'approved-hdr-regression.json')
)

$benchmarkIdentity = (
    Get-Content -LiteralPath $benchmarkReports['high'] -Raw |
        ConvertFrom-Json -Depth 64
).producerIdentity
if ($null -eq $benchmarkIdentity) {
    throw 'High-tier benchmark did not contain a producer identity.'
}
$testMatrixContract = @(
    'material-gi-test-matrix-command/v1',
    'configuration=Release',
    'cpuOracle=Njulf.Tests.MaterialTransportV2Tests',
    'gpuOracle=Njulf.Tests.GiMaterialGpuConformanceTests.WindowlessVulkanShader_MatchesCpuOracleAndRoundTripsExactAbi',
    'releaseTests=Njulf.Tests',
    "commit=$commit"
) -join "`n"
$testMatrixSettingsFingerprint = Get-Sha256Text $testMatrixContract
$testMatrixDeviceId = "${DeviceClass}::$($benchmarkIdentity.gpuName)"
Invoke-Dotnet @(
    'run',
    '--project',
    'Njulf.AssetTool/Njulf.AssetTool.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    'material-gi-test-matrix',
    '--out',
    (Join-Path $artifactDirectory 'test-matrix.json'),
    '--build-commit',
    [string] $benchmarkIdentity.buildCommit,
    '--shader-fingerprint',
    [string] $benchmarkIdentity.shaderFingerprint,
    '--settings-fingerprint',
    $testMatrixSettingsFingerprint,
    '--device-id',
    $testMatrixDeviceId,
    '--gpu-name',
    [string] $benchmarkIdentity.gpuName,
    '--driver-version',
    [string] $benchmarkIdentity.driverVersion,
    '--attest-release-build',
    '--trx',
    "CpuOracle=$cpuOracleTrx",
    '--trx',
    "GpuOracle=$gpuOracleTrx",
    '--trx',
    "ReleaseTests=$releaseTestsTrx"
)

Invoke-Dotnet -DotnetArguments (@(
    'run',
    '--project',
    'NjulfHelloGame/NjulfHelloGame.csproj',
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--',
    '--smoke-mode',
    'long-run',
    '--long-run-minutes',
    '30',
    '--long-run-report',
    (Join-Path $artifactDirectory 'long-run-30m.json'),
    '--long-run-warmup-frames',
    '360',
    '--long-run-sample-interval',
    '30',
    '--long-run-max-samples',
    '512',
    '--long-run-memory-growth-tolerance-bytes',
    '1048576',
    '--health-report',
    (Join-Path $artifactDirectory 'health-long-run-30m.json'),
    '--startup-log',
    (Join-Path $artifactDirectory 'startup-long-run-30m.jsonl')
) + $commonSmokeArguments)

Write-Host "Material-GI Release gates passed for device class '$DeviceClass'."
