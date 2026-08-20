[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [string]$CandidateId = "",
    [string]$CandidateEnvelopePath = "",
    [switch]$PrepareCandidateEnvelope,
    [string]$DiscoveryArtifactPath = "",
    [string]$AutomaticCandidateId = "",
    [string]$AutomaticCandidateSourceCommit = "",
    [string]$AutomaticCandidateFocusedTestFilter = "",
    [string]$CandidateEnvelopeOutputPath = "",
    [string]$RunDirectory = ".perf-loop-runs/campaign",
    [string]$CookedAssetRoot = "",
    [string]$TargetHypothesisId = "",
    [switch]$InitializeReferences,
    [switch]$InitializeReferencesOnly,
    [switch]$BaselineOnly,
    [switch]$DiscoverHotspots,
    [switch]$ValidateOnly,
    [switch]$FinalizeRetainedStack,
    [bool]$RollbackRejected = $true,
    [bool]$KeepRejectedCommits = $true
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
$Iterations = 1

$script:SolutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$providedManifestFile = if ([System.IO.Path]::IsPathRooted($ManifestPath)) {
    [System.IO.Path]::GetFullPath($ManifestPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $ManifestPath))
}
$script:ManifestFile = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "perf-campaign.bistro-sponza.json"))
if (-not [string]::Equals(
        $providedManifestFile,
        $script:ManifestFile,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Production campaign mode accepts only the built-in pinned manifest '$script:ManifestFile'."
}
$script:RunRoot = if ([System.IO.Path]::IsPathRooted($RunDirectory)) {
    [System.IO.Path]::GetFullPath($RunDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $RunDirectory))
}
$script:RepoRoot = ""
$script:CampaignBranch = ""
$script:ProtectedFingerprints = [ordered]@{}
$script:CampaignLockPath = ""
$script:CampaignLockSha256 = ""
$script:CampaignManifestSha256 = ""
$script:CookedAssetBundle = $null

function Get-PropertyValue {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) {
        return $Default
    }
    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) { return $Default }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Get-ItemCount {
    param($Value)
    if ($null -eq $Value) { return 0 }
    return @($Value).Count
}

function Assert-ExactPropertyNames {
    param($Object, [string[]]$ExpectedNames, [string]$Label)
    if ($null -eq $Object) {
        throw "$Label is missing."
    }
    $actualNames = if ($Object -is [System.Collections.IDictionary]) {
        @($Object.Keys | ForEach-Object { [string]$_ })
    } else {
        @($Object.PSObject.Properties |
            ForEach-Object { [string]$_.Name })
    }
    if (($actualNames -join "`n") -cne ($ExpectedNames -join "`n")) {
        throw "$Label property topology differs. Expected [$($ExpectedNames -join ', ')], got [$($actualNames -join ', ')]."
    }
}

function Assert-Text {
    param([string]$Value, [string]$Role)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Role is required."
    }
}

function Resolve-SolutionPath {
    param([string]$Path)
    Assert-Text $Path "Path"
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath(
        (Join-Path $script:SolutionRoot $Path))
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Bytes {
    param([byte[]]$Bytes)
    if ($null -eq $Bytes) { throw "Cannot hash null bytes." }
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-Sha256Text {
    param([string]$Text)
    return Get-Sha256Bytes (
        [System.Text.UTF8Encoding]::new($false).GetBytes($Text))
}

function Read-BoundedFileBytes {
    param(
        [string]$Path,
        [long]$MaximumLength,
        [string]$Label)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $stream = [System.IO.File]::Open(
        $fullPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $MaximumLength) {
            throw "$Label byte length $($stream.Length) is outside the admitted range."
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw "$Label ended before its admitted length." }
            $offset += $read
        }
        return $bytes
    } finally {
        $stream.Dispose()
    }
}

function Assert-JsonObject {
    param($Value, [string]$Label)
    if ($null -eq $Value -or
        $Value -is [string] -or
        $Value -is [System.Array] -or
        $Value -is [ValueType]) {
        throw "$Label must be a JSON object."
    }
    return $Value
}

function Assert-JsonString {
    param($Value, [string]$Label)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label must be a non-empty JSON string."
    }
    return [string]$Value
}

function Assert-JsonBoolean {
    param($Value, [string]$Label)
    if ($Value -isnot [bool]) { throw "$Label must be a JSON boolean." }
    return [bool]$Value
}

function Assert-JsonInteger {
    param($Value, [string]$Label, [long]$Minimum = [long]::MinValue)
    if ($Value -is [bool] -or $Value -isnot [ValueType] -or
        $Value -is [single] -or $Value -is [double] -or
        $Value -is [decimal]) {
        throw "$Label must be a JSON integer."
    }
    try { $integer = [long]$Value } catch { throw "$Label must be a JSON integer." }
    if ($integer -lt $Minimum) { throw "$Label must be at least $Minimum." }
    return $integer
}

function Assert-JsonArray {
    param($Value, [string]$Label)
    if ($null -eq $Value -or $Value -isnot [System.Array]) {
        throw "$Label must be a JSON array."
    }
    return $Value
}

function Assert-NoLinkedPathComponents {
    param([string]$Path, [string]$Label)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    $relative = $fullPath.Substring($root.Length)
    $current = $root
    foreach ($component in @($relative -split '[\\/]' | Where-Object {
            -not [string]::IsNullOrEmpty($_) })) {
        $current = Join-Path $current $component
        if (-not (Test-Path -LiteralPath $current)) { break }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse/link component: $current"
        }
    }
    return $fullPath
}

function Get-RuntimeExecutableBundleHash {
    param([string]$ExecutablePath)
    $fullExecutable = [System.IO.Path]::GetFullPath($ExecutablePath)
    $directory = Split-Path -Parent $fullExecutable
    [System.IO.FileInfo[]]$files = @(
        Get-Item -LiteralPath $fullExecutable
        Get-ChildItem -LiteralPath $directory -File -Filter "Njulf*.dll"
    )
    $fileNameComparer =
        [System.Collections.Generic.Comparer[System.IO.FileInfo]]::Create(
            [System.Comparison[System.IO.FileInfo]] {
                param($left, $right)
                return [StringComparer]::OrdinalIgnoreCase.Compare(
                    $left.Name,
                    $right.Name)
            })
    [Array]::Sort($files, $fileNameComparer)
    $builder = [System.Text.StringBuilder]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        $fullPath = [System.IO.Path]::GetFullPath($file.FullName)
        if (-not $seen.Add($fullPath)) { continue }
        [void]$builder.Append($file.Name)
        [void]$builder.Append(":sha256:")
        [void]$builder.Append((Get-Sha256 $fullPath))
        [void]$builder.Append("`n")
    }
    if ($builder.Length -eq 0) {
        throw "Executable bundle '$directory' contains no admitted files."
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    return "sha256:" + [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-LinearHdrPfm {
    param(
        [string]$Path,
        [int]$ExpectedWidth,
        [int]$ExpectedHeight,
        [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label PFM is missing: $Path"
    }
    $stream = [System.IO.File]::OpenRead($Path)
    $length = $stream.Length
    try {
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.Encoding]::ASCII,
            $false,
            1024,
            $true)
        try {
            $magic = $reader.ReadLine()
            $contract = $reader.ReadLine()
            $dimensionText = $reader.ReadLine()
            $dimensions = @(([string]$dimensionText) -split '\s+')
            $scaleText = $reader.ReadLine()
        } finally {
            $reader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
    $width = 0
    $height = 0
    $expectedContract =
        "# NJULF_LINEAR_FLOAT_IMAGE_VERSION=1 COLOR_SPACE=linear-scRGB LOGICAL_ORIGIN=top-left"
    $expectedHeader =
        "PF`n$expectedContract`n$ExpectedWidth $ExpectedHeight`n-1.0`n"
    $expectedLength =
        [System.Text.Encoding]::ASCII.GetByteCount($expectedHeader) +
        ([long]$ExpectedWidth * [long]$ExpectedHeight * 3L * 4L)
    if ($magic -ne "PF" -or
        $contract -ne $expectedContract -or
        $dimensions.Count -ne 2 -or
        -not [int]::TryParse([string]$dimensions[0], [ref]$width) -or
        -not [int]::TryParse([string]$dimensions[1], [ref]$height) -or
        $width -ne $ExpectedWidth -or
        $height -ne $ExpectedHeight -or
        $scaleText -ne "-1.0" -or
        $length -ne $expectedLength) {
        throw "$Label is not a canonical $($ExpectedWidth)x$ExpectedHeight RGB float PFM."
    }
}

function Test-Sha256Identity {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -match '^sha256:[0-9a-f]{64}$'
}

function Get-ExpectedQualityTier {
    param([string]$BudgetProfile)
    switch ($BudgetProfile.ToLowerInvariant()) {
        "low" { return "Low" }
        "medium" { return "Medium" }
        "high" { return "High" }
        "ultra" { return "Ultra" }
        "stress" { return "StressUnlimited" }
        default { throw "Unsupported budget profile '$BudgetProfile'." }
    }
}

function Test-CanonicalIdentityText {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -eq $Value.Trim() -and
        $Value -notmatch '^(unknown|unavailable:)'
}

function Get-CanonicalPathFingerprint {
    param([string]$Path)
    $fullPath = Assert-NoLinkedPathComponents `
        (Resolve-SolutionPath $Path) "Fingerprint path '$Path'"
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        return "file:sha256:" + (Get-Sha256 $fullPath)
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        return "absent"
    }
    $builder = [System.Text.StringBuilder]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force |
            Sort-Object FullName)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Fingerprint path '$Path' contains reparse/link entry '$($item.FullName)'."
        }
        $relative = $item.FullName.Substring(
            $fullPath.Length + 1).Replace("\", "/")
        if ($item.PSIsContainer) {
            [void]$builder.Append("directory  ").Append(
                $relative).Append("`n")
        } else {
            [void]$builder.Append("file:sha256:").Append(
                (Get-Sha256 $item.FullName)).Append(
                "  ").Append($relative).Append("`n")
        }
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    return "directory:sha256:" + [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-ProtectedFingerprints {
    param($Manifest)
    $fingerprints = [ordered]@{}
    foreach ($path in @($Manifest.protectedPaths)) {
        $fingerprints[[string]$path] = Get-CanonicalPathFingerprint ([string]$path)
    }
    return $fingerprints
}

function Assert-CampaignManifestIntegrity {
    if ([string]::IsNullOrWhiteSpace($script:CampaignManifestSha256)) {
        return
    }
    $null = Assert-NoLinkedPathComponents `
        $script:ManifestFile "Active campaign manifest"
    if (-not (Test-Path -LiteralPath $script:ManifestFile -PathType Leaf) -or
        (Get-Sha256 $script:ManifestFile) -cne
            $script:CampaignManifestSha256) {
        throw "Active campaign manifest changed after admission: $script:ManifestFile"
    }
}

function Get-AdmittedCampaignManifestSha256 {
    Assert-CampaignManifestIntegrity
    return $script:CampaignManifestSha256
}

function Get-BuildBundleFingerprint {
    param([string]$Path)
    $fullPath = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($Path)) "Build bundle '$Path'"
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Build bundle is missing: $fullPath"
    }
    $builder = [System.Text.StringBuilder]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force |
            Sort-Object FullName)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Build bundle contains reparse/link entry '$($item.FullName)'."
        }
        $relative = $item.FullName.Substring(
            $fullPath.Length + 1).Replace("\", "/")
        if ($relative -ceq "Cooked" -or
            $relative.StartsWith("Cooked/", [StringComparison]::Ordinal)) {
            continue
        }
        if ($item.PSIsContainer) {
            [void]$builder.Append("directory  ").Append(
                $relative).Append("`n")
        } else {
            [void]$builder.Append("file:sha256:").Append(
                (Get-Sha256 $item.FullName)).Append(
                "  ").Append($relative).Append("`n")
        }
    }
    return "directory:sha256:" + (Get-Sha256Text $builder.ToString())
}

function Get-CookedAssetInventoryValue {
    param($Bundle)
    return [ordered]@{
        schema = "njulf-perf-cooked-asset-inventory/v1"
        identityHash = [string]$Bundle.Identity.identityHash
        platform = [string]$Bundle.Identity.platform
        fileCount = [int]$Bundle.Identity.fileCount
        totalBytes = [long]$Bundle.Identity.totalBytes
        reports = @($Bundle.Identity.reports)
        files = @($Bundle.Files | ForEach-Object {
            [ordered]@{
                relativePath = [string]$_.relativePath
                length = [long]$_.length
                cookHash = [string]$_.cookHash
                sha256 = [string]$_.sha256
            }
        })
    }
}

function Resolve-CookedAssetBundle {
    param($Manifest, [string]$SourceRoot, [string]$Label)
    Assert-Text $SourceRoot "$Label cooked asset root"
    $fullSourceRoot = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($SourceRoot)) `
        "$Label cooked asset root"
    if (-not (Test-Path -LiteralPath $fullSourceRoot -PathType Container)) {
        throw "$Label cooked asset root is missing: $fullSourceRoot"
    }
    if ((Test-PathContainedBy $fullSourceRoot $script:RepoRoot) -or
        (Test-PathContainedBy $fullSourceRoot $script:RunRoot)) {
        throw "$Label cooked asset root must be an external immutable input, not campaign source or output."
    }
    $platform = [string]$Manifest.cookedAssets.platform
    $platformRoot = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath((Join-Path $fullSourceRoot $platform))) `
        "$Label cooked asset platform root"
    if (-not (Test-PathContainedBy $platformRoot $fullSourceRoot) -or
        -not (Test-Path -LiteralPath $platformRoot -PathType Container)) {
        throw "$Label cooked asset platform root is missing or escaped: $platformRoot"
    }
    $fileMap = [System.Collections.Generic.Dictionary[string,object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $reportIdentities = [System.Collections.Generic.List[object]]::new()
    foreach ($model in @($Manifest.cookedAssets.requiredModels)) {
        $modelName = [string]$model
        $reportRelative = "reports/$modelName.cook-report.json"
        $reportPath = Assert-NoLinkedPathComponents `
            ([System.IO.Path]::GetFullPath((Join-Path $platformRoot (
                $reportRelative.Replace("/", "\"))))) `
            "$Label cook report '$modelName'"
        if (-not (Test-PathContainedBy $reportPath $platformRoot) -or
            -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "$Label cook report is missing: $reportPath"
        }
        $reportRead = Read-StrictJsonFile `
            $reportPath 16777216 "$Label cook report '$modelName'"
        $report = $reportRead.Value
        $null = Assert-JsonObject $report "$Label cook report '$modelName'"
        if ([string]$report.status -cne "Succeeded") {
            throw "$Label cook report '$modelName' is not successful."
        }
        $assetId = [Guid]::Empty
        if (-not [Guid]::TryParse([string]$report.assetId, [ref]$assetId) -or
            $assetId -eq [Guid]::Empty) {
            throw "$Label cook report '$modelName' has no canonical asset identity."
        }
        $null = Assert-JsonObject `
            $report.outputs "$Label cook report '$modelName' outputs"
        $outputProperties = @($report.outputs.PSObject.Properties |
            Sort-Object Name)
        if ($outputProperties.Count -eq 0) {
            throw "$Label cook report '$modelName' has no outputs."
        }
        $packageRelative = "models/$modelName.njmodel"
        if ($null -eq $report.outputs.PSObject.Properties[$packageRelative]) {
            throw "$Label cook report '$modelName' omits '$packageRelative'."
        }
        foreach ($property in $outputProperties) {
            $relative = [string]$property.Name
            $segments = @($relative -split '/')
            if ([string]::IsNullOrWhiteSpace($relative) -or
                [System.IO.Path]::IsPathRooted($relative) -or
                $relative.Contains("\", [StringComparison]::Ordinal) -or
                $segments.Count -lt 2 -or
                @($segments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or $_ -in @(".", "..")
                }).Count -ne 0) {
                throw "$Label cook output path is not canonical: '$relative'."
            }
            [UInt64]$cookHash = 0
            if (-not [UInt64]::TryParse(
                    [string]$property.Value,
                    [Globalization.NumberStyles]::Integer,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$cookHash) -or $cookHash -eq 0) {
                throw "$Label cook output '$relative' has no canonical content hash."
            }
            $sourcePath = Assert-NoLinkedPathComponents `
                ([System.IO.Path]::GetFullPath((Join-Path $platformRoot (
                    $relative.Replace("/", "\"))))) `
                "$Label cook output '$relative'"
            if (-not (Test-PathContainedBy $sourcePath $platformRoot) -or
                -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "$Label cook output is missing or escaped: $sourcePath"
            }
            $length = [long](Get-Item -LiteralPath $sourcePath).Length
            if ($length -le 0) {
                throw "$Label cook output '$relative' is empty."
            }
            $entry = [pscustomobject]@{
                relativePath = $relative
                sourcePath = $sourcePath
                length = $length
                cookHash = $cookHash.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)
                sha256 = ""
            }
            if ($fileMap.ContainsKey($relative)) {
                $existing = $fileMap[$relative]
                if ([string]$existing.cookHash -cne [string]$entry.cookHash -or
                    [long]$existing.length -ne $length -or
                    -not [string]::Equals(
                        [string]$existing.sourcePath,
                        $sourcePath,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw "$Label cook reports disagree on '$relative'."
                }
            } else {
                $fileMap.Add($relative, $entry)
            }
        }
        $reportLength = [long](Get-Item -LiteralPath $reportPath).Length
        $reportEntry = [pscustomobject]@{
            relativePath = $reportRelative
            sourcePath = $reportPath
            length = $reportLength
            cookHash = "unavailable"
            sha256 = [string]$reportRead.Sha256
        }
        if ($fileMap.ContainsKey($reportRelative)) {
            throw "$Label report path collides with a cooked output: '$reportRelative'."
        }
        $fileMap.Add($reportRelative, $reportEntry)
        $reportIdentities.Add([pscustomobject]@{
            model = $modelName
            relativePath = $reportRelative
            sha256 = [string]$reportRead.Sha256
        })
    }
    $files = @($fileMap.Values | Sort-Object relativePath)
    [long]$totalBytes = 0
    foreach ($file in $files) {
        $totalBytes += [long]$file.length
    }
    if ($files.Count -gt [int]$Manifest.cookedAssets.maximumFiles -or
        $totalBytes -gt [long]$Manifest.cookedAssets.maximumBytes) {
        throw "$Label cooked asset bundle exceeds its admitted file or byte bound."
    }
    $canonical = [System.Text.StringBuilder]::new()
    [void]$canonical.Append("njulf-perf-cooked-assets/v1`n").Append(
        $platform).Append("`n")
    foreach ($file in $files) {
        [void]$canonical.Append([string]$file.relativePath).Append("`0").Append(
            [long]$file.length).Append("`0").Append(
            [string]$file.cookHash).Append("`0").Append(
            [string]$file.sha256).Append("`n")
    }
    $identity = [pscustomobject][ordered]@{
        schema = "njulf-perf-cooked-assets/v1"
        platform = $platform
        sourceRoot = $fullSourceRoot
        identityHash = "sha256:" + (Get-Sha256Text $canonical.ToString())
        fileCount = $files.Count
        totalBytes = $totalBytes
        reports = @($reportIdentities)
    }
    return [pscustomobject]@{
        Identity = $identity
        Files = $files
    }
}

function Initialize-CampaignHardLinkInterop {
    if ($null -ne ("NjulfPerfCampaignNativeMethods" -as [type])) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NjulfPerfCampaignNativeMethods
{
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}
'@
}

function Install-CookedAssetBundle {
    param($Bundle, [string]$BuildRoot, [string]$Label)
    if ($null -eq $Bundle) { throw "$Label has no admitted cooked asset bundle." }
    $fullBuildRoot = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($BuildRoot)) "$Label build root"
    $targetBase = [System.IO.Path]::GetFullPath(
        (Join-Path $fullBuildRoot "Cooked"))
    if (-not (Test-PathContainedBy $targetBase $fullBuildRoot) -or
        (Test-Path -LiteralPath $targetBase)) {
        throw "$Label cooked target must be a fresh path: $targetBase"
    }
    $targetRoot = Join-Path $targetBase ([string]$Bundle.Identity.platform)
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    Initialize-CampaignHardLinkInterop
    foreach ($file in @($Bundle.Files)) {
        $target = [System.IO.Path]::GetFullPath((Join-Path $targetRoot (
            ([string]$file.relativePath).Replace("/", "\"))))
        if (-not (Test-PathContainedBy $target $targetRoot) -or
            (Test-Path -LiteralPath $target)) {
            throw "$Label cooked target is duplicated or escaped: $target"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) `
            -Force | Out-Null
        $linked = [NjulfPerfCampaignNativeMethods]::CreateHardLink(
            $target,
            [string]$file.sourcePath,
            [IntPtr]::Zero)
        if (-not $linked) {
            [System.IO.File]::Copy([string]$file.sourcePath, $target, $false)
            if ((Get-Sha256 ([string]$file.sourcePath)) -cne
                (Get-Sha256 $target)) {
                throw "$Label copied cooked output differs from its source: $target"
            }
        }
        if ([long](Get-Item -LiteralPath $target).Length -ne [long]$file.length) {
            throw "$Label staged cooked output has the wrong length: $target"
        }
        if (-not [string]::IsNullOrEmpty([string]$file.sha256) -and
            (Get-Sha256 $target) -cne [string]$file.sha256) {
            throw "$Label staged cook report differs from its admitted bytes: $target"
        }
    }
    $inventoryPath = Join-Path $fullBuildRoot "campaign-cooked-assets.json"
    Write-JsonArtifact $inventoryPath (Get-CookedAssetInventoryValue $Bundle)
    return [pscustomobject][ordered]@{
        Schema = "njulf-perf-cooked-asset-staging/v1"
        Platform = [string]$Bundle.Identity.platform
        IdentityHash = [string]$Bundle.Identity.identityHash
        FileCount = [int]$Bundle.Identity.fileCount
        TotalBytes = [long]$Bundle.Identity.totalBytes
        InventoryPath = [System.IO.Path]::GetFullPath($inventoryPath)
        InventorySha256 = Get-Sha256 $inventoryPath
    }
}

function Assert-CookedAssetStaging {
    param($Staging, [string]$BuildRoot, [string]$Label)
    if ($null -eq $script:CookedAssetBundle) {
        throw "$Label has no active cooked asset source admission."
    }
    Assert-ExactPropertyNames $Staging @(
        "Schema", "Platform", "IdentityHash", "FileCount", "TotalBytes",
        "InventoryPath", "InventorySha256") "$Label cooked staging"
    $bundle = $script:CookedAssetBundle
    $expectedInventory = Get-CookedAssetInventoryValue $bundle
    $fullBuildRoot = [System.IO.Path]::GetFullPath($BuildRoot)
    $inventoryPath = [System.IO.Path]::GetFullPath(
        (Join-Path $fullBuildRoot "campaign-cooked-assets.json"))
    if ([string]$Staging.Schema -cne "njulf-perf-cooked-asset-staging/v1" -or
        [string]$Staging.Platform -cne [string]$bundle.Identity.platform -or
        [string]$Staging.IdentityHash -cne [string]$bundle.Identity.identityHash -or
        [int]$Staging.FileCount -ne [int]$bundle.Identity.fileCount -or
        [long]$Staging.TotalBytes -ne [long]$bundle.Identity.totalBytes -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$Staging.InventoryPath),
            $inventoryPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Staging.InventorySha256 -cnotmatch '^[0-9a-f]{64}$' -or
        -not (Test-Path -LiteralPath $inventoryPath -PathType Leaf) -or
        (Get-Sha256 $inventoryPath) -cne [string]$Staging.InventorySha256) {
        throw "$Label cooked staging identity changed."
    }
    $inventory = (Read-StrictJsonFile `
        $inventoryPath 1048576 "$Label cooked inventory").Value
    if (($inventory | ConvertTo-Json -Depth 12 -Compress) -cne
        ($expectedInventory | ConvertTo-Json -Depth 12 -Compress)) {
        throw "$Label cooked inventory differs from the admitted source reports."
    }
    $targetRoot = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath((Join-Path $fullBuildRoot (
            "Cooked/" + [string]$bundle.Identity.platform)))) `
        "$Label cooked target"
    if (-not (Test-Path -LiteralPath $targetRoot -PathType Container)) {
        throw "$Label cooked target is missing: $targetRoot"
    }
    $expectedPaths = [System.Collections.Generic.Dictionary[string,object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($bundle.Files)) {
        $expectedPaths.Add([string]$file.relativePath, $file)
    }
    $actualItems = @(Get-ChildItem -LiteralPath $targetRoot -Recurse -Force)
    foreach ($item in $actualItems) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label cooked target contains a link/reparse entry: $($item.FullName)"
        }
    }
    $actualFiles = @($actualItems | Where-Object { -not $_.PSIsContainer })
    if ($actualFiles.Count -ne $expectedPaths.Count) {
        throw "$Label cooked target file count changed."
    }
    foreach ($item in $actualFiles) {
        $relative = [System.IO.Path]::GetRelativePath(
            $targetRoot, $item.FullName).Replace("\", "/")
        if (-not $expectedPaths.ContainsKey($relative) -or
            [long]$item.Length -ne [long]$expectedPaths[$relative].length) {
            throw "$Label cooked target contains an unexpected or changed file: $relative"
        }
        $entry = $expectedPaths[$relative]
        if (-not [string]::IsNullOrEmpty([string]$entry.sha256) -and
            (Get-Sha256 $item.FullName) -cne [string]$entry.sha256) {
            throw "$Label cooked report bytes changed: $relative"
        }
    }
}

function Initialize-CampaignManifestSnapshot {
    Assert-CampaignManifestIntegrity
    if ($null -eq $script:CampaignManifestBytes -or
        $script:CampaignManifestBytes.Length -eq 0) {
        throw "Campaign manifest bytes were not admitted before snapshot initialization."
    }
    $snapshotPath = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath(
            (Join-Path $script:RunRoot "campaign.manifest.snapshot.json"))) `
        "Campaign manifest snapshot"
    if (-not (Test-PathContainedBy $snapshotPath $script:RunRoot)) {
        throw "Campaign manifest snapshot must remain inside the admitted run root."
    }
    Write-AtomicByteArtifact `
        $snapshotPath ([byte[]]$script:CampaignManifestBytes) `
        "Campaign manifest snapshot"
    if ((Get-Sha256 $snapshotPath) -cne $script:CampaignManifestSha256) {
        throw "Campaign manifest snapshot differs from the admitted manifest bytes."
    }
    $script:CampaignManifestSnapshotPath = $snapshotPath
}

function Assert-ProtectedFingerprints {
    param($Expected)
    Assert-CampaignManifestIntegrity
    foreach ($entry in $Expected.GetEnumerator()) {
        $actual = Get-CanonicalPathFingerprint ([string]$entry.Key)
        if (-not [string]::Equals(
                $actual,
                [string]$entry.Value,
                [StringComparison]::Ordinal)) {
            throw "Protected path '$($entry.Key)' changed. Expected $($entry.Value), got $actual."
        }
    }
}

function Assert-CampaignLockIntegrity {
    Assert-CampaignManifestIntegrity
    if ([string]::IsNullOrWhiteSpace($script:CampaignLockSha256)) { return }
    if (-not (Test-Path -LiteralPath $script:CampaignLockPath -PathType Leaf) -or
        -not [string]::Equals(
            (Get-Sha256 $script:CampaignLockPath),
            $script:CampaignLockSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Campaign lock changed after it was admitted: $script:CampaignLockPath"
    }
}

function Assert-AdvisoryBeautyTarget {
    param($Manifest)
    $relativeManifest = [string](Get-PropertyValue $Manifest "advisoryBeautyTargetManifest" "")
    Assert-Text $relativeManifest "advisoryBeautyTargetManifest"
    $targetManifestPath = Resolve-SolutionPath $relativeManifest
    if (-not (Test-Path -LiteralPath $targetManifestPath -PathType Leaf)) {
        throw "Advisory beauty target manifest is missing: $targetManifestPath"
    }
    $target = Get-Content -LiteralPath $targetManifestPath -Raw |
        ConvertFrom-Json -DateKind String
    if ([string]$target.schema -ne "njulf-advisory-beauty-target/v1" -or
        [string]$target.role -ne "advisory-display-referred" -or
        [bool]$target.linearHdrGateEligible) {
        throw "The beauty target must be explicitly advisory and ineligible for linear-HDR gates."
    }
    if ([string]$target.detectedMediaType -ne "image/jpeg" -or
        [string]$target.containerSignature -ne "JFIF" -or
        [int]$target.width -ne 1920 -or
        [int]$target.height -ne 1085) {
        throw "The beauty target metadata no longer matches the locked 1920x1085 JFIF attachment."
    }
    $targetPath = Join-Path (Split-Path -Parent $targetManifestPath) ([string]$target.file)
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
        throw "Advisory beauty target is missing: $targetPath"
    }
    $hash = Get-Sha256 $targetPath
    $approvedHash =
        "fec2d7ffd48b7433cb59725f2046afbcf8058753c20fa929fbbfd3ee1643fc69"
    if (-not [string]::Equals($hash, [string]$target.sha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($hash, $approvedHash, [StringComparison]::Ordinal)) {
        throw "Advisory beauty target hash mismatch. Expected $($target.sha256), got $hash."
    }
    [byte[]]$signature = Get-Content -LiteralPath $targetPath -AsByteStream -ReadCount 11 -TotalCount 11
    if ($signature.Length -lt 11 -or
        $signature[0] -ne 0xff -or $signature[1] -ne 0xd8 -or
        [System.Text.Encoding]::ASCII.GetString($signature, 6, 4) -ne "JFIF") {
        throw "Advisory beauty target does not contain the locked JFIF signature."
    }
    return [pscustomobject]@{
        ManifestPath = $targetManifestPath
        ManifestSha256 = Get-Sha256 $targetManifestPath
        ImagePath = $targetPath
        ImageSha256 = $hash
        MediaType = [string]$target.detectedMediaType
        Width = [int]$target.width
        Height = [int]$target.height
        Role = [string]$target.role
    }
}

function Assert-CampaignManifest {
    param($Manifest)
    $null = Assert-JsonObject $Manifest "Campaign manifest"
    Assert-ExactPropertyNames $Manifest @(
        "schema", "campaignId", "projectPath",
        "iterationConfiguration", "finalConfigurations",
        "advisoryBeautyTargetManifest", "cookedAssets", "protectedPaths", "capture",
        "quality", "qualitySequence", "acceptance", "discoveryPolicy",
        "candidates", "targetHypotheses", "workloads") `
        "Campaign manifest"
    if ([string]$Manifest.schema -ne "njulf-perf-campaign/v2") {
        throw "Unsupported campaign schema '$($Manifest.schema)'."
    }
    Assert-Text ([string]$Manifest.campaignId) "campaignId"
    if ([string]$Manifest.campaignId -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
        throw "campaignId must contain only lowercase letters, digits, and hyphens."
    }
    Assert-Text ([string]$Manifest.projectPath) "projectPath"
    $project = Resolve-SolutionPath ([string]$Manifest.projectPath)
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Campaign project is missing: $project"
    }
    if ([string]$Manifest.iterationConfiguration -ne "Release") {
        throw "The per-hypothesis campaign configuration must be Release."
    }
    $null = Assert-JsonObject $Manifest.cookedAssets "cookedAssets"
    Assert-ExactPropertyNames $Manifest.cookedAssets @(
        "platform", "requiredModels", "maximumFiles", "maximumBytes") `
        "cookedAssets"
    if ((Assert-JsonString $Manifest.cookedAssets.platform `
            "cookedAssets.platform") -cne "win-x64") {
        throw "The campaign requires the win-x64 cooked asset package."
    }
    $null = Assert-JsonArray `
        $Manifest.cookedAssets.requiredModels "cookedAssets.requiredModels"
    $requiredCookedModels = @(
        $Manifest.cookedAssets.requiredModels | ForEach-Object {
            Assert-JsonString $_ "cookedAssets.requiredModels entry"
        })
    if (($requiredCookedModels -join "`n") -cne
        (@(
            "BistroExterior",
            "NewSponza_Main_glTF_003",
            "NewSponza_Curtains_glTF",
            "Strut") -join "`n")) {
        throw "The cooked asset model set differs from the exact Bistro/Sponza contract."
    }
    if ((Assert-JsonInteger $Manifest.cookedAssets.maximumFiles `
            "cookedAssets.maximumFiles" 1) -ne 1024 -or
        (Assert-JsonInteger $Manifest.cookedAssets.maximumBytes `
            "cookedAssets.maximumBytes" 1) -ne 3221225472) {
        throw "The cooked asset package bounds differ from the approved contract."
    }
    $finalConfigurations = @($Manifest.finalConfigurations)
    if ($finalConfigurations.Count -ne 2 -or
        @($finalConfigurations | Select-Object -Unique).Count -ne 2 -or
        [string]$finalConfigurations[0] -ne "Release" -or
        [string]$finalConfigurations[1] -ne "ShippingPerformance") {
        throw "Final timing must contain exactly Release and ShippingPerformance."
    }
    if ([int]$Manifest.capture.abbaCycles -ne 3) {
        throw "capture.abbaCycles must be exactly three."
    }
    if ([int]$Manifest.capture.benchmarkTimeoutSeconds -lt 1 -or
        [int]$Manifest.capture.benchmarkTimeoutSeconds -gt 86400) {
        throw "capture.benchmarkTimeoutSeconds must be between 1 and 86400."
    }
    if ([int]$Manifest.capture.trialTimeoutSeconds -lt 1 -or
        [int]$Manifest.capture.trialTimeoutSeconds -gt 86400) {
        throw "capture.trialTimeoutSeconds must be between 1 and 86400."
    }
    if ([int]$Manifest.capture.maximumSettlingFrames -lt 4096) {
        throw "Production timing requires at least 4096 additional settling frames."
    }
    $maximumRelativeRmse = Assert-FiniteNumber `
        $Manifest.quality.maximumRelativeRmse "quality.maximumRelativeRmse"
    $maximumFlipP95 = Assert-FiniteNumber `
        $Manifest.quality.maximumFlipP95 "quality.maximumFlipP95"
    $maximumRoiMeanLuminanceShift = Assert-FiniteNumber `
        $Manifest.quality.maximumRoiMeanLuminanceShift `
        "quality.maximumRoiMeanLuminanceShift"
    $maximumRoiP95LuminanceShift = Assert-FiniteNumber `
        $Manifest.quality.maximumRoiP95LuminanceShift `
        "quality.maximumRoiP95LuminanceShift"
    if ($maximumRelativeRmse -gt 0.005 -or
        $maximumFlipP95 -gt 0.02 -or
        $maximumRoiMeanLuminanceShift -gt 0.02 -or
        $maximumRoiP95LuminanceShift -gt 0.03) {
        throw "Campaign quality thresholds are weaker than the approved contract."
    }
    if ([int]$Manifest.qualitySequence.baselineRepeatCount -ne 2 -or
        [int]$Manifest.qualitySequence.maximumReadbackDrainFrames -ne 240 -or
        [double]$Manifest.qualitySequence.temporalResidualFloor -ne 0.000001 -or
        [double]$Manifest.qualitySequence.temporalResidualMultiplier -ne 2.0 -or
        [double]$Manifest.qualitySequence.temporalResidualHardCeiling -ne 0.005) {
        throw "Campaign quality-sequence repeatability policy differs from the exact approved contract."
    }
    if ([double]$Manifest.acceptance.minimumFrameImprovementPercent -ne 1.0 -or
        [double]$Manifest.acceptance.minimumFrameImprovementMilliseconds -ne 0.10 -or
        [double]$Manifest.acceptance.minimumPassImprovementPercent -ne 5.0 -or
        [double]$Manifest.acceptance.minimumPassImprovementMilliseconds -ne 0.05 -or
        [double]$Manifest.acceptance.maximumRegressionPercent -ne 1.0 -or
        [int]$Manifest.acceptance.bootstrapSamples -ne 10000 -or
        [double]$Manifest.acceptance.bootstrapConfidence -ne 0.95) {
        throw "Campaign performance/statistical thresholds differ from the approved contract."
    }

    if ([string]$Manifest.capture.budgetProfile -ne "stress") {
        throw "The approved campaign uses the stress budget profile."
    }

    $mandatoryProtectedPaths = @(
        "tools/perf-campaign.ps1",
        "tools/perf-campaign.bistro-sponza.json",
        "tools/perf-campaign.tests.ps1",
        "tools/perf-quality-verify.ps1",
        "tools/perf-loop.ps1",
        "NjulfHelloGame/Program.cs",
        "NjulfHelloGame/SampleBenchmarkRunner.cs",
        "NjulfHelloGame/SampleBenchmarkReport.cs",
        "NjulfHelloGame/SampleBenchmarkEvidence.cs",
        "NjulfHelloGame/SampleBenchmarkCaptureVariant.cs",
        "NjulfHelloGame/SampleBenchmarkHdrQualityContract.cs",
        "NjulfHelloGame/SampleBenchmarkQualitySequence.cs",
        "NjulfHelloGame/SampleBenchmarkQualitySequenceRunner.cs",
        "NjulfHelloGame/SampleBenchmarkOptions.cs",
        "NjulfHelloGame/SampleBenchmarkPairComparer.cs",
        "NjulfHelloGame/SampleBenchmarkTrajectory.cs",
        "NjulfHelloGame/SampleEvidenceFileIo.cs",
        "NjulfHelloGame/SampleHealthReportWriter.cs",
        "NjulfHelloGame/SampleHealthReportEvaluation.cs",
        "NjulfHelloGame/SampleMaterialGiProducerIdentityFactory.cs",
        "NjulfHelloGame/SampleMaterialGiApprovedHdrRegression.cs",
        "NjulfHelloGame/SampleRenderSettingsFingerprint.cs",
        "NjulfHelloGame/SampleSmokeOptions.cs",
        "NjulfHelloGame/SampleSmokeOptionsParser.cs",
        "NjulfHelloGame/SampleInputController.cs",
        "NjulfHelloGame/SampleAssetManifest.cs",
        "NjulfHelloGame/SampleAssetValidationGate.cs",
        "NjulfHelloGame/SampleBenchmarkGateEvaluation.cs",
        "NjulfHelloGame/SampleBudgetMetricCoverage.cs",
        "NjulfHelloGame/SampleDdgiProductionGate.cs",
        "NjulfHelloGame/SampleDdgiBenchmarkSuite.cs",
        "Njulf.Rendering/Data/MaterialGiRolloutPolicy.cs",
        "Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs",
        "Njulf.Rendering/Diagnostics/RenderBudgetProfile.cs",
        "Njulf.Rendering/Diagnostics/RenderBudgetSettings.cs",
        "Njulf.Rendering/Diagnostics/RenderBudgetSnapshot.cs",
        "Njulf.Rendering/Diagnostics/RenderBudgetStatus.cs",
        "Njulf.Rendering/Diagnostics/RendererHealthReportWriter.cs",
        "Njulf.Rendering/Debugging/LinearHdrReadback.cs",
        "Njulf.Tests/SampleBenchmarkQualitySequenceTests.cs",
        "NjulfHelloGame/SampleBenchmarkActivation.cs",
        "NjulfHelloGame/SampleBenchmarkActivationVerificationCli.cs",
        "NjulfHelloGame/SampleBenchmarkControlledIsolation.cs",
        "NjulfHelloGame/SampleBenchmarkQualityActivationVerificationCli.cs",
        "NjulfHelloGame/SampleBenchmarkSponzaSceneAnimation.cs",
        "NjulfHelloGame/SampleBenchmarkSponzaSceneAnimationSidecar.cs",
        "NjulfHelloGame/SampleAnimatedCharacter.cs",
        "NjulfHelloGame/SampleBenchmarkReflectionProbeCaptureEvidence.cs",
        "NjulfHelloGame/SampleBenchmarkDdgiTransientEvidence.cs",
        "NjulfHelloGame/SampleBenchmarkDdgiTransientVerificationCli.cs",
        "NjulfHelloGame/SampleBistroQualityCaptureHarness.cs",
        "NjulfHelloGame/SampleSponzaGiCaptureHarness.cs",
        "NjulfHelloGame/SampleTailDdgiQualification.cs",
        "NjulfHelloGame/Strut.glb",
        "Njulf.Assets/Scenes/SceneDocumentLoader.cs",
        "Njulf.Assets/ContentManager.cs",
        "Njulf.Assets/Cooked",
        "Njulf.Core/Animation/Animator.cs",
        "Njulf.Core/Scene/Model.cs",
        "Njulf.Rendering/Data/RenderSettings.cs",
        "Njulf.Rendering/Data/DirectionalShadowRuntimeDiagnostics.cs",
        "Njulf.Rendering/Data/RendererDiagnostics.cs",
        "Njulf.Rendering/Data/SceneRenderingData.cs",
        "Njulf.Rendering/Data/MaterialGiReleaseEvidenceAuthenticity.cs",
        "Njulf.Rendering/Data/SimpleDdgiTransientFrameEvidence.cs",
        "Njulf.Rendering/Data/SimpleDdgiTransportTailSummary.cs",
        "Njulf.Rendering/Pipeline/DirectionalShadowPass.cs",
        "Njulf.Rendering/Pipeline/ForwardPlusPass.cs",
        "Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs",
        "Njulf.Rendering/Resources/ReflectionProbeCaptureScheduler.cs",
        "Njulf.Rendering/Resources/ReflectionProbeFrameTelemetry.cs",
        "Njulf.Rendering/Resources/ReflectionProbeGpuBudgetPlanner.cs",
        "Njulf.Rendering/Resources/ReflectionProbeManager.cs",
        "Njulf.Rendering/Resources/SimpleDdgiGpuScheduler.cs",
        "Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs",
        "Njulf.Rendering/Debugging/GpuTimestampRecorder.cs",
        "Njulf.Rendering/VulkanRenderer.cs",
        "Njulf.Tests/SampleAnimatedCharacterTests.cs",
        "Njulf.Tests/SampleBenchmarkSponzaSceneAnimationTests.cs",
        "Njulf.Tests/SampleBenchmarkControlledIsolationTests.cs",
        "Njulf.Tests/SampleBenchmarkPairComparerTests.cs",
        "Njulf.Tests/SampleBenchmarkEvidenceTests.cs",
        "Njulf.Tests/SampleBenchmarkGateEvaluationTests.cs",
        "Njulf.Tests/SampleBenchmarkTrajectoryTests.cs",
        "Njulf.Tests/SampleEvidenceFileIoTests.cs",
        "Njulf.Tests/SampleBenchmarkAnalyzerTests.cs",
        "Njulf.Tests/SampleBenchmarkReflectionProbeCaptureEvidenceTests.cs",
        "Njulf.Tests/SampleBenchmarkDdgiTransientEvidenceTests.cs",
        "Njulf.Tests/SampleBenchmarkDdgiTransientVerificationTests.cs",
        "Njulf.Tests/SimpleDdgiTransientFrameEvidenceTests.cs",
        "Njulf.Tests/ReflectionProbeCaptureSchedulerTests.cs",
        "Njulf.Tests/ReflectionProbeFrameTelemetryTests.cs",
        "Njulf.Tests/ReflectionProbeGpuBudgetPlannerTests.cs",
        "Njulf.Tests/ReflectionProbeRecapturePolicyTests.cs",
        "Njulf.Tests/SampleBistroQualityCaptureHarnessTests.cs",
        "Njulf.Tests/SampleSponzaGiCaptureHarnessTests.cs",
        "Njulf.Tests/DebugToolingContractsTests.cs",
        "Njulf.Tests/MaterialGiRolloutPolicyTests.cs",
        ".perf-loop-runs/campaign/beauty-target/manifest.json",
        ".perf-loop-runs/campaign/beauty-target/bistro-beauty-target.jpg",
        "NjulfHelloGame/Assets/Bistro_v5_2",
        "NjulfHelloGame/NewSponza_Main_glTF_003.gltf",
        "NjulfHelloGame/NewSponza_Main_glTF_003.bin",
        "NjulfHelloGame/NewSponza_Curtains_glTF.gltf",
        "NjulfHelloGame/NewSponza_Curtains_glTF.bin",
        "NjulfHelloGame/textures",
        "NjulfHelloGame/Cooked",
        "NjulfHelloGame/Scenes")
    $protectedPathSet = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($protectedPath in @($Manifest.protectedPaths)) {
        $value = [string]$protectedPath
        Assert-Text $value "protectedPaths entry"
        if (-not $protectedPathSet.Add($value)) {
            throw "Protected path '$value' is duplicated."
        }
    }
    foreach ($mandatoryPath in $mandatoryProtectedPaths) {
        if (-not $protectedPathSet.Contains($mandatoryPath)) {
            throw "Required protected path '$mandatoryPath' is missing."
        }
    }

    $expectedTopology = [ordered]@{
        # scene, scenario, timing trajectory, quality trajectory, variant,
        # warmup, timing frames, qualification, Bistro variant, activation,
        # observed pass, isolation group, isolation role
        "bistro-stationary" = @("Bistro", "Normal", "bistro-presentation", "bistro-presentation", "baseline", 480, 240, $true, "presentation", "none", "", "", "")
        "bistro-motion" = @("Bistro", "BistroQualityMotionRelight", "bistro-loop", "bistro-loop", "baseline", 480, 240, $true, "steady-motion", "none", "", "", "")
        "bistro-motion-relight" = @("Bistro", "BistroQualityMotionRelight", "bistro-loop", "bistro-loop", "baseline", 480, 240, $true, "sun-scale-step", "none", "", "", "")
        "sponza-low-stationary" = @("Sponza", "GiSponzaRightWallStationary", "sponza-low", "sponza-low", "baseline", 2048, 240, $true, "", "none", "", "", "")
        "sponza-high-stationary" = @("Sponza", "GiSponzaRightWallStationary", "sponza-high", "sponza-high", "baseline", 2688, 240, $true, "", "none", "", "", "")
        "sponza-horizontal-motion" = @("Sponza", "GiSponzaRightWallStationary", "sponza-horizontal", "sponza-horizontal", "baseline", 2688, 300, $true, "", "none", "", "", "")
        "sponza-vertical-motion" = @("Sponza", "GiSponzaRightWallStationary", "sponza-vertical", "sponza-vertical", "baseline", 2688, 960, $true, "", "none", "", "", "")
        "bistro-forward-gi-enabled" = @("Bistro", "Normal", "bistro-presentation", "bistro-presentation", "forward-gi-enabled", 480, 240, $false, "presentation", "none", "ForwardPlusPass", "bistro-forward-gi", "enabled")
        "sponza-forward-gi-enabled" = @("Sponza", "GiSponzaRightWallStationary", "sponza-horizontal", "sponza-horizontal", "forward-gi-enabled", 2688, 300, $false, "", "sponza-forward-gi", "ForwardPlusPass", "sponza-forward-gi", "enabled")
    }
    $workloads = @($Manifest.workloads)
    if ($workloads.Count -ne $expectedTopology.Count) {
        throw "Campaign workload topology must contain exactly $($expectedTopology.Count) approved workloads."
    }
    $expectedWorkloadIds = @($expectedTopology.Keys)
    for ($workloadIndex = 0; $workloadIndex -lt $workloads.Count; $workloadIndex++) {
        if ([string]$workloads[$workloadIndex].id -cne
            [string]$expectedWorkloadIds[$workloadIndex]) {
            throw "Campaign workload order differs at index $workloadIndex."
        }
    }
    $ids = @{}
    $requiredQualification = @(
        "bistro-stationary",
        "bistro-motion",
        "bistro-motion-relight",
        "sponza-low-stationary",
        "sponza-high-stationary",
        "sponza-horizontal-motion",
        "sponza-vertical-motion")
    foreach ($workload in $workloads) {
        $null = Assert-JsonObject $workload "Workload"
        $id = Assert-JsonString $workload.id "Workload id"
        $expectedWorkloadProperties = switch ($id) {
            { $_ -in @(
                    "bistro-stationary", "bistro-motion",
                    "bistro-motion-relight") } {
                @(
                    "id", "scene", "scenario", "trajectory",
                    "qualityTrajectory", "bistroQualityVariant",
                    "captureVariant", "activation", "warmupFrames",
                    "measureFrames", "qualification", "qualityRois")
                break
            }
            { $_ -in @(
                    "sponza-low-stationary", "sponza-high-stationary",
                    "sponza-horizontal-motion", "sponza-vertical-motion") } {
                @(
                    "id", "scene", "scenario", "trajectory",
                    "qualityTrajectory", "captureVariant", "activation",
                    "warmupFrames", "measureFrames", "qualification",
                    "qualityRois")
                break
            }
            { $_ -in @(
                    "bistro-forward-gi-enabled") } {
                @(
                    "id", "scene", "scenario", "trajectory",
                    "qualityTrajectory", "bistroQualityVariant",
                    "captureVariant", "activation", "warmupFrames",
                    "measureFrames", "qualification", "targetPass",
                    "isolationGroup", "isolationRole", "qualityRois")
                break
            }
            { $_ -in @(
                    "sponza-forward-gi-enabled") } {
                @(
                    "id", "scene", "scenario", "trajectory",
                    "qualityTrajectory", "captureVariant", "activation",
                    "warmupFrames", "measureFrames", "qualification",
                    "targetPass", "isolationGroup", "isolationRole",
                    "qualityRois")
                break
            }
            default { @("id") }
        }
        Assert-ExactPropertyNames `
            $workload $expectedWorkloadProperties "Workload '$id'"
        foreach ($property in @(
                "scene", "scenario", "trajectory", "qualityTrajectory",
                "captureVariant", "activation")) {
            $null = Assert-JsonString `
                $workload.$property "Workload '$id' $property"
        }
        foreach ($property in @(
                "bistroQualityVariant", "targetPass", "isolationGroup",
                "isolationRole")) {
            if ($expectedWorkloadProperties -contains $property) {
                $null = Assert-JsonString `
                    $workload.$property "Workload '$id' $property"
            }
        }
        $null = Assert-JsonInteger `
            $workload.warmupFrames "Workload '$id' warmupFrames" 0
        $null = Assert-JsonInteger `
            $workload.measureFrames "Workload '$id' measureFrames" 1
        $null = Assert-JsonBoolean `
            $workload.qualification "Workload '$id' qualification"
        if ($expectedWorkloadProperties -contains "qualityRois") {
            $null = Assert-JsonArray `
                $workload.qualityRois "Workload '$id' qualityRois"
        }
        if ($id -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
            throw "Workload id '$id' is invalid."
        }
        if ($ids.ContainsKey($id)) {
            throw "Workload id '$id' is duplicated."
        }
        $ids[$id] = $true
        if (-not $expectedTopology.Contains($id)) {
            throw "Workload '$id' is not in the approved campaign topology."
        }
        $expected = $expectedTopology[$id]
        $actual = @(
            [string]$workload.scene,
            [string]$workload.scenario,
            [string]$workload.trajectory,
            [string]$workload.qualityTrajectory,
            [string]$workload.captureVariant,
            [int]$workload.warmupFrames,
            [int]$workload.measureFrames,
            [bool]$workload.qualification,
            [string](Get-PropertyValue $workload "bistroQualityVariant" ""),
            [string]$workload.activation,
            [string](Get-PropertyValue $workload "targetPass" ""),
            [string](Get-PropertyValue $workload "isolationGroup" ""),
            [string](Get-PropertyValue $workload "isolationRole" ""))
        for ($topologyIndex = 0; $topologyIndex -lt $expected.Count; $topologyIndex++) {
            if (-not [string]::Equals(
                    [string]$actual[$topologyIndex],
                    [string]$expected[$topologyIndex],
                    [StringComparison]::Ordinal)) {
                throw "Workload '$id' differs from approved topology field $topologyIndex."
            }
        }
        if ([string]$workload.scene -notin @("Bistro", "Sponza")) {
            throw "Workload '$id' must use Bistro or Sponza."
        }
        Assert-Text ([string]$workload.scenario) "Workload '$id' scenario"
        $trajectory = [string]$workload.trajectory
        if ($trajectory -notin @(
                "stationary", "bistro-presentation", "bistro-loop",
                "sponza-low", "sponza-high", "sponza-horizontal", "sponza-vertical")) {
            throw "Workload '$id' has unsupported trajectory '$trajectory'."
        }
        $expectedFrames = switch ($trajectory) {
            "bistro-loop" { 240 }
            "sponza-horizontal" { 300 }
            "sponza-vertical" { 960 }
            default { 0 }
        }
        if ($expectedFrames -gt 0 -and [int]$workload.measureFrames -ne $expectedFrames) {
            throw "Workload '$id' must measure exactly $expectedFrames trajectory frames."
        }
        $qualityTrajectory = [string]$workload.qualityTrajectory
        if ($qualityTrajectory -notin @(
                "stationary", "bistro-presentation", "bistro-loop",
                "sponza-low", "sponza-high", "sponza-horizontal", "sponza-vertical")) {
            throw "Workload '$id' has unsupported quality trajectory '$qualityTrajectory'."
        }
        $activation = [string]$workload.activation
        if ($activation -notin @(
                "none", "sponza-forward-gi")) {
            throw "Workload '$id' has unsupported activation '$activation'."
        }
        if ([int]$workload.measureFrames -lt 120) {
            throw "Workload '$id' must measure at least 120 frames."
        }
        Assert-Text ([string]$workload.captureVariant) "Workload '$id' captureVariant"
        $reservedArguments = @(
            "--scene", "--performance-scenario", "--quality-preset",
            "--validation", "--gpu-timing", "--health-report",
            "--bistro-quality-variant", "--benchmark-activation",
            "--verify-benchmark-activation-report",
            "--verify-benchmark-ddgi-transient-report",
            "--verify-benchmark-quality-activation-report",
            "--verify-directional-controlled-isolation")
        $workloadArguments = @((Get-PropertyValue $workload "arguments" @()))
        foreach ($argument in $workloadArguments) {
            $optionName = ([string]$argument).Split('=', 2)[0].ToLowerInvariant()
            if ($optionName -eq "--benchmark" -or
                $optionName.StartsWith("--benchmark-", [StringComparison]::Ordinal) -or
                $reservedArguments -contains $optionName) {
                throw "Workload '$id' arguments may not override reserved option '$optionName'."
            }
        }
        if ($workloadArguments.Count -ne 0) {
            throw "Workload '$id' may not add arguments outside the exact approved topology."
        }
        $qualityRois = @((Get-PropertyValue $workload "qualityRois" @()))
        $expectedRoiCount = if ($expectedWorkloadProperties -contains
            "qualityRois") { 1 } else { 0 }
        if ($qualityRois.Count -ne $expectedRoiCount) {
            throw "Workload '$id' must use exactly $expectedRoiCount full-frame ROI contracts."
        }
        foreach ($roi in $qualityRois) {
            $null = Assert-JsonObject $roi "Workload '$id' ROI"
            Assert-ExactPropertyNames `
                $roi @("name", "x", "y", "width", "height") `
                "Workload '$id' ROI"
            $null = Assert-JsonString $roi.name "Workload '$id' ROI name"
            foreach ($property in @("x", "y", "width", "height")) {
                $null = Assert-JsonInteger `
                    $roi.$property "Workload '$id' ROI $property" 0
            }
            if ([int]$roi.x -ne 0 -or [int]$roi.y -ne 0 -or
                [int]$roi.width -ne 1920 -or [int]$roi.height -ne 1080) {
                throw "Workload '$id' ROI '$($roi.name)' must cover the full 1920x1080 performance frame."
            }
        }
    }
    foreach ($id in $requiredQualification) {
        if (-not $ids.ContainsKey($id)) {
            throw "Required qualification workload '$id' is missing."
        }
        $workload = @($workloads | Where-Object { [string]$_.id -ceq $id })[0]
        if (-not [bool]$workload.qualification) {
            throw "Required workload '$id' must be a qualification gate."
        }
    }

    $discovery = $Manifest.discoveryPolicy
    $null = Assert-JsonObject $discovery "discoveryPolicy"
    Assert-ExactPropertyNames $discovery @(
        "enabled", "domains", "minimumSharePercent",
        "minimumP95Milliseconds", "requireBothConfigurations",
        "requireBistroAndSponza", "attemptsPerTiming",
        "fullMatrixAfterCandidate") "discoveryPolicy"
    if ((Assert-JsonBoolean $discovery.enabled "discoveryPolicy.enabled") -ne $true -or
        (Assert-JsonBoolean $discovery.requireBothConfigurations `
            "discoveryPolicy.requireBothConfigurations") -ne $true -or
        (Assert-JsonBoolean $discovery.requireBistroAndSponza `
            "discoveryPolicy.requireBistroAndSponza") -ne $true -or
        (Assert-JsonBoolean $discovery.fullMatrixAfterCandidate `
            "discoveryPolicy.fullMatrixAfterCandidate") -ne $true -or
        (Assert-JsonInteger $discovery.attemptsPerTiming `
            "discoveryPolicy.attemptsPerTiming" 1) -ne 1 -or
        (Assert-FiniteNumber $discovery.minimumSharePercent `
            "discoveryPolicy.minimumSharePercent") -ne 5.0 -or
        (Assert-FiniteNumber $discovery.minimumP95Milliseconds `
            "discoveryPolicy.minimumP95Milliseconds") -ne 0.25) {
        throw "Hotspot discovery policy differs from the approved production contract."
    }
    $null = Assert-JsonArray $discovery.domains "discoveryPolicy.domains"
    if (@($discovery.domains).Count -ne 2 -or
        [string]$discovery.domains[0] -cne "gpu" -or
        [string]$discovery.domains[1] -cne "cpu") {
        throw "Hotspot discovery domains must be exact ordered GPU then CPU."
    }

    $expectedCandidates = [ordered]@{
        "receiver-cache-shared-workgroup" = @(
            "4a9ee4c18d00211571f056a26d2047779d9622fa",
            "6f54b21b9012d66d434905942552c7f392a9bd31",
            "receiver-cache",
            "FullyQualifiedName~SimpleDdgiShaderMirrorTests|FullyQualifiedName~ShaderBuildTests",
            @(
                "Njulf.Shaders/ddgi_simple_receiver_cache.comp",
                "Njulf.Tests/ShaderBuildTests.cs",
                "Njulf.Tests/SimpleDdgiShaderMirrorTests.cs"))
        "ao-center-depth-reuse" = @(
            "f634c59dab0f89f959e3135b6babc531e54720af",
            "02a27b53fba40845a1c65c6d090f43be61ae8fe3",
            "ambient-occlusion",
            "FullyQualifiedName~AmbientOcclusionShaderContractTests|FullyQualifiedName~ShaderBuildTests",
            @(
                "Njulf.Shaders/ambient_occlusion.comp",
                "Njulf.Tests/AmbientOcclusionShaderContractTests.cs",
                "Njulf.Tests/ShaderBuildTests.cs"))
        "ao-blur-shared-tile" = @(
            "d794d3163122b128b2390931e07114202de49223",
            "b84c0bf960a9a1b4fd930c065441273611714968",
            "ambient-occlusion-blur",
            "FullyQualifiedName~AmbientOcclusionBlurSharedTileTests",
            @(
                "Njulf.Shaders/ambient_occlusion_blur.comp",
                "Njulf.Tests/AmbientOcclusionBlurSharedTileTests.cs"))
        "ddgi-feedback-partial-reduction" = @(
            "60d6c5f5f96a288a5f71ff856060dc394f78985e",
            "eec1d6c3201b645585dcb3f292177d5c023f1d0a",
            "ddgi-scheduler-commit",
            "FullyQualifiedName~SimpleDdgiFeedbackPartialReductionTests|FullyQualifiedName~SimpleDdgiShaderMirrorTests|FullyQualifiedName~ShaderBuildTests",
            @(
                "Njulf.Rendering/Pipeline/SimpleDdgiSchedulerCommitPass.cs",
                "Njulf.Shaders/Njulf.Shaders.csproj",
                "Njulf.Shaders/VerifyProductionDiagnosticAtomics.ps1",
                "Njulf.Shaders/ddgi_simple_schedule_feedback.comp",
                "Njulf.Tests/SimpleDdgiFeedbackPartialReductionTests.cs",
                "Njulf.Tests/SimpleDdgiShaderMirrorTests.cs"))
    }
    $candidates = @($Manifest.candidates)
    if ($candidates.Count -ne $expectedCandidates.Count) {
        throw "Campaign must declare exactly four ordered reviewed candidates."
    }
    $candidateIds = @($expectedCandidates.Keys)
    for ($candidateIndex = 0; $candidateIndex -lt $candidates.Count; $candidateIndex++) {
        $candidate = $candidates[$candidateIndex]
        $expectedId = [string]$candidateIds[$candidateIndex]
        $expected = $expectedCandidates[$expectedId]
        $null = Assert-JsonObject $candidate "Candidate $candidateIndex"
        Assert-ExactPropertyNames $candidate @(
            "id", "sourceCommit", "patchId", "hypothesisId",
            "allowedPaths", "focusedTestFilter") "Candidate $candidateIndex"
        foreach ($property in @(
                "id", "sourceCommit", "patchId", "hypothesisId",
                "focusedTestFilter")) {
            $null = Assert-JsonString `
                $candidate.$property "Candidate '$expectedId' $property"
        }
        $null = Assert-JsonArray `
            $candidate.allowedPaths "Candidate '$expectedId' allowedPaths"
        if ([string]$candidate.id -cne $expectedId -or
            [string]$candidate.sourceCommit -cne [string]$expected[0] -or
            [string]$candidate.patchId -cne [string]$expected[1] -or
            [string]$candidate.hypothesisId -cne [string]$expected[2] -or
            [string]$candidate.focusedTestFilter -cne [string]$expected[3] -or
            (@($candidate.allowedPaths) -join "`n") -cne
                (@($expected[4]) -join "`n")) {
            throw "Candidate '$expectedId' differs from the approved patch contract."
        }
        if ((Get-StablePatchId ([string]$candidate.sourceCommit)) -cne
                [string]$candidate.patchId -or
            (@(Get-CommitChangedPaths ([string]$candidate.sourceCommit)) -join "`n") -cne
                (@($candidate.allowedPaths) -join "`n")) {
            throw "Candidate '$expectedId' source patch identity or changed paths differ."
        }
        foreach ($path in @($candidate.allowedPaths)) {
            if ($protectedPathSet.Contains([string]$path)) {
                throw "Candidate '$expectedId' may not modify protected path '$path'."
            }
        }
    }

    $expectedHypotheses = [ordered]@{
        "receiver-cache" = @(
            "SimpleDdgiReceiverCachePass",
            "Bistro", "bistro-forward-gi-enabled",
            "Sponza", "sponza-forward-gi-enabled")
        "ambient-occlusion" = @(
            "AmbientOcclusionPass",
            "Bistro", "bistro-motion",
            "Sponza", "sponza-horizontal-motion")
        "ambient-occlusion-blur" = @(
            "AmbientOcclusionBlurPass",
            "Bistro", "bistro-motion",
            "Sponza", "sponza-horizontal-motion")
        "ddgi-scheduler-commit" = @(
            "SimpleDdgiSchedulerCommitPass",
            "Bistro", "bistro-motion-relight",
            "Sponza", "sponza-horizontal-motion")
    }
    $hypotheses = @($Manifest.targetHypotheses)
    if ($hypotheses.Count -ne $expectedHypotheses.Count) {
        throw "Campaign target hypothesis topology must contain exactly four approved hypotheses."
    }
    $expectedHypothesisIds = @($expectedHypotheses.Keys)
    for ($hypothesisIndex = 0;
         $hypothesisIndex -lt $hypotheses.Count;
         $hypothesisIndex++) {
        $hypothesis = $hypotheses[$hypothesisIndex]
        $expectedId = [string]$expectedHypothesisIds[$hypothesisIndex]
        $null = Assert-JsonObject `
            $hypothesis "Target hypothesis $hypothesisIndex"
        Assert-ExactPropertyNames $hypothesis @(
            "id", "targetDomain", "targetPass", "claims") `
            "Target hypothesis $hypothesisIndex"
        $null = Assert-JsonString `
            $hypothesis.id "Target hypothesis $hypothesisIndex id"
        $null = Assert-JsonString `
            $hypothesis.targetDomain `
            "Target hypothesis '$expectedId' targetDomain"
        $null = Assert-JsonString `
            $hypothesis.targetPass "Target hypothesis '$expectedId' targetPass"
        $null = Assert-JsonArray `
            $hypothesis.claims "Target hypothesis '$expectedId' claims"
        if ([string]$hypothesis.id -cne $expectedId) {
            throw "Campaign target hypothesis order differs at index $hypothesisIndex."
        }
        $expectedHypothesis = $expectedHypotheses[$expectedId]
        if ([string]$hypothesis.targetDomain -cne "gpu" -or
            [string]$hypothesis.targetPass -cne
            [string]$expectedHypothesis[0]) {
            throw "Target hypothesis '$expectedId' pass differs from the approved contract."
        }
        $claims = @($hypothesis.claims)
        if ($claims.Count -ne 2) {
            throw "Target hypothesis '$expectedId' must contain exact Bistro and Sponza claims."
        }
        for ($claimIndex = 0; $claimIndex -lt 2; $claimIndex++) {
            $claim = $claims[$claimIndex]
            $null = Assert-JsonObject `
                $claim "Target hypothesis '$expectedId' claim $claimIndex"
            Assert-ExactPropertyNames $claim @("scene", "workloadId") `
                "Target hypothesis '$expectedId' claim $claimIndex"
            $null = Assert-JsonString `
                $claim.scene `
                "Target hypothesis '$expectedId' claim $claimIndex scene"
            $null = Assert-JsonString `
                $claim.workloadId `
                "Target hypothesis '$expectedId' claim $claimIndex workloadId"
            $expectedScene = [string]$expectedHypothesis[1 + ($claimIndex * 2)]
            $expectedWorkloadId =
                [string]$expectedHypothesis[2 + ($claimIndex * 2)]
            if ([string]$claim.scene -cne $expectedScene -or
                [string]$claim.workloadId -cne $expectedWorkloadId -or
                -not $ids.ContainsKey($expectedWorkloadId)) {
                throw "Target hypothesis '$expectedId' claim $claimIndex differs from the approved contract."
            }
        }
    }
}

function Read-CampaignManifest {
    $bytes = Read-BoundedFileBytes `
        $script:ManifestFile 4MB "Campaign manifest"
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $json = $encoding.GetString($bytes)
    } catch {
        throw "Campaign manifest is not canonical UTF-8."
    }
    $documentOptions = [System.Text.Json.JsonDocumentOptions]::new()
    $documentOptions.AllowTrailingCommas = $false
    $documentOptions.CommentHandling =
        [System.Text.Json.JsonCommentHandling]::Disallow
    $documentOptions.MaxDepth = 32
    try {
        $document = [System.Text.Json.JsonDocument]::Parse(
            $json,
            $documentOptions)
        try {
            if ($document.RootElement.ValueKind -ne
                [System.Text.Json.JsonValueKind]::Object) {
                throw "Campaign manifest root must be a JSON object."
            }
            Assert-NoDuplicateJsonProperties `
                $document.RootElement "Campaign manifest"
        } finally {
            $document.Dispose()
        }
        $manifest = $json | ConvertFrom-Json -DateKind String
    } catch {
        throw "Campaign manifest is not strict JSON: $($_.Exception.Message)"
    }
    $script:CampaignManifestBytes = [byte[]]$bytes
    $script:CampaignManifestSha256 = Get-Sha256Bytes $bytes
    Assert-CampaignManifest $manifest
    Assert-CampaignManifestIntegrity
    return $manifest
}

function Stop-ProcessTreeAndDrain {
    param(
        [System.Diagnostics.Process]$Process,
        $StdoutTask,
        $StderrTask,
        [string]$Label)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            $Process.Kill($true)
        }
    } catch {
        if (-not $Process.HasExited) {
            throw "$Label process tree could not be terminated: $($_.Exception.Message)"
        }
    }
    if (-not $Process.WaitForExit(30000) -or -not $Process.HasExited) {
        throw "$Label process tree did not reach a terminal state after termination."
    }
    foreach ($task in @($StdoutTask, $StderrTask)) {
        if ($null -eq $task) { continue }
        try {
            if (-not $task.Wait(30000)) {
                throw "$Label redirected stream did not drain after process termination."
            }
            $null = $task.GetAwaiter().GetResult()
        } catch {
            if (-not $task.IsCompleted) { throw }
        }
    }
}

function Invoke-ProcessChecked {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$Label,
        [int]$TimeoutSeconds,
        [string]$WorkingDirectory = $script:SolutionRoot
    )
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$info.ArgumentList.Add([string]$argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    Write-Host "$Label"
    $started = $false
    $stdoutTask = $null
    $stderrTask = $null
    try {
        if (-not $process.Start()) {
            throw "$Label failed to start."
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = if ($TimeoutSeconds -le 0) {
            $process.WaitForExit()
            $true
        } else {
            $process.WaitForExit($TimeoutSeconds * 1000)
        }
        if (-not $completed) {
            Stop-ProcessTreeAndDrain `
                $process $stdoutTask $stderrTask $Label
            throw "$Label timed out after $TimeoutSeconds seconds after terminal process-tree cleanup."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout.TrimEnd()
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Warning $stderr.TrimEnd()
        }
        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode)."
        }
    } finally {
        if ($started -and -not $process.HasExited) {
            Stop-ProcessTreeAndDrain `
                $process $stdoutTask $stderrTask $Label
        }
        $process.Dispose()
    }
}

function Write-AtomicByteArtifact {
    param([string]$Path, [byte[]]$Bytes, [string]$Label)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) {
        throw "$Label has no bytes to publish."
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $fullPath) {
        throw "$Label artifact already exists; refusing to overwrite $fullPath"
    }
    $directory = Split-Path -Parent $fullPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f
            [System.IO.Path]::GetFileName($fullPath),
            [Guid]::NewGuid().ToString("N"))
    try {
        $stream = [System.IO.File]::Open(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $stream.Write($Bytes, 0, $Bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        [System.IO.File]::Move($temporaryPath, $fullPath, $false)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Assert-NoDuplicateJsonProperties {
    param(
        [System.Text.Json.JsonElement]$Element,
        [string]$Label,
        [string]$Path = '$')
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "$Label contains duplicate JSON property '$($property.Name)' at $Path."
            }
            Assert-NoDuplicateJsonProperties `
                $property.Value $Label "$Path.$($property.Name)"
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperties $item $Label "$Path[$index]"
            $index++
        }
    }
}

function ConvertFrom-FrozenVerifierBytes {
    param([byte[]]$Bytes, [string]$Label)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0 -or
        $Bytes.Length -gt 16MB) {
        throw "$Label stdout byte length is outside the admitted range."
    }
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $encoding.GetString($Bytes)
    } catch {
        throw "$Label stdout is not canonical UTF-8."
    }
    $terminatorLength = if ($text.EndsWith("`r`n", [StringComparison]::Ordinal)) {
        2
    } elseif ($text.EndsWith("`n", [StringComparison]::Ordinal)) {
        1
    } else {
        0
    }
    if ($text.Length -le $terminatorLength -or
        $terminatorLength -eq 0 -or
        [char]::IsWhiteSpace($text[0])) {
        throw "$Label stdout must be exactly one compact newline-terminated JSON object."
    }
    $json = $text.Substring(0, $text.Length - $terminatorLength)
    if (-not $json.StartsWith("{", [StringComparison]::Ordinal) -or
        -not $json.EndsWith("}", [StringComparison]::Ordinal) -or
        $json.Contains("`r", [StringComparison]::Ordinal) -or
        $json.Contains("`n", [StringComparison]::Ordinal)) {
        throw "$Label stdout must contain exactly one terminal line ending."
    }
    $documentOptions = [System.Text.Json.JsonDocumentOptions]::new()
    $documentOptions.AllowTrailingCommas = $false
    $documentOptions.CommentHandling =
        [System.Text.Json.JsonCommentHandling]::Disallow
    $documentOptions.MaxDepth = 64
    try {
        $document = [System.Text.Json.JsonDocument]::Parse(
            $json,
            $documentOptions)
        try {
            if ($document.RootElement.ValueKind -ne
                [System.Text.Json.JsonValueKind]::Object) {
                throw "$Label stdout root must be an object."
            }
            Assert-NoDuplicateJsonProperties $document.RootElement $Label
        } finally {
            $document.Dispose()
        }
        $result = $json | ConvertFrom-Json -DateKind String
    } catch {
        throw "$Label stdout is not one strict JSON object: $($_.Exception.Message)"
    }
    if ($null -eq $result) {
        throw "$Label stdout deserialized to null."
    }
    return $result
}

function Read-StrictJsonFile {
    param(
        [string]$Path,
        [long]$MaximumLength,
        [string]$Label)
    $bytes = Read-BoundedFileBytes $Path $MaximumLength $Label
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $json = $encoding.GetString($bytes)
    } catch {
        throw "$Label is not canonical UTF-8."
    }
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 64
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($json, $options)
        try {
            if ($document.RootElement.ValueKind -ne
                [System.Text.Json.JsonValueKind]::Object) {
                throw "$Label root must be a JSON object."
            }
            Assert-NoDuplicateJsonProperties $document.RootElement $Label
        } finally {
            $document.Dispose()
        }
        $value = $json | ConvertFrom-Json -DateKind String
    } catch {
        throw "$Label is not strict JSON: $($_.Exception.Message)"
    }
    if ($null -eq $value) { throw "$Label deserialized to null." }
    return [pscustomobject]@{
        Bytes = [byte[]]$bytes
        Sha256 = Get-Sha256Bytes $bytes
        Value = $value
    }
}

function Test-ByteSequenceEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($null -eq $Left -or $null -eq $Right -or
        $Left.Length -ne $Right.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Invoke-FrozenVerifierProcess {
    param(
        $VerifierBuildIdentity,
        [string[]]$Arguments,
        [string[]]$InputPaths,
        [string[]]$ExpectedInputSha256,
        [string]$Label,
        [int]$TimeoutSeconds)
    Assert-BuildIdentity $VerifierBuildIdentity "$Label frozen verifier"
    Assert-CampaignLockIntegrity
    if ($InputPaths.Count -ne $ExpectedInputSha256.Count -or
        $InputPaths.Count -eq 0) {
        throw "$Label verifier input topology is invalid."
    }
    for ($index = 0; $index -lt $InputPaths.Count; $index++) {
        $path = [System.IO.Path]::GetFullPath([string]$InputPaths[$index])
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            [string]$ExpectedInputSha256[$index] -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-Sha256 $path) -cne [string]$ExpectedInputSha256[$index]) {
            throw "$Label verifier input $index differs before invocation."
        }
    }

    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = [string]$VerifierBuildIdentity.ExecutablePath
    $info.WorkingDirectory = [string]$VerifierBuildIdentity.RootPath
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$info.ArgumentList.Add([string]$argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    $stdoutStream = [System.IO.MemoryStream]::new()
    $started = $false
    $stdoutTask = $null
    $stderrTask = $null
    try {
        if (-not $process.Start()) {
            throw "$Label frozen verifier failed to start."
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync(
            $stdoutStream)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = if ($TimeoutSeconds -le 0) {
            $process.WaitForExit()
            $true
        } else {
            $process.WaitForExit($TimeoutSeconds * 1000)
        }
        if (-not $completed) {
            Stop-ProcessTreeAndDrain `
                $process $stdoutTask $stderrTask "$Label frozen verifier"
            throw "$Label frozen verifier timed out after $TimeoutSeconds seconds after terminal process-tree cleanup."
        }
        $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $stdoutBytes = $stdoutStream.ToArray()
        if ($process.ExitCode -ne 0) {
            throw "$Label frozen verifier failed with exit code $($process.ExitCode): $stderr"
        }
        if (-not [string]::IsNullOrEmpty($stderr)) {
            throw "$Label frozen verifier emitted unexpected stderr: $stderr"
        }
    } finally {
        if ($started -and -not $process.HasExited) {
            Stop-ProcessTreeAndDrain `
                $process $stdoutTask $stderrTask "$Label frozen verifier"
        }
        $stdoutStream.Dispose()
        $process.Dispose()
    }

    Assert-BuildIdentity $VerifierBuildIdentity "$Label frozen verifier post-run"
    Assert-CampaignLockIntegrity
    for ($index = 0; $index -lt $InputPaths.Count; $index++) {
        $path = [System.IO.Path]::GetFullPath([string]$InputPaths[$index])
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Sha256 $path) -cne [string]$ExpectedInputSha256[$index]) {
            throw "$Label verifier input $index changed during invocation."
        }
    }
    $result = ConvertFrom-FrozenVerifierBytes $stdoutBytes $Label
    return [pscustomobject]@{
        Bytes = [byte[]]$stdoutBytes
        Result = $result
    }
}

function Assert-FrozenVerifierResultHeader {
    param(
        $Result,
        [string[]]$ExpectedProperties,
        [string]$ExpectedKind,
        [string]$ExpectedSchema,
        [string]$Label)
    Assert-ExactPropertyNames $Result $ExpectedProperties "$Label result"
    if ([string]$Result.kind -cne $ExpectedKind -or
        [string]$Result.schema -cne $ExpectedSchema -or
        $Result.passed -isnot [bool] -or
        $Result.passed -ne $true -or
        $null -eq $Result.failures -or
        $Result.failures -isnot [System.Object[]] -or
        $Result.failures.Count -ne 0) {
        throw "$Label frozen verifier rejected its authenticated evidence: $(@($Result.failures) -join '; ')"
    }
}

function New-FrozenVerifierArtifact {
    param(
        $VerifierBuildIdentity,
        [string[]]$Arguments,
        [string[]]$InputPaths,
        [string[]]$ExpectedInputSha256,
        [string]$ArtifactPath,
        [string]$ExpectedKind,
        [string]$ExpectedSchema,
        [string[]]$ExpectedProperties,
        [string]$Label,
        [int]$TimeoutSeconds = 120)
    $invocation = Invoke-FrozenVerifierProcess `
        $VerifierBuildIdentity $Arguments $InputPaths $ExpectedInputSha256 `
        $Label $TimeoutSeconds
    Assert-FrozenVerifierResultHeader `
        $invocation.Result $ExpectedProperties $ExpectedKind $ExpectedSchema $Label
    Write-AtomicByteArtifact $ArtifactPath $invocation.Bytes $Label
    $fullArtifactPath = [System.IO.Path]::GetFullPath($ArtifactPath)
    return [ordered]@{
        artifactPath = $fullArtifactPath
        artifactSha256 = Get-Sha256 $fullArtifactPath
        artifactByteLength = [long]$invocation.Bytes.Length
        verifierBuildIdentity = $VerifierBuildIdentity
        result = $invocation.Result
    }
}

function Assert-FrozenVerifierArtifact {
    param(
        $Evidence,
        $VerifierBuildIdentity,
        [string[]]$Arguments,
        [string[]]$InputPaths,
        [string[]]$ExpectedInputSha256,
        [string]$ExpectedArtifactPath,
        [string]$ExpectedKind,
        [string]$ExpectedSchema,
        [string[]]$ExpectedProperties,
        [string]$Label,
        [int]$TimeoutSeconds = 120)
    Assert-ExactPropertyNames $Evidence @(
        "artifactPath", "artifactSha256", "artifactByteLength",
        "verifierBuildIdentity", "result") "$Label evidence"
    Assert-PathIdentity ([string]$Evidence.artifactPath) `
        $ExpectedArtifactPath "$Label artifact"
    $artifactPath = [System.IO.Path]::GetFullPath($ExpectedArtifactPath)
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf) -or
        [string]$Evidence.artifactSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        (Get-Sha256 $artifactPath) -cne [string]$Evidence.artifactSha256) {
        throw "$Label frozen verifier artifact bytes differ."
    }
    $storedBytes = [System.IO.File]::ReadAllBytes($artifactPath)
    if ([long]$Evidence.artifactByteLength -ne $storedBytes.LongLength) {
        throw "$Label frozen verifier artifact length differs."
    }
    if (($Evidence.verifierBuildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
        ($VerifierBuildIdentity | ConvertTo-Json -Depth 12 -Compress)) {
        throw "$Label frozen verifier build identity differs from the lock."
    }
    $storedResult = ConvertFrom-FrozenVerifierBytes $storedBytes "$Label stored"
    Assert-FrozenVerifierResultHeader `
        $storedResult $ExpectedProperties $ExpectedKind $ExpectedSchema $Label
    if (($storedResult | ConvertTo-Json -Depth 32 -Compress) -cne
        ($Evidence.result | ConvertTo-Json -Depth 32 -Compress)) {
        throw "$Label duplicated frozen verifier result differs."
    }
    $recomputed = Invoke-FrozenVerifierProcess `
        $VerifierBuildIdentity $Arguments $InputPaths $ExpectedInputSha256 `
        "$Label replay" $TimeoutSeconds
    Assert-FrozenVerifierResultHeader `
        $recomputed.Result $ExpectedProperties $ExpectedKind $ExpectedSchema `
        "$Label replay"
    if (-not (Test-ByteSequenceEqual `
            ([byte[]]$storedBytes) ([byte[]]$recomputed.Bytes))) {
        throw "$Label frozen verifier stdout differs from durable bytes."
    }
    return $storedResult
}

function Get-ActivationVerifierResultProperties {
    return @(
        "kind", "schema", "passed", "reportPath", "reportSha256",
        "activation", "activationFingerprint",
        "activationStructuralSequenceHash",
        "activationExecutionSequenceHash",
        "reflectionProbeCaptureEvidenceDigest",
        "reflectionProbeCaptureRawRowCount",
        "reflectionProbeCaptureResultRowCount",
        "sponzaSceneAnimationFingerprint", "sponzaSceneAnimationMode",
        "sponzaSceneAnimationConfigurationFingerprint",
        "sponzaSceneAnimationSequenceHash",
        "sponzaSceneAnimationSidecarPath",
        "sponzaSceneAnimationSidecarSha256", "failures")
}

function Get-DdgiTransientVerifierResultProperties {
    return @(
        "kind", "schema", "passed", "reportPath", "reportSha256",
        "reportByteLength", "applicable", "available", "rawRowCount",
        "recomputedWindowCount", "recomputedWindowFrameCount",
        "semanticDigest", "failures")
}

function Get-QualityActivationVerifierResultProperties {
    return @(
        "kind", "schema", "passed", "reportPath", "reportSha256",
        "sequenceId", "role", "activation", "activationFingerprint",
        "activationStructuralSequenceHash",
        "activationExecutionSequenceHash",
        "sponzaSceneAnimationFingerprint", "sponzaSceneAnimationMode",
        "sponzaSceneAnimationConfigurationFingerprint",
        "sponzaSceneAnimationSequenceHash",
        "sponzaSceneAnimationSidecarPath",
        "sponzaSceneAnimationSidecarSha256", "failures")
}

function Get-ControlledIsolationVerifierResultProperties {
    return @(
        "kind", "schema", "passed", "cachedReportPath",
        "cachedReportSha256", "forcedReportPath", "forcedReportSha256",
        "artifactIdentityHash", "comparison", "failures")
}

function Test-WorkloadDdgiTransientApplicable {
    param($Workload)
    return [string]$Workload.scenario -ceq
            "BistroQualityMotionRelight" -and
        [string]$Workload.trajectory -ceq "bistro-loop" -and
        [string](Get-PropertyValue `
            $Workload "bistroQualityVariant" "") -ceq "sun-scale-step"
}

function Assert-ResultReportIdentity {
    param($Result, [string]$ReportPath, [string]$ReportSha256, [string]$Label)
    Assert-PathIdentity ([string]$Result.reportPath) $ReportPath `
        "$Label report"
    if ([string]$Result.reportSha256 -cne $ReportSha256) {
        throw "$Label report hash differs from its frozen input."
    }
}

function Assert-SponzaAnimationVerifierIdentity {
    param($Workload, $ReportAnimation, $Result, [string]$Label)
    if ([string]$Workload.scene -ceq "Sponza") {
        if ([string]$Result.sponzaSceneAnimationFingerprint -cne
                [string]$ReportAnimation.Fingerprint -or
            [int]$Result.sponzaSceneAnimationMode -ne
                [int]$ReportAnimation.Mode -or
            [string]$Result.sponzaSceneAnimationConfigurationFingerprint -cne
                [string]$ReportAnimation.ConfigurationFingerprint -or
            [string]$Result.sponzaSceneAnimationSequenceHash -cne
                [string]$ReportAnimation.SequenceHash -or
            [string]$Result.sponzaSceneAnimationSidecarSha256 -cne
                [string]$ReportAnimation.SidecarSha256 -or
            -not (Test-Sha256Identity `
                ([string]$Result.sponzaSceneAnimationFingerprint)) -or
            -not (Test-Sha256Identity `
                ([string]$Result.sponzaSceneAnimationConfigurationFingerprint)) -or
            -not (Test-Sha256Identity `
                ([string]$Result.sponzaSceneAnimationSequenceHash)) -or
            [string]$Result.sponzaSceneAnimationSidecarSha256 -cnotmatch
                '^[0-9a-f]{64}$') {
            throw "$Label common Sponza animation identity differs."
        }
        Assert-PathIdentity `
            ([string]$Result.sponzaSceneAnimationSidecarPath) `
            ([string]$ReportAnimation.SidecarPath) "$Label animation sidecar"
        $sidecarPath = [System.IO.Path]::GetFullPath(
            [string]$Result.sponzaSceneAnimationSidecarPath)
        if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf) -or
            (Get-Sha256 $sidecarPath) -cne
                [string]$Result.sponzaSceneAnimationSidecarSha256) {
            throw "$Label common Sponza animation sidecar bytes differ."
        }
        return [ordered]@{
            path = $sidecarPath
            sha256 = [string]$Result.sponzaSceneAnimationSidecarSha256
        }
    }
    if ([string]$Result.sponzaSceneAnimationFingerprint -cne
            [string]$ReportAnimation.Fingerprint -or
        -not (Test-Sha256Identity (
            [string]$Result.sponzaSceneAnimationFingerprint)) -or
        [int]$Result.sponzaSceneAnimationMode -ne 0 -or
        [int]$ReportAnimation.Mode -ne 0 -or
        [string]$Result.sponzaSceneAnimationConfigurationFingerprint -cne
            "unavailable" -or
        [string]$Result.sponzaSceneAnimationSequenceHash -cne "unavailable" -or
        -not [string]::IsNullOrEmpty(
            [string]$Result.sponzaSceneAnimationSidecarPath) -or
        -not [string]::IsNullOrEmpty(
            [string]$Result.sponzaSceneAnimationSidecarSha256)) {
        throw "$Label non-Sponza animation evidence is not canonical unavailable."
    }
    return [ordered]@{ path = ""; sha256 = "" }
}

function Assert-TimingActivationVerifierResult {
    param(
        $Workload,
        $Report,
        $Result,
        [string]$ReportPath,
        [string]$ReportSha256,
        [string]$Label)
    Assert-ResultReportIdentity $Result $ReportPath $ReportSha256 $Label
    if ([string]$Result.activation -cne [string]$Workload.activation -or
        [string]$Result.activationFingerprint -cne
            [string]$Report.ActivationEvidence.Fingerprint -or
        [string]$Result.activationStructuralSequenceHash -cne
            [string]$Report.ActivationEvidence.ActivationStructuralSequenceHash -or
        [string]$Result.activationExecutionSequenceHash -cne
            [string]$Report.ActivationEvidence.ActivationExecutionSequenceHash) {
        throw "$Label activation evidence differs from the authenticated report."
    }
    if ([string]$Workload.activation -ceq "reflection-recapture") {
        if (-not (Test-Sha256Identity `
                ([string]$Result.reflectionProbeCaptureEvidenceDigest)) -or
            [int]$Result.reflectionProbeCaptureRawRowCount -ne
                [int]$Workload.measureFrames -or
            [int]$Result.reflectionProbeCaptureResultRowCount -ne 8) {
            throw "$Label reflection C3 evidence is incomplete."
        }
    } elseif ([string]$Result.reflectionProbeCaptureEvidenceDigest -cne
            "unavailable" -or
        [int]$Result.reflectionProbeCaptureRawRowCount -ne 0 -or
        [int]$Result.reflectionProbeCaptureResultRowCount -ne 0) {
        throw "$Label non-reflection C3 evidence is not canonical unavailable."
    }
    $sidecar = Assert-SponzaAnimationVerifierIdentity `
        $Workload $Report.SponzaSceneAnimationEvidence $Result $Label
    if ([string]$Workload.scene -ceq "Sponza") {
        Assert-PathIdentity ([string]$sidecar.path) `
            (([System.IO.Path]::GetFullPath($ReportPath)) +
                ".sponza-animation.bin") `
            "$Label campaign-owned animation sidecar"
    }
    return $sidecar
}

function Assert-DdgiTransientVerifierResult {
    param(
        $Workload,
        $Report,
        $Result,
        [string]$ReportPath,
        [string]$ReportSha256,
        [string]$Label)
    Assert-ResultReportIdentity $Result $ReportPath $ReportSha256 $Label
    $reportLength = (Get-Item -LiteralPath $ReportPath).Length
    $applicable = Test-WorkloadDdgiTransientApplicable $Workload
    if ([long]$Result.reportByteLength -ne $reportLength -or
        $Result.applicable -isnot [bool] -or
        $Result.available -isnot [bool] -or
        $Result.applicable -ne $applicable -or
        $Result.available -ne $applicable -or
        [bool]$Report.DdgiTransientRawEvidence.Applicable -ne $applicable -or
        [bool]$Report.DdgiTransientEvidence.Applicable -ne $applicable -or
        -not (Test-Sha256Identity ([string]$Result.semanticDigest))) {
        throw "$Label DDGI transient applicability or report identity differs."
    }
    if ($applicable) {
        if ([int]$Result.rawRowCount -ne 240 -or
            [int]$Result.recomputedWindowCount -ne 2 -or
            [int]$Result.recomputedWindowFrameCount -le 0) {
            throw "$Label applicable DDGI transient evidence is incomplete."
        }
    } elseif ([int]$Result.rawRowCount -ne 0 -or
        [int]$Result.recomputedWindowCount -ne 0 -or
        [int]$Result.recomputedWindowFrameCount -ne 0) {
        throw "$Label DDGI transient NotApplicable result is noncanonical."
    }
}

function New-TimingFrozenVerifierEvidence {
    param(
        $Workload,
        $Report,
        [string]$ReportPath,
        $VerifierBuildIdentity,
        [string]$Label)
    $reportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportSha256 = Get-Sha256 $reportPath
    $activationArtifactPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".activation-verification.json")
    $activationInputPaths = @($reportPath)
    $activationInputSha256 = @($reportSha256)
    if ([string]$Workload.scene -ceq "Sponza") {
        $activationInputPaths += [string]$Report.SponzaSceneAnimationEvidence.SidecarPath
        $activationInputSha256 +=
            [string]$Report.SponzaSceneAnimationEvidence.SidecarSha256
    }
    $activation = New-FrozenVerifierArtifact `
        $VerifierBuildIdentity `
        @("--verify-benchmark-activation-report", $reportPath) `
        $activationInputPaths $activationInputSha256 $activationArtifactPath `
        "njulf-benchmark-activation-verification" `
        "njulf-benchmark-activation-verification/v2" `
        (Get-ActivationVerifierResultProperties) "$Label activation" 120
    $sidecar = Assert-TimingActivationVerifierResult `
        $Workload $Report $activation.result $reportPath $reportSha256 `
        "$Label activation"

    $ddgiArtifactPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".ddgi-transient-verification.json")
    $ddgi = New-FrozenVerifierArtifact `
        $VerifierBuildIdentity `
        @("--verify-benchmark-ddgi-transient-report", $reportPath) `
        @($reportPath) @($reportSha256) $ddgiArtifactPath `
        "njulf-benchmark-ddgi-transient-verification" `
        "njulf-benchmark-ddgi-transient-verification/v1" `
        (Get-DdgiTransientVerifierResultProperties) "$Label DDGI transient" 120
    Assert-DdgiTransientVerifierResult `
        $Workload $Report $ddgi.result $reportPath $reportSha256 `
        "$Label DDGI transient"
    return [ordered]@{
        activation = $activation
        ddgiTransient = $ddgi
        sponzaAnimationSidecar = $sidecar
    }
}

function Assert-TimingFrozenVerifierEvidence {
    param(
        $Workload,
        $Report,
        [string]$ReportPath,
        $Evidence,
        $VerifierBuildIdentity,
        [string]$Label)
    Assert-ExactPropertyNames $Evidence @(
        "activation", "ddgiTransient", "sponzaAnimationSidecar") `
        "$Label frozen timing evidence"
    $reportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportSha256 = Get-Sha256 $reportPath
    $activationArtifactPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".activation-verification.json")
    $activationInputPaths = @($reportPath)
    $activationInputSha256 = @($reportSha256)
    if ([string]$Workload.scene -ceq "Sponza") {
        $activationInputPaths += [string]$Report.SponzaSceneAnimationEvidence.SidecarPath
        $activationInputSha256 +=
            [string]$Report.SponzaSceneAnimationEvidence.SidecarSha256
    }
    $activationResult = Assert-FrozenVerifierArtifact `
        $Evidence.activation $VerifierBuildIdentity `
        @("--verify-benchmark-activation-report", $reportPath) `
        $activationInputPaths $activationInputSha256 $activationArtifactPath `
        "njulf-benchmark-activation-verification" `
        "njulf-benchmark-activation-verification/v2" `
        (Get-ActivationVerifierResultProperties) "$Label activation" 120
    $sidecar = Assert-TimingActivationVerifierResult `
        $Workload $Report $activationResult $reportPath $reportSha256 `
        "$Label activation"
    if (($sidecar | ConvertTo-Json -Compress) -cne
        ($Evidence.sponzaAnimationSidecar | ConvertTo-Json -Compress)) {
        throw "$Label stored common animation sidecar identity differs."
    }
    $ddgiArtifactPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".ddgi-transient-verification.json")
    $ddgiResult = Assert-FrozenVerifierArtifact `
        $Evidence.ddgiTransient $VerifierBuildIdentity `
        @("--verify-benchmark-ddgi-transient-report", $reportPath) `
        @($reportPath) @($reportSha256) $ddgiArtifactPath `
        "njulf-benchmark-ddgi-transient-verification" `
        "njulf-benchmark-ddgi-transient-verification/v1" `
        (Get-DdgiTransientVerifierResultProperties) "$Label DDGI transient" 120
    Assert-DdgiTransientVerifierResult `
        $Workload $Report $ddgiResult $reportPath $reportSha256 `
        "$Label DDGI transient"
    return $Evidence
}

function Assert-QualityActivationVerifierResult {
    param(
        $Workload,
        $Report,
        [string]$Role,
        [string]$SequenceId,
        $Result,
        [string]$ReportPath,
        [string]$ReportSha256,
        [string]$Label)
    Assert-ResultReportIdentity $Result $ReportPath $ReportSha256 $Label
    if ([string]$Result.sequenceId -cne $SequenceId -or
        [int]$Result.role -ne (Get-QualitySequenceRoleValue $Role) -or
        [string]$Result.activation -cne [string]$Workload.activation -or
        [string]$Result.activation -cne [string]$Report.Activation -or
        [string]$Result.activationFingerprint -cne
            [string]$Report.ActivationFingerprint -or
        [string]$Result.activationFingerprint -cne
            [string]$Report.ActivationEvidence.Fingerprint -or
        [string]$Result.activationStructuralSequenceHash -cne
            [string]$Report.ActivationEvidence.ActivationStructuralSequenceHash -or
        [string]$Result.activationExecutionSequenceHash -cne
            [string]$Report.ActivationEvidence.ActivationExecutionSequenceHash) {
        throw "$Label activation evidence differs from the authenticated quality report."
    }
    $sidecar = Assert-SponzaAnimationVerifierIdentity `
        $Workload $Report.SponzaSceneAnimationEvidence $Result $Label
    if ([string]$Workload.scene -ceq "Sponza") {
        Assert-PathIdentity ([string]$sidecar.path) `
            (([System.IO.Path]::GetFullPath($ReportPath)) +
                ".sponza-animation.bin") `
            "$Label campaign-owned animation sidecar"
    }
    return $sidecar
}

function New-QualityFrozenVerifierEvidence {
    param(
        $Workload,
        $Report,
        [string]$Role,
        [string]$SequenceId,
        [string]$ReportPath,
        $VerifierBuildIdentity,
        [string]$Label)
    $reportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportSha256 = Get-Sha256 $reportPath
    $artifactPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".activation-verification.json")
    $inputPaths = @($reportPath)
    $inputSha256 = @($reportSha256)
    if ([string]$Workload.scene -ceq "Sponza") {
        $inputPaths += [string]$Report.SponzaSceneAnimationEvidence.SidecarPath
        $inputSha256 +=
            [string]$Report.SponzaSceneAnimationEvidence.SidecarSha256
    }
    $activation = New-FrozenVerifierArtifact `
        $VerifierBuildIdentity `
        @("--verify-benchmark-quality-activation-report", $reportPath) `
        $inputPaths $inputSha256 $artifactPath `
        "njulf-benchmark-quality-activation-verification" `
        "njulf-benchmark-quality-activation-verification/v1" `
        (Get-QualityActivationVerifierResultProperties) `
        "$Label quality activation" 120
    $sidecar = Assert-QualityActivationVerifierResult `
        $Workload $Report $Role $SequenceId $activation.result `
        $reportPath $reportSha256 "$Label quality activation"
    return [ordered]@{
        activation = $activation
        sponzaAnimationSidecar = $sidecar
    }
}

function Assert-QualityFrozenVerifierEvidence {
    param(
        $Workload,
        $Report,
        [string]$Role,
        [string]$SequenceId,
        [string]$ReportPath,
        $Evidence,
        $VerifierBuildIdentity,
        [string]$Label)
    Assert-ExactPropertyNames $Evidence @(
        "activation", "sponzaAnimationSidecar") `
        "$Label frozen quality evidence"
    $reportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportSha256 = Get-Sha256 $reportPath
    $artifactPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".activation-verification.json")
    $inputPaths = @($reportPath)
    $inputSha256 = @($reportSha256)
    if ([string]$Workload.scene -ceq "Sponza") {
        $inputPaths += [string]$Report.SponzaSceneAnimationEvidence.SidecarPath
        $inputSha256 +=
            [string]$Report.SponzaSceneAnimationEvidence.SidecarSha256
    }
    $result = Assert-FrozenVerifierArtifact `
        $Evidence.activation $VerifierBuildIdentity `
        @("--verify-benchmark-quality-activation-report", $reportPath) `
        $inputPaths $inputSha256 $artifactPath `
        "njulf-benchmark-quality-activation-verification" `
        "njulf-benchmark-quality-activation-verification/v1" `
        (Get-QualityActivationVerifierResultProperties) `
        "$Label quality activation" 120
    $sidecar = Assert-QualityActivationVerifierResult `
        $Workload $Report $Role $SequenceId $result $reportPath `
        $reportSha256 "$Label quality activation"
    if (($sidecar | ConvertTo-Json -Compress) -cne
        ($Evidence.sponzaAnimationSidecar | ConvertTo-Json -Compress)) {
        throw "$Label stored common animation sidecar identity differs."
    }
    return $Evidence
}

function Get-ControlledIsolationComparisonProperties {
    return @(
        "kind", "schema", "passed", "controlledIsolationPairId",
        "cachedPairId", "forcedPairId", "controlledIsolationIdentityHash",
        "controlledIsolationSettingsFingerprint",
        "controlledIsolationSequenceHash", "cachedSettingsFingerprint",
        "forcedSettingsFingerprint", "trajectory", "trajectoryFingerprint",
        "trajectoryRouteHash", "sponzaSceneAnimationConfigurationFingerprint",
        "sponzaSceneAnimationSequenceHash", "activationStructuralSequenceHash",
        "cachedActivationFingerprint",
        "cachedActivationExecutionSequenceHash",
        "forcedActivationFingerprint",
        "forcedActivationExecutionSequenceHash", "buildCommit",
        "executableHash", "shaderBundleHash", "timing", "failures")
}

function Assert-ControlledIsolationVerifierResult {
    param(
        $CachedReport,
        $ForcedReport,
        $Result,
        [string]$CachedReportPath,
        [string]$CachedReportSha256,
        [string]$ForcedReportPath,
        [string]$ForcedReportSha256,
        [string]$Label)
    Assert-PathIdentity ([string]$Result.cachedReportPath) `
        $CachedReportPath "$Label cached report"
    Assert-PathIdentity ([string]$Result.forcedReportPath) `
        $ForcedReportPath "$Label forced report"
    $comparison = $Result.comparison
    Assert-ExactPropertyNames $comparison `
        (Get-ControlledIsolationComparisonProperties) "$Label comparison"
    Assert-ExactPropertyNames $comparison.timing @(
        "cachedCpuFrameP95Milliseconds", "forcedCpuFrameP95Milliseconds",
        "cpuFrameDeltaMilliseconds", "cachedGpuFrameP95Milliseconds",
        "forcedGpuFrameP95Milliseconds", "gpuFrameDeltaMilliseconds",
        "cachedDirectionalShadowP95Milliseconds",
        "forcedDirectionalShadowP95Milliseconds",
        "directionalShadowDeltaMilliseconds") "$Label timing"
    if ([string]$Result.cachedReportSha256 -cne $CachedReportSha256 -or
        [string]$Result.forcedReportSha256 -cne $ForcedReportSha256 -or
        -not (Test-Sha256Identity ([string]$Result.artifactIdentityHash)) -or
        $comparison.passed -isnot [bool] -or
        $comparison.passed -ne $true -or
        $null -eq $comparison.failures -or
        $comparison.failures -isnot [System.Object[]] -or
        $comparison.failures.Count -ne 0 -or
        [string]$comparison.kind -cne
            "njulf-benchmark-controlled-isolation" -or
        [string]$comparison.schema -cne
            "njulf-benchmark-controlled-isolation/v2" -or
        -not (Test-Sha256Identity (
            [string]$comparison.controlledIsolationPairId)) -or
        -not (Test-Sha256Identity (
            [string]$comparison.controlledIsolationIdentityHash)) -or
        -not (Test-Sha256Identity (
            [string]$comparison.controlledIsolationSettingsFingerprint)) -or
        -not (Test-Sha256Identity (
            [string]$comparison.controlledIsolationSequenceHash)) -or
        [string]$comparison.controlledIsolationIdentityHash -cne
            [string]$CachedReport.CaptureContract.ControlledIsolationIdentityHash -or
        [string]$comparison.controlledIsolationIdentityHash -cne
            [string]$ForcedReport.CaptureContract.ControlledIsolationIdentityHash -or
        [string]$comparison.controlledIsolationSettingsFingerprint -cne
            [string]$CachedReport.CaptureContract.ControlledIsolationSettingsFingerprint -or
        [string]$comparison.controlledIsolationSettingsFingerprint -cne
            [string]$ForcedReport.CaptureContract.ControlledIsolationSettingsFingerprint -or
        [string]$comparison.controlledIsolationSequenceHash -cne
            [string]$CachedReport.CaptureContract.ControlledIsolationSequenceHash -or
        [string]$comparison.controlledIsolationSequenceHash -cne
            [string]$ForcedReport.CaptureContract.ControlledIsolationSequenceHash -or
        [string]$comparison.cachedSettingsFingerprint -cne
            [string]$CachedReport.ProducerIdentity.SettingsFingerprint -or
        [string]$comparison.forcedSettingsFingerprint -cne
            [string]$ForcedReport.ProducerIdentity.SettingsFingerprint -or
        [string]$comparison.cachedPairId -cne
            [string]$CachedReport.CaptureContract.PairId -or
        [string]$comparison.forcedPairId -cne
            [string]$ForcedReport.CaptureContract.PairId -or
        [string]$comparison.cachedActivationFingerprint -cne
            [string]$CachedReport.ActivationEvidence.Fingerprint -or
        [string]$comparison.forcedActivationFingerprint -cne
            [string]$ForcedReport.ActivationEvidence.Fingerprint -or
        [string]$comparison.cachedActivationExecutionSequenceHash -cne
            [string]$CachedReport.ActivationEvidence.ActivationExecutionSequenceHash -or
        [string]$comparison.forcedActivationExecutionSequenceHash -cne
            [string]$ForcedReport.ActivationEvidence.ActivationExecutionSequenceHash -or
        [string]$comparison.activationStructuralSequenceHash -cne
            [string]$CachedReport.ActivationEvidence.ActivationStructuralSequenceHash -or
        [string]$comparison.activationStructuralSequenceHash -cne
            [string]$ForcedReport.ActivationEvidence.ActivationStructuralSequenceHash -or
        [string]$comparison.trajectory -cne "sponza-low" -or
        [string]$comparison.trajectoryFingerprint -cne
            [string]$CachedReport.CaptureContract.TrajectoryFingerprint -or
        [string]$comparison.trajectoryFingerprint -cne
            [string]$ForcedReport.CaptureContract.TrajectoryFingerprint -or
        [string]$comparison.trajectoryRouteHash -cne
            [string]$CachedReport.CaptureContract.TrajectoryRouteHash -or
        [string]$comparison.trajectoryRouteHash -cne
            [string]$ForcedReport.CaptureContract.TrajectoryRouteHash -or
        [string]$comparison.sponzaSceneAnimationConfigurationFingerprint -cne
            [string]$CachedReport.SponzaSceneAnimationEvidence.ConfigurationFingerprint -or
        [string]$comparison.sponzaSceneAnimationConfigurationFingerprint -cne
            [string]$ForcedReport.SponzaSceneAnimationEvidence.ConfigurationFingerprint -or
        [string]$comparison.sponzaSceneAnimationSequenceHash -cne
            [string]$CachedReport.SponzaSceneAnimationEvidence.SequenceHash -or
        [string]$comparison.sponzaSceneAnimationSequenceHash -cne
            [string]$ForcedReport.SponzaSceneAnimationEvidence.SequenceHash -or
        [string]$comparison.buildCommit -cne
            [string]$CachedReport.LastDiagnostics.CaptureRun.Commit -or
        [string]$comparison.buildCommit -cne
            [string]$ForcedReport.LastDiagnostics.CaptureRun.Commit -or
        [string]$comparison.executableHash -cne
            [string]$CachedReport.LastDiagnostics.CaptureRun.ExecutableHash -or
        [string]$comparison.executableHash -cne
            [string]$ForcedReport.LastDiagnostics.CaptureRun.ExecutableHash -or
        [string]$comparison.shaderBundleHash -cne
            [string]$CachedReport.LastDiagnostics.CaptureRun.ShaderBundleHash -or
        [string]$comparison.shaderBundleHash -cne
            [string]$ForcedReport.LastDiagnostics.CaptureRun.ShaderBundleHash) {
        throw "$Label controlled directional evidence differs from its reports."
    }
    foreach ($value in @(
            $comparison.timing.cachedCpuFrameP95Milliseconds,
            $comparison.timing.forcedCpuFrameP95Milliseconds,
            $comparison.timing.cpuFrameDeltaMilliseconds,
            $comparison.timing.cachedGpuFrameP95Milliseconds,
            $comparison.timing.forcedGpuFrameP95Milliseconds,
            $comparison.timing.gpuFrameDeltaMilliseconds,
            $comparison.timing.cachedDirectionalShadowP95Milliseconds,
            $comparison.timing.forcedDirectionalShadowP95Milliseconds,
            $comparison.timing.directionalShadowDeltaMilliseconds)) {
        $null = Assert-FiniteNumber $value "$Label controlled timing"
    }
    $cachedDirectional = Get-PassTiming `
        $CachedReport "DirectionalShadowPass"
    $forcedDirectional = Get-PassTiming `
        $ForcedReport "DirectionalShadowPass"
    if ([double]$comparison.timing.cachedCpuFrameP95Milliseconds -ne
            [double]$CachedReport.CpuFrameMilliseconds.P95Milliseconds -or
        [double]$comparison.timing.forcedCpuFrameP95Milliseconds -ne
            [double]$ForcedReport.CpuFrameMilliseconds.P95Milliseconds -or
        [double]$comparison.timing.cpuFrameDeltaMilliseconds -ne
            ([double]$ForcedReport.CpuFrameMilliseconds.P95Milliseconds -
             [double]$CachedReport.CpuFrameMilliseconds.P95Milliseconds) -or
        [double]$comparison.timing.cachedGpuFrameP95Milliseconds -ne
            [double]$CachedReport.GpuFrameMilliseconds.P95Milliseconds -or
        [double]$comparison.timing.forcedGpuFrameP95Milliseconds -ne
            [double]$ForcedReport.GpuFrameMilliseconds.P95Milliseconds -or
        [double]$comparison.timing.gpuFrameDeltaMilliseconds -ne
            ([double]$ForcedReport.GpuFrameMilliseconds.P95Milliseconds -
             [double]$CachedReport.GpuFrameMilliseconds.P95Milliseconds) -or
        [double]$comparison.timing.cachedDirectionalShadowP95Milliseconds -ne
            [double]$cachedDirectional -or
        [double]$comparison.timing.forcedDirectionalShadowP95Milliseconds -ne
            [double]$forcedDirectional -or
        [double]$comparison.timing.directionalShadowDeltaMilliseconds -ne
            ([double]$forcedDirectional - [double]$cachedDirectional)) {
        throw "$Label controlled timing projection differs from its reports."
    }
}

function New-ControlledIsolationFrozenVerifierEvidence {
    param(
        $CachedReport,
        $ForcedReport,
        [string]$CachedReportPath,
        [string]$ForcedReportPath,
        $VerifierBuildIdentity,
        [string]$ArtifactPath,
        [string]$Label)
    $cachedPath = [System.IO.Path]::GetFullPath($CachedReportPath)
    $forcedPath = [System.IO.Path]::GetFullPath($ForcedReportPath)
    $cachedSha = Get-Sha256 $cachedPath
    $forcedSha = Get-Sha256 $forcedPath
    $inputPaths = @($cachedPath, $forcedPath)
    $inputHashes = @($cachedSha, $forcedSha)
    foreach ($report in @($CachedReport, $ForcedReport)) {
        $inputPaths += [string]$report.SponzaSceneAnimationEvidence.SidecarPath
        $inputHashes += [string]$report.SponzaSceneAnimationEvidence.SidecarSha256
    }
    $artifact = New-FrozenVerifierArtifact `
        $VerifierBuildIdentity `
        @("--verify-directional-controlled-isolation", $cachedPath, $forcedPath) `
        $inputPaths $inputHashes $ArtifactPath `
        "njulf-benchmark-controlled-isolation-verification" `
        "njulf-benchmark-controlled-isolation-verification/v2" `
        (Get-ControlledIsolationVerifierResultProperties) $Label 120
    Assert-ControlledIsolationVerifierResult `
        $CachedReport $ForcedReport $artifact.result $cachedPath $cachedSha `
        $forcedPath $forcedSha $Label
    return $artifact
}

function Assert-ControlledIsolationFrozenVerifierEvidence {
    param(
        $CachedReport,
        $ForcedReport,
        [string]$CachedReportPath,
        [string]$ForcedReportPath,
        $Evidence,
        $VerifierBuildIdentity,
        [string]$ArtifactPath,
        [string]$Label)
    $cachedPath = [System.IO.Path]::GetFullPath($CachedReportPath)
    $forcedPath = [System.IO.Path]::GetFullPath($ForcedReportPath)
    $cachedSha = Get-Sha256 $cachedPath
    $forcedSha = Get-Sha256 $forcedPath
    $inputPaths = @($cachedPath, $forcedPath)
    $inputHashes = @($cachedSha, $forcedSha)
    foreach ($report in @($CachedReport, $ForcedReport)) {
        $inputPaths += [string]$report.SponzaSceneAnimationEvidence.SidecarPath
        $inputHashes += [string]$report.SponzaSceneAnimationEvidence.SidecarSha256
    }
    $result = Assert-FrozenVerifierArtifact `
        $Evidence $VerifierBuildIdentity `
        @("--verify-directional-controlled-isolation", $cachedPath, $forcedPath) `
        $inputPaths $inputHashes $ArtifactPath `
        "njulf-benchmark-controlled-isolation-verification" `
        "njulf-benchmark-controlled-isolation-verification/v2" `
        (Get-ControlledIsolationVerifierResultProperties) $Label 120
    Assert-ControlledIsolationVerifierResult `
        $CachedReport $ForcedReport $result $cachedPath $cachedSha `
        $forcedPath $forcedSha $Label
    return $Evidence
}

function New-ReferenceControlledIsolationEvidence {
    param(
        $ConfigurationReferences,
        $ReferenceBuild,
        [string]$ArtifactPath,
        [string]$Label)
    $cachedId = "sponza-directional-shadow-moving-caster"
    $forcedId = "sponza-directional-shadow-forced-refresh"
    $cachedEntry = $ConfigurationReferences[$cachedId]
    $forcedEntry = $ConfigurationReferences[$forcedId]
    $cachedReport = Read-BenchmarkReport ([string]$cachedEntry.reportPath)
    $forcedReport = Read-BenchmarkReport ([string]$forcedEntry.reportPath)
    $artifact = New-ControlledIsolationFrozenVerifierEvidence `
        $cachedReport $forcedReport ([string]$cachedEntry.reportPath) `
        ([string]$forcedEntry.reportPath) $ReferenceBuild $ArtifactPath $Label
    return [ordered]@{
        schema = "njulf-perf-campaign-controlled-isolation-evidence/v1"
        cachedWorkloadId = $cachedId
        forcedWorkloadId = $forcedId
        artifact = $artifact
    }
}

function Assert-ReferenceControlledIsolationEvidence {
    param(
        $ConfigurationReferences,
        $Evidence,
        $ReferenceBuild,
        [string]$ExpectedArtifactPath,
        [string]$Label)
    Assert-ExactPropertyNames $Evidence @(
        "schema", "cachedWorkloadId", "forcedWorkloadId", "artifact") `
        "$Label envelope"
    $cachedId = "sponza-directional-shadow-moving-caster"
    $forcedId = "sponza-directional-shadow-forced-refresh"
    if ([string]$Evidence.schema -cne
            "njulf-perf-campaign-controlled-isolation-evidence/v1" -or
        [string]$Evidence.cachedWorkloadId -cne $cachedId -or
        [string]$Evidence.forcedWorkloadId -cne $forcedId) {
        throw "$Label controlled-isolation topology differs."
    }
    $cachedProperty = $ConfigurationReferences.PSObject.Properties[$cachedId]
    $cachedEntry = if ($null -eq $cachedProperty) {
        $ConfigurationReferences[$cachedId]
    } else { $cachedProperty.Value }
    $forcedProperty = $ConfigurationReferences.PSObject.Properties[$forcedId]
    $forcedEntry = if ($null -eq $forcedProperty) {
        $ConfigurationReferences[$forcedId]
    } else { $forcedProperty.Value }
    $cachedReport = Read-BenchmarkReport ([string]$cachedEntry.reportPath)
    $forcedReport = Read-BenchmarkReport ([string]$forcedEntry.reportPath)
    $null = Assert-ControlledIsolationFrozenVerifierEvidence `
        $cachedReport $forcedReport ([string]$cachedEntry.reportPath) `
        ([string]$forcedEntry.reportPath) $Evidence.artifact $ReferenceBuild `
        $ExpectedArtifactPath $Label
    return $Evidence
}

function Invoke-Git {
    param([string[]]$Arguments)
    $output = & git -C $script:SolutionRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed.`n$($output -join "`n")"
    }
    return @($output)
}

function Get-GitText {
    param([string[]]$Arguments)
    return ((Invoke-Git $Arguments) -join "`n").Trim()
}

function Get-StablePatchId {
    param([string]$Commit)
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') {
        throw "Stable patch identity requires an exact lowercase commit."
    }
    $null = Invoke-Git @("cat-file", "-e", "$Commit^{commit}")
    Push-Location $script:SolutionRoot
    try {
        $output = & git show --pretty=format: --binary $Commit |
            & git patch-id --stable
        if ($LASTEXITCODE -ne 0) {
            throw "Could not compute stable patch identity for $Commit."
        }
    } finally {
        Pop-Location
    }
    $fields = @(([string]$output).Trim() -split '\s+')
    if ($fields.Count -lt 1 -or $fields[0] -cnotmatch '^[0-9a-f]{40}$') {
        throw "Stable patch identity for $Commit is malformed."
    }
    return [string]$fields[0]
}

function Get-CommitChangedPaths {
    param([string]$Commit)
    $prefix = ([string]$script:SolutionRelativePath).Replace("\\", "/").TrimEnd("/") + "/"
    return @((Get-GitText @(
        "diff-tree", "--no-commit-id", "--name-only", "-r", "--no-renames",
        $Commit)) -split "`n" | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        } | ForEach-Object {
            $path = ([string]$_).Replace("\\", "/")
            if (-not $path.StartsWith($prefix, [StringComparison]::Ordinal)) {
                throw "Candidate commit changes path outside the solution: $path"
            }
            $path.Substring($prefix.Length)
        })
}

function Invoke-GitUpdateRefTransaction {
    param([string[]]$Commands, [string]$Label)
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = "git"
    $info.WorkingDirectory = $script:SolutionRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    [void]$info.ArgumentList.Add("-C")
    [void]$info.ArgumentList.Add($script:SolutionRoot)
    [void]$info.ArgumentList.Add("update-ref")
    [void]$info.ArgumentList.Add("--no-deref")
    [void]$info.ArgumentList.Add("--stdin")
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) {
        throw "$Label failed to start git update-ref."
    }
    $process.StandardInput.Write(($Commands -join "`n") + "`n")
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "$Label failed (git exit $($process.ExitCode)).`n$stdout`n$stderr"
    }
}

function Initialize-CampaignRepositoryRoot {
    $resolved = Get-GitText @("rev-parse", "--show-toplevel")
    $fullRoot = [System.IO.Path]::GetFullPath($resolved)
    $relativeSolution = [System.IO.Path]::GetRelativePath(
        $fullRoot,
        $script:SolutionRoot)
    if ($relativeSolution -eq ".." -or
        $relativeSolution.StartsWith(
            "..$([System.IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::Ordinal)) {
        throw "Solution root '$script:SolutionRoot' is outside git worktree '$fullRoot'."
    }
    $script:RepoRoot = $fullRoot
    $script:SolutionRelativePath = $relativeSolution
    $branch = Get-GitText @("symbolic-ref", "--quiet", "--short", "HEAD")
    Assert-Text $branch "Campaign branch"
    $script:CampaignBranch = $branch
    $gitInfoExclude = Get-GitText @("rev-parse", "--git-path", "info/exclude")
    $script:GitInfoExcludePath = if (
        [System.IO.Path]::IsPathRooted($gitInfoExclude)) {
        [System.IO.Path]::GetFullPath($gitInfoExclude)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $script:SolutionRoot $gitInfoExclude))
    }
    $script:GitInfoExcludeFingerprint =
        Get-CanonicalPathFingerprint $script:GitInfoExcludePath
}

function Test-PathContainedBy {
    param([string]$Path, [string]$Container)
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullContainer = [System.IO.Path]::GetFullPath($Container).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    return [string]::Equals(
            $fullPath,
            $fullContainer,
            [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith(
            $fullContainer + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CampaignPathTopology {
    param($Manifest)
    $null = Assert-NoLinkedPathComponents `
        $script:RepoRoot "Campaign repository root"
    $null = Assert-NoLinkedPathComponents `
        $script:SolutionRoot "Campaign solution root"
    $null = Assert-NoLinkedPathComponents `
        $script:ManifestFile "Active campaign manifest"
    $null = Assert-NoLinkedPathComponents `
        $script:RunRoot "Campaign run root"
    if (Test-Path -LiteralPath $script:RunRoot -PathType Leaf) {
        throw "Campaign run root is a file: $script:RunRoot"
    }
    if ((Test-PathContainedBy $script:RepoRoot $script:RunRoot) -or
        (Test-PathContainedBy $script:SolutionRoot $script:RunRoot)) {
        throw "Campaign run root may not contain the repository or solution root."
    }
    if (Test-PathContainedBy $script:ManifestFile $script:RunRoot) {
        throw "Active campaign manifest must be physically outside the mutable run root."
    }
    $advisoryExceptions = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @(
            ".perf-loop-runs/campaign/beauty-target/manifest.json",
            ".perf-loop-runs/campaign/beauty-target/bistro-beauty-target.jpg")) {
        [void]$advisoryExceptions.Add((Resolve-SolutionPath $path))
    }
    foreach ($protectedPath in @($Manifest.protectedPaths)) {
        $fullProtected = Assert-NoLinkedPathComponents `
            (Resolve-SolutionPath ([string]$protectedPath)) `
            "Protected path '$protectedPath'"
        if ((Test-PathContainedBy $fullProtected $script:RunRoot) -and
            -not $advisoryExceptions.Contains($fullProtected)) {
            throw "Protected path '$protectedPath' may not be contained by the mutable run root."
        }
        if ((Test-Path -LiteralPath $fullProtected -PathType Container) -and
            (Test-PathContainedBy $script:RunRoot $fullProtected)) {
            throw "Campaign run root may not be contained by protected directory '$protectedPath'."
        }
    }
}

function Assert-CampaignRepositoryRoot {
    Assert-CampaignWorktreeRoot
    $branch = Get-GitText @("symbolic-ref", "--quiet", "--short", "HEAD")
    if (-not [string]::Equals(
            $branch,
            $script:CampaignBranch,
            [StringComparison]::Ordinal)) {
        throw "Campaign branch changed to '$branch'; expected '$script:CampaignBranch'."
    }
}

function Assert-CampaignWorktreeRoot {
    Assert-Text $script:RepoRoot "Campaign repository root"
    $resolved = [System.IO.Path]::GetFullPath(
        (Get-GitText @("rev-parse", "--show-toplevel")))
    if (-not [string]::Equals(
            $resolved,
            $script:RepoRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing mutation outside campaign worktree '$script:RepoRoot': $resolved"
    }
}

function Assert-CleanCampaignWorktree {
    $status = Get-GitText @("status", "--porcelain=v1", "--untracked-files=all")
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "Campaign worktree must be clean before build/capture.`n$status"
    }
}

function Assert-ExactCampaignHead {
    param([string]$ExpectedCommit, [string]$Label)
    Assert-CampaignRepositoryRoot
    $actualCommit = Get-GitText @("rev-parse", "HEAD")
    if (-not [string]::Equals(
            $actualCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label HEAD changed to '$actualCommit'; expected '$ExpectedCommit'."
    }
}

function Invoke-BuildOutput {
    param(
        $Manifest,
        [string]$Configuration,
        [string]$OutputPath,
        [string]$Label,
        [string]$ExpectedCommit,
        [string]$FocusedTestFilter = "")
    if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw "$Label requires an exact lowercase commit identity."
    }
    Assert-ExactCampaignHead $ExpectedCommit "$Label pre-build"
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
    $fullOutputPath = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($OutputPath)) "$Label output"
    if (-not (Test-PathContainedBy $fullOutputPath $script:RunRoot)) {
        throw "$Label output must be contained by the admitted campaign run root."
    }
    if (Test-Path -LiteralPath $fullOutputPath) {
        throw "$Label output already exists; choose a fresh campaign run directory: $fullOutputPath"
    }
    New-Item -ItemType Directory -Path $fullOutputPath | Out-Null
    $isolatedParent = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "njulf-perf-campaign-build-worktrees"
    $isolatedRoot = Join-Path `
        $isolatedParent ([Guid]::NewGuid().ToString("N"))
    $null = Assert-NoLinkedPathComponents $isolatedRoot "$Label isolated source"
    if (Test-Path -LiteralPath $isolatedRoot) {
        throw "$Label isolated source unexpectedly exists: $isolatedRoot"
    }
    $worktreeAdded = $false
    try {
        $null = Invoke-Git @(
            "worktree", "add", "--detach", $isolatedRoot, $ExpectedCommit)
        $worktreeAdded = $true
        $isolatedHead = Get-GitText @(
            "-C", $isolatedRoot, "rev-parse", "HEAD")
        if ($isolatedHead -cne $ExpectedCommit) {
            throw "$Label isolated source checked out '$isolatedHead', expected '$ExpectedCommit'."
        }
        $isolatedSolution = Join-Path `
            $isolatedRoot $script:SolutionRelativePath
        $isolatedProject = Join-Path `
            $isolatedSolution ([string]$Manifest.projectPath)
        $isolatedProps = Join-Path $isolatedSolution "Directory.Build.props"
        $artifactRoot = Join-Path $isolatedRoot ".campaign-build-artifacts"
        if (-not (Test-Path -LiteralPath $isolatedProject -PathType Leaf) -or
            -not (Test-Path -LiteralPath $isolatedProps -PathType Leaf) -or
            (Test-Path -LiteralPath $artifactRoot)) {
            throw "$Label isolated compile graph is incomplete or already contaminated."
        }
        Invoke-ProcessChecked `
            "dotnet" `
            @(
                "build", $isolatedProject,
                "-c", $Configuration,
                "-o", $fullOutputPath,
                "--artifacts-path", $artifactRoot,
                "--no-incremental",
                "--nologo",
                "-p:RestoreLockedMode=true",
                "-p:UseSharedCompilation=false",
                "-p:ImportDirectoryBuildTargets=false",
                "-p:DirectoryBuildPropsPath=$isolatedProps",
                "-nodeReuse:false") `
            $Label `
            1800 `
            $isolatedSolution
        if (-not [string]::IsNullOrWhiteSpace($FocusedTestFilter)) {
            $isolatedTests = Join-Path `
                $isolatedSolution "Njulf.Tests/Njulf.Tests.csproj"
            Invoke-ProcessChecked `
                "dotnet" `
                @(
                    "test", $isolatedTests,
                    "-c", "Release",
                    "--artifacts-path", $artifactRoot,
                    "--nologo",
                    "--filter", $FocusedTestFilter,
                    "--logger", "console;verbosity=minimal",
                    "-p:RestoreLockedMode=true",
                    "-p:UseSharedCompilation=false",
                    "-p:ImportDirectoryBuildTargets=false",
                    "-p:DirectoryBuildPropsPath=$isolatedProps",
                    "-nodeReuse:false") `
                "$Label focused candidate tests" `
                3600 `
                $isolatedSolution
        }
    } finally {
        if ($worktreeAdded) {
            $null = Invoke-Git @(
                "worktree", "remove", "--force", $isolatedRoot)
            $null = Invoke-Git @("worktree", "prune")
        }
        if (Test-Path -LiteralPath $isolatedRoot) {
            throw "$Label isolated source did not cleanly disappear: $isolatedRoot"
        }
    }
    $executable = Join-Path $fullOutputPath "NjulfHelloGame.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "$Label did not produce $executable."
    }
    $cookedAssetStaging = Install-CookedAssetBundle `
        $script:CookedAssetBundle $fullOutputPath $Label
    Assert-CookedAssetStaging `
        $cookedAssetStaging $fullOutputPath $Label
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        Assert-ExactCampaignHead $ExpectedCommit "$Label post-build"
        Assert-CleanCampaignWorktree
    }
    return [pscustomobject]@{
        RootPath = $fullOutputPath
        ExecutablePath = [System.IO.Path]::GetFullPath($executable)
        ExecutableFileSha256 = Get-Sha256 $executable
        RuntimeExecutableBundleHash = Get-RuntimeExecutableBundleHash $executable
        BundleFingerprint = Get-BuildBundleFingerprint $fullOutputPath
        CookedAssetBundle = $cookedAssetStaging
        BuildCommit = $ExpectedCommit
        ProjectPath = [string]$Manifest.projectPath
        SourceProvenance = "git-worktree-exact-commit"
        IntermediateIsolation = "dotnet-artifacts-path"
    }
}

function Assert-BuildIdentity {
    param($BuildIdentity, [string]$Label)
    $cookedAssetsValid = $true
    try {
        Assert-CookedAssetStaging `
            $BuildIdentity.CookedAssetBundle `
            ([string]$BuildIdentity.RootPath) `
            $Label
    } catch {
        $cookedAssetsValid = $false
    }
    if ($null -eq $BuildIdentity -or
        -not (Test-Path -LiteralPath ([string]$BuildIdentity.RootPath) -PathType Container) -or
        -not (Test-Path -LiteralPath ([string]$BuildIdentity.ExecutablePath) -PathType Leaf) -or
        -not [string]::Equals(
            (Get-Sha256 ([string]$BuildIdentity.ExecutablePath)),
            [string]$BuildIdentity.ExecutableFileSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            (Get-BuildBundleFingerprint ([string]$BuildIdentity.RootPath)),
            [string]$BuildIdentity.BundleFingerprint,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            (Get-RuntimeExecutableBundleHash (
                [string]$BuildIdentity.ExecutablePath)),
            [string]$BuildIdentity.RuntimeExecutableBundleHash,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$BuildIdentity.BuildCommit -cnotmatch '^[0-9a-f]{40}$' -or
        [string]$BuildIdentity.ProjectPath -cne
            "NjulfHelloGame/NjulfHelloGame.csproj" -or
        [string]$BuildIdentity.SourceProvenance -cne
            "git-worktree-exact-commit" -or
        [string]$BuildIdentity.IntermediateIsolation -cne
            "dotnet-artifacts-path" -or
        -not $cookedAssetsValid) {
        throw "$Label build bundle changed after its fresh build lock."
    }
}

function Write-QualityContract {
    param($Manifest, $Workload)
    $directory = Join-Path $script:RunRoot "quality-contracts"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $path = Join-Path $directory ("{0}.json" -f [string]$Workload.id)
    if (Test-Path -LiteralPath $path) {
        throw "Quality contract already exists; use a fresh campaign run directory: $path"
    }
    $rois = @((Get-PropertyValue $Workload "qualityRois" @()) | ForEach-Object {
        [ordered]@{
            name = [string]$_.name
            x = [int]$_.x
            y = [int]$_.y
            width = [int]$_.width
            height = [int]$_.height
            maximumMeanLuminanceShift = [double]$Manifest.quality.maximumRoiMeanLuminanceShift
            maximumP95LuminanceShift = [double]$Manifest.quality.maximumRoiP95LuminanceShift
        }
    })
    if ($rois.Count -eq 0) {
        $rois = @([ordered]@{
            name = "$($Workload.id)-full-frame"
            x = 0
            y = 0
            width = 1920
            height = 1080
            maximumMeanLuminanceShift = [double]$Manifest.quality.maximumRoiMeanLuminanceShift
            maximumP95LuminanceShift = [double]$Manifest.quality.maximumRoiP95LuminanceShift
        })
    }
    $payload = [ordered]@{
        schema = "njulf-benchmark-hdr-quality/v1"
        width = 1920
        height = 1080
        rois = $rois
    }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Get-ReferencePath {
    param([string]$Configuration, $Workload)
    return Join-Path $script:RunRoot ("references/{0}/{1}/reference.hdr.pfm" -f $Configuration, [string]$Workload.id)
}

function Get-CampaignConfigurations {
    param($Manifest)
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($configuration in @(
            [string]$Manifest.iterationConfiguration) +
            @($Manifest.finalConfigurations | ForEach-Object { [string]$_ })) {
        if (-not $result.Contains($configuration)) {
            $result.Add($configuration)
        }
    }
    return @($result)
}

function Get-ReferenceLockEntry {
    param($Lock, [string]$Configuration, [string]$WorkloadId)
    $configurationProperty =
        $Lock.references.PSObject.Properties[$Configuration]
    if ($null -eq $configurationProperty) {
        throw "Campaign lock has no '$Configuration' references."
    }
    $workloadProperty =
        $configurationProperty.Value.PSObject.Properties[$WorkloadId]
    if ($null -eq $workloadProperty -or $null -eq $workloadProperty.Value) {
        throw "Campaign lock has no '$Configuration/$WorkloadId' reference."
    }
    return $workloadProperty.Value
}

function Get-ReferenceBuildIdentity {
    param($Lock, [string]$Configuration)
    $property = $Lock.referenceBuilds.PSObject.Properties[$Configuration]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "Campaign lock has no '$Configuration' reference build."
    }
    return $property.Value
}

function Get-QualitySequenceTrajectoryFrameCount {
    param([string]$Trajectory)
    switch ($Trajectory) {
        "bistro-loop" { return 240 }
        "sponza-horizontal" { return 300 }
        "sponza-vertical" { return 960 }
        default { return 1 }
    }
}

function Get-QualitySequenceCheckpointIndices {
    param([string]$Trajectory)
    switch ($Trajectory) {
        "bistro-loop" {
            return @(0, 59, 60, 61, 68, 76, 179, 180, 181, 239)
        }
        "sponza-horizontal" {
            return @(0, 1, 118, 119, 120, 121, 178, 179, 180, 181, 298, 299)
        }
        "sponza-vertical" {
            return @(0, 1, 239, 240, 479, 480, 719, 720, 958, 959)
        }
        default { return @(0) }
    }
}

function Get-QualitySequenceTemporalPairs {
    param([string]$Trajectory)
    $indices = @(Get-QualitySequenceCheckpointIndices $Trajectory)
    $pairs = @()
    for ($index = 1; $index -lt $indices.Count; $index++) {
        if ([int]$indices[$index] -eq ([int]$indices[$index - 1] + 1)) {
            $pairs += [pscustomobject]@{
                fromRouteFrameIndex = [int]$indices[$index - 1]
                toRouteFrameIndex = [int]$indices[$index]
            }
        }
    }
    return @($pairs)
}

function Get-QualitySequenceCheckpointFingerprint {
    param([string]$Trajectory)
    $frameCount = Get-QualitySequenceTrajectoryFrameCount $Trajectory
    $indices = @(Get-QualitySequenceCheckpointIndices $Trajectory)
    $builder = [System.Text.StringBuilder]::new(
        "njulf-benchmark-quality-checkpoints/v1|")
    [void]$builder.Append($Trajectory).Append('|')
    [void]$builder.Append($frameCount.ToString(
        [Globalization.CultureInfo]::InvariantCulture)).Append('|')
    [void]$builder.Append(($indices -join ',')).Append('|')
    foreach ($pair in @(Get-QualitySequenceTemporalPairs $Trajectory)) {
        [void]$builder.Append([int]$pair.fromRouteFrameIndex)
            .Append("->")
            .Append([int]$pair.toRouteFrameIndex)
            .Append(',')
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    return "sha256:" + [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-QualitySequenceRoleName {
    param([int]$Role)
    switch ($Role) {
        0 { return "canonical" }
        1 { return "repeat" }
        2 { return "candidate" }
        default { throw "Unknown quality-sequence role '$Role'." }
    }
}

function Get-QualitySequenceRoleValue {
    param([string]$Role)
    switch ($Role.ToLowerInvariant()) {
        "canonical" { return 0 }
        "repeat" { return 1 }
        "candidate" { return 2 }
        default { throw "Unknown quality-sequence role '$Role'." }
    }
}

function Get-QualitySequenceId {
    param(
        $Manifest,
        [string]$Configuration,
        [string]$WorkloadId,
        [string]$Role,
        [string]$Stage,
        [string]$Commit,
        [int]$Ordinal)
    Assert-Text $Stage "Quality-sequence stage"
    if ($Commit -notmatch '^[0-9a-f]{40}$') {
        throw "Quality-sequence commit is not canonical."
    }
    return (
        "$($Manifest.campaignId)-$Configuration-$WorkloadId-" +
        "$Stage-$Role-$Commit-$Ordinal")
}

function Get-QualitySequenceArguments {
    param(
        $Manifest,
        $Workload,
        [string]$Role,
        [string]$SequenceId,
        [string]$ReportPath,
        [string]$HealthPath,
        [string]$OutputDirectory,
        [string]$ReferenceContractPath,
        [string]$QualityContractPath)
    $arguments = @(
        "--benchmark-quality-sequence=true",
        "--benchmark-quality-sequence-role", $Role,
        "--benchmark-quality-sequence-id", $SequenceId,
        "--benchmark-quality-sequence-report", $ReportPath,
        "--benchmark-quality-sequence-output-dir", $OutputDirectory,
        "--benchmark-quality-sequence-warmup-frames", ([int]$Workload.warmupFrames).ToString(),
        "--benchmark-quality-sequence-max-settle-frames", ([int]$Manifest.capture.maximumSettlingFrames).ToString(),
        "--benchmark-quality-sequence-max-drain-frames", ([int]$Manifest.qualitySequence.maximumReadbackDrainFrames).ToString(),
        "--benchmark-quality-sequence-budget-profile", ([string]$Manifest.capture.budgetProfile),
        "--benchmark-quality-sequence-variant", ([string]$Workload.captureVariant),
        "--benchmark-quality-sequence-trajectory", ([string]$Workload.qualityTrajectory),
        "--benchmark-activation", ([string]$Workload.activation),
        "--health-report", $HealthPath,
        "--scene", ([string]$Workload.scene),
        "--performance-scenario", ([string]$Workload.scenario),
        "--quality-preset", "ddgi-high",
        "--validation", "off",
        "--gpu-timing")
    $bistroVariant = [string](Get-PropertyValue $Workload "bistroQualityVariant" "")
    if (-not [string]::IsNullOrWhiteSpace($bistroVariant)) {
        $arguments += @("--bistro-quality-variant", $bistroVariant)
    }
    foreach ($argument in @((Get-PropertyValue $Workload "arguments" @()))) {
        $arguments += [string]$argument
    }
    if ($Role -ne "canonical") {
        $arguments += @(
            "--benchmark-quality-sequence-reference-contract", $ReferenceContractPath,
            "--benchmark-quality-sequence-hdr-quality-contract", $QualityContractPath,
            "--benchmark-quality-sequence-hdr-max-relative-rmse", ([double]$Manifest.quality.maximumRelativeRmse).ToString([Globalization.CultureInfo]::InvariantCulture),
            "--benchmark-quality-sequence-hdr-max-flip-p95", ([double]$Manifest.quality.maximumFlipP95).ToString([Globalization.CultureInfo]::InvariantCulture))
    }
    return $arguments
}

function Read-QualitySequenceReport {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Quality-sequence report was not written: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw |
        ConvertFrom-Json -DateKind String
}

function Assert-CanonicalSha256 {
    param([string]$Value, [string]$Label, [bool]$Identity = $false)
    $pattern = if ($Identity) {
        '^sha256:[0-9a-f]{64}$'
    } else {
        '^[0-9a-f]{64}$'
    }
    if ($Value -cnotmatch $pattern) {
        throw "$Label is not a canonical SHA-256 value."
    }
}

function Assert-FiniteNumber {
    param($Value, [string]$Label, [double]$Minimum = 0.0)
    if ($null -eq $Value -or $Value -is [bool] -or
        $Value -isnot [byte] -and
        $Value -isnot [sbyte] -and
        $Value -isnot [int16] -and
        $Value -isnot [uint16] -and
        $Value -isnot [int32] -and
        $Value -isnot [uint32] -and
        $Value -isnot [int64] -and
        $Value -isnot [uint64] -and
        $Value -isnot [single] -and
        $Value -isnot [double] -and
        $Value -isnot [decimal]) {
        throw "$Label is absent or is not a JSON number."
    }
    $number = [double]$Value
    if (-not [double]::IsFinite($number) -or $number -lt $Minimum) {
        throw "$Label is non-finite or outside its admitted domain."
    }
    return $number
}

function Assert-QualitySequenceProducer {
    param($Producer, [string]$ExpectedCommit, [string]$Label)
    if ($null -eq $Producer -or
        [string]$Producer.Schema -ne "material-gi-producer-identity/v1" -or
        -not [string]::Equals(
            [string]$Producer.BuildCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Producer.ShaderFingerprint -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$Producer.SettingsFingerprint -cnotmatch '^[0-9a-f]{64}$' -or
        -not (Test-CanonicalIdentityText ([string]$Producer.GpuName)) -or
        -not (Test-CanonicalIdentityText ([string]$Producer.DriverVersion)) -or
        [string]$Producer.QualityTier -ne "StressUnlimited") {
        throw "$Label producer identity is unavailable or inconsistent."
    }
    $sources = @($Producer.SourceSettingsFingerprints)
    if ($sources.Count -ne 1 -or
        [string]$sources[0] -cne [string]$Producer.SettingsFingerprint) {
        throw "$Label producer settings sources are invalid."
    }
}

function Assert-QualitySequenceCaptureRun {
    param(
        $CaptureRun,
        $BuildIdentity,
        $Workload,
        [string]$Configuration,
        [string]$ExpectedCommit,
        [string]$Label)
    if ($null -eq $CaptureRun -or
        -not [string]::Equals(
            [string]$CaptureRun.SceneKind,
            [string]$Workload.scene,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$CaptureRun.Scenario,
            [string]$Workload.scenario,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            ([string]$CaptureRun.BuildConfiguration).Split(';', 2)[0].Trim(),
            $Configuration,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-CanonicalIdentityText ([string]$CaptureRun.ApplicationVersion)) -or
        -not [string]::Equals(
            [string]$CaptureRun.Commit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Sha256Identity ([string]$CaptureRun.ShaderBundleHash)) -or
        -not [string]::Equals(
            [string]$CaptureRun.ExecutableHash,
            [string]$BuildIdentity.RuntimeExecutableBundleHash,
            [StringComparison]::OrdinalIgnoreCase) -or
        [int]$CaptureRun.SettingsSchemaVersion -le 0 -or
        [string]$CaptureRun.DirtyWorktreeState -cne "clean") {
        throw "$Label CaptureRun differs from the frozen build/workload."
    }
}

function Assert-QualityCaptureRunEqual {
    param($Actual, $Expected, [bool]$CrossBuild, [string]$Label)
    $pairs = @(
        @("scene", [string]$Actual.SceneKind, [string]$Expected.SceneKind),
        @("scenario", [string]$Actual.Scenario, [string]$Expected.Scenario),
        @("build", [string]$Actual.BuildConfiguration, [string]$Expected.BuildConfiguration),
        @("settings schema", [string]$Actual.SettingsSchemaVersion, [string]$Expected.SettingsSchemaVersion))
    if (-not $CrossBuild) {
        $pairs += @(
            ,@("application", [string]$Actual.ApplicationVersion, [string]$Expected.ApplicationVersion),
            ,@("commit", [string]$Actual.Commit, [string]$Expected.Commit),
            ,@("shader", [string]$Actual.ShaderBundleHash, [string]$Expected.ShaderBundleHash),
            ,@("executable", [string]$Actual.ExecutableHash, [string]$Expected.ExecutableHash),
            ,@("dirty state", [string]$Actual.DirtyWorktreeState, [string]$Expected.DirtyWorktreeState))
    }
    foreach ($pair in $pairs) {
        if (-not [string]::Equals(
                [string]$pair[1],
                [string]$pair[2],
                [StringComparison]::Ordinal)) {
            throw "$Label CaptureRun $($pair[0]) differs."
        }
    }
}

function Assert-QualityProducerEqual {
    param($Actual, $Expected, [bool]$CrossBuild, [string]$Label)
    $pairs = @(
        @("schema", [string]$Actual.Schema, [string]$Expected.Schema),
        @("settings", [string]$Actual.SettingsFingerprint, [string]$Expected.SettingsFingerprint),
        @("GPU", [string]$Actual.GpuName, [string]$Expected.GpuName),
        @("driver", [string]$Actual.DriverVersion, [string]$Expected.DriverVersion),
        @("tier", [string]$Actual.QualityTier, [string]$Expected.QualityTier),
        @("settings sources", (@($Actual.SourceSettingsFingerprints) -join "`n"), (@($Expected.SourceSettingsFingerprints) -join "`n")))
    if (-not $CrossBuild) {
        $pairs += @(
            ,@("commit", [string]$Actual.BuildCommit, [string]$Expected.BuildCommit),
            ,@("shader", [string]$Actual.ShaderFingerprint, [string]$Expected.ShaderFingerprint))
    }
    foreach ($pair in $pairs) {
        if (-not [string]::Equals(
                [string]$pair[1],
                [string]$pair[2],
                [StringComparison]::Ordinal)) {
            throw "$Label producer $($pair[0]) differs."
        }
    }
}

function Assert-QualityCameraEqual {
    param($Actual, $Expected, [string]$Label)
    $pairs = @(
        @("position X", [string]$Actual.PositionX, [string]$Expected.PositionX),
        @("position Y", [string]$Actual.PositionY, [string]$Expected.PositionY),
        @("position Z", [string]$Actual.PositionZ, [string]$Expected.PositionZ),
        @("yaw", [string]$Actual.YawRadians, [string]$Expected.YawRadians),
        @("pitch", [string]$Actual.PitchRadians, [string]$Expected.PitchRadians),
        @("FOV", [string]$Actual.FieldOfViewRadians, [string]$Expected.FieldOfViewRadians),
        @("near", [string]$Actual.NearPlane, [string]$Expected.NearPlane),
        @("far", [string]$Actual.FarPlane, [string]$Expected.FarPlane),
        @("view hash", [string]$Actual.ViewHash, [string]$Expected.ViewHash),
        @("projection hash", [string]$Actual.ProjectionHash, [string]$Expected.ProjectionHash))
    foreach ($pair in $pairs) {
        if (-not [string]::Equals(
                [string]$pair[1],
                [string]$pair[2],
                [StringComparison]::Ordinal)) {
            throw "$Label camera $($pair[0]) differs."
        }
    }
}

function Assert-QualitySequenceReport {
    param(
        $Manifest,
        $Workload,
        $Report,
        $BuildIdentity,
        [string]$Configuration,
        [string]$Role,
        [string]$SequenceId,
        [string]$ExpectedCommit,
        [string]$ReportPath,
        [string]$OutputDirectory,
        [string]$ReferenceContractPath,
        [string]$ExpectedReferenceContractSha256,
        [string]$QualityContractPath,
        [string]$ExpectedQualityContractSha256,
        $ReferenceContract,
        [string]$Label)
    if ([string]$Report.Kind -ne
            "njulf-renderer-benchmark-quality-sequence" -or
        [string]$Report.Schema -ne
            "njulf-renderer-benchmark-quality-sequence/v1" -or
        [string]$Report.Kind -eq "njulf-renderer-benchmark" -or
        [bool]$Report.TimingEligible -or
        [bool]$Report.ProductionTiming) {
        throw "$Label is not a timing-ineligible quality-sequence report."
    }
    $roleValue = Get-QualitySequenceRoleValue $Role
    $expectedFrameCount = Get-QualitySequenceTrajectoryFrameCount (
        [string]$Workload.qualityTrajectory)
    $expectedIndices = @(Get-QualitySequenceCheckpointIndices (
        [string]$Workload.qualityTrajectory))
    if ([int]$Report.Role -ne $roleValue -or
        [string]$Report.SequenceId -cne $SequenceId -or
        [string]$Report.SceneKind -cne [string]$Workload.scene -or
        [string]$Report.Scenario -cne [string]$Workload.scenario -or
        [string]$Report.CaptureVariant -cne [string]$Workload.captureVariant -or
        [string]$Report.Activation -cne [string]$Workload.activation -or
        [string]$Report.ActivationEvidence.Activation -cne
            [string]$Workload.activation -or
        [string]$Report.ActivationFingerprint -cne
            [string]$Report.ActivationEvidence.Fingerprint -or
        -not (Test-Sha256Identity ([string]$Report.ActivationFingerprint)) -or
        -not [bool]$Report.ActivationEvidence.Passed -or
        [int]$Report.ActivationEvidence.MeasuredSampleCount -ne
            $expectedFrameCount -or
        (Get-ItemCount $Report.ActivationEvidence.Failures) -ne 0 -or
        [string]$Report.Trajectory -cne [string]$Workload.qualityTrajectory -or
        [int]$Report.TrajectoryFrameCount -ne $expectedFrameCount -or
        [int]$Report.WarmupFrameCount -ne [int]$Workload.warmupFrames -or
        [int]$Report.MaximumAdditionalSettlingFrameCount -ne
            [int]$Manifest.capture.maximumSettlingFrames -or
        [int]$Report.MaximumReadbackDrainFrameCount -ne
            [int]$Manifest.qualitySequence.maximumReadbackDrainFrames -or
        [string]$Report.BuildConfiguration -cne
            [string]$Report.CaptureRun.BuildConfiguration -or
        -not [bool]$Report.Passed -or
        (Get-ItemCount $Report.Failures) -ne 0 -or
        [bool]$Report.SettlingWaitTimedOut) {
        throw "$Label quality-sequence envelope failed."
    }
    $settling = [int]$Report.AdditionalSettlingFrameCount
    $first = [int]$Report.FirstRouteAbsoluteFrameIndex
    $startDelta = $first - [int]$Workload.warmupFrames
    if ($settling -lt 0 -or
        $settling -gt [int]$Manifest.capture.maximumSettlingFrames -or
        $startDelta -lt $settling -or
        $startDelta -gt ($settling + 1)) {
        throw "$Label has incoherent quality warmup/settling/route indices."
    }
    foreach ($identity in @(
            [string]$Report.TrajectoryFingerprint,
            [string]$Report.TrajectoryRouteHash,
            [string]$Report.TrajectorySequenceHash)) {
        if (-not (Test-Sha256Identity $identity)) {
            throw "$Label lacks a canonical trajectory identity."
        }
    }
    if ([string]$Report.CheckpointContractFingerprint -cne
        (Get-QualitySequenceCheckpointFingerprint ([string]$Workload.qualityTrajectory))) {
        throw "$Label checkpoint contract fingerprint differs."
    }
    $actualIndices = @($Report.CheckpointIndices | ForEach-Object { [int]$_ })
    if (($actualIndices -join ',') -cne ($expectedIndices -join ',')) {
        throw "$Label checkpoint order differs from the authored contract."
    }
    Assert-QualitySequenceCaptureRun `
        $Report.CaptureRun $BuildIdentity $Workload $Configuration `
        $ExpectedCommit "$Label top-level"
    Assert-QualitySequenceProducer `
        $Report.ProducerIdentity $ExpectedCommit "$Label top-level"
    if ([string]$Report.CaptureRun.ShaderBundleHash.Substring(7) -cne
            [string]$Report.ProducerIdentity.ShaderFingerprint -or
        [string]$Report.CaptureRun.Commit -cne
            [string]$Report.ProducerIdentity.BuildCommit) {
        throw "$Label top-level producer/CaptureRun linkage differs."
    }
    if ($Role -eq "canonical") {
        if (-not [string]::IsNullOrEmpty([string]$Report.ReferenceContractPath) -or
            -not [string]::IsNullOrEmpty([string]$Report.ReferenceContractSha256)) {
            throw "$Label canonical sequence consumed a reference contract."
        }
    } else {
        if (-not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$Report.ReferenceContractPath),
                [System.IO.Path]::GetFullPath($ReferenceContractPath),
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$Report.ReferenceContractSha256 -cne
                $ExpectedReferenceContractSha256 -or
            $null -eq $ReferenceContract -or
            [string]$Report.TrajectoryFingerprint -cne
                [string]$ReferenceContract.trajectoryFingerprint -or
            [string]$Report.TrajectoryRouteHash -cne
                [string]$ReferenceContract.trajectoryRouteHash -or
            [string]$Report.TrajectorySequenceHash -cne
                [string]$ReferenceContract.trajectorySequenceHash -or
            [string]$Report.Activation -cne
                [string]$ReferenceContract.activation -or
            [string]$Report.ActivationFingerprint -cne
                [string]$ReferenceContract.activationFingerprint -or
            [string]$Report.ActivationEvidence.AnimationConfigurationFingerprint -cne
                [string]$ReferenceContract.activationAnimationConfigurationFingerprint -or
            [string]$Report.ActivationEvidence.AnimationSequenceHash -cne
                [string]$ReferenceContract.activationAnimationSequenceHash -or
            [string]$Report.ActivationEvidence.ActivationStructuralSequenceHash -cne
                [string]$ReferenceContract.activationStructuralSequenceHash -or
            [string]$Report.ActivationEvidence.ActivationExecutionSequenceHash -cne
                [string]$ReferenceContract.activationExecutionSequenceHash) {
            throw "$Label differs from its immutable quality reference."
        }
        if ([string]$Workload.scene -ceq "Sponza") {
            if ([string]$Report.SponzaSceneAnimationEvidence.Fingerprint -cne
                    [string]$ReferenceContract.sponzaSceneAnimationFingerprint -or
                [int]$Report.SponzaSceneAnimationEvidence.Mode -ne
                    [int]$ReferenceContract.sponzaSceneAnimationMode -or
                [string]$Report.SponzaSceneAnimationEvidence.ConfigurationFingerprint -cne
                    [string]$ReferenceContract.sponzaSceneAnimationConfigurationFingerprint -or
                [string]$Report.SponzaSceneAnimationEvidence.SequenceHash -cne
                    [string]$ReferenceContract.sponzaSceneAnimationSequenceHash -or
                [string]$Report.SponzaSceneAnimationEvidence.SidecarSha256 -cne
                    [string]$ReferenceContract.sponzaSceneAnimationSidecarSha256) {
                throw "$Label common Sponza animation identity differs from reference."
            }
            Assert-PathIdentity `
                ([string]$Report.SponzaSceneAnimationEvidence.SidecarPath) `
                (([System.IO.Path]::GetFullPath($ReportPath)) +
                    ".sponza-animation.bin") `
                "$Label common animation sidecar"
        } elseif (-not [string]::IsNullOrEmpty(
                [string]$Report.SponzaSceneAnimationEvidence.SidecarPath) -or
            -not [string]::IsNullOrEmpty(
                [string]$Report.SponzaSceneAnimationEvidence.SidecarSha256) -or
            [int]$Report.SponzaSceneAnimationEvidence.Mode -ne 0 -or
            [string]$Report.SponzaSceneAnimationEvidence.ConfigurationFingerprint -cne
                "unavailable" -or
            [string]$Report.SponzaSceneAnimationEvidence.SequenceHash -cne
                "unavailable" -or
            [string]$ReferenceContract.sponzaSceneAnimationFingerprint -cne
                "unavailable" -or
            [int]$ReferenceContract.sponzaSceneAnimationMode -ne 0 -or
            [string]$ReferenceContract.sponzaSceneAnimationConfigurationFingerprint -cne
                "unavailable" -or
            [string]$ReferenceContract.sponzaSceneAnimationSequenceHash -cne
                "unavailable" -or
            -not [string]::IsNullOrEmpty(
                [string]$ReferenceContract.sponzaSceneAnimationSidecarPath) -or
            -not [string]::IsNullOrEmpty(
                [string]$ReferenceContract.sponzaSceneAnimationSidecarSha256)) {
            throw "$Label non-Sponza reference carries a common animation sidecar."
        }
    }
    $checkpoints = @($Report.Checkpoints)
    if ($checkpoints.Count -ne $expectedIndices.Count) {
        throw "$Label checkpoint evidence is incomplete."
    }
    $firstSerial = [UInt64]0
    $sceneAssetHash = ""
    for ($index = 0; $index -lt $checkpoints.Count; $index++) {
        $checkpoint = $checkpoints[$index]
        $routeFrame = [int]$expectedIndices[$index]
        $expectedPath = [System.IO.Path]::GetFullPath(
            (Join-Path $OutputDirectory ("checkpoint-{0:D4}.pfm" -f $routeFrame)))
        $expectedToken = "${SequenceId}:$($Workload.qualityTrajectory):{0:D2}:{1:D4}" -f $index, $routeFrame
        if ([int]$checkpoint.Ordinal -ne $index -or
            [int]$checkpoint.RouteFrameIndex -ne $routeFrame -or
            [int]$checkpoint.AbsoluteFrameIndex -ne ($first + $routeFrame) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$checkpoint.PfmPath),
                $expectedPath,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$checkpoint.CaptureToken -cne $expectedToken -or
            [int]$checkpoint.Width -ne 1920 -or
            [int]$checkpoint.Height -ne 1080 -or
            [UInt64]$checkpoint.DdgiFrameSerial -eq [UInt64]::MaxValue) {
            throw "$Label checkpoint $index identity is invalid."
        }
        if ($index -eq 0) { $firstSerial = [UInt64]$checkpoint.DdgiFrameSerial }
        if ([UInt64]$checkpoint.DdgiFrameSerial -ne
            [UInt64]($firstSerial + [UInt64]$routeFrame)) {
            throw "$Label checkpoint $index frame serial is not route-aligned."
        }
        Assert-CanonicalSha256 ([string]$checkpoint.PfmSha256) "$Label checkpoint $index PFM"
        Assert-CanonicalSha256 ([string]$checkpoint.SettingsFingerprint) "$Label checkpoint $index settings" $true
        Assert-CanonicalSha256 ([string]$checkpoint.SceneAssetHash) "$Label checkpoint $index scene asset" $true
        Assert-CanonicalSha256 ([string]$checkpoint.SceneStateHash) "$Label checkpoint $index scene state" $true
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf) -or
            [string]$checkpoint.PfmSha256 -cne (Get-Sha256 $expectedPath)) {
            throw "$Label checkpoint $index PFM bytes differ from its report."
        }
        Assert-LinearHdrPfm $expectedPath 1920 1080 "$Label checkpoint $index"
        Assert-QualitySequenceCaptureRun `
            $checkpoint.CaptureRun $BuildIdentity $Workload $Configuration `
            $ExpectedCommit "$Label checkpoint $index"
        Assert-QualitySequenceProducer `
            $checkpoint.ProducerIdentity $ExpectedCommit "$Label checkpoint $index"
        if ([string]$checkpoint.SettingsFingerprint.Substring(7) -cne
                [string]$checkpoint.ProducerIdentity.SettingsFingerprint -or
            [string]$checkpoint.CaptureRun.ExecutableHash -cne
                [string]$Report.CaptureRun.ExecutableHash -or
            [string]$checkpoint.ProducerIdentity.GpuName -cne
                [string]$Report.ProducerIdentity.GpuName -or
            [string]$checkpoint.ProducerIdentity.DriverVersion -cne
                [string]$Report.ProducerIdentity.DriverVersion -or
            [string]$checkpoint.ProducerIdentity.QualityTier -cne
                [string]$Report.ProducerIdentity.QualityTier) {
            throw "$Label checkpoint $index changed producer/build identity."
        }
        Assert-QualityCaptureRunEqual `
            $checkpoint.CaptureRun $Report.CaptureRun $false `
            "$Label checkpoint $index/top-level"
        Assert-QualityProducerEqual `
            $checkpoint.ProducerIdentity $Report.ProducerIdentity $false `
            "$Label checkpoint $index/top-level"
        if ([string]$checkpoint.CaptureRun.ShaderBundleHash.Substring(7) -cne
                [string]$checkpoint.ProducerIdentity.ShaderFingerprint -or
            [string]$checkpoint.CaptureRun.Commit -cne
                [string]$checkpoint.ProducerIdentity.BuildCommit) {
            throw "$Label checkpoint $index producer/CaptureRun linkage differs."
        }
        if ($index -eq 0) {
            $sceneAssetHash = [string]$checkpoint.SceneAssetHash
        } elseif ([string]$checkpoint.SceneAssetHash -cne $sceneAssetHash) {
            throw "$Label changed scene asset identity during the route."
        }
        if ($Role -eq "canonical") {
            $unavailable = $checkpoint.HdrDifference
            foreach ($numericName in @(
                    "Width", "Height", "Rmse", "RelativeRmse",
                    "MeanAbsoluteError", "MaximumAbsoluteError",
                    "MaximumRelativeRmse", "FlipP95", "MaximumFlipP95")) {
                $null = Assert-FiniteNumber `
                    $unavailable.$numericName `
                    "$Label canonical checkpoint $index $numericName"
            }
            if ([bool]$unavailable.Available -or
                [bool]$unavailable.Passed -or
                -not [string]::IsNullOrEmpty([string]$unavailable.ReferencePath) -or
                -not [string]::IsNullOrEmpty([string]$unavailable.CandidatePath) -or
                -not [string]::IsNullOrEmpty([string]$unavailable.ReferenceSha256) -or
                -not [string]::IsNullOrEmpty([string]$unavailable.CandidateSha256) -or
                [int]$unavailable.Width -ne 0 -or
                [int]$unavailable.Height -ne 0 -or
                [double]$unavailable.Rmse -ne 0.0 -or
                [double]$unavailable.RelativeRmse -ne 0.0 -or
                [double]$unavailable.MeanAbsoluteError -ne 0.0 -or
                [double]$unavailable.MaximumAbsoluteError -ne 0.0 -or
                [double]$unavailable.MaximumRelativeRmse -ne 0.12 -or
                [double]$unavailable.FlipP95 -ne 0.0 -or
                [double]$unavailable.MaximumFlipP95 -ne 0.02 -or
                -not [string]::IsNullOrEmpty(
                    [string]$unavailable.QualityContractPath) -or
                -not [string]::IsNullOrEmpty(
                    [string]$unavailable.QualityContractSha256) -or
                (Get-ItemCount $unavailable.RoiResults) -ne 0 -or
                [string]$unavailable.FailureReason -cne
                    "Canonical quality checkpoint; no reference comparison requested.") {
                throw "$Label canonical checkpoint $index unexpectedly contains comparison evidence."
            }
        } else {
            $difference = $checkpoint.HdrDifference
            $referenceCheckpoint = @($ReferenceContract.checkpoints)[$index]
            Assert-QualityCameraEqual `
                $checkpoint.Camera $referenceCheckpoint.camera `
                "$Label checkpoint $index/reference"
            if ([string]$checkpoint.SceneAssetHash -cne
                    [string]$referenceCheckpoint.sceneAssetHash -or
                [string]$checkpoint.SceneStateHash -cne
                    [string]$referenceCheckpoint.sceneStateHash -or
                [UInt64]$checkpoint.SceneContentRevision -ne
                    [UInt64]$referenceCheckpoint.sceneContentRevision -or
                [string]$checkpoint.SettingsFingerprint -cne
                    [string]$referenceCheckpoint.settingsFingerprint) {
                throw "$Label checkpoint $index scene/settings identity differs from reference."
            }
            $crossBuild = $Role -eq "candidate"
            Assert-QualityCaptureRunEqual `
                $checkpoint.CaptureRun $referenceCheckpoint.captureRun `
                $crossBuild "$Label checkpoint $index/reference"
            Assert-QualityProducerEqual `
                $checkpoint.ProducerIdentity `
                $referenceCheckpoint.producerIdentity `
                $crossBuild "$Label checkpoint $index/reference"
            if (-not [bool]$difference.Available -or
                -not [bool]$difference.Passed -or
                [int]$difference.Width -ne 1920 -or
                [int]$difference.Height -ne 1080 -or
                [string]$difference.CandidateSha256 -cne
                    [string]$checkpoint.PfmSha256 -or
                [string]$difference.ReferenceSha256 -cne
                    [string]$referenceCheckpoint.pfmSha256 -or
                [string]$difference.QualityContractSha256 -cne
                    $ExpectedQualityContractSha256 -or
                [double]$difference.MaximumRelativeRmse -ne
                    [double]$Manifest.quality.maximumRelativeRmse -or
                [double]$difference.MaximumFlipP95 -ne
                    [double]$Manifest.quality.maximumFlipP95 -or
                -not [string]::IsNullOrEmpty(
                    [string]$difference.FailureReason) -or
                -not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$difference.CandidatePath),
                    $expectedPath,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$difference.ReferencePath),
                    [System.IO.Path]::GetFullPath([string]$referenceCheckpoint.pfmPath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$difference.QualityContractPath),
                    [System.IO.Path]::GetFullPath($QualityContractPath),
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "$Label checkpoint $index HDR comparison provenance is invalid."
            }
            foreach ($metric in @(
                    "Rmse", "RelativeRmse", "MeanAbsoluteError",
                    "MaximumAbsoluteError", "FlipP95")) {
                $null = Assert-FiniteNumber `
                    $difference.$metric `
                    "$Label checkpoint $index $metric"
            }
            if ([double]$difference.RelativeRmse -gt
                    [double]$Manifest.quality.maximumRelativeRmse -or
                [double]$difference.FlipP95 -gt
                    [double]$Manifest.quality.maximumFlipP95) {
                throw "$Label checkpoint $index exceeds spatial image gates."
            }
            $rois = @($difference.RoiResults)
            $authoredRois = @((Get-PropertyValue $Workload "qualityRois" @()))
            $expectedRoi = if ($authoredRois.Count -eq 0) {
                [pscustomobject]@{
                    name = "$($Workload.id)-full-frame"
                    x = 0; y = 0; width = 1920; height = 1080
                }
            } else {
                $authoredRois[0]
            }
            if ($rois.Count -ne 1 -or -not [bool]$rois[0].Passed -or
                [string]$rois[0].Name -cne [string]$expectedRoi.name -or
                [int]$rois[0].X -ne [int]$expectedRoi.x -or
                [int]$rois[0].Y -ne [int]$expectedRoi.y -or
                [int]$rois[0].Width -ne [int]$expectedRoi.width -or
                [int]$rois[0].Height -ne [int]$expectedRoi.height -or
                [double]$rois[0].MaximumMeanLuminanceShift -ne
                    [double]$Manifest.quality.maximumRoiMeanLuminanceShift -or
                [double]$rois[0].MaximumP95LuminanceShift -ne
                    [double]$Manifest.quality.maximumRoiP95LuminanceShift -or
                (Assert-FiniteNumber $rois[0].MeanLuminanceShift "$Label checkpoint $index ROI mean") -gt
                    [double]$Manifest.quality.maximumRoiMeanLuminanceShift -or
                (Assert-FiniteNumber $rois[0].P95LuminanceShift "$Label checkpoint $index ROI P95") -gt
                    [double]$Manifest.quality.maximumRoiP95LuminanceShift) {
                throw "$Label checkpoint $index ROI gate failed."
            }
        }
    }
    $pairs = @(Get-QualitySequenceTemporalPairs ([string]$Workload.qualityTrajectory))
    $temporal = @($Report.TemporalResiduals)
    if ($Role -eq "canonical") {
        if ($temporal.Count -ne 0) {
            throw "$Label canonical report contains temporal comparisons."
        }
    } elseif ($temporal.Count -ne $pairs.Count) {
        throw "$Label temporal pair evidence is incomplete."
    } else {
        for ($index = 0; $index -lt $pairs.Count; $index++) {
            $result = $temporal[$index]
            if ([int]$result.FromRouteFrameIndex -ne
                    [int]$pairs[$index].fromRouteFrameIndex -or
                [int]$result.ToRouteFrameIndex -ne
                    [int]$pairs[$index].toRouteFrameIndex -or
                -not [bool]$result.Passed) {
                throw "$Label temporal pair $index is invalid."
            }
            $null = Assert-FiniteNumber $result.RelativeResidual "$Label temporal pair $index"
            if ($Role -eq "repeat") {
                if ($null -ne $result.MaximumRelativeResidual) {
                    throw "$Label repeat temporal pair $index consumed a candidate gate."
                }
            } else {
                $gate = @($ReferenceContract.temporalGates)[$index]
                $maximum = Assert-FiniteNumber `
                    $result.MaximumRelativeResidual `
                    "$Label temporal pair $index maximum"
                if ($maximum -ne [double]$gate.maximumRelativeResidual -or
                    [double]$result.RelativeResidual -gt $maximum) {
                    throw "$Label temporal pair $index differs from its locked gate."
                }
            }
        }
    }
}

function Assert-QualitySequenceHealthReport {
    param(
        $Manifest,
        $Workload,
        $Health,
        $Report,
        $BuildIdentity,
        [string]$Configuration,
        [string]$Role,
        [string]$SequenceId,
        [string]$ExpectedCommit,
        [string]$ReportPath,
        [string]$OutputDirectory,
        [string]$ReferenceContractPath,
        [string]$QualityContractPath,
        [string]$Label)
    $failure = [string](Get-PropertyValue $Health "failure" "")
    if ([string]$Health.kind -ne "renderer-health" -or
        [string]$Health.schema -ne "renderer-health/v3" -or
        [string]$Health.status -ne "passed" -or
        -not [string]::IsNullOrEmpty($failure) -or
        $null -eq $Health.options -or
        $null -eq $Health.options.BenchmarkQualitySequence -or
        $null -eq $Health.diagnostics -or
        $null -eq $Health.diagnostics.CaptureRun -or
        $null -eq $Health.producerIdentity) {
        throw "$Label quality health gate failed: $failure"
    }
    $options = $Health.options.BenchmarkQualitySequence
    $roleName = Get-QualitySequenceRoleName (
        Get-QualitySequenceRoleValue $Role)
    $expectedRoleName = $roleName.Substring(0, 1).ToUpperInvariant() +
        $roleName.Substring(1)
    $expectedTrajectoryName = switch ([string]$Workload.qualityTrajectory) {
        "bistro-presentation" { "BistroPresentation" }
        "bistro-loop" { "BistroLoop" }
        "sponza-low" { "SponzaLow" }
        "sponza-high" { "SponzaHigh" }
        "sponza-horizontal" { "SponzaHorizontal" }
        "sponza-vertical" { "SponzaVertical" }
        default { "Stationary" }
    }
    $expectedSceneKind = if ([string]$Workload.scene -eq "Sponza") {
        "SponzaPlaza"
    } else {
        [string]$Workload.scene
    }
    $expectedReferencePath = if ($Role -eq "canonical") {
        ""
    } else {
        [System.IO.Path]::GetFullPath($ReferenceContractPath)
    }
    $expectedRoiPath = if ($Role -eq "canonical") {
        ""
    } else {
        [System.IO.Path]::GetFullPath($QualityContractPath)
    }
    if (-not [bool]$options.Enabled -or
        [string]$options.Role -cne $expectedRoleName -or
        [string]$options.SequenceId -cne $SequenceId -or
        [int]$options.WarmupFrameCount -ne [int]$Workload.warmupFrames -or
        [int]$options.MaximumAdditionalSettlingFrameCount -ne
            [int]$Manifest.capture.maximumSettlingFrames -or
        [int]$options.MaximumReadbackDrainFrameCount -ne
            [int]$Manifest.qualitySequence.maximumReadbackDrainFrames -or
        [string]$options.ReportPath -cne
            [System.IO.Path]::GetFullPath($ReportPath) -or
        [string]$options.OutputDirectory -cne
            [System.IO.Path]::GetFullPath($OutputDirectory) -or
        [string]$options.ReferenceContractPath -cne $expectedReferencePath -or
        [string]$options.HdrQualityContractPath -cne $expectedRoiPath -or
        [string]$options.BudgetProfileOverride -cne "Stress" -or
        [string]$options.CaptureVariant -cne [string]$Workload.captureVariant -or
        [string]$options.Activation -cne [string]$Workload.activation -or
        [string]$options.ActivationFingerprint -cne
            [string]$Report.ActivationFingerprint -or
        [string]$options.SceneKind -cne $expectedSceneKind -or
        [string]$options.Scenario -cne [string]$Workload.scenario -or
        [string]$options.Trajectory -cne $expectedTrajectoryName -or
        [double]$options.HdrMaximumRelativeRmse -ne
            [double]$Manifest.quality.maximumRelativeRmse -or
        [double]$options.HdrMaximumFlipP95 -ne
            [double]$Manifest.quality.maximumFlipP95) {
        throw "$Label health options differ from the quality-sequence command."
    }
    $bistroVariant = [string](Get-PropertyValue $Workload "bistroQualityVariant" "")
    if (-not [string]::IsNullOrEmpty($bistroVariant)) {
        $expectedBistroVariant = switch ($bistroVariant) {
            "presentation" { "Presentation" }
            "steady-motion" { "SteadyMotion" }
            "sun-scale-step" { "SunScaleStep" }
            default { throw "$Label has unknown Bistro variant '$bistroVariant'." }
        }
        if ([string]$options.TrajectoryBistroVariant -cne
            $expectedBistroVariant) {
            throw "$Label health Bistro trajectory variant differs."
        }
    }
    $healthRun = $Health.diagnostics.CaptureRun
    Assert-QualitySequenceCaptureRun `
        $healthRun $BuildIdentity $Workload $Configuration `
        $ExpectedCommit "$Label health"
    $reportRun = $Report.CaptureRun
    foreach ($pair in @(
            @([string]$healthRun.SceneKind, [string]$reportRun.SceneKind, "scene"),
            @([string]$healthRun.Scenario, [string]$reportRun.Scenario, "scenario"),
            @([string]$healthRun.Commit, [string]$reportRun.Commit, "commit"),
            @([string]$healthRun.ExecutableHash, [string]$reportRun.ExecutableHash, "executable"),
            @([string]$healthRun.ShaderBundleHash, [string]$reportRun.ShaderBundleHash, "shader"),
            @([string]$healthRun.ApplicationVersion, [string]$reportRun.ApplicationVersion, "application"),
            @([string]$healthRun.SettingsSchemaVersion, [string]$reportRun.SettingsSchemaVersion, "settings schema"))) {
        if (-not [string]::Equals(
                [string]$pair[0],
                [string]$pair[1],
                [StringComparison]::Ordinal)) {
            throw "$Label health/report $($pair[2]) provenance differs."
        }
    }
    $producer = $Health.producerIdentity
    if ([string]$producer.schema -ne "material-gi-producer-identity/v1" -or
        [string]$producer.buildCommit -cne
            [string]$Report.ProducerIdentity.BuildCommit -or
        [string]$producer.shaderFingerprint -cne
            [string]$Report.ProducerIdentity.ShaderFingerprint -or
        [string]$producer.gpuName -cne
            [string]$Report.ProducerIdentity.GpuName -or
        [string]$producer.driverVersion -cne
            [string]$Report.ProducerIdentity.DriverVersion -or
        [string]$producer.settingsFingerprint -cne
            [string]$Report.ProducerIdentity.SettingsFingerprint -or
        -not [string]::IsNullOrEmpty([string]$producer.qualityTier) -or
        [string]$producer.settingsFingerprint -cnotmatch '^[0-9a-f]{64}$' -or
        @($producer.sourceSettingsFingerprints).Count -ne 1 -or
        [string]@($producer.sourceSettingsFingerprints)[0] -cne
            [string]$producer.settingsFingerprint -or
        [int]$Health.diagnostics.CaptureRenderWidth -ne 1920 -or
        [int]$Health.diagnostics.CaptureRenderHeight -ne 1080) {
        throw "$Label health producer/render identity is invalid."
    }
}

function Assert-QualitySequenceInputHashes {
    param(
        $BuildIdentity,
        [string]$ReferenceContractPath,
        [string]$ExpectedReferenceContractSha256,
        [string]$QualityContractPath,
        [string]$ExpectedQualityContractSha256,
        [string]$Role,
        [string]$Label)
    Assert-BuildIdentity $BuildIdentity $Label
    if ($Role -eq "canonical") {
        if (-not [string]::IsNullOrEmpty($ReferenceContractPath) -or
            -not [string]::IsNullOrEmpty($QualityContractPath)) {
            throw "$Label canonical sequence cannot consume comparison inputs."
        }
    } else {
        if (-not (Test-Path -LiteralPath $QualityContractPath -PathType Leaf) -or
            $ExpectedQualityContractSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-Sha256 $QualityContractPath) -cne
                $ExpectedQualityContractSha256) {
            throw "$Label ROI contract changed or is missing."
        }
        if (-not (Test-Path -LiteralPath $ReferenceContractPath -PathType Leaf) -or
            $ExpectedReferenceContractSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-Sha256 $ReferenceContractPath) -cne
                $ExpectedReferenceContractSha256) {
            throw "$Label reference contract changed or is missing."
        }
        $contract = Get-Content -LiteralPath $ReferenceContractPath -Raw |
            ConvertFrom-Json -DateKind String
        if ([string]$contract.sceneKind -ceq "Sponza") {
            $sidecarPath = [string]$contract.sponzaSceneAnimationSidecarPath
            $sidecarSha256 = [string]$contract.sponzaSceneAnimationSidecarSha256
            if ([string]::IsNullOrWhiteSpace($sidecarPath) -or
                $sidecarSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                -not (Test-Path -LiteralPath $sidecarPath -PathType Leaf) -or
                (Get-Sha256 $sidecarPath) -cne $sidecarSha256) {
                throw "$Label immutable common animation sidecar changed."
            }
        } elseif (-not [string]::IsNullOrEmpty(
                [string]$contract.sponzaSceneAnimationSidecarPath) -or
            -not [string]::IsNullOrEmpty(
                [string]$contract.sponzaSceneAnimationSidecarSha256)) {
            throw "$Label non-Sponza contract contains an animation sidecar."
        }
        foreach ($checkpoint in @($contract.checkpoints)) {
            $path = [string]$checkpoint.pfmPath
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                [string]$checkpoint.pfmSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                (Get-Sha256 $path) -cne [string]$checkpoint.pfmSha256) {
                throw "$Label immutable reference PFM changed: $path"
            }
            Assert-LinearHdrPfm $path 1920 1080 "$Label reference PFM"
        }
        $repeatHashes = @($contract.baselineRepeatReportSha256 |
            ForEach-Object { [string]$_ })
        if ($repeatHashes.Count -gt 0) {
            if ($repeatHashes.Count -ne 2 -or
                @($repeatHashes | Select-Object -Unique).Count -ne 2) {
                throw "$Label candidate contract has invalid repeat-report hashes."
            }
            $contractRoot = Split-Path -Parent $ReferenceContractPath
            for ($repeat = 1; $repeat -le 2; $repeat++) {
                $repeatPath = Join-Path $contractRoot (
                    "repeat-{0:D2}/report.json" -f $repeat)
                if (-not (Test-Path -LiteralPath $repeatPath -PathType Leaf) -or
                    (Get-Sha256 $repeatPath) -cne $repeatHashes[$repeat - 1]) {
                    throw "$Label immutable repeat report $repeat changed."
                }
                $repeatReport = Read-QualitySequenceReport $repeatPath
                if ([string]$repeatReport.Kind -cne
                        "njulf-renderer-benchmark-quality-sequence" -or
                    [string]$repeatReport.Schema -cne
                        "njulf-renderer-benchmark-quality-sequence/v1" -or
                    [int]$repeatReport.Role -ne 1 -or
                    -not [bool]$repeatReport.Passed) {
                    throw "$Label immutable repeat report $repeat is not admitted evidence."
                }
                foreach ($checkpoint in @($repeatReport.Checkpoints)) {
                    $repeatPfm = [string]$checkpoint.PfmPath
                    if (-not (Test-Path -LiteralPath $repeatPfm -PathType Leaf) -or
                        [string]$checkpoint.PfmSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                        (Get-Sha256 $repeatPfm) -cne
                            [string]$checkpoint.PfmSha256) {
                        throw "$Label immutable repeat $repeat PFM changed: $repeatPfm"
                    }
                    Assert-LinearHdrPfm `
                        $repeatPfm 1920 1080 `
                        "$Label repeat $repeat PFM"
                }
            }
        }
    }
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
}

function Invoke-QualitySequenceCapture {
    param(
        $Manifest,
        $Workload,
        $BuildIdentity,
        [string]$Configuration,
        [string]$Role,
        [string]$SequenceId,
        [string]$ReportPath,
        [string]$OutputDirectory,
        [string]$ReferenceContractPath,
        [string]$ExpectedReferenceContractSha256,
        [string]$QualityContractPath,
        [string]$ExpectedQualityContractSha256,
        $ReferenceContract,
        $VerifierBuildIdentity,
        $SpatialEnvelope,
        [string]$ExpectedCommit,
        [string]$Label)
    $healthPath = [System.IO.Path]::ChangeExtension(
        $ReportPath,
        ".health.json")
    $activationArtifactPath = [System.IO.Path]::ChangeExtension(
        $ReportPath,
        ".activation-verification.json")
    $metricArtifactPath = [System.IO.Path]::ChangeExtension(
        $ReportPath,
        ".metric-verification.json")
    $animationSidecarPath =
        ([System.IO.Path]::GetFullPath($ReportPath)) +
            ".sponza-animation.bin"
    $reservedOutputs = @(
        $ReportPath, $healthPath, $OutputDirectory, $activationArtifactPath)
    if ($Role -ne "canonical") { $reservedOutputs += $metricArtifactPath }
    if ([string]$Workload.scene -ceq "Sponza") {
        $reservedOutputs += $animationSidecarPath
    }
    foreach ($path in $reservedOutputs) {
        if (Test-Path -LiteralPath $path) {
            throw "$Label output already exists; refusing to overwrite $path"
        }
    }
    New-Item -ItemType Directory -Force -Path (
        Split-Path -Parent $ReportPath) | Out-Null
    $arguments = @(Get-QualitySequenceArguments `
        $Manifest $Workload $Role $SequenceId $ReportPath $healthPath `
        $OutputDirectory $ReferenceContractPath $QualityContractPath)
    Assert-QualitySequenceInputHashes `
        $BuildIdentity $ReferenceContractPath `
        $ExpectedReferenceContractSha256 $QualityContractPath `
        $ExpectedQualityContractSha256 $Role "$Label pre-capture"
    Invoke-ProcessChecked `
        ([string]$BuildIdentity.ExecutablePath) `
        $arguments $Label ([int]$Manifest.capture.benchmarkTimeoutSeconds)
    Assert-QualitySequenceInputHashes `
        $BuildIdentity $ReferenceContractPath `
        $ExpectedReferenceContractSha256 $QualityContractPath `
        $ExpectedQualityContractSha256 $Role "$Label post-capture"
    $report = Read-QualitySequenceReport $ReportPath
    $admittedReportSha256 = Get-Sha256 $ReportPath
    Assert-QualitySequenceReport `
        $Manifest $Workload $report $BuildIdentity $Configuration $Role `
        $SequenceId $ExpectedCommit $ReportPath $OutputDirectory `
        $ReferenceContractPath $ExpectedReferenceContractSha256 `
        $QualityContractPath $ExpectedQualityContractSha256 `
        $ReferenceContract $Label
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        throw "$Label did not publish its health report."
    }
    $health = Get-Content -LiteralPath $healthPath -Raw |
        ConvertFrom-Json -DateKind String
    $admittedHealthSha256 = Get-Sha256 $healthPath
    Assert-QualitySequenceHealthReport `
        $Manifest $Workload $health $report $BuildIdentity $Configuration `
        $Role $SequenceId $ExpectedCommit $ReportPath $OutputDirectory `
        $ReferenceContractPath $QualityContractPath $Label
    $frozenVerifierEvidence = New-QualityFrozenVerifierEvidence `
        $Workload $report $Role $SequenceId $ReportPath `
        $VerifierBuildIdentity $Label
    $verifiedMetrics = $null
    if ($Role -ne "canonical") {
        $verifiedMetrics = Get-RecomputedQualitySequenceMetrics `
            $Manifest $Workload $report $ReferenceContract `
            $VerifierBuildIdentity $QualityContractPath $metricArtifactPath `
            $null $Label
        if ($Role -eq "candidate") {
            Assert-QualitySequenceSpatialEnvelope `
                $verifiedMetrics @($SpatialEnvelope) $Label
        }
    }
    Assert-QualitySequenceInputHashes `
        $BuildIdentity $ReferenceContractPath `
        $ExpectedReferenceContractSha256 $QualityContractPath `
        $ExpectedQualityContractSha256 $Role "$Label post-verification"
    if ((Get-Sha256 $ReportPath) -cne $admittedReportSha256 -or
        (Get-Sha256 $healthPath) -cne $admittedHealthSha256) {
        throw "$Label report or health bytes changed during frozen verification."
    }
    foreach ($checkpoint in @($report.Checkpoints)) {
        if (-not (Test-Path -LiteralPath ([string]$checkpoint.PfmPath) `
                -PathType Leaf) -or
            (Get-Sha256 ([string]$checkpoint.PfmPath)) -cne
                [string]$checkpoint.PfmSha256) {
            throw "$Label checkpoint PFM changed during frozen verification."
        }
    }
    if ((Get-Sha256 ([string]$frozenVerifierEvidence.activation.artifactPath)) -cne
            [string]$frozenVerifierEvidence.activation.artifactSha256 -or
        ([string]$Workload.scene -ceq "Sponza" -and
         (Get-Sha256 ([string]$frozenVerifierEvidence.sponzaAnimationSidecar.path)) -cne
            [string]$frozenVerifierEvidence.sponzaAnimationSidecar.sha256) -or
        ($Role -ne "canonical" -and
         (Get-Sha256 ([string]$verifiedMetrics.metricVerifierArtifact.artifactPath)) -cne
            [string]$verifiedMetrics.metricVerifierArtifact.artifactSha256)) {
        throw "$Label verifier artifact or animation sidecar changed before admission."
    }
    $checkpointEvidence = @($report.Checkpoints | ForEach-Object {
        [pscustomobject]@{
            ordinal = [int]$_.Ordinal
            routeFrameIndex = [int]$_.RouteFrameIndex
            pfmPath = [System.IO.Path]::GetFullPath([string]$_.PfmPath)
            pfmSha256 = Get-Sha256 ([string]$_.PfmPath)
            captureToken = [string]$_.CaptureToken
            ddgiFrameSerial = [UInt64]$_.DdgiFrameSerial
            absoluteFrameIndex = [int]$_.AbsoluteFrameIndex
            width = [int]$_.Width
            height = [int]$_.Height
            camera = $_.Camera
            sceneAssetHash = [string]$_.SceneAssetHash
            sceneStateHash = [string]$_.SceneStateHash
            sceneContentRevision = [UInt64]$_.SceneContentRevision
            settingsFingerprint = [string]$_.SettingsFingerprint
            captureRun = $_.CaptureRun
            producerIdentity = $_.ProducerIdentity
            hdrDifference = $_.HdrDifference
            activationFrameState = $_.ActivationFrameState
        }
    })
    return [pscustomobject]@{
        role = $Role
        sequenceId = $SequenceId
        reportPath = [System.IO.Path]::GetFullPath($ReportPath)
        reportSha256 = $admittedReportSha256
        healthPath = [System.IO.Path]::GetFullPath($healthPath)
        healthSha256 = $admittedHealthSha256
        outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
        referenceContractPath = $ReferenceContractPath
        referenceContractSha256 = $ExpectedReferenceContractSha256
        qualityContractPath = $QualityContractPath
        qualityContractSha256 = $ExpectedQualityContractSha256
        buildRootPath = [System.IO.Path]::GetFullPath(
            [string]$BuildIdentity.RootPath)
        buildBundleFingerprint = [string]$BuildIdentity.BundleFingerprint
        runtimeExecutableBundleHash =
            [string]$BuildIdentity.RuntimeExecutableBundleHash
        buildIdentity = $BuildIdentity
        frozenVerifierEvidence = $frozenVerifierEvidence
        trajectoryRouteHash = [string]$report.TrajectoryRouteHash
        trajectorySequenceHash = [string]$report.TrajectorySequenceHash
        producerGpuName = [string]$report.ProducerIdentity.GpuName
        producerDriverVersion = [string]$report.ProducerIdentity.DriverVersion
        producerQualityTier = [string]$report.ProducerIdentity.QualityTier
        captureRun = $report.CaptureRun
        producerIdentity = $report.ProducerIdentity
        verifiedMetrics = $verifiedMetrics
        checkpoints = @($checkpointEvidence)
    }
}

function ConvertTo-QualityCaptureRunContract {
    param($Run)
    return [ordered]@{
        sceneKind = [string]$Run.SceneKind
        scenario = [string]$Run.Scenario
        buildConfiguration = [string]$Run.BuildConfiguration
        applicationVersion = [string]$Run.ApplicationVersion
        commit = [string]$Run.Commit
        shaderBundleHash = [string]$Run.ShaderBundleHash
        settingsSchemaVersion = [int]$Run.SettingsSchemaVersion
        executableHash = [string]$Run.ExecutableHash
        dirtyWorktreeState = [string]$Run.DirtyWorktreeState
    }
}

function ConvertTo-QualityProducerContract {
    param($Producer)
    return [ordered]@{
        schema = [string]$Producer.Schema
        buildCommit = [string]$Producer.BuildCommit
        shaderFingerprint = [string]$Producer.ShaderFingerprint
        settingsFingerprint = [string]$Producer.SettingsFingerprint
        sourceSettingsFingerprints = @(
            $Producer.SourceSettingsFingerprints | ForEach-Object { [string]$_ })
        gpuName = [string]$Producer.GpuName
        driverVersion = [string]$Producer.DriverVersion
        qualityTier = [string]$Producer.QualityTier
    }
}

function ConvertTo-QualityCameraContract {
    param($Camera)
    return [ordered]@{
        positionX = [single]$Camera.PositionX
        positionY = [single]$Camera.PositionY
        positionZ = [single]$Camera.PositionZ
        yawRadians = [single]$Camera.YawRadians
        pitchRadians = [single]$Camera.PitchRadians
        fieldOfViewRadians = [single]$Camera.FieldOfViewRadians
        nearPlane = [single]$Camera.NearPlane
        farPlane = [single]$Camera.FarPlane
        viewHash = [string]$Camera.ViewHash
        projectionHash = [string]$Camera.ProjectionHash
        cameraCutSerial = [UInt64]$Camera.CameraCutSerial
    }
}

function New-QualitySequenceReferenceContract {
    param(
        $Manifest,
        $Workload,
        $CanonicalReport,
        [string]$QualityContractPath,
        [string]$QualityContractSha256,
        [object[]]$TemporalGates = @(),
        [string[]]$BaselineRepeatReportSha256 = @())
    $checkpoints = @($CanonicalReport.Checkpoints | ForEach-Object {
        [ordered]@{
            ordinal = [int]$_.Ordinal
            routeFrameIndex = [int]$_.RouteFrameIndex
            absoluteFrameIndex = [int]$_.AbsoluteFrameIndex
            pfmPath = [System.IO.Path]::GetFullPath([string]$_.PfmPath)
            pfmSha256 = [string]$_.PfmSha256
            width = [int]$_.Width
            height = [int]$_.Height
            captureToken = [string]$_.CaptureToken
            ddgiFrameSerial = [UInt64]$_.DdgiFrameSerial
            camera = ConvertTo-QualityCameraContract $_.Camera
            sceneAssetHash = [string]$_.SceneAssetHash
            sceneStateHash = [string]$_.SceneStateHash
            sceneContentRevision = [UInt64]$_.SceneContentRevision
            settingsFingerprint = [string]$_.SettingsFingerprint
            captureRun = ConvertTo-QualityCaptureRunContract $_.CaptureRun
            producerIdentity = ConvertTo-QualityProducerContract $_.ProducerIdentity
            activationFrameState = $_.ActivationFrameState
        }
    })
    $sponzaAnimation = if ([string]$Workload.scene -ceq "Sponza") {
        [ordered]@{
            fingerprint =
                [string]$CanonicalReport.SponzaSceneAnimationEvidence.Fingerprint
            mode = [int]$CanonicalReport.SponzaSceneAnimationEvidence.Mode
            configurationFingerprint =
                [string]$CanonicalReport.SponzaSceneAnimationEvidence.ConfigurationFingerprint
            sequenceHash =
                [string]$CanonicalReport.SponzaSceneAnimationEvidence.SequenceHash
            sidecarPath =
                [string]$CanonicalReport.SponzaSceneAnimationEvidence.SidecarPath
            sidecarSha256 =
                [string]$CanonicalReport.SponzaSceneAnimationEvidence.SidecarSha256
        }
    } else {
        [ordered]@{
            fingerprint = "unavailable"
            mode = 0
            configurationFingerprint = "unavailable"
            sequenceHash = "unavailable"
            sidecarPath = ""
            sidecarSha256 = ""
        }
    }
    return [ordered]@{
        schema = "njulf-benchmark-quality-sequence-reference/v1"
        sceneKind = [string]$CanonicalReport.SceneKind
        scenario = [string]$CanonicalReport.Scenario
        captureVariant = [string]$CanonicalReport.CaptureVariant
        buildConfiguration = [string]$CanonicalReport.BuildConfiguration
        trajectory = [string]$CanonicalReport.Trajectory
        trajectoryFingerprint = [string]$CanonicalReport.TrajectoryFingerprint
        trajectoryRouteHash = [string]$CanonicalReport.TrajectoryRouteHash
        trajectorySequenceHash = [string]$CanonicalReport.TrajectorySequenceHash
        trajectoryFrameCount = [int]$CanonicalReport.TrajectoryFrameCount
        warmupFrameCount = [int]$CanonicalReport.WarmupFrameCount
        maximumAdditionalSettlingFrameCount =
            [int]$CanonicalReport.MaximumAdditionalSettlingFrameCount
        maximumReadbackDrainFrameCount =
            [int]$CanonicalReport.MaximumReadbackDrainFrameCount
        firstRouteAbsoluteFrameIndex = [int]$CanonicalReport.FirstRouteAbsoluteFrameIndex
        checkpointContractFingerprint =
            [string]$CanonicalReport.CheckpointContractFingerprint
        checkpointIndices = @(
            $CanonicalReport.CheckpointIndices | ForEach-Object { [int]$_ })
        checkpoints = @($checkpoints)
        qualityContractPath = [System.IO.Path]::GetFullPath($QualityContractPath)
        qualityContractSha256 = $QualityContractSha256
        maximumRelativeRmse = [double]$Manifest.quality.maximumRelativeRmse
        maximumFlipP95 = [double]$Manifest.quality.maximumFlipP95
        temporalGates = @($TemporalGates)
        captureRun = ConvertTo-QualityCaptureRunContract $CanonicalReport.CaptureRun
        producerIdentity = ConvertTo-QualityProducerContract $CanonicalReport.ProducerIdentity
        activation = [string]$CanonicalReport.Activation
        activationFingerprint = [string]$CanonicalReport.ActivationFingerprint
        activationAnimationConfigurationFingerprint =
            [string]$CanonicalReport.ActivationEvidence.AnimationConfigurationFingerprint
        activationAnimationSequenceHash =
            [string]$CanonicalReport.ActivationEvidence.AnimationSequenceHash
        activationStructuralSequenceHash =
            [string]$CanonicalReport.ActivationEvidence.ActivationStructuralSequenceHash
        activationExecutionSequenceHash =
            [string]$CanonicalReport.ActivationEvidence.ActivationExecutionSequenceHash
        sponzaSceneAnimationFingerprint =
            [string]$sponzaAnimation.fingerprint
        sponzaSceneAnimationMode =
            [int]$sponzaAnimation.mode
        sponzaSceneAnimationConfigurationFingerprint =
            [string]$sponzaAnimation.configurationFingerprint
        sponzaSceneAnimationSequenceHash =
            [string]$sponzaAnimation.sequenceHash
        sponzaSceneAnimationSidecarPath =
            [string]$sponzaAnimation.sidecarPath
        sponzaSceneAnimationSidecarSha256 =
            [string]$sponzaAnimation.sidecarSha256
        temporalResidualFloor =
            [double]$Manifest.qualitySequence.temporalResidualFloor
        temporalResidualMultiplier =
            [double]$Manifest.qualitySequence.temporalResidualMultiplier
        temporalResidualHardCeiling =
            [double]$Manifest.qualitySequence.temporalResidualHardCeiling
        baselineRepeatReportSha256 = @($BaselineRepeatReportSha256)
    }
}

function Write-QualitySequenceReferenceContract {
    param([string]$Path, $Contract)
    if (Test-Path -LiteralPath $Path) {
        throw "Quality-sequence reference contract already exists: $Path"
    }
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f
            [System.IO.Path]::GetFileName($Path),
            [Guid]::NewGuid().ToString("N"))
    try {
        $Contract | ConvertTo-Json -Depth 24 |
            Set-Content -LiteralPath $temporaryPath -Encoding utf8
        $null = Get-Content -LiteralPath $temporaryPath -Raw |
            ConvertFrom-Json -DateKind String
        [System.IO.File]::Move($temporaryPath, $Path, $false)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
    return [pscustomobject]@{
        path = [System.IO.Path]::GetFullPath($Path)
        sha256 = Get-Sha256 $Path
        contract = $Contract
    }
}

function New-QualitySequenceTemporalGates {
    param(
        $Manifest,
        $Workload,
        [object[]]$VerifiedRepeatMetrics)
    if ($VerifiedRepeatMetrics.Count -ne
        [int]$Manifest.qualitySequence.baselineRepeatCount) {
        throw "Temporal gates require exactly two admitted baseline repeats."
    }
    $pairs = @(Get-QualitySequenceTemporalPairs ([string]$Workload.qualityTrajectory))
    $gates = @()
    for ($pairIndex = 0; $pairIndex -lt $pairs.Count; $pairIndex++) {
        $residuals = @($VerifiedRepeatMetrics | ForEach-Object {
            $results = @($_.temporal)
            if ($results.Count -ne $pairs.Count) {
                throw "Baseline repeat temporal topology is incomplete."
            }
            [double]$results[$pairIndex].relativeResidual
        })
        foreach ($residual in $residuals) {
            $null = Assert-FiniteNumber `
                $residual `
                "Baseline repeat temporal residual $pairIndex"
        }
        $repeatMaximum = [double](
            $residuals | Measure-Object -Maximum).Maximum
        $derived = [Math]::Max(
            [double]$Manifest.qualitySequence.temporalResidualFloor,
            $repeatMaximum *
                [double]$Manifest.qualitySequence.temporalResidualMultiplier)
        if (-not [double]::IsFinite($derived) -or
            $derived -gt
                [double]$Manifest.qualitySequence.temporalResidualHardCeiling) {
            throw (
                "Clean-baseline temporal gate $($pairs[$pairIndex].fromRouteFrameIndex)->" +
                "$($pairs[$pairIndex].toRouteFrameIndex) derived as $derived, exceeding " +
                "the hard ceiling $($Manifest.qualitySequence.temporalResidualHardCeiling).")
        }
        $gates += [ordered]@{
            fromRouteFrameIndex = [int]$pairs[$pairIndex].fromRouteFrameIndex
            toRouteFrameIndex = [int]$pairs[$pairIndex].toRouteFrameIndex
            maximumRelativeResidual = $derived
        }
    }
    return @($gates)
}

function ConvertFrom-QualityMetricVerifierBytes {
    param([byte[]]$Bytes, [string]$Label)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0 -or
        $Bytes.Length -gt 16MB) {
        throw "$Label stdout byte length is outside the admitted range."
    }
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try { $json = $encoding.GetString($Bytes) } catch {
        throw "$Label stdout is not canonical UTF-8."
    }
    if (-not $json.StartsWith("{", [StringComparison]::Ordinal) -or
        -not $json.EndsWith("}", [StringComparison]::Ordinal) -or
        $json.Contains("`r", [StringComparison]::Ordinal) -or
        $json.Contains("`n", [StringComparison]::Ordinal)) {
        throw "$Label stdout must be exactly one compact JSON object."
    }
    try {
        $documentOptions = [System.Text.Json.JsonDocumentOptions]::new()
        $documentOptions.AllowTrailingCommas = $false
        $documentOptions.CommentHandling =
            [System.Text.Json.JsonCommentHandling]::Disallow
        $documentOptions.MaxDepth = 64
        $document = [System.Text.Json.JsonDocument]::Parse(
            $json,
            $documentOptions)
        try {
            if ($document.RootElement.ValueKind -ne
                [System.Text.Json.JsonValueKind]::Object) {
                throw "$Label stdout root must be an object."
            }
            Assert-NoDuplicateJsonProperties $document.RootElement $Label
        } finally {
            $document.Dispose()
        }
        $result = $json | ConvertFrom-Json -DateKind String
    } catch {
        throw "$Label stdout is not one strict JSON object: $($_.Exception.Message)"
    }
    return $result
}

function Assert-QualityMetricVerifierInputs {
    param([object[]]$Operations, [string]$Label)
    for ($index = 0; $index -lt $Operations.Count; $index++) {
        $operation = $Operations[$index]
        $pairs = switch ([string]$operation.kind) {
            "spatial" {
                @(
                    @([string]$operation.referencePath,
                        [string]$operation.referenceSha256),
                    @([string]$operation.candidatePath,
                        [string]$operation.candidateSha256),
                    @([string]$operation.qualityContractPath,
                        [string]$operation.qualityContractSha256))
                break
            }
            "temporal" {
                @(
                    @([string]$operation.referenceFromPath,
                        [string]$operation.referenceFromSha256),
                    @([string]$operation.referenceToPath,
                        [string]$operation.referenceToSha256),
                    @([string]$operation.candidateFromPath,
                        [string]$operation.candidateFromSha256),
                    @([string]$operation.candidateToPath,
                        [string]$operation.candidateToSha256))
                break
            }
            default {
                throw "$Label operation $index has unknown kind '$($operation.kind)'."
            }
        }
        $inputIndex = 0
        foreach ($pair in @($pairs)) {
            $path = [string]$pair[0]
            $sha256 = [string]$pair[1]
            if ([string]::IsNullOrWhiteSpace($path) -or
                $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
                -not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-Sha256 $path) -cne $sha256) {
                throw "$Label operation $index input $inputIndex changed or is invalid."
            }
            $inputIndex++
        }
    }
}

function Invoke-QualityMetricVerifier {
    param(
        $Manifest,
        $BuildIdentity,
        [object[]]$Operations,
        [string]$ArtifactPath,
        $ExpectedArtifactEvidence,
        [string]$Label)
    Assert-BuildIdentity $BuildIdentity "$Label verifier build"
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
    Assert-QualityMetricVerifierInputs $Operations "$Label pre-run"
    $request = [ordered]@{
        schema = "njulf-perf-quality-verify-request/v1"
        operations = @($Operations)
    }
    $requestJson = $request | ConvertTo-Json -Depth 12 -Compress
    $hostExecutable = Join-Path $PSHOME "pwsh.exe"
    $helperPath = Join-Path $script:SolutionRoot "tools/perf-quality-verify.ps1"
    $helperRelativePath = "tools/perf-quality-verify.ps1"
    $expectedHelperHash = [string]$script:ProtectedFingerprints[$helperRelativePath]
    [byte[]]$helperBytes = [System.IO.File]::ReadAllBytes($helperPath)
    $actualHelperHash = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($helperBytes)).ToLowerInvariant()
    if ($expectedHelperHash -cnotmatch '^[0-9a-f]{64}$' -or
        $actualHelperHash -cne $expectedHelperHash) {
        throw "$Label verifier helper differs from its admitted bytes."
    }
    $helperText = [System.Text.UTF8Encoding]::new(
        $false,
        $true).GetString($helperBytes)
    $encodedHelper = [Convert]::ToBase64String(
        [System.Text.Encoding]::Unicode.GetBytes($helperText))
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $hostExecutable
    $startInfo.WorkingDirectory = $script:SolutionRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["NJULF_PERF_VERIFY_BUILD_ROOT"] =
        [string]$BuildIdentity.RootPath
    foreach ($argument in @(
            "-NoProfile", "-NonInteractive",
            "-EncodedCommand", $encodedHelper)) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutStream = $null
    try {
        if (-not $process.Start()) {
            throw "$Label verifier failed to start."
        }
        $stdoutStream = [System.IO.MemoryStream]::new()
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($requestJson)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(
                [int]$Manifest.capture.benchmarkTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Label verifier timed out."
        }
        $stdoutTask.GetAwaiter().GetResult()
        $stdoutBytes = $stdoutStream.ToArray()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "$Label verifier exited $($process.ExitCode): $stderr"
        }
        if (-not [string]::IsNullOrEmpty($stderr)) {
            throw "$Label verifier emitted unexpected stderr: $stderr"
        }
        $result = ConvertFrom-QualityMetricVerifierBytes $stdoutBytes $Label
        Assert-ExactPropertyNames $result @("schema", "results") "$Label result"
        if ([string]$result.schema -cne
                "njulf-perf-quality-verify-result/v1" -or
            $null -eq $result.results -or
            $result.results -isnot [System.Object[]] -or
            $result.results.Count -ne $Operations.Count) {
            throw "$Label verifier result topology is invalid."
        }
        for ($index = 0; $index -lt $Operations.Count; $index++) {
            $operationResult = @($result.results)[$index]
            $expectedResultProperties = if (
                [string]$Operations[$index].kind -ceq "spatial") {
                @("id", "kind", "value", "inputs")
            } else {
                @("id", "kind", "value")
            }
            Assert-ExactPropertyNames $operationResult `
                $expectedResultProperties "$Label operation $index"
            if ([string]$operationResult.id -cne
                    [string]$Operations[$index].id -or
                [string]$operationResult.kind -cne
                    [string]$Operations[$index].kind) {
                throw "$Label verifier operation $index was reordered."
            }
            if ([string]$operationResult.kind -ceq "temporal") {
                if ($null -eq $operationResult.value) {
                    throw "$Label temporal operation $index has no value."
                }
                Assert-ExactPropertyNames $operationResult.value `
                    @("relativeResidual", "inputs") `
                    "$Label temporal operation $index value"
                if ($null -eq $operationResult.value.inputs -or
                    $operationResult.value.inputs -isnot [System.Object[]] -or
                    $operationResult.value.inputs.Count -ne 4) {
                    throw "$Label temporal operation $index input topology differs."
                }
            } else {
                if ($null -eq $operationResult.value) {
                    throw "$Label spatial operation $index has no value."
                }
                Assert-ExactPropertyNames $operationResult.value @(
                    "Available", "Passed", "ReferencePath", "CandidatePath",
                    "ReferenceSha256", "CandidateSha256", "Width", "Height",
                    "Rmse", "RelativeRmse", "MeanAbsoluteError",
                    "MaximumAbsoluteError", "MaximumRelativeRmse",
                    "FailureReason", "FlipP95", "MaximumFlipP95",
                    "QualityContractPath", "QualityContractSha256",
                    "RoiResults") "$Label spatial operation $index value"
                if ($operationResult.value.Available -isnot [bool] -or
                    $operationResult.value.Passed -isnot [bool] -or
                    $null -eq $operationResult.value.RoiResults -or
                    $operationResult.value.RoiResults -isnot
                        [System.Object[]] -or
                    $null -eq $operationResult.inputs -or
                    $operationResult.inputs -isnot [System.Object[]] -or
                    $operationResult.inputs.Count -ne 3) {
                    throw "$Label spatial operation $index value topology differs."
                }
                foreach ($roi in @($operationResult.value.RoiResults)) {
                    if ($null -eq $roi) {
                        throw "$Label spatial operation $index contains a null ROI."
                    }
                    Assert-ExactPropertyNames $roi @(
                        "Name", "X", "Y", "Width", "Height",
                        "MeanLuminanceShift", "P95LuminanceShift",
                        "MaximumMeanLuminanceShift",
                        "MaximumP95LuminanceShift", "Passed") `
                        "$Label spatial operation $index ROI"
                    if ($roi.Passed -isnot [bool]) {
                        throw "$Label spatial operation $index ROI Passed is not Boolean."
                    }
                }
            }
            $inputs = if ([string]$operationResult.kind -ceq "spatial") {
                @($operationResult.inputs)
            } else {
                @($operationResult.value.inputs)
            }
            foreach ($input in $inputs) {
                if ($null -eq $input) {
                    throw "$Label operation $index contains a null input."
                }
                Assert-ExactPropertyNames $input @("path", "sha256") `
                    "$Label operation $index input"
            }
        }
        Assert-BuildIdentity $BuildIdentity "$Label verifier build"
        Assert-ProtectedFingerprints $script:ProtectedFingerprints
        Assert-CampaignLockIntegrity
        Assert-QualityMetricVerifierInputs $Operations "$Label post-run"
        if ((Get-Sha256 $helperPath) -cne $expectedHelperHash) {
            throw "$Label verifier helper changed during execution."
        }
        $fullArtifactPath = [System.IO.Path]::GetFullPath($ArtifactPath)
        if ($null -eq $ExpectedArtifactEvidence) {
            Write-AtomicByteArtifact $fullArtifactPath $stdoutBytes $Label
            $artifact = [ordered]@{
                artifactPath = $fullArtifactPath
                artifactSha256 = Get-Sha256 $fullArtifactPath
                artifactByteLength = [long]$stdoutBytes.Length
                verifierBuildIdentity = $BuildIdentity
                result = $result
            }
        } else {
            $artifact = $ExpectedArtifactEvidence
            Assert-ExactPropertyNames $artifact @(
                "artifactPath", "artifactSha256", "artifactByteLength",
                "verifierBuildIdentity", "result") "$Label artifact evidence"
            Assert-PathIdentity ([string]$artifact.artifactPath) `
                $fullArtifactPath "$Label artifact"
            if (-not (Test-Path -LiteralPath $fullArtifactPath -PathType Leaf) -or
                (Get-Sha256 $fullArtifactPath) -cne
                    [string]$artifact.artifactSha256) {
                throw "$Label stored verifier artifact bytes differ."
            }
            $storedBytes = [System.IO.File]::ReadAllBytes($fullArtifactPath)
            if ([long]$artifact.artifactByteLength -ne $storedBytes.LongLength -or
                -not (Test-ByteSequenceEqual $storedBytes $stdoutBytes)) {
                throw "$Label verifier replay differs from its durable artifact."
            }
            $storedResult = ConvertFrom-QualityMetricVerifierBytes `
                $storedBytes "$Label stored"
            if (($storedResult | ConvertTo-Json -Depth 32 -Compress) -cne
                    ($result | ConvertTo-Json -Depth 32 -Compress) -or
                ($artifact.result | ConvertTo-Json -Depth 32 -Compress) -cne
                    ($result | ConvertTo-Json -Depth 32 -Compress) -or
                ($artifact.verifierBuildIdentity |
                    ConvertTo-Json -Depth 12 -Compress) -cne
                    ($BuildIdentity | ConvertTo-Json -Depth 12 -Compress)) {
                throw "$Label stored verifier result/build identity differs."
            }
        }
        return [pscustomobject]@{
            artifact = $artifact
            results = @($result.results)
        }
    } finally {
        if ($null -ne $stdoutStream) { $stdoutStream.Dispose() }
        $process.Dispose()
    }
}

function Assert-QualityMetricEqual {
    param($Actual, $Expected, [string]$Label)
    $actualValue = Assert-FiniteNumber $Actual $Label
    $expectedValue = Assert-FiniteNumber $Expected "$Label recomputed"
    if ($actualValue -ne $expectedValue) {
        throw "$Label differs from the protected verifier ($actualValue vs $expectedValue)."
    }
    return $actualValue
}

function Get-RecomputedQualitySequenceMetrics {
    param(
        $Manifest,
        $Workload,
        $Report,
        $ReferenceContract,
        $VerifierBuildIdentity,
        [string]$QualityContractPath,
        [string]$ArtifactPath,
        $ExpectedArtifactEvidence,
        [string]$Label)
    $reportCheckpoints = @($Report.Checkpoints)
    $referenceCheckpoints = @($ReferenceContract.checkpoints)
    if ($reportCheckpoints.Count -ne $referenceCheckpoints.Count) {
        throw "$Label cannot recompute an incomplete checkpoint set."
    }
    $operations = @()
    for ($index = 0; $index -lt $reportCheckpoints.Count; $index++) {
        $operations += [ordered]@{
            id = "spatial-$index"
            kind = "spatial"
            referencePath = [string]$referenceCheckpoints[$index].pfmPath
            referenceSha256 = [string]$referenceCheckpoints[$index].pfmSha256
            candidatePath = [string]$reportCheckpoints[$index].PfmPath
            candidateSha256 = [string]$reportCheckpoints[$index].PfmSha256
            maximumRelativeRmse = [double]$Manifest.quality.maximumRelativeRmse
            maximumFlipP95 = [double]$Manifest.quality.maximumFlipP95
            qualityContractPath = $QualityContractPath
            qualityContractSha256 = [string]$ReferenceContract.qualityContractSha256
        }
    }
    $pairs = @(Get-QualitySequenceTemporalPairs ([string]$Workload.qualityTrajectory))
    $ordinalByFrame = @{}
    for ($index = 0; $index -lt $referenceCheckpoints.Count; $index++) {
        $ordinalByFrame[[int]$referenceCheckpoints[$index].routeFrameIndex] = $index
    }
    for ($index = 0; $index -lt $pairs.Count; $index++) {
        $fromOrdinal = [int]$ordinalByFrame[
            [int]$pairs[$index].fromRouteFrameIndex]
        $toOrdinal = [int]$ordinalByFrame[
            [int]$pairs[$index].toRouteFrameIndex]
        $operations += [ordered]@{
            id = "temporal-$index"
            kind = "temporal"
            referenceFromPath = [string]$referenceCheckpoints[$fromOrdinal].pfmPath
            referenceFromSha256 = [string]$referenceCheckpoints[$fromOrdinal].pfmSha256
            referenceToPath = [string]$referenceCheckpoints[$toOrdinal].pfmPath
            referenceToSha256 = [string]$referenceCheckpoints[$toOrdinal].pfmSha256
            candidateFromPath = [string]$reportCheckpoints[$fromOrdinal].PfmPath
            candidateFromSha256 = [string]$reportCheckpoints[$fromOrdinal].PfmSha256
            candidateToPath = [string]$reportCheckpoints[$toOrdinal].PfmPath
            candidateToSha256 = [string]$reportCheckpoints[$toOrdinal].PfmSha256
        }
    }
    $verification = Invoke-QualityMetricVerifier `
        $Manifest $VerifierBuildIdentity $operations $ArtifactPath `
        $ExpectedArtifactEvidence "$Label protected metric verification"
    $verified = @($verification.results)
    $spatial = @()
    for ($index = 0; $index -lt $reportCheckpoints.Count; $index++) {
        $difference = $reportCheckpoints[$index].HdrDifference
        $recomputed = $verified[$index].value
        $operation = $operations[$index]
        $verifiedInputs = @($verified[$index].inputs)
        if ($verifiedInputs.Count -ne 3) {
            throw "$Label checkpoint $index verifier input topology differs."
        }
        for ($inputIndex = 0; $inputIndex -lt 3; $inputIndex++) {
            $expectedPath = @(
                [string]$operation.referencePath,
                [string]$operation.candidatePath,
                [string]$operation.qualityContractPath)[$inputIndex]
            $expectedSha = @(
                [string]$operation.referenceSha256,
                [string]$operation.candidateSha256,
                [string]$operation.qualityContractSha256)[$inputIndex]
            if (-not [string]::Equals(
                    [System.IO.Path]::GetFullPath(
                        [string]$verifiedInputs[$inputIndex].path),
                    [System.IO.Path]::GetFullPath($expectedPath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                [string]$verifiedInputs[$inputIndex].sha256 -cne $expectedSha) {
                throw "$Label checkpoint $index verifier input $inputIndex differs."
            }
        }
        if (-not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$recomputed.ReferencePath),
                [System.IO.Path]::GetFullPath([string]$operation.referencePath),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$recomputed.CandidatePath),
                [System.IO.Path]::GetFullPath([string]$operation.candidatePath),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$recomputed.QualityContractPath),
                [System.IO.Path]::GetFullPath($QualityContractPath),
                [StringComparison]::OrdinalIgnoreCase) -or
            [int]$recomputed.Width -ne 1920 -or
            [int]$recomputed.Height -ne 1080 -or
            [double]$recomputed.MaximumRelativeRmse -ne
                [double]$Manifest.quality.maximumRelativeRmse -or
            [double]$recomputed.MaximumFlipP95 -ne
                [double]$Manifest.quality.maximumFlipP95) {
            throw "$Label checkpoint $index verifier output provenance differs."
        }
        foreach ($name in @(
                "Rmse", "RelativeRmse", "MeanAbsoluteError",
                "MaximumAbsoluteError", "FlipP95")) {
            $null = Assert-QualityMetricEqual `
                $difference.$name $recomputed.$name `
                "$Label checkpoint $index $name"
        }
        if ([bool]$difference.Available -ne [bool]$recomputed.Available -or
            [bool]$difference.Passed -ne [bool]$recomputed.Passed -or
            [string]$difference.ReferenceSha256 -cne
                [string]$recomputed.ReferenceSha256 -or
            [string]$difference.CandidateSha256 -cne
                [string]$recomputed.CandidateSha256 -or
            [string]$difference.QualityContractSha256 -cne
                [string]$recomputed.QualityContractSha256 -or
            [string]$difference.FailureReason -cne
                [string]$recomputed.FailureReason) {
            throw "$Label checkpoint $index spatial verifier provenance differs."
        }
        $reportedRois = @($difference.RoiResults)
        $verifiedRois = @($recomputed.RoiResults)
        if ($reportedRois.Count -ne $verifiedRois.Count) {
            throw "$Label checkpoint $index ROI verifier topology differs."
        }
        $roiEvidence = @()
        for ($roiIndex = 0; $roiIndex -lt $reportedRois.Count; $roiIndex++) {
            $reportedRoi = $reportedRois[$roiIndex]
            $verifiedRoi = $verifiedRois[$roiIndex]
            foreach ($name in @(
                    "Name", "X", "Y", "Width", "Height",
                    "MaximumMeanLuminanceShift",
                    "MaximumP95LuminanceShift", "Passed")) {
                if ([string]$reportedRoi.$name -cne
                    [string]$verifiedRoi.$name) {
                    throw "$Label checkpoint $index ROI $roiIndex $name differs from verifier."
                }
            }
            $mean = Assert-QualityMetricEqual `
                $reportedRoi.MeanLuminanceShift `
                $verifiedRoi.MeanLuminanceShift `
                "$Label checkpoint $index ROI $roiIndex mean"
            $p95 = Assert-QualityMetricEqual `
                $reportedRoi.P95LuminanceShift `
                $verifiedRoi.P95LuminanceShift `
                "$Label checkpoint $index ROI $roiIndex P95"
            $roiEvidence += [pscustomobject]@{
                name = [string]$reportedRoi.Name
                meanLuminanceShift = $mean
                p95LuminanceShift = $p95
            }
        }
        $spatial += [pscustomobject]@{
            ordinal = $index
            routeFrameIndex = [int]$reportCheckpoints[$index].RouteFrameIndex
            relativeRmse = [double]$recomputed.RelativeRmse
            flipP95 = [double]$recomputed.FlipP95
            rois = @($roiEvidence)
        }
    }
    $temporal = @()
    $reportedTemporal = @($Report.TemporalResiduals)
    if ($reportedTemporal.Count -ne $pairs.Count) {
        throw "$Label temporal report topology differs during recomputation."
    }
    for ($index = 0; $index -lt $pairs.Count; $index++) {
        $verifiedTemporal = $verified[$reportCheckpoints.Count + $index].value
        $verifiedInputs = @($verifiedTemporal.inputs)
        $operation = $operations[$reportCheckpoints.Count + $index]
        if ($verifiedInputs.Count -ne 4) {
            throw "$Label temporal pair $index verifier input topology differs."
        }
        for ($inputIndex = 0; $inputIndex -lt 4; $inputIndex++) {
            $pathName = @(
                "referenceFromPath", "referenceToPath",
                "candidateFromPath", "candidateToPath")[$inputIndex]
            $shaName = @(
                "referenceFromSha256", "referenceToSha256",
                "candidateFromSha256", "candidateToSha256")[$inputIndex]
            if (-not [string]::Equals(
                    [System.IO.Path]::GetFullPath(
                        [string]$verifiedInputs[$inputIndex].path),
                    [System.IO.Path]::GetFullPath(
                        [string]$operation.$pathName),
                    [StringComparison]::OrdinalIgnoreCase) -or
                [string]$verifiedInputs[$inputIndex].sha256 -cne
                    [string]$operation.$shaName) {
                throw "$Label temporal pair $index verifier input $inputIndex differs."
            }
        }
        $recomputed = Assert-QualityMetricEqual `
            $reportedTemporal[$index].RelativeResidual `
            $verifiedTemporal.relativeResidual `
            "$Label temporal pair $index"
        $temporal += [pscustomobject]@{
            fromRouteFrameIndex = [int]$pairs[$index].fromRouteFrameIndex
            toRouteFrameIndex = [int]$pairs[$index].toRouteFrameIndex
            relativeResidual = $recomputed
        }
    }
    return [pscustomobject]@{
        metricVerifierArtifact = $verification.artifact
        spatial = @($spatial)
        temporal = @($temporal)
    }
}

function New-QualitySequenceSpatialEnvelope {
    param($Manifest, $Workload, [object[]]$VerifiedRepeatMetrics)
    if ($VerifiedRepeatMetrics.Count -ne 2) {
        throw "Spatial repeatability envelope requires exactly two verified repeats."
    }
    $checkpointIndices = @(Get-QualitySequenceCheckpointIndices (
        [string]$Workload.qualityTrajectory))
    $envelope = @()
    for ($checkpoint = 0; $checkpoint -lt $checkpointIndices.Count; $checkpoint++) {
        $repeatValues = @($VerifiedRepeatMetrics | ForEach-Object {
            @($_.spatial)[$checkpoint]
        })
        $roiCount = @($repeatValues[0].rois).Count
        $rois = @()
        for ($roi = 0; $roi -lt $roiCount; $roi++) {
            $name = [string]@($repeatValues[0].rois)[$roi].name
            if ([string]@($repeatValues[1].rois)[$roi].name -cne $name) {
                throw "Spatial repeat ROI topology changed."
            }
            $rois += [pscustomobject]@{
                name = $name
                maximumMeanLuminanceShift = [Math]::Max(
                    [double]@($repeatValues[0].rois)[$roi].meanLuminanceShift,
                    [double]@($repeatValues[1].rois)[$roi].meanLuminanceShift)
                maximumP95LuminanceShift = [Math]::Max(
                    [double]@($repeatValues[0].rois)[$roi].p95LuminanceShift,
                    [double]@($repeatValues[1].rois)[$roi].p95LuminanceShift)
            }
        }
        $envelope += [pscustomobject]@{
            ordinal = $checkpoint
            routeFrameIndex = [int]$checkpointIndices[$checkpoint]
            maximumRelativeRmse = [Math]::Max(
                [double]$repeatValues[0].relativeRmse,
                [double]$repeatValues[1].relativeRmse)
            maximumFlipP95 = [Math]::Max(
                [double]$repeatValues[0].flipP95,
                [double]$repeatValues[1].flipP95)
            rois = @($rois)
        }
    }
    return @($envelope)
}

function Assert-QualitySequenceSpatialEnvelope {
    param($VerifiedMetrics, [object[]]$Envelope, [string]$Label)
    $spatial = @($VerifiedMetrics.spatial)
    if ($spatial.Count -ne $Envelope.Count) {
        throw "$Label spatial envelope topology differs."
    }
    for ($index = 0; $index -lt $Envelope.Count; $index++) {
        if ([int]$spatial[$index].ordinal -ne [int]$Envelope[$index].ordinal -or
            [int]$spatial[$index].routeFrameIndex -ne
                [int]$Envelope[$index].routeFrameIndex -or
            [double]$spatial[$index].relativeRmse -gt
                [double]$Envelope[$index].maximumRelativeRmse -or
            [double]$spatial[$index].flipP95 -gt
                [double]$Envelope[$index].maximumFlipP95) {
            throw "$Label checkpoint $index exceeds baseline spatial repeatability."
        }
        $actualRois = @($spatial[$index].rois)
        $expectedRois = @($Envelope[$index].rois)
        if ($actualRois.Count -ne $expectedRois.Count) {
            throw "$Label checkpoint $index ROI envelope topology differs."
        }
        for ($roi = 0; $roi -lt $actualRois.Count; $roi++) {
            if ([string]$actualRois[$roi].name -cne
                    [string]$expectedRois[$roi].name -or
                [double]$actualRois[$roi].meanLuminanceShift -gt
                    [double]$expectedRois[$roi].maximumMeanLuminanceShift -or
                [double]$actualRois[$roi].p95LuminanceShift -gt
                    [double]$expectedRois[$roi].maximumP95LuminanceShift) {
                throw "$Label checkpoint $index ROI $roi exceeds baseline repeatability."
            }
        }
    }
}

function Get-BenchmarkArguments {
    param(
        $Manifest,
        $Workload,
        [string]$ReportPath,
        [string]$HealthPath,
        [string]$PairId,
        [string]$QualityContractPath,
        [string]$ReferencePath,
        [bool]$ReferenceInitialization
    )
    $arguments = @(
        "--benchmark",
        "--benchmark-report", $ReportPath,
        "--health-report", $HealthPath,
        "--benchmark-warmup-frames", ([int]$Workload.warmupFrames).ToString(),
        "--benchmark-measure-frames", ([int]$Workload.measureFrames).ToString(),
        "--benchmark-max-settle-frames", ([int]$Manifest.capture.maximumSettlingFrames).ToString(),
        "--benchmark-pair-id", $PairId,
        "--benchmark-variant", ([string]$Workload.captureVariant),
        "--benchmark-trajectory", ([string]$Workload.trajectory),
        "--benchmark-activation", ([string]$Workload.activation),
        "--benchmark-budget-profile", ([string]$Manifest.capture.budgetProfile),
        "--scene", ([string]$Workload.scene),
        "--performance-scenario", ([string]$Workload.scenario),
        "--quality-preset", "ddgi-high",
        "--validation", "off",
        "--gpu-timing")
    $bistroVariant = [string](Get-PropertyValue $Workload "bistroQualityVariant" "")
    if (-not [string]::IsNullOrWhiteSpace($bistroVariant)) {
        $arguments += @("--bistro-quality-variant", $bistroVariant)
    }
    foreach ($argument in @((Get-PropertyValue $Workload "arguments" @()))) {
        $arguments += [string]$argument
    }
    $candidatePath = [System.IO.Path]::ChangeExtension($ReportPath, ".hdr.pfm")
    if ($ReferenceInitialization) {
        $arguments += @("--benchmark-hdr-candidate", $ReferencePath)
    } else {
        $arguments += @(
            "--benchmark-hdr-reference", $ReferencePath,
            "--benchmark-hdr-candidate", $candidatePath,
            "--benchmark-hdr-max-relative-rmse", ([double]$Manifest.quality.maximumRelativeRmse).ToString([Globalization.CultureInfo]::InvariantCulture),
            "--benchmark-hdr-max-flip-p95", ([double]$Manifest.quality.maximumFlipP95).ToString([Globalization.CultureInfo]::InvariantCulture),
            "--benchmark-hdr-quality-contract", $QualityContractPath,
            "--benchmark-require-production")
    }
    return $arguments
}

function Read-BenchmarkReport {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Benchmark report was not written: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw |
        ConvertFrom-Json -DateKind String
}

function Assert-CaptureInputHashes {
    param(
        $BuildIdentity,
        [string]$QualityContractPath,
        [string]$ExpectedQualityContractSha256,
        [string]$ReferencePath,
        [string]$ExpectedReferenceSha256,
        [bool]$ReferenceInitialization,
        [string]$Label)
    Assert-BuildIdentity $BuildIdentity $Label
    if (-not (Test-Path -LiteralPath $QualityContractPath -PathType Leaf) -or
        -not [string]::Equals(
            (Get-Sha256 $QualityContractPath),
            $ExpectedQualityContractSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label quality contract changed or is missing."
    }
    if (-not $ReferenceInitialization) {
        if (-not (Test-Path -LiteralPath $ReferencePath -PathType Leaf) -or
            -not [string]::Equals(
                (Get-Sha256 $ReferencePath),
                $ExpectedReferenceSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label HDR reference changed or is missing."
        }
        Assert-LinearHdrPfm $ReferencePath 1920 1080 "$Label reference"
    }
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
}

function Assert-TimingStats {
    param(
        $Stats,
        [int]$MaximumOrExactCount,
        [bool]$RequireExactPositive,
        [string]$Label)
    if ($null -eq $Stats) {
        throw "$Label timing statistics are missing."
    }
    $count = [int]$Stats.Count
    $values = @(
        [double]$Stats.AverageMilliseconds,
        [double]$Stats.MinMilliseconds,
        [double]$Stats.MaxMilliseconds,
        [double]$Stats.MedianMilliseconds,
        [double]$Stats.P50Milliseconds,
        [double]$Stats.P95Milliseconds,
        [double]$Stats.P99Milliseconds)
    if (($RequireExactPositive -and $count -ne $MaximumOrExactCount) -or
        (-not $RequireExactPositive -and
         ($count -lt 0 -or $count -gt $MaximumOrExactCount)) -or
        @($values | Where-Object {
            -not [double]::IsFinite($_) -or $_ -lt 0.0
        }).Count -ne 0) {
        throw "$Label timing count/value domain is invalid."
    }
    if ($count -eq 0) {
        if (@($values | Where-Object { $_ -ne 0.0 }).Count -ne 0) {
            throw "$Label empty timing statistics must be exactly zero."
        }
        return
    }
    if (($RequireExactPositive -and
         @($values | Where-Object { $_ -le 0.0 }).Count -ne 0) -or
        [double]$Stats.MinMilliseconds -gt [double]$Stats.AverageMilliseconds -or
        [double]$Stats.AverageMilliseconds -gt [double]$Stats.MaxMilliseconds -or
        [double]$Stats.MinMilliseconds -gt [double]$Stats.MedianMilliseconds -or
        [double]$Stats.MedianMilliseconds -gt [double]$Stats.MaxMilliseconds -or
        [double]$Stats.MinMilliseconds -gt [double]$Stats.P50Milliseconds -or
        [double]$Stats.P50Milliseconds -gt [double]$Stats.P95Milliseconds -or
        [double]$Stats.P95Milliseconds -gt [double]$Stats.P99Milliseconds -or
        [double]$Stats.P99Milliseconds -gt [double]$Stats.MaxMilliseconds) {
        throw "$Label timing percentiles are incoherent."
    }
}

function Assert-BenchmarkReport {
    param(
        $Manifest,
        $Workload,
        $Report,
        [string]$Configuration,
        [string]$Label,
        [bool]$ReferenceInitialization,
        [string]$ExpectedPairId,
        [string]$ExpectedCommit,
        $BuildIdentity,
        $ReferenceIdentity,
        [string]$ExpectedCandidatePath
    )
    if ([string]$Report.Kind -ne "njulf-renderer-benchmark" -or
        [string]$Report.Schema -ne "njulf-renderer-benchmark/v4") {
        throw "$Label has unexpected report kind/schema '$($Report.Kind)'/'$($Report.Schema)'."
    }
    if ([int]$Report.MeasurementFrameCount -ne [int]$Workload.measureFrames) {
        throw "$Label measured $($Report.MeasurementFrameCount) frames, expected $($Workload.measureFrames)."
    }
    $firstMeasurementFrame = [int]$Report.FirstMeasurementFrameIndex
    $lastMeasurementFrame = [int]$Report.LastMeasurementFrameIndex
    $warmupFrameCount = [int]$Workload.warmupFrames
    $settlingFrameCount = [int]$Report.AdditionalSettlingFrameCount
    $measurementStartDelta = $firstMeasurementFrame - $warmupFrameCount
    if ([int]$Report.WarmupFrameCount -ne $warmupFrameCount -or
        [int]$Report.Options.WarmupFrameCount -ne $warmupFrameCount -or
        [int]$Report.Options.MeasureFrameCount -ne [int]$Workload.measureFrames -or
        $firstMeasurementFrame -lt $warmupFrameCount -or
        $lastMeasurementFrame -ne
            ($firstMeasurementFrame + [int]$Workload.measureFrames - 1) -or
        $settlingFrameCount -lt 0 -or
        $settlingFrameCount -gt [int]$Manifest.capture.maximumSettlingFrames -or
        $measurementStartDelta -lt $settlingFrameCount -or
        $measurementStartDelta -gt ($settlingFrameCount + 1)) {
        throw "$Label has incoherent warmup/settling/measurement frame indices."
    }
    if ([bool]$Report.SettlingWaitTimedOut) {
        throw "$Label exhausted the deterministic settling/alignment window."
    }
    if ($null -eq $Report.CaptureContract -or -not [bool]$Report.CaptureContract.Comparable) {
        throw "$Label is not comparable: $(@($Report.CaptureContract.Mismatches) -join '; ')"
    }
    if (-not (Test-Sha256Identity ([string]$Report.CaptureContract.IdentityHash)) -or
        -not (Test-Sha256Identity ([string]$Report.CaptureContract.FullIdentityHash)) -or
        $null -eq $Report.CaptureContract.Mismatches -or
        (Get-ItemCount $Report.CaptureContract.Mismatches) -ne 0) {
        throw "$Label lacks exact comparable capture identity hashes."
    }
    if (-not [string]::Equals(
            [string]$Report.CaptureContract.PairId,
            $ExpectedPairId,
            [StringComparison]::Ordinal)) {
        throw "$Label reported pair '$($Report.CaptureContract.PairId)', expected '$ExpectedPairId'."
    }
    if (-not [string]::Equals(
            [string]$Report.CaptureContract.Variant,
            [string]$Workload.captureVariant,
            [StringComparison]::Ordinal)) {
        throw "$Label reported capture variant '$($Report.CaptureContract.Variant)', expected '$($Workload.captureVariant)'."
    }
    if (-not [bool]$Report.CaptureContract.ProductionTiming) {
        throw "$Label is not a production timing capture."
    }
    $expectedTrajectoryFrameCount = switch ([string]$Workload.trajectory) {
        "bistro-loop" { 240 }
        "sponza-horizontal" { 300 }
        "sponza-vertical" { 960 }
        default { 1 }
    }
    if ([string]$Report.CaptureContract.Trajectory -ne [string]$Workload.trajectory -or
        [int]$Report.CaptureContract.TrajectoryFrameCount -ne $expectedTrajectoryFrameCount -or
        -not (Test-Sha256Identity ([string]$Report.CaptureContract.TrajectoryFingerprint)) -or
        -not (Test-Sha256Identity ([string]$Report.CaptureContract.TrajectoryRouteHash)) -or
        -not (Test-Sha256Identity ([string]$Report.CaptureContract.TrajectorySequenceHash))) {
        throw "$Label lacks the expected deterministic trajectory identity."
    }
    if ($null -ne $ReferenceIdentity -and
        (-not [string]::Equals(
                [string]$Report.CaptureContract.Trajectory,
                [string]$ReferenceIdentity.trajectory,
                [StringComparison]::Ordinal) -or
         -not [string]::Equals(
                [string]$Report.CaptureContract.TrajectoryFingerprint,
                [string]$ReferenceIdentity.trajectoryFingerprint,
                [StringComparison]::Ordinal) -or
         [int]$Report.CaptureContract.TrajectoryFrameCount -ne
                [int]$ReferenceIdentity.trajectoryFrameCount -or
         -not [string]::Equals(
                [string]$Report.CaptureContract.TrajectoryRouteHash,
                [string]$ReferenceIdentity.trajectoryRouteHash,
                [StringComparison]::Ordinal))) {
        throw "$Label trajectory identity differs from the immutable reference."
    }
    $diagnostics = $Report.LastDiagnostics
    $captureRun = $diagnostics.CaptureRun
    if ([int]$diagnostics.CaptureRenderWidth -ne 1920 -or
        [int]$diagnostics.CaptureRenderHeight -ne 1080) {
        throw "$Label reports $($diagnostics.CaptureRenderWidth)x$($diagnostics.CaptureRenderHeight); performance timing requires 1920x1080."
    }
    if (-not [string]::Equals(
            [string]$captureRun.SceneKind,
            [string]$Workload.scene,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$captureRun.Scenario,
            [string]$Workload.scenario,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label reports unexpected scene/scenario provenance."
    }
    $buildConfiguration = ([string]$diagnostics.CaptureRun.BuildConfiguration).Split(';', 2)[0].Trim()
    if (-not [string]::Equals($buildConfiguration, $Configuration, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label reports build '$buildConfiguration', expected '$Configuration'."
    }
    if (-not [string]::Equals(
            [string]$captureRun.Commit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$captureRun.DirtyWorktreeState,
            "clean",
            [StringComparison]::Ordinal) -or
        -not (Test-Sha256Identity ([string]$captureRun.ExecutableHash)) -or
        -not (Test-Sha256Identity ([string]$captureRun.ShaderBundleHash)) -or
        -not (Test-CanonicalIdentityText ([string]$captureRun.ApplicationVersion)) -or
        [int]$captureRun.SettingsSchemaVersion -le 0) {
        throw "$Label has invalid commit, clean-state, executable, or shader provenance."
    }
    if ($null -eq $BuildIdentity -or
        -not [string]::Equals(
            [string]$captureRun.ExecutableHash,
            [string]$BuildIdentity.RuntimeExecutableBundleHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label executable provenance differs from the frozen build bundle."
    }
    $producer = $Report.ProducerIdentity
    $expectedQualityTier = Get-ExpectedQualityTier (
        [string]$Manifest.capture.budgetProfile)
    if (-not [string]::Equals(
            [string]$Report.ProducerIdentity.BuildCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$Report.ProducerIdentity.ShaderFingerprint,
            ([string]$captureRun.ShaderBundleHash).Substring(7),
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$producer.SettingsFingerprint -notmatch '^[0-9a-f]{64}$' -or
        [string]$producer.Schema -ne "material-gi-producer-identity/v1" -or
        -not (Test-CanonicalIdentityText ([string]$producer.GpuName)) -or
        -not (Test-CanonicalIdentityText ([string]$producer.DriverVersion)) -or
        [string]$producer.QualityTier -ne $expectedQualityTier -or
        (Get-ItemCount $producer.SourceSettingsFingerprints) -ne 1 -or
        [string]@($producer.SourceSettingsFingerprints)[0] -ne
            [string]$producer.SettingsFingerprint) {
        throw "$Label has invalid producer identity."
    }
    if ($null -ne $ReferenceIdentity) {
        $referenceProducer = $ReferenceIdentity.producerIdentity
        if ([string]$producer.Schema -ne [string]$referenceProducer.schema -or
            [string]$producer.SettingsFingerprint -ne
                [string]$referenceProducer.settingsFingerprint -or
            [string]$producer.GpuName -ne [string]$referenceProducer.gpuName -or
            [string]$producer.DriverVersion -ne
                [string]$referenceProducer.driverVersion -or
            [string]$producer.QualityTier -ne
                [string]$referenceProducer.qualityTier) {
            throw "$Label producer environment/settings differ from the immutable reference."
        }
    }
    if ([int]$diagnostics.DdgiDetailedCountersCompiled -ne 0 -or
        [int]$diagnostics.DdgiDetailedCountersEnabled -ne 0 -or
        [int]$diagnostics.DdgiDetailedCountersRequested -ne 0 -or
        [bool]$diagnostics.GiMeasurement.DetailedCountersReadbackValid) {
        throw "$Label contains detailed DDGI diagnostics in a production timing run."
    }
    if ([int]$Report.GpuTimingSupported -ne 1 -or
        [int]$Report.GpuTimingValidSampleCount -ne [int]$Workload.measureFrames -or
        [int]$Report.GpuFrameMilliseconds.Count -ne [int]$Workload.measureFrames -or
        [int]$Report.CpuFrameMilliseconds.Count -ne [int]$Workload.measureFrames) {
        throw "$Label lacks complete GPU timing."
    }
    Assert-TimingStats `
        $Report.CpuFrameMilliseconds ([int]$Workload.measureFrames) `
        $true "$Label CPU frame"
    Assert-TimingStats `
        $Report.GpuFrameMilliseconds ([int]$Workload.measureFrames) `
        $true "$Label GPU frame"
    foreach ($collection in @(
            [pscustomobject]@{
                Items = @($Report.GpuPasses)
                Label = "$Label GPU pass"
            },
            [pscustomobject]@{
                Items = @($Report.CpuStages)
                Label = "$Label CPU stage"
            })) {
        $names = [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($timing in @($collection.Items)) {
            if ([string]::IsNullOrWhiteSpace([string]$timing.Name) -or
                -not $names.Add([string]$timing.Name)) {
                throw "$($collection.Label) timing names are empty or duplicated."
            }
            Assert-TimingStats `
                $timing ([int]$Workload.measureFrames) $false `
                "$($collection.Label) '$($timing.Name)'"
        }
    }
    if (-not $ReferenceInitialization) {
        if ($null -eq $ReferenceIdentity) {
            throw "$Label has no immutable reference identity."
        }
        if ($null -eq $Report.HdrDifference -or
            -not [bool]$Report.HdrDifference.Available -or
            -not [bool]$Report.HdrDifference.Passed) {
            throw "$Label failed strict HDR quality: $($Report.HdrDifference.FailureReason)"
        }
        if ([int]$Report.HdrDifference.Width -ne 1920 -or
            [int]$Report.HdrDifference.Height -ne 1080 -or
            [string]$Report.HdrDifference.CandidateSha256 -notmatch
                '^[0-9a-f]{64}$') {
            throw "$Label has invalid 1920x1080 linear-HDR evidence."
        }
        if ([double]$Report.HdrDifference.MaximumRelativeRmse -ne
                [double]$Manifest.quality.maximumRelativeRmse -or
            [double]$Report.HdrDifference.MaximumFlipP95 -ne
                [double]$Manifest.quality.maximumFlipP95) {
            throw "$Label used unexpected HDR quality thresholds."
        }
        $expectedRoiCount = Get-ItemCount (
            Get-PropertyValue $Workload "qualityRois" @())
        if ($expectedRoiCount -eq 0) { $expectedRoiCount = 1 }
        if ((Get-ItemCount $Report.HdrDifference.RoiResults) -ne $expectedRoiCount -or
            @($Report.HdrDifference.RoiResults | Where-Object { -not [bool]$_.Passed }).Count -ne 0) {
            throw "$Label lacks passing named-ROI evidence."
        }
        $expectedReferencePath = [System.IO.Path]::GetFullPath(
            [string]$ReferenceIdentity.path)
        $expectedContractPath = [System.IO.Path]::GetFullPath(
            [string]$ReferenceIdentity.qualityContractPath)
        if (-not [string]::Equals(
                [System.IO.Path]::GetFullPath(
                    [string]$Report.HdrDifference.ReferencePath),
                $expectedReferencePath,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath(
                    [string]$Report.HdrDifference.CandidatePath),
                [System.IO.Path]::GetFullPath($ExpectedCandidatePath),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath(
                    [string]$Report.HdrDifference.QualityContractPath),
                $expectedContractPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label HDR evidence used an unexpected reference, candidate, or ROI contract path."
        }
        if (-not [string]::Equals(
                [string]$Report.HdrDifference.ReferenceSha256,
                [string]$ReferenceIdentity.sha256,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [string]$Report.HdrDifference.QualityContractSha256,
                [string]$ReferenceIdentity.qualityContractSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label HDR evidence hashes do not match the immutable reference lock."
        }
        Assert-LinearHdrPfm $ExpectedCandidatePath 1920 1080 "$Label candidate"
        $candidateSha256 = Get-Sha256 $ExpectedCandidatePath
        if (-not [string]::Equals(
                $candidateSha256,
                [string]$Report.HdrDifference.CandidateSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label candidate PFM hash differs from the report."
        }
        $qualityContract = Get-Content -LiteralPath $expectedContractPath -Raw |
            ConvertFrom-Json -DateKind String
        if ([string]$qualityContract.schema -ne "njulf-benchmark-hdr-quality/v1" -or
            [int]$qualityContract.width -ne 1920 -or
            [int]$qualityContract.height -ne 1080 -or
            (Get-ItemCount $qualityContract.rois) -ne $expectedRoiCount) {
            throw "$Label ROI quality contract has unexpected topology."
        }
        for ($roiIndex = 0; $roiIndex -lt $expectedRoiCount; $roiIndex++) {
            $expectedRoi = @($qualityContract.rois)[$roiIndex]
            $actualRoi = @($Report.HdrDifference.RoiResults)[$roiIndex]
            if ([string]$actualRoi.Name -ne [string]$expectedRoi.name -or
                [int]$actualRoi.X -ne [int]$expectedRoi.x -or
                [int]$actualRoi.Y -ne [int]$expectedRoi.y -or
                [int]$actualRoi.Width -ne [int]$expectedRoi.width -or
                [int]$actualRoi.Height -ne [int]$expectedRoi.height -or
                [double]$actualRoi.MaximumMeanLuminanceShift -ne
                    [double]$expectedRoi.maximumMeanLuminanceShift -or
                [double]$actualRoi.MaximumP95LuminanceShift -ne
                    [double]$expectedRoi.maximumP95LuminanceShift) {
                throw "$Label ROI result $roiIndex does not match its locked contract."
            }
        }
    }
}

function Assert-HealthReport {
    param(
        $Manifest,
        $Workload,
        $Health,
        $Report,
        $BuildIdentity,
        [string]$ExpectedCommit,
        [string]$ExpectedPairId,
        [string]$Label)
    if ([string]$Health.kind -ne "renderer-health" -or
        [string]$Health.schema -ne "renderer-health/v3") {
        throw "$Label has an unexpected health-report contract."
    }
    $failure = [string](Get-PropertyValue $Health "failure" "")
    if ([string]$Health.status -ne "passed" -or
        -not [string]::IsNullOrWhiteSpace($failure)) {
        throw "$Label health gate failed: $failure"
    }
    if ($null -eq $Health.producerIdentity -or
        $null -eq $Health.diagnostics -or
        $null -eq $Health.diagnostics.CaptureRun) {
        throw "$Label health report lacks producer or capture provenance."
    }
    $producer = $Health.producerIdentity
    $reportProducer = $Report.ProducerIdentity
    $healthCaptureRun = $Health.diagnostics.CaptureRun
    $reportCaptureRun = $Report.LastDiagnostics.CaptureRun
    $pairs = @(
        @("producer schema", [string]$producer.schema, [string]$reportProducer.Schema),
        @("producer commit", [string]$producer.buildCommit, [string]$reportProducer.BuildCommit),
        @("producer shader", [string]$producer.shaderFingerprint, [string]$reportProducer.ShaderFingerprint),
        @("producer GPU", [string]$producer.gpuName, [string]$reportProducer.GpuName),
        @("producer driver", [string]$producer.driverVersion, [string]$reportProducer.DriverVersion),
        @("capture commit", [string]$healthCaptureRun.Commit, [string]$reportCaptureRun.Commit),
        @("capture dirty state", [string]$healthCaptureRun.DirtyWorktreeState, [string]$reportCaptureRun.DirtyWorktreeState),
        @("capture executable", [string]$healthCaptureRun.ExecutableHash, [string]$reportCaptureRun.ExecutableHash),
        @("capture shaders", [string]$healthCaptureRun.ShaderBundleHash, [string]$reportCaptureRun.ShaderBundleHash),
        @("capture application version", [string]$healthCaptureRun.ApplicationVersion, [string]$reportCaptureRun.ApplicationVersion),
        @("capture settings schema", [string]$healthCaptureRun.SettingsSchemaVersion, [string]$reportCaptureRun.SettingsSchemaVersion),
        @("capture scene", [string]$healthCaptureRun.SceneKind, [string]$reportCaptureRun.SceneKind),
        @("capture scenario", [string]$healthCaptureRun.Scenario, [string]$reportCaptureRun.Scenario))
    foreach ($pair in $pairs) {
        if (-not [string]::Equals(
                [string]$pair[1],
                [string]$pair[2],
                [StringComparison]::Ordinal)) {
            throw "$Label health and benchmark reports differ in $($pair[0])."
        }
    }
    if (-not [string]::Equals(
            [string]$producer.buildCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$healthCaptureRun.ExecutableHash,
            [string]$BuildIdentity.RuntimeExecutableBundleHash,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$healthCaptureRun.DirtyWorktreeState,
            "clean",
            [StringComparison]::Ordinal) -or
        [string]$producer.schema -ne "material-gi-producer-identity/v1" -or
        [string]$producer.settingsFingerprint -notmatch '^[0-9a-f]{64}$' -or
        -not (Test-CanonicalIdentityText ([string]$producer.gpuName)) -or
        -not (Test-CanonicalIdentityText ([string]$producer.driverVersion)) -or
        -not [string]::IsNullOrEmpty([string]$producer.qualityTier) -or
        -not [string]::Equals(
            [string]$producer.shaderFingerprint,
            ([string]$healthCaptureRun.ShaderBundleHash).Substring(7),
            [StringComparison]::OrdinalIgnoreCase) -or
        [int]$Health.diagnostics.CaptureRenderWidth -ne 1920 -or
        [int]$Health.diagnostics.CaptureRenderHeight -ne 1080 -or
        [int]$Health.options.Benchmark.WarmupFrameCount -ne
            [int]$Workload.warmupFrames -or
        [int]$Health.options.Benchmark.MeasureFrameCount -ne
            [int]$Workload.measureFrames -or
        -not [string]::Equals(
            [string]$Health.options.Benchmark.CapturePairId,
            $ExpectedPairId,
            [StringComparison]::Ordinal)) {
        throw "$Label health report does not match the frozen capture contract."
    }
    $sourceSettings = @($producer.sourceSettingsFingerprints)
    if ($sourceSettings.Count -ne 1 -or
        [string]$sourceSettings[0] -ne [string]$producer.settingsFingerprint) {
        throw "$Label health report has invalid producer source settings."
    }
}

function Invoke-BenchmarkCapture {
    param(
        $Manifest,
        $Workload,
        $BuildIdentity,
        $VerifierBuildIdentity,
        [string]$Configuration,
        [string]$ReportPath,
        [string]$PairId,
        [string]$QualityContractPath,
        [string]$ReferencePath,
        [bool]$ReferenceInitialization,
        [string]$ExpectedCommit,
        [string]$ExpectedQualityContractSha256,
        [string]$ExpectedReferenceSha256,
        $ReferenceIdentity,
        [string]$Label
    )
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
    $healthPath = [System.IO.Path]::ChangeExtension($ReportPath, ".health.json")
    $candidatePath = [System.IO.Path]::ChangeExtension($ReportPath, ".hdr.pfm")
    $activationArtifactPath = [System.IO.Path]::ChangeExtension(
        $ReportPath, ".activation-verification.json")
    $ddgiArtifactPath = [System.IO.Path]::ChangeExtension(
        $ReportPath, ".ddgi-transient-verification.json")
    $reservedOutputs = @(
        $ReportPath, $healthPath, $activationArtifactPath, $ddgiArtifactPath)
    if ($ReferenceInitialization) {
        $reservedOutputs += $ReferencePath
    } else {
        $reservedOutputs += $candidatePath
    }
    if ([string]$Workload.scene -ceq "Sponza") {
        $reservedOutputs +=
            ([System.IO.Path]::GetFullPath($ReportPath) +
                ".sponza-animation.bin")
    }
    foreach ($output in $reservedOutputs) {
        if (Test-Path -LiteralPath $output) {
            throw "$Label output already exists; refusing to overwrite $output"
        }
    }
    $arguments = @(Get-BenchmarkArguments `
        $Manifest $Workload $ReportPath $healthPath $PairId $QualityContractPath `
        $ReferencePath $ReferenceInitialization)
    Assert-CaptureInputHashes `
        $BuildIdentity `
        $QualityContractPath $ExpectedQualityContractSha256 `
        $ReferencePath $ExpectedReferenceSha256 `
        $ReferenceInitialization "$Label pre-capture"
    Invoke-ProcessChecked `
        ([string]$BuildIdentity.ExecutablePath) `
        $arguments `
        $Label `
        ([int]$Manifest.capture.benchmarkTimeoutSeconds)
    Assert-CaptureInputHashes `
        $BuildIdentity `
        $QualityContractPath $ExpectedQualityContractSha256 `
        $ReferencePath $ExpectedReferenceSha256 `
        $ReferenceInitialization "$Label post-capture"
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        throw "$Label did not publish its health report."
    }
    $health = Get-Content -LiteralPath $healthPath -Raw |
        ConvertFrom-Json -DateKind String
    $healthFailure = [string](Get-PropertyValue $health "failure" "")
    if ([string]$health.status -ne "passed") {
        throw "$Label health gate failed: $healthFailure"
    }
    $report = Read-BenchmarkReport $ReportPath
    Assert-BenchmarkReport `
        $Manifest $Workload $report $Configuration $Label $ReferenceInitialization `
        $PairId $ExpectedCommit $BuildIdentity $ReferenceIdentity $candidatePath
    Assert-HealthReport `
        $Manifest $Workload $health $report $BuildIdentity `
        $ExpectedCommit $PairId $Label
    $admittedOutputs = [ordered]@{
        ([System.IO.Path]::GetFullPath($ReportPath)) = Get-Sha256 $ReportPath
        ([System.IO.Path]::GetFullPath($healthPath)) = Get-Sha256 $healthPath
    }
    if ($ReferenceInitialization) {
        $admittedOutputs[[System.IO.Path]::GetFullPath($ReferencePath)] =
            Get-Sha256 $ReferencePath
    } else {
        $admittedOutputs[[System.IO.Path]::GetFullPath($candidatePath)] =
            Get-Sha256 $candidatePath
    }
    if ([string]$Workload.scene -ceq "Sponza") {
        $sidecarPath = [System.IO.Path]::GetFullPath($ReportPath) +
            ".sponza-animation.bin"
        $admittedOutputs[$sidecarPath] = Get-Sha256 $sidecarPath
    }
    $frozenEvidence = New-TimingFrozenVerifierEvidence `
        $Workload $report $ReportPath $VerifierBuildIdentity $Label
    $admittedOutputs[[System.IO.Path]::GetFullPath($activationArtifactPath)] =
        [string]$frozenEvidence.activation.artifactSha256
    $admittedOutputs[[System.IO.Path]::GetFullPath($ddgiArtifactPath)] =
        [string]$frozenEvidence.ddgiTransient.artifactSha256
    foreach ($entry in $admittedOutputs.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath ([string]$entry.Key) -PathType Leaf) -or
            (Get-Sha256 ([string]$entry.Key)) -cne [string]$entry.Value) {
            throw "$Label output '$($entry.Key)' changed during frozen verification."
        }
    }
    $report | Add-Member `
        -NotePropertyName CampaignFrozenVerifierEvidence `
        -NotePropertyValue $frozenEvidence
    return $report
}

function Get-Median {
    param([double[]]$Values)
    $items = @($Values | Sort-Object)
    if ($items.Count -eq 0) { throw "Cannot compute a median for an empty set." }
    $middle = [int]($items.Count / 2)
    if (($items.Count % 2) -eq 1) {
        return [double]$items[$middle]
    }
    return ([double]$items[$middle - 1] + [double]$items[$middle]) / 2.0
}

function Get-Timing {
    param($Report, [string]$Metric, [string]$Percentile)
    $stats = if ($Metric -eq "cpu") { $Report.CpuFrameMilliseconds } else { $Report.GpuFrameMilliseconds }
    $property = switch ($Percentile) {
        "p50" { "P50Milliseconds" }
        "p99" { "P99Milliseconds" }
        default { "P95Milliseconds" }
    }
    return [double](Get-PropertyValue $stats $property 0.0)
}

function Get-PassTiming {
    param($Report, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    $stats = @($Report.GpuPasses | Where-Object { [string]$_.Name -eq $Name })
    if ($stats.Count -eq 0) {
        $stats = @($Report.CpuStages | Where-Object { [string]$_.Name -eq $Name })
    }
    if ($stats.Count -eq 0) { return $null }
    return [double]$stats[0].P95Milliseconds
}

function Get-ScopedTiming {
    param($Report, [string]$Domain, [string]$Name)
    if ($Domain -notin @("gpu", "cpu") -or
        [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }
    $collection = if ($Domain -eq "gpu") {
        @($Report.GpuPasses)
    } else {
        @($Report.CpuStages)
    }
    $stats = @($collection | Where-Object {
        [string]$_.Name -ceq $Name
    })
    if ($stats.Count -ne 1) { return $null }
    return [double]$stats[0].P95Milliseconds
}

function New-HotspotDiscoveryData {
    param(
        $Manifest,
        [object[]]$CaptureEntries,
        [string]$RetainedCommit,
        [DateTimeOffset]$CreatedAtUtc)
    $expectedConfigurations = @(Get-CampaignConfigurations $Manifest)
    $expectedEntryCount = $expectedConfigurations.Count *
        @($Manifest.workloads).Count
    if ($CaptureEntries.Count -ne $expectedEntryCount -or
        $RetainedCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw "Hotspot discovery requires one authenticated report per workload and configuration."
    }
    $observations = [System.Collections.Generic.List[object]]::new()
    $reportRows = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $CaptureEntries) {
        $configuration = [string]$entry.Configuration
        $workload = $entry.Workload
        $report = $entry.Report
        if ($configuration -notin $expectedConfigurations -or
            $null -eq $workload -or $null -eq $report -or
            [string]$report.LastDiagnostics.CaptureRun.Commit -cne
                $RetainedCommit -or
            (Get-Sha256 ([string]$entry.ReportPath)) -cne
                [string]$entry.ReportSha256) {
            throw "Hotspot discovery report identity is incomplete."
        }
        $cpuFrame = Get-Timing $report "cpu" "p95"
        $gpuFrame = Get-Timing $report "gpu" "p95"
        if (-not [double]::IsFinite($cpuFrame) -or $cpuFrame -le 0.0 -or
            -not [double]::IsFinite($gpuFrame) -or $gpuFrame -le 0.0) {
            throw "Hotspot discovery requires finite positive CPU and GPU frame p95 timings."
        }
        $reportRows.Add([pscustomobject][ordered]@{
            configuration = $configuration
            workloadId = [string]$workload.id
            scene = [string]$workload.scene
            reportPath = [System.IO.Path]::GetFullPath(
                [string]$entry.ReportPath)
            reportSha256 = [string]$entry.ReportSha256
            cpuFrameP95Milliseconds = $cpuFrame
            gpuFrameP95Milliseconds = $gpuFrame
            buildIdentity = $entry.BuildIdentity
            frozenVerifierEvidence = $report.CampaignFrozenVerifierEvidence
        })
        foreach ($domain in @("gpu", "cpu")) {
            $stats = if ($domain -eq "gpu") {
                @($report.GpuPasses)
            } else {
                @($report.CpuStages)
            }
            $names = [System.Collections.Generic.HashSet[string]]::new(
                [StringComparer]::Ordinal)
            $frameP95 = if ($domain -eq "gpu") { $gpuFrame } else { $cpuFrame }
            foreach ($stat in $stats) {
                $name = [string]$stat.Name
                $p95 = [double]$stat.P95Milliseconds
                if ([string]::IsNullOrWhiteSpace($name) -or
                    -not $names.Add($name) -or
                    -not [double]::IsFinite($p95) -or $p95 -lt 0.0) {
                    throw "Hotspot discovery encountered malformed or duplicate $domain timing '$name'."
                }
                if ($p95 -le 0.0) { continue }
                $observations.Add([pscustomobject][ordered]@{
                    domain = $domain
                    name = $name
                    configuration = $configuration
                    workloadId = [string]$workload.id
                    scene = [string]$workload.scene
                    p95Milliseconds = $p95
                    frameP95Milliseconds = $frameP95
                    sharePercent = ($p95 / $frameP95) * 100.0
                })
            }
        }
    }
    $expectedReportKeys = @($expectedConfigurations | ForEach-Object {
        $configuration = [string]$_
        @($Manifest.workloads | ForEach-Object {
            "${configuration}::$([string]$_.id)"
        })
    })
    $actualReportKeys = @($reportRows | ForEach-Object {
        "$([string]$_.configuration)::$([string]$_.workloadId)"
    })
    if (($actualReportKeys -join "`n") -cne ($expectedReportKeys -join "`n")) {
        throw "Hotspot discovery report order/topology differs from the manifest."
    }

    $grouped = [ordered]@{}
    foreach ($observation in $observations) {
        $key = "$([string]$observation.domain)`0$([string]$observation.name)"
        if (-not $grouped.Contains($key)) {
            $grouped[$key] = [System.Collections.Generic.List[object]]::new()
        }
        $grouped[$key].Add($observation)
    }
    $eligible = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $grouped.GetEnumerator()) {
        $rows = @($entry.Value)
        $claims = [System.Collections.Generic.List[object]]::new()
        foreach ($scene in @("Bistro", "Sponza")) {
            $sceneCandidates = [System.Collections.Generic.List[object]]::new()
            $sceneRows = @($rows | Where-Object {
                [string]$_.scene -ceq $scene
            })
            foreach ($workloadId in @($sceneRows.workloadId |
                    Sort-Object -Unique)) {
                $workloadRows = @($sceneRows | Where-Object {
                    [string]$_.workloadId -ceq [string]$workloadId
                })
                $configurations = @($workloadRows.configuration |
                    Sort-Object -Unique)
                if (($configurations -join "`n") -cne
                    (@($expectedConfigurations | Sort-Object) -join "`n")) {
                    continue
                }
                $sceneCandidates.Add([pscustomobject]@{
                    Scene = $scene
                    WorkloadId = [string]$workloadId
                    MaximumP95Milliseconds = [double](
                        $workloadRows.p95Milliseconds |
                            Measure-Object -Maximum).Maximum
                    MaximumSharePercent = [double](
                        $workloadRows.sharePercent |
                            Measure-Object -Maximum).Maximum
                })
            }
            $selected = @($sceneCandidates | Sort-Object `
                @{ Expression = "MaximumP95Milliseconds"; Descending = $true },
                @{ Expression = "MaximumSharePercent"; Descending = $true },
                @{ Expression = "WorkloadId"; Descending = $false } |
                Select-Object -First 1)
            if ($selected.Count -eq 1) {
                $claims.Add([pscustomobject][ordered]@{
                    scene = [string]$selected[0].Scene
                    workloadId = [string]$selected[0].WorkloadId
                })
            }
        }
        $maximumP95 = [double]($rows.p95Milliseconds |
            Measure-Object -Maximum).Maximum
        $maximumShare = [double]($rows.sharePercent |
            Measure-Object -Maximum).Maximum
        $meetsThreshold = $maximumP95 -ge
                [double]$Manifest.discoveryPolicy.minimumP95Milliseconds -or
            $maximumShare -ge
                [double]$Manifest.discoveryPolicy.minimumSharePercent
        if ($claims.Count -ne 2 -or -not $meetsThreshold) { continue }
        $domain = [string]$rows[0].domain
        $eligible.Add([pscustomobject][ordered]@{
            domain = $domain
            name = [string]$rows[0].name
            maximumP95Milliseconds = $maximumP95
            maximumSharePercent = $maximumShare
            domainOrder = if ($domain -eq "gpu") { 0 } else { 1 }
            claims = @($claims)
        })
    }
    $ranked = @($eligible | Sort-Object `
        @{ Expression = "MaximumP95Milliseconds"; Descending = $true },
        @{ Expression = "MaximumSharePercent"; Descending = $true },
        @{ Expression = "DomainOrder"; Descending = $false },
        @{ Expression = "Name"; Descending = $false })
    $rank = 0
    $rankedRows = @($ranked | ForEach-Object {
        $rank++
        [pscustomobject][ordered]@{
            rank = $rank
            domain = [string]$_.domain
            name = [string]$_.name
            maximumP95Milliseconds = [double]$_.maximumP95Milliseconds
            maximumSharePercent = [double]$_.maximumSharePercent
            claims = @($_.claims)
        }
    })
    return [pscustomobject][ordered]@{
        schema = "njulf-perf-hotspot-discovery/v1"
        campaignId = [string]$Manifest.campaignId
        manifestSha256 = Get-AdmittedCampaignManifestSha256
        retainedCommit = $RetainedCommit
        createdAtUtc = $CreatedAtUtc.ToString("O")
        configurations = @($expectedConfigurations)
        policy = $Manifest.discoveryPolicy
        reports = @($reportRows)
        eligibleHotspots = @($rankedRows)
        recommendedHotspot = if ($rankedRows.Count -eq 0) {
            $null
        } else { $rankedRows[0] }
    }
}

function Assert-HotspotDiscoveryArtifact {
    param(
        $Manifest,
        $Lock,
        [string]$Path,
        [string]$ExpectedSha256,
        [string]$ExpectedRetainedCommit,
        [string]$Label)
    $fullPath = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($Path)) "$Label path"
    if (-not (Test-PathContainedBy $fullPath $script:RunRoot)) {
        throw "$Label must be contained by the admitted campaign run root."
    }
    $read = Read-StrictJsonFile $fullPath 8MB $Label
    if ([string]$read.Sha256 -cne $ExpectedSha256) {
        throw "$Label SHA-256 differs from its admitted identity."
    }
    $artifact = $read.Value
    Assert-ExactPropertyNames $artifact @(
        "schema", "campaignId", "manifestSha256", "retainedCommit",
        "createdAtUtc", "configurations", "policy", "reports",
        "eligibleHotspots", "recommendedHotspot") $Label
    $createdAt = [DateTimeOffset]::MinValue
    if ([string]$artifact.schema -cne "njulf-perf-hotspot-discovery/v1" -or
        [string]$artifact.campaignId -cne [string]$Manifest.campaignId -or
        [string]$artifact.manifestSha256 -cne
            (Get-AdmittedCampaignManifestSha256) -or
        [string]$artifact.retainedCommit -cne $ExpectedRetainedCommit -or
        -not [DateTimeOffset]::TryParseExact(
            [string]$artifact.createdAtUtc,
            "O",
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$createdAt) -or
        ((@($artifact.configurations) -join "`n") -cne
         (@(Get-CampaignConfigurations $Manifest) -join "`n")) -or
        (($artifact.policy | ConvertTo-Json -Depth 8 -Compress) -cne
         ($Manifest.discoveryPolicy | ConvertTo-Json -Depth 8 -Compress))) {
        throw "$Label envelope differs from the admitted campaign contract."
    }
    $expectedCount = @(Get-CampaignConfigurations $Manifest).Count *
        @($Manifest.workloads).Count
    $rows = @($artifact.reports)
    if ($rows.Count -ne $expectedCount) {
        throw "$Label report topology differs."
    }
    $entries = [System.Collections.Generic.List[object]]::new()
    $rowIndex = 0
    foreach ($configuration in @(Get-CampaignConfigurations $Manifest)) {
        $verifierBuild = Get-ReferenceBuildIdentity $Lock $configuration
        foreach ($workload in @($Manifest.workloads)) {
            $row = $rows[$rowIndex]
            Assert-ExactPropertyNames $row @(
                "configuration", "workloadId", "scene", "reportPath",
                "reportSha256", "cpuFrameP95Milliseconds",
                "gpuFrameP95Milliseconds", "buildIdentity",
                "frozenVerifierEvidence") "$Label report $rowIndex"
            if ([string]$row.configuration -cne $configuration -or
                [string]$row.workloadId -cne [string]$workload.id -or
                [string]$row.scene -cne [string]$workload.scene) {
                throw "$Label report $rowIndex is reordered or relabeled."
            }
            $reportPath = Assert-NoLinkedPathComponents `
                ([System.IO.Path]::GetFullPath([string]$row.reportPath)) `
                "$Label report $rowIndex path"
            if (-not (Test-PathContainedBy $reportPath $script:RunRoot) -or
                (Get-Sha256 $reportPath) -cne [string]$row.reportSha256) {
                throw "$Label report $rowIndex bytes differ."
            }
            Assert-BuildIdentity $row.buildIdentity `
                "$Label report $rowIndex build"
            $report = Read-BenchmarkReport $reportPath
            $reference = Get-ReferenceLockEntry `
                $Lock $configuration ([string]$workload.id)
            $candidatePfm = [System.IO.Path]::ChangeExtension(
                $reportPath, ".hdr.pfm")
            Assert-BenchmarkReport `
                $Manifest $workload $report $configuration `
                "$Label report $rowIndex" $false `
                ([string]$report.CaptureContract.PairId) `
                $ExpectedRetainedCommit $row.buildIdentity $reference $candidatePfm
            $null = Assert-TimingFrozenVerifierEvidence `
                $workload $report $reportPath $row.frozenVerifierEvidence `
                $verifierBuild "$Label report $rowIndex"
            $report | Add-Member `
                -NotePropertyName CampaignFrozenVerifierEvidence `
                -NotePropertyValue $row.frozenVerifierEvidence
            $entries.Add([pscustomobject]@{
                Configuration = $configuration
                Workload = $workload
                ReportPath = $reportPath
                ReportSha256 = [string]$row.reportSha256
                Report = $report
                BuildIdentity = $row.buildIdentity
            })
            $rowIndex++
        }
    }
    $recomputed = New-HotspotDiscoveryData `
        $Manifest @($entries) $ExpectedRetainedCommit $createdAt
    if (($recomputed | ConvertTo-Json -Depth 32 -Compress) -cne
        ($artifact | ConvertTo-Json -Depth 32 -Compress)) {
        throw "$Label differs from independently recomputed hotspot evidence."
    }
    Assert-CampaignLockIntegrity
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    return $artifact
}

function Assert-ReferenceTargetHypothesisPasses {
    param($Manifest, $ConfigurationReferences, [string]$Label)
    foreach ($hypothesis in @($Manifest.targetHypotheses)) {
        foreach ($claim in @($hypothesis.claims)) {
            $workloadId = [string]$claim.workloadId
            $property = $ConfigurationReferences.PSObject.Properties[$workloadId]
            $entry = if ($null -eq $property) {
                $ConfigurationReferences[$workloadId]
            } else { $property.Value }
            if ($null -eq $entry) {
                throw "$Label target claim '$workloadId' has no reference."
            }
            $report = Read-BenchmarkReport ([string]$entry.reportPath)
            $p95 = Get-ScopedTiming `
                $report ([string]$hypothesis.targetDomain) `
                ([string]$hypothesis.targetPass)
            if ($null -eq $p95 -or
                -not [double]::IsFinite([double]$p95) -or
                [double]$p95 -le 0.0) {
                throw "$Label target hypothesis '$($hypothesis.id)' claim " +
                    "'$workloadId' lacks finite positive '$($hypothesis.targetPass)' timing."
            }
        }
    }
}

function Get-TargetHypothesis {
    param($Manifest, [string]$HypothesisId)
    $id = if ([string]::IsNullOrWhiteSpace($HypothesisId)) {
        [string]@($Manifest.targetHypotheses)[0].id
    } else { $HypothesisId }
    $matches = @($Manifest.targetHypotheses | Where-Object {
        [string]$_.id -ceq $id
    })
    if ($matches.Count -ne 1) {
        throw "Target hypothesis '$id' was not found."
    }
    return $matches[0]
}

function Get-ReviewedCandidate {
    param($Manifest, [string]$Id)
    Assert-Text $Id "CandidateId"
    $matches = @($Manifest.candidates | Where-Object {
        [string]$_.id -ceq $Id
    })
    if ($matches.Count -ne 1) {
        throw "Reviewed candidate '$Id' was not found in the pinned manifest."
    }
    return $matches[0]
}

function New-ReviewedCandidateDecisionIdentity {
    param($Candidate)
    return [pscustomobject][ordered]@{
        kind = "reviewed"
        id = [string]$Candidate.id
        sourceCommit = [string]$Candidate.sourceCommit
        patchId = [string]$Candidate.patchId
        allowedPaths = @($Candidate.allowedPaths | ForEach-Object { [string]$_ })
        focusedTestFilter = [string]$Candidate.focusedTestFilter
        envelopePath = ""
        envelopeSha256 = ""
        discoveryArtifactPath = ""
        discoveryArtifactSha256 = ""
    }
}

function Read-CandidateEnvelope {
    param(
        $Manifest,
        $Lock,
        [string]$Path,
        [string]$ExpectedAcceptedHead,
        [bool]$InitialAdmission,
        [string]$ExpectedSha256 = "")
    $fullPath = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($Path)) "Candidate envelope"
    if (-not (Test-PathContainedBy $fullPath $script:RunRoot)) {
        throw "Candidate envelope must be contained by the admitted run root."
    }
    $read = Read-StrictJsonFile $fullPath 1MB "Candidate envelope"
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        [string]$read.Sha256 -cne $ExpectedSha256) {
        throw "Candidate envelope bytes differ from the admitted SHA-256."
    }
    $envelope = $read.Value
    Assert-ExactPropertyNames $envelope @(
        "schema", "campaignId", "manifestSha256", "lockSha256",
        "acceptedHead", "discoveryArtifactPath", "discoveryArtifactSha256",
        "hotspot", "attempt", "candidate") "Candidate envelope"
    Assert-ExactPropertyNames $envelope.hotspot @(
        "rank", "domain", "name", "maximumP95Milliseconds",
        "maximumSharePercent", "claims") "Candidate envelope hotspot"
    Assert-ExactPropertyNames $envelope.candidate @(
        "id", "sourceCommit", "patchId", "allowedPaths",
        "focusedTestFilter") "Candidate envelope candidate"
    $textFields = [ordered]@{
        schema = $envelope.schema
        campaignId = $envelope.campaignId
        manifestSha256 = $envelope.manifestSha256
        lockSha256 = $envelope.lockSha256
        acceptedHead = $envelope.acceptedHead
        discoveryArtifactPath = $envelope.discoveryArtifactPath
        discoveryArtifactSha256 = $envelope.discoveryArtifactSha256
        "hotspot.domain" = $envelope.hotspot.domain
        "hotspot.name" = $envelope.hotspot.name
        "candidate.id" = $envelope.candidate.id
        "candidate.sourceCommit" = $envelope.candidate.sourceCommit
        "candidate.patchId" = $envelope.candidate.patchId
        "candidate.focusedTestFilter" = $envelope.candidate.focusedTestFilter
    }
    foreach ($textField in $textFields.GetEnumerator()) {
        $null = Assert-JsonString $textField.Value `
            "Candidate envelope $($textField.Key)"
    }
    $null = Assert-JsonInteger $envelope.attempt "Candidate envelope attempt" 1
    $null = Assert-JsonInteger $envelope.hotspot.rank `
        "Candidate envelope hotspot rank" 1
    $null = Assert-JsonArray $envelope.hotspot.claims `
        "Candidate envelope hotspot claims"
    $null = Assert-JsonArray $envelope.candidate.allowedPaths `
        "Candidate envelope candidate allowedPaths"
    $numberFields = [ordered]@{
        maximumP95Milliseconds = $envelope.hotspot.maximumP95Milliseconds
        maximumSharePercent = $envelope.hotspot.maximumSharePercent
    }
    foreach ($numberField in $numberFields.GetEnumerator()) {
        if ($numberField.Value -is [string] -or
            -not [double]::IsFinite([double]$numberField.Value) -or
            [double]$numberField.Value -le 0.0) {
            throw "Candidate envelope hotspot $($numberField.Key) must be a finite positive JSON number."
        }
    }
    if ([string]$envelope.schema -cne "njulf-perf-candidate-envelope/v1" -or
        [string]$envelope.campaignId -cne [string]$Manifest.campaignId -or
        [string]$envelope.manifestSha256 -cne
            (Get-AdmittedCampaignManifestSha256) -or
        [string]$envelope.lockSha256 -cne $script:CampaignLockSha256 -or
        [string]$envelope.acceptedHead -cne $ExpectedAcceptedHead -or
        [int]$envelope.attempt -ne 1) {
        throw "Candidate envelope identity differs from the admitted campaign."
    }
    $discoveryPath = [System.IO.Path]::GetFullPath(
        [string]$envelope.discoveryArtifactPath)
    $discovery = Assert-HotspotDiscoveryArtifact `
        $Manifest $Lock $discoveryPath `
        ([string]$envelope.discoveryArtifactSha256) $ExpectedAcceptedHead `
        "Candidate discovery"
    $eligible = @($discovery.eligibleHotspots)
    $hotspotMatches = @($eligible | Where-Object {
        ($_ | ConvertTo-Json -Depth 8 -Compress) -ceq
            ($envelope.hotspot | ConvertTo-Json -Depth 8 -Compress)
    })
    if ($hotspotMatches.Count -ne 1) {
        throw "Candidate hotspot is not one exact authenticated eligible row."
    }
    if ($InitialAdmission) {
        $next = @($eligible | Where-Object {
            -not (Test-TimingAttemptReserved `
                $Manifest ([string]$_.domain) ([string]$_.name))
        } | Select-Object -First 1)
        if ($next.Count -ne 1 -or
            (($next[0] | ConvertTo-Json -Depth 8 -Compress) -cne
             ($envelope.hotspot | ConvertTo-Json -Depth 8 -Compress))) {
            throw "Candidate envelope does not target the highest-ranked unattempted hotspot."
        }
    }
    $candidate = $envelope.candidate
    $allowedPaths = @($candidate.allowedPaths | ForEach-Object { [string]$_ })
    if ([string]$candidate.id -cnotmatch '^auto-[a-z0-9][a-z0-9-]{2,63}$' -or
        [string]$candidate.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
        [string]$candidate.patchId -cnotmatch '^[0-9a-f]{40}$' -or
        [string]::IsNullOrWhiteSpace([string]$candidate.focusedTestFilter) -or
        $allowedPaths.Count -lt 1 -or $allowedPaths.Count -gt 8 -or
        (@($allowedPaths | Sort-Object -Unique)).Count -ne $allowedPaths.Count -or
        (Get-StablePatchId ([string]$candidate.sourceCommit)) -cne
            [string]$candidate.patchId -or
        (@(Get-CommitChangedPaths ([string]$candidate.sourceCommit)) -join "`n") -cne
            ($allowedPaths -join "`n")) {
        throw "Candidate envelope patch identity is malformed or unbounded."
    }
    $parentFields = @((Get-GitText @(
        "rev-list", "--parents", "-n", "1", [string]$candidate.sourceCommit)) -split '\s+')
    if ($parentFields.Count -ne 2 -or
        [string]$parentFields[1] -cne $ExpectedAcceptedHead) {
        throw "Automatic candidate must be one non-merge commit on the admitted head."
    }
    $changedLines = 0
    foreach ($line in @(Invoke-Git @(
                "diff", "--numstat", "$ExpectedAcceptedHead..$([string]$candidate.sourceCommit)"))) {
        $fields = @(([string]$line) -split "`t")
        if ($fields.Count -ne 3 -or $fields[0] -notmatch '^\d+$' -or
            $fields[1] -notmatch '^\d+$') {
            throw "Automatic candidate contains a binary or malformed diff."
        }
        $changedLines += [int]$fields[0] + [int]$fields[1]
    }
    if ($changedLines -gt 1200) {
        throw "Automatic candidate exceeds the 1,200-line bounded patch limit."
    }
    $automaticSourceExtensions = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(
            ".cs", ".comp", ".frag", ".vert", ".geom", ".tesc", ".tese",
            ".glsl", ".mesh", ".task")) {
        [void]$automaticSourceExtensions.Add($extension)
    }
    foreach ($relativePath in $allowedPaths) {
        if ([System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains("..", [StringComparison]::Ordinal) -or
            $relativePath.Contains("\", [StringComparison]::Ordinal)) {
            throw "Automatic candidate path '$relativePath' is not canonical."
        }
        if (-not $automaticSourceExtensions.Contains(
                [System.IO.Path]::GetExtension($relativePath))) {
            throw "Automatic candidate path '$relativePath' is not an admitted source extension; build, package, and campaign wiring are immutable."
        }
        $fullCandidatePath = Resolve-SolutionPath $relativePath
        foreach ($protectedPath in $script:ProtectedFingerprints.Keys) {
            if ([string]::Equals(
                    $fullCandidatePath, [string]$protectedPath,
                    [StringComparison]::OrdinalIgnoreCase) -or
                (Test-PathContainedBy $fullCandidatePath ([string]$protectedPath))) {
                throw "Automatic candidate path '$relativePath' intersects a protected root."
            }
        }
    }
    $identityHash = Get-Sha256Text (
        "$([string]$envelope.hotspot.domain)`0$([string]$envelope.hotspot.name)")
    $hypothesis = [pscustomobject][ordered]@{
        id = "auto-$($identityHash.Substring(0, 16))"
        targetDomain = [string]$envelope.hotspot.domain
        targetPass = [string]$envelope.hotspot.name
        claims = @($envelope.hotspot.claims)
    }
    $decisionIdentity = [pscustomobject][ordered]@{
        kind = "discovered"
        id = [string]$candidate.id
        sourceCommit = [string]$candidate.sourceCommit
        patchId = [string]$candidate.patchId
        allowedPaths = $allowedPaths
        focusedTestFilter = [string]$candidate.focusedTestFilter
        envelopePath = $fullPath
        envelopeSha256 = [string]$read.Sha256
        discoveryArtifactPath = $discoveryPath
        discoveryArtifactSha256 = [string]$envelope.discoveryArtifactSha256
    }
    return [pscustomobject]@{
        Candidate = $candidate
        Hypothesis = $hypothesis
        DecisionIdentity = $decisionIdentity
        Envelope = $envelope
    }
}

function New-AutomaticCandidateEnvelope {
    param(
        $Manifest,
        $Lock,
        [string]$DiscoveryPath,
        [string]$Id,
        [string]$SourceCommit,
        [string]$FocusedTestFilter,
        [string]$OutputPath,
        [string]$AcceptedHead)
    Assert-Text $Id "AutomaticCandidateId"
    Assert-Text $SourceCommit "AutomaticCandidateSourceCommit"
    Assert-Text $FocusedTestFilter "AutomaticCandidateFocusedTestFilter"
    Assert-Text $OutputPath "CandidateEnvelopeOutputPath"
    $fullDiscoveryPath = Assert-NoLinkedPathComponents `
        ([System.IO.Path]::GetFullPath($DiscoveryPath)) `
        "Automatic candidate discovery path"
    $discoverySha256 = Get-Sha256 $fullDiscoveryPath
    $discovery = Assert-HotspotDiscoveryArtifact `
        $Manifest $Lock $fullDiscoveryPath $discoverySha256 $AcceptedHead `
        "Automatic candidate discovery"
    $next = @($discovery.eligibleHotspots | Where-Object {
        -not (Test-TimingAttemptReserved `
            $Manifest ([string]$_.domain) ([string]$_.name))
    } | Select-Object -First 1)
    if ($next.Count -ne 1) {
        throw "No authenticated unattempted hotspot remains."
    }
    $candidatePaths = @(Get-CommitChangedPaths $SourceCommit)
    $envelope = [pscustomobject][ordered]@{
        schema = "njulf-perf-candidate-envelope/v1"
        campaignId = [string]$Manifest.campaignId
        manifestSha256 = Get-AdmittedCampaignManifestSha256
        lockSha256 = $script:CampaignLockSha256
        acceptedHead = $AcceptedHead
        discoveryArtifactPath = $fullDiscoveryPath
        discoveryArtifactSha256 = $discoverySha256
        hotspot = $next[0]
        attempt = 1
        candidate = [pscustomobject][ordered]@{
            id = $Id
            sourceCommit = $SourceCommit
            patchId = Get-StablePatchId $SourceCommit
            allowedPaths = $candidatePaths
            focusedTestFilter = $FocusedTestFilter
        }
    }
    $fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    if (-not (Test-PathContainedBy $fullOutputPath $script:RunRoot)) {
        throw "Candidate envelope output must be contained by the admitted run root."
    }
    Write-JsonArtifact $fullOutputPath $envelope
    try {
        $admission = Read-CandidateEnvelope `
            $Manifest $Lock $fullOutputPath $AcceptedHead $true
    } catch {
        if (Test-Path -LiteralPath $fullOutputPath -PathType Leaf) {
            Remove-Item -LiteralPath $fullOutputPath -Force
        }
        throw
    }
    return [pscustomobject][ordered]@{
        schema = "njulf-perf-candidate-envelope-preparation/v1"
        path = $fullOutputPath
        sha256 = Get-Sha256 $fullOutputPath
        candidate = $admission.DecisionIdentity
        hypothesis = $admission.Hypothesis
    }
}

function Get-HypothesisWorkloads {
    param($Manifest, [object[]]$Hypotheses)
    $orderedIds = [System.Collections.Generic.List[string]]::new()
    $claimsByWorkload = [ordered]@{}
    foreach ($hypothesis in $Hypotheses) {
        $manifestMatches = @($Manifest.targetHypotheses | Where-Object {
            [string]$_.id -ceq [string]$hypothesis.id
        })
        if ($manifestMatches.Count -eq 1) {
            if (($manifestMatches[0] | ConvertTo-Json -Depth 8 -Compress) -cne
                ($hypothesis | ConvertTo-Json -Depth 8 -Compress)) {
                throw "Target hypothesis '$($hypothesis.id)' differs from the locked manifest."
            }
        } elseif ($manifestMatches.Count -ne 0 -or
            [string]$hypothesis.id -cnotmatch '^auto-[0-9a-f]{16}$' -or
            [string]$hypothesis.targetDomain -notin @("gpu", "cpu") -or
            [string]::IsNullOrWhiteSpace([string]$hypothesis.targetPass) -or
            @($hypothesis.claims).Count -ne 2) {
            throw "Target hypothesis '$($hypothesis.id)' is not an admitted manifest or automatic hypothesis."
        }
        foreach ($claim in @($hypothesis.claims)) {
            $workloadId = [string]$claim.workloadId
            $workloadMatches = @($Manifest.workloads | Where-Object {
                [string]$_.id -ceq $workloadId
            })
            if ($workloadMatches.Count -ne 1 -or
                [string]$workloadMatches[0].scene -cne [string]$claim.scene) {
                throw "Target hypothesis '$($hypothesis.id)' claim '$workloadId' differs from the workload topology."
            }
            if (-not $claimsByWorkload.Contains($workloadId)) {
                $claimsByWorkload[$workloadId] =
                    [System.Collections.Generic.List[object]]::new()
                $orderedIds.Add($workloadId)
            }
            $claimsByWorkload[$workloadId].Add([pscustomobject][ordered]@{
                hypothesisId = [string]$hypothesis.id
                scene = [string]$claim.scene
                workloadId = $workloadId
                targetDomain = [string]$hypothesis.targetDomain
                targetPass = [string]$hypothesis.targetPass
            })
        }
    }
    $result = @()
    foreach ($workloadId in $orderedIds) {
        $source = @($Manifest.workloads | Where-Object {
            [string]$_.id -ceq $workloadId
        })[0]
        $copy = [ordered]@{}
        foreach ($property in $source.PSObject.Properties) {
            $copy[$property.Name] = $property.Value
        }
        $copy["campaignTargetClaims"] = @($claimsByWorkload[$workloadId])
        $result += [pscustomobject]$copy
    }
    return @($result)
}

function Get-TargetPassPairedDifferences {
    param($Workload, [object[]]$OrderedCycleReports)
    if (($OrderedCycleReports.Count % 4) -ne 0) {
        throw "Workload '$($Workload.id)' has an incomplete ABBA report topology."
    }
    $targets = @((Get-PropertyValue $Workload "campaignTargetClaims" @()) |
        ForEach-Object {
            [pscustomobject]@{
                Domain = [string]$_.targetDomain
                Name = [string]$_.targetPass
                Identity = "{0}::{1}" -f
                    [string]$_.targetDomain, [string]$_.targetPass
            }
        } | Sort-Object Identity -Unique)
    $result = [ordered]@{}
    foreach ($target in $targets) {
        $differences = @()
        for ($offset = 0;
             $offset -lt $OrderedCycleReports.Count;
             $offset += 4) {
            $values = @(0, 1, 2, 3 | ForEach-Object {
                Get-ScopedTiming `
                    $OrderedCycleReports[$offset + $_] `
                    ([string]$target.Domain) ([string]$target.Name)
            })
            if (@($values | Where-Object { $null -eq $_ }).Count -ne 0) {
                throw "Workload '$($Workload.id)' lacks '$($target.Identity)' in one or more ABBA slots."
            }
            $differences += [double]$values[0] - [double]$values[1]
            $differences += [double]$values[3] - [double]$values[2]
        }
        $result[[string]$target.Identity] = [double[]]$differences
    }
    return [pscustomobject]$result
}

function New-ConfigurationHypothesisResults {
    param([object[]]$WinWorkloads, [object[]]$Comparisons)
    $orderedHypothesisIds = [System.Collections.Generic.List[string]]::new()
    $targetDomainByHypothesis = [ordered]@{}
    $targetPassByHypothesis = [ordered]@{}
    $claimsByHypothesis = [ordered]@{}
    foreach ($workload in $WinWorkloads) {
        $comparison = @($Comparisons | Where-Object {
            [string]$_.Workload -ceq [string]$workload.id
        })
        if ($comparison.Count -ne 1) {
            throw "Target workload '$($workload.id)' has no unique comparison."
        }
        foreach ($claim in @((Get-PropertyValue $workload "campaignTargetClaims" @()))) {
            $hypothesisId = [string]$claim.hypothesisId
            if (-not $claimsByHypothesis.Contains($hypothesisId)) {
                $claimsByHypothesis[$hypothesisId] =
                    [System.Collections.Generic.List[object]]::new()
                $targetDomainByHypothesis[$hypothesisId] =
                    [string]$claim.targetDomain
                $targetPassByHypothesis[$hypothesisId] = [string]$claim.targetPass
                $orderedHypothesisIds.Add($hypothesisId)
            } elseif ([string]$targetDomainByHypothesis[$hypothesisId] -cne
                    [string]$claim.targetDomain -or
                [string]$targetPassByHypothesis[$hypothesisId] -cne
                    [string]$claim.targetPass) {
                throw "Target hypothesis '$hypothesisId' changed timing identity."
            }
            $matches = @($comparison[0].TargetClaimResults | Where-Object {
                [string]$_.TargetHypothesisId -ceq $hypothesisId -and
                [string]$_.Scene -ceq [string]$claim.scene -and
                [string]$_.WorkloadId -ceq [string]$claim.workloadId -and
                [string]$_.TargetDomain -ceq [string]$claim.targetDomain -and
                [string]$_.TargetPass -ceq [string]$claim.targetPass
            })
            if ($matches.Count -ne 1) {
                throw "Target hypothesis '$hypothesisId' claim '$($claim.workloadId)' has no unique result."
            }
            $claimsByHypothesis[$hypothesisId].Add($matches[0])
        }
    }
    $results = @()
    foreach ($hypothesisId in $orderedHypothesisIds) {
        $claims = @($claimsByHypothesis[$hypothesisId])
        $decision = if ($claims.Count -eq 2 -and
            @($claims | Where-Object {
                [string]$_.Decision -cne "keep"
            }).Count -eq 0) { "keep" } else { "rollback" }
        $results += [pscustomobject]@{
            TargetHypothesisId = $hypothesisId
            TargetDomain = [string]$targetDomainByHypothesis[$hypothesisId]
            TargetPass = [string]$targetPassByHypothesis[$hypothesisId]
            Decision = $decision
            Claims = @($claims)
        }
    }
    return @($results)
}

function Get-ImprovementPercent {
    param([double]$Baseline, [double]$Candidate)
    if ($Baseline -le 0.0) { return 0.0 }
    return (($Baseline - $Candidate) / $Baseline) * 100.0
}

function Get-BootstrapLowerBound {
    param([double[]]$Differences, [int]$SampleCount, [double]$Confidence)
    if ($Differences.Count -eq 0) { return [double]::NegativeInfinity }
    if (@($Differences | Where-Object {
            -not [double]::IsFinite([double]$_)
        }).Count -ne 0) {
        throw "Paired bootstrap differences must all be finite."
    }
    $random = [Random]::new(20260820)
    $distribution = [double[]]::new($SampleCount)
    for ($sample = 0; $sample -lt $SampleCount; $sample++) {
        $sum = 0.0
        for ($index = 0; $index -lt $Differences.Count; $index++) {
            $sum += $Differences[$random.Next($Differences.Count)]
        }
        $distribution[$sample] = $sum / $Differences.Count
    }
    [Array]::Sort($distribution)
    $tail = (1.0 - $Confidence) / 2.0
    $index = [Math]::Clamp([int][Math]::Floor($tail * $SampleCount), 0, $SampleCount - 1)
    return $distribution[$index]
}

function Assert-CrossBuildIdentity {
    param($BaselineReports, $CandidateReports, [string]$WorkloadId)
    $baseline = $BaselineReports[0]
    $candidate = $CandidateReports[0]
    $pairs = @(
        @("scenario", [string]$baseline.Scenario, [string]$candidate.Scenario),
        @("trajectory", [string]$baseline.CaptureContract.Trajectory, [string]$candidate.CaptureContract.Trajectory),
        @("trajectory fingerprint", [string]$baseline.CaptureContract.TrajectoryFingerprint, [string]$candidate.CaptureContract.TrajectoryFingerprint),
        @("trajectory frame count", [string]$baseline.CaptureContract.TrajectoryFrameCount, [string]$candidate.CaptureContract.TrajectoryFrameCount),
        @("trajectory route", [string]$baseline.CaptureContract.TrajectoryRouteHash, [string]$candidate.CaptureContract.TrajectoryRouteHash),
        @("scene asset", [string]$baseline.LastDiagnostics.CaptureSceneAssetHash, [string]$candidate.LastDiagnostics.CaptureSceneAssetHash),
        @("settings schema", [string]$baseline.LastDiagnostics.CaptureRun.SettingsSchemaVersion, [string]$candidate.LastDiagnostics.CaptureRun.SettingsSchemaVersion),
        @("quality", [string]$baseline.LastDiagnostics.ActiveQualityPreset, [string]$candidate.LastDiagnostics.ActiveQualityPreset),
        @("GI settings", [string]$baseline.LastDiagnostics.ResolvedGiSettings.StableHash, [string]$candidate.LastDiagnostics.ResolvedGiSettings.StableHash),
        @("feature isolation", [string]$baseline.LastDiagnostics.ActiveFeatureIsolation, [string]$candidate.LastDiagnostics.ActiveFeatureIsolation),
        @("debug view", [string]$baseline.LastDiagnostics.GlobalIlluminationDebugView, [string]$candidate.LastDiagnostics.GlobalIlluminationDebugView),
        @("producer schema", [string]$baseline.ProducerIdentity.Schema, [string]$candidate.ProducerIdentity.Schema),
        @("settings", [string]$baseline.ProducerIdentity.SettingsFingerprint, [string]$candidate.ProducerIdentity.SettingsFingerprint),
        @("GPU", [string]$baseline.ProducerIdentity.GpuName, [string]$candidate.ProducerIdentity.GpuName),
        @("driver", [string]$baseline.ProducerIdentity.DriverVersion, [string]$candidate.ProducerIdentity.DriverVersion),
        @("quality tier", [string]$baseline.ProducerIdentity.QualityTier, [string]$candidate.ProducerIdentity.QualityTier))
    foreach ($pair in $pairs) {
        if (-not [string]::Equals([string]$pair[1], [string]$pair[2], [StringComparison]::Ordinal)) {
            throw "Workload '$WorkloadId' changed $($pair[0]) identity across builds."
        }
    }
}

function Assert-WithinPhaseIdentity {
    param($Expected, $Actual, [string]$Label)
    if ($null -eq $Expected) { return }
    $pairs = @(
        @("scenario", [string]$Expected.Scenario, [string]$Actual.Scenario),
        @("trajectory", [string]$Expected.CaptureContract.Trajectory, [string]$Actual.CaptureContract.Trajectory),
        @("trajectory fingerprint", [string]$Expected.CaptureContract.TrajectoryFingerprint, [string]$Actual.CaptureContract.TrajectoryFingerprint),
        @("trajectory frame count", [string]$Expected.CaptureContract.TrajectoryFrameCount, [string]$Actual.CaptureContract.TrajectoryFrameCount),
        @("trajectory route", [string]$Expected.CaptureContract.TrajectoryRouteHash, [string]$Actual.CaptureContract.TrajectoryRouteHash),
        @("trajectory sequence", [string]$Expected.CaptureContract.TrajectorySequenceHash, [string]$Actual.CaptureContract.TrajectorySequenceHash),
        @("capture identity", [string]$Expected.CaptureContract.IdentityHash, [string]$Actual.CaptureContract.IdentityHash),
        @("full capture identity", [string]$Expected.CaptureContract.FullIdentityHash, [string]$Actual.CaptureContract.FullIdentityHash),
        @("build commit", [string]$Expected.LastDiagnostics.CaptureRun.Commit, [string]$Actual.LastDiagnostics.CaptureRun.Commit),
        @("application version", [string]$Expected.LastDiagnostics.CaptureRun.ApplicationVersion, [string]$Actual.LastDiagnostics.CaptureRun.ApplicationVersion),
        @("settings schema", [string]$Expected.LastDiagnostics.CaptureRun.SettingsSchemaVersion, [string]$Actual.LastDiagnostics.CaptureRun.SettingsSchemaVersion),
        @("executable", [string]$Expected.LastDiagnostics.CaptureRun.ExecutableHash, [string]$Actual.LastDiagnostics.CaptureRun.ExecutableHash),
        @("shader bundle", [string]$Expected.LastDiagnostics.CaptureRun.ShaderBundleHash, [string]$Actual.LastDiagnostics.CaptureRun.ShaderBundleHash),
        @("scene asset", [string]$Expected.LastDiagnostics.CaptureSceneAssetHash, [string]$Actual.LastDiagnostics.CaptureSceneAssetHash),
        @("scene state", [string]$Expected.LastDiagnostics.CaptureSceneStateHash, [string]$Actual.LastDiagnostics.CaptureSceneStateHash),
        @("GI settings", [string]$Expected.LastDiagnostics.ResolvedGiSettings.StableHash, [string]$Actual.LastDiagnostics.ResolvedGiSettings.StableHash),
        @("producer schema", [string]$Expected.ProducerIdentity.Schema, [string]$Actual.ProducerIdentity.Schema),
        @("producer settings", [string]$Expected.ProducerIdentity.SettingsFingerprint, [string]$Actual.ProducerIdentity.SettingsFingerprint),
        @("GPU", [string]$Expected.ProducerIdentity.GpuName, [string]$Actual.ProducerIdentity.GpuName),
        @("driver", [string]$Expected.ProducerIdentity.DriverVersion, [string]$Actual.ProducerIdentity.DriverVersion),
        @("quality tier", [string]$Expected.ProducerIdentity.QualityTier, [string]$Actual.ProducerIdentity.QualityTier),
        @("HDR reference", [string]$Expected.HdrDifference.ReferenceSha256, [string]$Actual.HdrDifference.ReferenceSha256),
        @("HDR quality contract", [string]$Expected.HdrDifference.QualityContractSha256, [string]$Actual.HdrDifference.QualityContractSha256))
    foreach ($pair in $pairs) {
        if (-not [string]::Equals(
                [string]$pair[1],
                [string]$pair[2],
                [StringComparison]::Ordinal)) {
            throw "$Label changed $($pair[0]) within one ABBA phase."
        }
    }
}

function Compare-WorkloadCaptures {
    param(
        $Manifest,
        $Workload,
        $BaselineReports,
        $CandidateReports,
        [double[]]$PairedDifferences,
        $TargetPassPairedDifferences,
        [bool]$RequireWin)
    $expectedPairCount = [int]$Manifest.capture.abbaCycles * 2
    if (@($BaselineReports).Count -ne $expectedPairCount -or
        @($CandidateReports).Count -ne $expectedPairCount -or
        $PairedDifferences.Count -ne $expectedPairCount) {
        throw "Workload '$($Workload.id)' has incomplete ABBA report/pair evidence; expected $expectedPairCount per phase."
    }
    Assert-CrossBuildIdentity $BaselineReports $CandidateReports ([string]$Workload.id)
    $metrics = [ordered]@{}
    foreach ($metric in @("cpu", "gpu")) {
        foreach ($percentile in @("p50", "p95", "p99")) {
            $key = "$metric-$percentile"
            $metrics[$key] = [pscustomobject]@{
                Baseline = Get-Median @($BaselineReports | ForEach-Object { Get-Timing $_ $metric $percentile })
                Candidate = Get-Median @($CandidateReports | ForEach-Object { Get-Timing $_ $metric $percentile })
            }
        }
    }
    $baselineBottleneck = [Math]::Max($metrics["cpu-p95"].Baseline, $metrics["gpu-p95"].Baseline)
    $candidateBottleneck = [Math]::Max($metrics["cpu-p95"].Candidate, $metrics["gpu-p95"].Candidate)
    $frameImprovementMs = $baselineBottleneck - $candidateBottleneck
    $frameImprovementPercent = Get-ImprovementPercent $baselineBottleneck $candidateBottleneck
    $frameBootstrapLower = Get-BootstrapLowerBound `
        $PairedDifferences `
        ([int]$Manifest.acceptance.bootstrapSamples) `
        ([double]$Manifest.acceptance.bootstrapConfidence)
    $regressions = @()
    foreach ($key in @("cpu-p95", "cpu-p99", "gpu-p95", "gpu-p99")) {
        $regression = -(Get-ImprovementPercent $metrics[$key].Baseline $metrics[$key].Candidate)
        if ($regression -gt [double]$Manifest.acceptance.maximumRegressionPercent) {
            $regressions += "$key regressed by $([Math]::Round($regression, 3))%"
        }
    }

    $targetClaims = @((Get-PropertyValue $Workload "campaignTargetClaims" @()))
    if ($RequireWin -and $targetClaims.Count -eq 0) {
        throw "Target workload '$($Workload.id)' has no authenticated hypothesis claim."
    }
    if (-not $RequireWin -and $targetClaims.Count -ne 0) {
        throw "Non-target workload '$($Workload.id)' unexpectedly carries target claims."
    }
    $frameWin =
        $frameImprovementPercent -ge [double]$Manifest.acceptance.minimumFrameImprovementPercent -and
        $frameImprovementMs -ge [double]$Manifest.acceptance.minimumFrameImprovementMilliseconds -and
        $frameBootstrapLower -gt 0.0
    $targetClaimResults = @()
    foreach ($claim in $targetClaims) {
        $targetDomain = [string]$claim.targetDomain
        $targetPass = [string]$claim.targetPass
        $targetIdentity = "${targetDomain}::${targetPass}"
        $baselineValues = @($BaselineReports | ForEach-Object {
            Get-ScopedTiming $_ $targetDomain $targetPass
        } | Where-Object { $null -ne $_ })
        $candidateValues = @($CandidateReports | ForEach-Object {
            Get-ScopedTiming $_ $targetDomain $targetPass
        } | Where-Object { $null -ne $_ })
        $allPassValues = @($baselineValues) + @($candidateValues)
        if ($baselineValues.Count -ne @($BaselineReports).Count -or
            $candidateValues.Count -ne @($CandidateReports).Count -or
            @($allPassValues | Where-Object {
                -not [double]::IsFinite([double]$_) -or [double]$_ -le 0.0
            }).Count -ne 0) {
            throw "Workload '$($Workload.id)' lacks a finite positive '$targetIdentity' sample in every ABBA slot."
        }
        $passBaseline = Get-Median $baselineValues
        $passCandidate = Get-Median $candidateValues
        $passImprovementMs = $passBaseline - $passCandidate
        $passImprovementPercent = Get-ImprovementPercent $passBaseline $passCandidate
        $differenceProperty =
            $TargetPassPairedDifferences.PSObject.Properties[$targetIdentity]
        $passDifferences = if ($null -eq $differenceProperty) {
            @($TargetPassPairedDifferences[$targetIdentity])
        } else { @($differenceProperty.Value) }
        if ($passDifferences.Count -ne $PairedDifferences.Count) {
            throw "Target timing '$targetIdentity' has incomplete paired differences."
        }
        $passBootstrapLower = Get-BootstrapLowerBound `
            ([double[]]$passDifferences) `
            ([int]$Manifest.acceptance.bootstrapSamples) `
            ([double]$Manifest.acceptance.bootstrapConfidence)
        $passWin =
            $passImprovementPercent -ge
                [double]$Manifest.acceptance.minimumPassImprovementPercent -and
            $passImprovementMs -ge
                [double]$Manifest.acceptance.minimumPassImprovementMilliseconds -and
            $passBootstrapLower -gt 0.0 -and
            $candidateBottleneck -le $baselineBottleneck
        $targetClaimResults += [pscustomobject]@{
            TargetHypothesisId = [string]$claim.hypothesisId
            Scene = [string]$claim.scene
            WorkloadId = [string]$Workload.id
            TargetDomain = $targetDomain
            TargetPass = $targetPass
            TargetPassPairedDifferencesMilliseconds = @(
                $passDifferences | ForEach-Object { [double]$_ })
            TargetPassBootstrapLower95Milliseconds = $passBootstrapLower
            TargetPassBaselineP95Milliseconds = $passBaseline
            TargetPassCandidateP95Milliseconds = $passCandidate
            TargetPassImprovementMilliseconds = $passImprovementMs
            TargetPassImprovementPercent = $passImprovementPercent
            FrameWin = $frameWin
            PassWin = $passWin
            Decision = if ($frameWin -or $passWin) { "keep" } else { "rollback" }
        }
    }
    $qualityRepeatability = [pscustomobject]@{
        BaselineRelativeRmseMaximum = [double](@($BaselineReports | ForEach-Object { [double]$_.HdrDifference.RelativeRmse } | Measure-Object -Maximum).Maximum)
        CandidateRelativeRmseMaximum = [double](@($CandidateReports | ForEach-Object { [double]$_.HdrDifference.RelativeRmse } | Measure-Object -Maximum).Maximum)
        BaselineFlipP95Maximum = [double](@($BaselineReports | ForEach-Object { [double]$_.HdrDifference.FlipP95 } | Measure-Object -Maximum).Maximum)
        CandidateFlipP95Maximum = [double](@($CandidateReports | ForEach-Object { [double]$_.HdrDifference.FlipP95 } | Measure-Object -Maximum).Maximum)
        RoiEnvelopes = @()
    }
    $roiRegression = $false
    foreach ($baselineRoi in @($BaselineReports[0].HdrDifference.RoiResults)) {
        $roiName = [string]$baselineRoi.Name
        $baselineMeanMaximum = [double](@(
            $BaselineReports |
                ForEach-Object { $_.HdrDifference.RoiResults } |
                Where-Object { [string]$_.Name -eq $roiName } |
                ForEach-Object { [double]$_.MeanLuminanceShift } |
                Measure-Object -Maximum).Maximum)
        $candidateMeanMaximum = [double](@(
            $CandidateReports |
                ForEach-Object { $_.HdrDifference.RoiResults } |
                Where-Object { [string]$_.Name -eq $roiName } |
                ForEach-Object { [double]$_.MeanLuminanceShift } |
                Measure-Object -Maximum).Maximum)
        $baselineP95Maximum = [double](@(
            $BaselineReports |
                ForEach-Object { $_.HdrDifference.RoiResults } |
                Where-Object { [string]$_.Name -eq $roiName } |
                ForEach-Object { [double]$_.P95LuminanceShift } |
                Measure-Object -Maximum).Maximum)
        $candidateP95Maximum = [double](@(
            $CandidateReports |
                ForEach-Object { $_.HdrDifference.RoiResults } |
                Where-Object { [string]$_.Name -eq $roiName } |
                ForEach-Object { [double]$_.P95LuminanceShift } |
                Measure-Object -Maximum).Maximum)
        if ($candidateMeanMaximum -gt $baselineMeanMaximum -or
            $candidateP95Maximum -gt $baselineP95Maximum) {
            $roiRegression = $true
        }
        $qualityRepeatability.RoiEnvelopes += [pscustomobject]@{
            Name = $roiName
            BaselineMeanMaximum = $baselineMeanMaximum
            CandidateMeanMaximum = $candidateMeanMaximum
            BaselineP95Maximum = $baselineP95Maximum
            CandidateP95Maximum = $candidateP95Maximum
        }
    }
    $qualityRegression =
        $qualityRepeatability.CandidateRelativeRmseMaximum -gt
            $qualityRepeatability.BaselineRelativeRmseMaximum -or
        $qualityRepeatability.CandidateFlipP95Maximum -gt
            $qualityRepeatability.BaselineFlipP95Maximum -or
        $roiRegression

    if ($regressions.Count -ne 0 -or $qualityRegression) {
        foreach ($claimResult in $targetClaimResults) {
            $claimResult.Decision = "rollback"
        }
    }

    $decision = "rollback"
    $reason = if ($RequireWin) {
        "no statistically supported frame or isolated-pass win"
    } else {
        "quality or cross-metric non-regression failed"
    }
    if ($regressions.Count -gt 0) {
        $reason = "cross-metric regression: $($regressions -join '; ')"
    } elseif ($qualityRegression) {
        $reason = "candidate quality exceeded clean-baseline repeatability"
    } elseif (-not $RequireWin) {
        $decision = "keep"
        $reason = "strict quality and cross-metric non-regression passed"
    } elseif (@($targetClaimResults | Where-Object {
            [string]$_.Decision -cne "keep"
        }).Count -eq 0) {
        $decision = "keep"
        $reason = "every authenticated target claim has an independent frame or pass win"
    }
    return [pscustomobject]@{
        Workload = [string]$Workload.id
        GateMode = if ($RequireWin) { "target-win" } else { "qualification-nonregression" }
        Decision = $decision
        Reason = $reason
        BaselineBottleneckP95Milliseconds = $baselineBottleneck
        CandidateBottleneckP95Milliseconds = $candidateBottleneck
        FrameImprovementMilliseconds = $frameImprovementMs
        FrameImprovementPercent = $frameImprovementPercent
        FramePairedDifferencesMilliseconds = @(
            $PairedDifferences | ForEach-Object { [double]$_ })
        FrameBootstrapLower95Milliseconds = $frameBootstrapLower
        TargetClaimResults = @($targetClaimResults)
        Metrics = $metrics
        Regressions = $regressions
        QualityRepeatability = $qualityRepeatability
    }
}

function Get-AbbaPairId {
    param(
        $Manifest,
        [string]$Configuration,
        [string]$WorkloadId,
        [string]$Stage,
        [string]$BaselineCommit,
        [string]$CandidateCommit,
        [int]$Iteration,
        [int]$Cycle)
    return (
        "$($Manifest.campaignId)-$Configuration-$WorkloadId-$Stage-" +
        "$BaselineCommit-$CandidateCommit-" +
        "$Iteration-$Cycle")
}

function Invoke-AbbaWorkload {
    param(
        $Manifest,
        $Workload,
        $BaselineBuild,
        $CandidateBuild,
        $VerifierBuildIdentity,
        [string]$Configuration,
        [int]$Iteration,
        [string]$Stage,
        $ReferenceEntry,
        [string]$BaselineCommit,
        [string]$CandidateCommit,
        [bool]$RequireWin,
        [string]$ArtifactRoot = ""
    )
    $baselineReports = @()
    $candidateReports = @()
    $baselinePhaseIdentity = $null
    $candidatePhaseIdentity = $null
    $pairedDifferences = @()
    $orderedCycleReports = @()
    $slotEvidence = @()
    $root = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        Join-Path $script:RunRoot (
            "iterations/{0:D6}/{1}/{2}/{3}" -f
                $Iteration, $Stage, $Configuration, [string]$Workload.id)
    } else {
        Join-Path $ArtifactRoot (
            "captures/{0}/{1}/{2}" -f
                $Stage, $Configuration, [string]$Workload.id)
    }
    for ($cycle = 1; $cycle -le [int]$Manifest.capture.abbaCycles; $cycle++) {
        $slots = @(
            [pscustomobject]@{ Phase = "baseline"; Build = $BaselineBuild; Commit = $BaselineCommit },
            [pscustomobject]@{ Phase = "candidate"; Build = $CandidateBuild; Commit = $CandidateCommit },
            [pscustomobject]@{ Phase = "candidate"; Build = $CandidateBuild; Commit = $CandidateCommit },
            [pscustomobject]@{ Phase = "baseline"; Build = $BaselineBuild; Commit = $BaselineCommit })
        $cycleReports = @()
        for ($slot = 0; $slot -lt $slots.Count; $slot++) {
            $entry = $slots[$slot]
            $reportPath = Join-Path $root ("cycle-{0:D2}-slot-{1}-{2}.json" -f $cycle, $slot + 1, $entry.Phase)
            $pairId = Get-AbbaPairId `
                $Manifest $Configuration ([string]$Workload.id) $Stage `
                $BaselineCommit $CandidateCommit $Iteration $cycle
            $label = "$($Workload.id) $Configuration ABBA cycle $cycle slot $($slot + 1) $($entry.Phase)"
            $report = Invoke-BenchmarkCapture `
                -Manifest $Manifest `
                -Workload $Workload `
                -BuildIdentity $entry.Build `
                -VerifierBuildIdentity $VerifierBuildIdentity `
                -Configuration $Configuration `
                -ReportPath $reportPath `
                -PairId $pairId `
                -QualityContractPath ([string]$ReferenceEntry.qualityContractPath) `
                -ReferencePath ([string]$ReferenceEntry.path) `
                -ReferenceInitialization $false `
                -ExpectedCommit ([string]$entry.Commit) `
                -ExpectedQualityContractSha256 ([string]$ReferenceEntry.qualityContractSha256) `
                -ExpectedReferenceSha256 ([string]$ReferenceEntry.sha256) `
                -ReferenceIdentity $ReferenceEntry `
                -Label $label
            $healthPath = [System.IO.Path]::ChangeExtension(
                $reportPath,
                ".health.json")
            $candidatePath = [System.IO.Path]::ChangeExtension(
                $reportPath,
                ".hdr.pfm")
            foreach ($evidencePath in @(
                    $reportPath,
                    $healthPath,
                    $candidatePath)) {
                if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
                    throw "$label is missing admitted evidence '$evidencePath'."
                }
            }
            $slotEvidence += [pscustomobject]@{
                cycle = $cycle
                slot = $slot + 1
                phase = [string]$entry.Phase
                pairId = $pairId
                reportPath = [System.IO.Path]::GetFullPath($reportPath)
                reportSha256 = Get-Sha256 $reportPath
                healthPath = [System.IO.Path]::GetFullPath($healthPath)
                healthSha256 = Get-Sha256 $healthPath
                candidatePfmPath = [System.IO.Path]::GetFullPath($candidatePath)
                candidatePfmSha256 = Get-Sha256 $candidatePath
                referencePfmPath = [System.IO.Path]::GetFullPath(
                    [string]$ReferenceEntry.path)
                referencePfmSha256 = [string]$ReferenceEntry.sha256
                qualityContractPath = [System.IO.Path]::GetFullPath(
                    [string]$ReferenceEntry.qualityContractPath)
                qualityContractSha256 =
                    [string]$ReferenceEntry.qualityContractSha256
                buildRootPath = [System.IO.Path]::GetFullPath(
                    [string]$entry.Build.RootPath)
                buildIdentity = $entry.Build
                buildBundleFingerprint = [string]$entry.Build.BundleFingerprint
                executableFileSha256 = [string]$entry.Build.ExecutableFileSha256
                runtimeExecutableBundleHash =
                    [string]$entry.Build.RuntimeExecutableBundleHash
                captureExecutableHash =
                    [string]$report.LastDiagnostics.CaptureRun.ExecutableHash
                captureShaderBundleHash =
                    [string]$report.LastDiagnostics.CaptureRun.ShaderBundleHash
                captureApplicationVersion =
                    [string]$report.LastDiagnostics.CaptureRun.ApplicationVersion
                captureSettingsSchemaVersion =
                    [int]$report.LastDiagnostics.CaptureRun.SettingsSchemaVersion
                captureCommit =
                    [string]$report.LastDiagnostics.CaptureRun.Commit
                captureDirtyWorktreeState =
                    [string]$report.LastDiagnostics.CaptureRun.DirtyWorktreeState
                producerSchema = [string]$report.ProducerIdentity.Schema
                producerSettingsFingerprint =
                    [string]$report.ProducerIdentity.SettingsFingerprint
                producerGpuName = [string]$report.ProducerIdentity.GpuName
                producerDriverVersion =
                    [string]$report.ProducerIdentity.DriverVersion
                producerQualityTier =
                    [string]$report.ProducerIdentity.QualityTier
                trajectory = [string]$report.CaptureContract.Trajectory
                trajectoryFingerprint =
                    [string]$report.CaptureContract.TrajectoryFingerprint
                trajectoryFrameCount =
                    [int]$report.CaptureContract.TrajectoryFrameCount
                trajectoryRouteHash =
                    [string]$report.CaptureContract.TrajectoryRouteHash
                trajectorySequenceHash =
                    [string]$report.CaptureContract.TrajectorySequenceHash
                frozenVerifierEvidence =
                    $report.CampaignFrozenVerifierEvidence
            }
            $cycleReports += $report
            if ($entry.Phase -eq "baseline") {
                Assert-WithinPhaseIdentity $baselinePhaseIdentity $report $label
                if ($null -eq $baselinePhaseIdentity) { $baselinePhaseIdentity = $report }
                $baselineReports += $report
            } else {
                Assert-WithinPhaseIdentity $candidatePhaseIdentity $report $label
                if ($null -eq $candidatePhaseIdentity) { $candidatePhaseIdentity = $report }
                $candidateReports += $report
            }
        }
        $orderedCycleReports += $cycleReports
        $pairedDifferences += [Math]::Max(
            (Get-Timing $cycleReports[0] "cpu" "p95"),
            (Get-Timing $cycleReports[0] "gpu" "p95")) - [Math]::Max(
            (Get-Timing $cycleReports[1] "cpu" "p95"),
            (Get-Timing $cycleReports[1] "gpu" "p95"))
        $pairedDifferences += [Math]::Max(
            (Get-Timing $cycleReports[3] "cpu" "p95"),
            (Get-Timing $cycleReports[3] "gpu" "p95")) - [Math]::Max(
            (Get-Timing $cycleReports[2] "cpu" "p95"),
            (Get-Timing $cycleReports[2] "gpu" "p95"))
    }
    $targetPassPairedDifferences = Get-TargetPassPairedDifferences `
        $Workload $orderedCycleReports
    $comparison = Compare-WorkloadCaptures `
        $Manifest $Workload $baselineReports $candidateReports `
        ([double[]]$pairedDifferences) $targetPassPairedDifferences $RequireWin
    $comparison | Add-Member `
        -NotePropertyName SlotEvidence `
        -NotePropertyValue @($slotEvidence)
    return $comparison
}

function Get-ConfigurationWorkloadSelection {
    param(
        $Manifest,
        [object[]]$WinWorkloads,
        [bool]$RunAllWorkloads)
    $selectedIds = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $orderedWorkloads = [System.Collections.Generic.List[object]]::new()
    foreach ($workload in $WinWorkloads) {
        if ($selectedIds.Add([string]$workload.id)) {
            $orderedWorkloads.Add($workload)
        }
    }
    $winIsolationGroups = @($WinWorkloads |
        ForEach-Object {
            [string](Get-PropertyValue $_ "isolationGroup" "")
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique)
    foreach ($workload in @($Manifest.workloads)) {
        $group = [string](Get-PropertyValue $workload "isolationGroup" "")
        if (-not [string]::IsNullOrWhiteSpace($group) -and
            $winIsolationGroups -contains $group -and
            $selectedIds.Add([string]$workload.id)) {
            $orderedWorkloads.Add($workload)
        }
    }
    foreach ($workload in @($Manifest.workloads)) {
        if (($RunAllWorkloads -or [bool]$workload.qualification) -and
            $selectedIds.Add([string]$workload.id)) {
            $orderedWorkloads.Add($workload)
        }
    }
    return @($orderedWorkloads)
}

function New-ConfigurationControlledIsolationEvidence {
    param(
        [object[]]$Comparisons,
        $VerifierBuildIdentity,
        [string]$Root,
        [bool]$RequireAvailable,
        [string]$Label)
    $cachedId = "sponza-directional-shadow-moving-caster"
    $forcedId = "sponza-directional-shadow-forced-refresh"
    $cachedComparison = @($Comparisons | Where-Object {
        [string]$_.Workload -ceq $cachedId
    })
    $forcedComparison = @($Comparisons | Where-Object {
        [string]$_.Workload -ceq $forcedId
    })
    if (-not $RequireAvailable) {
        return [ordered]@{
            schema = "njulf-perf-campaign-controlled-isolation-matrix/v1"
            applicable = $false
            cachedWorkloadId = $cachedId
            forcedWorkloadId = $forcedId
            pairs = @()
        }
    }
    if ($cachedComparison.Count -ne 1 -or $forcedComparison.Count -ne 1) {
        throw "$Label controlled isolation requires both exact workload roles."
    }
    $cachedSlots = @($cachedComparison[0].SlotEvidence)
    $forcedSlots = @($forcedComparison[0].SlotEvidence)
    if ($cachedSlots.Count -ne $forcedSlots.Count -or
        $cachedSlots.Count -eq 0) {
        throw "$Label controlled isolation slot topology differs."
    }
    $pairs = @()
    for ($index = 0; $index -lt $cachedSlots.Count; $index++) {
        $cachedSlot = $cachedSlots[$index]
        $forcedSlot = $forcedSlots[$index]
        if ([int]$cachedSlot.cycle -ne [int]$forcedSlot.cycle -or
            [int]$cachedSlot.slot -ne [int]$forcedSlot.slot -or
            [string]$cachedSlot.phase -cne [string]$forcedSlot.phase -or
            (($cachedSlot.buildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
             ($forcedSlot.buildIdentity | ConvertTo-Json -Depth 12 -Compress))) {
            throw "$Label controlled isolation pair $index crosses a slot/phase/build."
        }
        $cachedReport = Read-BenchmarkReport ([string]$cachedSlot.reportPath)
        $forcedReport = Read-BenchmarkReport ([string]$forcedSlot.reportPath)
        $artifactPath = Join-Path $Root (
            "cycle-{0:D2}-slot-{1}-{2}.json" -f
                [int]$cachedSlot.cycle,
                [int]$cachedSlot.slot,
                [string]$cachedSlot.phase)
        $artifact = New-ControlledIsolationFrozenVerifierEvidence `
            $cachedReport $forcedReport ([string]$cachedSlot.reportPath) `
            ([string]$forcedSlot.reportPath) $VerifierBuildIdentity `
            $artifactPath "$Label pair $index"
        $pairs += [ordered]@{
            cycle = [int]$cachedSlot.cycle
            slot = [int]$cachedSlot.slot
            phase = [string]$cachedSlot.phase
            artifact = $artifact
        }
    }
    return [ordered]@{
        schema = "njulf-perf-campaign-controlled-isolation-matrix/v1"
        applicable = $true
        cachedWorkloadId = $cachedId
        forcedWorkloadId = $forcedId
        pairs = @($pairs)
    }
}

function Assert-ConfigurationControlledIsolationEvidence {
    param(
        [object[]]$Comparisons,
        $Evidence,
        $VerifierBuildIdentity,
        [string]$Root,
        [bool]$ExpectedApplicable,
        [string]$Label)
    Assert-ExactPropertyNames $Evidence @(
        "schema", "applicable", "cachedWorkloadId",
        "forcedWorkloadId", "pairs") "$Label envelope"
    $cachedId = "sponza-directional-shadow-moving-caster"
    $forcedId = "sponza-directional-shadow-forced-refresh"
    if ([string]$Evidence.schema -cne
            "njulf-perf-campaign-controlled-isolation-matrix/v1" -or
        $Evidence.applicable -isnot [bool] -or
        $Evidence.applicable -ne $ExpectedApplicable -or
        [string]$Evidence.cachedWorkloadId -cne $cachedId -or
        [string]$Evidence.forcedWorkloadId -cne $forcedId) {
        throw "$Label controlled isolation envelope differs."
    }
    if (-not $ExpectedApplicable) {
        if (@($Evidence.pairs).Count -ne 0) {
            throw "$Label non-applicable controlled isolation contains pairs."
        }
        return $Evidence
    }
    $cachedComparison = @($Comparisons | Where-Object {
        [string]$_.Workload -ceq $cachedId
    })
    $forcedComparison = @($Comparisons | Where-Object {
        [string]$_.Workload -ceq $forcedId
    })
    if ($cachedComparison.Count -ne 1 -or $forcedComparison.Count -ne 1) {
        throw "$Label controlled isolation comparison roles differ."
    }
    $cachedSlots = @($cachedComparison[0].SlotEvidence)
    $forcedSlots = @($forcedComparison[0].SlotEvidence)
    $pairs = @($Evidence.pairs)
    if ($pairs.Count -ne $cachedSlots.Count -or
        $pairs.Count -ne $forcedSlots.Count) {
        throw "$Label controlled isolation pair count differs."
    }
    for ($index = 0; $index -lt $pairs.Count; $index++) {
        $pair = $pairs[$index]
        $cachedSlot = $cachedSlots[$index]
        $forcedSlot = $forcedSlots[$index]
        Assert-ExactPropertyNames $pair @(
            "cycle", "slot", "phase", "artifact") "$Label pair $index"
        if ([int]$pair.cycle -ne [int]$cachedSlot.cycle -or
            [int]$pair.cycle -ne [int]$forcedSlot.cycle -or
            [int]$pair.slot -ne [int]$cachedSlot.slot -or
            [int]$pair.slot -ne [int]$forcedSlot.slot -or
            [string]$pair.phase -cne [string]$cachedSlot.phase -or
            [string]$pair.phase -cne [string]$forcedSlot.phase -or
            (($cachedSlot.buildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
             ($forcedSlot.buildIdentity | ConvertTo-Json -Depth 12 -Compress))) {
            throw "$Label controlled isolation pair $index crosses identity."
        }
        $cachedReport = Read-BenchmarkReport ([string]$cachedSlot.reportPath)
        $forcedReport = Read-BenchmarkReport ([string]$forcedSlot.reportPath)
        $artifactPath = Join-Path $Root (
            "cycle-{0:D2}-slot-{1}-{2}.json" -f
                [int]$pair.cycle, [int]$pair.slot, [string]$pair.phase)
        $null = Assert-ControlledIsolationFrozenVerifierEvidence `
            $cachedReport $forcedReport ([string]$cachedSlot.reportPath) `
            ([string]$forcedSlot.reportPath) $pair.artifact `
            $VerifierBuildIdentity $artifactPath "$Label pair $index"
    }
    return $Evidence
}

function Invoke-ConfigurationMatrix {
    param(
        $Manifest,
        $Lock,
        [object[]]$WinWorkloads,
        $BaselineBuild,
        $CandidateBuild,
        [string]$Configuration,
        [int]$Iteration,
        [string]$Stage,
        [string]$BaselineCommit,
        [string]$CandidateCommit,
        [string]$ArtifactRoot = "",
        [bool]$RunAllWorkloads = $false)
    $comparisons = @()
    $winIds = @($WinWorkloads | ForEach-Object { [string]$_.id })
    $orderedWorkloads = @(Get-ConfigurationWorkloadSelection `
        $Manifest $WinWorkloads $RunAllWorkloads)
    $verifierBuild = Get-ReferenceBuildIdentity $Lock $Configuration
    Assert-BuildIdentity $verifierBuild `
        "$Configuration immutable timing verifier"
    foreach ($workload in $orderedWorkloads) {
        $requireWin = $winIds -contains [string]$workload.id
        $workloadStage = if ($requireWin) {
            $Stage
        } else {
            "$Stage-nonregression"
        }
        $entry = Get-ReferenceLockEntry `
            $Lock $Configuration ([string]$workload.id)
        $comparison = Invoke-AbbaWorkload `
            $Manifest $workload $BaselineBuild $CandidateBuild $verifierBuild `
            $Configuration $Iteration $workloadStage `
            $entry $BaselineCommit $CandidateCommit $requireWin $ArtifactRoot
        $comparisons += $comparison
        if ($comparison.Decision -ne "keep") { break }
    }
    $failures = @($comparisons | Where-Object { $_.Decision -ne "keep" })
    $hypothesisResults = @()
    if ($failures.Count -eq 0) {
        $hypothesisResults = @(New-ConfigurationHypothesisResults `
            $WinWorkloads $comparisons)
        if (@($hypothesisResults | Where-Object {
                [string]$_.Decision -cne "keep"
            }).Count -ne 0) {
            throw "One or more target hypotheses contradict kept workload comparisons."
        }
    }
    $controlledRoot = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        Join-Path $script:RunRoot (
            "iterations/{0:D6}/{1}/{2}/controlled-isolation" -f
                $Iteration, $Stage, $Configuration)
    } else {
        Join-Path $ArtifactRoot (
            "captures/{0}/{1}/controlled-isolation" -f
                $Stage, $Configuration)
    }
    $selectedIds = @($orderedWorkloads | ForEach-Object { [string]$_.id })
    $controlledApplicable =
        $failures.Count -eq 0 -and
        $selectedIds -contains "sponza-directional-shadow-moving-caster" -and
        $selectedIds -contains "sponza-directional-shadow-forced-refresh"
    $controlledIsolationEvidence = New-ConfigurationControlledIsolationEvidence `
        $comparisons $verifierBuild $controlledRoot $controlledApplicable `
        "$Configuration $Stage directional isolation"
    return [pscustomobject]@{
        configuration = $Configuration
        stage = $Stage
        decision = if ($failures.Count -eq 0) { "keep" } else { "rollback" }
        reason = if ($failures.Count -eq 0) {
            "target win plus quality/non-regression passed"
        } else {
            ($failures | ForEach-Object {
                "$($_.Workload): $($_.Reason)"
            }) -join '; '
        }
        comparisons = $comparisons
        hypothesisResults = @($hypothesisResults)
        controlledIsolationEvidence = $controlledIsolationEvidence
    }
}

function Assert-ConfigurationTimingEvidence {
    param(
        $Manifest,
        $Lock,
        [object[]]$WinWorkloads,
        $BaselineBuild,
        $CandidateBuild,
        [string]$Configuration,
        [int]$Iteration,
        [string]$Stage,
        [string]$BaselineCommit,
        [string]$CandidateCommit,
        $ConfigurationResult,
        [string]$ArtifactRoot = "",
        [bool]$RunAllWorkloads = $false,
        [string]$Label = "Timing matrix")
    if ([string]$ConfigurationResult.configuration -cne $Configuration -or
        [string]$ConfigurationResult.stage -cne $Stage -or
        [string]$ConfigurationResult.decision -cne "keep") {
        throw "$Label envelope is incomplete."
    }
    $configurationProperties = @(
        "configuration", "stage", "decision", "reason", "comparisons",
        "hypothesisResults", "controlledIsolationEvidence")
    if ($null -ne
        $ConfigurationResult.PSObject.Properties["qualitySequenceCompleted"]) {
        $configurationProperties += "qualitySequenceCompleted"
    }
    Assert-ExactPropertyNames $ConfigurationResult $configurationProperties `
        "$Label configuration result"
    $qualityCompleted = $null -ne
        $ConfigurationResult.PSObject.Properties["qualitySequenceCompleted"]
    $expectedReason = if ($qualityCompleted) {
        if (-not [bool]$ConfigurationResult.qualitySequenceCompleted) {
            throw "$Label quality completion flag is false."
        }
        "timing, standalone HDR sequence, and non-regression gates passed"
    } else {
        "target win plus quality/non-regression passed"
    }
    if ([string]$ConfigurationResult.reason -cne $expectedReason) {
        throw "$Label reason differs from recomputed evidence."
    }
    Assert-BuildIdentity $BaselineBuild "$Label baseline build"
    Assert-BuildIdentity $CandidateBuild "$Label candidate build"
    $verifierBuild = Get-ReferenceBuildIdentity $Lock $Configuration
    Assert-BuildIdentity $verifierBuild "$Label immutable verifier build"
    $workloads = @(Get-ConfigurationWorkloadSelection `
        $Manifest $WinWorkloads $RunAllWorkloads)
    $winIds = @($WinWorkloads | ForEach-Object { [string]$_.id })
    $comparisons = @($ConfigurationResult.comparisons)
    if ($comparisons.Count -ne $workloads.Count) {
        throw "$Label workload topology differs."
    }
    for ($workloadIndex = 0;
         $workloadIndex -lt $workloads.Count;
         $workloadIndex++) {
        $workload = $workloads[$workloadIndex]
        $comparison = $comparisons[$workloadIndex]
        $comparisonProperties = @(
            "Workload", "GateMode", "Decision", "Reason",
            "BaselineBottleneckP95Milliseconds",
            "CandidateBottleneckP95Milliseconds",
            "FrameImprovementMilliseconds", "FrameImprovementPercent",
            "FramePairedDifferencesMilliseconds",
            "FrameBootstrapLower95Milliseconds", "TargetClaimResults",
            "Metrics", "Regressions", "QualityRepeatability", "SlotEvidence")
        if ($null -ne
            $comparison.PSObject.Properties["QualitySequenceEvidence"]) {
            $comparisonProperties += "QualitySequenceEvidence"
        }
        Assert-ExactPropertyNames $comparison $comparisonProperties `
            "$Label '$($workload.id)' comparison"
        $requireWin = $winIds -contains [string]$workload.id
        $expectedGateMode = if ($requireWin) {
            "target-win"
        } else {
            "qualification-nonregression"
        }
        $workloadStage = if ($requireWin) { $Stage } else { "$Stage-nonregression" }
        if ([string]$comparison.Workload -cne [string]$workload.id -or
            [string]$comparison.GateMode -cne $expectedGateMode -or
            [string]$comparison.Decision -cne "keep") {
            throw "$Label workload $workloadIndex identity/decision differs."
        }
        $reference = Get-ReferenceLockEntry `
            $Lock $Configuration ([string]$workload.id)
        $captureRoot = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
            Join-Path $script:RunRoot (
                "iterations/{0:D6}/{1}/{2}/{3}" -f
                    $Iteration, $workloadStage, $Configuration,
                    [string]$workload.id)
        } else {
            Join-Path $ArtifactRoot (
                "captures/{0}/{1}/{2}" -f
                    $workloadStage, $Configuration, [string]$workload.id)
        }
        $slots = @($comparison.SlotEvidence)
        $expectedSlotCount = [int]$Manifest.capture.abbaCycles * 4
        if ($slots.Count -ne $expectedSlotCount) {
            throw "$Label '$($workload.id)' slot topology differs."
        }
        $baselineReports = @()
        $candidateReports = @()
        $allReports = @()
        $baselineIdentity = $null
        $candidateIdentity = $null
        for ($slotIndex = 0; $slotIndex -lt $slots.Count; $slotIndex++) {
            $slot = $slots[$slotIndex]
            $cycle = [int][Math]::Floor($slotIndex / 4) + 1
            $slotNumber = ($slotIndex % 4) + 1
            $phase = @("baseline", "candidate", "candidate", "baseline")[$slotNumber - 1]
            $expectedBuild = if ($phase -eq "baseline") {
                $BaselineBuild
            } else {
                $CandidateBuild
            }
            $expectedCommit = if ($phase -eq "baseline") {
                $BaselineCommit
            } else {
                $CandidateCommit
            }
            $pairId = Get-AbbaPairId `
                $Manifest $Configuration ([string]$workload.id) $workloadStage `
                $BaselineCommit $CandidateCommit $Iteration $cycle
            $reportPath = Join-Path $captureRoot (
                "cycle-{0:D2}-slot-{1}-{2}.json" -f
                    $cycle, $slotNumber, $phase)
            $healthPath = [System.IO.Path]::ChangeExtension(
                $reportPath,
                ".health.json")
            $candidatePath = [System.IO.Path]::ChangeExtension(
                $reportPath,
                ".hdr.pfm")
            if ([int]$slot.cycle -ne $cycle -or
                [int]$slot.slot -ne $slotNumber -or
                [string]$slot.phase -cne $phase -or
                [string]$slot.pairId -cne $pairId) {
                throw "$Label '$($workload.id)' slot $slotIndex identity differs."
            }
            Assert-PathIdentity ([string]$slot.reportPath) $reportPath `
                "$Label report"
            Assert-PathIdentity ([string]$slot.healthPath) $healthPath `
                "$Label health"
            Assert-PathIdentity ([string]$slot.candidatePfmPath) $candidatePath `
                "$Label candidate PFM"
            Assert-PathIdentity `
                ([string]$slot.referencePfmPath) ([string]$reference.path) `
                "$Label reference PFM"
            Assert-PathIdentity `
                ([string]$slot.qualityContractPath) `
                ([string]$reference.qualityContractPath) `
                "$Label quality contract"
            Assert-PathIdentity `
                ([string]$slot.buildRootPath) ([string]$expectedBuild.RootPath) `
                "$Label slot build"
            foreach ($item in @(
                    @($reportPath, [string]$slot.reportSha256, "report"),
                    @($healthPath, [string]$slot.healthSha256, "health"),
                    @($candidatePath, [string]$slot.candidatePfmSha256, "candidate PFM"))) {
                if ([string]$item[1] -cnotmatch '^[0-9a-f]{64}$' -or
                    -not (Test-Path -LiteralPath ([string]$item[0]) -PathType Leaf) -or
                    (Get-Sha256 ([string]$item[0])) -cne [string]$item[1]) {
                    throw "$Label '$($workload.id)' slot $slotIndex $($item[2]) changed."
                }
            }
            if ([string]$slot.referencePfmSha256 -cne [string]$reference.sha256 -or
                [string]$slot.qualityContractSha256 -cne
                    [string]$reference.qualityContractSha256 -or
                [string]$slot.buildBundleFingerprint -cne
                    [string]$expectedBuild.BundleFingerprint -or
                [string]$slot.executableFileSha256 -cne
                    [string]$expectedBuild.ExecutableFileSha256 -or
                [string]$slot.runtimeExecutableBundleHash -cne
                    [string]$expectedBuild.RuntimeExecutableBundleHash) {
                throw "$Label '$($workload.id)' slot $slotIndex frozen input/build differs."
            }
            $report = Read-BenchmarkReport $reportPath
            Assert-BenchmarkReport `
                $Manifest $workload $report $Configuration `
                "$Label '$($workload.id)' slot $slotIndex" $false `
                $pairId $expectedCommit $expectedBuild $reference $candidatePath
            $health = Get-Content -LiteralPath $healthPath -Raw |
                ConvertFrom-Json -DateKind String
            Assert-HealthReport `
                $Manifest $workload $health $report $expectedBuild `
                $expectedCommit $pairId `
                "$Label '$($workload.id)' slot $slotIndex"
            if (($slot.buildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
                ($expectedBuild | ConvertTo-Json -Depth 12 -Compress)) {
                throw "$Label '$($workload.id)' slot $slotIndex full build identity differs."
            }
            $null = Assert-TimingFrozenVerifierEvidence `
                $workload $report $reportPath $slot.frozenVerifierEvidence `
                $verifierBuild `
                "$Label '$($workload.id)' slot $slotIndex"
            $recordedPairs = @(
                @([string]$slot.captureExecutableHash, [string]$report.LastDiagnostics.CaptureRun.ExecutableHash),
                @([string]$slot.captureShaderBundleHash, [string]$report.LastDiagnostics.CaptureRun.ShaderBundleHash),
                @([string]$slot.captureApplicationVersion, [string]$report.LastDiagnostics.CaptureRun.ApplicationVersion),
                @([string]$slot.captureSettingsSchemaVersion, [string]$report.LastDiagnostics.CaptureRun.SettingsSchemaVersion),
                @([string]$slot.captureCommit, [string]$report.LastDiagnostics.CaptureRun.Commit),
                @([string]$slot.captureDirtyWorktreeState, [string]$report.LastDiagnostics.CaptureRun.DirtyWorktreeState),
                @([string]$slot.producerSchema, [string]$report.ProducerIdentity.Schema),
                @([string]$slot.producerSettingsFingerprint, [string]$report.ProducerIdentity.SettingsFingerprint),
                @([string]$slot.producerGpuName, [string]$report.ProducerIdentity.GpuName),
                @([string]$slot.producerDriverVersion, [string]$report.ProducerIdentity.DriverVersion),
                @([string]$slot.producerQualityTier, [string]$report.ProducerIdentity.QualityTier),
                @([string]$slot.trajectory, [string]$report.CaptureContract.Trajectory),
                @([string]$slot.trajectoryFingerprint, [string]$report.CaptureContract.TrajectoryFingerprint),
                @([string]$slot.trajectoryFrameCount, [string]$report.CaptureContract.TrajectoryFrameCount),
                @([string]$slot.trajectoryRouteHash, [string]$report.CaptureContract.TrajectoryRouteHash),
                @([string]$slot.trajectorySequenceHash, [string]$report.CaptureContract.TrajectorySequenceHash))
            foreach ($pair in $recordedPairs) {
                if ([string]$pair[0] -cne [string]$pair[1]) {
                    throw "$Label '$($workload.id)' slot $slotIndex duplicated provenance differs."
                }
            }
            $allReports += $report
            if ($phase -eq "baseline") {
                Assert-WithinPhaseIdentity `
                    $baselineIdentity $report `
                    "$Label '$($workload.id)' baseline slot $slotIndex"
                if ($null -eq $baselineIdentity) { $baselineIdentity = $report }
                $baselineReports += $report
            } else {
                Assert-WithinPhaseIdentity `
                    $candidateIdentity $report `
                    "$Label '$($workload.id)' candidate slot $slotIndex"
                if ($null -eq $candidateIdentity) { $candidateIdentity = $report }
                $candidateReports += $report
            }
        }
        $pairedDifferences = @()
        for ($cycleIndex = 0;
             $cycleIndex -lt [int]$Manifest.capture.abbaCycles;
             $cycleIndex++) {
            $offset = $cycleIndex * 4
            $pairedDifferences += [Math]::Max(
                (Get-Timing $allReports[$offset] "cpu" "p95"),
                (Get-Timing $allReports[$offset] "gpu" "p95")) -
                [Math]::Max(
                    (Get-Timing $allReports[$offset + 1] "cpu" "p95"),
                    (Get-Timing $allReports[$offset + 1] "gpu" "p95"))
            $pairedDifferences += [Math]::Max(
                (Get-Timing $allReports[$offset + 3] "cpu" "p95"),
                (Get-Timing $allReports[$offset + 3] "gpu" "p95")) -
                [Math]::Max(
                    (Get-Timing $allReports[$offset + 2] "cpu" "p95"),
                    (Get-Timing $allReports[$offset + 2] "gpu" "p95"))
        }
        $recomputed = Compare-WorkloadCaptures `
            $Manifest $workload $baselineReports $candidateReports `
            ([double[]]$pairedDifferences) `
            (Get-TargetPassPairedDifferences $workload $allReports) `
            $requireWin
        if ([string]$recomputed.Decision -cne "keep") {
            throw "$Label '$($workload.id)' no longer recomputes to keep: $($recomputed.Reason)"
        }
        foreach ($property in $recomputed.PSObject.Properties) {
            $stored = $comparison.PSObject.Properties[$property.Name]
            if ($null -eq $stored -or
                (($property.Value | ConvertTo-Json -Depth 12 -Compress) -cne
                 ($stored.Value | ConvertTo-Json -Depth 12 -Compress))) {
                throw "$Label '$($workload.id)' stored '$($property.Name)' differs from recomputation."
            }
        }
        for ($slotIndex = 0; $slotIndex -lt $slots.Count; $slotIndex++) {
            $finalItems = @(
                    @([string]$slots[$slotIndex].reportPath, [string]$slots[$slotIndex].reportSha256),
                    @([string]$slots[$slotIndex].healthPath, [string]$slots[$slotIndex].healthSha256),
                    @([string]$slots[$slotIndex].candidatePfmPath, [string]$slots[$slotIndex].candidatePfmSha256),
                    @([string]$slots[$slotIndex].frozenVerifierEvidence.activation.artifactPath,
                      [string]$slots[$slotIndex].frozenVerifierEvidence.activation.artifactSha256),
                    @([string]$slots[$slotIndex].frozenVerifierEvidence.ddgiTransient.artifactPath,
                      [string]$slots[$slotIndex].frozenVerifierEvidence.ddgiTransient.artifactSha256))
            $sidecar = $slots[$slotIndex].frozenVerifierEvidence.sponzaAnimationSidecar
            if (-not [string]::IsNullOrWhiteSpace([string]$sidecar.path)) {
                $finalItems += ,@([string]$sidecar.path, [string]$sidecar.sha256)
            }
            foreach ($item in $finalItems) {
                if (-not (Test-Path -LiteralPath ([string]$item[0]) -PathType Leaf) -or
                    (Get-Sha256 ([string]$item[0])) -cne [string]$item[1]) {
                    throw "$Label '$($workload.id)' slot $slotIndex changed during re-audit."
                }
            }
        }
    }
    $recomputedHypotheses = @(New-ConfigurationHypothesisResults `
        $WinWorkloads $comparisons)
    if (($recomputedHypotheses | ConvertTo-Json -Depth 16 -Compress) -cne
        ($ConfigurationResult.hypothesisResults |
            ConvertTo-Json -Depth 16 -Compress) -or
        @($recomputedHypotheses | Where-Object {
            [string]$_.Decision -cne "keep"
        }).Count -ne 0) {
        throw "$Label target hypothesis results differ."
    }
    $controlledRoot = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        Join-Path $script:RunRoot (
            "iterations/{0:D6}/{1}/{2}/controlled-isolation" -f
                $Iteration, $Stage, $Configuration)
    } else {
        Join-Path $ArtifactRoot (
            "captures/{0}/{1}/controlled-isolation" -f
                $Stage, $Configuration)
    }
    $workloadIds = @($workloads | ForEach-Object { [string]$_.id })
    $controlledApplicable =
        $workloadIds -contains "sponza-directional-shadow-moving-caster" -and
        $workloadIds -contains "sponza-directional-shadow-forced-refresh"
    $null = Assert-ConfigurationControlledIsolationEvidence `
        $comparisons $ConfigurationResult.controlledIsolationEvidence `
        $verifierBuild $controlledRoot $controlledApplicable `
        "$Label directional isolation"
}

function Complete-ConfigurationQualityMatrix {
    param(
        $Manifest,
        $Lock,
        [object[]]$WinWorkloads,
        $CandidateBuild,
        [string]$Configuration,
        [int]$Iteration,
        [string]$Stage,
        [string]$CandidateCommit,
        $ConfigurationResult,
        [string]$ArtifactRoot = "",
        [bool]$RunAllWorkloads = $false)
    if ([string]$ConfigurationResult.decision -ne "keep") {
        throw "Quality sequences cannot qualify a failed timing matrix."
    }
    $orderedWorkloads = @(Get-ConfigurationWorkloadSelection `
        $Manifest $WinWorkloads $RunAllWorkloads)
    $comparisons = @($ConfigurationResult.comparisons)
    if ($comparisons.Count -ne $orderedWorkloads.Count) {
        throw "Quality matrix workload topology differs from completed timing."
    }
    $referenceBuild = Get-ReferenceBuildIdentity $Lock $Configuration
    Assert-BuildIdentity $referenceBuild `
        "$Configuration locked quality verifier"
    for ($index = 0; $index -lt $orderedWorkloads.Count; $index++) {
        $workload = $orderedWorkloads[$index]
        $comparison = $comparisons[$index]
        if ([string]$comparison.Workload -cne [string]$workload.id -or
            [string]$comparison.Decision -cne "keep") {
            throw "Quality matrix reordered or admitted a failed timing workload."
        }
        $reference = Get-ReferenceLockEntry `
            $Lock $Configuration ([string]$workload.id)
        $candidateContractPath =
            [string]$reference.qualitySequence.candidateReferenceContractPath
        $candidateContractHash =
            [string]$reference.qualitySequence.candidateReferenceContractSha256
        if (-not (Test-Path -LiteralPath $candidateContractPath -PathType Leaf) -or
            (Get-Sha256 $candidateContractPath) -cne $candidateContractHash) {
            throw "Locked quality candidate contract changed for '$($workload.id)'."
        }
        $candidateContract = Get-Content `
            -LiteralPath $candidateContractPath -Raw |
            ConvertFrom-Json -DateKind String
        $qualityStage = if ([string]$comparison.GateMode -eq "target-win") {
            $Stage
        } else {
            "$Stage-nonregression"
        }
        $root = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
            Join-Path $script:RunRoot (
                "iterations/{0:D6}/quality/{1}/{2}/{3}" -f
                    $Iteration, $qualityStage, $Configuration,
                    [string]$workload.id)
        } else {
            Join-Path $ArtifactRoot (
                "quality/{0}/{1}/{2}" -f
                    $qualityStage, $Configuration, [string]$workload.id)
        }
        $sequenceId = Get-QualitySequenceId `
            $Manifest $Configuration ([string]$workload.id) `
            "candidate" $qualityStage $CandidateCommit 0
        $evidence = Invoke-QualitySequenceCapture `
            -Manifest $Manifest `
            -Workload $workload `
            -BuildIdentity $CandidateBuild `
            -Configuration $Configuration `
            -Role "candidate" `
            -SequenceId $sequenceId `
            -ReportPath (Join-Path $root "report.json") `
            -OutputDirectory (Join-Path $root "checkpoints") `
            -ReferenceContractPath $candidateContractPath `
            -ExpectedReferenceContractSha256 $candidateContractHash `
            -QualityContractPath ([string]$reference.qualityContractPath) `
            -ExpectedQualityContractSha256 ([string]$reference.qualityContractSha256) `
            -ReferenceContract $candidateContract `
            -VerifierBuildIdentity $referenceBuild `
            -SpatialEnvelope @($reference.qualitySequence.spatialEnvelope) `
            -ExpectedCommit $CandidateCommit `
            -Label "$($workload.id) $Configuration standalone quality sequence"
        $comparison | Add-Member `
            -NotePropertyName QualitySequenceEvidence `
            -NotePropertyValue $evidence
    }
    $ConfigurationResult | Add-Member `
        -NotePropertyName qualitySequenceCompleted `
        -NotePropertyValue $true
    $ConfigurationResult.reason =
        "timing, standalone HDR sequence, and non-regression gates passed"
    return $ConfigurationResult
}

function Assert-ConfigurationQualityEvidence {
    param(
        $Manifest,
        $Lock,
        [object[]]$WinWorkloads,
        $CandidateBuild,
        [string]$Configuration,
        [int]$Iteration,
        [string]$Stage,
        [string]$CandidateCommit,
        $ConfigurationResult,
        [string]$ArtifactRoot = "",
        [bool]$RunAllWorkloads = $false,
        [string]$Label = "Quality matrix")
    if (-not [bool]$ConfigurationResult.qualitySequenceCompleted -or
        [string]$ConfigurationResult.decision -cne "keep") {
        throw "$Label did not complete successfully."
    }
    $workloads = @(Get-ConfigurationWorkloadSelection `
        $Manifest $WinWorkloads $RunAllWorkloads)
    $comparisons = @($ConfigurationResult.comparisons)
    if ($comparisons.Count -ne $workloads.Count) {
        throw "$Label workload topology differs."
    }
    $verifierBuild = Get-ReferenceBuildIdentity $Lock $Configuration
    for ($index = 0; $index -lt $workloads.Count; $index++) {
        $workload = $workloads[$index]
        $comparison = $comparisons[$index]
        $qualityStage = if ([string]$comparison.GateMode -eq "target-win") {
            $Stage
        } else {
            "$Stage-nonregression"
        }
        $reference = Get-ReferenceLockEntry `
            $Lock $Configuration ([string]$workload.id)
        $contractPath =
            [string]$reference.qualitySequence.candidateReferenceContractPath
        $contractHash =
            [string]$reference.qualitySequence.candidateReferenceContractSha256
        $contract = Get-Content -LiteralPath $contractPath -Raw |
            ConvertFrom-Json -DateKind String
        $root = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
            Join-Path $script:RunRoot (
                "iterations/{0:D6}/quality/{1}/{2}/{3}" -f
                    $Iteration, $qualityStage, $Configuration,
                    [string]$workload.id)
        } else {
            Join-Path $ArtifactRoot (
                "quality/{0}/{1}/{2}" -f
                    $qualityStage, $Configuration, [string]$workload.id)
        }
        $sequenceId = Get-QualitySequenceId `
            $Manifest $Configuration ([string]$workload.id) `
            "candidate" $qualityStage $CandidateCommit 0
        $null = Assert-QualitySequenceStoredEvidence `
            $Manifest $workload $comparison.QualitySequenceEvidence `
            $CandidateBuild $Configuration "candidate" $sequenceId `
            $CandidateCommit $contractPath $contractHash `
            ([string]$reference.qualityContractPath) `
            ([string]$reference.qualityContractSha256) $contract `
            $verifierBuild @($reference.qualitySequence.spatialEnvelope) `
            $root "$Label $Configuration/$($workload.id)"
    }
}

function Assert-FinalDecisionArtifacts {
    param(
        $Manifest,
        $Lock,
        [string]$DecisionPath,
        $ExpectedSummary,
        [object[]]$WinWorkloads,
        $CandidateBuilds,
        [int]$Iteration,
        [string]$RetainedHead,
        [string]$FinalRoot,
        $AcceptanceRefSnapshot,
        $RetainedChain)
    if (-not (Test-Path -LiteralPath $DecisionPath -PathType Leaf)) {
        throw "Final decision artifact is missing."
    }
    $decisionSha256 = Get-Sha256 $DecisionPath
    $decision = Get-Content -LiteralPath $DecisionPath -Raw |
        ConvertFrom-Json -DateKind String
    if (($decision | ConvertTo-Json -Depth 24 -Compress) -cne
        ($ExpectedSummary | ConvertTo-Json -Depth 24 -Compress)) {
        throw "Final decision bytes differ from the materialized summary."
    }
    Assert-ExactPropertyNames $decision @(
        "schema", "campaignId", "manifestSha256",
        "manifestSnapshotPath", "manifestSnapshotSha256",
        "lockSha256", "mode",
        "baselineCommit", "retainedHead", "observedHeadAtDecision",
        "headPreserved", "recoveryAttempted", "recoverySucceeded",
        "initialPostAttemptInvariantFailures", "postAttemptInvariantFailures",
        "authenticatedCommits", "lastAcceptanceEvidence", "decision",
        "reason", "targetHypotheses", "winWorkloads", "candidateBuilds",
        "configurations") "Final decision"
    if ([string]$decision.schema -cne "njulf-perf-campaign-final/v2" -or
        [string]$decision.campaignId -cne [string]$Manifest.campaignId -or
        [string]$decision.manifestSha256 -cne
            (Get-AdmittedCampaignManifestSha256) -or
        -not [string]::Equals(
            [string]$decision.manifestSnapshotPath,
            $script:CampaignManifestSnapshotPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$decision.manifestSnapshotSha256 -cne
            $script:CampaignManifestSha256 -or
        [string]$decision.lockSha256 -cne $script:CampaignLockSha256 -or
        [string]$decision.mode -cne "FinalizeRetainedStack" -or
        [string]$decision.baselineCommit -cne [string]$Lock.baselineCommit -or
        [string]$decision.retainedHead -cne $RetainedHead -or
        [string]$decision.observedHeadAtDecision -cne $RetainedHead -or
        -not [bool]$decision.headPreserved -or
        [string]$decision.decision -cne "keep") {
        throw "Final decision envelope is incomplete or inconsistent."
    }
    $expectedHypotheses = @($RetainedChain.Hypotheses)
    $expectedWinWorkloads = @(Get-HypothesisWorkloads `
        $Manifest $expectedHypotheses)
    if (($decision.targetHypotheses | ConvertTo-Json -Depth 8 -Compress) -cne
            ($expectedHypotheses | ConvertTo-Json -Depth 8 -Compress) -or
        (($decision.winWorkloads | ConvertTo-Json -Depth 4 -Compress) -cne
         (@($expectedWinWorkloads | ForEach-Object {
            [string]$_.id
         }) | ConvertTo-Json -Depth 4 -Compress)) -or
        (($WinWorkloads | ConvertTo-Json -Depth 16 -Compress) -cne
         ($expectedWinWorkloads | ConvertTo-Json -Depth 16 -Compress))) {
        throw "Final decision target hypotheses or deduplicated workloads differ."
    }
    $configurations = @(Get-CampaignConfigurations $Manifest)
    Assert-ExactPropertyNames $decision.candidateBuilds $configurations `
        "Final candidate builds"
    $results = @($decision.configurations)
    if ($results.Count -ne $configurations.Count) {
        throw "Final decision configuration topology differs."
    }
    for ($index = 0; $index -lt $configurations.Count; $index++) {
        $configuration = [string]$configurations[$index]
        $candidateBuild = $CandidateBuilds[$configuration]
        if ($null -eq $candidateBuild) {
            throw "Final decision lacks the '$configuration' candidate build."
        }
        $storedBuildProperty =
            $decision.candidateBuilds.PSObject.Properties[$configuration]
        if ($null -eq $storedBuildProperty) {
            throw "Final decision lacks stored '$configuration' build identity."
        }
        $storedBuild = $storedBuildProperty.Value
        if ($null -eq $storedBuild -or
            (($storedBuild | ConvertTo-Json -Depth 12 -Compress) -cne
             ($candidateBuild | ConvertTo-Json -Depth 12 -Compress))) {
            throw "Final decision '$configuration' build identity differs."
        }
        $baselineBuild = Get-ReferenceBuildIdentity $Lock $configuration
        Assert-ConfigurationTimingEvidence `
            $Manifest $Lock $WinWorkloads $baselineBuild $candidateBuild `
            $configuration $Iteration "retained-stack-final" `
            ([string]$Lock.baselineCommit) $RetainedHead $results[$index] `
            $FinalRoot $true "Post-publication final timing audit"
        Assert-ConfigurationQualityEvidence `
            $Manifest $Lock $WinWorkloads $candidateBuild `
            $configuration $Iteration "retained-stack-final" `
            $RetainedHead $results[$index] $FinalRoot $true `
            "Post-publication final quality audit"
    }
    Assert-ExactCampaignHead $RetainedHead "Post-publication final audit"
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
    Assert-AcceptanceRefSnapshot `
        $Manifest $AcceptanceRefSnapshot "Post-publication final audit"
    $chain = Assert-RetainedAcceptanceChain $Manifest $Lock $RetainedHead
    if ([string]$chain.LastEvidence -cne [string]$RetainedChain.LastEvidence) {
        throw "Post-publication final audit changed the accepted-chain tip."
    }
    if (-not (Test-Path -LiteralPath $DecisionPath -PathType Leaf) -or
        (Get-Sha256 $DecisionPath) -cne $decisionSha256) {
        throw "Final decision bytes changed during post-publication validation."
    }
    return $decisionSha256
}

function Write-JsonArtifact {
    param([string]$Path, $Value)
    if (Test-Path -LiteralPath $Path) {
        throw "Artifact already exists and will not be overwritten: $Path"
    }
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f
            [System.IO.Path]::GetFileName($Path),
            [Guid]::NewGuid().ToString("N"))
    try {
        $Value | ConvertTo-Json -Depth 24 |
            Set-Content -LiteralPath $temporaryPath -Encoding utf8
        $null = Get-Content -LiteralPath $temporaryPath -Raw |
            ConvertFrom-Json -DateKind String
        [System.IO.File]::Move($temporaryPath, $Path, $false)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Assert-DecisionCandidateIdentity {
    param($Manifest, $Lock, $Decision)
    Assert-ExactPropertyNames $Decision.candidate @(
        "kind", "id", "sourceCommit", "patchId", "allowedPaths",
        "focusedTestFilter", "envelopePath", "envelopeSha256",
        "discoveryArtifactPath", "discoveryArtifactSha256") `
        "Accepted candidate identity"
    if ([string]$Decision.candidate.kind -ceq "reviewed") {
        $candidate = Get-ReviewedCandidate `
            $Manifest ([string]$Decision.candidate.id)
        $expected = New-ReviewedCandidateDecisionIdentity $candidate
        if (($Decision.candidate | ConvertTo-Json -Depth 8 -Compress) -cne
            ($expected | ConvertTo-Json -Depth 8 -Compress)) {
            throw "Accepted candidate identity differs from the pinned manifest."
        }
        return [pscustomobject]@{
            Candidate = $candidate
            Hypothesis = Get-TargetHypothesis `
                $Manifest ([string]$candidate.hypothesisId)
        }
    }
    if ([string]$Decision.candidate.kind -ceq "discovered") {
        $admission = Read-CandidateEnvelope `
            $Manifest $Lock ([string]$Decision.candidate.envelopePath) `
            ([string]$Decision.acceptedHead) $false `
            ([string]$Decision.candidate.envelopeSha256)
        if (($Decision.candidate | ConvertTo-Json -Depth 8 -Compress) -cne
            ($admission.DecisionIdentity | ConvertTo-Json -Depth 8 -Compress)) {
            throw "Accepted automatic candidate differs from its frozen envelope."
        }
        return $admission
    }
    throw "Accepted candidate kind is invalid."
}

function Assert-AcceptedDecisionEnvelope {
    param(
        $Manifest,
        $Lock,
        $Decision,
        [string]$ExpectedAcceptedHead,
        [string]$ExpectedCandidateHead,
        [string]$ExpectedPreviousEvidence)
    Assert-ExactPropertyNames $Decision @(
        "schema", "campaignId", "manifestSha256",
        "manifestSnapshotPath", "manifestSnapshotSha256", "lockSha256",
        "iteration", "acceptedHead", "candidateHead",
        "observedHeadAtDecision", "previousAcceptanceEvidence",
        "decisionArtifactPath", "decision", "reason", "candidate", "attempt",
        "targetHypothesisId", "targetDomain", "targetPass", "targetClaims",
        "baselineBuilds", "candidateBuilds", "configurations") `
        "Accepted decision"
    if ([string]$Decision.schema -ne "njulf-perf-campaign-decision/v3" -or
        [string]$Decision.campaignId -ne [string]$Manifest.campaignId -or
        [string]$Decision.manifestSha256 -ne
            (Get-AdmittedCampaignManifestSha256) -or
        -not [string]::Equals(
            [string]$Decision.manifestSnapshotPath,
            $script:CampaignManifestSnapshotPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Decision.manifestSnapshotSha256 -cne
            $script:CampaignManifestSha256 -or
        [string]$Decision.lockSha256 -ne $script:CampaignLockSha256 -or
        [string]$Decision.decision -ne "keep" -or
        [int]$Decision.iteration -lt 1 -or
        -not [string]::Equals(
            [string]$Decision.acceptedHead,
            $ExpectedAcceptedHead,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$Decision.candidateHead,
            $ExpectedCandidateHead,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$Decision.observedHeadAtDecision,
            $ExpectedCandidateHead,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Decision.previousAcceptanceEvidence -ne
            $ExpectedPreviousEvidence) {
        throw "Accepted decision envelope is incomplete or inconsistent."
    }
    $admission = Assert-DecisionCandidateIdentity $Manifest $Lock $Decision
    Assert-TimingAttemptEvidence $Manifest $Decision.attempt $Decision
    $hypothesis = $admission.Hypothesis
    if ([string]$Decision.targetHypothesisId -cne [string]$hypothesis.id -or
        [string]$Decision.targetDomain -cne [string]$hypothesis.targetDomain -or
        [string]$Decision.targetPass -cne [string]$hypothesis.targetPass -or
        (($Decision.targetClaims | ConvertTo-Json -Depth 8 -Compress) -cne
         ($hypothesis.claims | ConvertTo-Json -Depth 8 -Compress))) {
        throw "Accepted decision target hypothesis differs from the manifest."
    }
    $parentFields = @((Get-GitText @(
        "rev-list", "--parents", "-n", "1", $ExpectedCandidateHead)) -split '\s+')
    if ($parentFields.Count -ne 2 -or
        -not [string]::Equals(
            [string]$parentFields[1],
            $ExpectedAcceptedHead,
            [StringComparison]::OrdinalIgnoreCase) -or
        (Get-StablePatchId $ExpectedCandidateHead) -cne
            [string]$Decision.candidate.patchId -or
        (@(Get-CommitChangedPaths $ExpectedCandidateHead) -join "`n") -cne
            (@($Decision.candidate.allowedPaths | ForEach-Object {
                [string]$_
            }) -join "`n")) {
        throw "Accepted candidate is not one non-merge commit on its admitted parent."
    }
}

function Write-ValidatedAcceptanceDecisionArtifact {
    param(
        $Manifest,
        $Lock,
        [string]$Path,
        $Summary,
        [string]$ExpectedAcceptedHead,
        [string]$ExpectedCandidateHead,
        [string]$ExpectedPreviousEvidence)
    if (Test-Path -LiteralPath $Path) {
        throw "Artifact already exists and will not be overwritten: $Path"
    }
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory (
        ".decision-{0}.tmp" -f [Guid]::NewGuid().ToString("N"))
    try {
        $json = ($Summary | ConvertTo-Json -Depth 12) +
            [Environment]::NewLine
        [System.IO.File]::WriteAllBytes(
            $temporaryPath,
            [System.Text.UTF8Encoding]::new($false).GetBytes($json))
        $blob = Get-GitText @(
            "hash-object", "--no-filters", "-w", "--", $temporaryPath)
        if ($blob -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
            (Get-GitText @("cat-file", "-t", $blob)) -ne "blob") {
            throw "Acceptance artifact did not produce a Git blob."
        }
        $validated = (Get-GitText @("cat-file", "blob", $blob)) |
            ConvertFrom-Json -DateKind String
        Assert-AcceptedDecisionEnvelope `
            $Manifest $Lock $validated `
            $ExpectedAcceptedHead $ExpectedCandidateHead `
            $ExpectedPreviousEvidence
        Assert-AcceptedDecisionArtifacts `
            $Manifest $Lock $validated $temporaryPath
        if (-not [string]::Equals(
                (Get-GitText @(
                    "hash-object", "--no-filters", "--", $temporaryPath)),
                $blob,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Acceptance temp file changed after its blob was semantically validated."
        }
        [System.IO.File]::Move($temporaryPath, $Path, $false)
        if (-not [string]::Equals(
                (Get-GitText @("hash-object", "--no-filters", "--", $Path)),
                $blob,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Atomically published decision differs from its validated blob."
        }
        return $blob
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-AcceptanceRefPrefix {
    param($Manifest)
    $campaignId = [string]$Manifest.campaignId
    if ($campaignId -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
        throw "Campaign id '$campaignId' cannot be used in an acceptance ref."
    }
    return "refs/perf-campaign/accepted/$campaignId/"
}

function Get-AcceptanceRefName {
    param($Manifest, [string]$Commit)
    if ($Commit -notmatch '^[0-9a-f]{40}$') {
        throw "Acceptance evidence requires a canonical commit hash, got '$Commit'."
    }
    return (Get-AcceptanceRefPrefix $Manifest) + $Commit.ToLowerInvariant()
}

function Get-AcceptanceRefRawEntries {
    param($Manifest)
    $prefix = Get-AcceptanceRefPrefix $Manifest
    $entries = [ordered]@{}
    foreach ($line in @(Invoke-Git @(
                "for-each-ref",
                "--format=%(refname)%09%(objectname)%09%(symref)",
                $prefix))) {
        $text = ([string]$line).TrimEnd("`r")
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $fields = @($text.Split([char]9))
        if ($fields.Count -ne 3) {
            throw "Malformed campaign acceptance ref entry '$text'."
        }
        $objectName = [string]$fields[1]
        $symref = [string]$fields[2]
        if (-not $fields[0].StartsWith($prefix, [StringComparison]::Ordinal) -or
            ([string]::IsNullOrWhiteSpace($symref) -and
             $objectName -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') -or
            (-not [string]::IsNullOrWhiteSpace($symref) -and
             -not [string]::IsNullOrWhiteSpace($objectName) -and
             $objectName -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$')) {
            throw "Malformed campaign acceptance ref entry '$text'."
        }
        $name = [string]$fields[0]
        $entries[$name] = [pscustomobject]@{
            Name = [string]$fields[0]
            ObjectName = $objectName
            Symref = $symref
            CanonicalName =
                ([string]$fields[0]).Substring($prefix.Length) -match
                    '^[0-9a-f]{40}$'
        }
    }

    $refFormat = Get-GitText @("rev-parse", "--show-ref-format")
    if ($refFormat -ne "files") {
        throw "Campaign acceptance refs require the auditable Git files ref backend; found '$refFormat'."
    }
    $commonDirectoryText = Get-GitText @("rev-parse", "--git-common-dir")
    $commonDirectory = if ([System.IO.Path]::IsPathRooted(
            $commonDirectoryText)) {
        [System.IO.Path]::GetFullPath($commonDirectoryText)
    } else {
        [System.IO.Path]::GetFullPath(
            (Join-Path $script:SolutionRoot $commonDirectoryText))
    }
    $looseRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $commonDirectory $prefix))
    $looseRelative = [System.IO.Path]::GetRelativePath(
        $commonDirectory,
        $looseRoot)
    if ($looseRelative -eq ".." -or
        $looseRelative.StartsWith(
            "..$([System.IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::Ordinal)) {
        throw "Acceptance loose-ref root escaped Git common directory."
    }
    if (Test-Path -LiteralPath $looseRoot -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $looseRoot -Recurse -File)) {
            if ($file.Name.EndsWith(".lock", [StringComparison]::Ordinal)) {
                throw "Stale/concurrent acceptance ref lock is present: $($file.FullName)"
            }
            $relative = [System.IO.Path]::GetRelativePath(
                $looseRoot,
                $file.FullName).Replace("\", "/")
            $name = $prefix + $relative
            $content = (Get-Content -LiteralPath $file.FullName -Raw).Trim()
            $symref = ""
            $objectName = $content
            if ($content.StartsWith("ref: ", [StringComparison]::Ordinal)) {
                $symref = $content.Substring(5).Trim()
                $objectName = if ($entries.Contains($name)) {
                    [string]$entries[$name].ObjectName
                } else {
                    ""
                }
            } elseif ($content -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
                throw "Malformed loose campaign acceptance ref '$name'."
            }
            $entries[$name] = [pscustomobject]@{
                Name = $name
                ObjectName = $objectName
                Symref = $symref
                CanonicalName =
                    $relative -match '^[0-9a-f]{40}$'
            }
        }
    }
    return @($entries.Values | Sort-Object Name)
}

function Get-AcceptanceRefSnapshot {
    param($Manifest)
    $snapshot = [ordered]@{}
    foreach ($entry in @(Get-AcceptanceRefRawEntries $Manifest)) {
        if (-not [bool]$entry.CanonicalName) {
            throw "Campaign acceptance ref '$($entry.Name)' has a non-commit suffix."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$entry.Symref)) {
            throw "Campaign acceptance ref '$($entry.Name)' is a forbidden symref to '$($entry.Symref)'."
        }
        $snapshot[[string]$entry.Name] = [string]$entry.ObjectName
    }
    return $snapshot
}

function Assert-AcceptanceRefSnapshot {
    param($Manifest, $Expected, [string]$Label)
    $actual = Get-AcceptanceRefSnapshot $Manifest
    if ($actual.Count -ne $Expected.Count) {
        throw "$Label changed the campaign acceptance-ref namespace."
    }
    foreach ($entry in $Expected.GetEnumerator()) {
        if (-not $actual.Contains([string]$entry.Key) -or
            -not [string]::Equals(
                [string]$actual[[string]$entry.Key],
                [string]$entry.Value,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label changed campaign acceptance ref '$($entry.Key)'."
        }
    }
}

function Restore-AcceptanceRefSnapshot {
    param($Manifest, $Expected)
    Assert-CampaignWorktreeRoot
    $prefix = Get-AcceptanceRefPrefix $Manifest
    $actualEntries = @(Get-AcceptanceRefRawEntries $Manifest)
    $actualNames = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $commands = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $actualEntries) {
        $name = [string]$entry.Name
        [void]$actualNames.Add($name)
        if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal)) {
            throw "Refusing to restore acceptance ref outside '$prefix': $name"
        }
        if (-not $Expected.Contains($name)) {
            if ([string]::IsNullOrWhiteSpace([string]$entry.Symref)) {
                $commands.Add("delete $name $($entry.ObjectName)")
            } else {
                $commands.Add("delete $name")
            }
        } elseif (-not [string]::IsNullOrWhiteSpace([string]$entry.Symref)) {
            $commands.Add("update $name $($Expected[$name])")
        } elseif ([string]::Equals(
                [string]$entry.ObjectName,
                [string]$Expected[$name],
                [StringComparison]::OrdinalIgnoreCase)) {
            $commands.Add("verify $name $($entry.ObjectName)")
        } else {
            $commands.Add(
                "update $name $($Expected[$name]) $($entry.ObjectName)")
        }
    }
    foreach ($entry in $Expected.GetEnumerator()) {
        $name = [string]$entry.Key
        if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal)) {
            throw "Refusing to restore acceptance ref outside '$prefix': $name"
        }
        if (-not $actualNames.Contains($name)) {
            $commands.Add("create $name $($entry.Value)")
        }
    }
    Invoke-GitUpdateRefTransaction `
        @($commands) "Acceptance-ref rollback transaction"
    Assert-AcceptanceRefSnapshot $Manifest $Expected "Acceptance-ref rollback"
}

function Publish-AcceptanceEvidence {
    param(
        $Manifest,
        [string]$DecisionPath,
        [string]$CandidateCommit,
        [string]$DecisionBlob,
        $ExpectedSnapshot)
    if (-not (Test-Path -LiteralPath $DecisionPath -PathType Leaf)) {
        throw "Acceptance decision is missing: $DecisionPath"
    }
    $refName = Get-AcceptanceRefName $Manifest $CandidateCommit
    Assert-AcceptanceRefSnapshot `
        $Manifest $ExpectedSnapshot "Acceptance publication"
    if ($ExpectedSnapshot.Contains($refName)) {
        throw "Acceptance evidence already exists for $CandidateCommit."
    }
    if ($DecisionBlob -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
        (Get-GitText @("cat-file", "-t", $DecisionBlob)) -ne "blob" -or
        -not [string]::Equals(
            (Get-GitText @(
                "hash-object", "--no-filters", "--", $DecisionPath)),
            $DecisionBlob,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Acceptance decision no longer matches its validated Git blob."
    }
    $commands = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $ExpectedSnapshot.GetEnumerator()) {
        $commands.Add("verify $($entry.Key) $($entry.Value)")
    }
    $commands.Add("create $refName $DecisionBlob")
    Invoke-GitUpdateRefTransaction `
        @($commands) "Acceptance publication transaction"
    $published = Get-GitText @("rev-parse", "--verify", $refName)
    if (-not [string]::Equals(
            $published,
            $DecisionBlob,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Acceptance ref '$refName' did not resolve to its decision blob."
    }
    $expectedAfterPublish = [ordered]@{}
    foreach ($entry in $ExpectedSnapshot.GetEnumerator()) {
        $expectedAfterPublish[[string]$entry.Key] = [string]$entry.Value
    }
    $expectedAfterPublish[$refName] = $DecisionBlob
    Assert-AcceptanceRefSnapshot `
        $Manifest $expectedAfterPublish "Acceptance publication"
    return $DecisionBlob
}

function Assert-PathIdentity {
    param([string]$Actual, [string]$Expected, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($Actual) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath($Actual),
            [System.IO.Path]::GetFullPath($Expected),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label path '$Actual' differs from '$Expected'."
    }
}

function Assert-AcceptedDecisionArtifacts {
    param(
        $Manifest,
        $Lock,
        $Decision,
        [string]$DecisionPath)
    $iteration = [int]$Decision.iteration
    $iterationRoot = Join-Path $script:RunRoot (
        "iterations/{0:D6}" -f $iteration)
    Assert-PathIdentity `
        ([string]$Decision.decisionArtifactPath) `
        (Join-Path $iterationRoot "decision.json") `
        "Accepted decision"
    if (-not (Test-Path -LiteralPath $DecisionPath -PathType Leaf)) {
        throw "Accepted decision bytes are missing: $DecisionPath"
    }

    $configurationNames = @(Get-CampaignConfigurations $Manifest)
    $configurations = @($Decision.configurations)
    if ($configurations.Count -ne $configurationNames.Count) {
        throw "Accepted decision $iteration configuration topology differs."
    }
    Assert-ExactPropertyNames $Decision.baselineBuilds $configurationNames `
        "Accepted baseline builds"
    Assert-ExactPropertyNames $Decision.candidateBuilds $configurationNames `
        "Accepted candidate builds"
    $hypothesis = [pscustomobject][ordered]@{
        id = [string]$Decision.targetHypothesisId
        targetDomain = [string]$Decision.targetDomain
        targetPass = [string]$Decision.targetPass
        claims = @($Decision.targetClaims)
    }
    $winWorkloads = @(Get-HypothesisWorkloads $Manifest @($hypothesis))
    for ($index = 0; $index -lt $configurationNames.Count; $index++) {
        $configuration = [string]$configurationNames[$index]
        $result = $configurations[$index]
        if ([string]$result.configuration -cne $configuration -or
            [string]$result.stage -cne "hypothesis-screen" -or
            [string]$result.decision -cne "keep" -or
            -not [bool]$result.qualitySequenceCompleted) {
            throw "Accepted decision $iteration '$configuration' screen is incomplete."
        }
        $baselineBuild = $Decision.baselineBuilds.PSObject.Properties[
            $configuration].Value
        $candidateBuild = $Decision.candidateBuilds.PSObject.Properties[
            $configuration].Value
        Assert-PathIdentity `
            ([string]$baselineBuild.RootPath) `
            (Join-Path $iterationRoot "build-baseline/$configuration") `
            "Accepted $configuration baseline build"
        Assert-PathIdentity `
            ([string]$candidateBuild.RootPath) `
            (Join-Path $iterationRoot "build-candidate/$configuration") `
            "Accepted $configuration candidate build"
        Assert-BuildIdentity $baselineBuild `
            "Accepted iteration $iteration $configuration baseline"
        Assert-BuildIdentity $candidateBuild `
            "Accepted iteration $iteration $configuration candidate"
        Assert-ConfigurationTimingEvidence `
            $Manifest $Lock $winWorkloads $baselineBuild $candidateBuild `
            $configuration $iteration "hypothesis-screen" `
            ([string]$Decision.acceptedHead) ([string]$Decision.candidateHead) `
            $result "" $true `
            "Accepted decision $iteration $configuration timing audit"
        Assert-ConfigurationQualityEvidence `
            $Manifest $Lock $winWorkloads $candidateBuild `
            $configuration $iteration "hypothesis-screen" `
            ([string]$Decision.candidateHead) $result "" $true `
            "Accepted decision $iteration $configuration quality audit"
    }
}

function Get-TimingAttemptRefName {
    param($Manifest, [string]$Domain, [string]$Name)
    if ($Domain -notin @("gpu", "cpu") -or
        [string]::IsNullOrWhiteSpace($Name)) {
        throw "Timing attempt identity is invalid."
    }
    $identityHash = Get-Sha256Text ("$Domain`0$Name")
    return "refs/perf-campaign/attempted/$([string]$Manifest.campaignId)/$identityHash"
}

function Test-TimingAttemptReserved {
    param($Manifest, [string]$Domain, [string]$Name)
    $refName = Get-TimingAttemptRefName $Manifest $Domain $Name
    $null = & git -C $script:SolutionRoot show-ref --verify --quiet $refName
    return $LASTEXITCODE -eq 0
}

function Reserve-TimingAttempt {
    param(
        $Manifest,
        [string]$Domain,
        [string]$Name,
        [string]$CandidateId,
        [string]$AcceptedHead)
    $refName = Get-TimingAttemptRefName $Manifest $Domain $Name
    if (Test-TimingAttemptReserved $Manifest $Domain $Name) {
        throw "Timing identity '$Domain::$Name' already consumed its one bounded attempt."
    }
    $reservation = [pscustomobject][ordered]@{
        schema = "njulf-perf-timing-attempt/v1"
        campaignId = [string]$Manifest.campaignId
        manifestSha256 = Get-AdmittedCampaignManifestSha256
        lockSha256 = $script:CampaignLockSha256
        timingDomain = $Domain
        timingName = $Name
        candidateId = $CandidateId
        acceptedHead = $AcceptedHead
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $temporary = Join-Path ([System.IO.Path]::GetTempPath()) (
        "njulf-attempt-{0}.json" -f [Guid]::NewGuid().ToString("N"))
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
            (($reservation | ConvertTo-Json -Depth 6 -Compress) + "`n"))
        [System.IO.File]::WriteAllBytes($temporary, $bytes)
        $blob = Get-GitText @(
            "hash-object", "--no-filters", "-w", "--", $temporary)
        Invoke-GitUpdateRefTransaction @("create $refName $blob") `
            "Timing attempt reservation"
        if ((Get-GitText @("rev-parse", "--verify", $refName)) -cne $blob) {
            throw "Timing attempt reservation was not published atomically."
        }
        return [pscustomobject][ordered]@{
            refName = $refName
            blob = $blob
            reservation = $reservation
        }
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Assert-TimingAttemptEvidence {
    param($Manifest, $Evidence, $Decision)
    Assert-ExactPropertyNames $Evidence @("refName", "blob", "reservation") `
        "Timing attempt evidence"
    $expectedRef = Get-TimingAttemptRefName `
        $Manifest ([string]$Decision.targetDomain) ([string]$Decision.targetPass)
    if ([string]$Evidence.refName -cne $expectedRef -or
        [string]$Evidence.blob -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
        (Get-GitText @("rev-parse", "--verify", $expectedRef)) -cne
            [string]$Evidence.blob -or
        (Get-GitText @("cat-file", "-t", [string]$Evidence.blob)) -cne "blob") {
        throw "Timing attempt ref/blob identity differs."
    }
    $stored = (Get-GitText @("cat-file", "blob", [string]$Evidence.blob)) |
        ConvertFrom-Json -DateKind String
    if (($stored | ConvertTo-Json -Depth 8 -Compress) -cne
        ($Evidence.reservation | ConvertTo-Json -Depth 8 -Compress)) {
        throw "Timing attempt reservation bytes differ from the stored blob."
    }
    Assert-ExactPropertyNames $stored @(
        "schema", "campaignId", "manifestSha256", "lockSha256",
        "timingDomain", "timingName", "candidateId", "acceptedHead",
        "createdAtUtc") "Timing attempt reservation"
    $createdAt = [DateTimeOffset]::MinValue
    if ([string]$stored.schema -cne "njulf-perf-timing-attempt/v1" -or
        [string]$stored.campaignId -cne [string]$Manifest.campaignId -or
        [string]$stored.manifestSha256 -cne
            (Get-AdmittedCampaignManifestSha256) -or
        [string]$stored.lockSha256 -cne $script:CampaignLockSha256 -or
        [string]$stored.timingDomain -cne [string]$Decision.targetDomain -or
        [string]$stored.timingName -cne [string]$Decision.targetPass -or
        [string]$stored.candidateId -cne [string]$Decision.candidate.id -or
        [string]$stored.acceptedHead -cne [string]$Decision.acceptedHead -or
        -not [DateTimeOffset]::TryParseExact(
            [string]$stored.createdAtUtc, "O",
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$createdAt)) {
        throw "Timing attempt reservation semantics differ."
    }
}

function Assert-RetainedAcceptanceChain {
    param($Manifest, $Lock, [string]$RetainedHead)
    $baseline = [string]$Lock.baselineCommit
    if ([string]::Equals(
            $baseline,
            $RetainedHead,
            [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{
            Hypotheses = @()
            LastEvidence = "baseline:$baseline"
            Commits = @()
        }
    }
    Assert-LockBaselineAncestor $Lock $RetainedHead
    $commits = @((Get-GitText @(
        "rev-list", "--reverse", "--first-parent", "$baseline..$RetainedHead")) -split "`n" |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($commits.Count -eq 0) {
        throw "Retained HEAD '$RetainedHead' has no first-parent commits beyond '$baseline'."
    }
    $hypotheses = [System.Collections.Generic.List[object]]::new()
    $previousCommit = $baseline
    $previousEvidence = "baseline:$baseline"
    foreach ($commit in $commits) {
        $commit = ([string]$commit).Trim()
        $parentFields = @((Get-GitText @(
            "rev-list", "--parents", "-n", "1", $commit)) -split '\s+')
        if ($parentFields.Count -ne 2 -or
            -not [string]::Equals(
                [string]$parentFields[1],
                $previousCommit,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Retained commit '$commit' is a merge or breaks the accepted first-parent chain."
        }
        $refName = Get-AcceptanceRefName $Manifest $commit
        $blob = Get-GitText @("rev-parse", "--verify", $refName)
        if ((Get-GitText @("cat-file", "-t", $blob)) -ne "blob") {
            throw "Acceptance ref '$refName' does not point to a decision blob."
        }
        $decision = (Get-GitText @("cat-file", "blob", $blob)) |
            ConvertFrom-Json -DateKind String
        $iteration = [int]$decision.iteration
        $decisionPath = Join-Path $script:RunRoot (
            "iterations/{0:D6}/decision.json" -f $iteration)
        if (-not (Test-Path -LiteralPath $decisionPath -PathType Leaf) -or
            -not [string]::Equals(
                (Get-GitText @(
                    "hash-object", "--no-filters", "--", $decisionPath)),
                $blob,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$decision.schema -ne "njulf-perf-campaign-decision/v3" -or
            [string]$decision.campaignId -ne [string]$Manifest.campaignId -or
            [string]$decision.manifestSha256 -ne
                (Get-AdmittedCampaignManifestSha256) -or
            -not [string]::Equals(
                [string]$decision.manifestSnapshotPath,
                $script:CampaignManifestSnapshotPath,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$decision.manifestSnapshotSha256 -cne
                $script:CampaignManifestSha256 -or
            [string]$decision.lockSha256 -ne $script:CampaignLockSha256 -or
            [string]$decision.decision -ne "keep" -or
            -not [string]::Equals(
                [string]$decision.acceptedHead,
                $previousCommit,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [string]$decision.candidateHead,
                $commit,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [string]$decision.observedHeadAtDecision,
                $commit,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$decision.previousAcceptanceEvidence -ne $previousEvidence) {
            throw "Acceptance evidence for retained commit '$commit' is incomplete or inconsistent."
        }
        Assert-AcceptedDecisionEnvelope `
            $Manifest $Lock $decision $previousCommit $commit $previousEvidence
        Assert-AcceptedDecisionArtifacts `
            $Manifest $Lock $decision $decisionPath
        $hypothesis = [pscustomobject][ordered]@{
            id = [string]$decision.targetHypothesisId
            targetDomain = [string]$decision.targetDomain
            targetPass = [string]$decision.targetPass
            claims = @($decision.targetClaims)
        }
        if (@($hypotheses | Where-Object {
                [string]$_.id -ceq [string]$hypothesis.id
            }).Count -eq 0) {
            $hypotheses.Add($hypothesis)
        }
        $previousCommit = $commit
        $previousEvidence = $blob
    }
    if (-not [string]::Equals(
            $previousCommit,
            $RetainedHead,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Authenticated acceptance chain does not terminate at '$RetainedHead'."
    }
    return [pscustomobject]@{
        Hypotheses = @($hypotheses)
        LastEvidence = $previousEvidence
        Commits = @($commits)
    }
}

function Get-NextCampaignIterationId {
    $iterationsRoot = Join-Path $script:RunRoot "iterations"
    if (-not (Test-Path -LiteralPath $iterationsRoot -PathType Container)) {
        return 1
    }
    $maximum = 0
    foreach ($directory in @(Get-ChildItem -LiteralPath $iterationsRoot -Directory)) {
        $value = 0
        if ([int]::TryParse($directory.Name, [ref]$value)) {
            $maximum = [Math]::Max($maximum, $value)
        }
    }
    if ($maximum -eq [int]::MaxValue) {
        throw "Campaign iteration identifier space is exhausted."
    }
    return $maximum + 1
}

function Initialize-QualitySequenceReference {
    param(
        $Manifest,
        $Workload,
        $ReferenceBuild,
        [string]$Configuration,
        [string]$BaselineCommit,
        [string]$QualityContractPath,
        [string]$QualityContractSha256)
    $root = Join-Path $script:RunRoot (
        "references/{0}/{1}/quality-sequence" -f
            $Configuration, [string]$Workload.id)
    if (Test-Path -LiteralPath $root) {
        throw "Quality-sequence reference root already exists: $root"
    }
    $canonicalRoot = Join-Path $root "canonical"
    $canonicalId = Get-QualitySequenceId `
        $Manifest $Configuration ([string]$Workload.id) `
        "canonical" "reference-init" $BaselineCommit 0
    if (-not (Test-Path -LiteralPath $QualityContractPath -PathType Leaf) -or
        (Get-Sha256 $QualityContractPath) -cne $QualityContractSha256) {
        throw "Canonical quality ROI source changed before capture."
    }
    $canonicalEvidence = Invoke-QualitySequenceCapture `
        -Manifest $Manifest `
        -Workload $Workload `
        -BuildIdentity $ReferenceBuild `
        -Configuration $Configuration `
        -Role "canonical" `
        -SequenceId $canonicalId `
        -ReportPath (Join-Path $canonicalRoot "report.json") `
        -OutputDirectory (Join-Path $canonicalRoot "checkpoints") `
        -ReferenceContractPath "" `
        -ExpectedReferenceContractSha256 "" `
        -QualityContractPath "" `
        -ExpectedQualityContractSha256 "" `
        -ReferenceContract $null `
        -VerifierBuildIdentity $ReferenceBuild `
        -SpatialEnvelope $null `
        -ExpectedCommit $BaselineCommit `
        -Label "Initialize quality canonical $Configuration/$($Workload.id)"
    if ((Get-Sha256 $QualityContractPath) -cne $QualityContractSha256) {
        throw "Canonical quality ROI source changed during capture."
    }
    $canonicalReport = Read-QualitySequenceReport `
        ([string]$canonicalEvidence.reportPath)
    $repeatContractValue = New-QualitySequenceReferenceContract `
        $Manifest $Workload $canonicalReport `
        $QualityContractPath $QualityContractSha256 @() @()
    $repeatContract = Write-QualitySequenceReferenceContract `
        (Join-Path $root "repeat-reference.json") $repeatContractValue
    $repeatEvidence = @()
    $repeatReports = @()
    for ($repeat = 1;
         $repeat -le [int]$Manifest.qualitySequence.baselineRepeatCount;
         $repeat++) {
        $repeatRoot = Join-Path $root ("repeat-{0:D2}" -f $repeat)
        $repeatId = Get-QualitySequenceId `
            $Manifest $Configuration ([string]$Workload.id) `
            "repeat" "reference-init" $BaselineCommit $repeat
        $evidence = Invoke-QualitySequenceCapture `
            -Manifest $Manifest `
            -Workload $Workload `
            -BuildIdentity $ReferenceBuild `
            -Configuration $Configuration `
            -Role "repeat" `
            -SequenceId $repeatId `
            -ReportPath (Join-Path $repeatRoot "report.json") `
            -OutputDirectory (Join-Path $repeatRoot "checkpoints") `
            -ReferenceContractPath ([string]$repeatContract.path) `
            -ExpectedReferenceContractSha256 ([string]$repeatContract.sha256) `
            -QualityContractPath $QualityContractPath `
            -ExpectedQualityContractSha256 $QualityContractSha256 `
            -ReferenceContract $repeatContractValue `
            -VerifierBuildIdentity $ReferenceBuild `
            -SpatialEnvelope $null `
            -ExpectedCommit $BaselineCommit `
            -Label "Initialize quality repeat $repeat $Configuration/$($Workload.id)"
        $repeatEvidence += $evidence
        $repeatReports += Read-QualitySequenceReport ([string]$evidence.reportPath)
    }
    $repeatHashes = @($repeatEvidence | ForEach-Object {
        [string]$_.reportSha256
    })
    if (@($repeatHashes | Select-Object -Unique).Count -ne 2) {
        throw "Quality baseline repeats must be two distinct immutable reports."
    }
    $verifiedRepeatMetrics = @($repeatEvidence | ForEach-Object {
        $_.verifiedMetrics
    })
    $gates = @(New-QualitySequenceTemporalGates `
        $Manifest $Workload $verifiedRepeatMetrics)
    $spatialEnvelope = @(New-QualitySequenceSpatialEnvelope `
        $Manifest $Workload $verifiedRepeatMetrics)
    $candidateContractValue = New-QualitySequenceReferenceContract `
        $Manifest $Workload $canonicalReport `
        $QualityContractPath $QualityContractSha256 $gates $repeatHashes
    $candidateContract = Write-QualitySequenceReferenceContract `
        (Join-Path $root "candidate-reference.json") `
        $candidateContractValue
    return [pscustomobject]@{
        schema = "njulf-perf-campaign-quality-reference/v1"
        canonical = $canonicalEvidence
        repeatReferenceContractPath = [string]$repeatContract.path
        repeatReferenceContractSha256 = [string]$repeatContract.sha256
        repeats = @($repeatEvidence)
        candidateReferenceContractPath = [string]$candidateContract.path
        candidateReferenceContractSha256 = [string]$candidateContract.sha256
        temporalGates = @($gates)
        spatialEnvelope = @($spatialEnvelope)
        baselineRepeatReportSha256 = @($repeatHashes)
    }
}

function Get-QualitySequenceEvidencePropertyNames {
    return @(
        "role", "sequenceId", "reportPath", "reportSha256",
        "healthPath", "healthSha256", "outputDirectory",
        "referenceContractPath", "referenceContractSha256",
        "qualityContractPath", "qualityContractSha256", "buildRootPath",
        "buildBundleFingerprint", "runtimeExecutableBundleHash",
        "buildIdentity", "frozenVerifierEvidence",
        "trajectoryRouteHash", "trajectorySequenceHash", "producerGpuName",
        "producerDriverVersion", "producerQualityTier", "captureRun",
        "producerIdentity", "verifiedMetrics", "checkpoints")
}

function Assert-QualitySequenceStoredEvidence {
    param(
        $Manifest,
        $Workload,
        $Evidence,
        $BuildIdentity,
        [string]$Configuration,
        [string]$Role,
        [string]$SequenceId,
        [string]$ExpectedCommit,
        [string]$ReferenceContractPath,
        [string]$ExpectedReferenceContractSha256,
        [string]$QualityContractPath,
        [string]$ExpectedQualityContractSha256,
        $ReferenceContract,
        $VerifierBuildIdentity,
        $SpatialEnvelope,
        [string]$ExpectedRoot,
        [string]$Label)
    Assert-ExactPropertyNames $Evidence `
        (Get-QualitySequenceEvidencePropertyNames) "$Label evidence"
    if ($null -eq $Evidence -or
        [string]$Evidence.role -cne $Role -or
        [string]$Evidence.sequenceId -cne $SequenceId -or
        [string]$Evidence.reportSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$Evidence.healthSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label stored quality evidence envelope is invalid."
    }
    if ([string]$Evidence.referenceContractPath -cne
            $ReferenceContractPath -or
        [string]$Evidence.referenceContractSha256 -cne
            $ExpectedReferenceContractSha256 -or
        [string]$Evidence.qualityContractSha256 -cne
            $ExpectedQualityContractSha256) {
        throw "$Label stored reference/ROI inputs differ."
    }
    if ($Role -eq "canonical") {
        if (-not [string]::IsNullOrEmpty(
                [string]$Evidence.qualityContractPath)) {
            throw "$Label canonical evidence claims a consumed ROI contract."
        }
    } elseif (-not [string]::Equals(
            [System.IO.Path]::GetFullPath(
                [string]$Evidence.qualityContractPath),
            [System.IO.Path]::GetFullPath($QualityContractPath),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $QualityContractPath -PathType Leaf) -or
        (Get-Sha256 $QualityContractPath) -cne
            $ExpectedQualityContractSha256) {
        throw "$Label stored ROI input differs."
    }
    $reportPath = Join-Path $ExpectedRoot "report.json"
    $healthPath = [System.IO.Path]::ChangeExtension(
        $reportPath,
        ".health.json")
    $outputDirectory = Join-Path $ExpectedRoot "checkpoints"
    Assert-QualitySequenceInputHashes `
        $BuildIdentity $ReferenceContractPath `
        $ExpectedReferenceContractSha256 $QualityContractPath `
        $ExpectedQualityContractSha256 $Role "$Label stored pre-audit"
    Assert-PathIdentity ([string]$Evidence.reportPath) $reportPath "$Label report"
    Assert-PathIdentity ([string]$Evidence.healthPath) $healthPath "$Label health"
    Assert-PathIdentity `
        ([string]$Evidence.outputDirectory) $outputDirectory `
        "$Label checkpoints"
    foreach ($item in @(
            @($reportPath, [string]$Evidence.reportSha256, "report"),
            @($healthPath, [string]$Evidence.healthSha256, "health"))) {
        if (-not (Test-Path -LiteralPath ([string]$item[0]) -PathType Leaf) -or
            (Get-Sha256 ([string]$item[0])) -cne [string]$item[1]) {
            throw "$Label stored $($item[2]) bytes changed."
        }
    }
    if ([string]$Evidence.buildBundleFingerprint -cne
            [string]$BuildIdentity.BundleFingerprint -or
        [string]$Evidence.runtimeExecutableBundleHash -cne
            [string]$BuildIdentity.RuntimeExecutableBundleHash) {
        throw "$Label stored build identity differs."
    }
    if (($Evidence.buildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
        ($BuildIdentity | ConvertTo-Json -Depth 12 -Compress)) {
        throw "$Label stored full build identity differs."
    }
    Assert-PathIdentity `
        ([string]$Evidence.buildRootPath) ([string]$BuildIdentity.RootPath) `
        "$Label build"
    $report = Read-QualitySequenceReport $reportPath
    Assert-QualitySequenceReport `
        $Manifest $Workload $report $BuildIdentity $Configuration $Role `
        $SequenceId $ExpectedCommit $reportPath $outputDirectory `
        $ReferenceContractPath $ExpectedReferenceContractSha256 `
        $QualityContractPath $ExpectedQualityContractSha256 `
        $ReferenceContract $Label
    $health = Get-Content -LiteralPath $healthPath -Raw |
        ConvertFrom-Json -DateKind String
    Assert-QualitySequenceHealthReport `
        $Manifest $Workload $health $report $BuildIdentity $Configuration `
        $Role $SequenceId $ExpectedCommit $reportPath $outputDirectory `
        $ReferenceContractPath $QualityContractPath $Label
    $null = Assert-QualityFrozenVerifierEvidence `
        $Workload $report $Role $SequenceId $reportPath `
        $Evidence.frozenVerifierEvidence $VerifierBuildIdentity $Label
    $verifiedMetrics = $null
    if ($Role -ne "canonical") {
        Assert-ExactPropertyNames $Evidence.verifiedMetrics @(
            "metricVerifierArtifact", "spatial", "temporal") `
            "$Label verified metrics"
        $metricArtifactPath = [System.IO.Path]::ChangeExtension(
            $reportPath,
            ".metric-verification.json")
        $verifiedMetrics = Get-RecomputedQualitySequenceMetrics `
            $Manifest $Workload $report $ReferenceContract `
            $VerifierBuildIdentity $QualityContractPath $metricArtifactPath `
            $Evidence.verifiedMetrics.metricVerifierArtifact $Label
        if ($Role -eq "candidate") {
            Assert-QualitySequenceSpatialEnvelope `
                $verifiedMetrics @($SpatialEnvelope) $Label
        }
        if (($verifiedMetrics | ConvertTo-Json -Depth 16 -Compress) -cne
            ($Evidence.verifiedMetrics | ConvertTo-Json -Depth 16 -Compress)) {
            throw "$Label stored verified metrics differ from recomputation."
        }
    } elseif ($null -ne $Evidence.verifiedMetrics) {
        throw "$Label canonical evidence unexpectedly stores comparison metrics."
    }
    if ([string]$Evidence.trajectoryRouteHash -cne
            [string]$report.TrajectoryRouteHash -or
        [string]$Evidence.trajectorySequenceHash -cne
            [string]$report.TrajectorySequenceHash -or
        [string]$Evidence.producerGpuName -cne
            [string]$report.ProducerIdentity.GpuName -or
        [string]$Evidence.producerDriverVersion -cne
            [string]$report.ProducerIdentity.DriverVersion -or
        [string]$Evidence.producerQualityTier -cne
            [string]$report.ProducerIdentity.QualityTier) {
        throw "$Label stored route/producer identity differs."
    }
    if (($Evidence.captureRun | ConvertTo-Json -Depth 16 -Compress) -cne
            ($report.CaptureRun | ConvertTo-Json -Depth 16 -Compress) -or
        ($Evidence.producerIdentity | ConvertTo-Json -Depth 16 -Compress) -cne
            ($report.ProducerIdentity | ConvertTo-Json -Depth 16 -Compress)) {
        throw "$Label stored top-level provenance differs from its report."
    }
    $storedCheckpoints = @($Evidence.checkpoints)
    $reportCheckpoints = @($report.Checkpoints)
    if ($storedCheckpoints.Count -ne $reportCheckpoints.Count) {
        throw "$Label stored checkpoint evidence is incomplete."
    }
    for ($index = 0; $index -lt $storedCheckpoints.Count; $index++) {
        $stored = $storedCheckpoints[$index]
        $actual = $reportCheckpoints[$index]
        Assert-ExactPropertyNames $stored @(
            "ordinal", "routeFrameIndex", "pfmPath", "pfmSha256",
            "captureToken", "ddgiFrameSerial", "absoluteFrameIndex",
            "width", "height", "camera", "sceneAssetHash",
            "sceneStateHash", "sceneContentRevision", "settingsFingerprint",
            "captureRun", "producerIdentity", "hdrDifference",
            "activationFrameState") "$Label checkpoint $index evidence"
        $expectedStored = [ordered]@{
            ordinal = [int]$actual.Ordinal
            routeFrameIndex = [int]$actual.RouteFrameIndex
            pfmPath = [System.IO.Path]::GetFullPath([string]$actual.PfmPath)
            pfmSha256 = [string]$actual.PfmSha256
            captureToken = [string]$actual.CaptureToken
            ddgiFrameSerial = [UInt64]$actual.DdgiFrameSerial
            absoluteFrameIndex = [int]$actual.AbsoluteFrameIndex
            width = [int]$actual.Width
            height = [int]$actual.Height
            camera = $actual.Camera
            sceneAssetHash = [string]$actual.SceneAssetHash
            sceneStateHash = [string]$actual.SceneStateHash
            sceneContentRevision = [UInt64]$actual.SceneContentRevision
            settingsFingerprint = [string]$actual.SettingsFingerprint
            captureRun = $actual.CaptureRun
            producerIdentity = $actual.ProducerIdentity
            hdrDifference = $actual.HdrDifference
            activationFrameState = $actual.ActivationFrameState
        }
        if (($stored | ConvertTo-Json -Depth 32 -Compress) -cne
            ($expectedStored | ConvertTo-Json -Depth 32 -Compress)) {
            throw "$Label stored checkpoint $index differs from its report."
        }
        Assert-PathIdentity `
            ([string]$stored.pfmPath) ([string]$actual.PfmPath) `
            "$Label checkpoint $index"
    }
    Assert-QualitySequenceInputHashes `
        $BuildIdentity $ReferenceContractPath `
        $ExpectedReferenceContractSha256 $QualityContractPath `
        $ExpectedQualityContractSha256 $Role "$Label stored post-audit"
    foreach ($checkpoint in $reportCheckpoints) {
        $pfmPath = [string]$checkpoint.PfmPath
        if (-not (Test-Path -LiteralPath $pfmPath -PathType Leaf) -or
            (Get-Sha256 $pfmPath) -cne [string]$checkpoint.PfmSha256) {
            throw "$Label checkpoint PFM bytes changed during validation."
        }
    }
    foreach ($item in @(
            @($reportPath, [string]$Evidence.reportSha256, "report"),
            @($healthPath, [string]$Evidence.healthSha256, "health"),
            @([string]$Evidence.frozenVerifierEvidence.activation.artifactPath,
                [string]$Evidence.frozenVerifierEvidence.activation.artifactSha256,
                "quality activation verifier artifact"),
            @([string]$Evidence.frozenVerifierEvidence.sponzaAnimationSidecar.path,
                [string]$Evidence.frozenVerifierEvidence.sponzaAnimationSidecar.sha256,
                "common animation sidecar"))) {
        if ([string]::IsNullOrEmpty([string]$item[0])) {
            if (-not [string]::IsNullOrEmpty([string]$item[1])) {
                throw "$Label stored $($item[2]) has a hash without a path."
            }
            continue
        }
        if (-not (Test-Path -LiteralPath ([string]$item[0]) -PathType Leaf) -or
            (Get-Sha256 ([string]$item[0])) -cne [string]$item[1]) {
            throw "$Label stored $($item[2]) bytes changed during validation."
        }
    }
    if ($Role -ne "canonical") {
        $metricArtifact = $Evidence.verifiedMetrics.metricVerifierArtifact
        if (-not (Test-Path -LiteralPath ([string]$metricArtifact.artifactPath) `
                -PathType Leaf) -or
            (Get-Sha256 ([string]$metricArtifact.artifactPath)) -cne
                [string]$metricArtifact.artifactSha256) {
            throw "$Label stored metric verifier artifact bytes changed during validation."
        }
    }
    return $report
}

function Assert-QualitySequenceReferenceContract {
    param(
        $Manifest,
        $Workload,
        $Contract,
        $CanonicalReport,
        [string]$QualityContractPath,
        [string]$QualityContractSha256,
        [bool]$Candidate,
        [object[]]$ExpectedGates,
        [string[]]$ExpectedRepeatHashes,
        [string]$Label)
    $expectedContract = New-QualitySequenceReferenceContract `
        $Manifest $Workload $CanonicalReport $QualityContractPath `
        $QualityContractSha256 $ExpectedGates $ExpectedRepeatHashes
    $actualCanonicalJson = $Contract | ConvertTo-Json -Depth 24 -Compress
    $expectedCanonicalJson = $expectedContract |
        ConvertTo-Json -Depth 24 -Compress
    if ($actualCanonicalJson -cne $expectedCanonicalJson) {
        throw "$Label bytes do not encode the exact canonical-derived contract."
    }
    $expectedIndices = @(Get-QualitySequenceCheckpointIndices (
        [string]$Workload.qualityTrajectory))
    if ([string]$Contract.schema -cne
            "njulf-benchmark-quality-sequence-reference/v1" -or
        [string]$Contract.sceneKind -cne [string]$Workload.scene -or
        [string]$Contract.scenario -cne [string]$Workload.scenario -or
        [string]$Contract.captureVariant -cne [string]$Workload.captureVariant -or
        [string]$Contract.trajectory -cne [string]$Workload.qualityTrajectory -or
        [int]$Contract.trajectoryFrameCount -ne
            (Get-QualitySequenceTrajectoryFrameCount ([string]$Workload.qualityTrajectory)) -or
        [int]$Contract.warmupFrameCount -ne [int]$Workload.warmupFrames -or
        [int]$Contract.maximumAdditionalSettlingFrameCount -ne
            [int]$Manifest.capture.maximumSettlingFrames -or
        [int]$Contract.maximumReadbackDrainFrameCount -ne
            [int]$Manifest.qualitySequence.maximumReadbackDrainFrames -or
        [string]$Contract.checkpointContractFingerprint -cne
            (Get-QualitySequenceCheckpointFingerprint ([string]$Workload.qualityTrajectory)) -or
        [double]$Contract.maximumRelativeRmse -ne
            [double]$Manifest.quality.maximumRelativeRmse -or
        [double]$Contract.maximumFlipP95 -ne
            [double]$Manifest.quality.maximumFlipP95 -or
        [double]$Contract.temporalResidualFloor -ne
            [double]$Manifest.qualitySequence.temporalResidualFloor -or
        [double]$Contract.temporalResidualMultiplier -ne
            [double]$Manifest.qualitySequence.temporalResidualMultiplier -or
        [double]$Contract.temporalResidualHardCeiling -ne
            [double]$Manifest.qualitySequence.temporalResidualHardCeiling) {
        throw "$Label quality reference contract differs from the exact campaign policy."
    }
    if ((@($Contract.checkpointIndices | ForEach-Object { [int]$_ }) -join ',') -cne
        ($expectedIndices -join ',')) {
        throw "$Label checkpoint topology differs."
    }
    Assert-PathIdentity `
        ([string]$Contract.qualityContractPath) $QualityContractPath `
        "$Label ROI contract"
    if ([string]$Contract.qualityContractSha256 -cne $QualityContractSha256) {
        throw "$Label ROI contract hash differs."
    }
    foreach ($name in @(
            "trajectoryFingerprint", "trajectoryRouteHash",
            "trajectorySequenceHash")) {
        if (-not (Test-Sha256Identity ([string]$Contract.$name)) -or
            [string]$Contract.$name -cne [string]$CanonicalReport.$name) {
            throw "$Label $name differs from canonical evidence."
        }
    }
    if ([int]$Contract.firstRouteAbsoluteFrameIndex -ne
            [int]$CanonicalReport.FirstRouteAbsoluteFrameIndex -or
        [string]$Contract.buildConfiguration -cne
            [string]$CanonicalReport.BuildConfiguration -or
        @($Contract.checkpoints).Count -ne $expectedIndices.Count) {
        throw "$Label canonical execution identity differs."
    }
    for ($index = 0; $index -lt $expectedIndices.Count; $index++) {
        $contractCheckpoint = @($Contract.checkpoints)[$index]
        $canonicalCheckpoint = @($CanonicalReport.Checkpoints)[$index]
        if ([int]$contractCheckpoint.ordinal -ne $index -or
            [int]$contractCheckpoint.routeFrameIndex -ne
                [int]$expectedIndices[$index] -or
            [string]$contractCheckpoint.pfmSha256 -cne
                [string]$canonicalCheckpoint.PfmSha256 -or
            [string]$contractCheckpoint.captureToken -cne
                [string]$canonicalCheckpoint.CaptureToken -or
            [UInt64]$contractCheckpoint.ddgiFrameSerial -ne
                [UInt64]$canonicalCheckpoint.DdgiFrameSerial) {
            throw "$Label checkpoint $index differs from canonical evidence."
        }
        Assert-PathIdentity `
            ([string]$contractCheckpoint.pfmPath) `
            ([string]$canonicalCheckpoint.PfmPath) `
            "$Label canonical checkpoint $index"
    }
    $actualGates = @($Contract.temporalGates)
    $actualHashes = @($Contract.baselineRepeatReportSha256 |
        ForEach-Object { [string]$_ })
    if ($actualGates.Count -ne $ExpectedGates.Count -or
        (($actualHashes -join "`n") -cne ($ExpectedRepeatHashes -join "`n"))) {
        throw "$Label temporal derivation evidence differs."
    }
    for ($index = 0; $index -lt $ExpectedGates.Count; $index++) {
        if ([int]$actualGates[$index].fromRouteFrameIndex -ne
                [int]$ExpectedGates[$index].fromRouteFrameIndex -or
            [int]$actualGates[$index].toRouteFrameIndex -ne
                [int]$ExpectedGates[$index].toRouteFrameIndex -or
            [double]$actualGates[$index].maximumRelativeResidual -ne
                [double]$ExpectedGates[$index].maximumRelativeResidual) {
            throw "$Label temporal gate $index differs."
        }
    }
    if ($Candidate -and $actualHashes.Count -ne 2) {
        throw "$Label candidate contract lacks two baseline repeats."
    }
    if (-not $Candidate -and
        ($actualGates.Count -ne 0 -or $actualHashes.Count -ne 0)) {
        throw "$Label repeat contract contains derived candidate gates."
    }
}

function Assert-LockedQualitySequenceReference {
    param(
        $Manifest,
        $Workload,
        $ReferenceEntry,
        $ReferenceBuild,
        [string]$Configuration,
        [string]$BaselineCommit,
        [string]$Label)
    $quality = $ReferenceEntry.qualitySequence
    $root = Join-Path $script:RunRoot (
        "references/{0}/{1}/quality-sequence" -f
            $Configuration, [string]$Workload.id)
    if ($null -eq $quality -or
        [string]$quality.schema -cne
            "njulf-perf-campaign-quality-reference/v1" -or
        @($quality.repeats).Count -ne 2 -or
        @($quality.baselineRepeatReportSha256).Count -ne 2 -or
        @($quality.baselineRepeatReportSha256 | Select-Object -Unique).Count -ne 2) {
        throw "$Label quality reference topology is incomplete."
    }
    Assert-ExactPropertyNames $quality @(
        "schema", "canonical", "repeatReferenceContractPath",
        "repeatReferenceContractSha256", "repeats",
        "candidateReferenceContractPath",
        "candidateReferenceContractSha256", "temporalGates",
        "spatialEnvelope", "baselineRepeatReportSha256") `
        "$Label quality reference"
    $expectedEvidenceProperties = @(Get-QualitySequenceEvidencePropertyNames)
    Assert-ExactPropertyNames `
        $quality.canonical $expectedEvidenceProperties `
        "$Label canonical evidence"
    foreach ($repeatEvidence in @($quality.repeats)) {
        Assert-ExactPropertyNames `
            $repeatEvidence $expectedEvidenceProperties `
            "$Label repeat evidence"
    }
    $repeatContractPath = Join-Path $root "repeat-reference.json"
    $candidateContractPath = Join-Path $root "candidate-reference.json"
    Assert-PathIdentity `
        ([string]$quality.repeatReferenceContractPath) $repeatContractPath `
        "$Label repeat contract"
    Assert-PathIdentity `
        ([string]$quality.candidateReferenceContractPath) $candidateContractPath `
        "$Label candidate contract"
    foreach ($item in @(
            @($repeatContractPath, [string]$quality.repeatReferenceContractSha256, "repeat contract"),
            @($candidateContractPath, [string]$quality.candidateReferenceContractSha256, "candidate contract"))) {
        if (-not (Test-Path -LiteralPath ([string]$item[0]) -PathType Leaf) -or
            [string]$item[1] -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-Sha256 ([string]$item[0])) -cne [string]$item[1]) {
            throw "$Label $($item[2]) bytes changed."
        }
    }
    $repeatContract = Get-Content -LiteralPath $repeatContractPath -Raw |
        ConvertFrom-Json -DateKind String
    $candidateContract = Get-Content -LiteralPath $candidateContractPath -Raw |
        ConvertFrom-Json -DateKind String
    $canonicalId = Get-QualitySequenceId `
        $Manifest $Configuration ([string]$Workload.id) `
        "canonical" "reference-init" $BaselineCommit 0
    $canonicalReport = Assert-QualitySequenceStoredEvidence `
        $Manifest $Workload $quality.canonical $ReferenceBuild `
        $Configuration "canonical" $canonicalId $BaselineCommit `
        "" "" "" "" $null $ReferenceBuild $null `
        (Join-Path $root "canonical") `
        "$Label canonical"
    Assert-QualitySequenceReferenceContract `
        $Manifest $Workload $repeatContract $canonicalReport `
        ([string]$ReferenceEntry.qualityContractPath) `
        ([string]$ReferenceEntry.qualityContractSha256) `
        $false @() @() "$Label repeat contract"
    $repeatReports = @()
    for ($repeat = 1; $repeat -le 2; $repeat++) {
        $repeatId = Get-QualitySequenceId `
            $Manifest $Configuration ([string]$Workload.id) `
            "repeat" "reference-init" $BaselineCommit $repeat
        $repeatReport = Assert-QualitySequenceStoredEvidence `
            $Manifest $Workload @($quality.repeats)[$repeat - 1] `
            $ReferenceBuild $Configuration "repeat" $repeatId `
            $BaselineCommit $repeatContractPath `
            ([string]$quality.repeatReferenceContractSha256) `
            ([string]$ReferenceEntry.qualityContractPath) `
            ([string]$ReferenceEntry.qualityContractSha256) `
            $repeatContract $ReferenceBuild $null `
            (Join-Path $root ("repeat-{0:D2}" -f $repeat)) `
            "$Label repeat $repeat"
        $repeatReports += $repeatReport
        if ([string]@($quality.baselineRepeatReportSha256)[$repeat - 1] -cne
            [string]@($quality.repeats)[$repeat - 1].reportSha256) {
            throw "$Label repeat $repeat report hash is not locked."
        }
    }
    $verifiedRepeatMetrics = @($quality.repeats | ForEach-Object {
        $_.verifiedMetrics
    })
    $derivedGates = @(New-QualitySequenceTemporalGates `
        $Manifest $Workload $verifiedRepeatMetrics)
    $derivedSpatialEnvelope = @(New-QualitySequenceSpatialEnvelope `
        $Manifest $Workload $verifiedRepeatMetrics)
    Assert-QualitySequenceReferenceContract `
        $Manifest $Workload $candidateContract $canonicalReport `
        ([string]$ReferenceEntry.qualityContractPath) `
        ([string]$ReferenceEntry.qualityContractSha256) `
        $true $derivedGates `
        @($quality.baselineRepeatReportSha256 | ForEach-Object { [string]$_ }) `
        "$Label candidate contract"
    if (@($quality.temporalGates).Count -ne $derivedGates.Count) {
        throw "$Label stored temporal gate count differs."
    }
    if (($quality.spatialEnvelope | ConvertTo-Json -Depth 12 -Compress) -cne
        ($derivedSpatialEnvelope | ConvertTo-Json -Depth 12 -Compress)) {
        throw "$Label stored spatial envelope differs from recomputed repeats."
    }
    for ($index = 0; $index -lt $derivedGates.Count; $index++) {
        if ([double]@($quality.temporalGates)[$index].maximumRelativeResidual -ne
            [double]$derivedGates[$index].maximumRelativeResidual) {
            throw "$Label stored temporal gate $index differs from repeats."
        }
    }
    if ([string]$canonicalReport.TrajectoryFingerprint -cne
            [string]$ReferenceEntry.trajectoryFingerprint -or
        [string]$canonicalReport.TrajectoryRouteHash -cne
            [string]$ReferenceEntry.trajectoryRouteHash -or
        [int]$canonicalReport.TrajectoryFrameCount -ne
            [int]$ReferenceEntry.trajectoryFrameCount) {
        throw "$Label quality/timing authored trajectory differs."
    }
    Assert-QualityCaptureRunEqual `
        $canonicalReport.CaptureRun $ReferenceEntry.captureRun $false `
        "$Label quality/timing"
    Assert-QualityProducerEqual `
        $canonicalReport.ProducerIdentity `
        $ReferenceEntry.producerIdentity $false `
        "$Label quality/timing"
    return [pscustomobject]@{
        repeatContract = $repeatContract
        candidateContract = $candidateContract
        canonicalReport = $canonicalReport
        repeatReports = @($repeatReports)
        derivedGates = @($derivedGates)
    }
}

function Assert-InitializedCampaignReferences {
    param(
        $Manifest,
        [string]$BaselineCommit,
        $ReferenceBuilds,
        $References,
        $ControlledIsolations)
    if ($null -eq $ControlledIsolations -or $ControlledIsolations.Count -ne 0) {
        throw "Lean campaign references must not contain controlled directional isolation artifacts."
    }
    foreach ($configuration in @(Get-CampaignConfigurations $Manifest)) {
        $build = $ReferenceBuilds[$configuration]
        Assert-BuildIdentity $build "Initialized $configuration reference"
        foreach ($workload in @($Manifest.workloads)) {
            $entry = $References[$configuration][[string]$workload.id]
            foreach ($evidence in @(
                    @([string]$entry.path, [string]$entry.sha256, "PFM"),
                    @([string]$entry.reportPath, [string]$entry.reportSha256, "report"),
                    @([string]$entry.healthPath, [string]$entry.healthSha256, "health"),
                    @([string]$entry.qualityContractPath, [string]$entry.qualityContractSha256, "quality contract"))) {
                if (-not (Test-Path -LiteralPath ([string]$evidence[0]) -PathType Leaf) -or
                    [string]$evidence[1] -notmatch '^[0-9a-f]{64}$' -or
                    -not [string]::Equals(
                        (Get-Sha256 ([string]$evidence[0])),
                        [string]$evidence[1],
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Initialized $configuration/$($workload.id) $($evidence[2]) failed final audit."
                }
            }
            Assert-LinearHdrPfm `
                ([string]$entry.path) 1920 1080 `
                "Initialized $configuration/$($workload.id) reference"
            $report = Read-BenchmarkReport ([string]$entry.reportPath)
            Assert-BenchmarkReport `
                $Manifest $workload $report $configuration `
                "Initialized $configuration/$($workload.id) reference" `
                $true ([string]$entry.pairId) $BaselineCommit `
                $build $null ""
            $health = Get-Content -LiteralPath ([string]$entry.healthPath) -Raw |
                ConvertFrom-Json -DateKind String
            Assert-HealthReport `
                $Manifest $workload $health $report $build `
                $BaselineCommit ([string]$entry.pairId) `
                "Initialized $configuration/$($workload.id) reference"
            if (($entry.buildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
                ($build | ConvertTo-Json -Depth 12 -Compress)) {
                throw "Initialized $configuration/$($workload.id) build identity differs."
            }
            $null = Assert-TimingFrozenVerifierEvidence `
                $workload $report ([string]$entry.reportPath) `
                $entry.frozenVerifierEvidence $build `
                "Initialized $configuration/$($workload.id) reference"
            if ($null -eq $entry.qualitySequence -or
                [string]$entry.qualitySequence.schema -ne
                    "njulf-perf-campaign-quality-reference/v1" -or
                @($entry.qualitySequence.repeats).Count -ne 2 -or
                @($entry.qualitySequence.baselineRepeatReportSha256).Count -ne 2) {
                throw "Initialized $configuration/$($workload.id) quality sequence is incomplete."
            }
            $null = Assert-LockedQualitySequenceReference `
                $Manifest $workload $entry $build $configuration `
                $BaselineCommit `
                "Initialized $configuration/$($workload.id)"
        }
        Assert-ReferenceTargetHypothesisPasses `
            $Manifest $References[$configuration] `
            "Initialized $configuration"
    }
    Assert-ExactCampaignHead $BaselineCommit "Reference initialization final audit"
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
}

function Initialize-CampaignReferences {
    param($Manifest, $BeautyTarget, $ProtectedFingerprints)
    Assert-CleanCampaignWorktree
    $lockPath = Join-Path $script:RunRoot "campaign.lock.json"
    if (Test-Path -LiteralPath $lockPath) {
        throw "Campaign lock already exists and will not be overwritten: $lockPath"
    }
    $baselineCommit = Get-GitText @("rev-parse", "HEAD")
    $qualityContracts = [ordered]@{}
    foreach ($workload in @($Manifest.workloads)) {
        $qualityContract = Write-QualityContract $Manifest $workload
        $qualityContracts[[string]$workload.id] = [ordered]@{
            path = $qualityContract
            sha256 = Get-Sha256 $qualityContract
        }
    }
    $references = [ordered]@{}
    $referenceBuilds = [ordered]@{}
    $referenceControlledIsolations = [ordered]@{}
    foreach ($configuration in @(Get-CampaignConfigurations $Manifest)) {
        $buildRoot = Join-Path $script:RunRoot "reference-build/$configuration"
        $referenceBuild = Invoke-BuildOutput `
            $Manifest $configuration $buildRoot `
            "Reference build ($configuration)" $baselineCommit
        $referenceBuilds[$configuration] = $referenceBuild
        $configurationReferences = [ordered]@{}
        foreach ($workload in @($Manifest.workloads)) {
            $referencePath = Get-ReferencePath $configuration $workload
            if (Test-Path -LiteralPath $referencePath) {
                throw "Reference already exists and will not be overwritten: $referencePath"
            }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $referencePath) | Out-Null
            $quality = $qualityContracts[[string]$workload.id]
            $reportPath = Join-Path (Split-Path -Parent $referencePath) "reference.json"
            $pairId = "$($Manifest.campaignId)-$configuration-$($workload.id)-reference"
            $report = Invoke-BenchmarkCapture `
                -Manifest $Manifest `
                -Workload $workload `
                -BuildIdentity $referenceBuild `
                -VerifierBuildIdentity $referenceBuild `
                -Configuration $configuration `
                -ReportPath $reportPath `
                -PairId $pairId `
                -QualityContractPath ([string]$quality.path) `
                -ReferencePath $referencePath `
                -ReferenceInitialization $true `
                -ExpectedCommit $baselineCommit `
                -ExpectedQualityContractSha256 ([string]$quality.sha256) `
                -ExpectedReferenceSha256 "" `
                -ReferenceIdentity $null `
                -Label "Initialize $configuration reference $($workload.id)"
            if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
                throw "Reference capture was not written: $referencePath"
            }
            Assert-LinearHdrPfm `
                $referencePath 1920 1080 `
                "$configuration/$($workload.id) reference"
            $configurationReferences[[string]$workload.id] = [ordered]@{
                path = $referencePath
                sha256 = Get-Sha256 $referencePath
                reportPath = $reportPath
                reportSha256 = Get-Sha256 $reportPath
                healthPath = [System.IO.Path]::ChangeExtension(
                    $reportPath,
                    ".health.json")
                healthSha256 = Get-Sha256 (
                    [System.IO.Path]::ChangeExtension(
                        $reportPath,
                        ".health.json"))
                qualityContractPath = [string]$quality.path
                qualityContractSha256 = [string]$quality.sha256
                pairId = $pairId
                trajectory = [string]$report.CaptureContract.Trajectory
                trajectoryFingerprint = [string]$report.CaptureContract.TrajectoryFingerprint
                trajectoryFrameCount = [int]$report.CaptureContract.TrajectoryFrameCount
                trajectoryRouteHash = [string]$report.CaptureContract.TrajectoryRouteHash
                trajectorySequenceHash = [string]$report.CaptureContract.TrajectorySequenceHash
                producerIdentity = $report.ProducerIdentity
                captureRun = $report.LastDiagnostics.CaptureRun
                detailedCountersCompiled = [int]$report.LastDiagnostics.DdgiDetailedCountersCompiled
                detailedCountersEnabled = [int]$report.LastDiagnostics.DdgiDetailedCountersEnabled
                frozenVerifierEvidence =
                    $report.CampaignFrozenVerifierEvidence
                buildIdentity = $referenceBuild
            }
            Assert-ProtectedFingerprints $ProtectedFingerprints
            Assert-ExactCampaignHead `
                $baselineCommit `
                "Reference capture $configuration/$($workload.id)"
            Assert-CleanCampaignWorktree
        }
        $references[$configuration] = $configurationReferences
    }
    foreach ($configuration in @(Get-CampaignConfigurations $Manifest)) {
        Assert-ReferenceTargetHypothesisPasses `
            $Manifest $references[$configuration] `
            "Initialize $configuration"
    }
    # Global phase boundary: every Release and ShippingPerformance endpoint
    # timing reference is complete before any standalone quality readback.
    foreach ($configuration in @(Get-CampaignConfigurations $Manifest)) {
        $referenceBuild = $referenceBuilds[$configuration]
        $configurationReferences = $references[$configuration]
        foreach ($workload in @($Manifest.workloads)) {
            $quality = $qualityContracts[[string]$workload.id]
            $configurationReferences[[string]$workload.id]["qualitySequence"] =
                Initialize-QualitySequenceReference `
                    $Manifest $workload $referenceBuild $configuration `
                    $baselineCommit ([string]$quality.path) `
                    ([string]$quality.sha256)
            Assert-ProtectedFingerprints $ProtectedFingerprints
            Assert-ExactCampaignHead `
                $baselineCommit `
                "Quality reference $configuration/$($workload.id)"
            Assert-CleanCampaignWorktree
        }
    }
    Assert-InitializedCampaignReferences `
        $Manifest $baselineCommit $referenceBuilds $references `
        $referenceControlledIsolations
    $lock = [ordered]@{
        schema = "njulf-perf-campaign-lock/v9"
        campaignId = [string]$Manifest.campaignId
        createdAtUtc = [DateTimeOffset]::UtcNow
        manifestPath = $script:ManifestFile
        manifestSha256 = Get-AdmittedCampaignManifestSha256
        manifestSnapshotPath = $script:CampaignManifestSnapshotPath
        manifestSnapshotSha256 = Get-Sha256 `
            $script:CampaignManifestSnapshotPath
        gitInfoExcludePath = $script:GitInfoExcludePath
        gitInfoExcludeFingerprint = $script:GitInfoExcludeFingerprint
        baselineCommit = $baselineCommit
        baselineStatus = "clean"
        configurations = @(Get-CampaignConfigurations $Manifest)
        advisoryBeautyTarget = $BeautyTarget
        cookedAssets = $script:CookedAssetBundle.Identity
        protectedFingerprints = $ProtectedFingerprints
        referenceBuilds = $referenceBuilds
        references = $references
        controlledIsolations = $referenceControlledIsolations
        discoveryPolicy = $Manifest.discoveryPolicy
        reviewedCandidates = @($Manifest.candidates)
        targetHypotheses = @($Manifest.targetHypotheses)
    }
    Assert-ExactCampaignHead $baselineCommit "Campaign lock publication"
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $ProtectedFingerprints
    if (Test-Path -LiteralPath $lockPath) {
        throw "Campaign lock appeared during initialization: $lockPath"
    }
    Write-JsonArtifact $lockPath $lock
    $script:CampaignLockPath = $lockPath
    $script:CampaignLockSha256 = Get-Sha256 $lockPath
    return Read-CampaignLock $Manifest $BeautyTarget
}

function Read-CampaignLock {
    param($Manifest, $BeautyTarget)
    $path = Join-Path $script:RunRoot "campaign.lock.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Campaign lock is missing. Run with -InitializeReferences first."
    }
    $lock = Get-Content -LiteralPath $path -Raw |
        ConvertFrom-Json -DateKind String
    $script:CampaignLockPath = $path
    $script:CampaignLockSha256 = Get-Sha256 $path
    if ([string]$lock.schema -ne "njulf-perf-campaign-lock/v9" -or
        [string]$lock.campaignId -ne [string]$Manifest.campaignId -or
        [string]$lock.manifestSha256 -ne
            (Get-AdmittedCampaignManifestSha256)) {
        throw "Campaign lock does not match the current manifest. References must be re-established deliberately."
    }
    if ([string]$lock.baselineStatus -ne "clean" -or
        [string]$lock.baselineCommit -notmatch '^[0-9a-f]{40}$') {
        throw "Campaign lock has no canonical clean baseline commit."
    }
    Assert-ExactPropertyNames $lock @(
        "schema", "campaignId", "createdAtUtc", "manifestPath",
        "manifestSha256", "manifestSnapshotPath",
        "manifestSnapshotSha256", "gitInfoExcludePath",
        "gitInfoExcludeFingerprint", "baselineCommit", "baselineStatus",
        "configurations", "advisoryBeautyTarget", "cookedAssets",
        "protectedFingerprints",
        "referenceBuilds", "references", "controlledIsolations",
        "discoveryPolicy", "reviewedCandidates", "targetHypotheses") `
        "Campaign lock"
    $createdAtUtc = [DateTimeOffset]::MinValue
    $expectedConfigurations = @(Get-CampaignConfigurations $Manifest)
    if (-not [DateTimeOffset]::TryParse(
            [string]$lock.createdAtUtc,
            [ref]$createdAtUtc) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$lock.manifestPath),
            $script:ManifestFile,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath(
                [string]$lock.manifestSnapshotPath),
            $script:CampaignManifestSnapshotPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$lock.manifestSnapshotSha256 -cne
            $script:CampaignManifestSha256 -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$lock.gitInfoExcludePath),
            $script:GitInfoExcludePath,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$lock.gitInfoExcludeFingerprint -cne
            $script:GitInfoExcludeFingerprint -or
        ((@($lock.configurations | ForEach-Object { [string]$_ }) -join "`n") -cne
         ($expectedConfigurations -join "`n"))) {
        throw "Campaign lock metadata/configuration topology is invalid."
    }
    Assert-ExactPropertyNames $lock.advisoryBeautyTarget @(
        "ManifestPath", "ManifestSha256", "ImagePath", "ImageSha256",
        "MediaType", "Width", "Height", "Role") `
        "Campaign lock advisory beauty target"
    $lockedBeautyPairs = @(
        @([string]$lock.advisoryBeautyTarget.ManifestPath, [string]$BeautyTarget.ManifestPath),
        @([string]$lock.advisoryBeautyTarget.ManifestSha256, [string]$BeautyTarget.ManifestSha256),
        @([string]$lock.advisoryBeautyTarget.ImagePath, [string]$BeautyTarget.ImagePath),
        @([string]$lock.advisoryBeautyTarget.ImageSha256, [string]$BeautyTarget.ImageSha256),
        @([string]$lock.advisoryBeautyTarget.MediaType, [string]$BeautyTarget.MediaType),
        @([string]$lock.advisoryBeautyTarget.Width, [string]$BeautyTarget.Width),
        @([string]$lock.advisoryBeautyTarget.Height, [string]$BeautyTarget.Height),
        @([string]$lock.advisoryBeautyTarget.Role, [string]$BeautyTarget.Role))
    foreach ($pair in $lockedBeautyPairs) {
        if (-not [string]::Equals(
                [string]$pair[0],
                [string]$pair[1],
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Campaign lock advisory beauty target differs from the admitted attachment."
        }
    }
    Assert-ExactPropertyNames `
        $lock.protectedFingerprints `
        @($Manifest.protectedPaths | ForEach-Object { [string]$_ }) `
        "Campaign lock protected fingerprints"
    Assert-ExactPropertyNames `
        $lock.referenceBuilds $expectedConfigurations `
        "Campaign lock reference builds"
    Assert-ExactPropertyNames `
        $lock.references $expectedConfigurations `
        "Campaign lock references"
    if ($null -eq $lock.controlledIsolations -or
        @($lock.controlledIsolations.PSObject.Properties).Count -ne 0) {
        throw "Lean campaign lock must not contain directional controlled-isolation artifacts."
    }
    Assert-ExactPropertyNames $lock.cookedAssets @(
        "schema", "platform", "sourceRoot", "identityHash", "fileCount",
        "totalBytes", "reports") "Campaign lock cooked assets"
    foreach ($reportIdentity in @($lock.cookedAssets.reports)) {
        Assert-ExactPropertyNames $reportIdentity @(
            "model", "relativePath", "sha256") `
            "Campaign lock cooked asset report"
    }
    $cookedSourceRoot = if (-not [string]::IsNullOrWhiteSpace($CookedAssetRoot)) {
        $CookedAssetRoot
    } else {
        [string]$lock.cookedAssets.sourceRoot
    }
    $resolvedCookedAssets = Resolve-CookedAssetBundle `
        $Manifest $cookedSourceRoot "Campaign lock"
    if (($resolvedCookedAssets.Identity | ConvertTo-Json -Depth 12 -Compress) -cne
        ($lock.cookedAssets | ConvertTo-Json -Depth 12 -Compress)) {
        throw "Campaign lock cooked asset source or report inventory changed."
    }
    $script:CookedAssetBundle = $resolvedCookedAssets
    if (($lock.discoveryPolicy | ConvertTo-Json -Depth 12 -Compress) -cne
            ($Manifest.discoveryPolicy | ConvertTo-Json -Depth 12 -Compress) -or
        ($lock.reviewedCandidates | ConvertTo-Json -Depth 12 -Compress) -cne
            ($Manifest.candidates | ConvertTo-Json -Depth 12 -Compress) -or
        ($lock.targetHypotheses | ConvertTo-Json -Depth 12 -Compress) -cne
        ($Manifest.targetHypotheses | ConvertTo-Json -Depth 12 -Compress)) {
        throw "Campaign lock policy, candidates, or hypotheses differ from the manifest."
    }
    foreach ($path in @($Manifest.protectedPaths)) {
        $lockedProperty =
            $lock.protectedFingerprints.PSObject.Properties[[string]$path]
        $actualFingerprint = Get-CanonicalPathFingerprint ([string]$path)
        if ($null -eq $lockedProperty -or
            -not [string]::Equals(
                [string]$lockedProperty.Value,
                $actualFingerprint,
                [StringComparison]::Ordinal)) {
            throw "Campaign lock protected fingerprint failed for '$path'."
        }
    }
    $expectedWorkloadIds = @($Manifest.workloads |
        ForEach-Object { [string]$_.id })
    $expectedReferenceEntryProperties = @(
        "path", "sha256", "reportPath", "reportSha256", "healthPath",
        "healthSha256", "qualityContractPath", "qualityContractSha256",
        "pairId", "trajectory", "trajectoryFingerprint",
        "trajectoryFrameCount", "trajectoryRouteHash",
        "trajectorySequenceHash", "producerIdentity", "captureRun",
        "detailedCountersCompiled", "detailedCountersEnabled",
        "frozenVerifierEvidence", "buildIdentity", "qualitySequence")
    foreach ($configuration in $expectedConfigurations) {
        $referenceBuild = Get-ReferenceBuildIdentity $lock $configuration
        Assert-ExactPropertyNames $referenceBuild @(
            "RootPath", "ExecutablePath", "ExecutableFileSha256",
            "RuntimeExecutableBundleHash", "BundleFingerprint",
            "CookedAssetBundle", "BuildCommit", "ProjectPath",
            "SourceProvenance", "IntermediateIsolation") `
            "Locked $configuration reference build"
        $expectedBuildRoot = Join-Path $script:RunRoot (
            "reference-build/{0}" -f $configuration)
        Assert-PathIdentity `
            ([string]$referenceBuild.RootPath) $expectedBuildRoot `
            "Locked $configuration reference build"
        Assert-PathIdentity `
            ([string]$referenceBuild.ExecutablePath) `
            (Join-Path $expectedBuildRoot "NjulfHelloGame.exe") `
            "Locked $configuration reference executable"
        Assert-BuildIdentity `
            $referenceBuild `
            "Locked $configuration reference"
        $configurationEntries =
            $lock.references.PSObject.Properties[$configuration].Value
        Assert-ExactPropertyNames `
            $configurationEntries $expectedWorkloadIds `
            "Locked $configuration workload references"
        foreach ($workload in @($Manifest.workloads)) {
            $entry = Get-ReferenceLockEntry `
                $lock $configuration ([string]$workload.id)
            Assert-ExactPropertyNames `
                $entry $expectedReferenceEntryProperties `
                "Locked $configuration/$($workload.id) reference entry"
            $expectedReferencePath = Get-ReferencePath $configuration $workload
            $expectedQualityPath = Join-Path $script:RunRoot (
                "quality-contracts/{0}.json" -f [string]$workload.id)
            $expectedReportPath = Join-Path (
                Split-Path -Parent $expectedReferencePath) "reference.json"
            $expectedHealthPath = [System.IO.Path]::ChangeExtension(
                $expectedReportPath,
                ".health.json")
            $expectedPairId =
                "$($Manifest.campaignId)-$configuration-$($workload.id)-reference"
            if (-not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$entry.path),
                    [System.IO.Path]::GetFullPath($expectedReferencePath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$entry.reportPath),
                    [System.IO.Path]::GetFullPath($expectedReportPath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$entry.healthPath),
                    [System.IO.Path]::GetFullPath($expectedHealthPath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [System.IO.Path]::GetFullPath([string]$entry.qualityContractPath),
                    [System.IO.Path]::GetFullPath($expectedQualityPath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath ([string]$entry.path) -PathType Leaf) -or
                [string]$entry.sha256 -notmatch '^[0-9a-f]{64}$' -or
                [string]$entry.sha256 -ne (Get-Sha256 ([string]$entry.path)) -or
                -not (Test-Path -LiteralPath ([string]$entry.reportPath) -PathType Leaf) -or
                [string]$entry.reportSha256 -ne (Get-Sha256 ([string]$entry.reportPath)) -or
                -not (Test-Path -LiteralPath ([string]$entry.healthPath) -PathType Leaf) -or
                [string]$entry.healthSha256 -notmatch '^[0-9a-f]{64}$' -or
                [string]$entry.healthSha256 -ne (Get-Sha256 ([string]$entry.healthPath)) -or
                [string]$entry.qualityContractSha256 -notmatch '^[0-9a-f]{64}$' -or
                [string]$entry.qualityContractSha256 -ne (Get-Sha256 ([string]$entry.qualityContractPath)) -or
                [string]$entry.pairId -ne $expectedPairId -or
                -not (Test-Sha256Identity ([string]$entry.trajectoryFingerprint)) -or
                -not (Test-Sha256Identity ([string]$entry.trajectoryRouteHash)) -or
                -not (Test-Sha256Identity ([string]$entry.trajectorySequenceHash))) {
                throw "Reference lock failed for '$configuration/$($workload.id)'."
            }
            Assert-LinearHdrPfm `
                ([string]$entry.path) 1920 1080 `
                "Locked $configuration/$($workload.id) reference"
            $report = Read-BenchmarkReport ([string]$entry.reportPath)
            Assert-BenchmarkReport `
                $Manifest $workload $report $configuration `
                "Locked reference $configuration/$($workload.id)" $true `
                ([string]$entry.pairId) ([string]$lock.baselineCommit) `
                $referenceBuild $null ""
            $health = Get-Content -LiteralPath ([string]$entry.healthPath) -Raw |
                ConvertFrom-Json -DateKind String
            Assert-HealthReport `
                $Manifest $workload $health $report $referenceBuild `
                ([string]$lock.baselineCommit) ([string]$entry.pairId) `
                "Locked reference $configuration/$($workload.id)"
            if (($entry.buildIdentity | ConvertTo-Json -Depth 12 -Compress) -cne
                ($referenceBuild | ConvertTo-Json -Depth 12 -Compress)) {
                throw "Locked reference build identity differs for '$configuration/$($workload.id)'."
            }
            $null = Assert-TimingFrozenVerifierEvidence `
                $workload $report ([string]$entry.reportPath) `
                $entry.frozenVerifierEvidence $referenceBuild `
                "Locked reference $configuration/$($workload.id)"
            if (-not [string]::Equals(
                    [string]$report.CaptureContract.Trajectory,
                    [string]$entry.trajectory,
                    [StringComparison]::Ordinal) -or
                -not [string]::Equals(
                    [string]$report.CaptureContract.TrajectoryFingerprint,
                    [string]$entry.trajectoryFingerprint,
                    [StringComparison]::Ordinal) -or
                [int]$report.CaptureContract.TrajectoryFrameCount -ne
                    [int]$entry.trajectoryFrameCount -or
                -not [string]::Equals(
                    [string]$report.CaptureContract.TrajectoryRouteHash,
                    [string]$entry.trajectoryRouteHash,
                    [StringComparison]::Ordinal) -or
                -not [string]::Equals(
                    [string]$report.CaptureContract.TrajectorySequenceHash,
                    [string]$entry.trajectorySequenceHash,
                    [StringComparison]::Ordinal)) {
                throw "Locked reference report identity differs for '$configuration/$($workload.id)'."
            }
            $lockedIdentityPairs = @(
                @([string]$report.ProducerIdentity.Schema, [string]$entry.producerIdentity.Schema, "producer schema"),
                @([string]$report.ProducerIdentity.BuildCommit, [string]$entry.producerIdentity.BuildCommit, "producer commit"),
                @([string]$report.ProducerIdentity.ShaderFingerprint, [string]$entry.producerIdentity.ShaderFingerprint, "producer shader"),
                @([string]$report.ProducerIdentity.SettingsFingerprint, [string]$entry.producerIdentity.SettingsFingerprint, "producer settings"),
                @([string]$report.ProducerIdentity.GpuName, [string]$entry.producerIdentity.GpuName, "producer GPU"),
                @([string]$report.ProducerIdentity.DriverVersion, [string]$entry.producerIdentity.DriverVersion, "producer driver"),
                @([string]$report.ProducerIdentity.QualityTier, [string]$entry.producerIdentity.QualityTier, "producer tier"),
                @([string]$report.LastDiagnostics.CaptureRun.Commit, [string]$entry.captureRun.Commit, "capture commit"),
                @([string]$report.LastDiagnostics.CaptureRun.DirtyWorktreeState, [string]$entry.captureRun.DirtyWorktreeState, "capture dirty state"),
                @([string]$report.LastDiagnostics.CaptureRun.ExecutableHash, [string]$entry.captureRun.ExecutableHash, "capture executable"),
                @([string]$report.LastDiagnostics.CaptureRun.ShaderBundleHash, [string]$entry.captureRun.ShaderBundleHash, "capture shaders"),
                @([string]$report.LastDiagnostics.CaptureRun.BuildConfiguration, [string]$entry.captureRun.BuildConfiguration, "capture build"),
                @([string]$report.LastDiagnostics.CaptureRun.ApplicationVersion, [string]$entry.captureRun.ApplicationVersion, "capture application version"),
                @([string]$report.LastDiagnostics.CaptureRun.SettingsSchemaVersion, [string]$entry.captureRun.SettingsSchemaVersion, "capture settings schema"),
                @([string]$report.LastDiagnostics.CaptureRun.SceneKind, [string]$entry.captureRun.SceneKind, "capture scene"),
                @([string]$report.LastDiagnostics.CaptureRun.Scenario, [string]$entry.captureRun.Scenario, "capture scenario"))
            foreach ($pair in $lockedIdentityPairs) {
                if (-not [string]::Equals(
                        [string]$pair[0],
                        [string]$pair[1],
                        [StringComparison]::Ordinal)) {
                    throw "Locked reference $($pair[2]) differs for '$configuration/$($workload.id)'."
                }
            }
            $reportSources = @($report.ProducerIdentity.SourceSettingsFingerprints)
            $entrySources = @($entry.producerIdentity.SourceSettingsFingerprints)
            if ($reportSources.Count -ne $entrySources.Count -or
                (($reportSources -join "`n") -cne ($entrySources -join "`n")) -or
                [int]$entry.detailedCountersCompiled -ne
                    [int]$report.LastDiagnostics.DdgiDetailedCountersCompiled -or
                [int]$entry.detailedCountersEnabled -ne
                    [int]$report.LastDiagnostics.DdgiDetailedCountersEnabled) {
                throw "Locked reference producer source settings differ for '$configuration/$($workload.id)'."
            }
            $null = Assert-LockedQualitySequenceReference `
                $Manifest $workload $entry $referenceBuild $configuration `
                ([string]$lock.baselineCommit) `
                "Locked $configuration/$($workload.id)"
        }
        Assert-ReferenceTargetHypothesisPasses `
            $Manifest $configurationEntries "Locked $configuration"
    }
    return $lock
}

function Assert-LockBaselineAncestor {
    param($Lock, [string]$Commit = "HEAD")
    $null = & git -C $script:SolutionRoot merge-base --is-ancestor `
        ([string]$Lock.baselineCommit) $Commit
    if ($LASTEXITCODE -ne 0) {
        throw "Commit '$Commit' does not descend from immutable baseline '$($Lock.baselineCommit)'."
    }
}

function Invoke-PinnedCandidate {
    param(
        $Manifest,
        $Candidate,
        [string]$AcceptedHead)
    Assert-ExactCampaignHead $AcceptedHead "Pinned candidate admission"
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
    $sourceCommit = [string]$Candidate.sourceCommit
    $expectedPatchId = [string]$Candidate.patchId
    $expectedPaths = @($Candidate.allowedPaths | ForEach-Object {
        [string]$_
    })
    if ((Get-StablePatchId $sourceCommit) -cne $expectedPatchId -or
        (@(Get-CommitChangedPaths $sourceCommit) -join "`n") -cne
            ($expectedPaths -join "`n")) {
        throw "Pinned candidate '$($Candidate.id)' source patch changed after manifest admission."
    }
    try {
        $null = Invoke-Git @("cherry-pick", "--no-edit", $sourceCommit)
    } catch {
        $cherryPickHead = Get-GitText @("rev-parse", "--git-path", "CHERRY_PICK_HEAD")
        $fullCherryPickHead = if ([System.IO.Path]::IsPathRooted($cherryPickHead)) {
            $cherryPickHead
        } else {
            Join-Path $script:SolutionRoot $cherryPickHead
        }
        if (Test-Path -LiteralPath $fullCherryPickHead) {
            try { $null = Invoke-Git @("cherry-pick", "--abort") } catch { }
        }
        Assert-ExactCampaignHead $AcceptedHead "Failed pinned candidate recovery"
        throw
    }
    $candidateHead = Get-GitText @("rev-parse", "HEAD")
    $candidateParent = Get-GitText @("rev-parse", "$candidateHead^")
    if ($candidateParent -cne $AcceptedHead -or
        (Get-StablePatchId $candidateHead) -cne $expectedPatchId -or
        (@(Get-CommitChangedPaths $candidateHead) -join "`n") -cne
            ($expectedPaths -join "`n")) {
        throw "Pinned candidate '$($Candidate.id)' produced an unauthenticated commit."
    }
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $script:ProtectedFingerprints
    Assert-CampaignLockIntegrity
    return $candidateHead
}

function Get-CurrentCampaignBranch {
    $output = & git -C $script:SolutionRoot `
        symbolic-ref --quiet --short HEAD 2>$null
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) { return ([string]$output).Trim() }
    if ($exitCode -eq 1) { return "" }
    throw "Could not resolve the current campaign branch (git exit $exitCode)."
}

function Test-CampaignBranchExists {
    $null = & git -C $script:SolutionRoot `
        show-ref --verify --quiet "refs/heads/$script:CampaignBranch"
    return $LASTEXITCODE -eq 0
}

function Move-ExpectedAbsentProtectedAdditions {
    param($ProtectedFingerprints, [int]$Iteration)
    foreach ($entry in $ProtectedFingerprints.GetEnumerator()) {
        if ([string]$entry.Value -ne "absent") { continue }
        $source = Resolve-SolutionPath ([string]$entry.Key)
        if (-not (Test-Path -LiteralPath $source)) { continue }
        $relative = [System.IO.Path]::GetRelativePath(
            $script:SolutionRoot,
            $source)
        if ($relative -eq "." -or
            $relative -eq ".." -or
            $relative.StartsWith(
                "..$([System.IO.Path]::DirectorySeparatorChar)",
                [StringComparison]::Ordinal)) {
            throw "Refusing to recover expected-absent path outside the solution: $source"
        }
        $archiveRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $script:RunRoot (
                "rejected-protected/{0:D3}-{1}" -f
                    $Iteration, (Get-Date -Format "yyyyMMddHHmmssfff"))))
        $destination = [System.IO.Path]::GetFullPath(
            (Join-Path $archiveRoot $relative))
        $destinationRelative = [System.IO.Path]::GetRelativePath(
            $archiveRoot,
            $destination)
        if ($destinationRelative -eq ".." -or
            $destinationRelative.StartsWith(
                "..$([System.IO.Path]::DirectorySeparatorChar)",
                [StringComparison]::Ordinal)) {
            throw "Refusing to archive protected addition outside '$archiveRoot'."
        }
        if (Test-Path -LiteralPath $destination) {
            throw "Protected-addition recovery destination already exists: $destination"
        }
        New-Item -ItemType Directory -Force -Path (
            Split-Path -Parent $destination) | Out-Null
        Move-Item -LiteralPath $source -Destination $destination
        Write-Warning (
            "Moved rejected expected-absent addition '$source' to recoverable archive '$destination'.")
    }
}

function Restore-AcceptedHead {
    param(
        $Manifest,
        [string]$AcceptedHead,
        [int]$Iteration,
        [bool]$Archive,
        $ProtectedFingerprints,
        $AcceptanceRefSnapshot)
    Assert-CampaignWorktreeRoot
    Restore-AcceptanceRefSnapshot $Manifest $AcceptanceRefSnapshot
    Move-ExpectedAbsentProtectedAdditions $ProtectedFingerprints $Iteration
    $status = Get-GitText @("status", "--porcelain=v1", "--untracked-files=all")
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        $null = Invoke-Git @("stash", "push", "--include-untracked", "--message", "perf campaign rejected dirty trial $Iteration")
    }
    $candidateHead = Get-GitText @("rev-parse", "HEAD")
    if ($Archive -and $candidateHead -ne $AcceptedHead) {
        $ref = "refs/perf-campaign/rejected/{0:D3}-{1}" -f $Iteration, (Get-Date -Format "yyyyMMddHHmmss")
        $null = Invoke-Git @("update-ref", $ref, $candidateHead)
    }
    $currentBranch = Get-CurrentCampaignBranch
    $branchExists = Test-CampaignBranchExists
    if (-not [string]::Equals(
            $currentBranch,
            $script:CampaignBranch,
            [StringComparison]::Ordinal)) {
        if ($branchExists) {
            $null = Invoke-Git @("switch", $script:CampaignBranch)
        } else {
            $null = Invoke-Git @(
                "switch", "--create", $script:CampaignBranch, $AcceptedHead)
        }
    } elseif (-not $branchExists) {
        $null = Invoke-Git @(
            "update-ref", "refs/heads/$script:CampaignBranch", $AcceptedHead)
    }
    $null = Invoke-Git @("reset", "--hard", $AcceptedHead)
    Assert-CampaignRepositoryRoot
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $ProtectedFingerprints
    Assert-AcceptanceRefSnapshot `
        $Manifest $AcceptanceRefSnapshot "Rejected hypothesis rollback"
}

function Get-FinalInvariantFailures {
    param(
        $Manifest,
        $Lock,
        [string]$RetainedHead,
        $ProtectedFingerprints,
        $AcceptanceRefSnapshot,
        $ExpectedRetainedChain)
    $failures = [System.Collections.Generic.List[string]]::new()
    try {
        Assert-ExactCampaignHead $RetainedHead "Retained-stack final audit"
    } catch {
        $failures.Add($_.Exception.Message)
    }
    try {
        Assert-CleanCampaignWorktree
    } catch {
        $failures.Add($_.Exception.Message)
    }
    try {
        Assert-ProtectedFingerprints $ProtectedFingerprints
    } catch {
        $failures.Add($_.Exception.Message)
    }
    try {
        Assert-CampaignLockIntegrity
    } catch {
        $failures.Add($_.Exception.Message)
    }
    if ($null -eq $AcceptanceRefSnapshot) {
        $failures.Add(
            "Retained-stack final has no admitted acceptance-ref snapshot.")
    } else {
        try {
            Assert-AcceptanceRefSnapshot `
                $Manifest $AcceptanceRefSnapshot "Retained-stack final audit"
        } catch {
            $failures.Add($_.Exception.Message)
        }
    }
    if ($null -eq $ExpectedRetainedChain) {
        $failures.Add(
            "Retained-stack final has no authenticated retained chain.")
    } else {
        try {
            $auditedChain = Assert-RetainedAcceptanceChain `
                $Manifest $Lock $RetainedHead
            if ([string]$auditedChain.LastEvidence -ne
                [string]$ExpectedRetainedChain.LastEvidence) {
                throw "Authenticated retained chain changed during finalization."
            }
        } catch {
            $failures.Add($_.Exception.Message)
        }
    }
    return @($failures)
}

function Assert-CampaignModeContract {
    $hasCandidate = -not [string]::IsNullOrWhiteSpace($CandidateId)
    $hasEnvelope = -not [string]::IsNullOrWhiteSpace($CandidateEnvelopePath)
    $hasPreparationArguments = @(@(
        $DiscoveryArtifactPath,
        $AutomaticCandidateId,
        $AutomaticCandidateSourceCommit,
        $AutomaticCandidateFocusedTestFilter,
        $CandidateEnvelopeOutputPath) | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_)
        })
    if (($hasCandidate -and $hasEnvelope) -or
        ($PrepareCandidateEnvelope -and ($hasCandidate -or $hasEnvelope))) {
        throw "Reviewed, enveloped, and envelope-preparation candidate modes are mutually exclusive."
    }
    if (($PrepareCandidateEnvelope -and $hasPreparationArguments.Count -ne 5) -or
        (-not $PrepareCandidateEnvelope -and $hasPreparationArguments.Count -ne 0)) {
        throw "Candidate-envelope preparation requires exactly its five preparation arguments."
    }
    if (-not [string]::IsNullOrWhiteSpace($TargetHypothesisId)) {
        throw "TargetHypothesisId is derived from the admitted candidate and may not be supplied."
    }
    if ($InitializeReferences -xor $InitializeReferencesOnly) {
        throw "Reference initialization requires both InitializeReferences and InitializeReferencesOnly."
    }
    $modes = @(
        [bool]$ValidateOnly,
        [bool]($InitializeReferences -and $InitializeReferencesOnly),
        [bool]$BaselineOnly,
        [bool]$DiscoverHotspots,
        [bool]$PrepareCandidateEnvelope,
        [bool]$FinalizeRetainedStack,
        [bool]($hasCandidate -or $hasEnvelope))
    $modeCount = @($modes | Where-Object { $_ }).Count
    if ($modeCount -ne 1) {
        throw "Choose exactly one campaign mode: validation, reference initialization, baseline, hotspot discovery, envelope preparation, retained finalization, or one candidate."
    }
}

Assert-CampaignModeContract
Initialize-CampaignRepositoryRoot
$manifest = Read-CampaignManifest
Assert-CampaignPathTopology $manifest
$beautyTarget = Assert-AdvisoryBeautyTarget $manifest
$protectedFingerprints = Get-ProtectedFingerprints $manifest
$script:ProtectedFingerprints = $protectedFingerprints

$candidateDefinition = if (-not [string]::IsNullOrWhiteSpace($CandidateId)) {
    Get-ReviewedCandidate $manifest $CandidateId
} else { $null }
$candidateDecisionIdentity = if ($null -ne $candidateDefinition) {
    New-ReviewedCandidateDecisionIdentity $candidateDefinition
} else { $null }
$targetHypothesis = if ($null -ne $candidateDefinition) {
    Get-TargetHypothesis $manifest ([string]$candidateDefinition.hypothesisId)
} else { $null }
$screenWinWorkloads = if ($null -ne $targetHypothesis) {
    @(Get-HypothesisWorkloads $manifest @($targetHypothesis))
} else { @() }

if ($ValidateOnly) {
    Write-Host "Campaign manifest valid: $script:ManifestFile"
    Write-Host "Workloads: $(@($manifest.workloads).Count); qualification: $(@($manifest.workloads | Where-Object { [bool]$_.qualification }).Count)"
    Write-Host "Beauty target: advisory $($beautyTarget.Width)x$($beautyTarget.Height) $($beautyTarget.MediaType) sha256=$($beautyTarget.ImageSha256)"
    exit 0
}
if ($InitializeReferences) {
    $script:CookedAssetBundle = Resolve-CookedAssetBundle `
        $manifest $CookedAssetRoot "Reference initialization"
}
New-Item -ItemType Directory -Force -Path $script:RunRoot | Out-Null
if ($InitializeReferences) {
    Initialize-CampaignManifestSnapshot
} else {
    $script:CampaignManifestSnapshotPath = [System.IO.Path]::GetFullPath(
        (Join-Path $script:RunRoot "campaign.manifest.snapshot.json"))
    Assert-CampaignManifestIntegrity
}

$lock = $null
if ($InitializeReferences) {
    $lock = Initialize-CampaignReferences $manifest $beautyTarget $protectedFingerprints
    Write-Host "Campaign references initialized: $(Join-Path $script:RunRoot 'campaign.lock.json')"
    exit 0
} else {
    $lock = Read-CampaignLock $manifest $beautyTarget
}
Assert-LockBaselineAncestor $lock

if ($PrepareCandidateEnvelope) {
    Assert-CleanCampaignWorktree
    $acceptedHead = Get-GitText @("rev-parse", "HEAD")
    $prepared = New-AutomaticCandidateEnvelope `
        $manifest $lock $DiscoveryArtifactPath $AutomaticCandidateId `
        $AutomaticCandidateSourceCommit $AutomaticCandidateFocusedTestFilter `
        $CandidateEnvelopeOutputPath $acceptedHead
    Write-Host ($prepared | ConvertTo-Json -Depth 12 -Compress)
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($CandidateEnvelopePath)) {
    $envelopeAdmission = Read-CandidateEnvelope `
        $manifest $lock $CandidateEnvelopePath `
        (Get-GitText @("rev-parse", "HEAD")) $true
    $candidateDefinition = $envelopeAdmission.Candidate
    $candidateDecisionIdentity = $envelopeAdmission.DecisionIdentity
    $targetHypothesis = $envelopeAdmission.Hypothesis
    $screenWinWorkloads = @(
        Get-HypothesisWorkloads $manifest @($targetHypothesis))
}

if ($BaselineOnly -or $DiscoverHotspots) {
    Assert-CleanCampaignWorktree
    $summaries = @()
    $captureEntries = [System.Collections.Generic.List[object]]::new()
    $baselineCommit = Get-GitText @("rev-parse", "HEAD")
    $modeDirectory = if ($DiscoverHotspots) {
        "hotspot-discovery"
    } else { "baseline-only" }
    $baselineRoot = Join-Path $script:RunRoot "$modeDirectory/$baselineCommit"
    if (Test-Path -LiteralPath $baselineRoot) {
        throw "Baseline-only evidence already exists for $baselineCommit."
    }
    foreach ($configuration in @(Get-CampaignConfigurations $manifest)) {
        $verifierBuild = Get-ReferenceBuildIdentity $lock $configuration
        $build = Invoke-BuildOutput `
            $manifest $configuration `
            (Join-Path $baselineRoot "build/$configuration") `
            "Baseline-only $configuration build" $baselineCommit
        foreach ($workload in @($manifest.workloads)) {
            $entry = Get-ReferenceLockEntry `
                $lock $configuration ([string]$workload.id)
            $reportPath = Join-Path $baselineRoot (
                "captures/{0}/{1}.json" -f
                    $configuration, [string]$workload.id)
            $pairId = "$($manifest.campaignId)-$configuration-$($workload.id)-baseline-only"
            $report = Invoke-BenchmarkCapture `
                -Manifest $manifest `
                -Workload $workload `
                -BuildIdentity $build `
                -VerifierBuildIdentity $verifierBuild `
                -Configuration $configuration `
                -ReportPath $reportPath `
                -PairId $pairId `
                -QualityContractPath ([string]$entry.qualityContractPath) `
                -ReferencePath ([string]$entry.path) `
                -ReferenceInitialization $false `
                -ExpectedCommit $baselineCommit `
                -ExpectedQualityContractSha256 ([string]$entry.qualityContractSha256) `
                -ExpectedReferenceSha256 ([string]$entry.sha256) `
                -ReferenceIdentity $entry `
                -Label "Baseline-only $configuration/$($workload.id)"
            $summaries += [pscustomobject]@{
                configuration = $configuration
                workload = [string]$workload.id
                cpuP95Milliseconds = [double]$report.CpuFrameMilliseconds.P95Milliseconds
                cpuP99Milliseconds = [double]$report.CpuFrameMilliseconds.P99Milliseconds
                gpuP95Milliseconds = [double]$report.GpuFrameMilliseconds.P95Milliseconds
                gpuP99Milliseconds = [double]$report.GpuFrameMilliseconds.P99Milliseconds
                relativeRmse = [double]$report.HdrDifference.RelativeRmse
                flipP95 = [double]$report.HdrDifference.FlipP95
                frozenVerifierEvidence =
                    $report.CampaignFrozenVerifierEvidence
            }
            $captureEntries.Add([pscustomobject]@{
                Configuration = $configuration
                Workload = $workload
                ReportPath = [System.IO.Path]::GetFullPath($reportPath)
                ReportSha256 = Get-Sha256 $reportPath
                Report = $report
                BuildIdentity = $build
            })
            Assert-ProtectedFingerprints $protectedFingerprints
        }
    }
    Write-JsonArtifact (Join-Path $baselineRoot "summary.json") $summaries
    if ($DiscoverHotspots) {
        $hotspots = New-HotspotDiscoveryData `
            $manifest @($captureEntries) $baselineCommit `
            ([DateTimeOffset]::UtcNow)
        $hotspotPath = Join-Path $baselineRoot "hotspots.json"
        Write-JsonArtifact $hotspotPath $hotspots
        $nextHotspot = @($hotspots.eligibleHotspots | Where-Object {
            -not (Test-TimingAttemptReserved `
                $manifest ([string]$_.domain) ([string]$_.name))
        } | Select-Object -First 1)
        if ($nextHotspot.Count -eq 0) {
            Write-Host "No eligible authenticated CPU/GPU hotspot remains."
        } else {
            Write-Host (
                "Recommended hotspot: {0}::{1} p95={2:N3} ms share={3:N2}%" -f
                    [string]$nextHotspot[0].domain,
                    [string]$nextHotspot[0].name,
                    [double]$nextHotspot[0].maximumP95Milliseconds,
                    [double]$nextHotspot[0].maximumSharePercent)
        }
        Write-Host "Hotspot discovery artifact: $hotspotPath"
    }
    exit 0
}

if ($FinalizeRetainedStack) {
    Assert-CampaignRepositoryRoot
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $protectedFingerprints
    Assert-LockBaselineAncestor $lock
    $retainedHead = Get-GitText @("rev-parse", "HEAD")
    if ([string]::Equals(
            $retainedHead,
            [string]$lock.baselineCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "There is no retained candidate stack beyond the immutable baseline."
    }
    $finalRoot = Join-Path $script:RunRoot "finalizations/$retainedHead"
    if (Test-Path -LiteralPath $finalRoot) {
        throw "Final retained-stack evidence already exists for $retainedHead."
    }
    New-Item -ItemType Directory -Path $finalRoot | Out-Null
    $finalIteration = 0
    $configurationResults = @()
    $decision = "failed"
    $reason = "retained-stack final did not complete"
    $retainedChain = $null
    $winWorkloads = @()
    $finalAcceptanceRefSnapshot = $null
    $finalCandidateBuilds = [ordered]@{}
    try {
        $finalIteration = Get-NextCampaignIterationId
        $finalAcceptanceRefSnapshot = Get-AcceptanceRefSnapshot $manifest
        $retainedChain = Assert-RetainedAcceptanceChain `
            $manifest $lock $retainedHead
        $authenticatedHypotheses = @($retainedChain.Hypotheses)
        if ($authenticatedHypotheses.Count -eq 0) {
            throw "Retained stack contains no authenticated target hypotheses."
        }
        $winWorkloads = @(Get-HypothesisWorkloads `
            $manifest $authenticatedHypotheses)
        foreach ($configuration in @(Get-CampaignConfigurations $manifest)) {
            $baselineBuild = Get-ReferenceBuildIdentity $lock $configuration
            Assert-BuildIdentity $baselineBuild "Final $configuration baseline"
            $candidateBuild = Invoke-BuildOutput `
                $manifest $configuration `
                (Join-Path $finalRoot "build-candidate/$configuration") `
                "Final retained-stack $configuration candidate build" `
                $retainedHead
            $finalCandidateBuilds[$configuration] = $candidateBuild
            $result = Invoke-ConfigurationMatrix `
                $manifest $lock $winWorkloads $baselineBuild $candidateBuild `
                $configuration $finalIteration "retained-stack-final" `
                ([string]$lock.baselineCommit) $retainedHead $finalRoot $true
            $configurationResults += $result
            if ($result.decision -ne "keep") {
                $reason = "$configuration retained-stack final rejected: $($result.reason)"
                break
            }
        }
        # Release and ShippingPerformance timing both finish before any
        # timing-ineligible quality readback process is launched.
        if ($configurationResults.Count -eq
                @(Get-CampaignConfigurations $manifest).Count -and
            @($configurationResults | Where-Object {
                $_.decision -ne "keep"
            }).Count -eq 0) {
            $finalConfigurations = @(Get-CampaignConfigurations $manifest)
            for ($configurationIndex = 0;
                 $configurationIndex -lt $finalConfigurations.Count;
                 $configurationIndex++) {
                $configuration = [string]$finalConfigurations[$configurationIndex]
                $configurationResults[$configurationIndex] =
                    Complete-ConfigurationQualityMatrix `
                        $manifest $lock $winWorkloads `
                        $finalCandidateBuilds[$configuration] `
                        $configuration $finalIteration "retained-stack-final" `
                        $retainedHead $configurationResults[$configurationIndex] `
                        $finalRoot $true
            }
            # Re-read every earlier configuration only after the last quality
            # process has exited, so Shipping cannot invalidate Release evidence.
            for ($configurationIndex = 0;
                 $configurationIndex -lt $finalConfigurations.Count;
                 $configurationIndex++) {
                $configuration = [string]$finalConfigurations[$configurationIndex]
                $baselineBuild = Get-ReferenceBuildIdentity $lock $configuration
                Assert-ConfigurationTimingEvidence `
                    $manifest $lock $winWorkloads $baselineBuild `
                    $finalCandidateBuilds[$configuration] `
                    $configuration $finalIteration "retained-stack-final" `
                    ([string]$lock.baselineCommit) $retainedHead `
                    $configurationResults[$configurationIndex] `
                    $finalRoot $true "Retained-stack final timing audit"
                Assert-ConfigurationQualityEvidence `
                    $manifest $lock $winWorkloads `
                    $finalCandidateBuilds[$configuration] `
                    $configuration $finalIteration "retained-stack-final" `
                    $retainedHead $configurationResults[$configurationIndex] `
                    $finalRoot $true "Retained-stack final quality audit"
            }
        }
        if ($configurationResults.Count -eq
                @(Get-CampaignConfigurations $manifest).Count -and
            @($configurationResults | Where-Object {
                $_.decision -ne "keep"
            }).Count -eq 0) {
            Assert-ExactCampaignHead $retainedHead "Retained-stack final"
            Assert-CleanCampaignWorktree
            Assert-ProtectedFingerprints $protectedFingerprints
            Assert-CampaignLockIntegrity
            Assert-AcceptanceRefSnapshot `
                $manifest $finalAcceptanceRefSnapshot "Retained-stack final"
            $postFinalChain = Assert-RetainedAcceptanceChain `
                $manifest $lock $retainedHead
            if ([string]$postFinalChain.LastEvidence -ne
                [string]$retainedChain.LastEvidence) {
                throw "Final capture phase changed the authenticated accepted chain."
            }
            $decision = "keep"
            $reason = "retained stack passed locked-baseline Release and ShippingPerformance matrix"
        }
    } catch {
        $reason = $_.Exception.Message
    }
    $finalRecoveryAttempted = $decision -ne "keep"
    $finalRecoverySucceeded = -not $finalRecoveryAttempted
    if ($finalRecoveryAttempted -and
        $null -ne $finalAcceptanceRefSnapshot) {
        try {
            Restore-AcceptedHead `
                $manifest $retainedHead $finalIteration $false `
                $protectedFingerprints $finalAcceptanceRefSnapshot
            $finalRecoverySucceeded = $true
        } catch {
            $reason += "; retained-head recovery failed: $($_.Exception.Message)"
        }
    }
    $initialInvariantFailures = @(Get-FinalInvariantFailures `
        $manifest $lock $retainedHead $protectedFingerprints `
        $finalAcceptanceRefSnapshot $retainedChain)
    $finalInvariantFailures = @($initialInvariantFailures)
    if ($initialInvariantFailures.Count -ne 0) {
        $decision = "failed"
        $reason += "; initial post-attempt invariant failure: " +
            ($initialInvariantFailures -join "; ")
        if (-not $finalRecoveryAttempted -and
            $null -ne $finalAcceptanceRefSnapshot) {
            $finalRecoveryAttempted = $true
            try {
                Restore-AcceptedHead `
                    $manifest $retainedHead $finalIteration $false `
                    $protectedFingerprints $finalAcceptanceRefSnapshot
                $finalRecoverySucceeded = $true
            } catch {
                $finalRecoverySucceeded = $false
                $reason += "; retained-head recovery after audit failed: $($_.Exception.Message)"
            }
            $finalInvariantFailures = @(Get-FinalInvariantFailures `
                $manifest $lock $retainedHead $protectedFingerprints `
                $finalAcceptanceRefSnapshot $retainedChain)
        } elseif (-not $finalRecoveryAttempted) {
            $finalRecoveryAttempted = $true
            $finalRecoverySucceeded = $false
            $reason += "; retained-head recovery could not start without an admitted acceptance-ref snapshot"
        }
    }
    if ($finalInvariantFailures.Count -ne 0) {
        $decision = "failed"
        $reason += "; final post-recovery invariant failure: " +
            ($finalInvariantFailures -join "; ")
    }
    $observedHead = "unavailable"
    try { $observedHead = Get-GitText @("rev-parse", "HEAD") } catch { }
    $headPreserved = $finalInvariantFailures.Count -eq 0 -and
        [string]::Equals(
            $observedHead,
            $retainedHead,
            [StringComparison]::OrdinalIgnoreCase)
    $summary = [pscustomobject]@{
        schema = "njulf-perf-campaign-final/v2"
        campaignId = [string]$manifest.campaignId
        manifestSha256 = Get-AdmittedCampaignManifestSha256
        manifestSnapshotPath = $script:CampaignManifestSnapshotPath
        manifestSnapshotSha256 = $script:CampaignManifestSha256
        lockSha256 = $script:CampaignLockSha256
        mode = "FinalizeRetainedStack"
        baselineCommit = [string]$lock.baselineCommit
        retainedHead = $retainedHead
        observedHeadAtDecision = $observedHead
        headPreserved = $headPreserved
        recoveryAttempted = $finalRecoveryAttempted
        recoverySucceeded = $finalRecoverySucceeded
        initialPostAttemptInvariantFailures = @($initialInvariantFailures)
        postAttemptInvariantFailures = @($finalInvariantFailures)
        authenticatedCommits = if ($null -eq $retainedChain) {
            @()
        } else {
            @($retainedChain.Commits)
        }
        lastAcceptanceEvidence = if ($null -eq $retainedChain) {
            "unavailable"
        } else {
            [string]$retainedChain.LastEvidence
        }
        decision = $decision
        reason = $reason
        targetHypotheses = if ($null -eq $retainedChain) {
            @()
        } else {
            @($retainedChain.Hypotheses)
        }
        winWorkloads = @($winWorkloads | ForEach-Object { [string]$_.id })
        candidateBuilds = $finalCandidateBuilds
        configurations = $configurationResults
    }
    $finalDecisionPath = Join-Path $finalRoot "decision.json"
    if ($decision -eq "keep") {
        try {
            Write-JsonArtifact $finalDecisionPath $summary
            $finalDecisionSha256 = Assert-FinalDecisionArtifacts `
                $manifest $lock $finalDecisionPath $summary $winWorkloads `
                $finalCandidateBuilds $finalIteration $retainedHead $finalRoot `
                $finalAcceptanceRefSnapshot $retainedChain
            Write-JsonArtifact `
                (Join-Path $finalRoot "decision.audit.json") `
                ([ordered]@{
                    schema = "njulf-perf-campaign-final-audit/v1"
                    campaignId = [string]$manifest.campaignId
                    retainedHead = $retainedHead
                    decisionSha256 = $finalDecisionSha256
                    lastAcceptanceEvidence = [string]$retainedChain.LastEvidence
                    configurations = @(Get-CampaignConfigurations $manifest)
                    status = "passed"
                })
        } catch {
            $postPublicationFailure = $_.Exception.Message
            $recoveryFailure = ""
            try {
                Restore-AcceptedHead `
                    $manifest $retainedHead $finalIteration $false `
                    $protectedFingerprints $finalAcceptanceRefSnapshot
            } catch {
                $recoveryFailure = $_.Exception.Message
            }
            $postRecoveryInvariantFailures = @(Get-FinalInvariantFailures `
                $manifest $lock $retainedHead $protectedFingerprints `
                $finalAcceptanceRefSnapshot $retainedChain)
            Write-JsonArtifact `
                (Join-Path $finalRoot "decision.post-publication-failure.json") `
                ([ordered]@{
                    schema = "njulf-perf-campaign-final-post-publication-failure/v1"
                    campaignId = [string]$manifest.campaignId
                    retainedHead = $retainedHead
                    decisionPath = [System.IO.Path]::GetFullPath($finalDecisionPath)
                    decisionSha256 = if (Test-Path -LiteralPath $finalDecisionPath -PathType Leaf) {
                        Get-Sha256 $finalDecisionPath
                    } else {
                        "unavailable"
                    }
                    failure = $postPublicationFailure
                    recoveryFailure = $recoveryFailure
                    recoverySucceeded =
                        [string]::IsNullOrEmpty($recoveryFailure) -and
                        $postRecoveryInvariantFailures.Count -eq 0
                    postRecoveryInvariantFailures =
                        @($postRecoveryInvariantFailures)
                    status = "failed"
                })
            Write-Error (
                "Retained-stack final post-publication audit failed at " +
                "${retainedHead}: $postPublicationFailure")
            exit 1
        }
    } else {
        Write-JsonArtifact $finalDecisionPath $summary
    }
    Write-Host "$($decision.ToUpperInvariant()): $reason"
    if ($decision -ne "keep") {
        $preservationText = if ($headPreserved) {
            "HEAD and campaign invariants were preserved for a focused revert."
        } else {
            "HEAD/campaign preservation could not be proven; inspect the failed-final artifact before any recovery."
        }
        Write-Error "Retained-stack final failed at $retainedHead; $preservationText $reason"
        exit 1
    }
    exit 0
}

if ($null -eq $candidateDefinition -and
    [string]::IsNullOrWhiteSpace($CandidateEnvelopePath)) {
    throw "Candidate evaluation requires an admitted reviewed candidate or frozen candidate envelope."
}
$campaignSummaries = @()
$firstIterationId = 0
$lastIterationId = 0
for ($sequenceIndex = 1; $sequenceIndex -le $Iterations; $sequenceIndex++) {
    $iteration = Get-NextCampaignIterationId
    if ($firstIterationId -eq 0) { $firstIterationId = $iteration }
    $lastIterationId = $iteration
    Assert-CampaignRepositoryRoot
    Assert-CleanCampaignWorktree
    Assert-ProtectedFingerprints $protectedFingerprints
    Assert-LockBaselineAncestor $lock
    $acceptedHead = Get-GitText @("rev-parse", "HEAD")
    $acceptedChain = Assert-RetainedAcceptanceChain `
        $manifest $lock $acceptedHead
    $acceptanceRefSnapshot = Get-AcceptanceRefSnapshot $manifest
    $iterationRoot = Join-Path $script:RunRoot (
        "iterations/{0:D6}" -f $iteration)
    $baselineBuilds = [ordered]@{}
    foreach ($configuration in @(Get-CampaignConfigurations $manifest)) {
        $baselineBuilds[$configuration] = Invoke-BuildOutput `
            $manifest $configuration `
            (Join-Path $iterationRoot "build-baseline/$configuration") `
            "Iteration $iteration $configuration baseline build" $acceptedHead
        Assert-ExactCampaignHead `
            $acceptedHead "Iteration $iteration $configuration baseline build"
        Assert-CleanCampaignWorktree
        Assert-ProtectedFingerprints $protectedFingerprints
        Assert-CampaignLockIntegrity
        Assert-AcceptanceRefSnapshot `
            $manifest $acceptanceRefSnapshot `
            "Iteration $iteration $configuration baseline build"
    }
    $decision = "rollback"
    $reason = "trial did not complete"
    $configurationResults = @()
    $candidateHead = ""
    $candidateBuilds = [ordered]@{}
    $attemptEvidence = $null
    try {
        Assert-CampaignLockIntegrity
        if ($null -eq $candidateDefinition -or
            $null -eq $candidateDecisionIdentity -or
            $null -eq $targetHypothesis) {
            throw "Candidate identity was not admitted."
        }
        $attemptEvidence = Reserve-TimingAttempt `
            $manifest ([string]$targetHypothesis.targetDomain) `
            ([string]$targetHypothesis.targetPass) `
            ([string]$candidateDefinition.id) $acceptedHead
        $candidateHead = Invoke-PinnedCandidate `
            $manifest $candidateDefinition $acceptedHead
        Assert-AcceptanceRefSnapshot `
            $manifest $acceptanceRefSnapshot "Pinned candidate application"
        $postTrialChain = Assert-RetainedAcceptanceChain `
            $manifest $lock $acceptedHead
        if ([string]$postTrialChain.LastEvidence -ne
            [string]$acceptedChain.LastEvidence) {
            throw "Candidate application changed the authenticated accepted chain."
        }
        Assert-CampaignLockIntegrity
        Assert-CampaignWorktreeRoot
        $candidateHead = Get-GitText @("rev-parse", "HEAD")
        Assert-CampaignRepositoryRoot
        Assert-ProtectedFingerprints $protectedFingerprints
        Assert-CleanCampaignWorktree
        if ($candidateHead -eq $acceptedHead) {
            throw "Candidate application must create one focused candidate commit."
        }
        $candidateCount = [int](Get-GitText @(
            "rev-list", "--count", "$acceptedHead..$candidateHead"))
        $candidateParent = Get-GitText @("rev-parse", "$candidateHead^")
        $candidateParentFields = @(
            (Get-GitText @("rev-list", "--parents", "-n", "1", $candidateHead)) -split '\s+')
        if ($candidateCount -ne 1 -or
            $candidateParentFields.Count -ne 2 -or
            -not [string]::Equals(
                $candidateParent,
                $acceptedHead,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Candidate application must create exactly one non-merge commit directly on $acceptedHead."
        }
        Assert-LockBaselineAncestor $lock $candidateHead
        $configurations = @(Get-CampaignConfigurations $manifest)
        foreach ($configuration in $configurations) {
            $focusedFilter = if ($configuration -ceq "Release") {
                [string]$candidateDefinition.focusedTestFilter
            } else { "" }
            $candidateBuilds[$configuration] = Invoke-BuildOutput `
                $manifest $configuration `
                (Join-Path $iterationRoot "build-candidate/$configuration") `
                "Iteration $iteration $configuration candidate build" `
                $candidateHead $focusedFilter
        }

        # Every configuration completes the full timing matrix before the first
        # quality capture. This prevents a quality workload from warming or
        # otherwise perturbing the second configuration's timing evidence.
        foreach ($configuration in $configurations) {
            $result = Invoke-ConfigurationMatrix `
                $manifest $lock $screenWinWorkloads `
                $baselineBuilds[$configuration] $candidateBuilds[$configuration] `
                $configuration $iteration "hypothesis-screen" `
                $acceptedHead $candidateHead "" $true
            $configurationResults += $result
            if ($result.decision -ne "keep") {
                $reason = "$configuration hypothesis rejected: $($result.reason)"
                break
            }
            Assert-ConfigurationTimingEvidence `
                $manifest $lock $screenWinWorkloads `
                $baselineBuilds[$configuration] $candidateBuilds[$configuration] `
                $configuration $iteration "hypothesis-screen" `
                $acceptedHead $candidateHead $result "" $true `
                "$configuration full-matrix timing audit"
        }

        if ($configurationResults.Count -eq $configurations.Count -and
            @($configurationResults | Where-Object {
                [string]$_.decision -cne "keep"
            }).Count -eq 0) {
            for ($configurationIndex = 0;
                 $configurationIndex -lt $configurations.Count;
                 $configurationIndex++) {
                $configuration = [string]$configurations[$configurationIndex]
                $result = Complete-ConfigurationQualityMatrix `
                    $manifest $lock $screenWinWorkloads `
                    $candidateBuilds[$configuration] $configuration $iteration `
                    "hypothesis-screen" $candidateHead `
                    $configurationResults[$configurationIndex] "" $true
                $configurationResults[$configurationIndex] = $result
                Assert-ConfigurationTimingEvidence `
                    $manifest $lock $screenWinWorkloads `
                    $baselineBuilds[$configuration] $candidateBuilds[$configuration] `
                    $configuration $iteration "hypothesis-screen" `
                    $acceptedHead $candidateHead $result "" $true `
                    "$configuration post-quality timing audit"
                Assert-ConfigurationQualityEvidence `
                    $manifest $lock $screenWinWorkloads `
                    $candidateBuilds[$configuration] $configuration $iteration `
                    "hypothesis-screen" $candidateHead $result "" $true `
                    "$configuration full-matrix quality audit"
            }
            Assert-ExactCampaignHead $candidateHead "Candidate full-matrix screen"
            Assert-CampaignLockIntegrity
            Assert-CleanCampaignWorktree
            Assert-ProtectedFingerprints $protectedFingerprints
            Assert-AcceptanceRefSnapshot `
                $manifest $acceptanceRefSnapshot "Candidate full-matrix screen"
            $postCaptureChain = Assert-RetainedAcceptanceChain `
                $manifest $lock $acceptedHead
            if ([string]$postCaptureChain.LastEvidence -ne
                [string]$acceptedChain.LastEvidence) {
                throw "Capture phase changed the authenticated accepted chain."
            }
            $decision = "keep"
            $reason = "Both timing configurations and the full Bistro/Sponza quality/non-regression matrix passed"
        }
    } catch {
        $reason = $_.Exception.Message
    }

    if ($decision -ne "keep" -and $RollbackRejected) {
        Restore-AcceptedHead `
            $manifest $acceptedHead $iteration $KeepRejectedCommits `
            $protectedFingerprints $acceptanceRefSnapshot
        Assert-ExactCampaignHead $acceptedHead "Rejected hypothesis rollback"
    } elseif ($decision -ne "keep") {
        Restore-AcceptanceRefSnapshot $manifest $acceptanceRefSnapshot
    }
    $observedHead = "unavailable"
    try { $observedHead = Get-GitText @("rev-parse", "HEAD") } catch { }
    $decisionPath = Join-Path $iterationRoot "decision.json"
    $summary = [pscustomobject]@{
        schema = "njulf-perf-campaign-decision/v3"
        campaignId = [string]$manifest.campaignId
        manifestSha256 = Get-AdmittedCampaignManifestSha256
        manifestSnapshotPath = $script:CampaignManifestSnapshotPath
        manifestSnapshotSha256 = $script:CampaignManifestSha256
        lockSha256 = $script:CampaignLockSha256
        iteration = $iteration
        acceptedHead = $acceptedHead
        candidateHead = $candidateHead
        observedHeadAtDecision = $observedHead
        previousAcceptanceEvidence = [string]$acceptedChain.LastEvidence
        decisionArtifactPath = [System.IO.Path]::GetFullPath($decisionPath)
        decision = $decision
        reason = $reason
        candidate = $candidateDecisionIdentity
        attempt = $attemptEvidence
        targetHypothesisId = [string]$targetHypothesis.id
        targetDomain = [string]$targetHypothesis.targetDomain
        targetPass = [string]$targetHypothesis.targetPass
        targetClaims = @($targetHypothesis.claims)
        baselineBuilds = $baselineBuilds
        candidateBuilds = $candidateBuilds
        configurations = $configurationResults
    }
    $campaignSummaries += $summary
    try {
        if ($decision -eq "keep") {
            Assert-ExactCampaignHead $candidateHead "Acceptance publication"
            Assert-AcceptanceRefSnapshot `
                $manifest $acceptanceRefSnapshot "Acceptance publication"
            $validatedDecisionBlob = `
                Write-ValidatedAcceptanceDecisionArtifact `
                    $manifest $lock $decisionPath $summary `
                    $acceptedHead $candidateHead `
                    ([string]$acceptedChain.LastEvidence)
            $acceptanceBlob = Publish-AcceptanceEvidence `
                $manifest $decisionPath $candidateHead `
                $validatedDecisionBlob $acceptanceRefSnapshot
            $postPublishSnapshot = [ordered]@{}
            foreach ($entry in $acceptanceRefSnapshot.GetEnumerator()) {
                $postPublishSnapshot[[string]$entry.Key] =
                    [string]$entry.Value
            }
            $postPublishSnapshot[(
                Get-AcceptanceRefName $manifest $candidateHead)] =
                $acceptanceBlob
            Assert-ExactCampaignHead $candidateHead "Acceptance publication"
            Assert-CleanCampaignWorktree
            Assert-ProtectedFingerprints $protectedFingerprints
            Assert-CampaignLockIntegrity
            Assert-AcceptanceRefSnapshot `
                $manifest $postPublishSnapshot "Acceptance publication"
            $publishedChain = Assert-RetainedAcceptanceChain `
                $manifest $lock $candidateHead
            if ([string]$publishedChain.LastEvidence -ne $acceptanceBlob) {
                throw "Published acceptance blob is not the authenticated retained-chain tip."
            }
            Write-Host "Authenticated acceptance: $(Get-AcceptanceRefName $manifest $candidateHead) -> $acceptanceBlob"
        } else {
            Write-JsonArtifact $decisionPath $summary
        }
    } catch {
        if ($decision -eq "keep") {
            if ($RollbackRejected) {
                Restore-AcceptedHead `
                    $manifest $acceptedHead $iteration $KeepRejectedCommits `
                    $protectedFingerprints $acceptanceRefSnapshot
            } else {
                Restore-AcceptanceRefSnapshot `
                    $manifest $acceptanceRefSnapshot
            }
        }
        throw
    }
    Write-Host "$($decision.ToUpperInvariant()): $reason"
    if ($decision -ne "keep" -and -not $RollbackRejected) {
        Write-Warning "Rejected candidate was preserved for inspection; stopping the sequence."
        break
    }
}
$invocationSummaryPath = Join-Path $script:RunRoot (
    "invocations/screen-{0:D6}-{1:D6}.json" -f
        $firstIterationId, $lastIterationId)
Write-JsonArtifact $invocationSummaryPath $campaignSummaries
Write-Host "Candidate evaluation complete: $invocationSummaryPath"
Write-Host "Run -FinalizeRetainedStack once after the retained candidate sequence."
