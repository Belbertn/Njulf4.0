# Advanced GI experiment status

This document records implementation boundaries for the B1, C1, C3, C4, and
C5 work described by
`RemainingAdvancedGlobalIlluminationProductionImplementationPlan-20260809.md`.
It is deliberately an implementation-status document, not qualification
evidence.

## Current rollout state

The production code paths for B1, C1, C3, C4, and the bounded C5 prototype are
present. New settings request B1 `ExactCompacted`, C1
`ExtFourStateExperiment`, C3 `PerProbeHistogramExperiment`, C4
`WorldCacheExperiment`, and C5 `HiZHalfResolutionExperiment` by default.
Requested mode is only persisted intent: capability, prerequisite, content,
memory, and resource-completeness gates remain authoritative, and
`AutoQualified` remains evidence-gated. Existing saved settings retain their
persisted mode. C2/SER is intentionally excluded and owns no runtime code or
resources under this plan.

Requested, supported, admitted, effective, and qualified modes are separate.
The editor shows those states together with live resource/publication status,
and performance snapshot schema 10 persists them with exact central-memory
ownership. A requested setting, compiled shader, successful unit test, or
advertised extension is never treated as target-device qualification.

No repository artifact fabricates the remaining hardware evidence. Until a
complete prerequisite and per-feature qualification manifest is supplied for
the exact device, driver, shader bundle, content, source ABI, and profile, the
corresponding automatic promotion stays fail-closed.

## Startup and evidence loading

Advanced-GI selection happens before `VulkanRenderer.Initialize()` because the
effective modes determine immutable render-graph resources, shader variants,
descriptor pressure, and optional Vulkan device features. Engine clients set
the requested modes and qualification IDs on
`RenderingOptions.InitialSettings.GlobalIllumination`. The sample provides
equivalent `--simple-ddgi-receiver-feedback-mode`,
`--ddgi-opacity-micromap-mode`,
`--simple-ddgi-directional-guiding-mode`, `--gi-caustic-mode`, and
`--simple-ddgi-near-field-residual-mode` arguments plus matching qualification
ID arguments and environment variables documented in
`RendererSettingsReference.md`.

The startup evidence sources are applied in this order:

1. `AdvancedGiStartupProfileCodec`, when configured, atomically selects the
   content-addressed render settings and all remaining evidence paths before
   Vulkan device creation.
2. `AdvancedGiPrerequisiteManifestCodec` freezes the prerequisite contracts and
   corpus identity.
3. `AdvancedGiQualificationManifestCodec` authenticates the exact
   device/driver/shader/settings qualification records.
4. `AdvancedGiRuntimeEvidenceBundleCodec` supplies the scene-, source-,
   profile-, and layout-bound C4/C5 evidence that cannot be represented by the
   common device manifest.
5. `RenderingOptions.ConfigureAdvancedGiEvidence`, when supplied, may install
   application-owned strongly typed C4/C5 evidence under the same validators.

The ImGui editor exposes a detached next-start activation draft with per-input
preflight, atomic save/readback, and a full renderer/device/window restart. The
same transaction is available through `--advanced-gi-startup-profile`. Corpus
pinning and headless startup/qualification verification are provided by
`Njulf.AssetTool advanced-gi`; see
[`advanced-gi-activation.md`](advanced-gi-activation.md).

The runtime bundle is schema-versioned, bounded to 512 KiB, rejects comments,
trailing commas, duplicate or unknown properties, and must contain at least one
C4/C5 section. Loading recompiles the corresponding production plan and accepts
only an active plan with exact evidence bindings; successful JSON parsing alone
cannot enable a mode. File rejection atomically clears both file-provided C4/C5
records and records a startup-log reason while retaining canonical GI.

B1 is deliberately not represented by synthetic render-graph passes. Its real
candidate writes are recorded by the enabled forward, alpha/foliage,
transparent, particle, fog, capture, and refinement producers. The bounded
sort/reduce publication transaction runs after the final producer and feeds
only the next frame's scheduler. Its record, scratch, and summary buffers are
runtime-owned and fence-retired, avoiding duplicate graph/resource ownership.

## Implemented safety boundaries

| Area | Implemented boundary | Promotion still requires |
| --- | --- | --- |
| B1 exact receiver feedback | Real 48-byte all-producer capture; bounded local reservations; exact 32-byte V2 records; deterministic GPU radix/reduce; two publication banks; previous-frame scheduler binding; strict overflow/generation fallback; fence readback; per-stage timings and schema-10 counters/memory telemetry. | Equal-work transient-error, liveness, total-time, capacity, and long-run target-device evidence before declaring the path release-qualified or removing the legacy reference. |
| C1 opacity micromaps | Deterministic pinned NVIDIA CPU bake bridge; optional checksummed cooked payload; multi-submesh runtime partitioning; EXT device enablement; build/compaction; OMM-attached static-BLAS variants; cache/lease/retirement; ordinary-candidate fallback; lifecycle/content/memory diagnostics. | RTX 3060, Ada+, extension-disabled, and non-EXT same-ray/image conformance plus amortized total-GI performance evidence. KHR and SER remain out of scope. |
| C3 directional guiding | Equal-area hierarchy/PDF/MIS oracle; GPU-resident scheduler compaction into train/sample work; GPU train/build/sample/validate; double publication banks; compact status-last publication; central scratch; source-cache-owned slot-203 direction/PDF sidecar; generation-time PDF propagation into trace/projection/audits; maintenance rays; readback/statistical qualification contracts. | Archived multi-seed convergence, quality-per-time, cache-pressure, long-run, and device-matrix evidence for automatic promotion. |
| C4 caustics | Authored hero validation; analytic/path-reference contracts; tagged light/receiver producer; ray-query trace; deterministic radix/bottom-K world cache; coherent two-bank publication; screen resolve/composite; resize/revision/fence handling; isolated memory and diagnostics. | Archived analytic/path-traced energy, motion/reload/origin, ordinary-content zero-work, total-time, and target-device qualification evidence. |
| C5 near-field residual | Evidence-bound post-B3 admission; dedicated direct-diffuse-plus-emissive opaque/masked MRT; bounded Hi-Z trace; typed ray/PDF and hit/source identity; banked history/normal/metadata; reset, temporal rejection/moments, filtering, composite, counters/timestamps, and schema-versioned diagnostics. | A signed go decision and archived post-B3 equal-cost reference captures proving error reduction, edge/motion stability, energy ownership, long-run memory, and target-device cost. |

All optional allocations are transactional and independently budgeted. A
disabled or rejected feature binds only safe fallback descriptors where the
global bindless ABI requires a slot; it owns zero feature buffers/images and
records no feature dispatches.

## Completion audit

The source audit against Phases 0–12 reached the following boundary on
2026-08-11:

| Plan phases | Source/runtime state | Evidence state |
| --- | --- | --- |
| Phase 0 | Frozen-contract codecs, stable identities, fail-closed prerequisite loading, and the C2 exclusion are implemented. | The complete reference corpus and signed prerequisite manifest still need to be frozen and archived. |
| Phases 1–11 | Shared admission/memory contracts and the B1, C1, C3, C4, and bounded C5 production paths, fallbacks, diagnostics, shaders, and deterministic tests are implemented. | Each feature still requires the plan's equal-work, rendered-reference, long-run, and target-device measurements before promotion. |
| Phase 12 | Integrated source validation, documentation, settings, schema migration, shader verification, and manifest authentication are implemented. | Ada-or-newer NVIDIA and non-NVIDIA/fallback runs, 30–60 minute traversals, supported feature combinations, and archived qualification IDs remain outstanding. |

This is the intended production handoff boundary. Product policy now requests
the completed B1/C1/C3/C4/C5 production experiments in new settings, while
admission and effective activation remain fail-closed. This default request
does not manufacture qualification evidence, authorize `AutoQualified`, or
weaken any fallback gate.

## Source-validation snapshot (2026-08-11)

The final local source validation used Release binaries and the primary RTX
3060 Laptop target:

- `dotnet build Njulf.sln -c Release --no-restore -m:1 /nodeReuse:false`
  completed with 0 warnings and 0 errors. The shader build verified 113
  production non-scheduler modules, including 97 modules with exact pinned
  functional atomic counts and 12 deliberately excluded bounded scheduler
  modules; the receiver/cache ABI verifiers also passed.
- The full managed/shader/asset suite completed with 2,590 passed, 0 failed,
  and 0 skipped after installing the audited native OMM bridge. The explicit
  Vulkan hardware gate was run separately and passed.
- The native C1 cooker bridge was built from NVIDIA OMM SDK 1.9.2 commit
  `9abacd0f187d0efca491946a29ba7df8c5345264` with
  `MSVC-19.38.33145.0`, static SDK linkage, fast math and OpenMP disabled, and
  Control Flow Guard validation. Its binary SHA-256 is
  `ff4e25cf274243e34aece5b123245b93a2463194f7a592605911d2857f7da763`.
  The real native bake and cooked four-state payload round trip passed.
- The authenticated raster/ray-query alpha-visibility gate passed on
  `NVIDIA GeForce RTX 3060 Laptop GPU` at distances 2, 4, and 8. The maximum
  absolute coverage difference was 0.957%, below the 2% gate, with zero Vulkan
  validation warnings or errors. The report SHA-256 is
  `43775e1f48e6467d1f85ae0eb3adad625b1ab7cb4ab580b40f4b06b152bba051`;
  its authenticated binary evidence SHA-256 is
  `ffbfc86ecf1f788e0a6ce8e280b656c375d55ad1bf5c98bddff0e85b0289aab9`.
- A 12-frame real Vulkan startup requested B1 `ExactCompacted` and every C
  feature's `AutoQualified` mode without supplying manifests. The health
  report passed with zero validation warnings/errors and zero GI errors;
  canonical DDGI stayed active, every advanced mode remained unadmitted and
  ineffective, and the complete advanced-GI allocation was 0 bytes. Its
  SHA-256 is
  `aa0e89cd407bbfde9f4d3747ed2208e742cf67d9b69d3f173d356c8e041de5cd`.
- A second 12-frame RTX Vulkan startup supplied no advanced-GI command-line or
  environment selectors and no evidence manifests. Its health options contain
  null overrides, while runtime diagnostics record the new defaults exactly:
  B1 `ExactCompacted`, C1 `ExtFourStateExperiment`, C3
  `PerProbeHistogramExperiment`, C4 `WorldCacheExperiment`, and C5
  `HiZHalfResolutionExperiment`. The report passed with zero Vulkan validation
  warnings/errors and zero GI errors. Every request failed closed at the
  missing frozen prerequisite, canonical DDGI remained active, and advanced-GI
  allocation stayed at 0 bytes. Its SHA-256 is
  `071eb7e3d17539ff141976bd0e4c379602db6ecf692902f64b75b3b30ebc729d`.
- The locally installed `AMD Radeon(TM) Graphics` ICD was isolated for a
  non-NVIDIA attempt, but it is not an eligible matrix device: baseline device
  selection rejected it before renderer initialization because it lacks
  `VK_EXT_mesh_shader` and `VK_KHR_deferred_host_operations`. No advanced-GI
  result is inferred from that rejection; a capable non-NVIDIA device remains
  outstanding.

The reproducible local artifacts are under
`artifacts/advanced-gi-source-validation-20260811/` and the latest final TRX is
under `TestResults/advanced-gi-defaults-final-pass/`. Both roots are intentionally
ignored build/evidence output. These checks close the locally executable source
gates; they are not substitutes for the remaining device matrix, reference
captures, equal-work measurements, or signed promotion records.

## Validation available in the repository

- Managed ABI, planner, corruption, migration, lifecycle, memory, and
  statistical tests cover the fail-closed contracts and CPU/GPU reference
  boundaries.
- Every checked-in advanced-GI compute shader is compiled as Vulkan 1.3 SPIR-V
  and validated by the shader build.
- Performance snapshots preserve C5, C1, C3, C4, and B1 telemetry through
  schema versions 5–10 with explicit legacy migrations that never infer live
  work from a mode bit.
- The Global Illumination editor panel exposes requested/effective mode,
  fallback, qualification ID, live bytes, runtime state, and whether the last
  observation is authoritative. Startup-only controls are staged separately,
  preflighted, saved, and applied only after a complete cold restart.

Rendered captures, 30–60 minute traversals, and the required physical device
matrix are release evidence, not unit-test fixtures. They must be generated on
the named hardware and attached to a manifest; they are intentionally not
claimed by this source implementation.

## Operator invariants

- DDGI remains the canonical diffuse irradiance/visibility field.
- B1 can influence only bounded scheduling priority; it never gates liveness,
  residency, visibility, or allocation.
- C1 falls back to the unchanged shader candidate path for every unsupported
  or ambiguous alpha case.
- C3 retains a nonzero uniform proposal and keeps slot 203 owned by the source
  cache rather than the guiding allocation.
- C4 flux/cache data never enters DDGI transport, source, irradiance, or
  publication resources.
- C5 reads only its validated direct-diffuse-plus-emissive attachment; invalid
  trace/history/source data composites exactly to canonical DDGI+B3.

## Evidence required before qualification or automatic admission

Each promotion record must bind the exact GPU/driver/toolchain, shader ABI,
source/profile/layout, content revision, and reference corpus. It must include
the feature-isolation captures, memory lifetime telemetry, P50/P95/P99 GPU
timings, fallback/overflow/non-finite counters, and quality comparison against
the plan's appropriate baseline. Any ABI, source, content, profile, or driver
change invalidates the record and returns the mode to the fail-closed path.
