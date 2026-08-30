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
        private readonly object _gate = new();
        private int _completedCount;
        private string _lastScreenshotPath = string.Empty;
        private string _lastScreenshotError = string.Empty;
        private ScreenshotCaptureAnalysis _lastCaptureAnalysis;

        public int PendingCount
        {
            get
            {
                lock (_gate)
                    return _requests.Count;
            }
        }

        public int CompletedCount
        {
            get
            {
                lock (_gate)
                    return _completedCount;
            }
        }

        public string LastScreenshotPath
        {
            get
            {
                lock (_gate)
                    return _lastScreenshotPath;
            }
        }

        public string LastScreenshotError
        {
            get
            {
                lock (_gate)
                    return _lastScreenshotError;
            }
        }

        public ScreenshotCaptureAnalysis LastCaptureAnalysis
        {
            get
            {
                lock (_gate)
                    return _lastCaptureAnalysis;
            }
        }

        public void Request(string? outputPath = null, ScreenshotColorSpace colorSpace = ScreenshotColorSpace.FinalLdrSrgb)
        {
            string path = string.IsNullOrWhiteSpace(outputPath)
                ? ScreenshotRequest.CreateDefault(colorSpace).OutputPath
                : outputPath;

            lock (_gate)
            {
                _requests.Enqueue(new ScreenshotRequest(path, colorSpace));
                _lastScreenshotPath = path;
                _lastScreenshotError = string.Empty;
            }
        }

        public bool TryDequeue(out ScreenshotRequest request)
        {
            lock (_gate)
            {
                if (_requests.Count == 0)
                {
                    request = ScreenshotRequest.CreateDefault();
                    return false;
                }

                request = _requests.Dequeue();
                return true;
            }
        }

        public void MarkCompleted(
            string outputPath,
            ScreenshotContentAnalysis contentAnalysis = default)
        {
            lock (_gate)
            {
                _completedCount++;
                _lastScreenshotPath = outputPath;
                _lastScreenshotError = string.Empty;
                _lastCaptureAnalysis = new ScreenshotCaptureAnalysis(
                    outputPath,
                    contentAnalysis);
            }
        }

        public void MarkFailed(string outputPath, string error)
        {
            lock (_gate)
            {
                _lastScreenshotPath = outputPath;
                _lastScreenshotError = error ?? string.Empty;
            }
        }

        /// <summary>
        /// Fails requests that have not yet been assigned to a terminal frame.
        /// This is used during device loss and renderer disposal so callers do
        /// not wait forever for a request that can no longer be submitted.
        /// </summary>
        public void FailPendingRequests(string error)
        {
            lock (_gate)
            {
                while (_requests.Count > 0)
                {
                    ScreenshotRequest request = _requests.Dequeue();
                    _lastScreenshotPath = request.OutputPath;
                    _lastScreenshotError = error ?? string.Empty;
                }
            }
        }
    }
}
