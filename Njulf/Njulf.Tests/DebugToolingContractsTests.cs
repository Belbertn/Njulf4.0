using System.Threading;
using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
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
            var families = new (string Name, int Start, int Count)[]
            {
                ("meshlet", 0, RendererDiagnosticsBuffer.MeshletCounterCount),
                ("DDGI forward estimate", RendererDiagnosticsBuffer.DdgiForwardEstimateCounterBase, RendererDiagnosticsBuffer.DdgiForwardEstimateCounterCount),
                ("DDGI trace energy", RendererDiagnosticsBuffer.DdgiTraceEnergyCounterBase, RendererDiagnosticsBuffer.DdgiTraceEnergyCounterCount),
                ("DDGI trace early-out", RendererDiagnosticsBuffer.DdgiTraceEarlyOutCounterBase, RendererDiagnosticsBuffer.DdgiTraceEarlyOutCounterCount),
                ("DDGI blend energy", RendererDiagnosticsBuffer.DdgiBlendEnergyCounterBase, RendererDiagnosticsBuffer.DdgiBlendEnergyCounterCount),
                ("DDGI ring mismatch", RendererDiagnosticsBuffer.DdgiTraceRingMismatchSampleBase, RendererDiagnosticsBuffer.DdgiTraceRingMismatchSampleCount),
                ("far field", RendererDiagnosticsBuffer.FarFieldCounterBase, RendererDiagnosticsBuffer.FarFieldCounterCount),
                ("DDGI investigation", RendererDiagnosticsBuffer.DdgiInvestigationCounterBase, RendererDiagnosticsBuffer.DdgiInvestigationCounterCount),
                ("simple DDGI transport", RendererDiagnosticsBuffer.SimpleDdgiTransportCounterBase, RendererDiagnosticsBuffer.SimpleDdgiTransportCounterCount),
                ("directional shadow receiver", RendererDiagnosticsBuffer.DirectionalShadowReceiverCounterBase, RendererDiagnosticsBuffer.DirectionalShadowReceiverCounterCount),
                ("far-field material V2", RendererDiagnosticsBuffer.FarFieldMaterialV2CounterBase, RendererDiagnosticsBuffer.FarFieldMaterialV2CounterCount),
                ("material GI", RendererDiagnosticsBuffer.MaterialGiCounterBase, RendererDiagnosticsBuffer.MaterialGiCounterCount),
                ("simple DDGI gather rejection", RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionCounterBase, RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionCounterCount),
                ("simple DDGI gather all failed", RendererDiagnosticsBuffer.SimpleDdgiGatherAllFailedCounterBase, RendererDiagnosticsBuffer.SimpleDdgiGatherAllFailedCounterCount)
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
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeGatherCounterCount, Is.EqualTo(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumePrimaryGatherCounterBase, Is.EqualTo(RendererDiagnosticsBuffer.DdgiInvestigationCounterBase + RendererDiagnosticsBuffer.DdgiInvestigationFixedCounterCount));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiVolumeSampledGatherCounterBase, Is.EqualTo(RendererDiagnosticsBuffer.SimpleDdgiVolumePrimaryGatherCounterBase + RendererDiagnosticsBuffer.SimpleDdgiVolumeGatherCounterCount));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiTransportCounterCount, Is.EqualTo(6));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowReceiverCascadeCount, Is.EqualTo(ShadowSettings.MaxDirectionalCascades));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowReceiverCounterFamilyCount, Is.EqualTo(16));
                Assert.That(RendererDiagnosticsBuffer.DirectionalShadowReceiverDepthQuantizationScale, Is.EqualTo(65535.0f));
                Assert.That(RendererDiagnosticsBuffer.FarFieldMaterialV2CounterCount, Is.EqualTo(2));
                Assert.That(RendererDiagnosticsBuffer.MaterialGiCounterCount, Is.EqualTo(10));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiGatherRejectionReasonCount, Is.EqualTo(9));
                Assert.That(RendererDiagnosticsBuffer.SimpleDdgiGatherRoleCount, Is.EqualTo(3));
                Assert.That(RendererDiagnosticsBuffer.CounterCount, Is.EqualTo(nextExpectedStart));
                Assert.That(RendererDiagnosticsBuffer.CounterBufferSize, Is.EqualTo((ulong)nextExpectedStart * sizeof(uint)));
            });
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
                Assert.That(diagnostics.DdgiTraceEnergySampleCount, Is.EqualTo(0u));
                Assert.That(diagnostics.DdgiTraceEnergyRayLuminanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiTraceEnergyDirectLuminanceAverage, Is.EqualTo(0.0f));
                Assert.That(diagnostics.DdgiTraceEnergyDirectNoShadowLuminanceAverage, Is.EqualTo(0.0f));
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
                Assert.That(diagnostics.DdgiCameraMovementClass, Is.EqualTo(DdgiCameraMovementClass.None));
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
        public void SampleInputController_DdgiDebugShortcutsStayDocumented()
        {
            string controller = ReadRepoText("NjulfHelloGame", "SampleInputController.cs");
            string program = ReadRepoText("NjulfHelloGame", "Program.cs");
            string reference = ReadRepoText("RendererSettingsReference.md");

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain("WasChordPressed(Key.D, ref _cycleDdgiDebugPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.F, ref _toggleDdgiDiagnosticsFilterPressed)"));
                Assert.That(controller, Does.Contain("ApplyDdgiDiagnosticsCounterState(_getDiagnosticsFilter?.Invoke() ?? SampleDiagnosticsFilter.FullFrame);"));
                Assert.That(controller, Does.Contain("diagnostics.DdgiForwardEstimateCountersEnabled = true;"));
                Assert.That(controller, Does.Contain("DDGI forward estimate counters: enabled for DDGI-only diagnostics."));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.V, ref _cycleDdgiInvestigationViewPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.P, ref _resetNormalRenderViewPressed)"));
                Assert.That(controller, Does.Contain("ResetNormalRenderView()"));
                Assert.That(controller, Does.Contain("_renderer.Settings.ResetRenderViewOverrides()"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.T, ref _cycleDdgiQualityTierPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.L, ref _toggleDdgiProbeL1MetadataPressed)"));
                Assert.That(controller, Does.Contain("gi.DdgiProbeL1MetadataEnabled = !gi.DdgiProbeL1MetadataEnabled;"));
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
                Assert.That(reference, Does.Contain("`Ctrl+D` | Cycle DDGI-only debug view"));
                Assert.That(reference, Does.Contain("`Ctrl+F` | Toggle DDGI-only diagnostics console filter"));
                Assert.That(reference, Does.Contain("`Ctrl+V` | Cycle DDGI investigation views"));
                Assert.That(reference, Does.Contain("`Ctrl+P` | Restore the current scene/scenario's normal render view"));
                Assert.That(reference, Does.Contain("`Ctrl+T` | Cycle DDGI quality tier"));
                Assert.That(reference, Does.Contain("`Ctrl+L` | Toggle DDGI compact L1 probe metadata"));
                Assert.That(reference, Does.Contain("`Ctrl+R` | Print DDGI diagnostics"));
                Assert.That(reference, Does.Contain("category-colored screen border"));
                Assert.That(reference, Does.Contain("top-left checker/binary view-id badge"));
                Assert.That(reference, Does.Contain("bottom-left RGB legend strip"));
                Assert.That(reference, Does.Contain("-gi-DdgiSupportCoverage-ddgi-filter.png"));
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
        public void SampleInputController_DebugSnapshotShortcutStaysDocumented()
        {
            string controller = ReadRepoText("NjulfHelloGame", "SampleInputController.cs");
            string reference = ReadRepoText("RendererSettingsReference.md");

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain("WasChordPressed(Key.Keypad0, ref _requestDiagnosticSnapshotPressed)"));
                Assert.That(controller, Does.Contain("RequestDiagnosticSnapshot()"));
                Assert.That(controller, Does.Contain("Path.Combine(AppContext.BaseDirectory, \"DiagnosticSnapshots\")"));
                Assert.That(controller, Does.Contain("ExportPerformanceSnapshotFile(directory, \"Diagnostic output\")"));
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

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.SimpleDdgiFogEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiParticlesEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiRoughSpecularEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.SimpleDdgiStructuredGatherEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiReducedBlendEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(0.95f));
                Assert.That(settings.GlobalIllumination.SimpleDdgiToroidalScrollingEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiRegionalInvalidationEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.FarFieldSkyVisibilityEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.FarFieldSunShadowEnabled, Is.True);
                Assert.That((uint)GlobalIlluminationDebugView.FarFieldSkyVisibility, Is.EqualTo(44u));
                Assert.That((uint)GlobalIlluminationDebugView.FarFieldSunShadow, Is.EqualTo(45u));
                Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.FarFieldSkyVisibility => 122u"));
                Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.FarFieldSunShadow => 123u"));
                Assert.That(renderer, Does.Contain("ScheduleReflectionProbeRecapturesFromGi(sceneData, ddgiActive, simpleDdgiActive);"));
                Assert.That(renderer, Does.Contain("_reflectionProbeManager.RequestRecaptureAll(\"ddgi-ready\")"));
                Assert.That(renderer, Does.Contain("_reflectionProbeManager.RequestRecaptureAll(\"simple-ddgi-dirty\")"));
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
                Assert.That(manager.Split("_capturesCompletedThisFrame++", StringSplitOptions.None), Has.Length.EqualTo(2));
                Assert.That(manager, Does.Not.Contain("DrainCaptureQueue"));
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
                Assert.That(controller, Does.Contain("_renderer.Settings.Debug.Mode = NextDebugOverlay(_renderer.Settings.Debug.Mode);"));
            });
        }

        [Test]
        public void VulkanRenderer_DebugOverlaySupportsSimpleDdgiProbeVolume()
        {
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain("DrawSimpleDdgiProbeVolumeOverlay(sceneData, depthMode, remainingDetailedProbeMarkers);"));
                Assert.That(renderer, Does.Contain("Settings.GlobalIllumination.EffectiveUseSimpleDdgi"));
                Assert.That(renderer, Does.Contain("_simpleDdgiVolumeManager.ProbeCount <= 0"));
                Assert.That(renderer, Does.Contain("ReadOnlySpan<GPUSimpleDdgiVolume> volumes = _simpleDdgiVolumeManager.LastVolumes;"));
                Assert.That(renderer, Does.Contain("for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)"));
                Assert.That(renderer, Does.Contain("ResolveSimpleDdgiVolumeDebugColor(volumeIndex, volume)"));
                Assert.That(renderer, Does.Contain("sceneData.DebugDdgiProbeVolumesDrawn++;"));
                Assert.That(renderer, Does.Contain("_simpleDdgiVolumeManager?.IsProbeScheduledForUpdate(probeIndex) == true"));
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
            });
        }

        [Test]
        public void DdgiProbeDebugMarkerSampling_DistributesMarkersAcrossCameraClipmapVolume()
        {
            VulkanRenderer.DdgiProbeMarkerSampling sampling =
                VulkanRenderer.CalculateDdgiProbeMarkerSampling(24, 8, 24, 512);
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
                        if (!VulkanRenderer.ShouldDrawDdgiProbeMarker(x, y, z, sampling))
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
                int allocation = VulkanRenderer.CalculateDdgiProbeMarkerBudget(
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
                Assert.That(VulkanRenderer.CalculateDdgiProbeMarkerBudget(0, 4), Is.Zero);
                Assert.That(VulkanRenderer.CalculateDdgiProbeMarkerBudget(64, 0), Is.Zero);
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
                    renderer,
                    Does.Contain("Settings.AmbientOcclusion.ResolutionScale,\n                ssgiTargetEnabled"));
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
