using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    internal static class SimpleDdgiSceneBounds
    {
        public static BoundingBox Estimate(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            bool hasPoint = false;
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (renderObject == null || !renderObject.Enabled || !renderObject.Visible)
                    continue;

                if (renderObject.LocalMeshBounds is BoundingBox localMeshBounds)
                {
                    BoundingBox worldMeshBounds = SceneDataBuilder.TransformBoundingBox(
                        localMeshBounds,
                        renderObject.WorldMatrix);
                    min = Vector3.Min(min, worldMeshBounds.Min);
                    max = Vector3.Max(max, worldMeshBounds.Max);
                }
                else
                {
                    min = Vector3.Min(min, renderObject.Position);
                    max = Vector3.Max(max, renderObject.Position);
                }

                hasPoint = true;
            }

            if (!hasPoint)
                return new BoundingBox(new Vector3(-12.0f, -2.0f, -12.0f), new Vector3(12.0f, 10.0f, 12.0f));

            Vector3 size = max - min;
            if (size.X < 4.0f)
            {
                min.X -= 12.0f;
                max.X += 12.0f;
            }

            if (size.Y < 4.0f)
            {
                min.Y -= 2.0f;
                max.Y += 10.0f;
            }

            if (size.Z < 4.0f)
            {
                min.Z -= 12.0f;
                max.Z += 12.0f;
            }

            return new BoundingBox(min, max);
        }
    }
}
