#ifndef NJULF_DDGI_GUIDING_ARITHMETIC_GLSL
#define NJULF_DDGI_GUIDING_ARITHMETIC_GLSL

// Some NVIDIA native compilers reject the otherwise valid UINT_MAX / stride
// overflow idiom. OpUMulExtended provides the exact same check without a
// potentially speculative integer division.
bool SimpleDdgiGuidingTryMultiplyU32(
    uint left,
    uint right,
    out uint product)
{
    uint highWord;
    umulExtended(left, right, highWord, product);
    return highWord == 0u;
}

#endif
