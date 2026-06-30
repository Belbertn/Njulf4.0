using System.IO;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DdgiFrameSerialTests
    {
        [Test]
        public void ProbeAge_IncreasesMonotonicallyAcrossThreeHundredLogicalFrames()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 1, settings);
            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            DdgiClipmapCell cell = cascade.LogicalGridMinCell;
            cascade.MarkLogicalCellUpdated(cell, 1);

            for (ulong frameSerial = 2; frameSerial <= 300; frameSerial++)
                controller.Update(Vector3.Zero, frameSerial, settings);

            DdgiClipmapCellState state = cascade.GetCellState(cell);

            Assert.Multiple(() =>
            {
                Assert.That(state.Initialized, Is.True);
                Assert.That(state.LastUpdateFrame, Is.EqualTo(1UL));
                Assert.That(state.AgeFrames, Is.EqualTo(299UL));
                Assert.That(state.AgeFrames, Is.GreaterThan(1UL));
            });
        }

        [Test]
        public void NewScrolledCells_ResetAgeAndHistoryToCurrentFrameSerial()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 1, settings);
            controller.Update(new Vector3(1.1f, 0.0f, 0.0f), 10, settings);

            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            var newCell = new DdgiClipmapCell(2, -1, -2);
            DdgiClipmapCellState invalidated = cascade.GetCellState(newCell);
            cascade.MarkLogicalCellUpdated(newCell, 10);
            DdgiClipmapCellState refreshed = cascade.GetCellState(newCell);

            controller.Update(new Vector3(1.1f, 0.0f, 0.0f), 11, settings);
            DdgiClipmapCellState aged = cascade.GetCellState(newCell);

            Assert.Multiple(() =>
            {
                Assert.That(invalidated.Initialized, Is.False);
                Assert.That(invalidated.AgeFrames, Is.EqualTo(ulong.MaxValue));
                Assert.That(refreshed.Initialized, Is.True);
                Assert.That(refreshed.LastUpdateFrame, Is.EqualTo(10UL));
                Assert.That(refreshed.AgeFrames, Is.EqualTo(0UL));
                Assert.That(aged.LastUpdateFrame, Is.EqualTo(10UL));
                Assert.That(aged.AgeFrames, Is.EqualTo(1UL));
            });
        }

        [Test]
        public void MinimumProbeRefreshFrames_UsesFrameSerialInGpuScheduler()
        {
            string shared = ReadRepoText("Njulf.Shaders", "ddgi_schedule_shared.glsl");
            string score = ReadRepoText("Njulf.Shaders", "ddgi_schedule_score.comp");
            string update = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
            string manager = ReadRepoText("Njulf.Rendering", "Resources", "DdgiProbeVolumeManager.cs");

            Assert.Multiple(() =>
            {
                Assert.That(shared, Does.Contain("uint FrameSerial;"));
                Assert.That(shared, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FRAME_SERIAL"));
                Assert.That(score, Does.Contain("constants.FrameSerial - lastUpdateFrame >= constants.MinimumProbeRefreshFrames"));
                Assert.That(update, Does.Contain("WriteStorageWord(pc.ProbeStateBufferIndex, stateBase + 16u, pc.FrameSerial);"));
                Assert.That(manager, Does.Contain("FrameSerial = sceneData.DdgiFrameSerialLow32"));
                Assert.That(score, Does.Not.Contain("constants.FrameIndex - lastUpdateFrame"));
                Assert.That(update, Does.Not.Contain("stateBase + 16u, pc.CurrentFrameIndex"));
                Assert.That(manager, Does.Not.Contain("FrameSerial = sceneData.CurrentFrameIndex"));
            });
        }

        private static GlobalIlluminationSettings CreateSingleCascadeSettings()
        {
            return new GlobalIlluminationSettings
            {
                DdgiClipmapCascadeCount = 1,
                DdgiClipmapProbeCountX = 4,
                DdgiClipmapProbeCountY = 2,
                DdgiClipmapProbeCountZ = 4,
                DdgiClipmapBaseSpacing = 1.0f,
                DdgiClipmapSpacingScale = 2.0f,
                DdgiTeleportResetDistance = 1000.0f
            };
        }

        private static string ReadRepoText(params string[] parts)
        {
            DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Njulf.Rendering")))
                directory = directory.Parent;

            Assert.That(directory, Is.Not.Null, "Could not locate repository root.");
            string[] pathParts = new[] { directory!.FullName }.Concat(parts).ToArray();
            return File.ReadAllText(Path.Combine(pathParts));
        }
    }
}
