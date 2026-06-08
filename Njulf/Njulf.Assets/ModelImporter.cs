using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Core.Math;
using Silk.NET.Assimp;

namespace Njulf.Assets
{
    public class ModelImporter : IDisposable
    {
        private readonly Assimp _assimp;
        private bool _disposed;

        public ModelImporter()
        {
            _assimp = Assimp.GetApi();
        }

        public ModelMesh Import(string path, ImporterOptions options = null)
        {
            options ??= ImporterOptions.Default;

            if (!File.Exists(path))
                throw new FileNotFoundException("Model file not found", path);

            var postProcess = PostProcessSteps.Triangulate;
            if (options.GenerateNormals)
                postProcess |= PostProcessSteps.GenerateNormals;
            if (options.GenerateTangents)
                postProcess |= PostProcessSteps.GenerateTangents;
            if (options.JoinIdenticalVertices)
                postProcess |= PostProcessSteps.JoinIdenticalVertices;
            if (options.FlipUVs)
                postProcess |= PostProcessSteps.FlipUVs;
            if (options.SortByPrimitiveType)
                postProcess |= PostProcessSteps.SortByPrimitiveType;

            unsafe
            {
                var scene = _assimp.ImportFile(path, (uint)postProcess);
                if (scene == null)
                {
                    string error = SilkMarshal.PtrToStringAuto(_assimp.GetErrorString()) ?? "Unknown error";
                    throw new Exception($"Failed to import model: {error}");
                }

                try
                {
                    if ((scene->MFlags & SceneFlags.Incomplete) != 0)
                        throw new Exception("Scene import incomplete");

                    if (scene->MRootNode == null)
                        throw new Exception("No root node in scene");

                    var mesh = ProcessScene(scene, path, options);
                    return mesh;
                }
                finally
                {
                    _assimp.ReleaseImport(scene);
                }
            }
        }

        private unsafe ModelMesh ProcessScene(Scene* scene, string path, ImporterOptions options)
        {
            var mesh = new ModelMesh
            {
                Name = Path.GetFileNameWithoutExtension(path)
            };

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector3>();
            var bitangents = new List<Vector3>();
            var texCoords = new List<Vector2>();
            var indices = new List<uint>();

            int vertexOffset = 0;

            for (uint m = 0; m < scene->MNumMeshes; m++)
            {
                var aiMesh = scene->PMeshes[m];

                if (aiMesh->MPrimitiveTypes != PrimitiveType.Triangle)
                    throw new Exception("Mesh is not triangulated. Enable Triangulate post-process step.");

                if (aiMesh->MNumVertices == 0 || aiMesh->MNumFaces == 0)
                    continue;

                for (uint f = 0; f < aiMesh->MNumFaces; f++)
                {
                    var face = aiMesh->PFaces[f];
                    if (face->MNumIndices != 3)
                        throw new Exception("Face is not a triangle. Enable Triangulate post-process step.");
                }

                for (uint v = 0; v < aiMesh->MNumVertices; v++)
                {
                    var pos = aiMesh->MVertices[(int)v];
                    vertices.Add(new Vector3(pos.X * options.GlobalScale, pos.Y * options.GlobalScale, pos.Z * options.GlobalScale));

                    var normal = aiMesh->MNormals != null ? aiMesh->MNormals[(int)v] : default;
                    normals.Add(new Vector3(normal.X, normal.Y, normal.Z));

                    var tangent = aiMesh->MTangents != null ? aiMesh->MTangents[(int)v] : default;
                    tangents.Add(new Vector3(tangent.X, tangent.Y, tangent.Z));

                    var bitangent = aiMesh->MBitangents != null ? aiMesh->MBitangents[(int)v] : default;
                    bitangents.Add(new Vector3(bitangent.X, bitangent.Y, bitangent.Z));

                    Vector3 tc = default;
                    if (aiMesh->MTextureCoords[0] != null && aiMesh->MTextureCoords[0][(int)v] != null)
                    {
                        var tcSrc = aiMesh->MTextureCoords[0][(int)v];
                        tc = new Vector3(tcSrc.X, options.FlipUVs ? 1f - tcSrc.Y : tcSrc.Y, tcSrc.Z);
                    }
                    texCoords.Add(new Vector2(tc.X, tc.Y));
                }

                int baseVertex = vertexOffset;
                for (uint f = 0; f < aiMesh->MNumFaces; f++)
                {
                    var face = aiMesh->PFaces[f];
                    if (options.FlipWindingOrder)
                    {
                        indices.Add((uint)(baseVertex + face->MIndices[2]));
                        indices.Add((uint)(baseVertex + face->MIndices[1]));
                        indices.Add((uint)(baseVertex + face->MIndices[0]));
                    }
                    else
                    {
                        indices.Add((uint)(baseVertex + face->MIndices[0]));
                        indices.Add((uint)(baseVertex + face->MIndices[1]));
                        indices.Add((uint)(baseVertex + face->MIndices[2]));
                    }
                }

                vertexOffset += (int)aiMesh->MNumVertices;
            }

            mesh.Vertices = vertices.ToArray();
            mesh.Normals = normals.ToArray();
            mesh.Tangents = tangents.ToArray();
            mesh.Bitangents = bitangents.ToArray();
            mesh.TexCoords = texCoords.ToArray();
            mesh.Indices = indices.ToArray();

            ComputeBoundingVolume(vertices, out var bbox, out var bsphere);
            mesh.BoundingBox = bbox;
            mesh.BoundingSphere = bsphere;

            return mesh;
        }

        private static void ComputeBoundingVolume(List<Vector3> vertices, out BoundingBox bbox, out BoundingSphere bsphere)
        {
            if (vertices.Count == 0)
            {
                bbox = new BoundingBox();
                bsphere = new BoundingSphere();
                return;
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

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

        public void Dispose()
        {
            _disposed = true;
        }
    }

    public class ModelMesh
    {
        public string Name { get; set; }
        public Vector3[] Vertices { get; set; }
        public Vector3[] Normals { get; set; }
        public Vector3[] Tangents { get; set; }
        public Vector3[] Bitangents { get; set; }
        public Vector2[] TexCoords { get; set; }
        public uint[] Indices { get; set; }
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }

        public ModelMesh()
        {
            Vertices = Array.Empty<Vector3>();
            Normals = Array.Empty<Vector3>();
            Tangents = Array.Empty<Vector3>();
            Bitangents = Array.Empty<Vector3>();
            TexCoords = Array.Empty<Vector2>();
            Indices = Array.Empty<uint>();
        }
    }
}
