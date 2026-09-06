# Hybrid reflection and raw GTAO optimization — 2026-09-05

Retain the reflection temporal optimization. The DDGI specialization compiled
identically, the raw GTAO candidate was slower, and the resolve candidate did
not reach the agreed 3% improvement threshold. Those three production paths,
including the final fast GTAO spatial implementation, remain unchanged.

## Retained change

`hybrid_reflection_temporal.comp` computes the previous-frame projection and
depth tolerance once per receiver, shares it with camera-only reprojection,
rejects incompatible history metadata before fetching radiance/moments, and
reuses the center sample in the neighborhood loop. The four history taps,
3×3 neighborhood, validity thresholds, confidence, sparse-history rules,
variance, and output layouts are preserved.

Fresh Release SPIR-V shrank from 33,192 to 31,544 bytes; static `OpDot` sites
dropped from 16 to 12. Texture/image instruction-site counts are unchanged:
the fetch change is conditional execution, not a claim of fewer static sites.
The production-compiled temporal module exactly matches the tested module:
`1aaae00184c2a747581a9b7226ea74af556d6274312b32083967cb1e40adde64` (SHA-256).

## Measurements and decisions

Release, RTX 3060 Laptop, capture-reported driver 610.248.0, Sponza High camera, DDGI High,
1920×1080, full-resolution GTAO, VSync off, async compute disabled. Each timing
run used 120 warmup and 120 measured frames, plus the same 352 settling frames.
All runs settled and reported comparable production timing; settings
fingerprints match. The earlier 900p Nsight traces are prioritization evidence,
not this baseline.

| Candidate | Target mean before → after | Target delta | GPU frame mean before → after | Decision |
| --- | --- | --- | --- | --- |
| DDGI directional-result specialization | Identical optimized module | No compiled work removed | No redundant runtime trial | Discard |
| Raw GTAO repeated-texel suppression | 4.4833 → 4.9972 ms | +11.46% | 21.9133 → 22.3323 ms | Reject |
| Resolve optional-input loads | 0.5328 → 0.5214 ms | −2.14% | 21.9133 → 21.8604 ms | Inconclusive; discard |
| Temporal projection/load reuse, isolated trial | 0.4853 → 0.4583 ms | −5.58% | 21.9133 → 21.9653 ms | Keep |
| Temporal, final embedded Release artifact | 0.4853 → 0.4660 ms | −3.98% | 21.9133 → 21.7574 ms | Confirmed |

Temporal p95 improved from 0.494 to 0.468 ms in the isolated trial and 0.470 ms
in the final embedded capture. The isolated reflection-stack sum was
3.7113 → 3.6314 ms. Whole-frame timing was effectively neutral (+0.24% in the
trial, −0.71% in the final capture); this is a local pass improvement, not a
whole-frame speedup claim. Timing changes in unchanged passes are not attributed
to this shader edit.

The benchmark exports raw GTAO under `AmbientOcclusionPass`; with effective
GTAO mode verified, the SSAO contribution is zero. `AmbientOcclusionBlurPass`
contains GTAO temporal plus spatial filtering. No gain is attributed to those
unchanged passes.

## Focused correctness evidence

- All candidate shaders were freshly compiled and passed `spirv-val`.
- Temporal static HDR relative RMSE: 0.00007010; FLIP p95: 0.002627.
- One paired 300-frame Sponza horizontal route captured all 12 checkpoints.
  Both routes passed capture validation, started at frame 1323 after 1202
  settling frames, and matched camera, scene, settings, and frame identities.
- Independent comparison of those checkpoints passed the unchanged 0.005
  relative-RMSE ceiling: maximum 0.001895. The maximum adjacent-frame difference
  residual, normalized by reference-frame RMS, was 0.001653 across eight pairs.
  Static and worst-checkpoint images were also visually inspected.
- Final integrated Release build passed with zero warnings/errors, including
  the repository's required production DDGI shader-contract checks.
- Embedded temporal bytes exactly match the tested candidate. Embedded raw
  GTAO and resolve bytes exactly match their original compiled modules.
- One final capture without overrides settled, matched baseline settings,
  and passed HDR comparison (relative RMSE 0.00006200, FLIP p95 0.004499).

No new test framework, source-text assertions, quality reductions, or interface
changes were introduced. Motion captures used the existing standalone quality
route exporter; numerical comparison was performed directly on its validated
PFM checkpoints, not presented as a full campaign/ROI qualification.

## Artifact identity and limits

The DDGI experiment suppressed unused diffuse accumulation while retaining atlas
alpha validity, visibility and ownership. Baseline, specialized, and fresh
production modules all hash to
`db2d6e11027bad6e53342342ec8152ea411dab8cf3bed174cfbf01ab111523a3`
after debug stripping and ID compaction. Enabling the existing directional-only
receiver flag would additionally bypass atlas validity and was deliberately
not used.

The saved Nsight export provides raw shader-sampling data and encoded native
instruction blobs, but no decoded per-instruction stall attribution through the
available CLI. No hardware stall cause is claimed for raw GTAO. Its proposed
duplicate suppression was rejected on actual runtime cost; further stall
profiling remains outstanding.

The raw AO experiment skipped consecutive samples only when they resolved to
the same source texel in the same signed horizon search. It retained the
sampling pattern and horizon maximum. Compiled image-fetch sites stayed at
seven, while the module grew from 23,464 to 23,884 bytes; the added control flow
did not pay for itself in the runtime trial. This is not a new spatial-pass
shared-load opportunity.

Isolated runtime trials used the existing shader override loader. Its runtime
names end in `.spv`, while the capture fingerprint enumerates embedded names
without that suffix. Consequently the early override trial reports have the
same aggregate fingerprint despite different effective modules. Exact trial
SPIR-V files and hashes are retained separately; Vulkan pipeline-binary keys
are obtained from the actual shader-module create info. The motion candidate
also supplies the matching embedded-name alias, giving a distinct, correct
effective fingerprint. No capture metadata or renderer telemetry was modified.
The final timing result uses the rebuilt embedded shader with no overrides;
its reported shader fingerprint is
`e5cf0116bff5ecd27148d7ce3b2b1dc2b2ec55fb9be27aee410f83a99c4b02bf`.
`final-embedded-identities.json` independently records the exact shader hashes.

Artifacts, commands, comparisons and logs are in
[the local evidence directory](../.perf-loop-runs/reflection-ao-20260905/).
`ddgi-experiment`, `compile.py`, `run-capture.ps1`, `trial-shader-identities.json`,
`run-quality.ps1` and `compare-motion.py` preserve the experiment inputs and
checks. The prematurely launched AO capture and the failed quality-harness
setup runs are excluded from all comparisons.
