[CmdletBinding()]
param([string]$Configuration = 'Development')

# Run after building the solution. Local packages are never published.
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = Join-Path $workspace ('artifacts/framework-contracts/packages-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$feed = Join-Path $run 'feed'
$cache = Join-Path $run 'cache'
$version = '0.0.0-local.' + (Get-Date -Format 'yyyyMMddHHmmss')
$projects = @('Core', 'Graphics', 'Input', 'Assets', 'Shaders', 'Rendering', 'Framework', 'Assets.Tooling', 'Editor')
New-Item -ItemType Directory -Force -Path $feed, $cache | Out-Null
$oldHttp = $env:NUGET_HTTP_CACHE_PATH
$oldScratch = $env:NUGET_SCRATCH
try {
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $run 'http'
    $env:NUGET_SCRATCH = Join-Path $run 'scratch'
    foreach ($name in $projects) {
        dotnet pack (Join-Path $workspace "Njulf.$name/Njulf.$name.csproj") -c $Configuration --no-build --no-restore -o $feed "-p:PackageVersion=$version"
        if ($LASTEXITCODE) { throw "Packing $name failed." }
        $package = Join-Path $feed "Njulf.$name.$version.nupkg"
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            foreach ($extension in @('dll', 'xml')) {
                if (!$zip.GetEntry("lib/net10.0/Njulf.$name.$extension")) { throw "Package $name lacks $extension." }
            }
            $reader = [IO.StreamReader]::new($zip.GetEntry("lib/net10.0/Njulf.$name.xml").Open())
            try {
                [xml]$documentation = $reader.ReadToEnd()
                if (@($documentation.doc.members.member).Count -eq 0) { throw "Package $name has empty IntelliSense documentation." }
            } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
    }

    # The existing package cache is a read-only fallback. All newly restored data stays on D:.
    $fallback = (dotnet nuget locals global-packages --list) -replace '^global-packages: ', ''
    $escape = { param($value) [Security.SecurityElement]::Escape($value) }
    @"
<configuration>
  <packageSources><clear/><add key="local" value="$(& $escape $feed)"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <fallbackPackageFolders><clear/><add key="existing" value="$(& $escape $fallback.Trim())"/></fallbackPackageFolders>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $run 'NuGet.Config')

    foreach ($kind in @('Graphics', 'Framework')) {
        $consumer = Join-Path $run $kind
        New-Item -ItemType Directory -Path $consumer | Out-Null
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><RestorePackagesWithLockFile>false</RestorePackagesWithLockFile><RestoreLockedMode>false</RestoreLockedMode><RestorePackagesPath>$(& $escape $cache)</RestorePackagesPath></PropertyGroup>
  <ItemGroup><PackageReference Include="Njulf.$kind" Version="$version"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
        $source = if ($kind -eq 'Graphics') {
@'
using System;
using Njulf.Core.Math;
using Njulf.Graphics;
public static class Consumer
{
    public static void Create(GraphicsDevice device)
    {
        using Mesh mesh = device.CreateMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0u, 1u, 2u]);
        using Texture texture = device.CreateTexture2D(1, 1, [255, 255, 255, 255], TextureColorSpace.Srgb);
        using Material material = device.CreateMaterial(MaterialDefinition.Default, [new(MaterialTextureSlot.BaseColor, texture)]);
        using var instance = device.CreateRenderObject(mesh, material);
        GraphicsSettingsResult preview = device.Settings.Preview(new() { ResolutionScale = 0.75f });
        GraphicsCapabilities capabilities = device.Capabilities;
    }
}
'@
        } else {
@'
using System;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Rendering;
public sealed class Consumer : Game
{
    private InputAction jump = null!;
    private Action onJump = () => { };
    protected override void ConfigureRendering(RenderingOptions options) { }
    protected override void Load()
    {
        jump = Input.CreateAction("Jump");
        jump.AddBinding(new InputBinding(InputKey.Space));
        Vector2 mouse = Input.MousePosition;
        IContentManager content = Content;
        TextureColorSpace color = TextureColorSpace.HdrLinear;
        _ = color;
        _ = GraphicsDevice.Settings.Preview(new() { ResolutionScale = 1 });
    }
    protected override void Update(GameTime time)
    {
        base.Update(time);
        if (jump.WasPressed) onJump();
    }
}
'@
        }
        $source | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.cs')
        dotnet build (Join-Path $consumer 'Consumer.csproj') -c $Configuration --configfile (Join-Path $run 'NuGet.Config')
        if ($LASTEXITCODE) { throw "$kind package consumer failed." }
        $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $libraries = @($assets.libraries.Keys | ForEach-Object { ($_ -split '/')[0] })
        $forbidden = if ($kind -eq 'Graphics') { @($libraries | Where-Object { $_ -notin @('Njulf.Core', 'Njulf.Graphics') }) }
                     else { @($libraries | Where-Object { $_ -in @('Njulf.Editor', 'Njulf.Assets.Tooling', 'Njulf.AssetTool', 'Njulf.ShaderBuild') }) }
        if ($forbidden.Count) { throw "Unexpected $kind package dependencies: $forbidden" }
    }
    "PASS: nine packages contain DLL/XML pairs; Graphics-only and Framework-only consumers compile without project references." | Tee-Object -FilePath (Join-Path $run 'result.txt')
    Write-Output "Artifacts: $run"
} finally {
    $env:NUGET_HTTP_CACHE_PATH = $oldHttp
    $env:NUGET_SCRATCH = $oldScratch
}
