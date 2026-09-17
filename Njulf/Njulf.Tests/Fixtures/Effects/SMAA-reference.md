# Independent SMAA reference

`SMAA.hlsl` is the upstream reference, including its original license, from
https://github.com/iryoku/smaa/blob/master/SMAA.hlsl (retrieved 2026-09-15).
SHA-256: `4db53d92ef0b45661f8450303589ffabce93c352cae4282a6f32dd2b80efbeb9`.
The three wrappers run SMAA High at 128x96 without predication. Binary-color
fixtures have identical linear and sRGB edge inputs, while neighborhood
blending uses linear color. Keep this oracle independent of production edits.

The blend wrapper quantizes the reference weights to RGBA8 UNORM, matching
the renderer's weight attachment. The comparison permits one weight step in
linear light plus screenshot quantization. This is deliberately not a loose
sRGB tolerance: one linear step near black spans about 13 display codes.
The input wrapper samples the whole pattern texture into the post-effect chain.
