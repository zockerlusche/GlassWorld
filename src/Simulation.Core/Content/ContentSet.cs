using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Safety;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Content;

/// <summary>
/// All data files the core needs, loaded from a content folder. Every file goes through
/// <see cref="ContractSerializer"/> (schema check before use, ADR-0007); cross-file rules are checked
/// by the receiving types. Files are read in ordinal name order so the result never depends on the
/// file system's enumeration order.
/// </summary>
public sealed class ContentSet
{
    private ContentSet(
        WorldCalendar calendar,
        ObjectCatalog objects,
        CombinationRuleSet combinations,
        IReadOnlyDictionary<VocabularyKind, Vocabulary> vocabularies,
        InhabitantModel inhabitantModel,
        ActionRules actions,
        UtilityBrain brain,
        WorldGenContent worldGen,
        GiftRules giftRules,
        FavorSettings favor,
        InventionRules inventions,
        ChronicleKinds chronicle,
        MessageKinds messageKinds)
    {
        MessageKinds = messageKinds;
        GiftRules = giftRules;
        Favor = favor;
        Inventions = inventions;
        Chronicle = chronicle;
        Calendar = calendar;
        Objects = objects;
        Combinations = combinations;
        Vocabularies = vocabularies;
        InhabitantModel = inhabitantModel;
        Actions = actions;
        Brain = brain;
        WorldGen = worldGen;
    }

    /// <summary>Mailbox message kinds (content/player/message_kinds.json).</summary>
    public MessageKinds MessageKinds { get; }

    /// <summary>Rules for gifts and calls for help (content/player/gift_rules.json).</summary>
    public GiftRules GiftRules { get; }

    /// <summary>Favour: maximum, start options, growth and costs (content/player/favor.json).</summary>
    public FavorSettings Favor { get; }

    /// <summary>World time.</summary>
    public WorldCalendar Calendar { get; }

    /// <summary>Object types and their truth.</summary>
    public ObjectCatalog Objects { get; }

    /// <summary>Combination rules.</summary>
    public CombinationRuleSet Combinations { get; }

    /// <summary>Vocabularies by kind.</summary>
    public IReadOnlyDictionary<VocabularyKind, Vocabulary> Vocabularies { get; }

    /// <summary>How needs, moods and the will to live behave; start ranges (content/inhabitants/model.json).</summary>
    public InhabitantModel InhabitantModel { get; }

    /// <summary>What actions do (content/effects).</summary>
    public ActionRules Actions { get; }

    /// <summary>Routine layer (content/behaviour/utility.json).</summary>
    public UtilityBrain Brain { get; }

    /// <summary>World generator and weather data (content/world, content/climate; B2).</summary>
    public WorldGenContent WorldGen { get; }

    /// <summary>Invention settings, vocabularies and content guard for the core's final check (D471 D5).</summary>
    public InventionRules Inventions { get; }

    /// <summary>Which core events become chronicle entries (content/chronicle/kinds.json).</summary>
    public ChronicleKinds Chronicle { get; }

    /// <summary>Loads and checks the content folder.</summary>
    public static ContentSet Load(string contentDirectory)
    {
        var vocabularies = Files(contentDirectory, "vocabulary")
            .Select(f => ContractSerializer.Deserialize<Vocabulary>(File.ReadAllText(f)))
            .ToDictionary(v => v.Kind);
        var calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(contentDirectory, "world", "time.json")));
        var objects = ObjectCatalog.Create(
            Files(contentDirectory, "objects").Select(f => ContractSerializer.Deserialize<ObjectTypes>(File.ReadAllText(f))),
            Required(vocabularies, VocabularyKind.Property));
        var combinations = CombinationRuleSet.Create(
            Files(contentDirectory, "rules").Select(f => ContractSerializer.Deserialize<CombinationRules>(File.ReadAllText(f))),
            objects,
            Required(vocabularies, VocabularyKind.Action));
        var model = ContractSerializer.Deserialize<InhabitantModel>(File.ReadAllText(Path.Combine(contentDirectory, "inhabitants", "model.json")));
        var needs = Ids(vocabularies, VocabularyKind.Need);
        var moods = Ids(vocabularies, VocabularyKind.Mood);
        CheckModel(model, needs, moods, Ids(vocabularies, VocabularyKind.Trait));
        var actions = ActionRules.Create(
            Files(contentDirectory, "effects").Select(f => ContractSerializer.Deserialize<ActionEffects>(File.ReadAllText(f))),
            objects,
            Required(vocabularies, VocabularyKind.Action),
            needs,
            moods,
            calendar.Settings.TicksPerHour);
        var brain = new UtilityBrain(
            ContractSerializer.Deserialize<UtilityWeights>(File.ReadAllText(Path.Combine(contentDirectory, "behaviour", "utility.json"))),
            actions,
            needs,
            objects.PropertyVocabulary);
        var worldGen = WorldGenContent.Load(contentDirectory, vocabularies, objects, calendar);
        var inventionSettings = ContractSerializer.Deserialize<InventionSettings>(File.ReadAllText(Path.Combine(contentDirectory, "invention", "settings.json")));
        var inventions = new InventionRules(
            inventionSettings,
            objects.PropertyVocabulary,
            Ids(vocabularies, VocabularyKind.Action).ToFrozenSet(StringComparer.Ordinal),
            Ids(vocabularies, VocabularyKind.Process).ToFrozenSet(StringComparer.Ordinal),
            [.. Required(vocabularies, VocabularyKind.Tech_level).Entries.Select(e => e.Id)],
            TextGuard.FromJson(File.ReadAllText(Path.Combine(contentDirectory, "safety", "rules.json"))),
            new BuildRules(
                ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(contentDirectory, "build", "settings.json"))),
                ContractSerializer.Deserialize<BuildKit>(File.ReadAllText(Path.Combine(contentDirectory, "build", "kit.json"))),
                model.Needs.Where(n => n.NeglectBelow > 0).Select(n => n.Id).ToFrozenSet(StringComparer.Ordinal),
                ContractSerializer.Deserialize<JobPhases>(File.ReadAllText(Path.Combine(contentDirectory, "jobs", "phases.json"))).NightDayPhases.ToFrozenSet(StringComparer.Ordinal),
                ContractSerializer.Deserialize<SoundGeneration>(File.ReadAllText(Path.Combine(contentDirectory, "sound", "generation.json")))));
        CheckInventions(inventions, actions, Ids(vocabularies, VocabularyKind.Shape), Ids(vocabularies, VocabularyKind.Time_of_day));
        var chronicle = ContractSerializer.Deserialize<ChronicleKinds>(File.ReadAllText(Path.Combine(contentDirectory, "chronicle", "kinds.json")));
        var player = Path.Combine(contentDirectory, "player");
        return new ContentSet(
            calendar,
            objects,
            combinations,
            vocabularies,
            model,
            actions,
            brain,
            worldGen,
            ContractSerializer.Deserialize<GiftRules>(File.ReadAllText(Path.Combine(player, "gift_rules.json"))),
            ContractSerializer.Deserialize<FavorSettings>(File.ReadAllText(Path.Combine(player, "favor.json"))),
            inventions,
            chronicle,
            ContractSerializer.Deserialize<MessageKinds>(File.ReadAllText(Path.Combine(player, "message_kinds.json"))));
    }

    private static void CheckInventions(InventionRules rules, ActionRules actions, HashSet<string> shapes, HashSet<string> dayPhases)
    {
        var problems = new List<string>();
        problems.AddRange(rules.Settings.ImpliedProperties.Where(p => !rules.Properties.Contains(p)).Select(p => $"unknown implied property '{p}'"));
        var build = rules.Build.Settings;
        foreach (var action in new[] { build.StepAction, build.UseAction })
        {
            if (!actions.Actions.Contains(action, StringComparer.Ordinal))
            {
                problems.Add($"build action '{action}' has no timing in content/effects");
            }
        }

        if (actions.OfferedActions.Contains(build.StepAction, StringComparer.Ordinal))
        {
            problems.Add($"step action '{build.StepAction}' must not be a decider option (decider_option false)");
        }

        problems.AddRange(rules.Build.Kit.Shapes.Where(s => !shapes.Contains(s)).Select(s => $"kit shape '{s}' not in vocabulary 'shape'"));
        problems.AddRange(rules.Build.NightPhases.Where(p => !dayPhases.Contains(p)).Select(p => $"night phase '{p}' not in vocabulary 'time_of_day'"));
        if (!rules.TechLevels.Contains(rules.Settings.TechLevel, StringComparer.Ordinal))
        {
            problems.Add($"unknown tech level '{rules.Settings.TechLevel}'");
        }

        problems.AddRange(rules.Settings.LivingProperties.Where(p => !rules.Properties.Contains(p)).Select(p => $"unknown living property '{p}'"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Invention settings: " + string.Join("; ", problems) + ".");
        }
    }

    private static HashSet<string> Ids(Dictionary<VocabularyKind, Vocabulary> vocabularies, VocabularyKind kind) =>
        Required(vocabularies, kind).Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static void CheckModel(InhabitantModel model, HashSet<string> needs, HashSet<string> moods, HashSet<string> traits)
    {
        var unknown = model.Needs.Select(n => n.Id).Where(id => !needs.Contains(id))
            .Concat(model.Moods.Select(m => m.Id).Where(id => !moods.Contains(id)))
            .Concat(model.MoodDrivers.SelectMany(d => new[] { d.Need, d.Mood }).Where(id => !needs.Contains(id) && !moods.Contains(id)))
            .Concat(model.Start.Traits.Select(r => r.Id).Where(id => !traits.Contains(id)))
            .Concat(model.Start.Needs.Select(r => r.Id).Where(id => !needs.Contains(id)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var badRanges = model.Start.Traits.Concat(model.Start.Needs).Where(r => r.Min > r.Max).Select(r => r.Id).ToList();
        if (unknown.Count > 0 || badRanges.Count > 0)
        {
            throw new InvalidDataException($"Inhabitant model: unknown ids [{string.Join(", ", unknown)}], min > max for [{string.Join(", ", badRanges)}].");
        }
    }

    private static IEnumerable<string> Files(string contentDirectory, string folder) =>
        Directory.GetFiles(Path.Combine(contentDirectory, folder), "*.json").Order(StringComparer.Ordinal);

    private static Vocabulary Required(Dictionary<VocabularyKind, Vocabulary> vocabularies, VocabularyKind kind) =>
        vocabularies.TryGetValue(kind, out var vocabulary)
            ? vocabulary
            : throw new InvalidDataException($"Vocabulary '{kind}' is missing.");
}
