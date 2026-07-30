using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiMaterialGpuConformanceTests
{
    [Test]
    public void ConformanceContract_LocksStd430SizesAndMeasuredProductionAbi()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUMaterialData>(), Is.EqualTo(304));
            Assert.That(
                Marshal.SizeOf<GPUMaterialExtensionData>(),
                Is.EqualTo(GiMaterialGpuConformanceContract.MaterialExtensionAbiWords * sizeof(uint)));
            Assert.That(Marshal.SizeOf<GPUGiMaterialConformanceCase>(), Is.EqualTo(128));
            Assert.That(Marshal.SizeOf<GPUGiMaterialConformanceResult>(), Is.EqualTo(144));
            Assert.That(
                Marshal.SizeOf<GPUGiMaterialExtensionConformanceElement>(),
                Is.EqualTo(GiMaterialGpuConformanceContract.MaterialExtensionAlignedWords * sizeof(uint)));
            Assert.That(
                Marshal.OffsetOf<GPUGiMaterialExtensionConformanceElement>(
                    nameof(GPUGiMaterialExtensionConformanceElement.AlignmentPadding0)).ToInt32(),
                Is.EqualTo(Marshal.SizeOf<GPUMaterialExtensionData>()));
        });
    }

    [Test]
    public void ScenarioCatalog_CoversCoreMatrixAndCompleteLinearCaptureMetadata()
    {
        IReadOnlyList<SampleMaterialGiConformanceCase> cases =
            SampleMaterialGiConformanceCatalog.Cases;
        var names = cases.Select(static value => value.Name).ToHashSet(StringComparer.Ordinal);
        var groups = cases.Select(static value => value.Group).ToHashSet(StringComparer.Ordinal);
        SampleMaterialGiCaptureMetadata metadata =
            SampleMaterialGiConformanceCatalog.CreateCaptureMetadata(
                new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero),
                "0123456789abcdef0123456789abcdef01234567",
                new string('a', 64),
                "Conformance Vulkan Device",
                "conformance-driver");

        Assert.Multiple(() =>
        {
            Assert.That(cases, Has.Count.GreaterThanOrEqualTo(25));
            Assert.That(groups, Does.Contain("metallic-sweep"));
            Assert.That(groups, Does.Contain("roughness-sweep"));
            Assert.That(groups, Does.Contain("dielectric-f0-sweep"));
            Assert.That(groups, Does.Contain("emission-sweep"));
            Assert.That(groups, Does.Contain("occlusion"));
            Assert.That(groups, Does.Contain("alpha"));
            Assert.That(groups, Does.Contain("sidedness"));
            Assert.That(groups, Does.Contain("unlit"));
            Assert.That(groups, Does.Contain("extensions"));
            Assert.That(names, Does.Contain("metallic-0.00"));
            Assert.That(names, Does.Contain("metallic-1.00"));
            Assert.That(names, Does.Contain("emission-strength-0.0"));
            Assert.That(names, Does.Contain("emission-strength-10.0"));
            Assert.That(names, Does.Contain("alpha-mask-equality-static"));
            Assert.That(names, Does.Contain("alpha-mask-equality-skinned"));
            Assert.That(names, Does.Contain("alpha-mask-cutoff-above-one"));
            Assert.That(names, Does.Contain("alpha-blend-positive-raster-only"));
            Assert.That(names, Does.Contain("single-sided-back-face"));
            Assert.That(names, Does.Contain("double-sided-back-face"));
            Assert.That(names, Does.Contain("unlit-visibility-only"));
            Assert.That(names, Does.Contain("extensions-diffuse-energy-combined"));
            Assert.That(names, Does.Contain("diffuse-brdf-angular-grazing"));
            Assert.That(names, Does.Contain("diffuse-brdf-material-f0"));
            Assert.That(names, Does.Contain("diffuse-brdf-passive-white"));
            Assert.That(names, Does.Contain("diffuse-brdf-metal-zero"));
            Assert.That(
                cases.Count(static value =>
                    value.FixtureKind == SampleMaterialGiFixtureKind.SeparateUvOcclusion),
                Is.GreaterThanOrEqualTo(2));
            Assert.That(SampleMaterialGiConformanceCatalog.Fingerprint, Has.Length.EqualTo(64));
            Assert.That(
                SampleMaterialGiConformanceCatalog.RequiredOutputs
                    .Select(static output => output.Signal),
                Is.EquivalentTo(Enum.GetValues<SampleMaterialGiCaptureSignal>()));
            Assert.That(metadata.ContractFingerprint, Is.EqualTo(SampleMaterialGiConformanceCatalog.Fingerprint));
            Assert.That(metadata.SceneSha256, Is.EqualTo(SampleMaterialGiConformanceCatalog.SceneFingerprint));
            Assert.That(SampleMaterialGiConformanceCatalog.SceneFingerprint, Has.Length.EqualTo(64));
            Assert.That(metadata.MaterialCases, Is.EqualTo(cases.Select(static value => value.Name)));
            Assert.That(metadata.Width, Is.EqualTo(SampleMaterialGiConformanceCatalog.LockedWidth));
            Assert.That(metadata.Height, Is.EqualTo(SampleMaterialGiConformanceCatalog.LockedHeight));
            Assert.That(metadata.WarmupFrames, Is.EqualTo(SampleMaterialGiConformanceCatalog.WarmupFrameCount));
            Assert.That(metadata.RandomSeed, Is.EqualTo(SampleMaterialGiConformanceCatalog.FixedRandomSeed));
            Assert.That(metadata.RendererSettings.CaptureColorSpace, Is.EqualTo("linear-scRGB-float"));
            Assert.That(metadata.RendererSettings.ExposureAppliedToLinearArtifacts, Is.False);
            Assert.That(metadata.RendererSettings.ToneMappingAppliedToLinearArtifacts, Is.False);
        });
    }

    [Test]
    public void WindowlessVulkanShader_MatchesCpuOracleAndRoundTripsExactAbi()
    {
        IReadOnlyList<SampleMaterialGiConformanceCase> scenarios =
            SampleMaterialGiConformanceCatalog.Cases;
        var materials = new GPUMaterialData[scenarios.Count];
        var extensions = new GPUGiMaterialExtensionConformanceElement[scenarios.Count];
        var inputs = new GPUGiMaterialConformanceCase[scenarios.Count];
        var compiled = new CompiledMaterialTransport[scenarios.Count];

        for (int index = 0; index < scenarios.Count; index++)
        {
            SampleMaterialGiConformanceCase scenario = scenarios[index];
            compiled[index] = MaterialTransportCompiler.Compile(
                scenario.Material,
                new MaterialCompilationContext
                {
                    ProfileRevision = checked((uint)(1000 + index))
                });

            GPUMaterialData material = compiled[index].GpuMaterial;
            material.MaterialRevision = 0xa100_0000u + (uint)index;
            material.TextureContentRevision = 0xe500_0000u + (uint)index;
            materials[index] = material;
            extensions[index] = new GPUGiMaterialExtensionConformanceElement
            {
                Value = compiled[index].ExtensionData ?? default,
                AlignmentPadding0 = 0xb200_0000u + (uint)index,
                AlignmentPadding1 = 0xc300_0000u + (uint)index,
                AlignmentPadding2 = 0xd400_0000u + (uint)index
            };

            GiMaterialSampleInputs cpuInputs = scenario.CreateCpuInputs();
            inputs[index] = new GPUGiMaterialConformanceCase
            {
                BaseColorSample = scenario.BaseColorSample,
                MetallicRoughnessSampleAndOcclusion =
                    new Vector4(scenario.MetallicRoughnessSample, scenario.OcclusionSample),
                EmissiveSampleAndNdotL = new Vector4(scenario.EmissiveSample, scenario.NdotL),
                VertexColor = scenario.VertexColor,
                GeometricNormalAndFrontFacing =
                    new Vector4(scenario.GeometricNormal, scenario.FrontFacing ? 1f : 0f),
                ShadingNormalAndNdotV = new Vector4(scenario.ShadingNormal, cpuInputs.NdotV),
                ViewDirectionAndHasExtension =
                    new Vector4(scenario.ViewDirection, compiled[index].ExtensionData.HasValue ? 1f : 0f),
                Irradiance = new Vector4(scenario.Irradiance, 0f)
            };
        }

        if (!VulkanMaterialConformanceHarness.TryCreate(
                out VulkanMaterialConformanceHarness? harness,
                out string unavailableReason))
        {
            Assert.Ignore(
                "Windowless Vulkan material oracle skipped for an explicit capability reason: " +
                unavailableReason);
            return;
        }

        VulkanMaterialConformanceHarness activeHarness = harness!;
        using (activeHarness)
        {
            VulkanMaterialConformanceOutput gpu = activeHarness.Run(materials, extensions, inputs);
            TestContext.Progress.WriteLine(
                $"Material/GI GPU oracle device='{gpu.DeviceName}', " +
                $"api=0x{gpu.DeviceApiVersion:x8}, driver=0x{gpu.DriverVersion:x8}, " +
                $"cases={scenarios.Count}.");

            Assert.That(
                Serialize(gpu.MaterialRoundTrip),
                Is.EqualTo(Serialize(materials)),
                "The shader must preserve every byte of the measured GPUMaterialData ABI.");
            Assert.That(
                Serialize(gpu.ExtensionRoundTrip),
                Is.EqualTo(Serialize(extensions)),
                "The shader must preserve the 548-byte extension ABI and explicit 560-byte array stride.");

            for (int index = 0; index < scenarios.Count; index++)
            {
                AssertScenario(scenarios[index], compiled[index], materials[index], gpu.Results[index]);
            }
        }
    }

    private static void AssertScenario(
        SampleMaterialGiConformanceCase scenario,
        CompiledMaterialTransport compiled,
        GPUMaterialData material,
        GPUGiMaterialConformanceResult actual)
    {
        GiMaterialSampleInputs inputs = scenario.CreateCpuInputs();
        GiSurfaceSample expected =
            GiMaterialReferenceEvaluator.EvaluateSurface(compiled.Definition, inputs);
        bool expectedOpacity = GiMaterialReferenceEvaluator.EvaluateOpacity(
            expected.Opacity,
            compiled.Definition.AlphaMode,
            compiled.Definition.AlphaCutoff);
        bool expectedOpaqueTransport =
            MaterialAlphaCoverageContract.OccupiesOpaqueTransport(
                expected.Opacity,
                compiled.Definition.AlphaMode,
                compiled.Definition.AlphaCutoff);
        bool expectedSidedness = GiMaterialReferenceEvaluator.EvaluateSidedness(
            compiled.Definition.DoubleSided,
            scenario.FrontFacing);
        Vector3 expectedFromIrradiance =
            GiMaterialReferenceEvaluator.EvaluateDiffuseFromIrradiance(
                scenario.Irradiance,
                expected.DiffuseReflectance);
        Vector3 expectedBrdf = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            expected.DirectionalDiffuseBase,
            expected.DielectricF0,
            scenario.NdotL,
            inputs.NdotV);
        (float compactBaseR, float compactBaseG) =
            UnpackHalf2(material.PackedMeanGiDirectionalDiffuseBaseRg);
        (float compactBaseB, float compactF0R) =
            UnpackHalf2(material.PackedMeanGiDirectionalDiffuseBaseBAndF0R);
        (float compactF0G, float compactF0B) =
            UnpackHalf2(material.PackedMeanGiDielectricF0Gb);
        Vector3 expectedCompactBrdf = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            new Vector3(compactBaseR, compactBaseG, compactBaseB),
            new Vector3(compactF0R, compactF0G, compactF0B),
            scenario.NdotL,
            inputs.NdotV);

        Assert.Multiple(() =>
        {
            AssertVector(
                new Vector3(
                    actual.GeometricNormalAndOpacity.X,
                    actual.GeometricNormalAndOpacity.Y,
                    actual.GeometricNormalAndOpacity.Z),
                expected.GeometricNormal,
                scenario,
                "geometric normal");
            AssertFloat(
                actual.GeometricNormalAndOpacity.W,
                expected.Opacity,
                scenario,
                "opacity");
            AssertVector(
                new Vector3(
                    actual.ShadingNormalAndOcclusion.X,
                    actual.ShadingNormalAndOcclusion.Y,
                    actual.ShadingNormalAndOcclusion.Z),
                expected.ShadingNormal,
                scenario,
                "shading normal");
            AssertFloat(
                actual.ShadingNormalAndOcclusion.W,
                expected.MaterialOcclusion,
                scenario,
                "material occlusion");
            AssertVector(
                new Vector3(
                    actual.DiffuseReflectanceAndMetallic.X,
                    actual.DiffuseReflectanceAndMetallic.Y,
                    actual.DiffuseReflectanceAndMetallic.Z),
                expected.DiffuseReflectance,
                scenario,
                "diffuse reflectance");
            AssertFloat(
                actual.DiffuseReflectanceAndMetallic.W,
                expected.Metallic,
                scenario,
                "metallic");
            AssertVector(
                new Vector3(
                    actual.EmissiveRadianceAndRoughness.X,
                    actual.EmissiveRadianceAndRoughness.Y,
                    actual.EmissiveRadianceAndRoughness.Z),
                expected.EmissiveRadiance,
                scenario,
                "emissive radiance");
            AssertFloat(
                actual.EmissiveRadianceAndRoughness.W,
                expected.Roughness,
                scenario,
                "roughness");
            AssertVector(
                new Vector3(
                    actual.DiffuseFromIrradianceAndOpacityPass.X,
                    actual.DiffuseFromIrradianceAndOpacityPass.Y,
                    actual.DiffuseFromIrradianceAndOpacityPass.Z),
                expectedFromIrradiance,
                scenario,
                "diffuse from irradiance");
            Assert.That(
                actual.DiffuseFromIrradianceAndOpacityPass.W,
                Is.EqualTo(expectedOpacity ? 1f : 0f),
                $"{scenario.Name}: opacity boolean parity");
            AssertVector(
                new Vector3(
                    actual.DiffuseBrdfAndSidednessPass.X,
                    actual.DiffuseBrdfAndSidednessPass.Y,
                    actual.DiffuseBrdfAndSidednessPass.Z),
                expectedBrdf,
                scenario,
                "diffuse BRDF");
            Assert.That(
                actual.DiffuseBrdfAndSidednessPass.W,
                Is.EqualTo(expectedSidedness ? 1f : 0f),
                $"{scenario.Name}: sidedness boolean parity");
            AssertVector(
                new Vector3(
                    actual.CompactDiffuseBrdf.X,
                    actual.CompactDiffuseBrdf.Y,
                    actual.CompactDiffuseBrdf.Z),
                expectedCompactBrdf,
                scenario,
                "compact diffuse BRDF");
            Assert.That(
                actual.CompactDiffuseBrdf.W,
                Is.EqualTo(expectedOpaqueTransport ? 1f : 0f),
                $"{scenario.Name}: opaque-transport alpha parity");
            Assert.That(
                actual.TransportFlags,
                Is.EqualTo(material.TransportFlags),
                $"{scenario.Name}: integer transport flags");
            Assert.That(
                actual.HasExtensionData,
                Is.EqualTo(compiled.ExtensionData.HasValue ? 1u : 0u),
                $"{scenario.Name}: extension-presence integer");
            Assert.That(
                actual.MaterialRevision,
                Is.EqualTo(material.MaterialRevision),
                $"{scenario.Name}: material revision");
            Assert.That(
                actual.TransportProfileRevision,
                Is.EqualTo(material.TransportProfileRevision),
                $"{scenario.Name}: transport profile revision");
            Assert.That(
                actual.TextureContentRevision,
                Is.EqualTo(material.TextureContentRevision),
                $"{scenario.Name}: texture-content revision");
            Assert.That(
                actual.PackedMeanGiDirectionalDiffuseBaseRg,
                Is.EqualTo(material.PackedMeanGiDirectionalDiffuseBaseRg));
            Assert.That(
                actual.PackedMeanGiDirectionalDiffuseBaseBAndF0R,
                Is.EqualTo(material.PackedMeanGiDirectionalDiffuseBaseBAndF0R));
            Assert.That(
                actual.PackedMeanGiDielectricF0Gb,
                Is.EqualTo(material.PackedMeanGiDielectricF0Gb));
        });
    }

    private static void AssertVector(
        Vector3 actual,
        Vector3 expected,
        SampleMaterialGiConformanceCase scenario,
        string signal)
    {
        AssertFloat(actual.X, expected.X, scenario, signal + ".x");
        AssertFloat(actual.Y, expected.Y, scenario, signal + ".y");
        AssertFloat(actual.Z, expected.Z, scenario, signal + ".z");
    }

    private static void AssertFloat(
        float actual,
        float expected,
        SampleMaterialGiConformanceCase scenario,
        string signal)
    {
        Assert.That(float.IsFinite(actual), Is.True, $"{scenario.Name}: {signal} must be finite");
        Assert.That(
            actual,
            Is.EqualTo(expected).Within(GiMaterialGpuConformanceContract.MaximumAbsoluteError),
            $"{scenario.Name}: {signal}");
    }

    private static byte[] Serialize<T>(T[] values)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static (float Low, float High) UnpackHalf2(uint value)
    {
        return (
            (float)BitConverter.UInt16BitsToHalf((ushort)(value & 0xffffu)),
            (float)BitConverter.UInt16BitsToHalf((ushort)(value >> 16)));
    }
}
