# NjulfGame

Requires .NET 10, a local Njulf checkout, and the Vulkan SDK (`glslangValidator` on PATH).
Run on Windows x64 with a Vulkan-capable GPU meeting the engine requirements.

```powershell
dotnet restore
dotnet run -c Development
dotnet publish -c Release --self-contained true -o artifacts/publish/win-x64
```

Commit the generated `packages.lock.json`. If the checkout moves, update `NjulfEngineRoot`
in the project. Engine projects keep their own lock files.
Release already selects `win-x64` in the project; omit command-line `-r` so engine
projects retain their RID-independent lock files.

Put model sources and their dependencies in `Assets`. Build/run/publish automatically cook
changed models; restart to see changes. `--no-build` uses existing output. Escape exits.
The starter uses the Low graphics preset at native resolution. Change it in
`ConfigureRendering` as your game grows. `--frames N` exits after N fully rendered frames.
Ship the entire publish folder; users need the GPU driver, but no .NET or Vulkan SDK installation.

Runtime files that are not model-cooker inputs must be explicit project items:

```xml
<ItemGroup>
  <NjulfRuntimeContent Include="Assets\Scenes\*.njscene.json;Assets\Effects\*.njeffect.json" />
  <NjulfEffectShader Include="Assets\Effects\*.frag;Assets\Effects\*.comp" />
</ItemGroup>
```

Declare these items before the `Njulf.Game.targets` import. For custom shaders and content
options, see `docs/CustomEffects.md`, `docs/CookedAssets.md`, and `docs/GettingStarted.md`
in the engine checkout. The original tetrahedron fixture is provided by Njulf.
