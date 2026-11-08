using System.Security.Cryptography;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>Murmur without words (A1351, B8 R5, F5243=B).</summary>
public sealed class MurmurTests
{
    private const string Sentence = "Ich habe am Bach einen glatten Stein gefunden, der ganz warm von der Sonne war.";

    private static readonly MurmurSettings Settings =
        ContractSerializer.Deserialize<MurmurSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "voice", "murmur.json")));

    private static readonly Murmur Voice = new(Settings);

    [Fact]
    public void TwoInhabitantsSoundDifferentInPitchAndTempo()
    {
        // Criterion 1: measured from the samples, not from the parameters.
        var (pitchA, secondsA) = Measure("inh-000001", "female");
        var (pitchB, secondsB) = Measure("inh-000002", "male");
        Assert.True(pitchA > pitchB * 1.3, $"pitch {pitchA:F0} Hz vs {pitchB:F0} Hz");

        Assert.NotEqual(secondsA, secondsB);

        // Same sex, other body values: measurably another voice (higher and faster when smaller and younger).
        var (pitchSmall, secondsSmall) = Measure("inh-000003", "female", new VoiceBody(0.3, 0.3));
        var (pitchLarge, secondsLarge) = Measure("inh-000003", "female", new VoiceBody(0.7, 0.7));
        Assert.True(pitchSmall > pitchLarge * 1.08, $"pitch {pitchSmall:F0} Hz vs {pitchLarge:F0} Hz");
        Assert.True(secondsSmall < secondsLarge * 0.95, $"length {secondsSmall:F2} s vs {secondsLarge:F2} s");

        // Until the core has body values, every inhabitant keeps its own placeholder body.
        Assert.NotEqual(Voice.PlaceholderBody("inh-000001"), Voice.PlaceholderBody("inh-000003"));
        Assert.Equal(Voice.PlaceholderBody("inh-000001"), new Murmur(Settings).PlaceholderBody("inh-000001"));
    }

    [Fact]
    public void ABodyValueChangesTheVoiceAsTheTableSays()
    {
        // Smaller and younger: higher and faster (the data's direction; values are placeholders).
        var small = Voice.VoiceOf("female", new VoiceBody(0.1, 0.1));
        var large = Voice.VoiceOf("female", new VoiceBody(0.9, 0.9));
        Assert.True(small.BaseHz > large.BaseHz);
        Assert.True(small.Tempo > large.Tempo);
        Assert.True(small.FormantShift > large.FormantShift);
        Assert.Equal(Settings.Voice.DefaultHz, Voice.VoiceOf("unknown", new VoiceBody(0.5, 0.5)).BaseHz, 6);
    }

    [Fact]
    public void TheSameSentenceAndInhabitantGiveTheSameBuffer()
    {
        // Criterion 2: a new instance, the same input -> the same hash.
        var voice = Voice.VoiceOf("female", Voice.PlaceholderBody("inh-000001"));
        var first = Hash(Voice.Render(Voice.Plan("inh-000001", voice, Sentence)));
        var again = new Murmur(Settings);
        var second = Hash(again.Render(again.Plan("inh-000001", again.VoiceOf("female", again.PlaceholderBody("inh-000001")), Sentence)));
        Assert.Equal(first, second);

        Assert.NotEqual(first, Hash(Voice.Render(Voice.Plan("inh-000001", voice, Sentence + " Ja."))));
        Assert.NotEqual(first, Hash(Voice.Render(Voice.Plan("inh-000004", voice, Sentence))));
    }

    [Fact]
    public void ThereAreNoWords()
    {
        // Criterion 3: only the length and the final mark of the text count; the blocks come from the
        // data and the seed, never from the letters.
        var voice = Voice.VoiceOf("male", new VoiceBody(0.5, 0.5));
        var vowelIds = Settings.Vowels.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var plan = Voice.Plan("inh-000002", voice, Sentence);
        Assert.All(plan.Blocks, b => Assert.Contains(b.VowelId, vowelIds));

        // Same length, other letters: same number of blocks, same melody kind.
        var other = Voice.Plan("inh-000002", voice, new string('x', Sentence.Length - 1) + ".");
        Assert.Equal(plan.Blocks.Count, other.Blocks.Count);
        Assert.Equal(plan.Kind, other.Kind);

        // A text of a single letter does not give a single sound: the letters are not read.
        var monotone = Voice.Plan("inh-000002", voice, new string('a', 40));
        Assert.True(monotone.Blocks.Select(b => b.VowelId).Distinct().Count() > 1);

        // The same sentence in German and English sounds alike in kind and about in length, not in "words".
        var de = Voice.Plan("inh-000002", voice, "Wo ist das Wasser?");
        var en = Voice.Plan("inh-000002", voice, "Where is the water?");
        Assert.Equal(MurmurMelodyKind.Question, de.Kind);
        Assert.Equal(MurmurMelodyKind.Question, en.Kind);
        Assert.InRange(Math.Abs(de.Blocks.Count - en.Blocks.Count), 0, 1);
    }

    [Theory]
    [InlineData("Wo ist das Wasser?", MurmurMelodyKind.Question)]
    [InlineData("Schau!  ", MurmurMelodyKind.Exclamation)]
    [InlineData("Es ist ruhig.", MurmurMelodyKind.Calm)]
    [InlineData("", MurmurMelodyKind.Calm)]
    public void TheFinalMarkGivesTheMelody(string text, MurmurMelodyKind kind) => Assert.Equal(kind, Voice.KindOf(text));

    [Fact]
    public void AQuestionRisesAndAnExclamationStressesTheStart()
    {
        var voice = Voice.VoiceOf("female", new VoiceBody(0.5, 0.5));
        var question = Voice.Plan("inh-000001", voice, "Kommst du morgen mit zum Strand?");
        Assert.True(question.Blocks[^1].EndHz > question.Blocks[0].StartHz);
        var calm = Voice.Plan("inh-000001", voice, "Morgen gehe ich zum Strand.");
        Assert.True(calm.Blocks[^1].EndHz <= calm.Blocks[0].StartHz);
        var exclamation = Voice.Plan("inh-000001", voice, "Morgen gehe ich zum Strand!");
        Assert.True(exclamation.Blocks[0].Gain > exclamation.Blocks[1].Gain);
    }

    [Fact]
    public void TheMurmurIsCalm()
    {
        // B2466: normalised to the data's peak, no clipping, short sentences stay short.
        var voice = Voice.VoiceOf("female", Voice.PlaceholderBody("inh-000001"));
        var pcm = Voice.Render(Voice.Plan("inh-000001", voice, Sentence));
        var peak = pcm.Max(s => Math.Abs((int)s)) / (double)short.MaxValue;
        Assert.True(peak <= Settings.Peak + 0.01, $"peak {peak:0.00}");

        // Same level as the library one-shots (review #90): on the loudness target unless the peak limit caps it.
        var loudness = Pcm.LoudnessDbfs(new MonoSound([.. pcm.Select(s => s / 32768f)], Voice.SampleRate), Settings.GateDbfs);
        Assert.True(loudness <= Settings.LoudnessDbfs + 0.5, $"loudness {loudness:0.0} dBFS");
        Assert.True(loudness >= Settings.LoudnessDbfs - 0.5 || peak >= Settings.Peak - 0.01, $"loudness {loudness:0.0} dBFS, peak {peak:0.00}");
        var max = (Settings.Length.MaxSyllables * ((Settings.Syllable.DurationS * 1.5 / 0.3) + Settings.Consonant.DurationS + Settings.Syllable.PauseS)) + 0.1;
        Assert.True(pcm.Length / (double)Voice.SampleRate < max);
    }

    private static (double PitchHz, double Seconds) Measure(string id, string sex, VoiceBody? body = null)
    {
        var voice = Voice.VoiceOf(sex, body ?? Voice.PlaceholderBody(id));
        var pcm = Voice.Render(Voice.Plan(id, voice, Sentence));
        return (Pitch(pcm, Voice.SampleRate), pcm.Length / (double)Voice.SampleRate);
    }

    /// <summary>Base pitch by autocorrelation over the whole murmur (50–600 Hz).</summary>
    private static double Pitch(short[] pcm, int rate)
    {
        var x = pcm.Select(s => s / 32768.0).ToArray();
        var best = 0.0;
        var bestLag = 0;
        for (var lag = rate / 600; lag <= rate / 50; lag++)
        {
            var sum = 0.0;
            for (var i = 0; i + lag < x.Length; i += 2)
            {
                sum += x[i] * x[i + lag];
            }

            if (sum > best)
            {
                best = sum;
                bestLag = lag;
            }
        }

        return bestLag == 0 ? 0 : (double)rate / bestLag;
    }

    private static string Hash(short[] pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
