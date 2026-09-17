// Native guides are retained for representative selection and reconstruction.
#include "hybrid_reflection_sparse.glsl"
bool ArProtected(uvec4 payload) {
 return HybridResolveAdaptiveReflectionTier(HybridReflectionPayloadSchedulingRoughness(payload),
  HybridReflectionPayloadF0(payload),HybridReflectionPayloadSpecularOcclusion(payload),HybridReflectionPayloadLobeFlags(payload),
  uintBitsToFloat(ArHeader(55u)),uintBitsToFloat(ArHeader(56u)),uintBitsToFloat(ArHeader(57u)))==1u;
}
bool ArNativeHistory(ivec2 p,uvec4 payload,uint rawMeta,out vec4 value,out uvec2 metadata) {
 value=vec4(0);metadata=uvec2(0);
 if(ar.Reset!=0u)return false;
 vec2 dimensions=vec2(ArNativeSize()),uv=(vec2(p)+.5)/dimensions;
 vec2 motion=texelFetch(HybridMotionVectors,p,0).xy,previousUv=uv-motion;
 if(!HybridFinite(previousUv)||any(lessThan(previousUv,vec2(0)))||any(greaterThanEqual(previousUv,vec2(1))))return false;
 float depth=texelFetch(HybridSceneDepth,p,0).r;
 vec4 clip=ArMatrix(32u)*ArMatrix(16u)*ArMatrix(0u)*vec4(uv*2-1,depth,1);
 if(clip.w<=0)return false;
 float expectedDepth=clip.z/clip.w,tolerance=max(.0005,abs(expectedDepth)*.002);
 vec3 normal=HybridReflectionTraceNormal(payload);uint identity=HybridReceiverIdentity(payload);
 bool missing=HybridReflectionTemporalSparseState(rawMeta)!=HYBRID_REFLECTION_HISTORY_SPARSE_NONE;
 uint source=HybridMetadataSource(rawMeta);
 vec2 pos=previousUv*dimensions-.5,f=fract(pos);ivec2 base=ivec2(floor(pos));float weight=0,best=0;
 for(int y=0;y<2;y++)for(int x=0;x<2;x++) {
  ivec2 q=base+ivec2(x,y);if(!ArNativeInside(q))continue;
  uvec2 m=imageLoad(HybridMetadataPrevious,q).xy;uint s=HybridHistoryMetadataSource(m);
  if(!HybridHistoryMetadataValid(m)||HybridHistoryMetadataIdentity(m)!=identity||
    abs(HybridHistoryMetadataDepth(m)-expectedDepth)>tolerance||dot(normal,HybridHistoryMetadataNormal(m))<.9)continue;
  if(missing) {
   float speed=length(motion*dimensions);uint limit=speed<.25?16u:2u;
   if(speed>=1.5||(s!=HYBRID_REFLECTION_SOURCE_SSR&&s!=HYBRID_REFLECTION_SOURCE_RAY_QUERY)||
      (HybridHistoryMetadataSparseState(m)!=0u&&HybridHistoryMetadataAge(m)>=limit))continue;
  } else if(source!=s && !((source==HYBRID_REFLECTION_SOURCE_SSR||source==HYBRID_REFLECTION_SOURCE_RAY_QUERY)&&
                          (s==HYBRID_REFLECTION_SOURCE_SSR||s==HYBRID_REFLECTION_SOURCE_RAY_QUERY)))continue;
  vec4 old=imageLoad(HybridHistoryPrevious,q);if(!HybridFinite(old))continue;
  float w=(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);
  value+=old*w;weight+=w;if(w>best){best=w;metadata=m;}
 }
 if(weight<=1e-6)return false;value/=weight;return true;
}
float ArGuideWeight(ivec2 native,uvec4 payload,ivec2 low) {
 if(!ArInside(low)||ArRead(ar.Current,low,5u)==0u||ArRead(ar.Current,low,3u)!=HybridReceiverIdentity(payload))return 0;
 float r=HybridReflectionPayloadPhysicalRoughness(payload);
 if(abs(r*r-ArFloat(ar.Current,low,2u))>.1)return 0;
 vec3 normal=HybridReflectionTraceNormal(payload);
 float n=dot(normal,FFX_DNSR_Reflections_LoadWorldSpaceNormal(low));if(n<.9)return 0;
 float depth=texelFetch(HybridSceneDepth,native,0).r;
 float difference=abs(depth-ArFloat(ar.Current,low,4u)),tolerance=max(.0005,abs(depth)*.002);
 if(difference>tolerance)return 0;
 return pow(max(n,0),32.0)*exp(-difference/max(tolerance,1e-6));
}
