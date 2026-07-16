## Updated diagnosis

The stability work succeeded. The diagnostic now shows fixed render scale, valid SSGI history, all 320 DDGI probes updating, and relocation/classification disabled. The four screenshots appear to be **Disabled → SSGI → DDGI → Hybrid**, matching the mode sequence in the log. 

What remains is primarily deterministic shading correctness rather than temporal instability.

### 1. The validation boxes have invalid vertex normals

This is the largest immediate problem.

`AddValidationBox` uses `GetTreeTrunkMesh()`. That mesh has only eight shared cube vertices. The four `z = -0.5` vertices are assigned `+Z` normals, while their geometric face points toward `-Z`; the `z = +0.5` vertices have the opposite error. Those same eight vertices are reused for the top, bottom, left and right faces, so those faces interpolate between unrelated `+Z` and `-Z` normals.

This directly explains the large triangular and jagged regions on the boxes:

* Forward shading receives incorrect interpolated normals.
* SSGI traces its hemisphere around those incorrect normals.
* DDGI samples its directional irradiance using those incorrect normals.
* DDGI hit shading interpolates the same bad mesh normals.
* On a side face, the interpolated normal can approach zero between `+Z` and `-Z`; the DDGI shader then abruptly switches to its geometric-normal fallback, producing a discontinuity.

Create a separate flat-shaded validation cube with **24 vertices: four independent vertices per face**, each with the correct outward normal and tangent. Do not reuse `GetTreeTrunkMesh()` for the Cornell boxes.

A quick confirmation is to run `MaterialDebugView.WorldNormal`. Each box face must be one uniform color. The current mesh should show gradients, inversions or triangle splits.

---

### 2. GI sampling still uses the two-frame resource index as its random sequence

`_currentFrame` cycles only between 0 and 1:

```csharp
_currentFrame = (_currentFrame + 1) % FramesInFlight;
```

That value is passed into every render-graph pass.  Both SSGI and DDGI then put it into their shader `FrameIndex`.

Consequences:

* SSGI alternates between only **two ray-jitter patterns**, so temporal accumulation can never converge beyond those two patterns.
* DDGI has a 16×16 visibility atlas, or 256 directions, with 96 rays per update. Frame slot 0 traces directions 0–95 and slot 1 traces 96–191. Directions 192–255 are never refreshed and retain their initialized “fully visible” moments.
* The directional irradiance field also alternates between only two angular subsets.

This strongly explains why SSGI is now stable but remains grainy, and why DDGI has stable directional bands.

Add a monotonically increasing temporal sequence that is completely separate from frame-in-flight resource indexing:

```csharp
private uint _temporalSampleIndex;

// Once per successfully rendered frame:
sceneData.TemporalSampleIndex = _temporalSampleIndex++;
```

Then use:

```csharp
FrameIndex = sceneData.TemporalSampleIndex;
```

in `SsgiTracePass` and `DdgiUpdatePass`. Keep `sceneData.CurrentFrameIndex` or `_currentFrame` exclusively for double-buffered GPU resources.

---

### 3. The octahedral encoder aliases `+Z` and `-Z`

The current encoder folds the negative hemisphere using GLSL `sign()`:

```glsl
encoded = (1.0 - abs(encoded.yx)) * sign(encoded.xy);
```

For `(0, 0, -1)`, `encoded.xy` is zero and `sign(0)` returns zero. The result maps to `(0.5, 0.5)`, exactly the same atlas location as `(0, 0, +1)`. Opposite directions therefore become indistinguishable at the poles.

Use sign-not-zero in both the encoder and decoder, in both DDGI shaders:

```glsl
vec2 SignNotZero(vec2 value)
{
    return vec2(
        value.x >= 0.0 ? 1.0 : -1.0,
        value.y >= 0.0 ? 1.0 : -1.0);
}

vec2 OctahedralEncode(vec3 direction)
{
    vec3 n = direction /
        max(abs(direction.x) + abs(direction.y) + abs(direction.z), 0.0001);

    vec2 encoded = n.xy;
    if (n.z < 0.0)
        encoded = (1.0 - abs(encoded.yx)) * SignNotZero(encoded);

    return encoded * 0.5 + 0.5;
}
```

The atlas also needs proper octahedral seam handling. Clamping a bilinear footprint at an atlas edge is not equivalent to wrapping across the octahedral fold. A conventional solution is a one-texel replicated border around each probe tile.

---

### 4. DDGI visibility is still nearest-neighbour sampled

Irradiance is now bilinearly filtered, but visibility moments are selected from a single 16×16 texel.

As the direction from a probe to a surface moves across a texel boundary, the visibility term can jump abruptly. Because visibility participates directly in probe weighting, this produces polygonal or jagged masks even when irradiance is smooth.

Bilinearly sample the four visibility moment texels first, with octahedral seam remapping, and then run the variance visibility test on the interpolated moments.

The probe weighting also contains a hard discontinuity:

```glsl
float normalWeight =
    clamp(dot(normal, toProbe / distanceToProbe), 0.0, 1.0);
```

Once the box normals are fixed, replace that with a wrapped weight rather than completely rejecting probes behind the normal plane:

```glsl
float alignment = dot(normal, pointToProbeDirection);
float normalWeight = pow(max(0.05, alignment * 0.5 + 0.5), 2.0);
```

Also evaluate probe visibility from a biased receiver position, not the exact surface point:

```glsl
vec3 biasedPosition =
    worldPosition +
    normal * normalBias +
    viewDirection * viewBias;
```

---

### 5. SSGI temporal responsiveness is backwards for low-confidence samples

The current temporal shader increases current-frame weight toward `0.75` as confidence decreases:

```glsl
response = mix(response, 0.75, 1.0 - clamp(current.a, 0.0, 1.0));
```

For a confidence of `0.1`, a configured responsiveness of `0.12` becomes approximately `0.69`. The result is therefore mostly the current noisy sample rather than accumulated history.

Reverse that relationship:

```glsl
float baseResponse = clamp(pc.ReprojectionParams.x, 0.02, 1.0);
float confidence = clamp(current.a, 0.0, 1.0);

float response = mix(0.02, baseResponse, confidence);
response = mix(response, 0.75, smoothstep(0.25, 4.0, velocityPixels));
response = mix(response, 0.55, smoothstep(0.02, 0.25, localContrast));
```

The current history clamp also uses the raw current-frame 3×3 minimum and maximum. With sparse stochastic hits, that neighbourhood frequently contains mostly zero plus one bright sample, so valid accumulated history is repeatedly clipped. Replace the min/max clamp with variance clipping based on neighbourhood mean and variance.

Also note that `rejected=0` in the diagnostic is not a real per-pixel rejection count. The CPU currently reports the complete image size when history is globally invalid and zero whenever it is globally valid.

---

### 6. SSGI confidence is still effectively applied twice

The trace shader already premultiplies sampled scene radiance by hit confidence:

```glsl
radiance = FetchSceneColor(uv) * confidence;
```

The denoiser then gives the result another confidence-derived weight.  Low-confidence radiance is therefore suppressed twice while isolated high-confidence rays dominate, producing the visible speckle pattern.

The smallest correction is to remove `confidenceWeight` from the denoiser spatial weight. The more robust approach is to temporally accumulate:

```text
premultiplied radiance sum
statistical sample weight
```

and divide only when resolving the filtered result.

---

### 7. DDGI still injects artificial light at every hit

The updated hit shader now retrieves real normals, albedo and emissive values and traces light visibility, which is a good improvement. However, every hit still uses:

```glsl
radiance =
    surfaceEmissive +
    max(directDiffuse,
        bounceTint * surfaceAlbedo * max(rangeWeight, 0.05));
```

That `max()` imposes a minimum colored radiance on shadowed surfaces. It produces color bleeding even when a hit receives no direct or emissive light. Environment fallback should be used for ray misses, not as minimum radiance at an opaque hit.

Use:

```glsl
radiance = surfaceEmissive + directDiffuse;
```

until recursive/multi-bounce sampling is implemented.

The energy units also need to be made consistent:

* Hit shading already applies the hit material’s `albedo / PI`.
* Probe convolution currently divides by accumulated cosine weight rather than estimating a solid-angle integral.
* Receiver shading multiplies by receiver albedo without `/ PI`.

Choose one contract:

```text
Probe atlas stores irradiance E:
receiver outgoing diffuse = E × receiverAlbedo / PI
```

For approximately uniform sphere rays:

```glsl
irradiance =
    (4.0 * PI / float(rayCount)) *
    sum(rayRadiance * max(dot(rayDirection, normal), 0.0));
```

A spherical-Fibonacci or another equal-area ray sequence is preferable to treating a uniformly spaced octahedral grid as equal-solid-angle samples.

---

## Direct-shadow mismatch

The visible ceiling fixture remains a rectangular emissive panel at approximately `y = 3.96`, while the actual shadow-producing light is a point light at `y = 3.35`.

Therefore, even after GI is corrected, the hard point-light shadows will not visually originate from the apparent rectangular emitter. SSGI and DDGI do not replace the point-shadow cubemap.

Either implement a rectangular light, or make the visible fixture represent the actual point source. Moving the point light to the panel centre only aligns its apparent origin; it will still produce point-source rather than area-light shadows.

## Patch order

1. Replace the validation-box mesh with a 24-vertex flat-shaded cube.
2. Introduce a monotonic temporal sample index.
3. Fix octahedral sign handling and visibility filtering/seams.
4. Correct the low-confidence SSGI temporal response and confidence weighting.
5. Remove the DDGI hit `bounceTint` floor and normalize the irradiance units.
6. Re-evaluate probe weighting and receiver bias.

The first two changes should remove most of the triangular DDGI regions and persistent SSGI grain before any additional ray-count or hysteresis tuning.


You fixed the main flicker source: the repo now clears DDGI persistent buffers and initializes the visibility atlas on resource/signature changes. That explains why the pattern is stable now.

The remaining incorrectness looks like **lighting-model / validation setup**, not buffer corruption.

## The biggest remaining bug: DDGI still rejects backface hits

In `ddgi_update.comp`, you now resolve the actual hit surface material and normal, which is a good step. The hit normal is transformed and flipped against the ray direction.

But immediately before that, the shader still does this:

```glsl
if (!frontFace)
{
    radiance = vec3(0.0);
    return;
}
```

For a Cornell room made of thin single-sided quads, this is very likely wrong. A DDGI probe inside the room can hit the “back” side of a wall depending on mesh winding, transform handedness and which side the quad considers front-facing. You already have enough information to shade the surface by resolving and flipping the actual normal, so the ray-query `frontFace` flag should not decide whether the hit contributes radiance.

Change the DDGI ray hit path to shade both sides:

```glsl
// Do not early-out on !frontFace.
// Use it only for debug/classification metrics if needed.
backface = frontFace ? 0.0 : 1.0;

vec3 hitPosition = origin + direction * hitT;
vec3 surfaceNormal;
vec3 surfaceAlbedo;
vec3 surfaceEmissive;

ResolveCommittedHitSurface(
    instanceIndex,
    primitiveIndex,
    barycentrics,
    direction,
    surfaceNormal,
    surfaceAlbedo,
    surfaceEmissive);
```

Then use `surfaceNormal` for light visibility and diffuse. This should reduce the black/incorrect blocky patches on the boxes and wall-adjacent areas.

## Second DDGI issue: every hit gets artificial sky bounce

The current DDGI hit radiance is:

```glsl
radiance = surfaceEmissive + max(directDiffuse, bounceTint * surfaceAlbedo * max(rangeWeight, 0.05));
```

That `max()` is a large non-physical fallback. Even if the hit point is shadowed, the probe still receives environment-colored bounce. In a mostly closed Cornell box, this will wash out the image, brighten occluded areas and make the indirect field look like a tinted overlay rather than a light transport result.

For the validation room, make it strict:

```glsl
radiance = surfaceEmissive + directDiffuse;
```

Only add a fallback for **miss rays**, not hit rays. Miss rays already return environment radiance.

Once this is correct, add multi-bounce by sampling previous DDGI at the hit point or by injecting emissive/sky through deliberate rules, not by `max()`.

## Your screenshots are not isolated GI tests yet

The diagnostic still shows a lot of final-frame modifiers enabled: fog, SSAO, bloom, auto exposure, procedural sky, environment diffuse/specular and reflection probes. It also shows `RayQueryHybrid` running with both SSGI and DDGI active, not DDGI alone. 

For debugging “is GI correct,” use a hard isolated preset:

```csharp
renderer.Settings.Fog.Mode = FogMode.Disabled;
renderer.Settings.Fog.Enabled = false;

renderer.Settings.Bloom.Enabled = false;
renderer.Settings.AutoExposure.Enabled = false;

renderer.Settings.AmbientOcclusion.Enabled = false;

renderer.Settings.Environment.Enabled = false;
// or at least:
renderer.Settings.Environment.DiffuseIntensity = 0.0f;
renderer.Settings.Environment.SpecularIntensity = 0.0f;
renderer.Settings.Environment.SkyIntensity = 0.0f;

renderer.Settings.Reflections.Enabled = false;
renderer.Settings.DynamicResolution.Enabled = false;

renderer.Settings.GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
renderer.Settings.GlobalIllumination.UseSsgi = false;
renderer.Settings.GlobalIllumination.UseDdgi = true;
renderer.Settings.GlobalIllumination.UseRayQueryBackend = true;
```

Then test in this order:

1. Direct lighting only.
2. DDGI irradiance debug.
3. DDGI visibility debug.
4. DDGI final indirect.
5. Final frame with only direct + DDGI.

## RayQueryHybrid now means SSGI + DDGI

Your current `EffectiveUseSsgi` now includes `RayQueryHybrid`, and `EffectiveUseDdgi` also includes `RayQueryHybrid`, so that mode runs both paths.

That is fine for the final hybrid mode, but it is bad for diagnosing DDGI correctness. A bad SSGI sample or temporal history can hide DDGI behavior. Use `GlobalIlluminationMode.Ddgi` plus `UseSsgi = false` until DDGI is visually plausible.

Also note that your diagnostics show some mode switches producing transient invalid states, for example SSGI enabled with `ssgiSize=0x0` or DDGI enabled with `ddgiProbes=0/0` in the mode-cycle log. Wait a few frames after toggling, or explicitly reset/rebuild GI resources on mode changes before judging screenshots. 

## SSGI is improved, but still noisy because radiance is confidence-weighted at trace time

In `ssgi_trace.comp`, the hit radiance is still multiplied by confidence:

```glsl
radiance = FetchSceneColor(uv) * confidence;
```

and the final stored RGB is the average of those already-weighted samples.

That is not necessarily wrong, but it means the SSGI RGB is not “radiance”; it is “radiance × confidence.” Your hybrid combine therefore has to treat it differently from DDGI. The current forward combine adds `nearField = ssgiSample.diffuse` directly, while confidence only reduces fallback weight.

That is okay only if the SSGI buffer remains confidence-weighted. If you later change SSGI to store unweighted radiance plus alpha confidence, then the forward combine must become:

```glsl
nearField = ssgiSample.diffuse * ssgiConfidence;
```

For now, the noise in the SSGI screenshots is expected with only six half-res rays unless temporal accumulation is doing most of the work.

## One more practical rendering issue: the visible light is still an area emitter, but shadows are from a point light

The scene still appears to show a large ceiling light, but diagnostics show one selected point shadow with a 512 cubemap and no directional shadow. 

So the direct shadows will look like point-light shadows, not Cornell-box area-light shadows. That is fine as a test, but it will not match the visual expectation set by the ceiling panel.

## Next patches I would make

1. Remove the DDGI `!frontFace` radiance early-out.
2. Replace DDGI hit `max(directDiffuse, bounceTint...)` with `surfaceEmissive + directDiffuse`.
3. Run DDGI-only with fog/AO/bloom/auto-exposure/environment/reflections off.
4. Confirm `DdgiIrradiance`, `DdgiVisibility`, and `DdgiProbeState` debug views before testing hybrid.
5. Only after DDGI is correct, re-enable SSGI in `RayQueryHybrid`.

The first two shader changes are the highest impact. They should make the DDGI image stop behaving like a tinted overlay and start behaving like actual bounced direct light.
