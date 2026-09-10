[CmdletBinding()]
param([switch] $RunGame)

$ErrorActionPreference = 'Stop'
$engineRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $engineRoot ('artifacts/game-template/' + [Guid]::NewGuid().ToString('N'))
$gameRoot = Join-Path $runRoot 'Game With Spaces'
New-Item -ItemType Directory -Force $runRoot | Out-Null
$savedEnvironment = @{}
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:TEMP = Join-Path $runRoot 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $runRoot 'cli'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$timings = [ordered]@{}

function Invoke-Dotnet([string] $Step, [string[]] $Arguments, [switch] $ExpectFailure) {
    $log = Join-Path $runRoot "$Step.log"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments *> $log
    $code = $LASTEXITCODE
    $timings[$Step] = $timer.Elapsed.TotalSeconds
    if (($ExpectFailure -and $code -eq 0) -or (!$ExpectFailure -and $code -ne 0)) {
        Get-Content -LiteralPath $log -Tail 50 | Write-Host
        throw "$Step returned $code; see $log"
    }
    Write-Host "$Step completed in $([Math]::Round($timer.Elapsed.TotalSeconds, 1))s"
    return [IO.File]::ReadAllText($log)
}

try {
    # A props boundary models a project outside this checkout without writing outside artifacts.
    Set-Content -LiteralPath (Join-Path $runRoot 'Directory.Build.props') -Value '<Project />'
    Invoke-Dotnet 'install' @('new', 'install', (Join-Path $engineRoot 'templates/Njulf.Game'), '--force') | Out-Null
    Invoke-Dotnet 'create' @('new', 'njulf-game', '-n', 'StarterSmoke', '-o', $gameRoot, '--engine-root', $engineRoot) | Out-Null
    $project = Join-Path $gameRoot 'StarterSmoke.csproj'
    $assets = Join-Path $gameRoot 'Assets'
    $source = Join-Path $assets 'tetrahedron.gltf'
    # A second independent model and an external buffer exercise dependency-only edits.
    Copy-Item -LiteralPath $source -Destination (Join-Path $assets 'unaffected.gltf')
    $model = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
    $buffer = Join-Path $assets 'tetrahedron.bin'
    [IO.File]::WriteAllBytes($buffer, [Convert]::FromBase64String(($model.buffers[0].uri -split ',')[1]))
    $model.buffers[0].uri = 'tetrahedron.bin'
    $model | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $source
    $sourceText = [IO.File]::ReadAllText($source)
    # Also exercise the existing application-shader integration and explicit runtime content.
    [IO.File]::WriteAllText((Join-Path $assets 'smoke.comp'), "#version 460`nlayout(local_size_x=1) in;`nvoid main() {}`n")
    Set-Content -LiteralPath (Join-Path $assets 'runtime.txt') -Value 'runtime content fixture'
    $xml = [IO.File]::ReadAllText($project).Replace('  <Import Project=', @'
  <ItemGroup>
    <NjulfEffectShader Include="Assets/smoke.comp" />
    <NjulfRuntimeContent Include="Assets/runtime.txt" />
  </ItemGroup>
  <Import Project=
'@)
    [IO.File]::WriteAllText($project, $xml)
    $build = @('build', $project, '-c', 'Development', '-v', 'minimal')
    Invoke-Dotnet 'first-build' $build | Out-Null
    $package = Join-Path $gameRoot 'Cooked/win-x64/models/tetrahedron.njmodel'
    $unchanged = Join-Path $gameRoot 'Cooked/win-x64/models/unaffected.njmodel'
    $output = Join-Path $gameRoot 'bin/Development/net10.0'
    foreach ($relative in @('Cooked/win-x64/models/tetrahedron.njmodel', 'Assets/smoke.comp.spv', 'Assets/runtime.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $output $relative))) { throw "First build omitted $relative" }
    }
    $stamp = (Get-Item -LiteralPath $package).LastWriteTimeUtc
    $otherStamp = (Get-Item -LiteralPath $unchanged).LastWriteTimeUtc
    $log = Invoke-Dotnet 'unchanged-build' $build
    if ($log -notmatch 'Cooked 0 asset\(s\); skipped 2 unchanged' -or (Get-Item -LiteralPath $package).LastWriteTimeUtc -ne $stamp) {
        throw 'An unchanged build recooked assets.'
    }
    $bytes = [IO.File]::ReadAllBytes($buffer)
    [BitConverter]::GetBytes([single]0.25).CopyTo($bytes, 0)
    [IO.File]::WriteAllBytes($buffer, $bytes)
    $log = Invoke-Dotnet 'dependency-build' $build
    if ($log -notmatch 'Cooked 1 asset\(s\); skipped 1 unchanged' -or (Get-Item -LiteralPath $unchanged).LastWriteTimeUtc -ne $otherStamp) {
        throw 'Dependency edit did not recook exactly its owning model.'
    }
    Remove-Item -LiteralPath $package
    $log = Invoke-Dotnet 'missing-output-build' $build
    if ($log -notmatch 'Cooked 1 asset\(s\); skipped 1 unchanged') { throw 'Missing output was not repaired.' }

    [IO.File]::WriteAllText($source, "{`n  !`n}")
    $log = Invoke-Dotnet 'bad-content' $build -ExpectFailure
    if ($log -notmatch 'tetrahedron.gltf\(2,3\): error NJASSET:') { throw 'Missing located content diagnostic.' }
    [IO.File]::WriteAllText($source, $sourceText)
    [IO.File]::WriteAllText((Join-Path $assets 'smoke.comp'), "#version 460`n#error deliberate failure`n")
    $log = Invoke-Dotnet 'bad-shader' $build -ExpectFailure
    if ($log -notmatch 'smoke.comp\(2(?:,\d+)?\): error NJSHADER:') { throw 'Missing located shader diagnostic.' }
    [IO.File]::WriteAllText((Join-Path $assets 'smoke.comp'), "#version 460`nlayout(local_size_x=1) in;`nvoid main() {}`n")

    $publish = Join-Path $runRoot 'publish'
    $publishArgs = @('publish', $project, '-c', 'Release', '--self-contained', 'true', '-o', $publish, '-v', 'minimal')
    Invoke-Dotnet 'publish' $publishArgs | Out-Null
    $bad = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
        $_.Extension -in @('.gltf', '.glb', '.fbx', '.comp', '.frag', '.glsl', '.cs') -or
        $_.Name -match '^(SharpGLTF|Silk.NET.Assimp|Assimp|BCnEncoder|Njulf.AssetTool|Njulf.Assets.Tooling|Njulf.ShaderBuild|Njulf.Editor)'
    }
    if ($bad) { throw "Authoring payload leaked into publish: $($bad.Name -join ', ')" }
    foreach ($relative in @('StarterSmoke.exe', 'coreclr.dll', 'Cooked/win-x64/models/tetrahedron.njmodel', 'Assets/smoke.comp.spv', 'Assets/runtime.txt', 'THIRD-PARTY-NOTICES.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $relative))) { throw "Publish omitted $relative" }
    }
    Remove-Item -LiteralPath (Join-Path $assets 'unaffected.gltf'), (Join-Path $assets 'runtime.txt')
    [IO.File]::WriteAllText($project, [IO.File]::ReadAllText($project).Replace('<NjulfRuntimeContent Include="Assets/runtime.txt" />', ''))
    Invoke-Dotnet 'removed-source-build' $build | Out-Null
    Invoke-Dotnet 'removed-source-publish' $publishArgs | Out-Null
    foreach ($destination in @($output, $publish)) {
        foreach ($relative in @('Cooked/win-x64/models/unaffected.njmodel', 'Assets/runtime.txt')) {
            if (Test-Path -LiteralPath (Join-Path $destination $relative)) { throw "Removed content survived: $destination/$relative" }
        }
    }

    if ($RunGame) {
        # Move only this script's generated authoring folder, within the verified run root.
        $hiddenAssets = Join-Path $gameRoot 'AuthoringHidden'
        if (!$assets.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar) -or !$hiddenAssets.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe authoring move.' }
        Move-Item -LiteralPath $assets -Destination $hiddenAssets
        try {
            foreach ($name in @('PATH', 'VULKAN_SDK', 'NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD')) {
                $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
            }
            $env:VULKAN_SDK = $null
            $env:PATH = ($env:PATH -split ';' | Where-Object { $_ -notmatch 'VulkanSDK' }) -join ';'
            $env:NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD = 'false'
            $runtimeEnvironment = @('NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY', 'NJULF_PIPELINE_BINARY_CACHE_DIRECTORY', 'NJULF_DDGI_WARM_CACHE_DIR')
            foreach ($name in $runtimeEnvironment) {
                $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
                [Environment]::SetEnvironmentVariable($name, (Join-Path $runRoot $name))
            }
            $process = Start-Process -FilePath (Join-Path $publish 'StarterSmoke.exe') -ArgumentList @('--frames', '30') -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'runtime.log') -RedirectStandardError (Join-Path $runRoot 'runtime-error.log')
            for ($attempt = 0; $attempt -lt 6 -and !$process.WaitForExit(30000); $attempt++) {
                Write-Host 'Waiting for the published game to finish its frames...'
            }
            if (!$process.HasExited) { $process.Kill($true); throw 'Published game timed out.' }
            if ($process.ExitCode -ne 0) { throw "Published game failed; see $runRoot/runtime-error.log" }
        }
        finally { Move-Item -LiteralPath $hiddenAssets -Destination $assets }
    }
    [ordered]@{ timingsSeconds = $timings; runtimeTested = [bool]$RunGame; result = 'passed' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json')
    Write-Host "Game template smoke passed. Evidence: $runRoot"
}
catch {
    [ordered]@{ timingsSeconds = $timings; result = 'failed'; error = $_.Exception.Message } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json')
    throw
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
}
