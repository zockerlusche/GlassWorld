using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Audio;

/// <summary>A sound to start this frame.</summary>
/// <param name="SoundId">Sound id from the sound map.</param>
/// <param name="Variant">Variant (seeded per event, so replays sound the same).</param>
/// <param name="Bus">Mixer bus.</param>
/// <param name="Position">Where it sounds (metres).</param>
/// <param name="Gain">Gain at the listener when it starts.</param>
public sealed record PlayRequest(string SoundId, int Variant, MixBusId Bus, Position Position, double Gain)
{
    /// <summary>A made sound file to play instead of the sound id (invention sound, D471 D7), or null.</summary>
    public string? File { get; init; }
}

/// <summary>
/// Chooses the sounds of one frame from the core's events (B8 R2, R7): an action that starts sounds
/// at the inhabitant; inaudible sounds are skipped; each bus plays at most its voice budget, nearest
/// first. Sound is presentation only and never feeds back into the simulation.
/// </summary>
public sealed class SoundDirector(SoundLibrary library, SoundMixer mixer, IReadOnlyDictionary<string, int>? fileVariants = null)
{
    /// <summary>Plans the sounds for the events of one frame.</summary>
    /// <param name="events">New events since the previous frame (from the feed's cursor).</param>
    /// <param name="positions">Inhabitant positions by id.</param>
    /// <param name="listener">Listener position (camera, projected on the ground).</param>
    /// <param name="zoom">Zoom 0 (in) .. 1 (out).</param>
    /// <param name="playing">Sounds each bus is already playing.</param>
    /// <param name="inventionSound">Sound of an invented thing an event uses (D471 D7), or null for the action's sound.</param>
    public IReadOnlyList<PlayRequest> Plan(
        IEnumerable<WorldEvent> events,
        IReadOnlyDictionary<string, Position> positions,
        Position listener,
        double zoom,
        IReadOnlyDictionary<MixBusId, int> playing,
        Func<WorldEvent, InventionSound?>? inventionSound = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(playing);
        var candidates = new List<PlayRequest>();
        foreach (var e in events)
        {
            if (e.Kind != InhabitantsSystem.ActionStarted || e.Action is null || !positions.TryGetValue(e.InhabitantId, out var at))
            {
                continue;
            }

            // Using an invented thing sounds like the thing (D471 D7): its made file or a library sound.
            if (inventionSound?.Invoke(e) is { } own)
            {
                var ownGain = mixer.Gain(MixBusId.Effects, Distance(at, listener), zoom);
                if (ownGain > 0 && own.FilePath is { } path)
                {
                    candidates.Add(new PlayRequest("invention", 0, MixBusId.Effects, at, ownGain) { File = path });
                    continue;
                }

                if (ownGain > 0 && own.LibrarySoundId is { } id)
                {
                    var count = fileVariants is not null && fileVariants.TryGetValue(id, out var n) ? n : 1;
                    candidates.Add(new PlayRequest(id, VariantOf(e, count), MixBusId.Effects, at, ownGain));
                    continue;
                }
            }

            var sound = library.ForAction(e.Action);
            if (sound is null)
            {
                continue;
            }

            var bus = BusOf(library.Recipe(sound).Bus);
            var gain = mixer.Gain(bus, Distance(at, listener), zoom);
            if (gain > 0)
            {
                // Sounds with files in the base library use all their file variants (B8-4).
                var variants = fileVariants is not null && fileVariants.TryGetValue(sound, out var files) ? files : library.Recipe(sound).Variants;
                candidates.Add(new PlayRequest(sound, VariantOf(e, variants), bus, at, gain));
            }
        }

        return [.. candidates
            .GroupBy(c => c.Bus)
            .SelectMany(g => g.OrderByDescending(c => c.Gain).ThenBy(c => c.SoundId, StringComparer.Ordinal)
                .Take(Math.Max(0, mixer.Bus(g.Key).MaxVoices - playing.GetValueOrDefault(g.Key))))];
    }

    /// <summary>Bus of a recipe.</summary>
    public static MixBusId BusOf(SynthRecipeBus bus) => bus switch
    {
        SynthRecipeBus.Effects => MixBusId.Effects,
        SynthRecipeBus.Ambience => MixBusId.Ambience,
        SynthRecipeBus.Animals => MixBusId.Animals,
        SynthRecipeBus.Voices => MixBusId.Voices,
        SynthRecipeBus.Music => MixBusId.Music,
        _ => MixBusId.Effects,
    };

    /// <summary>Ground distance in metres.</summary>
    public static double Distance(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Variant from a stable hash of the event; the number of variants comes from the recipe.</summary>
    private static int VariantOf(WorldEvent e, int variants) =>
        (int)(SplitMix64.SeedOf($"{e.Tick}/{e.InhabitantId}/{e.Action}") % (ulong)Math.Max(1, variants));
}
