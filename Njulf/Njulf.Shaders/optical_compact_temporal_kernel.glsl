void OpticalCompactTemporalPixel(ivec2 p) { if(!OcInside(p))return;
 uint b=pc.Current;
 for(uint layer=0u;layer<2u;layer++) {
  uint r=OcRecord(p,layer); if(!OcValid(b,r))continue;
  uint native=OpticalWord(b,r+23u);
  vec3 expected=OpticalVec(pc.Padding0,native+8u);
  ivec2 previousPixel=ivec2(floor(unpackHalf2x16(OpticalWord(b,r+22u))*vec2(OcWidth(),OcHeight())));
  uint h=pc.Reset==0u?OcMatch(b,r,pc.Previous,previousPixel,expected,true):OPTICAL_INVALID;
  for(uint lobe=0u;lobe<2u;lobe++) {
   uint field=8u+lobe*2u; vec4 current=OcRadiance(b,r,field),result=current;
   float lum=OpticalLuminance(current.rgb); vec2 moments=vec2(lum,lum*lum);
   if(h!=OPTICAL_INVALID && OcSourceCompatible(b,r,pc.Previous,h,lobe)) {
    vec4 old=OcRadiance(pc.Previous,h,field); vec2 oldMoments=unpackHalf2x16(OpticalWord(pc.Previous,h+12u+lobe));
    if(old.a>0.0) {
     if(current.a>0.0) {
      vec3 lo=current.rgb,hi=current.rgb;
      for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++) {
       uint s=OcMatch(b,r,b,p+ivec2(x,y),OpticalVec(b,r+4u),false);
       if(s==OPTICAL_INVALID)continue;
       // Read raw native observations: temporal writes to the compact bank
       // must never race another invocation's neighborhood reads.
       uint n=OpticalWord(b,s+23u); if(OpticalFloat(pc.Padding0,n+15u+lobe*4u)<=0.0)continue;
       vec3 v=OpticalVec(pc.Padding0,n+12u+lobe*4u);lo=min(lo,v);hi=max(hi,v);
      }
      float sigma=sqrt(max(oldMoments.y-oldMoments.x*oldMoments.x,0.0));
      if(old.a>=4.0)old.rgb=clamp(old.rgb,lo-3.0*sigma,hi+3.0*sigma);
      result.a=min(old.a+1.0,16.0);result.rgb=mix(old.rgb,current.rgb,1.0/result.a);
      moments=mix(oldMoments,moments,1.0/result.a);
     }else {result=old;moments=oldMoments;}
     OpticalAddHistoryReuse(pc.Padding0);
    }
   }
   OcStoreRadiance(b,r,field,result);OpticalStore(b,r+12u+lobe,packHalf2x16(clamp(moments,vec2(0),vec2(65504))));
  }
 }
}

