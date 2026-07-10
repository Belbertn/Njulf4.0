# Stop patching, build an SDF conformance oracle (don't start over)

## Context

After ~50 commits the SDF still looks wrong, and the user is asking whether to restart. Assessment: the architecture (per-mesh SDFs + instanced transforms + clipmapped global SDF with brick updates, Lumen-style) is the correct, industry-standard design, and the recent commits fixed four *distinct, real* bugs (scroll invalidation/validity signatures, dirty-region padding gap, anisotropic distance conversion, analytic-box mesh distances in 39dce1f). The problem is the *process*: every one of these different bugs has the same on-screen symptom (speckles/blobs/melt), and verification has been screenshot-by-eyeball, so progress is invisible and each fix can't be distinguished from noise. Starting over rebuilds the same design and re-encounters the same class of bugs — minus the diagnostics already built.

What's missing is a ground-truth oracle. This scene is ideal for one: every object is an axis-aligned or Y-rotated box, so the exact scene SDF is analytically computable.

## Plan

### 1. CPU mirror of the bake math — new `Njulf/Njulf.Tests/GlobalSdfConformanceTests.cs`

Follow the existing shader-mirror pattern (`DdgiShaderModelMirrorTests.cs`): reimplement in C#, with source-contract assertions pinning the GLSL:

- `SampleMeshSdf` (global_sdf_update.comp:105-137): world→local transform, analytic-box path, outside-bounds path, min-axis-scale conversion.
- `ComposeGlobalSdfDistance` + `ComposeConservativeGlobalSdfVoxelDistance` (candidate min, corner sampling, `safeBound` cap).
- Instance record construction via the real `MeshSdfManager.TryCreateInstanceGpuRecord`.

### 2. Analytic reference + per-cascade error report

- Build the `GiSdfCascadeField` instance list (hardcode the ~40 box transforms from `SampleStressSceneBuilder.cs:960-1042`, or expose the builder's object list).
- Reference SDF = min over boxes of exact world-space signed box distance (rotation handled by transforming into each box's frame).
- For each cascade voxel size (0.125/0.25/0.5/1.0), evaluate the mirrored bake at voxel centers on a slab of the scene and assert: near-surface error (|ref| < 2 voxels) ≤ ~1 voxel; no stored value more negative than ref by > 1 voxel (phantom-interior guard); no stored value exceeding ref by > 1 voxel within the near band (tunneling guard).
- On failure, print worst offenders (position, ref, actual, contributing instance) — turns the next bug into a coordinate, not a screenshot.

### 3. Calibrate expectations in-app (no code)

- Judge conformance per cascade view (`GlobalSdfCascade0..3`, debug views 124-127) against that cascade's voxel size. Cascades 2/3 (0.5/1m voxels) will always render 0.16-0.65m geometry melted — that is voxelization, not a bug.
- The `GlobalSdfFullSlice` view returns the first cascade whose trace hits, so coarse-cascade dilation shows through any fine-cascade miss — treat it as a smoke test only.
- `SdfBackendFirstCascade = 2` means DDGI consumes only the coarse cascades: if cascade 0 now conforms but GI still misbehaves, the remaining work is policy/tuning (first-cascade choice, resolution, thin-geometry handling), not bake correctness.

## Verification

- `dotnet test` runs the new conformance suite on CPU (no GPU needed).
- User captures `GlobalSdfCascade0` at the previous snapshot pose after 39dce1f: crisp boxes expected (analytic path). If the test passes but the screen disagrees, the divergence is GPU-side (upload/indexing), which the existing validity diagnostics + physical-brick debug view can then isolate — a much smaller haystack.