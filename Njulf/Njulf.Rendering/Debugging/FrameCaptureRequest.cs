namespace Njulf.Rendering.Debug
{
    public enum ScreenshotColorSpace
    {
        FinalLdrSrgb,
        HdrLinear
    }

    public sealed record ScreenshotRequest(string OutputPath, ScreenshotColorSpace ColorSpace)
    {
        public static ScreenshotRequest CreateDefault(ScreenshotColorSpace colorSpace = ScreenshotColorSpace.FinalLdrSrgb)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Screenshots");
            string fileName = $"Njulf_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.png";
            return new ScreenshotRequest(Path.Combine(directory, fileName), colorSpace);
        }
    }

    public sealed class ScreenshotCaptureService
    {
        private readonly Queue<ScreenshotRequest> _requests = new();

        public int PendingCount => _requests.Count;
        public int CompletedCount { get; private set; }
        public string LastScreenshotPath { get; private set; } = string.Empty;
        public string LastScreenshotError { get; private set; } = string.Empty;

        public void Request(string? outputPath = null, ScreenshotColorSpace colorSpace = ScreenshotColorSpace.FinalLdrSrgb)
        {
            string path = string.IsNullOrWhiteSpace(outputPath)
                ? ScreenshotRequest.CreateDefault(colorSpace).OutputPath
                : outputPath;

            _requests.Enqueue(new ScreenshotRequest(path, colorSpace));
            LastScreenshotPath = path;
            LastScreenshotError = string.Empty;
        }

        public bool TryDequeue(out ScreenshotRequest request)
        {
            if (_requests.Count == 0)
            {
                request = ScreenshotRequest.CreateDefault();
                return false;
            }

            request = _requests.Dequeue();
            return true;
        }

        public void MarkCompleted(string outputPath)
        {
            CompletedCount++;
            LastScreenshotPath = outputPath;
            LastScreenshotError = string.Empty;
        }

        public void MarkFailed(string outputPath, string error)
        {
            LastScreenshotPath = outputPath;
            LastScreenshotError = error ?? string.Empty;
        }
    }
}
