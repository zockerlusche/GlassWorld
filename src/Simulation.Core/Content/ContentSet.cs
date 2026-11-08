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
        WritingMedia writing,
        SkillPractice skillPractice,
        MessageKinds messageKinds)
    {
        MessageKinds = messageKinds;
        GiftRules = giftRules;
        Favor = favor;
        Inventions = inventions;
        Chronicle = chronicle;
        Writing = writing;
        SkillPractice = skillPractice;
        Calendar = calendar;
        Objects = objects;
        Combinations = combinations;
        Vocabularies = vocabularies;
        InhabitantModel = inhabitantModel;
        Actions = actions;
        Brain = brain;
        WorldGen = worldGen;
    }

    /// <summary>Value ranges of the appearance record (content/look/appearance.json, B2 R27).</summary>
    public LookAppearance Appearance { get; private init; } = null!;

    /// <summary>Mailbox message kinds (content/player/message_kinds.json).</summary>
    public MessageKinds MessageKinds { get; }

    /// <summary>Knowledge with its source, trust and passing on (content/knowledge/settings.json, B4a R3/R4/R7).</summary>
    public KnowledgeSettings Knowledge { get; private init; } = null!;

    /// <summary>Event kinds the core may report (vocabulary <c>event</c>); reporting another kind is a data error.</summary>
    public FrozenSet<string> EventKinds { get; private init; } = FrozenSet<string>.Empty;

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

    /// <summary>Walking speed and gaits (content/behaviour/gaits.json, F5264).</summary>
    public GaitTable Gaits { get; private init; } = null!;

    /// <summary>World generator and weather data (content/world, content/climate; B2).</summary>
    public WorldGenContent WorldGen { get; }

    /// <summary>Invention settings, vocabularies and content guard for the core's final check (D471 D5).</summary>
    public InventionRules Inventions { get; }

    /// <summary>Which core events become chronicle entries (content/chronicle/kinds.json).</summary>
    public ChronicleKinds Chronicle { get; }

    /// <summary>Writing carriers and what write/read need (content/writing/media.json, B8 R13-R15).</summary>
    public WritingMedia Writing { get; }

    /// <summary>Which action practises which skill (content/skills/practice.json, B5 R18).</summary>
    public SkillPractice SkillPractice { get; }

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
            objects.PropertyVocabulary,
            Required(vocabularies, VocabularyKind.Action).Entries.Where(e => (e.Target ?? VocabularyEntryTarget.None) == VocabularyEntryTarget.None).Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal));
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
        var writing = ContractSerializer.Deserialize<WritingMedia>(File.ReadAllText(Path.Combine(contentDirectory, "writing", "media.json")));
        CheckWriting(writing, Ids(vocabularies, VocabularyKind.Writing_medium), Ids(vocabularies, VocabularyKind.Weather), Ids(vocabularies, VocabularyKind.Property), Ids(vocabularies, VocabularyKind.Trait), Ids(vocabularies, VocabularyKind.Biome));
        var skillPractice = ContractSerializer.Deserialize<SkillPractice>(File.ReadAllText(Path.Combine(contentDirectory, "skills", "practice.json")));
        CheckSkills(model, skillPractice, Ids(vocabularies, VocabularyKind.Skill), Ids(vocabularies, VocabularyKind.Action));
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
            writing,
            skillPractice,
            ContractSerializer.Deserialize<MessageKinds>(File.ReadAllText(Path.Combine(player, "message_kinds.json"))))
        {
            Appearance = LoadAppearance(contentDirectory),
            Gaits = GaitTable.Create(
                ContractSerializer.Deserialize<GaitSettings>(File.ReadAllText(Path.Combine(contentDirectory, "behaviour", "gaits.json"))),
                Ids(vocabularies, VocabularyKind.Action),
                brain.Weights,
                model),
            Knowledge = LoadKnowledge(contentDirectory, vocabularies, objects),
            EventKinds = Required(vocabularies, VocabularyKind.Event).Entries.Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal),
        };
    }

    /// <summary>Every carrier of the vocabulary has exactly one entry; all ids it uses are known (B8 R13).</summary>
    private static void CheckWriting(WritingMedia writing, HashSet<string> media, HashSet<string> weather, HashSet<string> properties, HashSet<string> traits, HashSet<string> biomes)
    {
        var problems = new List<string>();
        var defined = writing.Media.Select(m => m.Id).ToList();
        problems.AddRange(defined.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"carrier '{g.Key}' twice"));
        problems.AddRange(defined.Where(id => !media.Contains(id)).Select(id => $"unknown carrier '{id}'"));
        problems.AddRange(media.Where(id => !defined.Contains(id, StringComparer.Ordinal)).Select(id => $"carrier '{id}' has no entry"));
        problems.AddRange(writing.Media.SelectMany(m => m.WeatherLoss).Where(w => !weather.Contains(w.Weather)).Select(w => $"unknown weather '{w.Weather}'"));
        problems.AddRange(writing.Media.SelectMany(m => m.WeatherLoss.GroupBy(w => w.Weather, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"carrier '{m.Id}' names weather '{g.Key}' twice")));
        problems.AddRange(writing.Media.SelectMany(m => m.ToolProperties).Where(p => !properties.Contains(p)).Select(p => $"unknown tool property '{p}'"));
        problems.AddRange(writing.Media.SelectMany(m => m.Carrier.Properties).Where(p => !properties.Contains(p)).Select(p => $"unknown carrier property '{p}'"));
        problems.AddRange(writing.Media.SelectMany(m => m.Carrier.Biomes).Where(b => !biomes.Contains(b)).Select(b => $"unknown carrier biome '{b}'"));
        foreach (var m in writing.Media)
        {
            // Ground takes marks only in named biomes; objects are chosen by properties only.
            if (m.Carrier.Surface == CarrierSurface.Ground && (m.Carrier.Biomes.Count == 0 || m.Carrier.Properties.Count > 0))
            {
                problems.Add($"carrier '{m.Id}' on the ground needs biomes and no properties");
            }

            if (m.Carrier.Surface == CarrierSurface.Object && m.Carrier.Biomes.Count > 0)
            {
                problems.Add($"carrier '{m.Id}' on objects names biomes");
            }
        }

        if (!traits.Contains(writing.Read.CuriosityTrait))
        {
            problems.Add($"unknown trait '{writing.Read.CuriosityTrait}'");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Writing media: " + string.Join("; ", problems) + ".");
        }
    }

    /// <summary>The register names every skill of the vocabulary once; practice uses known actions and skills (A1372).</summary>
    private static void CheckSkills(InhabitantModel model, SkillPractice practice, HashSet<string> skills, HashSet<string> actions)
    {
        var problems = new List<string>();
        var registered = model.Skills.Select(s => s.Id).ToList();
        problems.AddRange(registered.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"skill '{g.Key}' twice"));
        problems.AddRange(registered.Where(id => !skills.Contains(id)).Select(id => $"unknown skill '{id}'"));
        problems.AddRange(skills.Where(id => !registered.Contains(id, StringComparer.Ordinal)).Select(id => $"skill '{id}' is not in the register"));
        problems.AddRange(model.Skills.Where(s => s.StartLevelMin > s.StartLevelMax).Select(s => $"skill '{s.Id}' has min above max"));
        problems.AddRange(practice.Practice.Where(p => !skills.Contains(p.Skill)).Select(p => $"practice of unknown skill '{p.Skill}'"));
        problems.AddRange(practice.Practice.Where(p => !actions.Contains(p.Action)).Select(p => $"practice by unknown action '{p.Action}'"));
        if (model.SkillRules.RuntimeStartLevelMin > model.SkillRules.RuntimeStartLevelMax)
        {
            problems.Add("new skills: start level min above max");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Skills: " + string.Join("; ", problems) + ".");
        }
    }

    private static KnowledgeSettings LoadKnowledge(string contentDirectory, Dictionary<VocabularyKind, Vocabulary> vocabularies, ObjectCatalog objects)
    {
        var data = ContractSerializer.Deserialize<KnowledgeSettings>(File.ReadAllText(Path.Combine(contentDirectory, "knowledge", "settings.json")));
        var actions = Required(vocabularies, VocabularyKind.Action).Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var passing in data.Passing)
        {
            if (!actions.TryGetValue(passing.Action, out var action) || action.Target != VocabularyEntryTarget.Inhabitant)
            {
                problems.Add($"passing action '{passing.Action}' is not an action with an inhabitant target");
            }

            problems.AddRange(passing.Properties.Where(p => !objects.PropertyVocabulary.Contains(p)).Select(p => $"passing action '{passing.Action}' names unknown property '{p}'"));
        }

        problems.AddRange(data.WaitWhileQuestionOpen.Where(a => !actions.ContainsKey(a)).Select(a => $"unknown action '{a}' in wait_while_question_open"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Knowledge settings: " + string.Join("; ", problems) + ".");
        }

        return data;
    }

    private static LookAppearance LoadAppearance(string contentDirectory)
    {
        var data = ContractSerializer.Deserialize<LookAppearance>(File.ReadAllText(Path.Combine(contentDirectory, "look", "appearance.json")));
        var problems = new List<string>();
        if (data.HeightCm.Min > data.HeightCm.Max || data.BuildPermille.Min > data.BuildPermille.Max || data.HeadScale.Min > data.HeadScale.Max)
        {
            problems.Add("a range has min > max");
        }

        problems.AddRange(data.Shapes.Where(s => s.Min > s.Max).Select(s => $"shape '{s.Id}' has min > max"));
        if (data.AgeStages.Count == 0 || data.SkinTones.Count == 0 || data.HairStyles.Count == 0 || data.HairColors.Count == 0
            || data.ClothingStyles.Count == 0 || data.ClothingColors.Count == 0)
        {
            problems.Add("every list needs at least one value");
        }

        return problems.Count == 0 ? data : throw new InvalidDataException("Appearance data: " + string.Join("; ", problems) + ".");
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
