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

    /// <summary>Look epochs (content/look/epochs.json, B2-5 R16): the core checks epoch commands against them.</summary>
    public LookEpochs LookEpochs { get; private init; } = null!;

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

    /// <summary>The learning log per game day (content/learning/log.json, A1620), or null without the file.</summary>
    public LearningLogSettings? LearningLog { get; init; }

    /// <summary>Which core events become chronicle entries (content/chronicle/kinds.json).</summary>
    public ChronicleKinds Chronicle { get; }

    /// <summary>Writing carriers and what write/read need (content/writing/media.json, B8 R13-R15).</summary>
    public WritingMedia Writing { get; }

    /// <summary>Which action practises which skill (content/skills/practice.json, B5 R18).</summary>
    public SkillPractice SkillPractice { get; }

    /// <summary>Talking with an inhabitant: limits and changes by outcome (content/player/conversation.json, B6 R13).</summary>
    public ConversationSettings Conversation { get; init; } = new();

    /// <summary>The basic processing rules (content/processes/rules.json, B2537) as a combine trial applies them (B2687 K2); null without the file.</summary>
    public ProcessRuleSet? Processes { get; init; }

    /// <summary>The nature takt (content/nature/settings.json, A1380); null without the file.</summary>
    public NatureSettings? Nature { get; init; }

    /// <summary>How long things last by material class (content/durability/settings.json, D711 section 5); null without the file.</summary>
    public DurabilitySettings? Durability { get; init; }

    /// <summary>Drying, spreading and remains of fire (B5-10, D1084); null = fires only burn down or go out.</summary>
    public FireSettings? Fire { get; init; }

    /// <summary>The milestones of the world (content/milestones/catalog.json, B2598); null = none.</summary>
    public MilestoneCatalog? Milestones { get; init; }

    /// <summary>Talent, forgetting and maximum of skill levels per inhabitant (content/skills/levels.json).</summary>
    public SkillLevelSettings SkillLevels { get; init; } = new();

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
        var conversation = ContractSerializer.Deserialize<ConversationSettings>(File.ReadAllText(Path.Combine(contentDirectory, "player", "conversation.json")));
        if (conversation.Praise is { } praise && !moods.Contains(praise.Mood))
        {
            // A1538 (review GW2 #345): a typo would silently skip the mood.
            throw new InvalidDataException($"conversation praise names unknown mood '{praise.Mood}'");
        }

        var actionIds = Ids(vocabularies, VocabularyKind.Action);
        if (model.Diary?.LeftOutActions?.FirstOrDefault(a => !actionIds.Contains(a)) is { } unknownAction)
        {
            throw new InvalidDataException($"Inhabitant model: diary.left_out_actions names unknown action '{unknownAction}'.");
        }

        CheckComfortZone(model.ComfortZone, needs, actionIds);

        var actions = ActionRules.Create(
            Files(contentDirectory, "effects").Select(f => ContractSerializer.Deserialize<ActionEffects>(File.ReadAllText(f))),
            objects,
            Required(vocabularies, VocabularyKind.Action),
            needs,
            moods,
            calendar.Settings.TicksPerHour,
            Ids(vocabularies, VocabularyKind.Event));
        var knowledge = LoadKnowledge(contentDirectory, vocabularies, objects, actions, model);
        var brain = new UtilityBrain(
            ContractSerializer.Deserialize<UtilityWeights>(File.ReadAllText(Path.Combine(contentDirectory, "behaviour", "utility.json"))),
            actions,
            needs,
            objects.PropertyVocabulary,
            Required(vocabularies, VocabularyKind.Action).Entries.Where(e => (e.Target ?? VocabularyEntryTarget.None) == VocabularyEntryTarget.None).Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal),
            Ids(vocabularies, VocabularyKind.Trait),
            model.Needs.Where(n => n.NeglectBelow > 0).ToFrozenDictionary(n => n.Id, n => n.NeglectBelow, StringComparer.Ordinal),
            Familiarity.From(model.Togetherness, knowledge.RelationshipStart),
            Required(vocabularies, VocabularyKind.Action).Entries.Where(e => e.Target == VocabularyEntryTarget.Inhabitant).Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal));
        if (brain.AimsAtInhabitants && brain.Familiarity is null)
        {
            // Who is familiar and who is asleep is defined once, by togetherness (W1744).
            throw new InvalidDataException("Utility weights: a consideration with target_inhabitant needs the togetherness section of the inhabitant model.");
        }

        var gaits = ContractSerializer.Deserialize<GaitSettings>(File.ReadAllText(Path.Combine(contentDirectory, "behaviour", "gaits.json")));
        var worldGen = WorldGenContent.Load(contentDirectory, vocabularies, objects, calendar, gaits.Spacing?.MinSeparationM ?? 0);
        var jobPhases = ContractSerializer.Deserialize<JobPhases>(File.ReadAllText(Path.Combine(contentDirectory, "jobs", "phases.json")));
        var inventionSettings = ContractSerializer.Deserialize<InventionSettings>(File.ReadAllText(Path.Combine(contentDirectory, "invention", "settings.json")));
        var inventions = new InventionRules(
            inventionSettings,
            objects.PropertyVocabulary,
            // An action that exists only in one situation (only_when, e.g. a stuck building) is no invention's purpose.
            Required(vocabularies, VocabularyKind.Action).Entries.Where(e => e.OnlyWhen is null).Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal),
            Ids(vocabularies, VocabularyKind.Process).ToFrozenSet(StringComparer.Ordinal),
            [.. Required(vocabularies, VocabularyKind.Tech_level).Entries.Select(e => e.Id)],
            TextGuard.FromJson(File.ReadAllText(Path.Combine(contentDirectory, "safety", "rules.json"))),
            new BuildRules(
                ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(contentDirectory, "build", "settings.json"))),
                ContractSerializer.Deserialize<BuildKit>(File.ReadAllText(Path.Combine(contentDirectory, "build", "kit.json"))),
                model.Needs.Where(n => n.NeglectBelow > 0).Select(n => n.Id).ToFrozenSet(StringComparer.Ordinal),
                jobPhases.NightDayPhases.ToFrozenSet(StringComparer.Ordinal),
                ContractSerializer.Deserialize<SoundGeneration>(File.ReadAllText(Path.Combine(contentDirectory, "sound", "generation.json"))),
                ContractSerializer.Deserialize<ImageGeneration>(File.ReadAllText(Path.Combine(contentDirectory, "image", "generation.json"))),
                jobPhases.WaitForSleepersHours));
        // GW2 D971: what a thing is by being made is not assumed for a found thing.
        brain.MadeProperties = inventions.Settings.ImpliedProperties.ToFrozenSet(StringComparer.Ordinal);
        CheckInventions(inventions, actions, Ids(vocabularies, VocabularyKind.Shape), Ids(vocabularies, VocabularyKind.Time_of_day));
        var chronicle = ContractSerializer.Deserialize<ChronicleKinds>(File.ReadAllText(Path.Combine(contentDirectory, "chronicle", "kinds.json")));
        var events = Ids(vocabularies, VocabularyKind.Event);
        var unknownEvents = chronicle.Kinds.Where(k => !events.Contains(k.Event)).Select(k => $"kind '{k.Id}' names unknown event '{k.Event}'").ToList();
        if (unknownEvents.Count > 0)
        {
            // The event vocabulary is the contract with the core: a kind for an event nobody can report is a typo.
            throw new InvalidDataException("Chronicle kinds: " + string.Join("; ", unknownEvents) + ".");
        }
        var player = Path.Combine(contentDirectory, "player");
        var writing = ContractSerializer.Deserialize<WritingMedia>(File.ReadAllText(Path.Combine(contentDirectory, "writing", "media.json")));
        CheckWriting(writing, Ids(vocabularies, VocabularyKind.Writing_medium), Ids(vocabularies, VocabularyKind.Weather), Ids(vocabularies, VocabularyKind.Property), Ids(vocabularies, VocabularyKind.Trait), Ids(vocabularies, VocabularyKind.Biome));
        var skillPractice = ContractSerializer.Deserialize<SkillPractice>(File.ReadAllText(Path.Combine(contentDirectory, "skills", "practice.json")));
        CheckSkills(model, skillPractice, Ids(vocabularies, VocabularyKind.Skill), Ids(vocabularies, VocabularyKind.Action));
        var names = LoadNames(contentDirectory);
        var processes = File.Exists(Path.Combine(contentDirectory, "processes", "rules.json"))
            ? ProcessRuleSet.Create(
                ContractSerializer.Deserialize<ProcessRules>(File.ReadAllText(Path.Combine(contentDirectory, "processes", "rules.json"))),
                objects,
                inventions.Processes,
                inventions.TechLevels,
                inventions.Settings.TechLevel)
            : null;
        var milestones = LoadMilestones(contentDirectory, Ids(vocabularies, VocabularyKind.Property), [.. combinations.RuleIds, .. processes?.RuleIds ?? []], inventions.TechLevels);
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
            SkillLevels = LoadSkillLevels(contentDirectory),
            LearningLog = LoadLearningLog(contentDirectory, events),
            Conversation = conversation,
            Processes = processes,
            Milestones = milestones,
            Fire = File.Exists(Path.Combine(contentDirectory, "fire", "settings.json"))
                ? ContractSerializer.Deserialize<FireSettings>(File.ReadAllText(Path.Combine(contentDirectory, "fire", "settings.json")))
                : null,
            Durability = File.Exists(Path.Combine(contentDirectory, "durability", "settings.json"))
                ? ContractSerializer.Deserialize<DurabilitySettings>(File.ReadAllText(Path.Combine(contentDirectory, "durability", "settings.json")))
                : null,
            Nature = File.Exists(Path.Combine(contentDirectory, "nature", "settings.json"))
                ? ContractSerializer.Deserialize<NatureSettings>(File.ReadAllText(Path.Combine(contentDirectory, "nature", "settings.json")))
                : null,
            Appearance = LoadAppearance(
                contentDirectory,
                [.. calendar.Settings.Seasons.Select(s => s.Id)],
                [.. worldGen.Climates.Keys],
                Ids(vocabularies, VocabularyKind.Biome),
                [.. inventions.TechLevels]),
            Gaits = GaitTable.Create(
                gaits,
                Ids(vocabularies, VocabularyKind.Action),
                brain.Weights,
                model),
            Knowledge = knowledge,
            EventKinds = Required(vocabularies, VocabularyKind.Event).Entries.Select(e => e.Id).ToFrozenSet(StringComparer.Ordinal),
            InhabitantNames = names.All,
            InhabitantNamesBySex = names.BySex,
            LookEpochs = LoadLookEpochs(contentDirectory, Ids(vocabularies, VocabularyKind.Epoch), inventions.TechLevels.Count),
        };
    }

    /// <summary>
    /// Reads the look epochs: at least one, every id once and in the vocabulary <c>epoch</c>, and a
    /// <c>tech_level</c> that is a position in the vocabulary <c>tech_level</c> (first = 0).
    /// </summary>
    private static LearningLogSettings? LoadLearningLog(string contentDirectory, HashSet<string> events)
    {
        var path = Path.Combine(contentDirectory, "learning", "log.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var settings = ContractSerializer.Deserialize<LearningLogSettings>(File.ReadAllText(path));
        var unknown = settings.Kinds.Where(k => !events.Contains(k.Event)).Select(k => k.Event).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException("Learning log names unknown events: " + string.Join(", ", unknown) + ".");
        }

        return settings;
    }

    private static LookEpochs LoadLookEpochs(string contentDirectory, HashSet<string> epochs, int techLevels)
    {
        var data = ContractSerializer.Deserialize<LookEpochs>(File.ReadAllText(Path.Combine(contentDirectory, "look", "epochs.json")));
        var problems = new List<string>();
        if (data.Epochs.Count == 0)
        {
            problems.Add("at least one epoch is needed");
        }

        problems.AddRange(data.Epochs.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"epoch '{g.Key}' twice"));
        problems.AddRange(data.Epochs.Where(e => !epochs.Contains(e.Id)).Select(e => $"epoch '{e.Id}' not in vocabulary 'epoch'"));
        problems.AddRange(data.Epochs.Where(e => e.TechLevel < 0 || e.TechLevel >= techLevels).Select(e => $"epoch '{e.Id}' names tech level {e.TechLevel}, the vocabulary 'tech_level' has {techLevels}"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Look epochs: " + string.Join("; ", problems) + ".");
        }

        return data;
    }

    /// <summary>
    /// Own names of the inhabitants (content/inhabitants/names.json, W1698), all of them: those that fit anybody,
    /// then the female and the male ones; empty without the file.
    /// </summary>
    public IReadOnlyList<string> InhabitantNames { get; private init; } = [];

    /// <summary>The names only for one sex (<c>female</c>, <c>male</c>; A1557, D809 M2); empty without such lists.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> InhabitantNamesBySex { get; private init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Reads the name lists; every name must be a ProperName (^[A-Z][a-z]{1,15}$) and appear only once in all
    /// lists together.
    /// </summary>
    private static (List<string> All, Dictionary<string, IReadOnlyList<string>> BySex) LoadNames(string contentDirectory)
    {
        var bySex = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var path = Path.Combine(contentDirectory, "inhabitants", "names.json");
        if (!File.Exists(path))
        {
            return ([], bySex);
        }

        var data = ContractSerializer.Deserialize<ProperNames>(File.ReadAllText(path));

        // The keys are the sex ids of the inhabitant model (inhabitant-model.schema.json: female, male). A new sex
        // id there needs its list here too; without one, its inhabitants pick from all names (review GW1 #267).
        if (data.Female is { Count: > 0 } female)
        {
            bySex["female"] = [.. female];
        }

        if (data.Male is { Count: > 0 } male)
        {
            bySex["male"] = [.. male];
        }

        List<string> names = [.. data.Names, .. bySex.Values.SelectMany(n => n)];
        var bad = names.Where(n => !IsProperName(n)).ToList();
        if (bad.Count > 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new InvalidDataException("Inhabitant names: every name must match ^[A-Z][a-z]{1,15}$ and appear once in all lists" + (bad.Count > 0 ? " ('" + string.Join("', '", bad) + "')." : "."));
        }

        return (names, bySex);
    }

    private static bool IsProperName(string name) =>
        name.Length is >= 2 and <= 16 && name[0] is >= 'A' and <= 'Z' && name.Skip(1).All(c => c is >= 'a' and <= 'z');

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

    private static SkillLevelSettings LoadSkillLevels(string contentDirectory)
    {
        var levels = ContractSerializer.Deserialize<SkillLevelSettings>(File.ReadAllText(Path.Combine(contentDirectory, "skills", "levels.json")));
        if (levels.TalentMin > levels.TalentMax)
        {
            throw new InvalidDataException($"Skill levels: talent_min {levels.TalentMin} above talent_max {levels.TalentMax}.");
        }

        return levels;
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

    private static KnowledgeSettings LoadKnowledge(string contentDirectory, Dictionary<VocabularyKind, Vocabulary> vocabularies, ObjectCatalog objects, ActionRules effects, InhabitantModel model)
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
        if (data.Naming is { } naming && (!actions.TryGetValue(naming.Action, out var namingAction) || namingAction.Target != VocabularyEntryTarget.Inhabitant || data.Passing.Any(p => p.Action == naming.Action)))
        {
            problems.Add($"naming action '{naming.Action}' is not an action with an inhabitant target, or is also a passing action");
        }

        if (data.Looking is { } looking)
        {
            // What a thing is by being made (the invention data's implied properties) is seen: a made thing looks made (TL #23489).
            var made = ContractSerializer.Deserialize<InventionSettings>(File.ReadAllText(Path.Combine(contentDirectory, "invention", "settings.json"))).ImpliedProperties;
            problems.AddRange(LookingProblems(looking, data, actions, objects, effects, made));
        }

        problems.AddRange(SocialProblems(data, actions, effects, model));
        problems.AddRange(MemoryProblems(data, actions, Ids(vocabularies, VocabularyKind.Event), model));

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Knowledge settings: " + string.Join("; ", problems) + ".");
        }

        return data;
    }

    /// <summary>
    /// Memory and night questions (B4a R5, B7): the limits fit each other and the job contract; every
    /// remembered event names a known event, known actions and, for an effect, a known need or mood with a
    /// bound; a trigger is listed for at most one question.
    /// </summary>
    private static IEnumerable<string> MemoryProblems(KnowledgeSettings data, Dictionary<string, VocabularyEntry> actions, HashSet<string> events, InhabitantModel model)
    {
        foreach (var twice in (data.NightQuestions?.Questions ?? []).GroupBy(q => q.Trigger).Where(g => g.Count() > 1))
        {
            yield return $"night_questions lists trigger '{twice.Key.ToString().ToLowerInvariant()}' twice";
        }

        if (data.Memory is not { } memory)
        {
            yield break;
        }

        if (memory.JobMaxFormative > memory.JobMaxEvents)
        {
            yield return "memory.job_max_formative is above job_max_events";
        }

        if (memory.MaxEvents < memory.CondenseAt)
        {
            yield return "memory.max_events is below condense_at (the memory would never be condensed)";
        }

        foreach (var entry in memory.Events)
        {
            if (!events.Contains(entry.Event))
            {
                yield return $"memory event '{entry.Event}' is not in vocabulary 'event'";
            }

            if (entry.Kind is not null && entry.ActionAsKind == true)
            {
                yield return $"memory event '{entry.Event}' has both kind and action_as_kind";
            }

            foreach (var action in (entry.LeftOutActions ?? []).Where(a => !actions.ContainsKey(a)))
            {
                yield return $"memory event '{entry.Event}' leaves out unknown action '{action}'";
            }

            foreach (var outcome in (entry.EverydayOutcomes ?? []).Where(o => !Inhabitants.ActionOutcomes.All.Contains(o, StringComparer.Ordinal)))
            {
                yield return $"memory event '{entry.Event}' names unknown outcome '{outcome}' in everyday_outcomes";
            }

            if (entry.Effect is not { } effect)
            {
                continue;
            }

            if (effect.DeltaAtMost is null && effect.DeltaAtLeast is null)
            {
                yield return $"memory event '{entry.Event}' has an effect without delta_at_most or delta_at_least";
            }

            if (effect.Id is { } id
                && ((effect.Kind == KnowledgeMemoryEffectKind.Need && !model.Needs.Any(n => n.Id == id))
                    || (effect.Kind == KnowledgeMemoryEffectKind.Mood && !model.Moods.Any(m => m.Id == id))))
            {
                yield return $"memory event '{entry.Event}' names unknown {effect.Kind.ToString().ToLowerInvariant()} '{id}'";
            }
        }
    }

    /// <summary>
    /// Meeting, relationships and imprinting (B4a R6-R8): meeting actions are offered inhabitant actions that are
    /// neither passing nor naming, with a known need; relationship actions aim at inhabitants; strangers start
    /// below the togetherness thresholds (an unknown human is not company); imprinting names known actions.
    /// </summary>
    private static IEnumerable<string> SocialProblems(KnowledgeSettings data, Dictionary<string, VocabularyEntry> actions, ActionRules effects, InhabitantModel model)
    {
        bool AtInhabitant(string action) => actions.TryGetValue(action, out var entry) && entry.Target == VocabularyEntryTarget.Inhabitant;
        foreach (var option in data.Meeting?.Options ?? [])
        {
            if (!AtInhabitant(option.Action) || !effects.OfferedActions.Contains(option.Action, StringComparer.Ordinal)
                || data.Passing.Any(p => p.Action == option.Action) || data.Naming?.Action == option.Action)
            {
                yield return $"meeting action '{option.Action}' is not an offered action with an inhabitant target, or is also a passing or naming action";
            }

            if (option.Need is { } need && !model.Needs.Any(n => n.Id == need))
            {
                yield return $"meeting action '{option.Action}' names unknown need '{need}'";
            }
        }

        foreach (var rule in data.RelationshipChanges?.Actions ?? [])
        {
            if (!AtInhabitant(rule.Action))
            {
                yield return $"relationship action '{rule.Action}' is not an action with an inhabitant target";
            }
        }

        if (data.StartRelationships is { } starts && model.Togetherness is { } together
            && starts.Strangers.Trust >= together.MinTrust && starts.Strangers.Affection >= together.MinAffection)
        {
            yield return "strangers start as familiar (start_relationships.strangers reach the togetherness thresholds of the inhabitant model)";
        }

        if (data.Imprinting is { } imprinting)
        {
            foreach (var action in imprinting.LeftOutActions.Where(a => !actions.ContainsKey(a)))
            {
                yield return $"unknown action '{action}' in imprinting.left_out_actions";
            }

            if (imprinting.FullAt < imprinting.HabitFrom)
            {
                yield return "imprinting.full_at is below habit_from";
            }
        }
    }

    /// <summary>
    /// Looking (TL #19921) teaches what can be seen, never an effect: no visible property may be one an
    /// action's effect depends on - except what a thing is by being made (implied properties): a made thing looks made; what is seen is less sure than what is tried, a guess at most guessed_cap.
    /// </summary>
    private static IEnumerable<string> LookingProblems(KnowledgeLooking looking, KnowledgeSettings data, Dictionary<string, VocabularyEntry> actions, ObjectCatalog objects, ActionRules effects, ICollection<string> made)
    {
        if (!actions.TryGetValue(looking.Action, out var action) || action.Target != VocabularyEntryTarget.Object)
        {
            yield return $"looking action '{looking.Action}' is not an action with an object target";
        }

        var effectProperties = effects.Actions.SelectMany(a => effects.Rules(a)).Select(r => r.RequiresProperty).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var property in looking.VisibleProperties)
        {
            if (!objects.PropertyVocabulary.Contains(property))
            {
                yield return $"unknown visible property '{property}'";
            }
            else if (effectProperties.Contains(property) && !made.Contains(property))
            {
                yield return $"visible property '{property}' is an effect (learned only by trying, being told or asking)";
            }
        }

        foreach (var guess in looking.Guesses)
        {
            if (!looking.VisibleProperties.Contains(guess.Seen) || !objects.PropertyVocabulary.Contains(guess.Property))
            {
                yield return $"guess '{guess.Seen}' -> '{guess.Property}' needs a visible property and a known property";
            }
        }

        if (looking.InspectConfidence >= data.ExperiencedConfidence)
        {
            yield return "inspect_confidence must be below experienced_confidence";
        }

        if (looking.GuessedConfidence > data.GuessedCap)
        {
            yield return "guessed_confidence must not exceed guessed_cap";
        }
    }

    /// <summary>Renamed ids (F4) point to a current id and are no longer current themselves.</summary>
    private static IEnumerable<string> Renames(ICollection<AppearanceRename>? renamed, IEnumerable<string> current, string what)
    {
        var ids = current.ToHashSet(StringComparer.Ordinal);
        foreach (var r in renamed ?? [])
        {
            if (!ids.Contains(r.NewId))
            {
                yield return $"renamed {what} '{r.OldId}' points to unknown '{r.NewId}'";
            }

            if (ids.Contains(r.OldId))
            {
                yield return $"renamed {what} '{r.OldId}' is still a current id";
            }
        }
    }

    private static LookAppearance LoadAppearance(string contentDirectory, HashSet<string> seasons, HashSet<string> climates, HashSet<string> biomes, HashSet<string> techLevels)
    {
        var data = ContractSerializer.Deserialize<LookAppearance>(File.ReadAllText(Path.Combine(contentDirectory, "look", "appearance.json")));
        var problems = new List<string>();
        if (data.HeightCm.Min > data.HeightCm.Max || data.BuildPermille.Min > data.BuildPermille.Max || data.HeadScale.Min > data.HeadScale.Max)
        {
            problems.Add("a range has min > max");
        }

        problems.AddRange(data.Shapes.Where(s => s.Min > s.Max).Select(s => $"shape '{s.Id}' has min > max"));
        if (data.AgeStages.Count == 0 || data.SkinTones.Count == 0 || data.HairStyles.Count == 0 || data.HairColors.Count == 0
            || data.Outfits.Count == 0 || data.ClothingColors.Count == 0)
        {
            problems.Add("every list needs at least one value");
        }

        // Outfits (F4): known ids in the conditions, and one outfit fits everywhere, so nobody is undressed.
        problems.AddRange(Twice(data.HairStyles.Select(h => h.Id), "hair style"));
        problems.AddRange(Twice(data.Outfits.Select(o => o.Id), "outfit"));
        problems.AddRange(Renames(data.RenamedHairStyles, data.HairStyles.Select(h => h.Id), "hair style"));
        problems.AddRange(Renames(data.RenamedOutfits, data.Outfits.Select(o => o.Id), "outfit"));
        if (!data.Outfits.Any(o => o.When is null))
        {
            problems.Add("no outfit without conditions (someone could find no clothes)");
        }

        foreach (var outfit in data.Outfits.Where(o => o.When is not null))
        {
            var when = outfit.When!;
            problems.AddRange(Unknown(when.Seasons, seasons, outfit.Id, "season"));
            problems.AddRange(Unknown(when.Climates, climates, outfit.Id, "climate"));
            problems.AddRange(Unknown(when.Biomes, biomes, outfit.Id, "biome"));
            problems.AddRange(Unknown(when.TechLevels, techLevels, outfit.Id, "tech level"));
        }

        // Sex proportions (F4): known stages, sane ranges, no clash with the body shapes.
        var stages = data.AgeStages.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(data.SexShapes.Where(s => s.Min > s.Max).Select(s => $"sex shape '{s.Id}' has min > max"));
        problems.AddRange(data.SexShapes.SelectMany(s => s.AgeStages).Where(a => !stages.Contains(a)).Distinct().Select(a => $"sex shape names unknown age stage '{a}'"));
        problems.AddRange(data.SexShapes.Where(s => data.Shapes.Any(b => b.Id == s.Id)).Select(s => $"sex shape '{s.Id}' is also a body shape"));

        return problems.Count == 0 ? data : throw new InvalidDataException("Appearance data: " + string.Join("; ", problems) + ".");
    }

    private static IEnumerable<string> Twice(IEnumerable<string> ids, string what) =>
        ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{what} '{g.Key}' twice");

    private static IEnumerable<string> Unknown(ICollection<string>? ids, HashSet<string> known, string outfit, string what) =>
        (ids ?? []).Where(id => !known.Contains(id)).Select(id => $"outfit '{outfit}' names unknown {what} '{id}'");

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

        if (build.Escalation is { } escalation)
        {
            foreach (var action in new[] { escalation.UseInsteadAction, escalation.SmallerAction, escalation.GiveUpAction, escalation.HelpAction }.OfType<string>())
            {
                if (!actions.Actions.Contains(action, StringComparer.Ordinal))
                {
                    problems.Add($"escalation action '{action}' has no timing in content/effects");
                }
                else if (actions.OfferedActions.Contains(action, StringComparer.Ordinal))
                {
                    problems.Add($"escalation action '{action}' must not be a decider option (decider_option false): only a stuck building offers it");
                }
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

    /// <summary>
    /// The milestones (B2598), if the file exists: known properties, rules and tech levels, unique ids, and exactly
    /// one of made_with and rule per milestone.
    /// </summary>
    private static MilestoneCatalog? LoadMilestones(string contentDirectory, HashSet<string> properties, HashSet<string> rules, IReadOnlyList<string> techLevels)
    {
        var path = Path.Combine(contentDirectory, "milestones", "catalog.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var catalog = ContractSerializer.Deserialize<MilestoneCatalog>(File.ReadAllText(path));
        var problems = new List<string>();
        foreach (var milestone in catalog.Milestones)
        {
            var made = milestone.MadeWith is { Count: > 0 };
            if (made == milestone.Rule is not null)
            {
                problems.Add($"'{milestone.Id}' needs exactly one of made_with and rule");
            }

            problems.AddRange((milestone.MadeWith ?? []).Where(p => !properties.Contains(p)).Select(p => $"'{milestone.Id}' names unknown property '{p}'"));
            if (milestone.Rule is { } rule && !rules.Contains(rule))
            {
                problems.Add($"'{milestone.Id}' names unknown rule '{rule}'");
            }

            if (milestone.RaisesTechLevel is { } level && !techLevels.Contains(level, StringComparer.Ordinal))
            {
                problems.Add($"'{milestone.Id}' names unknown tech level '{level}'");
            }
        }

        problems.AddRange(catalog.Milestones.GroupBy(m => m.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"'{g.Key}' twice"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Milestones: " + string.Join("; ", problems) + ".");
        }

        return catalog;
    }

    private static void CheckModel(InhabitantModel model, HashSet<string> needs, HashSet<string> moods, HashSet<string> traits)
    {
        var unknown = model.Needs.Select(n => n.Id).Where(id => !needs.Contains(id))
            .Concat(model.Moods.Select(m => m.Id).Where(id => !moods.Contains(id)))
            .Concat(model.MoodDrivers.SelectMany(d => new[] { d.Need, d.Mood }).Where(id => !needs.Contains(id) && !moods.Contains(id)))
            .Concat(model.Start.Traits.Select(r => r.Id).Where(id => !traits.Contains(id)))
            .Concat(model.Start.Needs.Select(r => r.Id).Where(id => !needs.Contains(id)))
            .Concat(model.Togetherness is { } together && !needs.Contains(together.Need) ? [together.Need] : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var badRanges = model.Start.Traits.Concat(model.Start.Needs).Where(r => r.Min > r.Max).Select(r => r.Id).ToList();
        if (unknown.Count > 0 || badRanges.Count > 0)
        {
            throw new InvalidDataException($"Inhabitant model: unknown ids [{string.Join(", ", unknown)}], min > max for [{string.Join(", ", badRanges)}].");
        }
    }

    /// <summary>The comfort zone names a known need and known actions (each once), and its zone is not upside down.</summary>
    private static void CheckComfortZone(ComfortZone? comfort, HashSet<string> needs, HashSet<string> actions)
    {
        if (comfort is null)
        {
            return;
        }

        var problems = new List<string>();
        if (!needs.Contains(comfort.Need))
        {
            problems.Add($"unknown need '{comfort.Need}'");
        }

        if (comfort.MinTenthC > comfort.MaxTenthC)
        {
            problems.Add($"min_tenth_c {comfort.MinTenthC} is above max_tenth_c {comfort.MaxTenthC}");
        }

        var factors = (comfort.ActionFactors ?? []).Select(f => f.Action).ToList();
        problems.AddRange(factors.Where(a => !actions.Contains(a)).Select(a => $"action_factors names unknown action '{a}'"));
        problems.AddRange(factors.GroupBy(a => a, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"action_factors names action '{g.Key}' twice"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Inhabitant model: comfort_zone: " + string.Join("; ", problems) + ".");
        }
    }

    private static IEnumerable<string> Files(string contentDirectory, string folder) =>
        Directory.GetFiles(Path.Combine(contentDirectory, folder), "*.json").Order(StringComparer.Ordinal);

    private static Vocabulary Required(Dictionary<VocabularyKind, Vocabulary> vocabularies, VocabularyKind kind) =>
        vocabularies.TryGetValue(kind, out var vocabulary)
            ? vocabulary
            : throw new InvalidDataException($"Vocabulary '{kind}' is missing.");
}
