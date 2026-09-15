namespace Njulf.Tests;

/// <summary>Preserves existing source-contract checks across the shared optical include.</summary>
internal static class ForwardShaderSource
{
    internal static string Read()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Njulf.Shaders")))
            root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("Njulf.Shaders");
        string directory = Path.Combine(root.FullName, "Njulf.Shaders");
        string source = File.ReadAllText(Path.Combine(directory, "forward.frag"));
        return source.Replace("#include \"forward_surface_shading.glsl\"",
            File.ReadAllText(Path.Combine(directory, "forward_surface_shading.glsl")),
            StringComparison.Ordinal).ReplaceLineEndings("\n");
    }
}
