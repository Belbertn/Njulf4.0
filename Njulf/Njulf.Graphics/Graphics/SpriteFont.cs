using Njulf.Core.Math;
using System.Text;

namespace Njulf.Graphics;

public readonly record struct SpriteGlyph(int CodePoint, SpriteRectangle Source, Vector2 Offset, float Advance);
public readonly record struct SpriteKerning(int First, int Second, float Amount);

/// <summary>A single bitmap atlas and immutable metrics. The font retains its own atlas reference.</summary>
public sealed class SpriteFont : IDisposable
{
    internal Texture Atlas { get; }
    private readonly Dictionary<int, SpriteGlyph> _glyphs;
    private readonly Dictionary<(int, int), float> _kerning;
    public float LineHeight { get; }
    public int FallbackCodePoint { get; }
    public bool IsDisposed { get; private set; }
    internal SpriteFont(Texture atlas, float lineHeight, int fallbackCodePoint, IEnumerable<SpriteGlyph> glyphs, IEnumerable<SpriteKerning>? kerning)
    {
        if (!float.IsFinite(lineHeight) || lineHeight <= 0) throw new ArgumentOutOfRangeException(nameof(lineHeight));
        Atlas = atlas;
        LineHeight = lineHeight;
        FallbackCodePoint = fallbackCodePoint;
        _glyphs = new(); _kerning = new();
        foreach (SpriteGlyph glyph in glyphs)
        {
            if (!Rune.IsValid(glyph.CodePoint) || !glyph.Source.IsValid || glyph.Source.X < 0 || glyph.Source.Y < 0 ||
                glyph.Source.Right > atlas.Width || glyph.Source.Bottom > atlas.Height || !float.IsFinite(glyph.Advance) || glyph.Advance < 0 ||
                !float.IsFinite(glyph.Offset.X) || !float.IsFinite(glyph.Offset.Y) || !_glyphs.TryAdd(glyph.CodePoint, glyph))
                throw new ArgumentException("Invalid or duplicate glyph.", nameof(glyphs));
        }
        if (!_glyphs.ContainsKey(fallbackCodePoint)) throw new ArgumentException("The fallback glyph must exist.", nameof(fallbackCodePoint));
        foreach (SpriteKerning pair in kerning ?? [])
            if (!float.IsFinite(pair.Amount) || !_glyphs.ContainsKey(pair.First) || !_glyphs.ContainsKey(pair.Second) || !_kerning.TryAdd((pair.First, pair.Second), pair.Amount))
                throw new ArgumentException("Invalid or duplicate kerning pair.", nameof(kerning));
    }
    internal SpriteGlyph GetGlyph(int value) => _glyphs.TryGetValue(value, out var glyph) ? glyph : _glyphs[FallbackCodePoint];
    internal float Kerning(int previous, int current) => _kerning.GetValueOrDefault((previous, current));
    internal void EnsureUsable() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    /// <summary>Returns advance width and line-box height, including empty lines; empty text measures zero.</summary>
    public Vector2 MeasureString(string text)
    {
        EnsureUsable(); ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return Vector2.Zero;
        float width = 0, x = 0, height = LineHeight;
        int previous = -1;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value == '\r') continue;
            if (rune.Value == '\n') { width = MathF.Max(width, x); x = 0; height += LineHeight; previous = -1; continue; }
            SpriteGlyph glyph = GetGlyph(rune.Value);
            x += Kerning(previous, glyph.CodePoint) + glyph.Advance;
            previous = glyph.CodePoint;
        }
        return new(MathF.Max(width, x), height);
    }
    public void Dispose() { if (IsDisposed) return; Atlas.Dispose(); IsDisposed = true; }
}

public abstract partial class GraphicsDevice
{
    public virtual SpriteBatch CreateSpriteBatch() => throw new NotSupportedException("Sprite drawing is unavailable on this device.");
    public SpriteFont CreateSpriteFont(Texture atlas, float lineHeight, int fallbackCodePoint,
        IEnumerable<SpriteGlyph> glyphs, IEnumerable<SpriteKerning>? kerning = null)
    {
        ArgumentNullException.ThrowIfNull(atlas); ArgumentNullException.ThrowIfNull(glyphs);
        Texture retained = RetainSpriteAtlas(atlas);
        try { return new(retained, lineHeight, fallbackCodePoint, glyphs, kerning); }
        catch { retained.Dispose(); throw; }
    }
    internal virtual Texture RetainSpriteAtlas(Texture atlas) => throw new NotSupportedException("Sprite fonts are unavailable on this device.");
}
