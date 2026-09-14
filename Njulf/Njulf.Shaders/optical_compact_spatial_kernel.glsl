void OpticalCompactSpatialPixel(ivec2 p) {if(!OcInside(p))return;uint b=pc.Current;
 for(uint layer=0u;layer<2u;layer++) {
  uint r=OcRecord(p,layer);if(!OcValid(b,r))continue;
  uint neighbors[9];uint i=0u;vec3 position=OpticalVec(b,r+4u);
  for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++) neighbors[i++]=OcMatch(b,r,b,p+ivec2(x,y)*int(pc.Step),position,false);
  for(uint lobe=0u;lobe<2u;lobe++) {
   uint source=(pc.Step==1u?8u:18u)+lobe*2u,target=(pc.Step==1u?18u:24u)+lobe*2u;
   vec4 center=OcRadiance(b,r,source);vec3 sum=vec3(0);float weights=0.0;
   i=0u;for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++) {
    uint s=neighbors[i++];if(s==OPTICAL_INVALID || !OcSourceCompatible(b,r,b,s,lobe))continue;
    vec4 v=OcRadiance(b,s,source);if(v.a<=0.0)continue;
    float w=(x==0?2.0:1.0)*(y==0?2.0:1.0)*pow(max(dot(OpticalNormal(OpticalWord(b,r+3u)),OpticalNormal(OpticalWord(b,s+3u))),0),mix(128.0,16.0,OpticalFloat(b,r+7u)));
    w*=min(v.a,center.a>0.0?min(center.a,4.0):4.0);
    sum+=v.rgb*w;weights+=w;
   }
   vec3 value=center.a>0.0?center.rgb+(sum-center.rgb*weights)/64.0:(weights>0.0?sum/weights:center.rgb);
   OcStoreRadiance(b,r,target,vec4(value,weights>0.0?max(center.a,1.0):0));
  }
 }
}

