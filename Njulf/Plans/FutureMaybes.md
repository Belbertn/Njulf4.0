No longer supported after this simplification:
•
GpuOcclusionCompactionPass: removed from the production render graph.
•
Current-frame Hi-Z visibility-list rewriting: draw lists are no longer rebuilt after DepthPrePass + HiZBuildPass.
•
Object-level Hi-Z occlusion inside GpuVisibilityPass: the visibility compute pass now only does object/frustum/LOD/material/shadow-list generation.
•
Post-occlusion compact draw counts: indirect counts now represent visibility-list candidates, not a second compacted “definitely visible after Hi-Z” list.
•
Visibility occlusion counters from the compute pass: OcclusionTested, OcclusionRejected, OcclusionAccepted, OcclusionSkipped from gpu_visibility.comp will no longer be meaningful.