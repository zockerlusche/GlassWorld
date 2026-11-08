using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>One note of a song.</summary>
/// <param name="StartHz">Pitch at the start.</param>
/// <param name="EndHz">Pitch at the end (glide).</param>
/// <param name="DurationS">Length.</param>
public sealed record SongNote(double StartHz, double EndHz, double DurationS);

/// <summary>
/// Songbird melodies (B8 R4): small, varying melodies built from the data (notes from a scale above a
/// base pitch, glides, short trills); each call has its own seed, so songs differ, and the same seed
/// always gives the same song.
/// </summary>
public static class Birdsong
{
    /// <summary>The notes of the song for a seed.</summary>
    public static IReadOnlyList<SongNote> Notes(BirdsongSettings settings, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var random = new SplitMix64(seed);
        var count = settings.NotesMin + (int)(random.Next() % (ulong)(Math.Max(settings.NotesMin, settings.NotesMax) - settings.NotesMin + 1));
        var baseHz = Between(settings.BaseHzMin, settings.BaseHzMax, random);
        var steps = settings.Steps.ToList();
        var notes = new List<SongNote>();
        while (notes.Count < count)
        {
            var pitch = baseHz * Math.Pow(2, steps[(int)(random.Next() % (ulong)steps.Count)] / 12.0);
            var glide = Math.Pow(2, settings.GlideSemitones * random.NextSigned() / 12.0);
            var duration = Between(settings.NoteSMin, settings.NoteSMax, random);
            if ((random.NextSigned() + 1) / 2 < settings.TrillChance)
            {
                // A trill: the same short note a few times.
                for (var i = 0; i < settings.TrillNotes && notes.Count < count; i++)
                {
                    notes.Add(new SongNote(pitch, pitch, settings.NoteSMin));
                }
            }
            else
            {
                notes.Add(new SongNote(pitch, pitch * glide, duration));
            }
        }

        return notes;
    }

    /// <summary>Renders the song for a seed into 16-bit mono PCM.</summary>
    public static short[] Render(BirdsongSettings settings, int sampleRate, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var notes = Notes(settings, seed);
        var total = notes.Sum(n => n.DurationS + settings.GapS);
        var mix = new double[Math.Max(1, (int)Math.Ceiling(total * sampleRate))];
        var position = 0;
        foreach (var note in notes)
        {
            var length = Math.Max(1, (int)Math.Round(note.DurationS * sampleRate));
            var phase = 0.0;
            for (var i = 0; i < length && position + i < mix.Length; i++)
            {
                var progress = (double)i / length;
                phase += (note.StartHz * Math.Pow(note.EndHz / note.StartHz, progress)) / sampleRate;
                phase -= Math.Floor(phase);

                // Soft edges: a short rise and a longer fall, like a whistle.
                var envelope = Math.Min(1, progress / 0.1) * Math.Pow(1 - progress, 1.5);
                mix[position + i] += Math.Sin(2 * Math.PI * phase) * envelope;
            }

            position += length + (int)Math.Round(settings.GapS * sampleRate);
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

    private static double Between(double min, double max, SplitMix64 random) =>
        min + ((Math.Max(min, max) - min) * (random.NextSigned() + 1) / 2);
}
