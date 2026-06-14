using System.Collections.Generic;
using System.Linq;
using Njulf.Assets;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class MeshletBuilderTests
    {
        [Test]
        public void BuildMeshlets_HandlesTrianglesBeyondOldChunkBoundary()
        {
            const int vertexCount = 1030;
            var vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new Vector3(i, i % 7, 0f);

            uint[] indices =
            {
                0, 1, 2,
                1024, 1025, 1026,
                1026, 1027, 1028
            };

            MeshletMesh mesh = new MeshletBuilder().BuildMeshlets(vertices, indices);

            AssertMeshletOutputIsValid(mesh, indices.Length / 3);
            Assert.That(mesh.MeshletVertices, Does.Contain(1024u));
        }

        [Test]
        public void BuildMeshlets_HandlesSparseIndexRangesWithoutLocalGlobalMixing()
        {
            var vertices = Enumerable.Range(0, 1500)
                .Select(i => new Vector3(i * 0.01f, i % 11, i % 3))
                .ToArray();
            uint[] indices =
            {
                100, 700, 1300,
                1300, 700, 1499,
                1024, 1200, 1400
            };

            MeshletMesh mesh = new MeshletBuilder().BuildMeshlets(vertices, indices);

            AssertMeshletOutputIsValid(mesh, indices.Length / 3);
            Assert.That(mesh.MeshletVertices, Does.Contain(1499u));
        }

        [Test]
        public void BuildMeshlets_RejectsIndicesOutsideVertexBuffer()
        {
            var vertices = new[]
            {
                Vector3.Zero,
                Vector3.UnitX,
                Vector3.UnitY
            };

            Assert.That(
                () => new MeshletBuilder().BuildMeshlets(vertices, new uint[] { 0, 1, 99 }),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        private static void AssertMeshletOutputIsValid(MeshletMesh mesh, int expectedTriangleCount)
        {
            Assert.Multiple(() =>
            {
                Assert.That(mesh.Meshlets, Is.Not.Empty);
                Assert.That(mesh.MeshletTriangles, Has.Length.EqualTo(expectedTriangleCount * 3));
            });

            int emittedTriangles = 0;
            foreach (Meshlet meshlet in mesh.Meshlets)
            {
                emittedTriangles += checked((int)meshlet.IndexCount);
                Assert.That(meshlet.VertexCount, Is.LessThanOrEqualTo(64));
                Assert.That(meshlet.IndexCount, Is.LessThanOrEqualTo(126));

                var localVertices = new HashSet<uint>();
                for (uint i = 0; i < meshlet.VertexCount; i++)
                    localVertices.Add(i);

                int firstIndex = checked((int)meshlet.IndexOffset * 3);
                int indexCount = checked((int)meshlet.IndexCount * 3);
                for (int i = firstIndex; i < firstIndex + indexCount; i++)
                {
                    Assert.That(
                        localVertices.Contains(mesh.MeshletTriangles[i]),
                        Is.True,
                        "Meshlet triangle indices must reference meshlet-local vertices.");
                }
            }

            Assert.That(emittedTriangles, Is.EqualTo(expectedTriangleCount));
        }
    }
}
