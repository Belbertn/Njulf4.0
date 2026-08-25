#ifndef NJULF_AREA_LIGHTING_GLSL
#define NJULF_AREA_LIGHTING_GLSL

// LTC implementation adapted from selfshadow/ltc_code. The corresponding
// copyright and paper attribution are retained in THIRD-PARTY-NOTICES.md.
const float NJULF_LTC_PI = 3.14159265358979323846;
const float NJULF_LTC_LUT_SIZE = 64.0;
const float NJULF_LTC_LUT_SCALE = (NJULF_LTC_LUT_SIZE - 1.0) / NJULF_LTC_LUT_SIZE;
const float NJULF_LTC_LUT_BIAS = 0.5 / NJULF_LTC_LUT_SIZE;

struct NjulfAreaLightResult
{
    vec3 lighting;
    vec3 diffuse;
    vec3 representativeDirection;
    float rangeAttenuation;
};

struct NjulfAreaSurfaceSample
{
    bool valid;
    vec3 position;
    vec3 normal;
    float areaPdf;
};

bool NjulfAreaFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool NjulfAreaFinite(vec3 value)
{
    return all(not(isnan(value))) && all(not(isinf(value)));
}

bool NjulfBuildLightFrame(
    GPULight light,
    out vec3 axis,
    out vec3 up,
    out vec3 right)
{
    axis = light.Direction;
    float axisLengthSquared = dot(axis, axis);
    if (!NjulfAreaFinite(axis) || axisLengthSquared <= 1e-12)
        return false;
    axis *= inversesqrt(axisLengthSquared);
    up = light.Up - axis * dot(light.Up, axis);
    if (!NjulfAreaFinite(up) || dot(up, up) <= 1e-12)
    {
        vec3 fallback = abs(axis.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        up = fallback - axis * dot(fallback, axis);
    }
    float upLengthSquared = dot(up, up);
    if (upLengthSquared <= 1e-12)
        return false;
    up *= inversesqrt(upLengthSquared);
    right = normalize(cross(up, axis));
    up = normalize(cross(axis, right));
    return NjulfAreaFinite(up) && NjulfAreaFinite(right);
}

float NjulfAreaSurfaceArea(GPULight light);

NjulfAreaSurfaceSample NjulfSampleAreaLightSurface(
    GPULight light,
    vec3 random)
{
    NjulfAreaSurfaceSample emitterSample;
    emitterSample.valid = false;
    emitterSample.position = light.Position;
    emitterSample.normal = vec3(0.0, 1.0, 0.0);
    emitterSample.areaPdf = 0.0;
    vec3 axis;
    vec3 up;
    vec3 right;
    float totalArea = NjulfAreaSurfaceArea(light);
    if (!NjulfBuildLightFrame(light, axis, up, right) ||
        !(totalArea > 0.0))
    {
        return emitterSample;
    }
    random = clamp(random, vec3(0.0), vec3(0.99999994));
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE)
    {
        emitterSample.position = light.Position +
            right * ((random.x - 0.5) * light.SizeX) +
            up * ((random.y - 0.5) * light.SizeY);
        emitterSample.normal = NjulfAreaLightIsTwoSided(light) && random.z >= 0.5
            ? -axis
            : axis;
    }
    else if (light.Type == GPU_LIGHT_TYPE_DISK)
    {
        float radial = light.SizeX * 0.5 * sqrt(random.x);
        float angle = 2.0 * NJULF_LTC_PI * random.y;
        emitterSample.position = light.Position +
            right * (radial * cos(angle)) + up * (radial * sin(angle));
        emitterSample.normal = NjulfAreaLightIsTwoSided(light) && random.z >= 0.5
            ? -axis
            : axis;
    }
    else if (light.Type == GPU_LIGHT_TYPE_TUBE)
    {
        float radius = light.SizeY * 0.5;
        float sideArea = 2.0 * NJULF_LTC_PI * radius * light.SizeX;
        float capArea = NJULF_LTC_PI * radius * radius;
        float selector = random.z * totalArea;
        if (selector < sideArea)
        {
            float angle = 2.0 * NJULF_LTC_PI * random.x;
            emitterSample.normal = right * cos(angle) + up * sin(angle);
            emitterSample.position = light.Position +
                axis * ((random.y - 0.5) * light.SizeX) +
                emitterSample.normal * radius;
        }
        else
        {
            bool positiveCap = selector >= sideArea + capArea;
            float radial = radius * sqrt(random.x);
            float angle = 2.0 * NJULF_LTC_PI * random.y;
            emitterSample.normal = positiveCap ? axis : -axis;
            emitterSample.position = light.Position +
                axis * (positiveCap ? light.SizeX * 0.5 : -light.SizeX * 0.5) +
                right * (radial * cos(angle)) + up * (radial * sin(angle));
        }
    }
    emitterSample.areaPdf = 1.0 / totalArea;
    emitterSample.valid = NjulfAreaFinite(emitterSample.position) &&
        NjulfAreaFinite(emitterSample.normal) && emitterSample.areaPdf > 0.0;
    return emitterSample;
}

float NjulfAreaSurfaceArea(GPULight light)
{
    if (!NjulfAreaFinite(light.SizeX) || !NjulfAreaFinite(light.SizeY) ||
        light.SizeX <= 1e-5 || light.SizeY <= 1e-5)
        return 0.0;
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE)
        return light.SizeX * light.SizeY * (NjulfAreaLightIsTwoSided(light) ? 2.0 : 1.0);
    if (light.Type == GPU_LIGHT_TYPE_DISK)
    {
        float scale = max(max(light.SizeX, light.SizeY), 1.0);
        if (abs(light.SizeX - light.SizeY) > scale * 1e-4)
            return 0.0;
        float radius = light.SizeX * 0.5;
        return NJULF_LTC_PI * radius * radius * (NjulfAreaLightIsTwoSided(light) ? 2.0 : 1.0);
    }
    if (light.Type == GPU_LIGHT_TYPE_TUBE)
    {
        float radius = light.SizeY * 0.5;
        return 2.0 * NJULF_LTC_PI * radius * light.SizeX +
            2.0 * NJULF_LTC_PI * radius * radius;
    }
    return 0.0;
}

float NjulfAreaBoundingRadius(GPULight light)
{
    if (light.Type == GPU_LIGHT_TYPE_DISK)
        return max(light.SizeX * 0.5, 0.0);
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE || light.Type == GPU_LIGHT_TYPE_TUBE)
        return 0.5 * length(max(vec2(light.SizeX, light.SizeY), vec2(0.0)));
    return 0.0;
}

vec3 NjulfAreaShapeExtent(GPULight light)
{
    vec3 axis;
    vec3 up;
    vec3 right;
    if (!NjulfBuildLightFrame(light, axis, up, right))
        return vec3(0.0);
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE)
        return abs(right) * (light.SizeX * 0.5) + abs(up) * (light.SizeY * 0.5);
    if (light.Type == GPU_LIGHT_TYPE_DISK)
    {
        float radius = light.SizeX * 0.5;
        return radius * sqrt(max(vec3(0.0), vec3(1.0) - axis * axis));
    }
    if (light.Type == GPU_LIGHT_TYPE_TUBE)
    {
        float halfLength = light.SizeX * 0.5;
        float radius = light.SizeY * 0.5;
        return abs(axis) * halfLength +
            radius * sqrt(max(vec3(0.0), vec3(1.0) - axis * axis));
    }
    return vec3(0.0);
}

float NjulfAreaClosestDistance(
    GPULight light,
    vec3 position,
    vec3 axis,
    vec3 up,
    vec3 right)
{
    vec3 local = position - light.Position;
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE)
    {
        vec3 closest = light.Position +
            right * clamp(dot(local, right), -light.SizeX * 0.5, light.SizeX * 0.5) +
            up * clamp(dot(local, up), -light.SizeY * 0.5, light.SizeY * 0.5);
        return length(position - closest);
    }
    if (light.Type == GPU_LIGHT_TYPE_DISK)
    {
        float axial = dot(local, axis);
        vec3 radial = local - axis * axial;
        float radialLength = length(radial);
        float radius = light.SizeX * 0.5;
        vec3 closest = light.Position +
            (radialLength > radius && radialLength > 1e-8
                ? radial * (radius / radialLength)
                : radial);
        return length(position - closest);
    }

    float halfLength = light.SizeX * 0.5;
    float radius = light.SizeY * 0.5;
    float axial = dot(local, axis);
    float radial = length(local - axis * axial);
    vec2 outside = max(
        vec2(radial - radius, abs(axial) - halfLength),
        vec2(0.0));
    return length(outside);
}

vec3 NjulfLtcIntegrateEdgeVector(vec3 v1, vec3 v2)
{
    float x = clamp(dot(v1, v2), -0.999999, 0.999999);
    float y = abs(x);
    float a = 0.8543985 + (0.4965155 + 0.0145206 * y) * y;
    float b = 3.4175940 + (4.1616724 + y) * y;
    float v = a / b;
    float thetaOverSinTheta = x > 0.0
        ? v
        : 0.5 * inversesqrt(max(1.0 - x * x, 1e-7)) - v;
    return cross(v1, v2) * thetaOverSinTheta;
}

mat3 NjulfLtcShadingBasis(vec3 normal, vec3 viewDirection)
{
    vec3 tangent = viewDirection - normal * dot(viewDirection, normal);
    if (dot(tangent, tangent) <= 1e-10)
    {
        vec3 fallback = abs(normal.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        tangent = fallback - normal * dot(fallback, normal);
    }
    tangent = normalize(tangent);
    vec3 bitangent = cross(normal, tangent);
    return transpose(mat3(tangent, bitangent, normal));
}

float NjulfLtcQuadIntegral(
    mat3 transform,
    mat3 shadingBasis,
    vec3 position,
    vec3 points[4],
    bool twoSided)
{
    mat3 combined = transform * shadingBasis;
    vec3 L0 = normalize(combined * (points[0] - position));
    vec3 L1 = normalize(combined * (points[1] - position));
    vec3 L2 = normalize(combined * (points[2] - position));
    vec3 L3 = normalize(combined * (points[3] - position));
    if (!NjulfAreaFinite(L0) || !NjulfAreaFinite(L1) ||
        !NjulfAreaFinite(L2) || !NjulfAreaFinite(L3))
    {
        return 0.0;
    }

    vec3 vectorSum = NjulfLtcIntegrateEdgeVector(L0, L1) +
        NjulfLtcIntegrateEdgeVector(L1, L2) +
        NjulfLtcIntegrateEdgeVector(L2, L3) +
        NjulfLtcIntegrateEdgeVector(L3, L0);
    float sumLength = length(vectorSum);
    if (sumLength <= 1e-8)
        return 0.0;
    float z = vectorSum.z / sumLength;
    vec3 direction = points[0] - position;
    vec3 lightNormal = cross(points[1] - points[0], points[3] - points[0]);
    bool behind = dot(direction, lightNormal) < 0.0;
    if (behind)
        z = -z;
    vec2 horizonUv = (vec2(z * 0.5 + 0.5, sumLength) *
        NJULF_LTC_LUT_SCALE) + NJULF_LTC_LUT_BIAS;
    float horizonScale = texture(
        BindlessTextures[nonuniformEXT(AREA_LIGHT_LTC_AMPLITUDE_TEXTURE_INDEX)],
        horizonUv).w;
    float integral = sumLength * max(horizonScale, 0.0);
    return behind && !twoSided ? 0.0 : max(integral, 0.0);
}

vec3 NjulfSolveCubic(vec4 coefficient)
{
    coefficient.xyz /= max(abs(coefficient.w), 1e-12) * sign(coefficient.w);
    coefficient.yz /= 3.0;
    float A = coefficient.w;
    float B = coefficient.z;
    float C = coefficient.y;
    float D = coefficient.x;
    vec3 delta = vec3(
        -coefficient.z * coefficient.z + coefficient.y,
        -coefficient.y * coefficient.z + coefficient.x,
        dot(vec2(coefficient.z, -coefficient.y), coefficient.xy));
    float discriminant = max(dot(vec2(4.0 * delta.x, -delta.y), delta.zy), 0.0);
    float thetaA = atan(sqrt(discriminant),
        -(-2.0 * B * delta.x + delta.y)) / 3.0;
    float rootA1 = 2.0 * sqrt(max(-delta.x, 0.0)) * cos(thetaA);
    float rootA3 = 2.0 * sqrt(max(-delta.x, 0.0)) *
        cos(thetaA + (2.0 / 3.0) * NJULF_LTC_PI);
    float xl = rootA1 + rootA3 > 2.0 * B ? rootA1 : rootA3;
    vec2 xlc = vec2(xl - B, A);
    float thetaD = atan(D * sqrt(discriminant),
        -(-D * delta.y + 2.0 * C * delta.z)) / 3.0;
    float rootD1 = 2.0 * sqrt(max(-delta.z, 0.0)) * cos(thetaD);
    float rootD3 = 2.0 * sqrt(max(-delta.z, 0.0)) *
        cos(thetaD + (2.0 / 3.0) * NJULF_LTC_PI);
    float xs = rootD1 + rootD3 < 2.0 * C ? rootD1 : rootD3;
    vec2 xsc = vec2(-D, xs + C);
    float E = xlc.y * xsc.y;
    float F = -xlc.x * xsc.y - xlc.y * xsc.x;
    float G = xlc.x * xsc.x;
    vec2 xmc = vec2(C * F - B * G, -B * F + C * E);
    vec3 roots = vec3(
        xsc.x / max(abs(xsc.y), 1e-12) * sign(xsc.y),
        xmc.x / max(abs(xmc.y), 1e-12) * sign(xmc.y),
        xlc.x / max(abs(xlc.y), 1e-12) * sign(xlc.y));
    if (roots.x < roots.y && roots.x < roots.z)
        roots.xyz = roots.yxz;
    else if (roots.z < roots.x && roots.z < roots.y)
        roots.xyz = roots.xzy;
    return roots;
}

float NjulfLtcDiskIntegral(
    mat3 transform,
    mat3 shadingBasis,
    vec3 position,
    vec3 center,
    vec3 right,
    vec3 up,
    float radius,
    bool twoSided)
{
    vec3 C = shadingBasis * (center - position);
    vec3 V1 = shadingBasis * (right * radius);
    vec3 V2 = shadingBasis * (up * radius);
    C = transform * C;
    V1 = transform * V1;
    V2 = transform * V2;
    if (!twoSided && dot(cross(V1, V2), C) < 0.0)
        return 0.0;
    float d11 = dot(V1, V1);
    float d22 = dot(V2, V2);
    float d12 = dot(V1, V2);
    if (d11 <= 1e-12 || d22 <= 1e-12)
        return 0.0;
    float a;
    float b;
    if (abs(d12) / sqrt(d11 * d22) > 0.0001)
    {
        float trace = d11 + d22;
        float determinantValue = sqrt(max(-d12 * d12 + d11 * d22, 1e-16));
        float u = 0.5 * sqrt(max(trace - 2.0 * determinantValue, 0.0));
        float v = 0.5 * sqrt(max(trace + 2.0 * determinantValue, 0.0));
        float maximumEigen = (u + v) * (u + v);
        float minimumEigen = max((u - v) * (u - v), 1e-12);
        vec3 newV1;
        vec3 newV2;
        if (d11 > d22)
        {
            newV1 = d12 * V1 + (maximumEigen - d11) * V2;
            newV2 = d12 * V1 + (minimumEigen - d11) * V2;
        }
        else
        {
            newV1 = d12 * V2 + (maximumEigen - d22) * V1;
            newV2 = d12 * V2 + (minimumEigen - d22) * V1;
        }
        a = 1.0 / maximumEigen;
        b = 1.0 / minimumEigen;
        V1 = normalize(newV1);
        V2 = normalize(newV2);
    }
    else
    {
        a = 1.0 / d11;
        b = 1.0 / d22;
        V1 *= sqrt(a);
        V2 *= sqrt(b);
    }
    vec3 V3 = cross(V1, V2);
    if (dot(C, V3) < 0.0)
        V3 = -V3;
    float distanceToPlane = dot(V3, C);
    if (abs(distanceToPlane) <= 1e-8)
        return 0.0;
    float x0 = dot(V1, C) / distanceToPlane;
    float y0 = dot(V2, C) / distanceToPlane;
    a *= distanceToPlane * distanceToPlane;
    b *= distanceToPlane * distanceToPlane;
    float c0 = a * b;
    float c1 = a * b * (1.0 + x0 * x0 + y0 * y0) - a - b;
    float c2 = 1.0 - a * (1.0 + x0 * x0) - b * (1.0 + y0 * y0);
    vec3 roots = NjulfSolveCubic(vec4(c0, c1, c2, 1.0));
    float e1 = roots.x;
    float e2 = roots.y;
    float e3 = roots.z;
    if (e1 == 0.0 || e3 == 0.0 || -e2 / e1 < 0.0 || -e2 / e3 < 0.0)
        return 0.0;
    vec3 averageDirection = vec3(
        a * x0 / max(abs(a - e2), 1e-8) * sign(a - e2),
        b * y0 / max(abs(b - e2), 1e-8) * sign(b - e2),
        1.0);
    averageDirection = normalize(mat3(V1, V2, V3) * averageDirection);
    float L1 = sqrt(max(-e2 / e3, 0.0));
    float L2 = sqrt(max(-e2 / e1, 0.0));
    float formFactor = L1 * L2 * inversesqrt(
        max((1.0 + L1 * L1) * (1.0 + L2 * L2), 1e-12));
    vec2 horizonUv = (vec2(averageDirection.z * 0.5 + 0.5, formFactor) *
        NJULF_LTC_LUT_SCALE) + NJULF_LTC_LUT_BIAS;
    float horizonScale = texture(
        BindlessTextures[nonuniformEXT(AREA_LIGHT_LTC_AMPLITUDE_TEXTURE_INDEX)],
        horizonUv).w;
    return max(formFactor * horizonScale, 0.0);
}

float NjulfLtcLinePrimitiveFpo(float distanceValue, float lineCoordinate)
{
    float d = max(distanceValue, 1e-6);
    return lineCoordinate / (d * (d * d + lineCoordinate * lineCoordinate)) +
        atan(lineCoordinate / d) / (d * d);
}

float NjulfLtcLinePrimitiveFwt(float distanceValue, float lineCoordinate)
{
    float d = max(distanceValue, 1e-6);
    return lineCoordinate * lineCoordinate /
        (d * (d * d + lineCoordinate * lineCoordinate));
}

float NjulfLtcDiffuseLine(vec3 p1, vec3 p2)
{
    vec3 tangent = normalize(p2 - p1);
    if (p1.z <= 0.0 && p2.z <= 0.0)
        return 0.0;
    if (p1.z < 0.0)
        p1 = (p1 * p2.z - p2 * p1.z) / max(p2.z - p1.z, 1e-8);
    if (p2.z < 0.0)
        p2 = (-p1 * p2.z + p2 * p1.z) / max(-p2.z + p1.z, 1e-8);
    float l1 = dot(p1, tangent);
    float l2 = dot(p2, tangent);
    vec3 perpendicular = p1 - l1 * tangent;
    float distanceValue = length(perpendicular);
    float integral =
        (NjulfLtcLinePrimitiveFpo(distanceValue, l2) -
         NjulfLtcLinePrimitiveFpo(distanceValue, l1)) * perpendicular.z +
        (NjulfLtcLinePrimitiveFwt(distanceValue, l2) -
         NjulfLtcLinePrimitiveFwt(distanceValue, l1)) * tangent.z;
    return integral / NJULF_LTC_PI;
}

float NjulfLtcTubeIntegral(
    mat3 transform,
    mat3 shadingBasis,
    vec3 position,
    vec3 p1,
    vec3 p2,
    float radius)
{
    vec3 localP1 = shadingBasis * (p1 - position);
    vec3 localP2 = shadingBasis * (p2 - position);
    vec3 transformedP1 = transform * localP1;
    vec3 transformedP2 = transform * localP2;
    vec3 edgeNormal = cross(localP1, localP2);
    float edgeLength = length(edgeNormal);
    float line = 0.0;
    if (edgeLength > 1e-8)
    {
        vec3 ortho = edgeNormal / edgeLength;
        float width = 1.0 / max(length(inverse(transpose(transform)) * ortho), 1e-8);
        line = radius * width * NjulfLtcDiffuseLine(transformedP1, transformedP2);
    }

    vec3 tangent = normalize(localP2 - localP1);
    float area = NJULF_LTC_PI * radius * radius;
    float determinantValue = abs(determinant(transform));
    float caps = 0.0;
    vec3 directions[2] = vec3[2](normalize(localP1), normalize(localP2));
    vec3 points[2] = vec3[2](localP1, localP2);
    for (int i = 0; i < 2; i++)
    {
        vec3 transformedDirection = transform * directions[i];
        float transformedLength = length(transformedDirection);
        float distribution = transformedLength > 1e-8
            ? max(transformedDirection.z / transformedLength, 0.0) *
                determinantValue /
                max(transformedLength * transformedLength * transformedLength,
                    1e-8) / NJULF_LTC_PI
            : 0.0;
        float facing = i == 0
            ? max(dot(tangent, directions[i]), 0.0)
            : max(dot(-tangent, directions[i]), 0.0);
        caps += area * distribution * facing /
            max(dot(points[i], points[i]), 1e-8);
    }
    return max(min(line + caps, 1.0), 0.0);
}

float EvaluateNjulfIesProfile(GPULight light, vec3 directionFromLight)
{
    if (light.IesTextureIndex < FIRST_DYNAMIC_TEXTURE_INDEX ||
        !NjulfIsPunctualLight(light))
    {
        return 1.0;
    }
    vec3 axis;
    vec3 up;
    vec3 right;
    if (!NjulfBuildLightFrame(light, axis, up, right))
        return 1.0;
    float cosine = clamp(dot(normalize(directionFromLight), axis), -1.0, 1.0);
    float polar = acos(cosine);
    float azimuth = atan(
        dot(directionFromLight, up),
        dot(directionFromLight, right)) + light.IesRotationRadians;
    const float iesHeight = 128.0;
    float verticalUv = ((polar / NJULF_LTC_PI) * (iesHeight - 1.0) + 0.5) /
        iesHeight;
    vec2 uv = vec2(
        fract(azimuth / (2.0 * NJULF_LTC_PI)),
        verticalUv);
    float multiplier = texture(
        BindlessTextures[nonuniformEXT(light.IesTextureIndex)], uv).r;
    return NjulfAreaFinite(multiplier) ? max(multiplier, 0.0) : 1.0;
}

NjulfAreaLightResult EvaluateNjulfAreaLightLtc(
    GPULight light,
    vec3 position,
    vec3 normal,
    vec3 viewDirection,
    float roughness,
    vec3 diffuseReflectance,
    vec3 specularF0)
{
    NjulfAreaLightResult result;
    result.lighting = vec3(0.0);
    result.diffuse = vec3(0.0);
    result.representativeDirection = vec3(0.0, 1.0, 0.0);
    result.rangeAttenuation = 0.0;
    vec3 axis;
    vec3 up;
    vec3 right;
    if (!NjulfBuildLightFrame(light, axis, up, right) ||
        NjulfAreaSurfaceArea(light) <= 0.0 || light.Range <= 0.0)
    {
        return result;
    }
    if ((light.Type == GPU_LIGHT_TYPE_RECTANGLE ||
         light.Type == GPU_LIGHT_TYPE_DISK) &&
        !NjulfAreaLightIsTwoSided(light) &&
        dot(axis, position - light.Position) <= 0.0)
    {
        return result;
    }

    float closestDistance = NjulfAreaClosestDistance(
        light, position, axis, up, right);
    result.rangeAttenuation = EvaluateNjulfFiniteRangeWindow(
        closestDistance, light.Range);
    if (result.rangeAttenuation <= 0.0)
        return result;
    vec3 toCenter = light.Position - position;
    float centerDistance = length(toCenter);
    result.representativeDirection = centerDistance > 1e-6
        ? toCenter / centerDistance
        : axis;
    float nDotV = clamp(dot(normal, viewDirection), 0.0, 1.0);
    if (nDotV <= 0.0)
        return result;
    vec2 lookupUv = (vec2(
        clamp(roughness, 0.001, 1.0),
        sqrt(max(1.0 - nDotV, 0.0))) * NJULF_LTC_LUT_SCALE) +
        NJULF_LTC_LUT_BIAS;
    vec4 matrixData = texture(
        BindlessTextures[nonuniformEXT(AREA_LIGHT_LTC_MATRIX_TEXTURE_INDEX)],
        lookupUv);
    vec4 amplitudeData = texture(
        BindlessTextures[nonuniformEXT(AREA_LIGHT_LTC_AMPLITUDE_TEXTURE_INDEX)],
        lookupUv);
    mat3 inverseMatrix = mat3(
        vec3(matrixData.x, 0.0, matrixData.y),
        vec3(0.0, 1.0, 0.0),
        vec3(matrixData.z, 0.0, matrixData.w));
    mat3 shadingBasis = NjulfLtcShadingBasis(normal, viewDirection);
    float specularIntegral = 0.0;
    float diffuseIntegral = 0.0;
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE)
    {
        vec3 points[4];
        vec3 ex = right * (light.SizeX * 0.5);
        vec3 ey = up * (light.SizeY * 0.5);
        // Winding intentionally produces -axis: the LTC reference treats a
        // receiver-to-emitter vector opposite the polygon normal as back-facing,
        // while Njulf's Direction points from the emitter toward its front side.
        points[0] = light.Position - ex - ey;
        points[1] = light.Position - ex + ey;
        points[2] = light.Position + ex + ey;
        points[3] = light.Position + ex - ey;
        specularIntegral = NjulfLtcQuadIntegral(
            inverseMatrix, shadingBasis, position, points,
            NjulfAreaLightIsTwoSided(light));
        diffuseIntegral = NjulfLtcQuadIntegral(
            mat3(1.0), shadingBasis, position, points,
            NjulfAreaLightIsTwoSided(light));
    }
    else if (light.Type == GPU_LIGHT_TYPE_DISK)
    {
        float radius = light.SizeX * 0.5;
        specularIntegral = NjulfLtcDiskIntegral(
            inverseMatrix, shadingBasis, position,
            light.Position, right, -up, radius,
            NjulfAreaLightIsTwoSided(light));
        diffuseIntegral = NjulfLtcDiskIntegral(
            mat3(1.0), shadingBasis, position,
            light.Position, right, -up, radius,
            NjulfAreaLightIsTwoSided(light));
    }
    else if (light.Type == GPU_LIGHT_TYPE_TUBE)
    {
        vec3 p1 = light.Position - axis * (light.SizeX * 0.5);
        vec3 p2 = light.Position + axis * (light.SizeX * 0.5);
        float radius = light.SizeY * 0.5;
        specularIntegral = NjulfLtcTubeIntegral(
            inverseMatrix, shadingBasis, position, p1, p2, radius);
        diffuseIntegral = NjulfLtcTubeIntegral(
            mat3(1.0), shadingBasis, position, p1, p2, radius);
    }
    vec3 radiance = max(light.Color, vec3(0.0)) *
        max(light.Intensity, 0.0) * result.rangeAttenuation;
    vec3 fresnelIntegral =
        specularF0 * amplitudeData.x +
        (vec3(1.0) - specularF0) * amplitudeData.y;
    result.diffuse = radiance * max(diffuseReflectance, vec3(0.0)) *
        max(diffuseIntegral, 0.0);
    vec3 specular = radiance * max(fresnelIntegral, vec3(0.0)) *
        max(specularIntegral, 0.0);
    result.lighting = result.diffuse + specular;
    if (!NjulfAreaFinite(result.lighting) || !NjulfAreaFinite(result.diffuse))
    {
        result.lighting = vec3(0.0);
        result.diffuse = vec3(0.0);
    }
    return result;
}

#endif
