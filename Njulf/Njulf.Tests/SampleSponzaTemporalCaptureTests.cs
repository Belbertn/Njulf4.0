using System.Security.Cryptography;
using System.Text;
using Njulf.Rendering.Debug;
using NjulfHelloGame;
using NUnit.Framework;
using StbImageSharp;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSponzaTemporalCaptureTests
{
    [Test]
    public void SequenceEmitsLockedWarmupAndBothCompleteRoutes()
    {
        var sequence = new SampleSponzaTemporalCaptureSequence();
        int capturedFrames = 0;

        for (int frame = 0;
             frame < SampleSponzaTemporalCaptureContract.WarmupFrameCount;
             frame++)
        {
            SampleSponzaTemporalCaptureInstruction instruction =
                sequence.CurrentInstruction;
            Assert.Multiple(() =>
            {
                Assert.That(
                    instruction.Stage,
                    Is.EqualTo(SampleSponzaTemporalCaptureStage.Warmup));
                Assert.That(instruction.StageFrameIndex, Is.EqualTo(frame));
                Assert.That(instruction.CaptureFrame, Is.False);
            });
            sequence.AdvanceAfterRenderedFrame(screenshotsComplete: false);
        }

        SampleSponzaGiCameraBookmark? horizontalLast = null;
        for (int frame = 0; frame < 300; frame++)
        {
            SampleSponzaTemporalCaptureInstruction instruction =
                sequence.CurrentInstruction;
            Assert.Multiple(() =>
            {
                Assert.That(
                    instruction.Stage,
                    Is.EqualTo(SampleSponzaTemporalCaptureStage.Horizontal));
                Assert.That(instruction.StageFrameIndex, Is.EqualTo(frame));
                Assert.That(instruction.CaptureFrame, Is.True);
                Assert.That(
                    instruction.Phase,
                    Is.EqualTo(frame < 120
                        ? "outbound"
                        : frame < 180 ? "hold" : "return"));
            });
            horizontalLast = instruction.Camera;
            capturedFrames++;
            sequence.AdvanceAfterRenderedFrame(screenshotsComplete: false);
        }

        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        AssertCameraEqual(contract.LowBookmark, horizontalLast!);
        AssertCameraEqual(
            contract.LowBookmark,
            sequence.CurrentInstruction.Camera);

        SampleSponzaGiCameraBookmark? verticalLast = null;
        for (int frame = 0; frame < 960; frame++)
        {
            SampleSponzaTemporalCaptureInstruction instruction =
                sequence.CurrentInstruction;
            Assert.Multiple(() =>
            {
                Assert.That(
                    instruction.Stage,
                    Is.EqualTo(SampleSponzaTemporalCaptureStage.Vertical));
                Assert.That(instruction.StageFrameIndex, Is.EqualTo(frame));
                Assert.That(instruction.CaptureFrame, Is.True);
                Assert.That(instruction.Phase, Is.EqualTo("vertical"));
            });
            verticalLast = instruction.Camera;
            capturedFrames++;
            sequence.AdvanceAfterRenderedFrame(screenshotsComplete: false);
        }

        AssertCameraEqual(contract.HighBookmark, verticalLast!);
        Assert.That(capturedFrames, Is.EqualTo(1260));
        Assert.That(
            sequence.CurrentInstruction.Stage,
            Is.EqualTo(SampleSponzaTemporalCaptureStage.Drain));

        sequence.AdvanceAfterRenderedFrame(screenshotsComplete: true);
        Assert.That(sequence.IsComplete, Is.True);
    }

    [Test]
    public void SequenceFailsWhenScreenshotsDoNotSettleWithinDrainLimit()
    {
        var sequence = new SampleSponzaTemporalCaptureSequence();
        int preDrainFrames =
            SampleSponzaTemporalCaptureContract.WarmupFrameCount +
            SampleSponzaTemporalCaptureContract.ExpectedFrameCount;
        for (int frame = 0; frame < preDrainFrames; frame++)
            sequence.AdvanceAfterRenderedFrame(screenshotsComplete: false);

        for (int frame = 0;
             frame < SampleSponzaTemporalCaptureSequence.MaximumDrainFrameCount - 1;
             frame++)
        {
            sequence.AdvanceAfterRenderedFrame(screenshotsComplete: false);
        }

        Assert.That(
            () => sequence.AdvanceAfterRenderedFrame(
                screenshotsComplete: false),
            Throws.TypeOf<TimeoutException>());
    }

    [Test]
    public void PixelChangeMetricsIgnoreAlphaAndMeasureKnownRgbDelta()
    {
        byte[] previous =
        [
            0, 0, 0, 0,
            0, 0, 0, 255
        ];
        byte[] current =
        [
            255, 255, 255, 255,
            0, 0, 0, 0
        ];

        SampleSponzaTemporalPixelChangeMetrics metrics =
            SampleSponzaTemporalCaptureAnalyzer.CalculatePixelChange(
                previous,
                current,
                width: 2,
                height: 1);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.MeanAbsoluteRgbDelta, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(
                metrics.RootMeanSquareRgbDelta,
                Is.EqualTo(Math.Sqrt(0.5)).Within(1e-12));
            Assert.That(metrics.P95AbsoluteChannelDelta, Is.EqualTo(1.0));
            Assert.That(metrics.MaximumAbsoluteChannelDelta, Is.EqualTo(1.0));
            Assert.That(metrics.ChangedPixelFraction, Is.EqualTo(0.5));
        });
    }

    [Test]
    public void PixelChangeMetricsReturnZeroForIdenticalFrames()
    {
        byte[] pixels =
        [
            3, 7, 11, 255,
            13, 17, 19, 128
        ];

        SampleSponzaTemporalPixelChangeMetrics metrics =
            SampleSponzaTemporalCaptureAnalyzer.CalculatePixelChange(
                pixels,
                pixels,
                width: 2,
                height: 1);

        Assert.That(metrics, Is.EqualTo(default(
            SampleSponzaTemporalPixelChangeMetrics)));
    }

    [Test]
    public void OfflineAnalysisRejectsAnIncompleteManifestWithoutRendering()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"temporal-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SampleSponzaTemporalCaptureContract.WriteJsonAtomic(
                Path.Combine(
                    directory,
                    SampleSponzaTemporalCaptureContract.RunFileName),
                new SampleSponzaTemporalRunManifest { Status = "running" },
                "test temporal manifest");
            using var output = new StringWriter(new StringBuilder());
            using var error = new StringWriter(new StringBuilder());

            int exitCode = SampleSponzaTemporalCaptureAnalyzer.RunOffline(
                directory,
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(error.ToString(), Does.Contain("completed capture manifest"));
                Assert.That(Directory.Exists(Path.Combine(directory, "analysis")), Is.False);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void RouteAnalysisWritesMetricsContactSheetAndRankedDifferenceSheet()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"temporal-route-analysis-{Guid.NewGuid():N}");
        string analysisTemporaryDirectory =
            Path.Combine(directory, "analysis-temporary");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] black = CreateSolidFrame(0, 0, 0);
            byte[] red = CreateSolidFrame(255, 0, 0);
            SampleSponzaTemporalFrameArtifact first = WriteFrame(
                directory,
                frameIndex: 0,
                rendererFrameSerial: 1,
                black);
            SampleSponzaTemporalFrameArtifact second = WriteFrame(
                directory,
                frameIndex: 1,
                rendererFrameSerial: 2,
                red);

            SampleSponzaTemporalRouteReview review =
                SampleSponzaTemporalCaptureAnalyzer.AnalyzeRouteForTesting(
                    directory,
                    analysisTemporaryDirectory,
                    SampleSponzaTemporalCaptureContract.HorizontalRoute,
                    [first, second]);

            string routeOutput = Path.Combine(
                analysisTemporaryDirectory,
                SampleSponzaTemporalCaptureContract.HorizontalRoute);
            ImageResult contact = ImageResult.FromMemory(
                File.ReadAllBytes(Path.Combine(
                    routeOutput,
                    "contact-0000-0001.png")),
                ColorComponents.RedGreenBlueAlpha);
            ImageResult differences = ImageResult.FromMemory(
                File.ReadAllBytes(Path.Combine(
                    routeOutput,
                    "top-changes-00.png")),
                ColorComponents.RedGreenBlueAlpha);
            string[] csvLines = File.ReadAllLines(
                Path.Combine(routeOutput, "changes.csv"));

            Assert.Multiple(() =>
            {
                Assert.That(review.FrameCount, Is.EqualTo(2));
                Assert.That(review.PairCount, Is.EqualTo(1));
                Assert.That(review.ContactSheets, Has.Count.EqualTo(1));
                Assert.That(review.TopChanges, Has.Count.EqualTo(1));
                Assert.That(contact.Width, Is.EqualTo(1600));
                Assert.That(contact.Height, Is.EqualTo(540));
                Assert.That(differences.Width, Is.EqualTo(960));
                Assert.That(differences.Height, Is.EqualTo(720));
                Assert.That(csvLines, Has.Length.EqualTo(2));
                Assert.That(csvLines[1], Does.Contain(",0,1,"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateSolidFrame(byte red, byte green, byte blue)
    {
        var pixels = new byte[checked(
            SampleSponzaTemporalCaptureContract.Width *
            SampleSponzaTemporalCaptureContract.Height * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static SampleSponzaTemporalFrameArtifact WriteFrame(
        string directory,
        int frameIndex,
        ulong rendererFrameSerial,
        byte[] pixels)
    {
        string relativePath =
            SampleSponzaTemporalCaptureContract.GetFrameRelativePath(
                SampleSponzaTemporalCaptureContract.HorizontalRoute,
                frameIndex);
        string path = Path.Combine(
            directory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        PngScreenshotEncoder.WriteAtomic(
            path,
            pixels,
            SampleSponzaTemporalCaptureContract.Width,
            SampleSponzaTemporalCaptureContract.Height,
            ScreenshotPixelFormat.Rgba8);
        var info = new FileInfo(path);
        using FileStream stream = File.OpenRead(path);
        string hash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(stream)).ToLowerInvariant();
        return new SampleSponzaTemporalFrameArtifact
        {
            CaptureOrdinal = frameIndex,
            Route = SampleSponzaTemporalCaptureContract.HorizontalRoute,
            Phase = "outbound",
            RouteFrameIndex = frameIndex,
            RelativePath = relativePath,
            ByteLength = info.Length,
            Sha256 = hash,
            RendererFrameSerial = rendererFrameSerial
        };
    }

    private static void AssertCameraEqual(
        SampleSponzaGiCameraBookmark expected,
        SampleSponzaGiCameraBookmark actual)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Position.X, Is.EqualTo(expected.Position.X));
            Assert.That(actual.Position.Y, Is.EqualTo(expected.Position.Y));
            Assert.That(actual.Position.Z, Is.EqualTo(expected.Position.Z));
            Assert.That(actual.Yaw, Is.EqualTo(expected.Yaw));
            Assert.That(actual.Pitch, Is.EqualTo(expected.Pitch));
            Assert.That(actual.FieldOfView, Is.EqualTo(expected.FieldOfView));
            Assert.That(actual.NearPlane, Is.EqualTo(expected.NearPlane));
            Assert.That(actual.FarPlane, Is.EqualTo(expected.FarPlane));
        });
    }
}
