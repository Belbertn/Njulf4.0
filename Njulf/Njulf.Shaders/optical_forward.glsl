// Included after the forward push constants. Export never samples a neighborhood.
vec3 opticalReflection = vec3(0.0), opticalTransmission = vec3(0.0);
vec3 opticalReflectionWeight = vec3(0.0), opticalTransmissionWeight = vec3(0.0);
vec3 opticalScatterNormal = vec3(0.0);
float opticalReflectionObserved = 1.0, opticalTransmissionObserved = 1.0;
float opticalReflectionDistance = 0.0, opticalTransmissionDistance = 0.0;
uint opticalReflectionSource = 0u, opticalTransmissionSource = 0u;
bool opticalExported = false;
#if FORWARD_TRANSPARENT_REFLECTIONS_ACTIVE && !defined(NJULF_VISIBILITY_COMPUTE) && !defined(NJULF_AUTOMATIC_PLANAR_CAPTURE)
#define OPTICAL_FORWARD_ACTIVE 1
layout(location=9) flat in uint opticalDrawOrdinal;
layout(location=10) in vec3 opticalPreviousWorld;
layout(location=11) perprimitiveEXT flat in uint opticalPrimitiveOrdinal;
// This descriptor view accesses only the dedicated optical allocation. Restrict
// prevents export stores from aliasing the ray shader's scene/material reads.
layout(set=0,binding=0) restrict buffer OpticalExportStorage { uint Words[]; } OpticalExportBuffers[];
#define BindlessStorageBuffers OpticalExportBuffers
#define OPTICAL_EXPORT_ONLY
#include "optical_layers.glsl"
#undef OPTICAL_EXPORT_ONLY
bool OpticalExportEnabled() { return (pc.Push.DiagnosticFlags & ((1u<<16u)|(1u<<31u))) == (1u<<16u); }
uint OpticalForwardBuffer() { return uint(OPTICAL_LAYER_BUFFER_BASE_INDEX)+pc.Push.CurrentFrameIndex; }
void OpticalInvalidatePixel()
{
    if (!OpticalExportEnabled()) return;
    uint b=OpticalForwardBuffer(), p=OpticalPixel(b,ivec2(gl_FragCoord.xy));
    uint old=atomicExchange(BindlessStorageBuffers[nonuniformEXT(b)].Words[p], 0x10000u);
    if (old<=OpticalWord(b,4u)) atomicAdd(BindlessStorageBuffers[nonuniformEXT(b)].Words[6u],1u);
}
void OpticalExport(GPUMaterialData material, vec3 geometricNormal, vec3 normal, float roughness, float alpha)
{
    opticalExported=true;
#if FORWARD_WEIGHTED_OIT
    if (alpha <= .001) return;
#endif
    if (!OpticalExportEnabled() || gl_HelperInvocation) return;
    if (!OpticalFinite(opticalReflection) || !OpticalFinite(opticalTransmission) ||
        !OpticalFinite(opticalReflectionWeight) || !OpticalFinite(opticalTransmissionWeight))
    { OpticalInvalidatePixel(); return; }
    uint b=OpticalForwardBuffer(), p=OpticalPixel(b,ivec2(gl_FragCoord.xy));
    uint slot=atomicAdd(BindlessStorageBuffers[nonuniformEXT(b)].Words[p],1u);
    if (slot>=OpticalWord(b,4u))
    {
        if (slot==OpticalWord(b,4u)) atomicAdd(BindlessStorageBuffers[nonuniformEXT(b)].Words[6u],1u);
        return;
    }
    uint index=atomicAdd(BindlessStorageBuffers[nonuniformEXT(b)].Words[5u],1u);
    if (index>=OpticalWord(b,3u)) { OpticalInvalidatePixel(); return; }
    OpticalStore(b,p+1u+slot,index);
    uint r=OPTICAL_HEADER_WORDS+OpticalWord(b,1u)*OpticalWord(b,2u)*OpticalPixelWords(b)+index*OPTICAL_RECORD_WORDS;
    GPUObjectData obj=ReadInstanceData(pc.Push.CurrentFrameIndex,fragObjectIndex);
    OpticalStore(b,r,obj.NearFieldStableObjectId);
    OpticalStore(b,r+1u,obj.NearFieldStableMaterialId ^ (material.MaterialRevision*0x9e3779b9u));
    // Water's animated scatter normal must reject stale wave detail as well.
    vec3 filterNormal = dot(opticalScatterNormal,opticalScatterNormal)>0.0 ? opticalScatterNormal : normal;
    OpticalStore(b,r+2u,OpticalPackNormal(geometricNormal)); OpticalStore(b,r+3u,OpticalPackNormal(filterNormal));
    OpticalStoreVec(b,r+4u,fragWorldPosition); OpticalStore(b,r+7u,floatBitsToUint(roughness));
    OpticalStoreVec(b,r+8u,opticalPreviousWorld);
    OpticalStore(b,r+11u,floatBitsToUint(length(fragWorldPosition-pc.Push.CameraPosition)));
    OpticalStoreVec(b,r+12u,opticalReflection); OpticalStore(b,r+15u,floatBitsToUint(opticalReflectionObserved));
    OpticalStoreVec(b,r+16u,opticalTransmission); OpticalStore(b,r+19u,floatBitsToUint(opticalTransmissionObserved));
    OpticalStoreVec(b,r+20u,opticalReflectionWeight); OpticalStore(b,r+23u,floatBitsToUint(clamp(alpha,0.0,1.0)));
    OpticalStoreVec(b,r+24u,opticalTransmissionWeight); OpticalStore(b,r+27u,floatBitsToUint(gl_FragCoord.z));
    OpticalStore(b,r+28u,floatBitsToUint(opticalReflectionDistance)); OpticalStore(b,r+29u,floatBitsToUint(opticalTransmissionDistance));
    OpticalStore(b,r+30u,(opticalReflectionObserved>0.0?opticalReflectionSource:0u) |
        ((opticalTransmissionObserved>0.0?opticalTransmissionSource:0u)<<16u));
    OpticalStore(b,r+31u,opticalDrawOrdinal); OpticalStore(b,r+32u,opticalPrimitiveOrdinal);
    OpticalStore(b,r+33u,gl_FrontFacing?1u:0u);
    vec4 previousPosition=vec4(opticalPreviousWorld,1.0);
    // CPU matrices are row-major; each stored vec4 is a column of the GLSL matrix.
    mat4 previousVP=mat4(vec4(OpticalVec(b,16u),OpticalFloat(b,19u)),
        vec4(OpticalVec(b,20u),OpticalFloat(b,23u)), vec4(OpticalVec(b,24u),OpticalFloat(b,27u)),
        vec4(OpticalVec(b,28u),OpticalFloat(b,31u)));
    vec4 clip=previousVP*previousPosition;
    vec2 uv=clip.xy/max(clip.w,1e-8)*.5+.5;
    if (clip.w<=0.0 || obj.SkinningEnabled!=0) uv=vec2(-10.0);
    OpticalStore(b,r+34u,floatBitsToUint(uv.x)); OpticalStore(b,r+35u,floatBitsToUint(uv.y));
}
#undef BindlessStorageBuffers
#else
#define OPTICAL_FORWARD_ACTIVE 0
#endif
