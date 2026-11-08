using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The sounds Marcel chose in listening tests 2-4 (W1651, W1662, W1664, W1665): recorded murmur voices,
/// recorded birdsong next to the melodies, and music variant B next to A.
/// </summary>
public sealed class ChosenSoundsTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    private static readonly MurmurSettings MurmurData = ContractSerializer.Deserialize<MurmurSettings>(File.ReadAllText(Path.Combine(Content, "voice", "murmur.json")));

    private static readonly MusicSettings MusicData = ContractSerializer.Deserialize<MusicSettings>(File.ReadAllText(Path.Combine(Content, "music", "music.json")));

    private static readonly BirdsongSettings BirdsongData = ContractSerializer.Deserialize<AnimalSoundSettings>(File.ReadAllText(Path.Combine(Content, "sound", "animals.json"))).Birdsong;

    private static readonly BaseSoundLibrary Library = ContractSerializer.Deserialize<BaseSoundLibrary>(File.ReadAllText(Path.Combine(Content, "sound", "library.json")));

    [Theory]
    [InlineData(0.1, 0.3, "female", "voice.child_high.calm")]
    [InlineData(0.1, 0.7, "male", "voice.child_low.calm")]
    [InlineData(0.5, 0.3, "female", "voice.female_high.calm")]
    [InlineData(0.5, 0.7, "female", "voice.female_low.calm")]
    [InlineData(0.5, 0.3, "male", "voice.male_high.calm")]
    [InlineData(0.5, 0.7, "male", "voice.male_low.calm")]
    public void TheVoiceFollowsAgeSexAndSize(double age, double size, string sex, string expected)
    {
        var voice = new Murmur(MurmurData);
        Assert.Equal(expected, voice.SampleFor("inh-000001", sex, new VoiceBody(size, age), "Hallo.")!.Value.SoundId);
        Assert.Equal(expected.Replace(".calm", ".question", StringComparison.Ordinal), voice.SampleFor("inh-000001", sex, new VoiceBody(size, age), "Hallo?")!.Value.SoundId);
    }

    [Fact]
    public void ASentenceAlwaysSoundsTheSameAndEveryVoiceHasItsRecordings()
    {
        var voice = new Murmur(MurmurData);
        var body = new VoiceBody(0.4, 0.5);
        Assert.Equal(voice.SampleFor("inh-000002", "male", body, "Wasser!"), voice.SampleFor("inh-000002", "male", body, "Wasser!"));

        var ids = Library.Sounds.ToDictionary(s => s.Id, s => s.Files.Count, StringComparer.Ordinal);
        foreach (var v in MurmurData.Samples!.Voices)
        {
            Assert.True(ids.GetValueOrDefault(v.Calm) >= 1, $"{v.Calm} not in the library");
            Assert.True(ids.GetValueOrDefault(v.Question) >= 1, $"{v.Question} not in the library");
        }

        // Without recorded voices in the data the formant murmur speaks.
        var withoutSamples = ContractSerializer.Deserialize<MurmurSettings>(File.ReadAllText(Path.Combine(Content, "voice", "murmur.json")));
        withoutSamples.Samples = null;
        Assert.Null(new Murmur(withoutSamples).SampleFor("inh-000001", "female", body, "Hallo."));
    }

    [Fact]
    public void SongbirdsSingMelodiesAndRecordingsByTheDataShare()
    {
        var recorded = Enumerable.Range(0, 4000).Count(i => Birdsong.Recorded(BirdsongData, (ulong)i * 7919));
        Assert.InRange(recorded / 4000.0, BirdsongData.RecordedShare - 0.05, BirdsongData.RecordedShare + 0.05);
        Assert.True(Library.Sounds.Single(s => s.Id == "animal.songbird.song").Files.Count >= 4);

        var none = ContractSerializer.Deserialize<AnimalSoundSettings>(File.ReadAllText(Path.Combine(Content, "sound", "animals.json"))).Birdsong;
        none.RecordedShare = 0;
        Assert.DoesNotContain(Enumerable.Range(0, 500), i => Birdsong.Recorded(none, (ulong)i));
    }

    [Fact]
    public void EachMoodPlaysBothItsVariantsOverTime()
    {
        // Music A and B (W1651): pieces of one mood alternate between the mood and its alternate.
        var director = new MusicDirector(MusicData, 7);
        var moods = new HashSet<string>(StringComparer.Ordinal);
        var now = 0.0;
        for (var i = 0; i < 400 && moods.Count < 2; i++, now += 30)
        {
            if (director.Update(now, "day").Start is { } cue)
            {
                moods.Add(cue.Variant);
            }
        }

        Assert.Equal(["day", "day_b"], moods.Order(StringComparer.Ordinal));
        Assert.All(MusicData.Moods.Where(m => !m.Id.EndsWith("_b", StringComparison.Ordinal)), m => Assert.Equal([m.Id + "_b"], m.Alternates!));
    }

    [Fact]
    public void AnAlternateMustBeAnotherKnownMood()
    {
        var broken = ContractSerializer.Deserialize<MusicSettings>(File.ReadAllText(Path.Combine(Content, "music", "music.json")));
        broken.Moods.First().Alternates = ["no_such_mood"];
        Assert.Throws<InvalidDataException>(() => new MusicDirector(broken, 1));
    }
}
