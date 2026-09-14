void OpticalSpatialPixel(ivec2 p)
{
    uint b=pc.Current;
    if (!OpticalPixelValid(b,p)) return;
    for(uint layer=0u;layer<OpticalCount(b,p);++layer)
    {
        uint r=OpticalRecord(b,p,layer);
        for(uint lobe=0u;lobe<2u;++lobe)
        {
            uint source=(pc.Step==1u?36u:44u)+lobe*4u;
            uint target=(pc.Step==1u?44u:52u)+lobe*4u;
            vec3 center=OpticalVec(b,r+source), sum=vec3(0.0); float weights=0.0;
            float roughness=OpticalFloat(b,r+7u);
            float confidence=OpticalFloat(b,r+source+3u);
            bool inactive=all(equal(OpticalVec(b,r+20u+lobe*4u),vec3(0.0)));
            if(inactive || pc.Bypass!=0u || (roughness<.12 && pc.Step>1u))
            {
                OpticalStoreVec(b,r+target,center);
                OpticalStore(b,r+target+3u,inactive?0u:floatBitsToUint(confidence));
                continue;
            }
            for(int y=-1;y<=1;++y) for(int x=-1;x<=1;++x)
            {
                uint s=(x==0 && y==0)?r:OpticalMatch(b,r,b,p+ivec2(x,y)*int(pc.Step),false);
                if(s==OPTICAL_INVALID || !OpticalSourceCompatible(b,r,b,s,lobe) || OpticalFloat(b,s+source+3u)<=0.0) continue;
                vec3 value=OpticalVec(b,s+source);
                float neighborRoughness=OpticalFloat(b,s+7u);
                float normalWeight=pow(max(dot(OpticalNormal(OpticalWord(b,r+3u)),OpticalNormal(OpticalWord(b,s+3u))),0.0),mix(128.0,16.0,(roughness+neighborRoughness)*.5));
                float weight=(x==0?2.0:1.0)*(y==0?2.0:1.0)*normalWeight;
                // Preserve sharp optical detail while broad rough lobes reconstruct noise.
                if(max(roughness,neighborRoughness)<.12 && confidence>0.0)
                    weight*=exp(-abs(OpticalLuminance(value)-OpticalLuminance(center))/max(.05,max(abs(OpticalLuminance(center)),abs(OpticalLuminance(value)))*.25));
                weight*=min(OpticalFloat(b,s+source+3u),confidence>0.0?min(confidence,4.0):4.0);
                sum+=value*weight; weights+=weight;
            }
            // Symmetric exchanges preserve energy: rejected neighbors leave
            // their share at the center instead of renormalizing a bright peak
            // toward its darker neighborhood. The full kernel weighs at most 64.
            vec3 filtered=confidence>0.0?center+(sum-center*weights)/64.0:(weights>0.0?sum/weights:center);
            OpticalStoreVec(b,r+target,filtered);
            OpticalStore(b,r+target+3u,floatBitsToUint(weights>0.0?max(OpticalFloat(b,r+source+3u),1.0):0.0));
        }
    }
}
