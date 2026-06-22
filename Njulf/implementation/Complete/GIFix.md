This is not mainly a ray-count or denoiser-tuning problem. There are several correctness issues.

The three screenshots are produced by separate systems:

SSGI is screen-space ray marching.
DDGI is the only path using Vulkan ray queries.
Direct shadows are still rendered with a 512×512-per-face point-light cubemap, so neither GI mode changes the primary shadow solution.

Also, the validation scenario called RayQueryHybrid is currently DDGI-only: it explicitly sets UseSsgi = false, and EffectiveUseSsgi does not include RayQueryHybrid.

DDGI flickering and block patterns
1. The persistent DDGI buffers appear to be used without initialization

DdgiProbeVolumeManager allocates persistent buffers for probe states, relocation, irradiance and visibility, but the upload path initializes only the volume metadata. The generic device-buffer allocator does not clear newly allocated memory.

The shader then immediately reads old values:

Visibility atlas values are read and blended with the new value.
Probe irradiance state is read and blended using hysteresis.

On first use, those “previous” values may be arbitrary data or non-finite half-floats. With Hysteresis = 0.94, only 6% of the new result is incorporated per frame, so bad initial data can remain visible for a long time.

Required fix: explicitly initialize every DDGI resource whenever it is created, resized, re-enabled, or when the volume signature changes.

Recommended initial values:

Probe irradiance:       rgb = 0, valid = 0
Probe relocation:       xyz = 0
Classification:         inactive/uninitialized
Irradiance atlas:       0
Visibility moments:     maxDistance, maxDistance²
Update queue:           0

Each probe also needs a dedicated historyValid or update-age field. The first real update of a probe should use an alpha of 1.0, not 1.0 - hysteresis.

2. The current DDGI result is effectively one RGB value per probe

Although an 8×8 directional irradiance atlas is allocated, WriteProbeIrradianceAtlas writes the same averaged irradiance into all 64 texels.

The forward shader then uses stateIrradiance.rgb while blending probes rather than sampling directional irradiance based on the surface normal.

That makes this closer to a trilinearly interpolated irradiance-point grid than normal-oriented DDGI. The large rectangular lighting regions in the DDGI screenshot are therefore expected: they follow probe cells and probe validity transitions.

Required fix:

Accumulate irradiance independently into octahedral direction texels.
Sample ReadDdgiProbeIrradiance(probeIndex, normal) in the forward path.
Bilinearly filter the octahedral atlas rather than nearest-selecting one texel.
Use probe confidence/coverage when blending the result.
3. Probe rays skip nearly an entire probe cell

For the Cornell room, each probe is configured with:

NormalBias = 0.16
ViewBias   = 0.38

The shader starts rays at:

origin = probePosition + direction * (normalBias + viewBias);
tMin   = viewBias;

That means geometry in approximately the first 0.92 world units along a ray can be missed:

0.16 + 0.38 + 0.38 = 0.92

The grid spacing is approximately:

X/Z: 6.7 / 7 = 0.96
Y:   4.6 / 4 = 1.15

So the ray can ignore geometry across almost a complete probe interval. Thin walls, box faces and nearby room boundaries can be skipped, producing leaks and unstable classification.

For probe tracing, use a small ray epsilon such as 0.01–0.03. Normal/view bias should primarily be applied when evaluating irradiance at a surface, not stacked into a near-one-metre exclusion zone around every probe.

4. Probe relocation and classification can change abruptly every frame

The test updates all 320 probes every frame with 96 rays each. The log confirms:

ddgiProbes=320/320
ddgiUpdated=320
ddgiRays=96
relocation=320
classification=320

It also shows substantial dynamic-resolution churn and exposure changes, both of which amplify visible instability.

Classification is a hard decision:

invalidProbe = closeRatio > 0.35 || backfaceRatio > 0.5;
activeProbe = invalidProbe ? 0 : 1;

Relocation is recalculated from the current stochastic ray subset and may be several times larger than the probe spacing.

Therefore, a probe can:

Be active in one frame and inactive in the next.
Move substantially between frames.
Change which side of a wall it effectively represents.
Cause a complete trilinear cell to brighten or darken.

For initial validation:

Disable relocation and classification entirely.
Confirm stable irradiance and visibility first.
Reintroduce classification with temporal hysteresis.
Clamp relocation to roughly:
maxRelocation = 0.4 * min(spacing.x, spacing.y, spacing.z);
Do not switch probe activity from one stochastic frame alone.
5. Two complete probe layers lie outside the Cornell room

The room occupies approximately:

Y = 0.0 to 4.0

The volume is:

Origin Y = -0.2
Size Y   = 4.6
Count Y  = 5

Its Y probe positions are therefore:

-0.20, 0.95, 2.10, 3.25, 4.40

The first layer is below the floor and the last is above the ceiling. A floor fragment interpolates mostly from the below-floor layer, and the ceiling interpolates mostly from the above-ceiling layer. Those probes see misses and procedural-sky radiance unless classification works perfectly.

For this test, keep the grid inside the enclosure, for example:

Origin = (-2.8, 0.2, -8.3)
Size   = ( 5.6, 3.6,  5.6)
Counts = 8 × 5 × 8

External probes can be useful in general scenes, but they require reliable classification and scrolling-volume behavior.

6. Hit shading is not using the actual hit surface

At a ray-query hit, the shader currently:

Invents the surface normal as normalize(-direction).
Uses a constant diffuse albedo of 0.78.
Does not retrieve the hit material or emissive value.
Does not cast a visibility ray from the hit point toward each light.

Consequently, DDGI does not know that the left wall is red or the right wall is green when calculating the bounce at a hit. More importantly, its direct-light estimate ignores shadows, so DDGI may inject light into regions that the direct shadow cubemap says are occluded.

A correct ray-query hit path needs primitive/instance identification, barycentrics, interpolated geometric normal, material albedo/emissive, and a secondary visibility query to the sampled light.

7. Low-confidence DDGI is normalized to full brightness

The forward path divides accumulated irradiance by totalWeight. If only one weak probe survives visibility and classification, its small contribution is normalized back to a full-strength irradiance value. The final DDGI field is then added without multiplying it by the calculated DDGI trust.

That creates the characteristic behavior where a cell suddenly turns fully lit as soon as one probe barely becomes valid.

Blend the normalized result by a meaningful confidence term:

float ddgiCoverage = saturate(totalWeight / expectedWeight);
worldField = ddgiDiffuse * ddgiCoverage;

The environment fallback should receive the complementary weight rather than DDGI and fallback both being added at substantial strength.

Why SSGI is noisy
1. A stochastic miss causes temporal history to be discarded

The temporal shader considers the current sample valid only when its confidence is nonzero:

currentValid = current.a > 0.0001 && ...

It then rejects history whenever !currentValid.

With six rays per half-resolution pixel, it is normal for a pixel to hit something in one frame and miss in another. The current implementation responds to a miss by throwing away the accumulated result and outputting black. This matches the speckled SSGI screenshot very closely.

A stochastic miss is not the same as an invalid surface. Separate the two conditions:

bool surfaceValid =
    currentDepth > 0.0 &&
    currentNormalSample.a > 0.0;

bool currentSampleValid = current.a > 0.0;

if (disoccluded || !surfaceValid)
    resolved = current;
else if (!currentSampleValid)
    resolved = history * historyDecay;
else
    resolved = mix(clampedHistory, current, response);
2. Previous-frame depth and normals are not stored

The temporal shader samples historyDepth and historyNormal from the current depth and normal textures at historyUv.

Those are not previous-frame surfaces, so the disocclusion test is not a valid temporal comparison. Store previous depth and normal alongside the GI history, or reproject the current world position through the previous view-projection matrix.

The diagnostics are also misleading here: the rejected-history counter is set to either the complete image size when all history is invalid or zero otherwise. It does not count per-pixel rejection.

3. Confidence is applied twice

The ray tracer already multiplies fetched scene radiance by confidence.

It then stores that radiance plus confidence separately.

The forward shader subsequently multiplies the resulting SSGI diffuse by confidence again.

The effective contribution is therefore close to confidence². Low-confidence samples become extremely dim, leaving only isolated bright dots. Store unweighted radiance with a separate confidence value, or remove the second confidence multiplication.

4. SSGI samples the fully lit scene buffer

SsgiTracePass reads SceneColor, and the shader samples HDR_SCENE_COLOR_TEXTURE_INDEX.

For stable one-bounce SSGI, the trace source should ideally contain direct lighting plus emissive surfaces, not a buffer that may already contain indirect illumination from the previous frame. Otherwise, indirect light can feed back into itself.

5. Dynamic resolution repeatedly invalidates SSGI history

Dynamic resolution is enabled by default.

The temporal pass resets history whenever its extent or resolution scale changes.

The diagnostic log shows many render-target resizes and changing SSGI dimensions. That makes temporal convergence impossible even after the temporal shader is corrected. Freeze render scale at 1.0 while validating GI.

Why the shadows appear to come from the wrong place

The visible ceiling panel is an emissive quad, but the actual shadow caster is a point light located beneath it:

Emissive panel: (0.0, 3.96, -5.55)
Point light:    (0.0, 3.35, -5.40)

Therefore the scene visually suggests a rectangular area light, while the shadows use perspective rays from one point approximately 0.61 metres below the panel. The resulting hard, diverging shadows will never precisely match the apparent source.

I did not find an obvious cubemap face-order or reverse-Z inversion. The ±X/±Y/±Z face matrices are consistent, and the shadow pipeline consistently uses reverse-Z clearing and GreaterOrEqual.

The more likely direct-shadow contributors are:

PointNormalBias = 0.03, which is a large three-centimetre receiver displacement for a small room.
A 512-pixel cubemap face.
A point source being used to represent a visible area emitter.
ShadowStrength = 0.9, environment IBL and GI filling in occluded regions.

For validation, try:

PointNormalBias        = 0.005–0.010
PointConstantDepthBias = 0.0002–0.0005
PointShadowMapSize     = 1024
PointPcfRadius         = 1

Then inspect the shadow receiver-factor view with GI, diffuse IBL, fog, SSAO, bloom and auto exposure disabled.

Recommended implementation order
Lock the test environment: fixed render resolution, fixed exposure, no fog/bloom/SSAO/reflections.
Initialize all DDGI persistent buffers and add a real per-probe history-valid flag.
Disable DDGI relocation/classification and move all Cornell probes inside the room.
Reduce probe-ray origin/tMin bias to a small epsilon.
Fix SSGI temporal accumulation so current misses retain valid history.
Add previous depth/normal history and verify motion-vector sign and units.
Remove the SSGI confidence double multiplication.
Implement hit normals, materials and light visibility in DDGI.
Implement directional irradiance atlas accumulation and sampling.
Replace the point-light approximation with an actual rectangular light, or make the visible fixture clearly represent the point light.