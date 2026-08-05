# Simple-DDGI-Only Compact Receiver Implementation Status

- Date: 2026-08-05
- Base commit: `4e5b6f32939dee8acf53830819582c483ef6a102`
- State: implementation and automated verification complete; target-hardware
  performance/forced-async qualification remains evidence-gated
- Receiver Release manifest SHA-256:
  `589cdad1a8702f67132b227827da3b91c3c9b2f2348b89b1d603324981704ae7`
- Compiler environment: Vulkan SDK 1.4.335.0, Vulkan 1.3 target

## Result

The renderer remains Simple-DDGI-only. No SSGI or legacy-DDGI runtime path was
added. Production forward, transparent, weighted OIT, foliage, fog, and particle
receivers consume a dedicated 16-byte compact probe projection. Update,
scheduler, relocation, transport, audit, and publication shaders retain the
32-byte authoritative compute state.

Each surviving gather corner performs one aligned `uvec4` receiver load, rejects
lifecycle-invalid state before atlas access, validates a stored logical atlas
address, and reuses one derived address for irradiance and visibility. Release
receiver SPIR-V has no compute-state or source-cache descriptor access and no
diagnostic atomics.

## Implemented contracts

- Exact 16-byte CPU/GPU ABI with spacing-relative SNORM16 relocation, UNORM16
  active weight, independent receiver rejection flags, coherent-publication bit,
  and invalid atlas sentinel.
- Round-to-even CPU/GLSL packers, finite/range validation, threshold-safe weight
  encoding, and fail-closed invalid records.
- Stable appended bindless slot, exact allocation/admission accounting, invalid
  dummy binding, lifecycle retirement, and disabled-mode retention accounting.
- No stable-frame receiver upload. CPU writes are limited to bounded fail-closed
  invalidation or a whole-buffer invalid fill when fragmentation is excessive.
- Allocation, clear, topology replacement, toroidal scroll exposure, generation
  advance, resize, reload, and disabled transitions invalidate before receivers.
- V1 and V2 publication validate queue/state generation. Resident scheduling
  validates and packs before public atlas/state writes, then commits the compact
  coherent flag last.
- Optional sampled images and compact records share the same completed
  transaction. GPU-resident mode mirrors private irradiance/visibility before
  canonical/receiver commit.
- Production gather result/debug payload split. Source-cache comparison,
  rejection masks, diagnostic counters, and debug resampling are absent from
  Release, ShippingPerformance, and ProfileSymbols receiver binaries.
- Production debug-view policy reports receiver-only views unavailable when the
  diagnostic shader sidecar was not compiled instead of silently selecting dead
  shader branches.
- Exact render-graph receiver reads/writes and async resource identity/bindings;
  update/solver passes continue to declare authoritative compute state.
- Async certification remains pending and is not promoted by this change.
- Persisted/live diagnostics include receiver capacity, bytes, resource
  generation, invalidation bytes/ranges/full-clear state, and fence-complete
  publication count.
- Shader builds remove obsolete SSGI and pre-specialization forward artifacts
  from every configuration output directory.

## Automated evidence

Fresh builds after the parallel tail-certified solver changes stabilized:

| Configuration | Result |
|---|---|
| Debug solution rebuild | Passed; 0 errors |
| Release solution rebuild | Passed; 0 errors |
| ProfileSymbols shader rebuild | Passed; production SPIR-V gates passed |
| DetailedInvestigation solution build | Passed; 0 errors |

Two existing obsolete-alias warnings remain in
`GlobalIlluminationDefaultsTests`; they are unrelated to the compact receiver.

Test results:

- Debug: 1,779 passed, 0 failed.
- Release: 1,779 passed, 0 failed.
- DetailedInvestigation focused receiver/graph/diagnostic set: 40 passed,
  0 failed.

Release and ProfileSymbols build gates:

- `spirv-val --target-env vulkan1.3` passed for all 12 production receiver
  modules.
- All 12 modules contain exactly one compact vector access per inlined gather
  site and no access to bindless compute-state slot 156 or source-cache slot
  160.
- All 12 contain no receiver diagnostic `OpAtomicIAdd`.
- The production atomic audit passed for 16 non-scheduler modules, with exact
  functional counts retained only in the three update/audit modules that need
  them.
- No obsolete SSGI or generic pre-specialization forward SPIR-V remains under
  `Njulf.Shaders/obj` or `Njulf.Shaders/bin`.

## Evidence still requiring target hardware

The implementation does not claim the plan's performance promotion threshold or
async correctness certificate from compilation/unit tests. The following remain
deliberate hardware qualification work:

- Vulkan validation for graphics-only and forced-async execution with sampled
  atlas on/off, resize/reload, motion, and two frames in flight;
- locked HDR image comparisons for scroll, topology, foliage, fog, particles,
  thin walls, and source changes;
- the identity-locked three-run RTX 3060 Laptop timing protocol, shader register
  and occupancy capture, and the required forward P95 improvement gate; and
- long target-device soak/device-loss testing.

Until that evidence exists, `Auto` async remains uncertified and the renderer
keeps its established graphics/CPU fallback policy.
