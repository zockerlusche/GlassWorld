using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Presentation.Audio;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Spatial sound (B2512, Spec 33 acceptance 8): a source gets quieter with distance, is silent beyond
/// its range, and zoom changes the hearing range; the voice budget keeps the nearest sounds.
/// </summary>
public sealed class MixTests
{
    private static readonly string MixJson = File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "mix.json"));
    private static readonly SoundMixer Mixer = SoundMixer.FromJson(MixJson);
    private static readonly SoundLibrary Library = SoundLibrary.FromJson(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "sound_map.json")),
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "synth_recipes.json")));

    public static TheoryData<MixBusId> SpatialBuses => [MixBusId.Ambience, MixBusId.Animals, MixBusId.Voices, MixBusId.Effects];

    [Theory]
    [MemberData(nameof(SpatialBuses))]
    public void SourceGetsQuieterWithDistanceAndIsSilentBeyondItsRange(MixBusId bus)
    {
        var silent = Mixer.SilentDistance(bus, 0.5);
        var previous = 1.0;
        for (var d = 0.0; d <= silent * 1.5; d += silent / 50)
        {
            var gain = Mixer.Gain(bus, d, 0.5);
            Assert.True(gain <= previous + 1e-12, $"{bus}: louder at {d:0.0} m");
            previous = gain;
        }

        Assert.Equal(1, Mixer.Gain(bus, 0, 0.5));
        Assert.Equal(0, Mixer.Gain(bus, silent, 0.5));
        Assert.Equal(0, Mixer.Gain(bus, silent * 3, 0.5));
        Assert.Equal(SoundMixer.SilentDb, SoundMixer.ToDb(Mixer.Gain(bus, silent * 3, 0.5)));
    }

    [Theory]
    [MemberData(nameof(SpatialBuses))]
    public void ZoomingOutWidensTheHearingRange(MixBusId bus)
    {
        var nearRange = Mixer.SilentDistance(bus, 0);
        var farRange = Mixer.SilentDistance(bus, 1);
        Assert.True(farRange > nearRange);
        var d = (nearRange + farRange) / 2;
        Assert.Equal(0, Mixer.Gain(bus, d, 0));
        Assert.True(Mixer.Gain(bus, d, 1) > 0);
    }

    [Theory]
    [InlineData(MixBusId.Music)]
    [InlineData(MixBusId.Master)]
    public void NonSpatialBusesIgnoreDistance(MixBusId bus) => Assert.Equal(1, Mixer.Gain(bus, 10_000, 0.5));

    [Fact]
    public void MusicAmbienceAndAnimalCallsAreOffUntilReplaced()
    {
        // W1699: music, ambience and animal calls are off until replaced; voices, effects and the master stay.
        var buses = JsonNode.Parse(MixJson)!["buses"]!.AsArray();
        var off = buses.Where(b => (bool?)b!["off"] == true).Select(b => (string)b!["id"]!).ToList();
        Assert.Equal(["music", "ambience", "animals"], off);
    }

    [Fact]
    public void BrokenRangeIsRejected()
    {
        var mix = JsonNode.Parse(MixJson)!;
        mix["buses"]!.AsArray().First(b => (string)b!["id"]! == "effects")!["silent_m"] = 1;
        Assert.Throws<InvalidDataException>(() => SoundMixer.FromJson(mix.ToJsonString()));
    }

    [Fact]
    public void DirectorPlaysActionStartsAndSkipsInaudibleOnes()
    {
        var director = new SoundDirector(Library, Mixer);
        var positions = new Dictionary<string, Position>(StringComparer.Ordinal) { ["inh-1"] = new(1, 0), ["inh-2"] = new(5000, 0) };
        var events = new[]
        {
            new WorldEvent(InhabitantsSystem.ActionStarted, 10, "inh-1", "eat", null),
            new WorldEvent(InhabitantsSystem.ActionStarted, 10, "inh-2", "eat", null),
            new WorldEvent(InhabitantsSystem.ActionFinished, 10, "inh-1", "eat", null),
        };
        var plan = director.Plan(events, positions, new Position(0, 0), 0.5, new Dictionary<MixBusId, int>());
        var request = Assert.Single(plan);
        Assert.Equal("action.eat", request.SoundId);
        Assert.Equal(new Position(1, 0), request.Position);
    }

    [Fact]
    public void VoiceBudgetKeepsTheNearest()
    {
        var director = new SoundDirector(Library, Mixer);
        var budget = Mixer.Bus(MixBusId.Effects).MaxVoices;
        var positions = Enumerable.Range(0, budget + 10).ToDictionary(i => $"inh-{i}", i => new Position(i * 0.5, 0), StringComparer.Ordinal);
        var events = positions.Keys.Select(id => new WorldEvent(InhabitantsSystem.ActionStarted, 5, id, "drink", null));
        var plan = director.Plan(events, positions, new Position(0, 0), 0.5, new Dictionary<MixBusId, int> { [MixBusId.Effects] = 2 });
        Assert.Equal(budget - 2, plan.Count);
        Assert.True(plan.Max(p => p.Position.X) < positions.Values.Max(p => p.X));
    }

    [Fact]
    public void WhileFollowingNothingBeyondThePicturesReachIsHeard()
    {
        // B2605, B8 R7: the camera follows an inhabitant; what lies outside the picture is silent.
        var mixer = SoundMixer.FromJson(MixJson);
        var silent = mixer.SilentDistance(MixBusId.Ambience, 0.5);
        var limit = silent / 4;
        Assert.True(mixer.Gain(MixBusId.Ambience, limit * 2, 0.5) > 0);

        mixer.HearingLimitM = limit;

        Assert.Equal(0, mixer.Gain(MixBusId.Ambience, limit * 2, 0.5));
        Assert.Equal(0, mixer.Gain(MixBusId.Ambience, limit, 0.5));
        Assert.True(mixer.Gain(MixBusId.Ambience, limit / 10, 0.5) > 0);
        Assert.Equal(limit, mixer.SilentDistance(MixBusId.Ambience, 0.5), 6);

        // A limit beyond the bus's own range changes nothing; without one the range is the bus's again.
        mixer.HearingLimitM = silent * 3;
        Assert.Equal(silent, mixer.SilentDistance(MixBusId.Ambience, 0.5), 6);
        mixer.HearingLimitM = null;
        Assert.True(mixer.Gain(MixBusId.Ambience, limit * 2, 0.5) > 0);
    }

    [Fact]
    public void VariantStaysWithinTheRecipeVariants()
    {
        var director = new SoundDirector(Library, Mixer);
        var positions = new Dictionary<string, Position>(StringComparer.Ordinal) { ["inh-1"] = new(1, 0) };
        var variants = Library.Recipe("action.drink").Variants;
        var seen = Enumerable.Range(0, 200)
            .Select(t => director.Plan([new WorldEvent(InhabitantsSystem.ActionStarted, t, "inh-1", "drink", null)], positions, new Position(0, 0), 0.5, new Dictionary<MixBusId, int>())[0].Variant)
            .ToHashSet();
        Assert.All(seen, v => Assert.InRange(v, 0, variants - 1));
        Assert.Equal(variants, seen.Count);
    }

    [Fact]
    public void SameEventGivesTheSameVariant()
    {
        var director = new SoundDirector(Library, Mixer);
        var positions = new Dictionary<string, Position>(StringComparer.Ordinal) { ["inh-1"] = new(1, 0) };
        var e = new[] { new WorldEvent(InhabitantsSystem.ActionStarted, 42, "inh-1", "drink", null) };
        var none = new Dictionary<MixBusId, int>();
        Assert.Equal(director.Plan(e, positions, new Position(0, 0), 0.5, none)[0].Variant, director.Plan(e, positions, new Position(0, 0), 0.5, none)[0].Variant);
    }
}
