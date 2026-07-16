# Forward-Pass Medium-Priority Fix Plan

This plan excludes SSGI-specific findings.

1. **Correct emissive defaults.** Treat a missing emissive texture as a white multiplicative sample so `material.Emissive` works independently. Keep black as the default emissive factor, not the default texture contribution.

2. **Align material upload and shader semantics.** Give the emissive-texture binding an explicit presence flag or documented fallback contract, then use that contract consistently in material upload, bindless defaults, and `forward.frag`.

3. **Fix tangent-space transforms.** Transform normals with the world inverse-transpose matrix, transform tangents with the world linear matrix, and orthonormalize the tangent against the transformed normal before shading.

4. **Preserve tangent handedness.** Fold the sign of mirrored object transforms into `tangent.w` and build the bitangent from the corrected normal, tangent, and handedness. Put the TBN construction in a shared shader helper used by applicable material passes.

5. **Implement local reflection-probe storage.** Allocate cubemap-array layers, assign a stable layer to every live probe, and release or recycle layers when probes are removed.

6. **Complete probe capture and sampling.** Render and prefilter all six probe faces, publish completed captures safely, bind the result as a cubemap array, and sample each probe through its `CubemapArrayIndex`. Use the global environment only as an explicit fallback for invalid or uncaptured probes.

7. **Add focused regression coverage.** Test emissive-factor-only materials, textured emissive materials, non-uniform and negative object scales, reflection-probe overlap, probe removal/reuse, and scenes where local probe radiance differs visibly from the global environment.

## Completion criteria

- A nonzero emissive factor produces emission without requiring a texture.
- Normal maps remain stable under uniform, non-uniform, and mirrored transforms.
- Every captured local probe samples its own cubemap-array layer.
- Uncaptured probes fall back predictably without exposing stale layers.
- Shader validation and the relevant renderer tests pass without new Vulkan validation errors.
