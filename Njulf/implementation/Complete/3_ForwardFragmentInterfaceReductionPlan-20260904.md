# Forward Fragment Interface Reduction

## Goal

Reduce Forward fragment register and stage-interface pressure without changing material or lighting behavior. Apply one interface change at a time after plan 1 makes the specialized families observable.

## Candidates

1. Remove interpolated world position. Reconstruct it once with the renderer's canonical screen-to-world helper, preserving jitter, Vulkan Y convention, reverse-Z, trace-resolution inputs, and capture views.
2. Pack the flat material, object, and meshlet IDs into one `uvec3` location. Keep all components 32-bit and treat this as a separate experiment because it reduces locations, not transported words.
3. Match interfaces to existing families:
   - `Simple`: normal, primary UV, packed IDs.
   - `SimpleFullInput`: `Simple` plus tangent, secondary UV, and vertex color.
   - `Full` and transparent: retain the complete input set required by material extensions.

Update regular and compacted mesh/fragment artifact pairs together. Do not add a new family, partial vertex-buffer format, descriptor change, or public setting. Partial vertex fetch is a separate storage/skinning ABI project and is out of scope.

## Verification

- For each candidate, fresh-build only the affected artifacts, run `spirv-val`, and take one matching warmed capture before moving to the next candidate.
- Add one focused SPIR-V reflection test for location, type, interpolation, and mesh/fragment agreement in `Simple`, `SimpleFullInput`, and `Full`.
- Run one Vulkan-validation draw of each affected family. For world reconstruction, compare one representative HDR frame.
- Record registers, spills/local memory, interface stalls, `ForwardPlusPass`, and whole-frame time. Keep a candidate only when the interface evidence and timing clearly improve without a rendering regression.
