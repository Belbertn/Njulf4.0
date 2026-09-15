#define NJULF_OPTICAL_COMPUTE 1
#define NJULF_VISIBILITY_COMPUTE 1
#include "forward_surface_shading.glsl"
#undef main
#undef pc
#define OPTICAL_STORAGE_DEFINED 1
uint OpticalWord(uint b,uint w) { return ReadStorageWordUniform(b,w); }
void OpticalStore(uint b,uint w,uint v) { WriteStorageWordUniform(b,w,v); }
void OpticalAddHistoryReuse(uint b) { atomicAdd(BindlessStorageBuffers[b].Words[7u],1u); }
#define pc optical
layout(local_size_x=8,local_size_y=8) in;
#include "optical_compact.glsl"

void OcShadeLayer(ivec2 p,uint layer) {
 uint b=pc.Current,r=OcRecord(p,layer);if(!OcValid(b,r))return;
 uint src=pc.Padding0,native=OpticalWord(b,r+23u),flags=OpticalWord(src,native+38u);
#if OPTICAL_SHADE_REFLECTION
 if((flags&1u)==0u)return;
#else
 if((flags&2u)==0u)return;
#endif
 // Selection records the native address and pixel explicitly; atomic export
 // order never determines the receiver or its reflection/refraction direction.
 uint nativePixel=OpticalWord(b,r+31u);
 VisibilityFragCoord=vec4(vec2(nativePixel%OpticalWord(src,1u),nativePixel/OpticalWord(src,1u))+.5,OpticalFloat(src,native+27u),1);
 VisibilityFrontFacing=OpticalWord(src,native+33u)!=0u;
 VisibilityWorldDx=OpticalVec(src,native+48u);VisibilityWorldDy=OpticalVec(src,native+51u);
 VisibilityDepthGradient=OpticalFloat(src,native+54u);
 fragWorldPosition=OpticalVec(src,native+4u);fragObjectIndex=OpticalWord(src,native+37u);fragMaterialIndex=OpticalWord(src,native+36u);
 GPUMaterialData material=ReadForwardMaterial(fragMaterialIndex);
 GPUEnvironmentData environment=ReadEnvironmentData();
 vec4 reflection=OcRadiance(b,r,8u),transmission=OcRadiance(b,r,10u);
 uint reflectionSource=OpticalWord(b,r+14u)&65535u,transmissionSource=OpticalWord(b,r+14u)>>16u;
#if OPTICAL_SHADE_REFLECTION
 if((flags&1u)!=0u && any(greaterThan(OpticalVec(src,native+20u),vec3(0)))) {
  opticalReflectionObserved=1.0;opticalReflectionDistance=0.0;
  GPUReflectionProbeHeader header=ReadReflectionProbeHeader();ForwardTransparentReflectionSample traceSample;
  vec3 direction=OpticalVec(src,native+40u),normal=OpticalNormal(OpticalWord(src,native+2u));
  bool found=ForwardTraceTransparentSsr(header,fragWorldPosition,normal,direction,OpticalFloat(src,native+39u),traceSample);
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
  if(!found&&ForwardEffectiveReflectionMode()==5u)
   found=ForwardTraceTransparentRayReflection(header,fragWorldPosition,normal,direction,environment,traceSample);
#endif
  if(found) {
   reflection=vec4(mix(reflection.rgb,traceSample.Radiance*header.Intensity,clamp(traceSample.Confidence,0,1)),1);
   if(traceSample.Confidence>=.5)reflectionSource=traceSample.Source;
  } else {reflection.a=opticalReflectionObserved;if(opticalReflectionObserved<=0)reflectionSource=0u;}
  OpticalStore(b,r+16u,floatBitsToUint(opticalReflectionDistance));
  atomicAdd(BindlessStorageBuffers[src].Words[9u],1u);
 }
#endif
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY && OPTICAL_SHADE_TRANSMISSION
 if((flags&2u)!=0u && any(greaterThan(OpticalVec(src,native+24u),vec3(0))) && ForwardTryReserveThickTransmissionTask()) {
  GPUMaterialExtensionData extensionData=ReadMaterialExtension(uint(material.ExtensionDataIndex));
  vec3 incident=normalize(fragWorldPosition-OpticalForwardFrames[pc.Current].Push.CameraPosition),scatter=OpticalVec(src,native+44u);
  uvec2 pixel=uvec2(VisibilityFragCoord.xy);uint seed=ThickTransmissionHash(OpticalWord(src,native)^material.MaterialRevision^
   OpticalWord(src,8u)*0xc2b2ae35u^pixel.x*0x9e3779b9u^pixel.y*0x85ebca6bu);
  ThickTransmissionPathResult path;vec3 radiance;
  bool found=ForwardTraceThickTransmissionChannel(material,extensionData,OpticalWord(src,native),incident,scatter,
   OpticalFloat(src,native+7u),seed,THICK_TRANSMISSION_SPECTRAL_CENTRAL,environment,radiance,path);
  if(found && ForwardThickTransmissionDispersionEnabled() && extensionData.Dispersion.x>0) {
   ThickTransmissionPathResult redPath,bluePath;vec3 red,blue;
   bool redValid=ForwardTraceThickTransmissionChannel(material,extensionData,OpticalWord(src,native),incident,scatter,
    OpticalFloat(src,native+7u),seed,THICK_TRANSMISSION_SPECTRAL_RED,environment,red,redPath);
   bool blueValid=ForwardTraceThickTransmissionChannel(material,extensionData,OpticalWord(src,native),incident,scatter,
    OpticalFloat(src,native+7u),seed,THICK_TRANSMISSION_SPECTRAL_BLUE,environment,blue,bluePath);
   if(redValid&&blueValid)radiance=vec3(red.r,radiance.g,blue.b);else found=false;
  }
  if(found){transmission=vec4(radiance,1);transmissionSource=5u;OpticalStore(b,r+17u,floatBitsToUint(path.PathLength));}
  atomicAdd(BindlessStorageBuffers[src].Words[10u],1u);
 }
#endif
 OcStoreRadiance(b,r,8u,reflection);OcStoreRadiance(b,r,10u,transmission);
 OpticalStore(b,r+14u,reflectionSource|(transmissionSource<<16u));
 // Raw observations are immutable until temporal completes. Spatial pass two
 // reuses these scratch words only after the intervening dispatch barriers.
 OcStoreRadiance(b,r,24u,reflection);OcStoreRadiance(b,r,26u,transmission);
}
void main() {
 // Match fragment-style 2x2 quad topology for the shared derivative helpers.
 uint lane=gl_LocalInvocationIndex;
 uvec2 local=uvec2((lane&1u)|((lane>>1u)&2u)|((lane>>2u)&4u),
                  ((lane>>1u)&1u)|((lane>>2u)&2u)|((lane>>3u)&4u));
 ivec2 p=ivec2(gl_WorkGroupID.xy*8u+local);if(!OcInside(p))return;
 OcShadeLayer(p,gl_WorkGroupID.z);
}
