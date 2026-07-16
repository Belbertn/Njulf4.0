using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Njulf.Rendering;
using Njulf.Rendering.Pipeline;

namespace Njulf.Editor;

/// <summary>
/// Owns the Dear ImGui context and exposes a deterministic frame/input boundary to the game host.
/// Vulkan submission intentionally remains outside this class so the editor assembly is optional.
/// </summary>
public sealed unsafe class ImGuiEditorOverlayHost : IEditorOverlayHost, IDisposable
{
    private readonly ImGuiContextPtr _context;
    private bool _enabled;
    private bool _disposed;

    public ImGuiEditorOverlayHost()
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
    }

    public bool WantCaptureMouse => !_disposed && _enabled && CurrentIo.WantCaptureMouse;
    public bool WantCaptureKeyboard => !_disposed && _enabled && CurrentIo.WantCaptureKeyboard;
    public ImDrawDataPtr DrawData { get; private set; }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public void BeginFrame(Vector2 displaySize, Vector2 framebufferScale, float deltaTime)
    {
        ThrowIfDisposed();
        if (displaySize.X <= 0f || displaySize.Y <= 0f)
            throw new ArgumentOutOfRangeException(nameof(displaySize));
        if (deltaTime <= 0f || !float.IsFinite(deltaTime))
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = displaySize;
        io.DisplayFramebufferScale = framebufferScale;
        io.DeltaTime = deltaTime;
        ImGui.NewFrame();
    }

    public ImDrawDataPtr EndFrame()
    {
        ThrowIfDisposed();
        ImGui.SetCurrentContext(_context);
        ImGui.Render();
        DrawData = ImGui.GetDrawData();
        return DrawData;
    }

    public void SubmitFrame(VulkanRenderer renderer)
    {
        ImDrawDataPtr drawData = EndFrame();
        ProcessTextures(drawData, renderer);
        renderer.QueueOverlayDrawData(Copy(drawData));
    }

    public void ClearRenderer(VulkanRenderer renderer) => renderer.QueueOverlayDrawData(null);

    public void AddMousePosition(Vector2 position) { CurrentIo.AddMousePosEvent(position.X, position.Y); }
    public void AddMouseButton(int button, bool down) { CurrentIo.AddMouseButtonEvent(button, down); }
    public void AddMouseWheel(Vector2 wheel) { CurrentIo.AddMouseWheelEvent(wheel.X, wheel.Y); }
    public void AddKey(ImGuiKey key, bool down) { CurrentIo.AddKeyEvent(key, down); }
    public void AddText(uint codePoint) { CurrentIo.AddInputCharacter(codePoint); }

    public void Dispose()
    {
        if (_disposed)
            return;
        _enabled = false;
        DrawData = default;
        ImGui.SetCurrentContext(_context);
        ImGui.DestroyContext(_context);
        _disposed = true;
    }

    private ImGuiIOPtr CurrentIo
    {
        get
        {
            ThrowIfDisposed();
            ImGui.SetCurrentContext(_context);
            return ImGui.GetIO();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ProcessTextures(ImDrawDataPtr data, VulkanRenderer renderer)
    {
        for (int i = 0; i < data.Textures.Size; i++)
        {
            ImTextureDataPtr texture = data.Textures[i];
            if (texture.IsNull || texture.Status is ImTextureStatus.Ok or ImTextureStatus.Destroyed) continue;
            if (texture.Status == ImTextureStatus.WantDestroy) { texture.SetStatus(ImTextureStatus.Destroyed); continue; }
            if (texture.Width <= 0 || texture.Height <= 0 || texture.Pixels == null) continue;
            int pixels = checked(texture.Width * texture.Height);
            byte[] rgba;
            if (texture.Format == ImTextureFormat.Rgba32) rgba = new ReadOnlySpan<byte>(texture.Pixels, checked(pixels * 4)).ToArray();
            else
            {
                rgba = new byte[checked(pixels * 4)];
                for (int p = 0; p < pixels; p++) { int t = p * 4; rgba[t] = rgba[t + 1] = rgba[t + 2] = 255; rgba[t + 3] = texture.Pixels[p]; }
            }
            int index = renderer.CreateOverlayTexture(rgba, (uint)texture.Width, (uint)texture.Height, $"ImGui Texture {texture.UniqueID}");
            texture.SetTexID(new ImTextureID((ulong)(uint)index)); texture.SetStatus(ImTextureStatus.Ok);
        }
    }

    private static OverlayDrawData? Copy(ImDrawDataPtr source)
    {
        if (source.IsNull || !source.Valid || source.TotalVtxCount <= 0 || source.TotalIdxCount <= 0) return null;
        var vertices = new OverlayVertex[source.TotalVtxCount]; var indices = new ushort[source.TotalIdxCount]; var commands = new List<OverlayDrawCommand>();
        int vb = 0, ib = 0;
        for (int l = 0; l < source.CmdListsCount; l++)
        {
            ImDrawListPtr list = source.CmdLists[l];
            for (int i = 0; i < list.VtxBuffer.Size; i++) { ImDrawVert v = list.VtxBuffer[i]; vertices[vb + i] = new OverlayVertex(v.Pos, v.Uv, v.Col); }
            for (int i = 0; i < list.IdxBuffer.Size; i++) indices[ib + i] = list.IdxBuffer[i];
            for (int i = 0; i < list.CmdBuffer.Size; i++)
            {
                ImDrawCmdPtr command = new(&list.CmdBuffer.Data[i]); if (command.UserCallback != null) continue;
                ulong texture = command.GetTexID().Handle;
                commands.Add(new OverlayDrawCommand(command.ElemCount, checked((uint)(ib + command.IdxOffset)), checked(vb + (int)command.VtxOffset), command.ClipRect, texture > int.MaxValue ? 0 : (int)texture));
            }
            vb += list.VtxBuffer.Size; ib += list.IdxBuffer.Size;
        }
        return new OverlayDrawData(source.DisplayPos, source.DisplaySize, source.FramebufferScale, vertices, indices, commands.ToArray());
    }
}
