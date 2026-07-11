using System;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Windowing;

namespace NjulfHelloGame;

internal static class SampleWindowCapture
{
    private const int BiRgb = 0;
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;
    private const uint DibRgbColors = 0;

    public static bool TryCaptureClientArea(IWindow window, string outputPath, out string error)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        error = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            error = "Window diagnostic screenshots are only implemented on Windows.";
            return false;
        }

        if (window is not INativeWindowSource nativeSource ||
            nativeSource.Native?.Win32 is not { } win32 ||
            win32.Item1 == IntPtr.Zero)
        {
            error = "Could not resolve the Win32 window handle.";
            return false;
        }

        IntPtr hwnd = win32.Item1;
        if (!GetClientRect(hwnd, out Rect clientRect))
        {
            error = $"GetClientRect failed: {Marshal.GetLastWin32Error()}";
            return false;
        }

        int width = clientRect.Right - clientRect.Left;
        int height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
        {
            error = $"Window client area is empty ({width}x{height}).";
            return false;
        }

        Point origin = new();
        if (!ClientToScreen(hwnd, ref origin))
        {
            error = $"ClientToScreen failed: {Marshal.GetLastWin32Error()}";
            return false;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            error = $"GetDC failed: {Marshal.GetLastWin32Error()}";
            return false;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                error = $"CreateCompatibleDC failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                error = $"CreateCompatibleBitmap failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            oldObject = SelectObject(memoryDc, bitmap);
            if (oldObject == IntPtr.Zero)
            {
                error = $"SelectObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, origin.X, origin.Y, Srccopy | Captureblt))
            {
                error = $"BitBlt failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            byte[] bgra = ReadBitmap(memoryDc, bitmap, width, height);
            WritePng(outputPath, bgra, width, height);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (oldObject != IntPtr.Zero)
                SelectObject(memoryDc, oldObject);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[] ReadBitmap(IntPtr memoryDc, IntPtr bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = checked((uint)(width * height * 4))
            }
        };

        byte[] pixels = new byte[checked(width * height * 4)];
        int rows = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors);
        if (rows == 0)
            throw new InvalidOperationException($"GetDIBits failed: {Marshal.GetLastWin32Error()}");

        return pixels;
    }

    private static void WritePng(string path, byte[] bgra, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);

        using FileStream file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(file, "IHDR", ihdr);

        using var uncompressed = new MemoryStream(checked((width * 4 + 1) * height));
        for (int y = 0; y < height; y++)
        {
            uncompressed.WriteByte(0);
            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int pixelOffset = rowOffset + x * 4;
                uncompressed.WriteByte(bgra[pixelOffset + 2]);
                uncompressed.WriteByte(bgra[pixelOffset + 1]);
                uncompressed.WriteByte(bgra[pixelOffset]);
                uncompressed.WriteByte(255);
            }
        }

        using var compressed = new MemoryStream();
        uncompressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            uncompressed.CopyTo(zlib);

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        typeBytes[0] = (byte)type[0];
        typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2];
        typeBytes[3] = (byte)type[3];
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffffu;
        foreach (byte value in type)
            crc = UpdateCrc(crc, value);
        foreach (byte value in data)
            crc = UpdateCrc(crc, value);
        return crc ^ 0xffffffffu;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr destinationDc,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        int rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr dc,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}
