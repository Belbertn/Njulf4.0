using System;
using System.IO;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkEvidenceTests
{
    [Test]
    public void HdrComparer_ReportsHashesAndAppliesRelativeRmseGate()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "benchmark-hdr-evidence",
            Guid.NewGuid().ToString("N"));
        string referencePath = Path.Combine(directory, "reference.pfm");
        string matchingPath = Path.Combine(directory, "matching.pfm");
        string failingPath = Path.Combine(directory, "failing.pfm");
        float[] reference =
        [
            1.0f, 0.5f, 0.25f,
            0.1f, 0.2f, 0.3f
        ];
        PfmLinearImageCodec.WriteAtomic(referencePath, reference, 2, 1);
        PfmLinearImageCodec.WriteAtomic(matchingPath, reference, 2, 1);
        PfmLinearImageCodec.WriteAtomic(
            failingPath,
            [0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f],
            2,
            1);

        SampleBenchmarkHdrDifference matching =
            SampleBenchmarkHdrComparer.Compare(referencePath, matchingPath);
        SampleBenchmarkHdrDifference failing =
            SampleBenchmarkHdrComparer.Compare(referencePath, failingPath);

        Assert.Multiple(() =>
        {
            Assert.That(matching.Available, Is.True);
            Assert.That(matching.Passed, Is.True);
            Assert.That(matching.RelativeRmse, Is.Zero);
            Assert.That(matching.ReferenceSha256, Has.Length.EqualTo(64));
            Assert.That(matching.CandidateSha256, Has.Length.EqualTo(64));
            Assert.That(failing.Available, Is.True);
            Assert.That(failing.Passed, Is.False);
            Assert.That(failing.RelativeRmse, Is.GreaterThan(
                SampleBenchmarkHdrDifference.DefaultMaximumRelativeRmse));
        });
    }

    [Test]
    public void HdrComparer_AppliesFlipAndNamedLinearRoiGates()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "benchmark-hdr-quality-contract",
            Guid.NewGuid().ToString("N"));
        string referencePath = Path.Combine(directory, "reference.pfm");
        string matchingPath = Path.Combine(directory, "matching.pfm");
        string shiftedPath = Path.Combine(directory, "shifted.pfm");
        string contractPath = Path.Combine(directory, "quality.json");
        float[] reference =
        [
            1.0f, 1.0f, 1.0f,
            1.0f, 1.0f, 1.0f,
            1.0f, 1.0f, 1.0f,
            1.0f, 1.0f, 1.0f
        ];
        float[] shifted = (float[])reference.Clone();
        shifted[0] = 0.5f;
        shifted[1] = 0.5f;
        shifted[2] = 0.5f;
        PfmLinearImageCodec.WriteAtomic(referencePath, reference, 2, 2);
        PfmLinearImageCodec.WriteAtomic(matchingPath, reference, 2, 2);
        PfmLinearImageCodec.WriteAtomic(shiftedPath, shifted, 2, 2);
        Directory.CreateDirectory(directory);
        var contract = new SampleBenchmarkHdrQualityContract(
            SampleBenchmarkHdrQualityContract.CurrentSchema,
            2,
            2,
            [new SampleBenchmarkHdrRoiContract("whole-frame", 0, 0, 2, 2, 0.01, 0.01)]);
        File.WriteAllText(
            contractPath,
            JsonSerializer.Serialize(
                contract,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));

        SampleBenchmarkHdrDifference matching =
            SampleBenchmarkHdrComparer.Compare(
                referencePath,
                matchingPath,
                maximumRelativeRmse: 1.0,
                maximumFlipP95: 1.0,
                qualityContractPath: contractPath);
        SampleBenchmarkHdrDifference shiftedResult =
            SampleBenchmarkHdrComparer.Compare(
                referencePath,
                shiftedPath,
                maximumRelativeRmse: 1.0,
                maximumFlipP95: 1.0,
                qualityContractPath: contractPath);

        Assert.Multiple(() =>
        {
            Assert.That(matching.Passed, Is.True);
            Assert.That(matching.FlipP95, Is.Zero);
            Assert.That(matching.RoiResults, Has.Count.EqualTo(1));
            Assert.That(matching.RoiResults[0].Passed, Is.True);
            Assert.That(matching.QualityContractSha256, Has.Length.EqualTo(64));
            Assert.That(shiftedResult.Passed, Is.False);
            Assert.That(shiftedResult.RoiResults[0].Passed, Is.False);
            Assert.That(shiftedResult.FailureReason, Does.Contain("whole-frame"));
        });
    }

    [Test]
    public void ShaderProfileLoader_RejectsMismatchedCaptureIdentity()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "benchmark-shader-profile-evidence",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "profile.json");
        Directory.CreateDirectory(directory);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            CaptureGpuDeviceName = "GPU",
            CaptureGpuDriverVersion = "Driver",
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                ExecutableHash = "sha256:executable",
                ShaderBundleHash = "sha256:shader"
            }
        };
        var artifact = new SampleShaderProfileArtifact(
            SampleShaderProfileArtifact.CurrentSchema,
            "NVIDIA Nsight Graphics",
            "test",
            "Different GPU",
            "Driver",
            "sha256:executable",
            "sha256:shader",
            [
                new SampleShaderStageProfile(
                    "ForwardPlusPass",
                    "forward_opaque_ddgi.frag",
                    "baseline",
                    96,
                    0,
                    0,
                    50.0,
                    90.0,
                    100,
                    50,
                    1_000,
                    ["texture dependency"])
            ]);
        File.WriteAllText(path, JsonSerializer.Serialize(artifact));

        SampleShaderProfileEvidence evidence =
            SampleShaderProfileEvidenceLoader.Load(path, diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Available, Is.False);
            Assert.That(evidence.UnavailableReason, Does.Contain("GPU identity"));
        });
    }

    [Test]
    public void ShaderProfileLoader_RequiresOpaqueAndDecalNsightStages()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "benchmark-shader-profile-stage-contract",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "profile.json");
        Directory.CreateDirectory(directory);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            CaptureGpuDeviceName = "GPU",
            CaptureGpuDriverVersion = "Driver",
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                ExecutableHash = "sha256:executable",
                ShaderBundleHash = "sha256:shader"
            }
        };
        SampleShaderStageProfile opaque = new(
            "ForwardPlusPass",
            "forward_opaque_ddgi.frag",
            "baseline",
            96,
            0,
            0,
            50.0,
            90.0,
            100,
            50,
            1_000,
            ["texture dependency"]);
        SampleShaderStageProfile decal = new(
            "TransparentPasses",
            "forward.frag",
            "geometry-decal",
            104,
            0,
            0,
            45.0,
            82.0,
            120,
            60,
            1_200,
            ["storage dependency"]);

        WriteArtifact(path, diagnostics, [opaque]);
        SampleShaderProfileEvidence incomplete =
            SampleShaderProfileEvidenceLoader.Load(path, diagnostics);
        WriteArtifact(path, diagnostics, [opaque, decal]);
        SampleShaderProfileEvidence complete =
            SampleShaderProfileEvidenceLoader.Load(path, diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(incomplete.Available, Is.False);
            Assert.That(incomplete.UnavailableReason, Does.Contain("both ForwardPlusPass"));
            Assert.That(complete.Available, Is.True);
            Assert.That(complete.Stages, Has.Count.EqualTo(2));
            Assert.That(complete.ArtifactSha256, Has.Length.EqualTo(64));
        });
    }

    private static void WriteArtifact(
        string path,
        RendererDiagnostics diagnostics,
        SampleShaderStageProfile[] stages)
    {
        var artifact = new SampleShaderProfileArtifact(
            SampleShaderProfileArtifact.CurrentSchema,
            "NVIDIA Nsight Graphics",
            "test",
            diagnostics.CaptureGpuDeviceName,
            diagnostics.CaptureGpuDriverVersion,
            diagnostics.CaptureRun.ExecutableHash,
            diagnostics.CaptureRun.ShaderBundleHash,
            stages);
        File.WriteAllText(path, JsonSerializer.Serialize(artifact));
    }
}
