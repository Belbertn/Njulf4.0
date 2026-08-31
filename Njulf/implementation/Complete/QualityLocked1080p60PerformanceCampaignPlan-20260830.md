# Quality-Locked 1080p60 Performance Campaign

## Success Contract

- Target native 1920x1080 on the RTX 3060 laptop: GPU p95 <=10 ms, renderer CPU p95 <=6 ms, and frame p99 <=16.67 ms across Bistro/Sponza stationary, traversal, and relighting workloads in Release and ShippingPerformance.
- Freeze the current `DdgiHigh` feature set, GI/AO/reflection/shadow/AA settings, render scale, textures, sampling budgets, and scene content. Dynamic resolution, upscaling, feature disabling, and reduced probe/ray/sample counts are not acceptable shortcuts.
- Require <=0.005 relative HDR RMSE, FLIP p95 <=0.02, ROI luminance shifts <=0.02 mean/<=0.03 p95, temporal-repeatability compliance, and visual approval against the supplied images.
- The Bistro image is an artistic target because it comes from another renderer; the Sponza image is a visual invariant. Exact regression testing uses freshly captured, converged 1080p linear-HDR references from the current renderer.

## Baseline and Instrumentation

- Preserve the dirty working tree exactly in an isolated campaign worktree and commit a content-hash-verified baseline there; leave the user's original tree untouched.
- Repair the existing campaign harness using PowerShell 7: retire stale candidate definitions already present in the baseline, retain protected acceptance tests, archive both supplied images with hashes, then pass harness validation and tests.
- Capture converged 1080p baselines with fixed AC power mode, driver, cameras, feature state, and thermals. Use three-cycle ABBA sampling without RenderDoc or validation overhead.
- Treat the supplied snapshot as diagnostic, not an acceptance baseline: it is a 1600x900 development capture still converging, but identifies a 34.09 ms GPU frame dominated by forward opaque (~27.67 ms), receiver cache (~6.03 ms), DDGI update (~3.55 ms), scheduler (~1.88 ms), and GTAO/blur (~1.68 ms).
- Use renderer timestamps/counters, paired feature isolation, SPIR-V inspection, Vulkan diagnostics, NVIDIA clock/thermal telemetry, and settled RenderDoc captures. Add backward-compatible telemetry fields where exclusive stage timing is currently unavailable.

## Iterative Optimization Loop

1. Rank authenticated hotspots exceeding 0.25 ms or 5% of frame time and record every hypothesis in a candidate ledger.
2. Implement one narrow, quality-neutral candidate per commit, with subsystem tests and shader invariants.
3. Measure it against the retained stack using ABBA runs in both configurations. Keep only statistically significant wins--at least 1%/0.10 ms total or 5%/0.05 ms in the target pass, 95% confidence, and no other-pass regression above 1%.
4. Run focused quality checks before retaining it; reject and roll back failures automatically.
5. Re-profile, re-rank, and repeat until the full target passes or no quality-neutral hypothesis remains.

Priority order:

- Isolate forward GI cost with paired GI-on/off captures, then inspect overdraw, mesh/task dispatches, material variants, divergence, register pressure, repeated resource reads, cache admission, and exact-fallback shader paths.
- Split receiver-cache classify/gather/resolve timing and optimize occupancy, redundant work, tile admission, memory access, and synchronization without changing results.
- Examine DDGI update/scheduler scans, dispatch compaction, barriers, and atlas traffic. Do not promote the currently unqualified accelerated-tail algorithm.
- Address GTAO, shadows, hybrid reflections, and smaller bandwidth/transition hotspots.
- Validate the 56.8M invalid mesh mappings, 91.3% residency hit rate, evictions, and 10.3% tracked-memory headroom; remove duplication or lifetime waste without reducing asset or GI data.
- After GPU backpressure is reduced, profile remaining CPU/GC work with dotTrace if CPU p95 still exceeds 6 ms. Use the runtime debugger only when counters, traces, and source inspection cannot establish the required control-flow fact.

## Final Qualification and Stop Conditions

- Run the complete workload/quality matrix, sustained thermal soak, traversal and relighting stress tests, memory audit, shader tests, and final baseline-versus-result RenderDoc comparison.
- Success requires every performance, quality, stability, and memory gate to pass together; tracked use must remain within 80% of the 2 GiB budget.
- Stop without changing quality when repeated profiles agree and the candidate ledger contains no untested quality-neutral avenue for every material hotspot. Preserve the best accepted stack and produce an infeasibility report quantifying the remaining gap and limiting passes.
- Deliver before/after captures, image-difference reports, RenderDoc captures, benchmark distributions, candidate ledger, retained commits, and rejected-candidate evidence.
- No intentional public rendering API or quality-setting changes; instrumentation/schema additions remain internal and backward-compatible.
