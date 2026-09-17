#include "amd_reflection_native.glsl"
shared uint ArTileReceivers;
void ArPrepareReduced() {
 if(gl_LocalInvocationIndex==0u)ArTileReceivers=0u;
 barrier();
 ivec2 p=ivec2(gl_GlobalInvocationID.xy);
 if(ArInside(p)) {
  ivec2 chosen=ivec2(-1);float closest=1e30;
  // First choose a surface, then prefer a fresh sample on that same surface.
  for(int y=0;y<2;y++)for(int x=0;x<2;x++) {
   ivec2 q=p*2+ivec2(x,y);if(!ArNativeInside(q))continue;
   uvec4 payload=texelFetch(HybridReceiverPayload,q,0);if(!HybridReflectionPayloadValid(payload))continue;
   float d=texelFetch(HybridSceneDepth,q,0).r;
   vec4 view=ArMatrix(0u)*vec4((vec2(q)+.5)/vec2(ArNativeSize())*2-1,d,1);
   float distance=abs(view.z/view.w);if(distance<closest){closest=distance;chosen=q;}
  }
  if(chosen.x>=0) {
   uvec4 surface=texelFetch(HybridReceiverPayload,chosen,0);
   for(int y=0;y<2;y++)for(int x=0;x<2;x++) {
    ivec2 q=p*2+ivec2(x,y);if(!ArNativeInside(q))continue;
    uvec4 other=texelFetch(HybridReceiverPayload,q,0);uint meta=imageLoad(HybridRawMetadata,q).x;
    if(HybridReflectionPayloadValid(other)&&HybridReceiverIdentity(other)==HybridReceiverIdentity(surface)&&
       dot(HybridReflectionTraceNormal(other),HybridReflectionTraceNormal(surface))>.95&&
       abs(texelFetch(HybridSceneDepth,q,0).r-texelFetch(HybridSceneDepth,chosen,0).r)<.0005&&
       abs(HybridReflectionPayloadPhysicalRoughness(other)-HybridReflectionPayloadPhysicalRoughness(surface))<.1&&
       HybridReflectionTemporalSparseState(meta)==HYBRID_REFLECTION_HISTORY_SPARSE_NONE&&HybridMetadataValid(meta))chosen=q;
   }
   uvec4 payload=texelFetch(HybridReceiverPayload,chosen,0);uint meta=imageLoad(HybridRawMetadata,chosen).x;
   vec4 raw=imageLoad(HybridRawRadiance,chosen);
   bool observed=HybridReflectionTemporalSparseState(meta)==HYBRID_REFLECTION_HISTORY_SPARSE_NONE;
   float hit=uintBitsToFloat(ReadStorageWordUniform(ar.Current,ArHeader(50u)+uint(chosen.y)*ArNativeSize().x+uint(chosen.x)));
   if(!observed) {
    // AMD expects a reconstructed signal at its filter resolution. A 2x2
    // cell can contain no observation with quarter-rate tracing. Seed it
    // from the nearest compatible current-frame observation before filtering,
    // instead of asking the final upsample to bridge invalid denoiser cells.
    float best=1e30;uint missingReason=HybridMetadataReason(meta);
    float depth=texelFetch(HybridSceneDepth,chosen,0).r;
    for(int y=-2;y<=2;y++)for(int x=-2;x<=2;x++) {
     ivec2 q=chosen+ivec2(x,y);if(!ArNativeInside(q))continue;
     uint m=imageLoad(HybridRawMetadata,q).x;
     if(!HybridMetadataValid(m)||HybridReflectionTemporalSparseState(m)!=HYBRID_REFLECTION_HISTORY_SPARSE_NONE)continue;
     uvec4 other=texelFetch(HybridReceiverPayload,q,0);
     if(!HybridReflectionPayloadValid(other)||HybridReceiverIdentity(other)!=HybridReceiverIdentity(payload)||
        dot(HybridReflectionTraceNormal(other),HybridReflectionTraceNormal(payload))<.9||
        abs(HybridReflectionPayloadPhysicalRoughness(other)-HybridReflectionPayloadPhysicalRoughness(payload))>.1||
        abs(texelFetch(HybridSceneDepth,q,0).r-depth)>max(.0005,abs(depth)*.002))continue;
     float distance=float(x*x+y*y);if(distance>=best)continue;
     vec4 v=imageLoad(HybridRawRadiance,q);if(!HybridFinite(v))continue;
     best=distance;observed=true;raw=v;
     meta=HybridPackMetadata(HybridMetadataSource(m),raw.a,missingReason,0u,true);
     hit=uintBitsToFloat(ReadStorageWordUniform(ar.Current,ArHeader(50u)+uint(q.y)*ArNativeSize().x+uint(q.x)));
    }
   }
   if(!observed) {
    vec4 old;uvec2 oldMeta;
    if(ArNativeHistory(chosen,payload,meta,old,oldMeta)) {
     observed=true;
     raw=vec4(old.rgb,old.a*.97);
     meta=HybridPackMetadata(HybridHistoryMetadataSource(oldMeta),raw.a,HybridMetadataReason(meta),0u,true);
     vec2 oldUv=(vec2(chosen)+.5)/vec2(ArNativeSize())-texelFetch(HybridMotionVectors,chosen,0).xy;
     ivec2 oldPixel=ivec2(floor(oldUv*vec2(ar.Width,ar.Height)));
     if(ArInside(oldPixel)&&ArRead(ar.Previous,oldPixel,3u)==HybridReceiverIdentity(payload))hit=ArFloat(ar.Previous,oldPixel,0u);
    }
   }
   if(!HybridFinite(raw))raw=vec4(0);
   // An analytic fallback for an untraced ray is not a black observation.
   // Leave reconstruction to the native fallback when neither a fresh sample
   // nor validated geometric history represents this cell.
   if(!observed)raw=vec4(0);
   vec3 normal=HybridReflectionTraceNormal(payload);float roughness=HybridReflectionPayloadPhysicalRoughness(payload);
   ArStore(p,0u,hit);ArWrite(p,1u,packSnorm2x16(NjulfHybridReflectionOctEncode(normal)));
   ArStore(p,2u,observed?roughness*roughness:1.0);ArWrite(p,3u,HybridReceiverIdentity(payload));
   ArStore(p,4u,texelFetch(HybridSceneDepth,chosen,0).r);ArWrite(p,5u,observed?HybridMetadataSource(meta):0u);
   ArStoreExtra(p,10u,uint(chosen.y)*ArNativeSize().x+uint(chosen.x));ArStoreExtra(p,11u,meta);
   ArStoreRgb(p,13u,raw.rgb);ArWrite(p,14u,packHalf2x16(vec2(clamp(raw.z,0,65504),clamp(raw.a,0,1))));ArWrite(p,15u,meta);
   if(observed)atomicAdd(ArTileReceivers,1u);
  } else {ArStore(p,2u,1.0);ArStore(p,4u,1.0);}
 }
 barrier();
 if(gl_LocalInvocationIndex==0u&&ArTileReceivers!=0u) {
  uint index=atomicAdd(BindlessStorageBuffers[ar.Current].Words[52u],1u);
  uvec2 origin=gl_WorkGroupID.xy*8u;
  WriteStorageWordUniform(ar.Current,ArHeader(51u)+index,origin.x|(origin.y<<16u));
 }
}
