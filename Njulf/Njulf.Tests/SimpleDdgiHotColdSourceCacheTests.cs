using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiHotColdSourceCacheTests
{
    [Test]
    public void Admission_RequiresMeasuredColdWorkAndRevokesHitHeavyWorkHysteretically()
    {
        var model = new SimpleDdgiHotColdAdmissionModel();

        Assert.That(model.Observe(new SimpleDdgiHotColdAdmissionSample(
            SurfaceHitCount: 128,
            MissCount: 1_792,
            RejectedBackFaceCount: 128)), Is.False);
        Assert.That(model.State.Admitted, Is.False);

        Assert.That(model.Observe(new SimpleDdgiHotColdAdmissionSample(
            SurfaceHitCount: 128,
            MissCount: 1_792,
            RejectedBackFaceCount: 128)), Is.True);
        Assert.That(model.State.Admitted, Is.True);

        bool changed = false;
        for (int sample = 0; sample < 32; sample++)
        {
            changed |= model.Observe(new SimpleDdgiHotColdAdmissionSample(
                SurfaceHitCount: 4_096,
                MissCount: 0,
                RejectedBackFaceCount: 0));
        }

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(model.State.Admitted, Is.False);
            Assert.That(model.State.ColdExitFraction,
                Is.LessThanOrEqualTo(SimpleDdgiHotColdAdmissionModel.RevocationColdFraction));
            Assert.That(model.State.Reason,
                Does.Contain("fixed-record").Or.Contain("hit-heavy"));
        });
    }

    [Test]
    public void Admission_IsBoundToLayoutIdentityAndRetainsAValidCompletedSample()
    {
        var model = new SimpleDdgiHotColdAdmissionModel();
        const ulong firstIdentity = 17UL;
        const ulong secondIdentity = 18UL;

        for (int sample = 0; sample < 2; sample++)
        {
            model.Observe(
                new SimpleDdgiHotColdAdmissionSample(
                    SurfaceHitCount: 128,
                    MissCount: 1_792,
                    RejectedBackFaceCount: 128),
                firstIdentity,
                sampleFrameSerial: (ulong)(100 + sample));
        }

        Assert.Multiple(() =>
        {
            Assert.That(model.State.Admitted, Is.True);
            Assert.That(model.State.HasCompletedSampleForIdentity, Is.True);
            Assert.That(model.State.LayoutIdentity, Is.EqualTo(firstIdentity));
            Assert.That(model.State.LastCompletedSampleFrameSerial, Is.EqualTo(101UL));
        });

        model.Observe(default, firstIdentity);
        Assert.Multiple(() =>
        {
            Assert.That(model.State.Admitted, Is.True);
            Assert.That(model.State.Reason, Does.Contain("retained-no-new-sample"));
        });

        Assert.That(model.EnsureIdentity(secondIdentity), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(model.State.Admitted, Is.False);
            Assert.That(model.State.HasCompletedSampleForIdentity, Is.False);
            Assert.That(model.State.LayoutIdentity, Is.EqualTo(secondIdentity));
            Assert.That(model.State.LastCompletedSampleFrameSerial, Is.Zero);
            Assert.That(model.State.Reason,
                Is.EqualTo("awaiting-completed-work-sample"));
        });
    }

    [TestCase(SimpleDdgiTransportCacheFormat.Compact24)]
    [TestCase(SimpleDdgiTransportCacheFormat.Compact28)]
    public void TransposedProbe_ReconstructsEveryFixedRecordBitExactly(
        SimpleDdgiTransportCacheFormat format)
    {
        const int rays = 7;
        int stride = format.WordCount();
        var fixedRecords = new uint[rays * stride];
        for (int ray = 0; ray < rays; ray++)
        {
            var sample = new SimpleDdgiTransportCachePacking.Sample(
                SourceRadiance: new Vector3(0.25f + ray, 0.5f, 1.0f),
                Distance: 2.0f + ray,
                Direction: Vector3.Normalize(new Vector3(1.0f, ray + 1.0f, 0.5f)),
                Normal: Vector3.UnitY,
                DiffuseReflectance: new Vector3(0.2f, 0.4f, 0.6f),
                TransmittedDiffuseReflectance: new Vector3(0.1f, 0.05f, 0.0f),
                MaterialOcclusion: 0.75f,
                HitKind: ray % 5,
                ProbeGeneration: 17,
                SourceLightingGeneration: 3,
                SourceEpoch: 9,
                SourceRayCount: rays);
            Assert.That(SimpleDdgiTransportCachePacking.Pack(
                format,
                sample,
                fixedRecords.AsSpan(ray * stride, stride),
                out _), Is.EqualTo(stride));
        }

        var transposed = new uint[fixedRecords.Length];
        SimpleDdgiHotColdCacheLayout.TransposeProbeFromFixedRecords(
            format,
            fixedRecords,
            rays,
            transposed);

        var reconstructed = new uint[stride];
        for (int ray = 0; ray < rays; ray++)
        {
            SimpleDdgiHotColdCacheLayout.CopyRecordToFixedOracle(
                format,
                transposed,
                ray,
                rays,
                reconstructed);
            Assert.That(reconstructed,
                Is.EqualTo(fixedRecords.AsSpan(ray * stride, stride).ToArray()),
                $"ray {ray}");
        }
    }

    [Test]
    public void LayoutCompiler_FingerprintsAndFlagsHotColdRepresentation()
    {
        SimpleDdgiTransportCacheRegionRequest request = Request();
        SimpleDdgiStorageLayout fixedLayout =
            SimpleDdgiStorageLayoutCompiler.Compile([request]);
        SimpleDdgiStorageLayout hotColdLayout =
            SimpleDdgiStorageLayoutCompiler.Compile(
                [request with { UseHotColdLayout = true }]);
        SimpleDdgiTransportCacheRegion region = hotColdLayout.Regions.Single();
        uint flags = SimpleDdgiStorageLayoutCompiler.PackVolumeFlags(
            region.Format,
            irradianceMirrorPresent: false,
            visibilityMirrorPresent: false,
            hotColdLayout.AbiVersion,
            hotColdLayout.DirectionCodebookVersion,
            hotColdLayout: true);

        Assert.Multiple(() =>
        {
            Assert.That((uint)hotColdLayout.AbiVersion, Is.EqualTo(7u));
            Assert.That(region.UsesHotColdLayout, Is.True);
            Assert.That(hotColdLayout.SourceCacheBytes,
                Is.EqualTo(fixedLayout.SourceCacheBytes));
            Assert.That(hotColdLayout.Fingerprint,
                Is.Not.EqualTo(fixedLayout.Fingerprint));
            Assert.That(flags & SimpleDdgiHotColdCacheLayout.LayoutFlag,
                Is.Not.Zero);
            Assert.That(
                SimpleDdgiHotColdCacheLayout.ResolveGenerationWord(
                    region.Format, 6),
                Is.LessThan(
                    SimpleDdgiHotColdCacheLayout.ResolvePayloadWord(
                        region.Format, 0, 7)));
        });
    }

    [Test]
    public void MissHeavySolveEstimate_ReadsOnlyHotHeadersForColdExits()
    {
        const ulong rayCount = 10_000;
        ulong fixedBytes = SimpleDdgiHotColdAdmissionModel.EstimateSolveReadBytes(
            SimpleDdgiTransportCacheFormat.Compact24,
            rayCount,
            coldExitFraction: 0.8f,
            hotColdLayout: false);
        ulong hotColdBytes = SimpleDdgiHotColdAdmissionModel.EstimateSolveReadBytes(
            SimpleDdgiTransportCacheFormat.Compact24,
            rayCount,
            coldExitFraction: 0.8f,
            hotColdLayout: true);
        ulong hitHeavyBytes = SimpleDdgiHotColdAdmissionModel.EstimateSolveReadBytes(
            SimpleDdgiTransportCacheFormat.Compact24,
            rayCount,
            coldExitFraction: 0.0f,
            hotColdLayout: true);

        Assert.Multiple(() =>
        {
            Assert.That(hotColdBytes, Is.LessThan(fixedBytes));
            Assert.That(hitHeavyBytes, Is.EqualTo(fixedBytes));
        });
    }

    [Test]
    public void ShaderAbi_UsesHeaderGenerationAndConditionalSurfacePayload()
    {
        string abi = ReadRepoText("Njulf.Shaders", "ddgi_simple_storage_abi.glsl");
        string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
        string commit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_commit_local.comp");
        string audit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_transport_audit.comp");

        Assert.Multiple(() =>
        {
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_STORAGE_HOT_COLD_LAYOUT_BIT = 1u << 16u"));
            Assert.That(abi, Does.Contain("SimpleDdgiStorageSurfacePayloadWord"));
            Assert.That(shared, Does.Contain("requiresSurfacePayload"));
            Assert.That(shared, Does.Contain("writeSurfacePayload"));
            Assert.That(commit, Does.Contain("SimpleDdgiStorageGenerationWord"));
            Assert.That(audit, Does.Contain("SimpleDdgiStorageGenerationWord"));
        });
    }

    private static SimpleDdgiTransportCacheRegionRequest Request() => new(
        VolumeIndex: 0,
        Identity: "outdoor",
        SourceOrdinal: 0,
        PhysicalFirstProbe: 0,
        PhysicalProbeCount: 8,
        RaysPerProbe: 32,
        GridCountX: 2,
        GridCountY: 2,
        GridCountZ: 2,
        Spacing: 1.0f,
        ArchitecturalThickness: 0.1f,
        PackingMode: SimpleDdgiStoragePackingMode.Packed);

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException(
            "Could not locate repository file.", Path.Combine(pathParts));
    }
}
