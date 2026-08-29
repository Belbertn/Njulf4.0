# GI all-on runtime qualification

This route proves that the DDGI receiver cache, accelerated transport solver,
C1 opacity micromaps, C3 directional guiding, and C4 tagged caustics are active
at the same time in a rendered scene. Requested settings alone never pass.

## Active production profile

Medium, High, DdgiHigh, and Ultra request the temporal-adaptive receiver cache,
the two-sweep accelerated solver, C1 `ExtFourStateExperiment`, C3
`PerProbeHistogramExperiment`, and C4 `WorldCacheExperiment`. Low retains the
exact receiver gather and canonical solver with C1/C3/C4 disabled. Unsupported
hardware, missing cooked C1 content, an ABI or allocation failure, or an empty
C4 scene still falls back safely during normal rendering.

The standalone qualifier pins DdgiHigh, GPU-resident scheduling, disabled async
compute, the explicit modes above, two accelerated sweeps, and an uninterrupted
Material Showcase, Sponza, or Bistro workload. It attaches the same real
alpha-masked grass fixture to every supported scene and adds a validated C4
caster/receiver hero where the scene has none. Material Showcase uses its
authored receiver lattice without a redundant camera ring so it remains inside
DdgiHigh's 16,384-probe limit.

Thick-transparent ray-query shading and hybrid specular reflections are
separate feature/pipeline families; the qualifier fixes thick transmission to
the bounded approximation, disables dispersion, and disables hybrid
reflections.
The dielectric remains in the ordinary ray scene and C4 still has to trace,
publish, resolve, and composite non-empty tagged-caustic work.

## Run it

Build the reviewed pinned OMM bridge first when the ignored local artifact is
not already present. Then run:

```powershell
.\tools\gi-all-on-qualification.ps1 `
  -OutputDirectory .\artifacts\gi-all-on-<run-id> `
  -Scene material-showcase `
  -Configuration Release
```

The wrapper builds the solution, force-recooks the C1 grass with anisotropy 1
and the pinned NVIDIA OMM bridge, verifies that the published `.njmodel`
contains a valid resource-complete Vulkan EXT four-state section, and launches
the controlled runtime. `-SkipBuild` and `-SkipCook` are intended only for an
already frozen local build/cook. A fresh output directory is mandatory.

To reproduce only the C1 transaction:

```powershell
.\tools\cook-gi-all-on-c1.ps1 -Configuration Release
```

The equivalent raw runtime route is:

```text
NjulfHelloGame --gi-all-on-qualification-report <report.json> \
  --scene material-showcase --smoke-frames 1800
```

## Pass contract

The atomic `gi-all-on-runtime-qualification` schema-v1 report passes only after
at least three all-on frames and a frame where every feature is simultaneously
supported and effective. Across that uninterrupted run it also requires:

- receiver-cache adaptive ABI/resources, a cache generation dispatch, and a
  forward receiver consumption;
- accelerated V2 transport with GPU-resident scheduling, exactly two sweeps,
  real accelerated dispatches, intermediate canonical publications, a final
  receiver-visible publication, and a current tail certificate;
- authoritative cooked C1 candidates, native builds/publications, DDGI rays,
  and a live TLAS;
- all C3 sample/train/build/validate stages, authoritative readback, a readable
  distribution, completed samples, and consuming rays;
- all C4 timed stages, a non-empty authoritative world-cache publication, a
  completed current receiver payload, and composite execution.

Adaptive-cache overflow, C1 query failure, C3/C4 fault, C4 overflow, or any
accelerated-tail timeout/no-progress/deadline recovery permanently fails the
run. Frames where any feature was not requested are counted and rejected.
Startup exceptions and cancellation publish a failed host-lifecycle report;
an abrupt native termination leaves an `in-progress` artifact, which is also
unambiguously non-qualifying. Performance snapshot schema 12 preserves the
same per-frame execution/publication boundaries and migrates older captures
without inferring work from mode bits.

A successful source build, cook, unit test, extension advertisement, or editor
toggle is not qualification. Only a report with `Status: "passed"` and
`Passed: true` establishes that all five paths were active and consumed in the
named scene/device run.
