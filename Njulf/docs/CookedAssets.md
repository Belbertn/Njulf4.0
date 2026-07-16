# Cooked asset pipeline

Njulf resolves versioned, hashed cooked packages before source models. Cooked startup bypasses SharpGLTF/Assimp import, source-image decoding, runtime downscaling, GPU vertex construction, and runtime meshlet generation. Source fallback is disabled by default and is an explicit editor/development opt-in.

## Cook assets

```powershell
dotnet run --project Njulf.AssetTool -- cook model NjulfHelloGame/Strut.glb --out NjulfHelloGame/Cooked
dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out NjulfHelloGame/Cooked
dotnet run --project Njulf.AssetTool -- cook changed NjulfHelloGame --out NjulfHelloGame/Cooked
dotnet run --project Njulf.AssetTool -- clean-stale --out NjulfHelloGame/Cooked
```

The cooker defaults to the host RID and writes `Cooked/<rid>/`. Override it with `--platform win-x64`, `linux-x64`, or another supported desktop RID. `cook changed` skips an asset only when its source, effective settings, dependencies, tool version, platform, and every recorded output hash are unchanged. Package and database writes are atomic. Pass `--force` to rebuild.

Mesh payloads contain renderer-ready streams plus deterministic meshlet LOD0/1/2, simplified to approximately 100%, 50%, and 20% triangle density. Win-x64 and linux-x64 use meshoptimizer encoding; other RIDs use zstd until a compatible native decoder is available.

Textures default to semantic BC formats in plain, non-supercompressed KTX2 containers: BC7 for color/data, BC5 for normal maps, BC4 for scalar maps, and BC6H for HDR. Full mip chains and color-correct filtering are generated offline. macOS currently selects RGBA8 pending an ASTC target profile. Use `--texture-format rgba8` when an uncompressed diagnostic output is useful.

The output layout is:

```text
Cooked/
  win-x64/
    assetdb.njassetdb
    models/*.njmodel, *.njmesh, *.njanim
    materials/*.njmat
    textures/*.ktx2, *.njtex
    reports/*.cook-report.json
```

## Runtime policy

`ContentManager.Load<Model>` probes `Cooked/<current-rid>/models/<source-name>.njmodel`, then the legacy non-RID layout for migration compatibility. Normal runtime policy is cooked-only and strict.

- `NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true` explicitly enables editor/development source import.
- `NJULF_COOKED_ASSET_STRICT=false` relaxes whole-file/source validation for diagnosis only.
- `NJULF_COOKED_ASSET_REQUIRE_SIGNATURE=true` enables detached-signature enforcement for shipping deployments.
- `NJULF_COOKED_ASSET_PUBLIC_KEY=<path-or-PEM>` supplies the trusted ECDSA public key.

`ContentManager.CookedDiagnostics` reports the selected path, bytes read, load/upload timings, fallback reasons, and mismatch counts. `TextureManager.RuntimeDecodedTextureCount`, `CookedTextureLoadCount`, and `DownscaledTextureCount` distinguish cooked loads from source decoding.

Release sample publishing sets `CookedAssetsOnly=true`: source model/image files and source-import runtime assemblies are not copied. Debug builds retain the importer for the explicit fallback/editor path.

## Signing and migration

Generate a key pair once and keep the private key outside source control:

```powershell
dotnet run --project Njulf.AssetTool -- keygen --private keys/cooked-private.pem --public keys/cooked-public.pem
dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out NjulfHelloGame/Cooked --signing-key keys/cooked-private.pem
```

Every binary package and KTX2 sidecar gets a detached ECDSA P-256/SHA-256 `.sig`. Runtime signature verification covers the complete file, while package references and section hashes protect the internal graph and decoded contents.

Upgrade older compatible package versions atomically:

```powershell
dotnet run --project Njulf.AssetTool -- migrate NjulfHelloGame/Cooked --out NjulfHelloGame/Cooked-Migrated --signing-key keys/cooked-private.pem
```

Omit `--out` for an in-place migration. Major-version incompatibilities require a source re-cook.

## Format and I/O guarantees

Every binary has a fixed 64-byte header and a 40-byte section table. Readers reject incompatible/future versions, wrong endianness, malformed ranges, unknown required sections, unsupported compression, corrupt XxHash3 section hashes, and invalid dependency hashes. Large packages use memory-mapped views; smaller payloads use direct `RandomAccess.Read` into typed arrays. Metadata uses zstd, with LZ4 available for latency-sensitive sections.
