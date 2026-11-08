using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>One note of a piece.</summary>
/// <param name="StartS">Start in the piece.</param>
/// <param name="DurationS">Length, release included.</param>
/// <param name="Hz">Pitch.</param>
/// <param name="Pad">True for a held chord tone, false for the melody.</param>
public sealed record MusicNote(double StartS, double DurationS, double Hz, bool Pad);

/// <summary>A piece to start.</summary>
/// <param name="Mood">Mood id.</param>
/// <param name="Seed">Seed of the piece (same seed, same piece).</param>
/// <param name="Seconds">Length of the piece.</param>
/// <param name="FadeInS">How long it fades in (cross-fade when the mood changes).</param>
public sealed record MusicCue(string Mood, ulong Seed, double Seconds, double FadeInS);

/// <summary>What the music player does now.</summary>
/// <param name="Start">A piece to start, or null.</param>
/// <param name="FadeOutS">Fade the playing piece out over this time (0 = leave it).</param>
public sealed record MusicStep(MusicCue? Start, double FadeOutS);

/// <summary>
/// Generative calm music (B8 R6, B2466): a piece of a mood is a soft held chord progression and a slow
/// melody from the mood's scale, both from a seed. The schema keeps every mood calm (soft waves, slow
/// tempo, long notes, gentle onsets).
/// </summary>
public static class MusicComposer
{
    /// <summary>The notes of a piece.</summary>
    public static IReadOnlyList<MusicNote> Notes(MusicMood mood, ulong seed, double seconds)
    {
        ArgumentNullException.ThrowIfNull(mood);
        var random = new SplitMix64(seed);
        var beat = 60.0 / mood.TempoBpm;
        var scale = mood.Scale.ToList();
        var notes = new List<MusicNote>();

        // Held chords on scale degrees, every few bars.
        var chordLength = mood.Pad.Bars * 4 * beat;
        for (var t = 0.0; t < seconds; t += chordLength)
        {
            var degree = scale[(int)(random.Next() % (ulong)scale.Count)] % 12;
            var length = Math.Min(chordLength + mood.Pad.ReleaseS, seconds - t);
            notes.AddRange(mood.Pad.Chord.Select(step => new MusicNote(t, length, mood.RootHz * Math.Pow(2, (degree + step) / 12.0), true)));
        }

        // A slow melody: small steps through the scale, with rests.
        var index = scale.Count / 2;
        var octave = Math.Pow(2, mood.Melody.Octave);
        var beats = mood.NoteBeats.ToList();
        for (var t = 0.0; t < seconds;)
        {
            var duration = beats[(int)(random.Next() % (ulong)beats.Count)] * beat;
            var rest = (random.NextSigned() + 1) / 2 < mood.RestChance;
            index = Math.Clamp(index + (int)(random.Next() % 5) - 2, 0, scale.Count - 1);
            if (!rest && t + duration <= seconds)
            {
                notes.Add(new MusicNote(t, duration, mood.RootHz * octave * Math.Pow(2, scale[index] / 12.0), false));
            }

            t += duration;
        }

        return notes;
    }

    /// <summary>Renders a piece into 16-bit mono PCM, normalised to the data's peak.</summary>
    public static short[] Render(MusicSettings settings, MusicMood mood, ulong seed, double seconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mood);
        var rate = (int)settings.SampleRate;
        var mix = new double[Math.Max(1, (int)Math.Ceiling(seconds * rate))];
        foreach (var note in Notes(mood, seed, seconds))
        {
            var voice = note.Pad
                ? (Soft: mood.Pad.Wave == MusicPadWave.Triangle, mood.Pad.Gain, mood.Pad.AttackS, mood.Pad.ReleaseS)
                : (Soft: mood.Melody.Wave == MusicVoiceWave.Triangle, mood.Melody.Gain, mood.Melody.AttackS, mood.Melody.ReleaseS);
            var start = (int)Math.Round(note.StartS * rate);
            var length = (int)Math.Round(note.DurationS * rate);
            var phase = 0.0;
            for (var i = 0; i < length && start + i < mix.Length; i++)
            {
                var t = (double)i / rate;
                phase += note.Hz / rate;
                phase -= Math.Floor(phase);
                var wave = voice.Soft ? 1 - (4 * Math.Abs(phase - 0.5)) : Math.Sin(2 * Math.PI * phase);
                var envelope = Math.Min(1, t / voice.AttackS) * Math.Clamp((note.DurationS - t) / voice.ReleaseS, 0, 1);
                mix[start + i] += wave * envelope * voice.Gain;
            }
        }

        // Soft edges of the whole piece (one second), so it never starts or stops with a click.
        var edge = Math.Min(rate, mix.Length / 2);
        for (var i = 0; i < edge; i++)
        {
            var f = (double)i / edge;
            mix[i] *= f;
            mix[^(i + 1)] *= f;
        }

        var loudest = mix.Max(Math.Abs);
        var scale = loudest > 0 ? settings.Peak / loudest : 0;
        var pcm = new short[mix.Length];
        for (var i = 0; i < mix.Length; i++)
        {
            pcm[i] = (short)Math.Round(Math.Clamp(mix[i] * scale, -1, 1) * short.MaxValue);
        }

        return pcm;
    }
}

/// <summary>
/// Which music plays (B8 R6): the mood follows time of day, season and epoch by the data's rules; a
/// change of mood cross-fades over the data's fade time; between two pieces of one mood comes a pause.
/// No time pressure, no alarm (B2466). Driven with any clock (real seconds in the game, virtual in tests).
/// </summary>
public sealed class MusicDirector
{
    private readonly MusicSettings _settings;
    private readonly ulong _worldSeed;
    private double _pieceEnds;
    private double _moodSince;
    private double? _pauseEnds;
    private long _counter;

    /// <summary>Creates the director; throws <see cref="InvalidDataException"/> for inconsistent data.</summary>
    public MusicDirector(MusicSettings settings, ulong worldSeed)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _worldSeed = worldSeed;
        var moods = settings.Moods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var problems = settings.Rules.Where(r => !moods.Contains(r.Mood)).Select(r => $"rule names unknown mood '{r.Mood}'").ToList();
        if (settings.MinMoodS < settings.FadeS)
        {
            problems.Add($"min_mood_s {settings.MinMoodS} is shorter than fade_s {settings.FadeS}");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Music: " + string.Join("; ", problems) + ".");
        }
    }

    /// <summary>The mood playing now (null before the first piece).</summary>
    public string? Current { get; private set; }

    /// <summary>The mood for a time of day, season and epoch: the first rule that matches.</summary>
    public string MoodFor(string timeOfDay, string season, string epoch)
    {
        static bool Matches(ICollection<string> list, string value) => list.Count == 0 || list.Contains(value);
        var rule = _settings.Rules.FirstOrDefault(r => Matches(r.TimesOfDay, timeOfDay) && Matches(r.Seasons, season) && Matches(r.Epochs, epoch));
        return rule?.Mood ?? _settings.Moods.First().Id;
    }

    /// <summary>The mood at a world time (phase as its time_of_day id) in an epoch.</summary>
    public string MoodAt(Simulation.Time.WorldTime time, string epoch) => MoodFor(
        time.Phase switch
        {
            Simulation.Time.DayPhase.Dawn => "dawn",
            Simulation.Time.DayPhase.Day => "day",
            Simulation.Time.DayPhase.Dusk => "dusk",
            _ => "night",
        },
        time.SeasonId,
        epoch);

    /// <summary>The mood's data.</summary>
    public MusicMood Mood(string id) => _settings.Moods.First(m => m.Id == id);

    /// <summary>Advances to <paramref name="now"/> (seconds) with the mood that should play.</summary>
    public MusicStep Update(double now, string wanted)
    {
        // A mood keeps playing for at least min_mood_s (>= fade_s): a short twilight at fast forward never
        // cuts a piece off mid-fade, and the music does not hop back and forth (review #92).
        if (Current is null || (wanted != Current && now - _moodSince >= _settings.MinMoodS))
        {
            var fadeOut = Current is null || _pauseEnds is not null ? 0 : _settings.FadeS;
            Current = wanted;
            _moodSince = now;
            return new MusicStep(NewPiece(now, wanted), fadeOut);
        }

        if (wanted != Current)
        {
            return new MusicStep(null, 0);
        }

        if (_pauseEnds is null && now >= _pieceEnds)
        {
            // The piece has ended by itself: a calm pause before the next one.
            _pauseEnds = now + Between(_settings.PauseS, "pause");
            return new MusicStep(null, 0);
        }

        return _pauseEnds is { } pause && now >= pause ? new MusicStep(NewPiece(now, wanted), 0) : new MusicStep(null, 0);
    }

    private MusicCue NewPiece(double now, string mood)
    {
        var seconds = Between(_settings.PieceS, "piece");
        _pieceEnds = now + seconds;
        _pauseEnds = null;
        var seed = SplitMix64.SeedOf(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_worldSeed}|music|{mood}|{_counter++}"));
        return new MusicCue(mood, seed, seconds, _settings.FadeS);
    }

    private double Between(MusicRange range, string what)
    {
        var random = new SplitMix64(SplitMix64.SeedOf(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_worldSeed}|music-{what}|{_counter}")));
        return range.Min + ((Math.Max(range.Min, range.Max) - range.Min) * (random.NextSigned() + 1) / 2);
    }
}
