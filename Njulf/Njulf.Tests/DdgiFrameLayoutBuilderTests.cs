using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DdgiFrameLayoutBuilderTests
    {
        [Test]
        public void Build_WhenCameraRelativeDisabled_PreservesDefaultSceneBoundsFallback()
        {
            var scene = new Scene();
            scene.Add(new RenderObject { Position = new Vector3(2.0f, 3.0f, 4.0f) });
            var camera = new FirstPersonCamera(Vector3.Zero);
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiCameraRelativeEnabled = false
            };

            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                new CameraRelativeDdgiClipmapController(),
                1,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.IsDdgiActive, Is.True);
                Assert.That(layout.CameraRelativeEnabled, Is.False);
                Assert.That(layout.DefaultVolumeIncluded, Is.True);
                Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(0));
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(0));
                Assert.That(layout.Volumes, Has.Count.EqualTo(1));
                Assert.That(layout.Volumes[0].Name, Is.EqualTo("Default DDGI Volume"));
                Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(layout.Volumes[0].ProbeCount));
                Assert.That(layout.DirtyProbeRequests, Is.Empty);
            });
        }

        [Test]
        public void Build_WhenCameraRelativeEnabled_EmitsCascadesThenAuthoredVolumesWithoutDefaultFallback()
        {
            var scene = new Scene();
            var authored = new GlobalIlluminationProbeVolume
            {
                Name = "Hero Interior",
                Origin = new Vector3(-2.0f, 0.0f, -2.0f),
                Size = new Vector3(4.0f, 3.0f, 4.0f),
                ProbeCountX = 3,
                ProbeCountY = 2,
                ProbeCountZ = 3
            };
            scene.Add(authored);
            var camera = new FirstPersonCamera(new Vector3(10.2f, 1.0f, -5.2f));
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();

            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                new CameraRelativeDdgiClipmapController(),
                10,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.IsDdgiActive, Is.True);
                Assert.That(layout.CameraRelativeEnabled, Is.True);
                Assert.That(layout.DefaultVolumeIncluded, Is.False);
                Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(1));
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(2));
                Assert.That(layout.Volumes, Has.Count.EqualTo(3));
                Assert.That(layout.Volumes[0].Name, Is.EqualTo("Camera DDGI Cascade 0"));
                Assert.That(layout.Volumes[1].Name, Is.EqualTo("Camera DDGI Cascade 1"));
                Assert.That(layout.Volumes[2], Is.SameAs(authored));
                Assert.That(layout.Volumes[0].MaxRayDistance, Is.EqualTo(settings.DdgiCascade0MaxRayDistance));
                Assert.That(layout.Volumes[1].MaxRayDistance, Is.EqualTo(settings.DdgiCascade1MaxRayDistance));
                Assert.That(layout.AuthoredProbeCount, Is.EqualTo(authored.ProbeCount));
                Assert.That(layout.CameraRelativeProbeCount, Is.EqualTo(4 * 2 * 4 * 2));
                Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(authored.ProbeCount + 4 * 2 * 4 * 2));
                Assert.That(layout.VolumeMetadata, Has.Count.EqualTo(3));
                Assert.That(layout.VolumeMetadata[0].Kind, Is.EqualTo(DdgiProbeVolumeKind.CameraClipmap));
                Assert.That(layout.VolumeMetadata[0].CascadeIndex, Is.EqualTo(0));
                Assert.That(layout.VolumeMetadata[0].LogicalGridMinX, Is.EqualTo(8));
                Assert.That(layout.VolumeMetadata[0].LogicalGridMinY, Is.EqualTo(0));
                Assert.That(layout.VolumeMetadata[0].LogicalGridMinZ, Is.EqualTo(-8));
                Assert.That(layout.VolumeMetadata[0].EdgeBlendFraction, Is.EqualTo(settings.DdgiClipmapEdgeBlendFraction));
                Assert.That(layout.VolumeMetadata[2].Kind, Is.EqualTo(DdgiProbeVolumeKind.Authored));
                Assert.That(layout.VolumeMetadata[2].Flags & GlobalIlluminationProbeVolumeData.VolumeAuthoredPriorityFlag, Is.Not.EqualTo(0));
                Assert.That(layout.DirtyProbeRequests, Has.Count.EqualTo(2));
                Assert.That(layout.DirtyProbeRequests[0].VolumeIndex, Is.EqualTo(0));
                Assert.That(layout.DirtyProbeRequests[0].PhysicalFirstProbeIndex, Is.EqualTo(0));
                Assert.That(layout.DirtyProbeRequests[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.LayoutChange));
            });
        }

        [Test]
        public void Build_WhenCameraRelativeScrolls_ProducesDirtyProbeRequestsForNewSlabs()
        {
            var scene = new Scene();
            var camera = new FirstPersonCamera(Vector3.Zero);
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            var controller = new CameraRelativeDdgiClipmapController();

            DdgiFrameLayout first = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                1,
                cameraCut: false);
            camera.Position = new Vector3(1.1f, 0.0f, 0.0f);
            DdgiFrameLayout second = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                2,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(first.DirtyProbeRequests, Has.Count.EqualTo(2));
                Assert.That(second.DefaultVolumeIncluded, Is.False);
                Assert.That(second.DirtyProbeRequests, Has.Count.EqualTo(1));
                Assert.That(second.DirtyProbeRequests[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.Scroll));
                Assert.That(second.DirtyProbeRequests[0].MinCell, Is.EqualTo(new DdgiClipmapCell(2, -1, -2)));
                Assert.That(second.DirtyProbeRequests[0].MaxCell, Is.EqualTo(new DdgiClipmapCell(2, 0, 1)));
            });
        }

        [Test]
        public void Build_WhenCameraTurnsInPlace_PreservesWorldAlignedCascadesAndUpdatesViewPriority()
        {
            var scene = new Scene();
            var camera = new FirstPersonCamera(Vector3.Zero);
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            settings.DdgiClipmapProbeCountX = 24;
            settings.DdgiClipmapProbeCountY = 8;
            settings.DdgiClipmapProbeCountZ = 24;
            settings.DdgiClipmapBaseSpacing = 1.25f;
            var controller = new CameraRelativeDdgiClipmapController();

            DdgiFrameLayout first = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                1,
                cameraCut: false);
            camera.Yaw += System.MathF.PI * 0.5f;
            DdgiFrameLayout second = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                2,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(first.VolumeMetadata[0].LogicalGridMinX, Is.EqualTo(-12));
                Assert.That(first.VolumeMetadata[0].LogicalGridMinZ, Is.EqualTo(-12));
                Assert.That(second.VolumeMetadata[0].LogicalGridMinX, Is.EqualTo(first.VolumeMetadata[0].LogicalGridMinX));
                Assert.That(second.VolumeMetadata[0].LogicalGridMinZ, Is.EqualTo(-12));
                Assert.That(second.Volumes[0].Origin, Is.EqualTo(first.Volumes[0].Origin));
                Assert.That(second.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Normal));
                Assert.That(second.DirtyProbeRequests, Is.Empty);
                Assert.That(first.ViewPriority.Forward.Z, Is.LessThan(-0.9f));
                Assert.That(second.ViewPriority.Forward.X, Is.GreaterThan(0.9f));
            });
        }

        [Test]
        public void Build_WhenCameraPitchesUp_PreservesWorldAlignedCascadesAndUpdatesViewPriority()
        {
            var scene = new Scene();
            var camera = new FirstPersonCamera(Vector3.Zero);
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            settings.DdgiClipmapProbeCountX = 24;
            settings.DdgiClipmapProbeCountY = 8;
            settings.DdgiClipmapProbeCountZ = 24;
            settings.DdgiClipmapBaseSpacing = 1.25f;
            var controller = new CameraRelativeDdgiClipmapController();

            DdgiFrameLayout first = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                1,
                cameraCut: false);
            camera.Pitch = -System.MathF.PI * 0.5f;
            DdgiFrameLayout second = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                2,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(first.VolumeMetadata[0].LogicalGridMinY, Is.EqualTo(-4));
                Assert.That(second.VolumeMetadata[0].LogicalGridMinY, Is.EqualTo(first.VolumeMetadata[0].LogicalGridMinY));
                Assert.That(second.VolumeMetadata[0].LogicalGridMinX, Is.EqualTo(-12));
                Assert.That(second.VolumeMetadata[0].LogicalGridMinZ, Is.EqualTo(-12));
                Assert.That(second.Volumes[0].Origin, Is.EqualTo(first.Volumes[0].Origin));
                Assert.That(second.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Normal));
                Assert.That(second.DirtyProbeRequests, Is.Empty);
                Assert.That(first.ViewPriority.Forward.Z, Is.LessThan(-0.9f));
                Assert.That(second.ViewPriority.Forward.Y, Is.GreaterThan(0.9f));
            });
        }

        [Test]
        public void WithDirtyBounds_WhenCameraRelativeEnabled_ConvertsBoundsToCascadeCellRequests()
        {
            var scene = new Scene();
            var camera = new FirstPersonCamera(Vector3.Zero);
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            var controller = new CameraRelativeDdgiClipmapController();

            _ = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                1,
                cameraCut: false);
            DdgiFrameLayout stable = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                2,
                cameraCut: false);

            DdgiFrameLayout dirtied = stable.WithDirtyBounds(new[]
            {
                new BoundingBox(new Vector3(-0.1f, -0.1f, -1.1f), new Vector3(0.1f, 0.1f, -0.9f))
            });

            Assert.Multiple(() =>
            {
                Assert.That(stable.DirtyProbeRequests, Is.Empty);
                Assert.That(dirtied.DirtyProbeRequests, Has.Count.EqualTo(2));
                Assert.That(dirtied.DirtyProbeRequests[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.DirtyBounds));
                Assert.That(dirtied.DirtyProbeRequests[0].CascadeIndex, Is.EqualTo(0));
                Assert.That(dirtied.DirtyProbeRequests[0].MinCell.X, Is.LessThanOrEqualTo(0));
                Assert.That(dirtied.DirtyProbeRequests[0].MaxCell.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(dirtied.DirtyProbeRequests[0].MinCell.Z, Is.LessThanOrEqualTo(-1));
                Assert.That(dirtied.DirtyProbeRequests[0].MaxCell.Z, Is.GreaterThanOrEqualTo(-1));
                Assert.That(dirtied.DirtyProbeRequests[0].PhysicalFirstProbeIndex, Is.EqualTo(0));
                Assert.That(dirtied.DirtyProbeRequests[1].CascadeIndex, Is.EqualTo(1));
                Assert.That(dirtied.DirtyProbeRequests[1].PhysicalFirstProbeIndex, Is.EqualTo(4 * 2 * 4));
            });
        }

        [Test]
        public void Build_WhenDdgiInactive_ReturnsEmptyLayout()
        {
            var scene = new Scene();
            scene.Add(new GlobalIlluminationProbeVolume());
            var settings = new GlobalIlluminationSettings
            {
                Enabled = false,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiCameraRelativeEnabled = true
            };

            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                new FirstPersonCamera(Vector3.Zero),
                settings,
                new CameraRelativeDdgiClipmapController(),
                1,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.IsDdgiActive, Is.False);
                Assert.That(layout.Volumes, Is.Empty);
                Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(0));
                Assert.That(layout.DirtyProbeRequests, Is.Empty);
            });
        }

        [Test]
        public void Build_WhenAuthoredVolumesExceedLimit_ReservesCameraRelativeCascadeSlots()
        {
            var scene = new Scene();
            for (int i = 0; i < DdgiProbeVolumeManager.AbsoluteMaxVolumeCount + 4; i++)
            {
                scene.Add(new GlobalIlluminationProbeVolume
                {
                    Name = $"Authored {i}",
                    ProbeCountX = 2,
                    ProbeCountY = 2,
                    ProbeCountZ = 2
                });
            }

            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            settings.DdgiClipmapCascadeCount = 4;
            var layout = DdgiFrameLayoutBuilder.Build(
                scene,
                new FirstPersonCamera(Vector3.Zero),
                settings,
                new CameraRelativeDdgiClipmapController(),
                1,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.Volumes, Has.Count.EqualTo(DdgiProbeVolumeManager.AbsoluteMaxVolumeCount));
                Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(DdgiProbeVolumeManager.AbsoluteMaxVolumeCount - 4));
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(4));
                Assert.That(layout.DefaultVolumeIncluded, Is.False);
                Assert.That(layout.Volumes[0].Name, Is.EqualTo("Camera DDGI Cascade 0"));
                Assert.That(layout.Volumes[3].Name, Is.EqualTo("Camera DDGI Cascade 3"));
                Assert.That(layout.Volumes[4].Name, Is.EqualTo("Authored 0"));
            });
        }

        [Test]
        public void Build_EnforcesResolvedActiveProbeBudgetBeforeEmittingCascades()
        {
            var scene = new Scene();
            scene.Add(new GlobalIlluminationProbeVolume
            {
                Name = "Small Authored",
                ProbeCountX = 2,
                ProbeCountY = 2,
                ProbeCountZ = 2
            });
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            settings.DdgiMaxActiveProbes = 40;

            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                new FirstPersonCamera(Vector3.Zero),
                settings,
                new CameraRelativeDdgiClipmapController(),
                1,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.AuthoredProbeCount, Is.EqualTo(8));
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(1));
                Assert.That(layout.CameraRelativeProbeCount, Is.EqualTo(32));
                Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(40));
                Assert.That(layout.Volumes[0].RaysPerProbe, Is.EqualTo(settings.DdgiCascade0RaysPerProbe));
                Assert.That(layout.Volumes[0].MaxProbeUpdatesPerFrame, Is.LessThanOrEqualTo(settings.DdgiMaxProbeUpdatesPerFrame));
            });
        }

        [Test]
        public void Build_ReservesCameraClipmapProbeBudgetBeforeAuthoredVolumes()
        {
            var scene = new Scene();
            scene.Add(new GlobalIlluminationProbeVolume
            {
                Name = "Huge Local Room",
                ProbeCountX = 4,
                ProbeCountY = 4,
                ProbeCountZ = 4
            });
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            settings.DdgiMaxActiveProbes = 64;
            settings.DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 64UL;

            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                new FirstPersonCamera(Vector3.Zero),
                settings,
                new CameraRelativeDdgiClipmapController(),
                1,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(2));
                Assert.That(layout.CameraRelativeProbeCount, Is.EqualTo(64));
                Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(0));
                Assert.That(layout.AuthoredProbeCount, Is.EqualTo(0));
                Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(64));
            });
        }

        [Test]
        public void Build_AdmitsNearbyAuthoredVolumesFromLeftoverBudget()
        {
            var scene = new Scene();
            var far = new GlobalIlluminationProbeVolume
            {
                Name = "Far Room",
                Origin = new Vector3(100.0f, 0.0f, 100.0f),
                Size = new Vector3(4.0f, 3.0f, 4.0f),
                ProbeCountX = 2,
                ProbeCountY = 2,
                ProbeCountZ = 2
            };
            var near = new GlobalIlluminationProbeVolume
            {
                Name = "Near Room",
                Origin = new Vector3(-1.0f, -1.0f, -1.0f),
                Size = new Vector3(2.0f, 2.0f, 2.0f),
                ProbeCountX = 2,
                ProbeCountY = 2,
                ProbeCountZ = 2
            };
            scene.Add(far);
            scene.Add(near);
            GlobalIlluminationSettings settings = CreateCameraRelativeSettings();
            settings.DdgiClipmapCascadeCount = 1;
            settings.DdgiMaxActiveProbes = 40;
            settings.DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 40UL;

            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                new FirstPersonCamera(Vector3.Zero),
                settings,
                new CameraRelativeDdgiClipmapController(),
                1,
                cameraCut: false);

            Assert.Multiple(() =>
            {
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(1));
                Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(1));
                Assert.That(layout.Volumes[0].Name, Is.EqualTo("Camera DDGI Cascade 0"));
                Assert.That(layout.Volumes[1], Is.SameAs(near));
            });
        }

        private static GlobalIlluminationSettings CreateCameraRelativeSettings()
        {
            return new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiCameraRelativeEnabled = true,
                DdgiClipmapCascadeCount = 2,
                DdgiClipmapProbeCountX = 4,
                DdgiClipmapProbeCountY = 2,
                DdgiClipmapProbeCountZ = 4,
                DdgiClipmapBaseSpacing = 1.0f,
                DdgiClipmapSpacingScale = 2.0f
            };
        }
    }
}
