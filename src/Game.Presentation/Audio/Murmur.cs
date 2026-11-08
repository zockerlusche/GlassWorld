using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>Body values that shape a voice, each 0..1 (B8 R5; from the core once it has them, B9).</summary>
/// <param name="Size">Body size (0 small … 1 large).</param>
/// <param name="Age">Age (0 young … 1 old).</param>
public sealed record VoiceBody(double Size, double Age);

/// <summary>A voice as the murmur uses it.</summary>
/// <param name="BaseHz">Base pitch.</param>
/// <param name="Tempo">Speaking tempo factor (1 = the data's block duration).</param>
/// <param name="FormantShift">Formant factor (above 1 = brighter, smaller body).</param>
public sealed record VoiceProfile(double BaseHz, double Tempo, double FormantShift);

/// <summary>One sound block of a murmur.</summary>
/// <param name="VowelId">Vowel block id from the data (never a letter of the text).</param>
/// <param name="F1Hz">First formant after the voice's shift.</param>
/// <param name="F2Hz">Second formant after the voice's shift.</param>
/// <param name="Consonant">Whether a soft noise comes before it.</param>
/// <param name="DurationS">Voiced duration.</param>
/// <param name="StartHz">Pitch at its start.</param>
/// <param name="EndHz">Pitch at its end.</param>
/// <param name="Gain">Loudness factor (stress).</param>
public sealed record MurmurBlock(string VowelId, double F1Hz, double F2Hz, bool Consonant, double DurationS, double StartHz, double EndHz, double Gain);

/// <summary>What a sentence sounds like before rendering.</summary>
/// <param name="Kind">Sentence kind from the final mark.</param>
/// <param name="Blocks">Blocks in order.</param>
public sealed record MurmurPlan(MurmurMelodyKind Kind, IReadOnlyList<MurmurBlock> Blocks);

/// <summary>
/// Murmur (B8 R5, F5243=B): inhabitants speak audibly only a murmur without words, no TTS. Only the
/// length of the bubble text and its final mark (question, exclamation, calm) shape the sound; the
/// blocks come from the data and a seed of inhabitant id and sentence, never from the letters. The
/// same sentence and inhabitant always give the same samples. Voice from body values through the data
/// table; the text stays readable in the bubble (DE/EN, B6).
/// </summary>
public sealed class Murmur(MurmurSettings settings)
{
    private readonly MurmurSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Sample rate of rendered murmurs.</summary>
    public int SampleRate => (int)_settings.SampleRate;

    /// <summary>
    /// Placeholder body values until the core has them (B9, GW1 #18477): drawn from the inhabitant id
    /// within the data's ranges, so every inhabitant keeps one voice. Children will inherit only once
    /// the values come from the core.
    /// </summary>
    public VoiceBody PlaceholderBody(string inhabitantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(inhabitantId);
        var random = new SplitMix64(SplitMix64.SeedOf("voice-body|" + inhabitantId));
        var range = _settings.PlaceholderBody;
        return new VoiceBody(Between(range.SizeMin, range.SizeMax, random), Between(range.AgeMin, range.AgeMax, random));
    }

    /// <summary>The voice for a sex (vocabulary id) and body values (data table).</summary>
    public VoiceProfile VoiceOf(string sex, VoiceBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var v = _settings.Voice;
        var baseHz = v.BaseHz.FirstOrDefault(b => b.Sex == sex)?.Hz ?? v.DefaultHz;
        var size = 0.5 - Math.Clamp(body.Size, 0, 1);
        var age = 0.5 - Math.Clamp(body.Age, 0, 1);
        return new VoiceProfile(
            baseHz * Math.Max(0.3, 1 + (v.SizePitch * size) + (v.AgePitch * age)),
            Math.Max(0.3, 1 + (v.SizeTempo * size) + (v.AgeTempo * age)),
            Math.Max(0.5, 1 + (v.SizeFormant * size)));
    }

    /// <summary>
    /// The recorded voice for a sentence (listening test 3, W1664), or null when the data has none that
    /// fits: child below the data's age, otherwise by sex; high voice below the data's size. Questions use
    /// the question recording, everything else the calm one. Which of the recordings of that sound plays
    /// follows the inhabitant and the sentence, so a sentence always sounds the same.
    /// </summary>
    public (string SoundId, int Variant)? SampleFor(string inhabitantId, string sex, VoiceBody body, string text)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_settings.Samples is not { } samples)
        {
            return null;
        }

        var child = body.Age < samples.ChildAgeMax;
        var high = body.Size < samples.HighSizeMax;
        var voice = samples.Voices.FirstOrDefault(v => v.Child == child && v.High == high && (child || v.Sex == sex));
        if (voice is null)
        {
            return null;
        }

        var sound = KindOf(text) == MurmurMelodyKind.Question ? voice.Question : voice.Calm;
        var variant = (int)(SplitMix64.SeedOf($"murmur-sample|{inhabitantId}|{text}") % 1024);
        return (sound, variant);
    }

    /// <summary>Sentence kind from the last visible character (language-neutral marks from the data).</summary>
    public MurmurMelodyKind KindOf(string text)
    {
        var last = (text ?? string.Empty).TrimEnd().LastOrDefault().ToString();
        return _settings.QuestionMarks.Contains(last, StringComparer.Ordinal) ? MurmurMelodyKind.Question
            : _settings.ExclamationMarks.Contains(last, StringComparer.Ordinal) ? MurmurMelodyKind.Exclamation
            : MurmurMelodyKind.Calm;
    }

    /// <summary>The blocks of a sentence for an inhabitant's voice.</summary>
    public MurmurPlan Plan(string inhabitantId, VoiceProfile voice, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(inhabitantId);
        ArgumentNullException.ThrowIfNull(voice);
        text ??= string.Empty;
        var kind = KindOf(text);
        var melody = _settings.Melodies.First(m => m.Kind == kind);
        var length = _settings.Length;
        var count = Math.Clamp((int)Math.Round(text.Trim().Length / length.CharsPerSyllable), length.MinSyllables, Math.Max(length.MinSyllables, length.MaxSyllables));

        // Seed from inhabitant and the whole sentence: the text decides only *which* murmur, not its sounds.
        var random = new SplitMix64(SplitMix64.SeedOf(inhabitantId + "\n" + text));
        var vowels = _settings.Vowels.ToList();
        var blocks = new List<MurmurBlock>(count);
        for (var i = 0; i < count; i++)
        {
            var vowel = vowels[(int)(random.Next() % (ulong)vowels.Count)];
            var consonant = (random.NextSigned() + 1) / 2 < _settings.Consonant.Chance;
            var duration = _settings.Syllable.DurationS / voice.Tempo * (1 + (_settings.Syllable.Variation * random.NextSigned()));
            var accent = i == 0 ? melody.Accent : 0;
            blocks.Add(new MurmurBlock(
                vowel.Id,
                vowel.F1Hz * voice.FormantShift,
                vowel.F2Hz * voice.FormantShift,
                consonant,
                duration,
                voice.BaseHz * (Contour(melody, i, count) + accent),
                voice.BaseHz * Contour(melody, i + 1, count),
                1 + (i == 0 ? melody.AccentGain : 0)));
        }

        return new MurmurPlan(kind, blocks);
    }

    /// <summary>
    /// Renders a plan into 16-bit mono PCM (same plan, same samples), at the data's loudness like the
    /// library one-shots, never above the data's peak (review #90).
    /// </summary>
    public short[] Render(MurmurPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var rate = SampleRate;
        var s = _settings.Syllable;
        var c = _settings.Consonant;
        var total = plan.Blocks.Sum(b => (b.Consonant ? c.DurationS : 0) + b.DurationS + s.PauseS);
        var mix = new double[Math.Max(1, (int)Math.Ceiling(total * rate))];
        var noise = new SplitMix64(SplitMix64.SeedOf("murmur-noise|" + plan.Blocks.Count));
        var position = 0;
        var phase = 0.0;
        foreach (var block in plan.Blocks)
        {
            if (block.Consonant)
            {
                position = Consonant(mix, position, rate, noise);
            }

            position = Vowel(mix, position, rate, block, ref phase);
            position += (int)Math.Round(s.PauseS * rate);
        }

        var loudest = mix.Max(Math.Abs);
        var loudness = Pcm.LoudnessDbfs(new MonoSound([.. mix.Select(x => (float)x)], rate), _settings.GateDbfs);
        var toLoudness = double.IsFinite(loudness) ? Math.Pow(10, (_settings.LoudnessDbfs - loudness) / 20) : 0;
        var scale = loudest > 0 ? Math.Min(toLoudness, _settings.Peak / loudest) : 0;
        var pcm = new short[mix.Length];
        for (var i = 0; i < mix.Length; i++)
        {
            pcm[i] = (short)Math.Round(Math.Clamp(mix[i] * scale, -1, 1) * short.MaxValue);
        }

        return pcm;
    }

    private static double Between(double min, double max, SplitMix64 random) =>
        min + ((Math.Max(min, max) - min) * (random.NextSigned() + 1) / 2);

    private static double Contour(MurmurMelody melody, int index, int count) =>
        count <= 1 ? melody.Start : melody.Start + ((melody.End - melody.Start) * Math.Min(1.0, (double)index / (count - 1)));

    private int Consonant(double[] mix, int start, int rate, SplitMix64 noise)
    {
        var c = _settings.Consonant;
        var length = (int)Math.Round(c.DurationS * rate);
        var alpha = 1 - Math.Exp(-2 * Math.PI * c.LowpassHz / rate);
        var low = 0.0;
        for (var i = 0; i < length && start + i < mix.Length; i++)
        {
            low += alpha * (noise.NextSigned() - low);
            var edge = Math.Sin(Math.PI * i / length);
            mix[start + i] += low * edge * c.Gain;
        }

        return start + length;
    }

    private int Vowel(double[] mix, int start, int rate, MurmurBlock block, ref double phase)
    {
        var s = _settings.Syllable;
        var length = Math.Max(1, (int)Math.Round(block.DurationS * rate));
        var f1 = new Resonator(block.F1Hz, _settings.FormantBandwidthHz, rate);
        var f2 = new Resonator(block.F2Hz, _settings.FormantBandwidthHz, rate);
        for (var i = 0; i < length && start + i < mix.Length; i++)
        {
            var t = (double)i / rate;
            var progress = (double)i / length;
            var frequency = block.StartHz + ((block.EndHz - block.StartHz) * progress);
            phase += frequency / rate;
            phase -= Math.Floor(phase);
            var source = (2 * phase) - 1;
            var voiced = f1.Next(source) + (0.6 * f2.Next(source));
            var attack = Math.Min(1, t / s.AttackS);
            var release = Math.Min(1, (block.DurationS - t) / s.ReleaseS);
            mix[start + i] += voiced * Math.Max(0, Math.Min(attack, release)) * block.Gain;
        }

        return start + length;
    }

    /// <summary>Two-pole band-pass at a formant.</summary>
    private sealed class Resonator(double hz, double bandwidth, int rate)
    {
        private readonly double _r = Math.Exp(-Math.PI * bandwidth / rate);
        private readonly double _a1 = 2 * Math.Exp(-Math.PI * bandwidth / rate) * Math.Cos(2 * Math.PI * Math.Min(hz, (rate / 2.0) - 1) / rate);
        private double _y1;
        private double _y2;

        public double Next(double x)
        {
            var y = ((1 - _r) * x) + (_a1 * _y1) - (_r * _r * _y2);
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
