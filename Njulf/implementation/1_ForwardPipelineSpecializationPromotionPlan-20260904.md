# Specialized Forward Pipeline Promotion

## Goal

Keep universal F1 for first-present startup, then promote eligible opaque draws to the existing specialized Forward families. This changes pipeline selection only; it does not add a shader permutation.

## Implementation

- Take one warmed Bistro baseline with the default startup mode and confirm the active Forward artifacts and absence of measured-frame pipeline creation.
- Extend the post-first-present scheduler to build the active base-pipeline bank, not only receiver-cache variants:
  - Taskless: F1 `CompactedFull`, F4 `CompactedSimple`, F5 `CompactedSimpleFullInput`.
  - Task-based: F0 `Full`, F2 `Simple`, F3 `SimpleFullInput`.
- Build every enabled sibling needed by the active scene, including exact/cache, hybrid-reflection, sparse-lobe, and enabled MRT variants. Keep a `MeshPipeline`-local readiness state and publish the bank atomically only after all required pipelines succeed. A partial or failed bank stays on the universal family.
- Do not cache a family resolution that was collapsed to the bootstrap pipeline. Reset readiness, cache entries, and owned specialized pipelines during pipeline recreation.
- Keep blocking and exhaustive startup behavior unchanged apart from using the same completed-bank publication rule.
- Refactor material routing so shading complexity is decided before alpha coverage:
  - Plain base PBR uses `Simple`.
  - Normal maps, UV transforms, secondary UVs, vertex color, and extension-free masking use `SimpleFullInput`.
  - Genuine material-extension payloads use `Full`, including extension-bearing masked materials.
  - Preserve masking in the existing independent coverage/classification flags so depth, alpha testing, and feedback behavior do not change.
- Apply the classification change to both CPU flat-draw and GPU-instance compaction paths and their scene-manifest logic.

## Compatibility

- Reuse the existing Forward artifacts and family values. Do not change public APIs, GPU buffers, descriptors, push constants, or material data.
- Universal F1 remains the permanent fallback.

## Verification

- Add focused tests for bootstrap fallback, atomic publication/failure, recreation, and non-sticky family resolution.
- Add routing tests for plain, normal-mapped, transformed, secondary-UV, vertex-colored, extension-free masked, and extension-bearing masked materials in both submission paths.
- Create and draw the active F1/F4/F5 families once under Vulkan validation and compare one transformed/masked smoke frame.
- Take one matching warmed Bistro candidate capture after promotion. Keep the change only if `ForwardPlusPass` improves clearly beyond jitter and whole-frame time does not materially regress.

## Out of Scope

- Forward shader-body and stage-interface changes belong to plans 2 and 3.
