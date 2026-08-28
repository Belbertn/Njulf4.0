namespace NjulfHelloGame;

internal sealed class SampleSceneReloadRunner
{
    private readonly Action _reload;

    public SampleSceneReloadRunner(Action reload)
    {
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
    }

    public void Reload()
    {
        _reload();
    }
}
