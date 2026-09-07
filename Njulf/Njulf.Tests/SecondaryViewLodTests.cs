using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SecondaryViewLodTests
{
    [Test]
    public void ReflectionQualificationRouteHasNearFarAndAdjacentTemporalCheckpoints()
    {
        var kind = NjulfHelloGame.SampleBenchmarkTrajectoryKind.ReflectionLod;
        var near = NjulfHelloGame.SampleBenchmarkTrajectory.ResolveCamera(kind, 0, default)!;
        var far = NjulfHelloGame.SampleBenchmarkTrajectory.ResolveCamera(kind, 120, default)!;
        Assert.That(near.Position.Z, Is.EqualTo(6));
        Assert.That(far.Position.Z, Is.EqualTo(22));
        Assert.That(NjulfHelloGame.SampleBenchmarkQualityCheckpointCatalog.GetCheckpointIndices(kind),
            Does.Contain(119).And.Contain(120).And.Contain(121));
        Assert.Throws<ArgumentException>(() => NjulfHelloGame.SampleSmokeOptionsParser.Parse(
            ["--benchmark", "--benchmark-trajectory=reflection-lod", "--scene=Bistro"]));
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.Medium);
        NjulfHelloGame.SampleReflectionLodScene.ConfigureSettings(settings);
        Assert.That(settings.EffectiveResolutionScale, Is.EqualTo(1));
        Assert.That(settings.Reflections.CaptureLodTargetPixelError, Is.EqualTo(2));
    }

    private static MeshInfo Mesh => new()
    {
        MeshletCount = 100, MeshletLod1Count = 40, MeshletLod2Count = 10,
        MeshletLod1SimplificationError = 0.01f, MeshletLod2SimplificationError = 0.04f
    };

    private static readonly BoundingBox Bounds = new(new(-1, -1, -1), new(1, 1, 1));

    private static Matrix4x4 Projection(float fov = MathF.PI / 2) =>
        Matrix4x4.CreatePerspectiveFieldOfView(fov, 1, 0.1f, 1000);

    private static readonly SecondaryLodInstanceKey Key = new(Guid.NewGuid(), 0, new MeshHandle(1, 1));

    private static SecondaryLodHistoryContract Contract(bool probe = false) =>
        new(probe, 1000, 1000, Projection(), true, 1, 1, 0);

    [TestCase(1000u, 10f, 1)]
    [TestCase(250u, 10f, 2)]
    [TestCase(1000u, 2f, 0)]
    [TestCase(1000u, 50f, 2)]
    public void CapturePixelsAndDistanceControlDetail(uint size, float distance, int expected) =>
        Assert.That(SecondaryViewLodPolicy.Select(Mesh, Matrix4x4.Identity, Bounds,
            new(0, 0, distance), Projection(), size, size, 1), Is.EqualTo(expected));

    [Test]
    public void NarrowerFieldOfViewAndLargerScaleRequireMoreDetail()
    {
        Matrix4x4 scale = Matrix4x4.Identity;
        scale.M11 = -2;
        scale.M22 = 3;
        Assert.Multiple(() =>
        {
            Assert.That(SecondaryViewLodPolicy.Select(Mesh, Matrix4x4.Identity, Bounds,
                new(0, 0, 10), Projection(MathF.PI / 6), 1000, 1000, 1), Is.Zero);
            Assert.That(SecondaryViewLodPolicy.Select(Mesh, scale, Bounds,
                new(0, 0, 10), Projection(), 1000, 1000, 1), Is.Zero);
        });
    }

    [Test]
    public void TransformErrorBoundDominatesShearedMirroredDirections()
    {
        Matrix4x4 world = Matrix4x4.Identity;
        world.M11 = -2;
        world.M21 = 4;
        world.M32 = -3;
        float bound = SecondaryViewLodPolicy.ConservativeScale(world);
        foreach (Vector3 direction in new Vector3[]
                 {
                     Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                     new Vector3(1, 1, 1).Normalized(), new Vector3(-1, 1, 0).Normalized()
                 })
            Assert.That((direction * world).Length(), Is.LessThanOrEqualTo(bound + 1e-5f));
        Assert.That(SecondaryViewLodPolicy.ConservativeScale(Matrix4x4.Identity), Is.EqualTo(1));
    }

    [Test]
    public void UnsupportedOrUntrustedInputsKeepFullDetail()
    {
        MeshInfo mesh = Mesh;
        mesh.MeshletLod1SimplificationError = float.NaN;
        mesh.MeshletLod2SimplificationError = -1;
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            new(0, 0, 100), Projection(), 100, 100, 1), Is.Zero);
        mesh = Mesh;
        mesh.IsSkinned = true;
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            new(0, 0, 100), Projection(), 100, 100, 1), Is.Zero);
        Assert.That(SecondaryViewLodPolicy.Select(Mesh, Matrix4x4.Identity, Bounds,
            Vector3.Zero, Projection(), 100, 100, 1), Is.Zero);
        Assert.That(SecondaryViewLodPolicy.Select(Mesh, Matrix4x4.Identity, Bounds,
            new(0, 0, 100), Projection(), 0, 0, 1), Is.Zero);
        Assert.That(SecondaryViewLodPolicy.Select(Mesh, Matrix4x4.Identity, Bounds,
            new(0, 0, 100), Projection(), 100, 100, 1, forceFullDetail: true), Is.Zero);
    }

    [Test]
    public void MissingCoarseLodUsesAvailableFinerLevel()
    {
        MeshInfo mesh = Mesh;
        mesh.MeshletLod2Count = 0;
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            new(0, 0, 50), Projection(), 1000, 1000, 1), Is.EqualTo(1));
    }

    [Test]
    public void HysteresisResistsOscillationAndEventuallyTransitions()
    {
        // Orthographic projection gives independent exact errors: 0.9 and 1.1 pixels.
        MeshInfo mesh = Mesh;
        mesh.MeshletLod2Count = 0;
        mesh.MeshletLod1SimplificationError = 0.0018f;
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            Vector3.Zero, Matrix4x4.Identity, 1000, 1000, 1, previous: 0), Is.Zero);
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            Vector3.Zero, Matrix4x4.Identity, 1000, 1000, 1, previous: 1), Is.EqualTo(1));
        mesh.MeshletLod1SimplificationError = 0.0022f;
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            Vector3.Zero, Matrix4x4.Identity, 1000, 1000, 1, previous: 1), Is.EqualTo(1));
        mesh.MeshletLod1SimplificationError = 0.0024f;
        Assert.That(SecondaryViewLodPolicy.Select(mesh, Matrix4x4.Identity, Bounds,
            Vector3.Zero, Matrix4x4.Identity, 1000, 1000, 1, previous: 1), Is.Zero);
    }

    [Test]
    public void CubemapTicketFreezesRequestedAndEffectiveLodAcrossFaces()
    {
        var history = new SecondaryViewLodHistory();
        history.Begin(Contract(true), 1);
        Assert.That(history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false), Is.EqualTo(1));
        history.ObserveEffective(Key, 2, 10); // Resident fallback.
        history.SealSnapshot();
        history.Begin(Contract(true), 1);
        Assert.That(history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 2), false), Is.EqualTo(1));
        Assert.That(history.ResolveRequest(Key), Is.EqualTo(2));
        history.ObserveEffective(Key, 2, 10);
        history.SealSnapshot();
        Assert.Throws<SecondaryViewLodUnavailableException>(() => history.ObserveEffective(Key, 1, 40));
        Assert.Throws<SecondaryViewLodUnavailableException>(() => history.ObserveEffective(Key, 2, 0));
        history.Begin(Contract(true), 2);
        Assert.That(history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 2), false), Is.Zero);
    }

    [Test]
    public void ProbeMembershipOrPolicyChangesRequireRetry()
    {
        var history = new SecondaryViewLodHistory();
        history.Begin(Contract(true), 1);
        history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false);
        history.ObserveEffective(Key, 1, 40);
        history.SealSnapshot();
        history.Begin(Contract(true), 1);
        Assert.Throws<SecondaryViewLodUnavailableException>(() => history.SealSnapshot());
        Assert.Throws<SecondaryViewLodUnavailableException>(() => history.Select(
            Key with { Mesh = new MeshHandle(1, 2) }, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false));
        Assert.Throws<SecondaryViewLodUnavailableException>(() =>
            history.Begin(Contract(true) with { PixelError = 2 }, 1));
    }

    [Test]
    public void HistorySurvivesSkippedFramesAndJitterButResetsForCameraCuts()
    {
        var history = new SecondaryViewLodHistory();
        history.Begin(Contract(), 1);
        history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false);
        history.SealSnapshot();
        history.Commit();
        var jitter = Projection();
        jitter.M31 = 0.001f;
        history.Begin(Contract() with { Projection = jitter }, 1000);
        Assert.That(history.Count, Is.EqualTo(1));
        history.Begin(Contract() with { CameraCutSerial = 1 }, 1001);
        Assert.That(history.Count, Is.Zero);
        Assert.That(new SecondaryViewLodHistory().Count, Is.Zero);
    }

    [TestCase(RenderQualityPreset.Low, 4f)]
    [TestCase(RenderQualityPreset.Medium, 2f)]
    [TestCase(RenderQualityPreset.High, 1f)]
    [TestCase(RenderQualityPreset.DdgiHigh, 1f)]
    [TestCase(RenderQualityPreset.Ultra, 0.5f)]
    public void PresetsAndLegacyFilesChooseCaptureBudget(RenderQualityPreset preset, float expected)
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(preset);
        Assert.That(settings.Reflections.CaptureLodTargetPixelError, Is.EqualTo(expected));
        string path = Path.Combine(Path.GetTempPath(), $"njulf-capture-lod-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
                { Version = 27, QualityPreset = preset.ToString(), Reflections = new { Enabled = true } }));
            settings = RenderSettings.Load(path);
            Assert.That(settings.Reflections.CaptureLodTargetPixelError, Is.EqualTo(expected));
            Assert.That(settings.Reflections.CaptureLodEnabled, Is.True);
            settings.Reflections.CaptureLodEnabled = false;
            settings.Reflections.CaptureLodTargetPixelError = 3.25f;
            settings.Save(path);
            settings = RenderSettings.Load(path);
            Assert.That(settings.Reflections.CaptureLodEnabled, Is.False);
            Assert.That(settings.Reflections.CaptureLodTargetPixelError, Is.EqualTo(3.25f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void InvalidSettingValuesAreBoundedAndChangeCaptureIdentity()
    {
        var settings = new ReflectionSettings();
        uint enabled = settings.CaptureLodSignature;
        settings.CaptureLodEnabled = false;
        Assert.That(settings.CaptureLodSignature, Is.Not.EqualTo(enabled));
        settings.CaptureLodTargetPixelError = float.NaN;
        Assert.That(settings.CaptureLodTargetPixelError, Is.EqualTo(1));
        settings.CaptureLodTargetPixelError = -1;
        Assert.That(settings.CaptureLodTargetPixelError, Is.EqualTo(0.125f));
        settings.CaptureLodTargetPixelError = 100;
        Assert.That(settings.CaptureLodTargetPixelError, Is.EqualTo(8));
    }

    [Test]
    public void ResidencyFailureRestartsProbeTicketAndRetainsPublishedCube()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1);
        var history = new SecondaryViewLodHistory();
        scheduler.Register(0, Key.Entity, hasPublishedCapture: true);
        scheduler.Request(0, Key.Entity, new(1, 1, 1, 1, 1, 1, 1),
            ReflectionCaptureReason.Manual, default, 1, 1);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out var first), Is.True);
        history.Begin(Contract(true), first.Ticket.Serial);
        history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false);
        history.ObserveEffective(Key, 1, 40);
        history.SealSnapshot();
        scheduler.CompleteWork(first);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out var second), Is.True);
        Assert.That(second.Face, Is.EqualTo(1));
        history.Begin(Contract(true), second.Ticket.Serial);
        history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false);
        Assert.Throws<SecondaryViewLodUnavailableException>(() => history.ObserveEffective(Key, 2, 10));
        scheduler.FailActive(second.Ticket, retry: true);
        Assert.That(scheduler.HasPublishedCapture(0, Key.Entity), Is.True);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out var retry), Is.True);
        Assert.That(retry.Face, Is.Zero);
        Assert.That(retry.Ticket.Serial, Is.Not.EqualTo(first.Ticket.Serial));
        history.Begin(Contract(true), retry.Ticket.Serial);
        history.Select(Key, Mesh, Matrix4x4.Identity, Bounds, new(0, 0, 10), false);
        history.ObserveEffective(Key, 2, 10);
        history.SealSnapshot();
    }
}
