#version 460

layout(push_constant) uniform AlphaVisibilityPushConstants
{
    uint DistanceIndex;
    uint Width;
    uint Height;
    float Distance;
    float RayTextureLod;
    float AlphaCutoff;
    uint SampleCount;
    uint DistanceCount;
} pc;

layout(location = 0) out vec2 fragTexCoord;

const float ALPHA_VISIBILITY_CARD_HALF_SIZE = 1.0;
const float ALPHA_VISIBILITY_TAN_HALF_VERTICAL_FOV = 0.5773502691896258;

void main()
{
    const vec2 positions[6] = vec2[6](
        vec2(-1.0, -1.0),
        vec2( 1.0, -1.0),
        vec2( 1.0,  1.0),
        vec2(-1.0, -1.0),
        vec2( 1.0,  1.0),
        vec2(-1.0,  1.0));
    const vec2 texCoords[6] = vec2[6](
        vec2(0.0, 0.0),
        vec2(1.0, 0.0),
        vec2(1.0, 1.0),
        vec2(0.0, 0.0),
        vec2(1.0, 1.0),
        vec2(0.0, 1.0));

    float aspect = float(pc.Width) / float(pc.Height);
    vec2 projectedHalfExtent = vec2(
        ALPHA_VISIBILITY_CARD_HALF_SIZE /
            (pc.Distance * ALPHA_VISIBILITY_TAN_HALF_VERTICAL_FOV * aspect),
        ALPHA_VISIBILITY_CARD_HALF_SIZE /
            (pc.Distance * ALPHA_VISIBILITY_TAN_HALF_VERTICAL_FOV));
    vec2 cardPosition = positions[gl_VertexIndex];
    gl_Position = vec4(cardPosition * projectedHalfExtent, 0.0, 1.0);
    fragTexCoord = texCoords[gl_VertexIndex];
}
