#ifndef NJULF_OPTICAL_LAYERS
#define NJULF_OPTICAL_LAYERS
// Mirrored by OpticalDenoisingGpuContract. Independent of the ray-query include graph.
const uint OPTICAL_HEADER_WORDS = 64u;
const uint OPTICAL_PIXEL_WORDS = 5u;
const uint OPTICAL_RECORD_WORDS = 64u;
const uint OPTICAL_MAX_LAYERS = 4u;
const uint OPTICAL_INVALID = 0xffffffffu;

#ifndef OPTICAL_STORAGE_DEFINED
uint OpticalWord(uint b, uint w) { return BindlessStorageBuffers[nonuniformEXT(b)].Words[w]; }
void OpticalStore(uint b, uint w, uint v) { BindlessStorageBuffers[nonuniformEXT(b)].Words[w] = v; }
void OpticalAddHistoryReuse(uint b) { atomicAdd(BindlessStorageBuffers[nonuniformEXT(b)].Words[7u],1u); }
#endif
float OpticalFloat(uint b, uint w) { return uintBitsToFloat(OpticalWord(b, w)); }
vec3 OpticalVec(uint b, uint w) { return vec3(OpticalFloat(b,w), OpticalFloat(b,w+1u), OpticalFloat(b,w+2u)); }
void OpticalStoreVec(uint b, uint w, vec3 v)
{
    OpticalStore(b,w,floatBitsToUint(v.x)); OpticalStore(b,w+1u,floatBitsToUint(v.y)); OpticalStore(b,w+2u,floatBitsToUint(v.z));
}
bool OpticalFinite(vec3 v) { return !any(isnan(v)) && !any(isinf(v)); }
uint OpticalPixel(uint b, ivec2 p) { return OPTICAL_HEADER_WORDS + (uint(p.y)*OpticalWord(b,1u)+uint(p.x))*OPTICAL_PIXEL_WORDS; }
bool OpticalInside(uint b, ivec2 p) { return all(greaterThanEqual(p,ivec2(0))) && all(lessThan(p,ivec2(OpticalWord(b,1u),OpticalWord(b,2u)))); }
uint OpticalCount(uint b, ivec2 p) { return OpticalWord(b, OpticalPixel(b,p)); }
bool OpticalPixelValid(uint b, ivec2 p) { return OpticalInside(b,p) && OpticalCount(b,p) <= OpticalWord(b,4u); }
uint OpticalRecord(uint b, ivec2 p, uint layer)
{
    uint index = OpticalWord(b, OpticalPixel(b,p)+1u+layer);
    return OPTICAL_HEADER_WORDS + OpticalWord(b,1u)*OpticalWord(b,2u)*OPTICAL_PIXEL_WORDS + index*OPTICAL_RECORD_WORDS;
}
uint OpticalPackNormal(vec3 n)
{
    n /= max(abs(n.x)+abs(n.y)+abs(n.z),1e-8);
    vec2 e = n.z >= 0.0 ? n.xy : (1.0-abs(n.yx))*mix(vec2(-1),vec2(1),greaterThanEqual(n.xy,vec2(0)));
    return packSnorm2x16(e);
}
#ifndef OPTICAL_EXPORT_ONLY
vec3 OpticalNormal(uint packed)
{
    vec2 e = unpackSnorm2x16(packed);
    vec3 n = vec3(e,1.0-abs(e.x)-abs(e.y));
    float t = max(-n.z,0.0);
    n.xy += mix(vec2(t),vec2(-t),greaterThanEqual(n.xy,vec2(0)));
    return normalize(n);
}
float OpticalLuminance(vec3 v) { return dot(v,vec3(.2126,.7152,.0722)); }
// Identity is stable across meshlets. Depth and facing separate multiple surfaces
// of the same object; allocation slots and stochastic path hashes are not identity.
bool OpticalCompatible(uint a, uint r, uint b, uint s, bool temporal)
{
    if (OpticalWord(a,r) != OpticalWord(b,s) || OpticalWord(a,r+1u) != OpticalWord(b,s+1u) ||
        OpticalWord(a,r+33u) != OpticalWord(b,s+33u)) return false;
    vec3 n = OpticalNormal(OpticalWord(a,r+2u));
    if (dot(n,OpticalNormal(OpticalWord(b,s+2u))) < .95) return false;
    float roughness = OpticalFloat(a,r+7u);
    if (abs(roughness-OpticalFloat(b,s+7u)) > .1) return false;
    if (dot(OpticalNormal(OpticalWord(a,r+3u)),OpticalNormal(OpticalWord(b,s+3u))) < mix(.995,.9,roughness)) return false;
    vec3 p = OpticalVec(a,r+(temporal ? 8u : 4u));
    vec3 d = OpticalVec(b,s+4u)-p;
    float tolerance = max(.003, OpticalFloat(a,r+11u)*.002);
    return temporal ? length(d) <= tolerance*3.0 : abs(dot(n,d)) <= tolerance;
}
bool OpticalSourceCompatible(uint a, uint r, uint b, uint s, uint lobe)
{
    uint shift = lobe*16u;
    uint sa = (OpticalWord(a,r+30u)>>shift)&65535u;
    uint sb = (OpticalWord(b,s+30u)>>shift)&65535u;
    if (sa != 0u && sb != 0u && sa != sb) return false;
    // Multi-interface transmission distance changes with the sampled Fresnel
    // branch. Treating that random change as a disocclusion prevents convergence.
    // Receiver geometry and sharp-lobe radiance weights protect those edges.
    if (lobe == 1u) return true;
    float da = OpticalFloat(a,r+28u+lobe), db = OpticalFloat(b,s+28u+lobe);
    return da <= 0.0 || db <= 0.0 || abs(da-db) <= max(.1,max(da,db)*mix(.05,.5,OpticalFloat(a,r+7u)));
}
uint OpticalMatch(uint a, uint r, uint b, ivec2 p, bool temporal)
{
    if (!OpticalPixelValid(b,p)) return OPTICAL_INVALID;
    uint best = OPTICAL_INVALID; float bestDistance = 1e30;
    for (uint i=0u; i<OpticalCount(b,p); ++i)
    {
        uint s = OpticalRecord(b,p,i);
        if (!OpticalCompatible(a,r,b,s,temporal)) continue;
        float d = length(OpticalVec(a,r+(temporal?8u:4u))-OpticalVec(b,s+4u));
        if (d < bestDistance) { bestDistance=d; best=s; }
    }
    return best;
}
float OpticalCompositionWeight(uint b,uint r,ivec2 p,bool weighted)
{
    float alpha=OpticalFloat(b,r+23u), weight=alpha;
        if(weighted)
        {
            float dw=clamp(pow(max(1.0-OpticalFloat(b,r+27u)*.95,.01),3.0),.01,1.0);
            float aw=max(alpha*8.0+.01,.01);
            weight*=clamp(aw*aw*aw*64.0*dw,.01,3000.0);
        }
        else for(uint front=0u;front<OpticalCount(b,p);++front)
        {
            uint s=OpticalRecord(b,p,front);
            bool later=OpticalWord(b,s+31u)>OpticalWord(b,r+31u) ||
                (OpticalWord(b,s+31u)==OpticalWord(b,r+31u) && OpticalWord(b,s+32u)>OpticalWord(b,r+32u));
            if(later) weight*=1.0-OpticalFloat(b,s+23u);
        }
    return weight;
}
#endif // !OPTICAL_EXPORT_ONLY
#endif
