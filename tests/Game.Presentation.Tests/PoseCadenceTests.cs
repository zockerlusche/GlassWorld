using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Calm poses (W1696/W1697): on the island, figures change their standing pose (and start or stop walking) at
/// most the data's number of times per real minute, at normal speed and at x16; no two figures change in the same frame; in a conversation
/// nobody sits or bends; idle variety starts only after standing idle for a while. The measurement drives the
/// core like the island presenter (<see cref="PoseMeter"/>) and prints a table (W1697 report).
/// </summary>
public sealed class PoseCadenceTests(ITestOutputHelper output)
{
    private const long Day = 1440;
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([Content, .. path])));

    [Theory]
    [InlineData(7, 1.0)]
    [InlineData(7, 16.0)]
    [InlineData(42, 16.0)]
    public void FiguresChangeTheirPoseCalmlyAndNeverInTheSameFrame(ulong seed, double speed)
    {
        var figures = Read<LookFigures>("look", "figures.json");
        var library = Read<AnimationLibrary>("animation", "library.json");
        var chooser = new AnimationChooser(library);
        var host = SimulationHost.CreateGeneratedWorld(Content, seed, new MockDecisionPort(), "de", Read<LookWorld>("look", "world.json").IslandPreset);
        var gate = new PoseGate(figures.PoseCadence.StaggerS);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var lowSince = new Dictionary<string, double>(StringComparer.Ordinal);
        var longestLow = 0.0;
        var shownIdle = new Dictionary<string, bool>(StringComparer.Ordinal);
        var low = new HashSet<string>(library.Idle.Where(i => !figures.PoseCadence.ConversationIdle.Contains(i.Id)).Select(i => i.Clip), StringComparer.Ordinal);
        var result = PoseMeter.Run(host, speed, Day, figures.WalkMinStepM, IdleAssets(library), id =>
        {
            var cadence = new PoseCadence(figures.PoseCadence, figures.IdleSlotSpread, id, figures.Lying);
            return (inhabitant, all, _, moving, realS, frame, applied) =>
            {
                var conversation = InhabitantActivity.InConversation(inhabitant, all);
                if (applied)
                {
                    AnimationChooser.CoreValues(values, inhabitant.Needs, inhabitant.Moods, inhabitant.Traits);
                    cadence.Want(chooser, inhabitant.Action, moving, values, InhabitantActivity.TargetKind(inhabitant, all), conversation, realS);
                }

                var clip = cadence.Update(realS, frame, gate);
                shownIdle[id] = !clip.Walking && chooser.IsIdleClip(clip.Clip.ClipId);
                if (conversation && chooser.StandsIdle(inhabitant.Action) && !clip.Walking && low.Contains(clip.Clip.ClipId))
                {
                    // Sitting or bending in a conversation ends at once (a free frame of the gate).
                    lowSince.TryAdd(id, realS);
                    longestLow = Math.Max(longestLow, realS - lowSince[id]);
                }
                else
                {
                    lowSince.Remove(id);
                }

                return PoseCadence.PoseKey(clip, figures.Lying);
            };
        }, id => shownIdle.GetValueOrDefault(id));
        foreach (var line in PoseMeter.Table($"fixed x{speed} seed {seed}", result))
        {
            output.WriteLine(line);
        }

        var minutes = result.RealS / 60;
        Assert.All(result.Changes, c =>
        {
            // Idle variety is calmed; walking and core actions follow the core without delay (GW3 review #164).
            var idle = c.Value.GetValueOrDefault(PoseSource.Idle);
            var core = c.Value.GetValueOrDefault(PoseSource.Walk) + c.Value.GetValueOrDefault(PoseSource.Action);
            Assert.True(idle / minutes <= figures.PoseCadence.MaxChangesPerMinute, $"{c.Key}: {idle / minutes:0.0} idle changes per minute");
            Assert.True(core / minutes <= figures.PoseCadence.MaxWalkChangesPerMinute, $"{c.Key}: {core / minutes:0.0} walk and action changes per minute");
        });
        if (speed <= 1)
        {
            // At normal speed figures do vary their idle poses: a meter that counts none measures nothing (review GW3 #170).
            Assert.True(result.Changes.Values.Sum(c => c.GetValueOrDefault(PoseSource.Idle)) > 0, "no idle changes counted at x1");
        }

        Assert.Equal(0, result.SharedFrames);
        Assert.True(longestLow <= 0.1, $"sat or bent in a conversation for {longestLow:0.00} s");
    }

    [Fact]
    public void BuildCChangedPosesByCoreTicks()
    {
        // The measurement of build c (W1697 report): idle slots in core ticks, no hold, no gate.
        var figures = Read<LookFigures>("look", "figures.json");
        var library = Read<AnimationLibrary>("animation", "library.json");
        var chooser = new AnimationChooser(library);
        foreach (var speed in new[] { 1.0, 16.0 })
        {
            var host = SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", Read<LookWorld>("look", "world.json").IslandPreset);
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            var last = new Dictionary<string, string>(StringComparer.Ordinal);
            var result = PoseMeter.Run(host, speed, Day, figures.WalkMinStepM, IdleAssets(library), id => (inhabitant, all, tick, moving, _, _, applied) =>
            {
                if (applied)
                {
                    AnimationChooser.CoreValues(values, inhabitant.Needs, inhabitant.Moods, inhabitant.Traits);
                    var slot = AnimationChooser.IdleSlot(id, tick, 10, figures.IdleSlotSpread);
                    last[id] = PoseCadence.PoseKey(chooser.ForInhabitant(inhabitant.Action, moving, id, slot, values, InhabitantActivity.TargetKind(inhabitant, all)));
                }

                return last[id];
            });
            foreach (var line in PoseMeter.Table($"build c x{speed}", result))
            {
                output.WriteLine(line);
            }

            // At x16 the old idle slot in core ticks changed poses far beyond the limit the fix is held to.
            Assert.True(speed < 2 || result.Changes.Values.Max(c => c.GetValueOrDefault(PoseSource.Idle)) / (result.RealS / 60) > figures.PoseCadence.MaxChangesPerMinute);
        }
    }

    [Fact]
    public void IdleVarietyStartsOnlyAfterStandingIdleAndHoldsEachPose()
    {
        var figures = Read<LookFigures>("look", "figures.json");
        var data = figures.PoseCadence;
        var chooser = new AnimationChooser(Read<AnimationLibrary>("animation", "library.json"));
        var cadence = new PoseCadence(data, figures.IdleSlotSpread, "inh-000001");
        var gate = new PoseGate(data.StaggerS);
        var values = new Dictionary<string, double> { ["sleep"] = 0.1, ["curiosity"] = 0.9 };
        var rest = PoseCadence.PoseKey(chooser.IdleById("inh-000001", data.RestIdle));
        var shown = new List<(double At, string Pose)>();
        long frame = 0;
        for (var t = 0.0; t < 600; t += 1 / 60.0, frame++)
        {
            cadence.Want(chooser, null, false, values, null, false, t);
            var pose = PoseCadence.PoseKey(cadence.Update(t, frame, gate));
            if (shown.Count == 0 || shown[^1].Pose != pose)
            {
                shown.Add((t, pose));
            }

            if (t < data.IdleVarietyAfterS)
            {
                Assert.Equal(rest, pose);
            }
        }

        Assert.True(shown.Count > 3, "idle variety plays after a while");
        Assert.All(shown.Zip(shown.Skip(1)), p => Assert.True(p.Second.At - p.First.At >= data.MinPoseS - 1e-9, $"{p.First.Pose} held {p.Second.At - p.First.At:0.00} s"));
    }

    [Fact]
    public void TheGateLetsOneFigurePerFrameAndKeepsTheStagger()
    {
        var gate = new PoseGate(0.4);
        Assert.True(gate.TryPass(1, 10.0));
        Assert.False(gate.TryPass(1, 10.0, frameOnly: true));
        Assert.False(gate.TryPass(2, 10.2));
        Assert.True(gate.TryPass(2, 10.2, frameOnly: true));
        Assert.True(gate.TryPass(3, 10.4));
    }

    [Fact]
    public void ArrivingWalkersStopWalkingWithinAFrameEachEvenWhenManyArriveAtOnce()
    {
        // GW3 review #164: stopping to walk is not staggered and not held, so nobody marches in place.
        const int Figures = 100;
        var figures = Read<LookFigures>("look", "figures.json");
        var chooser = new AnimationChooser(Read<AnimationLibrary>("animation", "library.json"));
        var gate = new PoseGate(figures.PoseCadence.StaggerS);
        var values = new Dictionary<string, double>();
        var cadences = Enumerable.Range(1, Figures).Select(i => new PoseCadence(figures.PoseCadence, figures.IdleSlotSpread, $"inh-{i:000000}", figures.Lying)).ToList();
        long frame = 0;
        var t = 0.0;
        foreach (var c in cadences)
        {
            c.Want(chooser, "wander", true, values, null, false, t);
            Assert.True(c.Update(t, frame++, gate).Walking);
        }

        // All arrive in the same frame (a tick at x16): each stops at the next free frame.
        var arrived = frame;
        var stopped = new long[Figures];
        foreach (var c in cadences)
        {
            c.Want(chooser, "eat", false, values, AnimationMappingTarget.Object, false, t);
        }

        for (; frame < arrived + Figures + 1; frame++, t += 1 / 60.0)
        {
            for (var i = 0; i < Figures; i++)
            {
                if (stopped[i] == 0 && !cadences[i].Update(t, frame, gate).Walking)
                {
                    stopped[i] = frame;
                }
            }
        }

        Assert.All(stopped, s => Assert.InRange(s, arrived, arrived + Figures - 1));
        Assert.Equal(Figures, stopped.Distinct().Count());
    }

    [Fact]
    public void ACoreActionShowsWithinAFrameEvenRightAfterAnIdlePoseBegan()
    {
        // GW3 re-check #164: idle -> core action is not held min_pose_s (a short drink at x16 must show).
        var figures = Read<LookFigures>("look", "figures.json");
        var chooser = new AnimationChooser(Read<AnimationLibrary>("animation", "library.json"));
        var gate = new PoseGate(figures.PoseCadence.StaggerS);
        var cadence = new PoseCadence(figures.PoseCadence, figures.IdleSlotSpread, "inh-000001", figures.Lying);
        var values = new Dictionary<string, double>();
        cadence.Want(chooser, null, false, values, null, false, 0);
        var idle = cadence.Update(0, 0, gate);
        cadence.Want(chooser, "drink", false, values, AnimationMappingTarget.Object, false, 0.5);
        Assert.NotEqual(PoseCadence.PoseKey(idle), PoseCadence.PoseKey(cadence.Update(0.5, 1, gate)));
        Assert.Equal("drink", cadence.Update(0.52, 2, gate).Clip.ClipId);
        Assert.True(0.5 < figures.PoseCadence.MinPoseS);
    }

    private static HashSet<string> IdleAssets(AnimationLibrary library)
    {
        var assets = library.Clips.Where(c => c.Asset is not null).ToDictionary(c => c.Id, c => c.Asset!, StringComparer.Ordinal);
        return [.. library.Idle.Select(i => assets[i.Clip])];
    }
}
