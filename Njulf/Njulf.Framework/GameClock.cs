namespace Njulf.Core;

internal sealed class GameClock
{
    private TimeSpan _origin;
    private TimeSpan? _lastUpdate, _lastDraw;
    public void Start(TimeSpan now) { _origin = now; _lastUpdate = _lastDraw = null; }
    public GameTime Update(TimeSpan now) => Read(now, ref _lastUpdate);
    public GameTime Draw(TimeSpan now) => Read(now, ref _lastDraw);
    private GameTime Read(TimeSpan now, ref TimeSpan? previous)
    {
        var result = new GameTime(now - _origin, previous.HasValue ? now - previous.Value : TimeSpan.Zero);
        previous = now;
        return result;
    }
}
