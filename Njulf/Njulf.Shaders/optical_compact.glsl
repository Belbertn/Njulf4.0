// Two half-resolution layers, with FP16 radiance and full-precision geometry.
// Fixed pixel/layer indexing makes filter neighborhoods independent of fragment
// allocation order. The native export retains exact full-resolution blend weights.
#include "optical_compute.glsl"
uint OcWidth() { return (OpticalWord(pc.Padding0,1u)+1u)/2u; }
uint OcHeight() { return (OpticalWord(pc.Padding0,2u)+1u)/2u; }
bool OcInside(ivec2 p) { return all(greaterThanEqual(p,ivec2(0))) && all(lessThan(p,ivec2(OcWidth(),OcHeight()))); }
uint OcRecord(ivec2 p,uint layer) { return 64u+(uint(p.y)*OcWidth()+uint(p.x))*64u+layer*32u; }
vec4 OcRadiance(uint b,uint r,uint field) { return vec4(unpackHalf2x16(OpticalWord(b,r+field)),unpackHalf2x16(OpticalWord(b,r+field+1u))); }
void OcStoreRadiance(uint b,uint r,uint field,vec4 value) { value=clamp(value,vec4(0),vec4(65504)); OpticalStore(b,r+field,packHalf2x16(value.xy)); OpticalStore(b,r+field+1u,packHalf2x16(value.zw)); }
bool OcValid(uint b,uint r) { return OpticalWord(b,r+30u)!=0u; }
bool OcCompatible(uint a,uint r,uint b,uint s,vec3 expected,bool temporal) {
 if(!OcValid(b,s) || OpticalWord(a,r)!=OpticalWord(b,s) || OpticalWord(a,r+1u)!=OpticalWord(b,s+1u) || OpticalWord(a,r+15u)!=OpticalWord(b,s+15u)) return false;
 if(abs(OpticalFloat(a,r+7u)-OpticalFloat(b,s+7u))>.1 || dot(OpticalNormal(OpticalWord(a,r+2u)),OpticalNormal(OpticalWord(b,s+2u)))<.95) return false;
 vec3 delta=OpticalVec(b,s+4u)-expected; float tolerance=max(.003,OpticalFloat(a,r+28u)*.003);
 return temporal?length(delta)<tolerance*3.0:abs(dot(delta,OpticalNormal(OpticalWord(a,r+2u))))<tolerance;
}
uint OcMatch(uint a,uint r,uint b,ivec2 p,vec3 expected,bool temporal) {
 if(!OcInside(p)) return OPTICAL_INVALID;
 uint best=OPTICAL_INVALID; float distance=1e30;
 for(uint l=0u;l<2u;l++) { uint s=OcRecord(p,l); if(!OcCompatible(a,r,b,s,expected,temporal))continue;
 float d=length(OpticalVec(b,s+4u)-expected); if(d<distance){distance=d;best=s;} }
 return best;
}
bool OcSourceCompatible(uint a,uint r,uint b,uint s,uint lobe) {
 uint shift=lobe*16u; uint x=(OpticalWord(a,r+14u)>>shift)&65535u,y=(OpticalWord(b,s+14u)>>shift)&65535u;
 return x==0u || y==0u || x==y;
}
