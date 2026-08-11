#ifndef NJULF_DDGI_LIGHT_TREE_GLSL
#define NJULF_DDGI_LIGHT_TREE_GLSL

#include "ddgi_content_stochastic.glsl"

#define DDGI_LIGHT_TREE_MAX_LEAVES 1024u
#define DDGI_LIGHT_TREE_NODE_WORDS 16u
#define DDGI_LIGHT_TREE_LEAF_WORDS 8u
#define DDGI_LIGHT_TREE_STATE_WORDS 16u
#define DDGI_LIGHT_TREE_SCRATCH_HEADER_WORDS 16u
#define DDGI_LIGHT_TREE_SCRATCH_RECORD_WORDS 12u
#define DDGI_LIGHT_TREE_STATE_VALID_BIT (1u << 0u)
#define DDGI_LIGHT_TREE_STATE_EMPTY_BIT (1u << 1u)
#define DDGI_LIGHT_TREE_NODE_LEAF_BIT (1u << 0u)
#define DDGI_LIGHT_TREE_NODE_INVALID_BOUND_BIT (1u << 3u)
#define DDGI_LIGHT_BUFFER_STATE_WORD_OFFSET (1024u * 16u)
#define DDGI_LIGHT_BUFFER_STATE_MAGIC 0x4444474cu

struct DdgiLightTreeNode
{
    vec3 minimum;
    float flux;
    vec3 maximum;
    float maximumRange;
    vec3 coneAxis;
    float coneCosine;
    uint leftOrFirstLeaf;
    uint rightOrLeafCount;
    uint descendantLeafCount;
    uint flagsAndChecksum;
};

struct DdgiLightTreeLeaf
{
    uint packedLightIndex;
    uint stableLightIdentity;
    uvec2 lightBufferRevision;
    vec3 center;
    float range;
};

struct DdgiLightTreeState
{
    uint rootNodeIndex;
    uint nodeCount;
    uint leafCount;
    uint maximumDepth;
    uint activeStorageIndex;
    uint publicationGeneration;
    uvec2 lightBufferRevision;
    uvec2 topologyRevision;
    uvec2 contentRevision;
    uint validationChecksum;
    uint flags;
    uint rebuildReason;
    uint paddedLeafCount;
};

struct DdgiLightTreeSample
{
    bool valid;
    uint packedLightIndex;
    uint stableLightIdentity;
    uint leafOrdinal;
    float pdf;
    bool uniformComponent;
    bool repairedInvalidBound;
};

bool DdgiLightTreeFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool DdgiLightTreeFinite(vec3 value)
{
    return all(not(isnan(value))) && all(not(isinf(value)));
}

DdgiLightTreeNode DdgiReadLightTreeNode(uint nodeIndex)
{
    uint baseWord = nodeIndex * DDGI_LIGHT_TREE_NODE_WORDS;
    DdgiLightTreeNode node;
    node.minimum = vec3(
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 0u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 1u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 2u));
    node.flux = ReadStorageFloatUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 3u);
    node.maximum = vec3(
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 4u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 5u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 6u));
    node.maximumRange = ReadStorageFloatUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 7u);
    node.coneAxis = vec3(
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 8u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 9u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 10u));
    node.coneCosine = ReadStorageFloatUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 11u);
    node.leftOrFirstLeaf = ReadStorageWordUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 12u);
    node.rightOrLeafCount = ReadStorageWordUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 13u);
    node.descendantLeafCount = ReadStorageWordUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 14u);
    node.flagsAndChecksum = ReadStorageWordUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 15u);
    return node;
}

void DdgiWriteLightTreeNode(uint nodeIndex, DdgiLightTreeNode node)
{
    uint baseWord = nodeIndex * DDGI_LIGHT_TREE_NODE_WORDS;
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 0u, node.minimum.x);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 1u, node.minimum.y);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 2u, node.minimum.z);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 3u, node.flux);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 4u, node.maximum.x);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 5u, node.maximum.y);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 6u, node.maximum.z);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 7u, node.maximumRange);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 8u, node.coneAxis.x);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 9u, node.coneAxis.y);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 10u, node.coneAxis.z);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 11u, node.coneCosine);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 12u, node.leftOrFirstLeaf);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 13u, node.rightOrLeafCount);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 14u, node.descendantLeafCount);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX), baseWord + 15u, node.flagsAndChecksum);
}

DdgiLightTreeLeaf DdgiReadLightTreeLeaf(uint leafIndex)
{
    uint baseWord = leafIndex * DDGI_LIGHT_TREE_LEAF_WORDS;
    DdgiLightTreeLeaf leaf;
    leaf.packedLightIndex = ReadStorageWordUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 0u);
    leaf.stableLightIdentity = ReadStorageWordUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 1u);
    leaf.lightBufferRevision = uvec2(
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 2u),
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 3u));
    leaf.center = vec3(
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 4u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 5u),
        ReadStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 6u));
    leaf.range = ReadStorageFloatUniform(
        uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 7u);
    return leaf;
}

void DdgiWriteLightTreeLeaf(uint leafIndex, DdgiLightTreeLeaf leaf)
{
    uint baseWord = leafIndex * DDGI_LIGHT_TREE_LEAF_WORDS;
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 0u, leaf.packedLightIndex);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 1u, leaf.stableLightIdentity);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 2u, leaf.lightBufferRevision.x);
    WriteStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 3u, leaf.lightBufferRevision.y);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 4u, leaf.center.x);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 5u, leaf.center.y);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 6u, leaf.center.z);
    WriteStorageFloatUniform(uint(SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX), baseWord + 7u, leaf.range);
}

DdgiLightTreeState DdgiReadLightTreeState()
{
    DdgiLightTreeState state;
    state.rootNodeIndex = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 0u);
    state.nodeCount = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 1u);
    state.leafCount = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 2u);
    state.maximumDepth = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 3u);
    state.activeStorageIndex = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 4u);
    state.publicationGeneration = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 5u);
    state.lightBufferRevision = uvec2(
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 6u),
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 7u));
    state.topologyRevision = uvec2(
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 8u),
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 9u));
    state.contentRevision = uvec2(
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 10u),
        ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 11u));
    state.validationChecksum = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 12u);
    state.flags = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 13u);
    state.rebuildReason = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 14u);
    state.paddedLeafCount = ReadStorageWordUniform(uint(SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX), 15u);
    return state;
}

bool DdgiLightTreeStateMatchesCurrentLightBuffer(
    DdgiLightTreeState state,
    uint expectedLightCount,
    uint expectedLocalLightCount)
{
    uint baseWord = DDGI_LIGHT_BUFFER_STATE_WORD_OFFSET;
    uint magic = ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 0u);
    uvec2 lightRevision = uvec2(
        ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 1u),
        ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 2u));
    uvec2 topologyRevision = uvec2(
        ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 3u),
        ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 4u));
    uvec2 contentRevision = uvec2(
        ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 5u),
        ReadStorageWordUniform(uint(LIGHT_BUFFER_INDEX), baseWord + 6u));
    uint lightCount = ReadStorageWordUniform(
        uint(LIGHT_BUFFER_INDEX), baseWord + 7u);
    uint localLightCount = ReadStorageWordUniform(
        uint(LIGHT_BUFFER_INDEX), baseWord + 8u);
    uint storedChecksum = ReadStorageWordUniform(
        uint(LIGHT_BUFFER_INDEX), baseWord + 9u);
    uint expectedChecksum = magic ^
        lightRevision.x ^ lightRevision.y ^
        topologyRevision.x ^ topologyRevision.y ^
        contentRevision.x ^ contentRevision.y ^
        lightCount ^ localLightCount;
    uint expectedStateChecksum = state.publicationGeneration ^
        state.leafCount ^ state.nodeCount ^
        state.lightBufferRevision.x ^ state.lightBufferRevision.y ^
        state.topologyRevision.x ^ state.topologyRevision.y ^
        state.contentRevision.x ^ state.contentRevision.y;
    return magic == DDGI_LIGHT_BUFFER_STATE_MAGIC &&
        storedChecksum == expectedChecksum &&
        lightCount == expectedLightCount &&
        localLightCount == expectedLocalLightCount &&
        state.leafCount == localLightCount &&
        state.lightBufferRevision == lightRevision &&
        state.topologyRevision == topologyRevision &&
        state.contentRevision == contentRevision &&
        state.validationChecksum == expectedStateChecksum;
}

float DdgiLightTreeBranchBound(DdgiLightTreeNode node, vec3 hitPosition)
{
    if ((node.flagsAndChecksum & DDGI_LIGHT_TREE_NODE_INVALID_BOUND_BIT) != 0u ||
        !DdgiLightTreeFinite(node.minimum) ||
        !DdgiLightTreeFinite(node.maximum) ||
        !DdgiLightTreeFinite(node.flux) || node.flux < 0.0)
    {
        return uintBitsToFloat(0x7fc00000u);
    }
    vec3 closest = clamp(hitPosition, node.minimum, node.maximum);
    float distanceSquared = dot(hitPosition - closest, hitPosition - closest);
    return node.flux / (1.0 + max(distanceSquared, 0.0));
}

float DdgiLightTreeTreePdf(
    DdgiLightTreeState state,
    vec3 hitPosition,
    uint targetLeafOrdinal)
{
    uint nodeIndex = state.rootNodeIndex;
    float pdf = 1.0;
    uint firstLeaf = 0u;
    uint leafSpan = state.paddedLeafCount;
    for (uint depth = 0u; depth < state.maximumDepth; depth++)
    {
        DdgiLightTreeNode node = DdgiReadLightTreeNode(nodeIndex);
        if ((node.flagsAndChecksum & DDGI_LIGHT_TREE_NODE_LEAF_BIT) != 0u)
            break;
        DdgiLightTreeNode left = DdgiReadLightTreeNode(node.leftOrFirstLeaf);
        DdgiLightTreeNode right = DdgiReadLightTreeNode(node.rightOrLeafCount);
        float leftBound = DdgiLightTreeBranchBound(left, hitPosition);
        float rightBound = DdgiLightTreeBranchBound(right, hitPosition);
        float total = leftBound + rightBound;
        if (!(total > 0.0) || !DdgiLightTreeFinite(total))
            return 0.0;
        uint halfSpan = leafSpan >> 1u;
        bool targetInLeft = targetLeafOrdinal < firstLeaf + halfSpan;
        pdf *= targetInLeft ? leftBound / total : rightBound / total;
        nodeIndex = targetInLeft ? node.leftOrFirstLeaf : node.rightOrLeafCount;
        if (!targetInLeft)
            firstLeaf += halfSpan;
        leafSpan = halfSpan;
    }
    return DdgiLightTreeFinite(pdf) ? pdf : 0.0;
}

DdgiLightTreeSample DdgiSampleLocalLightTree(
    vec3 hitPosition,
    uvec2 worldProbeStableKey,
    uint directionRayOrdinal,
    uint sourceLightingEpoch,
    uint samplingSequenceEpoch,
    uint sampleOrdinal,
    float uniformMixtureProbability)
{
    DdgiLightTreeSample result;
    result.valid = false;
    result.packedLightIndex = 0u;
    result.stableLightIdentity = 0u;
    result.leafOrdinal = 0u;
    result.pdf = 0.0;
    result.uniformComponent = false;
    result.repairedInvalidBound = false;
    DdgiLightTreeState state = DdgiReadLightTreeState();
    if ((state.flags & DDGI_LIGHT_TREE_STATE_VALID_BIT) == 0u ||
        state.leafCount == 0u || state.leafCount > DDGI_LIGHT_TREE_MAX_LEAVES)
    {
        return result;
    }

    float mixture = clamp(uniformMixtureProbability, 0.001, 0.25);
    uint componentHash = DdgiStableDecisionHash(
        worldProbeStableKey,
        directionRayOrdinal,
        sourceLightingEpoch,
        samplingSequenceEpoch,
        DDGI_STOCHASTIC_DOMAIN_LIGHT_TREE,
        sampleOrdinal,
        0u);
    float componentXi = (float(componentHash >> 8u) + 0.5) *
        (1.0 / 16777216.0);
    bool uniformComponent = componentXi < mixture;
    uint leafOrdinal = 0u;
    bool repair = false;
    if (uniformComponent)
    {
        uint selectionHash = DdgiStochasticMix(componentHash, 0xA511E9B3u);
        leafOrdinal = min(
            state.leafCount - 1u,
            uint((float(selectionHash >> 8u) + 0.5) *
                (1.0 / 16777216.0) * float(state.leafCount)));
    }
    else
    {
        uint nodeIndex = state.rootNodeIndex;
        for (uint depth = 0u; depth < state.maximumDepth; depth++)
        {
            DdgiLightTreeNode node = DdgiReadLightTreeNode(nodeIndex);
            if ((node.flagsAndChecksum & DDGI_LIGHT_TREE_NODE_LEAF_BIT) != 0u)
            {
                leafOrdinal = node.leftOrFirstLeaf;
                break;
            }
            DdgiLightTreeNode left = DdgiReadLightTreeNode(node.leftOrFirstLeaf);
            DdgiLightTreeNode right = DdgiReadLightTreeNode(node.rightOrLeafCount);
            float leftBound = DdgiLightTreeBranchBound(left, hitPosition);
            float rightBound = DdgiLightTreeBranchBound(right, hitPosition);
            float total = leftBound + rightBound;
            if (!(total > 0.0) || !DdgiLightTreeFinite(total))
            {
                repair = true;
                uint repairHash = DdgiStochasticMix(componentHash, 0x63D83595u);
                leafOrdinal = min(
                    state.leafCount - 1u,
                    uint((float(repairHash >> 8u) + 0.5) *
                        (1.0 / 16777216.0) * float(state.leafCount)));
                break;
            }
            uint branchHash = DdgiStochasticMix(
                componentHash,
                depth * 0x85EBCA6Bu + 0xC2B2AE35u);
            float branchXi = (float(branchHash >> 8u) + 0.5) *
                (1.0 / 16777216.0);
            nodeIndex = branchXi < leftBound / total
                ? node.leftOrFirstLeaf
                : node.rightOrLeafCount;
            if (depth + 1u == state.maximumDepth)
            {
                DdgiLightTreeNode leafNode = DdgiReadLightTreeNode(nodeIndex);
                leafOrdinal = leafNode.leftOrFirstLeaf;
            }
        }
    }

    uint leafIndex = state.activeStorageIndex * state.paddedLeafCount + leafOrdinal;
    DdgiLightTreeLeaf leaf = DdgiReadLightTreeLeaf(leafIndex);
    if (leaf.lightBufferRevision != state.lightBufferRevision)
        return result;
    float uniformPdf = 1.0 / float(state.leafCount);
    float treePdf = repair
        ? uniformPdf
        : DdgiLightTreeTreePdf(state, hitPosition, leafOrdinal);
    float pdf = repair
        ? uniformPdf
        : (1.0 - mixture) * treePdf + mixture * uniformPdf;
    if (!(pdf > 0.0) || !DdgiLightTreeFinite(pdf))
        return result;

    result.valid = true;
    result.packedLightIndex = leaf.packedLightIndex;
    result.stableLightIdentity = leaf.stableLightIdentity;
    result.leafOrdinal = leafOrdinal;
    result.pdf = pdf;
    result.uniformComponent = uniformComponent || repair;
    result.repairedInvalidBound = repair;
    return result;
}

#endif
