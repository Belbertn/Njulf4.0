# Async-compute certification evidence

Date: 2026-08-04  
Evidence scope: NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0, Vulkan 1.4.341, graphics queue family 0, dedicated compute queue family 2.  
Validation: Khronos Vulkan validation, Standard mode.  
Build: Debug, dirty worktree. This is correctness evidence, not a production-performance release certificate.

The executable audit records live in `Njulf.Rendering/Pipeline/AsyncComputeCertificationEvidence.cs` and are exposed through `AsyncComputePassCatalog`. The evidence revision is an explicit invalidation token: any change to a listed declaration, pass implementation, synchronization contract, or shader requires a new revision and fresh hardware evidence. `AsyncComputePathDiagnostic.EvidenceRevision` exports the token with each runtime path status.

## Disposition

| Path | Correctness | Forced hardware run | Linear-HDR / buffer result | Auto disposition |
|---|---|---|---|---|
| HiZBuild | Certified | `async-cert-hiz-final.json`, 0 Vulkan warnings/errors, 4/4 release and 4/4 acquire | HDR relative RMSE 0 | Graphics-only pending three ShippingPerformance pairs; GPU P95 regressed 1.908->2.045 ms in the available pair |
| AmbientOcclusionBlur | Certified | `async-cert-ao-final.json`, 0 Vulkan warnings/errors, 8/8 and 8/8 | HDR relative RMSE 0 | Graphics-only; GPU P95 and CPU P95 regressed |
| Bloom | Certified | `async-cert-bloom-final.json`, 0 Vulkan warnings/errors, 16/16 and 16/16 | HDR relative RMSE 0 | Graphics-only; CPU P95 regressed |
| Fog | Certified | `async-cert-fog-final.json`, 0 Vulkan warnings/errors, 6/6 and 6/6 | HDR relative RMSE 0 with fixed 1/60 benchmark particles | Graphics-only; async GPU P95 and CPU P95 regressed |
| GpuParticles | Certified for visual/invariant contract | `async-cert-gpu-particles-final.json`, 0 Vulkan warnings/errors, 28/28 and 28/28 | HDR relative RMSE 0.0284912; valid counters and zero dropped spawns, but exact counters are scheduling-dependent | Graphics-only; CPU P95 regressed from 4.826 to 6.855 ms in the low-profile Debug pair |
| FarFieldClipmapBake | Pending | Blocked by unrelated VK-08600 mesh/skybox descriptor-set pipeline-layout mismatch | No valid oracle | Not eligible for timing or promotion |
| SimpleDdgiUpdate | Pending | Sampled-simple-DDGI ownership gate kept the active atlas graphics-visible | No valid oracle | Not eligible for timing or promotion |

Correctness certification does not promote a path to Auto. The available timing captures are short Debug/Standard captures, not the required three identity-locked ShippingPerformance pairs. The runtime keeps the graphics route and the existing Auto timing guards; no path is approved for production Auto promotion by this evidence bundle. Retired GI backends are intentionally outside this evidence scope.

## Hardware lifecycle evidence

The five certified forced runs were 60-frame isolated-path runs with concrete queue handoffs:

- HiZ: 2 graphics segments, 1 compute segment, 4 ownership transfers.
- AO blur: 3 graphics segments, 1 compute segment, 8 ownership transfers.
- Bloom: 2 graphics segments, 1 compute segment, 16 ownership transfers.
- Fog: 2 graphics segments, 1 compute segment, 6 ownership transfers.
- GPU particles: 2 graphics segments, 1 compute segment, 28 ownership transfers.

Every run reported matching planned/emitted release and acquire counts, zero stale-plan rejections, zero validation fallbacks, and `status=passed`.

## Reproducible oracle artifacts

All artifacts are generated under the ignored `artifacts/` directory. SHA-256 values below make a copied evidence bundle auditable:

| Artifact | SHA-256 |
|---|---|
| `async-cert-hiz-final.json` | `2fd8ca7e476a3e9fbeca6f9662c87c980b9b6c04f1545877b30cb9e3dd54e9d3` |
| `async-cert-ao-final.json` | `0854541daf67b341b99a3b80028729a08a73946ca7bc749ccae272545ff56e89` |
| `async-cert-bloom-final.json` | `6b734d490a88158902c116daadd98b4845d5d8c37f035cfcc82e0fc1befeca59` |
| `async-cert-fog-final.json` | `a2855b94f658688f93fecd800f0e625685ceb44f8acec716d152298729e5b844` |
| `async-cert-gpu-particles-final.json` | `f0c1079af92b871a2f80a40bb1ba20429a5ffb7238d1b4d08c3d093322a13211` |
| `async-bench-vfx-hiz-graphics.json` | `f919d05b7c63a6d2cdb63b17afcfbd6267d737402205f542de6d52b5419d792a` |
| `async-bench-vfx-hiz-async.json` | `2a377e88d25afcd8da324f19f739633818cc0bbe8152498f26fde393bf2494e0` |
| `async-bench-vfx-ao-graphics.json` | `3c2167db8a9dc722b8ecbcc20fcd93fafe9209efd40ea83fb60abd6c3d32c4fb` |
| `async-bench-vfx-ao-async.json` | `25fda59969441ee0034b3a3c0d6bd932383cbe82d65a4230f7e13094064681e0` |
| `async-bench-vfx-bloom-graphics.json` | `f5bbfbe4267d4d050c57d68ec835de7937d425bb2ace0e04e8563e45b248a2cc` |
| `async-bench-vfx-bloom-async.json` | `5badbe487830a8c75836f96b09686fc52d3bef457ff8b23fe0519f8f2cde9d4d` |
| `async-bench-vfx-fog-graphics.json` | `2ffa83444a5706f38d0fa03a1da8466a079abd42a57efb1458e3426a3dfb99a2` |
| `async-bench-vfx-fog-async.json` | `c4f0f9b0e7e12bba5526164fc0f757f2ff43f93da874e1f317ba2f1c4ead6788` |
| `async-bench-vfx-gpu-particles-low-graphics.json` | `cccaf0dd2d613e72bd5b210e5f36405b6f1ad7111530cf5257452a16ddd2af2a` |
| `async-bench-vfx-gpu-particles-low-async.json` | `85eaf39e06abffb895feeb5138fe7d6181afc30c2dc898f9596ab54e6e104f17` |

The benchmark harness now locks particle simulation to its authored 1/60 timestep only when `--benchmark` is active. This removed wall-clock particle trajectory noise from graphics/async image comparisons; interactive rendering remains wall-clock driven.

## Required follow-up

Run three or more identity-locked `ShippingPerformance` graphics/async pairs per correctness-certified path. Promote only if median GPU improvement is at least 3%, GPU P95 does not regress, material pass timings remain within comparer tolerance, CPU record/submit cost does not materially regress, and the first-consumer wait does not consume the predicted overlap. Re-run the pending paths only after their stated resource/graph blockers are fixed.
