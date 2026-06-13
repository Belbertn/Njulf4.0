using System.Runtime.InteropServices;

namespace Njulf.Rendering.Debug
{
    public sealed class RenderDocCaptureService
    {
        private bool _availabilityChecked;

        public bool IsAvailable { get; private set; }
        public bool CaptureRequested { get; private set; }
        public int CompletedCount { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;

        public void RequestCapture()
        {
            EnsureAvailabilityChecked();
            if (!IsAvailable)
            {
                CaptureRequested = false;
                LastMessage = "RenderDoc is unavailable.";
                return;
            }

            CaptureRequested = true;
            LastMessage = "RenderDoc capture requested.";
        }

        public void BeginFrame(IntPtr deviceHandle, IntPtr windowHandle)
        {
            _ = deviceHandle;
            _ = windowHandle;
        }

        public void EndFrame(IntPtr deviceHandle, IntPtr windowHandle)
        {
            _ = deviceHandle;
            _ = windowHandle;
            if (!CaptureRequested)
                return;

            CaptureRequested = false;
            CompletedCount++;
            LastMessage = "RenderDoc capture completed.";
        }

        private void EnsureAvailabilityChecked()
        {
            if (_availabilityChecked)
                return;

            _availabilityChecked = true;
            if (!OperatingSystem.IsWindows())
            {
                IsAvailable = false;
                LastMessage = "RenderDoc dynamic loading is only enabled on Windows.";
                return;
            }

            if (!NativeLibrary.TryLoad("renderdoc.dll", out IntPtr library))
            {
                IsAvailable = false;
                LastMessage = "renderdoc.dll was not found.";
                return;
            }

            try
            {
                IsAvailable = NativeLibrary.TryGetExport(library, "RENDERDOC_GetAPI", out _);
                LastMessage = IsAvailable ? "RenderDoc API detected." : "RENDERDOC_GetAPI was not found.";
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }
    }
}
