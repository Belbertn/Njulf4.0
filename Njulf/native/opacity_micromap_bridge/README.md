# Njulf NVIDIA OMM CPU bridge

This directory contains a small, versioned C ABI around NVIDIA's CPU opacity
micromap baker. The NVIDIA SDK is intentionally not vendored. Build against an
explicitly reviewed SDK source checkout and record the resulting bridge binary
SHA-256 in the AssetTool provenance manifest. The supported build statically links
the SDK and its pinned dependencies into the bridge: a shared SDK install
is deliberately rejected because a bridge-only hash would not authenticate the
code in a separately loaded SDK DLL/shared object.

The bridge forces four-state output, 32-bit indices, validation, bounded output
and workload, and `FullyUnknownOpaque` for unresolved triangles. Njulf's DDGI
alpha comparison is `alpha >= cutoff`, whereas the SDK API tests `alpha >
cutoff`; the bridge uses the immediately preceding FP32 cutoff. For cutoff zero
it assigns both sides opaque. This equivalence is valid for the first profile's
unit material and vertex alpha factors.

Example:

```powershell
./native/opacity_micromap_bridge/Build-PinnedBridge.ps1 `
  -SdkRoot C:/src/pinned-omm-sdk `
  -BuildDirectory C:/build/njulf-omm `
  -OutputDirectory C:/artifacts/njulf-omm
```

The production script accepts only the reviewed SDK commit
`9abacd0f187d0efca491946a29ba7df8c5345264` (SDK 1.9.2), verifies the required
submodules against their gitlinks, rejects tracked modifications, and emits the
bridge, its strict AssetTool provenance manifest, and the complete applicable
license texts. The bridge CMake configuration forces CPU-only,
four-state-compatible build settings; disables fast-math, OpenMP, GPU shader
artifacts, shader-compiler discovery/downloads, tests, and viewer; and emits no
separate SDK runtime library.

The supported artifact is Windows x64 Release. The manifest records the actual
CMake compiler ID/version and generator rather than trusting a caller-supplied
label. `Build-PinnedBridge.ps1` writes an artifact only after configuration,
compilation, binary hashing, dependency inspection, and manifest generation all
succeed. `NJULF_TEST_OMM_BRIDGE_PATH` can point the integration test at the
resulting DLL to exercise a real native bake and cooked-payload round trip.

Do not distribute the bridge or SDK runtime until their license, commit, build
flags, compiler, and exact binary digest have been reviewed and recorded.
