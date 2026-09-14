# Optical artifacts: budget cutoffs and undenoised samples

2026-09-13. Revision `cc35bcc6fc69854dd96e316cd6e14ec69b139e8e`, dirty worktree.
The existing DDGI spatial-invalidation changes and GPU harness edits were preserved.
Hardware: NVIDIA GeForce RTX 3060 Laptop GPU. Development, 1600x900, sorted
transparency, MaterialShowcase, HybridRayQuery, thick ray transmission and RGB
dispersion. Compact observations: [comparison JSON](20260913-optical-artifacts.json).

## Findings and retained changes

- Thick transmission in `ForwardTryReserveThickTransmissionTask` admits the first
  262,144 fragments and abruptly substitutes environment refraction afterward.
  The cutoff can follow GPU raster work blocks. Raising the showcase allocation
  to 2,097,152 removed the large cutoff patches in the central glass in the
  reproduced view. This fits the existing 128 MiB admission envelope. It is
  workload headroom, not a general solution for arbitrarily large coverage.
- The supplied capture records 313,688 transparent reflection requests, with
  62,076 admitted and 251,612 rejected. The shader randomly gates these samples
  and directly composites different sources without transparent history or
  reconstruction. The showcase now allows 1,048,576 reflection rays; both new
  captures reported zero rejected rays. General-scene defaults are unchanged.
- `DielectricSampleInterface` also randomly chooses reflection/transmission and
  rough microfacets. Its single-path output remains visibly noisy even with
  sufficient budget. Those speckles are not missing raster coverage.
- Opaque classification previously froze any stationary, matching tile, including
  sparse/stochastic reflection observations. The classifier now keeps geometric
  sources and sparse histories active and requires eight accumulated frames
  before freezing analytic sources. This lets the existing temporal/spatial
  stages operate. Geometric ray work rose from zero in the budget-only control
  to 3,815 in the retained capture; active tiles rose from 76 to 5,368.
- MaterialShowcase baseline capture incorrectly applied Sponza's camera and
  lighting settings. It now uses a repeatable optical view matching the supplied
  capture camera, locks exposure to 0.0625, captures after 120 full-quality frames,
  and exports under `material-showcase`.

## Validation and cost

The changed classifier was freshly compiled, passed `spirv-val`, and was confirmed
as the loaded override by SHA-256 in the renderer capture. The retained scene
render completed with zero Vulkan validation errors. Screenshots and raw
captures are under `artifacts/transmission-artifacts-20260913/`.

The final normal Development build of `Njulf.Tests/Njulf.Tests.csproj` refreshed
the shader bundle and passed (47 warnings, zero errors). The
canonical Vulkan 1.3 classifier artifact also passed `spirv-val`. Focused checks:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-build --no-restore --filter 'FullyQualifiedName~HybridReflectionContractsTests|FullyQualifiedName~SampleMaterialShowcaseSceneTests'
```

Result: 46 passed, 3 failed. All four showcase tests passed, including the added
check that the larger thick-transmission budget still resolves to ray queries
inside its memory envelope. The three failures are unchanged source-text tests:
`ExactReceiverPublication_DefersCacheSpecializationsWithoutRenderTimeCreation`,
`HybridReceiverCache_UsesCompactProducerOnlyForEligibleSurfacePath`, and
`VulkanRuntime_ResetHeadersUseSingleOrderedTransferWrites`. Their absent renderer
markers and four-versus-three buffer-update count were verified directly in HEAD;
the relevant renderer/runtime files and test fixture are unchanged. The suite is
not reported as green. Details are in `final-tests.log` and
`preexisting-test-failures.json` in the investigation directory.

With the same increased budgets, camera, exposure and configuration, the
budget-only control's diagnostic GPU frame was 38.917 ms; the retained classifier
capture was 40.027 ms. Transparent-pass observations were 20.966 and 21.336 ms.
These are **single-frame observations, not a measured performance delta**:
lighting/water animation, GI convergence and instrumentation remain relevant.
P95/P99 and a warmed steady-state timing series were not collected. The original
user capture is not a comparable timing baseline. No 60 fps claim is made.

## Rejected inline denoising prototypes

A surface-aware subgroup filter was tested on optical lighting before alpha
composition. It rejected different object/material identities, tangent-plane
depth discontinuities, incompatible normals, and nonfinite samples. The headless
GPU test preserved constant radiance and surface boundaries. For its alternating
test signal, variance fell from 1 to 0.02399 with a full subgroup, and to 0.04663
with four lanes. This is numerical evidence for the filter, not a scene-quality
measurement.

Both integrated candidates spent multiple minutes creating
`forward_transparent_ray.frag.spv`'s first native graphics pipeline without
producing a beauty frame; they were stopped. Warm-cache control startup is not a
matched cold compiler baseline, so this does not quantify a compiler slowdown.
Neither candidate is accepted or remains in production. Small source/test/log
evidence is retained under the ignored investigation directory. The 24 rejected
candidate SPIR-V files (18,528,720 bytes) were removed after recording the findings.

## Remaining denoising work

Glass and water speckles are **not fixed** by the retained changes. Implement
denoising as a separate optical-lighting stage rather than adding neighborhood
operations to the already large inline ray-query program:

1. Export reflection and transmission radiance separately from direct lighting,
   tint, Fresnel, opacity and the opaque background. Carry observed/unsampled
   validity, sample confidence, roughness, receiver identity, geometric normal,
   receiver depth and optical hit distance.
2. Preserve transparent-layer identity. Opaque scene depth alone cannot validate
   a glass receiver; one frontmost surface cannot represent overlapping glass.
   Define layer storage and overflow/composition behavior for both sorted and
   weighted transparency before attaching shared history to either path.
3. Reproject by receiver and layer, reject disocclusions and optical path changes,
   and accumulate measured samples. A budget rejection is an absent observation,
   not black radiance and not a new environment measurement.
4. Run a small edge-aware spatial reconstruction on that optical signal, then
   compose it through the existing material and transparency rules. Keep direct
   highlights, silhouettes and the opaque background out of the blur.
5. Validate the glass close-up, full water pool, rough metal, overlapping glass,
   camera movement and budget exhaustion. Check energy retention, edge bleeding,
   temporal trails and frame cost before retaining a candidate.

An isolated shader would separate filtering from the inline ray-query compiler
path and give the filter actual image neighborhoods instead of subgroup boundaries.
This is a proposed implementation path, not a completed renderer feature.

## Reproduction and retention

From the repository root, build current shaders normally and run:

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore
$captureRoot = Join-Path (Get-Location) 'artifacts/optical-repro'
./NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.exe --scene material-showcase --smoke-frames 400 --baseline-snapshot-dir $captureRoot --validation standard --max-fps 60 --vsync off
```

Storage review used `tools/prune-local-artifacts.ps1`: no obsolete payload was
eligible; approximately 256 GiB remained free on D:. All new investigation data
was kept inside this workspace on D:. Keep the active optical references while
the follow-up denoising work needs them, then prune bulky data under the two-day
retention policy. No user attachment or source asset was removed.
