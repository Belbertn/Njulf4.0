namespace Njulf.Assets
{
    /// <summary>
    /// Declares a material texture layout used by an Assimp-imported source.
    /// Assimp texture slots alone do not specify how legacy packed maps encode
    /// physically based material channels, so non-standard layouts must be
    /// selected explicitly by the asset manifest or cooker command.
    /// </summary>
    public enum AssimpMaterialTextureConvention
    {
        Standard,

        /// <summary>
        /// The Assimp specular texture stores roughness in green and metallic
        /// in blue. Red is unused and must not be interpreted as ambient
        /// occlusion.
        /// </summary>
        SpecularGbIsRoughnessMetallic,

        /// <summary>
        /// Amazon Bistro's specular texture uses the same green/blue packed
        /// roughness/metallic layout, while its tangent-space normal maps use
        /// the DirectX (green-down) convention. The red packed channel stores
        /// occlusion amount rather than glTF occlusion visibility and is not
        /// bound directly.
        /// </summary>
        AmazonBistro
    }

    public class ImporterOptions
    {
        public bool FlipUVs { get; set; } = true;
        public bool GenerateNormals { get; set; } = true;
        public bool GenerateTangents { get; set; } = true;
        public bool Triangulate { get; set; } = true;
        public bool JoinIdenticalVertices { get; set; } = true;
        public bool SortByPrimitiveType { get; set; } = true;
        public bool CalculateBoundingBoxes { get; set; } = true;
        public float GlobalScale { get; set; } = 1.0f;
        public bool FlipWindingOrder { get; set; } = false;
        public string PreferredFormat { get; set; } = "gltf";
        public ModelImportBackend Backend { get; set; } = ModelImportBackend.Auto;
        public AssimpMaterialTextureConvention AssimpMaterialTextureConvention { get; set; } =
            AssimpMaterialTextureConvention.Standard;
        public bool ImportLights { get; set; } = true;
        public float DefaultImportedLightRange { get; set; } = 100f;
        public float MaximumImportedLightRange { get; set; } = 1000f;
        public float ImportedLightAttenuationCutoff { get; set; } = 1f / 256f;

        public ImporterOptions()
        {
        }

        public static ImporterOptions Default => new ImporterOptions();

        public static ImporterOptions ForGltf => new ImporterOptions
        {
            FlipUVs = true,
            GenerateNormals = true,
            GenerateTangents = true,
            Triangulate = true,
            JoinIdenticalVertices = true,
            // glTF defines positive-determinant triangle faces as CCW in asset space.
            // The renderer/backend is responsible for mapping that to its front-face state.
            FlipWindingOrder = false
        };

        public static ImporterOptions ForObj => new ImporterOptions
        {
            FlipUVs = false,
            GenerateNormals = true,
            GenerateTangents = false,
            Triangulate = true,
            JoinIdenticalVertices = true
        };

        public static ImporterOptions ForFbx => new ImporterOptions
        {
            FlipUVs = true,
            GenerateNormals = true,
            GenerateTangents = true,
            Triangulate = true,
            JoinIdenticalVertices = true,
            FlipWindingOrder = true
        };
    }
}
