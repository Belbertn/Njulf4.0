using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SpriteBatchTests
{
    private sealed class Atlas : Texture
    {
        public override int Width => 32;
        public override int Height => 32;
        public override TextureColorSpace ColorSpace => TextureColorSpace.Srgb;
        public override bool IsDisposed { get; protected set; }
        public override void Dispose() => IsDisposed = true;
    }
    private sealed record Snapshot(SpriteVertex[] Vertices, ushort[] Indices, SpriteCommand[] Commands);
    private sealed class Sink : ISpriteBatchSink
    {
        public readonly List<Snapshot> Packets = new();
        public int Retained, Released;
        public Vector2 Begin() => new(200, 100);
        public void ValidateFrame() { }
        public SpriteTextureLease Retain(Texture texture, SpriteSampler sampler)
        { Retained++; return new(Retained, () => Released++); }
        public void Submit(SpriteDrawData data) => Packets.Add(new(data.Vertices.ToArray(), data.Indices.ToArray(), data.Commands.ToArray()));
    }

    [Test]
    public void GeometryUsesTopLeftPixelsOriginRotationAndFlippedSource()
    {
        var sink = new Sink(); using var batch = new SpriteBatch(sink); using var atlas = new Atlas();
        batch.Begin();
        batch.Draw(atlas, new(20, 30), Color.White, new Vector2(8, 4), new(0, 0, 16, 8), MathF.PI / 2, new(4, 2), SpriteFlip.Horizontal);
        batch.End();
        var data = sink.Packets.Single();
        Assert.That(data.Vertices[0].Position.X, Is.EqualTo(22).Within(.0001));
        Assert.That(data.Vertices[0].Position.Y, Is.EqualTo(26).Within(.0001));
        Assert.That(data.Vertices[2].Position.X, Is.EqualTo(18).Within(.0001));
        Assert.That(data.Vertices[2].Position.Y, Is.EqualTo(34).Within(.0001));
        Assert.That(data.Vertices[0].Uv.X, Is.EqualTo(.5f));
        Assert.That(data.Vertices[2].Uv, Is.EqualTo(new System.Numerics.Vector2(0, .25f)));
    }

    [Test]
    public void BatchingPreservesOrderAndNestedClipBoundariesAndRetainsOnce()
    {
        var sink = new Sink(); using var batch = new SpriteBatch(sink); using var atlas = new Atlas();
        batch.Begin();
        batch.Draw(atlas, default, Color.Red); batch.Draw(atlas, new(2, 2), Color.Blue);
        batch.PushClip(new(10, 10, 30, 30)); batch.PushClip(new(20, 0, 40, 25));
        batch.Draw(atlas, default, Color.White); batch.PopClip(); batch.PopClip();
        batch.FillRectangle(new(0, 0, 10, 10), Color.Green);
        Assert.That(sink.Released, Is.Zero);
        batch.End();
        var commands = sink.Packets.Single().Commands;
        Assert.That(commands.Select(c => c.ElementCount), Is.EqualTo(new uint[] { 12, 6, 6 }));
        Assert.That(commands[1].Clip, Is.EqualTo(new SpriteRectangle(20, 10, 20, 15)));
        Assert.That(commands[2].TextureIndex, Is.Zero);
        Assert.That(sink.Retained, Is.EqualTo(1)); Assert.That(sink.Released, Is.EqualTo(1));
    }

    [Test]
    public void LargeBatchSplitsBaseVertexWithoutWrappingIndices()
    {
        var sink = new Sink(); using var batch = new SpriteBatch(sink);
        batch.Begin();
        for (int i = 0; i < 17_000; i++) batch.FillRectangle(new(i % 100, 0, 1, 1), Color.White);
        batch.End();
        var data = sink.Packets.Single();
        Assert.That(data.Commands.Length, Is.EqualTo(2));
        Assert.That(data.Indices.Length, Is.EqualTo(102_000));
        foreach (var command in data.Commands)
            for (uint i = command.IndexOffset; i < command.IndexOffset + command.ElementCount; i++)
                Assert.That(command.VertexOffset + data.Indices[i], Is.LessThan(data.Vertices.Length));
        Assert.That(data.Commands.Sum(c => (long)c.ElementCount), Is.EqualTo(102_000));
    }

    [Test]
    public void InvalidBatchStateAndDisposedTexturesFailWithoutSubmitting()
    {
        var sink = new Sink(); using var batch = new SpriteBatch(sink); var atlas = new Atlas();
        Assert.Throws<InvalidOperationException>(() => batch.End());
        batch.Begin(); Assert.Throws<InvalidOperationException>(() => batch.Begin());
        batch.PushClip(new(0, 0, 1, 1)); Assert.Throws<InvalidOperationException>(() => batch.End()); batch.PopClip();
        Assert.Throws<InvalidOperationException>(() => batch.PopClip());
        atlas.Dispose(); Assert.Throws<ObjectDisposedException>(() => batch.Draw(atlas, default, Color.White));
        batch.End(); Assert.That(sink.Packets, Is.Empty);
    }

    [Test]
    public void FontMeasurementAndDrawingShareKerningNewlinesAndScalarFallback()
    {
        using var font = new SpriteFont(new Atlas(), 12, '?',
            [new('A', new(0, 0, 5, 8), new(1, 2), 6), new('V', new(5, 0, 5, 8), default, 6), new('?', new(10, 0, 4, 8), default, 5)],
            [new('A', 'V', -2)]);
        Assert.That(font.MeasureString("AV\r\n\U0001F600"), Is.EqualTo(new Vector2(10, 24)));
        Assert.That(font.MeasureString(""), Is.EqualTo(Vector2.Zero));
        var sink = new Sink(); using var batch = new SpriteBatch(sink);
        batch.Begin(); batch.DrawString(font, "AV\n\U0001F600", new(10, 20), Color.White); batch.End();
        var vertices = sink.Packets.Single().Vertices;
        Assert.That(vertices.Length, Is.EqualTo(12));
        Assert.That(vertices[0].Position, Is.EqualTo(new System.Numerics.Vector2(11, 22)));
        Assert.That(vertices[4].Position.X, Is.EqualTo(14));
        Assert.That(vertices[8].Position.Y, Is.EqualTo(32));
    }

    [Test]
    public void CompositionAppliesDpiToBothPositionsAndClipsAndPlacesEditorLast()
    {
        var game = new OverlayDrawData(default, new(100, 50), new(2, 2), [new(new(10, 12), default, 1)], [0], [new(1, 0, 0, new(10, 12, 20, 25), 4)]);
        var editor = new OverlayDrawData(new(5, 5), new(200, 100), System.Numerics.Vector2.One,
            [new(new(9, 11), default, 2)], [0], [new(1, 0, 0, new(5, 5, 15, 15), 7)]);
        var result = OverlayDrawData.Compose([game], editor, 200, 100);
        Assert.That(result.Vertices[0].Position, Is.EqualTo(new System.Numerics.Vector2(20, 24)));
        Assert.That(result.Commands[0].ClipRectangle, Is.EqualTo(new System.Numerics.Vector4(20, 24, 40, 50)));
        Assert.That(result.Commands[1].TextureIndex, Is.EqualTo(7));
        Assert.That(result.Commands[1].VertexOffset, Is.EqualTo(1));
        Assert.That(result.Vertices[1].Position, Is.EqualTo(new System.Numerics.Vector2(4, 6)));
    }

    [Test]
    public void ProjectionMatchesCameraRayAndRejectsBehindCamera()
    {
        var camera = new FirstPersonCamera(new(0, 0, 5)) { AspectRatio = 2 };
        Vector2 screen = new(70, 25), size = new(200, 100);
        Ray ray = camera.ScreenPointToRay(screen, size);
        Assert.That(ScreenProjection.TryProject(camera, ray.Position + ray.Direction * 5, new(0, 0, 200, 100), out var projected), Is.True);
        Assert.That(projected.X, Is.EqualTo(screen.X).Within(.001)); Assert.That(projected.Y, Is.EqualTo(screen.Y).Within(.001));
        Assert.That(ScreenProjection.TryProject(camera, camera.Position - camera.Forward, new(0, 0, 200, 100), out _), Is.False);
    }
}
