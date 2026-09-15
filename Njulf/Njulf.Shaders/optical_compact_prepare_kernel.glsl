void OpticalCompactPreparePixel(ivec2 p) { if(!OcInside(p))return;
 uint src=pc.Padding0,b=pc.Current;
 ivec2 q=min(p*2+1,ivec2(OpticalWord(src,1u),OpticalWord(src,2u))-1);
 // Choose a covered representative in the 2x2 cell; rank layers by distance,
 // never by atomic export order. Keep each representative's two nearest layers.
 if(!OpticalPixelValid(src,q) || OpticalCount(src,q)==0u) {
   for(int y=0;y<2;y++)for(int x=0;x<2;x++) { ivec2 candidate=p*2+ivec2(x,y);
    if(OpticalPixelValid(src,candidate) && OpticalCount(src,candidate)>0u)q=candidate; }
 }
 uint first=OPTICAL_INVALID,second=OPTICAL_INVALID; float d0=1e30,d1=1e30;
 if(OpticalPixelValid(src,q))for(uint l=0u;l<OpticalCount(src,q);l++) {
  uint s=OpticalRecord(src,q,l); float d=OpticalFloat(src,s+11u);
  if(d<d0){second=first;d1=d0;first=s;d0=d;}else if(d<d1){second=s;d1=d;}
 }
 for(uint l=0u;l<2u;l++) {
  uint r=OcRecord(p,l),s=l==0u?first:second;
  OpticalStore(b,r+30u,s==OPTICAL_INVALID?0u:1u); if(s==OPTICAL_INVALID)continue;
  for(uint f=0u;f<8u;f++)OpticalStore(b,r+f,OpticalWord(src,s+f));
  for(uint lobe=0u;lobe<2u;lobe++) {
   vec4 raw=vec4(OpticalVec(src,s+12u+lobe*4u),OpticalFloat(src,s+15u+lobe*4u));
   OcStoreRadiance(b,r,8u+lobe*2u,raw);OcStoreRadiance(b,r,24u+lobe*2u,raw);
  }
  OpticalStore(b,r+14u,OpticalWord(src,s+30u)); OpticalStore(b,r+15u,OpticalWord(src,s+33u));
  OpticalStore(b,r+16u,OpticalWord(src,s+28u)); OpticalStore(b,r+17u,OpticalWord(src,s+29u));
  OpticalStore(b,r+22u,packHalf2x16(vec2(OpticalFloat(src,s+34u),OpticalFloat(src,s+35u))));
  OpticalStore(b,r+23u,s); OpticalStore(b,r+28u,OpticalWord(src,s+11u));
  OpticalStore(b,r+31u,uint(q.y)*OpticalWord(src,1u)+uint(q.x));
 }
}

