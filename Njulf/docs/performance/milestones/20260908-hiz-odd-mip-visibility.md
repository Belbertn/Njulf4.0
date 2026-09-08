# Hi-Z odd-mip visibility correction — 2026-09-08

Source: `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty workspace containing pre-existing asset, editor, renderer, local-shadow and scene changes. This investigation changes the Hi-Z downsample shader and adds a GPU regression plus a deterministic sample reproduction. [Compact machine-readable evidence](20260908-hiz-odd-mip-comparison.json).

## Failure and decision

The supplied Sponza capture showed distant gallery arches turning white/grey at a fixed camera angle; F8 removed the artifact. Reproduced the same shapes at the supplied camera position `(8.981506, 3.3245177, 4.6348004)`, yaw `-1.5707964`, pitch `-0.26577514`, FOV `0.98174775`, near/far `0.05/250`, at **1600×900**. Initial 1920×1080 captures did not reproduce it and were rejected as the regression workload.

The pyramid used `dstPixel * 2` and an unconditional 2×2 reduction. Its 800×450 base produces odd extents, including 400×225 -> 200×112. Fixed 2×2 regions omit odd edges and fail to cover the normalized UV footprint used by occlusion sampling. Visible surface depth can be replaced by a neighboring foreground occluder's depth. Replacing this with floor/ceil integer footprint bounds preserves all overlapping source texels, including the third row/column for odd dimensions and one-texel axes. Reverse-Z still uses minimum depth; culling modes and bias are unchanged.

**Accepted:** corrected shader with Hi-Z enabled. The reproduced white arches disappear and agree visually with the Hi-Z-disabled reference. Current-frame forward emission rises from 9,975 to 10,546 out of the same 22,490 candidates: 571 falsely rejected meshlets are restored; 11,944 occluded meshlets are still rejected.

## Validation

- Actual production SPIR-V executed in a headless Vulkan test: old shader **fails** the 5×5 center-hole case (expected depth 0, observed 0.8); corrected shader **passes** center overlap, odd edges, thin mips and an even-dimension control. Baseline bytecode was selected with `NJULF_HIZ_TEST_SHADER`, without reverting source or replacing the production DLL.
- Development build and 31 focused GPU/trajectory tests passed, no skips.
- Fresh `glslangValidator` compile and `spirv-val --target-env vulkan1.3` passed.
- Before, corrected and Hi-Z-disabled 1600×900 renderer captures each reported zero Vulkan validation warnings/errors. Reviewed lossless HDR captures through a fixed 2× exposure preview; benchmark lighting differs from the user's very dark screenshot, but the original white-arch geometry reproduces exactly.
- Runtime breakpoint hit `VulkanRenderer.PlanHiZVisibility` at line 6536. The live process had both Hi-Z stages enabled, bias 0.0005, nine mip levels, and no camera cut. It ended before assisted image capture; the subsequent independent deterministic runs supplied image evidence. Agent breakpoints were removed.

## Timing observations and limits

NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0; Development/Standard validation, DdgiHigh Sponza, graphics queue, 120 warmup and 120 measured frames. These are correctness captures, **not qualified production timing comparisons**: NormalTelemetry, Standard validation and Development cause the production comparability contract to reject them. A concurrent test build also affected corrected-run CPU measurements. No performance improvement is claimed.

| Run | GPU average / p95 / p99, ms | Hi-Z average / p95, ms | CPU average / p95 / p99, ms |
| --- | --- | --- | --- |
| Original, visible artifact | 17.740 / 18.056 / 18.171 | 0.039 / 0.040 | 5.229 / 6.013 / 10.609 |
| Corrected, Hi-Z enabled | 17.937 / 18.267 / 18.291 | 0.048 / 0.049 | 7.940 / 11.420 / 15.032 |
| Hi-Z disabled reference | 18.461 / 21.094 / 21.387 | 0.047 / 0.049 | 6.197 / 7.218 / 26.976 |

The observed downsample cost increases by about 0.009 ms. The pyramid remains built in the disabled reference because hybrid reflections consume it. The stationary gallery capture does not qualify every camera trajectory or every Hi-Z consumer. A legacy image-comparison helper lacked its FLIP dependency; no FLIP score is claimed.

## Reproduction and retention

Run from the repository root; all output stays under its ignored `.tmp` root on D:.

```powershell
New-Item -ItemType Directory -Force .tmp/hiz-visibility-20260908 | Out-Null
$env:TEMP = (Resolve-Path .tmp/hiz-visibility-20260908).Path
$env:TMP = $env:TEMP
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --filter 'FullyQualifiedName~HiZDepthPyramidGpuTests|FullyQualifiedName~SampleBenchmarkTrajectoryTests' --results-directory .tmp/hiz-visibility-20260908
& NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.exe --scene=SponzaPlaza --quality-preset=DdgiHigh --performance-scenario=Normal --benchmark --benchmark-trajectory=sponza-hiz-incident --benchmark-warmup-frames=120 --benchmark-measure-frames=120 --benchmark-max-settle-frames=1024 --benchmark-budget-profile=stress --validation=standard --gpu-timing --vsync=false --max-fps=0 --benchmark-report=.tmp/hiz-visibility-20260908/repro.json --health-report=.tmp/hiz-visibility-20260908/repro-health.json --benchmark-hdr-candidate=.tmp/hiz-visibility-20260908/repro.pfm
```

Add `--benchmark-variant=hiz-disabled` for the visual reference. The named incident trajectory fixes the original camera and 1600×900 window. Raw captures and original shader bytecode are temporary evidence in `.tmp/hiz-visibility-20260908`, eligible for pruning two days after completion. Superseded 1920×1080 image payloads and redundant exposure previews were removed after recording their results here. Storage was reviewed with `tools/prune-local-artifacts.ps1`; `-Apply` removed 922 obsolete generated payloads (6.31 GiB), preserving compact evidence and active references, with 298.31 GiB free afterward.
