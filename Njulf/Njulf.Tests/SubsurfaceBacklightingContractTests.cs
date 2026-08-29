using System.IO;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SubsurfaceBacklightingContractTests
{
    private const float Tolerance = 1e-6f;

    [Test]
    public void DiffuseSplit_ClampsStrengthAndMatchesEndpointsAndMidpoint()
    {
        Vector3 front = new(0.8f, 0.4f, 0.2f);
        Vector3 back = new(0.1f, 0.6f, 0.9f);

        AssertVector(front,
            GiMaterialReferenceEvaluator.ApplyGiSubsurfaceDiffuseSplit(
                front, back, -1f));
        AssertVector(front,
            GiMaterialReferenceEvaluator.ApplyGiSubsurfaceDiffuseSplit(
                front, back, 0f));
        AssertVector((front + back) * 0.5f,
            GiMaterialReferenceEvaluator.ApplyGiSubsurfaceDiffuseSplit(
                front, back, 0.5f));
        AssertVector(back,
            GiMaterialReferenceEvaluator.ApplyGiSubsurfaceDiffuseSplit(
                front, back, 1f));
        AssertVector(back,
            GiMaterialReferenceEvaluator.ApplyGiSubsurfaceDiffuseSplit(
                front, back, 2f));
    }

    [Test]
    public void DiffuseBudget_ClampsInputsAndAppliesTintPerChannel()
    {
        Vector3 ordinary = new(0.8f, 1.2f, -0.3f);
        Vector3 tint = new(0.25f, 0.5f, 2f);
        Vector3 tinted =
            GiMaterialReferenceEvaluator.EvaluateGiSubsurfaceDiffuseBudget(
                ordinary,
                tint);

        Assert.Multiple(() =>
        {
            AssertVector(new Vector3(0.2f, 0.5f, 0f), tinted);
            AssertVector(new Vector3(0.8f, 1f, 0f),
                GiMaterialReferenceEvaluator.EvaluateGiSubsurfaceDiffuseBudget(
                    ordinary,
                    Vector3.One));
            AssertVector(Vector3.Zero,
                GiMaterialReferenceEvaluator.EvaluateGiSubsurfaceDiffuseBudget(
                    ordinary,
                    Vector3.Zero));
        });
    }

    [Test]
    public void CanonicalBudgets_ApplyLayerAttenuationOnceAndNeverGainEnergy()
    {
        Vector3 baseColor = new(0.9f, 0.55f, 0.25f);
        Vector3 sheen = new(0.15f, 0.35f, 0.05f);
        Vector3 tint = new(0.3f, 0.7f, 1f);
        Vector3 directional =
            GiMaterialReferenceEvaluator.EvaluateDirectionalDiffuseBase(
                baseColor,
                metallic: 0.2f,
                transmission: 0.35f,
                clearcoat: 0.6f,
                sheenColor: sheen);
        Vector3 hemispherical =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                baseColor,
                metallic: 0.2f,
                transmission: 0.35f,
                clearcoat: 0.6f,
                sheenColor: sheen,
                nDotV: 0.55f);
        Vector3 backDirectional =
            GiMaterialReferenceEvaluator.EvaluateGiSubsurfaceDiffuseBudget(
                directional,
                tint);
        Vector3 backHemispherical =
            GiMaterialReferenceEvaluator.EvaluateGiSubsurfaceDiffuseBudget(
                hemispherical,
                tint);

        Assert.Multiple(() =>
        {
            AssertVector(directional * tint, backDirectional);
            AssertVector(hemispherical * tint, backHemispherical);
            AssertComponentwiseLessThanOrEqual(backDirectional, directional);
            AssertComponentwiseLessThanOrEqual(backHemispherical, hemispherical);
            AssertVector(Vector3.Zero,
                GiMaterialReferenceEvaluator.EvaluateDirectionalDiffuseBase(
                    baseColor, metallic: 1f));
            AssertVector(Vector3.Zero,
                GiMaterialReferenceEvaluator.EvaluateDirectionalDiffuseBase(
                    baseColor, metallic: 0f, transmission: 1f));
        });
    }

    [TestCase(0.001f, 0.001f)]
    [TestCase(0.5f, 0.5f)]
    [TestCase(1f, 1f)]
    public void EqualCosineBackLobe_IsConservativeAndObeysShadow(
        float nDotL,
        float nDotV)
    {
        Vector3 ordinaryBudget = new(0.75f, 0.45f, 0.2f);
        Vector3 dielectricF0 = new(0.04f);
        Vector3 backBudget =
            GiMaterialReferenceEvaluator.EvaluateGiSubsurfaceDiffuseBudget(
                ordinaryBudget,
                new Vector3(0.35f, 0.8f, 1f));
        Vector3 front = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            ordinaryBudget,
            dielectricF0,
            nDotL,
            nDotV) * nDotL;
        Vector3 back = GiMaterialReferenceEvaluator.EvaluateDiffuseBrdf(
            backBudget,
            dielectricF0,
            nDotL,
            nDotV) * nDotL;
        Vector3 split =
            GiMaterialReferenceEvaluator.ApplyGiSubsurfaceDiffuseSplit(
                front,
                back,
                0.65f);

        Assert.Multiple(() =>
        {
            AssertComponentwiseLessThanOrEqual(back, front);
            AssertComponentwiseLessThanOrEqual(split, front);
            AssertVector(Vector3.Zero, back * 0f);
            AssertVector(back, back * 1f);
            Assert.That(float.IsFinite(split.X) &&
                        float.IsFinite(split.Y) &&
                        float.IsFinite(split.Z), Is.True);
        });
    }

    [Test]
    public void MaterialValidation_RejectsNonFiniteSubsurfaceInputs()
    {
        MaterialDefinition invalidStrength = new()
        {
            FeatureFlags = MaterialFeatureFlags.Subsurface,
            Extensions = MaterialExtensionDefinition.None with
            {
                SubsurfaceStrength = float.NaN
            }
        };
        MaterialDefinition invalidTint = invalidStrength with
        {
            Extensions = MaterialExtensionDefinition.None with
            {
                SubsurfaceColor = new Vector3(float.PositiveInfinity, 1f, 1f),
                SubsurfaceStrength = 0.5f
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    invalidStrength),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MaterialDefinitionValidator.ValidateAndNormalize(
                    invalidTint),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void SharedShaderContract_UsesBoundedBudgetAndConvexSplitHelpers()
    {
        string shared = ReadRepoText(
            "Njulf.Shaders",
            "gi_material_transport.glsl");
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string budget = ExtractFunction(
            shared,
            "vec3 EvaluateGiSubsurfaceDiffuseBudget(");
        string split = ExtractFunction(
            shared,
            "vec3 ApplyGiSubsurfaceDiffuseSplit(");

        Assert.Multiple(() =>
        {
            Assert.That(budget,
                Does.Contain("clamp(ordinaryDiffuseBudget"));
            Assert.That(budget, Does.Contain("clamp(subsurfaceTint"));
            Assert.That(split, Does.Contain("return mix("));
            Assert.That(split, Does.Contain("max(frontDiffuse"));
            Assert.That(split, Does.Contain("max(backDiffuse"));
            Assert.That(split, Does.Contain("clamp(strength, 0.0, 1.0)"));
            Assert.That(CountOccurrences(
                    forward,
                    "EvaluateGiSubsurfaceDiffuseBudget("),
                Is.EqualTo(2));
        });
    }

    [Test]
    public void DirectShaderContract_RoutesSupportedLightsAndKeepsAreaFrontOnly()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string accumulate = ExtractFunction(
            forward,
            "void AccumulateLight(");
        int areaStart = accumulate.IndexOf(
            "else if (NjulfIsAreaLight(light))",
            StringComparison.Ordinal);
        int punctualStart = accumulate.IndexOf(
            "else if (NjulfIsPunctualLight(light))",
            StringComparison.Ordinal);
        string area = accumulate[areaStart..punctualStart];

        Assert.Multiple(() =>
        {
            Assert.That(accumulate, Does.Contain("float signedNdotL = 0.0;"));
            Assert.That(CountOccurrences(
                    accumulate,
                    "signedNdotL = dot(normal, lightDirection);"),
                Is.EqualTo(2));
            Assert.That(accumulate, Does.Contain("signedNdotL > 0.0"));
            Assert.That(accumulate,
                Does.Contain("signedNdotL < 0.0 && subsurfaceBacklightingActive"));
            Assert.That(accumulate,
                Does.Contain("shadowEvaluationNormal = -shadowNormal;"));
            Assert.That(accumulate,
                Does.Contain("shadowFactor = EvaluateDirectionalShadow("));
            Assert.That(CountOccurrences(
                    accumulate,
                    "shadowFactor = EvaluateDirectionalShadowForEffectiveMode("),
                Is.EqualTo(1));
            Assert.That(CountOccurrences(
                    accumulate,
                    "shadowFactor = EvaluateDirectionalShadow("),
                Is.EqualTo(1));
            Assert.That(accumulate,
                Does.Contain("attenuation *= EvaluateNjulfIesProfile("));
            Assert.That(accumulate,
                Does.Contain("EvaluateGiDiffuseBrdf(\n            subsurfaceDirectionalDiffuseBase"));
            Assert.That(accumulate,
                Does.Contain("directBackDiffuseSource +="));
            Assert.That(area,
                Does.Contain("directDiffuseSource += area.diffuse * shadowFactor;"));
            Assert.That(area, Does.Not.Contain("directBackDiffuseSource"));
        });
    }

    [Test]
    public void DirectShaderContract_SplitsBeforeTraceAndForwardMrtC5Publication()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        int split = forward.IndexOf(
            "vec3 originalDirectDiffuseSource = directDiffuseSource;",
            StringComparison.Ordinal);
        int tracePublication = forward.IndexOf(
            "C5WriteDirectDiffuseAndEmissiveSource(",
            split,
            StringComparison.Ordinal);
        int diffuseCapture = forward.IndexOf(
            "if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE)",
            tracePublication,
            StringComparison.Ordinal);
        int specularCapture = forward.IndexOf(
            "if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR)",
            split,
            StringComparison.Ordinal);
        int forwardMrtPublication = forward.LastIndexOf(
            "C5WriteDirectDiffuseAndEmissiveSource(",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(split, Is.GreaterThanOrEqualTo(0));
            Assert.That(tracePublication, Is.GreaterThan(split));
            Assert.That(diffuseCapture, Is.GreaterThan(tracePublication));
            Assert.That(specularCapture, Is.GreaterThan(diffuseCapture));
            Assert.That(forwardMrtPublication, Is.GreaterThan(specularCapture));
            Assert.That(forward,
                Does.Contain("directLighting +=\n            directDiffuseSource - originalDirectDiffuseSource;"));
            Assert.That(forward,
                Does.Contain("directLighting - directDiffuseSource"));
            Assert.That(forward,
                Does.Contain("clamp(directDiffuseSource + emissive,"));
        });
    }

    [Test]
    public void IndirectShaderContract_SplitsBeforeFinalDiagnosticsWithoutExtraGather()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string receiverCache = ReadRepoText(
            "Njulf.Shaders",
            "forward_ddgi_receiver_cache.glsl");
        int oppositeEnvironment = forward.IndexOf(
            "vec3 subsurfaceBackEnvironmentIrradiance =",
            StringComparison.Ordinal);
        int finalSplit = forward.IndexOf(
            "finalDiffuseIndirect = ApplyGiSubsurfaceDiffuseSplit(",
            oppositeEnvironment,
            StringComparison.Ordinal);
        int finalDebug = forward.IndexOf(
            "if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)",
            finalSplit,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(oppositeEnvironment, Is.GreaterThanOrEqualTo(0));
            Assert.That(forward,
                Does.Contain("EvaluateEnvironmentDiffuseIrradiance(\n                environment,\n                -normal);"));
            Assert.That(forward,
                Does.Contain("subsurfaceDiffuseReflectance) * indirectAo;"));
            Assert.That(forward,
                Does.Contain("simpleHybridDiagnostics.diffuse = diagnosticFinalDiffuseIndirect;"));
            Assert.That(finalSplit, Is.GreaterThan(oppositeEnvironment));
            Assert.That(finalDebug, Is.GreaterThan(finalSplit));
            Assert.That(CountOccurrences(
                    forward,
                    "SampleSimpleDdgiGather("),
                Is.EqualTo(1));
            Assert.That(receiverCache, Does.Contain("uvec4 Entries[];"));
            Assert.That(receiverCache, Does.Contain("uvec4 Packed;"));
            Assert.That(forward,
                Does.Not.Contain("ddgiDirectionalQueryForSubsurface"));
        });
    }

    [Test]
    public void RayQueryBuildContract_AvoidsGlslangIdOverflowForAllVariants()
    {
        string shaderProject = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        string atomicVerification = ReadRepoText(
            "Njulf.Shaders",
            "VerifyProductionDiagnosticAtomics.ps1");

        Assert.Multiple(() =>
        {
            Assert.That(shaderProject,
                Does.Contain("<NjulfForwardRayQueryOptimizationOptions>-Od</NjulfForwardRayQueryOptimizationOptions>"));
                Assert.That(CountOccurrences(
                    shaderProject,
                    "<AdditionalCompileOptions>$(NjulfForwardRayQueryOptimizationOptions)</AdditionalCompileOptions>"),
                Is.EqualTo(10));
            Assert.That(shaderProject,
                Does.Contain("<AdditionalCompileOptions>%(ReceiverFeedbackGraphicsShaderVariant.AdditionalCompileOptions)</AdditionalCompileOptions>"));
            Assert.That(atomicVerification,
                Does.Contain("'forward_transparent_ray.frag.spv' = 17"));
            Assert.That(atomicVerification,
                Does.Contain("'forward_weighted_oit_ray.frag.spv' = 17"));
        });
    }

    [Test]
    public void LegacyViewDependentWrap_IsAbsent()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(forward,
                Does.Not.Contain("float wrap = clamp(dot(normal, viewDirection)"));
            Assert.That(forward,
                Does.Not.Contain("color += albedo * subsurfaceColor * subsurfaceStrength"));
        });
    }

    private static void AssertVector(
        Vector3 expected,
        Vector3 actual,
        float tolerance = Tolerance)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(tolerance));
        });
    }

    private static void AssertComponentwiseLessThanOrEqual(
        Vector3 actual,
        Vector3 maximum)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.X, Is.LessThanOrEqualTo(maximum.X + Tolerance));
            Assert.That(actual.Y, Is.LessThanOrEqualTo(maximum.Y + Tolerance));
            Assert.That(actual.Z, Is.LessThanOrEqualTo(maximum.Z + Tolerance));
        });
    }

    private static string ExtractFunction(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0),
            $"Missing function signature: {signature}");
        int openingBrace = source.IndexOf('{', start);
        Assert.That(openingBrace, Is.GreaterThan(start));

        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..(index + 1)];
        }

        Assert.Fail($"Unterminated function: {signature}");
        return string.Empty;
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, segments));
    }
}
