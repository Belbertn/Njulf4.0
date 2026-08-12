using System.Runtime.InteropServices;

namespace Njulf.Rendering.Debug
{
    public sealed class RenderDocCaptureService
    {
        private const int RenderDocApiVersion_1_6_0 = 10600;
        private const int TriggerCaptureFunctionIndex = 15;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetApiDelegate(int version, out IntPtr apiPointers);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void TriggerCaptureDelegate();

        private bool _availabilityChecked;
        // Keep the module reference for the lifetime of the renderer. Function
        // pointers returned by RENDERDOC_GetAPI are owned by this module.
        private IntPtr _libraryHandle;
        private TriggerCaptureDelegate? _triggerCapture;

        public bool IsAvailable { get; private set; }
        public bool CaptureRequested { get; private set; }
        public int CompletedCount { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;

        public void RequestCapture()
        {
            EnsureAvailabilityChecked();
            if (!IsAvailable || _triggerCapture == null)
            {
                CaptureRequested = false;
                LastMessage = "RenderDoc is unavailable.";
                return;
            }

            try
            {
                // RenderDoc captures the next presented frame. The request is
                // made before VulkanRenderer.BeginFrame by both the input path
                // and the deterministic Sponza capture harness.
                _triggerCapture();
                CaptureRequested = true;
                LastMessage = "RenderDoc capture queued for the next frame.";
            }
            catch (Exception exception)
            {
                CaptureRequested = false;
                LastMessage = $"RenderDoc capture request failed: {exception.Message}";
            }
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

            if (!NativeLibrary.TryLoad("renderdoc.dll", out _libraryHandle))
            {
                IsAvailable = false;
                LastMessage = "renderdoc.dll was not found.";
                return;
            }

            if (!NativeLibrary.TryGetExport(
                    _libraryHandle,
                    "RENDERDOC_GetAPI",
                    out IntPtr getApiPointer))
            {
                IsAvailable = false;
                LastMessage = "RENDERDOC_GetAPI was not found.";
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;
                return;
            }

            GetApiDelegate getApi =
                Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiPointer);
            if (getApi(RenderDocApiVersion_1_6_0, out IntPtr apiPointers) != 1 ||
                apiPointers == IntPtr.Zero)
            {
                IsAvailable = false;
                LastMessage = "RenderDoc API 1.6 is unavailable.";
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;
                return;
            }

            IntPtr triggerCapturePointer = Marshal.ReadIntPtr(
                apiPointers,
                checked(TriggerCaptureFunctionIndex * IntPtr.Size));
            if (triggerCapturePointer == IntPtr.Zero)
            {
                IsAvailable = false;
                LastMessage = "RenderDoc TriggerCapture is unavailable.";
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;
                return;
            }

            _triggerCapture = Marshal.GetDelegateForFunctionPointer<TriggerCaptureDelegate>(
                triggerCapturePointer);
            IsAvailable = true;
            LastMessage = "RenderDoc API 1.6 detected.";
        }
    }
}
