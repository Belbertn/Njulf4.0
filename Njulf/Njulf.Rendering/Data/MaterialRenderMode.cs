namespace Njulf.Rendering.Data
{
    public enum MaterialRenderMode
    {
        Opaque = 0,
        Mask = 1,
        Blend = 2
    }

    public static class MaterialRenderModeExtensions
    {
        public const float OpaqueCode = 0f;
        public const float MaskCode = 1f;
        public const float BlendCode = 2f;

        public static MaterialRenderMode FromGpuMaterial(GPUMaterialData material)
        {
            float mode = material.NormalScaleBias.Y;
            if (mode >= 1.5f)
                return MaterialRenderMode.Blend;
            if (mode >= 0.5f)
                return MaterialRenderMode.Mask;

            return MaterialRenderMode.Opaque;
        }

        public static float ToGpuAlphaModeCode(this MaterialRenderMode mode)
        {
            return mode switch
            {
                MaterialRenderMode.Mask => MaskCode,
                MaterialRenderMode.Blend => BlendCode,
                _ => OpaqueCode
            };
        }
    }
}
