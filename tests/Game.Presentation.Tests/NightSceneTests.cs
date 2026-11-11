using GlassWorld.Contracts;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class NightSceneTests
{
    private static LookNight Data() => new() { ContractVersion = ContractInfo.Version, FadeOutS = 4, FadeInS = 3, MinSceneS = 6, Darkness = 0.8, PollS = 2 };

    private static void Run(NightScene scene, bool night, bool? done, double seconds)
    {
        for (var t = 0.0; t < seconds; t += 0.1)
        {
            scene.Update(night, done, 0.1);
        }
    }

    [Fact]
    public void TheNightDarkensDreamsAndTheMorningFadesInWhenTheServiceIsDone()
    {
        var scene = new NightScene(Data());
        Run(scene, false, null, 1);
        Assert.Equal(NightStage.Day, scene.Stage);

        scene.Update(true, false, 0.1);
        Assert.Equal(NightStage.FadingOut, scene.Stage);
        Run(scene, true, false, 4.1);
        Assert.Equal((NightStage.Dream, 0.8), (scene.Stage, scene.Darkness));

        // Busy service: the scene stays, however long.
        Run(scene, true, false, 30);
        Assert.Equal(NightStage.Dream, scene.Stage);

        Run(scene, true, true, 0.2);
        Assert.Equal(NightStage.FadingIn, scene.Stage);
        Run(scene, true, true, 3.2);
        Assert.Equal((NightStage.Awake, 0.0), (scene.Stage, scene.Darkness));

        // The world's night goes on: no second scene; the next night shows one again.
        Run(scene, true, false, 5);
        Assert.Equal(NightStage.Awake, scene.Stage);
        Run(scene, false, null, 1);
        scene.Update(true, false, 0.1);
        Assert.Equal(NightStage.FadingOut, scene.Stage);
    }

    [Fact]
    public void AQuickNightStillStandsItsMinimumTime()
    {
        var scene = new NightScene(Data());
        scene.Update(false, null, 0.1);
        Run(scene, true, true, 4.1 + 5);
        Assert.Equal(NightStage.Dream, scene.Stage);
        Run(scene, true, true, 1.2);
        Assert.Equal(NightStage.FadingIn, scene.Stage);
    }

    [Fact]
    public void AWorldStartedOrLoadedAtNightShowsNoScene()
    {
        var scene = new NightScene(Data());
        Run(scene, true, false, 10);
        Assert.Equal((NightStage.Day, 0.0), (scene.Stage, scene.Darkness));
    }

    [Fact]
    public void UnknownBeforeTheFirstTickIsNoStartOfTheNight()
    {
        var scene = new NightScene(Data());
        scene.Update(null, null, 0.1);
        Run(scene, true, false, 10);
        Assert.Equal(NightStage.Day, scene.Stage);
    }

    [Fact]
    public void PauseHoldsTheFadeAndMorningEndsTheScene()
    {
        var scene = new NightScene(Data());
        scene.Update(false, null, 0.1);
        Run(scene, true, false, 2);
        var held = scene.Darkness;
        scene.Update(true, false, 0);
        Assert.Equal(held, scene.Darkness);

        Run(scene, true, false, 3);
        Run(scene, false, false, 3.2);
        Assert.Equal((NightStage.Day, 0.0), (scene.Stage, scene.Darkness));
    }

    [Fact]
    public void TheServiceIsDoneWhenNothingIsLeftOrTheBudgetIsSpent()
    {
        PhaseStatus Status(int queued, int running, int budget) => new() { ContractVersion = ContractInfo.Version, Phase = PhaseStatusPhase.Night, Queued = queued, Running = running, BudgetLeftS = budget };
        Assert.True(NightScene.Done(Status(0, 0, 900)));
        Assert.True(NightScene.Done(Status(3, 1, 0)));
        Assert.False(NightScene.Done(Status(2, 0, 900)));

        // Still in the day phase (the service has not switched yet): never done.
        var day = Status(0, 0, 900);
        day.Phase = PhaseStatusPhase.Day;
        Assert.False(NightScene.Done(day));
    }
}
