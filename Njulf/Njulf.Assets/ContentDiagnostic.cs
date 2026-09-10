using System.Text.Json;

namespace Njulf.Assets;

internal static class ContentDiagnostic
{
    internal static string Format(string path, Exception error)
    {
        string location = error is JsonException { LineNumber: long line } json
            ? $"({line + 1},{(json.BytePositionInLine ?? 0) + 1})" : "";
        return $"{Path.GetFullPath(path)}{location}: error NJCONTENT: {error.Message}";
    }
}
