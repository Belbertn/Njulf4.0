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
            string reference = ReadRepoText("RendererSettingsReference.md");

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain("WasChordPressed(Key.D, ref _cycleDdgiDebugPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.P, ref _applyDdgiProductionProfilePressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.T, ref _cycleDdgiQualityTierPressed)"));
                Assert.That(controller, Does.Contain("WasChordPressed(Key.R, ref _printDdgiDiagnosticsPressed)"));
                Assert.That(controller, Does.Contain("ConfigureDdgiOnly(gi)"));
                Assert.That(controller, Does.Not.Contain("ApplyDdgiQualityTier(DdgiQualityTier.DdgiMedium);"));
                Assert.That(reference, Does.Contain("`Ctrl+D` | Cycle DDGI-only debug view"));
                Assert.That(reference, Does.Contain("`Ctrl+P` | Apply the DDGI High production profile"));
                Assert.That(reference, Does.Contain("`Ctrl+T` | Cycle DDGI quality tier"));
                Assert.That(reference, Does.Contain("`Ctrl+R` | Print DDGI diagnostics"));
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
