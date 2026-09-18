Implementation plan — TAA resolve fixes on Simplified-SDF

Context. 46fdef4 landed the motion-vector half correctly; the wobble now comes from taa_resolve.frag removing the jitter a second time on the colour read, and from a disocclusion test that rejects history on every silhouette. Two files, both edits local to one function, plus a test guard.

1. Njulf/Njulf.Shaders/taa_resolve.frag — stop offsetting the current-frame read

The fragment at inUv is the jittered sample. Sampling at inUv + jitter re-resamples it back toward the grid every frame: it wobbles the presented image by the absolute jitter (±0.5 px UV) and destroys the sub-pixel information TAA integrates.

Line 109-110 — delete sampleUv entirely:
ivec2 sourceExtent = ivec2(pc.SourceDimensions);
ivec2 sourceTexel = clamp(ivec2(inUv * pc.SourceDimensions), ivec2(0), sourceExtent - ivec2(1));

(drop lines 109-110 and the sampleUv uses on 113-114).

Take current from the 3×3 loop instead of a separate bilinear tap. The loop already fetches the centre texel at neighborOffset == ivec2(0). Declare vec3 current = vec3(0.0); above the loop and inside it:
if (neighborOffset == ivec2(0))
    current = neighborColor;

This guarantees the current sample is the exact texel with no filtering, even if SourceDimensions ever diverges from the render area.

Delete SampleCurrent() (lines 40-46) — it becomes unused.

Everything downstream (historyUv = inUv - velocity, historyDepthTexel, the velocity fetch at sourceTexel + closestOffset) then reads from one consistent pixel, which it does not today.

2. Njulf/Njulf.Shaders/taa_resolve.frag — fix the disocclusion test

The current form (lines 218-220) compares closestDepth (max over the 3×3, i.e. the nearest neighbour) against historyDepth. On a background pixel next to a foreground object with a static camera that is |0.9 − 0.05| / 0.9 ≈ 0.94 → disocclusion = 1 → currentLength = 1 → feedback = 0 → raw current frame, permanently, along the whole silhouette band. It also trips on grazing surfaces where the per-pixel relative depth change exceeds 2%. Flax's test is a min over the neighbourhood, which is exactly what stops it firing at silhouettes.

In the 3×3 loop, add float neighborDepths[9]; above it and store each tap:
neighborDepths[(y + 1) * 3 + (x + 1)] = neighborDepth;

Keep closestDepth / closestOffset — they are still needed for velocity dilation.

Replace lines 218-220 with:
// Flax TAA.shader: the miss test is a MIN over the neighborhood, so a
// silhouette - where at least one neighbor still matches the reprojected
// surface - is not a disocclusion. Reverse-Z: farther is a SMALLER value,
// so "occluded" means the neighbor sits behind the reprojected depth.
float minDepthRelative = 1.0;
for (int i = 0; i < 9; i++)
{
    float occluded = max(historyDepth - neighborDepths[i], 0.0);
    minDepthRelative = min(minDepthRelative, occluded / max(historyDepth, 1e-4));
}
float disocclusion = smoothstep(0.02, 0.08, minDepthRelative);

Behaviour this gives: sky (historyDepth == 0) never disoccludes, which is right because the background fill already supplies it camera velocity. A near surface reprojecting onto what is now all-sky gives 1 → full reset. A silhouette gives 0 because the foreground neighbour matches.

Leave lines 222-236 (currentLength / feedback ramp) as they are. Its fixed point is 1/disocclusion, so a partial trip pins feedback low — that is only a problem while the test false-positives, and fix 2 removes that.

3. Njulf/Njulf.Tests/AntiAliasingRepairTests.cs — guard the regression

inUv + pc.TaaCurrentJitterUv survived two commits, so pin it out in TaaResolve_ReprojectsWithJitterFreeVelocityAndRampsHistoryLength:

Assert.That(shader, Does.Not.Contain("inUv + pc.TaaCurrentJitterUv"));
Assert.That(shader, Does.Not.Contain("sampleUv"));
Assert.That(shader, Does.Contain("minDepthRelative"));

Keep the existing assertions. No ABI, settings, or C# rendering changes — the push layout (SmaaPredicationEnabled @100, jitter vec2s @104/112, TaaSharpness @120, size 124) is already correct and both layout tests already match.

Verification
dotnet build Njulf/Njulf.sln — the shader build task compiles taa_resolve.frag; a GLSL error fails here.
dotnet test Njulf/Njulf.Tests --filter AntiAliasingRepairTests then the full Njulf.Tests run (GPUStructLayoutTests, ShaderEffectGpuTests TAA smoke must stay at zero validation errors).

Deliberately unchanged

Motion-vector jitter removal and TemporalJitterNdc plumbing (correct), motion_vector_background.frag (correct — the + TemporalJitterNdc.xy before un-projecting through the jittered inverse VP is what makes inUv the unjittered current UV), ClipToAabb, the Catmull-Rom 5-tap, the velocity ramp and the 0.85/0.95 defaults, and the post-tonemap resolve point.