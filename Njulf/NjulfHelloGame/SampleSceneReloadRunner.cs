using System;

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
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _reload();
    }
}
