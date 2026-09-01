# Automatic Planar Reflection Opt-In and Capture Optimization Plan

## Status and scope

- Planning snapshot: `Simplified-SDF` at `15ab843185d057ae5a1685f27a61468e0e68e95e` on 2026-09-01.
- Primary performance target: the measured `AutomaticPlanarReflectionPass` bottleneck in the ProfileSymbols Bistro trace.
- Product addition: automatic planar reflections are disabled by default per material and may run only for materials explicitly selected by a developer.
- This plan does not implement the changes. It defines an ordered implementation and qualification contract.
- The material-policy work and each GPU optimization are independent candidates. Do not combine their performance claims.

## Outcome

Implement a material checkbox named **Automatic planar reflection** with these semantics:

- The default is `false`, including old assets, imported materials without the Njulf extra, the default material, and legacy scene overrides.
- A material with the option disabled can never become an automatic-planar receiver, even when it is water, a mirror, or a generic glossy plane.
- Enabling the option makes the material eligible for consideration. It does not bypass visibility, rigid/non-deforming geometry, valid planar evidence, projected-coverage, transform, capture-count, or memory checks.
- Explicit authoring replaces roughness/F0/texture-statistics as the material eligibility test. Those values remain useful for ranking and shading, but do not veto a developer's explicit choice.
- The setting applies to every surface using that material. To select only one object that shares an asset material, the editor uses the existing copy-on-write material edit path to create an object-local material definition.
- Turning the option off publishes zero use of that receiver on the next prepared frame and cannot sample a stale capture. Turning it on schedules a fresh capture.

After the receiver set is explicitly frozen, replace the per-fragment linear excluded-object search with an exact O(1) bitset lookup when the bounded metadata bank permits it. Keep an exact fallback and retain only measured GPU changes that pass correctness, quality, memory, and ABBA performance gates.

## Evidence motivating the work

The two newest ProfileSymbols captures are:

- Bistro: `C:\Users\njaal\Documents\NVIDIA Nsight Graphics\NjulfHelloGame_2026_09_01_18_00_04.ngfx-gputrace`
- Sponza control: `C:\Users\njaal\Documents\NVIDIA Nsight Graphics\NjulfHelloGame_2026_09_01_17_53_28.ngfx-gputrace`

The captures were recorded with Nsight Graphics 2026.3.1 on an RTX 3060 Laptop GPU with GPU clocks locked to base. The Bistro frame is the actionable outlier:

- Frame GPU time: `82.73 ms`.
- `AutomaticPlanarReflectionPass`: `42.55 ms`, or `51.4%` of the frame.
- Its two dominant draws: `36.55 ms` and `5.20 ms`.
- Dominant fragment program: `forward_opaque_ddgi.frag.spv`, profiler hash `0x5e7ae346fd19cd73`, with `93%` of sampled shader activity attributed to `forward.frag`.
- Fragment occupancy: `15.1%`; pixel-shader register limited: `72.6%`; ISBE allocation stalled: `96.8%`.
- `common.glsl`'s inlined `ReadStorageWord` accounts for `44.15%` of sampled WAIT attribution.
- `automatic_planar_reflection.glsl`'s excluded-object loop accounts for `13.66%` WAIT, and its list read/compare accounts for another `4.66%` BRANCH attribution.
- `forward.frag`'s call to `AutomaticPlanarShouldDiscardCaptureFragment` accounts for `6.77%` sampled attribution.

The ProfileSymbols artifact used by that draw must remain the attribution anchor:

- Artifact: `forward_opaque_ddgi.frag.spv`
- SHA-256: `e58fb3a84bde4f134dbf018bdaed02a39c6cb3026619f2976e1a5c1e5e45a139`
- ProfileSymbols bundle: `sha256:2602b760af51a02be47cc0fc4e1beb39cb3238d2878cea35a0952e0daa115801`
- Source: `forward.frag` with `FORWARD_OPAQUE=1`
- Relevant transitive include: `automatic_planar_reflection.glsl`

ProfileSymbols establishes attribution, not final timing. Release and ShippingPerformance builds are required for performance acceptance.

## Existing root cause

`AutomaticPlanarReflectionManager.WriteMetadata` currently writes, for each active capture, a sorted array of excluded object indices at record words `+90/+91`. Every capture fragment calls `AutomaticPlanarShouldDiscardCaptureFragment`, which invokes `AutomaticPlanarListContains` and linearly reads up to 256 indices before performing the retained-half-space plane test.

That creates three related costs:

1. Every fragment in the mirrored scene render pays metadata and branch work, including fragments whose object can be accepted or rejected once per draw or meshlet.
2. The generic storage accessor is sampled heavily and appears to retain non-uniform descriptor machinery even though the automatic-planar descriptor index is constant.
3. The entire forward fragment program is reused for a capture configuration that disables several features at runtime, retaining high register pressure even when their results are unreachable.

The first retained implementation addresses item 1's excluded-object membership test. Items 2 and 3 are conditional follow-ups and must not be bundled into the first measurement.

## Invariants and non-goals

- Preserve the exact reflected camera, reverse-Z ownership, clipping plane equation, tolerance, raster state, materials, lighting, shadows, alpha coverage, and transparent/foliage behavior.
- Do not reduce capture resolution, resolution scale, mip count, samples, rays, visible geometry, recapture cadence, reflection confidence, or quality tier.
- Do not silently truncate an exclusion set. The current shader's `min(count, 256)` behavior is not an acceptable fallback for a larger exact set.
- Do not add per-fragment diagnostic atomics.
- Do not change the bindless descriptor layout or GPU material ABI for the material toggle.
- Do not infer opt-in from `OpticalBoundaryKind.WaterSurface`, `GiCausticCasterPolicy.Mirror`, names, texture names, roughness, metallic, or F0.
- Do not edit `tools/perf-campaign.bistro-sponza.json` merely to make a candidate pass.
- Do not claim that this work fixes the separately observed main-forward, DDGI, audit, or transparent/refraction costs.

## Worktree and provenance guard

The planning worktree already has pre-existing edits, including overlapping material import/cooking, Bistro profile, shader build, `forward.frag`, tests, capture harness, documentation, and tools. Before implementation:

1. Record `git rev-parse HEAD`, `git status --short`, submodule/tool versions, driver, GPU, clocks, and render settings in the evidence directory.
2. Preserve an exact patch or commit for the existing dirty state, or implement in an isolated worktree created from an explicitly chosen commit.
3. Do not overwrite or reformat unrelated changes in `AmazonBistroMaterialProfile.cs`, `CookedModelImportContract.cs`, `ModelImporter.cs`, shader-build files, `forward.frag`, the capture harness, or their tests.
4. Record the compiled shader recipe, complete transitive include closure, SPIR-V hash, and capture configuration for every baseline and candidate.

## Phase 1 — Add the strict material opt-in

### 1.1 Canonical authored contract

Add `bool AutomaticPlanarReflectionEnabled { get; init; }` to `MaterialDefinition` in `Njulf.Rendering/Data/MaterialTransportContracts.cs`.

- Keep the CLR default `false`; do not add an `Auto` state.
- Keep it outside `MaterialFeatureFlags`. It is a CPU selection policy, not a fragment-shading feature, and must not force an extension payload, forward-class change, new shader variant, or GPU ABI change.
- Add `MaterialChangeMask.AutomaticPlanarReflection` as a CPU-only change classification and include it in `All`. The general material/content revision remains the invalidation source; a new GPU field and a new `MaterialAspectRevisions` lane are unnecessary.
- Validate that the value participates in material equality, deduplication, registration, and copy-on-write naturally through the immutable `MaterialDefinition` record.
- `MaterialDefinitionV1Adapter` and any legacy GPU-material conversion leave the property disabled because legacy GPU payloads cannot prove authoring intent.

### 1.2 Imported and cooked material path

Carry the value through every authored asset route:

| Route | Required change | Legacy behavior |
| --- | --- | --- |
| `ModelMaterial` | Add `AutomaticPlanarReflectionEnabled`, default `false`. | Missing JSON property is `false`. |
| glTF | Add boolean material extra `NJULF_automatic_planar_reflection`. Parse it in both `SharpGltfModelMeshConverter` and `ModelImporter.ReadNjulfMaterialExtras`; reject non-boolean values. | Missing extra is `false`. |
| Runtime upload | Map `ModelMaterial.AutomaticPlanarReflectionEnabled` into `MaterialDefinition` in `ModelRenderUploadService.BuildMaterialDefinition`. | No inferred mapping from mirror/water/gloss. |
| Cooked material | Persist the `ModelMaterial` property and bump `CookedFormatVersions.Material` from `1.2` to `1.3`. | Readers accept older `1.x` minors and normalize the missing field to `false`. |
| Migration/recook | Preserve an explicit value when present; old material payloads remain disabled. | Never synthesize `true` during migration. |

Document the extra and default-off behavior in `docs/CookedAssets.md` and `docs/ReflectionQualification.md`.

Do not change `AmazonBistroMaterialProfile` to infer reflective surfaces. If Bistro source assets cannot carry the extra, add an explicit, reviewed developer allowlist keyed by authenticated Bistro asset/material identity in a separate change. The exact opted-in material names must be chosen by the developer and recorded in the performance manifest; no name/roughness heuristic may populate that list.

### 1.3 Scene override and editor path

- Add nullable `bool? AutomaticPlanarReflectionEnabled` to `SceneMaterialOverrideDocument` so absence means “retain the asset value” and explicit `false` remains distinct from absence.
- Bump `SceneDocument.CurrentSchemaVersion` from `10` to `11`; older documents load with the asset/default policy unchanged.
- Map the property in both `MaterialManagerSceneMaterialOverrideStore.Apply` and `Capture`.
- Add an **Automatic planar reflection** checkbox under the material inspector's surface/reflection policy in `EditorImGuiPanels`.
- Tooltip: “Allows this material's rigid planar surfaces to compete for the automatic planar capture budget. Disabled by default.”
- Continue to edit through `EditorController.UpdateSelectedMaterialDefinition`, preserving the existing copy-on-write behavior for shared materials.
- Show a read-only rejection/status line when useful: disabled, non-planar, deforming, below coverage, capture limit, memory denied, selected, capture, or reproject.

### 1.4 Candidate admission and lifecycle

Change `AutomaticPlanarReflectionManager.AnalyzeInstance` and `AutomaticPlanarCandidateAnalyzer` as follows:

1. Resolve the canonical material definition before loading mesh transport geometry or computing fallback planar evidence.
2. If the material is not opted in, reject with a dedicated `MaterialOptInDisabled` reason and stop before geometry analysis, texture statistics, projected evidence, or material ranking work.
3. Add the opt-in value to `AutomaticPlanarCandidateInput` as defense in depth so the pure analyzer cannot admit a disabled material.
4. Remove implicit material admission based on water/mirror semantics and remove generic roughness/F0/texture-statistics eligibility vetoes. Preserve semantic, roughness, F0, and statistics for ranking, diagnostics, and shading only.
5. Keep rigid/deforming, valid planar evidence, transform, projected coverage, capture-count, and memory gates unchanged.
6. Preserve material revision in receiver identity/content signatures. Verify that enabling produces a new selection and a fresh capture, while disabling clears the selected count, publishes empty metadata when no receivers remain, advances capture generation, and prevents stale sampling on the next frame.

The renderer still enumerates scene objects to preserve the exact object-index contract used by capture shaders. The early material gate must avoid the expensive geometry/statistics work without changing object-index assignment.

### 1.5 Material-policy tests

Add or extend tests for:

- `MaterialDefinition.Default` and legacy adapters are disabled.
- Enabled/disabled classification sets `MaterialChangeMask.AutomaticPlanarReflection` and advances the general material revision without changing the GPU material layout.
- Both glTF import backends accept boolean `true`/`false`, reject other JSON kinds, and default missing extras to `false`.
- Cooked material `1.3` round-trips both values; representative `1.2` payloads load disabled; migration never manufactures opt-in.
- Scene schema 11 round-trips nullable/true/false values; older scene schemas retain the referenced material's policy.
- Editor edits preserve copy-on-write: changing one object does not mutate other users of a shared material.
- Disabled generic, mirror, and water materials are all rejected. Enabled versions proceed to the unchanged geometric/coverage gates.
- Explicitly enabled generic materials are not rejected solely for incomplete texture statistics, high roughness, or low F0.
- Runtime on→off and off→on edits clear stale selection and schedule the correct next action.
- Two submeshes sharing a material obey material-wide selection; a private override can select just one surface.

## Phase 2 — Freeze the qualifying workload before optimization

The material policy changes the workload, so it cannot be counted as evidence for the exclusion lookup optimization.

1. Choose and record the exact Bistro and Sponza material IDs intended to receive planar reflections. If no Sponza surface is intended, record zero opt-ins and use Sponza only as a non-regression control.
2. Author those choices through source glTF extras or explicit scene overrides. Do not choose materials dynamically from profiler results.
3. Recook assets normally and archive source/cooked hashes.
4. Verify captures contain at least one selected receiver and `AutomaticPlanarCaptureCount > 0` on the frames used for the target-pass comparison. Existing quality captures with `AutomaticPlanarSelectedCount = 0` are invalid evidence for this issue.
5. Establish Release and ShippingPerformance A/A distributions with the receiver set, camera path, reflection mode, quality preset, cache state, warmup, power, clocks, and driver frozen.
6. Classify capture frames (`CaptureCount > 0`), reprojection-only frames, and no-work frames separately. Do not dilute capture cost with no-work frames.

Report the policy change honestly: reducing automatic receivers to the explicitly requested set is expected product behavior, but it is not the shader optimization's A/B speedup.

## Phase 3 — Replace excluded-object scans with exact bitsets

### 3.1 Metadata v3 encoding

Keep the existing double-buffered 1024-word-per-frame metadata buffer and descriptor binding. Bump the CPU and GLSL metadata version together from `2` to `3`.

Retain record words `+88/+89` as the small receiver-identity list and preserve words `+92` through `+95`. Reinterpret only the excluded-object descriptor:

| Record word | Metadata v3 meaning |
| --- | --- |
| `+90` | Exclusion encoding descriptor. Bit 31 set = dense bitset; low 31 bits = bitset word count. Bit 31 clear = sorted-list fallback; low 31 bits = exact list count. |
| `+91` | Word offset of the selected exclusion payload within the current frame bank. |

For bitset mode:

- Build a zero-based dense mask sized to `(maximumExcludedObjectIndex >> 5) + 1` words.
- Set bit `(objectIndex & 31)` in word `(objectIndex >> 5)`.
- Shader lookup bounds-checks the word index, loads one payload word, and tests one bit.
- An empty exclusion set has zero payload words and always returns false.

For sorted-list fallback:

- Preserve the existing sorted, distinct CPU list.
- Search the complete encoded count exactly; remove the silent `min(count, 256)` truncation.
- Keep this path for correctness and diagnostics, not as a claimed optimization.

Precompute every receiver list, exclusion representation, and texture-index payload before writing either frame bank. Prefer bitset mode for every capture when the complete bank fits. If it does not, deterministically replace the most expensive bitset expansion with its exact sorted-list representation until the bank fits. If the complete exact payload still does not fit, reject/skip the affected capture with an explicit bounded-metadata diagnostic before touching mapped memory. Never partially write or overrun the bank.

Add low-cost per-frame CPU telemetry:

- bitset capture count;
- sorted-list fallback capture count;
- excluded object count per slot;
- bitset/list payload words per slot;
- total metadata words used and bank high-water mark;
- capacity rejection count.

Expose these fields through `SceneRenderingData`, `RendererDiagnostics`, `RendererDiagnosticsAssembler`, and `SampleBistroQualityCaptureHarness`. Do not add fragment atomics.

### 3.2 Shader lookup

In `Njulf.Shaders/automatic_planar_reflection.glsl`:

- Keep `AutomaticPlanarListContains` for receiver identities.
- Add a separate `AutomaticPlanarExcludedObjectContains` that decodes word `+90` and executes either the one-word bitset test or the exact list fallback.
- Call it only from `AutomaticPlanarShouldDiscardCaptureFragment`.
- Preserve the plane read, reflected-camera side selection, diagonal-scaled tolerance, and final half-space test byte-for-byte where practical.
- Keep the bitset/list choice as a uniform per-capture branch.
- Do not change `AutomaticPlanarRead` or its generic `ReadStorageWord` accessor in this candidate.
- Recompile every forward and foliage variant that includes this helper; do not use stale `UseExisting` artifacts for qualification.

Add an internal benchmark/test override with `SortedList` and `BitsetAuto` modes. It must affect only exclusion encoding, be recorded in manifests, and default to `BitsetAuto` in production only after qualification. It is not a public quality setting.

### 3.3 CPU and shader contract tests

Test exact semantic equivalence for:

- empty sets;
- indices `0`, `1`, `31`, `32`, `33`, and the maximum represented index;
- sparse high indices and absent indices inside/outside the represented range;
- duplicate and unsorted source inputs normalized to a deterministic exact set;
- both capture slots and both frame banks;
- mixed bitset/list encodings in one bank;
- exact-fit, one-word-over, fallback, and total-capacity rejection cases;
- metadata version/magic mismatch failing unavailable/safe;
- v3 CPU constants matching GLSL constants and record offsets;
- excluded receivers never appearing in their own mirrored capture;
- non-excluded objects remaining visible;
- the retained half-space result remaining identical at, inside, and outside tolerance;
- opaque, masked, foliage, double-sided, and sorted-transparent capture paths.

Compile the complete affected shader family with normal artifact generation. For each artifact:

- record source/define/transitive-include recipe and SHA-256;
- verify the expected fragment entry point and descriptor/push-constant/interface ABI;
- run `spirv-val` for the renderer's Vulkan target;
- inspect SPIR-V to confirm the bitset path is a bounded word load/bit test and that no unbounded or 256-element scan remains on that path;
- create the affected Vulkan pipelines under validation and exercise clean resize/shutdown lifetime.

Static instruction count is diagnostic only; it is not a success metric.

## Phase 4 — Measure and decide on the bitset candidate

### 4.1 Mechanism check

Use a new ProfileSymbols capture only after the exact source/artifact identity is verified. On every qualifying Bistro capture frame:

- `BitsetCaptureCount` must equal the active capture count.
- `SortedListFallbackCount` and capacity rejection count must be zero.
- Source attribution for the old excluded-object loop must disappear from the active bitset path.
- Exclusion-related storage WAIT/branch samples should fall; fragment occupancy, registers, and ISBE stalls must not regress materially.

Sampling percentages describe where remaining stalls occur; do not convert them directly into predicted milliseconds.

### 4.2 Release/ShippingPerformance gate

Run at least three ABBA cycles after an A/A noise study, using the same executable inputs and the internal encoding override:

- A: metadata v3 exact sorted-list mode.
- B: metadata v3 `BitsetAuto`, proven active by telemetry.

Measure:

- `AutomaticPlanarReflectionPass` p50/p95 and per-capture distribution;
- its dominant opaque/foliage/transparent draw timings where available;
- whole-GPU-frame p50/p95/p99;
- ordinary `ForwardPlus` and all secondary passes for regressions;
- renderer CPU time and metadata construction time;
- GPU and tracked memory, metadata high-water mark, and pipeline/cache bytes.

Retain the candidate only when the existing campaign contract is met:

- targeted pass improves by at least `5%` and `0.05 ms`, or the whole frame improves by at least `1%` and `0.10 ms`;
- 95% confidence from 10,000 bootstrap resamples;
- no repeated secondary-pass regression above `1%`;
- GPU p95 target `<= 10 ms`, renderer CPU p95 `<= 6 ms`, frame p99 `<= 16.6667 ms` remain the campaign goals rather than reasons to lower quality;
- tracked memory remains below 80% of the 2 GiB budget with at least 20% headroom;
- no Vulkan/shader validation, device-loss, bounds, or shutdown errors.

### 4.3 Image and temporal gate

Use identical opted-in receivers and compare converged linear-HDR output, not ProfileSymbols captures. Require:

- relative HDR RMSE `<= 0.005`;
- FLIP p95 `<= 0.02`;
- ROI luminance delta mean `<= 0.02`, p95 `<= 0.03`;
- candidate deltas no worse than the identical-build A/A envelope;
- no self-reflection, missing reflected objects, clipping seam, stale capture, reflection pop, new flicker, alpha/foliage loss, or transparent ordering change.

Inspect the opted-in planar ROI, plane boundary, reflected dynamic objects, thin/alpha geometry, and camera-cut/reprojection transitions manually.

## Conditional follow-ups — one candidate at a time

Only begin these after the bitset candidate is accepted and a new trace proves material residual cost.

### A. Constant-descriptor storage accessor

If `AutomaticPlanarRead` remains a leading WAIT source, introduce a specialized constant-index accessor for `AUTOMATIC_PLANAR_REFLECTION_BUFFER_INDEX`. Verify generated SPIR-V removes unnecessary non-uniform descriptor decoration/indexing without changing bounds or descriptor ABI. Measure it independently against the retained bitset stack.

### B. Reject whole excluded meshlets before fragment shading

Move the same exact exclusion decision into the taskless/task/mesh stage where object identity is uniform. Excluded objects or meshlets emit zero primitives. Optionally reject a meshlet wholly outside the retained half-space using conservative bounds, but retain the exact fragment plane test for intersecting meshlets.

Cover `forward.mesh`, `forward_simple.mesh`, compacted/generated variants, foliage, direct fallback, and both sidedness paths. Prove primitive ownership and image equivalence before measuring. Do not combine this with the constant-accessor change.

### C. Dedicated automatic-planar capture shader/pipeline

If the full forward fragment program remains register/ISBE limited, create a capture-specific variant that statically removes only work proven unreachable under `RecordAutomaticPlanarCapture` settings. Preserve every feature still visible in the capture, including direct lighting, shadows, material alpha, foliage, emissive, and the sorted transparent path.

Track pipeline count, compile/startup time, cache size, SPIR-V, registers, occupancy, and quality. Do not accept a smaller shader if it changes the reflected scene or merely shifts cost into pipeline creation.

## Expected implementation slices

Keep commits narrow and independently reversible:

1. Material contract, import/cook/scene persistence, editor checkbox, and tests.
2. Candidate hard gate, lifecycle invalidation, diagnostics, and tests.
3. Frozen opted-in Bistro/Sponza workload plus A/A evidence; no GPU optimization.
4. Metadata v3 CPU encoder, exact fallback, telemetry, and CPU tests.
5. GLSL bitset decoder, regenerated artifacts, ABI/SPIR-V/Vulkan tests.
6. ProfileSymbols mechanism evidence and Release/ShippingPerformance ABBA/quality report.
7. At most one conditional follow-up per later commit/evidence cycle.

The material opt-in is a product requirement and remains even if the bitset candidate fails its performance gate. A failed GPU candidate reverts to the exact list encoder without re-enabling implicit material discovery.

## Completion criteria

The issue is complete only when all of the following are true:

- Every new and legacy material is default-off unless a developer explicitly opts it in.
- The value persists through glTF, cooked assets, scene overrides, editor save/reload, copy-on-write, and runtime material updates.
- Water, mirror, and glossy semantics cannot implicitly activate a capture.
- The chosen Bistro surface set is explicit, stable, and recorded; qualifying benchmark frames actually perform captures.
- Metadata v3 is exact, bounded, version-matched, and validated across CPU/GLSL, both banks, and all affected shader variants.
- The Bistro target uses bitset mode without fallback, and the old exclusion loop is absent from its active path.
- ABBA shows a qualifying target-pass or whole-frame win in Release and ShippingPerformance with no quality, temporal, memory, validation, or secondary-pass regression.
- Remaining automatic-planar cost is re-profiled and classified before any mesh-stage or dedicated-pipeline follow-up begins.
- An evidence report records source commit/dirty patch, asset hashes, shader recipes/hashes, raw timings, telemetry, image metrics, validation results, and retained/rejected decisions.
