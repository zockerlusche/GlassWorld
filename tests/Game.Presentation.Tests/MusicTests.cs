using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>Calm music by time of day, season and epoch (A1352, B8 R6, B2466).</summary>
public sealed class MusicTests
{
    private static readonly MusicSettings Settings =
        ContractSerializer.Deserialize<MusicSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "music", "music.json")));

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    [Fact]
    public void TheMoodChangesWithThePhaseOfTheDay()
    {
        // Criterion 1: over one spring day the selection follows dawn, day, dusk and night.
        var director = new MusicDirector(Settings, 42);
        var moods = new List<string>();
        for (long t = 0; t < Calendar.TicksPerDay; t += Calendar.Settings.TicksPerHour / 4)
        {
            var mood = director.MoodAt(Calendar.At(t), "wilderness");
            if (moods.Count == 0 || moods[^1] != mood)
            {
                moods.Add(mood);
            }
        }

        Assert.True(moods.Distinct().Count() >= 3, string.Join(" > ", moods));
        Assert.Equal("night", director.MoodFor("night", "spring", "wilderness"));
        Assert.Equal("day", director.MoodFor("day", "summer", "wilderness"));
        Assert.Equal("dawn", director.MoodFor("dawn", "summer", "wilderness"));
        Assert.Equal("night", director.MoodFor("day", "winter", "wilderness"));
    }

    [Fact]
    public void EveryRuleNamesAMoodAndTheLastMatchesEverything()
    {
        var moods = Settings.Moods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Settings.Rules, r => Assert.Contains(r.Mood, moods));
        var last = Settings.Rules.Last();
        Assert.Empty(last.TimesOfDay);
        Assert.Empty(last.Seasons);
        Assert.Empty(last.Epochs);
    }

    [Fact]
    public void AChangeOfMoodCrossFadesAndAPieceEndsWithAPause()
    {
        var director = new MusicDirector(Settings, 42);
        var first = director.Update(0, "day");
        Assert.Equal(("day", 0.0), (first.Start!.Mood, first.FadeOutS));
        Assert.InRange(first.Start.Seconds, Settings.PieceS.Min, Settings.PieceS.Max);
        Assert.Null(director.Update(1, "day").Start);

        // Dusk: the day piece fades out while the dusk piece fades in, over the data's fade time.
        var at = Settings.MinMoodS;
        var change = director.Update(at, "dusk");
        Assert.Equal("dusk", change.Start!.Mood);
        Assert.Equal(Settings.FadeS, change.FadeOutS);
        Assert.Equal(Settings.FadeS, change.Start.FadeInS);

        // The piece ends by itself: silence, then the next piece of the same mood.
        var end = at + change.Start.Seconds;
        Assert.Null(director.Update(end, "dusk").Start);
        Assert.Null(director.Update(end + Settings.PauseS.Min - 1, "dusk").Start);
        var next = director.Update(end + Settings.PauseS.Max + 1, "dusk");
        Assert.Equal("dusk", next.Start!.Mood);
        Assert.NotEqual(change.Start.Seed, next.Start.Seed);

        // Same world, same choices.
        var again = new MusicDirector(Settings, 42);
        Assert.Equal(first.Start, again.Update(0, "day").Start);
    }

    [Fact]
    public void APhaseShorterThanTheFadeDoesNotCutAPieceOff()
    {
        // Review #92: at x16 a twilight lasts 2.3 s, less than fade_s; the mood holds for min_mood_s.
        var director = new MusicDirector(Settings, 42);
        Assert.NotNull(director.Update(0, "day").Start);
        Assert.Null(director.Update(2.3, "dusk").Start);
        Assert.Null(director.Update(4.6, "night").Start);
        Assert.Equal("day", director.Current);
        var change = director.Update(Settings.MinMoodS, "night");
        Assert.Equal(("night", Settings.FadeS), (change.Start!.Mood, change.FadeOutS));
    }

    [Fact]
    public void AtFastForwardMoodsNeverChangeFasterThanTheMinimumAndFadesNeverOverlap()
    {
        // A whole spring day at x16 in real seconds: every start is at least min_mood_s after the one
        // before, which is at least fade_s, so a fading piece has ended before its player is used again.
        var director = new MusicDirector(Settings, 42);
        var factor = Calendar.Settings.FastForwardFactors.Max();
        var secondsPerTick = Calendar.RealSecondsPerTickAtNormalSpeed / factor;
        var starts = new List<double>();
        for (long t = 0; t < Calendar.TicksPerDay; t++)
        {
            var now = t * secondsPerTick;
            if (director.Update(now, director.MoodAt(Calendar.At(t), "wilderness")) is { Start: not null, FadeOutS: > 0 })
            {
                starts.Add(now);
            }
        }

        Assert.NotEmpty(starts);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= Settings.MinMoodS - 1e-9, $"{p.First:0.0} s -> {p.Second:0.0} s"));
        Assert.True(Settings.MinMoodS >= Settings.FadeS);
    }

    [Fact]
    public void BrokenMusicDataIsRefusedOnLoad()
    {
        var unknown = ContractSerializer.Deserialize<MusicSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "music", "music.json")));
        unknown.Rules.First().Mood = "no_such_mood";
        Assert.Throws<InvalidDataException>(() => new MusicDirector(unknown, 1));
        var short_ = ContractSerializer.Deserialize<MusicSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "music", "music.json")));
        short_.MinMoodS = short_.FadeS - 1;
        Assert.Throws<InvalidDataException>(() => new MusicDirector(short_, 1));
    }

    [Fact]
    public void ThereAreNoAlarmTones()
    {
        // Criterion 2, measured on the rendered pieces of every mood: no high or short notes, no sudden
        // loudness, no clipping (B2466). The schema already allows only soft waves and slow tempi.
        foreach (var mood in Settings.Moods)
        {
            foreach (var seed in new ulong[] { 1, 2, 3 })
            {
                var notes = MusicComposer.Notes(mood, seed, 40);
                Assert.All(notes, n => Assert.InRange(n.Hz, 50, 1800));
                Assert.All(notes.Where(n => !n.Pad), n => Assert.True(n.DurationS >= 0.3, $"{mood.Id}: note of {n.DurationS:0.00} s"));

                var pcm = MusicComposer.Render(Settings, mood, seed, 40);
                var peak = pcm.Max(s => Math.Abs((int)s)) / (double)short.MaxValue;
                Assert.True(peak <= Settings.Peak + 0.01, $"{mood.Id}: peak {peak:0.00}");

                // Loudness in 50 ms windows never jumps up by more than a third of the peak.
                var window = (int)Settings.SampleRate / 20;
                var previous = 0.0;
                for (var start = 0; start + window <= pcm.Length; start += window)
                {
                    var rms = Math.Sqrt(pcm.Skip(start).Take(window).Average(s => (double)s * s)) / short.MaxValue;
                    Assert.True(rms - previous <= Settings.Peak / 3, $"{mood.Id}: loudness jumps at {start / (double)Settings.SampleRate:0.00} s");
                    previous = rms;
                }
            }
        }
    }

    [Fact]
    public void TheSamePieceSoundsTheSame()
    {
        var mood = Settings.Moods.First();
        Assert.Equal(MusicComposer.Render(Settings, mood, 7, 10), MusicComposer.Render(Settings, mood, 7, 10));
        Assert.NotEqual(MusicComposer.Notes(mood, 7, 30), MusicComposer.Notes(mood, 8, 30));
    }
}
