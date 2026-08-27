using System.Threading;
using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DebugToolingContractsTests
    {
        [Test]
        public void DebugOverlaySettings_DefaultsAreShippingSafe()
        {
            var settings = new RenderSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Debug.Enabled, Is.False);
                Assert.That(settings.Debug.Mode, Is.EqualTo(DebugOverlayMode.None));
                Assert.That(settings.Debug.AllowGpuTiming, Is.False);
                Assert.That(settings.Debug.AllowScreenshots, Is.False);
                Assert.That(settings.Debug.AllowRenderDocCapture, Is.False);
                Assert.That(settings.Debug.CpuSnapshotsEnabled, Is.False);
                Assert.That(settings.Diagnostics.GpuMeshletCountersEnabled, Is.False);
                Assert.That(settings.Diagnostics.DdgiForwardEstimateCountersEnabled, Is.False);
                Assert.That(settings.Debug.SelectedObjectIndex, Is.EqualTo(-1));
                Assert.That(settings.Debug.MaxDebugLineSegments, Is.EqualTo(DebugDrawList.DefaultMaxLineSegments));
            });
        }

        [Test]
        public void RendererDiagnosticsBuffer_CounterFamiliesAreContiguousAndFullyCounted()
        {
            string commonShader = ReadRepoText("Njulf.Shaders", "common.glsl");
            string simpleSharedShader = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
            var families = new (string Name, int Start, int Count)[]
            {
                ("meshlet", 0, RendererDiagnosticsBuffer.MeshletCounterCount),
                ("DDGI forward estimate", RendererDiagnosticsBuffer.DdgiForwardEstimateCounterBase,
                    RendererDiagnosticsBuffer.DdgiForwardEstimateCounterCount),
                ("DDGI trace energy", RendererDiagnosticsBuffer.DdgiTraceEnergyCounterBase,
                    RendererDiagnosticsBuffer.DdgiTraceEnergyCounterCount),
                ("DDGI trace early-out", RendererDiagnosticsBuffer.DdgiTraceEarlyOutCounterBase,
                    RendererDiagnosticsBuffer.DdgiTraceEarlyOutCounterCount),
                ("DDGI blend energy", RendererDiagnosticsBuffer.DdgiBlendEnergyCounterBase,
                    RendererDiagnosticsBuffer.DdgiBlendEnergyCounterCount),
                ("DDGI ring mismatch", RendererDiagnosticsBuffer.DdgiTraceRingMismatchSampleBase,
                    RendererDiagnosticsBuffer.DdgiTraceRingMismatchSampleCount),
                ("far field", RendererDiagnosticsBuffer.FarFieldCounterBase,
                    RendererDiagnosticsBuffer.FarFieldCounterCount),
                ("DDGI investigation", RendererDiagnosticsBuffer.DdgiInvestigationCounterBase,
                    RendererDiagnosticsBuffer.DdgiInvestigationCounterCount),
                ("simple DDGI transport", RendererDiagnosticsBuffer.SimpleDdgiTransportCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiTransportCounterCount),
                ("directional shadow receiver", RendererDiagnosticsBuffer.DirectionalShadowReceiverCounterBase,
                    RendererDiagnosticsBuffer.DirectionalShadowReceiverCounterCount),
                ("far-field material V2", RendererDiagnosticsBuffer.FarFieldMaterialV2CounterBase,
                    RendererDiagnosticsBuffer.FarFieldMaterialV2CounterCount),
                ("material GI", RendererDiagnosticsBuffer.MaterialGiCounterBase,
                    RendererDiagnosticsBuffer.MaterialGiCounterCount),
                ("simple DDGI gather rejection", RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionCounterCount),
                ("simple DDGI gather all failed", RendererDiagnosticsBuffer.SimpleDdgiGatherAllFailedCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiGatherAllFailedCounterCount),
                ("DDGI delivery failure", RendererDiagnosticsBuffer.DdgiDeliveryFailureCounterBase,
                    RendererDiagnosticsBuffer.DdgiDeliveryFailureCounterCount),
                ("DDGI shadow visibility", RendererDiagnosticsBuffer.DdgiShadowVisibilityCounterBase,
                    RendererDiagnosticsBuffer.DdgiShadowVisibilityCounterCount),
                ("DDGI layered receivers", RendererDiagnosticsBuffer.DdgiLayeredReceiverCounterBase,
                    RendererDiagnosticsBuffer.DdgiLayeredReceiverCounterCount),
                ("DDGI thin transport", RendererDiagnosticsBuffer.ThinSurfaceTransportCounterBase,
                    RendererDiagnosticsBuffer.ThinSurfaceTransportCounterCount),
                ("simple DDGI per-volume energy", RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterCount),
                ("DDGI effective albedo", RendererDiagnosticsBuffer.DdgiAlbedoCounterBase,
                    RendererDiagnosticsBuffer.DdgiAlbedoCounterCount),
                ("simple DDGI gather multiplicity", RendererDiagnosticsBuffer.SimpleDdgiGatherMultiplicityCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiGatherMultiplicityCounterCount),
                ("decal fragment attribution", RendererDiagnosticsBuffer.DecalFragmentAttributionCounterBase,
                    RendererDiagnosticsBuffer.DecalFragmentAttributionCounterCount),
                ("simple DDGI storage validation", RendererDiagnosticsBuffer.SimpleDdgiStorageValidationCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiStorageValidationCounterCount),
                ("simple DDGI per-volume energy evidence",
                    RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyEvidenceCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyEvidenceCounterCount),
                ("directional shadow caster attribution",
                    RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticCounterBase,
                    RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticCounterCount),
                ("DDGI geometry participation", RendererDiagnosticsBuffer.DdgiGeometryParticipationCounterBase,
                    RendererDiagnosticsBuffer.DdgiGeometryParticipationCounterCount),
                ("DDGI many-light estimator", RendererDiagnosticsBuffer.DdgiManyLightCounterBase,
                    RendererDiagnosticsBuffer.DdgiManyLightCounterCount),
                ("simple DDGI near visibility", RendererDiagnosticsBuffer.SimpleDdgiNearVisibilityCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiNearVisibilityCounterCount),
                ("DDGI debug overlay", RendererDiagnosticsBuffer.DebugDdgiOverlayCounterBase,
                    RendererDiagnosticsBuffer.DebugDdgiOverlayCounterCount),
                ("thick transmission", RendererDiagnosticsBuffer.ThickTransmissionCounterBase,
                    RendererDiagnosticsBuffer.ThickTransmissionCounterCount),
                ("DDGI area-light sampling", RendererDiagnosticsBuffer.DdgiAreaLightCounterBase,
                    RendererDiagnosticsBuffer.DdgiAreaLightCounterCount),
                ("transparent reflections", RendererDiagnosticsBuffer.TransparentReflectionCounterBase,
                    RendererDiagnosticsBuffer.TransparentReflectionCounterCount),
                ("simple DDGI receiver cache", RendererDiagnosticsBuffer.SimpleDdgiReceiverCacheCounterBase,
                    RendererDiagnosticsBuffer.SimpleDdgiReceiverCacheCounterCount)
            };

            Assert.Multiple(() =>
            {
                int nextExpectedStart = 0;
                foreach ((string name, int start, int count) in families)
                {
                    Assert.That(start, Is.EqualTo(nextExpectedStart), $"{name} counter base");
                    Assert.That(count, Is.GreaterThan(0), $"{name} counter count");
                    nextExpectedStart += count;
                }

                Assert.That(RendererDiagnosticsBuffer.DdgiForwardEstimateLuminanceScale, Is.EqualTo(4096.0f));
                Assert.That(RendererDiagnosticsBuffer.DdgiForwardEstimateCounterCount, Is.EqualTo(46));
                Assert.That(RendererDiagnosticsBuffer.DdgiBlendEnergyCounterCount, Is.EqualTo(7));
                Assert.That(RendererDiagnosticsBuffer.FarFieldCounterCount, Is.EqualTo(10));
                Assert.That(RendererDiagnosticsBuffer.DdgiInvestigationFixedCounterCount, Is.EqualTo(38));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeGatherCounterCount,
                    Is.EqualTo(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumePrimaryGatherCounterBase,
                    Is.EqualTo(RendererDiagnosticsBuffer.DdgiInvestigationCounterBase +
                               RendererDiagnosticsBuffer.DdgiInvestigationFixedCounterCount));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeSampledGatherCounterBase,
                    Is.EqualTo(RendererDiagnosticsBuffer.SimpleDdgiVolumePrimaryGatherCounterBase +
                               RendererDiagnosticsBuffer.SimpleDdgiVolumeGatherCounterCount));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiTransportCounterCount, Is.EqualTo(6));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowReceiverCascadeCount,
                    Is.EqualTo(ShadowSettings.MaxDirectionalCascades));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowReceiverCounterFamilyCount, Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowReceiverDepthQuantizationScale,
                    Is.EqualTo(65535.0f));
                Assert.That(RendererDiagnosticsBuffer.FarFieldMaterialV2CounterCount, Is.EqualTo(2));
                Assert.That(RendererDiagnosticsBuffer.MaterialGiCounterCount, Is.EqualTo(10));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionReasonCount, Is.EqualTo(10));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiGatherRoleCount, Is.EqualTo(3));
                Assert.That(RendererDiagnosticsBuffer.DdgiDeliveryFailureCounterCount, Is.EqualTo(1));
                Assert.That(RendererDiagnosticsBuffer.DdgiShadowVisibilityCounterCount, Is.EqualTo(4));
                Assert.That(RendererDiagnosticsBuffer.DdgiLayeredReceiverCounterCount, Is.EqualTo(6));
                Assert.That(RendererDiagnosticsBuffer.ThinSurfaceTransportCounterCount, Is.EqualTo(18));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterStride, Is.EqualTo(19));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterCount, Is.EqualTo(
                    GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * 19));
                Assert.That(RendererDiagnosticsBuffer.DdgiAlbedoCounterCount, Is.EqualTo(12));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiGatherMultiplicityCounterCount, Is.EqualTo(9));
                Assert.That(RendererDiagnosticsBuffer.DecalFragmentAttributionCounterCount, Is.EqualTo(6));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiStorageValidationCounterCount, Is.EqualTo(23));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyEvidenceHistogramCount, Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyEvidenceCounterStride, Is.EqualTo(39));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyEvidenceCounterCount, Is.EqualTo(
                    GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * 39));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticHeaderWordCount, Is.EqualTo(7));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticFrameMetadataMagic,
                    Is.EqualTo(0x44534346u));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticRecordCapacity, Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowCasterDiagnosticRecordStride, Is.EqualTo(28));
                Assert.That(RendererDiagnosticsBuffer.DdgiGeometryParticipationCounterCount, Is.EqualTo(12));
                Assert.That(RendererDiagnosticsBuffer.DdgiManyLightCounterCount, Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiNearVisibilityCounterCount, Is.EqualTo(10));
                Assert.That(RendererDiagnosticsBuffer.DebugDdgiOverlayReasonCounterCount, Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.DebugDdgiOverlayCounterCount, Is.EqualTo(27));
                Assert.That(RendererDiagnosticsBuffer.ThickTransmissionTaskCounter,
                    Is.EqualTo(RendererDiagnosticsBuffer.ThickTransmissionCounterBase));
                Assert.That(RendererDiagnosticsBuffer.ThickTransmissionCounterCount,
                    Is.EqualTo(1));
                Assert.That(RendererDiagnosticsBuffer.TransparentReflectionTaskCounter,
                    Is.EqualTo(RendererDiagnosticsBuffer.TransparentReflectionCounterBase));
                Assert.That(RendererDiagnosticsBuffer.TransparentReflectionCounterCount,
                    Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiReceiverCacheCounterCount,
                    Is.EqualTo(17));
                Assert.That(RendererDiagnosticsBuffer.CounterCount,
                    Is.EqualTo(RendererDiagnosticsBuffer.SimpleDdgiReceiverCacheCounterBase + 17));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiStorageValidationBufferSize,
                    Is.GreaterThanOrEqualTo((ulong)RendererDiagnosticsBuffer.SimpleDdgiStorageValidationCounterCount *
                                            sizeof(uint)));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiStorageValidationBufferSize % 256ul, Is.Zero);
                Assert.That(simpleSharedShader, Does.Contain(
                    $"SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterBase}u"));
                Assert.That(simpleSharedShader, Does.Contain(
                    $"SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_STRIDE = {RendererDiagnosticsBuffer.SimpleDdgiVolumeEnergyCounterStride}u"));
                Assert.That(commonShader, Does.Contain(
                    $"DDGI_THIN_TRANSPORT_COUNTER_BASE = {RendererDiagnosticsBuffer.ThinSurfaceTransportCounterBase}u"));
                Assert.That(commonShader, Does.Contain(
                    $"DDGI_ALBEDO_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiAlbedoCounterBase}u"));
                Assert.That(commonShader, Does.Contain(
                    "DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 9u"));
                Assert.That(commonShader, Does.Contain(
                    "SIMPLE_DDGI_INVALID_SOURCE_EPOCH_COUNTER ="));
                Assert.That(commonShader, Does.Contain(
                    "SIMPLE_DDGI_INVALID_HIT_KIND_COUNTER ="));
                Assert.That(commonShader, Does.Contain(
                    "SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_FRAME_METADATA_MAGIC = 0x44534346u"));
                Assert.That(commonShader, Does.Contain(
                    "DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "DDGI_MANY_LIGHT_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "DEBUG_DDGI_OVERLAY_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "THICK_TRANSMISSION_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "DDGI_AREA_LIGHT_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "TRANSPARENT_REFLECTION_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "TRANSPARENT_REFLECTION_ENVIRONMENT_FALLBACK_COUNTER ="));
                Assert.That(commonShader, Does.Contain(
                    "TRANSPARENT_REFLECTION_SSR_RESERVED_SAMPLE_COUNTER ="));
                Assert.That(commonShader, Does.Contain(
                    "TRANSPARENT_REFLECTION_RAY_EXACT_BUDGET_REJECT_COUNTER ="));
                Assert.That(commonShader, Does.Contain(
                    "SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE ="));
                Assert.That(commonShader, Does.Contain(
                    "SIMPLE_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_COUNTER ="));
                Assert.That(simpleSharedShader, Does.Contain(
                    "void RecordSimpleDdgiVolumeEnergyEvidence("));
                Assert.That(simpleSharedShader, Does.Contain(
                    "(winnerKey << SIMPLE_DDGI_ENERGY_EVIDENCE_PAYLOAD_BITS) | payload"));
                Assert.That(commonShader, Does.Contain(
                    "void AddSimpleDdgiStorageValidationDiagnostic("));
                Assert.That(simpleSharedShader, Does.Contain(
                    "AddSimpleDdgiStorageValidationDiagnostic("));
                Assert.That(renderer, Does.Contain(
                    "ValidationCounters = counters.StorageValidation"));
                Assert.That(commonShader,
                    Does.Contain("DDGI_THIN_INVALID_TRANSMISSION_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 17u"));
                Assert.That(RendererDiagnosticsBuffer.DdgiShadowHitDistanceScale, Is.EqualTo(256.0f));
                Assert.That(RendererDiagnosticsBuffer.CounterCount, Is.EqualTo(nextExpectedStart));
                Assert.That(RendererDiagnosticsBuffer.CounterBufferSize,
                    Is.EqualTo((ulong)nextExpectedStart * sizeof(uint)));
            });
        }

        [Test]
        public void RendererDiagnosticsBuffer_ReceiverCacheCountersRequireMarkerAndUseStableOffsets()
        {
            var words = new uint[RendererDiagnosticsBuffer.CounterCount];

            SimpleDdgiReceiverCacheGpuCounters unavailable =
                RendererDiagnosticsBuffer.DecodeSimpleDdgiReceiverCacheCounters(words);

            int counterBase = RendererDiagnosticsBuffer.SimpleDdgiReceiverCacheCounterBase;
            words[counterBase] = 1u;
            for (int offset = 1; offset < RendererDiagnosticsBuffer.SimpleDdgiReceiverCacheCounterCount; offset++)
                words[counterBase + offset] = (uint)(100 + offset);

            SimpleDdgiReceiverCacheGpuCounters decoded =
                RendererDiagnosticsBuffer.DecodeSimpleDdgiReceiverCacheCounters(words);

            Assert.Multiple(() =>
            {
                Assert.That(unavailable.ReadbackValid, Is.Zero);
                Assert.That(decoded.ReadbackValid, Is.EqualTo(1));
                Assert.That(decoded.ResolveCandidateCount, Is.EqualTo(101ul));
                Assert.That(decoded.ResolveValidCount, Is.EqualTo(102ul));
                Assert.That(decoded.ResolveInvalidOrNonFiniteRejectCount, Is.EqualTo(103ul));
                Assert.That(decoded.ResolveDepthOrPositionRejectCount, Is.EqualTo(104ul));
                Assert.That(decoded.ResolvePlaneRejectCount, Is.EqualTo(105ul));
                Assert.That(decoded.ResolveNormalRejectCount, Is.EqualTo(106ul));
                Assert.That(decoded.ResolveInsufficientSupportRejectCount, Is.EqualTo(107ul));
                Assert.That(decoded.ForwardCandidateCount, Is.EqualTo(108ul));
                Assert.That(decoded.ForwardAcceptedCount, Is.EqualTo(109ul));
                Assert.That(decoded.ForwardInvalidOrNonFiniteRejectCount, Is.EqualTo(110ul));
                Assert.That(decoded.ForwardDepthOrPositionRejectCount, Is.EqualTo(111ul));
                Assert.That(decoded.ForwardPlaneRejectCount, Is.EqualTo(112ul));
                Assert.That(decoded.ForwardNormalRejectCount, Is.EqualTo(113ul));
                Assert.That(decoded.ForwardInsufficientSupportRejectCount, Is.EqualTo(114ul));
                Assert.That(decoded.ExactFallbackFragmentCount, Is.EqualTo(115ul));
                Assert.That(decoded.LegacyFragmentCount, Is.EqualTo(116ul));
            });

            Assert.Throws<ArgumentException>(() =>
                RendererDiagnosticsBuffer.DecodeSimpleDdgiReceiverCacheCounters(
                    new uint[RendererDiagnosticsBuffer.CounterCount - 1]));
        }

        [Test]
        public void RenderSettings_ResetRenderViewOverridesClearsEveryVisualization()
        {
            var settings = new RenderSettings
            {
                ShowRawHdrSceneColor = true,
                FeatureIsolation = RenderFeatureIsolationMode.Shadows
            };
            settings.Shadows.DebugView = ShadowDebugView.CascadeOverlay;
            settings.Shadows.DirectionalShadowPreviewCascade = 3;
            settings.Shadows.ForceStaticCascadeCacheRefresh = true;
            settings.Bloom.DebugView = BloomDebugView.ExtractMask;
            settings.Environment.DebugView = EnvironmentDebugView.SkyboxOnly;
            settings.Reflections.DebugView = ReflectionDebugView.ProbeInfluence;
            settings.AmbientOcclusion.DebugView = AmbientOcclusionDebugView.RawAo;
            settings.GlobalIllumination.DebugView = GlobalIlluminationDebugView.FinalIndirect;
            settings.AntiAliasing.DebugView = AntiAliasingDebugView.InputColor;
            settings.Fog.DebugView = FogDebugView.FogFactor;
            settings.Transparency.DebugView = TransparencyDebugView.AlphaMode;
            settings.Decals.DebugView = DecalDebugView.GeometryDecalMask;
            settings.Animation.DebugView = AnimationDebugView.SkinnedObjects;
            settings.Particles.DebugView = ParticleDebugView.Bounds;
            settings.Foliage.DebugView = FoliageDebugView.Clusters;
            settings.Materials.DebugView = MaterialDebugView.BaseColor;
            settings.Debug.Mode = DebugOverlayMode.LightTiles;

            settings.ResetRenderViewOverrides();

            Assert.Multiple(() =>
            {
                Assert.That(settings.ShowRawHdrSceneColor, Is.False);
                Assert.That(settings.FeatureIsolation, Is.EqualTo(RenderFeatureIsolationMode.FullFrame));
                Assert.That(settings.Shadows.DebugView, Is.EqualTo(ShadowDebugView.None));
                Assert.That(settings.Shadows.DirectionalShadowPreviewCascade, Is.Zero);
                Assert.That(settings.Shadows.ForceStaticCascadeCacheRefresh, Is.False);
                Assert.That(settings.Bloom.DebugView, Is.EqualTo(BloomDebugView.None));
                Assert.That(settings.Environment.DebugView, Is.EqualTo(EnvironmentDebugView.None));
                Assert.That(settings.Reflections.DebugView, Is.EqualTo(ReflectionDebugView.None));
                Assert.That(settings.AmbientOcclusion.DebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(settings.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(settings.AntiAliasing.DebugView, Is.EqualTo(AntiAliasingDebugView.None));
                Assert.That(settings.Fog.DebugView, Is.EqualTo(FogDebugView.None));
                Assert.That(settings.Transparency.DebugView, Is.EqualTo(TransparencyDebugView.None));
                Assert.That(settings.Decals.DebugView, Is.EqualTo(DecalDebugView.None));
                Assert.That(settings.Animation.DebugView, Is.EqualTo(AnimationDebugView.None));
                Assert.That(settings.Particles.DebugView, Is.EqualTo(ParticleDebugView.None));
                Assert.That(settings.Foliage.DebugView, Is.EqualTo(FoliageDebugView.None));
                Assert.That(settings.Materials.DebugView, Is.EqualTo(MaterialDebugView.None));
                Assert.That(settings.Debug.Mode, Is.EqualTo(DebugOverlayMode.None));
            });
        }

        [Test]
        public void RendererDiagnostics_EmptyInitializesDebugFields()
        {
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty;

            Assert.Multiple(() =>
            {
                Assert.That(diagnostics.ActiveQualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
                Assert.That(diagnostics.DebugToolingEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DebugOverlayEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DebugOverlayMode, Is.EqualTo(DebugOverlayMode.None));
                Assert.That(diagnostics.CpuDebugSnapshotsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DebugSelectedObjectIndex, Is.EqualTo(-1));
                Assert.That(diagnostics.DebugSelectedObjectName, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DebugDrawEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DebugDrawLineCount, Is.EqualTo(0));
                Assert.That(diagnostics.DebugDrawPersistentLineCount, Is.EqualTo(0));
                Assert.That(diagnostics.DebugDrawDroppedLineCount, Is.EqualTo(0));
                Assert.That(diagnostics.DebugDdgiProbeVolumesDrawn, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiCascadeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiScrollCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiNewProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiStaleProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageProbeAge, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiMaxProbeAge, Is.EqualTo(0UL));
                Assert.That(diagnostics.DdgiFrustumUpdatePercentage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiOutsideFrustumUpdatePercentage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiForwardEstimateCountersReadbackValid, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiForwardEstimateSampleCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiForwardEstimateSampledIrradianceLuminance, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEnergySampleCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEnergyRayLuminanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiTraceEnergyDirectLuminanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiTraceEnergyDirectNoShadowLuminanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiShadowVisibilityRayCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiShadowVisibilityOccludedCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiShadowVisibilityNearHitCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiShadowVisibilityCommittedHitDistanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiTraceEarlyOutDisabledCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEarlyOutBeyondRequestCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEarlyOutResolveBoundsCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEarlyOutResolveProbeRangeCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEarlyOutResolveClipmapCellCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEarlyOutResolveClipmapRingCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceRingMismatchCorrectedCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceRingMismatchSample, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DdgiBlendEnergySampleCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiBlendEnergyIrradianceLuminanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiBlendEnergyConfidenceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiBlendEnergyNonFiniteIrradianceCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiBlendEnergyFireflySuppressedCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiClipmapInfoPrimaryAttemptCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiClipmapInfoPrimaryOkCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiClipmapInfoPrimaryFailedCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiClipmapInfoPrimaryEdgeFadeAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiClipmapInfoPrimaryBlendWeightAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiRuntimeSnapshot, Is.EqualTo(DdgiRuntimeSnapshot.Empty));
                Assert.That(diagnostics.DdgiDiagnosticWarnings, Is.Empty);
                Assert.That(diagnostics.DdgiResourceReinitializationCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiTotalResourceReinitializationCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuTimingSupported, Is.EqualTo(0));
                Assert.That(diagnostics.GpuTimingEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.GpuTimingUnavailableReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.ScreenshotPendingCount, Is.EqualTo(0));
                Assert.That(diagnostics.LastScreenshotPath, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.LastScreenshotError, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.RenderDocAvailable, Is.EqualTo(0));
                Assert.That(diagnostics.RenderDocCaptureRequested, Is.EqualTo(0));
                Assert.That(diagnostics.LastRenderDocCaptureMessage, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.GpuMeshletCountersEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.GpuMeshletCountersStatus, Is.EqualTo("GPU meshlet counters disabled."));
                Assert.That(diagnostics.FoliagePatchCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliagePrototypeCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageClusterCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageVisibleClusterCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageCulledClusterCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageVisibleMeshletDrawCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageGrassBladeEstimate, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageLod0VisibleCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageLod1VisibleCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageLod2VisibleCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageHiZTestedCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageHiZRejectedCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageOverflowCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageMeshletDrawOverflowCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageFarImpostorVisibleCount, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageIndirectMeshletDispatchEnabled, Is.True);
                Assert.That(diagnostics.FoliageInstanceBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageClusterBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageDrawBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageImpostorAtlasBytes, Is.EqualTo(0));
                Assert.That(diagnostics.CpuFoliageBuildMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuFoliageUploadMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageCullMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageDepthMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageForwardMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageShadowMicroseconds, Is.EqualTo(0));
            });
        }

        [Test]
        public void SampleInputController_SimpleDdgiDebugShortcutsStayDocumented()
        {
            string controller = ReadRepoText("NjulfHelloGame", "SampleInputController.cs");
            string program = ReadRepoText("NjulfHelloGame", "Program.cs");
            string reference = ReadRepoText("RendererSettingsReference.md");

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain("WasChordPressed(Key.D, ref _cycleDdgiDebugPressed)"));
                Assert.That(controller,
                    Does.Contain("WasChordPressed(Key.F, ref _toggleDdgiDiagnosticsFilterPressed)"));
                Assert.That(controller,
                    Does.Contain(
                        "ApplyDdgiDiagnosticsCounterState(_getDiagnosticsFilter?.Invoke() ?? SampleDiagnosticsFilter.FullFrame);"));
                Assert.That(controller, Does.Contain("diagnostics.DdgiForwardEstimateCountersEnabled = true;"));
                Assert.That(controller,
                    Does.Contain("DDGI forward estimate counters: enabled for Simple DDGI diagnostics."));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.V, ref _cycleDdgiInvestigationViewPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.P, ref _resetNormalRenderViewPressed)"));
                Assert.That(controller, Does.Contain("ResetNormalRenderView()"));
                Assert.That(controller, Does.Contain("_renderer.Settings.ResetRenderViewOverrides()"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.T, ref _cycleDdgiQualityTierPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.L, ref _toggleDdgiProbeL1MetadataPressed)"));
                Assert.That(controller,
                    Does.Contain("gi.DdgiProbeL1MetadataEnabled = !gi.DdgiProbeL1MetadataEnabled;"));
                Assert.That(controller, Does.Contain("PrintGlobalIlluminationSettings(\"DDGI L1 metadata\")"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.R, ref _printDdgiDiagnosticsPressed)"));
                Assert.That(controller, Does.Contain("ConfigureDdgiOnly(gi)"));
                Assert.That(controller, Does.Contain("DDGI debug legend: {DescribeDdgiDebugView(view)}"));
                Assert.That(controller, Does.Contain("NextDdgiInvestigationDebugView"));
                Assert.That(controller, Does.Contain("CreateScreenshotFileNameSuffix"));
                Assert.That(controller, Does.Contain("-gi-{SanitizeFileNameSegment(giDebugView.ToString())}"));
                Assert.That(controller, Does.Contain("-ddgi-filter"));
                Assert.That(controller, Does.Contain("-full-frame-filter"));
                Assert.That(controller, Does.Contain("_renderer.RequestScreenshot(screenshotPath)"));
                Assert.That(controller, Does.Contain("Screenshot requested: {screenshotPath}"));
                Assert.That(program, Does.Contain("() => diagnosticsReporter.Filter"));
                Assert.That(program, Does.Contain("() => RestoreSceneRenderSettings(renderer)"));
                Assert.That(program, Does.Contain("_performanceScenarioRunner.CurrentScenario"));
                Assert.That(controller, Does.Not.Contain("ApplyDdgiQualityTier(DdgiQualityTier.DdgiMedium);"));
                Assert.That(reference, Does.Contain("`Ctrl+D` | Enable Simple DDGI and cycle its debug view"));
                Assert.That(reference, Does.Contain("`Ctrl+F` | Toggle Simple-DDGI diagnostics console filter"));
                Assert.That(reference, Does.Contain("`Ctrl+V` | Cycle Simple-DDGI investigation views"));
                Assert.That(reference,
                    Does.Contain("`Ctrl+P` | Restore the current scene/scenario's normal render view"));
                Assert.That(reference, Does.Contain("`Ctrl+T` | Cycle Simple-DDGI quality tier"));
                Assert.That(reference, Does.Contain("`Ctrl+L` | Toggle Simple-DDGI compact L1 probe metadata"));
                Assert.That(reference, Does.Contain("`Ctrl+R` | Print Simple-DDGI diagnostics"));
                Assert.That(reference, Does.Contain("category-colored border"));
                Assert.That(reference, Does.Contain("view-id badge"));
                Assert.That(reference, Does.Contain("legend strip"));
                Assert.That(reference, Does.Contain("includes the selected view in the screenshot filename"));
            });
        }

        [Test]
        public void SponzaStartupScenario_AppliesLockedCameraOnLoadAndReload()
        {
            string program = ReadRepoText("NjulfHelloGame", "Program.cs");
            int callCount = program.Split(
                "ApplyPerformanceScenarioCamera(camera,",
                StringSplitOptions.None).Length - 1;

            Assert.Multiple(() =>
            {
                Assert.That(callCount, Is.EqualTo(2));
                Assert.That(program, Does.Contain(
                    "scenario != SamplePerformanceScenario.GiSponzaRightWallStationary"));
                Assert.That(program, Does.Contain(
                    "SampleSponzaGiCaptureContract.Default.LowBookmark"));
                Assert.That(program, Does.Contain("camera.FieldOfView = bookmark.FieldOfView;"));
                Assert.That(program, Does.Contain("camera.NearPlane = bookmark.NearPlane;"));
                Assert.That(program, Does.Contain("camera.FarPlane = bookmark.FarPlane;"));
            });
        }

        [Test]
        public void ProductionSmokeHost_ObservesRealWindowAndRestoresExactQualitySettings()
        {
            string program = ReadRepoText("NjulfHelloGame", "Program.cs");
            string lifecycle = ReadRepoText(
                "NjulfHelloGame",
                "SampleLifecycleSmokeRunner.cs");

            Assert.Multiple(() =>
            {
                Assert.That(
                    program,
                    Does.Contain(
                        "Window.WindowState = Silk.NET.Windowing.WindowState.Minimized;"));
                Assert.That(
                    program,
                    Does.Contain(
                        "Window.Size = new Silk.NET.Maths.Vector2D<int>(width, height);"));
                Assert.That(
                    program,
                    Does.Contain(
                        "Silk.NET.Maths.Vector2D<int> framebufferSize = Window.FramebufferSize;"));
                Assert.That(
                    program,
                    Does.Contain("_smokeRunner?.OnFramebufferMutationObserved("));
                Assert.That(program, Does.Contain("_smokeRunner?.OnUpdate(_drawnFrames);"));
                Assert.That(
                    lifecycle,
                    Does.Contain("minimize-zero-framebuffer"));
                Assert.That(
                    lifecycle,
                    Does.Contain("FramebufferMutationKind.Restore"));
                Assert.That(
                    program,
                    Does.Contain(
                        "SampleRenderSettingsSnapshot.Capture(renderer.Settings)"));
                Assert.That(
                    program,
                    Does.Contain(
                        "() => initialSettings.Restore(renderer.Settings)"));
                Assert.That(
                    program,
                    Does.Contain(
                        "SampleRenderSettingsFingerprint.Capture(renderer.Settings)"));
                Assert.That(
                    program,
                    Does.Contain(
                        "_smokeOptions.Mode is SampleSmokeMode.QualitySwitch or SampleSmokeMode.LongRun"));
            });
        }

        [Test]
        public void DirectionalShadowCasterDiagnostics_CoversFoliageOnlyInDiagnosticVariants()
        {
            string shaderProject = ReadRepoText("Njulf.Shaders", "Njulf.Shaders.csproj");
            string sharedDiagnostics = ReadRepoText(
                "Njulf.Shaders",
                "directional_shadow_caster_diagnostics.glsl");
            string grassMesh = ReadRepoText("Njulf.Shaders", "foliage_grass.mesh");
            string authoredMesh = ReadRepoText("Njulf.Shaders", "foliage_mesh.mesh");
            string foliagePipeline = ReadRepoText(
                "Njulf.Rendering",
                "Pipeline",
                "PipelineObjects",
                "FoliagePipeline.cs");

            Assert.Multiple(() =>
            {
                Assert.That(shaderProject, Does.Contain(
                    "<GpuDiagnosticMeshShader Include=\"foliage_grass.mesh;foliage_mesh.mesh\" />"));
                Assert.That(sharedDiagnostics, Does.Contain(
                    "const uint DIRECTIONAL_SHADOW_CASTER_CLASS_FOLIAGE = 3u;"));
                Assert.That(sharedDiagnostics, Does.Contain(
                    "#if NJULF_GPU_DIAGNOSTIC_COUNTERS"));
                Assert.That(grassMesh, Does.Contain(
                    "DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_FOLIAGE"));
                Assert.That(authoredMesh, Does.Contain(
                    "DIRECTIONAL_SHADOW_CASTER_CLASS_FOLIAGE"));
                Assert.That(foliagePipeline, Does.Contain(
                    "foliage_grass_diagnostics.mesh.spv"));
                Assert.That(foliagePipeline, Does.Contain(
                    "foliage_mesh_diagnostics.mesh.spv"));
            });
        }

        [Test]
        public void DirectionalShadowCasterDiagnostics_RuntimeToggleRecreatesFoliageShadowVariants()
        {
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain(
                    "bool foliageNeedsRecreate = _foliagePipeline != null &&"));
                Assert.That(renderer, Does.Contain(
                    "_foliagePipeline.GpuMeshletCountersEnabled != diagnosticCountersEnabled"));
                Assert.That(renderer, Does.Contain(
                    "if (!meshNeedsRecreate && !foliageNeedsRecreate)"));
                Assert.That(renderer, Does.Contain(
                    "_foliagePipeline!.Recreate("));
                Assert.That(renderer, Does.Contain(
                    "RenderTargetManager.MotionVectorFormat"));
            });
        }

        [Test]
        public void SimpleDdgiReceiverDebugViews_RespectCompiledArtifactCapability()
        {
            bool detailedCompiled =
                RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled;
            bool visualViewsCompiled =
                RendererBuildFeatures.DdgiVisualDebugViewsCompiled;

            Assert.Multiple(() =>
            {
                Assert.That(
                    RendererBuildFeatures.RequiresDetailedDdgiReceiverDiagnostics(
                        GlobalIlluminationDebugView.DdgiIrradiance),
                    Is.True);
                Assert.That(
                    RendererBuildFeatures.RequiresDetailedDdgiReceiverDiagnostics(
                        GlobalIlluminationDebugView.DdgiSourceCacheRadiance),
                    Is.True);
                Assert.That(
                    RendererBuildFeatures.SourceCacheRadianceReceiverDiagnosticCompiled,
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.ExtendedProbeMetadataReceiverDiagnosticsCompiled,
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(
                        GlobalIlluminationDebugView.DdgiSourceCacheRadiance),
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.ResolveGlobalIlluminationDebugView(
                        GlobalIlluminationDebugView.DdgiSourceCacheRadiance),
                    Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(
                    RendererBuildFeatures.GetGlobalIlluminationDebugViewAvailabilityReason(
                        GlobalIlluminationDebugView.DdgiSourceCacheRadiance),
                    Does.Contain("compute-projected"));
                Assert.That(
                    RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(
                        GlobalIlluminationDebugView.DdgiUpdateReasons),
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(
                        GlobalIlluminationDebugView.DdgiClassificationInvalidScore),
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.GetGlobalIlluminationDebugViewAvailabilityReason(
                        GlobalIlluminationDebugView.DdgiUpdateReasons),
                    Does.Contain("compact Simple-DDGI receiver payload"));
                Assert.That(
                    RendererBuildFeatures.RequiresDetailedDdgiReceiverDiagnostics(
                        GlobalIlluminationDebugView.FinalIndirect),
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.RequiresDetailedDdgiReceiverDiagnostics(
                        GlobalIlluminationDebugView.FarFieldTraceResult),
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.RequiresDetailedDdgiReceiverDiagnostics(
                        GlobalIlluminationDebugView.MaterialTransportHitProvenance),
                    Is.False);
                Assert.That(
                    RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(
                        GlobalIlluminationDebugView.DdgiProbeState),
                    Is.EqualTo(visualViewsCompiled));
                Assert.That(
                    RendererBuildFeatures.ResolveGlobalIlluminationDebugView(
                        GlobalIlluminationDebugView.DdgiProbeState),
                    Is.EqualTo(visualViewsCompiled
                        ? GlobalIlluminationDebugView.DdgiProbeState
                        : GlobalIlluminationDebugView.None));
                Assert.That(
                    !detailedCompiled || visualViewsCompiled,
                    Is.True,
                    "Detailed counter artifacts must also retain visual GI views.");
                Assert.That(
                    RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(
                        GlobalIlluminationDebugView.FarFieldSunShadow),
                    Is.True);
            });
        }

        [Test]
        public void SimpleDdgiEnergyEvidenceLuminanceDecode_ReservedZeroAndLogEndpointsAreExact()
        {
            float previous = -1.0f;
            for (uint code = 0; code <= 2047; code++)
            {
                float decoded = RendererDiagnosticsBuffer.DecodeSimpleDdgiEnergyEvidenceLuminance(code);
                Assert.That(decoded, Is.GreaterThanOrEqualTo(previous), $"code {code}");
                previous = decoded;
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    RendererDiagnosticsBuffer.DecodeSimpleDdgiEnergyEvidenceLuminance(0),
                    Is.Zero);
                Assert.That(
                    RendererDiagnosticsBuffer.DecodeSimpleDdgiEnergyEvidenceLuminance(1),
                    Is.Zero);
                Assert.That(
                    RendererDiagnosticsBuffer.DecodeSimpleDdgiEnergyEvidenceLuminance(2047),
                    Is.EqualTo(64.0f).Within(0.0001f));
                Assert.That(
                    RendererDiagnosticsBuffer.DecodeSimpleDdgiEnergyEvidenceLuminance(uint.MaxValue),
                    Is.EqualTo(64.0f).Within(0.0001f));
            });
        }

        [Test]
        public void BenchmarkHost_PinsDeferredAsyncAndAdaptiveDdgiScheduling()
        {
            string program = ReadRepoText("NjulfHelloGame", "Program.cs");
            string parser = ReadRepoText("NjulfHelloGame", "SampleSmokeOptionsParser.cs");

            Assert.Multiple(() =>
            {
                Assert.That(program, Does.Contain(
                    "renderer.Settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled = false;"));
                Assert.That(parser, Does.Contain(
                    "asyncComputeModeOverride = AsyncComputeMode.Disabled;"));
                Assert.That(parser, Does.Contain("if (!asyncComputeModeOverride.HasValue)"));
            });
        }

        [Test]
        public void BenchmarkHost_UsesDeterministicSimulationStep()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    HelloGame.ResolveSimulationDeltaTime(
                        0.004f,
                        benchmarkEnabled: true),
                    Is.EqualTo(1.0f / 60.0f));
                Assert.That(
                    HelloGame.ResolveSimulationDeltaTime(
                        0.004f,
                        benchmarkEnabled: false),
                    Is.EqualTo(0.004f));
            });
        }

        [Test]
        public void SampleInputController_DebugSnapshotShortcutStaysDocumented()
        {
            string controller = ReadRepoText("NjulfHelloGame", "SampleInputController.cs");
            string reference = ReadRepoText("RendererSettingsReference.md");

            Assert.Multiple(() =>
            {
                Assert.That(controller,
                    Does.Contain("WasChordPressed(Key.Keypad0, ref _requestDiagnosticSnapshotPressed)"));
                Assert.That(controller, Does.Contain("RequestDiagnosticSnapshot()"));
                Assert.That(controller,
                    Does.Contain("Path.Combine(AppContext.BaseDirectory, \"DiagnosticSnapshots\")"));
                Assert.That(controller,
                    Does.Contain("ExportPerformanceSnapshotFile(directory, \"Diagnostic output\")"));
                Assert.That(controller, Does.Contain("Path.ChangeExtension(diagnosticsPath, \".png\")"));
                Assert.That(controller, Does.Contain("_requestDiagnosticScreenshotCapture?.Invoke(screenshotPath)"));
                Assert.That(controller, Does.Contain("Diagnostic snapshot requested: cpuSnapshots=on"));
                Assert.That(reference, Does.Contain("with matching base filenames in `DiagnosticSnapshots`"));
            });
        }

        [Test]
        public void GlobalIlluminationQualityFeatureSwitches_DefaultOnAndDebugIdsStayMapped()
        {
            var settings = new RenderSettings();
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
            string ddgiFrames = ReadRepoText(
                "Njulf.Rendering",
                "Resources",
                "SimpleDdgiFrameCoordinator.cs");

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.SimpleDdgiFogEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiParticlesEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiRoughSpecularEnabled, Is.True);
                Assert.That(
                    settings.GlobalIllumination.EffectiveSimpleDdgiGlossyTransportMode,
                    Is.EqualTo(SimpleDdgiGlossyTransportMode.ReceiverOnly));
                Assert.That(settings.GlobalIllumination.SimpleDdgiStructuredGatherEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiReducedBlendEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold,
                    Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.SimpleDdgiToroidalScrollingEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiRegionalInvalidationEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.FarFieldSkyVisibilityEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.FarFieldSunShadowEnabled, Is.True);
                Assert.That((uint)GlobalIlluminationDebugView.FarFieldSkyVisibility, Is.EqualTo(44u));
                Assert.That((uint)GlobalIlluminationDebugView.FarFieldSunShadow, Is.EqualTo(45u));
                Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.FarFieldSkyVisibility => 122u"));
                Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.FarFieldSunShadow => 123u"));
                Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiDirectionalSupport => 124u"));
                Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiSourceCacheRadiance => 125u"));
                Assert.That(
                    ddgiFrames,
                    Does.Contain("ResolveReflectionRecaptureIntent("));
                Assert.That(
                    ddgiFrames,
                    Does.Contain("Reason: \"ddgi-ready\""));
                Assert.That(
                    renderer,
                    Does.Contain(
                        "_reflectionProbeManager?.RequestRecaptureAll(intent.Reason)"));
                Assert.That(
                    ddgiFrames,
                    Does.Not.Contain(
                        "simple-ddgi-dirty"),
                    "a dirty edge can expose a partially propagated cubemap source");
            });
        }

        [Test]
        public void ReflectionProbeCaptureContract_RequiresExplicitRenderingBeforePublication()
        {
            string manager = ReadRepoText("Njulf.Rendering", "Resources", "ReflectionProbeManager.cs");

            Assert.Multiple(() =>
            {
                Assert.That(manager, Does.Contain("public void RequestRecaptureAll(string reason)"));
                Assert.That(manager, Does.Contain("public bool TryBeginCapture(out ReflectionProbeCapture capture)"));
                Assert.That(manager, Does.Contain("public void PublishCapture(in ReflectionProbeCapture capture)"));
                Assert.That(manager, Does.Contain("_capturedProbeIds.Add(capture.ProbeId);"));
                Assert.That(manager, Does.Contain("_captureFrameCounters.RecordCompletedCapture();"));
                Assert.That(manager, Does.Not.Contain("DrainCaptureQueue"));
            });
        }

        [Test]
        public void DirectionalDdgiReflectionDebugViews_ExposeLobeAndNormalizedOwnership()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string controller = ReadRepoText(
                "NjulfHelloGame",
                "SampleInputController.cs");

            Assert.Multiple(() =>
            {
                Assert.That(
                    (uint)ReflectionDebugView.DdgiDirectionalRadianceLobe,
                    Is.EqualTo(11u));
                Assert.That(
                    (uint)ReflectionDebugView.SourceOwnership,
                    Is.EqualTo(12u));
                Assert.That(
                    (uint)ReflectionDebugView.DetailBudget,
                    Is.EqualTo(15u));
                Assert.That(
                    (uint)ReflectionDebugView.ReceiverMaterial,
                    Is.EqualTo(16u));
                Assert.That(
                    (uint)ReflectionDebugView.RoughnessInputs,
                    Is.EqualTo(17u));
                Assert.That(
                    shader,
                    Does.Contain(
                        "REFLECTION_DEBUG_DDGI_DIRECTIONAL_RADIANCE_LOBE = 11u"));
                Assert.That(
                    shader,
                    Does.Contain("REFLECTION_DEBUG_SOURCE_OWNERSHIP = 12u"));
                Assert.That(
                    shader,
                    Does.Contain(
                        "debugColor = vec3(localWeight, ddgiWeight, globalWeight)"));
                Assert.That(
                    shader,
                    Does.Contain(
                        "debugColor = vec3(0.0, ddgiWeight, 1.0 - ddgiWeight)"));
                Assert.That(
                    controller,
                    Does.Contain(
                        "ReflectionDebugView.GlobalFallbackOnly => ReflectionDebugView.DdgiDirectionalRadianceLobe"));
                Assert.That(
                    controller,
                    Does.Contain(
                        "ReflectionDebugView.DdgiDirectionalRadianceLobe => ReflectionDebugView.SourceOwnership"));
                Assert.That(
                    controller,
                    Does.Contain(
                        "ReflectionDebugView.SourceSelection => ReflectionDebugView.DetailBudget"));
                Assert.That(
                    controller,
                    Does.Contain(
                        "ReflectionDebugView.DetailBudget => ReflectionDebugView.ReceiverMaterial"));
                Assert.That(
                    controller,
                    Does.Contain(
                        "ReflectionDebugView.ReceiverMaterial => ReflectionDebugView.RoughnessInputs"));
            });
        }

        [Test]
        public void ReflectionSpecular_UsesProbeMipRangeAndAntialiasesCapturedHighlights()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string prefilter = ReadRepoText(
                "Njulf.Shaders",
                "reflection_probe_prefilter.comp");

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain(
                    "float probeMaxLod = max(float(header.ProbeMipCount) - 1.0, 0.0);"));
                Assert.That(shader, Does.Contain(
                    "? mix(1.0, probeMaxLod, roughness)"));
                Assert.That(shader, Does.Contain(
                    "EstimateReflectionSchedulingRoughness("));
                Assert.That(shader, Does.Not.Contain(
                    "roughness = FilterSpecularRoughness(roughness, normal);"));
                Assert.That(prefilter, Does.Contain(
                    "float medianNeighbour = 0.5 *"));
                Assert.That(prefilter, Does.Contain(
                    "center *= luminanceLimit / max(centerLuminance, 0.000001);"));
            });
        }

        [Test]
        public void ReflectionCapture_RebindsMeshDescriptorsAfterSkyboxLayout()
        {
            string forwardPass = ReadRepoText(
                "Njulf.Rendering",
                "Pipeline",
                "ForwardPlusPass.cs");
            int skybox = forwardPass.IndexOf(
                "RecordReflectionSkybox(cmd, view);",
                StringComparison.Ordinal);
            int meshRebind = forwardPass.IndexOf(
                "BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);",
                skybox,
                StringComparison.Ordinal);
            int firstBucket = forwardPass.IndexOf(
                "DrawForwardBucket(",
                skybox,
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(skybox, Is.GreaterThanOrEqualTo(0));
                Assert.That(meshRebind, Is.GreaterThan(skybox));
                Assert.That(firstBucket, Is.GreaterThan(meshRebind));
            });
        }

        [Test]
        public void DdgiReceiverDebugViews_DoNotPresentHealthyCandidateRejectionsOrIdleUpdatesAsFailures()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

            Assert.Multiple(() =>
            {
                Assert.That(
                    shader,
                    Does.Contain("ddgiSample.supportCoverage <= 0.000001"));
                Assert.That(
                    shader,
                    Does.Contain("updateReason != 0u")
                        .And.Contain("vec3(0.0)"));
                Assert.That(
                    shader,
                    Does.Contain("ddgiSample.relocation = simpleDebug.relocation"));
            });
        }

        [Test]
        public void ForwardDebugViews_PreserveGeometryDecalCoverage()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string normalized = shader.Replace("\r\n", "\n");
            int genericMain = normalized.IndexOf(
                "#else\nvoid main()",
                StringComparison.Ordinal);
            Assert.That(genericMain, Is.GreaterThanOrEqualTo(0));
            string genericForward = normalized[genericMain..];

            Assert.Multiple(() =>
            {
                Assert.That(
                    shader,
                    Does.Contain("WriteForwardColor(vec4(reflectionDebugColor, forwardDebugOutputAlpha));"));
                Assert.That(
                    shader,
                    Does.Contain("WriteForwardColor(vec4(finalDiffuseIndirect, forwardDebugOutputAlpha));"));
                Assert.That(
                    genericForward,
                    Does.Not.Contain("WriteForwardColor(vec4(reflectionDebugColor, 1.0));"));
                Assert.That(
                    genericForward,
                    Does.Not.Contain("WriteForwardColor(vec4(finalDiffuseIndirect, 1.0));"));
            });
        }

        [Test]
        public void SampleInputController_DebugOverlayShortcutEnablesDebugDrawList()
        {
            string controller = ReadRepoText("NjulfHelloGame", "SampleInputController.cs");

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain("WasChordPressed(Key.Keypad9, ref _cycleDebugOverlayPressed)"));
                Assert.That(controller, Does.Contain("_renderer.Settings.Debug.Enabled = true;"));
                Assert.That(controller, Does.Contain("_renderer.DebugDraw.Enabled = true;"));
                Assert.That(controller, Does.Contain("DebugOverlayCatalog.Next("));
                Assert.That(controller, Does.Contain("bool reverse = IsShiftDown();"));
                Assert.That(controller, Does.Not.Contain("NextDebugOverlay("));
            });
        }

        [Test]
        public void DebugOverlayMode_DdgiModesAppendAfterExistingModes()
        {
            Assert.Multiple(() =>
            {
                Assert.That((uint)DebugOverlayMode.ReflectionProbeVolumes, Is.EqualTo(3u));
                Assert.That((uint)DebugOverlayMode.DdgiProbeVolumes, Is.EqualTo(4u));
                Assert.That((uint)DebugOverlayMode.GpuMemory, Is.EqualTo(11u));
                Assert.That((uint)DebugOverlayMode.DdgiProbeActivity, Is.EqualTo(12u));
                Assert.That((uint)DebugOverlayMode.DdgiUpdatedProbes, Is.EqualTo(13u));
                Assert.That((uint)DebugOverlayMode.DdgiProbeRelocation, Is.EqualTo(14u));
                Assert.That((uint)DebugOverlayMode.DdgiProbeAge, Is.EqualTo(15u));
                Assert.That((uint)DebugOverlayMode.DdgiPhysicalSlots, Is.EqualTo(16u));
                Assert.That((uint)DebugOverlayMode.DdgiCascadeBounds, Is.EqualTo(17u));
                Assert.That((uint)DebugOverlayMode.DdgiNewlyExposedCells, Is.EqualTo(18u));
                Assert.That((uint)DebugOverlayMode.DdgiFrustumPriority, Is.EqualTo(19u));
                Assert.That((uint)DebugOverlayMode.DdgiSafetyRefresh, Is.EqualTo(20u));
                Assert.That((uint)DebugOverlayMode.DdgiCascadeBlend, Is.EqualTo(21u));
                Assert.That((uint)DebugOverlayMode.DdgiUpdateReasons, Is.EqualTo(22u));
                Assert.That((uint)DebugOverlayMode.DdgiProbeSpheres, Is.EqualTo(23u));
            });
        }

        [Test]
        public void DdgiProbeDebugMarkerSampling_DistributesMarkersAcrossCameraClipmapVolume()
        {
            DebugOverlayBuilder.DdgiProbeMarkerSampling sampling =
                DebugOverlayBuilder.CalculateDdgiProbeMarkerSampling(24, 8, 24, 512);
            int markerCount = 0;
            int negativeXNegativeZ = 0;
            int negativeXPositiveZ = 0;
            int positiveXNegativeZ = 0;
            int positiveXPositiveZ = 0;
            int distinctX = 0;
            int distinctY = 0;
            int distinctZ = 0;

            for (int z = 0; z < 24; z++)
            {
                bool zUsed = false;
                for (int y = 0; y < 8; y++)
                {
                    bool yUsed = false;
                    for (int x = 0; x < 24; x++)
                    {
                        if (!DebugOverlayBuilder.ShouldDrawDdgiProbeMarker(x, y, z, sampling))
                            continue;

                        markerCount++;
                        yUsed = true;
                        zUsed = true;
                        if (y == 0)
                            distinctX++;
                        if (x < 12 && z < 12)
                            negativeXNegativeZ++;
                        else if (x < 12)
                            negativeXPositiveZ++;
                        else if (z < 12)
                            positiveXNegativeZ++;
                        else
                            positiveXPositiveZ++;
                    }

                    if (yUsed && z == 0)
                        distinctY++;
                }

                if (zUsed)
                    distinctZ++;
            }

            Assert.Multiple(() =>
            {
                Assert.That(markerCount, Is.LessThanOrEqualTo(512));
                Assert.That(distinctX, Is.GreaterThan(4));
                Assert.That(distinctY, Is.GreaterThan(1));
                Assert.That(distinctZ, Is.GreaterThan(4));
                Assert.That(negativeXNegativeZ, Is.GreaterThan(0));
                Assert.That(negativeXPositiveZ, Is.GreaterThan(0));
                Assert.That(positiveXNegativeZ, Is.GreaterThan(0));
                Assert.That(positiveXPositiveZ, Is.GreaterThan(0));
            });
        }

        [Test]
        public void DdgiProbeDebugMarkerBudget_IsSharedAcrossRemainingVolumes()
        {
            int remainingMarkers = 768;
            int remainingVolumes = 4;
            var allocations = new int[remainingVolumes];

            for (int i = 0; i < allocations.Length; i++)
            {
                int allocation = DebugOverlayBuilder.CalculateDdgiProbeMarkerBudget(
                    remainingMarkers,
                    remainingVolumes);
                allocations[i] = allocation;
                remainingMarkers -= allocation;
                remainingVolumes--;
            }

            Assert.Multiple(() =>
            {
                Assert.That(allocations, Is.EqualTo(new[] { 192, 192, 192, 192 }));
                Assert.That(remainingMarkers, Is.Zero);
                Assert.That(DebugOverlayBuilder.CalculateDdgiProbeMarkerBudget(0, 4), Is.Zero);
                Assert.That(DebugOverlayBuilder.CalculateDdgiProbeMarkerBudget(64, 0), Is.Zero);
            });
        }

        [Test]
        public void SelectedObjectInspection_DecodesMaterialRenderModeAndPbrValues()
        {
            var material = new GPUMaterialData
            {
                Albedo = new Vector4(0.8f, 0.7f, 0.6f, 1.0f),
                Emissive = new Vector4(0.1f, 0.2f, 0.3f, 1.0f),
                NormalScaleBias = new Vector4(0.75f, MaterialRenderModeExtensions.BlendCode, 0.0f, 0.0f),
                MetallicRoughnessAO = new Vector4(1.0f, 0.42f, 0.9f, 0.0f),
                AlbedoTextureIndex = 10,
                NormalTextureIndex = 11,
                MetallicRoughnessTextureIndex = 12,
                EmissiveTextureIndex = 13
            };

            MaterialInspectionResult result = MaterialInspectionResult.FromGpuMaterial(7, material);

            Assert.Multiple(() =>
            {
                Assert.That(result.MaterialIndex, Is.EqualTo(7));
                Assert.That(result.RenderMode, Is.EqualTo(MaterialRenderMode.Blend));
                Assert.That(result.Metallic, Is.EqualTo(1.0f));
                Assert.That(result.Roughness, Is.EqualTo(0.42f));
                Assert.That(result.AmbientOcclusion, Is.EqualTo(0.9f));
                Assert.That(result.NormalStrength, Is.EqualTo(0.75f));
                Assert.That(result.AlbedoTextureIndex, Is.EqualTo(10));
                Assert.That(result.NormalTextureIndex, Is.EqualTo(11));
                Assert.That(result.MetallicRoughnessTextureIndex, Is.EqualTo(12));
                Assert.That(result.EmissiveTextureIndex, Is.EqualTo(13));
            });
        }

        [Test]
        public void GpuTimingSnapshot_MissingPassReturnsUnavailable()
        {
            FrameTimingSnapshot snapshot = FrameTimingSnapshot.Empty;

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.TryGetPass("ForwardPlusPass", out _), Is.False);
                Assert.That(snapshot.GetGpuMicrosecondsOrZero("ForwardPlusPass"), Is.EqualTo(0));
            });
        }

        [Test]
        public void GpuTimingSnapshot_ConvertsTimestampDeltaToMicroseconds()
        {
            long microseconds = FrameTimingSnapshot.ConvertTimestampDeltaToMicroseconds(
                start: 100,
                end: 350,
                timestampPeriodNanoseconds: 4.0f);

            Assert.That(microseconds, Is.EqualTo(1));
        }

        [Test]
        public void GpuTimingSnapshot_SubMicrosecondDeltaRemainsAvailable()
        {
            bool available = FrameTimingSnapshot.TryConvertTimestampDeltaToMicroseconds(
                start: 100,
                end: 101,
                timestampPeriodNanoseconds: 1.0f,
                out long microseconds);

            Assert.Multiple(() =>
            {
                Assert.That(available, Is.True);
                Assert.That(microseconds, Is.Zero);
            });
        }

        [TestCase(100UL, 100UL, 1.0f)]
        [TestCase(101UL, 100UL, 1.0f)]
        [TestCase(100UL, 101UL, 0.0f)]
        public void GpuTimingSnapshot_InvalidDeltaIsUnavailable(
            ulong start,
            ulong end,
            float timestampPeriodNanoseconds)
        {
            bool available = FrameTimingSnapshot.TryConvertTimestampDeltaToMicroseconds(
                start,
                end,
                timestampPeriodNanoseconds,
                out long microseconds);

            Assert.Multiple(() =>
            {
                Assert.That(available, Is.False);
                Assert.That(microseconds, Is.Zero);
            });
        }

        [Test]
        public void FrameCaptureRequest_DefaultPathIsStableAndUnique()
        {
            ScreenshotRequest first = ScreenshotRequest.CreateDefault();
            Thread.Sleep(2);
            ScreenshotRequest second = ScreenshotRequest.CreateDefault();

            Assert.Multiple(() =>
            {
                Assert.That(first.OutputPath, Does.Contain("Screenshots"));
                Assert.That(first.OutputPath, Does.EndWith(".png"));
                Assert.That(first.ColorSpace, Is.EqualTo(ScreenshotColorSpace.FinalLdrSrgb));
                Assert.That(second.OutputPath, Is.Not.EqualTo(first.OutputPath));
            });
        }

        [Test]
        public void AmbientOcclusionRenderTargetProfile_TracksConfiguredResolutionScale()
        {
            string targets = ReadRepoText("Njulf.Rendering", "Resources", "RenderTargetManager.cs");
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

            Assert.Multiple(() =>
            {
                Assert.That(
                    targets,
                    Does.Contain("CalculateAmbientOcclusionExtent(extent, ambientOcclusionResolutionScale)"));
                Assert.That(
                    targets,
                    Does.Not.Contain("CalculateAmbientOcclusionExtent(extent, 0.5f)"));
                Assert.That(
                    renderer.ReplaceLineEndings("\n"),
                    Does.Contain(
                        "Settings.AmbientOcclusion.ResolutionScale,\n                Settings.AntiAliasing.EffectiveMode"));
                Assert.That(
                    renderer,
                    Does.Contain("_lastAmbientOcclusionResolutionScale - ambientOcclusionResolutionScale"));
                Assert.That(
                    renderer.Split(
                        "_lastAmbientOcclusionResolutionScale = Settings.AmbientOcclusion.ResolutionScale;",
                        StringSplitOptions.None),
                    Has.Length.EqualTo(3));
            });
        }

        private static string ReadRepoText(params string[] pathParts)
        {
            string? directory = TestContext.CurrentContext.TestDirectory;
            while (directory != null)
            {
                string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = Directory.GetParent(directory)?.FullName;
            }

            Assert.Fail($"Could not find repo file '{Path.Combine(pathParts)}'.");
            return string.Empty;
        }
    }
}
