using System.Buffers.Binary;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using NjulfHelloGame;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialGiLinearCaptureTests
{
    [SetUp]
    public void ClearCaptureEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_PERFORMANCE_SCENARIO", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BASELINE_SNAPSHOT_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_SPONZA_GI_CAPTURE_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_MATERIAL_GI_CAPTURE_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_REPORT", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_VALIDATION", null);
    }

    [Test]
    public void PfmCodec_RoundTripsTopDownHdrFloatPixelsExactly()
    {
        float[] pixels =
        [
            0f, 0.25f, 8.5f,
            1f, 2f, 4f,
            -0.125f, 0.5f, 1.25f,
            16f, 0.0001f, 0.75f
        ];

        byte[] encoded = PfmLinearImageCodec.Encode(pixels, width: 2, height: 2);
        LinearFloatImage decoded = PfmLinearImageCodec.Decode(encoded);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Width, Is.EqualTo(2));
            Assert.That(decoded.Height, Is.EqualTo(2));
            Assert.That(decoded.Pixels, Is.EqualTo(pixels));
            Assert.That(decoded.Pixels.Max(), Is.GreaterThan(1f));
            Assert.That(System.Text.Encoding.ASCII.GetString(encoded.AsSpan(0, 2)), Is.EqualTo("PF"));
            Assert.That(
                System.Text.Encoding.ASCII.GetString(encoded),
                Does.Contain("NJULF_LINEAR_FLOAT_IMAGE_VERSION=1"));
        });
    }

    [Test]
    public void PfmCodec_RejectsNonfiniteEvidence()
    {
        Assert.That(
            () => PfmLinearImageCodec.Encode([0f, float.NaN, 1f], 1, 1),
            Throws.ArgumentException.With.Message.Contains("non-finite"));
        Assert.That(
            () => PfmLinearImageCodec.Encode([0f, float.PositiveInfinity, 1f], 1, 1),
            Throws.ArgumentException.With.Message.Contains("non-finite"));
    }

    [Test]
    public void PfmCodec_AtomicPublicationLeavesOnlyVerifiedEvidence()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-pfm-tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "evidence.pfm");
        float[] pixels = [0.25f, 2f, 16f, -0.5f, 1f, 4f];
        try
        {
            PfmLinearImageCodec.WriteAtomic(path, pixels, width: 2, height: 1);
            LinearFloatImage decoded = PfmLinearImageCodec.Decode(File.ReadAllBytes(path));

            Assert.Multiple(() =>
            {
                Assert.That(decoded.Pixels, Is.EqualTo(pixels));
                Assert.That(PfmLinearImageCodec.ComputeSha256(path), Has.Length.EqualTo(64));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly),
                    Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PfmCodec_RejectsOversizedOutputBeforeReplacingPublishedEvidence()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-pfm-bounds-tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "evidence.pfm");
        byte[] sentinel = [0x4e, 0x4a, 0x55, 0x4c, 0x46];
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, sentinel);
        try
        {
            Assert.That(
                () => PfmLinearImageCodec.WriteAtomic(
                    path,
                    Array.Empty<float>(),
                    PfmLinearImageCodec.MaximumPixelCount + 1,
                    height: 1),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("publication bound"));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(sentinel));
                Assert.That(
                    Directory.EnumerateFiles(
                        directory,
                        "*.tmp",
                        SearchOption.TopDirectoryOnly),
                    Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void NativeRgba16fDecode_PreservesHdrAndDropsOnlyAlpha()
    {
        float[] source = [0.5f, 2f, 16f, 0.25f, 1f, 4f, 8f, 0.75f];
        byte[] bytes = new byte[source.Length * sizeof(ushort)];
        for (int index = 0; index < source.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)source[index]));
        }

        float[] rgb = LinearHdrReadbackManager.DecodeRgba16Float(bytes, width: 2, height: 1);

        Assert.That(rgb, Is.EqualTo(new[] { 0.5f, 2f, 16f, 1f, 4f, 8f }));
    }

    [Test]
    public void LinearReadbackPlan_RequiresNativeFormatAndTransferSourceUsage()
    {
        LinearHdrReadbackFormatSupport supported = LinearHdrReadbackFormatSupport.Evaluate(
            Format.R16G16B16A16Sfloat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
        LinearHdrReadbackFormatSupport missingUsage = LinearHdrReadbackFormatSupport.Evaluate(
            Format.R16G16B16A16Sfloat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
        LinearHdrReadbackFormatSupport wrongFormat = LinearHdrReadbackFormatSupport.Evaluate(
            Format.R32G32B32A32Sfloat,
            ImageUsageFlags.TransferSrcBit);

        Assert.Multiple(() =>
        {
            Assert.That(supported.Supported, Is.True);
            Assert.That(missingUsage.Supported, Is.False);
            Assert.That(missingUsage.Reason, Does.Contain("TransferSrc"));
            Assert.That(wrongFormat.Supported, Is.False);
            Assert.That(wrongFormat.Reason, Does.Contain("R16G16B16A16"));
            Assert.That(LinearHdrReadbackFormatSupport.BytesPerPixel, Is.EqualTo(8));
        });
    }

    [Test]
    public void LinearCaptureService_PreservesTokenAndExactSubmittedFrameSerial()
    {
        var service = new LinearHdrCaptureService();
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "attested-linear-capture.pfm");
        const string token = "quality-sequence:route-0059";

        service.Request(path, token);
        Assert.That(service.TryDequeue(out LinearHdrCaptureRequest request), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(request.OutputPath, Is.EqualTo(Path.GetFullPath(path)));
            Assert.That(request.CaptureToken, Is.EqualTo(token));
        });

        service.MarkSubmitted(path, 42UL);
        LinearHdrCaptureResult submitted = service.GetResult(path);
        service.MarkCompleted(path);
        LinearHdrCaptureResult completed = service.GetResult(path);

        Assert.Multiple(() =>
        {
            Assert.That(submitted.State, Is.EqualTo(LinearHdrCaptureState.Submitted));
            Assert.That(submitted.CaptureToken, Is.EqualTo(token));
            Assert.That(submitted.FrameSerial, Is.EqualTo(42UL));
            Assert.That(completed.State, Is.EqualTo(LinearHdrCaptureState.Completed));
            Assert.That(completed.CaptureToken, Is.EqualTo(token));
            Assert.That(completed.FrameSerial, Is.EqualTo(42UL));
        });
    }

    [Test]
    public void CaptureSequence_WarmsExactly360FramesThenPublishesAllSignalsInOrder()
    {
        var sequence = new SampleMaterialGiCaptureSequence();
        for (int frame = 0; frame < SampleMaterialGiConformanceCatalog.WarmupFrameCount; frame++)
        {
            Assert.That(sequence.CurrentInstruction.Stage, Is.EqualTo(SampleMaterialGiCaptureStage.Warmup));
            sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Unknown);
        }

        var captured = new List<SampleMaterialGiCaptureSignal>();
        while (!sequence.IsComplete)
        {
            SampleMaterialGiCaptureInstruction instruction = sequence.CurrentInstruction;
            switch (instruction.Stage)
            {
                case SampleMaterialGiCaptureStage.PresentOutput:
                    sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Unknown);
                    break;
                case SampleMaterialGiCaptureStage.CaptureOutput:
                    Assert.That(instruction.QueueCapture, Is.True);
                    captured.Add(instruction.Output!.Signal);
                    sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Queued);
                    break;
                case SampleMaterialGiCaptureStage.AwaitReadback:
                    sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Completed);
                    break;
                default:
                    Assert.Fail($"Unexpected stage {instruction.Stage}.");
                    break;
            }
        }

        Assert.That(
            captured,
            Is.EqualTo(SampleMaterialGiConformanceCatalog.RequiredOutputs.Select(static output => output.Signal)));
        Assert.That(captured, Has.Count.EqualTo(SampleMaterialGiConformanceCatalog.RequiredOutputs.Count));
    }

    [Test]
    public void CaptureSequence_FailsClosedWhenReadbackNeverCompletes()
    {
        var sequence = new SampleMaterialGiCaptureSequence();
        for (int frame = 0; frame < SampleMaterialGiConformanceCatalog.WarmupFrameCount; frame++)
            sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Unknown);
        sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Unknown);
        sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Queued);

        for (int frame = 0; frame <= SampleMaterialGiCaptureSequence.MaximumReadbackWaitFrames; frame++)
            sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Submitted);

        Assert.Multiple(() =>
        {
            Assert.That(sequence.IsFailed, Is.True);
            Assert.That(sequence.FailureReason, Does.Contain("did not complete"));
        });
    }

    [Test]
    public void StandaloneCli_LocksMaterialShowcaseAndRejectsCompetingModes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NjulfMaterialGi", Guid.NewGuid().ToString("N"));
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
            ["--material-gi-capture-dir", directory]);

        Assert.Multiple(() =>
        {
            Assert.That(options.MaterialGiCaptureDirectory, Is.EqualTo(Path.GetFullPath(directory)));
            Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.MaterialShowcase));
            Assert.That(options.PerformanceScenario, Is.EqualTo(SamplePerformanceScenario.Normal));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.None));
            Assert.That(options.FrameCount, Is.Zero);
            Assert.That(options.EnableGpuTiming, Is.True);
        });

        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                ["--material-gi-capture-dir", directory, "--smoke-frames", "3"]),
            Throws.ArgumentException.With.Message.Contains("deterministic frame sequence"));
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                ["--material-gi-capture-dir", directory, "--scene", "sponza-plaza"]),
            Throws.ArgumentException.With.Message.Contains("MaterialShowcase"));
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                ["--material-gi-capture-dir", directory, "--sponza-gi-capture-dir", directory]),
            Throws.ArgumentException.With.Message.Contains("cannot be combined"));
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                ["--material-gi-capture-dir", directory, "--benchmark"]),
            Throws.ArgumentException.With.Message.Contains("benchmark"));
        SampleSmokeOptions forcedAsync = SampleSmokeOptionsParser.Parse(
            [
                "--material-gi-capture-dir",
                directory,
                "--async-compute-mode",
                "forced"
            ]);
        Assert.That(
            forcedAsync.AsyncComputeModeOverride,
            Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                [
                    "--material-gi-capture-dir",
                    directory,
                    "--async-compute-mode",
                    "auto"
                ]),
            Throws.ArgumentException.With.Message.Contains("not reproducible"));
    }

    [Test]
    public void CaptureProfile_EnablesSimpleDdgiAndDisablesNondeterministicPresentationFeatures()
    {
        var settings = new RenderSettings();
        SampleMaterialGiCaptureRunner.ApplyLockedSettings(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(settings.ResolutionScale, Is.EqualTo(1f));
            Assert.That(settings.DynamicResolution.Enabled, Is.False);
            Assert.That(settings.Exposure, Is.EqualTo(1f));
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.AntiAliasing.Mode, Is.EqualTo(AntiAliasingMode.None));
            Assert.That(settings.AntiAliasing.JitterEnabled, Is.False);
            Assert.That(settings.AsyncCompute.Mode, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(settings.GlobalIllumination.Enabled, Is.True);
            Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled, Is.False);
            Assert.That(settings.GlobalIllumination.FarFieldClipmapEnabled, Is.True);
            Assert.That(settings.GlobalIllumination.FarFieldForceAll, Is.False);
            Assert.That(settings.Animation.Enabled, Is.True);
            Assert.That(settings.Animation.SkinningMode, Is.EqualTo(AnimationSkinningMode.GpuCompute));
            Assert.That(settings.Animation.DebugView, Is.EqualTo(AnimationDebugView.None));
            Assert.That(settings.SceneSubmission.ValidationCompareCpuGpuLists, Is.False);
            Assert.That(settings.Debug.AllowScreenshots, Is.True);
        });

        var forcedAsyncSettings = new RenderSettings();
        SampleMaterialGiCaptureRunner.ApplyLockedSettings(
            forcedAsyncSettings,
            AsyncComputeMode.ForceEnabledForValidation);
        Assert.That(
            forcedAsyncSettings.AsyncCompute.Mode,
            Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
        Assert.That(
            () => SampleMaterialGiCaptureRunner.ApplyLockedSettings(
                new RenderSettings(),
                AsyncComputeMode.Auto),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ManifestValidation_RejectsMissingAndUnpublishedOutputs()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-manifest-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.That(
                () => SampleMaterialGiArtifactPublisher.ValidateCompleteArtifactSet(
                    directory,
                    Array.Empty<SampleMaterialGiArtifact>()),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains(
                    $"exactly {SampleMaterialGiConformanceCatalog.RequiredOutputs.Count}"));

            SampleMaterialGiArtifact[] records =
                SampleMaterialGiConformanceCatalog.RequiredOutputs
                    .Select(output => new SampleMaterialGiArtifact(
                        output.Signal,
                        output.FileStem,
                        SampleMaterialGiArtifactPublisher.GetRelativeArtifactPath(output),
                        new string('0', 64),
                        1,
                        SampleMaterialGiConformanceCatalog.LockedWidth,
                        SampleMaterialGiConformanceCatalog.LockedHeight,
                        0f,
                        0f))
                    .ToArray();
            Assert.That(
                () => SampleMaterialGiArtifactPublisher.ValidateCompleteArtifactSet(directory, records),
                Throws.TypeOf<FileNotFoundException>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void GraphicsAsyncComparison_UsesNumericalToleranceAndReportsHashDifference()
    {
        var reference = new LinearFloatImage(
            2,
            1,
            [0f, 0.5f, 8f, 1f, 2f, 4f]);
        var withinTolerance = new LinearFloatImage(
            2,
            1,
            [0f, 0.5f, 8.001f, 1f, 2f, 4f]);
        var outsideTolerance = new LinearFloatImage(
            2,
            1,
            [0f, 0.5f, 8.1f, 1f, 2f, 4f]);
        SampleMaterialGiComparisonTolerance tolerance =
            SampleMaterialGiComparisonTolerance.GraphicsAsyncEquivalence;

        SampleMaterialGiOutputComparison accepted =
            SampleMaterialGiCaptureComparer.CompareImages(
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                "05-indirect-composed.pfm",
                new string('a', 64),
                new string('b', 64),
                reference,
                withinTolerance,
                tolerance);
        SampleMaterialGiOutputComparison rejected =
            SampleMaterialGiCaptureComparer.CompareImages(
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                "05-indirect-composed.pfm",
                new string('a', 64),
                new string('c', 64),
                reference,
                outsideTolerance,
                tolerance);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Passed, Is.True);
            Assert.That(accepted.HashesEqual, Is.False);
            Assert.That(accepted.AbsoluteRmse, Is.GreaterThan(0.0));
            Assert.That(rejected.Passed, Is.False);
            Assert.That(
                rejected.MaximumAbsoluteComponentError,
                Is.GreaterThan(tolerance.MaximumAbsoluteComponentError));
        });
    }

    [Test]
    public void GraphicsAsyncComparison_RejectsNonfiniteImageInput()
    {
        var reference = new LinearFloatImage(1, 1, [0f, 1f, 2f]);
        var nonfinite = new LinearFloatImage(1, 1, [0f, float.NaN, 2f]);

        Assert.That(
            () => SampleMaterialGiCaptureComparer.CompareImages(
                SampleMaterialGiCaptureSignal.DirectDiffuse,
                "00-direct-diffuse.pfm",
                new string('a', 64),
                new string('b', 64),
                reference,
                nonfinite,
                SampleMaterialGiComparisonTolerance.GraphicsAsyncEquivalence),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("non-finite"));
    }

    [Test]
    public void GraphicsAsyncComparisonCli_EmitsMachineReadableFailureWithoutVulkan()
    {
        string root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-comparison-cli-tests",
            Guid.NewGuid().ToString("N"));
        string reference = Path.Combine(root, "graphics");
        string candidate = Path.Combine(root, "async");
        string reportPath = Path.Combine(root, "comparison.json");
        Directory.CreateDirectory(reference);
        Directory.CreateDirectory(candidate);
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            bool handled = SampleMaterialGiComparisonCli.TryRun(
                [
                    SampleMaterialGiComparisonCli.CompareOption,
                    reference,
                    candidate,
                    SampleMaterialGiComparisonCli.ReportOption,
                    reportPath
                ],
                stdout,
                stderr,
                out int exitCode);
            using System.Text.Json.JsonDocument report =
                System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(reportPath));

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(stderr.ToString(), Does.Contain("equivalence failed"));
                Assert.That(
                    report.RootElement.GetProperty("schemaVersion").GetString(),
                    Is.EqualTo(SampleMaterialGiCaptureComparer.ReportSchemaVersion));
                Assert.That(report.RootElement.GetProperty("status").GetString(), Is.EqualTo("failed"));
                Assert.That(
                    report.RootElement.GetProperty("failureReason").GetString(),
                    Does.Contain("manifest is missing"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CaptureShaderModes_AreLateDirectSignalBranchesAndNormalRenderingRemainsUntouched()
    {
        string shader = ReadRepoFile("Njulf.Shaders", "forward.frag");
        string settings = ReadRepoFile("Njulf.Rendering", "Data", "RenderSettings.cs");
        string targets = ReadRepoFile("Njulf.Rendering", "Resources", "RenderTargetManager.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE = 71u"));
            Assert.That(shader, Does.Contain("MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR = 72u"));
            Assert.That(shader, Does.Contain("directLighting - directDiffuseSource"));
            Assert.That(shader, Does.Contain("Both terms came from the same light loop"));
            Assert.That(settings, Does.Contain("CaptureLinearDirectDiffuse = 71"));
            Assert.That(settings, Does.Contain("CaptureLinearDirectSpecular = 72"));
            Assert.That(targets, Does.Contain("transferSource: true"));
            Assert.That(targets, Does.Not.Contain("LinearDirectDiffuse"));
            Assert.That(targets, Does.Not.Contain("LinearDirectSpecular"));
        });
    }

    private static string ReadRepoFile(params string[] parts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(parts)}'.");
    }
}
