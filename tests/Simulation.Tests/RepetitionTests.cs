using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// W1696: the same action at the same spot without any effect stops after the data's number of repeats and
/// rests for the data's hours (doing nothing excepted, data); telling someone what they already know is not offered again, and passing
/// knowledge on is company (data).
/// </summary>
public sealed class RepetitionTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private static readonly Position[] StartPositions = [new(0, 0), new(5, 0), new(2, 5), new(7, 5)];

    [Fact]
    public void LookingAroundWithoutEffectAtOneSpotRestsAfterTheLimitAndIsDroppedWhileItRests()
    {
        var repetition = Content.Brain.Weights.Repetition!;
        var (world, inhabitants) = NewWorld();
        var idler = inhabitants.Get("inh-000001");
        idler.Needs["fun"] = 1.0;
        idler.Moods["boredom"] = 0;
        var events = new List<WorldEvent>();
        inhabitants.Reported += e =>
        {
            if (e.InhabitantId == idler.Id)
            {
                events.Add(e);
            }
        };

        for (var i = 1; i <= repetition.MaxRepeatsWithoutEffect; i++)
        {
            idler.Needs["fun"] = 1.0;
            idler.Moods["boredom"] = 0;
            Do(world, idler, "inspect");
            var finished = events.Last(e => e.Kind == InhabitantsSystem.ActionFinished);
            Assert.Equal(i == repetition.MaxRepeatsWithoutEffect ? InhabitantsSystem.RepeatLimit : null, finished.Detail);
        }

        var restedAt = world.Tick;
        Assert.True(Rests(idler, "inspect", null, world.Tick));

        // An answer that still waits for the resting action is dropped, and the routine never chooses it.
        idler.Activity = new Activity("inspect", null, idler.Position, null);
        world.Step();
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.Detail == InhabitantsSystem.Resting);
        var restTicks = (long)Math.Round(repetition.RestHours * world.Calendar.Settings.TicksPerHour);
        while (world.Tick < restedAt + restTicks - 1)
        {
            world.Step();
            Assert.NotEqual("inspect", idler.Activity?.Action);
        }

        world.Step();
        Assert.False(Rests(idler, "inspect", null, world.Tick));
    }

    [Fact]
    public void ExemptActionsNeverRest()
    {
        // Doing nothing (data: exempt_actions, idle) never rests: it would only send the inhabitant off on strolls.
        var repetition = Content.Brain.Weights.Repetition!;
        Assert.Contains("idle", repetition.ExemptActions!);
        var (world, inhabitants) = NewWorld();
        var idler = inhabitants.Get("inh-000001");
        for (var i = 0; i < repetition.MaxRepeatsWithoutEffect * 3; i++)
        {
            Do(world, idler, "idle");
            Assert.False(Rests(idler, "idle", null, world.Tick));
        }
    }

    [Fact]
    public void AnActionWithAnEffectNeverRests()
    {
        var repetition = Content.Brain.Weights.Repetition!;
        var (world, inhabitants) = NewWorld();
        var looker = inhabitants.Get("inh-000001");
        for (var i = 0; i < repetition.MaxRepeatsWithoutEffect * 3; i++)
        {
            // Looking around (no target) is fun while fun is low: an effect every time.
            looker.Needs["fun"] = 0.2;
            Do(world, looker, "inspect");
            Assert.False(Rests(looker, "inspect", null, world.Tick));
            Assert.Null(looker.Repeat);
        }
    }

    [Fact]
    public void ANearlyFullNeedToppedUpCountsAsNoEffect()
    {
        var repetition = Content.Brain.Weights.Repetition!;
        var (world, inhabitants) = NewWorld();
        var looker = inhabitants.Get("inh-000001");
        for (var i = 1; i <= repetition.MaxRepeatsWithoutEffect; i++)
        {
            looker.Needs["fun"] = 1.0 - (repetition.MinEffect / 2);
            looker.Moods["boredom"] = 0;
            Do(world, looker, "inspect");
        }

        Assert.True(Rests(looker, "inspect", null, world.Tick));
    }

    [Fact]
    public void WhatTheListenerAlreadyBelievesIsNotPassedAgainAndPassingOnIsCompany()
    {
        var (world, inhabitants) = NewWorld();
        var teller = inhabitants.Get("inh-000001");
        var listener = inhabitants.Get("inh-000002");
        teller.Beliefs.Set("blue_berry", "edible", true, 0.9, KnowledgeSources.Experienced, null, 1);
        Assert.NotNull(InhabitantsSystem.PassableBelief(Content.Knowledge!, teller, listener, "teach"));

        teller.Needs["company"] = 0.1;
        teller.Activity = new Activity("teach", listener.Id, listener.Position, null);
        for (var i = 0; i < 10_000 && teller.Activity is not null; i++)
        {
            world.Step();
        }

        Assert.True(listener.Beliefs.TryGet("blue_berry", "edible", out var heard) && heard.Source == KnowledgeSources.Heard);
        Assert.True(teller.Needs["company"] > 0.1, "teaching is company (content/effects)");

        // The listener now holds it as surely as it would hear it again: nothing to pass, nothing offered.
        Assert.Null(InhabitantsSystem.PassableBelief(Content.Knowledge!, teller, listener, "teach"));
    }

    [Fact]
    public void RestingWithOneTargetDoesNotBlockAnotherTargetAndAPlaceOnlyThere()
    {
        // GW2 review #164: after repeats without effect towards A the action rests for A only; an action without
        // a target rests only at its place.
        var repetition = Content.Brain.Weights.Repetition!;
        var (world, inhabitants) = NewWorld();
        var teller = inhabitants.Get("inh-000001");
        teller.Needs["company"] = 1.0;
        teller.Resting.Add(new RestingAction("warn", "inh-000002", teller.Position, world.Tick + 100));
        teller.Resting.Add(new RestingAction("inspect", null, teller.Position, world.Tick + 100));
        Assert.True(Rests(teller, "warn", "inh-000002", world.Tick));
        Assert.False(Rests(teller, "warn", "inh-000003", world.Tick));
        Assert.True(Rests(teller, "inspect", null, world.Tick));
        var away = new Position(teller.Position.X + repetition.SamePlaceM + 1, teller.Position.Z);
        Assert.False(teller.Rests("inspect", null, away, world.Tick, repetition.SamePlaceM));

        // Warning B is still done (A rests); warning A is dropped as resting.
        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        var b = inhabitants.Get("inh-000003");
        teller.Activity = new Activity("warn", b.Id, b.Position, null);
        for (var i = 0; i < 10_000 && teller.Activity is not null; i++)
        {
            world.Step();
        }

        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == teller.Id && e.TargetId == b.Id);
        teller.Activity = new Activity("warn", "inh-000002", inhabitants.Get("inh-000002").Position, null);
        world.Step();
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.TargetId == "inh-000002" && e.Detail == InhabitantsSystem.Resting);
    }

    private static bool Rests(Inhabitant inhabitant, string action, string? targetId, long tick) =>
        inhabitant.Rests(action, targetId, inhabitant.Position, tick, Content.Brain.Weights.Repetition!.SamePlaceM);

    private static void Do(World world, Inhabitant inhabitant, string action)
    {
        inhabitant.Activity = new Activity(action, null, inhabitant.Position, null);
        for (var i = 0; i < 10_000 && inhabitant.Activity is not null; i++)
        {
            world.Step();
        }

        Assert.Null(inhabitant.Activity);
    }

    private static (World World, InhabitantsSystem Inhabitants) NewWorld()
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, StartPositions);
        return (world, inhabitants);
    }
}
