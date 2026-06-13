using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Njulf.Rendering.Diagnostics;

public sealed class RendererHealthReportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Write(string path, object report)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Health report path must not be empty.", nameof(path));

        string fullPath = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, JsonOptions));
    }
}
