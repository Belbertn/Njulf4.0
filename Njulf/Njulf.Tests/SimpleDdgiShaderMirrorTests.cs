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
                -Vector3.UnitZ,
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
        public void SampledAtlasSeams_UseWrappedImageFetchesAndRetainCanonicalFiltering()
        {
            const uint texelsPerProbe = 8;
            Vector3 seamDirection = SimpleDdgiOctDecode(new Vector2(0.01f, 0.50f));
            Vector4 canonical = SampleSyntheticAtlasBilinear(
                seamDirection,
                texelsPerProbe);
            Vector4 clampedImage = SampleSyntheticAtlasImageBilinear(
                seamDirection,
                texelsPerProbe);
            string shared = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(
                    IsInteriorAtlasQuad(seamDirection, texelsPerProbe),
                    Is.False);
                Assert.That(
                    Vector4.Distance(canonical, clampedImage),
                    Is.GreaterThan(1.0e-4f),
                    "Sampler clamping is not the canonical octahedral seam rule.");
                Assert.That(
                    shared,
                    Does.Contain(
                        "SampleSimpleDdgiAtlasImageWrappedBilinearAtAddress("));
                Assert.That(
                    shared,
                    Does.Contain(
                        "SimpleDdgiMirrorOctTexelIndex(coord, texelsPerProbe)"));
                Assert.That(
                    shared,
                    Does.Contain(
                        "return interior\n            ? SampleSimpleDdgiAtlasImageAtAddress("));
            });
        }

        [Test]
        public void OctEncode_NegativePoleDoesNotAliasPositivePole()
        {
            Vector2 positivePole = SimpleDdgiOctEncode(Vector3.UnitZ);
            Vector2 negativePole = SimpleDdgiOctEncode(-Vector3.UnitZ);

            Assert.Multiple(() =>
            {
                Assert.That(positivePole, Is.EqualTo(new Vector2(0.5f, 0.5f)));
                Assert.That(negativePole, Is.EqualTo(Vector2.One));
                Assert.That(Vector2.Distance(positivePole, negativePole),
                    Is.GreaterThan(0.7f));
                Assert.That(Vector3.Dot(
                        SimpleDdgiOctDecode(negativePole),
                        -Vector3.UnitZ),
                    Is.GreaterThan(0.999f));
            });
        }

        [Test]
        public void OpaqueReceiverCache_IsFrameLocalDepthAwareAndReadOnly()
        {
            string cache = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_receiver_cache.comp");
            string resolve = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_receiver_cache_resolve.comp");
            string cacheSampling = ReadRepoText(
                "Njulf.Shaders",
                "forward_ddgi_receiver_cache.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string pass = ReadRepoText(
                "Njulf.Rendering",
                "Pipeline",
                "ForwardPlusPass.cs");
            string meshPipeline = ReadRepoText(
                "Njulf.Rendering",
                "Pipeline",
                "PipelineObjects",
                "MeshPipeline.cs");
            string shaderProject = ReadRepoText(
                "Njulf.Shaders",
                "Njulf.Shaders.csproj");

            Assert.Multiple(() =>
            {
                Assert.That(cache, Does.Contain(
                    "#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE false"));
                Assert.That(cache, Does.Contain(
                    "#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 0"));
                Assert.That(cache, Does.Contain(
                    "if (!found || candidateDepth > closestDepth)"));
                Assert.That(cache, Does.Contain(
                    "SimpleDdgiGatherResult gather = SampleSimpleDdgiGather("));
                Assert.That(cache, Does.Contain(
                    "weightedIrradiance.z,"));
                Assert.That(cache, Does.Contain(
                    "weightedEnvironmentFallback"));
                Assert.That(cache, Does.Contain(
                    "farFieldSkyVisibility = EstimateFarFieldSkyVisibility("));
                Assert.That(cacheSampling, Does.Contain(
                    "layout(std430, set = 2, binding = 0) readonly buffer ForwardDdgiReceiverCacheBlock"));
                Assert.That(cacheSampling, Does.Contain(
                    "uvec4 packed = ForwardDdgiReceiverCache.Entries[entryIndex]"));
                Assert.That(cacheSampling, Does.Contain(
                    "FORWARD_DDGI_RECEIVER_CACHE_SCALE = 2u"));
                Assert.That(cacheSampling, Does.Contain(
                    "uvec2(fragmentCoordinate) >> uvec2(1u)"));
                Assert.That(cacheSampling, Does.Contain(
                    "cacheCoordinate.y * cacheWidth"));
                Assert.That(resolve, Does.Contain(
                    "layout(std430, set = 2, binding = 0) writeonly buffer ReceiverCacheOutputBlock"));
                Assert.That(resolve, Does.Contain(
                    "depthCode = packed.w"));
                Assert.That(cache, Does.Contain(
                    "EncodeReceiverCacheDepth(receiverDepth)"));
                Assert.That(cache, Does.Contain(
                    "receiverDepth"));
                Assert.That(resolve, Does.Contain(
                    "candidateDepthCode > bestDepthCode"));
                Assert.That(resolve, Does.Contain(
                    "ReceiverCacheOutput.Entries[entryIndex] = uvec4("));
                Assert.That(resolve, Does.Contain(
                    "resolved.DdgiIrradiance *= RECEIVER_CACHE_INV_PI"));
                Assert.That(resolve, Does.Contain(
                    "resolved.EnvironmentIrradiance *= RECEIVER_CACHE_INV_PI"));
                Assert.That(resolve, Does.Contain(
                    "resolvedCoord.y * pc.CacheWidth"));
                Assert.That(resolve, Does.Contain(
                    "TryLoadGatherSample("));
                Assert.That(resolve, Does.Contain(
                    "ReceiverCacheGatherSample ResolveGatherSample(ivec2 requestedCoord)"));
                Assert.That(resolve, Does.Contain(
                    "vec2 latticePosition ="));
                Assert.That(resolve, Does.Not.Contain(
                    "WriteStorageWordUniform("));
                Assert.That(cache, Does.Contain(
                    "for (uint sampleY = 0u; sampleY < scale; sampleY++)"));
                Assert.That(cache, Does.Contain(
                    "for (uint sampleX = 0u; sampleX < scale; sampleX++)"));
                Assert.That(forward, Does.Contain(
                    "cachedGather.DdgiIrradiance * ambientOcclusion"));
                Assert.That(forward, Does.Contain(
                    "cachedGather.EnvironmentIrradiance * indirectAo"));
                Assert.That(forward, Does.Contain(
                    "SampleForwardDdgiReceiverCache("));
                Assert.That(cache, Does.Contain(
                    "EvaluateEnvironmentDiffuseIrradiance(environment, normal)"));
                Assert.That(forward, Does.Contain(
                    "#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE"));
                Assert.That(forward, Does.Contain(
                    "#include \"forward_ddgi_receiver_cache.glsl\""));
                Assert.That(cacheSampling, Does.Not.Contain(
                    "ReadStorageAlignedUVec4Uniform("));
                Assert.That(pass, Does.Contain(
                    "private const int FramesInFlight = 2;"));
                Assert.That(pass, Does.Contain(
                    "SimpleDdgiReceiverGatherScale ="));
                Assert.That(pass, Does.Contain(
                    "SimpleDdgiReceiverFeedbackCaptureSourceAbi.SurfaceTileScale;"));
                Assert.That(
                    Njulf.Rendering.Data.SimpleDdgiReceiverFeedbackCaptureSourceAbi
                        .SurfaceTileScale,
                    Is.EqualTo(12u));
                Assert.That(pass, Does.Contain(
                    "SimpleDdgiReceiverCacheScale = 2u"));
                Assert.That(pass, Does.Contain(
                    "SimpleDdgiReceiverCacheEntryBytes = 16u"));
                Assert.That(pass, Does.Contain(
                    "PackSimpleDdgiReceiverCacheResolveDimensions("));
                Assert.That(pass, Does.Contain(
                    "SimpleDdgiReceiverGatherEntryBytes = 16u"));
                Assert.That(pass, Does.Contain(
                    "DstStageMask = PipelineStageFlags2.FragmentShaderBit"));
                Assert.That(pass, Does.Contain(
                    "DstStageMask = PipelineStageFlags2.ComputeShaderBit"));
                Assert.That(pass, Does.Contain(
                    "ddgi_simple_receiver_cache_resolve.comp.spv"));
                Assert.That(pass, Does.Contain(
                    "DescriptorType = DescriptorType.StorageBuffer"));
                Assert.That(pass, Does.Not.Contain(
                    "FormatFeatureFlags.SampledImageFilterLinearBit"));
                Assert.That(pass, Does.Contain(
                    "BufferUsageFlags.StorageBufferBit"));
                Assert.That(pass, Does.Contain(
                    "private readonly BufferHandle[] _simpleDdgiReceiverCacheBuffers"));
                Assert.That(pass, Does.Contain(
                    "_simpleDdgiReceiverCacheConsumerSets[frameIndex]"));
                Assert.That(pass, Does.Contain(
                    "BindSimpleDdgiReceiverCacheBuffer(cmd, frameIndex)"));
                Assert.That(meshPipeline, Does.Contain(
                    "ForwardReceiverCacheBufferSetLayout"));
                Assert.That(meshPipeline, Does.Contain(
                    "SetLayoutCount = 3"));
                Assert.That(shaderProject, Does.Contain(
                    "-DFORWARD_OPAQUE=1 -DFORWARD_DDGI_RECEIVER_CACHE=1"));
                Assert.That(shaderProject, Does.Contain(
                    "-DFORWARD_SIMPLE_OPAQUE=1 -DFORWARD_SIMPLE_VERTEX_INPUT=1 -DFORWARD_DDGI_RECEIVER_CACHE=1"));
                Assert.That(shaderProject, Does.Contain(
                    "forward_opaque_simple_full_input_ddgi_cache_required.frag.spv"));
            });
        }

        [TestCase(1920u, 1080u, 2u, 2u)]
        [TestCase(1921u, 1081u, 1u, 1u)]
        [TestCase(1u, 1u, 1u, 1u)]
        public void ReceiverCachePackedResolveDimensions_PreservePartialEdgeExtent(
            uint width,
            uint height,
            uint expectedLastBlockWidth,
            uint expectedLastBlockHeight)
        {
            uint packed =
                Njulf.Rendering.Pipeline.ForwardPlusPass
                    .PackSimpleDdgiReceiverCacheResolveDimensions(
                        new Silk.NET.Vulkan.Extent2D(width, height));

            Assert.Multiple(() =>
            {
                Assert.That(packed & 0xffu, Is.EqualTo(12u));
                Assert.That((packed >> 8) & 0xffu, Is.EqualTo(2u));
                Assert.That(
                    (packed >> 16) & 0xffu,
                    Is.EqualTo(expectedLastBlockWidth));
                Assert.That(
                    (packed >> 24) & 0xffu,
                    Is.EqualTo(expectedLastBlockHeight));
            });
        }

        [Test]
        public void DirectionalWeight_UsesWrapShadingOffsetToAvoidProbeCentredLobes()
        {
            float facing = SimpleDdgiBackfaceWeight(surfaceNormalDotMinusProbeDirection: 1.0f);
            float perpendicular = SimpleDdgiBackfaceWeight(surfaceNormalDotMinusProbeDirection: 0.0f);
            float backFacing = SimpleDdgiBackfaceWeight(surfaceNormalDotMinusProbeDirection: -1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(facing, Is.EqualTo(1.0f));
                Assert.That(perpendicular, Is.EqualTo(0.375f).Within(1.0e-6f));
                Assert.That(backFacing, Is.EqualTo(1.0f / 6.0f).Within(1.0e-6f));
                Assert.That(facing / backFacing, Is.EqualTo(6.0f).Within(1.0e-5f),
                    "Directional selection must stay bounded so one coarse probe cannot stamp a radial lobe into a planar receiver.");
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
                Assert.That(shared, Does.Contain("const uint SIMPLE_DDGI_HEADER_WORDS = 64u;"));
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
        public void ForwardGather_UsesBoundedSimpleDdgiSelection()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("const uint SIMPLE_DDGI_MAX_VOLUME_COUNT = 16u"));
                Assert.That(shared, Does.Contain("const uint SIMPLE_DDGI_MAX_SELECTION_VOLUME_CHECKS"));
                Assert.That(shared, Does.Contain("bool FindSimpleDdgiFallbackVolume("));
                Assert.That(shared, Does.Contain("SimpleDdgiGatherResult SampleSimpleDdgiGather("));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_RECOVERY_GATHER_SAMPLES = 2u"));
                Assert.That(shared, Does.Not.Contain("SIMPLE_DDGI_FORWARD_TILE_CANDIDATES"));
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
        public void RadiometricOwnership_UsesAvailabilityWithoutDirectionalLatticeAttenuation()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("float SimpleDdgiRadiometricOwnership(SimpleDdgiGatherResult gather)"));
                Assert.That(shared, Does.Contain("float spatialCoverage = clamp(gather.spatialCoverage, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float validSupport = clamp(gather.validSupport, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float availabilityAuthority = smoothstep("));
                Assert.That(shared, Does.Contain("return spatialCoverage * availabilityAuthority;"));
                Assert.That(shared, Does.Not.Contain("float SimpleDdgiDirectionalGatherAuthority("));
                Assert.That(shared, Does.Not.Contain("SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP"));
                Assert.That(shared, Does.Contain("float availableMass = 0.0;"));
                Assert.That(shared, Does.Contain("availableMass += dataWeight;"));
                Assert.That(shared, Does.Contain("? clamp(availableMass / spatialCoverage, 0.0, 1.0)"));
                Assert.That(shared, Does.Contain("float innerAvailableMass = inner.validSupport * inner.spatialCoverage * w;"));
                Assert.That(shared, Does.Contain("float outerAvailableMass = outer.validSupport * outer.spatialCoverage * outerWeight;"));
                Assert.That(shared, Does.Contain("result.ownership = clamp(availableMass, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("float radiometricOwnership = SimpleDdgiRadiometricOwnership(gather);"));
                Assert.That(shared, Does.Contain("float effectiveOwnership = radiometricOwnership * leakAttenuation;"));
                Assert.That(forward, Does.Contain("SimpleDdgiRadiometricOwnership(simpleGather);"));
                Assert.That(forward, Does.Contain("float simpleOwnership = simpleRadiometricOwnership * simpleLeakAttenuation;"));
                Assert.That(forward, Does.Not.Contain("float simpleOwnership = clamp(simpleGather.ownership, 0.0, 1.0);"));
                Assert.That(shared, Does.Contain("normalized independently of support"));
            });
        }

        [Test]
        public void StructuredGather_DirectionalSupportDoesNotOpenARepresentedCoarseRing()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain(
                    "SimpleDdgiRadiometricOwnership(selected) * edgeWeight;"));
                Assert.That(shared, Does.Contain(
                    "float selectedGatherWeight = edgeWeight;"));
                Assert.That(shared, Does.Contain(
                    "if (combined.ownership >= SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP)"));
                Assert.That(shared, Does.Match(
                    @"BlendSimpleDdgiGatherResults\(\s*recovery,\s*combined,\s*1\.0\);"));
                Assert.That(shared, Does.Not.Contain("selectedDirectionalSupportUsable"));
                Assert.That(shared, Does.Not.Contain("selectedDirectionalAuthority"));
                Assert.That(shared, Does.Not.Contain("combinedDirectionalAuthority"));
            });
        }

        [Test]
        public void SecondVolumeGather_UsesReceiverAvailabilityAuthorityAndPreservesEdgeTransitions()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain(
                    "SimpleDdgiRadiometricOwnership(selected) * edgeWeight;"));
                Assert.That(shared, Does.Contain(
                    "1.0 - selectedTransitionOwnership <= 0.00001"));
                Assert.That(shared, Does.Contain("selected.transitionWeight = edgeWeight;"));
                Assert.That(shared, Does.Contain(
                    "result.secondVolumeUsed = result.secondaryContributionWeight > 0.000001 ? 1.0 : 0.0;"));
                Assert.That(shared, Does.Not.Contain("if (edgeWeight >= 0.999"));
            });
        }

        [Test]
        public void BackendPreparation_UsesOnlySimpleDdgi()
        {
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
            string simpleManager = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain(
                    "_simpleDdgiVolumeManager?.EnsureDisabled(_stagingRing, _currentCommandBuffer);"));
                Assert.That(renderer, Does.Contain("bool simpleDdgiActive ="));
                Assert.That(simpleManager, Does.Contain(
                    "if (_controlHeaderInitialized && !_wasSimpleDdgiEnabled)"));
                Assert.That(simpleManager, Does.Contain("DisableCore(_settings.GlobalIllumination"));
                Assert.That(simpleManager, Does.Contain("_wasSimpleDdgiEnabled = false;"));
                Assert.That(simpleManager, Does.Contain("PackHeaderWord(0u)"));
            });
        }

        [Test]
        public void StructuredGather_OccludedFineFieldDoesNotBecomeCoarseCoverageHole()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");

            CpuSimpleDdgiCascadeBlend blend = BlendCascadeAvailability(
                innerSpatialCoverage: 1.0f,
                innerValidSupport: 1.0f,
                innerDirectionalSupport: 0.60f,
                innerTransitionWeight: 0.75f,
                outerSpatialCoverage: 1.0f,
                outerValidSupport: 1.0f,
                outerDirectionalSupport: 0.80f);
            CpuSimpleDdgiCascadeBlend fullyRepresentedLowDirectional = BlendCascadeAvailability(
                innerSpatialCoverage: 1.0f,
                innerValidSupport: 1.0f,
                innerDirectionalSupport: 0.001f,
                innerTransitionWeight: 1.0f,
                outerSpatialCoverage: 1.0f,
                outerValidSupport: 1.0f,
                outerDirectionalSupport: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(blend.InnerAvailableMass, Is.EqualTo(0.75f).Within(1.0e-6f));
                Assert.That(blend.OuterAvailableMass, Is.EqualTo(0.25f).Within(1.0e-6f));
                Assert.That(blend.InnerRadiometricMass, Is.EqualTo(0.75f).Within(1.0e-6f));
                Assert.That(blend.OuterRadiometricMass, Is.EqualTo(0.25f).Within(1.0e-6f));
                Assert.That(blend.DirectionalSupport, Is.EqualTo(0.65f).Within(1.0e-6f));
                Assert.That(fullyRepresentedLowDirectional.InnerRadiometricMass, Is.EqualTo(1.0f).Within(1.0e-6f));
                Assert.That(fullyRepresentedLowDirectional.OuterRadiometricMass, Is.Zero.Within(1.0e-6f));
                Assert.That(fullyRepresentedLowDirectional.DirectionalSupport, Is.EqualTo(0.001f).Within(1.0e-6f));
                Assert.That(shared, Does.Contain("geometricDirectionalMass += directionalTransportWeight;"));
                Assert.That(shared, Does.Contain(
                    "? clamp(geometricDirectionalMass / availableMass, 0.0, 1.0)"));
                Assert.That(shared, Does.Contain(
                    "float innerAvailableMass = inner.validSupport * inner.spatialCoverage * w;"));
                Assert.That(shared, Does.Contain(
                    "float outerAvailableMass = outer.validSupport * outer.spatialCoverage * outerWeight;"));
                Assert.That(shared, Does.Contain(
                    "vec3 accumulated = outer.irradiance * outerAvailableMass +"));
                Assert.That(shared, Does.Contain(
                    "inner.irradiance * innerAvailableMass;"));
                Assert.That(shared, Does.Contain(
                    "? clamp(accumulated / availableMass, vec3(0.0), vec3(64.0))"));
                Assert.That(shared, Does.Not.Contain("outer.irradiance * outerDirectionalMass"));
                Assert.That(shared, Does.Not.Contain("float innerValidMass = inner.ownership * w;"));
                Assert.That(shared, Does.Not.Contain("float outerValidMass = outer.ownership * outerWeight;"));
            });
        }

        [Test]
        public void VariableStrideRayRecords_UseArbitraryWordVectorReads()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            int scratchStart = shared.IndexOf(
                "bool ReadSimpleDdgiRayResultStorage(",
                StringComparison.Ordinal);
            int scratchEnd = shared.IndexOf(
                "void WriteSimpleDdgiTransportRayCache(",
                scratchStart,
                StringComparison.Ordinal);
            int cacheStart = shared.IndexOf(
                "bool ReadSimpleDdgiTransportRayCache(",
                StringComparison.Ordinal);
            int cacheEnd = shared.IndexOf(
                "bool SimpleDdgiTransportRayCacheIsHit(",
                cacheStart,
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(scratchStart, Is.GreaterThanOrEqualTo(0));
                Assert.That(scratchEnd, Is.GreaterThan(scratchStart));
                Assert.That(cacheStart, Is.GreaterThanOrEqualTo(0));
                Assert.That(cacheEnd, Is.GreaterThan(cacheStart));
            });

            string scratchReader = shared[scratchStart..scratchEnd];
            string cacheReader = shared[cacheStart..cacheEnd];
            Assert.Multiple(() =>
            {
                Assert.That(scratchReader,
                    Does.Contain("radianceDistance = ReadStorageVec4Uniform"));
                Assert.That(scratchReader,
                    Does.Not.Contain("ReadStorageAlignedVec4Uniform"));
                Assert.That(cacheReader,
                    Does.Contain("? ReadStorageVec4Uniform(bufferIndex, baseWord)"));
                Assert.That(cacheReader,
                    Does.Not.Contain("ReadStorageAlignedVec4Uniform"));
            });
        }

        [Test]
        public void SourceCacheRadianceDiagnostic_IsNotCompiledIntoReceiverFragmentShader()
        {
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(
                    forward,
                    Does.Not.Contain("#define NJULF_SIMPLE_DDGI_SOURCE_CACHE_DIAGNOSTIC 1"));
                Assert.That(
                    forward,
                    Does.Contain("#include \"ddgi_simple_shared.glsl\""));
            });
        }

        [Test]
        public void StorageValidationTelemetry_UsesDedicatedBankAtConsumerBoundaries()
        {
            string common = ReadRepoText("Njulf.Shaders", "common.glsl");
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
            string transport = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport.comp");
            string audit = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport_audit.comp");
            int legacyReaderStart = shared.IndexOf(
                "bool ReadSimpleDdgiLegacyTransportRayCacheForSolve(",
                StringComparison.Ordinal);
            int packedReaderStart = shared.IndexOf(
                "bool ReadSimpleDdgiPackedTransportRayCacheForSolve(",
                legacyReaderStart,
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(common, Does.Contain(
                    "SIMPLE_DDGI_STORAGE_VALIDATION_BUFFER_BASE_INDEX"));
                Assert.That(common, Does.Contain(
                    "uint physicalCounterIndex = logicalCounterIndex -"));
                Assert.That(common, Does.Contain(
                    "BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[physicalCounterIndex]"));
                Assert.That(shared, Does.Contain(
                    "AddSimpleDdgiStorageValidationDiagnostic("));
                Assert.That(shared, Does.Contain(
                    "MaxSimpleDdgiStorageValidationDiagnostic("));
                Assert.That(legacyReaderStart, Is.GreaterThanOrEqualTo(0));
                Assert.That(packedReaderStart, Is.GreaterThan(legacyReaderStart));
                Assert.That(shared[legacyReaderStart..packedReaderStart],
                    Does.Not.Contain("RecordSimpleDdgiDirectionComparison("));
                Assert.That(trace, Does.Contain(
                    "RecordSimpleDdgiDirectionComparison(\n                    params,\n                    pc.CurrentFrameIndex,"));
                Assert.That(transport, Does.Contain(
                    "RecordSimpleDdgiDirectionComparison(\n        params,\n        pc.CurrentFrameIndex,"));
                Assert.That(audit, Does.Contain(
                    "RecordSimpleDdgiDirectionComparison(\n        auditParams,\n        diagnosticFrameIndex,"));
                Assert.That(transport, Does.Not.Contain("Temporary A/B probe"));
                Assert.That(transport, Does.Not.Contain(
                    "SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_MISS_COUNTER"));
            });
        }

        [Test]
        public void SimpleDdgiShaderContracts_ArePresentAndAvoidLegacyConfidenceChain()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
            string transport = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport.comp");
            string transportOperator = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport_operator.glsl");
            string blend = ReadRepoText("Njulf.Shaders", "ddgi_simple_blend.comp");
            string relocate = ReadRepoText("Njulf.Shaders", "ddgi_simple_relocate_classify.comp");
            string hitShading = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string simplePasses = ReadRepoText("Njulf.Rendering", "Pipeline", "SimpleDdgiPasses.cs");
            string simpleManager = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");
            string sampledAtlas = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiSampledAtlas.cs");
            string sampledPublish = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_publish_sampled.comp");
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
            string storageAbi = ReadRepoText("Njulf.Shaders", "ddgi_simple_storage_abi.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("vec3 SampleSimpleDdgiIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("struct SimpleDdgiDebugSample"));
                Assert.That(shared, Does.Contain("SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)"));
                Assert.That(shared, Does.Contain("SimpleDdgiVolume ReadSimpleDdgiVolume(uint bufferIndex, uint volumeIndex)"));
                Assert.That(shared, Does.Contain("bool SelectSimpleDdgiVolume("));
                Assert.That(shared, Does.Contain("out bool refinementOrBaseFallback)"));
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
                Assert.That(shared, Does.Contain("bool SimpleDdgiProbeSupportsGather(SimpleDdgiProbeState state, vec4 irradiance)"));
                Assert.That(shared, Does.Contain("(state.flags & SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID) != 0u"));
                Assert.That(shared, Does.Contain("result.irradiance = directionalMass > 0.000001"));
                Assert.That(shared, Does.Contain("accumulated / directionalMass"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiCanSampleAtlasImageAtAddress("));
                Assert.That(shared, Does.Contain("bool SimpleDdgiMirrorPayloadOrMappingDeclared("));
                Assert.That(shared, Does.Contain("return compactFirstLayerPlusOne != 0u ||"));
                Assert.That(shared, Does.Contain("address.sampledStatusFlags = volume.cacheLayoutFlags &"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_ATLAS_ADDRESS_LAYER_BASE_DECLARED_BIT"));
                Assert.That(shared, Does.Contain("bool completeMirrorPayloadPair = mirrorPayloadBits =="));
                Assert.That(shared, Does.Contain("vec4 SampleSimpleDdgiAtlasImageAtAddress("));
                Assert.That(shared, Does.Contain("vec4 SampleSimpleDdgiAtlasImageWrappedBilinearAtAddress("));
                Assert.That(shared, Does.Contain("vec4 SampleSimpleDdgiIrradianceBilinearAtAddress("));
                Assert.That(shared, Does.Contain("vec2 SampleSimpleDdgiVisibilityBilinearAtAddress("));
                Assert.That(shared, Does.Contain("SimpleDdgiMirrorOctTexelIndex(base"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_WRAP_SHADING_OFFSET = 0.20"));
                Assert.That(shared, Does.Contain("(halfLambert * halfLambert + SIMPLE_DDGI_WRAP_SHADING_OFFSET)"));
                Assert.That(shared, Does.Contain("vec2 SimpleDdgiSignNotZero(vec2 value)"));
                Assert.That(shared, Does.Contain("SimpleDdgiSignNotZero(encoded)"));
                Assert.That(shared, Does.Not.Contain("sign(encoded.xy)"));
                Assert.That(shared, Does.Contain("float selectedDirectionalWeight = directionalTransportWeight *"));
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
                Assert.That(shared, Does.Contain("float EstimateFarFieldSkyVisibility("));
                Assert.That(shared, Does.Contain("uint diagnosticSampleWeight"));
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
                Assert.That(shared, Does.Contain("sourceRayCount >= 256u"));
                Assert.That(shared, Does.Contain("encodedSourceRayCount == 0u"));
                Assert.That(shared, Does.Contain("state.luminanceChangeEma = uintBitsToFloat"));
                Assert.That(shared, Does.Contain("SimpleDdgiParams p, float volumeSpacing)"));
                Assert.That(shared, Does.Contain("recoverySample < SIMPLE_DDGI_MAX_RECOVERY_GATHER_SAMPLES"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_SELECTION_VOLUME_CHECKS = SIMPLE_DDGI_MAX_VOLUME_COUNT"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_MAX_GATHER_FALLBACK_CANDIDATE_CHECKS"));
                Assert.That(shared, Does.Contain("bool FindSimpleDdgiFallbackVolume("));
                Assert.That(shared, Does.Contain("checkedCandidates < SIMPLE_DDGI_MAX_GATHER_FALLBACK_CANDIDATE_CHECKS"));
                Assert.That(shared, Does.Contain("combined.ownership >= SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP"));
                Assert.That(shared, Does.Contain("fallbackVolumeIndex,"));
                Assert.That(shared, Does.Match(
                    @"BlendSimpleDdgiGatherResults\(\s*recovery,\s*combined,\s*1\.0\);"));
                Assert.That(shared, Does.Contain("vec3 SimpleDdgiResolveInterpolationPosition("));
                Assert.That(shared, Does.Contain("!selectedBiasOutsideSelectionDomain"));
                Assert.That(shared, Does.Contain("!SimpleDdgiContains(candidate, worldPosition)"));
                Assert.That(shared, Does.Contain("fallback *= EstimateFarFieldSkyVisibility(worldPos, safeNormal, p, 1u);"));
                Assert.That(forward, Does.Contain("DdgiSparseDiagnosticSampleWeight()"));
                Assert.That(shared, Does.Contain("float fallbackWeight = (1.0 - radiometricOwnership) * p.environmentFallbackIntensity;"));
                Assert.That(shared, Does.Contain("if (fallbackWeight > SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT)"));
                Assert.That(shared, Does.Contain("floatBitsToUint(hysteresis.y)"));
                Assert.That(shared, Does.Contain("floatBitsToUint(hysteresis.z)"));
                Assert.That(simpleManager, Does.Contain("gi.DdgiThinWallPolicyEnabled"));
                Assert.That(simpleManager, Does.Contain("? gi.DdgiThinWallLeakClampStrength"));
                Assert.That(renderer, Does.Contain("sourcePolicySignature = HashAdd(sourcePolicySignature, gi.DdgiThinWallPolicyEnabled);"));
                Assert.That(renderer, Does.Contain("sourcePolicySignature = HashAdd(sourcePolicySignature, gi.DdgiThinWallLeakClampStrength);"));
                Assert.That(shared, Does.Contain("return packed == 0u ? fallback : min(packed - 1u, fallback);"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP = 0.15"));
                Assert.That(shared, Does.Contain("float availabilityAuthority = smoothstep("));
                Assert.That(shared, Does.Contain("return spatialCoverage * availabilityAuthority;"));
                Assert.That(shared, Does.Contain("float outerWeight = 1.0 - innerAvailableMass;"));
                Assert.That(shared, Does.Contain("outer.contributingVolumeColor * outerAvailableMass"));
                Assert.That(shared, Does.Contain("inner.contributingVolumeColor * innerAvailableMass"));
                Assert.That(shared, Does.Contain("contributorColorAccumulated / availableMass"));
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
                Assert.That(trace, Does.Contain("bool gpuScheduler = SimpleDdgiGpuSchedulerActive(pc.SchedulerArenaBufferIndex);"));
                Assert.That(trace, Does.Contain("uint updateProbeOffset = (gpuScheduler ? schedulerQueueOffset : pc.DispatchQueueOffset) +"));
                Assert.That(trace, Does.Contain("if (!gpuScheduler && updateProbeOffset >= params.probesToUpdate)"));
                Assert.That(trace, Does.Contain("uint globalRay = updateProbeOffset * params.raysPerProbe + rayIndex;"));
                Assert.That(trace, Does.Contain("uint activeRayCount = SimpleDdgiUpdateRayCount(update, params);"));
                Assert.That(trace, Does.Contain("if (rayIndex >= activeRayCount)"));
                Assert.That(trace, Does.Contain("uint sourceRayCount = SimpleDdgiUpdateSourceRayCount(update, params);"));
                Assert.That(trace, Does.Contain("uint directionRayIndex = SimpleDdgiUpdateDirectionRayIndex("));
                Assert.That(transportOperator, Does.Contain("reflectedBounceRadiance = vec3(0.0);"));
                Assert.That(transportOperator, Does.Contain("float q = params.transportAlbedoClamp;"));
                Assert.That(transportOperator, Does.Contain("reflectedBounceRadiance = EvaluateGiDiffuseFromIrradiance("));
                Assert.That(transportOperator, Does.Contain("transmittedBounceRadiance = EvaluateGiDiffuseFromIrradiance("));
                Assert.That(transportOperator, Does.Contain("vec3 totalBounce = reflectedBounceRadiance + transmittedBounceRadiance;"));
                Assert.That(transport, Does.Not.Contain("bounceRadiance = ApplyGiMaterialOcclusion("));
                Assert.That(transport, Does.Contain("ReadSimpleDdgiTransportRayCache("));
                Assert.That(transport, Does.Contain("bool gpuScheduler = SimpleDdgiGpuSchedulerActive(pc.SchedulerArenaBufferIndex);"));
                Assert.That(transport, Does.Contain("uint queueOffset = (gpuScheduler ? schedulerQueueOffset : pc.DispatchQueueOffset) +"));
                Assert.That(transport, Does.Contain("if (!gpuScheduler && queueOffset >= params.probesToUpdate)"));
                Assert.That(transport, Does.Contain("uint globalRay = queueOffset * params.raysPerProbe + rayIndex;"));
                Assert.That(transport, Does.Contain("EvaluateSimpleDdgiCachedRecursiveBounce("));
                Assert.That(transport, Does.Not.Contain("SampleSimpleDdgiUnifiedIrradiance("));
                Assert.That(transport, Does.Contain("#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 1"));
                Assert.That(transportOperator, Does.Contain("if (max(reflected.r, max(reflected.g, reflected.b)) > 0.0)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID"));
                Assert.That(storageAbi, Does.Contain("if (format == SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36)\n        return 9u;"));
                Assert.That(storageAbi, Does.Contain("if (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28)\n        return 7u;"));
                Assert.That(storageAbi, Does.Contain("if (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24)\n        return 6u;"));
                Assert.That(storageAbi, Does.Contain(
                    "const uint SIMPLE_DDGI_STORAGE_ABI_PACKED = 7u;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_TRANSPORT_RAY_CACHE_ABI_VERSION = 7u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_TRANSPORT_CACHE_GENERATION_MASK =\n    SIMPLE_DDGI_UPDATE_GENERATION_MASK"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_TRANSPORT_CACHE_CLASSIFICATION_SHIFT = 24u"));
                Assert.That(shared, Does.Contain("uint PackSimpleDdgiTransportCacheHitKind(float hitKind)"));
                Assert.That(shared, Does.Contain("uint UnpackSimpleDdgiTransportCacheHitKind(uint generationAndFlags)"));
                Assert.That(shared, Does.Contain("void UpdateSimpleDdgiTransportRayCacheHitKind("));
                Assert.That(shared, Does.Not.Contain("SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG"));
                Assert.That(shared, Does.Not.Contain("SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG"));
                Assert.That(shared, Does.Contain("bool SimpleDdgiStorageDiagnosticSample("));
                Assert.That(shared, Does.Contain("return (value & 63u) == 0u;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_FLAG_THIN_SURFACE_TRANSMISSION = 1u << 20"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_MASK = 0xffu << SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT"));
                Assert.That(shared, Does.Contain("vec3 transmittedDiffuseReflectance;"));
                Assert.That(transportOperator, Does.Contain("float surfaceOffset = max(0.03, volume.spacing * 0.02);"));
                Assert.That(transportOperator, Does.Contain("hitPosition - normal * surfaceOffset"));
                Assert.That(shared, Does.Contain("void MarkSimpleDdgiProbeSourceCacheInvalid("));
                Assert.That(shared, Does.Contain("void ClearSimpleDdgiProbeSourceCacheInvalid("));
                Assert.That(trace, Does.Contain("MarkSimpleDdgiProbeSourceCacheInvalid("));
                Assert.That(trace, Does.Contain("ClearSimpleDdgiProbeSourceCacheInvalid(pc.ProbeStateBufferIndex, probeIndex);"));
                Assert.That(transport, Does.Contain("MarkSimpleDdgiProbeSourceCacheInvalid(pc.ProbeStateBufferIndex, update.probeIndex);"));
                Assert.That(transport, Does.Contain("if (gpuScheduler)"));
                Assert.That(transport, Does.Contain("the frozen\n        // participant field"));
                Assert.That(transport, Does.Contain("update.outcomeIndex,\n                1u << 3u"));
                Assert.That(trace, Does.Contain("SIMPLE_DDGI_FLAG_DIRECTION_CODEBOOK"));
                Assert.That(trace, Does.Contain("SimpleDdgiReconstructRayDirection("));
                Assert.That(trace, Does.Contain("SimpleDdgiPerProbeRayRotation(probeIndex, params.rayRotation)"));
                Assert.That(trace, Does.Contain(
                    "if (!SimpleDdgiUpdateMatchesProbeGenerationAndRecord("));
                Assert.That(trace, Does.Contain("vec3 probePosition = SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) + probeState.relocation;"));
                Assert.That(trace, Does.Contain("surface.GeometricNormal * max(0.03, volume.spacing * 0.02)"));
                Assert.That(trace, Does.Contain("vec3 emissiveDiffuse = surface.EmissiveRadiance + emissiveProxyDiffuse;"));
                Assert.That(trace, Does.Not.Contain("emissiveProxyDiffuse * (1.0 - bounceOwnership)"));
                Assert.That(trace, Does.Contain("vec3 radiance = SampleSimpleDdgiEnvironmentMissRadiance("));
                Assert.That(trace, Does.Contain("direction,\n        params);"));
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
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_LEGACY_RAY_RESULT_STRIDE_WORDS = 8u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 5u"));
                Assert.That(shared, Does.Contain("uint PackSimpleDdgiRayMetadata("));
                Assert.That(shared, Does.Contain("bool UnpackSimpleDdgiRayMetadata("));
                Assert.That(trace, Does.Contain("bool TraceSimpleDdgiBackfaceVisibilityDistance("));
                Assert.That(trace, Does.Contain("gl_RayFlagsCullFrontFacingTrianglesEXT"));
                Assert.That(trace, Does.Not.Contain("gl_RayFlagsCullBackFacingTrianglesEXT"));
                Assert.That(trace, Does.Contain("gl_RayFlagsNoneEXT"));
                Assert.That(trace, Does.Contain("backfaceVisibilityDistance < visibilityDistance"));
                Assert.That(trace, Does.Contain("visibilityHitKind =\n                SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE;"));
                Assert.That(trace, Does.Contain("UpdateSimpleDdgiTransportRayCacheHitKind("));
                Assert.That(trace, Does.Contain("cacheMaterialOcclusion,\n                visibilityHitKind,"));
                Assert.That(trace, Does.Contain("visibilityHitKind);"));
                Assert.That(trace, Does.Contain("WriteSimpleDdgiRayResultStorage("));
                Assert.That(hitShading, Does.Contain("bool DdgiCandidatePassesTwoSidedOpacity("));
                Assert.That(transport, Does.Contain("ReadSimpleDdgiRayResultStorage("));
                Assert.That(transport, Does.Contain("SimpleDdgiTransportRayCacheHitKind(source));"));
                Assert.That(transport, Does.Not.Contain("priorVisibilityHit.y"));
                Assert.That(relocate, Does.Contain("float distance = max(visibilityHit.x, 0.0);"));
                Assert.That(relocate, Does.Contain("ReadSimpleDdgiRayResultStorage("));
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
                Assert.That(transportOperator, Does.Contain("SimpleDdgiRayHitKindIsOneSidedBackFace(hitKind)"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiTraceEnergyCounterBase}u"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiBlendEnergyCounterBase}u"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_GATHER_REJECTION_COUNTER_BASE = {RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionCounterBase}u"));
                Assert.That(shared, Does.Contain($"SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterBase}u"));
                Assert.That(shared, Does.Contain("solverOwnershipSum"));
                Assert.That(shared, Does.Contain("void RecordSimpleDdgiTraceEnergyDiagnostics("));
                Assert.That(shared, Does.Contain("void RecordSimpleDdgiBlendEnergyDiagnostics("));
                Assert.That(shared, Does.Contain("void RecordSimpleDdgiVolumeEnergyEvidence("));
                Assert.That(shared, Does.Contain("uint(round(normalized * 2046.0)) + 1u"));
                Assert.That(trace, Does.Contain("RecordSimpleDdgiTraceEnergyDiagnostics("));
                Assert.That(trace, Does.Contain("traceDirectNoShadowDiffuse"));
                Assert.That(trace, Does.Contain("traceBounceDiffuse"));
                Assert.That(trace, Does.Contain("traceSkyDiffuse"));
                Assert.That(blend, Does.Contain("RecordSimpleDdgiBlendEnergyDiagnostics("));
                Assert.That(blend, Does.Contain("ReadSimpleDdgiDiagnosticVisibilityMoments("));
                Assert.That(blend, Does.Contain("#if NJULF_DDGI_DETAILED_COUNTERS\n    SimpleDdgiVolumePaging diagnosticPaging"));
                Assert.That(blend, Does.Contain("Keep the production blend path free of the additional paging-table read."));
                Assert.That(blend, Does.Contain("SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(pc.ProbeUpdateQueueBufferIndex, localProbeOffset);"));
                Assert.That(blend, Does.Contain("SimpleDdgiAdaptiveIrradianceHysteresis"));
                Assert.That(blend, Does.Contain("SimpleDdgiAdaptiveVisibilityHysteresis"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_FLAG_LIGHTING_CHANGE_ACTIVE"));
                Assert.That(blend, Does.Contain("float stepHysteresis = min(probeHysteresis, 0.60);"));
                Assert.That(blend, Does.Contain("state.luminanceChangeEma = mix"));
                Assert.That(blend, Does.Contain("shared vec4 SharedSimpleRayRadianceDistance[256];"));
                Assert.That(blend, Does.Contain("shared uint SharedSimpleRayVisibilityHit[256];"));
                Assert.That(blend, Does.Contain("shared uint SharedSimpleRayInvalid;"));
                Assert.That(blend, Does.Contain("atomicOr(SharedSimpleRayInvalid, 1u);"));
                Assert.That(blend, Does.Contain("vec2 ReadCachedSimpleRayVisibilityHit("));
                Assert.That(blend, Does.Contain("ReadSimpleDdgiRayResultStorage("));
                Assert.That(blend, Does.Contain("UnpackSimpleDdgiRayVisibilityHit("));
                Assert.That(blend, Does.Contain("bool refreshVisibility ="));
                Assert.That(blend, Does.Contain("!transportV2Active ||"));
                Assert.That(blend, Does.Contain("SimpleDdgiUpdateRequiresSourceRefresh(update)"));
                Assert.That(blend, Does.Contain("if (refreshVisibility)"));
                Assert.That(blend, Does.Contain("void LoadSimpleRayCache("));
                Assert.That(blend, Does.Contain("SimpleDdgiVolume volume,"));
                Assert.That(blend, Does.Contain("SimpleDdgiProbeUpdate update,"));
                Assert.That(blend, Does.Contain("barrier();"));
                Assert.That(blend, Does.Contain("bool rayCacheValid = SharedSimpleRayInvalid == 0u;"));
                Assert.That(blend, Does.Contain("~SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID;"));
                Assert.That(blend, Does.Contain("state.flags |=\n                            SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID;"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_BLEND_FLAG_REDUCED_COMPLEXITY"));
                Assert.That(blend, Does.Contain("shared vec3 SharedSimpleShCoefficients[9];"));
                Assert.That(blend, Does.Contain("void BuildReducedSimpleDdgiIrradiance(SimpleDdgiParams params, uint localProbeOffset, uint activeRayCount)"));
                Assert.That(blend, Does.Contain("float BlendReducedIrradianceTexel("));
                Assert.That(blend, Does.Not.Contain("void BlendReducedVisibilityTexel("));
                Assert.That(blend, Does.Contain("BlendVisibilityTexel("));
                Assert.That(blend, Does.Not.Contain("SharedSimpleVisibilityWeight"));
                Assert.That(blend, Does.Contain("bool reducedComplexityEnabled ="));
                Assert.That(blend, Does.Contain("(params.flags & SIMPLE_DDGI_FLAG_TRANSPORT_V2) == 0u;"));
                Assert.That(blend, Does.Contain("bool sharedRayCacheEnabled = true;"));
                Assert.That(blend, Does.Contain(": SimpleDdgiCadenceAdjustedHysteresis(params, update);"));
                Assert.That(blend, Does.Contain("bool maintenanceUpdate = SimpleDdgiUpdateIsMaintenance(update);"));
                Assert.That(blend, Does.Contain("bool firstSweepFirstColor ="));
                Assert.That(blend, Does.Contain("sweepIndex == 0u &&"));
                Assert.That(blend, Does.Contain("bool freshUpdate = firstSweepFirstColor &&"));
                Assert.That(blend, Does.Contain("(firstSweepFirstColor &&"));
                Assert.That(blend, Does.Not.Contain("firstSolveColor &&"));
                Assert.That(blend, Does.Not.Contain("SimpleDdgiSolveIsFinalColor(pc.Flags)"));
                Assert.That(blend, Does.Contain("(initialState.flags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0u"));
                Assert.That(blend, Does.Contain("initialState.flags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING"));
                Assert.That(blend, Does.Contain("Publishing the probe state is the commit point for gather."));
                Assert.That(blend, Does.Contain("memoryBarrierBuffer();"));
                Assert.That(blend, Does.Contain("state.flags &= ~SIMPLE_DDGI_PROBE_FLAG_FRESH;"));
                Assert.That(blend, Does.Contain("SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID) != 0u"));
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
                Assert.That(relocate, Does.Contain("WriteRelocationClassification(\n            probeIndex,\n            blendedRelocation,"));
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
                Assert.That(forward, Does.Contain("else if (simpleDdgiActive)"));
                Assert.That(forward, Does.Contain("finalDiffuseIndirect = diffuseIbl * simpleDisabledFallbackWeight * indirectAo;"));
                Assert.That(forward, Does.Contain("precomputedSimpleDdgiGather = SampleSimpleDdgiGather("));
                Assert.That(forward, Does.Contain("simpleGather = precomputedSimpleDdgiGather;"));
                Assert.That(
                    forward.Split("SampleSimpleDdgiGather(", StringSplitOptions.None),
                    Has.Length.EqualTo(2),
                    "Each non-cache forward program must retain one structured gather site.");
                Assert.That(forward, Does.Contain("simpleDdgiParams,"));
                Assert.That(forward, Does.Contain("simpleDdgiSecondaryContributionWeight"));
                Assert.That(forward, Does.Contain("simpleDdgiSecondVolumeUsed"));
                Assert.That(forward, Does.Contain(
                    "bool primaryValid = simpleDdgiPrimaryContributionWeight > 0.000001"));
                Assert.That(forward, Does.Contain("float simpleFallback = (1.0 - simpleRadiometricOwnership) * simpleDdgiParams.environmentFallbackIntensity;"));
                Assert.That(forward, Does.Contain("simpleFallback > SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT"));
                Assert.That(forward, Does.Contain("simpleEnvironmentFallback *= EstimateFarFieldSkyVisibility("));
                Assert.That(forward, Does.Contain("EstimateFarFieldSunShadow(worldPosition, normal, normalize(-light.Direction))"));
                Assert.That(forward, Does.Contain("DDGI_INVESTIGATION_FAR_SUN_SHADOW_SAMPLE_COUNTER"));
                Assert.That(forward, Does.Not.Contain("DDGI_INVESTIGATION_ROUGH_SPECULAR_SAMPLE_COUNTER"));
                Assert.That(forward, Does.Not.Contain("SampleSimpleDdgiUnifiedIrradiance(fragWorldPosition, reflectionDirection, viewDirection, false)"));
                Assert.That(forward, Does.Contain("if (IsDdgiDebugView(debugViewMode) || DdgiForwardEstimateDiagnosticPixel())"));
                Assert.That(forward, Does.Contain("SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug("));
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
                Assert.That(
                    blend,
                    Does.Contain(
                        "if (previous.w > 0.0001 && !freshUpdate && !certifiedTransport)"));
                Assert.That(
                    blend,
                    Does.Contain(
                        "(params.flags & SIMPLE_DDGI_FLAG_TRANSPORT_V2) != 0u"));
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
                Assert.That(simplePasses, Does.Contain("if (!gpuResident && !_volumeManager.CanExecutePublishTransaction)"));
                Assert.That(simplePasses, Does.Contain("SimpleDdgiPublishPass"));
                Assert.That(simplePasses, Does.Contain("BeginSampledAtlasGpuPublication(cmd)"));
                Assert.That(simplePasses, Does.Contain("MarkPublishExecuted()"));
                Assert.That(simpleManager, Does.Contain("SynchronizeSampledAtlasIfRequired(commandBuffer);"));
                Assert.That(simpleManager, Does.Contain("BuildRayDispatchBatches();"));
                Assert.That(simpleManager, Does.Contain("Stable counting sort preserves scheduler priority within a ray tier"));
                Assert.That(simplePasses, Does.Contain("VolumeManager.RayDispatchBatches"));
                Assert.That(simplePasses, Does.Contain("pushConstants.DispatchQueueOffset"));
                Assert.That(simplePasses, Does.Contain("(ulong)batch.ProbeCount * (ulong)batch.RaysPerProbe"));
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
                Assert.That(sampledAtlas, Does.Contain(
                    "groupIndex < MaxGpuPublishTextureGroups"));
                Assert.That(sampledPublish, Does.Contain(
                    "void CompleteSampledPublication(SimpleDdgiProbeUpdate update)"));
                Assert.That(sampledPublish, Does.Contain(
                    "receivers validate the same mapping and take the SSBO fallback"));
                Assert.That(renderer, Does.Contain("sceneData.SimpleDdgiSampledAtlasActive == 0"));
                Assert.That(renderer, Does.Contain("FarFieldPagedFeatureEnabled = simpleDdgiRequested &&"));
                Assert.That(renderer, Does.Contain("bool farFieldCoverageReady = qualityAllowsStaticStreaming"));
                Assert.That(renderer, Does.Contain("_farFieldClipmapManager?.CoverageReady == true"));
                Assert.That(renderer, Does.Contain("? gi.GiAccelerationStructureStaticResidentDistance"));
                Assert.That(renderer, Does.Contain("gi.StreamedGiAccelerationStructuresEnabled && stats.Active ? 1 : 0"));
            });
        }

        [Test]
        public void TransportAudit_UsesFrozenCacheOnlyAndReportsFp16Quantization()
        {
            string audit = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_transport_audit.comp");
            string feedback = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_schedule_feedback.comp");
            string participant = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_transport_participant.glsl");
            int rayResolverStart = audit.IndexOf(
                "uint ResolveSimpleDdgiAuditRayProbe(",
                StringComparison.Ordinal);
            int rayCacheBaseSentinelCheck = audit.IndexOf(
                "bool addressAvailable = context.cacheProbeBaseWordPlusOne != 0u;",
                rayResolverStart,
                StringComparison.Ordinal);
            int rayTransientFlagsCheck = audit.IndexOf(
                "bool eligible = SimpleDdgiTransportParticipantEligible(",
                rayResolverStart,
                StringComparison.Ordinal);
            int currentResolverStart = audit.IndexOf(
                "uint ResolveSimpleDdgiAuditProbe(",
                StringComparison.Ordinal);
            int currentPublicationCheck = audit.IndexOf(
                "if (!address.resident || !address.published)",
                currentResolverStart,
                StringComparison.Ordinal);
            int currentGenerationRead = audit.IndexOf(
                "uint sourceLightingGeneration = ReadStorageWordUniform(",
                currentResolverStart,
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(audit, Does.Contain(
                    "#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 1"));
                Assert.That(audit, Does.Contain("AuditSchedulerProbeStateOffsetWords"));
                Assert.That(audit, Does.Contain(
                    "probeIndex * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS"));
                Assert.That(audit, Does.Not.Contain(
                    "SIMPLE_DDGI_AUDIT_PROBE_STATE_WORDS"));
                Assert.That(audit, Does.Contain("AuditSolveEpoch"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_COUNTER_OVERFLOW_WORD"));
                Assert.That(audit, Does.Contain("uint sourceLightingGeneration = ReadStorageWordUniform("));
                Assert.That(audit, Does.Contain("context.sourceEpoch = ReadStorageWordUniform("));
                Assert.That(audit, Does.Contain("uint volumeGeneration = ReadStorageWordUniform("));
                Assert.That(audit, Does.Contain("SimpleDdgiRequiredSourceRayCount"));
                Assert.That(audit, Does.Contain("requiredSourceRayCount > params.raysPerProbe"));
                Assert.That(audit, Does.Contain("ReadSimpleDdgiPackedTransportRayCacheForSolve("));
                Assert.That(audit, Does.Contain("ReadSimpleDdgiLegacyTransportRayCacheForSolve("));
                Assert.That(audit, Does.Contain("pc.AuditSourceLightingGeneration,"));
                Assert.That(audit, Does.Contain("context.sourceEpoch,"));
                Assert.That(audit, Does.Contain("context.sourceRayCount,"));
                Assert.That(audit, Does.Not.Contain("rawSourceRayCount"));
                Assert.That(audit, Does.Not.Contain("rawSourceLightingGeneration"));
                Assert.That(audit, Does.Not.Contain("rawSourceEpoch"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_CACHE_IDENTITY_FAILURE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_CACHE_CARDINALITY_FAILURE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_CACHE_SOURCE_GENERATION_FAILURE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_CACHE_SOURCE_EPOCH_FAILURE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_CACHE_PHYSICAL_GENERATION_FAILURE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_QUANTIZATION_FLOOR_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_MAXIMUM_DEFECT_WITNESS_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_FIRST_INVALID_CACHE_IDENTITY_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_EXCLUDED_INACTIVE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_EXCLUDED_NOT_VISIBLE_WORD"));
                Assert.That(audit, Does.Contain("SIMPLE_DDGI_AUDIT_EXCLUDED_STALE_SOURCE_WORD"));
                Assert.That(audit, Does.Contain("if (localTexel == 0u && participantEligible)"));
                Assert.That(audit, Does.Contain(
                    "SIMPLE_DDGI_AUDIT_EXPECTED_PARTICIPANT_WORD,\n            1u"));
                Assert.That(audit, Does.Not.Contain(
                    "SIMPLE_DDGI_AUDIT_EXPECTED_PARTICIPANT_WORD,\n            pc.AuditExpectedParticipantCount"));
                Assert.That(audit, Does.Contain(
                    "bool addressAvailable = context.cacheProbeBaseWordPlusOne != 0u;"));
                Assert.That(audit, Does.Contain(
                    "return SIMPLE_DDGI_AUDIT_STATUS_EXCLUDED_NOT_VISIBLE;"));
                Assert.That(rayResolverStart, Is.GreaterThanOrEqualTo(0));
                Assert.That(rayCacheBaseSentinelCheck, Is.GreaterThan(rayResolverStart));
                Assert.That(
                    rayTransientFlagsCheck,
                    Is.GreaterThan(rayCacheBaseSentinelCheck),
                    "The lean ray role must establish an address witness before applying the shared participant contract.");
                Assert.That(currentResolverStart, Is.GreaterThanOrEqualTo(0));
                Assert.That(currentPublicationCheck, Is.GreaterThan(currentResolverStart));
                Assert.That(
                    currentPublicationCheck,
                    Is.GreaterThan(currentGenerationRead),
                    "The resolver may load the frozen tuple eagerly, but must classify nonresident/unpublished probes before stale-source evidence.");
                Assert.That(
                    audit.IndexOf(
                        "uint currentStatus = ResolveSimpleDdgiAuditProbe(",
                        StringComparison.Ordinal),
                    Is.LessThan(audit.IndexOf(
                        "uint packedStatus = SimpleDdgiAuditReadWorkspaceStatus(",
                        StringComparison.Ordinal)));
                Assert.That(audit, Does.Not.Contain("SIMPLE_DDGI_AUDIT_PROBE_META_VISIBLE"));
                Assert.That(audit, Does.Contain("packHalf2x16(vec2(value, 0.0))"));
                Assert.That(audit, Does.Contain("unpackHalf2x16(previousBits)"));
                Assert.That(audit, Does.Not.Contain("rayQueryEXT"));
                Assert.That(audit, Does.Not.Contain("TraceSimpleDdgi"));
                Assert.That(audit, Does.Not.Contain("EvaluateEnvironment"));
                Assert.That(feedback, Does.Contain("bool participating ="));
                Assert.That(feedback, Does.Contain(
                    "bool participating = SimpleDdgiTransportParticipantEligible("));
                Assert.That(feedback, Does.Contain(
                    "SchedulerResolvePayloadAddressDetailed("));
                Assert.That(audit, Does.Contain(
                    "#include \"ddgi_simple_transport_participant.glsl\""));
                Assert.That(feedback, Does.Contain(
                    "#include \"ddgi_simple_transport_participant.glsl\""));
                Assert.That(participant, Does.Contain(
                    "bool SimpleDdgiTransportParticipantEligible("));
                Assert.That(participant, Does.Contain(
                    "cacheProbeBaseWordPlusOne != 0u"));
                Assert.That(feedback, Does.Contain("shared uint feedbackReduction"));
                Assert.That(feedback, Does.Contain("probeIndex = localIndex"));
                Assert.That(feedback, Does.Contain("probeIndex += gl_WorkGroupSize.x"));
                Assert.That(feedback, Does.Contain("atomicAdd(feedbackReduction"));
                Assert.That(feedback, Does.Not.Contain(
                    "if (gl_GlobalInvocationID.x != 0u)"));
                Assert.That(
                    feedback,
                    Does.Not.Contain("(!SchedulerTailCertification() || visible)"));
            });
        }

        [Test]
        public void ResidentSourceRefresh_UsesCompleteSequenceAndExactWorkCounters()
        {
            string admit = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_schedule_admit.comp");
            string feedback = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_schedule_feedback.comp");
            string classify = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_schedule_classify.comp");
            string commitLocal = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_schedule_commit_local.comp");
            string trace = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_trace.comp");
            string transport = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_transport.comp");
            string blend = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_blend.comp");
            string common = ReadRepoText(
                "Njulf.Shaders",
                "common.glsl");
            string shared = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_schedule_shared.glsl");
            string transportShared = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_shared.glsl");
            string participant = ReadRepoText(
                "Njulf.Shaders",
                "ddgi_simple_transport_participant.glsl");
            string acceleratedSolve = ReadRepoText(
                "Njulf.Rendering",
                "Pipeline",
                "SimpleDdgiAcceleratedSolvePass.cs");
            string shaderProject = ReadRepoText(
                "Njulf.Shaders",
                "Njulf.Shaders.csproj");

            Assert.Multiple(() =>
            {
                Assert.That(admit, Does.Contain("if (sourceWork)"));
                Assert.That(admit, Does.Contain("uint requiredSourceRayCount = clamp("));
                Assert.That(admit, Does.Contain("sourceRayCount = SchedulerTransportV2()"));
                Assert.That(admit, Does.Contain("if (SchedulerTransportV2() || sourceWork)"));
                Assert.That(admit, Does.Contain("activeRayCount = requiredSourceRayCount;"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_CANDIDATE_WORDS = 4u"));
                Assert.That(admit, Does.Contain("uint sequenceOrdinal = inputIndex;"));
                Assert.That(admit, Does.Contain("uint activeRayCount = candidateRayTier =="));
                Assert.That(admit, Does.Contain("sourceVolumeBudgets[volume]"));
                Assert.That(admit, Does.Contain("sourceVolumeUsage[volumeIndex]"));
                Assert.That(admit, Does.Contain("SchedulerSourceTargetProbes()"));
                Assert.That(admit, Does.Contain(
                    "SchedulerActiveProbeCount() + sourceProbeTarget - 1u"));
                Assert.That(admit, Does.Contain("uint sourcePhase = SchedulerFrameIndex() % sourceInterval;"));
                Assert.That(admit, Does.Contain("bool tailSourceCohortPhase ="));
                Assert.That(admit, Does.Contain("phase == 10u || tailSourceCohortPhase"));
                Assert.That(admit, Does.Contain(
                    "volumeUsage[volumeIndex] >= quotas[volumeIndex]"));
                Assert.That(admit, Does.Contain(
                    "classUsage[classBase + workClass] >= classLimit"));
                Assert.That(admit, Does.Contain(
                    "stop as soon as this lane has exhausted its only"));
                Assert.That(admit, Does.Contain("sourceWork && !tailSourceCohortPhase"));
                Assert.That(admit, Does.Contain("uint tailSourceReasonMask ="));
                Assert.That(admit, Does.Contain("candidateReasons & tailSourceReasonMask"));
                Assert.That(admit, Does.Contain("SIMPLE_DDGI_SCHEDULER_REASON_RELOCATION_RETRY"));
                Assert.That(admit, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_TRANSPORT_USED"));
                Assert.That(admit, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_SOURCE_PROBE_USED"));
                Assert.That(feedback, Does.Contain("SchedulerVolumeFullRays(sourceVolumeIndex)"));
                Assert.That(feedback, Does.Contain(
                    "bool sourceReady = SchedulerTransportV2() && sourceVolumeValid &&"));
                Assert.That(feedback, Does.Contain("SimpleDdgiTransportSourceReady("));
                Assert.That(feedback, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_PENDING_TAIL_SOURCE"));
                Assert.That(feedback, Does.Contain(
                    "pendingFresh += !inactive && hasFresh ? 1u : 0u;"));
                Assert.That(feedback, Does.Contain(
                    "pendingRelocation += !inactive && hasRelocation ? 1u : 0u;"));
                Assert.That(classify, Does.Contain(
                    "#include \"ddgi_simple_transport_participant.glsl\""));
                Assert.That(classify, Does.Contain(
                    "bool tailParticipantCutFrozen = SchedulerTailCertification() &&"));
                Assert.That(classify, Does.Contain(
                    "inactive && !tailParticipantCutFrozen"));
                Assert.That(classify, Does.Contain(
                    "classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE"));
                Assert.That(classify, Does.Contain(
                    "activeWeight <= 0.001;"));
                int inactiveAdmissionGate = classify.IndexOf(
                    "if (inactive && !inactiveRetry)",
                    StringComparison.Ordinal);
                int visibleZeroSupportAdmission = classify.IndexOf(
                    "else if (visibleZeroSupport)",
                    StringComparison.Ordinal);
                Assert.That(inactiveAdmissionGate, Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    visibleZeroSupportAdmission,
                    Is.GreaterThan(inactiveAdmissionGate),
                    "Classified-inactive probes must not bypass their bounded retry interval through FRESH/zero-support priority.");
                Assert.That(classify, Does.Contain("uint sourceEpoch = SchedulerArenaRead(schedulerStateBase + 3u);"));
                Assert.That(classify, Does.Contain("uint sourceVolumeGeneration = SchedulerArenaRead(schedulerStateBase + 4u);"));
                Assert.That(classify, Does.Contain("uint solveEpoch = SchedulerSolveEpoch();"));
                Assert.That(classify, Does.Contain("uint lastSolveEpoch = SchedulerArenaRead(schedulerStateBase + 9u);"));
                Assert.That(classify, Does.Contain("lastSolveEpoch == solveEpoch"));
                Assert.That(classify, Does.Contain(
                    "current epoch, exclude it from classification"));
                Assert.That(classify, Does.Contain("uint cacheProbeBaseWordPlusOne = SchedulerArenaRead(schedulerStateBase + 10u);"));
                Assert.That(classify, Does.Contain("SimpleDdgiTransportSourceReady("));
                Assert.That(classify, Does.Not.Contain("sourceRays != 0u"));
                Assert.That(participant, Does.Contain("bool SimpleDdgiTransportSourceReady("));
                Assert.That(participant, Does.Contain("sourceRayCount == requiredSourceRayCount"));
                Assert.That(participant, Does.Contain("sourceEpoch != 0u"));
                Assert.That(participant, Does.Contain("volumeGeneration == expectedVolumeGeneration"));
                Assert.That(participant, Does.Contain("cacheProbeBaseWordPlusOne != 0u"));
                Assert.That(classify, Does.Contain("if (!sourceReady)"));
                Assert.That(classify, Does.Contain("reasons |= SIMPLE_DDGI_SCHEDULER_REASON_SOURCE_INVALID"));
                Assert.That(classify, Does.Contain("else if (!sourceReady || sourceInvalid"));
                Assert.That(classify, Does.Contain("if (invalidate)"));
                Assert.That(classify, Does.Contain("Do not leak those signals into a cached-solver candidate"));
                Assert.That(common, Does.Contain("uint NextSimpleDdgiPhysicalGeneration(uint generation)"));
                Assert.That(trace, Does.Contain("uint sourceCacheProbeGeneration ="));
                Assert.That(trace, Does.Contain("NextSimpleDdgiPhysicalGeneration(sourceCacheProbeGeneration)"));
                Assert.That(trace, Does.Contain("if (transportV2 && sourceRefresh)"));
                Assert.That(trace, Does.Not.Contain("if (transportV2)\n    {\n        WriteSimpleDdgiTransportRayCache("));
                Assert.That(transport, Does.Contain("uint sourceCacheProbeGeneration ="));
                Assert.That(transport, Does.Contain("NextSimpleDdgiPhysicalGeneration(sourceCacheProbeGeneration)"));
                Assert.That(transport, Does.Contain("sourceCacheProbeGeneration,\n            update.sourceLightingGeneration,"));
                Assert.That(transport, Does.Contain("update.sourceEpoch,\n            sourceRayCount,\n            source)"));
                Assert.That(transport, Does.Contain("SimpleDdgiSolveIsFinalSweep("));
                Assert.That(transport, Does.Not.Contain("if (SimpleDdgiSolveIsFinalColor(pc.Flags))"));
                Assert.That(transportShared, Does.Contain("bool SimpleDdgiSolveIsFinalSweep("));
                Assert.That(transportShared, Does.Contain("sweepIndex + 1u >= max(acceleratedSweepCount, 1u)"));
                Assert.That(acceleratedSolve, Does.Contain("ddgi_simple_transport_solve_legacy.comp.spv"));
                Assert.That(acceleratedSolve, Does.Contain("ddgi_simple_transport_solve_validate.comp.spv"));
                Assert.That(acceleratedSolve, Does.Contain("ddgi_simple_transport_solve_packed.comp.spv"));
                Assert.That(acceleratedSolve, Does.Contain("ResolveTransportShaderName("));
                Assert.That(acceleratedSolve, Does.Contain(
                    "_directionalGuidingTransport"));
                Assert.That(acceleratedSolve, Does.Contain("EnsureTransportPipeline()"));
                Assert.That(acceleratedSolve, Does.Contain("Runtime/CLI settings are resolved after pass initialization"));
                Assert.That(acceleratedSolve, Does.Contain("must observe the canonical SSBO publication"));
                Assert.That(shaderProject, Does.Contain("ddgi_simple_transport_solve_legacy.comp"));
                Assert.That(shaderProject, Does.Contain("ddgi_simple_transport_solve_validate.comp"));
                Assert.That(shaderProject, Does.Contain("ddgi_simple_transport_solve_packed.comp"));
                Assert.That(transport, Does.Contain("#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 1"));
                Assert.That(shaderProject, Does.Contain("-DSIMPLE_DDGI_DIRECTION_VALIDATION=1"));
                Assert.That(
                    shaderProject,
                    Does.Match(
                        "(?s)ddgi_simple_trace_validate_reuse\\.comp.*?<StorageMode>1</StorageMode>.*?SIMPLE_DDGI_DIRECTION_VALIDATION"));
                Assert.That(
                    shaderProject,
                    Does.Match(
                        "(?s)ddgi_simple_transport_solve_validate\\.comp.*?<StorageMode>1</StorageMode>.*?SIMPLE_DDGI_DIRECTION_VALIDATION"));
                Assert.That(trace, Does.Contain("#if SIMPLE_DDGI_DIRECTION_VALIDATION"));
                Assert.That(transport, Does.Contain("ReadSimpleDdgiLegacyTransportRayCacheForSolve("));
                Assert.That(transport, Does.Contain("#if SIMPLE_DDGI_DIRECTION_VALIDATION"));
                Assert.That(transportShared, Does.Contain(
                    "bool ReadSimpleDdgiLegacyTransportRayCacheForSolve("));
                Assert.That(blend, Does.Contain("including probes whose irradiance parity"));
                Assert.That(blend, Does.Contain("if (gpuScheduler && firstSweepFirstColor && local == 0u)"));
                Assert.That(commitLocal, Does.Contain("NextSimpleDdgiPhysicalGeneration(currentGeneration)"));
                Assert.That(commitLocal, Does.Contain("bool SealCommittedSourceCache("));
                Assert.That(commitLocal, Does.Contain(
                    "SIMPLE_DDGI_COMMIT_PHYSICAL_GENERATION_MASK = 0x00ffffffu"));
                Assert.That(commitLocal, Does.Contain(
                    "classificationCode < 1u || classificationCode > 5u"));
                Assert.That(commitLocal, Does.Contain("cachedSourceRayCount == sourceRayCount;"));
                Assert.That(
                    commitLocal,
                    Does.Contain("uint appliedMarker = SchedulerArenaRead(stateBase + 8u);"));
                Assert.That(commitLocal, Does.Contain("invalidatingUpdate && !sourceRefresh"));
                Assert.That(commitLocal, Does.Contain("uint committedSourceEpoch = SchedulerArenaRead(stateBase + 3u);"));
                Assert.That(commitLocal, Does.Contain("SchedulerUpdatePreservesSourceEpoch(updateFlags)"));
                Assert.That(commitLocal, Does.Contain(": SchedulerAdvanceSourceEpoch(committedSourceEpoch)"));
                Assert.That(commitLocal, Does.Not.Contain("SchedulerArenaRead(outcomeBase + 12u)"));
                Assert.That(commitLocal, Does.Contain("SchedulerArenaWrite(stateBase + 3u, sourceEpoch)"));
                Assert.That(commitLocal, Does.Contain(
                    "!TryResolveSimpleDdgiTransportCacheProbeBase("));
                Assert.That(commitLocal, Does.Contain(
                    "cacheProbeBaseWordPlusOne != liveCacheProbeBaseWordPlusOne"));
                Assert.That(commitLocal, Does.Contain(
                    "SchedulerArenaWrite(stateBase + 10u, cacheProbeBaseWordPlusOne);"));
                Assert.That(
                    commitLocal,
                    Does.Not.Contain("SchedulerArenaWrite(stateBase + 3u, SchedulerSourceEpoch())"));
                Assert.That(shared, Does.Contain("SchedulerArenaWrite(base + 12u, 0u);"));
                Assert.That(shared, Does.Contain("outcome words 12 and 13 are producer completion counters"));
                Assert.That(shared, Does.Contain("uint committedSourceEpoch = SchedulerArenaRead(stateBase + 3u);"));
                Assert.That(shared, Does.Contain("SchedulerUpdatePreservesSourceEpoch(updateFlags)"));
                Assert.That(shared, Does.Contain(": SchedulerAdvanceSourceEpoch(committedSourceEpoch)"));
                Assert.That(classify, Does.Contain("bool stableTailParticipant ="));
                Assert.That(classify, Does.Contain("solveEpoch != 0u &&"));
                Assert.That(classify, Does.Contain("!stableTailParticipant"));
                Assert.That(classify, Does.Contain("solveEpoch == 0u"));
                Assert.That(classify, Does.Contain("!SchedulerGlobalConvergence()"));
                Assert.That(classify, Does.Contain("solveEpoch == 0u &&"));
                Assert.That(classify, Does.Contain("!routineDue"));
                Assert.That(classify, Does.Contain("failed pre-epoch work can set the private"));
                Assert.That(feedback, Does.Contain("SchedulerArenaWrite(base + 21u, transportUsed)"));
                Assert.That(feedback, Does.Contain("SchedulerArenaWrite(base + 62u, sourceProbeUsed)"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_TRANSPORT_USED = 11u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_SOURCE_PROBE_USED = 12u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_HARD_SOURCE_PROBE_USED = 13u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_ROUTINE_SOURCE_PROBE_USED = 14u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_CACHED_SOLVER_PROBE_USED = 15u"));
                Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_PENDING_TAIL_SOURCE = 48u"));
                Assert.That(classify, Does.Contain("bool atlasFresh = SchedulerAtlasFresh();"));
                Assert.That(classify, Does.Contain("topologyInvalid || exposed || atlasFresh"));
                Assert.That(commitLocal, Does.Contain("SIMPLE_DDGI_SCHEDULER_REASON_FRESH |"));
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
                Assert.That(fog, Does.Contain(
                    "gather = SampleSimpleDdgiGather("));
                Assert.That(fog, Does.Contain(
                    "SimpleDdgiRadiometricOwnership(gather)"));
                Assert.That(fog, Does.Contain(
                    "SimpleDdgiLeakAttenuation(gather, simpleParams)"));
                Assert.That(fog, Does.Not.Contain("SampleDdgiAmbientIrradiance("));
                Assert.That(particleVertex, Does.Contain("#include \"ddgi_simple_shared.glsl\""));
                Assert.That(particleVertex, Does.Contain("SIMPLE_DDGI_FLAG_PARTICLE_ENABLED"));
                Assert.That(particleVertex, Does.Contain("SampleSimpleDdgiIrradiance(center, particleDdgiNormal, particleDdgiNormal)"));
                Assert.That(particleVertex, Does.Not.Contain("SampleDdgiAmbientDiffuse("));
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
            const float wrapShadingOffset = 0.20f;
            return (halfLambert * halfLambert + wrapShadingOffset) /
                (1.0f + wrapShadingOffset);
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

        private static CpuSimpleDdgiCascadeBlend BlendCascadeAvailability(
            float innerSpatialCoverage,
            float innerValidSupport,
            float innerDirectionalSupport,
            float innerTransitionWeight,
            float outerSpatialCoverage,
            float outerValidSupport,
            float outerDirectionalSupport)
        {
            float w = Math.Clamp(innerTransitionWeight, 0.0f, 1.0f);
            float innerAvailableMass = innerValidSupport * innerSpatialCoverage * w;
            float outerAvailableMass = outerValidSupport * outerSpatialCoverage *
                (1.0f - innerAvailableMass);
            float availableMass = innerAvailableMass + outerAvailableMass;
            return new CpuSimpleDdgiCascadeBlend(
                innerAvailableMass,
                outerAvailableMass,
                innerAvailableMass,
                outerAvailableMass,
                availableMass > 1.0e-6f
                    ? (innerAvailableMass * innerDirectionalSupport +
                       outerAvailableMass * outerDirectionalSupport) / availableMass
                    : 0.0f);
        }

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
                    (1.0f - Math.Abs(encoded.Y)) * (encoded.X >= 0.0f ? 1.0f : -1.0f),
                    (1.0f - Math.Abs(encoded.X)) * (encoded.Y >= 0.0f ? 1.0f : -1.0f));
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

        private readonly record struct CpuSimpleDdgiCascadeBlend(
            float InnerAvailableMass,
            float OuterAvailableMass,
            float InnerRadiometricMass,
            float OuterRadiometricMass,
            float DirectionalSupport);

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
