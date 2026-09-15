#extension GL_EXT_nonuniform_qualifier : require
#ifndef OPTICAL_STORAGE_DEFINED
layout(set=0,binding=0) buffer OpticalStorage { uint Words[]; } BindlessStorageBuffers[];
#endif
#define nonuniformEXT(index) (index)
#include "optical_layers.glsl"
#undef nonuniformEXT
#ifndef OPTICAL_STORAGE_DEFINED
layout(push_constant) uniform OpticalPush
{
    uint Current; uint Previous; uint Step; uint Reset;
    uint Weighted; uint Bypass; uint Padding0; uint Padding1;
} pc;
layout(local_size_x=8,local_size_y=8) in;

#endif
