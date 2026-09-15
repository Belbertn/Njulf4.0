// Read frame constants directly from the uniform buffer index. Copying the
// complete forward ABI into per-invocation private state raises register pressure.
layout(push_constant) uniform OpticalPush {
 uint Current;uint Previous;uint Step;uint Reset;uint Weighted;uint Bypass;uint Padding0;uint Padding1;
} optical;
layout(set=0,binding=0) readonly buffer OpticalForwardFrameStorage {
 GPUForwardPushConstants Push;
} OpticalForwardFrames[];
#define pc OpticalForwardFrames[optical.Current]
