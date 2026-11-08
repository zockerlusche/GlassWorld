using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>Animal calls near the camera and songbird melodies (A1350, B8 R4, B2481).</summary>
public sealed class AnimalCallsTests
{
    private static readonly string Sound = Path.Combine(TestPaths.Root, "content", "sound");

    private static readonly SoundLibrary Library = SoundLibrary.FromJson(File.ReadAllText(Path.Combine(Sound, "sound_map.json")), File.ReadAllText(Path.Combine(Sound, "synth_recipes.json")));

    private static readonly AnimalSoundSettings Settings = ContractSerializer.Deserialize<AnimalSoundSettings>(File.ReadAllText(Path.Combine(Sound, "animals.json")));

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    private static readonly AnimalCalls Calls = new(Library, Settings);

    private static int TicksPerHour => Calendar.Settings.TicksPerHour;

    [Fact]
    public void CallsDoNotDependOnHowTimeIsSplitIntoFrames()
    {
        var day = Calendar.TicksPerDay;
        var whole = Day("meadow", 0, day);
        var frames = new List<AnimalCall>();
        for (long t = 0; t < day; t += 7)
        {
            frames.AddRange(Day("meadow", t, Math.Min(day, t + 7)));
        }

        Assert.NotEmpty(whole);
        Assert.Equal(whole, frames);
        Assert.Equal(whole, Day("meadow", 0, day));
        Assert.NotEqual(whole, Calls.Between(0, day, TicksPerHour, Time, "meadow", new Position(0, 0), 43));
    }

    [Fact]
    public void WhatCallsFollowsBiomeSeasonAndTimeOfDay()
    {
        // Spring day on the meadow: bees; spring night in the forest: owls; never a bee in winter.
        var spring = Calls.Between(0, Calendar.TicksPerDay, TicksPerHour, Time, "meadow", new Position(0, 0), 42);
        Assert.Contains(spring, c => c.Animal == "bee" && HourTime.Of(Calendar.At(c.Tick)).TimeOfDay == "day");
        Assert.All(spring.Where(c => c.Animal == "bee"), c => Assert.Equal("day", HourTime.Of(Calendar.At(c.Tick)).TimeOfDay));

        var forest = Calls.Between(0, Calendar.TicksPerDay, TicksPerHour, Time, "forest", new Position(0, 0), 42);
        Assert.All(forest.Where(c => c.Animal == "owl"), c => Assert.Equal("night", HourTime.Of(Calendar.At(c.Tick)).TimeOfDay));
        Assert.Contains(forest, c => c.Animal == "owl");

        var winterStart = Calendar.Settings.Seasons.TakeWhile(s => s.Id != "winter").Count() * Calendar.TicksPerSeason;
        var winter = Calls.Between(winterStart, winterStart + Calendar.TicksPerDay, TicksPerHour, Time, "meadow", new Position(0, 0), 42);
        Assert.Equal("winter", Calendar.At(winterStart).SeasonId);
        Assert.DoesNotContain(winter, c => c.Animal is "bee" or "butterfly" or "dragonfly");
        Assert.Empty(Calls.Between(0, Calendar.TicksPerDay, TicksPerHour, Time, null, new Position(0, 0), 42));
    }

    [Fact]
    public void CallsSoundNearTheListenerAndSongbirdsSing()
    {
        var listener = new Position(100, -40);
        var calls = Calls.Between(0, Calendar.TicksPerDay, TicksPerHour, Time, "forest", listener, 42);
        Assert.All(calls, c =>
        {
            var distance = Math.Sqrt(Math.Pow(c.Position.X - listener.X, 2) + Math.Pow(c.Position.Z - listener.Z, 2));
            Assert.InRange(distance, Settings.CallRadiusM.Min - 1e-9, Settings.CallRadiusM.Max + 1e-9);
        });
        Assert.All(calls, c => Assert.Equal(c.SoundId == Settings.Birdsong.Sound, c.Sung));
        Assert.Contains(calls, c => c.Sung);
    }

    [Fact]
    public void SongbirdMelodiesVaryButTheSameSeedSingsTheSameSong()
    {
        var a = Birdsong.Render(Settings.Birdsong, Library.SampleRate, 1);
        Assert.Equal(a, Birdsong.Render(Settings.Birdsong, Library.SampleRate, 1));
        var songs = Enumerable.Range(1, 20).Select(s => string.Join(",", Birdsong.Notes(Settings.Birdsong, (ulong)s).Select(n => Math.Round(n.StartHz)))).ToHashSet(StringComparer.Ordinal);
        Assert.True(songs.Count >= 15, $"{songs.Count} different songs of 20");

        foreach (var seed in Enumerable.Range(1, 20).Select(s => (ulong)s))
        {
            var notes = Birdsong.Notes(Settings.Birdsong, seed);
            Assert.InRange(notes.Count, Settings.Birdsong.NotesMin, Settings.Birdsong.NotesMax);
            Assert.All(notes, n => Assert.InRange(n.StartHz, Settings.Birdsong.BaseHzMin, Settings.Birdsong.BaseHzMax * Math.Pow(2, Settings.Birdsong.Steps.Max() / 12.0) + 1e-6));
        }

        // Calm: never above the data's peak.
        Assert.True(a.Max(x => Math.Abs((int)x)) / (double)short.MaxValue <= Settings.Birdsong.Peak + 0.01);
    }

    [Fact]
    public void AtFastForwardAtMostTheDataNumberOfCallsStartPerRealSecond()
    {
        // Review #91: x16 gave about 10 calls per second on the meadow; the budget keeps them apart.
        var budget = new CallBudget(Settings.MaxCallsPerSecond);
        var factor = Calendar.Settings.FastForwardFactors.Max();
        var secondsPerTick = Calendar.RealSecondsPerTickAtNormalSpeed / factor;
        var played = Calls.Between(0, Calendar.TicksPerDay, TicksPerHour, Time, "meadow", new Position(0, 0), 42)
            .Select(c => c.Tick * secondsPerTick)
            .Where(budget.TryTake)
            .ToList();
        Assert.NotEmpty(played);
        Assert.All(played.Zip(played.Skip(1)), p => Assert.True(p.Second - p.First >= (1 / Settings.MaxCallsPerSecond) - 1e-9));
        var seconds = Calendar.TicksPerDay * secondsPerTick;
        Assert.True(played.Count <= (seconds * Settings.MaxCallsPerSecond) + 1, $"{played.Count} calls in {seconds:0} s");
    }

    [Fact]
    public void AFractionOfCallsPerHourIsAChanceNotSilence()
    {
        // Review #91: 0.5 per hour gives a call in about half of the hours, never more than one.
        var map = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Sound, "sound_map.json")))!;
        foreach (var animal in map["animals"]!.AsArray())
        {
            animal!["per_hour"] = (string)animal["animal"]! == "owl" ? 0.5 : 0;
        }

        var library = SoundLibrary.FromJson(map.ToJsonString(), File.ReadAllText(Path.Combine(Sound, "synth_recipes.json")));
        var calls = new AnimalCalls(library, Settings);
        var perHour = Enumerable.Range(0, 400)
            .Select(h => calls.Between((long)h * TicksPerHour, (long)(h + 1) * TicksPerHour, TicksPerHour, _ => new HourTime("spring", "night"), "forest", new Position(0, 0), 42).Count)
            .ToList();
        Assert.All(perHour, n => Assert.InRange(n, 0, 1));
        Assert.InRange(perHour.Average(), 0.4, 0.6);
    }

    private static HourTime Time(long tick) => HourTime.Of(Calendar.At(tick));

    private static IReadOnlyList<AnimalCall> Day(string biome, long from, long to) =>
        Calls.Between(from, to, TicksPerHour, Time, biome, new Position(0, 0), 42);
}
