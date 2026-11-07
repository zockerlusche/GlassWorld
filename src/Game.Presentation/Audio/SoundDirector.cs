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
public sealed record PlayRequest(string SoundId, int Variant, MixBusId Bus, Position Position, double Gain);

/// <summary>
/// Chooses the sounds of one frame from the core's events (B8 R2, R7): an action that starts sounds
/// at the inhabitant; inaudible sounds are skipped; each bus plays at most its voice budget, nearest
/// first. Sound is presentation only and never feeds back into the simulation.
/// </summary>
public sealed class SoundDirector(SoundLibrary library, SoundMixer mixer)
{
    /// <summary>Plans the sounds for the events of one frame.</summary>
    /// <param name="events">New events since the previous frame (from the feed's cursor).</param>
    /// <param name="positions">Inhabitant positions by id.</param>
    /// <param name="listener">Listener position (camera, projected on the ground).</param>
    /// <param name="zoom">Zoom 0 (in) .. 1 (out).</param>
    /// <param name="playing">Sounds each bus is already playing.</param>
    public IReadOnlyList<PlayRequest> Plan(
        IEnumerable<WorldEvent> events,
        IReadOnlyDictionary<string, Position> positions,
        Position listener,
        double zoom,
        IReadOnlyDictionary<MixBusId, int> playing)
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

            var sound = library.ForAction(e.Action);
            if (sound is null)
            {
                continue;
            }

            var bus = BusOf(library.Recipe(sound).Bus);
            var gain = mixer.Gain(bus, Distance(at, listener), zoom);
            if (gain > 0)
            {
                candidates.Add(new PlayRequest(sound, VariantOf(e), bus, at, gain));
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

    private static int VariantOf(WorldEvent e) => (int)(SplitMix64.SeedOf($"{e.Tick}/{e.InhabitantId}/{e.Action}") % 4);
}
