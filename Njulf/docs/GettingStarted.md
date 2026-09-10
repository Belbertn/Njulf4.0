# Create and ship a game

This workflow uses a local Njulf engine checkout and .NET 10. The validated shipping
target is Windows x64. Install the Vulkan SDK and put its `Bin` directory on PATH:
builds use `glslangValidator`, `spirv-dis`, `spirv-opt`, and `spirv-val`. Running a
game requires a Vulkan 1.3 GPU/driver supporting the engine's mesh-shader and
ray-query feature requirements. The first engine build also compiles and validates
its shaders; subsequent builds reuse the shader cache.

## Create and run

From the engine checkout:

```powershell
dotnet new install ./templates/Njulf.Game
dotnet new njulf-game -n MyGame -o ../MyGame --engine-root (Get-Location).Path
Set-Location ../MyGame
dotnet restore
dotnet run -c Development
```

The template renders a small original tetrahedron with a fixed camera and light.
It uses the existing Low graphics preset at native resolution, avoiding advanced-GI
startup work for this small scene. Change the preset in `ConfigureRendering` when needed.
Escape exits. Edit `Program.cs` to add gameplay using the [framework API](FrameworkApi.md).
The engine path is stored in the generated project's `NjulfEngineRoot` property;
update it when moving the checkout. No engine NuGet packages are required.

Commit the generated `packages.lock.json` with your project. Release and CI restores
use locked mode. After an intentional dependency change, run
`dotnet restore --force-evaluate -p:RestoreLockedMode=false` and review the lock diff.
Engine projects retain their own checked-in lock files.

## Iterate on content

Place models and their referenced buffers/textures under `Assets`, then build or
run again. `build/Njulf.Game.targets` calls the existing `cook changed` command
and copies its outputs during that same build. Source, dependency, settings, tool
version, platform, and output hashes determine whether a model needs recooking.
Unchanged packages retain their timestamps. Missing/corrupt outputs are rebuilt;
failed cooks stop the build. Removed sources and obsolete generated outputs are
cleaned after a successful cook, including previously copied output files.

`NjulfAssetsRoot` defaults to the project's `Assets` directory and `NjulfCookedRoot`
to its generated `Cooked` directory. Use a dedicated generated directory for cooked
output, never a source directory or a shared output from another project. Cooking
selects the build RID, or the host SDK RID when no RID is specified. Existing cooker
format defaults apply; see [Cooked assets](CookedAssets.md) for supported model formats,
unique-basename requirements, and specialized importer workflows.

Only models and their dependencies go through this cooker. Declare other runtime
files explicitly, before the `Njulf.Game.targets` import:

```xml
<ItemGroup>
  <NjulfRuntimeContent Include="Assets\Scenes\*.njscene.json;Assets\Effects\*.njeffect.json" />
  <NjulfEffectShader Include="Assets\Effects\*.frag;Assets\Effects\*.comp" />
</ItemGroup>
```

Runtime content preserves project-relative paths; external items require a relative
`Link` destination. Content from referenced projects is not copied automatically;
declare any such runtime files in the game project. Application shaders reuse the existing include-aware shader cache,
and `.spv` outputs are copied beside their manifests. See [Custom effects](CustomEffects.md).
Do not declare authoring models or GLSL as runtime content. Standalone runtime images,
fonts, and other files that are intentionally decoded at runtime may be declared.

Restart the game after content edits. There is no background watcher. Design-time
builds do not cook; IDE builds must invoke MSBuild rather than skip its dependency
checks. `dotnet run --no-build` and `dotnet publish --no-build` reuse existing cooked
assets, so omit that flag when preparing a new release.

Shader errors identify the source/include, line, and compiler-provided column,
plus the artifact/variant. Content errors identify the source file and JSON parser
position when available (JSON columns are UTF-8 byte positions). File-only errors
are used when no source position exists. Original compiler output and inner
exceptions remain available; cooked-load errors retain the existing recook guidance.

## Publish

From the generated game directory:

```powershell
dotnet publish -c Release --self-contained true -o artifacts/publish/win-x64
./artifacts/publish/win-x64/MyGame.exe
```

The template selects `win-x64` locally for Release and includes it in the initial
restore lock. Omit command-line `-r`: it also changes the referenced engine projects'
restore graphs, which conflicts with their RID-independent checked-in locks.

Distribute the **entire output folder**. It includes the .NET runtime, required native
libraries, cooked content, declared runtime files, shaders, and existing third-party
notices. Add notices for your own dependencies/assets. Authoring models, GLSL, editor,
and cooker/build tooling are excluded. Runtime decoders such as StbImageSharp remain
intentional dependencies. Players need a suitable GPU driver, not the .NET or Vulkan SDK.

Content resolves relative to the executable directory, so launching from another
working directory works. `--frames 30` waits for 30 fully rendered frames, excluding
loading-screen presents. This workflow
does not enable trimming, single-file publishing, Native AOT, signing, or an installer.

## Validate changes to this workflow

From the engine checkout, run `./tools/test-game-template.ps1`; add `-RunGame` on a
machine with a supported GPU to launch the published game with authoring assets
unavailable. Generated projects, logs, and comparison data stay under
`artifacts/game-template`. The script isolates parent build settings to exercise the
same configuration as a project outside the engine checkout. Windows CI runs the
build/publish checks without the GPU launch.
