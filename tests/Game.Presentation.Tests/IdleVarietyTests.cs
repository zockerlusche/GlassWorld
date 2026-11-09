using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Idle variety (F5282): every idle behaviour plays a real clip, draws spread over the slots by weight,
/// speed and mirroring vary from data, inhabitants change their idle behaviour at their own ticks, and the
/// thought dots pulse calmly. All deterministic from inhabitant id and tick.
/// </summary>
public sealed class IdleVarietyTests
{
    private static readonly string[] Ids = ["inh-000001", "inh-000002", "inh-000003", "inh-000004", "inh-000005", "inh-000006"];

    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static AnimationLibrary Library() => Read<AnimationLibrary>("animation", "library.json");

    private static LookFigures Figures() => Read<LookFigures>("look", "figures.json");

    [Fact]
    public void EveryIdleBehaviourPlaysAnApprovedClipAndNotTheFallback()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        var assets = library.Clips.Where(c => c.Source == AnimationClipSource.Asset).ToDictionary(c => c.Id, c => c.Asset!);
        Assert.All(library.Idle, i => Assert.True(assets.ContainsKey(i.Clip), $"idle '{i.Id}' plays '{i.Clip}', not an asset clip"));
        var values = new Dictionary<string, double>();
        Assert.All(Enumerable.Range(0, 500), s => Assert.False(chooser.Idle("inh-000001", s, values).Clip.FellBack));

        // More than a standing pose: the idle behaviours use several different clips of the figure.
        Assert.True(library.Idle.Select(i => assets[i.Clip]).Distinct(StringComparer.Ordinal).Count() >= 5);
    }

    [Fact]
    public void DrawsSpreadOverTheSlotsByWeight()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        var values = new Dictionary<string, double>();
        const int Slots = 20000;
        var counts = Enumerable.Range(0, Slots).Select(s => chooser.ForIdle("inh-000007", s, values).ClipId)
            .GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Neutral core values (0.5): weight = base + half of every factor weight.
        var weights = library.Idle.ToDictionary(i => i.Clip, i => i.Weight + i.Factors.Sum(f => f.Weight / 2), StringComparer.Ordinal);
        var total = weights.Values.Sum();
        foreach (var (clip, weight) in weights)
        {
            var share = counts.GetValueOrDefault(clip) / (double)Slots;
            Assert.InRange(share, (weight / total) - 0.02, (weight / total) + 0.02);
        }

        // No behaviour runs for long stretches: the longest run of the same clip stays short.
        var run = 1;
        var longest = 1;
        for (long s = 1; s < 2000; s++)
        {
            run = chooser.ForIdle("inh-000007", s, values).ClipId == chooser.ForIdle("inh-000007", s - 1, values).ClipId ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        Assert.True(longest <= 8, $"longest run {longest}");
    }

    [Fact]
    public void SpeedAndMirrorVaryWithinTheData()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        var idle = library.Idle.ToDictionary(i => i.Clip, StringComparer.Ordinal);
        var values = new Dictionary<string, double>();
        var draws = Enumerable.Range(0, 4000).Select(s => chooser.Idle("inh-000008", s, values)).ToList();
        foreach (var draw in draws)
        {
            var spread = idle[draw.Clip.ClipId].SpeedSpread ?? 0;
            Assert.InRange(draw.Speed, (draw.Clip.Speed * (1 - spread)) - 1e-9, (draw.Clip.Speed * (1 + spread)) + 1e-9);
            Assert.True(!draw.Mirror || idle[draw.Clip.ClipId].Mirror == true, $"{draw.Clip.ClipId} must not mirror");
        }

        var stand = draws.Where(d => d.Clip.ClipId == "stand").ToList();
        Assert.True(stand.Select(d => d.Speed).Distinct().Count() > 10, "the speed varies from draw to draw");
        // Mirroring is off in the data until a clip shows it flips cleanly (review GW3 #140); when the
        // data allows it, about half of the draws mirror.
        var mirrorable = draws.Where(d => idle[d.Clip.ClipId].Mirror == true).ToList();
        if (mirrorable.Count > 0)
        {
            Assert.InRange(mirrorable.Count(d => d.Mirror) / (double)mirrorable.Count, 0.4, 0.6);
        }
        else
        {
            Assert.DoesNotContain(draws, d => d.Mirror);
        }
    }

    [Fact]
    public void TheSameInhabitantAndTickLookTheSameAgain()
    {
        var figures = Figures();
        var first = new AnimationChooser(Library());
        var second = new AnimationChooser(Library());
        var values = new Dictionary<string, double> { ["sleep"] = 0.3, ["joy"] = 0.7 };
        foreach (var id in Ids)
        {
            for (long tick = 0; tick < 300; tick += 7)
            {
                var slot = AnimationChooser.IdleSlot(id, tick, figures.IdleSlotTicks, figures.IdleSlotSpread);
                Assert.Equal(slot, AnimationChooser.IdleSlot(id, tick, figures.IdleSlotTicks, figures.IdleSlotSpread));
                Assert.Equal(first.Idle(id, slot, values), second.Idle(id, slot, values));
            }
        }
    }

    [Fact]
    public void InhabitantsChangeTheirIdleBehaviourAtTheirOwnTicks()
    {
        var figures = Figures();
        Assert.True(figures.IdleSlotSpread > 0, "the data spreads the slot lengths");
        HashSet<long> Changes(string id) =>
            [.. Enumerable.Range(1, 2000).Where(t => AnimationChooser.IdleSlot(id, t, figures.IdleSlotTicks, figures.IdleSlotSpread)
                != AnimationChooser.IdleSlot(id, t - 1, figures.IdleSlotTicks, figures.IdleSlotSpread)).Select(t => (long)t)];

        var changes = Ids.Select(Changes).ToList();
        foreach (var c in changes)
        {
            // About one change per slot length (idle_slot_ticks ± spread).
            var mean = 2000.0 / c.Count;
            Assert.InRange(mean, figures.IdleSlotTicks * (1 - figures.IdleSlotSpread) - 1, figures.IdleSlotTicks * (1 + figures.IdleSlotSpread) + 1);
        }

        // No two inhabitants change in lock-step: they share only a small part of their change ticks.
        for (var a = 0; a < changes.Count; a++)
        {
            for (var b = a + 1; b < changes.Count; b++)
            {
                var shared = changes[a].Intersect(changes[b]).Count();
                Assert.True(shared < Math.Min(changes[a].Count, changes[b].Count) / 2, $"{Ids[a]} and {Ids[b]} share {shared} change ticks");
            }
        }

        // Without spread every inhabitant keeps the plain slot length, still on its own phase.
        var plain = Enumerable.Range(1, 200).Count(t => AnimationChooser.IdleSlot("inh-000001", t, 10, 0) != AnimationChooser.IdleSlot("inh-000001", t - 1, 10, 0));
        Assert.Equal(20, plain);
    }

    [Fact]
    public void TwoInhabitantsStandingTogetherDoNotMirrorEachOther()
    {
        var figures = Figures();
        var chooser = new AnimationChooser(Library());
        var values = new Dictionary<string, double>();
        var same = 0;
        const int Ticks = 1000;
        for (long tick = 0; tick < Ticks; tick++)
        {
            var a = chooser.Idle("inh-000001", AnimationChooser.IdleSlot("inh-000001", tick, figures.IdleSlotTicks, figures.IdleSlotSpread), values);
            var b = chooser.Idle("inh-000002", AnimationChooser.IdleSlot("inh-000002", tick, figures.IdleSlotTicks, figures.IdleSlotSpread), values);
            same += a == b ? 1 : 0;
        }

        Assert.True(same < Ticks / 10, $"same look in {same} of {Ticks} ticks");
    }

    [Fact]
    public void ThinkingDotsShowWithAnOpenQuestionAndPulseCalmly()
    {
        var look = Figures().ThinkingDots;
        Assert.False(ThinkingDots.Shown(0));
        Assert.True(ThinkingDots.Shown(1) && ThinkingDots.Shown(3));
        Assert.True(look.PeriodS >= 1.5, "a calm wave, not a blink");
        Assert.InRange(look.AlphaMin, 0, look.AlphaMax);

        var phase = ThinkingDots.Phase("inh-000001");
        Assert.Equal(phase, ThinkingDots.Phase("inh-000001"));
        Assert.NotEqual(phase, ThinkingDots.Phase("inh-000002"));
        for (var t = 0.0; t < 10; t += 0.05)
        {
            for (var dot = 0; dot < ThinkingDots.Count; dot++)
            {
                Assert.InRange(ThinkingDots.Alpha(look, dot, t, phase), look.AlphaMin - 1e-9, look.AlphaMax + 1e-9);
            }
        }

        // The wave runs from left to right: the middle dot is where the left one was a third of a period ago.
        var third = look.PeriodS / ThinkingDots.Count;
        Assert.Equal(ThinkingDots.Alpha(look, 0, 1.0, phase), ThinkingDots.Alpha(look, 1, 1.0 + third, phase), 9);
        Assert.Equal(ThinkingDots.Alpha(look, 0, 0.3, phase), ThinkingDots.Alpha(look, 0, 0.3 + look.PeriodS, phase), 9);
        Assert.Equal(-look.SpacingPx, ThinkingDots.OffsetPx(look, 0), 9);
        Assert.Equal(0, ThinkingDots.OffsetPx(look, 1), 9);
    }
}
