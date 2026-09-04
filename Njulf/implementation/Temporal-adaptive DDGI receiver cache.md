Yes, but probably through an architectural redesign rather than tuning thresholds.
The algorithm is reasonable; the current integration loses because it must choose between two bad options:
•
One combined shader: cache lookup plus exact fallback increases register pressure and divergence.
•
Split shaders: cache-accepted and fallback geometry are rasterized separately, duplicating work.
A stronger Njulf implementation would:
•
Classify opaque pixels once using the existing depth/visibility data.
•
Compact them into “cache-safe” and “exact” screen-space work lists.
•
Shade both lists in compute, avoiding a second geometry rasterization.
•
Include primitive/material identity in validation, not only depth and normals.
•
Keep thin, animated, alpha-masked, and transparent geometry exact.
•
Use multiple surface representatives near discontinuities or simply reject those tiles.
•
Bound cache age and invalidate aggressively on lighting or geometry changes.
That could make the cache profitable on large, stable opaque surfaces. However, the present result is far enough from its gates that small optimizations will not suffice. The most promising fix is eliminating duplicated raster work and the large divergent forward shader; a “smarter temporal filter” alone would not solve it.
It may also turn out that optimizing the exact gather—fewer probe/SH reads, better subgroup sharing, and stronger shader specialization—is simpler and faster than maintaining a receiver cache at all.