using Njulf.Core.Math;

namespace Njulf.Core.Interfaces
{
    public interface ICamera
    {
        Vector3 Position { get; set; }
        Matrix4x4 ViewMatrix { get; }
        Matrix4x4 ProjectionMatrix { get; }
        Matrix4x4 ViewProjectionMatrix { get; }
        Vector3 Forward { get; }
        Vector3 Right { get; }
        Vector3 Up { get; }
        float NearPlane { get; set; }
        float FarPlane { get; set; }
        float FieldOfView { get; set; }
        float AspectRatio { get; set; }
        
        void Update();
        void LookAt(Vector3 target, Vector3 up);
    }
}
