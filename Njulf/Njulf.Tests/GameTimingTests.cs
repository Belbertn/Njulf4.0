using Njulf.Core;
using Njulf.Rendering;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GameTimingTests
{
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);
    private static GameClock FixedClock(double scale = 1)
    {
        var clock = new GameClock();
        clock.Configure(TimeSpan.Zero, true, Ms(10), 3, scale, false);
        clock.Start(TimeSpan.Zero);
        return clock;
    }
    private static List<GameTime> Steps(GameClock clock, TimeSpan now)
    {
        var steps = new List<GameTime>();
        while (clock.TryFixedUpdate(now, out var time)) steps.Add(time);
        return steps;
    }

    [Test]
    public void CatchUpIsBoundedAndKeepsFractionWithoutAdvancingDiscardedTime()
    {
        var clock = FixedClock();
        clock.Update(Ms(5));
        Assert.That(Steps(clock, Ms(5)), Is.Empty);
        Assert.That(clock.InterpolationAlpha, Is.EqualTo(1));
        clock.Update(Ms(105));
        var steps = Steps(clock, Ms(105));
        Assert.That(steps.Select(t => t.TotalGameTime), Is.EqualTo(new[] { Ms(10), Ms(20), Ms(30) }));
        Assert.That(steps.All(t => t.ElapsedGameTime == Ms(10)), Is.True);
        Assert.That(steps.All(t => t.UnscaledTotalGameTime == Ms(105)), Is.True);
        Assert.That(steps.All(t => t.UnscaledElapsedGameTime == TimeSpan.Zero), Is.True);
        Assert.That(clock.InterpolationAlpha, Is.EqualTo(.5f));
        clock.Update(Ms(110));
        var next = Steps(clock, Ms(110)).Single();
        Assert.That(next.TotalGameTime, Is.EqualTo(Ms(40)));
        Assert.That(next.UnscaledElapsedGameTime, Is.EqualTo(Ms(5)));
    }

    [TestCase(.5, 40, 2)]
    [TestCase(2, 10, 2)]
    public void ScaleChangesStepFrequencyNotStepSize(double scale, int elapsed, int count)
    {
        var clock = FixedClock(scale);
        clock.Update(Ms(elapsed));
        var steps = Steps(clock, Ms(elapsed));
        Assert.That(steps, Has.Count.EqualTo(count));
        Assert.That(steps.All(t => t.ElapsedGameTime == Ms(10)), Is.True);
    }

    [Test]
    public void PauseCancelsPendingStepsAndResumeDoesNotCatchUpWallTime()
    {
        var clock = FixedClock();
        clock.Update(Ms(25));
        Assert.That(clock.TryFixedUpdate(Ms(25), out _), Is.True);
        clock.Configure(Ms(25), true, Ms(10), 3, 1, true);
        Assert.That(Steps(clock, Ms(25)), Is.Empty);
        var paused = clock.Update(Ms(1000));
        Assert.That(paused.ElapsedGameTime, Is.EqualTo(TimeSpan.Zero));
        Assert.That(paused.UnscaledElapsedGameTime, Is.EqualTo(Ms(975)));
        Assert.That(clock.InterpolationAlpha, Is.EqualTo(1));
        clock.Configure(Ms(1000), true, Ms(10), 3, 1, false);
        clock.Update(Ms(1009));
        Assert.That(Steps(clock, Ms(1009)), Is.Empty);
        clock.Update(Ms(1010));
        Assert.That(Steps(clock, Ms(1010)).Single().TotalGameTime, Is.EqualTo(Ms(20)));
    }

    [Test]
    public void VariableStreamsScaleIndependentlyAndExcludePause()
    {
        var clock = new GameClock();
        clock.Start(TimeSpan.Zero);
        clock.Update(Ms(10));
        clock.Draw(Ms(20));
        clock.Configure(Ms(30), false, Ms(10), 3, .5, false);
        var update = clock.Update(Ms(50));
        var draw = clock.Draw(Ms(60));
        Assert.That(update.TotalGameTime, Is.EqualTo(Ms(40)));
        Assert.That(update.ElapsedGameTime, Is.EqualTo(Ms(30)));
        Assert.That(update.UnscaledElapsedGameTime, Is.EqualTo(Ms(40)));
        Assert.That(draw.ElapsedGameTime, Is.EqualTo(Ms(25)));
        clock.Configure(Ms(60), false, Ms(10), 3, 0, false);
        Assert.That(clock.Draw(Ms(1000)).TotalGameTime, Is.EqualTo(Ms(45)));
        Assert.That(clock.IsPaused, Is.True);
    }

    [Test]
    public void ChangingStepOrModeResetsRemainderButKeepsTotals()
    {
        var clock = FixedClock();
        clock.Update(Ms(15));
        Steps(clock, Ms(15));
        clock.Configure(Ms(15), true, Ms(20), 3, 1, false);
        clock.Update(Ms(30));
        Assert.That(Steps(clock, Ms(30)), Is.Empty);
        clock.Update(Ms(35));
        Assert.That(Steps(clock, Ms(35)).Single().TotalGameTime, Is.EqualTo(Ms(30)));
        clock.Configure(Ms(35), false, Ms(20), 3, 1, false);
        Assert.That(clock.Update(Ms(40)).TotalGameTime, Is.EqualTo(Ms(40)));
        Assert.That(Steps(clock, Ms(40)), Is.Empty);
    }

    [Test]
    public void ResumeRebasesBothFrameStreamsEvenWhenNoCallbacksRanWhilePaused()
    {
        var clock = new GameClock();
        clock.Start(TimeSpan.Zero);
        clock.Update(Ms(10));
        clock.Draw(Ms(20));
        clock.Configure(Ms(30), false, clock.TargetElapsedTime, 5, 1, true);
        clock.Configure(Ms(1030), false, clock.TargetElapsedTime, 5, 1, false);
        var update = clock.Update(Ms(1040));
        var draw = clock.Draw(Ms(1050));
        Assert.That(update.ElapsedGameTime, Is.EqualTo(TimeSpan.Zero));
        Assert.That(draw.ElapsedGameTime, Is.EqualTo(TimeSpan.Zero));
        Assert.That(update.TotalGameTime, Is.EqualTo(Ms(40)));
        Assert.That(draw.TotalGameTime, Is.EqualTo(Ms(50)));
        Assert.That(update.UnscaledElapsedGameTime, Is.EqualTo(Ms(1030)));
        Assert.That(draw.UnscaledElapsedGameTime, Is.EqualTo(Ms(1030)));
        Assert.That(clock.Update(Ms(1060)).ElapsedGameTime, Is.EqualTo(Ms(20)));
    }

    private sealed class EmptyGame : Game { }

    [Test]
    public void PublicControlsValidateAndExplicitPauseSurvivesScaleChanges()
    {
        using var game = new EmptyGame();
        Assert.That(game.IsFixedTimeStep, Is.False);
        Assert.That(game.PauseWhenInactive, Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => game.TargetElapsedTime = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => game.MaxCatchUpSteps = 0);
        foreach (double invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => game.TimeScale = invalid);
        game.IsPaused = true;
        game.TimeScale = 0;
        game.TimeScale = 1;
        Assert.That(game.IsSimulationPaused, Is.True);
        game.IsPaused = false;
        Assert.That(game.IsSimulationPaused, Is.False);
    }

    [Test]
    public void FocusReturnDoesNotClearExplicitPauseOrZeroScale()
    {
        using var game = new EmptyGame();
        game.SafeWindowFocusChanged(false);
        Assert.That(game.IsActive, Is.False);
        Assert.That(game.IsSimulationPaused, Is.False);
        game.PauseWhenInactive = true;
        Assert.That(game.IsSimulationPaused, Is.True);
        game.IsPaused = true;
        game.SafeWindowFocusChanged(true);
        Assert.That(game.IsSimulationPaused, Is.True);
        game.IsPaused = false;
        Assert.That(game.IsSimulationPaused, Is.False);
        game.TimeScale = 0;
        game.SafeWindowFocusChanged(false);
        game.SafeWindowFocusChanged(true);
        Assert.That(game.IsSimulationPaused, Is.True);
    }

    [Test]
    public void RendererClockPreservesPauseScaleOverridesAndConsumesFrameOnlyOnce()
    {
        var clock = new RendererAnimationClock();
        var time = new GameTime(Ms(200), Ms(10)) { UnscaledElapsedGameTime = Ms(20) };
        clock.SetGameTime(time, .5, false);
        var first = clock.Read(1, .04f);
        Assert.That(first.ParticleDeltaSeconds, Is.EqualTo(.02f));
        Assert.That(first.ElapsedSeconds, Is.EqualTo(.01f));
        Assert.That(first.UnscaledElapsedSeconds, Is.EqualTo(.02f));
        Assert.That(clock.Read(1, .04f).ParticleDeltaSeconds, Is.Zero);
        clock.SetGameTime(time, .5, true);
        var paused = clock.Read(1, .04f);
        Assert.That(paused.ParticleDeltaSeconds, Is.Zero);
        Assert.That(paused.ParticleTime, Is.EqualTo(first.ParticleTime));
        Assert.That(paused.SceneTime, Is.EqualTo(first.SceneTime));
        clock.SetGameTime(time, .5, false);
        Assert.That(clock.Read(1, 0).ParticleDeltaSeconds, Is.EqualTo(.01f));
        Assert.That(new RendererAnimationClock().Read(.03f, 0).ParticleDeltaSeconds, Is.EqualTo(.03f));
    }
}
