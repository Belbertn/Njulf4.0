#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#ifndef NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#define NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION 0
#endif

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_arithmetic : require
#extension GL_KHR_shader_subgroup_ballot : require
#endif

#include "common.glsl"
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE ((uint(gl_VertexIndex) % 6u) == 0u)
#define SIMPLE_DDGI_RECEIVER_CONTRIBUTION_SAMPLE ((uint(gl_VertexIndex) % 6u) == 0u)
#define SIMPLE_DDGI_RECEIVER_COVERAGE_HASH (uint(gl_VertexIndex) / 6u)
#define SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS SIMPLE_DDGI_RECEIVER_CONSUMER_PARTICLE
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#include "ddgi_simple_shared.glsl"
#undef SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET
#undef SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT
#undef SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS
#undef SIMPLE_DDGI_RECEIVER_COVERAGE_HASH
#undef SIMPLE_DDGI_RECEIVER_CONTRIBUTION_SAMPLE
#undef SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#include "ddgi_receiver_feedback_source_abi.glsl"
#include "ddgi_receiver_feedback_producer.glsl"
#endif

layout(location = 0) out vec2 outUv;
layout(location = 1) out vec4 outColor;
layout(location = 2) out vec4 outParams;
layout(location = 3) flat out uint outTextureIndex;
layout(location = 4) flat out uint outBlendMode;
layout(location = 5) flat out uint outDebugId;
layout(location = 6) out vec2 outNextUv;
layout(location = 7) flat out float outFlipbookBlend;
layout(location = 8) out vec3 outWorldPosition;
layout(location = 9) flat out vec3 outDdgiAmbient;

layout(push_constant) uniform ParticlePushBlock
{
    GPUParticlePushConstants Push;
} pc;

const vec2 QuadCorners[6] = vec2[](
    vec2(-0.5, -0.5),
    vec2( 0.5, -0.5),
    vec2( 0.5,  0.5),
    vec2(-0.5, -0.5),
    vec2( 0.5,  0.5),
    vec2(-0.5,  0.5)
);

const vec2 QuadUv[6] = vec2[](
    vec2(0.0, 1.0),
    vec2(1.0, 1.0),
    vec2(1.0, 0.0),
    vec2(0.0, 1.0),
    vec2(1.0, 0.0),
    vec2(0.0, 0.0)
);

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
const uint SIMPLE_DDGI_PARTICLE_FEEDBACK_PRODUCER = 3u;
const uint SIMPLE_DDGI_PARTICLE_BLEND_ALPHA_CLIP = 4u;

uint SimpleDdgiParticleFeedbackHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

vec2 ResolveParticleCorner(
    vec2 corner,
    float rotation,
    uint billboardMode,
    float velocityStretch)
{
    if (billboardMode == 5u)
        return corner;
    float c = cos(rotation);
    float s = sin(rotation);
    vec2 rotated = vec2(
        corner.x * c - corner.y * s,
        corner.x * s + corner.y * c);
    if (billboardMode == 4u)
        rotated.y *= velocityStretch;
    return rotated;
}

float SimpleDdgiParticleProjectedAreaPixels(
    GPUParticleInstance particle,
    GPUParticleFrameData frameData,
    vec3 center,
    vec3 cameraRight,
    vec3 cameraUp,
    float size,
    float velocityStretch)
{
    const vec2 corners[4] = vec2[](
        vec2(-0.5, -0.5),
        vec2( 0.5, -0.5),
        vec2( 0.5,  0.5),
        vec2(-0.5,  0.5));
    vec2 screen[4];
    vec2 minimumScreen = vec2(3.402823466e+38);
    vec2 maximumScreen = vec2(-3.402823466e+38);
    for (uint cornerIndex = 0u; cornerIndex < 4u; ++cornerIndex)
    {
        vec2 local = ResolveParticleCorner(
            corners[cornerIndex],
            particle.VelocityRotation.w,
            particle.BillboardMode,
            velocityStretch);
        vec3 world = center +
            (cameraRight * local.x + cameraUp * local.y) * size;
        vec4 clip = MulRowMajor(vec4(world, 1.0), frameData.ViewProjectionMatrix);
        if (!(clip.w > 0.00001) || isnan(clip.w) || isinf(clip.w))
            return 0.0;
        vec2 ndc = clip.xy / clip.w;
        screen[cornerIndex] = (ndc * 0.5 + vec2(0.5)) *
            max(frameData.ScreenDimensions, vec2(1.0));
        minimumScreen = min(minimumScreen, screen[cornerIndex]);
        maximumScreen = max(maximumScreen, screen[cornerIndex]);
    }
    float twiceArea = 0.0;
    for (uint cornerIndex = 0u; cornerIndex < 4u; ++cornerIndex)
    {
        vec2 a = screen[cornerIndex];
        vec2 b = screen[(cornerIndex + 1u) & 3u];
        twiceArea += a.x * b.y - a.y * b.x;
    }
    float projectedArea = abs(twiceArea) * 0.5;
    vec2 clippedMinimum = clamp(
        minimumScreen,
        vec2(0.0),
        frameData.ScreenDimensions);
    vec2 clippedMaximum = clamp(
        maximumScreen,
        vec2(0.0),
        frameData.ScreenDimensions);
    vec2 fullExtent = max(maximumScreen - minimumScreen, vec2(0.0));
    vec2 visibleExtent = max(clippedMaximum - clippedMinimum, vec2(0.0));
    float fullAabbArea = fullExtent.x * fullExtent.y;
    float visibleFraction = fullAabbArea > 0.000001
        ? clamp(
            (visibleExtent.x * visibleExtent.y) / fullAabbArea,
            0.0,
            1.0)
        : 0.0;
    float visibleArea = projectedArea * visibleFraction;
    return !isnan(visibleArea) && !isinf(visibleArea)
        ? max(visibleArea, 0.0)
        : 0.0;
}

float SimpleDdgiParticleSoftFadeAtCenter(
    GPUParticleInstance particle,
    GPUParticleFrameData frameData,
    vec3 center)
{
    float softDistance = particle.EmissiveLifetimeSoftClip.z;
    if (pc.Push.SoftParticlesEnabled == 0u || softDistance <= 0.0001)
        return 1.0;
    vec4 clip = MulRowMajor(vec4(center, 1.0), frameData.ViewProjectionMatrix);
    if (!(clip.w > 0.00001))
        return 0.0;
    vec3 ndc = clip.xyz / clip.w;
    vec2 uv = ndc.xy * 0.5 + vec2(0.5);
    if (any(lessThan(uv, vec2(0.0))) ||
        any(greaterThan(uv, vec2(1.0))))
    {
        return 0.0;
    }
    float sceneDepth = textureLod(
        BindlessTextures[nonuniformEXT(int(pc.Push.DepthTextureIndex))],
        uv,
        0.0).r;
    if (sceneDepth <= 0.000001)
        return 1.0;
    vec4 particleView = MulRowMajor(
        vec4(ndc.xy, ndc.z, 1.0),
        frameData.InverseProjectionMatrix);
    vec4 geometryView = MulRowMajor(
        vec4(ndc.xy, sceneDepth, 1.0),
        frameData.InverseProjectionMatrix);
    float particleDepth = abs(
        particleView.z / max(abs(particleView.w), 0.00001));
    float geometryDepth = abs(
        geometryView.z / max(abs(geometryView.w), 0.00001));
    return clamp(
        abs(geometryDepth - particleDepth) / softDistance,
        0.0,
        1.0);
}

float SimpleDdgiParticleMeanCoverage(
    GPUParticleInstance particle,
    float softFade)
{
    uint columns = max(particle.FlipbookColumns, 1u);
    uint rows = max(particle.FlipbookRows, 1u);
    uint frameCount = columns * rows;
    uint currentFrame = min(particle.FlipbookFrame, frameCount - 1u);
    uint nextFrame = min(particle.Padding0 >> 16u, frameCount - 1u);
    float flipbookBlend = float(particle.Padding0 & 0xffffu) / 65535.0;
    vec2 uvSize = vec2(1.0 / float(columns), 1.0 / float(rows));
    vec2 currentOffset = vec2(
        float(currentFrame % columns),
        float(currentFrame / columns)) * uvSize;
    vec2 nextOffset = vec2(
        float(nextFrame % columns),
        float(nextFrame / columns)) * uvSize;
    float coverage = 0.0;
    for (uint sampleY = 0u; sampleY < 3u; ++sampleY)
    for (uint sampleX = 0u; sampleX < 3u; ++sampleX)
    {
        vec2 localUv = (vec2(sampleX, sampleY) + vec2(0.5)) / 3.0;
        float currentAlpha = textureLod(
            BindlessTextures[nonuniformEXT(int(particle.TextureIndex))],
            currentOffset + localUv * uvSize,
            0.0).a;
        float nextAlpha = textureLod(
            BindlessTextures[nonuniformEXT(int(particle.TextureIndex))],
            nextOffset + localUv * uvSize,
            0.0).a;
        float alpha = clamp(
            mix(currentAlpha, nextAlpha, flipbookBlend) *
                particle.Color.a * softFade,
            0.0,
            1.0);
        if (particle.BlendMode == SIMPLE_DDGI_PARTICLE_BLEND_ALPHA_CLIP &&
            alpha <= particle.EmissiveLifetimeSoftClip.w)
        {
            alpha = 0.0;
        }
        if (alpha <= 0.001)
            alpha = 0.0;
        coverage += alpha;
    }
    return coverage / 9.0;
}

void EmitSimpleDdgiParticleReceiverFeedback(
    SimpleDdgiGatherResult gather,
    float effectiveOwnership,
    float indirectIntensity,
    GPUParticleInstance particle,
    GPUParticleFrameData frameData,
    uint instanceIndex,
    vec3 center,
    vec3 cameraRight,
    vec3 cameraUp,
    float size,
    float velocityStretch)
{
    uint controlOffsetWords;
    if (!SimpleDdgiReceiverFeedbackTryResolveFrameControlOffset(
            pc.Push.CurrentFrameIndex,
            controlOffsetWords))
    {
        return;
    }
    uint stableHash = SimpleDdgiParticleFeedbackHash(
        particle.DebugId ^
        SimpleDdgiParticleFeedbackHash(instanceIndex + 0x9e3779b9u));
    bool refinementOrBaseFallback =
        gather.exactFeedbackRefinementOrBaseFallback != 0u;
    for (uint producerPhase = 0u; producerPhase < 2u; ++producerPhase)
    {
        uint effectiveProducer = producerPhase == 0u
            ? SIMPLE_DDGI_PARTICLE_FEEDBACK_PRODUCER
            : 6u;
        bool laneBelongsToProducer = producerPhase == 0u
            ? !refinementOrBaseFallback
            : refinementOrBaseFallback;
        uint activeLaneCount = subgroupAdd(
            laneBelongsToProducer ? 1u : 0u);
        if (activeLaneCount == 0u)
            continue;
        bool controlValid =
            SimpleDdgiReceiverFeedbackProducerControlIsValid(
                controlOffsetWords,
                effectiveProducer);
        uint samplingPeriod = controlValid
            ? SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SAMPLE_PERIOD)
            : 0u;
        uint samplingPhase = controlValid
            ? SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SAMPLE_PHASE)
            : 0u;
        uint maximumOwners = controlValid
            ? SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_MAXIMUM_OWNERS)
            : 0u;
        bool policyValid = controlValid && samplingPeriod != 0u &&
            samplingPhase < samplingPeriod && maximumOwners != 0u &&
            maximumOwners <= SIMPLE_DDGI_EXACT_FEEDBACK_MAX_OWNERS;
        if (!policyValid)
        {
            if (subgroupElect() && activeLaneCount != 0u)
            {
                SimpleDdgiReceiverFeedbackMarkProducerFailure(
                    controlOffsetWords,
                    effectiveProducer,
                    activeLaneCount);
            }
            continue;
        }

        uint producerHash = SimpleDdgiParticleFeedbackHash(
            stableHash ^
            SimpleDdgiParticleFeedbackHash(
                effectiveProducer + 0xc2b2ae35u));
        bool selected = laneBelongsToProducer &&
            uint(gl_VertexIndex) == 0u &&
            producerHash % samplingPeriod == samplingPhase &&
            pc.Push.DebugView == 0u;
        bool ownerSetValid = gather.exactFeedbackOverflow == 0u &&
            gather.exactFeedbackOwnerCount <= maximumOwners;
        uint malformedCount = selected && !ownerSetValid
            ? max(gather.exactFeedbackOwnerCount, 1u)
            : 0u;
        uint subgroupMalformed = subgroupAdd(malformedCount);
        if (subgroupElect() && subgroupMalformed != 0u)
        {
            SimpleDdgiReceiverFeedbackMarkProducerFailure(
                controlOffsetWords,
                effectiveProducer,
                subgroupMalformed);
        }

        float projectedArea = selected && ownerSetValid
            ? SimpleDdgiParticleProjectedAreaPixels(
                particle,
                frameData,
                center,
                cameraRight,
                cameraUp,
                size,
                velocityStretch)
            : 0.0;
        float softFade = projectedArea > 0.0
            ? SimpleDdgiParticleSoftFadeAtCenter(particle, frameData, center)
            : 0.0;
        float coverage = projectedArea > 0.0
            ? SimpleDdgiParticleMeanCoverage(particle, softFade)
            : 0.0;
        float emissiveStrength = max(
            particle.EmissiveLifetimeSoftClip.x,
            0.0);
        float nonEmissiveWeight = clamp(
            1.0 - max(emissiveStrength - 1.0, 0.0),
            0.0,
            1.0);
        float physicalContribution = projectedArea * coverage *
            nonEmissiveWeight * clamp(effectiveOwnership, 0.0, 1.0) *
            max(indirectIntensity, 0.0) * 0.75 / SIMPLE_DDGI_PI;
        bool contributes = physicalContribution > 0.0 &&
            !isnan(physicalContribution) && !isinf(physicalContribution);
        uint localOwnerCount = selected && ownerSetValid && contributes
            ? gather.exactFeedbackOwnerCount
            : 0u;
        uint subgroupOwnerCount = subgroupAdd(localOwnerCount);
        uint subgroupOwnerPrefix = subgroupExclusiveAdd(localOwnerCount);
        SimpleDdgiReceiverFeedbackProducerReservation reservation;
        reservation.requestedCount = 0u;
        reservation.reservedBase = 0u;
        reservation.reservedCount = 0u;
        reservation.sharedBase = 0u;
        reservation.sharedCount = 0u;
        if (subgroupElect())
        {
            reservation = SimpleDdgiReceiverFeedbackReserveProducerRecords(
                controlOffsetWords,
                effectiveProducer,
                subgroupOwnerCount);
        }
        reservation.requestedCount = subgroupBroadcastFirst(
            reservation.requestedCount);
        reservation.reservedBase = subgroupBroadcastFirst(
            reservation.reservedBase);
        reservation.reservedCount = subgroupBroadcastFirst(
            reservation.reservedCount);
        reservation.sharedBase = subgroupBroadcastFirst(
            reservation.sharedBase);
        reservation.sharedCount = subgroupBroadcastFirst(
            reservation.sharedCount);
        uvec2 stableIdentity = uvec2(particle.DebugId, instanceIndex);
        for (uint ownerIndex = 0u;
             ownerIndex < localOwnerCount;
             ++ownerIndex)
        {
            uint recordIndex;
            if (!SimpleDdgiReceiverFeedbackTryGetReservationRecord(
                    reservation,
                    subgroupOwnerPrefix + ownerIndex,
                    recordIndex))
            {
                continue;
            }
            SimpleDdgiExactFeedbackOwner owner =
                gather.exactFeedbackOwners[ownerIndex];
            SimpleDdgiReceiverFeedbackWriteCandidate(
                controlOffsetWords,
                recordIndex,
                effectiveProducer,
                owner.fallbackRole,
                owner.requestedProbe,
                owner.resolvedProbe,
                owner.resolvedPage,
                owner.requestedPage,
                producerHash,
                owner.normalizedWeight,
                float(samplingPeriod),
                physicalContribution,
                owner.pageGeneration,
                stableIdentity);
        }
    }
}
#endif

void main()
{
    uint instanceIndex = pc.Push.InstanceOffset + uint(gl_InstanceIndex);
    GPUParticleInstance particle = ReadParticleInstance(
        pc.Push.ParticleInstanceBufferBaseIndex,
        pc.Push.CurrentFrameIndex,
        instanceIndex);
    GPUParticleFrameData frameData = ReadParticleFrameData(
        pc.Push.CurrentFrameIndex,
        pc.Push.ParticleFrameDataBufferBaseIndex);

    vec3 center = particle.PositionSize.xyz;
    float size = particle.PositionSize.w;
    vec3 cameraRight = normalize(vec3(
        frameData.InverseViewMatrix[0][0],
        frameData.InverseViewMatrix[0][1],
        frameData.InverseViewMatrix[0][2]));
    vec3 cameraUp = normalize(vec3(
        frameData.InverseViewMatrix[1][0],
        frameData.InverseViewMatrix[1][1],
        frameData.InverseViewMatrix[1][2]));

    vec2 corner = QuadCorners[gl_VertexIndex];
    float rotation = particle.VelocityRotation.w;
    float c = cos(rotation);
    float s = sin(rotation);
    vec2 rotated = vec2(
        corner.x * c - corner.y * s,
        corner.x * s + corner.y * c);

    vec3 velocity = particle.VelocityRotation.xyz;
    float velocityStretch = 1.0;
    if (particle.BillboardMode == 1u || particle.BillboardMode == 4u)
    {
        vec3 velocityDir = length(velocity) > 0.0001 ? normalize(velocity) : cameraUp;
        vec3 side = normalize(cross(velocityDir, normalize(frameData.CameraPosition - center)));
        if (length(side) <= 0.0001)
            side = cameraRight;
        cameraUp = velocityDir;
        cameraRight = side;
        if (particle.BillboardMode == 4u)
        {
            velocityStretch = clamp(length(velocity) * 0.08, 1.0, 4.0);
            rotated.y *= velocityStretch;
        }
    }
    else if (particle.BillboardMode == 5u)
    {
        vec3 axis = velocity;
        vec3 axisDir = length(axis) > 0.0001 ? normalize(axis) : cameraUp;
        vec3 side = normalize(cross(axisDir, normalize(frameData.CameraPosition - center)));
        if (length(side) <= 0.0001)
            side = cameraRight;
        cameraRight = side;
        cameraUp = axis;
        rotated = corner;
        size = max(size, 0.0001);
    }
    else if (particle.BillboardMode == 2u)
    {
        cameraRight = vec3(1.0, 0.0, 0.0);
        cameraUp = vec3(0.0, 0.0, 1.0);
    }

    vec3 worldPosition = center + (cameraRight * rotated.x + cameraUp * rotated.y) * size;
    gl_Position = MulRowMajor(vec4(worldPosition, 1.0), frameData.ViewProjectionMatrix);
    vec3 centerViewVector = frameData.CameraPosition - center;
    float centerViewLength = length(centerViewVector);
    vec3 particleDdgiNormal = centerViewLength > 0.0001 ? centerViewVector / centerViewLength : vec3(0.0, 1.0, 0.0);
    vec3 particleAlbedo = max(particle.Color.rgb, vec3(0.0));
    SimpleDdgiParams simpleParams = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    outDdgiAmbient = vec3(0.0);
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    SimpleDdgiGatherResult exactFeedbackGather = EmptySimpleDdgiGatherResult();
    float exactFeedbackOwnership = 0.0;
    float exactFeedbackIndirectIntensity = 0.0;
#endif
    if ((simpleParams.flags & (SIMPLE_DDGI_FLAG_ENABLED | SIMPLE_DDGI_FLAG_PARTICLE_ENABLED)) ==
        (SIMPLE_DDGI_FLAG_ENABLED | SIMPLE_DDGI_FLAG_PARTICLE_ENABLED) &&
        simpleParams.probeCount > 0u)
    {
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
        exactFeedbackGather = SampleSimpleDdgiGather(
            simpleParams,
            center,
            particleDdgiNormal,
            particleDdgiNormal);
        float radiometricOwnership =
            SimpleDdgiRadiometricOwnership(exactFeedbackGather);
        float leakAttenuation =
            SimpleDdgiLeakAttenuation(exactFeedbackGather, simpleParams);
        exactFeedbackOwnership = radiometricOwnership * leakAttenuation;
        exactFeedbackIndirectIntensity = max(simpleParams.indirectIntensity, 0.0);
        vec3 irradiance =
            exactFeedbackGather.irradiance * exactFeedbackOwnership;
        float fallbackWeight = (1.0 - radiometricOwnership) *
            simpleParams.environmentFallbackIntensity;
        if (fallbackWeight >
            SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT)
        {
            vec3 fallback = SimpleDdgiEnvironmentIrradianceFallback(
                particleDdgiNormal,
                simpleParams);
            if ((simpleParams.flags &
                    SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
            {
                fallback *= EstimateFarFieldSkyVisibility(
                    center,
                    particleDdgiNormal,
                    simpleParams,
                    1u);
            }
            irradiance += fallback * fallbackWeight;
        }
        outDdgiAmbient = clamp(irradiance, vec3(0.0), vec3(64.0)) *
            exactFeedbackIndirectIntensity * particleAlbedo *
            0.75 / SIMPLE_DDGI_PI;
#else
        outDdgiAmbient = SampleSimpleDdgiIrradiance(center, particleDdgiNormal, particleDdgiNormal) * particleAlbedo * 0.75 / SIMPLE_DDGI_PI;
#endif
    }

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    EmitSimpleDdgiParticleReceiverFeedback(
        exactFeedbackGather,
        exactFeedbackOwnership,
        exactFeedbackIndirectIntensity,
        particle,
        frameData,
        instanceIndex,
        center,
        cameraRight,
        cameraUp,
        size,
        velocityStretch);
#endif

    uint columns = max(particle.FlipbookColumns, 1u);
    uint rows = max(particle.FlipbookRows, 1u);
    uint frameCount = columns * rows;
    uint frame = min(particle.FlipbookFrame, frameCount - 1u);
    uint nextFrame = min(particle.Padding0 >> 16u, frameCount - 1u);
    float flipbookBlend = float(particle.Padding0 & 0xffffu) / 65535.0;
    vec2 uvSize = vec2(1.0 / float(columns), 1.0 / float(rows));
    vec2 uvOffset = vec2(float(frame % columns), float(frame / columns)) * uvSize;
    vec2 nextUvOffset = vec2(float(nextFrame % columns), float(nextFrame / columns)) * uvSize;

    outUv = uvOffset + QuadUv[gl_VertexIndex] * uvSize;
    outNextUv = nextUvOffset + QuadUv[gl_VertexIndex] * uvSize;
    outFlipbookBlend = flipbookBlend;
    outColor = particle.Color;
    outParams = particle.EmissiveLifetimeSoftClip;
    outTextureIndex = particle.TextureIndex;
    outBlendMode = particle.BlendMode;
    outDebugId = particle.DebugId;
    outWorldPosition = worldPosition;
}
