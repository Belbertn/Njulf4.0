Verdict

The Simplified branch’s SSGI is not production-ready, and the camera-motion noise is not primarily a tuning problem. It is the combined result of three architectural failures:

SSGI often has no motion vectors.
The displayed GI is probably one frame behind the camera.
The “denoiser” is only a 2×2 depth/normal-aware upsample.

When camera movement invalidates temporal history, the pipeline exposes an extremely sparse stochastic signal with almost no spatial reconstruction.

This was a static code audit; I did not execute the renderer or inspect a RenderDoc capture.

Primary camera-motion failure chain
P0 — Motion vectors are tied to TAA

MotionVectors is allocated at real resolution only when AA is TAA; otherwise it is a placeholder. MotionVectorPass also immediately exits and sets MotionVectorsEnabled = 0 for every non-TAA mode.

The SSGI temporal shader then substitutes zero velocity:

if (pc.MotionVectorsEnabled == 0u)
    return vec2(0.0);

vec2 historyUv = uv - velocity;

Consequently, after a camera movement, the current surface is compared with whatever occupied the same screen pixel in the previous frame. Depending on coincidental depth and normal similarity, this either:

rejects most history, exposing raw noise; or
accepts unrelated history, producing trailing and swimming.

Required fix: motion vectors must be a renderer service, not a TAA resource.

bool needsMotionVectors =
    settings.AntiAliasing.EffectiveMode == AntiAliasingMode.Taa ||
    (settings.GlobalIllumination.EffectiveUseSsgi &&
     settings.GlobalIllumination.TemporalEnabled);

Use that condition both for target allocation and MotionVectorPass execution. Do not reset the previous view-projection merely because TAA is disabled.

P0 — The final GI is consumed one frame late

The declared order is:

ForwardPlusPass
SsgiTracePass
SsgiTemporalPass
SsgiDenoisePass

Yet forward.frag, executed by ForwardPlusPass, samples GI_FINAL_DIFFUSE_TEXTURE_INDEX. No pass earlier in the frame writes that resource. Therefore, barring an unlisted external update, frame N shades with the GiFinalDiffuse produced for frame N−1.

That means even perfectly reprojected SSGI is presented using the previous camera. This is an architectural source of motion lag and swimming.

Required fix: split forward lighting and indirect composition:

ForwardDirectPass
    -> SceneColor without SSGI
    -> SsgiTraceSource
    -> receiver albedo/material guides

SsgiTrace
SsgiTemporal
SsgiSpatialFilter
SsgiUpsample

SsgiCompositePass
    -> add current-frame SSGI to SceneColor

This needs a receiver albedo/diffuse-color target or equivalent compact material data for the composite pass. Sampling the previous GI inside the forward material shader should be removed.

P0 — The denoiser does not denoise

ssgi_denoise.comp examines only the four half-resolution samples surrounding the full-resolution output pixel. The loops are fixed to 0..1; Radius and DenoiserEnabled are declared but never used.

This is an edge-aware bilinear upsampler, not a reconstruction filter. It cannot suppress a 1–8-ray stochastic signal, particularly after temporal rejection.

Replace it with a genuine half-resolution spatial stage:

Temporal resolve and moment update.
Three to five à-trous iterations with step widths such as 1, 2, 4 and 8.
Depth-, normal-, luminance- and variance-guided weights.
Nearest-depth or nearest-Z full-resolution upsample.

SVGF’s production-relevant structure is temporal accumulation, luminance variance estimation and a hierarchical wavelet filter; AMD’s denoisers likewise combine temporal reprojection with substantial spatial filtering and increase spatial contribution when temporal history is weak.

Signal and temporal-filter problems
P0 — RGB and confidence cease to represent valid energy after temporal filtering

The trace shader stores:

rgb   = accumulatedRadiance / accumulatedConfidence;
alpha = accumulatedConfidence / rayCount;

The final forward shader later multiplies RGB by alpha. For an unfiltered raw sample, that approximately reconstructs accumulatedRadiance / rayCount.

The problem is that the temporal pass independently blends conditional RGB and alpha:

resolved           = mix(history.rgb, current.rgb, response);
resolvedConfidence = mix(history.a, current.a, response);

Their product is no longer the temporal average of the original energy. Hit-coverage changes therefore become brightness changes. The neighborhood statistics also ignore zero-confidence misses, so one bright successful hit can dominate the estimated local mean.

Required fix: use an explicit premultiplied representation:

vec3 energy = accumulatedRadiance / float(rayCount) * intensity;
float support = accumulatedConfidence / float(rayCount);

output = vec4(energy, support);

Temporal and spatial stages should filter energy directly. support should control trust and filter radius, not multiply the final light contribution. Add separate luminance first/second moments and history length.

Also remove diffuseWeight from ssgi_trace.comp: the trace multiplies by (1 - metallic), and the forward composition multiplies by it again, squaring the receiver’s diffuse weight.

P1 — Surface validation and history-color sampling disagree

FindBestPreviousSurface searches four previous depth/normal samples and chooses the best candidate. But the history color was already bilinearly sampled at the original historyUv; it is not fetched from the selected surface candidate.

At silhouettes and disocclusions, the pass can therefore validate one surface while accumulating color blended from another.

Fix: return the chosen previous pixel or UV and fetch history radiance, moments and history length from that exact location. Optionally apply a wider reconstruction only after all participating taps pass surface validation.

P1 — Motion produces abrupt history loss

The temporal pass rejects history outright above 32 pixels of motion and writes the current raw sample when rejected:

velocityPixels > 32.0
...
resolved = current.rgb;

There is no spatially reconstructed fallback. Camera cuts are also absent from ResetHistoryIfInputsChanged; history resets only for extent and selected GI setting changes.

Fixes:

Do not reject solely because velocity exceeds a fixed number.
Gradually reduce history weight with motion.
Reject based on reprojection validity, linear depth, normal, material ID and object identity.
Explicitly reset on camera cuts, FOV/projection changes, teleportation and invalid previous matrices.
On disocclusion, use a spatially reconstructed current signal or DDGI/environment fallback—not raw SSGI.
P1 — History lacks production denoising state

The persistent SSGI data consists of color/confidence, raw depth and normal. There is no:

history length;
luminance first and second moments;
variance;
reactive/disocclusion mask;
material ID;
hit distance.

A production baseline should add:

HistoryRadiance    RGBA16F
HistoryMoments     RG16F   // E[luma], E[luma²]
HistoryLength      R8/R16F
HistoryViewZ       R32F
HistoryNormalMaterial packed target

Store linear view-space Z directly instead of storing raw depth and reconstructing previous depth with the current inverse projection.

NVIDIA’s NRD integration guidance similarly expects linear view Z, reliable non-jittered motion and explicit history restart behavior. It also recommends 2.5D motion where possible and nearest-Z upsampling for reduced-resolution denoising.

Trace quality issues
P1 — The sampling pattern is temporally noisy

Every pixel uses frame-dependent hashes to rotate its hemisphere pattern. There is no tiled blue noise or low-discrepancy sequence.

Replace this with:

tiled blue-noise values for spatial decorrelation;
Sobol/Owen or another low-discrepancy temporal sequence;
a small finite animation period where appropriate;
stable absolute-pixel addressing.

Low-discrepancy blue-noise sampling is specifically recommended for stable very-low-ray-count signals.

P1 — Hit classification is unstable under camera movement

The marcher uses a small fixed number of quadratic world-space steps, an increasingly permissive thickness of alongRay * 0.035, and no binary refinement. Although Hi-Z is built before SSGI, SsgiTracePass does not read it.

Minor depth-projection changes can consequently turn a hit into a miss from one frame to the next.

A more stable implementation should use:

view-space screen-space DDA or hierarchical-Z traversal;
conservative depth crossing;
thickness based on pixel footprint and depth derivatives;
four to six binary-refinement steps;
front-face and normal validation;
output first-hit distance for the denoiser.
P1 — TAA jitter uses the frame-slot index

The camera jitter is generated from _currentFrame, while the monotonic _temporalSampleIndex is maintained separately. GetHaltonJitter uses its input modulo the requested sequence length. The result is that jitter repeats with frame-in-flight slots instead of traversing the configured Halton sequence.

Change it to:

AntiAliasingJitter.GetHaltonJitter(
    checked((int)_temporalSampleIndex),
    ...);

This becomes particularly important if TAA is temporarily forced to obtain motion vectors.

Recommended implementation order
Production blocker patch
Decouple motion vectors from TAA.
Fix the jitter index and add camera-cut detection/history reset.
Move SSGI composition after the current frame’s denoising.
Change the raw/history representation to premultiplied energy.
Remove the duplicate receiver metallic weighting.
Replace the 2×2 “denoiser” with variance-guided à-trous filtering and nearest-depth upsampling.

These changes address the dominant camera-motion failure modes. Raising ray count or changing HistoryResponsiveness before this patch will only trade noise for ghosting.

Quality patch
Store moments and history length.
Sample history from the same candidate used for depth/normal validation.
Use linear view-Z and depth-aware velocity selection.
Add blue-noise/low-discrepancy sampling.
Move tracing to Hi-Z with hit refinement.
Add a spatial/DDGI fallback for disocclusions.
Production validation

The existing GI validation code configures Cornell, thin-wall, moving-light and moving-object scenarios, but it does not define image-quality or temporal-stability pass/fail criteria.

Add deterministic camera paths covering:

stationary convergence;
rotation and translation at several speeds;
camera cuts and FOV changes;
moving rigid and skinned objects;
moving lights;
thin walls and silhouettes;
TAA, SMAA and no-AA configurations;
resolution and dynamic-resolution changes.

Track separately:

history rejection by reason
reprojected temporal luminance error
history length distribution
disocclusion recovery frames
thin-wall leakage
NaN/Inf and HDR outliers
SSGI trace / temporal / spatial GPU timings

A reasonable initial gate is that stable surfaces reach low temporal error after accumulation, disocclusions settle within a few rendered frames, and no GI position lag is visible during a deterministic camera pan.