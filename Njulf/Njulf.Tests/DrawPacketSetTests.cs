using System.Collections.Generic;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DrawPacketSetTests
    {
        [Test]
        public void Empty_UsesUnpublishedRevisionAndStableEmptySignatures()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DrawPacketSet.Empty.Revision, Is.Zero);
                Assert.That(DrawPacketSet.Empty.DirectionalShadowCommands.IsEmpty, Is.True);
                Assert.That(
                    DrawPacketSet.Empty.DirectionalShadowSignature,
                    Is.EqualTo(DrawPacketSet.Empty.LocalDynamicShadowSignature));
            });
        }

        [Test]
        public void Create_SnapshotsCommandsAndCachesSignatures()
        {
            var directional = new List<GPUMeshletDrawCommand>
            {
                new() { MeshletIndex = 1, InstanceId = 2, MaterialIndex = 3 }
            };

            DrawPacketSet packets = CreatePackets(revision: 7, directional);
            ulong signature = packets.DirectionalShadowSignature;

            directional[0] = new GPUMeshletDrawCommand
            {
                MeshletIndex = 10,
                InstanceId = 20,
                MaterialIndex = 30
            };
            directional.Add(default);
            GPUMeshletDrawCommand frozenCommand = packets.DirectionalShadowCommands.Span[0];

            Assert.Multiple(() =>
            {
                Assert.That(packets.Revision, Is.EqualTo(7));
                Assert.That(packets.DirectionalShadowCommands.Length, Is.EqualTo(1));
                Assert.That(frozenCommand.MeshletIndex, Is.EqualTo(1));
                Assert.That(packets.DirectionalShadowSignature, Is.EqualTo(signature));
            });
        }

        [Test]
        public void Create_NewRevisionWithSamePackets_PreservesSignatures()
        {
            var directional = new List<GPUMeshletDrawCommand>
            {
                new() { MeshletIndex = 4, InstanceId = 5, MaterialIndex = 6 }
            };

            DrawPacketSet first = CreatePackets(revision: 1, directional);
            DrawPacketSet second = CreatePackets(revision: 2, directional);

            Assert.Multiple(() =>
            {
                Assert.That(second.Revision, Is.EqualTo(2));
                Assert.That(second.DirectionalShadowSignature, Is.EqualTo(first.DirectionalShadowSignature));
            });
        }

        [Test]
        public void Create_ChangedPacket_ChangesSignature()
        {
            var directional = new List<GPUMeshletDrawCommand>
            {
                new() { MeshletIndex = 4, InstanceId = 5, MaterialIndex = 6 }
            };
            DrawPacketSet first = CreatePackets(revision: 1, directional);

            directional[0] = new GPUMeshletDrawCommand
            {
                MeshletIndex = 4,
                InstanceId = 5,
                MaterialIndex = 7
            };
            DrawPacketSet second = CreatePackets(revision: 2, directional);

            Assert.That(second.DirectionalShadowSignature, Is.Not.EqualTo(first.DirectionalShadowSignature));
        }

        private static DrawPacketSet CreatePackets(
            ulong revision,
            IReadOnlyList<GPUMeshletDrawCommand> directional)
        {
            return DrawPacketSet.Create(
                revision,
                directional,
                [],
                [],
                [],
                [],
                []);
        }
    }
}
