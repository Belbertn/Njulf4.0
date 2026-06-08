namespace Njulf.Assets
{
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
            JoinIdenticalVertices = true
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
