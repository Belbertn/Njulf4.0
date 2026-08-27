using System.Numerics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class HybridReflectionPayloadCodecTests
{
    private const uint Oct12Mask = 0x0fffu;

    [Test]
    public void GeometricNormalAndSchedulingRoughness_RoundTripWithinQuantizationBounds()
    {
        var random = new Random(0x4e4a554c);
        for (int sample = 0; sample < 20_000; sample++)
        {
            Vector3 normal;
            do
            {
                normal = new Vector3(
                    random.NextSingle() * 2.0f - 1.0f,
                    random.NextSingle() * 2.0f - 1.0f,
                    random.NextSingle() * 2.0f - 1.0f);
            }
            while (normal.LengthSquared() < 1.0e-8f);

            normal = Vector3.Normalize(normal);
            float schedulingRoughness = random.NextSingle();
            uint packed = PackNormalAndSchedulingRoughness(
                normal,
                schedulingRoughness);
            Vector3 decodedNormal = DecodeNormal(packed);
            float decodedSchedulingRoughness = (packed >> 24) / 255.0f;

            Assert.Multiple(() =>
            {
                Assert.That(
                    Vector3.Dot(normal, decodedNormal),
                    Is.GreaterThan(0.99999f));
                Assert.That(
                    decodedSchedulingRoughness,
                    Is.EqualTo(schedulingRoughness)
                        .Within(0.5f / 255.0f + 1.0e-7f));
            });
        }
    }

    [Test]
    public void PhysicalAndSchedulingRoughness_OccupyIndependentPayloadFields()
    {
        const float physicalRoughness = 0.05f;
        const float schedulingRoughness = 0.63f;
        uint x = PackNormalAndSchedulingRoughness(
            Vector3.Normalize(new Vector3(0.31f, 0.91f, -0.27f)),
            schedulingRoughness);
        uint z = PackF0AndPhysicalRoughness(
            new Vector3(0.04f, 0.7f, 0.95f),
            physicalRoughness);

        Assert.Multiple(() =>
        {
            Assert.That(
                (z >> 24) / 255.0f,
                Is.EqualTo(physicalRoughness).Within(0.5f / 255.0f));
            Assert.That(
                (x >> 24) / 255.0f,
                Is.EqualTo(schedulingRoughness).Within(0.5f / 255.0f));
            Assert.That(z >> 24, Is.Not.EqualTo(x >> 24));
        });
    }

    [Test]
    public void ShaderContract_UsesPhysicalRoughnessForShadingAndSchedulingForAdmission()
    {
        string root = FindRepositoryRoot();
        string payload = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Shaders",
            "hybrid_reflection_payload.glsl"));
        string ssr = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Shaders",
            "hybrid_reflection_ssr.comp"));
        string resolve = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Shaders",
            "hybrid_reflection_resolve.comp"));
        string composite = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Shaders",
            "hybrid_reflection_composite.comp"));

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain(
                "NJULF_HYBRID_REFLECTION_PAYLOAD_ABI_VERSION = 4u"));
            Assert.That(payload, Does.Contain(
                "HybridReflectionPayloadPhysicalRoughness"));
            Assert.That(payload, Does.Contain(
                "HybridReflectionPayloadSchedulingRoughness"));
            Assert.That(payload, Does.Not.Contain(
                "float HybridReflectionPayloadRoughness"));
            Assert.That(ssr, Does.Contain(
                "HybridReflectionPayloadSchedulingRoughness(payload)"));
            Assert.That(resolve, Does.Contain(
                "HybridReflectionPayloadPhysicalRoughness(payload)"));
            Assert.That(composite, Does.Contain("pc.DebugView == 17u"));
            Assert.That(composite, Does.Contain(
                "abs(scheduling - physical)"));
        });
    }

    private static uint PackNormalAndSchedulingRoughness(
        Vector3 normal,
        float schedulingRoughness)
    {
        Vector2 encoded = OctEncode(normal) * 0.5f +
            new Vector2(0.5f);
        uint x = (uint)MathF.Round(
            Math.Clamp(encoded.X, 0.0f, 1.0f) * Oct12Mask);
        uint y = (uint)MathF.Round(
            Math.Clamp(encoded.Y, 0.0f, 1.0f) * Oct12Mask);
        uint roughness = (uint)MathF.Round(
            Math.Clamp(schedulingRoughness, 0.0f, 1.0f) * 255.0f);
        return x | (y << 12) | (roughness << 24);
    }

    private static Vector3 DecodeNormal(uint packed)
    {
        var encoded = new Vector2(
            (packed & Oct12Mask) / (float)Oct12Mask * 2.0f - 1.0f,
            ((packed >> 12) & Oct12Mask) / (float)Oct12Mask * 2.0f - 1.0f);
        var normal = new Vector3(
            encoded,
            1.0f - MathF.Abs(encoded.X) - MathF.Abs(encoded.Y));
        if (normal.Z < 0.0f)
        {
            float oldX = normal.X;
            normal.X = (1.0f - MathF.Abs(normal.Y)) * SignNotZero(oldX);
            normal.Y = (1.0f - MathF.Abs(oldX)) * SignNotZero(normal.Y);
        }
        return Vector3.Normalize(normal);
    }

    private static Vector2 OctEncode(Vector3 value)
    {
        Vector3 normal = Vector3.Normalize(value);
        normal /= MathF.Abs(normal.X) + MathF.Abs(normal.Y) +
            MathF.Abs(normal.Z);
        if (normal.Z < 0.0f)
        {
            float oldX = normal.X;
            normal.X = (1.0f - MathF.Abs(normal.Y)) * SignNotZero(oldX);
            normal.Y = (1.0f - MathF.Abs(oldX)) * SignNotZero(normal.Y);
        }
        return new Vector2(normal.X, normal.Y);
    }

    private static uint PackF0AndPhysicalRoughness(
        Vector3 f0,
        float physicalRoughness)
    {
        uint x = (uint)MathF.Round(Math.Clamp(f0.X, 0.0f, 1.0f) * 255.0f);
        uint y = (uint)MathF.Round(Math.Clamp(f0.Y, 0.0f, 1.0f) * 255.0f);
        uint z = (uint)MathF.Round(Math.Clamp(f0.Z, 0.0f, 1.0f) * 255.0f);
        uint roughness = (uint)MathF.Round(
            Math.Clamp(physicalRoughness, 0.0f, 1.0f) * 255.0f);
        return x | (y << 8) | (z << 16) | (roughness << 24);
    }

    private static float SignNotZero(float value) => value >= 0.0f ? 1.0f : -1.0f;

    private static string FindRepositoryRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(Path.Combine(directory, "Njulf.Shaders")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root containing Njulf.Shaders.");
    }
}
