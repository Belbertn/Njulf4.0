void OpticalTemporalPixel(ivec2 p)
{
    uint b=pc.Current;
    if (!OpticalPixelValid(b,p)) return;
    for (uint layer=0u;layer<OpticalCount(b,p);++layer)
    {
        uint r=OpticalRecord(b,p,layer), h=OPTICAL_INVALID;
        vec2 uv=vec2(OpticalFloat(b,r+34u),OpticalFloat(b,r+35u));
        ivec2 previousPixel=ivec2(floor(uv*vec2(OpticalWord(b,1u),OpticalWord(b,2u))));
        if (pc.Reset==0u && pc.Bypass==0u) h=OpticalMatch(b,r,pc.Previous,previousPixel,true);
        for (uint lobe=0u;lobe<2u;++lobe)
        {
            uint raw=12u+lobe*4u, temporal=36u+lobe*4u;
            vec3 current=OpticalVec(b,r+raw);
            if(all(equal(OpticalVec(b,r+20u+lobe*4u),vec3(0.0))))
            {
                OpticalStoreVec(b,r+temporal,current);
                OpticalStore(b,r+temporal+3u,0u);
                OpticalStore(b,r+60u+lobe*2u,0u); OpticalStore(b,r+61u+lobe*2u,0u);
                continue;
            }
            float observed=OpticalFloat(b,r+raw+3u);
            bool history=h!=OPTICAL_INVALID && OpticalSourceCompatible(b,r,pc.Previous,h,lobe);
            float count=history?OpticalFloat(pc.Previous,h+temporal+3u):0.0;
            vec3 value=current;
            float lum=OpticalLuminance(current); vec2 moments=vec2(lum,lum*lum);
            if (count>0.0)
            {
                vec3 previous=OpticalVec(pc.Previous,h+temporal);
                vec2 oldMoments=vec2(OpticalFloat(pc.Previous,h+60u+lobe*2u),OpticalFloat(pc.Previous,h+61u+lobe*2u));
                if (observed>0.0)
                {
                    vec3 low=current, high=current;
                    for (int y=-1;y<=1;++y) for(int x=-1;x<=1;++x)
                    {
                        uint s=(x==0 && y==0)?r:OpticalMatch(b,r,b,p+ivec2(x,y),false);
                        if(s==OPTICAL_INVALID || OpticalFloat(b,s+raw+3u)<=0.0 || !OpticalSourceCompatible(b,r,b,s,lobe)) continue;
                        vec3 v=OpticalVec(b,s+raw); low=min(low,v); high=max(high,v);
                    }
                    float sigma=sqrt(max(oldMoments.y-oldMoments.x*oldMoments.x,0.0));
                    // A first observation has zero estimated variance. Clamping
                    // it to the next random sample biases the optical estimator.
                    if (count>=4.0) previous=clamp(previous,low-vec3(3.0*sigma),high+vec3(3.0*sigma));
                    count=min(count+1.0,16.0);
                    value=mix(previous,current,1.0/count); moments=mix(oldMoments,moments,1.0/count);
                }
                else { value=previous; moments=oldMoments; }
                OpticalAddHistoryReuse(b);
            }
            else count=observed>0.0?1.0:0.0;
            OpticalStoreVec(b,r+temporal,value); OpticalStore(b,r+temporal+3u,floatBitsToUint(count));
            OpticalStore(b,r+60u+lobe*2u,floatBitsToUint(moments.x));
            OpticalStore(b,r+61u+lobe*2u,floatBitsToUint(moments.y));
        }
    }
}
