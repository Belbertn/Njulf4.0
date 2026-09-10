#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "anti_aliasing_push.glsl"
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
void main() {
    color = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], uv);
    color.rgb = clamp(color.rgb, 0.0, 1.0);
    if (pc.OutputToSrgb != 0u) {
        bvec3 cutoff = lessThanEqual(color.rgb, vec3(0.0031308));
        color.rgb = mix(1.055 * pow(color.rgb, vec3(1.0/2.4)) - 0.055, color.rgb * 12.92, cutoff);
    }
}
