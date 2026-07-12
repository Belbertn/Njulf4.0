using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class SimpleDdgiShaderMirrorTests
    {
        [Test]
        public void FibonacciDirections_AreUnitLengthAndWellDistributed()
        {
            const int rayCount = 256;
            Vector3 sum = Vector3.Zero;
            float maxLengthError = 0.0f;

            for (uint i = 0; i < rayCount; i++)
            {
                Vector3 direction = SimpleDdgiFibonacciDirection(i, rayCount, Quaternion.CreateFromYawPitchRoll(0.37f, 0.19f, 0.83f));
                sum += direction;
                maxLengthError = Math.Max(maxLengthError, Math.Abs(direction.Length() - 1.0f));
            }

            Assert.Multiple(() =>
            {
                Assert.That(maxLengthError, Is.LessThan(1.0e-5f));
                Assert.That((sum / rayCount).Length(), Is.LessThan(0.01f));
            });
        }

        [Test]
        public void OctEncodeDecode_RoundTripsRepresentativeDirections()
        {
            Vector3[] directions =
            [
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f)),
                Vector3.Normalize(new Vector3(-1.0f, 0.35f, 0.72f)),
                Vector3.Normalize(new Vector3(0.25f, -0.75f, -0.61f)),
                Vector3.Normalize(new Vector3(-0.5f, -0.2f, -0.84f))
            ];

            Assert.Multiple(() =>
            {
                foreach (Vector3 direction in directions)
                {
                    Vector3 decoded = SimpleDdgiOctDecode(SimpleDdgiOctEncode(direction));
                    Assert.That(Vector3.Dot(direction, decoded), Is.GreaterThan(0.999f), direction.ToString());
                }
            });
        }

        [Test]
        public void ChebyshevVisibility_IsStableMonotonicAndVarianceAware()
        {
            float atOrBeforeMean = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.04f, receiverDistance: 1.95f);
            float nearOccluder = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.04f, receiverDistance: 2.2f);
            float farBehindOccluder = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.04f, receiverDistance: 3.0f);
            float lowVarianceFar = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.0f, receiverDistance: 3.0f);

            Assert.Multiple(() =>
            {
                Assert.That(atOrBeforeMean, Is.EqualTo(1.0f));
                Assert.That(nearOccluder, Is.GreaterThan(farBehindOccluder));
                Assert.That(farBehindOccluder, Is.GreaterThan(lowVarianceFar));
                Assert.That(lowVarianceFar, Is.GreaterThan(0.0f));
                Assert.That(lowVarianceFar, Is.LessThan(0.01f));
            });
        }

        [Test]
        public void IrradianceBlend_ForConstantRayFieldProducesTexelIndependentResult()
        {
            Vector3 radiance = new(0.25f, 0.5f, 0.75f);
            Vector3 first = BlendConstantIrradianceTexel(texel: 0, texelsPerProbe: 8, rayCount: 512, radiance);
            float maxDelta = 0.0f;

            for (uint texel = 1; texel < 64; texel++)
            {
                Vector3 sample = BlendConstantIrradianceTexel(texel, texelsPerProbe: 8, rayCount: 512, radiance);
                maxDelta = Math.Max(maxDelta, Vector3.Abs(sample - first).Length());
            }

            Assert.Multiple(() =>
            {
                Assert.That(maxDelta, Is.LessThan(0.04f));
                Assert.That(first.X, Is.EqualTo(radiance.X * MathF.PI).Within(0.04f));
                Assert.That(first.Y, Is.EqualTo(radiance.Y * MathF.PI).Within(0.04f));
                Assert.That(first.Z, Is.EqualTo(radiance.Z * MathF.PI).Within(0.04f));
            });
        }

        [Test]
        public void BilinearOctahedralSampling_IsContinuousAcrossTexelSeams()
        {
            const uint texelsPerProbe = 8;
            Vector3 leftDirection = SimpleDdgiOctDecode(new Vector2(2.0f / texelsPerProbe - 0.001f, 0.5f));
            Vector3 rightDirection = SimpleDdgiOctDecode(new Vector2(2.0f / texelsPerProbe + 0.001f, 0.5f));

            Vector4 left = SampleSyntheticAtlasBilinear(leftDirection, texelsPerProbe);
            Vector4 right = SampleSyntheticAtlasBilinear(rightDirection, texelsPerProbe);

            Assert.That(Vector4.Distance(left, right), Is.LessThan(0.01f));
        }

        [Test]
        public void BackfaceWeight_UsesSquaredHalfLambertWithoutVisibilityFloor()
        {
            float facing = SimpleDdgiBackfaceWeight(surfaceNormalDotMinusProbeDirection: 1.0f);
            float perpendicular = SimpleDdgiBackfaceWeight(surfaceNormalDotMinusProbeDirection: 0.0f);
            float backFacing = SimpleDdgiBackfaceWeight(surfaceNormalDotMinusProbeDirection: -1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(facing, Is.EqualTo(1.0f));
                Assert.That(perpendicular, Is.EqualTo(0.25f).Within(1.0e-6f));
                Assert.That(backFacing, Is.EqualTo(0.0f));
                Assert.That(SimpleDdgiFinalProbeWeight(0.5f, backFacing, 0.0f), Is.EqualTo(0.5e-5f).Within(1.0e-8f));
            });
        }

        [Test]
        public void BiasedPosition_MovesReceiverBeforeChebyshevDistance()
        {
            Vector3 worldPos = new(1.0f, 2.0f, 3.0f);
            Vector3 normal = Vector3.UnitY;
            Vector3 viewDir = Vector3.UnitZ;

            Vector3 biased = SimpleDdgiBiasedSamplePosition(worldPos, normal, viewDir, normalBias: 0.1f, viewBias: 0.3f);

            Assert.That(biased, Is.EqualTo(new Vector3(1.0f, 2.1f, 3.3f)));
        }

        [Test]
        public void VolumeSelection_FinestContainingVolumeWinsAndFadesAtAuthoredEdge()
        {
            CpuSimpleDdgiVolume authored = new(
                Min: new Vector3(-2.0f, -1.0f, -2.0f),
                Max: new Vector3(2.0f, 1.0f, 2.0f),
                Spacing: 0.5f,
                VolumeIndex: 0);
            CpuSimpleDdgiVolume ring = new(
                Min: new Vector3(-12.0f, -6.0f, -12.0f),
                Max: new Vector3(12.0f, 6.0f, 12.0f),
                Spacing: 1.0f,
                VolumeIndex: 1);
            CpuSimpleDdgiVolume[] volumes = [authored, ring];

            CpuSimpleDdgiSelection center = SelectVolume(volumes, Vector3.Zero);
            CpuSimpleDdgiSelection nearEdge = SelectVolume(volumes, new Vector3(1.75f, 0.0f, 0.0f));
            CpuSimpleDdgiSelection outside = SelectVolume(volumes, new Vector3(2.05f, 0.0f, 0.0f));

            Assert.Multiple(() =>
            {
                Assert.That(center.SelectedVolume, Is.EqualTo(0));
                Assert.That(center.BlendVolume, Is.EqualTo(-1));
                Assert.That(center.EdgeWeight, Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(nearEdge.SelectedVolume, Is.EqualTo(0));
                Assert.That(nearEdge.BlendVolume, Is.EqualTo(1));
                Assert.That(nearEdge.EdgeWeight, Is.GreaterThan(0.0f).And.LessThan(1.0f));
                Assert.That(outside.SelectedVolume, Is.EqualTo(1));
                Assert.That(outside.BlendVolume, Is.EqualTo(-1));
            });
        }

        [Test]
        public void VolumeEdgeFade_IsMonotonicAndContinuousAcrossBoundary()
        {
            CpuSimpleDdgiVolume authored = new(
                Min: new Vector3(-2.0f, -1.0f, -2.0f),
                Max: new Vector3(2.0f, 1.0f, 2.0f),
                Spacing: 0.5f,
                VolumeIndex: 0);
            CpuSimpleDdgiVolume ring = new(
                Min: new Vector3(-12.0f, -6.0f, -12.0f),
                Max: new Vector3(12.0f, 6.0f, 12.0f),
                Spacing: 1.0f,
                VolumeIndex: 1);
            CpuSimpleDdgiVolume[] volumes = [authored, ring];

            float previous = 1.0f;
            float maxStep = 0.0f;
            for (int i = 0; i <= 16; i++)
            {
                float x = 1.2f + i * 0.05f;
                CpuSimpleDdgiSelection selection = SelectVolume(volumes, new Vector3(x, 0.0f, 0.0f));
                float effectiveAuthoredWeight = selection.SelectedVolume == 0 ? selection.EdgeWeight : 0.0f;
                Assert.That(effectiveAuthoredWeight, Is.LessThanOrEqualTo(previous + 1.0e-5f), $"x={x}");
                maxStep = Math.Max(maxStep, Math.Abs(previous - effectiveAuthoredWeight));
                previous = effectiveAuthoredWeight;
            }

            Assert.That(maxStep, Is.LessThan(0.2f));
        }

        [Test]
        public void ScrollCopyRuns_PreserveProbeWorldPositionsAndExposeSlabs()
        {
            const int countX = 4;
            const int countY = 3;
            const int countZ = 2;
            const int deltaX = 1;
            const int deltaY = 0;
            const int deltaZ = -1;
            Vector3 currentOrigin = Vector3.Zero;
            Vector3 previousOrigin = new(deltaX, deltaY, deltaZ);
            var runs = SimpleDdgiVolumeManager.BuildScrollCopyRunsForTest(countX, countY, countZ, deltaX, deltaY, deltaZ);
            bool[] copiedNew = new bool[countX * countY * countZ];
            int copiedCount = 0;
            float maxWorldDelta = 0.0f;

            foreach ((int oldLocal, int newLocal, int runCount) in runs)
            {
                for (int i = 0; i < runCount; i++)
                {
                    int oldIndex = oldLocal + i;
                    int newIndex = newLocal + i;
                    Vector3 oldWorld = previousOrigin + DecodeProbeCoord(oldIndex, countX, countY);
                    Vector3 newWorld = currentOrigin + DecodeProbeCoord(newIndex, countX, countY);
                    maxWorldDelta = Math.Max(maxWorldDelta, Vector3.Distance(oldWorld, newWorld));
                    copiedNew[newIndex] = true;
                    copiedCount++;
                }
            }

            int exposedCount = copiedNew.Count(copied => !copied);

            Assert.Multiple(() =>
            {
                Assert.That(runs, Has.Count.EqualTo((countY - Math.Abs(deltaY)) * (countZ - Math.Abs(deltaZ))));
                Assert.That(copiedCount, Is.EqualTo((countX - Math.Abs(deltaX)) * (countY - Math.Abs(deltaY)) * (countZ - Math.Abs(deltaZ))));
                Assert.That(exposedCount, Is.EqualTo(countX * countY * countZ - copiedCount));
                Assert.That(maxWorldDelta, Is.LessThan(1.0e-6f));
            });
        }

        [Test]
        public void RelocationClassificationMirror_ClampsDecaysAndClassifiesBackfaceDominatedProbesInactive()
        {
            CpuSimpleRayResult[] mixed =
            [
                new(Vector3.UnitX, HitKind: 2.0f, Distance: 0.10f),
                new(Vector3.UnitX, HitKind: 2.0f, Distance: 0.20f),
                new(-Vector3.UnitX, HitKind: 1.0f, Distance: 1.50f),
                new(Vector3.UnitY, HitKind: 0.0f, Distance: 4.00f)
            ];
            CpuSimpleRelocationResult normalUpdate = RelocateAndClassify(mixed, spacing: 1.0f, previousRelocation: Vector3.Zero, fresh: false);
            CpuSimpleRelocationResult freshUpdate = RelocateAndClassify(mixed, spacing: 1.0f, previousRelocation: Vector3.Zero, fresh: true);
            CpuSimpleRelocationResult decayed = RelocateAndClassify(
                [new(Vector3.UnitY, HitKind: 1.0f, Distance: 2.0f)],
                spacing: 1.0f,
                previousRelocation: new Vector3(0.2f, 0.0f, 0.0f),
                fresh: false);
            CpuSimpleRelocationResult allMiss = RelocateAndClassify(
                [new(Vector3.UnitX, HitKind: 0.0f, Distance: 4.0f), new(Vector3.UnitY, HitKind: 0.0f, Distance: 4.0f)],
                spacing: 1.0f,
                previousRelocation: Vector3.Zero,
                fresh: false);

            Assert.Multiple(() =>
            {
                Assert.That(normalUpdate.Active, Is.False);
                Assert.That(normalUpdate.Classification, Is.EqualTo(1));
                Assert.That(normalUpdate.Relocation.X, Is.EqualTo(0.07f).Within(1.0e-5f));
                Assert.That(freshUpdate.Relocation.X, Is.EqualTo(0.2f).Within(1.0e-5f));
                Assert.That(decayed.Relocation.X, Is.EqualTo(0.1274f).Within(1.0e-5f));
                Assert.That(allMiss.Active, Is.True);
                Assert.That(allMiss.Classification, Is.EqualTo(0));
                Assert.That(allMiss.MissRatio, Is.EqualTo(1.0f));
            });
        }

        [Test]
        public void VisibilityBlend_WithNoRayWeightKeepsPreviousInitializedMoments()
        {
            Vector4 previous = new(2.0f, 4.25f, 1.0f, 1.0f);
            Vector4 fresh = Vector4.Zero;

            Assert.Multiple(() =>
            {
                Assert.That(BlendVisibilityOrKeepPrevious(weightSum: 0.0f, previous, spacing: 1.25f, hysteresis: 0.97f), Is.EqualTo(previous));
                Assert.That(BlendVisibilityOrKeepPrevious(weightSum: 0.0f, fresh, spacing: 1.25f, hysteresis: 0.0f), Is.EqualTo(new Vector4(5.0f, 25.0f, 1.0f, 1.0f)));
            });
        }

        [Test]
        public void SimpleDdgiShaderContracts_ArePresentAndAvoidLegacyConfidenceChain()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
            string blend = ReadRepoText("Njulf.Shaders", "ddgi_simple_blend.comp");
            string relocate = ReadRepoText("Njulf.Shaders", "ddgi_simple_relocate_classify.comp");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("vec3 SampleSimpleDdgiIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("struct SimpleDdgiDebugSample"));
                Assert.That(shared, Does.Contain("SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("SimpleDdgiVolume ReadSimpleDdgiVolume(uint bufferIndex, uint volumeIndex)"));
                Assert.That(shared, Does.Contain("bool SelectSimpleDdgiVolume(SimpleDdgiParams p, vec3 worldPosition"));
                Assert.That(shared, Does.Contain("return mix(nextIrradiance, selectedIrradiance, edgeWeight) * p.indirectIntensity;"));
                Assert.That(shared, Does.Contain("vec4 SampleSimpleDdgiAtlasBilinear(uint bufferIndex, uint probeIndex, vec3 direction, uint texelsPerProbe)"));
                Assert.That(shared, Does.Contain("float backfaceWeight = halfLambert * halfLambert;"));
                Assert.That(shared, Does.Contain("result.visibilityMomentMean = mean;"));
                Assert.That(shared, Does.Contain("result.visibilityMomentVariance = variance;"));
                Assert.That(shared, Does.Contain("result.visibilityConfidence = mean > 0.0001"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_ENABLED"));
                Assert.That(shared, Does.Contain("SimpleDdgiBiasedSamplePosition(worldPos, safeNormal, viewDir, p)"));
                Assert.That(shared, Does.Contain("SimpleDdgiProbeState ReadSimpleDdgiProbeState(uint bufferIndex, uint probeIndex)"));
                Assert.That(shared, Does.Contain("SimpleDdgiProbeUpdate ReadSimpleDdgiProbeUpdate(uint bufferIndex, uint queueOffset)"));
                Assert.That(shared, Does.Contain("vec3 SimpleDdgiProbeRelocatedPosition(uint probeIndex, SimpleDdgiVolume volume, uint localProbeIndex)"));
                Assert.That(shared, Does.Contain("state.classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE"));
                Assert.That(trace, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, updateProbeOffset);"));
                Assert.That(trace, Does.Contain("vec3 probePosition = SimpleDdgiProbeRelocatedPosition(probeIndex, volume, localProbeIndex);"));
                Assert.That(trace, Does.Contain("bool frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);"));
                Assert.That(trace, Does.Contain("hitKind = frontFace ? 1.0 : 2.0;"));
                Assert.That(blend, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, localProbeOffset);"));
                Assert.That(blend, Does.Contain("float probeHysteresis = (update.flags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0u ? 0.0 : params.hysteresis;"));
                Assert.That(relocate, Does.Contain("SimpleDdgiProbeState previous = ReadSimpleDdgiProbeState(pc.ProbeStateBufferIndex, probeIndex);"));
                Assert.That(relocate, Does.Contain("bool activeProbe = backfaceRatio < SIMPLE_DDGI_INACTIVE_BACKFACE_RATIO;"));
                Assert.That(relocate, Does.Contain("state.classification = activeProbe ? SIMPLE_DDGI_CLASSIFICATION_ACTIVE : SIMPLE_DDGI_CLASSIFICATION_INACTIVE;"));
                Assert.That(relocate, Does.Contain("WriteRelocationClassification(probeIndex, blendedRelocation"));
                Assert.That(shared, Does.Not.Contain("confidence chain").IgnoreCase);
                Assert.That(shared, Does.Not.Contain("max(visibility, 0.03)"));
                Assert.That(forward, Does.Contain("bool simpleDdgiActive = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_ENABLED) != 0u && simpleDdgiParams.probeCount > 0u;"));
                Assert.That(forward, Does.Contain("vec3 simpleIrradiance = SampleSimpleDdgiIrradiance(fragWorldPosition, ddgiNormal, viewDirection);"));
                Assert.That(forward, Does.Contain("SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug(fragWorldPosition, ddgiNormal, viewDirection);"));
                Assert.That(forward, Does.Contain("ddgiSample.visibilityMomentMean = simpleDebug.visibilityMomentMean;"));
                Assert.That(forward, Does.Contain("ddgiSample.visibilityConfidence = simpleDebug.visibilityConfidence;"));
                Assert.That(forward, Does.Contain("ddgiSample.cascadeIndex = float(simpleDebug.volumeIndex);"));
                Assert.That(forward, Does.Contain("ddgiSample.minProbeSpacing = selectedSimpleVolume.spacing;"));
                Assert.That(forward, Does.Contain("ddgiDiffuse = simpleIrradiance * albedo * max(1.0 - metallic, 0.0) / PI;"));
                Assert.That(forward, Does.Contain("finalDiffuseIndirect = (ddgiDiffuse + diffuseIbl) * indirectAo;"));
            });
        }

        private static Vector3 BlendConstantIrradianceTexel(uint texel, uint texelsPerProbe, uint rayCount, Vector3 radiance)
        {
            uint x = texel % texelsPerProbe;
            uint y = texel / texelsPerProbe;
            Vector2 uv = new((x + 0.5f) / texelsPerProbe, (y + 0.5f) / texelsPerProbe);
            Vector3 texelDirection = SimpleDdgiOctDecode(uv);
            Vector3 accumulated = Vector3.Zero;
            float weightSum = 0.0f;

            for (uint ray = 0; ray < rayCount; ray++)
            {
                Vector3 rayDirection = SimpleDdgiFibonacciDirection(ray, rayCount, Quaternion.Identity);
                float weight = Math.Max(Vector3.Dot(texelDirection, rayDirection), 0.0f);
                accumulated += radiance * weight;
                weightSum += weight;
            }

            return weightSum > 0.000001f
                ? accumulated * (MathF.PI / weightSum)
                : Vector3.Zero;
        }

        private static Vector4 SampleSyntheticAtlasBilinear(Vector3 direction, uint texelsPerProbe)
        {
            Vector2 texelUv = SimpleDdgiOctEncode(direction) * texelsPerProbe - new Vector2(0.5f);
            Vector2 baseF = new(MathF.Floor(texelUv.X), MathF.Floor(texelUv.Y));
            Vector2 f = texelUv - baseF;
            Vector4 s00 = SyntheticAtlasTexel(MirrorOctTexel((int)baseF.X, (int)baseF.Y, texelsPerProbe), texelsPerProbe);
            Vector4 s10 = SyntheticAtlasTexel(MirrorOctTexel((int)baseF.X + 1, (int)baseF.Y, texelsPerProbe), texelsPerProbe);
            Vector4 s01 = SyntheticAtlasTexel(MirrorOctTexel((int)baseF.X, (int)baseF.Y + 1, texelsPerProbe), texelsPerProbe);
            Vector4 s11 = SyntheticAtlasTexel(MirrorOctTexel((int)baseF.X + 1, (int)baseF.Y + 1, texelsPerProbe), texelsPerProbe);
            return Vector4.Lerp(Vector4.Lerp(s00, s10, f.X), Vector4.Lerp(s01, s11, f.X), f.Y);
        }

        private static uint MirrorOctTexel(int x, int y, uint texelsPerProbe)
        {
            int n = (int)texelsPerProbe;
            if (x < 0)
            {
                x = -x - 1;
                y = n - 1 - y;
            }
            else if (x >= n)
            {
                x = 2 * n - x - 1;
                y = n - 1 - y;
            }

            if (y < 0)
            {
                y = -y - 1;
                x = n - 1 - x;
            }
            else if (y >= n)
            {
                y = 2 * n - y - 1;
                x = n - 1 - x;
            }

            x = Math.Clamp(x, 0, n - 1);
            y = Math.Clamp(y, 0, n - 1);
            return (uint)(x + y * n);
        }

        private static Vector4 SyntheticAtlasTexel(uint texel, uint texelsPerProbe)
        {
            uint x = texel % texelsPerProbe;
            uint y = texel / texelsPerProbe;
            return new Vector4(x / (float)texelsPerProbe, y / (float)texelsPerProbe, 0.0f, 1.0f);
        }

        private static float SimpleDdgiBackfaceWeight(float surfaceNormalDotMinusProbeDirection)
        {
            float halfLambert = Math.Clamp(surfaceNormalDotMinusProbeDirection * 0.5f + 0.5f, 0.0f, 1.0f);
            return halfLambert * halfLambert;
        }

        private static float SimpleDdgiFinalProbeWeight(float trilinear, float backfaceWeight, float visibility)
        {
            return Math.Max(trilinear * backfaceWeight * visibility, trilinear * 1.0e-5f);
        }

        private static Vector3 SimpleDdgiBiasedSamplePosition(Vector3 worldPos, Vector3 normal, Vector3 viewDir, float normalBias, float viewBias)
        {
            Vector3 safeNormal = normal.Length() > 0.00001f ? Vector3.Normalize(normal) : Vector3.UnitY;
            Vector3 safeView = viewDir.Length() > 0.00001f ? Vector3.Normalize(viewDir) : safeNormal;
            return worldPos + safeNormal * normalBias + safeView * viewBias;
        }

        private static Vector4 BlendVisibilityOrKeepPrevious(float weightSum, Vector4 previous, float spacing, float hysteresis)
        {
            Vector2 moments;
            if (weightSum > 0.000001f)
                moments = new Vector2(1.0f, 1.0f);
            else if (previous.Z > 0.5f)
                return previous;
            else
                moments = new Vector2(spacing * 4.0f, spacing * spacing * 16.0f);

            return Vector4.Lerp(new Vector4(moments.X, moments.Y, 1.0f, 1.0f), previous, hysteresis);
        }

        private static Vector3 DecodeProbeCoord(int index, int countX, int countY)
        {
            int xy = countX * countY;
            int z = index / xy;
            int rem = index - z * xy;
            int y = rem / countX;
            int x = rem - y * countX;
            return new Vector3(x, y, z);
        }

        private static CpuSimpleRelocationResult RelocateAndClassify(
            ReadOnlySpan<CpuSimpleRayResult> rays,
            float spacing,
            Vector3 previousRelocation,
            bool fresh)
        {
            int missCount = 0;
            int hitCount = 0;
            int backfaceCount = 0;
            Vector3 backfaceDirectionSum = Vector3.Zero;
            float nearestBackfaceDistance = float.MaxValue;
            Vector3 nearestBackfaceDirection = Vector3.Zero;
            float nearestHitDistance = float.MaxValue;

            foreach (CpuSimpleRayResult ray in rays)
            {
                if (ray.HitKind < 0.5f)
                {
                    missCount++;
                    continue;
                }

                hitCount++;
                nearestHitDistance = Math.Min(nearestHitDistance, ray.Distance);
                if (ray.HitKind > 1.5f)
                {
                    backfaceCount++;
                    Vector3 direction = Vector3.Normalize(ray.Direction);
                    backfaceDirectionSum += direction;
                    if (ray.Distance < nearestBackfaceDistance)
                    {
                        nearestBackfaceDistance = ray.Distance;
                        nearestBackfaceDirection = direction;
                    }
                }
            }

            Vector3 targetRelocation = Vector3.Zero;
            if (backfaceCount > 0)
            {
                Vector3 direction = backfaceDirectionSum.Length() > 0.00001f
                    ? Vector3.Normalize(backfaceDirectionSum)
                    : nearestBackfaceDirection;
                float maxOffset = spacing * 0.45f;
                float preferredOffset = nearestBackfaceDistance < float.MaxValue
                    ? Math.Clamp(nearestBackfaceDistance + spacing * 0.10f, 0.0f, maxOffset)
                    : maxOffset;
                targetRelocation = direction * preferredOffset;
            }

            float alpha = fresh ? 1.0f : 0.35f;
            Vector3 relocation = Vector3.Lerp(previousRelocation * 0.98f, targetRelocation, alpha);
            float maxRelocation = spacing * 0.45f;
            if (relocation.Length() > maxRelocation)
                relocation = Vector3.Normalize(relocation) * maxRelocation;

            int rayCount = Math.Max(rays.Length, 1);
            float backfaceRatio = backfaceCount / (float)rayCount;
            bool active = backfaceRatio < 0.25f;
            return new CpuSimpleRelocationResult(
                relocation,
                active,
                active ? 0u : 1u,
                missCount / (float)rayCount,
                hitCount / (float)rayCount,
                backfaceRatio,
                nearestHitDistance < float.MaxValue ? nearestHitDistance : 0.0f);
        }

        private static CpuSimpleDdgiSelection SelectVolume(ReadOnlySpan<CpuSimpleDdgiVolume> volumes, Vector3 worldPosition)
        {
            for (int i = 0; i < volumes.Length; i++)
            {
                CpuSimpleDdgiVolume volume = volumes[i];
                if (!Contains(volume, worldPosition))
                    continue;

                float edgeWeight = EdgeWeight(volume, worldPosition);
                int blendVolume = -1;
                if (edgeWeight < 1.0f)
                {
                    for (int j = i + 1; j < volumes.Length; j++)
                    {
                        if (Contains(volumes[j], worldPosition))
                        {
                            blendVolume = volumes[j].VolumeIndex;
                            break;
                        }
                    }
                }

                return new CpuSimpleDdgiSelection(volume.VolumeIndex, blendVolume, edgeWeight);
            }

            return new CpuSimpleDdgiSelection(-1, -1, 0.0f);
        }

        private static bool Contains(CpuSimpleDdgiVolume volume, Vector3 worldPosition) =>
            worldPosition.X >= volume.Min.X && worldPosition.Y >= volume.Min.Y && worldPosition.Z >= volume.Min.Z &&
            worldPosition.X <= volume.Max.X && worldPosition.Y <= volume.Max.Y && worldPosition.Z <= volume.Max.Z;

        private static float EdgeWeight(CpuSimpleDdgiVolume volume, Vector3 worldPosition)
        {
            Vector3 minFace = worldPosition - volume.Min;
            Vector3 maxFace = volume.Max - worldPosition;
            float edgeDistance = Math.Min(Math.Min(Math.Min(minFace.X, minFace.Y), minFace.Z), Math.Min(Math.Min(maxFace.X, maxFace.Y), maxFace.Z));
            float t = Math.Clamp(edgeDistance / Math.Max(volume.Spacing * 1.5f, 0.001f), 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        private static float SimpleDdgiChebyshev(float mean, float mean2, float receiverDistance)
        {
            if (receiverDistance <= mean)
                return 1.0f;

            float variance = Math.Max(mean2 - mean * mean, 0.0025f);
            float d = receiverDistance - mean;
            return Math.Clamp(variance / (variance + d * d), 0.0f, 1.0f);
        }

        private static Vector3 SimpleDdgiFibonacciDirection(uint rayIndex, uint rayCount, Quaternion rotation)
        {
            float i = rayIndex;
            float n = Math.Max(rayCount, 1u);
            const float golden = 2.399963229728653f;
            float z = 1.0f - 2.0f * (i + 0.5f) / n;
            float radius = MathF.Sqrt(Math.Max(0.0f, 1.0f - z * z));
            float angle = golden * i;
            return Vector3.Normalize(Vector3.Transform(new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, z), rotation));
        }

        private static Vector2 SimpleDdgiOctEncode(Vector3 n)
        {
            n /= Math.Max(Math.Abs(n.X) + Math.Abs(n.Y) + Math.Abs(n.Z), 0.000001f);
            Vector2 encoded = new(n.X, n.Y);
            if (n.Z < 0.0f)
            {
                encoded = new(
                    (1.0f - Math.Abs(encoded.Y)) * Math.Sign(encoded.X),
                    (1.0f - Math.Abs(encoded.X)) * Math.Sign(encoded.Y));
            }

            return encoded * 0.5f + new Vector2(0.5f);
        }

        private static Vector3 SimpleDdgiOctDecode(Vector2 e)
        {
            Vector2 f = e * 2.0f - Vector2.One;
            Vector3 n = new(f.X, f.Y, 1.0f - Math.Abs(f.X) - Math.Abs(f.Y));
            float t = Math.Clamp(-n.Z, 0.0f, 1.0f);
            n.X += n.X >= 0.0f ? -t : t;
            n.Y += n.Y >= 0.0f ? -t : t;
            return Vector3.Normalize(n);
        }

        private readonly record struct CpuSimpleDdgiVolume(Vector3 Min, Vector3 Max, float Spacing, int VolumeIndex);

        private readonly record struct CpuSimpleDdgiSelection(int SelectedVolume, int BlendVolume, float EdgeWeight);

        private readonly record struct CpuSimpleRayResult(Vector3 Direction, float HitKind, float Distance);

        private readonly record struct CpuSimpleRelocationResult(
            Vector3 Relocation,
            bool Active,
            uint Classification,
            float MissRatio,
            float HitRatio,
            float BackfaceRatio,
            float NearestHitDistance);

        private static string ReadRepoText(params string[] pathParts)
        {
            string? directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new FileNotFoundException("Could not locate repository file.", Path.Combine(pathParts));
        }
    }
}
