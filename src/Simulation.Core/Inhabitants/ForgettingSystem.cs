using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>A belief that is fading: its confidence before fading, when it was last used.</summary>
/// <param name="InhabitantId">Whose belief.</param>
/// <param name="TypeId">The type it is about.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Original">Its confidence before it began to fade (what using it brings back).</param>
/// <param name="Used">Tick it was last used (learned, acted on, looked at, told).</param>
public sealed record FadingBelief(string InhabitantId, string TypeId, string PropertyId, double Original, long Used);

/// <summary>A belief that was forgotten: kept as it was (nothing is deleted, D1017), out of the inhabitant's mind.</summary>
/// <param name="InhabitantId">Whose belief.</param>
/// <param name="TypeId">The type it is about.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Value">What it believed.</param>
/// <param name="Confidence">How sure it was before it faded.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="ToldBy">Who told it, or null.</param>
/// <param name="Learned">Tick it was learned.</param>
/// <param name="Forgotten">Tick it was forgotten.</param>
public sealed record ForgottenBelief(string InhabitantId, string TypeId, string PropertyId, bool Value, double Confidence, string Source, string? ToldBy, long Learned, long Forgotten);

/// <summary>A floor under an inhabitant's beliefs about a kind of thing, raised by a formative memory (D1017 G1b).</summary>
/// <param name="InhabitantId">Whose beliefs.</param>
/// <param name="TypeId">The kind of thing.</param>
/// <param name="Floor">The strength they never fall below.</param>
public sealed record FormativeFloor(string InhabitantId, string TypeId, double Floor);

/// <summary>What forgetting holds (saved; hashed only when not empty).</summary>
/// <param name="Fading">Beliefs that have faded or were used since they were learned, in key order.</param>
/// <param name="Forgotten">Forgotten beliefs, oldest first.</param>
public sealed record ForgettingState(IReadOnlyList<FadingBelief> Fading, IReadOnlyList<ForgottenBelief> Forgotten)
{
    /// <summary>Floors from formative memories (D1017 G1b), in key order; null = none (older saves).</summary>
    public IReadOnlyList<FormativeFloor>? Floors { get; init; }
}

/// <summary>
/// D1017 G1 (B2707): knowledge fades when it is not used. Once a game day, at its start, every belief's strength is
/// <c>1 - decay_per_day[source] x days since last used</c> (data, per source): below <c>forgotten_below</c> the belief
/// leaves the inhabitant's mind and is kept as forgotten (never deleted - world and log keep everything); above it,
/// the inhabitant is as sure as before times the strength, so a fading belief reaches the AI as a weaker one ("I
/// think ...") and every reader of beliefs sees the same. Using a belief - acting on a thing of its type, or learning
/// it again - makes it as sure as before. Without the data's <c>forgetting</c> nothing fades (worlds keep their hash).
/// The code knows no belief, thing or source by name.
/// </summary>
public sealed class ForgettingSystem : IWorldSystem, IOptionalWorldState
{
    private readonly KnowledgeForgetting? _settings;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly long _ticksPerDay;
    private readonly SortedDictionary<(string Inhabitant, string Type, string Property), (double Original, long Used)> _fading = new(KeyOrder.Instance);
    private readonly List<ForgottenBelief> _forgotten = [];
    private readonly SortedDictionary<(string Inhabitant, string Type), double> _floors = new(PairOrder.Instance);

    /// <summary>Creates the system; it listens to the inhabitants' events.</summary>
    /// <param name="settings">The knowledge data's <c>forgetting</c>, or null for none.</param>
    /// <param name="inhabitants">The inhabitants.</param>
    /// <param name="objects">The world's things (the type of a thing acted on).</param>
    /// <param name="ticksPerDay">Ticks per game day.</param>
    public ForgettingSystem(KnowledgeForgetting? settings, InhabitantsSystem inhabitants, WorldObjects objects, long ticksPerDay)
    {
        _settings = settings;
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _ticksPerDay = Math.Max(1, ticksPerDay);
        if (settings is not null)
        {
            _inhabitants.Reported += Observe;
        }
    }

    /// <inheritdoc/>
    public string Name => "forgetting";

    /// <inheritdoc/>
    public bool HasState => _fading.Count > 0 || _forgotten.Count > 0 || _floors.Count > 0;

    /// <summary>The forgotten beliefs, oldest first.</summary>
    public IReadOnlyList<ForgottenBelief> Forgotten => _forgotten;

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_settings is not { } settings || world.Tick == 0 || world.Tick % _ticksPerDay != 0)
        {
            return;
        }

        foreach (var inhabitant in _inhabitants.All)
        {
            foreach (var (typeId, propertyId, belief) in inhabitant.Beliefs.Ordered.ToList())
            {
                Fade(world.Tick, settings, inhabitant, typeId, propertyId, belief);
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_fading.Count);
        foreach (var ((inhabitant, type, property), (original, used)) in _fading)
        {
            hasher.Write(inhabitant).Write(type).Write(property).Write(original).Write(used);
        }

        hasher.Write(_forgotten.Count);
        foreach (var f in _forgotten)
        {
            hasher.Write(f.InhabitantId).Write(f.TypeId).Write(f.PropertyId).Write(f.Value).Write(f.Confidence).Write(f.Source)
                .Write(f.ToldBy ?? string.Empty).Write(f.Learned).Write(f.Forgotten);
        }

        // Floors only when there are any: states without them hash as before.
        if (_floors.Count > 0)
        {
            hasher.Write(_floors.Count);
            foreach (var ((inhabitant, type), floor) in _floors)
            {
                hasher.Write(inhabitant).Write(type).Write(floor);
            }
        }
    }

    /// <summary>The state to save.</summary>
    public ForgettingState Capture() =>
        new([.. _fading.Select(f => new FadingBelief(f.Key.Inhabitant, f.Key.Type, f.Key.Property, f.Value.Original, f.Value.Used))], [.. _forgotten])
        {
            Floors = _floors.Count > 0 ? [.. _floors.Select(f => new FormativeFloor(f.Key.Inhabitant, f.Key.Type, f.Value))] : null,
        };

    /// <summary>Restores a saved state (the beliefs themselves come back with the inhabitants).</summary>
    public void Restore(ForgettingState? state)
    {
        _fading.Clear();
        _forgotten.Clear();
        foreach (var f in state?.Fading ?? [])
        {
            _fading[(f.InhabitantId, f.TypeId, f.PropertyId)] = (f.Original, f.Used);
        }

        _forgotten.AddRange(state?.Forgotten ?? []);
        _floors.Clear();
        foreach (var f in state?.Floors ?? [])
        {
            _floors[(f.InhabitantId, f.TypeId)] = f.Floor;
        }
    }

    /// <summary>
    /// A memory was recorded (<see cref="MemoryRecorder.Remembered"/>): a formative one of a kind the data name
    /// (<c>floor_after</c>), about a kind of thing, raises the floor of everything the inhabitant believes about that
    /// kind - now and later learned. The highest floor stays.
    /// </summary>
    public void Remembered(Inhabitant inhabitant, string kind, string? aboutKind, string? aboutId, bool formative)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var floor = _settings?.FloorAfter?.Where(f => string.Equals(f.Kind, kind, StringComparison.Ordinal)).Select(f => f.Floor).DefaultIfEmpty(0).Max() ?? 0;
        if (!formative || aboutKind != InhabitantsSystem.AboutType || aboutId is null || floor <= 0)
        {
            return;
        }

        var key = (inhabitant.Id, aboutId);
        _floors[key] = Math.Max(floor, _floors.GetValueOrDefault(key));
    }

    /// <summary>
    /// Whether the inhabitant is no longer sure of a belief (GW2 #395): it has faded below the data's
    /// <c>unsure_below</c> - its confidence now against the confidence before it began to fade.
    /// </summary>
    public bool IsUnsure(Inhabitant inhabitant, string typeId, string propertyId)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return _settings?.UnsureBelow is { } below
            && _fading.TryGetValue((inhabitant.Id, typeId, propertyId), out var known) && known.Original > 0
            && inhabitant.Beliefs.TryGet(typeId, propertyId, out var belief)
            && belief.Confidence / known.Original < below;
    }

    /// <summary>The strength of a belief now: 1 when fresh, falling by the source's decay per day since it was used.</summary>
    internal double StrengthOf(Belief belief, long used, long now) =>
        _settings is { } settings && settings.DecayPerDay.TryGetValue(belief.Source, out var decay)
            ? Math.Clamp(1.0 - (decay * Math.Max(0, now - used) / _ticksPerDay), 0.0, 1.0)
            : 1.0;

    private void Fade(long tick, KnowledgeForgetting settings, Inhabitant inhabitant, string typeId, string propertyId, Belief belief)
    {
        var key = (inhabitant.Id, typeId, propertyId);
        var (original, used) = _fading.TryGetValue(key, out var known) ? known : (belief.Confidence, belief.Tick);

        // What hurt is never forgotten (D1077): never below the floor a formative memory set.
        var strength = Math.Max(StrengthOf(belief, used, tick), _floors.GetValueOrDefault((inhabitant.Id, typeId)));
        if (strength < settings.ForgottenBelow)
        {
            // Forgotten: out of its mind, kept as it was.
            inhabitant.Beliefs.Remove(typeId, propertyId);
            _fading.Remove(key);
            _forgotten.Add(new ForgottenBelief(inhabitant.Id, typeId, propertyId, belief.Value, original, belief.Source, belief.ToldBy, belief.Tick, tick));
            return;
        }

        if (strength >= 1.0)
        {
            return;
        }

        var faded = original * strength;
        if (Math.Abs(faded - belief.Confidence) > 1e-12)
        {
            inhabitant.Beliefs.Set(typeId, propertyId, belief.Value, faded, belief.Source, belief.ToldBy, belief.Tick);
        }

        _fading[key] = (original, used);
    }

    private void Observe(WorldEvent e)
    {
        if (!_inhabitants.TryGet(e.InhabitantId, out var inhabitant))
        {
            return;
        }

        if (e.Kind == InhabitantsSystem.KnowledgeGained && e.TargetId is { } learnedType && e.Detail is { } detail
            && detail.IndexOf('/', StringComparison.Ordinal) is var cut and > 0)
        {
            // Learned (again): fresh, as sure as it was just learned.
            var property = detail[..cut];
            _fading.Remove((inhabitant.Id, learnedType, property));

            // Telling uses the teller's knowledge too (review GW2 #392): the listener's belief names who told it.
            if (inhabitant.Beliefs.TryGet(learnedType, property, out var heard) && heard.ToldBy is { } tellerId
                && _inhabitants.TryGet(tellerId, out var teller))
            {
                Use(teller, learnedType, property, e.Tick);
            }

            return;
        }

        if (e.Kind != InhabitantsSystem.ActionFinished)
        {
            return;
        }

        // Acting on a thing uses what it believes about its kind: every belief about that type is as sure as before.
        var type = e.TargetType ?? (e.TargetId is { } id && _objects.TryGet(id, out var thing) ? thing.TypeId : null);
        if (type is null)
        {
            return;
        }

        foreach (var (propertyId, _) in inhabitant.Beliefs.About(type).ToList())
        {
            Use(inhabitant, type, propertyId, e.Tick);
        }
    }

    /// <summary>A belief used now: as sure as before it faded, its last use now.</summary>
    private void Use(Inhabitant inhabitant, string type, string propertyId, long tick)
    {
        if (!inhabitant.Beliefs.TryGet(type, propertyId, out var belief))
        {
            return;
        }

        var key = (inhabitant.Id, type, propertyId);
        var original = _fading.TryGetValue(key, out var known) ? known.Original : belief.Confidence;
        if (Math.Abs(original - belief.Confidence) > 1e-12)
        {
            inhabitant.Beliefs.Set(type, propertyId, belief.Value, original, belief.Source, belief.ToldBy, belief.Tick);
        }

        _fading[key] = (original, tick);
    }

    private sealed class PairOrder : IComparer<(string Inhabitant, string Type)>
    {
        public static readonly PairOrder Instance = new();

        public int Compare((string Inhabitant, string Type) x, (string Inhabitant, string Type) y)
        {
            var c = string.CompareOrdinal(x.Inhabitant, y.Inhabitant);
            return c != 0 ? c : string.CompareOrdinal(x.Type, y.Type);
        }
    }

    private sealed class KeyOrder : IComparer<(string Inhabitant, string Type, string Property)>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare((string Inhabitant, string Type, string Property) x, (string Inhabitant, string Type, string Property) y)
        {
            var c = string.CompareOrdinal(x.Inhabitant, y.Inhabitant);
            c = c != 0 ? c : string.CompareOrdinal(x.Type, y.Type);
            return c != 0 ? c : string.CompareOrdinal(x.Property, y.Property);
        }
    }
}
