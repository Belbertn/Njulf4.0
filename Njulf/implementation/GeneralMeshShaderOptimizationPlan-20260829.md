# AMD-Informed Mesh Shader Optimization Plan

Status: Proposed  
Date: 2026-08-29

## Summary

Enable the portable, low-complexity AMD recommendations by default while
retaining the current 36-byte meshlet ABI and qualified 48-vertex/64-primitive
profile. Keep alternate meshlet sizes and workgroup sizes as explicit test
modes usable on any supported GPU; do not introduce vendor whitelists or
automatic AMD-specific behavior.

## Implementation Changes

1. **Establish device and shader contracts**
   - Query `VkPhysicalDeviceMeshShaderPropertiesEXT` during device selection.
   - Validate workgroup, output, payload, and shared-memory limits before
     creating pipelines.
   - Expose the limits, AMD preference flags, selected permutation, and
     fallback reason through renderer diagnostics.

2. **Make production submission taskless**
   - Continue using exact compute-compacted indirect streams for opaque, depth,
     shadow, and motion passes.
   - Convert transparent rendering to the taskless compacted mesh shader
     because its input list is already exact and task culling is disabled.
   - Complete foliage taskless submission through the existing foliage plan.
   - Retain task shaders only as an explicitly selected compatibility path;
     production diagnostics must report zero task workgroups.

3. **Tighten and standardize mesh shaders**
   - Build shared permutations for `48v/64p x 64 threads`, `48v/64p x 128`,
     `64v/126p x 64`, and `64v/126p x 128`.
   - Give every compacted shader exact `max_vertices` and `max_primitives`
     declarations.
   - Call `SetMeshOutputsEXT` immediately after validating the command and
     counts, before vertex or attribute loads.
   - Use direct strided exports: invocation `i` writes element `i`, then
     `i + workgroupSize`; prohibit export atomics and large shared staging.
   - Keep culling, LOD selection, and compaction in compute shaders. Mesh
     shaders only fetch, transform, and export.
   - Preserve the current scalar shared state and packed ABI; do not introduce
     literal AMD 128-vertex/256-primitive meshlets.

4. **Add hardware-testable controls**
   - Add advanced `MeshShaderTuningMode` values: `Auto`, the four taskless
     permutations above, and `CompatibilityTask`.
   - Default `Auto` to taskless `48v/64p x 64`; select the wide taskless
     permutation when loaded content exceeds 48/64.
   - Add the `connected-64v-126t` cooking profile and an AssetTool
     `--meshlet-profile` option for controlled recooks.
   - Validate uploaded meshlet counts against the active output contract and
     fail safely to a compatible wide pipeline with a visible diagnostic.
   - Keep foliage authored meshlets at 48/64 and procedural grass at its exact
     64-vertex/32-primitive contract.

## Test Plan

- Compile and reflect every permutation, verifying local size, output limits,
  topology, push-constant compatibility, and absence/presence of task stages.
- Test profile validation, automatic wide fallback, forced modes, unsupported
  device limits, and compatibility rendering.
- Compare opaque, masked, transparent, shadow, motion-vector, skinned, and
  dense-foliage images; preserve transparent order and require zero validation,
  overflow, bounds, and false-cull errors.
- Run paired Release captures on any available hardware, recording three runs
  of 1,000 frames per variant. Report CPU/GPU P50/P95/P99, mesh/task
  invocations, emitted geometry, cache traffic, VGPR/LDS use, occupancy, and
  export stalls.
- Keep the default unless a candidate improves GPU P95 by at least 3% without
  more than 2% CPU/GPU regression or any quality regression.

## Assumptions and Defaults

- Safe taskless and tight-output changes ship enabled without hardware
  qualification.
- Experimental size/workgroup variants remain manually selectable so they can
  be tested on any GPU.
- No cooked-format or `GPUPackedMeshlet` ABI revision is introduced.
- Per-primitive attribute migration is excluded initially: current
  draw-constant IDs are cheaper to retain until profiling proves export
  pressure warrants another shader interface.
