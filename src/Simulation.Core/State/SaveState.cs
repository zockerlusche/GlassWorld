using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Random;

namespace GlassWorld.Simulation.State;

/// <summary>A belief as saved.</summary>
public sealed record SavedBelief(string TypeId, string PropertyId, bool Value, double Confidence, string Source = KnowledgeSources.Experienced, string? ToldBy = null, long Tick = 0);

/// <summary>A remembered place as saved (TL #19921).</summary>
public sealed record SavedPlace(string ObjectId, string TypeId, Position Position, long Tick);

/// <summary>A relationship as saved (B4a R7).</summary>
public sealed record SavedRelation(string Other, double Affection, double Trust, double Respect);

/// <summary>An imprinting counter as saved (B4a R6); empty kind and id for an action without a target.</summary>
public sealed record SavedImprint(string Action, string AboutKind, string AboutId, string TimeOfDay, double Count);

/// <summary>
/// An inhabitant's memory as saved (B4a R5): summary, running number, events, what it has remembered for the
/// first time, and up to which running number its events have gone out to a condensing.
/// </summary>
public sealed record SavedMemory(string Summary, long LastSeq, IReadOnlyList<MemoryEntry> Events, IReadOnlyList<string> Firsts, long SealedSeq = 0);

/// <summary>An inhabitant as saved.</summary>
public sealed record SavedInhabitant(
    string Id,
    string Sex,
    int Group,
    Position Position,
    IReadOnlyDictionary<string, double> Needs,
    IReadOnlyDictionary<string, double> Moods,
    IReadOnlyDictionary<string, double> Traits,
    double WillToLive,
    long NeglectTicks,
    Activity? Activity,
    Activity? Planned,
    IReadOnlyList<SavedBelief> Beliefs,
    Appearance? Appearance = null,
    IReadOnlyList<SavedRelation>? Relations = null,
    IReadOnlyDictionary<string, long>? SearchSince = null,
    IReadOnlyList<SavedPlace>? Places = null,
    RepeatRun? Repeat = null,
    IReadOnlyList<RestingAction>? Resting = null,
    string? Name = null,
    IReadOnlyList<string>? KnownNames = null,
    IReadOnlyList<string>? Met = null,
    IReadOnlyList<SavedImprint>? Imprints = null,
    SavedMemory? Memory = null);

/// <summary>An open question as saved.</summary>
public sealed record SavedQuestion(string Id, string InhabitantId, string Kind, string Subject, string RequestId, long AskedTick, IReadOnlyList<SavedOption> Options, long? RetryAtTick);

/// <summary>An offered option as saved.</summary>
public sealed record SavedOption(string OptionId, string Action, string? TargetId);

/// <summary>
/// The complete simulation state as plain data (no IO in the core). Simulation.Data stores it
/// in SQLite; restoring it gives exactly the same state hash (ADR-0003).
/// </summary>
public sealed record SaveState(
    ulong Seed,
    long Tick,
    IReadOnlyList<KeyValuePair<string, PcgState>> Streams,
    long NextObjectNumber,
    IReadOnlyList<WorldObject> Objects,
    IReadOnlyList<SavedInhabitant> Inhabitants,
    long QuestionCounter,
    IReadOnlyList<SavedQuestion> Questions,
    IReadOnlyList<KeyValuePair<string, long>> LastAsked,
    IReadOnlyList<DecisionLogEntry> DecisionLog,
    string? WorldPreset = null,
    string? IslandHash = null,
    IReadOnlyList<Map.Occurrence>? Occurrences = null,
    InventionState? Inventions = null,
    IReadOnlyList<ChronicleRecord>? Chronicle = null)
{
    /// <summary>The generated island as compact bytes (B2 R11); null on the placeholder world and in older saves.</summary>
    public byte[]? IslandData { get; init; }

    /// <summary>Favour, futile attempts and calls for help (B6); null in save games from before.</summary>
    public Player.PlayerState? Player { get; init; }

    /// <summary>Writing on objects (B8 R15); null in save games from before.</summary>
    public IReadOnlyList<Writing.WrittenText>? Writing { get; init; }

    /// <summary>Who has seen which text ("inhabitant|object", R15 found again); null when nobody has.</summary>
    public IReadOnlyList<string>? WritingSeen { get; init; }

    /// <summary>The weather (B2 R13); null in worlds without weather and in older save games.</summary>
    public Weather.WeatherState? Weather { get; init; }

    /// <summary>
    /// The look epoch (B2-5 R16); null while the world is in its first epoch and in save games from before,
    /// which load in the first epoch.
    /// </summary>
    public string? LookEpoch { get; init; }

    /// <summary>
    /// Seen cells (B2-6 R14): the player's view and the cells per inhabitant. Null on worlds without an
    /// island and in save games from before, which start with what the inhabitants see when loaded.
    /// </summary>
    public Exploration.ExplorationState? Exploration { get; init; }

    /// <summary>New skills of inventions (A1372); null in save games from before or without any.</summary>
    public SkillRegisterState? Skills { get; init; }

    /// <summary>Practised skill gains per inhabitant (B5 part); null when nobody has practised.</summary>
    public IReadOnlyList<SkillGain>? SkillGains { get; init; }

    /// <summary>The mailbox (B6 R11); null in save games from before.</summary>
    public Player.MailboxState? Mailbox { get; init; }

    /// <summary>The player's favourites (B6 R19); null in save games from before or with none marked.</summary>
    public Player.FavouritesState? Favourites { get; init; }

    /// <summary>
    /// True when the save game knows own names (W1698), even if nobody has one; false in save games from before,
    /// whose inhabitants get their names from the seed on load and know nobody's name.
    /// </summary>
    public bool NamesSaved { get; init; }

    /// <summary>Conversations with the player (B6 R13); null when nobody has been spoken to.</summary>
    public Player.ConversationState? Conversation { get; init; }

    /// <summary>
    /// Fingerprint of the content data the world ran with (set by the saving host, not world state and not in
    /// the hash); null in older save games and when the host did not set it. Replays compare it first.
    /// </summary>
    public string? ContentFingerprint { get; init; }

    /// <summary>
    /// Each inhabitant's diary of started actions (TL #20472): presentation-only history, saved but not world
    /// state and not in the hash; null in save games from before (they start with an empty diary).
    /// </summary>
    public IReadOnlyList<DiaryEntry>? Diary { get; init; }

    /// <summary>The diary's running number (continues across save and load); null in saves from before.</summary>
    public long? DiaryVersion { get; init; }

    /// <summary>
    /// Open memory and question jobs of the compute night (B4a R5, B7); null while there are none and in save
    /// games from before.
    /// </summary>
    public Night.NightJobsState? NightJobs { get; init; }
}

/// <summary>Captures and restores the complete state of a <see cref="SimulationHost"/>.</summary>
public static class StateCodec
{
    /// <summary>Takes the complete state.</summary>
    public static SaveState Capture(SimulationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var (counter, questions, lastAsked) = host.Decisions.CaptureState();
        return new SaveState(
            host.World.Seed,
            host.World.Tick,
            host.World.Random.Snapshot(),
            host.Objects.NextNumber,
            [.. host.Objects.All],
            [.. host.Inhabitants.All.Select(i => new SavedInhabitant(
                i.Id,
                i.Sex,
                i.Group,
                i.Position,
                new Dictionary<string, double>(i.Needs, StringComparer.Ordinal),
                new Dictionary<string, double>(i.Moods, StringComparer.Ordinal),
                new Dictionary<string, double>(i.Traits, StringComparer.Ordinal),
                i.WillToLive,
                i.NeglectTicks,
                i.Activity,
                i.Planned,
                [.. i.Beliefs.All.Select(b => new SavedBelief(b.TypeId, b.PropertyId, b.Belief.Value, b.Belief.Confidence, b.Belief.Source, b.Belief.ToldBy, b.Belief.Tick))],
                i.Appearance,
                [.. i.Relationships.All.Select(r => new SavedRelation(r.Other, r.Relation.Affection, r.Relation.Trust, r.Relation.Respect))],
                i.SearchSince.Count > 0 ? new Dictionary<string, long>(i.SearchSince, StringComparer.Ordinal) : null,
                i.Places.Count > 0 ? [.. i.Places.Select(p => new SavedPlace(p.Key, p.Value.TypeId, p.Value.Position, p.Value.Tick))] : null,
                i.Repeat,
                i.Resting.Count > 0 ? [.. i.Resting] : null,
                i.Name,
                i.KnownNames.Count > 0 ? [.. i.KnownNames] : null,
                i.Met.Count > 0 ? [.. i.Met] : null,
                i.Imprints.Count > 0 ? [.. i.Imprints.Select(p => new SavedImprint(p.Key.Action, p.Key.AboutKind, p.Key.AboutId, p.Key.TimeOfDay, p.Value))] : null,
                i.Memory.HasState ? new SavedMemory(i.Memory.Summary, i.Memory.LastSeq, [.. i.Memory.Events], [.. i.Memory.Firsts], i.Memory.SealedSeq) : null))],
            counter,
            [.. questions.Select(q => new SavedQuestion(q.Id, q.InhabitantId, q.Kind, q.Subject, q.RequestId, q.AskedTick, [.. q.Options.Select(o => new SavedOption(o.OptionId, o.Action, o.TargetId))], q.RetryAtTick))],
            lastAsked,
            [.. host.Decisions.Log.Entries],
            host.Map?.Island.PresetId,
            host.Map?.IslandHash,
            host.Map is null ? null : [.. host.Map.Occurrences],
            host.Inventions.Capture(),
            [.. host.Chronicle.All])
        {
            Player = host.Player.Capture(),
            Writing = host.Writing.HasState ? host.Writing.Capture() : null,
            WritingSeen = host.Writing.Seen.Count > 0 ? host.Writing.Seen : null,
            IslandData = host.Map is { } map ? Map.IslandCodec.Encode(map.Island, map.CellSizeCm) : null,
            Weather = host.WeatherSystem?.Capture(),
            LookEpoch = host.LookEpoch.Capture(),
            Exploration = host.Exploration?.Capture(),
            Skills = host.Skills.HasState ? host.Skills.Capture() : null,
            SkillGains = host.SkillLevels.HasState ? host.SkillLevels.Capture() : null,
            Mailbox = host.Mailbox.Capture(),
            Favourites = host.Favourites.HasState ? host.Favourites.Capture() : null,
            NamesSaved = true,
            Conversation = host.Conversation.HasState || host.Conversation.Log.Count > 0 ? host.Conversation.Capture() : null,
            Diary = host.Diary.All,
            DiaryVersion = host.Diary.Version,
            NightJobs = host.NightJobs.HasState ? host.NightJobs.Capture() : null,
        };
    }

    /// <summary>Rebuilds a simulation from a saved state with the given content and decider port.</summary>
    public static SimulationHost Restore(SimulationHostFactory factory, SaveState state)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(state);
        var world = World.Restore(state.Seed, factory.Content.Calendar, state.Tick, state.Streams);
        // Saves with island data load that island (B2 R11); older saves generate it again and are
        // checked against their island hash below.
        var host = factory.Assemble(world, state.WorldPreset, state.IslandData is { } data ? Map.IslandCodec.Decode(data) : null);

        // The island is generated again from the current data; if data or generator changed since the
        // save, the world would differ silently, so such a save is refused (review GW2 #56).
        if (state.IslandHash is not null && host.Map?.IslandHash != state.IslandHash)
        {
            throw new InvalidDataException(
                $"The island of this save game (preset '{state.WorldPreset}', hash {state.IslandHash[..12]}) differs from the island the current data generate ({host.Map?.IslandHash[..12] ?? "none"}): world data or generator changed since the save.");
        }

        // Runtime types first: objects of invented types are restored right after.
        if (state.Inventions is not null)
        {
            host.Inventions.Restore(state.Inventions);
        }

        host.Chronicle.Restore(state.Chronicle ?? []);
        host.Skills.Restore(state.Skills);
        host.SkillLevels.Restore(state.SkillGains);
        foreach (var obj in state.Objects)
        {
            host.Objects.Restore(obj, state.NextObjectNumber);
        }

        // Texts after their objects (a text needs its object).
        host.Writing.Restore(state.Writing, state.WritingSeen);

        host.Objects.Restore(state.NextObjectNumber);
        host.Map?.Restore(state.Occurrences ?? []);
        foreach (var saved in state.Inhabitants)
        {
            var inhabitant = new Inhabitant(
                saved.Id,
                saved.Sex,
                saved.Group,
                saved.Position,
                new Dictionary<string, double>(saved.Needs, StringComparer.Ordinal),
                new Dictionary<string, double>(saved.Moods, StringComparer.Ordinal),
                new Dictionary<string, double>(saved.Traits, StringComparer.Ordinal),
                saved.WillToLive)
            {
                NeglectTicks = saved.NeglectTicks,
                Activity = saved.Activity,
                Planned = saved.Planned,
                // Ids renamed since the save get their current ids (F4), so the figure can be drawn.
                Appearance = saved.Appearance?.WithCurrentIds(factory.Content.Appearance),
                Name = saved.Name,
            };
            inhabitant.KnownNames.UnionWith(saved.KnownNames ?? []);
            inhabitant.Met.UnionWith(saved.Met ?? []);
            foreach (var imprint in saved.Imprints ?? [])
            {
                inhabitant.Imprints[new ImprintKey(imprint.Action, imprint.AboutKind, imprint.AboutId, imprint.TimeOfDay)] = imprint.Count;
            }

            foreach (var belief in saved.Beliefs)
            {
                inhabitant.Beliefs.Set(belief.TypeId, belief.PropertyId, belief.Value, belief.Confidence, belief.Source, belief.ToldBy, belief.Tick);
            }

            foreach (var relation in saved.Relations ?? [])
            {
                inhabitant.Relationships.Set(relation.Other, new Relation(relation.Affection, relation.Trust, relation.Respect));
            }

            foreach (var (key, tick) in saved.SearchSince ?? new Dictionary<string, long>())
            {
                inhabitant.SearchSince[key] = tick;
            }

            if (saved.Memory is { } memory)
            {
                inhabitant.Memory.Restore(memory.Summary, memory.LastSeq, memory.Events, memory.Firsts, memory.SealedSeq);
            }

            inhabitant.Repeat = saved.Repeat;
            inhabitant.Resting.AddRange(saved.Resting ?? []);

            foreach (var place in saved.Places ?? [])
            {
                inhabitant.Places[place.ObjectId] = new PlaceMemory(place.TypeId, place.Position, place.Tick);
            }

            host.Inhabitants.Add(inhabitant);
        }

        // B4a R8: the members of a start group have always met, also where a state carries no meetings
        // (review GW3 #207), so loading never makes partners meet a second time.
        host.Inhabitants.RememberStartGroups();

        // Save games from before names (W1698): everyone gets an own name from the seed, nobody knows another's.
        if (!state.NamesSaved)
        {
            host.Inhabitants.GiveNames(state.Seed);
        }

        host.Decisions.RestoreState(
            state.QuestionCounter,
            state.Questions.Select(q => new OpenQuestion(q.Id, q.InhabitantId, q.Kind, q.Subject, q.RequestId, q.AskedTick, [.. q.Options.Select(o => (o.OptionId, o.Action, o.TargetId))], q.RetryAtTick)),
            state.LastAsked,
            state.DecisionLog);
        host.Player.Restore(state.Player);
        host.Mailbox.Restore(state.Mailbox);
        host.Favourites.Restore(state.Favourites);
        host.Conversation.Restore(state.Conversation);
        if (state.Weather is not null)
        {
            host.WeatherSystem?.Restore(state.Weather, state.Tick);
        }

        host.LookEpoch.Restore(state.LookEpoch);
        if (host.Exploration is { } exploration)
        {
            if (state.Exploration is not null)
            {
                exploration.Restore(state.Exploration);
            }
            else
            {
                // A save game from before the cloud cover (B2-6 R14) starts with what the inhabitants see now.
                exploration.Look();
            }

            // Where each inhabitant last looked from and how far it saw then is saved with the seen cells
            // (B2594: the sight range changes with the weather), so the first tick after a load looks again
            // exactly where the world that was never saved would. The weather is restored above, before this.
        }

        host.Diary.Restore(state.Diary ?? [], state.DiaryVersion);
        host.NightJobs.Restore(state.NightJobs);
        host.UseLoggedPlayerSettings();
        return host;
    }
}

/// <summary>Everything needed to assemble a simulation around a world (content, decider settings, port, locale).</summary>
public sealed class SimulationHostFactory(Content.ContentSet content, Contracts.DeciderSettings settings, IDecisionPort port, string displayLocale, IJobPort? jobPort = null, SimulationHostOptions? options = null)
{
    /// <summary>The content.</summary>
    public Content.ContentSet Content { get; } = content;

    /// <summary>Assembles the systems around <paramref name="world"/>.</summary>
    public SimulationHost Assemble(World world) => Assemble(world, null);

    /// <summary>Assembles the systems around <paramref name="world"/>, on the island of the preset if one is given.</summary>
    public SimulationHost Assemble(World world, string? worldPreset) => Assemble(world, worldPreset, null);

    /// <summary>Assembles the systems around <paramref name="world"/>, on a saved island if one is given (B2 R11).</summary>
    public SimulationHost Assemble(World world, string? worldPreset, (Map.GeneratedIsland Island, int CellSizeCm)? savedIsland) =>
        SimulationHost.Assemble(Content, settings, world, port, displayLocale, worldPreset, jobPort, savedIsland, options);
}
