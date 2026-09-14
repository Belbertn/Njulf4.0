# Reflection grid diagnosis — 2026-09-14

Source: `cc35bcc6fc69854dd96e316cd6e14ec69b139e8e`, dirty working tree with
existing DDGI, optical denoising, classifier and showcase changes. No production
code changed in this investigation.

Evidence: user image and `performance-20260914-005549-3258398-d5b606d4fe774bb98bd339bc01987f39.json`.
Device: NVIDIA GeForce RTX 3060 Laptop GPU. MaterialShowcase image, 1600×900,
Adaptive HybridRayQuery, valid history, no reset/source invalidation,
TemporalSampleIndex 14552. The capture's loaded-model name is BistroInterior;
the image and 34 submitted objects identify the showcase rather than that full model.

## Findings

The leading cause is sparse opaque reflection coverage exceeding the lifetime
of the reconstruction history. The source contains a concrete coverage defect:

- `hybrid_reflection_compute.glsl:268` demotes low-importance rays. The showcase's
  untextured BrushedMetal has roughness 0.42 and F0 about 0.72: its maximum
  importance is `0.72 * (1 - 0.42)^4 = 0.08148`, below 0.12. Its nominal 2×2
  tier is therefore demoted to 4×4 even at full specular visibility.
- `hybrid_reflection_ssr.comp:553` chooses `HybridHash(frame) % 16` globally.
  This samples one common pixel position in every 4×4 block, with replacement;
  it does not guarantee all positions receive an observation within 16 frames.
- `hybrid_reflection_temporal.comp:256` limits stationary sparse history to
  TemporalHistoryLength (default 16); movement tightens that limit to at most two.
  At line 265, expired history is discarded and current analytic fallback wins.
  Thus neighboring sampling lanes alternate between geometric reflection and
  fallback on a regular screen-space lattice.
- `hybrid_reflection_spatial.comp:109` gates filtering per 8×8 tile on temporal
  variance. Missing sample coverage is not part of that decision. Carried history
  preserves its variance, so spatial disagreement between stale/fallback lanes
  need not trigger filtering. This can leave the sampling grid visible.

Replaying the base-lobe phase function at sample 14552 gives last-visit ages
for lanes 0–15 of `[21,16,9,5,1,3,4,7,12,2,65,28,0,18,55,43]`.
Six lanes have not been selected for more than the default 16-frame history
window, before any ray admission rejection or surface/history rejection.
The corresponding 2×2 ages are `[0,2,4,5]`.
This replay assumes consecutive sample indices; it establishes the scheduling
defect, not exact per-pixel contents in the supplied image.

The capture further reports 41,728 estimated opaque ray requests, 9,601 actual
queries, capacity 11,250, and zero overflow. Admission happens before append,
so zero overflow does not mean every request was served. Request counters are
hash-subsampled/scaled; their ratio is approximate. Tile counts: 182 full rate,
2,465 half rate, 1,506 quarter rate, 15,247 analytic, one reuse tile. The prior
classifier-freezing defect is therefore not the primary explanation here.

OpticalDenoisingActive is true, but that pass exports transparent layers and does
not reconstruct the opaque brushed-metal spheres. Its 10,621 overflow pixels
can explain separate residual transparent noise, not this opaque coverage defect.

## Decision, validation and limitations

Diagnosis only. Corrective direction: guarantee bounded sampling coverage,
decorrelate the phase between screen blocks, and make reconstruction aware of
missing observations rather than treating low temporal variance as convergence.
Opaque ray admission also needs to fit the history/coverage budget. Simply
raising the transparent budget or enabling optical filtering cannot repair this.

Validation: source trace and deterministic uint32 scheduler replay. No GPU A/B
capture or production change was made, so the image attribution remains a strong
source-supported diagnosis, not a visually validated fix. No baseline/candidate
timing series or tail-latency measurements were collected; the supplied opaque
reflection pass timing fields are zero and cannot establish performance cost.
Only this compact record was generated. Original user attachments remain in place.

Reproduce the schedule with Python (no renderer or file output required):

```python
def h(v):
    v = ((v ^ (v >> 16)) * 0x7feb352d) & 0xffffffff
    v = ((v ^ (v >> 15)) * 0x846ca68b) & 0xffffffff
    return v ^ (v >> 16)

for tier in (2, 4):
    print(tier, [next(age for age in range(1000)
                     if h(14552 - age) % (tier * tier) == lane)
                 for lane in range(tier * tier)])
```
