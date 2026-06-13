using Njulf.Core.Math;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Debug
{
    public sealed record ObjectDebugSnapshot(
        int ObjectIndex,
        string Name,
        MeshHandle Mesh,
        MaterialHandle Material,
        Matrix4x4 WorldMatrix,
        BoundingBox WorldBounds,
        bool Visible,
        bool CpuCulled);
}
