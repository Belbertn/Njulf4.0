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
        public void ChebyshevVisibility_VarianceFloorStaysConservativeAtCoarseRingSpacing()
        {
            float fine = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.0f, receiverDistance: 3.0f, probeSpacing: 1.0f);
            float coarse = SimpleDdgiChebyshev(mean: 2.0f, mean2: 4.0f, receiverDistance: 3.0f, probeSpacing: 4.0f);

            Assert.That(coarse, Is.GreaterThan(fine).And.LessThan(0.10f));
        }

        [Test]
        public void ChebyshevVisibility_FineVarianceFloorToleratesArchitecturalMeanError()
        {
            float fine = SimpleDdgiChebyshev(
                mean: 2.0f,
                mean2: 4.0f,
                receiverDistance: 2.2f,
                probeSpacing: 1.0f);
            float coarseBlocked = SimpleDdgiChebyshev(
                mean: 2.0f,
                mean2: 4.0f,
                receiverDistance: 3.0f,
                probeSpacing: 4.0f);

            Assert.Multiple(() =>
            {
                Assert.That(fine, Is.GreaterThan(0.08f),
                    "A 20 cm fine-probe mean error should remain above the full-selection threshold.");
                Assert.That(coarseBlocked, Is.LessThan(0.10f),
                    "Coarse uncertainty must remain bounded by the mean-distance guard.");
            });
        }

        [Test]
        public void VisibilitySelection_CubesLeakProneConfidenceAndRetainsSupportFloor()
        {
            float blockedSelection = SimpleDdgiVisibilitySelectionWeight(transportVisibility: 0.0f);
            float leakProneSelection = SimpleDdgiVisibilitySelectionWeight(transportVisibility: 0.2f);
            float partialSelection = SimpleDdgiVisibilitySelectionWeight(transportVisibility: 0.5f);
            float fullSelection = SimpleDdgiVisibilitySelectionWeight(transportVisibility: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(blockedSelection, Is.EqualTo(0.05f).Within(1.0e-6f));
                Assert.That(leakProneSelection, Is.EqualTo(0.05f).Within(1.0e-6f));
                Assert.That(partialSelection, Is.EqualTo(0.125f).Within(1.0e-6f));
                Assert.That(fullSelection, Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(leakProneSelection * 0.2f, Is.EqualTo(0.01f).Within(1.0e-6f),
                    "A twenty-percent-visible probe must not regain full selection authority before normalization.");
                Assert.That(SimpleDdgiLeakAttenuation(0.0f, 1.0f), Is.EqualTo(0.05f).Within(1.0e-6f),
                    "Receiver composition, rather than gather normalization, suppresses fully blocked transport.");
            });
        }

        [Test]
        public void ReceiverOwnership_UsesDataAvailabilityWithoutRemovingFinalLeakProtection()
        {
            float visibilitySelection = SimpleDdgiVisibilitySelectionWeight(transportVisibility: 0.0f);
            float dataAvailability = 1.0f;
            float radiometricOwnership = SmoothStep(0.0f, 0.15f, dataAvailability);
            float effectiveOwnership = radiometricOwnership * SimpleDdgiLeakAttenuation(
                transportVisibility: 0.0f,
                thinWallLeakClampStrength: 0.9f);

            Assert.Multiple(() =>
            {
                Assert.That(visibilitySelection, Is.EqualTo(0.05f).Within(1.0e-6f),
                    "Visibility must retain its conservative probe-selection floor.");
                Assert.That(radiometricOwnership, Is.EqualTo(1.0f).Within(1.0e-6f),
                    "State-valid probe data must retain receiver ownership even when visibility is low.");
                Assert.That(effectiveOwnership, Is.EqualTo(0.1f).Within(1.0e-6f),
                    "The existing all-blocked thin-wall safeguard must remain active.");
            });
        }

        [Test]
        public void VisibilityMomentEstimator_IsBroadOnSmoothFieldsAndNarrowAtDepthDiscontinuities()
        {
            float[] directionCosines = [1.0f, 0.94f, 0.90f];
            float[] distances = [0.10f, 8.0f, 8.0f];

            float broadMean = DirectionalVisibilityMomentMean(
                directionCosines,
                distances,
                exponent: 16.0f);
            float narrowMean = DirectionalVisibilityMomentMean(
                directionCosines,
                distances,
                exponent: 64.0f);
            float resolvedCornerMean = ResolveDirectionalVisibilityMomentMean(
                directionCosines,
                distances,
                [true, false, false]);
            float resolvedOpenMean = ResolveDirectionalVisibilityMomentMean(
                directionCosines,
                [8.0f, 0.10f, 0.10f],
                [false, true, true]);
            float resolvedSmoothMean = ResolveDirectionalVisibilityMomentMean(
                directionCosines,
                [2.0f, 2.0f, 2.0f],
                [true, true, true]);

            Assert.Multiple(() =>
            {
                Assert.That(broadMean, Is.GreaterThan(2.0f));
                Assert.That(narrowMean, Is.LessThan(0.35f));
                Assert.That(resolvedCornerMean, Is.EqualTo(0.10f).Within(1.0e-6f),
                    "A measured corner discontinuity must reject multi-metre miss distances.");
                Assert.That(resolvedOpenMean, Is.EqualTo(8.0f).Within(1.0e-6f),
                    "An open central direction must not inherit the adjacent occluder's distance.");
                Assert.That(resolvedSmoothMean, Is.EqualTo(2.0f).Within(1.0e-6f),
                    "A smooth field must retain broad angular support rather than exposing individual rays.");
            });
        }

        [Test]
        public void LinearGridFraction_DoesNotCreateProbeCentredPlateaus()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SimpleDdgiLinearGridFraction(0.0f), Is.Zero);
                Assert.That(SimpleDdgiLinearGridFraction(0.25f), Is.EqualTo(0.25f).Within(1.0e-6f));
                Assert.That(SimpleDdgiLinearGridFraction(0.5f), Is.EqualTo(0.5f).Within(1.0e-6f));
                Assert.That(SimpleDdgiLinearGridFraction(0.75f), Is.EqualTo(0.75f).Within(1.0e-6f));
                Assert.That(SimpleDdgiLinearGridFraction(1.0f), Is.EqualTo(1.0f).Within(1.0e-6f));
            });
        }

        [Test]
        public void VisibilityHitSelection_UsesOnlyTheNearestBackfaceForRelocation()
        {
            Vector2 behindShell = SelectSimpleDdgiVisibilityHit(
                surfaceDistance: 8.0f,
                sourceHitKind: 0.0f,
                backfaceDistance: 0.10f,
                backfaceHit: true);
            Vector2 frontfaceFirst = SelectSimpleDdgiVisibilityHit(
                surfaceDistance: 0.05f,
                sourceHitKind: 1.0f,
                backfaceDistance: 0.10f,
                backfaceHit: true);

            Assert.Multiple(() =>
            {
                Assert.That(behindShell.X, Is.EqualTo(0.10f));
                Assert.That(behindShell.Y, Is.EqualTo(2.0f));
                Assert.That(frontfaceFirst.X, Is.EqualTo(0.05f));
                Assert.That(frontfaceFirst.Y, Is.EqualTo(1.0f));
            });
        }

        [Test]
        public void ThinWallLeakAttenuation_SuppressesOnlyLowVisibilityResiduals()
        {
            float blocked = SimpleDdgiLeakAttenuation(
                transportVisibility: 0.0f,
                thinWallLeakClampStrength: 0.9f);
            float transition = SimpleDdgiLeakAttenuation(
                transportVisibility: 0.04f,
                thinWallLeakClampStrength: 0.9f);
            float admitted = SimpleDdgiLeakAttenuation(
                transportVisibility: 0.08f,
                thinWallLeakClampStrength: 0.9f);
            float policyDisabled = SimpleDdgiLeakAttenuation(
                transportVisibility: 0.0f,
                thinWallLeakClampStrength: 0.0f);

            Assert.Multiple(() =>
            {
                Assert.That(blocked, Is.EqualTo(0.1f).Within(1.0e-6f));
                Assert.That(transition, Is.GreaterThan(blocked).And.LessThan(admitted));
                Assert.That(admitted, Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(policyDisabled, Is.EqualTo(1.0f).Within(1.0e-6f));
            });
        }

        [Test]
        public void VisibilitySelectedGather_NormalizesConstantIrradianceWithoutDoubleShadowing()
        {
            Vector3 irradiance = new(0.7f, 1.1f, 1.6f);
            float[] interpolationWeights = [0.45f, 0.35f, 0.20f];
            float[] transportVisibility = [1.0f, 0.20f, 0.015f];

            Vector3 gathered = NormalizeVisibilityWeightedIrradiance(
                [irradiance, irradiance, irradiance],
                interpolationWeights,
                transportVisibility);

            Assert.That(Vector3.Distance(gathered, irradiance), Is.LessThan(1.0e-6f));
        }

        [Test]
        public void VisibilitySelectedGather_RetainsSupportedIrradianceAtZeroRawVisibility()
        {
            Vector3 irradiance = new(0.4f, 0.6f, 0.8f);
            Vector3 gathered = NormalizeVisibilityWeightedIrradiance(
                [irradiance],
                [1.0f],
                [0.0f]);

            Assert.That(Vector3.Distance(gathered, irradiance), Is.LessThan(1.0e-6f));
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
                Assert.That(maintenanceRetention, Is.EqualTo(MathF.Pow(0.97f, 2.0f)).Within(1.0e-6f));
                Assert.That(maintenanceRetention, Is.LessThan(0.97f));
                Assert.That(maintenanceRetention, Is.GreaterThan(farRetention));
            });
        }

        [Test]
        public void RelocationPublication_WaitsForAFullTraceFromTheCommittedPosition()
        {
            var moved = ResolveRelocationPublication(
                previousPending: false,
                maintenance: false,
                relocationDelta: 0.20f,
                spacing: 1.0f);
            var maintenanceRetry = ResolveRelocationPublication(
                previousPending: true,
                maintenance: true,
                relocationDelta: 0.0f,
                spacing: 1.0f);
            var fullRetry = ResolveRelocationPublication(
                previousPending: true,
                maintenance: false,
                relocationDelta: 0.0f,
                spacing: 1.0f);
            var unchanged = ResolveRelocationPublication(
                previousPending: false,
                maintenance: false,
                relocationDelta: 0.0f,
                spacing: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(moved.Pending, Is.True);
                Assert.That(moved.PublishAtlas, Is.False);
                Assert.That(maintenanceRetry.Pending, Is.True);
                Assert.That(maintenanceRetry.PublishAtlas, Is.False);
                Assert.That(fullRetry.Pending, Is.False);
                Assert.That(fullRetry.PublishAtlas, Is.True);
                Assert.That(fullRetry.ResetHistory, Is.True);
                Assert.That(unchanged.Pending, Is.False);
                Assert.That(unchanged.PublishAtlas, Is.True);
                Assert.That(unchanged.ResetHistory, Is.False);
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
        public void SkyVisibilityTraceOrigin_StartsOneVoxelIntoReceiverSideAir()
        {
            Vector3 worldPosition = new(2.0f, 3.0f, 4.0f);
            Vector3 origin = ResolveSkyVisibilityTraceOrigin(
                worldPosition,
                Vector3.UnitX,
                voxelSize: 0.75f);

            Assert.That(origin, Is.EqualTo(new Vector3(2.75f, 3.0f, 4.0f)));
        }

        [TestCase(0.0f, 0.10f)]
        [TestCase(1.0f / 3.0f, 1.0f / 3.0f)]
        [TestCase(1.0f, 1.0f)]
        public void SkyVisibilityGate_AttenuatesButNeverAnnihilatesEnvironmentShare(float rawVisibility, float expected)
        {
            Assert.That(ApplySkyVisibilityFloor(rawVisibility), Is.EqualTo(expected).Within(1.0e-6f));
        }

        [Test]
        public void BiasedPosition_UsesArchitecturalThicknessAndWorldCapsAtCoarseSpacing()
        {
            Vector3 worldPos = new(1.0f, 2.0f, 3.0f);
            Vector3 biased = SimpleDdgiBoundedBiasedSamplePosition(
                worldPos,
                Vector3.UnitY,
                Vector3.UnitY,
                normalBiasScale: 0.1f,
                viewBiasScale: 0.3f,
                spacing: 9.0f,
                maximumWorldBias: 0.20f,
                architecturalThickness: 0.16f);
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                // One quarter of 16 cm is the stricter cap, so the combined
                // normal/view displacement can never grow with a 9 m far ring.
                Assert.That(Vector3.Distance(worldPos, biased), Is.LessThanOrEqualTo(0.04001f));
                Assert.That(shared, Does.Contain("float thicknessCap = max(0.002, p.architecturalThickness * 0.25);"));
                Assert.That(shared, Does.Contain("float totalBiasCap = min(p.maximumWorldBias, thicknessCap);"));
                Assert.That(shared, Does.Contain("float viewCap = min(max(totalBiasCap - normalBias, 0.0), max(0.0, spacing * 0.35));"));
                Assert.That(shared, Does.Contain("const uint SIMPLE_DDGI_HEADER_WORDS = 52u;"));
            });
        }

        [Test]
        public void BiasedInterpolation_ClampsAtTheSelectedDomainWithoutChangingWorldSpaceVolumeIdentity()
        {
            CpuSimpleDdgiVolume authored = new(
                Min: new Vector3(-2.0f, -1.0f, -2.0f),
                Max: new Vector3(2.0f, 1.0f, 2.0f),
                Spacing: 1.0f,
                VolumeIndex: 0);
            CpuSimpleDdgiVolume ring = new(
                Min: new Vector3(-8.0f, -4.0f, -8.0f),
                Max: new Vector3(8.0f, 4.0f, 8.0f),
                Spacing: 3.0f,
                VolumeIndex: 1);
            Vector3 receiver = new(1.95f, 0.0f, 0.0f);
            CpuSimpleDdgiSelection towardEdge = SelectVolume([authored, ring], receiver);
            CpuSimpleDdgiSelection awayFromEdge = SelectVolume([authored, ring], receiver);
            (Vector3 outwardPosition, bool outwardExited) = ResolveInterpolationPosition(
                authored, receiver, Vector3.UnitX, Vector3.UnitX, normalBias: 0.1f, viewBias: 0.3f);
            (Vector3 inwardPosition, bool inwardExited) = ResolveInterpolationPosition(
                authored, receiver, Vector3.UnitX, -Vector3.UnitX, normalBias: 0.1f, viewBias: 0.3f);

            Assert.Multiple(() =>
            {
                Assert.That(towardEdge.SelectedVolume, Is.EqualTo(0));
                Assert.That(awayFromEdge.SelectedVolume, Is.EqualTo(0));
                Assert.That(outwardExited, Is.True);
                Assert.That(outwardPosition.X, Is.EqualTo(authored.Max.X));
                Assert.That(inwardExited, Is.False);
                Assert.That(inwardPosition.X, Is.LessThan(authored.Max.X));
            });
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
        public void ForwardGatherPlan_PreservesLateFallbackCoverageWhileCappingExpensiveSamples()
        {
            const int maxVolumeCount = 16;
            var volumes = new CpuSimpleDdgiVolume[maxVolumeCount];
            volumes[0] = new CpuSimpleDdgiVolume(
                Min: new Vector3(-2.0f, -1.0f, -2.0f),
                Max: new Vector3(2.0f, 1.0f, 2.0f),
                Spacing: 0.5f,
                VolumeIndex: 0);
            for (int i = 1; i < maxVolumeCount - 1; i++)
            {
                float offset = 20.0f + i * 4.0f;
                volumes[i] = new CpuSimpleDdgiVolume(
                    Min: new Vector3(offset, -1.0f, -2.0f),
                    Max: new Vector3(offset + 1.0f, 1.0f, 2.0f),
                    Spacing: 1.0f,
                    VolumeIndex: i);
            }

            // The only usable fallback is deliberately the final table entry.
            // This is the reason the shader's metadata walk retains all 15
            // remaining entries rather than imposing an arbitrary smaller cap.
            volumes[^1] = new CpuSimpleDdgiVolume(
                Min: new Vector3(-12.0f, -6.0f, -12.0f),
                Max: new Vector3(12.0f, 6.0f, 12.0f),
                Spacing: 2.0f,
                VolumeIndex: maxVolumeCount - 1);

            CpuSimpleDdgiGatherPlan interior = PlanForwardGather(
                volumes,
                Vector3.Zero,
                primaryOwnership: 1.0f,
                primaryBiasOutsideSelectionDomain: false,
                ownershipThreshold: 0.9f);
            CpuSimpleDdgiGatherPlan edgeTransition = PlanForwardGather(
                volumes,
                new Vector3(1.75f, 0.0f, 0.0f),
                primaryOwnership: 1.0f,
                primaryBiasOutsideSelectionDomain: false,
                ownershipThreshold: 0.9f);
            CpuSimpleDdgiGatherPlan unsupportedInterior = PlanForwardGather(
                volumes,
                Vector3.Zero,
                primaryOwnership: 0.25f,
                primaryBiasOutsideSelectionDomain: false,
                ownershipThreshold: 0.9f);
            CpuSimpleDdgiGatherPlan biasedDomainExit = PlanForwardGather(
                volumes,
                Vector3.Zero,
                primaryOwnership: 1.0f,
                primaryBiasOutsideSelectionDomain: true,
                ownershipThreshold: 0.9f);

            Assert.Multiple(() =>
            {
                Assert.That(interior.SampledVolumeCount, Is.EqualTo(1));
                Assert.That(interior.CandidateChecks, Is.Zero);
                Assert.That(interior.FallbackVolume, Is.EqualTo(-1));

                Assert.That(edgeTransition.SampledVolumeCount, Is.EqualTo(2));
                Assert.That(edgeTransition.CandidateChecks, Is.EqualTo(maxVolumeCount - 1));
                Assert.That(edgeTransition.FallbackVolume, Is.EqualTo(maxVolumeCount - 1));

                Assert.That(unsupportedInterior.SampledVolumeCount, Is.EqualTo(2));
                Assert.That(unsupportedInterior.FallbackVolume, Is.EqualTo(maxVolumeCount - 1));
                Assert.That(biasedDomainExit.SampledVolumeCount, Is.EqualTo(2));
                Assert.That(biasedDomainExit.FallbackVolume, Is.EqualTo(maxVolumeCount - 1));

                Assert.That(edgeTransition.SampledVolumeCount, Is.LessThanOrEqualTo(2));
                Assert.That(unsupportedInterior.SampledVolumeCount, Is.LessThanOrEqualTo(2));
                Assert.That(biasedDomainExit.SampledVolumeCount, Is.LessThanOrEqualTo(2));
            });
        }

        [Test]
        public void ForwardTileCandidates_ValidateUnbiasedOwnershipAndFailClosedToBoundedSelection()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string tileManager = ReadRepoText("Njulf.Rendering", "Resources", "DdgiGatherTileManager.cs");

            Assert.Multiple(() =>
            {
                Assert.That(forward, Does.Contain("#define SIMPLE_DDGI_FORWARD_TILE_CANDIDATES 1"));
                Assert.That(shared, Does.Contain("#if SIMPLE_DDGI_FORWARD_TILE_CANDIDATES"));
                Assert.That(shared, Does.Contain("bool ReadSimpleDdgiForwardTileCandidates(out SimpleDdgiForwardTileCandidates candidates)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_GATHER_TILE_HEADER_SIMPLE_DDGI_FLAG"));
                Assert.That(shared, Does.Contain("bool TrySelectSimpleDdgiTilePrimary("));
                Assert.That(shared, Does.Contain("vec3 unbiasedReceiverWorldPosition"));
                Assert.That(shared, Does.Contain("!SimpleDdgiContains(candidate, unbiasedReceiverWorldPosition)"));
                Assert.That(shared, Does.Contain("bool TryFindSimpleDdgiTileSecondary("));
                Assert.That(shared, Does.Contain("candidates.secondaryVolumeIndex <= selectedVolumeIndex"));
                Assert.That(shared, Does.Contain("(tileCandidates.flags & SIMPLE_DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_GATHER_TILE_CANDIDATE_OVERFLOW_FLAG"));
                Assert.That(shared, Does.Contain("!selectedFromTileCandidates &&"));
                Assert.That(shared, Does.Contain("foundFallback = FindSimpleDdgiFallbackVolume("));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_GATHER_VOLUME_SAMPLES = SIMPLE_DDGI_MAX_VOLUME_COUNT"));
                Assert.That(tileManager, Does.Contain("public const uint HeaderSimpleDdgiFlag = 1u << 1"));
                Assert.That(tileManager, Does.Contain("HeaderEnabledFlag | HeaderSimpleDdgiFlag"));
                Assert.That(tileManager, Does.Contain("tile.Flags |= TileFallbackFlag;"));
                Assert.That(tileManager, Does.Contain("TileSimpleCandidateOverflowFlag = 1u << 4"));
            });
        }

        [TestCase(1u << 0, 0u, 1.0f, 1.0f, 1.0f, 1u << 0)]
        [TestCase(1u << 1, 0u, 1.0f, 1.0f, 1.0f, 1u << 1)]
        [TestCase(1u << 3, 0u, 1.0f, 1.0f, 1.0f, 1u << 2)]
        [TestCase(1u << 2, 0u, 1.0f, 1.0f, 1.0f, 1u << 3)]
        [TestCase(0u, 1u, 1.0f, 1.0f, 1.0f, 1u << 4)]
        [TestCase(0u, 0u, 0.001f, 1.0f, 1.0f, 1u << 5)]
        [TestCase(0u, 0u, 1.0f, 0.5f, 1.0f, 1u << 6)]
        [TestCase(0u, 0u, 1.0f, 1.0f, 0.5f, 1u << 7)]
        public void ProbeGatherRejectionMask_AttributesEveryProbeGate(
            uint flags,
            uint classification,
            float activeWeight,
            float irradianceAlpha,
            float visibilityMarker,
            uint expectedMask)
        {
            Assert.That(
                SimpleDdgiProbeGatherRejectionMask(
                    flags,
                    classification,
                    activeWeight,
                    irradianceAlpha,
                    visibilityMarker),
                Is.EqualTo(expectedMask));
        }

        [Test]
        public void BoundedContainingVolumeWalk_ReachesSupportedCoarseRingBeyondThirdVolume()
        {
            CpuSimpleDdgiVolume[] volumes =
            [
                new(new Vector3(-2), new Vector3(2), 0.5f, 0),
                new(new Vector3(-4), new Vector3(4), 1.0f, 1),
                new(new Vector3(-8), new Vector3(8), 2.0f, 2),
                new(new Vector3(-16), new Vector3(16), 4.0f, 3)
            ];
            bool[] supported = [false, false, false, true];

            Assert.That(
                SelectFirstSupportedContainingVolume(volumes, supported, Vector3.Zero),
                Is.EqualTo(3));
        }

        [Test]
        public void AllContainingVolumesUnsupported_LeavesZeroDdgiOwnershipAndFullComplement()
        {
            float ownership = SmoothStep(0.0f, 0.15f, 0.0f);
            float environmentComplement = 1.0f - ownership;

            Assert.Multiple(() =>
            {
                Assert.That(ownership, Is.Zero);
                Assert.That(environmentComplement, Is.EqualTo(1.0f));
            });
        }

        [Test]
        public void PersistentStateRecoveryContracts_AreBoundedAndObservable()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string relocate = ReadRepoText("Njulf.Shaders", "ddgi_simple_relocate_classify.comp");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_RELOCATION_PENDING_MAX_RETRY_AGE = 32u"));
                Assert.That(relocate, Does.Contain("bool relocationTimedOut"));
                Assert.That(relocate, Does.Contain("state.activeWeight = 0.0;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_GATHER_REJECTION_COUNTER_BASE"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_GATHER_ALL_FAILED_COUNTER_BASE"));
                Assert.That(forward, Does.Contain("simpleDdgiCombinedRejectionMask"));
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
        public void RelocationClassificationMirror_StabilizesSparseEvidenceAndKeepsClipmapSupport()
        {
            CpuSimpleRayResult[] mixed =
            [
                new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.10f),
                new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.20f),
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
                new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.02f),
                new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.03f),
                new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.04f),
                new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.05f)
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
            CpuSimpleRayResult[] moderatelyInvalid =
            [
                new(Vector3.UnitX, HitKind: 1.0f, Distance: 0.05f),
                new(-Vector3.UnitX, HitKind: 1.0f, Distance: 0.05f),
                new(Vector3.UnitY, HitKind: 1.0f, Distance: 0.05f),
                new(-Vector3.UnitY, HitKind: 1.0f, Distance: 0.05f),
                new(Vector3.UnitZ, HitKind: 1.0f, Distance: 0.05f),
                new(-Vector3.UnitZ, HitKind: 1.0f, Distance: 0.05f),
                new(Vector3.UnitX, HitKind: 1.0f, Distance: 1.0f)
            ];
            CpuSimpleRelocationResult invalidBelowActiveFloorRelease = RelocateAndClassify(
                moderatelyInvalid,
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
            CpuSimpleRelocationResult deeplyEmbedded = RelocateAndClassify(
                [
                    new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.30f),
                    new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.32f),
                    new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.34f),
                    new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.36f)
                ],
                spacing: 1.0f,
                previousRelocation: Vector3.Zero,
                fresh: true);
            CpuSimpleRelocationResult conflictingDirections = RelocateAndClassify(
                [
                    new(Vector3.UnitX, HitKind: 3.0f, Distance: 0.05f),
                    new(-Vector3.UnitX, HitKind: 3.0f, Distance: 0.20f),
                    new(-Vector3.UnitX, HitKind: 3.0f, Distance: 0.22f),
                    new(-Vector3.UnitX, HitKind: 3.0f, Distance: 0.24f)
                ],
                spacing: 1.0f,
                previousRelocation: Vector3.Zero,
                fresh: true);
            CpuSimpleRelocationResult additiveRetry = RelocateAndClassify(
                stronglyInvalid,
                spacing: 1.0f,
                previousRelocation: new Vector3(0.10f, 0.0f, 0.0f),
                fresh: false);
            CpuSimpleRelocationResult distantBackfaces = RelocateAndClassify(
                [
                    new(Vector3.UnitX, HitKind: 3.0f, Distance: 2.0f),
                    new(Vector3.UnitY, HitKind: 3.0f, Distance: 2.2f),
                    new(-Vector3.UnitX, HitKind: 3.0f, Distance: 2.4f),
                    new(-Vector3.UnitY, HitKind: 3.0f, Distance: 2.6f)
                ],
                spacing: 1.0f,
                previousRelocation: Vector3.Zero,
                fresh: true);

            Assert.Multiple(() =>
            {
                Assert.That(normalUpdate.Active, Is.True);
                Assert.That(normalUpdate.Classification, Is.Zero);
                Assert.That(normalUpdate.Relocation.X, Is.EqualTo(0.03f).Within(1.0e-5f));
                Assert.That(freshUpdate.Relocation.X, Is.EqualTo(0.20f).Within(1.0e-5f));
                Assert.That(decayed.Relocation.X, Is.EqualTo(0.20f).Within(1.0e-5f));
                Assert.That(allMiss.Active, Is.True);
                Assert.That(allMiss.Classification, Is.EqualTo(0));
                Assert.That(allMiss.MissRatio, Is.EqualTo(1.0f));
                Assert.That(invalidAuthored.Active, Is.False);
                Assert.That(invalidAuthored.Classification, Is.EqualTo(1));
                Assert.That(invalidAuthored.Relocation.X, Is.EqualTo(0.12f).Within(1.0e-5f));
                Assert.That(invalidRing.Active, Is.False);
                Assert.That(invalidRing.Classification, Is.EqualTo(1));
                Assert.That(invalidBelowActiveFloorRelease.Active, Is.False);
                Assert.That(invalidBelowActiveFloorRelease.ActiveWeight, Is.Zero);
                Assert.That(invalidBelowActiveFloorRelease.Classification, Is.EqualTo(1));
                Assert.That(maintenance.ActiveWeight, Is.EqualTo(0.6f).Within(1.0e-5f));
                Assert.That(maintenance.Relocation.X, Is.EqualTo(0.2f).Within(1.0e-5f));
                Assert.That(deeplyEmbedded.Relocation.X, Is.EqualTo(0.40f).Within(1.0e-5f));
                Assert.That(deeplyEmbedded.Active, Is.False);
                Assert.That(conflictingDirections.Relocation.X, Is.EqualTo(0.15f).Within(1.0e-5f));
                Assert.That(additiveRetry.Relocation.X, Is.EqualTo(0.118f).Within(1.0e-5f));
                Assert.That(distantBackfaces.Active, Is.False);
                Assert.That(distantBackfaces.Relocation, Is.EqualTo(Vector3.Zero));
            });
        }

        [Test]
        public void VisibilityBlend_WithNoRayWeightKeepsPreviousInitializedMoments()
        {
            Vector4 previous = new(2.0f, 4.25f, 1.0f, 1.0f);
            Vector4 fresh = Vector4.Zero;

            Assert.Multiple(() =>
            {
                Assert.That(BlendVisibilityOrKeepPrevious(weightSum: 0.0f, previous, spacing: 1.25f, hysteresis: 0.97f, freshUpdate: false), Is.EqualTo(previous));
                Assert.That(BlendVisibilityOrKeepPrevious(weightSum: 0.0f, fresh, spacing: 1.25f, hysteresis: 0.0f, freshUpdate: true), Is.EqualTo(new Vector4(1.25f, 1.953125f, 1.0f, 1.0f)));
            });
        }

        [Test]
        public void RadiometricOwnership_UsesSeparateAvailabilityAndDirectionalAuthorityWithoutRenormalizingIrradiance()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("float SimpleDdgiRadiometricOwnership(SimpleDdgiGatherResult gather)"));
                Assert.That(shared, Does.Contain("float spatialCoverage = clamp(gather.spatialCoverage, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float validSupport = clamp(gather.validSupport, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float directionalSupport = clamp(gather.directionalSupport, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float availabilityAuthority = smoothstep("));
                Assert.That(shared, Does.Contain("float directionalAuthority = smoothstep("));
                Assert.That(shared, Does.Contain("return spatialCoverage * availabilityAuthority * directionalAuthority;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP"));
                Assert.That(shared, Does.Contain("float availableMass = 0.0;"));
                Assert.That(shared, Does.Contain("availableMass += dataWeight;"));
                Assert.That(shared, Does.Contain("? clamp(availableMass / spatialCoverage, 0.0, 1.0)"));
                Assert.That(shared, Does.Contain("float innerAvailableMass = inner.validSupport * inner.spatialCoverage * w;"));
                Assert.That(shared, Does.Contain("float outerAvailableMass = outer.validSupport * outer.spatialCoverage * outerWeight;"));
                Assert.That(shared, Does.Contain("result.ownership = clamp(validMass, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float radiometricOwnership = SimpleDdgiRadiometricOwnership(gather);"));
                Assert.That(shared, Does.Contain("float effectiveOwnership = radiometricOwnership * leakAttenuation;"));
                Assert.That(forward, Does.Contain("float simpleRadiometricOwnership = SimpleDdgiRadiometricOwnership(simpleGather);"));
                Assert.That(forward, Does.Contain("float simpleOwnership = simpleRadiometricOwnership * simpleLeakAttenuation;"));
                Assert.That(forward, Does.Not.Contain("float simpleOwnership = clamp(simpleGather.ownership, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("normalized independently of support"));
            });
        }

        [Test]
        public void StructuredGather_BackFacingFineCellYieldsToCoarserDirectionalSupport()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain(
                    "float selectedDirectionalAuthority = SimpleDdgiDirectionalGatherAuthority(selected);"));
                Assert.That(shared, Does.Contain(
                    "selected.ownership * edgeWeight * selectedDirectionalAuthority"));
                Assert.That(shared, Does.Contain(
                    "float selectedGatherWeight = edgeWeight * selectedDirectionalAuthority;"));
                Assert.That(shared, Does.Contain(
                    "combined.directionalSupport >= SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP"));
                Assert.That(shared, Does.Contain(
                    "float combinedDirectionalAuthority ="));
                Assert.That(shared, Does.Contain(
                    "SimpleDdgiDirectionalGatherAuthority(combined);"));
                Assert.That(shared, Does.Not.Contain(
                    "BlendSimpleDdgiGatherResults(recovery, combined, 1.0)"));
            });
        }

        [Test]
        public void SecondVolumeGather_UsesCombinedTransitionOwnershipEarlyOut()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain(
                    "float selectedTransitionOwnership ="));
                Assert.That(shared, Does.Contain(
                    "selected.ownership * edgeWeight * selectedDirectionalAuthority;"));
                Assert.That(shared, Does.Contain(
                    "selectedTransitionOwnership >= p.secondVolumeOwnershipEarlyOutThreshold"));
                Assert.That(shared, Does.Contain("selected.transitionWeight = edgeWeight;"));
                Assert.That(shared, Does.Contain(
                    "result.secondVolumeUsed = result.secondaryContributionWeight > 0.000001 ? 1.0 : 0.0;"));
                Assert.That(shared, Does.Not.Contain("if (edgeWeight >= 0.999"));
            });
        }

        [Test]
        public void BackendModeSwitch_DisablesInactiveControlHeadersWithoutRunningInactiveUpdates()
        {
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
            string simpleManager = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");
            string legacyManager = ReadRepoText("Njulf.Rendering", "Resources", "DdgiProbeVolumeManager.cs");

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain(
                    "_ddgiProbeVolumeManager.EnsureDisabled(_stagingRing, _currentCommandBuffer);"));
                Assert.That(renderer, Does.Contain(
                    "_simpleDdgiVolumeManager?.EnsureDisabled(_stagingRing, _currentCommandBuffer);"));
                Assert.That(renderer, Does.Contain("if (ddgiActive)"));
                Assert.That(renderer, Does.Contain("if (simpleDdgiActive)"));
                Assert.That(simpleManager, Does.Contain(
                    "if (_controlHeaderInitialized && !_wasSimpleDdgiEnabled)"));
                Assert.That(simpleManager, Does.Contain("DisableCore(_settings.GlobalIllumination"));
                Assert.That(simpleManager, Does.Contain("_wasSimpleDdgiEnabled = false;"));
                Assert.That(simpleManager, Does.Contain("PackHeaderWord(0u)"));
                Assert.That(legacyManager, Does.Contain(
                    "if (_controlHeaderInitialized && !_wasDdgiEnabled)"));
                Assert.That(legacyManager, Does.Contain(
                    "Upload(DdgiFrameLayout.Empty, stagingRing, commandBuffer);"));
                Assert.That(legacyManager, Does.Contain("_controlHeaderInitialized = true;"));
            });
        }

        [Test]
        public void SimpleDdgiShaderContracts_ArePresentAndAvoidLegacyConfidenceChain()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
            string transport = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport.comp");
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
                Assert.That(shared, Does.Contain("float SimpleDdgiLeakAttenuation(SimpleDdgiGatherResult gather, SimpleDdgiParams p)"));
                Assert.That(shared, Does.Contain("mix(1.0, visibilityConfidence, p.thinWallLeakClampStrength)"));
                Assert.That(shared, Does.Contain("p.thinWallLeakClampStrength = clamp(biasLimits.z, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("vec3 contributingVolumeColor;"));
                Assert.That(shared, Does.Contain("uint selectedVolume;"));
                Assert.That(shared, Does.Contain("uint validProbeCount;"));
                Assert.That(shared, Does.Contain("SimpleDdgiGatherResult SampleSimpleDdgiGather(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiProbeSupportsGather(SimpleDdgiProbeState state, vec4 irradiance, vec4 visibility)"));
                Assert.That(shared, Does.Contain("result.irradiance = result.validProbeCount > 0u && directionalMass > 0.000001"));
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
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_TRANSPORT_V2"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_UPDATE_SOURCE_REFRESH"));
                Assert.That(shared, Does.Contain("vec4 SimpleDdgiPerProbeRayRotation(uint probeIndex, vec4 frameRotation)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_VISIBILITY_SELECTION_FLOOR = 0.05"));
                Assert.That(shared, Does.Contain("float SimpleDdgiVisibilitySelectionWeight(float transportVisibility)"));
                Assert.That(shared, Does.Contain("visibility * visibility * visibility"));
                Assert.That(shared, Does.Contain("vec3 fracV = clamp(grid - baseF, vec3(0.0), vec3(1.0));"));
                Assert.That(shared, Does.Not.Contain("SimpleDdgiSmoothGridFraction"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_VISIBILITY_BROAD_MOMENT_EXPONENT = 16.0"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_VISIBILITY_HIT_CLASS_THRESHOLD = 0.35"));
                Assert.That(blend, Does.Contain("float narrowWeight = broadWeight * broadWeight;"));
                Assert.That(blend, Does.Contain("? hitMoments"));
                Assert.That(blend, Does.Contain(": missMoments;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_VISIBILITY_VARIANCE_SPACING_CAP = 4.0"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SOLVER_OWNERSHIP_SUPPORT_RAMP = 0.02"));
                Assert.That(shared, Does.Contain("clamp(gather.validSupport, 0.0, 1.0)"));
                Assert.That(shared, Does.Not.Contain("SIMPLE_DDGI_SOLVER_LEAK_FLOOR"));
                Assert.That(shared, Does.Contain("varianceSpacing * varianceSpacing * 0.005"));
                Assert.That(shared, Does.Contain("accumulated += max(irradiance.rgb, vec3(0.0)) * selectedDirectionalWeight;"));
                Assert.That(shared, Does.Not.Contain("float transportWeight = selectedDirectionalWeight * transportVisibility;"));
                Assert.That(shared, Does.Not.Contain("if (transportVisibility < 0.05)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MINIMUM_SKY_VISIBILITY = 0.10"));
                Assert.That(shared, Does.Contain("float EstimateFarFieldSkyVisibility(vec3 worldPos, vec3 surfaceNormal)"));
                Assert.That(shared, Does.Contain("vec3 traceOrigin = worldPos + safeNormal * max(safeVoxelSize, 0.03);"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SKY_VISIBILITY_SAMPLE_COUNTER"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SKY_VISIBILITY_ACCUM_COUNTER"));
                Assert.That(shared, Does.Contain("vec3 SampleSimpleDdgiUnifiedIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir, bool allowFallback)"));
                Assert.That(shared, Does.Contain("vec3 SampleSimpleDdgiSolverBounceIrradiance("));
                int solverStart = shared.IndexOf(
                    "vec3 SampleSimpleDdgiSolverBounceIrradiance(",
                    StringComparison.Ordinal);
                int solverEnd = shared.IndexOf(
                    "SimpleDdgiDebugSample SampleSimpleDdgiDebug(",
                    solverStart,
                    StringComparison.Ordinal);
                string solverSource = shared[solverStart..solverEnd];
                Assert.That(solverSource, Does.Contain("solverOwnershipOut = solverOwnership;"));
                Assert.That(solverSource, Does.Contain("fallbackWeightOut = fallbackWeight;"));
                Assert.That(solverSource, Does.Not.Contain("fallback *= EstimateFarFieldSkyVisibility"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_UPDATE_RAY_COUNT_SHIFT"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiUpdateMatchesProbeGeneration(SimpleDdgiProbeUpdate update, SimpleDdgiProbeState state)"));
                Assert.That(shared, Does.Contain("uvec3 physicalOffset;"));
                Assert.That(shared, Does.Contain("(coord + volume.physicalOffset) % max(volume.gridCount, uvec3(1u))"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_RAYS_PER_PROBE = 256u"));
                Assert.That(shared, Does.Contain("uint SimpleDdgiUpdateRayCount(SimpleDdgiProbeUpdate update, SimpleDdgiParams p)"));
                Assert.That(shared, Does.Contain("uint SimpleDdgiUpdateSourceRayCount(SimpleDdgiProbeUpdate update, SimpleDdgiParams p)"));
                Assert.That(shared, Does.Contain("state.luminanceChangeEma = uintBitsToFloat"));
                Assert.That(shared, Does.Contain("SimpleDdgiParams p, float volumeSpacing)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_GATHER_VOLUME_SAMPLES = SIMPLE_DDGI_MAX_VOLUME_COUNT"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_SELECTION_VOLUME_CHECKS = SIMPLE_DDGI_MAX_VOLUME_COUNT"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_GATHER_FALLBACK_CANDIDATE_CHECKS"));
                Assert.That(shared, Does.Contain("bool FindSimpleDdgiFallbackVolume("));
                Assert.That(shared, Does.Contain("candidateOffset <= SIMPLE_DDGI_MAX_GATHER_FALLBACK_CANDIDATE_CHECKS"));
                Assert.That(shared, Does.Contain("combined.ownership >= SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP"));
                Assert.That(shared, Does.Contain("fallbackVolumeIndex,"));
                Assert.That(shared, Does.Contain("SimpleDdgiDirectionalGatherAuthority(combined);"));
                Assert.That(shared, Does.Contain("vec3 SimpleDdgiResolveInterpolationPosition("));
                Assert.That(shared, Does.Contain("!selectedBiasOutsideSelectionDomain"));
                Assert.That(shared, Does.Contain("!SimpleDdgiContains(candidate, worldPosition)"));
                Assert.That(shared, Does.Contain("fallback *= EstimateFarFieldSkyVisibility(worldPos, safeNormal);"));
                Assert.That(forward, Does.Contain("EstimateFarFieldSkyVisibility(fragWorldPosition, geometricNormal)"));
                Assert.That(shared, Does.Contain("float fallbackWeight = (1.0 - radiometricOwnership) * p.environmentFallbackIntensity;"));
                Assert.That(shared, Does.Contain("if (fallbackWeight > 0.0001)"));
                Assert.That(shared, Does.Contain("floatBitsToUint(hysteresis.y)"));
                Assert.That(shared, Does.Contain("floatBitsToUint(hysteresis.z)"));
                Assert.That(simpleManager, Does.Contain("gi.DdgiThinWallPolicyEnabled"));
                Assert.That(simpleManager, Does.Contain("? gi.DdgiThinWallLeakClampStrength"));
                Assert.That(renderer, Does.Contain("lightSignature = HashAdd(lightSignature, gi.DdgiThinWallPolicyEnabled);"));
                Assert.That(renderer, Does.Contain("lightSignature = HashAdd(lightSignature, gi.DdgiThinWallLeakClampStrength);"));
                Assert.That(shared, Does.Contain("return packed == 0u ? fallback : min(packed - 1u, fallback);"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP = 0.15"));
                Assert.That(shared, Does.Contain("float availabilityAuthority = smoothstep("));
                Assert.That(shared, Does.Contain("return spatialCoverage * availabilityAuthority * directionalAuthority;"));
                Assert.That(shared, Does.Contain("float outerWeight = 1.0 - innerValidMass;"));
                Assert.That(shared, Does.Contain("outer.contributingVolumeColor * outerDirectionalMass"));
                Assert.That(shared, Does.Contain("inner.contributingVolumeColor * innerDirectionalMass"));
                Assert.That(shared, Does.Contain("contributorColorAccumulated / directionalMass"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_VOLUME_PRIMARY_GATHER_COUNTER_BASE + selectedVolumeIndex"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + selectedVolumeIndex"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_SECOND_VOLUME_GATHER_COUNTER"));
                Assert.That(shared, Does.Contain("DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + fallbackVolumeIndex"));
                Assert.That(shared, Does.Not.Contain("sampledVolumeCount"));
                Assert.That(shared, Does.Contain("SimpleDdgiProbeState ReadSimpleDdgiProbeState(uint bufferIndex, uint probeIndex)"));
                Assert.That(shared, Does.Contain("SimpleDdgiProbeUpdate ReadSimpleDdgiProbeUpdate(uint bufferIndex, uint queueOffset)"));
                Assert.That(shared, Does.Contain("vec3 SimpleDdgiProbeRelocatedPosition(uint probeIndex, SimpleDdgiVolume volume, uint localProbeIndex)"));
                Assert.That(shared, Does.Contain("state.classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE"));
                Assert.That(trace, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, updateProbeOffset);"));
                Assert.That(trace, Does.Contain("uint activeRayCount = SimpleDdgiUpdateRayCount(update, params);"));
                Assert.That(trace, Does.Contain("if (rayIndex >= activeRayCount)"));
                Assert.That(trace, Does.Contain("uint sourceRayCount = SimpleDdgiUpdateSourceRayCount(update, params);"));
                Assert.That(trace, Does.Contain("uint sourceRayOrdinal = min("));
                Assert.That(trace, Does.Contain("uint directionRayIndex = sourceRayOrdinal * params.raysPerProbe / sourceRayCount;"));
                Assert.That(transport, Does.Contain("vec3 bounceRadiance = vec3(0.0);"));
                Assert.That(transport, Does.Contain("vec3(params.transportAlbedoClamp)"));
                Assert.That(transport, Does.Contain("reflectedBounceRadiance = EvaluateGiDiffuseFromIrradiance("));
                Assert.That(transport, Does.Contain("transmittedBounceRadiance = EvaluateGiDiffuseFromIrradiance("));
                Assert.That(transport, Does.Contain("bounceRadiance = reflectedBounceRadiance + transmittedBounceRadiance;"));
                Assert.That(transport, Does.Not.Contain("bounceRadiance = ApplyGiMaterialOcclusion("));
                Assert.That(transport, Does.Contain("ReadSimpleDdgiTransportRayCache("));
                Assert.That(transport, Does.Contain("SampleSimpleDdgiSolverBounceIrradiance("));
                Assert.That(transport, Does.Not.Contain("SampleSimpleDdgiUnifiedIrradiance("));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_TRANSPORT_RAY_CACHE_STRIDE_WORDS = 9u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_TRANSPORT_RAY_CACHE_ABI_VERSION = 2u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_THIN_SURFACE_TRANSMISSION = 1u << 20"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_MASK = 0xffu << SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT"));
                Assert.That(shared, Does.Contain("vec3 transmittedDiffuseReflectance;"));
                Assert.That(transport, Does.Contain("hitPosition - normal * max(0.03, volume.spacing * 0.02)"));
                Assert.That(shared, Does.Contain("void MarkSimpleDdgiProbeSourceCacheInvalid("));
                Assert.That(shared, Does.Contain("void ClearSimpleDdgiProbeSourceCacheInvalid("));
                Assert.That(trace, Does.Contain("MarkSimpleDdgiProbeSourceCacheInvalid(pc.ProbeStateBufferIndex, probeIndex);"));
                Assert.That(trace, Does.Contain("ClearSimpleDdgiProbeSourceCacheInvalid(pc.ProbeStateBufferIndex, probeIndex);"));
                Assert.That(transport, Does.Contain("MarkSimpleDdgiProbeSourceCacheInvalid(pc.ProbeStateBufferIndex, update.probeIndex);"));
                Assert.That(trace, Does.Contain("vec4 rayRotation = SimpleDdgiPerProbeRayRotation(probeIndex, params.rayRotation);"));
                Assert.That(trace, Does.Contain("SimpleDdgiFibonacciDirection(directionRayIndex, params.raysPerProbe, rayRotation)"));
                Assert.That(trace, Does.Contain("if (!SimpleDdgiUpdateMatchesProbeGeneration(update, probeState))"));
                Assert.That(trace, Does.Contain("vec3 probePosition = SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) + probeState.relocation;"));
                Assert.That(trace, Does.Contain("surface.GeometricNormal * max(0.03, volume.spacing * 0.02)"));
                Assert.That(trace, Does.Contain("vec3 emissiveDiffuse = surface.EmissiveRadiance + emissiveProxyDiffuse;"));
                Assert.That(trace, Does.Not.Contain("emissiveProxyDiffuse * (1.0 - bounceOwnership)"));
                Assert.That(trace, Does.Contain("vec3 radiance = SampleDdgiEnvironmentMissRadianceWithFallback"));
                Assert.That(trace, Does.Contain("params.environmentIntensity);"));
                Assert.That(hitShading, Does.Contain("GPUEnvironmentData environment = ReadGiEnvironmentData();"));
                Assert.That(hitShading, Does.Contain("max(fallbackIntensity, 0.0) * skyWeight"));
                Assert.That(hitShading, Does.Contain("EvaluateEnvironmentRadiance("));
                Assert.That(hitShading, Does.Not.Contain("max(environment.DiffuseIntensity, 0.0)"));
                Assert.That(shared, Does.Contain("EvaluateEnvironmentTransportIrradiance(environment, safeNormal)"));
                Assert.That(forward, Does.Contain("diffuseIbl = EvaluateGiDiffuseFromIrradiance("));
                Assert.That(forward, Does.Contain("vec3 irradiance = EvaluateEnvironmentDiffuseIrradiance(environment, normal);"));
                Assert.That(simpleManager, Does.Contain("_settings.Environment.Enabled ? _settings.Environment.SkyIntensity : 0.0f"));
                Assert.That(simpleManager, Does.Contain("_transportSourceCacheRayCapacity != sourceCacheRayCapacity"));
                Assert.That(simpleManager, Does.Contain("ProbeStateSourceCacheInvalidFlag"));
                Assert.That(trace, Does.Contain("float nearTlasMaxDistance = farFieldEnabled"));
                Assert.That(trace, Does.Contain("SIMPLE_DDGI_TRACE_FLAG_COMPLETE_RAY_SCENE"));
                Assert.That(trace, Does.Contain("farFieldEnabled && !completeRayScene"));
                Assert.That(trace, Does.Contain("TraceFarFieldClipmapDetailed(probePosition, direction, nearTlasMaxDistance, maxDistance"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 8u"));
                Assert.That(shared, Does.Contain("uint PackSimpleDdgiRayVisibilityHit("));
                Assert.That(shared, Does.Contain("vec2 UnpackSimpleDdgiRayVisibilityHit("));
                Assert.That(trace, Does.Contain("bool TraceSimpleDdgiBackfaceVisibilityDistance("));
                Assert.That(trace, Does.Contain("gl_RayFlagsCullFrontFacingTrianglesEXT"));
                Assert.That(trace, Does.Not.Contain("gl_RayFlagsCullBackFacingTrianglesEXT"));
                Assert.That(trace, Does.Contain("gl_RayFlagsNoneEXT"));
                Assert.That(trace, Does.Contain("backfaceVisibilityDistance < visibilityDistance"));
                Assert.That(trace, Does.Contain("visibilityHitKind = 2.0;"));
                Assert.That(trace, Does.Contain("visibilityHitKind);"));
                Assert.That(trace, Does.Contain("PackSimpleDdgiRayVisibilityHit(visibilityDistance, hitKind)"));
                Assert.That(hitShading, Does.Contain("bool DdgiCandidatePassesTwoSidedOpacity("));
                Assert.That(transport, Does.Contain("scratchBase + 7u"));
                Assert.That(transport, Does.Contain("globalRay * SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS + 7u"));
                Assert.That(transport, Does.Contain("visibilityHit.y);"));
                Assert.That(relocate, Does.Contain("float distance = max(visibilityHit.x, 0.0);"));
                Assert.That(relocate, Does.Contain("ReadSimpleRayVisibilityHit(rayResultIndex)"));
                Assert.That(relocate, Does.Contain("UnpackSimpleDdgiRayVisibilityHit("));
                Assert.That(relocate, Does.Not.Contain("float distance = max(radianceDistance.w, 0.0);"));
                Assert.That(trace, Does.Contain("bool frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);"));
                Assert.That(trace, Does.Contain("SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE"));
                Assert.That(trace, Does.Contain("SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE"));
                Assert.That(relocate, Does.Contain("bool solidBackface ="));
                Assert.That(relocate, Does.Contain("SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE"));
                Assert.That(relocate, Does.Not.Contain("SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE"));
                Assert.That(relocate, Does.Not.Contain("if (hitKind > 1.5)"));
                Assert.That(trace, Does.Contain("!frontFace && !hitDoubleSided"));
                Assert.That(blend, Does.Contain("SimpleDdgiRayHitKindIsOneSidedBackFace(visibilityHit.y)"));
                Assert.That(transport, Does.Contain("!SimpleDdgiRayHitKindIsOneSidedBackFace(hitKind)"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiTraceEnergyCounterBase}u"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiBlendEnergyCounterBase}u"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_GATHER_REJECTION_COUNTER_BASE = {RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionCounterBase}u"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterBase}u"));
                Assert.That(shared, Does.Contain("solverOwnershipSum"));
                Assert.That(shared, Does.Contain("void RecordSimpleDdgiTraceEnergyDiagnostics("));
                Assert.That(shared, Does.Contain("void RecordSimpleDdgiBlendEnergyDiagnostics("));
                Assert.That(trace, Does.Contain("RecordSimpleDdgiTraceEnergyDiagnostics("));
                Assert.That(trace, Does.Contain("traceDirectNoShadowDiffuse"));
                Assert.That(trace, Does.Contain("traceBounceDiffuse"));
                Assert.That(trace, Does.Contain("traceSkyDiffuse"));
                Assert.That(blend, Does.Contain("RecordSimpleDdgiBlendEnergyDiagnostics("));
                Assert.That(blend, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, localProbeOffset);"));
                Assert.That(blend, Does.Contain("SimpleDdgiAdaptiveIrradianceHysteresis"));
                Assert.That(blend, Does.Contain("SimpleDdgiAdaptiveVisibilityHysteresis"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_FLAG_LIGHTING_CHANGE_ACTIVE"));
                Assert.That(blend, Does.Contain("float stepHysteresis = min(probeHysteresis, 0.60);"));
                Assert.That(blend, Does.Contain("state.luminanceChangeEma = mix"));
                Assert.That(blend, Does.Contain("shared vec4 SharedSimpleRayRadianceDistance[256];"));
                Assert.That(blend, Does.Contain("shared uint SharedSimpleRayVisibilityHit[256];"));
                Assert.That(blend, Does.Contain("vec2 ReadCachedSimpleRayVisibilityHit("));
                Assert.That(blend, Does.Contain("ReadSimpleRayVisibilityHit(rayResultIndex)"));
                Assert.That(blend, Does.Contain("UnpackSimpleDdgiRayVisibilityHit("));
                Assert.That(blend, Does.Contain("bool refreshVisibility = !transportV2Active ||"));
                Assert.That(blend, Does.Contain("SimpleDdgiUpdateRequiresSourceRefresh(update)"));
                Assert.That(blend, Does.Contain("if (refreshVisibility)"));
                Assert.That(blend, Does.Contain("void LoadSimpleRayCache(SimpleDdgiParams params, uint localProbeOffset, uint activeRayCount)"));
                Assert.That(blend, Does.Contain("barrier();"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_BLEND_FLAG_REDUCED_COMPLEXITY"));
                Assert.That(blend, Does.Contain("shared vec3 SharedSimpleShCoefficients[9];"));
                Assert.That(blend, Does.Contain("void BuildReducedSimpleDdgiIrradiance(SimpleDdgiParams params, uint localProbeOffset, uint activeRayCount)"));
                Assert.That(blend, Does.Contain("float BlendReducedIrradianceTexel("));
                Assert.That(blend, Does.Not.Contain("void BlendReducedVisibilityTexel("));
                Assert.That(blend, Does.Contain("BlendVisibilityTexel("));
                Assert.That(blend, Does.Not.Contain("SharedSimpleVisibilityWeight"));
                Assert.That(blend, Does.Contain("bool reducedComplexityEnabled = (pc.Flags & SIMPLE_DDGI_BLEND_FLAG_REDUCED_COMPLEXITY) != 0u;"));
                Assert.That(blend, Does.Contain("bool sharedRayCacheEnabled = (pc.Flags & SIMPLE_DDGI_BLEND_FLAG_SHARED_RAY_CACHE) != 0u || reducedComplexityEnabled;"));
                Assert.That(blend, Does.Contain(": SimpleDdgiCadenceAdjustedHysteresis(params, update);"));
                Assert.That(blend, Does.Contain("bool maintenanceUpdate = SimpleDdgiUpdateIsMaintenance(update);"));
                Assert.That(blend, Does.Contain("(initialState.flags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0u;"));
                Assert.That(blend, Does.Contain("initialState.flags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING"));
                Assert.That(blend, Does.Contain("Publishing the probe state is the commit point for gather."));
                Assert.That(blend, Does.Contain("memoryBarrierBuffer();"));
                Assert.That(blend, Does.Contain("state.flags &= ~SIMPLE_DDGI_PROBE_FLAG_FRESH;"));
                Assert.That(blend, Does.Contain("previous.z > 0.5 && !freshUpdate"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_BOOTSTRAP_VISIBILITY_MEAN_SPACING = 1.0"));
                Assert.That(relocate, Does.Contain("SimpleDdgiProbeState previous = ReadSimpleDdgiProbeState(pc.ProbeStateBufferIndex, probeIndex);"));
                Assert.That(relocate, Does.Contain("uint activeRayCount = SimpleDdgiUpdateRayCount(update, params);"));
                Assert.That(relocate, Does.Contain("state.luminanceChangeEma = previous.luminanceChangeEma;"));
                Assert.That(relocate, Does.Contain("float softInvalidProbeScore = max("));
                Assert.That(relocate, Does.Contain("float activeFloor = (volume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED || hardInvalidProbeScore >= 0.95) ? 0.0 : 0.35;"));
                Assert.That(relocate, Does.Contain(": hardInvalidProbeScore >= 0.75);"));
                Assert.That(relocate, Does.Not.Contain("activeWeight <= 0.05 && hardInvalidProbeScore >= 0.75"));
                Assert.That(relocate, Does.Contain("state.classification = inactiveProbe ? SIMPLE_DDGI_CLASSIFICATION_INACTIVE : SIMPLE_DDGI_CLASSIFICATION_ACTIVE;"));
                Assert.That(relocate, Does.Contain("nearestBackfaceDistance + targetSurfaceDistance"));
                Assert.That(relocate, Does.Contain("float localBackfaceRatio = backfaceRatio * backfaceProximity;"));
                Assert.That(relocate, Does.Contain("nearestBackfaceDistance <= maximumActionableBackfaceDistance"));
                Assert.That(relocate, Does.Contain("targetRelocation = previous.relocation + nearestBackfaceDirection * targetDistance;"));
                Assert.That(relocate, Does.Contain("vec3 targetRelocation = previous.relocation;"));
                Assert.That(relocate, Does.Contain("bool relocationChanged = relocationDelta > max(volume.spacing * 0.02, 0.005);"));
                Assert.That(relocate, Does.Contain("relocationWasPending && maintenanceUpdate"));
                Assert.That(relocate, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING"));
                Assert.That(relocate, Does.Not.Contain("targetSurfaceDistance - nearestDistance"));
                Assert.That(relocate, Does.Contain("WriteRelocationClassification("));
                Assert.That(relocate, Does.Contain("probeIndex,\n        blendedRelocation,"));
                Assert.That(shared, Does.Contain("const uint SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING = 1u << 3;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING) != 0u"));
                Assert.That(simpleManager, Does.Contain("ProbeStateRelocationPendingFlag = 1u << 3"));
                Assert.That(simpleManager, Does.Contain("_probeFresh[probeIndex] = 1;"));
                Assert.That(shared, Does.Not.Contain("confidence chain").IgnoreCase);
                Assert.That(shared, Does.Not.Contain("max(visibility, 0.03)"));
                Assert.That(hitShading, Does.Contain("for (uint sourceIndex = 0u; sourceIndex < sourceCount; sourceIndex++)"));
                Assert.That(hitShading, Does.Contain("ReadDdgiEmissiveSource(sourceIndex)"));
                Assert.That(hitShading, Does.Contain("vec3 dominantVisibility = TraceLightVisibility"));
                Assert.That(hitShading, Does.Contain("DDGI_SHADOW_VISIBILITY_RAY_COUNTER"));
                Assert.That(hitShading, Does.Contain("DDGI_SHADOW_VISIBILITY_OCCLUDED_COUNTER"));
                Assert.That(hitShading, Does.Contain("DDGI_SHADOW_VISIBILITY_NEAR_HIT_COUNTER"));
                Assert.That(hitShading, Does.Contain("rayQueryGetIntersectionTEXT(shadowQuery, true)"));
                Assert.That(hitShading, Does.Contain("committedHitDistance < max(receiverProbeSpacing, 0.001)"));
                Assert.That(hitShading, Does.Contain("GPUDdgiEmissiveSource firstSource = ReadDdgiEmissiveSource(0u);"));
                Assert.That(hitShading, Does.Contain("GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(selectedIndex);"));
                Assert.That(forward, Does.Contain("bool simpleDdgiConfigured = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_ENABLED) != 0u && simpleDdgiParams.probeCount > 0u;"));
                Assert.That(forward, Does.Contain("(simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) != 0u;"));
                Assert.That(forward, Does.Contain("else if (simpleDdgiConfigured)"));
                Assert.That(forward, Does.Contain("finalDiffuseIndirect = diffuseIbl * fallbackWeight * indirectAo;"));
                Assert.That(forward, Does.Contain("SimpleDdgiGatherResult simpleGather = SampleSimpleDdgiGather(fragWorldPosition, ddgiNormal, viewDirection);"));
                Assert.That(forward, Does.Contain("simpleDdgiSecondaryContributionWeight"));
                Assert.That(forward, Does.Contain("simpleDdgiSecondVolumeUsed"));
                Assert.That(forward, Does.Contain(
                    "bool primaryValid = simpleDdgiPrimaryContributionWeight > 0.000001"));
                Assert.That(forward, Does.Contain("float simpleFallback = (1.0 - simpleRadiometricOwnership) * simpleDdgiParams.environmentFallbackIntensity;"));
                Assert.That(forward, Does.Contain("simpleEnvironmentFallback *= EstimateFarFieldSkyVisibility(fragWorldPosition, ddgiNormal);"));
                Assert.That(forward, Does.Contain("EstimateFarFieldSunShadow(worldPosition, normal, normalize(-light.Direction))"));
                Assert.That(forward, Does.Contain("DDGI_INVESTIGATION_FAR_SUN_SHADOW_SAMPLE_COUNTER"));
                Assert.That(forward, Does.Not.Contain("DDGI_INVESTIGATION_ROUGH_SPECULAR_SAMPLE_COUNTER"));
                Assert.That(forward, Does.Not.Contain("SampleSimpleDdgiUnifiedIrradiance(fragWorldPosition, reflectionDirection, viewDirection, false)"));
                Assert.That(forward, Does.Contain("if (IsDdgiDebugView(debugViewMode) || DdgiForwardEstimateDiagnosticPixel())"));
                Assert.That(forward, Does.Contain("SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug(fragWorldPosition, ddgiNormal, viewDirection);"));
                Assert.That(forward, Does.Contain("bool diagnosticBiasOutsideSelectionDomain;"));
                Assert.That(forward, Does.Contain("SimpleDdgiResolveInterpolationPosition("));
                Assert.That(forward, Does.Contain("ddgiSample.visibilityMomentMean = simpleDebug.visibilityMomentMean;"));
                Assert.That(forward, Does.Contain("ddgiSample.visibilityConfidence = simpleGather.transportVisibility;"));
                Assert.That(forward, Does.Contain("AccumulateDdgiVisibilityMomentDiagnostics("));
                Assert.That(forward, Does.Contain("ddgiSample.cascadeIndex = float(simpleGather.selectedVolume);"));
                Assert.That(forward, Does.Contain("simpleDdgiContributingVolumeColor = simpleGather.contributingVolumeColor;"));
                Assert.That(forward, Does.Contain("? simpleDdgiContributingVolumeColor"));
                Assert.That(forward, Does.Contain("ddgiSample.minProbeSpacing = simpleGather.selectedSpacing;"));
                Assert.That(forward, Does.Contain("simpleIrradiance * simpleDdgiParams.indirectIntensity,"));
                Assert.That(forward, Does.Contain("diffuseReflectance),"));
                Assert.That(forward, Does.Contain("finalDiffuseIndirect = finalDdgiDiffuse + simpleEnvironmentFallback * simpleFallback * indirectAo;"));
                Assert.That(forward, Does.Not.Contain("finalDiffuseIndirect = ddgiDiffuse + diffuseIbl * indirectAo;"));
                Assert.That(blend, Does.Contain("float SimpleDdgiStableRelativeDelta"));
                Assert.That(blend, Does.Contain("if (maintenanceUpdate)"));
                Assert.That(blend, Does.Contain("if (weightSum <= 0.000001)"));
                Assert.That(blend, Does.Contain("if (previous.w > 0.0001 && !freshUpdate)"));
                Assert.That(hitShading, Does.Contain("const uint DDGI_HIT_TOP_LIGHT_LIMIT = 8u;"));
                Assert.That(hitShading, Does.Contain("const uint DDGI_HIT_LIGHT_CANDIDATE_LIMIT = 64u;"));
                Assert.That(forward, Does.Not.Contain("specularOcclusion / PI"));
                Assert.That(forward, Does.Not.Contain("max(specularIbl, specularProbeFallback)"));
                Assert.That(simpleManager, Does.Contain("CanScheduleRelocateClassifyTransaction"));
                Assert.That(simpleManager, Does.Contain("CanScheduleBlendTransaction"));
                Assert.That(simpleManager, Does.Contain("settings.SimpleDdgiStructuredGatherEnabled && structuredGatherAvailable"));
                Assert.That(simpleManager, Does.Contain("bool farFieldCoverageAvailable"));
                Assert.That(simpleManager, Does.Contain("if (farFieldCoverageAvailable)"));
                Assert.That(simplePasses, Does.Contain("!VolumeManager.CanScheduleRelocateClassifyTransaction"));
                Assert.That(simplePasses, Does.Contain("!VolumeManager.CanScheduleBlendTransaction"));
                Assert.That(simplePasses, Does.Contain("!_volumeManager.CanSchedulePublishTransaction"));
                Assert.That(simplePasses, Does.Contain("!gi.SimpleDdgiStructuredGatherEnabled"));
                Assert.That(simplePasses, Does.Contain("if (!VolumeManager.CanExecuteRelocateClassifyTransaction)"));
                Assert.That(simplePasses, Does.Contain("if (!VolumeManager.CanExecuteBlendTransaction)"));
                Assert.That(simplePasses, Does.Contain("if (!_volumeManager.CanExecutePublishTransaction)"));
                Assert.That(simplePasses, Does.Contain("SimpleDdgiPublishPass"));
                Assert.That(simplePasses, Does.Contain("BeginSampledAtlasGpuPublication(cmd)"));
                Assert.That(simplePasses, Does.Contain("MarkPublishExecuted()"));
                Assert.That(simpleManager, Does.Contain("SynchronizeSampledAtlasIfRequired(commandBuffer);"));
                Assert.That(simpleManager, Does.Contain("GpuBufferUploader.UploadRunsToBuffer("));
                Assert.That(simpleManager, Does.Contain("PackHeaderWord(_frameIndex)"));
                Assert.That(simpleManager, Does.Contain("Math.Clamp(quality.MaxShadedLights, 0, 62) + 1"));
                Assert.That(simpleManager, Does.Not.Contain("Array.Sort(_transportPublishProbeIndices"));
                Assert.That(simpleManager, Does.Contain("SampledAtlasActive ? _sampledAtlas!.LayersPerTexture : 0"));
                Assert.That(sampledAtlas, Does.Contain("ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit"));
                Assert.That(sampledAtlas, Does.Contain("FormatFeatureFlags.SampledImageFilterLinearBit"));
                Assert.That(sampledAtlas, Does.Contain("MemoryBudgetExtensionEnabled"));
                Assert.That(sampledAtlas, Does.Contain("GpuAllocator.AllocationCreateFlags.WithinBudgetBit"));
                Assert.That(sampledAtlas, Does.Contain("TransitionImagesToShaderRead(commandBuffer);"));
                Assert.That(sampledAtlas, Does.Not.Contain("CopyUpdated("));
                Assert.That(sampledAtlas, Does.Contain("MaxTextureGroups = BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups"));
                Assert.That(renderer, Does.Contain("sceneData.SimpleDdgiSampledAtlasActive == 0"));
                Assert.That(renderer, Does.Contain("FarFieldPagedFeatureEnabled = simpleDdgiRequested &&"));
                Assert.That(renderer, Does.Contain("bool farFieldCoverageReady = qualityAllowsStaticStreaming"));
                Assert.That(renderer, Does.Contain("_farFieldClipmapManager?.CoverageReady == true"));
                Assert.That(renderer, Does.Contain("? gi.GiAccelerationStructureStaticResidentDistance"));
                Assert.That(renderer, Does.Contain("gi.StreamedGiAccelerationStructuresEnabled && stats.Active ? 1 : 0"));
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
            float referenceCadence = maintenance ? 16.0f : 8.0f;
            float exponent = Math.Clamp(elapsedFrames / referenceCadence, 1.0f, 8.0f);
            return MathF.Pow(baseHysteresis, exponent);
        }

        private static (bool Pending, bool PublishAtlas, bool ResetHistory) ResolveRelocationPublication(
            bool previousPending,
            bool maintenance,
            float relocationDelta,
            float spacing)
        {
            bool changed = relocationDelta > Math.Max(spacing * 0.001f, 0.0001f);
            bool pending = changed || (previousPending && maintenance);
            bool resetHistory = previousPending && !pending;
            return (pending, !pending, resetHistory);
        }

        private static float CompositeSimpleDdgiOwnership(float innerOwnership, float outerOwnership, float innerEdgeWeight)
        {
            float innerMass = Math.Clamp(innerOwnership, 0.0f, 1.0f) * Math.Clamp(innerEdgeWeight, 0.0f, 1.0f);
            float outerMass = Math.Clamp(outerOwnership, 0.0f, 1.0f) * (1.0f - innerMass);
            return Math.Clamp(innerMass + outerMass, 0.0f, 1.0f);
        }

        private static Vector3 NormalizeVisibilityWeightedIrradiance(
            ReadOnlySpan<Vector3> irradiance,
            ReadOnlySpan<float> interpolationWeights,
            ReadOnlySpan<float> transportVisibility)
        {
            Assert.That(interpolationWeights.Length, Is.EqualTo(irradiance.Length));
            Assert.That(transportVisibility.Length, Is.EqualTo(irradiance.Length));

            Vector3 accumulated = Vector3.Zero;
            float visibleMass = 0.0f;
            for (int i = 0; i < irradiance.Length; i++)
            {
                float weight = Math.Max(interpolationWeights[i], 0.0f) *
                    SimpleDdgiVisibilitySelectionWeight(transportVisibility[i]);
                accumulated += Vector3.Max(irradiance[i], Vector3.Zero) * weight;
                visibleMass += weight;
            }

            return visibleMass > 1.0e-6f
                ? accumulated / visibleMass
                : Vector3.Zero;
        }

        private static Vector3 SimpleDdgiBiasedSamplePosition(Vector3 worldPos, Vector3 normal, Vector3 viewDir, float normalBias, float viewBias)
        {
            Vector3 safeNormal = normal.Length() > 0.00001f ? Vector3.Normalize(normal) : Vector3.UnitY;
            Vector3 safeView = viewDir.Length() > 0.00001f ? Vector3.Normalize(viewDir) : safeNormal;
            return worldPos + safeNormal * normalBias + safeView * viewBias;
        }

        private static Vector3 SimpleDdgiBoundedBiasedSamplePosition(
            Vector3 worldPos,
            Vector3 normal,
            Vector3 viewDir,
            float normalBiasScale,
            float viewBiasScale,
            float spacing,
            float maximumWorldBias,
            float architecturalThickness)
        {
            Vector3 safeNormal = normal.Length() > 0.00001f ? Vector3.Normalize(normal) : Vector3.UnitY;
            Vector3 safeView = viewDir.Length() > 0.00001f ? Vector3.Normalize(viewDir) : safeNormal;
            float safeSpacing = Math.Max(spacing, 0.001f);
            float thicknessCap = Math.Max(0.002f, architecturalThickness * 0.25f);
            float totalBiasCap = Math.Min(maximumWorldBias, thicknessCap);
            float normalCap = Math.Min(totalBiasCap, Math.Max(0.002f, safeSpacing * 0.20f));
            float normalBias = Math.Clamp(normalBiasScale * safeSpacing, 0.002f, normalCap);
            float viewCap = Math.Min(Math.Max(totalBiasCap - normalBias, 0.0f), Math.Max(0.0f, safeSpacing * 0.35f));
            float viewBias = Math.Clamp(viewBiasScale * safeSpacing, 0.0f, viewCap);
            return worldPos + safeNormal * normalBias + safeView * viewBias;
        }

        private static (Vector3 Position, bool ExitedSelectionDomain) ResolveInterpolationPosition(
            CpuSimpleDdgiVolume volume,
            Vector3 worldPosition,
            Vector3 normal,
            Vector3 viewDirection,
            float normalBias,
            float viewBias)
        {
            Vector3 biased = SimpleDdgiBiasedSamplePosition(worldPosition, normal, viewDirection, normalBias, viewBias);
            bool exited = !Contains(volume, biased);
            return (Vector3.Clamp(biased, volume.Min, volume.Max), exited);
        }

        private static Vector4 BlendVisibilityOrKeepPrevious(float weightSum, Vector4 previous, float spacing, float hysteresis, bool freshUpdate)
        {
            Vector2 moments;
            if (weightSum > 0.000001f)
                moments = new Vector2(1.0f, 1.0f);
            else if (previous.Z > 0.5f && !freshUpdate)
                return previous;
            else
            {
                float mean = spacing;
                float standardDeviation = spacing * 0.5f;
                moments = new Vector2(mean, mean * mean + standardDeviation * standardDeviation);
            }

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
                if (Math.Abs(ray.HitKind - 3.0f) < 0.25f)
                {
                    backfaceCount++;
                    Vector3 direction = Vector3.Normalize(ray.Direction);
                    if (ray.Distance < nearestBackfaceDistance)
                    {
                        nearestBackfaceDistance = ray.Distance;
                        nearestBackfaceDirection = direction;
                    }
                }
            }

            Vector3 targetRelocation = previousRelocation;
            int rayCount = Math.Max(rays.Length, 1);
            float missRatio = missCount / (float)rayCount;
            float hitRatio = hitCount / (float)rayCount;
            float backfaceRatio = backfaceCount / (float)rayCount;
            float closeRatio = closeCount / (float)rayCount;
            float backfaceProximity = nearestBackfaceDistance < float.MaxValue
                ? 1.0f - SmoothStep(spacing * 0.25f, spacing * 0.45f, nearestBackfaceDistance)
                : 0.0f;
            float localBackfaceRatio = backfaceRatio * backfaceProximity;
            float hardInvalidScore = Math.Max(
                SmoothStep(0.70f, 0.90f, closeRatio),
                SmoothStep(0.55f, 0.75f, localBackfaceRatio));
            float enclosureScore =
                SmoothStep(0.60f, 0.85f, backfaceRatio) *
                (1.0f - SmoothStep(0.0f, 0.05f, missRatio));
            hardInvalidScore = Math.Max(hardInvalidScore, enclosureScore);
            float hardInvalid = SmoothStep(0.75f, 0.95f, hardInvalidScore);
            float activeFloor = authored || hardInvalidScore >= 0.95f ? 0.0f : 0.35f;
            float targetActive = Math.Max(1.0f - hardInvalid, activeFloor);
            float stateAlpha = fresh ? 1.0f : 0.12f;
            if (targetActive > previousActive)
                stateAlpha = Math.Max(stateAlpha, 0.35f);
            float activeWeight = maintenance
                ? previousActive
                : Lerp(previousActive, targetActive, stateAlpha);

            float maximumActionableBackfaceDistance = spacing * (0.45f - 0.10f);
            if (!maintenance &&
                backfaceRatio >= 0.10f &&
                nearestBackfaceDistance <= maximumActionableBackfaceDistance)
            {
                float targetSurfaceDistance = spacing * 0.10f;
                float targetDistance = Math.Clamp(
                    nearestBackfaceDistance + targetSurfaceDistance,
                    0.0f,
                    spacing * 0.45f);
                targetRelocation = previousRelocation + nearestBackfaceDirection * targetDistance;
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

            bool inactive = !maintenance && hardInvalidScore >= 0.75f;
            if (inactive)
                activeWeight = 0.0f;
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

        private static CpuSimpleDdgiGatherPlan PlanForwardGather(
            ReadOnlySpan<CpuSimpleDdgiVolume> volumes,
            Vector3 worldPosition,
            float primaryOwnership,
            bool primaryBiasOutsideSelectionDomain,
            float ownershipThreshold)
        {
            int selectedIndex = -1;
            float edgeWeight = 0.0f;
            for (int i = 0; i < volumes.Length; i++)
            {
                if (!Contains(volumes[i], worldPosition))
                    continue;

                selectedIndex = i;
                edgeWeight = EdgeWeight(volumes[i], worldPosition);
                break;
            }

            if (selectedIndex < 0)
                return new CpuSimpleDdgiGatherPlan(0, 0, -1);

            bool requiresFallback = edgeWeight < 0.999f ||
                primaryBiasOutsideSelectionDomain ||
                primaryOwnership < ownershipThreshold;
            if (!requiresFallback)
                return new CpuSimpleDdgiGatherPlan(1, 0, -1);

            int candidateChecks = 0;
            for (int candidateIndex = selectedIndex + 1; candidateIndex < volumes.Length; candidateIndex++)
            {
                candidateChecks++;
                if (Contains(volumes[candidateIndex], worldPosition))
                {
                    return new CpuSimpleDdgiGatherPlan(
                        SampledVolumeCount: 2,
                        CandidateChecks: candidateChecks,
                        FallbackVolume: volumes[candidateIndex].VolumeIndex);
                }
            }

            return new CpuSimpleDdgiGatherPlan(1, candidateChecks, -1);
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

            float measuredVariance = Math.Max(mean2 - mean * mean, 0.0f);
            float varianceSpacing = Math.Min(Math.Max(probeSpacing, 0.0f), 4.0f);
            float spacingFloor = Math.Max(0.0005f, varianceSpacing * varianceSpacing * 0.005f);
            float meanBound = Math.Max(0.0005f, mean * mean * 0.0625f);
            float variance = Math.Max(measuredVariance, Math.Min(spacingFloor, meanBound));
            float d = receiverDistance - mean;
            return Math.Clamp(variance / (variance + d * d), 0.0f, 1.0f);
        }

        private static float SimpleDdgiVisibilitySelectionWeight(float transportVisibility) =>
            Math.Max(
                MathF.Pow(Math.Clamp(transportVisibility, 0.0f, 1.0f), 3.0f),
                0.05f);

        private static uint SimpleDdgiProbeGatherRejectionMask(
            uint flags,
            uint classification,
            float activeWeight,
            float irradianceAlpha,
            float visibilityMarker)
        {
            uint mask = 0;
            if ((flags & (1u << 0)) != 0) mask |= 1u << 0;
            if ((flags & (1u << 1)) != 0) mask |= 1u << 1;
            if ((flags & (1u << 3)) != 0) mask |= 1u << 2;
            if ((flags & (1u << 2)) != 0) mask |= 1u << 3;
            if (classification == 1u) mask |= 1u << 4;
            if (activeWeight <= 0.001f) mask |= 1u << 5;
            if (irradianceAlpha <= 0.5f) mask |= 1u << 6;
            if (visibilityMarker <= 0.5f) mask |= 1u << 7;
            return mask;
        }

        private static int SelectFirstSupportedContainingVolume(
            ReadOnlySpan<CpuSimpleDdgiVolume> volumes,
            ReadOnlySpan<bool> supported,
            Vector3 worldPosition)
        {
            Assert.That(supported.Length, Is.EqualTo(volumes.Length));
            for (int i = 0; i < volumes.Length; i++)
            {
                if (Contains(volumes[i], worldPosition) && supported[i])
                    return volumes[i].VolumeIndex;
            }
            return -1;
        }

        private static float DirectionalVisibilityMomentMean(
            ReadOnlySpan<float> directionCosines,
            ReadOnlySpan<float> distances,
            float exponent)
        {
            Assert.That(distances.Length, Is.EqualTo(directionCosines.Length));
            float weightedDistance = 0.0f;
            float weightSum = 0.0f;
            for (int i = 0; i < distances.Length; i++)
            {
                float weight = MathF.Pow(
                    Math.Clamp(directionCosines[i], 0.0f, 1.0f),
                    exponent);
                weightedDistance += Math.Max(distances[i], 0.0f) * weight;
                weightSum += weight;
            }

            return weightSum > 1.0e-6f
                ? weightedDistance / weightSum
                : 0.0f;
        }

        private static float ResolveDirectionalVisibilityMomentMean(
            ReadOnlySpan<float> directionCosines,
            ReadOnlySpan<float> distances,
            ReadOnlySpan<bool> hitMask)
        {
            Assert.That(distances.Length, Is.EqualTo(directionCosines.Length));
            Assert.That(hitMask.Length, Is.EqualTo(directionCosines.Length));
            float hitMean = 0.0f;
            float hitWeight = 0.0f;
            float missMean = 0.0f;
            float missWeight = 0.0f;
            float narrowHitWeight = 0.0f;
            float narrowWeight = 0.0f;
            for (int i = 0; i < distances.Length; i++)
            {
                float broad = MathF.Pow(
                    Math.Clamp(directionCosines[i], 0.0f, 1.0f),
                    16.0f);
                float narrow = broad * broad;
                if (hitMask[i])
                {
                    hitMean += Math.Max(distances[i], 0.0f) * broad;
                    hitWeight += broad;
                    narrowHitWeight += narrow;
                }
                else
                {
                    missMean += Math.Max(distances[i], 0.0f) * broad;
                    missWeight += broad;
                }
                narrowWeight += narrow;
            }

            float resolvedHitMean = hitWeight > 1.0e-6f ? hitMean / hitWeight : 0.0f;
            float resolvedMissMean = missWeight > 1.0e-6f ? missMean / missWeight : 0.0f;
            if (hitWeight <= 1.0e-6f)
                return resolvedMissMean;
            if (missWeight <= 1.0e-6f)
                return resolvedHitMean;
            float narrowHitFraction = narrowHitWeight / Math.Max(narrowWeight, 1.0e-6f);
            return narrowHitFraction >= 0.35f ? resolvedHitMean : resolvedMissMean;
        }

        private static float SimpleDdgiLinearGridFraction(float fraction) =>
            Math.Clamp(fraction, 0.0f, 1.0f);

        private static Vector2 SelectSimpleDdgiVisibilityHit(
            float surfaceDistance,
            float sourceHitKind,
            float backfaceDistance,
            bool backfaceHit) =>
            backfaceHit && backfaceDistance < surfaceDistance
                ? new Vector2(backfaceDistance, 2.0f)
                : new Vector2(surfaceDistance, sourceHitKind);

        private static float SimpleDdgiLeakAttenuation(
            float transportVisibility,
            float thinWallLeakClampStrength) =>
            Math.Clamp(
                Lerp(
                    1.0f,
                    SmoothStep(0.01f, 0.08f, Math.Clamp(transportVisibility, 0.0f, 1.0f)),
                    Math.Clamp(thinWallLeakClampStrength, 0.0f, 1.0f)),
                0.05f,
                1.0f);

        private static Vector3 ResolveSkyVisibilityTraceOrigin(
            Vector3 worldPosition,
            Vector3 surfaceNormal,
            float voxelSize)
        {
            Vector3 safeNormal = surfaceNormal.LengthSquared() > 1.0e-10f
                ? Vector3.Normalize(surfaceNormal)
                : Vector3.UnitY;
            return worldPosition + safeNormal * Math.Max(voxelSize, 0.03f);
        }

        private static float ApplySkyVisibilityFloor(float rawVisibility) =>
            Math.Max(Math.Clamp(rawVisibility, 0.0f, 1.0f), 0.10f);

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

        private readonly record struct CpuSimpleDdgiGatherPlan(
            int SampledVolumeCount,
            int CandidateChecks,
            int FallbackVolume);

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
