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

        [TestCase(32)]
        [TestCase(128)]
        public void FibonacciDirections_RemainUniformForAdaptiveRayTiers(int rayCount)
        {
            Vector3 sum = Vector3.Zero;
            float maxLengthError = 0.0f;

            for (uint i = 0; i < rayCount; i++)
            {
                Vector3 direction = SimpleDdgiFibonacciDirection(i, (uint)rayCount, Quaternion.CreateFromYawPitchRoll(0.11f, 0.23f, 0.37f));
                sum += direction;
                maxLengthError = Math.Max(maxLengthError, Math.Abs(direction.Length() - 1.0f));
            }

            Assert.Multiple(() =>
            {
                Assert.That(maxLengthError, Is.LessThan(1.0e-5f));
                Assert.That((sum / rayCount).Length(), Is.LessThan(rayCount == 32 ? 0.04f : 0.02f));
            });
        }

        [Test]
        public void FibonacciDirections_PerProbeRotationBreaksFieldWideCorrelation()
        {
            Quaternion frameRotation = Quaternion.CreateFromYawPitchRoll(0.31f, 0.17f, 0.73f);
            Vector3 first = SimpleDdgiFibonacciDirection(17u, 128u, SimpleDdgiPerProbeRayRotation(0u, frameRotation));
            Vector3 sum = Vector3.Zero;
            float greatestMatch = -1.0f;

            for (uint probeIndex = 0u; probeIndex < 256u; probeIndex++)
            {
                Vector3 direction = SimpleDdgiFibonacciDirection(17u, 128u, SimpleDdgiPerProbeRayRotation(probeIndex, frameRotation));
                sum += direction;
                if (probeIndex != 0u)
                    greatestMatch = Math.Max(greatestMatch, Vector3.Dot(first, direction));
            }

            Assert.Multiple(() =>
            {
                Assert.That((sum / 256.0f).Length(), Is.LessThan(0.15f));
                Assert.That(greatestMatch, Is.LessThan(0.999f));
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
        public void ChebyshevVisibility_VarianceFloorScalesWithCoarseRingSpacing()
        {
            float fine = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.0f, receiverDistance: 3.0f, probeSpacing: 1.0f);
            float coarse = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.0f, receiverDistance: 3.0f, probeSpacing: 4.0f);

            Assert.That(coarse, Is.GreaterThan(fine).And.LessThan(0.1f));
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
        public void IrradianceBlend_ConstantFieldEnergyInvariantAcrossRayTiers()
        {
            Vector3 radiance = new(0.35f, 0.45f, 0.55f);
            Vector3 maintenance = BlendConstantIrradianceTexel(texel: 21, texelsPerProbe: 8, rayCount: 32, radiance);
            Vector3 full = BlendConstantIrradianceTexel(texel: 21, texelsPerProbe: 8, rayCount: 128, radiance);

            Assert.That(Vector3.Distance(maintenance, full), Is.LessThan(0.035f));
        }

        [Test]
        public void AdaptiveHysteresis_SmoothlyLowersHistoryForLightingSteps()
        {
            float unchanged = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 1.0f, currentLuma: 1.05f, changeThreshold: 0.50f, stepThreshold: 0.80f);
            float changed = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 1.0f, currentLuma: 1.75f, changeThreshold: 0.50f, stepThreshold: 0.80f);
            float stepped = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 1.0f, currentLuma: 6.0f, changeThreshold: 0.50f, stepThreshold: 0.80f);
            float boundaryA = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 1.0f, currentLuma: 1.499f, changeThreshold: 0.50f, stepThreshold: 0.80f);
            float boundaryB = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 1.0f, currentLuma: 1.501f, changeThreshold: 0.50f, stepThreshold: 0.80f);

            Assert.Multiple(() =>
            {
                Assert.That(unchanged, Is.EqualTo(0.97f).Within(0.0001f));
                Assert.That(changed, Is.LessThan(0.97f).And.GreaterThan(0.60f));
                Assert.That(stepped, Is.EqualTo(0.60f).Within(0.01f));
                Assert.That(Math.Abs(boundaryA - boundaryB), Is.LessThan(0.02f));
            });
        }

        [Test]
        public void AdaptiveHysteresis_DoesNotTreatDarkNoiseAsLightingMotion()
        {
            float noisyDark = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 0.0f, currentLuma: 0.015f, changeThreshold: 0.50f, stepThreshold: 0.80f);
            float meaningfulDarkChange = SimpleDdgiAdaptiveIrradianceHysteresis(0.97f, previousLuma: 0.0f, currentLuma: 1.0f, changeThreshold: 0.50f, stepThreshold: 0.80f);

            Assert.Multiple(() =>
            {
                Assert.That(noisyDark, Is.EqualTo(0.97f).Within(1.0e-6f));
                Assert.That(meaningfulDarkChange, Is.LessThan(0.97f));
            });
        }

        [Test]
        public void CadenceAdjustedHysteresis_EqualizesFullUpdateResponseAcrossRings()
        {
            float nearRetention = SimpleDdgiCadenceAdjustedHysteresis(0.97f, elapsedFrames: 8, maintenance: false);
            float farRetention = SimpleDdgiCadenceAdjustedHysteresis(0.97f, elapsedFrames: 32, maintenance: false);
            float maintenanceRetention = SimpleDdgiCadenceAdjustedHysteresis(0.97f, elapsedFrames: 32, maintenance: true);

            Assert.Multiple(() =>
            {
                Assert.That(MathF.Pow(nearRetention, 1.0f / 8.0f),
                    Is.EqualTo(MathF.Pow(farRetention, 1.0f / 32.0f)).Within(1.0e-6f));
                Assert.That(farRetention, Is.LessThan(nearRetention));
                Assert.That(maintenanceRetention, Is.EqualTo(0.97f).Within(1.0e-6f));
            });
        }

        [Test]
        public void CoarseVolume_FillsOwnershipMissingFromSelectedInnerVolume()
        {
            float ownership = CompositeSimpleDdgiOwnership(
                innerOwnership: 0.25f,
                outerOwnership: 1.0f,
                innerEdgeWeight: 1.0f);
            float edgeOwnership = CompositeSimpleDdgiOwnership(
                innerOwnership: 1.0f,
                outerOwnership: 1.0f,
                innerEdgeWeight: 0.5f);

            Assert.Multiple(() =>
            {
                Assert.That(ownership, Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(edgeOwnership, Is.EqualTo(1.0f).Within(1.0e-6f));
            });
        }

        [Test]
        public void IrradianceBlend_SharedRayCacheMatchesDirectStorageOrder()
        {
            CpuSimpleRayResult[] rays =
            [
                new(Vector3.Normalize(new Vector3(1.0f, 0.2f, 0.1f)), HitKind: 1.0f, Distance: 0.5f),
                new(Vector3.Normalize(new Vector3(-0.1f, 1.0f, 0.3f)), HitKind: 1.0f, Distance: 1.5f),
                new(Vector3.Normalize(new Vector3(0.4f, -0.3f, 1.0f)), HitKind: 1.0f, Distance: 2.5f),
                new(Vector3.Normalize(new Vector3(-0.8f, 0.1f, 0.6f)), HitKind: 1.0f, Distance: 3.5f)
            ];

            Vector3[] radiance =
            [
                new(0.2f, 0.1f, 0.3f),
                new(0.8f, 0.4f, 0.1f),
                new(0.0f, 0.5f, 0.2f),
                new(0.3f, 0.2f, 0.9f)
            ];

            Vector3 direct = BlendIrradianceTexelFromRays(texel: 17, texelsPerProbe: 8, rays, radiance);
            Vector3 cached = BlendIrradianceTexelFromCachedRays(texel: 17, texelsPerProbe: 8, rays, radiance);

            Assert.That(Vector3.Distance(direct, cached), Is.LessThan(1.0e-6f));
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
        public void SampledAtlasInteriorFiltering_MatchesCanonicalSsboBilinear()
        {
            const uint texelsPerProbe = 8;
            Vector3 direction = SimpleDdgiOctDecode(new Vector2(0.34f, 0.56f));

            Vector4 canonical = SampleSyntheticAtlasBilinear(direction, texelsPerProbe);
            Vector4 sampledImage = SampleSyntheticAtlasImageBilinear(direction, texelsPerProbe);

            Assert.Multiple(() =>
            {
                Assert.That(IsInteriorAtlasQuad(direction, texelsPerProbe), Is.True);
                Assert.That(Vector4.Distance(canonical, sampledImage), Is.LessThan(1.0e-6f));
            });
        }

        [Test]
        public void SampledAtlasSeams_RetainCanonicalMirrorFiltering()
        {
            const uint texelsPerProbe = 8;
            Vector3 seamDirection = SimpleDdgiOctDecode(new Vector2(0.01f, 0.50f));

            Assert.That(IsInteriorAtlasQuad(seamDirection, texelsPerProbe), Is.False,
                "A seam quad must stay on the mirror-filtered SSBO path rather than clamp a sampled image border.");
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
        public void RelocationClassificationMirror_StabilizesSparseEvidenceAndKeepsClipmapSupport()
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
            CpuSimpleRayResult[] stronglyInvalid =
            [
                new(Vector3.UnitX, HitKind: 2.0f, Distance: 0.02f),
                new(Vector3.UnitX, HitKind: 2.0f, Distance: 0.03f),
                new(Vector3.UnitX, HitKind: 2.0f, Distance: 0.04f),
                new(Vector3.UnitX, HitKind: 2.0f, Distance: 0.05f)
            ];
            CpuSimpleRelocationResult invalidAuthored = RelocateAndClassify(
                stronglyInvalid,
                spacing: 1.0f,
                previousRelocation: Vector3.Zero,
                fresh: true,
                authored: true);
            CpuSimpleRelocationResult invalidRing = RelocateAndClassify(
                stronglyInvalid,
                spacing: 1.0f,
                previousRelocation: Vector3.Zero,
                fresh: true,
                authored: false);
            CpuSimpleRelocationResult maintenance = RelocateAndClassify(
                mixed,
                spacing: 1.0f,
                previousRelocation: new Vector3(0.2f, 0.0f, 0.0f),
                fresh: false,
                previousActive: 0.6f,
                maintenance: true);

            Assert.Multiple(() =>
            {
                Assert.That(normalUpdate.Active, Is.True);
                Assert.That(normalUpdate.Classification, Is.Zero);
                Assert.That(normalUpdate.Relocation.X, Is.EqualTo(0.00675f).Within(1.0e-5f));
                Assert.That(freshUpdate.Relocation.X, Is.EqualTo(0.045f).Within(1.0e-5f));
                Assert.That(decayed.Relocation.X, Is.EqualTo(0.19f).Within(1.0e-5f));
                Assert.That(allMiss.Active, Is.True);
                Assert.That(allMiss.Classification, Is.EqualTo(0));
                Assert.That(allMiss.MissRatio, Is.EqualTo(1.0f));
                Assert.That(invalidAuthored.Active, Is.False);
                Assert.That(invalidAuthored.Classification, Is.EqualTo(1));
                Assert.That(invalidRing.Active, Is.True);
                Assert.That(invalidRing.ActiveWeight, Is.EqualTo(0.35f).Within(1.0e-5f));
                Assert.That(maintenance.ActiveWeight, Is.EqualTo(0.6f).Within(1.0e-5f));
                Assert.That(maintenance.Relocation.X, Is.EqualTo(0.2f).Within(1.0e-5f));
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
        public void RadiometricOwnership_DoesNotPremultiplyNormalizedIrradianceByProbeValidityMass()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("float SimpleDdgiRadiometricOwnership(SimpleDdgiGatherResult gather)"));
                Assert.That(shared, Does.Contain("return gather.validSupport > 0.000001"));
                Assert.That(shared, Does.Contain("? clamp(gather.spatialCoverage, 0.0, 1.0)"));
                Assert.That(shared, Does.Contain("float ownership = SimpleDdgiRadiometricOwnership(gather);"));
                Assert.That(forward, Does.Contain("float simpleOwnership = SimpleDdgiRadiometricOwnership(simpleGather);"));
                Assert.That(forward, Does.Not.Contain("float simpleOwnership = clamp(simpleGather.ownership, 0.0, 1.0);"));
                Assert.That(shared, Does.Not.Contain("float ownership = clamp(gather.ownership, 0.0, 1.0);"));
            });
        }

        [Test]
        public void SimpleDdgiShaderContracts_ArePresentAndAvoidLegacyConfidenceChain()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
            string blend = ReadRepoText("Njulf.Shaders", "ddgi_simple_blend.comp");
            string relocate = ReadRepoText("Njulf.Shaders", "ddgi_simple_relocate_classify.comp");
            string hitShading = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string simplePasses = ReadRepoText("Njulf.Rendering", "Pipeline", "SimpleDdgiPasses.cs");
            string simpleManager = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");
            string sampledAtlas = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiSampledAtlas.cs");
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("vec3 SampleSimpleDdgiIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("struct SimpleDdgiDebugSample"));
                Assert.That(shared, Does.Contain("SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("SimpleDdgiVolume ReadSimpleDdgiVolume(uint bufferIndex, uint volumeIndex)"));
                Assert.That(shared, Does.Contain("bool SelectSimpleDdgiVolume(SimpleDdgiParams p, vec3 worldPosition"));
                Assert.That(shared, Does.Contain("struct SimpleDdgiGatherResult"));
                Assert.That(shared, Does.Contain("float validSupport;"));
                Assert.That(shared, Does.Contain("float spatialCoverage;"));
                Assert.That(shared, Does.Contain("float transportVisibility;"));
                Assert.That(shared, Does.Contain("vec3 contributingVolumeColor;"));
                Assert.That(shared, Does.Contain("uint selectedVolume;"));
                Assert.That(shared, Does.Contain("uint validProbeCount;"));
                Assert.That(shared, Does.Contain("SimpleDdgiGatherResult SampleSimpleDdgiGather(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiProbeSupportsGather(SimpleDdgiProbeState state, vec4 irradiance, vec4 visibility)"));
                Assert.That(shared, Does.Contain("result.irradiance = directionalMass > 0.000001"));
                Assert.That(shared, Does.Contain("accumulated / directionalMass"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiCanSampleAtlasImage(SimpleDdgiParams p, uint bufferIndex, uint probeIndex)"));
                Assert.That(shared, Does.Contain("vec4 SampleSimpleDdgiAtlasImage("));
                Assert.That(shared, Does.Contain("vec4 SampleSimpleDdgiAtlasBilinear("));
                Assert.That(shared, Does.Contain("At octahedral seams retain the SSBO mirror lookup"));
                Assert.That(shared, Does.Contain("float directionalWeight = max(halfLambert * halfLambert, 1.0e-4);"));
                Assert.That(shared, Does.Contain("result.visibilityMomentMean = mean;"));
                Assert.That(shared, Does.Contain("result.visibilityMomentVariance = variance;"));
                Assert.That(shared, Does.Contain("result.visibilityConfidence = mean > 0.0001"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_ENABLED"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_FOG_ENABLED"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_PARTICLE_ENABLED"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_FAR_SUN_SHADOW_ENABLED"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED"));
                Assert.That(shared, Does.Not.Contain("SIMPLE_DDGI_FLAG_ROUGH_SPECULAR_ENABLED"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_ADAPTIVE_HYSTERESIS"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_LIGHTING_CHANGE_ACTIVE"));
                Assert.That(shared, Does.Contain("vec4 SimpleDdgiPerProbeRayRotation(uint probeIndex, vec4 frameRotation)"));
                Assert.That(shared, Does.Contain("if (transportVisibility < 0.05)"));
                Assert.That(shared, Does.Contain("float EstimateFarFieldSkyVisibility(vec3 worldPos)"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SKY_VISIBILITY_SAMPLE_COUNTER"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SKY_VISIBILITY_ACCUM_COUNTER"));
                Assert.That(shared, Does.Contain("vec3 SampleSimpleDdgiUnifiedIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir, bool allowFallback)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_RAY_COUNT_SHIFT"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiUpdateMatchesProbeGeneration(SimpleDdgiProbeUpdate update, SimpleDdgiProbeState state)"));
                Assert.That(shared, Does.Contain("uvec3 physicalOffset;"));
                Assert.That(shared, Does.Contain("(coord + volume.physicalOffset) % max(volume.gridCount, uvec3(1u))"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_RAYS_PER_PROBE = 256u"));
                Assert.That(shared, Does.Contain("uint SimpleDdgiUpdateRayCount(SimpleDdgiProbeUpdate update, SimpleDdgiParams p)"));
                Assert.That(shared, Does.Contain("state.luminanceChangeEma = uintBitsToFloat"));
                Assert.That(shared, Does.Contain("SimpleDdgiParams p, float volumeSpacing)"));
                Assert.That(shared, Does.Contain("selectedVolume.spacing"));
                Assert.That(shared, Does.Contain("float outerWeight = 1.0 - innerValidMass;"));
                Assert.That(shared, Does.Contain("outer.contributingVolumeColor * outerDirectionalMass"));
                Assert.That(shared, Does.Contain("inner.contributingVolumeColor * innerDirectionalMass"));
                Assert.That(shared, Does.Contain("contributorColorAccumulated / directionalMass"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_VOLUME_PRIMARY_GATHER_COUNTER_BASE + selectedVolumeIndex"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + selectedVolumeIndex"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + nextVolumeIndex"));
                Assert.That(shared, Does.Contain("SimpleDdgiProbeState ReadSimpleDdgiProbeState(uint bufferIndex, uint probeIndex)"));
                Assert.That(shared, Does.Contain("SimpleDdgiProbeUpdate ReadSimpleDdgiProbeUpdate(uint bufferIndex, uint queueOffset)"));
                Assert.That(shared, Does.Contain("vec3 SimpleDdgiProbeRelocatedPosition(uint probeIndex, SimpleDdgiVolume volume, uint localProbeIndex)"));
                Assert.That(shared, Does.Contain("state.classification != SIMPLE_DDGI_CLASSIFICATION_INACTIVE"));
                Assert.That(trace, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, updateProbeOffset);"));
                Assert.That(trace, Does.Contain("uint activeRayCount = SimpleDdgiUpdateRayCount(update, params);"));
                Assert.That(trace, Does.Contain("if (rayIndex >= activeRayCount)"));
                Assert.That(trace, Does.Contain("rayIndex * params.raysPerProbe / activeRayCount"));
                Assert.That(trace, Does.Contain("vec4 rayRotation = SimpleDdgiPerProbeRayRotation(probeIndex, params.rayRotation);"));
                Assert.That(trace, Does.Contain("SimpleDdgiFibonacciDirection(directionRayIndex, params.raysPerProbe, rayRotation)"));
                Assert.That(trace, Does.Contain("if (!SimpleDdgiUpdateMatchesProbeGeneration(update, probeState))"));
                Assert.That(trace, Does.Contain("vec3 probePosition = SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) + probeState.relocation;"));
                Assert.That(trace, Does.Contain("vec3 radiance = SampleDdgiEnvironmentMissRadianceWithFallback"));
                Assert.That(trace, Does.Contain("float nearTlasMaxDistance = farFieldEnabled"));
                Assert.That(trace, Does.Contain("TraceFarFieldClipmapDetailed(probePosition, direction, nearTlasMaxDistance, maxDistance"));
                Assert.That(trace, Does.Contain("bool frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);"));
                Assert.That(trace, Does.Contain("hitKind = frontFace ? 1.0 : 2.0;"));
                Assert.That(blend, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, localProbeOffset);"));
                Assert.That(blend, Does.Contain("SimpleDdgiAdaptiveIrradianceHysteresis"));
                Assert.That(blend, Does.Contain("SimpleDdgiAdaptiveVisibilityHysteresis"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_FLAG_LIGHTING_CHANGE_ACTIVE"));
                Assert.That(blend, Does.Contain("float stepHysteresis = min(probeHysteresis, 0.60);"));
                Assert.That(blend, Does.Contain("state.luminanceChangeEma = mix"));
                Assert.That(blend, Does.Contain("shared vec4 SharedSimpleRayRadianceDistance[256];"));
                Assert.That(blend, Does.Contain("void LoadSimpleRayCache(SimpleDdgiParams params, uint localProbeOffset, uint activeRayCount)"));
                Assert.That(blend, Does.Contain("barrier();"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_BLEND_FLAG_REDUCED_COMPLEXITY"));
                Assert.That(blend, Does.Contain("shared vec3 SharedSimpleShCoefficients[9];"));
                Assert.That(blend, Does.Contain("void BuildReducedSimpleDdgiTables(SimpleDdgiParams params, uint localProbeOffset, uint activeRayCount)"));
                Assert.That(blend, Does.Contain("float BlendReducedIrradianceTexel("));
                Assert.That(blend, Does.Contain("void BlendReducedVisibilityTexel("));
                Assert.That(blend, Does.Contain("bool reducedComplexityEnabled = (pc.Flags & SIMPLE_DDGI_BLEND_FLAG_REDUCED_COMPLEXITY) != 0u;"));
                Assert.That(blend, Does.Contain("bool sharedRayCacheEnabled = (pc.Flags & SIMPLE_DDGI_BLEND_FLAG_SHARED_RAY_CACHE) != 0u || reducedComplexityEnabled;"));
                Assert.That(blend, Does.Contain("float probeHysteresis = SimpleDdgiCadenceAdjustedHysteresis(params, update);"));
                Assert.That(blend, Does.Contain("bool maintenanceUpdate = SimpleDdgiUpdateIsMaintenance(update);"));
                Assert.That(relocate, Does.Contain("SimpleDdgiProbeState previous = ReadSimpleDdgiProbeState(pc.ProbeStateBufferIndex, probeIndex);"));
                Assert.That(relocate, Does.Contain("uint activeRayCount = SimpleDdgiUpdateRayCount(update, params);"));
                Assert.That(relocate, Does.Contain("state.luminanceChangeEma = previous.luminanceChangeEma;"));
                Assert.That(relocate, Does.Contain("float softInvalidProbeScore = max("));
                Assert.That(relocate, Does.Contain("float activeFloor = (volume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED || hardInvalidProbeScore >= 0.95) ? 0.0 : 0.35;"));
                Assert.That(relocate, Does.Contain("state.classification = inactiveProbe ? SIMPLE_DDGI_CLASSIFICATION_INACTIVE : SIMPLE_DDGI_CLASSIFICATION_ACTIVE;"));
                Assert.That(relocate, Does.Contain("WriteRelocationClassification(probeIndex, blendedRelocation"));
                Assert.That(shared, Does.Not.Contain("confidence chain").IgnoreCase);
                Assert.That(shared, Does.Not.Contain("max(visibility, 0.03)"));
                Assert.That(hitShading, Does.Contain("for (uint sourceIndex = 0u; sourceIndex < sourceCount; sourceIndex++)"));
                Assert.That(hitShading, Does.Contain("ReadDdgiEmissiveSource(sourceIndex)"));
                Assert.That(hitShading, Does.Contain("float dominantVisibility = TraceLightVisibility"));
                Assert.That(hitShading, Does.Not.Contain("ReadDdgiEmissiveSource(0u)"));
                Assert.That(forward, Does.Contain("bool simpleDdgiConfigured = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_ENABLED) != 0u && simpleDdgiParams.probeCount > 0u;"));
                Assert.That(forward, Does.Contain("(simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) != 0u;"));
                Assert.That(forward, Does.Contain("else if (simpleDdgiConfigured)"));
                Assert.That(forward, Does.Contain("finalDiffuseIndirect = diffuseIbl * fallbackWeight * indirectAo;"));
                Assert.That(forward, Does.Contain("SimpleDdgiGatherResult simpleGather = SampleSimpleDdgiGather(fragWorldPosition, ddgiNormal, viewDirection);"));
                Assert.That(forward, Does.Contain("float simpleFallback = (1.0 - simpleOwnership) * simpleDdgiParams.environmentFallbackIntensity;"));
                Assert.That(forward, Does.Contain("EstimateFarFieldSunShadow(worldPosition, normal, normalize(-light.Direction))"));
                Assert.That(forward, Does.Contain("DDGI_INVESTIGATION_FAR_SUN_SHADOW_SAMPLE_COUNTER"));
                Assert.That(forward, Does.Not.Contain("DDGI_INVESTIGATION_ROUGH_SPECULAR_SAMPLE_COUNTER"));
                Assert.That(forward, Does.Not.Contain("SampleSimpleDdgiUnifiedIrradiance(fragWorldPosition, reflectionDirection, viewDirection, false)"));
                Assert.That(forward, Does.Contain("if (IsDdgiDebugView(debugViewMode) || DdgiForwardEstimateDiagnosticPixel())"));
                Assert.That(forward, Does.Contain("SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug(fragWorldPosition, ddgiNormal, viewDirection);"));
                Assert.That(forward, Does.Contain("ddgiSample.visibilityMomentMean = simpleDebug.visibilityMomentMean;"));
                Assert.That(forward, Does.Contain("ddgiSample.visibilityConfidence = simpleGather.transportVisibility;"));
                Assert.That(forward, Does.Contain("ddgiSample.cascadeIndex = float(simpleGather.selectedVolume);"));
                Assert.That(forward, Does.Contain("simpleDdgiContributingVolumeColor = simpleGather.contributingVolumeColor;"));
                Assert.That(forward, Does.Contain("? simpleDdgiContributingVolumeColor"));
                Assert.That(forward, Does.Contain("ddgiSample.minProbeSpacing = simpleGather.selectedSpacing;"));
                Assert.That(forward, Does.Contain("ddgiDiffuse = simpleIrradiance * simpleDdgiParams.indirectIntensity * albedo * max(1.0 - metallic, 0.0) / PI;"));
                Assert.That(forward, Does.Contain("finalDiffuseIndirect = finalDdgiDiffuse + diffuseIbl * simpleFallback * indirectAo;"));
                Assert.That(forward, Does.Not.Contain("finalDiffuseIndirect = ddgiDiffuse + diffuseIbl * indirectAo;"));
                Assert.That(blend, Does.Contain("float SimpleDdgiStableRelativeDelta"));
                Assert.That(blend, Does.Contain("if (maintenanceUpdate)"));
                Assert.That(blend, Does.Contain("if (weightSum <= 0.000001 && previous.w > 0.0001)"));
                Assert.That(forward, Does.Not.Contain("specularOcclusion / PI"));
                Assert.That(forward, Does.Not.Contain("max(specularIbl, specularProbeFallback)"));
                Assert.That(simpleManager, Does.Contain("CanScheduleRelocateClassifyTransaction"));
                Assert.That(simpleManager, Does.Contain("CanScheduleBlendTransaction"));
                Assert.That(simpleManager, Does.Contain("settings.SimpleDdgiStructuredGatherEnabled && structuredGatherAvailable"));
                Assert.That(simplePasses, Does.Contain("!VolumeManager.CanScheduleRelocateClassifyTransaction"));
                Assert.That(simplePasses, Does.Contain("!VolumeManager.CanScheduleBlendTransaction"));
                Assert.That(simplePasses, Does.Contain("!gi.SimpleDdgiStructuredGatherEnabled"));
                Assert.That(simplePasses, Does.Contain("if (!VolumeManager.CanExecuteRelocateClassifyTransaction)"));
                Assert.That(simplePasses, Does.Contain("if (!VolumeManager.CanExecuteBlendTransaction)"));
                Assert.That(simplePasses, Does.Contain("VolumeManager.SynchronizeSampledAtlasesAfterBlend(cmd);"));
                Assert.That(simplePasses, Does.Contain("VolumeManager.LastSampledAtlasSynchronizationMicroseconds"));
                Assert.That(simplePasses, Does.Contain("sceneData.CpuSimpleDdgiRecordMicroseconds"));
                Assert.That(simpleManager, Does.Contain("SynchronizeSampledAtlasIfRequired(commandBuffer);"));
                Assert.That(simpleManager, Does.Contain("public void SynchronizeSampledAtlasesAfterBlend(CommandBuffer commandBuffer)"));
                Assert.That(simpleManager, Does.Contain("LastSampledAtlasSynchronizationMicroseconds"));
                Assert.That(simpleManager, Does.Contain("SampledAtlasActive ? _sampledAtlas!.LayersPerTexture : 0"));
                Assert.That(sampledAtlas, Does.Contain("ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit"));
                Assert.That(sampledAtlas, Does.Contain("FormatFeatureFlags.SampledImageFilterLinearBit"));
                Assert.That(sampledAtlas, Does.Contain("MemoryBudgetExtensionEnabled"));
                Assert.That(sampledAtlas, Does.Contain("GpuAllocator.AllocationCreateFlags.WithinBudgetBit"));
                Assert.That(sampledAtlas, Does.Contain("TransitionImagesToShaderRead(commandBuffer);"));
                Assert.That(sampledAtlas, Does.Contain("CopyUpdated("));
                Assert.That(sampledAtlas, Does.Contain("MaxTextureGroups = BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups"));
                Assert.That(renderer, Does.Contain("sceneData.SimpleDdgiSampledAtlasActive == 0"));
            });
        }

        [Test]
        public void SimpleDdgiParticipatingMediaAndParticles_UseSimpleSamplerWhenFlagged()
        {
            string fog = ReadRepoText("Njulf.Shaders", "fog.comp");
            string particleVertex = ReadRepoText("Njulf.Shaders", "particle.vert");

            Assert.Multiple(() =>
            {
                Assert.That(fog, Does.Contain("#include \"ddgi_simple_shared.glsl\""));
                Assert.That(fog, Does.Contain("SIMPLE_DDGI_FLAG_FOG_ENABLED"));
                Assert.That(fog, Does.Contain("SampleSimpleDdgiIrradiance(samplePosition, ambientNormal, -viewDirection)"));
                Assert.That(fog, Does.Contain("SampleDdgiAmbientIrradiance(samplePosition, ambientNormal, 6u)"));
                Assert.That(particleVertex, Does.Contain("#include \"ddgi_simple_shared.glsl\""));
                Assert.That(particleVertex, Does.Contain("SIMPLE_DDGI_FLAG_PARTICLE_ENABLED"));
                Assert.That(particleVertex, Does.Contain("SampleSimpleDdgiIrradiance(center, particleDdgiNormal, particleDdgiNormal)"));
                Assert.That(particleVertex, Does.Contain("SampleDdgiAmbientDiffuse(center, particleDdgiNormal, particleAlbedo, 0.75, 4u)"));
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

        private static Vector3 BlendIrradianceTexelFromRays(uint texel, uint texelsPerProbe, ReadOnlySpan<CpuSimpleRayResult> rays, ReadOnlySpan<Vector3> radiance)
        {
            uint x = texel % texelsPerProbe;
            uint y = texel / texelsPerProbe;
            Vector2 uv = new((x + 0.5f) / texelsPerProbe, (y + 0.5f) / texelsPerProbe);
            Vector3 texelDirection = SimpleDdgiOctDecode(uv);
            Vector3 accumulated = Vector3.Zero;
            float weightSum = 0.0f;

            for (int ray = 0; ray < rays.Length; ray++)
            {
                float weight = Math.Max(Vector3.Dot(texelDirection, Vector3.Normalize(rays[ray].Direction)), 0.0f);
                accumulated += radiance[ray] * weight;
                weightSum += weight;
            }

            return weightSum > 0.000001f
                ? accumulated * (MathF.PI / weightSum)
                : Vector3.Zero;
        }

        private static Vector3 BlendIrradianceTexelFromCachedRays(uint texel, uint texelsPerProbe, ReadOnlySpan<CpuSimpleRayResult> rays, ReadOnlySpan<Vector3> radiance)
        {
            var cachedRays = rays.ToArray();
            var cachedRadiance = radiance.ToArray();
            return BlendIrradianceTexelFromRays(texel, texelsPerProbe, cachedRays, cachedRadiance);
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

        private static Vector4 SampleSyntheticAtlasImageBilinear(Vector3 direction, uint texelsPerProbe)
        {
            Vector2 texelUv = SimpleDdgiOctEncode(direction) * texelsPerProbe - new Vector2(0.5f);
            Vector2 baseF = new(MathF.Floor(texelUv.X), MathF.Floor(texelUv.Y));
            Vector2 f = texelUv - baseF;
            int maximum = (int)texelsPerProbe - 1;
            int x0 = Math.Clamp((int)baseF.X, 0, maximum);
            int y0 = Math.Clamp((int)baseF.Y, 0, maximum);
            int x1 = Math.Clamp(x0 + 1, 0, maximum);
            int y1 = Math.Clamp(y0 + 1, 0, maximum);
            Vector4 s00 = SyntheticAtlasTexel((uint)(x0 + y0 * (int)texelsPerProbe), texelsPerProbe);
            Vector4 s10 = SyntheticAtlasTexel((uint)(x1 + y0 * (int)texelsPerProbe), texelsPerProbe);
            Vector4 s01 = SyntheticAtlasTexel((uint)(x0 + y1 * (int)texelsPerProbe), texelsPerProbe);
            Vector4 s11 = SyntheticAtlasTexel((uint)(x1 + y1 * (int)texelsPerProbe), texelsPerProbe);
            return Vector4.Lerp(Vector4.Lerp(s00, s10, f.X), Vector4.Lerp(s01, s11, f.X), f.Y);
        }

        private static bool IsInteriorAtlasQuad(Vector3 direction, uint texelsPerProbe)
        {
            Vector2 texelUv = SimpleDdgiOctEncode(direction) * texelsPerProbe - new Vector2(0.5f);
            Vector2 baseF = new(MathF.Floor(texelUv.X), MathF.Floor(texelUv.Y));
            return baseF.X >= 0.0f && baseF.Y >= 0.0f &&
                baseF.X + 1.0f < texelsPerProbe && baseF.Y + 1.0f < texelsPerProbe;
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

        private static float SimpleDdgiAdaptiveIrradianceHysteresis(float probeHysteresis, float previousLuma, float currentLuma, float changeThreshold, float stepThreshold)
        {
            float absoluteNoiseFloor = 0.02f;
            float relativeDelta = Math.Max(Math.Abs(currentLuma - previousLuma) - absoluteNoiseFloor, 0.0f) /
                Math.Max(Math.Max(Math.Abs(previousLuma), Math.Abs(currentLuma)), absoluteNoiseFloor);
            float changeT = SmoothStep(changeThreshold, stepThreshold, relativeDelta);
            float softHysteresis = probeHysteresis * 0.5f;
            float stepHysteresis = Math.Min(probeHysteresis, 0.60f);
            return Lerp(probeHysteresis, Lerp(softHysteresis, stepHysteresis, changeT), SmoothStep(changeThreshold * 0.75f, changeThreshold, relativeDelta));
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / Math.Max(edge1 - edge0, 0.000001f), 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float SimpleDdgiCadenceAdjustedHysteresis(float baseHysteresis, int elapsedFrames, bool maintenance)
        {
            if (maintenance)
                return baseHysteresis;
            float exponent = Math.Clamp(elapsedFrames / 8.0f, 1.0f, 8.0f);
            return MathF.Pow(baseHysteresis, exponent);
        }

        private static float CompositeSimpleDdgiOwnership(float innerOwnership, float outerOwnership, float innerEdgeWeight)
        {
            float innerMass = Math.Clamp(innerOwnership, 0.0f, 1.0f) * Math.Clamp(innerEdgeWeight, 0.0f, 1.0f);
            float outerMass = Math.Clamp(outerOwnership, 0.0f, 1.0f) * (1.0f - innerMass);
            return Math.Clamp(innerMass + outerMass, 0.0f, 1.0f);
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
            bool fresh,
            float previousActive = 1.0f,
            bool authored = false,
            bool maintenance = false)
        {
            int missCount = 0;
            int hitCount = 0;
            int backfaceCount = 0;
            Vector3 backfaceDirectionSum = Vector3.Zero;
            float nearestBackfaceDistance = float.MaxValue;
            Vector3 nearestBackfaceDirection = Vector3.Zero;
            float nearestHitDistance = float.MaxValue;
            int closeCount = 0;

            foreach (CpuSimpleRayResult ray in rays)
            {
                if (ray.HitKind < 0.5f)
                {
                    missCount++;
                    continue;
                }

                hitCount++;
                nearestHitDistance = Math.Min(nearestHitDistance, ray.Distance);
                if (ray.Distance <= spacing * 0.25f)
                    closeCount++;
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
            int rayCount = Math.Max(rays.Length, 1);
            float missRatio = missCount / (float)rayCount;
            float hitRatio = hitCount / (float)rayCount;
            float backfaceRatio = backfaceCount / (float)rayCount;
            float closeRatio = closeCount / (float)rayCount;
            float hardInvalidScore = Math.Max(
                SmoothStep(0.70f, 0.90f, closeRatio),
                SmoothStep(0.55f, 0.75f, backfaceRatio));
            float hardInvalid = SmoothStep(0.75f, 0.95f, hardInvalidScore);
            float targetActive = Math.Max(1.0f - hardInvalid, authored ? 0.0f : 0.35f);
            float stateAlpha = fresh ? 1.0f : 0.12f;
            if (targetActive > previousActive)
                stateAlpha = Math.Max(stateAlpha, 0.35f);
            float activeWeight = maintenance
                ? previousActive
                : Lerp(previousActive, targetActive, stateAlpha);

            if (!maintenance && backfaceCount > 0)
            {
                Vector3 direction = backfaceDirectionSum.Length() > 0.00001f
                    ? Vector3.Normalize(backfaceDirectionSum)
                    : nearestBackfaceDirection;
                float targetSurfaceDistance = spacing * 0.10f;
                float nearestDistance = nearestBackfaceDistance < float.MaxValue ? nearestBackfaceDistance : targetSurfaceDistance;
                float relocationEvidence = SmoothStep(0.10f, 0.35f, closeRatio) * (1.0f - missRatio);
                float neededPush = Math.Max(targetSurfaceDistance - nearestDistance, 0.0f);
                float closePush = closeRatio * spacing * 0.12f;
                float targetDistance = Math.Clamp(Math.Max(neededPush, closePush) * relocationEvidence, 0.0f, spacing * 0.45f);
                targetRelocation = direction * targetDistance;
            }

            float alpha = fresh
                ? 1.0f
                : (targetRelocation.Length() > previousRelocation.Length() ? 0.15f : 0.05f);
            Vector3 relocation = maintenance
                ? previousRelocation
                : Vector3.Lerp(previousRelocation, targetRelocation, alpha);
            float maxRelocation = spacing * 0.45f;
            if (relocation.Length() > maxRelocation)
                relocation = Vector3.Normalize(relocation) * maxRelocation;

            bool inactive = !maintenance && activeWeight <= 0.05f && hardInvalidScore >= 0.75f;
            return new CpuSimpleRelocationResult(
                relocation,
                !inactive,
                activeWeight,
                inactive ? 1u : 0u,
                missRatio,
                hitRatio,
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

        private static float SimpleDdgiChebyshev(float mean, float mean2, float receiverDistance, float probeSpacing = 1.0f)
        {
            if (receiverDistance <= mean)
                return 1.0f;

            float variance = Math.Max(mean2 - mean * mean, Math.Max(0.0025f, probeSpacing * probeSpacing * 0.0025f));
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

        private static Quaternion SimpleDdgiPerProbeRayRotation(uint probeIndex, Quaternion frameRotation)
        {
            float u1 = SimpleDdgiHashToUnitFloat(probeIndex, 0x9e3779b9u);
            float u2 = SimpleDdgiHashToUnitFloat(probeIndex, 0x7f4a7c15u);
            float u3 = SimpleDdgiHashToUnitFloat(probeIndex, 0x94d049bbu);
            float r1 = MathF.Sqrt(Math.Max(0.0f, 1.0f - u1));
            float r2 = MathF.Sqrt(Math.Max(0.0f, u1));
            Quaternion probeRotation = new(
                r1 * MathF.Sin(2.0f * MathF.PI * u2),
                r1 * MathF.Cos(2.0f * MathF.PI * u2),
                r2 * MathF.Sin(2.0f * MathF.PI * u3),
                r2 * MathF.Cos(2.0f * MathF.PI * u3));
            return Quaternion.Normalize(Quaternion.Multiply(frameRotation, probeRotation));
        }

        private static float SimpleDdgiHashToUnitFloat(uint value, uint salt) =>
            (SimpleDdgiHash(value ^ salt) >> 8) * (1.0f / 16777216.0f);

        private static uint SimpleDdgiHash(uint value)
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value;
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
            float ActiveWeight,
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
