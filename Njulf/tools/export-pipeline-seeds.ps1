[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [string] $PipelineCachePath,

    [Parameter()]
    [string] $DestinationRoot =
        (Join-Path $PSScriptRoot '..\NjulfHelloGame'),

    [Parameter()]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PipelineBinaryGlobalKeyDirectory,

    [Parameter()]
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$resolvedDestination = [IO.Path]::GetFullPath($DestinationRoot)
if ([string]::IsNullOrWhiteSpace($PipelineCachePath) -and
    [string]::IsNullOrWhiteSpace($PipelineBinaryGlobalKeyDirectory)) {
    throw 'Specify -PipelineCachePath, -PipelineBinaryGlobalKeyDirectory, or both.'
}

$resolvedCache = $null
$cacheName = $null
$cacheLength = $null
$cacheDestination = $null
if (-not [string]::IsNullOrWhiteSpace($PipelineCachePath)) {
    if (-not (Test-Path -LiteralPath $PipelineCachePath -PathType Leaf)) {
        throw "Pipeline cache '$PipelineCachePath' does not exist."
    }
    $resolvedCache = (Resolve-Path -LiteralPath $PipelineCachePath).Path
    $cacheName = [IO.Path]::GetFileName($resolvedCache)
    if ($cacheName -notmatch '^gi-[0-9a-fA-F]{8}-[0-9a-fA-F]{8}\.njvkcache$') {
        throw "Pipeline cache '$resolvedCache' does not use the qualified Njulf device-cache name."
    }

    $cacheLength = (Get-Item -LiteralPath $resolvedCache).Length
    if ($cacheLength -le 0 -or $cacheLength -gt 536870912) {
        throw "Pipeline cache length $cacheLength is outside the admitted 1..512 MiB range."
    }

    $cacheDestinationDirectory = Join-Path $resolvedDestination 'PipelineCacheSeeds'
    $cacheDestination = Join-Path $cacheDestinationDirectory $cacheName
    if ((Test-Path -LiteralPath $cacheDestination) -and -not $Force) {
        throw "Seed '$cacheDestination' already exists. Pass -Force to replace it."
    }

    if ($PSCmdlet.ShouldProcess($cacheDestination, 'Export qualified Vulkan pipeline cache seed')) {
        New-Item -ItemType Directory -Path $cacheDestinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $resolvedCache -Destination $cacheDestination -Force:$Force
    }
}

$binaryDestination = $null
if ($PipelineBinaryGlobalKeyDirectory) {
    $resolvedBinary = (Resolve-Path -LiteralPath $PipelineBinaryGlobalKeyDirectory).Path
    $globalKey = [IO.Path]::GetFileName($resolvedBinary.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar))
    if ($globalKey -notmatch '^[0-9a-fA-F]{16,64}$') {
        throw "Pipeline binary directory '$resolvedBinary' is not named by a Vulkan global key."
    }
    $manifest = Join-Path $resolvedBinary 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Pipeline binary directory '$resolvedBinary' has no manifest.json."
    }
    $blobs = Join-Path $resolvedBinary 'blobs'
    if (-not (Test-Path -LiteralPath $blobs -PathType Container)) {
        throw "Pipeline binary directory '$resolvedBinary' has no blobs directory."
    }

    $binarySeedRoot = Join-Path $resolvedDestination 'PipelineBinarySeeds\v1'
    $binaryDestination = Join-Path $binarySeedRoot $globalKey
    if ((Test-Path -LiteralPath $binaryDestination) -and -not $Force) {
        throw "Seed '$binaryDestination' already exists. Pass -Force to replace it."
    }
    if ($PSCmdlet.ShouldProcess($binaryDestination, 'Export qualified Vulkan pipeline-binary seed')) {
        New-Item -ItemType Directory -Path $binarySeedRoot -Force | Out-Null
        if (Test-Path -LiteralPath $binaryDestination) {
            # The destination is rooted below PipelineBinarySeeds/v1 and the
            # final component was validated as a hex global key above.
            Remove-Item -LiteralPath $binaryDestination -Recurse -Force
        }
        New-Item -ItemType Directory -Path $binaryDestination -Force | Out-Null
        Copy-Item -LiteralPath $manifest -Destination $binaryDestination
        Copy-Item -LiteralPath $blobs -Destination $binaryDestination -Recurse
    }
}

$cacheHash = if ($resolvedCache) {
    (Get-FileHash -LiteralPath $resolvedCache -Algorithm SHA256).Hash
} else {
    $null
}
$receipt = [ordered]@{
    schemaVersion = 1
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourcePipelineCache = $resolvedCache
    pipelineCacheFile = if ($cacheName) { "PipelineCacheSeeds/$cacheName" } else { $null }
    pipelineCacheSha256 = $cacheHash
    pipelineCacheBytes = $cacheLength
    pipelineBinarySeed = if ($binaryDestination) {
        $binaryDestination.Substring($resolvedDestination.Length).TrimStart('\', '/').Replace('\', '/')
    } else {
        $null
    }
    qualificationRequired = $true
}
$receiptPath = Join-Path $resolvedDestination 'pipeline-seed-receipt.json'
if ($PSCmdlet.ShouldProcess($receiptPath, 'Write pipeline seed export receipt')) {
    $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receiptPath -Encoding utf8
}

if ($cacheDestination) {
    Write-Output "Pipeline cache seed: $cacheDestination"
}
if ($binaryDestination) {
    Write-Output "Pipeline binary seed: $binaryDestination"
}
Write-Output "Receipt: $receiptPath"
