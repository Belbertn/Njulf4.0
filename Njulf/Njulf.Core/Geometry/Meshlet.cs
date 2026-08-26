using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Core.Geometry
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Meshlet
    {
        public Vector3 BoundingSphereCenter;
        public float BoundingSphereRadius;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint LocalVertexOffset;
        public uint LocalVertexCount;
        public uint LocalTriangleOffset;
        public uint LocalTriangleCount;
        // Conservative geometric-normal cone used for whole-meshlet
        // back-face rejection. A cutoff of 1 disables cone culling (the
        // representation used for degenerate or wider-than-a-hemisphere
        // clusters).
        public Vector3 NormalConeAxis;
        public float NormalConeCutoff;

        public Meshlet(
            Vector3 boundingSphereCenter,
            float boundingSphereRadius,
            uint vertexOffset,
            uint vertexCount,
            uint indexOffset,
            uint indexCount,
            uint localVertexOffset,
            uint localVertexCount,
            uint localTriangleOffset,
            uint localTriangleCount,
            Vector3 normalConeAxis = default,
            float normalConeCutoff = 1.0f)
        {
            BoundingSphereCenter = boundingSphereCenter;
            BoundingSphereRadius = boundingSphereRadius;
            VertexOffset = vertexOffset;
            VertexCount = vertexCount;
            IndexOffset = indexOffset;
            IndexCount = indexCount;
            LocalVertexOffset = localVertexOffset;
            LocalVertexCount = localVertexCount;
            LocalTriangleOffset = localTriangleOffset;
            LocalTriangleCount = localTriangleCount;
            NormalConeAxis = normalConeAxis;
            NormalConeCutoff = normalConeCutoff;
        }
    }
}
