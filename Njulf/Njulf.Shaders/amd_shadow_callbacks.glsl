// Resource adapter for the unmodified FidelityFX SDK 1.1.4 shadow kernels.
// State: 128 header words, eight packed words/pixel, metadata, 8x4 light masks.
#include "common.glsl"
#define FFX_GPU 1
#define FFX_GLSL 1
#define FFX_HALF 1
#define FFX_DENOISER_OPTION_INVERTED_DEPTH 1
#include "ThirdParty/FidelityFX/ffx_core.h"
layout(push_constant) uniform AmdShadowPush {
    uint Current; uint Previous; uint Source; uint Width;
    uint Height; uint Slot; uint Reset; uint Stage;
} ad;
ivec2 AdNativeSize() { return ivec2(ReadStorageWord(ad.Current,52u),ReadStorageWord(ad.Current,53u)); }
ivec2 AdNativePixel(ivec2 p) { return clamp(p*2+1,ivec2(0),AdNativeSize()-1); }
uint AdPixels() { return ad.Width * ad.Height; }
uint AdTiles() { return ((ad.Width+7u)/8u)*((ad.Height+7u)/8u); }
uint AdMetadata() { return 128u+AdPixels()*8u; }
uint AdMasks() { return AdMetadata()+AdTiles(); }
bool AdInside(ivec2 p) { return all(greaterThanEqual(p,ivec2(0)))&&all(lessThan(p,ivec2(ad.Width,ad.Height))); }
uint AdPixel(ivec2 p) { p=clamp(p,ivec2(0),ivec2(ad.Width,ad.Height)-1); return uint(p.y)*ad.Width+uint(p.x); }
uint AdRead(uint bank,ivec2 p,uint field) { return ReadStorageWord(bank,128u+AdPixel(p)*8u+field); }
void AdStore(ivec2 p,uint field,uint value) { if(AdInside(p)) WriteStorageWord(ad.Current,128u+AdPixel(p)*8u+field,value); }
mat4 AdMatrix(uint offset) { return mat4(ReadStorageVec4(ad.Current,offset),ReadStorageVec4(ad.Current,offset+4u),ReadStorageVec4(ad.Current,offset+8u),ReadStorageVec4(ad.Current,offset+12u)); }
mat4 AdFlipY() { return mat4(1,0,0,0,0,-1,0,0,0,0,1,0,0,0,0,1); }
ivec2 BufferDimensions() { return ivec2(ad.Width,ad.Height); }
vec2 InvBufferDimensions() { return 1.0/vec2(ad.Width,ad.Height); }
mat4 ProjectionInverse() { return AdMatrix(0u)*AdFlipY(); }
mat4 ViewProjectionInverse() { return AdMatrix(16u)*AdFlipY(); }
mat4 ReprojectionMatrix() { return AdMatrix(32u)*AdFlipY(); }
vec3 Eye() { return ReadStorageVec4(ad.Current,48u).xyz; }
int IsFirstFrame() { return int(ad.Reset); }
float DepthSimilaritySigma() { return 1.0; }
float LoadDepth(ivec2 p) { return AdInside(p)?uintBitsToFloat(AdRead(ad.Current,p,0u)):0.0; }
float LoadPreviousDepth(ivec2 p) { return ad.Reset==0u && AdInside(p)?uintBitsToFloat(AdRead(ad.Previous,p,0u)):0.0; }
vec3 AdUnpackNormal(uint packed) { vec2 e=unpackSnorm2x16(packed); vec3 n=vec3(e,1.0-abs(e.x)-abs(e.y)); float t=max(-n.z,0.0); n.xy+=mix(vec2(t),vec2(-t),greaterThanEqual(n.xy,vec2(0))); return normalize(n); }
uint AdPackNormal(vec3 n) { n/=max(abs(n.x)+abs(n.y)+abs(n.z),1e-8); vec2 e=n.xy; if(n.z<0.0)e=(1.0-abs(e.yx))*mix(vec2(-1),vec2(1),greaterThanEqual(e,vec2(0))); return packSnorm2x16(e); }
vec3 LoadNormals(uvec2 p) { return AdUnpackNormal(AdRead(ad.Current,ivec2(p),1u)); }
vec2 LoadVelocity(ivec2 p) { return -texelFetch(BindlessTextures[nonuniformEXT(MOTION_VECTOR_TEXTURE_INDEX)],AdNativePixel(p),0).xy; }
bool IsShadowReciever(uvec2 p) { float d=LoadDepth(ivec2(p)); return d>0.0&&d<1.0&&AdInside(ivec2(p)); }
vec3 LoadPreviousMomentsBuffer(ivec2 p) { return ad.Reset!=0u?vec3(0):vec3(unpackHalf2x16(AdRead(ad.Previous,p,2u)),uintBitsToFloat(AdRead(ad.Previous,p,3u))); }
float AdHistory(ivec2 p) { return ad.Reset!=0u?0.0:unpackHalf2x16(AdRead(ad.Previous,p,5u)).x; }
float LoadHistory(vec2 uv) { vec2 q=uv*vec2(BufferDimensions())-.5; ivec2 p=ivec2(floor(q)); vec2 f=fract(q); return mix(mix(AdHistory(p),AdHistory(p+ivec2(1,0)),f.x),mix(AdHistory(p+ivec2(0,1)),AdHistory(p+ivec2(1)),f.x),f.y); }
uint LoadRaytracedShadowMask(uint p) { return ReadStorageWord(ad.Current,AdMasks()+p); }
void StoreMetadata(uint p,uint v) { if(p<AdTiles())WriteStorageWord(ad.Current,AdMetadata()+p,v); }
uint LoadTileMetaData(uint p) { return ReadStorageWord(ad.Current,AdMetadata()+min(p,AdTiles()-1u)); }
void StoreMoments(uvec2 p,vec3 v) { AdStore(ivec2(p),2u,packHalf2x16(v.xy)); AdStore(ivec2(p),3u,floatBitsToUint(min(v.z,32.0))); }
void StoreReprojectionResults(uvec2 p,vec2 v) { AdStore(ivec2(p),4u,packHalf2x16(v)); }
f16vec2 LoadFilterInput(uvec2 p) { return f16vec2(unpackHalf2x16(AdRead(ad.Current,ivec2(p),ad.Stage+1u))); }
void StoreHistory(uvec2 p,vec2 v) { AdStore(ivec2(p),ad.Stage+2u,packHalf2x16(v)); }
void StoreFilterOutput(uvec2 p,float v) { AdStore(ivec2(p),7u,floatBitsToUint(clamp(v,0.0,1.0))); }

