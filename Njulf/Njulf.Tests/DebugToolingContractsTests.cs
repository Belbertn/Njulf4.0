using System.Threading;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
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
                Assert.That(diagnostics.FoliageInstanceBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageClusterBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.FoliageDrawBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.CpuFoliageBuildMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuFoliageUploadMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageCullMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageDepthMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageForwardMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuFoliageShadowMicroseconds, Is.EqualTo(0));
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
    }
}
