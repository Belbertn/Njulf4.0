
1. Stabilize the existing CSM

- Replace the threshold-based up-vector switch with a continuously transported light-space basis.
- Fit each cascade to a rotation-invariant bounding sphere or fixed extent.
- Round rather than floor the texel-snapped center.
- Stabilize depth bounds separately from XY coverage.
- Retain the existing cascade overlap/blending.
- Add a short, rejection-aware temporal filter to the resolved shadow factor only if remaining pixel stepping is visible.

This should remove the apparent reset without changing the shadow architecture.

2. Add ray-query shadows as an optional Ultra path

A camera-space shadow-mask pass could cast one first-hit ray toward the sun for each visible pixel. Vulkan explicitly supports this efficient first-hit shadow-query pattern. [Vulkan ray-query shadow documentation](https://docs.vulkan.org/tutorial/latest/courses/18_Ray_tracing/03_Ray_query_shadows.html)

This engine already has TLAS/BLAS construction and ray-query visibility code for DDGI, so it has useful foundations. Advantages include:

- No cascade seams, projection changes, or shadow-map resolution.
- Exact hard-shadow geometry.
- Shared acceleration structures with DDGI.
- Natural support for long-distance shadows when geometry is resident.

However, it still requires:

- Full-screen traversal cost.
- Correct skinned, foliage, alpha-mask, and transparent-shadow semantics.
- A residency policy suitable for camera-visible shadows, not merely GI.
- Temporal/spatial denoising if sampling a finite-sized sun for soft shadows.

NVIDIA notes that ray-tracing overhead is easier to amortize when GI, glossy effects, and shadow rays share the infrastructure. [NVIDIA RTXGI overview](https://developer.nvidia.com/blog/rtx-global-illumination-part-i/)

3. Long-term hybrid

The strongest practical configuration would likely be:

- Stable CSM for inexpensive general directional shadows.
- Ray-query contact shadows near the camera or for selected high-quality receivers.
- DDGI for indirect diffuse.
- Optional full ray-query sun shadows on an Ultra tier.