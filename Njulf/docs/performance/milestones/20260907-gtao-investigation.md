Archived on 2026-09-07 from `artifacts/gtao-loop-verification-20260907-075724/verification.md`. Paths and statements about retained raw files below refer to that original run directory and the time of the experiment. Raw payloads are subject to the repository retention policy; this report preserves the findings.

The statement accurately describes GTAO's current sampling arithmetic for samples that survive the screen-bounds and depth checks. The proposed optimization is plausible and showed a modest improvement in one GPU comparison. The quick performance decision is **inconclusive**: the target mean fell 4.25%, below the skill's approximately 5% signal. Production source was left unchanged.

Source inspected at HEAD `c30a5d5a614a10031a0719d4ece020b12c1999ec`:

- `Njulf.Shaders/gtao.comp:101`: homogeneous reconstruction using `MulRowMajor` and `max(abs(w), 1e-6)`.
- `Njulf.Shaders/gtao.comp:237`: sample placement, texel resolution, and depth rejection.
- `Njulf.Shaders/gtao.comp:253`: reconstruction, `length(delta)`, finite/radius rejection, tangent-plane rejection, slice-tangent rejection, then weighting and horizon update.
- `Njulf.Shaders/common.glsl:3036`: the generic multiply is four four-component dot products.
- `Njulf.Rendering/Data/SceneDataBuilder.cs:597`: the inverse matrix is computed on the CPU. The shader does not invert a matrix per sample.
- `Njulf.Core/Math/Matrix4x4.cs:201` and `Njulf.Rendering/Data/SceneDataBuilder.cs:3313`: the normal camera uses sparse reverse-Z perspective, with temporal jitter offsets that must be preserved.

The fresh baseline used `glslangValidator` 16.0.0, `-V --target-env vulkan1.3 -Os`, and the three Release diagnostic defines set to zero. It passed `spirv-val`, and its bytes exactly match `Njulf.Shaders/obj/Release/net10.0/Shaders/gtao.comp.spv`.

Baseline SHA-256: `2f6b2d769bc3d68ea182f965f7dcb8aff51c375855250a97b742b0fc4802914c`.

Optimized SPIR-V evidence:

- `baseline.spvasm:227`: the inverse matrix load and extracted rows are shared outside the sampling loops; helper calls have been inlined.
- `baseline.spvasm:793`: four `OpDot` instructions reconstruct each eligible sample, followed by vector homogeneous division at line 802 and `Length` at line 804. Radius rejection follows; the tangent-plane dot is at line 851 and the slice-tangent dot at line 860. The second signed horizon loop contains the same sequence.
- `Length` retains square-root semantics in this intermediate representation, as defined by the [Khronos SPIR-V GLSL instruction specification](https://registry.khronos.org/SPIR-V/specs/unified1/GLSL.std.450.html). SPIR-V operation counts are not native instruction counts or timings.

The isolated candidate precomputes perspective coefficients once per invocation, includes jitter offsets, and guards the sparse matrix structure with exact zero comparisons. Other projection structures execute the original reconstruction. It tests squared distance and both plane conditions before `sqrt`; surviving samples still use ordinary distance in falloff and horizon normalization. Sampling coordinates, texel snapping, direction and step budgets, local size, descriptors, push constants, and outputs are preserved. Center and geometric-normal reconstruction remain unchanged.

`candidate.spvasm:880` retains the four dots in the generic branch. The perspective branch at line 892 skips those dots. Its distance-squared dot is at line 915, plane dots at lines 956/965, and square root at line 972. The generic fallback makes the full module larger even though the perspective path performs less reconstruction arithmetic.

| Compiled variant | SPIR-V bytes | Static image-fetch sites | Length sites | Explicit Sqrt sites |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 23,464 | 7 | 3 | 2 |
| Perspective reconstruction only | 26,068 | 7 | 3 | 2 |
| Early rejection only | 23,388 | 7 | 1 | 4 |
| Combined candidate | 25,992 | 7 | 1 | 4 |

These are whole-module static sites, including both signed searches and work outside the sampling loop. Moving `Length` to `dot` plus conditional `Sqrt` changes execution placement; it does not remove square-root semantics for accepted samples. All three candidate variants compiled freshly and passed `spirv-val`. Only the combined candidate was timed, so the runtime result cannot attribute gains separately to either mechanism.

The runtime comparison used the same existing Release executable and an explicit shader override for each side. Exact executable/assembly hashes are saved in `baseline-hashes.json` and `candidate-hashes.json`; the host reports build commit `c3a254b901b99b4264fd433587a4a1dba20dd0c5`. The raw GTAO source, consuming pass, camera matrix constructor, and AO settings have no committed changes from that revision to inspected HEAD. The loaded-module records authenticate the current-source baseline and the candidate independently of host commit metadata.

Configuration: SponzaPlaza, DdgiHigh, fixed `sponza-high` camera, 1920x1080, full-resolution High GTAO (6 directions, 8 steps per signed search), VSync off, unlimited FPS, async compute disabled, GPU timestamps on, Vulkan validation off. GPU: NVIDIA GeForce RTX 3060 Laptop GPU; `nvidia-smi` driver 610.62, capture-reported driver 610.248.0. Both sides used the existing writable cache and ordinary pipeline initialization, 120 requested warmup frames, 408 additional settling frames, and 120 measured frames. Neither settling window timed out. No caches were cleared.

| GPU metric | Baseline mean | Candidate mean | Change |
| --- | ---: | ---: | ---: |
| Raw GTAO | 4.659892 ms | 4.461925 ms | -4.2483% |
| Whole GPU frame | 20.433158 ms | 20.123808 ms | -1.5140% |

The benchmark exposes raw GTAO under `AmbientOcclusionPass`: `VulkanRenderer.cs:9930` sums SSAO and GTAO into that field, and effective mode 2 (GTAO) was verified for both captures. Filtering is a separate metric. Raw-GTAO p95 was 4.759 -> 4.520 ms; whole-frame p95 was 20.650 -> 20.309 ms.

Both captures report `Comparable=true` and `ProductionTiming=true`. Settings and camera/trajectory fingerprints match. The repository's `Assert-LoadedShaderMeasurement` passed for both captures; module inventories stayed stable throughout each window. Both contained 157 modules and only `gtao.comp.spv` changed. The GTAO pipeline is created during pass initialization, before the measured steady-state workload. This was not a native-ISA or instruction-stall capture.

One representative static final-HDR check passed: relative RMSE 0.000107357 against a 0.005 limit, FLIP p95 0.00515954 against a 0.02 limit. Sampling code was retained, but algebraic reconstruction and squared comparisons may round differently near thresholds. Absolute measured frame numbers differed (731–850 vs 663–782); this was a fixed camera and each 120-frame window spans 15 full eight-phase sampling cycles. No claim of bitwise equality, motion quality, or exercised generic-projection fallback is made.

The evidence supports the proposed investigation, but this pair is insufficient to retain the candidate under the quick-loop signal. The shader variants, compile commands, disassemblies, captured images, logs, authenticated identities, and comparison JSON are retained here for review. No production edits or Git mutations were performed.
