using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Writing;

/// <summary>
/// Writing on an object (B8 R15, spec 33 §9a): which knowledge, on which carrier, how readable, by whom
/// and when. The object is the thing; the text is a part of it that wears away.
/// </summary>
/// <param name="ObjectId">The object it is written on.</param>
/// <param name="MediumId">Carrier (writing_medium vocabulary).</param>
/// <param name="KnowledgeRef">Which knowledge entry it holds (language-neutral; the knowledge store A1400 resolves it).</param>
/// <param name="AuthorId">Who wrote it.</param>
/// <param name="WrittenTick">When.</param>
/// <param name="Legibility">How readable it is now (0..1); at 0 only the object remains.</param>
public sealed record WrittenText(string ObjectId, string MediumId, string KnowledgeRef, string AuthorId, long WrittenTick, double Legibility);

/// <summary>
/// Writing as a frame in the core (B8 R13-R15): texts on objects wear away by the carrier's data
/// (by itself per day, more in some weather, e.g. rain wipes sand). A text whose legibility reaches 0
/// is gone, the object stays; a text goes with its object. Lost objects keep their text, so it can be
/// found generations later. Nothing here writes: the actions write/read come with the ability register
/// (A1372) and the knowledge store (A1400). The start content has no writing, so the system stays out of
/// the world hash until the first text (golden hashes).
/// </summary>
public sealed class WritingSystem : IWorldSystem, IOptionalWorldState
{
    private readonly Dictionary<string, WritingMedium> _media;
    private readonly WritingMedia _rules;
    private readonly WorldObjects _objects;
    private readonly IWeather? _weather;
    private readonly int _ticksPerHour;
    private readonly long _ticksPerDay;
    private readonly SortedDictionary<string, WrittenText> _texts = new(StringComparer.Ordinal);

    /// <summary>Creates the system; <paramref name="weather"/> is null while the world has no weather.</summary>
    public WritingSystem(WritingMedia rules, WorldObjects objects, IWeather? weather, int ticksPerHour, long ticksPerDay)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerHour);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerDay);
        _media = rules.Media.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _weather = weather;
        _ticksPerHour = ticksPerHour;
        _ticksPerDay = ticksPerDay;
    }

    /// <inheritdoc/>
    public string Name => "writing";

    /// <inheritdoc/>
    public bool HasState => _texts.Count > 0;

    /// <summary>All texts in object id order.</summary>
    public IReadOnlyList<WrittenText> Texts => [.. _texts.Values];

    /// <summary>The text on an object, or null.</summary>
    public WrittenText? On(string objectId) => _texts.GetValueOrDefault(objectId);

    /// <summary>Whether a text can still be read at all (the data's least legibility).</summary>
    public bool Readable(WrittenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Legibility >= _rules.Read.MinLegibility;
    }

    /// <summary>
    /// Puts a text on an object (the write action of A1372/B4b uses this after its checks). The object
    /// must exist, the carrier must be known, and legibility is capped by the carrier.
    /// </summary>
    public void Place(WrittenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_objects.TryGet(text.ObjectId, out _))
        {
            throw new InvalidOperationException($"no object '{text.ObjectId}' to write on");
        }

        var medium = _media.GetValueOrDefault(text.MediumId) ?? throw new InvalidOperationException($"unknown writing medium '{text.MediumId}'");
        _texts[text.ObjectId] = text with { Legibility = Math.Clamp(text.Legibility, 0, medium.Legibility) };
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_texts.Count == 0)
        {
            return;
        }

        foreach (var text in _texts.Values.ToList())
        {
            if (!_objects.TryGet(text.ObjectId, out var obj))
            {
                // The object is gone (used up, burnt): so is what was written on it.
                _texts.Remove(text.ObjectId);
                continue;
            }

            var medium = _media[text.MediumId];
            var loss = medium.DecayPerDay / _ticksPerDay;
            if (_weather?.WeatherAt(obj.Position, world.Tick) is { } weather)
            {
                loss += (medium.WeatherLoss.FirstOrDefault(w => w.Weather == weather)?.PerHour ?? 0) / _ticksPerHour;
            }

            var legibility = text.Legibility - loss;
            if (legibility <= 0)
            {
                _texts.Remove(text.ObjectId);
            }
            else
            {
                _texts[text.ObjectId] = text with { Legibility = legibility };
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_texts.Count);
        foreach (var t in _texts.Values)
        {
            hasher.Write(t.ObjectId).Write(t.MediumId).Write(t.KnowledgeRef).Write(t.AuthorId).Write(t.WrittenTick).Write(t.Legibility);
        }
    }

    /// <summary>The state to save.</summary>
    public IReadOnlyList<WrittenText> Capture() => Texts;

    /// <summary>Restores saved texts (null = a save game from before writing: none).</summary>
    public void Restore(IEnumerable<WrittenText>? texts)
    {
        _texts.Clear();
        foreach (var text in texts ?? [])
        {
            _texts[text.ObjectId] = text;
        }
    }
}
