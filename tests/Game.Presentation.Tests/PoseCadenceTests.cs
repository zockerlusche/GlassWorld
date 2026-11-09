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
        });
        foreach (var line in PoseMeter.Table($"fixed x{speed} seed {seed}", result))
        {
            output.WriteLine(line);
        }

        var minutes = result.RealS / 60;
        Assert.All(result.Changes, c =>
        {
            var standing = c.Value.GetValueOrDefault(PoseSource.Idle) + c.Value.GetValueOrDefault(PoseSource.Action);
            Assert.True(standing / minutes <= figures.PoseCadence.MaxChangesPerMinute, $"{c.Key}: {standing / minutes:0.0} standing pose changes per minute");
            Assert.True(c.Value.GetValueOrDefault(PoseSource.Walk) / minutes <= figures.PoseCadence.MaxWalkChangesPerMinute, $"{c.Key}: {c.Value.GetValueOrDefault(PoseSource.Walk) / minutes:0.0} walk starts/stops per minute");
        });
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
            Assert.True(speed < 2 || result.Changes.Values.Max(c => c.GetValueOrDefault(PoseSource.Idle) + c.GetValueOrDefault(PoseSource.Action)) / (result.RealS / 60) > figures.PoseCadence.MaxChangesPerMinute);
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
        Assert.False(gate.TryPass(1, 10.0, walk: true));
        Assert.False(gate.TryPass(2, 10.2));
        Assert.True(gate.TryPass(2, 10.2, walk: true));
        Assert.True(gate.TryPass(3, 10.4));
    }

    private static HashSet<string> IdleAssets(AnimationLibrary library)
    {
        var assets = library.Clips.Where(c => c.Asset is not null).ToDictionary(c => c.Id, c => c.Asset!, StringComparer.Ordinal);
        return [.. library.Idle.Select(i => assets[i.Clip])];
    }
}
