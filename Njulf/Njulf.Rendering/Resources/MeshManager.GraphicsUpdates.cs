using System.Runtime.InteropServices;
using Njulf.Graphics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using CoreVector3 = Njulf.Core.Math.Vector3;
using Vector3 = System.Numerics.Vector3;

namespace Njulf.Rendering.Resources;

public sealed unsafe partial class MeshManager
{
    private readonly Dictionary<int, MeshChangeSource> _dynamicChanges = new();
    internal MeshChangeSource? GetChangeSource(MeshHandle handle) => _dynamicChanges.GetValueOrDefault(handle.Index);

    internal MeshHandle RegisterStandardMesh(GPUVertex[] vertices, uint[] indices, bool dynamic)
    {
        var registration = new MeshRegistrationData(vertices, ExtractPositions(vertices), indices, true)
            { Dynamic = dynamic };
        var handle = RegisterMeshes([registration])[0];
        if (dynamic) _dynamicChanges.Add(handle.Index, new());
        return handle;
    }

    internal void RecordVertexUpdate(VulkanGraphicsDevice device, CommandBuffer cmd, MeshHandle handle,
        GPUVertex[] vertices)
    {
        lock (_lock)
        {
            var info = GetMeshInfo(handle);
            var positions = BuildVertexPositionStream(vertices);
            var normals = BuildVertexNormalTangentStream(vertices);
            var uvs = BuildVertexUvColorStream(vertices);
            var low = new Vector3(vertices[0].Position.X, vertices[0].Position.Y, vertices[0].Position.Z);
            var high = low;
            foreach (var vertex in vertices)
            {
                var p = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                low = Vector3.Min(low, p);
                high = Vector3.Max(high, p);
            }

            info.BoundingBoxMin = low;
            info.BoundingBoxMax = high;
            info.ContentRevision = info.ContentRevision == uint.MaxValue ? 1 : info.ContentRevision + 1;
            var center = (low + high) * .5f;
            float radius = Vector3.Distance(low, center);
            var meshlets = new Njulf.Core.Geometry.Meshlet[info.MeshletCount];
            for (int i = 0; i < meshlets.Length; i++)
            {
                var value = _meshlets[(int)info.MeshletOffset + i];
                // A common conservative bound remains valid for any fixed-topology deformation.
                value.BoundingSphereCenter = new CoreVector3(center.X, center.Y, center.Z);
                value.BoundingSphereRadius = radius;
                value.NormalConeAxis = default;
                value.NormalConeCutoff = -1;
                meshlets[i] = value;
            }

            var geometry = _transportGeometry[handle.Index];
            var updatedGeometry =
                CreateTransportGeometry(positions, uvs, geometry.Indices.ToArray(), false, null, default);
            var packed = PackGpuMeshlets(meshlets);
            GPUMeshInfo[] metadata = [CreateGpuMeshInfo(info)];
            Upload(positions, _vertexPositionBuffer, info.VertexOffset * VertexPositionStride);
            Upload(normals, _vertexNormalTangentBuffer, info.VertexOffset * VertexNormalTangentStride);
            Upload(uvs, _vertexUvColorBuffer, info.VertexOffset * VertexUvColorStride);
            Upload(packed, _meshletBuffer, info.MeshletOffset * MeshletStride);
            Upload(metadata, _meshMetadataBuffer, info.MeshMetadataOffset * MeshMetadataStride);
            _meshes[handle.Index] = info;
            _transportGeometry[handle.Index] = updatedGeometry;
            AppendCpuMeshlets(info, meshlets);
            _dynamicChanges[handle.Index].Publish();

            void Upload<T>(T[] values, BufferHandle destination, ulong offset) where T : unmanaged
            {
                var bytes = MemoryMarshal.AsBytes(values.AsSpan());
                var staging = device.StageBytes(bytes);
                device.Releases.EnqueueRetirement(() => _bufferManager.DestroyBuffer(staging), true);
                device.RecordBufferCopy(cmd, staging, destination, offset, (ulong)bytes.Length);
            }
        }
    }
}