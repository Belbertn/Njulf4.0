namespace Njulf.Core;

// Fixed phases and a shared ownership set, not a configurable scheduler.
internal sealed class GameModules(HashSet<IGameModule> registrations)
{
    private readonly List<IGameModule> _modules = [];
    internal bool RequiresFixedTimeStep => _modules.Any(m => m.RequiresFixedTimeStep);
    internal bool IsEmpty => _modules.Count == 0;

    internal T Register<T>(T module, bool active, bool paused, bool fixedStep) where T : IGameModule
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!Enum.IsDefined(module.Phase)) throw new ArgumentOutOfRangeException(nameof(module));
        if (registrations.Contains(module)) throw new InvalidOperationException("The module is already registered.");
        if (active && module.RequiresFixedTimeStep && !fixedStep)
            throw new InvalidOperationException("Simulation physics requires IsFixedTimeStep.");
        module.SetActive(active, paused);
        registrations.Add(module);
        _modules.Add(module);
        return module;
    }

    internal void Validate(bool fixedStep)
    {
        if (!fixedStep && RequiresFixedTimeStep)
            throw new InvalidOperationException("Simulation physics requires IsFixedTimeStep.");
    }

    internal void Activate(bool paused)
    {
        foreach (var module in _modules) module.SetActive(true, paused);
    }

    internal void FixedUpdate(GameTime time, Func<bool> canContinue)
    {
        foreach (var module in _modules)
        {
            if (!canContinue()) break;
            if (module.Phase == GameModulePhase.Physics) module.FixedUpdate(time);
        }
    }

    internal void Update(GameModulePhase phase, GameModuleFrame frame, Func<bool> canContinue)
    {
        foreach (var module in _modules)
        {
            if (!canContinue()) break;
            if (module.Phase == phase) module.Update(frame);
        }
    }

    internal void Dispose()
    {
        List<Exception> failures = [];
        foreach (var module in _modules)
            try { module.SetActive(false, true); } catch (Exception e) { failures.Add(e); }
        // Local voices before worlds; a device adapter, if present, is released last.
        foreach (var phase in new[] { GameModulePhase.AudioSpatial, GameModulePhase.Physics, GameModulePhase.AudioMaintenance })
            for (int i = _modules.Count - 1; i >= 0; i--)
            {
                var module = _modules[i];
                if (module.Phase != phase) continue;
                try { module.Dispose(); } catch (Exception e) { failures.Add(e); }
                registrations.Remove(module);
            }
        _modules.Clear();
        if (failures.Count != 0) throw new AggregateException("Module cleanup failed.", failures);
    }
}
