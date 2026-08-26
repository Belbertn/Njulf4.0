using System;
using System.Collections.Generic;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets
{
    public class MeshletBuilder
    {
        public const int DefaultMaxVerticesPerMeshlet = 64;
        public const int DefaultMaxTrianglesPerMeshlet = 126;
        private readonly int _maxVerticesPerMeshlet;
        private readonly int _maxTrianglesPerMeshlet;

        public MeshletBuilder(
            int maxVerticesPerMeshlet = DefaultMaxVerticesPerMeshlet,
            int maxTrianglesPerMeshlet = DefaultMaxTrianglesPerMeshlet)
        {
            if (maxVerticesPerMeshlet is < 3 or > DefaultMaxVerticesPerMeshlet)
                throw new ArgumentOutOfRangeException(nameof(maxVerticesPerMeshlet), $"Meshlet vertex limit must be between 3 and {DefaultMaxVerticesPerMeshlet}.");
            if (maxTrianglesPerMeshlet is < 1 or > DefaultMaxTrianglesPerMeshlet)
                throw new ArgumentOutOfRangeException(nameof(maxTrianglesPerMeshlet), $"Meshlet triangle limit must be between 1 and {DefaultMaxTrianglesPerMeshlet}.");
            _maxVerticesPerMeshlet = maxVerticesPerMeshlet;
            _maxTrianglesPerMeshlet = maxTrianglesPerMeshlet;
        }

        public MeshletMesh BuildMeshlets(
            Vector3[] vertices,
            uint[] indices,
            Vector3[]? normals = null,
            Vector3[]? tangents = null,
            Vector3[]? bitangents = null,
            Vector2[]? texCoords = null,
            string? name = null)
        {
            if (vertices == null || vertices.Length == 0)
                throw new ArgumentException("Vertices cannot be null or empty");
            if (indices == null || indices.Length == 0)
                throw new ArgumentException("Indices cannot be null or empty");
            if (indices.Length % 3 != 0)
                throw new ArgumentException("Indices must be a multiple of 3 (triangles only)");

            var mesh = new MeshletMesh
            {
                Name = name ?? "Unnamed",
                Vertices = vertices,
                Indices = indices
            };

            ComputeBoundingVolume(vertices, out var bbox, out var bsphere);
            mesh.BoundingBox = bbox;
            mesh.BoundingSphere = bsphere;

            var meshlets = new List<Meshlet>();
            var meshletVertices = new List<uint>();
            var meshletTriangles = new List<uint>();

            BuildMeshletsInternal(
                vertices,
                indices,
                meshlets,
                meshletVertices,
                meshletTriangles);

            ComputeMeshletNormalCones(
                vertices,
                meshlets,
                meshletVertices,
                meshletTriangles);

            mesh.Meshlets = meshlets.ToArray();
            mesh.MeshletVertices = meshletVertices.ToArray();
            mesh.MeshletTriangles = meshletTriangles.ToArray();

            return mesh;
        }

        private void BuildMeshletsInternal(
            Vector3[] vertices,
            uint[] indices,
            List<Meshlet> meshlets,
            List<uint> meshletVertices,
            List<uint> meshletTriangles)
        {
            // meshoptimizer grows clusters spatially even when modelling seams
            // split otherwise adjacent faces into disjoint index components.
            // The previous topology-only walk stopped at every such seam and
            // reduced Bistro to only a few triangles per meshlet.
            int optimizedTriangleLimit = _maxTrianglesPerMeshlet & ~3;
            if (optimizedTriangleLimit >= 4)
            {
                BuildMeshletsOptimized(
                    vertices,
                    indices,
                    optimizedTriangleLimit,
                    meshlets,
                    meshletVertices,
                    meshletTriangles);
                return;
            }

            BuildMeshletsAdjacencyFallback(
                vertices,
                indices,
                meshlets,
                meshletVertices,
                meshletTriangles);
        }

        private void BuildMeshletsOptimized(
            Vector3[] vertices,
            uint[] indices,
            int optimizedTriangleLimit,
            List<Meshlet> meshlets,
            List<uint> meshletVertices,
            List<uint> meshletTriangles)
        {
            MeshOptimizerMeshletBuildResult result = MeshOptimizerCodec.BuildMeshlets(
                indices,
                vertices,
                _maxVerticesPerMeshlet,
                optimizedTriangleLimit);

            foreach (MeshOptimizerMeshletDescriptor descriptor in result.Meshlets)
            {
                int sourceVertexOffset = checked((int)descriptor.VertexOffset);
                int sourceVertexCount = checked((int)descriptor.VertexCount);
                int sourceTriangleOffset = checked((int)descriptor.TriangleOffset);
                int sourceTriangleIndexCount = checked((int)descriptor.TriangleCount * 3);

                uint outputVertexOffset = checked((uint)meshletVertices.Count);
                uint outputTriangleOffset = checked((uint)(meshletTriangles.Count / 3));
                ReadOnlySpan<uint> globalVertexIndices = result.Vertices.AsSpan(
                    sourceVertexOffset,
                    sourceVertexCount);
                ComputeMeshletBounds(
                    vertices,
                    globalVertexIndices,
                    out Vector3 center,
                    out float radius);

                meshlets.Add(new Meshlet(
                    center,
                    radius,
                    outputVertexOffset,
                    descriptor.VertexCount,
                    outputTriangleOffset,
                    descriptor.TriangleCount,
                    outputVertexOffset,
                    descriptor.VertexCount,
                    outputTriangleOffset,
                    descriptor.TriangleCount));

                for (int i = 0; i < globalVertexIndices.Length; i++)
                    meshletVertices.Add(globalVertexIndices[i]);
                for (int i = 0; i < sourceTriangleIndexCount; i++)
                    meshletTriangles.Add(result.Triangles[sourceTriangleOffset + i]);
            }
        }

        private void BuildMeshletsAdjacencyFallback(
            Vector3[] vertices,
            uint[] indices,
            List<Meshlet> meshlets,
            List<uint> meshletVertices,
            List<uint> meshletTriangles)
        {
            int totalTriangles = indices.Length / 3;
            var vertexToTriangles = new List<List<int>>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
                vertexToTriangles.Add(new List<int>());

            for (int triangleIndex = 0; triangleIndex < totalTriangles; triangleIndex++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    uint vertexIndex = indices[triangleIndex * 3 + corner];
                    if (vertexIndex >= vertices.Length)
                        throw new ArgumentOutOfRangeException(nameof(indices), $"Index {vertexIndex} is outside the vertex buffer.");

                    vertexToTriangles[(int)vertexIndex].Add(triangleIndex);
                }
            }

            var usedTriangles = new bool[totalTriangles];
            var candidateMarks = new bool[totalTriangles];

            for (int seedTriangle = 0; seedTriangle < totalTriangles; seedTriangle++)
            {
                if (usedTriangles[seedTriangle])
                    continue;

                var meshletTriangleIds = new List<int> { seedTriangle };
                var meshletVertexSet = new HashSet<int>();
                var meshletLocalVertices = new Dictionary<int, int>();

                AddTriangleVertices(seedTriangle, indices, meshletVertexSet, meshletLocalVertices);

                bool expanded = true;
                while (expanded &&
                       meshletTriangleIds.Count < _maxTrianglesPerMeshlet &&
                       meshletLocalVertices.Count < _maxVerticesPerMeshlet)
                {
                    expanded = false;
                    var candidates = new List<int>();

                    foreach (int vertexIndex in meshletVertexSet)
                    {
                        foreach (int candidateTriangle in vertexToTriangles[vertexIndex])
                        {
                            if (usedTriangles[candidateTriangle] ||
                                meshletTriangleIds.Contains(candidateTriangle) ||
                                candidateMarks[candidateTriangle])
                            {
                                continue;
                            }

                            candidateMarks[candidateTriangle] = true;
                            candidates.Add(candidateTriangle);
                        }
                    }

                    candidates.Sort((a, b) => CompareTriangleFit(a, b, meshletVertexSet, indices));

                    foreach (int candidateTriangle in candidates)
                    {
                        candidateMarks[candidateTriangle] = false;

                        if (meshletTriangleIds.Count >= _maxTrianglesPerMeshlet)
                            break;

                        int newVertexCount = CountNewTriangleVertices(candidateTriangle, indices, meshletLocalVertices);

                        if (meshletLocalVertices.Count + newVertexCount > _maxVerticesPerMeshlet)
                            continue;

                        meshletTriangleIds.Add(candidateTriangle);
                        AddTriangleVertices(candidateTriangle, indices, meshletVertexSet, meshletLocalVertices);
                        expanded = true;
                    }

                    for (int i = 0; i < candidates.Count; i++)
                        candidateMarks[candidates[i]] = false;
                }

                uint meshletVertexOffset = (uint)meshletVertices.Count;
                uint meshletTriangleOffset = (uint)meshletTriangles.Count / 3;

                ComputeMeshletBounds(vertices, meshletVertexSet, out var center, out float radius);

                meshlets.Add(new Meshlet(
                    center,
                    radius,
                    meshletVertexOffset,
                    (uint)meshletVertexSet.Count,
                    meshletTriangleOffset,
                    (uint)meshletTriangleIds.Count,
                    meshletVertexOffset,
                    (uint)meshletVertexSet.Count,
                    meshletTriangleOffset,
                    (uint)meshletTriangleIds.Count));

                var localToGlobalVertices = new uint[meshletLocalVertices.Count];
                foreach (var pair in meshletLocalVertices)
                    localToGlobalVertices[pair.Value] = (uint)pair.Key;

                for (int i = 0; i < localToGlobalVertices.Length; i++)
                    meshletVertices.Add(localToGlobalVertices[i]);

                foreach (int triangleIndex in meshletTriangleIds)
                {
                    usedTriangles[triangleIndex] = true;
                    for (int corner = 0; corner < 3; corner++)
                        meshletTriangles.Add((uint)meshletLocalVertices[(int)indices[triangleIndex * 3 + corner]]);
                }
            }
        }

        private static void AddTriangleVertices(
            int triangleIndex,
            uint[] indices,
            HashSet<int> meshletVertexSet,
            Dictionary<int, int> meshletLocalVertices)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex = (int)indices[triangleIndex * 3 + corner];
                if (meshletLocalVertices.ContainsKey(vertexIndex))
                    continue;

                meshletVertexSet.Add(vertexIndex);
                meshletLocalVertices[vertexIndex] = meshletLocalVertices.Count;
            }
        }

        private static int CountNewTriangleVertices(
            int triangleIndex,
            uint[] indices,
            Dictionary<int, int> meshletLocalVertices)
        {
            int count = 0;
            int firstNewVertex = -1;
            int secondNewVertex = -1;
            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex = (int)indices[triangleIndex * 3 + corner];
                if (meshletLocalVertices.ContainsKey(vertexIndex) ||
                    vertexIndex == firstNewVertex ||
                    vertexIndex == secondNewVertex)
                {
                    continue;
                }

                if (count == 0)
                    firstNewVertex = vertexIndex;
                else
                    secondNewVertex = vertexIndex;

                count++;
            }

            return count;
        }

        private static int CompareTriangleFit(int a, int b, HashSet<int> seedVertices, uint[] indices)
        {
            int aShared = 0, bShared = 0;
            for (int j = 0; j < 3; j++)
            {
                if (seedVertices.Contains((int)indices[a * 3 + j]))
                    aShared++;

                if (seedVertices.Contains((int)indices[b * 3 + j]))
                    bShared++;
            }
            return bShared.CompareTo(aShared);
        }

        private static void ComputeMeshletBounds(
            Vector3[] vertices,
            HashSet<int> meshletVertexSet,
            out Vector3 center,
            out float radius)
        {
            center = Vector3.Zero;
            foreach (int vertexIndex in meshletVertexSet)
                center += vertices[vertexIndex];

            center /= meshletVertexSet.Count;

            radius = 0f;
            foreach (int vertexIndex in meshletVertexSet)
                radius = System.Math.Max(radius, Vector3.Distance(center, vertices[vertexIndex]));
        }

        private static void ComputeMeshletBounds(
            Vector3[] vertices,
            ReadOnlySpan<uint> meshletVertexIndices,
            out Vector3 center,
            out float radius)
        {
            center = Vector3.Zero;
            for (int i = 0; i < meshletVertexIndices.Length; i++)
                center += vertices[checked((int)meshletVertexIndices[i])];

            center /= meshletVertexIndices.Length;

            radius = 0f;
            for (int i = 0; i < meshletVertexIndices.Length; i++)
            {
                radius = System.Math.Max(
                    radius,
                    Vector3.Distance(
                        center,
                        vertices[checked((int)meshletVertexIndices[i])]));
            }
        }

        private static void ComputeMeshletNormalCones(
            Vector3[] vertices,
            List<Meshlet> meshlets,
            List<uint> meshletVertices,
            List<uint> meshletTriangles)
        {
            const float normalLengthEpsilon = 1e-20f;
            const float axisLengthEpsilon = 1e-12f;

            for (int meshletIndex = 0; meshletIndex < meshlets.Count; meshletIndex++)
            {
                Meshlet meshlet = meshlets[meshletIndex];
                int triangleBase = checked((int)meshlet.LocalTriangleOffset * 3);
                int triangleCount = checked((int)meshlet.LocalTriangleCount);
                int vertexBase = checked((int)meshlet.LocalVertexOffset);
                Vector3 axisSum = Vector3.Zero;
                int validTriangleCount = 0;

                for (int triangle = 0; triangle < triangleCount; triangle++)
                {
                    int scalar = checked(triangleBase + triangle * 3);
                    uint local0 = meshletTriangles[scalar + 0];
                    uint local1 = meshletTriangles[scalar + 1];
                    uint local2 = meshletTriangles[scalar + 2];
                    Vector3 p0 = vertices[checked((int)meshletVertices[checked(vertexBase + (int)local0)])];
                    Vector3 p1 = vertices[checked((int)meshletVertices[checked(vertexBase + (int)local1)])];
                    Vector3 p2 = vertices[checked((int)meshletVertices[checked(vertexBase + (int)local2)])];
                    Vector3 geometricNormal = Vector3.Cross(p1 - p0, p2 - p0);
                    float lengthSquared = geometricNormal.LengthSquared();
                    if (!float.IsFinite(lengthSquared) || lengthSquared <= normalLengthEpsilon)
                        continue;

                    axisSum += geometricNormal / MathF.Sqrt(lengthSquared);
                    validTriangleCount++;
                }

                float axisLengthSquared = axisSum.LengthSquared();
                if (validTriangleCount == 0 ||
                    !float.IsFinite(axisLengthSquared) ||
                    axisLengthSquared <= axisLengthEpsilon)
                {
                    meshlet.NormalConeAxis = Vector3.Zero;
                    meshlet.NormalConeCutoff = 1.0f;
                    meshlets[meshletIndex] = meshlet;
                    continue;
                }

                Vector3 axis = axisSum / MathF.Sqrt(axisLengthSquared);
                float minimumDot = 1.0f;
                for (int triangle = 0; triangle < triangleCount; triangle++)
                {
                    int scalar = checked(triangleBase + triangle * 3);
                    uint local0 = meshletTriangles[scalar + 0];
                    uint local1 = meshletTriangles[scalar + 1];
                    uint local2 = meshletTriangles[scalar + 2];
                    Vector3 p0 = vertices[checked((int)meshletVertices[checked(vertexBase + (int)local0)])];
                    Vector3 p1 = vertices[checked((int)meshletVertices[checked(vertexBase + (int)local1)])];
                    Vector3 p2 = vertices[checked((int)meshletVertices[checked(vertexBase + (int)local2)])];
                    Vector3 geometricNormal = Vector3.Cross(p1 - p0, p2 - p0);
                    float lengthSquared = geometricNormal.LengthSquared();
                    if (!float.IsFinite(lengthSquared) || lengthSquared <= normalLengthEpsilon)
                        continue;

                    Vector3 normal = geometricNormal / MathF.Sqrt(lengthSquared);
                    minimumDot = MathF.Min(minimumDot, Vector3.Dot(axis, normal));
                }

                // Cones spanning 90 degrees or more cannot conservatively
                // reject a view hemisphere. Mark them disabled instead of
                // introducing false negatives on folded or non-manifold data.
                if (!float.IsFinite(minimumDot) || minimumDot <= 0.0f)
                {
                    meshlet.NormalConeAxis = Vector3.Zero;
                    meshlet.NormalConeCutoff = 1.0f;
                }
                else
                {
                    minimumDot = Math.Clamp(minimumDot, 0.0f, 1.0f);
                    meshlet.NormalConeAxis = axis;
                    meshlet.NormalConeCutoff = MathF.Sqrt(
                        MathF.Max(0.0f, 1.0f - minimumDot * minimumDot));
                }

                meshlets[meshletIndex] = meshlet;
            }
        }

        private static void ComputeBoundingVolume(Vector3[] vertices, out BoundingBox bbox, out BoundingSphere bsphere)
        {
            if (vertices == null || vertices.Length == 0)
            {
                bbox = new BoundingBox();
                bsphere = new BoundingSphere();
                return;
            }

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = System.Math.Min(min.X, v.X);
                min.Y = System.Math.Min(min.Y, v.Y);
                min.Z = System.Math.Min(min.Z, v.Z);
                max.X = System.Math.Max(max.X, v.X);
                max.Y = System.Math.Max(max.Y, v.Y);
                max.Z = System.Math.Max(max.Z, v.Z);
            }

            bbox = new BoundingBox(min, max);
            bsphere = BoundingSphere.FromBox(bbox);
        }

        public static MeshletMesh BuildSimpleMeshlets(Vector3[] vertices, uint[] indices, string? name = null)
        {
            var builder = new MeshletBuilder();
            return builder.BuildMeshlets(vertices, indices, null, null, null, null, name);
        }
    }
}
