1.
Pre-index render graph barriers instead of scanning every pass totalDrawUs=482ms is far larger than the named pass timings. A likely gap is barrier execution: VulkanRenderer.cs allocates new lists and scans _renderGraph.BarrierPlan.Passes for every before/after pass barrier call. Fix: compile barriers into beforeByPass / afterOwnershipReleaseByProducer dictionaries once when the render graph is compiled, reuse scratch arrays, and add CpuRenderGraphBarrierUs diagnostics.
2.
Finish the GPU scene migration or remove its hot-path cost VulkanRenderer uploads GpuSceneBufferSet every frame at VulkanRenderer.cs, but gpu_visibility.comp still reads legacy GPUObjectData through ReadInstanceData at gpu_visibility.comp and common.glsl. Fix: either move visibility/task/mesh shaders to GPU_SCENE_* buffers, or gate GpuSceneBufferSet.ApplyUploadPlan and CopyCurrentTransformsToPrevious until those buffers are actually consumed.
3.
Stop rebuilding camera-dependent CPU scene payload for GPU-driven visibility SceneCullingSignature includes the view-projection matrix, so camera movement forces payloadRebuilt=1 every frame at SceneDataBuilder.cs. Even with CPU meshlet lists disabled, object culling and counts are rebuilt. Fix: split static object upload from diagnostics-only CPU culling. In GPU-driven mode, avoid CPU frustum tests unless debug overlays or CPU snapshots need them.
4.
Stop uploading legacy instance data unconditionally The old instance buffer is uploaded every frame with contentChanged: true at SceneDataBuilder.cs. Fix: track dirty transforms/ranges or move shaders to GPU scene transforms. This directly attacks uploadUs, objectBytes, and the per-frame staging churn.

Post-Processing
5.
Let the graph own image transitions Hi-Z, bloom, AO blur, and SMAA all declare graph resources but also manually transition targets inside passes. Examples: HiZBuildPass.cs, BloomPass.cs, AntiAliasingPass.cs. Fix: remove redundant per-pass transitions after validating graph barriers. This should reduce the hizRecordUs=16051, bloom down/up record costs, AO blur cost, and SMAA record cost.
6.
Make Hi-Z adaptive earlier Adaptive Hi-Z only learns after 512 occlusion tests at VulkanRenderer.cs. This frame has zero completed forward counters but still pays for Hi-Z. Fix: suppress Hi-Z when recent visibility counters are unavailable/low, object count is small, or last Hi-Z CPU/GPU cost exceeds observed culling benefit.
7.
Use cheaper dev defaults for this scene Current high defaults enable bloom 6 mips, SSAO + blur, SMAA medium, fog, reflections, shadows, particles. For this diagnostic, quick knobs are: Bloom.MipCount=4, AntiAliasing.Mode=Fxaa, AmbientOcclusion.SampleCount=8, possibly disable Hi-Z until counters prove useful.

Particles / Materials
8.
Particle “Gpu” mode is still CPU simulation ParticleSimulationPass.Execute only sets the mode label; CPU work happens in ParticleSystemManager.Update. See ParticleSimulationPass.cs and VulkanRenderer.cs. Fix: implement real GPU spawn/sim buffers. The simUs=15717 and buildUs=4785 for 88 particles is too high to ignore.
9.
Skip material barriers/descriptor writes when nothing changed UploadMaterials records read barriers and re-registers bindless buffers even when no upload occurred. That likely contributes to materialUploadUs=4043. Fix: only record transfer-to-read barriers after actual material uploads or buffer reallocations; register bindless buffers only when handles change.