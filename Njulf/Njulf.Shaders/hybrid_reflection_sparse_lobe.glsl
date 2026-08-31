#ifndef NJULF_HYBRID_REFLECTION_SPARSE_LOBE_GLSL
#define NJULF_HYBRID_REFLECTION_SPARSE_LOBE_GLSL

// Screen-linear, two-word sidecar mirrored by
// HybridReflectionSparseLobePayloadAbi. The host clears the complete current
// frame bank before Forward. Raster stores only extension-bearing materials;
// the SSR consumer can therefore load zero for every untouched pixel.
const uint NJULF_HYBRID_SPARSE_LOBE_WORDS_PER_PIXEL = 2u;

uint NjulfHybridSparseLobeBufferIndex(uint frameIndex)
{
    return uint(HYBRID_REFLECTION_SPARSE_LOBE_BUFFER_BASE_INDEX) +
        (frameIndex & 1u);
}

bool NjulfHybridSparseLobeTryResolveWordOffset(
    uint bufferIndex,
    uvec2 extent,
    uvec2 pixel,
    out uint wordOffset)
{
    wordOffset = 0u;
    if (extent.x == 0u || extent.y == 0u ||
        any(greaterThanEqual(pixel, extent)) ||
        pixel.y > ((0xffffffffu - pixel.x) / extent.x))
    {
        return false;
    }

    uint pixelIndex = pixel.y * extent.x + pixel.x;
    if (pixelIndex > 0xffffffffu /
            NJULF_HYBRID_SPARSE_LOBE_WORDS_PER_PIXEL)
    {
        return false;
    }
    wordOffset = pixelIndex * NJULF_HYBRID_SPARSE_LOBE_WORDS_PER_PIXEL;
    uint wordCount = uint(BindlessStorageBuffers[
        nonuniformEXT(bufferIndex)].Words.length());
    return wordOffset <= wordCount &&
        wordCount - wordOffset >=
            NJULF_HYBRID_SPARSE_LOBE_WORDS_PER_PIXEL;
}

void NjulfHybridSparseLobeStore(
    uint frameIndex,
    uvec2 extent,
    uvec2 pixel,
    uvec2 extension)
{
    uint bufferIndex = NjulfHybridSparseLobeBufferIndex(frameIndex);
    uint wordOffset;
    if (!NjulfHybridSparseLobeTryResolveWordOffset(
            bufferIndex,
            extent,
            pixel,
            wordOffset))
    {
        return;
    }
    WriteStorageWordUniform(bufferIndex, wordOffset + 0u, extension.x);
    WriteStorageWordUniform(bufferIndex, wordOffset + 1u, extension.y);
}

uvec2 NjulfHybridSparseLobeLoad(
    uint frameIndex,
    uvec2 extent,
    uvec2 pixel)
{
    uint bufferIndex = NjulfHybridSparseLobeBufferIndex(frameIndex);
    uint wordOffset;
    if (!NjulfHybridSparseLobeTryResolveWordOffset(
            bufferIndex,
            extent,
            pixel,
            wordOffset))
    {
        return uvec2(0u);
    }
    return uvec2(
        ReadStorageWordUniform(bufferIndex, wordOffset + 0u),
        ReadStorageWordUniform(bufferIndex, wordOffset + 1u));
}

#endif
