[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SdkRoot,

    [Parameter(Mandatory = $true)]
    [string] $BuildDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $Generator = 'Visual Studio 17 2022',
    [string] $GeneratorInstance = '',

    [ValidateRange(1, 2147483647)]
    [int] $MaximumInputBytes = 536870912,

    [ValidateRange(1, 2147483647)]
    [int] $MaximumOutputBytes = 536870912,

    [ValidateRange(1, 16777216)]
    [int] $MaximumPrimitiveCount = 16777216
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedCommit = '9abacd0f187d0efca491946a29ba7df8c5345264'
$expectedSdkVersion = '1.9.2'
$requiredSubmodules = @(
    'external/ShaderMake',
    'external/glm',
    'external/lz4',
    'external/stb',
    'external/xxHash'
)

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Process '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Resolve-ExistingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Container)) {
        throw "$Name must identify an existing directory."
    }
    return [IO.Path]::GetFullPath($resolved.Path)
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The supported pinned bridge artifact is Windows x64.'
}

$cmake = (Get-Command cmake -CommandType Application -ErrorAction Stop).Source
$git = (Get-Command git -CommandType Application -ErrorAction Stop).Source
$sdk = Resolve-ExistingDirectory -Path $SdkRoot -Name 'SdkRoot'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
$build = [IO.Path]::GetFullPath($BuildDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)

$actualCommit = (& $git -C $sdk rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $expectedCommit) {
    throw "OMM SDK commit '$actualCommit' is not the reviewed commit '$expectedCommit'."
}

$trackedStatus = @(& $git -C $sdk status --porcelain=v1 --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $trackedStatus.Count -ne 0) {
    throw 'The OMM SDK checkout has tracked modifications.'
}

foreach ($submodule in $requiredSubmodules) {
    $status = @(& $git -C $sdk submodule status -- $submodule)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 1 -or
        -not $status[0].StartsWith(' ', [StringComparison]::Ordinal)) {
        throw "Required submodule '$submodule' is missing or differs from the reviewed gitlink."
    }

    $submoduleRoot = Join-Path $sdk $submodule
    $submoduleChanges = @(& $git -C $submoduleRoot status --porcelain=v1 --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $submoduleChanges.Count -ne 0) {
        throw "Required submodule '$submodule' has tracked modifications."
    }
}

$ommHeader = Join-Path $sdk 'libraries/omm-lib/include/omm.h'
$headerText = [IO.File]::ReadAllText($ommHeader)
$versionParts = foreach ($part in @('MAJOR', 'MINOR', 'BUILD')) {
    $match = [regex]::Match(
        $headerText,
        "(?m)^#define\s+OMM_VERSION_$part\s+([0-9]+)\s*$")
    if (-not $match.Success) {
        throw "Could not read OMM_VERSION_$part from '$ommHeader'."
    }
    $match.Groups[1].Value
}
$actualSdkVersion = $versionParts -join '.'
if ($actualSdkVersion -cne $expectedSdkVersion) {
    throw "OMM SDK version '$actualSdkVersion' is not '$expectedSdkVersion'."
}

[IO.Directory]::CreateDirectory($build) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null

$configureArguments = @(
    '-S', $source,
    '-B', $build,
    '-G', $Generator,
    '-A', 'x64',
    "-DNJULF_OMM_SDK_ROOT=$sdk"
)
if (-not [string]::IsNullOrWhiteSpace($GeneratorInstance)) {
    $configureArguments += "-DCMAKE_GENERATOR_INSTANCE=$GeneratorInstance"
}
Invoke-CheckedProcess -FilePath $cmake -ArgumentList $configureArguments
Invoke-CheckedProcess -FilePath $cmake -ArgumentList @(
    '--build', $build,
    '--config', 'Release',
    '--target', 'njulf_omm_bridge',
    '--parallel'
)

$compilerFiles = @(Get-ChildItem -LiteralPath (Join-Path $build 'CMakeFiles') `
    -Recurse -File -Filter 'CMakeCXXCompiler.cmake')
if ($compilerFiles.Count -ne 1) {
    throw "Expected one CMakeCXXCompiler.cmake, found $($compilerFiles.Count)."
}
$compilerText = [IO.File]::ReadAllText($compilerFiles[0].FullName)
$compilerIdMatch = [regex]::Match(
    $compilerText,
    '(?m)^set\(CMAKE_CXX_COMPILER_ID "([^"]+)"\)')
$compilerVersionMatch = [regex]::Match(
    $compilerText,
    '(?m)^set\(CMAKE_CXX_COMPILER_VERSION "([^"]+)"\)')
$compilerPathMatch = [regex]::Match(
    $compilerText,
    '(?m)^set\(CMAKE_CXX_COMPILER "([^"]+)"\)')
if (-not $compilerIdMatch.Success -or -not $compilerVersionMatch.Success -or
    -not $compilerPathMatch.Success) {
    throw 'CMake did not record a complete C++ compiler identity.'
}
$compilerIdentity = '{0}-{1};generator={2};architecture=x64' -f `
    $compilerIdMatch.Groups[1].Value,
    $compilerVersionMatch.Groups[1].Value,
    $Generator

$builtLibrary = Join-Path $build 'Release/njulf_omm_bridge.dll'
if (-not (Test-Path -LiteralPath $builtLibrary -PathType Leaf)) {
    throw "The expected bridge binary was not produced at '$builtLibrary'."
}

$compilerDirectory = Split-Path -Parent $compilerPathMatch.Groups[1].Value
$dumpbin = Join-Path $compilerDirectory 'dumpbin.exe'
if (-not (Test-Path -LiteralPath $dumpbin -PathType Leaf)) {
    throw "The selected MSVC toolchain has no dumpbin.exe beside its compiler."
}
$dependencyOutput = @(& $dumpbin /nologo /dependents $builtLibrary)
if ($LASTEXITCODE -ne 0) {
    throw 'dumpbin failed while validating bridge dependencies.'
}
$dependencies = @(
    $dependencyOutput |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^[A-Za-z0-9_.-]+\.dll$' }
)
$allowedDependencies = @(
    '^KERNEL32\.dll$',
    '^ADVAPI32\.dll$',
    '^MSVCP140\.dll$',
    '^VCRUNTIME140(?:_1)?\.dll$',
    '^api-ms-win-crt-[a-z0-9-]+-l1-1-0\.dll$'
)
foreach ($dependency in $dependencies) {
    if (-not ($allowedDependencies | Where-Object { $dependency -match $_ })) {
        throw "Bridge has an unexpected dynamic dependency: '$dependency'."
    }
}
if ($dependencies.Count -eq 0) {
    throw 'dumpbin reported no bridge dependencies; its output format was not recognized.'
}

$headerOutput = @(& $dumpbin /nologo /headers $builtLibrary)
if ($LASTEXITCODE -ne 0) {
    throw 'dumpbin failed while validating bridge PE headers.'
}
$headerText = $headerOutput -join "`n"
foreach ($requiredHeader in @(
    '(?im)machine \(x64\)',
    '(?im)^\s+Dynamic base\s*$',
    '(?im)^\s+NX compatible\s*$'
)) {
    if ($headerText -notmatch $requiredHeader) {
        throw "Bridge PE hardening validation failed for '$requiredHeader'."
    }
}
$loadConfigOutput = @(& $dumpbin /nologo /loadconfig $builtLibrary)
if ($LASTEXITCODE -ne 0) {
    throw 'dumpbin failed while validating the bridge load configuration.'
}
$loadConfigText = $loadConfigOutput -join "`n"
foreach ($requiredGuardCfProperty in @(
    '(?im)^\s+CF instrumented\s*$',
    '(?im)^\s+FID table present\s*$',
    '(?im)^\s+[1-9][0-9]* Guard CF function count\s*$'
)) {
    if ($loadConfigText -notmatch $requiredGuardCfProperty) {
        throw "Bridge Control Flow Guard validation failed for '$requiredGuardCfProperty'."
    }
}

$artifactLibrary = Join-Path $output 'njulf_omm_bridge.dll'
Copy-Item -LiteralPath $builtLibrary -Destination $artifactLibrary -Force
$binaryHash = (Get-FileHash -LiteralPath $artifactLibrary -Algorithm SHA256).Hash.ToLowerInvariant()

$buildFlags = @(
    'configuration=Release',
    'architecture=x64',
    'OMM_STATIC_LIBRARY=ON',
    'OMM_ENABLE_TESTS=OFF',
    'OMM_BUILD_VIEWER=OFF',
    'OMM_BUILD_OMM_GPU_NVRHI=OFF',
    'OMM_ENABLE_PRECOMPILED_SHADERS_DXIL=OFF',
    'OMM_ENABLE_PRECOMPILED_SHADERS_SPIRV=OFF',
    'OMM_ENABLE_OPENMP=OFF',
    'OMM_ENABLE_FAST_MATH=OFF',
    'OMM_LIB_INSTALL=OFF',
    'SHADERMAKE_FIND_COMPILERS=OFF'
) -join ';'

$manifest = [ordered]@{
    schemaVersion = 1
    bridgeAbi = 1
    sourceUri = 'https://github.com/NVIDIA-RTX/OMM'
    commitOrRelease = $actualCommit
    licenseIdentifier = 'LicenseRef-NVIDIA-RTX-SDKs-2023-01-23'
    buildFlags = $buildFlags
    compilerIdentity = $compilerIdentity
    binarySha256 = $binaryHash
    sdkVersion = $actualSdkVersion
    maximumInputBytes = $MaximumInputBytes
    maximumOutputBytes = $MaximumOutputBytes
    maximumPrimitiveCount = $MaximumPrimitiveCount
}
$manifestPath = Join-Path $output 'njulf_omm_bridge.provenance.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath `
    -Encoding utf8NoBOM

Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE.txt') `
    -Destination (Join-Path $output 'NVIDIA-RTX-SDKs-LICENSE.txt') -Force

$thirdPartyNoticePath = Join-Path $output 'njulf_omm_bridge.third-party-notices.txt'
$noticeSources = [ordered]@{
    'glm (MIT)' = Join-Path $sdk 'external/glm/copying.txt'
    'LZ4 (BSD-2-Clause)' = Join-Path $sdk 'external/lz4/LICENSE'
    'stb (MIT or public domain)' = Join-Path $sdk 'external/stb/LICENSE'
    'xxHash (BSD-2-Clause)' = Join-Path $sdk 'external/xxHash/LICENSE'
}
$noticeBuilder = [Text.StringBuilder]::new()
foreach ($entry in $noticeSources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required third-party license is missing: '$($entry.Value)'."
    }
    [void] $noticeBuilder.AppendLine(('=' * 78))
    [void] $noticeBuilder.AppendLine($entry.Key)
    [void] $noticeBuilder.AppendLine(('=' * 78))
    [void] $noticeBuilder.AppendLine([IO.File]::ReadAllText($entry.Value).TrimEnd())
    [void] $noticeBuilder.AppendLine()
}
[IO.File]::WriteAllText(
    $thirdPartyNoticePath,
    $noticeBuilder.ToString(),
    [Text.UTF8Encoding]::new($false))

$result = [ordered]@{
    library = $artifactLibrary
    manifest = $manifestPath
    sdkLicense = Join-Path $output 'NVIDIA-RTX-SDKs-LICENSE.txt'
    thirdPartyNotices = $thirdPartyNoticePath
    sha256 = $binaryHash
    compiler = $compilerIdentity
}
$result | ConvertTo-Json -Depth 3
