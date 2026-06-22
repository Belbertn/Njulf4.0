## Main findings

Your mode order is **SSGI → DDGI → Hybrid → RayQueryHybrid**. The last two currently select the same effective SSGI+ray-query-DDGI configuration, so Third and Fourth should look almost identical.

### DDGI: the horizontal line is the probe-volume boundary

The probe lattice spans:

```text
X: -2.8 …  2.8
Y:  0.2 …  3.8
Z: -8.3 … -2.7
```

The room spans approximately:

```text
X: -3.0 …  3.0
Y:  0.0 …  4.0
Z: -8.5 … -2.5
```

Both boxes begin at `Y=0`, but DDGI sampling rejects every receiver below `Y=0.2`. The lower 20 cm therefore uses environment IBL while the portion above it uses DDGI. That is exactly the horizontal strip visible at the bottom of both boxes; it is not a point-light shadow. The floor, ceiling, side walls and back wall are also just outside the DDGI sampling bounds.

The final shader switches between fallback IBL and DDGI without a volume-edge fade, making the boundary obvious.

Keep the probes inset, but expand the **influence bounds** by half a probe cell:

```glsl
vec3 latticeMax = origin + spacing * vec3(probeCounts - uvec3(1u));
vec3 influenceMin = origin - spacing * 0.5;
vec3 influenceMax = latticeMax + spacing * 0.5;

if (any(lessThan(worldPosition, influenceMin)) ||
    any(greaterThan(worldPosition, influenceMax)))
{
    continue;
}

vec3 gridPosition = clamp(
    (worldPosition - origin) / spacing,
    vec3(0.0),
    vec3(probeCounts - uvec3(1u)));
```

With the current spacing of `(0.8, 0.9, 0.8)`, this covers the actual room surfaces while keeping probe positions away from them. Add a short `smoothstep` fade at the influence edge rather than switching directly to zero coverage.

### DDGI: several probes are inside the boxes

Plugging the authored grid into the two oriented-box bounds gives six probes inside solid geometry:

```text
Tall box:
(-1.2, 0.2, -6.7)
(-1.2, 1.1, -6.7)
(-1.2, 0.2, -5.9)
(-1.2, 1.1, -5.9)

Short box:
( 1.2, 0.2, -5.1)
( 1.2, 0.2, -4.3)
```

The diagnostic confirms relocation and classification are both zero.  The update pass also sends only `EnabledFlag`; its classification and relocation shader paths can therefore never activate.

Those interior probes generate invalid visibility fields and strongly influence nearby box faces. They are the most likely source of the angular/fan-shaped DDGI contribution.

Re-enable **classification only** first. Keep relocation disabled until classification is stable. Classification should be immediate on a probe’s first update and use a hysteretic score afterward, rather than gradually blending `activeProbe` between zero and one.

## Why SSGI is still noisy

The log shows only six rays per pixel at 800×450, although temporal history is valid.  Six rays can work, but several shader choices prevent useful convergence.

### The hit-normal threshold is far too strict

The same `NormalRejectionThreshold = 0.85` used for temporal history rejection is also passed to the SSGI ray tracer. Rays are discarded unless they hit a surface almost head-on.

This rejects most useful oblique wall and floor hits. Split this into separate settings:

```text
TemporalNormalThreshold:  0.85–0.95
SsgiHitNormalThreshold:   0.05–0.25
```

Because your hemisphere sampling is already cosine weighted, `originFacing` should not additionally attenuate the estimator. Use hit facing as a loose validity test, not as a strong energy weight.

### The ray starts too far from the receiver

For the validation setting `MaxBounceDistance=10`:

```text
thickness     = 10 × 0.0125 = 0.125 m
startDistance = max(2 × 0.125, 10 × 0.04) = 0.4 m
```

The origin is also displaced by `0.125 m`. This is a very large exclusion region for the box-floor contacts.

SSGI and DDGI should not share one distance control. For this room, test:

```text
SsgiMaxDistance = 2–3 m
SsgiThickness   = 0.02–0.05 m
startDistance   = about 1.5 × thickness
```

### Temporal accumulation keeps injecting current-frame noise

The temporal pass computes local contrast from all nine raw samples, including zero-confidence misses as black. High noisy contrast then drives the current-frame response toward `0.55`, overriding the configured `0.12` responsiveness.

That gives the static image only a few frames of effective accumulation.

Use confidence-weighted neighborhood statistics and remove the radiance-contrast response boost. Motion, geometry disocclusion and camera cuts should increase responsiveness; raw Monte Carlo contrast should not.

The reported `rejected=0` is also not a real per-pixel rejection count. It is hardcoded to zero whenever history is globally valid.

### The upsampler mixes across geometry before checking depth

`FetchSsgi`, `FetchDepth`, and `FetchNormal` all use filtered `texture()` sampling, and the screen sampler is linear. Consequently, floor and box values are blended before the bilateral weights are calculated. This produces contact halos and the strange shadow-like region near the ground.

Implement a true joint bilateral upsample:

1. Fetch the four surrounding half-resolution SSGI texels with `texelFetch`.
2. Fetch representative depth and normal for each source texel without linear filtering.
3. Compare **linear view depth**, not raw reverse-Z depth.
4. Weight and combine only after those tests.

The current temporal and denoise passes compare nonlinear device depth directly, using fixed thresholds. That is not stable across distance.

### Low-confidence conditional radiance is spatially over-weighted

The trace now writes conditional mean radiance plus confidence. That representation is valid before filtering, but the denoiser gives every nonzero-confidence RGB sample the same color weight. A bright one-ray hit can therefore affect its neighborhood as strongly as a well-sampled result.

Either use confidence-weighted normalized filtering:

```glsl
float w = bilateralWeight * ssgi.a;
colorSum += ssgi.rgb * w;
confidenceWeightSum += w;
```

or store premultiplied expected radiance from the trace:

```glsl
rgb = accumulatedRadiance / float(rayCount);
alpha = accumulatedConfidence / float(rayCount);
```

Then filter RGB directly and do not multiply it by alpha again in the forward pass.

## Why SSGI creates a dark “shadow”

The hybrid combiner subtracts up to 25% of environment fallback merely because SSGI found a hit:

```glsl
fallbackWeight =
    1.0 - ddgiFieldCoverage - ssgiConfidence * 0.25;
```

Near the box-floor contact, SSGI often hits floor pixels lying in the direct point-light shadow. Their radiance is low but confidence is high, so fallback IBL is removed and little light is added. The result looks like an erroneous extra shadow.

Do not use SSGI hit confidence as an occlusion value. In SSGI-only mode, retain full fallback IBL and add the positive SSGI contribution. In hybrid mode, blend SSGI against the DDGI/world result, but do not darken it solely because a screen-space ray hit something.

## Secondary DDGI problems

The current maximum DDGI ray distance is only the largest volume axis, `5.6 m`. The volume diagonal is about `8.7 m`, so some rays terminate before reaching a closed wall and are incorrectly treated as sky misses.

Use at least the volume diagonal for this test.

The visibility atlas is 16×16, but only 96 of its 256 directions are traced per update. The directions are deterministic texel centers, so visibility variance tends toward zero and produces hard Chebyshev-shaped masks.

After fixing the volume and interior probes, temporarily use `256` rays per probe. If the remaining angular shape disappears, replace the production sequence with jittered/equal-area directions and filtered visibility moments.

## Recommended patch order

1. Expand DDGI influence bounds by half a probe spacing and add an edge fade.
2. Enable stable probe classification; the six probes inside boxes must become inactive.
3. Remove the `ssgiConfidence * 0.25` fallback subtraction.
4. Lower and separate the SSGI hit-normal threshold.
5. Decouple SSGI distance/thickness from the 10 m DDGI distance.
6. Make temporal statistics confidence-aware and remove the `0.55` noisy-contrast response.
7. Replace linear SSGI upsampling with point-fetched, linear-depth-aware bilateral upsampling.
8. Increase DDGI max distance and test 256 rays per probe.

The DDGI horizontal line should disappear after step 1, while the angular box artifact should reduce substantially after step 2. Steps 3–7 address the SSGI ground halo and persistent grain.
