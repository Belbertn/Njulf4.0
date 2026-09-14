#ifndef NJULF_AMD_REFLECTION_CALLBACKS
#define NJULF_AMD_REFLECTION_CALLBACKS
#include "common.glsl"
#include "hybrid_reflection_compute.glsl"
#define FFX_GPU 1
#define FFX_GLSL 1
#ifndef FFX_HALF
#define FFX_HALF 0
#endif
#include "ThirdParty/FidelityFX/ffx_core.h"
layout(push_constant) uniform AmdReflectionPush { uint Current; uint Previous; uint Width; uint Height; uint Reset; uint Stage; } ar;
ivec2 ArDispatchPixel;
bool ArInside(ivec2 p) { return all(greaterThanEqual(p,ivec2(0))) && all(lessThan(p,ivec2(ar.Width,ar.Height))); }
ivec2 ArClamp(ivec2 p) { return clamp(p,ivec2(0),ivec2(ar.Width,ar.Height)-1); }
uint ArAddress(ivec2 p,uint field) { p=ArClamp(p); return 128u+(uint(p.y)*ar.Width+uint(p.x))*16u+field; }
uint ArRead(uint b,ivec2 p,uint f) { return ReadStorageWord(b,ArAddress(p,f)); }
float ArFloat(uint b,ivec2 p,uint f) { return uintBitsToFloat(ArRead(b,p,f)); }
void ArWrite(ivec2 p,uint f,uint v) { if(ArInside(p)) WriteStorageWord(ar.Current,ArAddress(p,f),v); }
void ArStore(ivec2 p,uint f,float v) { ArWrite(p,f,floatBitsToUint(v)); }
vec3 ArRgb(uint b,ivec2 p,uint f) { return vec3(unpackHalf2x16(ArRead(b,p,f)),unpackHalf2x16(ArRead(b,p,f+1u)).x); }
void ArStoreRgb(ivec2 p,uint f,vec3 v) { v=clamp(v,vec3(0),vec3(65504)); ArWrite(p,f,packHalf2x16(v.xy)); ArWrite(p,f+1u,packHalf2x16(vec2(v.z,0))); }
mat4 ArMatrix(uint f) { return mat4(ReadStorageVec4(ar.Current,f),ReadStorageVec4(ar.Current,f+4u),ReadStorageVec4(ar.Current,f+8u),ReadStorageVec4(ar.Current,f+12u)); }
mat4 ArFlip() { mat4 m=mat4(1); m[1][1]=-1; return m; }
mat4 InvProjection() { return ArMatrix(0u)*ArFlip(); }
mat4 InvView() { return ArMatrix(16u); }
mat4 PrevViewProjection() { return ArFlip()*ArMatrix(32u); }
uvec2 RenderSize() { return uvec2(ar.Width,ar.Height); }
vec2 InverseRenderSize() { return 1.0/vec2(RenderSize()); }
float TemporalStabilityFactor() { return 0.95; }
float RoughnessThreshold() { return 1.0; }
uint GetDenoiserTile(uint group) { uvec2 p=uvec2(group%((ar.Width+7u)/8u),group/((ar.Width+7u)/8u))*8u; return p.x|(p.y<<16u); }
vec3 FFX_DNSR_Reflections_LoadWorldSpaceNormal(ivec2 p) { return HybridReflectionTraceNormal(texelFetch(HybridReceiverPayload,ArClamp(p),0)); }
vec3 FFX_DENOISER_LoadWorldSpaceNormal(ivec2 p) { return FFX_DNSR_Reflections_LoadWorldSpaceNormal(p); }
float FFX_DNSR_Reflections_LoadRoughness(ivec2 p) { uvec4 payload=texelFetch(HybridReceiverPayload,ArClamp(p),0); float r=HybridReflectionPayloadPhysicalRoughness(payload); return HybridReflectionPayloadValid(payload)?r*r:1.0; }
float FFX_DENOISER_LoadDepth(ivec2 p,int mip) { return texelFetch(HybridSceneDepth,ArClamp(p),0).x; }
float FFX_DNSR_Reflections_LoadDepth(ivec2 p) { return FFX_DENOISER_LoadDepth(p,0); }
vec2 FFX_DNSR_Reflections_LoadMotionVector(ivec2 p) { return -texelFetch(HybridMotionVectors,ArClamp(p),0).xy; }
float FFX_DNSR_Reflections_LoadRayLength(ivec2 p) { return ArFloat(ar.Current,p,0u); }
vec3 FFX_DNSR_Reflections_LoadRadiance(ivec2 p) { return ar.Stage==3u?ArRgb(ar.Current,p,10u):ArRgb(ar.Current,p,13u); }
vec3 LoadRadiance(ivec3 p) { return FFX_DNSR_Reflections_LoadRadiance(p.xy); }
float FFX_DNSR_Reflections_LoadVariance(ivec2 p) { return ArFloat(ar.Current,p,ar.Stage==3u?12u:7u); }
float LoadVariance(ivec3 p) { return FFX_DNSR_Reflections_LoadVariance(p.xy); }
float FFX_DNSR_Reflections_LoadNumSamples(ivec2 p) { return ArFloat(ar.Current,p,6u); }
vec3 FFX_DNSR_Reflections_LoadRadianceReprojected(ivec2 p) { return ArRgb(ar.Current,p,8u); }
void FFX_DNSR_Reflections_StoreVariance(ivec2 p,float v) { ArStore(p,7u,v); }
void FFX_DNSR_Reflections_StoreNumSamples(ivec2 p,float v) { ArStore(p,6u,ar.Reset!=0u?1.0:min(v,32.0)); }
void FFX_DNSR_Reflections_StoreRadianceReprojected(ivec2 p,vec3 v) { ArStoreRgb(p,8u,ar.Reset!=0u?ArRgb(ar.Current,p,13u):v); }
void FFX_DNSR_Reflections_StorePrefilteredReflections(ivec2 p,vec3 v,float variance) { ArStoreRgb(p,10u,v); ArStore(p,12u,variance); }
vec3 FFX_DNSR_Reflections_LoadRadianceHistory(ivec2 p) { return ar.Reset!=0u?vec3(0):imageLoad(HybridHistoryPrevious,ArClamp(p)).rgb; }
vec3 FFX_DNSR_Reflections_LoadWorldSpaceNormalHistory(ivec2 p) { return ar.Reset!=0u?vec3(0):NjulfHybridReflectionOctDecode(ArRead(ar.Previous,p,1u)); }
float FFX_DNSR_Reflections_LoadDepthHistory(ivec2 p) { return ar.Reset!=0u?0.0:ArFloat(ar.Previous,p,4u); }
// AMD's history samplers require bilinear filtering. Private SSBO guides and
// storage-image history use the same four taps and texel-centre convention.
vec3 ArHistory(ivec2 p,int kind) {
 if(ar.Reset!=0u) return vec3(0);
 if(kind==0) return FFX_DNSR_Reflections_LoadRadianceHistory(p);
 if(kind==1) return vec3(imageLoad(HybridMomentsPrevious,ArClamp(p)).x);
 if(kind==2) {
  uint identity=HybridReceiverIdentity(texelFetch(HybridReceiverPayload,ArClamp(ArDispatchPixel),0));
  uint source=HybridMetadataSource(ArRead(ar.Current,ArDispatchPixel,15u));
  return identity==ArRead(ar.Previous,p,3u) && source==ArRead(ar.Previous,p,5u)?vec3(ArFloat(ar.Previous,p,6u)):vec3(0);
 }
 if(kind==3) return FFX_DNSR_Reflections_LoadWorldSpaceNormalHistory(p);
 return vec3(ArFloat(ar.Previous,p,kind==4?2u:4u));
}
vec3 ArSample(vec2 uv,int kind) { vec2 pos=uv*vec2(RenderSize())-0.5; ivec2 p=ivec2(floor(pos)); vec2 f=fract(pos); return mix(mix(ArHistory(p,kind),ArHistory(p+ivec2(1,0),kind),f.x),mix(ArHistory(p+ivec2(0,1),kind),ArHistory(p+1,kind),f.x),f.y); }
vec3 FFX_DNSR_Reflections_SampleRadianceHistory(vec2 uv) { return ArSample(uv,0); }
float FFX_DNSR_Reflections_SampleVarianceHistory(vec2 uv) { return ArSample(uv,1).x; }
float FFX_DNSR_Reflections_SampleNumSamplesHistory(vec2 uv) { return ArSample(uv,2).x; }
vec3 FFX_DNSR_Reflections_SampleWorldSpaceNormalHistory(vec2 uv) { return ArSample(uv,3); }
float FFX_DNSR_Reflections_SampleRoughnessHistory(vec2 uv) { return ArSample(uv,4).x; }
float FFX_DNSR_Reflections_SampleDepthHistory(vec2 uv) { return ArSample(uv,5).x; }
uint ArAverageAddress(ivec2 p) { ivec2 size=ivec2((RenderSize()+7u)/8u); p=clamp(p,ivec2(0),size-1); return 128u+ar.Width*ar.Height*16u+uint(p.y*size.x+p.x)*2u; }
void FFX_DNSR_Reflections_StoreAverageRadiance(ivec2 p,vec3 v) { uint a=ArAverageAddress(p); WriteStorageWord(ar.Current,a,packHalf2x16(v.xy)); WriteStorageWord(ar.Current,a+1u,packHalf2x16(vec2(v.z,0))); }
vec3 ArAverage(ivec2 p) { uint a=ArAverageAddress(p); return vec3(unpackHalf2x16(ReadStorageWord(ar.Current,a)),unpackHalf2x16(ReadStorageWord(ar.Current,a+1u)).x); }
vec3 FFX_DNSR_Reflections_SampleAverageRadiance(vec2 uv) { vec2 pos=uv*vec2((RenderSize()+7u)/8u)-0.5; ivec2 p=ivec2(floor(pos)); vec2 f=fract(pos); return mix(mix(ArAverage(p),ArAverage(p+ivec2(1,0)),f.x),mix(ArAverage(p+ivec2(0,1)),ArAverage(p+1),f.x),f.y); }
void FFX_DNSR_Reflections_StoreTemporalAccumulation(ivec2 p,vec3 v,float variance) {
 if(!ArInside(p)) return;
 uvec4 payload=texelFetch(HybridReceiverPayload,p,0);
 bool valid=HybridReflectionPayloadValid(payload);
 vec4 raw=vec4(ArRgb(ar.Current,p,13u),unpackHalf2x16(ArRead(ar.Current,p,14u)).y);
 if(!HybridFinite(v)) v=raw.rgb;
 imageStore(HybridHistoryCurrent,p,valid?vec4(max(v,vec3(0)),raw.a):vec4(0));
 imageStore(HybridMomentsCurrent,p,vec4(valid && HybridFinite(variance)?variance:0));
 vec3 normal=FFX_DNSR_Reflections_LoadWorldSpaceNormal(p);
 float depth=FFX_DNSR_Reflections_LoadDepth(p);
 uint source=HybridMetadataSource(ArRead(ar.Current,p,15u));
 uint reason=HybridMetadataReason(ArRead(ar.Current,p,15u));
 uint sparseState=reason==HYBRID_REFLECTION_REASON_RESOLUTION_SKIP?HYBRID_REFLECTION_HISTORY_SPARSE_RESOLUTION:
     reason==HYBRID_REFLECTION_REASON_RAY_BUDGET?HYBRID_REFLECTION_HISTORY_SPARSE_RAY_BUDGET:HYBRID_REFLECTION_HISTORY_SPARSE_NONE;
 vec2 previousUv=(vec2(p)+.5)*InverseRenderSize()-texelFetch(HybridMotionVectors,p,0).xy;
 ivec2 previousPixel=ivec2(floor(previousUv*vec2(RenderSize())));
 uvec2 previousMeta=ar.Reset==0u && ArInside(previousPixel)?imageLoad(HybridMetadataPrevious,previousPixel).xy:uvec2(0);
 bool previousSparse=HybridHistoryMetadataSparseState(previousMeta)!=HYBRID_REFLECTION_HISTORY_SPARSE_NONE;
 uint age=sparseState!=HYBRID_REFLECTION_HISTORY_SPARSE_NONE?(previousSparse?min(HybridHistoryMetadataAge(previousMeta)+1u,31u):1u):
     previousSparse?1u:uint(FFX_DNSR_Reflections_LoadNumSamples(p));
 imageStore(HybridMetadataCurrent,p,uvec4(HybridPackHistoryMetadata(HybridReceiverIdentity(payload),depth,normal,source,age,sparseState,valid),0,0));
 ArWrite(p,1u,packSnorm2x16(NjulfHybridReflectionOctEncode(normal)));
 ArStore(p,2u,FFX_DNSR_Reflections_LoadRoughness(p)); ArWrite(p,3u,HybridReceiverIdentity(payload)); ArStore(p,4u,depth);
 ArWrite(p,5u,source);
}
#endif


