using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiValidationPlanTests
    {
        [Test]
        public void CameraPathValidation_CoversWalkSprintTurnBacktrackAndTeleportWithoutResourceChurn()
        {
            Scene scene = CreateSmallScene();
            GlobalIlluminationSettings settings = CreateValidationSettings(cascadeCount: 3);
            var controller = new CameraRelativeDdgiClipmapController();
            var camera = new FirstPersonCamera(Vector3.Zero);

            DdgiFrameLayout initial = Build(scene, camera, settings, controller, frame: 1, velocity: Vector3.Zero);
            ulong initialSignature = CreateResourceSignature(initial, settings);

            camera.Position = new Vector3(0.75f, 0.0f, -0.75f);
            DdgiFrameLayout slowWalk = Build(scene, camera, settings, controller, frame: 2, velocity: new Vector3(0.75f, 0.0f, -0.75f));

            camera.Position = new Vector3(4.25f, 0.0f, -4.25f);
            DdgiFrameLayout fastSprint = Build(scene, camera, settings, controller, frame: 3, velocity: new Vector3(3.5f, 0.0f, -3.5f));

            camera.Yaw += MathF.PI;
            DdgiFrameLayout turnInPlace = Build(scene, camera, settings, controller, frame: 4, velocity: Vector3.Zero);

            camera.Position = new Vector3(0.25f, 0.0f, -0.25f);
            DdgiFrameLayout backtrack = Build(scene, camera, settings, controller, frame: 5, velocity: new Vector3(-4.0f, 0.0f, 4.0f));

            camera.Position = new Vector3(120.0f, 0.0f, 120.0f);
            DdgiFrameLayout teleport = Build(scene, camera, settings, controller, frame: 6, velocity: new Vector3(119.75f, 0.0f, 120.25f));

            Assert.Multiple(() =>
            {
                Assert.That(slowWalk.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Normal));
                Assert.That(fastSprint.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Fast));
                Assert.That(turnInPlace.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Normal));
                Assert.That(backtrack.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Fast));
                Assert.That(teleport.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Teleport));
                Assert.That(teleport.DirtyProbeRequests, Has.Some.Matches<DdgiFrameLayoutDirtyProbeRequest>(request =>
                    request.Reason == DdgiClipmapDirtyReason.Teleport));
                Assert.That(CreateResourceSignature(slowWalk, settings), Is.EqualTo(initialSignature));
                Assert.That(CreateResourceSignature(fastSprint, settings), Is.EqualTo(initialSignature));
                Assert.That(CreateResourceSignature(turnInPlace, settings), Is.EqualTo(initialSignature));
                Assert.That(CreateResourceSignature(backtrack, settings), Is.EqualTo(initialSignature));
                Assert.That(CreateResourceSignature(teleport, settings), Is.EqualTo(initialSignature));
                Assert.That(turnInPlace.ViewPriority.Forward.Z, Is.GreaterThan(0.9f));
                Assert.That(backtrack.TotalPhysicalProbeCount, Is.LessThanOrEqualTo(settings.DdgiMaxActiveProbes));
            });
        }

        [Test]
        public void SchedulerValidation_PrioritizesNewNearCellsAndStillReservesSafetyWork()
        {
            Scene scene = CreateSmallScene();
            GlobalIlluminationSettings settings = CreateValidationSettings(cascadeCount: 2);
            settings.DdgiMaxProbeUpdatesPerFrame = 8;
            settings.DdgiOutOfFrustumMinimumUpdateFraction = 0.25f;
            var controller = new CameraRelativeDdgiClipmapController();
            var camera = new FirstPersonCamera(Vector3.Zero);

            _ = Build(scene, camera, settings, controller, frame: 1, velocity: Vector3.Zero);
            camera.Position = new Vector3(1.25f, 0.0f, 0.0f);
            DdgiFrameLayout scrolled = Build(scene, camera, settings, controller, frame: 2, velocity: new Vector3(1.25f, 0.0f, 0.0f));
            GPUDdgiProbeUpdateRequest[] requests = BuildRequests(scrolled, settings, hardMaxRequestCount: 8, out DdgiProbeUpdateSchedulerResult result);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(8));
                Assert.That(requests.Take(6), Has.All.Matches<GPUDdgiProbeUpdateRequest>(request =>
                    request.Priority == DdgiProbeUpdateScheduler.PriorityNewCell &&
                    (request.Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0 &&
                    request.VolumeIndex == 0u));
                Assert.That(result.SafetyRequestCount, Is.GreaterThanOrEqualTo(1));
                Assert.That(requests.Take(result.RequestCount), Has.Some.Matches<GPUDdgiProbeUpdateRequest>(request =>
                    (request.Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag) != 0));
            });
        }

        [Test]
        public void SchedulerValidation_RoundRobinRefreshDoesNotStarveAnyProbe()
        {
            GlobalIlluminationSettings settings = CreateValidationSettings(cascadeCount: 1);
            settings.DdgiOutOfFrustumMinimumUpdateFraction = 0.0f;
            var volume = new GPUDdgiProbeVolume
            {
                OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                SizeAndProbeCountX = new Vector4(3.0f, 0.0f, 1.0f, 4.0f),
                ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                BiasAndProbeCountZ = new Vector4(0.05f, 0.2f, 16.0f, 2.0f),
                ClipmapGridMinAndKind = new Vector4(0.0f, 0.0f, 0.0f, (uint)DdgiProbeVolumeKind.Authored)
            };
            var requests = new GPUDdgiProbeUpdateRequest[2];
            var marks = new byte[8];
            var visited = new HashSet<uint>();
            int cursor = 0;

            for (int frame = 0; frame < 4; frame++)
            {
                DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                    new[] { volume },
                    layout: null,
                    dirtyBounds: null,
                    activeProbeCount: 8,
                    hardMaxRequestCount: 2,
                    updateCursor: cursor,
                    settings,
                    requests,
                    marks);
                cursor = result.NextUpdateCursor;
                for (int i = 0; i < result.RequestCount; i++)
                    visited.Add(requests[i].ProbeIndex);
            }

            Assert.That(visited.OrderBy(value => value), Is.EqualTo(Enumerable.Range(0, 8).Select(value => (uint)value)));
        }

        [Test]
        public void SceneChangeValidation_DirtyBoundsCoverIndoorOutdoorLightAndEmissiveChanges()
        {
            Scene scene = CreateSmallScene();
            GlobalIlluminationSettings settings = CreateValidationSettings(cascadeCount: 3);
            var controller = new CameraRelativeDdgiClipmapController();
            var camera = new FirstPersonCamera(Vector3.Zero);
            DdgiFrameLayout stable = Build(scene, camera, settings, controller, frame: 1, velocity: Vector3.Zero);
            stable = Build(scene, camera, settings, controller, frame: 2, velocity: Vector3.Zero);

            DdgiFrameLayout dirtied = stable.WithDirtyBounds(new[]
            {
                new BoundingBox(new Vector3(-1.0f, -0.5f, -2.0f), new Vector3(1.0f, 2.0f, 2.0f)),
                new BoundingBox(new Vector3(5.0f, 0.0f, -8.0f), new Vector3(8.0f, 4.0f, -4.0f)),
                new BoundingBox(new Vector3(-3.0f, 0.0f, 3.0f), new Vector3(-2.5f, 1.0f, 3.5f))
            });

            Assert.Multiple(() =>
            {
                Assert.That(stable.DirtyProbeRequests, Is.Empty);
                Assert.That(dirtied.DirtyBounds, Has.Count.EqualTo(3));
                Assert.That(dirtied.DirtyProbeRequests, Is.Not.Empty);
                Assert.That(dirtied.DirtyProbeRequests, Has.All.Matches<DdgiFrameLayoutDirtyProbeRequest>(request =>
                    request.Reason == DdgiClipmapDirtyReason.DirtyBounds));
                Assert.That(dirtied.DirtyProbeRequests.Select(request => request.CascadeIndex), Does.Contain(0));
                Assert.That(dirtied.DirtyProbeRequests.Select(request => request.CascadeIndex), Does.Contain(1));
                Assert.That(dirtied.DirtyProbeRequests.Count, Is.LessThanOrEqualTo(dirtied.CameraRelativeCascadeCount * dirtied.DirtyBounds.Count));
            });
        }

        [Test]
        public void BudgetValidation_KeepsMemoryBoundedAndFarCascadesLowerFrequency()
        {
            Scene scene = CreateLargeScene(renderObjectCount: 2000);
            GlobalIlluminationSettings settings = CreateValidationSettings(cascadeCount: 4);
            settings.DdgiMaxActiveProbes = 288;
            settings.DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 288UL;
            var layout = Build(scene, new FirstPersonCamera(Vector3.Zero), settings, new CameraRelativeDdgiClipmapController(), frame: 1, velocity: Vector3.Zero);

            Assert.Multiple(() =>
            {
                Assert.That(layout.DefaultVolumeIncluded, Is.False);
                Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(4));
                Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(288));
                Assert.That(GlobalIlluminationProbeVolumeData.EstimateTextureBytes(layout.TotalPhysicalProbeCount), Is.LessThanOrEqualTo(settings.DdgiAtlasMemoryBudgetBytes));
                Assert.That(layout.Volumes[^4].RaysPerProbe, Is.GreaterThan(layout.Volumes[^3].RaysPerProbe));
                Assert.That(layout.Volumes[^3].RaysPerProbe, Is.GreaterThanOrEqualTo(layout.Volumes[^2].RaysPerProbe));
                Assert.That(layout.Volumes[^2].RaysPerProbe, Is.GreaterThanOrEqualTo(layout.Volumes[^1].RaysPerProbe));
                Assert.That(layout.Volumes[^4].ProbeSpacing.X, Is.LessThan(layout.Volumes[^1].ProbeSpacing.X));
                Assert.That(layout.Volumes[^1].MaxProbeUpdatesPerFrame, Is.LessThanOrEqualTo(settings.DdgiMaxProbeUpdatesPerFrame));
            });
        }

        [Test]
        public void PresetValidation_ChangesResourceSignatureOnlyForPersistentPolicyChanges()
        {
            Scene scene = CreateSmallScene();
            var camera = new FirstPersonCamera(Vector3.Zero);
            GlobalIlluminationSettings high = CreateValidationSettings(cascadeCount: 3);
            GlobalIlluminationSettings low = CreateValidationSettings(cascadeCount: 2);
            low.DdgiClipmapProbeCountX = 4;
            low.DdgiClipmapProbeCountZ = 4;
            low.DdgiCascade0RaysPerProbe = 48;

            ulong highSignature = CreateResourceSignature(
                Build(scene, camera, high, new CameraRelativeDdgiClipmapController(), frame: 1, velocity: Vector3.Zero),
                high);
            ulong lowSignature = CreateResourceSignature(
                Build(scene, camera, low, new CameraRelativeDdgiClipmapController(), frame: 1, velocity: Vector3.Zero),
                low);

            Assert.That(lowSignature, Is.Not.EqualTo(highSignature));
        }

        private static DdgiFrameLayout Build(
            Scene scene,
            FirstPersonCamera camera,
            GlobalIlluminationSettings settings,
            CameraRelativeDdgiClipmapController controller,
            ulong frame,
            Vector3 velocity)
        {
            return DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                settings,
                controller,
                frame,
                cameraCut: false,
                velocity);
        }

        private static GPUDdgiProbeUpdateRequest[] BuildRequests(
            DdgiFrameLayout layout,
            GlobalIlluminationSettings settings,
            int hardMaxRequestCount,
            out DdgiProbeUpdateSchedulerResult result)
        {
            var gpuVolumes = new GPUDdgiProbeVolume[DdgiProbeVolumeManager.AbsoluteMaxVolumeCount];
            int volumeCount = GlobalIlluminationProbeVolumeData.BuildVolumes(
                layout.Volumes,
                settings,
                gpuVolumes,
                out _,
                out int activeProbeCount,
                out _,
                out _,
                layout.VolumeMetadata);
            var requests = new GPUDdgiProbeUpdateRequest[hardMaxRequestCount];
            var marks = new byte[Math.Max(activeProbeCount, 1)];
            result = DdgiProbeUpdateScheduler.BuildRequests(
                gpuVolumes.AsSpan(0, volumeCount),
                layout,
                layout.DirtyBounds,
                activeProbeCount,
                hardMaxRequestCount,
                updateCursor: 0,
                settings,
                requests,
                marks);
            return requests;
        }

        private static ulong CreateResourceSignature(DdgiFrameLayout layout, GlobalIlluminationSettings settings)
        {
            var gpuVolumes = new GPUDdgiProbeVolume[DdgiProbeVolumeManager.AbsoluteMaxVolumeCount];
            int volumeCount = GlobalIlluminationProbeVolumeData.BuildVolumes(
                layout.Volumes,
                settings,
                gpuVolumes,
                out int totalProbeCount,
                out int activeProbeCount,
                out int raysPerProbe,
                out int maxProbeUpdatesPerFrame,
                layout.VolumeMetadata);

            return DdgiProbeVolumeManager.CreateResourceSignature(
                gpuVolumes.AsSpan(0, volumeCount),
                totalProbeCount,
                activeProbeCount,
                raysPerProbe,
                maxProbeUpdatesPerFrame);
        }

        private static Scene CreateSmallScene()
        {
            var scene = new Scene();
            scene.Add(new RenderObject { Position = new Vector3(-2.0f, 0.0f, -3.0f) });
            scene.Add(new RenderObject { Position = new Vector3(3.0f, 1.0f, -5.0f) });
            scene.Add(new RenderObject { Position = new Vector3(1.0f, 0.0f, 4.0f) });
            return scene;
        }

        private static Scene CreateLargeScene(int renderObjectCount)
        {
            var scene = new Scene();
            for (int i = 0; i < renderObjectCount; i++)
            {
                int x = i % 50;
                int z = i / 50;
                scene.Add(new RenderObject { Position = new Vector3(x * 4.0f, 0.0f, z * -4.0f) });
            }

            return scene;
        }

        private static GlobalIlluminationSettings CreateValidationSettings(int cascadeCount)
        {
            return new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiCameraRelativeEnabled = true,
                DdgiClipmapCascadeCount = cascadeCount,
                DdgiClipmapProbeCountX = 6,
                DdgiClipmapProbeCountY = 2,
                DdgiClipmapProbeCountZ = 6,
                DdgiClipmapBaseSpacing = 1.0f,
                DdgiClipmapSpacingScale = 2.0f,
                DdgiClipmapSafetyMarginCells = 1,
                DdgiTeleportResetDistance = 64.0f,
                DdgiMaxActiveProbes = 512,
                DdgiMaxProbeUpdatesPerFrame = 16,
                DdgiOutOfFrustumMinimumUpdateFraction = 0.25f,
                DdgiCascade0RaysPerProbe = 96,
                DdgiCascade1RaysPerProbe = 64,
                DdgiCascade2RaysPerProbe = 48,
                DdgiCascade3RaysPerProbe = 32,
                DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 512UL
            };
        }
    }
}
