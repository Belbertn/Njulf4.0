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

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        DurableJsonFileWriter.Write(
            path,
            payload,
            "renderer health report");
    }
}
